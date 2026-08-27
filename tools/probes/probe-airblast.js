#!/usr/bin/env node
//=============================================================================
// probe-airblast.js - the three measurements the airblast is blocked on.
//
// Flamethrower.events/User Event 2.xml creates an AirBlastO 25px ahead of the
// weapon along owner.aimDirection, with image_angle = aimDirection when the
// pyro faces right and image_angle = aimDirection + 180 with image_xscale = -1
// when it faces left, then asks
//   collision_circle(x, y, r, other.poof, false, true)
// about every Rocket, Flare, Mine (r=5), Character, LooseSheet and Gib (r=25)
// - with the VICTIM's real-valued (x, y) as the centre. So:
//
// 1. AirBlastO collides as its mask, AirBlastMaskS (AirBlastO.xml declares
//    <mask>). One frame; the XML says origin (1, 14) and an AUTO RECTANGLE
//    bound, but the sentry probe showed the XML is not a substitute for asking
//    the engine (a MANUAL bound was clamped). Read box + origin live.
// 2. The box is ROTATED (and flipped for a left-facing pyro), so sample enough
//    angles at both xscales to check Aabb.ForRotatedSprite's model, including
//    a fractional angle - aimDirection is a real.
// 3. collision_circle was measured with integer centres only
//    (docs/SIMULATION.md flags the gap). The airblast centres it on character
//    x/y, which is fractional. Probe fractional centres against a known box to
//    tell "real Euclidean distance" from "centre rounded/floored first".
//
// Bonus: three flipped+rotated samples on SentryTurretS, because the turret's
// xscale=-1 inflation under rotation is an open question measured only at
// angle 0 (probe-turret.js).
//
// Needs any running instance with the agent bridge - a dedicated server with
// an empty match is enough. Do not run while an MCP session holds the bridge.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

// 1 + 2: the mask's identity and its box, straight and rotated, both xscales.
// Fractions are written as integer ratios - GM8's literal parser is one ULP
// off for some decimals (CLAUDE.md).
const BOX = `
var s, out, i, a;
out = "";
out += "maskOf(AirBlastO)=" + string(object_get_mask(AirBlastO));
out += " AirBlastMaskS=" + string(AirBlastMaskS);
out += " maskFrames=" + string(sprite_get_number(AirBlastMaskS));
out += " spriteFrames=" + string(sprite_get_number(AirBlastS));
out += " bbox=" + string(sprite_get_bbox_left(AirBlastMaskS)) + "," + string(sprite_get_bbox_top(AirBlastMaskS)) + "," + string(sprite_get_bbox_right(AirBlastMaskS)) + "," + string(sprite_get_bbox_bottom(AirBlastMaskS));
out += " origin=" + string(sprite_get_xoffset(AirBlastMaskS)) + "," + string(sprite_get_yoffset(AirBlastMaskS));
out += " size=" + string(sprite_get_width(AirBlastMaskS)) + "," + string(sprite_get_height(AirBlastMaskS));
s = instance_create(1000, 1000, Shot);
s.mask_index = AirBlastMaskS;
s.image_index = 0;
s.image_yscale = 1;
s.image_xscale = 1;
for (i = 0; i < 8; i += 1)
{
    a = i * 45;
    s.image_angle = a;
    out += " r" + string(a) + "=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
}
s.image_angle = 30;
out += " r30=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
s.image_angle = 33 + 3/4;
out += " r33p75=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
s.image_xscale = -1;
for (i = 0; i < 8; i += 1)
{
    a = i * 45;
    s.image_angle = a;
    out += " f" + string(a) + "=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
}
s.image_angle = 213 + 3/4;
out += " f213p75=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
with(s) instance_destroy();
global.airblastBox = out;
`;

// 3: fractional centres. The probe wears SentryMaskS: at (1000,1000) its box
// is exactly x 992..1007, y 993..1012 inclusive (probe-circle.js). With r=10
// the last integer hit rightward is cx=1017 and downward cy=1022.
//   real Euclidean: 1016+3/4 hits; 1017+1/4, 1017+1/2, 1017+3/4 miss.
//   floor(centre): all four hit.
//   round(centre): 1017+1/4 hits, 1017+1/2 and 1017+3/4 miss (half-to-even
//                  sends 1017.5 to 1018).
// The diagonal separates real distance from any rounding even harder:
// d=7+1/16 off the corner is 9.9879 away (hit iff real), d=7+1/8 is 10.0762
// (miss under every model that reaches the corner exactly).
const CIRCLE = `
var p, out;
out = "";
p = instance_create(1000, 1000, Shot);
p.mask_index = SentryMaskS;
p.image_index = 0;
p.image_xscale = 1;
p.image_yscale = 1;
p.image_angle = 0;
out += "probebox=" + string(p.bbox_left) + "," + string(p.bbox_top) + "," + string(p.bbox_right) + "," + string(p.bbox_bottom);
out += " x1016p75=" + string(collision_circle(1016 + 3/4, 1000, 10, Shot, false, false) >= 0);
out += " x1017p25=" + string(collision_circle(1017 + 1/4, 1000, 10, Shot, false, false) >= 0);
out += " x1017p5="  + string(collision_circle(1017 + 1/2, 1000, 10, Shot, false, false) >= 0);
out += " x1017p75=" + string(collision_circle(1017 + 3/4, 1000, 10, Shot, false, false) >= 0);
out += " y1021p75=" + string(collision_circle(1000, 1021 + 3/4, 10, Shot, false, false) >= 0);
out += " y1022p25=" + string(collision_circle(1000, 1022 + 1/4, 10, Shot, false, false) >= 0);
out += " y1022p5="  + string(collision_circle(1000, 1022 + 1/2, 10, Shot, false, false) >= 0);
out += " diag7p0625=" + string(collision_circle(1007 + 7 + 1/16, 1012 + 7 + 1/16, 10, Shot, false, false) >= 0);
out += " diag7p125="  + string(collision_circle(1007 + 7 + 1/8, 1012 + 7 + 1/8, 10, Shot, false, false) >= 0);
out += " byInstance=" + string(collision_circle(1017, 1000, 10, p, false, false) >= 0);
out += " byInstanceMiss=" + string(collision_circle(1018, 1000, 10, p, false, false) >= 0);
with(p) instance_destroy();
global.airblastCircle = out;
`;

// Bonus: the turret's flipped box under rotation (open question - measured
// only at angle 0 so far).
const TURRET = `
var s, out;
out = "";
s = instance_create(1000, 1000, Shot);
s.mask_index = SentryTurretS;
s.image_index = 0;
s.image_yscale = 1;
s.image_xscale = -1;
s.image_angle = 45;
out += "tf45=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
s.image_angle = 90;
out += " tf90=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
s.image_angle = 315;
out += " tf315=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
with(s) instance_destroy();
global.airblastTurret = out;
`;

async function main() {
  await ev(BOX);
  console.log((await evalx('global.airblastBox')).trim().replace(/ /g, '\n'));
  console.log('---');
  await ev(CIRCLE);
  console.log((await evalx('global.airblastCircle')).trim().replace(/ /g, '\n'));
  console.log('---');
  await ev(TURRET);
  console.log((await evalx('global.airblastTurret')).trim().replace(/ /g, '\n'));
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
