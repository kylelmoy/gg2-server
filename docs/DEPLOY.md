# Deploying gg2-server

Running the server somewhere that is not a developer's checkout — a Linux VPS,
in practice. What is portable, what is measured, what to rent, and the ways a
deployment differs from a `dotnet run` out of the repository.

---

## ⚠️ What is verified and what is not

| Claim | How it is held up |
|---|---|
| It compiles and links for Linux | `dotnet publish -r linux-x64` produces an ELF 64-bit LSB PIE executable |
| No unguarded native call | `PortabilityTests.EveryNativeCallIsAnnotatedWindowsOnly`, plus the exact-set pin below it |
| No hard-coded Windows path | `PortabilityTests.NoShippedAssemblyHardCodesAWindowsPath`, over the metadata #US heap |
| SIGTERM stops gracefully | `DeploymentTests.SigtermRequestsAGracefulStop` |
| A nav-less deployment says so | `DeploymentTests.BotsWithNoNavDirectoryWarnAtStartup`, with both control arms |
| Nav ships with a publish | Nothing mechanical checks this — `dotnet publish` was inspected by hand; see "the nav graphs" |
| A nav graph matches its map | `NavManifestTests`, with a mutated-walkmask arm and an entities-only arm |
| CPU and memory cost | Measured on Windows, table below |

⛔ **NOBODY HAS RUN THE BINARY ON LINUX.** Every row above is a static check, a
unit test, or a measurement taken on Windows. They are the checks that would
catch the *known* ways portability breaks; they are not a green run on the
target, and they cannot see a case-sensitivity assumption, a file-permission
difference, or a `glibc`/`musl` problem.

⚠️ **What would make this section stale**: the first actual Linux run. When
somebody does it, replace this box with what happened — including the boring
answer — rather than deleting it.

---

## Hardware

Measured on **Release**, workstation GC (`DOTNET_gcServer=0`), the process
**pinned to a single core** with `ProcessorAffinity` to stand in for a 1-vCPU
VPS. `ctf_truefort`, `--no-idle-when-empty`, no clients connected.

| Configuration | CPU per tick | % of one core | Working set |
|---|---|---|---|
| 12 bots, `--bot-mode scripted` | **0.156 ms** | **0.5%** | 55 MB |
| 24 bots, 24 slots, `--bot-mode scripted` | **0.226 ms** | **0.7%** | 55 MB |

⚠️ **Pacing numbers (p99, worst tick) are not reliable evidence of the
server's own cost** — scheduling latency from other processes on the box can
dominate them while process CPU time and working set stay unaffected.
`docs/BOTS.md`, "late ticks are a pacing measurement", is the long version, and
the server's own late-tick line prints a verdict alongside the number: `work
0.10ms, so: NOT this tick - the process was not scheduled`.

These agree with the two independently pinned figures — 0.151 ms for twelve bots
(`docs/ARCHITECTURE.md`, `BotPopulationTests.TwelveBotsRunWellUnderTheTickBudget`)
and 0.104 ms of process CPU (`docs/BOTS.md`).

### What to rent

**Minimum: 1 vCPU, 1 GB RAM, 1 GB disk.** Genuinely enough for the default
configuration at up to ~24 bots. CPU is nowhere near the bound.

**Recommended: 2 vCPU, 2 GB RAM** — and the reason is not the workload.

⚠️ **The thing that produces rubberbanding on a cheap VPS is steal time, not
your tick.** A tick that does 0.2 ms of work and arrives 90 ms late is
indistinguishable, from inside the game, from a tick that took 90 ms. On a
burstable instance (t3/t4g-class, the cheaper OVH and Hetzner shared tiers) CPU
credit exhaustion looks identical to a server bug and will be diagnosed as one.
**Prefer a dedicated-CPU tier over a burstable one at the same price.** A second
core also keeps GC and socket work off the tick loop.

**Disk**: ~7 MB of application, ~70 MB for a framework-dependent .NET 10 runtime
(or ~75 MB self-contained), 1.2 MB of nav graphs and 1.62 MB of their next-hop
tables. Under 100 MB all in — the published directory itself is **~4.5 MB** (the 2.9 MB measured by hand before
the tables, plus their 1.62 MB — arithmetic, not a fresh publish),
against 36 MB before the nav graphs were gzipped.

**Network**: not measured. Size it after watching a real game rather than from a
number in this file.

### ⛔ The one setting that changes the answer

**Keep `--bot-mode` at its default, `scripted`.** `docs/BOTS.md` measures a
search decision at **3617 µs at twelve live characters** against ~7 µs for a
policy forward pass — three to four orders of magnitude above the scripted
layer's per-bot cost. `search` and `tiered` are what turn a 0.7%-of-a-core server
into a CPU-bound one.

It degrades rather than breaks: `MicroBudgetController` walks the arbiter's
budget down by AIMD when ticks crowd their deadline, so the tick survives and the
bots get dumber. But you would be paying for cores to buy behaviour the scripted
layer gives away, and `--bot-mode search` **has never been measured with humans
in the server** — see the flag's own help text.

⚠️ `--speed` and `--control-port` must stay **off** on anything a client
connects to. CLAUDE.md, "Phase 8's four switches", says why: an unpaced server
desynchronises every connected client at once, and the control port executes
commands against a live game.

---

## Building and shipping it

```bash
dotnet publish src/Gg2.Host/Gg2.Host.csproj \
  -c Release -r linux-x64 --self-contained false \
  -o out/
```

**No flags.** The output is 2.9 MB: the assemblies, `maps/` (23 PNGs) and `nav/`
(25 graphs plus `manifest.tsv`).

Drop `-r linux-x64 --self-contained false` for a portable build that needs a
.NET 10 runtime installed; add `--self-contained true` to carry one.

```bash
scp -r out/ server:/opt/gg2-server/
ssh server chmod +x /opt/gg2-server/gg2-server
```

### A systemd unit

```ini
[Unit]
Description=Gang Garrison 2 server
After=network-online.target

[Service]
Type=simple
User=gg2
WorkingDirectory=/opt/gg2-server
ExecStart=/opt/gg2-server/gg2-server --bots 8 --players 12

# ⚠️ on-failure, NOT always, and the difference is the exit code. The server
# exits 3 when its tick loop has failed repeatedly (TickFaultBudget), which is
# the case a restart actually fixes — a fresh process is the only thing that
# reliably clears corrupted in-memory state. An ordinary shutdown exits 0, and
# `always` would restart that too, so an operator stopping the server on
# purpose would be silently undone.
Restart=on-failure

# ⚠️ Not RestartSec=0: a listening port with connections in TIME_WAIT refuses
# to rebind for up to 60s. See "restarting" below.
RestartSec=5

# The tick loop is latency-sensitive and single-threaded; server GC is tuned for
# many-core throughput. See "garbage collection" below - this is a precaution,
# not a measured win.
Environment=DOTNET_gcServer=0

# A backstop, not the bound. ConnectionLimits is what actually caps what a
# client can make the server hold (~95 MB worst case at 48 players — the
# arithmetic is in docs/HARDENING.md). This is for when something else is
# wrong, and it makes the OOM killer pick this process rather than whatever
# else is on the box.
MemoryMax=2G

# Sandboxing. None of it changes what the server does; all of it narrows what a
# bug in it could reach.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
RestrictAddressFamilies=AF_INET AF_INET6

# ⚠️ ProtectSystem=strict makes the WHOLE filesystem read-only, so every path
# the server writes has to be listed or it starts fine and fails later — the
# worst of both. There are three, and two are easy to forget:
#   nav/     a graph built at a map change for a custom map (--no-navgen off)
#   traces/  --record segments
#   logs/    --log-file, if you use it rather than journald
ReadWritePaths=/opt/gg2-server/nav /opt/gg2-server/traces /opt/gg2-server/logs

[Install]
WantedBy=multi-user.target
```

`KillSignal` is left at its default (SIGTERM) deliberately — the server handles
it.

⛔ **The firewall and kernel settings are not in this file and this process
cannot do them.** `docs/HARDENING.md` has them, along with what is deliberately
not defended against.

**Logging goes to journald** with no configuration: info on stdout, warnings and
errors on **stderr**, so `journalctl -u gg2 -p warning` is the whole of the
alerting story and needs no parser. `--log-file` exists for a copy that survives
independently of the journal — see "a log file, if you want one" below — and is
not needed under systemd.

---

## How a deployment differs from a checkout

### 1. The nav graphs

✅ **`nav/` ships in every build and every publish, with no flag.** 25 graphs
and `manifest.tsv`, 1.2 MB, beside `maps/` — plus one `<key>.dist.gz` per graph,
**1.62 MB together**, holding the next-hop tables A\*'s exact heuristic reads.
⚠️ **A publish that carries the graphs and not the tables still works** and is
slower at every map change (377 ms against 62 ms on `ctf_truefort`); the server
logs which of the two happened.

**Measured, on all 25 graphs:**

| | working tree | compressed |
|---|---|---|
| hex text, as the generator writes it | 35.68 MB | 1.112 MB |
| **gzipped, as shipped** | **1.11 MB** | 1.11 MB |
| a hand-rolled packed binary, *rejected* | 4.06 MB | 1.111 MB |

⚠️ **The third row is why there is no custom nav format.** A binary encoding
shrinks the tree 8.8x and what actually ships by **0.1%** — everything it removes
is redundancy deflate was already removing, because the files are hex-encoded
`ds_grid_write` blobs (32 bytes of file per numeric value, for values that are
mostly small integers). It would cost a `NAV_CACHE_VERSION` bump, a new writer,
a new reader and a new way for header and payload to disagree, to save 3 MB of
disk and about 8 ms per map change. Do not re-propose it without a reason that
is not size.

**Decompression costs about a millisecond**, once per map change, against a
33.3 ms tick: the largest map (`ctf_conflict`) loads in 9.14 ms plain and
10.29 ms gzipped. ⚠️ **That assumes one graph per map change**, which is what
`BotPopulationService.MapChanged` does — re-measure before reusing it if nav ever
loads per-round or eagerly for a rotation.

#### Regenerating them — one command

The generator is in this repository (`src/Gg2.Nav`, `docs/NAVGEN.md`), and
`gg2-server navgen` does the whole cycle:

```bash
dotnet run --project src/Gg2.Host -- navgen          # every map in maps/
dotnet run --project src/Gg2.Host -- navgen --check  # did anything move? exit 1 if so
dotnet test Gg2.sln
```

It writes the shipped `.txt.gz`, deletes a stale `.txt` beside it, and rewrites
`manifest.tsv` — and **skips a graph identical to the one on disk**, so
`git status` afterwards names the graphs a change actually moved.

⛔ **A plain `.txt` still WINS over a shipped `.txt.gz`.** An outside generator
(`gg2-nav-gen`) writes `<map>_a<area>.txt` into the same `nav/` directory that
holds the checked-in `.txt.gz`, so the only way a plain file appears beside a
compressed one is that somebody just ran one — and the rule is **what somebody
just wrote beats what shipped**. `NavCache.FindFile` implements it; the server
logs when it finds both, naming the one it used, because which one is in effect
is otherwise invisible.

`tools/compress-nav.sh` is now only for `gg2-nav-gen`'s output; `navgen` writes
the shipped container itself. Both are deterministic — the same graph gives the
same bytes, so a regeneration that changed nothing produces no git diff. The two
do not produce the *same* bytes as each other (zlib and .NET pack the same input
differently), which is why `NavGeneratorTests` compares the decompressed
**text**: the format is the text, the gzip is a container.

#### It can still fail, and it still says so

**A map with no graph is no longer a map with no bot navigation.** The server
builds one at the map change — well under a second depending on the map, logged
— because everything a graph needs is in the map's own PNG. That is what makes
a **custom map** navigable, and it is on by default:

```
built a nav graph for mymap area 1 in 86ms - 687 nodes, 6747 edges
```

`--no-navgen` turns it off, and `--navgen-save` writes the result into `--botnav`
so the next map change reads it instead of rebuilding. `docs/NAVGEN.md`.

### `--no-bot-occupancy`: let the team bunch up

Bots normally price an edge a team-mate is already walking 25% above its honest
cost, so a squad fans out instead of filing down one corridor. This turns that
off.

⛔ **It is a behaviour switch, not a performance one.** Without it match
dispersion falls from 0.785 to 0.634 and nothing replaces it. What it buys is a
live server that routes exactly the way `eval` and every training rollout
already do — those have never had occupancy — which is worth something if you
are comparing live play against measured runs.

### `--no-nav-distances`: the straight-line heuristic back

⚠️ **The exact heuristic is ON by default since 2026-09-04**, and this turns it
off. The tables are read from `nav/<key>.dist.gz` — written by `gg2-server
navgen`, 1.62 MB for all 25 graphs — and only derived when a map has no file.
They give A\* an exact heuristic instead of a straight-line guess. A\* is about 30%
of a twelve-bot tick, so this is real — but the search is *weighted*, so an exact
heuristic changes **which** route comes back as well as how fast, and a server
run with it is not route-comparable to one without.

Measured over 21 maps: route cost 103.6% → 99.1% of the cheapest route (better,
because the incumbent A\* is deliberately suboptimal), dispersion 0.634 → 0.613,
and 3 of 21 maps regress.

⛔ **Turning it OFF is a behaviour change, not a slower run**: the search is
weighted, so the heuristic picks the route as well as the speed, and a server
without it is not route-comparable to one with it.

The `eval` wash that made this off-by-default is **gone now that the tables
ship**. It was a wash because the tables were built once per map and the saving
accrued per match; the build is what the file removes. `docs/TRAPS-BOTS.md`,
"Shipping the tables".

It logs what it cost, **once per map+area and not once per round** — a rotation that stays on one map re-parses its graph every round, and the tables are cached past that:

```
nav distances for ctf_truefort area 1: loaded - 4 table(s), 10.8 MB, 62 ms
```

and when a map has no file — a custom one, whose graph was itself built at the
map change — it says so and what to do:

```
nav distances for cp_custom area 1: derived (no file) - 4 table(s), 10.8 MB, 377 ms.
Run `gg2-server navgen` to ship the tables and skip this next time
```

⚠️ **377 ms against 62 ms is the whole point of shipping them**, and the same
line reports which happened, so a server that silently went back to deriving
says so in its log rather than in a profile.

With generation off and no nav directory, the server runs, serves rounds and
fills with bots — **bots that fight whatever reaches them and never walk
anywhere**. That degradation is intentional (`NavCache.TryLoad`'s contract,
inherited from `navCacheLoad.gml`: a missing graph is not an error), which is
what made it quiet. The startup warning:

```
no nav directory and --no-navgen: bots will fight but not navigate.
Pass --botnav <path>, set GG2_NAVCACHE, or drop --no-navgen
```

Three ways to satisfy it, in the order `ResolveNav` probes: `GG2_NAVCACHE`, a
`nav/` directory beside the executable (what a publish produces), or one found by
walking up from it. `--botnav <path>` sets it explicitly.

⚠️ A **per-map** miss is a different message — `no nav cache for <map> area <n>`
— and appears only with `--no-navgen`, because otherwise the graph is about to
be built.

⚠️ **And a third**, when a graph and its map have drifted apart:

```
nav graph for <map> area <n> was built against different geometry and has been
refused. Rebuild it with `gg2-server navgen --map <map>`, or delete
nav/manifest.tsv's line for it to load it anyway
```

⛔ **That one is then rebuilt AND overwritten on disk, even without
`--navgen-save`** — the file describes terrain that is not there, so leaving it
means every restart refuses it again and any other tool reading the directory
still gets the wrong graph. A map that simply never had a graph is not a fault to
correct, and its generated graph stays in memory.

#### `manifest.tsv` — which map each graph was built from

⚠️ **A nav graph is keyed on the map's NAME**, deliberately (`NavCache.Key`
explains why the MD5 cannot be the key) — a graph built against an older version
of a map would otherwise load perfectly and hand bots terrain that is not there.
The header's mask dimensions catch a resize and nothing else; the symptom of a
mismatch is bots walking into walls, which reads as a bot bug.

`manifest.tsv` records, per graph, a hash of **the area's walkmask and its
entities** — the inputs a graph is built from, so art edits and PNG re-encodings
do not cry wolf. A mismatch refuses the graph; **a graph with no line is not
checked**, which is what keeps an operator's own maps working.

⚠️ **The entities are in the hash because the walkmask alone is not enough**:
`cp_dirtbowl`'s three areas share one mask and carry different graphs (835, 837
and 837 nodes), so a mask-only hash would give all three the same answer and
could not see a door or a gate move. `NavManifestTests.EntitiesAreInTheHash`.

Regenerating nav means regenerating this:

```bash
GG2_UPDATE_NAV_MANIFEST=1 dotnet test Gg2.sln --filter NavManifestTests
```

⚠️ **That run fails on purpose** after rewriting the file, so a manifest cannot
be regenerated by a CI job that happened to have the variable set and be reported
as green. Re-run without it, check the diff is the change you meant, and say so
in the commit.

⛔ **What is NOT gated: that a publish actually carries `nav/`.** There is no
test for it; a reader who needs this to be true should run `dotnet publish` and
look. The csproj glob is `..\..\nav\*` rather than a per-extension one, so a
container is less likely to silently stop shipping — but that is not a
substitute for a check.

### 2. SIGTERM, not Ctrl+C

`Console.CancelKeyPress` covers SIGINT, which is what an operator watching a
terminal sends. systemd's default `KillSignal` is **SIGTERM** and `docker stop`
sends SIGTERM; `ShutdownSignals`, wired in `Program.Main`, handles both.

⚠️ **Why it is worth handling rather than letting the process die.**
`GameServer.Dispose` is reached only on the graceful path, and it does three
things nothing else does: it **takes the lobby listing down** (otherwise a dead
server stays advertised until the lobby's own timeout and players connect to
nothing), it **closes an in-flight `--record` segment** (a `.part` file only
becomes a readable `.trace` when closed — the recording is lost, not truncated),
and it shuts the client sockets down.

### 3. Restarting can refuse the port for up to 60 seconds

On Linux, `bind()` to a port with connections still in TIME_WAIT fails with
`EADDRINUSE` unless `SO_REUSEADDR` is set — and it is not. `GameServer` sets it
to `false` explicitly.

⚠️ **That line is a no-op on Linux**, which is worth knowing before anybody
"fixes" it: `SO_REUSEADDR` is off by default there, so removing the call changes
nothing. On Windows it is doing real work — it keeps a second server on the same
port from silently sharing it.

So this is Linux's ordinary behaviour rather than a bug the port introduced.
`RestartSec=5` and `Restart=always` ride it out. Nothing here needs changing
unless a restart-under-load requirement appears; setting `SO_REUSEADDR` to true
on Linux only would fix it, at the cost of a platform conditional in a line that
currently has none.

### 4. Filenames become case-sensitive

`GameServer.LoadMap` resolves a map by `File.Exists(<dir>/<name>.png)`, so
`--map CTF_Truefort` works on Windows and fails on Linux with
`no map at /opt/gg2-server/maps/CTF_Truefort.png`.

⚠️ **Linux is the more correct side and this is not being "fixed".**
`InternalMaps` already matches with `StringComparer.Ordinal`, because the stock
client's own switch matches the literal string — a server that accepted
`CTF_Truefort` would be accepting a name the client will not. The error message
names the full path it looked for, which is enough to diagnose it.

The case to watch is a `gg2.ini` or rotation file **copied off a Windows host**,
where a mis-cased entry has been working by accident.

---

### A log file, if you want one

⛔ **You do not want one under systemd or Docker.** Both already capture stdout
and stderr, both already rotate, and the level split — info on stdout, warnings
and errors on **stderr** — is what makes `journalctl -u gg2 -p warning` work
with no parser. Turning on a second copy buys nothing and costs a write per
line.

`--log-file /opt/gg2-server/logs/server.log` is for the deployments with
neither: a bare console, a Windows service, a `screen` session. It **tees** —
the console sink stays wired, because the console is the record you have when
the thing that has broken is the disk.

```
--log-file <path>       also write here, rotating by size
--log-max-bytes <n>     rotate past this (default 16777216)
--log-keep <n>          rotated files kept (default 5); 0 rotates by deleting
```

Three behaviours worth knowing, all of them tested:

- **It appends across restarts.** The log of why the last process stopped is
  the log most worth having, and truncating on open would delete it at the
  exact moment a supervisor restarted the server to recover from whatever wrote
  it.
- **A path that cannot be opened at all fails the start** (exit 2). That is a
  typo in the command line, and starting anyway leaves you with a running
  server and a log you will go looking for and never find.
- **A failure after that is swallowed**, with one warning to the console and no
  more. A full disk is not a reason to take a game server down, and a log sink
  that can throw is a log sink that can kill the tick.

⚠️ Under `ProtectSystem=strict` the log directory has to be in
`ReadWritePaths` or the server starts and fails on the first write.

---

## Runtime knobs

### Garbage collection

`Gg2.Host.csproj` sets `ServerGarbageCollection=true` and
`ConcurrentGarbageCollection=false` (`docs/ARCHITECTURE.md`, "Runtime settings").
Server GC sizes its heaps per core and is tuned for throughput; the tick loop is
single-threaded and latency-sensitive.

⚠️ **`DOTNET_gcServer=0` is a precaution, not a measured win.** Working set
differed by under 3 MB between the two modes (52.5 MB server GC / 54.9 MB
workstation) and no pause-time difference was measured; the tick allocates
approximately nothing anyway (`docs/BOTAI.md`) so gen2 collections should be
rare either way. The csproj default is deliberately left alone because flipping
it on an unmeasured hunch would contradict a documented decision.

⚠️ **What would settle it**: `--tick-stats` on a real 1-2 vCPU box, with and
without `DOTNET_gcServer=0`, looking for a bimodal tail that moves. If it does
not move, delete this section.

### Globalization

`InvariantGlobalization=true` repo-wide, so there is no ICU dependency and the
server runs on Alpine/musl without `icu-libs`. Nothing to configure.

### Reference checkouts

None needed. Constants are compiled into `Gg2.Protocol`, the maps ship in the
publish output, and `GG2_REFERENCE` is read only by the **test** suite. A
deployed server has no dependency on a Gang Garrison 2 checkout of any kind.

---

## Where else to look

| For | Read |
|---|---|
| running it where anybody can reach it | `docs/HARDENING.md` |
| running the attacks at it yourself | `tools/abuse/README.md` |
| what the switches do and why two are dangerous | `CLAUDE.md`, "Phase 8's four switches" |
| bot cost, the arbiter, the budget controller | `docs/BOTS.md` |
| why a late tick is usually not the server | `docs/BOTS.md`, "late ticks are a pacing measurement" |
| the runtime settings and what the TFM bump proved | `docs/ARCHITECTURE.md`, "Runtime settings" |
| every CLI flag | `gg2-server --help` |
</content>
