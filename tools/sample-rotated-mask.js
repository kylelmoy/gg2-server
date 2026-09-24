#!/usr/bin/env node
//=============================================================================
// sample-rotated-mask.js - what GM8's overlap primitives actually test when a
// projectile is ROTATED, which is not what this port assumed.
//
// The port models every projectile as the axis-aligned bounding box GM8 reports
// for its rotated sprite (Aabb.ForRotatedSprite, gated by rotated-box-samples
// .json over 1,008 boxes). That box is right - the engine really does report it
// - and `distance_to_object` really is computed from it, rotated or not, which
// is why splash damage has always come out exact.
//
// place_free and place_meeting are a different question, and the answer is
// different: they test the PRECISE MASK. For an unrotated sprite the two
// coincide - a projectile's mask fills its own bbox - so nothing ever noticed.
// Rotate it and the bbox becomes a strict superset, sometimes a large one: at
// image_angle 351 a Rocket's 8x3 box holds 16 opaque pixels, and its top row
// holds exactly ONE.
//
// That is worth a tool because it is worth six pixels of an explosion. A rocket
// backing out of the character it hit (really_move_contact_solid) stops as soon
// as it is free, and stopping on the box rather than on the mask carries it six
// pixels too far - which is the whole of the gg_debug damage divergence. See
// docs/TRAPS-SIMULATION.md, "`gg_debug` stops on a rocket blast four pixels
// short", and GgDebugSplashOpenFindingTests.
//
// What this records, per sprite and per angle:
//
//   box    the bbox GM8 reports, relative to the instance's position
//   mask   collision_point(..., precise) over a grid covering that box
//
// collision_point with precise=true is the only primitive that answers about a
// single pixel, so it is the one that can rasterise a mask rather than infer it
// from where a second box happens to stop overlapping.
//
// ⚠️ Every probe is created AND destroyed inside one gg2_eval. A free-standing
// Rocket left alive for even one frame throws from its own Step, Draw and
// Collision events (`owner`, `team`, `ownerPlayer` are all unset) and the bot
// layer's botRocketDodge walks `with(Rocket)` as well - a wall of GML errors
// that makes the next reading untrustworthy.
//
// ⚠️ Do NOT freeze the game for this. gg2_step empties the roster for the
// frozen frames - measured, `instance_number(Character)` reads 0 - so a probe
// that pins a character while frozen measures nothing at all. Every batch
// re-places what it needs instead, like sample-primitives.js.
//
// Requires a running dedicated server with the agent bridge. Unlike
// sample-primitives.js it does NOT need a bot: every probe is a projectile it
// creates itself.
//
//   node tools/sample-rotated-mask.js [--out <file>]
//=============================================================================

const fs = require('fs');
const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/rotated-mask-samples.json');

// An exact integer position, so the bbox rounding is not part of what is
// measured. docs/SIMULATION.md, "place_free rounds the position".
const ORIGIN_X = 3000;
const ORIGIN_Y = 600;

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function batch(body) {
  await ev('global.maskOut = ""; ' + body);
  return evalx('global.maskOut');
}

// The nine projectile objects Gg2.Simulation.ProjectileSprites names, with the
// half-width of the grid each one needs. The grid has to cover the sprite's
// longest half-diagonal at every angle, not just its axis-aligned box: a Shot
// is 18x2 flat and 13x13 at 45 degrees.
//
// ⚠️ The GML object names are not the port's enum names. BladeB is the blade in
// flight (Blade is the weapon) and StabMask is the spy's knife.
//
// ⚠️ `prime` exists because three of these cannot be DESTROYED free-standing.
// A Mine's Destroy event is `owner.currentWeapon.lobbed -= 1`, a BladeB's
// refunds ammo through the same chain, and a Bubble's is `gun.bubbleCount -= 1`
// - so a probe that was not created by a real weapon throws on cleanup, and the
// error wall makes every later reading untrustworthy. sample-primitives.js hit
// this and dropped the Mine rather than solving it.
//
// ⚠️ Read those Destroy events out of the RAW XML, not through a grep that
// strips tag lines. CLAUDE.md's "when an event looks like it is missing its
// first statement, read the raw XML" caught this tool too: BladeB's real first
// line is `owner.currentWeapon.bladesOut -= 1;`, and it sits on the same line
// as the opening <argument kind="STRING"> tag, so a first pass primed every
// variable except that one and failed on it alone.
//
// Pointing `owner` and `currentWeapon` at the probe ITSELF closes it: GML
// resolves `owner.currentWeapon.lobbed` to the instance's own `lobbed`, which
// this then supplies. The Destroy event decrements a number nobody reads and
// nothing else in it touches the world. The mask is read before the destroy in
// any case, so even a misbehaving cleanup could not corrupt a sample - what it
// would corrupt is the NEXT one.
const PROBES = [
  { name: 'Rocket', object: 'Rocket', reach: 10 },
  { name: 'Shot', object: 'Shot', reach: 24 },
  { name: 'Needle', object: 'Needle', reach: 24 },
  { name: 'Flare', object: 'Flare', reach: 8 },
  { name: 'Mine', object: 'Mine', reach: 8, prime: 'owner = id; currentWeapon = id; lobbed = 1;' },
  { name: 'Flame', object: 'Flame', reach: 8 },
  {
    name: 'Blade',
    object: 'BladeB',
    reach: 16,
    prime: 'owner = id; currentWeapon = id; bladesOut = 1; refireTime = 0; '
         + 'ammoCount = 0; ammoDrained = 0;',
  },
  // ⚠️ `owner` as well as `gun`: the guard is `if(instance_exists(owner))`, and
  // an unset owner throws on the guard rather than on the line it protects.
  // A Bubble's Destroy also leaves a `Pop` behind, which is a draw effect that
  // removes itself when its animation ends.
  { name: 'Bubble', object: 'Bubble', reach: 12, prime: 'owner = id; gun = id; bubbleCount = 1;' },
  { name: 'Stab', object: 'StabMask', reach: 48 },
];

// Angles. Zero and the four axes are the cases where the mask and the box agree
// and a regression would be invisible; the rest are spread so that no two share
// a sine or cosine. 351.002197265625 is not decoration - it is the exact angle
// of the rocket in the gg_debug divergence, which is a wire value (a 16-bit
// netAimDirection) and therefore reproducible.
const ANGLES = [
  0, 9, 30, 45, 60, 90, 120, 135, 150, 180,
  210, 225, 240, 270, 300, 315, 330, 351.002197265625,
];

// One angle of one sprite: the reported box, then the mask rasterised over a
// grid that covers it.
//
// The grid is emitted as one '#'/'.' string per row, rows separated by '|', so
// the reply is compact and reads as a picture in the fixture.
//
// ⚠️ The probe is created and destroyed inside this one string. See the header:
// a projectile that survives a frame throws from its own events.
function maskGml(probe, angle) {
  const { object, reach } = probe;

  return `
    global.mp = instance_create(${ORIGIN_X}, ${ORIGIN_Y}, ${object});
    with(global.mp)
    {
        ${probe.prime ?? ''}
        image_angle = ${angle};
        global.maskOut += string(bbox_left - x) + " " + string(bbox_top - y) + " "
            + string(bbox_right - x) + " " + string(bbox_bottom - y) + " ";
        for(global.my = -${reach}; global.my <= ${reach}; global.my += 1)
        {
            for(global.mx = -${reach}; global.mx <= ${reach}; global.mx += 1)
            {
                if(collision_point(${ORIGIN_X} + global.mx, ${ORIGIN_Y} + global.my,
                                   ${object}, true, false) > 0)
                    global.maskOut += "#"
                else
                    global.maskOut += ".";
            }
            global.maskOut += "|";
        }
        instance_destroy();
    }`;
}

// ⚠️ One batch per angle, not one per sprite. A Stab's grid is 97x97, and
// eighteen of those in a single reply is over 160 KB - past what the bridge
// hands back in one read, and it truncates rather than failing.
async function maskSamples(probe) {
  const rows = [];

  for (const angle of ANGLES) {
    const raw = await batch(maskGml(probe, angle));
    const parts = raw.trim().split(/\s+/);

    if (parts.length < 5)
      throw new Error(`${probe.name} at ${angle}: bridge returned "${raw.slice(0, 120)}"`);

    const mask = parts[4].split('|').filter(Boolean);
    const side = probe.reach * 2 + 1;

    // A truncated reply is the failure mode this tool has to refuse loudly:
    // a short grid still parses and would be checked in as a real measurement.
    if (mask.length !== side || mask.some((row) => row.length !== side))
      throw new Error(`${probe.name} at ${angle}: grid is not ${side}x${side}`);

    rows.push({
      angle,
      box: {
        left: Number(parts[0]),
        top: Number(parts[1]),
        right: Number(parts[2]),
        bottom: Number(parts[3]),
      },
      reach: probe.reach,
      mask,
    });

    process.stdout.write('.');
  }

  return rows;
}

async function main() {
  const out = {
    generated: new Date().toISOString().slice(0, 10),
    origin: { x: ORIGIN_X, y: ORIGIN_Y },
    note: 'collision_point(..., precise=true) over a grid, per sprite per angle. '
        + 'The bbox is what GM8 reports; the mask is what place_free tests.',
    sprites: {},
  };

  for (const probe of PROBES) {
    process.stdout.write(`${probe.name}: `);
    out.sprites[probe.name] = await maskSamples(probe);
    process.stdout.write('\n');
  }

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 1) + '\n');

  const total = Object.values(out.sprites).reduce((n, rows) => n + rows.length, 0);
  console.log(`wrote ${OUT}: ${total} masks`);
}

main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
