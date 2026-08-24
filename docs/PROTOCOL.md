# The GG2 wire protocol

Everything here was read out of the reference implementation in
`D:\code\Gang-Garrison-2\Source\gg2` at GG2 v2.9.2 (`VERSION = 29200`). File
references are to that tree. **The stock client is the specification.** Where
this document and `Scripts/Client/ClientBeginStep.gml` disagree, the client is
right and this file has a bug — fix it here.

## Transport

- Plain TCP. No encryption, no compression, no framing beyond what is described
  below. Default port 8190 (`gg2.ini`, `HostingPort`).
- **Little-endian** for every multi-byte integer. `Scripts/Game/game_init.gml:23`
  calls `set_little_endian_global(true)`, and the only exception in the codebase
  is the lobby registration buffer, which explicitly flips to big-endian
  (`sendLobbyRegistration.gml:13`) and which a private server never sends.
- The server sends a **single broadcast byte stream to every client**. There is
  one shared buffer (`global.sendBuffer`); at the end of each tick
  `GameServerEndStep.gml` writes the whole thing to every connected player's
  socket verbatim. Per-client bytes exist only on the join path and for kicks,
  where the server writes to that one socket directly.
- Strings are **length-prefixed, not NUL-terminated**, and are raw bytes — GM8
  strings are byte strings, so treat them as opaque/Latin-1, not UTF-8. The
  prefix is one byte for most messages and two for a few
  (`Scripts/Networking/receivestring.gml`).

### Types

| Name | Bytes | Notes |
|---|---|---|
| `ubyte` | 1 | |
| `byte` | 1 | signed, two's complement |
| `ushort` | 2 | LE |
| `short` | 2 | LE, signed |
| `uint` | 4 | LE |
| `string1` | 1 + n | ubyte length, then n raw bytes |
| `string2` | 2 + n | ushort length, then n raw bytes |

### How a number becomes those bytes

GML has one numeric type — an IEEE-754 double — so the reference routinely
writes a *fractional* value into an integer field and lets the networking
extension decide what that is. It is not a cast.
[Faucet Networking](https://github.com/Medo42/Faucet-Networking-Extension),
`faucet/ReadWritable.hpp`:

```cpp
IntType converted = clipped_cast<IntType>(round(value));
```

**Round half away from zero, then clamp to the type's range.** Both C# defaults
are the opposite answer — `Math.Round` is half-to-even, and an integer cast
wraps — so this has its own transcription in `Gg2.Protocol.WireQuantise` and
`NetWriter.Write*FromReal`. `docs/SIMULATION.md`, "Settled: how a real becomes
an integer on the wire", has the consequences, including the two that bite:
`Character` and `Intelligence` quantise the same coordinate differently, and the
velocity byte saturates asymmetrically.

## Handshake

Driven by `JoiningPlayer` (`Objects/InGameElements/JoiningPlayer.events/Create.xml`
for the state constants, `Scripts/GameServer/serviceJoiningPlayer.gml` for the
transitions) on the server, and `Scripts/Client/ClientCreate.gml` +
`ClientBeginStep.gml` on the client.

```
client -> server   HELLO (0x00) + 16 bytes of PROTOCOL_UUID       (exactly 17 bytes)
```

`PROTOCOL_UUID` is `b31c2209-4256-9a19-d0ef-c71c5373bd75`, sent as its 16 bytes
in textual order (`Scripts/Versioning/parseUuid.gml`). The server compares all
16; on any mismatch it replies `INCOMPATIBLE_PROTOCOL` (43) and drops the
connection. **This value must be reproduced exactly or no stock client will ever
connect.**

Then, if a password is set:

```
server -> client   PASSWORD_REQUEST (25)
client -> server   string1 password
server -> client   PASSWORD_WRONG (27)     on mismatch; connection is then dropped
```

A wrong password is **not** retryable. `serviceJoiningPlayer.gml:70-78` writes
`PASSWORD_WRONG` without assigning `newState`, and the `state = newState` at
line 186 then destroys the `JoiningPlayer` and its socket. The client agrees:
its `PASSWORD_WRONG` case shows a message and destroys the `Client`
(`ClientBeginStep.gml:420-423`). The same "fall through with `newState == -1`"
rule ends the connection after `INCOMPATIBLE_PROTOCOL`, after `SERVER_FULL`, on
a `PLAYER_JOIN` with no prior `RESERVE_SLOT`, and on any unrecognised byte in
`STATE_EXPECT_COMMAND` — that last one is the opposite of the in-game command
loop's behaviour, which silently ignores unknown bytes.

On success (or immediately, with no password) the server sends its greeting:

```
server -> client   HELLO (0x00)
                   string1  serverName
                   string1  currentMap
                   string1  currentMapMD5       ("" means a built-in map)
                   ubyte    pluginsRequired
                   string2  pluginList
```

An **empty MD5 means the map is built into the client** and cannot be
downloaded; a non-empty MD5 means a custom map the client may need to fetch. A
headless server serving only built-in maps should always send `""` here, which
removes the entire map-download path from scope.

The client then enters a command loop. The server sits in `STATE_EXPECT_COMMAND`
reading one byte at a time:

| Client command | Payload | Effect |
|---|---|---|
| `PING` (57) | none | keepalive while the client loads plugins |
| `RESERVE_SLOT` (60) | `string1` name | claims a slot; server replies `RESERVE_SLOT` (60), or `SERVER_FULL` (11) if the roster is full |
| `DOWNLOAD_MAP` (45) | none | server replies `uint` byte count then streams the raw `Maps/<name>.png` |
| `PLAYER_JOIN` (1) | none | **requires a prior `RESERVE_SLOT`**; completes the join |

Names are truncated to `MAX_PLAYERNAME_LENGTH` = 20.

On `PLAYER_JOIN` the server does this, in order
(`serviceJoiningPlayer.gml:108-129`):

1. sends the whole join burst on the joining socket alone
   (`Scripts/Message Builders/ServerJoinUpdate.gml`) — this is **more than a
   `JOIN_UPDATE`**, and a port that sends only the header desynchronises the
   client immediately:

   ```
   JOIN_UPDATE (44)   ubyte playerId        // = roster size *before* the join
                      ubyte currentMapArea
   CHANGE_MAP  (7)    string1 map, string1 md5
   for each existing roster entry i, in order:
       PLAYER_JOIN        (1)  string1 name
       PLAYER_CHANGECLASS (4)  ubyte i, ubyte class
       PLAYER_CHANGETEAM  (3)  ubyte i, ubyte team
   FULL_UPDATE (8)    ...
   ```

   The `CHANGE_MAP` is what makes the client load the map
   (`ClientBeginStep.gml:471`), and the per-player replay is the only way the
   joiner learns the existing roster — the `FULL_UPDATE` that follows carries
   state for players it must already know about, and aborts with "Wrong number
   of players while deserializing state" if it does not.

2. creates the `Player` and appends it to the roster;
3. broadcasts `PLAYER_JOIN` + `string1 name` **to everyone, including the joiner**
   — so the joiner learns about *itself* here, after the replay of everyone else;
4. optionally sends `MESSAGE_STRING` with the welcome message, to that socket only.

## Roster indexing — the thing that breaks everything if you get it wrong

Players are identified **by their index in `global.players`**, a dense list, not
by a stable id. Every message that names a player carries that index as a
`ubyte`.

Two consequences a fresh implementation must honour:

- **Index 0 is reserved for the server's own player.** `GameServerCreate.gml:38`
  creates it unconditionally, before any client can connect, even in
  `-dedicated` mode. A headless port has no local player to simulate, but it
  **must still occupy slot 0** with a roster entry (a permanent spectator is
  fine) or every index a client receives is off by one.
- **`PLAYER_LEAVE` compacts the list.** The client removes the entry and
  decrements its own `global.playerID` if the departing index was lower
  (`ClientBeginStep.gml:212-221`); the server does the same (the `i -= 1` after
  `removePlayer` in `GameServerBeginStep.gml`). Indices are therefore **not
  stable across a leave**, and the port must renumber identically.

Bots are ordinary roster entries with no socket (`Scripts/Bots/botAdd.gml`).
Clients cannot tell a bot from a human — which is exactly why the bot layer is
free to be rewritten however we like.

## Client → server commands

`Scripts/GameServer/GameServerDefineCommands.gml` is the length table and
`processClientCommands.gml` the handler. Framing is: one command byte, then a
fixed number of payload bytes from that table — with two escape values meaning
"the length is itself prefixed".

| Command | Payload length | Notes |
|---|---|---|
| `PLAYER_LEAVE` (2) | 0 | client is disconnecting |
| `PLAYER_CHANGECLASS` (4) | 1 | `ubyte class` |
| `PLAYER_CHANGETEAM` (3) | 1 | `ubyte team`; anything not RED/BLUE/SPECTATOR means "any" |
| `CHAT_BUBBLE` (15) | 1 | `ubyte image` |
| `BUILD_SENTRY` (16) | 0 | |
| `DESTROY_SENTRY` (17) | 0 | |
| `DROP_INTEL` (21) | 0 | |
| `OMNOMNOMNOM` (24) | 0 | sandvich |
| `TOGGLE_ZOOM` (41) | 0 | |
| `PLAYER_CHANGENAME` (31) | 1-byte length prefix | rate-limited to one per 1000 ms |
| `INPUTSTATE` (6) | 4 | see below |
| `REWARD_REQUEST` (50) | 1-byte length prefix | |
| `REWARD_CHALLENGE_RESPONSE` (52) | 16 | |
| `PLUGIN_PACKET` (55) | 2-byte length prefix | |
| `CLIENT_SETTINGS` (58) | 1 | `ubyte queueJump` |

Any other byte is **invalid and silently ignored** — the server does not
resynchronise, it waits for the next byte and tries again
(`commandBytesInvalidCommand`). Reproduce that; a stricter port would kick
clients the reference tolerates.

The server processes **at most 10 commands per player per tick** as flood
protection (`commandLimitRemaining = 10`), leaving the rest buffered.

### INPUTSTATE (client → server), 4 bytes

```
ubyte   keyState
ushort  aimDirection    // degrees * 65536 / 360
ubyte   aimDistance     // world distance / 2, clamped to 255
```

Decoded server-side as `netAimDirection * 360 / 65536` and `byte * 2`. Key bits
(`Constants.xml`):

```
KEY_TAUNT   0x01     KEY_ATTACK  0x10
KEY_DOWN    0x02     KEY_RIGHT   0x20
KEY_SPECIAL 0x08     KEY_LEFT    0x40
                     KEY_JUMP    0x80
```

`0x04` is unused.

## Server → client messages

`ClientBeginStep.gml` is the complete list — 47 `case` labels, one per message
the client understands. The ones with non-obvious framing:

| Message | Payload |
|---|---|
| `JOIN_UPDATE` (44) | `ubyte playerId`, `ubyte currentMapArea` |
| `PLAYER_JOIN` (1) | `string1 name` |
| `PLAYER_LEAVE` (2) | `ubyte playerId` |
| `PLAYER_DEATH` (10) | `ubyte victim`, `ubyte killer`, `ubyte assistant`, `ubyte cause` |
| `PLAYER_SPAWN` (5) | `ubyte playerId`, `ubyte`, `ubyte` |
| `PLAYER_CHANGENAME` (31) | `ubyte playerId`, `string1 name` |
| `CHAT_BUBBLE` (15) | `ubyte playerId`, `ubyte image` |
| `BUILD_SENTRY` (16) | `ubyte playerId`, `ushort x*5`, `ushort y*5`, `byte dir` |
| `MAP_END` (14) | `string1 nextMap`, `ubyte winners`, `ubyte currentMapArea` |
| `CHANGE_MAP` (7) | `string1 map`, `string1 md5` |
| `INPUTSTATE` / `QUICK_UPDATE` / `FULL_UPDATE` / `CAPS_UPDATE` | see next section |
| `KICK` (38) | `ubyte reason` (`KICK_NAME` 39, `KICK_BAD_PLUGIN_PACKET` 56, `KICK_MULTI_CLIENT` 59) |

For anything not listed, read the matching `case` in `ClientBeginStep.gml`
together with the server-side `write_*` calls that produce it — that pair is the
authoritative definition and takes about two minutes per message. Do not guess
from the name.

## State updates — the hot path

`Scripts/Serialization/serializeState.gml` writes them; `deserializeState.gml`
reads them. Four tiers, and **the cadence is part of the contract** because the
client dead-reckons between the position-bearing ones
(`GameServerBeginStep.gml:88-95`):

```
every tick, except every 7th   INPUTSTATE   (6)
every 7th tick                 QUICK_UPDATE (9)
on join                        FULL_UPDATE  (8)
on cap / every 3600 ticks /
  setup timer hitting 180      CAPS_UPDATE  (28)
```

Layout, after the leading message byte:

```
FULL_UPDATE only:   ushort  tdmInvulnerabilityTicks
all tiers:          ubyte   playerCount        // client aborts if it disagrees
```

then, for every tier except `CAPS_UPDATE`, **one block per roster entry in
roster order**, then one block per `MovingPlatform` in instance order.

Per player (`Objects/InGameElements/Player.events/User Event 12.xml`):

```
FULL_UPDATE only:
  ubyte   kills, deaths, caps, assists, destruction, stabs
  ushort  healing
  ubyte   defenses, invulns, bonus, points
  ubyte   queueJump
  string2 unparsedRewards
  ubyte   dominationKills   x (playerCount - 1), skipping self, in roster order

all tiers:
  ubyte   subobjects     // 0x01 = has a Character, 0x02 = has a Sentry
  <Character block>      if 0x01
  <Sentry block>         if 0x02
```

Per character (`Objects/InGameElements/Character.events/User Event 12.xml`) —
this is the block that matters:

```
all tiers:
  ubyte   keyState
  ushort  netAimDirection
  ubyte   aimDistance / 2

QUICK_UPDATE and FULL_UPDATE:
  ushort  x * 5
  ushort  y * 5
  byte    hspeed * 8.5
  byte    vspeed * 8.5
  ubyte   ceil(hp)
  ubyte   currentWeapon.ammoCount
  ubyte   flags       // 0x01 = cloak, bits 1..3 = moveStatus & 0x07

FULL_UPDATE only:
  ubyte   animationOffset
  ubyte   class-specific    // spy cloakAlpha*255 | medic uberCharge*255/2000
                            // | engineer nutsNBolts | sniper charge | else 0
  short   alarm[1] * delta_factor
  ubyte   intel
  short   intelRecharge
  <weapon block>            // the weapon's own User Event 12
```

Note the quantisation: position to **1/5 world pixel** in an unsigned 16-bit
field (so the coordinate space tops out at 65535/5 = 13107), and velocity to
**1/8.5 px/tick in a signed byte**, saturating at ±15 — conveniently the same
clamp the physics applies (`Character.events/Step.xml`).

`FULL_UPDATE` additionally closes with the intel entities, the cap counters, the
gamemode HUD block and the ten class limits; `CAPS_UPDATE` carries only the cap
counters, respawn time and HUD block. Read `serializeState.gml` directly for
those — they are short and unambiguous.

## What a headless server can skip

Not everything in the reference is load-bearing for a private headless server:

- **Lobby registration** (`sendLobbyRegistration.gml`) announces to
  `ganggarrison.com:29944`. Skip it, or gate it behind a flag that defaults off.
- **Reward/auth** (`REWARD_*`, `Documentation/Authentication.txt`) talks to an
  external auth server. Answer `REWARD_REQUEST` with nothing and never grant
  rewards.
- **Plugins** (`PLUGIN_PACKET`, `pluginList`). Advertise an empty plugin list and
  `pluginsRequired = 0`; the client's plugin path is then never entered.
- **Map download.** Serve only built-in maps and always advertise an empty MD5.
- **UPnP port forwarding** (`GameServerCreate.gml:5-19`).
- **The loopback socket.** The reference server connects to itself over TCP so
  the host's local player is "just another client". A headless port has no local
  player: keep the roster slot, drop the socket.

None of that is visible to a stock client beyond a server that has no plugins,
no rewards, and only built-in maps.
