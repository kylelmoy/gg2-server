# Driving the live game

**`D:\code\Gang-Garrison-2` holds stock upstream GG2, not the bot fork the running game was built and driven from.** Nothing below runs until the fork is
re-cloned (`git clone https://github.com/kylelmoy/Gang-Garrison-2.git`,
`git checkout bot-route-variety`, then rebuild `Source/build/`). Stock upstream
has no `Source/build/`, no bots and no bridge.

The traps that only bite a session holding the running Game Maker 8 game —
recording a trace, driving a stock client from the outside, and writing a
probe that measures the engine rather than measuring itself.

Most of these look identical to a broken bridge from the outside — the
failure mode is never an error message, it is a character that does not move.

**Read `docs/PARALLEL.md` first if more than one session is running.** The
bridge serves ONE TCP client and exactly one lane may hold it, and restoring
`gg2.ini` empties another lane's roster mid-measurement.

### Recording a trace with something in it

The reference's own bots are the cheapest source of real physics. In the build
directory's `gg2.ini` set `[Bots] Enabled=1` and a small `FillToPlayers`, run
the dedicated server, record, then **put the ini back**. `gg2.ini` lives in
`Source/build/`, which is gitignored, but it is still the user's config.

```powershell
gg2-parity record --port 8190 --seconds 30 --out fixture.trace --source "..."
gg2-parity replay fixture.trace
```

An `--out` ending in `.trace` gets the header that makes it replayable;
anything else is written raw, which is what a byte diff against another server
wants.

### Driving a real client

`gg2-server --port 8191`, then point the stock game at it — it takes
`-server <ip> -port <n>` on the command line
(`Scripts/Game/game_init.gml:304-341`), so no bridge and no menu driving is
needed:

```powershell
node D:\code\gg2-agent\tools\launcher.js "D:\code\Gang-Garrison-2\Source\build\Gang Garrison 2.exe" `
     --name c1 --role client -agentport 17779 -server 127.0.0.1 -port 8191
```

Use the launcher rather than the exe directly: it clicks away GM8's modal
dialogs, and saves a screenshot of each one, which is usually the whole
explanation for a client that stopped working. Screen-capture the window by
its `TRunnerForm` rect — `PrintWindow` returns black for GM8's renderer.

### `menuOpen` eats every movement key

`gg2_input` does drive a stock client — what stops it is the team-select
overlay.

**`PlayerControl.events/Begin Step.xml` gates every movement key behind
`if(!menuOpen)`**, and `menuOpen` is true while `TeamSelectController` or
`ClassSelectController` exists. **Picking a team by injecting
`ClientPlayerChangeteam` over the socket bypasses the UI**, so the overlay is
never closed: the server hands out a character, the mask reaches the bridge
(`AgentBridge.heldMask = 32`), and `keyState` stays 0 until the overlay
instances are destroyed:

```
gg2_eval: ClientPlayerChangeteam(TEAM_RED, global.serverSocket);
          socket_send(global.serverSocket);
gg2_eval: with(TeamSelectController) instance_destroy();
          with(ClassSelectController) instance_destroy();
```

With the overlay gone, `keyState` moves from 0 and the character walks.
Without it, nothing moves and it looks exactly like the bridge being broken.

### A watch expression is the only per-frame hook there is

A tool round trip is 30-100 game frames, so anything needing frame accuracy —
a jump, a stop inside a 60-pixel window — cannot be issued from outside.
**`gg2_watch` evaluates its expression every single frame**, and
`execute_string` is a function, so a watch expression can *run statements*:

```
gg2_watch add drive
  execute_string("with(AgentBridge) { if (global.myself.object.x > 2300 and global.myself.object.x < 2360) heldMask = 0 else heldMask = 160 - (heldMask & 128) }")
```

That one line is a bunny-hop with an automatic stop. Both halves matter:

- **GG2 jumps on the PRESSED edge, so a held jump fires once.** `press jump`
  climbs exactly one step and then the character sits there looking wedged.
  Toggling bit 128 every frame is a jump every other frame, and
  **bunny-hopping climbs terrain a single jump cannot**.
- **Write the mask absolutely, never with `|`.** `heldMask = heldMask | 32`
  leaves an earlier `left` bit set and the character walks the wrong way.
  `160 - (heldMask & 128)` alternates exactly 160/32 (jump+right, right) and
  clears everything else.
- **`gg2_input`'s bits: right = 32, jump = 128** (`keyState` 160 is both).

**Probing `place_free` from a client needs `with(CustomMapO) solid = true`
first.** The map instance is not flagged solid at the moment the bridge's Step
runs, so an unprepared scan answers "free" at every point of every column and
looks like a broken query rather than an empty room. With it, a column scan
recording each free→solid transition is the cheapest way to read a map's
walkable structure — far better than reading a walkmask screenshot by eye.

**A client returning to spectator is usually NOT the AFK timer.** With
`afktimer` pinned high and `TeamSelectController.afk = 0`, a revert can still
come from the **server**, which sets every roster entry to `Team.Spectator` on
a map change (`GameServer.cs`, citing the reference's `with(Player) ... team =
TEAM_SPECTATOR`). Check `PlayerControl.afktimer` before blaming AFK.

**A driven client goes AFK after 60 seconds and un-picks its team.**
`PlayerControl.events/End Step.xml` counts `afktimeout = 60*30` frames down and
then forces the team-select screen, which sends `PLAYER_CHANGETEAM(spectator)`
and destroys the character. Once the timer has already expired every *later*
attempt to join a team dies on its first frame, because the fresh character
has `afk = false` and the check fires again immediately — so it looks exactly
like the server refusing to spawn anyone. The reset is `keybyte != 0` **or** a
real `keyboard_check`, and the bridge's injected mask does not satisfy it.
Turn it off before driving anything:

```
gg2_eval: with(PlayerControl) { afktimeout = 999999; afktimer = 999999; }
```

A spectating client disconnects itself after **exactly five minutes**
(`Spectator.events/Create.xml`, `afktimeout = 9000`). It does that to the
reference server too — this is not a server-side bug.

### A client-side watchdog latch, and how to prove it can fire

`gg2_watch` samples up to eight expressions **every frame** and logs only what
*changes*, which is the shape a long unattended run needs: a sticky latch logs
one line when it trips and nothing for the hours either side. `execute_string`
makes a watch expression run statements, so the whole check can be one watch
that scans the roster and writes `global.wdCode`, with a second watch reading
that global back. A full `Player`-plus-`Character` scan every frame costs no
observable fps: 27 fps before and after on a six-bot `cp_dirtbowl` client
(room speed 30).

**Give the checker a self-test input — a gate that cannot fire is worse than
no gate.** A spare global (`global.wdTest`) that forces the latch is enough:
set it, confirm the latch trips within a frame, reset it. That proves the
watch is running, the sticky write works and the log records it, all without
touching game state.

**Do NOT prove it by corrupting the roster.** Pointing a spectator
`Player.object` at a live instance to manufacture the condition hangs the
client — GM8 stops servicing the bridge entirely, with no dialog for the
launcher to dismiss and nothing in any log. `Player.object` is dereferenced as
a `Character` by code that never checks, so anything else there is a wedge, not
an error. The self-test global costs nothing and is safe.

The invariant worth latching on is narrower than it looks. `TEAM_SPECTATOR` is
**2**, and `getCharacterSpriteId.gml:20` accepts only team 0 and 1 — so the
`unknown team ID: 2` fatal is not a framing slip producing a garbage value, it
is `Character.events/Create.xml:69` making a character for a player the client
holds as a **spectator**. It fires at *create*, so the state never survives to
be observed: watch the roster's transitions leading in, not the aftermath.

### `Music=1` truncates every client's room setup on a machine with no audio

**This invalidates client observations rather than merely adding noise.** A
GG2 client launched on a machine with no audio device, with `Music=1` in
`Source/build/gg2.ini`, throws inside `basicRoomSetup`:

```
EXECUTION ERROR in creation code of room CustomMapRoom
In script basicRoomSetup: In script AudioControlPlaySong:
  if(AudioControl.currentSong != -1) sound_stop(AudioControl.currentSong);
  at position 18: Unknown variable currentSong
```

`AudioControl` has no `currentSong` when there is no audio device — the same
situation `launcher.js`'s own header calls routine hits over RDP. The launcher
presses **Ignore**, and GM8's Ignore abandons the rest of the script. The call
is `basicRoomSetup.gml:80`, so **everything from line 82 down never runs**:

- `instance_create(..., Spectator)` (`:82`) — **there is no `Spectator`**.
- `global.redCaps`, `global.blueCaps`, `global.winners` (`:84-86`) — never
  initialised, so `CTFHUD`'s Step throws `Unknown variable winners` **every
  frame**.
- Whatever `:88` onward does, including the MOTD.

**The visible symptom points at the wrong thing.** A flood of `CTFHUD` errors
reads like a HUD or gamemode bug in the port; it is one dismissed audio dialog
upstream. **Bisect a client error storm against an unmodified server before
believing it is yours.**

**What it invalidates**: anything that reads `global.winners`,
`global.redCaps`/`blueCaps`, or `Spectator` — including
`Spectator.afktimeout`, the five-minute spectator disconnect, and any
`wdWins`-style counter in the watchdog latch. All of them silently measure a
client whose room setup stopped two thirds of the way through.

**The fix is one line and it is in a runtime file, not the reference source.**
`Source/build/gg2.ini`, `Music=3` (`MUSIC_NONE`, `Constants.xml:127`) — then
`basicRoomSetup.gml:79`'s guard skips the call and the script completes,
giving zero GML errors over a joined client's session (`winners=-1`,
`spectators=1`). `gg2.ini` is a gitignored build artifact the tooling already
rewrites, so this is not a reference-tree edit. If a client log fills with
`Unknown variable winners`, check this setting first — something restored the
file.

### The frame a joining client does not know itself, and the errors it prints

A stock client that has just joined prints, for a handful of frames and then
never again:

```
ERROR in ... of End Step Event for object Spectator:
Error in code at line 4: if(global.myself.object != -1)
at position 19: Unknown variable object
```

…plus the same thing from `AmmoCounter`'s Draw (line 8) and Begin Step
(line 1). **This is stock GG2 behaviour and not a port bug** — do not spend a
soak on it.

- `ClientCreate.gml:20` sets `global.myself = -1`.
- `Spectator.events/End Step.xml` guards its *first* read with
  `if (global.myself != -1)` and then dereferences `global.myself.object` on
  line 4 **unguarded**. `AmmoCounter` has the same shape.
- `global.myself` is bound in exactly one place —
  `ClientBeginStep.gml:206-208`, on the `PLAYER_JOIN` that lands at
  `ds_list_size(global.players)-1 == global.playerID`.
- The joiner's own `PLAYER_JOIN` is **one frame later than its burst**, on the
  reference as much as on the port: `GameServerEndStep.gml:7-10` flushes
  `global.sendBuffer` and *then* `:18-20` services joining players, so the
  `ServerPlayerJoin` appended at `serviceJoiningPlayer.gml:124` waits for the
  next frame's flush.
- Meanwhile the burst's `CHANGE_MAP` sets `roomchange` and **stops the client's
  message loop** (`ClientBeginStep.gml`'s `do … until(roomchange)`), so the room
  — and `basicRoomSetup.gml:82`'s `Spectator` — exists before the rest of the
  burst is read.

So there is a real window in which a `Spectator` is running its End Step while
`global.myself` is still `-1`, and whether a given join lands inside it is a
race between the client's 30 Hz frame and the server's. Two clients joined the
same way seconds apart can land on opposite sides of that race, one with a
handful of these errors and one with none. It heals on the frame the client's
own `PLAYER_JOIN` arrives.

**What it is NOT**: it is not the `cp_dirtbowl` fatal's precondition, and it
does not move a roster count. `docs/PARITY.md`, "Nothing moves a roster count
inside the join window".

**The dangerous cousin is the same error that never heals.** The bind is
positional and tried once, so a client whose roster is off by one at that
instant keeps `global.myself = -1` for the whole session and errors on *every*
frame rather than a handful. **Count the errors and check whether they stop**
before reading a client log as evidence of anything: a burst of them at join is
stock, a stream of them is a real desync. In-process that distinction is
`ServerStream.SelfBound` and `MirrorClient.Impossible`
(`ClientIdentityTests`).

### Writing a probe against the live game

Both oracles work by driving the running engine from a state chosen exactly
and reading the result back.

- **The game keeps stepping between `gg2_eval` calls.** A scenario split
  across two of them measures the bot, not your inputs. Run the whole thing
  inside one eval, or freeze with `gg2_step`.
- **Do not send a start state as a decimal literal.** GM8's parser is one ULP
  off for some values — the same issue as the physics constants. Send integers
  and ratios and let the game divide.
- **`string_format(v, 1, 17)` is only good to ~16 significant digits** and the
  last one is noise. For ULP work pull the mantissa out in chunks, each under
  2^19, because GM8's `floor()` wraps at 32 bits. Use `tools/exact-double.js` —
  do not write another one. `node tools/exact-double.js` self-tests it.
- **A probe character inherits its bot's `player.team`,** and `charSetSolids`
  makes the *other* team's gates solid — set `team` explicitly or the
  character walks into a wall the recording walked through.
- **`instance_number`/`instance_find` include an object's descendants**, and
  `Character` is a parent with no direct instances: `Character.x` is an
  error, `global.myself.object.x` is not.
- **`place_free` at the top level of `gg2_eval` runs in the *bridge's*
  scope.** Wrap it in `with(...)`.
- **`pi` is a GM8 constant**, so `for(pi=0; ...)` is a compile error reported
  as "Variable name expected".

## The samplers, the probes, and handing over the socket

`tools/sample-collision.js`, `tools/sample-physics.js`, `tools/sample-rng.js`,
`tools/sample-primitives.js`, `tools/sample-collision-line.js`,
`tools/sample-motion.js` and `tools/sample-rotated-box.js` regenerate the
engine oracles. All seven need a **running game with the agent bridge**; the
physics and primitives ones also need at least one bot character, and the RNG,
collision-line, motion and rotated-box ones are happy with any running
instance — the last two create and scale their own probe rather than
borrowing a bot's character, so a dedicated server with an empty match is
enough.

`tools/probes/` holds the one-off probes that settled a single question rather
than generating a fixture — the sprite boxes, the bubble pair, the sentry
mask, the airblast, the turret. Read the header of one before writing another:
each records what it cost to get right.

`collision-samples.json` covers every `BulletWall`, including the ones on
`gg_debug` as well as `arena_lumberyard` and `koth_harvest`; `CollisionSampleTests`
compares every rectangle the port builds against it.

**The bridge serves one TCP client at a time and the MCP server process holds
it.** Once a session has called any `gg2_*` tool, a `node tools/sample-*.js`
run times out on its first call — even `gg2_ping` — even though the game is
fine. Hand the socket over deliberately: stop the game, restart it with
`node D:/code/gg2-agent/run-agent.js --port 17777 --name server --role server -- -dedicated -map ctf_truefort`,
and do not touch `gg2_*` until the tool has finished. The other direction is
free, because each CLI run exits and releases.

**Restoring `gg2.ini` empties the running server's roster.** The dedicated
server re-reads it, so putting the bots setting back mid-session removes every
bot and the next probe measures nothing at all. Restore it last.

To bring a bot server up: set `[Bots] Enabled=1`, `FillToPlayers=4`,
`MinHumans=0` in `gg2.ini`, start the server, and put the ini back when you
are finished with the game.

## Soaking a stock client against the port

The instrumentation is in `gg2-agent`'s payload, not a fork of the game:
`agentRate`, `agentSnap` and `agentSoak`, all off by default, plus
`agentRateArm()` and `agentSnapReport()` to read back over `gg2_evalx`. That
repo's `docs/CLIENTDEBUG.md` has the patch sites. What belongs here is what a
soak measures and what ends one early.

`tools/soak-transitions.js --keep-teamed <name>` keeps a human slot on a team
for the length of a run. Without it a soak measures a spectator from its first
map change onward: the port puts every roster entry back to `Team.Spectator` on
a map change, as the reference does, and a client that picked its team through
the UI does not re-pick itself. The client stays connected and keeps
deserialising, so the run looks clean because nothing is being predicted.

### The client must be taken off the team-select screen, or it sends nothing

`--keep-teamed` puts a client on a team through the control port, which is the
server acting; the client's own `TeamSelectController` is still up, and
`PlayerControl`'s Begin Step drops every movement key while
`TeamSelectController` or `ClassSelectController` exists. So the client holds a
character, predicts it, and presses nothing — `INPUTSTATE` goes out with a
constant key byte for the whole run.

A map change re-opens both menus, so closing them once is not enough. The only
per-frame hook is a watch, and this one closes whichever is open by the menu's
own path rather than by destroying it:

```
execute_string("global.wMenu = 0;
if (instance_exists(TeamSelectController)) { with (TeamSelectController) {
  if (!done) { global.wMenu = 1; team = TEAM_ANY; event_user(1) } } }
if (instance_exists(ClassSelectController)) { with (ClassSelectController) {
  if (!done) { global.wMenu = global.wMenu + 2; class = CLASS_SCOUT; event_user(1) } } }
") * 0 + global.wMenu
```

Each menu's User Event 1 sends the change if it differs, sets `done`, and the
Step event slides it away and destroys it. Neither dereferences anything that a
map change can make `-1` in a way that throws: both compare against
`global.myself.<field>`, and with `global.myself` at `-1` that reads the
controller's own `team`/`class`, which is what was just assigned — so the guard
is "equal, send nothing", not an error. `gg2_input press right;press jump` then
holds through the rest of the run.

### An invalid-byte kick now explains itself

`CommandStream` records the first eight *runs* of unrecognised bytes — where each
started, what command it followed, how long it ran and its first bytes — and
`InvalidCommandReport` puts that in the log the first time a connection skips
anything, and again in the kick reason. A run is the unit that separates the two
causes: one long run means the framer lost alignment and the bytes are a real
message it mis-sized; many short runs of one shape mean the sender is emitting
something the table does not have.

The recurrence happened, and the report settled the shape of it. Against a
remote server the client emits **exactly one stray `00` per framed command**
from about command #854 onward; runs are one byte each and the next command
frames correctly, so nothing is desynchronised. ⚠️ It was sending ~293 commands
a second against a nominal 30 fps, unexplained and never read back, so **per
command** is the measured ratio and **per frame** is not. `docs/HARDENING.md`, "The
one guard that has been seen to fire on a stock client", has the log line and
what it means for the guard.

⛔ **A loopback soak cannot find this.** 49 minutes at 4x with the same client
build, the same plugin, 862 joins and 120 map changes produced zero. The same
client against a VPS 58 ms away produced it inside a minute, twice. Anything
about how a client's writes reach this server has to be measured over a real
path; loopback answers a different question.

The pipelining bug found alongside it (`docs/PROTOCOL.md`, "`PLAYER_JOIN` does
not arrive alone") is **not** the cause — a lost whole command costs a command,
not alignment, and these runs never lose alignment.

### Churning real players, not bots

`soak-transitions.js --plan churn` moves the bot fill up and down, which compacts
the roster inside the server and puts nothing on the wire: a bot is an
`IInputSource` with no socket, so it never reaches `RESERVE_SLOT`, the join burst,
`SkipBroadcastBytes`' flush ordering, or a disconnect driving `PLAYER_LEAVE`.

`tools/soak/` is a fleet of real TCP clients that join, switch team and class,
and leave on a seeded schedule, 70% by `PLAYER_LEAVE` and FIN and 30% by RST. Its
README carries the server flags it needs — two per-address limits refuse a fleet
from `127.0.0.1` by default, and the refusal reads as the tool not working.

What it is for is the off-by-one in CLAUDE.md: a departure from a low slot
renumbers every slot above it, and a watching client tracks that with one line
(`ClientBeginStep.gml:218`). Nothing inside this repo can check that line is
still right. The invariant a watching client should hold is

    global.myself == ds_list_find_value(global.players, global.playerID)

and the README has the guarded watch expression for it. Measured over 72 joins and
72 departures with a stock client attached: the invariant never broke, no
`DESYNC` was recorded, and the client tracked roster sizes 4 through 10.

### What the client's prediction error actually is

`agentSnap` records `|predicted - authoritative|` for x, y, hspeed, vspeed and
hp at the moment a correction lands, inside the frame that lands it. That is the
only place it can be taken: between updates a client's state *is* its
prediction, so an external sampler comparing client against server measures the
dead-reckoning gap plus two round trips of skew and can never report the
correction itself.

Baseline, 30 fps arm, 251,576 corrections over about two hours of game time:

| field | mean | max | over 1 px / 0.5 hp | over 8 px / 1 hp | over 64 px / 5 hp |
|---|---|---|---|---|---|
| x | 0.70 px | 55.6 | 11.9% | 1.3% | 0 |
| y | 0.34 px | 70 | 4.5% | 0.8% | 3 |
| hp | 0.10 hp | 55 | 2.0% | 1.6% | 0.3% |

The same numbers came back at two different boost factors, which is what rules
the boost out as an influence on the measurement. A max in the tens of pixels is
a respawn teleport, not drift; the statistic that matters is that the tail stays
flat as a run gets longer.

**hp is corrected by under a hit point most of the time, and that is the
"health flickers between two values" report.** The wire carries `ceil(hp)` while
the server holds a double, and a client re-simulates hp between updates — a
medic beam moves it 0.667 hp a tick — so the readout alternates between the
client's fraction and the server's integer about four times a second. It is the
wire format, not a divergence, and it needs a continuous hp term to be visible.

### The 60 fps arm diverges about twice as far

`[Settings] Framerate=1` in `gg2.ini` gives a client `global.delta_factor = 1/2`
and `ticks_per_virtual = 2`: the simulation still advances 30 virtual ticks a
second, in two half-steps. The port is hard-wired to the 30 Hz arm
(`Movement.cs`, `ClassStats.cs`). Controlled against the 30 arm at the same
server speed, the same virtual tick rate and matched sample counts:

| | 30 arm (89,611) | 60 arm (92,448) |
|---|---|---|
| x mean | 0.70 px | 1.38 px |
| x max | 55.6 px | 200.8 px |
| x over 64 px | 0 | 102 |
| hp mean | 0.10 hp | 0.22 hp |
| hp over 1 hp | 1.61% | 3.04% |

This does not establish that the port is wrong. GG2's 60 fps mode is a
sub-stepping approximation, so a 60 fps client may diverge from a stock 30 Hz
server by a similar amount. **The control that would settle it is a 60 fps
client against the reference GM8 server**, and it should be run before any
physics change is made on the strength of these numbers.

In the 60 arm the simulation advances at `room_speed / ticks_per_virtual`, so
matching a boosted client to a boosted server means matching virtual ticks, not
frames.

### What ends a soak early

Every timer the game counts in frames arrives sooner in wall-clock time by
exactly the boost factor, so a run long enough to be interesting in game time
trips clocks a real session never reaches.

| Clock | Frames | At 30 fps | At 240 fps |
|---|---|---|---|
| `PlayerControl` AFK | 1800 | 60 s | 7.5 s |
| `Spectator` self-disconnect | 9000 | 5 min | 38 s |

`agentSoak` pins both. Pin `afktimer` as well as `afktimeout` on each: the
timeout is only the value the countdown is reset to, and pinning it alone leaves
the countdown running and still drops the client.

A GM8 client on this hardware tops out near 346 fps. Leave headroom — a client
held at its ceiling drops frames irregularly, and a dialog it has to dismiss
while boosted can stall it long enough for the server to close the connection
with `ConnectionReset`.

### A watch expression that can throw is fatal at a boosted rate

`gg2_watch` evaluates every frame, so an expression that raises does so every
frame, and the launcher dismisses a dialog for each one. At 240 fps that ended a
client's connection. Verify an expression as a one-shot `gg2_evalx` before
installing it as a watch, and guard every dereference: `global.myself` is `-1`
during a map change, and in GM8 `-1` is `self`, so `global.myself.object` then
reads a variable off whatever object is running the expression.

`execute_string` returns a real, so `execute_string(...) + "text"` is a type
error and `execute_string(...) * 0 + value` is how a watch runs statements and
still reports a number.

### `Music=3` does not cover every audio call site

`WinBanner.events/Create.xml` reaches `AudioControlPlaySong` unguarded, so a
**round ending** raises `Unknown variable currentSong` on a machine where that
variable is absent — with `Music=3` set, which only guards
`basicRoomSetup.gml:79`. Observed at every round end, sixteen dialogs in one
soak. The lost sound costs nothing (the call is the last statement in the event,
after `teamoffset` and every player's `humiliated` flag); the dialog is what
costs, because dismissing one while boosted stalls the client.

`agentAudioStopSong` replaces that line, makes the stock script safe to call and
then does what the stock line did: zero occurrences across nine round ends in
both fps arms afterwards.

**Why the variable goes missing is not established.** It is present on
`AudioControl` at the menu, and the misplaced parenthesis in
`AudioControl.events/Create.xml` — `if(instance_number(AudioControl)) > 1 {` — is
**not** the cause: GM8 parses it as intended, measured by running the same shape
against an object with exactly one instance and watching the body not fire.
