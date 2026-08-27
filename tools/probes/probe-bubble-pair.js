#!/usr/bin/env node
//=============================================================================
// probe-bubble-pair.js - does GM8 run BOTH sides of a projectile collision?
//
// This one decides a whole weapon. Quote's bubble screen works only if both
// instances' collision events fire:
//
//   Bubble.events/Collision with Shot   if(team != other.team or other.perseverant) instance_destroy();
//   Shot.events/Collision with Bubble   if(team != other.team and !perseverant)     instance_destroy();
//
// If only one side ran, the outcome would depend on which instance the engine
// reached first, and for the ordinary case - a screen older than the pellet
// flying into it - every pellet would survive and `perseverant` would be dead
// code. The port (Projectiles.ResolveBubblePair) applies both sides from
// whichever end it reaches the pair. Until 2026-08-27 that was inferred from
// the reference's design and never measured.
//
// The experiment: put a real Bubble and a real Shot on the same point in open
// space and read back which of them still exists one frame later. Four cells:
//
//   teams   perseverant   both events run   bubble only   pellet only
//   enemy   0             both die          pellet lives  bubble lives
//   enemy   1             bubble dies       bubble dies   both live
//   same    1             bubble dies       bubble dies   both live
//   same    0             both live         both live     both live
//
// The enemy/ordinary row is the decisive one: no single event can destroy both
// instances, and each single-sided hypothesis is refuted by a different row.
//
// Measured 2026-08-27: both die, bubble dies, bubble dies, both live. The port
// is right and GM8 runs both sides - so an instance that destroyed itself in
// its own collision event is still there for the other one's.
//
// ⚠️ THREE traps, each of which cost this measurement an attempt:
//
//  1. A bare Bubble dereferences `owner` in its Begin Step, Step and Destroy,
//     and `gun.bubbleCount` in its Destroy. An AgentSpare0 carrying
//     sprite_index/aimDirection/bubbleCount/hspeed/vspeed satisfies all of it.
//  2. ⚠️ A Bubble's Step pushes it away from a STATIONARY owner at about
//     3.3 px a tick, and its Begin Step destroys it past
//     `distance_to_object(owner) > 160`. Read the outcome within a few frames
//     or the "both survive" row reports the bubble dead for a reason that has
//     nothing to do with the pellet. That is why every scenario below runs
//     inside gg2_wait's `setup` with a three-frame budget rather than as an
//     eval followed by a read seconds later.
//  3. `place_free` and `place_meeting` at the top level of an eval run in the
//     BRIDGE's scope - wrap them in with(). The overlap is checked rather than
//     assumed, or the "both survive" row would pass for the wrong reason.
//
// The pair is placed far outside the map's terrain, so no wall, gate or bullet
// wall can destroy the Shot for an unrelated reason.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const call = (tool, args) => mcp.callTool(tool, args);

const X = 14000;
const Y = 10000;

function setup(shotTeam, perseverant) {
  return `
with(global.pdummy) instance_destroy();
global.pdummy = instance_create(${X}, ${Y}, AgentSpare0);
global.pdummy.sprite_index = BubbleS;
global.pdummy.aimDirection = 0;
global.pdummy.bubbleCount = 5;
global.pdummy.hspeed = 0;
global.pdummy.vspeed = 0;
global.pb = instance_create(${X}, ${Y}, Bubble);
global.pb.owner = global.pdummy;
global.pb.gun = global.pdummy;
global.pb.ownerPlayer = noone;
global.pb.team = TEAM_RED;
global.pb.speed = 0;
global.pb.alarm[0] = 3000;
global.ps = instance_create(${X}, ${Y}, Shot);
global.ps.ownerPlayer = noone;
global.ps.team = ${shotTeam};
global.ps.perseverant = ${perseverant};
global.ps.direction = 0;
global.ps.speed = 0;
global.ps.image_angle = 0;
global.ps.alarm[0] = 3000;
global.probeOverlap = -1;
with(global.pb) global.probeOverlap = place_meeting(x, y, Shot);
`;
}

// Each scenario names the outcome it expects as the expression to wait for, so
// a PASS is the engine agreeing rather than this script interpreting anything.
const SCENARIOS = [
  {
    name: 'enemy pellet, ordinary   ',
    shotTeam: 'TEAM_BLUE',
    perseverant: 0,
    expect: 'both die',
    expr: '!instance_exists(global.pb) and !instance_exists(global.ps)',
  },
  {
    name: 'enemy pellet, perseverant',
    shotTeam: 'TEAM_BLUE',
    perseverant: 1,
    expect: 'bubble dies, pellet lives',
    expr: '!instance_exists(global.pb) and instance_exists(global.ps)',
  },
  {
    name: 'team pellet, perseverant ',
    shotTeam: 'TEAM_RED',
    perseverant: 1,
    expect: 'bubble dies, pellet lives',
    expr: '!instance_exists(global.pb) and instance_exists(global.ps)',
  },
  {
    // The only row whose expectation is "nothing happens", so it is written as
    // a wait that is expected to TIME OUT. A timeout here is the pass.
    name: 'team pellet, ordinary    ',
    shotTeam: 'TEAM_RED',
    perseverant: 0,
    expect: 'both live (wait must time out)',
    expr: '!instance_exists(global.pb) or !instance_exists(global.ps)',
    timeoutIsPass: true,
  },
];

async function run(scenario) {
  let result;
  let timedOut = false;

  try {
    result = await call('gg2_wait', {
      setup: setup(scenario.shotTeam, scenario.perseverant),
      expr: scenario.expr,
      frames: 3,
    });
  } catch (e) {
    timedOut = true;
    result = e && e.message ? e.message : String(e);
  }

  const overlapped = await call('gg2_evalx', { expr: 'global.probeOverlap' });
  const pass = scenario.timeoutIsPass ? timedOut : !timedOut;

  console.log(
    `${pass ? 'PASS' : 'FAIL'}  ${scenario.name}  overlap=${overlapped}  `
      + `expected ${scenario.expect}  -> ${String(result).trim()}`,
  );

  return pass && String(overlapped).trim() === '1';
}

async function main() {
  await call('gg2_eval', { code: 'global.pdummy = 0; global.pb = 0; global.ps = 0;' });

  let ok = true;
  for (const scenario of SCENARIOS)
    ok = (await run(scenario)) && ok;

  await call('gg2_eval', {
    code: 'with(global.pdummy) instance_destroy();\nglobal.pdummy = 0;\nglobal.pb = 0;\nglobal.ps = 0;',
  });

  console.log('');
  console.log(
    ok
      ? 'All four cells agree with Projectiles.ResolveBubblePair: GM8 runs both sides.'
      : 'A cell disagreed - see above. Check the overlap column first.',
  );

  process.exitCode = ok ? 0 : 1;
}

main().then(
  () => process.exit(process.exitCode || 0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
