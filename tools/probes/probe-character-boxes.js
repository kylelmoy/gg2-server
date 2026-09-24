#!/usr/bin/env node
//=============================================================================
// probe-character-boxes.js - the ten character collision boxes, asked of the
// engine the way the game itself computes them.
//
// Character.events/Create.xml:
//
//     left_bound_offset   = sprite_get_bbox_left(sprite_index)   - sprite_get_xoffset(...)
//     right_bound_offset  = sprite_get_bbox_right(sprite_index)  - sprite_get_xoffset(...)
//     top_bound_offset    = sprite_get_bbox_top(sprite_index)    - sprite_get_yoffset(...)
//     bottom_bound_offset = sprite_get_bbox_bottom(sprite_index) - sprite_get_yoffset(...)
//
// So a character's box is a pure property of its class sprite, and needs no
// character, no bot and no map to read. That is the whole reason this probe is
// cheap and the reason nothing forced it to be written earlier.
//
// ⚠️ WHY IT EXISTS. CharacterBox.For is the box every character collision in
// the port uses, and tools/sample-physics.js OVERRIDES the engine's values with
// a hardcoded table of its own before driving a scenario:
//
//     top_bound_offset = ${cls.top};      // -6 for Scout, Heavy and Pyro
//
// but CharacterBox.For says top is -10 for a Scout and -12 for a Heavy. The two
// disagree, and physics-samples.json was generated with the sampler's numbers
// while PhysicsSampleTests replays it through the port's. The suite is green,
// which means no scenario in the fixture has ever had an outcome that depended
// on the top edge - 48 scenarios of 48 ticks with no head-to-ceiling contact
// that mattered. That is luck, not coverage: the first scenario that touches a
// ceiling would be checked in with a wrong expectation and read as a port bug.
//
// This settles which table is right. Whichever loses gets corrected.
//
// ⚠️ The sprite is the BLUE one for each class, which is what
// getCharacterSpriteId returns for team 2 and what sample-physics.js used. If a
// team's sprites ever differed in bbox the port would be wrong in a way no
// fixture could see, so both teams are read and compared here.
//
// Needs any running instance with the agent bridge - a dedicated server with an
// empty match is enough, and no Character need exist. Do not run while an MCP
// session holds the bridge.
//=============================================================================

const path = require('path');

const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

const CLASSES = [
  'Scout', 'Soldier', 'Sniper', 'Demoman', 'Medic',
  'Engineer', 'Heavy', 'Spy', 'Pyro', 'Quote',
];

// What the port believes, from Gg2.Simulation.CharacterBox.For.
const PORT = {
  Scout: [-6, 6, -10, 23],
  Soldier: [-6, 6, -8, 23],
  Sniper: [-6, 6, -8, 23],
  Demoman: [-7, 7, -10, 23],
  Medic: [-7, 7, -8, 23],
  Engineer: [-6, 6, -10, 23],
  Heavy: [-9, 9, -12, 23],
  Spy: [-6, 6, -10, 23],
  Pyro: [-7, 7, -6, 23],
  Quote: [-7, 7, -12, 11],
};

const ev = (code) => mcp.callTool('gg2_eval', { code });

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  return typeof r === 'string' ? r : JSON.stringify(r);
}

function gml() {
  const lines = ['global.cbOut = "";', 'var sp;'];

  // ⚠️ Built through the game's own getCharacterSpriteId rather than by
  // spelling the names out: it prefixes from global.characterSpriteClassPrefixes
  // and returns asset_get_index, so it gets Quote right, whose sprites are NOT
  // "QuoteRedS". Spelling them out is what the first run of this probe did, and
  // the game answered with "Unknown variable QuoteRedS".
  //
  // Its team argument is 0 or 1, not the TEAM_ constants.
  for (let cls = 0; cls < CLASSES.length; cls++) {
    for (const [team, label] of [[0, 'Red'], [1, 'Blue']]) {
      lines.push(`sp = getCharacterSpriteId(${cls}, ${team}, "");`);
      lines.push(`global.cbOut += "||${CLASSES[cls]} ${label} ";`);
      lines.push(
        'global.cbOut += string(sprite_get_bbox_left(sp) - sprite_get_xoffset(sp)) + " "'
          + ' + string(sprite_get_bbox_right(sp) - sprite_get_xoffset(sp)) + " "'
          + ' + string(sprite_get_bbox_top(sp) - sprite_get_yoffset(sp)) + " "'
          + ' + string(sprite_get_bbox_bottom(sp) - sprite_get_yoffset(sp));',
      );
    }
  }

  return lines.join('\n');
}

async function main() {
  await ev(gml());
  const raw = await evalx('global.cbOut');

  const rows = raw.split('||').slice(1).map((chunk) => {
    const [cls, team, l, r, t, b] = chunk.trim().split(/\s+/);
    return { cls, team, box: [Number(l), Number(r), Number(t), Number(b)] };
  });

  console.log('class      team   engine  (left,right,top,bottom)      port           verdict');

  let wrong = 0;
  let teamDiff = 0;

  for (const cls of CLASSES) {
    const red = rows.find((x) => x.cls === cls && x.team === 'Red');
    const blue = rows.find((x) => x.cls === cls && x.team === 'Blue');

    if (String(red.box) !== String(blue.box)) teamDiff++;

    const port = PORT[cls];
    const ok = String(blue.box) === String(port);

    if (!ok) wrong++;

    console.log(
      `  ${cls.padEnd(9)} both   [${blue.box.join(',').padEnd(16)}]  `
      + `[${port.join(',').padEnd(16)}]  ${ok ? 'ok' : '<<< DIFFERS'}`
      + `${String(red.box) === String(blue.box) ? '' : '  (RED DIFFERS FROM BLUE)'}`,
    );
  }

  console.log(`\n${wrong} of ${CLASSES.length} classes disagree with CharacterBox.For`);
  console.log(`${teamDiff} of ${CLASSES.length} classes differ between red and blue`);
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('FAILED: ' + (e && e.message ? e.message : e));
    process.exit(1);
  },
);
