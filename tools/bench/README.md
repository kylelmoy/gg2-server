# `tools/bench` — the measurement instrument

Not in `Gg2.sln`. Build it explicitly; `dotnet test` should not build a benchmark
and CI should not run one.

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
dotnet publish -c Release tools/bench/bench.csproj -o <somewhere>
```

⚠️ **Publish to a fresh directory, not over a running one.** A background
`bench.exe` holds its own file, `dotnet build` then silently cannot overwrite it,
and the next run measures the *old* binary. That cost a session an hour and looks
exactly like a change having no effect.

Every mode is selected by an environment variable and configured by more of them.
There is no argument parser; `Program.cs` is a chain of
`if (GetEnvironmentVariable(...) is { } x)`.

Common to most modes:

| variable | meaning |
|---|---|
| `GG2_REFERENCE` | the reference checkout (default `D:\code\Gang-Garrison-2`) |
| `GG2_NAV` | the bot nav cache directory |
| `NPZ` | a `gg2-rl` policy checkpoint |
| `BENCH_MAP`, `BENCH_FOCUS` | which map, or a comma-separated list |

## ⛔ Read this before adding a mode or reading a number

The whole reason this exists in the repo rather than in a scratch directory is
the set of measurement traps in `docs/BOTS.md`, "Measuring a bot change". The
four that are built into the code here:

1. **A statistic over a subset prints the subset size in the same row.** Never in
   a caption — a caption can be separated from the number by a copy-paste.
2. **`decided` is not the win share's denominator.** `MatchOutcome.Decided`
   includes a draw. Wins-plus-losses is the denominator, and on some maps it is a
   twentieth of the decided count.
3. **Warm past tier-1 before measuring.** Promotion is at 30 calls and the rejit
   is asynchronous, so a short warm-up measures tier-0 code. Modes here warm with
   200,000 iterations.
4. **Keep the control arm even when it looks redundant.** Two of the biggest
   findings of 2026-09-01 came out of a control row added only to validate an
   assumption.

## Modes that matter for bot work

| variable | what it does |
|---|---|
| `BENCH_PERMAP=<n>` | **the criterion.** Every shipped map, `n` matches an arm, policy against an all-scripted control. Prints absolute win and death counts, a two-proportion z per comparison, and `capped`. `BENCH_CAP=<minutes>` sets the match cap. |
| `BENCH_SWEEP=<n>` | **the mechanism.** Control plus policy at each engagement range in `BENCH_RANGES`. Prints the policy's share of control, both death-share estimators, the zero-death count, why each match ended, and — at `BENCH_CAP > 5` — the matches stratified by the 300-second boundary. |
| `BENCH_POLICYCOST=1` | one forward pass and one observation extraction, in nanoseconds. `OBS` points at an observations fixture. |
| `BENCH_EVAL=<n>` | the handover-rule and engagement-range comparison that chose the 150px default. |
| `analyse.py <table>` | reads a `BENCH_PERMAP` table and prints what it cannot show by eye: the decisive denominator per map, a filter-equality z, the maps ranked by **differential** censoring, and the death-share signs counted mechanically. |

⚠️ **`analyse.py` ranks on `decisive`, not on `decided`, and prints the `decided`
ranking underneath to show they disagree.** Ranking on `decided` sees the time-cap
layer and is blind to the draw layer — it clears a map whose two arms are 6%
decisive and flags one that is fine.

## Modes kept for reference, not current

These answered one question each and are unlikely to be run again. They are here
because they were never in git and would otherwise be lost, not because they are
maintained.

`Alloc` `Cmp` `Cold` `Digest` `Duel` `Geom` `Latency` `Obs` `Ray` `Reset`
`Search` `Tick` — the `Gg2.Env` throughput and allocation work
(`docs/BOTS.md`, "The environment seam"). `Dkoth` and `Dkoth2` verified one bug
fix and are superseded by `BENCH_PERMAP`.

⚠️ **A number from one of these is older than the code.** Re-run before quoting.
