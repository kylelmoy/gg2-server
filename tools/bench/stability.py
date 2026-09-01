"""Does each map's DEATH-SHARE DIRECTION survive the sample size?

    python stability.py small.txt large.txt

Both arguments are BENCH_PERMAP tables, taken at different match counts.

WHY THIS EXISTS
---------------
`gg2-rl` asked whether a per-map sign test at 40 matches an arm is supported,
because phase 4 had three maps flip between 40 and 400 -- one reversing sign.
Those were WIN share, whose unit is decided matches (~38 at n=40, so twelve
points is five matches).  Death share counts DEATHS, so its per-map denominator
is in the hundreds and may be perfectly stable.  Nobody had checked.

THE REFINEMENT THAT DECIDES WHAT TO RECORD
------------------------------------------
`gg2-rl` again, and it changes the output rather than the method:

    DIRECTION STABILITY IS NOT A PROPERTY OF n.  It is a property of EFFECT SIZE
    relative to noise.  So "directions agreed at n=40" licenses only a comparison
    whose effect is at least as large as the one it was measured on.

So the number to record is NOT the agreement count.  "21 of 21 agreed" is what
will travel, and it is exactly what goes wrong when somebody later applies it to
a one-point effect.  This prints the smallest effect at which every direction
held, and says to quote that instead.

WHAT IT LICENSES, AND WHAT IT NEVER DOES
----------------------------------------
It licenses the sign test AS A WHOLE and never an individual map's contribution:
a map whose effect is small can flip whatever the aggregate says.  That is fine,
because a sign test only ever claimed the whole.

If no effect-size floor makes every direction agree, the honest move is not a
bigger run -- it is to report the AGGREGATE death share WITH ITS DEATH COUNTS
(trap 1: the denominator in the row, not the caption) and say plainly that the
per-map sign test was unaffordable.  A weaker claim honestly labelled beats a
stronger one at a size that cannot support it.
"""
import re
import sys

# The BENCH_PERMAP row, same shape analyse.py reads.
ROW = re.compile(
    r"^(\S+)\s+(\d+)-(\d+)\s+\S+\s+\S+\s+(\d+)-(\d+)\s+\S+\s+\S+\s*\|"
    r"\s*(\d+)-(\d+)\s+\S+\s+(\d+)-(\d+)\s+\S+\s*\|\s*(\d+)/(\d+)")

FIELDS = "map pw pl prd pbd cw cl crd cbd pdec cdec".split()


def read(path):
    """Per-map death-share effect in points: policy minus control."""
    out, n = {}, None

    for line in open(path, encoding="utf-8", errors="replace"):
        if m := re.match(r"^\d+ maps, (\d+) matches", line):
            n = int(m.group(1))
        if m := ROW.match(line.strip()):
            g = [m.group(1)] + [int(x) for x in m.groups()[1:]]
            r = dict(zip(FIELDS, g))
            policy = r["prd"] / max(1, r["prd"] + r["pbd"])
            control = r["crd"] / max(1, r["crd"] + r["cbd"])
            out[r["map"]] = (policy - control) * 100.0

    if not out:
        sys.exit(f"no data rows parsed from {path}")

    return out, n


def main(small_path, large_path):
    small, n_small = read(small_path)
    large, n_large = read(large_path)
    shared = sorted(set(small) & set(large))

    if not shared:
        sys.exit("no maps in common between the two tables")

    print(f"death-share DIRECTION stability, n={n_small} against n={n_large}")
    print("  effect = policy death share minus control, in points; negative is")
    print("  better for the policy. The question is whether the SIGN survives.")
    print()
    print(f"{'map':<18} {'n=' + str(n_small):>11} {'n=' + str(n_large):>11}   agree")

    flips = []

    for m in shared:
        agrees = (small[m] < 0) == (large[m] < 0)

        if not agrees:
            flips.append((m, small[m], large[m]))

        print(f"{m:<18} {small[m]:>+10.1f}p {large[m]:>+10.1f}p   "
              f"{'yes' if agrees else 'NO'}")

    print()
    print(f"{len(shared) - len(flips)} of {len(shared)} maps kept their direction.")

    # The floor: the smallest |effect| at which every map above it agreed.
    floor = None

    for step in range(0, 201):
        cut = step * 0.1
        band = [m for m in shared if abs(large[m]) >= cut]

        if band and all((small[m] < 0) == (large[m] < 0) for m in band):
            floor = cut
            break

    print()

    if not flips:
        smallest = min(abs(large[m]) for m in shared)
        print(f"Every direction agreed, down to an effect of {smallest:.1f}p.")
        print("QUOTE THAT, NOT THE COUNT. It licenses a comparison whose per-map")
        print("effect is at least that large, and nothing smaller.")
        return

    for m, a, b in flips:
        print(f"  {m} flipped: {a:+.1f}p -> {b:+.1f}p")

    if floor is None:
        print()
        print("No effect-size floor makes every direction agree, so the per-map sign")
        print("test is NOT supported at this size. Report the aggregate death share")
        print("with its death counts and say the sign test was unaffordable.")
        return

    print()
    print(f"Directions held for every map with |effect| >= {floor:.1f}p at n={n_large}.")
    print("QUOTE THAT, NOT THE COUNT. A comparison with a smaller per-map effect is")
    print("not licensed by this check however many maps agreed.")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)

    main(sys.argv[1], sys.argv[2])
