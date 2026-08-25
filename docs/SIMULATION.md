# The simulation contract

The wire protocol is the easy half. This document is the hard half: what the
ported simulation must reproduce, and — just as important — what it is free to
throw away.

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
if hp <= 0: death
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
2. **How an alarm assigned a fractional number of ticks rounds.** GM8 stores
   alarms as integers — it has to, or an alarm set to 30.45 would step past zero
   and never fire — and `Gg2.Simulation.Timers.SetAlarm` rounds half to even
   because that is this codebase's default for every GML-side rounding. It has
   not been measured. It is reachable: the scattergun sets a pellet's lifetime
   to `35` times a factor in `[0.5, 1]`, so every shot from a moving scout
   assigns a fraction. The cost of being wrong is one tick of a pellet's maximum
   range, which no fixture reaches.

Both are answerable with `gg2-agent`'s live bridge. Do that before building on a
guess.

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

`tests/Gg2.Tests/Fixtures/physics-samples.json` is 48 whole trajectories — three
classes, two maps, 48 ticks each — driven inside the running game from start
states chosen exactly, with position and velocity read back to the last bit. The
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

**One trap that cost this session an hour, and which the other samplers still
carry.** The mantissa encoder in `sample-rng.js`, `sample-physics.js` and
`sample-primitives.js` opens with `if (v == 0)` — and *that is GML's fuzzy
comparison*, true for anything under 1e-13. It reports a hard zero for
`cos(degtorad(90))`. `sample-motion.js` has the fixed version, which scales by
2^400 before testing the sign or the zero and has no normalisation loops at all.
Every row of the three older fixtures was audited and none took the fuzzy
branch, so those oracles are sound — but port the fix across before regenerating
any of them.

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
- After firing it rests at 0, not -1, and is consumed on the following tick.
  That is what makes it fire once.

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

**`image_angle` is the aim; `direction` is where the pellet actually goes.**
`createShot` sets both to `owner.aimDirection`, and then the Scattergun scatters
`direction` by `random(15)-7.5` and leaves `image_angle` alone. `Shot` has no
Step event, so nothing writes it again. Since a projectile's bounding box is the
rotated sprite's (see "Settled: a rotated sprite's bounding box"), **all six
pellets of one blast carry the same box shape while flying along six different
headings**.

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

**A `Shot` has no Step event and does not collide with walls in flight.** Its
whole per-tick behaviour is GM8's automatic movement. The only `place_free` is
the first line of its collision events, bracketed by `gunSetSolids` — so a pellet
that reaches a character *through* a wall dies at that moment having dealt no
damage, and a pellet fired into a wall with nobody behind it sails on until its
alarm expires. Do not "fix" this.

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
`maps.json`; the two that place bullet walls, `arena_lumberyard` and
`koth_harvest`, carry no properties at all. The port treats every bullet wall as
permanently closed, which is exact for the shipped maps and wrong for a custom
map that uses the trigger.
