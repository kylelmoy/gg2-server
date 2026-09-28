# Where everything lives in the reference

An index into `Source/gg2` of the reference checkout (GG2 v2.9.2), so a session
does not spend its first hour finding things. Paths are relative to that
directory.

## The two file formats

Scripts under `Scripts/` are plain `.gml`. **Object event code is not** — it
lives inside XML, in `<argument kind="STRING">` elements, and is XML-escaped:

```xml
<argument kind="STRING">if (dist &lt; closestDist or closestDist == -1)</argument>
```

So `grep` misses a large part of the game's logic — roughly 940 KB of event text
across 19 object directories. Two ways to read it:

```powershell
# unescape one event by hand
cat "Objects/InGameElements/Character.events/Step.xml" |
  sed -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&amp;/\&/g'
```

The live-game bridge's `gg2_find` (scripts and unescaped event text together)
and `gg2_event` tools exist precisely for this and are worth using if that MCP
server is available in the session.

## Constants

`Constants.xml` — the whole table, ~250 entries: message ids, teams, classes,
key bits, stat indices, and the `NAV_*` / `BOT_*` tuning sets. Transcribe it
wholesale in phase 0.

## Networking

| What | Where |
|---|---|
| Server tick, service loop, state cadence, map change | `Scripts/GameServer/GameServerBeginStep.gml` |
| Broadcast fan-out, accept, joining-player service | `Scripts/GameServer/GameServerEndStep.gml` |
| Server startup, roster slot 0, loopback socket | `Scripts/GameServer/GameServerCreate.gml` |
| Join handshake state machine | `Scripts/GameServer/serviceJoiningPlayer.gml` |
| Handshake state constants | `Objects/InGameElements/JoiningPlayer.events/Create.xml` |
| Client → server command framing and handlers | `Scripts/GameServer/processClientCommands.gml` |
| Command payload length table | `Scripts/GameServer/GameServerDefineCommands.gml` |
| Accept + multi-client limit | `Scripts/GameServer/acceptJoiningPlayer.gml` |
| **Every server → client message the client understands** | `Scripts/Client/ClientBeginStep.gml` |
| Client connect + protocol UUID send | `Scripts/Client/ClientCreate.gml` |
| Individual message writers | `Scripts/Message Builders/*.gml` |
| Length-prefixed string read/write, blocking receive | `Scripts/Networking/*.gml` |
| Endianness (`set_little_endian_global`) | `Scripts/Game/game_init.gml:23` |
| Command-line flags, ini parsing, dedicated mode | `Scripts/Game/game_init.gml` |

## State serialisation

| What | Where |
|---|---|
| Update tiers, player loop, full/caps tails | `Scripts/Serialization/serializeState.gml` |
| The client's mirror of it | `Scripts/Serialization/deserializeState.gml` |
| Per-player block (stats, subobject flags) | `Objects/InGameElements/Player.events/User Event 12.xml` |
| **Per-character block — position, velocity, hp** | `Objects/InGameElements/Character.events/User Event 12.xml` |
| Client-side counterparts | the matching `User Event 13.xml` |

`User Event 12` is serialise, `User Event 13` is deserialise, consistently
across `Player`, `Character`, weapons, `MovingPlatform` and `HUD`.

## Physics

| What | Where |
|---|---|
| Input, jump, friction/acceleration, gravity accumulation | `Objects/InGameElements/Character.events/Begin Step.xml` |
| Speed clamp, gravity split, the move, dropdown platforms | `Objects/InGameElements/Character.events/Step.xml` |
| Stair descent, death, bounds clamp, cloak/taunt/eat | `Objects/InGameElements/Character.events/End Step.xml` |
| Bounding box from sprite mask, class defaults | `Objects/InGameElements/Character.events/Create.xml` |
| Per-class run power, hp, abilities | `Objects/Characters/*.events/Create.xml` |
| **The movement solver** | `Scripts/Physics/characterHitObstacle.gml` |
| Accurate `move_contact_solid` reimplementation | `Scripts/Physics/good_move_contact_solid.gml` |
| Which objects are solid, and when | `Scripts/Physics/charSetSolids.gml`, `charUnsetSolids.gml` |
| Framerate scaling helpers | `Scripts/Physics/delta_mult.gml`, `delta_mult_skip.gml` |
| Tick-rate globals | `Objects/RateController.events/`, `Documentation/RateController.md` |
| Character mask rectangles | `Sprites/**/<Class><Team>S.xml` |

## Maps and collision geometry

| What | Where |
|---|---|
| Walkmask sprite creation, entity block extraction | `Scripts/Maps/CustomMaps/CustomMapProcessLevelData.gml` |
| Entity instantiation from the entity block | `Scripts/Maps/CustomMaps/CustomMapCreateEntitiesFromEntityData.gml` |
| Walkmask compression | `Scripts/Maps/CustomMaps/compressWalkmask.gml` |
| Map dimensions | `Scripts/Maps/CustomMaps/map_width.gml`, `map_height.gml` |
| The 6× scale on the collision instance | `Objects/Collision maps/CollisionMapO.events/Create.xml` |
| Solid object set | anything with `<solid>true</solid>`: `CollisionMapO`, `CustomMapO`, `Obstacle`, `PlayerWall`, `PlayerWallHorizontal` |
| Gates, doors, platforms | `Objects/Map elements/Colliders/` |
| Map rotation, map change | `Scripts/Maps/nextMapInRotation.gml`, `serverGotoMap.gml` |

## Gameplay

| What | Where |
|---|---|
| Weapons | `Objects/Weapons/` |
| Projectiles | `Objects/Projectiles/` |
| Damage | `Scripts/Damage API/` |
| Spawning | `Scripts/Spawning/` |
| Game mode logic | `Objects/Map elements/Gamemode-specific/`, `Scripts/Maps/` |
| Class limits, team balance | `Scripts/Classlimits/`, `Scripts/Team Balance/` |
| Rewards / auth | `Scripts/Rewards/`, `Documentation/Authentication.txt` |

## Bots

| What | Where |
|---|---|
| Bot behaviour (~5,000 lines) | `Scripts/Bots/` |
| Nav cache load and path search (~2,100 lines) | `Scripts/BotNav/` |
| Bot creation and roster insertion | `Scripts/Bots/botAdd.gml` |
| Per-tick input generation — the seam | `Scripts/Bots/botInputUpdate.gml` |
| Tuning constants | `Constants.xml`, the `BOT_*` block |
| Design notes | `Documentation/Bots.md` |

**The paths in this section resolve only in the bot fork**, at
`github.com/kylelmoy/Gang-Garrison-2` (branch `bot-route-variety`) — the
reference checkout is stock upstream GG2 and has none of `Scripts/Bots/`,
`Scripts/BotNav/`, the `BOT_*`/`NAV_*` constants, or `Documentation/Bots.md`. See
CLAUDE.md, "Reading the reference", for the full picture. This port's bot layer
has since diverged from the fork's, so nothing here is a specification; it is a
map of where the ideas came from. Every other section of this file indexes the
stock tree and is accurate.

**Nav graph generation is no longer in the game.** It lives in this repo's
`src/Gg2.Nav` (`gg2-server navgen`), which writes `botnav/<map>_a<area>.txt`
into `nav/` (or wherever `GG2_NAVCACHE` points). The cache format is documented
in the JavaScript nav-graph decoder — it is `ds_grid_write`'s column-major
layout with sixteen-byte cells and the double four bytes in, which is not the
obvious format. Do not re-derive it by hand.

## Tooling worth borrowing from the live-game bridge

| What | Where |
|---|---|
| Walkmask / level-data decoder (port this) | `tools/walkmask.js` |
| Nav cache reader and world↔node conversion | `tools/navgraph.js` |
| Bot behaviour scenarios (port the suite) | `tools/bot-scenarios.js`, `tools/botscenario.js` |
| Nav method and measured results | `docs/NAVMETHOD.md`, `docs/NAVLOG.md` |
| GM8/GML gotchas that cost real debugging time | `GML.md` |
| Live-game control (bridge, MCP tools) | `CLAUDE.md` |

The bridge's GML notes are worth reading once even though no GML is being written
here: several entries are about engine behaviour the port has to reproduce, not
about syntax.
