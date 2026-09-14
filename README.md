# gg2-server

A headless [Gang Garrison 2](https://www.ganggarrison.com/) server in C#,
wire-compatible with the stock Game Maker 8 client.

Stock clients connect to it and play a normal game — pick a team and a class,
spawn, fight, capture the intel, see the round end and the map change — with no
modification and no patched client. Underneath, the simulation is a pure,
I/O-free C# port of GG2's physics, weapons and gamemodes, which makes two things
possible that the original server cannot do: bots with room to think, and
matches that run thousands of times faster than real time.

```powershell
dotnet run --project src/Gg2.Host -- --map ctf_truefort --bots 12
```

That is a full 12-bot game. The bots walk the map, fight over the objective and
cost about 0.5% of the 30 Hz tick budget in a Release build.

---

## Features

- **Wire-compatible** with GG2 v2.9.2. Framing, handshake, roster indexing and
  state-update layouts all match the reference byte for byte.
- **All ten classes, fully armed** — rockets, mines, healing beams, ubercharge,
  afterburn, the airblast, the sentry gun, the sniper's scope, the bubble screen,
  the spy's cloak and backstab, the sandvich, taunting.
- **All eight gamemodes** — CTF, King of the Hill, Dual KotH, both control-point
  arrangements, Invasion, Arena, Generator and Team Deathmatch — plus the round
  clock, win conditions, the victory screen and the map rotation.
- **Physics proven against the running game**, not by eye: dozens of
  engine-driven trajectories reproduced bit for bit, and a long recording of the
  reference server replayed closed-loop with zero positional error.
- **Bots** — nav graphs, A\*, a path follower with jump and rocket-jump branches,
  targeting, a combat controller, an objective layer and a population manager.
- **A headless match runner** for fast bot iteration — dozens of matches across
  every map in a few seconds, thousands of times real time, so a bot change is
  evaluated in seconds instead of by watching a live server.
- **Batteries included** — the shipped maps and bot nav graphs are checked in.
  A fresh clone serves a round and runs bots with no arguments and no GG2
  checkout.

## Requirements

- **.NET 10 SDK** — that is all, to build and run the server.
- A **Gang Garrison 2 client** if you want to connect to it (any stock build).
- A **GG2 source checkout** only if you want to run the test suite; see
  [Development](#development).

## Quick start

```powershell
git clone https://github.com/kylelmoy/gg2-server.git
cd gg2-server

# Serve ctf_truefort on the default port 8190.
dotnet run --project src/Gg2.Host

# ...with twelve players' worth of bots.
dotnet run --project src/Gg2.Host -- --map koth_valley --bots 12

# Play bot matches across every map as fast as the CPU allows.
dotnet run -c Release --project src/Gg2.Host -- eval --matches 4
```

Then point a GG2 client at `127.0.0.1:8190` (Join Game → Direct Connect).

## Usage

`gg2-server --help` prints the full list. The options you are most likely to
want:

| Option | Default | What |
|---|---|---|
| `--port <n>` | `8190` | Listen port. |
| `--map <name>` | `ctf_truefort` | Built-in map to serve; the rotation takes over from there. |
| `--rotation <list\|path>` | | Map rotation: comma-separated names, or a rotation file. Without `--map`, the server opens on its first entry. |
| `--shuffle <mode>` | `none` | `arena-last` shuffles but will not open on an arena map; `any` takes whatever comes out. |
| `--config <path>` | | Read a reference-format `gg2.ini`. **Every** server setting can be set from it — `docs/CONFIG.md` has the key table. Applied before every flag, so a flag always wins. Only written back when the admin panel saves. |
| `--name <text>` | | Server name shown to clients. |
| `--welcome <text>` | | Message sent to each joiner. |
| `--chat-join-message <text>` | | A **chat** line sent to each player as they join chat — the place to tell them a command exists. Needs a chat plugin; a client without one never sees it. |
| `--plugins <list>` | | Server-sent plugins clients are asked to load, comma-separated (e.g. `chat` — the chat console and vote dialog; `chat_v2_pr` is the other one). An entry may be pinned as `name@md5`. |
| `--plugins-required` | off | Tell clients they must load them to join. Advisory only — the reference does not enforce it and neither does this. |
| `--players <n>` | `10` | Seats for clients, 1–48. |
| `--password <text>` | | Require a password to join. |
| `--time-limit <n>` | `15` | Round clock in minutes. |
| `--cap-limit <n>` | `5` | Captures to win. |
| `--kill-limit <n>` | `30` | Kills to win a deathmatch round. |
| `--tdm-invuln <n>` | `5` | Seconds of team-deathmatch spawn protection. `0` is none. |
| `--class-limit <n\|list>` | unlimited | One limit for every class, or per class as `medic=1,spy=0`. Per team, not per server; `255` is unlimited and `0` bans. |
| `--no-autobalance` | on | Stop moving a player across when one team is two larger. |
| `--bots <n>` | `0` | Fill target including humans, not a bot count — a server filling to 8 with 3 people on it runs 5 bots. |
| `--bot-difficulty <1-5>` | `5` | How good they are. The lower tiers are a handicap ladder — aim error, an acquire delay, a hold-fire roll — so pass a lower one for beatable bots. |
| `--bot-suspend-minutes <n>` | `0` | Let any player type `/bots off` in chat to take the bots away for this long. `0` means the command does not exist. `docs/PLUGINS.md`, "the bot suspension". |
| `--lobby` | off | List the server publicly at ganggarrison.com so it appears in the client's browser. |
| `--tick-stats` | off | Log a histogram of tick intervals every 30s. Safe with clients connected. |

Maps and nav graphs are found automatically; `--maps`, `--botnav`, `GG2_MAPS`
and `GG2_NAVCACHE` override them.

With no rotation the server replays `--map` forever, which is not what a stock
dedicated server does — that plays a full multi-map rotation. `--config gg2.ini`
reproduces it, `--rotation` states one explicitly.

### Advanced bot controllers

`--bot-mode` picks which of the four shipped brains the bots run. Each of the
three non-scripted ones replaces only the bot's keys and aim, and only inside its
engagement window — the scripted layer still owns movement, routing and
objectives.

| `--bot-mode` | | What |
|---|---|---|
| `scripted` | `0` | The scripted layer alone. A complete bot, and the control every comparison is made against. |
| `search` | `1` | Plus a rollout search. Better on death share, and much more expensive. Not yet measured with humans in the server. |
| `policy` | `2` | Plus a trained checkpoint. Needs `--policy <path>`. |
| `tiered` | `3` | Both, most expensive first, with an arbiter choosing between them when several bots engage on the same tick. Needs `--policy`; defaults `--bot-arbiter` to 10000. |

`--bot-arbiter <microseconds>` is a per-tick budget shared by every bot. Its
default is derived from measured per-decision costs but has never itself been
tuned.

Bots need a nav graph, and the graphs shipped with the repository cover the
built-in maps. Anything else — a custom map, an edited one — gets one built at
the map change, in well under a second, because everything a graph needs is in
the map's own PNG. `--no-navgen` turns that off; `gg2-server navgen` builds them
offline instead.

A trained policy ships in `checkpoints/`, as two tiers covering five of the nine
classes between them — the other four stay scripted. `--policy` is repeatable and
appends, one tier per checkpoint:

```
gg2-server --map ctf_truefort --bots 12 --bot-mode policy \
  --policy checkpoints/tier0_combat4_s2.npz \
  --policy checkpoints/tier2_pyro_rest3_s0.npz
```

Each checkpoint drives the classes its own file claims, so that command needs no
further configuration. Two settings adjust it, and they answer different
questions:

| | |
|---|---|
| `--bot-class-policy Soldier=scripted` | who drives a class. A checkpoint's file name without its extension, or `scripted`. Repeatable, and it only has to name the classes being overridden. |
| `--bot-class-limit medic=1,spy=0` | which classes bots play: how many bots of each class one team may hold, 0 keeping bots off a class. Nothing derives it from the driven set, so to make every bot the configuration under test rather than a mix of that and the all-scripted arm, set the classes no checkpoint drives to 0 — the startup log names each checkpoint's classes. |

A checkpoint is dated by the simulation it was trained against: the loader prints
both stamps, the classes each will drive, and refuses a mismatch unless told
otherwise. Read `checkpoints/README.md` before quoting any number off these —
what has a match number and what does not is not obvious from the files.

### Offline tooling

Six switches turn the server into a laboratory. All are off by default, and the
first two must stay off wherever a client is connected — clients pace themselves
at 30 Hz and re-simulate between updates, so an unpaced server desynchronises
every one of them at once.

| Switch | What |
|---|---|
| `--speed <n\|max>` | Run at n times real time, or unpaced. Pacing only: every duration in the game is counted in ticks, so the simulation is identical at any speed. |
| `--control-port <n>` | A line-of-JSON control surface on 127.0.0.1 only — read state, freeze, step N ticks, change map, teleport, set the bot count. It executes commands against a live game. |
| `--record <dir>` | Write replayable `.trace` recordings of the broadcast stream. Contains every player's keys and aim on every tick, so it is training data as well as a parity fixture. |
| `--web <n>` | An admin panel in a browser: the roster, a live map, the round, performance graphs (tick time, CPU, memory, late ticks, bandwidth, load), the log, the same operator controls the control port has plus `kick`, and a settings page that edits every key of the `--config` file with a restart button that applies it. The only surface here meant to be reachable from the network. It binds `127.0.0.1` unless `--web-bind` says otherwise, the password cannot be turned off (one is generated and printed if you give none), and a non-loopback bind without `--web-tls` is warned about at every startup. |
| `--analytics <path>` | Count who plays here and keep it across restarts: unique players all time and per day, playtime, peak concurrency, and — with a `--geoip` database the operator supplies — a map of where they connect from, by country and by US state. Shown in the panel's Analytics view. Addresses are stored as salted hashes, never as addresses; `docs/ANALYTICS.md` has what the file holds and what it deliberately does not. |
| `eval` | A subcommand, not a switch: plays bot matches across maps and reports the results. `gg2-server eval --help`. |

## Other executables

| Command | What |
|---|---|
| `dotnet run --project src/Gg2.Parity` | `gg2-parity` — record a server's broadcast stream, `census` it, `diff` two recordings byte for byte, or `replay` a trace against the port with a drift report. This is how the port is proven correct. |
| `dotnet run --project src/Gg2.Env` | `gg2-env` — the batch-stepping surface a learned policy trains against. Loopback only, one client. |
| `dotnet run --project tools/bench` | The measurement instrument: per-map criterion, engagement-range sweep, policy cost. Not in `Gg2.sln` — build it explicitly. |

## Repository layout

| Project | What |
|---|---|
| `src/Gg2.Protocol` | The constant table, wire types, the little-endian buffer reader/writer, wire quantisation, command framing, `PlayerInput`. |
| `src/Gg2.Maps` | Map PNG level-data extraction, the walkmask decoder, GGON, the entity list. |
| `src/Gg2.Simulation` | `WorldState`, collision, the movement solver, the character update, weapons and gamemodes. Pure — no sockets, no clock, no files — enforced by project references. |
| `src/Gg2.Bots` | Nav, A\*, the path follower, targeting, combat, objectives, population. A bot is an `IInputSource` like a socket; the simulation never special-cases one. |
| `src/Gg2.Server` | Sockets, the join handshake, the roster, the 30 Hz broadcast, the tick that drives the simulation. The only I/O. |
| `src/Gg2.Host` | `gg2-server`, the console entry point, and the `eval` and `navgen` subcommands. |
| `src/Gg2.Nav` | The bot nav-graph generator: turns a map's walkmask and entities into the graph the path follower routes on. |
| `src/Gg2.Parity` | `gg2-parity`: a stream recorder, a byte diff, and a trace replayer with drift reporting. |
| `src/Gg2.Env` | `gg2-env`: the batch-stepping environment seam for policy training. |

| Directory | What |
|---|---|
| `maps/` | The shipped maps plus `gg_debug2`, a port-authored debug range. A byte-identical copy of the reference's. |
| `nav/` | The bot nav graphs, gzipped, plus a manifest. |
| `tests/Gg2.Tests` | xunit. Parity fixtures live here and run in CI. |
| `tools/` | Engine samplers, fixture generators and the benchmark harness. |
| `docs/` | Design notes and protocol, physics and architecture documentation. |

## Development

```powershell
dotnet test Gg2.sln
```

The test suite needs a stock GG2 source checkout for `Constants.xml` and the
scripts — only the maps and nav graphs are decoupled. It defaults to
`D:\code\Gang-Garrison-2`; override with `GG2_REFERENCE`.

Two rules that are not negotiable, whatever you are changing:

1. **Compatible on the wire and in observable simulation. Free everywhere else.**
   The stock client re-simulates the physics itself between position updates, so
   trajectories, byte layouts, update cadence and roster indexing must match.
   Nothing else does — objects, events, `with()` loops, alarms, GM8 data
   structures and per-frame recomputation are Game Maker showing through and
   should be replaced with whatever is fastest and clearest in C#. Do not
   transcribe GML structure; transcribe GML arithmetic.
2. **Never copy GML into this repo, and never modify the reference trees.** Cite
   the reference by path and write C#.

## Related repositories

| Repo | What |
|---|---|
| `Gang-Garrison-2` | The reference implementation. Stock GG2 v2.9.2, cloned from [`Gang-Garrison-2/Gang-Garrison-2`](https://github.com/Gang-Garrison-2/Gang-Garrison-2). Read-only from here. |
| `gg2-agent` | Tooling for the GM8 game: build scripts, the agent bridge, the MCP server, nav analysis. |
| `gg2-rl` | Bot-policy trainer, on the other side of the `gg2-env` seam. |

## Reference version

GG2 v2.9.2, `VERSION = 29200`, `PROTOCOL_UUID =
b31c2209-4256-9a19-d0ef-c71c5373bd75`.
