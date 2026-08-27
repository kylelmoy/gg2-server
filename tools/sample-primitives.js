#!/usr/bin/env node
//=============================================================================
// sample-primitives.js - the GM8 collision primitives phase 3 deliberately did
// not write, measured against the running game.
//
// Phase 3 pinned place_free and stopped there, because nothing before combat
// needed more. Combat needs three more, and each of them decides a number a
// client can see:
//
//   distance_to_object   every splash radius test in Rocket's User Event 5,
//                        and the damage falloff computed from it
//   place_meeting        whether a projectile is touching a character
//   collision_line       the Rifle's hitscan binary search, and
//                        collision_line_bulletblocking on every rocket fired
//
// This tool measures the first two. collision_line is not here yet - see the
// note at the bottom of this file for what it needs.
//
// Everything is driven from exact integer positions so the answers are exact
// integers: GM8 rounds an instance's position to build its bounding box, and a
// probe run from fractional coordinates measures the rounding as well as the
// primitive. Phase 3 settled the rounding (docs/SIMULATION.md, "place_free
// rounds the position"); this tool takes it as known and asks a different
// question.
//
// Requires a running dedicated server with the agent bridge and at least one
// bot character - see CLAUDE.md's bots recipe.
//
//   node tools/sample-primitives.js [--out <file>]
//=============================================================================

const fs = require('fs');
const path = require('path');

// The exact-double encoder lives in tools/exact-double.js, which is one
// copy of what used to be four - and three of those four were wrong. Run
// `node tools/exact-double.js` to self-test it; it needs no game.
const exact = require(path.join(__dirname, 'exact-double.js'));


const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/primitive-samples.json');

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function batch(body) {
  await ev('global.primOut = ""; ' + body);
  return (await evalx('global.primOut')).trim();
}


// The exact-double encoder from sample-physics.js. string_format() is good to
// about sixteen significant digits, which is not good enough for a fixture this
// repo compares with ==: distance_to_object is a square root of an integer, so
// both sides compute the same thing and agree to the last bit, and a fixture
// that can only express six decimals cannot say so. Every step below is a
// power-of-two scale or a floor, and every chunk stays under 2^19 because GM8's
// floor() truncates through a 32-bit integer.
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
// Where to put the probes
//---------------------------------------------------------------------------

// Somewhere with nothing in it. The character is teleported here and put back
// afterwards; the point is only that two instances can be placed at chosen
// coordinates without a wall deciding the answer for us.
const ORIGIN_X = 3000;
const ORIGIN_Y = 600;

// Offsets in both axes, in pixels. Deliberately not a uniform grid: the
// interesting answers are at zero separation, at one pixel of separation, and
// where the boxes exactly touch - the places an inclusive-vs-exclusive mistake
// changes the number and nowhere else does.
const OFFSETS = [-64, -33, -32, -31, -20, -13, -12, -11, -1, 0, 1, 11, 12, 13, 20, 31, 32, 33, 64];

// The rocket carries the splash, the shot carries the bullets. Both can be
// created and destroyed free-standing, which is what a probe needs.
//
// Mine is deliberately absent. Its Destroy event is
// `owner.currentWeapon.lobbed -= 1`, so a mine that was not created by a
// demoman's Minegun cannot be cleaned up without raising a GML error - the
// probe would have to build a whole demoman to ask a question about a
// rectangle. Its geometry can be sampled when its lifecycle is modelled.
const PROJECTILES = ['Rocket', 'Shot'];

//---------------------------------------------------------------------------

// GML that pins the probe character to the origin, run at the top of every
// batch.
//
// **This is not belt and braces, and leaving it out is how this tool was wrong
// the first time.** The character is a live bot: the game keeps stepping between
// gg2_eval calls, so a character placed once and measured across nineteen calls
// is measured nineteen times in nineteen different places, and the numbers that
// come back describe the bot's walk rather than the primitive. Every batch
// re-places it, and every batch records the box it actually got - so a row in
// the fixture is a statement about two rectangles it names, not about a position
// this tool assumed. (CLAUDE.md, "The game keeps stepping between gg2_eval
// calls".)
const PIN_CHARACTER = `
    global.pc = instance_find(Character, 0);
    with(global.pc)
    {
        x = ${ORIGIN_X};
        y = ${ORIGIN_Y};
        hspeed = 0;
        vspeed = 0;
        global.primOut += string(bbox_left) + " " + string(bbox_top) + " "
            + string(bbox_right) + " " + string(bbox_bottom) + " ; ";
    }`;

// Splits the leading "char box ;" cell off a batch's output.
function takeCharacterBox(text) {
  const [head, ...rest] = text.split(';');
  const [left, top, right, bottom] = head.trim().split(/\s+/).map(Number);

  if (!Number.isFinite(left))
    throw new Error('no Character in the running game - are bots enabled?');

  return { box: { left, top, right, bottom }, rest: rest.join(';') };
}

// Places the probe character on an exact integer coordinate and reports the
// bounding box the engine gives it, so the fixture records the geometry rather
// than assuming the class table.
async function placeCharacter() {
  const raw = await batch(`
    ${PIN_CHARACTER}
    with(global.pc) global.primOut += object_get_name(object_index);`);

  const { box, rest } = takeCharacterBox(raw);
  return { class: rest.trim(), ...box };
}

// distance_to_object, sampled over a 2-D grid of offsets. Each row records the
// projectile's own bounding box as the engine reports it, so the fixture is a
// statement about two rectangles rather than about a sprite this repo cannot
// see.
async function distanceSamples(projectile) {
  const rows = [];

  for (const dy of OFFSETS) {
    const raw = await batch(`
      ${PIN_CHARACTER}
      global.pr = instance_create(0, 0, ${projectile});
      ${OFFSETS.map((dx) => `
      with(global.pr) { x = ${ORIGIN_X + dx}; y = ${ORIGIN_Y + dy}; }
      with(global.pr)
          global.primOut += string(bbox_left) + " " + string(bbox_top) + " "
              + string(bbox_right) + " " + string(bbox_bottom) + " ";
      with(global.pc)
      {
          ${exact.appendInto('global.primOut', 'distance_to_object(global.pr)')}
          global.primOut += " ; ";
      }`).join('')}
      with(global.pr) instance_destroy();`);

    const { box, rest } = takeCharacterBox(raw);
    const cells = rest.split(';').map((s) => s.trim()).filter(Boolean);

    cells.forEach((cell, i) => {
      const parts = cell.split(/\s+/);
      const [left, top, right, bottom] = parts.slice(0, 4).map(Number);
      rows.push({
        dx: OFFSETS[i],
        dy,
        character: box,
        left,
        top,
        right,
        bottom,
        distance: exact.decode(parts.slice(4).join(' ')),
      });
    });

    process.stdout.write('.');
  }

  return rows;
}

// place_meeting(x, y, Character) asked from the projectile's side, which is the
// direction the collision events run in.
async function meetingSamples(projectile) {
  const rows = [];

  for (const dy of OFFSETS) {
    const raw = await batch(`
      ${PIN_CHARACTER}
      global.pr = instance_create(0, 0, ${projectile});
      ${OFFSETS.map((dx) => `
      with(global.pr)
      {
          x = ${ORIGIN_X + dx};
          y = ${ORIGIN_Y + dy};
          global.primOut += string(bbox_left) + " " + string(bbox_top) + " "
              + string(bbox_right) + " " + string(bbox_bottom) + " "
              + string(place_meeting(${ORIGIN_X + dx}, ${ORIGIN_Y + dy}, Character)) + " ; ";
      }`).join('')}
      with(global.pr) instance_destroy();`);

    const { box, rest } = takeCharacterBox(raw);
    const cells = rest.split(';').map((s) => s.trim()).filter(Boolean);

    cells.forEach((cell, i) => {
      const [left, top, right, bottom, meeting] = cell.split(/\s+/).map(Number);
      rows.push({ dx: OFFSETS[i], dy, character: box, left, top, right, bottom, meeting: meeting !== 0 });
    });

    process.stdout.write('.');
  }

  return rows;
}

async function main() {
  const character = await placeCharacter();
  console.log(`probe character: ${character.class} ` +
              `[${character.left},${character.top} .. ${character.right},${character.bottom}]`);

  const out = {
    generated: new Date().toISOString().slice(0, 10),
    origin: { x: ORIGIN_X, y: ORIGIN_Y },
    character,
    distance: {},
    meeting: {},
  };

  for (const projectile of PROJECTILES) {
    process.stdout.write(`${projectile} distance_to_object: `);
    out.distance[projectile] = await distanceSamples(projectile);
    process.stdout.write(`\n${projectile} place_meeting:     `);
    out.meeting[projectile] = await meetingSamples(projectile);
    process.stdout.write('\n');
  }

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 1) + '\n');

  const total = PROJECTILES.reduce((n, p) => n + out.distance[p].length + out.meeting[p].length, 0);
  console.log(`wrote ${OUT}: ${total} samples`);
}

//=============================================================================
// collision_line is measured, but not here: tools/sample-collision-line.js.
//
// It needed a different shape of probe. distance_to_object and place_meeting
// are formulas over two rectangles, so a grid of offsets settles them; GM8's
// collision_line clips the segment, rounds the clipped endpoints and then walks
// whole pixels, so what it needs is a *line* swept across a box at sub-pixel
// offsets, and boxes at both coordinate parities. It also does not need a bot,
// because its probe is a BulletWall it creates and scales itself.
//=============================================================================

main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
