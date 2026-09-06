# `tools/abuse/` — running the attacks at a real server

`docs/HARDENING.md` says what the server defends against. This is what proves
it, against a running process rather than a socket pair in a unit test.

⚠️ **Read the traps below before quoting a number out of this.** Two of the five
attacks cannot reach their limit over loopback at all, and both report so rather
than reporting a pass — a harness whose "not dropped" reads like a failure is
how a real regression gets waved through next time.

Not in `Gg2.sln`, like `tools/bench/`. It needs Python 3 and nothing else.

## Running it

Start a server with the control port on, then attach:

```
gg2-server --port 8190 --control-port 9190 --bots 8 --players 16
python tools/abuse/flood.py
```

It **attaches rather than starting a server**, so the configuration under test
is the operator's own — which is the point, since the whole question is whether
a given set of limits holds.

```
--seconds N     how long each attack runs (default 10)
--clients N     sockets for the attacks that take several (default 3)
--only NAME     one of: connect-flood, deaf-client, garbage, send-flood, slot-squat
```

⛔ **The only hard gate is `tickFaults`.** Everything else is a number to read.
A non-zero fault count means the server absorbed an exception during the run,
which is a bug whose stack trace is in the log — the harness exits 1 for it and
for nothing else.

## Measured, 2026-09-06

Windows 11, Release build, `--bots 8 --players 16 --handshake-deadline 60`,
eight seconds per attack.

| Attack | Result | Against |
|---|---|---|
| connect-flood | 1500 sockets opened; the server held exactly **128** | `MaxJoiningConnections` (128) |
| garbage | dropped after **5120 bytes** sent | `MaxInvalidCommandBytes` (4096) |
| send-flood | dropped after **231 KB** sent | `MaxReceiveBufferBytes` (256 KB) |
| slot-squat | seats released after **60s** | `--handshake-deadline 60` |
| deaf-client | not dropped — see the trap below | `MaxSendBacklogBytes` |

And the server, throughout all of it:

```
worst tick work seen:  1.379ms of 33.333ms budget
peak managed heap:     17.5MB
late ticks:            0 (worst overrun 0ms)
tick faults:           0
```

**That is the headline.** A fifteen-hundred-connection flood plus four
concurrent floods cost about **4% of one tick's budget** and **4 MB of heap**,
and not one tick ran late. The limits are doing the work in the place they were
supposed to: the connections never become the server's problem.

⚠️ **What this does not establish.** One machine, loopback, one configuration,
eight seconds. It is not a load test, there is no distributed component, and
nothing here says what happens at a thousand connections a second from a
thousand addresses — which `docs/HARDENING.md` names as out of scope for a
single process and belonging in the firewall.

## The traps, which are built into the modes

### `deaf-client` cannot reach its limit over loopback

**Expected output: "not dropped".** Three deaf clients on an eight-bot server
for sixty seconds, with `--max-send-backlog` at its 65538-byte minimum, still
did not trigger it.

The kernel's loopback buffers absorb everything the server writes, so
`ClientConnection.Flush` keeps succeeding and `_pending` never grows. The
harness sets `SO_RCVBUF` to 2048 to try to close the window; Windows ignores it
on loopback.

The mechanism is proven instead by
`ConnectionLimitTests.AClientThatStopsReadingIsDroppedRatherThanBufferedForEver`,
which pushes ~32 MB through a real socket pair — enough to overrun even those
buffers — and asserts both the fault and that the queue never passed the limit.
Over a real link to a genuinely stalled peer the window closes at the *peer* and
the backlog fills in seconds.

What the live run does establish is weaker and still worth having: deaf clients
cost the server no tick time and no unbounded heap while they sit there.

### `slot-squat` takes ten minutes at the default

`MaxHandshakeDuration` is ten minutes, so an eight-second run will always report
"still held" — which is correct behaviour, not a failure. Run the server with
`--handshake-deadline 60` and the harness with `--seconds 75` to see it lapse.

⚠️ Do not "fix" this by lowering the shipped default. Ten minutes is sized
against the slowest real handshake, which is a custom map download; a deadline a
real client can hit is an outage the operator has to debug.

### `connect-flood`'s "still open" count is not a server number

The client's `connect()` returns as soon as the *kernel* completes the TCP
handshake, which happens whether or not the server process has accepted the
socket. So "still open: 1500" means the kernel took them, and "server joining:
128" is the number that matters — the process held its cap and left the rest in
the accept backlog, which is exactly the design.

### Run the attacks one at a time when a number looks odd

The harness sleeps two seconds between attacks so one run's dropped sockets are
not still in `TIME_WAIT` during the next one's. That is enough on an idle
machine and may not be on a busy one; `--only` is there for when a number needs
to be trusted.

### The tick figure is an EMA, not a benchmark

`tickMs` from the control port is an exponential moving average over whatever
the server was doing, map loads included — the same number the panel shows, with
the same caveat. It is the right instrument for "did this attack cost the tick
anything" and the wrong one for "what does a tick cost". `tools/bench/` is where
a per-tick cost comes from.
