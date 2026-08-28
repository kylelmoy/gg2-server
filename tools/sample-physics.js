#!/usr/bin/env node
//=============================================================================
// sample-physics.js - ask the running game where a character ends up, and write
// it down.
//
// The phase-4 counterpart to sample-collision.js. Phase 3 pinned place_free by
// sampling it; this pins the character update the same way, and for the same
// reason: a recorded trace only ever says where a character was to a fifth of a
// pixel, so it cannot tell a wrong sum from a differently-rounded anchor.
// Driving the engine's own Character events from a state we chose exactly takes
// the anchor out of the question altogether.
//
// One tick is driven as:
//
//     event_perform(ev_step, ev_step_begin);
//     event_perform(ev_step, ev_step_normal);
//     x += hspeed; y += vspeed;          // GM8's automatic movement
//     event_perform(ev_step, ev_step_end);
//
// The third line is not a fudge. GM8 adds hspeed/vspeed to the position between
// the Step and End Step events, which is exactly why Character's Step.xml ends
// with `x -= hspeed; y -= vspeed;` - those two lines exist only to cancel it.
// Driving the events by hand skips the engine's half of that pair, so it goes
// back in.
//
// The whole scenario runs inside ONE gg2_eval. The game is live and its own
// frames would otherwise step the character between calls, which reads as the
// physics being wrong when it is really the bot playing.
//
// Requires a running dedicated server with the agent bridge and at least one
// Character - see gg2-agent/CLAUDE.md, and CLAUDE.md here for the bots recipe.
//
//   node tools/sample-physics.js [--out <file>] [--maps ctf_truefort,...]
//=============================================================================

const fs = require('fs');
const path = require('path');

// The exact-double encoder lives in tools/exact-double.js, which is one
// copy of what used to be four - and three of those four were wrong. Run
// `node tools/exact-double.js` to self-test it; it needs no game.
const exact = require(path.join(__dirname, 'exact-double.js'));


const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));
const walkmask = require(path.join(AGENT, 'tools', 'walkmask.js'));

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const MAPS = arg('maps', 'ctf_truefort,koth_valley').split(',');
const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/physics-samples.json');

const CELL = 6;
const TICKS = 48;

//---------------------------------------------------------------------------
// Talking to the game
//---------------------------------------------------------------------------

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

const ev = (code) => mcp.callTool('gg2_eval', { code });

// Splits a double into sign, exponent and three exact mantissa chunks.
//
// string_format() is only good to about sixteen significant digits and the
// seventeenth is noise, which is useless here: the differences this tool exists
// to find are single ULPs. Every step below is a power-of-two scale or a floor,
// so nothing rounds - and every chunk stays under 2^19 because **GM8's floor()
// truncates through a 32-bit integer** and silently wraps above 2^31.
// ⚠️ The `if (v == 0)` below and the two `while` loops after it use GML's
// COMPARISON operators, which are fuzzy to 1e-13 (docs/SIMULATION.md). So this
// encoder reports a hard zero for any value under 1e-13 - cos(degtorad(90)),
// for one - and its normalisation loops can exit with the mantissa outside
// [1, 2) and lose the last bit. tools/sample-motion.js has the corrected
// version: it scales by 2^400 before testing the sign or the zero, and it has
// no normalisation loops at all.
//
// Every row in this tool's checked-in fixture was audited and none of them took
// the fuzzy branch, so the oracle is sound as it stands. Port the fixed encoder
// across before regenerating anything that can produce a value near zero.


//---------------------------------------------------------------------------
// The classes, and where to put them
//---------------------------------------------------------------------------

// Three different box widths and three different run powers, because a rule
// that happens to hold for one of each is not a rule. The bounds are the
// character's own left/right/top/bottom_bound_offset, which the movement solver
// reads directly rather than taking from the sprite.
const CLASSES = [
  // ⚠️ `left`/`right`/`top`/`bottom` here are NO LONGER the collision box - the
  // GML derives that from the sprite. `bottom` is still used, to place the probe
  // relative to a floor, and it is the one value that was right anyway.
  { name: 'Scout', sprite: 'ScoutBlueS', left: -6, right: 6, top: -6, bottom: 23, runPower: 1.4, doubleJump: 1 },
  { name: 'Heavy', sprite: 'HeavyBlueS', left: -9, right: 9, top: -6, bottom: 23, runPower: 0.8, doubleJump: 0 },
  { name: 'Pyro', sprite: 'PyroBlueS', left: -7, right: 7, top: -6, bottom: 23, runPower: 1.1, doubleJump: 0 },

  // ⚠️ Added 2026-08-28. The Soldier was absent for six phases, and its
  // runPower of 0.9 is a value no other sampled class has (1.4, 0.8, 1.1) - so
  // the friction equilibrium it settles at was never measured. It is also the
  // class of gg_debug's t523 residue, which is still open.
  { name: 'Soldier', sprite: 'SoldierBlueS', left: -6, right: 6, top: -8, bottom: 23, runPower: 0.9, doubleJump: 0 },
];

// Deterministic, so re-running the tool produces the same fixture and its diff
// is empty unless the game changed.
function rng(seed) {
  let s = seed >>> 0;
  return () => {
    s ^= s << 13; s >>>= 0;
    s ^= s >>> 17;
    s ^= s << 5; s >>>= 0;
    return s / 4294967296;
  };
}

// Somewhere with a floor and room to move: a scenario that spends every tick
// inside solid rock proves nothing, and neither does one in open sky.
function groundSites(mask) {
  const open = (cx, cy, w, h) => {
    for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) if (mask.solid(cx + i, cy + j)) return false;
    return true;
  };
  const floor = (cx, cy, w) => {
    for (let i = 0; i < w; i++) if (!mask.solid(cx + i, cy)) return false;
    return true;
  };

  const found = [];
  for (let cy = 2; cy < mask.height - 10; cy++)
    for (let cx = 2; cx < mask.width - 10; cx++)
      if (open(cx, cy, 8, 7) && floor(cx, cy + 7, 8)) { found.push([cx, cy]); cx += 7; }

  // Spread the picks over the whole map rather than taking the first few rows:
  // one ledge sampled sixteen times is one ledge.
  const picks = [];
  const step = Math.max(1, Math.floor(found.length / 8));
  for (let i = 0, n = 0; i < found.length && n < 8; i += step, n++) picks.push(found[i]);
  return picks;
}

// A key sequence with something in it: runs long enough to reach the speed cap,
// direction reversals, jumps, and the occasional both-directions-at-once, which
// the reference treats as not being in control at all.
function keySequence(random) {
  const LEFT = 0x40, RIGHT = 0x20, JUMP = 0x80, DOWN = 0x02;
  const keys = [];
  let held = random() < 0.5 ? LEFT : RIGHT;

  for (let t = 0; t < TICKS; t++) {
    const roll = random();
    if (roll < 0.08) held = held === LEFT ? RIGHT : LEFT;
    else if (roll < 0.12) held = LEFT | RIGHT;
    else if (roll < 0.16) held = 0;
    else if (roll < 0.20) held = random() < 0.5 ? LEFT : RIGHT;

    let k = held;
    if (random() < 0.12) k |= JUMP;
    if (random() < 0.06) k |= DOWN;
    keys.push(k);
  }
  return keys;
}

//---------------------------------------------------------------------------
// Running one scenario
//---------------------------------------------------------------------------

async function runScenario(scenario) {
  const { cls, start, keys, aims, team } = scenario;

  await ev(keys.map((k, i) => `global.k[${i}]=${k};global.a[${i}]=${aims[i]};`).join('\n'));

  // Note what is NOT written as a decimal literal here. GM8 parses some of them
  // one ULP off the IEEE-nearest double (docs/SIMULATION.md), so a start state
  // sent as decimal text would not be the state we think we asked for.
  // Everything below is an integer or an exact ratio of integers.
  await ev(`
global.probe = instance_find(Character, 0);
with (global.probe) {
    sprite_index = ${cls.sprite}; mask_index = -1;

    // ⚠️ DERIVED FROM THE SPRITE, exactly as Character.events/Create.xml:12-15
    // does - NOT from a table here. This used to hardcode CLASSES' own numbers
    // and two of them were wrong: it forced top_bound_offset = -6 for a Scout
    // and a Heavy where the engine derives -10 and -12
    // (tools/probes/probe-character-boxes.js, 2026-08-28).
    //
    // That made the oracle a measurement of a character the port does not
    // simulate, and it went unnoticed for six phases because PhysicsSampleTests
    // is green: no scenario in 48 x 48 ticks ever had an outcome that turned on
    // the top edge. Luck, not coverage.
    left_bound_offset = sprite_get_bbox_left(sprite_index) - sprite_get_xoffset(sprite_index);
    right_bound_offset = sprite_get_bbox_right(sprite_index) - sprite_get_xoffset(sprite_index);
    top_bound_offset = sprite_get_bbox_top(sprite_index) - sprite_get_yoffset(sprite_index);
    bottom_bound_offset = sprite_get_bbox_bottom(sprite_index) - sprite_get_yoffset(sprite_index);
    runPower = ${cls.runPower}; basemaxspeed = abs(${cls.runPower}*baseControl/(baseFriction-1));
    canDoublejump = ${cls.doubleJump};
    hp = 1000; maxHp = 1000;
    team = ${team}; intel = 0; player.humiliated = false;
    taunting = false; omnomnomnom = false; cloak = false;

    x = ${start.xNum}/${start.den}; y = ${start.yNum}/${start.den};
    hspeed = ${start.hNum}/${start.den}; vspeed = ${start.vNum}/${start.den};
    applied_gravity = 0; moveStatus = 0;
    onground = false; obstacleBelow = false; doublejumpUsed = 0; spinjumping = false;
    keyState = global.k[0]; lastKeyState = global.k[0]; pressedKeys = 0; releasedKeys = 0;
    aimDirection = global.a[0] * 360 / 65536;
    if(((aimDirection+270) mod 360)>180) image_xscale = 1; else image_xscale = -1;
    _last_xscale = image_xscale;

    for (qt = 0; qt < ${TICKS}; qt += 1) {
        keyState = global.k[qt];
        aimDirection = global.a[qt] * 360 / 65536;
        event_user(1);
        event_perform(ev_step, ev_step_begin);
        event_perform(ev_step, ev_step_normal);
        x += hspeed; y += vspeed;
        event_perform(ev_step, ev_step_end);
        global.rx[qt] = x; global.ry[qt] = y;
        global.rh[qt] = hspeed; global.rv[qt] = vspeed;
    }
}`);

  // Read back in chunks: one reply cannot carry a whole scenario.
  const fields = ['rx', 'ry', 'rh', 'rv'];
  const ticks = [];
  const CHUNK = 12;

  for (let from = 0; from < TICKS; from += CHUNK) {
    const to = Math.min(TICKS, from + CHUNK);
    const body = fields
      .map((f) => exact.encodeInto('global.piece', `global.${f}[qt]`) + '\nglobal.out += global.piece + "|";')
      .join('\n');

    await ev(`global.out = "";\nfor (qt = ${from}; qt < ${to}; qt += 1) {\n${body}\n}`);
    const parts = (await evalx('global.out')).split('|').filter((p) => p.length);
    if (parts.length !== (to - from) * fields.length)
      throw new Error(`scenario readback: ${parts.length} values, wanted ${(to - from) * fields.length}`);

    for (let q = 0; q < to - from; q++) {
      const [x, y, h, v] = parts.slice(q * fields.length, (q + 1) * fields.length).map(exact.decode);
      ticks.push({ x, y, hspeed: h, vspeed: v });
    }
  }

  return ticks;
}

//---------------------------------------------------------------------------

async function main() {
  const out = { generated: new Date().toISOString().slice(0, 10), ticksPerScenario: TICKS, scenarios: [] };
  const random = rng(0x9e3779b9);

  for (const map of MAPS) {
    process.stdout.write(`${map}: `);

    await ev(`serverGotoMap("${map}");`);
    await mcp.callTool('gg2_wait', { expr: `global.currentMap == "${map}"`, frames: 300 });
    await mcp.callTool('gg2_wait', { expr: 'instance_number(Character) > 0', frames: 900 });

    const mask = walkmask.decode(map);

    for (const [cx, cy] of groundSites(mask)) {
      for (const cls of CLASSES) {
        const floorY = (cy + 7) * CELL;

        // A denominator of 40 lands the start on eighths and fifths of a pixel:
        // the fifth is the bin the wire quantises to, and the eighth is the step
        // the landing crawl backs off by. Both are where a rounding rule shows.
        const den = 40;
        const start = {
          den,
          xNum: Math.round((cx + 4) * CELL * den) + Math.floor(random() * den),
          yNum: Math.round((floorY - cls.bottom - 1 - Math.floor(random() * 12)) * den) + Math.floor(random() * den),
          hNum: Math.round((random() * 12 - 6) * den),
          vNum: Math.round((random() * 10 - 3) * den),
        };

        const keys = keySequence(random);
        const aims = keys.map(() => Math.floor(random() * 65536));

        const scenario = { map, cls, team: 1, start, keys, aims };
        const ticks = await runScenario(scenario);

        out.scenarios.push({
          map,
          class: cls.name,
          team: scenario.team,
          start: {
            x: start.xNum / den,
            y: start.yNum / den,
            hspeed: start.hNum / den,
            vspeed: start.vNum / den,
          },
          keys,
          aims,
          ticks,
        });
        process.stdout.write('.');
      }
    }
    process.stdout.write('\n');
  }

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 1) + '\n');
  console.log(`wrote ${OUT}: ${out.scenarios.length} scenarios x ${TICKS} ticks`);
}

main().catch((e) => {
  console.error('FAIL', e.message);
  process.exit(1);
});
