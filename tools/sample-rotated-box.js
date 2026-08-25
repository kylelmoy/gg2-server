#!/usr/bin/env node
//=============================================================================
// sample-rotated-box.js - the bounding box GM8 gives a rotated sprite.
//
// Phase 4 never needed this: a character's image_angle is always 0, so its box
// is the sprite's own bbox offset by the origin, which is what
// Gg2.Simulation.SolidRect already does. Every projectile is different.
// createShot.gml sets `image_angle = dir` on whatever it creates, and GM8
// recomputes the axis-aligned bounding box of the *rotated* sprite - so a
// scattergun pellet fired at 45 degrees has a 13x13 box where the same pellet
// fired flat has an 18x2 one.
//
// That box is what every hit is decided against, so it has to be measured
// rather than assumed. The model this checks:
//
//     take the sprite's own bbox corners, relative to the origin and scaled
//     rotate each about the origin by image_angle
//         x' =  dx*cos(a) + dy*sin(a)
//         y' = -dx*sin(a) + dy*cos(a)
//     take min/max of the four
//     round each half to EVEN - the same rounding as SolidRect.FromEntity
//
// ⚠️ It is exact except at the angles where a corner lands on a precise half
// pixel. There, the x87 gap documented in docs/SIMULATION.md ("the four motion
// variables") decides a rounding tie, and the box comes out one pixel wider or
// narrower than the double-precision model says. Measured at 2 angles in 144 -
// 210 and 330 degrees, where sin is analytically exactly -0.5. Those are the
// only angles it can happen at, and a pellet never has one: the scattergun adds
// `random(15)-7.5` to every shot's direction, so an exact 210.000 has
// probability zero. The fixture records the engine's answer either way, and the
// test allows a one-pixel disagreement ONLY on rows the model itself flags as
// a tie.
//
// Requires a running game with the agent bridge. No bots and no players needed.
//
//   node tools/sample-rotated-box.js [--out <file>] [--dry]
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

const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/rotated-box-samples.json');

// Every projectile createShot can be handed, plus the two it cannot but that
// still rotate. Each is created, measured and destroyed inside one eval.
const OBJECTS = ['Shot', 'Rocket', 'Needle', 'Flare', 'Mine', 'Flame', 'BladeB'];

// Tenths of a degree, so the sweep can be sent as an integer ratio: GM8's
// decimal parser is one ULP off for some literals (docs/SIMULATION.md).
const STEP = 25;
const SWEEP_END = 3600;

// The half-integer angles, which is where a rounding tie can be decided
// differently by the engine's x87 trig than by IEEE doubles. Included on
// purpose - a sweep that stepped over them would report a clean pass and hide
// the one case that is not.
const TIE_ANGLES = [300, 1500, 2100, 3300];

const ANGLES = (() => {
  const out = new Set();
  for (let a = 0; a < SWEEP_END; a += STEP) out.add(a);
  for (const a of TIE_ANGLES) out.add(a);
  return [...out].sort((x, y) => x - y);
})();

// Angles per eval. Each one appends four integers, so this is bounded by the
// size of the GML source.
const BATCH = 40;

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

// The probe is created fresh in every batch and destroyed at the end of it.
// A projectile left alive would fly away, collide with somebody and be gone
// mid-run - and a destroyed probe answers with whatever the bridge's own scope
// holds rather than with an error. Speed is zeroed for the same reason.
function batchGml(object, angles) {
  const measure = angles
    .map(
      (a) => `image_angle = (${a})/10;
        global.rbOut += string(bbox_left - x) + " " + string(bbox_top - y) + " "
            + string(bbox_right - x) + " " + string(bbox_bottom - y) + " | ";`,
    )
    .join('\n        ');

  return `global.rbOut = "";
    global.rbProbe = instance_create(100, 100, ${object});
    with (global.rbProbe)
    {
        x = 100; y = 100; speed = 0; image_angle = 0;

        // A projectile with no owner cannot be destroyed: Mine's Destroy event
        // decrements owner.currentWeapon.lobbed and dies on an unknown
        // variable, which the bridge reports as an empty reply rather than as
        // an error against the batch. Pointing all three at the probe itself
        // makes that line resolve and decrement a field nobody reads. Harmless
        // for the projectiles that use none of them.
        owner = id; ownerPlayer = id; currentWeapon = id;
        lobbed = 0; bladesOut = 0; ammoDrained = 0; refireTime = 0; ammoCount = 0;
        global.rbOut += string(sprite_get_bbox_left(sprite_index)) + " "
            + string(sprite_get_bbox_top(sprite_index)) + " "
            + string(sprite_get_bbox_right(sprite_index)) + " "
            + string(sprite_get_bbox_bottom(sprite_index)) + " "
            + string(sprite_get_xoffset(sprite_index)) + " "
            + string(sprite_get_yoffset(sprite_index)) + " "
            + string(image_xscale) + " " + string(image_yscale) + " ; ";
        ${measure}
        instance_destroy();
    }`;
}

async function sampleObject(object) {
  const rows = [];
  let sprite = null;

  for (let i = 0; i < ANGLES.length; i += BATCH) {
    const chunk = ANGLES.slice(i, i + BATCH);
    await ev(batchGml(object, chunk));
    const raw = (await evalx('global.rbOut')).trim();

    const [head, ...rest] = raw.split(';');
    const s = head.trim().split(/\s+/).map(Number);
    const got = {
      bboxLeft: s[0], bboxTop: s[1], bboxRight: s[2], bboxBottom: s[3],
      xOrigin: s[4], yOrigin: s[5], xScale: s[6], yScale: s[7],
    };

    // The sprite is read back in every batch and compared, because a probe
    // destroyed and recreated between batches could in principle come back
    // different - and a silently different sprite is a whole fixture of
    // plausible, wrong numbers.
    if (sprite === null) sprite = got;
    else if (JSON.stringify(sprite) !== JSON.stringify(got))
      throw new Error(`${object}: sprite changed mid-run: ${JSON.stringify(sprite)} -> ${JSON.stringify(got)}`);

    const boxes = rest.join(';').split('|').map((t) => t.trim()).filter((t) => t);
    if (boxes.length !== chunk.length)
      throw new Error(`${object}: expected ${chunk.length} boxes, got ${boxes.length}`);

    chunk.forEach((a, j) => {
      const b = boxes[j].split(/\s+/).map(Number);
      rows.push({ angle: [a, 10], left: b[0], top: b[1], right: b[2], bottom: b[3] });
    });

    process.stdout.write('.');
  }

  return { object, sprite, rows };
}

async function main() {
  if (DRY) {
    console.log(`${OBJECTS.length} objects x ${ANGLES.length} angles`);
    console.log(`${OBJECTS.length * Math.ceil(ANGLES.length / BATCH)} evals`);
    console.log('\nfirst batch:\n' + batchGml(OBJECTS[0], ANGLES.slice(0, 2)));
    return;
  }

  const objects = [];
  for (const object of OBJECTS) {
    process.stdout.write(`${object}: `);
    objects.push(await sampleObject(object));
    process.stdout.write('\n');
  }

  const out = { generated: new Date().toISOString().slice(0, 10), objects };
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 1) + '\n');

  const total = objects.reduce((n, o) => n + o.rows.length, 0);
  console.log(`wrote ${OUT}: ${objects.length} objects, ${total} boxes`);
}

main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
