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

## What a miss costs, in pixels

`--cost` runs the counterfactual: at tick t, copy the replayed world into two
scratch simulations, run both forward, one on the inputs the recording carries
and one on tick t's inputs held, and compare. The prediction arm is compared
against the other simulation and never against the recording, so replay drift,
anchoring and the wire's `x*5` quantisation are common to both arms and cancel.

    dotnet run --project tools/inputpredict -c Release -- --cost <directory>
    dotnet run --project tools/inputpredict -c Release -- --cost --control <directory>

`--control` feeds both arms the recorded inputs. Every error must then read
`0.00`; it is what says the instrument measures the prediction rather than two
simulations drifting apart on their own. Measured: exactly 0.00 at every
staleness, max 0.0.

**Staleness is one less than the window.** The first tick after an anchor is
simulated from the input the node has just received, so it is correct in both
arms by construction; only the ticks after it are predicted.

### Measured, 8 fleet recordings, 2026-09-17

Euclidean position error at the end of one prediction cycle, 16,350 human
samples per row and 85,000 bot:

| stale | human mean | p90 | p99 | max | over 8px |
|---|---|---|---|---|---|
| 1 tick | 0.14 px | 0.00 | 6.09 | 14.6 | 0.23% |
| 2 ticks | 0.45 px | 0.05 | 14.00 | 33.5 | 1.36% |
| 3 ticks | 0.95 px | 2.38 | 21.40 | 46.2 | 3.54% |
| 5 ticks | 2.61 px | 9.71 | 34.00 | 76.0 | 11.88% |

Bots are worse at every row — 0.67 px mean and 1.44% over 8px at two ticks
against the people's 0.45 and 1.36% — the same direction as the miss rates
above.

**The comparison that matters** is against what a GG2 client already absorbs.
`docs/TRAPS-LIVEGAME.md`, "What the client's prediction error actually is",
measured a stock client's own dead reckoning at 251,576 corrections: x mean
0.70 px, 1.3% over 8 px. A two-tick prediction here reads 0.45 px mean and
1.36% over 8 px. Not the same statistic — this is Euclidean over both axes,
that one is per-axis — but the same order, which puts the correction a
transatlantic prediction would need inside the range the client is already
snapping through several times a second in ordinary play.

⚠️ A correction is not free just because it is small: these are averages over
every character, and the tail is what a player sees. At two ticks, 1 sample in
74 exceeds 8 px.

## Is there a better cheap guess than "keep holding"?

`--predictor <name>` swaps what the prediction arm assumes about a tick it has
not heard yet. Measured on the same 8 recordings, human rows, mean pixels at
two ticks of staleness:

| predictor | mean px | over 8px | |
|---|---|---|---|
| `Hold` | 0.45 | 1.36% | repeat the last input — the baseline |
| `HoldNoJump` | 0.45 | 1.36% | identical to `Hold`, to the digit |
| `MoveOnly` | 0.45 | 1.36% | dropping attack/special changes nothing measurable |
| `Release` | 1.90 | 2.02% | 4x worse |

**No cheap rule beats repeat-last-key**, and one of them cannot differ from it
at all: `HoldNoJump` is byte-identical because a jump fires on the PRESSED edge
(`CLAUDE.md`), so a held jump key never fires twice. A missed jump is always a
press the predictor did not know about, never a stale one it kept holding.

Dropping the attack bits does not move a character either, which is the
measured version of why holding fire until it is confirmed costs nothing
positionally.

So the headroom is not in rules over the last keystate. It is in predicting
*changes* — when a player releases or presses — which needs either context or a
model, and the numbers above are what such a thing has to beat.

## What a correction looks like when it happens

The unconditional rows above mix every exact hit in with the misses, so they
answer "what is the average correction" rather than "what does a correction
look like". Restricted to windows where a key really was mispredicted, human
rows:

| stale | share of windows | mean px | p50 | p90 | p99 | max |
|---|---|---|---|---|---|---|
| 1 tick | 9.7% | 1.39 | 0.76 | 6.29 | 11.40 | 14.6 |
| 2 ticks | 17.4% | 2.55 | 0.83 | 8.00 | 16.30 | 33.5 |
| 3 ticks | 24.4% | 3.87 | 1.07 | 11.71 | 23.26 | 46.2 |
| 5 ticks | 34.9% | 7.45 | 3.24 | 22.20 | 40.20 | 76.0 |

At the transatlantic figure — two ticks — a correction is due on about one
window in six, is under a pixel half the time it is due, and exceeds 8 px on
8% of those, or about 1.4% of all windows. The worst seen is 33 px.
