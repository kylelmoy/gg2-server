# `soak-players` — a fleet of synthetic players

Real TCP clients that join, play, switch teams and classes, and leave, over and
over, against a running server. Built explicitly; not in `Gg2.sln`, the same way
`tools/bench` is not.

```powershell
dotnet build -c Release tools/soak/soak-players.csproj
./tools/soak/bin/Release/net10.0/soak-players.exe --port 8191 --players 8 --seconds 240 --seed 7
```

| Flag | Default | |
|---|---|---|
| `--host` | `127.0.0.1` | |
| `--port` | `8191` | the server's game port |
| `--players` | `4` | fleet size |
| `--seconds` | `300` | wall clock |
| `--seed` | `1` | each member's stream is derived from it, so a run repeats |
| `--name-prefix` | `soak` | roster names |
| `--log` | — | also append the tally lines here |

## The server needs three flags, or this looks broken rather than refused

Every member connects from `127.0.0.1`, and two separate per-address limits bite
there — one on how many connections an address holds at once, one on how fast it
opens them:

| Flag | Default | Why it refuses a fleet |
|---|---|---|
| `--multi-client-limit 0` | 3 | concurrent connections one address may hold — and a watching GM8 client is one of them |
| `--connect-rate 0` | 10/min | connections one address may open per minute, and churn is nothing but reconnects |
| `--players <n>` | 10 | seats for fleet + bots + the watching client |

```
gg2-server --port 8191 --control-port 8192 --bots 4 \
           --players 24 --multi-client-limit 0 --connect-rate 0
```

The tally reports `refusedServerFull` and `refusedPerAddress` separately so a
refusal names which one it was.

⛔ **`--multi-client-limit 0` does not mean "no limit". It means "refuse every
non-loopback connection."** `ExceedsMultiClientLimit` counts the candidate
itself, so the test is `1 > 0` for the first connection from any address. It
works above only because **loopback is exempt and never reaches the check** — a
local fleet would be let in whatever the value were. Point the fleet at a remote
server with this setting and the server refuses everyone with
`KICK`/`KICK_MULTI_CLIENT`, which reads as the server being broken. Use a number
above the fleet size instead, e.g. `--multi-client-limit 64`.

⚠️ **`--connect-rate 0` really does mean off**, so the two flags read the same
digit in opposite directions. `ConnectionLimits.ConnectionBurstPerAddress` says
"Zero turns the rate limit off"; `ServerOptions.MultiClientLimit` has no such
escape value.

## Why not `--bots`

A bot is an `IInputSource` with no socket. It never reaches `RESERVE_SLOT`, the
join burst, `ClientPlayer.SkipBroadcastBytes`' flush ordering, or a disconnect
driving `PLAYER_LEAVE`. So `soak-transitions.js --plan churn`, which moves the bot
fill up and down, exercises roster compaction inside the server and nothing on
the wire. These are real clients, so a join and a departure cost what a player's
cost.

Both departures are exercised, and they are different server paths:

| Share | Close | Server logs |
|---|---|---|
| 70% | `PLAYER_LEAVE`, then FIN | `left (peer closed the connection)` |
| 30% | RST, via a zero linger | `left (ConnectionReset)` |

A plain socket close sends FIN, which is the orderly path one message later — so
without the zero linger the tool would claim two departures and cover one. The
tally's `politeLeaves`/`abruptLeaves` should match the server log's two reasons
exactly; that agreement is the cheapest check that the tool is doing what it says.

## This tool is not the oracle

It completes the handshake and then **drains and discards**. Draining is not
optional — a client that stops reading backs the server's send buffer up until
the connection dies for a reason unrelated to the test. But it deliberately does
not parse the state stream: a second, unverified implementation of the wire would
fail in its own bugs and report them as the server's.

The oracle is a stock GM8 client attached to the same server. Two things to watch,
both of which cost nothing while they hold:

**Its own index, which is what roster compaction threatens.** A departure from a
low slot renumbers every slot above it, and the client tracks that with one line —
`ClientBeginStep.gml:218`, `if(playerID < global.playerID) global.playerID -= 1`.
Nothing inside this repo can check that line is still right. Install as a
`gg2_watch`: 1 means checked and holding, 0 means the index has slipped, 2 means
not currently checkable (a map change empties the list).

```
execute_string("global.wIdent = 2; if (global.myself != -1) { if (global.playerID >= 0) {
  if (global.playerID < ds_list_size(global.players)) {
    if (ds_list_find_value(global.players, global.playerID) == global.myself)
      global.wIdent = 1 else global.wIdent = 0 } } }") * 0 + global.wIdent
```

A watch logs only on change, so a single line of `1` for a whole run is the pass —
read it alongside `ds_list_size(global.players)`, which should log many changes.
If the roster watch is quiet too, the client was not following the churn and the
run says nothing.

**`agentDebugDesync`**, already patched into `deserializeState.gml` by
`gg2-agent`, which records a declared-count mismatch to the bridge log instead of
only putting it on screen.

## Measured

240 s, 8 members, seed 7, against 4 bots and one watching GM8 client: **72 joins
and 72 leaves** (45 FIN, 27 RST), 670 team changes, 319 class changes, no
refusals, no server faults. The client held roster sizes 4 through 10 across 143
transitions, its index invariant never left `1`, no `DESYNC` was recorded and no
dialog was raised. Prediction stayed tight throughout — x off by 0.11 px on
average, hp by 0.02.
