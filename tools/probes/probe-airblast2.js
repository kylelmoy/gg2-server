#!/usr/bin/env node
//=============================================================================
// probe-airblast2.js - two questions probe-airblast.js's answers opened.
//
// 1. The airblast poof is a ROTATED instance, and collision_circle has only
//    been measured against an angle-0 box. Circle-versus-reported-AABB and
//    circle-versus-rotated-rectangle part company at the AABB's empty corner:
//    a SentryTurretS probe at 45 degrees has its AABB corner ~8px away from
//    any actual sprite pixel. Centre a circle diagonally off that corner and
//    the two models give opposite answers.
//    Probe box at 45 deg (predicted from probe-airblast.js's fit):
//    (-14,-18,15,11) about (1000,1000) -> abs 986,982,1015,1011.
//    Top-right AABB corner (1015, 982) vs rotated-rect corner (~1007, ~982).
// 2. Does collision_circle see the flipped bbox_left inflation? SentryMaskS
//    flipped is predicted 991..1008 x 993..1012 (mirror of -8..7 about the
//    origin is -7..8, minus the 2px pre-rotation inflation on the mirrored
//    left). A circle at cx=981 r=10 hits iff the inflated edge (991) counts;
//    the uninflated mirror (993) needs cx>=983.
//
// Needs the same bridge as probe-airblast.js.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

// 1: circle vs a rotated instance. Read the engine's own bbox back first so
// the corner arithmetic is anchored on the truth, not on the model.
const ROTATED = `
var p, out, d;
out = "";
p = instance_create(1000, 1000, Shot);
p.mask_index = SentryTurretS;
p.image_index = 0;
p.image_xscale = 1;
p.image_yscale = 1;
p.image_angle = 45;
out += "box45=" + string(p.bbox_left) + "," + string(p.bbox_top) + "," + string(p.bbox_right) + "," + string(p.bbox_bottom);
// Diagonally off the AABB's top-right corner, r=10: hit for d<=7 iff the AABB
// is the shape (7*sqrt2=9.9<=10, 8*sqrt2=11.3>10); miss for ALL d>=0 well
// before that if the rotated rectangle is the shape (the corner region is
// ~8px of empty AABB).
out += " neCorner:";
for (d = 0; d <= 9; d += 1)
{
    out += string(collision_circle(p.bbox_right + d, p.bbox_top - d, 10, Shot, false, false) >= 0);
}
// Control on a covered edge: straight off the box's right edge at the
// instance's own y, where the rotated rect actually reaches the AABB edge.
out += " rightEdge:";
for (d = 8; d <= 12; d += 1)
{
    out += string(collision_circle(p.bbox_right + d, 1000, 10, Shot, false, false) >= 0);
}
// And straight up from the top edge above x=1000-7 (the rect's top corner is
// near x=1007 at y=bbox_top).
out += " topEdge:";
for (d = 8; d <= 12; d += 1)
{
    out += string(collision_circle(1007, p.bbox_top - d, 10, Shot, false, false) >= 0);
}
with(p) instance_destroy();
global.airblast2rot = out;
`;

// 2: circle vs the flipped inflation, plus the flipped box read directly.
const FLIPPED = `
var p, out;
out = "";
p = instance_create(1000, 1000, Shot);
p.mask_index = SentryMaskS;
p.image_index = 0;
p.image_xscale = -1;
p.image_yscale = 1;
p.image_angle = 0;
out += "flipbox=" + string(p.bbox_left) + "," + string(p.bbox_top) + "," + string(p.bbox_right) + "," + string(p.bbox_bottom);
out += " x980=" + string(collision_circle(980, 1000, 10, Shot, false, false) >= 0);
out += " x981=" + string(collision_circle(981, 1000, 10, Shot, false, false) >= 0);
out += " x982=" + string(collision_circle(982, 1000, 10, Shot, false, false) >= 0);
out += " x983=" + string(collision_circle(983, 1000, 10, Shot, false, false) >= 0);
with(p) instance_destroy();
global.airblast2flip = out;
`;

async function main() {
  await ev(ROTATED);
  console.log((await evalx('global.airblast2rot')).trim().replace(/ /g, '\n'));
  console.log('---');
  await ev(FLIPPED);
  console.log((await evalx('global.airblast2flip')).trim().replace(/ /g, '\n'));
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
