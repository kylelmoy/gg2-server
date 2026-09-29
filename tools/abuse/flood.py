#!/usr/bin/env python3
"""Drive a running gg2-server the way an attacker would, and measure what it costs.

Read tools/abuse/README.md before believing a number out of this.

Usage:
    python tools/abuse/flood.py [--port 8190] [--control 9190] [--only NAME]

The server must already be running with --control-port; this attaches rather
than starting one, so the flags under test are the operator's own.
"""

import argparse
import json
import socket
import sys
import time

# The handshake, from GG2.Protocol.MessageType and GameConstants.
HELLO = 0
PLAYER_JOIN = 1
RESERVE_SLOT = 60
PROTOCOL_UUID = bytes.fromhex("b31c220942569a19d0efc71c5373bd75")

# A byte that is not in the client-to-server command table, so the framer skips
# it. See ClientCommands.KindOf.
NOT_A_COMMAND = 0xFE


class Control:
    """The loopback JSON control port. One connection per command, like a tool."""

    def __init__(self, port):
        self.port = port

    def send(self, **cmd):
        s = socket.create_connection(("127.0.0.1", self.port), timeout=5)
        try:
            s.sendall((json.dumps(cmd) + "\n").encode())
            buffered = b""
            while b"\n" not in buffered:
                chunk = s.recv(65536)
                if not chunk:
                    raise RuntimeError("the control port closed mid-reply")
                buffered += chunk
            return json.loads(buffered.split(b"\n")[0])
        finally:
            s.close()

    def state(self):
        reply = self.send(cmd="state")
        if not reply.get("ok"):
            raise RuntimeError(f"state failed: {reply}")
        return reply


def handshake(port, name, timeout=5):
    """A complete join. Returns the socket, still connected."""
    s = socket.create_connection(("127.0.0.1", port), timeout=timeout)
    s.sendall(bytes([HELLO]) + PROTOCOL_UUID)
    _read_some(s)
    s.sendall(bytes([RESERVE_SLOT, len(name)]) + name.encode("latin-1"))
    _read_some(s)
    s.sendall(bytes([PLAYER_JOIN]))
    _read_some(s)
    return s


def _read_some(s, deadline=2.0):
    """Wait for the server to say something. Returns what arrived."""
    s.settimeout(deadline)
    try:
        return s.recv(65536)
    except (socket.timeout, TimeoutError):
        return b""


def is_dead(s):
    """Whether the server has closed this socket."""
    s.setblocking(False)
    try:
        return s.recv(1) == b""
    except BlockingIOError:
        return False
    except OSError:
        return True
    finally:
        try:
            s.setblocking(True)
        except OSError:
            pass


# --------------------------------------------------------------------- attacks


def attack_connect_flood(args, control):
    """Open connections as fast as possible and never speak.

    What this is checking: MaxAcceptsPerTick bounds the tick's share,
    MaxJoiningConnections bounds the list, and neither lets the tick overrun.
    """
    sockets = []
    refused = 0
    start = time.monotonic()

    while time.monotonic() - start < args.seconds:
        for _ in range(50):
            try:
                s = socket.create_connection(("127.0.0.1", args.port), timeout=2)
                s.setblocking(False)
                sockets.append(s)
            except OSError:
                refused += 1
        time.sleep(0.01)

    state = control.state()
    alive = sum(0 if is_dead(s) else 1 for s in sockets)

    for s in sockets:
        s.close()

    return {
        "opened": len(sockets),
        "refused by the OS": refused,
        "still open": alive,
        "server joining": state["server"]["joining"],
    }


def attack_deaf_client(args, control):
    """Join, then never call recv. The classic, and it needs no exploit.

    What this is checking: MaxSendBacklogBytes faults the connection instead of
    the server growing a queue until it is killed.

    ⛔ THIS ONE DOES NOT REACH ITS LIMIT OVER LOOPBACK AND IS EXPECTED TO REPORT
    "not dropped". Measured: three deaf clients on an eight-bot server for sixty
    seconds, with the backlog limit set to its 65538-byte minimum, still did not
    trigger it. The kernel's loopback buffers absorb everything the server
    sends, so ClientConnection.Flush keeps succeeding and _pending never grows —
    SO_RCVBUF below is set anyway and Windows ignores it on loopback.

    The mechanism is proven by ConnectionLimitTests over a real socket pair,
    which pushes ~32 MB to overrun even those buffers. Over a real link to a
    genuinely stalled peer the window closes at the peer and the backlog is
    reached in seconds. What this run establishes is the weaker but still useful
    claim: deaf clients do not cost the server tick time or unbounded heap while
    they sit there.
    """
    sockets = [handshake(args.port, f"deaf{i}") for i in range(args.clients)]

    # Shrink the receive window so the server's own queue is what fills, rather
    # than the kernel's buffers absorbing everything. ⚠️ Without this on
    # loopback the server never feels back-pressure at all — see the README.
    for s in sockets:
        try:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 2048)
        except OSError:
            pass

    start = time.monotonic()
    dropped_at = None

    while time.monotonic() - start < args.seconds:
        time.sleep(0.25)
        if all(is_dead(s) for s in sockets):
            dropped_at = time.monotonic() - start
            break

    state = control.state()

    for s in sockets:
        s.close()

    return {
        "clients": len(sockets),
        "all dropped after": (
            f"{dropped_at:.1f}s"
            if dropped_at
            else "not dropped - EXPECTED over loopback, see the docstring"
        ),
        "server heap MB": state["server"]["heapMb"],
    }


def attack_garbage(args, control):
    """Join, then send bytes the command table does not know.

    What this is checking: MaxInvalidCommandBytes. Skipping an invalid byte is
    the reference's behaviour and is kept; what is bounded is how long it lasts,
    because skipping does not spend one of the ten commands a tick allows and so
    nothing else rate-limits it.
    """
    s = handshake(args.port, "garbage")
    sent = 0
    start = time.monotonic()

    try:
        while time.monotonic() - start < args.seconds:
            s.sendall(bytes([NOT_A_COMMAND]) * 256)
            sent += 256
            time.sleep(0.005)
            if is_dead(s):
                break
    except OSError:
        pass

    dropped = is_dead(s)
    s.close()

    return {
        "bytes sent": sent,
        "dropped": dropped,
        "limit": 4096,
    }


def attack_send_flood(args, control):
    """Join, then send valid commands faster than ten a tick drains.

    What this is checking: MaxReceiveBufferBytes. The reference's own flood
    control is what makes this unbounded — it handles ten commands a tick and
    leaves the rest buffered, so the faster the flood the faster the growth.
    """
    # CHAT_BUBBLE is two bytes and always parses, so every one of these is a
    # complete, legal command that has to be buffered rather than skipped.
    payload = bytes([30, 1]) * 512

    s = handshake(args.port, "shouter")
    sent = 0
    start = time.monotonic()

    try:
        while time.monotonic() - start < args.seconds:
            s.sendall(payload)
            sent += len(payload)
            if is_dead(s):
                break
    except OSError:
        pass

    dropped = is_dead(s)
    state = control.state()
    s.close()

    return {
        "bytes sent": sent,
        "dropped": dropped,
        "server heap MB": state["server"]["heapMb"],
    }


def attack_slot_squat(args, control):
    """Reserve a slot, then PING for ever without joining.

    ⚠️ The reference's 30s handshake timeout is an IDLE timeout and PING resets
    it, so this holds a seat indefinitely on a stock server. What ends it is
    MaxHandshakeDuration, which no activity resets — ten minutes by default, so
    run the server with --handshake-deadline 60 to see it inside a test.
    """
    sockets = []

    for i in range(args.clients):
        s = socket.create_connection(("127.0.0.1", args.port), timeout=5)
        s.sendall(bytes([HELLO]) + PROTOCOL_UUID)
        _read_some(s)
        name = f"squat{i}"
        s.sendall(bytes([RESERVE_SLOT, len(name)]) + name.encode("latin-1"))
        _read_some(s)
        sockets.append(s)

    held = control.state()["server"]["joining"]
    start = time.monotonic()
    released_at = None

    while time.monotonic() - start < args.seconds:
        for s in sockets:
            try:
                s.sendall(bytes([57]))  # PING
            except OSError:
                pass
        time.sleep(5)

        if control.state()["server"]["joining"] == 0:
            released_at = time.monotonic() - start
            break

    for s in sockets:
        s.close()

    return {
        "seats held": held,
        "released after": f"{released_at:.0f}s" if released_at else f"STILL HELD after {args.seconds}s",
        "note": "10 minutes by default; run with --handshake-deadline 60 to see it lapse",
    }


ATTACKS = {
    "connect-flood": attack_connect_flood,
    "deaf-client": attack_deaf_client,
    "garbage": attack_garbage,
    "send-flood": attack_send_flood,
    "slot-squat": attack_slot_squat,
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8190, help="the game port")
    parser.add_argument("--control", type=int, default=9190, help="the control port")
    parser.add_argument("--seconds", type=float, default=10.0, help="how long each attack runs")
    parser.add_argument("--clients", type=int, default=3, help="sockets per attack, where it takes several")
    parser.add_argument("--only", help="run one attack by name")
    args = parser.parse_args()

    control = Control(args.control)

    try:
        before = control.state()
    except OSError as e:
        print(f"cannot reach the control port on 127.0.0.1:{args.control}: {e}", file=sys.stderr)
        print("start the server with --control-port <n>.", file=sys.stderr)
        return 2

    print(f"server: frame {before['server'].get('uptimeSeconds')}s up, "
          f"tick {before['server']['tickMs']}ms of {before['server']['tickBudgetMs']}ms, "
          f"heap {before['server']['heapMb']}MB\n")

    chosen = {args.only: ATTACKS[args.only]} if args.only else ATTACKS
    worst_tick = before["server"]["tickMs"]
    peak_heap = before["server"]["heapMb"]

    for name, run in chosen.items():
        print(f"--- {name}")
        result = run(args, control)

        for key, value in result.items():
            print(f"    {key}: {value}")

        state = control.state()
        worst_tick = max(worst_tick, state["server"]["tickMs"])
        peak_heap = max(peak_heap, state["server"]["heapMb"])

        # ⚠️ Let the server settle before the next attack, or one attack's
        # dropped sockets are still in TIME_WAIT during the next one's and the
        # numbers blame the wrong thing.
        time.sleep(2)

    after = control.state()

    print("\n--- the server, throughout")
    print(f"    worst tick work seen:  {worst_tick}ms of {after['server']['tickBudgetMs']}ms budget")
    print(f"    peak managed heap:     {peak_heap}MB")
    print(f"    late ticks:            {after['server']['lateTicks']}"
          f" (worst overrun {after['server']['worstOverrunMs']}ms)")
    print(f"    tick faults:           {after['server']['tickFaults']}")
    print(f"    players still on:      {after['server'].get('joining')} joining")

    # ⛔ The one hard gate. Everything else here is a number to read; this is a
    # claim that would be false if the server had died or corrupted itself.
    if after["server"]["tickFaults"] != 0:
        print("\nFAIL: the server absorbed a tick fault. Read the log.", file=sys.stderr)
        return 1

    print("\nthe server survived every attack and is still ticking.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
