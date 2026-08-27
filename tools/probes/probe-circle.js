#!/usr/bin/env node
//=============================================================================
// probe-circle.js - where collision_circle's boundary actually falls.
//
// Not a sampler and not a fixture: enough to tell the next session whether
// collision_circle is a plain circle-versus-box test or another pixel walk like
// collision_line. The coarse sweep already ruled out anything exotic; this
// finds the exact last hit on two axes and on the diagonal, which is where the
// two models disagree.
//
// The probe wears SentryMaskS, whose box is exactly (-8,-7)..(7,12) about the
// origin - so at (1000,1000) it covers x 992..1007 and y 993..1012 inclusive.
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
var p, out, d, k;
out = "";
p = instance_create(1000, 1000, Shot);
p.mask_index = SentryMaskS;
p.image_index = 0;
p.image_xscale = 1;
p.image_yscale = 1;
p.image_angle = 0;
out += "box=" + string(p.bbox_left) + "," + string(p.bbox_top) + "," + string(p.bbox_right) + "," + string(p.bbox_bottom);

// Walk the centre right, one pixel at a time, radius 10. Last hit tells us
// where the right edge is taken to be.
out += " right:";
for (d = 10; d <= 24; d += 1)
{
    out += string(collision_circle(1000 + d, 1000, 10, Shot, false, false) >= 0);
}

// Same walking left.
out += " left:";
for (d = 10; d <= 24; d += 1)
{
    out += string(collision_circle(1000 - d, 1000, 10, Shot, false, false) >= 0);
}

// Down, against bbox_bottom.
out += " down:";
for (d = 10; d <= 28; d += 1)
{
    out += string(collision_circle(1000, 1000 + d, 10, Shot, false, false) >= 0);
}

// The diagonal off the bottom-right corner. A true circle-rect test and a
// pixel walk part company here.
out += " diag:";
for (d = 0; d <= 18; d += 1)
{
    out += string(collision_circle(1007 + d, 1012 + d, 10, Shot, false, false) >= 0);
}

// Radius 0 and 1 on the corner pixel itself.
out += " r0corner=" + string(collision_circle(1007, 1012, 0, Shot, false, false) >= 0);
out += " r0just=" + string(collision_circle(1008, 1013, 0, Shot, false, false) >= 0);
out += " r1just=" + string(collision_circle(1008, 1013, 1, Shot, false, false) >= 0);

with(p) instance_destroy();
global.circleFine = out;
`;

async function main() {
  await ev(GML);
  console.log((await evalx('global.circleFine')).trim().replace(/ /g, '\n'));
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
