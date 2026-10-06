# aimscan

Replays `--record` traces through `AimAnalysis` and writes every session's
counters, at every lag, as CSV. It is how the thresholds in
`AimAnalysis.Judge` were set and how they should be re-checked.

```
dotnet build tools/aimscan -c Release
dotnet tools/aimscan/bin/Release/net10.0/aimscan.dll <trace-or-dir>... [--maps <dir>] > scan.csv
```

Not in `GG2.sln`; build it explicitly.

One row per (trace, slot, session, arm, lag). A session is one person in one
slot of one trace; a roster compaction is followed by name. The arms:

| Arm | Input |
|---|---|
| `real` | what was recorded, for people and bots |
| `lock`, `lock3`, `lock6`, `lock10` | the person's input with the aim replaced by the nearest visible enemy's origin, plus 0, 3, 6 or 10 px Gaussian jitter |
| `trigger` | the person's aim, with attack held exactly while the aim ray is on a visible enemy |
| `esp` | the person's aim, except with no visible enemy it follows the nearest hidden one (3 px jitter) |

Every synthetic arm aims at positions `2 + slot % 4` ticks old, as a lagged
client sees them, through a whole-pixel pointer, so it also passes `AimCheck`.

**Reading it.** For each session and signal, take the lag with the highest
Wilson lower bound (z = 3.29) among lags meeting the signal's minimum sample,
and compare it with the threshold, exactly as `AimAnalysis.Judge` does. The
`real` human rows are the false-positive check and must flag nothing; the
synthetic rows say what is caught. `docs/HARDENING.md`, "Aim analysis", has
the last run's numbers.

The traces name real players: keep them, and the CSV, out of the repository.
