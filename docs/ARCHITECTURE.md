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
  cells; the test is a few masked word reads — `Walkmask.AnySolidInBox`.
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

| | cost |
|---|---|
| `CollisionWorld.PlaceFree` (character box + filter) | **13.9 ns** |
| `Walkmask.AnySolidInBox` alone | 8.8 ns |
| `Movement.GoodMoveContactSolid`, a 10px fall | **177 ns** — about 13 `PlaceFree`s |
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
  43.4 ns a simulated tick, so 52 µs of a 4,795 µs decision. ⚠️ **The safe version
  saves less than that**, because skipping dead slots is *not* equivalent: today
  they are written `PlayerInput.None` every tick, and a slot that dies mid-rollout
  — or respawns inside the horizon — would otherwise keep its last live input.
  Preserving that means clearing what just died, which costs about what the loop
  costs. **1% is not worth a silent, rare divergence in what the search decides.**
- ⛔ **Memoising the last mask query: MEASURED at 43.9% hit rate, and rejected.**
  Consecutive `AnySolidInBox` calls repeat their cell rectangle 43.9% of the time
  over 3,000 ticks of a twelve-character world (537,549 calls). ⚠️ **The arithmetic
  predicted ~83%** — one pixel of a crawl only crosses a 6px cell boundary every
  sixth step — and real motion is 2D with the crawl alternating axes, so the
  prediction was wrong by half. At 8.8 ns a mask test that ceiling is ~3.9 ns
  against a ~1 ns compare and a `[ThreadStatic]` read. ⛔ **And it cannot live on
  `CollisionWorld`**: `RolloutOptions.Threads` shares one across worker threads, so
  a mutable memo there races and `RolloutThreadingTests`' identity gate is exactly
  what would catch it — after it had changed a decision.
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
