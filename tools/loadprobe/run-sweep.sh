#!/usr/bin/env bash
#
# The client-performance sweep: more and more spectator clients against one
# host, with the server's own health and the host's CPU accounting collected
# alongside, so a bad number can be attributed instead of argued about.
#
#     tools/loadprobe/run-sweep.sh <ssh-dest> <game-host> <out-dir> [steps...]
#
# Three layers come back, and the point is that they are independent:
#
#   client.tsv   when broadcasts ARRIVED, measured from this box (loadprobe)
#   perf.tsv     what the SERVER thought it was doing, per second (PerfHistory)
#   host.tsv     CPU steal, load, memory and per-connection RTT (host-sample.py)
#
# ⚠️ A jitter number from client.tsv alone cannot say whose fault it is. perf.tsv
# says whether the server was late; host.tsv's steal column says whether the box
# was taken away from it; the rtt columns say whether the network did it. Read
# all three or none.
#
# ⚠️ The server must already be configured for this (delisted, MultiClientLimit
# raised, bot population pinned). The sweep does NOT change the server's
# configuration - it only measures - so that a run cannot quietly be measuring a
# different server than the one that was set up.
#
set -euo pipefail

dest="${1:?ssh destination, e.g. gg2-us-west}"
game="${2:?the game host or address the probe connects to}"
out="${3:?output directory}"
shift 3
steps=("$@")
[ ${#steps[@]} -eq 0 ] && steps=(1 2 4 6 8 10 12 16 20 24)

probe="${LOADPROBE:-loadprobe}"
ini="${GG2_CONFIG:-/opt/gg2-server/shared/gg2.json}"
seconds="${SWEEP_SECONDS:-90}"
warmup="${SWEEP_WARMUP:-10}"
settle="${SWEEP_SETTLE:-10}"

mkdir -p "$out"

# Enough for every step plus the gaps between them, and some slack: the sampler
# stopping early would silently truncate the last step's host data.
total=$(( (${#steps[@]} * (seconds + warmup + settle + 15)) + 120 ))

echo "==> sweep on $dest ($game), steps: ${steps[*]}"
echo "    ${seconds}s per step after a ${warmup}s warmup; host sampler for ${total}s"

scp -q "$(dirname "$0")/host-sample.py" "$(dirname "$0")/perf-pull.py" "$dest:/tmp/"

ssh "$dest" "nohup python3 /tmp/host-sample.py /tmp/sweep-host.tsv $total 5 \
    > /tmp/sweep-host.log 2>&1 &" || true

# PerfHistory is a 300-second ring, so each step's server-side seconds are
# collected when that step ends rather than at the end of the sweep, which would
# have thrown away everything older than the last five minutes.
last_seq=-1

for n in "${steps[@]}"; do
    echo
    echo "--- step: $n client(s) ---"

    "$probe" --host "$game" --clients "$n" --seconds "$seconds" --warmup "$warmup" \
        --stagger-ms 200 --label "${n}c" \
        --out "$out/client.tsv" --gaps "$out/gaps.tsv" || echo "  (step failed, carrying on)"

    ssh "$dest" "python3 /tmp/perf-pull.py $ini $last_seq" > "$out/.perf-step.tsv" || true

    if [ -s "$out/.perf-step.tsv" ]; then
        if [ ! -f "$out/perf.tsv" ]; then
            { printf 'label\t'; head -1 "$out/.perf-step.tsv"; } > "$out/perf.tsv"
        fi

        tail -n +2 "$out/.perf-step.tsv" | sed "s/^/${n}c\t/" >> "$out/perf.tsv"
        last_seq="$(tail -1 "$out/.perf-step.tsv" | cut -f1)"
        echo "  server: $(tail -n +2 "$out/.perf-step.tsv" | wc -l) second(s) collected, seq now $last_seq"
    fi

    sleep "$settle"
done

rm -f "$out/.perf-step.tsv"

echo
echo "==> collecting the host sampler"
ssh "$dest" "cat /tmp/sweep-host.tsv" > "$out/host.tsv" || true

echo "==> done: $out"
ls -la "$out"
