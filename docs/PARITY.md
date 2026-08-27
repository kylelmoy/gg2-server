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

⚠️ **And not one of those twenty-eight deaths happened inside a spawn room.** So
the fast-respawn arm of `Character.events/Destroy.xml:37` — the one added in
iteration 43 — has **no fixture evidence at all** and is gated only by unit
tests. That is written as an assertion rather than a comment
(`NoRecordedDeathHappensInASpawnRoom`) so that it stops being true *loudly*: the
first recording that does contain such a death fails it, and that recording is
then the first thing in the project able to gate the fast respawn against the
engine. **A failure there is good news, not a regression**, and the test says so
in its message.

**The fixture-side half is a gate too, on a narrower thing**: it pins
`TraceParser`'s tick indexing and the fixtures' own configuration. Doubling the
index in the parser fails it, along with four other fixture tests — checked.
