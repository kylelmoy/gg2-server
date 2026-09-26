#!/usr/bin/env python3
"""Joins the sweep's three layers into one table, one row per step.

    analyse.py <out-dir>

client.tsv already carries its own percentiles. What this adds is the two layers
that say whose fault a bad percentile is, aligned to the same steps.

⚠️ Alignment is by SELF-IDENTIFYING columns, not by wall clock. perf.tsv rows are
kept only where `humans` equals the step's client count, and host.tsv rows only
where `conns` does. That drops the settle gaps and the connect ramps, which run
at a different duty cycle (the server idles to 2-3 ticks a second when empty) and
would otherwise be averaged into the step they sit next to.
"""
import sys
from pathlib import Path

out = Path(sys.argv[1] if len(sys.argv) > 1 else ".")


def rows(path):
    if not path.exists():
        return []
    lines = path.read_text().strip().splitlines()
    if len(lines) < 2:
        return []
    head = lines[0].split("\t")
    return [dict(zip(head, ln.split("\t"))) for ln in lines[1:]]


def num(x, default=0.0):
    try:
        return float(x)
    except (TypeError, ValueError):
        return default


client = rows(out / "client.tsv")
perf = rows(out / "perf.tsv")
host = rows(out / "host.tsv")

if not client:
    sys.exit(f"no client.tsv in {out}")


def agg(values, how):
    if not values:
        return None
    if how == "mean":
        return sum(values) / len(values)
    if how == "max":
        return max(values)
    if how == "min":
        return min(values)
    if how == "sum":
        return sum(values)
    raise ValueError(how)


print()
print("CLIENT - when broadcasts arrived, measured from the probe box")
print(f"{'clients':>7} {'reads/s':>8} {'KiB/s':>7} {'p50':>6} {'p99':>6} {'p99.9':>7} "
      f"{'max':>7} {'>50ms':>7} {'>100ms':>7} {'faults':>7}")

for c in client:
    print(f"{c['clients']:>7} {c['reads_per_s']:>8} {c['kib_per_s']:>7} {c['p50']:>6} "
          f"{c['p99']:>6} {c['p999']:>7} {c['max']:>7} {c['over50']:>7} {c['over100']:>7} "
          f"{c['faults']:>7}")

print()
print("SERVER - what it thought it was doing (PerfHistory, per second; chars is LIVE characters,")
print("         so it sits below the 24 bots on the roster while some are respawning)")
print(f"{'clients':>7} {'secs':>5} {'ticks/s':>8} {'tickMean':>9} {'tickPeak':>9} "
      f"{'cpu%':>6} {'cpuMax':>7} {'rssMB':>6} {'late':>5} {'chars':>6} {'KiB/s':>7}")

server_by_clients = {}

for c in client:
    n = int(c["clients"])
    mine = [p for p in perf if p.get("label") == f"{n}c" and int(num(p.get("humans"), -1)) == n]

    if not mine:
        print(f"{n:>7}   (no server rows with humans=={n})")
        continue

    row = {
        "secs": len(mine),
        "ticks": agg([num(p["ticks"]) for p in mine], "mean"),
        "tickMean": agg([num(p["tickMean"]) for p in mine], "mean"),
        "tickPeak": agg([num(p["tickPeak"]) for p in mine], "max"),
        "cpu": agg([num(p["cpu"]) for p in mine], "mean"),
        "cpuMax": agg([num(p["cpu"]) for p in mine], "max"),
        "rss": agg([num(p["rss"]) for p in mine], "max"),
        "late": agg([num(p["late"]) for p in mine], "sum"),
        "chars": agg([num(p["characters"]) for p in mine], "mean"),
        "kib": agg([num(p["bytes"]) for p in mine], "mean") / 1024.0,
    }
    server_by_clients[n] = row

    print(f"{n:>7} {row['secs']:>5} {row['ticks']:>8.1f} {row['tickMean']:>9.3f} "
          f"{row['tickPeak']:>9.3f} {row['cpu']:>6.2f} {row['cpuMax']:>7.2f} "
          f"{row['rss']:>6.1f} {row['late']:>5.0f} {row['chars']:>6.1f} {row['kib']:>7.1f}")

print()
print("HOST - the box the server is renting a slice of")
print(f"{'clients':>7} {'n':>4} {'steal%':>7} {'stealMax':>9} {'idle%':>7} {'load':>6} "
      f"{'memMB':>7} {'gg2cpu%':>8} {'rtt':>7} {'rttMax':>7} {'retrans':>8}")

for c in client:
    n = int(c["clients"])
    mine = [h for h in host if int(num(h.get("conns"), -1)) == n]

    if not mine:
        print(f"{n:>7}   (no host rows with conns=={n})")
        continue

    steal = [num(h["cpu_steal"]) for h in mine]
    rtts = [num(h["rtt_mean"]) for h in mine if num(h.get("rtt_mean"), -1) >= 0]
    rttmax = [num(h["rtt_max"]) for h in mine if num(h.get("rtt_max"), -1) >= 0]

    print(f"{n:>7} {len(mine):>4} {agg(steal, 'mean'):>7.2f} {agg(steal, 'max'):>9.2f} "
          f"{agg([num(h['cpu_idle']) for h in mine], 'mean'):>7.2f} "
          f"{agg([num(h['loadavg']) for h in mine], 'max'):>6.2f} "
          f"{agg([num(h['mem_avail_mb']) for h in mine], 'min'):>7.0f} "
          f"{agg([num(h['gg2_cpu_pct']) for h in mine], 'mean'):>8.2f} "
          f"{(agg(rtts, 'mean') or -1):>7.1f} {(agg(rttmax, 'max') or -1):>7.1f} "
          f"{agg([num(h['retrans']) for h in mine], 'max'):>8.0f}")

# --- the verdict ------------------------------------------------------------
# A gap is what a player sees. 100 ms is about three missed broadcasts, which is
# where the stock client's dead reckoning visibly diverges and snaps back.
print()
print("VERDICT")

worst = None

for c in client:
    per_client_per_s = num(c["over100"]) / max(num(c["window_s"]), 1) / max(num(c["clients"]), 1)
    if worst is None or per_client_per_s > worst[1]:
        worst = (int(c["clients"]), per_client_per_s)

clean = [int(c["clients"]) for c in client
         if num(c["over100"]) == 0 and int(c["faults"]) == 0]

if clean:
    print(f"  no gap over 100ms at any of: {', '.join(str(x) + 'c' for x in sorted(clean))}")

dirty = [(int(c["clients"]), int(num(c["over100"])), num(c["max"])) for c in client
         if num(c["over100"]) > 0]

for n, over, mx in sorted(dirty):
    print(f"  {n}c: {over} gap(s) over 100ms, worst {mx:.0f}ms"
          + (f", server tickPeak {server_by_clients[n]['tickPeak']:.1f}ms"
             f", late {server_by_clients[n]['late']:.0f}" if n in server_by_clients else ""))

faulted = [(int(c["clients"]), int(c["faults"])) for c in client if int(c["faults"]) > 0]
for n, f in sorted(faulted):
    print(f"  {n}c: {f} client(s) FAULTED - the run did not hold at this size")

print()
print("  Read the server and host tables before blaming the server: a tickPeak")
print("  near the budget is the server; steal with tickPeak low is the neighbour;")
print("  rtt/retrans moving with nothing else is the network.")
