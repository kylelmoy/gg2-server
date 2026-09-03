# Server-sent plugins

**Status: implemented 2026-09-03.** Off by default — a server with no
`--plugins` advertises an empty list and behaves exactly as it did before.

✅ **Verified against two real GM8 clients on 2026-09-03**, not only against the
test harness. Both downloaded `chat_v2_pr` from ganggarrison.com, ran it, chatted
through this server, and saw each other's messages; team chat did not cross
teams; the second client — which had the same `PlayerName` — was renamed
`Player2` by the server and every client saw it. How to repeat it is at the
bottom of this file, because the harness cannot prove the client half loaded at
all.

✅ **The chat commands landed 2026-09-03 too** — `/votekick`, `/votemute`,
`/votemap`, `/votescramble`, `/votenextround`, `/party` and `/help`, ported from
five of Lorgan3's server-side plugins and baked into the same implementation.
⚠️ **Those extend a different chat plugin from the one this server advertises**,
which has a vote dialog `chat_v2_pr` does not, so the vote is text here. See
"The chat commands" below before assuming anything about how they look.

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

---

## The chat commands — `/votekick` and friends

`ChatV2Commands.cs`, folded into the same plugin. Ported from Lorgan3's five
**local** server plugins — `party.gml`, `votekick.gml`, `votemap.gml`,
`votemute.gml`, `votescramble.gml` (forum topic 33773). Those are dropped in a
server's own `Plugins/` directory and loaded by `loadplugins.gml`, so only the
machine running the server ever has them; nothing is sent to a client.

⛔ **They extend a DIFFERENT chat plugin from the one this server advertises, and
that is the fact the whole design turns on.** Every one of them registers into
`global.commandMap` / `global.voteCommandMap` and calls `global.sendVote`,
`global.sendVoteResults`, `global.findPlayer` and `Player.gid`. **None of those
exist in `chat_v2_pr`** — it has no command parsing and, the part that actually
matters, **no vote dialog**. The clients here run `chat_v2_pr`'s GML and cannot
be given one, because this port does not get to change what the site serves.

So **the vote UI becomes text.** The server announces the question and the
options as chat lines, and players answer by typing. Everything else is the
reference's, cited per rule in the source.

⚠️ **This works because the client half does not know commands exist.** It sends
`/votekick bob` as an ordinary chat message; the server sees the leading slash,
acts, and never echoes it. Nothing on the wire changed, and a client with no
plugin at all is unaffected.

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
| `/yes`, `/no` | answer a yes/no vote |
| `/vote <n>` | answer a numbered vote |
| `/kick`, `/nextround` | host only — see below |

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

⚠️ **`global.chatWindow.alarm[0]` is the VOTE TIMER, not the chat window's
fade.** In `chat_v2_pr` that same alarm is the four-second fade, so the
expression reads as "wait four seconds after anybody speaks" — which is not what
it means in the plugin these extend: the refusal message divides it by 30 and
calls the result seconds, and `sendVote` is given `30 * 30`. Reading it the other
way would have shipped a cooldown nobody asked for.

### Where this deliberately differs

⚠️ **The quorum counts players who could vote, not the roster.** The reference is
`yes + no < floor(ds_list_size(global.players) / 2)` — the whole roster, host and
all. This server **fills with bots by default**, and a bot cannot type, so the
reference's sum makes the quorum unreachable on every bot server: the feature
would look implemented and never once pass a vote. Here the divisor is the
clients who are in chat. The reference had no bots, so on its own servers the two
sums are the same number.

⚠️ **`/kick` and `/nextround` answer "You can only use this command as host."**
and nothing else. That *is* the reference's behaviour for a client, and a
headless server has no host player, so it is the only reachable branch. The
operator's equivalent is the control port.

⚠️ **A vote kick is one disconnection, not a ban.** The reference sets
`target.kicked` and keeps a `chatKicklist` its chat plugin consults on reconnect.
A server-wide ban list is a bigger decision than a chat plugin should make by
itself, and a half-built one that only some paths consult is worse than none.
What does persist is the chat mute — it shares the ban list the spam check
already keeps — so a vote-kicked player can come back, and comes back unable to
talk.

⚠️ **A successful map vote overrides the next round only.** The reference assigns
`global.map_rotation` a one-entry list and never restores it, so one vote
replaces the server's rotation for the rest of its life.

⚠️ **`--votable-maps` falls back twice**: to the map rotation, as the reference
does, and then to every map in `--maps`. Without that third step a single-map
server — the default — can only answer "No maps were found!", which is exactly
where a map vote is most wanted.

⚠️ **A vote still running when the map changes is dropped, not resolved.** A
scramble or a round-end vote landing in the round *after* the one it was called
in would act on a world nobody voted about.

⚠️ **Commands count towards the anti-spam limit**, like any other message. Six of
anything in two seconds is a mute.

### What the plugin needed from the server, and why it is safe

The vote commands are the reason `IPluginHost` grew: `Kick`, `SetTeam`,
`EndRound`, `SetNextMap`, `VotableMaps`, `StableIdOf` / `SlotOfStableId`, and a
`Random`.

⛔ **Every one routes through a `GameServer` method a client command or the
control port already uses** — `ApplyChangeTeam`, `Rounds.Decide`, the same kick
path. A plugin still may not touch `WorldState`, and in particular may not draw
from `WorldState.Random`: that stream is observable simulation, so a map-list
shuffle taken from it would move every client's re-derived weapon spread. This is
CLAUDE.md's rule for bots, one layer out — **a plugin must not be able to produce
a state a client could not.**

⛔ **`StableIdOf` is the one that is not a convenience.** A vote runs for thirty
seconds, which is long enough for two people to disconnect, and a slot index
means something different after each one. A vote holding a slot index kicks
whoever inherited the number. The reference's plugin API has exactly this as
`Player.gid`, which is some evidence it is the right shape rather than a local
invention. ⚠️ **It is never on the wire** — clients name players by position, and
that is not negotiable.

### Testing this

`ChatV2CommandTests`, against `FakePluginHost`.

⛔ **Three of those tests were hollow and mutation found all three**, after they
had been written, read and passed. Two voted unanimously to test a threshold —
which passes at *every* threshold — and one asserted a scramble moved nobody,
which is also true when a two-player scramble deals them the teams they already
had. **A threshold needs a row on each side of it**, and an effect under test has
to be one that cannot happen by luck. The fixed tests say so in their own remarks
so the lesson survives the diff.

---

## Verifying against a real client

⛔ **The test suite cannot do this and it is the half that matters.** Everything
in `PluginWireTests` is this port talking to itself: it proves the framing is
self-consistent, not that a GM8 client accepts it, that the zip downloads, that
7-Zip extracts it, or that `execute_file` runs the GML without a dialog. Run this
after any change to the wire fields or to a plugin implementation.

`docs/TRAPS-LIVEGAME.md` and the `gg2-playtest` skill are the general rules —
**exactly one session holds the live game**, and the bridge serves one TCP client.

⛔ **Before you blame your change for a client dying: read the last section of
this file.** A rotation kills every connected client on a server with bots, it
has nothing to do with plugins, and the server log says only
`ConnectionReset`.

1. **Two ini keys, or every launch stops on a modal.** In
   `D:/code/Gang-Garrison-2/Source/build/gg2.ini`, under `[Settings]`:
   `ServerPluginsPrompt=0` (else the client raises the "download these plugins?"
   dialog and the bridge stops answering) and `RestartPrompt=0` (else
   `pluginscleanup` raises one on disconnect). ⚠️ **Back the file up and restore
   it** — it is the live game's own config, not this repo's.
2. `gg2-server --port 8190 --plugins chat_v2_pr --bots 2`
3. Point a client at it, rather than at a GG2 server:
   `cd D:/Code/gg2-agent && node run-agent.js --port 17778 --name client1 --role client --keep -- -server 127.0.0.1 -port 8190`
4. `tail agent_launcher_17778.log` — a clean run has no `E|` or `M|` lines after
   the launch. A plugin that failed to load shows up there and nowhere else.
5. `gg2_evalx` on the client: `global.serverPluginsInUse` is 1,
   `global.chatPacketID` is 0, and `global.chatLog` holds the server's reply.
   ⚠️ **`chatLog` is the assertion, not a screenshot** — the window fades after
   four seconds (`alarm[0]`), so a screenshot taken a moment late shows nothing
   and looks like a failure.
6. Send as the client would, without needing keyboard focus:
   `with(global.chatWindow) { _message = "..."; _team = 0; event_user(3); }`
   — `_team` here is the client's *boolean*, 0 global and 1 team. See the
   off-by-one warning above.
7. A second client with the same `PlayerName` exercises the de-duplication, and
   two clients on different teams exercise the routing.

⚠️ **`--bots N` is worth having on**, because the roster then compacts under you:
the population manager drops a bot to make room for each human, so the first
client's slot moves between its join and its first message. That is exactly the
path `PluginPlayerTable` exists for, and it is free to exercise here.
---

## ⛔ A rotation kills every connected client, and it needs bots

**Found 2026-09-03 while live-testing `/votenextround`. NOT caused by the plugin
layer — reproduced with `--plugins` off — and NOT fixed. It is written here
because this is the file that sent somebody to drive a real client, and the bug
is invisible from the test suite.**

### What happens

A stock GM8 client connected to this server dies with a GM8 fatal error about
**twenty-five seconds after any map rotation**:

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

### The two narrowing runs, both against a real client

| Run | Rotation | Bots | Client |
|---|---|---|---|
| plugins on, `/votenextround` | ctf_truefort → koth_valley | 4 | ⛔ died, 25s after the change |
| **plugins off**, `--time-limit 1` | ctf_truefort → koth_valley | 4 | ⛔ died, 25s after the change |
| plugins off, `--time-limit 1` | ctf_truefort → koth_valley | **0** | ✅ survived, clean launcher log |

So: **an ordinary time-limit rotation is enough, and bots are required.**

### What was ruled out, and what was not

Ruled out:

- The plugin layer. The middle run has none.
- `/votenextround` and `RequestRoundEnd`. The middle run rotates on the clock.
- The server spawning a spectator. The guard is there and is correct.

Not yet established — **do not treat any of this as diagnosed**:

- Where the team desync is introduced. `Rounds.ResetForNewMap` sets every player
  to spectator on the server and the client does the same for itself
  (`ClientBeginStep.gml:543`, inside `global.currentMapArea == 1`), so the reset
  itself looks symmetric.
- Why 25 seconds, twice, to the second. Something periodic, and nothing in the
  server log fires at that cadence.
- ⚠️ **A separate suspicion worth checking first, because it may be the same
  bug:** `BotPopulationService.Service` returns early when `bots == target`, and
  nothing else re-teams a bot. After a rotation every bot has been set to
  spectator — so it looks as though **bots may simply stop playing after the
  first rotation**. If that is true it is a bigger bug than the crash and the
  crash may be a symptom of whatever half-re-teams them.

### How to reproduce it in three minutes

```
gg2-server --port 8190 --map ctf_truefort --bots 4 --time-limit 1 \
           --rotation ctf_truefort,koth_valley
cd D:/Code/gg2-agent && node run-agent.js --port 17778 --name client1 \
    --role client --keep -- -server 127.0.0.1 -port 8190
```

Wait for `now playing koth_valley`, wait another 30 seconds, then:

```
tail D:/code/Gang-Garrison-2/Source/build/agent_launcher_17778.log
```

⚠️ **The launcher log is the only place the failure appears.** The server log
shows a `ConnectionReset` and nothing else, which is indistinguishable from a
player closing the game.

### Why no test caught it

⛔ **Every rotation test in the suite is the server talking to itself.** The
server's own state stays perfectly consistent across a rotation — the desync only
exists in what a *client* was told, and the only thing that reads that is a
client. `MapRotationTests` and `MatchActuallyHappensTests` are both green.

That makes this an instance of the failure mode CLAUDE.md names: the code is
self-consistent and the description ("a stock client has played a round start to
finish", phase 6) is true only for **one** round. Nobody had watched a second
one.

⚠️ **What would make this catchable without a GM8 client:** a harness client that
maintains its own roster from the broadcast — teams included — and asserts it
matches the server's after a rotation. `LoopbackClient` already parses the stream;
it does not model a roster. That is the gate this project is missing, and it is
worth more than a fix for this one symptom.
