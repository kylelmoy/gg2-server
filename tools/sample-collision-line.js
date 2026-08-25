#!/usr/bin/env node
//=============================================================================
// sample-collision-line.js - GM8's collision_line, measured against the running
// game.
//
// This is the primitive the Rifle's hitscan is built out of
// (Rifle.events/User Event 3.xml binary-searches it eleven times) and the one
// collision_line_bulletblocking asks before any rocket is allowed to fly. It is
// the last of the three phase-5 oracles and the only one that is not a
// one-line formula: GM8 clips the segment to the instance's bounding box,
// rounds the clipped endpoints to integers, walks whole pixels along the
// longer axis and looks each step up in the sprite's collision mask. Every one
// of those steps is observable, and each of them changes an answer somewhere.
//
// Two things this tool exists to pin down, both of which cost a session a day
// if guessed:
//
//   1. The box is asymmetric. A segment reaches an instance over
//      [bbox_left, bbox_right + 1) horizontally - inclusive at the low end,
//      exclusive at the high end - and the *rounding* of the walk's endpoints
//      is round-half-to-even, so whether a coordinate ending in .5 counts
//      depends on the parity of the pixel it would round to. Probes at both
//      parities are therefore not redundant.
//   2. The walk is not the mathematical segment. Clipping, then rounding, then
//      stepping whole pixels means a line can register against a box it never
//      geometrically enters, by up to half a pixel. Sweeping a line across a
//      1x1 box at 1/8-pixel offsets is what makes that visible; nothing with a
//      character-sized box would have shown it.
//
// Requires a running game with the agent bridge. Unlike sample-physics.js and
// sample-primitives.js it does **not** need a bot: every probe is a BulletWall
// created and scaled by this tool, plus the map's own collision sprite. So the
// gg2.ini bots recipe in CLAUDE.md is not needed here.
//
//   node tools/sample-collision-line.js [--out <file>]
//
// The bridge serves one TCP client at a time and an MCP session holds it - see
// CLAUDE.md. Start the game from the CLI before running this.
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

const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/collision-line-samples.json');

// How many collision_line calls go into one gg2_eval. The reply is one
// character per call, so this is bounded by the GML source size rather than by
// the answer.
const BATCH = 90;

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

const ev = (code) => mcp.callTool('gg2_eval', { code });

//---------------------------------------------------------------------------
// Coordinates
//
// Every coordinate is a base integer plus a ratio of two integers, and it is
// emitted as *that expression* rather than as a decimal literal, because GM8's
// parser reads some decimals one ULP above the IEEE nearest (CLAUDE.md, "GM8
// arithmetic"). An integer over an integer is safe: the division happens in
// double on both sides and gives the same bits, so the number this tool writes
// into the fixture is the number the game was asked about.
//---------------------------------------------------------------------------

function coord(base, num = 0, den = 1) {
  return { gml: `(${base})+(${num})/(${den})`, val: base + num / den };
}

const at = (v) => (typeof v === 'object' ? v : coord(v));

function line(x1, y1, x2, y2) {
  return [at(x1), at(y1), at(x2), at(y2)];
}

//---------------------------------------------------------------------------
// The line families
//
// Deliberately not random. Each family isolates one decision in the algorithm,
// and the interesting answers are all within a pixel of a box edge - a uniform
// scatter over open space measures nothing but "no".
//---------------------------------------------------------------------------

function linesFor(box) {
  const [L, T, R, B] = box;
  const out = [];

  // Vertical sweeps across the left and right edges. A vertical line's x is
  // constant, so this reads the horizontal acceptance interval directly.
  for (let k = -8; k <= 8 * (R - L + 3); k++)
    out.push(line(coord(L - 1, k, 8), T - 40, coord(L - 1, k, 8), B + 40));

  // Horizontal sweeps across the top and bottom edges - only across the edges,
  // because the interior of a tall box answers "yes" to every one of them and
  // says nothing.
  for (const edge of [T - 1, B - 1])
    for (let k = -8; k <= 24; k++)
      out.push(line(L - 40, coord(edge, k, 8), R + 40, coord(edge, k, 8)));

  // Endpoints that stop short of, land on, or reach past an edge. This is a
  // different question from the sweep above: the clip only trims the segment
  // when it overhangs, so a segment that ends inside the box exercises the
  // unclipped path.
  for (let k = -16; k <= 16; k++) {
    out.push(line(L - 60, T, coord(L, k, 8), T));
    out.push(line(coord(R + 1, k, 8), B, R + 60, B));
    out.push(line(L - 60, coord(T, k, 8), L + 2, coord(T, k, 8)));
  }

  // Diagonals. The slope decides which axis the walk iterates over and how far
  // the rounded walk can stray from the true segment, so both signs and both
  // sides of |slope| = 1 are needed.
  const slopes = [[1, 1], [-1, 1], [1, 2], [-1, 2], [2, 1], [-2, 1], [1, 3], [-1, 3], [3, 1], [-3, 1]];
  for (const [dy, dx] of slopes) {
    const span = 20 * dx;
    for (let k = -16; k <= 16; k++) {
      // Through the box's top-left corner, swept perpendicular-ish by k/8.
      out.push(line(L - span, coord(T - dy * 20, k, 8), L + span, coord(T + dy * 20, k, 8)));
      // And the same line walked the other way, which must agree.
      if (k % 6 === 0)
        out.push(line(L + span, coord(T + dy * 20, k, 8), L - span, coord(T - dy * 20, k, 8)));
    }
  }

  // Degenerate segments: a point. GM8 takes the same path, with a walk of
  // exactly one step, and it is the cheapest way to read the acceptance
  // rectangle off the engine. Only around the corners - the middle of an edge
  // repeats what the sweeps above already said.
  for (const [cx, cy] of [[L, T], [R, T], [L, B], [R, B]])
    for (let ky = -6; ky <= 6; ky++)
      for (let kx = -6; kx <= 6; kx++)
        if ((kx + ky) % 3 === 0)
          out.push(line(coord(cx, kx, 4), coord(cy, ky, 4), coord(cx, kx, 4), coord(cy, ky, 4)));

  // Rifle-length lines. 1500 px is what Rifle.events/User Event 3.xml fires,
  // and the binary search then asks about 750, 375, ... of it - so the walk is
  // hundreds of steps long and any per-step drift has room to show.
  for (let a = 0; a < 36; a++) {
    const dx = Math.round(1500 * Math.cos((a * Math.PI) / 18));
    const dy = Math.round(1500 * Math.sin((a * Math.PI) / 18));
    const x0 = L - Math.round(dx / 3);
    const y0 = T - Math.round(dy / 3);
    for (const [n, d] of [[0, 1], [1, 3], [2, 3], [1, 7], [5, 7]])
      out.push(line(coord(x0, n, d), coord(y0, n, d), coord(x0 + dx, n, d), coord(y0 + dy, n, d)));
  }

  return out;
}

//---------------------------------------------------------------------------
// The probes
//
// A BulletWall, because ctf_truefort has none of its own: collision_line takes
// an object and answers about every instance of it, so a probe against an
// object the map also uses would be measuring the map. Its sprite is 6 x 60
// with a full rectangular mask and its origin at (0, 0), and image_xscale /
// image_yscale reshape it into whatever box a case needs.
//---------------------------------------------------------------------------

const PROBES = [
  { label: 'unit-even', x: 3000, y: 620, xs: [1, 6], ys: [1, 60], mask: 'own' },
  { label: 'unit-odd', x: 2999, y: 619, xs: [1, 6], ys: [1, 60], mask: 'own' },
  { label: 'bar-even', x: 3000, y: 600, xs: [1, 1], ys: [1, 1], mask: 'own' },
  { label: 'bar-odd', x: 3001, y: 601, xs: [1, 1], ys: [1, 1], mask: 'own' },
  { label: 'scaled', x: 3000, y: 620, xs: [3, 2], ys: [3, 5], mask: 'own' },
  { label: 'offset-origin', x: 3000, y: 620, xs: [1, 1], ys: [1, 1], mask: 'dup' },
  // image_xscale = -1 is how a character faces left, and GM8 divides by it when
  // it turns a walk point into a mask pixel - so the acceptance interval flips
  // which end is open. Measured rather than assumed; see docs/SIMULATION.md.
  { label: 'mirrored', x: 3000, y: 620, xs: [-1, 1], ys: [1, 1], mask: 'dup' },
];

// Re-created and re-pinned at the top of every batch. The probe is an instance
// in a live game and the game keeps running between calls: a round restart
// destroys map elements, and a destroyed probe answers "no" to everything
// without saying so. Every batch re-reads the bounding box it actually got and
// the caller checks it, so a row cannot be silently invalidated.
function setupGml(probe) {
  const mask = probe.mask === 'dup' ? 'global.clDup' : '-1';
  return `
    if (!variable_global_exists("clProbe")) global.clProbe = noone;
    if (!instance_exists(global.clProbe))
        global.clProbe = instance_create(0, 0, BulletWall);
    with(global.clProbe)
    {
        x = ${probe.x}; y = ${probe.y};
        mask_index = ${mask};
        image_xscale = (${probe.xs[0]})/(${probe.xs[1]});
        image_yscale = (${probe.ys[0]})/(${probe.ys[1]});
        global.clOut += string(bbox_left) + " " + string(bbox_top) + " "
            + string(bbox_right) + " " + string(bbox_bottom) + " ; ";
    }`;
}

async function runBatch(setup, object, lines) {
  const calls = lines
    .map((l) => `global.clOut += string(collision_line(${l[0].gml}, ${l[1].gml}, ${l[2].gml}, ${l[3].gml}, ${object}, 1, 0) > 0);`)
    .join('\n');

  await ev(`global.clOut = "";${setup}\n${calls}`);
  const raw = (await evalx('global.clOut')).trim();

  const [head, ...rest] = raw.split(';');
  const box = head.trim().split(/\s+/).map(Number);
  const bits = rest.join(';').trim();

  if (bits.length !== lines.length)
    throw new Error(`expected ${lines.length} answers, got ${bits.length}: ${raw.slice(0, 120)}`);

  return { box, bits };
}

async function sampleProbe(probe) {
  const setup = setupGml(probe);

  // One throwaway batch to learn the box, so the line families can be built
  // around the geometry the engine actually gave us rather than around the
  // scale arithmetic this tool guessed.
  const first = await runBatch(setup, 'BulletWall', [line(0, 0, 0, 0)]);
  const box = first.box;

  const lines = linesFor(box);
  const rows = [];

  for (let i = 0; i < lines.length; i += BATCH) {
    const chunk = lines.slice(i, i + BATCH);
    const { box: got, bits } = await runBatch(setup, 'BulletWall', chunk);

    if (got.join(',') !== box.join(','))
      throw new Error(`probe box moved: ${box} -> ${got}`);

    chunk.forEach((l, j) => rows.push([l[0].val, l[1].val, l[2].val, l[3].val, bits[j] === '1' ? 1 : 0]));
    process.stdout.write('.');
  }

  return { label: probe.label, x: probe.x, y: probe.y, box, rows };
}

//---------------------------------------------------------------------------
// The map
//
// CustomMapO is a child of Obstacle, so collision_line(..., Obstacle, ...) -
// which is what collision_line_bulletblocking and the Rifle both ask - walks
// the map's runtime sprite. That sprite is precise, one mask pixel to six world
// pixels, so this section is the only one that exercises the mask lookup
// against something that is not a filled rectangle.
//---------------------------------------------------------------------------

function mapLines(width, height) {
  const out = [];

  // A coarse grid of rifle-length shots in every direction. Most of these hit
  // something; the ones that do not are the ones worth having.
  for (let gy = 200; gy < height - 100; gy += 271)
    for (let gx = 300; gx < width - 300; gx += 611)
      for (let a = 0; a < 24; a++) {
        const dx = Math.round(1500 * Math.cos((a * Math.PI) / 12));
        const dy = Math.round(1500 * Math.sin((a * Math.PI) / 12));
        out.push(line(coord(gx, 1, 3), coord(gy, 2, 7), coord(gx + dx, 1, 3), coord(gy + dy, 2, 7)));
      }

  // Short segments, which is what the Rifle's binary search narrows down to
  // and what a rocket's per-tick bullet-blocking check asks about.
  for (let gy = 150; gy < height - 100; gy += 97)
    for (let gx = 400; gx < width - 400; gx += 211)
      for (const [dx, dy] of [[6, 0], [0, 6], [6, 6], [6, -6], [13, 5], [-13, 5]])
        out.push(line(coord(gx, 3, 8), coord(gy, 5, 8), coord(gx + dx, 3, 8), coord(gy + dy, 5, 8)));

  // Off the edges of the mask, on every side. The walk indexes the sprite
  // directly, so a coordinate left of or above the map must be a miss and not
  // a wrapped-around lookup.
  for (let k = -40; k <= 40; k += 3) {
    out.push(line(coord(-30, k, 8), coord(300, 0), coord(30, k, 8), coord(300, 0)));
    out.push(line(coord(width - 30, k, 8), coord(300, 0), coord(width + 30, k, 8), coord(300, 0)));
    out.push(line(coord(600, 0), coord(-30, k, 8), coord(600, 0), coord(30, k, 8)));
    out.push(line(coord(600, 0), coord(height - 30, k, 8), coord(600, 0), coord(height + 30, k, 8)));
  }

  return out;
}

async function sampleMap() {
  const setup = `
    with(CustomMapO)
        global.clOut += string(bbox_left) + " " + string(bbox_top) + " "
            + string(bbox_right) + " " + string(bbox_bottom) + " ; ";`;

  const probe = await runBatch(setup, 'CustomMapO', [line(0, 0, 0, 0)]);
  const box = probe.box;

  const lines = mapLines(box[2] + 1, box[3] + 1);
  const rows = [];

  for (let i = 0; i < lines.length; i += BATCH) {
    const chunk = lines.slice(i, i + BATCH);
    const { box: got, bits } = await runBatch(setup, 'CustomMapO', chunk);

    if (got.join(',') !== box.join(','))
      throw new Error(`map box moved: ${box} -> ${got}`);

    chunk.forEach((l, j) => rows.push([l[0].val, l[1].val, l[2].val, l[3].val, bits[j] === '1' ? 1 : 0]));
    process.stdout.write('.');
  }

  return { box, rows };
}

//---------------------------------------------------------------------------

// Indented like the other fixtures, except that a row stays on one line.
// Nine thousand rows at seven lines each is sixty thousand lines of vertical
// bracket, which nobody will ever read and every diff has to carry; one line per
// query is both smaller and reviewable.
//
// The rows are swapped for sentinels before stringify and swapped back after,
// rather than patched with a regex over the output, so nothing depends on how
// the numbers happened to format. JSON.stringify prints the shortest
// round-tripping form of a double, so this is lossless.
//
// The sentinel is a private-use character and not something obvious like NUL,
// because JSON.stringify escapes control characters: a NUL sentinel comes back
// out as the six characters \u0000 and the substitution silently matches
// nothing, leaving every row in the fixture as a quoted placeholder. Ask me how
// I know.
//
// Written as a regex *literal* below for the same reason. A template literal
// swallows the backslash-d - it is not a recognised escape, so a pattern built
// that way matches the letter d instead of a digit, and the failure looks
// exactly the same.
const SENTINEL = '\uE000';

function serialise(document) {
  const rows = [];

  const stash = (value) => {
    if (Array.isArray(value) && value.every((v) => typeof v === 'number'))
      return `${SENTINEL}${rows.push(`[${value.join(',')}]`) - 1}${SENTINEL}`;
    if (Array.isArray(value)) return value.map(stash);
    if (value && typeof value === 'object')
      return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, stash(v)]));
    return value;
  };

  const text = JSON.stringify(stash(document), null, 1);
  const restored = text.replace(/"\uE000(\d+)\uE000"/g, (_, i) => rows[Number(i)]);

  // A placeholder that survived means the substitution missed, and writing the
  // file anyway would destroy the measurements it took a game to collect.
  if (restored.includes(SENTINEL))
    throw new Error('serialise: a row placeholder was not substituted back');

  return restored + '\n';
}

async function main() {
  // --dry builds every line family without a game, and prints how many
  // questions the run would ask. Worth doing before a real run: the families
  // are combinatorial and it is easy to write one that asks fifty thousand.
  if (DRY) {
    let total = 0;
    for (const probe of PROBES) {
      const w = Math.round(6 * (probe.xs[0] / probe.xs[1])) - 1;
      const h = Math.round(60 * (probe.ys[0] / probe.ys[1])) - 1;
      const n = linesFor([probe.x, probe.y, probe.x + Math.abs(w), probe.y + Math.abs(h)]).length;
      console.log(`${probe.label}: ${n}`);
      total += n;
    }
    const m = mapLines(5346, 1080).length;
    console.log(`walkmask: ${m}`);
    console.log(`total: ${total + m}`);
    return;
  }

  const map = await evalx('global.currentMap');

  // A duplicate of the probe sprite with its origin moved off (0, 0), because
  // every character mask has one and the mask lookup adds it back. Duplicated
  // rather than edited in place: sprite45 is the game's own wall collider and
  // this tool is a guest in a running server.
  await ev(`
    if (!variable_global_exists("clDup")) global.clDup = -1;
    if (!sprite_exists(global.clDup))
    {
        global.clDup = sprite_duplicate(sprite45);
        sprite_set_offset(global.clDup, 3, 30);
    }`);

  const sprite = (await evalx(`
    string(sprite_get_width(global.clDup)) + " " + string(sprite_get_height(global.clDup)) + " "
    + string(sprite_get_xoffset(global.clDup)) + " " + string(sprite_get_yoffset(global.clDup)) + " "
    + string(sprite_get_bbox_left(global.clDup)) + " " + string(sprite_get_bbox_top(global.clDup)) + " "
    + string(sprite_get_bbox_right(global.clDup)) + " " + string(sprite_get_bbox_bottom(global.clDup)) + " "
    + string(sprite_get_width(sprite45)) + " " + string(sprite_get_height(sprite45)) + " "
    + string(sprite_get_bbox_left(sprite45)) + " " + string(sprite_get_bbox_top(sprite45)) + " "
    + string(sprite_get_bbox_right(sprite45)) + " " + string(sprite_get_bbox_bottom(sprite45))`))
    .trim().split(/\s+/).map(Number);

  const out = {
    generated: new Date().toISOString().slice(0, 10),
    map,
    sprites: {
      own: { width: sprite[8], height: sprite[9], originX: 0, originY: 0, mask: sprite.slice(10, 14) },
      dup: { width: sprite[0], height: sprite[1], originX: sprite[2], originY: sprite[3], mask: sprite.slice(4, 8) },
    },
    probes: [],
    walkmask: null,
  };

  for (const probe of PROBES) {
    process.stdout.write(`${probe.label}: `);
    const result = await sampleProbe(probe);
    result.sprite = probe.mask;
    result.xscale = probe.xs[0] / probe.xs[1];
    result.yscale = probe.ys[0] / probe.ys[1];
    out.probes.push(result);
    process.stdout.write(` ${result.rows.length} (box ${result.box})\n`);
  }

  process.stdout.write('walkmask: ');
  out.walkmask = await sampleMap();
  process.stdout.write(` ${out.walkmask.rows.length}\n`);

  await ev(`
    if (!variable_global_exists("clProbe")) global.clProbe = noone;
    if (instance_exists(global.clProbe)) with(global.clProbe) instance_destroy();`);

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, serialise(out));

  const total = out.probes.reduce((n, p) => n + p.rows.length, 0) + out.walkmask.rows.length;
  console.log(`wrote ${OUT}: ${total} samples`);
}

main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
