# gg2-server

A headless [Gang Garrison 2](https://www.ganggarrison.com/) server in C#,
wire-compatible with the stock Game Maker 8 client- with bots!

Stock GG2 v2.9.2 clients connect and play a normal game with no patches. It
supports all ten classes and all eight gamemodes. The shipped maps and bot nav
graphs are included, so a fresh clone runs with no other files.

## Quick start

You need the **.NET 10 SDK**.

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

`dotnet publish` does not include `checkpoints/`, so copy that folder to the
server yourself.

### Admin panel and analytics

| Option | What |
|---|---|
| `--web <port>` | Runs a browser admin panel: roster, live map, performance graphs, kick, and a settings page for the `--config` file. It binds `127.0.0.1` unless you pass `--web-bind`. Use `--web-password` to choose the password; otherwise one is generated and printed at startup. Add `--web-tls <pfx>` before exposing the panel. See [`docs/WEBADMIN.md`](docs/WEBADMIN.md). |
| `--analytics <path>` | Tracks unique players, playtime and peak concurrency across restarts. Addresses are stored only as salted hashes. Add `--geoip <path>` with a database you download yourself to show locations. See [`docs/ANALYTICS.md`](docs/ANALYTICS.md). |
| `--tick-stats` | Logs a tick-timing histogram every 30 seconds. |

## Deploying

```bash
dotnet publish src/Gg2.Host/Gg2.Host.csproj -c Release -r linux-x64 --self-contained false -o out/
```

The published output includes the maps and nav graphs. Copy `out/` to the host
and run `gg2-server`. The server also runs on Windows.

[`docs/DEPLOY.md`](docs/DEPLOY.md) covers hardware sizing, a systemd unit,
shutting down with SIGTERM, and updating several hosts. Read
[`docs/HARDENING.md`](docs/HARDENING.md) before putting the server on the public
internet.

Never use `--speed` or `--control-port` on a server with clients connected. They
are development tools, and they desync every client.

## Compatibility

GG2 v2.9.2, `VERSION = 29200`, `PROTOCOL_UUID =
b31c2209-4256-9a19-d0ef-c71c5373bd75`.
