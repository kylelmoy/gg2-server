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
- The automatic-movement dance. GM8 adds `hspeed`/`vspeed` to `x`/`y` between
  the Step and End Step events, so `Character.events/Step.xml` ends with
  `x -= hspeed; y -= vspeed;` purely to cancel it. **In C# you apply the
  movement once and delete both lines.** Any GML that looks nonsensical is
  usually this kind of thing.
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
mask bounds relative to its origin (`Character.events/Create.xml:20-23`). Two
samples, and the rest are in `Sprites/**/<Class><Team>S.xml`:

```
scout:  left -6  right +6  top -10  bottom +23     (origin 30,40; bounds 24,36,30,63)
heavy:  left -9  right +9  top -12  bottom +23     (origin 19,40; bounds 10,28,28,63)
```

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

## GM8 semantics that will bite

Collected because each of these has already cost someone real time; several are
recorded in `gg2-agent/GML.md`.

- **Automatic movement.** Described above — GM8 adds velocity to position
  between Step and End Step. `x -= hspeed` at the end of a Step event means "undo
  the engine's move", not "move backwards".
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

1. **Precise-mask collision against a 6×-scaled sprite.** GM8's exact
   rasterisation rule at the boundary (which world pixels a mask cell covers, and
   whether the test is inclusive at the edge) has not been verified against the
   engine here. It is very likely `floor(worldX / 6)`, but *verify it* — a
   half-open interval error puts every wall one pixel out and every wall-run
   subtly wrong.
2. **Whether `sprite_add` sprites are precise in this GM8 build.** The signature
   is `sprite_add(fname, imgnumb, removeback, smooth, xorig, yorig)` — there is
   no `precise` argument, so the default applies. Confirm empirically.
3. **Instance ordering under heavy churn** (projectiles created and destroyed in
   the same tick).
4. **How Faucet Networking's `write_ushort`/`write_ubyte` convert a fractional
   real.** The client sends `point_direction(...) * 65536 / 360` and
   `min(255, point_distance(...) / 2)` without rounding either first
   (`Scripts/Message Builders/ClientInputstate.gml`), so the extension decides.
   This does **not** affect wire compatibility — inbound aim is carried verbatim
   and decoded exactly as `netAimDirection * 360 / 65536` — but it decides
   whether a ported bot aiming at a given angle produces the same byte a human
   would. Only matters once bots aim; `Gg2.Protocol.PlayerInput.FromAim` says so
   at the call site.

All four are answerable in an afternoon with `gg2-agent`'s live bridge. Do that
before building on a guess.
