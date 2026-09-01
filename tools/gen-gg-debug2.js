#!/usr/bin/env node
//=============================================================================
// gen-gg-debug2.js - generate maps/gg_debug2.png, the port-authored debug map
//
// gg_debug2 is NOT a reference map. It is authored by this repository, by this
// script, as a test range for the things gg_debug (one room, TDM) cannot
// exercise: an objective, nav-edge variety, and contained entity test rigs.
// MapCopyTests carves it out of the byte-identity gate by name and asserts the
// reference does NOT ship it, so a name collision with a future upstream map
// cannot pass silently.
//
//   node tools/gen-gg-debug2.js          # writes maps/gg_debug2.png
//
// The output is deterministic: same script, same bytes. Never edit the PNG by
// hand - change this script and re-run it, then regenerate the nav graph:
//
//   node <gg2-nav-gen>/bin/gg2navgen.js build maps/gg_debug2.png --md5 none --out nav
//
// (--md5 none because the server keys nav caches by map NAME, the way it does
// for every built-in map; gg2navgen would otherwise treat a PNG given by path
// as a custom map and suffix the file's MD5.)
//
// ⚠️ A stock client cannot join this map. The port always advertises an empty
// map MD5 ("built into the client"), and gg_debug2 is not built into the
// client, so a joining client would look it up in its internal list and fail.
// The map is for the headless side: tests, bots, eval, the env seam.
//
// ⚠️ No moving_platform, deliberately. The port declares the type but does not
// simulate it (CharacterPhysics.cs), and a map placing one would also make
// WorldShape promise FULL_UPDATE blocks the writer never writes.
//
// The map, in one paragraph: a 240x80-cell (1440x480 world px) mirror-symmetric
// CTF layout. Two bases (a gated spawn zone deepest, then an open intel zone
// with a pedestal, an ungated outer door, and a horizontal team gate on a roof
// hatch over the intel), a stepped valley of exact 6px auto-steps on each approach, a
// jump-platform ladder (42px risers: single-jumpable one to the next,
// double-jump-only straight from the floor), a central tower whose top is 168px
// above the floor (rocket-jump only; above the ~115px double-jump apex, under
// the 192px rocket apex) with a MoveBoxUp elevator on each face, an under-tower
// tunnel entered through dropdownPlatforms with a 6px ceiling dip inside, and a
// sealed physics chamber floating in the sky on each side - solid shell, no
// entrance, teleport-only - the red one holding the hazard and wall rigs
// (killbox, fragbox, playerwall, bulletwall, a mid-air dropdownPlatform, a
// free-standing probe wall, graduated 1..4-cell steps), the blue one bare flat
// ground for clean-room probes. The walkmask is exactly mirror-symmetric in x;
// the generator asserts it.
//=============================================================================

'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const W = 240; // cells; 1 cell = 6 world px = 1 art px
const H = 80;
const CELL = 6;

//---------------------------------------------------------------------------
// The walkmask, as rect fills. x1/y1 inclusive, in cells.
//---------------------------------------------------------------------------

const grid = new Uint8Array(W * H); // 1 = solid

function fill(x0, y0, x1, y1, v) {
  if (x0 < 0 || y0 < 0 || x1 >= W || y1 >= H || x0 > x1 || y0 > y1)
    throw new Error(`bad rect ${x0},${y0}..${x1},${y1}`);
  for (let y = y0; y <= y1; y++)
    for (let x = x0; x <= x1; x++) grid[y * W + x] = v;
}
const solid = (x0, y0, x1, y1) => fill(x0, y0, x1, y1, 1);
const open = (x0, y0, x1, y1) => fill(x0, y0, x1, y1, 0);

// Mirror a rect in x about the map's centreline (cell x -> 239 - x).
const mx = (x) => W - 1 - x;
const solidM = (x0, y0, x1, y1) => { solid(x0, y0, x1, y1); solid(mx(x1), y0, mx(x0), y1); };
const openM = (x0, y0, x1, y1) => { open(x0, y0, x1, y1); open(mx(x1), y0, mx(x0), y1); };

// Borders and the main floor (top surface row 66, world y 396).
solid(0, 0, W - 1, 1);       // top
solid(0, 78, W - 1, 79);     // bottom
solid(0, 0, 1, H - 1);       // left
solid(W - 2, 0, W - 1, H - 1); // right
solid(2, 66, W - 3, 77);     // floor slab

// Stepped valley on each approach: 6px (one-cell) steps down to a 36px-deep
// basin and back up. 6px is exactly the auto-step height, so the whole valley
// is walkable without a jump in both directions.
{
  const profile = [
    [48, 1], [50, 2], [52, 3], [54, 4], [56, 5],
    [58, 6], [60, 6], [62, 6], [64, 6],
    [66, 5], [68, 4], [70, 3], [72, 2], [74, 1],
  ];
  for (const [x, d] of profile) openM(x, 66, x + 1, 65 + d);
}

// Under-tower tunnel: interior rows 67..73 (42px), floor at row 74. Entered by
// dropping through a dropdownPlatform over each 10-cell shaft in the main
// floor; left by a 48px jump back out (single-jumpable, apex ~57px). A one-row
// ceiling dip under the tower gives the ceiling-contact arms something to hit.
openM(100, 66, 109, 66);  // shaft openings in the floor surface
open(100, 67, 139, 73);   // tunnel interior (self-symmetric)
solid(114, 67, 125, 67);  // ceiling dip: interior is 36px under the tower

// Bases. The spawn zone sits deepest, behind an interior wall whose doorway
// carries the team gate; the intel zone is between that gate and the ungated
// outer door, so the enemy can reach the intel but never the spawns.
// ⚠️ The first cut gated the OUTER door, which made each intel unreachable by
// the enemy for ever - a nav search from red spawn to blue intel correctly
// found no route. Gates protect spawns, not objectives; every shipped CTF map
// places them that way.
// ⚠️ And no pedestal under the intel: a 24px pedestal in the 8-cell intel zone
// split the ground into slivers narrower than the nav clearance box, so the
// zone got no floor node and the gate node dead-ended. The intel hovers over
// flat floor instead, the way ctf_orange places its own.
solidM(2, 40, 31, 42);    // roof (hatch cols 32..39 stay open)
solidM(40, 40, 41, 55);   // outer door wall (doorway rows 56..65 stays open)
solidM(30, 43, 31, 55);   // interior wall (gated doorway rows 56..65 below it)

// Jump ladder: 42px risers. Floor -> L1 -> L2 climbs to the roof; floor -> P1
// -> P2 -> P3 climbs toward the tower. P2 is 84px off the floor (double-jump
// only from below); every adjacent hop is a single jump.
solidM(58, 58, 63, 58);   // L1: 48px above the floor
solidM(70, 48, 75, 48);   // L2: 60px above L1
solidM(52, 44, 57, 44);   // L3: 24px above L2; the top of the outer door wall
                          // is one more 24px hop, and the roof a level jump
                          // across the hatch from there
solidM(80, 59, 85, 59);   // P1: 42px above the floor
solidM(90, 52, 95, 52);   // P2: 42px above P1
solidM(100, 45, 105, 45); // P3: 42px above P2

// The tower. Top surface row 38 = 168px above the floor: over the ~115px
// double-jump apex, under the 192px rocket-jump apex. MoveBoxUp elevators
// (entities, below) ride the open shafts along each face.
solid(115, 38, 124, 65);  // self-symmetric about the centreline

// Sealed physics chambers, one per side, no entrance - teleport-only, so no
// nav edge can lead a bot in. Shells and internal geometry mirrored to keep
// the mask symmetric; the test-rig ENTITIES live only in the red one.
solidM(44, 6, 96, 32);    // shell
openM(46, 8, 94, 30);     // interior: floor top row 31, ceiling row 7
solidM(50, 30, 51, 30);   // step, 1 cell - auto-step clears it
solidM(54, 29, 55, 30);   // step, 2 cells - needs a jump
solidM(58, 28, 59, 30);   // step, 3 cells
solidM(62, 27, 63, 30);   // step, 4 cells
solidM(70, 22, 70, 30);   // free-standing probe wall

// The mask must be exactly mirror-symmetric - cheap to assert, load-bearing
// for the map's own tests.
for (let y = 0; y < H; y++)
  for (let x = 0; x < W; x++)
    if (grid[y * W + x] !== grid[y * W + mx(x)])
      throw new Error(`mask not symmetric at ${x},${y}`);

//---------------------------------------------------------------------------
// Entities, in world px. Collider sizes (ColliderSprites.cs, SpawnRooms.cs):
// spawnroom/killbox/fragbox/MoveBox 42x42, medCabinet 32x48, vertical bars
// 6x60, horizontal bars 60x6, all origin top-left, stretched by scale. Intel
// has origin (9,12) on a 20x22 sprite, so its x,y is roughly its centre.
//---------------------------------------------------------------------------

const entities = [];
const e = (type, x, y, extra) => entities.push({ type, x, y, ...(extra || {}) });
const WPX = W * CELL; // 1440

// Point entity and its mirror (centre-origin: intel, spawns).
const eM = (redType, blueType, x, y, extra) => {
  e(redType, x, y, extra);
  e(blueType, WPX - x, y, extra);
};
// Box entity and its mirror (top-left origin, width w at the entity's scale).
const eBoxM = (redType, blueType, x, y, w, extra) => {
  e(redType, x, y, extra);
  e(blueType, WPX - x - w, y, extra);
};

e('meta', 0, 0, { background: '2a3140' });
delete entities[0].x; delete entities[0].y; // meta carries no position

// Bases. The floor's top surface is y=396; spawns are placed a few px up and
// settle. Spawnroom tiles cover cells 10..23 x rows 59..65, both spawns inside.
eBoxM('spawnroom', 'spawnroom', 60, 354, 42);
eBoxM('spawnroom', 'spawnroom', 102, 354, 42);
eM('redspawn', 'bluespawn', 78, 372);
eM('redspawn', 'bluespawn', 114, 372);
eM('redintel', 'blueintel', 216, 380);          // in the open intel zone, on the floor
eBoxM('medCabinet', 'medCabinet', 48, 348, 32);
eBoxM('redteamgate', 'blueteamgate', 180, 336, 6);    // seals the INTERIOR doorway
eBoxM('redteamgate2', 'blueteamgate2', 192, 240, 60); // seals the roof hatch

// Tunnel lids: dropdownPlatforms over the two floor shafts, flush with the
// floor surface. Positions are self-mirroring (1440-600-60=780).
e('dropdownPlatform', 600, 396);
e('dropdownPlatform', 780, 396);

// Tower elevators: MoveBoxUp columns riding the shafts along the tower faces,
// 18px wide (xscale 18/42) by 168px tall (yscale 4), floor to tower top.
eBoxM('MoveBoxUp', 'MoveBoxUp', 672, 228, 18, { xscale: '0.43', yscale: '4' });

// The red chamber's test rigs. Interior is x 276..569, y 48..185, floor 186.
e('dropdownPlatform', 330, 120);   // mid-air, nothing beneath but the floor
e('bulletwall', 390, 126);         // passes characters, stops shots
e('playerwall', 408, 126);         // passes shots, stops characters
e('fragbox', 444, 144);            // gibs on touch
e('killbox', 498, 144);            // kills on touch

//---------------------------------------------------------------------------
// Level data text: {ENTITIES} in the GGON list form (the one every shipped
// map but gg_debug uses), then {WALKMASK} in compressWalkmask.gml's 6-bit
// encoding.
//---------------------------------------------------------------------------

function ggon(entity) {
  const keys = Object.keys(entity).sort(); // deterministic; GGON maps are unordered
  return '{' + keys.map((k) => `${k}:${entity[k]}`).join(',') + '}';
}

const entityBlock = '[' + entities.map(ggon).join(',') + ']';

function encodeWalkmask() {
  let out = '';
  let acc = 0;
  let bits = 0;
  for (let i = 0; i < W * H; i++) {
    acc = (acc << 1) | grid[i];
    if (++bits === 6) {
      out += String.fromCharCode(acc + 32);
      acc = 0;
      bits = 0;
    }
  }
  if (bits > 0) out += String.fromCharCode((acc << (6 - bits)) + 32);
  return out;
}

const levelData =
  '{ENTITIES}\n' + entityBlock + '\n{END ENTITIES}\n' +
  '{WALKMASK}\n' + W + '\n' + H + '\n' + encodeWalkmask() + '\n{END WALKMASK}\n';

//---------------------------------------------------------------------------
// The PNG: 240x80 indexed art at one pixel per cell (the client scales x6),
// drawn from the mask so the art can never disagree with the collision, plus
// the level data in a zTXt chunk. Everything below is plain PNG plumbing.
//---------------------------------------------------------------------------

const PALETTE = [
  [0xc9, 0xd2, 0xdc], // 0 sky
  [0x3c, 0x44, 0x50], // 1 solid
  [0x8f, 0x4a, 0x46], // 2 red-base solid
  [0x46, 0x5d, 0x8f], // 3 blue-base solid
  [0xc8, 0x86, 0x2f], // 4 hazard marker
  [0x55, 0x8a, 0x62], // 5 entity marker
];

const art = new Uint8Array(W * H);
for (let i = 0; i < W * H; i++) art[i] = grid[i];
// Tint the base structures so the sides read at a glance.
for (let y = 38; y <= 77; y++)
  for (let x = 2; x <= 41; x++) {
    if (grid[y * W + x]) art[y * W + x] = 2;
    if (grid[y * W + mx(x)]) art[y * W + mx(x)] = 3;
  }
// Mark entity anchor cells: hazards orange, everything else green.
for (const entity of entities) {
  if (entity.type === 'meta') continue;
  const cx = Math.min(W - 1, Math.max(0, Math.floor(entity.x / CELL)));
  const cy = Math.min(H - 1, Math.max(0, Math.floor(entity.y / CELL)));
  art[cy * W + cx] = entity.type === 'killbox' || entity.type === 'fragbox' ? 4 : 5;
}

const CRC_TABLE = new Int32Array(256);
for (let n = 0; n < 256; n++) {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  CRC_TABLE[n] = c;
}
function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, 'latin1');
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
  return out;
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(W, 0);
ihdr.writeUInt32BE(H, 4);
ihdr[8] = 8;  // bit depth
ihdr[9] = 3;  // indexed colour
// [10..12] compression/filter/interlace = 0

const plte = Buffer.from(PALETTE.flat());

const raw = Buffer.alloc((W + 1) * H);
for (let y = 0; y < H; y++) {
  raw[y * (W + 1)] = 0; // filter: none
  for (let x = 0; x < W; x++) raw[y * (W + 1) + 1 + x] = art[y * W + x];
}
const idat = zlib.deflateSync(raw, { level: 9 });

const keyword = 'Gang Garrison 2 Level Data';
const compressed = zlib.deflateSync(Buffer.from(levelData, 'latin1'), { level: 9 });
const ztxt = Buffer.concat([Buffer.from(keyword + '\0\0', 'latin1'), compressed]);

const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk('IHDR', ihdr),
  chunk('PLTE', plte),
  chunk('IDAT', idat),
  chunk('zTXt', ztxt),
  chunk('IEND', Buffer.alloc(0)),
]);

const outPath = path.join(__dirname, '..', 'maps', 'gg_debug2.png');
fs.writeFileSync(outPath, png);

let solidCount = 0;
for (const c of grid) solidCount += c;
console.log(
  `gg_debug2.png: ${png.length} bytes, ${W}x${H} cells, ` +
  `${solidCount} solid (${((100 * solidCount) / (W * H)).toFixed(1)}%), ` +
  `${entities.length - 1} entities + meta -> ${outPath}`);
