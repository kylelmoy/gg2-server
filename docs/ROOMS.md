# Private rooms

A player in gg2-client asks for a server for themselves and their friends,
gets a short code to share, and the server goes away once everybody has left.
Nobody hosts from a phone. This is the industry's session-based dedicated
server pattern (Agones allocation, GameLift game sessions, PlayFab
multiplayer servers), sized for a fleet of small VPSes.

**Status: steps 1 and 2 built.** The table below says which parts exist and
what proves each. Nothing else in this file exists until its row says so.

| Step | Part | State |
|---|---|---|
| 1 | `--exit-when-empty` and `--max-lifetime` on gg2-server | **Built.** `EmptyServerExitTests`; the real host exits 0 three seconds after an empty start |
| 2 | `gg2-room@.service` template unit | **Built**, run under systemd 255 in WSL as a user unit (below); not yet installed on any VPS |
| 3 | The dispatcher, one per VPS | not started |
| 4 | Device check and the ban list | not started |
| 5 | gg2-client: create, join by code | not started |
| 6 | Fleet rollout: firewall range, units, dispatcher | not started |

## Where rooms run

**On the three VPSes only**: `gg2-us-west`, `gg2-us-east`, `gg2-de`. Not on
barbara, which is a home connection. Each VPS also runs its public fleet
server, and that server's slots come first: the dispatcher's capacity check is
what protects them.

## The shape

```
gg2-client ──HTTPS──> dispatcher (per VPS) ──systemctl──> gg2-room@<port>
     │                                                        │
     └────────────── GG2 TCP, password = derived from code ───┘
```

1. The client pings every dispatcher, picks the nearest one that says it has
   room, and sends `POST /rooms` with a device proof (below) and the room's
   choices (map or rotation, bots on or off).
2. The dispatcher checks capacity, takes a free port from its range, writes a
   room ini, starts `gg2-room@<port>`, and replies
   `{code, host, port, password, expiresAt}`.
3. Friends type the code. Its first letter is the host's prefix in the
   directory (below), so the client knows which dispatcher to send `GET /rooms/{code}` to, and gets the same
   address and password. No central directory is needed.
4. The server exits on its own once nobody has been connected for
   `ExitWhenEmptySeconds`. systemd reaps it, and the port is free again.

**Nothing new reaches the wire.** A room is an ordinary server with a
password and lobby announcing off. A stock GM8 client given the address and
password can join it too. None of this touches the simulation.

## The pieces

### 1. `--exit-when-empty <seconds>` and `--max-lifetime <minutes>` (built)

`ServerOptions.ExitWhenEmptySeconds`, `[Runtime] ExitWhenEmptySeconds` in a
config file. `EmptyServerExit` is the rule:

- The clock starts at startup, so a room nobody joins goes away after the
  same interval as one everybody left.
- A person is a connection: a spectator and a half-finished handshake count,
  bots do not.
- Wall clock, not ticks, because an empty server idles at 2 Hz.
- `--max-lifetime` / `[Runtime] MaxLifetimeMinutes` ends a room at a hard limit
  whoever is on it (planned: 6 hours), so an idle client cannot hold one open.
- The process exits **0**, so a `Restart=on-failure` unit leaves it stopped.
  Under `Restart=always` it would come back empty, which is why no fleet
  server is given this setting.

The intended value for a room is 120 seconds. That covers the creator's
connect after the dispatcher replies, and a stock client dropping and
reconnecting at a map change to a custom map it lacks (`docs/DEPLOY.md`
has that failure).

### 2. A systemd template unit (built)

`gg2-room@.service`, below, is the unit, for the release layout under
`/opt/gg2-server` (`docs/DEPLOY.md`). The instance name is the port:
`systemctl start gg2-room@8301`. The fleet's local `scripts/deploy.sh
--print-room-unit` prints the same text for another `--root`; `scripts/` is
not carried in this repo, so this copy is the one a clone has. It is never
enabled; the dispatcher starts rooms and rooms stop themselves. Against the
fleet unit it differs in four ways:

- **Port, empty timeout and lifetime are flags** (`--port %i
  --exit-when-empty 120 --max-lifetime 360`), and a flag beats the file, so
  no room config can move a room off its instance's port or make it permanent.
- **`--config` names `rooms/<port>/gg2.ini`**, which the dispatcher writes
  before the start.
- **`MemoryMax=256M`**, against 96-129 MB measured from 1 to 24 clients.
- **No `ReadWritePaths`.** A room writes nothing: shipped maps only, whose nav
  graphs ship, and no panel, bans, analytics or recording.

```ini
# /etc/systemd/system/gg2-room@.service
#
#   scripts/deploy.sh --print-room-unit > room-unit
#   ssh HOST "sudo tee /etc/systemd/system/gg2-room@.service > /dev/null" < room-unit
#   ssh HOST "sudo systemctl daemon-reload"
#
# Never enabled: a room is started by the dispatcher and stops itself. The
# instance name is the game port, and the dispatcher writes the room's config
# to /opt/gg2-server/rooms/<port>/gg2.ini before starting it.

[Unit]
Description=Gang Garrison 2 private room on port %i
After=network-online.target

[Service]
Type=simple
User=gg2
WorkingDirectory=/opt/gg2-server/current

# The port, the empty timeout and the lifetime are FLAGS, and a flag beats the
# file: whatever the dispatcher's template says, a room listens where its
# instance name says and cannot make itself permanent.
ExecStart=/opt/gg2-server/current/gg2-server --config /opt/gg2-server/rooms/%i/gg2.ini --port %i --exit-when-empty 120 --max-lifetime 360

# on-failure: a crashed room comes back on the same port, so its code still
# works. Both ways a room stops itself exit 0, which this leaves alone.
# NOT RuntimeMaxSec for the lifetime: systemd restarts that as a timeout.
Restart=on-failure
RestartSec=5
Environment=DOTNET_gcServer=0

# A server measures 96-129 MB from 1 to 24 clients (docs/DEPLOY.md); a room
# has 12 seats. The bound that protects the box's public server.
MemoryMax=256M

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
RestrictAddressFamilies=AF_INET AF_INET6

# No ReadWritePaths at all: a room writes nothing. It serves shipped maps only,
# whose nav graphs ship, and has no panel, bans, analytics or recording.
```

**The lifetime is `--max-lifetime` and not systemd's `RuntimeMaxSec`.**
systemd counts a `RuntimeMaxSec` stop as a timeout and `Restart=on-failure`
restarts a timeout: measured on systemd 255, two restarts in seven seconds
against a two-second limit. A room would come back empty each time its
lifetime ran out. Players are dropped without warning when it does; a chat
notice before the end does not exist yet.

**What was run** (WSL, systemd 255, a user unit, so `User=` was removed and
`ProtectHome` turned off because the test tree was under `/home`; the rest of
the sandbox as printed): the room listened on its instance's port with the
config's 12 slots, exited 0 five seconds after an empty start with
`--exit-when-empty 5`, and exited 0 at one minute with `--max-lifetime 1`.
Both times the unit ended `inactive`, `Result=success`, `NRestarts=0`. Not yet
run: as a system unit with `User=gg2` under `/opt` on a VPS.

The dispatcher keeps no state of its own. On startup it rebuilds the room list
from `systemctl list-units 'gg2-room@*'` and the ini files beside them, so a
dispatcher crash loses nothing. It needs to start and stop rooms and nothing
else, which is one sudoers line:

```
gg2-dispatch ALL=(root) NOPASSWD: /usr/bin/systemctl start gg2-room@*, /usr/bin/systemctl stop gg2-room@*
```

**A deploy and a running room.** `ExecStart` goes through `current`, which a
deploy flips; a running room should keep the release it started from, because its working directory and executable path resolve the link at start (reasoned, not measured). A deploy keeps
five releases, so only five deploys inside one room lifetime would delete a
release out from under a running room.

### 3. The dispatcher

A small HTTP service on each VPS, behind TLS. Planned as a `gg2-server
dispatch` subcommand, so it ships in the same build and reuses the config
reader.

| Endpoint | Does |
|---|---|
| `GET /status` | region, rooms in use, rooms free. What the client's region picker reads |
| `POST /rooms` | device proof plus room choices; creates a room or says why not |
| `GET /rooms/{code}` | the address and password for a live room, 404 otherwise |

**Capacity**, all checked on every create:

- a room cap per box, set by RAM: about 4 on a 1 GB VPS beside its fleet
  server, about 10 on 2 GB. **Memory is the limit, not CPU**, since a twelve-bot
  server is 0.5% of a core;
- `MemAvailable` above a floor, so the fleet server is never squeezed;
- a free port in the room range (planned: 8300-8339, opened in `ufw`);
- per-device and per-address limits: one live room per device, a few creates
  an hour.

**Room codes**: a region letter plus five characters from an alphabet without
look-alikes (no `0/O`, `1/I/L`), about 2.9 x 10^7 per region. The server
password is derived from the code with a secret that only the dispatcher
knows, so sharing the code is the whole invitation and the password never
needs to be typed.

### 4. Device check and bans

The point is to make bans stick, so abuse costs a device rather than a
reinstall.

**iOS** uses Apple's two services together:

- **App Attest** proves a request comes from the real app on a real device.
  At first launch the app makes a key, Apple attests it, and the dispatcher
  checks the attestation once and remembers the key. Every `POST /rooms`
  carries an assertion signed with that key. The key ID is the identity rate
  limits and room ownership use.
- **DeviceCheck** gives two bits per device that **survive a reinstall**. A
  ban sets one of them through Apple's server API, and a create from a device
  with the bit set is refused, whatever its App Attest key. That is what makes
  a ban stick.

Both need an Apple developer account's private key on the server side. **A
copy lives on each VPS** with the dispatcher's other secrets (outside the
release tree, readable only by the dispatcher's user), so each dispatcher
verifies on its own and no host depends on another.

**Only mobile clients can create rooms.** Desktop has no platform attestation,
and does not need allocation: a desktop client hosts its own server the way
the GM8 game does, by running gg2-server in-process and sharing its address.
A desktop client still joins a room by code.

**The ban list** is keyed by App Attest key ID, with the DeviceCheck bit as
the reinstall-proof half. Every room's ini records the creator's key ID, so a
report against a room can be traced to a device.

### Room settings (agreed)

Just enough for a simple game on a phone. Everything the player chooses maps
onto a setting that already exists, so the dispatcher writes a fixed template
and fills in three values. The dispatcher accepts only these values and
refuses anything else; it never passes text from the client into the ini.

| Choice | Options | Sets |
|---|---|---|
| Map | **Rotation** (the stock rotation, `ShuffleRotation=arena-last`), or any one of the 22 shipped maps. The game mode follows from the map | `Map`, `Rotation`, `ShuffleRotation` |
| Bots | **Off**, Easy, Normal, Hard, Adaptive | `[Bots] Enabled`; `Difficulty` 1 / 3 / 5; Adaptive is `AutoDifficulty=1` |
| Team size | Small (fill to 6), **Medium** (fill to 10), Large (fill to 16). Only matters with bots on | `FillToPlayers`, `MaxBots` |

Bold is the default. Fixed for every room:

| Setting | Value | Why |
|---|---|---|
| `PlayerLimit` | 12 | A friends' game. Memory per room is set by it (`docs/DEPLOY.md`) |
| `Time Limit`, `CapLimit`, respawn, `AutoBalance` | stock | One set of rules everybody already knows |
| `UseLobby` | 0 | Private |
| `Password` | derived from the code | The code is the invitation |
| `[Bots] Mode` and nets | the fleet's, from `deploy/config/common.ini` | Same bots as the public servers |
| `[Chat] Enabled` | 1 | gg2-client has native chat |
| `QuickUpdateInterval` | 7 (stock) | |
| `[Analytics]`, `--record`, `[Web]`, control port | off | A private room records nothing about who played |
| `ExitWhenEmptySeconds` | 120 | Step 1 |

**Shipped maps only**: a custom map would need a download for stock clients
and a nav graph built at the map change, and neither is worth it for this.

### 5. gg2-client

"Create private game" (region picker, map or rotation, bots on or off) and
"Join by code". Joining uses the existing password flow. The creator's client
shows the code in the in-game menu so it can be shared mid-match.

## A changing set of hosts

The hosts will not stay the same three: a provider gets swapped, a region gets
added, or one day somebody else runs the servers. So **no host is built into
the client**, and nothing in a room's lifecycle depends on more than the one
host it runs on.

### The directory

The client learns the dispatchers from a **signed directory**: a small JSON
file at a stable URL, and the only thing about the hosts the app knows.

```json
{
  "operator": "Vindicator's",
  "issued": "2026-11-21T00:00:00Z",
  "hosts": [
    {"prefix": "W", "region": "US West", "url": "https://w.rooms.example"},
    {"prefix": "E", "region": "US East", "url": "https://e.rooms.example"},
    {"prefix": "D", "region": "Germany", "url": "https://d.rooms.example"}
  ],
  "signature": "<Ed25519 over the rest>"
}
```

- **The app ships the directory's URL and its public signing key**, and caches
  the last good copy, so a down URL does not stop room creation on hosts that
  are up. A copy that fails the signature check is ignored, so whoever controls
  the web server or DNS still cannot point players at their own hosts.
- **Hosts are found by the directory, not by `deploy/hosts`.** The fleet's
  deploy list says where to push builds; the directory says where players go.
  They overlap today and need not.
- **A host that is down is skipped.** The client already pings every
  dispatcher's `/status` to pick a region, so a dead entry costs one timeout
  and nothing else. Liveness is never written into the directory.
- **A prefix belongs to a host.** A room code's first letter is looked up in
  the directory, so it names a host and not a provider. A prefix is not
  reused for a different host within one room lifetime (`--max-lifetime`) of
  retiring it, or an old code would resolve on the wrong machine.

### Switching providers

Each host's dispatcher URL is a hostname under a domain the operator controls,
so moving a host is: provision the new VPS from `docs/DEPLOY.md`, install the
room unit and the dispatcher, copy the Apple key, move the DNS name, and retire
the old box after its last room has ended (at most `--max-lifetime`). The
directory does not change unless the region does. A new region is one new entry
and a re-signed directory.

**Bans survive the move** because the reinstall-proof half lives in Apple's
DeviceCheck bits, not on any host. The App Attest key IDs each dispatcher has
seen, and its per-key limits, are local and start fresh on a new box. That is
acceptable: a fresh limit does not undo a ban.

### Handing the servers to somebody else

Everything a new operator needs is in this repo or rented, except four things
that belong to whoever runs it now. These are what would be handed over:

| Asset | What it controls | If it is not handed over |
|---|---|---|
| The directory's domain | where the app looks for hosts | the app finds no hosts until an update ships a new URL |
| The directory's signing key | which hosts the app trusts | same: a new key needs an app update |
| The App Store listing (Apple app transfer) | the bundle ID App Attest binds to | a new operator ships the app under their own account and their own bundle ID |
| The Apple DeviceCheck key | reading and setting ban bits | bans start from zero under a new account |

**The cheapest route to a clean handover is to make the directory URL a
setting in the client**, with the shipped default. A new operator who gets
none of the above can still publish their own signed directory, and players
point the app at it. Players lose only what was never portable: the ban list
under the old account. Without that setting, a handover depends entirely on
the transfer.

**Untested**: whether DeviceCheck bits carry over an App Store app transfer
between developer teams. Nothing here has checked Apple's documentation on
that, and it decides whether bans survive a handover. Check it before the
handover, not during.

### When hosts belong to different people

Copying the Apple key to every host is the right call while all hosts are
Kyle's. If hosts ever belong to several people, each copy is a key that can
read and set every device's ban bits, held by someone else. The change then is
**one token service** that holds the Apple key, verifies a device once, and
issues a short-lived signed room token. Every dispatcher, whoever runs it,
checks the token with a public key and never sees Apple's. Until then, the per-host copy is
simpler and the dispatcher's request format should not need to change to
switch: `POST /rooms` carries "a device proof", whichever kind it is.

## Hardening

The dispatcher is a new surface reachable from the internet, and a worse one
than the game socket: one request makes the box start a 100 MB process. It
belongs in `docs/HARDENING.md`'s surface table when it is built. The bounds
are the capacity checks above, the device proof on every create, TLS, and a
request size cap. `GET /rooms/{code}` is rate-limited per address, so a guess
at codes is slow; with about 4 live rooms per region against 2.9 x 10^7 codes,
the chance of a guess hitting is small even before that limit.

## Open questions

- **Android**, if gg2-client ever ships there: Play Integrity is the
  equivalent of App Attest, but nothing like DeviceCheck's reinstall-proof
  bits exists, so a ban there would be weaker.

Settled: rooms are created from mobile only; desktop hosts its own server.
The Apple key is copied to each VPS.
