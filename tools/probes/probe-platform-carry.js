#!/usr/bin/env node
//=============================================================================
// probe-platform-carry.js - Phase C. Does a CHARACTER meet a platform's precise
// mask the way PreciseCollision models it?
//
// probe-platform-mask.js settled the mask itself: removeback samples the
// bottom-left pixel, scaling is exact replication, and a GIF's mask is the union
// of its subimages. Every one of those was rasterised with a 1x1 probe point, so
// what is still unmeasured is the OTHER side of the query - the character.
//
// Two questions, and the first is the one that could invalidate the model:
//
//  1. ⚠️ IS A CHARACTER A RECTANGLE TO GM8, or a per-pixel silhouette?
//     PreciseCollision does rect-vs-precise: CharacterBox on one side, the
//     sprite mask on the other. If GM8 instead uses the character sprite's own
//     precise mask, a character at a platform's edge collides on its outline
//     rather than its box and the whole model is wrong at exactly the pixels
//     that decide whether somebody is standing on something.
//
//     The discriminator needs no model of either answer: for the same character
//     at the same position, compare place_meeting against the platform with
//     collision_rectangle over that character's OWN bbox. Rectangle mask -> the
//     two agree everywhere. Precise silhouette -> they disagree wherever the
//     outline is inside the box, which for a person shape is most places.
//
//  2. The inclusive bounds and the rounding, swept. A character is essentially
//     never on an integer coordinate, so the interesting positions are the
//     fractional ones either side of first contact. Swept in tenths across the
//     platform's left edge; PreciseCollisionOracleTests replays the same sweep.
//
// ⚠️ The sweep is the point. A single position agreeing proves nothing about
// where the boundary is - that was the lesson of the character-box work, where
// 48 scenarios passed for a year without one of them touching a ceiling.
//
// ⚠️ Offsets are sent as ratios, never decimal literals: GM8's parser is a ULP
// off for some decimals and this probe is entirely about sub-pixel placement.
// (CLAUDE.md, and probe-fractional-mask.js's header.)
//
// Writes tests/Gg2.Tests/Fixtures/platform-carry-samples.json. Needs any running
// instance with the agent bridge - a dedicated server with an empty match is
// enough, no bots required. Do not run while an MCP session holds the bridge.
//=============================================================================

const path = require('path');
const fs = require('fs');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const REPO = path.resolve(__dirname, '..', '..');
const FIXTURES = path.join(REPO, 'tests', 'Gg2.Tests', 'Fixtures');

const ORIGIN_X = 3000;
const ORIGIN_Y = 3000;

// The platform sprite the sweep runs against: 46x5, an RGBA PNG whose mask this
// port already matches pixel for pixel at 1x and 3x.
const SPRITE = 'sprite-ji.png';

// Blue sprites, which is what getCharacterSpriteId returns for team 2 and what
// probe-character-boxes.js read.
const CLASSES = [
  { name: 'Scout', sprite: 'ScoutBlueS' },
  { name: 'Heavy', sprite: 'HeavyBlueS' },
];

// Tenths across the platform's left edge, bracketing first contact for BOTH
// boxes: the sprite starts at ORIGIN_X, so a Scout (right edge +6) touches at
// ORIGIN_X-6 and a Heavy (+9) at ORIGIN_X-9. Sweeping ORIGIN_X-12 to ORIGIN_X-2
// puts both transitions inside the range with clear space either side.
//
// ⚠️ The first version swept ORIGIN_X-16 to ORIGIN_X-12 and never made contact
// at all. Every sample was "no collision" on both sides, so the two agreed
// perfectly and the probe reported zero disagreements - a vacuous pass that
// looked exactly like a real one. The guard in main() exists because of it.
const STEPS = 100;

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

function setup() {
  const full = path.join(FIXTURES, SPRITE).replace(/\\/g, '/');

  return `
    global.pcS = sprite_add("${full}", 1, 1, 0, 0, 0);
    global.pcO = instance_create(${ORIGIN_X}, ${ORIGIN_Y}, SpriteObject);
    with(global.pcO)
    {
        sprite_index = global.pcS;
        image_index = 0;
        image_speed = 0;
        image_xscale = 1;
        image_yscale = 1;
    }`;
}

// One class: its box straight off the class sprite (Character/Create's own
// formula), then the sweep.
function sweep(cls) {
  return `
    global.pcOut = "";
    global.pcSpr = ${cls.sprite};
    global.pcL = sprite_get_bbox_left(global.pcSpr) - sprite_get_xoffset(global.pcSpr);
    global.pcR = sprite_get_bbox_right(global.pcSpr) - sprite_get_xoffset(global.pcSpr);
    global.pcT = sprite_get_bbox_top(global.pcSpr) - sprite_get_yoffset(global.pcSpr);
    global.pcB = sprite_get_bbox_bottom(global.pcSpr) - sprite_get_yoffset(global.pcSpr);
    global.pcOut += string(global.pcL) + " " + string(global.pcR) + " "
        + string(global.pcT) + " " + string(global.pcB) + " ";

    global.pcProbe = instance_create(0, 0, SpriteObject);
    with(global.pcProbe)
    {
        sprite_index = global.pcSpr;
        image_index = 0;
        image_speed = 0;
    }

    for(global.pcI = 0; global.pcI < ${STEPS}; global.pcI += 1)
    {
        global.pcX = ${ORIGIN_X} - 12 + global.pcI/10;
        global.pcY = ${ORIGIN_Y} + 2;

        with(global.pcProbe)
        {
            x = global.pcX;
            y = global.pcY;

            // (a) the character's own mask against the platform
            if(place_meeting(x, y, global.pcO))
                global.pcOut += "1";
            else
                global.pcOut += "0";

            // (b) that character's BOX against the platform, precise on the
            //     platform side. If the character is a rectangle to GM8 these
            //     two strings are identical.
            if(collision_rectangle(x + global.pcL, y + global.pcT,
                                   x + global.pcR, y + global.pcB,
                                   global.pcO, true, false) > 0)
                global.pcOut += "1";
            else
                global.pcOut += "0";
        }

        global.pcOut += " ";
    }

    with(global.pcProbe) instance_destroy();`;
}

async function main() {
  await ev(setup());

  const samples = [];

  for (const cls of CLASSES) {
    await ev(sweep(cls));
    const raw = (await evalx('global.pcOut')).trim();
    const parts = raw.split(/\s+/);

    if (parts.length < 4 + STEPS) {
      throw new Error(`${cls.name}: bridge said "${raw.slice(0, 200)}"`);
    }

    const box = {
      left: Number(parts[0]),
      right: Number(parts[1]),
      top: Number(parts[2]),
      bottom: Number(parts[3]),
    };

    const meeting = [];
    const rectangle = [];

    for (let i = 0; i < STEPS; i++) {
      const pair = parts[4 + i];
      meeting.push(pair[0] === '1');
      rectangle.push(pair[1] === '1');
    }

    const disagreements = meeting.filter((m, i) => m !== rectangle[i]).length;

    samples.push({
      class: cls.name,
      sprite: SPRITE,
      box,
      originX: ORIGIN_X,
      originY: ORIGIN_Y,
      startX: ORIGIN_X - 12,
      stepDenominator: 10,
      steps: STEPS,
      probeY: ORIGIN_Y + 2,
      placeMeeting: meeting,
      collisionRectangle: rectangle,
      disagreements,
    });

    console.log(`${cls.name}: box ${box.left},${box.right},${box.top},${box.bottom}`);
    console.log(`  place_meeting       ${meeting.map((m) => (m ? '#' : '.')).join('')}`);
    console.log(`  collision_rectangle ${rectangle.map((m) => (m ? '#' : '.')).join('')}`);
    console.log(`  disagreements: ${disagreements}`);

    // ⚠️ A sweep that never touches the platform agrees with itself perfectly
    // and reports zero disagreements, which is indistinguishable from a real
    // pass. The first run of this probe did exactly that. Refuse to write a
    // fixture unless the sweep actually crossed the boundary in both directions.
    const hits = meeting.filter(Boolean).length;
    if (hits === 0 || hits === STEPS) {
      throw new Error(
        `${cls.name}: the sweep never crossed the platform edge (${hits}/${STEPS} hits). ` +
        'A uniform result proves nothing — widen the range rather than recording it.');
    }
  }

  await ev(`
    with(global.pcO) instance_destroy();
    sprite_delete(global.pcS);`);

  const out = path.join(FIXTURES, 'platform-carry-samples.json');
  fs.writeFileSync(out, JSON.stringify({
    generated: new Date().toISOString().slice(0, 10),
    source: 'tools/probes/probe-platform-carry.js against the running game',
    note: 'The ENGINE answer. Where this and PreciseCollision disagree, this is right.',
    samples,
  }, null, 2) + '\n');

  console.log(`\nwrote ${out}`);
}

main().catch((e) => {
  console.error(e.message || e);
  process.exit(1);
});
