#!/usr/bin/env python3
"""Samples the HOST while a load run is in progress. Runs on the server.

Writes one TSV row a second: the CPU accounting deltas (steal is the one that
matters on a shared vCPU), load average, available memory, and the gg2 process's
own RSS and CPU. Every fifth row also carries what the kernel thinks of the
network path to each connected client, from `ss -tin`.

⚠️ This competes for the very core it is measuring. That is unavoidable and is
why it is deliberately cheap — three small /proc reads a second, and `ss` only
every fifth — and why it records its OWN cpu time in the last column, so a run
can say how much of what it saw was itself.

⚠️ It does NOT poll the admin panel. PerfHistory keeps 300 seconds, so the
server's own per-second health is pulled once when a step ends (perf-pull.py)
rather than over an HTTP request a second on a 1-vCPU box.

    host-sample.py <out.tsv> <seconds> [ss_every=5]
"""
import os
import re
import subprocess
import sys
import time

out_path, seconds = sys.argv[1], float(sys.argv[2])
ss_every = int(sys.argv[3]) if len(sys.argv) > 3 else 5

CLOCK = os.sysconf("SC_CLK_TCK")
PAGE = os.sysconf("SC_PAGE_SIZE")


def cpu_fields():
    with open("/proc/stat") as f:
        parts = f.readline().split()
    # user nice system idle iowait irq softirq steal guest guest_nice
    return [int(x) for x in parts[1:11]]


def loadavg():
    with open("/proc/loadavg") as f:
        return f.readline().split()[0]


def mem_available_mb():
    with open("/proc/meminfo") as f:
        for line in f:
            if line.startswith("MemAvailable:"):
                return int(line.split()[1]) // 1024
    return -1


def find_gg2():
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                if b"gg2-server" in f.read():
                    return pid
        except OSError:
            continue
    return None


def gg2_stat(pid):
    """Returns (rss_mb, cpu_jiffies) for the server, or (-1, -1)."""
    try:
        with open(f"/proc/{pid}/stat") as f:
            parts = f.read().split()
        # utime=14 stime=15 rss=24, 1-indexed after the comm field's parens
        utime, stime, rss = int(parts[13]), int(parts[14]), int(parts[23])
        return rss * PAGE // (1024 * 1024), utime + stime
    except (OSError, IndexError, ValueError):
        return -1, -1


RTT = re.compile(r"\brtt:([0-9.]+)/([0-9.]+)")
RETRANS = re.compile(r"\bretrans:\d+/(\d+)")


def game_sockets(port):
    """Per-connection rtt and cumulative retransmits on the game port."""
    try:
        raw = subprocess.run(
            ["ss", "-tin", "state", "established", f"( sport = :{port} )"],
            capture_output=True, text=True, timeout=10).stdout
    except (subprocess.SubprocessError, OSError):
        return 0, -1, -1, -1

    rtts = [float(m.group(1)) for m in RTT.finditer(raw)]
    retrans = sum(int(m.group(1)) for m in RETRANS.finditer(raw))
    # One header line, then two lines per connection.
    count = len(rtts)

    if not rtts:
        return count, -1, -1, retrans

    return count, round(sum(rtts) / len(rtts), 2), round(max(rtts), 2), retrans


pid = find_gg2()
if pid is None:
    print("no gg2-server process found", file=sys.stderr)
    sys.exit(1)

previous = cpu_fields()
prev_gg2 = gg2_stat(pid)[1]
started = time.time()
row = 0

with open(out_path, "w") as out:
    out.write("t\tcpu_user\tcpu_sys\tcpu_idle\tcpu_iowait\tcpu_steal\tloadavg"
              "\tmem_avail_mb\tgg2_rss_mb\tgg2_cpu_pct\tconns\trtt_mean\trtt_max"
              "\tretrans\tsampler_cpu_s\n")
    out.flush()

    while time.time() - started < seconds:
        time.sleep(1.0)
        row += 1

        current = cpu_fields()
        delta = [c - p for c, p in zip(current, previous)]
        total = sum(delta) or 1
        previous = current

        rss, gg2_cpu = gg2_stat(pid)
        gg2_pct = -1.0
        if gg2_cpu >= 0 and prev_gg2 >= 0:
            # Percent of ONE core, the same denominator PerfHistory uses.
            gg2_pct = round((gg2_cpu - prev_gg2) * 100.0 / CLOCK, 2)
        prev_gg2 = gg2_cpu

        if row % ss_every == 0:
            conns, rtt_mean, rtt_max, retrans = game_sockets(8190)
        else:
            conns, rtt_mean, rtt_max, retrans = -1, -1, -1, -1

        def pct(i):
            return round(delta[i] * 100.0 / total, 2)

        out.write("\t".join(str(x) for x in [
            round(time.time() - started, 1),
            pct(0), pct(2), pct(3), pct(4), pct(7),
            loadavg(), mem_available_mb(), rss, gg2_pct,
            conns, rtt_mean, rtt_max, retrans,
            round(time.process_time(), 2),
        ]) + "\n")
        out.flush()

print(f"wrote {out_path} ({row} rows, sampler used {time.process_time():.2f}s cpu)")
