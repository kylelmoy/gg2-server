# Port-authored debug maps

Maps this repository authors itself, for testing — the reference does not ship
them, so every claim about one is gated inside this repo or nowhere. One exists
today: **`gg_debug2`**. (`gg_debug` looks like a debug map but is stock GG2 —
it ships with the game and is covered by the ordinary map gates.)

## The rules that make a port-authored map safe to have

- **Generated, never hand-edited.** `maps/gg_debug2.png` is
  `tools/gen-gg-debug2.js`'s output, deterministically. The fix for anything
  about the map is a change to the generator and a re-run.
- **`MapCopyTests` asserts the reference does NOT ship the name** — a future
  upstream map colliding with it fails loudly instead of silently diverging.
  The name set is `ReferenceTree.PortAuthoredMaps`.
- **Every shipped-map census excludes it** (`ShippedEntityCensusTests` filters
  on that set), because "no shipped map does X" is load-bearing and a
  port-authored map must not be able to move it. `GgDebug2MapTests` carries the
  map's own exact census instead.
- **`gg_debug2` is the only built-in map that places `moving_platform`** —
  five of them, since 2026-09-01, added for the moving-platforms work. ⚠️ The
  first version of this file said a non-zero count would be "a framing break:
  the writer never writes the blocks", and **that was false when written**:
  `MatchState.BuildEntities` has fed `StateUpdateWriter`'s five-byte platform
  blocks since phase 6. What was missing was the *motion* (the positions never
  advanced), which is the moving-platforms lane's `MovingPlatforms.EndStep`.
  The map is therefore wire-safe with or without that work merged.
  ⚠️ **Only the chamber raft can ever carry a character**: the carry needs the
  platform's collision mask, which arrives from an embedded sprite named by a
  `resource` property, and the raft is the one platform that names one (the
  other four deliberately never grow a shape — the census test pins that).
  ⚠️ **The raft narrows the moving-platforms `SIM_UUID` exemption rather than
  voiding it** — see `SimulationStamp` and
  `MovingPlatformTests`: the exemption's premise is now "no platform with a
  shape is reachable in play", which the raft satisfies because it lives in
  the sealed chamber, and the two lanes agreed that wording rather than either
  quietly widening it. All five are nav-invisible — `gg2-nav-gen` skips the
  type outright, and the nav graph is byte-identical with and without them
  (measured).
- ✅ **A stock client CAN join it**, as of the custom-map serving merge. This
  bullet used to say the opposite and named its own expiry; the expiry has
  arrived, so here is what replaced it. `gg_debug2` is not one of
  `Gg2.Maps.InternalMaps`' 22 names, so the server advertises **its MD5** rather
  than the empty string that means "built into the client", and answers
  `DOWNLOAD_MAP` by streaming the file — a `uint` length then the bytes,
  rate-limited across ticks and shared between simultaneous downloaders. The
  client writes it to its own `Maps/`, re-hashes it, and joins.
  ⚠️ **Nothing is published for that to work**: the map is served off this server
  over the same socket, to whoever connects. `--lobby` is a different switch and
  still defaults off.

## Regenerating

```powershell
node tools/gen-gg-debug2.js
node <gg2-nav-gen>/bin/gg2navgen.js build maps/gg_debug2.png --md5 none --out nav
```

`--md5 none` because the server keys nav caches by map *name* (the built-in
rule); without it gg2navgen treats a by-path map as a custom map and suffixes
the file's MD5, and `NavCache.TryLoad` never finds it.

⚠️ **A generator change moves pinned numbers, deliberately.** `GgDebug2MapTests`
pins the map's census and nav properties, and the whole-cache pins in
`EdgeFlightTests` and `NavGraphFixtureTests.ParallelEdgesAreCommonAndCarryMoreThanOneType`
sum over every graph in `nav/`, this one included. A failure there after a
regeneration is the re-record path: check the change is the one you meant,
update the numbers, say so in the commit.

## gg_debug2 — the CTF debug range

240x80 cells (1440x480 world px), mirror-symmetric in x (asserted by the
generator and by `GgDebug2MapTests.TheMaskIsMirrorSymmetric`). Red on the left.
Six matches at seed 1 split 3–3 with zero unfinished. What it has that
`gg_debug` (one room, TDM) does not:

| Zone | Where (cells; world = x6) | What it exercises |
|---|---|---|
| Bases | x 2..41 and mirror | Gated **spawn zone** (spawnroom tiles, 2 spawns, medCabinet) behind an interior wall whose doorway carries the team gate; open **intel zone** between that gate and the ungated outer door; roof with a `redteamgate2`/`blueteamgate2` **hatch** over the intel — own team falls through, enemy walks across |
| Step valley | x 48..75 and mirror | Exact 6px auto-steps down 36px and back up — walkable both ways with no jump. A `moving_platform` ferry loops horizontally above each valley (world x 300↔420 and its mirror, y 380, 2 px/tick) |
| Jump ladder | P1/P2/P3 + L1/L2/L3, x 52..105 and mirror | 42px risers: single-jumpable hop to hop, P2 at 84px is double-jump-only from the floor; the ladder tops out one jump from the tower top |
| Tower | x 115..124, top at row 38 | 168px above the floor: over the ~115px double-jump apex, under the 192px rocket apex — rocket-jump direct, or the ladder the slow way. `MoveBoxUp` elevator on each face (nav-invisible; humans and physics tests) |
| Tunnel | x 100..139, under the tower | Entered by two `dropdownPlatform` lids in the floor (nav **drop** edges), left by a 48px jump out of either shaft; one-row ceiling dip inside for the ceiling-contact arms |
| Sealed chambers | x 44..96 / 143..195, rows 6..32, in the sky | **No entrance** — teleport-only, so no nav edge can lead a bot in (flood-fill asserted). Red chamber: killbox, fragbox, playerwall, bulletwall, a mid-air dropdownPlatform, a free-standing probe wall, graduated 1..4-cell steps, and three `moving_platform`s — a vertical hoist at (564, 168) rising 90px on asymmetric speeds (1 up, 3 down: both speed arms, plus the reference's snap-at-start-only asymmetry), a `trigger:1` platform at (300, 120) that stays parked for ever because no rider can exist without the carry, and **the raft** at (282, 150), the only platform anywhere with a collision shape: a 24x12 embedded PNG (`resource:raftsprite` in the meta block, raw file bytes as a GGON string), non-rectangular — full 24x2 deck over a hull tapering 1px a side per row — and removeback-discriminating: **178 solid pixels under GM8's bottom-left background rule, 110 under a wrong top-left reading**, so a live-game probe can settle that corner from the count alone. Blue chamber: bare flat ground, a clean room for probes |

The nav graph (`nav/gg_debug2_a1.txt`, 70 nodes) carries **every edge type the
bot layer consumes** — walk, jump, fall, drop-through, double-jump, rocket-jump
— asserted by `GgDebug2MapTests.TheNavGraphCoversEveryEdgeType`, and
`APlainWalkerReachesTheEnemyIntelButNotTheEnemySpawn` gates pathability in both
directions plus the gates' refusal.

⚠️ **The bug its first cut shipped, kept because the shape generalises**: the
team gates sat on the *outer* doors, so each intel was behind a gate solid to
the enemy — no route to either objective existed for anybody, while every
geometry test stayed green and bots milled around convincingly. Gates protect
spawns, not objectives, and pathability is a claim about the *graph*, so it is
gated on the graph rather than inferred from the mask. The second cut's intel
pedestal repeated the lesson smaller: a 24px pedestal split the narrow intel
zone into slivers under the nav clearance box and the zone lost its floor node.
**After changing base geometry, re-run the nav build and the pathability test
before believing the map.**
