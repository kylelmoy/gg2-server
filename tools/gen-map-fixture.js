#!/usr/bin/env node
//=============================================================================
// gen-map-fixture.js - regenerate tests/Gg2.Tests/Fixtures/maps.json
//
// Phase 0's exit criterion (docs/PLAN.md) is "decode every built-in map and
// match walkmask.js cell-for-cell". This script is the oracle side of that: it
// runs gg2-agent's decoders over every map shipped with the reference and
// writes down what they said, so the C# tests can check themselves against a
// decoder that has already been verified against the running game.
//
// The fixture is checked in so CI needs neither node nor gg2-agent. Re-run this
// only when the reference's maps change, and expect the diff to be empty
// otherwise - a fixture that moves on its own means one of the two decoders
// changed behaviour.
//
//   node tools/gen-map-fixture.js [--agent D:\code\gg2-agent] [--repo D:\code\Gang-Garrison-2]
//
// Two oracles, not one:
//
//   walkmask  - walkmask.js decode(). This is the real oracle: it is the
//               decoder gg2-nav-gen builds nav graphs with, and a 35x30 window
//               of it was checked cell-for-cell against collision_point in the
//               live game on ctf_avanti.
//
//   entities  - navgraph.js entities(). A weaker oracle: it splits the GGON
//               text with a regex rather than parsing it, so it only handles
//               the list form ([{...},{...}]) and would mangle the map form
//               ({0:{...},length:n}). gg_debug is the one shipped map written
//               that way, so it carries no entity oracle here and the C# side
//               tests it against a hand-checked expectation instead.
//=============================================================================

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const agent = path.resolve(arg('agent', 'D:/code/gg2-agent'));
const repo = path.resolve(arg('repo', 'D:/code/Gang-Garrison-2'));

const walkmask = require(path.join(agent, 'tools', 'walkmask.js'));
const navgraph = require(path.join(agent, 'tools', 'navgraph.js'));

const included = path.join(repo, 'Source', 'gg2', 'Included Files');
const maps = fs
  .readdirSync(included)
  .filter((f) => f.endsWith('.png'))
  .map((f) => f.slice(0, -4))
  .sort();

// gg_debug's entity block is a GGON map, which navgraph.js's regex splitter
// cannot read. Excluded from the entity oracle, not from the walkmask one.
const NO_ENTITY_ORACLE = new Set(['gg_debug']);

const out = { generated: new Date().toISOString().slice(0, 10), source: 'gg2-agent walkmask.js + navgraph.js', maps: [] };

for (const name of maps) {
  const mask = walkmask.decode(name, repo);
  const record = {
    name,
    width: mask.width,
    height: mask.height,
    solidCells: mask.bits.reduce((n, b) => n + b, 0),
    // One byte per cell, row-major, 1 where solid: Walkmask.ToCellBytes().
    cellsSha256: crypto.createHash('sha256').update(mask.bits).digest('hex'),
  };

  if (!NO_ENTITY_ORACLE.has(name)) {
    // `meta` is not an entity - it is map metadata that the reference hands to
    // loadMetadata() after the instances are made, and it carries no x/y.
    record.entities = navgraph
      .entities(name, repo)
      .filter((e) => e.type !== 'meta')
      .map((e) => ({
        type: String(e.type),
        x: Number(e.x),
        y: Number(e.y),
        xscale: e.xscale === undefined ? 1 : Number(e.xscale),
        yscale: e.yscale === undefined ? 1 : Number(e.yscale),
      }));
  }

  out.maps.push(record);
}

const dest = path.join(__dirname, '..', 'tests', 'Gg2.Tests', 'Fixtures', 'maps.json');
fs.mkdirSync(path.dirname(dest), { recursive: true });
fs.writeFileSync(dest, JSON.stringify(out, null, 2) + '\n');
console.log(`wrote ${path.relative(process.cwd(), dest)}: ${out.maps.length} maps`);
