"""Match-level death-share test: the unit of observation is a MATCH, not a death.

    python clustered.py arms_matches.csv

WHY THIS EXISTS
---------------
The pooled death-share test compared ~34,000 deaths an arm and reported z = -4.26.
`gg2-rl` pointed out that **deaths cluster by match**: ~40 arrive together, from
~840 matches an arm, so a test over pooled deaths treats clustered data as
independent and overstates its own evidence.  A plausible intra-match correlation
moves it a long way:

    rho    design effect    z becomes
    0.02       1.78          -3.20
    0.05       2.95          -2.48
    0.10       4.90          -1.93   <- no longer significant

Nobody can estimate rho here, and correcting by a number nobody can estimate is
worse than not correcting.  So this does not correct: it **changes the unit**.
Each match contributes ONE observation -- its own death share -- and the arms are
compared with Welch's t over ~840 of them.  That sidesteps clustering entirely
rather than adjusting for it.

    comes back near -4   ->  clustering was negligible; the pooled headline stands
    comes back near -2   ->  the pooled headline was overstated, and we would
                             rather know than have it found later

⚠️ A ZERO-DEATH MATCH HAS NO SHARE and must be dropped, which reintroduces a
filter.  The count of dropped matches is printed per arm, because a filter that
differs between arms is what invalidates the comparison -- not a filter that is
merely present.  That is this repo's own lesson about `decided`, one level down.
"""
import csv
import sys
from math import sqrt


def welch(a, b):
    """t and Welch degrees of freedom for two unequal-variance samples."""
    na, nb = len(a), len(b)
    ma, mb = sum(a) / na, sum(b) / nb
    va = sum((x - ma) ** 2 for x in a) / (na - 1)
    vb = sum((x - mb) ** 2 for x in b) / (nb - 1)
    se = sqrt(va / na + vb / nb)

    if se == 0:
        return ma - mb, 0.0, 0.0

    t = (ma - mb) / se
    df = (va / na + vb / nb) ** 2 / (
        (va / na) ** 2 / (na - 1) + (vb / nb) ** 2 / (nb - 1))

    return ma - mb, t, df


def main(path):
    shares, dropped, matches = {}, {}, {}

    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            arm = row["arm"]
            red, blue = int(row["red_deaths"]), int(row["blue_deaths"])
            matches[arm] = matches.get(arm, 0) + 1

            if red + blue == 0:
                dropped[arm] = dropped.get(arm, 0) + 1
                continue

            shares.setdefault(arm, []).append(red / (red + blue))

    print("match-level death share -- one observation per MATCH, not per death")
    print()
    print(f"{'arm':>4} {'matches':>9} {'usable':>8} {'zero-death':>11} {'mean share':>11}")

    for arm in sorted(shares):
        s = shares[arm]
        print(f"{arm:>4} {matches[arm]:>9} {len(s):>8} {dropped.get(arm, 0):>11} "
              f"{sum(s) / len(s):>10.2%}")

    print()

    # ⚠️ The filter has to be EQUAL between arms, not merely small. A zero-death
    # match is dropped, and if one arm drops many more its sample is more selected.
    rates = {a: dropped.get(a, 0) / matches[a] for a in matches}
    spread = max(rates.values()) - min(rates.values())
    print(f"zero-death drop rate: " + ", ".join(f"arm {a} {r:.1%}" for a, r in sorted(rates.items())))
    print(f"  spread between arms: {spread:.1%}"
          + ("  <- unequal filtering, read the comparison with that in mind"
             if spread > 0.02 else "  (comparable)"))
    print()

    for lo, hi in (("3", "2"), ("3", "1"), ("2", "1")):
        if lo not in shares or hi not in shares:
            continue

        diff, t, df = welch(shares[lo], shares[hi])
        print(f"arm {lo} vs arm {hi}: {diff:+.4%} points, Welch t = {t:+.2f}, df = {df:.0f}")

    print()
    print("Compare with the POOLED test's z. A large gap means the pooled figure was")
    print("counting clustered deaths as independent observations.")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)

    main(sys.argv[1])
