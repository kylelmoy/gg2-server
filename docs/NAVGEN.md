# The nav-graph generator

`src/GG2.Nav` builds a bot nav graph from a map PNG, with no game running and no
reference checkout. It is the reason a **custom map** is navigable at all.

Two entry points, and they are the same code:

```powershell
dotnet run --project src/GG2.Host -- navgen --help    # the offline tool
dotnet run --project src/GG2.Host -- --map ctf_truefort   # a server, which builds what it misses
```

## Why it exists here

Nav graphs for the shipped maps are also produced by `gg2-nav-gen`, a standalone
Node tool that reads a Game Maker checkout for `Constants.xml` and the sprite
sizes, and originally by GML running inside the game itself
(`Source/gg2/Scripts/BotNav/*.gml`). Neither can run inside this repository, so
without a generator here, `nav/`'s 25 files are the only navigable maps:
`nav/` covers the 22 shipped maps plus `gg_debug`/`gg_debug2`, and anything
else has no graph — a map an operator added, a map edited since its graph was
built, a stage of a multi-area map nobody generated. A bot with no graph fights
whatever reaches it and walks nowhere, which on a full-size map means the two
teams never meet, every match ends 0-0 and unfinished, and it reads as a broken
bot layer rather than as absent data. Everything a graph needs is in the map's
own PNG (the walkmask and the entity list, deflated into a zTXt chunk, the same
input `CustomMapInit.gml` reads), so it was never data that had to be missing.

## The claim, and what backs it

**This is a port, and it has an oracle.** `NavGeneratorTests.RebuildsEveryShippedGraphExactly`
rebuilds all 25 checked-in graphs from the map PNGs and compares them
**character for character** against `nav/`. Those files were produced by
`gg2-nav-gen`; they are what the bot layer's pinned end-to-end measurements were
taken against. A generator that reproduces them exactly is provably the same
generator.

**That test is load-bearing for a design decision, not just for correctness.**
`gg2-nav-gen` keeps three copies of the arc machinery, one per manoeuvre, because
it has no oracle of its own to check a change to its single-jump path against
(the GML generator it was ported from no longer exists), so it keeps
unverifiable edits off the hot path of every ordinary jump. Here there is one
`NavArcModel` with three subclasses, because the comparison catches what the
duplication was defending against. **If that test is ever deleted or relaxed to
counts or to a tolerance, the consolidation has to be undone with it.**

**Counts are not enough and a tolerance is not enough.** Each of the traps below
produced a graph with a plausible node and edge count that was *different* on 22
of 23 maps:

1. **`round()` in GM8 is banker's rounding.** Delphi's `Round` is
   round-half-to-even: `round(0.5)` is 0, `round(2.5)` is 2. The arc sampler
   rounds a row and a column every sample, so one disagreeing half-integer is a
   clearance read against different terrain. `Gml.Round`, everywhere.
2. **`ds_priority_delete_min` removes by swapping in the last element.** It scans
   for the first strictly-smaller priority and fills the hole from the end of the
   list, so for three equal-cost candidates added as 29, 30, 31 it returns 29,
   31, 30 — and behind one cheaper entry, 31, 30, 29. Not a stable order, and not
   the reverse of one. It decides which candidates fill a fixed quota, so a
   "sensible" tie-break is a different graph. `NavGraphBuilder.TakeCheapest`.
3. **x87 shows through in `ceil(flightTime)`.** A flight whose exact time is 21.0
   comes out of the game as exactly 21 and out of IEEE doubles an ulp over, and
   `ceil` then takes one more sample of the arc. `NavArcModel.SamplesFor` snaps a
   value within 1e-9 of an integer, which is a correction toward the true value
   rather than a fudge. Without it `cp_dirtbowl` is one real edge short.
4. **The bounding box of a stamped instance.** Every nav-relevant object wears a
   sprite with origin (0, 0) and a full-image RECTANGLE mask, so the box is the
   whole scaled sprite, inclusive at both ends. `NavInstances`.

## The stages

```
map PNG ──zTXt chunk──> walkmask (1 bit per cell) + entity list (GGON)
                              │                    [GG2.Maps: LevelMap, Walkmask]
              entities ──> instances ──> stamp solid / platform / lethal / door / gate
                              │                    [NavInstances]
                        clearance dilation by the character box
                              │                    [NavSurfaces.Clearance]
                        nodes: run-length encode the standable anchors
                              │                    [NavSurfaces.ExtractNodes]
              edges: walk, fall, drop-through, jump, doublejump,
                     rocketjump, stickyjump (opt-in), movebox
                              │        └── each arc vetoed by the CHARACTER
                              │            that has to fly it [NavCharacter]
                        counting sort by from-node
                              │                    [NavEdgeBuffer.SortByFrom]
                        nav/<map>_a<area>.txt.gz   [NavCacheWriter]
```

| File | What |
|---|---|
| `NavInstances.cs` | entity → object → bounding box → grid stamp |
| `NavGrids.cs` | solidity, clearance, the standable runs, the two indices |
| `NavArcModel.cs` | the arc machinery: ceiling, landing, takeoff search, clearance walk |
| `NavJumpModel.cs` | the ordinary jump's height curve and flight time |
| `NavDoubleJumpModel.cs` | the Scout's second jump: two impulses welded at a re-jump tick |
| `NavRocketJumpModel.cs` | a Soldier firing at its own feet — and, on the same law, a Demoman's mine |
| `NavCharacter.cs` | the character: two tick laws, the run-up, whether the speed is there |
| `NavEdgeBuffer.cs` | the edge accumulator and the counting sort |
| `NavGraphBuilder.cs` | the eight edge passes |
| `NavCacheWriter.cs` | `ds_grid_write`, byte for byte — what `NavCache` reads |
| `NavGenerator.cs` | one map area, end to end |

Each file's header carries the reasoning for what is in it, and most of it exists
because a specific map broke. Read the one you are about to change.

### The graph is written for Heavy, on purpose

An arc is geometry: a parabola at a constant horizontal velocity from tick 0,
every clearance cell checked. A character is not — it accelerates, so it starts
at whatever it walked in with and closes on `basemaxspeed` geometrically, and an
arc that asks for most of that ceiling has no surplus left to repay the deficit
built up over the ramp. `basemaxspeed` runs from Heavy's 4.53 px/tick to Scout's
7.93 over **one shared graph**, so an arc is only honest if the slowest class
asked to fly it can.

**The run-up is a property of the map.** A one-column perch — the kind
`ExtractNodes` emits wherever a character can stand three-quarters off a
ledge — offers no run-up at all in the direction it overhangs, so every arc off
it starts from a standstill; `ctf_oldfort`'s n41 is one, and a standing Heavy
covers 75 px of its arc.

The double-jump fan is written for a Scout and the rocket-jump fan for a healthy
Soldier, because the search refuses those edges to every other class — so the
slowest class that can fly one is the only class that can. The arc geometry
still uses `NAV_JUMP_VX` in all three: that constant is also the per-sample
catch-up budget in the clearance walk.

### The three later fans are purely additive, and must stay that way

Every jump fan keeps a fixed number of candidates per side. So a stage that could
feed an *earlier* fan would mean a **different set** of ordinary jumps kept, not a
superset — on every map, for every class, to add a manoeuvre one class can
perform. Hence: each of the double-jump, rocket-jump and sticky-jump fans runs
after the ones before it, keeps its own quota, and reads an adjacency snapshot
taken **before any of the three started**.

**The rocket fan's filter is inverted against the double jump's, deliberately.
Do not harmonise them.** The double jump exists to reach what is otherwise
unreachable, so it skips anything already joined within `NAV_DJ_SKIP_HOPS` (3) and
prices itself out of ordinary routes at `NAV_DJ_PENALTY` 40. Rocket jumping is a
*variety* feature — measured, it opens about one extra place per team-map over
the double jump and changes objective reachability on zero maps — so
`NAV_RJ_SKIP_HOPS` is **1** (a two- or three-hop shortcut *is* the feature) and
`NAV_RJ_PENALTY` is low enough that the search actually takes them.

`docs/TRAPS-BOTS.md` has the measurements behind each of those numbers.

## Running it

```powershell
dotnet run --project src/GG2.Host -- navgen                # every map in maps/
dotnet run --project src/GG2.Host -- navgen --map koth_valley
dotnet run --project src/GG2.Host -- navgen --map .\mymap.png    # a custom map, by path
dotnet run --project src/GG2.Host -- navgen --map cp_dirtbowl --area 2
dotnet run --project src/GG2.Host -- navgen --check        # did anything I changed move a graph?
```

**One command does the whole cycle**: it writes the shipped `.txt.gz`
container, deletes the plain `.txt` if one is there, writes the `<key>.dist.gz`
next-hop tables the exact A\* heuristic reads, and rewrites `nav/manifest.tsv`.

**The tables are written by default and a graph change invalidates them.**
They are keyed to a hash of the graph's own nodes and edges, so regenerating a
graph without its table leaves a file the server refuses at load — it then
derives instead, which costs 377 ms rather than 62 ms at the map change and is
logged. `--no-distances` skips them deliberately; `--check` reports
`tables STALE` per map. `docs/TRAPS-BOTS.md`, "Shipping the tables".

**A graph identical to the one already on disk is not rewritten**, so
`git status` after a run names the graphs a source edit actually moved — which is
the question a regeneration is asked to answer. `--check` answers it without
writing at all, and exits 1 when something differs.

**`--out` defaults to the nearest `nav/` at or above the working directory**,
not to the server's own `ResolveNav`. That probe walks up from the *executable*,
and `nav/` is copied into the build output — so the server's answer would write
graphs into `bin/Debug/net10.0/nav`, leave the checked-in ones untouched, and
report success. `GG2_NAVCACHE` overrides both.

**`--sticky` writes `NAV_EDGE_STICKYJUMP` (type 7) edges and the Game Maker
game must never load them.** Its `navFindPath` takes `canDouble` and `canRocket`
and has **no `canSticky`** — a type-7 edge in its cache routes every class over an
arc only a Demoman can fly, forever. This port's `NavGraph.EdgeRoutable` refuses
them by class, which is the whole reason the stage may exist; the default staying
off is what keeps a file generated here safe to hand to the game.
`NavGeneratorTests.NoShippedGraphCarriesAStickyEdge`.

### What it costs

Measured 2026-09-03, Release, on this machine. Say which build: Debug is
roughly 2x.

| | |
|---|---|
| all 25 shipped graphs | **3.8 s**, one process, single-threaded |
| the largest map (`cp_dirtbowl`, 835 nodes) | **~145–190 ms** a stage |
| a mid-size map (`ctf_truefort`, 687 nodes) | **~86 ms** |
| `gg_debug` (52 nodes) | **3 ms** |

## On the fly, in the server

`BotPopulationService.MapChanged` builds a graph for a map that has none. **On by
default**; `--no-navgen` turns it off.

**It runs on the tick thread, at the map change.** That is deliberate
placement: a map change is the one moment nothing is being simulated and every
client is loading, so a 20–190 ms stall disturbs nothing — where a background
build would have to hand a graph to bots that had already planned routes without
one. The elapsed time is logged, so the cost is visible rather than inferred from
a hitch:

```
built a nav graph for mymap area 1 in 86ms — 687 nodes, 6747 edges
```

**The graph is held in memory and not written anywhere**, unless
`--navgen-save` says so. A server putting files in an operator's install
directory because it rotated onto a map is a surprise; paying the build once a
rotation is not. `--navgen-save` is worth it on a rotation that is mostly custom
maps.

**One asymmetry, and it is the interesting half.** A graph **refused by the
manifest** — one built against different geometry — is rewritten *even with
saving off*. That file describes terrain that is not there: leaving it means every
restart refuses it and rebuilds, and any other tool reading the directory still
gets the wrong graph. A map that simply never had a graph is not a fault to
correct, so its generated graph stays in memory.
`NavGenerateOnMapChangeTests.OverwritesAGraphThatNoLongerFitsItsMap`.

**A build failure is not fatal.** A map this generator cannot handle logs and
falls back to the degradation `NavCache.TryLoad` already promises — bots that do
not navigate — because that is strictly better than refusing to serve the map.

### `eval` does it too, and there it matters more

`gg2-server eval` is where a bot change is measured, so "the map had no graph"
is the one thing it must not quietly be measuring. It builds a missing graph the
same way and the nav column says `built` rather than `ok`.

Measured 2026-09-03, two custom CTF maps (`ctf_atalia`, `ctf_doublecross`),
4 matches each, 12 bots, the default 36000-tick ceiling:

| | red | blue | drew | unfinished |
|---|---|---|---|---|
| `--no-navgen` (no graph) | 0 | 0 | **8** | 0 |
| graph built | 3 | 1 | 4 | 0 |

**Look at the "unfinished" column.** With no graph every match is a 0-0 draw at
the time limit — a **finished** match, so any "N completed" criterion is satisfied
by a map on which nothing happened. That is CLAUDE.md's "a match that ends is not
a match that happened", and it is why the nav column exists at all.

## Staying in step

- **The constants are `NavConstants` and `BotConstants`**, transcribed from
  `Constants.xml` and pinned by `ConstantsTests`. `gg2-nav-gen` reads the XML at
  run time; this cannot, because it ships without a checkout — the test is what
  replaces that.
- **Three of them are `BOT_*` rather than `NAV_*`**: `BOT_RUNUP_CELLS`,
  `BOT_RUNUP_HOPS` and `BOT_JUMP_VTOL`. The follower reads them to decide how far
  back to walk before a jump and `NavCharacter` reads them to decide whether the
  resulting arc is real, so this is the one place the generator and the bot must
  agree on a number that is not about geometry. Change one and the graph and the
  bot stop describing the same jump.
- **The four collider sprite sizes are restated** in `NavInstances`, because
  `GG2.Nav` deliberately does not reference `GG2.Simulation`. `NavInstanceTests`
  asserts they agree with `ColliderSprites` and that the reference's sprite XML
  still declares origin (0, 0) with a full-image RECTANGLE mask.
- **The field layout is versioned.** `NAV_CACHE_VERSION` is 15; `NavCache.Parse`
  refuses a file that declares anything else rather than reading columns that mean
  something else. Bump it whenever a field moves.
- **`GG2.Nav` does not reference `GG2.Bots`.** Its product is the cache file's
  text, and the caller turns that into a `NavGraph` with `NavCache.Parse` — the
  same call a file off disk gets. So an on-the-fly graph and a generated file
  are the same graph by construction rather than by two code paths agreeing.

## What is still in `gg2-nav-gen`

The Node generator still exists as its own repository and still works. What is
there and not here:

- a worker pool, so a full rebuild uses every core (this is single-threaded and
  takes 3.8 s, which has not been worth fixing);
- `--md5`, which appends a custom map's file MD5 to the cache key the way
  `serverGotoMap` does. This port keys on the map name alone, which is what
  `NavCache.Key` has always done on the reading side;
- reading `Constants.xml` at run time rather than through `ConstantsTests`.

**`tools/compress-nav.sh` is only for that generator's output now.**
`navgen` writes the shipped container itself.

## One thing that was measured on the way in

**`gg2-nav-gen` derives a collider's bounding box from the raw entity
coordinates; GM8 rounds an instance's position first**, and so does this port's
own `SolidRect.FromEntity`. The two differ by up to a pixel on a *fractional*
scale — a 60x6 bar at yscale 0.3 ends at 607.8, not 608 — which can put a wall in
a different nav cell from the one the physics puts it in.

Measured 2026-09-03: rounding changes nothing on any of the 25 shipped graphs,
so the two were free to differ and did. `NavInstances` rounds, because that is
what the reference's own `navMarkInstances` read (it ran inside GM8, against
live instances) and because it means a custom map cannot have the graph and the
physics disagreeing about where a wall is. `NavInstanceTests` pins the
agreement.
