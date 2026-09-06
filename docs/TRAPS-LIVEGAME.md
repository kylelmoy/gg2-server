# Driving the live game

⚠️ **The local bot fork was deleted on 2026-09-05, and `D:\code\Gang-Garrison-2` now holds stock upstream GG2 instead.** The GM8 game was built and run
from that fork's `Source/build/`, so **nothing in this document runs as written
until you re-clone it** (`git clone https://github.com/kylelmoy/Gang-Garrison-2.git`,
`git checkout bot-route-variety`, then rebuild `Source/build/`). The paths below
still name `D:\code\Gang-Garrison-2`, which today resolves to stock GG2 — a tree
with no `Source/build/`, no bots and no bridge.

The traps that only bite a session holding the running Game Maker 8 game —
recording a trace, driving a stock client from the outside, and writing a
probe that measures the engine rather than measuring itself.

⛔ Most of these look identical to a broken bridge from the outside — the
failure mode is never an error message, it is a character that does not move.

⚠️ **Read `docs/PARALLEL.md` first if more than one session is running.** The
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

- ⚠️ **GG2 jumps on the PRESSED edge, so a held jump fires once.** `press jump`
  climbs exactly one step and then the character sits there looking wedged.
  Toggling bit 128 every frame is a jump every other frame, and
  **bunny-hopping climbs terrain a single jump cannot**.
- ⚠️ **Write the mask absolutely, never with `|`.** `heldMask = heldMask | 32`
  leaves an earlier `left` bit set and the character walks the wrong way.
  `160 - (heldMask & 128)` alternates exactly 160/32 (jump+right, right) and
  clears everything else.
- **`gg2_input`'s bits: right = 32, jump = 128** (`keyState` 160 is both).

⚠️ **Probing `place_free` from a client needs `with(CustomMapO) solid = true`
first.** The map instance is not flagged solid at the moment the bridge's Step
runs, so an unprepared scan answers "free" at every point of every column and
looks like a broken query rather than an empty room. With it, a column scan
recording each free→solid transition is the cheapest way to read a map's
walkable structure — far better than reading a walkmask screenshot by eye.

⚠️ **A client returning to spectator is usually NOT the AFK timer.** With
`afktimer` pinned high and `TeamSelectController.afk = 0`, a revert can still
come from the **server**, which sets every roster entry to `Team.Spectator` on
a map change (`GameServer.cs`, citing the reference's `with(Player) ... team =
TEAM_SPECTATOR`). Check `PlayerControl.afktimer` before blaming AFK.

⚠️ **A driven client goes AFK after 60 seconds and un-picks its team.**
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

⚠️ A spectating client disconnects itself after **exactly five minutes**
(`Spectator.events/Create.xml`, `afktimeout = 9000`). It does that to the
reference server too — do not go looking for a server-side bug.

### A client-side watchdog latch, and how to prove it can fire

`gg2_watch` samples up to eight expressions **every frame** and logs only what
*changes*, which is the shape a long unattended run needs: a sticky latch logs
one line when it trips and nothing for the hours either side. `execute_string`
makes a watch expression run statements, so the whole check can be one watch
that scans the roster and writes `global.wdCode`, with a second watch reading
that global back. Measured 2026-09-05: a full `Player`-plus-`Character` scan
every frame cost **no observable fps** on a six-bot `cp_dirtbowl` client
(27 fps before, 27 after; room speed 30).

⚠️ **Give the checker a self-test input — a gate that cannot fire is worse than
no gate.** A spare global (`global.wdTest`) that forces the latch is enough:
set it, confirm the latch trips within a frame, reset it. That proves the
watch is running, the sticky write works and the log records it, all without
touching game state.

⛔ **Do NOT prove it by corrupting the roster.** Pointing a spectator
`Player.object` at a live instance to manufacture the condition **hung the
client** — GM8 stopped servicing the bridge entirely, with no dialog for the
launcher to dismiss and nothing in any log. `Player.object` is dereferenced as
a `Character` by code that never checks, so anything else there is a wedge, not
an error. The self-test global costs nothing and is safe.

The invariant worth latching on is narrower than it looks. `TEAM_SPECTATOR` is
**2**, and `getCharacterSpriteId.gml:20` accepts only team 0 and 1 — so the
`unknown team ID: 2` fatal is not a framing slip producing a garbage value, it
is `Character.events/Create.xml:69` making a character for a player the client
holds as a **spectator**. It fires at *create*, so the state never survives to
be observed: watch the roster's transitions leading in, not the aftermath.

### ⛔ `Music=1` truncates every client's room setup on a machine with no audio

**Measured 2026-09-05, and it invalidates client observations rather than
merely adding noise.** A GG2 client launched here with `Music=1` in
`Source/build/gg2.ini` throws inside `basicRoomSetup`:

```
EXECUTION ERROR in creation code of room CustomMapRoom
In script basicRoomSetup: In script AudioControlPlaySong:
  if(AudioControl.currentSong != -1) sound_stop(AudioControl.currentSong);
  at position 18: Unknown variable currentSong
```

`AudioControl` has no `currentSong` when there is no audio device — the same
situation `launcher.js`'s own header calls routine over RDP. The launcher
presses **Ignore**, and GM8's Ignore abandons the rest of the script. The call
is `basicRoomSetup.gml:80`, so **everything from line 82 down never runs**:

- `instance_create(..., Spectator)` (`:82`) — **there is no `Spectator`**.
- `global.redCaps`, `global.blueCaps`, `global.winners` (`:84-86`) — never
  initialised, so `CTFHUD`'s Step throws `Unknown variable winners` **every
  frame**: 962 errors in 25 s, 1755 in another run.
- Whatever `:88` onward does, including the MOTD.

⚠️ **The visible symptom points at the wrong thing.** A thousand `CTFHUD`
errors reads like a HUD or gamemode bug in the port; it is one dismissed audio
dialog upstream. The port was innocent — the identical run against a server
built without the change under test produced the identical errors, which is
the only reason it was not written up as a regression. **Bisect a client error
storm against an unmodified server before believing it is yours.**

⚠️ **What it invalidates**: anything that reads `global.winners`,
`global.redCaps`/`blueCaps`, or `Spectator` — including
`Spectator.afktimeout`, the five-minute spectator disconnect, and any
`wdWins`-style counter in the watchdog latch. All of them silently measure a
client whose room setup stopped two thirds of the way through.

**The fix is one line and it is in a runtime file, not the reference source.**
`Source/build/gg2.ini`, `Music=3` (`MUSIC_NONE`, `Constants.xml:127`) — then
`basicRoomSetup.gml:79`'s guard skips the call and the script completes.
Verified immediately: **zero** GML errors over several minutes of a joined
client, `winners=-1`, `spectators=1`. It was set to `3` on 2026-09-05 and
should stay there; `gg2.ini` is a gitignored build artifact that the tooling
already rewrites, so this is not a reference-tree edit. ⚠️ If a future client
log fills with `Unknown variable winners`, check this setting first — something
restored the file.

### The frame a joining client does not know itself, and the errors it prints

**Settled 2026-09-05 by reading the reference, not by another soak.** A stock
client that has just joined prints, for a handful of frames and then never
again:

```
ERROR in ... of End Step Event for object Spectator:
Error in code at line 4: if(global.myself.object != -1)
at position 19: Unknown variable object
```

…plus the same thing from `AmmoCounter`'s Draw (line 8) and Begin Step
(line 1). ⛔ **This is stock GG2 behaviour and not a port bug.** Do not spend a
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
race between the client's 30 Hz frame and the server's. **That is why two
clients joined the same way seconds apart produce 21 errors and zero** — the
2026-09-05 `c1`/`c2` asymmetry recorded as "the strongest lead of the session"
in `.claude/notes/evidence-0905-joinrace/`. It heals on the frame the client's
own `PLAYER_JOIN` arrives.

⚠️ **What it is NOT**: it is not the `cp_dirtbowl` fatal's precondition, and it
does not move a roster count. `docs/PARITY.md`, "Nothing moves a roster count
inside the join window".

⛔ **The dangerous cousin is the same error that never heals.** The bind is
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

⚠️ **The bridge serves one TCP client at a time and the MCP server process
holds it.** Once a session has called any `gg2_*` tool, a
`node tools/sample-*.js` run times out on its first call — even `gg2_ping` —
even though the game is fine. Hand the socket over deliberately: stop the
game, restart it with
`node D:/code/gg2-agent/run-agent.js --port 17777 --name server --role server -- -dedicated -map ctf_truefort`,
and do not touch `gg2_*` until the tool has finished. The other direction is
free, because each CLI run exits and releases.

⚠️ **Restoring `gg2.ini` empties the running server's roster.** The dedicated
server re-reads it, so putting the bots setting back mid-session removes every
bot and the next probe measures nothing at all. Restore it last.

To bring a bot server up: set `[Bots] Enabled=1`, `FillToPlayers=4`,
`MinHumans=0` in `gg2.ini`, start the server, and put the ini back when you
are finished with the game.
