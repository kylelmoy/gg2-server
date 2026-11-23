# The shipped policy checkpoints

## The gg2-sim nets (`.gg2p`) — what the fleet runs

Nine gg2-sim policies, one per class: the set gg2-sim's
`scripts/runs/matches-best.sh` calls best (its `docs/STATUS.md`, "Round two
summary", 2026-11-11), copied from `gg2-sim/build/` at gg2-sim `9142f46`. Every
one is base layout v2 (`docs/BOTS.md`, "gg2-sim policies"). **A `.gg2p` names
no class of its own**: loaded by `[Bots] Policy` alone each one drives nothing
(the startup log warns), so `[Bots] ClassPolicy` has to assign them, one
`Class=file-name` each. A server given no `--policy` loads these nine with that
assignment already made. Best is
judged against the other classes' policies in gg2-sim's own scoring, not
against the scripted bots, and is gg2-sim's claim: nothing here measures it.

| file | md5 | drives | gg2-sim's note |
|---|---|---|---|
| `ppo-warm-soldier-s1.gg2p` | `5d0089aaa40050ba965d2d161101886b` | soldier | warm start, round two |
| `ppo-finetune-scout-s1.gg2p` | `e92f443986971f983e550b79b6fb23fd` | scout | closed its mirror |
| `ppo-finetune2-heavy-s1.gg2p` | `3bdbca06345f2f51d3796a6d2b74aabf` | heavy | round two |
| `ppo-finetune2-sniper-s1.gg2p` | `8a3cbff6ce3e17af6b3b753fd613f955` | sniper | round two; scopes while driving |
| `ppo-freeze-demoman-s1.gg2p` | `d6a922b4c0e24e513c09bcb98ef684a5` | demoman | freeze seed |
| `ppo-finetune2-pyro-s1.gg2p` | `bcc7fe5ed7edb41fd32bfb9c2e3d236c` | pyro | round two |
| `ppo-freeze-medic-s1.gg2p` | `e9014acc53993a00ffd7a24bfa4f48b6` | medic | freeze seed; low priority |
| `ppo-freeze-engineer-s1.gg2p` | `1e901bb88fd79b64a0ed152d23c15fb2` | engineer | freeze seed; builds while driving |
| `ppo-freeze-spy-s1.gg2p` | `0bbf9157dc701538b053f99916ef185c` | spy | no clear best spy; low priority |

What would show a file stale: gg2-sim naming a different best set in its
STATUS, or a physics change here (a `.gg2p` carries no simulation stamp, so
nothing refuses one trained against an older simulation).

## The duel nets (`duel/`) — what the duel server runs

Ten gg2-sim duel nets, one per class, trained for the duel server's cells
(gg2-sim `docs/HANDOFF-SERVER-DUEL.md` and `docs/STATUS.md`, "Duel nets", at
gg2-sim `cc9ae50`, pin `da4fafb`). Six of them (`ppo-duels-*`, `ppo-duels2-*`)
are gg2-sim's fine-tunes with a share of worlds against its port of the
rollout-search bot this server once ran, copied from its `build/scratch/duelnets/` at gg2-sim `a027aa6`
(pin `98cce00`); the score after each is its `ref searchduels`, 8,000 sampled
duels, the shipped net's and the fine-tune's. The other four already scored
0.98-1.00 there and were not fine-tuned. Each is a **terrain-layout** v2 net (the
Quote one also the Quote layout) and was scored **sampled**, so the duel server
runs them with `[Bots] PolicySampled=1`; `docs/DUEL.md`, "The duel nets", has
the full settings. Exported from gg2-sim's `.pt` checkpoints (md5 of the source
alongside) by a reader equivalent to its `python/export_policy.py`, and
cross-checked bit-exact against its `ref/PolicyDriver.cs` (`docs/BOTS.md`).

| file | md5 | drives | source `.pt` md5 |
|---|---|---|---|
| `ppo-duels2-soldier-s1.gg2p` | `b2897095297d34670b2c1926bb54a65d` | soldier | `6464899c008a8fa5574280025572211b` (search fine-tune, 0.751 -> 0.832 vs the search) |
| `ppo-duels2-scout-s1.gg2p` | `5f1d71f2e06d28e23ef285d7166fed36` | scout | `8d94f971da993042756a7a580423819c` (search fine-tune, 0.561 -> 0.757 vs the search) |
| `ppo-duels2-heavy-s1.gg2p` | `b0de53f5470e06776fb361a0220dfdaa` | heavy | `632e9d497b2fd668f996a4d35c5ed7d5` (search fine-tune, 0.671 -> 0.764 vs the search) |
| `ppo-duel2-demoman-s1.gg2p` | `646d25267962445a1a4a10cbfe183205` | demoman | `345c8364977b73762e67ccb4bf061403` |
| `ppo-duels2-pyro-s1.gg2p` | `8468170a68864ca78f652f9ad89e4c27` | pyro | `d97ce831c38d8f0c75b4c7884cb9e149` (search fine-tune, 0.663 -> 0.823 vs the search) |
| `ppo-duel2-medic-s1.gg2p` | `c9c06c54fd0ab5515a3ad38cabe8382a` | medic | `ef1804fbb78686b9eb08f1306a7d80e5` |
| `ppo-duels-engineer-s1.gg2p` | `6c408380e0df16df31b2afefcfe46441` | engineer | `ba280b0c4f5d088810211969870410f8` (search fine-tune, 0.456 -> 0.566 vs the search) |
| `ppo-duel2-sniper-s1-1000M.gg2p` | `cace34c6b9d33c4067351e616251fb26` | sniper | `4b42a28ef6cb810bc9c696d7a1f9b41b` (1B checkpoint: peaks there) |
| `ppo-duels2-spy-s1.gg2p` | `2e3d18e60475c2a45c81dc0b8f2da10f` | spy | `01db50fd0721df5285add69867b1a7d2` (search fine-tune, 0.439 -> 0.529 vs the search) |
| `ppo-duelq-quote-s1-1000M.gg2p` | `65afd411f324c0ccd32e890dff3ead2d` | Quote | `bf5cf6af9a398760427e7705b4324faf` (1B checkpoint) |

Their win rates are gg2-sim's, against its scripted stand-ins; none has been
measured here against a person.

`ShippedCheckpointTests` pins each file's md5 as listed here, checks all nine
load as team nets, and runs one match in which the soldier net takes the wheel.
`.gg2p` is the only checkpoint format the server loads.
