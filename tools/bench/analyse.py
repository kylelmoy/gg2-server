"""Read a PerMapEval table and test the things the table cannot show by eye.

Three questions, none of which a column of percentages answers:

  1. IS THE FILTER EQUAL BETWEEN ARMS?  gg2-rl's sharpening, and the one that
     outranks the others: what invalidates a win-share comparison is not that
     censoring is HIGH, it is that it DIFFERS between the arms.  3% vs 3% is
     comparable; 3% vs 15% is not, and both look fine beside 50%.  So the first
     column read is the two-proportion z on DECIDEDNESS, map by map.
  2. Does the death-share sign hold across every map?  Counted mechanically,
     because 21 signs read off a column by eye is how a 19-of-19 becomes an
     18-of-19 nobody notices.
  3. Which win-share gaps survive their own n?
"""
import re
import sys
from math import sqrt


def z(a1, n1, a2, n2):
    if not n1 or not n2:
        return None
    p = (a1 + a2) / (n1 + n2)
    se = sqrt(p * (1 - p) * (1 / n1 + 1 / n2))
    return None if se == 0 else (a1 / n1 - a2 / n2) / se


ROW = re.compile(
    r"^(\S+)\s+(\d+)-(\d+)\s+\S+\s+\S+\s+(\d+)-(\d+)\s+\S+\s+\S+\s*\|"
    r"\s*(\d+)-(\d+)\s+\S+\s+(\d+)-(\d+)\s+\S+\s*\|\s*(\d+)/(\d+)")

rows = []
matches = None
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    if m := re.match(r"^\d+ maps, (\d+) matches", line):
        matches = int(m.group(1))
    if m := ROW.match(line.strip()):
        g = [m.group(1)] + [int(x) for x in m.groups()[1:]]
        rows.append(dict(zip(
            "map pw pl prd pbd cw cl crd cbd pdec cdec".split(), g)))

if not rows:
    sys.exit(f"no data rows parsed from {sys.argv[1]}")

print(f"{len(rows)} maps, {matches} matches an arm\n")

# THE DENOMINATOR THAT IS NOT `decided`. MatchOutcome.Decided INCLUDES A DRAW --
# HeadlessMatch.cs:84 says so in as many words, "a team won, OR the round was
# drawn", and Rounds.cs decides with Team.Spectator in five places. A win share
# is over red wins + blue wins, which on some maps is a small fraction of the
# decided count: ctf_classicwell's control is 337/400 decided and 25 of those
# have a winner. Six per cent, not eighty-four.
#
# So every "decided" number in this exchange -- including the ones used to argue
# the two arms were filtered EQUALLY -- was measuring a filter that is not the
# win share's filter. The cap censoring was real and this sits underneath it:
# two layers, and `capped` reveals only the top one.
print("=== the win-share denominator, which is NOT `decided` ===")
print("   decisive = matches with a WINNER. A drawn round is Decided and has none.")
print()
print(f"{'map':<18} {'decisive p/c':>15} {'as % of arm':>15}   {'win share p/c':>17}")
for r in sorted(rows, key=lambda q: q["pw"] + q["pl"]):
    pn, cn = r["pw"] + r["pl"], r["cw"] + r["cl"]
    thin = "  <-- win share over a THIN subset" if min(pn, cn) < 0.25 * matches else ""
    print(f"{r['map']:<18} {pn:>6}/{cn:<8} {pn / matches:>6.1%}/{cn / matches:<8.1%}"
          f"  {r['pw'] / max(1, pn):>7.1%}/{r['cw'] / max(1, cn):<7.1%}{thin}")
print()

print(f"{'map':<18} {'DECIDED p/c':>13} {'z':>6}   {'death share p/c':>17} {'z':>6}   "
      f"{'win share p/c':>15} {'z':>6}")

signs = filt = 0
for r in rows:
    dz = z(r["pdec"], matches, r["cdec"], matches)
    hz = z(r["prd"], r["prd"] + r["pbd"], r["crd"], r["crd"] + r["cbd"])
    wz = z(r["pw"], r["pw"] + r["pl"], r["cw"], r["cw"] + r["cl"])
    pd = r["prd"] / max(1, r["prd"] + r["pbd"])
    cd = r["crd"] / max(1, r["crd"] + r["cbd"])
    pw = r["pw"] / max(1, r["pw"] + r["pl"])
    cw = r["cw"] / max(1, r["cw"] + r["cl"])

    if r["prd"] + r["pbd"] and pd < cd:
        signs += 1
    if dz is not None and abs(dz) >= 1.96:
        filt += 1

    fmt = lambda v: "  -  " if v is None else f"{v:+5.1f}"
    flag = " <-- FILTER DIFFERS" if dz is not None and abs(dz) >= 1.96 else ""
    print(f"{r['map']:<18} {r['pdec']:>6}/{r['cdec']:<6} {fmt(dz)}   "
          f"{pd:>8.1%}/{cd:<8.1%} {fmt(hz)}   {pw:>7.1%}/{cw:<7.1%} {fmt(wz)}{flag}")

# ⛔ gg2-rl's sharpening, as a ranked list: the quantity that invalidates a
# win-share comparison is the DIFFERENCE in censoring between the arms, not the
# level.  A map at 3%/3% is fine and a map at 30%/30% is fine; a map at 3%/15%
# is the broken one and it looks better than the second.  Ranking by the
# differential is what finds the next one without guessing which map to suspect.
print()
print("=== ranked by DIFFERENTIAL censoring of the WIN SHARE'S OWN denominator ===")
print("   i.e. |policy decisive - control decisive|, where decisive = wins + losses.")
print("   NOT the differential in `decided`: that ranks by the cap layer and is BLIND to")
print("   the draw layer, which is the one that actually moves this denominator. Ranking")
print("   on `decided` clears ctf_classicwell (84.3% vs 76.5% decided) whose two arms are")
print("   6% decisive - the exact row the ranking exists to catch.")
print()
ranked = sorted(rows, key=lambda q: -abs((q["pw"] + q["pl"]) - (q["cw"] + q["cl"])))
for r in ranked:
    pn, cn = r["pw"] + r["pl"], r["cw"] + r["cl"]
    d = pn - cn
    dz = z(pn, matches, cn, matches)
    mark = "  <-- win shares NOT comparable" if dz is not None and abs(dz) >= 1.96 else ""
    print(f"{r['map']:<18} {d:+5d} decisive  ({d / matches:+6.1%} of the arm)"
          f"   z {'  -  ' if dz is None else f'{dz:+5.1f}'}"
          f"   win share {r['pw'] / max(1, pn):.1%} vs {r['cw'] / max(1, cn):.1%}{mark}")
print()
print("--- the same ranking on `decided`, kept only to show it disagrees ---")
for r in sorted(rows, key=lambda q: -abs(q["pdec"] - q["cdec"]))[:5]:
    d = r["pdec"] - r["cdec"]
    print(f"{r['map']:<18} {d:+5d} decided   ({d / matches:+6.1%} of the arm)")
print()

n = sum(1 for r in rows if r["prd"] + r["pbd"])
print(f"\ndeath share better under the policy on {signs} of {n} maps"
      f"   (sign test p = {0.5 ** n:.2g} if all)")
print(f"maps where the two arms are censored DIFFERENTLY (|z| >= 1.96 on decidedness): {filt} of {len(rows)}")
print("  ^ on those maps the win shares are not over comparable populations, whatever the capped LEVEL is")
