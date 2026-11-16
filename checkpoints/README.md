# The shipped policy checkpoints

## The gg2-sim nets (`.gg2p`) — what the fleet runs

Nine gg2-sim policies, one per class: the set gg2-sim's
`scripts/runs/matches-best.sh` calls best (its `docs/STATUS.md`, "Round two
summary", 2026-11-11), copied from `gg2-sim/build/` at gg2-sim `9142f46`. Every
one is base layout v2 (`docs/BOTS.md`, "gg2-sim policies"). **A `.gg2p` names
no class of its own**: loaded by `[Bots] Policy` alone each one drives nothing
(the startup log says so), so `[Bots] ClassPolicy` has to assign all nine, one
`Class=file-name` each. Best is
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

`ShippedCheckpointTests` pins each file's md5 as listed here, checks all nine
load as team nets, and runs one match in which the soldier net takes the wheel.
`.gg2p` is the only checkpoint format the server loads.
