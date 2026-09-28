# gg2-server

A headless [Gang Garrison 2](https://www.ganggarrison.com/) server in C#,
wire-compatible with the stock Game Maker 8 client- with bots!

Stock GG2 v2.9.2 clients connect and play a normal game with no patches. It
supports all ten classes and all eight gamemodes. The shipped maps and bot nav
graphs are included, so a fresh clone runs with no other files.

## Quick start

Download the archive for your platform from
[Releases](https://github.com/kylelmoy/gg2-server/releases), extract it, and run
`gg2-server` (`gg2-server.exe` on Windows). It needs nothing else installed.

```bash
./gg2-server --map koth_valley --bots 12
```

To build from source instead, you need the **.NET 10 SDK**:

```powershell
git clone https://github.com/kylelmoy/gg2-server.git
cd gg2-server
dotnet run -c Release --project src/Gg2.Host -- --map koth_valley --bots 12
```

Then connect a GG2 client to `127.0.0.1:8190` (Join Game → Direct Connect).
`gg2-server --help` lists every option.

## Options

| Option | Default | What |
|---|---|---|
| `--port <n>` | `8190` | Listen port. |
| `--name <text>` | | Server name shown to clients. |
| `--password <text>` | | Require a password to join. |
| `--players <n>` | `10` | Seats for clients, 1–48. |
| `--map <name>` | `ctf_truefort` | Opening map. With no rotation, the server replays this map forever. |
| `--rotation <list\|path>` | | Comma-separated map names, or a rotation file. |
| `--shuffle <mode>` | `none` | `arena-last` shuffles the rotation without opening on an arena map; `any` shuffles with no restriction. |
| `--time-limit <n>` | `15` | Round length in minutes. |
| `--cap-limit <n>` | `5` | Captures needed to win. |
| `--kill-limit <n>` | `30` | Kills needed to win a deathmatch round. |
| `--class-limit <n\|list>` | unlimited | Per team. One number for every class, or per class, e.g. `medic=1,spy=0`. |
| `--no-autobalance` | | Turns autobalance off. |
| `--welcome <text>` | | Message sent to each player who joins. |
| `--plugins <list>` | | Server-sent plugins for clients to load, e.g. `chat`. |
| `--lobby` | off | List the server publicly at ganggarrison.com. |
| `--config <path>` | | Read settings from a reference-format `gg2.ini`. Every setting can be set there; a command-line flag always overrides the file. See [`docs/CONFIG.md`](docs/CONFIG.md). |

### Bots

| Option | Default | What |
|---|---|---|
| `--bots <n>` | `0` | Target player count. Bots fill the seats humans leave empty, so a target of 8 with 3 humans connected runs 5 bots. |
| `--bot-difficulty <1-5>` | `5` | Lower levels miss more and react more slowly. |
| `--bot-suspend-minutes <n>` | `0` | Lets any player type `/bots off` in chat to remove the bots for this many minutes. `0` turns the command off. |
| `--bot-class-limit <list>` | | The maximum number of bots per class on each team, e.g. `medic=1,spy=0`. |

A custom map gets a nav graph built automatically when the server changes to it.

To use trained bots, add `--bot-mode policy` and pass one or more checkpoints
from `checkpoints/`. They control combat for the classes they were trained on,
and the scripted bots handle everything else:

```powershell
gg2-server --bots 12 --bot-mode policy `
  --policy checkpoints/tier0_combat4_s2.npz `
  --policy checkpoints/tier2_pyro_rest3_s0.npz
```

### Admin panel and analytics

| Option | What |
|---|---|
| `--web <port>` | Runs a browser admin panel: roster, live map, performance graphs, kick, and a settings page for the `--config` file. It binds `127.0.0.1` unless you pass `--web-bind`. Use `--web-password` to choose the password; otherwise one is generated and printed at startup. Add `--web-tls <pfx>` before exposing the panel. See [`docs/WEBADMIN.md`](docs/WEBADMIN.md). |
| `--analytics <path>` | Tracks unique players, playtime and peak concurrency across restarts. Addresses are stored only as salted hashes. Add `--geoip <path>` with a database you download yourself to show locations. See [`docs/ANALYTICS.md`](docs/ANALYTICS.md). |
| `--tick-stats` | Logs a tick-timing histogram every 30 seconds. |

## Deploying

A release archive is ready to copy to a host as it is. To build one yourself:

```bash
dotnet publish src/Gg2.Host/Gg2.Host.csproj -c Release -r linux-x64 --self-contained false -o out/
```

The published output includes the maps, nav graphs and checkpoints. Copy `out/` to the host
and run `gg2-server`. The server also runs on Windows.

Pushing a tag named `v<version>` builds and tests both platforms and publishes a
release (`.github/workflows/release.yml`).

[`docs/DEPLOY.md`](docs/DEPLOY.md) covers hardware sizing, a systemd unit,
shutting down with SIGTERM, and updating several hosts. Read
[`docs/HARDENING.md`](docs/HARDENING.md) before putting the server on the public
internet.

Never use `--speed` or `--control-port` on a server with clients connected. They
are development tools, and they desync every client.

## Performance

Compared against the stock GM8 server (GG2 `ea8d6951`, `-dedicated`) on the
same machine, map and load. Each figure is the range over two 79-second runs
per server on an i7-13700K, with this server built in Release at `e04314a`. The
load is real TCP clients from `tools/loadprobe` (spectators) and `tools/soak`
(players that join, pick teams and classes, press random keys and leave, with
the same seed for both servers).

| Load | GM8 CPU | gg2-server CPU | GM8 memory | gg2-server memory |
|---|---|---|---|---|
| 1 spectator | 10.3–10.5% | 0.3–0.7% | 278 MB | 57–61 MB |
| 16 spectators | 10.1–10.6% | 1.3–1.6% | 280 MB | 70 MB |
| 8 players | 14.8–15.6% | 1.2–1.3% | 280 MB | 67–72 MB |

CPU is a percentage of one core; memory is the working set. With 8 players that
is about 5.1 ms of CPU per 33.3 ms tick for GM8 and 0.42 ms for gg2-server,
roughly 12 times less. GM8 also spends about 10% of a core with nobody
connected, while this server slows its tick almost to a stop until someone joins.
It starts accepting connections in 0.55 s against GM8's 2.1 s.

Both servers delivered exactly 30 broadcasts a second, with the same bytes per
second. The gap between broadcasts had a p99 of 34–36 ms and a maximum of
35–41 ms on both.

Stock GM8 has no bots, so there is no bot comparison. On its own, this server
runs twelve bots at 0.151 ms a tick (Release), 0.5% of the 33.3 ms budget.

## Compatibility

GG2 v2.9.2, `VERSION = 29200`, `PROTOCOL_UUID =
b31c2209-4256-9a19-d0ef-c71c5373bd75`.

## License

[Mozilla Public License 2.0](LICENSE), the same license as
[Gang Garrison 2](https://github.com/Gang-Garrison-2/Gang-Garrison-2). The maps
in `maps/` other than `gg_debug2` are copied unmodified from GG2 v2.9.2.
