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
(or ~75 MB self-contained), 1.2 MB of nav graphs. Under 100 MB all in — the
published directory itself is **2.9 MB**, against 36 MB before the nav graphs
were gzipped.

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
it.

---

## How a deployment differs from a checkout

### 1. The nav graphs

✅ **`nav/` ships in every build and every publish, with no flag.** 25 graphs
and `manifest.tsv`, 1.2 MB, beside `maps/`.

**Measured, on all 25 graphs:**

| | working tree | compressed |
|---|---|---|
| hex text, as `gg2-nav-gen` writes it | 35.68 MB | 1.112 MB |
| **gzipped, as shipped** | **1.11 MB** | 1.11 MB |
| a hand-rolled packed binary, *rejected* | 4.06 MB | 1.111 MB |

⚠️ **The third row is why there is no custom nav format.** A binary encoding
shrinks the tree 8.8x and what actually ships by **0.1%** — everything it removes
is redundancy deflate was already removing, because the files are hex-encoded
`ds_grid_write` blobs (32 bytes of file per numeric value, for values that are
mostly small integers). It would cost a `NAV_CACHE_VERSION` bump coordinated with
`gg2-nav-gen`, a new writer, a new reader and a new way for header and payload
to disagree, to save 3 MB of disk and about 8 ms per map change. Do not
re-propose it without a reason that is not size.

**Decompression costs about a millisecond**, once per map change, against a
33.3 ms tick: the largest map (`ctf_conflict`) loads in 9.14 ms plain and
10.29 ms gzipped. ⚠️ **That assumes one graph per map change**, which is what
`BotPopulationService.MapChanged` does — re-measure before reusing it if nav ever
loads per-round or eagerly for a rotation.

#### Regenerating them

`gg2-nav-gen` writes plain `.txt`; `NavCache` reads both containers, so nothing
about the generator itself needs to change.

⚠️ **A plain `.txt` WINS over a shipped `.txt.gz`.** `gg2-nav-gen` writes
`<map>_a<area>.txt` into the same `nav/` directory that holds the checked-in
`.txt.gz`, so the only way a plain file appears beside a compressed one is that
somebody just ran the generator — and the rule is that what was just written
beats what shipped. `NavCache.FindFile` implements this; the server logs when it
finds both, naming the one it used, because which one is in effect is otherwise
invisible.

The full cycle:

```bash
npx gg2navgen build --all      # writes .txt into gg2-server/nav
tools/compress-nav.sh          # gzip -9 -n, verify the roundtrip, delete the .txt
GG2_UPDATE_NAV_MANIFEST=1 dotnet test Gg2.sln --filter NavManifestTests
dotnet test Gg2.sln
```

⚠️ **The suite catches a half-finished cycle**:
`NavContainerTests.TheCheckedInGraphsAreCompressed` fails with the loose files
named and these commands in the message. **The manifest step is not optional if
the maps moved** — skipping it leaves the old pairing recorded and the new graphs
refused at runtime.

✅ **`gzip -9 -n` is deterministic**, so decompressing and recompressing a
checked-in graph reproduces it byte for byte. A regeneration that changed
nothing produces no git diff; without `-n` the timestamp field would rewrite
all 25 files every time.

#### It can still fail, and it still says so

Without a nav directory the server runs, serves rounds and fills with bots —
**bots that fight whatever reaches them and never walk anywhere**. That
degradation is intentional (`NavCache.TryLoad`'s contract, inherited from
`navCacheLoad.gml`: a missing graph is not an error). The startup warning:

```
no nav directory: bots will fight but not navigate. Pass --botnav <path> or set GG2_NAVCACHE
```

Three ways to satisfy it, in the order `ResolveNav` probes: `GG2_NAVCACHE`, a
`nav/` directory beside the executable (what a publish produces), or one found by
walking up from it. `--botnav <path>` sets it explicitly.

⚠️ A **per-map** miss is a different message — `no nav cache for <map> area <n>`
— and means the directory was found but that one map has no graph generated.
Both can appear in the same run.

⚠️ **And a third, for a graph that has drifted from its map**:

```
nav graph for <map> area <n> was built against different geometry and has been
refused; bots on it will not navigate. Regenerate it with gg2-nav-gen, or delete
nav/manifest.tsv's line for it to load it anyway
```

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
| what the switches do and why two are dangerous | `CLAUDE.md`, "Phase 8's four switches" |
| bot cost, the arbiter, the budget controller | `docs/BOTS.md` |
| why a late tick is usually not the server | `docs/BOTS.md`, "late ticks are a pacing measurement" |
| the runtime settings and what the TFM bump proved | `docs/ARCHITECTURE.md`, "Runtime settings" |
| every CLI flag | `gg2-server --help` |
</content>
