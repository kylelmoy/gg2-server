# navscan

Replays `--record` traces and writes one CSV row per jump take-off made out of
combat (no live enemy within 300px), for people and bots alike, so the way each
side gets over an obstacle can be compared on the same maps.

```
dotnet build tools/navscan -c Release
dotnet tools/navscan/bin/Release/net10.0/navscan.dll <trace-or-dir>... [--maps maps] > takeoffs.csv
```

Pass Windows paths (`D:/...`) on Windows; a Git Bash `/d/...` path is not found
and the run reports `0 trace(s) scanned`.

Columns: what is ahead (`step`, `gap`, `flat`) and how far; the approach (speed
at take-off, the most in the 10 ticks before, ticks nearly still, reversals);
the flight (ticks, left/right key changes per tick, share of ticks with neither
key, speed reversals, mean speed); the height gained; and for a jump within 60
ticks of the last landing, the ticks on the ground between them. The header of
`Program.cs` has the definitions. Not in `GG2.sln`, like `tools/aimscan`.

The first reading: bots take off at about half the people's speed after braking,
change keys about ten times as often in the air, and pause about twice as long
between chained jumps.
