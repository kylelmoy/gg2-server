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
| A human's death kills their bot, and both respawn in a new cell | **Built**: the bot dies by an ordinary class change with its damage credit cleared (`GameServer.RespawnBotUncredited`), so a human's death never scores for red |
| A bot targets only its partner | **Not built.** Bots in a cell map today target whoever their layer picks; walls keep them apart, but a bot whose partner is dead can route toward an unreachable human |

Run it with `gg2-server --duel --maps maps/duel --botnav nav/duel --map duel_01`.
Until partner-only targeting exists, a bot targets whoever its layer picks;
the walls keep everyone else out of reach, but a bot whose human is dead can
plan a route toward an unreachable human.

## The pairing

`BotPopulationService.ServiceDuel` runs every tick in place of the fill.

| Event | What happens |
|---|---|
| A human is on red | They get a free cell (a spawn index nobody else holds) and one blue bot of their class; both are pinned to the cell |
| A human picks blue | Moved to red (`ForceTeam`), since the balance guard would refuse red while a pair waits on its bot |
| The human dies (any death: their death count rose) | A new free cell, never the one just left while another is free; the bot is re-classed to the human's class with no credit, which kills it and arms its respawn there |
| The bot dies | Nothing moves; the bot respawns in the same cell |
| The human changes class while dead | The bot follows by the ordinary class change |
| The human spectates or leaves | The bot is removed |

A death is detected by the human's scoreboard death count, not by "was alive":
a class change is processed before the population tick, so a human can die
before that tick has ever seen them alive. That relies on every death scoring,
which `ChangeDeathScoringTests` gates for the class and team change.

A freshly spawned character is under Team Deathmatch's spawn protection, and a
protected death respawns on the next tick (`Destroy.xml:37`): a human who dies
within moments of spawning finds their bot already waiting in the new cell.

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
| Two spawns, one in each outer 40% of the window, each with a 36x54 px clear column over two floor cells | A spawn has to land; `CellMapGenerator.CanStand` |
| The spawns reach each other both ways over walk, fall, jump and drop-through edges, on the nav graph of the cell built as a map of its own | Every class can reach its opponent; rocket and double jumps are extra routes, never the only one |
| Every surface reachable from a spawn leads back to it, over the same edges, and there are at least 8 | No pit strands whoever falls in, and no cell is a box with a floor. Measured on the first pool: 34 of 192 cells had a one-way pocket and some reached only 3-4 surfaces |
| A window touching a `pitfall` (or kill/frag box) is refused | Its art promises a death the cut map would not deliver |
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
100x67 mask cells (600x402 px) with a 70-cell (420 px) gap. Every map is
1090x618 mask cells, 6540x3708 px, 35-46 KB; the pool is 336 KB of maps and
1.4 MB of graphs and next-hop tables.

| Choice | Reason |
|---|---|
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
| Does it download, load and run the map? | Yes: downloaded on join, `CustomMapRoom`, 30 fps, joined red and spawned on the floor of its cell |
| What does the bigger map cost the client? | 287 MB working set and 656 MB private, against 284 and 647 MB on `cp_egypt` in the same session shape: about 10 MB, so map size is not what limits the cell count |
| What is on screen from inside a cell? | The cell, its edge, and the flat field; no other cell, from the one spawn looked at |

Not yet measured, and what each would change:

| Question | If the answer is bad |
|---|---|
| Standing at a cell's very edge, how much of the neighbour shows? The gap is 420 px and the view 800, so the far cell's edge sits about 20 px inside the screen | Widen the gap |
| A rocket fired into a dividing wall. The server decides damage, and no blast reaches 130 px, so this can only show a client-side explosion drawn at the wall | Nothing should; anything else is a client divergence worth a trace |

The server side: it loads a cell map as Team Deathmatch and builds the nav graph
in 243 ms when none is shipped; with `nav/duel/` it reads it.
