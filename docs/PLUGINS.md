# Server-sent plugins

Off by default — a server with no `--plugins` advertises an empty list and
behaves exactly as a server with no plugin support.

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

**The critical fact: a plugin is ONE script that runs on both sides.** It is
full of `if (global.isHost)` branches. The client's copy draws the UI and sends
packets; the host's copy receives them, decides things and sends replies. There
is no separate "server plugin" artefact.

That is why plugins are portable at all. **This port cannot execute GML and
never will** — but it does not have to. It only has to reproduce the
`global.isHost` branches in C#, and speak the same packets. The client half is
untouched: it is still the site's GML, still downloaded by the client, still
run by Game Maker 8. A stock client cannot tell.

**What this costs, stated plainly:** the two halves are now separate
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

**`pluginList` is the entire binding between the two halves.** The client
splits it on `,`, then each piece on `@`, downloads the zip by hash and gives
each plugin **the index it appeared at** as its packet id
(`loadserverplugins.gml:194-206`, where the loop counter is both the queue key
and `env.packetID`). Nothing on the wire ever names a plugin again.

So: **no spaces, never re-sorted.** A `", "` separator would make the client
download a plugin called `" chat_v2_pr"` and 404. `PluginNames.Advertise` builds
it and `PluginListTests` gates the format.

**`pluginsRequired` is advisory and the reference does not enforce it
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

**The length covers the id byte.** `PluginPacketSend.gml:39` writes
`buffer_size(dataBuffer) + 1`, and the client reads exactly that many bytes
before parsing anything else (`ClientBeginStep.gml:614-626`). One off here does
not fail here: the client keeps going, reads the next message from the wrong
offset, and reports it several messages later as "The Server sent unexpected
data".

Client to server it is an ordinary command with a two-byte length prefix
(`commandBytesPrefixLength2`), which is the same field.

**An id outside the advertised list is a KICK, not an ignore.**
`processClientCommands.gml:338-362` hands the packet to `_PluginPacketPush`,
which returns false when the id is not in `global.pluginPacketBuffers` — a map
filled only by `loadserverplugins`, keyed by position. So a server with no
plugins kicks every sender and a server with two plugins kicks any id above 1,
with `KICK_BAD_PLUGIN_PACKET` (56). Unreachable from a stock client whose list
matches the greeting's.

**A known id whose plugin has no C# implementation is accepted and
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
3. **The hash the C# implementation was written against**, from
   `ServerPluginCatalogue`. **Not what the reference does**, and the difference
   is the point — see Pinning.
4. **`GET http://www.ganggarrison.com/plugins/<name>.md5`**, exactly as
   `getpluginhashes.gml` does. Synchronous, at startup, before the listener
   exists.

Anything that disagrees with an implementation's pin is logged loudly. That is
the one condition under which the two halves are running different protocols.

**Failing to resolve fails `Start()`**, which is `GameServerCreate.gml`'s
`game_end()` on the same failure. A server that came up advertising a list it
could not serve would strand every client that believed it.

---

## Pinning: the falsification condition

`ServerPluginCatalogue` maps each implemented name to `(md5, factory)`. The md5
is **the zip the C# was read from**, and it is advertised by default.

**If you port a newer version of a plugin's GML, move the pin in the same
commit and say which zip you read.** A pin that no longer names the file the code
was written from is worse than none, because it claims a guarantee it is not
giving.

**What would show this has gone stale:** download
`http://www.ganggarrison.com/plugins/<name>.md5` and compare it to the pin. A
difference does not mean the implementation is broken — most releases change the
client half only — but it does mean nobody has checked. There is no test that
can do this without reaching the network, which is why it is written here.

`PluginListTests.AnImplementedPluginUsesItsPinnedHashAndNeverTouchesTheNetwork`
fails if the pin is edited, so at least it cannot move by accident.

---

## The seam

`src/GG2.Server/Plugins/`.

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

**1. Nothing here reaches the simulation.** A plugin may say things on the
wire and may rename a player. It may not move a character, spawn a projectile or
draw from `WorldState.Random` — that stream is observable simulation, every
client re-derives a weapon's spread from its position, so a plugin drawing from
it would make the game depend on what the plugins were saying. Same rule and same
reason as the one CLAUDE.md states for bots.

**2. Per-player state goes in a `PluginPlayerTable<T>`.** A slot index is a
roster index and removal **compacts**. In GML this problem does not arise —
per-player variables live on the `Player` instance and die with it. In C# the
obvious shape (a dictionary or array keyed by slot) is silently wrong the first
time somebody disconnects, and the symptom is a mute, a name or a chat flag
landing on an unrelated player some seconds later. `PlayerRemoving` is called
**before** the roster removal, so the host still answers for the departing
player.

**3. `IPluginHost` is deliberately small.** It is what
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
   (`ChatPluginTests`) for what it decides; the server over a real socket
   (`PluginWireTests`) for the framing. **Then mutate** — break the routing,
   the framing and the compaction on purpose and check each goes red. It is the
   cheapest way to confirm a gate actually gates something.

---

## Chat

**Chat is the server's own, not a plugin: `docs/CHAT.md`.** It travels on the
`chat` plugin's packet id because that is the only way a stock client gets a chat
window, and the server advertises `chat` itself while `[Chat] Enabled` is on (the
default). The catalogue knows `chat` only as the zip to pin.

`chat_v2_pr` is the one chat that is still a plugin here, available with the
server's chat off (`[Chat] Enabled=0`); listing it with chat on is refused at
startup.

| | `chat` | `chat_v2_pr` |
|---|---|---|
| server half | the server's chat (`ServerChat`) | `ChatV2Plugin.cs` + `ChatV2Commands.cs` |
| zip | `chat@bad8081d64f0b808a2bb3a6d88978fb5.zip` | `chat_v2_pr@46d0937c9206dcf27e036100d1ea8685.zip` |
| who colours a line | the client, from the sender's roster index | the server, before it sends |
| votes | a real dialog, answered with a number key | chat lines, answered by typing |
| behaviour | the native chat's (`docs/CHAT.md`) | the reference's, transcribed |

---

## `chat` on the wire: Lorgan3's v6

The client half a stock client downloads, from `plugin.gml`, `chat_api.gml`,
`vote_api.gml` and `commands.gml` inside
`chat@bad8081d64f0b808a2bb3a6d88978fb5.zip`. The server's chat speaks this to a
player who greeted with v6; `docs/CHAT.md` has what it sends for each thing it
does.

### The wire

**Client to server**, a kind byte then its body (`plugin.gml:436-522`):

| Kind | Body |
|---|---|
| 0 | hello — "I have the plugin". None |
| 1 | global chat: a `ubyte` length and that many bytes |
| 2 | team chat: the same |
| 3 | a vote answer: a `ubyte`, the 1-based option the dialog reported |

**Server to client**, a `ubyte` that is either a roster index or a code
(`plugin.gml:525-592`):

| First byte | Rest |
|---|---|
| < 200 | the speaker's roster index, then a `ubyte` audience and a `ubyte`-prefixed string |
| 200 | a poll: question, option count, each option, then a `ushort` of steps remaining |
| 201 | the result: a `ubyte` 1-based winner, or **0 to close the dialog with no result** |
| 203 | somebody voted: a `ubyte` 0-based option, which every open dialog's bar chart adds to |
| 204 | 200's fields plus one `ubyte` tally per option, for a client that joined mid-vote |
| 254 | a `ubyte`-prefixed pre-coloured line about players |
| 255 | the same, for anything a player must see |

**The text of a player's line is raw and the client colours it.** The server
sends the index and the bytes; `doColorCode` (`chat_api.gml:96-123`) adds the
name, picks red, blue or white from the speaker's team, and inserts the `chr(13)`
that separates the two colours. A server that formatted the line itself would
draw the name twice. That is the opposite of `chat_v2_pr`, and it is the single
difference most likely to be got wrong by a reader who ported one and then the
other.

⚠️ **254 is suppressible and 255 is not.** A client with "Display player info"
off drops every 254 (`chat_api.gml:50-52`), so joins, leaves and renames go out
as 254 and command replies and vote results as 255. Send something a player
needs as 254 and it is invisible on an ordinary setting.

**A message longer than 255 bytes becomes several packets**, not a truncation:
`chat_api.gml:7-28` loops until the remainder is empty. An empty message sends
nothing at all, which is the same loop.

**The audience is off by one, twice, and they are not the same quantity.** The
client sends a packet *kind* of 1 for global and 2 for team (`chat_api.gml:33`
writes a boolean plus one); the host re-derives 0-for-global and `1 + team`
(`plugin.gml:495`) and routes on `team == audience - 1` (`chat_api.gml:22`). The
two numbers meet only at the kind byte.

---

## `chat_v2_pr` — the other one

`ChatV2Plugin`, from `plugin.gml` inside
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

**The `_team` field is off by one, twice, and they are not the same
quantity.** The client sends `_team + 1` where `_team` is a *boolean* "is this
team chat" (`event3`), giving 1 for global and 2 for team. The host then derives
`0` for global and `1 + player.team` for team chat, and routes on
`team == audience - 1`. The two numbers only meet at the kind byte.

**Line format** (`event1`):

- Global: `<colour><name>: #W<message>`
- Team: `<colour><name>: <message>`
- Colour is `R` red, `B` blue, `W` spectator; `O` is orange and is used for the
  plugin's own announcements.
- **`#` in what a player typed becomes a space**, because the client half uses
  `#` to find where the colour changes when it wraps a long line (`event4`). A
  `#` left in a message recolours the rest of it.
- Truncated to 255 bytes, because the length prefix is one byte.

**Routing** (`event2`): player by player, never a broadcast — global chat reaches
only players who sent hello, and team chat only that team's. A client cannot be
trusted to hide what it has been sent.

**Anti-spam** (`event7`): a sixth message inside two seconds mutes the sender,
and the muted address is remembered so a reconnect cannot rejoin chat. The
window restarts from the **last** message, not the first, so a player typing
steadily every 1.9 seconds is muted on their sixth line. Transcribed, not
smoothed.

**Name de-duplication** (`Player`'s Begin Step, host branch): a player whose
first 18 bytes match somebody else's, case-insensitively, is renamed to
`<first 18 bytes><how many matched>`. **This is the one thing the plugin does
that a client without it still sees**, because it goes out as an ordinary
`PLAYER_CHANGENAME`. The counter is the match count rather than the next free
number, so the third "alice" is "alice3" and, if "alice2" leaves, the next is
"alice2" again.

**A rename by a player who never sent hello leaves `nameBkp` stale for
ever**, so that branch is re-evaluated every tick from then on. Harmless — the
de-duplication settles after one pass — and transcribed rather than fixed,
because the fix would change what a stock client sees.

**Not ported, because it is client-only:** the window, the font, the sprite, the
key bindings, the colour rendering, the wrapping, the options menu, and the
`cheatycheat` object that stops you moving while typing. Also the host's own
chat window: a headless server has no window, so `event4` becomes a line in the
server log. The admin panel's Chat card is the host's input instead:
`SayAsServer` formats its line with `event1`'s `global.myself` branch (an orange
message), outside the command parser and the anti-spam count.

---

## `chat_v2_pr`'s commands: `/votekick` and friends

Ported from Lorgan3's five **local** server plugins — `party.gml`,
`votekick.gml`, `votemap.gml`, `votemute.gml`, `votescramble.gml` (forum topic
33773). Those are dropped in a server's own `Plugins/` directory and loaded by
`loadplugins.gml`, so only the machine running the server ever has them; nothing
is sent to a client. They were written against v6's API (`global.sendVote`,
`global.commandMap`, `Player.gid`); on `chat_v2_pr` they are a reconstruction
with v6's dialog replaced by text. **The server's chat does not use them**: it runs its own commands and votes
(`docs/CHAT.md`).

**This works because the client half does not know commands exist.** It sends
`/votekick bob` as an ordinary chat message; the server sees the leading slash,
acts, and never echoes it — not even when the command is refused. Nothing on the
wire changed, and a client with no plugin at all is unaffected.

### The commands

| Command | What it does |
|---|---|
| `/help [command]` | lists them, or explains one |
| `/party` | Party hard. |
| `/votekick <name>` | vote to disconnect somebody |
| `/votemute <name>` | vote to take somebody out of chat |
| `/votescramble` | vote to reshuffle the teams |
| `/votenextround` | vote to end the round now |
| `/votemap [name]` | vote for the next map |
| `/yes`, `/no`, `/vote <n>` | answer a vote; a later answer replaces an earlier one |
| `/kick`, `/nextround` | host only — see below |
| `/bots [off|on]` | **this port's own** — see below |

### The rules, and where they come from

| Rule | Value | Source |
|---|---|---|
| vote length | 30 seconds | `sendVote(30 * 30, ...)`, all five |
| one vote at a time | — | `global.chatWindow.alarm[0] > 0`, all five |
| kick | **90%** | `votekick.gml:89` |
| mute | **70%** | `votemute.gml:92` |
| scramble, next round | **> 50%** | `votescramble.gml:68`, `votemap.gml:280` |
| one named map | **>= 50%** | `votemap.gml:204` |
| map choice | most votes, ties at random | `votemap.gml:141-183` |
| map options offered | 8, plus "keep default" | `votemap.gml:83`, `:131` |
| map vote per round | once | `selectedmap`, `votemap.gml:42` |
| the target may not vote | — | `target.mayVote = false`, `votekick.gml:40` |

**`global.chatWindow.alarm[0]` is the VOTE TIMER in v6, which these extend, not
a chat window's fade.** In `chat_v2_pr` that same alarm is the four-second fade,
so the expression reads as "wait four seconds after anybody speaks" — which is
not what it means: the refusal message divides it by 30 and calls the result
seconds, and `sendVote` is given `30 * 30`. Reading it the other way would have
shipped a cooldown nobody asked for.

### The bot suspension and the join line

Two things in this section have **no GML behind them at all**. Stock GG2 ships no
bots and no server-configured chat line, so neither is a transcription and
neither can be checked against the reference.

**`/bots` — reporting and suspending the bot population.**

| | |
|---|---|
| `/bots` | how many bots are playing, or how long a suspension has left |
| `/bots off` | holds the population at zero for `[Bots] SuspendMinutes` |
| `/bots on` | ends a suspension early |

- **Off by default.** `[Bots] SuspendMinutes` is `0`, and the command then
  answers "this server does not let bots be turned off from chat" rather than
  behaving as though it had worked. One key rather than a boolean plus a
  duration, because those two can be configured into a state that reads as
  working and does nothing.
- **Not a vote**, unlike every other command here that changes the server. The
  case it exists for is a handful of people on a server whose bots are in the
  way, which is the roster size at which a 50% quorum of players in chat is two
  people and a dialog is ceremony. What bounds it is the clock: the window
  expires by itself, so the worst outcome is the bots being away for
  `SuspendMinutes` and then coming back. Nothing persists it across a restart.
- **A second `/bots off` inside a running window does not re-arm it.** That is
  the whole bound; re-arming would make one player typing three words every few
  minutes an indefinite switch.
- **Four different replies, deliberately.** "No such command here", "this server
  is not running bots", "already off for another N minutes" and the announcement
  are four states a player reaches by typing three words, and collapsing them
  produces the reading where somebody suspends bots on a server that has none
  and watches bots appear when the next person joins.
- **An operator can always end it.** `SetBotFill` clears a suspension, so saving
  a bot count in the admin panel does; the control port's `bots-suspend` with
  `minutes` 0 does it directly. Without that the panel's one bot control would
  accept a number, report it back and change nothing for up to a day with no way
  to see why — so the panel states a running suspension where it already states
  the `MinHumans` floor.
- **The suspension is a target of zero, not `Bots.Enabled = false`.** The latter
  means *do nothing* to `BotPopulationService` — it deliberately leaves a
  hand-added bot alone — so reading it as a suspension would tell a player the
  command worked while the bots carried on playing.
- **It is a clock in ticks**, like a vote's and like the round timer, so it stops
  with `freeze` and moves with `--speed`.

**`[Server] ChatJoinMessage` — a line to each player as they join chat.** Sent on
the plugin's hello, not on the roster entry: before the hello there is nothing on
the other end to draw it into. It goes out as a 255 rather than a 254, because a
client with "Display player info" off drops every 254 and this is the line that
tells a player what the server's commands are; on the server's chat it is also
the native WELCOME's motd. ⚠️ `#` is **not** the same character everywhere — to
a v6 client it is GM8's line break and is left alone, to a native client it is a
literal `#`, and on `chat_v2_pr` it is the colour-continuation
marker its client half looks for and is replaced with a space.

⛔ **Both settings do nothing at all without chat** — the server's own, on by
default, or `chat_v2_pr`. With `[Chat] Enabled=0` and no `chat_v2_pr` either is
configured on a server that can never show it to anybody. `ConfigWarnings.For`
names that combination and three surfaces say it: the server's log at startup, the fleet
editor's notes, and `config render` before a push. It is a warning, not a
refusal; `docs/CONFIG.md`, "settings that are valid and cannot do anything".

⚠️ Both reach a plugin through `IPluginHost`, which is otherwise a stand-in for
a GML surface. They are there rather than inside a plugin because both are
**server** state — one server, one bot population, one configured line — and a
plugin holding either would give two loaded chat plugins two different answers.

### Where `chat_v2_pr`'s commands deliberately differ

**The quorum counts players who could vote, not the roster.** The reference is
`yes + no < floor(ds_list_size(global.players) / 2)` — the whole roster, host and
all. This server **fills with bots by default**, and a bot cannot type, so the
reference's sum makes the quorum unreachable on every bot server: the feature
would look implemented and never once pass a vote. Here the divisor is the
clients who are in chat. The reference had no bots, so on its own servers the two
sums are the same number. The same substitution is what ends a vote early once
everybody has answered.

**A vote kick is one disconnection, not a ban.** The reference sets
`target.kicked` and keeps a `chatKicklist` its chat plugin consults on reconnect.
A server-wide ban list is a bigger decision than a chat plugin should make by
itself, and a half-built one that only some paths consult is worse than none.
What does persist is the chat mute — it shares the ban list the hello check
already consults — so a vote-kicked player can come back, and comes back unable
to talk.

**A successful map vote overrides the next round only.** The reference assigns
`global.map_rotation` a one-entry list and never restores it, so one vote
replaces the server's rotation for the rest of its life.

**`--votable-maps` falls back twice**: to the map rotation, as the reference
does, and then to every map in `--maps`. Without that third step a single-map
server — the default — can only answer "No maps were found!", which is exactly
where a map vote is most wanted. `/maplist` reads the rotation instead, because
that is where the next round actually comes from, and answers "This server is not
rotating maps." when there is none — the reference's `global.map_rotation` always
had at least one entry and it indexes straight into it.

**An out-of-range vote answer is dropped rather than indexed.** The reference's
guard is `vote <= 9`, which lets a zero through into `optionVotes[-1]`. A client
is not a trusted source and an answer no dialog could have produced must not be
an exception on the tick thread. See `docs/HARDENING.md`.

**`/kick` and `/nextround` answer "You can only use this command as host."** and
nothing else. That *is* the reference's behaviour for a client, and a headless
server has no host player, so it is the only reachable branch. The operator's
equivalent is the control port.

**A vote still running when the map changes is dropped, not resolved.** A
scramble or a round-end vote landing in the round *after* the one it was called
in would act on a world nobody voted about.

**Commands count towards the anti-spam limit**, like any other message. Six of
anything in two seconds is a mute.

### What the plugin needed from the server, and why it is safe

The vote commands are the reason `IPluginHost` grew: `Kick`, `SetTeam`,
`EndRound`, `SetNextMap`, `VotableMaps`, `MapRotation`, `CurrentMapIndex`,
`PointsOf`, `StableIdOf` / `SlotOfStableId`, and a `Random`.

`ChatJoinMessage`, `Bots`, `SuspendBots` and `ResumeBots` are the exception and
are named as one: nothing in the reference could reach them. `ChatJoinMessage` is
a plain read of configuration. The other three move the **population manager's
target**, which is the server's own scheduling decision about how many
sockets-that-are-not-sockets to hold open; removing a bot takes the same path a
disconnection does, so none of it can produce a world a client could not.

**Every one routes through a `GameServer` method a client command or the control
port already uses** — `ApplyChangeTeam`, `Rounds.Decide`, the same kick path — or
is a plain read (`PointsOf` is `Scoring.Get`, the reference's
`Player.stats[POINTS]`). A plugin still may not touch `WorldState`, and in
particular may not draw from `WorldState.Random`: that stream is observable
simulation, so a map-list shuffle taken from it would move every client's
re-derived weapon spread. This is CLAUDE.md's rule for bots, one layer out — **a
plugin must not be able to produce a state a client could not.**

**`StableIdOf` is the one that is not a convenience.** A vote holding a slot
index kicks whoever inherited the number. The reference's plugin API has exactly
this as `Player.gid`, which is some evidence it is the right shape rather than a
local invention. **It is never on the wire** — clients name players by position,
and that is not negotiable.

### Testing this

`ChatV2CommandTests` here, and `ServerChatTests` and `ServerChatV6Tests` for the
server's chat, against `FakePluginHost`. **A
threshold needs a row on each side of it, and an effect under test has to be one
that cannot happen by luck.** A test that votes unanimously on a threshold passes
at every threshold; a scramble test needs more than two players or "moved
nobody" is also true of a correct scramble.

⚠️ **`FakePluginHost` hands out stable ids starting at 1000, on purpose.** Ids
that happened to equal the slot they were issued at let a plugin keeping slot
indices pass every compaction test by coincidence — which they did, until the
offset was added and the mutation finally went red.

---

## Verifying against a real client

**The test suite cannot do this and it is the half that matters.** Everything
in `PluginWireTests` is this port talking to itself: it proves the framing is
self-consistent, not that a GM8 client accepts it, that the zip downloads, that
7-Zip extracts it, or that `execute_file` runs the GML without a dialog. Run this
after any change to the wire fields or to a plugin implementation.

`docs/TRAPS-LIVEGAME.md` and the `gg2-playtest` skill are the general rules —
**exactly one session holds the live game**, and the bridge serves one TCP client.

**The server's chat has its own live record in `docs/CHAT.md`.** The two `chat`
runs below were against the v6 server half it replaced. What they establish about
the client — that a stock client parses each v6 shape — still holds, because the
server sends the same shapes.

**`chat` was driven this way on 2026-09-12**, against three stock GM8 clients on
`--plugins chat --bots 2`. Every packet shape in both directions was exercised
and read back out of the client's own state, so what is established is that a GM8
client *parses* these bytes, not only that this port composes them:

| Verified | How it was read back |
|---|---|
| the plugin loads at all | `global.serverPluginsInUse` 1, `global.chatPacketID` 0 |
| hello (0), announcement (254) | `global.chatLog` ends `Oalice has joined chat!` |
| global chat (1), a player line | the client coloured it itself: `W<name>: <chr(13)>W<text>` |
| team chat (2) | the red client got `R<name>: <text>`; the blue one and the spectator got nothing |
| a command reply (255) | `/help "votekick"` — quotes stripped, and the client wrapped the answer at 53 characters |
| the poll (200) | `global.voter.question`, both options, `alarm[0]` |
| the running tally (203) | `optionVotes[0]` and `total` both 1 after one answer |
| the result (201), `win = N` | `win` 1, `done` 1 |
| the result (201), `win = 0` | `instance_exists(global.voter)` 0 — the picker's dialog closed |
| the resync (204) | a third client launched mid-vote came up with the question, a partial clock and `optionVotes[0]` 1 |
| the player picker | listed `Server (0)`, `[BOT] 1 (0)`, `Player (0)`; picking the bot re-dispatched and was refused |
| the 60-second vote cooldown | "Please wait 10 seconds before starting a vote." |
| roster compaction under a live client | the population manager dropped a bot, moving one client from slot 2 to slot 1; its `hasChat` survived |

**`/bots` and `ChatJoinMessage` were driven this way on 2026-09-13**, against a
stock client on **both** plugins in turn — `--plugins chat_v2_pr` then
`--plugins chat`, each with `--bots 4 --bot-suspend-minutes 2` and a join line.
Every assertion is the server's own roster or the client's `chatLog`, never a
screenshot:

| Verified | How it was read back |
|---|---|
| the join line reaches a client | last entry of `global.chatLog` after the join announcement, on both plugins |
| `#` is a line break on `chat` and a space on `chat_v2_pr` | the `chat` client drew two lines; the `chat_v2_pr` log held one with spaces |
| `/bots` reports the real population | "3 bots are playing" against a roster of exactly three |
| `/bots off` removes them | roster 3 → 0, and the client's own team counters read RED×0 BLU×0 |
| the announcement reaches everyone | `chatLog` carried "Player turned the bots off for 2 minutes" |
| a second `/bots off` does not re-arm | the clock went on counting down (120 → 97) and answered "already off for another 2 minutes" |
| `/bots on` | roster 0 → 3 in the same tick |
| the window expiring by itself | a one-minute window, watched down 58 → 30 → 2 → gone, bots back, with a client connected throughout |
| `/bots` is in `/help` | the list ends "votemap and bots" on `chat`, "/nextround /bots" on `chat_v2_pr` |

⛔ **It found a bug no test could have.** `plugin.gml`'s draw loop ends
`msg = string_copy(msg, 2, 255)`, so character 1 of every line is eaten as a
colour code whether or not one is there — a join line sent without one lost its
first letter, and a stock client drew `Welcome!` as `elcome!`. `chat_v2_pr` was
unaffected because that path already prefixed one. The assertion that let it
ship was comparing the sent text to the configured text; it now compares against
`ColourOrange + ` the configured text.

⚠️ **What the 2026-09-12 run did NOT cover**: a vote decided by more than one
human, a vote whose target disconnects mid-vote, `/votemap` against a real
rotation, and the 255-byte chunking loop (nothing in the run composed a line that
long). ⚠️ **What the 2026-09-13 one did not**: a suspension surviving a map
rotation, two humans racing `/bots off` against `/bots on`, and a join line long
enough to chunk.

**Before you blame your change for a client dying: read the section below.**
A rotation kills every connected client on a server with bots, it has nothing to
do with plugins, and the server log says only `ConnectionReset`.

1. **Two ini keys, or every launch stops on a modal.** In
   `<reference>/Source/build/gg2.ini`, under `[Settings]`
   (`<reference>` and `<bridge>` are defined in `docs/TRAPS-LIVEGAME.md`):
   `ServerPluginsPrompt=0` (else the client raises the "download these plugins?"
   dialog and the bridge stops answering) and `RestartPrompt=0` (else
   `pluginscleanup` raises one on disconnect). **Back the file up and restore
   it** — it is the live game's own config, not this repo's.
2. `gg2-server --port 8190 --plugins chat --bots 2`, then point a client at it
   via the live-game bridge's `run-agent.js --role client -- -server 127.0.0.1 -port
   8190`. `tail`ing its launcher log shows any load failure — nothing else does.
3. `gg2_evalx` on the client: `global.serverPluginsInUse` is 1,
   `global.chatPacketID` is 0, and `global.chatLog` holds the server's reply.
   **`chatLog` is the assertion, not a screenshot** — the window hides lines
   older than ten seconds when it is closed (`chat`) or fades after four
   (`chat_v2_pr`), so a screenshot taken a moment late shows nothing.
4. Send as the client would, without needing keyboard focus. On `chat`:
   `execute_string(global.sendChatMessageClient, "hello", 0)`, where the second
   argument is the *boolean* team flag, 0 global and 1 team. On `chat_v2_pr`:
   `with(global.chatWindow) { _message = "..."; _team = 0; event_user(3); }`.
   See the off-by-one warning in each plugin's section.
5. Two clients on different teams exercise the routing. On `chat`, `/votescramble`
   from one of them should raise the dialog on both — `instance_exists(global.voter)`
   and `global.voter.question` are the assertions — and pressing `1` should carry
   it; on `chat_v2_pr`, a second client with the same `PlayerName` exercises the
   name de-duplication instead.

**`--bots N` is worth having on**, because the roster then compacts under you:
the population manager drops a bot to make room for each human, so the first
client's slot moves between its join and its first message. That is exactly the
path `PluginPlayerTable` exists for.

---

## Known issue: a rotation kills every connected client, and it needs bots

A stock GM8 client connected to this server dies with a GM8 fatal error about
**twenty-five seconds after any map rotation** — not a plugin bug; it reproduces
with `--plugins` off too:

```
FATAL ERROR in action number 1 of Create Event for object Character:
Attempted to get a sprite for unknown team ID: 2
```

Team 2 is `TEAM_SPECTATOR`. The client is creating a `Character` for a player its
own roster has as a spectator, so **the server and the client disagree about
somebody's team after a rotation**. The server never sends `PLAYER_SPAWN` for a
spectator — `Timers.PlayerSpawn` guards `player.Team is not (Team.Red or
Team.Blue)` — so the disagreement is in what the client was told, not in what the
server did.

The server sees only `ConnectionReset` and refills the empty slot with a bot, so
**nothing in the server log says anything went wrong.**

### What reproduces it, and what does not

| Rotation | Bots | Client |
|---|---|---|
| ctf_truefort → koth_valley, `--time-limit 1` | 4 | dies, 25s after the change |
| ctf_truefort → koth_valley, `--time-limit 1` | 0 | survives, clean launcher log |

So: **an ordinary time-limit rotation is enough, and bots are required.** Not
caused by the plugin layer, by `/votenextround`/`RequestRoundEnd` specifically
(an ordinary clock rotation reproduces it too), or by the server spawning a
spectator directly (that guard is correct).

### Not yet established — do not treat any of this as diagnosed

- Where the team desync is introduced. `Rounds.ResetForNewMap` sets every player
  to spectator on the server and the client does the same for itself
  (`ClientBeginStep.gml:543`, inside `global.currentMapArea == 1`), so the reset
  itself looks symmetric.
- Why 25 seconds, to the second. Something periodic, and nothing in the server
  log fires at that cadence.
- **A separate suspicion worth checking first, because it may be the same
  bug:** `BotPopulationService.Service` returns early when `bots == target`, and
  nothing else re-teams a bot. After a rotation every bot has been set to
  spectator — so it looks as though **bots may simply stop playing after the
  first rotation**. If that is true it is a bigger bug than the crash and the
  crash may be a symptom of whatever half-re-teams them.

### How to reproduce it in three minutes

```
gg2-server --port 8190 --map ctf_truefort --bots 4 --time-limit 1 \
           --rotation ctf_truefort,koth_valley
cd <bridge> && node run-agent.js --port 17778 --name client1 \
    --role client --keep -- -server 127.0.0.1 -port 8190
```

Wait for `now playing koth_valley`, wait another 30 seconds, then:

```
tail <reference>/Source/build/agent_launcher_17778.log
```

**The launcher log is the only place the failure appears.** The server log
shows a `ConnectionReset` and nothing else, which is indistinguishable from a
player closing the game.

### Why no test catches it

**Every rotation test in the suite is the server talking to itself.** The
server's own state stays perfectly consistent across a rotation — the desync
exists only in what a *client* was told, and the only thing that reads that is a
client. `MapRotationTests` and `MatchActuallyHappensTests` are both green
despite the bug.

**This is an instance of a category, written up in `docs/PARITY.md`, "The gap
in every tier above: nothing compares what a client was TOLD".** Read that for
the general argument — why none of the three automated tiers can see this class
of bug, and what would close it (a harness client that keeps its own roster from
the broadcast). Do not restate the argument here.
