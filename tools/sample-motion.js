#!/usr/bin/env node
//=============================================================================
// sample-motion.js - GM8's four motion variables, measured against the running
// game.
//
// `Gg2.Simulation.Motion` was transcribed from OpenGMK rather than measured,
// because nothing in phase 4 can reach it: a character writes only hspeed and
// vspeed, so no existing fixture ever sees `direction` or `speed` written. A
// projectile writes them constantly - createShot.gml sets direction and speed,
// then Scattergun.events/User Event 3.xml adds to hspeed, then to speed, then
// to direction, so a single pellet round-trips between the polar and the
// component representation three times before it has moved a pixel. Every one
// of those conversions can lose a bit, and a lost bit is a pellet that misses.
//
// So this is the oracle that has to exist before the first shot moves. What it
// pins down:
//
//   1. polar -> components   direction/speed written, hspeed/vspeed read back
//   2. components -> polar   hspeed/vspeed written, direction/speed read back
//   3. the snap              a recomputed component that lands within 1e-4 of
//                            an integer is *replaced* by that integer, which is
//                            why a shot fired at 90 degrees has hspeed exactly
//                            0 and not 8e-16
//   4. the no-op             writing hspeed the value it already holds must not
//                            recompute direction, or an instance at rest
//                            forgets where it was aimed
//   5. motion_add            adds to the components, so it takes the path that
//                            does *not* snap
//   6. the Scattergun order  the three round trips, in the order the reference
//                            performs them
//   7. lengthdir_x/y, cos    the pellet's 15-pixel nudge and the alarm[0]
//                            formula in the same event
//
// Every sample carries its own complete operation list and is replayed by
// tests/Gg2.Tests/MotionSampleTests.cs through `Motion` in the same order, so a
// row cannot be invalidated by anything the game did between calls. Each list
// begins with its own reset, which makes it independent of the sample before.
//
// Inputs are sent as integer ratios, never as decimal literals: GM8's parser is
// one ULP off for some decimals (docs/SIMULATION.md), and n/d is exact on both
// sides for any n and d that fit a double. Results come back through the exact
// mantissa encoder, because string_format() runs out of digits well before a
// ULP question is answered.
//
// The probe is a CollisionDummy - no sprite, no events, not solid, and used by
// the reference only during map compression - so a match in progress does not
// notice it. It is destroyed at the end of the run.
//
// Requires a running game with the agent bridge. No bots and no players needed;
// a dedicated server with an empty match is the cheapest host.
//
//   node tools/sample-motion.js [--out <file>] [--dry]
//
// The bridge serves one TCP client at a time and an MCP session holds it - see
// CLAUDE.md. Start the game from the CLI before running this.
//
// Known limitation: the encoder reports -0 as 0, so the fixture cannot pin the
// sign of a zero component. Nothing observable depends on it - the one place it
// could matter is a direction recomputed from two zeroes, and the no-op rule in
// section 4 is what stops that happening.
//=============================================================================

const fs = require('fs');
const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const DRY = process.argv.includes('--dry');
const mcp = DRY ? null : require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/motion-samples.json');

// Samples per gg2_eval. Each one emits four encoder blocks, so this is bounded
// by the size of the GML source rather than by the size of the answer.
const BATCH = 6;

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

//---------------------------------------------------------------------------
// The exact-double encoder
//
// ⚠️ NOT the one in sample-physics.js and sample-rng.js. Theirs opens with
// `if (v == 0)` and closes with two `while` normalisation loops, and **every
// one of those comparisons is GML's fuzzy one**: two reals compare equal when
// they are within 1e-13 of each other (docs/SIMULATION.md). So `v == 0` is
// true for cos(degtorad(90)), which is 6.1e-17, and the encoder reports a hard
// zero for it. That is exactly the value this tool exists to measure — a
// component a hair off an axis is what the snap is about — so the fuzz had to
// go.
//
// Nothing here compares a small number to anything:
//
//   * the zero and sign tests are made on the value scaled by 2^400, which puts
//     any real double this tool can see far outside the epsilon;
//   * there are no normalisation loops. floor(log2(a)) may be off by one either
//     way and it does not matter: a * 2^(52-e) lands in [2^51, 2^54) and is an
//     exact integer in every case, because scaling by a power of two only moves
//     the exponent.
//
// Chunks stay under 2^19 throughout, because GM8's floor() truncates through a
// 32-bit integer and wraps silently above 2^31.
//---------------------------------------------------------------------------
const enc = (src) => `
{
    global.mV = ${src};
    global.mZ = global.mV * power(2, 400);
    if (global.mZ == 0) { global.mOut += "0 0 0 0 0 | "; }
    else {
        global.mS = 1; global.mA = global.mV;
        if (global.mZ < 0) { global.mS = -1; global.mA = -global.mA; }
        global.mE = floor(log2(global.mA));
        global.mN = global.mA * power(2, 52 - global.mE);
        global.mC1 = floor(global.mN / 68719476736);
        global.mN = global.mN - global.mC1 * 68719476736;
        global.mC2 = floor(global.mN / 262144);
        global.mC3 = global.mN - global.mC2 * 262144;
        global.mOut += string(global.mS) + " " + string(global.mE) + " "
            + string(global.mC1) + " " + string(global.mC2) + " " + string(global.mC3) + " | ";
    }
}`;

// The three chunks are an exact integer of up to 54 bits, so they are put back
// together with BigInt: the sum overflows a double's exact integer range even
// though the value it encodes does not.
function decodeDouble(text) {
  const [sign, exponent, c1, c2, c3] = text.trim().split(/\s+/).map(Number);
  if (sign === 0) return 0;
  const n = (BigInt(c1) << 36n) + (BigInt(c2) << 18n) + BigInt(c3);
  return sign * Number(n) * Math.pow(2, exponent - 52);
}

//---------------------------------------------------------------------------
// Operations
//
// A sample is an ordered list of writes. Each carries its arguments as integer
// ratios so that GM8's decimal parser is never in the loop, and the C# test
// applies exactly this list to a `Motion`.
//---------------------------------------------------------------------------

// A ratio that both sides evaluate to the same double: n/d is one correctly
// rounded IEEE division wherever it is done.
const r = (n, d = 1) => [n, d];
const gml = (v) => (v[1] === 1 ? `(${v[0]})` : `((${v[0]})/(${v[1]}))`);
const show = (v) => (v[1] === 1 ? `${v[0]}` : `${v[0]}/${v[1]}`);

const op = (name, a, b) => (b === undefined ? { op: name, a } : { op: name, a, b });

function opGml(o) {
  switch (o.op) {
    case 'direction=': return `direction = ${gml(o.a)};`;
    case 'direction+=': return `direction += ${gml(o.a)};`;
    case 'speed=': return `speed = ${gml(o.a)};`;
    case 'speed+=': return `speed += ${gml(o.a)};`;
    case 'hspeed=': return `hspeed = ${gml(o.a)};`;
    case 'hspeed+=': return `hspeed += ${gml(o.a)};`;
    case 'vspeed=': return `vspeed = ${gml(o.a)};`;
    case 'vspeed+=': return `vspeed += ${gml(o.a)};`;
    case 'motion_add': return `motion_add(${gml(o.a)}, ${gml(o.b)});`;
    default: throw new Error(`unknown op ${o.op}`);
  }
}

// Every sample starts from the same place, stated rather than assumed: the
// game keeps stepping between evals and an instance carries its motion across
// them, so a list that did not reset would measure the list before it.
const RESET = [op('speed=', r(0)), op('direction=', r(0))];

//---------------------------------------------------------------------------
// What to ask
//---------------------------------------------------------------------------

// Directions worth asking about: the axes and diagonals, where cos or sin is a
// hair off an exact 0 or 1 and the snap decides the answer; the quadrant
// boundaries; values outside [0, 360) in both directions, which pin the
// normalisation; and a few that are not multiples of anything.
const DIRECTIONS = [
  r(0), r(1), r(30), r(45), r(60), r(90), r(120), r(135), r(180), r(200),
  r(225), r(270), r(315), r(330), r(359), r(360), r(361), r(720),
  r(-30), r(-90), r(-400),
  r(91, 2), r(15, 2), r(45, 2), r(549, 4), r(89999, 1000), r(900001, 10000),
];

// Speeds GG2 actually fires at (13 scattergun, 11 after the worst spread roll,
// 4 needle, 20 rocket), plus zero, a fraction, and a negative - `speed` may be
// negative in GM8 and the reference never guards against it.
const SPEEDS = [r(0), r(1), r(4), r(13), r(20), r(100), r(1, 2), r(-3), r(11)];

// Component pairs. Zeroes are in the list on purpose: writing a component the
// value it already holds is the no-op case, and after RESET both are zero.
const COMPONENTS = [
  r(0), r(1), r(-1), r(3), r(-3), r(1, 2), r(-1, 2), r(13), r(-7),
];

function polarToComponents() {
  const out = [];
  for (const d of DIRECTIONS)
    for (const s of SPEEDS)
      out.push({
        family: 'polar',
        label: `dir ${show(d)} speed ${show(s)}`,
        ops: [...RESET, op('direction=', d), op('speed=', s)],
      });
  return out;
}

function componentsToPolar() {
  const out = [];
  for (const h of COMPONENTS)
    for (const v of COMPONENTS)
      out.push({
        family: 'components',
        label: `h ${show(h)} v ${show(v)}`,
        ops: [...RESET, op('hspeed=', h), op('vspeed=', v)],
      });
  return out;
}

// The no-op rule, isolated. After `speed = 0` both components are zero (one of
// them negatively), so writing either of them a zero must change nothing at
// all: direction has to still be what it was aimed at. Without the rule the
// recompute would fold every one of these to 0 or 180.
function noOps() {
  const out = [];
  for (const d of [r(200), r(45), r(137), r(0), r(315)]) {
    out.push({
      family: 'noop',
      label: `hspeed=0 at dir ${show(d)}`,
      ops: [...RESET, op('direction=', d), op('speed=', r(0)), op('hspeed=', r(0))],
    });
    out.push({
      family: 'noop',
      label: `vspeed=0 at dir ${show(d)}`,
      ops: [...RESET, op('direction=', d), op('speed=', r(0)), op('vspeed=', r(0))],
    });
  }

  // And the same question with a live speed: re-writing hspeed the value it
  // already has cannot be expressed as a literal, but writing vspeed its own
  // value on an axis-aligned shot can, because the snap has already made it
  // exact.
  out.push({
    family: 'noop',
    label: 'vspeed=-13 after dir 90 speed 13',
    ops: [...RESET, op('direction=', r(90)), op('speed=', r(13)), op('vspeed=', r(-13))],
  });
  return out;
}

// motion_add goes through the component path, so its results must *not* be
// snapped. The cancelling pairs are the ones that show it: a shot fired up at
// 7 and pushed down at 7 keeps a hspeed of 4e-16 rather than collapsing to
// zero, and every later comparison sees that.
function motionAdds() {
  const cases = [
    [r(45), r(10), r(90), r(5)],
    [r(0), r(0), r(180), r(3)],
    [r(270), r(7), r(90), r(7)],
    [r(90), r(13), r(270), r(13)],
    [r(0), r(13), r(180), r(13)],
    [r(137), r(4), r(23), r(9)],
    [r(30), r(1, 2), r(210), r(1, 2)],
    [r(200), r(-3), r(20), r(3)],
  ];
  return cases.map(([d, s, ad, as]) => ({
    family: 'motion_add',
    label: `dir ${show(d)} speed ${show(s)} + motion_add(${show(ad)}, ${show(as)})`,
    ops: [...RESET, op('direction=', d), op('speed=', s), op('motion_add', ad, as)],
  }));
}

// The Scattergun's own sequence, in its own order:
//
//     createShot(..., owner.aimDirection, 13)   direction = aim; speed = 13
//     shot.hspeed += owner.hspeed               component write
//     shot.speed  += random(4)-2                polar write
//     shot.direction += random(15)-7.5          polar write
//
// The random draws are already settled by sample-rng.js, so they are sent here
// as the fixed values they can take at the edges of their ranges.
function scattergun() {
  const aims = [r(0), r(45), r(90), r(135), r(180), r(225), r(270), r(330), r(3457, 100)];
  const ownerH = [r(0), r(15, 4), r(-15, 4), r(1)];
  const dSpeed = [r(-2), r(0), r(3, 2)];
  const dDir = [r(-15, 2), r(0), r(13, 4)];

  const out = [];
  for (let i = 0; i < aims.length; i++) {
    for (let j = 0; j < ownerH.length; j++) {
      const ds = dSpeed[(i + j) % dSpeed.length];
      const dd = dDir[(i + 2 * j) % dDir.length];
      out.push({
        family: 'scattergun',
        label: `aim ${show(aims[i])} ownerH ${show(ownerH[j])} dspeed ${show(ds)} ddir ${show(dd)}`,
        ops: [
          ...RESET,
          op('direction=', aims[i]),
          op('speed=', r(13)),
          op('hspeed+=', ownerH[j]),
          op('speed+=', ds),
          op('direction+=', dd),
        ],
      });
    }
  }
  return out;
}

// The snap, isolated so that it is measured rather than inferred.
//
// Everything here is aimed along an axis or written straight into a component,
// which means cos is exactly 1 and sin exactly 0 and **no trig is involved at
// all**. That matters: the polar family above cannot separate "the snap fired"
// from "the engine's x87 trig landed somewhere else", and these rows can.
//
// Two questions:
//
//   1. Where is the threshold? The sweep steps the offset from an integer in
//      units of 2^-30 across 1e-4, which is 107374.1824 of them, so the rows
//      either side of 107374 bracket it exactly.
//   2. What gets snapped? A direct write to speed, direction, hspeed or vspeed
//      stores what it was given; it is only the *recomputed* variables that
//      snap. `hspeed = 3.00005` therefore keeps its 3.00005 and reports a speed
//      of exactly 3 — which is how the speed snap was found, and it is not in
//      the OpenGMK transcription this model started from.
function snaps() {
  const out = [];

  // 1. The threshold, on the one path where no trig can perturb it.
  for (const n of [0, 1, 100000, 107370, 107373, 107374, 107375, 107376, 107380, 110000, 536870912]) {
    out.push({
      family: 'snap',
      label: `speed 3 + ${n}/2^30 at dir 0`,
      ops: [...RESET, op('speed=', r(3 * 1073741824 + n, 1073741824))],
    });
  }

  // 2. What is recomputed, and what is merely stored.
  const written = [
    ['hspeed 3.00005', [op('hspeed=', r(300005, 100000))]],
    ['vspeed -4.00005', [op('vspeed=', r(-400005, 100000))]],
    ['h 3.000001 v -4 (speed 5.0000006)', [op('hspeed=', r(3000001, 1000000)), op('vspeed=', r(-4))]],
    ['h 3.001 v -4 (speed 5.0006)', [op('hspeed=', r(3001, 1000)), op('vspeed=', r(-4))]],
    ['h 1000000 v -1 (direction 5.7e-5)', [op('hspeed=', r(1000000)), op('vspeed=', r(-1))]],
    ['h 10000 v -1 (direction 5.7e-3)', [op('hspeed=', r(10000)), op('vspeed=', r(-1))]],
    ['h 1e-15 (speed 1e-15)', [op('hspeed=', r(1, 1000000000000000))]],
    ['direction 45.00005 then speed 5', [op('direction=', r(4500005, 100000)), op('speed=', r(5))]],
    ['direction 45.001 then speed 5', [op('direction=', r(45001, 1000)), op('speed=', r(5))]],
  ];

  for (const [label, ops] of written)
    out.push({ family: 'snap', label, ops: [...RESET, ...ops] });

  return out;
}

const SAMPLES = [
  ...polarToComponents(),
  ...componentsToPolar(),
  ...noOps(),
  ...motionAdds(),
  ...scattergun(),
  ...snaps(),
];

//---------------------------------------------------------------------------
// Asking
//---------------------------------------------------------------------------

// The probe is re-created at the top of every batch. An instance in a live game
// can be destroyed under you by a round restart, and a destroyed probe would
// otherwise answer with whatever the bridge's own scope happens to hold.
const PROBE = `
    if (!variable_global_exists("mProbe")) global.mProbe = noone;
    if (!instance_exists(global.mProbe))
        global.mProbe = instance_create(0, 0, CollisionDummy);
    with (global.mProbe) { x = 0; y = 0; }`;

// The four reads are stashed into an array and encoded by one loop rather than
// by four inlined encoder blocks: the encoder is twenty lines and a batch of
// six samples would otherwise be a thousand-line eval for no reason.
function sampleGml(sample) {
  return `
    with (global.mProbe)
    {
        ${sample.ops.map(opGml).join('\n        ')}
        global.mR[0] = hspeed; global.mR[1] = vspeed;
        global.mR[2] = direction; global.mR[3] = speed;
    }
    for (global.mI = 0; global.mI < 4; global.mI += 1)
    ${enc('global.mR[global.mI]')}`;
}

async function runBatch(batch) {
  const code = `global.mOut = "";${PROBE}\n${batch.map(sampleGml).join('\n')}`;
  await ev(code);
  const raw = (await evalx('global.mOut')).trim();

  const fields = raw.split('|').map((s) => s.trim()).filter((s) => s.length > 0);
  if (fields.length !== batch.length * 4)
    throw new Error(`expected ${batch.length * 4} values, got ${fields.length}: ${raw.slice(0, 160)}`);

  return batch.map((sample, i) => {
    const [h, v, d, s] = fields.slice(i * 4, i * 4 + 4);
    return {
      family: sample.family,
      label: sample.label,
      ops: sample.ops,
      hspeed: { bits: h, value: decodeDouble(h) },
      vspeed: { bits: v, value: decodeDouble(v) },
      direction: { bits: d, value: decodeDouble(d) },
      speed: { bits: s, value: decodeDouble(s) },
    };
  });
}

//---------------------------------------------------------------------------
// lengthdir_x/y and cos(degtorad(d))
//
// Not motion variables, but the same event: each pellet is nudged 15 pixels
// along its own direction with lengthdir, and its lifetime alarm is set from
// cos(degtorad(aimDirection)). Both are one multiply away from the conversions
// above and both reach the wire through a trajectory, so they are measured with
// the same encoder rather than assumed.
//---------------------------------------------------------------------------
async function trig() {
  const out = [];
  for (let i = 0; i < DIRECTIONS.length; i += 4) {
    const chunk = DIRECTIONS.slice(i, i + 4);
    const code = `global.mOut = "";\n${chunk
      .map((d) => `global.mD = ${gml(d)};
        global.mR[0] = lengthdir_x(15, global.mD);
        global.mR[1] = lengthdir_y(15, global.mD);
        global.mR[2] = cos(degtorad(global.mD));
        global.mR[3] = sin(degtorad(global.mD));
        global.mR[4] = degtorad(global.mD);
        for (global.mI = 0; global.mI < 5; global.mI += 1)
        ${enc('global.mR[global.mI]')}`)
      .join('\n')}`;
    await ev(code);
    const raw = (await evalx('global.mOut')).trim();
    const fields = raw.split('|').map((s) => s.trim()).filter((s) => s.length > 0);
    if (fields.length !== chunk.length * 5)
      throw new Error(`trig: expected ${chunk.length * 5} values, got ${fields.length}`);

    chunk.forEach((d, j) => {
      const [lx, ly, c, s, rad] = fields.slice(j * 5, j * 5 + 5);
      out.push({
        direction: d,
        lengthdirX: { bits: lx, value: decodeDouble(lx) },
        lengthdirY: { bits: ly, value: decodeDouble(ly) },
        cos: { bits: c, value: decodeDouble(c) },
        sin: { bits: s, value: decodeDouble(s) },
        degtorad: { bits: rad, value: decodeDouble(rad) },
      });
    });
    process.stdout.write('.');
  }
  return out;
}

async function main() {
  if (DRY) {
    const byFamily = {};
    for (const s of SAMPLES) byFamily[s.family] = (byFamily[s.family] || 0) + 1;
    console.log(`${SAMPLES.length} motion samples in ${Math.ceil(SAMPLES.length / BATCH)} evals`);
    for (const [k, v] of Object.entries(byFamily)) console.log(`  ${k}: ${v}`);
    console.log(`${DIRECTIONS.length} trig rows in ${Math.ceil(DIRECTIONS.length / 4)} evals`);
    console.log('\nfirst sample:\n' + sampleGml(SAMPLES[0]));
    return;
  }

  process.stdout.write('motion: ');
  const rows = [];
  for (let i = 0; i < SAMPLES.length; i += BATCH) {
    rows.push(...(await runBatch(SAMPLES.slice(i, i + BATCH))));
    process.stdout.write('.');
  }

  process.stdout.write('\ntrig:   ');
  const trigRows = await trig();
  process.stdout.write('\n');

  await ev('if (variable_global_exists("mProbe")) if (instance_exists(global.mProbe)) with(global.mProbe) instance_destroy();');

  const out = {
    generated: new Date().toISOString().slice(0, 10),
    samples: rows,
    trig: trigRows,
  };

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 1) + '\n');
  console.log(`wrote ${OUT}: ${rows.length} motion samples, ${trigRows.length} trig rows`);
}

main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
