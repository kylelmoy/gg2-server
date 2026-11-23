# gg2-server

A dedicated server for [Gang Garrison 2](https://www.ganggarrison.com/), written
in C#, with bots.

Stock GG2 v2.9.2 clients connect and play a normal game with no patches: all
ten classes, all eight gamemodes, the shipped maps and custom ones. It uses
about a twelfth of the CPU of the stock Game Maker 8 server and runs headless
on Linux, Windows and macOS. Bots fill empty seats, walk the maps and play the
objective.

## Quick start

### Linux and macOS

```sh
curl -fsSL https://raw.githubusercontent.com/kylelmoy/gg2-server/master/install.sh | sh
./gg2-server/gg2-server --bots 8
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/kylelmoy/gg2-server/master/install.ps1 | iex
.\gg2-server\gg2-server.exe --bots 8
```

Windows Firewall asks whether to allow the server the first time it starts.
Allow it if players on other machines should be able to join.

### Docker

The same on every OS with Docker installed. In an empty directory:

```sh
curl -fsSLO https://raw.githubusercontent.com/kylelmoy/gg2-server/master/compose.yaml
docker compose up -d
```

In Windows PowerShell, type `curl.exe` instead of `curl`. Without Compose:

```sh
docker run -d --name gg2 -p 8190:8190 ghcr.io/kylelmoy/gg2-server:latest --bots 8
```

### Connecting

In Gang Garrison 2, choose **Join Game**, then **Direct Connect** to
`127.0.0.1` port `8190`. Players on other machines connect to this machine's
address; from the internet, forward TCP port 8190 to it.

`gg2-server --help` lists every option. Running the install script again
updates the server in place and keeps any files you added, such as a `gg2.ini`.

### Platforms

| Platform | Release archive | Install |
|---|---|---|
| Linux x64 | `gg2-server-<version>-linux-x64.tar.gz` | `install.sh` |
| macOS, Apple Silicon | `gg2-server-<version>-osx-arm64.tar.gz` | `install.sh` |
| macOS, Intel | `gg2-server-<version>-osx-x64.tar.gz` | `install.sh` |
| Windows x64 (and Windows on Arm, emulated) | `gg2-server-<version>-win-x64.zip` | `install.ps1` |
| Docker, `linux/amd64` (Apple Silicon runs it emulated) | `ghcr.io/kylelmoy/gg2-server` | `compose.yaml` |

Every archive is self-contained, so nothing else needs to be installed. To
install by hand, download one from
[Releases](https://github.com/kylelmoy/gg2-server/releases) and extract it.
On macOS, an archive downloaded with a browser is quarantined, and Gatekeeper
refuses to start the server. Clear the flag with
`xattr -dr com.apple.quarantine gg2-server`. The install script downloads with
curl or wget, which do not set it.

The install scripts read three environment variables: `GG2_DIR` (where to
install, default `./gg2-server`), `GG2_VERSION` (a release tag such as
`v1.1.0`, default the latest) and `GG2_REPO` (a fork's `owner/name`).

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

`--speed` and `--control-port` are development tools. Never use them on a server
with players connected: they desynchronise every client.

### Bots

| Option | Default | What |
|---|---|---|
| `--bots <n>` | `0` | Target player count. Bots fill the seats humans leave empty, so a target of 8 with 3 humans connected runs 5 bots. |
| `--bot-difficulty <1-5>` | `5` | Skill tier. 5 plays the trained nets exactly; lower tiers aim like people (they lag a target and sit off it), take longer to pick a target up, and hold fire more often. |
| `--auto-difficulty` | off | Each bot plays at the level of the human it is shooting at. Every human gets a rating that moves with their fights against bots, starting at `--bot-difficulty`'s level, so a player who keeps winning meets harder bots. |
| `--bot-suspend-minutes <n>` | `0` | Lets any player type `/bots off` in chat to remove the bots for this many minutes. `0` turns the command off. |
| `--bot-class-limit <list>` | `spy=0` | The maximum number of bots per class on each team, e.g. `medic=1`. Bots never play spy unless you allow it. |

A custom map gets a nav graph built automatically when the server changes to it.

Bots fight with trained policy nets, nine of them in `checkpoints/`, one per
class. The nets drive combat, and a scripted layer handles movement, routing and
the objective. To run your own nets, pass them with `--policy` and assign each
the classes it drives; to hand a class back to the scripted layer, assign it
`scripted`:

```sh
gg2-server --bots 12 \
  --policy checkpoints/ppo-warm-soldier-s1.gg2p \
  --policy checkpoints/ppo-finetune-scout-s1.gg2p \
  --bot-class-policy Soldier=ppo-warm-soldier-s1,Scout=ppo-finetune-scout-s1

gg2-server --bots 12 --bot-class-policy Sniper=scripted
```

`checkpoints/README.md` lists the nine and the class each drives.

### Duel server

`--duel` pairs every human with a bot of their own class, alone in a small arena
cut from a shipped map, with a difficulty that follows each player's wins and
losses. The arenas ship in `maps/duel/`:

```sh
gg2-server --duel --maps maps/duel --botnav nav/duel --map duel_01
```

See [`docs/DUEL.md`](docs/DUEL.md).

### Admin panel and analytics

| Option | What |
|---|---|
| `--web <port>` | Runs a browser admin panel: roster, live map, performance graphs, chat, kick and ban, and a settings page for the `--config` file. It binds `127.0.0.1` unless you pass `--web-bind`. Use `--web-password` to choose the password; otherwise one is generated and printed at startup. Add `--web-tls <pfx>` before exposing the panel. See [`docs/WEBADMIN.md`](docs/WEBADMIN.md). |
| `--analytics <path>` | Tracks unique players, playtime and peak concurrency across restarts. Addresses are stored only as salted hashes. Add `--geoip <path>` with a database you download yourself to show locations. See [`docs/ANALYTICS.md`](docs/ANALYTICS.md). |
| `--tick-stats` | Logs a tick-timing histogram every 30 seconds. |

## Running in Docker

Each release pushes an image to `ghcr.io/kylelmoy/gg2-server`, tagged with its
version (`:1.1.0`) and `:latest`. Arguments after the image name are ordinary
server options; in `compose.yaml` they are the `command:` list.

```sh
docker run -d --name gg2 --restart on-failure \
  -p 8190:8190 -p 127.0.0.1:8191:8191 \
  -v /srv/gg2:/data \
  ghcr.io/kylelmoy/gg2-server:latest --config /data/gg2.ini --bots 8 --lobby
```

| | |
|---|---|
| Ports | `8190/tcp` is the game. `8191/tcp` is the admin panel, which is always on in the image. |
| Admin panel | Plain HTTP, and listening on every interface inside the container. Publishing it as `127.0.0.1:8191:8191` (as above and in `compose.yaml`) keeps it off the network; reach it remotely with `ssh -N -L 8191:127.0.0.1:8191 <host>`. Publish it as `8191:8191` only behind TLS (`--web-tls`, or a reverse proxy). |
| Password | Printed in `docker logs gg2` (`docker compose logs`) unless `[Web] Password` in the config file sets one. A generated one changes every time the container is recreated. |
| Config | `--config` pointing into a mounted directory, so the panel's settings page can save it. The file must exist. |
| Lobby | Not listed unless `--lobby` is passed. |
| Updating | `docker compose pull && docker compose up -d`, or `docker pull` and re-create the container. |
| Stopping | `docker stop gg2` shuts down cleanly and takes the lobby listing down. |

[`docs/DEPLOY.md`](docs/DEPLOY.md) covers hardware sizing, a systemd unit,
shutting down with SIGTERM, and updating several hosts. Read
[`docs/HARDENING.md`](docs/HARDENING.md) before putting the server on the public
internet.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/kylelmoy/gg2-server.git
cd gg2-server
dotnet run -c Release --project src/GG2.Host -- --bots 8
```

The maps, nav graphs and bot checkpoints are in the repository, so a clone
runs with no other files. To build a directory you can copy to a host:

```sh
dotnet publish src/GG2.Host/GG2.Host.csproj -c Release -r linux-x64 --self-contained true -o out/
```

Use `win-x64`, `osx-arm64` or `osx-x64` for the other platforms. Build a macOS
target on a Mac: only there does the SDK sign the executable, and Apple Silicon
refuses to start an unsigned one. `docker build -t gg2-server .` builds the
image.

`dotnet test GG2.sln` runs the test suite. The first run clones stock GG2 at a
pinned commit for the files the tests read, so it needs git and the network
once. Pushing a tag named `v<version>` tests, builds every platform and
publishes a release (`.github/workflows/release.yml`).

## Performance

Compared against the stock GM8 server (GG2 `ea8d6951`, `-dedicated`) on the
same machine, map and load. Each figure is the range over two 79-second runs
per server on an i7-13700K, with this server built in Release at `e04314a`. The
load is real TCP clients from `tools/loadprobe` (spectators) and `tools/soak`
(players that join, pick teams and classes, press random keys and leave, with
the same seed for both servers). `tools/gm8-compare` reproduces it.

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

## How it works

The stock client re-simulates the physics itself between position updates, so
this server reproduces the game's arithmetic exactly and its structure not at
all. [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) is the map of the code,
[`docs/SIMULATION.md`](docs/SIMULATION.md) the determinism contract,
[`docs/PARITY.md`](docs/PARITY.md) how it is checked against the running game,
and [`docs/BOTS.md`](docs/BOTS.md) the bot layer.

## Compatibility

GG2 v2.9.2, `VERSION = 29200`, `PROTOCOL_UUID =
b31c2209-4256-9a19-d0ef-c71c5373bd75`.

## License

[Mozilla Public License 2.0](LICENSE), the same license as
[Gang Garrison 2](https://github.com/Gang-Garrison-2/Gang-Garrison-2). The maps
in `maps/` other than `gg_debug2` are copied unmodified from GG2 v2.9.2.
