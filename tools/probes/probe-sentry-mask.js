#!/usr/bin/env node
//=============================================================================
// probe-sentry-mask.js - the collision box a Sentry actually presents.
//
// Two questions, one eval:
//
//  1. Does Sentry's mask_index override really apply? Sentry.xml declares
//     <mask>SentryMaskS</mask> while its *sprite* SentryRed has twelve frames
//     that grow out of a toolbox and swing 21 px above the origin at frame 8.
//     If the override applies, the hitbox is constant; if it does not, it
//     animates.
//  2. What are SentryMaskS's AUTO bounds? They are computed by GM8 at load
//     time and are therefore not a number anywhere in the reference tree.
//
// NOT by creating a Sentry: its Destroy event dereferences ownerPlayer.object
// and ownerPlayer.sentry, so a bare one throws a GM8 modal and wedges the
// bridge. A Shot wearing SentryMaskS answers the same question - the mask is a
// property of the sprite, and image_index/image_xscale are properties of the
// instance.
//
// Everything runs inside ONE eval: the game keeps stepping between calls.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

// One statement per line, joined - keeps the GML readable in the log if it
// fails to compile.
const GML = `
var s, out, i, sx;
out = "";
out += "maskOf(Sentry)=" + string(object_get_mask(Sentry));
out += " SentryMaskS=" + string(SentryMaskS);
out += " SentryRed=" + string(SentryRed);
out += " frames(SentryMaskS)=" + string(sprite_get_number(SentryMaskS));
out += " frames(SentryRed)=" + string(sprite_get_number(SentryRed));
out += " maskbbox=" + string(sprite_get_bbox_left(SentryMaskS)) + "," + string(sprite_get_bbox_top(SentryMaskS)) + "," + string(sprite_get_bbox_right(SentryMaskS)) + "," + string(sprite_get_bbox_bottom(SentryMaskS));
out += " maskorigin=" + string(sprite_get_xoffset(SentryMaskS)) + "," + string(sprite_get_yoffset(SentryMaskS));
out += " redbbox=" + string(sprite_get_bbox_left(SentryRed)) + "," + string(sprite_get_bbox_top(SentryRed)) + "," + string(sprite_get_bbox_right(SentryRed)) + "," + string(sprite_get_bbox_bottom(SentryRed));
out += " redorigin=" + string(sprite_get_xoffset(SentryRed)) + "," + string(sprite_get_yoffset(SentryRed));
s = instance_create(1000, 1000, Shot);
s.mask_index = SentryMaskS;
s.image_angle = 0;
s.image_yscale = 1;
for (i = 0; i < 12; i += 1)
{
    s.image_index = i;
    s.image_xscale = 1;
    out += " f" + string(i) + "=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
}
s.image_index = 0;
s.image_xscale = -1;
out += " flipped=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y) + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);
with(s) instance_destroy();
global.sentryProbe = out;
`;

async function main() {
  await ev(GML);
  const raw = await evalx('global.sentryProbe');
  console.log(raw.trim().replace(/ /g, '\n'));
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
