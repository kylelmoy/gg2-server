# The simulation contract

The wire protocol is the easy half. This document is the hard half: what the
ported simulation must reproduce, and — just as important — what it is free to
throw away.

## The state itself — what `WorldState` holds, and why

⚠️ **Moved out of `CLAUDE.md` on 2026-09-01, verbatim.** The five rules that bite
a session before it knows it is near them stayed there; this is the reference
half, which is worth grepping when you change what the state holds and worth
nothing to a session that is not going to. Nothing below was re-authored.

`Gg2.Simulation.WorldState` is the flat, cheaply-copyable state `docs/BOTAI.md`
item 1 asks for: a value type built on `[InlineArray]` slot buffers, holding no
reference to anything — no map, no config, no logger. Read-only inputs (the
`CollisionWorld`) are passed **to** `MatchSimulation.Tick`, never held by the
state. That item is the one the document says cannot be retrofitted, so do not
put a reference type in there.

It holds characters, their weapons, their sentries, the players behind them
(`PlayerState`: team, class, respawn timer — phase 6), the two briefcases and
the cap counters (`IntelState` — phase 6's objective), the round (clock, limits,
winner, `mapchanging`, the setup timer), the control points, the KotH timers and
Arena's round state, both generators, Team Deathmatch's kill limit and spawn
invulnerability, the scoreboard and the domination matrix, the autobalance fuse
and the class limits, a fixed-capacity projectile pool and the match's own
`GmlRandom` stream. **`Gg2.Server.MatchState` holds no objective at all any
more** — the generator and the kill limit were the last of its placeholders. What is left is
server configuration and the roster's view of the map, and it should keep
shrinking rather than grow. `StateUpdateWriter` reaches the simulated half
through delegates (`CharacterLookup`, `SentryLookup`, `ObjectiveLookup`) so it
never copies a `WorldState`.

The RNG is in the state so that `Tick` stays a pure function and a forked
rollout forks the stream with it. **Which seeds it produces is free** — every one
is broadcast — but the stream has to live somewhere copyable, and a default
`WorldState` starting at seed 0 is what makes a test reproducible. `GameServer`
overwrites it at startup, which is the reference's `randomize()` and the only
place outside the simulation that touches it.

**Clear is a death and RemoveSlot is a departure, and the difference shows up in
what survives.** A weapon dies with its character; a sentry does not, and neither
does a rocket already in the air. That is the reference — a weapon is created by
`Character.events/Create.xml`, a sentry belongs to the *player*, and a
projectile's `ownerPlayer` is a player rather than a character, which is what
lets it still credit a kill to somebody who died before it landed.

⚠️ **A departure then destroys all of it anyway, and for four phases this port
did none of that.** `Player.events/Destroy.xml` destroys the character — the
*whole* Destroy event, not only the flag drop — and then sweeps every `Rocket`,
`BurningProjectile`, `Shot`, `Needle` and `Sentry` the player owned. The rule
runs the opposite way from the intuition: a rocket outlives its shooter's
**death** precisely so it can still credit the kill, and is destroyed the moment
that player is **gone**, because there is no longer anybody to credit. Until
2026-08-26 `RemoveSlot` dropped the briefcase and nothing else, so a soldier
could disconnect and still kill somebody a second later. `docs/SIMULATION.md`,
"Settled: what a departure destroys"; `RemoveSlotTests`.

⚠️ **And that line is invisible to every code-extracting grep of the
reference.** `if(object != -1) with(object) instance_destroy();` sits on the
*same line* as the opening `<argument kind="STRING">` tag, so anything that
strips lines beginning with a tag hands back a Destroy event that starts three
lines in. **Third time this project has been caught by the event XML** after the
`Bubble`'s `action_kill_object` events and the Revolver's `action_set_alarm` —
the first two were drag-and-drop actions, this one is ordinary code. When an
event looks like it is missing its first statement, read the raw XML.

**Projectiles are walked in creation order, not pool order.** Pool slots are
recycled, so `WorldState.Projectiles` says nothing about when anything was
fired; `LiveProjectileSlot(i)` walks `ProjectileOrder`, which is dense and in
creation order. `docs/SIMULATION.md` makes that order observable. Handles are
index-plus-generation and resolve through `TryGetProjectile`, which refuses a
stale one.

**`ScenarioWorld.Load` reads through a process-wide cache** (`ScenarioResources`):
the `LevelMap` and `NavGraph` are decoded once per map and shared by every world
and every test in the process — both are deeply immutable, and the
`CollisionWorld` is still per-world because `SetCaptureZones` writes to one.
⚠️ **A map edited on disk mid-process is not re-read** (`resources: null` forces a
decode), and ⚠️ **a shared cache makes first-call properties order-dependent** —
it turned `ObservationWriter`'s zero-allocation gate into a coin flip.
`docs/ENV.md`, "the reset reload".

## Why any of this is a contract at all

GG2 is a **server-authoritative simulation with client-side prediction**, not a
state-streaming game. Six ticks out of every seven the server sends only
`INPUTSTATE` — everyone's `keyState` and aim, no positions
(`GameServerBeginStep.gml:88-95`). The client re-derives every character's
position by running *its own copy of the physics* over those inputs, and is
corrected on the seventh tick by a `QUICK_UPDATE` carrying `x*5`, `y*5`,
`hspeed*8.5`, `vspeed*8.5`.

So the port is not merely producing bytes the client accepts. It is producing a
**trajectory the stock client's GM8 physics independently agrees with**. Where
it disagrees, the client visibly snaps roughly four times a second — on every
client, for every player, and never in a unit test.

That is the whole reason `docs/PARITY.md` exists. Read it before writing physics.

## Contract vs. incidental

The user constraint for this project is: match the client on the wire and in
observable simulation; owe GM8 nothing else. Concretely:

**Contract — must be reproduced:**

- Every byte layout and message cadence in `PROTOCOL.md`.
- The numeric result of the character update: position and velocity per tick,
  to the precision the wire and the client's own prediction can distinguish.
- The *order of operations within* that update, where it changes the number —
  half-gravity before movement and half after is not stylistic, it defines the
  arc.
- Collision geometry: what counts as solid, and where its boundary is.
- Damage, hitscan and projectile outcomes, because clients render them and
  because they change `hp` on the wire.
- Roster indexing and renumbering.

**Incidental — GM8 artefacts, delete them:**

- Objects, events, `with()` blocks, alarms, user events, `instance_deactivate`.
  These are how GM8 expresses control flow; C# has better ones.
- `ds_list` / `ds_grid` / `buffer_*` and the whole GM8 data-structure layer.
- ~~The automatic-movement dance.~~ **This was wrong, and it is measured.** GM8
  adds `hspeed`/`vspeed` to `x`/`y` between the Step and End Step events, so
  `Character.events/Step.xml` ends with `x -= hspeed; y -= vspeed;` to cancel it
  — but subtracting a double and adding it straight back **is not the identity**.
  It loses a ULP whenever the intermediate needs more precision than the result:
  `y = 511.490625` with `vspeed = -2.3000000000000034` comes back as
  `511.49062499999997`, and the reference carries on from that value. Reproduce
  the round trip; see "Settled: the automatic-movement round trip" below.
- The loopback TCP socket the host uses to talk to itself.
- Drawing, sprites, particles, sounds, the HUD, the view/camera.
- ~~Server-side RNG.~~ **Mostly, but not all of it, and the exception is
  weapons.** Spawn-point choice, spawn-team selection, chat bubbles and particle
  emission are free: the server decides and broadcasts the *result*, so the port
  may use any generator it likes for those.

  Weapon fire is the other way round. A weapon's `User Event 1` draws
  `irandom(65535)` and puts **the seed** on the wire
  (`Scripts/Events/sendEventFireWeapon.gml`); `doEventFireWeapon.gml` then runs
  `random_set_seed(seed)` on the server *and on every client*, and each derives
  the shot spread itself. No pellet position is ever transmitted. So GM8's
  generator is shared with client prediction after all, and the port has to be
  the same generator — see "Settled: the random number generator" below, where
  it is measured.

  Two weapons are exempt because the reference exempts them: `Minigun` and
  `Blade` call `randomize()` and fire with no seed and no `sendEventFireWeapon`,
  so their spread genuinely differs on every machine and the server is simply
  authoritative.
- Per-frame recomputation the reference does because it has nowhere to cache.

**Neither, and there is exactly one of them:** a state in which the reference
does not terminate. `good_move_contact_solid`'s crawl can reach a fixed point
where GM8 spins for ever (see "The movement solver" below); the port breaks out
of it. A hang has no observable behaviour to reproduce, so this is not a
divergence — but it is a departure, and it is written down rather than left for
a reader to find in a diff.

**Explicitly free, and worth using:** structure the C# for performance and
testability. Struct-of-arrays for characters, a pure `Step(state, inputs) ->
state` core with no I/O in it, collision as a queryable service. The reference's
shape is an artefact of a 2008 IDE, not a design.

## Fix the tick rate at 30 Hz and most of the complexity evaporates

The reference supports 30 and 60 fps and threads four globals through the
physics to do it (`Documentation/RateController.md`,
`Objects/RateController.events`). A headless server has no reason to run at 60.
Pin these and fold them into the constants:

```
global.delta_factor       = 1
global.skip_delta_factor  = 1/2
global.ticks_per_virtual  = 1
global.frameskip          = 2
global.run_virtual_ticks  = true      // always, at 30fps
```

Every `* global.delta_factor` and `/ global.delta_factor` in the GML then
disappears, every `if (global.run_virtual_ticks)` guard becomes unconditional,
and `repeat(global.frameskip)` becomes a fixed loop of 2. Two helpers survive as
constants:

```
delta_mult(f)       = f                       // f*1 + (1-1)
delta_mult_skip(f)  = f*0.5 + 0.5
```

**Do not "simplify" the frameskip loop away.** Running acceleration and friction
twice per tick with `skip_delta_factor = 0.5` is not the same number as running
them once — that is the entire reason the loop exists
(`Documentation/RateController.md`, "frameskip").

## The character update

Distilled from `Objects/InGameElements/Character.events/{Begin Step, Step, End
Step}.xml` at 30 fps, with GM8 artefacts removed. This is a reading aid, **not a
substitute for reading those three files** — they carry cases this omits
(taunting, sandvich, cloak, uber, humiliation, intel).

Per-class constants (`Objects/Characters/*.events/Create.xml`, defaults in
`Character.events/Create.xml:168-175`):

```
baseControl  = 0.85        (all classes)
baseFriction = 1.15        (all classes)
jumpStrength = 8 + 0.6/2 = 8.3
basemaxspeed = baseRunPower * baseControl / (baseFriction - 1)
             = baseRunPower * 5.6667

class      baseRunPower   maxHp   basemaxspeed   notes
scout          1.4         100       7.933       canDoublejump
soldier        0.9         160       5.100
sniper         0.9         120       5.100
demoman        1.0         120       5.667
medic          1.09        120       6.177
engineer       1.0         120       5.667
heavy          0.8         200       4.533
spy            1.08        100       6.120       canCloak
pyro           1.1         120       6.233
quote          1.07        140       6.063
```

Character collision boxes are **rectangles**, taken from the sprite's manual
mask bounds relative to its origin (`Character.events/Create.xml:5-8`). All ten,
read out of the running game — they do **not** follow a pattern, so do not
extrapolate from one:

```
class      left  right   top  bottom
scout       -6     +6    -10    +23
soldier     -6     +6     -8    +23
sniper      -6     +6     -8    +23
demoman     -7     +7    -10    +23
medic       -7     +7     -8    +23
engineer    -6     +6    -10    +23
heavy       -9     +9    -12    +23
spy         -6     +6    -10    +23
pyro        -7     +7     -6    +23
quote       -7     +7    -12    +11
```

Widths are 6, 7 or 9; tops range from -6 to -12; and Quote is a different shape
altogether, ending at +11 where every other class ends at +23. Red and blue
share a mask on every class. Quote's sprites are named `Querly*`, which is why
they do not turn up under a `<Class><Team>S` search.

Both ends of the box are **inclusive**, and the position is rounded to an
integer before the box is built — see "Settled: `place_free` rounds the
position" below. `Gg2.Simulation.CharacterBox` is the transcription.

### Order of operations

```
--- begin step ---
stuckInWall     = !placeFree(x, y)
obstacleBelow   = !placeFree(x, y+1)
onground        = obstacleBelow, or standing on a DropdownPlatform (unless KEY_DOWN)
if onNonSurfingGround: moveStatus = 0
if onground:           doublejumpUsed = 0

apply afterburn damage
handle jump / doublejump  -> vspeed = -jumpStrength, onground = false
handle attack, special, cloak
pick controlFactor / frictionFactor from moveStatus:
    0 (normal)      control = 0.85 (0.75 with intel, 0.65 humiliated), friction = 1.15
    1 (self rj)     control = 0.65, friction = 1
    2 (enemy rj)    control = 0.45, friction = 1.05
    3 (airblast)    control = 0.35, friction = 1.05
    4 (team rj)     control = 0.85, friction = 1

repeat 2 times:                                    // frameskip
    if LEFT  and hspeed >= -basemaxspeed: hspeed -= runPower*controlFactor*0.5
    if RIGHT and hspeed <=  basemaxspeed: hspeed += runPower*controlFactor*0.5
    if |hspeed| > basemaxspeed*2 or (a direction held and |hspeed| < basemaxspeed):
        hspeed /= delta_mult_skip(baseFriction)      // /1.075
    else:
        hspeed /= delta_mult_skip(frictionFactor)

if |hspeed| < 0.195 and not controlling: hspeed = 0     // anti ice-skating
if not onground and not stuckInWall:
    applied_gravity += rocketJumping ? 0.54 : 0.6

--- step ---
hspeed = clamp(hspeed, -15, 15)                     // per axis, independently
vspeed = clamp(vspeed, -15, 15)
spinjumping check; if spinjumping: applied_gravity -= gravity   // wall-rub boost

vspeed += applied_gravity / 2                       // HALF the gravity, before moving
if vspeed > 10: vspeed = 10

if !placeFree(x + hspeed, y + vspeed):
    characterHitObstacle()                          // the movement solver, below
else:
    x += hspeed;  y += vspeed

if placeFree(x, y+1): vspeed += applied_gravity / 2  // the other half, after moving
if vspeed > 10: vspeed = 10
applied_gravity = 0

dropdown-platform push-out

--- end step ---
climb down stairs: if vspeed == 0 and placeFree(x,y+6) and !placeFree(x,y+7): y += 6
                   (and a 12px variant when speed > 6)
if hp <= 0: death, and EXIT - nothing below this line runs
if hp > maxHp: hp = maxHp
image_xscale (facing) from aimDirection; currentWeapon follows to round(x), round(y)
clamp x,y into [0, mapWidth] x [0, mapHeight]
cloak alpha, taunt index, sandvich, deathmatch invuln decay
```

The split gravity is deliberate and documented in the GML: applying half before
and half after the motion is what makes the arc framerate-independent, and
`jumpStrength = 8 + 0.6/2` compensates for it. Get this wrong and every jump in
the game is subtly the wrong height.

### The movement solver

`Scripts/Physics/characterHitObstacle.gml` runs when the naive move would land
in a wall. Summarised:

1. If already inside a wall, try `move_outside_solid` in all four directions and
   take the shortest escape; if none works, restore the old position.
2. Loop while more than 0.1 px of movement remains (bail after 10 iterations):
   - `good_move_contact_solid(direction, distance)` — slide until contact;
   - if blocked vertically: zero `vspeed` and the remaining vertical movement
     (and reset `moveStatus` if it was a *floor*, not a ceiling);
   - if blocked horizontally: try stepping **up 6 px**, else **down 6 px** when
     `|hspeed| >= |vspeed|`, else zero `hspeed` and remaining horizontal movement;
   - if nothing was rectified and ≥1 px remains, zero vertical movement — the
     falling-onto-a-corner case.

`good_move_contact_solid` (`Scripts/Physics/good_move_contact_solid.gml`) is
**already a pure-GML reimplementation of GM8's `move_contact_solid`**, written
because the built-in was buggy. It crawls forward in `1/8`-pixel steps. That
means it is a specification you transcribe, not behaviour you reverse-engineer —
the single luckiest fact in this port.

⚠️ **It is transcribed with exactly one line added, and that line is the only
deliberate departure in the physics.** The crawl's `while` can reach a fixed
point: when what is left to travel is above GML's `1e-13` comparison fuzz but
below half a ULP of the position — which needs a coordinate in the thousands,
and is why it surfaced on `cp_dirtbowl` — the step rounds away, `totalMoved`
stops growing, and the loop never ends. The port breaks out when a step leaves
both coordinates unchanged. **The reference does not terminate in that state
either**, so nothing observable is being traded away; there is no later frame to
match. Measured, with the numbers, in `docs/TRAPS-SIMULATION.md`, "the crawl
that could not finish".

## The collision world

Far smaller than "reimplement GM8 collision" suggests. Everything solid is one
of:

- **The map walkmask.** One instance of `CustomMapO` (child of `CollisionMapO`),
  `image_xscale = image_yscale = 6`, whose sprite is built at runtime by
  `sprite_add` from the map PNG's embedded walkmask
  (`Scripts/Maps/CustomMaps/CustomMapProcessLevelData.gml:22`). One mask pixel =
  **6 × 6 world pixels**, which is also `NAV_CELL_SIZE`. Sprites added at
  runtime in GM8 use precise (per-pixel) collision.
- **A handful of axis-aligned rectangles**: `Obstacle`, `Gate` (`TeamGate`,
  `IntelGate`), `ControlPointSetupGate`, `PlayerWall`, `PlayerWallHorizontal`,
  `MovingPlatform`/`DropdownPlatform`.

Solidity is **toggled per query, not baked in**. `charSetSolids()` sets `solid`
on the obstacle set — including gates, whose solidity depends on the querying
character's team and whether it is carrying intel — runs the check, then
`charUnsetSolids()` clears it (`Scripts/Physics/charSetSolids.gml`). In C# this
becomes a collision query that takes a filter, which is both faster and clearer
than mutating global state twice per call.

So `placeFree(x, y)` is: *does this character's rectangle, at (x, y), overlap any
set walkmask cell or any enabled solid rectangle?* That is a bitmask lookup over
a handful of cells plus a few AABB tests. It should be nanoseconds, and it is
the single hottest function in the server.

**There is prior art for the mask decode.** `gg2-agent/tools/walkmask.js` already
decodes the `zTXt` "Gang Garrison 2 Level Data" chunk and its `{WALKMASK}`
bitstream with no game running, and `gg2-nav-gen` already runs collision queries
against it offline accurately enough to build nav graphs the live game accepts.
Port that decoder rather than re-deriving the format.

## Float rules for the C# port

- **Use `double` everywhere.** GML has exactly one numeric type and it is an
  IEEE-754 double. A `float` anywhere in the physics is a bug.
- Do not use `MathF`, and do not let anything round to single precision on the
  way in or out.
- `Math.Round` in .NET is banker's rounding; GML `round()` is also
  round-half-to-even, so those agree — but **`floor` on negatives, integer
  division, and `mod` on negatives differ between languages**. GML `mod` follows
  C-style truncation toward zero, as does C# `%`, so those agree; GML has no
  integer type, so `div` is the only truncating operator. Check each use.
- `point_direction` returns degrees in `[0, 360)` measured **counter-clockwise
  with y pointing down** — i.e. `point_direction(x1,y1,x2,y2) =
  atan2(y1-y2, x2-x1)` in degrees, normalised. Getting the sign wrong here
  mirrors the whole game.
- SIMD and parallelism are fine *across* independent characters, but never let a
  vectorised reduction change the order of a floating-point accumulation that
  the client also performs.
- **GM8 does not parse every decimal literal to the nearest double, and GML's
  comparisons are fuzzy.** Both are measured; both change trajectories. See the
  two sections below.

## GM8 semantics that will bite

Collected because each of these has already cost someone real time; several are
recorded in `gg2-agent/GML.md`.

- **Automatic movement.** Described above — GM8 adds velocity to position
  between Step and End Step. `x -= hspeed` at the end of a Step event means "undo
  the engine's move", not "move backwards". It does **not** cancel exactly, so
  both halves have to be reproduced.
- **Event phases are global, not per-instance.** GM8 runs Begin Step for *every*
  instance, then Step for every instance, then the automatic move, then End Step.
  A character's Begin Step therefore sees every other character's *previous*
  end-of-tick state. Preserve the phase split; it is observable.
- **Within a phase, instances run in creation order.** Observable wherever
  instances interact — damage attribution, healing, capture contests. Pin it in
  the parity harness rather than trusting it.
- **`solid` is a mutable per-instance flag**, and `Obstacle.solid` is only true
  during the window `charSetSolids`/`charUnsetSolids` brackets.
- **`place_free` tests the *mask*, not the sprite**, and character masks are
  manual rectangles that do not match the drawn sprite.
- **Alarms count down by exactly 1 per frame**, which is why the reference
  divides initial values by `delta_factor` instead of scaling the decrement. At a
  fixed 30 fps they are plain tick counters. The firing rule is narrower than it
  looks — see "Settled: the alarm stage" below.
- **All four of `hspeed`, `vspeed`, `direction` and `speed` are stored**, and
  writing any of them rewrites the others. This bullet used to say `speed` and
  `direction` were *derived*; that is true only of an instance which never
  writes them, which is every character and no projectile. See "Settled: the
  four motion variables" below.

## What is genuinely uncertain

Be honest about these in the parity harness rather than assuming:

1. **Instance ordering under heavy churn** (projectiles created and destroyed in
   the same tick).

~~2. How an alarm assigned a fractional number of ticks rounds.~~ **Measured
2026-08-27: half to even**, which is what `Timers.SetAlarm` already did. See
"Settled: the alarm stage" for the table.

The one that is left is answerable with `gg2-agent`'s live bridge. Do that
before building on a guess.

### Settled: the collision boundary rule

Was items 1 and 2 on this list. Measured against the running game on
`ctf_truefort` with `collision_point` against `CollisionMapO`, then checked
across **23,040 sampled points on three maps** — the fixture is
`tests/Gg2.Tests/Fixtures/collision-samples.json` and
`tools/sample-collision.js` regenerates it.

- **A mask cell covers `[6c, 6c+6)` on both axes.** World pixel to cell is
  `floor(v / 6)`, half-open at the top. Sweeping x across a known edge, the
  first solid pixel is exactly `6 × 526 = 3156`; sweeping y, exactly
  `6 × 58 = 348`. No off-by-one, and no inclusive edge.
- **The floor must be a floor.** C#'s `/` on integers rounds toward zero, so
  `-1 / 6 == 0` and a pixel left of the map would read cell 0 — which is often
  solid. The game answers *open* for every negative coordinate.
- **Off the mask is open, on every side.** Solid runs to `map_width() - 1` and
  is open from `map_width()` up; likewise open for all negative coordinates.
- **Runtime `sprite_add` sprites are precise.** If the collision sprite were
  not, `collision_point` with `prec = 1` would fall back to its bounding box —
  the whole map — and every probe would answer solid. They do not.

### Settled: `place_free` rounds the position, half to even

The cell rule says where a wall is. This says where the *character* is, and it
was the last thing in the way of a collision implementation — a character is
almost never on an integer coordinate, so a wrong rule here is wrong nearly
always.

**GM8 rounds the instance position to an integer before building the bounding
box it collides with, half to even.** The box then covers
`[round(x) + left, round(x) + right]` **inclusive** on both ends.

Measured by sweeping a real character box toward a wall an eighth of a pixel at
a time. With a Heavy box (right offset +9) and a wall whose first solid pixel is
1572, the threshold is `1572 - 9 = 1563` and the flip falls between
`x = 1562.5` (free) and `x = 1562.625` (blocked). Only half-to-even explains
that: 1562.5 rounds *down* to the even 1562, where half-up would give 1563 and
block. Floor and ceiling are ruled out outright — floor would leave 1565.5 free
for a Scout, ceiling would block 1565.25. Confirmed on both axes and across
three classes whose box widths put the threshold at different parities.

**Three different roundings live in this codebase and they are not the same
one.** Getting them crossed produces a one-unit error that looks exactly like
netcode jitter:

| Where | Rule | In C# |
|---|---|---|
| A real onto the wire (`write_ushort` and friends) | half **away from zero**, then clamp | `Gg2.Protocol.WireQuantise` |
| GML's own `round()` | half to **even** | `Gg2.Protocol.Gml.Round` |
| A position into a collision box | half to **even** | `Gg2.Simulation.CollisionWorld.RoundPosition` |
| A scaled sprite's extent into a bounding box | half to **even** | `Gg2.Simulation.SolidRect.FromEntity` |
| `move_outside_solid`'s allowance into whole pixels | half to **even** | `Gg2.Simulation.Movement.MoveOutsideSolid` |

Only the first is different. Everything GM8 itself rounds is half to even; the
odd one out is the networking extension, which is not GM8 at all.

### Settled: the solid rectangles

Gates and player walls are instances, not mask cells, and their extent comes
from a collider sprite stretched by the entity's scale. There are only four
collider sprites, all plain filled rectangles with their origin at (0, 0):
`sprite44` 60×6 (horizontal bars), `sprite45` 6×60 (vertical), `sprite3` 16×16
(`Obstacle`), `SetupGateS` 32×32.

GM8 works in half-open sprite space, so the covered pixels are

```
bbox_right = round(x + spriteWidth * xscale) - 1
```

with the usual half-to-even round. Checked against every gate and player wall on
three maps — scales in the shipped maps include 0.3, 0.8, 1.7, 2, 3, 7.3 and 13.

Solidity is per-query, from `charSetSolids.gml`:

| Kind | Solid when |
|---|---|
| `Obstacle` (incl. the map itself) | always |
| `PlayerWall`, `PlayerWallHorizontal` | always |
| `TeamGate` | `team != asker.team` **or** the asker carries intel |
| `IntelGate` | `team != asker.team` **and** the asker carries intel |
| `ControlPointSetupGate` | the setup timer is running |

`IntelGateVertical` and `IntelGateHorizontal` carry `TEAM_ANY`, which never
equals a character's team, so they block every intel carrier. The "or intel" on
a team gate is what stops a team walking its own flag out through its own gate.

⚠️ `instance_number` and `instance_find` include an object's **descendants**, so
enumerating `PlayerWall` also returns every `PlayerWallHorizontal`. Anything
counting instances by object needs to de-duplicate by id.

### Settled: `move_outside_solid`

A GM8 built-in, and the one piece of `characterHitObstacle` that is not
transcribable from GML. Measured:

- It steps **whole pixels** and **keeps the fractional part** of where it
  started. From x = 860.25 against a wall clear at 870 it lands on 870.25 — not
  870, and not 869.75.
- The allowance is rounded to whole pixels, half to even: 3.5 gives four steps
  and 2.5 gives two.
- **On failure it still moves the whole allowance**, and leaves the instance
  stuck. This is load-bearing rather than incidental:
  `characterHitObstacle` tries all four directions and takes the *shortest*
  escape, so a failure has to report a *large* distance. If it reported zero it
  would always win, the character would never be pushed anywhere, and the
  reference's own `place_free` re-check would restore it every time.

### Facing does not change the collision box

A character flips `image_xscale` to -1 when facing left
(`Character.events/End Step.xml:55-60`), and GM8 then reports a **wider**
`bbox_left` for it — a scout reads -8 instead of -6. That inflated box is a
broad-phase bound; `place_free` against a precise mask does not use it. Every
class's mask rectangle is symmetric about its origin, so mirroring it changes
nothing, and sweeping a flipped and an unflipped scout into the same wall gives
the same answer to the quarter-pixel. Do not "fix" this by reading `bbox_left`.

### `map_width()` is the art, not the mask

`Scripts/Maps/CustomMaps/map_width.gml` returns
`background_width[7] * background_xscale[7]` — the **background image**, not the
collision sprite. So a map's bounds and its collision geometry come from two
different pictures, and they need not be the same size.

Twenty-one of the twenty-two shipped maps have them equal, which is exactly why
this is worth writing down. `gg_debug` is 120 × 64 of art over a 118 × 69 mask:
its bounds reach twelve world pixels *past* the mask horizontally and stop
thirty *short* of it vertically. `Character.events/End Step.xml` clamps
positions into `map_width()` × `map_height()`, so the physics wants the art's
number — `Gg2.Maps.LevelMap.Width` — and not the walkmask's extent.

### Settled: how a real becomes an integer on the wire

Was on this list; is not any more. Read out of the networking extension's own
source ([Faucet Networking](https://github.com/Medo42/Faucet-Networking-Extension),
`faucet/ReadWritable.hpp` and `faucet/clipped_cast.hpp`):

```cpp
template<typename IntType>
void writeIntValue(double value) {
    IntType converted = clipped_cast<IntType>(round(value));
    ...
}
```

So every `write_ubyte` / `write_byte` / `write_ushort` / `write_short` /
`write_uint` / `write_int` of a fractional real:

1. **rounds half away from zero** — C++ `std::round`, so `2.5 -> 3` and
   `-2.5 -> -3`; and
2. **clamps to the target type's range** rather than wrapping.

Both defaults in C# are the other answer: `Math.Round` is half-to-even and an
integer cast wraps. `Gg2.Protocol.WireQuantise` is the transcription; use it,
and do not spell the conversion out by hand at a call site.

**This is not GML's `round()`**, which *is* half-to-even, and the reference
applies that one to some fields and not others. The two consequences worth
holding on to:

- `Character` writes its position as plain `x*5`
  (`Character.events/User Event 12.xml`), so half-integral coordinates round
  away from zero. `Intelligence` writes `round(x*5)`
  (`Intelligence.events/User Event 10.xml`), so the same coordinate rounds to
  even. **Two objects, two quantisations, one wire.**
- The velocity field is asymmetric. `hspeed` is clamped to ±15 and written as
  `hspeed*8.5` in a signed byte: `+15 -> 127.5 -> 128 -> clamps to 127`, while
  `-15 -> -127.5 -> -128`, which fits. A parity harness that expects ±127 will
  report a phantom mismatch at terminal leftward speed.

### Settled: GM8's decimal literals are not always the nearest double

Measured against the running game on 2026-08-25, by pulling the mantissa out of
a live value a chunk at a time (`tools/sample-physics.js` has the encoder; the
game's own `string_format` is only good to about sixteen significant digits and
the seventeenth is noise, which is useless when the question is a single ULP).

| GML text | GM8 holds | IEEE nearest | |
|---|---|---|---|
| `0.6` | `0.6000000000000001` | `0.6` | **+1 ULP** |
| `0.85` | `0.8500000000000001` | `0.85` | **+1 ULP** |
| `1.15` | `1.1500000000000001` | `1.15` | **+1 ULP** |
| `0.65` | `0.6500000000000001` | `0.65` | **+1 ULP** |
| `0.35` | `0.35000000000000003` | `0.35` | **+1 ULP** |
| `1.75` | `1.7500000000000002` | `1.75` | **+1 ULP** |
| `0.1` `0.2` `0.25` `0.45` `0.5` `0.54` `0.9` `1.05` `1.4` `8.3` `0.195` | same | same | |

`1.75` was added on 2026-08-25 while measuring the RNG, and it is the entry that
kills the tempting theory. `1.75` is **exactly representable** — it is 7/4 — so
this is not a decimal that cannot be written in binary being rounded. GM8 gets a
value one ULP above a number it could have parsed exactly. Whatever the parser
is doing, "is the literal dyadic?" does not predict it, and neither does "how
many decimal places". The only safe rule is the one already in force: **never
write a physics constant as a plain C# decimal; measure it, and take it from
`Gml`.**

Computing the same value instead of writing it — `3/5`, `23/20`, `17/20` — gives
the IEEE-nearest double, so this is the literal parser and not the arithmetic.

Those five are gravity, `baseControl`, `baseFriction`, and two of the rocket
jump's control factors. `Gg2.Protocol.Gml` holds each of them as the C# decimal
that rounds to GM8's double, and `ClassStats` takes them from there.

**One ULP is not cosmetic here, for two reasons.** `basemaxspeed` is
`abs(baseRunPower * baseControl / (baseFriction - 1))`, and that subtraction
throws away almost the whole value: one ULP in `baseFriction` comes out as
**fourteen** in `basemaxspeed` (`7.933333333333326` against
`7.933333333333338` for a scout). And `good_move_contact_solid` backs out of an
obstruction in eighths of a pixel until a position is free, so a difference far
below any sensible epsilon lands on the other side of a boundary and stays there
as a sixteenth of a pixel.

### Settled: GML's comparisons are fuzzy, to 1e-13

`a == b` in GML is true when `|a - b| < 1e-13`, and every other comparison is
built on that. Measured: `1e-13 != 0` is true, `1e-14 != 0` is false,
`sign(1e-14)` is `0`, and `1e6` still differs from `1e6 + 1e-10` — so the epsilon
is **absolute**, not relative. `min()` and `max()` do *not* use it and return the
raw value.

The rules that reproduce every measured case:

| GML | C# |
|---|---|
| `a == b` | `Math.Abs(a - b) < 1e-13` |
| `a > b` | `a > b && !Equal(a, b)` |
| `a >= b` | `a > b \|\| Equal(a, b)` |
| `sign(x)` | `Equal(x, 0) ? 0 : Math.Sign(x)` |

`Gg2.Protocol.Gml` has all of them, and every real-to-real comparison in
`Gg2.Simulation` goes through them.

**What this costs if you skip it.** In `characterHitObstacle`, a crawl that uses
up its whole allowance leaves `hleft` at about `-2.7e-14` through ordinary
cancellation. GML calls that zero and skips the wall check entirely. C#'s
`hleft != 0` takes it, finds a wall, and slides the character **six pixels** down
a sloping ceiling — one tick of input turning into a visible teleport, from a
difference of 3e-14.

⚠️ **What it costs when you do reproduce it.** The epsilon is *absolute*, so it
does not scale with the numbers it is comparing, and a ULP of a coordinate in the
thousands is `4.5e-13` — bigger than the epsilon. A distance that is
"still remaining" by GML's rule can therefore be too small for the position to
represent, which is a loop that never finishes. That is a real hang, it is in the
reference too, and it is the subject of `docs/TRAPS-SIMULATION.md`, "the crawl
that could not finish".

### Settled: the automatic-movement round trip

`Character.events/Step.xml` ends with `x -= hspeed; y -= vspeed;`, and GM8 adds
both back before End Step. That pair is **not** a no-op: `(y - v) + v` differs
from `y` by a ULP whenever the intermediate needs more precision. Reproduce it at
the end of the Step phase, using `hspeed`/`vspeed` as of that point — after the
second half of gravity and the terminal clamp, not the values the move used.

### Settled: `floor()` truncates through a 32-bit integer

GM8's `floor` wraps above 2^31: `floor(5153960755.2)` returns `858993459`. It
does not come up in the physics, where every floored value is a screen
coordinate, but it does come up in any tool that pulls bits out of a double, and
it fails silently.

### Settled: the character update matches the engine exactly

`tests/Gg2.Tests/Fixtures/physics-samples.json` is 64 whole trajectories — four
classes (Scout, Soldier, Pyro, Heavy), two maps, 48 ticks each, **32 of them
started from rest** — driven inside the running game from start states chosen
exactly, with position and velocity read back to the last bit. (It was 48 across
three classes until 2026-08-28, when the Soldier and the rest starts were added
to put item 1's residue-bearing state under the oracle.) The
port reproduces every one of them **bit for bit**. Regenerate with
`node tools/sample-physics.js` against a running dedicated server with bots.

This is the gate phase 4 is actually held to, and it exists because a recorded
trace cannot do the job: the wire carries position to a fifth of a pixel and
velocity to `1/8.5` px/tick, so a closed-loop replay of a *mid-game* recording
starts inside those bins rather than on the reference's own state, and a residual
of one wire unit cannot be attributed to either side.

That is not a guess. Driving the reference engine's own `Character` events
through `reference-ctf_truefort-bots.trace`'s recorded inputs, from the same
wire-quantised anchor the replayer uses, the engine lands on
`y = 516.6899190010863` entering tick 218 — wire **2583**, the port's answer, and
one unit away from what its own recording said at that tick. Over the same 201
ticks the port and the engine agree on `x`, `y`, `hspeed` and `vspeed` to the
last bit on every one. The engine disagrees with its own recording by exactly
the amount the replay does, for exactly the same reason.

### Settled: the random number generator

Measured on 2026-08-25, and the answer is short: **Game Maker 8 uses Delphi's
`Random`**, which is what you would expect of a Delphi program.

```
seed'      = (seed * 0x08088405 + 1)  mod 2^32       // 134775813, Borland's
random(x)  = x * seed' / 2^32
irandom(n) = floor((n + 1) * seed' / 2^32)           // n is INCLUSIVE
choose(..) = argument[floor(count * seed' / 2^32)]   // exactly one advance
```

`Gg2.Protocol.GmlRandom` is the transcription and
`tests/Gg2.Tests/Fixtures/rng-samples.json` is the evidence — 15 seeds × 8
advances of the chain, 225 `random(x)` values compared **bit for bit** through
the mantissa encoder, and every `irandom`/`choose` draw. Regenerate with
`node tools/sample-rng.js`. Four properties are worth stating because each one
could have gone the other way:

- **The seed advances before the value is taken.** `random_set_seed(s)` followed
  by `random_get_seed()` gives back `s`, but the first draw does not use `s` —
  it uses the seed after one step.
- **The arithmetic is exact.** Dividing by 2^32 is a power-of-two scale, and
  every product GG2 asks for stays under 2^53, so `x * u / 2^32` and
  `x * (u / 2^32)` are the same double. All 210 integer-argument samples agree
  under both orders; there is no rounding rule to get wrong here.
- **`choose()` costs exactly one advance**, the same as one `random()`. This
  matters more than which value it returns: `Shot`'s Create draws a
  `choose(0, 0, 1)` *inside the seeded window* between one pellet and the next,
  so a `choose` that stepped the seed a different number of times would leave
  every pellet after the first somewhere else.
- **`irandom`'s ceiling is inclusive**, so `irandom(65535)` — the weapon fire
  seed — really does span the whole `ushort` range.

The reference reads the seed back through GML's one numeric type, so a seed
above 2^31 prints negative. That is the *display*, not the value; the tool
unsigns it on the way into the fixture.

⚠️ Do not conclude from this that all GM8 randomness is now reproducible and
therefore contract. It is reproducible; it is only **contract** where the seed is
on the wire, which is weapon fire and nothing else so far. Everything in the
"Incidental" list above stays free, and reproducing it there would be work that
buys nothing.

### Settled: `distance_to_object`, `place_meeting`, and the one place facing matters

Phase 3 pinned `place_free` and stopped there on purpose. Combat needs two more
primitives, and both were measured on 2026-08-25 against the running game —
`tools/sample-primitives.js` regenerates
`tests/Gg2.Tests/Fixtures/primitive-samples.json`, which is a grid of offsets of
a real `Rocket` and a real `Shot` around a real character, **with both bounding
boxes recorded per sample** rather than assumed.

```
place_meeting(a, b)      = inclusive AABB overlap; touching counts
distance_to_object(a, b) = sqrt(gapx^2 + gapy^2)
    gapx = max(0, a.left - b.right, b.left - a.right)
    gapy = max(0, a.top  - b.bottom, b.top  - a.bottom)
```

Both exact on all 722 samples, with no rounding rule and no epsilon.
`Gg2.Simulation.Aabb` is the transcription.

**Two things that are not obvious and cost real accuracy:**

**1. `distance_to_object` uses the facing-inflated bounding box. `place_meeting`
does not.** This document already records that a character facing left gets
`image_xscale = -1` and that GM8 then reports a `bbox_left` two pixels wider
than the mask — and says, correctly, that `place_free` ignores it. It does. But
`distance_to_object` is a *bounding-box* primitive and uses exactly the box GM8
reports, inflation included. Measured as a direct A/B, same character, same
coordinates, same tick, one rocket at 2979..2986:

```
image_xscale =  1   box 2994..3006   distance_to_object = 8
image_xscale = -1   box 2992..3006   distance_to_object = 6
```

`Rocket.events/User Event 5.xml` gates a splash hit on
`distance_to_object(other) < other.blastRadius` and then scales the damage by
`1 - distance/blastRadius`, so **splash damage in GG2 depends on which way the
victim is facing**. It is not a rounding artefact and it is not cosmetic: two
pixels out of a 65-pixel blast radius is about 3% of a rocket's splash. Pass the
facing when computing a splash distance; leave it out when testing an overlap.

The inflation is `-2` on the left for every class, because every class's mask is
symmetric about its origin. Derived from the sprite metadata — with
`image_xscale = -1` GM8 computes `bbox_left = x - (spriteBboxRight - xoffset) - 2`
and `bbox_right = x - (spriteBboxLeft - xoffset)` — and now **measured on six
classes**: Scout, Soldier, Medic, Engineer, Pyro, and the two the derivation was
least safe for, Heavy (the widest box at 19 px) and Quote (the one that ends at
+11 where every other class ends at +23). All six shift `bbox_left` by exactly
-2 and leave the other three sides alone.

**2. Do not use .NET's `double.Hypot`.** It rescales its arguments to avoid
overflow, and that changes the last bit. Against the same fixture,
`sqrt(x*x + y*y)` matches the engine on all 722 samples and `double.Hypot`
matches 636. GML's `point_distance` is the naive form; write the naive form.

### Settled: `collision_line`

Was the last unmeasured primitive and the one the hitscan waits on. Measured
across **13,702 segments** on `ctf_truefort` —
`tests/Gg2.Tests/Fixtures/collision-line-samples.json`, regenerate with
`node tools/sample-collision-line.js`. `Gg2.Simulation.CollisionLine` reproduces
every one of them, exactly.

**It is not a segment-versus-rectangle intersection, and writing one gives wrong
answers.** GM8 clips, rounds, and then walks:

```
1. reject unless the segment's bounding box overlaps
   [bbox_left, bbox_right + 1) x [bbox_top, bbox_bottom + 1)
2. order the endpoints by x, and clip the segment to that horizontal span
   — horizontally only; there is no vertical clip
3. reject if both clipped endpoints are above bbox_top, or both at or below
   bbox_bottom + 1
4. round the clipped endpoints and the instance position to integers,
   half to even
5. step whole pixels along whichever rounded axis is longer (ties go to x),
   and for each step look up
       floor((px - round(inst.x)) / image_xscale) + sprite origin
   in the mask
```

The consequences, each of which decides an answer somewhere:

- **The box is half-open, and only at the high end.** A segment reaches an
  instance over `[bbox_left, bbox_right + 1)`. Combined with the half-to-even
  rounding in step 4, a coordinate ending in `.5` gets in or does not
  **depending on the parity of the pixel it rounds to**: against a one-pixel box
  at x = 3005, x = 3005.5 misses; against one at 3006, x = 3006.5 hits. Both
  parities are in the fixture for exactly this reason.
- **Rounding before walking makes it inexact, and asymmetrically so.** A line
  can register against a box it never geometrically enters, by up to half a
  pixel. Sweeping a 45° line across a 1x1 box at eighth-pixel offsets, the band
  of offsets that hit is a full pixel wide going down-right and **half** a pixel
  wide going down-left. No exact intersection test reproduces that, and three
  plausible ones were tried and discarded before the walk was found.
- **`image_xscale` is divided by, not just compared against.** A character
  facing left has `image_xscale = -1`, and the division flips which end of the
  acceptance interval is open. This is a second, independent way facing changes
  a combat answer — the first being `distance_to_object` above.
- **`prec` does not gate on the sprite's mask shape.** GG2 passes `prec = 1`
  everywhere (`Rifle.events/User Event 3.xml`,
  `collision_line_bulletblocking.gml`), so the walk runs even for the
  rectangle-masked colliders, where the mask lookup is just the bounds test.
  The one mask that is genuinely per-pixel is the map's, and it is the one the
  Rifle asks about most.

The map is reached through `Obstacle`: `CustomMapO` descends from
`CollisionMapO` which descends from `Obstacle`, which is why
`collision_line_bulletblocking`'s first line stops a bullet at a wall without
ever naming the map. Its instance sits at (0, 0) with
`image_xscale = image_yscale = 6` and origin (0, 0), so the mask index is
`floor(x / 6)` — the same cell arithmetic as `CollisionWorld.CellOf`.

#### What this means for shooting a character

`LineCollider.ForCharacter` builds the collider, and two things about it are
measured rather than assumed — against live Scout, Heavy, Quote and Soldier bots
at both facings, 4,688 of the segments above.

- **The two pixels `image_xscale = -1` adds to `bbox_left` are not reachable.**
  They are in the box the engine reports and the broad-phase check lets them
  through; the mask lookup then rejects them, because the mask itself was never
  widened. A Heavy at x = 3000 facing left reports `bbox_left = 2989`, and a
  vertical line at 2989 or at 2990 misses it. **Sizing a hitscan off `bbox_left`
  makes a left-facing character two pixels wider to shoot at than the engine
  makes it** — the opposite of the `distance_to_object` case above, where the
  inflated box is exactly what the engine uses. The two combat primitives
  disagree about the same character, and both are right.
- **Facing flips which end of the span is open**, and it takes a diagonal to
  see. `[left, right + 1)` facing right against `(left - 1, right]` facing left.
  On an axis-aligned line the walk rounds its endpoints first and both spans
  hold the same integers; on a diagonal the minor axis interpolates to a
  fraction, and a sample at x = 3009.5 floors to mask column 9 facing right and
  to -10 facing left. Measured both ways round on the same character in the same
  tick.

The sprite origin does not need to be carried. The mask bounds are given
relative to it, so it cancels out of the only comparison it enters — which is
what lets the collider be built from `CharacterBox`, whose numbers are already
`sprite_get_bbox_* - sprite_get_*offset`.

And a character's box does not change with what it is doing: a class sets
`sprite_index` once in its Create event, and `Character.events/Draw.xml` picks
the animation frame into a *local* to draw rather than reassigning it. Checked
live across four classes.

The structure of `CollisionLine` follows GM8's, which this document otherwise
tells you not to do. It is transcribed because **the walk is the arithmetic**:
the order of clip, round and step decides the answer and there is no closed form
that reproduces it.

### Settled: the four motion variables

Was the bullet in "Other engine behaviours to preserve" that called `speed` and
`direction` *derived*. They are not derived; **all four of `hspeed`, `vspeed`,
`direction` and `speed` are stored on the instance**, and writing any of them
rewrites the others. `Gg2.Simulation.Motion`.

The old wording was harmless for phase 4 because no character ever writes
`direction` or `speed` — but a projectile does. `Scattergun.events/User Event
3.xml` sets `direction` and `speed` through `createShot`, then adds the owner's
`hspeed`, then adjusts `speed`, then adjusts `direction` — four writes that
round-trip between the two representations three times before the shot moves.

```
set direction:  direction = direction mod 360 (never negative), then
                hspeed = snap(cos(rad(direction)) * speed)
                vspeed = snap(-sin(rad(direction)) * speed)
set speed:      speed = v, then the same two lines

set hspeed:     NO-OP if unchanged; otherwise
                direction = snap(deg(atan2(-vspeed, hspeed)) mod 360)
                speed     = snap(sqrt(hspeed^2 + vspeed^2))
set vspeed:     the same

snap(v)         = round(v) if |round(v) - v| < 1e-4, else v      -- round is
                  GM8's own half-to-even, though a 1e-4 window can never reach
                  a tie
```

Four of those details change results rather than last digits:

- **The snap.** `cos(90 degrees)` is 6.1e-17, so without it a shot fired
  straight up carries a residue in `hspeed` forever. The snap window is 1e-4,
  which is a *billion* times wider than `Gml.Epsilon` — do not confuse the two.
- **A *recomputed* value snaps; a value written straight in does not.**
  `speed = 3.00005` stores 3.00005 and gives `hspeed` exactly 3.
  `hspeed = 3.00005` stores 3.00005 and gives `speed` exactly 3. Both measured.
- **Writing a component the value it already holds is a no-op**, so
  `hspeed = hspeed` does not collapse a direction that a zero speed can no
  longer encode. An instance brought to rest still knows where it was pointing.
- **`motion_add` does not snap.** It goes through the *component* path
  (`instance.set_hvspeed(hspeed + dx, vspeed + dy)`), so a knockback straight up
  leaves the 2.4e-16 that an identical-looking `direction = 90; speed = 4;`
  would have cleaned away. Small enough for `Gml.Equal` to absorb, big enough to
  be worth not "fixing". Note the asymmetry with the line above: the residue
  survives in the components and is snapped out of `speed`.

**Status: measured.** `tools/sample-motion.js`, 399 write sequences and 27
angles against the running game, replayed by
`tests/Gg2.Tests/MotionSampleTests.cs`. The starting point was OpenGMK's
`gm8emulator/src/instance.rs`, and the measurement moved it: **the transcription
snapped `direction` and not `speed`, and the engine snaps both.** That is a
1e-4-sized correction, not a last-bit one.

#### `lengthdir_x` and `lengthdir_y` snap; `cos`, `sin` and the rest do not

The same 1e-4 rule, applied by the same engine helper, and it is easy to miss
because it lives inside two functions that look like plain trig:

| Snaps | Does not snap |
|---|---|
| `lengthdir_x`, `lengthdir_y` | `cos`, `sin`, `sqrt`, `abs` |
| `hspeed`, `vspeed`, `direction`, `speed` when **recomputed** | `point_distance`, `point_direction`, `radtodeg(arctan2(...))` |
| | any of the four written **directly** |

Measured: `lengthdir_x(1, 89.999)` is **0** where `1*cos(degtorad(89.999))` is
1.7e-5, and `lengthdir_y(15, 89.999)` is **-15** where the product is
-14.999999998. Each scattergun pellet is nudged fifteen pixels along its own
direction with `lengthdir` before it flies, so this is on the live path.
`Gg2.Protocol.Gml.LengthDirX`/`LengthDirY` are the ones to call — never a bare
`Math.Cos(...) * length`.

#### ⚠️ This is the one primitive that is not bit-exact, and it cannot be made so

GM8 is a Delphi program. Its motion conversions run on the **x87 stack at 80-bit
extended precision**, using Intel's `FSIN`/`FCOS` and their 66-bit argument
reduction; the port runs on IEEE doubles. The difference is visible three ways,
all measured:

- `cos(degtorad(90))` is **6.123031769111886e-17** in the game and
  6.123233995736766e-17 in .NET. The gap, 2e-20 per quadrant of argument
  reduction, is x87's internal pi being 66 bits.
- `degtorad(30)` is one ULP **above** `30 * (pi/180)` in double arithmetic,
  because the engine multiplies by an 80-bit pi.
- The engine's *internal* `update_hvspeed` is not `cos(degtorad(d)) * speed`
  either — it keeps the whole chain in registers and rounds once at the store,
  so it lands up to 1e-14 away from any two-step double computation.

Eight spellings of degrees-to-radians were measured against 486 components —
`d*(pi/180)`, `(d*pi)/180`, `(d/180)*pi`, `d/(180/pi)`, a double-double
correction, and others — and **none reproduced the engine**. The best of them is
the obvious one, which is what `Motion` uses.

What that costs, measured over 1,197 sampled values:

| | |
|---|---|
| components and `speed` bit-exact | 87% |
| worst residual | **1.5e-15 per unit of speed** (about 7 machine epsilons) |
| `direction` bit-exact | 95% |
| worst residual | 1.1e-13 degrees |

And why it is safe to build on: GML's own comparison operators are fuzzy to
**1e-13**, so the reference itself cannot distinguish a difference this small; a
projectile lives at most 40 ticks, so under 1e-12 of a pixel accumulates over
its whole life; positions reach the wire quantised to a fifth of a pixel; and
collision runs on integer bounding boxes. The part that *is* discrete — the
snap — is exact, and `MotionSampleTests` asserts it exactly on rows aimed along
an axis, where `cos` is exactly 1 and no trig error can reach them.

Do not try to close the last bit. Three intersection tests were tried and
discarded before `collision_line`'s walk was found; this is the same shape of
trap, and here the answer is that the gap is in the FPU rather than in the
formula.

**One trap that cost this session an hour**, and which turned out to have two
more inside it. The mantissa encoder was copy-pasted into four samplers and
**three of the four copies were wrong**. It is one file now,
`tools/exact-double.js`, and `node tools/exact-double.js` self-tests it without
a game — see "Settled: reading a GM8 double back to the last bit" below.

### Settled: a rotated sprite's bounding box

`Gg2.Simulation.Aabb.ForRotatedSprite`, regenerate with
`node tools/sample-rotated-box.js`. Phase 4 never needed it and phase 5 cannot
start without it.

A character's `image_angle` is always 0, so its box is a fixed rectangle offset
by its position. **Every projectile is different**: `createShot.gml` does
`image_angle = dir` on whatever it creates, and GM8 reports the axis-aligned
bound of the *rotated* sprite. A scattergun pellet fired flat has an 18x2 box;
the same pellet at 45 degrees has a 13x13 one. That box is what every hit is
decided against, so the angle decides who a pellet can reach.

```
corners = the SPRITE's own bbox, relative to the origin, times the scale
rotate each about the origin:
    x' =  dx*cos(a) + dy*sin(a)
    y' = -dx*sin(a) + dy*cos(a)         -- y down, angles counter-clockwise
bbox = round(min x'), round(min y'), round(max x'), round(max y')
                                        -- half to EVEN, each edge on its own
```

Two things it is easy to get wrong:

- It rotates the **sprite's bbox**, not the sprite's full rectangle. `ShotS` is
  20x6 with a bbox of (2, 2, 19, 3) and an origin at (19, 4), and at angle 0 the
  instance box is exactly that bbox offset by the origin — 18x2 and not 20x6.
- **Each edge is rounded on its own**, after the min/max. It is not a rounded
  position plus a rounded extent, and the two differ wherever the roundings
  disagree.

Measured over 1,008 boxes — `Shot`, `Rocket`, `Needle`, `Flare`, `Mine`,
`Flame`, `BladeB`, 144 angles each. **1,002 exact.**

⚠️ **The six that are not are the one place the x87 gap becomes visible.**
Rounding is a discontinuity, so a corner that lands on a precise half pixel has
its tie broken by the last bit of `sin` — and that last bit is the one thing the
port cannot reproduce (see "the four motion variables" above). All six are 210
and 330 degrees, where `sin` is analytically exactly -0.5, on the three sprites
whose box is asymmetric. Everywhere else the 1e-15 difference is nowhere near a
tie and the boxes agree exactly.

It is unreachable in play: the scattergun adds `random(15)-7.5` to every
pellet's direction, so a direction of exactly 210.000 has probability zero.
**Do not add a fudge factor for it** — a fudge would be wrong at every angle
instead of at two. `RotatedBoxSampleTests` grants the one-pixel exemption only
on rows where the model itself says a corner is on a half, so it cannot quietly
cover an ordinary mistake.

### Settled: the alarm stage

`Gg2.Simulation.Timers`, from OpenGMK's `run_alarms`. Two parts, both narrower
than the obvious reading:

**The firing rule.** Per alarm, per frame:

```
if alarm >= 0 { alarm -= 1; if alarm == 0 { run the event } }
```

- An alarm set to `n` fires `n` ticks later, not `n+1`.
- An alarm set to **0 never fires at all** — it goes straight to -1.
  `Character.events/Create.xml` writes `alarm[1] = 0`, and that is how GM8
  spells "off", not "fire immediately".
- After firing it rests at **0**, not -1, and is consumed on the following tick.
  That is what makes it fire once.

✅ **Measured 2026-08-27** against the running game, and the pseudocode above is
exactly right. `tools/probes/probe-alarm-image.js`; the witness is a `StabAnim`,
whose Create sets `alarm[0] = 10` and whose `Alarm 0` sets `image_speed = 1`, so
the frame the alarm fires is visible without any injected code. Watching
`alarm[0]` and `image_speed` from `AgentBridge`'s Step event — that is, during
the Step stage, after that frame's alarm stage — the counter reads
`9, 8, 7, 6, 5, 4, 3, 2, 1, 0, -1` on successive frames and `image_speed`
becomes 1 on the frame the counter reads **0**.

⚠️ **`Gg2.Simulation.Timers.Advance` writes -1 at the moment of firing, and the
engine does not.** That is a real one-tick divergence, and it has exactly one
observable site — the one the change was made for.
`Character.events/Begin Step.xml:67` gates the afterburn intensity fade on
`alarm[0] == -1` *exactly*, and Begin Step runs **before** the alarm stage:

| | fires at tick T | Begin Step T+1 sees | Begin Step T+2 sees |
|---|---|---|---|
| reference | alarm stage of T, value left at 0 | `0` | `-1` |
| port today | alarm stage of T, value forced to -1 | `-1` | `-1` |

So the port starts every burn's intensity decay **one tick early**. The fix is
to delete the `timer = Inactive;` line from `Advance` and let the following
tick's decrement carry the counter from 0 to -1 — which is what the pseudocode
above already says. It is owed to whichever lane owns `src/Gg2.Simulation`; the
measurement lane found it and does not apply it.

⚠️ **GM8 does not count down an alarm index the object has no event for.**
Measured the same day: an `AgentSpare0` given `alarm[1] = 4` still read 4 sixty
frames later. This is why `Character.events/Alarm 0.xml` exists at all — its
entire body is the comment `Afterburn intensity stuff`, and that empty event is
**load-bearing**, because without it `alarm[0]` would never reach -1 and the
afterburn intensity would never fade. Do not "tidy" an empty alarm event away
when reading the reference, and do not expect a bare probe instance to count.

✅ **How a fractional alarm rounds is measured too**, and it is **half to even** —
GML's own `round()`, which is what `Timers.SetAlarm` already used. Assigning and
reading back in the same frame: `0.5 → 0`, `1.5 → 2`, `2.5 → 2`, `3.5 → 4`,
`30.5 → 30`, `31.5 → 32`, `17.5 → 18`, `26.25 → 26`, `26.75 → 27`, `30.4 → 30`.
The rounding happens at assignment, on an object with or without the matching
alarm event. `TimerTests.AFractionalAlarmBecomesAWholeNumberOfTicks` carries the
table. This was item 2 of "What is genuinely uncertain" and is no longer.

**The order is alarm-index major.** `run_alarms` loops `for alarm_id in 0..=11`
on the outside and over instances on the inside, so *every* instance's alarm 0
fires before *any* instance's alarm 1 — not one instance's alarms in order and
then the next instance's. Nothing in the port crosses instances from an alarm
yet; it will stop being invisible the moment an expiry damages somebody.

The stage sits between Begin Step and Step, which is where
`MatchSimulation.Tick` calls it. That matters for a weapon, and **in the
opposite direction to the obvious reading**: a weapon fires in *Begin Step*,
which runs before this stage, so an input arriving this tick is tested against
the refire counter **last tick left behind** and the decrement that may re-arm
the weapon happens afterwards. This document said the reverse until phase 5
stage 3.

An alarm assigned a *real* is stored as a whole number; see "What is genuinely
uncertain" for the part of that which is not measured.

### Settled: GM8 advances `image_index` after the whole Step stage

Measured 2026-08-27, `tools/probes/probe-alarm-image.js`, in the same trace as
the alarm rule above.

**A Step event that reads `image_index` sees the value the end of the *previous*
frame left.** The witness: a `StabAnim` is created with `image_speed = 0` and
`alarm[0] = 10`; on the frame the alarm fires it sets `image_speed = 1`, and on
that same frame `image_index` still reads **0** during the Step stage. The first
`1` appears on the next frame.

This is the last unmeasured tick of the spy's freeze, and it settles it in
favour of the number already in the port:

- `StabAnim.events/Step.xml` latches `done` on `image_index >= 32`. With the
  advance after the Step stage, `image_index` on the animation's *k*-th Step
  after the alarm is `k`, so the threshold lands on its **42nd** tick, not its
  41st.
- Its `alpha` then falls 0.1 a tick from 1 and the instance destroys itself on
  the **tenth** subtraction, because 1 - 10x0.1 is 1.4e-16 and GML's `>` is fuzzy
  to 1e-13 — the rocket's fade again.
- 42 + 10 = **52**, which is `Weapons.StabFreezeTicks`. ✅ Confirmed end to end
  in the same run: the owner's `stabbing` went back to 0 when the `StabAnim`
  destroyed itself.

⚠️ **The read point is what makes any of this measurable.** `gg2_eval` and
`gg2_watch` are both serviced from `AgentBridge`'s **Step** event
(`payload/Scripts/AgentBridge/agentBridgeStep.gml` calls `agentBridgeWatchTick`
at the top), so a watch samples during the Step stage — after that frame's alarm
stage and before whatever the engine does at the end of the frame. A probe that
does not know where in the frame it is reading cannot answer a question of this
shape at all; three designs were discarded before that was noticed.

### Settled: where a shot comes from, and where its box points

Phase 5 stage 3, transcribed from `Objects/Weapons/Scattergun.events/{User Event
1, User Event 3}.xml`, `Objects/Projectiles/Shot.events/*` and
`Scripts/Object Creation/createShot.gml`. All of it is confirmed by an exact
898-tick closed-loop replay of `reference-ctf_truefort-bots.trace`, which
contains six scattergun blasts.

**A weapon is a separate instance that trails its owner, and a shot leaves from
*it*.** `Character.events/End Step.xml:64-65` is the only thing that writes the
weapon's position, as `round(x)` and `round(y)` — GML's round, half to even. A
weapon fires in Begin Step. So the muzzle is the character's position **at the
end of the previous tick, rounded to a whole pixel**: up to half a pixel and one
tick of travel away from where the character is when the trigger is pulled.
Using the character's own coordinates instead moves every pellet.

**`image_angle` is the aim for one stage of one tick, and then it is the flight
path.** `createShot` sets both `image_angle` and `direction` to
`owner.aimDirection`, and the Scattergun scatters `direction` alone — but
`move_all_bullets.gml` assigns `image_angle = direction` on every pass including
the pellet's first, and that pass runs in the Step stage while the pellet was
created in Begin Step. So the aim angle is gone before any collision event can
look at the box, and **each pellet of a blast carries its own box angle**.

⚠️ **This document said the opposite for three phases** — that a blast's six
pellets share one box shape — and the claim was wrong for the reason the whole
section below is: it was read off `Objects/Projectiles/Shot.events/`, which has
no Step event, without reading the script that does the work.

**The seeded window costs exactly three RNG advances per pellet**, in this
order:

1. `choose(0, 0, 1)` in `Shot.events/Create.xml` — `instance_create` runs the
   Create event before `createShot`'s own `with` block, so this comes first.
2. `random(4) - 2`, added to `speed`.
3. `random(15) - 7.5`, added to `direction`.

Every client re-derives the blast from the seed on the wire, so the *count* is
as much a contract as the use. `perseverant` is stored and read by nothing until
Quote's bubbles land — dropping the draw because of that would leave pellet 1
correct and desync every pellet after it.

**The motion round trip is three writes and cannot be collapsed.** A pellet is
created with `direction`, then `speed`, then has `hspeed` added (a *component*
write), then `speed` adjusted, then `direction` adjusted. Each round trip
re-derives the other representation and snaps it — see "Settled: the four motion
variables" — so writing one polar assignment instead gives different last bits
and, near an axis, a different answer. The fifteen-pixel forward nudge uses
`lengthdir_x`/`lengthdir_y`, which snap; a bare `cos * 15` does not.

### Settled: the projectile pass sees the world as it was last tick

⚠️ **`move_all_bullets` runs before anything has moved, and a blast that goes off
inside it reads every character's *end-of-previous-tick* position.**

The chain, all of it in the reference:

1. `move_all_bullets` is called from
   `Objects/Networking/GameServer.events/Step.xml:16` — a **Step** event, the
   same stage the characters' Step events run in.
2. GM8's automatic-movement pass comes **after** every Step event.
3. `Character.events/Step.xml` deliberately subtracts its own `hspeed` and
   `vspeed` back off at the end, so that pass can re-add them (see "Settled: the
   automatic-movement round trip").

So whichever order the two Step events happen to run in, no character has
committed this tick's movement while the projectiles are being walked. **This
also retires the long-standing question of whether `GameServer`'s Step runs
before a `Character`'s: for this purpose it does not matter.**

`Gg2.Simulation.MatchSimulation` therefore runs `Projectiles.Move` **before** the
`CharacterPhysics.Step` loop. The port commits a character's move inside `Step`
rather than subtracting it back, so running the projectiles first is the only way
to show them the same world the engine shows them.

**Measured.** On `reference-ctf_truefort-closedcombat.trace` a soldier
rocket-jumps on the same tick they jump — `explodeImmediately` is set, so the
rocket goes off at the muzzle inside the movement pass. With the pass *after* the
character move, the character had already risen eight pixels, the blast landed
four pixels outside its box, and it dealt `30 × (1 - 4/65) = 28.15385` instead of
30. With the pass first the damage is exactly 30, and the fixture's exact window
goes from 336 ticks to 357.

⚠️ **The collision events are the other way round.** They run *after* the
automatic-movement pass, so `Projectiles.Collide` sees both sides at their new
positions. A blast reads last tick's world; a direct hit reads this tick's. They
are different stages and the port keeps them apart.

### Settled: a projectile's per-tick behaviour is not in its object

⚠️ **`Scripts/Physics/System/move_all_bullets.gml` is where a projectile lives**,
and it is reached from `Objects/Networking/GameServer.events/Step.xml:16` — the
networking controller's Step event, not the projectile's. `Shot` genuinely has
no Step event; `Objects/Projectiles/Shot.events/` is a Create, an Alarm 0 and
four collision events. Reading only the object is how this repo concluded for
three phases that a pellet had no gravity, no wall test and a fixed
`image_angle`, and shipped `Projectiles.Move` as two additions. All three
conclusions were wrong.

The script is one `with` block per object and they are **not**
interchangeable: a `Shot` falls, a `Rocket` accelerates and drags and tracks a
travel budget, a `Mine` substeps in six-pixel hops and sticks to walls, a
`BurningProjectile` substeps without sticking. `Gg2.Simulation.Projectiles.Move`
dispatches on the kind for that reason.

**`with(Shot)`, in order:**

1. `vspeed += 0.15` — a quarter of a character's gravity, applied **before** the
   move, so the first tick of flight already carries a tick of drop. It goes
   through the *component* path, so it rewrites `direction` and `speed` too (see
   "Settled: the four motion variables") — which matters because the knockback a
   hit deals is `motion_add(shot.direction, shot.speed*0.03)`.
2. `image_angle = direction` — see above.
3. `if(firststep) colliding |= !place_free(x, y)` — the **muzzle** test, once
   only. This is what the Scattergun's fifteen-pixel forward nudge exists to
   defend against; its own comment says so.
4. `x += hspeed; y += vspeed;` then `colliding |= !place_free(x, y)` — the
   **destination** test. A pellet that flies into a wall is destroyed there.
5. `x -= hspeed; y -= vspeed;` — *not* a retreat. The move above was a
   look-ahead so `place_free` could be asked about the destination; GM8's
   automatic-movement pass, which runs after every Step event, performs the real
   one. The port moves once and keeps it, which lands on the same coordinates.
   This is **not** an instance of the "automatic-movement round trip" ULP loss
   below: there the engine adds back a value the reference already stored,
   whereas here the subtraction and the addition are the same value in the same
   tick.

Both `place_free`s are bracketed by `gunSetSolids`, so they use the *bullet's*
solids — `CollisionFilter.ForBullet()`.

**A pellet that reaches a character *through* a wall still deals no damage**, and
that part was always right: the first line of `Shot.events/Collision with
Character.xml` is its own `place_free`. What is new is that the pellet usually
never gets that far, because step 4 already killed it.

⚠️ **None of this is visible to a state-update comparison.** A `Shot` is never
serialised, so its trajectory is observable only where it changes whether
somebody is hit — and `reference-ctf_truefort-bots.trace` replays closed-loop
with zero error either way. `tests/Gg2.Tests/ProjectileMoveTests.cs` is the gate;
each of its rules was checked by deleting the corresponding line and watching
only its own tests go red while the trace stayed green. Absence of a red test is
not evidence.

**Measured: an instance created in Begin Step moves in the same frame.** GM8's
automatic-movement pass walks the instances present when it runs, not the ones
present when the frame started. Delaying a pellet's first move by one tick and
replaying the bot trace closed-loop diverges at tick 393: the same six pellets
land, a tick late, and both scouts' `hspeed` at the next broadcast is wrong by
the knockback. With the move in the right phase the whole recording is exact.

**The knockback arguments come from the shot, not from the victim.** The
collision event ends with `with(other) { motion_add(other.direction,
other.speed*0.03); }` — and inside a `with`, `other` is the *caller*. So a
standing target is pushed along the pellet's heading at 3% of the pellet's
speed, rather than accelerated along its own.

### Settled: the rocket

`Gg2.Simulation.Explosions`, the `with(Rocket)` block of `move_all_bullets.gml`,
`Objects/Weapons/Rocketlauncher.events/User Event 3.xml` and
`Objects/Projectiles/Rocket.events/*`. Gated by
`tests/Gg2.Tests/RocketTests.cs`, every rule of which was checked by deleting
the line it is about.

**Firing.** The muzzle is `weapon.x + lengthdir_x(20, aimDirection)` — the
*weapon's* position, which is where the character was at the end of the previous
tick rounded half to even, plus twenty pixels along the aim. The leash below is
anchored on the *character*, so those twenty pixels are already spent. The
launcher draws and broadcasts a seed like every other weapon and then uses none
of it: a rocket has no spread, so the seeded window is empty.

**Flight, per tick, in order:**

1. `lastknownx/lastknowny = owner.x/owner.y` while `instance_exists(owner)` —
   `owner` is the *character*, so the anchor follows a living shooter and freezes
   where a dead one fell.
2. `travelDistance = point_distance(x, y, lastknownx, lastknowny)`, and at
   `>= distanceToTravel` (800) the rocket sets `fade` and re-arms `alarm[3]` to 8.
3. `speed += 1` then `speed *= 0.92`, as two separate polar writes. The reference
   keeps the acceleration deliberately — "it's needed for consistent
   airblasting" — and the pair converges downwards on 11.5 from a launch of 13.
4. `image_angle = direction`.
5. `image_alpha -= 0.1` while fading, then **`if (image_alpha > 0) … else
   instance_destroy()`**.
6. `colliding = explodeImmediately`, then the `firststep` muzzle test, then —
   only if still free — the move and the destination test.

⚠️ **A rocket does not move on a tick it is already colliding**, where a pellet
moves unconditionally. The reference's comment: "Only move forward if we don't
collide, to ensure that the rocket will explode at the place of its collision."
Moving first shifts every wall blast by a tick of travel and changes who is
inside the radius.

⚠️ **`image_alpha` is how an over-ranged rocket actually dies, and it dies on the
tenth fading pass.** `alarm[3]` is re-armed on *every* pass the leash is still
over-extended, so it only ever fires for a rocket whose owner has come back
towards it. Ten subtractions of 0.1 from 1 leave 1.4e-16, which is under GML's
own 1e-13 comparison fuzz, so `> 0` is already false there — eleven would be the
answer with an exact comparison. The port counts ticks rather than storing the
alpha; the sequence is fixed, so that is exact and not an approximation.

⚠️ **A faded rocket does no damage at all.** `if (!fade) event_user(5) else
instance_destroy()` — one that reaches a wall at the end of its range is deleted
in silence, and somebody standing in front of that wall takes nothing.

**Hitting a character.** `Rocket.events/Collision with Character.xml` guards on
*team and nothing else* — no `place_free`, no `hp > 0`, no `ubered`, no owner
check, all of which a pellet's event has. An enemy is flickered, the rocket is
backed out of them with `really_move_contact_solid`, the victim is shoved two
units along **the rocket's** heading (`with(other) motion_add(other.direction,
2)` — inside a `with`, `other` is the caller), and the blast goes off. A
team-mate instead costs the rocket 100 of its travel budget, once each; the
shooter is free.

**The blast**, `Rocket.events/User Event 5.xml`, the largest single event in the
game:

- ⚠️ Its outer guard is `if (characterHit.id != ownerPlayer.object)` and reads as
  nonsense until you know **GML's `self` is -1**. `characterHit` starts at -1, so
  on a wall explosion `characterHit.id` resolves to the *rocket's own* id and the
  comparison is trivially true. It is not testing the sentinel.
- ⚠️ The direct-hit condition is `characterHit.team != team || characterHit.id ==
  ownerPlayer.object and !characterHit.ubered`, and **GML gives `&&`, `||` and
  `^^` the same precedence**, left to right. It means `(A || B) && C`. Under the
  C reading an ubered enemy would take the full 25.
- The splash distance is `distance_to_object`, which takes the
  **facing-inflated** box — so the same rocket at the same coordinates can be
  inside the cutoff for a left-facing character and outside it for a right-facing
  one. `place_meeting`, three lines away, does not inflate. Both are in
  `Aabb`.
- A **grounded team-mate who is not the shooter is exempt from the whole blast**,
  knockback included: `!(team == other.team and id != other.ownerPlayer.object
  and place_meeting(x, y+1, Obstacle))`. Only `Obstacle` is asked, so a team-mate
  standing on a player wall is still thrown.
- `vectorfactor = point_distance(0, 0, sin(rdir)^2, cos(rdir)^2)`, which is
  `sqrt(sin^4 + cos^4)`: exactly 1 along an axis and 0.707 at 45 degrees. A blast
  directly below throws you up to thirty per cent further than one below and to
  the side. The identity that looks like it would tidy this away is the one for
  `sin^2 + cos^2`, and this is not that.
- `if(fake_distance < 1) fake_distance = 0` pulls a direct hit to exactly zero so
  it takes the full falloff-free 30 on top of its 25.
- ⚠️ `if (1 - fake_distance/blastRadius <= 0.25) continue` — and `continue`
  inside a `with` moves to the next instance, so the outer quarter of the radius
  gets **no knockback either**. Reading it as "no damage" leaves a long-range
  rocket able to push people around.
- `moveStatus` is 1 for your own rocket, 4 for a team-mate's and 2 for an
  enemy's. All three mean rocket-jump gravity to `CharacterPhysics` and all three
  reach the wire. Only 1 skips the extra `vspeed -= 4*(1-falloff); speed *= 1.3`
  lift, which is gated on two `point_direction` tests that together mean "the
  explosion is under you".
- ⚠️ The finish-off window is armed under `if (other.ownerPlayer != id)`, which
  compares the shooter's **player** instance with the victim's **character**
  instance. Different objects, so their ids can never be equal and **the guard is
  always true**. A port that reads it as "unless the victim is the shooter" skips
  the window on every self-damaging rocket jump — the one case the line looks
  like it is about.
- Killing yourself with your own rocket while somebody else had already hurt you
  sets `lastDamageSource = DAMAGE_SOURCE_FINISHED_OFF_GIB` and **leaves
  `lastDamageDealer` alone**, so the credit stays with them.

**`blastRadius` and `explosionDamage` are per-kind constants, `knockback` is
not.** The only thing in the game that edits a projectile in flight is the pyro's
airblast (`Flamethrower.events/User Event 2.xml`), and it writes
`explosionDamage = 30` — the value a rocket already has — while genuinely
changing `knockback` and re-arming `alarm[1]`/`alarm[2]` to 40 and 80. So a
rocket's knockback is not a function of its age and cannot be derived from one.

#### Settled: `move_contact_solid` does not move an instance already in collision

`really_move_contact_solid(dir, speed)` is `move_contact_solid(dir, speed)` then
`move_outside_solid(dir + 180, speed)`, under the reference's own comment
"move_contact_solid is broken. let's fix that." The rocket calls it from its
collision event, so the instance is **always** already inside the character it is
backing out of — and the port transcribes only the second half.

Measured 2026-08-25 against the running game, with a character-sized solid (a
`Rocket` scaled to 24x34, `solid = true`) and a rocket-shaped probe inside it:

| Start | `move_contact_solid` | then `move_outside_solid(dir+180, 13)` |
|---|---|---|
| centred, (400, 400) | **(400, 400)** — unmoved | (390, 400), free |
| overlapping the left edge, (395, 400) | **(395, 400)** — unmoved | (390, 400), free |
| diagonal, (405, 405), dir 30 | **(405, 405)** — unmoved | (393.74, 411.50), **still stuck** |
| (400.25, 400) | **(400.25, 400)** — unmoved | (390.25, 400), free |

So the two halves compose to "back out the way you came", and
`Projectiles.BackOutOfCharacter` is exact.

The same probe re-confirmed both of `move_outside_solid`'s awkward properties,
which that helper copies: it steps **whole pixels while keeping the fractional
part** of the start (400.25 → 390.25, not 390), and when nothing within the
allowance is free it **moves the whole allowance anyway and stays stuck** — the
diagonal row above needed more than thirteen pixels to clear and landed at
exactly thirteen, still inside. An allowance of 3 where 10 were needed likewise
landed 3 along, still inside.

⚠️ Starting *outside* is a different question and was measured too:
`move_contact_solid(0, 13)` from 50 pixels away moved the full 13 without
reaching contact. Do not read the table above as "it never moves".

#### Settled: every solid a map places is a filled rectangle

This is what `collision_line` against a gate needed before it could be written,
because a rectangle is an *instance* rather than map geometry and so needs its
own `LineCollider` — and building one means claiming the sprite's mask fills its
bounding box.

Measured 2026-08-25 by walking every pixel of the reported box with a precise
`collision_point`:

- `gg_debug`, all eight solid level-entity types — `BulletWall`,
  `BulletWallHorizontal`, `PlayerWall`, `PlayerWallHorizontal`, `RedTeamGate`,
  `BlueTeamGate`, `RedTeamGate2`, `BlueTeamGate2` — **360/360 solid each**.
- `ctf_truefort`, all twelve gates, at scales 3x0.30, 3x0.80, 3x0.90, 3x1 and
  13x0.30 — **100% filled in every case**.
- `arena_lumberyard`, a `BulletWall` at 3x1.30 — **1404/1404**.

They wear one of two sprites the game builds at runtime, and both have their
**origin at (0, 0)** with a mask that is the whole sprite:

| Sprite | Size | Mask | Origin | Worn by |
|---|---|---|---|---|
| `sprite45` | 6x60 | `0,0..5,59` | `0,0` | the vertical variants |
| `sprite44` | 60x6 | `0,0..59,5` | `0,0` | the `*Horizontal` and `*Gate2` variants |

`Gg2.Simulation.LineCollider.ForSolidRect` is built on exactly that, and
`SolidRect` now carries the instance (position, scale, sprite extent) alongside
the box, which is free because a `SolidRect` lives in `CollisionWorld` and is
never cloned by a rollout.

**No new fixture was needed to gate it.** Every rectangle probe in
`collision-line-samples.json` is a literal `BulletWall` the sampler created and
scaled, wearing that same 6x60 sprite — so
`CollisionLineSampleTests.SolidRectanglesPresentTheColliderTheEngineMeasured`
rebuilds each probe through `SolidRect.FromEntity` and runs the engine's own
recorded answers back through `ForSolidRect`. The `dup`-sprite probe is excluded
because its origin is (3, 30) and no level entity has a non-zero one.

`CollisionWorld.CollisionLineBlocksBullets` is therefore complete: the walkmask
plus every solid rectangle the *bullet's* filter accepts, which is exactly the
reference's `Obstacle`, `BulletWall`, `IntelGate`, `TeamGate` and
`ControlPointSetupGate` and correctly not `PlayerWall`. `global.mapchanging` is
not modelled — it is true only while a map loads, when no character exists to
fire.

### Settled: the mine, which is not a small rocket

`Gg2.Simulation` — the `with(Mine)` block of `move_all_bullets.gml`,
`Objects/Weapons/Minegun.events/*` and `Objects/Projectiles/Mine.events/*`.
Gated by `tests/Gg2.Tests/MinegunTests.cs`.

**Firing.** `weapon.x + lengthdir(10, aim)`, speed 12, and then — the line that
catches people — **`oid.image_angle = 0`**, written straight after `createShot`
set it to the aim. A mine is the one projectile with an axis-aligned box however
it is thrown, and its movement block never writes the angle again where the
Shot's and the Rocket's both do.

⚠️ `Minegun.events/User Event 1.xml` carries a fourth clause the other weapons
do not: `lobbed < maxMines`, with `maxMines = 8`. It sits with the ready and ammo
tests, **above the seed** — so a demoman at eight mines is silent and does not
advance the shared RNG stream.

**Flight.** Three things no other projectile does:

1. `vspeed += 0.2` and then **`vspeed = min(vspeed, 8)`** — the only terminal
   velocity in the game, and what gives a lobbed mine its arc.
2. **It substeps.** The movement is a loop that walks at most six pixels along
   the remaining vector, testing `place_free` after each hop. A mine cannot
   tunnel through a thin wall the way a Shot at speed 13 can.
3. **It stops without dying.** A mine that reaches an `Obstacle` is backed out of
   it, has its speed zeroed and sticks. One that reaches anything *else* a bullet
   collides with — a gate, a bullet wall — is destroyed where it is. The
   difference is one `place_meeting(x, y, Obstacle)`, bracketed by
   `wallSetSolid()`, which is `with(Obstacle) solid = true` and nothing more.

A mine also has **no `Collision with Character` event at all** — its object holds
a Sentry collision and a Generator collision and nothing else — so it flies
straight through people. The only thing it can do to a character is explode.

**The blast is a different function from the rocket's**, and five things differ
in ways that change who dies:

| | Rocket | Mine |
|---|---|---|
| `vectorfactor` | `sqrt(sin⁴ + cos⁴)` | `point_distance(0, 0, sin, cos*0.8)` — **not squared**, vertical scaled |
| a direct touch | `if (d < 1) d = 0` | no such line; falloff applies |
| quarter-radius cutoff | always | **only while `splashThreshhold`** |
| self-damage | full | **× 5/9** |
| `moveStatus` | 1 self, 2 enemy, 4 team-mate, plus a speed boost | **1 self, 4 everyone else**, no boost |

⚠️ **A stuck mine has no cutoff.** `move_all_bullets` sets `splashThreshhold`
from `!stickied` on every pass, and the cutoff is guarded by it — so a landed
mine damages everything inside its forty pixels however faintly, while one still
in the air behaves like a rocket. The port stores the flag rather than deriving
it from `stickied`, because the two are a tick apart: detonation is a Begin Step
and the pass that refreshes the flag is in Step, so a blast sees what *last*
tick's movement left behind.

`dealFlicker` is also outside the damage branch here, so a team-mate the blast
merely shoves is still flickered.

**Mines chain, and that is why `exploded` has to exist.** A mine's blast
detonates every mine in its radius that is an enemy's or its own owner's — a
team-mate's is left alone — and those blasts scan back over a list that still
contains the first one, because GM8's `with` walks the instances that existed
when the block was entered and the `instance_destroy` is the *last* line of the
event. Without the flag a pair of adjacent mines would detonate each other
forever. The walk is depth-first, and the order is observable: the knockback is a
sequence of `motion_add`s on the same characters.

⚠️ **Mines die with their owner, silently.** `Minegun.events/Destroy.xml` is
`with(Mine) if ownerPlayer == other.ownerPlayer instance_destroy();`, and a
weapon is destroyed with its character. This is the **one exception** to "a
projectile outlives its owner": a rocket already in the air does not care that
its shooter died, a mine belongs to the weapon and goes with it — without
exploding.

**`lobbed` is a stored count, not a derived one.** It goes on the wire as the
first byte of a demoman's weapon block (`Minegun.events/User Event 12.xml`), it
gates firing, and `Mine.events/Destroy.xml` decrements it on *any* destruction —
exploded, flown into a gate, or killed off with its owner. Losing a decrement
permanently costs the demoman a mine.

### Settled: the right-click runs in the middle of Begin Step

`Character.events/Begin Step.xml` runs the attack (line 83), the jump (93-116),
the **right-click** (120-126), and only then the `switch(moveStatus)` that picks
this tick's control and friction factors (143).

⚠️ **That order is observable and it is what a mine jump is.** A demoman's
right-click detonates their mines; a mine going off under its owner sets
`moveStatus = 1`; and the switch four lines later reads it *on the same tick*.
Running the detonation after the whole of Begin Step gives the jump last tick's
friction and a different trajectory.

`Gg2.Simulation.CharacterPhysics` is therefore split into `BeginStepInput` and
`BeginStepAccelerate`, with `Weapons.SpecialStep` between them in
`MatchSimulation.Tick`. The second half recomputes `stuckInWall` and the key
union rather than carrying them across: both are pure functions of the character
and nothing in between moves it — a blast changes velocity through `motion_add`
and sets `moveStatus`, and touches neither the position nor the keys.

Only the Minegun answers the key so far. The Medigun, the Flamethrower's
airblast, the Revolver's stab and Quote's bubbles all hang off the same one; for
eight of the ten classes User Event 2 is empty and the key does nothing.

### Settled: bullets and characters collide with different things

`Scripts/Physics/charSetSolids.gml` and `gunSetSolids.gml` are different lists,
and three of the five kinds of solid differ:

| Kind | Character | Bullet |
|---|---|---|
| `Obstacle` (includes the map) | solid | solid |
| `PlayerWall` | solid | **not solid** |
| `BulletWall` | **not solid** | solid |
| `TeamGate` | `team != mine or carrying intel` | **always solid** |
| `IntelGate` | `team != mine and carrying intel` | **always solid** |
| `ControlPointSetupGate` | while setup runs | while setup runs |

`Gg2.Simulation.CollisionFilter.ForBullet()` selects the right column;
`SolidRect.IsSolidTo` is the predicate. Maps use the `PlayerWall`/`BulletWall`
pair to build a window that stops people but not shots, and the reverse.

⚠️ A `BulletWall` can also *open*: `BulletWall.events/Begin Step.xml` clears its
mask while any character is within a `distance` property. The property defaults
to -1 (never) and **no built-in map sets it** — checked across all 22 in
`maps.json`; the maps that place bullet walls carry no properties at all.
(That list used to be given here as "`arena_lumberyard` and `koth_harvest`", and
it was short: **`gg_debug` places them too**, which is how the regenerated
`collision-samples.json` gained two rectangles that map had never reported.) The port treats every bullet wall as
permanently closed, which is exact for the shipped maps and wrong for a custom
map that uses the trigger.

### Settled: death, and who gets the credit

`Character.events/End Step.xml:34-47`, `Scripts/Events/sendEventPlayerDeath.gml`
and `doEventPlayerDeath.gml`. `Gg2.Simulation.Deaths`.

⚠️ **The check `exit`s, and its position in End Step is observable.** It sits
after the stair descent and *before* the hp clamp, the facing and
`currentWeapon.x = round(x)` — so a character that dies this tick has already
walked down a step but never turns to face its aim, never drags its weapon along
and is never clamped into the map. The clamp is the reason to care: a character
killed outside the room keeps the coordinate it died on.

⚠️ **`hp <= 0` is a GML comparison and carries the 1e-13 fuzz.** Afterburn
subtracts a fractional amount of hp every tick, so an hp that ordinary
cancellation leaves at `+2.7e-14` is dead in the reference and alive to a raw
`<= 0`. `Gml.LessOrEqual`, like every other real comparison in the port.

**Attribution is four lines and three of them are easy to read wrongly:**

```gml
assistant = secondToLastDamageDealer;
with(lastDamageDealer)
    if (object)
        if (object.healer)
            assistant = object.healer;
```

- The `with` is over the **killer's player**, so `object.healer` is the medic
  beaming *them*. A medic who kept the killer alive takes the assist off the
  victim's second dealer **entirely** — it is a replacement, not a tie-break.
- `if (object)` is what makes a dead killer safe. `Destroy.xml` writes
  `player.object = -1`, and **GM8's truth test is `> 0.5`, so -1 is false.**
  That guard existing at all is the proof: were -1 truthy, this line would
  dereference it and the reference would error out on every trade kill.
  (`Scripts/Bots/botServerActions.gml:50` claims the opposite in a comment. The
  comment is wrong; the guard is the evidence.)
- `healer` holds a **player** instance or -1 (`Create.xml:139`,
  `Medigun.events/User Event 1.xml:80`), so the same `> 0.5` test means "somebody
  is healing them". In the port that is `Healer != CharacterState.Nobody` —
  **255, not 0.** A healer in slot 0 is a real healer.

**`killer` and `assistant` are 255 for nobody on the wire**, written where
`instance_exists` is false. Slot 0 is the server's own reserved player and always
exists, so a defaulted zero credits it with every unattributed kill.

**What death destroys and what it does not.** `Destroy.xml` destroys
`currentWeapon` and the blade's bubbles; a **sentry** belongs to the *player* and
survives, and so does any projectile already in the air — a projectile's
`ownerPlayer` is a player, which is what lets a rocket credit a kill to somebody
who died before it landed. `WorldState.Clear`, never `RemoveSlot`: a death is not
a departure, the player keeps their roster index, and compacting would hand every
slot above them to the wrong person.

**Both landed in phase 6**, and this paragraph said they had not for three
phases afterwards. `Destroy.xml` drops carried intel (`doEventDropIntel`) and
arms the respawn on `player.alarm[5]`. The respawn is `Deaths.Kill` arming
`PlayerState.RespawnTimer`, run by `Timers`' alarm-5 stage; the drop is in
`WorldState.Clear` rather than in `Deaths`, because every way a character stops
existing has to drop the flag and a death is only one of them.
⚠️ **A "not implemented yet" sentence has no test behind it and nothing goes red
when it comes true.** Grep before repeating one.

**Two other death paths exist and are not the hp check.**
`processClientCommands.gml:80-115` and `:145-175` kill a character when its
player changes class or team while damaged and outside a spawn room —
`DAMAGE_SOURCE_BID_FAREWELL` if nobody else had hurt them,
`DAMAGE_SOURCE_FINISHED_OFF` if somebody had, with the same healer redirect.
Inside a spawn room it is a plain `instance_destroy` with no message at all.
Those are server command handling and belong with the rest of it.

### Settled: the Medigun, which is a stored target rather than a shot

`Objects/Weapons/Medigun.events/{Create, Begin Step, Step, Alarm 5, User Event 1,
User Event 2, Destroy}.xml`, `Objects/Characters/Medic.events/{Create, Alarm
11}.xml`, `Scripts/Misc/calculateHealingFactor.gml`,
`Scripts/Events/{doEventUber, doEventUberReady}.gml`. `Gg2.Simulation.Healing`,
`Weapons.FireNeedleOrUber`.

⚠️ **The attack key does not fire this weapon.** Every other weapon's
`User Event 1` pulls a trigger; the Medigun's picks a patient and heals them, and
broadcasts no `WEAPON_FIRE` at all. The needle gun is on `User Event 2`, the
*right*-click — the same key a demoman detonates with. A port that routes the
Medigun through the shared fire path gives it the Scattergun's
`readyToShoot and ammoCount > 0` guard, which it does not have, and silences the
beam for two ticks in three.

**The beam is a stored target, not a ray.** `healTarget` is a **player**, chosen
once and then re-validated against range and line of sight every tick. It ends
only when a tick goes by without it connecting, which the weapon's own **Step**
event notices through `healedThisStep` — releasing the button cannot end it from
inside `User Event 1`, because that event is not reached at all on a tick the
button is up.

⚠️ **Three of the acquisition's measurements start from different points, and
two of them are in the same expression.** Inside `with(Character)`, `other` is
the Medigun and `owner` is its character:

| Quantity | From | To |
|---|---|---|
| `healDistance` (≤ 300) | the candidate | the **weapon** |
| `mouseDistance` (≤ 150 to score high) | the candidate | `owner.x + lengthdir(aimDistance, aim)` — the **character** |
| the aim ray | the **weapon** | `owner.x + lengthdir(300, aim)` — the **character** |

So the aim ray is a segment whose ends come from two different instances. In play
they differ by half a pixel and a tick of travel (`WeaponState.X`), which is
enough to pick a different patient out of a crowd and is invisible to any test
that spawns the weapon on top of its owner —
`MedigunTests.TheAimRayLeavesTheWeaponAndEndsWhereTheCharacterPoints` displaces
them forty pixels on purpose.

**Scoring is two disjoint bands.** Within 150 of the cursor a candidate scores
`3 - mouseDistance/150`, in [2, 3]; otherwise `1 - healDistance/300`, in [0, 1].
Anybody under the cursor therefore beats everybody who is not, however far away.
⚠️ The guard is `targetScore < bestScore`, strictly — so **a tie goes to the
candidate examined last**, which is the highest instance id in the reference and
the highest roster slot in the port.

⚠️ **The healing tier reads the patient's hp *before* the heal and the uber
charge reads it *after*.** Same variable, four lines apart:
`if(hp > 125) healthGained *= 0.75` comes first, then
`hp = min(hp + healthGained, maxHp)`, then
`if(hp < maxHp) uberCharge += 2.5 else uberCharge += 1.75`. A patient one tick's
heal short of full is topped up and then charges at the *healthy* rate.

**The healing ramp is `calculateHealingFactor(timeUnscathed)`:** 1 below 150
ticks, 3 above 225, linear between. ⚠️ **`timeUnscathed` is counted up in
`Character.events/Step.xml:25`**, capped at 300, and reset by every damage path —
this repo had the resets and not the counter for four phases, because nothing
read it until the beam did.

**The medic mends itself**, `alarm[11]`, the only alarm in the game belonging to
one class: +3, +4 or +5 hp every 30 ticks at the same 210/420-tick thresholds,
**unclamped** — End Step's clamp catches it.

⚠️ **An ubercharge lasts 241 ticks, not the 240 the arithmetic promises.**
`uberCharge -= 25/3` from 2000 leaves **7.02e-12** after 240 subtractions, which
is seventy times GML's 1e-13 fuzz, so `if(uberCharge <= 0)` is false and the uber
survives another tick. The reference performs the identical repeated subtraction
in the identical order. Same shape as the rocket's tenth fading pass; do not
round it away.

⚠️ **Popping the uber reads `keyState` where everything around it reads the
down-or-pressed union.** `uberReady && (owner.keyState & $10)` — a left-click
that arrives and is released inside one tick opens the beam and will not spend
the charge. And the uber is the **`else` arm of the needle**: a medic who is
ready to shoot, has no patient and has a round left fires that round instead, so
the way to activate an uber is to be beaming somebody or to be out of ammo.

**Who is ubered by what:** the medic ubers *itself* from the weapon's own Begin
Step, unconditionally, for as long as the charge lasts; the patient is ubered
from `User Event 1`, so only while the beam is actually on them. Both are
`alarm[2] = 3`, which the same tick's alarm stage takes to 2 — so invulnerability
lapses on the **second** tick after the last renewal, not the third.

⚠️ **The weapon's Begin Step is a *separate instance's* event.** A weapon is
created by its character's Create event and carries a higher instance id, so GM8
runs it after the whole of its owner's Begin Step and before the next
character's. The uber drain lives there.

**The needle is a `Shot` with three differences.** It falls at **0.2** rather
than 0.15 (`move_all_bullets.gml`'s `with(Needle)` block is otherwise
line-for-line the `with(Shot)` block), it deals 4 rather than 8, and its Create
event makes **no random draw** — there is no `perseverant` on a needle. It leaves
from `y + yoffset + 1`, and `yoffset` is 3 because `Weapon.events/Create.xml`
adds `sprite_yoffset` while `sprite_index` is still the object's default
`MedigunS` (origin 8, 3). There is no forward nudge, so a medic against a wall
shoots into it.

⚠️ **The needle's speed comes off the global random stream, not a broadcast
seed.** `7 + random(3)`, with no `sendEventFireWeapon` anywhere in the event —
the Medigun joins the Minigun and the Blade as exempt from "a weapon puts its
seed on the wire". *Which* value is drawn is therefore free; that a draw
**happens** is not, because the next weapon that does broadcast a seed takes it
from the same stream.

**`Medigun.events/Alarm 5.xml` is one unguarded line**, `ammoCount = maxAmmo` —
not the Scattergun's one-round-and-re-arm, and not even the Revolver's
`if ammoCount < maxAmmo`. All forty come back 55 ticks after the last needle.

**Being ubered refills the magazine and puts fires out.**
`Character.events/Begin Step.xml:389-394`, the tail of that event, and the only
branch of the tail this port transcribes so far. ⚠️ Its
`event_perform(ev_alarm, 5)` is inert — the line above has already filled the
magazine, so every reload body in the game (`if ammoCount < maxAmmo`) does
nothing and all that survives is the `alarm[5] = -1` after it.

✅ **The `setup` clause LANDED 2026-09-03.** A map with a setup gate, during its
setup timer, charges at the hurt rate (2.5) off a full-health patient rather than
the healthy rate (1.75) — `Medigun.events/User Event 1.xml:92-101`, where `setup`
is `instance_exists(ControlPointSetupGate) && areSetupGatesClosed()`. Here that is
`state.AttackDefend && state.SetupTimer > 0`: the map entity is named `SetupGate`
and `builder_init.gml:301` builds a `ControlPointSetupGate` from it, and
`areSetupGatesClosed.gml` is nothing but `global.setupTimer > 0`.

⚠️ **It was missing for three phases behind a comment claiming no mode here had a
`ControlPointSetupGate`.** That was false from phase 6: `Rounds.Start` arms the
timer for Invasion (`ctf_avanti`) and for attack/defend control points. The
clause is the only way to build charge before the gates open — during setup
nobody has been shot yet, so every patient is at full health and the healthy rate
means arriving at the first fight with 30% less meter than the reference gives.

⛔ **Landed WITHOUT a `SimulationBehaviourVersion` bump, deliberately, and the
reasoning has an expiry.** The stamp would not have caught it either way — its
surface is chosen rather than swept, constants and tables rather than branch
conditions — so the bump would have been a hand-made claim that every existing
checkpoint is now dated. It was skipped because **the change cannot reach
training**: nothing in `src/Gg2.Env` sets `SetupTimer` or `AttackDefend`, so both
sit at their `default(WorldState)` values and the branch is unreachable there, and
`PolicyController.TrainedClasses` (Scout, Soldier, Sniper, Heavy) contains no
Medic. ⚠️ **If an env or a duel ever arms a setup timer, or the policy is trained
on Medic, that reasoning expires and the bump is owed.**

✅ **No parity fixture moved and none of the three pinned bot measurements
shifted** when it landed, which says no checked-in trace covers a medic healing a
full-health patient during setup — a green fixture is not confirmation of a
behaviour. `MedigunTests.SetupChargesAFullHealthPatientAtTheHurtRate` is the
gate, with a control on each half of the conjunction.

✅ **The stat counters landed and this entry did not say so.** `HEALING` is
`Healing.cs:308`, `INVULNS` is `Weapons.cs:2410`, and `POINTS` is awarded from
`Scoring`, `Intel`, `ControlPoints` and `Healing`.

### Settled: afterburn, and the flame that starts it

`Character.events/Begin Step.xml:41-70`, `Character.events/{Create, Alarm 0}.xml`,
`Objects/Weapons/Flamethrower.events/{Create, Begin Step, Alarm 5, User Event
1}.xml`, `Objects/Projectiles/{Flame, BurningProjectile}.events/*.xml` and the
`with(BurningProjectile)` block of `move_all_bullets.gml`.
`Gg2.Simulation.Afterburn`, `FlameStats`, `Weapons.FireFlamethrower`.

**Two quantities, not one.** `burnIntensity` is "heat" — damage per second,
capped at 13 — and `burnDuration` is "fuel", ticks remaining, capped at 210. A
flame tops the two up independently: a flat 30 of fuel and a variable amount of
heat. Per tick the victim loses `burnIntensity / 30` hp and one tick of fuel, and
either quantity reaching zero ends the fire and clears `burnedBy`.

⚠️ **The heat only starts fading ninety ticks after the last flame lands, and the
gate is `alarm[0] == -1` exactly — not `<= 0`.** Every flame re-arms `alarm[0]`
to `decayDelay` (90); when it finally fires, `Character.events/Alarm 0.xml`
computes `intensityDecay = burnIntensity / decayDuration` (90) and only then does
the Begin Step line start subtracting it.

⚠️ **That one comparison forced a correction to the alarm stage.** GM8 writes -1
to an alarm *before* running its event — which is the only way a handler that
re-arms its own alarm can work, and `Medic.events/Alarm 11.xml` and
`Scattergun.events/Alarm 5.xml` both do exactly that. This port used to leave a
fired counter at 0 for one tick, which is indistinguishable everywhere else (a 0
decrements to -1 without firing, and the wire clamps both to 0) but delayed every
burn's fade by a tick. `Timers.Advance` now writes `Inactive`. **Inferred from
the reference, not measured against the running engine.**

⚠️ **A fire on somebody already at zero hp deals no damage and takes no credit.**
The reference cites its own bug number: "Don't count if someone else already
finished him off, to prevent afterburn from 'stealing' sticky kills (Bug
#1021989)". The fuel is still consumed — the guard is only around the damage and
the attribution.

⚠️ **The falloff runs the opposite way from the comment beside it.** A flame adds
`burnIncrease * falloffFactor` of heat, where
`falloffAmount = min(1, (flameLife - alarm[0]) / flameLife)` and
`falloffFactor = falloffAmount*0.65 + 0.35`. The comment says "value higher when
life is fresh (alarm 0 is high)" and "Reduce heat additions of 'old'/distant
flames" — but the expression is *small* when the alarm is high. So a point-blank
flame adds **0.35** of the heat and one at the end of its fifteen-tick life adds
the full 1.35. The code is the specification.

Above `legIntensity` (7) the addition is halved again — the reference names it
after the knee in a dynamic-range compressor. Both the curve and the leg are
gated on the projectile's `afterburnFalloff`, which is true for a `Flame` and
false for a `Flare`.

**A flame is a `Shot` in none of the ways that matter.**

| | `Shot` | `Flame` |
|---|---|---|
| movement | one jump | **six-pixel substeps**, like a mine |
| gravity | 0.15 | 0.15, but only because `object_index == Flame` |
| `image_angle` | rewritten to `direction` every pass | never rewritten — stays on the aim |
| wall test in the collision event | yes, dies inside a wall | **none** — burns through walls |
| victims | one, then destroyed | **one recorded, dies on the second** |
| repeat hits | n/a | `enemiesHit` refuses the same victim twice |

⚠️ **The penetration cap is compared before the victim is recorded.**
`if (ds_list_size(enemiesHit) >= penetrateCap and !perseverant) instance_destroy();
else ds_list_add(...)` with a cap of 1 — the list is empty on the first hit, so
the flame survives it and the *second* victim is the one that kills it. A
`perseverant` flame (one in eight, `random(1) < 1/8`) skips the destroy entirely
and can burn a whole team.

**The Flamethrower's tank is not a magazine.**

- ⚠️ `if (ammoCount < 1.8) ammoCount -= 1.8;` sits **above** the ready-and-loaded
  guard, so an already-empty tank is driven further negative on every tick the
  trigger is held. The reference's comment is "prevent sputtering": without it a
  tank hovering just under the threshold would refill past it and fire one frame
  in every few.
- The refill is 1.8 a tick from the *weapon's* Begin Step, gated on
  `isRefilling`, which firing clears and `alarm[5]` restores seven ticks later.
  A pyro holding the trigger therefore never refills.
- ⚠️ **The guard is `ammoCount <= maxAmmo`, so the tank overfills to 201.8 and
  stops.** Two hundred is not the resting value: a pyro who has fired once at any
  point in the match sits at 201.8 for the rest of it and the wire carries
  **202**. It is an `else if` with the negative-clamp, so a tick that clamped does
  not also refill.
- A tank that just ran dry waits `reloadBuffer*2` (14) ticks rather than
  `refireTime` (1).

⚠️ **`motion_add(owner.direction, owner.speed)` — the pyro's whole velocity
vector**, where a pellet and a needle inherit `hspeed` alone. A falling pyro adds
their fall to every flame.

⚠️ **The muzzle carries `owner.equipmentOffset`, which is computed in the
character's *Draw* event.** `Character.events/Draw.xml:170-190` sets it as the
run-animation bob, and `Flamethrower.events/User Event 1.xml:19` is the only
place in the entire game where a drawing variable feeds the simulation. A
headless server has no Draw event, so this port keeps it at its `Create` value of
0 — up to two pixels of divergence on a running pyro's flame origin. Reproducing
it would mean porting the run-animation state machine, which is drawing.

**No seed and no `WEAPON_FIRE`.** The event calls `randomize()`, so the
Flamethrower joins the Minigun and the Blade as exempt from "a weapon puts its
seed on the wire" — the spread is server-side RNG the port is free about. The
port draws from `WorldState.Random` rather than reseeding, because a simulation
with no clock cannot `randomize()`.

The airblast (`User Event 2`) and the `Flare` were blocked on two oracles for
one stage and are now implemented — see "Settled: the airblast, and the flare
that rides on it" below.

### Settled: the Shotgun is the Scattergun with four numbers changed

`Objects/Weapons/Shotgun.events/{Create, User Event 1, User Event 3}.xml`.
`Gg2.Simulation.Weapons.FireScattergun`, which serves both.

The two `User Event 3`s are line for line identical — the same `createShot`, the
same three writes per pellet in the same order (component, speed, direction), the
same fifteen-pixel `lengthdir` nudge, the same timings (`refireTime` 20,
`reloadBuffer + reloadTime` 35, six rounds). Four things differ:

| | Scattergun | Shotgun |
|---|---|---|
| pellets | 6 | **5** |
| damage per pellet | 8 | **7** |
| direction jitter | `random(15) - 7.5` | **`random(11) - 5.5`** |
| lifetime | `35 * ((min(1, forward/actual) - 1)/2 + 1)` | **none — the `Shot`'s own 40** |

⚠️ **The missing lifetime override is the one that changes the weapon's
character.** The Scattergun's is a range compensation: a pellet inherits the
shooter's `hspeed`, so a scout running at its target would out-range a standing
one, and the fuse is shortened by up to half to cancel it. The Shotgun writes
nothing, so a running engineer's pellets really do travel further — and every
engineer pellet out-ranges every scout pellet. Deleting the compensation would
make the two weapons agree, which is why
`ShotgunTests.AShotgunPelletHasNoRangeCompensation` asserts both halves.

The damage is set on the pellet **after** `createShot`, exactly as the Medigun
sets a needle's: `Shot.events/Create.xml` says 8 and the weapon overwrites it.
A projectile's own Create event is the default, not the answer.

**Both weapons broadcast their seed**, so five pellets cost exactly fifteen draws
— `choose(0, 0, 1)` from the Shot's Create, then the speed jitter, then the
direction jitter — and the count is contract, not bookkeeping.

### Settled: the four boxes that were derived, and are not any more

Measured 2026-08-27, `tools/probes/probe-sprite-boxes.js`, gated by
`SpriteBoxSampleTests` against
`tests/Gg2.Tests/Fixtures/sprite-box-samples.json`. Phases 5 and 6 landed four
collision boxes without a live session, each with a comment saying so. **All
four were right**, and none of them can drift silently again.

| Sprite | `sprite_get_bbox_*` | origin | about the origin | was |
|---|---|---|---|---|
| `BubbleS` | 0, 0, 11, 11 | 6, 6 | -6, -6, +5, +5 | derived from `mode="FULL"` |
| `StabMaskS` | 24, 13, 57, 55 | 26, 37 | -2, -24, +31, +18 | read off the PNG's alpha |
| `IntelligenceRedS` / `BlueS` | 0, 0, 19, 19 | 9, 12 | -9, -12, +10, +7 | read off the PNGs |
| `GeneratorRedS` / `BlueS` | 2, 14, 43, 41 | 23, 21 | -21, -7, +20, +20 | read off the PNG |

Every one of the six objects reports `object_get_mask == -1`, so none of them
wears the `Sentry`'s `mask_index` override and the sprite's own bound is the
answer. The generator's 48 sub-images share one bounding box, which is why it
needs no override where the sentry does.

- **The briefcase is the one that mattered.** It was the only collision box in
  the project that had never been measured, and `docs/PARITY.md` records that
  three of its four edges can each move **sixteen** pixels with every fixture
  still green. Note `bbox_bottom` is 19 on a 22-tall image: the AUTO bound stops
  two rows short of the art, which is exactly what reading a PNG's nominal size
  gets wrong. It happened not to be wrong here.
- **`mode="FULL"` really does mean the whole image**, confirmed directly rather
  than through `MineS` as a control.
- **The generator's box is what its 400-pixel blast measures every distance
  from**, so measuring the box measured the reach.

⚠️ **The flipped readings show the mirror that `Aabb.ForCharacter` skips.** At
`image_xscale = -1` the engine reports `[-right - 2, -left]` about the origin —
the box mirrored, *then* `bbox_left` pushed two pixels out. `ForCharacter`
applies only the two pixels, and that is correct **only because every character
mask is symmetric about its origin**. None of the four boxes above is: the
briefcase runs -9..+10 and mirrors to -10..+9. Nothing ever draws a briefcase or
a generator flipped, so nothing reads it — but hand `ForCharacter` an asymmetric
box some day and it will be quietly wrong.

### Settled: the sentry's collision box does not animate, and the turret's does

Measured 2026-08-26 against the running game (`tools/probes/probe-sentry-mask.js`).

⚠️ **A sentry's drawn sprite grows out of a toolbox and its extent swings
wildly; its hitbox does neither.** `SentryRed`/`SentryBlue` have **twelve**
frames and `Sentry.events/Begin Step.xml` animates through them with
`image_index = floor(hp/maxHp*10)`. Their extents, relative to the origin
(22, 27), are all over the place:

| frame | left, top, right, bottom |
|---|---|
| 0-3 (toolbox) | -8, -1, 7, 12 |
| 5 | -12, -9, 11, 12 |
| 8 | -10, **-21**, 11, 12 |
| 11 (built) | -10, 1, 5, 12 |

But `Objects/InGameElements/Sentry.xml` declares `<mask>SentryMaskS</mask>`, and
`mask_index` overrides `sprite_index` for **all** collision in GM8.
`SentryMaskS` has exactly **one** frame, so `image_index` cannot select a
different mask and the box is constant. Confirmed live —
`object_get_mask(Sentry)` returns `SentryMaskS`, and an instance wearing that
mask reports the identical box at every one of the twelve `image_index` values:

```
Sentry collision box, relative to the origin:   left -8, top -7, right 7, bottom 12
   (16 x 20; sprite bbox 14,20,29,39 with origin 22,27)
```

That is almost certainly *why* the mask sprite exists. A hitbox that ballooned to
frame 8's size and back during construction would shove a sentry through walls
mid-build.

⚠️ **The two-pixel facing inflation applies here too.** At
`image_xscale = -1` GM8 reports `-9, -7, 8, 12` — the mirror of the box would be
`-7 .. 8`, and `bbox_left` is two pixels further left than that. Exactly the
rule already documented for characters under "distance_to_object, place_meeting,
and the one place facing matters". A sentry's `image_xscale` is its
`startDirection`, so a left-facing sentry is genuinely two pixels wider to
`distance_to_object`.

**The turret is the opposite case.** `object_get_mask(SentryTurret)` is `-1`, so
it collides as its own sprite `SentryTurretS` — four frames, sprite bbox
`3,4,33,15`, origin `(15, 12)`, giving `-12, -8, 18, 3` about the origin. It is
drawn with `image_angle = direction`, so its box is the **rotated** one and goes
through `Aabb.ForRotatedSprite`; measured at 0/45/90/135/180 degrees. At
`image_xscale = -1` it reports `-20, -8, 12, 3`, again the mirror plus two
pixels on `bbox_left`.

⚠️ **The flip inflation is applied to the sprite edge *before* the rotation, not
to the rotated box** — measured 2026-08-26 (`tools/probes/probe-airblast.js`;
the port assumed the opposite for one session). At `image_xscale = -1` GM8's box
is the rotated bound of the *mirrored sprite box with its mirrored-left edge
extended two sprite pixels*, so at 45° the two pixels land partly on the
`bbox_bottom`: the flipped turret at 45° is `-20, -14, 11, 16` where the
post-rotation rule predicts a bottom of 15, and at 90° it is `-8, -12, 3, 20`
where post-rotation predicts `-10, -12, 3, 18`. Nineteen boxes on
`AirBlastMaskS` and three flipped turret rotations all match the pre-rotation
model exactly; `Aabb.ForRotatedSpriteReported` is the implementation and
`AirBlastBoxTests` pins every measured row.

⚠️ **A MANUAL mask bound in the sprite XML is clamped to the image.**
`SentryRed.xml` declares `<bottom>40</bottom>` on a sprite 40 pixels tall and GM8
reports `bbox_bottom = 39`. So the XML is not a substitute for asking the engine
even when it looks like one.

### Settled: `collision_circle` is a plain Euclidean test, unlike `collision_line`

Measured 2026-08-26 (`tools/probes/probe-circle.js`). **This is the good outcome:**
`collision_line` turned out to be a clip-round-pixel-walk that three exact
intersection tests failed to reproduce, so the reasonable fear was that
`collision_circle` would be another one. It is not.

```
collision_circle(cx, cy, r, obj, prec, notme) hits an instance iff the
Euclidean distance from (cx, cy) to that instance's INCLUSIVE bounding box
[bbox_left, bbox_right] x [bbox_top, bbox_bottom] is <= r.
```

Evidence, against a probe whose box is exactly `992..1007` by `993..1012`:

- **Each axis, one pixel at a time, r = 10.** Last hit going right is a centre of
  1017 — distance to `bbox_right` exactly 10. Going left, 982 — distance to
  `bbox_left` exactly 8+2 = 10. Going down, 1022 — exactly 10 from
  `bbox_bottom`. Three independent confirmations that the boundary is
  **inclusive at exactly `r`**, so the comparison is `<=` and not `<`.
- **The diagonal off the bottom-right corner**, where a true distance test and a
  pixel walk part company: centres at `(1007+d, 1012+d)` hit for `d <= 7` and
  miss from `d = 8`. `7*sqrt(2) = 9.899 <= 10`; `8*sqrt(2) = 11.31 > 10`. A
  corner-to-centre Euclidean test predicts exactly that changeover.
- **Radius 0 on the corner pixel `(1007, 1012)` hits** and one pixel past it
  misses, so the box really is inclusive at both ends.

**Second measurement, 2026-08-26 (`tools/probes/probe-airblast.js`,
`probe-airblast2.js`), which closed the gaps the first left open:**

- ⚠️ **The centre is floored per axis before the test.** The airblast centres
  the circle on the *victim's* real-valued position, and every fractional
  centre up to +0.99 past the last integer hit still hits — including a
  diagonal whose true Euclidean distance is 10.076 against r = 10, which a
  real-centre test would miss, and a +0.5 case that rounding would miss. Floor
  and truncate-toward-zero are indistinguishable at positive coordinates,
  which is all the probe (and GG2's maps) reach. `Aabb.CircleHits`.
- **A rotated target answers with its reported axis-aligned box, not the
  rotated rectangle.** A circle walked diagonally off a rotated turret's AABB
  corner — ~8px from any actual sprite pixel — hits for exactly the offsets a
  Euclid-to-AABB test predicts (d ≤ 7 at r = 10) and a rotated-rectangle test
  would always miss.
- **A flipped target's two-pixel `bbox_left` inflation counts.** The circle
  reaches the inflated edge (hit at exactly r from it, miss one pixel
  further), so `collision_circle` consumes the same reported box
  `distance_to_object` does.
- **An instance-id target behaves like an object target** — which is how the
  airblast passes `other.poof`.

⚠️ **What is still not covered:** integer radii only (every GG2 call site is
an integer radius), and a `RECTANGLE` mask. `prec = true` gave identical
answers throughout — but against a rectangular mask that proves nothing about
the precise path, which is the one a map's 6x mask would take. Nothing in GG2
calls `collision_circle` with `prec = true` (`serverBuildSentry.gml`,
`Sentry.events/Begin Step.xml` and `Flamethrower.events/User Event 2.xml` all
pass `false`), so the uncovered path is also the unused one.

This unblocks all three callers: the pyro's airblast, the sentry build
precondition (`no sentry within 50 px`) and the sentry's self-destruct guard
(`another sentry within 25 px of (x, y+10)`).

### Settled: the sentry, and the turret that fires from End Step

`Gg2.Simulation.Sentries`, `SentryState`. Transcribed from
`Sentry.events/{Create, Begin Step, End Step, User Event 2, Destroy}.xml`,
`SentryTurret.events/{Create, User Event 0, End Step, Alarm 0}.xml`,
`serverBuildSentry.gml`, `buildSentry.gml` and `damageSentry.gml`; gated by
`SentryTests` (20), the wire round-trip in `SentryUpdateLayoutTests`, and the
fixture landing gate below.

**Building.** `serverBuildSentry` demands `nutsNBolts == 100` — an *equality*,
through GML's fuzzy compare — no sentry within `collision_circle(x, y, 50)` of
the builder, no sentry already owned, engineer class, and two things the port
does not model: not inside a `SpawnRoom` and not on a `HealingCabinet`
(`Sentries.CanBuild` documents the gap). The build takes the whole hundred and
`Character.events/Begin Step.xml:387` regenerates `min(nutsNBolts + 0.1, 100)`
per tick, for every class, in the same Begin Step tail as the uber effects
(`CharacterPhysics.BeginStepUberEffects`).

**Construction.** 1.2 hp per tick from 25 while `vspeed == 0`, finishing *built*
with hp written to exactly 100 on the pass after the increment overshoots.
⚠️ **The rate ramp beside it is a no-op**: `buildrate = min(buildrate,
buildrate + 0.6/45)` under a comment claiming 0.5→1 — `min` of a value and a
larger value is the value. The only write that ever moves it is
`damageSentry.gml`'s `buildrate = 0.6`, which is **permanent** — one pellet
mid-build slows the rest of the build and every repair after it. The turret is
created on the tick `built` flips, and its Create event's `alarm[0] = 5` starts
the refire clock immediately.

**The fall.** `wallSetSolid` is `with(Obstacle) solid = true` and *nothing
else* — a falling sentry passes through player walls, bullet walls and every
gate (`CollisionFilter.ForSentry()`, a third solidity list beside
`charSetSolids` and `gunSetSolids`). Gravity is the same one-ULP-high 0.6 as a
character's, capped at 10; the landing is `really_move_contact_solid(direction,
speed)` — the derived motion pair, (270, vspeed) for a plain fall — then
`if (!place_free(x, y+1)) vspeed = 0`. The `SENTRY_POSITION` broadcast fires on
`oldVspeed != 0 && vspeed == 0`, once per placement. The Begin Step ends with
the `x += hspeed; x -= hspeed` delta-factor dance, which is a ULP-losing
identity and transcribed as such.

**The turret** (`SentryTurret.events/End Step.xml`):

- ⚠️ **It fires from End Step** — the only weapon in the game that does. It is
  younger than every character alive at build time, so its End Step runs after
  theirs: a character killed this tick is already gone when the scan runs, and a
  victim it finishes dies in their *own* End Step next tick.
- **Targets are enemy characters and enemy sentries**, within
  `distance_to_object <= 375`, popped nearest-first from a `ds_priority`. The
  turret's *own* sentry enters the queue at distance zero and is discarded by
  the team test — one wasted pop per tick, transcribed.
- **The arc is ±45° of horizontal** (`<=45 || >=315 || 135..225`), with a
  `place_meeting` exception for somebody standing on the gun — and the aim is
  then clamped back into the arc when firing (45/135/225/315).
- **Sight is `collision_line_bulletblocking`** (map, bullet walls, all gates)
  plus a per-sentry `collision_line` test: any sentry that is neither the
  turret's own nor the target obscures the shot, *friendly sentries included*.
- ⚠️ **Acquiring a target out of idle costs `refireTime*2`**: `noticedTarget`
  makes the first shot land ten ticks after first sight; thereafter one shot
  per five.
- **The flip takes fourteen ticks**: `rotateindex += 0.3` across four frames,
  flipping `image_xscale` and adding 180 to the direction only when the counter
  arrives — and the fire check runs *before* the flip block, so the first shot
  backwards lands on the fifteenth tick.
- **Damage is 8 via `damageAuto`** — `damageCharacter` for a character (full
  assist bookkeeping, `DAMAGE_SOURCE_SENTRYTURRET`), `damageSentry` for a
  sentry. The `nearestTarget.alarm[3] = ASSIST_TIME` write lands on a *sentry's*
  alarm 3 too, which no event reads — inert, not transcribed.
- **Its boxes rotate.** `image_xscale` and `image_angle` are rewritten from the
  direction at the top of every End Step and the turret collides as its own
  rotated sprite (`Aabb.ForRotatedSprite`, plus the two-pixel `bbox_left`
  inflation at `xscale = -1`, assumed to survive rotation — measured only at
  angle 0).

**Projectiles against a sentry** — four different events, none the character
one reused: a `Shot`/`Needle` runs the wall test then damages an enemy sentry
and dies; a `Rocket` makes the sentry its direct victim (25 + the
zero-distance splash 30 = 55) with no back-out and no shove; a flame damages
and dies **with no wall test and no penetration count — even perseverant**; a
`Mine` sticks (`speed = 0; stickied = true`) to enemy sentries and is not
destroyed.

**Blasts.** The rocket's `with(Sentry)` loop is damage only — no knockback, no
quarter-radius cutoff, no grounded exemption. The mine's deals
`explosionDamage*3/2*(1 - falloff)` (the reference's own comment: two stickies
should do 100, not 90) and *does* honour `splashThreshhold`.

**Destruction.** Out of hp → End Step sends `DESTROY_SENTRY` with the killer
and the **killer's healer** as assistant (no second dealer on a sentry), then
the Destroy event: every mine `place_meeting` the sentry is unstuck, and the
owner — **only if airborne**, `!place_meeting(x, y+1, Obstacle)` — is shoved
with the rocket's vectorfactor curve, knockback 4, radius 65, quarter cutoff.
The Begin Step guard destroys a sentry overlapping another
(`collision_circle(x, y+10, 25)`) with killer nobody and cause 0.
⚠️ **The class/team half of that guard lives at the seams** —
`ownerPlayer.class` is *player* state `WorldState` does not hold, so
`Gg2.Server` and `PortedSimulation` destroy the sentry when they apply a
change-class/team; it moves into the simulation when phase 6 brings player
state in.

**Fixture evidence.** `closedsupport`'s two recorded sentries are injected at
t861/t877 — *inside* the t774–t1102 hp window — and both land on the exact
recorded tick at the exact recorded fifth-of-a-pixel
(`SupportClassFixtureTests.TheInjectedSentriesLandOnTheRecordedTickAtTheRecordedSpot`),
and the hp and trajectory windows did not move with the turrets live: 489/328
before, 489/328 after. ⚠️ The landing gate alone **cannot** see a wrong gravity
constant on an eleven-pixel fall (0.6 and 0.7 land on the same tick in the same
wire bin — checked by mutation); the digit-exact resting y in
`SentryTests.AFreshSentryFallsLandsOnTheFloorAndAnnouncesItOnce` is the control
that bites.

### Settled: the airblast, and the flare that rides on it

`Objects/Weapons/Flamethrower.events/User Event 2.xml`, dispatched by the same
right-click as the mine detonation (`Character.events/Begin Step.xml:120-126`).
`Gg2.Simulation.Weapons.AirBlast`, gated by `AirBlastTests` and the oracle rows
in `AirBlastBoxTests`. Each rule below was verified to bite by mutation or was
measured directly.

✅ **And a recording gates it too, since 2026-08-27** —
`reference-ctf_truefort-closedairblast.trace`, three pyros against three
soldiers. **1,266 exact trajectory ticks with the airblast implemented and 286
with `Weapons.AirBlast` stubbed to a no-op.** `AirblastFixtureTests`.

⚠️ **This file used to say "the Flamethrower is unseeded, so no recording can
gate it", and that was a claim about the fixtures rather than about the
airblast.** Two things were wrong with it:

- **The reflectable set is `Rocket`, `Flare` and `Mine`.** A pyro with none of
  those in front of it never presses the key. `closedsupport` is the only
  earlier fixture with pyros in it and it is medic/pyro/engineer — **no rockets
  and no mines at all** — so its pyros could not airblast however long it ran.
  That is the whole reason implementing the airblast moved no bound.
- ⚠️ **An unseeded weapon caps an *hp* window, not necessarily a *trajectory*
  one.** `Projectiles.BurningHitCharacter` deals damage and calls no
  `motion_add`, so where the port's flames land cannot move anybody — where a
  Minigun's pellets shove, which is why a heavy recording's trajectory window
  really is capped. **When a weapon looks ungateable, ask whether it moves
  people or only hurts them.**

- ⚠️ **`blastAngle = 75` is dead, and so is the per-victim `angle`.** Both are
  computed and never read: the blast's reach is *only* the rotated `AirBlastO`
  box plus a per-victim `collision_circle` — r = 25 for characters (and the
  client-side sheets and gibs), r = 5 for rockets, flares and mines. There is
  no cone test in the airblast.
- **The poof is built from three measured pieces.** `AirBlastMaskS` (the mask
  `AirBlastO.xml` declares): bbox `9,1,97,28` on a 120x30 image about origin
  `(1, 14)`. Created 25px ahead of the *weapon* along the aim, with
  `image_angle = aim` for a right-facing pyro and `aim + 180` with
  `image_xscale = -1` for a left-facing one — the weapon's xscale, which
  `Character.events/End Step.xml:38-46` writes in the same block as
  `currentWeapon.x`, so it is the character's facing. The reported box applies
  the flip inflation *pre-rotation* (`Aabb.ForRotatedSpriteReported`), and the
  circle floors the victim's centre (`Aabb.CircleHits`).
- **The poof is not worth pooling.** Its entire simulated effect happens inside
  the event; afterwards it is an `image_index` animation and an
  `instance_destroy` on Obstacle contact, none of it observable — and each
  blast tests only against its *own* poof. The port computes its box, applies
  the event, and discards it.
- ⚠️ **The shove has no uber test and no hp test.** An ubered enemy is thrown;
  so is one at zero hp waiting for End Step. `motion_add(aim,
  15 * (1 - dist/150))` with `dist` measured **weapon-to-victim** (the
  Medigun's two-instances trap again), then `vspeed -= 2`, `moveStatus = 3`
  (0.35 control, 1.05 friction), the assist bookkeeping, and
  `lastDamageSource = -1` — a shove into a pit credits the pyro with no
  weapon.
- **The extinguish is a re-spawn, not a removal.** A burning team-mate —
  including the blasting pyro — loses the fire and sheds `realnumflames` real
  `Flame`s owned by the *pyro*, at the victim's position plus per-flame
  offsets, flying along the aim at `6.5 + random(2.5)` **doubled** by
  `motion_add(direction, speed)` on themselves. `realnumflames` is
  `5 * burnDuration / 210`, recomputed in End Step (line 128, below the death
  exit) so the airblast reads a value one decay stale, and the `for` loop's
  fractional bound means 3.7 flames is four. The reference's offsets are
  per-character randoms rolled once at Create; the port rolls fresh ones per
  extinguish — both are off the unseeded stream. These flames are made by
  `instance_create`, not `createShot`, so their `image_angle` stays 0.
- **A reflect is an ownership transfer plus a turn.** Rockets, flares and
  mines of the *other* team inside r = 5: `ownerPlayer`, `team` and `weapon`
  become the pyro's, and `motion_set(aim, speed)` turns without slowing. A
  rocket is restored to `hitDamage 25, explosionDamage 30, knockback 8` with
  the knockback alarms re-armed **longer** than a fresh rocket's (40/80
  against 20/30); a flare gets its whole `alarm[0] = 40` life back.
- ⚠️ **A reflected mine is the one projectile whose `alarm[0]` is not a
  destroy.** `Mine.events/Alarm 0.xml` is `reflector = noone;` — the stickied
  branch of `move_all_bullets` arms it to 30 when a reflected mine lands, so
  the pyro's claim on it expires thirty ticks later and the mine lives on.
  `Timers.Advance` special-cases the kind; a fresh mine's `Lifetime` must rest
  at -1 (not a defaulted 0) or the re-arm misses its landing tick.
- **A reflected mine leaves at `max(speed, 28/3)`** (`blastStrength/3` — the
  sheet/gib constant, not the character one), and a *stuck* one is unstuck:
  `speed *= 0.65` (the ULP-high literal — `Gml.PointSixFive`), then a bounce
  off the surface normal built from four `place_meeting(±3, Obstacle)` probes,
  reflecting only a velocity component *into* the surface, as component
  writes with no snap.
- **The reflector arms of the mine's blast attribution** (`Mine.events/User
  Event 2.xml`): a reflected mine reaching its own owner is never the
  finish-off-gib case and credits the *reflector* as
  `DAMAGE_SOURCE_REFLECTED_STICKY`; the demotion clause carries
  `other.reflector != lastDamageDealer`, which also (reflector `noone`,
  dealer `noone`) skips a demotion that was previously a harmless
  Nobody-over-Nobody write. `reflector` is a **player**, so it survives the
  pyro's death and `WorldState.RemoveSlot` renumbers it.
- ⚠️ **The flare's gate reads `keyState`, not the down-or-pressed union** —
  attack *held* while right-clicking, plus 75 ammo and `readyToFlare`, plus a
  clear `collision_line_bulletblocking` to the muzzle (the same 25px point as
  the poof, with no `equipmentOffset`). It costs 35 on top of the blast's 40
  and re-arms `alarm[2] = 55`. It leaves *before* the reflect loops, which
  therefore walk it and skip it on team.
- **The flare is a `BurningProjectile` with six numbers changed**: hitDamage
  **30**, flameLife **40**, burnIncrease **8**, durationIncrease **35**,
  `afterburnFalloff = false` (full heat at any age, no leg), penetrateCap
  **0** — it dies on its *first* victim unless perseverant. And it is the one
  child without the gravity line (`if (object_index == Flame)`), so it flies
  flat.
- **The finish block**: `readyToBlast = false`, `readyToShoot = false`,
  `alarm[5] = 40` (the refill restart), `isRefilling = false`,
  `alarm[1] = 40`, `alarm[0] = 15` (`blastNoFlameTime` — the flame is silenced
  through the ordinary refire path), `ammoCount -= 40`. `justBlast` is the
  recoil animation and is not modelled. Nothing goes on the wire: like the
  flame, every client re-derives the blast from the replicated keystate.

**Fixture evidence is null, and that is the expected shape.** All five
closed/open windows (357/1204/756 closedcombat, 489/328 closedsupport) are
byte-identical before and after the airblast landed — the same signature the
sentry had. The Flamethrower's unseeded RNG caps what any recording can say
about a pyro; the unit tests carry this stage.

### Settled: the player, the respawn, and a change of class or team

`Objects/InGameElements/Player.events/{Create, Alarm 5}.xml`,
`Scripts/Spawning/PlayerSpawn.gml`, `Character.events/Destroy.xml`,
`processClientCommands.gml`'s `PLAYER_CHANGECLASS`/`PLAYER_CHANGETEAM` cases.
`Gg2.Simulation.PlayerState`, `PlayerCommands`, the alarm-5 stage in `Timers`;
gated by `PlayerLifecycleTests` and the end-to-end
`SimulatedServerTests.AClassChangeKillsAndTheServerRespawnsByItself` — no
recording gates respawn, because every closed-loop fixture injects the
reference's own `PLAYER_SPAWN`s.

- **Three player fields feed the simulation** out of the reference's sprawling
  `Player` object: `team`, `class`, and `alarm[5]`, the respawn timer.
  `object` is `Alive`, `sentry` is `HasSentry`; the stats, rewards and (in
  this fork) the bot-AI state are server bookkeeping the simulation never
  reads. `Player.events/Create.xml` starts every player a **spectator scout**
  — and `Team.Red` is 0, so a zeroed `PlayerState` is the slot-0 trap again:
  use `Fresh`.
- **The respawn is `player.alarm[5]`,** armed to `Server_Respawntime`
  (ini seconds × 30, floored at one tick) by `Character.events/Destroy.xml` —
  every way a character stops existing runs it — and to **1** when a
  characterless player picks a class or team (`processClientCommands.gml:107,
  177`: `if (player.alarm[5] <= 0)`, so a timer already counting is left
  alone). ⚠️ A team change with a live character *also* re-arms it explicitly
  after the death (`:175`), which in the reference overrides the Destroy
  event's spawn-room fast path; with spawn rooms unmodelled the two writes are
  indistinguishable here.
- **`Player.events/Alarm 5.xml` is `if (spawningAllowed()) PlayerSpawn();`.**
  `spawningAllowed` is arena-round gating, true in every mode the port runs.
  `PlayerSpawn.gml` guards on `team != TEAM_SPECTATOR and object == -1`, picks
  `selectSpawnGroup(team)` — **0 for every mode without control points** — and
  `spawnpointID = floor(random(n))` off the global stream: free, because
  `sendEventSpawn` broadcasts the id and every client follows the message.
  Players are older instances than any character or weapon, so within alarm
  index 5 every respawn fires before any weapon's reload; a character born in
  the alarm stage runs its Step — and is seen by `move_all_bullets` — in the
  same tick, like any GM8 instance created between stages.
- ⚠️ **A class or team change with a live character is a death.**
  `!instance_exists(lastDamageDealer) || lastDamageDealer == player` picks
  `DAMAGE_SOURCE_BID_FAREWELL` with the player as their own killer and no
  assistant; otherwise the last dealer gets a `DAMAGE_SOURCE_FINISHED_OFF`
  kill with the standard killer's-healer assist redirect. The death message
  precedes the change message in the stream. The class-change case checks
  `collision_point(x, y, SpawnRoom, ...)` and silently swaps inside a spawn
  room — unmodelled, like the sentry build precondition, so the port always
  takes the death arm.
- ⚠️ **The team balance guard**: an invalid team byte is a request for "any";
  "any" resolves to the smaller team or to `choose(TEAM_RED, TEAM_BLUE)` when
  level; the request is refused — silently, no death, no broadcast — when it
  names the bigger team (counting everyone but the requester) **or the team
  the player is already on. An "any" whose coin lands on the requester's own
  team is refused too**; that is the reference, not a port artifact.
- **The sentry's Begin Step guard is whole again**: `ownerPlayer.class !=
  CLASS_ENGINEER or team != ownerPlayer.team or collision_circle(...)`, firing
  on the tick *after* a change — the old server/replayer seams fired it inside
  the change, one tick early.
- **Message timing**: a spawn (like a fire or a death) is appended to the send
  buffer mid-tick and leaves on the *next* tick's flush, ahead of that tick's
  update — the byte order `TraceParser` already assumes. The old direct spawn
  path sent it one flush early.
- **Not modelled**, each documented at its site: `checkClasslimits` (no port
  path enforces class limits), `ServerBalanceTeams` (moves other players
  mid-round), and the spawn-room fast respawn. The intel drop in `Destroy.xml`
  **is** modelled since the objective landed — see below.


### Settled: the intel, and the four events that move it

`Objects/Map elements/Gamemode-specific/CTF/Intelligence*`,
`Scripts/Events/doEvent{Grab,Drop,Return,Score}Intel.gml`, the four
`Character.events/Collision with Intelligence*.xml`, and
`Character.events/Begin Step.xml:303-369`. `Gg2.Simulation.Intel`,
`IntelState`, `WorldState.{RedIntel, BlueIntel, RedCaps, BlueCaps}`; gated by
`IntelTests`, `IntelUpdateLayoutTests` and `IntelFixtureTests`.

- ⚠️ **A carried briefcase has no instance at all.** `doEventGrabIntel` ends
  with `instance_destroy()` and `doEventDropIntel` creates a fresh one where
  the carrier stood. "Who has it" is `intel` on the *character*, never a field
  on the flag — and `serialize(IntelligenceRed)` of a carried flag writes an
  instance count of **zero**, which is why the wire block is a list.
- ⚠️ **The carrier inherits the flag's remaining return alarm, and a flag taken
  off its base gives them nothing.** `intelRecharge = max(0, alarm[0])` turns
  the resting -1 into **0**, and a drop writes that 0 straight back into the
  new instance's `alarm[0]`. An alarm of 0 never fires in GM8, so **a
  briefcase dropped in the first seconds of a run lies there indefinitely**.
  Both halves are the reference's.
- **The return timer charges with distance, not time**:
  `INTEL_MAX_TIMER/((3 + abs(min(hspeed, 7))/3.5)*30)` per tick — a full 900 in
  three seconds standing still and five at a scout's top speed. (Note the
  `min` before the `abs`: running left passes `min` unchanged and is capped by
  the `abs`. Same number, different reading.)
- ⚠️ **The grab guard is `(canGrabIntel or other.alarm[0] <= 1)`.** A flag at
  its base rests at -1 and is therefore always grabbable, cooldown or not; a
  dropped one needs the ten-second `alarm[1]` a *drop* starts. And the
  zero-alarm case above means an ubered carrier can drop the flag in Begin
  Step and pick the same flag back up in the collision stage of the same tick.
- ⚠️ **`!place_meeting(x, y, TeamGate)` is the object `TeamGate`, not `Gate`,
  and it asks nothing about solidity.** `IntelGate` is a *sibling* under
  `Gate`, so an intel gate does not refuse a grab and a team gate does —
  **including the grabber's own**, which is not solid to them and which
  `place_free` would call clear. `CollisionWorld.PlaceMeetingKind`.
- ⚠️ **A drop caused by the carrier's death broadcasts nothing.**
  `sendEventDropIntel`'s own comment: it would be a nested event inside the
  death and the client would process the death first and then error out
  dropping a flag for a character it had already destroyed. So the drop lives
  in `WorldState.Clear` — the Destroy event — and covers a death, a class or
  team change, and a departure alike, silently. Only the ubered drop and the
  client's own `DROP_INTEL` command are broadcast.
- ⚠️ **`doEventReturnIntel`'s "force carriers to drop it" branch is dead
  code.** It does `with(Character) ... event_user(5)` and **`Character` has no
  `User Event 5`**, so GM8 runs nothing. It never matters: both callers reach
  it with the instance already existing — `Alarm 0` is the flag's own alarm,
  and `Destroy.xml` calls `doEventDropIntel` first.
- **A capture does not start a grab cooldown.** `doEventScoreIntel` touches
  neither `canGrabIntel` nor `alarm[1]`, unlike a drop — and it recreates the
  flag on **its own** base, not the one it was carried to.
- **The collision stage is event-major.** GM8 keys a collision event by the
  other object's id and walks them in that order:
  `IntelligenceBaseRed` (104), `IntelligenceBaseBlue` (105),
  `IntelligenceRed` (133), `IntelligenceBlue` (134) — so every character's
  *capture* resolves before any character's *grab*. Observable exactly once: a
  flag lying on the enemy's own base, captured by one player and grabbed by
  another on the same tick.
- **In Invasion the red briefcase is scenery.** `Collision with
  IntelligenceRed.xml` carries a `!instance_exists(InvasionHUD)` guard the blue
  one does not. Resolved once at map load (`IntelState.Grabbable`), because the
  gamemode cannot change while a map is loaded.
- **`round(x*5)` on the way out**, GML's half-to-even — not the character's
  plain `x*5`. docs/TRAPS-SIMULATION.md's rounding table, rows one and two.
- **A capture sets `GameServer.syncTimer = 1`**, which sends an extra
  `CAPS_UPDATE` on the *next* tick, in addition to that tick's ordinary update
  (`GameServerBeginStep.gml:88`). The port sends the `frame mod 3600`
  heartbeat too; the third trigger, `global.setupTimer == 180`, waits on the
  setup timer being simulated.

✅ **Settled 2026-08-27: the briefcase's box is `-9, -12, +10, +7` about the
origin**, measured against the running game. `IntelligenceRedS`,
`IntelligenceBlueS` and `IntelligenceBaseS` are all 20x22 with their origin at
(9, 12) and a `RECTANGLE` mask on `AUTO` bounds; the engine reports every one of
them as `sprite_get_bbox_* = 0, 0, 19, 19` — the bottom two rows of all three
images are fully transparent — and none of the four objects that wear them
declares a `<mask>` of its own, so one box serves the flags and the bases alike.
A **live briefcase on `ctf_truefort` answers `bbox_* - x/y = -9, -12, 10, 7`
directly**, at `image_xscale = image_yscale = 1` and `image_angle = 0`.
`tools/sample-sprite-box.js` regenerates
`tests/Gg2.Tests/Fixtures/sprite-box-samples.json`; `SpriteBoxSampleTests` is
the gate.

⚠️ **It was worth measuring precisely because no fixture could, and that is the
lesson rather than the number.** The box had been derived from the PNGs and the
sprite XML and it was right in every edge — but **the fixtures corroborate almost
none of it**: measured by mutation against `closedsupport`'s recorded grab,
`BoxLeft` has to move two pixels before the tick changes, and `BoxRight`,
`BoxTop` and `BoxBottom` can each move **sixteen** unnoticed. A bot approaches
from one side and never crosses the other three edges. **A green fixture is not
confirmation of a box.**

**Not modelled**, each documented at its site: the `LooseSheet` particles a
carrier sheds (they draw one or two `random(1)` per tick from the global
stream — free, because every seed the port feeds is broadcast); the
`stats[CAPS]`/`stats[POINTS]` a capture awards, which no client re-simulates;
the `lastDamageSource in (KILL_BOX, FRAG_BOX, PITFALL)` arm of `Destroy.xml`,
which returns the flag outright instead of leaving it in a pit — **six
built-in maps place a `pitfall`**, so that one is reachable and waits on the
map object; and the round ending at `global.capLimit`.


### Settled: the round, and the ten seconds after it

`Objects/Overlays/Gamemodes/CTFHUD.events/{Create, Step}.xml` and
`Scripts/GameServer/GameServerBeginStep.gml:102-179`. `Gg2.Simulation.Rounds`,
`WorldState.{GameMode, RoundTimer, TimeLimitMinutes, CapLimit, RoundDecided,
Winner, Overtime, MapChanging}`; gated by `RoundTests` and `MapRotationTests`.

- **The round lives on the HUD**, which is a naming accident of a game whose
  host is also a player: `CTFHUD.events/Step.xml` is what ends the match, and a
  dedicated server creates one too. Each gamemode has its own; only CTF and
  Invasion are transcribed.
- **`timer = global.timeLimitMins*30*60`** and `if (timer > 0) timer -= 1`. It
  **stops at zero** rather than going negative, which is what keeps
  `timer <= 0` true once reached — and the field is a `uint` on the wire.
- ⚠️ **The tie-break is not about players.** `redIsCapping` is true when
  *blue's* briefcase is not sitting on its own base — carried by anybody, or
  merely lying somewhere else. A tied match with a flag out at 0:00 goes to
  **overtime** instead of ending; a tied match with both flags home is a draw,
  spelled `TEAM_SPECTATOR` and sent as a **2**.
- ⚠️ **The winner is decided in Step and acted on in the *next* tick's Begin
  Step.** `GameServerBeginStep.gml:105` reads `global.winners` at the top of a
  frame and `CTFHUD.Step` writes it in the middle of one, so a win on tick N is
  broadcast at the start of N+1, after that tick's state update.
- **Where `Rounds.Step` sits is observable**: `CTFHUD` is younger than the
  `GameServer` (which persists across rooms) and older than every `Character`,
  so it runs after `move_all_bullets` and *before* any character's Step. That
  matters because deciding the round sets `global.mapchanging`, and the
  characters moving later in the same tick already see the gates open.

**`global.mapchanging` — the ten seconds the win is on screen:**

- ⚠️ **Every gate opens, and the two solidity lists disagree about which.**
  `charSetSolids.gml:4` wraps *both* its `IntelGate` and `TeamGate` blocks in
  `if(!global.mapchanging)`; `gunSetSolids.gml:5` wraps only its `TeamGate`
  block and leaves `with(IntelGate) solid = true` outside it. So **a bullet is
  still stopped by an intel gate** during the victory screen and a character
  walks through it. Obstacles, player walls and setup gates are untouched by
  either: the world does not open, only its doors.
- **A carrier drops the briefcase** (`Begin Step.xml:366`, the same line as the
  ubered drop) and **nobody may grab one** (the two collision events' own
  guard) — so the victory screen cannot also be a capture.
- **Everybody gets a full magazine**, `Begin Step.xml:404`, a separate block
  with the same body as the uber's a few lines above.
- ⚠️ **Nobody respawns, but the alarm is still spent.** The guard is *inside*
  `PlayerSpawn.gml`, not on `alarm[5]` — so the timer fires, resets itself and
  the player simply does not appear. That is what makes the map change's
  blanket `alarm[5] = 1` for everybody safe.

**The map change**, ten seconds (300 ticks) later: `serverGotoMap` reloads the
room, so every character, weapon, sentry and projectile is destroyed and the
`Player` instances — which are persistent — survive. On **area 1 only** their
stats are wiped and their team is reset to `TEAM_SPECTATOR`; every area arms
`alarm[5] = 1`, so the whole server spawns in that same tick's alarm stage.
A red win on a multi-area map (Invasion) advances `global.currentMapArea` and
replays the same map instead of rotating.

**Not modelled**, each documented at its site: `player.humiliated`, set on the
losing team by `ScoreTableController` — a drawing object — which suppresses the
ammo refill and drops a spy's cloak; the arena arm of that same refill, which
waits on `ArenaHUD`; the per-player stats the reset wipes, which no client
re-simulates; `botReteamAll`; and `sendLobbyRegistration`, which a headless
server skips on purpose.

⚠️ **`global.timeLimitMins` and `global.caplimit` are floored at 1**
(`game_init.gml:96, 101`, both `max(1, min(255, ...))`) — exactly like
`Server_Respawntime`. So **0 is unreachable in the reference and means "no
limit" in `WorldState`**, which is the default and what a parity replay, a unit
test and a bot rollout all get. Without that escape a zeroed `WorldState` would
be a round whose clock read 0:00 before its first tick.


### Settled: the control point, and King of the Hill

`Objects/Map elements/Gamemode-specific/CP/ControlPoint.events/`,
`CaptureZone.events/Alarm 0.xml`, `Scripts/Maps/pointCapture.gml`,
`Objects/Overlays/Gamemodes/KothHUD.events/`, and the `cappingPoint` block of
`Character.events/Begin Step.xml:429-439`. `Gg2.Simulation.ControlPoints`,
`ControlPointState`, `Rounds.StepKoth`; gated by `ControlPointTests`.

- **One object, four gamemodes.** `ControlPoint`'s `mode` is decided in
  `User Event 0` from which point objects exist: Arena, KotH, DKotH, attack/
  defend or symmetrical CP. Only KotH is transcribed so far; the others are
  arms on the same Step.
- ⚠️ **`redPresence` is three-valued, not a count.** `max(2, ...)` for an
  ordinary player and `max(1, ...)` for an ubered one, so it says *what kind* of
  presence there is. 1 means "only ubered players", who **cannot capture but do
  stop their own team's progress reverting** — the distinction is the whole
  reason it is not a bool.
- ⚠️ **A contested point is frozen, not reverting.** With both teams present the
  reference plays a sound and does nothing else: no slow revert, no stalemate
  timer. That is what makes a single body on the hill at 0:00 block a KotH win.
- ⚠️ **Reverting is faster than capturing, and comes first.**
  `capping -= (1 + cappers*0.5)` against the capture's `+cappers`, so taking a
  half-captured point costs roughly twice what taking a fresh one does.
- ⚠️ **Past two cappers the rate is halved**: `cappers <= 2 ? cappers :
  cappers/2 + 1`. Two soldiers cap at 2 a tick, four at 3. A **scout is
  `capStrength` 2** — the only class override in the game
  (`Scout.events/Create.xml:58`) — so one scout caps at the full 2 and two
  scouts cap at 3.
- ⚠️ **Locking a point discards the meter**: `capping = 0; exit;`, not a pause.
  And `if (arenaRoundEnd || global.mapchanging) locked = true` locks every point
  "regardless of game mode" — the reference's own comment.
- ⚠️ **`pointCapture` does not clear `cappingTeam`.** It writes
  `team = cappingTeam; capping = 0` and leaves the other alone; only the next
  tick's `capping <= 0` branch clears it. Both go on the wire, so the tick a
  point changes hands broadcasts a point owned *and* being captured by the same
  team.
- ⚠️ **A character is a *point* on a capture zone, not a box.**
  `collision_point(x, y, CaptureZone, 0, 0)` — `prec = 0`, so the zone's
  bounding box against the character's origin. Standing with your feet over the
  edge does not count. **A cloaked spy is not on the point, and neither is one
  still fading in** (`!cloak and cloakAlpha == 1`).
- **A zone belongs to the *nearest* point**, resolved a step after creation
  (`instance_nearest`) so a custom map's entity order cannot change the answer.
  Neither ever moves, so the port resolves it once at load.
- **`CaptureZoneS` is 42x42, origin (0, 0), `RECTANGLE` mask on `FULL`
  bounds** — the whole image, no alpha involved, which is exactly the shape
  `SolidRect.FromEntity` was measured against in phase 3 for the collider
  sprites. So the box is the phase-3 rule with a different size rather than a
  new inference. ⚠️ What is *not* measured is whether GM8 rounds the point
  before testing it; the port rounds half to even like every other position, and
  it costs at most half a pixel at the rim.

**King of the Hill**, `KothHUD.events/Step.xml`:

- **Thirty seconds of pregame** (`cpUnlock = 900`), then the hill unlocks for
  everybody at once. **Three minutes each** (`redTimer = blueTimer = 180*30`),
  counting down only while the hill is unlocked and that team owns it.
- ⚠️ **Winning needs the hill uncontested as well as held**:
  `redTimer == 0 and team == TEAM_RED and capping == 0 and bluePresence == 0`.
  A blue player standing on red's hill at 0:00 blocks the win for as long as
  they survive — and they are not capturing, because a contested point is
  frozen. That is the mode's endgame.
- ⚠️ **The counter is read and *then* decremented**, so `cpUnlock = 900` is 901
  ticks: 900 of counting down and one more to notice the zero. The
  `cpUnlock == 150` arm — a `CAPS_UPDATE` five seconds before the hill opens —
  lands on tick 751 for the same reason.
- **The host timer ends an idle round as a draw.** It runs only while the hill
  is owned, unlocked and completely quiet, which is the only thing stopping an
  empty KotH server sitting on one map for ever. In `WorldState` it is
  `RoundTimer`, the same field CTF uses for its round clock.

**`GameServer.syncTimer` is transcribed too** — an extra `CAPS_UPDATE` when a
point is captured, when a capture meter's `floor` lands on 225, 75 or 30, and
five seconds before the pregame lock lifts. ⚠️ It is an **equality on the
floor**, not a crossing: a meter moving two a tick can step straight over an odd
threshold and never ask. Bandwidth rather than correctness — but the messages
are bytes a byte diff sees.

**Not modelled**, each documented at its site: the point's `alpha`/`fade` pulse
and every sound; the per-capper `stats[CAPS]`; `cappingPower`, which is 1
everywhere except an `ArenaControlPoint` (where it scales by how many of the
capturing team are alive) and which is written out as a multiplication so Arena
has somewhere to hook in; and the CP modes' neighbour-based locking.


### Settled: Dual King of the Hill

`Objects/Overlays/Gamemodes/DKothHUD.events/{Create, Step}.xml` and the
`mode == 4` branch of `ControlPoint.events/Step.xml:12-19`.
`Gg2.Simulation.Rounds.StepDkoth`, `ControlPoints.Relock`, `Rounds.Start`;
gated by `DualKothTests`.

- ⚠️ **The clocks are crossed, and that is the whole mode.** `redTimer` counts
  down while **`KothBlueControlPoint`** is red's — you win by holding the
  *enemy's* hill. Reading it as "KotH with two points" gets every condition in
  the HUD backwards.
- ⚠️ **Both hills start owned by their own team** (`DKothHUD.events/Create.xml`,
  not `User Event 0`), which no other mode does. Without it both read neutral
  and the cross-lock — which asks who owns the *other* hill — never engages.
- ⚠️ **The cross-lock**: red's hill is locked while red holds blue's, and blue's
  while blue holds red's. So whoever is ahead cannot be counter-attacked at
  home, and only one hill is ever in play. The reference calls its own spelling
  "bruteforce because the below code won't lock the two edge cases without
  causing errors in other modes".
- ⚠️ **DKotH never sends `UNLOCKCP`, and KotH always does.** A `ControlPoint` is
  a map instance and a HUD is created by `basicRoomSetup` afterwards, so the
  points' Steps run **first**: on the tick `cpUnlock` reaches zero the DKotH
  hills clear their own lock from the `mode == 4` branch, and the HUD's
  `if (both locked)` is already false when it runs. KotH's hill is `mode == 3`
  and is excluded from that branch ("Locking is done externally in Arena mode
  and KotH"), so only the HUD can open it and the message goes out. Clients
  reach the same state either way, from their own copy of the same line — which
  is what the `cpUnlock == 150` scoreboard update exists to keep in step.
- ⚠️ **The standoff is decided by a margin, and the *lower* clock wins.**
  `abs(redTimer - blueTimer) < 30*30` is a draw; otherwise
  `if (redTimer < blueTimer) winners = TEAM_RED`. A clock only runs while its
  team holds the enemy hill, so the team that has **spent more** of its three
  minutes is the one ahead — the opposite of how a countdown usually reads.
- ⚠️ **The idle timer resets to full whenever either hill is locked**, which
  includes the whole pregame, so it only starts counting once both hills are in
  play and both are home. It fires on `hostTimer <= 0` where KotH uses `== 0`;
  the two agree, and both are transcribed as written.
- ⚠️ **Its "uncontested" clauses check the hill's *own* team's presence**, not
  the enemy's — the reference's comments say otherwise. Together with the two
  `capping == 0` clauses, which an enemy on a hill would break, it means nobody
  at all is on either hill. The comments are the misleading part.
- **Both clocks run while *either* hill is unlocked**, not both.
- ⚠️ Each hill's cross-lock reads the other's team **as of this tick**, and the
  two Steps run in index order — so blue's hill sees red's already updated and
  red's does not. Observable only on the tick a hill changes hands.


### Settled: map areas, and the entities that do not exist

`Scripts/Maps/basicRoomSetup.gml:7-26`. `Gg2.Maps.LevelMap.ForArea`; gated by
`ControlPointModeTests`.

⚠️ **A tall map can be several rounds stacked vertically, and only one area's
entities exist at a time.** The reference counts the `NextAreaO` markers, takes
each one's `y` as a boundary, and then runs `with all ... instance_destroy()`
over everything outside the slice being played. `cp_dirtbowl` writes **six**
control points into its file and plays **two**.

- **The terrain is not narrowed, only the entities.** The collision mask is one
  instance covering the whole image and survives the destroy, so every area's
  ground stays solid. What changes is which spawns, gates, points and objectives
  exist.
- **The boundaries are taken in instance order** (`instance_find(NextAreaO,
  i-2)`), which for map entities is the order the level data lists them — not
  sorted.
- ⚠️ **An entity exactly on a boundary survives in both areas.** The tests are
  `y > bottom` and `y < top`, and the `y > 0` guard beside them exists only to
  protect the objects at the origin (the HUDs, the server) from the same
  `with all`. No shipped map puts an entity on a boundary.
- ⚠️ **`GameModes.ShapeOf` must be given the narrowed map.** The shape is what a
  reader frames control-point blocks by, so building it from the unnarrowed
  entity list makes it expect six blocks where an update carries two — and the
  stream then desynchronises hundreds of bytes later with no hint of the cause.

**The port ignored areas entirely until phase 6's control points**, because
`cp_dirtbowl` is the only shipped map with more than one — and it is also the
only attack/defend map, so the mode could not be right without this.

### Settled: the control points, attack/defend, and Invasion

`Objects/Overlays/Gamemodes/ControlPointHUD.events/{Create, Step}.xml`,
`InvasionHUD.events/{Create, Step}.xml`, and the neighbour-locking branch of
`ControlPoint.events/Step.xml:20-35`. `Gg2.Simulation.Rounds.StepControlPoints`
/ `StepInvasion`, `ControlPoints.RelockNeighbour` / `CapTimeFor` /
`AssignStartingOwners`; gated by `ControlPointModeTests`.

**One HUD object, two games.** A control-point map is *attack/defend* if it
places a `ControlPointSetupGate` and *symmetrical* otherwise — and the same
entity is what turns a CTF map into Invasion, so one question answers both
(`GameModes.HasSetupGate`).

- ⚠️ **`User Event 0` starts a point locked in Arena, KotH and DKotH and
  *unlocked* in the two control-point modes.** It only shows on a point the
  per-mode rules never write to — which is exactly the neutral middle point a
  symmetrical map opens on. Starting it locked makes that opening fight
  impossible and no other mode notices.
- ⚠️ **Every point has its own `capTime`**, from a lookup table rather than a
  formula. Symmetrical (`basetime = 7*30`) is a hill — cheap at the ends, dear
  in the middle, mirrored: `1, 2, 3, 2, 1`. Attack/defend (`basetime = 6*30`) is
  a ramp — `5, 4, 3, 2, 1` from point 1, so pushing gets harder. Below five
  points the multipliers are fractional (`1.5`, `2.25`, `4.5`, `7.5`), and a map
  with **more** than five falls through every branch and keeps the default 300.
- ⚠️ **The neighbour rule is one-directional per team.** A blue point is locked
  unless the point to its *left* is red's; a red point unless the point to its
  *right* is blue's. **A neutral point is touched by neither branch**, and so is
  a blue point at the far left or a red point at the far right — the guards are
  `if (cp > 1)` and `if (cp < totalControlPoints)`, and those two are the points
  that end the match.
- ⚠️ **In attack/defend a red point is locked unconditionally**, after the
  neighbour rules. Blue defends and never takes anything back.
- ⚠️ **Attack/defend ignores the configured time limit** and always plays three
  minutes (`timeLimit = 3*30*60`). Each capture then adds **5,400 ticks**
  (`ControlPoint.events/Step.xml:222`), capped at **9,000** — the cap is there
  to stop the `uint` it is sent in overflowing, and the reference credits the
  two players who found that.
- ⚠️ **Overtime here is not CTF's latch.** It is set to 0 and re-derived every
  tick from "the clock is out and some point has a capture in progress", so it
  switches off the moment the meter empties and the round ends on that tick.
- **Symmetrical is won by holding both *ends*** — the reference tests only
  points 1 and n, so the middle can belong to anybody — and drawn on the clock.
  **Attack/defend is won by red taking the last point** and by blue surviving
  the clock.

**The setup timer**, shared by attack/defend (900 ticks) and Invasion (1,800):

- `areSetupGatesClosed()` is `global.setupTimer > 0`, and while it is true every
  `ControlPointSetupGate` is solid — to characters and bullets alike, the one
  solidity rule both lists agree on.
- ⚠️ **The round clock is restarted at 30, not at 0** — a second before the
  gates open, which is the siren. Everything above 30 is a countdown beep.
- ⚠️ **`CollisionFilter.SetupGatesClosed` had been permanently false since phase
  3.** It existed and nothing ever set it, so every setup gate on an Invasion or
  attack/defend map was open from the first tick and attackers could simply walk
  out during setup. No fixture noticed: none is on either kind of map.

**Invasion**, which the port had been running through CTF's round rules:

- ⚠️ **It is one-sided.** Red attacks blue's briefcase (the red one is scenery —
  `IntelState.Grabbable`), red wins on the cap limit, and **blue wins simply by
  running the clock out**.
- ⚠️ **Blue's win has a let-off for a red team one capture from winning**:
  `timer <= 0 and (!redIsCapping or global.redCaps+1 < caplimit)`. A capture in
  progress only holds the match open when it would be the *winning* capture.
- ⚠️ **`redIsCapping` is not CTF's "away from base"**: it is
  `!instance_exists(IntelligenceBlue) or alarm[0] > 0` — carried, or dropped
  with its return timer still running. A briefcase dropped straight off its base
  has an alarm of zero and counts as safe.


### Settled: Arena, the only mode with no respawns

`Objects/Overlays/Gamemodes/ArenaHUD.events/{Create, Step}.xml` and
`Scripts/Maps/Arena/`. `Gg2.Simulation.Arena`, `ArenaState`, the arena arm of
`ControlPoints.StepOne`; gated by `ArenaTests`.

The reference's own summary, from the top of its Create event: a round is a
fixed setup period, then the round proper — won by eliminating the enemy team
**or** by taking the control point, which unlocks a minute in — then a fixed
scoreboard period. A map ends when one team has won `caplimit` rounds or the map
timer runs out, and **only ever after a round has finished**, so a running round
always gets to end.

- ⚠️ **`spawningAllowed()` is Arena's whole shape.** It is true only in
  `ARENA_STATE_WAITING` and `ARENA_STATE_ROUND_SETUP`, so a death in the round
  proper is final — which is what makes elimination a win condition. The alarm
  still fires and is still spent; the guard is inside `PlayerSpawn`.
- ⚠️ **`cappingPower` is the one place in the game a capture rate is not 1**, and
  **the teams are crossed**: `cappingPower[BLUE]` is computed from *red's*
  numbers. `max(1, reds) / ((1 - 1/4)*livereds + (1/4)*max(1, reds))` — 1 with
  every enemy alive, 4 with none, which stops a wiped-out team stalling a round
  by hiding. `max(1, reds)` keeps an empty team from dividing by zero.
- ⚠️ **Two different powers are used in one Step.** The reverting branches take
  `cappingTeamPower`, read from whoever was already capping when the Step began;
  the capture branch takes `cappingPower[cappingTeam]` **after** assigning the
  new team. They differ on the tick a capture starts, which is every capture's
  first tick.
- ⚠️ **`cpUnlock` is frozen through the setup period** and reset again when the
  round starts, so the minute before the point opens is a minute of the *round*.
  The reset is redundant given the freeze and is transcribed anyway — it is what
  guarantees the invariant however the setup period was entered.
- ⚠️ **The team head-count has a scoping bug in the reference.**
  `if (player.object != -1 or alarm[5] != -1)` — that `alarm[5]` is *the HUD's
  own*, because a bare `alarm[]` in a plain `for` loop is `self`'s. `ArenaHUD`
  has no alarm events, so it is always -1 and the clause is always false. The
  intent was "alive or waiting to respawn"; the behaviour is "alive". Which is
  the right behaviour for elimination, and is why the bug is invisible.
- ⚠️ **A round ending does not sweep the map.** The bodies, the sentries and
  anything in the air stay for the whole scoreboard period — only the next
  restart clears them. The losing team's sentries *are* destroyed, twice
  (`serverArenaEndRound` and then `doEventArenaEndRound`, which it calls).
- ⚠️ **`respawnEveryone()` calls `PlayerSpawn()` directly, not `alarm[5] = 1`.**
  The alarm stage has already run by the time a HUD's Step does, so arming it
  would put every character in a new round a tick late — and the round starts on
  the same tick.
- ⚠️ **`ARENA_STATE_WAITING` is the one place a map can end without a round
  finishing**: an empty server whose clock runs out is a draw immediately.
- **Overtime latches**, unlike the control-point modes': set once the clock is
  out and cleared only by a fresh round.
- ⚠️ **The `if(not arenaRoundEnd)` guard around the presence scan is dead
  code.** `arenaRoundEnd` sets `locked = true` twelve lines above it and
  `if(locked) { capping = 0; exit; }` has already returned. Not transcribed,
  like `doEventReturnIntel`'s `event_user(5)`.

**Not modelled**: `serverArenaRestart`'s team balance (it moves the
lowest-scoring player of the bigger team across and broadcasts a `BALANCE`,
which needs per-player stats no phase owns); the MVP lists in `ARENA_ENDROUND`,
which the port sends as zero — framed correctly, empty in content; and
`humiliated`, set on the losing team here as it is at a map change.

### Settled: the generator, the only objective with hit points

`Objects/Map elements/Gamemode-specific/Generator/Generator.events/`,
`Scripts/Damage API/damageGenerator.gml`,
`Scripts/Events/doEventGeneratorDestroy.gml` and
`Objects/Overlays/Gamemodes/GeneratorHUD.events/Step.xml`.
`Gg2.Simulation.Generators`, `GeneratorState`, `Rounds.StepGenerator`, the
generator arms of `Projectiles.CollideOne` and `Explosions`; gated by
`GeneratorTests` and `GeneratorUpdateLayoutTests`.

A generator is a **map object** — placed by the level, older than the HUD and
older than every character — with 2,100 hit points behind a 300-point shield. It
sits still, does nothing but absorb damage, and ends the match when it runs out.
`gen_destroy` is the only shipped map that has any.

- ⚠️ **The shield is a discount, not a barrier.** `damageGenerator.gml:13` deals
  `damage * shieldResistance` to the generator's own hp **and** takes the full
  `damage` off the shield. So a covered hit still costs half its value in real
  hit points, and the whole 300-point shield is worth exactly 150 points of
  generator. Overkill is split: `(damage - shieldHp) + shieldHp*0.5`.
- ⚠️ **`isShieldRegenerating` is also the regeneration rate.**
  `Step.xml:15` is `shieldHp + isShieldRegenerating*global.delta_factor`, and
  GM8's `true` is 1 — one point a tick, ten seconds for a full shield. Not a
  typo to tidy into a named constant: there is no constant to name.
- ⚠️ **The regeneration buffer's alarm is read as a flag by the round.** Every
  hit sets `alarm[0] = regenerationBuffer` (90 ticks) and clears
  `isShieldRegenerating`, so a generator under sustained fire never regenerates
  — and `GeneratorHUD` breaks a stage tie at 0:00 by asking `alarm[0] > -1`,
  which is "hit in the last three seconds". A generator's alarm therefore has to
  rest at -1 rather than at 0, or a fresh map is permanently in overtime.
- ⚠️ **The tick the buffer expires is the tick the shield first moves.** The
  alarm stage runs between Begin Step and Step, and the regeneration is in the
  Step below it.
- **There is no attribution at all.** `damageGenerator` ends at `dealDamage`,
  which is `hp -= damage` plus a plugin hook. Nothing records who fired the last
  shot, and `GENERATOR_DESTROY` names a *team*.
- ⚠️ **The message names the team that LOST a generator, and
  `sendEventGeneratorDestroy.gml`'s own docstring says the opposite** ("the team
  who destroyed the bomb"). Its one caller passes the generator's own `team` and
  `doEventGeneratorDestroy` awards the win to the other. The comment is wrong.
- ⚠️ **`if(global.winners == -1)` guards the win**, so if both generators reach
  zero on one tick the first one to be visited decides the match and the second
  cannot overturn it. Both messages still go out.

#### The explosion

`Generator.events/Destroy.xml`, and it is the largest blast in the game:
`blastRadius = 400`, six times a rocket's.

- ⚠️ **It has no team test, no uber test and no line of sight.** Every character
  within 400 pixels — including the team that has just won — takes `hp -= 999`
  and a `motion_add` shove away from the generator's origin, scaled
  `15 - 15*(distance/radius)`. The same goes for the `with(Sentry)` loop.
- ⚠️ **It writes `hp` directly rather than calling `damageCharacter` or
  `damageSentry`**, so it skips the deathmatch invulnerability check, the plugin
  hook and — for a sentry — the permanent build-rate penalty.
- **Attribution is a two-way branch.** `instance_exists(lastDamageDealer) &&
  lastDamageDealer != player` means somebody else hurt them recently, so the
  source becomes `FINISHED_OFF_GIB` and that somebody keeps the kill; otherwise
  the dealer is cleared and the source is `GENERATOR_EXPLOSION`.
- **Rockets in range explode (`characterHit = -1; event_user(5)`) and mines
  detonate (`event_user(2)`)**, rockets first. `DeadGuy`, `Gib` and `Bubble` are
  debris or an unimplemented weapon's projectile and are not modelled.
- The port clears `Exists` **before** running the blast. In the reference the
  instance is still alive during its own Destroy event, so a rocket the blast
  sets off could `damageGenerator` the corpse — unobservable, because the
  instance is removed the moment the event returns and nothing reads its hp in
  between.

#### What can damage one

Nine collision events, four of which the port has projectiles for.

- **`Shot`, `Needle` and `BurningProjectile`**: `damageGenerator(ownerPlayer,
  other.id, hitDamage); instance_destroy();`.
  ⚠️ **None of them runs the wall test** that the same objects'
  `Collision with Character` and `Collision with Sentry` events open with
  (`gunSetSolids(); if (!place_free(x, y)) instance_destroy();`). A pellet
  standing in solid map still damages a generator it overlaps, which is the
  opposite of the rule everywhere else.
- **`Rocket`**: `damageGenerator(..., hitDamage)` and *then*
  `characterHit = -1; event_user(5)` — so unlike a sentry hit the generator is
  not the blast's direct victim and takes the splash on top at distance zero.
  A direct rocket is 25 + 30 before the shield's cut.
- **`Mine`**: `speed = 0; stickied = true`, exactly as on a sentry. It does not
  explode and is not destroyed.
- **The blast arms differ.** The rocket's `with (Generator)` has **no
  quarter-radius cutoff** and the mine's keeps the
  `1 - distance/radius <= 0.25 and other.splashThreshhold` `continue`. Since
  `splashThreshhold` is written only by `Mine.events/Create.xml`, giving the
  rocket's arm the cutoff is an *equivalent* change rather than a wrong one —
  the flag it reads can never be set on a rocket.
- **`SentryTurret` does not target generators**; its End Step calls `damageAuto`
  on a character it acquired. `Rifle`, `BladeB`, `StabMask` and `Bubble` have
  generator events of their own. ⚠️ This line used to end "and belong to weapons
  the port has not implemented", which was true when it was written and stopped
  being true in phase 5 — every weapon is implemented, hitscan reaches a
  generator through `Weapons.cs:812`, and the stab's generator multiplier is
  `Weapons.StabGeneratorFactor`.

#### The round

`GeneratorHUD.events/Step.xml`. **The round proper is not decided here** — a
generator reaching zero ends the match from its own Begin Step. This is only
what happens when the clock runs out with both still standing.

- ⚠️ **The comparison is by damage *stage*, not by hit points**: 3 above
  `maxHp*0.66`, 2 above `maxHp*0.33`, 1 below — the same three sprite ranges the
  Begin Step picks between. So a generator on 1,400 and one on 2,100 are tied,
  and a team 700 points behind can still draw. This is the one place the
  simulation cares what is drawn.
- ⚠️ **The winner is the team whose generator is in *better* shape.**
  `redStage > blueStage` means red has taken less damage.
- ⚠️ **Overtime reads the alarm of the generator that is about to WIN.**
  `if (redStage > blueStage && GeneratorRed.alarm[0] > -1)` — red ahead and
  **red's** generator being hit. So the match stays open exactly while the
  trailing team is still pushing. Reading it as "the trailing generator is under
  fire" inverts every case, which is what the first draft of the port's own test
  table did.
- ⚠️ **The tie-break cannot run on the tick the clock reaches zero.** The
  reference is `if timer > 0 { timer -= 1 } else if (timer <= 0 and ...)`, so
  the decrement and the decision are never the same tick.
- Both generators must still exist for any of it, and overtime latches: nothing
  in the method writes it back to false.

### Settled: Team Deathmatch, and a spawn you cannot be shot out of

`Objects/Overlays/Gamemodes/TeamDeathmatchHUD.events/Step.xml`,
`Scripts/Events/doEventPlayerDeath.gml:307-317`,
`Scripts/Damage API/dealDamage.gml`, and the `deathmatch_invulnerable` lines
spread across `Character.events/{Create, Begin Step, End Step, Destroy}.xml`.
`Gg2.Simulation.DamageApi`, `Rounds.StepTeamDeathmatch`,
`Deaths.CountTeamDeathmatchKill`, `CharacterState.DeathmatchInvulnerable`,
`PlayerState.DeathmatchRespawnBypass`; gated by `TeamDeathmatchTests`.

⚠️ **Team Deathmatch is what a map with no objective *is*.** `basicRoomSetup`'s
chain ends with it, so `gg_debug` is a TDM map and this port has been running it
as one since phase 0 — without the spawn protection, which is why adding the
mode changed an existing server test.

#### The round, and the kills it counts

- ⚠️ **The kills go into `global.redCaps` and `global.blueCaps`.** Team
  Deathmatch has no counter of its own, which is why **it needs no per-player
  statistics at all** to decide a round — a claim two handoffs carried and which
  turned out to be wrong. (The scoreboard's per-player stats are still missing,
  and Arena's MVP lists and the two balance routines still want them.)
- ⚠️ **Three guards on a kill, and one of them is redundant.**
  `instance_exists(TeamDeathmatchHUD)`, then `killer` (GM8's `> 0.5`, so a death
  with no killer scores nothing), then `killer != victim`, then
  `killer.team != victim.team`. A team kill scores nothing **for either side** —
  it is not a point for the enemy. The suicide guard cannot fire without the team
  guard firing too, in the reference or in the port, so removing it is an
  *equivalent* change.
- **It reads the killer's `player`**, which outlives their character, so a trade
  in which both die still credits whoever struck first.
- ⚠️ **The win condition is not in an `else`.** Unlike every other mode, the
  clock's decrement and the decision are the same statement sequence — so the
  tick a kill limit is reached is the tick the round ends.
- ⚠️ **Reaching the limit is not winning.** The three-way comparison runs
  whatever triggered it, so both teams crossing on one tick is a **draw**, and so
  is the clock running out level.
- ⚠️ **`overtime` is dead here.** `Create.xml` sets it false and nothing writes
  it again — the only mode with no overtime at all, because no capture can be in
  progress to hold a round open for.

#### The spawn protection

- ⚠️ **`deathmatch_invulnerable` is not a tick count.** It is seeded from
  `global.tdmInvulnerabilityTicks`, which `GameServerCreate.gml:88` computes as
  `seconds * 30 * 0.2` — **a fifth** of the ticks — and spent at
  `deathmatch_invuln_decay` a tick, which starts at **0.2**. The two fifths
  cancel, and neither is redundant: **moving changes the rate, not the amount**.
- ⚠️ **0.2 on spawn, 1 the moment you do anything, and never lowered again.**
  `Begin Step.xml` raises the rate on a jump (:98), a double jump (:109) and on
  `controlling` — pressing left or right (:270). The jump line sits *outside* the
  `if(not stabbing)` below it, so a spy who cannot actually jump still loses the
  slow decay. One step ends it for that whole life.
- ⚠️ **The attack key and the right-click end it outright**, and it is the
  **key** that does it rather than the shot: `Begin Step.xml:85` and `:124` sit
  beside the `event_user` calls, not inside the weapon. A scout out of ammo with
  a hot refire timer still gives it up by pulling the trigger.
- ⚠️ **Both End Step comparisons are fuzzy, and that is load-bearing.**
  `if(deathmatch_invulnerable > 0) ... -= decay; if(deathmatch_invulnerable <= 0)
  ... = 0;` — two `if`s, not an else. At the default five seconds, **150
  subtractions of 0.2 from 30 leave 7.3e-14**, which is *inside* GML's 1e-13
  comparison fuzz, so the reference clamps and a raw C# `<= 0` does not. Written
  raw, the countdown rests on a positive residue for ever and the character is
  **permanently invulnerable**. Measured by writing it raw and watching
  `TeamDeathmatchTests.ProtectionLastsFiveTimesItsTickCountWhileStandingStill`
  fail. Same family as the ubercharge's 241st tick and the rocket's tenth fading
  pass, and the *opposite* outcome to the uber's: there the residue was 7.02e-12,
  seventy times the fuzz.
- **The guard is in `dealDamage`, at the bottom of every damage path**, so a
  blocked hit still does everything *around* the hit points — `damageCharacter`
  resets `timeUnscathed` on the line after, and every caller's assist bookkeeping
  runs. The one damage path that bypasses the guard entirely is the generator
  explosion, which writes `hp -= 999` directly.
- ⚠️ **Dying while still protected respawns you on the *next tick* with what was
  left.** `Destroy.xml:37` is
  `if (place_meeting(x,y,SpawnRoom) or deathmatch_invulnerable != 0)`, and the
  arm writes `alarm[5] = 1` and `player.deathmatch_respawn_bypass =
  deathmatch_invulnerable`. So killing somebody through their spawn bubble buys
  nothing at all. The `else` arm writes the bypass back to **0**, so protection
  cannot be hoarded across a later ordinary death.
- **The `SpawnRoom` half of that guard is still not modelled** — `SpawnRoom` is
  not a map object this port knows.
- **Two clears are unreachable and are not transcribed**: `Begin Step.xml:305`
  (carrying intel) and `:436` (standing on a capture zone), both of which the
  reference annotates "in case anyone ever applies this code to another
  gamemode". A map with either of those objects is not classified Team
  Deathmatch by `GameModes.Of`, so neither can fire. `:340` (`player.humiliated`)
  is unmodelled for the usual reason.

### Settled: the scoreboard, and the two arrays behind it

`Objects/InGameElements/Player.events/{Create, User Event 12}.xml`,
`Scripts/Events/doEvent{PlayerDeath, ScoreIntel, Uber, Destruction}.gml`,
`Scripts/Maps/pointCapture.gml`, `Scripts/Maps/Arena/{resetRoundStats,
serverArenaEndRound}.gml` and `Objects/Weapons/Medigun.events/User Event 1.xml`.
`Gg2.Simulation.Scoring`, `StatBuffer`, `PlayerState.{Stats, RoundStats}`; gated
by `ScoringTests` and `PlayerStatsLayoutTests`.

Thirteen counters per player, indexed by `StatIndex`, of which **eleven reach
the wire** on every `FULL_UPDATE`.

- ⚠️ **There are two arrays and every award writes both.** `stats[]` is the
  map's scoreboard; `roundStats[]` is the Arena round's. All fourteen
  `stats[X] += n` lines in the reference are immediately followed by
  `roundStats[X] += n` with the same index and amount — no exceptions — so a
  single `Scoring.Award` writing both is the faithful transcription and removes
  the chance of forgetting one.
- ⚠️ **Only `ARENA_ENDROUND` ever reads the second array.** Outside Arena it is
  written by everything and read by nothing.
- ⚠️ **`DOMINATIONS` and `REVENGE` are vestigial in the reference.** Reset in
  four places and incremented nowhere; the real domination tracking is the
  separate `dominationKills` table, which rides in its own part of the
  `FULL_UPDATE` (one byte per *other* player) and is **not modelled** — the port
  writes it as zeroes, and its framing is already right because the count comes
  from the roster.
- **The counters are reals**, and they have to be: an assist is worth **half** a
  point and healing accumulates fractional hit points.
- ⚠️ **A half point rounds on the wire, so a client's scoreboard can read one
  point above the host's.** `write_ubyte(stats[POINTS])` rounds half away from
  zero; `ScoreTableController` draws `floor(stats[POINTS])`. That is the
  reference's own inconsistency, and it is why the `floor` is not redundant.
- ⚠️ **Serialisation order is not `StatIndex` order**: KILLS through BONUS, then
  **POINTS**, skipping DOMINATIONS and REVENGE. Healing is the one `ushort`.

#### What a death is worth

`doEventPlayerDeath.gml:30-79`, and it is scored **before** the body is
destroyed two hundred lines later — four of the six clauses read the victim's
character.

- ⚠️ **`victim.stats[DEATHS] += 1` is unconditional.** A pit, a generator
  explosion and shooting yourself all count.
- ⚠️ **The stab and medic-bonus clauses are inside `if(killer)` but *outside*
  `if(killer != victim)`.** So a medic who kills themselves with a full charge
  awards themselves the bonus point. Read as one nested block the two would
  agree; they do not.
- **Killing a carrier is a DEFENCE** (`victim.object.intel`), worth a second
  point on top of the kill.
- **An assist is ASSISTS + 0.5 POINTS** — the only fractional award in the game.

#### The other awards

| Event | Award |
|---|---|
| `doEventScoreIntel` | CAPS +1, POINTS +2 |
| `pointCapture` | CAPS +1, POINTS +2, to **every** team-mate on the point |
| `doEventUber` | INVULNS +1, POINTS +1 |
| `doEventDestruction` | DESTRUCTION +1, POINTS +1, `killer != owner` only |
| `Medigun` User Event 1 | HEALING += gross healed; POINTS +1 per 800 |

- ⚠️ **`pointCapture`'s test is `player.team == point.cappingTeam`**, not "was
  helping" — a team-mate who arrived this tick scores the same two points. And
  it is reachable with an **enemy** standing on the point: a contested point is
  frozen rather than reverting, but a meter that is already full still completes
  on the tick an enemy arrives, because the freeze is inside `Advance` and the
  completion check is below it.
- ⚠️ **Healing banks the *gross* amount**, not what actually landed — a medic
  beaming a full-health patient still scores it.
- ⚠️ **`hphealed -= HP_PER_POINT`, not `= 0`.** The overshoot carries into the
  next point. It is an `if` and not a `while`, so one tick can never bank two.
- **`hphealed` belongs to the weapon and dies with the medic's character**, so
  799 banked hit points are thrown away by a death; the stat itself survives.
- **`doEventDestruction` has no team test** — destroying a team-mate's sentry
  scores.

#### The resets, and the Arena MVP lists

- ⚠️ **A map change wipes both arrays, but only on area 1** —
  `GameServerBeginStep.gml:143`, the same `global.currentMapArea == 1` guard the
  team reset uses. A multi-area Invasion map carries its scoreboard between
  areas.
- **An Arena round start clears only the round array** (`resetRoundStats`), and
  that is the entire reason there are two.
- **`ARENA_ENDROUND` names the top three of each team by `roundStats[POINTS]`**,
  best first, five bytes each: roster index, kills, healing (`ushort`), points.
- ⚠️ **Every player on the team is a candidate, whatever they scored and whether
  or not they ever spawned.** The reference adds all of them to a
  `ds_priority` and only then trims to three, and it does not read `object`. On
  a team of three, everybody is an MVP with nothing to their name.
- ⚠️ **`redMVP[i].roundStats[HEALING] = round(...)` writes the stat back.** The
  rounding is an assignment, not a cast on the way out, so a medic's round
  healing total is permanently rounded (half to even) the moment a round ends.
  Harmless — the array is about to be reset — but it is transcribed as written.
- ⚠️ **The tie rule is still unmeasured.** GM8's `ds_priority_delete_min` is a
  heap and which of two equal keys it evicts is an implementation detail nobody
  has established. The port breaks ties by roster index, lowest first, so at
  least the answer is deterministic and a saved world reproduces it.

**Not modelled**: `ServerBalanceTeams` and `serverArenaRestart`'s balance, which
now have the `stats[POINTS]` they were waiting on but are a separate behaviour —
they move the lowest-scoring player of the bigger team across and broadcast a
`BALANCE`.

### Settled: autobalance, and the class limits it leans on

`Scripts/Team Balance/ServerBalanceTeams.gml`,
`Scripts/Maps/Arena/serverArenaRestart.gml`,
`Scripts/Classlimits/{checkClasslimits, countClassmembersExcept}.gml`.
`Gg2.Simulation.TeamBalance`, `Classlimits`, `ClassLimitBuffer`,
`WorldState.{Autobalance, BalanceNoticeGiven, BalanceCounter, ClassLimits}`;
gated by `TeamBalanceTests`.

**Two stages with a long fuse.** The first time the teams are found two or more
apart the server only broadcasts a notice; the switch itself waits until
`balancecounter` reaches `Server_Respawntime + 60`.

- ⚠️ **The counter runs every tick and the check does not.**
  `GameServerBeginStep.gml:1-2` increments it at the very top of every frame, but
  `ServerBalanceTeams` is only *called* from three places: a character's death
  (`Character.events/End Step.xml:29-31`, `with(GameServer)`, immediately after
  `doEventPlayerDeath` and inside the block that `exit`s), a player leaving
  (`GameServerBeginStep.gml:70`) and a team change
  (`processClientCommands.gml:189`). So the fuse burns continuously and the
  switch lands on whichever of those three happens next — on a quiet server a
  notice can hang unresolved indefinitely.
- ⚠️ **The counter only burns while a notice is outstanding**
  (`if(serverbalance != 0)`), so a server that has never been lopsided has it at
  zero.
- ⚠️ **Two players apart, not one.** `redteam >= blueteam + 2`, counting
  *players* and not characters — so the dead and the never-spawned count, which
  is what keeps the balance from swinging with every death. Spectators count for
  neither.
- **Teams that even out on their own cancel the notice**, resetting both the
  flag and the counter.
- ⚠️ **Only dead players move**, and the guard's shape is
  `if (player.object != -1) if (!(player.object.hp <= 0)) continue;` — so a
  player with no character is eligible, and so is one whose character is alive
  but already on no hit points. That second case is how the balance takes the
  player who has *just* been killed: `doEventPlayerDeath` destroyed their
  character before `ServerBalanceTeams` was called.
- ⚠️ **If nobody is eligible the fuse is deliberately not reset** — the next
  call retries at once.
- **Ties go to the first slot**: the comparison is a strict `<` against a running
  minimum, unlike the Arena MVP list's `ds_priority`.
- ⚠️ **Destroying the character is not a death.** No `PLAYER_DEATH`, nobody
  credited, no `DEATHS` stat — the client is told only by the `BALANCE` message.
  `alarm[5]` is re-armed **inside** the `object != -1` guard, so a player already
  waiting to respawn keeps the timer their death started.
- ⚠️ **Arena is excluded** (`!instance_exists(ArenaHUD)`) and **a decided round
  is too** (`global.winners == -1`), so nobody is moved during a victory screen.
- **`Balancer.name = player.name` uses the leftover `for`-loop variable**, not
  `balanceplayer` — so the notice names the *last player in the roster*. Cosmetic
  (the `Balancer` is a HUD object), and not modelled.

#### Arena's is a different script

`serverArenaRestart.gml` runs before everything else a restart does, and differs
in three ways that matter:

- ⚠️ **It loops** until the gap is under two, where `ServerBalanceTeams` moves
  at most one player per call. The loop counts its own local
  `teammore`/`teamless` down rather than recounting, which is what makes it
  terminate.
- ⚠️ **There is no "only dead players" guard** — everybody is about to be
  respawned anyway.
- ⚠️ **It does not arm `alarm[5]`**, because `respawnEveryone()` follows a moment
  later and a balanced player would otherwise come back twice.

#### `checkClasslimits`

Called on a team change *before* the team is announced, and on both balance
paths.

- ⚠️ **Quote is excluded from the random pick and then offered as a last
  resort.** The loop is `for (class=0; class<9; ...)` and the reference's own
  comment says why — "Quote shouldn't be randomly assigned" — but the line below
  it hands out the tenth class anyway when nothing else is free.
- ⚠️ **If even Quote is full it returns the desired class regardless**, over the
  limit. A full server stops enforcing rather than refusing.
- ⚠️ **The player asking is not counted against their own limit**
  (`countClassmembersExcept`), so a team change that keeps the class is never
  refused by the player's own presence.
- ⚠️ **The random pick is `irandom` off the shared stream**, so it advances the
  seed every client re-derives weapon spreads from. *Which* class comes out is
  free — the answer is broadcast — but the draw is not optional. A class that
  fits draws nothing, because the early return is above the list.
- ⚠️ **A zeroed `ClassLimits` means every class is full**, which would send every
  team change through the random pick. The reference's default is 255.

### Settled: the domination kill table

`Scripts/Dominations/`, `Scripts/Events/doEventPlayerDeath.gml:81-96`,
`Objects/InGameElements/Player.events/{Create, Destroy, User Event 12}.xml`.
`Gg2.Simulation.Dominations`, `WorldState.DominationKills`; gated by
`DominationTests`.

`killer.dominationKills[victim]` is **how many times that killer has killed that
victim since last dying to them** — not kills ever. The reference keeps one
`ds_map` per player keyed by the victim, where a missing key reads as zero
(`domination_kills_get`); the port keeps one 64×64 byte matrix, where an absent
entry and a zero are the same thing.

**It is simulation because it is on the wire.** Every `FULL_UPDATE` carries each
player's row, so a joining client can draw the right medals at once; after that
both sides keep their copies in step by running the same `doEventPlayerDeath`,
and nothing incremental is ever sent.

- ⚠️ **A kill wipes the victim's count of the killer** —
  `domination_kills_delete`, not a decrement. One kill back ends a domination
  outright however long it ran.
- ⚠️ **The assistant is dominating the victim exactly as much as the shooter.**
  Both go into `killersForDomination`: the killer only when
  `killer != victim`, the assistant whenever there is one. Both get the increment
  and both have their own count wiped by a kill in return.
- ⚠️ **A suicide is a no-op even without its guard.** The increment and the wipe
  address the same cell, so `[me][me]` goes up by one and is then set to zero.
  The `killer != victim` test is real but exists to keep the killer out of
  `killersForDomination`; removing it changes nothing about the table. (Proved by
  mutation.)
- **The four-kill threshold is presentation only.** `> 3` (already dominating)
  and `== 3` (this kill starts it), both read *before* the increment, decide
  whether the kill log says "domination" or "revenge"; the score table and the
  character's Draw event use the same test for medals. A headless server has
  none of them, so the port carries the constant and transcribes neither branch.
  What it owes is the counts, exactly, because a client derives its own medals
  from them.
- **`stats[DOMINATIONS]` and `stats[REVENGE]` are not this table** and are
  vestigial: reset in four places and incremented nowhere. See "Settled: the
  scoreboard".

#### Clearing, and the one place the port and the reference differ

`clearPlayerDominations(player)` zeroes both directions — the dominations that
player holds and the ones held over them.

- **Called on a team change** (`processClientCommands.gml:188`) **and on a player
  leaving** (`Player.events/Destroy.xml:8`), and on nothing else. ⚠️ **A class
  change does not clear it**, so switching class does not shake off a
  domination.
- ⚠️ **The port does not call it on a departure, on purpose.** The reference's
  tables are keyed by player *instance* and survive the roster list compacting,
  so a leaver has to be deleted out of them by hand. The port's matrix is keyed
  by **slot on both axes**, and `WorldState.RemoveSlot`'s shift overwrites every
  cell the clear would have zeroed. Proved by mutation: deleting the call changes
  nothing.
- ⚠️ **It is the only two-dimensional thing in `WorldState`, so `RemoveSlot`
  shifts it twice** — the rows with the main compaction loop, and then the
  columns inside every row. A one-dimensional renumbering leaves every column one
  place to the right of where it belongs, which would credit dominations to the
  wrong player after any disconnect.
- **The count saturates at 255** rather than wrapping. The reference stores a
  real and would keep counting, but `write_ubyte` clamps on the way out — so the
  two agree on every byte a client can see and differ only in a number nothing
  reads past 255.

### Settled: the Minigun, and a line that is worth one tick

`Objects/Weapons/Minigun.events/{Create, Begin Step, Alarm 5, User Event 1}.xml`.
`Gg2.Simulation.Weapons.{FireMinigun, WeaponBeginStep}`; gated by
`MinigunTests`.

The heavy's chaingun: one `Shot` every two ticks, eight damage, a ±7° cone and a
200-point tank that refills rather than reloading. **No recording can gate any
of it** — the Minigun calls `randomize()` and broadcasts no seed, so no client
re-derives its spread and no replay can reproduce it (docs/PARITY.md, "an
unseeded weapon caps a fixture's hp window").

- ⚠️ **The muzzle drop is outside the `lengthdir`.**
  `shotx = x + lengthdir_x(20, aim)` but
  `shoty = y + 12 + lengthdir_y(20, aim)` — twenty pixels along the aim and then
  **twelve straight down**, whichever way the heavy is looking. No other weapon
  in the game offsets asymmetrically.
- ⚠️ **The guard is two and the charge is three.** `ammoCount >= 2` fires and
  `ammoCount -= 3` pays, so a minigun on exactly 2 shoots and lands on **-1** —
  which the weapon's own Begin Step clamps to 0 the same tick. It is the only
  weapon whose shot costs more than it checks for.
- ⚠️ **The "prevent sputtering" line is above the ready-and-loaded guard**, the
  same shape as the Flamethrower's, so a heavy holding the trigger on an empty
  tank drives `ammoCount` further negative every tick.
- ⚠️ **A blocked muzzle costs the shot and nothing else.**
  `collision_line_bulletblocking(x, y, shotx, shoty)` is tested before the
  bullet is created and its `else` makes only a cosmetic `Impact`, but the ammo,
  the refire and the reload buffer below it all run regardless.
- ⚠️ **The tank rests at 201**, because the refill guard is
  `ammoCount <= maxAmmo` — the same off-by-one as the pyro's 201.8.
- ⚠️ **The reload-buffer factor is read *after* the ammo is spent**, so
  `if(ammoCount < 3) reloadBufferFactor = 2.5` is about the tank the shot left
  behind rather than the one it found. The reference keeps a commented-out
  cosine spline beside it; the shipped code is this two-valued step.
- **The bullet keeps the `Shot`'s own 8 damage** — the Minigun never touches
  `hitDamage`, unlike the engineer's Shotgun — but **overrides the lifetime to
  30**, not the `Shot`'s 40.
- **Both jitter draws happen while evaluating `createShot`'s arguments**, so
  they come *before* the `Shot`'s own `choose(0,0,1)` — the opposite order from
  the Scattergun, which passes the unjittered aim and adjusts afterwards.

#### `alarm[5] += 1`, and what it is actually worth

`Begin Step` has a third line the Flamethrower's does not:
`if(!readyToShoot and alarm[5] < 25 and !isRefilling) alarm[5] += 1`.

- ⚠️ **It is not what stops a held trigger refilling the tank.** That is the
  `alarm[5] = reloadBuffer*factor` on **every shot**, which re-arms the buffer to
  10 every two ticks and never lets it reach zero.
- ⚠️ **Measured: it is worth exactly one tick.** The alarm stage takes one off
  immediately after it, so during the ticks between `readyToShoot = false` and
  `alarm[0]` putting it back the buffer holds instead of decaying. Letting go of
  the trigger after one shot starts the refill on tick **11** against a
  `reloadBuffer` of 10; deleting the line makes it 10. One rather than two
  because the weapon's Begin Step runs after its owner's in the *same* tick as
  the shot.
- **It reads as frame-rate compensation**: the decrement is
  `-global.delta_factor` and this is a flat `+1`, so below 30 Hz it would push
  the buffer up rather than merely hold it.
- ⚠️ **`!isRefilling` is unreachable** and is transcribed anyway. For it to
  matter the weapon would have to be refilling *and* not ready to shoot, and
  nothing reaches that: a shot clears `isRefilling`, and the only thing that
  clears `readyToShoot` is a shot. Proved by mutation.
- **`Alarm 5` is `isRefilling = true` and nothing else**, the same single line
  the Flamethrower's is — and the port's alarm-5 switch had **no Minigun case at
  all** until this landed, so the alarm fired and did nothing.

⚠️ **`randomize()` is deliberately not transcribed.** It reseeds GM8's global
stream from the clock; doing that to `WorldState.Random` would make a forked
rollout diverge from its parent for a reason unrelated to the actions being
compared, and would break docs/BOTAI.md item 2's "restore to a bit-identical
state". Drawing from the existing stream is just as unpredictable to a client —
which sees no seed either way — and stays reproducible.

### Settled: the sniper's Rifle, the scope, and the only hitscan in the game

`Objects/Weapons/Rifle.events/{Create, Begin Step, User Event 1, User Event
3}.xml`, `Scripts/Misc/toggleZoom.gml`,
`Scripts/GameServer/serverToggleZoom.gml`. `Gg2.Simulation.Weapons.{FireRifle,
RifleBeginStep, Hitscan}`, `PlayerCommands.ToggleZoom`,
`CharacterState.{Zoomed, RunPower, JumpStrength}`; gated by `RifleTests`.

**No recording gates any of it** — every fixture was recorded with
`[Classlimits]` holding the bots to the classes the port had, so no sniper has
ever appeared in one.

#### The scope changes the physics

- ⚠️ **Zoom is not a key.** It is its own client command (`TOGGLE_ZOOM`), which
  is why the reference extracted `serverToggleZoom.gml` — a bot has no socket to
  send itself a packet on and no bit in the input byte to set.
- ⚠️ **It is never serialised.** A client infers it from the charge byte:
  `Character.events/User Event 13.xml:42` is
  `if(currentWeapon.t) zoomed = true; // Quick hack`. The `TOGGLE_ZOOM`
  broadcast is what actually keeps both sides in step, and it says nothing about
  *which way* — everyone flips their copy, so a client that missed one is
  inverted until the next.
- ⚠️ **`runPower` and `jumpStrength` are per-character**, not per-class.
  `toggleZoom` sets **0.6** and **6+(0.6/2)**; `Create.xml:15` and `:158` start
  them at the class values. **0.6 is one of the five ULP-off decimal literals**,
  and it appears twice.
- ⚠️ **`basemaxspeed` is NOT recomputed** — `Create.xml:159` derives it from
  `baseRunPower` once. And ⚠️ **it is an acceleration guard, not a speed cap**:
  a character's terminal speed comes from the friction balance and really is
  lower while scoped (measured 3.4 against 5.1), while what not recomputing it
  changes is whether the push is applied *at all*. A scoped sniper thrown above
  their own equilibrium by a blast keeps accelerating.
- ⚠️ **`runPower` has three writers, not one** — `toggleZoom`, the heavy's Step,
  and the spy's stab freeze (`Revolver`/`StabAnim`). Deriving it from `zoomed`
  would have been wrong the moment the heavy landed.

#### The charge

`Rifle.events/Begin Step.xml`, four lines that recompute the damage every tick.

- ⚠️ **It only runs while scoped *and* ready to shoot**, and anything else
  resets it to zero outright — there is no decay. Unscoping throws a full charge
  away instantly, and so does firing, because the shot clears `readyToShoot`.
- ⚠️ **The ramp is a square root**:
  `baseDamage + floor(sqrt(t/chargeTime)*(maxDamage-baseDamage))`. A quarter of
  the 105-tick charge is already half the 30-point bonus, and one tick alone is
  worth two. The `floor` keeps the damage an integer.
- **35 unscoped, 45 to 75 scoped**, and `hitDamage` is recomputed every tick
  rather than at the shot — which makes `toggleZoom`'s own write to it
  redundant, so this port does not transcribe that write.

#### The hitscan

`Rifle.events/User Event 3.xml`, and it is the only one in the game.

- ⚠️ **It is a binary search, not a ray cast.** The segment is halved until
  under a pixel, testing only the *near* half each round and keeping whatever it
  hit. The reference's own comment explains why: `collision_line` can test one
  object or all of them, never "some instances of some objects".
- ⚠️ **The object order decides ties and is not "nearest wins"**: `Obstacle,
  Generator, Character, Sentry, [TeamGate], IntelGate,
  [ControlPointSetupGate], BulletWall`. The inner loop stops at the first object
  *type* that reports a hit anywhere in the tested half — so a **character is
  tested before a gate**, and one sharing a sub-segment with a gate is hit
  through it.
- ⚠️ **Three kinds of instance are made invisible**, by blanking their masks for
  the duration: friendly generators, friendly sentries and **the shooter**.
  Everything else stops the bullet — **including team-mates**, who take no
  damage because of the team test in the damage arm. Body-blocking a sniper
  works, and so does standing an ubered team-mate in front of one.
- ⚠️ **The range is the damage**: `len = 1500*(hitDamage/maxDamage)`, so an
  unscoped shot carries **700** pixels and a fully charged one **1,500**. The
  ×100-then-÷100 round trip in the reference is transcribed as written — the
  result seeds a sequence of halvings.
- **A scoped shot takes 60 ticks to come back and an unscoped one 40**:
  `alarm[0] = reloadTime + 20*owner.zoomed`, GM8 multiplying by a boolean.
- **The Rifle has no magazine** (`maxAmmo = 0`), so its User Event 1 tests only
  `readyToShoot` — the shared ready-and-loaded guard would silence it for ever.
  It **is** seeded, and like the Rocketlauncher's the seed is broadcast and then
  read by nothing.

### Settled: the heavy slows down while firing

`Objects/Characters/Heavy.events/Step.xml` — **the only class-specific Step
event in the game** (the spy's End Step is the only other per-class event of any
kind). `CharacterPhysics.HeavyStep`; gated by `MinigunTests`.

`if(keyState & $10) runPower = 0.3; else runPower = 0.8;`

- ⚠️ **It reads `keyState` alone, not the down-or-pressed union**, so a click
  that arrives and releases inside one tick fires a bullet and does *not* slow
  the heavy. (Equivalent in the real tick order, because `pressedKeys` is
  cleared in Begin Step and this runs in Step — but not equivalent to anything
  calling the two out of order.)
- ⚠️ **It runs after the inherited Step**, so the rate it sets is what *next*
  tick's Begin Step accelerates with. The event's `action_inherited` comes first
  and the code action second.

### Settled: Quote's Blade, which is two weapons on one magazine

`Objects/Weapons/Blade.events/*`, `Objects/Projectiles/BladeB.events/*`,
`Objects/Projectiles/Bubble.events/*`, and the `with(BladeB)` block of
`Scripts/Physics/System/move_all_bullets.gml`. `Gg2.Simulation.Weapons`
(`FireBubble`, `ThrowBlade`, `BladeThrowDamage`), `Gg2.Simulation.Projectiles`
(`MoveBlade`, `BubbleBeginStep`, `BubbleStep`, `BladeEndStep`, `BubbleCollide`,
`ResolveBubblePair`), `WeaponState.{Replenishing, BubbleCount, BladesOut,
ReadyToStab}`, `ProjectileState.{AmmoDrained, Bubbled}`. Gated by `BladeTests`
alone — see "an unseeded weapon caps a fixture's hp window", to which the Blade
is the third entry.

**The weapon.** `refireTime = 5`, `bubbleRefireTime = 4`, `maxAmmo = 90`,
`replenishingRate = 2`, `reloadTime = 55`, `maxBubbles = 25`, `maxBlades = 1`,
`bladeLife = 15`, `bladeDamage = 17`, `depletedBladeDamage = 8`,
`lowAmmoThreshold = 30`, `highAmmoThreshold = 60`.

- **Begin Step is one line**: `if (replenishing) ammoCount = min(max(ammoCount +
  2, 0), maxAmmo)`. ⚠️ **Clamped at both ends**, where the Flamethrower's and
  the Minigun's refills are two separate branches — so a Blade rests at exactly
  90 and never at the pyro's 201.8. The guard is a flag, not a level, so there
  is no `<=` to overshoot.
- **`alarm[5]` is `replenishing = 1`**, not a reload. It is armed by *throwing a
  blade* — `alarm[5] = bladeLife`, the reference's own comment being "this isn't
  for when the blade actually dies, but the longest it could live" — so a Quote
  whose blade came home in three ticks still waits fifteen for the tank to start
  climbing.

**The left click is free.** `User Event 1` has **no ammo clause and spends no
ammo**: its only guard is `readyToShoot and bubbleCount < maxBubbles`. A Quote
can hold the trigger down for a whole round. The magazine belongs entirely to
the right click.

- ⚠️ **Unseeded.** `randomize()`, no `WEAPON_FIRE`, no seed on the wire — the
  third weapon on that list after the Minigun and the Flamethrower. It draws
  nothing at all, in fact: a `Bubble`'s Create event has no `choose(0,0,1)`, so
  unlike a `Shot` it does not even cost the shared stream one advance.
- The bubble leaves the **weapon** eight pixels along the aim at speed 10, then
  `motion_add(owner.direction, owner.speed)` — the owner's *whole* velocity,
  like a flame's.
- ⚠️ **A bubble's box is rotated by the direction it was fired in**, for ever.
  `createShot` writes `image_angle = dir` and nothing overwrites it, because a
  `Bubble` has **no `with` block in `move_all_bullets`** — which is where every
  other projectile's `image_angle = direction` lives. A 12x12 square turned 45
  degrees reports a box about 17 wide, so this decides who a bubble blocks.

✅ **Settled 2026-08-27: `BubbleS` is `0, 0, 11, 11` about origin (6, 6)** — the
whole 12x12 image, which is what `mode="FULL"` in the XML had always been read
to mean. ⚠️ **It is not sampled the way the other projectile sprites are.**
`tools/sample-rotated-box.js` measures a projectile by creating one and reading
its instance `bbox_*`, and a bubble is not something `createShot` can be handed;
`tools/sample-sprite-box.js` asks `sprite_get_bbox_*` of the sprite resource
instead, which needs no instance at all, and also records that `Bubble` declares
no `<mask>` — without which the sprite's bbox would not be its collision box.

**A bubble moves in its own Step event, and that is a phase of its own.**

```
hspeed += owner.hspeed * 0.2;  vspeed += owner.vspeed * 0.2;
motion_add(owner.aimDirection, 2);
motion_add(point_direction(owner.x, owner.y, x, y),
           -2 * arctan((point_distance(x, y, owner.x, owner.y) - 90) / 4));
speed = (speed + 2.2) * delta_mult(0.6);
```

- ⚠️ **It runs *after* every character's Step**, where `move_all_bullets` runs
  before. A `Bubble` instance is younger than the character that made it, so the
  `hspeed` it picks up a fifth of is *this* tick's, already through the friction
  and the wall tests.
- ⚠️ **The owner's position it reads is `x - hspeed`.** During the Step stage the
  reference has every character back where its own Step left it, before GM8's
  movement pass; this port folds that round trip into `CharacterPhysics.Step`
  instead. One subtraction recovers it, to within a ULP — and it feeds a steering
  force of at most two units in a projectile that is never serialised. See
  "Settled: the automatic-movement round trip".
- ⚠️ **The spring runs both ways.** The direction is measured owner-to-bubble
  and the strength is *negated*, so a bubble inside the 90-pixel ring is pushed
  out and one outside is pulled in. Easy to read backwards.
- ⚠️ **`arctan` is used as a strength, not an angle** — nothing converts it back
  from radians. Its range is (-pi/2, pi/2), so the term is at most about 3.14
  either way.
- **0.6 is one of the five ULP-high literals**, so it comes from `Gml`.
- The reference's comment says the speed "should stay around 3.3", which is the
  fixed point of the drag line *alone* — s = (s + 2.2) x 0.6 solves at 3.3.
  ⚠️ **It is not what a bubble settles at**: with the steering above it running,
  a bubble measured 1.75 after forty ticks. Do not read the comment as a
  prediction.
- **The leash is the only thing that ends a bubble early.**
  `Bubble.events/Begin Step.xml` is
  `if(distance_to_object(owner) > 160) instance_destroy();` — and it is
  `distance_to_object`, so the owner's **facing-inflated** box, two pixels wider
  on the left. Otherwise a bubble lives its `alarm[0] = 155`.

**The right click bills its owner while the blade flies.** `User Event 2` spends
nothing at the throw; it only sets `replenishing = 0`.

- `BladeB.events/End Step.xml` takes **one unit a tick** off the owner's
  magazine and counts it in `ammoDrained`; `BladeB.events/Destroy.xml` then
  charges `refireTime - ammoDrained` if the blade came home early. So a blade
  that hits something immediately costs **5** and one that runs its fifteen
  ticks costs **15**.
- ⚠️ **`ammoDrained` cannot be derived from the remaining lifetime.** Both count
  the same ticks, but the alarm stage runs before the End Step, so a blade
  destroyed by a collision in between has spent an alarm it never drank.
- ⚠️ **`image_angle` is forced to 0**, whichever way the blade was thrown — the
  one projectile in the game whose box does not follow its flight. The
  reference's own comment cites GG2 issue #230: a rotated 17x17 box gets wider
  than the Quote who threw it and sticks in the wall behind them. The
  `with(BladeB)` movement block has no `image_angle = direction` line either, so
  nothing puts it back.
- **The damage ramp is read at the throw and frozen onto the blade**: 17 above
  two thirds of the magazine, 8 below one third, linear in between. The drain
  the blade then causes does not change the damage it already carries.
- `with(shot) hspeed += owner.hspeed` — the **horizontal alone**, so a falling
  Quote does not throw the blade downwards. A component write after two polar
  ones, which re-derives the pair and snaps it.
- **The `with(BladeB)` movement block is the `with(Shot)` block with three lines
  removed**, and each absence is a rule: no gravity (a blade flies dead
  straight), no `image_angle = direction`, no `image_alpha` fade. It keeps the
  muzzle test on the first pass and the destination test on every pass, so a
  blade thrown into a wall dies in it exactly like a pellet.

**A blade's character event is not a pellet's with a bigger number.**

- ⚠️ **No `place_free` muzzle test**, so a blade that reached somebody through a
  wall still sticks in them.
- ⚠️ **The guard is two clauses, not four** — not yourself and not a team-mate.
  `ubered` is checked *inside*, around the damage alone, and `hp > 0` is not
  checked at all.
- ⚠️ **So an ubered enemy still stops the blade and is still shoved**, taking no
  damage: the `motion_add`, the `dealFlicker` and the `instance_destroy` are all
  outside the uber guard.
- The shove is `speed * 0.4` — thirteen times a pellet's 0.03, and nearly five
  units of velocity at the blade's muzzle speed of 12.

**A bubble's character event is not one either.**

- ⚠️ **A cloaked spy who could stab is invisible to bubbles.** The first two
  lines are `if(other.cloak) if (other.currentWeapon.readyToStab) exit;`, so a
  bubble passes through one without popping and without giving them away. That
  is why `WeaponState.ReadyToStab` landed with Quote rather than with the spy;
  `Revolver.events/Create.xml:23` starts it true.
- ⚠️ **No "not the shooter" clause** — the team test does that job, because a
  bubble and its owner are always on the same team.
- ⚠️ **The `alarm[0] div 2` in the middle is dead code**: both arms are followed
  by an unconditional `instance_destroy()` at the end of the event. It is live
  in the *mine* event, which has no trailing destroy.
- ⚠️ **No `timeUnscathed = 0`.** Every other damaging collision event in the
  game resets it and this one does not, so bubbles cannot hold a medic's healing
  ramp down. 0.75 damage, no knockback, no `dealFlicker`.

**A bubble has sixteen collision events, in ascending order of the other
object's id**: `Obstacle` 0, `Character` 2, `Rocket` 3, `Shot` 97, `Mine` 99,
`TeamGate` 110, `Needle` 130, `Sentry` 460, `BladeB` 465, `Bubble` 471,
`BulletWall` 478, `IntelGate` 516, `ControlPointSetupGate` 536, `Generator` 560,
`Flare` 792, `BurningProjectile` 793.

- ⚠️ **The four wall events are exactly `gunSetSolids` minus `Obstacle`** —
  `BulletWall` and `IntelGate` destroy unconditionally, `TeamGate` destroys
  unless the map is changing, `ControlPointSetupGate` destroys while the setup
  timer runs. That is what `SolidRect.IsSolidTo` already answers for a bullet,
  intel-gate trap included, so `CollisionWorld.MeetsNonObstacleSolid` reuses it.
  There is no `PlayerWall` event and a bullet's list has no player wall in it.
- ⚠️ **`Collision with Obstacle` is a wall *response*, not a wall death** — the
  only one in the game. Back out along `direction - 180` by at most `speed`, try
  a **six-pixel vertical nudge** (down first, then up, and only where the nudged
  step is itself clear), then zero whichever of `hspeed`, `vspeed` and `speed` is
  still blocked. ⚠️ **Those last three are three statements and each reads what
  the ones above left behind** — with a purely horizontal bubble the
  `hspeed = 0` line is redundant because `speed = 0` would zero the pair anyway,
  and only a bubble with a free vertical component tells them apart.
- ⚠️ **A `Shot` pops a bubble if it is an enemy's *or* if it is perseverant**, so
  a Quote's own team-mate's lucky pellet clears their screen for them. A
  `BladeB` pops one if it is an enemy's *or* if it belongs to the same player, so
  a Quote's own blade cuts through their own screen and a team-mate Quote's does
  not.
- ⚠️ **A `Mine` is reflected rather than popped** — both components negated,
  `bubbled = true`, `reflector` set to the bubble's owner — and the bubble then
  **halves its own lifetime** (`alarm[0] div 2`) or dies if ten ticks or fewer
  remain. `bubbled` makes it once only, so a screen cannot pin a mine in the air.
  Note this is *not* an airblast reflection: no alarm is re-armed.
- ⚠️ **Two same-team bubbles push each other apart at 0.5 a tick** rather than
  popping; enemy bubbles annihilate.
- ⚠️ **A `Rocket` and a `Flame` have no `Collision with Bubble` of their own**,
  so both fly straight through a bubble that dies on them. A `Flare` does have
  one — it overrides `BurningProjectile`'s absence — and so do `Shot` and
  `Needle`.
- **A bubble's sentry and generator events are the pellet's**, wall test and all
  — which is to say without one. So are a blade's.

⚠️ **A collision between two projectiles runs two events, and the port applies
both from whichever side it reaches the pair first.** GM8 fires
`Bubble.events/Collision with Shot` on the bubble and
`Shot.events/Collision with Bubble` on the pellet, and each is written on the
assumption that the other has also run — the pellet's spares a perseverant shot
and the bubble's pops for one anyway. Applying only the event of whichever
instance the port happened to walk first would make the outcome depend on which
was created first, and for the commonest case (a screen older than the pellet
flying into it) would leave every pellet alive and make `perseverant`
meaningless. `Projectiles.ResolveBubblePair`.

✅ **Measured 2026-08-27, and the port was right.**
`tools/probes/probe-bubble-pair.js`: a real `Bubble` and a real `Shot` are
created on the same point in open space — `place_meeting` between them checked,
so the overlap is not assumed — and which of them still exists is read back one
frame later. All four cells:

| bubble vs pellet | `perseverant` | bubble | pellet |
|---|---|---|---|
| enemy | 0 | **dies** | **dies** |
| enemy | 1 | **dies** | lives |
| same | 1 | **dies** | lives |
| same | 0 | lives | lives |

Exactly `bubblePops = enemy or perseverant` and `pelletDies = enemy and not
perseverant` — each instance's own condition, both applied. The **enemy/ordinary
row is the decisive one**: no single event can destroy both instances, so both
must have run. It also refutes each single-sided alternative on its own — bubble
only would leave the pellet alive in that row, pellet only would leave the bubble
alive in the other three.

⚠️ **So a projectile that destroys itself in its own collision event is still
there for the other instance's**, which is the GM8 property the whole reading
rested on. Whichever side the engine reaches first, the other still fires in the
same frame. The measurement cannot say which side goes first, and nothing
observable depends on it.

⚠️ **The same-team row needs a short window to be honest.** A `Bubble`'s Step
pushes it away from a stationary owner at about 3.3 px a tick and its Begin Step
destroys it past `distance_to_object(owner) > 160`, so a "both survive" read
taken a second later reports the bubble dead for a reason that has nothing to do
with the pellet. Read the outcome within three frames. That confound cost this
measurement one wrong answer before it was noticed.

`BladeTests.ThePelletAndBubbleTableHoldsWhicheverWasFiredFirst` carries the
table for both creation orders — the port reaches the pair through `HitBubble`
when the pellet is older and `BubbleHitProjectiles` when the bubble is, and only
the second had coverage before.

⚠️ **And the collision stage now walks a snapshot of handles rather than a cursor
over the live array.** A cursor can only express "the projectile I am looking at
died": freeing any *other* entry compacts the creation-order array underneath it
and steps clean past its neighbour. That was harmless while every collision
destroyed at most the projectile whose event was running, and Quote's bubbles
broke it — a bubble and the pellet it meets destroy each other in one call, and a
rocket's blast pops bubbles that may be *older* than the rocket. GM8 walks the
instances that existed when the stage began and skips one an earlier event
destroyed, which is what the snapshot says.
`BladeTests.AnExplosionDoesNotMakeTheStageSkipTheProjectileBehindIt` is the gate.

**Three explosions pop bubbles, and all three use a different formula.**

| Blast | Test | Radius used |
|---|---|---|
| Rocket (`User Event 5`) | `distance_to_object` | `blastRadius * 0.66` = 42.9 |
| Generator (`Destroy`) | `distance_to_object` | `blastRadius` = **400** |
| Mine (`User Event 2`) | `point_distance`, centre to centre | `blastRadius + selfPopRadius` = **44** |

⚠️ The mine's is *widened* rather than scaled, by the bubble's own
`selfPopRadius = 4` — so a bubble 42 pixels from a mine pops where a character 42
pixels away takes nothing. None of the three has a team test: a blast pops its
own side's bubbles as readily as the enemy's. `Bubble`'s `selfPop` field is set
false in its Create event and read by nothing.

**Bubbles and the blade die with their owner, silently** — `Blade.events/
Destroy.xml` has a `with` block for each, exactly as the Minegun has one for its
mines. So `WorldState.Clear` frees all three kinds.

**Not modelled**, and each is drawing: the `Pop` instance a bubble's Destroy
creates, the `Impact` a blade's wall death creates, the recoil and reload
sprites, `justShot`, and `BladeB.events/Begin Step.xml` (which sets
`image_xscale` from the sign of `hspeed` — a *drawing* flip, and the blade's
`image_angle` is pinned at 0 so it cannot reach the box through the rotation path
either).

### Settled: the spy, the cloak and the knife that arrives late

`Objects/Weapons/Revolver.events/*`, `Objects/Projectiles/{StabMask,
StabAnim}.events/*`, and the cloak block of
`Objects/InGameElements/Character.events/Begin Step.xml`.
`Gg2.Simulation.Weapons` (`FireRevolver`, `TryStab`, `StabAnimStep`,
`CreateStabMask`), `Gg2.Simulation.Projectiles` (`StabStep`, `StabConnects`,
`StabHitCharacter`, `StabHitSentry`), `CharacterPhysics.CloakStep`,
`CharacterState.{Stabbing, StabFreezeTimer}`,
`WeaponState.{ReadyToStab, StabTimer, StabReadyTimer, StabDirection}`,
`ProjectileState.{NumKills, XScale}`. Gated by `SpyTests` alone — **no fixture in
the project contains a spy.**

**The class.** `maxHp = 100`, `baseRunPower = 1.08`, `canCloak = 1`,
`numFlames = 4`. ⚠️ **The spy's own End Step is entirely drawing** — it decides
whether the client draws this spy as invisible for being behind an enemy, and
reads `global.myself`. It is the only per-class event besides the heavy's Step,
and unlike the heavy's it is *not* transcribed.

#### The cloak

`Character.events/Begin Step.xml:129-142`, between the right-click block and the
`moveStatus` switch — and **on the same key as the right click**, so one press
runs both.

```
if (!player.humiliated && (pressedKeys & $08) && canCloak
    && ((cloakAlpha == 1 and !cloak) or cloak) && !intel && !taunting)
{
    deathmatch_invulnerable = 0;
    if (currentWeapon.readyToStab) {
        if (cloak) { canGrabIntel = false; alarm[1] = max(alarm[1], 25); }
        cloak = !cloak;
    }
}
```

- ⚠️ **`$08` is `PlayerKeys.Special`, the right click** — the same bit the
  Revolver's stab arm tests for *not* being held.
- ⚠️ **`pressedKeys`, not the down-or-pressed union** — an edge, so holding the
  key does not flicker.
- ⚠️ **`(cloakAlpha == 1 and !cloak) or cloak`**: a cloak can only be *started*
  from fully visible, and can always be dropped. A spy who uncloaks waits the
  twenty ticks of fade-in before they can vanish again.
- ⚠️ **`readyToStab` gates the toggle**, so the fifty ticks after a stab are
  fifty a spy can neither vanish nor reappear. That flag belongs to the
  *Revolver*, which is why the toggle needs the weapon.
- ⚠️ **`deathmatch_invulnerable = 0` is above the `readyToStab` test**, so the
  key gives up spawn protection even when the toggle is refused — the same shape
  the attack key and the right click have.
- ⚠️ **Uncloaking costs the intel grab for 25 ticks**, and the alarm is a
  `max` — a longer cooldown already running is not shortened. The reference's own
  comment: "stop spies immediately picking up intel after uncloaking".
- An intel carrier cannot cloak at all, and ⚠️ **a carrying spy who is somehow
  cloaked is uncloaked *outright*** — `cloak = false; cloakAlpha = 1`, not a
  fade (`Begin Step.xml:328-332`, inside the `if (intel)` block that charges the
  return timer).

**The fade** is `Character.events/End Step.xml:69-72`, immediately below the map
clamps:

```
if (cloak and cloakAlpha > 0 and !cloakFlicker) cloakAlpha = max(cloakAlpha - 0.05, 0);
else if (!cloak and cloakAlpha < 1)             cloakAlpha = min(cloakAlpha + 0.05, 1);
```

- Twenty ticks each way, and ⚠️ **exactly zero at the end rather than a residue**
  — the reference clamps with `max(..., 0)` and not by comparison, so this is
  not another 1.4e-16 case.
- ⚠️ **An `else if`, so a flickering cloak does neither.** `dealFlicker` raises
  the alpha to 0.3 and sets `cloakFlicker`; the alpha is then pinned there until
  `alarm[7]` clears the flicker and snaps it back to 0.
- **`cloak` and `cloakAlpha` are not interchangeable.** `cloak` alone gates the
  intel grab, the knife and a bubble's blindness; `cloakAlpha == 1` gates only
  the capture point and the start of a new cloak.

#### The Revolver

`refireTime = 18`, `reloadTime = 45`, `reloadBuffer = 18`, `maxAmmo = 6`,
`StabreloadTime = 32`, `readyToStab = true`.

`User Event 1` is an if/else with two entirely different bodies:

```
if (readyToShoot && !owner.cloak && ammoCount > 0)          -> the bullet, seeded
else if (readyToStab && owner.cloak && !(owner.keyState & $08)) -> the knife
```

so **the attack key shoots out of cloak and stabs in it**, and there is no
separate melee input at all.

**The bullet** (`User Event 3`):

- **28 damage at speed 21** — the hardest and fastest single projectile in the
  game, against a scattergun pellet's 8 at 15.
- ⚠️ **The speed boost is a product of two speeds**:
  `with(shot) speed += owner.hspeed * hspeed / 15`, where the bare `hspeed`
  inside the `with` is the *shot's*. A spy running right at 4 adds `4 × 21 / 15`
  to a bullet aimed right and **subtracts** the same from one aimed left; a
  bullet fired straight up gets nothing.
- ⚠️ **The bullet is moved a whole step forward the moment it is made**, at the
  boosted speed, before anything can test it — the reference's comment says "to
  avoid immediate collision with a wall behind the character" and
  "delta_factor is left out intentionally". That is on top of the movement
  pass's own step, so a revolver bullet covers two on its first tick.
- **The muzzle is the weapon's own `x`** — the only weapon with no `lengthdir`
  at all — and `y + yoffset + 1`, five pixels up.
- ⚠️ **`alarm[5] = reloadBuffer + reloadTime` in one go** (63), where the
  Scattergun family arms the buffer and re-arms per round. The Revolver's
  `Alarm 5` puts **the whole cylinder** back at once and does not re-arm.
- It is seeded like every other broadcast weapon, and the seed is spent by
  `createShot`'s `perseverant` draw and nothing else.

#### The knife, which is three instances and a second and a half

⚠️ **Nothing takes damage when the button is pressed.** The stab arm creates a
`StabAnim`, freezes its owner and arms `alarm[1] = 32`; `Alarm 1` is what
creates the `StabMask` that actually hurts anybody, and it then arms
`alarm[2] = 18` — ⚠️ **through a drag-and-drop `action_set_alarm(18, 2)` that a
grep for GML code inside the event does not see at all.** So the cooldown is
**32 + 18 = 50** ticks.

⚠️ **The freeze is longer than the cooldown: 52 ticks.** `StabAnim` sets
`image_speed = 0` and `alarm[0] = 10`; the alarm sets `image_speed = 1`, so
`image_index` climbs one a tick from tick 10 and reaches the `>= 32` that sets
`done` at tick 42 (`SpyRedBackstabTorsoS` has exactly 33 frames, so it never
wraps first). From there an `alpha` of 1 falls 0.1 a tick and the instance
destroys itself when it is no longer `> 0` — the **tenth** subtraction, not the
eleventh, because 1 - 10×0.1 is 1.4e-16 and GML's `>` is fuzzy to 1e-13. The
rocket's fade in another costume. Its Destroy restores `runPower`,
`jumpStrength` and `stabbing` from the **base** values.

⚠️ **One tick of that is not certain and cannot be settled by reading**: whether
GM8 advances `image_index` before or after an instance's Step decides 42 or 41.
Measure it at the next live session.

⚠️ **`stabbing` gates the jump on its own** (`Begin Step.xml:99`), and that is
not redundant with `jumpStrength = 0`: the guard above it is
`vspeed > -jumpStrength`, which a zeroed jumpStrength *passes*, so without the
flag a falling spy would have `vspeed` **set to** -0 and stop falling.

**The port models `StabAnim` as a counter on the character**
(`CharacterState.StabFreezeTimer`) rather than an instance. It has no collision
events, no damage and no position, it dies with its owner (which
`Revolver.events/Destroy.xml` spells out), and a second stab overwriting the
counter is exactly what the reference's "do not re-allow movement if there is
another StabAnim for the same player" guard amounts to.

**The `StabMask`** is a real projectile, and an odd one:

- ✅ **`StabMaskS` is 34×43 about the origin** — 2 pixels back, **31 forward**,
  24 up and 18 down — the largest melee reach in the game. Measured 2026-08-27:
  `sprite_get_bbox_* = 24, 13, 57, 55` about origin (26, 37) on a 64x64 image,
  `object_get_mask(StabMask) = -1`. ⚠️ **This is the one late-sampled box where
  the image and the collision box genuinely differ** — 64 wide against 34 — so
  taking the reach from the sprite's dimensions would have been thirty pixels
  wrong.
- ⚠️ **`image_xscale` is ±1 from the stab's `direction`**
  (`< 90 or > 270` → +1), which mirrors the whole box — and **is also what
  decides a backstab**: `sign(other.image_xscale) == sign(image_xscale)`, the
  victim's facing against the *knife's*, which is where the spy committed fifty
  ticks earlier and not where they are looking now. Both are 200 damage; only
  the kill log and the scoreboard tell them apart.
- **Its Step is `x = owner.x; y = owner.y`** and it has no `with` block in
  `move_all_bullets` at all. ⚠️ **So it trails its spy by exactly one tick's
  velocity**: a `StabMask` is younger than every character, so the Step reads
  the reference's mid-Step `x - hspeed`, and the automatic movement then adds
  the mask's *own* speed, which is zero.
- **Six ticks of life** (`alarm[0] = 6`), and ⚠️ **two kills** (`maxKills = 2`)
  — one knife takes a medic and their patient, and the counter is bumped by the
  sentry event too.
- **The reachability test has two halves, either of which is enough**:

```
if (ownerPlayer.object != -1) with(ownerPlayer.object) connecting |= place_meeting(x, y, otherId);
connecting |= !collision_line_bulletblocking(x, y-12, other.x, other.y);
```

  ⚠️ **The overlap is the *spy's* box against the victim's, not the knife's** —
  so a spy standing inside somebody stabs them through any wall, and the line is
  only for the reach beyond arm's length. ⚠️ **The line starts twelve pixels
  above the knife and ends at the victim's *origin*, which is at their feet**, so
  it is a diagonal. `collision_line_bulletblocking` is `gunSetSolids` exactly:
  obstacles and the map, bullet walls, intel gates always, team gates unless the
  map is changing, setup gates while the timer runs, and **no player walls**.
- ⚠️ **A stab that cannot reach destroys the whole hitbox**, not one of its two
  hits — the `else` is inside the four-clause guard. Touching a team-mate, a
  corpse or an ubered enemy costs nothing at all.
- ⚠️ **A generator takes `hitDamage * 0.36`** (72) and **has no line-of-sight
  test**, which the character and sentry arms both do. The reference says why:
  "map designers might put walls behind the generator". It is the only place in
  the game a damage figure is scaled for the generator.
- ⚠️ **No `timeUnscathed = 0` and no `dealFlicker`** — the knife is the one
  character damage path with neither.

**Not modelled**, and each is drawing: the `Impact` a knife's Destroy makes, the
`Shell` ejection on `Alarm 7` and its `ejected` counter, the recoil and reload
sprites, `idle`, `StabAnim`'s sprite and alpha themselves, and the spy's own End
Step ghost.

### Settled: reading a GM8 double back to the last bit

`tools/exact-double.js`. `string_format(v, 1, 17)` is good to about sixteen
significant digits and the seventeenth is noise, which is useless when the
question is a single ULP — so every sampler that needs an exact value pulls the
double apart inside the game and puts it back together in node.

The encoder is deliberately built out of operations GM8 and IEEE agree on
exactly: a power-of-two scale, a `floor`, and `log2`. Nothing rounds, nothing
compares a small number to anything, and every chunk stays under 2^19 because
GM8's `floor()` truncates through a 32-bit integer.

```
mV = <value>;
mZ = mV * power(2, 400);              // sign and zero are tested on THIS
if (mZ == 0) -> "0 0 0 0 0"
mS = sign;  mA = |mV|;
mE = floor(log2(mA));
mN = mA * power(2, -mE) * power(2, 53);   // normalise, THEN scale
mC1 = floor(mN / 2^36);  mN -= mC1 * 2^36;
mC2 = floor(mN / 2^18);  mC3 = mN - mC2 * 2^18;
```

and back: `sign * ((c1 << 36) + (c2 << 18) + c3) * 2^(e - 53)`, assembled with
BigInt because the integer is up to 54 bits.

⚠️ **This was four copies and three of them were wrong.** Each bug loses the
last bit or the whole value, silently, in an *oracle* — so the port would have
been held to a number the game never said.

1. ⚠️ **`if (v == 0)` is GML's fuzzy comparison**, true for anything under
   1e-13. It reported a hard zero for `cos(degtorad(90))` = 6.1e-17 — which is
   exactly the value a motion sampler exists to measure. Fixed by testing the
   value scaled by 2^400, which puts any double a sampler can see far outside
   the epsilon. The two `while` normalisation loops the same copies used are
   fuzzy for the same reason and could exit with the mantissa outside [1, 2).
2. ⚠️ **`a * power(2, 52 - floor(log2(a)))` is not always an integer**, and the
   version written to fix (1) asserted in its own comment that it was.
   `floor(log2(a))` **overshoots by one** whenever `a` sits close enough below a
   power of two that `log2` rounds up — `2^53 - 1` is the smallest case, where
   it returns 53 against a true exponent of 52 — and the scaled value is then
   `M/2` for an odd mantissa: a **half**-integer, which GM8 prints as
   `"262143.50"`. Fixed by normalising to the mantissa first and scaling by
   2^53, which absorbs the overshoot: the result is `M` or `2M`, both integers,
   both under 2^54.
3. ⚠️ **`power(2, 52 - e)` overflows to infinity** for a value below about
   2^-972, because the exponent passes 1024. The same normalise-then-scale fixes
   it: the first intermediate is always in [1, 2) and the second always in
   [2^53, 2^54).

**Bugs (2) and (3) were found by writing the self-test, not by reading the
code** — the comment above the arithmetic said it was exact in every case, and
it was wrong. `node tools/exact-double.js` round-trips 20,000 swept values plus
a battery of the residues, ULP-high literals and power-of-two boundaries this
project cares about, and it **asserts that all three bugs still reproduce** in a
faithful transcription of the old encoder, so a green run means something.

None of the checked-in fixtures is affected: every row of the three older ones
was audited when (1) was found, and none holds a value that can reach (2) or
(3). ⚠️ **But nothing may be regenerated with an old copy** — and there is no
longer an old copy to reach for.

**The lesson worth more than the bugs**: four copies of a twelve-line function
drifted into three different states of wrong, and the one everybody trusted was
the one whose comment claimed the most. A tool that produces an oracle needs a
gate as much as the simulation does.

### Settled: the spawn room and the healing cabinet

`Objects/Map elements/Spawnroom objects/`, `Character.events/Destroy.xml:37`,
`Character.events/Collision with HealingCabinet.xml`,
`Character.events/End Step.xml:104`, `processClientCommands.gml:84` and
`serverBuildSentry.gml`. `Gg2.Simulation.SpawnRooms`,
`Gg2.Simulation.Cabinets`, `CollisionWorld.{SpawnRooms, Cabinets,
PointInSpawnRoom, MeetsSpawnRoom, MeetsCabinet}`, `CharacterState.OnCabinet`.
Gated by `SpawnRoomTests`.

**`SpawnRoom` is the emptiest object in the game**: no events at all, invisible,
not solid. It exists only to be asked about. `sprite64` is 42x42, origin (0, 0),
a `RECTANGLE` mask on **FULL** bounds — so its rectangle is
`SolidRect.FromEntity`'s phase-3 rule with a different size, exactly as
`CaptureZoneS` is, and not a new inference.

⚠️ **Every spawn point on every map is inside a spawn room.** Checked on
`ctf_truefort`, `koth_valley`, `gg_debug` and `cp_dirtbowl`, and asserted by
`SpawnRoomTests.EverySpawnPointIsInsideASpawnRoom`. That is what turns both
rules below from corner cases into **the commonest thing that happens in a
match**: picking a class the moment you spawn.

⚠️ **The reference asks about a spawn room in two different ways and they do not
agree.**

| Caller | Test | What it is |
|---|---|---|
| `processClientCommands.gml:84` (class change) | `collision_point(x, y, SpawnRoom, 0, 0)` | the **origin**, at the feet |
| `serverBuildSentry.gml` | `collision_point(x, y, SpawnRoom, 0, 0)` | the **origin** |
| `Character.events/Destroy.xml:37` (respawn) | `place_meeting(x, y, SpawnRoom)` | the whole **box** |

So there is a band about thirty pixels deep above every room's top edge where a
character is *outside* it for a class change and *inside* it for the respawn: a
class change there kills you **and** brings you back next tick. Transcribed as
written, and gated by `ThePointAndTheBoxDisagreeAboveARoom`.

**The fast respawn.** `Character.events/Destroy.xml:37-46` is

```
if (place_meeting(x, y, SpawnRoom) or deathmatch_invulnerable != 0)
{ player.alarm[5] = 1;  player.deathmatch_respawn_bypass = deathmatch_invulnerable; }
else
{ player.alarm[5] = global.Server_Respawntime;  player.deathmatch_respawn_bypass = 0; }
```

The port already had the second half (see "Settled: Team Deathmatch"); the spawn
room is the first. ⚠️ **Neither arm is guarded by the respawn time** — the
reference writes `alarm[5] = 1` outright — so a `RespawnTime` of 0 does not stop
it. The parity replayer is protected instead by spawning nobody at all.

⚠️ **A death in a spawn room is still a death**: the message, the kill credit
and the `DEATHS` stat all happen. Only the timer changes.

**The silent class swap.** `processClientCommands.gml:84`'s `else` arm is a bare
`instance_destroy()` — ⚠️ **no `PLAYER_DEATH` at all**, no kill credit, no death
stat. The Destroy event still runs, so the replacement is one tick away. **The
port broadcast a death there for six phases**, and two server tests asserted it;
both were wrong, and both now assert the swap.

**`HealingCabinet`.** `sprite74` is 32x48, origin (0, 0), `AUTO` bounds — and
its alpha covers the whole image, so AUTO and FULL agree and it is the same
rule again. It carries three `BOOL` properties: `heal` and `refill` default
true, `uber` defaults false. ⚠️ **`readProperty.gml`'s BOOL arm accepts only the
exact strings `"true"` and `"false"`** — "TRUE", "1" and "yes" all silently take
the default.

The character's collision event, in order:

- The whole thing is inside `if (!global.mapchanging)`. ⚠️ **A cabinet is the
  one map object that switches *off* with the round** rather than opening up,
  where every gate opens.
- `if ((hp < maxHp or nutsNBolts < 100 or !canEat) and other.heal)`: outside
  Team Deathmatch it fills the bar **and** the nuts and bolts at once; inside
  it, ⚠️ **ten hit points a second and nothing else** — a TDM engineer gets no
  build resource from a cabinet at all. `canEat` is the heavy's sandvich, which
  is not modelled and is always true.
- ⚠️ **The extinguish is outside the `heal` guard** — and outside the ammo guard
  — so a cabinet a map has switched everything off on still puts a burning
  character out.
- `if (other.refill)`: fills the magazine, and for the Rocketlauncher,
  Scattergun, Shotgun, Revolver and Medigun cancels `alarm[5]`.
  ⚠️ **The `event_perform(ev_alarm, 5)` before that cancel is a no-op**: each of
  those five bodies either guards on `ammoCount < maxAmmo`, which the line above
  just made false, or (the Medigun's) assigns the maximum again. The reference's
  own comment on it is "stop the animation". Only the cancel is observable.
- `if (other.uber and currentWeapon == Medigun)`: ⚠️ **hands over a *finished*
  ubercharge**, not progress towards one, and only to a medic who is neither
  holding one nor spending one. No shipped map switches it on.

**`onCabinet`** is `place_meeting(x, y, HealingCabinet)` written in
`End Step.xml:104` — the reference's own comment is "for things polling whether
the character is on a medcabinet", and exactly one thing polls it: a sentry
build refuses. ⚠️ **It is a tick behind by construction**, because a build is a
client command processed outside the tick, and that is the reference's behaviour
rather than a shortcut here.

**Two of `serverBuildSentry`'s preconditions were missing and are not any more**:
`collision_point(x, y, SpawnRoom, 0, 0)` and `!player.object.onCabinet`. An
engineer could previously build where the reference refuses — including *in
their own spawn room*, which every spawn point is inside.

⚠️ **No recording can gate any of this.** A spawn room and a cabinet are
invisible on the wire: there is no message for either, and what they do — a
respawn timer, an absent death, a full hp bar — looks in a state update exactly
like the same thing happening for another reason.

**Not modelled**, and each is presentation or a feature the port does not have:
the cabinet's sounds, `canEat`/`alarm[6]` (the sandvich), and the `ejected`
shell counter the Revolver's alarm-5 body resets.

### Settled: the three boxes that kill you, and the flag they send home

`Character.events/Collision with {KillBox, FragBox, PitFall}.xml`,
`Sentry.events/Collision with {KillBox, FragBox}.xml`, and
`Character.events/Destroy.xml`'s
`lastDamageSource in (KILL_BOX, FRAG_BOX, PITFALL)` branch.
`Gg2.Simulation.KillBoxes`, `CollisionWorld.KillBoxes`, and the return arm in
`Deaths.Kill`. Gated by `KillBoxTests`.

⚠️ **What the shipped maps actually place, scanned across all 22**: `tdm_mantic`
places **six pit falls**, and nothing else places anything at all. The project's
notes carried this for several sessions as "six built-in maps place a
`pitfall`" — it is six *boxes* on *one* map, and `OnlyTeamDeathmatchManticPlaces-
AnyBoxes` asserts it so the claim cannot rot again. A `killbox` and a `fragbox`
are reachable only on a custom map.

**All three objects are empty**: no events, invisible, not solid, and all three
on `sprite64` — the same 42x42 collider a `SpawnRoom` uses, origin (0, 0), FULL
bounds. So the rectangle is `SolidRect.FromEntity`'s phase-3 rule again.

**The character event, which all three share:**

```
hp = 0;
if (instance_exists(lastDamageDealer) && lastDamageDealer != player)
    lastDamageSource = <FINISHED_OFF, or FINISHED_OFF_GIB for a frag box>;
else
    lastDamageSource = <KILL_BOX / PITFALL / FRAG_BOX>;
```

- ⚠️ **A box does not kill — it sets `hp = 0`** and the victim's own End Step
  does the rest, later in the same tick, through the path any other lethal
  damage takes. That is what makes the kill log, the scoring and the respawn all
  behave normally.
- ⚠️ **Somebody else's claim survives the box.** If anyone but the victim hurt
  them recently the kill is handed over, and the dealer is **not** cleared — so
  they keep the credit and the box only names itself when there is nobody to
  hand it to.
- ⚠️ **A frag box hands it over as a *gib* and the other two as a plain
  finish-off.** That, and the frag box's explosion sound, are the entire
  difference between the three events.

**The sentry events are two, not three.** ⚠️ **A pit fall has no
`Collision with PitFall` on `Sentry` at all**, so an engineer can build in a pit
and the gun survives what its owner does not. The other two are
`hp -= 999; lastDamageDealer = noone;` — a raw subtraction rather than
`damageSentry`, so no build-rate penalty and no credit to anybody, the same
shape as the generator explosion's.

**The flag-return arm.** `Character.events/Destroy.xml`, inside `if (intel)` and
below the implicit drop:

```
if (global.isHost and (lastDamageSource == KILL_BOX || FRAG_BOX || PITFALL))
{ doEventReturnIntel(otherTeam); sendEventReturnIntel(otherTeam); }
```

- **Drop first, then return** — the flag lands where the body fell and is then
  picked back up and sent home. Both happen, in that order.
- ⚠️ **The return is broadcast where the drop deliberately is not** (see
  "Settled: the intel"), which is why the port does it in `Deaths.Kill` — which
  has the events — rather than in `WorldState.Clear`, where the drop lives.
  Only a death can carry one of these three sources anyway, since a box zeroes
  hit points the moment it is touched.
- ⚠️ **A box kill that somebody else took the credit for leaves the flag in
  the pit**, because the box wrote `FINISHED_OFF` rather than naming itself.
  `AStolenBoxKillStillLeavesTheFlagInThePit` is the control.
- The port reads `lastDamageSource` as the reference does rather than the death
  message's source. **Those are the same value today** — `Deaths.Resolve` copies
  the field verbatim — so swapping them is an *equivalent* mutation, confirmed
  by the audit. It is written the reference's way so that it stays correct if
  `Resolve` ever starts transforming the source.
- ⚠️ **This arm is reachable on no shipped map**: the only map with boxes is
  Team Deathmatch and has no intel. It has been transcribed-and-unreachable
  since phase 6 and is now merely unreachable-by-map.

**Where they sit in the collision stage.** These are *character* collision
events, so GM8 keys them by the other object's id: `IntelligenceBase*` 104/105,
`HealingCabinet` 126, `Intelligence*` 133/134, then `KillBox` 506, `FragBox` 509,
`PitFall` 794. ⚠️ **That order is observable in exactly one place** — a character
standing in a pit *on* a briefcase grabs it and then dies, which is what makes
the return arm reachable at all.

**Not modelled**: the frag box's explosion sound, and `SentryGibs`' own two
collision events (the debris is presentation). The `MoveBox{Up,Down,Left,Right}`
family in the same folder is a separate object with a separate purpose — see
below.

### Settled: the four boxes that shove you

`Character.events/Collision with MoveBox{Up,Down,Left,Right}.xml`,
`MoveBox.events/{Create, User Event 1}.xml`. `Gg2.Simulation.MoveBoxes`,
`CollisionWorld.MoveBoxes`, and one line in `MatchSimulation`'s collision stage.
Gated by `MoveBoxTests`.

**All four events are two lines**, identical but for the component and the sign:

```
moveStatus = 0;
if (global.run_virtual_ticks) hspeed += other.pushPower;
```

- ⚠️ **It accelerates, it does not set.** The write is `+=` and the event fires
  on *every* tick the boxes overlap, so a column of them gains five units of
  speed a tick without limit — which is what makes them lifts rather than
  conveyor belts. A port that wrote `vspeed = -pushPower` would give the same
  answer on the first tick and a very different one on the fourth;
  `StandingInABoxKeepsAccelerating` is the control.
- ⚠️ **`moveStatus = 0` is outside the `run_virtual_ticks` guard**, and it is
  the half that matters at any frame rate: it puts a character still flagged as
  rocket- or mine-jumping back on ordinary friction and control, so a move box
  **cancels a knockback** as well as adding to it. `run_virtual_ticks` is always
  true at 30 Hz, so the guard itself is not transcribed.
- ⚠️ **The push is a *component* write**, so it takes `Motion`'s component path
  and re-derives `direction` and `speed` with the snap — a character shoved
  straight up out of a standstill ends with `direction` exactly 90 and `speed`
  exactly 5. A port that wrote the polar pair directly would agree on the
  components and disagree everywhere the pair is read.
- **`pushPower` is per-instance.** `Create` starts it at 5 and `User Event 1`
  overrides it from a `speed` property — `readProperty(..., REAL, pushPower)`, a
  REAL rather than a BOOL, so it accepts any number the map writes and falls
  back to 5 for anything unparseable.

**The collider is `sprite64` once more** — 42x42, origin (0, 0), FULL — so the
rectangle is `SolidRect.FromEntity`'s phase-3 rule, the same one the spawn room
and the three killing boxes use.

⚠️ **What the shipped maps actually place, scanned across all 22**: `tdm_mantic`
alone, **nine up and six down**, none left or right and not one overriding the
default power. The same map that places the six pit falls, and the only one that
places anything of either family. `OnlyTeamDeathmatchManticPlacesAnyMoveBoxes`
asserts the counts so the claim cannot rot — the killing boxes' equivalent claim
had been wrong in this project's notes for several sessions. The left and right
arms and the `speed` override are reachable only from a hand-built entity, which
is what `MoveBoxes.Of` is a seam for.

⚠️ **Two boxes stacked can cancel each other exactly, and `tdm_mantic` has such
a pair.** A character's box is 34 pixels tall against the box's 42, so one
standing in the middle of a lift can overlap a *down* box beside it — five up
plus five down is zero, not a lift. That is not a bug in either the map or the
port; it is what `+=` composition means. It cost this session a test failure
that looked exactly like the push never running, and
`ATickLiftsWhoeverIsStandingInAnUpBox` now picks an unambiguous box on purpose.

**Where they sit in the collision stage.** The four object ids are 756, 758, 759
and 761, which puts them between `FragBox` (509) and `PitFall` (794) in a
character's collision-event order. ⚠️ **None of that interleaving is
observable**: the killing boxes write `hp` and `lastDamageSource` and these write
the velocity pair and `moveStatus`, and two boxes pushing the same character
compose by addition, which does not care about order — the final component pair
is the same either way and `direction`/`speed` are a pure function of it. So the
port runs the whole family after the killing boxes and the ordering is free.

**No recording can gate any of it**: no fixture is on `tdm_mantic`, and none can
be until a recording is made there. `MoveBoxTests` is the whole gate, and its
sixteen mutants are all caught.


### Settled: what a departure destroys

`Player.events/Destroy.xml`, reached from `Scripts/Misc/removePlayer.gml` and
`GameServerBeginStep.gml:68`. `Gg2.Simulation.WorldState.RemoveSlot`, gated by
`RemoveSlotTests`.

⚠️ **The event's first line is invisible to every code-extracting grep of the
reference tree.** GM8 stores an event's code inside
`<argument kind="STRING">`, and this one begins on the *same line* as the
opening tag, so anything that filters out lines starting with a tag returns a
Destroy event that begins at `ds_map_destroy(rewards)`. The line is:

```
if(object != -1) with(object) instance_destroy();
```

That is the third time this project has been caught by the event XML — after a
`Bubble`'s three `action_kill_object` events and the Revolver's
`action_set_alarm(18, 2)`, both drag-and-drop actions. This one is ordinary
code. **When an event looks like it is missing its first statement, read the raw
XML.**

**So a departure is two rules, in this order:**

1. **The character is destroyed** — the *whole* of
   `Character.events/Destroy.xml`, not only its intel drop. The weapon, the
   mines the Minegun takes with it, Quote's blade and bubbles, the knife hitbox,
   the medic's beam, the flag. The `object != -1` guard is this port's `Alive[]`:
   a player who is dead or has never picked a team has no character to destroy
   and no flag to drop, since a carried briefcase lives on the character.
2. **The player sweeps what is left in the air:**

   ```
   with(Rocket)            if(ownerPlayer == other.id) instance_destroy();
   with(BurningProjectile) if(ownerPlayer == other.id) instance_destroy();
   with(Shot)              if(ownerPlayer == other.id) instance_destroy();
   with(Needle)            if(ownerPlayer == other.id) instance_destroy();
   with(Sentry)            if(ownerPlayer == other.id) instance_destroy();
   ```

⚠️ **This runs the opposite way from the intuition, and the port had it wrong
for four phases.** A projectile's `ownerPlayer` is a *player* and not a
character — which is exactly what lets a rocket outlive its shooter's **death**
and still credit the kill. The moment the player is **gone** there is nobody
left to credit, so the same rocket is destroyed. Until 2026-08-26 `RemoveSlot`
dropped the briefcase and nothing else, so a soldier could disconnect and still
kill somebody a second later, and a leaving demoman's mines stayed armed.

**Which of the nine projectile kinds each path takes:**

| Kind | Belongs to | A death (`Clear`) | A departure (`RemoveSlot`) |
|---|---|---|---|
| `Mine` | the Minegun | destroyed | destroyed |
| `Blade`, `Bubble` | the Blade | destroyed | destroyed |
| `Stab` | the Revolver | destroyed | destroyed |
| `Rocket` | the player | **survives** | destroyed |
| `Shot`, `Needle` | the player | **survives** | destroyed |
| `Flame`, `Flare` | the player | **survives** | destroyed |

`Flame` and `Flare` both declare `<parent>BurningProjectile</parent>` and a GM8
`with` walks an object's descendants, so one line covers both. `Needle` has no
parent, which is why the reference's list is four lines and not three.

⚠️ **Listing the weapon-owned four in the player's sweep as well is a proved
equivalent mutant** — measured, not argued: the two sets are unreachably
disjoint by the time the sweep runs, because owning one of those four requires a
weapon, and the only way to still hold one after step 1 is to have had no
character, in which case the character that owned them was cleared earlier and
took them then. The mutation that sweeps all nine kinds survives the whole
suite. The port writes it the reference's way so the two rules stay
distinguishable.

**The sentry needs no transcription.** A sentry lives in its owner's own roster
slot, so `RemoveSlot`'s compaction overwrites it. That is true by construction
rather than by intent, so `ADepartingPlayersSentryGoesWithThem` asserts it — a
later change that moved sentries out of the slot array would otherwise break it
silently.

⚠️ **Two false leads worth not re-walking.** `Character.events/Destroy.xml`'s
`with(bubble) instance_destroy()` is **not** about Quote: `Create.xml:128` sets
`bubble = instance_create(0, 0, SpeechBubbleO)`, the chat bubble, and it is
presentation. And `removePlayer.gml` destroys only the player instance — the
character comes down through the hidden first line above, not through any orphan
guard, and there is no `instance_exists(player)` check anywhere in `Character`'s
events.

`with(DeathCam)` and `with(ghost)` are the client's, and `rewards`/`badges` are
presentation. `clearPlayerDominations` is deliberately not transcribed and is
documented in `RemoveSlot` itself: the reference's tables are keyed by player
instance and survive the roster compacting, where this port's matrix is keyed by
slot on both axes and the shift overwrites every cell the clear would zero.

**No recording gates any of this.** `reference-ctf_truefort-combat.trace` is the
only fixture with a `PLAYER_LEAVE` in it, and nothing that player owned was in
the air when they went.
