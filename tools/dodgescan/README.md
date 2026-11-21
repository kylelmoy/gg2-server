# dodgescan

Replays `--record` traces and writes one CSV row per (enemy rocket, character
it came within 600px of): whether it was on target when first seen, its warning
in ticks, what became of it (`direct`, `splash`, `miss`), and the first tick the
target pressed JUMP or changed LEFT/RIGHT after it appeared.

```
dotnet build tools/dodgescan -c Release
dotnet tools/dodgescan/bin/Release/net10.0/dodgescan.dll <trace-or-dir>... [--maps <dir>] > dodge.csv
```

Not in `GG2.sln`; build it explicitly. `--maps` defaults to this repo's `maps/`,
and a map not found there is looked for in `maps/duel/`, so duel-server traces
need no flag.

"On target" and "direct" are the scripted dodge's own body model
(`BotReflex.RocketDodge`), the same one `BENCH_DODGE` uses, so the two read on
one scale. **The reaction column is an upper bound, not a reaction time**:
people change their keys all the time, so a person's share "reacted within 2
ticks" (about 15%) is their ordinary key-change rate. Read the direct-hit share
of on-target rockets with 5-8 ticks of warning, against the same shooter where
possible: a better shooter lands more, whoever is dodging.

`docs/TRAPS-BOTS.md`, "the policy's rocket dodge", has what the duel server's
soldier recordings came to. The traces name real players: keep them, and the
CSV, out of the repository.
