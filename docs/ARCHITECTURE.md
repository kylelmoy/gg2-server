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
  cells; the test is a few masked word reads.
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
layer is ~7,100 lines of it (`Scripts/Bots/` + `Scripts/BotNav/`) running for
every bot every tick. Compiled C# on the same work should leave one to two
orders of magnitude of budget, which is what buys real pathfinding, prediction
and coordination instead of the current per-tick approximations. ⚠️ **That
figure has never been measured**, and measuring it is phase 7's exit criterion.

Two things to carry over rather than reinvent:

- **The nav graph generator already lives outside the game**, and the port
  **reads its cache** (phase 7 item 2): `gg2-nav-gen` builds
  `Source/build/botnav/<map>_a<area>.txt` offline; `Gg2.Bots.NavCache` loads it
  and `Gg2.Bots.NavGraph` is the model, with the coordinate conversions, the
  node lookup, the gate rules and the two indices `navCacheLoad.gml` derives
  rather than stores. Moving *generation* into `Gg2.Bots` is a natural
  consolidation later; it is not done and is not needed.
  ⚠️ `gg2-agent/tools/navgraph.js` documents the format and is right about it —
  including the non-obvious `ds_grid_write` column-major layout with
  sixteen-byte cells — but it **floors the feet row where the game rounds it**,
  which is a whole row on stepped terrain. CLAUDE.md, "the nav cache reader",
  has that and the rest of the traps.
- **The `BOT_*` constants** in `Gang-Garrison-2/Source/gg2/Constants.xml` are a
  tuned set with real playtesting behind them. Port the values even if the
  algorithms get rewritten.

## Runtime settings

```xml
<TargetFramework>net9.0</TargetFramework>
<ServerGarbageCollection>true</ServerGarbageCollection>
<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
<TieredPGO>true</TieredPGO>
<InvariantGlobalization>true</InvariantGlobalization>
```

Native AOT is attractive for startup and memory but not required; revisit once
the thing runs. Do not enable any fast-math-equivalent switch — there isn't one
in .NET, which is part of why C# is a reasonable choice here.

## A control surface, eventually

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
