# The duel server

A server where every human fights one bot of their own class, alone, in a
small arena of their own. The arenas are cells of one generated map; a human is
spawned into a random free cell and their bot into the same one. Humans are
always red and bots always blue, so the team score reads as humans against bots.

## What exists and what does not

| Part | State |
|---|---|
| Cell maps: generator, an 8-map pool, nav graphs | **Built.** `gg2-server cellgen`, `maps/duel/`, `nav/duel/`, `CellMapTests` |
| A stock client on a cell map | **Measured**: loads, runs at 30 fps, about 10 MB over `cp_egypt`. See "What a stock client showed" |
| Pinning a player's spawn to a cell | **Built**: `PlayerState.SpawnPin` (null = unpinned; stored as id + 1 so a zeroed player is unpinned) replaces `Timers.PlayerSpawn`'s random draw, gated by `SpawnPinTests` |
| Pairing: one bot per human, opposite team, same class, same cell | **Built**: `--duel` / `[Bots] Duel`, `BotPopulationService.Duel.cs`, gated by `DuelServerTests`. A human who picks blue is moved to red; autobalance is held off |
| A human's death kills their bot, after the kill cam, and both respawn in a new cell on the same tick | **Built**: the bot dies by an ordinary class change with its damage credit cleared (`GameServer.RespawnBotUncredited`), so a human's death never scores for red; its respawn countdown is then set to the human's (`GameServer.ArmBotRespawn`) |
| A bot's death respawns its human, with no death, in a new cell | **Built**: `GameServer.RelocateDuelist`, gated by `DuelServerTests.TheBotsDeathHealsAndMovesTheHumanWithTheBot` |
| A bot targets only its partner | **Built**: every bot view carries an opponents table (`WorldView.MayOppose`) that the duel service fills each tick, and every place the bot layer picks an enemy asks it. Gated by `OpponentFilterTests` and `DuelServerTests` |

Run it with `gg2-server --duel --maps maps/duel --botnav nav/duel --map duel_01`.

## The pairing

`BotPopulationService.ServiceDuel` runs every tick in place of the fill.

| Event | What happens |
|---|---|
| A human is on red | They get a free cell (a spawn index nobody else holds) and one blue bot of their class; both are pinned to the cell |
| A human picks blue | Moved to red (`ForceTeam`), since the balance guard would refuse red while a pair waits on its bot |
| The human dies (any death: their death count rose) | A new free cell, never the one just left while another is free, and the human's sentry is destroyed. The bot stays alive for `KillCamTicks` (90, three seconds), or until the human is one tick from respawning, then is re-classed to the human's class with no credit, which kills it, and its respawn countdown is set to the human's so both appear in the new cell on the same tick |
| The bot dies while its human is alive | The human won: a new free cell is picked and pinned at once. After `WinMoveTicks` (45, a second and a half) the human is respawned there with no death (a fresh character: hp, ammo, reload, motion, burning and spawn protection all as a respawn leaves them) and loses their sentry; the bot's respawn countdown is set so it appears there on the same tick. A death during the wait cancels the move, and the death's own rules apply |
| The bot dies while its human is dead | No win; nothing moves beyond the human's death above |
| Every tick | Each bot's only allowed opponent is its own human (`GameServer.SetOpponents`). The bot layer picks targets by distance and walls do not stop that choice, so without it a bot could pick a human in the next cell, 420 px of wall away. While the human waits to respawn the bot has nobody to fight and stands still |
| The human changes class while dead | The bot follows by the ordinary class change |
| The human spectates or leaves | The bot is removed |

A death is detected by the human's scoreboard death count, not by "was alive":
a class change is processed before the population tick, so a human can die
before that tick has ever seen them alive. That relies on every death scoring,
which `ChangeDeathScoringTests` gates for the class and team change.

A freshly spawned character is under Team Deathmatch's spawn protection, and a
protected death respawns on the next tick (`Destroy.xml:37`), so the bot is
killed at once and there is no kill cam to wait for.

**Why the bot outlives its human.** The stock client's `DeathCam` frames the
killer 60 frames after the death (`DeathCam.events/Create.xml`, `alarm[0]`;
`Alarm 0.xml`). A killer with no character by then is drawn as "killed by the
late" at its last known spot, so a bot killed with its human leaves the kill cam
pointing at nothing. Three seconds is that snapshot plus a second for lag; the
server's respawn time is 5 s by default, so the human never waits on the bot.

**A duel bot spawns ready.** An ordinary bot holds still for a random 0-90
ticks after spawning until a team-mate is near (`BotConstants.RegroupTicks`),
and picks its first objective at the next 30-tick `ObjectivePeriod`; in a duel
there is no team-mate, so that was up to four seconds stood in front of its
human. `ScriptedBot.SpawnsReady`, set only on a duel server, skips the hold and
decides on the life's first tick: measured on `duel_01`, a soldier, scout and
heavy each press a direction within 3 ticks of spawning (it was 20 for the
first soldier). Gated by `DuelServerTests.ADuelBotMovesOffItsSpawnAtOnce`.

**A bot holds fire at a spawn-protected target.** Pressing `Attack` or
`Special` ends a character's own protection whether or not anything fires
(`Begin Step.xml:85`, `:124`), and damage to a protected victim is refused, so a
bot that opened fire on sight lost its protection and a clip to a human who
could then shoot it for free: every class fired 15-20 ticks into a life
against a standing human with 120+ ticks of protection left. `BotSpawnProtection`
strips `Attack` (and `Special` for the medic, or for anybody still protected
themselves) while the target is protected, keeps the aim, and covers both the
scripted layer and the policy nets, which fall back to the nearest allowed enemy
when the scripted layer has no target yet. Every other bot match runs with
protection off, so it changes nothing there. Gated by
`DuelServerTests.ADuelBotHoldsFireWhileItsHumanIsSpawnProtected`, which goes red
with either half removed. Not addressed: a bot still walks in and spends its
own protection five times as fast as a human standing still, so a human who
waits and shoots first still gets the first hit.

**What the client sees of a win.** A `PLAYER_SPAWN` for a player who is
alive: `doEventSpawn.gml` destroys the client's character and creates a new one
at the point, the same as the server's `WorldState.Spawn`, so nothing is
patched field by field and nothing is left for the client to disagree about.
The client's `Character.events/Destroy.xml` runs on the way; the one rule of it
that reaches the new character, carrying TDM spawn protection still running,
is mirrored on the server. No death is scored. The sentry goes by
`DESTROY_SENTRY`'s body, seen through the subobject bit. Mines and projectiles
already in flight stay in the old cell.

## The cell maps

`CellMapGenerator` (`src/GG2.Host/`) cuts each cell from a shipped map and
packs the cells onto a solid field. A map is a pure function of its
`CellMapOptions` and the shipped maps, so the pool is regenerated rather than
edited:

```powershell
gg2-server cellgen                                   # the pool, into maps/duel/
gg2-server navgen --maps maps/duel --out nav/duel    # its graphs and tables
gg2-server cellgen --check                           # exit 1 if a pool map is stale
```

| Rule | Why |
|---|---|
| A cell is a 4-connected flood fill of open space from one floor cell, bounded by a random window; everything the fill does not reach is solid | The cell's shape is the source's own geometry, so cells are not square |
| 30-80% of the window must be open | Less is a corridor, more is open sky with nothing to fight round |
| At least 40% of the places a character can stand have 240 px (40 cells) clear straight up | A low cell takes the vertical half of the game from the scout's double jump and the soldier's and demoman's blast jumps (`TallHeadroom`, `MinTallShare`) |
| Two spawns, one in each outer 40% of the window, each with a 36x54 px clear column over two floor cells | A spawn has to land; `CellMapGenerator.CanStand` |
| The spawns reach each other both ways over walk, fall, jump and drop-through edges, on the nav graph of the cell built as a map of its own | Every class can reach its opponent; rocket and double jumps are extra routes, never the only one |
| Every surface reachable from a spawn leads back to it, over the same edges, and there are at least 8 | No pit strands whoever falls in, and no cell is a box with a floor. Measured on the first pool: 34 of 192 cells had a one-way pocket and some reached only 3-4 surfaces |
| A window touching a `pitfall` (or kill/frag box) is refused | Its art promises a death the cut map would not deliver |
| The bottom-left mask cell is open, alone in the solid field | The stock client writes the walkmask out one pixel per cell and loads it with `sprite_add(..., removeback = true, ...)` (`CustomMapProcessLevelData.gml:22`), which makes the bottom-left pixel's colour transparent. With that cell solid the client inverts the whole mask: its character is embedded in wall everywhere, cannot predict a step, and moves only when a server correction lands, so play looks laggy and wrong while the server is healthy. Every shipped map has the cell open; the cell is `Gap` cells from any cell, so nothing reaches it. Gated by `CellMapTests.TheBottomLeftMaskCellIsOpen` and, for the shipped maps the rule was read from, `EveryShippedMapHasAnOpenBottomLeftMaskCell` |
| Dropdown platforms and invisible player/bullet walls wholly inside the window are kept; every other entity is dropped | The art draws the first and the geometry relies on the second; gates, objectives, spawn rooms, cabinets, doors and move boxes would each change the duel |
| Spawns sit 42 px above their floor | The commonest offset among the 227 shipped spawn points (range 26-66) |
| Cell `i` holds red spawn `i` and blue spawn `i` | The index a pinned spawn will be sent with in `PLAYER_SPAWN` |
| Art: the source's own pixels for the cell plus a 2-cell rim, flat `#28282f` everywhere else, also the `meta` background | The walls round a cell are the source's, and the field compresses to nothing |
| Sources: every built-in map file except `gg_debug`, by file not by name | `InternalMaps` has aliases; three names for `ctf_2dfort.png` would triple its weight |

The map has no objective, so it is Team Deathmatch (`docs/TRAPS-SIMULATION.md`,
"Team Deathmatch"). Its name is not built in, so a stock client downloads it
from the server on join (`docs/DEBUG-MAPS.md`, "A stock client can join it").

### The pool

`CellMapGenerator.Pool`: `duel_01` to `duel_08`, seeds 1-8, each 24 cells of
150x100 mask cells (900x600 px) with a 70-cell (420 px) gap, six columns by
four rows. Every map is 1390x750 mask cells, 8340x4500 px, 51-80 KB; the pool
is 513 KB of maps and 2.5 MB of graphs and next-hop tables. 19 of the 21
source maps supply cells; `gen_destroy` the most (44 of 192), `ctf_orange` next
(24).

| Choice | Reason |
|---|---|
| 900x600 px | Wider than the 800 px view, so a duel has room to back off and flank, and as tall as it, so a jump has somewhere to go |
| 24 cells | `PlayerLimit` is at most 48 (`ServerOptions.Validate`, the reference's own clamp), so at most 24 humans with a bot each. A server expecting fewer humans still gains variety from spare cells |
| 420 px gap | Blasts ignore walls (`Explosions`: rocket and sentry 65 px, mine 40), so anything over 130 px is safe; the rest is about the screen, because the client draws every character whatever is between. Half an 800 px view keeps a neighbour's fight off screen while you are inside your cell; "What a stock client showed" has what was seen |
| A separate directory | The default `maps/` and `nav/` feed every shipped-map census, the whole-cache pins and `eval`; a duel server names the duel directories instead. `GG2.Host.csproj` ships both |

File size is not a constraint at any cell count the player limit allows: a
48-cell map with 780x480 cells and the same gap is 104 KB as truecolour.

### Known issue: in some cells the bot cannot finish a cornered opponent

Measured 2026-10-28 on `duel_01` with `--duel`, a stationary human and a 60 s
cap per life, about 70 lives a class. Most kills take 7-15 s, spawn protection
included. In a few cells the bot never kills: soldier cells 4, 16 and 20, and
scout cell 0. A trace of one such cell (on the pool before its regeneration):
once protection ends the bot's rocket knocks the human into a hollow a ledge
below, and the bot then stands on that ledge, sees its target, never fires,
and fails the same two-node path down for the rest of the life. The cell has no
one-way pocket, so this is the bot layer, not the geometry. A moving human is
not caught like this as easily, which is why it is written down rather than
chased; a stationary dummy is the worst case. Re-measure by pinning a human to
each cell through `DuelServerTests`' harness and counting ticks to the first
death.

### What `CellMapTests` gates

- each checked-in map equals its pool entry's output, by level data and art (not
  bytes, which depend on the zlib build), and the directory holds nothing else;
- each is custom-named, single-area Team Deathmatch;
- spawn index equals cell index, inside the cell's window;
- nothing outside the cells is open, and the gap is over twice the rocket radius;
- all ten classes, spawned at all 48 spawns at once, are alive and on the ground
  inside their own cell after 60 ticks of the real simulation;
- on the shipped graph, each cell's partners reach each other both ways, every
  surface the red spawn reaches leads back to it, and there are at least eight;
- the shipped graphs are what the nav generator builds, character for character.

Two were checked by breaking them: spawning blue into the next cell fails the
landing test, and counting walk edges alone fails reachability on several cells
(those duels need a jump, which is why jump is in the set).

### What a stock client showed

Measured 2026-10-28 with the stock client against `duel_01` (server and six
bots on this machine):

| Question | Result |
|---|---|
| Does it download, load and run the map? | Yes: downloaded on join, `CustomMapRoom`, 30 fps |
| Does it predict its own character? | Yes, with the bottom-left mask cell open (measured on `duel_04`, 2026-11-12: holding left, hspeed ramps -1.07 to -6.08 and x advances by it every frame, stopping at a wall where the server's x agrees to 0.001 px). With that cell solid it did not: the client's mask round its character was the exact complement of the server's, and x moved only on corrections, about four times a second |
| A player who downloaded an older copy | The client compares MD5s on join and asks "The server's copy of the map (duel_04) differs from ours. Would you like to download this server's version of the map?"; yes downloads it. Regenerating the pool puts that prompt in front of every returning player |
| What does the bigger map cost the client? | 287 MB working set and 656 MB private, against 284 and 647 MB on `cp_egypt` in the same session shape: about 10 MB, so map size is not what limits the cell count |
| What is on screen from inside a cell? | The cell, its edge, and the flat field; no other cell, from the one spawn looked at |

Not yet measured, and what each would change:

| Question | If the answer is bad |
|---|---|
| Standing at a cell's very edge, how much of the neighbour shows? The gap is 420 px and the view 800, so the far cell's edge sits about 20 px inside the screen | Widen the gap |
| A rocket fired into a dividing wall. The server decides damage, and no blast reaches 130 px, so this can only show a client-side explosion drawn at the wall | Nothing should; anything else is a client divergence worth a trace |

The server side: it loads a cell map as Team Deathmatch and builds the nav graph
in 243 ms when none is shipped; with `nav/duel/` it reads it.
