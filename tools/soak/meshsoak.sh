#!/usr/bin/env bash
# Two mesh nodes, real client churn on both, and the numbers that say whether
# they stayed in step.
#
# ⛔ IN THE REPO ON PURPOSE. The previous version of this lived in a session
# scratchpad, was named in a handoff as "the instrument for whoever picks this
# up", and was gone by the time anybody did.
#
#   tools/soak/meshsoak.sh <out-dir> <seconds> <clients-per-side> [minutes] [bots]
#
# ⛔ The fourth argument is the ROUND LENGTH in minutes, and it is what reaches
# the map change. A round end rebuilds both nodes exactly as an operator's map
# change does (docs/MESH.md), and a time limit gets there on a clock rather than
# by waiting for somebody to cap — which a fleet of synthetic players, who do
# not play the objective, may never do. 1 is the minimum the wire can carry.
# Pass 0 to leave the round alone and soak without any rebuild at all.
#
# So: seconds / minutes is roughly how many map changes a run gets. Ask for at
# least four to observe three.
#
# ⚠️ A CLEAN SHORT RUN MEANS NOTHING HERE. Every mesh defect found so far was
# invisible below eight clients a side, below a map change, or both — and one
# needed THREE map changes, because the first rebuild was clean and the second
# broke it. docs/MESH.md.
set -u

out="${1:?usage: meshsoak.sh <out-dir> <seconds> <clients-per-side> [cap-limit]}"
secs="${2:?}"
clients="${3:?}"
minutes="${4:-1}"
bots="${5:-6}"

root="$(cd "$(dirname "$0")/../.." && pwd)"
server="$root/src/Gg2.Host/bin/Release/net10.0/gg2-server.exe"
players="$root/tools/soak/bin/Release/net10.0/soak-players.exe"

mkdir -p "$out"

# Ports. Fixed rather than ephemeral so the log lines can be read back against
# a known pair; change them if something else on the box has them.
mesh=27610
a_game=8191; a_ctl=8192
b_game=8291; b_ctl=8292

# ⛔ ONE SOAK AT A TIME. The ports above are fixed, so a second run started while
# one is still going loses every server to
# `unable to start: Only one usage of each socket address` — and the summary
# below then prints EMPTY fields rather than saying anything failed, which reads
# like "nothing to report" instead of "nothing ran". Checked here because that
# is a whole wasted run otherwise.
if command -v ss > /dev/null 2>&1; then
  busy=$(ss -ltn 2>/dev/null | grep -cE ":($a_game|$b_game|$mesh)\b")
else
  busy=$(netstat -an 2>/dev/null | grep -cE "LISTENING.*:($a_game|$b_game|$mesh)\b")
fi

if [ "${busy:-0}" -gt 0 ]; then
  echo "meshsoak: ports $a_game/$b_game/$mesh are already in use — another soak is" >&2
  echo "          probably still running. Wait for it, or edit the ports above." >&2
  exit 1
fi

# ⚠️ --bots is what makes the world MOVE. Synthetic players connect, pick teams
# and press keys, but a match with nobody fighting is a much quieter thing than
# a real one, and every defect found here so far needed load to show.
# The joiner ignores its own --bots: only the listener fills (MeshFillsBots).
common="--players 40 --multi-client-limit 0 --connect-rate 0 --bots $bots"

# ⛔ A ROTATION, or the round end is not a map CHANGE. ServerOptions.MapRotation
# is empty by default and NextMapInRotation then returns the map already
# loaded, so without this every rebuild reopens the same map: same collision
# world, same spawn points, warmest possible OpenMap. Measured — 60 of 60
# rebuilds across a 20-run batch were ctf_truefort -> ctf_truefort.
#
# ⚠️ That matters because the asymmetry every rebuild fault has come from is the
# WALL-CLOCK gap between the two nodes' swaps: they rebuild at the same tick at
# different moments, so each has taken in a different amount of the peer's
# traffic. Reopening a cached map is the shortest that gap ever gets.
#
# Four maps, four gamemodes, deliberately different sizes. --shuffle defaults to
# None so both nodes walk the same order, and the chosen map travels in the
# ChangeMap command anyway.
rotation="ctf_truefort,koth_valley,cp_egypt,ctf_2dfort"
common="$common --rotation $rotation"

if [ "$minutes" != "0" ]; then
  common="$common --time-limit $minutes"
fi

echo "meshsoak: ${secs}s, $clients clients a side, ${minutes}min rounds, $bots bots -> $out"
echo "meshsoak: rotation $rotation"

# ⛔ The listener first. The joiner now waits for it (Mesh.ConnectPatienceSeconds),
# so this order is no longer load-bearing — but starting it first still saves
# every run the retry interval.
"$server" --port $a_game --control-port $a_ctl --mesh-listen $mesh \
          $common --log-file "$out/listener.log" > "$out/listener.out" 2>&1 &
a=$!

"$server" --port $b_game --control-port $b_ctl --mesh-connect 127.0.0.1:$mesh \
          $common --log-file "$out/joiner.log" > "$out/joiner.out" 2>&1 &
b=$!

# Let both bind and agree a tick origin before anybody connects.
sleep 8

"$players" --port $a_game --players "$clients" --seconds "$secs" --seed 11 \
           --name-prefix la > "$out/players-a.txt" 2>&1 &
pa=$!

"$players" --port $b_game --players "$clients" --seconds "$secs" --seed 22 \
           --name-prefix jb > "$out/players-b.txt" 2>&1 &
pb=$!

wait $pa $pb

# A moment for the last departures to land on both nodes before the logs stop.
sleep 5

kill $a $b 2>/dev/null
wait $a $b 2>/dev/null

echo
echo "=== agreement, last line each ==="
for side in listener joiner; do
  printf '%-9s ' "$side"
  grep -o 'agreed [0-9]*/[0-9]*' "$out/$side.log" | tail -1
done

echo
echo "=== what actually catches things (docs/MESH.md: the digest does not) ==="
for side in listener joiner; do
  echo "--- $side"
  echo "  rebuilds:        $(grep -c 'node rebuilt at tick' "$out/$side.log")"
  echo "  roster vs world: $(grep -c 'a player left the world without' "$out/$side.log")"
  echo "  stale departure: $(grep -c 'cannot leave the mesh' "$out/$side.log")"
  echo "  refused command: $(grep -c 'REFUSED' "$out/$side.log")"
  # ⚠️ NOT the last sample. The fleet has gone home by the time the last line is
  # written, so a final `owned 1` means "one player left", not "ownership
  # collapsed" — which is exactly how a healthy run reads as the orphaning bug.
  # The minimum WHILE the fleet was connected is the number that means something.
  echo "  owned, min/max while loaded: $(grep -o 'owned [0-9]*' "$out/$side.log" \
    | head -n -2 | awk '{print $2}' | sort -n | sed -n '1p;$p' | paste -sd/ -)"
  grep -o 'events [0-9]* released / [0-9]* held' "$out/$side.log" | tail -1 | sed 's/^/  last /'

  # ⛔ The one that says whether a rebuild broke the match. `agreed` FROZEN while
  # the total climbs is a permanent divergence; both climbing together with a
  # fixed gap is the transient kind. They read almost the same on one line.
  echo "  agreement over time: $(grep -o 'agreed [0-9]*/[0-9]*' "$out/$side.log" \
    | awk -F'[ /]' '{print $2}' | uniq | tr '\n' ' ')"
done

echo
echo "=== map changes ==="
# ⚠️ The DISTINCT count first. A rebuild that reopened the same map exercises the
# node swap but not a map load, and the run then says nothing about the gap
# between the two nodes' swaps — which is the thing a rotation is here to widen.
# One distinct map means the rotation did not move, whatever the count says.
for side in listener joiner; do
  echo "  $side distinct maps: $(grep -h 'now playing' "$out/$side.log" \
    | sed 's/.*now playing //' | awk '{print $1}' | sort -u | tr '\n' ' ')"
done
grep -h 'now playing' "$out/listener.log" | sed 's/^/  listener /' || true
grep -h 'now playing' "$out/joiner.log"   | sed 's/^/  joiner   /' || true
