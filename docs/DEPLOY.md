# Deploying gg2-server

Running the server somewhere that is not a developer's checkout — a Linux VPS,
in practice. What is portable, what is measured, what to rent, and the four ways
a deployment differs from a `dotnet run` out of the repository.

---

## ⚠️ What is verified and what is not

**Read this before quoting anything below.** The distinction is the whole point
of the section, and CLAUDE.md's rule about claims a repo makes about itself
applies to this file more than to most.

| Claim | How it is held up |
|---|---|
| It compiles and links for Linux | `dotnet publish -r linux-x64` produces an ELF 64-bit LSB PIE executable. Run 2026-09-03 |
| No unguarded native call | `PortabilityTests.EveryNativeCallIsAnnotatedWindowsOnly`, plus the exact-set pin below it |
| No hard-coded Windows path | `PortabilityTests.NoShippedAssemblyHardCodesAWindowsPath`, over the metadata #US heap |
| SIGTERM stops gracefully | `DeploymentTests.SigtermRequestsAGracefulStop` |
| A nav-less deployment says so | `DeploymentTests.BotsWithNoNavDirectoryWarnAtStartup`, with both control arms |
| CPU and memory cost | Measured 2026-09-03, table below, **on Windows** |

⛔ **NOBODY HAS RUN THE BINARY ON LINUX.** Every row above is a static check, a
unit test, or a measurement taken on Windows. They are the checks that would
catch the *known* ways portability breaks; they are not a green run on the
target, and they cannot see a case-sensitivity assumption, a file-permission
difference, or a `glibc`/`musl` problem.

⚠️ **What would make this section stale**: the first actual Linux run. When
somebody does it, replace this box with what happened — including the boring
answer — rather than deleting it. A "we checked once" that nobody dated is how
the three entry-point sentences in `gg2-rl` outlived their measurements.

---

## Hardware

Measured 2026-09-03, **Release**, workstation GC (`DOTNET_gcServer=0`), the
process **pinned to a single core** with `ProcessorAffinity` to stand in for a
1-vCPU VPS. `ctf_truefort`, `--no-idle-when-empty`, no clients connected.

| Configuration | CPU per tick | % of one core | Working set |
|---|---|---|---|
| 12 bots, `--bot-mode scripted` | **0.156 ms** | **0.5%** | 55 MB |
| 24 bots, 24 slots, `--bot-mode scripted` | **0.226 ms** | **0.7%** | 55 MB |

⚠️ **The pacing numbers from the same runs are worthless and were discarded.**
The box was running another lane's training — **70 python processes, 8.1 of 24
cores**, measured during the run — so p99 read 100 ms and the worst tick 262 ms.
Process CPU time and working set are unaffected by that; scheduling latency is
nothing but that. `docs/BOTS.md`, "late ticks are a pacing measurement", is the
long version, and the server's own late-tick line prints the verdict:
`work 0.10ms, so: NOT this tick - the process was not scheduled`.

These agree with the two independently pinned figures — 0.151 ms for twelve bots
(`docs/ARCHITECTURE.md`, `BotPopulationTests.TwelveBotsRunWellUnderTheTickBudget`)
and 0.104 ms of process CPU (`docs/BOTS.md`) — which is the only reason to
believe a number taken on a machine in that state.

### What to rent

**Minimum: 1 vCPU, 1 GB RAM, 1 GB disk.** Genuinely enough for the default
configuration at up to ~24 bots. CPU is nowhere near the bound.

**Recommended: 2 vCPU, 2 GB RAM** — and the reason is not the workload.

⚠️ **The thing that produces rubberbanding on a cheap VPS is steal time, not
your tick.** A tick that does 0.2 ms of work and arrives 90 ms late is
indistinguishable, from inside the game, from a tick that took 90 ms — and the
measurement above is exactly that failure, reproduced accidentally. On a
burstable instance (t3/t4g-class, the cheaper OVH and Hetzner shared tiers) CPU
credit exhaustion looks identical to a server bug and will be diagnosed as one.
**Prefer a dedicated-CPU tier over a burstable one at the same price.** A second
core also keeps GC and socket work off the tick loop.

**Disk**: ~7 MB of application, ~70 MB for a framework-dependent .NET 10 runtime
(or ~75 MB self-contained), ~34 MB of nav graphs. Under 150 MB all in.

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
  -p:PublishNav=true \
  -o out/
```

`-p:PublishNav=true` is the flag you want and is **not** the default —
see "the nav graphs" below. The output is 36 MB: the assemblies, `maps/` (23
PNGs, always included) and `nav/` (25 graphs).

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

# ⚠️ Restart, not RestartSec=0: a listening port with connections in TIME_WAIT
# refuses to rebind for up to 60s. See "restarting" below.
Restart=always
RestartSec=5

# The tick loop is latency-sensitive and single-threaded; server GC is tuned for
# many-core throughput. See "garbage collection" below - this is a precaution,
# not a measured win.
Environment=DOTNET_gcServer=0

[Install]
WantedBy=multi-user.target
```

`KillSignal` is left at its default (SIGTERM) deliberately — the server handles
it now.

---

## The four ways a deployment differs from a checkout

### 1. The nav graphs do not ship unless you ask

⚠️ **This is the one that fails silently, and it is the default outcome.**

`nav/` is 34 MB, so it is deliberately excluded from ordinary build output —
`MapDirectory.ResolveNav`'s remarks have the cost argument, and it is paid twice
over for Debug and Release on every build. A published directory therefore has no
nav graphs unless `-p:PublishNav=true` was passed.

Without them the server still runs, still serves rounds, and still fills with
bots — **bots that fight whatever reaches them and never walk anywhere**. That
degradation is intentional (`NavCache.TryLoad`'s contract, inherited from
`navCacheLoad.gml`: a missing graph is not an error), which is exactly what made
it quiet.

✅ **The server now warns at startup** when bots are enabled and no nav directory
resolved at all:

```
no nav directory: bots will fight but not navigate. Pass --botnav <path> or set GG2_NAVCACHE
```

Three ways to satisfy it, in the order `ResolveNav` probes: `GG2_NAVCACHE`, a
`nav/` directory beside the executable (what `-p:PublishNav=true` produces), or
one found by walking up from it. `--botnav <path>` sets it explicitly.

⚠️ A **per-map** miss is a different and much older message — `no nav cache for
<map> area <n>` — and means the directory was found but that one map has no
graph generated. Both can appear in the same run.

### 2. SIGTERM, not Ctrl+C

`Console.CancelKeyPress` covers SIGINT, which is what an operator watching a
terminal sends. systemd's default `KillSignal` is **SIGTERM** and `docker stop`
sends SIGTERM, and neither reaches it.

✅ Handled since 2026-09-03 — `ShutdownSignals`, wired in `Program.Main`.

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

## Runtime knobs

### Garbage collection

`Gg2.Host.csproj` sets `ServerGarbageCollection=true` and
`ConcurrentGarbageCollection=false` (`docs/ARCHITECTURE.md`, "Runtime settings").
Server GC sizes its heaps per core and is tuned for throughput; the tick loop is
single-threaded and latency-sensitive.

⚠️ **`DOTNET_gcServer=0` is a precaution, not a measured win.** The measurements
above used it, and working set differed by under 3 MB between the two modes
(52.5 MB server GC / 54.9 MB workstation). **No pause-time difference was
measured**, and the tick allocates approximately nothing anyway
(`docs/BOTAI.md`) so gen2 collections should be rare either way. The csproj
default is deliberately left alone because flipping it on an unmeasured hunch
would contradict a documented decision.

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
| what the switches do and why two are dangerous | `CLAUDE.md`, "Phase 8's four switches" |
| bot cost, the arbiter, the budget controller | `docs/BOTS.md` |
| why a late tick is usually not the server | `docs/BOTS.md`, "late ticks are a pacing measurement" |
| the runtime settings and what the TFM bump proved | `docs/ARCHITECTURE.md`, "Runtime settings" |
| every CLI flag | `gg2-server --help` |
