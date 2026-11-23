#!/usr/bin/env node
//
// Drives a running gg2-server through map, gamemode and AREA transitions on its
// control port, so a stock GM8 client attached to the same server is exercised
// across the boundaries that no fixture in the corpus reaches.
//
// WHY THIS EXISTS, AND WHY IT DRIVES ONLY THE CONTROL PORT
//
// docs/PARITY.md, "The gap every automated gate has": every automated gate in
// this project reads the SERVER. Both client fatals found so far (the stale
// MatchState HUD block, ServerStream's skipped MAP_END area byte) lived in the
// gap between what the port believes and what a stock client derives, and both
// were TRANSITION bugs — state carried across a boundary. This tool makes those
// boundaries cheap to hit thousands of times instead of a handful.
//
// ⚠️ It deliberately does NOT touch the agent bridge. The bridge serves ONE TCP
// client and the MCP server process holds it (docs/TRAPS-LIVEGAME.md), so a
// script that grabbed it would take the client away from the session watching
// it. The client-side watchdog latch is installed over the bridge instead, and
// its per-frame trace lands in the launcher's own
// `agent_bridge_<port>.log` on disk — which is readable by anyone, without the
// socket. So: this drives, the latch observes, and the two never contend.
//
// Usage:
//   node tools/soak-transitions.js --control 8192 [--plan areas|maps|mixed|churn|all]
//                                  [--dwell 12] [--cycles 0] [--log <path>]
//                                  [--keep-teamed <roster name>]
//
//   --plan areas   cp_dirtbowl 1->2->3 only. The open intermittent client fatal
//                  in docs/PARITY.md is an area-advance bug; this is its repro.
//   --plan maps    rotate one map of every gamemode, which is the pairing
//                  GamemodeRotationTests covers in-process and nothing covers
//                  against a real client.
//   --plan mixed   both, interleaved (default) — a map change landing on a
//                  multi-area map is the combination neither plan reaches alone.
//   --plan churn   roster churn: bot fill up and down, which adds and removes
//                  players from the MIDDLE of the roster. That is the
//                  compaction path — CONTRIBUTING.md's "roster indices are positional
//                  and shift on PLAYER_LEAVE ... intermittently" — and it is
//                  only observable to a client watching some OTHER slot move,
//                  so it wants two real clients attached, not one.
//   --plan all     transitions and churn interleaved. A map change with a
//                  compaction in flight is the pairing nothing else reaches.
//
//   --cycles 0 runs until killed.

const net = require('net');

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i === -1 ? fallback : process.argv[i + 1];
}

const CONTROL = Number(arg('control', 8192));
const PLAN = arg('plan', 'mixed');
const DWELL = Number(arg('dwell', 12)) * 1000;
const CYCLES = Number(arg('cycles', 0));
const LOGPATH = arg('log', null);

// The roster name of a human client to keep on a team for the whole run.
//
// ⚠️ Without this an unattended soak measures a SPECTATOR after its first
// transition. A map change puts every roster entry back to Team.Spectator — the
// port doing what the reference's `with(Player) ... team = TEAM_SPECTATOR` does —
// and a client that picked its team through the UI does not re-pick itself. So
// the client keeps its connection, keeps deserialising, and has no character
// from the first map change onward: every later measurement is of a client with
// nothing to predict, which reads as "clean" for the wrong reason.
const KEEP_TEAMED = arg('keep-teamed', null);

const fs = require('fs');
function log(...parts) {
  const line = `${new Date().toISOString()} ${parts.join(' ')}`;
  console.log(line);
  if (LOGPATH) { try { fs.appendFileSync(LOGPATH, line + '\n'); } catch (e) { /* best effort */ } }
}

function send(obj) {
  return new Promise((resolve, reject) => {
    const s = net.connect(CONTROL, '127.0.0.1', () => s.write(JSON.stringify(obj) + '\n'));
    let buf = '';
    s.setEncoding('utf8');
    s.on('data', d => {
      buf += d;
      if (buf.includes('\n')) { s.destroy(); try { resolve(JSON.parse(buf.trim())); } catch (e) { reject(e); } }
    });
    s.on('error', reject);
    setTimeout(() => { s.destroy(); reject(new Error('control port timeout')); }, 8000);
  });
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

// One shipped map per gamemode. gg_debug/gg_debug2 are omitted: they are debug
// geometry, not rounds, and gg_debug2 is port-authored (docs/DEBUG-MAPS.md).
const BY_MODE = [
  'ctf_truefort',      // CaptureTheFlag
  'koth_valley',       // KingOfTheHill
  'dkoth_atalia',      // DualKingOfTheHill
  'cp_egypt',          // ControlPoints
  'tdm_mantic',        // TeamDeathmatch
  'arena_lumberyard',  // Arena
  'gen_destroy',       // GeneratorDestroy
  'ctf_avanti',        // Invasion (setup gate on a CTF map)
];

// ⚠️ cp_dirtbowl is the ONLY shipped multi-area map (three areas). An area
// advance is not a rotation: teams and the scoreboard survive it, and that
// difference is exactly what the open fatal sits on.
const AREA_MAP = 'cp_dirtbowl';
const AREAS = [1, 2, 3];

async function settle(wantMap, wantArea) {
  // The control port queues a map change onto the tick thread and returns
  // immediately, so reading state straight back races it — an early version of
  // this tool misread cp_dirtbowl's area count that way.
  for (let i = 0; i < 60; i++) {
    await sleep(500);
    const st = await send({ cmd: 'state' });
    if (st.map === wantMap && (wantArea === undefined || st.match.area === wantArea)) return st;
  }
  return null;
}

// ⚠️ A map change takes TEN SECONDS (Rounds.MapChangeDelay) and
// GameServer.RequestMapChange refuses outright while `_world.MapChanging` is
// set. Without this wait the driver fires the whole map list into a single
// in-flight change, gets every one refused, and races on — measured
// 2026-09-05: 718 of 859 mode changes refused, so the driver reported
// "cycle 88" while having actually rotated 141 times. A cycle counter is not
// a transition counter.
async function awaitIdle() {
  for (let i = 0; i < 80; i++) {
    const st = await send({ cmd: 'state' });
    if (st.match.changeIn < 0) return true;
    await sleep(500);
  }
  return false;
}

// What ACTUALLY happened, as opposed to how many times round the loop we went.
const tally = { area: 0, mode: 0, leave: 0, churn: 0, refused: 0, stuck: 0, unsettled: 0 };

async function go(map, area, tag) {
  const kind = tag.includes('/area') ? 'area' : tag.includes('/leave') ? 'leave' : 'mode';

  if (!await awaitIdle()) { tally.stuck++; log(`STUCK-CHANGING ${tag} ${map} area ${area}`); return null; }

  const r = await send({ cmd: 'map', name: map, area: area });
  if (!r.ok) { tally.refused++; log(`REFUSED ${tag} ${map} area ${area}: ${JSON.stringify(r)}`); return null; }
  const st = await settle(map, area);
  if (!st) { tally.unsettled++; log(`NOT-SETTLED ${tag} ${map} area ${area}`); return null; }
  tally[kind]++;
  log(`ok ${tag} ${map} area=${st.match.area}/${st.match.areas} mode=${st.match.mode} ` +
      `players=${st.players.length} lateTicks=${st.server.lateTicks} tickMs=${st.server.tickMs}`);
  await sleep(DWELL);
  return st;
}

// Bot fills that add and remove several slots at a time. The humans keep their
// seats, so every one of these moves some OTHER slot under them.
const FILLS = [8, 2, 7, 3, 10, 4, 6, 2];

async function churn(tag) {
  for (const fill of FILLS) {
    const r = await send({ cmd: 'bots', fill });
    if (!r.ok) { tally.refused++; log(`REFUSED ${tag} bots fill=${fill}`); continue; }
    await sleep(1500);
    const st = await send({ cmd: 'state' });
    tally.churn++;
    const humans = st.players.filter(p => p.connected).length;
    log(`ok ${tag} fill=${fill} roster=${st.players.length} humans=${humans} ` +
        `map=${st.map} area=${st.match.area}`);
  }
}

// Puts KEEP_TEAMED back on a team whenever it is sitting in Spectator. Runs as
// its own loop rather than inside the transition cycle, because the window that
// matters opens at a map change and the driver is asleep in `dwell` for most of
// one.
//
// ⚠️ It asks for the SMALLER team, never a named one. `ResolveRequestedTeam`
// refuses a request for the bigger team in silence (the reference's balance
// block), so a fixed choice works until the bots happen to fill that side and
// then fails without saying anything.
async function keepTeamed() {
  let placed = 0;

  while (true) {
    await sleep(2000);

    try {
      const st = await send({ cmd: 'state' });
      const me = (st.players || []).find(p => p.name === KEEP_TEAMED);
      if (!me || me.team !== 'Spectator') continue;

      const red = st.players.filter(p => p.team === 'Red').length;
      const blue = st.players.filter(p => p.team === 'Blue').length;

      await send({ cmd: 'command', slot: me.slot, kind: 'ChangeTeam', argument: red <= blue ? 0 : 1 });
      placed++;
      log(`keep-teamed ${KEEP_TEAMED} slot=${me.slot} -> ${red <= blue ? 'Red' : 'Blue'} (#${placed})`);
    } catch (e) {
      log(`keep-teamed ERROR: ${e.message}`);
    }
  }
}

(async () => {
  log(`soak-transitions start plan=${PLAN} dwell=${DWELL}ms cycles=${CYCLES || 'unbounded'}`);

  if (KEEP_TEAMED) {
    log(`keep-teamed armed for "${KEEP_TEAMED}"`);
    keepTeamed();
  }
  let n = 0;
  while (CYCLES === 0 || n < CYCLES) {
    n++;
    try {
      if (PLAN === 'churn' || PLAN === 'all') await churn(`cycle${n}/churn`);
      if (PLAN === 'areas' || PLAN === 'mixed' || PLAN === 'all') {
        for (const a of AREAS) await go(AREA_MAP, a, `cycle${n}/area`);
        // Leaving a multi-area map from area 3 is a rotation, not an advance.
        if (PLAN === 'mixed' || PLAN === 'all') await go(BY_MODE[n % BY_MODE.length], 1, `cycle${n}/leave-area`);
      }
      if (PLAN === 'maps' || PLAN === 'mixed' || PLAN === 'all') {
        for (const m of BY_MODE) await go(m, 1, `cycle${n}/mode`);
      }
    } catch (e) {
      log(`ERROR cycle${n}: ${e.message}`);
      await sleep(3000);
    }

    // ⚠️ Printed every cycle so nobody reads the cycle number as a transition
    // count again. `refused` should stay near zero; if it climbs, awaitIdle is
    // not holding and the driver is spinning rather than transitioning.
    log(`TALLY after cycle${n}: ${JSON.stringify(tally)}`);
  }
  log('soak-transitions done');
})();
