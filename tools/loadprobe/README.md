# `tools/loadprobe` — what a client actually experiences

Not in `Gg2.sln`. Build it explicitly; `dotnet test` should not build a load
generator and CI should not run one.

```powershell
dotnet publish -c Release tools/loadprobe/loadprobe.csproj -o <somewhere>
```

`tools/bench` measures the simulation's own cost with no network and no clients.
This measures the other half: whether the broadcasts a real client is waiting for
actually arrive on time, over a real network, from a real host.

## What it is for

The question it was built to answer is whether one non-dedicated vCPU is enough
to host a game people would want to play. `docs/DEPLOY.md`'s hardware table
cannot answer that: it is measured on Windows, pinned to one core, with **no
clients connected**, and it says outright that network is not measured.

## The three layers, and why one of them alone says nothing

| File | Layer | Answers |
|---|---|---|
| `client.tsv` | this box | when broadcasts ARRIVED |
| `perf.tsv` | the server | what it thought it was doing, per second |
| `host.tsv` | the host OS | CPU steal, load, memory, per-connection RTT |

⚠️ **A jitter number from `client.tsv` alone cannot say whose fault it is**, and
on a shared vCPU the likeliest cause is not the server. `perf.tsv` says whether
the server was late; `host.tsv`'s `cpu_steal` says whether the box was taken
away from it; `rtt_mean`/`rtt_max`/`retrans` say whether the network did it.
`LateTickMonitor`'s own remarks have the signature to look for: work near zero
with lateness in the tens or hundreds of milliseconds is a scheduling problem and
no amount of optimising the server moves it.

## Traps built into this code

1. **The reader blocks; it does not poll.** `Gg2.Parity.Recorder` already joins a
   remote server and timestamps its chunks, and was not reused for one reason: it
   polls with `Thread.Sleep(1)`, which on Windows rounds up to the system timer
   resolution — 1 to 15 ms. The signal here is a 33.3 ms interval, so a
   sleep-driven reader injects jitter the size of the thing being measured and
   nothing downstream can separate them again.

2. **`reads/s` is printed next to the percentiles because it licenses them.**
   Nothing is framed — the stream interleaves chat, joins, kills and plugin
   packets with the update tiers, so framing it means writing a real client's
   reader. Instead the server's `NoDelay` (`ClientConnection.cs:36`) makes one
   tick's broadcast one write and normally one read, and `reads/s ≈ 30` is the
   evidence that held on this run. Well under 30 means reads coalesced and the
   gaps are inflated; well over means a broadcast arrived split and they are
   deflated. Read the percentiles only after reading that number.

3. **A fault at teardown is not a fault.** The driver wakes the blocked reader
   threads by disposing the socket under them, so every client reports
   "connection aborted" at the end of a clean run. Those are suppressed while
   the run is cancelling, because otherwise every run carries faults and a run
   where the server really dropped clients looks identical.

4. **The probe's own CPU is in the output.** If this box is the bottleneck the
   gaps are this box's, not the server's. It has been 0.2–0.3% of one core for
   up to 24 clients, which is the evidence that it is not.

5. **A spectator loads the fan-out path, not the simulation.** A probe client has
   no character, so it costs the server a roster entry and a per-tick send and
   nothing else. That is deliberate — it isolates the per-client cost — but it
   means a sweep of probe clients is **not** a sweep of players in a match.

6. **A probe client counts as a human, so it takes bots away.** Bot population is
   `min(MaxBots, FillToPlayers - humans)`, so adding clients *reduces* the
   character count unless the configuration stops it. Pin the simulation by
   setting `FillToPlayers` well above `MaxBots` (48 against 24), or the sweep
   changes two variables at once and measures neither.

7. **The sampler competes for the core it is measuring**, and there is no way
   around that on a one-core box. It is kept to three small `/proc` reads a
   second with `ss` only every fifth, the panel is read once per step rather than
   once per second, and its own CPU time is the last column of `host.tsv`.

8. **The server idles when empty.** `ticks` in `perf.tsv` is 2–3 a second with
   nobody connected and 30 with somebody, so any per-second server figure from a
   step's warmup or from between steps is from a different duty cycle. Compare
   like with like.

## Running one

The sweep only measures. It deliberately does **not** configure the server, so a
run cannot quietly measure a different server than the one that was set up. The
host needs, for the duration: `UseLobby=0` (so a real player cannot wander into
it or contaminate a step), `MultiClientLimit` above the client count (the default
is 1 — the reference's answer to a household behind one NAT), and
`ConnectionBurstPerAddress=0` (the default of 10 a minute refuses the eleventh
client partway through a ramp). `PlayerLimit` bounds the top step: the code
default is **10**, not the 24 in the settings table.

```bash
tools/loadprobe/run-sweep.sh gg2-us-west 203.0.113.7 out/ 1 2 4 8 12 16 20 24
```

One step on its own:

```bash
loadprobe --host <addr> --clients 8 --seconds 90 --warmup 10 --out client.tsv
```

⚠️ **Restore the host's configuration afterwards.** Keep a hashed backup of
`shared/gg2.ini` and diff against it, rather than reverting from memory.
