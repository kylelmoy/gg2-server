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

## The duel nets (`duel/`) — what the duel server runs

Ten gg2-sim duel nets, one per class, trained for the duel server's cells
(gg2-sim `docs/HANDOFF-SERVER-DUEL.md` and `docs/STATUS.md`, "Duel nets", at
gg2-sim `cc9ae50`, pin `da4fafb`). Each is a **terrain-layout** v2 net (the
Quote one also the Quote layout) and was scored **sampled**, so the duel server
runs them with `[Bots] PolicySampled=1`; `docs/DUEL.md`, "The duel nets", has
the full settings. Exported from gg2-sim's `.pt` checkpoints (md5 of the source
alongside) by a reader equivalent to its `python/export_policy.py`, and
cross-checked bit-exact against its `ref/PolicyDriver.cs` (`docs/BOTS.md`).

| file | md5 | drives | source `.pt` md5 |
|---|---|---|---|
| `ppo-duel2-soldier-s1.gg2p` | `8f67b807ab6c200eae5e77cf2406fb68` | soldier | `987091c6fbbfc1c1a22d169661181cd6` |
| `ppo-duel2-scout-s1.gg2p` | `d945283640d795b62db9fb7c8139b69f` | scout | `6abf2a2dc1173dee985b08d6ac8f8a40` |
| `ppo-duel2-heavy-s1.gg2p` | `df87ef7478d79196f6ecc1f0f8fae458` | heavy | `4d7f47bad15037a2506151680c794ccd` |
| `ppo-duel2-demoman-s1.gg2p` | `646d25267962445a1a4a10cbfe183205` | demoman | `345c8364977b73762e67ccb4bf061403` |
| `ppo-duel2-pyro-s1.gg2p` | `61ecca8a63131a1b839ff2873fff9e3c` | pyro | `be5dc17469aad6a3c19ff5b0e5eb1fd0` |
| `ppo-duel2-medic-s1.gg2p` | `c9c06c54fd0ab5515a3ad38cabe8382a` | medic | `ef1804fbb78686b9eb08f1306a7d80e5` |
| `ppo-duel2-engineer-s1.gg2p` | `53c59f702e9349bfd1e753dff54e7b16` | engineer | `a00d5a2195c481e0228aea7edee7655a` |
| `ppo-duel2-sniper-s1-1000M.gg2p` | `cace34c6b9d33c4067351e616251fb26` | sniper | `4b42a28ef6cb810bc9c696d7a1f9b41b` (1B checkpoint: peaks there) |
| `ppo-duel3-spy-s1.gg2p` | `225765b7744b3e532a76cc89395cb6b8` | spy | `f2a8c3c3a9ed562cb405efb633ff55cc` (6B, round three) |
| `ppo-duelq-quote-s1-1000M.gg2p` | `65afd411f324c0ccd32e890dff3ead2d` | Quote | `bf5cf6af9a398760427e7705b4324faf` (1B checkpoint) |

Their win rates are gg2-sim's, against its scripted stand-ins; none has been
measured here against a person.

`ShippedCheckpointTests` pins each file's md5 as listed here, checks all nine
load as team nets, and runs one match in which the soldier net takes the wheel.
`.gg2p` is the only checkpoint format the server loads.
