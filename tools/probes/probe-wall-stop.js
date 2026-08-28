#!/usr/bin/env node
//=============================================================================
// probe-wall-stop.js - where EXACTLY does a character come to rest against a
// wall? The one measurement gg_debug's t523 residue is waiting on.
//
// THE FINDING THIS SERVES. reference-gg_debug-closedtdm.trace replays exactly
// for 142 ticks and then, at t523, slot 4's x and hspeed are each one wire unit
// out. Three sessions narrowed it and none could measure it, for a reason that
// is structural: a trace carries x only to a fifth of a pixel, so the
// reference's exact position is unrecoverable from any recording.
//
// This probe recovers it - not from the recording, but by making the engine
// redo the one thing the recording's slot 4 did.
//
// THE SCENARIO, reduced to nothing. A Soldier stands at (588, 168) on gg_debug
// and holds RIGHT. That is what slot 4 does from t383. It accelerates for
// thirteen ticks, hits a wall, and then CREEPS into it for four more ticks with
// hspeed already zero - good_move_contact_solid's back-off ladder converging on
// the last free position. Then it stops, and everything downstream inherits
// wherever it stopped.
//
// THE PORT'S ANSWER, for comparison (Gg2.Bots.ScenarioWorld, gg_debug, area 1):
//
//     t12  x = 626.5277729831873   hspeed = 4.3220528294914455
//     t13  x = 629.2933979831873   hspeed = 0            <- wall
//     t14  x = 629.4650989026032
//     t15  x = 629.4758302100668
//     t16  x = 629.4865615175303
//     t17  x = 629.4972928249938   <- rest, and it never moves again
//
// The interesting number is the last one. The mask's free/blocked boundary at
// that spot is exactly x = 629.5 (half-to-even sends 629.5 to 630, which is
// solid), so the character rests 0.0027 px short of a rounding TIE. Both
// simulations report round(x*5) = 3147 whatever they do inside that window,
// which is precisely why no fixture can see the difference.
//
// WHAT EACH OUTCOME MEANS:
//
//   engine == 629.4972928249938   the walk and the wall stop are exact, and
//                                 t523's residue is born LATER - after t418,
//                                 where slot 4 starts rocket-jumping. The whole
//                                 "born during the walk" reading is then wrong
//                                 and the search moves to the explosion path.
//
//   engine != that                the residue is measured for the first time.
//                                 Its size names the mechanism: a whole ladder
//                                 rung (~0.0107) is a place_free disagreement
//                                 near the tie; something near 1e-13 is the
//                                 fuzzy-compare epsilon; anything between is
//                                 the creep loop running a different number of
//                                 times.
//
// Do NOT send the start state as decimals with a fraction, and do not
// "simplify" the readback to string_format - GM8 parses some decimals one ULP
// off, and string_format is good to ~16 digits with noise in the last. Both
// traps are in CLAUDE.md; tools/exact-double.js is the answer to the second.
//
// Requires a running dedicated server on gg_debug with the agent bridge and at
// least one Character to borrow (CLAUDE.md's bots recipe). The bridge serves
// ONE client, so no gg2_* MCP call may be in flight while this runs.
//
//   node tools/probes/probe-wall-stop.js [--map gg_debug] [--ticks 30]
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));
const exact = require(path.join(__dirname, '..', 'exact-double.js'));

const arg = (name, fallback) => {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
};

const MAP = arg('map', 'gg_debug');
const TICKS = Number(arg('ticks', '30'));

// Slot 4's start, straight off the recording's t383 update: raw x 2940, raw
// y 840, so x = 588 and y = 168 exactly. Integers, deliberately.
const START_X = 588;
const START_Y = 168;
const RIGHT = 32;

// The port's resting x, to sixteen significant digits. A run that reproduces
// this exactly is a null result and a real one.
const PORT_REST = 629.4972928249938;

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
}

async function main() {
  await ev(`serverGotoMap("${MAP}");`);
  await mcp.callTool('gg2_wait', { expr: `global.currentMap == "${MAP}"`, frames: 300 });
  await mcp.callTool('gg2_wait', { expr: 'instance_number(Character) > 0', frames: 900 });

  // The whole scenario in ONE eval: the game keeps stepping between calls, and
  // a scenario split across two of them measures the bot rather than the input.
  //
  // The bounds come off the sprite exactly as Character.events/Create.xml does
  // them - hardcoding a table here is what made the physics oracle measure a
  // character the port does not simulate, for six phases.
  await ev(`
global.probe = instance_find(Character, 0);
with (global.probe) {
    sprite_index = SoldierBlueS; mask_index = -1;
    left_bound_offset = sprite_get_bbox_left(sprite_index) - sprite_get_xoffset(sprite_index);
    right_bound_offset = sprite_get_bbox_right(sprite_index) - sprite_get_xoffset(sprite_index);
    top_bound_offset = sprite_get_bbox_top(sprite_index) - sprite_get_yoffset(sprite_index);
    bottom_bound_offset = sprite_get_bbox_bottom(sprite_index) - sprite_get_yoffset(sprite_index);
    runPower = 9/10; basemaxspeed = abs(runPower*baseControl/(baseFriction-1));
    canDoublejump = 0;
    hp = 1000; maxHp = 1000;
    intel = 0; player.humiliated = false;
    taunting = false; omnomnomnom = false; cloak = false;

    x = ${START_X}; y = ${START_Y};
    hspeed = 0; vspeed = 0;
    applied_gravity = 0; moveStatus = 0;
    onground = false; obstacleBelow = false; doublejumpUsed = 0; spinjumping = false;
    keyState = ${RIGHT}; lastKeyState = ${RIGHT}; pressedKeys = 0; releasedKeys = 0;
    aimDirection = 0;
    image_xscale = 1; _last_xscale = 1;

    for (qt = 0; qt < ${TICKS}; qt += 1) {
        keyState = ${RIGHT};
        event_user(1);
        event_perform(ev_step, ev_step_begin);
        event_perform(ev_step, ev_step_normal);
        x += hspeed; y += vspeed;
        event_perform(ev_step, ev_step_end);
        global.rx[qt] = x; global.rh[qt] = hspeed;
    }
}`);

  const rows = [];
  const CHUNK = 12;

  for (let from = 0; from < TICKS; from += CHUNK) {
    const to = Math.min(TICKS, from + CHUNK);
    const body = ['rx', 'rh']
      .map((f) => exact.encodeInto('global.piece', `global.${f}[qt]`) + '\nglobal.out += global.piece + "|";')
      .join('\n');

    await ev(`global.out = "";\nfor (qt = ${from}; qt < ${to}; qt += 1) {\n${body}\n}`);
    const parts = (await evalx('global.out')).split('|').filter((p) => p.length);
    if (parts.length !== (to - from) * 2)
      throw new Error(`readback: ${parts.length} values, wanted ${(to - from) * 2}`);

    for (let q = 0; q < to - from; q++)
      rows.push({ x: exact.decode(parts[q * 2]), hspeed: exact.decode(parts[q * 2 + 1]) });
  }

  rows.forEach((r, t) => console.log(`t${String(t).padStart(2)}  x = ${r.x}   hspeed = ${r.hspeed}`));

  const rest = rows[rows.length - 1].x;
  const delta = rest - PORT_REST;

  console.log('');
  console.log(`engine rest  ${rest}`);
  console.log(`port rest    ${PORT_REST}`);
  console.log(`difference   ${delta}   (${delta * 5} wire units of x)`);
  console.log(delta === 0
    ? 'NULL RESULT: the walk and the wall stop agree exactly. The residue is born after t418.'
    : 'MEASURED: this is the t523 residue, seen directly for the first time.');

  if (mcp.close) await mcp.close();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
