# The web admin panel — `gg2-server --web`

A browser console for a running server: who is on it, where they are, what the
round is doing, six graphs of how the process is coping, and the operator
controls — freeze, step, speed, bot count, map change, teleport, kick.

It is the **only** surface in this project that is meant to be reachable from
the network, and everything below follows from that.

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

### What it costs

⚠️ **Release, 24 cores, `ctf_truefort`, against a 33,333 us budget. Re-measure
before repeating any of these** — a number quoted without its build is how a
stale figure can sit in multiple documents unnoticed.

| | Cost | Of a 33.3ms tick |
|---|---|---|
| **`--web` off** | one `null` check per tick | — |
| Tick timing in `Run()` — ⚠️ **NOT gated by `--web`** | 46 ns/tick | 0.00014% |
| `--web` on, nobody watching: the sampler's early-out | 3.5 ns/tick | 0.00001% |
| `--web` on, nobody watching: the 1 Hz sample | 661 ns/**second** | 0.0020% of one tick |
| **A browser watching**: one snapshot, 13 on the roster | 10.6 us (p95 24) | 0.032% |
| **A browser watching**: one snapshot, 48 on the roster | 33.5 us (p95 67) | 0.101% |

⛔ **The tick-timing accumulators are the one part that is NOT behind `--web`.**
Two `Stopwatch.Elapsed` reads and some field arithmetic run in `GameServer.Run`
on every tick of every server, panel or no panel — a real unconditional cost,
left ungated because a branch would make the paced loop two loops for a saving
smaller than the branch.

⚠️ **Snapshot cost is per SNAPSHOT, not per watcher.** The string is built once by
the tick thread and published; every stream then writes that same string from its
own thread. Ten people with the panel open cost the tick thread exactly what one
does. What does scale it is the roster — it is a linear walk — so the 48-player
figure is the one to plan against, and at 5 Hz that is **0.17 ms of tick thread
per second**, or one tick in six costing 34 us more than the other five.

⚠️ **`GET /api/state` is the exception**: a one-shot read is executed on the tick
thread through the command queue, so a script polling it at 30 Hz would cost what
30 snapshots a second cost. The panel does not — it reads the stream.

**Memory**: about 100 KB in total — 300 perf samples (~31 KB), 500 log lines
(~50 KB) and the current snapshot string (3.5 KB at 13 players, 11 KB at 48).

**End-to-end**: `--web` on versus off, both unpaced with 12 bots, run
simultaneously on one machine and alternated between slots: **-0.19% +/- 2.79%**
— no effect resolvable, with the interval about a hundred times looser than the
20 ns/tick the table predicts. ⚠️ **Measure arms simultaneously, not
sequentially** — sequential runs are vulnerable to the machine's load moving
between runs, which can produce a confident-looking cost number smaller than the
spread within a single arm.

The same instrument *can* see a watcher at unpaced speeds: 16.1% of unpaced
throughput. ⚠️ That is an upper bound, not the real cost — at 65,000 ticks/s the
panel is serialising ten thousand events a second and allocating 40 MB/s, neither
of which happens at 30 Hz. Take the direct per-snapshot number above.

⚠️ **An idle server ticks at 2 Hz** (`--no-idle-when-empty` turns that off), so
with nobody connected the panel updates twice a second. It says `idle` in the
speed tile rather than looking stuck.

`WebAdminTests.NobodyWatchingMeansNoSnapshots` gates the structural half of this.
⚠️ **What would show the table has gone stale**: a snapshot that stops being
O(roster), a sampler moved off the 1 Hz cadence, or anything new added to
`PerfHistory.Sample`'s once-a-second body.

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
| `GET /api/events` | — | server-sent events: `state` snapshots, `log` lines and `perf` samples |
| `POST /api/command` | a control-port command | freeze, resume, step, speed, bots, map, teleport, kick, command |
| `GET /api/maps` | — | what this server could change to, plus its rotation |
| `GET /api/map.png?name=` | — | a map's PNG, for the live map |
| `GET /api/log?after=` | — | the scrollback, by sequence number |
| `GET /api/perf?after=` | — | the performance history, by sequence number |
| `GET /api/system` | — | the host: OS, CPU, memory, runtime, paths |

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
`unknown cmd`. There is a negative control beside it so the assertion is known to
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

### The look: HeroUI, imitated rather than imported

The panel wears **HeroUI v3's design language with none of HeroUI's code.** The
token names, the OKLCH surface ladder, the `color-mix()` derivations and the
radius scale in `app.css` are HeroUI's own, read out of `@heroui/styles@3.2.4`
(`dist/themes/default/variables.css` and `dist/themes/shared/theme.css`) and
re-hued to the theme:

```
lightness 0.77   chroma 0.13   hue 302.636371990466   radius small   base 0.0043
```

⚠️ **Why tokens and not a vendored stylesheet.** HeroUI ships ~403 KB of CSS for
a component set this page does not have, needs a directory route so its chunks
can lazy-load — which puts back the path-traversal surface `WebAssets`
deliberately removed — and would be this repo's first runtime dependency. What
actually makes it *look* like HeroUI is the token system: the surface ladder,
the hairline borders, the soft-variant fills, the flat dark cards, the focus
ring. That is about ninety declarations.

⚠️ **`oklch()` and `color-mix()` are used directly, as HeroUI does**, with no hex
fallbacks — both have been baseline since 2023, and a browser without them would
render the page unstyled rather than wrongly. The panel is an operator console;
that is an acceptable floor.

Three things that had to be derived rather than copied:

- ⚠️ **`base` is the neutral CHROMA, not a lightness.** Stock HeroUI dark builds
  its greys at chroma 0.004-0.006 around hue 286, and the builder's hue slider
  moves the whole theme — so the greys here are the same lightness ladder at
  chroma 0.0043, hue 302.64. A grey that is faintly the accent's colour.
- ⛔ **A light accent needs DARK text on it.** This accent is L 0.77, so `--snow`
  over it is **2.13:1** and `--eclipse` is **8.16:1** — measured, and the one
  thing a light-accent theme gets wrong. `--accent-foreground` is the dark one.
- ⚠️ **`--radius: 0.375rem` is an inference**, not a value read off the builder
  (which is a client-side app). HeroUI's default is `0.5rem` and "small" is a
  step below. Every other radius is a `calc()` off that one line, so it is one
  line to correct if that turns out wrong.

⚠️ **The chart series are NOT theme tokens and do not move with the accent.**
They are the data-viz palette's dark steps, validated against `--surface`
(`#19181a`): worst all-pairs CVD ΔE 9.4, worst normal-vision ΔE 24.6, all ≥ 3:1.
Violet was kept over blue after measuring blue worse on this surface (tritan ΔE
4.0 against 9.4); the two never appear as peers and every series is keyed in a
legend. The one exception is **late ticks**, which uses the theme's `--warning`:
that series means "something is wrong", the panel says that in one colour
everywhere, and a single-series chart has no pair check to fail.

⚠️ **`<canvas>` cannot read a custom property**, so `app.js` resolves the ones it
draws with through `token()` and caches them. That is what keeps the live map and
the charts on the same palette as everything else; the alternative — hex repeated
in the JS — is exactly how a theme drifts.

### Things the page had to be told, that are not obvious

- ⚠️ **World pixels are art pixels × 6** (`GameConstants.CellSize`, and what
  `LevelMap.Width` applies), so the map PNG scaled to the world's width *is* the
  coordinate system. A dot off by a constant factor is that number.
- ⚠️ **Markers are sized in CSS pixels and converted back into world units.**
  `ctf_truefort` is 5,346 world pixels wide, so a 9-unit dot on a canvas scaled
  to fit is **one screen pixel**.
- ⚠️ **Cap the map card's WIDTH, never its height.** A canvas stretches its
  bitmap to whatever box CSS gives it, so a `max-height` distorts the map and
  puts every dot in the wrong place. `cp_dirtbowl` is three areas stacked
  vertically and is the case that needs the cap at all.
- ⚠️ **`IntelState.OnField` does not mean "dropped"** — it means the briefcase
  exists rather than being carried, which is also true of one sitting at home.
  The state sends the home position as well so the two can be told apart.
- ⚠️ **A map change is a ten-second countdown, not an event.** The panel counts
  it down in a banner rather than a toast, which would look like a button that
  did nothing.
- ⚠️ **Below `--bots-min-humans` the population target is ZERO rather than the
  fill**, so a bot count set on an empty server does nothing. The panel says
  which it is, because the default is 1 and the first thing anybody tries is
  setting bots on an empty server.
- ⚠️ **A player's name is whatever they typed into their own client.** It reaches
  the DOM through `textContent`, never `innerHTML`, and the CSP is the second
  net.

## The performance graphs

Six small multiples on one time axis — **tick time**, **CPU**, **memory**, **late
ticks**, **bytes sent** and **load** (live characters, bots, humans) — over the
last five minutes at 1 Hz, with a synchronised crosshair so a hitch in one can be
read against every other at the same instant. That is the whole reason they are
six frames rather than six numbers: "the tick spiked, and *at the same second*
the character count jumped and gen-2 collected" is an answer, and six separate
readouts are not.

`PerfHistory` samples on the tick thread once a second. ⚠️ **It samples whether
or not anybody is watching, which is the opposite of the state snapshot and is
deliberate**: an operator opens the panel *because* something already happened,
so a history that started when they arrived would answer a question nobody has.
The per-tick cost of that is one `Environment.TickCount64` comparison — 3.5 ns,
measured — and the syscalls are at 1 Hz and total 661 ns. Both halves of the
asymmetry are gated: `NobodyWatchingMeansNoSnapshots` and
`ThePerformanceHistoryIsCollectedWithNobodyWatching`. The full table is above.

⚠️ **The once-a-second body allocates nothing.** The sample is a struct written
into a preallocated ring, and the bot count is taken from the same roster walk as
the character count rather than from `GameServer.BotSlots`, which builds a
`List<int>` on every access.

⚠️ **The samples go over the wire as arrays of bare numbers** against a `fields`
list, because 300 samples × 15 named fields is 60 KB of repeated key names for
5 KB of data. `EveryPerformanceRowMatchesTheDeclaredFields` is what stops the
list and the rows drifting apart — a column added to one and not the other
silently shifts every series after it onto the wrong chart.

### ⚠️ CPU is measured over five seconds, not one, and that is a measurement fact

**Windows accounts process CPU at the 15.625 ms clock interrupt**, so a
one-second delta of `Process.TotalProcessorTime` can only ever be a multiple of
**1.56%**. With ten bots (about 0.4% of a core), a per-second series is an
alternating `0.00 / 1.56 / 0.00` sawtooth that reads as spiking. Over a
five-second trailing window the same quantum is 0.31%, below what anybody reads
off a graph. The series therefore lags a step change by up to five seconds, and
the chart's caption says `5s`.

⚠️ **It is a percentage of ONE core, not of the machine.** The tick loop is a
single thread, so "60% of one core" is the number that says how close it is to
being the bottleneck; dividing by the core count would hide that behind a
comfortable-looking 7%. It can exceed 100 — GC and the panel's own threads are in
it.

### The rest of what the graphs had to be told

- ⚠️ **The tick budget joins the y-scale only once the data is within a quarter
  of it.** Forcing 33.3 ms into a chart whose real values are 0.1-0.5 ms flattens
  every one of them onto the baseline: the reference line destroys the resolution
  of the thing it is a reference for. So a healthy server's chart zooms into its
  own range, and the budget line appears — with its label — exactly when a tick
  starts getting near it.
- ⚠️ **`_lateTicks` is incremented outside the `&&` the warning hangs off.**
  `LateTickMonitor` rate-limits itself, returning null for most late ticks so the
  log is not a flood; counting only when it produced a line would have
  under-reported by exactly the amount the rate limiter suppressed, which on a
  struggling server is nearly all of them.
- ⚠️ **A departing client's bytes are retired into a running total before it is
  removed.** Summing the live connections alone would make the figure drop
  whenever somebody disconnected, and a rate derived from it would go negative at
  exactly the moment an operator was looking to see why.
- ⚠️ **`ClientConnection.BytesSent` counts what the socket accepted, not what was
  queued.** A client whose receive window is full has bytes going nowhere, and
  counting those would report bandwidth a congested link is not carrying — which
  is the client the operator is looking at the graph about.
- ⚠️ **The series colors are not the team colors.** Red and blue mean RED and BLU
  everywhere else on this page. The charts use the data-viz palette's aqua,
  orange and violet, validated against this card's own surface rather than
  assumed: worst all-pairs CVD ΔE 9.4, worst normal-vision ΔE 24.6, all ≥ 3:1
  contrast. Late ticks wear the status *warning* step, because that series means
  "something is wrong" rather than "series four", and its title carries the label
  so the color is never alone.
- ⚠️ **One y-axis per chart, ever.** Working set and managed heap share a frame
  because they share a unit; tick time and CPU do not and never will. A second
  y-scale invents a correlation that is not in the data.
- ⚠️ **`.seg-btn` is scoped to its own group.** Each chart's range buttons and the
  speed selector share the class name but have separate handlers, so clicking one
  does not affect the other's selection state.

⛔ **None of these numbers is a benchmark number.** They are measured over
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

⚠️ **Re-read on every open, not just the first.** Half of it is live: memory,
both uptimes, the thread count, the GC counters.

⛔ **The command line and the environment are never in it.** `--web-password` is
in one and may be in the other, and this endpoint is read by a browser.
`SystemNeverLeaksTheCommandLineOrTheEnvironment` looks for the actual password in
the reply, so it fails on any future field that starts including either.

### Where each number comes from, per platform

| | Linux | Windows |
|---|---|---|
| CPU model, clock | `/proc/cpuinfo` — ⚠️ the **current** clock of core 0 | registry `CentralProcessor ` — ⚠️ the **nominal** clock at boot |
| Memory | cgroup v2 `memory.max`/`memory.current` where limited, else `/proc/meminfo` | `GlobalMemoryStatusEx` |
| OS uptime | `/proc/uptime` | `Environment.TickCount64` |

⚠️ **The two clocks mean different things and the panel says which** — `(now)` or
`(nominal)`. On a shared VPS the Linux figure moves between requests.

⛔ **`GC.GetGCMemoryInfo()` is NOT the source for system memory, and that is
measured.** Its fields describe *the last garbage collection*: on a freshly
started server this build reported `MemoryLoadBytes = 0` on a machine with 64 GB
in it. A panel showing "0 bytes in use" is worse than one showing nothing.

⚠️ **The cgroup is preferred over `/proc/meminfo` where one exists**, because that
is the whole point on a VPS: a container sees the host's `/proc/meminfo` and is
killed at its own limit. The panel reports which of the two it read.

⚠️ **A Debug build is called out with a badge**, not just reported. The tick
graphs directly above it are twice as slow in Debug — 0.307ms against 0.151ms at
twelve bots — and an operator comparing them against a number from somewhere else
needs to know which they are looking at.

## Running it on Linux

⚠️ **Audited and cross-compiled, NOT run.** There is no Linux machine in this
project's development environment, so what follows is a source audit plus a
`dotnet publish -c Release -r linux-x64` that builds clean with
warnings-as-errors and the platform-compatibility analyser on — which catches an
unguarded Windows API. **That is not the same as having run it**, and the first
real Linux run is the test this section should be checked against.

- ✅ **One file in `src/` is Windows-only**: `TimerResolution` (a `winmm.dll`
  P/Invoke), already guarded by `OperatingSystem.IsWindows()` and a no-op
  elsewhere. `SystemInfo` adds two more guarded branches and no others.
- ⚠️ **No timer raise on Linux, and that changes the late-tick numbers.** On
  Windows the loop raises the timer to 1 ms because a wait rounds up to the
  15.6 ms quantum. Linux has no equivalent knob and its wait granularity is the
  kernel's; the late-tick graph is likely *better* there, and is untested.
- ⛔ **`nav/` does not travel with a publish and `maps/` does.** The host csproj
  copies `maps/*.png` (458 KB) and nothing copies the 35 MB of nav graphs, so a
  published server on a VPS serves rounds and runs bots that **fight but do not
  walk**. Copy `nav/` beside the binary or set `GG2_NAVCACHE`.
- ⚠️ **The filesystem is case-sensitive.** The panel's map list passes back
  exactly what it read off disk, so that path is safe; a hand-typed
  `CTF_Truefort` in the map box works on Windows and fails on Linux.
- ⚠️ **`--web 80` or `--web 443` needs `CAP_NET_BIND_SERVICE`** or a proxy in
  front. Ports below 1024 are privileged.
- ⚠️ **`--web-tls` with a legacy `.pfx`**: OpenSSL 3, which current distros ship,
  refuses the old RC2-encrypted PKCS#12 files Windows tooling still produces.
  Re-export with `-keypbe AES-256-CBC -certpbe AES-256-CBC` if it will not load.

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
| `WebAdminTests` | over a real socket with a real HTTP client: every route refuses an unauthenticated request, the CSRF header is required, the map route takes a map name and nothing else, a command reaches the tick thread, every control-port verb is reachable, the page is served, the security headers are present, nobody watching means no snapshots but does mean a performance history, every sample row matches the declared fields, and an open stream gets both |
| `AdminSessionTests` | the password, the tokens, both timeouts, the lockout and its ceiling, per-address isolation, the session cap, and the generated password's alphabet — all on an injected clock, so no test sleeps |
| `CliCoverageTests` | every `--web-*` flag moves the option it names; a settings flag with no `--web` is refused; `--web` alone is not remote |
</content>
