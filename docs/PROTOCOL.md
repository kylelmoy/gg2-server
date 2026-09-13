# The GG2 wire protocol

Extracted from the reference implementation at GG2 v2.9.2 (`VERSION = 29200`),
`D:\code\Gang-Garrison-2\Source\gg2`. File references are to that tree.
**The stock client is the specification.** Where this document and
`Scripts/Client/ClientBeginStep.gml` disagree, the client is right and this file
has a bug — fix it here.

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

`pluginList` is `name@md5` per plugin, joined by **bare commas**, and empty when
the server asks for none — which is the default. **The client derives each
plugin's packet id from this string's ORDER** and nothing on the wire names a
plugin again, so it is never re-sorted and never spaced. `pluginsRequired` picks
which dialog the client shows and is not enforced by either side. Both fields,
and the `PLUGIN_PACKET` they enable, are `docs/PLUGINS.md`.

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

### The four steps are one write, and splitting them kills the client

Steps 1 to 4 must reach the joining socket together. A joiner that receives the
burst without the `PLAYER_JOIN` naming itself dies on the first step it takes in
the map room, with:

```
ERROR in action number 1 of End Step Event for object Spectator:
Error in code at line 4:
   if(global.myself.object != -1)
at position 19: Unknown variable object
```

followed by the same error from `AmmoCounter`'s Draw at its line 8.

The client stops reading the burst at the `CHANGE_MAP`: it sets `roomchange`
(`ClientBeginStep.gml:471`) and the receive loop ends on it (`:654`), so the
rest of the burst stays in the socket while the room change runs and
`basicRoomSetup.gml:82` creates a `Spectator` in the new room. The client drains
what is left on its next step. If its own `PLAYER_JOIN` is not in that drain,
`ds_list_size(global.players)-1 == global.playerID` never holds
(`ClientBeginStep.gml:205`) and `global.myself` is still the `-1` that
`ClientCreate.gml:20` left. In GM8 `-1` is `self`, so `global.myself.object`
reads `Spectator.object`, which does not exist. Neither that read nor
`AmmoCounter`'s is guarded; only the `global.myself.team` line above the first
one is.

The reference never splits them, and not by intent: `serviceJoiningPlayer.gml:118`
hands the socket to the new `Player` and sets `socket = -1`, so the `socket_send`
at `:185` sends nothing. The burst waits in the socket's own buffer until
`GameServerEndStep.gml:6-7` appends `global.sendBuffer` — carrying this player's
broadcast `PLAYER_JOIN` — and sends both in one call.

A port that flushes the burst on its own leaves the joiner one client step from
the crash: the client survives by however long it waited to poll after the burst
arrived, which is why the split passes on a LAN and fails on a link with jitter,
or behind any tick that runs long. `GameServer.CompleteJoin` therefore sends the
pending broadcast on the joining socket before it flushes, and advances that
client's `SkipBroadcastBytes` past it so `FlushBroadcast` does not send it twice.
The bytes and their order are unchanged; only when they leave.

### `PLAYER_JOIN` does not arrive alone

The client writes `PLAYER_JOIN` and then, into the same buffer and before a
single `socket_send`, two optional commands (`ClientBeginStep.gml:162-176`):

| Command | Sent when | Shape |
|---|---|---|
| `REWARD_REQUEST` | `global.rewardKey` and `global.rewardId` are both set | length-prefixed, up to 255 bytes |
| `CLIENT_SETTINGS` | `global.queueJumping` is on — the `Queued Jumping` option | one payload byte |

So the segment that finishes a handshake routinely carries in-game commands
behind it, and **whatever the handshake reader does not consume belongs to the
player it is about to create.** The reference has no handover to get wrong:
`serviceJoiningPlayer.gml:118` gives the *socket* to the `Player`, so one receive
buffer serves the handshake and then the game.

A port that gives the new client a fresh buffer drops those bytes, and the damage
is worse than the command it loses. A write can be split, so the dropped tail can
be the first part of a command whose remainder arrives afterwards — and the
framer then resumes in the middle of one, reading payload as command bytes and
skipping them (`ConnectionLimits.MaxInvalidCommandBytes` counts exactly that, and
eventually disconnects for it).

`GameServer.CompleteJoin` appends `JoiningConnection.Unconsumed` to the new
client's buffer before it is serviced.
`JoinAndBroadcastTests.KeepsACommandThatArrivedWithThePlayerJoin` is the gate; it
fails without that line, leaving the client in `Spectator` with its
`PLAYER_CHANGETEAM` gone.

`JoinAndBroadcastTests.TheJoinerHearsAboutItselfInTheSameTickAsItsBurst` is the
gate, and it goes red if a tick is inserted before the joiner's own
`PLAYER_JOIN`. It was written from a live A/B: a stock GM8 client against a
server stalled 150ms after the burst dies with the errors above when the two are
split and joins cleanly when they are not.

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
| `PLUGIN_PACKET` (55) | 2-byte length prefix | `ubyte packetId` then the plugin's data; the prefix **covers the id byte**. An id outside the advertised list is a `KICK` / `KICK_BAD_PLUGIN_PACKET`. `docs/PLUGINS.md` |
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
| `PLAYER_DEATH` (10) | `ubyte victim`, `ubyte killer`, `ubyte assistant`, `ubyte cause` (see below) |
| `PLAYER_SPAWN` (5) | `ubyte playerId`, `ubyte`, `ubyte` |
| `PLAYER_CHANGENAME` (31) | `ubyte playerId`, `string1 name` |
| `CHAT_BUBBLE` (15) | `ubyte playerId`, `ubyte image` |
| `BUILD_SENTRY` (16) | `ubyte playerId`, `ushort round(x*5)`, `ushort round(y*5)`, `byte dir` |
| `SENTRY_POSITION` (46) | `ubyte playerId`, `ushort round(x*5)`, `ushort round(y*5)` — the landing spot, from the sentry's Begin Step the tick `vspeed` reaches zero |
| `DESTROY_SENTRY` (17) | `ubyte playerId`, `ubyte killer`, `ubyte assistant`, `ubyte cause` — same 255-for-nobody rule as `PLAYER_DEATH`; the assistant is the **killer's healer** (a sentry has no second dealer), and the Begin Step self-destruct sends `cause` 0. ⚠️ An owner detonating their own sentry broadcasts **nothing** — `processClientCommands.gml`'s case is a bare `instance_destroy`, and clients notice through the subobject bit of the next update |
| `MAP_END` (14) | `string1 nextMap`, `ubyte winners`, `ubyte currentMapArea` |
| `CHANGE_MAP` (7) | `string1 map`, `string1 md5` |
| `INPUTSTATE` / `QUICK_UPDATE` / `FULL_UPDATE` / `CAPS_UPDATE` | see next section |
| `KICK` (38) | `ubyte reason` (`KICK_NAME` 39, `KICK_BAD_PLUGIN_PACKET` 56, `KICK_MULTI_CLIENT` 59) |

`PLAYER_DEATH`'s `killer` and `assistant` are **255 for nobody**, not 0 — zero
is roster slot 0, which is the server's own reserved player.

Its `cause` is a damage-source id, and **there is no table of those in the
reference**. `Scripts/Kill Log/initializeDamageSources.gml` calls
`createGlobalDamageSource` once per source and `createDamageSource` returns
`ds_list_size` *after* the add, so the ids are the order of those calls starting
at **1**: needlegun, rifle, charged rifle, minegun, minigun, flamethrower,
scattergun, shotgun, rocketlauncher, revolver, sentry turret, blade, bubble,
reflected rocket, reflected sticky, knife, backstab, flare, reflected flare, kill
box, frag box, pitfall, finished off, finished off gib, bid farewell, generator
explosion. `Gg2.Protocol.DamageSource` has them; 0 is free and is used there for
the reference's `lastDamageSource = -1`. Reordering that script renumbers the
wire.

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
  <Sentry block>         if 0x02   // ...but see below: EMPTY on an INPUTSTATE
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

The weapon block is **FULL_UPDATE only** — it is written inside the character's
own `if(global.updateType == FULL_UPDATE)`, so unlike the sentry there is no
tier trap in it. Its shape depends on the class: two bytes for eight of them
(`readyToShoot`, `alarm[0]`), **one** for a medic (`healTarget`, or 255), and for
a demoman a count followed by one seven-byte record per mine
(`Objects/Weapons/Minegun.events/User Event 12.xml`):

```
Demoman weapon block:
  ubyte   lobbed            // how many mine records follow
  per mine, in INSTANCE order (creation order):
    ushort  round(x * 5)    // GML round(), half to even
    ushort  round(y * 5)
    byte    round(hspeed * 5)   // ⚠️ 5, not the character's 8.5
    byte    round(vspeed * 5)
    ubyte   stickied
```

⚠️ **The count is a promise and the client reads the records positionally**, so
the two must be produced by one walk. `MineUpdateLayoutTests` round-trips the
real writer through the real reader, which is the only way to catch a mismatch
between a `WeaponState`'s tracked count and what actually gets written.

Per sentry (`Objects/InGameElements/Sentry.events/User Event 12.xml`):

```
FULL_UPDATE only:
  byte    startDirection
  ushort  round(x * 5)      // GML round(), half to EVEN, like Intelligence
  ushort  round(y * 5)      // and unlike Character, which writes plain x*5

QUICK_UPDATE and FULL_UPDATE:
  ubyte   built*0x80 + min(127, hp)

FULL_UPDATE and only while !built:
  ubyte   vspeed
```

⚠️ **On an `INPUTSTATE` a sentry contributes NOTHING — not even its hp.** The
event guards each group on the tier and between them they cover only
`FULL_UPDATE` and `QUICK_UPDATE`, so on an `INPUTSTATE` every branch is skipped
and the player's block ends at its `subobjects` byte with bit 1 still set. A
reader that consumes the hp byte unconditionally slips one byte per sentry on
every tick that is not a quick update (`GameServerBeginStep.gml:99`), which is
most of them. No recorded fixture contains an engineer, so
`SentryUpdateLayoutTests` round-trips the writer through the reader directly
rather than relying on a recording; `FixtureFramingTests` gates the recordings.

⚠️ Note also that the trailing `vspeed` is **self-describing through bit 7 of
the byte before it**: a built sentry has no `vspeed`, a falling one does. A
reader has to look at the value it just read to know the width of the block it
is in.

Note the quantisation: position to **1/5 world pixel** in an unsigned 16-bit
field (so the coordinate space tops out at 65535/5 = 13107), and velocity to
**1/8.5 px/tick in a signed byte**, saturating at ±15 — conveniently the same
clamp the physics applies (`Character.events/Step.xml`).

`FULL_UPDATE` additionally closes with the intel entities, the cap counters, the
gamemode HUD block and the ten class limits; `CAPS_UPDATE` carries only the cap
counters, respawn time and HUD block. Read `serializeState.gml` directly for
those — they are short and unambiguous.

### Per intel entity

`serialize(IntelligenceRed)` then `serialize(IntelligenceBlue)`, each a
`ushort` instance count followed by that many `User Event 10` blocks:

```
ushort  instance count       // 0 or 1
  ushort  round(x * 5)       // GML round(), half to EVEN
  ushort  round(y * 5)
  short   alarm[0]           // ticks until it returns itself; -1 at rest
```

⚠️ **A briefcase somebody is carrying writes a count of zero.**
`doEventGrabIntel` destroys the instance, so "carried" is a *shorter* message
rather than a flagged one, and the client's `deserialize` destroys every
instance it has and recreates exactly `count` of them. Writing a block anyway
slips six bytes and surfaces hundreds of bytes later as a framing error.
`IntelUpdateLayoutTests` round-trips the real writer through the real reader.

### The KotH HUD block, and the control points

```
ushort  cpUnlock                 // ticks of pregame left
ushort  redTimer                 // ticks red must still hold the hill
ushort  blueTimer
  ... then one block per control point, in order, with no count:
  byte    team                   // -1 neutral
  byte    cappingTeam            // -1 nobody
  ushort  round(capping)         // GML round(), half to EVEN
```

⚠️ **No count prefix on the points.** The client has the same map loaded and
creates the same `ControlPoint` instances from it, so it knows how many to read
— the same self-describing-by-the-map arrangement the moving platforms use. Get
the number or the order wrong and the stream desynchronises rather than showing
a wrong number; `Gg2.Simulation.ControlPoints.Place` fixes the order once at
load (numbered points by number, DKotH red then blue).

⚠️ **`team` and `cappingTeam` are signed**, and the tick a point changes hands
puts a real team in *both* — `pointCapture` does not clear `cappingTeam`.

DKotH uses the same three scalars and then **two** point blocks, red's hill
first (`DKothHUD.events/User Event 12.xml` is `with KothRedControlPoint` then
`with KothBlueControlPoint`). ⚠️ With no index in the block, getting that order
wrong swaps two hills on every client and nothing anywhere errors.

The control-point block is the time limit, the round clock, the **setup
timer**, and then one block per point:

```
ubyte   global.timeLimitMins
uint    timer
ushort  global.setupTimer        // 0 unless the map has setup gates
  ... then one block per control point, in order, with no count
```

Invasion writes the same three scalars and no points.

### Arena

⚠️ **The only HUD block whose shape differs between tiers.** Five extra fields
lead it on a `FULL_UPDATE` and are absent from a `CAPS_UPDATE`:

```
  (FULL_UPDATE only)
  ubyte   redWins, ubyte blueWins
  ubyte   state              // 0 waiting, 1 setup, 2 round, 3 scoreboard
  byte    winners            // the ROUND's winner, not the map's
  ushort  endCount
  (always)
  ubyte   global.timeLimitMins
  uint    timer
  ushort  cpUnlock
  ubyte   floor(roundStart/2)    // 300 ticks in one byte
  ... then the one control point's block
```

⚠️ **`ARENA_ENDROUND` is not a bodyless message.** It carries five bytes and
then five more per MVP:

```
ubyte   win
ubyte   mvpsRed, ubyte mvpsBlue
ubyte   redWins, ubyte blueWins
  mvpsRed + mvpsBlue × {
    ubyte playerId, ubyte kills, ushort healing, ubyte points
  }
```

The counts have to be read to know how long the message is. The other three
Arena messages (`ARENA_RESTART`, `ARENA_STARTROUND`, `ARENA_WAITFORPLAYERS`)
really are bodyless.

⚠️ **Both counts are written before either list**, so a sender cannot stream
the records and count as it goes — both lists have to be chosen first. The
records are the top three of each team by `roundStats[POINTS]`, best first; see
docs/SIMULATION.md, "Settled: the scoreboard".

⚠️ **The point count comes from the map, and from the *area* of it being
played.** `cp_dirtbowl` is three rounds stacked vertically with two points each,
and a reader that framed by the whole file's six would desynchronise. See
docs/SIMULATION.md, "Settled: map areas".

⚠️ **DKotH never sends `UNLOCKCP` and KotH always does** — see
docs/SIMULATION.md, "Settled: Dual King of the Hill". It falls out of instance
order rather than out of the protocol, and a client reaches the same state
either way.

### BALANCE, which has two shapes

```
BALANCE (18)  ubyte 255                       the "teams are uneven" notice
BALANCE (18)  ubyte playerId, ubyte class     somebody was moved
```

⚠️ **Not a fixed-width message.** The client reads one byte and only then
decides whether to read a second (`ClientBeginStep.gml:244-253`), so a reader
that always takes two is one byte ahead from the first notice onwards and never
recovers.

⚠️ **The switch does not say which team.** The client flips the player's own
`team` field, so both sides must already agree on where they were. The class
byte is there because `checkClasslimits` may have changed it on the way across.
Both sides also destroy the player's character, and **no `PLAYER_DEATH` goes
out** — see docs/SIMULATION.md, "Settled: autobalance".

### The player stat block

`FULL_UPDATE` only, one per roster entry, ahead of the `subobjects` byte:

```
ubyte   kills, deaths, caps, assists, destruction, stabs
ushort  healing
ubyte   defenses, invulns, bonus, points
ubyte   queueJump
string2 unparsed rewards
  one per OTHER roster entry, in roster order {
    ubyte dominationKills
  }
```

⚠️ **The order is not `StatIndex` order.** `DOMINATIONS` and `REVENGE` are
skipped and `POINTS` follows `BONUS` — `Player.events/User Event 12.xml` is the
authority, not the enum.

⚠️ **Every counter is a real in the reference and all but healing are
`ubyte`s.** An assist is worth **half** a point, so `write_ubyte` rounds it half
away from zero while the host's own scoreboard draws `floor(stats[POINTS])` — a
client can therefore read one point above the host. That is the reference's, not
a quantisation this port chose. Healing needs its two bytes: a medic passes 255
hit points in the first seconds of a match.

The domination table is **one byte per other player**, so its length comes from
the roster and a reader needs no count. ⚠️ **Self is skipped rather than
written as a zero**, so the row is one byte shorter than the roster — a reader
that took `playerCount` bytes would be one ahead for every player in the update.
Each cell is that player's kills of that other player *since last dying to
them*. See docs/SIMULATION.md, "Settled: the domination kill table".

⚠️ **No recording can see any of this.** The block is `FULL_UPDATE`-only and
`gg2-parity record` joins immediately, so the one full update in a trace is the
join's — sent before anybody has scored. `PlayerStatsLayoutTests` round-trips
the real writer through the real reader instead.

### The Team Deathmatch HUD block

```
ubyte   global.timeLimitMins
uint    timer
ushort  killLimit
```

The kills themselves are the ordinary `redCaps`/`blueCaps` bytes in the shared
tail — Team Deathmatch has no score field of its own, so its HUD draws the
capture counters. See docs/SIMULATION.md, "Settled: Team Deathmatch".

⚠️ **`global.tdmInvulnerabilityTicks` is not in this block.** It leads *every*
`FULL_UPDATE` whatever the mode, ahead of the player count
(`serializeState.gml:18`), and is zero outside Team Deathmatch. It is
`seconds * 30 * 0.2` — a fifth of the ticks it buys, because the client spends
it at 0.2 a tick to draw the shrinking bubble.

### The generator HUD block

```
ubyte   global.timeLimitMins
uint    timer
  one per SURVIVING generator, blue first {
    ushort ceil(hp)           // out of 2100
    ushort ceil(shieldHp)     // out of 300
  }
```

⚠️ **There is one record per *surviving* generator, not one per team.** The HUD
reaches each through a `with` (`GeneratorHUD.events/User Event 12.xml`), which
visits no instances once a generator has been destroyed — so a
`GENERATOR_DESTROY` shortens every later HUD block by four bytes. A reader that
always takes two loses the stream, and surfaces hundreds of bytes later as "no
framing for server message N".

A client stays in step because it destroys its own instance on the message.
`Gg2.Protocol.WorldShape.GeneratorCount` is how anything else does — it is the
one field in a `WorldShape` a map cannot settle for good, and a consumer has to
decrement it on each `GENERATOR_DESTROY`.

⚠️ **Blue's record is written first**, which is the reverse of every other
paired field on the wire: `with GeneratorBlue event_user(12); with GeneratorRed
event_user(12);`.

`ceil(hp)`, not `round`: a generator on 0.1 hp is still standing and the client
must not draw it dead.

`GENERATOR_DESTROY` itself is one byte of body, and ⚠️ **it names the team that
*lost* a generator** — see docs/SIMULATION.md, "Settled: the generator".

### The CTF HUD block

```
ubyte   global.timeLimitMins     // 1..255, never 0
uint    timer                    // ticks left in the round
```

Six of the eight gamemodes open their HUD block with exactly these two, and the
client rebuilds `timeLimit = timeLimitMins*30*60` from the first and then runs
its own copy of the second down between updates. ⚠️ **So a server that never
advances the timer does not merely show a wrong number — it makes every client's
clock jump backwards on each update it receives.**

⚠️ **`CAPS_UPDATE` is sent *in addition* to that tick's ordinary update**, not
instead of it — `GameServerBeginStep.gml:88`, above the tier selection. A
capture sets `GameServer.syncTimer = 1` and the extra message goes out on the
next tick; there is also a `frame mod 3600` heartbeat. So a client can see two
state messages on one tick, and `TraceParser` treats a `CAPS_UPDATE` as an
event rather than a tick boundary for exactly that reason.

## What a headless server can skip, and what does not skip

Not everything in the reference is load-bearing for a private headless server.

- **Lobby registration** — implemented, behind `--lobby`, which defaults off.
  `LobbyRegistration` builds the two datagrams and `LobbyAnnouncer` sends them;
  all four of the reference's re-announce triggers are wired (the 30-second
  cadence, a map change, a join, and a leave that un-fills a full server), plus
  the unregister at shutdown.
  **The packet is BIG-endian** — `set_little_endian(lobbyBuffer, false)` — so
  it is the one thing in this project not written through `NetWriter`. A
  little-endian packet is not rejected loudly; the server just never appears,
  which looks exactly like the flag being off.
  **The live service accepts the packet.** A `--lobby` server on a public
  address appeared in the list read back over the list protocol
  (`LobbyController.events/Create.xml` and `Step.xml`: a TCP connect to the same
  host and port, the list protocol UUID `297d0df4-430c-bf61-640a-640897eaef57`
  then the gg2 lobby UUID, and a big-endian count and length-prefixed blocks
  back), measured 2026-09-07. `LobbyRegistrationTests` still asserts only that
  this port agrees with `sendLobbyRegistration.gml`; the service is a third
  party and no test contacts it.
  **The cadence is REAL TIME, not a frame count**, and this port needs the
  distinction where the reference does not. The lobby drops an entry it has not
  heard from within under a minute — an announce seen in the list was gone from
  it 45 seconds later — while `ServerOptions.IdleWhenEmpty` makes an empty
  server's 900 ticks seven and a half minutes. `LobbyRegistration.IsReRegisterDue`
  is therefore due on either clock, and `LobbyCadenceTests` pins it. A
  frame-only cadence does not merely delist an idle server: it makes the
  delisting permanent, because the player whose arrival would restore the live
  rate cannot see the server to join it.
  **Bots are announced in the packet's own bot field, which the reference
  leaves at zero.** The player count stays `getNumberOfOccupiedSlots`, humans
  only; the browser renders the pair as `players+bots/slots`
  (`LobbyController.events/Step.xml:78-82`) and falls back to `players/slots`
  when the bot field is zero, so a real count needs nothing from a stock client.
  Stock GG2's server has no bots, so its hard zero is the absence of a number
  rather than a claim of none.
  **An idle bot server announces the bots a joiner would find, not the zero it
  is running.** `BotPopulationOptions.MinHumans` keeps an empty server from
  filling with bots, so the live count is zero until somebody arrives and the
  listing reads as dead. `GameServer.AnnouncedBotCount` sends the live count
  whenever there is one and otherwise `BotPopulation.Target(humans + 1, …)` —
  what one more player unlocks, which is zero when bots are off or when
  `MinHumans` is above one. `LobbyBotCountTests`.
- **Reward/auth** — implemented, behind `--rewards`, which defaults off.
  `RewardAuth` builds the packets, `RewardAuthClient` is the socket, and
  `GameServer` answers `REWARD_REQUEST` with a challenge, relays the response and
  broadcasts the `REWARD_UPDATE`. The string is kept on the roster so a later
  joiner's `FULL_UPDATE` carries it too.
  **The default is off, and that is a DEPARTURE from the reference, not a
  transcription of it**: `processClientCommands.gml:320-325` mints a challenge
  and replies to every `REWARD_REQUEST` unconditionally, with no server-side
  opt-out in stock GG2. Turning it on makes this server open outbound TCP to a
  third party carrying its players' user ids, and a headless binary has no
  hosting menu in which somebody agreed to that.
  **`--auth-server host[:port]` overrides where it authenticates**, which is
  the only way the flow can be exercised: `ganggarrison.com:45022` is a third
  party and is not contacted from this repo. `RewardAuthTests` stands up a
  loopback listener instead.
  **The HOST self-auth is deliberately NOT ported.**
  `GameServerCreate.gml:79-83` authenticates the host's own player with
  `ipCheck = false` and `hmac_md5(key, challenge)`. This port has no local player
  — slot 0 is a reserved spectator nobody plays — so there is nothing to
  decorate.
  **Deliberately skipped, not a gap**: making the game server issue outbound
  HTTP requests to a third party carrying player identities is a decision, not a
  transcription task, and it cannot be verified against the live auth service
  from this repo. Every effect of rewards is cosmetic (gear, hats, particles) and
  none of it perturbs the seeded RNG stream — particles run on GM8's
  free-running generator, which is excluded from parity by `docs/SIMULATION.md`.
- **Plugins** (`PLUGIN_PACKET`, `pluginList`) — implemented. See
  `docs/PLUGINS.md`, which is the whole subject: the greeting fields, the
  container framing, the seam a server-half implementation plugs into, and how a
  plugin is ported. The default is still an empty list, so nothing changes for a
  server that does not ask for one.
  **A plugin packet is KICKED, not ignored, for an id outside the advertised
  list.** `processClientCommands.gml:338-362` calls `_PluginPacketPush`, which
  returns false for an id absent from `global.pluginPacketBuffers` — a map filled
  by `loadserverplugins` and keyed by each plugin's *position*. So a server with
  no plugins kicks every sender and a server with two kicks any id above 1.
  Unreachable from a stock client whose list matches the greeting's.
  `PluginPacketTests`, `PluginWireTests`.
- **Map download** — implemented. A map that is not in `Gg2.Maps.InternalMaps`
  is advertised with its MD5 and served on `DOWNLOAD_MAP` as a `uint` length
  followed by the raw file, rate-limited across ticks and shared between
  simultaneous downloaders (`serviceJoiningPlayer.gml:8-24`, `:131-144`).
  **The MD5 is 32 LOWERCASE hex characters, measured rather than assumed.**
  `CustomMapGetMapMD5` calls the compiled `GG2DLL_compute_MD5`; the extension's
  source, `Extensions/GG2DLL/GG2DLL/GG2DLL.cpp:485-516`, formats with
  `sprintf("%02x")`. Uppercase compares unequal on every client, and the symptom
  would be a download that completes and is then rejected.
  **`InternalMaps` carries five ALIASES** — `ctf_2dfort2`, `ctf_2dfortremix`,
  `dkoth_60s`, `ctf_2dfort1` and `ctf_2dfort[0]` (brackets verbatim, and almost
  certainly a typo in the reference that the client nonetheless matches). Missing
  one makes every client re-download a map it already has, under a name it then
  cannot find. `MapDownloadTests.EveryShippedMapIsInternal` gates the list
  against what is on disk, because the two are independent and a mismatch is
  silent.
  **The bandwidth limit is TOTAL, not per client** — a second downloader
  halves the first one's rate rather than doubling the server's outgoing traffic.
  `MapDownloadBytesPerSecond`, default 50000.
- **UPnP port forwarding** (`GameServerCreate.gml:5-19`) — not ported.
- **The loopback socket.** The reference server connects to itself over TCP so
  the host's local player is "just another client". A headless port has no local
  player: keep the roster slot, drop the socket.

None of the above is visible to a stock client beyond a server that has no
rewards.

### Coverage against the full reference

Checked against all 330 scripts and 687 object-event files in the reference
tree, so the list above is a *complete* accounting rather than a plausible one:

- **All 15 client commands** in `processClientCommands.gml` reach
  `GameServer.cs`'s switch, and are fully handled — not merely relayed: this
  includes `CLIENT_SETTINGS` (stored, broadcast and simulated, see
  `QueueJumpTests`), `PLAYER_CHANGENAME` (kicks and throttles exactly as the
  reference does, `NameAndBubbleTests`), and `CHAT_BUBBLE` (applies the April
  Fools substitution, same file). `PLUGIN_PACKET` is dispatched or kicked per
  `docs/PLUGINS.md` — silence is what a *client* does with an unknown id; the
  server kicks.
- **Every map entity type** registered by `builder_init.gml`'s `addEntity` block
  is in `EntityTypes.cs`; every simulation-bearing object in `InGameElements`
  and `GameElements` has a home in `Gg2.Simulation`.
- **All ten classes**, `Quote` included.

**Client-only, so absent on purpose**, each settled by grep from the
definition outward rather than assumed from the name:

- **Gear/hats** (613 lines, five `GEAR_*` constants). Gear never touches the
  wire — it is a client draw derived from reward state.
  `ConstantsTests.NotCarried` lists the constants as "rewards, out of scope".
- **Holidays** (81 lines). `xmasTime.gml` is nothing but `sprite_assign` calls
  and stays client-only. `calculateAprilFools` is the exception:
  `processClientCommands.gml:194-206` reads `global.aFirst` on the
  `CHAT_BUBBLE` command path, rewriting every bubble but 45 on April 1st and
  dropping bubble 61 on every other day — server behaviour deciding what goes
  on the wire, and it is ported (`ChatBubbles`).
- **Kill Log** (302 lines). Called from the `doEvent*` scripts, which run
  server-side too — but it builds a `ds_map` for `Objects/Overlays/KillLog`'s
  **Draw** event. A HUD list, not state.
