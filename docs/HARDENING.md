# Running this on the internet

The reference server was written for a game hosted off somebody's desktop for
their friends. This port inherited its transport shape faithfully — which is the
right call for parity and the wrong one for a socket anybody can reach.

This file is the security and robustness picture: what an attacker can reach,
what the process now defends against, what it deliberately does not, and what is
left to the machine it runs on. `docs/DEPLOY.md` is how to run it;
`docs/WEBADMIN.md` is the admin panel in detail.

**Nothing here is a claim that the server is secure.** It is a record of
which specific failures were found, fixed and tested, so the next reader can
tell what was checked from what was never looked at. The last section is the
second list and it is the more useful one.

---

## What an attacker can reach

| Surface | Exposure | Auth |
|---|---|---|
| **the game port** (`--port`, default 8190) | `[::]` dual-stack, plus `0.0.0.0` through it (IPv4-only where the host cannot bind IPv6), as the stock server listens. An IPv4 peer's `::ffff:` address is mapped down at capture, so bans and limits see plain IPv4 (`GameListener`). This is the one that has to be open. | optional `--password`, checked before anything but the protocol handshake |
| **the web panel** (`--web`) | off unless asked for; `127.0.0.1` unless `--web-bind` | mandatory password, generated if unset |
| **the control port** (`--control-port`) | off unless asked for; **`IPAddress.Loopback`, never `Any`** | none — reaching it means already being on the machine |
| **the lobby** (`--lobby`) | outbound only, off unless asked for | publishes the server's address to a third party |
| **reward auth** (`--rewards`) | outbound only, off unless asked for | relays players' user ids to a third party |

Two things follow from this table and both are load-bearing:

- **Everything optional is off.** A server started with no flags opens exactly
  one port and talks to nobody. That is the reference's shape too and it is
  worth keeping: every one of these was a decision somebody made, so it should
  be visible in the command line.
- **The game port is the interesting one.** The web panel was designed knowing
  it would be reachable — constant-time password comparison, sessions with two
  expiries, CSRF double-submit, a bounded hand-written HTTP parser, a
  `default-src 'self'` CSP, a per-address lockout that doubles. The game port
  was transcribed from a LAN server. That asymmetry is where every finding
  below came from.

---

## What the process defends against now

Each of these was a real, reachable failure. None of them needed a modified
client or a malformed byte — which is why none was caught by the parity work,
where the bytes are the whole subject.

### Resource exhaustion — `src/GG2.Server/ConnectionLimits.cs`

Every buffer in the transport had the same shape: grow on demand, for ever.

| What | Was | Now |
|---|---|---|
| how fast one address may connect | unbounded; only concurrent connections were capped | `--connect-rate` (10/min), loopback exempt |
| a client that stops reading | `_pending` doubled until the process died | faulted at `MaxSendBacklogBytes` (1 MB) |
| a client that sends faster than it is drained | unbounded, and the reference's own ten-commands-a-tick limit is what *guaranteed* the growth | faulted at `MaxReceiveBufferBytes` (256 KB) |
| one socket's share of a tick | `Receive` drained until the kernel would block | `MaxReceiveBytesPerTick` (64 KB) |
| garbage bytes | skipped for free, and skipping does not spend one of the ten commands, so nothing rate-limited it | `MaxInvalidCommandBytes` (4096) for the session |
| accepts | drained the whole backlog, quadratically | `MaxAcceptsPerTick` (32) |
| half-finished handshakes | unbounded list | `MaxJoiningConnections` (128) |

**Overflowing the receive buffer is a disconnect, not a discard.** Losing
bytes out of the middle of a command stream would leave the framer reading a
payload as a command byte for ever after. A silently desynchronised client is
worse than a disconnected one, and it looks exactly like a port bug.

### Slot squatting — `src/GG2.Server/JoiningConnection.cs`

**The reference's 30-second handshake timeout is an IDLE timeout, and
`PING` resets it.** `serviceJoiningPlayer.gml:1` drops a connection when
`current_time - lastContact > 30000`, and every byte received refreshes
`lastContact`. `PING` is legal in the `ExpectCommand` state and costs one byte.
`RESERVE_SLOT` sets `OccupiesSlot`, which `OccupiedSlots` counts against the
player limit.

So: connect, reserve, ping every twenty seconds. The connection is never idle,
never times out, and holds a seat for ever. A few of those and the server
advertises itself as full to the lobby using nothing but stock commands.

`MaxHandshakeDuration` (10 minutes) is a second clock that no activity resets.
Both are needed: the idle timeout ends the common case — somebody whose
connection dropped mid-handshake — in thirty seconds rather than ten minutes.

### Peers that vanished — `ClientConnection.ConfigureKeepAlive`

A client whose machine loses power sends no FIN and no RST.

**A receive-idle timeout would be wrong here**, and this is the trap worth
writing down: a **spectator legitimately sends nothing at all**. Only
`PlayerControl` sends `INPUTSTATE` every tick, and a spectator takes the other
branch of that event entirely (`PlayerControl.events/Begin Step.xml`). Any
heartbeat-based timeout kicks spectators.

The kernel is the right layer, since it is the only thing that can tell a quiet
peer from an absent one, and it takes two options because a peer can vanish in
two states:

| Peer vanished while | What the server's socket has | What frees the seat |
|---|---|---|
| nothing was in flight (a joining connection between commands) | an idle connection | TCP keepalive, 30 s + 3 x 10 s. `--no-tcp-keepalive` turns it off and is a regression, not a tuning choice |
| the server was writing to it (every connected client: the broadcast is per tick) | unacknowledged data, so **keepalive never sends a probe** | `UnacknowledgedTimeoutSeconds` (60 s): Linux `TCP_USER_TIMEOUT`, Windows `TCP_MAXRT` |

Without the second, a connected client that vanishes holds its roster slot until
TCP's retransmit gives up (`tcp_retries2`, about 15.5 minutes on Linux). The
stock client makes that routine: it drops its socket and reconnects at every
map change to a custom map it lacks (`ClientBeginStep.gml`, `CHANGE_MAP`), and
when that close is lost the old seat stays. On the duel server, whose rotation
is all custom maps, one player accumulated three seats, each freed about 16
minutes later, until `MultiClientLimit` refused the fourth connection. A WSL2
repro (two network namespaces, a joined loadprobe spectator, then the peer's
address flushed so nothing answers) freed the seat 67 s after silence with the
60 s default and 1,078 s (`left (HostUnreachable)`) with
`UnacknowledgedTimeoutSeconds=0`.

A minute is deliberate: a GM8 client stops reading while its window is dragged
or a modal dialog is open, and the zero-window probes that follow are bounded by
the same timeout. **What would show this stale:** a `left (TimedOut)` line more
than about two minutes after that player's last activity, on Linux or Windows.

### One fault must not end the game for everybody

`Run` called `Tick` bare. An exception from any stage reached `Main` and killed
the process.

Three layers, narrowest first — the useful question is not "did it survive" but
"how much did it cost":

- **`ServiceClients`** — everything below `ProcessCommands` is driven by bytes
  one client chose, so a fault costs that one connection.
- **`ServiceJoiningConnections`** — the same, one step earlier, and reachable
  before any password or completed handshake.
- **`ServiceBots`** — a bot is this project's code, so a fault is a bug rather
  than an attack, but not catching it costs every human their game. The bot is
  removed rather than retried: a source that threw on this world state will
  throw on the next, and a stack trace every 33 ms is the second outage.

**`Run`'s net does not simply swallow.** `TickFaultBudget` absorbs a fault
and refuses to absorb a pattern: past three in a rolling minute the server stops
and the host exits **3**. A server that catches everything for ever is up,
listed in the lobby, accepting players and simulating nothing — nobody is paged
for that, the players just leave. `Restart=on-failure` is what turns exit 3 into
a clean process, and it is the only thing that reliably clears corrupted
in-memory state.

`GameServer.TickFaults` is the count. **Anything other than zero is a bug
that has already happened**, even though the server carried on; the stack trace
is in the log at error level.

### Log injection — `src/GG2.Server/LogText.cs`

A player name is up to twenty arbitrary bytes decoded as Latin-1, including
`\n`, `\r` and ESC — nothing on the join path rejects them, because the
reference does not and the bytes must reach the wire intact. Interpolated into a
log line, that lets a player write log lines the server never wrote, or drive
the operator's terminal.

**Rendering only.** The name in the roster, on the wire and in a
`FULL_UPDATE` stays byte-for-byte what the client sent. Sanitising there would
desynchronise every other client's roster. The web panel needed nothing — every
string it emits goes through `JsonEncodedText` under a `default-src 'self'` CSP.

### The log as the amplifier — `src/GG2.Server/LogThrottle.cs`

Writing a log line happens on the tick thread and is synchronous.
`Console.WriteLine` takes a lock and can block on a pipe. That is fine for
events the server causes and wrong for events a *client* causes, because those
have no natural rate: one line per refused connection during a connection flood
makes the defence the outage.

Refusals and connection churn go through token buckets sized so ordinary play
never reaches them. **What is held back is counted and reported** — a log
that quietly omits the evidence during the incident being investigated makes a
flood look like a handful of connections, which is worse than a long log because
it is wrong rather than merely long.

### Connection rate — `src/GG2.Server/ConnectionRateLimiter.cs`

**`--multi-client-limit` is not this and the two get confused.** That is the
reference's answer to a household behind one NAT: how many connections one
address holds *at once*, default 3. It does nothing about how *fast* an address
opens them — connect, get refused, reconnect, repeat, with the concurrent count
never above one and every cycle costing an accept, an allocation, a roster walk
and (before the throttle) a log line.

`--connect-rate` is a token bucket per address, ten per minute by default, which
is far above a player whose game crashed and far below a script. **Loopback is
exempt**, exactly as it is from `--multi-client-limit` and for the reference's
own reason: it is how a host runs a client beside its own server, and a flood
from `127.0.0.1` is already inside the box.

**It fails OPEN when its table is full, and that is deliberate.** The table
has a ceiling — an unbounded per-address dictionary is the same growth bug one
layer up. Past `MaxTracked` (4096) a new address is *allowed* rather than
refused, because failing closed would let an attacker with enough addresses lock
out every legitimate player by filling the table, turning the rate limiter into
the denial of service. Saturation is logged once per episode, because a limiter
that has quietly stopped limiting is worse than one that was never on: the
configuration still says it is on.

### Bans — `src/GG2.Server/BanList.cs`

**Read this before relying on it: an address is not a person.** The protocol
has no account and no durable identity — a name is twenty bytes the client
chooses — so the only thing a ban can key on is the address, and that is wrong
in both directions. It catches everybody behind a shared address or a carrier
NAT, and it catches nobody whose address changes between connections, which on a
mobile network is most people. **It is a moderation tool for somebody being a
nuisance from one place. It is not a security control.**

Which is why **nothing adds to it automatically**. Every entry comes from an
operator — `ban`/`unban`/`bans` on the control port and the panel, or the file —
and the rate limiter above is what answers a machine, because that lapses on its
own and this does not. An automatic ban is how a shared address gets a whole
school locked out of a server nobody was even attacking.

- Address or CIDR (`1.2.3.0/24`), optional expiry, a reason for the operator.
- Banning **disconnects whoever is already on** from that address. A ban that
  only refused future connections would leave the person it was aimed at sitting
  there until they chose to leave.
- **Unbanning is exact.** Lifting `1.2.3.4` does not delete a `1.2.3.0/24`
  somebody put there on purpose; the error names the blocks that are in force.
- **Without `--ban-file` a ban is memory-only and dies with the process.**
  The server says so the first time one is added, rather than letting an
  operator find out by having the same person come back after a restart.
- A ban is refused at accept with **no message written back** — the peer is not
  owed an explanation, and naming which address is banned is free
  reconnaissance.

### Broadcast amplification — `GameServer.ExecuteChatBubble`

**A deliberate departure from the reference, and the only one a client could
notice.** The reference throttles `PLAYER_CHANGENAME` to once a second because
"every accepted rename is an unbounded broadcast to every connected client", and
does not apply the same reasoning to the `CHAT_BUBBLE` case beside it. Ten
commands a tick is three hundred broadcasts a second to everybody.

250 ms rather than the rename's second: a bubble is a keypress and a human sends
a few in a burst. `ChatBubbleIntervalMs = 0` restores the reference exactly.

---

### Client prediction — `GameServer.Prediction.cs`

`PREDICT_HELLO` (`0xC0`) is the one client command the reference does not
have. What it can make the server hold or send is bounded: one welcome per
connection however many hellos arrive, one five-byte ack per tick to that
connection only, and an input queue that is a fixed ring of 16 which merges
rather than grows (`NetworkInputSource.QueueCapacity`). A client sending faster
than 30 Hz is merged down to one tick per tick, so it cannot buy extra movement.
Nothing about a hello is logged. `ClientPrediction=0` makes the byte ignored, as
GM8 ignores it. `docs/PREDICTION.md`.

---

### The aim check — `src/GG2.Server/AimCheck.cs`

`INPUTSTATE`'s aim is `ClientInputstate.gml`'s `point_direction` and
`point_distance / 2` from the view centre to the pointer, and both ends are
integers, so a stock client can only send direction/distance pairs that some
integer offset produces. An aimbot that writes the angle to a target's
fractional position lands on one about 5% of the time (`AimCheckTests`).
`[Server] AimCheck` / `--aim-check`: `off`, `log` (default: one line per
connection, at the first), or `kick` (the line, then a kick after 30). The
input is applied either way, so the check never changes the simulation.

Measured in a stock client, 2026-10-05:

| Fact | Consequence |
|---|---|
| `view_xview` is stored as an integer, and every width in `rooms_fix_views.gml` is even | the view centre is integral |
| `mouse_x` is floored when the window is scaled (266.4 reads 266) | the pointer is integral at any window size |
| the pointer is tracked outside the window (1500,900 in an 800x600 window) | **there is no distance ceiling**: byte 255 is legal in every direction and always passes |
| `write_ushort`/`write_ubyte` round half away from zero and clamp (65536 writes 65535) | the encoding `AimCheck` inverts |
| the pointer at the centre writes 0/0 | distance 0 is legal only with direction 0 |

`Fixtures/aim-encoding-samples.txt` is the client's own encoding over a grid
of offsets, and every row must pass; the full 38,746-row dump passed when it
was taken. Against this server in `kick` mode, a stock client whose pointer was
moved to a random position inside and outside the window every frame for 600
frames, at 800x600 and scaled to 1000x750, logged nothing; the same client then
writing `1/100` by hand was logged at the first and kicked at the thirtieth. **Log, not kick, by default** because a client-side plugin may
compute aim differently, and only an operator can tell that from a cheat.
**What would show this is stale:** a stock client's offset that fails
`IsReachable`, which `EveryEncodingTheStockClientWroteIsReachable` checks
against the fixture; a GG2 release that changes `ClientInputstate.gml` or the
view sizes needs a new dump.

### Aim analysis — `src/GG2.Server/AimAnalysis.cs`

Per-player statistics over every connected person's inputs, kept at each lag
from 0 to 12 ticks (a client aims at the world it was sent, a round trip ago,
so a perfect aimbot is exact only at that player's lag). A signal fires when
the 99.9% Wilson lower bound of its rate, at the player's best lag, exceeds a
threshold, after a minimum sample. Each fires once per player, as a throttled
warning naming the evidence. `[Server] AimAnalysis` / `--no-aim-analysis`;
on by default, **log only**, never a kick.

| Signal | Counts | Minimum | Threshold |
|---|---|---|---|
| lock | ticks the aim ray passes within 1 px of a visible enemy whose bearing is turning | 200 | 0.12 |
| flick | one-tick turns of 20 degrees or more that land within 1 px of a visible enemy | 20 | 0.35 |
| trigger | attack presses on the first tick the aim ray meets a visible enemy's box | 20 | 0.40 |
| hidden | ticks within 1 px of an enemy that is off screen at every aspect ratio, behind a wall, or cloaked, with no visible enemy within 4 px | 300 | 0.07 |

**Calibration.** `tools/aimscan` replayed the fleet's 567 recordings
(2026-09-15 to 2026-10-05, 662 human sessions). No human session crosses any
threshold; the highest human lower bounds are 0.047 (lock), 0.035 (flick),
0.111 (trigger) and 0.032 (hidden), under half of each threshold. The same
sessions with a cheat substituted for part of the input, aimed at positions 2
to 5 ticks old through a whole-pixel pointer:

| Synthetic cheat | Flagged (of 662; the rest lack the minimum sample) |
|---|---|
| aim locked on the nearest visible enemy | 452 (lock), 368 also by flick |
| the same with 3 px jitter per tick | 451 |
| the same with 6 px jitter | 77 |
| the same with 10 px jitter | 0 |
| fire held exactly while on an enemy | 397 |
| aim follows the nearest hidden enemy, 3 px jitter | 490 |

**What it does not catch.** An aimbot that adds 6 px or more of per-tick noise
to its point, a cheat used for only a few seconds, and a wallhack used only to
know where enemies are without aiming at them. The highest human hidden-lock
sessions are on `koth_harvest`, which suggests a bullet-blocking shape there that
does not block sight; it stays below the threshold.

**Cost.** 0.064 ms a tick with 24 watched players on `ctf_truefort` (Release),
from one wall test per pair per tick and no trigonometry in the per-lag loop.
Bots are not watched: this project's own bots cross the lock threshold in 16%
of their sessions, and four policy-bot sessions cross the hidden one, which is
the known cloaked-spy leak in `BotVisibility`'s remarks.

**Live.** A stock client against this server, its pointer moved every frame
onto the nearest enemy as the client saw it (through walls), was logged as
`hidden 192/460 at lag 2` 36 seconds after joining.

**What would show this is stale:** a warning naming a player an operator knows
to be legitimate. Re-run `tools/aimscan` over new recordings, which prints the
human distributions, before moving a threshold; a GG2 release that changes how
aim reaches the wire needs the same.

## What is left to the machine

**None of the below is done for you, and the process cannot do it.** A
volumetric flood is absorbed before a packet reaches this code or it is not
absorbed at all.

### The firewall and the kernel

- Expose the game port. **Do not expose the control port**; it binds loopback,
  but a rule is a second lock on a door that should never be tried.
- The web panel behind a reverse proxy or a VPN if it is reachable at all. A
  non-loopback bind without `--web-tls` sends the password in the clear, and the
  server warns about exactly that.
- SYN cookies (`net.ipv4.tcp_syncookies=1`, usually already on) and a raised
  `net.ipv4.tcp_max_syn_backlog`. The accept cap above deliberately leaves
  excess connections in the kernel backlog, which is where a flood should be
  dropped — that only helps if the backlog is sized for it.
- A per-address connection-rate rule in `nftables`/`iptables` is worth more than
  anything in this process, because it costs the kernel a hash lookup and costs
  this process an accept, an allocation and a roster walk.

What the fleet runs: `ufw` with `DEFAULT_INPUT_POLICY=DROP` and three inbound
rules on each VPS member, `22/tcp`, `8190/tcp`, and `8191/tcp` for the fleet
hub. The hub reaches each member's panel over Tailscale, from its tailnet
address `100.64.0.1`, and the panel there is a fleet member (TLS,
`AllowFrom` naming that address alone). The tailnet policy (`tag:gg2`: the
tagged VPS hosts cannot start connections, and only the owner's devices can
reach them) and `ufw` are what keep 8191 off the public internet; `AllowFrom` is
the lock behind them. `Bind` stays `0.0.0.0`: binding to the 100.x address
fails whenever the server starts before `tailscaled`. A connect to 8191 from
outside the tailnet times out, which is the check that the rule is live rather
than assumed. The hub, on the home LAN, admits `192.168.x.0/24` and has no
forwarded port. If the hub's tailnet address changes, every member refuses it
until `AllowFrom` is updated; an ssh tunnel to loopback still reaches each
member's panel.

**`systemctl is-active ufw` does not answer whether the firewall is up.** The
unit is `Type=oneshot` with `RemainAfterExit=yes` and the `ufw` CLI loads rules
into the kernel itself, so a host whose rules are live and whose
`/etc/ufw/ufw.conf` says `ENABLED=yes` still reports `inactive` until the unit
runs at the next boot. `ufw status` and `ufw.conf` are what to read; the unit
state is not evidence in either direction.

**On Debian 12, the packaged `fail2ban` sshd jail cannot start on a host with
no rsyslog.** `/etc/fail2ban/jail.d/defaults-debian.conf` is `[sshd] enabled =
true` and nothing else, so `backend = auto` resolves to `/var/log/auth.log` —
a file that never exists when logging is journald-only — and `fail2ban-server`
exits 255 within 100 ms, before any jail starts. The service reads `enabled` in
`systemctl list-unit-files` the whole time, which is how it goes unnoticed.
Debian 13's drop-in sets `backend = systemd` itself and has no such problem. The
fix is a `jail.local` naming the journal:

```ini
[sshd]
backend = systemd
journalmatch = _SYSTEMD_UNIT=ssh.service + _COMM=sshd
```

**Enable the firewall before restarting fail2ban, not after.** `ufw enable`
rebuilds the tables, and fail2ban's bans are chains inserted into those same
tables, so the ordering is a precaution against a jail that reports bans it is
no longer enforcing. Not measured here - what would settle it is banning an
address, running `ufw reload`, and asking `nft list ruleset` whether the
`f2b-sshd` chain survived.

### The unit file

`docs/DEPLOY.md` has the working unit. For an internet-facing deployment, add:

```ini
# The exit code from a repeated tick fault is 3, and on-failure is what acts
# on it. `always` restarts a clean shutdown too, which hides an operator
# stopping the server on purpose.
Restart=on-failure
RestartSec=5

# A bounded worst case is what the connection limits above are for; this is
# the backstop when something else is wrong. The OOM killer picking this
# process is a far better outcome than it picking the database next door.
MemoryMax=2G

# Sandboxing. None of it changes what the server does; all of it narrows what
# a bug in it could reach.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/gg2-server/nav /opt/gg2-server/traces
RestrictAddressFamilies=AF_INET AF_INET6
```

**`ProtectSystem=strict` needs `ReadWritePaths` for anything the server
writes**, and it writes in two places that are easy to forget: `navgen` output
for a custom map (`--no-navgen` turns that off) and `--record` segments. Without
them the server starts and fails later, which is the worst of both.

### Watching it

- `journalctl -p warning` is the whole of the alerting story, because warnings
  and errors go to **stderr** and info goes to stdout. That separation is the
  point of the log levels — it needs no parser.
- `GameServer.TickFaults` on the panel, or `--web`'s graphs. Non-zero means go
  and read the trace.
- A late-tick line is **almost never the bots**: a twelve-bot server's own tick
  work is ~0.104 ms of process CPU. The line carries the tick's own work and a
  verdict. Check other processes before believing a pacing number.

---

## What is NOT defended, and why

This is the more useful list. Each of these is known, reachable, and left alone
on purpose — an unrecorded decision gets retried.

- **A distributed flood.** Nothing in a single process answers this. The limits
  above bound what *one* connection costs; they do not bound how many
  connections arrive.
- **Attributing a distributed flood.** `--connect-rate` (below) limits one
  address, and `MaxJoiningConnections` bounds the total, but neither can tell a
  botnet from a LAN party. Past `ConnectionRateLimiter.MaxTracked` addresses the
  limiter **fails open** — see below — and the answer is the firewall.
- **Cheating, except statistically.** The server trusts the aim it is told and
  broadcasts every player's state to every client, because the stock client
  re-simulates the physics from it; changing either breaks the stock client. So
  nothing prevents an aimbot, a triggerbot or a wallhack. The aim check and aim
  analysis above detect them after the fact, from the inputs, and only log.
  Movement, damage, fire rate and ammunition are simulated here, so a modified
  client cannot cheat them in the first place.
- **The chat/name content itself.** Names and bubbles are relayed as the client
  sent them, modulo the length limit the reference already enforces. There is no
  filter, and adding one would be a policy decision rather than a security one.
- **The reward relay.** **This server verifies nothing and cannot** — it does
  not have the client's key and is not meant to. It mints a nonce, relays the
  HMAC and broadcasts whatever the auth server returns. That is the design, not
  a weakness in the port; do not add a local check, there is nothing to check
  against. `RewardAuth.cs` says why in full.
- **Plugin code.** A loaded plugin runs in-process with no sandbox and gets
  attacker-chosen bytes through `PLUGIN_PACKET`. The fault guard contains a
  *throw* to one connection; it does nothing about a plugin that corrupts state
  quietly. `docs/PLUGINS.md`, and the pinned zip hash, are the whole of the
  answer.
- **Anything beyond one machine.** The measurements below cover one
  configuration, on loopback, eight seconds per attack. That is not a load
  test and says nothing about a thousand connections a second from a thousand
  addresses.

### The one guard that has been seen to fire on a stock client

`MaxInvalidCommandBytes` is the port's own addition; the reference has no
equivalent and tolerates unrecognised bytes for ever. Every kick and the first
skip on any connection carry `InvalidCommandReport`: how many runs, what each
followed, and the start of each run's bytes.

**What the report says, measured against a GM8 client on a WAN link (58 ms RTT,
US West VPS, 2026-09-13):**

```
4100 unrecognised command bytes in 4099 runs, after 4953 framed commands;
first 8 runs: @4267 after InputState+4 (#854): 1 byte 00
            | @4273 after InputState+4 (#855): 1 byte 00
            | @4279 after InputState+4 (#856): 1 byte 00 | ...
```

The offsets are **exactly six apart** and an `INPUTSTATE` is five bytes, so from
command #854 onward the client emits **exactly one stray `00` per framed
command.** The two log lines pin that ratio without relying on the eight kept
runs: 9 strays at 863 commands and 4099 strays at 4953, and 863-854 = 9 while
4953-854 = 4099. Each run is a single byte and the command after it frames
correctly, so **the stream is never desynchronised**: not a mis-sized message,
not a coalescing artifact. It is a spurious byte the reference would skip for
ever.

⚠️ **The client was sending about 293 commands a second** — 4099 of them in the
14 s between the two lines — against a nominal 30 fps, and close to the ~346 fps
ceiling this hardware reaches. Its `room_speed` was never read before it was
dropped. **So "one per command" is measured and "one per frame" is not**, and a
client running unthrottled may be the trigger rather than the link itself. The
run that produced this had no `agentRate` boost set, which is what makes the
rate unexplained.

**The guard fired on a runaway client, which is what it is for.** The control
that settles this: the same client build, the same 58 ms link, the same server,
with `room_speed` read back as 30 and watched for the whole run — **zero
unrecognised bytes in 15 minutes**, one join, no disconnect. The client that
tripped the guard was sending ~293 commands a second against a nominal 30, and
had simultaneously stopped answering its own agent bridge. So this is a client in
a runaway state, not a client playing normally, and a sender at ten times the
tick rate is exactly the case the ceiling exists to bound.

⚠️ **A lifetime total still cannot tell a fast sender from a long-lived one** —
it has no time term at all, so a slow trickle reaches any ceiling eventually. A
rate would express the intent directly. That is a latent weakness rather than an
observed failure, and nothing measured so far reaches it.

⚠️ **The report keeps only the first eight runs, all from the onset**, so
nothing here says whether the pattern held or changed over the remaining 4091.
Keeping a few of the most recent runs as well as the earliest is what would
answer that.

**What is established:** the byte appears only from a client sending far above
the tick rate. Loopback soaking never produced it (49 min at 4x, 862 joins, 120
map changes); nor did a healthy 30 fps client over a WAN (15 min). Only the
runaway client did, and then at exactly one byte per command.

**What is not:** what emits the byte, and what puts a client into that state —
it had just reconnected after a server restart. Both sightings used a client
carrying the live-game bridge payload, whose own writes go to its bridge socket and
not to `global.serverSocket`; a clean, un-injected client has not been tried.

---

## What the defences actually cost, measured

`tools/abuse/` runs the attacks at a running server and reads the damage off the
control port. **Read its README before quoting any of this** — two of the
five attacks cannot reach their limit over loopback at all, and the README says
which and why rather than letting a "not dropped" read as a pass.

2026-09-06, Windows 11, Release, `--bots 8 --players 16 --handshake-deadline 60`:

| Attack | Result | Against |
|---|---|---|
| connect-flood | 1500 sockets opened; the server held exactly **128** | `MaxJoiningConnections` |
| garbage | dropped after **5120 bytes** | `MaxInvalidCommandBytes` (4096) |
| send-flood | dropped after **231 KB** | `MaxReceiveBufferBytes` (256 KB) |
| slot-squat | seats released after **60s** | `--handshake-deadline 60` |
| deaf-client | not dropped — loopback absorbs it; see the README | `MaxSendBacklogBytes` |

Throughout all of it: **worst tick work 1.379 ms of a 33.3 ms budget, peak
managed heap 17.5 MB, zero late ticks, zero tick faults.**

That is the number worth keeping. A fifteen-hundred-connection flood plus four
concurrent floods cost about **4% of one tick** and **4 MB**, and no player on
the server would have seen a thing. The limits work where they were supposed to:
the connections never become the simulation's problem.

### And what they cost when nothing is attacking

**The defences are on the tick path, so the question is whether they moved
the project's headroom number.** They did not, measured as a diff rather than
quoted as an absolute — which is the only form CLAUDE.md accepts, and the reason
this was done against a clean `master` worktree on the same machine, back to
back:

| Build | 12 bots, steady state |
|---|---|
| `master` (b6aba7c) | median **0.112 ms**, range 0.086–0.222 |
| `server-hardening` (e85d3f7) | median **0.101 ms**, range 0.075–0.225 |

Within noise, and the branch reads marginally *lower*. `master` was sampled
first, so any warm-up bias worked against the branch and it still came out
level — which is what makes "no regression" the safe reading rather than a
hopeful one.

**Neither figure is a benchmark number and neither replaces the 0.151 ms in
`CLAUDE.md`.** `tickMs` is an exponential moving average over whatever the
server was doing; `tools/bench/` is where a per-tick cost comes from. This is a
*comparison* and is only good for one claim: the hardening work did not make the
tick more expensive. It is cheap to redo — `tools/abuse/`'s control-port
sampling is the same mechanism.

The reason to expect this is that almost none of the new work is per-tick: the
ban and rate-limit checks are per *accept*, the fault guards cost a `try` that
does not throw, and the ban sweep and saturation check ride the existing
30-second block.

**The theoretical ceiling is arithmetic, not a measurement.** 48 players ×
(1 MB send + 256 KB receive) plus 128 joining × 256 KB is about **95 MB** worst
case, on top of ~11 MB of nav tables per map and the runtime. Nothing has been
run that reaches it — the measured peak above is 17.5 MB — so treat it as the
bound the limits *permit*, not a figure anybody has seen.

### What would show this has gone stale

- **A limit a real player hits.** Every default is orders of magnitude above
  what a stock client does. If a legitimate client is being dropped for one,
  something about the protocol or a plugin has changed, and the ratio in that
  field's remarks is the thing to re-check — not the number.
- **`TickFaults` above zero on a healthy server.** The containment working is
  not the same as nothing being wrong.
- **A protocol change that raises the largest legal message.** `PLUGIN_PACKET`
  is `ushort`-prefixed, so 65,538 bytes is the floor under both buffer limits,
  and `ConnectionLimitTests.TheShippedDefaultsClearWhatARealClientDoes` is what
  goes red if a default drops below it.
- **A new client-driven log line.** Anything a client can cause the server to
  log needs a throttle, or it is the next amplifier.
- **`tools/abuse/` reporting a limit at a different number.** Each row above
  names the limit it is testing; a garbage flood surviving past 4096 bytes means
  the framer changed, not that the harness is wrong.
