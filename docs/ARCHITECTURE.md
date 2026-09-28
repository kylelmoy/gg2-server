# Proposed C# architecture

Shaped by the two goals in `docs/STATUS.md`: performance headroom for much heavier
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

The tick allocates nothing. The broadcast buffer is pooled, serialisation uses
`Span<byte>`, and projectiles live in a free-list-backed array rather than a
`List` of class instances.

## Collision

The single hottest call in the server is `placeFree`:

- The walkmask is a **bitset** — `ulong[]` with a row stride, one bit per mask
  cell, one cell per 6 × 6 world pixels. A character rectangle spans a handful of
  cells; `Walkmask.AnySolidInBox` tests it with a few masked word reads, or, for
  the shapes a character box actually produces, a single bit read from a
  precomputed **erosion table** (below).
- Solid rectangles (gates, player walls, platforms) are a small array of AABBs
  tested afterwards.
- **Solidity is a query filter, not global state.** The reference toggles a
  `solid` flag on every obstacle before each check and clears it after
  (`charSetSolids`/`charUnsetSolids`) because GML gave it no better option. The
  port passes a filter instead:

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

## Performance-sensitive design decisions

These are current facts about where the tick's cost lives and the design
choices that follow from it. ⚠️ **Any absolute number here is a wall-clock
figure on one machine (Release, net10.0, 24-core box, `ctf_truefort`) and decays
— re-measure with `tools/bench/` before quoting one; what survives is the
shape.** `tools/bench/README.md` has the measurement traps.

- **The tick cost is per live character, not per roster slot.** The population
  loop iterates `MaxSlots` (64) mostly-empty entries; that is not worth
  compacting — the fixed per-tick overhead is small next to the per-character
  cost.
- **`PlaceFree` is the leaf and the crawl is the multiplier.**
  `Movement.GoodMoveContactSolid` (the GM8 "crawl") asks `PlaceFree` once per
  pixel of travel, which is why the walkmask layout above was chosen: the cost
  is the call count, not the call. `Movement.GoodMoveContactSolid` deliberately
  stops when a step leaves both coordinates unchanged rather than looping
  forever — see "Non-negotiables" in `CLAUDE.md` and `docs/TRAPS-SIMULATION.md`,
  "the crawl that could not finish". A binary search on the crawl is unsound:
  the free/blocked predicate along a path is not monotone (a character can pass
  a pillar and land in open space beyond it), so only a *conservative* pre-test
  is safe. A swept-AABB pre-test of this kind was built and is bit-identical,
  but it clears only ~1-3% of crawls — gravity puts the destination inside the
  floor on almost every grounded tick — so it is not worth carrying.
- **`Walkmask.AnySolidInBox` is a table lookup for common character-box shapes**,
  one bit read instead of a word scan per row. A character box's cell rectangle
  is fixed by its pixel size and phase, so each class produces at most two
  widths and two heights; a handful of tabulated shapes (3×6, 4×6, 3×7, 4×7,
  plus Pyro's 3×5/4×5) covers the great majority of real-match mask queries.
  The table is at **cell** resolution — the same footprint as the mask itself —
  because a per-pixel table would not fit in cache. `WalkmaskBoxTests` mutation-
  checks the tabulated shapes against every legal position on three maps, and
  `IsTabulatedShape` is public so a test can assert the tabulated set matches
  what real boxes actually produce (a table nothing reaches would otherwise be
  green and slow).
- **`CollisionLine.Walk` (the hitscan/line-of-sight walker) skips open ground.**
  It samples the mask once per pixel of the segment (GM8's own rule, not
  negotiable), but `Walkmask.Clearance` lets a sample standing on a cell of
  clearance `c` advance `6c - 7` pixels, since every pixel that close is known
  to be open. This matters most for `BotArc.Clear` (a soldier/demoman testing a
  rocket's ballistic line of sight across mostly-empty air) and also speeds up
  sentries and hitscan weapons. `CollisionLineSkipTests` is the gate — the
  parity fixtures do not cover it, since a hitscan in a recorded trace is a
  short line in a firefight and the skip only engages over open ground.
- **`CollisionWorld.BulletRectsIn` filters solid rects once per ballistic arc**,
  not once per segment, using a bounding box around the whole arc; `BotArc.Clear`
  otherwise re-walks the full rect set for each of ten segments per shot. The
  filter is exact because it is conservative: a rect that can meet a segment
  must overlap the box around the whole arc. `BulletRectFilterTests` gates it —
  the parity fixtures do not cover firing across a doorway.
- **`ScenarioResources.Shared` caches decoded maps and parsed nav graphs by map
  name, process-wide.** `LevelMap` and `NavGraph` are `init`-only and safe to
  share; `CollisionWorld` and `SpawnPoints` are NOT shared and are rebuilt per
  match (`CollisionWorld.SetCaptureZones` mutates it) — `SharedMapDataTests`
  gates the split both ways. The cache is never invalidated, which is correct
  for a batch runner (`HeadlessMatch`) and would be wrong for the live server;
  `GameServer` does not use this path and calls `LevelMap.FromPng` on every map
  rotation, deliberately, so an edited map on disk is picked up.
- **A\* deliberately gives up optimality.** `NavSearch` never re-opens a closed
  node (the reference's `navFindPath.gml` does, because the straight-line
  heuristic is admissible but not consistent) and inflates the heuristic by
  `NavConstants.SearchWeight = 1.3`, so the frontier commits toward the goal
  sooner. The two only work together — inflating the heuristic without the
  closed-set skip makes the search slower, not faster, because an inconsistent
  heuristic re-opens far more nodes. This is a behaviour change (not just a
  speed win) and is allowed: `CLAUDE.md` puts `src/Gg2.Bots/` outside "the
  reference is the specification". Route *completeness* is unaffected (every
  reachable goal still finds a route); only which equal-or-near-cost route comes
  back can differ. `NavRouteIdentityTests` digests every route over all shipped
  graphs to catch a routing regression, since `NavSearchFixtureTests`' property
  assertions (walkable chain, correct endpoints, gate rules) hold for many
  possible routes, not just the one this port returns. `NavConstants` (this
  weight included) is part of `SimulationStamp.RoutingUuid`, tracked separately
  from the physics `SIM_UUID`, because a nav change shifts *which states* a
  policy is asked about without changing what any action does.
- **The A\* priority queue is .NET's stock `PriorityQueue<int, double>`.** A
  hand-written 4-ary heap replicating its exact sift semantics was built and
  measured faster, but by an amount not worth a second implementation of the
  routability rule.
- **`WorldState` is a value type, sized close to its test ceiling.**
  `ACloneStaysWithinItsBudget` caps a clone at 160 KB; the struct is currently
  156,624 bytes. A `RolloutSearch` decision clones `WorldState` once per
  candidate tick (candidates × horizon), so the struct's size is on the hot
  path for search-based bots, though the simulation tick itself (not the clone)
  dominates a rollout's cost.
- **`dotnet-trace`'s sampling profiler is unreliable on this workload** and has
  repeatedly misattributed cost to runtime helpers (`memset`/`memmove`-shaped
  code) rather than to where the time actually is. Use it only to pick which
  method to look at; size a change with in-process `Stopwatch` stage timers
  (checked by summing to what an outer timer reads) and confirm any change with
  an interleaved A/B, not a single before/after pair — machine drift is
  otherwise indistinguishable from a real change of a few percent.

## Bots: the actual point of the project

Bots are already, in the reference, just players whose `keyState` is written by
code instead of arriving on a socket (`Scripts/Bots/botInputUpdate.gml`). That
is the seam, and it is built (phase 7 item 1):

```csharp
public interface IInputSource {
    PlayerInput Poll(in WorldView world, int slot, int tick);
}
```

`Gg2.Simulation.IInputSource`, `WorldView`, `PlayerCommand`;
`Gg2.Server.NetworkInputSource`, `GameServer.GatherInputs` / `ServiceBots` /
`AddBot` / `RemoveBot`. Gated by `InputSourceTests` and `WorldViewTests`.

It is a transcription of a shape the reference already has:
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
  `IBatchInputSource.PollMany` once with all of them. `docs/BOTAI.md` item 5
  asks for the interface; the grouping is here because that item's warning is
  about the call site — "adding it after every call site assumes per-slot
  polling is a wide change".

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

A network client and a bot are both an `IInputSource`. Consequences of that:

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

✅ **Phase 7's exit criterion: twelve bots cost 0.151ms of a 33.3ms tick — 0.5%
of the budget** (Release; 0.307ms/0.9% in Debug — always say which build). 600
steady-state ticks of a real server on `ctf_truefort`, with the population
manager, twelve policies each planning and fighting, the whole simulation, the
state serialiser and the broadcast in the loop.
`BotPopulationTests.TwelveBotsRunWellUnderTheTickBudget`.

⚠️ **That measures this port's cost, not the ratio to GM8.** Nobody has
benchmarked the reference's bot layer here, so "one to two orders of magnitude"
above is a design intention — what the number establishes is that a bot can be
made roughly two orders of magnitude heavier before the tick is at risk, which
is the claim the project actually needs.

Two things carried over rather than reinvented:

- **The nav graph generator lives outside the game**, and the port **reads its
  cache** (phase 7 item 2): `gg2-nav-gen` builds `botnav/<map>_a<area>.txt`
  offline — checked into this repo's `nav/` (gzipped) — and `Gg2.Bots.NavCache`
  loads it and `Gg2.Bots.NavGraph` is the model, with the coordinate
  conversions, the node lookup, the gate rules and the two indices
  `navCacheLoad.gml` derives rather than stores. Moving *generation* into
  `Gg2.Bots` is a natural consolidation later; it is not done and is not
  needed. `docs/TRAPS-BOTS.md`, "the nav cache reader", has the format traps
  (including the column-major `ds_grid_write` layout and the feet-row rounding
  direction).
- **The `BOT_*` constants** — in the bot fork's `Source/gg2/Constants.xml`, at
  `github.com/kylelmoy/Gang-Garrison-2` (branch `bot-route-variety`), **not** in the local stock tree — are a
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

⚠️ **`ServerGarbageCollection` is the wrong shape for a small VPS and is left on
anyway.** Server GC sizes heaps per core for throughput; the tick loop is
single-threaded and latency-sensitive, so `DOTNET_gcServer=0` is the plausible
setting there. It stays on here because the difference was measured and was
not there — under 3 MB of working set, no pause-time difference looked for or
found. `docs/DEPLOY.md`, "Garbage collection", has the numbers and says what
would settle it.

Native AOT is attractive for startup and memory but not required; revisit once
the thing runs. Do not enable any fast-math-equivalent switch — there isn't one
in .NET, which is part of why C# is a reasonable choice here.

### The target framework is net10.0

The machine-wide .NET install carries runtimes 6.0, 8.0 and 10.0 and **no
9.0**, so a net9.0 build can only run from a user-local SDK that is not on
`PATH` — both `dotnet test` and a directly launched `.exe` fail in ways that
look like anything but a missing runtime. If those symptoms come back, check
the TFM first.

⚠️ **`SIM_UUID` does NOT cover the runtime.** `SimulationStamp.Lines()` digests
constants and version numbers only, deliberately, so a stamp does not move
between runtimes for nothing — which also means a runtime that *did* change the
physics would go on asserting the same simulation. **The parity fixtures are
the only thing that would catch that.** Re-run them on any runtime change; the
stamp will not do it for you. The wire quantises position to 1/5 px and
velocity to 1/8.5, so a sub-quantum floating-point difference between runtimes
would not show in a closed-loop fixture at all — `ClientPredictionGateTests`,
which pins p99 open-loop drift to exact integers over long windows, is the
tightest instrument available, but it is still a bound, not a proof.

## The control surface

A C# server does not need injection the way the live-game bridge needs one to drive the
GM8 game: it exposes the same capabilities as a small local-only TCP/JSON
control port, or an in-process API when hosting the sim from a test.

Available: read live state, step N ticks, run until a condition, set bot count,
change map, teleport, freeze/resume, run faster than real time. That last one
is nearly free here — the tick loop simply stops sleeping — and is the single
biggest iteration win over the GM8 setup.

It is **loopback-only and off by default**, for the same reason the GM8 bridge
is: it executes commands against a live game.

✅ **`ControlPort`, behind `gg2-server --control-port <n>`.** One JSON object
per line, answered by one per line — enough for `nc`, a test or a script, with
no schema to negotiate. `ping`, `state`, `freeze`, `resume`, `step`, `speed`,
`bots`, `map`, `teleport` and `command`.

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
the accept, the joining state machine and the flush all still running.

⚠️ **Anything a client can also do goes through `Execute(RosterEntry,
PlayerCommand)`** — the `command` verb — rather than being reimplemented.
CLAUDE.md's "three places" rule, honoured by not adding a fourth. What is
control-port-only is the operator actions no client can ask for: freeze, step,
teleport, map change, bot count, speed.

### Linux and containers

⚠️ **This project has only ever been run on Windows.** The audited baseline is
a source review plus a clean `dotnet publish -c Release -r linux-x64` —
warnings-as-errors and the platform-compatibility analyser catch an unguarded
Windows API — but **compiling for a RID is not running on it**; say so before
quoting any of this as "Linux support".

- ✅ **Exactly one file in `src/` is Windows-only**: `TimerResolution`, a
  `winmm.dll` P/Invoke, already guarded by `OperatingSystem.IsWindows()` and a
  no-op elsewhere. `Web/SystemInfo.cs` adds two guarded branches (the CPU
  registry read and `GlobalMemoryStatusEx`) and nothing else in the tree
  P/Invokes at all.
- ⚠️ **The pacing loop's behaviour is genuinely different and untested on
  Linux.** On Windows it raises the timer to 1 ms because a wait rounds up to
  the 15.6 ms quantum — measured: 15% of ticks arrived ~14 ms late without it.
  Linux has no such knob; its granularity is the kernel's. The late-tick rate
  on Linux is unknown and is the first thing to look at on a real run.
- ✅ **`nav/` travels with a publish.** `Gg2.Host.csproj` copies `maps/*.png`
  and `nav/*` alike, with no flag needed; a published directory is 2.9 MB.
- ⚠️ **Case-sensitive filesystems** make `MapExists` stricter than it is on
  Windows: a map name typed with the wrong case resolves there and not here.
- ⚠️ **`Environment.TickCount64` is a monotonic clock, not the OS uptime.** On
  Linux it is `CLOCK_MONOTONIC` and does not count time suspended, which is why
  `SystemInfo` reads `/proc/uptime` where it exists.

### A second transport: the web admin panel

✅ **`gg2-server --web <port>` hosts an admin panel in a browser**, and it is the
only surface in this project meant to be reachable from the network.
**`docs/WEBADMIN.md`** is the subject file: the flags, the auth model, the HTTP
subset, the endpoints and what the panel deliberately does not do.

⛔ **Every verb goes through `ControlCommands`; both surfaces are transport
only.** The control port and the panel cannot drift into two slightly different
control surfaces because there is one dispatch, one set of replies and one set
of error messages. `kick` is the one verb the panel added, and the control port
gained it for free.

⚠️ **The panel does NOT service its sockets from inside `Tick`, and the
difference from the control port is deliberate.** A control-port client is a
tool on loopback; a panel client is across a network and may be slow, hostile,
or a phone that went into a tunnel mid-reply, and writing to it from the tick
thread would let it stall the simulation for every player on the server by not
reading. Instead the socket threads touch nothing: commands are queued and
drained by `Pump` on the tick thread, and reads are answered from a JSON
snapshot the tick thread republishes — and only while a browser is actually
watching, which `WebAdminTests.NobodyWatchingMeansNoSnapshots` gates rather than
asserting in a comment.
