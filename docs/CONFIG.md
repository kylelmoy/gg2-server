# The config file — `gg2-server --config`

Every server setting can be read from a reference-format `gg2.ini`, written back
to one, and edited from the admin panel. This file is the key reference and the
rules around it.

```powershell
gg2-server --config gg2.ini
```

```ini
; a hand-written config. Anything absent keeps its default.
[Settings]
HostingPort=8190
PlayerLimit=16

[Server]
ServerName=A Real Server
Rotation=ctf_truefort,koth_valley,cp_dirtbowl

[Bots]
Enabled=1
FillToPlayers=8
MinHumans=1
```

## The three rules

⛔ **A flag always beats the file, whichever side of `--config` it appears on.**
`--config` is applied in its own pass before the argument loop, so
`--map koth_valley --config gg2.ini` and `--config gg2.ini --map koth_valley` are
the same server. `CliCoverageTests.AFlagBeatsTheConfigFileInEitherOrder` pins it.

⚠️ **The consequence is a trap the admin panel has to work around**: a setting
pinned by a flag can be edited in the panel, saved correctly, restarted, and do
nothing. `ServerOptions.CommandLineOverrides` is the list of settings that
applies to, computed by diffing what `Parse` built against what the file alone
would produce; the panel marks those fields **pinned**.

⚠️ **Anything absent keeps this codebase's default, not the reference's.** The
two agree everywhere except `UseLobby`, which defaults **off** here on purpose —
`ServerOptions.AnnounceToLobby` has the argument. Unknown keys and unknown
sections are ignored rather than rejected, so a real `gg2.ini` copied off a
hosted GG2 server works unedited.

⛔ **A value that must be a number and is not is refused, not ignored.** A typo
in `PlayerLimit` that silently left the default in place is a server quietly
running a configuration nobody wrote.

## Where the keys came from

⚠️ **`[Settings]`, `[Server]`, `[Classlimits]` and `[Maps]` are the
REFERENCE's**, spelling and all — `Total bandwidth limit for map downloads in
bytes per second` is a real key in a real `gg2.ini`
(`Scripts/Game/game_init.gml:66-106`). They are transcribed rather than tidied so
an inherited config works here.

`[Limits]`, `[Log]`, `[Nav]`, `[Bots]`, `[BotClasslimits]`, `[Web]` and `[Runtime]` are **this
port's own**: stock GG2 has no connection bounds, no log rotation, no nav
graphs, no admin panel and no pacing control, so there was nothing to transcribe.

⚠️ **The reference's hosting port lives under `[Settings]`, not `[Server]`**,
which reads as a mistake and is load-bearing: an ini written by the stock client
puts it there and a server that looked under `[Server]` would silently listen on
the wrong port.

## ⛔ One table, four readers

`ServerSettings.All` is the single table of settings. It is read by:

| | |
|---|---|
| `ServerConfigFile.Apply` | the file → options |
| `ServerConfigFile.Write` | options → the file |
| `ControlCommands.Config` / `config-set` | the admin panel's settings page |
| `ConfigFileCoverageTests` | the gate, against `ServerOptions`'s own properties |

**Four hand-written lists would be four chances for one to be missing a row**,
and a missing row is silent in every direction: a setting that loads and cannot
be saved, or one the page shows and the file ignores.

⚠️ **Order matters in exactly two places.** `[Web] Enabled` must come after
`[Web] Port`, and `[Runtime] ControlPortEnabled` after `[Runtime] ControlPort`,
because naming the port also turns the thing on and the explicit switch has to
win. `Apply` walks the table in **table order**, not the file's, for that reason.

⚠️ **The stored form of every setting is a STRING — the ini's own form.** The
`SettingKind` is presentation and validation, not representation. One
representation is what makes `Write` the exact inverse of `Apply`; a second one
is where a round-trip starts losing values.

### Adding a setting

1. Add the property to `ServerOptions`.
2. Add a row to `ServerSettings`, with a `Probe` that is a legal **non-default**
   value.
3. Add a flag and a `CliCoverageTests` row, or a reason to
   `CliCoverageTests.NotOnTheCommandLine`.
4. Add its one-line description to `src/GG2.Server/Web/Assets/strings.js`, under
   `settingHelp.<Section>.<Key>`.

`ConfigFileCoverageTests` fails on a property with no row, so step 2 cannot be
forgotten, and `WebStringsTests.EverySettingHasOneDescription` fails on a row
with no description, so step 4 cannot be either. Nothing else has to be added
to the admin panel: it renders the table.

## The gates

| | |
|---|---|
| `EverySettableOptionIsReachableFromAConfigFile` | reflects over `ServerOptions` and fails on a property no row names, or a row naming a property that no longer exists |
| `EveryRowChangesTheOptionItNames` | each row's `Set` actually moves the property, off its default — a setter that drops the value on the floor looks identical from outside to one that works |
| `EverySettingSurvivesAWriteAndAReRead` | `Write` then `Apply` gives every setting back, **probed with a non-default value**: a round trip over defaults would pass for a setting the writer skips, because the reader would leave the default in place and the two would agree for the wrong reason |
| `TheRoundTripWouldNoticeAWriterThatWroteNothing` | the vacuity control for the row above — a writer that emitted nothing and a reader that read nothing would agree perfectly |

⚠️ **The probe is spelled out per row rather than derived from the kind.** A
derived probe has to guess at every clamp, every enum's spelling and every list's
separator, and one that lands on the default makes the assertion pass while
testing nothing. Booleans carry the `<flip>` sentinel instead of a literal,
because `TcpKeepAlive` defaults on and `TickStats` defaults off and one fixed
string would be a silent no-op for half of them.

## Settings that are valid and cannot do anything

A different failure from an invalid value, and none of the gates above catch it.
`Validate` refuses a value that is out of range; `EverySettableOptionIsReachable`
proves a key reaches its property. Both pass for a setting that is perfectly
valid and delivered by a subsystem this server is not running — the operator sets
it, the settings page shows it back, a restart applies it, and nothing happens.

`ConfigWarnings.For` is the one place those combinations are named, and three
surfaces read it so they cannot drift into saying different things about the same
server:

| Surface | When |
|---|---|
| the server's log, `Warn` | at startup, after the plugins are loaded |
| the fleet editor's notes | on the page where somebody sets them |
| `config render` warnings | before a push, which is the last point anybody is looking |

**A warning, never a refusal.** Setting a join line before adding the plugin that
carries it is a reasonable order to work in, and a configuration that will not
start is worse than one that says what it is missing.

What it currently names: `[Server] ChatJoinMessage` and `[Bots] SuspendMinutes`
on a server with no chat: `[Chat] Enabled=0` and no `chat_v2_pr` in
`ServerPluginList`. Both are delivered by chat and by nothing else.
`ConfigWarningsTests` gates each one in both directions — a warning that fired
whatever was configured would be noise an operator learns to skip.

## Writing it back

`ServerConfigFile.Write` is what the admin panel's save calls. **It edits; it
does not regenerate.**

- Comments, blank lines, key order and keys this project has never heard of are
  left exactly where they were.
- A managed key the file already names has its value replaced **in place**, so
  the file does not grow a second `ServerName` the reader would prefer. ⚠️ The
  **last** occurrence wins, matching the parser: updating the first of two would
  write a value the reader then ignores.
- ⚠️ **A key is only written when the file already names it, or when its value
  differs from what the file alone would produce.** Writing every key
  unconditionally is what this replaced, and it was wrong in a way only a real
  save showed: `[Server] MapDirectory` and `[Nav] Directory` default to paths
  **resolved from disk**, so the first save stamped one machine's absolute
  install path into the operator's config and pinned it there. It also turned a
  twelve-line hand-written ini into eighty lines of defaults.
- ⚠️ **Values whose text would not read back are quoted.** The case that made
  this necessary is a **trailing space**, and it is a default:
  `[Bots] NamePrefix` is `"[BOT] "`, the reader trims every value, and an
  unquoted round trip renames every bot on the server from `[BOT] Scout` to
  `[BOT]Scout`. `ini_write_string` quotes unconditionally; this quotes only when it
  changes the answer, so an ordinary file stays free of quotes nobody typed.
- ⚠️ **Written through a temporary file and moved into place.** The config file
  is the thing the admin password is in; truncating it is how a panel locks its
  operator out.

## The rotation, and its three sources

Checked in this order, and the order is the point:

1. **`[Server] Rotation`** — a comma-separated list. This port's own key and the
   one `Write` emits, so it has to win: a rotation saved from the panel that lost
   to a `[Maps]` section left in the same file is an edit that silently does
   nothing.
2. **`[Server] MapRotation`** — a rotation **file**, one map per line, `#`
   comments skipped (`sampleMapRotation.txt`). ⚠️ The reference falls back to
   `[Maps]` when the named file does not **exist**, silently; that is
   transcribed rather than turned into an error, because diverging would make the
   same ini behave differently on the two implementations. A relative path is
   resolved against **the config file's own directory**, not the working
   directory — the reference's ini is always beside the executable and
   `--config /etc/gg2/server.ini` is not in that position.
3. **`[Maps]`** — the reference's priority list. ⚠️ **Priority order, not file
   order**, and a map with no key gets its 1-based position in the built-in
   twenty as its priority — which is what makes an empty `[Maps]` produce the
   stock rotation rather than nothing. **`Maps` REORDERS the twenty; it does not
   select from them.** A key of `0` means "never play this map". Ties are broken
   by the built-in order, which the reference leaves unspecified.

⚠️ **Saving removes a lower-precedence source that disagrees** with the rotation
being written, because a stale one is not merely redundant — it is a line an
operator will edit expecting it to do something. A rotation file that still names
the same maps is left alone, so a server that uses one keeps using it.

⚠️ **A file with a rotation and no `Map` key opens on the rotation.**
`GameServerCreate.gml:110-113`, expressed by emptying `Map` (`global.launchMap ==
""`). Applied by `Apply` rather than only by `CommandLine`, because two other
things ask "what would this file alone produce": the panel's settings page, and
the command-line override diff. Without it both compared a file-loaded `Map` of
`ctf_truefort` against a running `Map` of `""` and reported the opening map as
changed by a command line that never mentioned it.

## Deliberately absent

⚠️ **A row here is a decision, not an exemption to reach for.** The bar is that
the setting would be *wrong* to expose, not that nobody has wired it up.

| | |
|---|---|
| `RandomSeed` | forces the simulation's random stream, which a live server must never do — the reference's `randomize()` is what stops every server on a machine scattering its first blast identically. It has no flag either. A config key would invite somebody to run a real server with it, and unlike a flag it would then stay there across every restart. |
| `ConfigFile` | is the path of the config file itself. A file naming another file is a redirect with a cycle in it and no rule about which wins. |
| `CommandLineOverrides` | is derived, not configured: a config key for it would be a file describing which of its own keys are being ignored. |

⛔ **`[Haxxy]` is not read.** `RewardKey`/`RewardId` are the reference's HOST
self-auth, and a headless server has no local player to decorate. Neither are
the client preferences under `[Settings]` — crosshairs, gib level, HUD
positions, music — which have no meaning here.

## Editing it from the admin panel

`docs/WEBADMIN.md`, "The settings page". The short version:

- The page renders `ServerSettings.All`; nothing about it has to be updated when
  a setting is added.
- ⛔ **It edits the FILE, not the running server.** Every subsystem reads its
  options once, at `GameServer.Start`. The panel says so and offers a restart.
- ⛔ **A secret's value is never sent to the browser** — only whether one is set.
  An empty secret field is not submitted, or opening the page and pressing save
  would erase the admin password.

## Many servers, one configuration

Several hosts that share a configuration except for a few keys (normally
`ServerName` and `WelcomeMessage`) are configured from one directory on the
operator's machine and pushed. Nothing here is reachable from the network.

```
deploy/config/common.ini              every setting the servers share
deploy/config/hosts/<dest>.ini        one host's own keys; they win over common.ini
```

`<dest>` is the ssh destination as `deploy/hosts` names it, with every character
outside `[A-Za-z0-9._@-]` replaced by `_` (`FleetConfig.OverlayFileName`, and the
same `sed` in `deploy.sh`; `FleetConfigTests` pins the two together).

| Command | What it does |
|---|---|
| `gg2-server config edit` | a local page (loopback, one-run token in the printed URL) for writing `common.ini` and each host's overrides. A save validates every host's result before writing any file. |
| `gg2-server config render --out <dir>` | writes each host's `gg2.ini`, validated. `deploy.sh` calls it. |
| `scripts/deploy.sh --config --check` | renders, diffs each host's current `gg2.ini` against it (passwords masked), writes nothing |
| `scripts/deploy.sh --config` | writes `shared/gg2.ini` and `shared/gg2.ini.pushed` on each host that changed |
| `scripts/deploy.sh --config --restart` | the same, then restarts at once, dropping anyone connected |
| `scripts/deploy.sh --status` | adds a `config` line: not managed, edited on the host, in force, or waiting |

**A rendered file is `common.ini` followed by the overlay, verbatim.** The reader
keeps the last value of a repeated key, so the concatenation means "the overlay
wins" by the reader's own rules. Applying the two files one after the other is
not equivalent: an overlay with no `Map` key empties a `Map` that `common.ini`
set, because a file with a rotation and no `Map` opens on the rotation.
`FleetConfigTests.AnOverlayThatDoesNotNameTheMapKeepsCommonsMap` has both arms.

**`[Server] MapRotation` is refused at render**: it names a file beside the
config, and only the config is pushed. Use `[Server] Rotation`.

**A push is applied by the server, the first time nobody is connected**
(`PushedConfigWatcher`). Every 15 seconds the server reads `gg2.ini.pushed`; when
it names the hash of the current file and that is not the file the server started
from, the server re-validates the file and does the panel's in-process restart
once no player, spectator or joining connection remains. At start it writes the
hash it started from to `gg2.ini.applied`, which is what `--status` compares.
Only a pushed file triggers this: a panel save changes the file and not the
marker, so it is never applied behind the operator's back. A release that
predates this code never applies a push by itself; `--restart` does.

**A host whose file was edited there since the last push is refused**, with the
diff of what a push would undo, until `--force`. The same applies to a first push
over a file `--config` never wrote. The admin panel's settings page says when its
file is managed, edited since the push, or has a push waiting.

What would show this has gone stale: `deploy.sh --status` reporting "waiting" on a
host that has been empty for more than a minute on a release built from this code,
or a push that `--check` reports as up to date while the host's server name
differs from the overlay's.

## Every key

⚠️ **Written by hand from `ServerSettings.All` — if a key here disagrees with the
code, the code is right and this table is stale.** `ConfigKeyReferenceTests` is
what stops a key being added to the code and never reaching this list; it checks
that every setting is NAMED under its own section, not that what is said about it
is true. `[Maps]` is exempt: it is a map name per key rather than a fixed set,
and it is described above.

### `[Settings]`

| Key | Type | Default | |
|---|---|---|---|
| `HostingPort` | integer | `8190` | The game port clients connect to. 0 asks the OS for a free one. |
| `PlayerLimit` | integer | `10` | Seats for network clients, 1..48. Slot 0 and bots do not count against it. |
| `MultiClientLimit` | integer | `3` | Connections allowed from one address. Loopback is exempt. |
| `PlayerName` | text | `Server` | The name of reserved roster slot 0, a permanent spectator. |
| `UseLobby` | boolean | `0` | Announce this server to ganggarrison.com so it appears in the client's server list. The only outbound connection this project makes. |
| `Rewards` | boolean | `0` | Answer `REWARD_REQUEST`, relaying each player's reward id to the auth server. |

### `[Server]`

| Key | Type | Default | |
|---|---|---|---|
| `ServerName` | text | `My Server` | Shown in the client's server browser and on join. |
| `WelcomeMessage` | text | *(empty)* | Sent to each joiner. Empty means none. |
| `ChatJoinMessage` | text | *(empty)* | A **chat** line sent to each player as they join chat. Empty means none. A different channel from `WelcomeMessage`: that one is a `MESSAGE_STRING` every client draws, this one needs chat (`[Chat] Enabled`, the default, or `chat_v2_pr`) and a client without it never sees it — ⚠️ set with neither it is **warned about**, not refused. To a v6 client `#` starts a new line, to a native client it is a `#`, and on `chat_v2_pr` it becomes a space, because that plugin's renderer uses `#` as a colour marker. |
| `Password` | secret | *(empty)* | The game password. Empty means none, and no `PASSWORD_REQUEST`. |
| `Map` | text | `ctf_truefort` | The map to open on. Empty means the first entry of the rotation. |
| `Rotation` | list | *(empty)* | The map rotation, in order. A map may appear more than once. |
| `ShuffleRotation` | none / arena-last / any | `none` | How the rotation is ordered at startup. `arena-last` shuffles but does not open on an arena map. |
| `VotableMaps` | list | *(empty)* | Maps a plugin map vote may offer. Empty means the rotation, else every map in the maps directory. |
| `ServerPluginList` | text | *(empty)* | Server-sent plugins clients are asked to load, comma-separated. With `[Chat] Enabled` on (the default) the server also advertises `chat`, appended unless listed here. |
| `ServerPluginsRequired` | boolean | `0` | Clients must load the plugin list to join. |
| `AutoBalance` | boolean | `1` | Move a player across when one team is two larger. |
| `AimCheck` | `off`, `log`, `kick` | `log` | Port's own. What to do about an `INPUTSTATE` whose aim no mouse could produce: nothing, one log line per connection, or that line and a kick after 30. `docs/HARDENING.md`, "The aim check". |
| `AimAnalysis` | boolean | `1` | Port's own. Per-player aim statistics; logs a player whose lock, flick, trigger or hidden-lock rate crosses a threshold no recorded human has. Log only. `docs/HARDENING.md`, "Aim analysis". |
| `ClientPrediction` | boolean | `1` | Port's own. Answers a client's `PREDICT_HELLO`: a welcome, one input per tick through a short queue, and an ack every tick, to that client only. A stock client never sends the hello and is sent the same bytes either way. `docs/PREDICTION.md`. |
| `PredictionMaxQueuedInputs` | integer | `2` | Port's own. Inputs a predicting client may have waiting after a tick before the oldest fold into that tick. Clamped 1-4. `docs/PREDICTION.md`, "The input queue". |
| `PredictionQueueDrainTicks` | integer | `30` | Port's own. Ticks a predicting client's queue may stand non-empty before two inputs fold together. Clamped 1-3600. |
| `QuickUpdateInterval` | integer | `7` | Port's own. Ticks between position updates (`QUICK_UPDATE`); the rest are `INPUTSTATE`. 7 is the reference's cadence. Lower corrects every client's mispredictions sooner, stock clients included, for more bytes. Clamped 1-7. `docs/PREDICTION.md`, "Position update cadence". |
| `ExplosionUpdateTicks` | integer | `0` | Port's own. Position updates on this many ticks after each rocket or mine explosion, starting with the first update that can show it; 0 is off. Bounds how long a client's own resolution of an explosion (upstream issue #68) can stand. Clamped 0-30. |
| `CapLimit` | integer | `5` | Captures that win a round, 1..255. **0 is clamped to 1, not to unlimited.** |
| `Time Limit` | integer | `15` | Round length in minutes, 1..255. 0 is clamped to 1. |
| `Deathmatch Kill Limit` | integer | `30` | Kills that win a team-deathmatch round, 1..65535. |
| `Team Deathmatch Invulnerability Seconds` | integer | `5` | Spawn protection in team deathmatch. 0 really is none. |
| `Respawn Time` | integer | `5` | Seconds a dead player waits. 0 becomes a single tick. |
| `Total bandwidth limit for map downloads in bytes per second` | integer | `50000` | **TOTAL** across every custom-map download in flight, not per client. |
| `BanFile` | path | *(empty)* | Where operator bans are kept. Empty means they do not survive a restart. |
| `MapDirectory` | path | *(resolved from disk)* | Where the map PNGs are read from. |
| `RecordingDirectory` | path | *(empty)* | Write replayable `.trace` recordings of this server's broadcast stream here. Empty records nothing — recording a game people are playing is a decision. |
| `AuthServerHost` | text | `ganggarrison.com` | The reward auth service. Only contacted when `Rewards` is on. |
| `AuthServerPort` | integer | `45022` | That service's port. |

### `[Chat]`

The server's own chat (`docs/CHAT.md`). None has a command-line flag.

| Key | Type | Default | |
|---|---|---|---|
| `Enabled` | boolean | `1` | The server runs chat and advertises the `chat` plugin for it, which is how a stock client gets a chat window: appended to `ServerPluginList` unless the list names it. Off, nothing is advertised for chat. `chat` listed with this off, or `chat_v2_pr` listed with it on, is refused at startup. |
| `Gravetalk` | boolean | `1` | The living hear dead players and spectators. Off, those are heard only by each other. |
| `UniqueNames` | boolean | `0` | Rename a player whose name is taken to the lowest free number (`bob2`). Every client sees the rename, with or without chat. |
| `MuteMinutes` | integer | `10` | A vote mute, and `/mute` with no minutes, 1..1440. Follows the address across reconnects. |
| `Votes` | boolean | `1` | Players may call `/votekick`, `/votemute`, `/votescramble`, `/votenextround` and `/votemap`. |
| `VoteKickPercent` | integer | `60` | Yes share a kick vote needs, 1..100, inclusive. Half the voters, and at least two, must answer. |
| `VoteMutePercent` | integer | `60` | The same, for a mute vote. |
| `KickBanMinutes` | integer | `10` | How long a vote-kicked address is refused, 0..1440. It is an address, so everyone behind the same NAT is refused with it. |
| `AdminPassword` | secret | *(empty)* | The password for `/login`, which gives `/say`, `/kick`, `/mute`, `/unmute`, `/nextround` and `/map` for the session. Empty disables `/login`. |

### `[Analytics]`

Both off by default, and grouped rather than left in `[Server]` because both are
about keeping a record of people: an operator reading a config file should find
that decision in one place. `docs/ANALYTICS.md` has what the file holds and what
it deliberately does not.

| Key | Type | Default | |
|---|---|---|---|
| `File` | path | *(empty)* | Count who plays here and keep it across restarts. Empty counts nothing and writes nothing. Addresses are stored as salted hashes, never as addresses. |
| `GeoIpDatabase` | path | *(empty)* | A MaxMind `.mmdb` used to put players on the analytics map. **None ships with this server.** A country-level database resolves no US states. Only opened when `File` is set. |

### `[Classlimits]`

Ten keys — `Scout`, `Pyro`, `Soldier`, `Heavy`, `Demoman`, `Medic`, `Engineer`,
`Spy`, `Sniper`, `Quote` — each an integer defaulting to `255`.

⚠️ **A limit is PER TEAM, not per server**: `Medic=1` is one medic each, two on
the map. ⛔ **0 BANS the class and 255 unlimits it**, which is the reference's
encoding (`readClasslimitFromIni.gml`) and the wrong way round from every
intuition. A negative becomes 0, which bans rather than unlimits.

### `[Log]`

| Key | Type | Default | |
|---|---|---|---|
| `File` | path | *(empty)* | **Tee** the log into this file. The console sink stays wired — the console is the record an operator has when the thing that broke is the disk. Not needed under systemd or Docker. |
| `MaxBytes` | integer | `16777216` | Rotate the log file at this size. At least 1024. |
| `Keep` | integer | `5` | Rotated log files to keep, 0..1000. |

### `[Limits]`

⛔ **`docs/HARDENING.md` before changing any of these.** Every default is orders
of magnitude above what a stock client uses — `ConnectionLimits` states the ratio
for each — so raising one is not a way to be more generous to real players, it is
a way to give an attacker more room.

| Key | Type | Default | |
|---|---|---|---|
| `MaxReceiveBufferBytes` | integer | `262144` | Unread bytes one client may have buffered before it is dropped. A stock client's steady state is a few bytes. |
| `MaxReceiveBytesPerTick` | integer | `65536` | Bytes read from one socket in one tick, however many are waiting. This one is about the **clock**, not about memory. |
| `MaxSendBacklogBytes` | integer | `1048576` | Queued bytes one client may leave unread before it is dropped. The bound on the cheapest attack there is: connect, join, stop reading. |
| `MaxInvalidCommandBytes` | integer | `4096` | Garbage one connection may send before it is dropped. |
| `MaxAcceptsPerTick` | integer | `32` | New connections taken off the listener in one tick. |
| `MaxJoiningConnections` | integer | `128` | Half-finished handshakes held at once. |
| `MaxHandshakeSeconds` | integer | `600` | Longest a connection may take to finish joining, however active it is. ⚠️ **NOT the 30s idle timeout** — a `PING` resets that one and cannot reset this. |
| `ConnectionBurstPerAddress` | integer | `10` | Connections one address may open inside the window below. 0 turns the rate limit off. ⚠️ Not `MultiClientLimit`, which bounds how many are held at once. |
| `ConnectionBurstWindowSeconds` | integer | `60` | The window that burst is counted over. |
| `ChatBubbleIntervalMs` | integer | `250` | Smallest gap between one player's chat bubbles. |
| `TcpKeepAlive` | boolean | `1` | Probe for peers that vanished. ⚠️ Off is a **regression**: a client whose machine dies holds a roster slot for ~15 minutes. A spectator sends nothing, so this is the mechanism rather than a heartbeat. |
| `KeepAliveIdleSeconds` | integer | `30` | Quiet seconds before the first keepalive probe. |
| `KeepAliveIntervalSeconds` | integer | `10` | Seconds between probes. |
| `KeepAliveProbeCount` | integer | `3` | Unanswered probes before the peer is declared gone. |
| `UnacknowledgedTimeoutSeconds` | integer | `60` | Seconds sent data may go unacknowledged before the connection drops (Linux `TCP_USER_TIMEOUT`, Windows `TCP_MAXRT`); 0 keeps the system's ~15 minutes. **This, not keepalive, frees a connected player who vanished**: keepalive never probes while the broadcast has data in flight. `docs/HARDENING.md`, "Peers that vanished". |

### `[Nav]`

| Key | Type | Default | |
|---|---|---|---|
| `Directory` | path | *(resolved from disk)* | Where the bot nav graphs are read from. Empty is legal: bots then fight but do not walk. |
| `Generate` | boolean | `1` | Build a missing graph at the map change, which is what makes a custom map navigable. Held in memory unless `Save` is on. |
| `Save` | boolean | `0` | Write a generated graph into the nav directory. Off by default because it writes files the operator did not ask for. |
| `Distances` | boolean | `1` | Give A\* exact node-to-node distances instead of a straight-line estimate. ⛔ **Off is a DIFFERENT ARM, not a slower one** — the search is weighted, so this picks the route as well as the speed. |

### `[Bots]`

`docs/BOTS.md` before changing the last five.

| Key | Type | Default | |
|---|---|---|---|
| `Enabled` | boolean | `0` | Manage a bot population at all. |
| `FillToPlayers` | integer | `8` | How many players the server wants **in total**, humans included — not a bot count. |
| `MaxBots` | integer | `8` | A hard ceiling on top of the fill target. |
| `MinHumans` | integer | `1` | Humans that must be connected before **any** bot is spawned. ⚠️ Below it the target is **zero**, not the fill — an empty server full of bots looks full in the lobby list and is not. |
| `Difficulty` | integer | `5` | 1 to 5. 5 is "as good as the layer gets"; the lower tiers are a deliberate handicap ladder, and with `HumanAim` on (the default) they also aim like people. |
| `NamePrefix` | text | `[BOT] ` | What a bot's name starts with. ⚠️ The trailing space is real — see the quoting rule above. |
| `Duel` | boolean | `0` | The duel server: each human on red fights one blue bot of their own class, both pinned to one cell of a cell map. Replaces the fill (`Enabled`, `FillToPlayers` and `MaxBots` are not read), moves a human who picks blue to red, and holds `AutoBalance` off. Serve `maps/duel`; see `docs/DUEL.md`. |
| `DuelLadder` | boolean | `1` | On a duel server, each human's bot plays a level 1-8 that rises when they win and falls when they lose, one per human and class, and is named for it (`Soldier 3 (Normal)`); `Difficulty` is not read for duel bots. Off, every duel bot plays `Difficulty` and is named `NamePrefix` + class. `docs/DUEL.md`, "The difficulty ladder". |
| `DuelLadderStartLevel` | integer | `3` | The ladder level a duel human starts each class at. 3 is `Difficulty` 2's skill. Clamped 1-8. |
| `DuelLadderWinPercent` | integer | `60` | The share of duels the ladder settles a human at winning. Clamped 50-90. |
| `AutoDifficulty` | boolean | `0` | Off a duel server: each human gets a level 1-8 from their kills of and deaths to bots, and every bot plays the level of the human it is shooting at. Bot against bot, and a new human's start, stay at `Difficulty`. `docs/BOTS.md`, "Auto difficulty". |
| `AutoDifficultyWinPercent` | integer | `60` | The share of their fights with bots auto difficulty settles a human at winning. Clamped 50-90. |
| `SuspendMinutes` | integer | `0` | How long the `/bots off` chat command takes the bot population away for, 0..1440. ⚠️ **0 means the command does not exist on this server**, which is the default — one key rather than a flag and a duration that can disagree. Needs chat (`[Chat] Enabled`, the default, or `chat_v2_pr`), which is the only way a player can type `/bots`; set with neither it is **warned about**, not refused, and the control port's `bots-suspend` still works. An operator ends a running suspension by saving a bot count in the admin panel, or with `bots-suspend` at 0 minutes. `docs/PLUGINS.md`, "the bot suspension". |
| `ClassPolicy` | list | *(empty)* | Which controller **drives** each class — `Class=name` entries, where name is a checkpoint's file name without its extension or `scripted`. A class not named is driven by no net: the search takes it under `tiered`, the scripted layer otherwise. A net nobody assigned a class is warned about, because it loads and never drives. A misspelt name, a class named twice, or the setting under a mode that runs no policy is refused. `Quote` is accepted: no fill bot is ever a Quote, but the duel server makes one for a Quote human. |
| `Occupancy` | boolean | `1` | Price an edge a team-mate is already walking above its honest cost, so a squad fans out. ⛔ A **behaviour** switch: off, match dispersion falls 0.785 → 0.634 and nothing replaces it. |
| `Humanize` | boolean | `0` | Inside a fight, make each bot's aim flick and correct the way a person's does. Keys are untouched. A **behaviour** switch, and a human-like bot is a weaker one. `docs/TRAPS-BOTS.md`, "Human-like output". |
| `HumanizePreset` | `v3`, `strokes`, `lazy`, `hold`, `tracking`, `tracking2`, `tracking3`, `tracking4`, `tracking5`, `tracking6`, `tracking7`, `tracking8`, `tracking9` | `v3` | Which measured `HumanizedBot` preset `Humanize` uses. `strokes` is v4, `lazy` is v5 (lazy aim between shots) and `hold` is D2 (the aim holds while the bot has no target and an enemy is close); each is a measured arm, `docs/TRAPS-BOTS.md`, "Human-like output". Ignored unless `Humanize` is on. |
| `HumanAim` | boolean | `1` | Difficulty 1-4 aim and move like people: they lag a target, sit off it, and turn round after a reversal cooldown that lengthens as the tier falls (`HumanizeOptions.Tracking9`, D12). Tier 5 is untouched and stays the strongest bot. A **behaviour** switch, and what the lower tiers mean; `Humanize`, when on, wins. `gg2-rl` `docs/PLAN.md` phase 5, D3, D4, D9 and D10. |
| `EngageBots` | boolean | `0` | Run the expensive micro-controller in bot-vs-bot fights too. It does not make the bots better; it makes those fights cost more. |
| `HandoverRule` | `macrotask`, `distance`, `headsplit` | `macrotask` | When a bot hands the wheel to its controller (`HandoverRule`). `macrotask` is what every shipped arm was measured under; `distance` (an enemy within 150px, kept to 300px) is what gg2-sim's `.gg2p` policies are evaluated under. A **behaviour** switch; nothing under `Mode=scripted`. |
| `HandoverRange` | integer (px) | `150` | How near the nearest opponent must be for that handover; a driving controller keeps the wheel out to the larger of this and 300. Very large (e.g. `100000`) with `HandoverRule=distance` makes the policy drive whenever an opponent is alive, which the duel server uses. A **behaviour** switch. Past about 400px a policy's observation saturates and it has no route, so wider is out of what it was trained on. |
| `PolicySampled` | boolean | `0` | Draw each gg2-sim policy decision from its heads' softmax, with the bot's own random stream, instead of taking the argmax (`--bot-policy-sampled`). Greedy is the cross-checked default; sampled is how the duel nets were trained and scored, and greedy can settle on a still target and never fire. A **behaviour** switch; nothing without a `.gg2p` tier. |
| `BrainTeam` | `both`, `red`, `blue` | `both` | The one team whose bots the brain drives (`--bot-brain-team`); the other team's bots are plain scripted bots. Read every tick, so a bot that changes team changes brain with it (`TeamGatedBot`). `red` is how gg2-sim's matches are played. A **behaviour** switch; nothing under `Mode=scripted`. |
| `HandoverBubbles` | boolean | `0` | Bots put up a chat bubble whenever their brain changes hands: `z1` a policy took the wheel, `z3` the search did, `z2` the scripted layer took it back. For debugging; every bubble is a broadcast, and it changes nothing a bot decides. `docs/BOTS.md`, "watching a handover from inside the game". |
| `AdaptiveBudget` | boolean | `1` | Let the server walk the controller budget down when ticks crowd their deadline. `Arbiter` stays an upper bound. |
| `Mode` | scripted / search / policy / tiered | `scripted` | Which of the four shipped brains runs. `search` is the better bot and the far more expensive one. |
| `Policy` | list | *(empty)* | The gg2-sim `.gg2p` nets `policy` and `tiered` load, **one tier each**; the shipped bundle is nine, one per class (`checkpoints/README.md`). Any other file is refused at the settings check. A net carries no stamp and names no classes: it drives only the classes `ClassPolicy` assigns it (`docs/BOTS.md`, "gg2-sim policies"). Comma-separated, so a checkpoint path cannot contain a comma. |
| `Arbiter` | integer | *(empty)* | **Microseconds** of bot-controller work per tick, against a 33,300 µs tick. Empty lets the mode pick its default. |
| `SearchThreads` | integer | *(cores/3, 1..8)* | Threads the rollout search uses. Threads change **no** decision, so this is a pure cost knob. |

⚠️ **`Mode`, `Policy`, `ClassPolicy`, `Arbiter` and `SearchThreads` are
INPUTS to the brain, not the brain.** `BotBrain.Build` is the
one place they become a `BotBrainSpec`, shared by the config file and the command
line. They exist as settings because a `BotBrainSpec` holds a loaded
`SimPolicyNetwork` — megabytes of weights with no textual form — so it cannot
round-trip and the inputs can.

`[BotClasslimits]` and `ClassPolicy` answer different questions and must stay
separate: the limits say which classes bots **play**, `ClassPolicy` says who
**drives** one that is played. A server drawing classes no tier drives runs a
mix of the hybrid arm and the all-scripted one; to make every bot the
configuration under test, set the classes nothing drives to 0.

### `[BotClasslimits]`

Nine keys — `Scout`, `Pyro`, `Soldier`, `Heavy`, `Demoman`, `Medic`, `Engineer`,
`Spy`, `Sniper` — each an integer: how many **bots** of that class one team may
hold. The encoding is `[Classlimits]`' own: **0 keeps bots off the class, 255
is unlimited**. Every key defaults to `255` except **`Spy`, which defaults to
`0`** — the population manager's own roster, so a server that sets nothing draws
what it always drew (`BotPopulation.DefaultClassLimits`). There is no `Quote`
key; no bot is drawn as one. `--bot-class-limit` is the flag: one number for all
nine, or `class=n` pairs applied over whatever is already configured.

- **Bots only, and in addition to `[Classlimits]`.** Humans neither count
  towards these nor are limited by them; a bot is still subject to the server's
  own limits, which decide what it is granted.
- **Checked when a bot joins and at every death.** A joining bot's class is
  drawn from those with room on its team; when none has room, no bot is added
  and the fill stops short, and limits that cannot seat the fill at all are
  warned about at startup. A bot that dies comes back as a random class with
  room under both these and `[Classlimits]`, and is renamed `NamePrefix` plus
  the class. A living bot moved over a limit by a rotation or the autobalance
  keeps its class until it dies (`docs/TRAPS-BOTS.md`, "Bot class limits").

### `[Web]`

`docs/WEBADMIN.md`. This is the one surface in the project meant to be reachable
from the network.

| Key | Type | Default | |
|---|---|---|---|
| `Port` | integer | `8191` | The admin panel's port. ⚠️ **Naming it at all turns the panel ON**; `0` asks the OS for a free one, which is why presence rather than non-zero is the switch. |
| `Enabled` | boolean | `0` | Host the panel. Written explicitly so turning it **off** is expressible; a `Port` key on its own turns it on. |
| `Bind` | text | `127.0.0.1` | Where the panel listens. ⛔ `0.0.0.0` is what makes it remote, and remote without a certificate sends the admin password in the clear. |
| `Password` | secret | *(empty)* | ⛔ **Empty does not mean no password** — it means one is generated and printed to the console at startup. A generated one lasts as long as the process: a restart from the panel carries it, so pressing restart does not lock you out. |
| `Certificate` | path | *(empty)* | A PKCS#12 certificate to serve HTTPS with. Empty is plain HTTP. `auto` makes a self-signed one on first start, writes it beside the config file as `web-cert.pfx` and reuses it, so its fingerprint stays the same for a fleet hub to pin. |
| `CertificatePassword` | secret | *(empty)* | That certificate's password, if it has one. Refused with `auto`. |
| `AllowFrom` | text | *(empty)* | Addresses and CIDR blocks allowed to connect at all, comma-separated. Empty allows anyone; loopback is always allowed. Checked when the connection is accepted, before TLS or any HTTP. On a fleet member this is normally the hub's address. |
| `FleetMember` | boolean | `0` | Accept a fleet hub's token (`Authorization: Bearer`) as well as the password. Needs a `Certificate`. The token is generated on first start into `fleet-member.token` beside the config file, never in it, and the pairing code for the hub is printed at every start. `docs/WEBADMIN.md`, "Fleet members". |
| `FleetDirectory` | path | *(empty)* | Make this panel a fleet hub. The directory, relative to the config file's, holds `members.ini` with every member's token. Empty is not a hub. A hub bound beyond loopback with no `Certificate` is refused, because its password opens every member. `docs/WEBADMIN.md`, "The fleet hub". |

### `[Runtime]`

| Key | Type | Default | |
|---|---|---|---|
| `Speed` | decimal | `1` | Tick pacing. `1` is real time, `max` is unpaced. ⛔ **Must stay at 1 on a server anybody is connected to** — clients pace themselves at 30 Hz and re-simulate between updates, so an unpaced server desynchronises every one at once. |
| `IdleWhenEmpty` | boolean | `1` | Drop to 2 Hz when nobody is connected and no bots are running. ⚠️ The round clock slows with it, so an idle server's round is not wall-clock `Time Limit`. |
| `ExitWhenEmptySeconds` | integer | `0` | Stop, exit code 0, after this many seconds with nobody connected, counted from startup too. Bots do not count; a spectator and a half-finished join do. For private rooms (`docs/ROOMS.md`). `0` runs for ever. Never on a fleet server whose unit says `Restart=always`. |
| `TickStats` | boolean | `0` | Log a histogram of tick intervals every 30 seconds. Pacing only, so it is safe with players on. |
| `ControlPort` | integer | `0` | The loopback-only JSON control port. Naming it at all turns it **on**; `0` asks the OS for a free one. It executes commands against a live game. |
| `ControlPortEnabled` | boolean | `0` | Open the control port at all. A `ControlPort` key on its own turns it on; this is how it is turned back off. |

