# Driving the live game

⚠️ **The traps that only bite a session holding the running Game Maker 8 game** —
recording a trace, driving a stock client from the outside, and writing a probe
that measures the engine rather than measuring itself.

⛔ **Every one of these was paid for by a session that lost hours to it**, and
most of them look identical to a broken bridge from the outside. That is the
whole reason they are written down: the failure mode is never an error message,
it is a character that does not move.

⚠️ **Split out of `CLAUDE.md` on 2026-09-01.** It sat there for six phases,
loaded into every session's context whether or not that session was going
anywhere near the game — which is exactly the cost that split the traps files in
the first place. Nothing here changed in the move; the anchors and the
`file:line` citations are as they were.

⚠️ **Read `docs/PARALLEL.md` first if more than one session is running.** The
bridge serves ONE TCP client and exactly one lane may hold it, and restoring
`gg2.ini` empties another lane's roster mid-measurement.

### Recording a trace with something in it

The reference's own bots are the cheapest source of real physics. In the build
directory's `gg2.ini` set `[Bots] Enabled=1` and a small `FillToPlayers`, run the
dedicated server, record, then **put the ini back**. `gg2.ini` lives in
`Source/build/`, which is gitignored, but it is still the user's config.

```powershell
gg2-parity record --port 8190 --seconds 30 --out fixture.trace --source "..."
gg2-parity replay fixture.trace
```

An `--out` ending in `.trace` gets the header that makes it replayable; anything
else is written raw, which is what a byte diff against another server wants.

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
dialogs, and **saves a screenshot of each one**, which is usually the whole
explanation for a client that stopped working. Screen-capture the window by its
`TRunnerForm` rect — `PrintWindow` returns black for GM8's renderer.

### ⚠️ Driving a client: `menuOpen` eats every movement key

✅ **Answered 2026-08-27.** `gg2_input` DOES drive a stock client — what stops
it is the team-select overlay.

**`PlayerControl.events/Begin Step.xml` gates every movement key behind
`if(!menuOpen)`**, and `menuOpen` is true while `TeamSelectController` or
`ClassSelectController` exists. **Picking a team by injecting
`ClientPlayerChangeteam` over the socket bypasses the UI**, so the overlay is
never closed: the server hands out a character, the mask reaches the bridge
(`AgentBridge.heldMask = 32`), and `keyState` stays **0**.

The recipe, verified before and after:

```
gg2_eval: ClientPlayerChangeteam(TEAM_RED, global.serverSocket);
          socket_send(global.serverSocket);
gg2_eval: with(TeamSelectController) instance_destroy();
          with(ClassSelectController) instance_destroy();
```

With the overlay gone, `keyState` went 0 → 32 and the character walked
(x 1704 → 1967). Without it, nothing moves and it looks exactly like the bridge
being broken.

### ⚠️ Driving a client: a watch expression is the only per-frame hook there is

✅ **2026-08-27, and it is what made a driven client actually usable.** A tool
round trip is 30-100 game frames, so anything needing frame accuracy — a jump, a
stop inside a 60-pixel window — cannot be issued from outside. **`gg2_watch`
evaluates its expression every single frame**, and `execute_string` is a
function, so a watch expression can *run statements*:

```
gg2_watch add drive
  execute_string("with(AgentBridge) { if (global.myself.object.x > 2300 and global.myself.object.x < 2360) heldMask = 0 else heldMask = 160 - (heldMask & 128) }")
```

That one line is a **bunny-hop with an automatic stop**. Both halves matter:

- ⚠️ **GG2 jumps on the PRESSED edge, so a held jump fires once.** `press jump`
  climbs exactly one step and then the character sits there looking wedged —
  which cost this session several minutes of thinking the bridge was broken.
  Toggling bit 128 every frame is a jump every other frame, and **bunny-hopping
  climbs terrain a single jump cannot**: it took a character from a room floor
  at y=858 up onto a ledge at y=525 in one round trip.
- ⚠️ **Write the mask absolutely, never with `|`.** `heldMask = heldMask | 32`
  leaves an earlier `left` bit set and the character walks the wrong way —
  measured. `160 - (heldMask & 128)` alternates exactly 160/32 (jump+right,
  right) and clears everything else.
- **`gg2_input`'s bits: right = 32, jump = 128** (`keyState` 160 is both).

⚠️ **Probing `place_free` from a client needs `with(CustomMapO) solid = true`
first.** The map instance is not flagged solid at the moment the bridge's Step
runs, so an unprepared scan answers "free" at every point of every column and
looks like a broken query rather than an empty room. With it, a column scan
recording each free→solid transition is the cheapest way to read a map's
walkable structure — far better than reading a walkmask screenshot by eye, which
this session tried first and got wrong twice.

⚠️ **And a client returning to spectator is usually NOT the AFK timer.** Measured
with `afktimer` pinned at 9,999,999 and `TeamSelectController.afk = 0`: the
revert came from the **server**, which sets every roster entry to
`Team.Spectator` on a map change (`GameServer.cs`, citing the reference's
`with(Player) ... team = TEAM_SPECTATOR`). Check `PlayerControl.afktimer` before
blaming AFK.

⚠️ **A driven client goes AFK after 60 seconds and un-picks its team.**
`PlayerControl.events/End Step.xml` counts `afktimeout = 60*30` frames down and
then forces the team-select screen, which sends `PLAYER_CHANGETEAM(spectator)`
and destroys the character. Once the timer has already expired every *later*
attempt to join a team dies on its first frame, because the fresh character has
`afk = false` and the check fires again immediately — so it looks exactly like
the server refusing to spawn anyone. The reset is `keybyte != 0` **or** a real
`keyboard_check`, and the bridge's injected mask does not satisfy it. Turn it
off before driving anything:

```
gg2_eval: with(PlayerControl) { afktimeout = 999999; afktimer = 999999; }
```

⚠️ A spectating client disconnects itself after **exactly five minutes**
(`Spectator.events/Create.xml`, `afktimeout = 9000`). It does that to the
reference server too. Do not go looking for a server-side bug.

### Writing a probe against the live game

Both oracles work by driving the running engine from a state chosen exactly and
reading the result back. Phase 5 needs the same trick for damage and
projectiles, and these are the things that cost a session a day each:

- **The game keeps stepping between `gg2_eval` calls.** A scenario split across
  two of them measures the bot, not your inputs. Run the whole thing inside one
  eval, or freeze with `gg2_step`.
- **Do not send a start state as a decimal literal.** GM8's parser is one ULP
  off for some values — the same bug as the physics constants above. Send
  integers and ratios and let the game divide.
- **`string_format(v, 1, 17)` is only good to ~16 significant digits** and the
  last one is noise. For ULP work pull the mantissa out in chunks, each under
  2^19, because GM8's `floor()` wraps at 32 bits. **Use
  `tools/exact-double.js`** — do not write another one, three of the four that
  existed were wrong. `node tools/exact-double.js` self-tests it.
- **A probe character inherits its bot's `player.team`,** and `charSetSolids`
  makes the *other* team's gates solid — set `team` explicitly or the character
  walks into a wall the recording walked through.
- **`instance_number`/`instance_find` include an object's descendants**, and
  `Character` is a parent with no direct instances: `Character.x` is an error,
  `global.myself.object.x` is not.
- **`place_free` at the top level of `gg2_eval` runs in the *bridge's* scope.**
  Wrap it in `with(...)`.
- **`pi` is a GM8 constant**, so `for(pi=0; ...)` is a compile error reported as
  "Variable name expected".
## The samplers, the probes, and handing over the socket

⚠️ **Moved out of `CLAUDE.md` on 2026-09-01, verbatim.** It sat under "Building
and testing", where every session read it and almost none of them was going to
run a sampler. Nothing below was re-authored.

`tools/sample-collision.js`, `tools/sample-physics.js`, `tools/sample-rng.js`,
`tools/sample-primitives.js`, `tools/sample-collision-line.js`,
`tools/sample-motion.js` and `tools/sample-rotated-box.js` regenerate the engine
oracles. All seven need a **running game with the agent bridge**; the physics and
primitives ones also need at least one bot character, and the RNG,
collision-line, motion and rotated-box ones are happy with any running instance — the last two create and scale their own probe rather than
borrowing a bot's character, so a dedicated server with an empty match is enough.

`tools/probes/` holds the one-off probes that settled a single question rather
than generating a fixture — the sprite boxes, the bubble pair, the sentry mask,
the airblast, the turret. Read the header of one before writing another: each
records what it cost to get right.

`collision-samples.json` was regenerated on 2026-08-25 with `BulletWall`
enumerated and `arena_lumberyard` added, so `CollisionSampleTests` compares every
rectangle the port builds. **`gg_debug` places bullet walls too** — the docs used
to name only `arena_lumberyard` and `koth_harvest`.

⚠️ **The bridge serves one TCP client at a time and the MCP server process holds
it.** Once this session has called any `gg2_*` tool, a `node tools/sample-*.js`
run times out on its first call — even `gg2_ping` — and the game is fine. Hand
the socket over deliberately: stop the game, restart it with
`node D:/code/gg2-agent/run-agent.js --port 17777 --name server --role server -- -dedicated -map ctf_truefort`,
and do not touch `gg2_*` until the tool has finished. The other direction is
free, because each CLI run exits and releases.

⚠️ **Restoring `gg2.ini` empties the running server's roster.** The dedicated
server re-reads it, so putting the bots setting back mid-session removes every
bot and the next probe measures nothing at all. Restore it last.

To bring a bot server up: set `[Bots] Enabled=1`, `FillToPlayers=4`,
`MinHumans=0` in `gg2.ini`, start the server, and put the ini back when you are
finished with the game.

