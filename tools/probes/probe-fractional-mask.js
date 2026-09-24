#!/usr/bin/env node
//=============================================================================
// probe-fractional-mask.js - does a precise mask hang off the instance's exact
// position, or off the rounded one?
//
// The open question left by sample-rotated-mask.js, which is recorded in the
// handoff and in RotatedMaskSampleTests: every sample it takes is at an EXACT
// INTEGER position, deliberately, "so the bbox rounding is not part of what is
// measured". That makes the fixture clean and leaves the anchor unmeasured.
//
// ProjectileMask assumes the ROUNDED one - it anchors on
// CollisionWorld.RoundPosition, because that is what Aabb.ForRotatedSprite does
// and docs/SIMULATION.md records that place_free rounds the position. Nothing
// has ever asked the engine about the MASK, and a bbox and a precise mask are
// answered by different code in GM8.
//
// It matters because projectiles live at fractional positions permanently: a
// rocket's x moves by hspeed every tick and is essentially never an integer. If
// the mask grid follows the exact position instead, every mask test the port
// does on a moving projectile is up to half a pixel out - the same order as the
// six pixels that were the whole gg_debug damage divergence.
//
// The discriminator, and it needs no model of either answer:
//
//   Rasterise the mask at x = X + f for f in {0, 0.25, 0.5, 0.75}, one pixel
//   apart in the rounding sense. If the mask is anchored on round(x), the
//   pattern at f=0 and f=0.25 are IDENTICAL and the pattern at f=0.75 equals
//   the f=0 pattern shifted one pixel right. If it follows the exact position,
//   f=0.25 already differs from f=0 - a mask boundary lands between them.
//
// ⚠️ f=0.5 is deliberately included and deliberately NOT used as evidence:
// GML's round() is half-to-even, so 0.5 is the tie and tells you about the
// rounding rule rather than about the anchor. It is recorded to be looked at,
// not asserted on.
//
// A rotated angle is used because an unrotated sprite's mask fills its own box,
// so the two models agree by construction and the probe would measure nothing.
//
// Needs any running instance with the agent bridge - a dedicated server with an
// empty match is enough. Do not run while an MCP session holds the bridge.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const ORIGIN_X = 3000;
const ORIGIN_Y = 3000;
const REACH = 9;

const OFFSETS = [0, 0.25, 0.5, 0.75];
const ANGLE = 30;

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

// ⚠️ The offset is sent as a ratio, never as a decimal literal: CLAUDE.md's
// standing rule, because GM8's parser is a ULP off for some decimals and this
// probe is entirely about sub-pixel placement.
function ratio(f) {
  if (f === 0) return '0';
  return `${f * 4}/4`;
}

function gml(offset) {
  return `
    global.fmOut = "";
    global.fm = instance_create(${ORIGIN_X}, ${ORIGIN_Y}, Rocket);
    with(global.fm)
    {
        x = ${ORIGIN_X} + ${ratio(offset)};
        image_angle = ${ANGLE};
        global.fmOut += string(bbox_left - ${ORIGIN_X}) + " "
            + string(bbox_right - ${ORIGIN_X}) + " ";
        for(global.fy = -${REACH}; global.fy <= ${REACH}; global.fy += 1)
        {
            for(global.fx = -${REACH}; global.fx <= ${REACH}; global.fx += 1)
            {
                if(collision_point(${ORIGIN_X} + global.fx, ${ORIGIN_Y} + global.fy,
                                   Rocket, true, false) > 0)
                    global.fmOut += "#"
                else
                    global.fmOut += ".";
            }
            global.fmOut += "|";
        }
        instance_destroy();
    }`;
}

function shiftRight(rows) {
  return rows.map((row) => `.${row.slice(0, -1)}`);
}

async function main() {
  const results = [];

  for (const offset of OFFSETS) {
    await ev(gml(offset));
    const raw = await evalx('global.fmOut');
    const parts = raw.trim().split(/\s+/);

    if (parts.length < 3) throw new Error(`offset ${offset}: bridge said "${raw.slice(0, 120)}"`);

    const rows = parts[2].split('|').filter(Boolean);
    const side = REACH * 2 + 1;

    if (rows.length !== side || rows.some((r) => r.length !== side))
      throw new Error(`offset ${offset}: grid is not ${side}x${side}`);

    results.push({ offset, bboxLeft: Number(parts[0]), bboxRight: Number(parts[1]), rows });
  }

  const at = (f) => results.find((r) => r.offset === f);
  const same = (a, b) => a.rows.join('|') === b.rows.join('|');

  console.log(`angle ${ANGLE}, Rocket, ${REACH * 2 + 1}x${REACH * 2 + 1} grid\n`);

  for (const r of results)
    console.log(`  offset ${r.offset}: bbox x ${r.bboxLeft}..${r.bboxRight}`);

  console.log('');
  console.log(`  f=0.25 identical to f=0 ?  ${same(at(0.25), at(0))}`);
  console.log(`  f=0.75 identical to f=0 ?  ${same(at(0.75), at(0))}`);
  console.log(
    `  f=0.75 equals f=0 shifted right ?  `
      + `${at(0.75).rows.join('|') === shiftRight(at(0).rows).join('|')}`,
  );
  console.log(`  f=0.5  identical to f=0 ?  ${same(at(0.5), at(0))}   (the tie; not evidence)`);

  console.log('\nf=0 vs f=0.25 vs f=0.75, row by row:');

  for (let i = 0; i < at(0).rows.length; i++)
    console.log(`  ${at(0).rows[i]}   ${at(0.25).rows[i]}   ${at(0.75).rows[i]}`);
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
