# Private rooms

A player in gg2-client asks for a server for themselves and their friends,
gets a short code to share, and the room goes away once everybody has left.
Nobody hosts from a phone. This is the industry's session-based dedicated
server pattern (Agones allocation, GameLift game sessions, PlayFab
multiplayer servers), built so that one process holds many rooms.

**Status: steps 1 and 2 built; the room host has no command or API yet.** The table says which parts exist and what proves
each. Nothing else in this file exists until its row says so.

| Step | Part | State |
|---|---|---|
| 1 | A server stops itself: empty timeout and hard lifetime | **Built.** `EmptyServerExitTests`; the real host exits 0 three seconds after an empty start |
| 2 | The room host: one process, one port, many rooms | **Built** as a library (`RoomHost`, `RoomFrontDoor`, `SharedNav`). `RoomHostTests` drive real sockets through the one port. Not yet run against a stock GM8 client, and no command starts one |
| 3 | The dispatcher API, inside the room host | not started |
| 4 | Device check and the ban list | not started |
| 5 | gg2-client: create, join by code | not started |
| 6 | Fleet rollout: one unit and one port per VPS | not started |

## Where rooms run

**On the three VPSes only**: `gg2-us-west`, `gg2-us-east`, `gg2-de`. Not on
barbara, which is a home connection. Each VPS also runs its public fleet
server, and that server's slots come first: the room host's capacity check is
what protects them.

## The shape

```
gg2-client ──HTTPS──> room host (one process per VPS) ──in memory──> room
     │                    │ game port: the front door
     └─── GG2 TCP ────────┘ routes each connection by its password
```

1. The client pings every room host, picks the nearest one that says it has
   room, and sends `POST /rooms` with a device proof (below) and the room's
   choices.
2. The room host checks capacity, builds the room in memory, and replies
   `{code, host, port, expiresAt}`. Creating a room starts no process.
3. Friends type the code. Its first letter is the host's prefix in the
   directory (below), so the client knows which host to connect to.
4. Every room on a host shares that host's one game port. **The code is the
   room's password**, and the front door reads the password from the
   handshake and hands the connection to that room.
5. A room ends on its own once nobody has been connected for 120 seconds, or
   at its hard lifetime. The host drops it from memory.

**Nothing new reaches the wire.** To a client a room is an ordinary
password-protected server. A stock GM8 client given the address and the code
can join it too, typing the code into the password dialog. None of this
touches the simulation.

## Why one process holds the rooms

The numbers below are the ones in `docs/DEPLOY.md`, scaled to 100,000 players
at about ten per room, so 10,000 rooms.

| Resource | Per room | 10,000 rooms | |
|---|---|---|---|
| CPU | 0.15-0.23 ms of a 33.3 ms tick | ~50 cores | cheap |
| Memory, one process per room | 96-129 MB | ~1 TB | mostly per-process overhead |
| Bandwidth | ~1.5 kbit/s per roster entry per client, ~24 kbit/s per client in a 16-player room | ~2.4 Gbit/s out | the real cost at that scale |

Most of a process's memory is the runtime, the maps, and the nav tables
(10.8 MB for one map's next-hop tables), each loaded once per process. A
match's own state is small: `WorldState` is a flat value type with no
references. So rooms in one process, sharing what is read-only, cost about a
megabyte each rather than a hundred: measured below, under "The room host".

The rest of the design follows from rooms being objects instead of
processes: creating one is building a `WorldState`, so it takes microseconds
rather than a one-second start; one listening port serves every room, so there
is no port pool or firewall range; and the host knows every room it has, so
the dispatcher is part of it rather than a separate service driving systemd.

What does **not** change: the simulation, and the language. CPU is not what
runs out, the tick loop allocates nothing, and rewriting the physics in
another language would mean re-proving the six roundings, the `x -= hspeed`
ULP and the x87 `ceil` against every parity fixture to save memory that
sharing one process already saves. If memory per process still mattered
after that, .NET Native AOT is the cheaper step.

## The pieces

### 1. A server stops itself (built)

`EmptyServerExit` is the rule, and every room in the host runs it:

- **Empty**: `ServerOptions.ExitWhenEmptySeconds`, `--exit-when-empty`,
  `[Runtime] ExitWhenEmptySeconds`. The clock starts at startup, so a room
  nobody joins goes away after the same interval as one everybody left. A
  person is a connection: a spectator and a half-finished handshake count,
  bots do not. Wall clock, not ticks, because an empty server idles at 2 Hz.
  120 seconds for a room, which covers the creator's connect and a stock
  client dropping and reconnecting at a map change (`docs/DEPLOY.md`).
- **Lifetime**: `ServerOptions.MaxLifetimeMinutes`, `--max-lifetime`,
  `[Runtime] MaxLifetimeMinutes`. Ends a room whoever is on it (planned: 6
  hours), so an idle client cannot hold one open. Players are dropped without
  warning; a chat notice before the end does not exist yet.

`GameServer.SelfExit` says which happened. A standalone server that stops
itself exits 0, which a `Restart=on-failure` unit leaves alone. That is not
systemd's `RuntimeMaxSec`: systemd counts that stop as a timeout and
`Restart=on-failure` restarts it (measured on systemd 255).

### 2. The room host

One process per VPS, a `gg2-server rooms` subcommand, run by an ordinary
systemd unit. It holds:

- **The front door**: the one game listener. It accepts a connection, checks
  the 17-byte HELLO, sends `PASSWORD_REQUEST`, and reads the password. The
  handshake allows this for a stock client, because the password comes
  **before** anything that names a server or a map (`docs/PROTOCOL.md`). A
  password that names a room hands the connection, and any bytes after the
  password, to that room, which carries on from the greeting. Anything else
  gets `PASSWORD_WRONG` and is dropped, which is the stock behaviour for a
  wrong password. The front door owns the connection limits that are about
  the host: the accept rate per address, the cap on handshakes in flight, and
  the handshake timeouts.
- **The rooms**: one `GameServer` each, with no listener of its own. A room
  takes handed-over connections where a standalone server takes accepted ones,
  and still applies its own per-room rules (seats, `MultiClientLimit`).
  Everything a standalone server does per process (lobby, web panel, control
  port, recording, analytics) is off for a room.
- **The loop**: ticks every room at 30 Hz on one thread to begin with.
  Each room has its own fault budget, so a room whose ticks keep failing is
  ended alone instead of taking the host down. A room that stops itself is
  disposed and its code freed.

**Shared data**: `SharedNav` loads each map's nav graph and next-hop tables
once per host, and every room holds a reference. Both types write their arrays
only in their constructors, which is what makes sharing safe. A miss is cached,
nothing is evicted (bounded by the shipped maps), and a graph a room GENERATED
for a map with none is never shared. Maps themselves are still loaded per room;
they are small next to the tables.

**Measured** (Release, Windows, one 13700K core, `ctf_truefort`, ten scripted
bots per room, no clients connected):

| | 1 room | 10 rooms | 30 rooms |
|---|---|---|---|
| managed heap | 12.3 MB | 19.9 MB | 37.0 MB |
| process private memory | 47 MB | 56 MB | 65 MB |
| creating one more room | 197 ms | 5 ms | 3 ms |
| one host tick, all rooms | | | 4.2 ms |

So a room after the first costs **about 0.85 MB of heap** and about 0.14 ms of
a tick: roughly 230 rooms per core by CPU, and memory is no longer what runs
out. Before `SharedNav` each room cost about 12 MB of heap, almost all of it
its own copy of the tables. The 1- and 10-room tick samples include warm-up and
are not quoted. **Not measured**: connected clients (serialisation and
bandwidth per client), maps other than `ctf_truefort`, a VPS core, and policy
bots, whose nets are loaded per room today.

**Later, if a host fills a core**: one tick thread per core with rooms spread
across them and their ticks staggered through the 33 ms window, and the bots'
policy nets evaluated in one batch across rooms. Batching can change the
summation order of a net's floats, which is allowed because a bot's decisions
are not physics on the wire.

### 3. The dispatcher API

HTTP inside the room host, behind TLS.

| Endpoint | Does |
|---|---|
| `GET /status` | region, rooms in use, rooms free. What the client's region picker reads |
| `POST /rooms` | device proof plus room choices; creates a room or says why not |
| `GET /rooms/{code}` | whether a room is live, and its address |

**Capacity**, checked on every create: a room cap per host, a floor on
`MemAvailable` so the fleet server is never squeezed, and per-device and
per-address limits (one live room per device, a few creates an hour). The cap
is set from step 2's measured cost per room.

**Room codes**: a host prefix letter plus five characters from an alphabet
without look-alikes (no `0/O`, `1/I/L`): 31^5, about 2.9 x 10^7 per host. The
five characters are the room's password.

**A restart ends every room on the host**, so a deploy waits for the host to
empty, the same way a config push already waits for a fleet server to
(`docs/CONFIG.md`). A host that is draining refuses new rooms.

### 4. Device check and bans

The point is to make bans stick, so abuse costs a device rather than a
reinstall.

**iOS** uses Apple's two services together:

- **App Attest** proves a request comes from the real app on a real device.
  At first launch the app makes a key, Apple attests it, and the host checks
  the attestation once and remembers the key. Every `POST /rooms` carries an
  assertion signed with that key. The key ID is the identity rate limits and
  room ownership use.
- **DeviceCheck** gives two bits per device that **survive a reinstall**. A
  ban sets one of them through Apple's server API, and a create from a device
  with the bit set is refused, whatever its App Attest key. That is what makes
  a ban stick.

Both need an Apple developer account's private key on the server side. **A
copy lives on each VPS**, outside the release tree and readable only by the
room host's user, so each host verifies on its own and no host depends on
another.

**Only mobile clients can create rooms.** Desktop has no platform attestation,
and does not need allocation: a desktop client hosts its own server the way
the GM8 game does, by running gg2-server in-process and sharing its address.
A desktop client still joins a room by code.

**The ban list** is keyed by App Attest key ID, with the DeviceCheck bit as
the reinstall-proof half. Every room records its creator's key ID, so a
report against a room can be traced to a device.

### Room settings (agreed)

Just enough for a simple game on a phone. Everything the player chooses maps
onto a setting that already exists, so the host builds a fixed template and
fills in three values. It accepts only these values and refuses anything else.

| Choice | Options | Sets |
|---|---|---|
| Map | **Rotation** (the stock rotation, `ShuffleRotation=arena-last`), or any one of the 22 shipped maps. The game mode follows from the map | `Map`, `Rotation`, `ShuffleRotation` |
| Bots | **Off**, Easy, Normal, Hard, Adaptive | `[Bots] Enabled`; `Difficulty` 1 / 3 / 5; Adaptive is `AutoDifficulty=1` |
| Team size | Small (fill to 6), **Medium** (fill to 10), Large (fill to 16). Only matters with bots on | `FillToPlayers`, `MaxBots` |

Bold is the default. Fixed for every room:

| Setting | Value | Why |
|---|---|---|
| `PlayerLimit` | 12 | A friends' game |
| `Time Limit`, `CapLimit`, respawn, `AutoBalance` | stock | One set of rules everybody already knows |
| `UseLobby` | 0 | Private |
| `Password` | the code | The code is the invitation |
| `[Bots] Mode` and nets | the fleet's, from `deploy/config/common.ini` | Same bots as the public servers |
| `[Chat] Enabled` | 1 | gg2-client has native chat |
| `QuickUpdateInterval` | 7 (stock) | |
| `[Analytics]`, recording, `[Web]`, control port | off | A private room records nothing about who played |
| `ExitWhenEmptySeconds`, `MaxLifetimeMinutes` | 120, 360 | Step 1 |

**Shipped maps only**: a custom map would need a download for stock clients
and a nav graph built at the map change, and neither is worth it for this.

### 5. gg2-client

"Create private game" (region picker, map or rotation, bots, team size) and
"Join by code". Joining answers the password request with the code. The
creator's client shows the code in the in-game menu so it can be shared
mid-match.

### 6. Rollout

One `gg2-rooms.service` per VPS, from the same release as the fleet server,
and one more TCP port open in `ufw` (planned: 8300) plus the HTTPS port.

## A changing set of hosts

The hosts will not stay the same three: a provider gets swapped, a region gets
added, or one day somebody else runs the servers. So **no host is built into
the client**, and nothing in a room's lifecycle depends on more than the one
host it runs on.

### The directory

The client learns the room hosts from a **signed directory**: a small JSON
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
- **A host that is down is skipped.** The client already pings every host's
  `/status` to pick a region, so a dead entry costs one timeout and nothing
  else. Liveness is never written into the directory.
- **A prefix belongs to a host.** A room code's first letter is looked up in
  the directory, so it names a host and not a provider. A prefix is not
  reused for a different host within one room lifetime of retiring it, or an
  old code would resolve on the wrong machine.

### Switching providers

Each host's URL is a hostname under a domain the operator controls, so moving
a host is: provision the new VPS from `docs/DEPLOY.md`, install the room host,
copy the Apple key, move the DNS name, and retire the old box after its last
room has ended (at most one room lifetime). The directory does not change
unless the region does. A new region is one new entry and a re-signed
directory.

**Bans survive the move** because the reinstall-proof half lives in Apple's
DeviceCheck bits, not on any host. The App Attest key IDs each host has seen,
and its per-key limits, are local and start fresh on a new box. That is
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
issues a short-lived signed room token. Every host, whoever runs it, checks
the token with a public key and never sees Apple's. `POST /rooms` carries "a
device proof", whichever kind it is, so the switch does not change the request.

## Beyond a handful of hosts

What changes if rooms ever need many hosts per region rather than one:

- **Room codes resolve through a shared key-value store** instead of a prefix
  per host, so hosts come and go without the directory changing. The
  directory then lists regions.
- **Hosts report free capacity to a regional registry** every few seconds, and
  a create goes to the host with the most room.
- **Hosts scale** from a cloud provider against rooms in use, with the
  per-device limits enforced in one place rather than per host.
- **The token service** above replaces the per-host Apple key.

The client sees none of it: a create still returns an address and a code, a
join is still a password, and the device proof is opaque.

## Hardening

The room host's HTTP API is a new surface reachable from the internet. It
belongs in `docs/HARDENING.md`'s surface table when it is built. The bounds are
the capacity checks above, the device proof on every create, TLS, and a
request size cap. The front door is the game socket's surface with one
addition, guessing a room's password, and the accept rate per address already
bounds that: a wrong password ends the connection, so every guess costs a
connect. With a few live rooms per host against 2.9 x 10^7 codes, a guess
hitting is unlikely even before the rate limit.

## Known gaps

- **Every room's bots roll alike.** A room's bot population and plugin RNG are
  seeded from `ServerOptions.Port`, which is the host's one port for every
  room, so rooms fill with the same names and classes in the same order.
  `WorldState.Random` is not affected; it is seeded from a fresh RNG.
- **Policy bots are not shared.** A room in `Mode=policy` loads its own nets,
  and a brain object cannot simply be shared, because its decision arbiter
  keeps per-tick budget state. Unmeasured.
- **No test makes a room's tick throw**, so the per-room fault budget has never
  run.
- **No pre-lifetime warning** reaches the players before `--max-lifetime`
  drops them.

## Open questions

- **Android**, if gg2-client ever ships there: Play Integrity is the
  equivalent of App Attest, but nothing like DeviceCheck's reinstall-proof
  bits exists, so a ban there would be weaker.
