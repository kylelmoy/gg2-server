#!/usr/bin/env node
//=============================================================================
// sample-collision.js - ask the running game what is solid, and write it down.
//
// Phase 3's exit criterion (docs/PLAN.md) is that a few thousand sampled
// place_free queries agree with the live game exactly, on at least three maps
// of different shapes, with samples concentrated on cell boundaries and map
// edges. This is the "ask the live game" half; the C# side compares against it
// as an ordinary unit test, with no game running.
//
// It samples collision_point against CollisionMapO, which is the map's own
// collision instance - a sprite built at runtime from the walkmask PNG and
// scaled 6x. That is the primitive every place_free in the physics is built on,
// so pinning it pins the rest.
//
// Windows are placed deliberately rather than uniformly, because the answer is
// only ever in doubt at a boundary:
//
//   - straddling cell boundaries, at both x % 6 == 0 and x % 6 == 3;
//   - across the origin, so negative coordinates are covered;
//   - across the far edge, where the sprite stops.
//
// Requires a running dedicated server with the agent bridge - see
// gg2-agent/CLAUDE.md. Only one client can hold a bridge at a time.
//
//   node tools/sample-collision.js [--out <file>] [--maps ctf_truefort,...]
//=============================================================================

const fs = require('fs');
const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));
const walkmask = require(path.join(AGENT, 'tools', 'walkmask.js'));

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

// Three shapes on purpose: a wide symmetric fort, a long flat hill map, and the
// tiny collision test map whose geometry is deliberately awkward.
const MAPS = arg('maps', 'ctf_truefort,koth_valley,gg_debug').split(',');
const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/collision-samples.json');

const CELL = 6;
const WINDOW_W = 40;
const WINDOW_H = 16;

// callTool hands back the plain result, but the MCP transport wraps it in a
// content array - accept either so this works called directly or over MCP.
async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

// One window of collision_point answers as a row-major string of 0/1.
//
// Built inside the game rather than one call per point: a bridge round trip is
// ~40ms, and 640 of them per window would take half a minute for no reason.
async function window(x0, y0, w, h) {
  // Not `pi` for the inner counter: pi is a GM8 built-in constant and assigning
  // to it is a compile error ("Variable name expected"), which is a confusing
  // way to be told so.
  const code = [
    'global.probe = "";',
    `for(qy=0; qy<${h}; qy+=1)`,
    `    for(qx=0; qx<${w}; qx+=1)`,
    `        global.probe += string(collision_point(${x0}+qx, ${y0}+qy, CollisionMapO, 1, 0) > 0);`,
  ].join('\n');

  await mcp.callTool('gg2_eval', { code });
  const bits = await evalx('global.probe');

  if (bits.length !== w * h) throw new Error(`window returned ${bits.length} bits, wanted ${w * h}`);
  return bits;
}

// Where to look. Interior windows are placed on real mask edges, because a
// window in the middle of solid rock or open sky proves nothing.
function windowsFor(mask) {
  const worldW = mask.width * CELL;
  const worldH = mask.height * CELL;
  const spots = [];

  // The origin and the far corner: both sides of where the sprite stops.
  spots.push({ why: 'origin', x: -CELL, y: -CELL });
  spots.push({ why: 'far-corner', x: worldW - WINDOW_W + CELL, y: worldH - WINDOW_H + CELL });
  spots.push({ why: 'left-edge', x: -CELL, y: Math.floor(worldH / 2) });
  spots.push({ why: 'top-edge', x: Math.floor(worldW / 2), y: -CELL });

  // Interior: cells where a solid meets an open neighbour, which is where a
  // boundary rule can be wrong.
  const edges = [];
  for (let cy = 1; cy < mask.height - 1 && edges.length < 64; cy++) {
    for (let cx = 1; cx < mask.width - 1; cx++) {
      const here = mask.solid(cx, cy);
      if (here !== mask.solid(cx - 1, cy) || here !== mask.solid(cx, cy - 1)) {
        edges.push([cx, cy]);
        break;
      }
    }
  }

  // Spread the picks over the map rather than taking the first few rows, and
  // offset half of them by half a cell so both x % 6 == 0 and x % 6 == 3 are
  // covered.
  const step = Math.max(1, Math.floor(edges.length / 8));
  for (let i = 0, n = 0; i < edges.length && n < 8; i += step, n++) {
    const [cx, cy] = edges[i];
    const offset = n % 2 === 0 ? 0 : 3;
    spots.push({
      why: `edge@${cx},${cy}`,
      x: cx * CELL - WINDOW_W / 2 + offset,
      y: cy * CELL - WINDOW_H / 2,
    });
  }

  return spots;
}

async function main() {
  const out = { generated: new Date().toISOString().slice(0, 10), cell: CELL, maps: [] };

  for (const map of MAPS) {
    process.stdout.write(`${map}: `);

    await mcp.callTool('gg2_eval', { code: `serverGotoMap("${map}");` });

    // The map load happens over a frame or two; wait for the game to agree it
    // is there before asking it anything.
    await mcp.callTool('gg2_wait', { expr: `global.currentMap == "${map}"`, frames: 300 });

    // map_width() measures the map ART, not the walkmask - it is
    // background_width[7] * background_xscale[7]. Twenty-one of the twenty-two
    // shipped maps have the two the same size, and gg_debug does not, so this
    // check is against the PNG's own dimensions rather than the mask's.
    const live = await evalx('string(map_width()) + "x" + string(map_height())');
    const mask = walkmask.decode(map);
    const png = fs.readFileSync(walkmask.mapFile(map));
    const art = { w: png.readUInt32BE(16), h: png.readUInt32BE(20) };

    const expectedDims = `${art.w * CELL}x${art.h * CELL}`;
    if (live !== expectedDims) throw new Error(`${map}: game says ${live}, art says ${expectedDims}`);

    const record = {
      name: map,
      mapWidth: art.w * CELL,
      mapHeight: art.h * CELL,
      maskWidth: mask.width * CELL,
      maskHeight: mask.height * CELL,
      windows: [],
    };

    for (const spot of windowsFor(mask)) {
      const bits = await window(spot.x, spot.y, WINDOW_W, WINDOW_H);
      record.windows.push({ why: spot.why, x: spot.x, y: spot.y, w: WINDOW_W, h: WINDOW_H, solid: bits });
      process.stdout.write('.');
    }

    out.maps.push(record);
    process.stdout.write(` ${record.windows.length} windows\n`);
  }

  const total = out.maps.reduce((n, m) => n + m.windows.length * WINDOW_W * WINDOW_H, 0);
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 2) + '\n');
  console.log(`wrote ${OUT}: ${total} sampled points across ${out.maps.length} maps`);
}

main().catch((e) => {
  console.error('FAIL', e.message);
  process.exit(1);
});
