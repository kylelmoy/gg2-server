# Contributing to gg2-server

The rules a change has to follow. Read this before writing anything.

## What this is

A headless Gang Garrison 2 server in C#, wire-compatible with the stock Game
Maker 8 client. It exists to give the bot layer performance headroom and a fast
test loop — see `docs/STATUS.md`, "Why the project exists", for the full why.

**The port is finished.** Every message on the wire, every class, every weapon
and projectile, all eight gamemodes, the bot layer and the tooling are built
and gated. `docs/STATUS.md` is what landed and the evidence behind each claim.

**From here the work is bug fixes and new features, not porting** — which
changes what a change has to argue for itself. Transcribing the reference is
no longer the default answer, and adding behaviour it does not have is
ordinary; what has not changed is the bar either has to clear, which is "The
rule" below.

Every collision box in the project is measured against the running game —
including boxes whose edges are invisible to any checked-in trace (`BubbleS`,
`StabMaskS`, the briefcase); a green fixture is not confirmation of a box.

⚠️ **A gap recorded only in a code comment is a gap nobody is tracking.** If a
feature is known to be missing, it belongs in a traps file or here, not only in
a comment near the code. Do not add a blanket "nothing is missing" claim
anywhere in this repo: it is unfalsifiable from inside the repo and has been
wrong every time it was written. State what was checked, when, and against
what, instead.

Taunting and humiliation are observable simulation, not decoration — a taunt
freezes a character and the client derives that from the same key bit.
`docs/TRAPS-SIMULATION.md`, "Taunting and the humiliation round".

### Runtime switches

All off by default. **The first two must stay off wherever a client is
connected** — clients pace themselves at 30 Hz, so an unpaced server
desynchronises every one at once.

| Switch | What |
|---|---|
| `--speed <n\|max>` | pacing only |
| `--control-port <n>` | loopback-only JSON (freeze, step, teleport, map, bots, speed); executes commands against a live game |
| `--config <path>` | reads a `gg2.json` (`config convert` turns a stock `gg2.ini` into one), and **every** settable `ServerOptions` property is reachable from it — `ServerSettings.All` is the one table, gated by `ConfigFileCoverageTests`. ⚠️ **A flag always beats the file**, so a setting pinned by a flag can be saved from the panel and do nothing; `ServerOptions.CommandLineOverrides` is what says which. `docs/CONFIG.md` |
| `--web <n>` | the only surface here reachable from the network — an admin panel in a browser with six live performance graphs, a settings page over `--config`, and a restart button. Binds `127.0.0.1` unless `--web-bind`; the password cannot be configured away; every verb comes from `ControlCommands`, shared with the control port. Its socket threads touch nothing — commands are queued and drained on the tick thread. ⛔ The settings page edits the FILE and a restart applies it: most subsystems read their options once at `Start`. The main page's live controls (password, limits, rotation, brain, `GameServer.Live`) change only settings read at use time, and never write the file. Its graphs are diagnostics, never benchmark numbers. `docs/WEBADMIN.md` before changing any of that |
| `--record <dir>` | `docs/BOTAI.md` item 3. Writes trace segments labelled with who was a person — the wire cannot say, so an unlabelled recording of a bot-filled server is this project's own bots sold as human play. ⚠️ The 1 GB cap bounds the DIRECTORY, so pull and prune; a human playtest is still not the training data this wants |
| `--analytics <path>` + `--geoip <path>` | who has played here, all time, in the panel's Analytics view. ⛔ **Off unless asked for**, like `--record` and for the same reason. An address is stored as a salted hash and never as an address, and a "unique player" IS an address — wrong in both directions, exactly as `BanList` says. **No geo database ships**; the operator downloads one. `docs/ANALYTICS.md` |
| `navgen` (subcommand) | writes the nav graphs **and** their `.dist.gz` next-hop tables, skipping what did not change; `--check` asks without writing and reports `tables STALE`; `--no-distances` writes graphs alone |
| `--no-nav-distances` | ⚠️ **The exact A\* heuristic is ON by default since 2026-09-04** and this turns it off. The next-hop tables ship in `nav/` as `<key>.dist.gz` (1.62 MB) and are READ, not derived — 377 ms derived against 62 ms loaded at a `ctf_truefort` map change. A map with no file derives instead and still works. `--nav-distances` is still accepted and does nothing. ⛔ Off is a **different arm**, not a slower one: the search is weighted, so the heuristic picks the route as well as the speed. `docs/TRAPS-BOTS.md`, "Shipping the tables" |
| `--no-bot-occupancy` | ⛔ **a BEHAVIOUR switch, not a performance one.** Off, a team's routes stop being pushed apart and match dispersion falls 0.785 → 0.634 with nothing replacing it. What it buys is a live server routing the way `eval` and every training rollout already do. `docs/TRAPS-BOTS.md`, "the dispersion mechanism is now a switch" |
| `eval` (subcommand) | bot matches across every map; results do not depend on `--threads` |

### The headroom number

`gg2-server --bots 12 --botnav <path>` runs twelve bots that walk, fight and
play the objective at **0.151ms a tick against a 33.3ms budget, 0.5% of it**
(Release; Debug reads 0.307ms, so always say which build a re-measure used).
That is the project's justification, and it establishes headroom rather than a
ratio to anything — nobody has benchmarked a GM8 bot layer here.
`docs/STATUS.md`, "Phase 7", has the claim in full and what would falsify it.

⚠️ **A late-tick flood is almost never the bots.** A twelve-bot server's own
tick work is ~0.104ms of process CPU, so p99 and worst-case latency spike from
unrelated load on the same machine instead. The late-tick log line carries the
tick's own work and a verdict (`work 0.12ms, so: NOT this tick`), so this never
needs a profiling session — check what else is on the box first.
`docs/BOTS.md`, "late ticks are a PACING measurement", has the rest.

### Exposing it to the internet

⛔ **`docs/HARDENING.md` before changing anything on the socket path.** The
reference is a LAN server and its transport shape was transcribed faithfully;
what the port now adds on top is a set of bounds on what one connection can
make the server hold and do (`ConnectionLimits`), a fault guard at three
widths, and a rule that a client-driven log line must be throttled. That file
has the surface table, the found-and-fixed list, what is deliberately NOT
defended, and what would show any of it has gone stale.

⚠️ **Three of its findings bite before you know you are near them**, and
that file states each in full: the reference's 30-second handshake timeout is
an IDLE timeout that `PING` resets; a spectator sends NOTHING, so a
receive-idle timeout would kick one; and a player name is twenty arbitrary
bytes that need `LogText.Safe` before a log line and never before the wire.

### Where the traps are written down

The measured facts and traps — the part this file forbids re-deriving — live
in three files, one section per subject:

- **`docs/TRAPS-SIMULATION.md`** — the simulation. The six roundings, the
  motion variables, each weapon and projectile, each gamemode, the map
  objects, the parity-fixture rules, and the upstream bugs this port fixes
  rather than transcribes.
- **`docs/TRAPS-BOTS.md`** — the bot layer, item by item. The nav format, A*,
  the path follower and its jump and rocket branches, targeting and aim, the
  combat controller, the objective layer, the population manager.
- **`docs/TRAPS-LIVEGAME.md`** — the running GM8 game. Recording a trace,
  driving a stock client, writing a probe that measures the engine rather than
  itself.

⚠️ **Grep the relevant section before writing.** A fact measured once against
the running game should never be re-derived. All three files are indexed by
heading; each entry names the reference `file:line` and the `docs/` section
that settled the claim.

⚠️ **Three end-to-end bot measurements are pinned exactly, in both
directions** — `ScenarioSuiteTests.Baseline`, `BotObjectiveTests`' intel run,
and `PathFollowerFixtureTests`' longest steerable leg. A failure there is a
re-record, not a broken test: confirm the change is one you meant, confirm it
went the way you meant, update the row, and say so in the commit. The reverse
matters too: a pin that does NOT move when you changed something real that
should have moved it is equally an alarm. **A pinned number quoted without its
diff says nothing**, and for any measurement quoted anywhere in this repo, ask
what would go red if it doubled.

Driving the live game (recording a trace, driving a stock client, writing a
probe that measures the engine rather than itself) is `docs/TRAPS-LIVEGAME.md`
in full; read it before touching the running game, not after. Three headlines:

- `menuOpen` eats every movement key, so a client that joined a team over the
  socket never moves and looks broken.
- A `gg2_watch` expression is the only per-frame hook there is — a tool round
  trip is 30-100 frames, so a jump or a stop inside a 60px window cannot be
  issued from outside.
- A driven client goes AFK after 60 seconds and un-picks its team, and every
  later join then dies on its first frame.

### `WorldState` — the rules that bite before you know you are near them

The flat, cheaply-copyable simulation state: a value type on `[InlineArray]`
slot buffers holding no reference to anything — no map, no config, no logger.
Read-only inputs (the `CollisionWorld`) are passed **to** `MatchSimulation.Tick`,
never held. `docs/BOTAI.md` item 1 says this cannot be retrofitted, so **do not
put a reference type in there.**

- **A slot index is a roster index.** `WorldState.Clear` removes a character;
  `WorldState.RemoveSlot` removes it *and compacts*, which is what a
  `PLAYER_LEAVE` needs — using the first where the second belongs hands a
  character to the wrong player after somebody disconnects. `RemoveSlot` also
  renumbers every stored roster index (a projectile's owner, a last damage
  dealer, who set somebody on fire, a medic's heal target); anything you add
  that names a player has to be renumbered there too.
- **"Nobody" is 255, never 0.** Slot 0 is the server's own reserved player and
  always exists, so a defaulted zero silently names it.
  `CharacterState.Nobody`, `WeaponState.NoTarget`, `ProjectileState.NoOwner`.
- ⚠️ **Make a character with `CharacterState.Fresh`, never `new
  CharacterState()`.** Six of its fields name a roster slot and three more
  start at something other than zero, so a zeroed character claims slot 0
  last hurt it, assisted, set it on fire and is healing it — and none of that
  throws, because slot 0 always exists.
- **The projectile pool has a ceiling and the reference does not.** A full
  pool makes `SpawnProjectile` return `ProjectileHandle.None` and a shot the
  reference would have fired does not exist. That is a divergence, not
  graceful degradation — raise `MaxProjectiles` rather than handling it
  quietly.

⚠️ **The rest of `WorldState` is in `docs/SIMULATION.md`, "The state itself"**
— the field inventory, why the RNG lives in the state, what a death destroys
versus what a departure destroys, projectile creation order, and the
`ScenarioResources` cache. Read that section before changing what the state
holds.

### The bot layer — start at `docs/BOTS.md`

⛔ **If you are working on bots, read `docs/BOTS.md` first.** It is the front
door: the rules, what exists, how to measure a bot change, the policy seam,
and a table saying which file to open for what. `docs/TRAPS-BOTS.md` is the
detail behind it, one section per item.

The three rules that outrank everything in the layer:

- ⚠️ **A bot must NOT draw from `WorldState.Random`.** The match's stream is
  *observable simulation* — every seeded weapon puts its draw on the wire and
  every client re-derives the spread from it — so a bot drawing from it would
  make the stream's position depend on how many bots were thinking.
  `ScriptedBot.Random` is its own, and has to be part of any snapshot.
- ⚠️ **The simulation never special-cases a bot.** A bot is an `IInputSource`
  like a socket. When adding a client command, add it in **three** places —
  the socket switch, `PlayerCommandKind`, and `GameServer.Execute`'s dispatch —
  with the body in a shared method both reach, or a bot can do something a
  client cannot.
- ⚠️ **`WorldView` is a `ref struct` over a `ref readonly WorldState`.** Do not
  "simplify" it to a class or to a by-value struct: the first breaks the
  no-allocation rule, the second is a kilobyte copy per bot per decision, and
  both throw away the compiler's refusal to let bot code stash it.

## Read these before writing code

⛔ **Working on bots? `docs/BOTS.md` first** — it is the front door for
`src/GG2.Bots/` and tells you which of the below you actually need.

Otherwise, in this order, and do not skip the second one:

1. `docs/PROTOCOL.md` — the wire format.
2. `docs/SIMULATION.md` — the determinism contract. **This is the document
   that decides whether the project works.**
3. `docs/PARITY.md` — how the port is proven correct.
4. `docs/ARCHITECTURE.md` — how the projects fit together and what each may
   reference. `docs/STATUS.md` is what is built and what proves it; read it
   before claiming something does or does not exist.
5. `docs/BOTAI.md` — structural constraints that are cheap now and impossible
   to retrofit. Read it before designing `WorldState` or the bot seam.
6. `docs/PLUGINS.md` **if you are touching server-sent plugins** — the wire
   fields, the `IServerPlugin` seam, the roster-compaction rule, and why a
   plugin implementation is pinned to one zip hash. Chat is not one: `docs/CHAT.md`.
7. `docs/NAVGEN.md` **if you are touching the nav-graph generator** — the
   stages, the oracle that proves it, and why one flight model rather than three.
8. `docs/CONFIG.md` **if you are adding or changing a server setting** — the
   one settings table, the key reference, the flag-beats-file rule, and the
   three gates that make "every setting loads from a file" checkable.
9. `docs/SOURCEMAP.md` when you need to find something in the reference.

And **the section of `docs/TRAPS-SIMULATION.md`, `docs/TRAPS-BOTS.md` or
`docs/TRAPS-LIVEGAME.md` that covers whatever you are about to touch** — not
the whole file, the section. All three are indexed by heading; each one names
the reference `file:line` and the `docs/` section that settled every claim in
it.

## Building and testing

.NET 10 SDK, the machine-wide one on `PATH`. Nothing to set up.

```powershell
dotnet test GG2.sln
dotnet run --project src/GG2.Host -- --map ctf_truefort
```

⛔ **Gate a commit on the suite's EXIT CODE, never on its output.** A
`dotnet test … | tail` reports `tail`'s status, so `dotnet test … && git commit`
after a pipe commits on a red suite.
Spell it `if dotnet test GG2.sln > log; then git commit …; else …; fi`.
⚠️ **And capture WHICH test failed**, not just that one did: two green re-runs
and an unnamed red read as "it is fine now", so a flake nobody can name cannot be
recognised as recurring — and recurrence is the only signal that a fix was
necessary but insufficient.

⚠️ **A runtime bump is a physics risk here and `SIM_UUID` does not cover it**
— re-run the parity fixtures after one. `docs/ARCHITECTURE.md`, "Runtime
settings", has what the green suite does and does not establish across
runtimes.

The server is standalone: the 22 shipped maps plus the port-authored
`gg_debug2` are checked in at `maps/` (458 KB) and the 25 nav graphs at `nav/`
(gzipped, 1.2 MB, plus 1.62 MB of `.dist.gz` next-hop tables), so a clone serves
a round and runs bots with no arguments
and no reference checkout. `MapDirectory.Resolve` and `ResolveNav` find them;
`--maps`, `--botnav`, `GG2_MAPS` and `GG2_NAVCACHE` override. Tests still need
the reference for `Constants.xml`, the scripts and a few sprites: with no
`GG2_REFERENCE`, `ReferenceTree` clones stock upstream GG2 at the pinned
commit (`ReferenceTree.PinnedCommit`, `V2.9.2-78-gea8d6951`) into
`%LOCALAPPDATA%/gg2-server/reference/` on first use and reuses it after. A
fresh clone needs git and the network once. ⚠️ `.github/workflows/release.yml`
pins the same commit and must move with it.

- ⚠️ **`maps/` is a copy, and `MapCopyTests` is the only thing that would
  catch reference drift** now that both sides read the same bytes locally — so
  do not delete it as redundant. `gg_debug2` is the one map this repo authors
  and is excluded from every shipped-map census; `docs/DEBUG-MAPS.md`.
- ⚠️ **The 25 bot nav graphs live in `nav/` in this repo, not the reference
  tree.** Checked in, gzipped, shipped by every build and publish with no
  flag; 188 tests fail without them, and `GG2_NAVCACHE` overrides both ends.
  `gg2-server navgen` regenerates the whole cycle and `--check` asks without
  writing; a map with no graph gets one built at the map change, which is what
  makes a custom map navigable (`--no-navgen` turns that off).
  ⛔ **`NavGeneratorTests` rebuilds all 25 CHARACTER FOR CHARACTER, and that
  test licenses a design decision** — one arc model for every flight
  rather than three. **Weaken the test and undo the consolidation with
  it**; counts are not enough, because banker's rounding, GM8's
  delete-by-swap order and an x87 `ceil` each produced a plausible graph wrong
  on most maps. Everything else about them — the manifest, the compression
  cycle, and why there is no custom binary format — is `docs/DEPLOY.md`, "the
  nav graphs", and `docs/NAVGEN.md`.

The live-game fixtures under `tests/GG2.Tests/Fixtures/` were recorded from the
running GM8 game; the suite reads only the checked-in files.

`tools/bench/` is the measurement instrument — the per-map criterion, the
engagement-range sweep, the policy cost, and `analyse.py`. **Not in
`GG2.sln`**; build it explicitly. ⚠️ Read `tools/bench/README.md` before
reading a number out of it — the measurement traps are built into the modes
and stated there.

Everything about driving the running game is in `docs/TRAPS-LIVEGAME.md`.

## The rule

**Compatible on the wire and in observable simulation. Free everywhere else.**

The stock client re-simulates the physics itself between position updates, so
trajectories must match. Byte layouts and update cadence must match (the
default cadence does; `QuickUpdateInterval` and `ExplosionUpdateTicks` can only
add position updates, `docs/PREDICTION.md`). Roster indexing must match.

**That is the legacy wire.** The server also speaks a native protocol to the
native client, chosen by the HELLO, owned by this project and free to change
under `NativeProtocol.Version` (`docs/PROTOCOL.md`, "Two protocols"). A native
change goes in `NativeServerEncoder` or `ClientCommandTable.Native`, never in
`ServerMessages` or the legacy table, which are the stock client's.

Nothing else does. Objects, events, `with()` loops, alarms, GM8 data
structures, per-frame recomputation, server-side RNG — all of it is Game Maker
8 showing through and should be replaced with whatever is fastest and clearest
in C#. **Do not transcribe GML structure. Transcribe GML arithmetic.**

⚠️ The `x -= hspeed` automatic-movement cancellation looks like it belongs on
that "free" list and **does not**: subtracting a double and adding it straight
back loses a ULP, and the reference carries on from that value. Measured; see
`docs/SIMULATION.md`.

⚠️ **The physics departs from the reference in one other place for a reason
that is not a bug fix, and it is a `break`.** `good_move_contact_solid`'s crawl hangs — in GM8 too — when
what is left to travel is above GML's `1e-13` fuzz but below half a ULP of the
position; `Movement.GoodMoveContactSolid` stops when a step leaves both
coordinates unchanged. **Do not "restore" it to the GML**: a hang has no
observable behaviour to match. `docs/TRAPS-SIMULATION.md`, "the crawl that
could not finish".

⛔ **Six upstream bugs ARE fixed rather than transcribed** — each marked as a
deliberate departure at the line it lives on, each safe against a stock client,
each argued and gated. `docs/TRAPS-SIMULATION.md`, "Upstream bug fixes carried
into the port", has the six, the eleven that were reviewed and found not to
apply here, and the one deliberately left alone. **Read it before fixing a
seventh or re-deriving any of that.**

⛔ **A new feature answers to the same test those six did**, now that adding
one is the usual reason to be here. Does a stock client learn the result
through a message it already handles, or can it not see the difference at all?
If neither, the server and the client simulate different outcomes with nothing
on the wire to reconcile them — a divergence, however much the feature is
wanted. **A new message type is not a way round that**: in the in-game loop a
stock client silently ignores an id it does not know (`docs/PROTOCOL.md`), so
the server would act and no client would. The native protocol does not change
that answer either: it can carry a new message to a native client, but a stock
client on the same server still simulates without it. What is free is everything no
simulation depends on — the admin panel, analytics, the control port, `eval`,
the recorder — and that is where the features have gone.

When unsure which side of the line something falls on, `docs/SIMULATION.md`
has an explicit contract-vs-incidental list. Add to it when you settle a new
case.

## Non-negotiables

- **The simulation has no I/O.** No sockets, no clock, no `async`, no file
  access inside `GG2.Simulation`. Enforced by project references. This is what
  makes the parity harness and the bot iteration loop possible; if it breaks,
  the project loses its main advantage over the GM8 server.
- **`double` everywhere in the physics.** GML has one numeric type and it is
  an IEEE-754 double. A `float` is a bug.
- **The parity harness comes before the physics**, and still does for
  anything new in it: the fixture first, then the change. Physics written
  without one cannot be verified afterwards except by eye, and the failure
  mode looks exactly like ordinary GG2 netcode jitter.
- **Never copy GML into this repo.** Cite it by path and write C#.
- **Never modify either reference tree from this repo's work.** They are the
  specification and the client; treat both as read-only.

## Reading the reference

Reference tree: `Source/gg2` in the checkout `GG2_REFERENCE` names, or in the
tests' cached clone (`ReferenceTree`) — stock GG2 v2.9.2 plus upstream's
later master commits. Read it to settle what the game does.

⚠️ **It is stock GG2 and nothing else** — no `Scripts/Bots/`, no
`Scripts/BotNav/`, no `BOT_*` or `NAV_*` constants. The bot fork this port's
bot layer was seeded from lives only at `github.com/kylelmoy/Gang-Garrison-2`
(branch `bot-route-variety`); any note contrasting "the fork" with "upstream"
describes a local layout that no longer exists.

Scripts under `Scripts/` are plain `.gml`. **Object event code is XML-escaped
inside `.xml` files**, so `grep` misses most of the game's logic — about
940 KB of event text. To read one:

```powershell
cat "Objects/InGameElements/Character.events/Step.xml" |
  sed -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&amp;/\&/g'
```

If the live-game bridge MCP server is available in the session, `gg2_find` searches
scripts and unescaped event text together, and `gg2_event read` hands back
real GML — both are much better than doing it by hand.

## The reference is the specification, not this repo

Where `docs/` and the reference disagree, **the reference is right and the doc
has a bug** — fix the doc in the same change, with the file:line that settled
it. Facts in `docs/` were extracted from GG2 v2.9.2 (`VERSION = 29200`); if
the reference tree moves, re-check the protocol docs against it.

**Except the bot layer**: the reference's bot layer is treated as INCOMPLETE,
not as a specification, for `src/GG2.Bots/` only — a bot's decisions are this
project's own, improved where the fix is straightforward and measured, with
the hard ones written down as known issues rather than chased. Stock GG2
ships no bots at all, and the fork this port's bot layer was originally
seeded from has since diverged; `src/GG2.Bots/` is measured against itself
(`docs/BOTS.md`, "how to measure a bot change").

`SimPolicyNetwork` runs a trained `.gg2p` net in-process and `HybridBot`
drives real bots from it: the scripted layer owns movement, routing and
objectives, the policy owns keys and aim inside a bounded engagement window.
**`.gg2p` is the only checkpoint format the server loads.** A net names no classes, so `[Bots] ClassPolicy`
assigns each one. ⚠️ **A net is dated by the simulation it was trained
against** and a `.gg2p` carries no stamp, so nothing refuses a stale one after
a physics change; `docs/BOTS.md` has the current status. The suite drives the
shipped nets through `tests/GG2.Tests/PolicyFixture.cs`.

⚠️ **None of this reaches the wire or observable simulation** — a bot may
press "drop intel"; `MatchSimulation` may not decide to drop one. The rules
for a departure, and known issues, are in `docs/TRAPS-BOTS.md`.

## Where documentation goes

| Kind | Lives | Rule |
|---|---|---|
| **Entry points** — `README.md`, `CONTRIBUTING.md` | repo root | Edited in place. Anything a contributor must know *before* starting belongs here, not in a new file. New trap or measured-fact content goes in `docs/TRAPS-*.md`, not here. |
| **A trap, a measured fact, a rule paid for once** | `docs/TRAPS-SIMULATION.md`, `docs/TRAPS-BOTS.md`, `docs/TRAPS-LIVEGAME.md` | One section per port stage or item. This is where a "do not re-derive this" fact goes, with the reference `file:line` that settled it. |
| **Durable reference** — a format, a measured result, a rule | `docs/` | One file per subject. Add to the matching file; a new one needs a subject no existing file covers. |

Working notes are not documentation. When an investigation ends, what stays true
gets **promoted** into `docs/` or into the header comment of the code it is
about; everything else is **deleted**, because git has it. Do not leave notes
at the repo root.

### House style for `docs/`, `README.md` and any new `.md`

The rules, in force for anything written or rewritten from now on. Older files
have not been retrofitted, so match these rather than what is next to you.

- **Describe the current state of the code, not how it got there.** No "this
  used to say X", no "until <date>", no account of the bug that caused a
  design. Where a past mistake taught a rule, keep the *rule* in the present
  tense and drop the story: "an explicit assignment beats the file in both
  directions", not "assignment was added after X broke".
- **No emoji.** Not as severity markers, not as bullets. Rank by putting the
  important sentence first and by using **bold** sparingly; if a paragraph
  needs a warning glyph to be read correctly, it needs rewriting.
- **Summarise.** One paragraph or one bullet per fact. Prefer a table to
  prose for anything with more than two cases.
- **Dates and commit hashes only where they are still load-bearing** — a
  checkpoint's provenance, a `SIM_UUID`, a measurement's sample size. Never as
  narration.
- **A claim expensive to act on wrongly still states what would falsify it**,
  and that stays: it describes the present, not the past.
- ⚠️/⛔/✅ elsewhere in `docs/` are pre-existing. Leave them alone unless you
  are rewriting the section anyway.

None of this changes code comments, which keep explaining *why* a line exists.

### The failure mode no test can catch: a claim the repo makes about itself

A doc can be self-consistent with the code and still describe a capability
that doesn't actually exist — every test can be green while the described
capability never worked (an empty round loop where every "round" test passed
by construction; a "sampled or explicit" API where only explicit ever
existed). Nothing inside the repo can contradict a false claim about its own
capability or coverage; only a reader who *acts* on the description finds out
— usually someone building against it, not the author.

- Grep before writing a claim about what exists — a stale "not done" costs
  what a stale "done" does.
- Ask what would go red if the claim were false; if "nothing," check it by
  hand before writing it down.
- Reasoning is not checking: inferring a live path is fine because its
  replay/transcription path is clean is not the same as running it.
- A match/run that ends is not a match/run that happened — any "N completed"
  criterion is satisfiable by a broken feature that terminates early
  (`MatchActuallyHappensTests` makes that mechanical for match results).
- Write the falsification condition into the claim: a sentence expensive to
  act on wrongly should say what would show it has gone stale.
- The author is not a reliable reviewer of their own claim — re-reading
  recovers the meaning you intended, which is exactly the meaning that might
  be wrong. Send a claim about meaning across a seam early rather than
  reviewing it harder alone; review still catches mechanical errors like a
  transposed index.
- A gate can be wrong by being unsatisfiable by the right evidence (e.g. a
  criterion nothing could ever actually trigger) — check that it could fire.

## C# conventions

- .NET 10, nullable enabled, warnings as errors.
- Standard .NET naming. No Hungarian notation, no GML naming carried across.
- xunit for tests. Parity fixtures live with the tests and run in CI.
- No allocations in the tick loop. Pool buffers, use `Span<byte>`, keep hot
  entity data in flat arrays rather than object graphs.
- No LINQ in the simulation or the serialisation path. It is fine everywhere
  else.
- Comment the *why*, especially where a line exists to match GM8 behaviour
  that would otherwise look wrong. Those are the comments that stop a future
  session "fixing" a deliberate quirk. Cite the reference file when you do.

## Things that will waste your time

- **The client's physics is a hard constraint you cannot see.** There is no
  test that fails when trajectories drift — the client just snaps. This is
  why `docs/PARITY.md` exists and why it comes first.
- **Roster indices are positional and shift on `PLAYER_LEAVE`.** Slot 0 is
  reserved for the server's own player even in a dedicated headless server.
  Get this wrong and every message that names a player is off by one,
  intermittently.
- **`sprite_add`'s third argument is `removeback`, not `precise`.** The GM8
  signature is `sprite_add(fname, imgnumb, removeback, smooth, xorig, yorig)`,
  confirmed against the engine's own `fnames` table. Do not assume the map
  collision sprite's precision from that call; verify it empirically
  (`docs/SIMULATION.md`, "What is genuinely uncertain").
- **The reference server has a loopback TCP connection to itself.** That is
  how the *host's local player* works, not a protocol requirement. A headless
  server does not need it — but it does still need roster slot 0.
