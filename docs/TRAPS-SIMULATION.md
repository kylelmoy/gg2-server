# The simulation traps

Every ⚠️ in this file cost a session time. They are the traps, the measured
facts and the rules behind the port of the simulation — phases 3 to 6, plus the
weapon, class and map-object sections that landed after them.

**This file is the distillation, not the specification.** `docs/SIMULATION.md`
has the full rules and the measurements behind each one; what is here is the
part a session needs *in front of it* while it edits the code, and the pointer
to the section of `SIMULATION.md` that settled it. Where the two disagree, read
the reference and fix both.

⚠️ **Nothing here may be re-derived.** `CLAUDE.md`'s rule stands: a fact marked
settled was measured against the running game, and re-measuring it is how a
session spends a day arriving where it started.

### What each project is, and the ordering facts that came with it

- `Gg2.Protocol`, `Gg2.Maps` — phase 0. Every built-in map decodes
  cell-for-cell against `gg2-agent/tools/walkmask.js`.
- `Gg2.Server`, `Gg2.Host` — phase 1. A stock GM8 client joins and reaches the
  team-select screen; `gg2-parity` shows this server's broadcast stream is
  byte-identical to the reference's. (That claim used to end "apart from the CTF
  round timer" — the timer is simulated since phase 6's round.)
- `Gg2.Parity` — phase 2. Recorder, byte diff, stream framer, trace format,
  replayer and drift report. `gg2-parity replay <trace>` runs a recording
  against `EmptySimulation` and reports every divergence by tick, slot, field
  and signed delta. Phase 4 replaces that stand-in by implementing
  `ITraceSimulation`.
- `Gg2.Simulation` — phase 3. `PlaceFree` agrees with the running game exactly
  across 26,496 samples on three maps, gates and player walls included, and
  `good_move_contact_solid`, `move_outside_solid` and `characterHitObstacle` are
  transcribed. `PlaceMeeting` is not written; nothing needs it yet.
  `CollisionLine` **is** written, as phase 5's third oracle — see below.
- `Gg2.Simulation.CharacterPhysics` — phase 4. The character update reproduces
  the running engine **bit for bit** across 48 trajectories on two maps
  (`tests/Gg2.Tests/Fixtures/physics-samples.json`, regenerate with
  `node tools/sample-physics.js`), and replays the checked-in 898-tick bot trace
  closed-loop with **zero** error on both axes. That used to stop at tick 393,
  the first tick on which anything takes damage; phase 5's scattergun removed
  the boundary and the whole 898 ticks are now exact, hp included. No nudges, no
  tolerances.
- ⚠️ **`Projectiles.Move` runs before the characters move, and `Projectiles.Collide`
after.** `move_all_bullets` is a Step event and `Character.events/Step.xml` puts
its position back before the engine's movement pass — so a blast reads every
character's *end-of-previous-tick* position, while a direct hit (a collision
event, which is a later stage) reads this tick's. Measured: a same-tick rocket
jump deals 28.15385 instead of 30 with the pass in the wrong place.
`docs/SIMULATION.md`, "the projectile pass sees the world as it was last tick".

`Gg2.Server` runs the simulation as of phase 4: a player who picks a team gets
  a character at a real spawn point, `MatchSimulation.Tick` advances it, and
  `StateUpdateWriter` serialises it. Tick order is the reference's —
  `processClientCommands`, then `serializeState`, then the character update — so
  one update carries this tick's input with last tick's position.
- `Gg2.Maps.SpawnPoints` — what made that last part reachable. A `PLAYER_SPAWN`
  carries a spawn point id and a group rather than a position
  (`sendEventSpawn.gml`), so a replay resolves it against the map exactly as the
  client does and starts the character on an integer coordinate with both speeds
  zero. Anchoring on a broadcast position instead costs a fifth of a pixel, and
  that was the entire residual phase 4 spent its time chasing.
- The phase-3 reassessment point came out well: collision matches the engine
  exactly, so the risk it was guarding against did not materialise.

### Six roundings, and only one of them is different

This has already been settled by measurement; do not re-derive it, and do not
assume one of them applies where another does.

| Where | Rule | Use |
|---|---|---|
| A real onto the wire | half **away from zero**, then clamp | `Gg2.Protocol.WireQuantise` |
| GML's own `round()` | half to **even** | `Gg2.Protocol.Gml.Round` |
| A position into a collision box | half to **even** | `Gg2.Simulation.CollisionWorld.RoundPosition` |
| A scaled sprite's extent into a bbox | half to **even** | `Gg2.Simulation.SolidRect.FromEntity` |
| A **rotated** sprite's extent into a bbox | half to **even**, per edge | `Gg2.Simulation.Aabb.ForRotatedSprite` |
| `move_outside_solid`'s allowance | half to **even** | `Gg2.Simulation.Movement.MoveOutsideSolid` |

`docs/SIMULATION.md` has the measurements. The reference applies GML's `round()`
to *some* serialised fields and not others, so the call site has to say which it
means — `Character` writes plain `x*5` and `Intelligence` writes `round(x*5)`.

### Phase 5's three oracles, and what they settled

Combat needs primitives phases 0-4 did not. All three are now measured and exact.
Do not re-derive any of them.

- **GM8's RNG is Delphi's** — `seed' = seed*0x08088405 + 1 mod 2^32`, then
  `random(x) = x*seed'/2^32`. `Gg2.Protocol.GmlRandom`, regenerate the fixture
  with `node tools/sample-rng.js`. It is **contract, not incidental**: a weapon
  puts the *seed* on the wire and every client re-derives the shot spread itself,
  so `docs/SIMULATION.md`'s "server-side RNG is free" does not cover weapon fire.
  `Minigun` and `Blade` are exempt — they `randomize()` and send no seed.
- **`distance_to_object` and `place_meeting`** — `Gg2.Simulation.Aabb`,
  regenerate with `node tools/sample-primitives.js`. Both exact over 722 samples.
  ⚠️ `distance_to_object` uses the **facing-inflated** bounding box and
  `place_meeting` does not, so **splash damage depends on which way the victim is
  facing**. And do not reach for `double.Hypot`; it rescales and loses the last
  bit.
- **`collision_line` clips, rounds, then walks pixels** —
  `Gg2.Simulation.CollisionLine`, regenerate with
  `node tools/sample-collision-line.js`. Exact over 9,014 segments.
  ⚠️ **It is not a segment-versus-rectangle intersection.** GM8 clips the segment
  to the instance's horizontal span, rounds the clipped endpoints half to even,
  and steps whole pixels along the longer axis — so a line can register against a
  box it never geometrically enters, by up to half a pixel, and by a *different*
  amount in each diagonal. Three exact intersection tests were tried and
  discarded before the walk was found; do not try a fourth.
  ⚠️ The box is `[bbox_left, bbox_right + 1)` — half-open at the high end only —
  and `image_xscale` is **divided by**, so a left-facing character accepts a
  different half-open interval. That is the second way facing changes a combat
  answer.
  `CollisionWorld.CollisionLineHitsMask` is the map's side of it and
  `LineCollider.ForCharacter` is a character's; the map is reached through
  `Obstacle`, because `CustomMapO` descends from it.
  ⚠️ **The two pixels the facing inflation adds to `bbox_left` are not
  reachable by a hitscan.** They are in the box GM8 reports and the mask rejects
  them. So `distance_to_object` uses the inflated box and `collision_line`
  effectively does not — the two combat primitives disagree about the same
  character, on purpose.

### The four motion variables, measured — and the one place exactness ends

`Gg2.Simulation.Motion`, regenerate with `node tools/sample-motion.js`. **All
four of `hspeed`, `vspeed`, `direction` and `speed` are stored**, and writing any
one rewrites the others. A character never writes `direction`, which is why
phase 4 got away with two fields; a projectile writes it three times before it
moves. `docs/SIMULATION.md` has the full rules; these are the traps:

⚠️ **A *recomputed* value snaps to a whole number within 1e-4 — a billion times
`Gml.Epsilon`, do not confuse them. A value written directly does not.**
`hspeed = 3.00005` keeps its 3.00005 and makes `speed` exactly 3. Both
`direction` and `speed` snap; the OpenGMK transcription this started from snapped
only `direction`, and the running game corrected it.

⚠️ **`lengthdir_x` and `lengthdir_y` snap too, and `cos`, `sin`, `sqrt`,
`point_distance` and `point_direction` do not.** Call `Gml.LengthDirX`/
`LengthDirY`, never a bare `Math.Cos(...) * length`: the engine answers
`lengthdir_y(15, 89.999)` with exactly `-15`, and every scattergun pellet is
nudged fifteen pixels with it.

⚠️ **`motion_add` takes the component path, which does *not* snap** — a
cancelling knockback leaves a 2e-16 residue where an identical-looking
`direction = 90; speed = 4;` would have cleaned it away. The same rows show the
asymmetry: the residue survives in the components and is snapped out of `speed`.

⚠️ **This is the one primitive in the project that is not bit-exact, and no
amount of effort will close it.** GM8 is Delphi and its motion conversions run on
the x87 stack at 80 bits with Intel's `FSIN`/`FCOS`; the port runs on IEEE
doubles. Eight spellings of degrees-to-radians were measured against 486
components and none reproduced the engine. Measured residual: **87% of components
bit-exact, worst case 1.5e-15 per unit of speed** — an order of magnitude inside
GML's own 1e-13 comparison fuzz, and twelve orders below the wire's fifth of a
pixel. `MotionSampleTests` asserts a bound for the trig and stays exact for the
snap. Do not re-derive it and do not try to beat it.

✅ **The mantissa encoder is one file now: `tools/exact-double.js`.** There
were four copies of it and **three of them were wrong**, so the duplication is
gone and `node tools/exact-double.js` self-tests it without a game. All four
samplers use it. Three bugs, each of which silently loses the last bit or the
whole value:

1. `if (v == 0)` is GML's **fuzzy** comparison — a hard zero for anything under
   1e-13, `cos(degtorad(90))` included. That was the known one.
2. ⚠️ **`a * power(2, 52 - floor(log2(a)))` is not always an integer**, and
   the version that fixed (1) claimed it was. `floor(log2(a))` overshoots by one
   whenever `a` sits close enough below a power of two for `log2` to round up
   — `2^53 - 1` is the smallest case — and the result is a **half**-integer that
   GM8 prints as "262143.50". Normalise to the mantissa first, then scale by
   2^53, and let the decoder take the extra bit back out.
3. ⚠️ **`power(2, 52 - e)` overflows to infinity** below about 2^-972. The
   same normalise-then-scale keeps every intermediate inside [1, 2) and
   [2^53, 2^54).

The checked-in fixtures were all generated before this and were audited when (1)
was found: none of them took the fuzzy branch, and none holds a value that could
reach (2) or (3). **They are sound as they stand and must not be regenerated
with an old copy of the encoder** — there is no longer an old copy to reach for.

### One more GM8 built-in combat needs, still unmeasured

- **An alarm set to `n` fires `n` ticks later, and one set to `0` never fires.**
  `Gg2.Simulation.Timers`, transcribed from OpenGMK and pinned by tests but not
  measured against the running game. The stage runs between Begin Step and Step
  and is **alarm-index major**: every instance's alarm 0 before any instance's
  alarm 1.
  ⚠️ **A weapon fires in Begin Step, which is *before* this stage** — so a shot
  is tested against the refire counter last tick left behind, not one that has
  already advanced this tick. `SIMULATION.md` and `MatchSimulation` both said
  the reverse until stage 3.
  ⚠️ **An alarm assigned a fractional number of ticks is rounded to an integer**
  (`Timers.SetAlarm`), or it would step past zero and never fire. Half to even,
  by this codebase's convention — **not measured**, and reachable: a scattergun
  pellet's lifetime is `35` times a factor in `[0.5, 1]`.

### Phase 5 stage 3: the Scattergun, and three things that had to be right

`Gg2.Simulation.Weapons` and `Gg2.Simulation.Projectiles`. Confirmed by an exact
898-tick closed-loop replay containing six blasts, so these are measured, not
read.

- ⚠️ **A shot leaves from the *weapon*, not from the character.**
  `Character.events/End Step.xml:64-65` writes `currentWeapon.x = round(x)`, and
  a weapon fires in Begin Step — so the muzzle is the character's position at
  the end of the **previous** tick, rounded half to even. `WeaponState.X`.
- ⚠️ **`image_angle` is the aim for one stage of one tick, then it is the
  flight path.** `createShot` sets both and the Scattergun scatters only
  `direction` — but `move_all_bullets.gml` writes `image_angle = direction` on
  every pass including the first, which runs before any collision event. So each
  pellet's box follows its own heading. This file said the reverse until the
  script was read.
- ⚠️ **An instance created in Begin Step moves in the same frame.** GM8's
  movement pass walks what exists when it runs. Measured by delaying it and
  watching the trace fail at tick 393.
- **Eighteen draws per blast, in a fixed order** — `choose(0,0,1)` from the
  Shot's Create event, then the speed jitter, then the direction jitter, three
  per pellet. The count is contract: every client re-derives the blast from the
  seed. `perseverant` is stored and read by nothing yet *because* the draw
  cannot be skipped.
- ⚠️ **A projectile's per-tick behaviour is not in its object.**
  `Scripts/Physics/System/move_all_bullets.gml` is, reached from
  `GameServer.events/Step.xml:16`. A `Shot` really has no Step event, and reading
  only `Objects/Projectiles/Shot.events/` is how this repo gave a pellet no
  gravity, no wall test and a frozen `image_angle` for three phases. It falls at
  `0.15` a tick, and it is destroyed the moment it is inside a wall — at the
  muzzle on its first pass, at the destination on every pass. A pellet that
  reaches somebody *through* a wall still deals no damage; it just rarely gets
  that far now. `Gg2.Simulation.Projectiles.Move`,
  `tests/Gg2.Tests/ProjectileMoveTests.cs`.
- ⚠️ **A `Shot`'s trajectory is invisible to the parity harness.** It is
  never serialised, so the 898-tick trace replays with zero error whether or not
  a pellet falls. Every rule above was gated by deleting its line and watching
  only its own tests go red. Do not treat a green trace as coverage of anything
  a state update cannot carry.
- **`gunSetSolids` is not `charSetSolids`.** A bullet ignores `PlayerWall`, is
  stopped by `BulletWall`, and is stopped by every gate regardless of team.
  `CollisionFilter.ForBullet()`.
- **`MatchSimulation.Tick` takes a `TickEvents`**, which reports what the tick
  did that the state does not record (so far: weapon fire and its seed, a death
  and its attribution, and a medic charging or spending an uber, which the server
  turns into `WEAPON_FIRE`, `PLAYER_DEATH`, `UBER_CHARGED` and `UBER`) and carries the one input a tick can be
  *told* — a forced seed, which is how the parity replayer reproduces a recorded
  shot.

### Phase 5 stage 4: death, and the gate that had to exist first

`Gg2.Simulation.Deaths`, `Character.events/End Step.xml:34-47`. Confirmed by the
same 898-tick replay, whose two deaths land on ticks 431 and 432.

- ⚠️ **The `hp <= 0` check `exit`s, and where it sits is observable.** After
  the stair descent, *before* the hp clamp, the facing and
  `currentWeapon.x = round(x)` — so a character that dies this tick never turns
  to face its aim, never drags its weapon along and is never clamped into the
  map. `CharacterPhysics.EndStep` returns the decision rather than acting on it;
  attribution needs the whole roster.
- ⚠️ **The assist redirect is over the *killer's* medic, and it replaces the
  victim's second dealer rather than tie-breaking with it.** `with(lastDamageDealer)
  if (object) if (object.healer) assistant = object.healer;`
- ⚠️ **GM8's truth test is `> 0.5`, so -1 is false.** That is what makes
  `if (object)` safe when the killer is themselves dead (`player.object = -1`),
  and what makes `healer = -1` mean "nobody". `botServerActions.gml:50` says the
  opposite in a comment and is wrong. In the port both are
  `!= CharacterState.Nobody` — **255, not 0**; a healer in slot 0 is a real
  healer.
- **`Clear`, never `RemoveSlot`.** A death is not a departure. The weapon dies
  with the character; the sentry and anything already in the air do not.
- ⚠️ **A state update cannot see a death, so `Replayer` compares the
  message.** A dead character is merely *absent*, updates come every seventh
  tick, and nothing in one names a killer — a port that killed nobody replayed
  the bot fixture with zero mismatches until `Fields.Deaths` was added. Like a
  fire seed, a recorded `PLAYER_DEATH` is compared and **never fed in**, and it
  is read out of the *following* tick's events for the same reason.
- **Since phase 6:** respawn (`player.alarm[5]`, a *player* timer, now in
  `PlayerState`), the `BID_FAREWELL`/`FINISHED_OFF` deaths on a class or team
  change, and the intel drop are all implemented — see the phase 6 sections
  below. ⚠️ The drop lives in `WorldState.Clear`, not in `Deaths.Kill`, because
  it is the *Destroy* event: a departure and a class change run it too, and
  none of the three broadcasts a `DROP_INTEL`.

### Phase 5 stage 5: the Rocketlauncher, and the biggest event in the game

`Gg2.Simulation.Explosions`, `Projectiles.MoveRocket`,
`Weapons.FireRocketlauncher`. `docs/SIMULATION.md`, "Settled: the rocket", has
the full rules; these are the ones that will bite a change.

- ⚠️ **A rocket does not move on a tick it is already colliding**, where a pellet
  moves unconditionally — so a blast lands where it hit rather than a step past
  it. The reference says why in a comment.
- ⚠️ **`image_alpha` is a real destroy.** A rocket past its leash fades and dies
  on the **tenth** fading pass, because 1 - 10×0.1 is 1.4e-16 and GML's `>` is
  fuzzy to 1e-13. `alarm[3]` is re-armed every over-range pass and normally never
  fires. A faded rocket that reaches a wall does no damage at all.
- ⚠️ **GML gives `&&`, `||` and `^^` the same precedence**, left to right. The
  direct-hit condition means `(enemy || self) && !ubered`, not the C reading.
- ⚠️ **GML's `self` is -1**, which is the only reason
  `if (characterHit.id != ownerPlayer.object)` is not an error on a wall
  explosion.
- ⚠️ **The finish-off guard `if (other.ownerPlayer != id)` compares a player
  instance with a character instance and is therefore always true.** Reading it
  as "unless the victim is the shooter" breaks every rocket jump's assist window.
- ⚠️ **The quarter-radius cutoff is a `continue` inside a `with`**, so the outer
  quarter gets no knockback either.
- **A grounded team-mate who is not the shooter is exempt from the entire
  blast**; an airborne one is thrown and still takes no damage. `Obstacle` only,
  so a player wall does not count as ground.
- **`blastRadius` and `explosionDamage` are per-kind lookups; `knockback` is
  per-instance** — the pyro's airblast changes the third and merely restores the
  second.
- **Both of the things this stage left open are now measured** (2026-08-25,
  against the running game; `docs/SIMULATION.md` has the tables):
  **`move_contact_solid` does not move an instance already in collision**, so the
  rocket's contact reposition is exact; and **every solid a map places is a
  filled rectangle** on one of two runtime sprites with origin (0,0), so
  `CollisionWorld.CollisionLineBlocksBullets` now walks the gates and bullet
  walls as well as the map.

⚠️ **`reference-ctf_truefort-combat.trace` can never be a closed-loop gate**, and
that is a property of the recording rather than of the port. All six of its bots
were alive before it started, so there is no `PLAYER_SPAWN` to anchor on and
every character begins from a wire-quantised position — measured, by replaying
the *bots* trace with spawn-point resolution switched off and watching it stop
being exact. It is an open-loop fixture: a bound on drift, never exactness.
`docs/PARITY.md`, "a recording of players who were already alive is open-loop
only", has the table of which kind a recording is and how to tell.

⚠️ **It is also barely a combat recording.** Its first `WEAPON_FIRE` of any kind
is on **tick 647 of 748**, so 86% of it is character physics — implementing a
weapon will not move its residual. A fixture that actually exercises combat early
is still missing.

It has still been worth its weight: it is the only fixture with a `PLAYER_LEAVE`
in it and the only one where a key is pressed for a single tick, and each of
those caught a harness bug that every other recording hid. **When adding a
fixture, ask what state it cannot reach** — and `docs/PARITY.md` now has both
findings written up.

**`reference-ctf_truefort-closedcombat.trace` is the sharpest combat fixture.**
Recorded with the recorder connected first and the map reloaded underneath it, so
all six bots spawn *inside* the recording and every anchor is exact. The port
replays it **closed-loop with zero error for 357 ticks** after the spawn wave —
nine weapon fires and a death — which is the first exactness claim the combat
work has ever been able to make. `docs/PARITY.md` has the recipe.

- The exact window is measured **twice**, and the second number is the one to
  trust when the first one moves. ⚠️ **An exact window can end on a rounding
  boundary rather than on a bug.** An observation is the reference's own position
  rounded to a fifth of a pixel, so a port a ten-thousandth of a unit past the
  boundary reports a mismatch and the same port a ten-thousandth the other side
  reports nothing. That is exactly what tick 1274 is: the port has slot 1 at
  14125.5014 wire units against a reference below 14125.5 — **0.0003 world
  pixels** — and the replay is clean again until **t1673**, where the first real
  divergence is 12.7 wire units. `Gg2.Parity.SubWireProbe` reports the continuous
  quantity; at a tolerance of one wire unit the same fixture gives **756 ticks**.
  ⚠️ It does **not** replace the exact window — a boundary is the one place an
  integer comparison is more sensitive than any tolerance, and dropping the
  one-ULP correction on `0.6` collapses the exact window to 28 while leaving the
  sub-wire one at 756. `docs/PARITY.md`, "an exact window can end on a rounding
  boundary".
- ⚠️ **And t1673, where it really does end, is a `place_free` *threshold* — also
  not a bug.** Slot 1's `x + hspeed` going into t1667 is 2117.5087 against a
  boundary at exactly 2117.5, so the port is stopped by a wall **0.0087 world
  pixels** early, loses its `hspeed` a tick before the reference and turns around
  a tick early. The signature: the reference's *horizontal* state at t1673 and
  t1680 matches the port's at t1672 and t1679 while the *vertical* is in step —
  one axis running a tick early, which only a discrete event can cause.
  `Gg2.Parity.BinSweep` tells that from a rule error by sweeping the anchor
  across its own quantisation bin and counting what reproduces the tick: **34 of
  49** for the port, **0 of 49** with `good_move_contact_solid` broken. Do not
  re-derive this, and ⚠️ **do not try to close it by testing the residuals for a
  bias** — they are autocorrelated, a resting character repeats one `y` for
  dozens of observations, and the test reports z = 16 for nothing.
  `docs/PARITY.md`, "an exact window can also end on a threshold".

**`reference-ctf_truefort-closedsupport.trace` and `-hotsupport.trace` are the
medic/pyro/engineer fixtures** (2026-08-26), and the first recordings in the
project containing any of those three classes. The closed one is **fully**
spawn-anchored — all six bots, by t774, which `closedcombat` is not — and
replays **closed-loop exactly for 412 ticks on trajectory** with none of those
weapons implemented, which is the first evidence their run powers and boxes are
right. Perturbing any one class's `RunPower` collapses it to 6-20 ticks. It also
holds two sentries built inside the recording and a medic uber, both states no
earlier fixture could reach. ⚠️ **`[Classlimits]` does not decide what a bot
picks** — force the roster with `botAdd` after `global.botsEnabled = 0`;
`docs/PARITY.md` has the recipe and the reason.

**`reference-ctf_truefort-hotcombat.trace` is the open-loop combat fixture.**
1,495 ticks recorded thirty seconds into a running match, 21 weapon fires from
tick 12, three deaths and three respawns, with `[Classlimits]` used to keep the
bots to the three classes the port implements. Disabling a weapon fails its
bound, which neither older fixture noticed. `docs/PARITY.md` has the recording
recipe.

### Phase 5 stage 6: the Minegun, and an ordering inside Begin Step

`Gg2.Simulation.Weapons.FireMinegun` / `SpecialStep`, `Projectiles.MoveMine`,
`Explosions.DetonateMine`. `docs/SIMULATION.md`, "the mine, which is not a small
rocket", has the comparison table; these are the traps.

- ⚠️ **The right-click runs *between* the jump and the friction switch.** A mine
  under its owner sets `moveStatus = 1` and the switch reads it the same tick —
  that is a mine jump. `CharacterPhysics` is split into `BeginStepInput` and
  `BeginStepAccelerate` with `Weapons.SpecialStep` between them for this and
  nothing else. (That method is the whole right-click, not just the detonation —
  the Medigun's needle and uber hang off the same key.)
- ⚠️ **A mine's blast is not the rocket's with different numbers.** Different
  `vectorfactor` (not squared, vertical scaled by 0.8), no direct-touch
  correction, self-damage × 5/9, `moveStatus` 1 or 4 and never 2, no speed boost.
- ⚠️ **A stuck mine has no quarter-radius cutoff** — the guard is
  `and other.splashThreshhold`, which the movement pass clears on landing. Stored
  rather than derived from `stickied`, because detonation is a Begin Step and the
  refresh is in Step, so a blast sees last tick's value.
- ⚠️ **Mines die with their owner, silently.** The one exception to "a projectile
  outlives its owner" — they belong to the weapon, and the weapon dies with the
  character.
- ⚠️ **`lobbed < maxMines` is checked before the seed is drawn**, so a demoman at
  eight mines does not advance the shared RNG stream.
- **A mine substeps** (six pixels a hop), **falls at 0.2 capped at 8**, **sticks
  to `Obstacle` only** — a gate kills it — and **has no character collision event
  at all**, so it flies through people.

### Phase 5 stage 7: the Medigun, which is a stored target rather than a shot

`Gg2.Simulation.Healing`, `Weapons.FireNeedleOrUber`. `docs/SIMULATION.md`,
"Settled: the Medigun", has the full rules; these are the ones that will bite.

- ⚠️ **The attack key does not fire this weapon.** `Medigun`'s `User Event 1` is
  the heal beam and broadcasts no `WEAPON_FIRE`; the needle gun is on the
  *right*-click, `User Event 2`. Routing it through the shared fire path applies
  the Scattergun's `readyToShoot and ammoCount > 0` guard, which the Medigun does
  not have, and silences the beam two ticks in three.
- ⚠️ **The aim ray's two ends come from two different instances.** It starts at
  the **weapon** (`other.x` inside `with(Character)`) and ends at the
  **character**'s aim point (`owner.x + lengthdir(300, aim)`), and the
  reconstructed cursor is measured from the character too. `healDistance` is to
  the weapon. Any test that spawns the weapon on its owner cannot tell these
  apart — two in `MedigunTests` displace them on purpose.
- ⚠️ **The healing tier reads the patient's hp before the heal and the uber
  charge reads it after**, four lines apart. A patient one tick short of full is
  topped up and then charges at the *healthy* rate.
- ⚠️ **`timeUnscathed` had never been counted.** `Character.events/Step.xml:25`
  counts it up to 300 and every damage path resets it; the port had the resets
  and not the counter for four phases, because nothing read it until the healing
  ramp did. It also drives the medic's own `alarm[11]` regeneration, +3/+4/+5 hp
  a second, which is likewise new.
- ⚠️ **An ubercharge lasts 241 ticks, not 240.** 240 subtractions of `25/3` from
  2000 leave 7.02e-12 — seventy times GML's fuzz — so `uberCharge <= 0` is false
  for one more tick. Same shape as the rocket's tenth fading pass.
- ⚠️ **Popping the uber reads `keyState`, not the down-or-pressed union**, and
  it is the `else` arm of the needle: a medic who *can* shoot shoots.
- ⚠️ **The weapon's own Begin Step is a separate instance's event** and runs
  after the whole of its owner's. The uber drain lives there;
  `MatchSimulation`'s per-slot block ends with it.
- **The beam ends in the weapon's Step event**, not in `User Event 1` — releasing
  the button means that event is never reached, so `healedThisStep` is the only
  thing that can notice.
- **The needle is a `Shot` that falls at 0.2, deals 4, draws nothing in its
  Create event and broadcasts no seed** — its `7 + random(3)` comes off the
  global stream, like the Minigun's and the Blade's.

⚠️ **A demoman's mine blocks are written from one walk, and for four phases
they were not written at all.** `WeaponState` carried both a `Lobbed` the
simulation maintained and a `MinesLobbed` the serialiser read, and nothing ever
wrote the second — so a demoman with mines out put a **0** on the wire and no
mine records. Self-consistent, therefore silent: no client desynchronised, no
framing error fired, every client simply drew no mines. Fixed 2026-08-26; there
is one field now, `CharacterWire.ToWeaponBlock(in WorldState, int)` fills the
records from the projectile pool in creation order, and a mismatch between the
count and the walk throws. `docs/PROTOCOL.md` has the layout;
`MineUpdateLayoutTests` round-trips the real writer through the real reader,
which is the only way to see bytes that were never written.

### Phase 5 stage 8: afterburn, the Flamethrower, and a correction to the alarm stage

`Gg2.Simulation.Afterburn`, `FlameStats`, `Weapons.FireFlamethrower` /
`RefillFlamethrower`, `Projectiles.MoveBurning` / `BurningHitCharacter`.
`docs/SIMULATION.md`, "Settled: afterburn", has the full rules.

- ⚠️ **A fired alarm rests at -1, and this file used to say 0.**
  `Timers.Advance` now writes `Inactive` when a counter expires. GM8 writes -1
  *before* running the event, which is the only way `Medic.events/Alarm 11.xml`
  and `Scattergun.events/Alarm 5.xml` — both of which re-arm their own alarm —
  can work. It is invisible everywhere except
  `Character.events/Begin Step.xml:67`, which gates the afterburn fade on
  `alarm[0] == -1` **exactly**. Inferred from the reference; still not measured
  live.
- ⚠️ **The falloff curve runs the opposite way from the comment beside it.** A
  point-blank flame adds **0.35** of its heat and one at the end of its life adds
  the full amount. `(flameLife - alarm[0])/flameLife` is small when the alarm is
  high. Do not "fix" it to match the comment.
- ⚠️ **The penetration cap is compared before the victim is recorded**, so a
  flame burns its first target and dies on the *second*.
- ⚠️ **A burning character at or below zero hp takes no afterburn damage and
  the fire takes no credit** — the reference cites its own bug number for it.
  The fuel still burns.
- ⚠️ **The pyro's tank rests at 201.8, not 200**, because the refill guard is
  `ammoCount <= maxAmmo`. The wire carries 202. And the "prevent sputtering"
  line sits *above* the ready-and-loaded guard, so an empty tank is driven
  further negative on every tick the trigger is held.
- ⚠️ **A flame's muzzle carries `owner.equipmentOffset`, which is computed in
  the character's Draw event** — the only place in the game where a drawing
  variable feeds the simulation. A headless server keeps it at 0; up to two
  pixels of divergence on a running pyro. Documented, not modelled.
- **A flame substeps six pixels like a mine**, inherits the pyro's *whole*
  velocity via `motion_add(owner.direction, owner.speed)`, and has **no wall test
  in its collision event** — it burns through walls where a pellet dies in them.
- **The airblast and the `Flare` arrived in stage 12** once their oracles were
  measured — see below.

### Phase 5 stage 9: the Shotgun, which is the Scattergun with four numbers changed

`Gg2.Simulation.Weapons.FireScattergun` serves both. `docs/SIMULATION.md`,
"Settled: the Shotgun". The two `User Event 3`s are line for line identical; what
differs is 5 pellets rather than 6, 7 damage rather than 8, a `random(11) - 5.5`
cone rather than `random(15) - 7.5`, and —

- ⚠️ **no lifetime override at all.** The Scattergun ends every pellet with the
  range compensation `35 * ((min(1, forward/actual) - 1)/2 + 1)`; the Shotgun
  writes nothing, so its pellets keep the `Shot`'s own 40 and a running
  engineer's shots genuinely out-range a standing one's. Deleting the
  compensation would make the two weapons agree, which is why it is the one
  difference with its own test.

⚠️ **A weapon's knockback is part of the trajectory, not only its damage.**
Adding the Shotgun moved `closedsupport`'s **trajectory** window from 412 to
**489** ticks, halved its hp mismatches (473 to 217) and took its deaths from 18
to 14 — all from `motion_add(shot.direction, shot.speed * 0.03)` on each pellet
that lands. When a bound will not move, ask whether a *missing* weapon is holding
it, not only a wrong one.

⚠️ **Building a sentry is a client command, not a keypress.**
`processClientCommands.gml`'s `BUILD_SENTRY` case, extracted into
`serverBuildSentry.gml`. So it does not flow through `MatchSimulation.Tick`'s
inputs at all and a replay has to inject it the way it injects `PLAYER_SPAWN`.
Its preconditions use `collision_circle`, which is now measured — see below.

### Phase 5 stage 11: the sentry, and the one weapon that fires from End Step

`Gg2.Simulation.Sentries`, `SentryState`. `docs/SIMULATION.md`, "Settled: the
sentry", has the full rules; these are the traps:

- ⚠️ **The turret fires from End Step**, after every character's — so a
  character killed this tick is already gone from its scan, and a victim it
  finishes dies in their own End Step next tick. First shot lands **ten** ticks
  after first sight (`noticedTarget` doubles the refire), then one per five.
- ⚠️ **`buildrate = min(buildrate, buildrate + 0.6/45)` is a no-op** — the
  comment beside it lies. The only write that moves the rate is
  `damageSentry.gml`'s `0.6`, and it is **permanent**.
- ⚠️ **`wallSetSolid` is a third solidity list**: Obstacle only. A falling
  sentry passes through player walls, bullet walls and every gate.
  `CollisionFilter.ForSentry()`.
- The class/team half of the Begin Step self-destruct guard **moved into the
  simulation with phase 6** — the whole three-clause guard now fires in
  `Sentries.BeginStep`, on the tick *after* the change, the reference's own
  timing (the old seams fired a tick early).
- ⚠️ **An owner detonating their own sentry broadcasts nothing** — the
  `DESTROY_SENTRY` message goes out only for simulated destructions, and its
  assistant is the **killer's healer** (a sentry has no second dealer).
- ⚠️ **The fixture landing gate cannot see a wrong gravity constant** — an
  eleven-pixel fall lands on the same tick in the same wire bin for 0.6 and
  0.7 (checked by mutation). The digit-exact resting y in `SentryTests` is the
  control that bites.
- **Every projectile treats a sentry differently from a character** — a flame
  dies on one even perseverant with no wall test, a mine sticks instead of
  flying through, a rocket deals 25+30 with no back-out, a mine blast deals
  ×3/2. Do not reuse the character events.

### The sentry's box, the turret's box, and collision_circle — all measured

Measured 2026-08-26 against the running game; `tools/probes/` has the scripts and
`docs/SIMULATION.md` has the tables. These unblocked the sentry and the airblast.

- ⚠️ **A sentry's hitbox does NOT animate, though its sprite does.**
  `SentryRed` has twelve frames whose extent swings from `-8,-1,7,12` to
  `-10,-21,11,12`; `Sentry.xml` declares `<mask>SentryMaskS</mask>`, which has
  **one** frame, and `mask_index` overrides `sprite_index` for all collision. The
  box is **`-8, -7, 7, 12`** about the origin at every `image_index`. Confirmed
  live, not inferred from the PNG.
- ⚠️ **The two-pixel `bbox_left` facing inflation applies to the sentry and the
  turret too**, not just to characters. A sentry's `image_xscale` is its
  `startDirection`, so a left-facing one really is two pixels wider to
  `distance_to_object`.
- **The turret collides as its own sprite** (`object_get_mask` is -1):
  `-12, -8, 18, 3` about origin (15, 12), and **rotated**, so it goes through
  `Aabb.ForRotatedSprite`.
- ⚠️ **A MANUAL mask bound in the sprite XML is clamped to the image.**
  `SentryRed.xml` says `<bottom>40</bottom>` on a 40-tall sprite and GM8 reports
  39. The XML is not a substitute for asking the engine even when it looks like
  one.
- **`collision_circle` is a plain Euclidean test** — hit iff the distance from
  the centre to the instance's *inclusive* bbox is `<= r`. Confirmed on three
  axes at exactly `r` and on the corner diagonal, which is where a pixel walk
  would have shown itself. Nothing like `collision_line`. Only `prec = false` —
  which is the only form GG2 uses. ⚠️ **A fractional centre is floored per
  axis** (second measurement, `probe-airblast.js` — the airblast centres it on
  the victim's real position), a rotated target answers with its reported
  *axis-aligned* box, and the flipped two-pixel inflation counts.

### Phase 5 stage 12: the airblast, the Flare, and the flip that rotates

`Weapons.AirBlast`, `Aabb.ForRotatedSpriteReported`, `Aabb.CircleHits`.
`docs/SIMULATION.md`, "Settled: the airblast, and the flare that rides on it",
has the full rules; these are the traps:

- ⚠️ **The flipped-sprite inflation is applied to the sprite edge *before* the
  rotation.** Measured on 22 boxes; the post-rotation `Left - 2` the turret
  shipped with reproduces none of the flipped rotations and was a real,
  fixture-invisible bug (two pixels on the turret's 375px acquisition radius).
- ⚠️ **`blastAngle = 75` is dead code** — so is the per-victim `angle`. The
  blast's reach is only the rotated `AirBlastO` box (mask `AirBlastMaskS`,
  `9,1,97,28` about (1,14)) plus a per-victim floored-centre circle: r=25
  characters, r=5 projectiles. No cone.
- ⚠️ **The shove has no uber and no hp test**, is measured weapon-to-victim,
  and writes `lastDamageSource = -1` — a pyro can claim a kill with no weapon.
- ⚠️ **A mine's `alarm[0]` is `reflector = noone`, not a destroy** — the one
  projectile lifetime that isn't. `Timers.Advance` special-cases it, and a
  fresh mine's `Lifetime` must rest at -1 or a reflected mine's landing re-arm
  misses its tick.
- ⚠️ **The flare gate reads `keyState`, not the down-or-pressed union** (the
  uber-pop distinction again), and the flare leaves *before* the reflect
  loops, which walk it and skip it on team. It is a `BurningProjectile` with
  `penetrateCap = 0` — dead on its *first* victim — no falloff, and no
  gravity.
- **An extinguish is a re-spawn**: a burning team-mate sheds `realnumflames`
  real Flames owned by the *blasting* pyro at twice the rolled speed
  (`motion_add` of its own vector). `realnumflames` is End-Step state the port
  never held before (`CharacterState.RealNumFlames`).
- **No fixture moved and none can** — the Flamethrower is unseeded, so
  `AirBlastTests`/`AirBlastBoxTests` are the gate, like the flame's.

### Phase 6 groundwork: the player in the state, and the respawn

`Gg2.Simulation.PlayerState`, `PlayerCommands`, the alarm-5 respawn stage in
`Timers`. `docs/SIMULATION.md`, "Settled: the player, the respawn, and a change
of class or team", has the full rules; these are the traps:

- ⚠️ **Make a player with `PlayerState.Fresh` (or `WorldState.AddPlayer`),
  never `default`** — `Team.Red` is 0, so a zeroed player is a red scout ready
  to respawn where the reference starts a spectator. The `CharacterState.Fresh`
  trap wearing another hat.
- ⚠️ **`WorldState.RespawnTime = 0` means autonomous respawn is off**, and it
  is the default on purpose: the parity replayer injects the recording's own
  `PLAYER_SPAWN`s and must not race them with spawn-point picks of its own.
  The reference floors the ini value at one tick, so 0 is unreachable there.
  `GameServer` sets `max(1, seconds*30)`.
- ⚠️ **A class or team change with a live character is a death** —
  `BID_FAREWELL` by their own hand, or `FINISHED_OFF` for whoever damaged them
  last (killer's-healer assist redirect included) — broadcast *before* the
  change message. A characterless change arms `alarm[5] = 1`: a spawn in this
  same tick's alarm stage. A tick's spawn goes out on the *next* flush, like a
  `WEAPON_FIRE` — the old `TrySpawn` was one flush early.
- ⚠️ **The team balance guard refuses the bigger team and the team you are
  already on — including an "any" whose `choose(RED, BLUE)` coin lands on your
  own team.** That refusal is the reference's, not an accident of the port.
- **Every sentry test needs a player now**: the whole Begin Step guard reads
  `Players[slot]`, so a sentry whose owner-slot has no engineer of its team
  dies on the first tick. `Sentries.Build` reads the owner's team from the
  player too.
- ✅ **That "not modelled" list is empty now.** `checkClasslimits` and
  `ServerBalanceTeams` landed with autobalance, the intel drop landed with the
  objective, and the spawn room and the healing cabinet landed on 2026-08-26 —
  see the section below.

### Phase 6: the intel, and the four events that move it

`Gg2.Simulation.Intel`, `IntelState`, `WorldState.{RedIntel, BlueIntel,
RedCaps, BlueCaps}`. `docs/SIMULATION.md`, "Settled: the intel", has the full
rules; these are the traps:

- ⚠️ **A carried briefcase has no instance at all** — `doEventGrabIntel` ends
  with `instance_destroy()`. "Who has it" is `intel` on the *character*, and a
  carried flag serialises as an instance count of **zero**.
- ⚠️ **A flag taken off its base gives its carrier a return timer of 0**
  (`max(0, alarm[0])` on the resting -1), and a drop writes that straight into
  the new instance's `alarm[0]` — **which never fires**. A briefcase dropped in
  the first seconds of a run lies there indefinitely, and the grab guard's
  `alarm[0] <= 1` arm means anyone can pick it straight back up.
- ⚠️ **`!place_meeting(x, y, TeamGate)` is `TeamGate`, not `Gate`, and asks
  nothing about solidity.** `IntelGate` is a *sibling*, so an intel gate does
  not refuse a grab and a team gate does — **including the grabber's own**,
  which is not solid to them. `CollisionWorld.PlaceMeetingKind`, which exists
  for this one call.
- ⚠️ **A drop caused by a death broadcasts nothing** — the reference's own
  comment says a nested event would make the client error out. So the drop is
  in `WorldState.Clear` and covers death, class/team change and departure
  alike; only the ubered drop and the client's `DROP_INTEL` command are sent.
- ⚠️ **`doEventReturnIntel`'s "force carriers to drop" branch is dead code**:
  `event_user(5)` on a `Character` that has no `User Event 5`. Both callers
  reach it with the instance already existing, so it never matters — do not
  model it.
- ✅ **The briefcase's box is measured** (2026-08-27): `-9, -12, +10, +7` about
  the origin, which is what it had always claimed. The engine reports
  `IntelligenceRedS`, `IntelligenceBlueS` and `IntelligenceBaseS` all as
  `0, 0, 19, 19` about origin (9, 12) on a 20x22 image — the bottom two rows are
  transparent — and a **live briefcase on `ctf_truefort` answers `-9, -12, 10, 7`
  directly**, at scale 1 and angle 0 with `mask_index = -1`.
  `SpriteBoxSampleTests`; regenerate with `node tools/sample-sprite-box.js`.
  ⚠️ **It was worth measuring because no fixture could.** Measured by mutation,
  `closedsupport`'s recorded grab moves only when `BoxLeft` shifts two pixels,
  and `BoxRight`/`BoxTop`/`BoxBottom` can each move **sixteen** with every
  fixture still green — a bot walks in from one side and grabs, and the other
  three edges are never the ones it crosses. **Do not read a green fixture as
  confirmation of a box.**
- **A capture starts no grab cooldown** (unlike a drop) and puts the flag back
  on **its own** base. It also sets `GameServer.syncTimer = 1`, an extra
  `CAPS_UPDATE` on the next tick — the port sends the `frame mod 3600`
  heartbeat too, and both were new with the objective.
- **The collision stage is event-major**: every capture resolves before any
  grab, because GM8 keys a collision event by the other object's id
  (`IntelligenceBase*` 104/105, `Intelligence*` 133/134).

⚠️ **`Fields.Intel` is the only way a recording can see the objective at all.**
A briefcase and a carrier's `intel` byte are `FULL_UPDATE`-only and a recording
holds exactly one of those, on the join path before anything has happened — so
a port with no objective replays every fixture with exactly the drift it had
before. Adding the whole of it moved none of the five windows by a tick.
`docs/PARITY.md`, "the objective is the one thing four fixtures observe and
none anchors", has which of the four events can be gated and why the other two
cannot.

### Phase 6: the round, and the ten seconds after it

`Gg2.Simulation.Rounds`, `WorldState`'s round fields, `GameServer.ServeRoundEnd`
/ `OpenMap`. `docs/SIMULATION.md`, "Settled: the round", has the full rules;
these are the traps:

- ⚠️ **`timeLimitMins` and `caplimit` are floored at 1** in `game_init.gml`, so
  **0 is unreachable in the reference and means "no limit" here** — the same
  escape `RespawnTime` uses, and the default. Without it a zeroed `WorldState`
  would be a round whose clock read 0:00 before its first tick, which is every
  unit test and every bot rollout.
- ⚠️ **`RoundDecided` is a flag and `Winner` is a separate field, on purpose.**
  The reference says "nobody has won" with `global.winners = -1` and `Team` has
  no -1 — `Team.Red` is **0**, so a single `Winner` field would make a zeroed
  state say red had already won. The slot-0 trap in its fourth costume.
- ⚠️ **The tie-break is not about players.** `redIsCapping` means *blue's*
  briefcase is not on its own base — carried by anyone, or lying anywhere else.
  A tie with a flag out at 0:00 is **overtime**; a tie with both home is a draw,
  which the wire spells `TEAM_SPECTATOR` (2).
- ⚠️ **`global.mapchanging` opens the gates, and the two solidity lists disagree
  about which.** `charSetSolids` opens both kinds to a character;
  `gunSetSolids` opens only `TeamGate`, so **a bullet is still stopped by an
  intel gate** during the victory screen. Obstacles and player walls never open.
- ⚠️ **Nobody respawns while the map changes, but the alarm is still spent** —
  the guard is inside `PlayerSpawn.gml`, not on `alarm[5]`. That is what makes
  the reset's blanket `alarm[5] = 1` for everybody safe.
- ⚠️ **The winner is decided in Step and broadcast in the *next* tick's Begin
  Step**, after that tick's state update. `Rounds.Step` runs between
  `move_all_bullets` and the characters' Steps, which is where `CTFHUD` sits in
  instance order and is observable — the gates open for the characters moving
  immediately below it.
- ⚠️ **The join path used to name `ServerOptions.Map`, not the map now
  loaded.** Both the `HELLO` and the join block's `CHANGE_MAP` did, and they are
  the same string until the first rotation and different for ever after — so
  every player joining after round one loaded the wrong map and then simulated a
  different world, silently. Found by writing `MapRotationTests`; the fix is
  `_currentMap`, and `OpenMap` exists so `Start` and a rotation cannot drift
  apart again.
- **No recording can gate any of this.** The shortest round the reference
  permits is a minute and the longest fixture is eighty seconds, so no
  checked-in trace holds a `MAP_END`, a `CHANGE_MAP` or one tick of
  `mapchanging`. `RoundTests` and `MapRotationTests` are the whole gate, and
  `MapRotationTests` drives a real server through two complete rounds.

### Phase 6: the control point, and King of the Hill

`Gg2.Simulation.ControlPoints`, `ControlPointState`, `Rounds.StepKoth`,
`CollisionWorld.CaptureZones`. `docs/SIMULATION.md`, "Settled: the control
point", has the full rules; these are the traps:

- ⚠️ **`redPresence` is three-valued, not a count**: 2 for an ordinary player,
  1 for an ubered one, 0 for nobody. An ubered player alone **cannot capture but
  does stop their own team's progress reverting**, which is why it is not a
  bool.
- ⚠️ **A contested point is frozen, not reverting.** Both teams present and the
  meter does not move at all. That is what lets a single body block a KotH win.
- ⚠️ **Reverting the enemy's meter is faster than capturing** —
  `-(1 + cappers*0.5)` against `+cappers` — and **past two cappers the capture
  rate is halved** (`cappers/2 + 1`). A **scout is `capStrength` 2**, the only
  class override in the game.
- ⚠️ **Locking a point discards its meter** (`capping = 0; exit;`), and
  `mapchanging` locks every point in every mode.
- ⚠️ **`pointCapture` leaves `cappingTeam` set.** The tick a point changes hands
  broadcasts it owned *and* being captured by the same team; only the next
  tick's `capping <= 0` clears it.
- ⚠️ **A character is a *point* on a capture zone, not a box** —
  `collision_point(x, y, CaptureZone, 0, 0)`, `prec = 0`. Its origin has to be
  inside. A cloaked spy is not on the point, and neither is one still fading in.
- ⚠️ **The KotH counters are read and *then* decremented**, so `cpUnlock = 900`
  is 901 ticks and the five-second warning lands on 751. Every "n ticks later"
  number in this mode is off by one from the obvious reading.
- ⚠️ **Winning KotH needs the hill uncontested as well as held**:
  `capping == 0 and bluePresence == 0` on top of the timer and the ownership.
- **The control point blocks have no count prefix on the wire** — the client
  builds the same points from the same map. Order is fixed once at load by
  `ControlPoints.Place`. `docs/PROTOCOL.md`, "The KotH HUD block".
- **No fixture reaches any of it**: every checked-in recording is
  `ctf_truefort`, so there has never been a KotH, CP or Arena trace in this
  project. `ControlPointTests` and `DualKothTests` are the whole gate.

⚠️ **Dual King of the Hill's clocks are crossed**: `redTimer` runs down while
**blue's** hill is red's — you win by holding the *enemy's* hill. Both hills
start owned by their own team (`DKothHUD.Create`, not `User Event 0`), taking
one locks your own, the standoff goes to the **lower** clock outside a
thirty-second margin, and ⚠️ **DKotH never sends `UNLOCKCP` where KotH always
does** — the hills unlock themselves in their own Step, which runs before the
HUD's. `docs/SIMULATION.md`, "Settled: Dual King of the Hill".

### Phase 6: map areas, the control points and Invasion

`Gg2.Maps.LevelMap.ForArea`, `Rounds.StepControlPoints` / `StepInvasion`,
`ControlPoints.RelockNeighbour` / `CapTimeFor` / `AssignStartingOwners`.
`docs/SIMULATION.md`, "Settled: map areas" and "Settled: the control points,
attack/defend, and Invasion", have the full rules; these are the traps:

- ⚠️ **A tall map can be several rounds stacked vertically and only one area's
  entities exist.** `cp_dirtbowl` writes six control points and plays two.
  **Narrow with `LevelMap.ForArea(area)` at the load site**, once, and
  everything downstream sees the right world — and ⚠️ **`GameModes.ShapeOf`
  must be given the narrowed map**, or a reader frames control-point blocks by
  the wrong count and the stream desynchronises. The terrain is *not* narrowed:
  the mask is one instance and survives the `with all`.
- ⚠️ **`User Event 0` starts a point locked in Arena/KotH/DKotH and *unlocked*
  in the two CP modes.** Visible only on a point the per-mode rules never write
  to — the neutral middle a symmetrical map opens on. Getting it wrong makes
  that opening fight impossible and no other mode notices.
- ⚠️ **Every point has its own `capTime`**, from a table: symmetrical is a hill
  (`1, 2, 3, 2, 1` × 7 s), attack/defend a ramp (`5, 4, 3, 2, 1` × 6 s). More
  than five points falls through and keeps the default 300.
- ⚠️ **The neighbour rule is one-directional per team, and skips three cases**:
  a neutral point, a blue point at the far left, and a red point at the far
  right. Those last two are the match-winning points and are always takeable.
  In attack/defend a red point is locked unconditionally on top.
- ⚠️ **Attack/defend ignores the configured time limit** (always three minutes),
  **adds 5,400 ticks per capture** and **caps the clock at 9,000** so the `uint`
  it is sent in cannot overflow.
- ⚠️ **CP's overtime is not CTF's latch** — it is re-derived every tick, so it
  switches off the moment a capture meter empties.
- ⚠️ **`CollisionFilter.SetupGatesClosed` had been permanently false since phase
  3.** Nothing set it, so every setup gate was open from the first tick. It is
  fed from `WorldState.SetupTimer` now, mirrored onto the character beside
  `MapChanging` — and ⚠️ **the round clock restarts at setupTimer 30, not 0**.
- ⚠️ **Invasion is not CTF with gates**, and the port was running it through
  CTF's round rules. Red wins on the cap limit; **blue wins by surviving the
  clock**, with a let-off only when red is one capture from winning; and its
  `redIsCapping` is "carried, or dropped with a live return timer", not "away
  from base".
- **`state.BulletFilter`** is now the single construction site for
  `gunSetSolids`' list — both world flags are easy to forget at a call site and
  each is silent when missed.

### Phase 6: Arena, and a framing bug it exposed

`Gg2.Simulation.Arena`, `ArenaState`, the arena arm of
`ControlPoints.StepOne`. `docs/SIMULATION.md`, "Settled: Arena", has the full
rules; these are the traps:

- ⚠️ **`ARENA_ENDROUND` is not a bodyless message and this port framed it as
  one.** It carries five bytes and then five more **per MVP**, so the counts
  have to be read to know the length. A reader would have lost the stream the
  first time a round ended. It survived because no recording has ever been made
  on an arena map — the sentry `INPUTSTATE` bug's twin. `docs/PROTOCOL.md`,
  "Arena".
- ⚠️ **Arena is the only mode where nobody respawns.** `spawningAllowed()` is
  true only while waiting or in the setup period, which is what makes wiping the
  enemy team out a win condition.
- ⚠️ **`cappingPower` is the one capture rate in the game that is not 1, and it
  is crossed**: blue's is computed from *red's* numbers, 1 with every enemy
  alive and 4 with none. And **two different powers are used in one Step** — the
  reverts take the old capping team's, the capture takes the newly-assigned
  team's, and they differ on the first tick of every capture.
- ⚠️ **`respawnEveryone()` calls `PlayerSpawn()` directly, not `alarm[5] = 1`.**
  The alarm stage has already run by the time a HUD's Step does, so arming it
  puts a new round's characters a tick late. `Timers.SpawnNow`.
- ⚠️ **A round ending does not sweep the map** — bodies and sentries stay up for
  the whole scoreboard; only the next restart clears them.
- ⚠️ **The reference's team head-count reads the HUD's own `alarm[5]`**, not the
  player's — a bare `alarm[]` in a plain `for` loop is `self`'s. It is always -1,
  so the clause is dead and the count is "alive", which is what elimination
  wants anyway.
- ⚠️ **The `if(not arenaRoundEnd)` guard around the presence scan is dead code**
  (the autolock has already `exit`ed) and is deliberately not transcribed.
- **The Arena HUD block is the only one whose shape differs between tiers** —
  five extra fields on a `FULL_UPDATE`.

### Phase 6: the generator, and a third message that shortens itself

`Gg2.Simulation.Generators`, `GeneratorState`, `Rounds.StepGenerator`, the
generator arms of `Projectiles.CollideOne` and `Explosions`.
`docs/SIMULATION.md`, "Settled: the generator", has the full rules; these are
the traps:

- ⚠️ **The shield is a discount, not a barrier.** A covered hit deals
  `damage * 0.5` to the generator's own hp **and** takes the full `damage` off
  the shield — so 300 points of shield buy exactly 150 points of generator.
- ⚠️ **`isShieldRegenerating` is also the rate**, because GM8's `true` is 1.
  One point a tick. There is no constant to name.
- ⚠️ **The regeneration alarm is read as a flag by the round** — `alarm[0] > -1`
  is "hit in the last three seconds" — so a generator's alarm must rest at
  **-1**, or a fresh map is permanently in overtime. The `Timers.Inactive` trap
  in a new costume.
- ⚠️ **Overtime reads the alarm of the generator about to *win*.** `redStage >
  blueStage && GeneratorRed.alarm[0] > -1`: the match stays open while the
  *trailing* team is still pushing. The first draft of the port's test table had
  it inverted and the implementation was right — the DKotH pattern exactly.
- ⚠️ **The clock is decided by damage *stage*, not hit points** (3/2/1 at two
  thirds and one third of 2,100), so a team 700 points behind can still draw.
  The one place the simulation cares what is drawn.
- ⚠️ **The explosion has no team test, no uber test and no `dealDamage`.**
  Everything within **400** pixels takes `hp -= 999` and a shove, the winners
  included, and a sentry caught in it does *not* take the build-rate penalty.
- ⚠️ **The `Shot`/`Needle`/flame generator events have no wall test**, where the
  same objects' character and sentry events both open with one. A pellet inside a
  wall still damages a generator.
- ⚠️ **`GENERATOR_DESTROY` names the team that *lost* one**, and
  `sendEventGeneratorDestroy.gml`'s own docstring says the opposite.
- ⚠️ **The HUD block carries one record per *surviving* generator**, so a
  destruction shortens every later update by four bytes —
  `WorldShape.GeneratorCount` is the one shape field a map cannot settle for
  good. Third message in the project with that shape after the sentry's
  `INPUTSTATE` block and `ARENA_ENDROUND`, and invisible to every recording for
  the same reason: `gen_destroy` is the only generator map and nothing has ever
  been recorded on it. Blue's record is written **first**.
- **`MatchState` now holds no objective at all** — the generator was the last of
  its placeholders.

### Phase 6: Team Deathmatch, and a fuzzy comparison that had to be fuzzy

`Gg2.Simulation.DamageApi`, `Rounds.StepTeamDeathmatch`,
`Deaths.CountTeamDeathmatchKill`, `CharacterState.DeathmatchInvulnerable`,
`PlayerState.DeathmatchRespawnBypass`. `docs/SIMULATION.md`, "Settled: Team
Deathmatch", has the full rules; these are the traps:

- ⚠️ **Team Deathmatch is what a map with no objective *is*** —
  `basicRoomSetup`'s chain ends with it. `gg_debug` is a TDM map and has been
  running as one since phase 0.
- ⚠️ **The kills go into `redCaps`/`blueCaps`**, so the mode needs **no
  per-player statistics** to decide a round. Two handoffs claimed the opposite;
  the stats are still missing and still wanted by Arena's MVP lists and the two
  balance routines, but nothing in TDM's round rules needs them.
- ⚠️ **`deathmatch_invulnerable` is a fifth of the ticks it buys**
  (`seconds * 30 * 0.2`), spent at **0.2** a tick — and moving raises the rate to
  1 and **never lowers it again**. The attack key and the right-click end it
  outright, and it is the *key* that does it, not the shot.
- ⚠️ **Its End Step guard and clamp are both fuzzy, and that is load-bearing.**
  150 subtractions of 0.2 from 30 leave **7.3e-14** — inside GML's 1e-13 fuzz —
  so the reference's `<= 0` clamps and a raw C# one does not. Written raw, a
  character is **permanently invulnerable**. Measured, by writing it raw. Same
  family as the uber's 241st tick, opposite outcome (that residue was 7.02e-12,
  seventy times the fuzz).
- ⚠️ **Dying while still protected respawns you next tick with what was left**
  (`Destroy.xml:37`), so killing somebody through their spawn bubble buys
  nothing. The `else` arm zeroes the carry, so it cannot be hoarded.
- ⚠️ **`Gg2.Simulation.DamageApi.Deal` is `dealDamage` and every character damage
  path goes through it now.** Its return value is deliberately ignored at every
  site: a blocked hit still resets `timeUnscathed` and still does its assist
  bookkeeping, exactly as `damageCharacter.gml:3-7` does. The one path that
  bypasses it is the generator explosion's direct `hp -= 999`.
- **The win condition is not in an `else`** (the tick a limit is reached is the
  tick the round ends), **reaching the limit is not winning** (both teams
  crossing at once is a draw), and **`overtime` is dead** — the only mode with
  none.

### Phase 6: the scoreboard, and the two arrays behind it

`Gg2.Simulation.Scoring`, `StatBuffer`, `PlayerState.{Stats, RoundStats}`.
`docs/SIMULATION.md`, "Settled: the scoreboard", has the full rules; these are
the traps:

- ⚠️ **Two arrays, and every award writes both.** All fourteen
  `stats[X] += n` lines in the reference are followed by
  `roundStats[X] += n` — no exceptions — so `Scoring.Award` writes the pair and
  no call site gets the chance to forget one. Only `ARENA_ENDROUND` reads the
  second.
- ⚠️ **The counters are reals.** An assist is worth **half** a point, and
  `write_ubyte` rounds it half away from zero while the host draws
  `floor(stats[POINTS])` — so **a client's scoreboard can read one point above
  the host's**. The reference's own inconsistency.
- ⚠️ **A death is scored before the body is destroyed**, because four of the
  six clauses read the victim's character. And the medic-bonus clause is
  *outside* `killer != victim`, so a medic who kills themselves with a full
  charge collects it.
- ⚠️ **`pointCapture` awards every team-mate standing on the point**, not
  the ones who helped — and it is reachable with an enemy standing there, since
  a full meter still completes on the tick an enemy arrives.
- ⚠️ **`hphealed -= HP_PER_POINT`, not `= 0`** — the overshoot carries. And
  it belongs to the *weapon*, so a medic's progress towards the next point dies
  with their character while the stat survives.
- ⚠️ **A map change wipes both arrays only on area 1**, the same guard the
  team reset uses.
- ⚠️ **Every player on a team is an Arena MVP candidate**, whatever they
  scored and whether or not they ever spawned; the list is trimmed to three
  afterwards. `ds_priority_delete_min`'s tie rule is still unmeasured — the port
  breaks ties by roster index.
- **`StatBuffer` is thirteen entries and not eleven**, and the two spare doubles
  are deliberate: `DOMINATIONS` and `REVENGE` are vestigial in the reference too,
  and keeping them buys away an index mapping between `StatIndex` and the
  buffer. The clone budget moved 128 KB → 160 KB for the scoreboard's 13 KB;
  `WorldStateTests.ACloneStaysWithinItsBudget` carries that audit.

### Phase 6: autobalance, and a fourth self-shortening message

`Gg2.Simulation.TeamBalance`, `Classlimits`, `ClassLimitBuffer`.
`docs/SIMULATION.md`, "Settled: autobalance", has the full rules; these are the
traps:

- ⚠️ **`BALANCE` is variable-length** — one body byte for the notice, two for
  a switch — and `ServerStream` framed it as a fixed two until this landed. The
  **fourth** message of that family, after the sentry `INPUTSTATE` block,
  `ARENA_ENDROUND` and the generator HUD block, and it survived for the same
  reason: no recording reaches the state. **When adding a message, ask what
  makes its length change.**
- ⚠️ **The fuse runs every tick and the check does not.** `balancecounter`
  is incremented at the top of every frame, but `ServerBalanceTeams` is called
  only on a death, a departure and a team change — so a notice can hang
  unresolved on a quiet server.
- ⚠️ **Only dead players move**, and the guard reads the *character*: a
  player with no character is eligible and so is one whose body is on no hit
  points. That is how the balance takes the player who has just been killed.
- ⚠️ **If nobody is eligible the fuse is deliberately not reset**, so the
  next call retries at once.
- ⚠️ **The move is not a death**: no `PLAYER_DEATH`, no credit, no `DEATHS`
  stat, and `alarm[5]` is re-armed only for a player who had a character.
- ⚠️ **Arena has its own script** — it loops until the gap closes, takes
  living players, and arms no respawn.
- ⚠️ **`checkClasslimits` excludes Quote from its random pick and then
  offers it as a last resort**, and hands back the desired class regardless when
  even that is full. Its `irandom` draws off the **shared** stream.
- ⚠️ **A zeroed `WorldState.ClassLimits` means every class is full.** 255 is
  the default and `GameServer` sets it; a hand-built test world must too.

### Phase 6: the domination kill table, the only 2-D thing in the state

`Gg2.Simulation.Dominations`, `WorldState.DominationKills`.
`docs/SIMULATION.md`, "Settled: the domination kill table", has the full rules;
these are the traps:

- ⚠️ **Both axes are roster slots, so `RemoveSlot` shifts it twice** — rows
  with the main compaction loop, columns inside every row. A one-dimensional
  renumbering puts every column one place right and credits dominations to the
  wrong player after any disconnect.
- ⚠️ **A kill wipes the victim's count of the killer** (delete, not
  decrement), so the counter is "kills since they last killed me" and one kill
  back ends a domination outright.
- ⚠️ **The assistant counts as a killer**, gets the increment, and has their
  own count wiped by a kill in return.
- ⚠️ **A team change clears both directions; a class change does not.**
- ⚠️ **The port does not clear on a departure and the reference must.** The
  reference's tables are keyed by player *instance* and survive the roster
  compacting; the port's matrix is keyed by slot, and the shift overwrites every
  cell a clear would zero. Proved by mutation.
- ⚠️ **Self is skipped on the wire**, so a player's row is one byte shorter
  than the roster.
- **The four-kill threshold is presentation only** — kill log and medals — so
  neither branch is transcribed. What is owed is the counts.

### The Minigun, and a line that is worth one tick

`Gg2.Simulation.Weapons.{FireMinigun, WeaponBeginStep}`. `docs/SIMULATION.md`,
"Settled: the Minigun", has the full rules; these are the traps:

- ⚠️ **The muzzle drop is outside the `lengthdir`**: twenty pixels along the
  aim and then **twelve straight down**, whichever way the heavy looks. No other
  weapon offsets asymmetrically.
- ⚠️ **The guard is two and the charge is three**, so a minigun on exactly 2
  fires and lands on -1 — clamped back to 0 by the weapon's own Begin Step.
- ⚠️ **A blocked muzzle costs the shot and nothing else**: the ammo, the
  refire and the reload buffer all run regardless.
- ⚠️ **The tank rests at 201** (`ammoCount <= maxAmmo`), the pyro's 201.8 in
  another costume, and the buffer factor is read **after** the ammo is spent.
- ⚠️ **`alarm[5] += 1` is not what stops a held trigger refilling** — the
  re-arm on every shot is. Measured, the line is worth exactly **one tick**, and
  it reads as frame-rate compensation. Its `!isRefilling` clause is
  unreachable.
- ⚠️ **`Timers`' alarm-5 switch had no Minigun case**, so its refill alarm
  fired and did nothing. **When adding a weapon, check every per-weapon switch,
  not only the fire path.**
- ⚠️ **`randomize()` is deliberately not transcribed** — reseeding
  `WorldState.Random` from a clock would break docs/BOTAI.md item 2. Drawing
  from the existing stream is equally unpredictable to a client, which sees no
  seed either way.

### The Rifle, the scope, and the heavy's Step

`Gg2.Simulation.Weapons.{FireRifle, RifleBeginStep, Hitscan}`,
`PlayerCommands.ToggleZoom`, `CharacterPhysics.HeavyStep`.
`docs/SIMULATION.md`, "Settled: the sniper's Rifle" and "Settled: the heavy
slows down while firing", have the full rules; these are the traps:

- ⚠️ **`runPower` and `jumpStrength` are per-character, with three
  writers** — the sniper's scope, the heavy's Step and the spy's stab freeze.
  They joined `CharacterState`'s "a zeroed character is not a valid one" list,
  so a hand-built test anchor must set them or it stands still and never jumps.
- ⚠️ **`basemaxspeed` is not recomputed, and it is an acceleration *guard*,
  not a speed cap.** A scoped sniper's terminal speed really is two thirds
  (friction balance); what the guard changes is whether the push is applied at
  all, so one thrown above their own equilibrium keeps accelerating.
- ⚠️ **Zoom is never serialised** — a client infers it from the charge byte
  and from a `TOGGLE_ZOOM` that says nothing about which way.
- ⚠️ **The hitscan is a binary search, not a ray cast**, and its **object
  order decides ties**: a character is tested *before* a gate, so one sharing a
  sub-segment with a gate is hit through it. Team-mates and ubered enemies stop
  the bullet and take nothing; the shooter and friendly buildings are invisible
  to it.
- ⚠️ **The range is the damage** — 700 pixels unscoped, 1,500 fully charged.
- ⚠️ **The charge ramp is a square root and is floored**, and it is thrown
  away by unscoping *or* firing.
- ⚠️ **The heavy's Step is the only class-specific Step in the game** and
  reads `keyState` alone, so a one-tick tap fires without slowing them.

### Quote's Blade: two weapons on one magazine, and a projectile that steers

`Gg2.Simulation.Weapons.{FireBubble, ThrowBlade, BladeThrowDamage}`,
`Projectiles.{MoveBlade, BubbleBeginStep, BubbleStep, BladeEndStep,
BubbleCollide, ResolveBubblePair}`. `docs/SIMULATION.md`, "Settled: Quote's
Blade", has the full rules; these are the traps:

- ⚠️ **The left click is free and the right click bills you afterwards.** A
  bubble costs no ammo at all; a thrown blade costs nothing at the throw and
  then drinks **one unit a tick** of its owner's magazine while it flies, with
  its Destroy event charging the difference up to `refireTime` if it came home
  early. So a blade that lands at once costs 5 and one that runs its 15 ticks
  costs 15.
- ⚠️ **A `Bubble` is the one projectile with no `with` block in
  `move_all_bullets`.** It moves in its own Step event, which runs **after**
  every character's Step rather than before — so it needs a phase of its own in
  `MatchSimulation`, and the owner position it reads is `X - HSpeed`, the
  reference's mid-Step value.
- ⚠️ **Its box is rotated by the direction it was fired in, for ever**, because
  nothing overwrites `createShot`'s `image_angle` — a 12x12 square at 45° is
  about 17 wide. A **blade's** is the opposite: forced to 0 and never turned,
  with the reference citing its own issue #230.
- ⚠️ **`Collision with Obstacle` is a wall response, not a wall death** — back
  out, a six-pixel vertical nudge, then zero whichever axis is still blocked.
  The only projectile in the game that survives a wall.
- ⚠️ **A collision between two projectiles runs two events, and the port applies
  both from whichever side it reaches the pair first.** Otherwise the outcome
  depends on creation order and `perseverant` becomes meaningless. **Inferred
  from the reference's design, not measured** — the one thing here worth an hour
  at the next live session, and **still owed**. ⚠️ **A probe for it needs a real
  roster, which is why the cheap version does not work**: `Bubble`'s Begin Step
  destroys it whenever `distance_to_object(owner) > 160`, and GM8's
  `distance_to_object` **excludes self and answers 1,000,000** for a missing or
  self-referential owner — so a synthetic bubble with `owner = id` dies on its
  first frame. Its Destroy event also needs a `gun` carrying `bubbleCount`. Add
  a Quote with `botAdd` after `global.botsEnabled = 0` and fire real bubbles
  (attempted 2026-08-27 and abandoned at that point).
- ⚠️ **`Projectiles.Collide` now walks a snapshot of handles**, because a
  cursor over the live array skips a neighbour whenever anything *other* than
  the current projectile is freed — which bubbles made reachable.
- ⚠️ **A cloaked spy who could stab is invisible to bubbles**, which is why
  `WeaponState.ReadyToStab` landed with Quote rather than with the spy.
- ⚠️ **Three explosions pop bubbles and all three measure differently**: a
  rocket `distance_to_object < blastRadius*0.66`, a generator
  `distance_to_object < blastRadius` (the whole 400), a mine `point_distance <
  blastRadius + 4`.
- ✅ **`BubbleS` is measured now** (2026-08-27): `0, 0, 11, 11` about origin
  (6, 6) — the whole 12x12 image, exactly as the `mode="FULL"` reading said.
  ⚠️ **It could not be sampled the way the other projectile sprites were.**
  `tools/sample-rotated-box.js` creates the projectile and reads its instance
  `bbox_*`, and a bubble is not something `createShot` can be handed;
  `tools/sample-sprite-box.js` asks `sprite_get_bbox_*` instead, which needs no
  instance at all.

### The spy: a cloak on the right click, and a knife that arrives late

`Gg2.Simulation.Weapons.{FireRevolver, TryStab, StabAnimStep, CreateStabMask}`,
`Projectiles.{StabStep, StabConnects, StabHitCharacter}`,
`CharacterPhysics.CloakStep`. `docs/SIMULATION.md`, "Settled: the spy, the cloak
and the knife that arrives late", has the full rules; these are the traps:

- ⚠️ **The attack key does both.** `Revolver.events/User Event 1.xml` is an
  if/else: a bullet if the spy is *not* cloaked, the knife if they are. There is
  no separate melee input, and the stab arm is reachable only when the shot arm
  is refused.
- ⚠️ **Nothing takes damage when the button is pressed.** The stab starts an
  animation and arms `alarm[1] = 32`; that alarm creates the hitbox and then
  arms `alarm[2] = 18` — **through a drag-and-drop `action_set_alarm` that a
  grep for GML code does not see**. Fifty ticks of cooldown, and the cooldown
  gates the **cloak toggle** as well.
- ⚠️ **The freeze is 52 ticks and its length comes from two drawing
  variables** — `image_index >= 32` after a ten-tick delay, then an alpha
  falling 0.1 a tick that dies on the *tenth* subtraction (1.4e-16, the rocket's
  fade again). One tick of it is unmeasured.
- ⚠️ **`stabbing` gates the jump on its own**, and is not redundant with
  `jumpStrength = 0`: the guard above it is `vspeed > -jumpStrength`, which a
  zeroed strength passes, so a falling spy would have `vspeed` set to -0.
- ⚠️ **The knife trails its spy by a tick's velocity** — its Step reads the
  owner's mid-Step position and it has no speed of its own to be moved by
  afterwards. Same phase as a `Bubble`'s, same reason.
- ⚠️ **A stab reaches 31 pixels forward and hits two things**, and
  `image_xscale` mirrors the whole box *and* decides backstab-versus-knife:
  `sign(other.image_xscale) == sign(image_xscale)`, the victim's facing against
  the knife's committed direction.
- ⚠️ **Either half of the reach test is enough**: the *spy's own*
  `place_meeting` against the victim — so a spy inside somebody stabs through
  any wall — or a clear `collision_line_bulletblocking` from twelve pixels above
  the knife to the victim's feet. A stab that reaches neither destroys the whole
  hitbox.
- ⚠️ **A generator takes 0.36 of it and has no line-of-sight test at all**,
  which the character and sentry arms both have.
- ⚠️ **The cloak is on `pressedKeys & Special` — the same key as the right
  click** — can only be *started* from `cloakAlpha == 1`, is refused to an intel
  carrier, and costs 25 ticks of intel grab when dropped. An `else if` freezes
  the fade while a hit has the cloak flickering.
- ✅ **`StabMaskS` is measured now** (2026-08-27): `24, 13, 57, 55` about origin
  (26, 37) on a 64x64 image, so 2 back, 31 forward, 24 up and 18 down. The AUTO
  bound read off the PNG was right. ⚠️ **It is the one of the three where the
  image and the collision box genuinely differ** — 64 wide against 34 — so
  taking the reach from the sprite's dimensions would have been wrong by thirty
  pixels.

### The spawn room, and the two ways the reference asks about it

`Gg2.Simulation.{SpawnRooms, Cabinets}`,
`CollisionWorld.{PointInSpawnRoom, MeetsSpawnRoom, MeetsCabinet}`,
`CharacterState.OnCabinet`. `docs/SIMULATION.md`, "Settled: the spawn room and
the healing cabinet", has the full rules; these are the traps:

- ⚠️ **Every spawn point on every map is inside a spawn room.** Checked on
  four maps and asserted by a test. So the two rules below are the commonest
  thing in a match, not a corner case.
- ⚠️ **A class change inside one broadcasts *nothing*** —
  `processClientCommands.gml:84`'s `else` arm is a bare `instance_destroy()`.
  No `PLAYER_DEATH`, no credit, no death stat, and a one-tick respawn on top.
  **The port broadcast a death there for six phases and two server tests
  asserted it.**
- ⚠️ **A death inside one respawns you next tick**
  (`Character.events/Destroy.xml:37`), and it is still a death — message,
  credit and stat all happen. Neither arm is guarded by the respawn time.
- ⚠️ **The class change asks with `collision_point` and the respawn with
  `place_meeting`.** The origin against the box: there is a band above every
  room where a class change kills you *and* brings you straight back.
- ⚠️ **A cabinet is the one map object that switches off with the round** —
  the whole event is inside `if (!global.mapchanging)`, where every gate opens.
- ⚠️ **Its extinguish is outside the `heal` guard**, so a cabinet with
  everything switched off still puts a burning character out. And Team
  Deathmatch gets ten hp a second and **no nuts and bolts**.
- ⚠️ **`onCabinet` is a tick behind by construction** — written in End Step,
  read by a sentry build, which is a client command processed outside the tick.
- **A BOOL map property is only the exact strings `"true"`/`"false"`.**
  "TRUE", "1" and "yes" silently take the default.

### The three boxes that kill you, and the flag they send home

`Gg2.Simulation.KillBoxes`, and the return arm in `Deaths.Kill`.
`docs/SIMULATION.md`, "Settled: the three boxes that kill you", has the full
rules; these are the traps:

- ⚠️ **One shipped map places any of them.** `tdm_mantic`, six pit falls,
  scanned across all 22. The notes carried this as "six built-in maps place a
  pitfall" for several sessions — it is six *boxes* on *one* map, and a test
  asserts it now. A killbox and a fragbox exist only on custom maps.
- ⚠️ **A box does not kill; it sets `hp = 0`** and the victim's own End Step
  does the rest, so scoring, the kill log and the respawn all behave normally.
- ⚠️ **Somebody else's recent claim survives the box** — the kill is handed
  over and the dealer is *not* cleared. A frag box hands it over as a **gib**
  and the other two as a plain finish-off; that is the whole difference between
  the three events.
- ⚠️ **A pit fall has no `Sentry` event at all**, where a killbox and a
  fragbox do. An engineer's gun survives what its owner does not.
- ⚠️ **A box kill somebody else took the credit for leaves the flag in the
  pit** — the box wrote `FINISHED_OFF` rather than naming itself, and the
  return arm tests the source. (Reading `death.Source` instead is *equivalent*
  today: `Deaths.Resolve` copies the field verbatim.)
- ⚠️ **The return is broadcast where the drop is not**, which is why it is
  in `Deaths.Kill` and not in `WorldState.Clear` beside the drop.
- ⚠️ **The boxes' collision events run after the objective's** (506/509/794
  against 104/105 and 133/134), so a character standing in a pit on a briefcase
  grabs it and *then* dies — which is the only thing that makes the return arm
  reachable at all.

### The four boxes that shove you

`Gg2.Simulation.MoveBoxes`, one line in `MatchSimulation`'s collision stage.
`docs/SIMULATION.md`, "Settled: the four boxes that shove you", has the full
rules; these are the traps:

- ⚠️ **It accelerates, it does not set.** `hspeed += other.pushPower` on every
  tick the boxes overlap, without limit — which is what makes a column of them
  a lift rather than a conveyor belt. A port that assigned instead agrees for
  exactly one tick.
- ⚠️ **`moveStatus = 0` is outside the `run_virtual_ticks` guard** and is the
  half that matters at 30 Hz: a move box **cancels a rocket or mine jump's
  control override** as well as shoving.
- ⚠️ **The push is a *component* write**, so it re-derives `direction` and
  `speed` through `Motion`'s snapping path. Writing the polar pair instead
  agrees on the components and disagrees everywhere the pair is read.
- ⚠️ **One shipped map places any**: `tdm_mantic`, **nine up and six down**,
  none left or right and none overriding `pushPower`. The same map as the six
  pit falls, and the only one with either family. The left/right arms and the
  `speed` property are reachable only from a hand-built entity.
- ⚠️ **Two stacked boxes can cancel exactly, and `tdm_mantic` has such a
  pair** — a character's box is 34 tall against the box's 42, so one standing
  in a lift can overlap a *down* box beside it and go nowhere. Five up plus
  five down is zero. That looks exactly like the push never running, and cost
  this session a test failure.
- **The collision-event interleaving with the killing boxes is not
  observable** — those write `hp`, these write the velocity pair, and two
  pushes compose by addition. So the port runs the family after them and the
  order is free.
- **No recording can gate any of it**: no fixture is on `tdm_mantic`.

### A death lasts 150 ticks, and the recordings are what say so

⚠️ **Twenty-eight death-to-respawn pairs across five fixtures, every gap
exactly 150 ticks, no variance.** `RespawnTimingTests`; `docs/PARITY.md`,
"Settled: a death lasts exactly 150 ticks". Respawn timing had only ever been
gated by unit tests asserting the port against itself, which cannot catch a
counter armed in the wrong event or read in the wrong stage.

- **Read `Alive`, not `RespawnTimer`.** The counter reaching zero and a
  character existing again are two different events an alarm stage apart, and
  only the second is what a client sees.
- ⚠️ **No recorded death is inside a spawn room**, so iteration 43's fast
  respawn has no fixture evidence and is gated by unit tests alone. That is
  asserted rather than commented, so it stops being true loudly — the first
  recording containing such a death **fails the test on purpose**, and is then
  the first thing able to gate the fast respawn against the engine.

### A bound nobody re-measures decays into a formality

⚠️ **Re-measure the parity bounds after any simulation change, not only when a
window is expected to move.** Measured 2026-08-26: after four classes landed and
the collision stage was rewritten, **all five closed-loop windows were unchanged**
(357 / 1,204 / 756 / 489 / 328) — and two *open-loop* bounds nobody had re-run
were a factor of three loose. `hotsupport` asserted p99 |dy| &lt; 60 against an
actual of 17, and the cause was measured rather than guessed: **switching the
engineer's Shotgun off reproduces the bound's original numbers exactly.** So a
weapon's knockback moves the open-loop *percentile* as well as the closed-loop
window — and it does **not** move the maximum, which is one stair step either
way. `docs/PARITY.md`, "the closed-loop windows survived four classes landing".

### An unseeded weapon caps a fixture's hp window, and nothing will raise it

⚠️ **The Flamethrower, the Minigun and the Blade call `randomize()` and
broadcast no seed**, where the other seven weapons send one and every client —
and every replay — re-derives the same spread. So a recording containing a pyro,
a heavy or a Quote has an hp window that ends at the first shot of theirs that
connects, whatever the port does. A flame's spread reaches ±7.2°, which at its
range is wider than a character.

That is what `closedsupport`'s 328-tick hp window is: t1102 is a pyro being burnt
by the other pyro, the port lands seven flames where the reference landed about
six, and the window is **328 with the Flamethrower implemented and 328 with it
stubbed out**. ⚠️ **Two sessions in a row read that bound as "waiting on the
next weapon".** Before believing that, check the *sign* of the residual — the
port was three hp **low**, which no missing weapon can explain — and check
whether an unseeded weapon is in the recording at all.

**The fixture that carries the damage claim is `closedcombat`** (scout, soldier,
demoman — all seeded): its **hp window is 1,204 ticks**, three and a half times
its 357-tick trajectory window, and hp survives both the rounding boundary at
t1274 and the `place_free` threshold at t1673.
`PhysicsParityTests.ThePortReproducesAFightsDamageForFarLongerThanItsTrajectory`.
`docs/PARITY.md`, "an unseeded weapon caps a fixture's hp window".

**When adding a fixture, ask which weapons are in it** — as well as what state it
cannot reach.

### An INPUTSTATE carries no sentry bytes, and that shipped for six phases

⚠️ **A sentry contributes NOTHING to an `INPUTSTATE`-tier update — not even its
hp.** `Sentry.events/User Event 12.xml` guards each group of writes on the tier
and between them they cover only `FULL_UPDATE` and `QUICK_UPDATE`, so on an
`INPUTSTATE` the player's block ends at its `subobjects` byte with bit 1 still
set. Reading the hp byte unconditionally slips one byte per sentry on every tick
that is not a quick update (`GameServerBeginStep.gml:99`) — which is most of
them — and surfaces hundreds of bytes later as "no framing for server message 0".

It survived six phases because **no recording had ever contained an engineer**;
every fixture was scout, soldier, demoman and medic. It was found within minutes
of the first one that did. `docs/PROTOCOL.md`, "Per sentry", has the layout;
`SentryUpdateLayoutTests` gates the arithmetic.

⚠️ **And `TraceParser.Parse` hid it.** A capture can stop mid-message, so the
loop treats a `ProtocolException` as the end of the trace — which makes a
desynchronised reader look exactly like a short recording. Use the overload with
`out string? stopped` in anything that asserts a fixture's *contents*;
`FixtureFramingTests` asserts every checked-in trace consumes its last byte.

⚠️ **A framing error never points at its cause.** Localise it by **bisecting the
roster**: five seconds each of six pyros, six medics, six engineers. Two frame
and one does not, and that is the answer in three minutes.

### Every class writes a FULL_UPDATE weapon block

Two bytes for eight classes, one for a medic, three-plus-mines for a demoman.
This repo said "zero for eight of ten" for four phases and no fixture caught it,
because `gg2-parity record` joins immediately and a `FULL_UPDATE` goes out *on
join* — so no recording contained a character in a full update at all. **When
adding a fixture, ask what state it cannot reach.** `gg2-parity census <trace>`
prints what a recording holds and which message framing stopped on.

### Two things about GM8 arithmetic that are not optional

Both measured against the running game, both in `Gg2.Protocol.Gml`, and both
change trajectories rather than last digits:

- **GM8 parses some decimal literals one ULP above the IEEE nearest** — `0.6`,
  `0.85`, `1.15`, `0.65`, `0.35`, which are gravity, `baseControl`,
  `baseFriction` and two rocket-jump control factors. Never write a physics
  constant as a plain C# decimal; take it from `Gml`.
- **GML's comparisons are fuzzy to `1e-13`, and so is `sign()`.** Every
  real-to-real comparison in `Gg2.Simulation` goes through `Gml.Equal`,
  `Gml.Greater`, `Gml.Sign` and friends. A raw `!= 0` on a value that ordinary
  cancellation left at `-2.7e-14` moves a character six pixels.
