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
- Server-side RNG. `random()` and `choose()` appear in spawn-team selection,
  chat bubbles and particle emission. None of it is shared with client
  prediction — the server decides and broadcasts the result — so the port may
  use any RNG it likes. This removes a whole class of determinism worry; do not
  invent a GM8-compatible RNG.
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
  fixed 30 fps they are plain tick counters.
- **`speed` and `direction` are derived from `hspeed`/`vspeed`** and writing
  either rewrites both. The GML reads `speed` in a couple of places
  (`End Step`'s stair descent, the intel trail); that is `hypot(hspeed, vspeed)`.

## What is genuinely uncertain

Be honest about these in the parity harness rather than assuming:

1. **Instance ordering under heavy churn** (projectiles created and destroyed in
   the same tick).

That is the only one left, and it is answerable with `gg2-agent`'s live bridge.
Do that before building on a guess.

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
| `0.1` `0.2` `0.45` `0.54` `0.9` `1.05` `1.4` `8.3` `0.195` | same | same | |

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
