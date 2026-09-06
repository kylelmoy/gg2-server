# Proving the port matches

Physics parity is the risk that sinks this project, and it is not the kind of
bug playtesting finds — a 0.3 px/tick divergence looks like "the netcode feels a
bit rubbery", which GG2 already does. The harness exists so this is checkable in
CI instead of by eye.

## The key realisation

The reference server's own broadcast stream is a complete golden trace.

Every tick it sends `INPUTSTATE`, which carries **every player's `keyState` and
aim**. Every seventh tick it sends `QUICK_UPDATE`, which carries **every
player's position, velocity and hp**. So one recording of a real match contains
both the inputs the simulation consumed and the outputs it produced, already
quantised to exactly the precision the client can observe.

That gives a test with no instrumentation, no bridge, no GML changes and no
game running:

```
record:   connect a passive recorder to the reference GM8 server, dump every byte
replay:   feed the recorded keyStates into the ported server tick by tick
compare:  the port's QUICK_UPDATE fields against the recording's, per character
```

If they agree, the stock client cannot tell the two servers apart. That is the
whole specification.

## Tier 0 — the recorder

`Gg2.Parity`, the `gg2-parity` console app. `record` joins a server as a passive
spectator and writes its broadcast stream to a file; `diff` compares two
recordings and reports the first differing byte with a window either side;
`census` prints what a recording contains and where framing stopped.

Speaks the handshake from `PROTOCOL.md`, `RESERVE_SLOT`, `PLAYER_JOIN`, then
never sends an `INPUTSTATE` of its own — it parses the inbound stream into a
list of framed messages and writes it to disk with a tick index.

Framing lives in `Gg2.Protocol.ServerStream` and `StateUpdateReader`. The
stream is **not self-delimiting**, and this is the part worth knowing before
touching a reader: a state update's width comes from three places at once.

1. **The roster**, which the reader has to track exactly as the client does —
   including the compaction on `PLAYER_LEAVE`.
2. **The map**, via `WorldShape`: the gamemode picks the HUD block, and both the
   control-point count and the `MovingPlatform` count are map facts that appear
   on the wire with no count prefix.
3. **Bytes inside the update.** A player's `subobjects` byte decides whether a
   character and a sentry follow; a sentry's hp byte carries "built" in bit 7
   and an unbuilt one has an extra `vspeed` byte on a `FULL_UPDATE`; a demoman's
   `Minegun` writes `lobbed` and then that many seven-byte mine blocks.

Point 3 has a consequence that is easy to miss: **the reader must know each
player's class**, because the weapon block that closes a character's
`FULL_UPDATE` is class-dependent, and it is **never zero bytes** — every weapon
inherits `<parent>Weapon</parent>`, and `Weapon.events/User Event 12.xml`
writes `readyToShoot`/`alarm[0]` even when a child defines nothing of its own.
Measured on the running engine (`global.serializeBuffer`, `event_user(12)`,
`buffer_size`):

| class | weapon | bytes |
|---|---|---|
| Medic | `Medigun` | **1** — heal target only |
| Demoman | `Minegun` | **3 + 7n** — the parent's two, `lobbed`, then the mines |
| every other class | | **2** |

`Medigun` differs from every other weapon because its `User Event 12` XML has
no `action_inherited`; `Minegun`'s does. That distinction is invisible in the
GML text and was only found by measuring buffer sizes directly.

`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-combat.trace` is the fixture
that exercises a character inside a `FULL_UPDATE` at all — every recording made
by joining immediately captures state before any bot has spawned, so a
`FULL_UPDATE` containing a live character was unreached until a fixture was
recorded with six bots already spawned (medic, demoman and four ordinary
weapons in one update, eight `WEAPON_FIRE`s, one `PLAYER_LEAVE`).

`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-spectator.bin` is a recording
of the real GM8 server on `ctf_truefort`, and the test that frames it to the
last byte is what would notice any of the above being wrong.

**When adding a fixture, ask what state it cannot reach.** That question has
found nearly every framing bug in this project.

### The trace format

`Gg2.Protocol.Trace` and `TraceParser`. **The trace is the raw stream**, with a
header naming the map, the reference version and where it came from — not a
parsed structure. Three reasons:

1. It is lossless: a parser change never invalidates a recording.
2. It is bounded and cheap — a 30 Hz broadcast to a spectator watching two bots
   is about 440 bytes a second.
3. The parity recorder and a live server writing training data are the **same
   path** (`docs/BOTAI.md`, item 3); a second logging format invented later is
   exactly how the two drift apart.

`TraceParser` turns it into ticks. A tick is delimited by its state update, and
this is a framing **convention**, not a fact about the server: every Begin Step
event (a `WEAPON_FIRE`, a generator's destruction) is appended after that
update, and a `PLAYER_DEATH` is appended after the flush entirely. So
`TraceParser`'s "tick N" is really *[the events of N-1][the update serialised
at the top of N]*. `FULL_UPDATE` and `CAPS_UPDATE` are not tick boundaries — the
first is sent on the join path outside the cadence, the second goes out **in
addition to** that tick's own update. See `docs/TRAPS-SIMULATION.md`, "The
GameServer is the OLDEST instance, so its flush is the FIRST End Step".

### What a wedged client looks like

Worth knowing before debugging one. `receiveCompleteMessage` is a **blocking
spin** with no `io_handle()`:

```gml
do {
    if(socket_has_error(argument0)) return 2;
} until(tcp_receive(argument0, argument1));
```

A client waiting for bytes that will not come therefore pegs one core, stops
running Draw events (window goes black, not frozen-looking), and stops
servicing the agent bridge (`gg2_evalx` hangs with no error). All three
symptoms share one cause, and none of them says *where* the stream went wrong —
that is what the recorder is for.

An unknown message byte does **not** wedge the client: it hits `default:
promptRestartOrQuit("The Server sent unexpected data.")` and puts up a dialog.
A silent black screen means a *short* message, not an unrecognised one.

A wedge also looks identical to a client that has disconnected itself and had
its modal force-closed — a spectator does this at exactly five minutes
(`docs/PLAN.md`, phase 1). Check the launcher log for a dismissed dialog before
concluding the stream is at fault.

### Building a trace

Two rules that matter for every recording:

- **Start the trace at a `FULL_UPDATE`.** It is the only message carrying
  complete initial state; a trace beginning mid-match has no defined starting
  point. The `JOIN_UPDATE`/`FULL_UPDATE` pair sent on join is the natural
  anchor.
- **Record the roster events too** (`PLAYER_JOIN`, `PLAYER_LEAVE`,
  `PLAYER_CHANGECLASS`, `PLAYER_CHANGETEAM`, `PLAYER_SPAWN`, `PLAYER_DEATH`).
  Replay needs them to keep indices and class stats aligned.

**The cheapest source of a populated trace is the reference's own bots.** Set
`[Bots] Enabled=1` in the build directory's `gg2.ini`, run the dedicated
server, and record. Put the `gg2.ini` back **after the game is stopped** — the
dedicated server re-reads it live, so restoring it mid-session empties the
roster of whatever is still recording.

### How to record a fixture that replays closed-loop

A recording of players who were already alive can only ever be an **open-loop**
bound (see "Two kinds of fixture" below). To get a **closed-loop** fixture,
make every character spawn inside the recording:

1. Start the dedicated server with bots and class limits (below).
2. Wait for the bots to be up, then start `gg2-parity record`.
3. Once the recorder has joined, reload the map **over the bridge**:
   `gg2_eval: serverGotoMap("<the same map>")` — same map, so the trace header
   and the actual geometry agree.
4. Record long enough for the bots to leave their bases and fight (75 seconds
   ≈ 2,240 ticks is typical).

`Replayer` handles the `CHANGE_MAP` (`PortedSimulation.LoadMap` resets the
world) and the `PLAYER_SPAWN`s that follow give every character an exact
integer anchor — and so do the projectiles they then fire (see "What an
anchor cannot reach" below).

**To get a fixture that is about combat rather than walking**, in order of how
much each matters:

1. **Start recording thirty seconds late** — connect once `gg2_state` shows
   people below full hp, so bots have had time to meet.
2. **Use `[Classlimits]` to restrict the bots to classes the port implements.**
   This is the single trick that turns a recording into a usable fixture;
   without it half the fire is from unimplemented weapons and the residual is
   unreadable.
3. **`RemoveOnDeath=0`**, so the roster stays still and the trace stays about
   combat rather than joins and leaves.
4. **Record long** — fifty seconds gives ~1,500 ticks and ~1,000 comparisons.

**To force a specific class mix** the bots will not reliably pick on their own
(`[Classlimits]` narrows what a bot *may* pick, not what it *does* — a
multinomial draw over allowed classes can miss one entirely), force the roster
over the bridge instead. `global.botsEnabled` gates only `botPopulationUpdate`,
so it can be zeroed to freeze the roster without stopping bots playing:

```
gg2_eval: global.botsEnabled = 0;
          with(Player) if(isBot) botRemove(id);
gg2_eval: global.botNameCounter += 1;
          botAdd(TEAM_RED, CLASS_MEDIC, global.botNamePrefix + string(global.botNameCounter));
          ...  // once per bot, with the class and team you actually want
```

`botAdd(team, class, name)` still runs `checkClasslimits`, so the ini has to
allow the class, but the mix is now deterministic. ⚠️ **Force the roster
_after_ a map reload, not before** — the reload sends every player back to
spectator and the bots re-pick, which silently undoes team forcing (though
class forcing survives, since every class-forced fixture's class set does).

**To reach a slow objective inside a closed-loop window**, remove the enemy
team. What ends a closed-loop window is weapon knockback; with nobody to fight,
a window can run thousands of ticks instead of hundreds. Steering bots at a
goal with `botGoalLocked`/`botSetGoal` is legitimate for this — a replay
consumes recorded `INPUTSTATE`s and simulates no bot AI, so a goal changes only
what gets recorded, never how it replays. A **teleport** is not an option: it
moves a character the port would have had to derive, which breaks the
closed-loop anchor.

**The closed-loop anchor tick** is derived once, from `FixtureAnchors.LastExactAnchor`:
the latest first-`PLAYER_SPAWN` over the slots the recording observes, with one
admission — a slot that never spawns poisons the recording only if it is still
observed after that anchor tick.

## Tier 1 — primitives, against the live engine

Before any trajectory can match, `placeFree` must match. `gg2-agent` can answer
this directly from the running game: `gg2_eval` a `place_free(x, y)` at a few
thousand sampled points on a map and compare against the C# implementation
reading the same walkmask.

Sample deliberately at the nasty places, not uniformly: exactly on
`x % 6 == 0`, at negative coordinates, at the map edges, and at the character
rectangle's own corners.

## Tier 2 — trace replay, the main event

`Gg2.Parity.Replayer` and `DriftReport`, driven by `gg2-parity replay <trace>`.

The simulation it talks to is `ITraceSimulation`, deliberately tiny and living
in the harness rather than in the simulation, so the shape of `WorldState` is
not prejudged by an interface written before it exists. Everything crosses in
**wire units**, because the comparison is about the integers a client can
observe. `PortedSimulation` is the real adapter; `EmptySimulation` is a
do-nothing stand-in used to sanity-check the harness itself.

Two modes, and both matter:

**Closed-loop (what the server must do).** Feed inputs, never correct.
Divergence accumulates. The target is *exact equality of the quantised wire
fields* for the whole trace — achievable, because both sides do
double-precision arithmetic in the same order, so drift means a real
difference in the maths.

**Open-loop segments (what the client actually experiences).** Reset the
port's state from each `QUICK_UPDATE`, simulate forward to the next one
(**7 ticks, not 6** — `QUICK_UPDATE` fires when `frame mod 7 == 0`), then
compare. Report as a distribution: max and 99th-percentile `|Δx|`, `|Δy|` in
wire units (1/5 px).

CI gates:

| Metric | Gate |
|---|---|
| Closed-loop wire-field mismatch, per fixture | 0 |
| Open-loop 7-tick max error | project-specific bound per fixture (see below) |
| Any `hp` / `ammo` / flags mismatch | 0 |
| Recorded `WEAPON_FIRE` seeds left unconsumed | 0 |
| `PLAYER_DEATH` mismatch — tick, victim, killer, assistant or cause | 0 |

### What a state update cannot see

**A gate built only on state updates can be green for a program that does
nothing.** Three things a tick does never appear in one:

- **A weapon firing.** Only the emptied magazine reaches the update, and a shot
  that missed leaves no trace at all. `Fields.Trajectory`'s companion is the
  seed: the replayer hands the simulation each recorded `WEAPON_FIRE`'s seed
  and counts the ones no shot consumed (`PortedSimulation.SeedsUnused`).
- **A character dying.** A dead character is *absent* from the update, updates
  arrive every seventh tick, and nothing in one names a killer or a cause. So
  absence alone says only "died sometime in the last seven ticks."
  `Fields.Deaths` compares the recorded `PLAYER_DEATH` against the deaths the
  tick actually produced (`ITraceSimulation.DeathsLastTick`), in **both
  directions** — a death the recording has and the port does not, and a death
  the port invented — keyed on the victim.
- **A medic charging an ubercharge.** The class-specific byte
  (`uberCharge*255/2000`) saturates at 255 and sits there, so a port charging
  at the wrong rate can look identical once it tops out. `UBER_CHARGED` names
  the tick; `Fields.Ubers`, `ITraceSimulation.UbersLastTick`.

⚠️ **Neither a death nor an uber is ever fed *in*.** A recorded event is a
comparison, not an instruction; feeding it in would make the gate vacuous. The
question these exist to answer is whether the port reaches the same state by
itself.

⚠️ **A message produced during tick *n* is recorded under tick *n+1***. The
`GameServer` instance is older than any `Character`, so GM8 runs its Begin Step
first and the state update is already in the buffer before a character fires
(Begin Step) or dies (End Step). `TraceParser` closes a tick at each state
update, so both land in the following tick's `Events`. `SupplyFireSeeds`,
`ReconcileDeaths` and `ReconcileUbers` all read one tick ahead for this reason.

### Where replay legitimately diverges

Do not chase these — exclude them and say so in the report:

- **Server-side RNG**: team selection, chat bubbles, respawn timing, and
  *which* spawn point a character gets. Replay takes roster/team/spawn events
  from the trace rather than deciding them.
- **A spawn *position* is not on that list.** `PLAYER_SPAWN` carries a **spawn
  point id and a group**, not coordinates (`sendEventSpawn.gml`), and the
  client resolves the pair against the map itself (`doEventSpawn.gml`). Do the
  same: `Gg2.Maps.SpawnPoints` reproduces `offloadSpawnPoints.gml`'s lists, and
  the result is an integer coordinate with both speeds zero — an anchor that
  passed through no wire at all. Resynchronising instead from a broadcast
  `QUICK_UPDATE` position (quantised to a fifth of a pixel, sent every seventh
  tick) starts the character somewhere inside a bin, several ticks after it
  actually spawned, and produces spurious divergence. Only the *choice* of
  spawn point is RNG; the choice is on the wire.
- **Anything after a divergence.** Report the first mismatching tick and stop;
  a hundred downstream mismatches are one bug.

### Two kinds of fixture: closed-loop and open-loop-only

**A closed-loop replay needs an anchor that never passed through the wire.** If
every character in a recording was alive before it started, there is no
`PLAYER_SPAWN` to resolve, and the replayer has nothing to anchor on but a
broadcast `QUICK_UPDATE` — a fifth of a pixel of position *and* of velocity,
followed by up to seven ticks of free simulation before the first comparison.
That is enough on its own to lose exactness (a rounded position flips a
`place_free`, and a discrete decision turns a fifth of a pixel into a whole
one). Measured directly:
`PhysicsParityTests.AnchoringOnTheWireInsteadOfASpawnPointBreaksAnExactTrace`
replays the same 898-tick bot recording twice, changing only whether
`SpawnCharacter` resolves from a spawn point or from the wire — exact from a
spawn point, not exact from the wire.

| | anchor | replay mode | what a report means |
|---|---|---|---|
| everyone spawns during the recording | `PLAYER_SPAWN` → exact integer | **closed loop** | any mismatch is a bug |
| someone was alive at the start | quantised `QUICK_UPDATE` | **open loop only** | a *bound* on drift over the gap |

### The fixture corpus

All fixtures live in `tests/Gg2.Tests/Fixtures/`. This table is the current
inventory — map, roster, what each is for, and its measured closed-loop
exact-window length where one applies. Regenerate the row after changing
anything in `src/Gg2.Simulation` or the harness; a window that does not move
when it should have is itself a finding (CLAUDE.md, "the other direction").

| fixture | map / mode | roster | closed-loop? | what it gates |
|---|---|---|---|---|
| `reference-ctf_truefort-spectator.bin` | CTF | live match | n/a | raw framing to the last byte |
| `reference-ctf_truefort-bots.trace` | CTF | 2 bots, own AI | closed | first closed-loop fixture; 898 ticks |
| `reference-ctf_truefort-combat.trace` | CTF | 6 bots, own AI | open only | a `FULL_UPDATE` with a live character; scout/soldier/demoman/medic |
| `reference-ctf_truefort-hotcombat.trace` | CTF | 3v3, scout/soldier/demoman | open only | dense combat: 21 fires from tick 12, 1,495 ticks |
| `reference-ctf_truefort-closedcombat.trace` | CTF | 3v3, scout/soldier/demoman, reload-anchored | closed | main combat gate — trajectory **357**, hp **1,204**, sub-wire tolerance-1 **756** |
| `reference-ctf_truefort-closedsupport.trace` | CTF | 3v3, medic/pyro/engineer, reload-anchored | closed | uber, Shotgun, Flamethrower — trajectory **489**, +hp **328** |
| `reference-ctf_truefort-hotsupport.trace` | CTF | medic/pyro/engineer, dense | open only | open-loop bound: p99 `|dx|` 15, `|dy|` 25 |
| `reference-ctf_truefort-closedheavy.trace` | CTF | 3v3 heavy, reload-anchored | closed | trajectory **396** (Minigun unseeded caps it) |
| `reference-ctf_truefort-closedsniper.trace` | CTF | 3v3 sniper, reload-anchored | closed | trajectory **1,630** (seeded Rifle) |
| `reference-ctf_truefort-closedquote.trace` | CTF | 3v3 Quote, reload-anchored | closed | trajectory **1,756** |
| `reference-ctf_truefort-closedspy.trace` | CTF | 3v3 spy, reload-anchored | closed | trajectory **1,829**; flags/cloak gate |
| `reference-ctf_truefort-closedairblast.trace` | CTF | 3 pyro v 3 soldier | closed | airblast/reflect — trajectory **1,266**, +hp **1,133** |
| `reference-ctf_truefort-closedsentrykill.trace` | CTF | 3 engineer v 3 soldier | closed | sentry destroy by weapon (exact) and self-destruct (partial, see below) — trajectory **581** |
| `reference-ctf_truefort-closedsandvich.trace` | CTF | 6 heavy, forced eats | closed | `OMNOMNOMNOM` injection — **687**-tick window |
| `reference-ctf_truefort-closedsentryteam.trace` | CTF | 6 engineer, forced team/class switch | closed | sentry self-destruct causes: team change, class change, autobalance (3 of 4 reproduced) |
| `reference-ctf_truefort-spawnroomdeath.trace` | CTF | forced hp=-1000 in spawn room | closed | fast-respawn arm: 1-tick gap vs. ordinary 150-tick |
| `reference-ctf_truefort-autobalance.trace` (`ctf_2dfort-autobalance.trace`) | CTF | 4v2 forced, then re-forced after reload | mixed | `BALANCE` framing and application (both shapes) |
| `reference-ctf_2dfort-closedrespawn.trace` | CTF | 3 scout v 3 soldier | closed | second-map fixture; trajectory **362** |
| `reference-ctf_2dfort-uncontestedcap.trace` | CTF | 6 red scout, no blue, steered | closed | grab + capture inside one window — **4,934** ticks, whole recording |
| `reference-ctf_2dfort-dropintel.trace` | CTF | 6 red scout, no blue, forced drop | closed | `DROP_INTEL` injection |
| `reference-ctf_avanti-closedinvasion.trace` | Invasion (`ctf_avanti`) | 3v3 | closed | the only shipped Invasion map; setup gate |
| `reference-koth_valley-closedkoth.trace` | KotH | 3v3 | closed | control-point framing/comparison plumbing — trajectory **330** |
| `reference-koth_valley-uncontested.trace` | KotH | 6 red, no blue | closed | full capture-rules gate, unlock-timing model — trajectory **3,111** |
| `reference-cp_egypt-closedsymmetric.trace` | CP (symmetric) | 3v3 | closed | 5-point block; meter reading in-window — **892** ticks |
| `reference-cp_egypt-uncontestedcp.trace` | CP | 6 red, no blue, steered | closed | isolates capture-timing bug from drift — **4,617** ticks |
| `reference-cp_dirtbowl-closedcp.trace` | CP, attack/defend | 3v3 | closed | multi-area `ForArea` narrowing — **905** ticks |
| `reference-dkoth_atalia-closeddkoth.trace` | DKotH | 3v3 | closed | 2-point block, no `UNLOCKCP`, hills start owned |
| `reference-tdm_mantic-closedtdm.trace` | TDM | 6 soldier | closed | TDM shape; move boxes present but unreached — **3,043** ticks |
| `reference-gg_debug-closedtdm.trace` | TDM | 6 soldier | closed (short) | busiest recording (52 deaths); landing bug, window only **23** ticks |
| `reference-gen_destroy-closedgen.trace` | Generator | 3v3 | closed | generator HUD block, `GENERATOR_DESTROY`, `MAP_END`/`CHANGE_MAP` — **408** ticks |
| `reference-gen_destroy-rotation.trace` / `-rotationcaps.trace` | Generator → CTF | 3v3 | n/a | mode-changing rotation; HUD re-shaping mid-trace |
| `reference-arena_lumberyard-closedarena.trace` | Arena | 3v3 | closed | `ARENA_ENDROUND` framing (variable length), round loop |
| `reference-arena_lumberyard-waitforplayers.trace` | Arena | 3v3, blue removed | n/a | `ARENA_WAIT_FOR_PLAYERS` — bodyless message, exclusive branch |
| `reference-ctf_truefort-framed.trace` (+`.frames`) | CTF | 6 bots | n/a | flush-fingerprint ground truth from the reference's own wire |

⚠️ **Every one of the closed-loop windows above was re-measured after the
alarm-stage spawn seam and after the collision stage moved from a cursor to a
handle snapshot; all were unchanged except the heavy (270→396 with the seam).**
Re-measure the whole table after any change to spawn timing, collision
ordering, or projectile lifetime — an unmoved window is the expected result
for everything else, and a window that *should* have moved and did not is the
alarm (CLAUDE.md, "the other direction").

### Fixture recipes worth knowing before recording a new one

- **A recorder contributes its own `PLAYER_CHANGECLASS`** — it joins as a
  passive spectator with the default class (Scout), so a single-class
  recording's class set is always `{the class, Scout}`, never `{the class}`.
- **Classify a mode/map by asking the code, never by reading a map's name.**
  Invasion is not an `inv_*` prefix; it is any Capture the Flag map placing a
  `ControlPointSetupGate` (`GameModes.HasSetupGate`) — the only shipped one is
  `ctf_avanti`.
- **`MapRotation` in `gg2.ini` is a file path, not a list**
  (`game_init.gml:94`). With no file, the rotation holds only the current map.
- **Red winning a Generator round does not rotate** — it advances the area if
  the map has one and keeps the same map (`GameServerBeginStep.gml:107`).
  Blue has to win to force `nextMapInRotation()`.
- **A tall/multi-area map (`cp_dirtbowl`) needs its shape narrowed to the
  active area** before framing — `map.ForArea(n)`, not the unnarrowed map's
  full control-point count. This is a no-op on every single-area map, which is
  why it can only be caught on a map like this one.

### Message coverage

`MessageCoverageTests` walks every fixture in the corpus and pins the set of
message types they contain, answering "what state can no recording reach?"
mechanically instead of by memory. Four message names are **dead protocol** —
declared in `Constants.xml` but referenced nowhere in the reference's scripts
or events, and provably unreachable: `RED_TEAM_CAP`, `BLUE_TEAM_CAP`,
`CP_CAPTURED` (a capture reaches the wire only as a `CAPS_UPDATE`), and
`SERVER_KICK`. (Two names differ from the port's own member names and can look
dead when they are not — grep the `Constants.xml` name, not the C# one:
`KICK_MULTI_CLIENT` and `ARENA_WAIT_FOR_PLAYERS` are both used.)

Everything else that is gameplay is now reached by some fixture in the corpus
above. The remainder are structural: the handshake (`HELLO`, `RESERVE_SLOT`,
`PASSWORD_REQUEST`, `PASSWORD_WRONG`, `INCOMPATIBLE_PROTOCOL`, `SERVER_FULL`,
`DOWNLOAD_MAP`) happens before `gg2-parity record` starts writing ticks, and
the kick, plugin and reward families need an operator, a plugin or the medal
server.

⚠️ **The census asserts both directions.** Several message types are held by
**exactly one** fixture (`Balance`, `ArenaEndRound`, `GeneratorDestroy`,
`UnlockCp`, `Uber`), so a re-recording that came out quiet could silently stop
exercising a whole family. Adding a type fails loudly ("add it here"); losing
one fails loudly too ("a recording has stopped exercising a family").

⚠️ **A message can be reached by a recording and still discarded by the
harness — that is the inverse failure and it has happened three times.**
`TOGGLE_ZOOM` and `OMNOMNOMNOM` sat in the checked-in corpus (31 zooms across
four fixtures, four eats across two) unapplied for nine phases before the
replayer was wired to inject them (`ClientCommandInjectionTests` now counts
injections in both directions). `BALANCE` was the same shape a day later. The
question `MessageCoverageTests` asks is not sufficient on its own — also ask
"of what it reaches, what does the harness actually apply?"

### The Begin-Step-guard sentry self-destruct (open)

`reference-ctf_truefort-closedsentrykill.trace` records three sentry destroys,
all `killer 255, cause 0` (self-destruct). The port reproduces the one weapon
kill in the fixture exactly, but **misses the self-destruct that falls inside
the trajectory window** (t496; a second self-destruct at t2296 is 1,300 ticks
past the window and proves nothing). `Sentries.BeginStep`'s three-clause guard
fires the reference's self-destruct one tick after the sentry is built, and
the port's guard does not reproduce it at the same tick.
`SentryKillFixtureTests.ThePortStillMissesBothOfTheReferencesSentrySelfDestructs`
pins both ticks so a fix must edit this test on purpose. Owed to whoever owns
`src/Gg2.Simulation`; not this document's fix to make.

### Move boxes on `tdm_mantic`: cannot currently be recorded

`tdm_mantic` is the only shipped map placing move boxes (fifteen) or pit falls
(six), and no recording has ever put a bot on one. Measured against the live
engine and pinned by `MoveBoxTests`:

| Fact | Measured |
|---|---|
| Up boxes cancelled by a down box at the same `bbox_left`, `bbox_top + 6` | **6 of 9** |
| Up boxes that actually lift | 3, at `(2028,1014)`, `(2100,1266)`, `(2160,852)` |
| `pushPower` | 5 on every box; no `speed` override anywhere |
| A character dropped into `(2028,1014)` | hovers indefinitely (~30-frame period) |
| Scout jump apex | 58 px |

Two thirds of the map's move boxes are inert (a character's 34px box against a
42px up-box always overlaps its down partner too), and the three that work
hang in open air, unreachable by a scout's jump or run — measured, both fall
short. What is untried: a driven soldier or demoman entering a fan on a rocket
or mine jump, with the aim written into the client's own `INPUTSTATE` (aim is
an input, not a teleport — a teleport breaks the closed-loop anchor).
`MoveBoxTests` and `KillBoxTests` remain the whole gate for both families.
`reference-tdm_mantic-closedtdm.trace` is kept as the map's TDM fixture and
gates neither behaviour (checked by mutation: disabling `MoveBoxes.CollisionStep`
leaves its window unchanged).

### Projectiles are state an anchor cannot reach

**An open-loop replay resynchronises characters and nothing else.** A
projectile's position is on the wire only in a `FULL_UPDATE`
(`Character.events/User Event 12.xml`), and a recording holds exactly one, on
join. For most projectiles this does not matter — a `Shot` lives ~35 ticks,
well inside the error a resync would correct — but **a mine lives until
detonated**, so its error has no bound, and if the port loses one the
reference still has, no later message restores it.

On `reference-ctf_truefort-hotcombat.trace` this shows up as 89 hp of damage
the port never deals, entirely attributable to the anchor limit rather than a
weapon bug: every recorded `WEAPON_FIRE` seed is consumed, disabling the
Minegun *increases* the unaccounted damage (to 201), and at the ticks it misses
badly the port holds zero live projectiles at all
(`PortedSimulation.LiveProjectiles`). Tests assert a **bound**, not zero
(`ThePortTracksARealFightWithinABound`, `AProjectileIsStateTheAnchorCannotReach`),
chosen to separate this known limit from a weapon regression. The residual is
otherwise ±1-2 hp from splash-radius sensitivity to the anchor's own
quantisation — not chaseable further without a `FULL_UPDATE`-carrying
recording, which the reference does not send mid-match.

### An unseeded weapon caps an hp — and sometimes a trajectory — window

**Seven of the ten weapons broadcast the seed their spread is drawn from; three
do not.** Scattergun, Shotgun, Rocketlauncher, Minegun, Revolver, Rifle and the
Medigun needle all call `sendEventFireWeapon(ownerPlayer, seed)`, so a replay
re-derives them exactly (`ITraceSimulation.SetFireSeed`). The **Flamethrower**,
the **Minigun** and the **Blade** call `randomize()` and broadcast nothing —
their spread exists only on the reference server and cannot be reproduced.

A recording containing any of the three has an hp window that ends at the
first shot of theirs that connects — a property of the recording, not a defect
to chase. Whether it *also* caps the trajectory window depends on whether the
weapon moves people:
`Projectiles.BurningHitCharacter` deals damage with no `motion_add`, so a
misplaced flame cannot shift a trajectory (`closedsupport` stays a hp-only
cap); a Minigun's pellets can and do, which is why `closedheavy` stops on
**position** (t769/396) rather than only on hp.

⚠️ **Check the sign of a residual before blaming a missing feature.** On
`closedsupport`'s t1102, the port read three hp *low* — over-damaging or
under-healing, never explainable by an unimplemented weapon (which can only
leave the port high). Two separate sessions misread this window as "waiting on
the pyro/medic" before checking the sign.

**Fixtures whose only classes broadcast seeds can carry an hp claim past their
trajectory window** — `closedcombat`'s hp window is **1,204** ticks against a
357-tick trajectory window, because a seeded pellet still lands correctly even
after a rounding-boundary or `place_free`-threshold divergence (see below) has
ended positional exactness. **When adding a fixture, ask which weapons are in
it** — one meant to gate damage should hold only classes that broadcast their
seeds.

### A weapon's knockback is part of the trajectory, not only its damage

Adding the engineer's Shotgun to `closedsupport` moved the **trajectory**
window from 412 to 489 ticks, purely from `motion_add(shot.direction,
shot.speed * 0.03)` on each landing pellet — more than halving the hp
mismatches over the whole recording and reproducing three previously-unreachable
kills. The corresponding open-loop fixture, `hotsupport`, shows the same shape:
p99 `|dy|` is 17 wire units with the Shotgun in and 30 with it stubbed out; the
vertical **maximum** barely moves (166 either way, one stair step — see below),
so **a knockback moves the percentile, not the maximum**, and a bound placed
only on the maximum would miss it.

### Reading a closed-loop residual: rounding boundaries and thresholds are not bugs

**A closed-loop replay reports the first tick on which the port and the
reference disagree about an *integer*.** An observation is the reference's own
position rounded to a fifth of a pixel (`WireQuantise`, half away from zero),
so an apparent divergence can be the port sitting a fraction of a wire unit
past a rounding boundary while the underlying physics is still exact.
`Gg2.Parity.SubWireProbe` (an `ITraceSimulation` decorator over
`PortedSimulation`) reports the un-rounded residual (`port*5 - observed` for
position, `port*8.5 - observed` for velocity) so this can be told apart from a
real bug: `closedcombat`'s apparent divergence at t1274 is 0.0014 wire units
(0.0003 world pixels) past the boundary, and the replay is clean again for
another 400 ticks. At tolerance 0.5 the probe reproduces the integer
comparison exactly (`PhysicsParityTests.TheSubWireProbeAgreesWithTheIntegerComparison`).

⚠️ **The tolerance-1 window is not strictly stronger than the exact window and
does not replace it.** A rounding boundary is the one place an integer
comparison is *more* sensitive than any tolerance — measured by mutation, a
one-ULP gravity error collapses the exact window from 357 to 28 ticks while the
tolerance-1 window stays at 756, and a genuine ordering bug (`Projectiles.Move`
before vs. after the character move) collapses **both** to 336. Both gates are
kept for this reason (`PhysicsParityTests.ThePortStaysWithinAWireUnitOfARealFight`).

**A genuine physics *threshold* — not a rounding artefact — can also end an
exact window without being a bug.** `Gg2.Parity.BinSweep` anchors one slot from
the wire and sweeps it across the quantisation bin the observation was rounded
from (0.1 px position, 1/17 px/tick velocity), counting how many of those
states reproduce the target tick exactly. Wide coverage (measured: 34/49 at
`closedcombat`'s t1673, a `place_free` boundary the port clears by 0.0087 px)
means every rule is right and the port is a hair off a threshold the wire
cannot resolve; zero coverage means a real rule bug with an address (measured
with a deliberately broken `good_move_contact_solid`: 0/49, and the divergence
relocates entirely). `PhysicsParityTests.TheDivergenceThatEndsTheWindowIsAThresholdRatherThanAWrongRule`
is the gate. ⚠️ **Do not try to settle this by measuring a mean bias in the
residuals** — the samples are autocorrelated (a resting character reports the
same `y` for dozens of ticks), so a naive bias test reports significance
(z=16) that is purely an artefact of that autocorrelation.

**Reading a divergence table**: a mismatch whose *horizontal* half matches the
*previous* tick's port state and whose *vertical* half matches *this* tick's is
one axis running a tick early from a discrete event (a collision resolving one
tick sooner), not an arithmetic error — this is how the t1673 case above was
diagnosed as a threshold rather than a rule bug.

### Reading an open-loop residual: discrete events, not accumulated error

**Before blaming the physics for an open-loop residual, check whether its
characteristic size is one discrete event.** On
`reference-ctf_truefort-hotcombat.trace` the p99 vertical error is exactly 30
wire units — one stair step (6 px) — from a character landing one tick earlier
or later than the reference and therefore being one step further up a slope at
the next comparison. `PortedSimulation.Nudges` sweeps the anchor's velocity
across its own quantisation width; the residual is acutely sensitive to
sub-wire-unit velocity (a discrete amplifier's signature) and the unnudged
value is the *best* of the sweep, consistent with the port being right and the
anchor's own estimate being unbiased. ⚠️ **This kind of bound is near its floor
and tightening it is not a goal** — it is useful for catching things that make
it much worse (disabling the Rocketlauncher takes this fixture's `|dy|` from 75
to 240).

### `PLAYER_LEAVE` must compact the roster in the harness too

A roster index is positional: a player leaving renumbers everybody above them,
and the reference's own state updates are already compacted by the time a
recorder sees them. `ITraceSimulation.RemoveSlot` /
`Gg2.Simulation.WorldState.RemoveSlot` do the compaction and renumber every
stored roster index (a projectile's owner, a last damage dealer, who set
somebody on fire, a medic's heal target). Any per-slot state the harness keeps
outside `WorldState` (`PortedSimulation` keeps a departed player's class/team,
since both outlive the character) has to be shifted by hand on the same event.
`PhysicsParityTests.APlayerLeavingDoesNotShiftTheComparisonOntoTheirNeighbour`
is the gate — an open-loop bound rather than an exactness claim, per the
two-kinds-of-fixture rule above.

### An anchor must not touch the input history

Jump, the demoman's right-click and the spy's cloak are all edge-detected
(`KeyState & ~LastKeyState`). A state update carries **this tick's `keys`
alongside last tick's position**, so anchoring `LastKeyState` from the update
hands a character a key it is about to be told about as though it were already
held — the edge computes empty and the press never happens. This only bites
when a key is first pressed on an update tick (1 in 7) for one of the three
edge-detected keys, and only a demoman's single-tick jump press exposed it
(`reference-ctf_truefort-combat.trace`, tick 9): anchoring `LastKeyState`
produced a 217-wire-unit (43px) divergence; leaving it alone, 20. **The
replay's own input history is the authority** — it has seen every `INPUTSTATE`;
the update's `keys` is the same thing arriving a tick later, and reapplying it
would double-count. This is an instance of a general rule: **an anchor exists
to correct only what the wire measures** (position, velocity, hp, ammo);
anything the replay derives for itself from the message stream (an input edge,
a timer, a queued event) must be left alone, because the wire's copy of it is a
tick out of date by construction. Gated by
`PhysicsParityTests.AnAnchorDoesNotSwallowAFreshKeyPress` and
`APlayerLeavingDoesNotShiftTheComparisonOntoTheirNeighbour`.

### Spawning inside a tick: the alarm stage, and its remaining harness gap

The reference creates a spawning character in the **alarm stage**
(`player.alarm[5]` fires `PlayerSpawn`, running between Begin Step and Step),
so on its spawn tick a character gets a Step and End Step but **no Begin
Step** — Begin Step is where gravity applies. Its first End Step therefore
still sees `vspeed == 0`, which is the guard on the stair-descent snap in
`Character.events/End Step.xml` (`if(vspeed==0 and place_free(x,y+6) and
!place_free(x,y+7)) y += 6;`) — so a character spawning within six pixels of
the floor is snapped onto it on its first tick rather than dropped over
several. `ITraceSimulation.QueueSpawn` reproduces this: it asks the simulation
to build the character inside the *next* tick's alarm stage rather than
placing it before the tick runs, arming `player.alarm[5]` and handing
`MatchSimulation.Tick` the map's spawn points so the tick itself creates the
character where `PlayerSpawn.gml` does. Verified both ways on `gg_debug`
(`SpawnTickTimingTests`): alarm-stage creation lands exact (`y=168, vspeed=0`);
pre-placing then ticking leaves the character airborne.

⚠️ **The spawn must be queued a tick EARLY** — off the *next* tick's messages,
the same off-by-one a fire seed and a death share — because
`GameServerBeginStep.gml` serialises state before the alarm stage runs, so a
character created on a tick cannot appear in that tick's own update. Queueing
on message arrival is a tick late and fails every spawn-anchored fixture
(measured: it broke twenty tests at once).

⚠️ **Forcing the spawn-point RNG draw is fragile against an unseeded weapon
firing in the same tick.** `TryForceSpawnPointDraws` arranges the shared GML
stream so the alarm stage's `floor(random(numSpawnPoints))` lands on the
recorded id — but weapons fire in Begin Step, ahead of the alarm stage, and an
unseeded one (Flamethrower, Minigun, Blade) draws from the same stream first.
`PortedSimulation.SeedIndependentSpawnPoints` removes the dependency instead:
it hands the tick a spawn-point list whose every index for a spawning team
resolves to the recorded point, so `floor(random(n))` can return anything and
still land correctly — but **only when at most one slot per team is spawning**
in that tick; a multi-slot wave still needs the seed-forcing route (sound
there because a wave happens at round start, before anything is shooting).
`SpawnPlacementTests` replays every fixture in both modes and asserts no
queued spawn lands off its recorded marker
(`PortedSimulation.MisplacedSpawns`) — currently green on all fixtures, with a
control showing the narrowed-list mechanism is exactly what fixes the three
recordings containing a pyro (`closedsupport`, `closedairblast`,
`hotsupport`).

### Sentry injection

A `BUILD_SENTRY` in a recording is injected like a `PLAYER_SPAWN` —
preconditions are not re-run, everything after placement (fall, construction,
turret fire, death) is simulated. Two conventions:

- **The anchor prefers the simulated builder's own position over the broadcast
  one.** The reference builds at `player.object.x`, a full double; the message
  carries `round(x*5)`. In a closed loop the port's own character is already
  at the exact position, so `PortedSimulation.BuildSentry` uses it and falls
  back to the quantised message only when the slot has no character.
- **`BUILD_SENTRY` and `SENTRY_POSITION` use opposite tick conventions.**
  `BUILD_SENTRY` is written pre-update (`processClientCommands`, before
  `serializeState`), so it applies within its own recorded tick.
  `SENTRY_POSITION` comes from the sentry's Begin Step *after* the update, so —
  like a `WEAPON_FIRE` — it is recorded under the *following* tick.

`SupportClassFixtureTests.TheInjectedSentriesLandOnTheRecordedTickAtTheRecordedSpot`
checks both recorded landings by tick, slot and quantised position through a
`SentryWatcher` decorator over the real replay loop.

### `DROP_INTEL` and other client commands are injected, not derived

`DROP_INTEL`, like `BUILD_SENTRY`, must be **injected** — nothing in an
`INPUTSTATE` says a player pressed drop, so a replay cannot derive it, and an
unhandled message would carry the flag on forever. `ITraceSimulation.DropIntel`
is the seam. ⚠️ **It applies BEFORE the tick — the opposite of a grab.**
`processClientCommands` runs in the server's Begin Step, ahead of
`serializeState`, so the message lands in its own tick's block; `GRAB_INTEL`
comes out of the collision stage, after the update, landing in the *next*
tick's block. The two halves of the same objective use opposite conventions.

⚠️ **`message.PlayerId` is null for this whole message family** — `ServerStream`
frames `DROP_INTEL`, `GRAB_INTEL`, `SCORE_INTEL`, `UBER`, `OMNOMNOMNOM` and
similar through `Fixed()`, which skips the body without decoding it, so a
`when message.PlayerId is { } slot` guard silently never matches. Read
`Payload[0]` directly.

⚠️ **A drop can be followed by an instant re-grab on the same tick, and that is
correct** — a flag taken off its base gets its carrier a return timer of 0, a
drop writes that into the new instance's `alarm[0]`, and the grab guard's
`alarm[0] <= 1` arm lets anyone pick it straight back up. Assert on the named
slot, not on "nobody carries the flag."

The reference has exactly **three** commands that are not key bits — zoom,
build, and eat (`Scripts/Bots/botServerActions.gml`) — handled adjacently in
`processClientCommands.gml`. `TOGGLE_ZOOM` (`runPower = 0.6`, `jumpStrength = 6
+ 0.6/2`) and `OMNOMNOMNOM` (freezes acceleration/facing for 128 ticks, heals
1.6 hp/tick) are physics, not HUD flags, and both must be injected the same
way. `BALANCE` (not a client command, but `ServerBalanceTeams.gml`-decided)
shares the failure mode exactly: it moves a player to the other team and
destroys their character with `instance_destroy()`, broadcasting **no
`PLAYER_DEATH`** — the two-byte message is the entire record.
`ClientCommandInjectionTests` counts injections in both directions for all
four.

⚠️ **One recorded eat is refused on purpose** — `closedheavy`'s first
`OMNOMNOMNOM` is in the t0 block (the character was already eating when the
recorder connected, so the replay has no character to feed).
`ClientCommandInjectionTests` pins the refusal at exactly one; a second refusal
would be a real cooldown disagreement.

### Objective events: what is gated and what is not

`Fields.Intel` matches every recorded `GRAB_INTEL`, `DROP_INTEL`,
`SCORE_INTEL` and `RETURN_INTEL` as a multiset against what the port produced,
both directions. **This exists because nothing else can see the objective** — a
briefcase's position and a carrier's `intel` byte are `FULL_UPDATE`-only, and a
recording holds exactly one, on join, before anything has happened.

- `reference-ctf_2dfort-uncontestedcap.trace` (six red scouts, no blue,
  steered) has both a `GRAB_INTEL` and a `SCORE_INTEL` inside one exact
  4,934-tick window — the only fixture that gates a capture end to end.
- `closedsupport`'s single grab lands on its recorded tick even though the
  fixture has stopped being bit-exact by then (969 ticks past its 489-tick
  exact trajectory window) — a replay that has drifted still walks the same
  bot into the same briefcase on the same tick.
- `hotsupport`'s `SCORE_INTEL` (t59) belongs to a bot that was already carrying
  before the recorder connected, which no current replay can reconstruct: the
  join `FULL_UPDATE` is filed by `TraceParser` under `start` and never applied
  by `Replayer`, so every replay begins with both flags on their bases. Closing
  this needs applying `start` before the first tick, which would also move
  every character's initial position earlier than the first `QUICK_UPDATE` —
  unattempted, because it can move every measured window and needs its own
  before/after.
- `closedcombat` loses two characters simultaneously at t767 with **no
  message of any kind** (the shape of a round reset, not a normal death), which
  makes its later grabs and its would-be `RETURN_INTEL` unusable evidence —
  the port's own handling (treating each vanishing as a destruction) is
  correct for what it can see, but the divergence this produces is well before
  the fixture's t917 exact anchor and proves nothing about the objective.

**When adding a fixture, ask whether it contains a capture with its own
grab.** Only a recording that starts before the grab and runs past the capture
gates `doEventScoreIntel` end to end.

### Control points: framing, comparison, and one closed harness bug

The control-point block carries **no count prefix** — a reader has to rebuild
the same points from the same map in the same order, or desynchronise after
the first update. `koth_valley` (1 point), `dkoth_atalia` (2), `cp_egypt` (5,
the largest block anywhere) and `cp_dirtbowl` (narrowed to 2 of its 6 via
`ForArea`) each control this independently — parsing any of them at the wrong
count fails immediately.

`TraceTick.ControlPoints`, `Fields.ControlPoints`,
`ITraceSimulation.TryGetControlPoint` and `Replayer.ReconcileControlPoints`
compare the block. Three things about it are worth knowing:

- **The block rides on a `CAPS_UPDATE` or the join `FULL_UPDATE` only** —
  `StateUpdateReader` returns before the HUD tail on every other tier — so
  `TraceParser` must take it from the tick's `pending` list, and the
  comparison must **not** be gated on `HasPositions` (a `CAPS_UPDATE` arrives
  independently of the positional cadence; gating on it silently skips every
  observation).
- **`PortedSimulation.LoadMap` must call `ControlPoints.Place` with the mode
  already set from the map** (`state.GameMode`, not a defaulted CTF), and must
  call `Rounds.Start` for the pregame lock — three separate omissions found by
  the first three control-point fixtures, all now fixed and gated
  (`KothFixtureTests`).
- **The comparison must run before the tick advances, not after.** The block
  rides in a tick's state update — serialised in Begin Step, describing the
  *end of the previous tick* — exactly like position. Comparing after
  `Advance` held the port's end-of-this-tick meter against the reference's
  end-of-last-tick one, worth exactly one tick of capture progress
  (`+2` on a `capStrength`-1 recording, `+4` doubled on a `capStrength`-2 one —
  the same one-tick error, not a rate error). The correct order is **inject
  unlock → compare → advance**; the unlock injection must stay ahead of the
  comparison because it re-locks and zeroes the meter.

⚠️ **A window containing a control-point observation is not the same as the
observation falling *inside* the exact window.** `koth_valley-closedkoth`'s
nine post-anchor observations all fall outside its 330-tick trajectory window,
so that fixture proves the plumbing (framing, ownership, wire round-trip) but
not the capture rules — only the uncontested variants, whose windows run
thousands of ticks, can gate the rate/timing arithmetic itself.

**The KotH unlock model, measured on two independent recordings and exact on
both:** `unlock = (spawn wave − 150) + 901` — 901 because `KothHUD`'s
`cpUnlock = 900` decrements *after* the check, so the unlock fires 901 ticks
after the HUD is created (i.e. after the round restarts), and 150 is the
ordinary respawn time. `CHANGE_MAP` is **not** the round start (both
recordings carry one `CHANGE_MAP` at t0 and unlock at two different later
offsets). The first HUD readings after a reload are **stale** (the old round's
HUD keeps serialising for a few dozen ticks), so **only a positive `cpUnlock`
reading may be taken as an anchor** — zero is ambiguous between "already
unlocked" and "old round." And the unlock is a **latch**: re-locking a point
must also zero its meter (`ControlPoint`'s Step does `capping = 0; exit;`).
`Replayer` anchors `cpUnlock` from the wire and reproduces
`koth_valley-uncontested` with zero control-point mismatches
(`KothCaptureFixtureTests.ThePortReproducesTheHillExactly`).

**DKotH never sends `UNLOCKCP` and KotH always does** — the one message that
tells the two modes apart on the wire (`dkoth_atalia-closeddkoth.trace`); both
hills start owned by their own team, from `DKothHUD`'s Create event rather than
`User Event 0` (every other mode's points start neutral).

### `BALANCE` and the four self-shortening messages

`BALANCE` is not fixed-width — `BALANCE, 255` (a "teams are uneven" notice) is
one body byte, `BALANCE, playerId, class` (a switch) is two, and the client
decides which after reading the first byte (`ClientBeginStep.gml:244-253`).
Reaching it needs three conditions at once: a team two or more players larger
than the other, at least one death (`ServerBalanceTeams` is called only on a
death/departure/team-change, not every tick), and the fuse expired with a dead
player available (`balancecounter >= Server_Respawntime + 30*2`, 210 ticks by
default, eligibility read from the *character* so an awaiting-respawn player
qualifies). `reference-ctf_2dfort-autobalance.trace` reaches all three and
frames three `BALANCE` messages of both shapes cleanly.

`ARENA_ENDROUND` is similarly variable-length: five bytes plus five per MVP,
and a fixed-length reader loses the stream the first time a round ends with a
nonempty MVP list. `reference-arena_lumberyard-closedarena.trace` carries
three round-ends (one with MVPs) and frames to the last byte.

⚠️ **When adding a message, ask what makes its length change.** This family —
the sentry `INPUTSTATE` block, `ARENA_ENDROUND`, the generator HUD block and
`BALANCE` — produced four framing bugs, and every one survived until a
recording happened to reach the state that shortens or lengthens it.

### The generator HUD block does not shorten on the only shipped map

`docs/PROTOCOL.md` records the generator block as one record per *surviving*
generator, which would make `GeneratorCount` a value a reader must decrement on
destruction. `reference-gen_destroy-closedgen.trace` shows the wire carries
**two** records for the whole recording, including the 300 ticks between a
destruction and the resulting `MAP_END`/`CHANGE_MAP` (measured with three
control values — decrementing to 1 desyncs, leaving at 2 frames clean,
incrementing to 3 desyncs). This is because destroying a generator ends the
round immediately and a round ending does not sweep the map — the dead
instance survives the scoreboard and the HUD keeps writing it. `gen_destroy`
has exactly one generator per team, so a destruction there always ends the
round; a hypothetical custom map with two per team could still reach the
shortened block, but no shipped one can.

Two further measured facts: `GENERATOR_DESTROY` names the team that **lost** a
generator (not, as `sendEventGeneratorDestroy.gml`'s own docstring implies, the
winner), and the map change follows `MAP_END` by exactly **300 ticks** (ten
seconds), confirmed independently on both the red-wins/same-map path
(`GeneratorFixtureTests`) and the blue-wins/rotates path
(`reference-gen_destroy-rotation.trace`).

⚠️ **`TraceParser` takes one wire shape for a whole trace file**, re-shaping
only on `CHANGE_MAP` via an optional `Func<string, WorldShape>` resolver
(`gg2-parity`'s census/replay/diff and `FixtureFramingTests` all pass one).
Without it, a recording that changes gamemode mid-stream and then sends a
`CAPS_UPDATE` or `FULL_UPDATE` on the new map desynchronises — Generator's HUD
block is 13 bytes, Capture the Flag's is 5, and reading one recording with the
other's shape fails partway through. `reference-gen_destroy-rotationcaps.trace`
forces a post-rotation `CAPS_UPDATE` specifically to exercise this;
`ModeChangeFramingTests`'s first assertion is a control proving a single shape
*cannot* frame that recording, so the fix cannot regress unnoticed.

### `WEAPON_FIRE`, `DROP_INTEL` and other events land one tick later than they happened

Repeated for emphasis because it governs every event-message comparison in
this document: **a message produced during tick *n* is recorded under tick
*n+1***, because `GameServer`'s Begin Step (where the update serialises) runs
before any character's own Begin Step or End Step. `SupplyFireSeeds`,
`ReconcileDeaths`, `ReconcileUbers` and the control-point comparison all read
one tick ahead for this reason; `DROP_INTEL`, injected pre-update, is the one
exception, applying within its own recorded tick.

### Death timing: 150 ticks, measured from the wire, not from a counter

`RespawnTimingTests` measures every death-to-respawn gap across the fixture
corpus (60 pairs across eleven recordings as of the last count) directly from
recorded `PLAYER_DEATH`/`PLAYER_SPAWN` tick indices — a measurement that needs
no simulation at all, and every gap outside a spawn room is exactly **150
ticks** (`Server_Respawntime`'s five-second default at 30 Hz) with zero
variance. This closed a gap that unit-testing the port's own counter could
not: a counter armed in the wrong event, read in the wrong stage, or off by
one against the engine is invisible to a test that only checks the counter
against itself. The port's half is driven through whole
`MatchSimulation.Tick` calls and read via `Alive`, not the counter — the
counter reaching zero and a character existing again are two different events
an alarm stage apart, and only the second is what a client sees.

**A death inside a spawn room is answered on the very next tick** (a gap of 1,
not 150) — `Character.events/Destroy.xml:37` arms `player.alarm[5] = 1`
instead of the respawn time.
`reference-ctf_truefort-spawnroomdeath.trace` (hp forced to -1000 while
`place_meeting(x, y, SpawnRoom)` is true in the same eval — a character
standing on a healing cabinet undoes an ordinary `hp = 0` before End Step ever
tests it) carries this gap alongside two ordinary 150-tick deaths in the same
recording, which is the control that rules out "this server's respawn time
just is one tick."

`WorldState.RespawnTime = 0` (the harness default) means `Deaths.Kill` and the
team-change re-arm never fire the port's own respawn timer during a replay —
**a replay must never respawn anybody by itself**; every `PLAYER_SPAWN` is
injected from the recording, including the reference's own spawn-point pick,
and a port racing its own pick against the injection would spawn at a
different point on a different tick. The reference floors its own value at one
tick (`game_init.gml`), so 0 is unreachable there and unambiguous here
(`PlayerLifecycleTests.WithRespawnTimeZeroADeathArmsNothing`).

## Tier 3 — a real client, watched

The end-to-end check the other tiers cannot give: launch a stock GM8 client
against the ported server and play. `gg2-agent` automates this —
`tools/launcher.js` starts a client, `gg2_session` manages a server-plus-clients
set, `gg2_screenshot`/`gg2_area_shot` see what is happening, and `gg2_evalx`
reads the client's own predicted position (`Character.syncWrongness`) so it
can be compared against what the port believes.

### The gap every automated gate has: nothing compares what a client was TOLD

**Every automated gate in this project reads the SERVER.** The replayer
compares the port's state against a recording of the reference; the mirror
below compares the port against its own reader. **Nothing compares what a real
GM8 client parses and derives from the port's bytes** except driving one by
hand — so a bug that lives entirely in the gap between the server's belief and
the client's cannot go red however green the suite is.

`MirrorClient` and `ClientRosterAgreementTests` close half of this: the mirror
keeps its own roster from `ServerStream` (teams included), applies the
client's own rules (including that `CHANGE_MAP` spectates everybody on area 1),
and asserts both that the two rosters agree and that no character is ever sent
for a slot the client holds as a spectator.

⚠️ **The mirror has a structural blind spot: it shares the port's own reader.**
It frames with `ServerStream` and shapes with `GameModes.ShapeOf`, so it can
only catch the port disagreeing with itself or failing to send something. A
disagreement between the port's **writer** and the **reference client's**
reader is invisible to it by construction — both ends of the comparison are
this port's own code. The gamemode-change bug below sat exactly in this blind
spot: it killed every real client and did not reproduce in-process at all,
because the port's reader and writer agreed with each other about a layout the
real client read differently.

**So the tiers now stand as:** the mirror covers "did the server say it", the
fixture corpus covers "does the port match a recording of the reference", and
**nothing covers "does a stock client read what the port writes"** except
driving one by hand. Do not let a green suite, mirror included, stand in for
that.

### Fixed: a gamemode change desynchronised every stock client

`StateUpdateWriter` captured its `MatchState` once, in `Start`, but
`GameServer.OpenMap` replaces `_match` with a new object on every rotation — so
after any map change the writer kept emitting the **previous** map's gamemode
HUD block. This is invisible until the first scoreboard update after the
change (the HUD block rides only on `FULL_UPDATE`/`CAPS_UPDATE`), then
desynchronises the stream permanently, killing the client 25-30 seconds later.
Fixed by making the writer take a `Func<MatchState>` instead of a value.

Verified live with a stock GM8 client through four gamemode changes in one
session (`ctf_truefort` → `koth_valley` → `tdm_mantic` → `cp_egypt` →
`ctf_truefort`) — zero client errors, zero dialogs, no disconnect, roster
correct throughout; the same client died 28 seconds after the first change
before the fix. `CapLimit`, `ClassLimits`, `RespawnTimeSeconds` and
`CurrentMapArea` shared the same stale-capture bug in `StateUpdateWriter`,
corrupting a joining client's scoreboard rather than the stream.

⚠️ **Why the existing suite missed it, and what changed:** a same-gamemode
rotation is harmless (stale gamemode equals live gamemode, so it was never a
control for this bug), and `ClientRosterAgreementTests` was watching but ran
too short to trigger a post-rotation `CAPS_UPDATE` at all. **A layout test has
to make the layout actually get written** —
`TheHudBlockAfterAGamemodeChangeIsTheNewGamemodes` now runs to the two-minute
scoreboard heartbeat and asserts a `CAPS_UPDATE` arrived, in both directions
(CTF→KotH and KotH→CTF are different stale pairings). No trace fixture could
have caught this either — no recording in the corpus contains a map rotation.

### Fixed: every custom map was unjoinable by a stock client

`WriteJoinBurst` sent `CHANGE_MAP` with a **hardcoded empty MD5**.
`ServerJoinUpdate.gml:8` passes `global.currentMapMD5` there, and an empty MD5
is not "no hash handy" — to a client it means **internal map**
(`ClientBeginStep.gml:476`). A custom map is not in `findInternalMapName`'s
list, so every joining client showed
`Server went to invalid internal map: <name>#Exiting.` and dropped to the menu.
**It could never join at all.**

⚠️ **The whole custom-map feature was dead on a real client** — `--maps`,
`MapDownload`, `--map-download-limit`, the map-change navgen — while
`MapDownloadTests` was green throughout. Every test in that file checks the
**HELLO**, which always carried the right hash; nothing read the **join
burst**, which is the message that actually makes a client load the map. The
two are separate messages carrying the same pair, and only one of them was
tested. `EveryShippedMapIsInternal` could not catch it either: it asserts every
*reference-shipped* map is internal, and the only custom map in the repo is the
port-authored `gg_debug2`, which that census deliberately excludes.

Found by serving `gg_debug2` to two stock clients on 2026-09-05: both died on
arrival with that dialog, and the server then logged `0 client(s)`. Fixed by
passing `_currentMapMd5`; gated by
`MapDownloadTests.TheJoinBurstCarriesTheMapHashAndNotAnEmptyOne` with
`TheJoinBurstOnABuiltInMapCarriesAnEmptyHash` as the other direction, both
mutation-checked. **Verified live afterwards**: a stock client with an empty
`Maps/` directory downloaded `gg_debug2.png` from the port, rejoined, and
played in `CustomMapRoom` — the server logging `Player joined as player 4`.

⚠️ **`MirrorClient` is blind to this by construction** and it is a clean example
of the blind spot above: the mirror frames with the port's own `ServerStream`,
which reads an empty MD5 quite happily. Only the *stock client's* reader treats
it as a claim about the map being built in.

### Fixed: a joiner was sent the tick it had already been given

**A client joining on the tick somebody died was sent that death, on top of a
`FULL_UPDATE` that had already buried the victim.** A stock client then runs
`doEventPlayerDeath` against a slot whose `object` is `-1` and dies on
`doEventPlayerDeath.gml:41`, `victim.object.currentWeapon.object_index` —
GM8's "Unknown variable currentWeapon". Four of exactly that were logged live
on 2026-09-05 by a client that joined while bots were fighting; the client
survived with warnings, which is why it had never been chased.

**Where the tick puts the two halves.** The burst is written at the very bottom
of the tick, in `ServiceJoiningConnections` (`GameServer.cs:822`), so its
`FULL_UPDATE` carries the world as `AdvanceSimulation` (`:797`) left it —
that tick's deaths included. The broadcast buffer the joiner then inherits was
filled *between* the flush (`:802`) and that point, by `BroadcastEndStepEvents`
(`:807`) and by the control port and web admin executing against the live game
(`:812-814`). The joiner got the state *after* the events and the events as
well.

⚠️ **The reference does not have to solve this, because its burst PREDATES the
events** — not because it sends them safely. `GameServerEndStep.gml:18-20`
services a joining player in the GameServer's End Step, which is the *first*
End Step of the frame, so `Character.events/End Step.xml:43`'s
`doEventPlayerDeath` has not run yet and the dying character is still in the
`serializeState` the joiner is handed. The port fuses Step and End Step into
one `MatchSimulation.Tick` and cannot serialise between them, so it closes the
gap from the other end: `ClientPlayer.SkipBroadcastBytes` records the buffer
length standing when the burst was written, and that client's next flush skips
it. One consistent story either way; the port tells the other one.

Everything droppable is state-carrying and therefore already in the
`FULL_UPDATE`: `PLAYER_DEATH`, `RETURN_INTEL`, `DESTROY_SENTRY` and `BALANCE`
from `BroadcastEndStepEvents`. The mark is taken **before** the joiner's own
`PLAYER_JOIN` is appended, so the joiner still learns about itself — without
that, a stock client never binds `global.myself`
(`ClientBeginStep.gml:205-208`) and is broken for the rest of the session with
nothing on the server to show for it.

⚠️ **An earlier version of this list ended "…and a control-port or web-admin
`kick`'s `PLAYER_LEAVE`". That was wrong, and the correction matters more than
the fix does** — see "Nothing moves a roster count inside the join window"
below.

Gated by `JoinDuringDeathTests`, and both halves are mutation-checked: sending
the whole buffer again produces one blind death per exercised race (five of
five, three runs), and overshooting the mark past the joiner's own
`PLAYER_JOIN` turns the self-join assertion red.

✅ **Verified live, which is the half a green suite cannot supply here.** A
stock GM8 client joined a `--bots 8` server on `ctf_truefort` and came up with
`playerID=8`, a nine-entry roster, `global.myself` bound to its own `Player`,
and **zero** GML errors over several minutes — so the skip did not eat the
joiner's own `PLAYER_JOIN`, which is the one way this change could have broken
every join at once. The join went through the bot-displacement race as well
(`bot [BOT] 8 removed from player 8` immediately followed by `Player joined as
player 8`), and the client's roster still matched the server's.
⚠️ **That run needed `Music=3` first** — see `docs/TRAPS-LIVEGAME.md`,
"`Music=1` truncates every client's room setup on a machine with no audio";
with music on, an ignored audio dialog abandons `basicRoomSetup` two thirds of
the way through and buries any real finding under a thousand `CTFHUD` errors.

⛔ **The test's second assertion is the load-bearing one.** "No blind death
arrived" is satisfied just as well by a run where no join ever landed on a
death tick, so the test counts the races it actually exercised, from the
server, and fails if it saw fewer than five. Two traps were paid for here:
bots piled on a *single* point produce **zero** deaths — splash weapons are not
fired at zero range — so the two teams are teleported to piles 90px apart; and
the race must be detected with the bot population held FLAT (`FillToPlayers`
far above `MaxBots`), because a displaced bot compacts the roster and every
slot above the departure then reads as a death.

⚠️ **What this does NOT explain.** The live capture that found it also held 17
`global.myself.object` errors and, separately, the `Wrong number of players
while deserializing state` dialog behind the open `cp_dirtbowl` fatal below.
Neither follows from this bug: a roster-count disagreement needs a
`PLAYER_JOIN` or `PLAYER_LEAVE` inside that window. The `global.myself` half is
now settled and is **not a port bug at all** — `docs/TRAPS-LIVEGAME.md`, "the
frame a joining client does not know itself". The count half is still open, and
the window is a narrower suspect than this section used to say.

### Nothing moves a roster count inside the join window

**Established 2026-09-05 by reading every writer, after the fix above was
already in.** The window is `GameServer.cs:802`→`:822` and exactly three things
can append to `_broadcast` inside it: `BroadcastEndStepEvents` (`:807`), the
control port (`:812`) and the web admin (`:814`). **None of the three can change
`_roster.Count`**, and that includes the two verbs this document previously
named as the ones that could:

- ⛔ **A `kick` is not a writer here.** `GameServer.Control.cs:255-276`
  (`KickPlayer`) sends `SERVER_KICK` on that one socket and calls
  `Connection.Close` — it appends **nothing** to the broadcast. The
  `PLAYER_LEAVE` is written by `RemoveClient` (`GameServer.Commands.cs:445`),
  reached from `ServiceClients` at `:697` — *above* the flush, on the following
  tick. Gated by `ClientIdentityTests.AKickInFlightDoesNotCostAJoinerItsIdentity`.
- ⛔ **A control-port or web-admin `bots` is not a writer here either.**
  `SetBotFill` (`GameServer.Control.cs:466`) only moves `ServerOptions.Bots`;
  the population manager converges at `_population.Service()` (`:715`), above
  the flush.
- The web admin reaches the same verbs — `WebAdminServer.Pump` dispatches
  through `ControlCommands`, so it has no extra surface.
- `AcceptConnections` and `ServiceJoiningConnections` write to the joining
  socket alone, except `CompleteJoin`'s own `PLAYER_JOIN`, which is appended
  *after* that joiner's mark by construction.

⛔ **So "the join window moved the count" is no longer a live hypothesis for the
`cp_dirtbowl` fatal, and the `SkipBroadcastBytes` fix cannot have closed it.**
The remaining candidates are elsewhere: a message whose byte length the port and
a stock client disagree about (the class that produced the `MAP_END` area byte
and the empty-MD5 bugs), which misaligns the stream and makes
`deserializeState.gml:12` read a garbage byte as the count.
**What would falsify this section**: any `PLAYER_JOIN` or `PLAYER_LEAVE`
appended to `_broadcast` from code reachable between those two line numbers. If
one is ever added, the mark still covers it — the claim being made here is about
the hunt, not about safety.

### Multi-area maps: one bug fixed, one intermittent client fatal still open

`cp_dirtbowl` is the only shipped map with more than one area (three, sharing
one walkmask, differing only in entities). `ServerStream` skipped the area
byte in `MAP_END` — the only place the wire states the area after a join — so
any reader sat on area 1 forever: wrong `ForArea` narrowing, and teams wiped on
an advance that should keep them. Fixed and gated by `MultiAreaMapTests`
(mutation-checked). Verified live with a real client across area 1→2→3, area
1→3 directly, and natural rotations, with both branches of the advance
decision observed (defenders holding rotates away; red winning the last area
rotates rather than advancing, since `3 < 3` is false).

⚠️ **`3 < 3` was NOT what the code evaluated until 2026-09-05** — it evaluated
`3 < 2`. `OpenMap` narrowed the map with `ForArea` and then counted the
*slice's* `NextAreaO` markers, giving `TotalMapAreas` 2/3/2 on areas 1/2/3
where `basicRoomSetup.gml:7` computes 3 on all three (it runs *before* the
loop at `:9-25` destroys the other areas' entities). All three of
`cp_dirtbowl`'s advance decisions come out the same either way — 1<2, 2<3,
3<2 against 1<3, 2<3, 3<3 — so no behavioural test on the only shipped
multi-area map could see it, and the sentence above recorded a correct outcome
with a false reason. Found by reading the control port's `areas` field against
the reference, not by a failing test. Fixed in `OpenMap` (the count now comes
from the unnarrowed map) and gated by
`MultiAreaMapTests.TheServedAreaCountIsTheMapsNotTheSlices`, which pins the
slice counts too so the gate cannot go vacuous. **A custom four-area map has
no such coincidence to rely on.**

### The message-width audit against the GML — done, and clean

**Every server-to-client message the port writes was checked against the width
the stock client reads, message type by message type, on 2026-09-05.** It was
the leading hypothesis for the count dialog ("a message whose byte length the
port and a stock client disagree about") and it came back **clean**. Do not
redo it from scratch; this is the record.

The oracle is the pair `Scripts/Client/ClientBeginStep.gml`'s
`receiveCompleteMessage(socket, N, ...)` sizes and the `write_*` calls behind
them, against `src/Gg2.Protocol/ServerMessages.cs` and
`src/Gg2.Server/StateUpdateWriter.cs`. Checked and agreeing:

- **Every fixed-width message.** `HELLO`, `JOIN_UPDATE` (2), `PLAYER_JOIN`,
  `PLAYER_LEAVE` (1), `PLAYER_DEATH` (4), `BALANCE` (1, or 1+1 on a switch),
  `PLAYER_CHANGETEAM`/`CHANGECLASS`/`CLIENT_SETTINGS` (2), `PLAYER_SPAWN` (3),
  `CHAT_BUBBLE` (2), `BUILD_SENTRY` (6), `SENTRY_POSITION` (5),
  `DESTROY_SENTRY` (4), `WEAPON_FIRE` (9), the five one-byte intel/uber/zoom
  messages, `KICK` (1), `REWARD_CHALLENGE_CODE` (16), `MAP_END` (string + 2),
  `CHANGE_MAP` (two strings), `PLUGIN_PACKET` (ushort length then that many).
- **Every variable-length block**, which is where the probability was:
  the per-player stats + `unparseRewards` string + the domination row of
  `roster - 1` bytes (`Player.events/User Event 12`); the subobject byte and
  its three tiers of character block; `Minegun`'s `lobbed` count and its
  seven-byte mine records; `Medigun` overriding `User Event 12` with no
  inherited action, so a medic's weapon block is the heal target alone; the
  uncounted `MovingPlatform` and control-point runs; both intel counts.
- **Every gamemode HUD block.** CTF (1+4), Invasion (1+4+2), TDM (1+4+2),
  Generator (1+4 then blue-before-red), KotH and DKotH (6 + points), Arena
  (6 more on `FULL_UPDATE` only, then 8 + points), ControlPoints (1+4+2 +
  4 per point). `basicRoomSetup.gml:36-71` creates exactly one HUD, and the
  port's `GameModes.Of` walks the same if/else chain in the same order over the
  **narrowed** map, which is what the client sees.

⚠️ **`Gg2Text` is 1:1 Latin-1 and throws rather than substituting**, so a name
cannot desynchronise a length prefix from its payload; `WriteString1`/`2`
range-check the prefix. That closes the classic UTF-8-bytes-versus-char-count
slip by construction.

⚠️ **One reachable gap, and it is not on a shipped map.** `ControlPointHUD`'s
`User Event 12` walks `global.cp[i]` for `i = 1..instance_number(ControlPoint)`,
so a map whose control points are numbered **non-contiguously within one area**
(a `controlPoint2` with no `controlPoint1`) makes the reference write fewer
blocks than its own loop count while the port writes exactly
`ControlPointCount`. Every shipped map numbers contiguously. Worth knowing
before anybody authors one.

### Fixed: a joiner in the map-change countdown was told the wrong area

**`GameServerBeginStep.gml:74-92` moves `global.currentMapArea` when the
countdown OPENS, ten seconds before the map changes** — `+= 1` on an area
advance, `= 1` on a rotation — and only then writes the `MAP_END` that carries
it. The port left `_match.CurrentMapArea` on the area being played until
`OpenMap` ran at the end of the countdown, and `WriteJoinBurst`
(`GameServer.Joining.cs:192`) hands that value to every joiner as its
`JOIN_UPDATE` area. So a client that joined inside those ten seconds was told
the area the server was **leaving**.

⚠️ **`JOIN_UPDATE` and `MAP_END` are the only two places the wire ever states
the area**, and this joiner is by definition too late for the `MAP_END`. Its
`global.currentMapArea` is then frozen wrong for as long as it stays connected —
there is no later message that corrects it. Two things read it:

- `basicRoomSetup.gml:16-25` narrows the map to that area's entities, so the
  client plays different geometry from the server.
- `ClientBeginStep.gml:511-544` wipes every player's stats and team to
  `TEAM_SPECTATOR` on `global.currentMapArea == 1` — a rule the server applies
  to the area it is going **to** (`Rounds.ResetForNewMap(firstArea:)` and
  `GameServer.Maps.cs`'s roster wipe).

**Stale at 1 across an advance, the joiner wipes teams the server keeps**, then
reads a character for a slot it now holds as a spectator, and `Character`'s
Create kills it with `unknown team ID: 2`. That is the open fatal's symptom
exactly, and it is now reproduced in-process: with the fix reverted,
`MultiAreaMapTests.AJoinerInTheMapChangeCountdownIsToldTheAreaItIsAboutToLoad`
collects `InputState: slot 1 has a character, and this client has them as
Spectator` from `MirrorClient.Impossible`. The inverse — stale at 3 across a
rotation — keeps teams the server has cleared, which corrupts the scoreboard
rather than killing anyone.

Fixed by `GameServer.Maps.cs`'s `BeginMapChange`, which both doors now go
through: `ServeRoundEnd` (the one production reaches) and `RequestMapChange`
(the control port and the web admin). They were separate bodies, which is why
the fix needs two gates:

- `AJoinerInTheMapChangeCountdownIsToldTheAreaItIsAboutToLoad` — the control
  door, an advance, and the fatal direction.
- `ARoundEndingInTheCountdownTellsAJoinerTheAreaItRotatesTo` — the round-end
  door, a rotation off area 3, and the wipe in the other direction.

Both mutation-checked, one door at a time: dropping the assignment from
`BeginMapChange` fails exactly the first, and restoring the old area after the
round-end call fails exactly the second. `TheServedAreaCountIsTheMapsNotTheSlices`
was tightened in the same change — its wait was `CurrentMapArea == area`, which
now comes true on the tick the change is *requested*, so it waits for `NextMap`
to empty as well.

⛔ **This does NOT close the count dialog, and it is not a framing bug.**
Measured: `GameModes.ShapeOf(cp_dirtbowl.ForArea(a))` is `ControlPoints`,
2 control points, 0 platforms, 0 generators, setup gate present — **identical
for a = 1, 2 and 3**. A joiner narrowed to the wrong area therefore frames every
state update at exactly the right width, so this bug cannot produce "Wrong
number of players while deserializing state". It is a **second, independent
producer of the same fatal**, reached without any misalignment at all. The
consequence for the note below: `unknown team ID: 2` in live evidence is no
longer evidence of a count mismatch, and the two have to be attributed
separately. **A custom multi-area map whose areas differ in control-point count
would have had both at once**, which is what makes this easy to have mis-read.

⛔ **Still open — an intermittent client fatal, seen once and not reproduced.**
One six-bot run driven 1→2→3 killed the client one second after reaching area
3 with the same symptom as the fixed `MatchState` bug (`unknown team ID: 2`,
preceded by an unreadable `show_message`). A second identical run was clean, as
were four other multi-area runs. The server binary was identical across both
runs, so this is not a regression and not the `MAP_END` fix failing — it is
intermittent, meaning timing- or state-dependent, and **not confirmed to share
a cause** with the fixed bug (only the symptom, which is generic to any framing
slip a client reads a subobject bit against). To chase it: run `cp_dirtbowl`
with six-plus bots, drive 1→2→3 repeatedly with `--record` on, until it fires.

⚠️ **2026-09-05 — the CAUSE is now known, and the sprite error is only the
symptom.** The launcher saved a screenshot of the "unreadable `show_message`"
that preceded each death. It reads:

```
Wrong number of players while deserializing state
```

That is `deserializeState.gml:11-13`: the client compares the state update's
declared player count against its own `global.players` list and, on a mismatch,
**shows a message and carries on deserialising anyway**. From that point the
read is misaligned, and a character bit eventually lands on a slot the client
holds as a spectator — `Character`'s Create event then calls
`getCharacterSpriteId`, which accepts team 0 and 1 only, and kills the client
with `unknown team ID: 2`. So the chain is: roster-count disagreement →
misframed read → character for a spectator → fatal.

⚠️ **2026-09-05, later — that last sentence is one chain, not the only one.**
The join-window area bug above reaches `character for a spectator → fatal`
with **no** count disagreement and **no** misframed read, and it is reproducible
where this is not. So a live `unknown team ID: 2` does not by itself imply the
dialog fired: the dialog is evidence of a count mismatch, the sprite error is
not. What is still unattributed is the **dialog** — the screenshots are real,
and nothing found since moves a roster count.

⚠️ **It fired TWICE, not once** — `agent_launcher_17800.log` (105 s after
launch) and `17801.log` (**6 s** after launch, a different server). A
six-second death is at or near JOIN, not after an area advance, so the
"driven 1→2→3" framing above is too narrow: at least one of the two was a
join-time desync.

⚠️ **A join-window bug WAS found and fixed on 2026-09-05, and it is not this
one** — see "Fixed: a joiner was sent the tick it had already been given"
above. It put a duplicate `PLAYER_DEATH` in front of a joiner and accounts
exactly for the four live `doEventPlayerDeath.gml:41` errors, but it cannot
move a roster count, so it does not produce this dialog. Do not read that fix
as closing this. What would: a `PLAYER_JOIN` or `PLAYER_LEAVE` landing between
the flush (`GameServer.cs:802`) and the join burst (`:822`) — and 2026-09-05
established that **nothing can**, not even a `kick` (see "Nothing moves a roster
count inside the join window" above). So the writer that moved the count has
still not been identified, and the join window is no longer where to look for
it: the live candidate now is a message whose byte length the port and a stock
client disagree about, which misaligns the stream and makes
`deserializeState.gml:12` read a garbage byte as the count.

⛔ **`MirrorClient` could not see any of this, by construction.**
`StateUpdateReader` takes the player count off the wire and reads exactly that
many blocks; unlike the stock client it never compares that count against the
roster it holds. **Fixed 2026-09-05**: the mirror now makes the same comparison
`deserializeState.gml` does and records a mismatch in `Impossible`, so every
test that uses it checks this for free. The whole suite stays green with the
check armed — and it is mutation-checked: writing `Roster.Count - 1` in
`StateUpdateWriter` turns five transition tests red.

`GamemodeRotationTests` generalises the gamemode-change gate: it rotates a
client through one shipped map of every gamemode (seven of eight have one),
waiting for a `CAPS_UPDATE` after each so the HUD block is actually written,
and checks the roster still adds up (mutation-checked: a single-capture writer
fails so hard the client never sees the next `CHANGE_MAP` at all).

## Which flush a message rides

**The bug class this section exists for: a value or message on the wrong side
of the reference's serialise/flush boundary.** Six instances have been found
this way, and the tier-1/tier-2 harnesses above are **structurally blind** to
every one of them.

### Why a recording or a live diff cannot see it

**The send buffer is FIFO.** Moving the flush point changes which TCP segment a
message rides in and never the byte order — both orderings produce a
bit-identical stream, so there is no discrepancy in a `.trace` to find. And two
live sessions cannot be diffed either: this port's bot layer has diverged from
any reference bot layer and owes it nothing, so the two sides never produce
comparable event sequences. The comparison has to be a **structural
fingerprint computed independently on each side**, not a trace diff.

### Three instruments, cheapest first

**1. The stage table below, plus a gate on the port's routing.** Exhaustive,
static, and the thing that goes red on a regression. Build this first.

**2. A framed recording, for a lead/trail fingerprint.** `FlushFrames` and
`FlushFingerprint` (`src/Gg2.Parity/FlushFrames.cs`); `gg2-parity record`
writes a `.frames` sidecar alongside the `.trace` (kept as a sidecar rather
than changed trace format, so every existing recording stays valid).
`FlushFingerprintTests` is the gate; `FlushFingerprintTests.ATickIsExactlyOneFlush`
establishes that the grouping means what it says. Groups messages by flush and
records, per group, which ones precede the state update and which follow it.
Grouping by arrival time on the reference side is a heuristic (TCP coalescing;
the reference is not known to set `TCP_NODELAY`) — calibrate against
`gg2_watch buffer_size(global.sendBuffer)` sampled at the bridge's Step, which
gives the leftover prefix length directly. The port side needs none of this:
`--record` tees inside `FlushBroadcast`, so its groups are exact by
construction.

**2b. Reference-graded evidence.** 40 seconds of six bots on a live reference
server, recorded framed, checked in as
`reference-ctf_truefort-framed.trace` (+`.frames`);
`ReferenceFlushFingerprintTests` is the gate. 1,188 reads against 1,196 state
updates (0.7% coalescing) — the fingerprint is byte-identical from a 2ms
grouping gap to a 28ms one, and it independently confirms the stage table
below directly from the reference's own wire: every `PLAYER_DEATH` leads its
packet, every `WEAPON_FIRE` trails, `PLAYER_SPAWN` and `GRAB_INTEL` trail,
`PLAYER_CHANGECLASS`/`CHANGETEAM` lead.

**3. A lag sweep, for the half the other two cannot see.**
`Replayer.InputLag` and `InputLagSweepTests`. A **content**-phase bug — keys
reaching clients a tick late — is invisible to instruments 1 and 2, because
the type-level and stage-level facts are unaffected; only re-simulating
open-loop with the recorded keys shifted by L ∈ {−2…+2} and taking the argmin
of prediction error catches it. A correct stream has argmin L=0; a one-tick
phase error produces a **20-70× change** in p99 open-loop drift (measured:
`closedcombat` 4→175 wire units at L=0 vs L=+1; `gg_debug-closedtdm` 4→287).
Pointed at the port's own `--record` output
(`ThisServersOwnRecordingIsInPhase`), this is the only instrument of the three
that grades what the server **publishes** rather than the reference's
physics — a mutation reintroducing a keys-a-tick-late bug flips the argmin from
0 to +1 exactly as predicted.

⚠️ **The table below cannot settle everything even with a correct fingerprint.**
Three message types appear **twice**, in different stages, and the type alone
does not say which flush an instance rides: `PLAYER_DEATH`, `RETURN_INTEL` and
`DESTROY_SENTRY` each have one pre-flush site and one post-flush site
(`TickEvents.ReturnedRedIntelInEndStep`, `TickEvents.SentryDestroyedInEndStep`,
gated by `WireStageTests`). A type-level fingerprint can only catch a type that
should *never* lead and did; only the port's own stage flags can grade a
specific instance.

| bug | table + routing gate | fingerprint | lag sweep |
|---|---|---|---|
| every event a tick late | ✅ | ✅ | ✗ |
| deaths a tick early | ✅ | ✗ (type appears both sides) | ✗ |
| `RETURN_INTEL` on a pit death | ✅ | ✗ | ✗ |
| `DESTROY_SENTRY` from Begin Step | ✅ | ✗ | ✗ |
| keys a tick late | ✗ | ✗ | ✅ |

### The coverage trap

A fingerprint over one CTF bot session will never see generator destruction,
arena transitions, sentry destruction, map change or team balance — so the
analysis must report **unseen message types as a first-class result** (an
empty row is otherwise indistinguishable from agreement), and the session
needs a gamemode rotation, not one long match. Practical limits: the recorder
joins as a spectator and self-disconnects at exactly five minutes, so it needs
reconnection or a non-spectator recorder.

### The stage table

**Stage decides everything**, because `GameServerEndStep` is the FIRST End
Step of the frame (`docs/TRAPS-SIMULATION.md`, "The GameServer is the OLDEST
instance"). Anything appended at or after another object's End Step misses
that frame's flush.

| stage | rides |
|---|---|
| `GameServerBeginStep` before `serializeState` | this tick, **ahead of** the update |
| any Begin Step, alarm, Step, or collision event | this tick, **after** the update |
| any non-`GameServer` End Step | **the next tick**, ahead of that update |
| `acceptJoiningPlayer` / `serviceJoiningPlayer` | the next tick (run after the flush) |

| message | reference append site | stage |
|---|---|---|
| `PLAYER_LEAVE` | `GameServerBeginStep.gml:41` | pre-update |
| `PLAYER_JOIN`, `CHANGETEAM`, `CHANGECLASS`, `CHANGENAME`, `CHATBUBBLE`, `OMNOMNOMNOM`, `TOGGLE_ZOOM`, `DESTROY_SENTRY` (command), `BUILD_SENTRY` | `processClientCommands.gml` | pre-update |
| `PLAYER_DEATH` (class-change suicide) | `processClientCommands.gml:88` | pre-update |
| `DROP_INTEL` (command) | `processClientCommands.gml` | pre-update |
| `CAPS_UPDATE` | `GameServerBeginStep.gml:57` | pre-update |
| `INPUTSTATE` / `QUICK_UPDATE` | `GameServerBeginStep.gml:64-66` | the update |
| `MAP_END` | `GameServerBeginStep.gml:88-92` | post-update |
| `CHANGE_MAP` | `GameServerBeginStep.gml:104` | post-update |
| `GENERATOR_DESTROY` | `Generator.events/Begin Step.xml` | post-update |
| `DESTROY_SENTRY` (owner left the class/team, or overlap) | `Sentry.events/Begin Step.xml:1-3` | post-update |
| `SENTRY` position | `Sentry.events/Begin Step.xml` | post-update |
| `WEAPON_FIRE` | `Weapons/*.events/User Event 1.xml`, from Character Begin Step | post-update |
| `DROP_INTEL` (ubered carrier) | `Character.events/Begin Step.xml` | post-update |
| `UBER`, `UBERREADY` | `Medigun.events/User Event 1,2`, `Collision with HealingCabinet` | post-update |
| `PLAYER_SPAWN` | `PlayerSpawn.gml`, from `Player.alarm[5]` | post-update (alarm) |
| `RETURN_INTEL` (recovery timer) | `Intelligence.events/Alarm 0.xml` | post-update (alarm) |
| `UNLOCKCP` | `ArenaHUD`/`KothHUD`/`DKothHUD` `Step.xml` | post-update |
| `ARENA_*` | `ArenaHUD.events/Step.xml`, `serverArena*.gml` | post-update |
| `GRAB_INTEL` | `Character.events/Collision with Intelligence{Red,Blue}.xml` | post-update |
| `SCORE_INTEL` | `Character.events/Collision with IntelligenceBase{Red,Blue}.xml` | post-update |
| **`PLAYER_DEATH`** (damage) | `Character.events/End Step.xml:34-47` | ⛔ **NEXT TICK** |
| **`RETURN_INTEL`** (carrier killed by a kill box, frag box or pitfall) | `Character.events/Destroy.xml:20-32`, reached from `doEventPlayerDeath` | ⛔ **NEXT TICK** |
| **`BALANCE`** | `with(GameServer) ServerBalanceTeams()`, `Character.events/End Step.xml:42-44` | ⛔ **NEXT TICK** |
| **`DESTROY_SENTRY`** (`hp <= 0`) | `Sentry.events/End Step.xml` | ⛔ **NEXT TICK** |
| `PLAYER_JOIN` + join burst | `serviceJoiningPlayer.gml`, from `GameServerEndStep.gml:18-20` | next tick |

## Tier 4 — behaviour scenarios

`gg2-agent/tools/bot-scenarios.js` is an existing suite of "can a bot still
walk from A to B on map M" cases, with tick counts. Once the port runs bots,
the same scenarios should pass with comparable tick counts. This is the
regression net for the bot work that motivated the project, and porting the
runner is worth doing early rather than reinventing it.

## Order of work

The tiers are also the order. Tier 1 gates tier 2 — a trace replay against a
wrong `placeFree` produces a wall of mismatches that says nothing about the
movement code. Get `placeFree` bit-exact first, then a walking-only trace, then
jumps, then everything else.

## Gate liveness: a passing comparison must be proven to run

**Every comparison family needs two mutations checked against it, not one: shift
it a tick, and delete it (route its mismatches to a discarded list).** A family
that survives either mutation is not actually gated. This was applied to all
five comparison families in one pass and found real holes:

| Family | shift, before | delete, before | after |
|---|---|---|---|
| Deaths | 3 fail | 1 fail | gated |
| Intel | 2 fail | **0 fail** | gated |
| Sentries | 2 fail | 1 fail | gated |
| Control points | 3 fail | **0 fail** | gated |
| Ubers | **0 fail** | **0 fail** | gated |

`Fields.Ubers` had existed since phase 5 and was never passed to a `Replayer`
by any test — `SupportClassFixtureTests.ThePortNeverChargesAnUberEarlierThanTheReference`
looks like its gate and is not, because it replays with `Fields.None` and
watches a decorator instead.

⚠️ **`Assert(mismatches.Length == 0)` is satisfied equally by a comparison that
never runs.** Every gate in this section now carries a **liveness control**
beside it — a second replay against a doctored trace (messages stripped, or
blocks XORed) asserting the comparison *does* report a difference. A liveness
control proves the comparison runs and can report; it does not prove the
fixture *reaches* the behaviour — those are different holes (the second one is
the `KothFixtureTests` case above, whose observations all fell outside its
window).

**Ammo and flags were the same hole, found the same way.** A census of which
`Fields` members any test actually passes to a `Replayer` found `Fields.Ammo`
and `Fields.Flags` used nowhere, despite every phase serialising both. Both are
now gated with a liveness control: `closedcombat` (1,325 ticks, zero
mismatches — every weapon broadcasts a seed, no excuse available) for ammo,
`closedspy` (1,829 ticks — the only recording with a cloak) for flags.
`closedheavy`'s Minigun demonstrates the unseeded-weapon rule applies to ammo
too (ammo window 760 ticks vs. flags 1,744 on the same recording, same
anchor) — asserted as a control, since the two windows converging would mean
either the Minigun became reproducible (impossible) or the comparison broke.

⚠️ **General rule, paid for three times: census the seams.** "Which of these
does any test actually use?" found `Ubers`, `Ammo` and `Flags` unexercised for
phases, and a fourth pass (`MessageCoverageTests`) found the inverse failure —
messages a fixture reached that the harness discarded. A seam that is never
exercised is indistinguishable from one that is wrong; check both directions
whenever a new comparison or injection is added.

## Uber charging: the longest correct-behaviour chain a fixture can ask for

Filling an ubercharge takes 800 consecutive ticks of the beam finding the
right patient, holding line of sight, and charging at the right one of two
rates (2.5/tick off a hurt patient, 1.75/tick off a healthy one) — nothing
else in the corpus asks for an unbroken chain that long.
`SupportClassFixtureTests.ThePortChargesAnUberInTheClosedFixtureAndNeverEarly`
is asserted **one-sided**: the port must charge, exactly once, and never
early. It cannot assert exact timing, because how hurt the patient is depends
on unimplemented/unseeded damage sources — a lag in charging is not
necessarily a beam bug (measured: the observed 412-tick lag on
`closedsupport` sits exactly between the all-hurt and all-healthy rate
predictions, consistent with a damage mixture rather than a wrong rate).
⚠️ **A bound that does not move when a feature lands is not thereby evidence the
feature failed** — check which side of the ledger the bound is on (a
window capped by missing *damage* only widens when damage is added; it is not
evidence the new weapon does nothing).

## Airblast: measured and implemented, structurally ungateable by a recording

The airblast and the `Flare` are implemented; the physics rules are in
`docs/SIMULATION.md`. What belongs here is the evidence shape: every fixture
window is byte-identical before and after implementing it, which is not
evidence of absence of bugs — the Flamethrower is unseeded, so a pyro's blast
in any recording can never be re-derived closed-loop. The gate is therefore
unit tests only (`AirBlastTests`, `AirBlastBoxTests`, pinning every value
measured live against the engine, including a corrected two-pixel sentry-box
inflation rule and `collision_circle`'s floored-not-rounded fractional
centre).

## What Tier 3 driving has demonstrated live

A stock GM8 client has played complete rounds against the port on `ctf_truefort`
(spawn at real markers, correct draw/win logic, survives a map change,
re-picks a team) and a full King of the Hill loop end to end (join, unlock,
capture, hold to timeout, win, map-change-in-ten-seconds, next round starts
locked and neutral) — including a live class change mid-match. `--time-limit`,
`--cap-limit` and `--respawn-time` exist on `gg2-server` for exactly this;
`--time-limit 0` is floored to one minute rather than meaning "no limit"
(`WorldState`'s own floor, matching `game_init.gml:96,101` — gated by
`MapRotationTests.AZeroTimeLimitIsFlooredToOneMinuteRatherThanMeaningNoLimit`).

Driving a client through a menu requires destroying `TeamSelectController`/
`ClassSelectController` after injecting a team pick — `PlayerControl.events/Begin
Step.xml` gates every movement key behind `!menuOpen`, and picking a team over
the socket alone leaves those controllers (and `menuOpen`) standing.
`gg2_watch` is the only per-frame hook available (a tool round trip is 30-100
frames), and its expression can call `execute_string` to run statements — the
mechanism behind driving a character onto terrain that needs a jump or stop
inside a narrow window. GG2 jumps on the pressed edge, so a held jump fires
once; a movement mask must be written absolutely, not OR'd, or an earlier
direction bit sticks. A client returning to spectator on a map change is the
**server's** own behaviour (every roster entry is set to `Team.Spectator` on
`OpenMap`), not the AFK timer.
