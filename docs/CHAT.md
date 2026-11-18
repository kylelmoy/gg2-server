# Chat

The server's own chat: one room, one rate limit, one mute and kick-ban list, one
command console and one vote engine. **On by default** (`[Chat] Enabled=1`).
`ServerChat` (`src/GG2.Server/Chat/`) is built and owned by the server and
configured from `[Chat]`; nothing about it is a plugin except the packet id it
travels on.

| | |
|---|---|
| implementation | `ServerChat.cs`, `ServerChat.Commands.cs`, `ServerChat.Votes.cs` |
| configuration | `[Chat]` in `--config` (`docs/CONFIG.md`), `ServerOptions.Chat` |
| a stock GM8 client speaks | Lorgan3's chat v6, from the `chat` plugin it downloads |
| a native client (gg2-client) speaks | the native format, behind a `0x80` byte on the same packet id |
| tests | `ServerChatTests` (native behaviour), `ServerChatV6Tests` (every v6 translation and the mixed room), `PluginWireTests` (over a socket) |

## Why it rides the `chat` plugin

Stock GG2 has no chat. A stock client gets a chat window only by downloading a
server-sent plugin, and `chat@bad8081d64f0b808a2bb3a6d88978fb5` (Lorgan3's v6) is
on ganggarrison.com and widely installed. So with chat on the server advertises
`chat` itself, whether or not `ServerPluginList` names it, and answers that
packet id with `ServerChat`.

- **Its position.** Named in `ServerPluginList`, `chat` keeps its place there.
  Not named, it is appended, so every listed plugin keeps its packet id. The id
  is the position on both sides (`docs/PLUGINS.md`).
- **Off.** `[Chat] Enabled=0` advertises nothing for chat and runs none.
- **Refused at startup**: `chat` in `ServerPluginList` with chat off (a plugin
  nothing answers), and `chat_v2_pr` with chat on (a second chat window on the
  same key). `chat_v2_pr` is still available with chat off; `docs/PLUGINS.md`.
- **Stock clients are asked to download it.** A server advertising a plugin
  makes a stock client with the default `ServerPluginsPrompt=1` ask its player
  before joining. `ServerPluginsRequired` stays off by default, so a player who
  declines still joins, without chat.
- The catalogue (`ServerPluginCatalogue`) knows `chat` only as the zip to pin; it
  builds nothing for it.

## The wire

| First byte on `chat`'s packet id | Meaning |
|---|---|
| `0x80` (`ServerChat.Envelope`) | the rest is exactly one native message, in either direction |
| anything else | Lorgan3's v6 (`docs/PLUGINS.md`, "`chat` on the wire") |

**The native format is `PROTOCOL.md` version 1 in gg2-chat2026**, the spec both
ends cite. Its host logic (`src/host/`, `src/commands/`, `src/votes/` there) is
what `ServerChat` transcribes; a method citing `host/*.gml` names the file it
stands for.

**The envelope cannot collide.** v6's client kinds are 0-3, and both v6's GM8 host
(`plugin.gml` user event 0, a `switch` with no `default`) and this server ignore
any other first byte, so a native client may greet any `chat` server this way and
fall back to v6 if no WELCOME comes. Server to client, v6 reads a first byte below
200 as a roster index, so `0x80` would read as player 128: **an enveloped frame is
only ever sent to a player who greeted with one.**

**Nothing is sent to a player who has not greeted**, in either format. A GM8
client without the plugin raises a fatal error on a plugin packet it has no queue
for, and gg2-client on iOS joins without ever saying hello.
`ServerChatV6Tests.APlayerWhoNeverGreetsIsSentNothingWhateverHappens` and
`PluginWireTests.AV6AndANativeClientShareOneRoomAndASilentClientHearsNothing` are
the gates.

**The most recent hello sets the format.** A repeat hello in the same format is
ignored. A hello in the other format switches the player: an enveloped HELLO is
answered with WELCOME, a v6 hello with the MOTD, and either re-offers a running
vote without clearing an answer already given. gg2-client sends an enveloped
HELLO, waits two seconds for WELCOME and then falls back to v6, so a WELCOME that
is merely late still leaves both ends on v6.

## What a v6 player is sent

| The server does | native player | v6 player |
|---|---|---|
| a player's line | LINE: index, flags (team, dead, spectating, action), team, name, text | roster index, audience (0, or `1 + team`), raw text. v6 has no field for dead or spectating |
| `/me` | LINE with the action flag | `chr(13)` + the text, which is v6's own `/me` form; a stock client draws `*Name text`. v6's `/me` arrives that way and becomes the action flag |
| join, leave, rename, team change | NOTICE 1-4 | 254, which a v6 client can hide ("Display player info") |
| a reply, a server message, a vote announcement | NOTICE 0, 5 or 6 | 255, never hidden |
| the MOTD (`[Server] ChatJoinMessage`) | WELCOME's motd | a 255 on hello |
| a vote | VOTE_START + VOTE_TALLY | 204 (a 200 with the counts so far) |
| an answer | VOTE_TALLY | one 203 per vote the dialog has not counted |
| the end | VOTE_END with the result text | 201 with the winner (0 when cancelled or when there is no text, which closes the dialog), then the text as a 255 |

- **Every server-composed v6 line is `O` + the text with `#` escaped.** `O` is
  orange, and it is not optional: v6's draw loop eats the first character of
  every line as a colour code. `#` is GM8's line break, so a player called `a#b`
  could split an announcement and forge its second half. The MOTD is the
  exception: `#` there is the operator's own line break. A player's own text is
  relayed raw; the v6 client escapes it at draw time (`chat_api.gml:99`).
- **A v6 dialog can only add.** 203 increments one option, so when a voter leaves
  and a count falls, a v6 dialog keeps the higher number. A player who greets
  mid-vote gets the exact counts in the 204.
- **A v6 kick or mute target gets no dialog**, only the result line: v6 has no
  dialog a player may see and not answer. An answer it sends anyway is not
  counted.
- **v6 answers carry no vote id**, so an answer counts towards whatever vote is
  running. A native VOTE whose id is not the running vote's is dropped.
- A native SAY is capped at 127 bytes; a v6 line may be a whole 255-byte string,
  and goes to native players as one LINE.

## What a stock player notices

The behaviour is the native host's for everybody. Against a GM8 host running v6
and Lorgan3's local vote plugins:

| | GM8 host with v6 and Lorgan3's vote plugins | this server |
|---|---|---|
| kick vote | 90% | `VoteKickPercent`, default 60, inclusive |
| mute vote | 70% | `VoteMutePercent`, default 60 |
| quorum | half the roster | `ceil(voters / 2)` and at least two, of the players offered the vote |
| a vote kick | one disconnection | refuses the address for `KickBanMinutes` (default 10) |
| a mute | permanent, and the muted address's hello is refused, so it reads nothing either | timed (`MuteMinutes`), and a muted player still reads chat |
| spam | no limit | a burst of 3, then one line per 2 seconds; ten refusals in 30 seconds mute for a minute |
| team chat | no commands (`plugin.gml:493-505` skips the console), and spectators cannot use it | commands run whatever the audience; spectators reach spectators |
| gravetalk | none | with `Gravetalk=0`, the dead and spectators are heard only by each other |
| joining | "X has joined chat!" on hello | "X has joined the game." on arrival, and leave, rename and team-change lines |
| the first minute | nobody can call a vote (`chat_api.gml:175`) | no wait |
| `/party` | yes | no |
| the player picker | everybody, slot 0 and bots included | never slot 0 or the caller |

## Commands

`/help`, `/players`, `/maplist`, `/login`, `/votekick`, `/votemute`,
`/votescramble`, `/votenextround`, `/votemap`, and for an admin who has used
`/login` (`[Chat] AdminPassword`) `/say`, `/kick`, `/mute`, `/unmute`,
`/nextround` and `/map`. `/me`, `/ignore` and `/unignore` are listed in `/help`
and run in the native client. A vote command with no name opens a private picker
vote, which remembers stable ids, never slots. **`/bots`** is described in
`docs/PLUGINS.md`, "The bot suspension and the join line", and works the same here.

## Where it differs from the native reference host

- **Slot 0 never greets**, so it never receives, votes, or counts towards a
  quorum. On a dedicated GM8 host the GML does the same (`chat_vote_host`
  defaults off there). "The host" in a reply means slot 0.
- **A map vote calls `SetNextMap`**, and map votes and the map picker offer
  `VotableMaps`, so a single-map server can vote on a map. `/map` is one path.
- **Scramble uses `SetTeam`**, the server's own team switch.
- **`/bots` and the admin panel's chat box** (`IServerChat.SayAsServer`, a line
  from slot 0 that is never a command and never rate limited) have no GML.
  `/help` lists `/bots` first, so its text differs from the reference host's.

## What it reads from the server

`ServerChat` reaches the server through `IPluginHost`, the same surface the
plugins use (`docs/PLUGINS.md`, "What the plugin needed from the server"), plus
two reads:

| Member | GML | Why |
|---|---|---|
| `IsAlive(slot)` | `player.object != -1` | a dead sender's line is flagged dead and routed by gravetalk |
| `RoundOver` | `global.mapchanging` | a running vote is cancelled when the round ends, and none can start |

## Verified against a real client

**Driven on 2026-11-18** (`--plugins chat`, before chat was on by default), with
one stock GM8 client speaking v6 and a scratch socket client speaking the native
format, read back from the GM8 client's `global.chatLog` and `global.voter` and
from the decoded frames the socket client received:

| Verified | How it was read back |
|---|---|
| v6 hello, the MOTD as a 255 | `chatLog` ended `OWelcome!#Second line` |
| enveloped HELLO, WELCOME | `WELCOME v1 flags=3 burst=3 secs=2 motd='Welcome!#Second line'` |
| join and leave notices to v6 | `Odesk26 has joined the game.`, `Odesk26 has left the game.` |
| native to v6 | `Wdesk26: <CR>Whello from chat2026`, coloured by the client from the roster index |
| v6 to native | `LINE #1 flags=4 team=2 stockv6: hi from stock v6 a#b` |
| `/me` from native to v6 | `W *desk26 waves`. Sent first as `chr(13)` + `W` + text, it drew `*desk26 Wwaves`: `doColorCode` runs `sanitiseNewlines`, which turns the `chr(13)` into a space, so nothing after it is a colour code |
| a vote called from a native client | the v6 dialog opened from a 204 with the question, both options and the clock, and counted the caller's Yes from a 203 |
| a v6 answer | the native client got `VOTE_TALLY 2 2,0` then `VOTE_END 2 winner=1 Teams scrambled (2 yes, 0 no).`; the v6 dialog showed `win` 1 with `total` 2, and `chatLog` ended with the result as a 255 |

gg2-client then confirmed two of its desktop clients settling on the native format
against this server and chatting both ways, and its no-hello client staying
connected on a server that requires `chat`.

Not covered: a mid-vote 204 resync to a v6 client that greets late, a v6 kick
target, two stock clients, the 255-byte chunking, and the default-on
advertisement against a stock client (the download prompt). How to drive a client
is `docs/PLUGINS.md`, "Verifying against a real client".
