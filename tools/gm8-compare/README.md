# `tools/gm8-compare` — this server against the stock GM8 server

Measures both servers under the same client load on the same machine: process
CPU, memory, startup time, and broadcast timing as a client sees it. The
README's "Performance" section is its output. Windows only, because the GM8
server is.

```powershell
./tools/gm8-compare/setup.ps1 -Gm8Build <reference>\Source\build -WorkDir <scratch>
./tools/gm8-compare/all.ps1 -WorkDir <scratch>              # ~22 minutes
./tools/gm8-compare/run.ps1 -Server gm8 -Scenario play8 -WorkDir <scratch>   # one run
```

`setup.ps1` publishes this server, `tools/loadprobe` and `tools/soak` in
Release, and **copies** the GM8 build with `UseLobby=0` and `PlayerLimit=24`.
It never writes to the source build: its `gg2.ini` is the owner's config, and a
benchmark server left announcing itself to the public lobby is the failure that
rule prevents.

## Scenarios

| Name | Load |
|---|---|
| `idle` | nobody connected |
| `spec1` | one `loadprobe` spectator |
| `spec16` | sixteen `loadprobe` spectators |
| `play8` | eight `soak-players` members (seed 7) and one `loadprobe` client sending input |

Both servers run `ctf_truefort` on port 8190, one at a time. This server runs
with `--bots 0`, because stock GM8 has no bots.

## Output

`results.csv` has one row per run; `logs/` has each run's `loadprobe` summary
(inter-arrival percentiles, `reads/s`) and `soak-players` tally.

| Column | Meaning |
|---|---|
| `startup_ms` | process launch until port 8190 accepts a connection |
| `cpu_pct_core` | server process CPU time over the window, as % of one core |
| `cpu_max_1s` | the busiest one-second sample |
| `ws_mb_*`, `private_mb_max` | working set and private bytes |

## Reading it

- **`idle` is not a like-for-like row.** This server slows its tick almost to
  a stop with nobody connected; GM8 runs its full frame loop. `spec1` is the
  fair "awake, nothing happening" comparison.
- **GM8's floor includes a window.** Its dedicated mode still creates one and
  loads every asset, which is most of its idle CPU and memory. That is the
  reference server's real cost, not an artefact of the harness.
- **Check the soak tallies match** between the two `play8` arms (`joins`,
  `teamChanges`, zero refusals) before comparing their CPU. A refused fleet
  measures a lighter server.
- **Check `reads/s` is 30.0** before reading any percentile; `tools/loadprobe/README.md`
  says why.
- **Nothing else should be running.** Both arms measure process CPU on a shared
  machine, and the arms alternate so drift lands on both, but a busy box still
  inflates both.
