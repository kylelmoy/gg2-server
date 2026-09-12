# Deploying gg2-server

Running the server somewhere that is not a developer's checkout — a Linux VPS,
in practice. What is portable, what is measured, what to rent, and the ways a
deployment differs from a `dotnet run` out of the repository.

---

## What is verified and what is not

| Claim | How it is held up |
|---|---|
| It compiles, links and RUNS on Linux | `dotnet publish -r linux-x64` produces an ELF 64-bit LSB PIE executable, and one has served on Ubuntu 24.04 — box below |
| No unguarded native call | `PortabilityTests.EveryNativeCallIsAnnotatedWindowsOnly`, plus the exact-set pin below it |
| No hard-coded Windows path | `PortabilityTests.NoShippedAssemblyHardCodesAWindowsPath`, over the metadata #US heap |
| SIGTERM stops gracefully | `DeploymentTests.SigtermRequestsAGracefulStop` |
| A nav-less deployment says so | `DeploymentTests.BotsWithNoNavDirectoryWarnAtStartup`, with both control arms |
| Nav ships with a publish | Nothing mechanical checks this — `dotnet publish` was inspected by hand; see "the nav graphs" |
| A nav graph matches its map | `NavManifestTests`, with a mutated-walkmask arm and an entities-only arm |
| CPU and memory cost | Measured on Windows, table below |
| `--version` says which commit is running | `BuildInfoTests`, against the regex `scripts/deploy.sh` parses it with; `ControlPortTests.StateReportsTheRoster` for the `state` reply |
| A deploy reaches the hosts and flips them | Done for real to three hosts, latest 2026-09-12, `0d04911` to two Debian 12 and one Ubuntu 24.04. `--rollback` and the prune are still unrun; see "what a real deploy has and has not exercised" |
| A stock client joins one | Done 2026-09-12, all three hosts — the handshake and the plugin gate only. **Not** evidence about trajectories; see the box above |

**The binary runs on Linux.** First done 2026-09-08 on Ubuntu 24.04.4 LTS,
x86-64, .NET 10.0.11, framework-dependent publish of `9daae8f` deployed by
`scripts/deploy.sh`: it starts, loads `ctf_truefort`, reads its shipped nav
tables (`4 table(s), 10.8 MB, 120 ms` — loaded, not derived), listens on 8190,
announces to the lobby, serves the web panel, and logs no warning or error in
the invocation. `journalctl -u gg2 _SYSTEMD_INVOCATION_ID=<id> -p warning` is
empty. SIGTERM and the graceful stop were exercised by the restart.

**A stock GM8 client joins a Linux-hosted server.** First done 2026-09-12 against
all three hosts running `0d04911` — two Debian 12, one Ubuntu 24.04. The client
reached each from the lobby listing, completed the handshake and picked a team.
That also exercises the plugin gate: all three run `ServerPluginsRequired=1`, so a
client that could not load `chat_v2_pr` could not have joined at all.

**What a Linux run still has not shown**: nobody has *played* on one. The join was
not followed by sustained movement, so **there is still no evidence about
trajectory parity from a Linux host** — that is the claim the client's own
re-simulation would falsify, and it is the one that matters, because the symptom
is rubberbanding rather than an error anywhere. No round has been played to
completion and no map change has happened under a connected client, so the
rotation is unexercised on Linux too. The case-sensitivity, file-permission and
`glibc`/`musl` classes of problem are narrowed rather than closed — two
distributions now, one filesystem, one publish shape.

⚠️ **A joining client is not a playing one, and the difference is the whole of
phase 4.** Everything the join establishes is protocol; nothing it establishes is
physics.


---

## Hardware

Measured on **Release**, workstation GC (`DOTNET_gcServer=0`), the process
**pinned to a single core** with `ProcessorAffinity` to stand in for a 1-vCPU
VPS. `ctf_truefort`, `--no-idle-when-empty`, no clients connected.

| Configuration | CPU per tick | % of one core | Working set |
|---|---|---|---|
| 12 bots, `--bot-mode scripted` | **0.156 ms** | **0.5%** | 55 MB |
| 24 bots, 24 slots, `--bot-mode scripted` | **0.226 ms** | **0.7%** | 55 MB |

**Pacing numbers (p99, worst tick) are not reliable evidence of the
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

**The thing that produces rubberbanding on a cheap VPS is steal time, not
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

### The one setting that changes the answer

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

`--speed` and `--control-port` must stay **off** on anything a client
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

`scripts/build-linux.sh` (and `scripts/build-linux.ps1`, for a machine with no
bash) runs that command into `deploy/linux` and checks what came out: an ELF
64-bit x86-64 binary, the maps, the nav graphs, `manifest.tsv`, and a warning
when the `.dist.gz` tables are missing. Neither cleans its output directory,
because a deployment directory accumulates state a publish has no business
deleting.

For one machine, that directory plus `scp` is the whole story:

```bash
scp -r out/ server:/opt/gg2-server/
ssh server chmod +x /opt/gg2-server/gg2-server
```

**The executable bit does not survive `scp` from Windows**, which is what the
second line is for.

## Updating many machines at once

`scp -r` over a live install is the thing that stops scaling, and not because of
the copying. It overwrites in place, so there is nothing to roll back to; it
carries whatever else is in the directory, including the developer's `gg2.ini`;
and afterwards nothing on the host says which commit is running.

`scripts/deploy.sh` answers all three. One release directory per commit, a
`current` symlink the unit points at, and a `shared` directory the deploy never
touches:

```
/opt/gg2-server/
  releases/17bec29/     an immutable publish, named for the commit
  releases/cba32ad/     the one before it, kept for --rollback
  current  -> releases/17bec29
  previous -> releases/cba32ad
  shared/               gg2.ini, bans.txt, traces/, logs/, custom maps
```

```bash
scripts/deploy.sh                # build HEAD, push to every host in deploy/hosts
scripts/deploy.sh --status       # what is each host actually running?
scripts/deploy.sh --rollback     # flip every host back one release
scripts/deploy.sh -H gg2@1.2.3.4 # one host, ignoring the hosts file
scripts/deploy.sh --print-unit   # the systemd unit for this layout
```

`deploy/hosts` is one ssh destination per line. A destination is whatever `ssh`
accepts, so `~/.ssh/config` carries the ports, keys and jump hosts and this file
stays a list of names. `deploy/` is excluded from git, so real addresses do not
reach the repository.

| Step | Why it is that way |
|---|---|
| **preflight the host before transferring** | passwordless `sudo`, the unit's existence and write access are all checkable in one round trip, and all three otherwise fail late — `sudo` after the symlink has already been flipped, which leaves a host carrying a release its service was never restarted onto. The failure prints the exact sudoers line to install. |
| publish into a fresh `deploy/stage/<sha>` | `deploy/linux` accumulates `gg2.ini`, custom maps and generated nav. Staging somewhere disposable keeps the developer's copies of those off every host; the archive also excludes them by name, for `--from`. |
| stream a `tar` over ssh, not `scp -r` | tar carries the mode bits, so the executable bit survives a copy from Windows and the `chmod +x` step disappears. Nothing is written to `/tmp` at either end. |
| unpack to `releases/<sha>.incoming` | the running server never sees a half-written release. |
| **run `--version` there before flipping** | the only check that executes the binary on the machine it will run on. A missing .NET 10 runtime, a publish for the wrong architecture and a truncated transfer all fail here, while `current` still points at the release that works. |
| flip `current`, record `previous` | `ln -sfn` onto a symlink-to-a-directory drops the new link *inside* it; a fresh link and `mv -Tf` is both correct and atomic. |
| `systemctl restart`, then read the version back | `is-active` says a process is running, not which one. |
| prune to `--keep` (default 5) | never `current` or `previous`, and never fatal. |

⚠️ **Probing for passwordless sudo is harder than it looks, and two obvious
checks are both wrong.** `sudo -n systemctl is-active gg2` exits 1 when sudo
needs a password *and* when the unit is merely stopped, so a host with no
sudoers rule reports as ready. `sudo -n true` fails on a host whose rule is
perfectly good, because `true` is not one of the granted commands. And
`sudo -n -l <command>` answers about authorisation without regard to whether a
password would be wanted, so any member of the `sudo` group passes it. The
preflight runs `sudo -n systemctl show <unit> -p LoadState --value`: it is
granted by the rule, and `systemctl show` exits 0 whenever it runs — even for a
unit that does not exist — so a non-zero status means sudo refused and nothing
else.

**Hosts are sequential and the first failure stops the run.** With several
machines the property worth having is that a bad build reaches one of them
rather than all of them; `--keep-going` overrides it once you have watched it
work somewhere.

**It refuses a dirty working tree.** The commit is stamped into the assembly
from git HEAD at build time, so a build with uncommitted edits reports a commit
it is not, and the pre-flip check on the target would then agree with the
release name and be wrong with it. `--allow-dirty` says you mean it.

**It does not run the tests.** Gate on the suite's exit code yourself:

```bash
if dotnet test Gg2.sln > log; then scripts/deploy.sh; else cat log; fi
```

**A restart drops everyone connected.** The graceful path still runs — the
lobby listing comes down, an in-flight `--record` segment is closed into a
readable trace — but players in a match are disconnected without warning. The
control port's `state` verb reports the roster if you want to wait for an empty
server first.

### Which build is running

```
$ gg2-server --version
gg2-server 1.0.0 (17bec29), .NET 10.0.11, linux-x64
```

The same line is logged once at startup — once per **process**, not per
in-process restart, because the control port's `restart` verb and the panel's
button both rebuild the server without replacing the binary. The commit and the
version are also in the control port's `state` reply, so the panel and the port
both answer the question.

The runtime and the architecture are on the line because those are the two
deployment failures a commit hash cannot see. The commit is HEAD at build time
and says nothing about the working tree it was built from, which is what
`deploy.sh`'s dirty-tree refusal is for: it is the only check that can still see
the tree.

`BuildInfoTests` gates the format against the regex the deploy script reads it
with. They are a shell script and a C# format string with nothing else between
them.

### What a real deploy has and has not exercised

First real deploy: 2026-09-08, `9daae8f` to one Ubuntu 24.04 host. Latest:
2026-09-12, `0d04911` to three — two Debian 12 and that Ubuntu 24.04.

**Run and working**: the preflight, the tar stream, the unpack into `.incoming`,
the `--version` probe on the target, the flip, the `systemctl restart`, the
post-restart version read-back, `--status` against a deployed host, `--from`
against a staged publish, **multiple hosts in one invocation**, **recording
`previous`** (the Ubuntu host's advanced `9daae8f` -> `00ee10b`), and **the
sequential stop**: two hosts were named, the first refused the preflight for want
of passwordless sudo, and the second was never contacted.

**Not yet run by anything**: `--rollback`, which now has a target on exactly one
host; the prune, which needs more than `--keep` releases and the most any host has
is three; and `--keep-going`, still untested against a real failure.

⚠️ **A first deploy to a host is a migration, not an install, if anything is
already there.** One host carried the flat pre-`--version` layout and needed its
`gg2.ini` lifted into `shared/` and the flat tree removed before `deploy.sh` could
run at all — the script deliberately does none of that. The preflight catches it
as "cannot write `<root>`", because the old install is owned by the service user
rather than the deploy user.

Three defects the first deploy found, all fixed, all of a kind no test in this
repository could have caught:

- **`set -e` is ignored inside a command whose status is tested.** The
  per-host body was `if ! ( set -e; … )`, and the shell disables errexit for
  the whole subshell there — the `!` included. A failing preflight ran the
  transfer anyway, and a failing transfer would have flipped `current` onto a
  release that was never unpacked, silently. Every step now checks with
  `|| return 1`.
- **The failure message asserted something it could not know.** It said
  "nothing was flipped there" about a host that had been flipped. The flip now
  announces itself and the message points at that line.
- **Probing for passwordless sudo, three times wrong** — see the box under
  "Updating many machines at once".

### Setting a host up the first time

```bash
scripts/deploy.sh --print-unit > unit
ssh HOST "sudo useradd -r -s /usr/sbin/nologin gg2 || true"
ssh HOST "sudo mkdir -p /opt/gg2-server/releases /opt/gg2-server/shared"
# The deploy user owns the tree it writes; the SERVICE user owns only shared.
ssh HOST "sudo chown -R \$USER /opt/gg2-server"
ssh HOST "sudo chown gg2:\$USER /opt/gg2-server/shared && sudo chmod 2775 /opt/gg2-server/shared"
ssh HOST "sudo tee /etc/systemd/system/gg2.service > /dev/null" < unit
ssh HOST "sudo systemctl daemon-reload && sudo systemctl enable gg2"
scripts/deploy.sh -H HOST
```

⛔ **Do not `chown -R gg2` the whole tree.** The deploy writes `releases/` and the
service only reads it, so the two directories want different owners. A tree owned
by the service user fails the preflight with "cannot write `<root>`" — after the
unit and the sudoers rule are already installed, which makes it look like a
permissions problem with those instead.

**`chmod 2775` on `shared/` is setgid and it is load-bearing.** The service writes
there as `gg2` and the deploy account edits `gg2.ini` there as itself; setgid keeps
the group on anything either of them creates. Without it an in-place edit that
replaces the file — `sed -i` does — silently drops the group to the editor's own,
and the panel's settings page can no longer save. Nothing announces that: the
server reads the file fine and only a save fails. Give `gg2.ini` itself `664`
for the same reason.

The unit is printed rather than installed: writing to `/etc` on somebody's
server is a larger authority than "update the binaries", and the script
deliberately does not take it.

The ssh account needs two things and nothing else: ownership of
`/opt/gg2-server`, and permission to restart the unit. The script reaches for
`sudo -n` on `systemctl` alone, so one sudoers line covers it.

```
deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart gg2, /usr/bin/systemctl is-active gg2, /usr/bin/systemctl show gg2 *
```

⚠️ **The path has to be the one `command -v systemctl` prints on that host**, and
a wrong one fails in the worst way: sudoers matches the literal path, so the rule
parses clean under `visudo -c`, installs without complaint, and the preflight still
reports "cannot run sudo without a password". `/usr/bin/systemctl` is right on
Debian and Ubuntu; `/bin/systemctl` is a different string even where `/bin` is a
symlink to `/usr/bin`. The preflight's own failure message builds the line with
`command -v`, so copying it from there rather than from here cannot get this wrong.

Anything needing this password has to be run from a real terminal. A Claude Code
`!` command allocates no TTY, so `sudo` has nowhere to prompt and fails with
"a terminal is required to read the password".

`--no-sudo` turns even that off, for an ssh user that is already root or for a
`--user` unit.

**`shared/` is where anything the server writes belongs.** `ProtectSystem=strict`
with a `ReadWritePaths` naming only `shared` makes that mechanical rather than a
convention: a release directory is read-only to the service, so a deploy cannot
lose anything the server put there. Custom maps and their nav graphs go there
too, reached with `--maps` and `--botnav` — and both **override** rather than
add, so seed those directories from a release's `maps/` and `nav/` once or the
23 shipped maps and 25 graphs stop being found.

### A systemd unit

The flat form, for a single machine installed by hand. For the release
layout above, take the unit from `scripts/deploy.sh --print-unit` instead:
it is this file with `WorkingDirectory` and `ExecStart` going through
`current`, and `ReadWritePaths` naming `shared` alone. Everything the
comments below explain applies to both.

```ini
[Unit]
Description=Gang Garrison 2 server
After=network-online.target

[Service]
Type=simple
User=gg2
WorkingDirectory=/opt/gg2-server
ExecStart=/opt/gg2-server/gg2-server --bots 8 --players 12

# on-failure, NOT always, and the difference is the exit code. The server
# exits 3 when its tick loop has failed repeatedly (TickFaultBudget), which is
# the case a restart actually fixes — a fresh process is the only thing that
# reliably clears corrupted in-memory state. An ordinary shutdown exits 0, and
# `always` would restart that too, so an operator stopping the server on
# purpose would be silently undone.
Restart=on-failure

# Not RestartSec=0: a listening port with connections in TIME_WAIT refuses
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

# ProtectSystem=strict makes the WHOLE filesystem read-only, so every path
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

**The firewall and kernel settings are not in this file and this process
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

**`nav/` ships in every build and every publish, with no flag.** 25 graphs
and `manifest.tsv`, 1.2 MB, beside `maps/` — plus one `<key>.dist.gz` per graph,
**1.62 MB together**, holding the next-hop tables A\*'s exact heuristic reads.
**A publish that carries the graphs and not the tables still works** and is
slower at every map change (377 ms against 62 ms on `ctf_truefort`); the server
logs which of the two happened.

**Measured, on all 25 graphs:**

| | working tree | compressed |
|---|---|---|
| hex text, as the generator writes it | 35.68 MB | 1.112 MB |
| **gzipped, as shipped** | **1.11 MB** | 1.11 MB |
| a hand-rolled packed binary, *rejected* | 4.06 MB | 1.111 MB |

**The third row is why there is no custom nav format.** A binary encoding
shrinks the tree 8.8x and what actually ships by **0.1%** — everything it removes
is redundancy deflate was already removing, because the files are hex-encoded
`ds_grid_write` blobs (32 bytes of file per numeric value, for values that are
mostly small integers). It would cost a `NAV_CACHE_VERSION` bump, a new writer,
a new reader and a new way for header and payload to disagree, to save 3 MB of
disk and about 8 ms per map change. Do not re-propose it without a reason that
is not size.

**Decompression costs about a millisecond**, once per map change, against a
33.3 ms tick: the largest map (`ctf_conflict`) loads in 9.14 ms plain and
10.29 ms gzipped. **That assumes one graph per map change**, which is what
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

**A plain `.txt` still WINS over a shipped `.txt.gz`.** An outside generator
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

**It is a behaviour switch, not a performance one.** Without it match
dispersion falls from 0.785 to 0.634 and nothing replaces it. What it buys is a
live server that routes exactly the way `eval` and every training rollout
already do — those have never had occupancy — which is worth something if you
are comparing live play against measured runs.

### `--no-nav-distances`: the straight-line heuristic back

**The exact heuristic is ON by default since 2026-09-04**, and this turns it
off. The tables are read from `nav/<key>.dist.gz` — written by `gg2-server
navgen`, 1.62 MB for all 25 graphs — and only derived when a map has no file.
They give A\* an exact heuristic instead of a straight-line guess. A\* is about 30%
of a twelve-bot tick, so this is real — but the search is *weighted*, so an exact
heuristic changes **which** route comes back as well as how fast, and a server
run with it is not route-comparable to one without.

Measured over 21 maps: route cost 103.6% → 99.1% of the cheapest route (better,
because the incumbent A\* is deliberately suboptimal), dispersion 0.634 → 0.613,
and 3 of 21 maps regress.

**Turning it OFF is a behaviour change, not a slower run**: the search is
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

**377 ms against 62 ms is the whole point of shipping them**, and the same
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

A **per-map** miss is a different message — `no nav cache for <map> area <n>`
— and appears only with `--no-navgen`, because otherwise the graph is about to
be built.

**And a third**, when a graph and its map have drifted apart:

```
nav graph for <map> area <n> was built against different geometry and has been
refused. Rebuild it with `gg2-server navgen --map <map>`, or delete
nav/manifest.tsv's line for it to load it anyway
```

**That one is then rebuilt AND overwritten on disk, even without
`--navgen-save`** — the file describes terrain that is not there, so leaving it
means every restart refuses it again and any other tool reading the directory
still gets the wrong graph. A map that simply never had a graph is not a fault to
correct, and its generated graph stays in memory.

#### `manifest.tsv` — which map each graph was built from

**A nav graph is keyed on the map's NAME**, deliberately (`NavCache.Key`
explains why the MD5 cannot be the key) — a graph built against an older version
of a map would otherwise load perfectly and hand bots terrain that is not there.
The header's mask dimensions catch a resize and nothing else; the symptom of a
mismatch is bots walking into walls, which reads as a bot bug.

`manifest.tsv` records, per graph, a hash of **the area's walkmask and its
entities** — the inputs a graph is built from, so art edits and PNG re-encodings
do not cry wolf. A mismatch refuses the graph; **a graph with no line is not
checked**, which is what keeps an operator's own maps working.

**The entities are in the hash because the walkmask alone is not enough**:
`cp_dirtbowl`'s three areas share one mask and carry different graphs (835, 837
and 837 nodes), so a mask-only hash would give all three the same answer and
could not see a door or a gate move. `NavManifestTests.EntitiesAreInTheHash`.

Regenerating nav means regenerating this:

```bash
GG2_UPDATE_NAV_MANIFEST=1 dotnet test Gg2.sln --filter NavManifestTests
```

**That run fails on purpose** after rewriting the file, so a manifest cannot
be regenerated by a CI job that happened to have the variable set and be reported
as green. Re-run without it, check the diff is the change you meant, and say so
in the commit.

**What is NOT gated: that a publish actually carries `nav/`.** There is no
test for it; a reader who needs this to be true should run `dotnet publish` and
look. The csproj glob is `..\..\nav\*` rather than a per-extension one, so a
container is less likely to silently stop shipping — but that is not a
substitute for a check.

### 2. SIGTERM, not Ctrl+C

`Console.CancelKeyPress` covers SIGINT, which is what an operator watching a
terminal sends. systemd's default `KillSignal` is **SIGTERM** and `docker stop`
sends SIGTERM; `ShutdownSignals`, wired in `Program.Main`, handles both.

**Why it is worth handling rather than letting the process die.**
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

**That line is a no-op on Linux**, which is worth knowing before anybody
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

**Linux is the more correct side and this is not being "fixed".**
`InternalMaps` already matches with `StringComparer.Ordinal`, because the stock
client's own switch matches the literal string — a server that accepted
`CTF_Truefort` would be accepting a name the client will not. The error message
names the full path it looked for, which is enough to diagnose it.

The case to watch is a `gg2.ini` or rotation file **copied off a Windows host**,
where a mis-cased entry has been working by accident.

---

### A log file, if you want one

**You do not want one under systemd or Docker.** Both already capture stdout
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

Under `ProtectSystem=strict` the log directory has to be in
`ReadWritePaths` or the server starts and fails on the first write.

---

## Runtime knobs

### Garbage collection

`Gg2.Host.csproj` sets `ServerGarbageCollection=true` and
`ConcurrentGarbageCollection=false` (`docs/ARCHITECTURE.md`, "Runtime settings").
Server GC sizes its heaps per core and is tuned for throughput; the tick loop is
single-threaded and latency-sensitive.

**`DOTNET_gcServer=0` is a precaution, not a measured win.** Working set
differed by under 3 MB between the two modes (52.5 MB server GC / 54.9 MB
workstation) and no pause-time difference was measured; the tick allocates
approximately nothing anyway (`docs/BOTAI.md`) so gen2 collections should be
rare either way. The csproj default is deliberately left alone because flipping
it on an unmeasured hunch would contradict a documented decision.

**What would settle it**: `--tick-stats` on a real 1-2 vCPU box, with and
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
