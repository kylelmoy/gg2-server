#!/usr/bin/env node
//=============================================================================
// probe-sprite-boxes.js - the four collision boxes the port had to derive.
//
// Phases 5 and 6 landed four boxes without a live session and said so in the
// code:
//
//   BubbleS            derived from BubbleS.xml's mode="FULL" meaning the whole
//                      12x12 image, with MineS as the measured control.
//   StabMaskS          read off the PNG's alpha extent. No measured control for
//                      an AUTO bound existed anywhere in ProjectileSprites.
//   IntelligenceRed/BlueS  the briefcase - the one collision box in the project
//                      that was never measured, and one that no recording can
//                      pin: docs/PARITY.md records three of its four edges
//                      moving sixteen pixels with every fixture still green.
//   GeneratorRed/BlueS the generator, in the same standing, and the box its
//                      400-pixel blast measures every distance from.
//
// All four were asked of the engine on 2026-08-27 and all four were right.
// This regenerates tests/Gg2.Tests/Fixtures/sprite-box-samples.json, which
// SpriteBoxSampleTests compares the port's constants against.
//
// NOT by creating the objects themselves - an Intelligence dereferences its
// base and a Generator its HUD. A Shot wearing the sprite as mask_index answers
// the same question, because a bounding box is a property of the sprite and
// image_xscale is a property of the instance. Same trick as probe-sentry-mask.js.
//
// ⚠️ object_get_mask is read for every object as well, because SentryRed is the
// standing proof that a sprite's own bbox can be the wrong answer: Sentry.xml
// declares <mask>SentryMaskS</mask> and mask_index beats sprite_index. All six
// objects here report -1, so their own sprite is the mask.
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

// sprite id, object name - the object is read only for object_get_mask.
const SPRITES = [
  ['BubbleS', 'Bubble'],
  ['StabMaskS', 'StabMask'],
  ['IntelligenceRedS', 'IntelligenceRed'],
  ['IntelligenceBlueS', 'IntelligenceBlue'],
  ['GeneratorRedS', 'GeneratorRed'],
  ['GeneratorBlueS', 'GeneratorBlue'],

  // ⚠️ The dropdown platform, added 2026-08-28. Its box was INFERRED, never
  // measured, because the collision oracle only ever sampled solid things and a
  // platform is never solid to anything (SolidKind.DropdownPlatform). The port
  // builds it as a 60x6 bar; nothing has ever asked the engine. It is the one
  // collider left in that standing, and the same mask_index trick reaches it.
  ['sprite44', 'DropdownPlatform'],
];

function gml() {
  const lines = [
    'var s, out, sp;',
    'out = "";',
    's = instance_create(-3000, -3000, Shot);',
    's.image_angle = 0;',
    's.image_yscale = 1;',
    's.image_index = 0;',
  ];

  for (const [sprite, object] of SPRITES) {
    lines.push(`sp = ${sprite};`);
    lines.push('s.mask_index = sp;');
    lines.push('s.image_xscale = 1;');
    lines.push(`out += "||${sprite}";`);
    lines.push(`out += " mask=" + string(object_get_mask(${object}));`);
    lines.push('out += " n=" + string(sprite_get_number(sp));');
    lines.push('out += " size=" + string(sprite_get_width(sp)) + "," + string(sprite_get_height(sp));');
    lines.push('out += " org=" + string(sprite_get_xoffset(sp)) + "," + string(sprite_get_yoffset(sp));');
    lines.push(
      'out += " sbbox=" + string(sprite_get_bbox_left(sp)) + "," + string(sprite_get_bbox_top(sp))'
        + ' + "," + string(sprite_get_bbox_right(sp)) + "," + string(sprite_get_bbox_bottom(sp));',
    );
    lines.push(
      'out += " inst=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y)'
        + ' + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);',
    );
    // The flipped reading is the control: it is the two-pixel bbox_left
    // inflation on four sprites that are not characters, and it is the half
    // Aabb.ForCharacter is allowed to skip only because character masks are
    // symmetric. None of these is.
    lines.push('s.image_xscale = -1;');
    lines.push(
      'out += " flip=" + string(s.bbox_left - s.x) + "," + string(s.bbox_top - s.y)'
        + ' + "," + string(s.bbox_right - s.x) + "," + string(s.bbox_bottom - s.y);',
    );
  }

  lines.push('with(s) instance_destroy();');
  lines.push('global.spriteBoxProbe = out;');
  return lines.join('\n');
}

function parse(raw) {
  const out = [];

  for (const chunk of raw.split('||').slice(1)) {
    const fields = chunk.trim().split(/\s+/);
    const get = (key) => {
      const hit = fields.find((f) => f.startsWith(`${key}=`));
      if (hit === undefined) throw new Error(`no ${key} in "${chunk}"`);
      return hit.slice(key.length + 1).split(',').map(Number);
    };

    const [sprite, object] = SPRITES[out.length];
    const [width, height] = get('size');
    const [xOrigin, yOrigin] = get('org');
    const [bboxLeft, bboxTop, bboxRight, bboxBottom] = get('sbbox');
    const [il, it, ir, ib] = get('inst');
    const [fl, ft, fr, fb] = get('flip');

    out.push({
      sprite,
      object,
      objectMask: get('mask')[0],
      frames: get('n')[0],
      width,
      height,
      xOrigin,
      yOrigin,
      bboxLeft,
      bboxTop,
      bboxRight,
      bboxBottom,
      instance: { left: il, top: it, right: ir, bottom: ib },
      flipped: { left: fl, top: ft, right: fr, bottom: fb },
    });
  }

  if (out.length !== SPRITES.length)
    throw new Error(`expected ${SPRITES.length} sprites, parsed ${out.length}`);

  return out;
}

async function main() {
  await ev(gml());
  const raw = await evalx('global.spriteBoxProbe');
  const sprites = parse(raw);

  const doc = {
    generated: new Date().toISOString().slice(0, 10),
    source: 'tools/probes/probe-sprite-boxes.js, against any dedicated server (a sprite box does not depend on the map)',
    note:
      'Raw sprite_get_* and instance bbox_* readings for the boxes phases 5 and 6 had to derive '
      + "from a PNG or an XML rather than ask the engine for. 'instance' and 'flipped' are bbox_* "
      + 'minus the instance position, at image_xscale +1 and -1.',
    sprites,
  };

  console.log(JSON.stringify(doc, null, 2));
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
