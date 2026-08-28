# Proving the port matches

Physics parity is the risk that sinks this project, and it is not the kind of
bug playtesting finds — a 0.3 px/tick divergence looks like "the netcode feels a
bit rubbery", which GG2 already does. **Build the harness before the physics.**

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
whole specification, and it is checkable in CI.

## Tier 0 — the recorder

**Built** — `Gg2.Parity`, the `gg2-parity` console app. `record` joins a server
as a passive spectator and writes its broadcast stream to a file; `diff`
compares two recordings and reports the first differing byte with a window
either side.

It earned its place before the physics did. Pointed at the reference GM8 server
and at this port in turn, on `ctf_truefort` with one spectator, the two
recordings came back **569 bytes each and byte-identical except the CTFHUD round
timer** — which turned "the client does not like my stream" from a guess into a
four-byte value difference in about ten minutes. That comparison is worth
re-running whenever the serialisation changes; it is much cheaper than a client.

A ~200-line console app. Speak the handshake from `PROTOCOL.md`, `RESERVE_SLOT`,
`PLAYER_JOIN`, then never send an `INPUTSTATE` of your own; just parse the
inbound stream into a list of framed messages and write it to disk with a tick
index.

**Framing is built too** — `Gg2.Protocol.ServerStream` and
`StateUpdateReader`. `record` now frames what it captured and prints a message
census before it exits, because a trace that cannot be framed is not a trace and
finding that out while the server is still up is much cheaper than finding it
out in CI.

The stream is **not self-delimiting**, and this is the part worth knowing before
writing a reader: a state update's width comes from three places at once.

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
`FULL_UPDATE` is class-dependent.

⚠️ **This document used to say that block was zero bytes for eight classes out
of ten, and it is not. It is never zero.** The reasoning was that only `Medigun`
and `Minegun` have a `User Event 12` in `Objects/Weapons/` — true, and
irrelevant, because **GM8 runs a parent's event when the child does not define
one**. Every weapon has `<parent>Weapon</parent>`, and
`Objects/InGameElements/Weapon.events/User Event 12.xml` writes `readyToShoot`
and `alarm[0]`. The client agrees from the other side: `Weapon`'s
`User Event 13` is a two-byte `receiveCompleteMessage`.

Measured on the running engine on 2026-08-25 — swap `global.serializeBuffer` for
a fresh one, call `with(currentWeapon) event_user(12)`, and read `buffer_size`
either side:

| class | weapon | bytes |
|---|---|---|
| Medic | `Medigun` | **1** — heal target only |
| Demoman | `Minegun` | **3 + 7n** — the parent's two, `lobbed`, then the mines |
| every other class | | **2** |

`Medigun` and `Minegun` differ by one thing that is invisible in the GML text:
`Minegun`'s `User Event 12` opens with an `action_inherited` in its XML and
`Medigun`'s does not. Reading the code alone cannot tell you that; reading the
`.xml` — or measuring — can.

**The reason this survived four phases is worth more than the fix.** Every
recording in the repo was made by `gg2-parity record`, which joins immediately,
and a `FULL_UPDATE` is sent *on join* — so every fixture was captured before any
bot had spawned, and not one of them contained a character inside a full update.
The path was not undertested; it was unreachable by the fixtures that existed. A
recording made after the bots spawned fails to frame on its very first
`FULL_UPDATE`.

`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-combat.trace` closes the hole:
six bots already spawned, classes forced so a medic, a demoman and four
ordinary weapons all appear in one update, plus eight `WEAPON_FIRE`s and a
`PLAYER_LEAVE` roster compaction. `gg2-parity census <trace>` prints what a
recording contains and where framing stopped, which is how this was found.

**When adding a fixture, ask what state it cannot reach.** That is the question
that would have caught this one.

A recording of the real GM8 server on `ctf_truefort` is checked in as
`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-spectator.bin`, and the test
that frames it to the last byte is the one that would notice any of the above
being wrong.

### The trace format

**Built** — `Gg2.Protocol.Trace` and `TraceParser`.

**The trace is the raw stream**, with a header in front naming the map, the
reference version and where it came from. Not a parsed structure. Three reasons,
and the third is the one that matters:

1. It is lossless, so a change to the parser never invalidates a recording.
2. It is bounded and cheap: a 30 Hz broadcast to a spectator watching two bots
   is about 440 bytes a second.
3. It means the parity recorder and a live server writing training data are the
   **same path** (`docs/BOTAI.md`, item 3). A second logging format invented
   later is exactly how the two drift apart.

`TraceParser` turns it into ticks. A tick is delimited by its state update,
which is always the *last* thing the server writes for that tick —
`GameServerBeginStep.gml` queues command responses first and appends the update
afterwards, and the whole buffer goes out in End Step. `FULL_UPDATE` and
`CAPS_UPDATE` are not tick boundaries: the first is sent on the join path
outside the cadence, the second goes out *in addition* to that tick's update.

### What a wedged client looks like

Worth knowing before debugging one. `receiveCompleteMessage` is a **blocking
spin**:

```gml
do {
    if(socket_has_error(argument0)) return 2;
} until(tcp_receive(argument0, argument1));
```

There is no `io_handle()` in it. A client waiting for bytes that will not come
therefore pegs one core, stops running Draw events — so the window goes black,
not frozen-looking — and stops servicing the agent bridge, so `gg2_evalx` hangs
with no error. All three symptoms have the same cause, and none of them says
*where* the stream went wrong. That is what the recorder is for.

Note also that an unknown message byte does **not** wedge the client: it hits
`default: promptRestartOrQuit("The Server sent unexpected data.")` at the bottom
of `ClientBeginStep.gml` and puts up a dialog. A silent black screen means a
*short* message, not an unrecognised one.

A wedge is also what a client looks like **after** it has disconnected itself and
had its modal force-closed — which is what a spectator does at exactly five
minutes, of its own accord (`docs/PLAN.md`, phase 1). Check the launcher log for
a dismissed dialog before concluding the stream is at fault; `gg2-agent`'s
launcher saves a screenshot of every dialog it clicks, and that picture is
usually the whole answer.

Two things to get right:

- **Start the trace at a `FULL_UPDATE`.** It is the only message that carries
  complete initial state; a trace that begins mid-match has no defined starting
  point. The `JOIN_UPDATE`/`FULL_UPDATE` pair sent on join is the natural
  anchor.
- **Record the roster events too** (`PLAYER_JOIN`, `PLAYER_LEAVE`,
  `PLAYER_CHANGECLASS`, `PLAYER_CHANGETEAM`, `PLAYER_SPAWN`, `PLAYER_DEATH`).
  Replay needs them to keep indices and class stats aligned.

Build traces from a range of situations, and keep them in the repo as test
fixtures once they are small enough: flat ground running, a jump arc, a
double-jump, a wall-run into a step-up, a fall onto a corner, a rocket jump, a
drop-through platform, a gate transition.

**The cheapest source of a populated trace is the reference's own bots.** Set
`[Bots] Enabled=1` in the build directory's `gg2.ini`, run the dedicated server,
and record — the bots walk, jump, shoot and die on their own, which is thirty
seconds of real GM8 physics for no input driving at all.
`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-bots.trace` is one such
recording: 13 KB, 898 ticks, two bots on `ctf_truefort`. Put the `gg2.ini` back
afterwards.

That covers walking and jumping but not the deliberate cases, which need
scripted input rather than a bot's own judgement.

## Tier 1 — primitives, against the live engine

Before any trajectory can match, `placeFree` must match. This is where the two
genuinely uncertain things in `SIMULATION.md` get settled.

`gg2-agent` can answer these directly from the running game: `gg2_eval` a
`place_free(x, y)` at a few thousand sampled points on a map and compare against
the C# implementation reading the same walkmask. A disagreement anywhere is a
boundary-rule bug, and it is far cheaper to find here than three layers up.

Sample deliberately at the nasty places, not uniformly: exactly on `x % 6 == 0`,
at negative coordinates, at the map edges, and at the character rectangle's own
corners.

## Tier 2 — trace replay, the main event

**Built** — `Gg2.Parity.Replayer` and `DriftReport`, driven by
`gg2-parity replay <trace>`.

The simulation it talks to is `ITraceSimulation`, which is deliberately tiny and
deliberately lives in the harness rather than in the simulation: phase 4 writes
the physics against `Tick(WorldState, ReadOnlySpan<PlayerInput>)` and an adapter
bridges the two, so the shape of `WorldState` is not prejudged by an interface
written before it exists. Everything crosses in **wire units**, because the
comparison is about the integers a client can observe.

`EmptySimulation` is the stand-in until then. It accepts characters and never
moves them, so replaying the bot fixture against it reports the movement it
failed to reproduce:

```
reference-ctf_truefort-bots.trace  [ClosedLoop]
  37 ticks, 2 comparison(s), 2 resync(s) on spawn
  8 mismatch(es), first at tick 36:
    tick 36 slot 2 x: expected 18395, got 18510 (+115)
    tick 36 slot 2 hspeed: expected -43, got 0 (+43)
    ...
  |dx| max 115, p99 115   |dy| max 90, p99 90   (wire units; 5 = one world pixel)
```

Replay the trace and diff. Two modes, and you want both:

**Closed-loop (what the server must do).** Feed inputs, never correct. Divergence
accumulates. This is the honest measure of "is the physics the same", and the
target is *exact equality of the quantised wire fields* for the whole trace. It
is achievable: both sides are doing double-precision arithmetic in the same
order, so drift means a real difference in the maths, not floating-point noise.

**Open-loop segments (what the client actually experiences).** Reset the port's
state from each `QUICK_UPDATE`, simulate forward to the next one, then compare.
This measures exactly the thing a player sees: the prediction error at the moment
of correction. Report it as a distribution, not a pass/fail — max and
99th-percentile `|Δx|`, `|Δy|` in wire units (1/5 px).

That gap is **7 ticks, not 6**. `QUICK_UPDATE` goes out when `frame mod 7 == 0`,
so consecutive ones are seven ticks apart and six `INPUTSTATE`s sit between
them — the ratio in a real recording is 6 : 1, and the tick indices in a drift
report step 36, 43, 50. An earlier draft of this document said six; a replayer
that believes it lands one tick short of the thing it is comparing against, and
reports a drift that is really just a missing tick of gravity.

Sensible gates for CI:

| Metric | Gate |
|---|---|
| Closed-loop wire-field mismatch, per fixture | 0 |
| Open-loop 6-tick max error | 0 wire units |
| Any `hp` / `ammo` / flags mismatch | 0 |
| Recorded `WEAPON_FIRE` seeds left unconsumed | 0 |
| `PLAYER_DEATH` mismatch — tick, victim, killer, assistant or cause | 0 |

Start with the gates loose enough to be green on a walking-only trace, then
tighten to zero as each behaviour lands. A gate that has never been green tells
you nothing.

### What a state update cannot see

**A gate built only on state updates can be green for a program that does
nothing.** The updates are the whole of what tier 2 compares, and there are
things a tick does that never appear in one:

- **A weapon firing.** Only the emptied magazine reaches the update, and a shot
  that missed leaves no trace in it at all. `Fields`' answer is the seed: the
  replayer hands the simulation each recorded `WEAPON_FIRE`'s seed and counts
  the ones no shot consumed, so firing on the wrong tick is a number rather
  than an inference (`PortedSimulation.SeedsUnused`).
- **A character dying.** A dead character is *absent* from the update, updates
  arrive every seventh tick, and nothing in one names a killer or a cause. So
  the absence says only "died at some point in the last seven ticks", and the
  replayer's own reconciliation then clears the slot to match — quietly, and
  reporting nothing. Before `Fields.Deaths` existed, a port that killed nobody
  at all replayed `reference-ctf_truefort-bots.trace` with **zero mismatches**;
  the two slots were cleared by the harness four ticks after the reference had
  killed them.

- **A medic charging an ubercharge.** The charge *is* in the update — a medic's
  class-specific byte is `uberCharge*255/2000` — and that is exactly why it is
  not enough: the byte saturates at 255 and then sits there for as long as the
  charge is banked, so a port charging at twice the right rate looks identical
  from the moment it tops out. `UBER_CHARGED` names the tick. `Fields.Ubers`,
  `ITraceSimulation.UbersLastTick`.

The pattern in all three cases: the recording contains a message the state does
not imply, so compare the message. A recorded `PLAYER_DEATH` is checked against the
deaths the tick produced (`ITraceSimulation.DeathsLastTick`) in **both
directions** — a death the recording has and the port does not, and a death the
port invented — keyed on the victim, because a character that dies is destroyed
and so no slot can appear twice in a tick.

⚠️ **Neither is ever fed *in*.** A recorded death is a comparison, not an
instruction; applying it would make the gate vacuous, exactly as supplying "you
fired this tick" instead of only the seed would. The question these exist to
answer is whether the port reaches `hp <= 0` on the same tick by itself.

⚠️ **A message produced during tick *n* is recorded under tick *n+1*.** The
`GameServer` instance is older than any `Character`, so GM8 runs its Begin Step
first and the state update is already in the buffer before a character fires
(Begin Step) or dies (End Step). `TraceParser` closes a tick at each state
update, so both land in the following tick's `Events`. `SupplyFireSeeds`,
`ReconcileDeaths` and `ReconcileUbers` all read one tick ahead for this reason.

### Where replay legitimately diverges

Do not chase these — exclude them and say so in the report:

- **Server-side RNG**: team selection, chat bubbles, respawn timing, and *which*
  spawn point a character gets. `SIMULATION.md` establishes the port is free
  here. Replay should take roster/team/spawn events from the trace rather than
  deciding them.
- ⚠️ **A spawn *position* is not on that list, and this document used to say it
  was.** The advice here was "resynchronise the character's state on every
  `PLAYER_SPAWN`", and following it is what made phase 4 look like it had a
  one-wire-unit arithmetic bug for as long as it did. A resync anchors on a
  broadcast position, which is quantised to a fifth of a pixel and only sent
  every seventh tick (`GameServerBeginStep.gml:96`) — so it starts the character
  somewhere inside a bin, several ticks after it actually spawned.

  `PLAYER_SPAWN` carries a **spawn point id and a group**, not coordinates
  (`sendEventSpawn.gml`), and the client resolves the pair against the map
  itself (`doEventSpawn.gml`). Do the same: `Gg2.Maps.SpawnPoints` reproduces
  `offloadSpawnPoints.gml`'s lists, and the result is an integer coordinate with
  both speeds zero — an anchor that passed through no wire at all. Only the
  choice is RNG, and the choice is on the wire.
- **Anything after a divergence.** Report the first mismatching tick and stop;
  100 downstream mismatches are one bug.

### How to record a fixture that is actually about combat

The first two combat fixtures were both disappointing, in the same way and for
the same reason: **a recording that starts when the map does is a recording of
bots walking.** `reference-ctf_truefort-combat.trace` is 748 ticks and its first
`WEAPON_FIRE` of any kind lands on **tick 647** — 86% of it is character physics,
and a session spent implementing a weapon to fix its residual moved the mismatch
count by two.

`reference-ctf_truefort-hotcombat.trace` was recorded to fix that. What made the
difference, in order of how much it mattered:

1. **Start recording thirty seconds late.** The bots need that long on
   `ctf_truefort` to leave their bases and meet. Connect the recorder once
   `gg2_state` shows people below full hp.
2. ⚠️ **Use `[Classlimits]` to keep the bots to classes the port implements.**
   Setting `Pyro`, `Heavy`, `Medic`, `Engineer`, `Spy`, `Sniper` and `Quote` to
   `0` in `gg2.ini` leaves only Scout, Soldier and Demoman, so every shot in the
   recording is one the port can take. This is the single trick that turns a
   recording into a fixture — without it half the fire is from weapons that do
   not exist here and the residual is unreadable.
3. **`RemoveOnDeath=0`**, so the roster stays still and the trace is about combat
   rather than about joins and leaves.
4. **Record long.** Fifty seconds gives ~1,500 ticks and ~1,000 comparisons.

The result, against the two before it:

| | ticks | fires | first fire | deaths | spawns |
|---|---|---|---|---|---|
| `bots` | 898 | 6 | tick 388 | 2 | — |
| `combat` | 748 | 8 | **tick 647** | 0 | 0 |
| `hotcombat` | 1495 | **21** | **tick 12** | 3 | 3 |

It is open-loop only, like any recording whose players were alive before it
started — see below. What it buys is that **disabling a weapon now fails a
test**: turning off the Rocketlauncher takes its `|dy|` bound from 75 to 240.
Neither older fixture noticed.

⚠️ **Restore `gg2.ini` last**, after the game is stopped. The dedicated server
re-reads it, so putting the bots setting back mid-session empties the roster and
whatever is still recording captures nothing.

### How to record a fixture for a class the bots will not reliably pick

`[Classlimits]` narrows what a bot *may* pick; it does not decide what it
*does*. `botPickClass.gml` draws `irandom(8)` and rejects only the Spy, so with
three classes allowed and six bots the roster is a multinomial draw — the first
attempt at a medic/pyro/engineer fixture produced **no engineer at all**, which
is a one-in-eleven outcome and would have been invisible until the fixture was
already checked in.

**Force the roster over the bridge instead.** `global.botsEnabled` gates nothing
but `botPopulationUpdate`, so setting it to zero freezes the roster without
stopping the bots playing:

```
gg2_eval: global.botsEnabled = 0;
          with(Player) if(isBot) botRemove(id);
gg2_eval: global.botNameCounter += 1;
          botAdd(TEAM_RED, CLASS_MEDIC, global.botNamePrefix + string(global.botNameCounter));
          ...  // once per bot, with the class and team you actually want
```

`botAdd(team, class, name)` runs `checkClasslimits` itself, so the ini still has
to allow the class — but the *mix* is now yours. Six calls give a deterministic
2/2/2 roster, and the fixture is reproducible rather than lucky.

⚠️ **The reference respawns on a timer, so a map reload does not produce one
spawn burst.** `reference-ctf_truefort-closedcombat.trace` happens to spawn its
roster inside 115 ticks and the test that uses it derives the wave as "every
spawn within 200 ticks of the first"; `reference-ctf_truefort-closedsupport.trace`
spreads the same six spawns over **493** ticks and that heuristic gets the wrong
tick. The robust definition is **the tick by which every slot that ever holds a
character has had a `PLAYER_SPAWN`** —
`SupportClassFixtureTests.AnchorTick`. It also refuses outright when some slot
never spawns, which is a property worth asserting: one wire-anchored character
makes the whole recording open-loop.

⚠️ And `closedcombat` does **not** have that property — its slot 6 never
spawns. It gets away with it only because that slot holds no character during
the window the test asserts over.

### Settled: the fixtures found a protocol bug the moment they reached new state

The medic/pyro/engineer recordings desynchronised on their first outing, and the
cause had been shipped for six phases: **an `INPUTSTATE`-tier update carries no
sentry bytes at all** and the reader consumed one anyway, so every stream
containing an engineer slipped a byte per sentry per tick.
`docs/PROTOCOL.md`, "Per sentry", has the layout.

Three things about how it stayed hidden are worth more than the bug:

1. **No recording had ever contained an engineer.** Every fixture was scout,
   soldier, demoman and medic, so bit 1 of `subobjects` had never been set on a
   tier that omits the block. docs/TRAPS-SIMULATION.md's "when adding a fixture, ask what state
   it cannot reach" is the whole lesson, and the answer this time was a class.
2. ⚠️ **`TraceParser.Parse` swallowed it.** A capture can legitimately stop
   mid-message when the socket closes, so the loop treats a `ProtocolException`
   as the end of the trace — which makes a *desynchronised reader*
   indistinguishable from a short recording. `hotsupport` framed 26 of its 1,570
   messages, dropped 59,000 bytes, and every test that loaded it passed. There
   is now an overload that reports where framing stopped, and
   `FixtureFramingTests` asserts every checked-in fixture consumes its last
   byte.
3. **It surfaced hundreds of bytes downstream**, as "no framing for server
   message 0" — a byte of character data being read as a message type. A
   framing error never points at its cause. The way to localise it is to
   **bisect the roster**: record five seconds each of six pyros, six medics and
   six engineers. Two frame cleanly and one does not, and that is the answer in
   three minutes.

### Four classes now exist in the port that no fixture has ever contained

⚠️ **The heavy, the sniper, Quote and the spy all landed on 2026-08-26 and not
one of them appears in any checked-in recording.** Every fixture in the project
is some mixture of scout, soldier, demoman, medic, pyro and engineer, which is
what the bots pick.

That matters twice over:

1. **The wire has not been exercised for them.** The engineer's `INPUTSTATE`
   bug above shipped for six phases for exactly this reason, and the sniper's
   charge byte, the spy's `cloakAlpha` byte and a spy's `cloak` bit in the
   `moveStatus` field are in the same standing now: written by the port, never
   read back by a real reader against a real recording. `FullUpdateLayoutTests`
   and the per-weapon layout tests are the only thing between them and the same
   class of bug.
2. **The simulation has no closed-loop evidence at all.** Three of the four are
   unseeded or hitscan (see "an unseeded weapon caps a fixture's hp window"), so
   even a recording containing them would not gate the shots — but it *would*
   gate the trajectories, the boxes and the wire.

**A six-of-each recording of the four is the cheapest thing on the live-session
list**, and the roster-forcing recipe in "how to record a fixture for a class the
bots will not reliably pick" is exactly the tool for it.

### Settled: the four recordings, and what they found

✅ **Recorded 2026-08-27** — `reference-ctf_truefort-closedheavy.trace`,
`-closedsniper.trace`, `-closedquote.trace`, `-closedspy.trace`. Six of one
class each, three a side, forced with `botAdd`; map reloaded under a connected
recorder, so **all four are spawn-anchored and closed-loop**.
`NewClassFixtureTests`.

**The headline is a negative: all four frame cleanly to the last byte.** There
is no `INPUTSTATE`-style layout bug for the heavy, the sniper, Quote or the spy.
Six of one class per recording is CLAUDE.md's bisect-the-roster shape used
*before* there is a bug rather than after — a stream that had desynchronised
would have named the class by construction.

| fixture | ticks | anchor | exact (trajectory) | fires | deaths |
|---|---|---|---|---|---|
| `closedheavy` | 2,235 | t499 | **270** | 0 | 3 |
| `closedsniper` | 2,231 | t613 | **362** | 32 | 5 |
| `closedquote` | 2,227 | t486 | **1,756** | 0 | 0 |
| `closedspy` | 2,229 | t406 | **1,829** | 0 | 0 |

⚠️ **The spy's 1,829 and Quote's 1,756 are the longest closed-loop windows in the
project by a factor of three** — the best before them was `closedsupport`'s 489.
Do not read that as those two classes being better implemented. It is that a
bubble barely pushes anybody and a cloaked spy is not fighting, so almost nothing
in either recording depends on a weapon at all. **A long window on a quiet
recording is worth less than a short one on a loud one**, which is the
`reference-ctf_truefort-combat.trace` lesson in a new costume.

⚠️ **An unseeded weapon caps a *trajectory* window, not only an hp window.**
That section below is written about hp, and the heavy is the counterexample: the
Minigun broadcasts no seed, so the port lands its pellets somewhere else, shoves
people somewhere else and diverges on **position** at t769. The sniper's Rifle is
seeded and reaches 362. Both are short for the weapon's reason, not the class's —
which is why `NewClassFixtureTests` compares trajectory only and says so.

⚠️ **Three of the four record no `WEAPON_FIRE` at all, and a zero there is not a
quiet recording.** The Minigun and the Blade call `randomize()`; only the Rifle
is seeded. A heavy-only recording with three deaths in it still shows `0 fires`.
Read the hp instead — `NewClassFixtureTests` asserts `MinObservedHp` for exactly
this reason.

**States no earlier fixture could reach**, each now asserted:

- **A cloaked spy.** `cloak` rides in bit 0 of the flags byte of every
  `QUICK_UPDATE`; until this recording the bit had only ever been written by the
  port and read back by the port's own reader.
- **`TOGGLE_ZOOM`** — 22 of them. Zoom is never serialised as state, so this is
  the first recorded evidence the message exists on the wire here at all.
- **`OMNOMNOMNOM`** — the heavy's sandvich.
- **`RETURN_INTEL`** in the Quote recording, an objective event inside a trace.

⚠️ **The recorder contributes a `PLAYER_CHANGECLASS` of its own.** It joins as a
passive spectator and its class byte is the default **Scout**, so the set of
classes a single-class recording names is always `{the class, Scout}` and never
`{the class}`. Assert the pair rather than filtering the Scout out — a
*seventh* class appearing there means the roster forcing did not hold.

**The gate bites sharply and per class:** changing the spy's `RunPower` from
1.08 to 1.09 takes `closedspy` from 1,829 exact ticks to **10**, and leaves the
other three untouched.

### Settled: the airblast can be gated by a recording after all

✅ **`reference-ctf_truefort-closedairblast.trace`**, recorded 2026-08-27:
**three pyros against three soldiers** on `ctf_truefort`, 2,666 ticks,
spawn-anchored, framed cleanly. `AirblastFixtureTests`.

| compared | anchor | exact | with `Weapons.AirBlast` stubbed |
|---|---|---|---|
| trajectory | t471 | **1,266** | **286** |
| trajectory + hp | t471 | **1,133** | — |

⚠️ **docs/SIMULATION.md said "the Flamethrower is unseeded, so no recording can
gate it" and that was a statement about the fixtures that existed.** Two
corrections, and the second generalises:

1. **The reflectable set is `Rocket`, `Flare` and `Mine`**
   (`Flamethrower.events/User Event 2.xml`). `closedsupport` is the only earlier
   fixture containing pyros and it is medic/pyro/engineer — **no rockets, no
   mines** — so its pyros could never press the key. Implementing the airblast
   moved no bound because no bound could have moved. **The roster decides which
   behaviours a recording can even reach; ask that before concluding a
   subsystem is ungateable.**
2. ⚠️ **An unseeded weapon caps an hp window; whether it caps a *trajectory*
   window depends on whether it moves people.**
   `Projectiles.BurningHitCharacter` deals damage and calls no `motion_add` at
   all, so a flame landing somewhere else cannot shift a trajectory — where a
   Minigun's pellets can, which is exactly why `closedheavy` stops at 270. Both
   weapons are unseeded and only one of them caps a trajectory.

**Its hp window is unusually long for a recording with a pyro in it** — 1,133
against `closedsupport`'s 328 — because the damage here is overwhelmingly seeded
rocket damage and a pyro has to be close to burn anybody. That 1,133 is second
only to `closedcombat`'s 1,204 and is still capped by the first flame that
connects and lands differently; **do not try to raise it by implementing
something.**

It also holds two `GRAB_INTEL`s, which no earlier fixture had.

### Settled: `Fields.Sentries`, and the divergence it found on its first run

✅ **`reference-ctf_truefort-closedsentrykill.trace`**, recorded 2026-08-27:
**three engineers against three soldiers**, 3,592 ticks, spawn-anchored, framed
cleanly. Six sentries built, five landed, **three destroyed** — the first
`DESTROY_SENTRY` in any recording in this project. 125 seeded rocket fires,
eleven deaths. Trajectory window **581** ticks from t427.
`SentryKillFixtureTests`.

⚠️ **The harness could not see any of it, so `Fields.Sentries` was added.**
`Replayer` *injected* `BUILD_SENTRY` like a spawn and compared **no sentry state
whatever** — a port that destroyed the wrong sentry, or none at all, replayed
every fixture in the project without a murmur. It is the `Fields.Deaths`
argument only stronger: **a sentry writes no bytes at all on an `INPUTSTATE`,
not even its hp**, so on six ticks in seven a recording says nothing about one,
and on the seventh a destroyed sentry is an unset subobject bit naming nobody.
`SentryRecord` mirrors `DeathRecord`; `TickEvents.SentryDestroyed` already
existed and was simply never collected.

**The three destroys are not alike, and the difference is the whole finding:**

| tick | killer | assistant | cause | port |
|---|---|---|---|---|
| t496 | 255 | 255 | **0** (self-destruct) | ❌ missed |
| t1691 | 4 | 255 | **9** (a soldier shot it down) | ✅ reproduced exactly |
| t2296 | 255 | 255 | **0** (self-destruct) | ❌ missed |

✅ **The weapon kill — the thing the recording was made for — the port
reproduces on the tick, with the killer.** It is inside the trajectory window.

⚠️ **The port reproduces neither self-destruct, and one of them is a real
divergence.** `Sentries.BeginStep`'s three-clause guard destroys a sentry with
`killer 255, cause 0`; the reference fires it **one tick after the sentry is
built**, twice, both for slot 1. **Only t496 is evidence** — it is inside the
window (t427 + 581 = t1008), so both sides still agree about where everything
is. t2296 is thirteen hundred ticks past the window, where the port's sentries
are elsewhere entirely, and proves nothing on its own.

Two candidate causes, neither run down, both in `src/Gg2.Simulation` and so not
this lane's to fix: either the port never built that sentry (nothing to destroy)
or it built one and `AnySentryInCircle`/the class-and-team clauses did not fire
where the reference's did. **A blanket "Build never works" is already ruled
out**, because the port builds at t1125 — that is the sentry it correctly kills
at t1691.

`SentryKillFixtureTests.ThePortStillMissesBothOfTheReferencesSentrySelfDestructs`
pins the two ticks, so **fixing it fails the test on purpose**. Delete the pin
and fold the ticks into the test above when it does.

**The gate bites:** making `Sentries.ReportDestruction` not set
`events.SentryDestroyed` fails both sentry tests.

### Settled: a replay gave a spawning character one Begin Step too many — FIXED

✅ **2026-08-27, in the harness.** The section below is how the divergence was
found and diagnosed; this is what closed it.

**`ITraceSimulation.QueueSpawn`** asks the simulation to build a spawning
character **inside the next tick's alarm stage** instead of placing it before the
tick runs. `PortedSimulation` arms `player.alarm[5]` and hands
`MatchSimulation.Tick` the map's spawn points, so the tick itself creates the
character exactly where `PlayerSpawn.gml` does.

⚠️ **The spawn point still has to be the recorded one, and the alarm stage draws
it from the shared stream.** The harness cannot reach into the middle of a tick,
so it arranges the stream beforehand: GM8's generator is Delphi's
`seed' = seed*0x08088405 + 1` with `random(x) = x*seed'/2^32`, so the seeds
producing a wanted first result form **one contiguous run**, every later draw
follows from it, and walking that run finds a seed for a whole six-slot spawn
wave in a few thousand steps. `PortedSimulation.TryForceSpawnPointDraws`, which
verifies each candidate by running a real `GmlRandom` rather than by re-deriving
the algebra. **Overwriting the stream is free here and nowhere else** — the
spawn-point choice is broadcast, and a replay already forces every weapon seed,
which is the one place the stream is contract.

⚠️ **The spawn must be queued a tick EARLY**, off the *next* tick's messages —
the same off-by-one a fire seed and a death have. `GameServerBeginStep.gml`
serialises the state inside Begin Step and the alarm stage runs after it, so a
character created on a tick cannot appear in that tick's own update. **Queueing
when the message arrives is a tick late and breaks every spawn-anchored fixture**;
measured both ways, and the wrong way failed twenty tests at once.

| Fixture | Window before | Window after |
|---|---|---|
| `reference-ctf_2dfort-closedrespawn.trace` | 5 | **362** |
| `reference-gg_debug-closedtdm.trace` | 2 | **23** |
| every other fixture | — | unchanged |

Mutation-checked: stopping the replayer from queueing fails exactly the two tests
that assert those windows and nothing else.

⚠️ **What is left on `gg_debug` is a different finding: the DROPDOWN PLATFORM.**
The port does not implement one — `CharacterPhysics` says so in two comments —
and this recording is the first evidence that it matters. `gg_debug` places
**two**, at `438,216` and `498,216`, and the residual is slots 1 and 3 at rest on
`y = 192` in the reference while the port falls past; a soldier's origin sits
22.6 above its foot, so `192 + 22.6 = 214.6` is a character standing exactly on a
platform whose top is 216. ⚠️ **The earlier note ruled dropdown platforms out
because "`tdm_mantic` has four and is exact"** — but a map placing a feature is
not its bots touching one, which is the same mistake the `tdm_mantic` move-box
recording made. **Open, and owed to the lane that owns `src/Gg2.Simulation`.**

⚠️ **`ctf_2dfort`'s capture is still out of reach**, and no longer for this
reason: its `GRAB_INTEL` is at t1404 and the window now ends at t877, so what
separates them is ordinary drift. A fixture that gates `doEventScoreIntel` needs
the objective to happen **early**, not another physics fix.

### How that divergence was found: the first fixture on a second map lands 0.4 px low

⚠️ **`reference-ctf_2dfort-closedrespawn.trace`**, recorded 2026-08-27: three
scouts against three soldiers on **`ctf_2dfort`**, the narrowest CTF map, chosen
so the bots would meet early enough for a death and its 150-tick respawn to sit
inside a closed-loop window. **It does not deliver that**, because its window is
**five ticks**. `SecondMapFixtureTests`.

**Every character lands 0.4 world pixels low, on its very first fall.**
`ctf_2dfort`'s spawn points are at `y = 894` and the floor beneath them stops a
scout at `y = 900`, so everybody drops six pixels the moment they spawn. The
reference lands on exactly **900**; the port lands on **900.4** — all six slots,
on the same tick (five after the wave), and it stays there. Every mismatch reads
`y expected 4500, got 4502`.

**The map layer is exonerated, and that was measured rather than assumed:**

- The engine, asked with a live scout's own box at the red spawn column, answers
  `place_free` **true for y ≤ 900 and false for y ≥ 901**.
- The port's `CollisionWorld.PlaceFree` answers **identically**. That is now a
  permanent test, and it is a fifth map for the collision layer —
  `collision-samples.json` covers `ctf_truefort`, `koth_valley`, `gg_debug` and
  `arena_lumberyard`, and never this one.
- `maps.json` already gates this map's mask cell-for-cell, and the map places
  **no gates, no player walls and no dropdown platforms** — one `Obstacle` and
  nothing else. Checked live: `instance_number(DropdownPlatform) = 0`.

⚠️ **So it is the landing, and the reason no earlier fixture caught it is that
no earlier fixture ever dropped anybody.** On `ctf_truefort` the spawn wave
produces no such offset — six fixtures there stay exact for hundreds of ticks
past their anchors — so a spawn-drop is a path phase 4's original 48
trajectories evidently never took. **An open finding for the lane that owns
`src/Gg2.Simulation`.**

`SecondMapFixtureTests.ThePortLandsFourTenthsOfAPixelLowOnThisMap` pins the
five-tick window, so **fixing it fails that test on purpose**.

#### ✅ ROOT CAUSE, 2026-08-27: a replay gives a spawning character one Begin Step too many

**The reference creates a character in the ALARM STAGE.** `player.alarm[5]`
fires `PlayerSpawn`, which runs between Begin Step and Step — so on its spawn
tick the character gets a Step and an End Step but **no Begin Step**, and Begin
Step is where gravity is applied. Its very first End Step therefore still sees
`vspeed == 0`, which is the guard on the stair descent in
`Character.events/End Step.xml`:

```
if(vspeed == 0 and ...)
    if(place_free(x,y+6)) if(!place_free(x,y+7)) y += 6;
```

**So a character spawning within six pixels of the floor is snapped onto it on
its first tick, not dropped onto it over four or five.**

⚠️ **A replay places the character BEFORE running the tick** —
`Replayer` applies a recorded `PLAYER_SPAWN` by calling
`ITraceSimulation.SpawnCharacter`, so the character exists for that tick's Begin
Step, takes gravity there, arrives at End Step with a non-zero `vspeed`, and
falls instead.

**Verified both ways** (`SpawnTickTimingTests`, on `gg_debug`, marker (570,162),
floor blocked from 169):

| how the character is created | first tick |
|---|---|
| **alarm stage** (`RespawnTimer = 1`), as the reference does | **y = 168, vspeed 0** |
| **pre-placed then ticked**, as the replay does | still airborne, vspeed ≠ 0 |

The recording agrees with the first row exactly: slot 1 is observed at y = 168
with zero vspeed two ticks after the wave, and stays there.

⚠️ **Which is why `ctf_truefort` never caught it**: its markers are **eighteen**
pixels above the floor — outside the six-pixel snap — so both sides genuinely
fall and agree. `gg_debug` (162 → 168) and `ctf_2dfort` (894 → 900) are exactly
six.

**The fix needs a seam that does not exist**: a way to spawn at a *given* point
through the alarm stage. Arming `RespawnTimer` alone is not enough, because the
stage picks its own point and would race the recorded choice — which is exactly
why `PortedSimulation` places directly today. **So the simulation is correct and
the harness cannot currently express what the reference does**; closing it means
adding that seam.

#### Narrowed by the two Team Deathmatch recordings

Two more maps were recorded on 2026-08-27 and they cut the search space hard.

| fixture | map | window | note |
|---|---|---|---|
| `closedtdm` | `tdm_mantic` | **3,043** | exact on `Trajectory｜Hp｜Deaths`, and quiet |
| `closedtdm` | `gg_debug` | **2** | same signature as `ctf_2dfort`, much larger |

On `gg_debug`, two ticks after the anchor and **all six slots at once**: the
reference has them at rest at `y = 168` with `vspeed 0`, the port at `163.2`
still falling at ~1.18 px a tick — exactly where a character dropped from the
spawn marker at `y = 162` sits after two ticks of gravity.

**What that rules out:**

- ❌ **"Any map that is not `ctf_truefort`."** `tdm_mantic` is a third map and
  is exact for 3,043 ticks.
- ❌ **The collision mask.** Established for `ctf_2dfort` by asking the engine
  and the port the same column.
- ❌ **Dropdown platforms**, which the port does not implement: `tdm_mantic` has
  **four** and is exact, `ctf_2dfort` has **none** and diverges.
- ❌ **Player walls and bullet walls**: `gg_debug` has 2/1/2 of them,
  `ctf_2dfort` has none, and both diverge.

**The signature both failing maps share:** the reference is at rest on an
**integer** `y` shortly after the anchor while the port is still falling from a
spawn marker **six pixels** above it (894→900, 162→168). On `ctf_truefort`,
where the port is exact, the reference instead comes to rest on a
**fractional** y (474.47) after an eighteen-pixel drop — which is what a fall
that really happened looks like.

⚠️ **Root cause deliberately not claimed.** `doEventSpawn.gml` and
`offloadSpawnPoints.gml` were both read and neither applies any offset — a
character is created at the spawn marker verbatim — so the next step is on the
port's side of the replay and belongs to whoever owns `src/Gg2.Simulation` and
`src/Gg2.Parity`.

### Settled: the first King of the Hill fixture, and the gap it exposes

✅ **`reference-koth_valley-closedkoth.trace`**, 2026-08-27: three scouts against
three soldiers, 3,598 ticks, spawn-anchored, **framed cleanly**, reported by
census as `KingOfTheHill, 1 control point(s)`. `KothFixtureTests`.

**The framing is the sharp result, and it is sharper than it looks.** The
control-point block carries **no count prefix** — a reader has to build the same
points from the same map, in the same order, or every update after the first is
misframed. Nothing had ever tested that against a real stream.

**Proved by mutation:** making `GameModes.ControlPointsFor` return 2 for KotH
fails `FixtureFramingTests` on this fixture and all four `KothFixtureTests` —
and **no other fixture in the project fails**, because none of them has a
control point. This recording is the only thing here that can catch that class
of bug.

| what | value |
|---|---|
| exact window (`Trajectory｜Hp｜Deaths`) | **330** ticks, t409 → t739 |
| `UNLOCKCP` | **1** — the project's first |
| `CAPS_UPDATE` | **39** (a CTF fixture holds one or two) |
| weapon fires / deaths | 118 / 14 |

The roster is scouts against soldiers on purpose: **a scout's `capStrength` is 2
and every other class's is 1**, the only class override in the capture rules.
`UNLOCKCP` is contract rather than decoration — KotH always sends it and DKotH
never does, so it is the one message separating the two modes on the wire.

⚠️ **It is also a useful negative for the landing finding**: `koth_valley` is a
fifth map and behaves normally — the first divergence is ordinary single-slot
drift of two wire units at t739, not six characters falling at once.

#### ⚠️ Nothing in the harness compares a control point

`StateUpdateReader` parses the block into
`ControlPointBlock(Team, CappingTeam, Capping)` and **`TraceParser` then throws
it away** — a `TraceTick` carries character slots and messages and nothing else.
So a port that never captured the hill, captured it for the wrong team, or moved
the meter at the wrong rate replays this fixture exactly as well as a correct
one. `ControlPointTests` and `DualKothTests` remain the whole gate for two
gamemodes.

✅ **Closed on 2026-08-27**: `TraceTick.ControlPoints`, `Fields.ControlPoints`,
`ITraceSimulation.TryGetControlPoint`, `Replayer.ReconcileControlPoints`.

⚠️ **The block rides on a `CAPS_UPDATE` or the join `FULL_UPDATE` and on
nothing else** — `StateUpdateReader` returns before the HUD tail on any other
tier. Three consequences, each of which cost an attempt:

- **`TraceParser` must take it from `pending`, not from the tick's own update.**
  Neither a `FULL_UPDATE` nor a `CAPS_UPDATE` delimits a tick, so both are
  sitting in the pending list when the tick is built. Reading `state.ControlPoints`
  gives an empty array on every tick.
- ⚠️ **The comparison must NOT be gated on `HasPositions`.** A `CAPS_UPDATE`
  arrives independently of the positional cadence, so gating on it silently
  skipped **every** observation — the comparison reported zero mismatches and
  looked like a pass.
- **A recording observes a hill a few dozen times in several thousand ticks**,
  on no fixed cadence. `koth_valley` holds 40 observations, 9 of them after the
  anchor.

#### ⚠️ It found two harness bugs immediately, and neither could have been seen before

1. **`PortedSimulation.LoadMap` never called `ControlPoints.Place`.** It placed
   the briefcases and stopped, so **a replay of any KotH, Control Points or
   Arena map ran with no control point and no capture zone at all** — a
   character standing on the hill was standing on nothing.
2. ⚠️ **Fixing that alone was not enough.** `ControlPoints.Place` switches on
   `state.GameMode` to decide which entity types are points, and a defaulted
   `WorldState` is Capture the Flag — which places **none** of a KotH map's. The
   mode has to be set from the map first, as `GameServer` does.
   `KothFixtureTests.AReplayNowHasTheHillItIsSupposedToHave` gates both;
   removing either line fails it.

#### ⚠️ What it still cannot gate

**Every one of `koth_valley`'s nine post-anchor observations falls outside its
330-tick trajectory window** — the earliest is t1010 and the window ends at
t739. By then the port's characters have drifted, so which of them stands on the
point can differ for reasons unrelated to the capture rules, and the eleven
differences the comparison reports (`cappingTeam`, and `capping` off by ±1)
cannot distinguish a wrong rule from positional drift.

**So the plumbing is proven and the rules are not** — for that recording.

### Settled: the uncontested KotH fixture, and what it caught

✅ **`reference-koth_valley-uncontested.trace`**, 2026-08-27: **six red scouts
and no blue team**, spawn-anchored, framed cleanly, **zero weapon fire and zero
deaths**. `KothCaptureFixtureTests`.

⚠️ **The one-sided roster is the design.** The hill cannot be captured before
its unlock timer fires (~900 ticks), and the unlock **cannot be forced** — the
port simulates its own, so forcing the reference's early manufactures a mismatch
rather than exposing one. So the *window* had to grow instead, and what ends
these windows is **weapon knockback**. With nobody to fight:

| | contested (`closedkoth`) | uncontested |
|---|---|---|
| trajectory window | 330 | **3,111** |
| earliest CP observation after anchor | t1010 (**outside**) | **all inside** |
| can it gate the capture rules? | no | **yes** |

#### ⚠️ A third harness bug, and then a real finding

**`PortedSimulation.LoadMap` also never called `Rounds.Start`** — the third
omission in that method, after `ControlPoints.Place` and `GameMode`.
`GameServer.OpenMap` calls it immediately after `Place` and says why: it is
"everything a gamemode's HUD sets in its Create event ... **the pregame lock**".
Without it a replay's hill is capturable from the first tick, so six characters
standing on it took it at once. Fixed.

**What is left is a genuine control-point divergence, and it is not drift.**
With trajectories exact for 3,111 ticks — every character where the reference
put it — **the port starts capturing about 180 ticks early**. Recorded: neutral
at t1086, capturing from ~t1240 (30 at t1246, 75 at t1261, 225 at t1311),
captured at t1336. The port already has the meter at **177 at t1086**.

Ownership is now right (that was the missing lock). ✅ **The rest was run down
on 2026-08-27 and it is a HARNESS limitation, not a simulation bug** — see
below. `KothCaptureFixtureTests.ThePortStillCapturesTheHillTooEarly` pins it.

### Settled: the objective is gated at last — a grab AND a capture inside a window

✅ **`reference-ctf_2dfort-uncontestedcap.trace`** (2026-08-27): **six red scouts
and no blue team**, spawn-anchored by the map-reload recipe, with the bots
steered to blue's briefcase and then home again. `IntelCaptureFixtureTests`.

**It replays with no mismatches at all, over its whole 4,934 ticks** — the
longest exact window in the project — and the objective happens in the middle of
it: anchor **t618**, `GRAB_INTEL` **t1355**, `SCORE_INTEL` **t2867**, last tick
t4933. Zero weapon fires, zero deaths.

⚠️ **This is the first fixture for which deleting the objective would fail a
test.** Adding the whole of it in phase 6 moved none of the five closed-loop
windows by a tick, because every earlier fixture observed the objective *outside*
its window — `reference-ctf_2dfort-closedrespawn.trace` holds a grab at t1404
against a window ending at t877. Mutation-checked: breaking the grab guard fails
`ThePortGrabsAndCapturesOnTheReferencesTicks`.

⚠️ **`Fields.Intel` compares the objective's MESSAGES, not wire state.** A
briefcase and a carrier's `intel` byte are `FULL_UPDATE`-only and a recording
holds exactly one of those, on the join path before anything has happened. What
is gated is that the port emits a `GRAB_INTEL` and a `SCORE_INTEL` on the
reference's own ticks — which is what `doEventGrabIntel` and `doEventScoreIntel`
amount to from outside.

**The recipe generalises, and this is now the second time it has paid:** a
one-sided roster buys a window long enough to reach a slow objective, because
**what ends a closed-loop window is weapon knockback**. Removing the enemy team
took the KotH capture from unreachable to gated, and it has now done the same for
the flag. ⚠️ It costs what it buys — a long window on a quiet recording is worth
far less per tick than a short one on a loud one — so use it for objectives, not
for physics.

⚠️ **And steering the bots there is legitimate.** A replay consumes recorded
`INPUTSTATE`s and simulates no bot AI, so `botGoalLocked`/`botSetGoal` change
what gets recorded and nothing about how it replays. A **teleport** would break
the anchor; a goal does not.

### Settled: when a KotH point unlocks, and why a replay is early

`KothHUD.events/Create.xml` sets `cpUnlock = 900`; its Step fires
`doEventUnlockCP` on the tick it reads `cpUnlock == 0` and decrements
**afterwards**, so the unlock lands **901 ticks after the HUD was created** —
which is when the server restarts the room.

✅ **Measured, and the model is exact on two independent recordings:**

> **unlock = (spawn wave − 150) + 901**

| fixture | wave | implied round start | predicted | recorded |
|---|---|---|---|---|
| `uncontested` | t485 | t335 | t1236 | **t1236** |
| `closedkoth` | t404 | t254 | t1155 | **t1160** |

The 150 is the ordinary respawn time — a reload destroys every character and
they come back on the usual timer, which `RespawnTimingTests` measures
independently. The five-tick slack on the second is the wave's own stagger
(t404 and t409).

⚠️ **`CHANGE_MAP` is NOT the round start, and a replay assumes it is.** Both
recordings carry exactly one `CHANGE_MAP`, at **t0**, and unlock 1236 and 1160
ticks later — two different offsets, neither of them 901. If `CHANGE_MAP` were
the round start both would be 901.

**So the port was right and the replay was early.** ✅ **Fixed 2026-08-27**:
`Replayer` anchors `cpUnlock` from the wire, and with it the port reproduces
`reference-koth_valley-uncontested.trace`'s hill **exactly — zero mismatches**
on ownership, capping team and meter, with the trajectories exact throughout.
`KothCaptureFixtureTests.ThePortReproducesTheHillExactly`.

**The capture arithmetic is gated against the engine for the first time.** By
mutation, halving a scout's `capStrength` from 2 to 1 gives
`capping exp 30 got 20`, `exp 75 got 50`, `exp 225 got 150` — the 2:1 ratio,
visible directly — and the capture then never completes. Dropping the
"`pointCapture` leaves `cappingTeam` set" quirk fails it too.

#### Four things that had to be right, each of which cost an attempt

1. ⚠️ **`CHANGE_MAP` is not the round start and the delay is not a constant.**
   The two recordings restart their rounds **335** and **254** ticks after it, so
   there is nothing to subtract.
2. ⚠️ **The first HUD readings are stale.** The server keeps serialising the old
   round's HUD after announcing the change: `uncontested` reads `cpUnlock = 0` at
   t0 and t62 and does not show the new round's countdown until **t1086, reading
   149** (which lands the unlock on t1236, as recorded).
3. ⚠️ **So only a POSITIVE reading may be taken.** A zero is ambiguous — it means
   "already unlocked", true of both rounds. A positive value is unambiguous,
   because `KothHUD.events/Step.xml` fires the unlock on the tick the counter
   reads 0: it says both "this many ticks left" **and** "still locked", which is
   why the simulation re-locks on it.
4. ⚠️ **The unlock is a latch, and re-locking must discard the meter.** Setting
   the counter back does not re-lock a point that has already unlocked; and a
   lock that left the meter standing is a state the reference cannot be in —
   `ControlPoint`'s Step does `capping = 0; exit;`. Without that the anchor tick
   itself mismatched by `capping 177`.

**What is conceded** is the countdown, which is `-1` a tick, unit-tested, and
gated against the recordings directly by `KothUnlockTimingTests`. **What is now
gated** is everything downstream — presence, capture rate, ownership — so
`ControlPointTests` and `DualKothTests` are no longer the *whole* gate for King
of the Hill.

⚠️ **A recorder that joins before a map reload cannot see the new round's
countdown for several hundred ticks.** That is a property of the recipe, not a
bug: the closed-loop recipe deliberately reloads *under* a connected recorder so
the spawns anchor. The positive-reading rule is what makes both work at once.

### Settled: `tdm_mantic`'s move boxes cannot be recorded, and the MAP is why

⚠️ **Three attempts now, and the third measured the cause.** Two with bots, and
one on 2026-08-27 with the **driven client** the second attempt concluded was
needed. The driven attempt failed too — and it established that the obstacle is
the map's own geometry rather than the tooling, which is what makes this a
closed question rather than an open one.

Measured against the running game by enumerating `MoveBoxUp`/`MoveBoxDown`; the
port's map layer agrees, and `MoveBoxTests` pins every row:

| Fact | Measured |
|---|---|
| Up boxes cancelled by a down box at the same `bbox_left`, `bbox_top + 6` | **6 of 9** (the engine answered `111111000`) |
| Up boxes that actually lift | 3, at `(2028,1014)`, `(2100,1266)`, `(2160,852)` |
| `pushPower` | 5 on every box; no `speed` override anywhere |
| A character dropped into `(2028,1014)` | **hovers indefinitely**, y ≈ 915 ↔ 1003, ~30-frame period |
| Scout jump apex | **58 px** |
| Platform below the middle fan → its catch window | 75 px short |
| Floor below the lowest fan → its catch window | 123 px short |

⚠️ **Two thirds of this map's move boxes are inert.** A character's box is 34
tall against the box's 42, so one overlapping an up box overlaps its down partner
as well, and five up plus five down is exactly zero. That alone explains both bot
attempts: most of what the bots were being steered at does nothing at all.

⚠️ **And the three that work hang in open air** — a diagonal chain of updraft
fans with no floor near them, which is not somewhere a bot goes. ✅ **They do
work**: a character placed in one hovers forever, so the rule is right and the
*approach* is what is impossible.

⚠️ **A driven scout cannot reach them either.** A straight jump falls 17 px short
of the fan above the platform, and walking off the platform edge at run speed
overshoots the fan below — measured, the character passes `x ≈ 2210` at the fan's
`y`, where the catch window ends at 2147.

**What is left to try:** a driven **soldier or demoman** entering a fan on a
rocket or mine jump, with the aim written into the client's own input — aim rides
in `INPUTSTATE`, so it is an input rather than a teleport. ⚠️ **A teleport is not
an option**: it moves a character the port would have had to derive, which breaks
the closed-loop anchor.

The two new gates are
`MoveBoxTests.SixOfManticsNineUpBoxesAreCancelledByADownBoxSixPixelsBelow` and
`ACharacterCentredOnACancelledUpBoxIsPushedNowhere`. Mutation-checked: making a
down box push nothing fails the second.

#### The two bot attempts, kept for the technique

`tdm_mantic` is the only shipped map placing move boxes (fifteen, confirmed
live, clustered at x 1968–2237, y 852–1499 — including the exactly-cancelling
stacked pairs docs/SIMULATION.md describes) or pit falls (six). Two attempts to
get a character onto one:

1. **A 110-second recording with six bots' goals locked across the lift column**
   (`botGoalLocked` plus `botSetGoal` above and below it). Framed cleanly,
   2,859 exact ticks — and **zero observations anywhere inside the box
   rectangle**. Deleted rather than checked in: it gates nothing that
   `reference-tdm_mantic-closedtdm.trace` does not already.
2. **Goals set directly ON individual boxes**, then sixty seconds watched live
   with `collision_rectangle` over the whole column. **No character ever entered
   it.**

**So the bots will not path there**, and whether that is a missing nav route or
something else was not determined. `MoveBoxTests` and `KillBoxTests` remain the
whole gate for both families. **The next attempt needs a driven client, not
bots** — CLAUDE.md, "Driving a real client".

✅ **One technique worth keeping from the attempt: steering bots cannot
invalidate a closed-loop fixture.** A replay consumes the recorded
`INPUTSTATE`s and simulates no bot AI at all, so `botGoalLocked` and
`botSetGoal` change *what inputs get recorded* and nothing about how they are
replayed. That makes goal-locking a legitimate way to aim a recording at a
behaviour — unlike teleporting, which moves a character the port would have to
have derived and breaks the anchor.

### Settled: Arena has a fixture, and the last unreached framing risk is reached

✅ **`reference-arena_lumberyard-closedarena.trace`** (2026-08-27): three
soldiers a side, spawn-anchored by the map-reload recipe. **Arena was the only
gamemode never recorded**, and it carried the project's oldest unreached framing
risk. `ArenaFixtureTests`.

⚠️ **`ARENA_ENDROUND` is not a bodyless message and this port framed it as one**
until phase 6's arena work. It carries five bytes and then five more *per MVP*,
so the counts must be read to know the length — a reader that skipped the body
would have lost the stream the first time a round ended. It survived because no
recording reached the state, exactly as the sentry `INPUTSTATE` bug did for six
phases.

✅ **The recording frames to the last byte, three round-ends and all**: 3
`ARENA_ENDROUND`, 5 `ARENA_STARTROUND`, 4 `ARENA_RESTART`, 9 deaths, 72 weapon
fires over 4,495 ticks. Every `ARENA_ENDROUND`'s length is exactly
`5 + 5 × (mvpsRed + mvpsBlue)` as re-derived from the counts inside its own
payload, and at least one carries MVP records — **which is the control that
matters**: if every round had ended with both lists empty, a fixed-length reader
would frame this recording perfectly and the bug would still be unreached.

⚠️ **What this adds over the synthetic gates.** `ArenaTests` and
`PlayerStatsLayoutTests` already round-trip the same arithmetic through the
port's own writer and reader, so they agree with each other whether or not either
agrees with the engine. This is the reference's bytes. Mutation-checked: dropping
the MVP skip fails all five fixture tests and both synthetic ones — the
difference is what a *pass* means, not what a failure catches.

⚠️ **Its trajectory window is short by construction and is not the point.**
Arena re-spawns everybody at the start of every round (`respawnEveryone()` calls
`PlayerSpawn()` directly rather than arming `alarm[5]`), so the recording holds
**54** `PLAYER_SPAWN`s across five round starts and its last exact anchor is very
late.

**Of the four self-shortening messages, three are now reached by a recording**
— the sentry `INPUTSTATE` block, the generator HUD block and `ARENA_ENDROUND`.
⚠️ **`BALANCE` is the one left**: one body byte for a notice, two for a switch,
and no fixture reaches it. `ServerBalanceTeams` is called only on a death, a
departure and a team change, so a recording would need autobalance on and lopsided
teams.

### Settled: `BALANCE` is reached, and all four self-shortening messages are done

✅ **`reference-ctf_2dfort-autobalance.trace`** (2026-08-27): four soldiers
against two, spawn-anchored, with the roster forced lopsided. **Three `BALANCE`
messages and it frames to the last byte.** `AutobalanceFixtureTests`.

⚠️ **`BALANCE` is not a fixed-width message and this port framed it as one** —
two body bytes — until phase 6. `BALANCE, 255` is the "teams are uneven" notice
and `BALANCE, playerId, class` says somebody was moved; the client reads one byte
and only then decides whether to read a second
(`ClientBeginStep.gml:244-253`), so a reader that always takes two is **one byte
ahead from the first notice onwards**.

**Both shapes are in this stream**, which is the property that matters — a
recording holding only notices would frame perfectly under the old reader for the
wrong reason, and one holding only switches for the right one. Mutation-checked:
forcing the reader back to a fixed two fails all four fixture tests, the two
`TeamBalanceTests`, and `FixtureFramingTests` for this file.

**How to reach it**, because it took three conditions at once:

1. **A team two players larger than the other** (`ServerBalanceTeams.gml`). Every
   other fixture here is a balanced bot roster, which is why it was never
   reached.
2. **Deaths.** The script is called only on a death, a departure or a team
   change — the fuse (`balancecounter`) runs every tick, the check does not.
3. **The fuse expired, with a dead player available.** The switch needs
   `balancecounter >= Server_Respawntime + 30*2` — 210 ticks at the default — and
   only **dead** players are eligible, the guard reading the *character*, so a
   player awaiting respawn qualifies.

⚠️ **And the map reload re-balances the roster underneath you.** A map change
puts every player back to spectator and the bots re-pick, which turned a forced
4/2 into **3/3** — measured. **Force the roster _after_ the reload**, not before.
The existing recipe above says to force it first, which is right for classes
(every class-forced fixture's class set survives the reload) and wrong for teams.
The `PLAYER_LEAVE` in this recording is that second forcing, and it is the only
fixture other than `combat` to hold one.

**All four self-shortening messages are now reached by a recording** — the sentry
`INPUTSTATE` block, `ARENA_ENDROUND`, the generator HUD block and `BALANCE`.
⚠️ **When adding a message, ask what makes its length change** — that family has
produced four framing bugs and every one of them survived until a recording
reached the state.

### Settled: every bound re-measured after the alarm-stage spawn seam

✅ **2026-08-27, all 25 fixtures.** The seam
(`ITraceSimulation.QueueSpawn`) changed how *every* spawn-anchored fixture
starts, so the rule at the top of this section applied: **re-measure after any
change, not only when a window is expected to move.**

**Fourteen closed-loop windows, and thirteen are unchanged to the tick:**
`closedcombat` 357 traj / **1,204 hp** / 1,325 deaths, `closedsupport` 489 / 328
/ 373, `closedairblast` 1,266 / 1,112 / 1,213, `closedspy` 1,829,
`closedquote` 1,756, `closedsniper` 362, `closedsentrykill` 581,
`cp_dirtbowl` 905, `koth_valley` 330, `uncontested` 3,111, `tdm_mantic` 3,043,
`closedgen` 408, `bots` 869.

⚠️ **The one that moved is the heavy: 270 → 396**, and it is attributed by
measurement rather than guessed — switching the queueing off reproduces 270
exactly. That the other thirteen did **not** move is the control: a seam that
shifted every fixture by a tick would have moved all of them.

**New windows from this session's fixtures:** `ctf_2dfort-closedrespawn` 362 (was
5), `gg_debug-closedtdm` 23 (was 2), `uncontestedcap` **4,315 — the whole
recording after its anchor**, `arena_lumberyard` 207, `autobalance` 4.
⚠️ **The last two are short by construction and are framing fixtures, not physics
ones**: Arena re-spawns everybody every round and the autobalance recording has
its roster re-forced mid-stream, so in both the *last* spawn anchor is a late
single spawn in the middle of an ongoing fight.

**Open loop:** `combat` max 33/20 p99 2/2; `hotcombat` max 77/75 p99 **2/2**
(down from 11 and 30 on 2026-08-25 — the maxima are single events and the
percentiles are what improved as the weapons landed); `hotsupport` p99 9/17,
unchanged, against ceilings of 15 and 25.

#### ✅ Diagnosed and fixed: an unseeded weapon moves the spawn-point draw

**The artefact was `hotsupport`'s open-loop max `|dx|` at 120 wire units with the
alarm-stage seam and 10 without it** — one comparison, t1287 slot 2, two ticks
after the spawn at t1285, expected 18402 got 18522. **24 world pixels, which is
exactly the spacing of `ctf_truefort`'s spawn markers** (1608, 1632, 1656, 1680,
1704). The port had spawned that character **one marker along**.

⚠️ **Because forcing the RNG before a tick is fragile, and this is the thing that
breaks it.** `TryForceSpawnPointDraws` arranges the stream so the alarm stage's
`floor(random(numSpawnPoints))` lands on the recorded id — but the alarm stage is
not the first thing in a tick. **Weapons fire in Begin Step, and an unseeded one
draws from the same shared stream.** A replay forces every *seeded* weapon's seed
from the recording, so those never disturb it; the Flamethrower, the Minigun and
the Blade do. `hotsupport`'s pyros fire constantly, so a respawn in the middle of
that fight came out on the wrong marker.

✅ **The fix removes the dependency instead of fighting it.**
`PortedSimulation.SeedIndependentSpawnPoints` hands the tick a spawn point list
whose every index for a spawning team resolves to the point the recording named —
`floor(random(n))` can return anything and the coordinates are the same. The
*count* is left alone so the same single draw is consumed and the stream ends
where the reference's did. `LevelMap.WithEntities` is the seam that makes it
possible. ⚠️ **Only when at most one slot per team is spawning**; a wave wants a
different point per slot out of one shared list, so those keep the seed search,
which is sound for them because a wave happens at a round start with nothing
shooting yet.

**Measured after: `hotsupport`'s max `|dx|` is 10 again and every one of the
twenty-two closed-loop windows is unchanged to the tick.** Mutation-checked:
disabling the narrowed list fails exactly one test, the max `|dx|` assertion
added to `SupportClassFixtureTests` for this — **no percentile and no window
notices**, which is why the maximum is asserted at all.

✅ **And there is a gate on it now: `SpawnPlacementTests`**, which replays every
fixture in both modes and asserts that no queued spawn lands anywhere but the
marker the recording named (`PortedSimulation.MisplacedSpawns`). It passes on all
25 today, five mid-fight Arena round starts included.

⚠️ **Its control says something the original diagnosis could not.** Disabling the
narrowed list misplaces spawns in **three** fixtures — `closedsupport`,
`closedairblast` and `hotsupport` — and those are **exactly the three recordings
with a pyro in them**. The unseeded Flamethrower is the whole mechanism, and it
is now three data points rather than one.

⚠️ **Two of those three are closed-loop fixtures whose windows do not move
either way.** So a wrong-marker spawn is invisible to a closed-loop window as
well as to a percentile: of everything this project measures, one open-loop
maximum and this counter are the only things that see it.

⚠️ **A wave is still on the fragile route.** `SeedIndependentSpawnPoints` cannot
serve several slots of one team from a single list, so those keep the seed
search. Nothing shoots at a round start, which is why it holds today — and
`SpawnPlacementTests` is what will say so if a recording ever puts a wave next to
an unseeded weapon.

⚠️ **And the tick-early queueing is now properly controlled.** The first attempt
at the seam queued a spawn from the tick its message arrives on and failed twenty
tests; that could have been the missing `HasPendingSpawn` guards rather than the
timing. Re-run 2026-08-27 **with the guards in place**: same-tick queueing still
fails, on the same fixtures. The timing is the cause.

### Settled: Dual King of the Hill has a fixture, and every gamemode with a map now does

✅ **`reference-dkoth_atalia-closeddkoth.trace`** (2026-08-27): three soldiers a
side, spawn-anchored, teams forced *after* the reload. `DualKothFixtureTests`.

**It was the last gamemode with a shipped map and no recording.** ⚠️ The earlier
claim that "every gamemode has a fixture" was **wrong** — it was made after
Arena landed and missed DKotH, which has two shipped maps (`dkoth_atalia`,
`dkoth_sixties`). The claim is true now.

⚠️ ~~**Invasion has no shipped map at all**~~ — **that was wrong, and it was
wrong for an instructive reason.** It came from looking for an `inv_*` map and
finding none. **Invasion is not a map-name prefix**: it is a Capture the Flag map
that places a `ControlPointSetupGate` (`GameModes.HasSetupGate`, from
`ControlPointHUD.events/Create.xml`), and the only shipped map that does is
called **`ctf_avanti`**. ✅ Recorded the same day — see below.
**Classify a map by asking the code, never by reading its name.**

**What it gates that nothing else could:**

- ⚠️ **DKotH never sends `UNLOCKCP` and KotH always does** — the one message
  that tells the two modes apart on the wire. A KotH hill is unlocked by
  `KothHUD` calling `doEventUnlockCP`; DKotH's hills unlock themselves in their
  own Step, which runs before the HUD's, so nothing is broadcast. **That was a
  reading of the reference with no evidence behind it until now**, and the KotH
  fixture is the control that makes the negative mean something.
- ✅ **Both hills start owned by their own team**, from `DKothHUD`'s Create event
  rather than `User Event 0`. Every other mode starts its points neutral, so this
  is the one place a port could reasonably have gone the other way — and with the
  clocks crossed (you win by holding the *enemy's* hill) starting them wrong runs
  both timers backwards.
- **Two control points frame cleanly.** The block has no count prefix, so a
  reader rebuilds the points from the map in the same order or desynchronises;
  `koth_valley` proved that for one point. Controlled: parsing this recording at
  a count of 1 or 3 fails, at 2 it is clean.

### Settled: Invasion has a fixture, and its map is called `ctf_avanti`

✅ **`reference-ctf_avanti-closedinvasion.trace`** (2026-08-27): three soldiers a
side, spawn-anchored, teams forced after the reload. **Framed cleanly**, and it
is the last gamemode to get a recording. `InvasionFixtureTests`.

⚠️ **The finding is the map, not the fixture.** This project stated on the same
day that Invasion had no shipped map — reached by grepping the map list for
`inv_*`. It has one, and it is named `ctf_avanti`, because the mode is decided by
an **entity** and not by a name. `ExactlyOneShippedMapPlaysInvasion` asserts the
whole classification across all 22 maps, with the other eight `ctf_` maps as its
control, so the mistake cannot be made again quietly.

**What it holds:** three `GRAB_INTEL` and three `SCORE_INTEL` — red capturing,
which is the only way red can win, since **blue wins by surviving the clock** —
95 weapon fires, six deaths, and a sentry built and destroyed. It is also the
first recording in which a character is held behind a **setup gate**: Invasion
opens with a full minute of setup against attack/defend's thirty seconds.

⚠️ **And the same audit found a second gap.** `cp_egypt` has **no** setup gate and
**five** control points, where `cp_dirtbowl` has one and two — so they are the
*two different control-point arrangements* (symmetrical and attack/defend), and
only attack/defend had a fixture. ✅ **Recorded the same day — see below.**

### Settled: the symmetrical control points, and the first in-window meter reading

✅ **`reference-cp_egypt-closedsymmetric.trace`** (2026-08-27): three soldiers a
side, spawn-anchored, framed cleanly with **five** control points — the largest
block anywhere, and the block has no count prefix, so parsing at 4 or 6 fails and
at 5 is clean. `SymmetricControlPointFixtureTests`.

**Measured live off `cp_egypt` and now pinned:**

| | |
|---|---|
| Starting owners | red, red, **neutral**, blue, blue |
| `capTime` per point | **210, 420, 630, 420, 210** — `1,2,3,2,1 × 7 s` |
| Locked at the start | the four owned; **the neutral middle is not** |
| Attack/defend, for contrast | a `5,4,3,2,1` ramp on a six-second base |

⚠️ **The wire cannot show `locked`** — the block is team, capping team and meter
— so the neighbour rule stays unobservable and the ownership is what a recording
can hold. And ⚠️ **reading a fact off a fixture is not a gate on the port**: the
ownership is asserted twice, once against the recording and once by loading the
map into `PortedSimulation` and asking it. Only the second bites, and it is
mutation-checked.

#### ⚠️ OPEN: with trajectories exact, the port's meter is two ahead

**This is the first fixture whose control-point observation lands inside its
trajectory window.** The window is **892 ticks** (t709–t1601) and the observation
at **t1599** is inside it, where `koth_valley`'s earliest is three hundred ticks
past the end.

**The reference reads 63 and the port reads 65.** With every character exactly
where the reference put them, **drift is not available as an explanation** — and
drift is what explains every other control-point disagreement this project has
measured. Something starts the middle point's meter about two ticks early, or
counts one capper too many for two ticks.

The later observations are outside the window and prove nothing: 146 against 158
at t1688, 445 against 472 at t1969, and the reference completing the capture at
t2033 where the port is still 605 in.

✅ **Chased, and the answer is two ticks — `reference-cp_egypt-uncontestedcp.trace`.**
Six red scouts, no blue, bots steered onto the neutral middle point:
**4,617 exact ticks** and **all three post-anchor observations inside the
window**. `UncontestedControlPointFixtureTests`.

⚠️ **The port starts the capture meter TWO TICKS before the reference does**, and
the two recordings prove it is a *timing* error rather than a rate one:

| Recording | Class | `capStrength` | Reference | Port | Δ |
|---|---|---|---|---|---|
| `closedsymmetric` | soldier | 1 | 63 | 65 | **+2** |
| `uncontestedcp` | scout | **2** | 157 | 161 | **+4** |

A meter advances by the cappers' `capStrength` each tick, so a fixed head start
shows up **doubled** on a scout recording — which is what happens. A wrong *rate*
would scale with the meter, and it does not: by t2723 the two agree again,
because both have finished.

The same error seen from the other end: at t1577 the reference still reads
`cappingTeam = 0` — red is still capturing — and the port has already cleared it
to -1, having finished first.

⚠️ **Drift was not available as an explanation** — the window is 4,617 ticks and
every observation is inside it — which made it look like a simulation bug.

#### ✅ It was the harness: the capture meter was compared one tick late

**Fixed 2026-08-27, and the harness was eliminated first exactly as the note above
said to.** `Replayer` compared the control-point block **after** running the tick.
The block rides in that tick's state update, which — like the positions beside it
— was serialised in the server's Begin Step and therefore describes the **end of
the previous tick**. So the port's end-of-this-tick meter was being held against
the reference's end-of-last-tick one: **one tick of capture, read as a rule
error**.

Every other comparison in the loop already respected that rule. The character
`Reconcile` runs before `Advance`; deaths, ubers, intel and sentries are read out
of the *following* tick's messages after it. The control-point comparison was the
one that did neither.

**The order that is right, and all three parts are measured:**

| Order | `koth_valley` uncontested | `cp_egypt` |
|---|---|---|
| inject unlock → **compare** → advance | exact | **exact** |
| inject → advance → compare (the bug) | exact | +2 / +4 |
| compare → advance → inject | **7 mismatches** | exact |

⚠️ **The unlock injection must stay ahead of the comparison**, because it
re-locks and zeroes the meter — comparing first holds the port against a value
the injection is about to wipe, which is the third row.

⚠️ **And note what nearly hid it: the wrong order made `koth_valley` exact.** One
fixture agreeing is not evidence an ordering is right. It took a second
control-point recording, on a different mode, with a different `capStrength`, to
make the shape of the error legible — `+2` at strength 1 and `+4` at strength 2 is
one tick, not a rate.

Gated by `UncontestedControlPointFixtureTests.ThePortCapturesThePointExactlyAsTheReferenceDoes`,
`SymmetricControlPointFixtureTests.ThePortsMeterAgreesOnTheObservationInsideTheWindow`
and `KothCaptureFixtureTests.ThePortReproducesTheHillExactly`; moving the
comparison back fails all three.

✅ **And the uncontested recipe has now paid four times** — `koth_valley`'s hill,
`ctf_2dfort`'s capture, and both halves of this. **When an objective is too slow
to fit inside a window, remove the enemy team rather than looking for a physics
fix.** ⚠️ It is not silent, and that is worth knowing: this recording still holds
fourteen weapon fires and four deaths (a scout shooting a wall, and falls). What
buys the window is that nothing shoves anybody *else*.

### Settled: every comparison family is now pinned to its tick AND proved alive

✅ **2026-08-27**, prompted by the control-point comparison turning out to read a
tick late. If one family's alignment was wrong and no test noticed, the others
were worth auditing the same way — by mutation rather than by reading.

**Two mutations per family**: shift it a tick, and delete it (route its
mismatches to a discarded list). A family that survives either is not gated.

| Family | shift, before | delete, before | after |
|---|---|---|---|
| Deaths | 3 fail | 1 fail | gated |
| Intel | 2 fail | **0 fail** | gated |
| Sentries | 2 fail | 1 fail | gated |
| Control points | 3 fail | **0 fail** | gated |
| **Ubers** | **0 fail** | **0 fail** | gated |

⚠️ **`Fields.Ubers` was used by no test at all.** It has existed since phase 5,
carries a documented argument for why it is a strong claim, and nothing ever
passed it to a `Replayer` — so both mutations were free.
`SupportClassFixtureTests.ThePortNeverChargesAnUberEarlierThanTheReference` looks
like its gate and is not: it replays with `Fields.None` and watches the
simulation through a decorator, so the comparison never runs.

⚠️ **And the deeper hole: "no mismatches" is satisfied by a comparison that never
runs.** Three of the five families asserted exactly that and nothing else, so
deleting the comparison was free even where a shift was caught. **Every
`Assert(mismatches.Length == 0)` needs a liveness control beside it** — a second
replay against a doctored trace, with the messages stripped or the blocks
altered, asserting the comparison *does* report a difference. All five have one
now.

⚠️ **Note what this is not.** A liveness control proves the comparison runs and
can report; it does not prove the fixture reaches the behaviour. Those are two
different holes and this project has now fallen into both — the first here, the
second in `KothFixtureTests`, whose observations all fell outside its window.

**The uber gate's own trade**, worth keeping: whether `closedsupport`'s medic
lives long enough to charge is decided by unseeded pyro flames, so a *missing*
uber is not evidence of anything. The gate asserts both directions anyway,
because the weak version does not catch deletion — with the remedy written into
the failure message, so a future session that hits it knows to weaken rather than
chase.

### Settled: ammo and flags are compared at last, and they were never compared before

✅ **2026-08-27**, and found by census rather than suspicion. After
`Fields.Ubers` turned out to be used by no test, the obvious follow-up was to
count which members of `Fields` any test passes to a `Replayer`:

```
34 Fields.Trajectory   10 Fields.Deaths    5 Fields.Intel          4 Fields.Sentries
21 Fields.Hp            7 Fields.Ubers     5 Fields.ControlPoints  2 Fields.Position
```

⚠️ **`Fields.Ammo` and `Fields.Flags` appear nowhere.** Every phase has
serialised both and nothing has ever held the port to either. They are not
decorative: **ammo** is the whole of the magazine accounting on the wire — refire,
reload, the pyro's tank resting at 201.8 and the heavy's at 201 — and **flags**
carries the cloak bit and `moveStatus`, so a rocket jump and a cloaked spy are
visible in it and nowhere else.

**Both are now gated, and both were already correct:**

| Gate | Fixture | Window | Why that fixture |
|---|---|---|---|
| Ammo | `closedcombat` | **1,325** ticks, zero mismatches | scout/soldier/demoman — **every weapon broadcasts a seed**, so no excuse is available |
| Flags | `closedspy` | **1,829** ticks, zero mismatches | the only recording in which anybody cloaks |

Mutation-checked against real regressions, not just against the harness: making a
scattergun shot cost two ammo fails two tests, and suppressing the cloak bit in
`CharacterWire` fails two. Each gate also carries a **liveness control** —
a replay against blocks whose ammo and flags are XORed — because
`Assert(mismatches.Length == 0)` is satisfied by a comparison that never runs.

⚠️ **And a third field falls under the unseeded-weapon rule.** It was established
for hp and then for trajectory; `closedheavy` shows it for ammo. Its Minigun
calls `randomize()` and broadcasts no seed, so who shoots and how much they spend
cannot be reproduced: **ammo 760 ticks against flags 1,744**, same recording, same
anchor. Asserted, because it is the cheapest demonstration that the ammo
comparison is sensitive to something real — if the two ever converge, either the
Minigun became reproducible, which it cannot, or the comparison stopped working.

⚠️ **The general lesson, now paid for twice: census the seams.** Two rounds of
"which of these does any test actually use?" found three unexercised comparisons
(`Ubers`, `Ammo`, `Flags`) that had existed for phases. A seam that is never
called is indistinguishable from one that is wrong.

### Settled: which message types the corpus reaches, and four that cannot exist

✅ **2026-08-27**, the third and largest application of "census the seams". The
question that has found every framing bug here is **"what state can no recording
reach?"**, and `MessageCoverageTests` makes it mechanical instead of a matter of
memory: it walks all 27 fixtures and pins the set of message types they contain.

**Thirty types are reached.** The census also settles the other 27, and they fall
into three groups:

⚠️ **Four are DEAD PROTOCOL and can never appear on any wire** — declared in the
reference's `Constants.xml` and referenced **nowhere** in its scripts or object
events, measured by grepping the tree:

| | |
|---|---|
| `RED_TEAM_CAP` | 12 |
| `BLUE_TEAM_CAP` | 13 |
| `CP_CAPTURED` | 30 |
| `SERVER_KICK` | 37 |

⚠️ **`CP_CAPTURED` is the one that looks like an oversight and is not.** This
corpus captures control points on `koth_valley`, `cp_egypt` and `dkoth_atalia`
and none of them sends it: **a capture reaches the wire as a `CAPS_UPDATE` and
nothing else.** Asserted, so nobody spends a day trying to record one.
(⚠️ Two spellings differ from the port's names and cost a first pass:
`KICK_MULTI_CLIENT` and `ARENA_WAIT_FOR_PLAYERS` **are** used — grep the constant
name from `Constants.xml`, not the C# member name.)

**Most of the rest are structural**: the handshake (`HELLO`, `RESERVE_SLOT`,
`PASSWORD_REQUEST`, `PASSWORD_WRONG`, `INCOMPATIBLE_PROTOCOL`, `SERVER_FULL`,
`DOWNLOAD_MAP`) happens before `gg2-parity record` starts writing ticks, and the
kick, plugin and reward families need an operator, a plugin or the medal server.

✅ **Five are reachable and simply unreached — the candidates for the next
fixture**: `DROP_INTEL` (an ubered carrier dropping, or a client's own drop
command — a death-caused drop broadcasts nothing), `ARENA_WAIT_FOR_PLAYERS`,
`CHAT_BUBBLE`, `MESSAGE_STRING` and `PLAYER_CHANGENAME`. **Only the first two are
gameplay.**

⚠️ **The census asserts both directions, and the second is the one that will
matter.** Several types are held by **exactly one fixture** — `Balance`,
`ArenaEndRound`, `GeneratorDestroy`, `UnlockCp`, `Uber` — so a re-recording that
came out quiet would silently stop exercising a whole message family. Adding a
type fails with "good news, add it here"; losing one fails with "a recording has
stopped exercising a family".

### Settled: `DROP_INTEL` is reached, and it is a client command like a sentry

✅ **`reference-ctf_2dfort-dropintel.trace`** (2026-08-27): six red scouts, no
blue, steered to blue's briefcase, and the carrier then made to drop it. The last
*gameplay* message the corpus could reach and did not.
`DropIntelFixtureTests`.

**How, and why it is the reference's own behaviour**: `processClientCommands.gml`
handles a client's `DROP_INTEL` with two calls behind one guard —
`sendEventDropIntel(player); doEventDropIntel(player);` — and those two lines were
invoked over the bridge for the carrying bot. A driven client pressing drop
reaches the identical pair; what is bypassed is only the client-to-server half,
and a recording is of the server's broadcast.

⚠️ **A death-caused drop broadcasts nothing** — `sendEventDropIntel.gml`'s own
docstring says the event is skipped on death "to avoid issues with order of
execution" — which is why no earlier fixture had one despite dozens of deaths
with the flag in play.

✅ **`ITraceSimulation.DropIntel` is the new seam.** A `DROP_INTEL` has to be
**injected**, like a `BUILD_SENTRY` and a `PLAYER_SPAWN`: nothing in an
`INPUTSTATE` says the player pressed drop, so a replay cannot derive it, and a
port that ignored the message would carry the flag on for ever while the
reference put it down.

⚠️ **It is applied BEFORE the tick — the opposite of a grab.**
`processClientCommands` runs inside the server's Begin Step, ahead of
`serializeState`, so the message lands in its *own* tick's block; a `GRAB_INTEL`
comes out of the collision stage, after the update, and lands in the next one's.
**The two halves of the same objective use opposite tick conventions.**

#### Two traps, both of which cost an attempt

⚠️ **`message.PlayerId` is null for this whole message family.** `ServerStream`
frames `DROP_INTEL`, `GRAB_INTEL`, `SCORE_INTEL`, `UBER`, `OMNOMNOMNOM` and the
rest through `Fixed()`, which *skips* the body without decoding it — so a
`case ... when message.PlayerId is { } slot` guard **silently never matches** and
the injection does nothing. Read `Payload[0]`. The test's first assertion is now
that the seam was reached at all, precisely to catch that shape of failure.

⚠️ **A drop can be followed by an instant re-grab, and that is correct.** The
first version of the gate asserted "nobody carries the flag afterwards" and
failed: slot 5 dropped it and **slot 2 had it by the end of the same tick**. A
flag taken off its base gives its carrier a return timer of 0, a drop writes that
straight into the new instance's `alarm[0]`, and the grab guard's `alarm[0] <= 1`
arm lets anyone pick it straight back up (docs/SIMULATION.md, "Settled: the
intel"). The recording corroborates it — that re-grab is the second of its two
`GRAB_INTEL`s. **Assert on the named slot, not on the flag.**

### Settled: the corpus had two client commands in it that the replayer threw away

✅ **2026-08-28.** The reference has exactly **three** commands that are not key
bits — `Scripts/Bots/botServerActions.gml` names them in one line, *"Zoom, build
and eat are client commands"* — and `processClientCommands.gml` handles all three
in adjacent cases (212 `BUILD_SENTRY`, 232 `OMNOMNOMNOM`, 236 `TOGGLE_ZOOM`),
each broadcasting at the moment the server accepts it.

⚠️ **The replayer injected one of the three, for nine phases.** `BUILD_SENTRY`
was wired up when the sentry landed and `DROP_INTEL` beside it; `TOGGLE_ZOOM` and
`OMNOMNOMNOM` never were — while their messages sat in the checked-in corpus the
whole time: **31 zooms across four fixtures and four eats across two**
(`gg2-parity census`).

**Both are physics, not HUD flags:**

| | what the reference does | where |
|---|---|---|
| `TOGGLE_ZOOM` | `runPower = 0.6`, `jumpStrength = 6 + 0.6/2` | `Scripts/Misc/toggleZoom.gml` |
| `OMNOMNOMNOM` | freezes acceleration and facing for 128 ticks, heals 1.6 hp a tick | `Character.events/End Step.xml:83-96` |

So a replay that dropped them walked a scoped sniper at the wrong acceleration,
jumped it at the wrong strength, and let an eating heavy accelerate while the
reference's stood still.

✅ **What it was worth, measured both ways** — the fixtures were replayed with
the injection on and off in the same session:

| fixture | open-loop p99 x/y, off | on | closed-loop exact window |
|---|---|---|---|
| `ctf_truefort-closedsniper` | 25 / 20 | 25 / **3** | **362 → 1,630 ticks** |
| `ctf_truefort-closedheavy` | 49 / 3 | **15** / 2 | 396, unchanged |
| `gen_destroy-rotation` | 7 / 10 | **6** / 10 | n/a |
| `cp_egypt-uncontestedcp` | 5 / 1 | **6 / 2** | 4,617, unchanged |
| `cp_egypt-closedsymmetric` (control, neither message) | 6 / 2 | 6 / 2 | unchanged |

⚠️ **The sniper's 362-tick window was being explained by the wrong thing, in a
comment, for two sessions.** `NewClassFixtureTests` said the heavy's and the
sniper's short windows were "the weapons, not the classes — an unseeded Minigun's
spread cannot be reproduced". That is true of the heavy and was never true of the
sniper: the Rifle is hitscan and the comparison is trajectory-only. What ended the
window was **t923's unapplied zoom**. **A plausible explanation that covers two
rows will happily hide the one row it does not fit.**

⚠️ **One row moved the wrong way and it is not a regression.**
`cp_egypt-uncontestedcp` went 5/1 → 6/2 when its three zooms started being
applied — but **its maxima did not move at all** (53 and 111 either way), so no
new outlier appeared; the distribution shifted by one wire unit, 0.2 px, on a
fixture whose sniper then stands scoped for thousands of ticks. The control
fixture is identical either way, which is what says the change reaches only what
it should.

⚠️ **One recorded eat is refused, and it is a bootstrap artefact.**
`closedheavy`'s first `OMNOMNOMNOM` is in the **t0** block: slot 5's heavy was
already alive and already eating when the recorder connected, so the replay has no
character to feed. ✅ **Deferring the injection until after the anchor was tried
and does not help** — the anchor resets characters, it does not create them. Same
limit as "a recording of players who were already alive is open-loop only".
`ClientCommandInjectionTests` pins the refusal at exactly one, so a *second* one —
which would be a real disagreement about the sandvich's 45-second cooldown —
fails.

⚠️ **The general lesson, and it is the third time this project has paid for it:**
`MessageCoverageTests` asks *"what state can no recording reach?"*. This was the
inverse — **a recording reached it and the harness discarded it** — and nothing in
the suite asked that question. `ClientCommandInjectionTests` now counts the
injections in both directions: the fixtures still contain 31 zooms and 4 eats, and
every one is accepted.

### Settled: `ARENA_WAIT_FOR_PLAYERS`, and every gameplay message is now recorded

✅ **`reference-arena_lumberyard-waitforplayers.trace`** (2026-08-27): three
soldiers a side, with blue removed while the scoreboard was up.
`ArenaWaitFixtureTests`. **With it, no gameplay message the reference can send is
left unreached by the corpus** — what remains is handshake, admin, plugin and
reward traffic, plus the four dead constants.

**The recipe comes from the branch.** `ArenaHUD.events/Step.xml` decides what
happens when a scoreboard runs out in a fixed order: the cap limit, the clock,
then `redteam == 0 or blueteam == 0` → wait for players, and only otherwise
`serverArenaRestart()`. ⚠️ **So it needs a round to end with one team having no
PLAYERS** — not merely no living characters, which is what wiping a team out
gives and what every earlier arena round produced. Removing blue's bots during
the scoreboard is the reference's own path: a server whose blue team all
disconnect between rounds takes exactly this branch.

**What it gates:** the message is **bodyless** — asserted against its two
bodyless siblings and, as the control, against `ARENA_ENDROUND` in the same
stream, which is not and was framed as though it were for a phase. And the last
round **ends into a wait rather than a restart**, with no restart after it, which
is the exclusive branch rather than the message merely existing.

### Settled: a mode change is invisible unless a HUD-bearing update follows it

✅ **`reference-gen_destroy-rotation.trace`**, 2026-08-27: `gen_destroy`
rotating to **`ctf_2dfort`** at t2622 — the first recording in the project that
changes to a **different map in a different gamemode** mid-stream, with 669
ticks of play after it. `MapRotationFixtureTests`.

**Two things had to be arranged, and both are worth knowing:**

- ⚠️ **`MapRotation` in the ini is a FILE PATH, not a list**
  (`game_init.gml:94`). With no file the server's rotation held a single entry —
  the current map — which is why `reference-gen_destroy-closedgen.trace` rotated
  back to itself. The list was extended over the bridge.
- ⚠️ **Red winning does not rotate.** `GameServerBeginStep.gml:107`: if red wins
  and the map has another area it advances the *area* and keeps the same map.
  Only the `else` arm calls `nextMapInRotation()`, so **blue has to win**.

⚠️ **The reader survived the mode change — and the reason is the finding.**
`TraceParser` takes **one shape for the whole trace**, and the two shapes are
genuinely incompatible: `Generator`'s HUD is **thirteen** bytes and
`CaptureTheFlag`'s is **five**, and parsing this trace with the CTF shape fails
at byte 432. It framed anyway because:

> **Only `FULL_UPDATE` and `CAPS_UPDATE` carry the HUD block.**
> `StateUpdateReader` returns before the tail on every other tier, so an
> `INPUTSTATE` and a `QUICK_UPDATE` never read it.

In this recording the caps updates land at **t389, t2188, t2189 — all before the
rotation** — and the only full update is the join at t0. The 95 quick updates
after the change read no HUD at all.

**So the wrong-shape risk is real but latent**: it needs a `CAPS_UPDATE` or
`FULL_UPDATE` *after* a mode change, which means a capture, a join, or a
scoreboard sync on the new map.
`MapRotationFixtureTests.NothingAfterTheRotationReadsTheHudBlock` asserts the
absence so it stops being true loudly.

#### ✅ Reached, and fixed, the same day

**`reference-gen_destroy-rotationcaps.trace`** is the same rotation with a
`CAPS_UPDATE` forced *after* the change (`GameServer.syncTimer = 1`, which is
exactly what a capture does). **It does not frame**: a single shape stops at
**byte 79,467 of 124,340**, discarding two thirds of the file — the
thirteen-byte Generator HUD read out of a five-byte Capture the Flag one.

✅ **`TraceParser.Parse` takes an optional `Func<string, WorldShape>` and
re-shapes on `CHANGE_MAP`.** With it the recording frames whole, 3,483
messages. `gg2-parity`'s census and replay and `FixtureFramingTests` all pass a
resolver now; passing null keeps the old single-shape behaviour, which is
correct for every recording that never changes map.
`ModeChangeFramingTests` is the gate, and its first test is the control — it
asserts that a single shape *cannot* frame this recording, so the fix cannot be
reverted unnoticed.

⚠️ **The census's message counts were wrong before the fix, not merely
truncated**: it reported 2 `CAPS_UPDATE`s where there are **4**. A desynchronised
reader does not just stop — everything it reported up to that point is the only
part worth trusting.

✅ **And the ten-second map change is confirmed a second time**, on the other
branch: 300 ticks from `MAP_END` to `CHANGE_MAP`, with blue winning and the map
actually changing, where `GeneratorFixtureTests` measured it with red winning
and the same map reloading.

### Settled: the generator block does not shorten, and probably cannot

✅ **`reference-gen_destroy-closedgen.trace`**, 2026-08-27: three soldiers a
side on the only shipped generator map, 3,297 ticks, spawn-anchored, framed
cleanly. Both generators took real bot damage (to 1846 and 1755 of 2100) before
blue's was finished off over the bridge at t2027. **408-tick** window on
`Trajectory｜Hp｜Deaths`. `GeneratorFixtureTests`.

**Four things reach a fixture for the first time**: a `GENERATOR_DESTROY`, a
`MAP_END`, a `CHANGE_MAP` that is not the recorder's own join, and the generator
HUD block.

⚠️ **The headline is a negative that contradicts the standing risk.**
docs/PROTOCOL.md records that the block carries one record per *surviving*
generator, so a destruction shortens every later update by four bytes — making
`WorldShape.GeneratorCount` the one shape field a map cannot settle for good and
requiring a reader to decrement it. **Nothing in `ServerStream` decrements it,
and this recording shows it does not have to.**

**Measured:** the wire carries **two** records for the whole recording,
including the 300 ticks between the destruction and the map change.

| `GeneratorCount` | result |
|---|---|
| 1 (a reader that decremented) | desync at byte 436 |
| **2** | **clean to the last byte** |
| 3 | desync at byte 444 |

Those two controls matter: without them "it framed" would be equally consistent
with a reader that never looked at the block.

⚠️ **Why, and why it may be unreachable:** destroying a generator ends the round
immediately (t2027, `MAP_END` at t2028) and **a round ending does not sweep the
map** — the dead generator's instance survives the scoreboard, so the HUD still
writes it. On `gen_destroy` each team has exactly one generator, so destroying
one *always* ends the round. **A custom map with two per team could still reach
the shortened block; no shipped one can.**

**Two more numbers, measured rather than read:**

- ⚠️ **`GENERATOR_DESTROY` names the team that LOST one** — the payload is
  team 1 (blue, whose generator died) and the `MAP_END` a tick later names red
  as the winner. `sendEventGeneratorDestroy.gml`'s own docstring says the
  opposite; the pair of messages settles it.
- ✅ **The map changes exactly 300 ticks after `MAP_END`** — the documented ten
  seconds, now measured.

⚠️ **The rotation goes back to the same map, so the wire shape never changes.**
`TraceParser` takes **one shape for the whole trace**, so a rotation to a map of
a different mode would frame the rest of the recording wrongly. Still unreached.

### Settled: the first multi-area fixture, and the reader bug it found

⚠️ **`reference-cp_dirtbowl-closedcp.trace`**, 2026-08-27: three soldiers a
side, 3,585 ticks, spawn-anchored, attack/defend. **It did not frame at all on
its first outing**, which is what it was recorded to find.
`CpDirtbowlFixtureTests`.

**A tall map is several rounds stacked vertically and only one area's entities
exist.** `cp_dirtbowl` writes **six** control points and plays **two**. The HUD
block carries **no count prefix**, so a reader framing by the wrong number
desynchronises on the very first update:

| shape from | `ControlPointCount` | framed |
|---|---|---|
| the map | 6 | **458 of 138,406 bytes**, 137,948 silently dropped |
| `map.ForArea(1)` | 2 | **all of it**, 3,585 ticks |

⚠️ **`PortedSimulation.LoadMap` had narrowed since phase 6** and carried a
comment saying it "would need the recording's own currentMapArea if one were
ever made on cp_dirtbowl". One has been now — **and the gap was not there.** It
was in every *reader*: `gg2-parity`'s three `ShapeOf` call sites (census, replay,
diff) and `FixtureFramingTests` all passed the un-narrowed map. All four fixed.

**The window is 905 ticks** on `Trajectory | Hp | Deaths` (t417 → t1322), with 85
weapon fires and ten deaths — which is also the first evidence that `ForArea`
produces the right *world* and not merely the right block count: a character on
the wrong area's terrain would be somewhere else entirely.

⚠️ **Narrowing is a no-op for every single-area map**, which is every other
fixture — so this could only ever have been caught by a recording on a map like
this one. `CpDirtbowlFixtureTests.TheUnNarrowedShapeCannotFrameThisRecording` is
the control that keeps the fix honest: without it the narrowing could be
reverted and every other test in that file would still pass.

⚠️ **Still not handled: an area that advances mid-recording.** Area 1 is right
for a fresh round, and attack/defend advances the area when the attackers win —
which would change the control-point count underneath the reader, the same shape
as the generator's self-shortening HUD block. No recording reaches it yet.

### Settled: the first Team Deathmatch fixtures

✅ **`reference-tdm_mantic-closedtdm.trace`** and
**`reference-gg_debug-closedtdm.trace`**, both 2026-08-27, six soldiers, both
spawn-anchored, **both framed cleanly** and both reported as `TeamDeathmatch` by
census. Until then every fixture was Capture the Flag, so `GameModes.ShapeOf`
had never produced a TDM shape for a real reader. **No framing bug** — the same
class of negative the four single-class recordings bought.

- ⚠️ **`gg_debug` is the busiest recording in the project**: 52 deaths, 59
  spawns and 262 seeded weapon fires in a hundred seconds, and the first
  recorded evidence that Team Deathmatch scores at all. **None of it is
  assertable yet** — the window is two ticks — which is exactly why the landing
  finding above is worth fixing.
- ⚠️ **`tdm_mantic` was chosen because it is the only shipped map placing pit
  falls (six) or move boxes (fifteen), and it gates neither.** Checked by
  mutation: commenting out `MoveBoxes.CollisionStep` leaves the window at 3,043
  ticks, so no bot ever rode one; and with no deaths there were no pit falls.
  **A recording existing on the right map is not the same as it reaching the
  behaviour** — ask which of the map's features the bots actually touched.

**The recording is kept rather than re-taken.** It holds eight deaths, sixteen
spawns, and a `GRAB_INTEL` with the `SCORE_INTEL` that follows it — **the first
capture-with-its-grab in any fixture in this project**, so `doEventScoreIntel`
finally has recorded evidence to be gated against. None of it is assertable
while the window is five ticks; all of it becomes assertable the moment the
landing agrees.

⚠️ **A recording can never gate respawn *timing* closed-loop**, whatever the
window. `Replayer` **injects** each `PLAYER_SPAWN` and `PortedSimulation` keeps
`RespawnTime = 0` so the port never respawns on its own — deliberately, so the
replay does not race the recording with spawn-point picks of its own. What a
death-and-respawn inside a window buys is that the port reproduces the *death*
on the tick and the trajectory survives the character being cleared and
replaced. `RespawnTimingTests` measures the **reference's** 150-tick gaps; the
port's respawn counter is gated by unit tests alone.

### Settled: a recording of players who were already alive is open-loop only

**A closed-loop replay needs an anchor that never passed through the wire.** If
every character in a recording was alive before it started, there is no
`PLAYER_SPAWN` to resolve and the replayer has nothing to anchor on but a
broadcast `QUICK_UPDATE` — a fifth of a pixel of position *and* of velocity,
followed by up to seven ticks of free simulation before the first comparison.
That is enough on its own to lose exactness, and GG2's physics amplifies it: a
rounded position flips a `place_free`, and a discrete decision turns a fifth of
a pixel into a whole one.

This is **measured, not argued**.
`PhysicsParityTests.AnchoringOnTheWireInsteadOfASpawnPointBreaksAnExactTrace`
replays the 898-tick bot recording twice with the same simulation and the same
comparisons, changing only whether `SpawnCharacter` resolves. From spawn points
it is exact; from the wire it is not.

So a recording is one of two kinds, and it is worth knowing which before
spending a day on its residual:

| | anchor | replay mode | what a report means |
|---|---|---|---|
| everyone spawns during the recording | `PLAYER_SPAWN` → exact integer | **closed loop** | any mismatch is a bug |
| someone was alive at the start | quantised `QUICK_UPDATE` | **open loop only** | a *bound* on drift over the gap |

`reference-ctf_truefort-bots.trace` is the first kind. `census` shows
`reference-ctf_truefort-combat.trace` is the second — six bots, zero
`PLAYER_SPAWN`s, six resyncs on the first update — which is the whole
explanation for its "divergence at tick 9". No amount of correctness will make
it exact, and it should never be asserted to be.

**The line between the two kinds is now one shared derivation** —
`tests/Gg2.Tests/FixtureAnchors.LastExactAnchor`, unified 2026-08-26 after two
versions coexisted and agreed only by luck. The anchor is **the latest
first-`PLAYER_SPAWN` over the slots the recording observes**, with one
admission: a slot that never spawns poisons the recording *only if it is still
observed after that anchor*. `closedcombat`'s slot 6 is why the admission
exists — a pre-reload ghost, last observed at t21, 781 ticks before the t917
anchor, that the strict rule refused outright. The retired alternative,
"every spawn within 200 ticks of the first", got `closedsupport` wrong (its
wave is staggered across five hundred ticks) and would have silently included
a respawn that landed early. `FixtureAnchorsTests` pins all three branches on
synthetic ticks, including the refusal no checked-in fixture reaches.

### How to record a fixture that can be replayed closed-loop

Everything above says why a recording of players who were already alive can only
ever be an open-loop bound. **The fix is to make them spawn inside the
recording**, and the way to do that is to reload the map underneath a recorder
that is already connected:

1. Start the dedicated server with bots and the class limits (recipe above).
2. Wait for the bots to be up, then start `gg2-parity record`.
3. Once the recorder has joined — `instance_number(Player)` goes up — reload the
   map **over the bridge**: `gg2_eval: serverGotoMap("<the same map>")`.
   The same map, so the trace header and the actual map agree and nothing has to
   reason about a mid-trace change of geometry.
4. Record long enough for the bots to leave their bases and fight: 75 seconds
   gives ~2,240 ticks, of which the last ~1,400 are after the spawn wave.

`Replayer` handles the `CHANGE_MAP` — `PortedSimulation.LoadMap` resets the whole
world — and the `PLAYER_SPAWN`s that follow give every character an exact integer
anchor. So do the projectiles they then fire, which is what the section below is
about.

**The result on `reference-ctf_truefort-closedcombat.trace`:** from the end of
the spawn wave the port reproduces the reference **exactly** for **335 ticks** —
eleven seconds containing nine weapon fires and a death — across position,
velocity, hp and death attribution. That is the first closed-loop gate the combat
work has ever had, and it is far sharper than a bound: removing the `Shot`'s
gravity shortens the window from 335 to 266, and disabling the Rocketlauncher
takes it to 203.

⚠️ **The ticks before the spawn wave are not assertable** and the test does not
try. The recorder joins a match in progress, so until the reload those characters
are wire-anchored and diverge exactly as every other fixture's do.

⚠️ **That was written when the window ended at 335 ticks and the first
divergence was 2 hp. Both of its ends have since been chased and neither is a
bug** — see "an exact window can end on a rounding boundary" and "...can also end
on a threshold" below. Do not assume a closed-loop residual is arithmetic just
because there is no anchor quantisation left to blame; probe and sweep first.

### Settled: what an anchor cannot reach — projectiles

**An open-loop replay resynchronises characters and nothing else.** Every seventh
tick a `QUICK_UPDATE` puts every character back where the reference has it. A
projectile's position is on the wire **only in a `FULL_UPDATE`** —
`Character.events/User Event 12.xml` puts `with(currentWeapon) event_user(12)`
inside the `if(global.updateType == FULL_UPDATE)` branch — and a recording
contains exactly one of those, on join.

For most projectiles that does not matter. A `Shot` lives about 35 ticks, so it
is created, flies and dies well inside the error a resync would have corrected.
⚠️ **A mine lives until somebody detonates it**, so its error has no bound — and
if the port loses one the reference still has, no later message can put it back.

On `reference-ctf_truefort-hotcombat.trace` that shows up as **89 hp of damage
the port never deals**, concentrated in two windows. The evidence that this is
the anchor's limit and not a weapon bug:

- **Every shot the reference took, the port takes.** 21 `WEAPON_FIRE` messages,
  21 seeds supplied, 21 shots fired, none unused.
- **The weapons do land.** Disabling the Minegun alone takes the unaccounted
  damage from 89 to **201** and the missed deaths from one to three.
- **At the ticks it misses badly it has nothing in the air at all.** At tick 339
  the reference deals 42 hp and the port holds zero mines, zero rockets and zero
  shots — so this is not a shot that missed, it is a projectile that no longer
  exists. `PortedSimulation.LiveProjectiles` is the diagnostic.

The small residual is a different thing and is expected: splash damage is
`explosionDamage * (1 - distance/radius)`, so a character anchored a fifth of a
pixel away takes one or two hp more or less. Five of the seven hp mismatches are
±1 or ±2.

⚠️ **So do not chase an hp residual around a demoman.** The tests assert a
*bound* on it — `ThePortTracksARealFightWithinABound` and
`AProjectileIsStateTheAnchorCannotReach` — chosen to separate the known limit
from a weapon regression, and they trip when a weapon is switched off.

Fixing it properly would need a recording of `FULL_UPDATE`s, which is not what
the server sends. The alternative is a closed-loop fixture where everyone spawns
during the recording — then the projectiles start from an exact anchor too, and
nothing needs correcting.

### Settled: an open-loop residual is measured in discrete events, not pixels

**Before blaming the physics for an open-loop residual, check whether its
characteristic size is one discrete event.** On
`reference-ctf_truefort-hotcombat.trace` the p99 vertical error is **30 wire
units — exactly six pixels — which is exactly one stair step**. That is not a
coincidence and it is not a physics error: it is a character landing one tick
earlier or later than the reference and therefore being one step further up a
slope when the next comparison lands.

The chain is short and every link is unavoidable:

1. The anchor quantises velocity to a wire byte, `1/8.5` of a pixel per tick.
2. Over seven ticks of fall that is worth a fraction of a pixel of position.
3. A fraction of a pixel decides whether `place_free` reports ground *this*
   tick or the next — a **discrete** answer.
4. Landing a tick early puts the character a whole stair step ahead, and a stair
   step is six pixels.

**The test that tells quantisation from a bug is to nudge inside the bin.**
`PortedSimulation.Nudges` exists for this. Sweeping the anchor's `vspeed` across
its own quantisation width (±0.058 px/tick, half a wire unit) on that fixture:

| vspeed nudge | \|dy\| max | \|dy\| p99 |
|---|---|---|
| -0.058 | 263 | 109 |
| -0.030 | 263 | 108 |
| **0.000** | **75** | **30** |
| +0.030 | 155 | 125 |
| +0.058 | 154 | 94 |

Two things follow. The residual is **acutely sensitive to sub-wire-unit
velocity**, which is the signature of a discrete amplifier rather than of
accumulated arithmetic error — and the bin centre is the *best* of the five,
which is what you expect when the port is right and the anchor's estimate is
unbiased. The same sweep over `y` (±0.099 px, half a position unit) moves
`|dy| max` not at all.

⚠️ So an open-loop bound on a fixture like this is near its floor, and tightening
it is not a goal. What such a fixture is good for is **catching things that make
it much worse** — disabling the Rocketlauncher takes that `|dy|` from 75 to 240.

### Settled: an anchor must not touch the input history

⚠️ **`PortedSimulation.SetCharacter` used to anchor `LastKeyState` from the
update's `keys`, and that silently ate one key press in seven.**

Jump, the demoman's right-click and the spy's cloak are all edge-detected:
`KeyState & ~LastKeyState`. A state update carries **this tick's `keys`
alongside last tick's position** — that is the ordering `SIMULATION.md` settles
under the tick order — so anchoring `LastKeyState` from it hands the character a
key it is about to be told about as though it had already been holding it. The
edge is then computed as empty and the press never happens.

It only bites when a key is *first* pressed on an update tick, which is one tick
in seven, and only for the three edge-detected keys. Every fixture before the
combat trace hid it, because a player who holds jump for several ticks does not
care that the first tick's edge was lost.

Measured on `reference-ctf_truefort-combat.trace`, whose demoman presses Jump for
exactly one tick — tick 9, an update tick:

| | max \|dy\| | mismatches |
|---|---|---|
| anchoring `LastKeyState` | **217** wire units — 43 px, the whole jump | 270 |
| leaving it alone | 20 | 256 |

**The replay's own input history is the authority.** It has seen every
`INPUTSTATE`; the update's `keys` is a snapshot of the same thing arriving a tick
later. `SetCharacter` still sets both on a character it is seeing for the *first*
time, because there is no history to preserve then — and that first anchor can
still swallow an edge, which is one more reason a recording of players who were
already alive is a weaker fixture than one where they spawn.

Gated twice: `PhysicsParityTests.AnAnchorDoesNotSwallowAFreshKeyPress` drives a
`PortedSimulation` directly, and
`APlayerLeavingDoesNotShiftTheComparisonOntoTheirNeighbour` carries the
trace-level `|dy|` bound.

⚠️ **The general rule this is an instance of: an anchor exists to correct the
things the wire measures, and nothing else.** Position, velocity, hp, ammo — yes.
Anything the replay derives for itself from the message stream — the input edge
here, and the same argument would apply to a timer or a queued event — must be
left alone, because the wire's copy of it is a tick out of date by construction.

### Settled: `PLAYER_LEAVE` compacts the roster, and a replay has to as well

⚠️ **This was wrong in `Replayer` and shipped**, because no fixture contained a
departure. The handler called `ClearCharacter` — the *death* semantics — where
the reference compacts: a roster index is positional, so a player leaving
renumbers everybody above them, and the server's own state updates are already
compacted by the time a recorder sees them. A replay that merely clears the slot
compares its slot 3 against the recording's slot 3 from the next update onwards,
while the two name different players.

The size of it, on `reference-ctf_truefort-combat.trace`, whose medic leaves on
tick 13:

| | max \|dx\| |
|---|---|
| `ClearCharacter` | **22,649** wire units — 4,530 px, a character confused with somebody across the map |
| `RemoveSlot` | 34 |

`ITraceSimulation.RemoveSlot` is the interface half and
`Gg2.Simulation.WorldState.RemoveSlot` does the work — compacting characters,
weapons and sentries, and renumbering every stored roster index. A harness that
keeps its own per-slot maps outside the state (`PortedSimulation` holds a
player's class and team there, because both outlive their character) has to shift
those by hand.

`PhysicsParityTests.APlayerLeavingDoesNotShiftTheComparisonOntoTheirNeighbour`
is the gate, and it is an open-loop bound rather than an exactness claim for the
reason in the section above.

⚠️ **CLAUDE.md flags this class of bug as intermittent and it is worse than
that** — it is invisible until a fixture contains a departure, and then it looks
like a physics catastrophe rather than an indexing mistake. When adding a
fixture, ask what state it cannot reach.

### Settled: an exact window can end on a rounding boundary rather than on a bug

**A closed-loop replay reports the first tick on which the port and the
reference disagree about an integer, and where that tick falls is partly luck.**
An observation is the reference's own position rounded to a fifth of a pixel
(`WireQuantise`, half away from zero), so the port agrees about the integer
exactly when its own `x * 5` lands within half a unit of it. A port a
ten-thousandth of a unit past the boundary reports a mismatch; the same port a
ten-thousandth the other side reports nothing, for hundreds more ticks.

That is not hypothetical. On `reference-ctf_truefort-closedcombat.trace` the
exact window ended at **tick 1274**, and a session was queued to go and find the
bug. There is none:

| | wire units |
|---|---|
| port, slot 1, `x * 5` | 14125.5014 |
| the rounding boundary | 14125.5 |
| the reference reported | 14125 |
| **the disagreement** | **0.0014 — 0.0003 world pixels** |

and the replay is clean again from t1281 to **t1673**, where the first real
divergence is an overshoot of **12.7 wire units**. There is nothing in between.

**`Gg2.Parity.SubWireProbe` is how to ask.** It is an `ITraceSimulation`
decorator over `PortedSimulation` that hooks `TryGetCharacter` — the call the
replayer makes once per compared slot per tick and nowhere else — and records
`port * 5 - observed` for position and `port * 8.5 - observed` for velocity,
without rounding. Two things follow from what it reports:

- **A port that reproduced the reference exactly would not report zeroes.** It
  would report the reference's own discarded fraction, spread across
  `[-0.5, 0.5]`. Seeing that spread *is* the evidence of tracking; a column of
  zeroes would mean the harness was comparing something with itself.
- **At a tolerance of 0.5 the probe reproduces the integer comparison exactly**,
  because in-the-bin and integer-exact are the same statement.
  `PhysicsParityTests.TheSubWireProbeAgreesWithTheIntegerComparison` asserts
  that on both closed-loop fixtures, and it is the gate on the probe's own tick
  cursor: the replayer compares *before* it advances, so a cursor off by one
  matches every sample against the wrong observation. Both directions were
  mutated and both fail it.

**Above 0.5 it is a different measure, and the point of it is that it does not
move when a residual grazes a boundary.** At a tolerance of one wire unit the
same fixture gives **756 ticks** — t917 to t1673, fourteen weapon fires, a death
and a respawn across five characters — against the exact window's 357.
`PhysicsParityTests.ThePortStaysWithinAWireUnitOfARealFight`.

⚠️ **It is not strictly stronger than the exact window and must not replace
it.** A rounding boundary is the one place an integer comparison is *more*
sensitive than any tolerance, and that sensitivity is worth keeping. Measured,
by mutation:

| mutation | exact window | within one wire unit |
|---|---|---|
| none | 357 | 756 |
| gravity as the plain C# literal `0.6` instead of GM8's one-ULP-high parse | **28** | 756 |
| `Projectiles.Move` after the character move instead of before | **336** | **336** |

The first row of mutations is why both gates are kept: an error far below a
thousandth of a wire unit is invisible to any tolerance and lands on a boundary
within thirty ticks. The second is why the tolerance is not a fudge factor — a
divergence in this simulation is a cliff, because a wrong velocity is re-applied
every tick, so no tolerance between a thousandth and a unit picks a different
tick. The test asserts that gap directly, and if it ever fails the tolerance
*has* started hiding a drift.

**So: when an exact window ends, probe before you dig.** An overshoot of a
thousandth is a boundary and the next four hundred ticks are still exact; an
overshoot of order one is a bug and worth a session.

### Settled: an exact window can also end on a *threshold*, which is not a bug either

The section above is about a rounding boundary in the *reporting*. This one is
about a threshold in the *simulation*, and it is the more expensive of the two to
misread, because the divergence it produces is large.

**Tick 1673 of `reference-ctf_truefort-closedcombat.trace`, measured.** Slot 1 is
a soldier running right into a wall. Going into t1667:

| | |
|---|---|
| port `x` | 2115.9779950445145 |
| port `hspeed` after Begin Step | 1.5307154131631306 |
| port `x + hspeed` | **2117.5087104576776** |
| the `place_free` boundary | **2117.5** exactly |

The collision box origin rounds half to even, so 2117.4999 is free and 2117.5 is
not. The port clears the boundary by **0.0087 world pixels**, is stopped by
`characterHitObstacle`, and has its `hspeed` zeroed **one tick before the
reference does**. It turns around a tick early, and from then on the two are out
of step horizontally while remaining in step vertically:

| observation | matches the port at | dx | dy | dhs | dvs |
|---|---|---|---|---|---|
| t1673 | **t1672** | -0.10 | +31.45 | -0.04 | -5.25 |
| t1673 | t1673 | -13.22 | +0.45 | -3.32 | -0.15 |
| t1680 | **t1679** | -0.10 | +10.45 | +0.48 | -5.55 |
| t1680 | t1680 | -21.11 | +0.45 | -0.71 | -0.45 |

⚠️ **That table is the diagnosis, and it is worth learning to read.** A
divergence whose *horizontal* half matches the previous tick and whose *vertical*
half matches this one is not an arithmetic error — it is one axis running a tick
early, which only a discrete event can cause.

**0.0087 px is twenty times finer than the wire can express**, so no observation
between the spawn anchor and t1667 can say which side of the boundary the
reference was on. `Gg2.Parity.BinSweep` is what settles it instead: it anchors
one slot from the wire and sweeps that anchor across the bin the observation was
rounded from — 0.1 px of position and 1/17 px/tick of velocity either way — then
counts how many of those states reproduce the target tick exactly.

| | coverage of slot 1's bin at t1673 |
|---|---|
| the port | **34 / 49** |
| with the doubled `i / MAX_I` factor dropped from `good_move_contact_solid` | **0 / 49** (and the divergence moves to t1218, slot 4) |

**Wide coverage means every rule is right and the port is a hair off a
threshold. Zero coverage means the reference did something the port cannot do
from anywhere the wire allows, which is a bug with an address.**
`PhysicsParityTests.TheDivergenceThatEndsTheWindowIsAThresholdRatherThanAWrongRule`
is the gate, and it asks about whichever slot actually diverged so that a fix
elsewhere does not leave it interrogating the wrong one.

This is the closed-loop form of the amplifier the open-loop section already
describes. The difference is that closed-loop has no anchor quantisation left to
blame, so the temptation is to call every residual real — and here the residual
is real, four hundred ticks of it, and still not a bug.

⚠️ **Do not try to close it by measuring a bias in the residuals.** The obvious
test — if the port were exact, `port*5 - obs` would have mean zero — does not
work, because the samples are **not independent**: a character standing on the
ground reports the same `y` for dozens of consecutive observations, so the
standard error is far smaller than the sample count suggests. Run on this
fixture it reports the vertical residual as biased at *z = 16*, which is an
artefact of that autocorrelation and not a finding. The horizontal residual,
which does change every tick, shows no significant bias (z = 0.7).

### Settled: a stock client plays a round on the C# server, start to finish

✅ **Demonstrated 2026-08-27** — phase 6's owed exit criterion, on
`ctf_truefort`. `gg2-server --port 8191 --map ctf_truefort --time-limit 1`, a
stock GM8 client launched with `-server 127.0.0.1 -port 8191`, AFK timer off:

| step | result |
|---|---|
| client joins | roster shows the server's reserved slot 0 and the client, both spectator |
| map loads | `CustomMapRoom`, `isHost = 0` |
| picks red | character spawns at **(1704, 474.40)** — a real `ctf_truefort` red marker |
| round runs | ends on the clock after ~1,124 frames |
| winner | **`TEAM_SPECTATOR` (2)** — a **draw**, correct: nobody capped and both flags were home |
| map change | client survives it, stays connected |
| re-picks blue | spawns at **(3702, 474.40)**, the far side, second round runs |

⚠️ **Being returned to spectator across a map change is the reference's own
behaviour**, not a port bug: `reference-gen_destroy-closedgen.trace` carries
**13** `PLAYER_CHANGETEAM`s where a non-rotating recording carries 7 — the bots
re-pick after the change too. A driven client simply has to re-pick as a human
would.

**`gg2-server` gained `--time-limit`, `--cap-limit` and `--respawn-time`**,
without which no round can be played to completion in a session — the default
clock is fifteen minutes. Its help text also claimed "No simulation yet", which
had been wrong since phase 4.

#### ✅ `koth_valley` is demonstrated in full (2026-08-27)

A stock GM8 client joined `gg2-server --map koth_valley --time-limit 255
--respawn-time 1`, walked to the hill, captured it and held it to the end of the
round. The whole King of the Hill loop runs against a real client:

| Event | Observed |
|---|---|
| Join | 1 control point and the KotH HUD built from the port's wire |
| Unlock | `cpUnlock` → 0, `locked = 0`, `UNLOCKCP` sent |
| Capture | client at **(2344.40, 606.40)**, `collision_point` against the `CaptureZone` returns an instance; `ControlPoint.team` = red |
| Hold | red's `redTimer` runs **5400 → 0** while the point is owned and uncontested |
| Win | server logs **`round over: Red wins`** |
| Victory screen | map change **exactly ten seconds later**, `now playing koth_valley` |
| Next round | point **locked** and neutral, `cpUnlock` counting from 900, `redTimer` back at 5400, player returned to spectator |

⚠️ **The three-minute hold is what made this expensive, and the hill is what made
it hard to reach** — see the driving notes below. A live **class change** was
exercised on the way (`ClientPlayerChangeclass` mid-match, answered with a death
and a respawn on the marker), which is a second path through the server that no
recording gates.

⚠️ **`KothHUD`'s `hostTimer` only counts down while the point is owned and
uncontested**, so a neutral hill never runs the clock out — which is why the
earlier half-demonstration stopped where it did, and is correct GG2 rather than a
port defect.

✅ **The client-driving blocker is answered (2026-08-27), and it was the
team-select overlay.** `PlayerControl.events/Begin Step.xml` gates every movement
key behind `if(!menuOpen)`, and `menuOpen` is true while `TeamSelectController`
or `ClassSelectController` exists. Picking a team by injecting
`ClientPlayerChangeteam` over the socket bypasses the UI, so the overlay never
closes: the server hands out a character, the mask reaches the bridge, and
`keyState` stays 0. Destroying both controllers after the pick takes `keyState`
0 → 32 and the character walks (x 1704 → 1967) — verified before and after.
CLAUDE.md, "Driving a client: `menuOpen` eats every movement key".

✅ **And the second half of driving a client is a per-frame hook** (2026-08-27),
which is what finally got a character onto the hill. A tool round trip is 30-100
game frames, so a jump or a stop inside a 60-pixel window cannot be issued from
outside — but **`gg2_watch` evaluates its expression every frame**, and
`execute_string` is a function, so a watch expression can run statements. One
line gives a bunny-hop with an automatic stop; CLAUDE.md, "Driving a client: a
watch expression is the only per-frame hook there is", has it, along with the two
traps: **GG2 jumps on the pressed edge, so a held jump fires once** (a character
climbs one step and then looks wedged), and the mask must be written
**absolutely** — an `|` leaves an earlier direction bit set and the character
walks the wrong way.

⚠️ **And a client returning to spectator is usually NOT the AFK timer.**
Measured with `afktimer` pinned at 9,999,999 and `TeamSelectController.afk = 0`,
the revert still happened — it comes from the **server**, which sets every roster
entry to `Team.Spectator` on a map change (`GameServer.cs`, citing the
reference's `with(Player) ... team = TEAM_SPECTATOR`).

✅ **And the thing that looked unexplained is settled: `--time-limit 0` is a
ONE-MINUTE round.** The flag reached the round rules perfectly — `GameServer`
floors it with the reference's own `max(1, min(255, ...))` (`game_init.gml:96,
101`), so the round ended on time and the map change swept the client back to
spectator. **The "0 means no limit" escape belongs to `WorldState` and nothing
above it**, which is what made the flag look broken. For a long live session pass
a large number. Gated by
`MapRotationTests.AZeroTimeLimitIsFlooredToOneMinuteRatherThanMeaningNoLimit`
and its control `ALimitAboveTheFloorIsNotFlooredToIt` — the pair fails, one
test, if the floor is removed.

## Tier 3 — a real client, watched

The end-to-end check the other tiers cannot give: launch a stock GM8 client
against the ported server and play. `gg2-agent` already automates every part of
this — `tools/launcher.js` starts a client, `gg2_session` manages a
server-plus-clients set, `gg2_screenshot` and `gg2_area_shot` see what is
happening, and `gg2_evalx` reads the *client's* predicted position so it can be
compared against what the port believes.

That last one is the sharpest tool in the box: `syncWrongness` already exists as
a variable on `Character`, and reading the client-side character's `x`/`y` while
the ported server runs turns "feels rubbery" into a number.

## Tier 4 — behaviour scenarios

`gg2-agent/tools/bot-scenarios.js` is an existing suite of "can a bot still walk
from A to B on map M" cases, with tick counts. Once the port runs bots, the same
scenarios should pass with comparable tick counts. This is the regression net for
the bot work that motivated the whole project, and it is worth porting the
runner early rather than reinventing it.

## Order of work

The tiers are also the order. Tier 1 gates tier 2; a trace replay against a wrong
`placeFree` produces a wall of mismatches that says nothing about the movement
code. Get `placeFree` bit-exact first, then a walking-only trace, then jumps,
then everything else.


### Settled: an uber charge is the longest chain of correct behaviour a fixture can ask for

Added 2026-08-26 with the Medigun. `Fields.Ubers`,
`SupportClassFixtureTests.ThePortChargesAnUberInTheClosedFixtureAndNeverEarly`.

Filling an ubercharge takes **800 consecutive ticks** of the beam finding the
right patient, holding line of sight to them and charging at the right one of two
rates. Nothing else in this project asks for an unbroken chain that long, and it
is worth having precisely because a port that never healed anybody produces no
`UBER_CHARGED` at all — the class byte would simply stay low, and low is a legal
value.

Measured on `reference-ctf_truefort-closedsupport.trace`, the only fixture with a
medic in it: the reference charges on **t1928** and the port on **t2340**, and
those are the only two uber events in 2,393 ticks.

⚠️ **The 412-tick lag is not a bug in the beam, and the reason it is not says
something about how to read these numbers.** The rate is 2.5 a tick off a hurt
patient and 1.75 off a healthy one. The port's patients are healthy far more
often than the reference's, because the pyro and the engineer that are hurting
them are not implemented. 2000 at 2.5 is 800 ticks; at 1.75 it is 1,143. The
observed lag sits between the two, which is what a mixture of the rates predicts
and what an error in the beam itself would not.

So the assertion is **one-sided**: the port must charge, must charge exactly
once, and must never charge *early*. Early cannot be explained by missing damage
— it would mean the rate or the target selection is wrong. The bound tightens to
an equality as the other two weapons land, and it should be tightened rather than
left at 500.

The same fixture's **hp** window is the mirror image of the same fact. It was 328
ticks after the anchor before the Medigun existed and it is 328 ticks after, and
the first hp disagreement in the recording is at **t66** — hundreds of ticks
before the anchor — where slots 5 and 6 drop to 99 and 113 while the port holds
them at a full 120. Healing can only move that window once there is damage for it
to undo. ⚠️ **An implementation that does not move a bound is not thereby
wrong**; check which side of the ledger the bound is on before concluding
anything from it.

⚠️ And the pyro did not move it either — see "t1102 of `closedsupport` is not
the pyro, and not the medic either" below, which is where that guess was tested
and failed.

### Settled: t1102 of `closedsupport` is not the pyro, and not the medic either

Measured 2026-08-26, immediately after implementing afterburn and the
Flamethrower. `SupportClassFixtureTests.TheSupportFixturesHpWindowIsBoundedByDamageThePortCannotDeal`.

The previous session's handoff read the closed support fixture's 328-tick hp
window as "damage the port never dealt", and named the pyro as the lever that
would move it. **It is not.** With the pyro's flames implemented and again with
`FireFlamethrower` stubbed out, the window is **328 ticks either way** and the
first post-anchor hp mismatch is **t1102, slot 4** in both runs.

| compared | pyro off | pyro on |
|---|---|---|
| trajectory | 412 (t1186) | 412 (t1186) |
| trajectory + hp | 328 (t1102) | 328 (t1102) |
| hp alone | 328, 481 mismatches | 328, **473** mismatches |
| deaths | 373 (t1147), 14 mismatches | **368** (t1142), 18 mismatches |
| ubers | 1154 (t1928) | 1154 (t1928) |

So the flames move the *totals* — eight fewer hp disagreements over the whole
recording, and the port's uber charge six ticks earlier because hurt patients
charge at 2.5 rather than 1.75 — while leaving the window exactly where it was.
⚠️ **They make the deaths window slightly worse**, which is what partial damage
should do: a port that deals some of the damage kills people at times neither the
old port nor the reference does.

At t1102 the port has slot 4 at **98** against the reference's **101** — the port
is three hp *low*, so it is over-damaging or under-healing, not under-damaging.
That rules out the two unimplemented weapons on its own: a missing Shotgun or
sentry would leave the port *high*.

**Answered, in the section below.** Slot 4 is a pyro being burnt by the other
pyro, and the flames that connect are unreproducible — see "an unseeded weapon
caps a fixture's hp window". The window is a ceiling, not a lead.

⚠️ **Do not read a bound that did not move as an implementation that did not
work.** Check which side of the ledger it is on first, and check the sign of the
residual: three hp low and three hp high are different bugs.

### The airblast is measured and implemented, and no fixture can gate it

The two oracles this section used to list as blockers were measured on
2026-08-26 (`tools/probes/probe-airblast.js`, `probe-airblast2.js`, against a
dedicated server with an empty match) and the airblast and the `Flare` are
implemented — `docs/SIMULATION.md`, "Settled: the airblast, and the flare that
rides on it", has the rules. What belongs to *this* document is the evidence
shape:

- **All five fixture windows are byte-identical before and after** —
  357/1204/756 on `closedcombat`, 489/328 on `closedsupport`. That is not
  evidence of absence of bugs: the Flamethrower is unseeded, so a pyro's blast
  in a recording can never be re-derived, and `closedsupport`'s hp window was
  already capped at t1102 by flame spread. The unit tests
  (`AirBlastTests`, `AirBlastBoxTests` — every measured probe row pinned) are
  the gate, the same arrangement the flame itself has.
- ⚠️ **The measurement corrected a shipped assumption**: the turret's flipped
  box under rotation applied its two-pixel inflation to the *rotated* box; the
  engine applies it to the sprite edge *before* rotating. Two pixels on a
  375px acquisition radius, live in `Sentries.TurretReportedBox` since the
  sentry stage — and invisible to every fixture, which is worth remembering
  when weighing what a green window proves.
- The probes also closed `collision_circle`'s fractional-centre gap (floored,
  not real and not rounded) — reachable by every sentry-guard call site too,
  since a builder's position is a real.

### Settled: an unseeded weapon caps a fixture's hp window, and no correctness will raise it

Measured 2026-08-26, chasing t1102 of `closedsupport`.
`SupportClassFixtureTests.TheSupportFixturesHpWindowIsCappedByThePyrosUnseededSpread`,
`PhysicsParityTests.ThePortReproducesAFightsDamageForFarLongerThanItsTrajectory`.

**Seven of the ten weapons broadcast the seed their spread is drawn from, and
three do not.** The Scattergun, Shotgun, Rocketlauncher, Minegun, Revolver, Rifle
and Medigun-needle all go through `sendEventFireWeapon(ownerPlayer, seed)`, so
every client re-derives the same pellets — and so can a replay
(`ITraceSimulation.SetFireSeed`). The **Flamethrower**, the **Minigun** and the
**Blade** call `randomize()` and send nothing. Their spread comes off an
OS-seeded stream and exists only on the server.

⚠️ **So a recording containing a pyro, a heavy or a Quote has an hp window that
ends at the first shot of theirs that connects, whatever the port does.** This is
a property of the recording and the weapon, exactly like "a recording of players
who were already alive is open-loop only" — not a defect to be chased.

The evidence, on `reference-ctf_truefort-closedsupport.trace`:

- Slot 4 is a pyro. Its first hp disagreement after the anchor is **t1102**.
- Between t1096 and t1101 the port lands **seven** flames on it (−3.14 each plus
  afterburn), reaching 97.63 where the reference is at 101 — about **six**.
- The inputs are replayed, so both pyros pulled the trigger on the same ticks.
  What differs is which flames connected.
- The spread is `sign(random(2)-1) * power(random(3), 1.8)`, so up to **±7.2°**.
  At a flame's range that is wider than a character.
- Measured both ways: with the Flamethrower implemented and with
  `FireFlamethrower` stubbed out, the window is **328** and the first mismatch is
  **t1102** in both runs. Before the pyro the port dealt no flame damage and
  diverged there; after it, it deals the wrong flame damage and diverges there.

⚠️ **Two sessions in a row read that 328 as "damage the port cannot deal yet" and
named the next weapon as the fix.** It was neither the medic's beam nor the
pyro's flames. Before concluding that a bound is waiting on a feature, check the
**sign** of the residual — the port was three hp *low*, which no missing weapon
can explain — and check whether an unseeded weapon is in the recording at all.

**The fixture that can carry a damage claim is `closedcombat`**, whose three
classes (scout, soldier, demoman) all broadcast their seeds. Its **hp window is
1,204 ticks** — t917 to t2121, forty seconds, nineteen fire ticks — which is
three and a half times its trajectory window of 357. Hp survives both the
rounding boundary at t1274 and the real `place_free` threshold at t1673, because
neither is large enough to change what a pellet hits. The first hp disagreement
is slot 4 at 72 against 80: one scattergun pellet, four hundred ticks after the
trajectory stopped being exact.

**When adding a fixture, ask which weapons are in it.** A recording meant to gate
damage should hold only classes that broadcast their seeds; one holding a pyro
can gate trajectory, run powers, boxes and the uber, but not hp.

### Settled: a weapon's knockback is part of the trajectory, not just its damage

Measured 2026-08-26 when the engineer's Shotgun landed.
`SupportClassFixtureTests.TheSupportClassesTrajectoriesAreExactFromTheirSpawnAnchors`.

`reference-ctf_truefort-closedsupport.trace` holds two engineers, and adding
their Shotgun moved the **trajectory** window from **412 to 489 ticks** — 77
ticks bought by a weapon whose only effect on position is
`motion_add(shot.direction, shot.speed * 0.03)` on each pellet that lands. It
also more than halved the hp mismatches over the whole recording (473 to 217) and
took the deaths from 18 to 14.

The lesson for reading these bounds: **a missing weapon shows up in the
trajectory window, not only in the hp one.** Eight of the recording's fourteen
deaths are now reproduced within one to six ticks with the right killer and the
right damage source, and three of them — the two engineer-on-engineer kills at
t130/t131 and the one at t1364 — could not happen at all before.

⚠️ **And it broke a gate that deserved to break.** The uber gate asserted the
port charges an ubercharge in this fixture. With the Shotgun in, the port kills
people it previously could not, slot 2's medic dies to pyro flames at t1742
instead of surviving to charge at t1928, and the assertion failed for a change
that improved every other axis. Whether that medic lives is decided by which
flames connect, which is unseeded — so the gate now asserts only the half with no
RNG in it: **never early, never twice**. A gate whose subject has to survive an
unseeded weapon is a gate on that weapon's luck.

**And it shows up in the open-loop residual too** (measured 2026-08-26, on the
re-measurement below). `reference-ctf_truefort-hotsupport.trace` replays with
p99 `|dy|` of **17** wire units today and **30** with the Shotgun switched off,
and max `|dx|` of **10** against **25**. Its recorded numbers were taken before
the Shotgun landed and were never re-measured, so its bounds — p99 `|dx|` &lt; 30
and `|dy|` &lt; 60 — had been a factor of three loose ever since. They are 15 and
25 now, and the Shotgun-off run is the control that proves they bite: at 60 the
same regression passed.

⚠️ **The vertical *maximum* does not move** — 166 wire units either way. That is
one stair step against a wire anchor, which "an open-loop residual is measured in
discrete events" says is the floor rather than an error. **A knockback moves the
percentile, not the maximum**, and a bound placed on the maximum would not have
seen any of this.

### Settled: the closed-loop windows survived four classes landing

Re-measured 2026-08-26 after the heavy, sniper, Quote and spy, and after the
collision stage was changed from a cursor to a handle snapshot. **Every one of
the five is unchanged:**

| Fixture | Window | Ticks | Range |
|---|---|---|---|
| `closedcombat` | trajectory + hp + deaths | **357** | t917-t1274 |
| `closedcombat` | hp alone | **1,204** | t917-t2121 |
| `closedcombat` | sub-wire, tolerance 1 | **756** | t917-t1673 |
| `closedsupport` | trajectory | **489** | t774-t1263 |
| `closedsupport` | trajectory + hp | **328** | t774-t1102 |

That is the point of re-measuring: four iterations of new simulation, including
per-character `runPower`/`jumpStrength` and a rewritten collision stage, and the
parity work cost nothing. Worst sub-wire overshoot inside the window is still
0.0014 wire units.

⚠️ **The snapshot change is correctness insurance, not a measured improvement.**
Reverting `Projectiles.Collide` to its old cursor reproduces all five windows and
all four open-loop residuals *to the digit* — no checked-in fixture reaches the
case where a collision frees a projectile other than the one whose event is
running. `BladeTests.AnExplosionDoesNotMakeTheStageSkipTheProjectileBehindIt` is
the only thing that knows the difference, which is exactly why it exists.

⚠️ **A bound nobody re-measures decays into a formality.** Two had: the one above,
and `OpenLoopPredictionErrorIsSubPixelOnATypicalTick`, whose horizontal p99 bound
of 25 wire units was *five* pixels under a test named for being sub-pixel. It is
5 now, against an actual of 2. **Re-measure the bounds when the simulation
changes, not only when a window is expected to move** — the windows here were
the ones expected to move and the open-loop residuals were the ones that had.

### Settled: a sentry is injected like a spawn, and its landing is the comparison

A `BUILD_SENTRY` in a recording is the reference's own acceptance of the build,
so the replayer injects it the way it injects a `PLAYER_SPAWN` — preconditions
not re-run — and everything after the placement is simulated: the fall, the
construction, the turret's fire, the death. Two conventions worth writing down:

- **The anchor prefers the simulated builder over the broadcast position.** The
  reference built at `player.object.x`, a full double; the message carries
  `round(x*5)`. In a closed loop the port's own character *is* at the exact
  position, so `PortedSimulation.BuildSentry` uses it and falls back to the
  quantised message only when the slot has no character.
- **The tick offsets differ between the two sentry messages.** `BUILD_SENTRY`
  is written by `processClientCommands` *before* `serializeState`, so it lands
  in its own tick's events and applies before that tick advances;
  `SENTRY_POSITION` comes from the sentry's Begin Step *after* the update, so —
  like a `WEAPON_FIRE` — it is recorded under the following tick. A message at
  t867 reports a landing during simulated tick 866.

`SupportClassFixtureTests.TheInjectedSentriesLandOnTheRecordedTickAtTheRecordedSpot`
compares both recorded landings by tick, slot and quantised position, through a
`SentryWatcher` decorator over the real replay loop (the third such decorator —
never reimplement the loop). Both match exactly, and `closedsupport`'s hp window
is 328 with the turrets live inside it — the same 328 as before the sentry
existed, which is the closed-loop evidence the turret neither deals damage the
reference did not nor misses damage it dealt. Recorded `DESTROY_SENTRY`
messages are not yet compared; no checked-in fixture contains one.

### A replay never respawns anybody by itself, and `RespawnTime = 0` is how

Phase 6 gave the simulation autonomous respawn: a death arms
`PlayerState.RespawnTimer` and the alarm stage spawns the character at
`floor(random(n))` over the team's spawn points. **A replay must not run that
path.** A recording carries the reference's own `PLAYER_SPAWN`s — with the
reference's random point picks inside them — and the replayer injects those as
exact anchors; a port that also rolled its own picks would race the injection
with a spawn at a different point, on a tick decided by its own simulated
death timing.

The off switch is `WorldState.RespawnTime = 0`, the default: `Deaths.Kill` and
the team-change re-arm are both gated on it, so nothing in a replay ever arms
the timer, and the alarm stage has nothing to fire. The reference floors its
own value at one tick (`game_init.gml`), so 0 is unreachable there and
unambiguous here. `PlayerLifecycleTests.WithRespawnTimeZeroADeathArmsNothing`
is the control.

What a future fixture *could* compare: the **tick** a `PLAYER_SPAWN` arrives
on. The point id is a free server-side draw and can never match, but the
timing — death tick plus `Server_Respawntime` — is deterministic, and no
checked-in recording currently gates it. A closed-loop recording whose deaths
are already exact would get the spawn ticks for free; compare them the way
deaths are compared (from the messages, never fed in) if a fixture ever needs
to pin respawn timing.


### Settled: the objective is the one thing four fixtures observe and none anchors

Phase 6's intel added `Fields.Intel` to the replayer: every recorded
`GRAB_INTEL`, `DROP_INTEL`, `SCORE_INTEL` and `RETURN_INTEL` is matched, as a
multiset, against the ones the port produced — both directions, like the ubers
and for the same reason. Four of the six fixtures hold one
(`gg2-parity census` prints them):

| fixture | intel messages |
|---|---|
| `closedcombat` | `GRAB_INTEL` t133 (slot 3), t746 (4), t2177 (1) |
| `closedsupport` | `GRAB_INTEL` t2232 (slot 1) |
| `hotcombat` | `GRAB_INTEL` t67 (slot 5) |
| `hotsupport` | `SCORE_INTEL` t59 (slot 6), `GRAB_INTEL` t819 (3) |

⚠️ **The comparison is worth having precisely because nothing else can see the
objective.** A briefcase's position and its return alarm ride in a
`FULL_UPDATE`, of which a recording holds exactly one — sent on the join path,
before anything has happened — and the carrier's own `intel` byte is in the
same block. So an `INPUTSTATE` and a `QUICK_UPDATE` say nothing at all, and a
port with **no objective implemented** replays every fixture with exactly the
drift it had before. That was measured: adding the intel moved none of the five
windows by a tick (357 / 1204 / 756 on `closedcombat`, 489 / 328 on
`closedsupport`).

**What can be gated, and what cannot:**

- **`closedsupport`'s grab lands on its recorded tick**, closed loop. It is the
  only fully spawn-anchored recording, so it is the only one whose characters
  are on exact integer coordinates rather than inside a quantisation bin — and
  the grab is at **t2232**, 969 ticks past the end of that fixture's 489-tick
  exact trajectory window. A replay that has stopped being bit-exact still
  walks the same bot into the same briefcase on the same tick.
  `IntelFixtureTests.TheSpawnAnchoredFixturesGrabLandsOnItsRecordedTick`.
- **All four reproduce the same set of grabs**, open loop, ignoring ticks — an
  open-loop replay resynchronises every seventh tick from a position quantised
  to a fifth of a pixel, so a bot walking into the flag can cross the boundary
  a tick either side.
- ⚠️ **Neither of the other two events can be gated by any current fixture**,
  for reasons that belong to the recordings:

**`hotsupport`'s capture is unreachable: a replay cannot know who was already
carrying.** Its `SCORE_INTEL` on t59 belongs to a bot that had the flag before
the recorder connected. The only statement of that anywhere is the join
`FULL_UPDATE` — and `TraceParser` files that under `start` and treats it as an
event rather than a tick, so `Replayer` never applies it and `SetCharacter`
never sees a block with `HasFullDetail`. Every replay therefore begins with
both briefcases on their bases and nobody carrying. Closing it means applying
`start` before the first tick, which would *also* place every character from
the wire several ticks earlier than the first `QUICK_UPDATE` does — a change
that can move all five measured windows and needs its own before-and-after.
Note the same gap silently affects gate solidity: an intel gate is solid only
to a carrier, so a replay that does not know somebody is carrying will walk
them through a wall the reference was stopped by.

**`closedcombat` loses every character at once on t767, with no message of any
kind, and that makes it useless for the return.** Measured, not inferred: slots
4 and 5 are the only live characters at t766 (the other three died at t651,
t656 and t705) and *both* vanish between one `INPUTSTATE` and the next, then
respawn together at t916 — 150 ticks, the respawn time. No `PLAYER_DEATH`, no
`PLAYER_CHANGETEAM`, no `CHANGE_MAP`, nothing. It has the shape of a round
reset, which in the reference also puts the briefcase back on its base and
clears its alarm. The port sees only two characters disappearing, treats each
as a destruction — correctly; that is what `Destroy.xml` does — drops the flag
and returns it 899 ticks later at t1669, where the recording has no
`RETURN_INTEL` at all.

The same region explains why `closedcombat`'s two later grabs land a tick late
in a closed-loop replay, and it is not the intel's doing: the port kills slot 3
on **t658** where the recording says **t656**, so its briefcase is dropped
2.8 px further along that bot's walk and the next player reaches it a tick
later. All of it is before t917, which is where this fixture's exact anchor
starts — CLAUDE.md's rule that a green trace is not coverage, from the other
direction. `closedcombat` is a grab-set fixture and nothing more.

**When adding a fixture, ask whether it contains a capture with its grab.** A
recording that starts before the grab and runs past the capture is the only
thing that would gate `doEventScoreIntel` end to end, and none of the six does.

### Settled: a death lasts exactly 150 ticks, and the fixtures say so

`RespawnTimingTests`. **Twenty-eight death-to-respawn pairs across five
recordings, and every single gap is 150 ticks with no variance at all** —
`closedcombat` 4, `hotcombat` 3, `closedsupport` 14, `hotsupport` 5, `bots` 2.
That is `Server_Respawntime`'s default of five seconds at 30 Hz, and it is the
reference's own answer rather than anything the port computed.

This closes an open question the project had carried for several sessions:
**respawn timing had only ever been gated by unit tests that asserted the port
against itself** — a counter set to `RespawnTime` and then counted back down,
which cannot catch the counter being armed in the wrong event, read in the wrong
stage, or off by one against the engine.

**The recordings can answer it because they need no simulation.** A
`PLAYER_DEATH` and a `PLAYER_SPAWN` are both real messages at real tick indices,
so the gap between them is a measurement. The port's half is separate and driven
through whole `MatchSimulation.Tick` calls, reading `Alive` rather than the
counter — ⚠️ **the counter reaching zero and a character existing again are two
different events, an alarm stage apart, and only the second is what a client
sees.** Between them the claim is end to end: the recordings give the number and
the port reproduces it.

⚠️ **Zero variance is what makes it a gate rather than a statistic.** A rule that
armed the counter one tick early would move all twenty-eight pairs at once, so
the port test catches an off-by-one in either direction — checked by mutation,
both ways.

✅ **And the spawn-room arm is measured now too, at one tick.** This used to say
that not one of those twenty-eight deaths happened inside a spawn room, so the
fast-respawn arm of `Character.events/Destroy.xml:37` had **no fixture evidence
at all** — written as `NoRecordedDeathHappensInASpawnRoom`, an assertion
designed to stop being true *loudly*. It stopped being true on **2026-08-27**,
on purpose: `reference-ctf_truefort-spawnroomdeath.trace` was recorded to break
it, and `ADeathInsideASpawnRoomIsAnsweredOnTheVeryNextTick` replaces it.

**A death inside a spawn room is answered on the very next tick — a gap of 1,
against 150 outside.** That arm writes `player.alarm[5] = 1` rather than the
respawn time, and an alarm set to `n` fires `n` ticks later, so the
`PLAYER_DEATH` and the `PLAYER_SPAWN` answering it land on consecutive ticks.
It confirms the alarm rule end to end through the wire as a side effect.

⚠️ **The recording carries its own control, and it needs one**: the same ninety
seconds hold two ordinary deaths, both outside a spawn room and both answered
after the full 150 (`OneRecordingHoldsBothRules`). Without that, a one-tick gap
is equally well explained by a server whose respawn time simply *is* one tick.

⚠️ **Bots do not die in spawn rooms on their own, so the death had to be forced
— and the obvious way does not work.** Setting a character's `hp` to 0 over the
bridge produced **no death at all**: a character standing in a spawn room is on
a **healing cabinet**, which tops it back above zero before End Step ever tests
`hp <= 0`. Use a value the cabinet cannot undo in one tick (`hp = -1000`), and
check `place_meeting(x, y, SpawnRoom)` in the *same* eval that assigns it, or
the bot walks out between the two calls.

**The pair count is 60 now, not 28**, across eleven recordings — the six 2026-08-27
fixtures with deaths in them were added to `RespawnTimingTests.Fixtures` so that
"every recorded death" keeps meaning every one of them.

**The fixture-side half is a gate too, on a narrower thing**: it pins
`TraceParser`'s tick indexing and the fixtures' own configuration. Doubling the
index in the parser fails it, along with four other fixture tests — checked.
