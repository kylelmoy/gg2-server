# Proposed C# architecture

Shaped by the two goals in `README.md`: performance headroom for much heavier
bot logic, and a faster loop for writing and testing that logic. The reference
implementation's structure is an artefact of Game Maker 8 and carries no weight
here.

## The one rule everything else follows from

**The simulation is a pure function with no I/O in it.**

```csharp
void Tick(WorldState state, ReadOnlySpan<PlayerInput> inputs);
```

No sockets, no clock, no `async`, no logging that allocates, no `DateTime.Now`.
Everything the tick needs is in `state` and `inputs`; everything it produces is
in `state` and an out-going event list.

That single constraint buys all four things this project needs:

- **Testability.** `docs/PARITY.md`'s trace replay is just calling `Tick` in a
  loop with no server anywhere. Unit tests are microseconds.
- **Speed of iteration.** Bot changes are evaluated by running thousands of
  simulated ticks with no rendering, no network and no real time elapsing.
- **Determinism.** No scheduler, no thread interleaving, nothing to make two runs
  of the same trace differ.
- **Throughput.** Independent matches are independent `WorldState`s and can run
  on separate cores with no shared mutable state.

Everything below is in service of not breaking that rule.

## Projects

```
Gg2.Protocol      framing, message codecs, the constant table.  Pure, no I/O.
Gg2.Maps          walkmask / level-data decoding, entity extraction.  Pure.
Gg2.Simulation    physics, characters, weapons, projectiles, gamemodes.  Pure.
Gg2.Bots          bot input generation.  Pure. Depends on Simulation for reads.
Gg2.Server        sockets, tick loop, session lifecycle, config.  The only I/O.
Gg2.Parity        trace recorder, trace replayer, drift reporting.
Gg2.Tests         xunit; unit tests plus the parity fixtures.
Gg2.Host          console entry point.
```

`Gg2.Simulation` must not reference `Gg2.Server`. Enforce it with project
references and keep it that way — the moment the physics can read a socket, the
parity harness stops being able to run it.

⚠️ **`Gg2.Server` references `Gg2.Bots`, and not the other way round.** The bot
*population manager* is server-side in the reference (`botPopulationUpdate` is
called from `GameServer`'s Begin Step) because only the server has a roster, a
config file and a socket to broadcast a join on — so `Gg2.Server.BotPopulationService`
drives `Gg2.Bots.BotPopulation`'s decisions. `Gg2.Bots` stays pure and gains
nothing from the edge, and the rule above is untouched. The `IInputSource` seam
still means the *simulation* never special-cases a bot.

## Threading

- **One thread owns the tick**, at a fixed 30 Hz. It never blocks on I/O.
- **Socket I/O lives on its own threads** (or the thread pool, via
  `SocketAsyncEventArgs` / `PipeReader`). Inbound bytes are parsed into
  `PlayerInput` records and pushed onto a single-producer queue per connection;
  the tick thread drains them at the top of each tick. Outbound, the tick thread
  writes one broadcast buffer and hands it to the I/O side to fan out.
- Because the server sends **one identical byte stream to every client**
  (`PROTOCOL.md`), the fan-out is a single buffer written to N sockets — not N
  serialisations. Keep it that way; it is why the send path is nearly free.
- Nothing in the simulation touches a lock.

## Data layout

Characters are the hot data. Prefer a flat structure indexed by roster slot over
an object graph:

```csharp
// One of these per world, sized to maxPlayers.
struct CharacterState {
    public double X, Y, HSpeed, VSpeed, AppliedGravity;
    public double Hp;
    public byte   KeyState, PressedKeys, ReleasedKeys, MoveStatus;
    public ushort NetAimDirection;
    // ...
}
```

`double` everywhere in the physics — see `SIMULATION.md`, "Float rules". A
`float` is a bug, not an optimisation.

The tick should allocate nothing. Pool the broadcast buffer, use `Span<byte>` for
serialisation, keep projectiles in a free-list-backed array rather than a `List`
of class instances. This matters less for 16 players than it will for whatever
the bot layer eventually wants to do per tick, and retrofitting it is miserable.

## Collision

The single hottest call in the server is `placeFree`. Design it deliberately:

- The walkmask is a **bitset** — `ulong[]` with a row stride, one bit per mask
  cell, one cell per 6 × 6 world pixels. A character rectangle spans a handful of
  cells; the test is a few masked word reads — `Walkmask.AnySolidInBox` — or,
  for the shapes a character box actually produces, a single bit read from a
  precomputed erosion table.
  ⛔ **That last clause was aspirational until 2026-09-02.** The layout was always
  the bitset; every consumer was a cell-by-cell loop calling `IsSolid`, in **four
  separate copies**. Nothing could fail, because the loop was correct — see
  "Where a tick actually goes" below.
- Solid rectangles (gates, player walls, platforms) are a small array of AABBs
  tested afterwards.
- **Solidity is a query filter, not global state.** The reference toggles a
  `solid` flag on every obstacle before each check and clears it after
  (`charSetSolids`/`charUnsetSolids`) because GML gave it no better option. Pass
  a filter instead:

```csharp
readonly struct CollisionFilter {
    public readonly Team Team;
    public readonly bool CarryingIntel;
    public readonly bool IgnoreDropdown;   // KEY_DOWN held
}

bool PlaceFree(in Rect box, double x, double y, in CollisionFilter filter);
```

This is both faster and clearer, and it makes gate semantics — which depend on
the asking character's team *and* whether it carries intel — a parameter instead
of a hidden mutation.

## Where a tick actually goes

**Measured 2026-09-02, Release, 24-core box, `ctf_truefort`, soldiers.** ⚠️ **A
wall-clock figure on one machine — re-measure before quoting, and say which
build.** The probe is a throwaway; what survives is the shape.
⚠️ **These are the CURRENT figures, after both the word path and the erosion
tables.** The two sections below give the before/after for each.
⛔ **The tick row and the sweep under it come from a ScenarioWorld holding twelve
LIVE characters that are standing still** — read it as the cost of twelve live
characters, not of a match. A static scenario is not a workload; see the memo
correction below.

| | cost |
|---|---|
| `CollisionWorld.PlaceFree` (character box + filter) | **12.0 ns** |
| `Walkmask.AnySolidInBox` alone | 7.0 ns |
| `Movement.GoodMoveContactSolid`, a 10px fall | **145 ns** — about 13 `PlaceFree`s |
| `MatchSimulation.Tick`, 12 live characters | **3.7 µs** |
| `WorldState` whole-struct copy (156,624 bytes) | 2.2 µs |
| a `RolloutSearch` decision, 60 candidates × horizon 20, population 12 | **4.8 ms** |

### The three things this settles, because each one was assumed wrong first

⛔ **1. The tick is per-CHARACTER, not per-slot.** Sweeping the population 2→32
gives ~0.45 µs a live character and a fixed cost of **0.26 µs** — about 5% at
twelve. The nine `for (slot = 0; slot < MaxSlots; slot++)` passes over 64 mostly
empty slots are **not** worth compacting; that was the first hypothesis and it is
wrong by an order of magnitude.

⛔ **2. `PlaceFree` is the leaf, and the crawl is the multiplier.**
`good_move_contact_solid` asks it **once per pixel of travel**, so a character
moving ten pixels pays thirteen box tests on that axis alone. This is why
`placeFree` is "the single hottest call in the server" and why the mask layout
was chosen — the cost is the call count, not the call.

⛔ **3. The `WorldState` copy is NOT where a search spends its time.** At
population 12 a candidate is 2.2 µs of fork against 20 ticks at 3.7 µs — the fork
is about **3%**. ⚠️ **`BENCH_SEARCH` prints a per-candidate intercept that was
labelled "fork" and read 27 µs**, an order of magnitude above the actual copy,
because the intercept also carries the zoom check, the decoder and the aim-error
scan. **Shrinking `WorldState` buys ~3% of a candidate, not ~25%.** The label is
corrected and `tools/bench/Search.cs` says so at the call site.

### What the word path bought, and what it did not

Switching all four cell loops to `Walkmask.AnySolidInBox`:

| | before | after | |
|---|---|---|---|
| `PlaceFree` | 24.5 ns | 13.9 ns | **−43%** |
| `GoodMoveContactSolid`, 10px | 310 ns | 177 ns | **−43%** |
| `MatchSimulation.Tick` @ pop 12 | 4.57 µs | 3.74 µs | **−18%** |
| search decision @ pop 12, h20 | 6,364 µs | 4,795 µs | **−25%** |

⚠️ **The tick figure held across the whole population sweep** (2, 8, 12, 16, 24,
32), all six in the same direction at 8–24%, and the search figure held across all
five horizons — which is why these are quoted and the one below is not.

⛔ **It is NOT measurable in the twelve-scripted-bot server tick, and that is
reported rather than buried.** `TwelveBotsRunWellUnderTheTickBudget`, interleaved
Release runs: **before 0.172–0.190 ms, after 0.151–0.178 ms.** The distributions
overlap; a single before/after pair read 0.190 → 0.174 and would have been written
down as −8%. **That figure is dominated by the scripted bot layer — nav, targeting,
objectives — not by collision**, so a collision win is invisible in it by
construction. ⚠️ **CLAUDE.md's 0.151 ms is inside this spread, so it is a sample
from it rather than a number that moved.**

### Why this is the lever for the bot layer

A rollout search re-simulates every live character for every tick of every
candidate, so **the simulation tick is multiplied by candidates × horizon** — 1,200
at the shipped settings. **Anything taken off `MatchSimulation.Tick` is taken off a
search decision at 1,200×**, which is the opposite of the scripted-bot case above,
where the same saving disappears into the bot layer's own cost. That asymmetry is
the whole reason to look here.

⚠️ **The scripted case has its own lever and it is NOT the tick** — see "Where a
headless bot match's tick goes" below. In a twelve-bot match the simulation is
38% and one A\* search is 30%, so a saving on `MatchSimulation.Tick` pays a
rollout search 1,200× and pays a scripted bot barely at all.

### ⛔ Where `PlaceFree` calls actually come from — and why the crawl is not the target

**Measured 2026-09-02 over 2,940 ticks of a real bot match** (`ctf_truefort`, 12
bots, Release), attributed by calling method:

| caller | share | what it asks |
|---|---|---|
| `CharacterPhysics.Step` | **28%** | the move target, and `y+1` |
| `CharacterPhysics.BeginStepInput` | **28%** | stuck-in-wall at `(x,y)`, obstacle below at `y+1` |
| `CharacterPhysics.UpdateSpinjump` | **14%** | rubbing a wall at `x±1` |
| `CharacterPhysics.BeginStepAccelerate` | **14%** | `y-0.1`, the surf check |
| `CharacterPhysics.EndStep` | **10.5%** | the stair climb — `y+6`, `y+7`, `y+12`, `y+13` |
| **`Movement.GoodMoveContactSolid`** | **4.0%** | the crawl |
| `CharacterHitObstacle.Resolve` | 1.6% | the four escapes |

⛔ **The crawl is 4-7% of the calls, not the bulk**, and it averages **4.6-5.0
`PlaceFree` calls** — not the ~13 a 10 px benchmark suggests, because a character
moves a few pixels in a tick, not ten. **Eliminating the crawl entirely would buy
under 7%.**

⛔ **The other 84% are GM8's own per-character-per-tick probes, and they are the
specification.** Each names a distinct position — `(x,y)`, `y+1`, `y-0.1`, `y+6`,
`y+7`, `y+12`, `y+13`, `y-gravity`, `x±1`, `(x+hspeed, y+vspeed)` — so there is no
redundancy to remove: they are different questions, and the reference asks all of
them. ⚠️ **One exception, and it is small**: `UpdateSpinjump` re-asks `y+1`, which
`BeginStepInput` already stored in `CharacterState.ObstacleBelow`. It sits behind
an early return that only fires while spinjumping, so it is rare — and reusing the
stored value would need proof that nothing moved the character between the two
stages.

### ✅ The erosion tables: precomputing the box test, because the SHAPE barely varies

⛔ **`Walkmask.AnySolidInBox` is a table lookup for nine of the ten character
classes now**, one bit read instead of a word scan per row.

**The observation that makes it cheap.** A character box's *cell rectangle* is
fixed by its pixel size and the sub-cell phase of its position, so each class
produces at most **two widths and two heights** — enumerated over all 36 phases,
13 px wide is ALWAYS 3 columns, 19 px is ALWAYS 4, and only 15 px varies. So a
handful of shapes answers nearly every query. **Measured over 2,940 ticks of real
bot matches: four shapes (3x6, 4x6, 3x7, 4x7) cover 86.7%, 88.4% and 94.5% of all
mask queries on three maps**, with 0.02% off-map. Pyro's two (3x5, 4x5) were added
so every class in the bot draw is covered; only Quote falls through.

⛔ **At CELL resolution, which is the whole reason it fits.** One bit per possible
top-left cell is the same footprint as the mask — **about 20 KB a shape, 120 KB
for six** on `ctf_truefort`. **A per-PIXEL table would be 36x that**, would not
stay in cache, and is the version of this idea that does not work.

**Built by a separable sliding OR** — a horizontal pass over `w` columns, then a
vertical pass over `h` rows of that — so it is linear in cells. ⚠️ **1.5 ms a map
warm, 2.7 ms cold.** The first version re-scanned the whole window at every
position and cost 6x that. `HeadlessMatch.Load` does NOT cache maps, so this is
paid per match: ~2% of `eval`'s per-match budget.

| measurement | word scan | erosion table | |
|---|---|---|---|
| `AnySolidInBox` | 8.9 ns | **7.0 ns** | −21% |
| `PlaceFree` | 14.2 ns | **12.0 ns** | −16% |
| `GoodMoveContactSolid`, 10 px | 180 ns | **145 ns** | −19% |
| `MatchSimulation.Tick` @ 12 chars | 3.83 µs | **3.65 µs** | −5% |
| **search decision, per-horizon slope** | **258.6 µs** | **234.8 µs** | **−9.2%** |

⚠️ **Interleaved A/B in ONE binary behind a switch, ON/OFF/ON/OFF**, so machine
drift lands on both arms — the mask, `PlaceFree` and crawl ranges do not overlap.
⚠️ **The per-horizon SLOPE is the statistic to read for the search**, not the
single horizon-20 reading: it is a regression over five horizons, where the
one-point numbers overlap between arms.

⛔ **A wrong bit here is a wrong collision answer** — a character through a wall,
or stopped by nothing. The lookup is exact rather than conservative, so there is
no "maybe" path to fall through to. `WalkmaskBoxTests` walks every tabulated shape
across every legal position on three maps and **mutation-checks four ways**:
horizontal window short (7 red), vertical window short (7), shape-index collision
(3), bounds test missing an edge (3).

⛔ **Equivalence alone cannot catch a table nothing reaches** — a set of shapes no
query matches is green and slow. `IsTabulatedShape` is public for exactly that
test, which asserts the shapes real boxes produce ARE the tabulated ones.

⚠️ **The shape set was WRONG when it was reasoned about, and the test control
caught it.** The first version excluded Heavy on a hand-derived claim that a 19 px
box spans 4-5 columns; it spans exactly 4, so the control — "a class left out must
not be fully covered" — failed. **Enumerate the phases; do not derive them by
hand.**

### ⛔ Binary search on the crawl is UNSOUND, and the hitscan analogy is where it breaks

**The question comes up because `good_move_contact_solid` steps one pixel at a
time and a sniper's hitscan does not.** The two are different queries:

- A **hitscan** asks *"is anything solid on this segment?"* — **disjunctive**, no
  ordering. A conservative skip (the `Walkmask.Clearance` field, which
  `ObservationWriter`'s ray fan uses) can jump ahead by a distance proven to
  contain nothing, and the answer is unchanged.
- The **crawl** asks *"where is the FIRST obstruction?"* The free/blocked
  predicate along the path is **not monotone** — a character can pass a pillar and
  land in open space beyond it. ⛔ **A binary search assumes free-then-blocked and
  would tunnel straight through.** That is not a parity risk; it is wrong.

✅ **The sound member of that family was built and measured, and it is worthless.**
A swept-AABB pre-test — the union of the start and end boxes, tested once — is
conservative (a straight-line sweep of an AABB is contained in the AABB of its
endpoints) and would be **bit-identical**, because the elision is
`clear || PlaceFree(...)`: the loop's arithmetic never changes, only a predicate
known to be true goes unevaluated. ⚠️ `RoundPosition` is half-to-even and
`floor(v) <= round(v) <= ceil(v)`, so flooring and ceiling the endpoints brackets
every rounded position with **no margin** — and a margin is not free, see below.

⛔ **The swept path is clear on 0.9-3.3% of crawls.** Measured across three maps.
That is worth **~0.1% of all `PlaceFree` calls**, and the reason is structural
rather than incidental: **gravity puts the destination inside the floor.** A
grounded character carries a downward velocity component every tick, so the end
box overlaps the ground it is standing on — and stopping it there is exactly what
the crawl is for. **A clear sweep means airborne with nothing nearby, which is the
minority of ticks.**

⚠️ **A 1-pixel safety margin took the clear rate from 3.3% to 2.9%** by pushing the
swept box into the floor underfoot. Recorded because the margin looks free and is
not.

### ✅ Map data is decoded ONCE per process, and where it deliberately is not

⛔ **`HeadlessMatch.Load` re-decoded the map and re-parsed the nav graph on every
match.** Measured 2026-09-02: **18.0 ms against 283.5 ms to play a 9,000-tick
match — 6% of every match**, paid again for bytes that had not changed. It goes
through `ScenarioResources.Shared` now: **18.36 ms → 0.08 ms** on the second ask,
a 230x cut.

⚠️ **Most of the 18 ms is the NAV GRAPH, not the PNG.** A warm
`LevelMap.FromPng` + `ForArea` is 2.78 ms; the rest is nav text. Anyone attacking
load time should start there.

**Holding everything in memory is cheap**, which is what makes this a
non-question: **23 decoded maps are 9.0 MB** (about 400 KB each) and **23 nav
graphs 5.7 MB** — call it 15 MB for every shipped map and its graph, against a
156 KB `WorldState` per forked candidate.

⛔ **Only the deeply immutable half is shared, and that division is the entire
safety argument.** `LevelMap` and `NavGraph` are `init`-only throughout;
**`CollisionWorld` is NOT** — `SetCaptureZones` writes to it — and neither is
`SpawnPoints`. `HeadlessMatch.On` builds both per match, at 0.009 ms, so there is
nothing to gain by widening the cache and a whole class of irreproducibility to
lose. `SharedMapDataTests` gates the split in both directions: **sharing the
`CollisionWorld` too fails it, and reverting to an uncached load fails it.**

⚠️ **The cache is NEVER INVALIDATED**, so a map edited on disk mid-process is not
served again. **That is right for a batch runner and wrong for a long-lived
server** — and the server does not use this path. `GameServer` still calls
`LevelMap.FromPng` on rotation, deliberately.

⛔ **Do not assert on `ScenarioResources.Shared.Count` in a test.** It is
process-wide, so whether an ask adds an entry depends on which tests ran first — a
draft of `SharedMapDataTests` did, passed alone and failed in the suite, *including
its control*. **Reference identity (`Assert.Same`) is the stronger claim anyway**
and holds whatever else the process has done.

⚠️ **Is the live server worth caching too?** It would save ~18 ms once per map
rotation, which fits inside a single 33.3 ms tick and drops no frame — **no
measured problem**, against giving up re-reading an edited map. The memory is not
the obstacle; the absence of a symptom is. **What would change this: a rotation
spike showing up in `--tick-stats`.**

### ⛔ Where a headless bot MATCH's tick goes — and A\* is the biggest thing here

**Measured 2026-09-02, Release, net10.0, 24-core box, `ctf_truefort`, twelve
scripted bots, four headless matches of 9,000 ticks each.** In-process
`Stopwatch` timers around named stages; the instrumentation is throwaway and its
own overhead measured **0.05 µs of the 6 µs tick**, because `GetTimestamp` here
is an rdtsc-backed read of about 3 ns.

⚠️ **This is a different quantity from the sweep above**, which is twelve live
characters standing still in a `ScenarioWorld`. This is a match: bots think,
people die and respawn, projectiles fly.

| | µs a tick | share |
|---|---|---|
| **the bots' `Poll`** | **9.24** | **61%** |
| `PathFollower.Step` | 5.7 | 38% |
| — of which **`NavSearch.Find`** | **4.5** | **30%** |
| `BotCombat.Step` | 3.0 | 20% |
| — of which `KeepOrDropTarget` + `LookForOne` | 0.87 | 6% |
| `UpdateObjective` | 0.58 | 4% |
| the rocket dodge, the query rebuild, the holds | 0.78 | 5% |
| **`MatchSimulation.Tick`** | **5.72** | **38%** |
| the Begin Step character loop (with `Generators.BeginStep`) | 1.67 | 11% |
| the Step character loop (with `Rounds`/`Arena`) | 1.02 | 7% |
| `Projectiles.Collide` | 1.01 | 7% |
| `Projectiles.Move` | 0.65 | 4% |
| `Timers.Advance` | 0.49 | 3% |
| `MovingPlatforms.EndStep` + the End Step loop | 0.36 | 2% |
| `Intel.CollisionStep`, `TickEvents.ClearOutputs`, cabinets, the rest | 0.62 | 4% |
| the command drain, `_events = default` | 0.17 | 1% |
| **total** | **15.13** | |

⚠️ **These are the figures the analysis below rests on, and the total has since
moved**: the two `NavSearch` wins and the line-walk skip take it to **13.96 µs**.
The shares are what matter here, and they are the shares *before* any of it.

⛔ **`NavSearch.Find` alone is 30% of the whole system** — more than every stage
of the simulation put together bar the two character loops. **That is where a
bot-layer performance question starts**, and nothing in this repo said so before
2026-09-02: collision had been attacked four times over and the A\* underneath
the path follower never once.

**One search costs 15.2 µs and pops 203 nodes**, at 0.245 searches a tick — a bot
re-plans every 45-89 ticks (`BotConstants.ReplanTicks` plus its own stagger) and
each search expands about a third of a 687-node graph.

⛔ **The heuristic is NOT the problem, which was the first guess.** Measured over
a thousand searches: the straight-line estimate is **0.914 of the true path
cost**. It is tight, the units of `NavEdge.Cost` and the cell-space distance
really do agree, and A\* is not degenerating into Dijkstra. **Do not go looking
for a scale error here.**

⛔ **A quarter of a search is the priority queue.** A faithful microbenchmark of
exactly the heap traffic one search does — 475 enqueues and 425 dequeues on a
`PriorityQueue<int, double>`, interleaved the way the frontier interleaves them —
costs **6.84 µs against the search's 29 µs**. ⚠️ **Replacing it is not free**:
.NET's is a 4-ary heap at about 7.6 ns an operation, already near the floor, and
a hand-written one would have to reproduce its tie-breaking exactly or two
equal-cost routes would swap. On a symmetric map that is a real event, and it
would move bot behaviour for no stated reason.

### ⛔ The .NET sampling profiler was wrong THREE times here. Use in-process timers.

`dotnet-trace --providers Microsoft-DotNETCore-SampleProfiler` on this workload
produced confident, large, wrong numbers, each then settled against a direct
timer or an A/B:

| the sampler said | it actually is | how it was settled |
|---|---|---|
| `Buffer.ZeroMemoryInternal` is **63%** of an `eval` run | every memset-shaped thing in the tick is **under 2%** | stage timers — `_events = default` on a 6,160-byte `TickEvents` is **0.067 µs** |
| `Projectiles.Collide` is **27.6%** of the run | **6.7%** — 1.01 µs of a 15.13 µs tick | stage timers summing to what the outer timer reads |
| `Reconstruct`'s memmove is **33%** of A\* | **1.2%** | removed the memmove entirely and re-measured |
| `PriorityQueue.RemoveRootNode` is **0.87%** of A\* | the heap is **24%** | a microbenchmark of the same operation counts |

⚠️ **Its ORDERING of managed methods was roughly right** — it put `NavSearch.Find`
at 31% of an `eval` run against a measured 30%, and that is what pointed this
session at A\* in the first place. **Its magnitudes below that were not.** The
pattern is that leaf attribution lands on runtime helpers — `memset`, `memmove` —
that are not where the time is.

⛔ **So: use the sampler to pick the method, a `Stopwatch` around named stages to
size it, and an A/B to confirm anything you change.** The check that makes a
stage table trustworthy is cheap and was applied here: **the stages must sum to
what an outer timer reads around the whole call** — 6.009 against 6.04 µs.
⚠️ And the trap `3aa644e`'s memo number hit applies to any A\* bench too: a
reading taken before the JIT has tiered up says **57-70 µs** for the same search
that settles at **15.2**.

### ✅ Two exact-equivalence wins in `NavSearch.Find`, and four things that measured nothing

**Landed 2026-09-02.** Both are bit-identical by construction and gated by
mutation: forcing `hasBlocked` false reddens 2 tests, `hasOccupancy` false
reddens 1, and dropping the `Reverse()` reddens 41.

- **The blacklist and occupancy lookups are hoisted out of the edge loop.** Both
  collections are nearly always empty — a bot's blacklist averaged **0.15
  entries** a search, and the occupancy map is null whenever one team fields the
  bots — and an empty one still cost the key arithmetic plus an
  interface-dispatched lookup on each of about 2,000 edges a search.
- **`Reconstruct` appends and reverses** instead of `path.Insert(0, …)`, which
  was quadratic in the route length: about two thousand element moves to produce
  a sixty-node route.

⚠️ **Interleaved A/B, both arms twice, Release**, over the same four 9,000-tick
matches:

| | HEAD | after | |
|---|---|---|---|
| one A\* search, 1,000-pair bench | 30.9 µs | 29.3 µs | **−5.0%** |
| the bots' `Poll` | 9.24 µs | 8.90 µs | **−3.7%** |
| the whole tick | 15.13 µs | 14.75 µs | **−2.5%** |

✅ **The three pinned bot measurements did NOT move, and here that is the expected
result rather than an absent alarm** — the change is meant to be bit-identical,
and the A\* bench moving 5% is the independent evidence that the edited code is
reached at all. CLAUDE.md's "a pin that does not move is equally an alarm" cuts
that way only for a change meant to alter behaviour.

⛔ **Four things measured and NOT taken**, each built and measured rather than
reasoned about:

- **A compact 16-byte search-side edge array** — A\* reads four of `NavEdge`'s
  nine fields and the struct is 56 bytes. **~1%**, isolated by an interleaved A/B
  against the other change. Rejected: a second representation of the edge set,
  and a second place the routability rule could drift, for one percent.
- **Precomputed per-node heuristic centres**, so a relaxation reads two doubles
  instead of loading a `NavNode`: **0.0%**. Rejected.
- **`Projectiles.Collide`'s `stackalloc ProjectileHandle[512]` sized to the live
  count instead** — 4 KB of stack zeroed every tick, including the many ticks
  with nothing in the air. It read as a 3% win on one pass and **did not survive
  an interleaved A/B**: nine alternations put it inside the drift. Rejected, and
  the code is unchanged.
- **`BotTargeting.Find`'s two `stackalloc`s**, 1.5 KB a bot a tick. Shrunk to 8
  entries as a measurement — wrong behaviour, right question — and the tick did
  not move.

⚠️ **The last two confirm the note below that the 512-element `stackalloc`s "look
alarming and are not"**, and together with the sampler's three misses they have
one shape: **zeroing is not where this program spends its time.**

### ✅ The line walk skips open ground, which was the largest single win here

**Landed 2026-09-02, after the A\* work above.** `CollisionLine.Walk` samples the
mask **once per pixel of the segment**, which is GM8's own rule and is not
negotiable — but *which* of those samples get asked is.
<see cref="Walkmask.Clearance"/> already existed for the observation ray fan, and
now the walk reads it too: a sample standing on a cell of clearance `c` may
advance `6c - 7` pixels, because every pixel that close is known to be open.

⛔ **The reason it is worth so much is `BotArc.Clear`.** A soldier or demoman with
a target tests its ballistic arc for line of sight — ten segments spanning a
rocket's whole flight, hundreds of pixels of mostly empty air. **Measured at
1.25 µs a call and 1.06 calls a tick: 9% of the entire tick from one call**, and
10.5 of the 10.5 `CollisionLineBlocksBullets` calls a tick were its. The mask
walk was 71% of that; the solid-rect loop is the other 29% and is unchanged.

⚠️ **Interleaved A/B, six alternations, Release**, twelve bots on `ctf_truefort`.
**Every reading of one arm sits below every reading of the other**; the figures
are the minima, and the means agree:

| | before | after | |
|---|---|---|---|
| the bots' `Poll` | 8.89 µs | 8.11 µs | **−8.8%** |
| `MatchSimulation.Tick` | 5.94 µs | 5.65 µs | **−4.9%** |
| the whole tick | 15.01 µs | 13.96 µs | **−7.0%** |
| `eval`, 16 serial matches | 8.30 s | 7.60 s | **+9.2% throughput** |

✅ **The simulation gains too, which was not the target** — a sentry's line of
sight and a hitscan weapon go through the same walk. It is a smaller share there
because those lines are short.

### ⛔ The gate: `CollisionLineSkipTests`, and why the parity fixtures are not it

**113 parity fixtures pass and prove nothing about this.** A hitscan in a
recorded trace is a short line between two people in a firefight; the skip only
engages over open ground, and an over-long one would step over a thin ledge in
the middle of a map no trace ever shoots across. **This is CLAUDE.md's "a green
fixture is not confirmation of a box", and it applies to a whole optimisation
rather than to one edge.**

⚠️ **The control is the production walk with a mask that refuses to promise**, not
a transcription of it — a struct answering `IsSolid` identically and `0` from
`SafeSkipPixels`, which is the documented "walk pixel by pixel" answer. So the
two runs differ in the skip and nothing else, and there is no second copy of the
rounding, the major-axis choice or the cell mapping to go stale. 20,000 segments
a map over three maps, endpoints swept across the whole map and 20% beyond it, at
three reaches.

⛔ **The witness is a PROBE COUNT, not a threshold.** Two runs that agree prove
nothing if the fast one never skipped, so the counting control asserts the
skipping walk probes the mask **less than half** as often. A first draft asserted
"more than 10,000 of 20,000 segments could skip" and failed on `cp_dirtbowl` at
8,603 — **the fix was to state the property rather than lower the bar**, which is
the same trap as fitting a tick count until a death total passes.

### ⚠️ Mutation-checking this found that three "obvious" mutations are SOUND

⛔ **Do not read a passing mutation here as a hole in the test.** The bound has a
deliberate pixel of slack (`6c - 7` where `6c - 6` is tight) *and* the loop gives
another back (`i += skip - 1`), so the first two pixels of over-skip are still
correct:

| mutation | result | why |
|---|---|---|
| `6c - 5` | **green, and rightly** | still inside the tight bound |
| `i += skip` | **green, and rightly** | skips to distance `6c - 7`, exactly what is promised |
| off-mask promises 64 | **green** | unreachable — the walk bounds-checks first, see `WalkmaskLookup.SafeSkipPixels` |
| **`6c - 4`** — the first genuinely unsafe value | **3 red** | |
| `12c - 7` | **3 red** | |
| **`i += skip + 2`** | **2 red** | |

⚠️ **`ObservationWriter`'s own note says a `6c - 5` mutation once passed a test
written to catch it.** That is the same phenomenon and not a coincidence: the
constant has margin, so a mutation of ±1 is not a bug and a test that reddened on
it would be wrong. **The mutation to reach for here is the first value that is
actually unsound, and it has to be worked out rather than guessed.**

⚠️ **The bound now has two readers and one body**, `Walkmask.SafeSkipPixels`.
`ObservationWriter.SafeSkipPixels` forwards to it and a test asserts they agree
across the whole clearance range — because the failure mode of two copies is one
marcher stepping over a wall, which nothing else in this repo would notice.

### Still on the table, measured and not taken

- **`PlaceFree`'s rect scan is fine.** It is a linear pass over every solid
  rectangle, and the worst shipped map has **13** (`ctf_conflict`). The comment
  claiming "small and rarely more than a dozen, so a linear scan beats any index"
  is accurate — checked, not assumed.
- **`stackalloc ProjectileHandle[512]` costs 18 ps a call** even zeroed, against a
  3.7 µs tick. There is no `SkipLocalsInit` in this repo and it would buy nothing
  here. ⚠️ Recorded so the next reader does not chase it: the four 512-element
  stackallocs look alarming and are not.
- ⛔ **`RolloutSearch`'s 64-slot opponent loop: MEASURED at 1.1%, and rejected.**
  43.4 ns a simulated tick, so 52 µs of a 4,795 µs decision. ⚠️ Its share rises
  slightly as the tick gets cheaper — re-measure before rejecting it a second time. ⚠️ **The safe version
  saves less than that**, because skipping dead slots is *not* equivalent: today
  they are written `PlayerInput.None` every tick, and a slot that dies mid-rollout
  — or respawns inside the horizon — would otherwise keep its last live input.
  Preserving that means clearing what just died, which costs about what the loop
  costs. **1% is not worth a silent, rare divergence in what the search decides.**
- ⛔ **Memoising the last mask query: 17-19% hit rate in a real match, rejected.**
  ⚠️ **This said 43.9% for a day and that number was wrong** — it was taken on a
  `ScenarioWorld` whose characters were standing still, which is not a workload.
  Re-measured 2026-09-02 over 2,940 ticks of real bot matches: **19.2%**
  (`ctf_truefort`), **17.3%** (`ctf_2dfort`), **18.6%** (`koth_valley`). At 8.8 ns
  a mask test the gross ceiling is ~1.6 ns against a ~1 ns compare paid on every
  call — **a wash.** ⚠️ **The thread-safety objection recorded with the old number
  was also wrong**: a `[ThreadStatic]` memo keyed on the `Walkmask` reference is
  sound across workers and across forks, because the mask is immutable and gates
  live in `_rects`. The hit rate is the reason, not the threading.
- **`WorldState` is 156,624 bytes against `ACloneStaysWithinItsBudget`'s 160 KB
  ceiling — 98%.** ⛔ **One ordinary field away from red**, and worth knowing
  before somebody adds one and reads the failure as a surprise.

## Bots: the actual point of the project

Bots are already, in the reference, just players whose `keyState` is written by
code instead of arriving on a socket (`Scripts/Bots/botInputUpdate.gml`). That
is the seam, and **it is built** (phase 7 item 1):

```csharp
public interface IInputSource {
    PlayerInput Poll(in WorldView world, int slot, int tick);
}
```

`Gg2.Simulation.IInputSource`, `WorldView`, `PlayerCommand`;
`Gg2.Server.NetworkInputSource`, `GameServer.GatherInputs` / `ServiceBots` /
`AddBot` / `RemoveBot`. Gated by `InputSourceTests` and `WorldViewTests`.

**It is a transcription of a shape the reference already has.**
`GameServerBeginStep.gml:79-85` calls `botInputUpdate(player)` in exactly the
position, and in the same roster loop, that it calls
`processClientCommands(player, i)` for a socket. The port keeps that: one input
gather over the whole roster, one set of command handlers, one roster.

### The five things it settled

- **A network client is an `IInputSource` too.** `NetworkInputSource` holds the
  last `INPUTSTATE` that arrived and returns it. ⚠️ **Holding it is the
  behaviour, not laziness** — `processClientCommands.gml:267` writes `keyState`
  when a packet arrives and nothing clears it, so a dropped packet must leave
  the player pressing what they last pressed. Returning `PlayerInput.None`
  between packets would turn every lost packet into a stutter, and the client
  dead-reckons on the opposite assumption.

- **⚠️ The actions that are not keys have their own channel.** Building a
  sentry is a client *command* (`serverBuildSentry.gml`), and so are the zoom,
  the class and team changes and `DROP_INTEL` — none of them reaches
  `MatchSimulation.Tick` at all, so none can be expressed as a `PlayerInput`.
  They are `PlayerCommand`s, pushed into a `PlayerCommandBuffer` by an optional
  `ICommandSource`. **The buffer's capacity is `ClientCommands.PerTickLimit`,
  on purpose**: a bot gets exactly the socket's flood budget, enforced by there
  being nowhere to put an eleventh.

- **⚠️ Each command has one handler, reached from both sides.** `GameServer`'s
  `ExecuteBuildSentry`, `ExecuteToggleZoom`, `ExecuteChangeClass`,
  `ExecuteChangeTeam`, `ExecuteDropIntel`, `ExecuteDestroySentry` are called by
  the socket switch *and* by `ServiceBots`. `botServerActions.gml` says why in
  its own header — a bot "performs them by calling the same server-side scripts
  the network path calls … each of which re-checks its own preconditions" — and
  a second copy would let a bot build a sentry a client could not. A failed
  precondition is silence, never an error.

- **`WorldView` is a `ref struct`.** It carries a `ref readonly WorldState`, so
  constructing one is free rather than a kilobyte copy per bot per tick, and the
  language then refuses to let a source stash it in a field or keep it past the
  call. The read-only guarantee is structural instead of documented.

- **The batched shape is wired at the call site, not just declared.**
  `GatherInputs` groups slots by source *identity* and calls
  `IBatchInputSource.PollMany` once with all of them. docs/BOTAI.md item 5 asks
  for the interface; the grouping is here because that item's warning is about
  the call site — "adding it after every call site assumes per-slot polling is a
  wide change".

### Two divergences from the reference, both deliberate

- **Bots are serviced in a pass of their own, after the clients.** The reference
  interleaves them by roster index; `ServiceClients` walks its clients
  *backwards* so a disconnect cannot skip the next one, and a departure compacts
  the roster, so running the bots afterwards is what keeps every slot index
  stable. The cost is that a bot's broadcasts follow the humans' within a tick.
  Nothing observes it: commands from different players produce independent
  messages.

- **`AddBot` sends no `FULL_UPDATE` burst.** That exists to catch a joining
  socket up; a bot has no socket. Otherwise it is `botAdd.gml` exactly — roster
  first, then `PLAYER_JOIN`, then `checkClasslimits`, then ⚠️ **`CHANGETEAM`
  before `CHANGECLASS`**, which is the opposite of the order `CompleteJoin`
  replays an existing roster in, and then `alarm[5] = 1` for anything but a
  spectator.

A network client and a bot are both an `IInputSource`. Consequences worth
having, and now available:

- Bot logic is testable without a server, a socket or a client.
- A bot can be swapped, A/B'd, or run at a different think-rate per slot.
- `WorldView` is a read-only projection, which stops bot code from mutating
  simulation state by accident — the failure mode that is hardest to debug and
  most likely under heavy bot logic.

The headroom argument is concrete: GM8 interprets its GML, and the existing bot
layer is 7,202 lines of it (`Scripts/Bots/` + `Scripts/BotNav/`) running for
every bot every tick. Compiled C# on the same work should leave one to two
orders of magnitude of budget, which is what buys real pathfinding, prediction
and coordination instead of the current per-tick approximations.

✅ **Measured 2026-08-27, and it is phase 7's exit criterion: twelve bots cost
0.151ms of a 33.3ms tick — 0.5% of the budget.** 600 steady-state ticks of a real
server on `ctf_truefort`, with the population manager, twelve policies each
planning and fighting, the whole simulation, the state serialiser and the
broadcast in the loop. `BotPopulationTests.TwelveBotsRunWellUnderTheTickBudget`.

⚠️ **That is a Release measurement and the build matters: the same test in the
default Debug build reads 0.307ms, 0.9%.** Twice the cost, so a re-measure that
forgets to say which build it ran looks like a regression that is not one. The
test's own bound is a quarter of the budget, which both builds clear comfortably,
because the claim is an order of magnitude rather than a digit and a CI machine
under load is not a benchmark.

⚠️ **That measures this port's cost, not the ratio to GM8.** Nobody has
benchmarked the reference's bot layer here, so the "one to two orders of
magnitude" above is still a design intention — what the number establishes is
that a bot can be made roughly two orders of magnitude heavier before the tick
is at risk, which is the claim the project actually needs.

Two things to carry over rather than reinvent:

- **The nav graph generator already lives outside the game**, and the port
  **reads its cache** (phase 7 item 2): `gg2-nav-gen` builds
  `botnav/<map>_a<area>.txt` offline — kept in this repo's gitignored `nav/`
  since 2026-09-01, previously in the reference tree — and `Gg2.Bots.NavCache` loads it
  and `Gg2.Bots.NavGraph` is the model, with the coordinate conversions, the
  node lookup, the gate rules and the two indices `navCacheLoad.gml` derives
  rather than stores. Moving *generation* into `Gg2.Bots` is a natural
  consolidation later; it is not done and is not needed.
  ⚠️ `gg2-agent/tools/navgraph.js` documents the format and is right about it —
  including the non-obvious `ds_grid_write` column-major layout with
  sixteen-byte cells — but it **floors the feet row where the game rounds it**,
  which is a whole row on stepped terrain. docs/TRAPS-BOTS.md, "the nav cache reader",
  has that and the rest of the traps.
- **The `BOT_*` constants** in `Gang-Garrison-2/Source/gg2/Constants.xml` are a
  tuned set with real playtesting behind them. Port the values even if the
  algorithms get rewritten.

## Runtime settings

```xml
<TargetFramework>net10.0</TargetFramework>
<ServerGarbageCollection>true</ServerGarbageCollection>
<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
<TieredPGO>true</TieredPGO>
<InvariantGlobalization>true</InvariantGlobalization>
```

Native AOT is attractive for startup and memory but not required; revisit once
the thing runs. Do not enable any fast-math-equivalent switch — there isn't one
in .NET, which is part of why C# is a reasonable choice here.

### ⚠️ The TFM moved 9.0 → 10.0 on 2026-09-02, and what that proved

**The whole suite was green on both** — 2,601 passing, including every parity
fixture and all three pinned bot measurements. The absence of a fast-math switch
is what makes a runtime bump defensible here at all.

⚠️ **What it establishes is "no observable difference", not "bit-identical
doubles".** The wire quantises position to 1/5 px and velocity to 1/8.5, so a
*sub-quantum* floating-point change would not show in a closed-loop fixture at
all. `ClientPredictionGateTests` pins p99 open-loop drift to exact integers over
long windows and did not move either, which is the tightest instrument there is —
but it is still a bound, not a proof.

⛔ **`SIM_UUID` does NOT cover the runtime.** `SimulationStamp.Lines()` digests
constants and version numbers only, and deliberately so: its own comment says a
stamp depending on reflection order "would move between runtimes for nothing". So
a runtime that *did* change the physics would go on asserting the same
simulation, and `gg2-rl`'s checkpoint guard would not notice. **The parity
fixtures are the only thing that would.** Re-run them on any runtime change; the
stamp will not do it for you.

⚠️ **The practical reason the move happened**: the machine-wide install carries
runtimes 6.0, 8.0 and 10.0 and **no 9.0**, so a net9.0 build could only be run by
a user-local SDK that is not on `PATH`. Both `dotnet test` and a directly
launched `.exe` failed in ways that look like anything but a missing runtime.

## The control surface — built, 2026-08-27

`gg2-agent` drives the GM8 game through an injected bridge because there was no
other way in. A C# server does not need injection: expose the same capabilities
as a small local-only TCP/JSON control port, or an in-process API when hosting
the sim from a test.

Worth having, roughly in the order the existing tooling found them useful: read
live state, step N ticks, run until a condition, set bot count, change map,
teleport, freeze/resume, run faster than real time. That last one is nearly free
here — the tick loop simply stops sleeping — and is the single biggest iteration
win over the current setup.

Keep it **loopback-only and off by default**, for the same reason the GM8 bridge
is: it executes commands against a live game.

✅ **Built: `ControlPort`, behind `gg2-server --control-port <n>`.** One JSON
object per line, answered by one per line — enough for `nc`, a test or a script,
with no schema to negotiate. `ping`, `state`, `freeze`, `resume`, `step`,
`speed`, `bots`, `map`, `teleport` and `command`.

⚠️ **It binds `IPAddress.Loopback`, not `Any`.** There is no configuration in
which it is reachable from the network, and that is a property of the bind
rather than of a check somebody can forget.

⚠️ **It is serviced from inside `GameServer.Tick`, on the tick thread.** No
locks, no concurrent access to `WorldState`, and a command's effect always lands
between two ticks rather than inside one. A control port on its own thread would
need the simulation to be thread-safe, which is a far larger claim than this
project makes anywhere — and the failures would be the irreproducible kind.

⚠️ **Freezing skips exactly one call — `AdvanceSimulation()` — and everything
around it keeps running.** A freeze that skipped the whole tick would stop
servicing sockets, so a frozen server would drop the clients it was frozen to
inspect and stop answering the port that froze it: the freeze would be permanent
and would read as a hang. Gated by joining a client *while frozen*, which needs
the accept, the joining state machine and the flush all still running — the
weaker version of that test passed the mutation it claimed to catch.

⚠️ **Anything a client can also do goes through `Execute(RosterEntry,
PlayerCommand)`** — the `command` verb — rather than being reimplemented.
CLAUDE.md's "three places" rule, honoured by not adding a fourth. What is
control-port-only is the operator actions no client can ask for: freeze, step,
teleport, map change, bot count, speed.
