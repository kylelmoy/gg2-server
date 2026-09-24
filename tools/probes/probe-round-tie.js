#!/usr/bin/env node
//=============================================================================
// probe-round-tie.js - which way does GM8 round a position sitting exactly on
// a half?
//
// CollisionWorld.RoundPosition is round-half-to-EVEN, and its comment says so
// as a statement of fact: "the same rule as GML's round() and .NET's default".
// Every collision the port performs at a fractional position goes through it.
//
// probe-fractional-mask.js settled the neighbouring question - a precise mask
// hangs off the ROUNDED position, not the exact one - and produced a reading
// that does not fit: at x = 3000.5 the engine's mask matched x = 3001, where
// half-to-even predicts 3000 because 3000 is even.
//
// ⚠️ Only an EVEN base can tell the two rules apart. At an odd base both
// half-to-even and half-away-from-zero round up, so 3001.5 -> 3002 either way
// and proves nothing. This asks both, and the odd one is the control: if the
// even base rounds up AND the odd base rounds up, the rule is half-away; if the
// even base rounds down, it is half-to-even and the port is right.
//
// ⚠️ The half is sent as `1/2`, never as `0.5` - CLAUDE.md's rule about GM8's
// decimal parser, and this probe is entirely about the exact value of a half.
//
// A rotated rocket is the subject because an unrotated mask fills its own box,
// which makes neighbouring integer positions indistinguishable.
//
// Needs any running instance with the agent bridge. Do not run while an MCP
// session holds the bridge.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const PROBE_Y = 3000;
const REACH = 9;
const ANGLE = 30;

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

// The grid is sampled around a FIXED origin so that two different instance
// positions are compared over the same world pixels.
function gml(xExpr, originX) {
  return `
    global.rtOut = "";
    global.rt = instance_create(${originX}, ${PROBE_Y}, Rocket);
    with(global.rt)
    {
        x = ${xExpr};
        image_angle = ${ANGLE};
        global.rtOut += string(bbox_left) + " " + string(bbox_right) + " ";
        for(global.ry = -${REACH}; global.ry <= ${REACH}; global.ry += 1)
        {
            for(global.rx = -${REACH}; global.rx <= ${REACH}; global.rx += 1)
            {
                if(collision_point(${originX} + global.rx, ${PROBE_Y} + global.ry,
                                   Rocket, true, false) > 0)
                    global.rtOut += "#"
                else
                    global.rtOut += ".";
            }
            global.rtOut += "|";
        }
        instance_destroy();
    }`;
}

async function grid(xExpr, originX) {
  await ev(gml(xExpr, originX));
  const raw = await evalx('global.rtOut');
  const parts = raw.trim().split(/\s+/);

  if (parts.length < 3) throw new Error(`"${xExpr}": bridge said "${raw.slice(0, 120)}"`);

  return { bbox: `${parts[0]}..${parts[1]}`, cells: parts[2] };
}

async function main() {
  console.log('which integer does x.5 collide as?  (rotated Rocket, angle 30)\n');

  for (const base of [3000, 3001, 3002, 3003]) {
    // All three sampled around the SAME origin, so the grids are comparable.
    const down = await grid(`${base}`, base);
    const half = await grid(`${base} + 1/2`, base);
    const up = await grid(`${base + 1}`, base);

    const verdict = half.cells === down.cells
      ? `DOWN to ${base}`
      : half.cells === up.cells
        ? `UP to ${base + 1}`
        : 'NEITHER neighbour';

    const parity = base % 2 === 0 ? 'even' : 'odd ';
    const predicted = base % 2 === 0 ? 'half-to-even says DOWN' : 'both rules say UP (control)';

    console.log(
      `  x = ${base}.5  (${parity} base)  ->  ${verdict.padEnd(18)}`
      + `${predicted}`,
    );
    console.log(
      `      bbox: at ${base} ${down.bbox} | at ${base}.5 ${half.bbox} | at ${base + 1} ${up.bbox}`,
    );
  }
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
