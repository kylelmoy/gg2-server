# Private rooms

A player in gg2-client asks for a server for themselves and their friends,
gets a short code to share, and the server goes away once everybody has left.
Nobody hosts from a phone. This is the industry's session-based dedicated
server pattern (Agones allocation, GameLift game sessions, PlayFab
multiplayer servers), sized for a fleet of small VPSes.

**Status: planned, step 1 built.** The table below says which parts exist and
what proves each. Nothing else in this file exists until its row says so.

| Step | Part | State |
|---|---|---|
| 1 | `--exit-when-empty` on gg2-server | **Built.** `EmptyServerExitTests`; the real host exits 0 three seconds after an empty start |
| 2 | `gg2-room@.service` template unit | not started |
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
3. Friends type the code. Its first letter names the region, so the client
   knows which dispatcher to send `GET /rooms/{code}` to, and gets the same
   address and password. No central directory is needed.
4. The server exits on its own once nobody has been connected for
   `ExitWhenEmptySeconds`. systemd reaps it, and the port is free again.

**Nothing new reaches the wire.** A room is an ordinary server with a
password and lobby announcing off. A stock GM8 client given the address and
password can join it too. None of this touches the simulation.

## The pieces

### 1. `--exit-when-empty <seconds>` (built)

`ServerOptions.ExitWhenEmptySeconds`, `[Runtime] ExitWhenEmptySeconds` in a
config file. `EmptyServerExit` is the rule:

- The clock starts at startup, so a room nobody joins goes away after the
  same interval as one everybody left.
- A person is a connection: a spectator and a half-finished handshake count,
  bots do not.
- Wall clock, not ticks, because an empty server idles at 2 Hz.
- The process exits **0**, so a `Restart=on-failure` unit leaves it stopped.
  Under `Restart=always` it would come back empty, which is why no fleet
  server is given this setting.

The intended value for a room is 120 seconds. That covers the creator's
connect after the dispatcher replies, and a stock client dropping and
reconnecting at a map change to a custom map it lacks (`docs/DEPLOY.md`
has that failure).

### 2. A systemd template unit

`gg2-room@.service`, where the instance name is the port:
`systemctl start gg2-room@8301`. It copies the fleet unit's sandboxing and adds:

- `--config /var/lib/gg2-rooms/%i/gg2.ini`, written by the dispatcher;
- `Restart=on-failure`, never `always`;
- `RuntimeMaxSec=` as the hard lifetime (planned: 6 hours), so a room that
  somebody keeps open with an idle client still ends;
- `MemoryMax=` per room (planned: 200 MB; a server measures 96-129 MB from 1
  to 24 clients, `docs/DEPLOY.md`).

The dispatcher keeps no state of its own. On startup it rebuilds the room list
from `systemctl list-units 'gg2-room@*'` and the ini files beside them, so a
dispatcher crash loses nothing.

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

Both need an Apple developer account's private key on the server side. It
lives in one place: a small verification step all three dispatchers call, or
copied to each with the dispatcher's other secrets. That is undecided.

**Desktop** has no platform attestation. Until it has one, desktop clients can
join rooms by code but not create them. That is the default, and the open
question below.

**The ban list** is keyed by App Attest key ID, with the DeviceCheck bit as
the reinstall-proof half. Every room's ini records the creator's key ID, so a
report against a room can be traced to a device.

### 5. gg2-client

"Create private game" (region picker, map or rotation, bots on or off) and
"Join by code". Joining uses the existing password flow. The creator's client
shows the code in the in-game menu so it can be shared mid-match.

## Hardening

The dispatcher is a new surface reachable from the internet, and a worse one
than the game socket: one request makes the box start a 100 MB process. It
belongs in `docs/HARDENING.md`'s surface table when it is built. The bounds
are the capacity checks above, the device proof on every create, TLS, and a
request size cap. `GET /rooms/{code}` is rate-limited per address, so a guess
at codes is slow; with about 4 live rooms per region against 2.9 x 10^7 codes,
the chance of a guess hitting is small even before that limit.

## Open questions

- **Desktop creates.** Allow them behind a weaker check (a signed install key,
  stricter limits), or keep creating rooms on iOS only?
- **Where the Apple key lives**: one verifier or a copy per dispatcher.
- **Room settings exposed to the player**: map choice, rotation, bot count,
  player limit. Fewer choices mean a smaller ini surface to validate.
