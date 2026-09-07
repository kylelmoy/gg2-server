# The shipped policy checkpoints

**Received from the `gg2-rl` lane on 2026-09-07 at the user's instruction**, and
checked in here for the reason `maps/` and `nav/` are: without them a clone
cannot run the thing this repository documents. `gg2-rl`'s own `checkpoints/` is
gitignored, so before this handover these weights existed on exactly one machine.

⚠️ **This file records what was MEASURED and by whom.** Nothing in this
repository can reproduce the numbers below — they are match statistics from the
training lane's bench, over arms this repository cannot construct. What this
repository checks is in `tests/Gg2.Tests/ShippedCheckpointTests.cs`: that these
files are present, are the bytes that were handed over, load against this build's
`SIM_UUID`, drive exactly the classes claimed, and really take the wheel in a
match. **Presence and routing are gated; quality is quoted.**

## What ships

| file | md5 | drives |
|---|---|---|
| `tier0_combat4_s2.npz` | `3c6f997b222c37ce5cdc8eabbf724f90` | scout, soldier, demoman, heavy |
| `tier2_pyro_rest3_s0.npz` | `4229edb1b53cab8fc72315b343c5e36f` | **pyro** |
| — | — | *sniper, spy, engineer, medic → scripted* |

Both carry `SIM_UUID 698913b5ea547d60` — this build's, pinned by
`SimulationStampTests` — with layout **v2** (211 floats), encoding v2 and action
repeat **4**. Trained at `gg2-rl` commit `c434229`. `HybridBot`, two tiers,
**150px**, `HandoverRule.MacroTask` — which are `BotBrainSpec`'s defaults, so the
command below needs no gate flags.

✅ **The two class sets are DISJOINT**, so `HybridBot.EligibleTiers` yields
exactly one eligible tier per class and `Choose`'s tier order cannot change
behaviour. Register them in either order.
`ShippedCheckpointTests.TheTwoShippedTiersDriveDisjointClassSets` is what says so
when that stops being true.

## Running them

```powershell
dotnet run --project src/Gg2.Host -- `
  --map ctf_truefort --bots 12 --bot-mode policy `
  --policy checkpoints/tier0_combat4_s2.npz `
  --policy checkpoints/tier2_pyro_rest3_s0.npz
```

`--policy` is repeatable and **appends**; a config file says the same thing as
`[Bots] Policy = a.npz,b.npz`. The server prints one line per checkpoint carrying
its stamp, this build's, and the classes it will drive.

⛔ **Do not pass `--bot-classes trained` with this bundle.** That shorthand is the
*default* four-class set, resolved before any checkpoint is loaded — flags are
parsed before `--policy` opens a file, and argument order is not guaranteed — so
it silently drops pyro, and the pyro tier never drives. Name the five explicitly:

```
--bot-classes Scout,Soldier,Demoman,Heavy,Pyro
```

## ⛔⛔ `tier2_pyro_rest3_s0.npz` carries a SHOULD-DRIVE set in a TRAINED-ON field

**Its `meta[7]` says `PYRO`. The run trained `ENGINEER,SPY,PYRO`.** `gg2-rl`'s
`scripts/ship_tier.py` minted it from `rest3_s0/ppo_iter12000.npz` with the
weights byte-identical and only the metadata narrowed.

⚠️ **There is one field, and it is now carrying two meanings.** The file is *true
about what it may drive* and *incomplete about what it learned*, and **nothing in
either repository can tell those apart** — `PolicyNetwork.TrainedClasses` and
`PolicyController.Drives` read the same set, which is also why the should-drive
question is unmeasurable on that bench (`docs/TRAPS-BOTS.md`). ⛔ `ship_tier.py`
refuses to WIDEN a set — a should-drive set may only ever narrow a trained one —
so it cannot make a policy claim a class it never trained. That refusal is the
only thing standing between this convention and a checkpoint that lies.

**What would show this has gone stale**: a second metadata field arriving on the
`gg2-rl` side, at which point `PolicyNetwork` should read both and this section
should be deleted rather than edited. The training lane has not asked for it.

## ⚠️ What has a match number and what does not

| configuration | death share Δ | win share Δ |
|---|---|---|
| tier 0, its four classes, 150px | −1.50 | −0.88 |
| `rest3` driving **all three** classes it trained, 150px | **−2.82** (2·SE 0.69) | **+3.02** (2·SE 2.14) |

⛔ **The shipped tier 2 drives PYRO ALONE and has no match number of its own.**
Q21 and Q22 both measured all three driving, and the bench cannot produce one for
a narrower set: `AllowedClasses` and `Drives` read the same set, so a pyro-only
artefact yields a pyro-only match rather than a mixed one.

✅ **What supports it instead**: pyro's own mirror is **74.90** (2·SE 3.66)
against a 50% null, and its nine-opponent row is **82.70**, second only to heavy.

## ⚠️ Three things not to read as settled

- ⛔ **SOLDIER ships UNDECIDED.** Its mirror is 46.47 with a 2·SE of 12.13 —
  *below* the 50% null and not clearing. It drives because undecided means status
  quo, **not because it measured well**, and `combat4_s2` is the worst soldier of
  its four seeds in two independent samples. If a playtest ever makes a soldier
  look wrong, this is the first place to look and it will not be a surprise.
- ⛔ **SNIPER was dropped on a JUDGEMENT, not a refutation.** A `control`
  checkpoint's sniper mirror is 89.08. The only match number for that checkpoint
  had it driving all four of its classes and lost 1.95 points of win share, which
  is attributable to any of the four.
- ⛔ **SPY was dropped despite clearing** at 88.22. The user's call.

## ✅ Why 150px, on this checkpoint's own evidence

`gg2-rl`'s Q22 — 21 maps × 100 matches × four widths × four seeds:

| | 80px | **150px** | 220px | 300px |
|---|---|---|---|---|
| death share Δ | −1.29 | **−2.73** | ⛔ −2.86 | −2.24 |
| win share Δ | +0.52 | ✅ **+1.34** | −2.39 | ⛔ −10.23 |
| ticks driven | 2.0% | **5.5%** | 10.1% | 16.0% |

⛔ **Death share alone would have chosen 220px and paid 3.73 points of win
share** — the third reproduction of that trap on the third checkpoint family, and
the first time it would have changed a decision already made. Combat quality and
match outcome are not the same axis, and the wider window trades the second for
the first.

## Housekeeping

- ⚠️ **`tier0_combat4_s2.npz` is byte-identical to
  `tests/Gg2.Tests/Fixtures/policy/combat4_s2_iter12000.npz`.** They are
  duplicated deliberately, because they have different lifecycles: the fixture is
  a no-op control for `TrainedClassesTests`, this is what a server plays.
  `ShippedCheckpointTests.TheShippedTierZeroIsTheSameFileAsTheDefaultSetFixture`
  is the `MapCopyTests`-shaped guard on the copy. If they ever legitimately
  diverge, **delete that test and say so here** rather than weakening it.
- ⚠️ **A policy is DATED by the physics.** A `SIM_UUID` move retires both of
  these; `PolicyNetwork.Load` refuses a foreign stamp and
  `ShippedCheckpointTests` deliberately does not bypass it, so the retirement
  lands on the run that moved the stamp. `docs/BOTS.md`, "a policy is DATED by the
  physics".
- **Canonical record on the producing side: `docs/STATUS.md` in `gg2-rl`.**
