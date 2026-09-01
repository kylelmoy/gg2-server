#!/usr/bin/env node
//=============================================================================
// probe-platform-mask.js - what does GM8 actually think a map-supplied sprite's
// collision mask is?
//
// A MovingPlatform has no sprite of its own; stringToResource.gml loads one out
// of the map's metadata with
//
//     sprite_add(name, 1, 1, 0, 0, 0)
//
// whose third argument is REMOVEBACK, not precise (CLAUDE.md). This port decodes
// those files itself - SpriteMask, PngSprite, GifSprite - because the mask
// decides where a character can stand on a platform, and no image library models
// GM8's removeback.
//
// Everything about that decode is currently self-consistent and unverified. The
// riskiest part by far:
//
//   ⚠️ removeback is documented to make the colour of the BOTTOM-LEFT pixel
//   transparent. That is from Game Maker's manual, not from a measurement, and
//   it decides the shape of 18 of the 31 sprites in the map archive - the ones
//   with no alpha channel at all, where removeback is the ONLY thing carving
//   them.
//
// The discriminator needs no model of either answer: rasterise the engine's own
// mask with collision_point(prec = 1) and compare it pixel for pixel against
// what this port produced. sprite-eleplat.png is the sharp instrument - 10x2,
// fully opaque, and the port has exactly half of it carved away by the corner
// rule alone. If GM8 samples a different corner, that fixture disagrees first.
//
// Also captured, because they are free once the sprite is loaded:
//   - sprite_get_number, which says whether GM8 expanded an animated GIF into
//     several subimages. If it did, "which frame collides" becomes a real
//     question and animationspeed stops being decoration.
//   - the same grid at image_xscale/yscale = 3, which is how real maps place
//     these (scale runs 1, 2, 3 and 6) and checks the floor-division mapping.
//
// NOT captured, deliberately: the anchor question. probe-fractional-mask.js
// already settled that a precise mask hangs off the ROUNDED position, and
// PreciseCollision is built on CollisionWorld.RoundPosition because of it.
//
// ⚠️ The sprites are loaded by ABSOLUTE PATH out of this repo's test fixtures.
// Nothing is copied into the reference tree - CLAUDE.md says never to modify it,
// and Source/build is still part of it.
//
// Writes tests/Gg2.Tests/Fixtures/sprite-mask-samples.json, which
// SpriteMaskOracleTests compares against. Needs any running instance with the
// agent bridge - a dedicated server with an empty match is enough. Do not run
// while an MCP session holds the bridge.
//=============================================================================

const path = require('path');
const fs = require('fs');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const REPO = path.resolve(__dirname, '..', '..');
const FIXTURES = path.join(REPO, 'tests', 'Gg2.Tests', 'Fixtures');

// Far from anything the map places, so nothing else is under the probe.
const ORIGIN_X = 3000;
const ORIGIN_Y = 3000;

const SPRITES = [
  { file: 'sprite-eleplat.png', width: 10, height: 2 },
  { file: 'sprite-ji.png', width: 46, height: 5 },
  { file: 'sprite-bubble1.gif', width: 40, height: 40 },
];

const SCALES = [1, 3];

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

// GML string literals do not treat backslash as an escape, so a Windows path
// goes in verbatim - but forward slashes work too and read better here.
function gml(file, width, height, scale) {
  const full = path.join(FIXTURES, file).replace(/\\/g, '/');

  return `
    global.pmOut = "";
    global.pmS = sprite_add("${full}", 1, 1, 0, 0, 0);
    if(global.pmS < 0)
    {
        global.pmOut = "LOADFAILED";
    }
    else
    {
        global.pmO = instance_create(${ORIGIN_X}, ${ORIGIN_Y}, SpriteObject);
        with(global.pmO)
        {
            sprite_index = global.pmS;
            image_index = 0;
            image_speed = 0;
            image_xscale = ${scale};
            image_yscale = ${scale};
        }
        global.pmOut += string(sprite_get_width(global.pmS)) + " "
            + string(sprite_get_height(global.pmS)) + " "
            + string(sprite_get_number(global.pmS)) + " ";
        for(global.pmY = 0; global.pmY < ${height * scale}; global.pmY += 1)
        {
            for(global.pmX = 0; global.pmX < ${width * scale}; global.pmX += 1)
            {
                if(collision_point(${ORIGIN_X} + global.pmX, ${ORIGIN_Y} + global.pmY,
                                   global.pmO, true, false) > 0)
                    global.pmOut += "#";
                else
                    global.pmOut += ".";
            }
            global.pmOut += "|";
        }
        with(global.pmO) instance_destroy();
        sprite_delete(global.pmS);
    }`;
}

async function main() {
  const samples = [];

  for (const sprite of SPRITES) {
    for (const scale of SCALES) {
      await ev(gml(sprite.file, sprite.width, sprite.height, scale));
      const raw = (await evalx('global.pmOut')).trim();

      if (raw === 'LOADFAILED') {
        throw new Error(`${sprite.file}: the engine refused to load it`);
      }

      const parts = raw.split(/\s+/);
      if (parts.length < 4) {
        throw new Error(`${sprite.file} @${scale}x: bridge said "${raw.slice(0, 160)}"`);
      }

      const rows = parts[3].split('|').filter(Boolean);
      const solid = rows.join('').split('').filter((c) => c === '#').length;

      samples.push({
        file: sprite.file,
        scale,
        spriteWidth: Number(parts[0]),
        spriteHeight: Number(parts[1]),
        subimages: Number(parts[2]),
        rows,
        solid,
      });

      console.log(
        `${sprite.file} @${scale}x -> ${parts[0]}x${parts[1]}, ` +
        `${parts[2]} subimage(s), ${rows.length} rows, ${solid} solid`);

      if (scale === 1) {
        for (const row of rows.slice(0, 6)) console.log(`    ${row}`);
        if (rows.length > 6) console.log(`    ... ${rows.length - 6} more`);
      }
    }
  }

  const out = path.join(FIXTURES, 'sprite-mask-samples.json');
  fs.writeFileSync(out, JSON.stringify({
    generated: new Date().toISOString().slice(0, 10),
    source: 'tools/probes/probe-platform-mask.js against the running game',
    note: 'The ENGINE answer. Where this and SpriteMask disagree, this is right.',
    samples,
  }, null, 2) + '\n');

  console.log(`\nwrote ${out}`);
}

main().catch((e) => {
  console.error(e.message || e);
  process.exit(1);
});
