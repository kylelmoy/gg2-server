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

- `GG2.Protocol`, `GG2.Maps` — phase 0. Every built-in map decodes
  cell-for-cell against the JavaScript walkmask decoder.
- `GG2.Server`, `GG2.Host` — phase 1. A stock GM8 client joins and reaches the
  team-select screen; `gg2-parity` shows this server's broadcast stream is
  byte-identical to the reference's, including the CTF round timer.
- `GG2.Parity` — phase 2. Recorder, byte diff, stream framer, trace format,
  replayer and drift report. `gg2-parity replay <trace>` runs a recording
  against `EmptySimulation` and reports every divergence by tick, slot, field
  and signed delta. Phase 4 replaces that stand-in by implementing
  `ITraceSimulation`.
- `GG2.Simulation` — phase 3. `PlaceFree` agrees with the running game exactly
  across 26,496 samples on three maps, gates and player walls included, and
  `good_move_contact_solid`, `move_outside_solid` and `characterHitObstacle` are
  transcribed. `PlaceMeeting` is not written; nothing needs it yet.
  `CollisionLine` **is** written, as phase 5's third oracle — see below.
- `GG2.Simulation.CharacterPhysics` — phase 4. The character update reproduces
  the running engine **bit for bit** across 64 trajectories on two maps
  (`tests/GG2.Tests/Fixtures/physics-samples.json`, regenerate with
  `node gg2-samplers/sample-physics.js`), and replays the checked-in 898-tick bot trace
  closed-loop with **zero** error on both axes, hp included. No nudges, no
  tolerances.
- ⚠️ **`Projectiles.Move` runs before the characters move, and `Projectiles.Collide`
after.** `move_all_bullets` is a Step event and `Character.events/Step.xml` puts
its position back before the engine's movement pass — so a blast reads every
character's *end-of-previous-tick* position, while a direct hit (a collision
event, which is a later stage) reads this tick's. Measured: a same-tick rocket
jump deals 28.15385 instead of 30 with the pass in the wrong place.
`docs/SIMULATION.md`, "the projectile pass sees the world as it was last tick".

`GG2.Server` runs the simulation as of phase 4: a player who picks a team gets
  a character at a real spawn point, `MatchSimulation.Tick` advances it, and
  `StateUpdateWriter` serialises it. Tick order is the reference's —
  `processClientCommands`, then `serializeState`, then the character update — so
  one update carries this tick's input with last tick's position.
- `GG2.Maps.SpawnPoints` — what made that last part reachable. A `PLAYER_SPAWN`
  carries a spawn point id and a group rather than a position
  (`sendEventSpawn.gml`), so a replay resolves it against the map exactly as the
  client does and starts the character on an integer coordinate with both speeds
  zero. Anchoring on a broadcast position instead costs a fifth of a pixel.

### Six roundings, and only one of them is different

This has already been settled by measurement; do not re-derive it, and do not
assume one of them applies where another does.

| Where | Rule | Use |
|---|---|---|
| A real onto the wire | half **away from zero**, then clamp | `GG2.Protocol.WireQuantise` |
| GML's own `round()` | half to **even** | `GG2.Protocol.Gml.Round` |
| A position into a collision box | half to **even** | `GG2.Simulation.CollisionWorld.RoundPosition` |
| A scaled sprite's extent into a bbox | half to **even** | `GG2.Simulation.SolidRect.FromEntity` |
| A **rotated** sprite's extent into a bbox | half to **even**, per edge | `GG2.Simulation.Aabb.ForRotatedSprite` |
| `move_outside_solid`'s allowance | half to **even** | `GG2.Simulation.Movement.MoveOutsideSolid` |

`docs/SIMULATION.md` has the measurements. The reference applies GML's `round()`
to *some* serialised fields and not others, so the call site has to say which it
means — `Character` writes plain `x*5` and `Intelligence` writes `round(x*5)`.

### Phase 5's three oracles, and what they settled

Combat needs primitives phases 0-4 did not. All three are now measured and exact.
Do not re-derive any of them.

- **GM8's RNG is Delphi's** — `seed' = seed*0x08088405 + 1 mod 2^32`, then
  `random(x) = x*seed'/2^32`. `GG2.Protocol.GmlRandom`, regenerate the fixture
  with `node gg2-samplers/sample-rng.js`. It is **contract, not incidental**: a weapon
  puts the *seed* on the wire and every client re-derives the shot spread itself,
  so `docs/SIMULATION.md`'s "server-side RNG is free" does not cover weapon fire.
  `Minigun` and `Blade` are exempt — they `randomize()` and send no seed.
- **`distance_to_object` and `place_meeting`** — `GG2.Simulation.Aabb`,
  regenerate with `node gg2-samplers/sample-primitives.js`. Both exact over 722 samples.
  ⚠️ `distance_to_object` uses the **facing-inflated** bounding box and
  `place_meeting` does not, so **splash damage depends on which way the victim is
  facing**. And do not reach for `double.Hypot`; it rescales and loses the last
  bit.
- **`collision_line` clips, rounds, then walks pixels** —
  `GG2.Simulation.CollisionLine`, regenerate with
  `node gg2-samplers/sample-collision-line.js`. Exact over 9,014 segments.
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
  ✅ **The walk SKIPS open ground and the answer is identical rather than
  approximate** — `Walkmask.Clearance` says how far the next solid
  cell is, so the samples jumped over cannot be the first solid one. It is worth
  −7% of a twelve-bot tick, almost all of it a soldier's ballistic line-of-sight
  test. ⛔ **The parity fixtures do not gate it and cannot**: a recorded hitscan
  is short and the skip only engages in the open. `CollisionLineSkipTests` is the
  gate, and `docs/ARCHITECTURE.md`, "The line walk skips open ground", has the
  measurement and the mutation table — including three mutations that pass
  *because they are still correct*.
  ⚠️ **The two pixels the facing inflation adds to `bbox_left` are not
  reachable by a hitscan.** They are in the box GM8 reports and the mask rejects
  them. So `distance_to_object` uses the inflated box and `collision_line`
  effectively does not — the two combat primitives disagree about the same
  character, on purpose.

### The four motion variables, measured — and the one place exactness ends

`GG2.Simulation.Motion`, regenerate with `node gg2-samplers/sample-motion.js`. **All
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

✅ **The mantissa encoder is one file now: `gg2-samplers/exact-double.js`.** There
were four copies of it and **three of them were wrong**, so the duplication is
gone and `node gg2-samplers/exact-double.js` self-tests it without a game. All four
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

None of the checked-in fixtures take the fuzzy branch or hold a value that
could reach (2) or (3). **They are sound as they stand and must not be
regenerated with an encoder that lacks this fix.**

### The crawl that could not finish

⛔ **`good_move_contact_solid` can loop for ever, and it is not a transcription
bug — the reference loops too.** Reproduced at iteration 78 of the run
`docs/CONTRACT.md` names, on `cp_dirtbowl`, and caught with a temporary
iteration guard in `Movement.GoodMoveContactSolid`:

```
x=2698.437089230609  y=3933.463891267056   dir=118.11026973845824
maxDistance=2.267464623061927  totalMoved=2.267464623061713   i=8
```

⚠️ **Two tolerances disagree, and the gap between them is the bug.**
`maxDistance - totalMoved` is `2.14e-13`. That is **above** `Gml.Epsilon`
(`1e-13`, absolute — "Settled: GML's comparisons are fuzzy" in
`docs/SIMULATION.md`), so `Gml.Less(totalMoved, maxDistance)` says the move is
unfinished. It is **below** half a ULP of the position — a ULP at `y ≈ 3933` is
`4.547e-13` — so `y + step` rounds straight back to `y`, and so does `x`.
`totalMoved` gains exactly `0.0`, `i` stays at `8` because the position is free,
and every later iteration recomputes the same numbers. It is a fixed point, not
a slow loop: it never ends, and no amount of waiting changes that.

⚠️ **The map is not a coincidence and the class probably is.** The window is
`(1e-13, ULP(position)/2]`, so it is **empty below about 1024** and widens as the
coordinate grows: `cp_dirtbowl` is three rounds stacked vertically and is the
tall map, which is where a `y` in the thousands comes from. Both of `gg2-rl`'s
occurrences also had a soldier on the other side, and this repo has **not**
established that a rocket is needed — what the arithmetic needs is a large
coordinate and a residual that lands in the window.

✅ **The fix is one line, and it is the one place the physics deliberately
departs from `Scripts/Physics/good_move_contact_solid.gml`:**

```csharp
if (newX == mover.X && newY == mover.Y)
    break;
```

⚠️ **Nothing observable is given up, and that is provable rather than hopeful.**
Where a step *is* representable the test is false and the arithmetic is
untouched; where it is not, the reference produces no further frames at all —
GM8 spins in the same `while`, so there is no behaviour to diverge from. The
whole 2,343-test suite, every parity fixture included, is unmoved by it, and the
90-iteration training run reproduces its earlier numbers **exactly** through the
iteration that used to hang.

⚠️ **A non-terminating loop cannot be gated by calling it.**
`MovementTests.TheCrawlStopsWhenAStepIsSmallerThanThePositionCanHold` replays
the recorded state on a worker with a ten-second deadline, because a plain call
wedges the whole test run instead of going red. Reverting the one line turns it
red in ten seconds.

⚠️ **The other crawls in the project were checked and are safe**, for reasons
worth keeping: `Movement.MoveOutsideSolid` and `Projectiles.BackOutOfWalls` are
bounded `for` loops; `Projectiles.SubstepWalk` decrements its *remaining vector*
rather than reading it back off the position, so a step the position cannot hold
still ends it; `Weapons.Hitscan` halves a length; the platform lift in
`CharacterPhysics` moves `0.1` at a time, which no reachable coordinate can
absorb. **The shape to look for is a loop whose progress is measured by reading
a coordinate back.**

### The alarm stage, now measured

- **An alarm set to `n` fires `n` ticks later, and one set to `0` never fires.**
  `GG2.Simulation.Timers`, transcribed from OpenGMK and measured against the
  running game. The stage runs between Begin Step and Step and is
  **alarm-index major**: every instance's alarm 0 before any instance's alarm 1.
  ⚠️ **A weapon fires in Begin Step, which is *before* this stage** — so a shot
  is tested against the refire counter last tick left behind, not one that has
  already advanced this tick.
  ✅ **An alarm assigned a fractional number of ticks is rounded half to even**
  (`Timers.SetAlarm`), or it would step past zero and never fire — `0.5 → 0`,
  `1.5 → 2`, `2.5 → 2`, `30.5 → 30`, `31.5 → 32`. Reachable: a scattergun
  pellet's lifetime is `35` times a factor in `[0.5, 1]`.

### Phase 5 stage 3: the Scattergun, and three things that had to be right

`GG2.Simulation.Weapons` and `GG2.Simulation.Projectiles`. Confirmed by an exact
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
  pellet's box follows its own heading.
- ⚠️ **An instance created in Begin Step moves in the same frame.** GM8's
  movement pass walks what exists when it runs.
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
  that far now. `GG2.Simulation.Projectiles.Move`,
  `tests/GG2.Tests/ProjectileMoveTests.cs`.
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

`GG2.Simulation.Deaths`, `Character.events/End Step.xml:34-47`. Confirmed by the
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

`GG2.Simulation.Explosions`, `Projectiles.MoveRocket`,
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
- ✅ **A rocket jump costs its shooter exactly what the same blast costs an
  enemy — GG2 has no self-damage reduction at all.** Confirmed by reading
  `Rocket.events/User Event 5.xml` down through `damageCharacter.gml` and
  `dealDamage.gml`: the damage expression
  `explosionDamage*(1-(fake_distance/blastRadius))` sits on a single line reached
  by both arms of `(other.team != team) or (id==other.ownerPlayer.object)`, and
  nothing below it tests the owner. ⚠️ **The most natural "fix" for a self-kill
  report is a self-damage factor, and it would have been wrong.** Gated by
  `RocketTests.ARocketJumpCostsItsShooterExactlyWhatItWouldCostAnEnemy`, which
  runs the identical geometry twice — once as the shooter, once as an enemy — and
  asserts the two losses are *equal*, so any factor fails rather than passing.
- ✅ **A rocket jump therefore costs at most `explosionDamage`, never
  `hitDamage + explosionDamage`**: the direct hit's 25 is unreachable because the
  whole of `User Event 5` is inside `if(characterHit.id != ownerPlayer.object)`,
  `instance_destroy()` included — a rocket that reports its own shooter as the
  thing it struck neither explodes nor dies. **So the worst a rocket jump can
  cost a soldier is 30 of 160, and it takes SIX from full health to kill.**
  ⚠️ 160/30 is 5.33, not 5, so the count is pinned rather than left in prose.
  ⚠️ **Removing that outer guard is invisible to any test that passes
  `CharacterState.Nobody` as the direct victim** — only
  `RocketTests.ARocketThatStruckItsOwnShooterNeitherExplodesNorDies` exercises
  the arm.
- ⚠️ **Rocket self-damage magnitude is gated by mutation**: halving it fails
  seven parity fixtures (`PhysicsParityTests` ×2, `KothFixtureTests`,
  `CpDirtbowlFixtureTests`, `GeneratorFixtureTests`, `AirblastFixtureTests`,
  `BotAiConstraintsTests` — real players rocket-jump in those recordings) but
  **not** the unit test named after it, which asserts only `Hp < before`. ⚠️ **A
  recording gating a fact diffusely is not the same as a test naming it**: the
  fixtures say a window shrank, not what changed.
- **A grounded team-mate who is not the shooter is exempt from the entire
  blast**; an airborne one is thrown and still takes no damage. `Obstacle` only,
  so a player wall does not count as ground.
- **`blastRadius` and `explosionDamage` are per-kind lookups; `knockback` is
  per-instance** — the pyro's airblast changes the third and merely restores the
  second.
- **Measured against the running game** (`docs/SIMULATION.md` has the tables):
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

It is still worth its weight: it is the only fixture with a `PLAYER_LEAVE` in
it and the only one where a key is pressed for a single tick, and each of those
caught a harness bug that every other recording hid. **When adding a fixture,
ask what state it cannot reach** — `docs/PARITY.md` has both findings.

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
  divergence is 12.7 wire units. `GG2.Parity.SubWireProbe` reports the continuous
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
  `GG2.Parity.BinSweep` tells that from a rule error by sweeping the anchor
  across its own quantisation bin and counting what reproduces the tick: **34 of
  49** for the port, **0 of 49** with `good_move_contact_solid` broken. Do not
  re-derive this, and ⚠️ **do not try to close it by testing the residuals for a
  bias** — they are autocorrelated, a resting character repeats one `y` for
  dozens of observations, and the test reports z = 16 for nothing.
  `docs/PARITY.md`, "an exact window can also end on a threshold".

**`reference-ctf_truefort-closedsupport.trace` and `-hotsupport.trace` are the
medic/pyro/engineer fixtures**, the first recordings in the project containing
any of those three classes. The closed one is **fully**
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

`GG2.Simulation.Weapons.FireMinegun` / `SpecialStep`, `Projectiles.MoveMine`,
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

`GG2.Simulation.Healing`, `Weapons.FireNeedleOrUber`. `docs/SIMULATION.md`,
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

⚠️ **A demoman's mine blocks are written from one walk.** `WeaponState` has a
single `Lobbed` field; `CharacterWire.ToWeaponBlock(in WorldState, int)` fills
the mine records from the projectile pool in creation order, and a mismatch
between the count and the walk throws. (A prior split between a `Lobbed` field
the simulation maintained and a separate `MinesLobbed` the serialiser read was
self-consistent yet silently wrote zero mine records — no framing error, no
desync, just no mines drawn — which is why the walk now throws on mismatch
instead of trusting a count.) `docs/PROTOCOL.md` has the layout;
`MineUpdateLayoutTests` round-trips the real writer through the real reader,
which is the only way to see bytes that were never written.

### Phase 5 stage 8: afterburn, the Flamethrower, and a correction to the alarm stage

`GG2.Simulation.Afterburn`, `FlameStats`, `Weapons.FireFlamethrower` /
`RefillFlamethrower`, `Projectiles.MoveBurning` / `BurningHitCharacter`.
`docs/SIMULATION.md`, "Settled: afterburn", has the full rules.

- ⚠️ **A fired alarm rests at 0 for the rest of its own frame, and the port is
  wrong about it.** Measured (`gg2-samplers/probes/probe-alarm-image.js`): the counter
  reads `…2, 1, 0, -1` on successive frames and the event runs on the frame it
  reads **0**. `Timers.Advance` writes `Inactive` at the moment of firing, one
  tick early. Invisible everywhere except `Character.events/Begin Step.xml:67`,
  which gates the afterburn fade on `alarm[0] == -1` **exactly** and runs
  *before* the alarm stage — so the port starts every burn's intensity decay a
  tick early. **Open finding, not yet fixed.**
  `docs/SIMULATION.md`, "Settled: the alarm stage", has the table.
- ⚠️ **GM8 does not count down an alarm index whose object has no event for
  it.** An `AgentSpare0` given `alarm[1] = 4` still read 4 sixty frames later.
  That is why `Character.events/Alarm 0.xml` exists with nothing in it but the
  comment `Afterburn intensity stuff` — the empty event is **load-bearing**.
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

`GG2.Simulation.Weapons.FireScattergun` serves both. `docs/SIMULATION.md`,
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

`GG2.Simulation.Sentries`, `SentryState`. `docs/SIMULATION.md`, "Settled: the
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

Measured 2026-08-26 against the running game; `gg2-samplers/probes/` has the scripts and
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
- ⚠️ **The reflectable set is `Rocket`, `Flare` and `Mine`**, so a pyro with
  none of those in front of it never presses the key — `closedsupport`
  (medic/pyro/engineer, no rockets or mines) cannot gate the airblast however
  long it runs.
  ✅ **`reference-ctf_truefort-closedairblast.trace`** (three pyros against
  three soldiers) gates it hard: **1,266 exact trajectory ticks with the
  airblast, 286 with `Weapons.AirBlast` stubbed to a no-op.** `AirblastFixtureTests`.
- ⚠️ **An unseeded Flamethrower caps an *hp* window, not a *trajectory* one.**
  `Projectiles.BurningHitCharacter` deals damage and calls no `motion_add`, so
  where the port's flames land cannot move anybody — unlike the Minigun, whose
  pellets shove. That asymmetry is why a pyro recording can carry a trajectory
  gate and a heavy recording cannot. **When a weapon looks ungateable, ask
  whether it moves people or only hurts them.**

### Phase 6 groundwork: the player in the state, and the respawn

`GG2.Simulation.PlayerState`, `PlayerCommands`, the alarm-5 respawn stage in
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
  **It is scored like any death**: `processClientCommands.gml:89/99/162/172`
  call `doEventPlayerDeath`, so the victim's death, the finisher's kill and
  points, the assist, domination and Team Deathmatch's kill all count
  (`PlayerCommands.BidFarewell` → `Scoring.RecordDeath`,
  `Deaths.CountTeamDeathmatchKill`). No parity fixture moved when that was
  added, so `ChangeDeathScoringTests` is the only gate.
- ⚠️ **The team balance guard refuses the bigger team and the team you are
  already on — including an "any" whose `choose(RED, BLUE)` coin lands on your
  own team.** That refusal is the reference's, not an accident of the port.
- **Every sentry test needs a player now**: the whole Begin Step guard reads
  `Players[slot]`, so a sentry whose owner-slot has no engineer of its team
  dies on the first tick. `Sentries.Build` reads the owner's team from the
  player too.
- ✅ **`checkClasslimits` and `ServerBalanceTeams` implement autobalance**; the
  intel drop landed with the objective; the spawn room and healing cabinet are
  implemented — see the section below.

### Phase 6: the intel, and the four events that move it

`GG2.Simulation.Intel`, `IntelState`, `WorldState.{RedIntel, BlueIntel,
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
- **The briefcase's box**: `-9, -12, +10, +7` about the origin, an `AUTO`
  bound of `0, 0, 19, 19` on a 20x22 image with the origin at `9, 12`.
  ⚠️ **No fixture can confirm it and none ever will**:
  by mutation, `closedsupport`'s recorded grab moves only when `BoxLeft` shifts
  two pixels, and `BoxRight`/`BoxTop`/`BoxBottom` can each move **sixteen** with
  every fixture still green. `SpriteBoxSampleTests` is the only gate that bites
  on the other three edges.
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

✅ **`reference-ctf_2dfort-uncontestedcap.trace`** covers it — six red scouts,
no blue team, bots steered to blue's briefcase and home again. **No mismatches
at all over its whole 4,934 ticks**, the longest exact window here, with the
`GRAB_INTEL` at t1355 and the `SCORE_INTEL` at t2867 against an anchor of
t618. Deleting the objective fails the test; mutation-checked.
`IntelCaptureFixtureTests`.
⚠️ **The recipe is the KotH one**: what ends a closed-loop window is weapon
knockback, so **removing the enemy team** is how you buy one long enough to reach
a slow objective — and it costs what it buys, because a long window on a quiet
recording is worth far less per tick than a short one on a loud one.

### Phase 6: the round, and the ten seconds after it

`GG2.Simulation.Rounds`, `WorldState`'s round fields, `GameServer.ServeRoundEnd`
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
- ⚠️ **The join path names `_currentMap`, never `ServerOptions.Map`.** The
  option and the current map are the same string until the first rotation and
  different for ever after, so naming the option would join a player after
  round one to the wrong map, silently. `OpenMap` is what keeps `Start` and a
  rotation from drifting apart; `MapRotationTests` gates it.
- **No recording can gate any of this.** The shortest round the reference
  permits is a minute and the longest fixture is eighty seconds, so no
  checked-in trace holds a `MAP_END`, a `CHANGE_MAP` or one tick of
  `mapchanging`. `RoundTests` and `MapRotationTests` are the whole gate, and
  `MapRotationTests` drives a real server through two complete rounds.

### Phase 6: the control point, and King of the Hill

`GG2.Simulation.ControlPoints`, `ControlPointState`, `Rounds.StepKoth`,
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

✅ **DKotH: `reference-dkoth_atalia-closeddkoth.trace`**, three soldiers a
side, framed cleanly with **two** control points. It gates the two things
nothing else could — **DKotH never sends `UNLOCKCP` where KotH always does**
(the one message that tells the modes apart, with the KotH fixture as the
control), and **both hills start owned by their own team**.

✅ **Invasion: `reference-ctf_avanti-closedinvasion.trace`**, framed cleanly,
three captures. **Invasion is not a map-name prefix** — it is a CTF map that
places a `ControlPointSetupGate` (`GameModes.HasSetupGate`), and the only
shipped map that does is called **`ctf_avanti`**. **Classify a map by asking
the code, never by reading its name.**
`InvasionFixtureTests.ExactlyOneShippedMapPlaysInvasion` asserts the whole
classification across all 22 maps.

⚠️ **`cp_egypt` is the symmetrical control-point map and `cp_dirtbowl` is the
attack/defend one** — five points and no setup gate against two points and one —
so the two CP arrangements are different rule sets.
✅ **Both have a recording**: `reference-cp_egypt-closedsymmetric.trace`,
framed cleanly with **five** points, the largest block anywhere.
Measured live and pinned: **starting owners red, red, neutral, blue, blue**, and
**`capTime` 210/420/630/420/210** — the `1,2,3,2,1 × 7 s` hill, against
attack/defend's `5,4,3,2,1` on a six-second base.
⚠️ **The wire cannot show `locked`**, so the neighbour rule stays unobservable;
ownership is what a recording can hold. ⚠️ **And reading a fact off a fixture is
not a gate on the port** — the ownership is asserted both against the recording
and by loading the map into `PortedSimulation` and asking it, and only the second
bites.

⚠️ **The control-point block is serialised in the server's Begin Step and
describes the end of the *previous* tick.** `Replayer` must compare it against
that, not against the end of the tick it just ran, or a real one-tick capture
delta (`+2` on a soldier recording, `+4` on a scout one) reads as a rule error.
**A control-point divergence has looked like a rule error and been the replay
before — eliminate the harness first.**

⚠️ **The order is `inject unlock → compare → Advance`, and all three parts are
measured.** The unlock injection must stay ahead of the comparison because it
re-locks and zeroes the meter. ⚠️ **A single fixture agreeing is not evidence an
ordering is right** — the wrong order still made `koth_valley` exact; it took a
second CP recording with a different `capStrength` to expose it.
`docs/PARITY.md`, "the capture meter was compared one tick late".

⚠️ **"No mismatches" is satisfied by a comparison that never runs.** Every
comparison family in `Replayer` needs a **liveness control**: a second replay
against a doctored trace asserting the comparison *does* report a difference,
mutated two ways — shifted a tick, and deleted. `Fields.Ubers`,
`Fields.Intel` and `Fields.ControlPoints` have all been found silently unwired
this way (`Fields.Ubers` had existed since phase 5 with no test ever passing it
to a `Replayer`). All five are gated now. **Write a liveness control beside
every `Assert(mismatches.Length == 0)`** — this is a different hole from "the
fixture never reaches the behaviour", and this project has fallen into both.
`docs/PARITY.md`, "every comparison family is now pinned to its tick AND
proved alive".

⚠️ **CENSUS THE SEAMS.** Counting which members of `Fields` any test passes to
a `Replayer` is how `Ammo` and `Flags` were found unused — serialised since
phase 1, never compared. Both were correct and are now gated: ammo on
`closedcombat` (**1,325** exact ticks, every weapon in it broadcasting a seed)
and flags on `closedspy` (**1,829**, cloak bit included), each with a liveness
control and mutation-checked against a real regression. **A seam nothing calls
is indistinguishable from one that is wrong.**
⚠️ **Ammo is the third field to fall under the unseeded-weapon rule**, after hp
and trajectory: `closedheavy` gives **ammo 760 against flags 1,744** on the same
anchor, because the Minigun broadcasts no seed.

✅ **And the census reached the wire itself.** `MessageCoverageTests` pins which
of the protocol's message types the whole fixture corpus contains — **thirty** —
so "what state can no recording reach?" is mechanical rather than remembered.
⚠️ **Four message types are DEAD PROTOCOL**: `RED_TEAM_CAP` (12),
`BLUE_TEAM_CAP` (13), `CP_CAPTURED` (30) and `SERVER_KICK` (37) are in
`Constants.xml` and referenced **nowhere** else in the reference. **A control
point capture reaches the wire as a `CAPS_UPDATE` and nothing else** — do not go
hunting for a recording that sends `CP_CAPTURED`.
⚠️ **Grep the constant name from `Constants.xml`, not the port's C# member
name** — `KICK_MULTI_CLIENT` and `ARENA_WAIT_FOR_PLAYERS` are live and a first
pass called them dead on a spelling.
✅ **`DROP_INTEL`: `reference-ctf_2dfort-dropintel.trace`**. ⚠️ **It is a CLIENT
COMMAND and must be injected**, like a
`BUILD_SENTRY`: nothing in an `INPUTSTATE` says the player pressed drop, so a
replay cannot derive it. `ITraceSimulation.DropIntel` is the seam.
⚠️ **And it is applied BEFORE the tick, the opposite of a grab** —
`processClientCommands` runs ahead of `serializeState`, so the message lands in
its own tick's block where a `GRAB_INTEL` lands in the next one's. **The two
halves of the same objective use opposite tick conventions.**
⚠️ **`message.PlayerId` is null for that whole message family** — `ServerStream`
frames them through `Fixed()`, which skips the body without decoding it, so a
`when message.PlayerId is { }` guard **silently never matches**. Read
`Payload[0]`.
⚠️ **A drop can be followed by an instant re-grab and that is correct**: a flag
off its base has a return timer of 0, the drop writes it into the new instance's
`alarm[0]`, and the grab guard's `alarm[0] <= 1` arm lets anyone take it at once.
Measured — slot 5 dropped and slot 2 had it by the end of the same tick.
**Assert on the named slot, not on the flag.**
✅ **`ARENA_WAIT_FOR_PLAYERS`: `reference-arena_lumberyard-waitforplayers.trace`**
— with it, **no gameplay message the reference can send is unreached by the
corpus**.
⚠️ **The branch needs a round to END with one team having no PLAYERS**, not
merely no living characters — `ArenaHUD.events/Step.xml` tests
`redteam == 0 or blueteam == 0` before falling through to
`serverArenaRestart()`. Removing a team's bots during the scoreboard is what
reaches it.
⚠️ **The census asserts both directions**: several types are held by exactly one
fixture (`Balance`, `ArenaEndRound`, `GeneratorDestroy`, `UnlockCp`, `Uber`), so
a re-recording that came out quiet would otherwise stop exercising a whole
message family in silence.

✅ **The uncontested recipe has now paid four times** — `koth_valley`'s hill,
`ctf_2dfort`'s capture, and both halves of this. **When an objective is too slow
to fit inside a window, remove the enemy team rather than looking for a physics
fix.** ⚠️ It is not *silent*: the one-sided recording still holds fourteen weapon
fires and four deaths. What buys the window is that nothing shoves anybody else.

⚠️ **Dual King of the Hill's clocks are crossed**: `redTimer` runs down while
**blue's** hill is red's — you win by holding the *enemy's* hill. Both hills
start owned by their own team (`DKothHUD.Create`, not `User Event 0`), taking
one locks your own, the standoff goes to the **lower** clock outside a
thirty-second margin, and ⚠️ **DKotH never sends `UNLOCKCP` where KotH always
does** — the hills unlock themselves in their own Step, which runs before the
HUD's. `docs/SIMULATION.md`, "Settled: Dual King of the Hill".

### Phase 6: map areas, the control points and Invasion

`GG2.Maps.LevelMap.ForArea`, `Rounds.StepControlPoints` / `StepInvasion`,
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

`GG2.Simulation.Arena`, `ArenaState`, the arena arm of
`ControlPoints.StepOne`. `docs/SIMULATION.md`, "Settled: Arena", has the full
rules; these are the traps:

- ⚠️ **`ARENA_ENDROUND` is not a bodyless message and this port framed it as
  one.** It carries five bytes and then five more **per MVP**, so the counts
  have to be read to know the length. A reader would have lost the stream the
  first time a round ended. It survived because no recording had ever been made
  on an arena map — the sentry `INPUTSTATE` bug's twin. `docs/PROTOCOL.md`,
  "Arena".
  ✅ **`reference-arena_lumberyard-closedarena.trace`**, three soldiers a side,
  **three round-ends and it frames to the last byte**. Every payload's length
  is exactly `5 + 5 × (mvpsRed + mvpsBlue)`
  re-derived from its own counts, and at least one carries MVP records — the
  control that matters, because all-empty lists would let a fixed-length reader
  frame it perfectly. ⚠️ **Its short trajectory window is by construction**:
  `respawnEveryone()` gives the recording 54 `PLAYER_SPAWN`s across five round
  starts, so its last anchor is very late. `ArenaFixtureTests`.
  ⚠️ **Of the four self-shortening messages, `BALANCE` is now the only one no
  recording reaches.**
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

`GG2.Simulation.Generators`, `GeneratorState`, `Rounds.StepGenerator`, the
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
  *trailing* team is still pushing — the DKotH pattern exactly.
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

`GG2.Simulation.DamageApi`, `Rounds.StepTeamDeathmatch`,
`Deaths.CountTeamDeathmatchKill`, `CharacterState.DeathmatchInvulnerable`,
`PlayerState.DeathmatchRespawnBypass`. `docs/SIMULATION.md`, "Settled: Team
Deathmatch", has the full rules; these are the traps:

- ⚠️ **Team Deathmatch is what a map with no objective *is*** —
  `basicRoomSetup`'s chain ends with it. `gg_debug` is a TDM map and has been
  running as one since phase 0.
- ⚠️ **The kills go into `redCaps`/`blueCaps`**, so the mode needs **no
  per-player statistics** to decide a round — those are wanted by Arena's MVP
  lists and the two balance routines, but nothing in TDM's round rules needs
  them.
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
- ⚠️ **`GG2.Simulation.DamageApi.Deal` is `dealDamage` and every character damage
  path goes through it now.** Its return value is deliberately ignored at every
  site: a blocked hit still resets `timeUnscathed` and still does its assist
  bookkeeping, exactly as `damageCharacter.gml:3-7` does. The one path that
  bypasses it is the generator explosion's direct `hp -= 999`.
- **The win condition is not in an `else`** (the tick a limit is reached is the
  tick the round ends), **reaching the limit is not winning** (both teams
  crossing at once is a draw), and **`overtime` is dead** — the only mode with
  none.

### Phase 6: the scoreboard, and the two arrays behind it

`GG2.Simulation.Scoring`, `StatBuffer`, `PlayerState.{Stats, RoundStats}`.
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

`GG2.Simulation.TeamBalance`, `Classlimits`, `ClassLimitBuffer`.
`docs/SIMULATION.md`, "Settled: autobalance", has the full rules; these are the
traps:

- ⚠️ **`BALANCE` is variable-length** — one body byte for the notice, two for
  a switch — and `ServerStream` framed it as a fixed two until this landed. The
  **fourth** message of that family, after the sentry `INPUTSTATE` block,
  `ARENA_ENDROUND` and the generator HUD block. **When adding a message, ask what
  makes its length change.**
  ✅ **`reference-ctf_2dfort-autobalance.trace`**, four
  soldiers against two — **three `BALANCE` messages, both shapes, framed to the
  last byte**, mutation-checked. Reaching it took three conditions at once: a
  team **two** players larger, **deaths** (the script runs only on a death, a
  departure or a team change), and the fuse expired with a **dead** player
  available (`balancecounter >= Server_Respawntime + 60`, 210 ticks).
  ⚠️ **The map reload re-balances the roster** — a map change sends everybody to
  spectator and the bots re-pick, turning a forced 4/2 into 3/3, so **force teams
  AFTER the reload**. Classes survive it; teams do not. `AutobalanceFixtureTests`.
  ✅ **All four self-shortening messages are now reached by a recording.**
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

`GG2.Simulation.Dominations`, `WorldState.DominationKills`.
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

`GG2.Simulation.Weapons.{FireMinigun, WeaponBeginStep}`. `docs/SIMULATION.md`,
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

`GG2.Simulation.Weapons.{FireRifle, RifleBeginStep, Hitscan}`,
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

`GG2.Simulation.Weapons.{FireBubble, ThrowBlade, BladeThrowDamage}`,
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
- ✅ **A collision between two projectiles runs two events, and the port applies
  both from whichever side it reaches the pair first.** Otherwise the outcome
  depends on creation order and `perseverant` becomes meaningless. **Measured
  2026-08-27** (`gg2-samplers/probes/probe-bubble-pair.js`) across all four cells of
  the team × `perseverant` table, and the port was right. The decisive row is an
  enemy ordinary pellet, where **both** instances die — which no single event
  can do. ⚠️ So **a projectile that destroys itself in its own collision event
  is still there for the other instance's**. `docs/SIMULATION.md`, "Settled:
  Quote's Blade".
- ⚠️ **`Projectiles.Collide` now walks a snapshot of handles**, because a
  cursor over the live array skips a neighbour whenever anything *other* than
  the current projectile is freed — which bubbles made reachable.
- ⚠️ **A cloaked spy who could stab is invisible to bubbles**, which is why
  `WeaponState.ReadyToStab` landed with Quote rather than with the spy.
- ⚠️ **Three explosions pop bubbles and all three measure differently**: a
  rocket `distance_to_object < blastRadius*0.66`, a generator
  `distance_to_object < blastRadius` (the whole 400), a mine `point_distance <
  blastRadius + 4`.
- ✅ **`BubbleS` is sampled now**: `0, 0, 11, 11` about origin
  `6, 6`, exactly the `mode="FULL"` derivation. `SpriteBoxSampleTests`.

### The spy: a cloak on the right click, and a knife that arrives late

`GG2.Simulation.Weapons.{FireRevolver, TryStab, StabAnimStep, CreateStabMask}`,
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
  fade again). ✅ **The tick that was unmeasured is measured**:
  **GM8 advances `image_index` after the whole Step stage**, so a Step reading it
  sees what the previous frame left, the threshold lands on tick **42**, and
  42 + 10 = 52 stands. `docs/SIMULATION.md`, "Settled: GM8 advances
  `image_index` after the whole Step stage".
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
- ✅ **`StabMaskS`'s box is sampled now**: `24, 13, 57, 55` about
  origin `26, 37`, so -2 back and +31 forward — exactly what the PNG said.
  `SpriteBoxSampleTests`.

### The spawn room, and the two ways the reference asks about it

`GG2.Simulation.{SpawnRooms, Cabinets}`,
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

`GG2.Simulation.KillBoxes`, and the return arm in `Deaths.Kill`.
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

`GG2.Simulation.MoveBoxes`, one line in `MatchSimulation`'s collision stage.
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
  five down is zero. That looks exactly like the push never running.
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
- ✅ **A death inside a spawn room is answered on the very next tick — a gap of
  1 against 150 outside.** `reference-ctf_truefort-spawnroomdeath.trace`. The
  arm writes `player.alarm[5] = 1`, so it also confirms the alarm rule end to
  end through the wire. The same recording holds two ordinary 150-tick deaths
  as its control — without one, a gap of 1 is equally well explained by a
  server whose respawn time *is* one tick.
- ⚠️ **A character in a spawn room is on a healing cabinet, so `hp = 0` does
  not kill it.** The cabinet tops it back above zero before End Step tests
  `hp <= 0`. Forcing a spawn-room death needs a value the cabinet cannot undo in
  one tick, and the `place_meeting(x, y, SpawnRoom)` check must be in the *same*
  eval as the assignment or the bot walks out between calls.

### A bound nobody re-measures decays into a formality

✅ **Re-measured across all 25 fixtures after the alarm-stage spawn seam.**
**Thirteen of fourteen closed-loop windows unchanged to the tick**; the
**heavy's moved 270 → 396**, attributed by measurement (switching the queueing
off reproduces 270). `hotcombat`'s open-loop p99s improved from 11/30 to **2/2**
while its maxima stayed at 77/75 — the maxima are single discrete events and the
percentiles are what weapons landing moves.
✅ **The one thing that got worse is diagnosed and fixed.** `hotsupport`'s
open-loop max `|dx|` had gone from 10 to 120 wire units — **24 world pixels,
exactly the spacing of `ctf_truefort`'s spawn markers**, because the port spawned
one marker along.
⚠️ **Forcing the RNG before a tick is fragile, and an unseeded weapon is what
breaks it.** The alarm stage is not the first thing in a tick: weapons fire in
Begin Step, and the Flamethrower, the Minigun and the Blade draw from the same
shared stream the spawn-point pick uses. A replay forces every *seeded* weapon's
seed, so only the unseeded three disturb it — and `hotsupport`'s pyros fire
constantly. `PortedSimulation.SeedIndependentSpawnPoints` fixes it by narrowing
what the draw can land on instead of arranging the stream: every index resolves
to the recorded point, so the result cannot matter. Every closed-loop window is
unchanged and the max is 10 again. `docs/PARITY.md`, "an unseeded weapon moves
the spawn-point draw".
⚠️ **A maximum was the only thing that noticed** — no percentile and no window
moved either way, which is why `SupportClassFixtureTests` now asserts one.
✅ **`SpawnPlacementTests` is the general gate**: every fixture, both modes, no
queued spawn anywhere but its recorded marker
(`PortedSimulation.MisplacedSpawns`). ⚠️ **Its control names the mechanism** —
disabling the narrowed list misplaces spawns in exactly the **three recordings
with a pyro in them**, and two of those are closed-loop fixtures whose windows do
not move either way. ⚠️ **A spawn wave is still on the fragile route** (one list
cannot serve several slots of one team); nothing shoots at a round start, which
is why it holds, and this test is what will say so if that changes.


⚠️ **Re-measure the parity bounds after any simulation change, not only when a
window is expected to move.** After four classes landed and the collision
stage was rewritten, **all five closed-loop windows were unchanged**
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
stubbed out**. ⚠️ **Before reading such a bound as "waiting on the next
weapon", check the *sign* of the residual** — the port was three hp **low**,
which no missing weapon can explain — and check whether an unseeded weapon is
in the recording at all.

**The fixture that carries the damage claim is `closedcombat`** (scout, soldier,
demoman — all seeded): its **hp window is 1,204 ticks**, three and a half times
its 357-tick trajectory window, and hp survives both the rounding boundary at
t1274 and the `place_free` threshold at t1673.
`PhysicsParityTests.ThePortReproducesAFightsDamageForFarLongerThanItsTrajectory`.
`docs/PARITY.md`, "an unseeded weapon caps a fixture's hp window".

**When adding a fixture, ask which weapons are in it** — as well as what state it
cannot reach.

⚠️ **An unseeded weapon caps a *trajectory* window too, not only an hp one.**
On the four new class fixtures: the heavy's is **270** ticks and the sniper's
**362**, against Quote's 1,756 and the spy's 1,829 — and the
short pair are short because the Minigun's spread cannot be reproduced, so the
port shoves people somewhere else. Do not read a short trajectory window as a
physics bug before checking whether an unseeded weapon is in the recording.

### Every class is now in a fixture, and all four new ones frame cleanly

✅ **`reference-ctf_truefort-closed{heavy,sniper,quote,spy}.trace`** — six of
one class each, three a side, all spawn-anchored and closed-loop.
`NewClassFixtureTests`; `docs/PARITY.md`, "the four recordings, and what they
found", has the table.

**The result is a negative and that is the point: all four frame to the last
byte.** No `INPUTSTATE`-style layout bug exists for these four classes. They
also carry four states no earlier recording could reach — a
**cloaked spy** (bit 0 of the flags byte, never before read back from a real
server), a **`TOGGLE_ZOOM`**, an **`OMNOMNOMNOM`** and a **`RETURN_INTEL`**.

- ⚠️ **A long window on a quiet recording is worth less than a short one on a
  loud one.** The spy's 1,829 exact ticks are three times the previous best and
  mean less than `closedcombat`'s 357, because almost nothing in that recording
  depends on a weapon.
- ⚠️ **The recorder contributes a `PLAYER_CHANGECLASS` naming Scout** — it joins
  as a spectator with the default class — so a single-class recording's class
  set is `{the class, Scout}`. Assert the pair; a *seventh* class means the
  roster forcing did not hold.

### A stock client has played a round on the C# server, start to finish

✅ **Phase 6's owed exit criterion, on `ctf_truefort`.** A stock GM8
client joined `gg2-server`, spawned at a real red marker **(1704, 474.40)**,
played until the clock ran out (~1,124 frames), was told the round was a
**draw** (`TEAM_SPECTATOR` = 2 — correct, nobody capped and both flags were
home), survived a clean map change, re-picked blue and spawned at **(3702,
474.40)** for a second round. `docs/PARITY.md` has the table.

⚠️ **Being returned to spectator across a map change is the REFERENCE's
behaviour**, not a port bug — `closedgen` carries 13 `PLAYER_CHANGETEAM`s where
a non-rotating recording carries 7, because the bots re-pick too.

✅ **`gg2-server` gained `--time-limit`, `--cap-limit`, `--respawn-time`.**
Without them no round can finish in a session (the default clock is fifteen
minutes). Its help also still claimed "No simulation yet", wrong since phase 4.

⚠️ **`--time-limit 0` is a ONE-MINUTE round, not an endless one**, and the same
goes for `--cap-limit 0`. `GameServer` applies the reference's own
`max(1, min(255, ...))` (`game_init.gml:96, 101`), so **the "0 means no limit"
escape is `WorldState`'s alone** — it exists so a defaulted world is not a round
whose clock already reads 0:00, and it is unreachable from the CLI on purpose.
Measured 2026-08-27 by a live session that passed `--time-limit 0` expecting an
endless round and had its driven client put back on team-select fifty seconds in
by the map change. **For a long session pass a large number.**
`MapRotationTests.AZeroTimeLimitIsFlooredToOneMinuteRatherThanMeaningNoLimit`,
with `ALimitAboveTheFloorIsNotFlooredToIt` as its control.

✅ **`koth_valley` is done too — the exit criterion is fully satisfied.** A
stock GM8 client joined `gg2-server --map koth_valley`, walked to
the hill at **(2344.40, 606.40)** (`collision_point` against the `CaptureZone`),
captured it and held it while the port ran red's KotH clock **5400 → 0**. The
server logged **`round over: Red wins`**, then **`now playing koth_valley`
exactly ten seconds later**, and the client came through the change with a fresh
round: point **locked** and neutral again, `cpUnlock` counting down from 900,
`redTimer` back at 5400, and the player returned to spectator (the reference's
own map-change reset). The whole KotH loop — lock, unlock, capture, hold, win,
victory screen, map change — runs against a real client.

⚠️ **A live class change is on the same path and works**: `ClientPlayerChangeclass`
mid-match was answered with a death and a respawn on the marker.

### Seven rectangles in the whole game depend on `FromEntity` rounding, and all seven are setup gates

Measured live on a dedicated `ctf_truefort` server.

`SolidRect.FromEntity` computes `Right = RoundPosition(x + width * xScale) - 1`.
⚠️ **Replacing that round with a truncation passed all 2,185 tests.** The rounding
was completely ungated.

A scan across all 22 shipped maps: **7 of 128 collider rectangles differ between
rounding and truncating**, and every one is a `ControlPointSetupGate` on
`cp_dirtbowl` or `ctf_avanti`. Those are the solids that hold players in spawn
during setup, so a one-pixel edge is wire-visible geometry.

✅ **The engine rounds.** Each of the seven was created live at its map position
and scale and its `bbox_right`/`bbox_bottom` read back; all seven match the port
and none matches truncation. Gated by
`SpriteBoxSampleTests.TheSevenSetupGatesThatDistinguishRoundingFromTruncationMatchTheEngine`,
with a companion that asserts those seven are still the whole population so a map
change cannot add an eighth unnoticed.

⚠️ **Send a scale as an integer ratio, never a decimal literal** — `56/100`, not
`0.56`. GM8's parser is a ULP off on some decimals, which would measure a
neighbouring rectangle and blame the port. Same rule as the physics constants.

⚠️ **Why every existing oracle missed it.** `collision-samples.json` enumerates
what is **solid**, and a setup gate is only solid during setup — the same shape of
hole as the dropdown platform, which is never solid at all. **Two colliders have
now escaped that oracle for the same reason; when adding a collider, ask whether
it is ever solid at the moment the oracle samples.**

⚠️ **And the dropdown platform is not where this is visible**, though it looks like
it should be: all four shipped platforms are scaled (**1.6, 0.5, 0.6, 1.2** — none
at 1, which is the only case the pre-existing test covered), but `60 * s` lands on
an exact integer for all four, so rounding and truncation agree: `60 * 0.6` is
exactly 36.0 in `double`.

✅ **The dropdown platform's mask is a filled rectangle and fills its scaled box**:
`collision_point` reports 360 of 360 cells unscaled, 36 of 36 at scale 0.6 and 96
of 96 at scale 1.6, contiguous, nothing outside. `sprite44.xml` declares
`<shape>RECTANGLE</shape>` with `mode="FULL"` and means it.

### `tdm_mantic`'s move boxes: six of nine do nothing, and three hang in the air

⚠️ **The map is the only shipped one with move boxes (fifteen) or pit falls
(six), and no recording of its move boxes is obtainable — by bots or by a
driven client.**

Measured against the running game (`instance_find` over `MoveBoxUp`
and `MoveBoxDown`; the port's map layer agrees, `MoveBoxTests`):

- ⚠️ **Six of the nine up boxes are cancelled by a down box at the same
  `bbox_left`, six pixels below.** The engine answered `111111000` to
  "how many down boxes sit at `bbox_top + 6`". A character's box is 34 tall
  against the box's 42, so it overlaps both, and **five up plus five down is
  exactly nothing**. Two thirds of this map's move boxes are inert.
- ⚠️ **The three that work hang in open air**, at `(2028,1014)`, `(2100,1266)`
  and `(2160,852)` — a diagonal chain of updraft fans, not a column. `pushPower`
  is the default 5 on every box; none overrides `speed`.
- ✅ **They do work.** A character placed in the `(2028,1014)` fan **hovers
  indefinitely**, oscillating y ≈ 915 ↔ 1003 on a ~30-frame period. The rule is
  right; it is the *approach* that is impossible.
- ⚠️ **And a driven scout cannot reach any of them.** Measured jump apex is
  **58 px**; the platform under the middle fan is 75 px short of its catch
  window and the floor under the lowest fan is 123 px short. Walking off the
  platform edge at run speed overshoots the lowest fan horizontally — measured,
  the character passes `x≈2210` at the fan's `y`, where the window ends at 2147.
  **The remaining idea is a rocket or mine jump from a driven soldier or
  demoman**, with aim written into the client's own input (aim is part of
  `INPUTSTATE`, so it is a legitimate input rather than a teleport). ⚠️ A
  **teleport is not an option** — it breaks the closed-loop anchor.

**`MoveBoxTests` and `KillBoxTests` remain the whole gate for both families.**
Two of `MoveBoxTests` now pin the map's own geometry so a future session is told
this by a red test rather than by another wasted day.

✅ **Worth keeping from the attempt: steering bots cannot invalidate a
closed-loop fixture.** A replay consumes recorded `INPUTSTATE`s and simulates no
bot AI, so `botGoalLocked`/`botSetGoal` change *what gets recorded* and nothing
about how it replays. Unlike a **teleport**, which moves a character the port
would have had to derive and breaks the anchor.

### A mode change is invisible to a reader unless a HUD-bearing update follows

✅ **`reference-gen_destroy-rotation.trace`** rotates `gen_destroy`
→ **`ctf_2dfort`** at t2622 with 669 ticks after it — the first fixture that
changes to a different map **and mode** mid-stream.

⚠️ **`TraceParser` takes one shape for the whole trace**, and the shapes are
incompatible (`Generator`'s HUD is **13** bytes, `CaptureTheFlag`'s is **5**;
parsing this trace with the CTF shape fails at byte 432). It framed anyway
because **only `FULL_UPDATE` and `CAPS_UPDATE` carry the HUD block** —
`StateUpdateReader` returns before the tail on every other tier. Here the caps
updates are at t389/t2188/t2189, **all before the rotation**, and the 95 quick
updates after it read no HUD.

**So the wrong-shape risk is real but latent**: it needs a caps or full update
*after* a mode change — a capture, a join, or a scoreboard sync on the new map.

✅ **Reached and fixed.**
`reference-gen_destroy-rotationcaps.trace` forces one with
`GameServer.syncTimer = 1` (what a capture does) and **does not frame** — a
single shape stops at **byte 79,467 of 124,340**. `TraceParser.Parse` now takes
an optional `Func<string, WorldShape>` and **re-shapes on `CHANGE_MAP`**;
`gg2-parity`'s census and replay and `FixtureFramingTests` all pass one.
`ModeChangeFramingTests` gates it, with a control asserting a single shape
cannot frame that recording.

⚠️ **A desynchronised reader's counts are wrong, not merely short**: census
reported **2** `CAPS_UPDATE`s where the file has **4**. Trust nothing after the
stop, and re-check anything derived before it.

Two traps in arranging it:

- ⚠️ **`MapRotation` in the ini is a FILE PATH, not a list** (`game_init.gml:94`);
  with no file the rotation holds a single entry — the current map.
- ⚠️ **Red winning does not rotate**: `GameServerBeginStep.gml:107` advances the
  *area* and keeps the map. **Blue has to win** for `nextMapInRotation()` to run.

### The generator block does not shorten, and probably cannot

⚠️ **`reference-gen_destroy-closedgen.trace`** contradicts a
standing risk. docs/PROTOCOL.md says the HUD carries one record per *surviving*
generator, so a destruction shortens later updates and a reader must decrement
`WorldShape.GeneratorCount`. **Nothing in `ServerStream` decrements it, and it
does not have to**: the wire carries **two** records for the whole recording,
including the 300 ticks between the destruction and the map change. Measured —
parsing at 1 desyncs at byte 436, at 3 at byte 444, at **2 it is clean**.

⚠️ **Why:** destroying a generator ends the round at once, and **a round ending
does not sweep the map** — the dead generator's instance survives the
scoreboard, so the HUD still writes it. `gen_destroy` gives each team exactly
one generator, so destroying one *always* ends the round. **No shipped map can
reach the shortened block**; a custom one with two per team could.

Two more measured facts from it:

- ⚠️ **`GENERATOR_DESTROY` names the team that LOST one** — payload is blue,
  whose generator died, and the `MAP_END` a tick later names red as winner. The
  reference's own docstring says the opposite; the message pair settles it.
- ✅ **The map changes exactly 300 ticks after `MAP_END`** — the documented ten
  seconds, measured.

⚠️ **The rotation returns to the same map, so the wire shape never changes.**
`TraceParser` takes **one shape for the whole trace**; a rotation to a different
mode would frame the rest wrongly. Unreached.

### The first multi-area fixture would not frame, and the readers were at fault

⚠️ **`reference-cp_dirtbowl-closedcp.trace`** framed **458 of
138,406 bytes** on its first outing, silently dropping the rest. `cp_dirtbowl`
writes **six** control points and plays **two**, the HUD block has **no count
prefix**, and every *reader* was passing the un-narrowed map to
`GameModes.ShapeOf` — `gg2-parity`'s census, replay and diff, plus
`FixtureFramingTests`. All fixed with `.ForArea(1)`; narrowed it frames to the
last byte, 3,585 ticks, with a **905-tick** window on
`Trajectory|Hp|Deaths`.

⚠️ **`PortedSimulation.LoadMap` had narrowed correctly since phase 6** and even
carried a comment predicting this recording. **The gap was in the readers, not
the simulation** — so when a comment says "this would need X if a fixture ever
did Y", check the *other* side of the seam too.

⚠️ **Narrowing is a no-op on every single-area map**, which is every other
fixture, so nothing but a recording on a map like this could have caught it.
`CpDirtbowlFixtureTests.TheUnNarrowedShapeCannotFrameThisRecording` is the
control that keeps the fix honest.

⚠️ **Still unhandled: an area that advances mid-recording**, which changes the
control-point count underneath the reader — the generator HUD block's shape in
another costume. No recording reaches it yet.

### King of the Hill has a fixture, and a control point still cannot be compared

✅ **`reference-koth_valley-closedkoth.trace`** — the first KotH
recording, **framed cleanly**, 330 exact ticks on `Trajectory|Hp|Deaths`, the
project's first `UNLOCKCP`, and 39 `CAPS_UPDATE`s against a CTF fixture's one or
two.

⚠️ **The control-point block carries no count prefix**, so a reader must rebuild
the points from the map in the same order — and this is the only fixture that
can catch getting it wrong. Proved by mutation: a wrong `ControlPointsFor` fails
this fixture's framing and **no other fixture at all**.

✅ **`Fields.ControlPoints` closed that gap** and found two harness
bugs on its first run:

- **`PortedSimulation.LoadMap` never called `ControlPoints.Place`** — a replay
  of any KotH, CP or Arena map had **no control point and no capture zone at
  all**.
- ⚠️ **And fixing that alone was not enough**: `ControlPoints.Place` switches on
  `state.GameMode`, and a defaulted `WorldState` is Capture the Flag, which
  places none of a KotH map's. Set the mode from the map first, as `GameServer`
  does.

⚠️ **A control point reaches the wire only on a `CAPS_UPDATE` or the join
`FULL_UPDATE`** — `StateUpdateReader` returns before the HUD tail on every other
tier. So `TraceParser` must take the block from the *pending* messages rather
than the tick's own update, and **the comparison must not be gated on
`HasPositions`**: a `CAPS_UPDATE` arrives off the positional cadence, and gating
on it skipped every observation while reporting a clean pass.

✅ **And a recording that DOES gate them**:
`reference-koth_valley-uncontested.trace` — **six red scouts, no blue**, so no
combat, so **no knockback**, so a **3,111-tick** trajectory window that swallows
the capture whole. ⚠️ **What ends a closed-loop window is weapon knockback**, so
removing the enemy team is how you buy one long enough to reach a slow objective.
The unlock timer cannot be forced instead — the port simulates its own, so
forcing the reference's early manufactures a mismatch rather than exposing one.

⚠️ **A third omission in `PortedSimulation.LoadMap`: it never called
`Rounds.Start`**, which is what **locks** a KotH point before its unlock. Without
it a replay's hill was capturable from tick one. `GameServer.OpenMap` calls it
right after `Place`. Fixed.

✅ **The cause: a replay starts the round too early.**
`KothHUD.Create` sets `cpUnlock = 900` and its Step fires the unlock on the tick
it reads 0, decrementing afterwards — **901 ticks after the HUD is created**,
which is when the server restarts the room.

⚠️ **`CHANGE_MAP` is NOT the round start.** Both KotH recordings carry exactly
one, at **t0**, and unlock at t1236 and t1160 — two different offsets, neither
901. The measured model, exact on both:

> **unlock = (spawn wave − 150) + 901**

where 150 is the ordinary respawn time. So **the port was right and the replay
was early**. ✅ **Fixed** — `Replayer` anchors `cpUnlock` from the wire, and the
port now reproduces that recording's hill **exactly, zero mismatches**. **The
capture arithmetic is gated against the engine**: halving a scout's
`capStrength` shows up as `capping exp 30 got 20`, `exp 75 got 50`,
`exp 225 got 150`.

Four traps:

- ⚠️ **The delay is not a constant to subtract** — 335 ticks in one recording,
  254 in the other.
- ⚠️ **The first HUD readings are stale**: the server serialises the *old*
  round's HUD after announcing the change. `uncontested` reads 0 at t0 and t62
  and first shows the new countdown at t1086, reading 149.
- ⚠️ **So only a POSITIVE `cpUnlock` may be taken.** A zero means "already
  unlocked", true of both rounds. A positive value is unambiguous — the unlock
  fires on the tick the counter reads 0 — so it means "still locked" too, which
  is why the replay re-locks on it.
- ⚠️ **The unlock is a latch, and re-locking must discard the meter**
  (`capping = 0; exit;`), or the anchor tick itself mismatches.

`ControlPointTests`/`DualKothTests` are no longer the whole gate for KotH.

### Team Deathmatch has fixtures now, and one of them gates nothing

✅ **`reference-{tdm_mantic,gg_debug}-closedtdm.trace`** — the
first non-CTF recordings in the project. **Both frame cleanly**, both report
`TeamDeathmatch`, so there is no TDM framing bug. `gg_debug` is the **busiest
fixture here** (52 deaths, 262 fires) and none of it is assertable, because its
window is two ticks — see the landing finding below.

⚠️ **`tdm_mantic` is the only shipped map placing pit falls (six) or move boxes
(fifteen), and its recording gates neither.** Proved by mutation: commenting out
`MoveBoxes.CollisionStep` leaves its window at 3,043 ticks, so no bot rode one;
and with no deaths in it, no pit fall ever fired. **A recording existing on the
right map is not the same as it reaching the behaviour** — ask which of the
map's features the bots actually touched.

### The first fixture on a second map lands 0.4 px low

⚠️ **A fixture on a map other than `ctf_truefort` found a divergence in five
ticks.** `reference-ctf_2dfort-closedrespawn.trace`: spawn points at `y = 894`, floor
stopping a scout at `y = 900`, so every character drops six pixels on spawn. The
reference lands on exactly **900**, the port on **900.4** — all six slots, same
tick, and it stays there.

- **The map layer is exonerated by measurement, not assumption**: the engine and
  the port both answer `place_free` true for y ≤ 900 and false for y ≥ 901 at
  that column, `maps.json` gates the mask cell-for-cell, and the map places no
  gates, no player walls and no dropdown platforms.
- ⚠️ **A second map shows it and a third does not.** `gg_debug` diverges the
  same way and far harder — two ticks, all six slots, reference at rest on
  `y = 168` while the port falls from the marker at 162 — and `tdm_mantic`
  replays **exactly for 3,043 ticks**. So it is not "any non-truefort map", not
  the mask, not dropdown platforms (`tdm_mantic` has four and is exact,
  `ctf_2dfort` has none and fails) and not player walls.
- ✅ **ROOT CAUSE: a replay gives a spawning character one Begin
  Step too many.** The reference creates a character in the **alarm stage**
  (`player.alarm[5]` → `PlayerSpawn`, between Begin Step and Step), so on its
  spawn tick it gets a Step and an End Step but **no Begin Step** — and Begin
  Step is where gravity is applied. Its first End Step therefore still sees
  `vspeed == 0`, the guard on the stair descent, and it **snaps** six pixels onto
  the floor rather than falling over four or five ticks.
  ⚠️ **A replay places the character BEFORE running the tick**, so it takes that
  tick's gravity and misses the snap. Verified both ways by
  `SpawnTickTimingTests`: alarm-stage spawn lands at y=168 vspeed 0 on tick one;
  pre-placed-then-ticked is still airborne.
  ⚠️ **Which is why `ctf_truefort` never caught it** — its markers are
  **eighteen** pixels up, outside the six-pixel snap, so both sides genuinely
  fall and agree.
  ✅ **FIXED in the harness — `ITraceSimulation.QueueSpawn`.** It
  arms `player.alarm[5]` so the tick itself builds the character, and forces the
  spawn-point draw by installing a seed: GM8's generator is invertible, so the
  seeds giving a wanted first result form one contiguous run and a whole spawn
  wave is found in a few thousand steps (`PortedSimulation.TryForceSpawnPointDraws`).
  Overwriting the stream is free **here and nowhere else** — the spawn-point
  choice is broadcast, and a replay already forces every weapon seed.
  ⚠️ **The spawn is queued a tick EARLY**, off the *next* tick's messages, for
  the same reason a fire seed and a death are: the state is serialised in Begin
  Step and the alarm stage runs after it, so a character created on a tick is
  announced on the next one. Queueing when the message arrives is a tick late and
  breaks **every** spawn-anchored fixture — measured both ways.
  **`ctf_2dfort` went from 5 exact ticks to 362; `gg_debug` from 2 to 23**, and
  no other fixture's window moved.
- **When a bound collapses on a new map, ask what that map makes a character do
  that no other one did.**

✅ **What was left on `gg_debug` was a different thing — the DROPDOWN
PLATFORM.** The map places **two**, at `438,216` and
`498,216`, and the residual was slots 1 and 3 at rest on `y = 192` in the
reference while the port fell past. A soldier's origin sits 22.6 above its foot,
so `192 + 22.6 = 214.6` is a character standing exactly on a platform whose top
is 216. ⚠️ **The old note ruled dropdown platforms out because "`tdm_mantic` has
four and is exact"** — a map placing a feature is not its bots touching one, the
same mistake the move-box recording made. All three reference branches landed
and the window went **23 → 100 ticks**, with the trajectories inside it exact
(`|dx| max 0, |dy| max 0`). What stops it now is damage, not position — see
"`gg_debug` stops on a rocket blast four pixels short" below.

⚠️ **A recording can never gate respawn *timing* closed-loop.** `Replayer`
**injects** every `PLAYER_SPAWN` and `PortedSimulation` keeps `RespawnTime = 0`,
deliberately, so a replay does not race the recording with spawn-point picks of
its own. `RespawnTimingTests` measures the *reference's* 150-tick gaps; the
port's counter is gated by unit tests alone.

### A sentry's fate was invisible to the harness, and now is not

✅ **`Fields.Sentries`**. `Replayer` *injected* `BUILD_SENTRY` like
a spawn and compared **no sentry state whatever**, so a port that destroyed the
wrong sentry — or none — replayed every fixture without a murmur. The
`Fields.Deaths` argument, only stronger: **a sentry writes no bytes at all on an
`INPUTSTATE`**, so six ticks in seven say nothing about one and the seventh
shows only an unset subobject bit that names nobody. `SentryRecord` mirrors
`DeathRecord`; `TickEvents.SentryDestroyed` already existed and was never
collected.

⚠️ **It found a divergence on its first run:**
`reference-ctf_truefort-closedsentrykill.trace` (three engineers vs three
soldiers) holds three `DESTROY_SENTRY`s: one **weapon kill** (cause 9, killer 4)
which the port reproduces **exactly, on the tick**, and two **self-destructs**
(cause 0, killer 255) which it does not. The self-destruct fires one tick after
the sentry is built; only the first is evidence, being inside the trajectory
window. **An open finding for the lane that owns `src/GG2.Simulation`** —
`docs/PARITY.md`, "`Fields.Sentries`, and the divergence it found on its first
run", has the two candidate causes and what is already ruled out.

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

Both measured against the running game, both in `GG2.Protocol.Gml`, and both
change trajectories rather than last digits:

- **GM8 parses some decimal literals one ULP above the IEEE nearest** — `0.6`,
  `0.85`, `1.15`, `0.65`, `0.35`, which are gravity, `baseControl`,
  `baseFriction` and two rocket-jump control factors. Never write a physics
  constant as a plain C# decimal; take it from `Gml`.
- **GML's comparisons are fuzzy to `1e-13`, and so is `sign()`.** Every
  real-to-real comparison in `GG2.Simulation` goes through `Gml.Equal`,
  `Gml.Greater`, `Gml.Sign` and friends. A raw `!= 0` on a value that ordinary
  cancellation left at `-2.7e-14` moves a character six pixels.

### Taunting and the humiliation round

Both are **observable simulation**: the taunt key is a bit in `keyState`, so
the stock client sets `taunting` from the same wire the server does, and a
server that ignored it would keep walking a character the client has already
stopped.

**What a taunt does** (`Character.events/Begin Step.xml:95-152`, `End Step.xml:90-94`):

| effect | where |
|---|---|
| no acceleration at all | `:259`, the block wrapped in `if(!taunting && !omnomnomnom)` |
| no attack | `:97`, inside the same guarded block |
| no right click — **except a demoman**, who may detonate | `:137`, `(!taunting or player.class == CLASS_DEMOMAN)` |
| no cloak | `:147` |
| no sandvich | `serverEatSandvich.gml:18` |
| ends after the class's taunt sprite frames | `End Step.xml:90-94` |

⚠️ **The jump on the tick the taunt starts still fires.** GML evaluates
`if(!taunting and !omnomnomnom)` **once**, sets the flag inside the block, and
tests the jump further down the *same* block — so taunt-and-jump together jumps.
Everything outside that block re-reads the flag and refuses on the same tick.
That asymmetry is the reference's and `TauntTests` pins both halves.

⚠️ **A taunt's duration is the class's sprite frame count and nothing else.**
`tauntindex += tauntspeed*0.1` with `tauntspeed = 3`, ending at `>=
sprite_get_number(tauntsprite)`. Counted from the reference tree, red and blue
identical for every class:

| class | frames | ticks | | class | frames | ticks |
|---|---|---|---|---|---|---|
| Scout | 9 | 30 | | Engineer | 13 | 44 |
| Soldier | 16 | 54 | | Heavy | 12 | 40 |
| Sniper | 13 | 44 | | Spy | 11 | 37 |
| Demoman | 11 | 37 | | Pyro | 10 | 34 |
| Medic | 12 | 40 | | Quote (`Querly*`) | 17 | 57 |

⚠️ **Write the rate as `3 * 0.1`, not `0.3`.** GM8 holds them as different
doubles and this is a repeated addition compared against a small integer, so
which tick the taunt ends on can differ. Same class of trap as the physics
constants.

**What humiliation does.** `doEventArenaEndRound.gml:26` is `with Player if team
!= win humiliated = 1`, cleared by `doEventArenaRestart.gml:11` and
`doEventArenaWaitForPlayers.gml:12`. It is **Arena and nothing else**, and true
only for the ten seconds the scoreboard is up. It costs **0.2 of control**,
suppresses the attack, the taunt, the right click, the cloak and the sandvich,
and withholds the round-end ammo refill everybody else gets.

⚠️ **The control penalties do not stack.** `Begin Step.xml:176-181` is an
if/else-if chain, so a humiliated flag carrier is `baseControl - 0.2` and not
`- 0.3`.

⚠️ **It is a PLAYER flag, and it takes spectators with it.** `with Player`
includes everybody whose team is not the winner's, and `Team.Spectator` is not
the winner either. It also survives the death that usually earns it.

#### ⚠️ The sentry is NOT destroyed, and the port destroyed it for two phases

`doEventArenaEndRound.gml:27` is `with Sentry if team != win event_user(1)`, and
that user event destroys **`currentWeapon`** — the turret — and sets the
*sentry's* own `humiliated`. `Arena.EndRound` removed the whole sentry until
2026-08-28.

And the flag is load-bearing rather than cosmetic: `Sentry.events/Begin
Step.xml:51` re-runs the user event every tick, because the build branch
immediately above it re-creates the turret every tick — which also means **a
humiliated sentry is healed to `maxHp` on every tick of the scoreboard**
(`if (built == 1 and currentWeapon == -1) { ... hp = maxHp; ... }`). Both are the
reference's.

#### ⚠️ The Arena round loop: a replay artefact, and a LIVE BUG hiding behind it

⚠️ **The Arena round loop has two independent causes producing one symptom** —
a live bug (fixed, below) and a separate replay artefact (still open). Checking
only the replay and reasoning that live play must be fine, because the state
machine is a faithful transcription, misses the live one: **the port's Arena
loop is nowhere near the recording's.**

| | rounds |
|---|---|
| `reference-arena_lumberyard-closedarena` | ends at t1342, t2533, t3601 — 561 and 591 ticks long, won by both teams |
| the port, replaying it | ends **one tick after each start**, on a 601-tick cycle, **always Red** |

**Cause 1, live, FIXED.** `Player.events/Alarm 5.xml` is
`if (spawningAllowed()) PlayerSpawn();` — **the guard is on the alarm.** This
port had moved it inside `PlayerSpawn`, which reads like the same thing and is
not: Arena's `respawnEveryone()` calls `PlayerSpawn()` *directly*, precisely so
that it can spawn a round's worth of characters at a moment `spawningAllowed()`
refuses — `doEventArenaStartRound` sets the HUD to `ARENA_STATE_ROUND_PROPER`
**before** it calls the sweep. With the guard inside, every arena round began
with nobody alive.

Measured, 8 bots on `arena_lumberyard`, before and after: rounds of **1 tick**
became rounds of **447 to 753 ticks**, against the recording's 561 and 591.
`ArenaRoundLoopTests` gates all three claims and all three go red on the old
placement.

⚠️ **`spawningAllowed()` is called from exactly ONE place in the reference** —
`botPopulationUpdate.gml:30` — plus the alarm. It is not a general spawn rule,
and treating it as one cost a gamemode.

**Cause 2, replay, still open.** A replay may only spawn characters the
recording announced, so `PortedSimulation` passes no spawn points on most ticks
and the sweep's spawn is refused for a second, unrelated reason. The replayed
loop is still 300 + 300 + 1, and the two `ClientPredictionGateTests` arena rows
did not move when the live bug was fixed — which is the evidence that these
really are two causes.

**The price is two rows of `ClientPredictionGateTests`**, which went from 2/2 and
9/2 to 19/21 and 28/9 — every one of those units is the port humiliating half a
roster in a window the reference never had. ⚠️ Flipping the humiliated team
gives 19 and 27, indistinguishable, which is the evidence that the **window** is
wrong rather than the side.

⚠️ **Nothing caught the live half, and the reason is worth more than the bug.**
The arena fixtures gate the *recording's* framing and the port's trajectories,
both of which were fine. `eval` ran arena matches to a decision and reported them
as wins, because five instant rounds reach a cap limit exactly like five real
ones. **A match that ends is not a match that happened**, and no test asked how
long a round lasted.

⚠️ **Injecting the recording's four `ARENA_*` messages is the obvious fix and is
not obviously right.** Everything else a replay cannot derive is injected
because the reference already made the choice and put it on the wire, and these
qualify — but the port would go on making its *own* decision beside the injected
one, and there is no way to tell it not to: `MatchSimulation.Tick` takes no mode
and `docs/SIMULATION.md`'s rule is that the simulation never special-cases its
caller. Settle that question before writing anything. `ArenaReplayLoopTests`
pins the artefact in both directions, and
`PortedSimulation.ArenaTransitions` is the diagnostic it reads.

### t523's residue: the stop is a WALL, and the "+0.4865" was never a divergence

The residue itself is still open; several earlier candidate explanations have
been ruled out (below).

**The reproduction, with nothing in it.** Slot 4 at t383 is at (588, 168) on
`gg_debug` holding RIGHT, and that is the whole of the interesting part. Placed
in a bare `ScenarioWorld` — no trace, no replayer, nobody else on the map — the
port does this:

```
t12  x = 626.5277729831873   hspeed = 4.3220528294914455
t13  x = 629.2933979831873   hspeed = 0          <- the wall
t14  x = 629.4650989026032   hspeed = 0
t15  x = 629.4758302100668   hspeed = 0
t16  x = 629.4865615175303   hspeed = 0
t17  x = 629.4972928249938   <- rest, and it never moves again
```

⚠️ **The stop is a WALL, not friction.** The recording holds key 0x20 — RIGHT,
held — on every tick from t388 through t406, and `hspeed` goes to zero at t404
anyway. The acceleration formula is not the cause — `PhysicsSampleTests`
already measures it exact over 64 engine scenarios.

⚠️ **The famous "+0.4865 residual, which grows while walking and then freezes"
is not a measurement of anything.** It is `x*5 - 3147` for the port's own
resting position: 629.4972928… × 5 = 3147.4865. It grows while the character
walks because the character is walking, and freezes when it stops because it has
stopped. **A recording cannot say where the reference rests to better than a
fifth of a pixel**, so no quantity computed from the port's own x and a wire
integer is a divergence. Reading it as one is what made the residue look as
though it were born during the walk.

⚠️ **`good_move_contact_solid`'s back-off ladder is NOT an uncovered arm.**
Measured by counting it inside the physics oracle: the 64 engine scenarios enter
the back-off branch **1,354 times** and come to rest on a rung **198 times**, and
the port is exact on every one. The ladder was the natural next suspect and it is
dead.

#### What the reproduction did turn up: the rest is 0.0027 px from a tie

The mask's free/blocked boundary at that spot is exactly **x = 629.5**, and
`RoundPosition` is half-to-even, so 629.5 becomes 630 and is solid. The
character rests at 629.4972928…, **0.0027 px short of a rounding tie** — reached
by a four-tick *creep* with `hspeed` already zero, because the walk keys are
still held, so it re-accelerates a fraction each tick, moves until `place_free`
fails and backs off a rung. Each tick gains one rung less than the last: 0.1717,
then 0.0107 three times, then the smallest rung no longer fits.

**Both simulations report `round(x*5) = 3147` from anywhere in [629.3, 629.5).**
That is the whole reason no fixture can see this.

#### ⬅️ The next step is one command, and it is decisive

`gg2-samplers/probes/probe-wall-stop.js` drives that scenario in the engine and prints
the engine's resting x beside the port's. It needs a dedicated server on
`gg_debug` with the bridge and one Character to borrow, and it answers the item
either way:

- **engine = 629.4972928249938** — the walk and the wall stop agree exactly, the
  "born during the walk" reading is finished, and the search moves to what slot 4
  does from t418: it starts rocket-jumping.
- **anything else** — the residue is measured for the first time, and its size
  names the mechanism. A whole rung (~0.0107) is a `place_free` disagreement near
  the tie; ~1e-13 is the fuzzy-compare epsilon; in between is the creep loop
  running a different number of times.

`WallStopTests` pins the port's side of that comparison to the last bit,
deliberately: the probe's entire output is `engine − 629.4972928249938`, so a
port change that moves the number invalidates a comparison somebody may already
have run.

### `gg_debug` stops on a rocket blast four pixels short

✅ **Found and FIXED — three bugs, each behind the last.**
`reference-gg_debug-closedtdm.trace` replayed exactly for 100 ticks after its
spawn anchor at t381 and then diverged — not on a position, but because **slot 4
survived a death the reference recorded**. It now runs to **135 on every
field**, and what stops it is a fifth of a pixel on slot 4's x at t523. `GgDebugSplashOpenFindingTests` holds
the whole of it, including the arithmetic below; read that file before
re-deriving any of this.

**What is not in dispute.** All six rockets in the window are fired by the same
players on the same ticks in both, all six explode on the same ticks, and the
reference's `PLAYER_DEATH` at t477 names killer 5 and assistant 3 — exactly the
pair the port's own damage bookkeeping picked. The port simply deals a little
less over t475..t480: slot 2 takes 97.4 against 99–100, slot 6 takes 53.9
against 56–57, slot 4 takes 68.4 against at least 70.

**Slot 6 is the measurement, because it never moves.** Its position is exact on
every observation and it still ends three hp higher here, so nothing about the
physics is in question. Its three hits are the t475 blast, a point-blank t476
blast already at the falloff floor, and the t480 wall blast — which pins the
shortfall to the t475 blast and, through `ceil(hp)` on the wire, pins that
blast's `distance_to_object` to **30 or 31** where the port computes **36**. (29
would leave slot 6 on 13 and 32 would leave it on 15.) Slot 4's death needs the
same number independently: 25 + 30 at t476 leaves it needing 15 from t475, which
is a distance of 32.5 or less.

⚠️ **It is not a tuning error in the back-out, and that is provable without the
engine.** The escape from a direct hit runs along `direction + 180`, almost due
left here, so freeing the rocket means clearing slot 2's box on the left — and
slot 4's box starts a fixed 36 pixels beyond slot 2's left edge. Making the
rocket's own box longer or shorter moves the stopping place and the measured gap
by the same amount. `NoBackOutThatFreesTheRocketFromSlotTwoCanKillSlotFour`
enumerates every whole-pixel step `really_move_contact_solid` could stop on and
shows the distance is **never** below 36. So a distance of 30 means the rocket
stopped **four pixels back instead of ten** — it exploded *while still inside
slot 2's bounding box*.

✅ **CAUSE FOUND, and it was the third candidate: GM8's overlap primitives use
the PRECISE MASK for a rotated projectile, where this port uses the bounding
box.** Measured with `collision_point(..., precise)` — nine sprites by eighteen
angles, `tests/GG2.Tests/Fixtures/rotated-mask-samples.json`, regenerate with
`node gg2-samplers/sample-rotated-mask.js`.

⚠️ **The split runs through the middle of one GML line, and it is sharper than
the facing asymmetry already recorded on `Aabb.ForCharacter`:**

| primitive | uses | port |
|---|---|---|
| `distance_to_object` | the **bounding boxes**, rotated or not, with the character's `image_xscale` inflation | ✅ right — re-checked live at the divergence angle |
| `place_free`, `place_meeting` | the **precise masks** | ❌ uses the box |

**Unrotated the two coincide** — every one of the nine sprites' masks fills its
own bbox exactly, which is what `RotatedMaskSampleTests.EveryUnrotatedMaskExactlyFillsItsBoundingBox`
gates — so six phases never noticed. Rotated, the box is not a small
over-estimate: a `Shot` at 45° has **25 opaque pixels inside a 169-pixel box**,
and the Scattergun spreads every pellet by `random(15) - 7.5`, so diagonal
pellets are the normal case.

At the divergence angle the rocket's 8x3 box carries 16 opaque pixels and its
**top row is one pixel, at offset zero** — the row that overlapped slot 2. Six
pixels of back-out, step 10 → step 4, distance 36 → 30. Both constraints then
land exactly: slot 4 takes 71.15 and dies, slot 6 finishes on 13.30 which
`ceil`s to the recorded 14.

**Two candidates were killed by running oracles rather than by argument, and
both are worth keeping:**

- ❌ **What `distance_to_object` returns.** `PrimitiveSampleTests.DistanceToObjectMatchesTheEngineExactly`
  holds `Aabb.Distance` against the engine over 361 rocket offsets spanning
  ±64 px, exactly.
- ❌ **The two-pixel `image_xscale = -1` inflation** (`Aabb.ForCharacter`).
  Widening it to eight *does* produce the missing distance of exactly 30 — and
  it breaks four live-engine oracles. ⚠️ **Right arithmetic, wrong cause: a
  candidate that reproduces the number exactly can still be ruled out by an
  oracle it never occurred to you to run.**

⚠️ **Why no existing oracle could see it.** All 361 rockets in
`primitive-samples.json` are **unrotated** — `sample-primitives.js` never sets
`image_angle` — so `place_meeting` was only ever asked the question where the
two answers agree. `rotated-box-samples.json` gates what `bbox_*` **reports**
for a rotated sprite (1,008 boxes) and not what the overlap primitives **use**.
**Ask what a fixture cannot reach**, the same rule as "when adding a fixture,
ask what state it cannot reach".

✅ **FIXED** — `GG2.Simulation.ProjectileMask`, wired into
`Projectiles.CollideOne` (characters, sentries, generators) and
`BackOutOfCharacter`. The model inverse-rotates each candidate world pixel into
sprite space, floors, and tests the sprite's own bbox rectangle, with the
bounding box kept as the broad phase in front of it.

✅ **And it is exact except on a rounding tie — 99.9736%, with every one of the
82 disagreements a tie rather than a fault.** Measured against the fixture: the
box alone is right on **91.5%** of 310,194 sampled pixels and the mask on
**99.9736%**. `RotatedMaskSampleTests` asserts the *structural* count is
**zero**, which is a much stronger claim than the percentage.

⚠️ **Two corrections to the first write-up of this, both worth keeping:**

- ❌ It said **99.861%, 431 wrong, 171 of them structural**, and concluded the
  model was an approximation that needed replacing. **The scoring was wrong, not
  the model.** It asked `Covers` about every pixel of the sampled grid, where the
  shipped path only ever asks inside the reported box — so it counted
  disagreements about pixels no caller can reach. ⚠️ **Score what the code does,
  not what a helper would say if asked a question it is never asked.**
- ❌ It called the thing a **precise mask**, and there is no pixel art anywhere in
  this system. **All nine projectile sprites declare `<shape>RECTANGLE</shape>`**
  in their `Sprites/**/*.xml` (MANUAL for the Rocket, Flame and Flare, AUTO for
  Needle, Blade and Stab, FULL for Mine and Bubble). So the mask is exactly the
  bbox rectangle, and the test is an **analytic rotated-rectangle** one:
  `floor(u) ∈ [L,R]` *is* `L ≤ u < R+1`, the region a pixel rectangle covers.
  ⚠️ **Read the sprite XML before inferring a mask from measurements** — one
  `grep` for `<shape>` would have saved a long search over rasterisation models.

**What the 82 are.** All ties, of two kinds, and both are already-accepted
classes rather than new ones:

- a **mask floor tie** — the transformed coordinate lands within `1e-9` of an
  integer, where docs/SIMULATION.md's x87 gap decides the floor the other way;
- a **box tie** — the six samples where `Aabb.ForRotatedSprite` is one pixel from
  the engine, which `RotatedBoxSampleTests` already exempts by the same
  predicate. A mask disagreement at an edge only one of the two boxes covers is
  that tie showing through, not a second fault.

⚠️ **`ProjectileMask.Covers` therefore clamps to the reported box, and that is
part of the answer rather than a broad-phase optimisation.** The box and the
mask are rounded by *different* rules — the box takes each rotated corner half
to even, the mask floors a rotated point — and where they disagree the box wins,
because it is the one measured directly. Leaving the clamp out costs a factor of
five.

**What settled shipping it was the windows, not the percentage:**

| fixture | before | after |
|---|---|---|
| `reference-gg_debug-closedtdm.trace` | traj 100, all 95 | **traj 135, all 135** |
| `reference-ctf_truefort-closedsupport.trace` | traj 489 | **traj 531** |
| `reference-ctf_truefort-closedsentrykill.trace` | traj 581 | **traj 595** |

⚠️ **Three better, twenty-four unchanged, none worse.** Re-run that comparison
before touching this again: a model that is more accurate on pixels can still be
wrong in a *different* place and regress a green fixture, so "more accurate" is
not by itself an argument.

⚠️ **Every sample was taken at an INTEGER position**, so which coordinate the
pixel grid hangs off is unmeasured. `ProjectileMask` anchors it on
`CollisionWorld.RoundPosition` because that is what `Aabb.ForRotatedSprite`
does. A probe at fractional positions would settle it and is cheap.

### A rotated-box rounding tie is not only where `sin` is a half

⚠️ **A correction to `Aabb.ForRotatedSprite`'s own note**, which said the ties
are "210 and 330 degrees, where `sin` is analytically -0.5". That was
`sample-rotated-box.js`'s angle grid, not the rule.
`rotated-mask-samples.json` puts three of its six ties at **300**, where it is
`cos` that is analytically 0.5 — the same mechanism on the other axis.

`RotatedBoxSampleTests.IsRoundingTie` always covered both, because it asks
whether a **rotated corner** sits within `1e-9` of a half rather than which
angle it is. That is exactly why the exemption is written as a predicate and not
as a list of angles, and it is the reason this correction cost nothing: only the
prose was narrow. **Do not fudge the rounding** — a fudge would be wrong at
every angle instead of at a few, and a tie is unreachable in play.

### Slot order is not creation order, and a respawn is where they part

⚠️ **The third bug behind the `gg_debug` blast**, wrong since the collision
stage was written. `Projectiles.CollideOne`
walked characters in **slot** order, with a comment saying that is "the
reference's instance order ... an id is issued at creation". It is — **until
somebody respawns.** A slot whose player died and came back holds a character
*younger* than the ones in higher slots that never did.

**It matters wherever the FIRST hit ends the search.** A rocket explodes on the
first character its collision event fires against and is destroyed there, so
which one it visits first is not a tie-break — it is the whole outcome.

**Measured, and the arithmetic closes exactly.** At t509 of
`reference-gg_debug-closedtdm.trace`, slot 6 had respawned at t508 and slot 2 at
t509. The reference's rocket struck **slot 6**; the port, walking slots, struck
slot 2 — which was invulnerable and took nothing. Slot 6 therefore finished the
window **25 hp high**, exactly a rocket's contact damage: 25 + 30 splash = 55,
and 140 − 55 = the 85 the recording holds. Fixing it took the window from 135 to
**142**, and moved no other fixture at all.

`CharacterState.CreationOrder` is a `uint` on the character rather than a
parallel `SlotBuffer`, **on purpose**: it then moves with the character through
`RemoveSlot`'s compaction and cannot be forgotten there — which CLAUDE.md warns
is exactly how a per-slot field goes wrong. `WorldState.NextCharacterOrder`
issues it, and lives in the state for the reason the RNG does: a forked rollout
has to fork it.

⚠️ **`Explosions.SplashCharacters` still walks slot order and that is still
fine** — its own comment already says why, and it is now half-true rather than
wrong: nothing in the blast loop reads another character, so the order is not
observable *there*. Move it the moment that stops being true.

✅ **The rest of the first-hit-wins sites were audited and all four now follow
the rule.** None is contradicted by a fixture — re-measuring all 27
after the change moved nothing — so they follow the rule the character case
proved rather than a measurement of their own. **That distinction is recorded
deliberately**, so a future session re-arguing one of them knows which evidence
it rests on.

| site | first hit ends it? | fixed |
|---|---|---|
| `CollideOne` characters | yes — the projectile explodes there | ✅ creation order, **measured** |
| `CollideOne` sentries | yes | ✅ build order (`SentryState.CreationOrder`), by rule |
| `BubbleCollide` characters | yes — the bubble pops | ✅ creation order, by rule |
| `CollideOne` generators | two, and their order is the map's | ⬅️ left alone: `HitGenerator(Red) \|\| HitGenerator(Blue)` is an assumption about map entity order, and a map places one of each |
| `Sentries` mine sweep | **no** — the `with` visits every mine | order irrelevant ✅ |
| `Explosions.SplashCharacters` | no — every character in the blast | order irrelevant ✅ |

⚠️ **Two of those sites were also using the BOX where the mask now applies**, and
that is a membership error rather than an ordering one: `BubbleCollide`'s
character test and the mine sweep in `Sentries`. A rotated mine's box is a strict
over-estimate, so the sweep was unsticking mines that were not touching the
sentry at all.

⚠️ **`SentryState.CreationOrder` and `CharacterState.CreationOrder` draw from one
counter** — `WorldState.NextInstanceOrder` — because GM8 issues instance ids from
a single sequence whatever object is being created. Nothing in the port compares
a character against a sentry today, so the sharing buys nothing yet; it costs
nothing either, and it keeps the concept from drifting into "two independent
sequences", which would be wrong the first time something did compare them.

⚠️ **Both orderings are separate loops rather than one shared sort.** The key
lives in a different `SlotBuffer` for each, and an abstraction that hides which
buffer is being read is exactly the one that can silently sort by an unrelated
number. Eight lines of duplication is the cheaper mistake.

### The GameServer is the OLDEST instance, so its flush is the FIRST End Step

⛔ **Measured against the running game**, and it is the same instance-id rule
as the section above pointed at a different object. `GameServer`
is created by `start_server.gml:7` and is `<persistent>true</persistent>`, so it
predates every room object and every character. Under GM8's instance-id event
order that means:

- `GameServerBeginStep` runs **before every other Begin Step**, and
- `GameServerEndStep` — the flush — runs **before every other End Step**.

**The consequence, and it is the one that costs a session:** anything appended to
`global.sendBuffer` in a Character's or a Sentry's own End Step **misses that
frame's flush and ships on the next tick**. Three messages are in that position:

| append | site |
|---|---|
| `PLAYER_DEATH` | `Character.events/End Step.xml:34-47` |
| sentry `DESTRUCTION` | `Sentry.events/End Step.xml`, the `hp<=0` block |
| `BALANCE` | `with(GameServer) ServerBalanceTeams()`, `Character.events/End Step.xml:42-44` |

⚠️ **The flush does not happen after everything that frame has been appended.**
The state update is not the last thing written for its tick either — every
Begin Step event appends after it.

#### The probe, because no trace can settle this

⛔ **A recording is structurally blind to it.** The send buffer is FIFO, so the
flush point changes which TCP segment a message rides in and **never** the byte
order: both orderings produce an identical stream. There is no discriminator to
look for in a trace, and looking for one is wasted time.

What works is reading the buffer from inside the game, once a frame:

```
gg2_watch add probe string(buffer_size(global.sendBuffer)) + "b " + string(instance_number(Character)) + "c"
gg2_speed 0.05          # 2 fps, or the trace outruns a gg2_log round trip
gg2_eval  with(Character) hp = -1000;
```

⚠️ **The agent bridge's hook is its own Step event** (`AgentBridge.id` 100015
against `GameServer.id` 100024), so the sample lands after every Begin Step and
before every End Step — which is exactly the window a leftover shows up in.

**The reading, and the arithmetic closes exactly.** Two bots killed on one frame:
the steady state with no characters was **5 bytes**, and the frame after the
deaths read **17** — 12 left over, being 5 + 5 for the two `PLAYER_DEATH`s
(`sendEventPlayerDeath.gml`, five `write_ubyte`s) and 2 for the `BALANCE` notice
(`ServerBalanceTeams.gml:29-30`). Those bytes can only have been appended in the
previous frame's End Steps and survived its flush.

⚠️ **Four things that cost time getting there, in order:**

- **`gg2_step` freezes at the bridge's Step, not between frames**, and a frozen
  instance is unreachable by object name — `GameServer.frame` and
  `instance_number(Character)` both fail, while `global.` reads still work. Slow
  the game with `gg2_speed` instead of stepping it.
- **A watch expression that dereferences an object which may not exist throws
  every frame**, puts up a dialog every frame, and drags the game to ~4 fps.
  `Character.hp` did exactly that the moment the character died. Guard it or
  watch `instance_number` only.
- **A character standing in a spawn room is restored to `maxHp` every tick**, so
  `hp = -1000` does nothing and looks like the death path being unreachable. Move
  it to mid-map first — in the *same* eval, per "the game keeps stepping between
  `gg2_eval` calls".
- **Use `botAdd(team, class, name)` rather than a driven client.** A bot is a
  server-side Player with no socket, so neither the 60-second AFK team-unpick nor
  the spectator's five-minute self-disconnect can take the character away
  mid-measurement. Both did, first.

#### What is fixed, and what is still open

✅ **The delivery half is fixed** — `GameServer.BroadcastEndStepEvents`, appended
after `FlushBroadcast()`, gated by `FlushShapeTests` in both directions (no
`WEAPON_FIRE` may lead a packet; a `PLAYER_DEATH` must). Mutation-checked.

⚠️ **A death now shares a packet with the respawn it caused**, because the
one-tick respawn's `PLAYER_SPAWN` is an alarm-stage append on tick D+1 and the
death is a leftover from D. A test that drains until it finds the death consumes
the spawn in the same drain — `SimulatedServerTests` was rewritten for it.

⛔ **The join-snapshot half is NOT fixed.** `acceptJoiningPlayer` and
`serviceJoiningPlayer` are `GameServerEndStep.gml:18-20`, so the reference's join
`FULL_UPDATE` is serialised *before* `Character.events/End Step` — before the
stair-climb `y += 6`, the room-bounds clamp, the `hp > maxHp` clamp, the sandvich
heal, `cloakAlpha` and the taunt index. This port runs the whole tick inside
`MatchSimulation.Tick`, so its join burst is taken after all of them. Closing it
means splitting the simulation's End Step out of that call, which is a change to
the stage boundary and serialised across lanes; it is worth a whole session, not
a corner of one. **What being wrong costs: one joining client, once, sees a
snapshot at most one tick stale in those six fields, corrected by the next
`QUICK_UPDATE` seven ticks later.**

### The map test was the last place a projectile was still a box

✅ **Closed, and it was worth more than the character path that
found the rule.** `MoveRocket` and its siblings asked
`world.PlaceFree(projectile.Box, ...)` — the axis-aligned bound — while the same
projectile had already become a rotated rectangle to characters and sentries.
The map is a precise pixel mask, so GM8 tests where the two actually overlap.

`ProjectileMask.PlaceFree` and `PlaceFreeInMask` now do that at all fifteen
sites (Shot, Blade, Flame, Rocket, Mine, the shared mover and the obstacle
back-out).

**What it bought, and it is the largest single jump any fixture here has had:**

| fixture | before | after |
|---|---|---|
| `reference-koth_valley-closedkoth.trace` | 330 | **568** |
| `reference-gen_destroy-rotation.trace` | 445 | **466** |

**Two better, twenty-five unchanged, none worse.** ⚠️ **A hill map felt it where
every combat fixture did not**, and the reason is worth keeping: `koth_valley` is
full of walls, and a bullet that stops a few pixels early stops *in the wrong
place* — where a bullet that hits a person a few pixels early still hits the
person. **A geometry error shows up against the map long before it shows up
against a target**, so when a collision rule changes, the fixture to watch is the
one with the most terrain, not the one with the most combat.

⚠️ **The box stays as the broad phase and that is load-bearing, not an
optimisation.** The mask never reaches outside the box, so a free box is a free
mask and the pixel scan never runs; it only runs on the tick something is
actually blocked. Without that, every projectile would scan its own area every
tick.

**The narrow phase asks the world about one pixel at a time** rather than
reaching into the walkmask, because a 1x1 box is exactly the question and
`CollisionWorld` already answers it for both the pixel mask and the rectangle
list. Attribution checked by mutation: forcing the narrow phase to block
reproduces **exactly 330** on `koth_valley`, the pre-change number, so the whole
gain is this and nothing else.

✅ **And projectile vs projectile closed with it** — the two Bubble sites, where
**both** masks are rotated rectangles and a pixel has to be inside each. Only
Quote's bubbles ever meet another projectile, and a pellet arriving at one is
rotated to its heading, so the box answered yes for pairs that never touched.

⚠️ **That one moved no fixture window at all**, and neither did the sentry and
bubble *ordering* changes. **Three of this session's changes are rule
consistency with no measurement behind them**, and each says so where it lives:
no recording has a pellet grazing a bubble, or two sentries overlapping one
projectile. They rest on the character and map cases, which were measured.
`RotatedMaskSampleTests.TwoProjectilesCanShareABoxCornerWithoutTheirMasksMeeting`
is the only thing that would notice a regression in the bubble pair — a rocket at
45° and a bubble sharing exactly one box pixel, which is the corner of the
rocket's box its diagonal mask does not reach.

**Every overlap in the port now tests the mask.** The box survives only as the
broad phase in front of each one.

### A harness gate belongs where there is no drift

`SupportClassFixtureTests.ThePortEmitsNoUberMessageTheReferenceDidNot` was the
project's only check that `Fields.Ubers` is compared against the right tick, and
it worked by finding an `UBER_CHARGED` **1,155 ticks past where its recording
still agreed with the port at all**. The rotated-mask fix moved that charge by
one tick, a 30-tick drift tolerance had to go in, and a harness reading a tick
late then looked exactly like the drift.

`ReplayerAlignmentTests` asks the same question of a **ten-tick synthetic
trace**: a stand-in simulation that announces one event, and the recorded message
placed in the following tick's block, the tick's own block, and the one after. No
fixture, no drift, no RNG.

✅ **All four event families are covered**, not just Ubers — deaths, the
objective and sentries were in exactly the same condition — **and control points
alongside them**, in a different shape.

⚠️ **The check that matters is the mutation, and it was run for each family.**
Pointing one `Reconcile*` at the current tick instead of the next:

| family | caught by the new tests | caught by everything else |
|---|---|---|
| Deaths | 2 | 4 |
| **Ubers** | 2 | **0** |
| Intel | 2 | 2 |
| Sentries | 2 | 2 |
| Control points | 3 | 3 |

**Ubers was unguarded entirely** — the number that says the debt was real — and
deaths, intel and sentries rested on two to four fixture assertions that happen
to reach them. That is the hole measured rather than asserted.

⚠️ **Control points were the exception, and the honest reading is that they were
already fine**: three fixture tests catch the mutation, because this is the one
that was found the hard way and gated properly afterwards. What the new tests
add there is not first coverage but **independence from drift** — the same thing
Ubers lost when a simulation fix moved its charge by a tick.

**Control points take a different shape** for a real reason: they are not an
event family. The block rides on a `CAPS_UPDATE` and is compared *before* the
tick runs rather than out of the next tick's messages, so the sensitivity
control cannot be "move the message to another block" — there is one observation
per tick. The offset goes into the **value** instead, which is what a one-tick
misalignment does to it anyway, and the assertion is that
`Expected - Actual` is uniformly ±1 rather than merely non-zero.

**The general shape, which is the transferable part:**

- ⚠️ **An assertion that needs a thousand ticks of agreement to reach its subject
  is not testing its subject** — it is testing everything upstream, and the first
  unrelated fix retires it.
- **A synthetic trace is cheap.** `TraceTick` is a plain record; ten of them and
  a stub `ITraceSimulation` are a few dozen lines, and they answer the harness
  question without any simulation being right about anything.
- ⚠️ **Assert the PAIR of mismatches, not "some mismatch".** A misaligned
  comparison produces two — the recorded message nobody claimed and the port's
  event nobody recorded — where a comparison that has simply stopped running
  produces none and one that has broken produces something else.

### Phase 2's scripted fixtures, and what they turned out to gate

✅ **Recorded**, the deliverable `docs/PLAN.md` has listed as open since
phase 2: recordings of a **scripted** character rather than a bot's own
judgement. `reference-gg_debug-scriptedwalk.trace` and
`reference-gg_debug-scriptedjumps.trace`, one scout driven from the agent bridge
against the reference server. **Both replay closed-loop to the last tick of
their recording on `Fields.All`** — 723 and 625 ticks, zero mismatches. Nobody
fires and nobody dies, so nothing caps the window but the recording's length.

⚠️ **Two of the four manoeuvres are covered, and which two was MEASURED rather
than intended.** Deleting each branch and watching both windows:

| branch disabled | walk | jumps |
|---|---|---|
| *baseline* | 723 | 625 |
| double jump | 723 | **162** ✅ |
| dropdown platform | **116** ✅ | 625 |
| wall step-up (`Y -= StepHeight`) | 723 | 625 ❌ |
| sloping ceiling (`Y += StepHeight`) | 723 | 625 ❌ |

**The dropdown platform gains a much sharper gate than it had** — previously only
`gg_debug-closedtdm`, which moves 135 → about 23; this moves 723 → 116.

⚠️ **The step arms are still ungated, and chasing that produced the more useful
result.** A third recording (`scriptedstairs`, 474 ticks) was made specifically
to reach them: a scout walking down `gg_debug`'s six-pixel staircase and back up
it, twice, with the ascent verified from the server mid-recording (192 → 168).
It does not reach them either.

**Counted during the replays**, which is what turns this from a failure into a
finding:

| fixture | `characterHitObstacle` calls | wall branch | **step-up** | sloping ceiling |
|---|---|---|---|---|
| `scriptedwalk` | 340 | 340 | **0** | 0 |
| `scriptedstairs` | 165 | 165 | **0** | 0 |
| `scriptedjumps` | 34 | 0 | 0 | 0 |

**505 real wall collisions, and every one takes the third arm — the one that
stops the character dead.** Neither step is ever free.

⚠️ **So a six-pixel staircase is not a step-up**, and that is the thing to know
before recording a fourth time. Reaching those arms needs geometry where
`PlaceFree(x + sign, y - 6)` is true while `PlaceFree(x + sign, y)` is false: a
wall at most six pixels tall **with headroom above it**. A staircase approached
at a scout's five pixels a tick presents a wall with the next step's mass right
above it, so the step-up probe finds no room and the character just stops. The
scout climbs it by some other path, which is itself unexplained and worth one
probe next time.

⚠️ **And the first route was planned from a walkmask profile that used
`CollisionFilter.ForBullet`**, so it could not see the player walls that shape a
character's world — `gg_debug` has one at `372,156`, which is exactly where the
first recording stopped. **Profile with the filter the thing you are planning
for actually uses.**

### Driving a client: a second `gg2_input press` did not take

⚠️ **Observed twice, mechanism NOT established** — recorded as a symptom
because it cost a whole recording and the workaround is cheap.

In both recordings the **first** input action took effect and every later
`gg2_input press` did not: the character kept the first held direction until the
end. `clear` on its own *did* work — `AgentBridge.heldMask` read 0 afterwards
both times — so it is the second `press` that is in question, not the bridge
being wedged.

⚠️ **Do not conclude it was the watch.** The first recording had no `gg2_watch`
running when its second press failed, so the `gg2_watch`-writes-`heldMask`
explanation is ruled out by that alone.

**The workaround, and it is what CLAUDE.md already recommends for frame
accuracy:** drive everything from **one** `gg2_watch` expression that computes
the mask, and change the expression when the phase changes, rather than
alternating `gg2_input` calls. A watch expression is evaluated every frame and
demonstrably keeps writing.

⚠️ **And verify each phase from the SERVER's state, not from the tool's "ok".**
Every one of these calls returned `ok`. What would have caught it in seconds is
one `gg2_state` between phases: the character sat at the same x for 400 ticks.

### A human found in 100 seconds what three scripted routes could not

✅ **After three scripted recordings reached `characterHitObstacle`'s step
arms **zero** times, a human played `tdm_mantic` for a hundred seconds.**
`reference-tdm_mantic-humanmoves.trace`, 2,999 ticks, **1,161 exact** from its
spawn anchor.

| branch | three scripted takes | one human take |
|---|---|---|
| wall step-up (`Y -= StepHeight`) | **0** | **50** |
| sloping ceiling (`Y += StepHeight`) | **0** | **3** |

⚠️ **Why, and it matters before anyone writes another driver.** A script walks
in straight lines between points worked out in advance, so it only ever meets
geometry somebody predicted — and two of the three routes were planned against a
walkmask profile taken with the **wrong collision filter**. A person wanders,
changes direction mid-stride, and taps a key for five ticks and lets go.
**A person is a better fuzzer than a plan.**

⚠️ **Two mistakes cost a whole take, and both are avoidable:**

- **Do not teleport a character during a recording.** A `gg2_eval` teleport moves
  it server-side with no wire event, so the replay simulates the character
  walking while the reference jumped, and diverges there. It also killed the
  player twice by dropping them where nothing was standable.
- **Check reachability, not just geometry.** The first route sent the player to
  `gg_debug`'s step-up staircase, which is **enclosed** — the human said so in
  seconds where the map analysis had not. The fix was a scan that walks from
  every spawn on every map and reports where a step-up is actually met; it found
  ten maps, the nearest **24 px** from a `tdm_mantic` spawn.

### ⚠️ OPEN: the port puts a player back on a platform they dropped through

**Found by that human take**, and it is the fourth manoeuvre `docs/PLAN.md`
asked for. At t1621–1625 the player holds `KEY_DOWN` on the dropdown platform at
`2622,1014` and releases it. The reference drops them through and they keep
falling — y 990.4 → 1001.2 → 1041.2 → 1086.4. **The port falls with them and
then lifts them back onto the platform**, holding 990.4 for the rest of the
recording. It is why that fixture's window stops at 1,161.

⚠️ **Not a missing feature.** All three dropdown paths check `KEY_DOWN` — the
Begin Step ground test, the Step push-out, and
`CharacterPhysics.StandingOnDropdown`. The suspect is the Step push-out's
"already inside it before this tick's move" guard, which exists precisely to
stop a character being caught on a platform it is falling through and is
evidently not catching this once the key comes back up.

⚠️ **This is not the dropdown coverage the walk fixture already had.** That one
gates the platform **holding a character up** (723 → 116 when disabled), which
is the opposite direction. Pinned by
`Phase2FixtureTests.ThePortPutsAPlayerBackOnAPlatformTheyDroppedThrough`, which
**fails when fixed**.

### The first two-human playtest: what it proved, and three reports from it

✅ **`koth_corinth`, two humans, ~12 minutes.** The first exercise of
`--record` on a real multi-client game. Two segments, **both framed cleanly to
the last byte** (13,811 and 9,522 messages), and rich where every scripted
recording is thin: 22 deaths, 20 class changes, 185 weapon fires, a sentry built
*and* destroyed, a sniper zooming, the hill contested, chat.

⚠️ **The deadline is still NOT discharged.** `docs/BOTAI.md` item 3 wants the
*first public games* — strangers, unrepeatable. Two arranged players is a
rehearsal, and rehearsal is what it was for.

⚠️ **`--record` is not the only route, and it is the slower one.** It needs
strangers to come to this server. `Recorder` is a passive spectator client that
takes a host, so `gg2-parity record --host <public-server> --port <n>` records
strangers on somebody *else's* GM8 server, today. Same format, same deadline,
no playerbase required — `docs/BOTAI.md` item 3.

#### ⚠️ A kill loses the round in progress

`MatchRecorder` writes to `<name>.trace.part` and only prepends a header on
`Close()`, because a trace is a header in front of one stream and the header
needs the length. A clean stop finalises it — the host cancels on **Ctrl+C**,
`Run` returns, `Dispose` → `Close`. A `taskkill /F`, a crash or a power cut does
not, and the bytes are then unreadable.

⚠️ **On a public server the segment in progress is the round in progress**, and
the events most likely to end a server uncleanly are the events most worth
having recorded. `gg2-parity recover <file.trace.part>` now puts the header on
afterwards; proven on the real 4,096-byte orphan this playtest produced, which
came back as 1,017 whole messages. **Stop a recording server with Ctrl+C.**

⚠️ **A recovered `.part` ends mid-message** — the reader stops with a precise
byte offset and the last whole message before it is all there is. That is the
ceiling on recovery, not a bug.

#### The three things the players reported

⬅️ **1. Rubberbanding / hitching. ✅ One measured cause found and fixed.**
⚠️ It is the failure mode CLAUDE.md says has no test: "the client re-simulates
the physics itself between position updates … there is no test that fails when
trajectories drift — the client just snaps."

**The tick pacing was bimodal.** `GameServer.Run` paces with
`cancellation.WaitHandle.WaitOne(wait)`, and Windows rounds a wait up to its
timer quantum — about 15.6 ms. Measured **in the real server process, idle, no
clients and no bots**, so this is pacing alone:

| | before | after `timeBeginPeriod(1)` |
|---|---|---|
| p50 | 31.04 ms | **33.30 ms** (the target) |
| p99 | 46.98 ms | **34.79 ms** |
| max | 47.54 ms | **36.10 ms** |
| ticks over 40 ms | **133 / 870 — 15.3%** | **0** |
| shape | bimodal: 736 at 30–34, 133 at 45–49 | 868 of 870 in one bucket |

⚠️ **The mean hid it completely.** Before the fix the *median* was 31.04 ms —
below the 33.33 target — because the loop catches up on the ticks after each
overshoot. Only the distribution shows the problem, which is why "average tick
time looks fine" is not an answer to a report of hitching.

`GG2.Server.TimerResolution` holds the window open for the life of the run.
⚠️ **Pacing only, never simulation**: a tick computes the same thing whenever it
runs, so docs/SIMULATION.md's contract is untouched.

⚠️ **The obvious alternative does not work**, measured rather than assumed: a
`Thread.Sleep(1)` plus spin loop is bimodal in the same way, because `Sleep` is
rounded by the same quantum.

⬅️ **This does NOT close the report.** It removes one measured cause; whether the
players' rubberbanding was pacing or genuine physics drift is still unproven, and
drift is the more serious possibility. The next playtest is the test — and if it
still hitches, the cause is the one with no gate.

⬅️ **2. The heavy's sandvich does nothing — because it is not implemented.**
Confirmed in the source, which says so in four places: `PlayerCommand.cs`
("`serverEatSandvich` is absent because the port has no sandvich"),
`CharacterPhysics.cs`, `SpawnRooms.cs` (twice) and `Weapons.cs`.

⚠️ **This contradicts two claims in this repo** and both were wrong before the
playtest: CLAUDE.md's "✅ Every class is implemented", and the handoff's
"nothing else in the port is known-missing". A class being playable is not a
class being complete. **A gap noted only in a code comment is a gap nobody is
tracking.**

⬅️ **3. A rocket killed a scout and carried on to explode on the stairs beyond
it.** Not yet diagnosed. ⚠️ **It is in the code this session changed most** —
`RocketHitCharacter`, `BackOutOfCharacter` and `ProjectileMask` — so check
against a build from before `0a4e9c3` before assuming it is long-standing.
⚠️ **And it may be a client-side artefact**: the same session reported
rubberbanding, and a mispredicted rocket is drawn flying on by a client that has
not yet been corrected. The recording is on disk, so this is answerable from the
trace rather than from memory.

### ✅ ANSWERED: the rubberbanding is physics drift, and it is ours

**Measured against the reference server, with the same client.** The pacing
fix above removed one cause but did not close report 1. This closes it.

**The instrument.** `Character.events/User Event 13.xml` **hard-assigns** `x`,
`y`, `hspeed` and `vspeed` from the wire on every `QUICK_UPDATE` — no smoothing,
no reconciliation — and then sets `xprevious = x`, erasing the evidence. So a
desync *is* that assignment jumping, and it is visible from inside a stock client
with no modification to the reference tree: a `gg2_watch` expression samples the
character once a frame and compares each frame's position against the previous
frame's `x + hspeed`.

⚠️ **The residual has to be split into two populations, not thresholded.** A
crude prediction model has a bias — gravity alone leaves ~0.7 px a frame in `y` —
and the bias lands on update and non-update frames alike, so **the non-update
population is the noise floor and the comparison is the measurement**. Absolute
thresholds ("corrections over 4 px") count model error as desync; the first run
here did that and read 102 snaps that were mostly jump impulses.

✅ **Three checks say the instrument works before any number is believed:**

- **The update frames are 1 in 7.** Measured `updRatio` 0.1426 and 0.1422
  against the protocol's exact 0.1429, which is what proves `global.updateType`
  is being sampled *after* the network read rather than a frame stale.
- **A motionless character reads exactly 0.00 on both populations**, so the
  `QUICK_UPDATE` assignment introduces no error of its own.
- **The non-update floor is 0.0003–0.024 px**, so the model accounts for the
  client's own physics to a fraction of a pixel and anything above it is signal.

**The comparison.** Same map (`koth_corinth`), same class, same constant input
mask, same client build; only the server differs. **Both runs ended at
identical x — 3077.50 and 515.50 — so the terrain traversed was the same.**

| leg | mean unexplained Δx on correction frames | max Δx | |
|---|---|---|---|
| hop right, reference | 0.1134 px | 4.24 px | |
| hop right, this port | **0.2068 px** | **19.30 px** | 1.8× / 4.6× |
| walk left, reference | 0.0501 px | 0.27 px | |
| walk left, this port | **0.1737 px** | **4.03 px** | 3.5× / 15× |
| either, non-update floor | 0.0186–0.0202 px | ~5 px | the control |

⚠️⚠️ **A live measurement of this kind must be audited from its own recording
before being trusted.** A contaminated run (a human firing a rocket launcher
mid-"constant input" leg; the reference's own maximum varying 0.33 px to
4.24 px run to run) inflates the mean and destroys any ratio built on a single
run's maximum. The trace records every player's keys every tick, so count
ATTACK ticks and distinct key masks inside the measurement window — a driven
leg must be one constant mask and zero attacks. The port's own walk-left leg
passes it: mask 64 held for 316 s, **zero** attack ticks.

⚠️ **The port drifts more than the reference on both legs**, with the
non-update floor identical (0.0186 vs 0.0202) as a control: **1.8–3.5× on the
mean**. The *walking* leg has the larger ratio (3.5× / 15×) than the hopping
one (1.8× / 4.6×) — it is not worst airborne.

⚠️ **Quantisation is ruled out by the reference's own column.** Position goes on
the wire as `round(x*5)`, so a correction of up to 0.1 px is inherent — and the
reference never exceeds **0.33 px**, which is that bound plus accumulation. This
port reaches **23 px**. The two servers quantise identically; only one drifts.

⚠️ **Input timing is ruled out by the client's own code.** The client reads
`keyState` off the wire *every tick*, on the `INPUTSTATE` tier too — so between
position updates it is simulating with **the server's inputs, not its own**. With
input identical by construction, seven ticks of divergence can only be physics.
The constant-mask leg rules it out a second way: a held key is the same value
every frame, so no arrival offset can survive to explain 0.1737 px.

⚠️ **This is the failure mode CLAUDE.md says has no test, and it had none.** The
parity harness proves the port matches the reference *server* on recorded traces;
it says nothing about the *client's* prediction, and the client is what a player
watches. The harness and this measurement are not redundant — a fixture's exact
window ending is the same divergence seen from the other side.

⚠️ **Do not read this as "the physics is wrong everywhere".** The non-update
floor says the client and the port agree to a fiftieth of a pixel while nobody is
being corrected. What diverges is small. **There is no reliable pointer to which
branch yet** — see the withdrawal above — and the honest next step is more
measurement, on more than one leg each, rather than a hunt aimed by one run.

**How to repeat it**: the whole run is four `gg2_eval` calls and two waits per
server.

### The client-prediction gate, and what pinning it turned up

Built for the failure mode CLAUDE.md calls invisible — "there is
no test that fails when trajectories drift; the client just snaps".

⚠️ **`ReplayMode.OpenLoop` has existed since phase 2, and
`PhysicsParityTests.OpenLoopPredictionErrorIsSubPixelOnATypicalTick` already
gated it — at p99 ≤ 5 wire units on **one** fixture.** What was missing was not
the instrument but the **pin**, and the breadth: a bound of 1 px passes
comfortably at every error this port actually makes, so it could never have
noticed a regression short of a catastrophe.

✅ **Open-loop is the client's view exactly, and that is checkable rather than
hopeful.** `Replayer.Anchor` resets each slot from the trace's *observed* block
— the quantised wire values — then simulates to the next position-bearing tick.
That is what `Character.events/User Event 13.xml` does to a client: hard-assign
`x`, `y`, `hspeed`, `vspeed`, then dead-reckon seven ticks.

`ClientPredictionGateTests` pins 34 fixtures. **Four single-player recordings
hold the client to two wire units — 0.4 px.** The fifth, the human take, sits at
**270 / 44 in y**, which is the known drop-through-platform bug: 54 px of
correction, and the row will move when that is fixed.

⚠️ **p99, not max, and it is forced.** A death teleports a character to a spawn
point, so any window containing a respawn shows an "error" of most of the map.
That is why the combat fixtures have maxima in the hundreds beside a p99 of 2.

⚠️ **Two fixtures did not frame, and the assertion found both in one run.** The
first draft pinned `cp_dirtbowl` from **458 bytes of its 138,406** — 0.3% — and
it passed. A multi-area map writes every area's control points into its file and
plays one area's worth, so the un-narrowed shape expects a wider HUD block than
the wire carries; `.ForArea(1)` fixes it. `gen_destroy-rotationcaps` cannot be
framed by any single shape and is excluded: it exists to *be* that case.
**A distribution is only worth pinning if the whole recording was read**, and
`TraceParser.Parse`'s three-argument overload throws that away silently.

⚠️ **A steady walk is blind to input phase — measured, and it changes which
fixture to reach for.** The off-by-one update pairing that
`PairingInputsWithTheWrongTickIsMeasurablyWorse` detects on the bot trace moves
`scriptedwalk` **not at all**: both score p99 1, because a constant input is the
same input a tick early. **The single-player fixtures are the sharpest
instrument for trajectory error and the bluntest for input timing.** Asserted, so
it is not rediscovered.

**Rows worth a look some day**, recorded because nothing else would notice them:
the heavy at 49 in x, the sniper at 25/20, `gg_debug-closedtdm` at 77 in y, and
`gen_destroy-closedgen` at 18/30.

### ✅ FIXED: the dropdown push-out's guard had the wrong sign

The port put a player back on a platform they had dropped through. One
character, one sign, and it was worth more than its size.

`Character.events/Step.xml`, inside `with(DropdownPlatform)`:

```
if(place_meeting(x, y + (other.y - other.y_previous), other)) continue;
```

⚠️ **`x` and `y` there are the PLATFORM's and `other` is the character**, so the
platform moves DOWN by the character's own dy. Relative to the character that
asks *"was I already inside this platform at my previous y"* — which is the
whole mechanism that lets somebody keep falling through a platform they have
already entered. The port moved the **character** down by dy instead, which asks
whether it will be inside the platform *next* tick: the opposite question. The
comment sitting above the line already described the correct behaviour, so this
was a transcription slip, not a misreading.

⚠️ **Shift the platform, not the character — they are not equivalent.** GM8
rounds the *calling instance's* position to build the box `place_meeting` tests,
so the reference rounds `platformY + dy`; rounding the character's `y - dy`
instead is a different number with an unrelated fractional part. A platform's own
y is an integer, so `round(P + dy)` is `P + round(dy)` except where `dy` lands
exactly on a half and the platform's parity decides it — a tie this project does
not chase.

**What one sign was worth, measured across every fixture, with nothing
regressed:**

| | before | after |
|---|---|---|
| `humanmoves` exact window | 1,161 | **2,525** — the whole recording past its anchor, zero mismatches |
| `humanmoves` client prediction, y | max 270, p99 44 | **max 24, p99 2** |
| `gg_debug-closedtdm` first divergence | tick 152 | **tick 166** |
| `gg_debug-closedtdm` client prediction, y | p99 **77** | **2** |
| every other fixture | — | unchanged |

⚠️ **`gg_debug-closedtdm` gained a mismatch while improving** — 2 becomes 3 — but
its *first* divergence moved 14 ticks later. The count is not the bound; the
first bad tick is.

✅ **Nothing but the new client-prediction gate was watching
`gg_debug-closedtdm`'s 77.** Its closed-loop window is pinned and moved by 14
ticks, which is small enough to be noise to a reader; the open-loop p99 fell by
a factor of 38. That is the case for the gate existing.

⚠️ **This is a plausible contributor to the rubberbanding two humans reported**,
and it is not proven to be. A player who taps down through a platform gets
corrected by up to 54 px, which is exactly what they described — but the reports
were on `koth_corinth` and the measurement above is on other maps. Do not close
the drift item on this.

### ✅ IMPLEMENTED: the heavy's sandvich

From a player's report in the first two-human game — "heavy sandwich doesn't
work" — not from any test.

⚠️ **It had been absent for the whole port and was recorded only in four code
comments.** CLAUDE.md said "✅ Every class is implemented" and the handoff said
"nothing else in the port is known-missing", and both were wrong.
**A gap noted only in a code comment is a gap nobody is tracking.**

**The shape.** `OMNOMNOMNOM` (24) is a client *command* with no payload, like the
zoom and the sentry build, so it cannot be a key bit and a bot reaches it by the
same method the socket does. `serverEatSandvich.gml` checks five preconditions
and only starts the animation; **every hit point comes from
`Character.events/End Step.xml:83-96`, a tick at a time.**

| | |
|---|---|
| duration | `omnomnomnomindex` 0 → 32 at **0.25/tick** = **128 ticks**. No timer is involved |
| heal | **1.6 hp/tick**, so 204.8 over a full eat — more than a heavy's 200 |
| cooldown | **1350 ticks (45 s)**, `alarm[6]` |

⚠️ **The reference's own comment says "10 second cooldown" and is wrong** —
`eatCooldown = 1350`. Copying the comment would have imported the bug.

⚠️ **The cooldown is re-armed on every tick of the eat while hurt**, not once at
the start; the reference explains why in its own comment ("the 'ate and got hit
but didn't refresh cooldown' bug"). A heavy hit on the last tick pays the full
45 seconds from there.

⚠️ **Eating suppresses MOVEMENT, not just the attack.** `Begin Step.xml:237`
wraps the acceleration and the both-keys cancel in `if(!taunting and
!omnomnomnom)` — but **not** the friction below them, so an eating heavy coasts
to a stop rather than stopping dead or sliding on. The attack, the taunt, the
cloak and the right click go with it.

⚠️ **The facing freezes, and that is not cosmetic on the server.**
`End Step.xml:86` writes `image_xscale = xscale` *after* the line deriving the
facing from the aim. `Aabb.ForCharacter` inflates a character's box by two
pixels on the side it faces, and that box is what `distance_to_object` measures
splash damage against — so a frozen facing changes who a rocket hurts.

⚠️ **A heavy who eats at full health sits at 201.6 hp for the whole eat.**
`if (hp <= maxHp) hp += 1.6` fires at exactly maxHp, and the clamp is *earlier*
in the same event, so each tick clamps to 200 and is immediately pushed back
over. Every state update in that window carries the overshoot. Transcribed, not
tidied — and the first draft of the test asserted 200 and was wrong.

✅ **The cabinet's third clause became load-bearing.** `(hp < maxHp or
nutsNBolts < 100 or !canEat)` — a heavy at full health and full ammo with a spent
sandvich now enters that branch purely to have the cooldown cleared, where before
it was a no-op transcribed as a comment. ⚠️ **Team Deathmatch's arm does not
clear it**, so a TDM heavy standing on a cabinet to reset their sandvich gets a
third of a hit point and keeps waiting.

✅ **Re-measured across every fixture: nothing moved.** The feature is inert
until somebody eats, and no recording contains one — which is also the honest
statement of its coverage. `SandvichTests` (9) is unit coverage, not parity
evidence; **no fixture holds a sandvich, so none of this is measured against the
running game.**

### ✅ DIAGNOSED: the rocket that "carried on past the scout it killed"

The third report from the first two-human game. **No server bug — and the
mechanism is not the one the report points at.**

⚠️ **A rocket is NEVER on the wire.** `WEAPON_FIRE` carries the shooter's
position and velocity, quantised, plus the RNG seed — and nothing else. **The
client creates the rocket from that and simulates its entire flight locally,
with nothing ever correcting it.** Mines *are* serialised, every tick, which is
what makes the omission easy to miss: `StateUpdateWriter` has a mine block and
no rocket block.

**So the rocket a player watches is their own client's, start to finish**, and
the question "did the server's rocket carry on" cannot be answered by looking at
the picture. On the server it does not: `RocketHitCharacter` explodes and frees
it on the tick it hits, now gated end-to-end through the real collision path by
`RocketTests.ARocketThatHitsAnEnemyIsDestroyedRatherThanFlyingOn`.

✅ **The actual mechanism is the target's prediction, not the projectile's.**
Both sides simulate the rocket from near-identical launch states — position to
1/5 px, and the velocity is the launcher's own constant rather than anything
inherited — so the two rockets stay close. What the client is *corrected* on
every seventh tick is the **character**. A scout is the fastest thing in the
game, so its seven-tick dead-reckoning error is the largest in the game: the
rocket passes where the client has drawn the scout and hits where the server
has it, and the client draws the miss.

⬅️ **Which makes this the same finding as the rubberbanding, seen from another
angle**, and it inherits that item's open question rather than closing it: the
port's client-prediction error is **1.8–3.5×** the reference's, so this port
makes the artefact more likely than stock GG2 does — but the artefact exists in
stock GG2 and no amount of server-side work removes it.

⚠️ **Two traps for whoever tests a projectile next:**

- **A tick is `Move` and then `Collide`, two stages far apart**
  (`MatchSimulation.cs:234` and `:313`, with every character's physics between
  them). A test that calls only `Move` flies the rocket through everybody — and
  looks exactly like a reproduction of this report. The first draft of that test
  did precisely that.
- **Do not reach for the recording.** The trace is the server's broadcast, so it
  holds the shot, the death and the positions — and never the projectile. The
  question the report asks is not in there.

### `gg_debug`'s t523 residue: narrowed, and the standing suspect was wrong

Still open, but it is no longer "one wire unit, undiagnosed".

**What diverges.** At t523, slot 4 only:

```
tick 523 slot 4 x:      expected 2374, got 2375  (+1)
tick 523 slot 4 hspeed: expected  -46, got  -45  (+1)
```

**Both are rounding-boundary crossings, and the port's exact values say so.**
Replaying to the tick before and reading `PortedSimulation.TryGetState`:

```
x      = 474.94116167832897   ->  x*5      = 2374.7058   (rounds to 2375)
hspeed = -5.350951194573562   ->  hspeed*8.5 =  -45.4831 (rounds to -45)
```

⚠️ **The hspeed residual is 0.0169 of a wire unit from the boundary** — about
**0.002 px/tick**. This is a ULP-scale disagreement being magnified by
quantisation, not a wrong branch.

**The residual per position-bearing tick, slot 4, from the anchor** (port's exact
value minus the recorded quantised one, in wire units):

| tick | hspeed residual | x residual | moveStatus |
|---|---|---|---|
| 383 | +0.0000 | +0.0000 | 0 |
| 390 | −0.1105 | −0.1604 | 0 |
| 397 | −0.4433 | +0.2213 | 0 |
| 404 | +0.0000 | **+0.3792** | 0 |
| 411–474 | +0.0000 | **+0.4865, frozen** | 0 |
| 523 | **+0.5169** | +0.7058 | 0 |

⚠️ **It does not accumulate monotonically — it oscillates and finally crosses.**
Every earlier tick is inside half a unit, which is why the window is 142 ticks
and not 20.

⚠️ **`moveStatus` is 0 on every tick of the window where the residual is born,
so no knockback is involved — and the standing suspicion that it was the
knockback path is WITHDRAWN.** `MotionAdd` and the explosion positions are not
implicated by this evidence. The residual grows across t383–t404 while the
character walks 41 px and stops, and then **freezes for 70 ticks while it stands
still**. The suspect is ordinary walking and friction arithmetic.

⚠️ **Do not read the frozen +0.4865 as a measured 0.097 px offset.** The
reference column is quantised, so what is known is that the port's resting x is
629.4973 px and the reference's is somewhere in `[629.3, 629.5)`. The true
difference is bounded, not measured — **a trace can never hand back an exact
reference position**, which is the ceiling on how far this can be chased from
recordings alone.

⚠️ **And the earlier confusion is worth keeping**: the recorded x moves 2940 →
3050 in seven ticks, which reads as impossible for a walking soldier until you
remember the wire's ×5. That is 22 px, not 110. **Divide before disbelieving.**

### ✅ The damage accumulators, and why the parameters are required

`gg2-rl/gg2-rl/docs/ENV.md`'s first prerequisite — the half of
`docs/BOTAI.md` item 7 that was never built. `TickEvents` carried kills, deaths,
spawns and captures with attribution and **no per-tick damage amounts**.

⚠️ **They cannot be reconstructed by diffing hp**, which is why item 7 says so:
healing, a medic's beam, a cabinet, spawn invulnerability and a respawn all move
hp without anybody dealing damage, and afterburn moves it without anybody acting
that tick.

**`TickEvents.DamageDealt` / `DamageTaken`**, per slot, cleared by
`ClearOutputs`. Accumulated inside `DamageApi.Deal`, the one chokepoint every
character's damage passes through.

⚠️ **`Deal`'s new parameters are REQUIRED, and the convenience overload was
deliberately not kept.** An overload without them would let a new damage site
accumulate nothing and nobody notice — **a reward signal that is quietly
incomplete is far harder to see than one that is obviously broken**, because a
policy simply learns a slightly wrong game. Making the compiler ask "who dealt
this?" is the design, and it is what found all eleven sites in one build.

The rules, each gated by `DamageAccumulatorTests`:

- **Credited at application time, following `LastDamageDealer` semantics** — so a
  rocket in the air still credits a shooter who has since died, exactly as a kill
  does.
- ⚠️ **Self-damage is taken and never dealt.** A rocket jump must not pay. The
  sharp case is one explosion that does both: 60 px from an enemy, the blast
  deals to them *and* self-damages the shooter, and only the first half reaches
  `DamageDealt`.
- ⚠️ **Blocked damage accumulates nothing.** `Deal` returning false is Team
  Deathmatch invulnerability. Note this is deliberately **not** the rule beside
  it: the reference resets `timeUnscathed` even on a hit that did nothing.
- ⚠️ **The dealer may be `Nobody` (255, never 0)** — afterburn whose lighter has
  left. Then the damage is taken by somebody and dealt by no one. A defaulted
  zero would credit the server's own reserved player.
- **Sentries and generators are out of v0** and do not route through `Deal`
  anyway. Damage a sentry's *turret* deals to a character is in, credited to the
  sentry's **owner**.

✅ **Re-measured across every fixture: nothing moved.** This is additive
bookkeeping with no behaviour change, and the sweep says so rather than the
argument.

⚠️ **The unit tests pay for it with an argument they do not want.**
`DiscardedEvents` is a `[ThreadStatic]` buffer for the 132 test call sites that
drive a simulation entry point and do not care what it announced. A convenience
overload in the simulation would have been tidier and would have put the hole
straight back, because production code could reach it too.

### The platform's box, and where a mask hangs

Two live-game questions, settled with a dedicated server on `gg_debug` and
**no `gg2.ini` edit at all** — neither probe needs a character, so the bots
setting is untouched and the reference tree is unchanged.

#### The dropdown platform's box: 60 × 6, and the port is right

`sprite44`, via `probe-sprite-boxes.js`'s `mask_index` trick — a `Shot` wearing
the sprite, which works because **a bounding box is a property of the sprite and
solidity is not**. The collision oracle samples every rectangle the port
builds, but only for things that are *solid*, and a dropdown platform is never
solid to anything — so this box needed its own probe.

Measured: **width 60, height 6, origin (0,0), sprite bbox 0,0..59,5**, instance
box `[0..59] × [0..5]`, **nine sub-images**. `ColliderSprites`' inferred
`BarLong × BarShort` bar is exact, and `SolidRect.FromEntity`'s
`round(x + w) - 1` gives the same inclusive right edge the engine reports.

⚠️ **The flip rule now holds on a seventh sprite**: `flipped = [-(right + 2),
-left]`, two pixels wider than the unflipped box. `SpriteBoxSampleTests` already
asserted it over every sample, so the platform joined that gate for free.

#### ⚠️ OPEN, latent: a flipped collider builds an inverted rectangle

`SolidRect.FromEntity` computes `Right = round(x + width * xScale) - 1`, so at
`xScale = -1` it returns **Left = x, Right = x − 61** — a rectangle whose right
edge is left of its left edge, which overlaps nothing. The engine's answer is
**x − 61 .. x**.

✅ **Unreachable with the shipped maps** — *no* entity in any built-in map has a
negative scale, checked across `maps.json`, which is why no oracle ever showed
it. **A custom map can set one**, and the server serves custom maps. Pinned by
`SpriteBoxSampleTests.AFlippedColliderStillBuildsAnInvertedRectangle`, which
**fails when it is fixed**; left unfixed because changing collision geometry
with no case that needs it is a change made on spec.

#### ✅ A precise mask hangs off the ROUNDED position

The open half of the mask model: every sample in `rotated-mask-samples.json` is
at an exact integer position, so the anchor was a choice rather than a
measurement — and it matters because a projectile is essentially never at an
integer position.

`probe-fractional-mask.js`, rotated Rocket at 30°, rasterised at +0, +0.25,
+0.5, +0.75: **+0.25 is pixel-identical to +0, and +0.75 is +0 shifted exactly
one pixel right.** That is rounding, and it rules out the mask following the
real position, which would have moved a boundary between +0 and +0.25.
✅ **`ProjectileMask`'s anchor on `CollisionWorld.RoundPosition` is correct**,
and `RotatedMaskSampleTests.AFractionalPositionCollidesAsItsRoundedOne` now
holds the port to the same property.

#### ⚠️ NEW, open: at an exact half, the box is the union of both neighbours

`probe-round-tie.js`, four bases:

| x | bbox at ⌊x⌋ | **bbox at x.5** | bbox at ⌈x⌉ |
|---|---|---|---|
| 3000.5 | 2999..3005 | **2999..3006** | 3000..3006 |
| 3001.5 | 3000..3006 | **3000..3007** | 3001..3007 |
| 3002.5 | 3001..3007 | **3001..3008** | 3002..3008 |
| 3003.5 | 3002..3008 | **3002..3009** | 3003..3009 |

⚠️ **The tie is not rounded to one integer at all.** The left edge takes the
lower neighbour and the right edge the upper, so the box is one pixel wider than
either. Consistent at both parities, so this is **not** half-to-even and **not**
half-away-from-zero — and `CollisionWorld.RoundPosition`, whose comment states
half-to-even as fact, cannot express it at ties.

✅ **NARROWED 2026-08-28, and the scary half of this entry was wrong.** The union
is a property of the reported **bounding box** — the `distance_to_object` family —
and **not** of `place_free`. The two are different GM8 primitives, which this
project already knows in another context ("GM8's two collision primitives
disagree about the same pair of instances": `distance_to_object` uses bboxes,
`place_free`/`place_meeting` use masks).

⚠️ **So "`RoundPosition` is on every collision path and cannot express this" is a
misreading.** On the `place_free` path `RoundPosition` is **measured correct**:
`CollisionSampleTests.HalvesRoundToEvenAndTheSamplesProveIt` sweeps four maps
with three different box widths *specifically* to discriminate half-up from
half-to-even at both parities, against the live game. `place_free` rounds half to
even and the port matches it.

**Tested, not argued.** Modelling the union on all three character box paths
(`PlaceFreeInMask`, `PlaceFree`, `PlaceMeetingKind`) **fails ten engine-oracle
tests** — the four `HalvesRoundToEven`, the four
`PlaceFreeAgreesWithTheLiveGameAtFractionalPositions`, and two
`PhysicsSampleTests`. The change is not merely unnecessary, it is wrong.

⚠️ **The input is NOT measure-nearly-zero, which was the stated reason for
leaving it.** Instrumenting a full `gg_debug` replay: **297 exact-half inputs to
`RoundPosition` in 521,780 calls, 138 of them on the three character box paths.**
Ties happen constantly. The real reason to leave the entry alone is better: on the
path where ties are frequent, half-to-even is the *measured* answer.

⬅️ **What genuinely remains open** is much narrower: at a tie the port builds one
rounded **bbox** where the engine reports the union of both neighbours, so the
edge is in `Aabb.ForCharacter`/`ForRotatedSprite` and reaches only
`distance_to_object` consumers — the rocket blast falloff, `collision_circle`, the
bubble pop radius. Not every collision path. The precise *mask* at a half matched
neither neighbour at an even base and matched the upper one at an odd base —
unexplained, recorded rather than theorised about.

⚠️ **And it is not the `gg_debug` t523 residue.** That was the reason to try it —
the residue is born during a walk, which is a collision path — and the union
change **does not move the divergence tick at all**, with 138 ties exercised.
Falsified; do not retry it.

### ✅ Every character box measured — and the physics oracle was measuring the wrong one

`gg2-samplers/probes/probe-character-boxes.js`. Chasing the `gg_debug`
t523 residue into the physics oracle turned up something bigger than the residue.

✅ **`CharacterBox.For` is exactly right for all ten classes, on both teams.**
The table had been *derived* and never asked, and nothing pinned it — a wrong
edge would have surfaced as a mysterious trajectory divergence rather than as a
wrong box. `CharacterBoxTests` holds it now.

**A character's box is a pure property of its class sprite** — four subtractions
in `Character.events/Create.xml:12-15` — so the probe needs no character, no bot
and no map. That is why this was cheap once anybody thought to ask, and why
nothing had forced it.

⚠️ **`gg2-samplers/sample-physics.js` overrode the engine with a table of its own, and
two of its numbers were wrong.** It set `top_bound_offset = -6` for the Scout
and the Heavy where the engine derives **-10** and **-12**. So
`physics-samples.json` is a recording of a character the port does not simulate,
and `PhysicsSampleTests` replays it through the port's real box.

⚠️ **It has been green for six phases on luck, not coverage.** No scenario in
48 × 48 ticks ever had an outcome that turned on the top edge — and the reason
that is easy is in the table itself: **nine of the ten classes share a bottom of
23**, so ground contact, which is most of what a scenario does, cannot tell the
boxes apart at all. The next scenario that touched a ceiling would have been
checked in with a wrong expectation and read as a port bug.

✅ **The sampler now derives all four bounds the way Create.xml does.**
⬅️ **The fixture itself still wants regenerating**, which needs a live game with
a character — the bots recipe — and is the first thing to do in the next live
session.

#### Two gaps in that oracle, found the same way

⚠️ **No Soldier.** Six phases of Scout, Heavy and Pyro only, and the Soldier's
`runPower` of **0.9** is a value none of those three has (1.4, 0.8, 1.1), so the
friction equilibrium it settles at was never measured. It is also the class of
the t523 residue. Added to the sampler.

⚠️ **Nothing ever starts from rest.** `hNum = round((random() * 12 - 6) * den)`,
so a start with `hspeed == 0` is essentially impossible — and the t523 residual
is born while a Soldier **accelerates from rest**. Not yet addressed; the
regeneration should force some rest starts.

⬅️ **So t523's lead is now "an unmeasured corner of the physics oracle" rather
than "somewhere in walking".** That is a much smaller place to look, and it does
not need the six-player recording reproduced.

### ✅ The physics oracle regenerated: correct boxes, a Soldier, and rest starts

The follow-up the box measurement left owing, and the port **matches every
tick of it**.

`physics-samples.json` is now **64 scenarios × 48 ticks**, up from 48, and the
three things it was missing are all in:

| | before | after |
|---|---|---|
| character box | sampler's own table, **wrong** for Scout and Heavy | derived from the sprite, as `Create.xml` does |
| classes | Scout, Heavy, Pyro | **+ Soldier** (16 scenarios) |
| starts | always already moving | **32 of 64 start at exact rest**, and all 32 move |

✅ **The port reproduces all of it.** So the per-tick physics is exact for a
Soldier, for a character accelerating from rest, and for the real boxes — none
of which had ever been measured.

⬅️ **Which narrows `gg_debug`'s t523 residue again, by elimination.** The lead
was "ordinary walking and friction arithmetic"; that arithmetic is now measured
in the state the residue is born in — a Soldier accelerating from rest — and it
is exact. **The residue is not in the per-tick walking formula.** What remains
is the interaction: the stage order around it, the collision path, or the state
the character carries into the walk.

⚠️ **`rest` is decided on the site index, not drawn**, and the random draws
happen either way, so making a site a rest start does not shift the stream for
every scenario after it. A sampler whose variants perturb the seed cannot be
compared run to run.

#### ⚠️ The one-client trap, hit again and worth the reminder

The first sampler run timed out with "the game did not reply within 10000ms".
The cause was not the game: **an earlier backgrounded run of the same sampler
was still alive and holding the bridge.** CLAUDE.md records the MCP-server form
of this; the other form is **two CLI tools at once**, and it looks exactly like
a hung game. `Get-CimInstance Win32_Process` filtered on the command line is
what identified it in seconds — `Get-Process` alone shows only `node.exe` and
cannot tell two node tools apart.

### ✅ The sentry self-destruct: ANSWERED. It is the gate collision event

A sentry built inside a
`TeamGate` or an `IntelGate` loses 999 hp to a collision event and dies in its
own End Step, on the tick it was built. The port had no such event, so it kept
every one of them.

`Sentry.events/Collision with TeamGate.xml` and
`Sentry.events/Collision with IntelGate.xml`, in full — both are two lines and
identical to `Collision with KillBox.xml`:

```
hp -= 999;
lastDamageDealer = noone;
```

**The evidence is positional and it discriminates perfectly.** Across
`reference-ctf_truefort-closedsentryteam.trace`'s twelve builds, taking each
`BUILD_SENTRY`'s broadcast coordinates and the sentry's own mask box
(`SentryMaskS`, -8/+7/-7/+12):

| build | position | gate | outcome |
|---|---|---|---|
| t280 slot 3 | (1574.2, 474.4) | red [1578,444]-[1595,497] | died t281 |
| t326 slot 2 | (3454.8, 474.4) | blue [3438,444]-[3455,497] | died t327 |
| t337 slot 6 | (3582.4, 570.4) | blue [3558,546]-[3575,593] | died t338 |
| t866 slot 2 | (3444.8, 474.4) | blue [3438,444]-[3455,497] | died t867 |
| t1102 slot 6 | (3454.8, 474.4) | blue [3438,444]-[3455,497] | died t1103 |
| t294, t338, t342, t790, t1482, t1612, t1646 | — | none | all lived |

**5 of 5 and 7 of 7.** The two in `closedsentrykill` are a thirteenth and a
fourteenth of the same shape: (1890.8, 474.4) sits in the red gate at
[1890,444]-[1907,497]. Both fixtures now replay with **zero** sentry mismatches.

#### ⚠️ The one step that made this look impossible: `cause 0` is not a literal

Every session that ran at this read the destruction byte the same way — *"killer
255, assistant 255, cause 0, and the only site that passes a literal 0 is
`sendEventDestruction(ownerPlayer, noone, noone, 0)` in the Begin Step guard, so
it must be the guard"*. Then it spent its time on the guard's three arms, all of
which really do read false at those coordinates.

**The premise is false.** `Sentry.events/Create.xml` sets `lastDamageSource =
-1`; none of the four collision events touches it; `End Step` passes it to
`sendEventDestruction`, which ends `write_ubyte(global.sendBuffer,
damageSource)` — and **`write_ubyte(-1)` reaches the wire as 0**, not 255. So an
End Step destruction with nobody to blame is *byte-identical* to the guard's.
The recording cannot tell them apart, and the guard was never the only candidate.

⚠️ **This confirms `DamageSource.None = 0` empirically** for the first time. The
port already mapped the reference's `-1` onto 0 on the argument that 0 is the one
value `createDamageSource`'s numbering leaves free — an inference. These
fourteen destructions are a recording of the reference doing it.

#### What follows, and what does not

- ⚠️ **Any gate, including your own team's.** Neither event looks at `team`, and
  three of the five above are an engineer's sentry in his own doorway. This is
  not the "enemy gate is solid" rule showing through; a gate is lethal to a
  sentry from both sides.
- ⚠️ **A setup gate is not a gate.** `ControlPointSetupGate` is object **536**
  with **no parent**, so it is neither a `TeamGate` (110) nor an `IntelGate`
  (516) and a sentry may stand in one. The collision events are keyed on the
  parents, and GM8 fires a collision event for an object's descendants:
  `TeamGate` covers 108, 109, 476, 477 and `IntelGate` covers 499-504.
- **`serverBuildSentry.gml` says nothing about a gate.** Its preconditions are
  class, 100 nuts and bolts, no sentry within 50 px, not in a spawn room, no
  sentry already owned, not on a cabinet. So the reference *accepts* the build,
  broadcasts `BUILD_SENTRY`, takes the hundred nuts and bolts, and kills the gun
  a moment later. An engineer who builds in a doorway simply loses the build,
  which is why a recording of bot engineers contains five of them.
- **The stage order is unobservable.** GM8 keys collision events by the other
  object's id, so strictly `TeamGate` (110) runs before `KillBox` (506) and
  `IntelGate` (516) after `FragBox` (509). All four write the same two fields
  and none reads anything, so `Sentries.GateCollisionStep` does both together,
  next to `KillBoxes.CollisionStep`.
- ⚠️ **A pit fall still does not touch a sentry**, and that asymmetry is
  unchanged: `Sentry.events/` has `KillBox` and `FragBox` and no `PitFall`.

#### ⚠️ The live experiment that "eliminated position" tested nothing

A live session built at the trace's exact coordinates, watched the sentry die,
then built a control at a position the trace says works — and *that* died too,
both of them to the server resetting a lone test player's roster entry to
`TEAM_SPECTATOR` and firing the guard's second arm. It concluded **position is
eliminated**, and wrote that into two files.

The control did its job: it proved the experiment was contaminated. What it
cannot do is eliminate the hypothesis the contaminated run was testing. **A
failed experiment leaves a hypothesis exactly where it was**, and this one was
recorded as a negative result for a day, which is the more expensive of the two
mistakes available here.

Gated by `SentryCauseFixtureTests` (the replay, plus a control that reads the
gate geometry straight off the map and needs no simulation),
`SentryKillFixtureTests` and two unit tests in `SentryTests`. All four go red
when `Sentries.GateCollisionStep` is removed — measured, not assumed.

### ⛔ The carry LANDED, and NOTHING IN THE SUITE COVERS IT

`MovingPlatforms.Carry` — 362 lines, and the reason `SIM_UUID` moved from
`7ff5940d97aa7bed` to `5dac8a5e7c37ce3a` — **is not exercised by a single test**.
Neutralised the whole function with an early return and ran the suite: **25
failures before, 25 after, identical sets.** Not "weakly covered": zero.

⚠️ **Re-measured on a GREEN suite**, because the original reading was taken
while 25 tests were red for an unrelated reason and "the same 25" is a weaker
statement than it looks. With the stamp red cleaned up (`PolicyFixture`)
the suite is **2,572 passing, and 2,572 still pass with the carry neutralised.**
The gap is unchanged and is now stated against a baseline of zero.
⚠️ **The mutation has to be opaque to the compiler** — a bare `return` at the top
is `error CS0162` under warnings-as-errors, so the probe returns on a condition
the compiler cannot fold.

⚠️ **`PreciseCollisionOracleTests` gates the COLLISION PRIMITIVE, not the carry.**
The probe settled that a character is a rectangle to GM8, that bounds are
inclusive and that position rounds half to even — all real, all about
`PreciseCollision`. The commit that landed the carry says it is "gated by
`PreciseCollisionOracleTests`", and that sentence is **false in the way this repo
keeps getting caught by**: the thing named is genuinely tested and is not the
thing claimed.

⛔ **An A/B that cannot detect the thing it is testing for is not a null
result, it is no result.** A stamp-mismatched policy A/B run on `ctf_2dfort`
came back identical on 23 maps — evidence only if the comparison can detect
the carry at all, which it cannot, since no bot stands on a moving platform on
any of them.

### NO SHIPPED MAP CONTAINS A MOVING PLATFORM

`MovingPlatforms.Build` reads exactly one entity type, `EntityTypes.MovingPlatform`
= `"moving_platform"`. Counted across the checked-in oracle
(`tests/GG2.Tests/Fixtures/maps.json`, 22 shipped maps): **zero occurrences.**

⚠️ **Two entities look like it and are not.** `tdm_mantic` carries 4
`dropdownPlatform` (the jump-through kind) and 15 `MoveBoxUp`/`MoveBoxDown`.
Neither reaches `Build`. **Only `gg_debug2` — this port’s own debug map — has
moving platforms**, five of them, and even 40 bot matches there are byte-identical
either side of the carry, because no bot ever stood on one.

✅ **So the carry cannot fire on any map the server rotates through**, which is
what makes `--policy-ignore-stamp` honest for a shipped map rather than merely
convenient. ⚠️ **Re-check this before repeating it**: it is a fact about the map
set, and a new map with a `moving_platform` entity falsifies it silently.

### Moving platforms: the motion is ported, the carry cannot be

`MovingPlatforms`, `WorldState.MovingPlatforms`, and a stage
in `MatchSimulation.Tick`. Reference:
`Objects/Map elements/Colliders/MovingPlatform.events` — Create for the defaults,
User Event 1 for the map's overrides, End Step for the motion, User Events 10
and 11 for the wire.

**What was actually wrong before.** The wire was complete in both directions and
had been since phase 6 — `MovingPlatformBlock`, the reader, the writer. What fed
the writer was `MatchState`, which held the entity's **load position and never
advanced it**. So a custom map's platform sat still on every client while each
client's own End Step walked its local copy away from it. A block that frames
correctly is not a block that says anything true.

**The five wire bytes** are `write_ushort(x_*5)`, `write_ushort(y_*5)`,
`write_ubyte(movementState)`, on `QUICK_UPDATE` and `FULL_UPDATE` and nothing on
an `INPUTSTATE`. ⚠️ **`x_`, not `x`** — the underscored pair is the real
position the motion accumulates into, and GM8's `x`/`y` are `round()`ed copies it
draws and collides with. No count prefix: the client sizes the read off its own
copy of the map, so writer and reader must agree or the stream desynchronises
permanently.

⚠️ **The degree round-trip must not be simplified.** The reference takes a
`point_direction`, converts it back with `degtorad`, and feeds `cos`/`sin` — so a
platform moving straight up has a horizontal step of `cos(90°)`, 6.1e-17 rather
than 0. Normalising `(left, -top)` directly is arithmetically nicer and is a
divergence.

✅ **And a correction to what that first said here.** The residue does **not**
make `x_` creep. At a real map coordinate it is ~14 orders of magnitude below the
position's own ULP, so `x_ += 1.8e-16` at `x_ = 300` is exactly `x_`. It is
visible only at the origin, which is where `MovingPlatformTests` has to show it.
The transcription stays faithful anyway — "unobservable at the coordinates shipped
maps use" is a weaker claim than "identical", and the second is free.

⚠️ **The clamp is asymmetric and that is not a slip.** Reaching the far end only
flips `movementState`; reaching the start assigns `startX`/`startY` back. So a
platform whose travel is not a whole number of steps overshoots its stated top,
turns round from there, and is snapped exactly on the return — which is what
makes the cycle an exact number of ticks for ever instead of drifting.

⛔ **The carry is NOT ported, and it is gated on something else.** The
reference's End Step ends with a `with(Character)` block that picks up whoever is
standing on the platform and shoves whoever it runs into. Every branch of it is a
`place_meeting` against the platform's own mask — and **a `MovingPlatform` has no
sprite**. `MovingPlatform.xml` declares `<sprite/>` empty, unlike its parent
`DropdownPlatform`'s `sprite44`; the mask arrives at run time from an embedded
sprite named by the entity's `resource` property and decoded out of the map's
metadata by `CustomMapCreateEntitiesFromEntityData.gml:68-88`. **That pipeline is
part of the unported custom-map work** (`docs/PROTOCOL.md`, "What a headless
server can skip"), so this port cannot know a platform's box — and GM8 with
`sprite_index = -1` collides with nothing, so every branch of the carry is a
no-op there too. **What is ported is therefore the whole of the reference's
behaviour for every map this port can currently load, not a subset of it.**

✅ **The parent is the good news for the day it lands.** Because
`MovingPlatform`'s parent IS `DropdownPlatform`, every `with(DropdownPlatform)`
and `place_meeting(…, DropdownPlatform)` in the character physics already means
"or a moving platform". So a character will stand on one through
`CharacterPhysics`'s existing dropdown path with no new standing rule — only the
carry has to be written.

⚠️ **`SIM_UUID` was deliberately NOT bumped, and this is the condition that
voids that.** A new stage in `MatchSimulation` is normally a bump. It is exempt
because the stage is *structurally* unreachable on every shipped map:
`MovingPlatforms.EndStep` iterates `min(state.MovingPlatformCount,
definitions.Length)`, both come from the map's entities, and **all 22 shipped maps
place none** — so the loop body cannot execute and the tick is bit-identical.
**`MovingPlatformTests.NoShippedMapPlacesAMovingPlatform` is the gate: if it goes
red, bump `SimulationBehaviourVersion` and do not reason about it.** The
alternative was invalidating `gg2-rl`'s trained checkpoint, which
`PolicyNetwork.Load` hard-refuses on a stamp mismatch. Note the exemption is about
what a *checkpoint* was trained against; a match on a custom map is outside the
stamp's claim either way.

⚠️ **`WorldState.MaxMovingPlatforms` is 16 and is a guess, not a measurement** —
nothing places one, so there is no maximum to size against. Overrunning it drops
platforms silently, the same shape as `MaxProjectiles`; raising it is the fix.

#### What the map archive says about moving platforms — measured 2026-09-01

⚠️ **The carry is NOT hypothetical — "no shipped map places one" was hiding
that.** Scanned all 2,348 maps in a community map archive with this port's own
`LevelMap.FromPng`: **1,838 parsed, and 8 of them place a moving platform.**

| map | platforms |
|---|---|
| `maw_deathmatch` | **139** |
| `cp_scrap` | 18 |
| `dj_bugs` | 16 |
| `Happy-Holidays-AT-The-RaveYard` | 11 |
| `ctf_2dfort-ani` | 5 |
| `CTF_Boat_struggle` | 4 |
| `Sani-request-marina-shakedown` | 3 |
| `surf_mup` | 1 |

⚠️ **Every one of them names a `resource`**, which settles the open question in
the other direction from what would have been convenient: `crane1`, `roof`,
`elevator2`, `Platform`, `ji`, `pl`, `Boat`, `SURF`, `animap`, `NewAnimap`,
`Joe`, `joe`, `bubble1`. There is no real map that leaves the sprite unset, so
"a platform with no mask" is not the common case — it is the case this port
currently produces for all of them.

**What the real values look like**, which is worth having before anyone tunes the
motion: `top` from 0 to 1518, `left` from 0 to 20000, speeds from 0 to 10000, and
`scale` up to 6. Several are decorative — `upspeed=0, downspeed=0, top=0` — which
lands in the `totalDist <= 0` arm and parks them, matching the reference.
`maw_deathmatch`'s 139 bubbles are `trigger=1`, so they wait to be stood on and
in this port will wait for ever.

⛔ **What the carry would actually cost, so it is scoped rather than guessed.**
The mask comes from `stringToResource` → `sprite_add(name, 1, 1, 0, 0, 0)`, and
`docs/SIMULATION.md` records that **a runtime-added GM8 sprite uses PRECISE,
per-pixel collision**. So the carry needs:

1. embedded sprite bytes decoded out of the map's `meta` properties (PNG or GIF,
   the format sniffed from the first three characters);
2. a **precise per-pixel collision primitive**, scaled by `image_xscale`/
   `image_yscale` — which nothing else in this port has or needs, everything else
   being walkmask cells and axis-aligned rectangles;
3. `removeback = 1` handling, so the sprite's background colour becomes
   transparency and therefore non-colliding;
4. only then the `with(Character)` block itself, including its
   `good_move_contact_solid` push with the platform made temporarily solid.

⚠️ **That is a subsystem, not a function**, and it is the reason this is written
down rather than attempted. ⚠️ **It is also the ceiling on `MaxMovingPlatforms`
being 16** — `maw_deathmatch` places 139, so that map would silently lose 123 of
them. Raise the ceiling before anyone points a server at it.

⚠️ **Separately: 510 of the 2,348 archive maps failed to parse here.** Not
investigated. It may be entirely non-map PNGs in the archive, or it may be a real
gap in `LevelMap.FromPng` — nobody has looked, and the number is large enough
that it should not be assumed benign.

#### The archive re-measured after the tEXt fix — and the shape question answered

⚠️ **The 510 parse failures were this port being stricter than the game.**
`LevelData.Extract` read `zTXt` only; the game reads through libpng's
`png_get_text`, which returns `tEXt`, `zTXt` and `iTXt` alike
(`Extensions/GG2DLL/GG2DLL/GG2DLL.cpp:357-368`). GG2 *writes* `zTXt` (:288), so
every map saved by the game itself worked and every map from another editor did
not. Fixed. ⚠️ **`gg2-nav-gen` had the identical bug in its own
reader and found 438 affected maps; this port recovered 438 too** — two
independent readers, same count, which is what makes it a diagnosis rather than a
coincidence.

**Archive parse rate: 1,838 → 2,276 of 2,348.** The 72 that still fail are
honest: 65 carry no level-data chunk at all (ordinary PNGs in the archive), 2 are
not PNGs, and 5 hit `entity '0' has a non-numeric y: 'PayloadNode'` — a custom
entity from a gamemode mod, **not investigated**.

✅ **Every embedded sprite resource decodes intact**, which was the open risk on
the platform carry. 31 resources across the 8 platform maps, all recovered from
the `meta` block with PNG or GIF signatures whole — so GGON carries raw bytes
through the Latin-1 level-data string without corruption. That was the cheapest
thing that could have sunk the carry and it did not.

⛔ **The bounding-box shortcut is NOT viable, and this is the measurement that
settles it.** Of 31 platform sprites, **12 carry a transparency mechanism** in
the file (RGBA PNG, or a GIF transparent-colour flag) and 18 are opaque; one
(`animap`, 9 bytes) is a truncated GIF that GM8 would fail to load, leaving
`sprite_index = -1` and no collision at all. Weighted by platform *instances*
rather than by sprite, the transparent ones cover **175 of 197 — 89%**. The
decisive case is `maw_deathmatch`: **139 of its platforms are 40x40 GIF
"bubbles"**, and a bounding box round a bubble is a square round a circle.

⚠️ **Two honest limits on that number.** A file carrying an alpha channel is not
proof that any pixel is actually transparent — that would need a real pixel
decode, which was not done. And it cuts the other way too: **`removeback = 1` is
always on**, so the 18 "opaque" sprites also have their background colour carved
out at load, and several are plainly shaped rather than rectangular (`Boat`
111x31, `piston` 60x161, `room1`/`room2` 20x76). So 89% is a *floor* on how many
platforms are irregular, not an estimate.

#### Verifying the precise mask against the running game — the plan

⛔ **NOT YET RUN. Everything in `SpriteMask`, `PngSprite`, `GifSprite` and
`PreciseCollision` is self-consistent and UNVERIFIED.** The suite proves the port
agrees with itself; it cannot prove GM8 agrees. This is the plan that would
settle it, written while the decisions were fresh.

⚠️ **The live game is exclusive** (`docs/PARALLEL.md`): one session holds the
bridge, and while it does, no other lane calls a `gg2_*` tool or runs a sampler.
Take it before starting, and say so in the handoff.

**What is actually uncertain**, worst first — this is the list a probe has to
retire, not a general "check it works":

| # | Uncertainty | Why it bites | Settled by |
|---|---|---|---|
| 1 | **`removeback` samples the BOTTOM-LEFT pixel** | Taken from Game Maker's docs, never measured. Wrong corner ⇒ a different shape for **18 of 31** sprites, which are opaque and carved by nothing else | A |
| 2 | Does `removeback` also apply on top of an **alpha channel**? | This port applies both. If GM8 skips it for RGBA, every alpha sprite is over-carved | A |
| 3 | Scaling maps room→source by **floor division** | Real maps scale up to 6x; an off-by-one at a boundary is a pixel of standing room | B |
| 4 | Character box vs mask, and **rounding at the edge** | A character is never on an integer coordinate; this decides nearly every real query | C |
| 5 | **Which frame of an animated GIF** collides | Wide open. If frames differ, `animationspeed` stops being decoration and becomes simulation | D |
| 6 | `grem` decodes to **1.0% solid** | A 56x70 sprite with 39 solid pixels is either a nearly-blank first frame or a bug in this port's GIF path | A, D |

**Phase A — the mask, at scale 1.** The core, and it retires 1, 2 and 6 on its
own.

1. Copy `tests/GG2.Tests/Fixtures/sprite-{ji.png,eleplat.png,bubble1.gif}` and
   `grem` into the game's working directory.
2. In the game, do exactly what the reference does —
   `s = sprite_add(file, 1, 1, 0, 0, 0)` — then attach it:
   `o = instance_create(0, 0, SpriteObject); o.sprite_index = s;`
3. Dump the engine's own mask with a `collision_point(xx, yy, o, true, false)`
   grid, one character per pixel, rows separated. ⚠️ **`prec = 1` is the whole
   point**: it is what makes GM8 answer per pixel rather than by bounding box.
4. Compare byte for byte against `SpriteMask.Decode`.

   ⚠️ **`eleplat` is the sharp instrument**: 10x2, no alpha, and this port has it
   exactly half carved. If the corner rule is wrong it disagrees on the first
   fixture rather than the fifth. `ji` (RGBA) answers 2 in the same run.
   ⚠️ **Keep the dump under a few thousand characters** — do not try this on
   `SURF` (973x198); sample it instead.

**Phase B — scaling.** Repeat A with `image_xscale = image_yscale = 3`, dump the
3x grid, compare against `PreciseCollision.Meets` with a 1x1 box. Retires 3.

**Phase C — a real character.** Place a Scout and a Heavy (the widest box) near a
platform edge and compare `place_meeting(x, y, o)` against
`PreciseCollision.Meets` with that class's `CharacterBox`, **sweeping x in 0.1
steps across the boundary**. The sweep is the point: a single position agreeing
proves nothing about the rounding. Retires 4.

**Phase D — animation.** `sprite_get_number(s)` on `Joe` (80x80) and `NewAnimap`.
If it is 1, GM8 took one frame and this port is right by construction. If it is
more, dump the mask at each `image_index` and see whether the silhouettes differ.
Retires 5, and tells us whether the carry needs an animation clock.

**How to record it.** ⚠️ Follow the parity discipline, not an ad-hoc note: any
mask that disagrees becomes a checked-in fixture with the engine's answer as the
oracle, the way `sprite-box-samples.json` already works for character boxes. A
probe run that finds no disagreement should still leave its grids behind — "we
checked and it matched" is only worth anything if the next person can see what
was checked.

⚠️ **Do not skip the negative result.** If Phase A agrees on all four sprites,
say so here with the date, and the `removeback` warning in `SpriteMask` comes
down. An unverified claim left standing after it was verified is its own kind of
stale.

#### ✅ RUN 2026-09-01 — three of the six retired, and the GIF model was wrong twice

`probe-platform-mask.js` against a dedicated server. The oracle is checked in as
`tests/GG2.Tests/Fixtures/sprite-mask-samples.json` and
`SpriteMaskOracleTests` holds the decoders to it pixel for pixel.

✅ **1 and 2 — `removeback` samples the BOTTOM-LEFT pixel, and it applies on top
of an alpha channel.** Both PNGs matched exactly on the first run.
`sprite-eleplat.png` was the discriminator as intended: 10x2, fully opaque, and
the engine's mask is the top row solid and the bottom row clear — which is the
bottom-left rule and nothing else. **The warning that stood on
`SpriteMask.BackgroundPixelIndex` is now a measurement.**

✅ **3 — scaling is exact replication.** At 3x every sprite has exactly nine
times the solid pixels (10→90, 184→1656, 586→5274), so the room→source mapping
is plain floor division, which is what `PreciseCollision` does.

✅ **The anchor was already settled** and needed no probe: `probe-fractional-mask.js`
established that a precise mask hangs off the ROUNDED position, so
`PreciseCollision`'s use of `CollisionWorld.RoundPosition` is right by prior
measurement.

⛔ **5 and 6 — the GIF model was wrong, twice, and only measurement found it.**
The engine says **586** solid for `bubble1`; this port said 576.

1. **`sprite_get_number` is 4.** GM8 expands an animated GIF into subimages
   whatever `imgnumb` asks for, and the mask is the **union of all of them** —
   GM8 sprites default to one shared mask rather than separate per-subimage ones.
   ✅ **So the shape does NOT change as a platform animates**, and
   `animationspeed` stays decoration as far as collision goes. First-frame-only
   was out by 10 pixels.
2. **`removeback` is applied PER SUBIMAGE, each sampling its OWN bottom-left
   pixel.** The bubble's later frames are 3x5 and 3x7 patches at (1,16), (0,16)
   and (1,17) whose own corners differ in colour from the 40x40 first frame; one
   global background colour left three pixels solid that the engine clears.
   Per-frame: **zero mismatches.**

⚠️ **The bug that hid the frames is worth naming.** The LZW decoder returns the
moment it sees the end code, leaving its cursor inside a sub-block — so following
that cursor reads a length byte as a block type, the walk stops, and only frame 1
is ever seen. This mistake is easy to repeat (it appeared independently in both
a throwaway analysis script and in `GifSprite`) — decode from a copy of the
offset and walk the sub-block chain separately.

⚠️ **Still open: 4 (a real character box against a mask) and the `grem` sprite**,
which decodes to 1.0% solid and has not been put in front of the engine. Phase C
is unrun — everything above used a 1x1 box, so the inclusive-bounds and
class-box halves of the query are still only self-consistent.

### A respawn replaces the character and leaves the player alone

`doEventSpawn.gml:30-38` destroys `spawner.object` and creates a new **character**
into it. The `Player` instance beside it is never touched: it is created on a
join (`serviceJoiningPlayer.gml:117`, `GameServerCreate.gml:39`) and
`Player.events/Create.xml:28-40` is the only place `stats[]` is zeroed. So
`WorldState.Spawn` updates the player in place and must never assign a fresh one.

Five fields depend on it, and each fails differently:

| Field | What replacing the player costs |
|---|---|
| `Stats` | the scoreboard resets on every spawn. `Deaths` is the one that can never be right — a death awards it and arms the respawn that wipes it, so it reads 0 for everybody for the life of a server. |
| `RoundStats` | the same, and Arena's `resetRoundStats.gml` is what clears it. That script only makes sense because a spawn is not one. |
| `Humiliated` | a player flag precisely so it survives the death that usually earns it. Only `doEventArenaRestart`, `doEventArenaWaitForPlayers` and `basicRoomSetup` clear it. |
| `QueueJump` | a CLIENT_SETTINGS preference, sent once. `Character.events/Begin Step` reads it on every landing, so losing it is a dead-reckoning divergence from every client, not a cosmetic loss. |
| `IsBot` | this port's own mirror, and what `Handover.ShouldEngage` reads. Losing it turns `EngageOpponents.HumansOnly` — the live server's default — into `Anyone`, so `--bot-engages-bots` does nothing. |

`DeathmatchRespawnBypass` is the exception: the spawn it feeds **spends** it, and
the next protected death writes it again. A fix that preserved the player
indiscriminately would grant permanent spawn protection in Team Deathmatch.

⚠️ **`PlayerState.Fresh` first when `HasPlayer[slot]` is false.** `RemoveSlot`
leaves `default` in the tail — a red scout with its alarm at zero, the trap
`Fresh` exists for — so a slot reused after a departure would inherit it.

**`RespawnKeepsThePlayerTests` is the gate**, and nothing else in the suite
covers any of it: no other test asserts a stat across a respawn, and every arm
measurement runs `EngageOpponents.Anyone` by design, so the `IsBot` half is
invisible to all of them. Restoring the fields moved no recorded measurement —
`StatIndex.Deaths` is read only by `GameServer.Broadcast`'s wire scoreboard, and
the bot measurements count deaths their own way.

⚠️ **Verified live rather than only green**: two 8-bot `ctf_truefort` servers,
`--bot-mode tiered`, no humans. Default gate, `engage: humans` — **0**
micro-driven ticks across the roster. With `--bot-engages-bots`, `engage:
anyone` — micro-driven ticks appear. Both report balanced kills and deaths, and
a bot that died kept the kill it had.

### Upstream bug fixes carried into the port

Reviewed on 2026-09-26 against the 24 fix branches on
`github.com/kylelmoy/Gang-Garrison-2` — a GG2 fork that triaged upstream GitHub
issues and wrote a branch per fix. Each was read for whether this port has the
same defect, and six changed code here. **The reference is still the
specification everywhere else**; these are the exceptions, and each one is
marked as a deliberate departure at the line it lives on.

The rule applied: a fix lands only if a stock GM8 client is told about the
result through a message it already handles, or cannot see the difference at
all. A fix that would make the server and the client simulate different
outcomes with nothing on the wire to reconcile them does not land, however
correct it is.

#### The six that landed

| What | Where | Why a stock client is fine with it |
|---|---|---|
| An assisted hazard death leaves the flag in the pit | `CharacterState.DiedToHazard`, set in `KillBoxes.CollisionStep`, read in `Deaths.Kill` | The whole arm is host-only upstream too (`if (global.isHost ...)`), and the clients are told by the `RETURN_INTEL` it broadcasts. |
| Afterburn kills a character on the tick it becomes invulnerable | `Afterburn.BeginStep` | `End Step.xml:34` is `if(global.isHost && hp<=0)`, so no client kills a character itself, and hit points are on the wire in every `QUICK_UPDATE`. |
| A key tapped between two updates is dropped | `NetworkInputSource.TakeEdges`, applied in `GameServer.Tick`, relayed in `GameServer.TryGetCharacter` | The key byte's width and meaning are unchanged; the client reads one tick of a held key, which is what happened. |
| A joiner inside the map-change countdown sets up the wrong stage | `GameServer.BeginMapChange` and `GameServer.CompleteJoin` | `MAP_END` is a message every stock client already handles, sent with the same four values every other client got. |
| A server name or welcome message over 255 characters | `ServerOptions.ServerName` and the two strings beside it | Nothing reaches the wire that did not before; the alternative was a throw inside every join. |
| A team with no spawn point strands its players | `Timers.PlayerSpawn` | Nothing is sent either way. The player does not appear, and asks again a second later. |

Each of the first four is separately gated, and the gates are written to fail
for the right reason: `KillBoxTests.AStolenBoxKillAlsoSendsTheFlagHome` asserts
the premise — that the box handed the kill over — before the consequence;
`FlamethrowerTests.AnUberedCharacterTakesNoAfterburn` runs the same character
un-ubered as a control; `InputReachesTheWireTests` has one test per half of the
tap fix plus a `Down` control; `MultiAreaMapTests` asserts the server keeps the
running area **and** that the joiner's `MAP_END` arrives after its own
`PLAYER_JOIN`.

Three details worth not re-deriving:

- **The tap fix is not a nicety, and the port was worse than the reference.**
  A client sends one `INPUTSTATE` per virtual tick, so two arrive in one server
  tick whenever packets coalesce — ordinary over the internet — and up to ten
  are read per player per tick (`ClientCommands.PerTickLimit`). The reference
  runs `event_user(1)` per input (`processClientCommands.gml:315`), so each
  one's edge lands in `pressedKeys` and the server acts on the tap; this port
  kept only `NetworkInputSource.Latest`, so the press was never seen at all,
  and a tapped stab, jump or airblast happened nowhere. The relay half is the
  fork's, and `Down` is the one key excluded from it: every other key reaches
  the simulation through `downOrPressedKeys` or a pressed-edge test, so the
  server did act on the tap, while `Down` is read as held only and relaying it
  would drop clients through a platform the server kept them on.
- **`MAP_END` after `PLAYER_JOIN`, never before.** Its handler creates a
  `WinBanner`, whose Create reads `global.myself.team` with no guard, and
  `global.myself` is the `-1` that `ClientCreate.gml:20` left until this
  player's own `PLAYER_JOIN` lands. `-1` is `self` in GM8, so the wrong order
  kills the client before it draws a frame.
- **The area cannot be corrected without the `MAP_END`.** Telling a joiner the
  running area and nothing else freezes its `global.currentMapArea` at that
  value for the whole connection, and stale at 1 across an advance it runs
  `ClientBeginStep.gml:511-544`'s team wipe that the server does not. The two
  halves are one fix.

#### Not affected, and why — so nobody re-derives it

| Fork fix | Why this port does not have it |
|---|---|
| Sentry hp packed under the built flag (`built*$80 + min(127,hp)`), where a negative hp borrows from bit 7 and clears the flag | Unreachable in both codebases. The state update is written at the top of the tick (`GameServer.WriteStateUpdate`, matching `GameServerBeginStep`), and a sentry whose hit points reached zero or below is destroyed in `Sentries.EndStep` in the same tick that took them there. **What would make it reachable:** moving the update below `Sentries.EndStep`, or adding a damage path that runs after it. Then clamp, as the fork does. |
| `BUILD_SENTRY` coordinates written to `global.serializeBuffer` | `ServerMessages.BuildSentry` writes all seven bytes to one writer. The fork's finding is that the reference's two buffers are *not* always aliased — `serializeBuffer` points at a joining player's socket for a tick after `ServerJoinUpdate` — which is a reference bug this port cannot express. |
| Autobalance drops the wrong team's intel | `TeamBalance.Switch` flips `player.Team` before clearing the character, exactly as the reference does, but `Intel.Drop` reads `character.Team` where `doEventDropIntel` reads `player.team`. The character's copy is still the pre-switch team, so the port already drops what the clients drop. **Do not "correct" the drop to read the player's team for fidelity** — that is what introduces the bug. |
| Minegun mine count read out of `lobbed` while destroying mines | A client-side deserialiser. The port writes the count from the projectile pool and never rebuilds mines from the wire. |
| Spy knife re-arm not delta-scaled (`alarm[2] = 18`) | The port is fixed at 30 Hz, where `global.delta_factor` is 1 and `18` and `18 / delta_factor` are the same number. A 60 fps *client* still disagrees with this server about when a stab re-arms, and nothing server-side can fix that: the stab decision is made on every machine from unsynced weapon state, and making it host-authoritative needs a stab event the protocol does not have. |
| Spawn at (0,0) when a team has no spawn points | `Timers.PlayerSpawn` has always returned on an empty list rather than reading index 0 of one. Only the re-arm half was missing; see above. |
| Map rotation shuffled in place and never restored | The reference's is a global that outlives a hosting session inside one process. This is a process that exits. |
| Autobalance notice names the wrong player | The name is on the host's own `Balancer` instance, which is presentation. `BALANCE`'s notice form carries no name at all — see `ServerMessages.BalanceNotice`. |
| A client that lacks the next map reconnects to download it and loses its slot | The client half is not this port's, and the server half already works: `JoiningConnection` serves `DOWNLOAD_MAP` before `RESERVE_SLOT` and without taking a slot, so the fork's fixed client keeps its place. |
| Client stops parsing on an unknown class or a wrong player count; recent-message diagnostics | Client-side robustness. The port refuses an undefined class on the way in (`Enum.IsDefined` in the `PLAYER_CHANGECLASS` case), so it cannot emit one. **The port's own reader does not make the player-count comparison** the fixed client does; that blind spot is `MirrorClient.CheckSpawnable`'s remark and `docs/PARITY.md`'s, and this review did not change it. |
| Captured-point effects replayed on a joining client's `FULL_UPDATE`; four-legged stabbing spy; sentry drawn folded; sentry gibs' collision mask; weapon shells during humiliation; Golden Weapon overlays at Christmas | Drawing, particles and client-side deserialisation. None of it exists here. |
| A client that disconnects is noticed late — the fixed client sends `PLAYER_LEAVE` first | The server half already works: `MessageType.PlayerLeave` closes the connection in `GameServer.Handle`, and during a handshake `JoiningConnection` closes on it as an unexpected command. Either way the seat is freed at once. |
| Orphaned sentry turret after `built` goes back to 0 | A client-side symptom of build progress diverging between machines. The port's sentry has no separate turret instance to orphan: `SentryState.HasTurret` is a field on the sentry. |

#### Deliberately not implemented

**A shot that ends its step inside a wall still misses a target standing right
in front of it.** `move_all_bullets` destroys the shot before its collision
events run, and at 30 Hz a Revolver shot travels 21 px a step — more than a
sentry is tall. The fork walks the path back to the last free point and stops
the shot there when an enemy is standing on it.

Not carried across, and this is the line the other six do not cross: a client
simulates every shot itself from `WEAPON_FIRE` and destroys it at the wall the
same way. A server that registered the hit anyway would deal damage no client
predicted, with nothing on the wire saying where it came from — a hit that
happens on the server and nowhere else. It is also a change to hit registration
rather than to a bug's consequences, which is the distinction `CLAUDE.md`'s rule
turns on.
