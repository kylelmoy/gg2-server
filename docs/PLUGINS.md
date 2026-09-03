# Server-sent plugins

**Status: implemented 2026-09-03.** Off by default — a server with no
`--plugins` advertises an empty list and behaves exactly as it did before.

This document is the whole subject: what the reference does, what half of it a
C# server can do, the seam an implementation plugs into, and how a plugin gets
ported. `docs/PROTOCOL.md` carries the two wire fields; everything else is here.

---

## What a GG2 plugin actually is

A zip of GML, hosted at `http://www.ganggarrison.com/plugins/`, named
`<name>@<md5>.zip`, with a `plugin.gml` in its root. The server names the
plugins it wants in `ServerPluginList`; each client downloads the zip, checks its
md5, unpacks it and `execute_file`s the GML inside a fresh `PluginEnvironment`
instance (`Scripts/Plugins/loadserverplugins.gml`).

⛔ **The critical fact, and the one this repo got wrong for eight phases: a
plugin is ONE script that runs on both sides.** It is full of `if (global.isHost)`
branches. The client's copy draws the UI and sends packets; the host's copy
receives them, decides things and sends replies. There is no separate
"server plugin" artefact.

That is why plugins are portable at all. **This port cannot execute GML and never
will** — but it does not have to. It only has to reproduce the `global.isHost`
branches in C#, and speak the same packets. The client half is untouched: it is
still the site's GML, still downloaded by the client, still run by Game Maker 8.
A stock client cannot tell.

⚠️ **What this costs, stated plainly:** the two halves are now separate
artefacts, and the packets carry no version field. If the site re-releases a
plugin, the client half moves and the C# half does not. See "Pinning" below —
this is what `ServerPluginCatalogue` exists for.

---

## The wire

Two fields in the greeting, and one message in each direction.

### The greeting (`serviceJoiningPlayer.gml:89-91`)

```
ubyte    HELLO
string1  serverName
string1  currentMap
string1  currentMapMD5
ubyte    pluginsRequired          <-- 0 or 1
string2  pluginList               <-- "name@md5,name@md5", or empty
```

⚠️ **`pluginList` is the entire binding between the two halves.** The client
splits it on `,`, then each piece on `@`, downloads the zip by hash and gives
each plugin **the index it appeared at** as its packet id
(`loadserverplugins.gml:194-206`, where the loop counter is both the queue key
and `env.packetID`). Nothing on the wire ever names a plugin again.

So: **no spaces, never re-sorted.** A `", "` separator would make the client
download a plugin called `" chat_v2_pr"` and 404. `PluginNames.Advertise` builds
it and `PluginListTests` gates the format.

⚠️ **`pluginsRequired` is advisory and the reference does not enforce it
either.** Its whole effect is which dialog the client shows
(`ClientBeginStep.gml:62-121`): required offers "Download and join" or
"Disconnect", optional offers "Download" or "Skip". A client that skipped an
optional plugin looks identical to one with no plugin support. If a plugin's
server half sends unprompted packets, require it — that is the only lever there
is, and it is a social one.

### `PLUGIN_PACKET` (55), both directions

```
ubyte    PLUGIN_PACKET
ushort   length          <-- 1 + data.Length: it COVERS the id byte
ubyte    packetId        <-- the plugin's index in pluginList
bytes    data
```

⚠️ **The length covers the id byte.** `PluginPacketSend.gml:39` writes
`buffer_size(dataBuffer) + 1`, and the client reads exactly that many bytes
before parsing anything else (`ClientBeginStep.gml:614-626`). One off here does
not fail here: the client keeps going, reads the next message from the wrong
offset, and reports it several messages later as "The Server sent unexpected
data".

Client to server it is an ordinary command with a two-byte length prefix
(`commandBytesPrefixLength2`), which is the same field.

⚠️ **An id outside the advertised list is a KICK, not an ignore.**
`processClientCommands.gml:338-362` hands the packet to `_PluginPacketPush`,
which returns false when the id is not in `global.pluginPacketBuffers` — a map
filled only by `loadserverplugins`, keyed by position. So a server with no
plugins kicks every sender and a server with two plugins kicks any id above 1,
with `KICK_BAD_PLUGIN_PACKET` (56). Unreachable from a stock client whose list
matches the greeting's.

⚠️ **A known id whose plugin has no C# implementation is accepted and
discarded, which IS a divergence.** The reference would have run the plugin's
GML. Kicking instead would punish a client for loading exactly what this server
asked it to load. The gap is logged at startup, by name.

---

## Configuring it

| Setting | ini key | flag |
|---|---|---|
| plugin list | `[Server] ServerPluginList` | `--plugins <list>` |
| required | `[Server] ServerPluginsRequired` | `--plugins-required` |

```
gg2-server --plugins chat_v2_pr --plugins-required
```

Names are `[a-z0-9_]+` (`checkpluginname.gml` — **no uppercase**, and the
reference's own 404 message says so). The configured list may not exceed 254
characters (`game_init.gml:99`) and the hashed one may not exceed 65535
(`GameServerCreate.gml:137`); both are transcribed. Duplicates are refused,
because the ids are positional.

### Where a hash comes from

`PluginHashes.Resolve`, in order:

1. **`name@md5` written in the list.** A port extension, not the reference's
   syntax — `checkpluginname` rejects an `@`. It is how an operator pins a
   version and how a test runs offline.
2. **`ServerPluginsDebug/<name>.zip`**, hashed locally. The reference's own first
   choice (`getpluginhashes.gml:43-47`), and how a plugin is developed. Logs the
   reference's warning: every client needs that same file.
3. ⛔ **The hash the C# implementation was written against**, from
   `ServerPluginCatalogue`. **Not what the reference does**, and the difference
   is the point — see Pinning.
4. **`GET http://www.ganggarrison.com/plugins/<name>.md5`**, exactly as
   `getpluginhashes.gml` does. Synchronous, at startup, before the listener
   exists.

Anything that disagrees with an implementation's pin is logged loudly. That is
the one condition under which the two halves are running different protocols.

⚠️ **Failing to resolve fails `Start()`**, which is `GameServerCreate.gml`'s
`game_end()` on the same failure. A server that came up advertising a list it
could not serve would strand every client that believed it.

---

## Pinning: the falsification condition

`ServerPluginCatalogue` maps each implemented name to `(md5, factory)`. The md5
is **the zip the C# was read from**, and it is advertised by default.

⛔ **If you port a newer version of a plugin's GML, move the pin in the same
commit and say which zip you read.** A pin that no longer names the file the code
was written from is worse than none, because it claims a guarantee it is not
giving.

⚠️ **What would show this has gone stale:** download
`http://www.ganggarrison.com/plugins/<name>.md5` and compare it to the pin. A
difference does not mean the implementation is broken — most releases change the
client half only — but it does mean nobody has checked. There is no test that
can do this without reaching the network, which is why it is written here.

`PluginListTests.AnImplementedPluginUsesItsPinnedHashAndNeverTouchesTheNetwork`
fails if the pin is edited, so at least it cannot move by accident.

---

## The seam

`src/Gg2.Server/Plugins/`.

| Type | What it is |
|---|---|
| `IServerPlugin` / `ServerPlugin` | the server half. `Attach`, `PlayerAdded`, `PlayerRemoving`, `Tick`, `Receive` |
| `IPluginHost` | what a plugin can see and do. Implemented by `GameServer` |
| `PluginChannel` | the host plus this plugin's packet id, bound once |
| `PluginPlayerTable<T>` | per-player state that compacts with the roster |
| `ServerPlugins` | the loaded set; `Advertised`, and `Dispatch` by id |
| `PluginNames` / `PluginSpec` | parsing and validating `ServerPluginList` |
| `PluginHashes` / `IPluginHashSource` | resolving a hash; the HTTP half |
| `ServerPluginCatalogue` | name → (pinned md5, factory) |

### The three rules

⛔ **1. Nothing here reaches the simulation.** A plugin may say things on the
wire and may rename a player. It may not move a character, spawn a projectile or
draw from `WorldState.Random` — that stream is observable simulation, every
client re-derives a weapon's spread from its position, so a plugin drawing from
it would make the game depend on what the plugins were saying. Same rule and same
reason as the one CLAUDE.md states for bots.

⛔ **2. Per-player state goes in a `PluginPlayerTable<T>`.** A slot index is a
roster index and removal **compacts**. In GML this problem does not arise —
per-player variables live on the `Player` instance and die with it. In C# the
obvious shape (a dictionary or array keyed by slot) is silently wrong the first
time somebody disconnects, and the symptom is a mute, a name or a chat flag
landing on an unrelated player some seconds later. `PlayerRemoving` is called
**before** the roster removal, so the host still answers for the departing
player.

⚠️ **3. `IPluginHost` is deliberately small.** It is what
`global.players`, `socket_remote_ip`, `current_time` and
`Scripts/Plugins/API` gave a GML plugin, and no more. Adding to it is adding a
capability the reference's plugins did not have; that may be right, but it is a
decision, not a convenience.

### Where the hooks fire

| Hook | Where in `GameServer` | Reference |
|---|---|---|
| `Attach` | `Start`, after the roster and slot 0 | `loadserverplugins.gml:199-217` |
| `PlayerAdded` | slot 0 at `Start`; `CompleteJoin`; `AddBot` | `Player`'s Create event |
| `PlayerRemoving` | `RemoveClient`, `RemoveBot`, **before** the removal | — |
| `Tick` | after `ServiceClients`, before the input pass | `Player`'s Begin Step |
| `Receive` | the `PLUGIN_PACKET` case in `Handle` | `processClientCommands.gml:338` |

`Tick` is after the tick's client commands so a `PLAYER_CHANGENAME` that arrived
this tick is seen this tick. GM8 gave the reference no such choice and nothing
observable turns on it.

`SendPluginPacketToAll` appends to the broadcast buffer (`global.sendBuffer`);
`SendPluginPacketTo` queues on one client's own connection. Both leave in the
tick's single flush, in the order they were written — which is the same order the
reference produces, even though it calls `socket_send` immediately.

---

## Porting a plugin

1. **Get the zip** and confirm its md5 equals the hash in its filename.
2. **Read `plugin.gml` for `global.isHost`.** Those branches, plus anything the
   host-side `event_user` handlers they call do, are the whole job. Everything
   else — drawing, fonts, key bindings, menus, `object_add`, `object_event_add` —
   stays on the client and is not your problem.
3. **Write down the packet format** in the implementation's doc comment, both
   directions, citing the GML line for each field. There is no version byte;
   this comment is the only record.
4. **Transcribe, do not improve.** A plugin's quirks are behaviour players are
   used to, and a client renders whatever it is handed, so a "fix" has no symptom
   the server can see. Where a quirk is worth knowing, comment it — see
   `ChatV2Plugin`'s spam window and its name-counter.
5. **Add the catalogue entry** with the md5 and the date, and say which zip.
6. **Test at two levels.** The plugin against a fake `IPluginHost`
   (`ChatV2PluginTests`) for what it decides; the server over a real socket
   (`PluginWireTests`) for the framing. ⚠️ **Then mutate** — break the routing,
   the framing and the compaction on purpose and check each goes red. Three of
   this layer's gates were confirmed that way and it is the cheapest check there
   is.

---

## `chat_v2_pr` — the one that is ported

`ChatV2Plugin`, from `plugin.gml` dated 2017-04-10 inside
`chat_v2_pr@46d0937c9206dcf27e036100d1ea8685.zip`.

**Client to server:** a kind byte, then for a chat message a `ubyte` length and
that many bytes.

| Kind | Meaning |
|---|---|
| 0 | hello — "I have the plugin". No body |
| 1 | global chat |
| 2 | team chat |

**Server to client:** a `ubyte` length and that many bytes — one finished line,
already coloured and already prefixed with the speaker's name. There is no kind
byte in this direction.

⚠️ **The `_team` field is off by one, twice, and they are not the same
quantity.** The client sends `_team + 1` where `_team` is a *boolean* "is this
team chat" (`event3`), giving 1 for global and 2 for team. The host then derives
`0` for global and `1 + player.team` for team chat, and routes on
`team == audience - 1`. The two numbers only meet at the kind byte.

**Line format** (`event1`):

- Global: `<colour><name>: #W<message>`
- Team: `<colour><name>: <message>`
- Colour is `R` red, `B` blue, `W` spectator; `O` is orange and is used for the
  plugin's own announcements.
- ⚠️ **`#` in what a player typed becomes a space**, because the client half uses
  `#` to find where the colour changes when it wraps a long line (`event4`). A
  `#` left in a message recolours the rest of it.
- Truncated to 255 bytes, because the length prefix is one byte.

**Routing** (`event2`): player by player, never a broadcast — global chat reaches
only players who sent hello, and team chat only that team's. A client cannot be
trusted to hide what it has been sent.

**Anti-spam** (`event7`): a sixth message inside two seconds mutes the sender,
and the muted address is remembered so a reconnect cannot rejoin chat. ⚠️ The
window restarts from the **last** message, not the first, so a player typing
steadily every 1.9 seconds is muted on their sixth line. Transcribed, not
smoothed.

**Name de-duplication** (`Player`'s Begin Step, host branch): a player whose
first 18 bytes match somebody else's, case-insensitively, is renamed to
`<first 18 bytes><how many matched>`. ⚠️ **This is the one thing the plugin does
that a client without it still sees**, because it goes out as an ordinary
`PLAYER_CHANGENAME`. The counter is the match count rather than the next free
number, so the third "alice" is "alice3" and, if "alice2" leaves, the next is
"alice2" again.

⚠️ **A rename by a player who never sent hello leaves `nameBkp` stale for
ever**, so that branch is re-evaluated every tick from then on. Harmless — the
de-duplication settles after one pass — and transcribed rather than fixed,
because the fix would change what a stock client sees.

**Not ported, because it is client-only:** the window, the font, the sprite, the
key bindings, the colour rendering, the wrapping, the options menu, and the
`cheatycheat` object that stops you moving while typing. Also the host's own
chat window: a headless server has no window, so `event4` becomes a line in the
server log.
