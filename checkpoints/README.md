# The shipped policy checkpoints

Two trained checkpoints from the `gg2-rl` lane, checked in for the reason `maps/`
and `nav/` are: without them a clone cannot run what this repository documents.
They travel with a `dotnet publish`.

| file | md5 | drives |
|---|---|---|
| `tier0_combat4_s2.npz` | `3c6f997b222c37ce5cdc8eabbf724f90` | scout, soldier, demoman, heavy |
| `tier2_pyro_rest3_s0.npz` | `4229edb1b53cab8fc72315b343c5e36f` | pyro |
| — | — | sniper, spy, engineer, medic stay scripted |

Both carry `SIM_UUID 698913b5ea547d60` — this build's, pinned by
`SimulationStampTests` — with layout v2 (211 floats), encoding v2 and action
repeat 4, trained at `gg2-rl` commit `c434229`. They run as two `HybridBot` tiers
at 150px under `HandoverRule.MacroTask`, which are `BotBrainSpec`'s defaults.

The two class sets are disjoint, so exactly one tier is ever eligible for a class
and registration order cannot change behaviour.
`ShippedCheckpointTests.TheTwoShippedTiersDriveDisjointClassSets` fails if that
stops being true.

## Running them

```powershell
dotnet run --project src/Gg2.Host -- `
  --map ctf_truefort --bots 12 --bot-mode policy `
  --policy checkpoints/tier0_combat4_s2.npz `
  --policy checkpoints/tier2_pyro_rest3_s0.npz
```

`--policy` is repeatable and appends, one tier per checkpoint; a config file says
the same as `[Bots] Policy = a.npz,b.npz`. Each checkpoint drives the classes its
own file claims, so nothing further needs configuring. The server prints one line
per checkpoint carrying its stamp, this build's, and the classes it will drive.

Two settings adjust it:

- `--bot-class-policy Soldier=scripted` — who drives a class. The value is a
  checkpoint's file name without its extension, or `scripted`. Repeatable, and it
  only has to name the classes being overridden.
- `--bot-classes driven` — which classes bots play. Restricts the draw to
  whatever the loaded policies drive, following any `ClassPolicy` overrides, so
  every bot is the configuration under test rather than a mix of that and the
  all-scripted arm.

## `tier2_pyro_rest3_s0.npz` carries a should-drive set in a trained-on field

Its `meta[7]` says `PYRO`. The run trained `ENGINEER,SPY,PYRO`. `gg2-rl`'s
`scripts/ship_tier.py` minted it with the weights byte-identical and only the
metadata narrowed.

A checkpoint has one class-set field and it answers two questions: what the run
trained, and what its author intends it to drive. This file is true about the
second and incomplete about the first, and nothing in either repository can tell
those apart. `ship_tier.py` refuses to widen a set, so the convention cannot make
a policy claim a class it never trained; that guard lives entirely on the
producing side.

The information the narrowing dropped can be restored from the server side by
someone who knows it: `--bot-class-policy Engineer=tier2_pyro_rest3_s0` drives
engineer with weights that did train it. The server warns when a class is
assigned to a checkpoint that does not list it, so the warning is expected here
and is not an error.

This section stops being needed if `gg2-rl` adds a second metadata field. Delete
it then rather than editing it.

## What has a match number and what does not

| configuration | death share | win share |
|---|---|---|
| tier 0, its four classes, 150px | −1.50 | −0.88 |
| `rest3` driving all three classes it trained, 150px | −2.82 (2·SE 0.69) | +3.02 (2·SE 2.14) |

The shipped tier 2 drives pyro alone and has no match number of its own. Both
measurements had all three of `rest3`'s classes driving, and the bench cannot
produce a narrower one: `AllowedClasses` and `Drives` read the same set there, so
a pyro-only artefact yields a pyro-only match rather than a mixed one. What
supports it instead is pyro's own mirror, 74.90 (2·SE 3.66) against a 50% null,
and its nine-opponent row of 82.70, second only to heavy.

## Three things not to read as settled

- **Soldier ships undecided.** Its mirror is 46.47 with a 2·SE of 12.13, below
  the 50% null and not clearing, and `combat4_s2` is the worst soldier of its
  four seeds in two independent samples. It drives because undecided means status
  quo. If a playtest makes a soldier look wrong, this is the first place to look.
  `--bot-class-policy Soldier=scripted` turns off exactly that and leaves the
  other three driving.
- **Sniper was dropped on a judgement, not a refutation.** A `control`
  checkpoint's sniper mirror is 89.08. The only match number for that checkpoint
  had it driving all four of its classes and lost 1.95 points of win share, which
  is attributable to any of the four.
- **Spy was dropped despite clearing** at 88.22, on the user's call.

## Why 150px

`gg2-rl`'s width sweep, 21 maps x 100 matches x four widths x four seeds:

| | 80px | 150px | 220px | 300px |
|---|---|---|---|---|
| death share Δ | −1.29 | **−2.73** | −2.86 | −2.24 |
| win share Δ | +0.52 | **+1.34** | −2.39 | −10.23 |
| ticks driven | 2.0% | **5.5%** | 10.1% | 16.0% |

Death share alone picks 220px and pays 3.73 points of win share. Combat quality
and match outcome are separate axes, and a wider window buys the first with the
second, so any change to the width, the gate or the handover rule has to be read
on both.

## Maintenance

- `tier0_combat4_s2.npz` is byte-identical to
  `tests/Gg2.Tests/Fixtures/policy/combat4_s2_iter12000.npz`, and
  `ShippedCheckpointTests.TheShippedTierZeroIsTheSameFileAsTheDefaultSetFixture`
  asserts it. The two are duplicated because their goals differ: a fixture wants
  a stable test input, a shipped artefact wants the best available policy. They
  will diverge when tier 0 next improves, at which point delete that test and say
  here that the two are no longer the same file.
- A policy is dated by the physics it was trained against. A `SIM_UUID` move
  retires both files; `PolicyNetwork.Load` refuses a foreign stamp and
  `ShippedCheckpointTests` does not bypass it, so the retirement surfaces on the
  run that moves the stamp.
- `ShippedCheckpointTests` gates presence and routing only — the md5s, the stamp,
  the class sets in both directions, disjointness, and a match per tier proving
  the weights take the wheel. It says nothing about whether either checkpoint
  plays well. That evidence is the training lane's and is quoted above.
- Canonical record on the producing side: `docs/STATUS.md` in `gg2-rl`.
