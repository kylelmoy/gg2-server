# The web admin panel — `gg2-server --web`

A browser console for a running server: who is on it, where they are, what the
round is doing, six graphs of how the process is coping, the operator controls —
chat, bot count, map change, teleport, kick, ban — and a settings
page that edits the config file, with a restart button that applies it.

It is the **only** surface in this project that is meant to be reachable from
the network, and everything below follows from that.

```powershell
# local only, password printed at startup
dotnet run --project src/GG2.Host -- --map ctf_truefort --bots 8 --web 8191

# reachable from the network, over TLS, with a password you chose
gg2-server --web 8191 --web-bind 0.0.0.0 --web-password "$PANEL_PASSWORD" `
           --web-tls cert.pfx --web-tls-password "$CERT_PASSWORD"
```

## The flags

| Flag | Default | |
|---|---|---|
| `--web <n>` | off | Host the panel on port `n`. `0` asks the OS for a free port. **The only flag that turns it on.** |
| `--web-bind <addr>` | `127.0.0.1` | Where to listen. `0.0.0.0` is what makes it remote. |
| `--web-password <text>` | generated | The admin password. There is no way to configure it away. |
| `--web-tls <path>` | none | A PKCS#12 (`.pfx`) certificate; serve HTTPS. |
| `--web-tls-password <text>` | none | That certificate's password. |

**The four settings flags do not turn the panel on, and one given without
`--web` is refused rather than ignored** — the rule `--policy` without a mode
that consults it already follows. `CliCoverageTests` gates this.

The same settings live in a config file's `[Web]` section (`--config`), which is
where an admin password belongs rather than in a process listing:

```ini
[Web]
Port=8191
Bind=0.0.0.0
Password=a long one
Certificate=cert.pfx
CertificatePassword=
```

**`Port` being present is what enables it**, not `Port` being non-zero: `0`
means "ask the OS for a free port" everywhere else in this project and keeps
meaning that here.

## The threading rule, which is the whole design

The simulation is single-threaded and `WorldState` has no locks. So **nothing on
a socket thread touches `GameServer`**:

- a request that **changes** anything is put on a queue and executed by
  `WebAdminServer.Pump`, which `GameServer.Tick` calls — on the tick thread,
  between two ticks, exactly the guarantee `ControlPort` gets by being serviced
  inline;
- a request that only **reads** is answered from a snapshot: one immutable JSON
  string the tick thread republishes, read with `Volatile.Read`.

**The control port's own approach is wrong here, and that is why the two
differ.** A control-port client is a tool on loopback; a panel client is across a
network and may be slow, hostile, or a phone that went into a tunnel mid-reply.
Writing to it from inside `Tick` would let it stall the simulation for every
player on the server by simply stopping reading. `ControlPort` binds
`IPAddress.Loopback` so that can never happen to it; the panel cannot make that
promise, so it does not run there.

**A queued command times out after 5 seconds** and says the server may have
stopped ticking, rather than hanging the browser.

### What it costs

**Release, 24 cores, `ctf_truefort`, against a 33,333 us budget. Re-measure
before repeating any of these** — a number quoted without its build is how a
stale figure can sit in multiple documents unnoticed.

| | Cost | Of a 33.3ms tick |
|---|---|---|
| **`--web` off** | one `null` check per tick | — |
| Tick timing in `Run()` — **NOT gated by `--web`** | 46 ns/tick | 0.00014% |
| `--web` on, nobody watching: the sampler's early-out | 3.5 ns/tick | 0.00001% |
| `--web` on, nobody watching: the 1 Hz sample | 661 ns/**second** | 0.0020% of one tick |
| **A browser watching**: one snapshot, 13 on the roster | 10.6 us (p95 24) | 0.032% |
| **A browser watching**: one snapshot, 48 on the roster | 33.5 us (p95 67) | 0.101% |

**The tick-timing accumulators are the one part that is NOT behind `--web`.**
Two `Stopwatch.Elapsed` reads and some field arithmetic run in `GameServer.Run`
on every tick of every server, panel or no panel — a real unconditional cost,
left ungated because a branch would make the paced loop two loops for a saving
smaller than the branch.

**Snapshot cost is per SNAPSHOT, not per watcher.** The string is built once by
the tick thread and published; every stream then writes that same string from its
own thread. Ten people with the panel open cost the tick thread exactly what one
does. What does scale it is the roster — it is a linear walk — so the 48-player
figure is the one to plan against, and at 5 Hz that is **0.17 ms of tick thread
per second**, or one tick in six costing 34 us more than the other five.

**`GET /api/state` is the exception**: a one-shot read is executed on the tick
thread through the command queue, so a script polling it at 30 Hz would cost what
30 snapshots a second cost. The panel does not — it reads the stream.

**Memory**: about 100 KB in total — 300 perf samples (~31 KB), 500 log lines
(~50 KB) and the current snapshot string (3.5 KB at 13 players, 11 KB at 48).

**End-to-end**: `--web` on versus off, both unpaced with 12 bots, run
simultaneously on one machine and alternated between slots: **-0.19% +/- 2.79%**
— no effect resolvable, with the interval about a hundred times looser than the
20 ns/tick the table predicts. **Measure arms simultaneously, not
sequentially** — sequential runs are vulnerable to the machine's load moving
between runs, which can produce a confident-looking cost number smaller than the
spread within a single arm.

The same instrument *can* see a watcher at unpaced speeds: 16.1% of unpaced
throughput. That is an upper bound, not the real cost — at 65,000 ticks/s the
panel is serialising ten thousand events a second and allocating 40 MB/s, neither
of which happens at 30 Hz. Take the direct per-snapshot number above.

**An idle server ticks at 2 Hz** (`--no-idle-when-empty` turns that off), so
with nobody connected the panel updates twice a second. It says `idle` in the
state tile rather than looking stuck.

`WebAdminTests.NobodyWatchingMeansNoSnapshots` gates the structural half of this.
**What would show the table has gone stale**: a snapshot that stops being
O(roster), a sampler moved off the 1 Hz cadence, or anything new added to
`PerfHistory.Sample`'s once-a-second body.

## Authentication

`AdminSessions` is the whole of it, and the parts that matter are:

- **There is no unauthenticated configuration.** An empty `--web-password`
  means "generate one and print it at startup", not "no password". The generated
  one is 20 characters from an alphabet with no `O`/`0` and no `I`/`l`/`1`,
  because it gets typed by hand off a log line.
- **The password is never stored in the clear** in the comparison path — a
  per-process salted SHA-256, compared with
  `CryptographicOperations.FixedTimeEquals`. The panel is reachable from the
  network by design, so the comparison is one an attacker can time.
- **A session is a random 256-bit token in an `HttpOnly; SameSite=Strict` cookie**
  (`Secure` too, under TLS). It expires 4 hours idle **and** 1 day absolutely —
  both, because an idle timeout alone means a tab left polling is authenticated
  for ever. At most 32 at once, oldest evicted.
- **Every mutating request must echo a CSRF token** issued at sign-in, in
  `X-CSRF-Token`. `SameSite=Strict` is the first defence; this is the second.
  `/api/session` re-issues it to a browser that already holds a valid cookie,
  so a reload does not mean signing in again — safe there and only there,
  because no CORS headers are sent and a cross-origin page cannot read the reply.
- **Five wrong passwords from an address start a lockout that doubles** to a
  15-minute ceiling, and every failure costs 250 ms whether or not it locks —
  which also flattens the timing difference between "wrong password" and "no such
  session". A correct password clears the record, so an operator who fumbled it
  is not locked out after getting it right.

**`--web-bind` beyond loopback without `--web-tls` is warned about at every
startup**, in terms of what an attacker gets rather than as advice: the admin
password and every session cookie cross the network in the clear. The three
answers are `--web-tls`, a reverse proxy that terminates TLS, or binding
`127.0.0.1` and reaching it over an SSH tunnel:

```bash
ssh -N -L 8191:127.0.0.1:8191 you@your-server   # then open http://127.0.0.1:8191/
```

## The HTTP server

Hand-written, in `Web/HttpExchange.cs`. **Not `HttpListener`, and the reason is
Windows**: it can only bind a prefix other than `localhost` if an administrator
has run `netsh http add urlacl`, so the one thing this feature exists for —
reaching the panel from another machine — would have needed an elevated command
nobody expects to run to host a web page. ASP.NET Core would solve it and would
be this project's first NuGet dependency, for a page with eight endpoints.

**It is a subset on purpose and the omissions are the security argument.** No
chunked request bodies (refused, not ignored — silently treating one as absent is
a request-smuggling primitive), no pipelining, no ranges, no upgrades. Everything
is bounded before it is allocated: 16 KB of head, 64 KB of body, 64 header lines,
64 connections, 200 requests per connection, a 60-second idle timeout.

Every reply carries `Content-Security-Policy: default-src 'self'` **with no
`unsafe-inline`**, which is why the page's CSS and JavaScript are separate routes
rather than inline: the panel renders player-chosen names into the DOM, and that
header is the difference between a name that renders as text and one that runs.

### Endpoints

All JSON, all under the session cookie except the first four.

| | | |
|---|---|---|
| `GET /`, `/app.css`, `/common.js`, `/analytics.js`, `/app.js`, `/favicon.svg` | — | the page, embedded in the assembly |
| `GET /world.json` | — | the analytics map's coastlines and markers, embedded. **Served without a session**, like the page: it is the same public-domain geometry for every server in the world and says nothing about this one |
| `GET /api/session` | — | signed in?, TLS?, server name, CSRF token if signed in |
| `POST /api/login` | `{password}` | sets the cookie, returns the CSRF token |
| `POST /api/logout` | — | clears it |
| `GET /api/state` | — | one fresh state object, run on the tick thread |
| `GET /api/events` | — | server-sent events: `state` snapshots, `log` lines and `perf` samples |
| `POST /api/command` | a control-port command | freeze, resume, step, speed, bots, bots-suspend, map, teleport, kick, command, say, ban, unban, bans, analytics, config-set, restart |
| `GET /api/config` | — | every setting, its value **in the config file**, and which are pinned or pending |
| `GET /api/analytics` | — | who has played here, for how long and from where — `docs/ANALYTICS.md` |
| `GET /api/maps` | — | what this server could change to, plus its rotation |
| `GET /api/map.png?name=` | — | a map's PNG, for the live map |
| `GET /api/log?after=` | — | the scrollback, by sequence number |
| `GET /api/perf?after=` | — | the performance history, by sequence number |
| `GET /api/system` | — | the host: OS, CPU, memory, runtime, paths |

**`/api/command` takes the control port's own commands, unchanged.** Both
transports go through `ControlCommands`, which is the one place any of them is
implemented — see below.

**Server-sent events rather than WebSockets**: everything flows one way, so
SSE needs no handshake, no framing and no ping protocol, over a transport that
reconnects itself. About forty lines against several hundred.

## Two transports, one dispatch

`ControlCommands` holds every operator verb and every reply. `ControlPort` and
`WebAdminServer` are transport and nothing else — the day they each grow their
own copy of "what does `step` mean" is the day one of them can do something the
other cannot. This is CLAUDE.md's "three places" rule one level up.

**A verb added to `ControlCommands` needs a row in
`WebAdminTests.EveryControlPortVerbIsReachableFromThePanel`.** What that test
establishes is *reachability*, not equivalence: it asserts the reply is not
`unknown cmd`. There is a negative control beside it so the assertion is known to
be able to fail at all.

`kick` is the one verb the panel added, and the control port gained it for free.
**It sends `KickReason.Operator` (255), which stock GG2 does not define** —
measured rather than hoped: `ClientBeginStep.gml:433-437` compares the byte
against its three known reasons and falls into `else kickReason = ""`, showing
"You have been kicked from the server." with no cause, which is exactly what an
operator kick means. **Kicking a bot is refused by name**, because a bot has no
connection to close and the population manager would put it straight back; the
message names the bot count as the thing to change instead.

**The roster's Ban button bans the player's address permanently**, through `ban`
by slot, after a confirmation that says so and says that everyone on that
address is banned with them. `BanList` has the case against reading an address
as a person.

**Freeze, step and speed are verbs without panel controls.** They exist for
debugging and each desynchronises or stalls every connected client, so the page
does not offer them; the state tile still shows `frozen` if the control port has
frozen the server.

### Chat

The Chat card shows what players say and sends a line as the server's own slot-0
spectator, through `say`. **Stock GG2 has no chat**, so where the line goes
depends on the plugins, and the card's hint says which:

| Loaded | What a player sees |
|---|---|
| `chat` or `chat_v2_pr` | a chat line from slot 0, named and coloured as the reference's host's own lines are |
| neither (the default) | a `MESSAGE_STRING`: the on-screen notice every stock client draws, reading `<slot 0's name>: <text>`, which replaces the previous notice rather than scrolling |

A line is at most 255 characters (the `ubyte` length prefix) and is refused, not
cut, above that. The history is `ChatHistory`, 200 lines held only while the
panel is enabled, fed by the chat plugins with what players said (team chat
marked, commands left out) and by `say`; it goes to the browser as the SSE
`chat` event. A leading `/` in the operator's line is text, never a command.

## The page

`Web/Assets/`, five files, embedded in `GG2.Server.dll`. Hand-written HTML, CSS
and JavaScript that a browser runs as-is: no build step, no framework, no CDN —
a LAN server's panel has to work on a machine with no route to the internet.
About 120 KB in total, of which 81 KB is `world.json` — the analytics map's
geometry, generated by `tools/gen-geo-assets.js` and checked in.

⚠️ **No build step means nothing checks the JavaScript**, and two mistakes are
both mechanical and both silent: a second top-level declaration of an existing
name replaces the first, and a `$('id')` for an element that is not in the HTML
returns null. `WebAssetTests` catches those two. Everything else about this page
is verified by opening it.

**Embedded rather than served off disk**, so a deployment cannot end up with a
working binary and a missing page, and so there is no directory to traverse: the
routes name the six files by hand.

**Three scripts in one global scope**, loaded in order: `common.js` (the element
lookup, cell builders, theme tokens, number formats), `analytics.js` (the
Analytics view, which draws an object and never fetches one), then `app.js`.
The view is its own file because `scripts/fleet-analytics.sh` inlines it into
the fleet page too. `WebAssetTests` scans the files together for a name declared
twice, and checks that every id the fleet page's scripts ask for is inside the
analytics sheet that page cuts out of `index.html`.

Two rules hold `app.js` together: **state arrives on one event stream and is
applied by `render()`** — nothing else writes to the DOM from a timer, so the
panel can never show two halves of two different ticks — and **everything that
changes the server goes through `send()`**, which posts one control command and
shows what came back.

### The look: HeroUI, imitated rather than imported

The panel wears **HeroUI v3's design language with none of HeroUI's code.** The
token names, the OKLCH surface ladder, the `color-mix()` derivations and the
radius scale in `app.css` are HeroUI's own, read out of `@heroui/styles@3.2.4`
(`dist/themes/default/variables.css` and `dist/themes/shared/theme.css`) and
re-hued to the theme:

```
lightness 0.77   chroma 0.13   hue 302.636371990466   radius small   base 0.0043
```

**Why tokens and not a vendored stylesheet.** HeroUI ships ~403 KB of CSS for
a component set this page does not have, needs a directory route so its chunks
can lazy-load — which puts back the path-traversal surface `WebAssets`
deliberately removed — and would be this repo's first runtime dependency. What
actually makes it *look* like HeroUI is the token system: the surface ladder,
the hairline borders, the soft-variant fills, the flat dark cards, the focus
ring. That is about ninety declarations.

**`oklch()` and `color-mix()` are used directly, as HeroUI does**, with no hex
fallbacks — both have been baseline since 2023, and a browser without them would
render the page unstyled rather than wrongly. The panel is an operator console;
that is an acceptable floor.

Three things that had to be derived rather than copied:

- **`base` is the neutral CHROMA, not a lightness.** Stock HeroUI dark builds
  its greys at chroma 0.004-0.006 around hue 286, and the builder's hue slider
  moves the whole theme — so the greys here are the same lightness ladder at
  chroma 0.0043, hue 302.64. A grey that is faintly the accent's colour.
- **A light accent needs DARK text on it.** This accent is L 0.77, so `--snow`
  over it is **2.13:1** and `--eclipse` is **8.16:1** — measured, and the one
  thing a light-accent theme gets wrong. `--accent-foreground` is the dark one.
- **`--radius: 0.375rem` is an inference**, not a value read off the builder
  (which is a client-side app). HeroUI's default is `0.5rem` and "small" is a
  step below. Every other radius is a `calc()` off that one line, so it is one
  line to correct if that turns out wrong.

**The chart series are NOT theme tokens and do not move with the accent.**
They are the data-viz palette's dark steps, validated against `--surface`
(`#19181a`): worst all-pairs CVD ΔE 9.4, worst normal-vision ΔE 24.6, all ≥ 3:1.
Violet was kept over blue after measuring blue worse on this surface (tritan ΔE
4.0 against 9.4); the two never appear as peers and every series is keyed in a
legend. The one exception is **late ticks**, which uses the theme's `--warning`:
that series means "something is wrong", the panel says that in one colour
everywhere, and a single-series chart has no pair check to fail.

**`<canvas>` cannot read a custom property**, so `app.js` resolves the ones it
draws with through `token()` and caches them. That is what keeps the live map and
the charts on the same palette as everything else; the alternative — hex repeated
in the JS — is exactly how a theme drifts.

### Things the page had to be told, that are not obvious

- **World pixels are art pixels × 6** (`GameConstants.CellSize`, and what
  `LevelMap.Width` applies), so the map PNG scaled to the world's width *is* the
  coordinate system. A dot off by a constant factor is that number.
- **Markers are sized in CSS pixels and converted back into world units.**
  `ctf_truefort` is 5,346 world pixels wide, so a 9-unit dot on a canvas scaled
  to fit is **one screen pixel**.
- **Cap the map card's WIDTH, never its height.** A canvas stretches its
  bitmap to whatever box CSS gives it, so a `max-height` distorts the map and
  puts every dot in the wrong place. `cp_dirtbowl` is three areas stacked
  vertically and is the case that needs the cap at all.
- **`IntelState.OnField` does not mean "dropped"** — it means the briefcase
  exists rather than being carried, which is also true of one sitting at home.
  The state sends the home position as well so the two can be told apart.
- **A map change is a ten-second countdown, not an event.** The panel counts
  it down in a banner rather than a toast, which would look like a button that
  did nothing.
- **Below `--bots-min-humans` the population target is ZERO rather than the
  fill**, so a bot count set on an empty server does nothing. The panel says
  which it is, because the default is 1 and the first thing anybody tries is
  setting bots on an empty server.
- **A player may have turned the bots off.** `/bots off` holds the population
  at zero for `[Bots] SuspendMinutes`, and nothing else on the page would say
  so. The same warning line states it, and saving a bot count ends it — as does
  the `bots-suspend` verb with `minutes` 0. `docs/PLUGINS.md`, "the bot
  suspension".
- **A player's name is whatever they typed into their own client.** It reaches
  the DOM through `textContent`, never `innerHTML`, and the CSP is the second
  net.

## Which brain the bots run

The Bots card names the arm and every tier in it, and each bot's row in the
roster carries the brain that will drive it. Three separate questions, and the
card answers all three because no one of them is enough on its own:

| | Where | What it is |
|---|---|---|
| What was **configured** | `bots.mode`, `bots.brain` | The mode as given, and `BotBrainSpec.Name` — the same string `tools/bench` prints in a result table, so a bot watched here and a number read off the bench name the same arm. The mode alone is not enough: a reconcile that leaves no controller clears the brain, and a policy server whose bots are all scripted still reports mode `policy`. |
| What each tier is **allowed** to drive | `bots.tiers[].classes` | Read off a live controller through `IMicroController.Drives`, never re-derived — that interface is the one place a tier's class set is decided, narrowed by `[Bots] ClassPolicy`. A server with no bots on it therefore sends no class sets, which is honest: with nothing built there is nothing to ask. |
| What a brain has **actually done** | the share beside each tier | Ticks a controller drove over ticks it was polled on, summed over the bots it drives. It is the only one of the three that says the brain is consulted at all — a tier can be loaded, named on this card and never admitted by the arbiter. |

**A slot's `brain` is the tier that will drive it, and `driving` is whether one
held the wheel on the tick the snapshot was taken.** A controller only drives
inside the engagement window, so the badge is unlit and the share is a few per
cent on a perfectly healthy server. Nothing is shown as "0 driving": a quiet
instant must not read as a broken brain. A share of exactly zero is the one that
means something.

**Two tiers claiming one class shows up here as a tier with no bots.** The
roster reports the most capable eligible tier — `HybridBot.TierFor`, the same
walk the arbiter's input comes from — so an overlapped tier is visibly driving
nothing while the tier above it takes its classes. `BotBrain.Build` warns about
this at startup; the card is where it stays visible afterwards.

⛔ **`bots.engage` is the gate every tier is behind and it is stated as
configuration, never as a prediction.** A live server hands a controller the
wheel for a HUMAN enemy only (`--bot-engages-bots` opens it to bot fights), so an
operator reading a low share wants to know that first. **It states the
configuration and never predicts that the bots will not drive** — the share is
the measurement, and it is the one to believe. What the gate rests on is
`PlayerState.IsBot` surviving a respawn: `docs/TRAPS-SIMULATION.md`, "a respawn
replaces the character and leaves the player alone".

## The settings page

**Settings** in the top bar opens a modal over the dashboard: every key
`docs/CONFIG.md` documents, grouped by section, filterable, with the help text
beside each field. It renders `ServerSettings.All`, so **adding a setting needs
no change here at all** — a new row appears on the page the day it is added to
the table.

**It edits the FILE, not the running server, and that is a decision rather
than a gap.** Every subsystem reads its options exactly once, at
`GameServer.Start`: the listener has bound its port, `BanList` has opened its
file, the plugin list has been served to everyone connected, `WebAdminServer` has
hashed its own password. Writing new values into a running server's options
object would apply some of them and not others with **nothing telling an operator
which**. So a save writes the file and says plainly that the restart is what
applies it. The three things that *can* change on a live server — the map, the
bot count, the speed — have their own `ControlCommands` verbs. The map and bot
count are panel controls; speed is reachable only through `POST /api/command`
and the control port, because anything but 1x desynchronises every connected
client.

**A file pushed by `scripts/deploy.sh --config` is marked as managed** (the
`config` verb's `managed`, `editedSincePush` and `pushWaiting`). Saving still
works, and is the right tool for a change that cannot wait, but the next push
refuses this server until the change is copied into `deploy/config` or the push
is forced. `docs/CONFIG.md`, "Many servers, one configuration".

### The four things it had to be told

- **A secret is never sent to the browser.** Five settings are marked
  `Secret` — the game password, the admin password, the certificate's — and each
  is reported as `"set": true|false` with **no `value` field at all**. Two of
  them are this panel's own credentials, so an endpoint that echoed them would
  turn one stolen session cookie into the credential itself. The field is
  write-only: it renders empty with a placeholder saying whether one is set, and
  **an empty secret field is not submitted** — or opening the page and
  pressing save would erase the admin password.
- **A flag beats the file, so a pinned setting can be saved correctly and do
  nothing.** `overridden` names those and the field is badged **pinned**. It
  stays editable, because saving it is still the right thing to do before the
  flag is removed. `ServerOptions.CommandLineOverrides` computes the list by
  diffing what `Parse` built against what the file alone would produce — a diff
  rather than a record of which flags were seen, because `--rotation` with no
  `--map` silently empties `Map` and no per-flag record would notice.
- **A saved setting the server has not picked up yet is badged `restart
  pending`.** **`pending` and `overridden` are mutually exclusive by
  construction**: a flag makes the file and the running value differ *for ever*
  and a restart will not reconcile them, so marking one "restart pending" would
  leave a badge that no action clears next to the badge that actually explains
  it.
- **The values shown are the FILE's, not the running server's.** Measured in a
  browser, not reasoned about: with the running server's values, a successful
  save re-read the page and showed the operator the **old** value back, with a
  "saved" toast beside it — every save looked discarded. What the fields edit is
  what the next start will use.

**Validated before anything is written**, so a rejected value leaves the file
exactly as it was. That is what makes "Save & restart" safe to offer: a config
that would stop the server coming back up never reaches the disk.

**This does file I/O on the tick thread**, as every `ControlCommands` verb
runs there. It is a few kilobytes, once, when an operator presses a button —
against a 33.3 ms budget and beside a map load that does far more. It must not
become something that happens per tick.

## The restart button

**Restart** in the top bar, and **Save & restart** in the settings footer, both
reach `ControlCommands`'s `restart` verb. `GameServer.RequestRestart` ends
`Run()`; `Program.Main` disposes the server, re-parses the command line — which
re-reads `--config` — and builds a new one **in the same process**.

**A restart, not a reload.** The whole thing is torn down and rebuilt, which
is the one operation whose result an operator can predict from the config file
alone.

- **Every client is disconnected and the round is lost.** The panel says so
  before it asks.
- **`Dispose` still runs**, so the lobby listing comes down, an in-flight
  `--record` segment is closed into a readable trace, and every client gets a
  clean socket shutdown rather than a hang until TCP gives up.
- **The flag/file precedence survives it.** The *whole* command line is
  re-applied over the file, so a flag still beats the file after a restart — a
  restart that quietly became a different server would be worse than no restart.
- **It resumes a frozen server first.** A frozen server is exactly the state
  an operator reaches for the restart button in, and "works unless you froze it"
  is a gap nobody finds until they need it.
- **A generated admin password is CARRIED across it.** `--web` with no
  `--web-password` prints a generated one to the **console**, which an operator
  who just pressed restart in a browser on another machine cannot see — so
  without the carry the restart button would lock out whoever used it. It is
  carried only within this process, only while no password is configured, and it
  is logged when it happens. A new process generates a new one.
- **The admin SESSION does not survive.** The new `AdminSessions` has a fresh
  salt, so the browser returns to the login card and signs in again with the same
  password. The event stream drops and `EventSource` reconnects on its own; the
  panel shows "reconnecting" in the meantime.
- **A `TickFaultException` is NOT restarted here**, however much the loop
  looks like it would. A tick loop that failed repeatedly is exactly the state a
  fresh *process* clears and a fresh `GameServer` may not, and looping would turn
  a supervisor's restart budget into an unsupervised spin. It still exits 3.
- **If the re-parse fails, the process exits 2** and says so: the old server
  has already stopped and there is nothing to fall back to. `config-set`
  validates before it writes, so reaching that state means the file was changed
  some other way between the save and the restart.

## The performance graphs

Six small multiples on one time axis — **tick time**, **CPU**, **memory**, **late
ticks**, **bytes sent** and **load** (live characters, bots, humans) — over the
last five minutes at 1 Hz, with a synchronised crosshair so a hitch in one can be
read against every other at the same instant. That is the whole reason they are
six frames rather than six numbers: "the tick spiked, and *at the same second*
the character count jumped and gen-2 collected" is an answer, and six separate
readouts are not.

`PerfHistory` samples on the tick thread once a second. **It samples whether
or not anybody is watching, which is the opposite of the state snapshot and is
deliberate**: an operator opens the panel *because* something already happened,
so a history that started when they arrived would answer a question nobody has.
The per-tick cost of that is one `Environment.TickCount64` comparison — 3.5 ns,
measured — and the syscalls are at 1 Hz and total 661 ns. Both halves of the
asymmetry are gated: `NobodyWatchingMeansNoSnapshots` and
`ThePerformanceHistoryIsCollectedWithNobodyWatching`. The full table is above.

**The once-a-second body allocates nothing.** The sample is a struct written
into a preallocated ring, and the bot count is taken from the same roster walk as
the character count rather than from `GameServer.BotSlots`, which builds a
`List<int>` on every access.

**The samples go over the wire as arrays of bare numbers** against a `fields`
list, because 300 samples × 15 named fields is 60 KB of repeated key names for
5 KB of data. `EveryPerformanceRowMatchesTheDeclaredFields` is what stops the
list and the rows drifting apart — a column added to one and not the other
silently shifts every series after it onto the wrong chart.

### CPU is measured over five seconds, not one, and that is a measurement fact

**Windows accounts process CPU at the 15.625 ms clock interrupt**, so a
one-second delta of `Process.TotalProcessorTime` can only ever be a multiple of
**1.56%**. With ten bots (about 0.4% of a core), a per-second series is an
alternating `0.00 / 1.56 / 0.00` sawtooth that reads as spiking. Over a
five-second trailing window the same quantum is 0.31%, below what anybody reads
off a graph. The series therefore lags a step change by up to five seconds, and
the chart's caption says `5s`.

**It is a percentage of ONE core, not of the machine.** The tick loop is a
single thread, so "60% of one core" is the number that says how close it is to
being the bottleneck; dividing by the core count would hide that behind a
comfortable-looking 7%. It can exceed 100 — GC and the panel's own threads are in
it.

### The rest of what the graphs had to be told

- **The tick budget joins the y-scale only once the data is within a quarter
  of it.** Forcing 33.3 ms into a chart whose real values are 0.1-0.5 ms flattens
  every one of them onto the baseline: the reference line destroys the resolution
  of the thing it is a reference for. So a healthy server's chart zooms into its
  own range, and the budget line appears — with its label — exactly when a tick
  starts getting near it.
- **`_lateTicks` is incremented outside the `&&` the warning hangs off.**
  `LateTickMonitor` rate-limits itself, returning null for most late ticks so the
  log is not a flood; counting only when it produced a line would have
  under-reported by exactly the amount the rate limiter suppressed, which on a
  struggling server is nearly all of them.
- **A departing client's bytes are retired into a running total before it is
  removed.** Summing the live connections alone would make the figure drop
  whenever somebody disconnected, and a rate derived from it would go negative at
  exactly the moment an operator was looking to see why.
- **`ClientConnection.BytesSent` counts what the socket accepted, not what was
  queued.** A client whose receive window is full has bytes going nowhere, and
  counting those would report bandwidth a congested link is not carrying — which
  is the client the operator is looking at the graph about.
- **The series colors are not the team colors.** Red and blue mean RED and BLU
  everywhere else on this page. The charts use the data-viz palette's aqua,
  orange and violet, validated against this card's own surface rather than
  assumed: worst all-pairs CVD ΔE 9.4, worst normal-vision ΔE 24.6, all ≥ 3:1
  contrast. Late ticks wear the status *warning* step, because that series means
  "something is wrong" rather than "series four", and its title carries the label
  so the color is never alone.
- **One y-axis per chart, ever.** Working set and managed heap share a frame
  because they share a unit; tick time and CPU do not and never will. A second
  y-scale invents a correlation that is not in the data.
- **`.seg-btn` is scoped to its own group.** Each chart's range buttons share the
  class name but have separate handlers, so clicking one does not affect
  another's selection state.

**None of these numbers is a benchmark number.** They are measured over
whatever the server happened to be doing, map loads and GC included, on a machine
that is also running a browser. `tools/bench/` is where a per-tick cost comes
from, and it says which build it was measured in.

## The system section

A collapsed `<details>` under the graphs: OS, CPU model and clock, logical cores,
memory, runtime, GC mode, thread count, and the paths the server is loading from.
Collapsed because it is reference rather than telemetry; its summary line still
reads `Linux 6.8 · 4 cores · 7.8 GB` so the common question is answered without
opening it. `<details>` rather than a JS toggle — it works with no script and
keeps the disclosure semantics a screen reader expects.

**Re-read on every open, not just the first.** Half of it is live: memory,
both uptimes, the thread count, the GC counters.

**The command line and the environment are never in it.** `--web-password` is
in one and may be in the other, and this endpoint is read by a browser.
`SystemNeverLeaksTheCommandLineOrTheEnvironment` looks for the actual password in
the reply, so it fails on any future field that starts including either.

### Where each number comes from, per platform

| | Linux | Windows |
|---|---|---|
| CPU model, clock | `/proc/cpuinfo` — the **current** clock of core 0 | registry `CentralProcessor ` — the **nominal** clock at boot |
| Memory | cgroup v2 `memory.max`/`memory.current` where limited, else `/proc/meminfo` | `GlobalMemoryStatusEx` |
| OS uptime | `/proc/uptime` | `Environment.TickCount64` |

**The two clocks mean different things and the panel says which** — `(now)` or
`(nominal)`. On a shared VPS the Linux figure moves between requests.

**`GC.GetGCMemoryInfo()` is NOT the source for system memory, and that is
measured.** Its fields describe *the last garbage collection*: on a freshly
started server this build reported `MemoryLoadBytes = 0` on a machine with 64 GB
in it. A panel showing "0 bytes in use" is worse than one showing nothing.

**The cgroup is preferred over `/proc/meminfo` where one exists**, because that
is the whole point on a VPS: a container sees the host's `/proc/meminfo` and is
killed at its own limit. The panel reports which of the two it read.

**A Debug build is called out with a badge**, not just reported. The tick
graphs directly above it are twice as slow in Debug — 0.307ms against 0.151ms at
twelve bots — and an operator comparing them against a number from somewhere else
needs to know which they are looking at.

## Running it on Linux

**Audited and cross-compiled, NOT run.** There is no Linux machine in this
project's development environment, so what follows is a source audit plus a
`dotnet publish -c Release -r linux-x64` that builds clean with
warnings-as-errors and the platform-compatibility analyser on — which catches an
unguarded Windows API. **That is not the same as having run it**, and the first
real Linux run is the test this section should be checked against.

- **One file in `src/` is Windows-only**: `TimerResolution` (a `winmm.dll`
  P/Invoke), already guarded by `OperatingSystem.IsWindows()` and a no-op
  elsewhere. `SystemInfo` adds two more guarded branches and no others.
- **No timer raise on Linux, and that changes the late-tick numbers.** On
  Windows the loop raises the timer to 1 ms because a wait rounds up to the
  15.6 ms quantum. Linux has no equivalent knob and its wait granularity is the
  kernel's; the late-tick graph is likely *better* there, and is untested.
- **`nav/` does not travel with a publish and `maps/` does.** The host csproj
  copies `maps/*.png` (458 KB) and nothing copies the 35 MB of nav graphs, so a
  published server on a VPS serves rounds and runs bots that **fight but do not
  walk**. Copy `nav/` beside the binary or set `GG2_NAVCACHE`.
- **The filesystem is case-sensitive.** The panel's map list passes back
  exactly what it read off disk, so that path is safe; a hand-typed
  `CTF_Truefort` in the map box works on Windows and fails on Linux.
- **`--web 80` or `--web 443` needs `CAP_NET_BIND_SERVICE`** or a proxy in
  front. Ports below 1024 are privileged.
- **`--web-tls` with a legacy `.pfx`**: OpenSSL 3, which current distros ship,
  refuses the old RC2-encrypted PKCS#12 files Windows tooling still produces.
  Re-export with `-keypbe AES-256-CBC -certpbe AES-256-CBC` if it will not load.

## What it deliberately does not do

- **No chat, and no way to send one.** A client command the panel could issue and
  a game client could not would break CLAUDE.md's rule that the simulation never
  special-cases anybody. If chat is wanted, it goes through `PlayerCommandKind`
  and the socket switch first, and the panel gets it for free.
- **No LIVE config editing.** The settings page writes the config file and the
  restart button applies it; nothing is pushed into a running server. See "The
  settings page" above for why, and for what the panel is trusted with as a
  result.
- **No user accounts.** One password, one role. An operator console with a
  permission model is a different product.
- **No PERFORMANCE history across a restart.** The six graphs are the last five
  minutes and go with the process; the tick figure is an EMA for a human
  watching, and **it is not a benchmark number** — it is measured over whatever
  the server was doing, map loads included. `tools/bench/` is where a per-tick
  cost comes from. Player history is a different thing and does persist:
  `docs/ANALYTICS.md`.

## KNOWN ISSUE: the accept and serve loops are thread-pool work

`WebAdminServer.Start` runs `AcceptLoopAsync` as `Task.Run`, and each connection
as another. **`Task.Run` on an async method puts the loop AND every
continuation after its first `await` on the thread pool**, so under a saturated
pool an accept — or a response already in flight — can be delayed by seconds.

A full test suite saturates the pool (it grows by roughly a thread a second once
saturated), which is why `WebAdminTests` fails with `HttpClient.Timeout of 20
seconds elapsing`. **Which test fails moves between runs** — it is whichever web
test asks for a connection while the pool has none to give — so it reads as an
unrelated flake rather than as one cause.

**The rate is a property of the machine, not a constant.** Measured 2026-09-12 on
24 cores: **3 full-suite runs out of 3** red at the default pool floor, including
one on a clean `1067363` with no working-tree changes. More cores means more
concurrent xunit collections holding pool threads, so a faster machine fails this
more often, not less.

The fix elsewhere in this repo — `RewardAuthClient`'s pump and `FakeAuthServer`'s
accept — is to give the loop a real `Thread` with synchronous I/O. **Both ends of
a socket conversation have to be off the pool; fixing the end you own leaves the
other end deciding the outcome.**

**Not fixed here, deliberately.** The panel binds loopback by default and is a
diagnostic, so the cost is a slow page under load rather than anything the match
sees — its socket threads touch nothing, and commands are still drained on the
tick thread. **But the failure mode is real for a busy host**, and a panel that
stops answering exactly when the machine is loaded is a panel that is missing when
it is most wanted. If it is taken up, the accept loop is the piece to move first
and the per-connection serve second.

### Why the tests raise the ThreadPool floor

`tests/GG2.Tests/GG2.Tests.csproj` sets `ThreadPoolMinThreads` to 128, which
reaches the test process as `System.Threading.ThreadPool.MinThreads` in its
`runtimeconfig.json`. Below the floor the pool hands out a thread immediately;
above it the thread is what the suite waits one-to-two seconds each for, and the
20s timeout is what runs out.

Measured, full suite, 24 cores:

| Arm | Result | Duration |
|---|---|---|
| default floor | red, 3 of 3 runs | 2m20s |
| `xUnit.MaxParallelThreads=4` | green | 2m52s |
| **`ThreadPoolMinThreads=128`** | **green** | **2m00s** |

The floor is the arm that is both green and fastest, because the delay it removes
was never CPU work — the panel's request is a millisecond of it, waiting on a
thread that already exists.

**This changes the test harness and nothing about the server, so the known issue
above is unaffected.** A deployment cannot starve itself the way the suite does:
the tick loop is a dedicated thread and the sockets are async I/O, and there is no
equivalent of twenty-four CPU-bound test collections in a running server. What
would show the issue is still live — and would not be caught by this suite — is a
busy host whose panel stops answering while the game keeps ticking.

Do not read a green `WebAdminTests` as evidence the panel is quick under load. It
asserts behaviour on an unloaded socket, and the floor is what keeps the load out.

## Gates

| | |
|---|---|
| `WebAdminTests` | over a real socket with a real HTTP client: every route refuses an unauthenticated request, the CSRF header is required, the map route takes a map name and nothing else, a command reaches the tick thread, every control-port verb is reachable, the page is served, the security headers are present, nobody watching means no snapshots but does mean a performance history, every sample row matches the declared fields, and an open stream gets both |
| `BotBrainStateTests` | the brain block and the per-slot fields the Bots card and the roster are built from: a scripted server names the scripted brain and no tier, a policy server names each tier and the classes a live controller says it drives, a class no tier drives falls back by name, the counters that say a brain is consulted are sent, the engagement gate is reported both ways, and two policy tiers are told apart on the bot as well as on the card |
| `WebAssetTests` | the panel's own files: no top-level name declared twice, every literal `$('id')` present in `index.html`, every asset actually embedded, and the analytics map's geometry — both views present, every ring an even number of coordinates, and no ring jumping the antimeridian |
| `AdminSessionTests` | the password, the tokens, both timeouts, the lockout and its ceiling, per-address isolation, the session cap, and the generated password's alphabet — all on an injected clock, so no test sleeps |
| `CliCoverageTests` | every `--web-*` flag moves the option it names; a settings flag with no `--web` is refused; `--web` alone is not remote; a config file alone overrides nothing and a flag beside it overrides exactly what it names |
| `ServerRestartTests` | over a real `GameServer`: a saved setting is in the file and a rebuilt server reads it, `restart` ends `Run` and is not a shutdown, a restart works on a frozen server, saving with no `--config` is refused by name, an invalid value is refused and the file is untouched, saving does not promote the command line's overrides into the file, `config` reports the file and flags what is waiting for a restart, a pinned setting is never also pending, no secret's value is in the reply, a secret can still be written, and a generated admin password is carried while a configured one is not |
| `ConfigFileCoverageTests` | `docs/CONFIG.md`'s gates: every setting is reachable from a file, every row moves the option it names, every setting survives a write and a re-read, and the vacuity control beside it |
