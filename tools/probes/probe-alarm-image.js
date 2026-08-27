#!/usr/bin/env node
//=============================================================================
// probe-alarm-image.js - two GM8 frame-order facts, from one instance's life.
//
// Both were inferences the port could not settle by reading, and both are
// worth exactly one tick of somebody's timer.
//
//  1. Does GM8 advance image_index BEFORE or AFTER the Step event?
//     It decides Weapons.StabFreezeTicks - 52 or 51 - because
//     StabAnim.events/Step.xml latches `done` on `image_index >= 32` and the
//     whole spy freeze is that instance's life.
//
//  2. What does a fired alarm read for the rest of the frame it fired on?
//     Timers.Advance writes -1 at the moment of firing. It matters only in one
//     place, and that place is real: Character.events/Begin Step.xml:67 gates
//     the afterburn intensity fade on `alarm[0] == -1` EXACTLY.
//
// The witness is a StabAnim, because the game already contains the experiment:
// its Create sets image_speed = 0 and alarm[0] = 10, its Alarm 0 sets
// image_speed = 1, and its Step reads image_index. Reading image_index on the
// frame the alarm fires answers (1); reading alarm[0] on that same frame
// answers (2).
//
// ⚠️ The read point is what makes this work. gg2_eval and gg2_watch are both
// serviced from AgentBridge's STEP event (payload/Scripts/AgentBridge/
// agentBridgeStep.gml, which calls agentBridgeWatchTick at the top), so every
// sample below is taken during the Step stage - after that frame's alarm stage
// and before whatever the engine does at the end of the frame.
//
// ⚠️ A bare StabAnim needs an owner: its End Step, Draw and Destroy all
// dereference one. An AgentSpare0 with `invisible = true` satisfies all three -
// Draw exits on the first line, End Step reads nothing else, and Destroy writes
// runPower/jumpStrength/stabbing back onto it. Watching `stabbing` return to 0
// is also the end-to-end confirmation that the freeze really is that long.
//
// ⚠️ Do NOT reach for an alarm index whose object has no event for it. GM8 does
// not count those down at all: an AgentSpare0 given alarm[1] = 4 still read 4
// sixty frames later. Measured here on 2026-08-27, and it is why this probe
// needs a real object with a real Alarm event rather than a bare instance.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const ev = (code) => mcp.callTool('gg2_eval', { code });
const call = (tool, args) => mcp.callTool(tool, args);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// The watch list, in the order the bridge samples it. That order is what
// disambiguates two changes logged a clock tick apart: within one sample the
// entries appear in this order, so a later-listed name appearing BEFORE an
// earlier-listed one is the next frame.
const WATCHES = [
  ['idx', 'global.pa.image_index'],
  ['spd', 'global.pa.image_speed'],
  ['ex', 'instance_exists(global.pa)'],
  ['al', 'global.pa.alarm[0]'],
];

const SETUP = `
global.pdummy = instance_create(-2000, -2000, AgentSpare0);
global.pdummy.invisible = true;
global.pdummy.runPower = 1;
global.pdummy.baseRunPower = 1;
global.pdummy.jumpStrength = 1;
global.pdummy.baseJumpStrength = 1;
global.pdummy.stabbing = 1;
global.pdummy.image_speed = 0;
global.pdummy.image_index = 99;
global.pa = global.pdummy;
`;

// One eval, after the watches are armed: the StabAnim exists from here on and
// image_index dropping 99 -> 0 is the frame stamp for its creation.
const CREATE = `
var a;
a = instance_create(-2000, -2000, StabAnim);
a.owner = global.pdummy;
a.golden = 0;
a.team = 0;
a.direction = 0;
global.pdummy.stabbing = 1;
global.pa = a;
`;

const CLEANUP = `
with(global.pdummy) instance_destroy();
global.pa = 0;
global.pdummy = 0;
`;

async function main() {
  await ev(SETUP);

  await call('gg2_watch', { action: 'clear' });
  for (const [label, expr] of WATCHES)
    await call('gg2_watch', { action: 'add', label, expr });

  await ev(CREATE);

  // The freeze is 52 ticks and the trace runs a few frames past its end, where
  // the watches start erroring on a destroyed instance. Three seconds covers it.
  await sleep(3000);

  await call('gg2_watch', { action: 'clear' });

  const log = await call('gg2_log', { source: 'bridge', lines: 120 });
  await ev(CLEANUP);

  console.log(typeof log === 'string' ? log : JSON.stringify(log));
  console.log('');
  console.log('Read the trace as: idx 99 -> 0 is creation (call it F0+1, the');
  console.log('first frame the StabAnim is sampled). al counts 9..0 one a');
  console.log('frame. The decisive sample is the one holding BOTH spd = 1 and');
  console.log('al = 0:');
  console.log('  idx unchanged at 0 there  -> the advance is after the Step');
  console.log('     stage, done latches on tick 42, StabFreezeTicks = 52.');
  console.log('  idx = 1 there             -> the advance is before it, 41/51.');
  console.log('  al = 0 there and -1 next  -> a fired alarm rests at 0 for the');
  console.log('     rest of its own frame, NOT -1.');
  console.log('Measured 2026-08-27: idx stayed 0, al read 0 then -1.');
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
