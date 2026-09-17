# inputpredict

How predictable is a player's next few ticks of input?

A rollback netcode design has to guess what a remote player is pressing while
their real input is still in flight — one way across the Atlantic is 2-3 ticks
at 30 Hz. The guess it starts from is "they are still holding what they were
holding". This reads `--record` recordings and measures how often that is
wrong, separately for the people and the bots the same recordings label.

    dotnet run --project tools/inputpredict -c Release -- <trace-or-directory>...

Not in `Gg2.sln`; build it explicitly, like `tools/bench`.

## Measured, 48 fleet recordings, 2026-09-17

460,689 human samples and 2,968,087 bot samples, movement keys only
(left/right/jump/down — a mesh predicts those and holds fire until it is
confirmed, because a shot cannot be retracted on a stock client).

| horizon | human miss | bot miss | human aim p90 |
|---|---|---|---|
| 1 tick | 7.2% | 13.7% | 2.3 deg |
| 2 ticks | 13.2% | 19.3% | 4.7 deg |
| 3 ticks | 18.1% | 22.8% | 7.4 deg |
| 5 ticks | 25.6% | 28.6% | 13.0 deg |
| 10 ticks | 35.3% | 35.7% | 28.9 deg |

Humans hold a movement key state for a median of 5 ticks (mean 12.9); the bots
change theirs twice as often, median 2 (mean 7.1).

**People are more predictable than this project's own bots**, by six points at
one tick and five at two — so a prediction scheme tuned against a bot-filled
server is being graded on a harder problem than it will face. Not explained,
only measured.

Human aim is nearly still at the median and flicks hard in the tail: p99 is
86 deg at two ticks, 123 deg at three. That is why aim-dependent events are
worth holding rather than predicting.

⚠️ **Three people.** The recordings carry three human names across 48 segments,
so this is an estimate of how *these* players behave, not of humanity. A fourth
regular could move it.
