#!/usr/bin/env node
//=============================================================================
// probe-turret.js - the SentryTurret's box, and a first look at
// collision_circle.
//
// SentryTurretS has MANUAL mask bounds in its XML, but the Sentry probe just
// showed GM8 clamps a manual bound to the image (SentryRed's XML says bottom 40
// on a 40-tall image and GM8 reports 39), so read them from the engine.
//
// The turret is drawn with image_angle = direction, so its box is the rotated
// one - measured at a few angles against Aabb.ForRotatedSprite's model.
//
// collision_circle is wanted in three places (the airblast, the sentry build
// precondition, the sentry self-destruct guard) and has never been measured.
// This is not a sampler, just enough to say whether it behaves like a
// circle-versus-bbox test at prec=false.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

const GML = `
var s, out, i, a, r;
out = "";
out += "maskOf(SentryTurret)=" + string(object_get_mask(SentryTurret));
out += " SentryTurretS=" + string(SentryTurretS);
out += " frames=" + string(sprite_get_number(SentryTurretS));
out += " bbox=" + string(sprite_get_bbox_left(SentryTurretS)) + "," + string(sprite_get_bbox_top(SentryTurretS)) + "," + string(sprite_get_bbox_right(SentryTurretS)) + "," + string(sprite_get_bbox_bottom(SentryTurretS));
out += " origin=" + string(sprite_get_xoffset(SentryTurretS)) + "," + string(sprite_get_yoffset(SentryTurretS));
s = instance_create(1000, 1000, Shot);
s.mask_index = SentryTurretS;
s.image_index = 0;
s.image_yscale = 1;
s.image_xscale = 1;
for (i = 0; i < 5; i += 1)
{
    a = i * 45;
    s.image_angle = a;
    out += " a" + string(a) + "=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
}
s.image_angle = 0;
s.image_xscale = -1;
out += " flipped=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
with(s) instance_destroy();
global.turretProbe = out;
`;

// collision_circle against one probe whose box is known exactly, so the answer
// can be compared with both models: circle-vs-box and circle-vs-centre.
const CIRCLE = `
var p, out, d;
out = "";
p = instance_create(1000, 1000, Shot);
p.mask_index = SentryMaskS;
p.image_index = 0;
p.image_xscale = 1;
p.image_yscale = 1;
p.image_angle = 0;
out += "probebox=" + string(p.bbox_left - p.x) + "," + string(p.bbox_top - p.y) + "," + string(p.bbox_right - p.x) + "," + string(p.bbox_bottom - p.y);
for (d = 0; d <= 40; d += 4)
{
    out += " r10at" + string(d) + "=" + string(collision_circle(1000 + d, 1000, 10, Shot, false, false) >= 0);
}
for (d = 0; d <= 40; d += 4)
{
    out += " precAt" + string(d) + "=" + string(collision_circle(1000 + d, 1000, 10, Shot, true, false) >= 0);
}
with(p) instance_destroy();
global.circleProbe = out;
`;

async function main() {
  await ev(GML);
  console.log((await evalx('global.turretProbe')).trim().replace(/ /g, '\n'));
  console.log('---');
  await ev(CIRCLE);
  console.log((await evalx('global.circleProbe')).trim().replace(/ /g, '\n'));
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
