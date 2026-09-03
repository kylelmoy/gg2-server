# The web admin panel — `gg2-server --web`

A browser console for a running server: who is on it, where they are, what the
round is doing, how close the tick is to its deadline, and the operator controls
— freeze, step, speed, bot count, map change, teleport, kick.

Built 2026-09-03. It is the **only** surface in this project that is meant to be
reachable from the network, and everything below follows from that.

```powershell
# local only, password printed at startup
dotnet run --project src/Gg2.Host -- --map ctf_truefort --bots 8 --web 8191

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

⚠️ **The four settings flags do not turn the panel on, and one given without
`--web` is refused rather than ignored** — the rule `--policy` without a mode
that consults it already follows. `--web-password` accepted, dropped on the
floor, and indistinguishable from working until somebody needs the password is
exactly the failure `CliCoverageTests` exists to catch.

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

⚠️ **`Port` being present is what enables it**, not `Port` being non-zero: `0`
means "ask the OS for a free port" everywhere else in this project and keeps
meaning that here.

## ⛔ The threading rule, which is the whole design

The simulation is single-threaded and `WorldState` has no locks. So **nothing on
a socket thread touches `GameServer`**:

- a request that **changes** anything is put on a queue and executed by
  `WebAdminServer.Pump`, which `GameServer.Tick` calls — on the tick thread,
  between two ticks, exactly the guarantee `ControlPort` gets by being serviced
  inline;
- a request that only **reads** is answered from a snapshot: one immutable JSON
  string the tick thread republishes, read with `Volatile.Read`.

⚠️ **The control port's own approach is wrong here, and that is why the two
differ.** A control-port client is a tool on loopback; a panel client is across a
network and may be slow, hostile, or a phone that went into a tunnel mid-reply.
Writing to it from inside `Tick` would let it stall the simulation for every
player on the server by simply stopping reading. `ControlPort` binds
`IPAddress.Loopback` so that can never happen to it; the panel cannot make that
promise, so it does not run there.

⚠️ **A queued command times out after 5 seconds** and says the server may have
stopped ticking, rather than hanging the browser.

### What it costs a server nobody is looking at

**Nothing per tick beyond one comparison.** The snapshot is built only while at
least one browser holds the event stream open, and then only every 6th tick
(5 Hz on a 30 Hz server). `WebAdminTests.NobodyWatchingMeansNoSnapshots` gates
that, because "costs nothing when idle" is precisely the kind of claim CLAUDE.md
records this project having been wrong about before.

⚠️ **An idle server ticks at 2 Hz** (`--no-idle-when-empty` turns that off), so
with nobody connected the panel updates twice a second. It says `idle` in the
speed tile rather than looking stuck.

## Authentication

`AdminSessions` is the whole of it, and the parts that matter are:

- ⛔ **There is no unauthenticated configuration.** An empty `--web-password`
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
  ⚠️ `/api/session` re-issues it to a browser that already holds a valid cookie,
  so a reload does not mean signing in again — safe there and only there,
  because no CORS headers are sent and a cross-origin page cannot read the reply.
- **Five wrong passwords from an address start a lockout that doubles** to a
  15-minute ceiling, and every failure costs 250 ms whether or not it locks —
  which also flattens the timing difference between "wrong password" and "no such
  session". A correct password clears the record, so an operator who fumbled it
  is not locked out after getting it right.

⚠️ **`--web-bind` beyond loopback without `--web-tls` is warned about at every
startup**, in terms of what an attacker gets rather than as advice: the admin
password and every session cookie cross the network in the clear. The three
answers are `--web-tls`, a reverse proxy that terminates TLS, or binding
`127.0.0.1` and reaching it over an SSH tunnel:

```bash
ssh -N -L 8191:127.0.0.1:8191 you@your-server   # then open http://127.0.0.1:8191/
```

## The HTTP server

Hand-written, in `Web/HttpExchange.cs`. ⚠️ **Not `HttpListener`, and the reason is
Windows**: it can only bind a prefix other than `localhost` if an administrator
has run `netsh http add urlacl`, so the one thing this feature exists for —
reaching the panel from another machine — would have needed an elevated command
nobody expects to run to host a web page. ASP.NET Core would solve it and would
be this project's first NuGet dependency, for a page with eight endpoints.

⚠️ **It is a subset on purpose and the omissions are the security argument.** No
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
| `GET /`, `/app.css`, `/app.js`, `/favicon.svg` | — | the page, embedded in the assembly |
| `GET /api/session` | — | signed in?, TLS?, server name, CSRF token if signed in |
| `POST /api/login` | `{password}` | sets the cookie, returns the CSRF token |
| `POST /api/logout` | — | clears it |
| `GET /api/state` | — | one fresh state object, run on the tick thread |
| `GET /api/events` | — | server-sent events: `state` snapshots and `log` lines |
| `POST /api/command` | a control-port command | freeze, resume, step, speed, bots, map, teleport, kick, command |
| `GET /api/maps` | — | what this server could change to, plus its rotation |
| `GET /api/map.png?name=` | — | a map's PNG, for the live map |
| `GET /api/log?after=` | — | the scrollback, by sequence number |

⚠️ **`/api/command` takes the control port's own commands, unchanged.** Both
transports go through `ControlCommands`, which is the one place any of them is
implemented — see below.

⚠️ **Server-sent events rather than WebSockets**: everything flows one way, so
SSE needs no handshake, no framing and no ping protocol, over a transport that
reconnects itself. About forty lines against several hundred.

## ⛔ Two transports, one dispatch

`ControlCommands` holds every operator verb and every reply. `ControlPort` and
`WebAdminServer` are transport and nothing else — the day they each grow their
own copy of "what does `step` mean" is the day one of them can do something the
other cannot. This is CLAUDE.md's "three places" rule one level up.

⚠️ **A verb added to `ControlCommands` needs a row in
`WebAdminTests.EveryControlPortVerbIsReachableFromThePanel`.** What that test
establishes is *reachability*, not equivalence: it asserts the reply is not
`unknown cmd`. If somebody gives the panel its own switch statement it keeps
passing until the two lists differ, which is the point at which it starts
failing — and there is a negative control beside it so the assertion is known to
be able to fail at all.

`kick` is the one verb the panel added, and the control port gained it for free.
⚠️ **It sends `KickReason.Operator` (255), which stock GG2 does not define** —
measured rather than hoped: `ClientBeginStep.gml:433-437` compares the byte
against its three known reasons and falls into `else kickReason = ""`, showing
"You have been kicked from the server." with no cause, which is exactly what an
operator kick means. ⚠️ **Kicking a bot is refused by name**, because a bot has no
connection to close and the population manager would put it straight back; the
message names the bot count as the thing to change instead.

## The page

`Web/Assets/`, four files, embedded in `Gg2.Server.dll`. Hand-written HTML, CSS
and JavaScript that a browser runs as-is: no build step, no framework, no CDN —
a LAN server's panel has to work on a machine with no route to the internet.
About 40 KB in total.

⚠️ **Embedded rather than served off disk**, so a deployment cannot end up with a
working binary and a missing page, and so there is no directory to traverse: the
routes name the four files by hand.

Two rules hold `app.js` together: **state arrives on one event stream and is
applied by `render()`** — nothing else writes to the DOM from a timer, so the
panel can never show two halves of two different ticks — and **everything that
changes the server goes through `send()`**, which posts one control command and
shows what came back.

### Things the page had to be told, that are not obvious

- ⚠️ **World pixels are art pixels × 6** (`GameConstants.CellSize`, and what
  `LevelMap.Width` applies), so the map PNG scaled to the world's width *is* the
  coordinate system. A dot off by a constant factor is that number.
- ⚠️ **Markers are sized in CSS pixels and converted back into world units.**
  `ctf_truefort` is 5,346 world pixels wide, so a 9-unit dot on a canvas scaled
  to fit is **one screen pixel** — which read as "the panel cannot see the
  players".
- ⚠️ **Cap the map card's WIDTH, never its height.** A canvas stretches its
  bitmap to whatever box CSS gives it, so a `max-height` distorts the map and
  puts every dot in the wrong place. `cp_dirtbowl` is three areas stacked
  vertically and is the case that needs the cap at all.
- ⚠️ **`IntelState.OnField` does not mean "dropped"** — it means the briefcase
  exists rather than being carried, which is also true of one sitting at home.
  The state sends the home position as well so the two can be told apart;
  without that the panel reported a CTF emergency on every quiet round.
- ⚠️ **A map change is a ten-second countdown, not an event.** The panel counts
  it down in a banner; a toast would have looked like a button that did nothing.
- ⚠️ **Below `--bots-min-humans` the population target is ZERO rather than the
  fill**, so a bot count set on an empty server does nothing. The panel says
  which it is, because the default is 1 and the first thing anybody tries is
  setting bots on an empty server.
- ⚠️ **A player's name is whatever they typed into their own client.** It reaches
  the DOM through `textContent`, never `innerHTML`, and the CSP is the second
  net.

## What it deliberately does not do

- **No chat, and no way to send one.** A client command the panel could issue and
  a game client could not would break CLAUDE.md's rule that the simulation never
  special-cases anybody. If chat is wanted, it goes through `PlayerCommandKind`
  and the socket switch first, and the panel gets it for free.
- **No config editing.** Everything it changes is something `ControlCommands`
  already exposes and a restart forgets. A panel that could rewrite the ini would
  need to be trusted with the file the password is in.
- **No user accounts.** One password, one role. An operator console with a
  permission model is a different product.
- **No metrics history.** The tick figure is an EMA for a human watching, and
  ⛔ **it is not a benchmark number** — it is measured over whatever the server
  was doing, map loads included. `tools/bench/` is where a per-tick cost comes
  from.

## Gates

| | |
|---|---|
| `WebAdminTests` | over a real socket with a real HTTP client: every route refuses an unauthenticated request, the CSRF header is required, the map route takes a map name and nothing else, a command reaches the tick thread, every control-port verb is reachable, the page is served, the security headers are present, nobody watching means no snapshots, and an open stream does get them |
| `AdminSessionTests` | the password, the tokens, both timeouts, the lockout and its ceiling, per-address isolation, the session cap, and the generated password's alphabet — all on an injected clock, so no test sleeps |
| `CliCoverageTests` | every `--web-*` flag moves the option it names; a settings flag with no `--web` is refused; `--web` alone is not remote |
