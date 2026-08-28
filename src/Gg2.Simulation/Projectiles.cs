using System.Numerics;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The two tick phases that belong to projectiles rather than to characters:
/// GM8's automatic movement, and the collision events that follow it.
/// </summary>
/// <remarks>
/// <b>These are separate phases and not one loop, because the engine's are.</b>
/// Game Maker moves <em>every</em> instance and only then runs collision events,
/// so a pellet that would pass through a character mid-step still collides with
/// wherever it lands, and two pellets that reach the same victim on the same
/// tick both do so from their post-move positions. Interleaving move-and-check
/// per projectile is a different simulation.
/// <para>
/// ⚠ <b>A projectile's per-tick behaviour is not in its object.</b>
/// <c>Objects/Projectiles/Shot.events/</c> holds a Create, an Alarm 0 and four
/// collision events and nothing else — but that is not the whole of a
/// pellet's life. <c>Scripts/Physics/System/move_all_bullets.gml</c> is, and it
/// is reached from <c>Objects/Networking/GameServer.events/Step.xml:16</c>.
/// This repo read the object and concluded a pellet had no gravity and no wall
/// test; both conclusions were wrong, and <see cref="Move"/> is the corrected
/// transcription.
/// </para>
/// </remarks>
public static class Projectiles
{
    /// <summary>
    /// What a <c>Shot</c>'s <c>vspeed</c> gains each tick.
    /// </summary>
    /// <remarks>
    /// <c>move_all_bullets.gml</c>, <c>with(Shot)</c>. A plain literal rather
    /// than a <see cref="Gml"/> constant: 0.15 is not one of the five decimal
    /// literals GM8 parses a ULP above the IEEE nearest, which are 0.6, 0.85,
    /// 1.15, 0.65 and 0.35.
    /// <para>
    /// The reference's own bot code states the same law twice, as the flight
    /// model it solves against — <c>Scripts/Bots/botAimSolve.gml:13</c> and
    /// <c>Scripts/Unit tests/botaim/test_botaim.gml:11</c>, "vspeed += gravity,
    /// then move".
    /// </para>
    /// </remarks>
    private const double ShotGravity = 0.15;

    /// <summary>
    /// What a <c>Needle</c> falls at: <c>vspeed += 0.2</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not a pellet's 0.15.</b> <c>move_all_bullets.gml</c>'s
    /// <c>with(Needle)</c> block is otherwise line-for-line the
    /// <c>with(Shot)</c> block, and this one constant is the entire difference —
    /// which is exactly the kind of near-duplicate that gets "simplified" into a
    /// shared body with the wrong number in it. A needle drops a third faster
    /// than a pellet and a medic has to lead further because of it.
    /// </remarks>
    private const double NeedleGravity = 0.2;

    // ----- Quote's two projectiles ------------------------------------------

    /// <summary><c>Bubble.events/Create.xml</c>: <c>hitDamage = 0.75</c>.</summary>
    public const double BubbleHitDamage = 0.75;

    /// <summary><c>Bubble.events/Create.xml</c>: <c>lifetime = 155</c>.</summary>
    public const int BubbleLifetime = 155;

    /// <summary>
    /// <c>selfPopRadius</c>: how much a mine's blast radius is widened when it
    /// asks about bubbles.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only a mine's blast uses it.</b> The three explosions that pop
    /// bubbles use three different formulas — see
    /// <see cref="Explosions.PopBubbles"/>.
    /// </remarks>
    public const double BubbleSelfPopRadius = 4;

    /// <summary>How far from its owner a bubble may drift before it pops.</summary>
    /// <remarks>
    /// <c>Bubble.events/Begin Step.xml</c>, and it is
    /// <c>distance_to_object</c> — so the <em>facing-inflated</em> box of the
    /// owner, two pixels wider on the left when they face that way. See
    /// <see cref="Aabb"/>.
    /// </remarks>
    public const double BubbleLeash = 160;

    /// <summary>What a bubble is pulled towards its owner's crosshair at.</summary>
    private const double BubbleAimPull = 2;

    /// <summary>The radius of the ring a bubble is held in around its owner.</summary>
    private const double BubbleOrbitRadius = 90;

    /// <summary>The softness of that ring: the arctangent's denominator.</summary>
    private const double BubbleOrbitSoftness = 4;

    /// <summary>How much of its owner's velocity a bubble picks up each tick.</summary>
    private const double BubbleOwnerDrag = 0.2;

    /// <summary>What is added to a bubble's speed each tick before the drag.</summary>
    private const double BubbleSpeedGain = 2.2;

    /// <summary>How much a same-team bubble shoves its neighbour away.</summary>
    private const double BubbleRepulsion = -0.5;

    /// <summary>How far a bubble is nudged vertically to slide past a corner.</summary>
    private const double BubbleWallSlide = 6;

    /// <summary>
    /// <c>maxKills</c>: how many things one knife connects with before it goes
    /// away.
    /// </summary>
    public const int StabMaxKills = 2;

    /// <summary>
    /// How far above the knife its line-of-sight test starts: <c>y - 12</c>.
    /// </summary>
    /// <remarks>
    /// Chest height on the spy, and the line ends at the victim's
    /// <em>origin</em>, which is at their feet — so the test is a diagonal and
    /// not a horizontal sweep.
    /// </remarks>
    private const double StabLineHeight = 12;

    /// <summary>How hard a blade shoves the character it sticks in.</summary>
    /// <remarks>
    /// ⚠️ <b>Thirteen times a pellet's 0.03</b>, and — unlike the pellet's — it
    /// is applied to an ubered victim as well, because the shove sits outside
    /// the <c>if(!other.ubered)</c> block that guards the damage.
    /// </remarks>
    private const double BladeKnockback = 0.4;

    /// <summary>
    /// What a <c>Rocket</c>'s <c>speed</c> gains each tick, before the drag.
    /// </summary>
    private const double RocketAcceleration = 1;

    /// <summary>
    /// What is left of a <c>Rocket</c>'s <c>speed</c> after each tick's drag.
    /// </summary>
    /// <remarks>
    /// <c>speed *= delta_mult(0.92)</c>, and <c>delta_mult(f)</c> is
    /// <c>f*delta_factor + (1 - delta_factor)</c>, which is <c>f</c> at 30 Hz.
    /// <para>
    /// The reference's own comment on the pair: "It might seem like a good idea
    /// to remove the acceleration function, but it's needed for consistent
    /// airblasting." A rocket therefore approaches a terminal speed of 12.5
    /// rather than holding the 13 it was fired at.
    /// </para>
    /// </remarks>
    private const double RocketDrag = 0.92;

    /// <summary>
    /// How many passes a fading rocket survives.
    /// </summary>
    /// <remarks>
    /// Ten, because the reference's <c>image_alpha -= 0.1</c> has reached
    /// 1.4e-16 by then and GML's <c>&gt;</c> is fuzzy to 1e-13. See
    /// <see cref="ProjectileState.FadeTicks"/>.
    /// </remarks>
    private const int RocketFadeLifetime = 10;

    /// <summary>
    /// <c>alarm[3]</c>: ticks from the travel budget running out to the rocket
    /// vanishing.
    /// </summary>
    /// <remarks>
    /// Rearmed on <em>every</em> pass the budget is still over-spent, so it only
    /// ever fires for a rocket whose owner has moved back towards it. The usual
    /// end of an over-range rocket is <see cref="ProjectileState.ImageAlpha"/>
    /// reaching zero two ticks later.
    /// </remarks>
    private const int RocketFadeTicks = 8;

    /// <summary>
    /// What flying through a team-mate costs a rocket's travel budget.
    /// </summary>
    private const double RocketPassThroughCost = 100;

    /// <summary>What a <c>Mine</c>'s <c>vspeed</c> gains each tick.</summary>
    /// <remarks>
    /// Heavier than a pellet's 0.15 and a third of a character's 0.6, and unlike
    /// either it is <b>capped</b> — see <see cref="MineTerminalVSpeed"/>.
    /// </remarks>
    private const double MineGravity = 0.2;

    /// <summary>
    /// The fastest a mine ever falls. <c>vspeed = min(vspeed, 8)</c>.
    /// </summary>
    /// <remarks>
    /// The only terminal velocity on any projectile in the game, and it is what
    /// gives a lobbed mine its lazy arc. It is a plain <c>min</c> rather than a
    /// fuzzy comparison, because that is what the reference writes.
    /// </remarks>
    private const double MineTerminalVSpeed = 8;

    /// <summary>
    /// The longest hop a mine takes before checking what it has hit.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A mine substeps and a pellet does not.</b> Its whole movement is a
    /// loop that walks at most six pixels at a time along the remaining vector,
    /// testing <c>place_free</c> after each hop — so a mine cannot tunnel
    /// through a thin wall the way a Shot at speed 13 can, and it stops at the
    /// <em>first</em> six-pixel boundary inside a solid rather than at the far
    /// side of its full step.
    /// </remarks>
    private const double MineSubstep = 6;

    /// <summary>
    /// Ticks a landed reflected mine keeps crediting its reflector:
    /// <c>alarm[0] = 30</c> in <c>move_all_bullets.gml</c>'s stickied branch.
    /// </summary>
    private const int MineReflectorWindow = 30;

    /// <summary>
    /// What a <c>Flame</c> falls at, and the one line of its movement block that
    /// is guarded on the object.
    /// </summary>
    /// <remarks>
    /// <c>move_all_bullets.gml</c>'s <c>with(BurningProjectile)</c> block covers
    /// the <c>Flame</c> and the <c>Flare</c> together and opens with
    /// <c>if(object_index == Flame) vspeed += 0.15</c> — so a flare flies flat
    /// and a flame droops. The same 0.15 a pellet falls at, not the needle's 0.2.
    /// </remarks>
    private const double FlameGravity = 0.15;

    /// <summary>
    /// The longest hop a burning projectile takes before checking what it hit.
    /// </summary>
    /// <remarks>
    /// The same six pixels a mine uses and for the same reason: a flame at speed
    /// 10 would otherwise tunnel through a thin wall. The reference has two
    /// copies of the loop; <see cref="SubstepWalk"/> is the one copy here.
    /// </remarks>
    private const double BurningSubstep = 6;

    /// <summary>
    /// <c>Scripts/Physics/System/move_all_bullets.gml</c>: every projectile
    /// accelerates, aims its sprite, moves, and dies if it landed in a wall.
    /// </summary>
    /// <remarks>
    /// <b>The script is one <c>with</c> block per object and they are not
    /// interchangeable</b> — a <c>Shot</c> falls, a <c>Rocket</c>
    /// accelerates and drags, a <c>Mine</c> substeps — so this dispatches
    /// on the kind rather than sharing a body. Only
    /// <see cref="ProjectileKind.Shot"/> has a block so far; the rest arrive
    /// with their weapons.
    /// <para>
    /// <b>The script's <c>x += hspeed</c> … <c>x -= hspeed</c> is not a
    /// move.</b> It is a look-ahead: the position is advanced so
    /// <c>place_free</c> can be asked about the destination, then put straight
    /// back, and GM8's automatic movement pass — which runs after every
    /// Step event — performs the real one. The port does the move once and
    /// keeps it, which lands on the same coordinates. It is <em>not</em>
    /// subject to docs/SIMULATION.md's "automatic-movement round trip" caveat,
    /// which is about a character subtracting a double and having the engine add
    /// it back: here the reference's own subtraction is undone by an addition of
    /// the identical value in the same tick, so no ULP is lost on either side.
    /// </para>
    /// <para>
    /// The move is written straight onto the position rather than through
    /// <see cref="Motion"/>, because moving an instance does not touch its
    /// motion variables. The <em>acceleration</em> above it does, and goes
    /// through the component setter for exactly that reason: <c>vspeed</c> is
    /// one of GM8's four linked motion variables, so adding gravity to it
    /// rewrites <c>direction</c> and <c>speed</c> — which is what the next
    /// line's <c>image_angle = direction</c> and the collision event's knockback
    /// both read.
    /// </para>
    /// <para>
    /// Characters are not here. Their movement is folded into
    /// <see cref="CharacterPhysics.Step"/>, because
    /// <c>Character.events/Step.xml</c> ends by subtracting the same two values
    /// so that the engine's move puts them back — see that file's remarks.
    /// </para>
    /// <para>
    /// ⚠️ <b>That subtraction is why this pass has to run before the characters
    /// move.</b> <c>move_all_bullets</c> is a Step event, and every character's
    /// Step event has put its position back to where the tick started by the
    /// time any of them finish — so a blast that goes off in here sees the world
    /// as it was at the end of the previous tick. See
    /// <see cref="MatchSimulation"/>, which is where the ordering lives.
    /// </para>
    /// <para>
    /// <b>A projectile created earlier in the same tick moves in it, and that is
    /// measured rather than assumed.</b> A weapon fires in Begin Step and this
    /// pass comes later in the same frame, so the question is whether GM8's
    /// movement pass walks instances that did not exist when the frame started.
    /// It does: delaying a pellet's first move by one tick and replaying
    /// <c>reference-ctf_truefort-bots.trace</c> closed-loop diverges at tick 393
    /// — the same six pellets still land, but a tick late, and both scouts'
    /// <c>hspeed</c> at the next broadcast is wrong by the knockback. With the
    /// move here the whole 898-tick recording is exact. See
    /// <c>PhysicsParityTests.ClosedLoopIsExactAcrossTheWholeTrace</c>.
    /// </para>
    /// </remarks>
    public static void Move(CollisionWorld world, ref WorldState state, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        // The index only advances when the projectile survived, for the same
        // reason as in Collide: freeing a pool slot compacts the creation-order
        // array under the cursor.
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            if (MoveOne(world, ref state, state.LiveProjectileHandle(i), ref events))
                continue;

            i++;
        }
    }

    /// <summary>Dispatch for one projectile. True if it was destroyed.</summary>
    private static bool MoveOne(
        CollisionWorld world, ref WorldState state, ProjectileHandle handle, ref TickEvents events)
    {
        return state.Projectiles[handle.Index].Kind switch
        {
            ProjectileKind.Shot => MoveShot(world, ref state, handle, ShotGravity),

            // The Needle's `with` block differs from the Shot's in one constant
            // and in nothing else, so it shares the body rather than being
            // copied. The gravity is the parameter because it is the only thing
            // the two disagree about.
            ProjectileKind.Needle => MoveShot(world, ref state, handle, NeedleGravity),

            ProjectileKind.Rocket => MoveRocket(world, ref state, handle, ref events),
            ProjectileKind.Mine => MoveMine(world, ref state, handle),

            // Both children of BurningProjectile share its with() block; the
            // one line inside it that distinguishes them (the Flame's gravity)
            // is keyed on the kind in the body.
            ProjectileKind.Flame => MoveBurning(world, ref state, handle),
            ProjectileKind.Flare => MoveBurning(world, ref state, handle),

            ProjectileKind.Blade => MoveBlade(world, ref state, handle),

            // ⚠️ A Bubble has no `with` block in move_all_bullets at all — it
            // is the one projectile that moves in its own Step event, which
            // runs *after* every character's rather than before. See
            // BubbleStep, which MatchSimulation calls from the right phase.
            ProjectileKind.Bubble => false,

            // Nor does a StabMask, for the same reason and with a stronger
            // one on top: it has no velocity at all. Its Step pins it to its
            // owner — see StabStep.
            ProjectileKind.Stab => false,

            var kind => throw new ArgumentOutOfRangeException(
                nameof(handle), kind, "no movement pass for this projectile"),
        };
    }

    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(Shot)</c> block, which is also its
    /// <c>with(Needle)</c> block. True if the projectile was destroyed.
    /// </summary>
    /// <param name="gravity">
    /// <see cref="ShotGravity"/> or <see cref="NeedleGravity"/> — the one line
    /// the two blocks disagree about.
    /// </param>
    private static bool MoveShot(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        double gravity)
    {
        ref var shot = ref state.Projectiles[handle.Index];

        // vspeed += 0.15. A pellet falls, at a quarter of a character's gravity.
        // ⚠ This repo left it out for a phase and the bot trace stayed
        // exact anyway: a Shot is never serialised, so the drop is observable
        // only where it changes whether something is hit, and at the ranges in
        // that recording it does not. Absence of a red test was not evidence.
        shot.Motion.VSpeed += gravity;

        // image_angle = direction, every pass. The pellet's box turns to follow
        // its own heading, so the aim angle createShot wrote survives only until
        // this line first runs — which is before any collision event can
        // look at the box.
        shot.ImageAngle = shot.Motion.Direction;

        // A pellet born inside a wall dies without getting anywhere. Only on the
        // first pass: afterwards the previous pass has already established that
        // the position is free, because a pellet that reached a solid one was
        // destroyed there.
        var colliding = !shot.HasMoved && !ProjectileMask.PlaceFree(world, in shot, state.BulletFilter);

        shot.X += shot.Motion.HSpeed;
        shot.Y += shot.Motion.VSpeed;
        shot.HasMoved = true;

        // `colliding |=`, not `else`: the destination is tested whether or not
        // the muzzle was. Both are the *bullet's* solids — a pellet passes
        // through a PlayerWall and is stopped by a BulletWall and by every gate,
        // whichever team's. state.BulletFilter.
        colliding |= !ProjectileMask.PlaceFree(world, in shot, state.BulletFilter);

        if (!colliding)
            return false;

        // The reference creates an Impact here, which is a particle: it has no
        // collision events and nothing on the server can observe it.
        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(BladeB)</c> block. True if the
    /// blade was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>The <c>with(Shot)</c> block with three lines removed, and each
    /// absence is a rule.</b> There is <em>no gravity</em>, so a thrown blade
    /// flies dead straight for its fifteen ticks; there is no
    /// <c>image_angle = direction</c>, so the 0 that
    /// <c>Blade.events/User Event 2.xml</c> forced survives the whole flight
    /// (see that event's own comment, and GG2 issue #230); and there is no
    /// <c>image_alpha</c> fade, which is drawing either way.
    /// <para>
    /// What it keeps is the muzzle test on the first pass and the destination
    /// test on every pass — so a blade thrown into a wall dies in it, exactly
    /// like a pellet.
    /// </para>
    /// </remarks>
    private static bool MoveBlade(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        ref var blade = ref state.Projectiles[handle.Index];

        var colliding = !blade.HasMoved && !ProjectileMask.PlaceFree(world, in blade, state.BulletFilter);

        blade.X += blade.Motion.HSpeed;
        blade.Y += blade.Motion.VSpeed;
        blade.HasMoved = true;

        colliding |= !ProjectileMask.PlaceFree(world, in blade, state.BulletFilter);

        if (!colliding)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>Bubble.events/Begin Step.xml</c> for every live bubble: the leash.
    /// </summary>
    /// <remarks>
    /// The whole event is
    /// <c>if(distance_to_object(owner) &gt; 160) instance_destroy();</c>. It is
    /// the <em>only</em> reason a bubble ever leaves before its 155-tick alarm
    /// or a collision, and it is what makes a Quote's bubbles a screen of cover
    /// around them rather than a cloud they can walk away from.
    /// <para>
    /// ⚠️ <b><c>distance_to_object</c>, so the owner's facing-inflated box</b> —
    /// a Quote looking left reaches two pixels further than one looking right.
    /// The trap docs/SIMULATION.md records for splash damage, in a second place.
    /// </para>
    /// <para>
    /// A bubble whose owner has stopped existing is not reachable here: the
    /// Blade's Destroy event takes every one of them with it
    /// (<see cref="WorldState.Clear"/>), which is the same exception to
    /// "a projectile outlives its owner" that a mine has.
    /// </para>
    /// </remarks>
    public static void BubbleBeginStep(ref WorldState state)
    {
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var handle = state.LiveProjectileHandle(i);
            ref var bubble = ref state.Projectiles[handle.Index];

            if (bubble.Kind != ProjectileKind.Bubble)
            {
                i++;
                continue;
            }

            var owner = bubble.OwnerSlot;
            if (owner == ProjectileState.NoOwner || !state.Alive[owner])
            {
                i++;
                continue;
            }

            ref var character = ref state.Characters[owner];
            var ownerBox = Aabb.ForCharacter(character.Box, character.X, character.Y, character.Facing);

            if (Gml.Greater(bubble.Box.Distance(ownerBox), BubbleLeash))
                state.FreeProjectile(handle);
            else
                i++;
        }
    }

    /// <summary>
    /// <c>Bubble.events/Step.xml</c> for every live bubble: the steering, and
    /// the move.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A bubble is the one projectile that is not in
    /// <c>move_all_bullets</c>.</b> It moves in its own Step event, and a
    /// <c>Bubble</c> instance is younger than the character that made it — so
    /// this runs <em>after</em> every character's Step, where the pellet pass
    /// runs before. That is why it has a phase of its own in
    /// <see cref="MatchSimulation"/>.
    /// <para>
    /// The event is four steering terms and a move:
    /// </para>
    /// <list type="number">
    /// <item>pick up a fifth of the owner's velocity, as components;</item>
    /// <item><c>motion_add(owner.aimDirection, 2)</c> — drift towards the
    /// crosshair;</item>
    /// <item>a spring towards a ring of radius 90 about the owner, softened by
    /// an arctangent, and note it is <em>negative</em>: the direction is
    /// measured owner-to-bubble and the strength is negated, so a bubble inside
    /// the ring is pushed out and one outside is pulled in;</item>
    /// <item><c>speed = (speed + 2.2) * 0.6</c>, which settles at 3.3 —
    /// the reference's own comment says so.</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>The owner's position it reads is not the one the port stores.</b>
    /// During the Step stage every character's <c>x</c> is still the value its
    /// own Step subtracted its <c>hspeed</c> back off, and GM8's movement pass
    /// has not run yet; this port folds that round trip into
    /// <see cref="CharacterPhysics.Step"/> instead. So the reference's
    /// mid-stage <c>owner.x</c> is <c>X - HSpeed</c> here — <em>this tick's</em>
    /// speed against <em>this tick's</em> final position. It can differ from
    /// what the engine held by a ULP, and it feeds a steering force of at most
    /// two units in a projectile that is never serialised; see
    /// docs/SIMULATION.md, "Settled: the automatic-movement round trip".
    /// </para>
    /// </remarks>
    public static void BubbleStep(ref WorldState state)
    {
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var bubble = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (bubble.Kind != ProjectileKind.Bubble)
                continue;

            var owner = bubble.OwnerSlot;
            if (owner == ProjectileState.NoOwner || !state.Alive[owner])
                continue;

            ref var character = ref state.Characters[owner];

            var ownerX = character.X - character.HSpeed;
            var ownerY = character.Y - character.VSpeed;

            // Two component writes, which re-derive the polar pair and snap it.
            bubble.Motion.HSpeed += character.HSpeed * BubbleOwnerDrag;
            bubble.Motion.VSpeed += character.VSpeed * BubbleOwnerDrag;

            bubble.Motion.MotionAdd(character.AimDirection, BubbleAimPull);

            // arctan returns radians and is used as a *strength* rather than an
            // angle, so nothing converts it back. Its range is (-pi/2, pi/2),
            // which makes this term at most about 3.14 in either direction.
            bubble.Motion.MotionAdd(
                Gml.PointDirection(ownerX, ownerY, bubble.X, bubble.Y),
                -BubbleAimPull * Math.Atan(
                    (Gml.PointDistance(bubble.X, bubble.Y, ownerX, ownerY) - BubbleOrbitRadius)
                    / BubbleOrbitSoftness));

            // `speed = (speed + 2.2) * delta_mult(0.6)`, and delta_mult(f) is f
            // at 30 Hz. ⚠️ 0.6 is one of the five literals GM8 parses a ULP
            // above the IEEE nearest, so it comes from Gml.
            bubble.Motion.Speed = (bubble.Motion.Speed + BubbleSpeedGain) * Gml.PointSix;

            // `x += hspeed; y += vspeed; x -= hspeed; y -= vspeed;` and then
            // GM8's movement pass puts the same two values back. The port moves
            // once and keeps it, which lands on the same coordinates — the same
            // reading docs/SIMULATION.md settles for a pellet, and *not* the
            // round trip a character's Step performs, where the engine adds back
            // a value the reference has already stored.
            bubble.X += bubble.Motion.HSpeed;
            bubble.Y += bubble.Motion.VSpeed;
        }
    }

    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(BurningProjectile)</c> block. True
    /// if the flame was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>A flame substeps like a mine and dies like a pellet.</b> It walks the
    /// same six-pixel hops (<see cref="SubstepWalk"/>) and the first one that
    /// lands in something solid ends it — there is no sticking and no explosion,
    /// it simply stops existing.
    /// <para>
    /// ⚠️ <b>there is no <c>speed != 0</c> guard</b>, where the mine block
    /// has one. A flame with no speed still enters the loop; the first iteration
    /// finds a remaining distance of zero, takes a zero-length hop and finishes.
    /// </para>
    /// <para>
    /// And no <c>image_angle = direction</c>, unlike the Shot and Rocket blocks:
    /// a burning projectile keeps the angle <c>createShot</c> gave it, so its box
    /// stays aligned with the aim rather than with its own heading. It is a 3x3
    /// box centred on its origin, so this is nearly unobservable — but it is what
    /// the script says.
    /// </para>
    /// </remarks>
    private static bool MoveBurning(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        ref var flame = ref state.Projectiles[handle.Index];

        // if(object_index == Flame) vspeed += 0.15 — the Flare, the other child
        // of BurningProjectile, flies flat.
        if (flame.Kind == ProjectileKind.Flame)
            flame.Motion.VSpeed += FlameGravity;

        var colliding = !flame.HasMoved && !ProjectileMask.PlaceFree(world, in flame, state.BulletFilter);

        SubstepWalk(world, ref flame, BurningSubstep, state.BulletFilter, ref colliding);
        flame.HasMoved = true;

        if (!colliding)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(Rocket)</c> block. True if the
    /// rocket was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Four things a pellet does not do.</b> It accelerates and then drags;
    /// it measures its distance from a leash anchored on its owner and starts
    /// fading when the leash runs out; it dies to an alpha countdown rather than
    /// to a lifetime; and — the one that changes where an explosion lands —
    /// <b>it does not move at all on a tick it is already colliding</b>, so that
    /// the blast is centred on what it hit rather than a step past it.
    /// </remarks>
    private static bool MoveRocket(
        CollisionWorld world, ref WorldState state, ProjectileHandle handle, ref TickEvents events)
    {
        ref var rocket = ref state.Projectiles[handle.Index];

        // if(instance_exists(owner)) { lastknownx = owner.x; ... }
        //
        // `owner` is the character, not the player — so the anchor follows a
        // living shooter around and freezes where a dead one fell. A rocket
        // whose shooter dies keeps whatever range it had left from that spot.
        if (rocket.OwnerSlot != ProjectileState.NoOwner && state.Alive[rocket.OwnerSlot])
        {
            rocket.LastKnownX = state.Characters[rocket.OwnerSlot].X;
            rocket.LastKnownY = state.Characters[rocket.OwnerSlot].Y;
        }

        var travelled = Gml.PointDistance(rocket.X, rocket.Y, rocket.LastKnownX, rocket.LastKnownY);
        if (Gml.GreaterOrEqual(travelled, rocket.DistanceToTravel))
        {
            rocket.Fade = true;
            rocket.FadeTimer = RocketFadeTicks;
        }

        // Two separate polar writes, in this order. Each rewrites the components
        // and snaps them (see Motion), so `speed = (speed + 1) * 0.92` is a
        // different number in the last bits.
        rocket.Motion.Speed += RocketAcceleration;
        rocket.Motion.Speed *= RocketDrag;

        rocket.ImageAngle = rocket.Motion.Direction;

        if (rocket.Fade)
            rocket.FadeTicks++;

        // The alpha countdown, which is a real destroy and not a draw call. See
        // ProjectileState.FadeTicks for why the tenth fading pass is the last
        // one and not the eleventh.
        if (rocket.FadeTicks >= RocketFadeLifetime)
        {
            state.FreeProjectile(handle);
            return true;
        }

        // colliding = explodeImmediately — a rocket fired with a wall already
        // between the weapon and the muzzle goes off where it was made.
        var colliding = rocket.ExplodeImmediately;

        if (!rocket.HasMoved)
            colliding |= !ProjectileMask.PlaceFree(world, in rocket, state.BulletFilter);

        // ⚠️ `if(!colliding)`, where the Shot moves unconditionally. The
        // reference's own comment: "Only move forward if we don't collide, to
        // ensure that the rocket will explode at the place of its collision."
        // Moving first would shift every wall blast by a tick of travel and
        // change who is inside the radius.
        if (!colliding)
        {
            rocket.X += rocket.Motion.HSpeed;
            rocket.Y += rocket.Motion.VSpeed;
            colliding |= !ProjectileMask.PlaceFree(world, in rocket, state.BulletFilter);
        }

        rocket.HasMoved = true;

        if (!colliding)
            return false;

        // if (!fade) event_user(5) else instance_destroy().
        //
        // ⚠️ A rocket past its range does no damage at all — not to the wall, and
        // not to somebody standing in front of it. It is deleted where it is.
        if (rocket.Fade)
        {
            state.FreeProjectile(handle);
            return true;
        }

        return Explosions.Explode(world, ref state, handle, CharacterState.Nobody, ref events);
    }

    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(Mine)</c> block. True if the mine
    /// was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>The only projectile that stops without dying.</b> A mine that reaches
    /// an <c>Obstacle</c> is backed out of it, has its speed zeroed and sticks;
    /// one that reaches anything else a bullet collides with — a gate, a bullet
    /// wall — is destroyed where it is. The difference is one
    /// <c>place_meeting(x, y, Obstacle)</c>, and it is why
    /// <see cref="CollisionWorld.PlaceFreeInMask(in Aabb)"/> exists.
    /// </remarks>
    private static bool MoveMine(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        ref var mine = ref state.Projectiles[handle.Index];

        if (mine.Stickied)
        {
            // if (reflector != noone and alarm[0] < 0) alarm[0] = 30: a
            // reflected mine that lands starts the thirty-tick window in which
            // its blast still credits the pyro. The mine's Alarm 0 is
            // `reflector = noone`, not a destroy — Timers.Advance special-cases
            // it.
            if (mine.Reflector != ProjectileState.NoOwner && mine.Lifetime < 0)
                mine.Lifetime = MineReflectorWindow;

            mine.SplashThreshold = false;
        }
        else
        {
            // A component write, so it rewrites direction and speed — which the
            // substep loop below then reads.
            mine.Motion.VSpeed = Math.Min(mine.Motion.VSpeed + MineGravity, MineTerminalVSpeed);
            mine.SplashThreshold = true;
        }

        // A stuck mine has speed 0 and skips the whole of the rest, which is why
        // it never falls again and never re-tests its position.
        if (!Gml.NotEqual(mine.Motion.Speed, 0))
            return false;

        // ⚠️ `colliding = !place_free`, not `|=`: unlike the Shot's block this
        // one *assigns*, so the muzzle test is the whole of the initial value.
        var colliding = !mine.HasMoved && !ProjectileMask.PlaceFree(world, in mine, state.BulletFilter);

        SubstepWalk(world, ref mine, MineSubstep, state.BulletFilter, ref colliding);
        mine.HasMoved = true;

        if (!colliding)
            return false;

        // place_meeting(x, y, Obstacle) — the map alone, because the reference
        // swaps gunSetSolids for wallSetSolid around this and wallSetSolid is
        // `with(Obstacle) solid = true` and nothing more.
        if (!ProjectileMask.PlaceFreeInMask(world, in mine))
        {
            BackOutOfWalls(world, ref mine, mine.Motion.Direction + 180, mine.Motion.Speed);

            // A polar write of zero. It leaves `direction` alone — which is what
            // the back-out above just used — and takes both components to zero,
            // so the mine stops and stays stopped.
            mine.Motion.Speed = 0;
            mine.Stickied = true;
        }

        // Re-tested after the stick: a mine that backed out successfully is now
        // free and lives. One that hit a gate never entered the branch at all,
        // and one that could not escape the wall dies in it.
        if (ProjectileMask.PlaceFree(world, in mine, state.BulletFilter))
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// The <c>while(not mdone and not colliding)</c> loop shared by the
    /// <c>Mine</c> and <c>BurningProjectile</c> blocks of
    /// <c>move_all_bullets.gml</c>.
    /// </summary>
    /// <remarks>
    /// <b>The reference has two copies and they are identical</b>, so this is one
    /// copy rather than a generalisation: walk at most <paramref name="substep"/>
    /// pixels along what is left of the step, test <c>place_free</c> after each
    /// hop, and stop at the first hop that lands in something solid. That is what
    /// stops a fast projectile tunnelling through a thin wall, which the Shot's
    /// single-jump block does not.
    /// <para>
    /// <c>lengthdir</c>, which snaps — so a projectile falling straight down
    /// takes hops of exactly six pixels with no horizontal drift.
    /// </para>
    /// <para>
    /// <paramref name="colliding"/> comes in carrying the muzzle test and goes
    /// out carrying it or any hop that hit. The loop does not run at all if it
    /// arrives true, which is the reference's own guard.
    /// </para>
    /// </remarks>
    private static void SubstepWalk(
        CollisionWorld world,
        ref ProjectileState projectile,
        double substep,
        in CollisionFilter filter,
        ref bool colliding)
    {
        var dx = projectile.Motion.HSpeed;
        var dy = projectile.Motion.VSpeed;
        var done = false;

        while (!done && !colliding)
        {
            var remaining = Gml.PointDistance(0, 0, dx, dy);
            double motion;

            if (Gml.Greater(remaining, substep))
            {
                motion = substep;
                done = false;
            }
            else
            {
                motion = remaining;
                done = true;
            }

            var direction = Gml.PointDirection(0, 0, dx, dy);
            var stepX = Gml.LengthDirX(motion, direction);
            var stepY = Gml.LengthDirY(motion, direction);

            projectile.X += stepX;
            projectile.Y += stepY;
            dx -= stepX;
            dy -= stepY;

            colliding |= !ProjectileMask.PlaceFree(world, in projectile, filter);
        }
    }

    /// <summary>
    /// <c>move_outside_solid</c> against the map only, for a mine backing out of
    /// the wall it landed on.
    /// </summary>
    /// <remarks>
    /// The same stepping rule as <see cref="Movement.MoveOutsideSolid"/> and
    /// <see cref="BackOutOfCharacter"/>, measured against the running game: whole
    /// pixels along the direction, keeping the fractional part of the start, and
    /// moving the whole allowance anyway if nothing within it is free.
    /// </remarks>
    private static void BackOutOfWalls(
        CollisionWorld world,
        ref ProjectileState projectile,
        double directionDegrees,
        double maxDistance)
    {
        if (ProjectileMask.PlaceFreeInMask(world, in projectile))
            return;

        var radians = Gml.DegToRad(directionDegrees);
        var stepX = Math.Cos(radians);
        var stepY = -Math.Sin(radians);

        var startX = projectile.X;
        var startY = projectile.Y;
        var steps = CollisionWorld.RoundPosition(maxDistance);

        for (var step = 1; step <= steps; step++)
        {
            projectile.X = startX + (stepX * step);
            projectile.Y = startY + (stepY * step);

            if (ProjectileMask.PlaceFreeInMask(world, in projectile))
                return;
        }

        projectile.X = startX + (stepX * steps);
        projectile.Y = startY + (stepY * steps);
    }

    /// <summary>
    /// The collision phase: every projectile against every character.
    /// </summary>
    /// <remarks>
    /// Projectiles are walked in <b>creation order</b>
    /// (<see cref="WorldState.LiveProjectileSlot"/>) and characters in slot
    /// order, which is the reference's instance order in both cases — GM8 runs a
    /// collision event over the instances of one object and then the instances
    /// of the other, both by id, and an id is issued at creation.
    /// </remarks>
    public static void Collide(CollisionWorld world, ref WorldState state, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        // ⚠️ A snapshot of handles, not a cursor over the live array — and the
        // difference is not cosmetic.
        //
        // GM8 runs this stage over the instances that exist when it begins, in
        // id order, and skips one that an earlier event has already destroyed.
        // A cursor over the creation-order array can only express "the
        // projectile I am looking at died", because freeing any *other* entry
        // compacts the array underneath it and silently skips its neighbour.
        // That was enough while every collision destroyed at most the projectile
        // whose event was running — and Quote's bubbles broke it, because a
        // bubble and the pellet it meets destroy each other in one call. See
        // BubbleHitProjectiles.
        //
        // Handles are index-plus-generation, so a slot recycled during the stage
        // cannot be mistaken for the projectile that used to be in it.
        Span<ProjectileHandle> order = stackalloc ProjectileHandle[WorldState.MaxProjectiles];
        var count = state.LiveProjectileCount;

        for (var i = 0; i < count; i++)
            order[i] = state.LiveProjectileHandle(i);

        for (var i = 0; i < count; i++)
        {
            if (state.IsLive(order[i]))
                CollideOne(world, ref state, order[i], ref events);
        }
    }

    /// <summary>
    /// One projectile against every character. True if it was destroyed.
    /// </summary>
    private static bool CollideOne(
        CollisionWorld world, ref WorldState state, ProjectileHandle handle, ref TickEvents events)
    {
        // A Bubble's sixteen collision events have almost nothing in common
        // with the other six projectiles' three, so they are a body of their
        // own rather than a pile of branches inside this one.
        if (state.Projectiles[handle.Index].Kind == ProjectileKind.Bubble)
            return BubbleCollide(world, ref state, handle, ref events);

        // Hoisted out of the character loop: the box depends only on the
        // projectile, and nothing inside the loop moves it.
        var projectileBox = state.Projectiles[handle.Index].Box;

        // ⚠️ A Mine has no `Collision with Character` event at all — its
        // Objects/Projectiles/Mine.events/ holds Sentry and Generator and
        // nothing else. So a mine flies straight through people, and the only
        // thing it can do to a character is explode near them — or stick to a
        // sentry, which is why it does not skip the sentry loop below.
        if (state.Projectiles[handle.Index].Kind != ProjectileKind.Mine)
        {
            // ⚠️ CREATION order, not slot order, and the difference is a kill.
            // A rocket explodes on the FIRST character its collision event fires
            // against and GM8 fires those in ascending instance id — so a slot
            // that respawned a tick ago is visited *after* one that has been
            // alive since the wave, however low its number is. See
            // CharacterState.CreationOrder for the recording that measured it.
            Span<byte> characters = stackalloc byte[WorldState.MaxSlots];
            var liveCount = state.LiveCharactersInCreationOrder(characters);

            for (var i = 0; i < liveCount; i++)
            {
                var slot = characters[i];

                // Re-checked because an earlier iteration's collision can kill
                // somebody the snapshot listed — the same reason Collide walks a
                // snapshot of projectile handles rather than a live cursor.
                if (!state.Alive[slot])
                    continue;

                ref var victim = ref state.Characters[slot];

                // place_meeting's box, which is not distance_to_object's: the
                // facing inflation GM8 reports on bbox_left is a broad-phase
                // bound and the overlap test goes to the precise mask, which is
                // symmetric about the origin. Passing the facing here would
                // widen every left-facing character by two pixels for free (see
                // Aabb).
                var victimBox = Aabb.ForCharacter(victim.Box, victim.X, victim.Y);

                if (!ProjectileMask.Overlaps(
                        in state.Projectiles[handle.Index], in projectileBox, in victimBox))
                {
                    continue;
                }

                if (HitCharacter(world, ref state, handle, slot, ref events))
                    return true;

                // The box was hoisted before the loop, and a rocket's collision
                // can move it (the contact reposition). Nothing after this
                // point in the loop is entitled to the stale one.
                projectileBox = state.Projectiles[handle.Index].Box;
            }
        }

        // `Collision with Sentry`, which every combat projectile has. The
        // character events run first here; GM8 orders one instance's collision
        // events by target object id, which this repo has not measured — the
        // difference is observable only when one projectile touches a character
        // and a sentry on the same tick.
        //
        // ⚠️ Build order, not slot order, for the reason the character loop
        // above uses creation order: a projectile explodes on the first sentry
        // it meets. No fixture holds two sentries overlapping one projectile, so
        // this follows the rule rather than a measurement — see
        // WorldState.LiveSentriesInCreationOrder.
        Span<byte> sentries = stackalloc byte[WorldState.MaxSlots];
        var sentryCount = state.LiveSentriesInCreationOrder(sentries);

        for (var i = 0; i < sentryCount; i++)
        {
            var slot = sentries[i];

            // Re-checked: an earlier iteration's explosion can destroy a sentry
            // the snapshot listed.
            if (!state.HasSentry[slot])
                continue;

            // place_meeting again: the sentry's mask box, no facing inflation.
            var sentryBody = Sentries.BodyBox(state.Sentries[slot]);

            if (!ProjectileMask.Overlaps(
                    in state.Projectiles[handle.Index], in projectileBox, in sentryBody))
            {
                continue;
            }

            if (HitSentry(world, ref state, handle, slot, ref events))
                return true;

            projectileBox = state.Projectiles[handle.Index].Box;
        }

        // `Collision with Bubble`, which only three of the seven projectiles
        // have — and it sits *here*, between Sentry (460) and Generator (560),
        // because Bubble's object id is 471 and GM8 runs one instance's
        // collision events in ascending order of the other object's id.
        //
        // ⚠️ A Rocket and a Flame have no such event at all, so both fly
        // straight through a bubble that dies on them; the Bubble's own side of
        // each pair does the popping. A Mine has none either, and the Bubble
        // *reflects* it rather than dying.
        if (HitBubble(ref state, handle, projectileBox))
            return true;

        // `Collision with Generator`, last of the three. GM8 keys one
        // instance's collision events by the *other* object's id, and the ids
        // are Character 2, Sentry 460, Generator 560 — so this order is the
        // reference's rather than a convenience. It is observable only when one
        // projectile touches two of the three on the same tick, which on a
        // generator map is a scattergun pellet catching a defender standing
        // against the generator.
        if (HitGenerator(world, ref state, handle, Team.Red, ref events)
            || HitGenerator(world, ref state, handle, Team.Blue, ref events))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// One projectile's <c>Collision with Generator</c> event. True if the
    /// projectile was destroyed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The <c>Shot</c>'s and the flame's generator events have no wall
    /// test</b>, where their <c>Collision with Sentry</c> and
    /// <c>Collision with Character</c> events both open with one
    /// (<c>gunSetSolids(); if (!place_free(x, y)) instance_destroy();</c>). So a
    /// pellet that is inside a wall still damages a generator it overlaps,
    /// which is the opposite of the rule everywhere else. Not an oversight to
    /// tidy: the generator is a large static target and the reference's own
    /// wall test would make its hitbox depend on the map behind it.
    /// <para>
    /// A <c>Mine</c> sticks rather than exploding, exactly as it does on a
    /// sentry, and is not destroyed.
    /// </para>
    /// </remarks>
    private static bool HitGenerator(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        Team team,
        ref TickEvents events)
    {
        ref var generator = ref Generators.Of(ref state, team);
        if (!generator.Exists)
            return false;

        ref var projectile = ref state.Projectiles[handle.Index];

        // place_meeting's box. A generator's image_xscale is never assigned, so
        // its reported and overlap boxes are the same one.
        if (!ProjectileMask.Overlaps(in projectile, Generators.BodyBox(generator)))
            return false;

        // Every one of the four events opens with the same team test, spelled
        // `other.team != team` in three of them and `team != other.team` in the
        // mine's.
        if (team == projectile.Team)
            return false;

        switch (projectile.Kind)
        {
            case ProjectileKind.Shot:
            case ProjectileKind.Needle:
            case ProjectileKind.Flame:
            case ProjectileKind.Flare:

            // BladeB's and Bubble's generator events are the same three lines
            // as the pellet's, wall test and all — which is to say without one.
            case ProjectileKind.Blade:
            case ProjectileKind.Bubble:
                Generators.Damage(ref state, team, projectile.HitDamage);
                state.FreeProjectile(handle);
                return true;

            case ProjectileKind.Rocket:
                // damageGenerator, then `characterHit = -1; event_user(5)` — so
                // unlike a sentry hit the generator is *not* the blast's direct
                // victim, and it takes the splash on top at distance zero.
                Generators.Damage(ref state, team, projectile.HitDamage);
                return Explosions.Explode(world, ref state, handle, CharacterState.Nobody, ref events);

            case ProjectileKind.Stab:
                // ⚠️ 0.36 of the knife's damage, and ⚠️ no line-of-sight
                // test — the reference's own comment says why: "map designers
                // might put walls behind the generator". The character and
                // sentry arms both have one.
                Generators.Damage(ref state, team, projectile.HitDamage * Weapons.StabGeneratorFactor);
                state.FreeProjectile(handle);
                return true;

            case ProjectileKind.Mine:
                // A polar write, which zeroes both components and snaps.
                projectile.Motion.Speed = 0;
                projectile.Stickied = true;
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// One projectile's <c>Collision with Sentry</c> event. True if the
    /// projectile was destroyed.
    /// </summary>
    /// <remarks>
    /// Four different events, and none of them is the Character one reused:
    /// <list type="bullet">
    /// <item><c>Shot</c> and <c>Needle</c> run the wall test, then damage an
    /// enemy sentry and die on it (<c>damageSentry</c> — which permanently
    /// slows the build — plus the dealer bookkeeping);</item>
    /// <item>a <c>Rocket</c> makes the sentry its direct victim and explodes —
    /// no back-out, no contact shove;</item>
    /// <item>a flame damages and dies with <b>no wall test and no penetration
    /// count</b> — even a perseverant flame dies on a sentry;</item>
    /// <item>a <c>Mine</c> sticks: <c>speed = 0; stickied = true</c>, enemy
    /// sentries only, and it is not destroyed.</item>
    /// </list>
    /// </remarks>
    private static bool HitSentry(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        ref var projectile = ref state.Projectiles[handle.Index];
        ref var sentry = ref state.Sentries[slot];

        switch (projectile.Kind)
        {
            case ProjectileKind.Shot:
            case ProjectileKind.Needle:
                // gunSetSolids(); if (!place_free(x, y)) { instance_destroy(); }
                // — the same muzzle-through-a-wall test as the Character event.
                if (!ProjectileMask.PlaceFree(world, in projectile, state.BulletFilter))
                {
                    state.FreeProjectile(handle);
                    return true;
                }

                if (sentry.Team == projectile.Team)
                    return false;

                Sentries.Damage(ref state, slot, projectile.HitDamage,
                    projectile.OwnerSlot, projectile.Source);
                state.FreeProjectile(handle);
                return true;

            case ProjectileKind.Rocket:
                if (sentry.Team == projectile.Team)
                    return false;

                // characterHit = other.id; event_user(5): the sentry is the
                // direct victim, and the blast happens where the rocket is.
                return Explosions.ExplodeOnSentry(world, ref state, handle, slot, ref events);

            case ProjectileKind.Flame:
            case ProjectileKind.Flare:

            // BladeB's and Bubble's sentry events are that same body: a team
            // test, damage, the dealer writes and a destroy — and, like the
            // burning ones and unlike the pellet's, ⚠️ **no wall test**. A
            // blade that reached a sentry through a wall still damages it.
            case ProjectileKind.Blade:
            case ProjectileKind.Bubble:
                // BurningProjectile's Collision with Sentry event, which both
                // children share unchanged.
                if (sentry.Team == projectile.Team)
                    return false;

                Sentries.Damage(ref state, slot, projectile.HitDamage,
                    projectile.OwnerSlot, projectile.Source);
                state.FreeProjectile(handle);
                return true;

            case ProjectileKind.Mine:
                if (sentry.Team == projectile.Team)
                    return false;

                // A polar write, which zeroes both components and snaps —
                // exactly the reference's `speed = 0`.
                projectile.Motion.Speed = 0;
                projectile.Stickied = true;
                return false;

            case ProjectileKind.Stab:
                return StabHitSentry(world, ref state, handle, slot);

            default:
                return false;
        }
    }

    /// <summary>
    /// One projectile's collision event against one character. True if the
    /// projectile was destroyed.
    /// </summary>
    private static bool HitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        return state.Projectiles[handle.Index].Kind switch
        {
            ProjectileKind.Shot => ShotHitCharacter(world, ref state, handle, slot, ref events),

            // Needle.events/Collision with Character.xml is the Shot's event
            // with `perseverant` and the Bubble clause absent — neither of which
            // this body reaches — so the two really are the same code. The
            // damage difference rides on the instance, not on the event.
            ProjectileKind.Needle => ShotHitCharacter(world, ref state, handle, slot, ref events),

            ProjectileKind.Rocket => RocketHitCharacter(world, ref state, handle, slot, ref events),
            ProjectileKind.Flame => BurningHitCharacter(ref state, handle, slot, ref events),
            ProjectileKind.Flare => BurningHitCharacter(ref state, handle, slot, ref events),
            ProjectileKind.Blade => BladeHitCharacter(ref state, handle, slot, ref events),
            ProjectileKind.Stab => StabHitCharacter(world, ref state, handle, slot, ref events),
            var kind => throw new ArgumentOutOfRangeException(
                nameof(handle), kind, "no collision event for this projectile"),
        };
    }

    /// <summary>
    /// <c>Shot.events/Collision with Character.xml</c>, in full. True if the
    /// pellet was destroyed.
    /// </summary>
    private static bool ShotHitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        ref var projectile = ref state.Projectiles[handle.Index];

        // gunSetSolids(); if (!place_free(x, y)) { instance_destroy(); exit; }
        //
        // The event's first act, and it is about the *projectile's* own
        // position, not the character's: a pellet that has reached somebody
        // through a wall is inside that wall, and dies there without dealing
        // damage. This is the only wall test a Shot ever gets, which is why it
        // is here rather than in Move.
        if (!ProjectileMask.PlaceFree(world, in projectile, state.BulletFilter))
        {
            state.FreeProjectile(handle);
            return true;
        }

        ref var victim = ref state.Characters[slot];

        // if(other.id != ownerPlayer.object and other.team != team
        //    && other.hp > 0 && other.ubered == 0)
        //
        // `ownerPlayer.object` is the owner's *current* character, so a pellet
        // cannot hit the player who fired it even after they have died and
        // respawned. A roster slot is both halves of that comparison here.
        if (projectile.OwnerSlot == slot
            || victim.Team == projectile.Team
            || !Gml.Greater(victim.Hp, 0)
            || victim.Ubered)
        {
            // Not a hit, and *not* a destroy: the pellet carries on through a
            // team-mate. The event simply falls off its end.
            return false;
        }

        // damageCharacter -> dealDamage. ⚠️ The return value is deliberately
        // ignored: a Team Deathmatch spawn bubble stops the hit points moving
        // and nothing else, so the timeUnscathed reset below and the assist
        // bookkeeping under it both still run — which is exactly what
        // damageCharacter.gml:3-7 does.
        DamageApi.Deal(ref events, slot, projectile.OwnerSlot, ref victim, projectile.HitDamage);
        victim.TimeUnscathed = 0;

        // Assist bookkeeping. The demoted dealer inherits what is *left* of the
        // finish-off window rather than getting a fresh one — `alarm[4] =
        // alarm[3]`, not ASSIST_TIME.
        if (victim.LastDamageDealer != projectile.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = projectile.OwnerSlot;
        victim.LastDamageSource = projectile.Source;

        // dealFlicker: a hit spy is briefly visible whatever their cloak is
        // doing.
        victim.DealFlicker();

        // with(other) { motion_add(other.direction, other.speed*0.03); }
        //
        // ⚠️ Inside a `with`, `other` is the *caller* — so both arguments come
        // from the shot, not from the character being pushed. Reading them as
        // the victim's own direction and speed would make a hit accelerate
        // whoever was already moving and do nothing to somebody standing still.
        victim.Motion.MotionAdd(projectile.Motion.Direction, projectile.Motion.Speed * 0.03);

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>BurningProjectile.events/Collision with Character.xml</c>. True if the
    /// flame was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Not the pellet's event with different numbers.</b> Three things are its
    /// own: it has no <c>place_free</c> muzzle test, so a flame that reached
    /// somebody through a wall still burns them; it remembers who it has already
    /// burnt and refuses to burn them twice; and it sets them alight, which is
    /// <see cref="Afterburn.Ignite"/>.
    /// <para>
    /// ⚠️ <b>the penetration count is tested before the victim is
    /// recorded.</b> <c>if (ds_list_size(enemiesHit) &gt;= penetrateCap and
    /// !perseverant) instance_destroy(); else ds_list_add(...)</c> with a cap of
    /// 1 — so the list is empty on the first hit and the flame survives it, and
    /// it is the <em>second</em> victim that kills the flame. A flame burns one
    /// person and dies on the next.
    /// </para>
    /// </remarks>
    private static bool BurningHitCharacter(
        ref WorldState state, ProjectileHandle handle, int slot, ref TickEvents events)
    {
        ref var flame = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        // The same four-clause guard the pellet has, and note what is missing
        // above it: no gunSetSolids/place_free, so unlike a Shot a flame does not
        // die for having arrived inside a wall.
        if (flame.OwnerSlot == slot
            || victim.Team == flame.Team
            || victim.Ubered
            || !Gml.Greater(victim.Hp, 0))
            return false;

        // `if (ds_list_find_index(enemiesHit, other.id) != -1) exit;` — already
        // burnt by this flame, so nothing happens at all. Not a destroy: the
        // flame carries on through them.
        var bit = 1UL << slot;
        if ((flame.EnemiesHit & bit) != 0)
            return false;

        DamageApi.Deal(ref events, slot, flame.OwnerSlot, ref victim, flame.HitDamage);
        victim.TimeUnscathed = 0;

        if (victim.LastDamageDealer != flame.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = flame.OwnerSlot;
        victim.LastDamageSource = flame.Source;

        Afterburn.Ignite(ref victim, flame, flame.OwnerSlot);

        victim.DealFlicker();

        // The count, then the record. See the remarks: with a cap of 1 this
        // destroys on the second victim, never the first.
        if (BitOperations.PopCount(flame.EnemiesHit) >= FlameStats.PenetrateCap(flame.Kind) && !flame.Perseverant)
        {
            state.FreeProjectile(handle);
            return true;
        }

        flame.EnemiesHit |= bit;
        return false;
    }

    /// <summary>
    /// <c>Rocket.events/Collision with Character.xml</c>. True if the rocket was
    /// destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Almost nothing the pellet's event guards on is guarded on here.</b>
    /// There is no <c>place_free</c>, no <c>hp &gt; 0</c>, no <c>ubered</c> and
    /// no owner check — a rocket that reaches an enemy through a wall still
    /// explodes on them, and the uber test happens later, inside the blast. The
    /// only branch is team, and a team-mate takes the second arm rather than
    /// nothing.
    /// </remarks>
    private static bool RocketHitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        ref var rocket = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        if (victim.Team == rocket.Team)
        {
            // else if (ds_list_find_index(playersPassed, other.player) == -1
            //          and other.player != ownerPlayer)
            //
            // A rocket loses a hundred pixels of range for each *different*
            // team-mate it passes through, and its own shooter is free. This is
            // the only thing that ever changes distanceToTravel.
            var bit = 1UL << slot;

            if ((rocket.PlayersPassed & bit) == 0 && slot != rocket.OwnerSlot)
            {
                rocket.PlayersPassed |= bit;
                rocket.DistanceToTravel -= RocketPassThroughCost;
            }

            return false;
        }

        victim.DealFlicker();

        // really_move_contact_solid(direction, speed), with the victim made
        // solid for the length of the call and *everything else* not — the
        // movement pass has already run gunUnsetSolids by the time a collision
        // event fires. So the rocket backs out of the character it is inside and
        // nothing else can stop it.
        BackOutOfCharacter(ref rocket, in victim, rocket.Motion.Direction + 180, rocket.Motion.Speed);

        // with(other) motion_add(other.direction, 2).
        //
        // ⚠️ Inside the `with`, `other` is the rocket — the same trap as the
        // pellet's knockback. A standing target is shoved two units along the
        // rocket's heading, not along its own.
        victim.Motion.MotionAdd(rocket.Motion.Direction, 2);

        return Explosions.Explode(world, ref state, handle, (byte)slot, ref events);
    }

    /// <summary>
    /// <c>Scripts/Physics/really_move_contact_solid.gml</c>, for a projectile
    /// sitting inside the character it just hit.
    /// </summary>
    /// <remarks>
    /// The script is <c>move_contact_solid(dir, speed)</c> followed by
    /// <c>move_outside_solid(dir + 180, speed)</c>, above the reference's own
    /// comment "move_contact_solid is broken. let's fix that." Only the second
    /// half is transcribed, and the stepping rule is the one
    /// <see cref="Movement.MoveOutsideSolid"/> measured: whole pixels along the
    /// direction, keeping the fractional part of the start, and if nothing is
    /// free within the allowance it goes the whole way anyway.
    /// <para>
    /// <b>Only the second half is transcribed, and that is measured rather than
    /// assumed.</b> The question was whether <c>move_contact_solid</c> moves an
    /// instance that is <em>already</em> in collision — which this one always
    /// is, because the collision event is what summoned it. Asked of the running
    /// game on 2026-08-25 with a character-sized solid and a rocket-shaped
    /// probe inside it: <b>it does not move at all</b>. Centred in the box,
    /// overlapping one edge, and along a diagonal, the position came back
    /// unchanged every time. So the two halves compose to "back out the way you
    /// came" and this is exact.
    /// </para>
    /// <para>
    /// The same probe confirmed the two things
    /// <see cref="Movement.MoveOutsideSolid"/> documents, which this copies:
    /// whole-pixel steps that <em>keep the fractional part</em> of the start
    /// (400.25 backed out to 390.25, not 390), and — when nothing within the
    /// allowance is free — moving the whole allowance anyway and staying stuck
    /// (a diagonal escape needing more than thirteen pixels landed at exactly
    /// thirteen, still inside).
    /// </para>
    /// </remarks>
    private static void BackOutOfCharacter(
        ref ProjectileState projectile,
        in CharacterState victim,
        double directionDegrees,
        double maxDistance)
    {
        // place_free's box, not distance_to_object's: no facing inflation. See
        // Aabb.ForCharacter.
        var victimBox = Aabb.ForCharacter(victim.Box, victim.X, victim.Y);

        // ⚠️ The MASK, not the box, and this is the line the gg_debug damage
        // divergence was about. `place_free` frees a rotated projectile as soon
        // as its opaque pixels are clear, which happens several whole pixels
        // before its bounding box is — six of them for the rocket at 351°,
        // whose top row is one pixel wide inside an eight-wide box. Backing out
        // on the box carries the explosion six pixels too far and costs a kill.
        // See ProjectileMask and GgDebugSplashOpenFindingTests.
        if (!ProjectileMask.Overlaps(in projectile, in victimBox))
            return;

        var radians = Gml.DegToRad(directionDegrees);
        var stepX = Math.Cos(radians);
        var stepY = -Math.Sin(radians);

        var startX = projectile.X;
        var startY = projectile.Y;
        var steps = CollisionWorld.RoundPosition(maxDistance);

        for (var step = 1; step <= steps; step++)
        {
            projectile.X = startX + (stepX * step);
            projectile.Y = startY + (stepY * step);

            if (!ProjectileMask.Overlaps(in projectile, in victimBox))
                return;
        }

        projectile.X = startX + (stepX * steps);
        projectile.Y = startY + (stepY * steps);
    }

    // ===== the spy's knife ==================================================

    /// <summary>
    /// <c>StabMask.events/Step.xml</c>: the knife rides its owner.
    /// </summary>
    /// <remarks>
    /// The whole event is <c>x = owner.x; y = owner.y;</c> plus the mirror,
    /// which <see cref="ProjectileState.XScale"/> derives instead of storing.
    /// <para>
    /// ⚠️ <b>So the knife trails its spy by exactly one tick's velocity.</b> A
    /// <c>StabMask</c> is younger than every character, so this runs after
    /// their Steps — where the reference has each character back at
    /// <c>x - hspeed</c>, before GM8's movement pass — and then GM8 adds the
    /// mask's <em>own</em> speed, which is zero. A spy running at 5 stabs from
    /// five pixels behind where they are standing.
    /// </para>
    /// <para>
    /// A knife whose owner has stopped existing cannot arise: the Revolver's
    /// Destroy event takes it, like the Blade's bubbles and the Minegun's
    /// mines.
    /// </para>
    /// </remarks>
    public static void StabStep(ref WorldState state)
    {
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var mask = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (mask.Kind != ProjectileKind.Stab)
                continue;

            if (mask.OwnerSlot == ProjectileState.NoOwner || !state.Alive[mask.OwnerSlot])
                continue;

            ref var owner = ref state.Characters[mask.OwnerSlot];

            mask.X = owner.X - owner.HSpeed;
            mask.Y = owner.Y - owner.VSpeed;
        }
    }

    /// <summary>
    /// Both halves of the knife's reachability test:
    /// <c>place_meeting</c> from the <em>spy</em>, or a clear line from the
    /// knife.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The overlap test is the spy's box against the victim's, not the
    /// knife's.</b> <c>with(ownerPlayer.object) connecting |= place_meeting(x,
    /// y, otherId)</c> — so a spy standing inside somebody stabs them through
    /// any wall, and the line test below is only for the reach the knife has
    /// beyond arm's length.
    /// <para>
    /// ⚠️ <b>And the line starts twelve pixels above the knife</b>, at chest
    /// height, ending at the victim's <em>origin</em> — which is at their feet.
    /// <c>collision_line_bulletblocking</c> is <c>gunSetSolids</c>' list
    /// exactly: obstacles and the map, bullet walls, intel gates always, team
    /// gates unless the map is changing, setup gates while the timer runs. No
    /// player walls.
    /// </para>
    /// </remarks>
    private static bool StabConnects(
        CollisionWorld world,
        in WorldState state,
        in ProjectileState mask,
        in Aabb targetBox,
        double targetX,
        double targetY)
    {
        if (mask.OwnerSlot != ProjectileState.NoOwner && state.Alive[mask.OwnerSlot])
        {
            ref readonly var spy = ref state.Characters[mask.OwnerSlot];

            // place_meeting's box on both sides: no facing inflation.
            if (Aabb.ForCharacter(spy.Box, spy.X, spy.Y).Overlaps(targetBox))
                return true;
        }

        return !world.CollisionLineHitsMask(mask.X, mask.Y - StabLineHeight, targetX, targetY)
               && !world.CollisionLineBlocksBullets(
                   mask.X, mask.Y - StabLineHeight, targetX, targetY, state.BulletFilter);
    }

    /// <summary>
    /// <c>StabMask.events/Collision with Character.xml</c>. True if the knife
    /// was destroyed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A stab hits two things and only then goes away</b> —
    /// <c>maxKills = 2</c> — so a knife buried in a medic still has one left for
    /// the patient behind them.
    /// <para>
    /// ⚠️ <b>A stab that cannot reach destroys itself.</b> The <c>else</c> is
    /// inside the four-clause guard, so touching a team-mate, a corpse or an
    /// ubered enemy costs nothing, and touching a reachable-looking enemy
    /// through a wall ends the knife outright.
    /// </para>
    /// <para>
    /// ⚠️ <b>Whether it is a backstab is decided by the two facings agreeing</b>
    /// — <c>sign(other.image_xscale) == sign(image_xscale)</c>, the victim's
    /// against the <em>knife's</em>, which is the direction the spy committed to
    /// thirty-two ticks earlier and not where they are looking now. Both are
    /// 200 damage; only the kill log and the scoreboard tell them apart.
    /// </para>
    /// </remarks>
    private static bool StabHitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        ref var mask = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        if (mask.OwnerSlot == slot
            || victim.Team == mask.Team
            || !Gml.Greater(victim.Hp, 0)
            || victim.Ubered)
        {
            return false;
        }

        if (!StabConnects(
                world, in state, in mask,
                Aabb.ForCharacter(victim.Box, victim.X, victim.Y),
                victim.X, victim.Y))
        {
            state.FreeProjectile(handle);
            return true;
        }

        DamageApi.Deal(ref events, slot, mask.OwnerSlot, ref victim, mask.HitDamage);

        if (victim.LastDamageDealer != mask.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = mask.OwnerSlot;
        victim.LastDamageSource = victim.Facing == mask.XScale
            ? DamageSource.Backstab
            : DamageSource.Knife;

        // ⚠️ No `timeUnscathed = 0` and no dealFlicker — a knife is the one
        // character damage path with neither. At 200 damage the omission is
        // rarely observable, but a stab that a spawn bubble blocks leaves the
        // healing ramp climbing.
        mask.NumKills += 1;

        if (mask.NumKills < StabMaxKills)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>StabMask.events/Collision with Sentry.xml</c>: the character event
    /// with the hp and uber clauses gone.
    /// </summary>
    private static bool StabHitSentry(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot)
    {
        ref var mask = ref state.Projectiles[handle.Index];
        ref var sentry = ref state.Sentries[slot];

        if (sentry.Team == mask.Team)
            return false;

        if (!StabConnects(
                world, in state, in mask,
                Sentries.BodyBox(sentry), sentry.X, sentry.Y))
        {
            state.FreeProjectile(handle);
            return true;
        }

        Sentries.Damage(ref state, slot, mask.HitDamage, mask.OwnerSlot, mask.Source);

        mask.NumKills += 1;

        if (mask.NumKills < StabMaxKills)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    // ===== Quote's Blade and its Bubble =====================================

    /// <summary>
    /// <c>BladeB.events/Collision with Character.xml</c>. True if the blade was
    /// destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Not the pellet's event with a bigger number in it.</b> Four things
    /// differ, and three of them favour the blade:
    /// <list type="bullet">
    /// <item>⚠️ <b>there is no <c>place_free</c> muzzle test</b>, so a blade
    /// that reached somebody through a wall still sticks in them;</item>
    /// <item>⚠️ <b>the guard is two clauses, not four</b> — not yourself and
    /// not a team-mate. <c>ubered</c> is checked <em>inside</em>, around the
    /// damage alone, and <c>hp &gt; 0</c> is not checked at all;</item>
    /// <item>⚠️ <b>so an ubered enemy still stops the blade and is still
    /// shoved</b>, taking no damage — the <c>motion_add</c>, the
    /// <c>dealFlicker</c> and the <c>instance_destroy</c> are all outside the
    /// uber guard;</item>
    /// <item>the shove is <b>0.4</b> of the blade's speed rather than a
    /// pellet's 0.03, which at speed 12 is nearly five units of velocity.</item>
    /// </list>
    /// </remarks>
    private static bool BladeHitCharacter(
        ref WorldState state, ProjectileHandle handle, int slot, ref TickEvents events)
    {
        ref var blade = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        // if(other.id != ownerPlayer.object and other.team != team)
        if (blade.OwnerSlot == slot || victim.Team == blade.Team)
            return false;

        if (!victim.Ubered)
        {
            DamageApi.Deal(ref events, slot, blade.OwnerSlot, ref victim, blade.HitDamage);
            victim.TimeUnscathed = 0;

            if (victim.LastDamageDealer != blade.OwnerSlot && victim.LastDamageDealer != slot)
            {
                victim.SecondToLastDamageDealer = victim.LastDamageDealer;
                victim.AssistTimer = victim.FinishOffTimer;
            }

            victim.FinishOffTimer = GameConstants.AssistTime;
            victim.LastDamageDealer = blade.OwnerSlot;
            victim.LastDamageSource = blade.Source;
        }

        // with(other) motion_add(other.direction, other.speed*0.4) — `other`
        // inside the `with` is the blade, so both arguments are the blade's.
        victim.Motion.MotionAdd(blade.Motion.Direction, blade.Motion.Speed * BladeKnockback);
        victim.DealFlicker();

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// The <c>Collision with Bubble</c> event of whichever projectile this is.
    /// True if it was destroyed.
    /// </summary>
    /// <remarks>
    /// Only three projectiles have one, and they do not agree:
    /// <list type="bullet">
    /// <item>a <c>Shot</c> dies on an enemy bubble <b>unless it is
    /// perseverant</b> — which is what the one-in-three <c>choose(0,0,1)</c>
    /// draw in every pellet's Create event has been for since phase 5;</item>
    /// <item>a <c>Needle</c> and a <c>Flare</c> die on an enemy bubble with no
    /// such let-off.</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>A team-mate's bubble stops none of them</b> — the condition is
    /// <c>team != other.team</c> in all three — even though the bubble's own
    /// side of the pellet pair pops for a <em>friendly</em> perseverant shot.
    /// The asymmetry is the reference's.
    /// </para>
    /// <para>
    /// <b>Both halves of the pair are applied here</b>, not just this one, and
    /// the same is true of <see cref="BubbleHitProjectiles"/> — see
    /// <see cref="ResolveBubblePair"/> for why.
    /// </para>
    /// </remarks>
    private static bool HitBubble(ref WorldState state, ProjectileHandle handle, in Aabb projectileBox)
    {
        if (state.Projectiles[handle.Index].Kind
            is not (ProjectileKind.Shot or ProjectileKind.Needle or ProjectileKind.Flare
                or ProjectileKind.Rocket or ProjectileKind.Flame))
        {
            return false;
        }

        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var bubbleHandle = state.LiveProjectileHandle(i);

            // ⚠️ Both masks, not both boxes. A pellet arriving at a bubble is
            // rotated to its heading, so its box answers yes for pairs that
            // never touch. See ProjectileMask.
            if (state.Projectiles[bubbleHandle.Index].Kind != ProjectileKind.Bubble
                || !ProjectileMask.Overlaps(
                        in state.Projectiles[handle.Index],
                        in projectileBox,
                        in state.Projectiles[bubbleHandle.Index],
                        state.Projectiles[bubbleHandle.Index].Box))
            {
                i++;
                continue;
            }

            if (ResolveBubblePair(ref state, bubbleHandle, handle))
                return true;

            // The bubble may or may not have gone; either way the projectile
            // survived, so keep looking for the next one it is inside.
            if (state.IsLive(bubbleHandle))
                i++;
        }

        return false;
    }

    /// <summary>
    /// One bubble against one other projectile: <em>both</em> instances' events,
    /// applied together. True if <paramref name="otherHandle"/> was destroyed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A collision between two projectiles runs two events, and the port
    /// has to apply both from whichever side it reaches the pair first.</b> GM8
    /// fires <c>Bubble.events/Collision with Shot</c> on the bubble and
    /// <c>Shot.events/Collision with Bubble</c> on the pellet, and each is
    /// written on the assumption that the other has also run: the pellet's
    /// spares a perseverant shot and the bubble's pops for one anyway. Applying
    /// only the event of whichever instance the port happened to walk first
    /// would make the outcome depend on which was created first — and, for the
    /// commonest case of a screen that is older than the pellet flying into it,
    /// would leave every pellet alive and make <c>perseverant</c> meaningless.
    /// <para>
    /// ✅ <b>MEASURED live 2026-08-27</b> (<c>tools/probes/probe-bubble-pair.js</c>),
    /// across all four cells of the team x <c>perseverant</c> table, and this
    /// transcription was right. The decisive row is an enemy ordinary pellet,
    /// where <b>both</b> instances die — an outcome no single event can produce.
    /// ⚠️ So <b>a projectile that destroys itself in its own collision event is
    /// still there for the other instance's</b>: GM8 defers removal to the end of
    /// the step, not the end of the event. See <c>docs/TRAPS-SIMULATION.md</c>,
    /// the Quote section, and <c>docs/SIMULATION.md</c>, "Settled: Quote's Blade".
    /// </para>
    /// </remarks>
    private static bool ResolveBubblePair(
        ref WorldState state, ProjectileHandle bubbleHandle, ProjectileHandle otherHandle)
    {
        ref var bubble = ref state.Projectiles[bubbleHandle.Index];
        ref var other = ref state.Projectiles[otherHandle.Index];

        var enemy = other.Team != bubble.Team;

        bool bubblePops;
        var otherDies = false;

        switch (other.Kind)
        {
            case ProjectileKind.Shot:
                // Bubble: `team != other.team or other.perseverant`.
                // Shot:   `team != other.team and !perseverant`.
                bubblePops = enemy || other.Perseverant;
                otherDies = enemy && !other.Perseverant;
                break;

            case ProjectileKind.Needle:
            case ProjectileKind.Flare:
                // Both sides are the same single line, so the pair annihilates.
                bubblePops = enemy;
                otherDies = enemy;
                break;

            case ProjectileKind.Rocket:
            case ProjectileKind.Flame:
                // ⚠️ Neither has a Collision with Bubble event of its own — a
                // Flame is a BurningProjectile and only the *Flare* overrides
                // it — so both fly on through a bubble that dies on them.
                bubblePops = enemy;
                break;

            case ProjectileKind.Blade:
                // `ownerPlayer == other.ownerPlayer or team != other.team`.
                bubblePops = enemy || other.OwnerSlot == bubble.OwnerSlot;
                break;

            case ProjectileKind.Bubble:
                if (!enemy)
                {
                    // Same team: a shove apart rather than a pop, and each
                    // bubble applies its own when the stage reaches it.
                    bubble.Motion.MotionAdd(
                        Gml.PointDirection(bubble.X, bubble.Y, other.X, other.Y),
                        BubbleRepulsion);
                    return false;
                }

                bubblePops = true;
                otherDies = true;
                break;

            case ProjectileKind.Mine:
                if (enemy && !other.Stickied && !other.Bubbled)
                {
                    // Two component writes, which re-derive the polar pair and
                    // snap it — the same shape as the airblast's.
                    other.Motion.HSpeed *= -1;
                    other.Motion.VSpeed *= -1;
                    other.Bubbled = true;
                    other.Reflector = bubble.OwnerSlot;
                }

                // ⚠️ Outside the guard: a bubble that touches a stuck mine still
                // spends half its life on it. And this is the one place the
                // `alarm[0] div 2` is live — the character event's copy is
                // followed by an unconditional destroy.
                if (bubble.Lifetime > 10)
                {
                    bubble.Lifetime /= 2;
                    return false;
                }

                bubblePops = true;
                break;

            default:
                return false;
        }

        if (bubblePops)
            state.FreeProjectile(bubbleHandle);

        if (!otherDies)
            return false;

        state.FreeProjectile(otherHandle);
        return true;
    }

    /// <summary>
    /// Every one of a <c>Bubble</c>'s sixteen collision events, in the order
    /// GM8 runs them. True if the bubble was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>The order is the other object's id, ascending</b>, which is how GM8
    /// keys a collision event — and it matters here as it does nowhere else in
    /// this file, because a bubble has sixteen of them and most end in a
    /// destroy. The ids: <c>Obstacle</c> 0, <c>Character</c> 2, <c>Rocket</c> 3,
    /// <c>Shot</c> 97, <c>Mine</c> 99, <c>TeamGate</c> 110, <c>Needle</c> 130,
    /// <c>Sentry</c> 460, <c>BladeB</c> 465, <c>Bubble</c> 471,
    /// <c>BulletWall</c> 478, <c>IntelGate</c> 516,
    /// <c>ControlPointSetupGate</c> 536, <c>Generator</c> 560, <c>Flare</c> 792,
    /// <c>BurningProjectile</c> 793.
    /// <para>
    /// The last two are folded together: a <c>Flare</c> is a
    /// <c>BurningProjectile</c>, so it fires both events, and both are the same
    /// line — <c>if(team != other.team) instance_destroy()</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>The four wall events are exactly <c>gunSetSolids</c> minus
    /// <c>Obstacle</c>.</b> <c>BulletWall</c> and <c>IntelGate</c> destroy
    /// unconditionally, <c>TeamGate</c> destroys unless the map is changing and
    /// <c>ControlPointSetupGate</c> destroys while the setup timer runs — which
    /// is what <see cref="SolidRect.IsSolidTo"/> already answers for a bullet,
    /// including the trap that an intel gate stays shut to bullets during the
    /// victory screen. There is no <c>PlayerWall</c> event, and a bullet's list
    /// has no player wall in it either.
    /// </para>
    /// </remarks>
    private static bool BubbleCollide(
        CollisionWorld world, ref WorldState state, ProjectileHandle handle, ref TickEvents events)
    {
        // 0: Obstacle. It moves the bubble and never destroys it, so it has to
        // run before anything that reads the box.
        BubbleHitObstacle(world, ref state, handle);

        var bubbleBox = state.Projectiles[handle.Index].Box;

        // 2: Character.
        //
        // ⚠️ Creation order and the MASK, for the same two reasons the rocket's
        // loop uses them: a bubble that pops on somebody is destroyed there, so
        // which character it meets first is the outcome; and a rotated
        // projectile's box is a strict over-estimate of its mask.
        Span<byte> characters = stackalloc byte[WorldState.MaxSlots];
        var liveCount = state.LiveCharactersInCreationOrder(characters);

        for (var i = 0; i < liveCount; i++)
        {
            var slot = characters[i];

            if (!state.Alive[slot])
                continue;

            ref var victim = ref state.Characters[slot];
            var victimBox = Aabb.ForCharacter(victim.Box, victim.X, victim.Y);

            if (!ProjectileMask.Overlaps(
                    in state.Projectiles[handle.Index], in bubbleBox, in victimBox))
            {
                continue;
            }

            if (BubbleHitCharacter(ref state, handle, slot, ref events))
                return true;
        }

        // 3, 97, 99, 130, 465, 471, 792/793: the seven projectile events, in id
        // order. Walked once, because a bubble that survives one of them has not
        // moved and the next event asks about a different kind.
        if (BubbleHitProjectiles(ref state, handle, bubbleBox))
            return true;

        // 110, 478, 516, 536: the gates and the bullet wall, interleaved among
        // the ids above but folded into one test — none of them can move the
        // bubble, and all four end in the same destroy, so the only thing their
        // order could decide is which of four identical outcomes happened.
        if (world.MeetsNonObstacleSolid(bubbleBox, state.BulletFilter))
        {
            state.FreeProjectile(handle);
            return true;
        }

        // 560: Generator, whose event is the pellet's.
        return HitGenerator(world, ref state, handle, Team.Red, ref events)
               || HitGenerator(world, ref state, handle, Team.Blue, ref events);
    }

    /// <summary>
    /// <c>Bubble.events/Collision with Character.xml</c>. True if the bubble was
    /// destroyed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A cloaked spy who could stab is invisible to bubbles.</b> The
    /// event's first two lines are <c>if(other.cloak) if
    /// (other.currentWeapon.readyToStab) exit;</c> — so a bubble passes through
    /// one without popping and without giving them away, which is the whole
    /// point. See <see cref="WeaponState.ReadyToStab"/>.
    /// <para>
    /// ⚠️ <b>There is no "not the shooter" clause</b>, unlike every other
    /// projectile's character event; the team test does that job, because a
    /// bubble and its owner are always on the same team.
    /// </para>
    /// <para>
    /// ⚠️ <b>The <c>alarm[0] div 2</c> in the middle is dead code.</b> Both
    /// arms of it are followed by an unconditional <c>instance_destroy()</c> at
    /// the end of the event, so the bubble is gone either way and the halved
    /// lifetime is never read. It is live in the <em>mine</em> event, which has
    /// no trailing destroy — see <see cref="BubbleHitProjectiles"/>.
    /// </para>
    /// <para>
    /// There is no knockback and no <c>dealFlicker</c>: a bubble deals 0.75 and
    /// moves nobody.
    /// </para>
    /// </remarks>
    private static bool BubbleHitCharacter(
        ref WorldState state, ProjectileHandle handle, int slot, ref TickEvents events)
    {
        ref var bubble = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        if (victim.Cloak && state.Weapons[slot].ReadyToStab)
            return false;

        if (victim.Team == bubble.Team || victim.Ubered || !Gml.Greater(victim.Hp, 0))
            return false;

        DamageApi.Deal(ref events, slot, bubble.OwnerSlot, ref victim, bubble.HitDamage);

        if (victim.LastDamageDealer != bubble.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = bubble.OwnerSlot;
        victim.LastDamageSource = bubble.Source;

        // ⚠️ No `timeUnscathed = 0`. Every other damaging collision event in the
        // game resets it and this one does not, so a Quote's bubbles cannot stop
        // a medic's healing ramp climbing or a medic's own regeneration.
        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// A bubble's seven projectile collision events. True if the bubble was
    /// destroyed.
    /// </summary>
    /// <remarks>
    /// In id order: <c>Rocket</c> (3), <c>Shot</c> (97), <c>Mine</c> (99),
    /// <c>Needle</c> (130), <c>BladeB</c> (465), <c>Bubble</c> (471), and the
    /// two burning ones (792/793).
    /// <list type="bullet">
    /// <item>⚠️ <b>A <c>Shot</c> pops a bubble if it is an enemy's <em>or</em>
    /// if it is perseverant</b> — so a Quote's own team-mate's lucky pellet
    /// clears their screen for them, which is the other half of the asymmetry
    /// <see cref="HitBubble"/> describes.</item>
    /// <item>⚠️ <b>A <c>BladeB</c> pops a bubble if it is an enemy's <em>or</em>
    /// if it belongs to the same player</b> — a Quote's own thrown blade cuts
    /// through their own screen, and a team-mate Quote's does not.</item>
    /// <item>⚠️ <b>A <c>Mine</c> is reflected rather than popped.</b> An enemy
    /// mine that is neither stuck nor already bubbled has both components
    /// negated and is credited to the bubble's owner, exactly as an airblast
    /// would — and <c>bubbled</c> makes it once only.</item>
    /// <item>⚠️ <b>Two same-team bubbles push each other apart</b> at 0.5 a
    /// tick rather than popping — <c>motion_add(point_direction(x, y, other.x,
    /// other.y), -0.5)</c>, a negative speed along the direction <em>towards</em>
    /// the neighbour. Enemy bubbles pop each other.</item>
    /// </list>
    /// <para>
    /// The mine event is the one place the <c>alarm[0] div 2</c> is live: a
    /// bubble that reflects a mine with more than ten ticks left has its
    /// lifetime <b>halved</b> — GM8's <c>div</c>, integer division — and one
    /// with ten or fewer is destroyed. Nothing follows it, so unlike the
    /// character event both arms are reachable.
    /// </para>
    /// </remarks>
    private static bool BubbleHitProjectiles(ref WorldState state, ProjectileHandle handle, in Aabb bubbleBox)
    {
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var otherHandle = state.LiveProjectileHandle(i);

            // Both masks, the same as HitBubble's side of the pair.
            if (otherHandle == handle
                || !ProjectileMask.Overlaps(
                        in state.Projectiles[handle.Index],
                        in bubbleBox,
                        in state.Projectiles[otherHandle.Index],
                        state.Projectiles[otherHandle.Index].Box))
            {
                i++;
                continue;
            }

            ResolveBubblePair(ref state, handle, otherHandle);

            if (!state.IsLive(handle))
                return true;

            if (state.IsLive(otherHandle))
                i++;
        }

        return false;
    }

    /// <summary>
    /// <c>Bubble.events/Collision with Obstacle.xml</c>: back out, try to slide
    /// past a corner, and stop whichever axes are blocked.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Nothing here destroys the bubble</b>, which is what makes a Quote's
    /// screen drape itself over the terrain instead of evaporating on it. It is
    /// the only projectile in the game with a wall response rather than a wall
    /// death — a mine sticks, and everything else dies.
    /// <para>
    /// ⚠️ <b>The six-pixel nudge is vertical only, and it is tried in one
    /// order</b>: down first, then up, and only if moving that way would not
    /// also be blocked. So a bubble hugging a ceiling drops under it and one on
    /// a floor climbs over — but a bubble in a vertical shaft is simply stopped.
    /// </para>
    /// <para>
    /// <c>wallSetSolid()</c> brackets the whole event, so the solids are
    /// <c>Obstacle</c> alone — the map's own mask included, because
    /// <c>CustomMapO</c> descends from it. That is
    /// <see cref="CollisionFilter.ForSentry"/>'s list, which is named for its
    /// first user rather than for the script.
    /// </para>
    /// </remarks>
    private static void BubbleHitObstacle(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        ref var bubble = ref state.Projectiles[handle.Index];

        if (!MeetsObstacle(world, in bubble, bubble.X, bubble.Y))
            return;

        // move_outside_solid(direction-180, speed).
        BackOutOfObstacles(world, ref bubble, bubble.Motion.Direction - 180, bubble.Motion.Speed);

        var hspeed = bubble.Motion.HSpeed;
        var vspeed = bubble.Motion.VSpeed;

        if (MeetsObstacle(world, in bubble, bubble.X + hspeed, bubble.Y + vspeed)
            && !MeetsObstacle(world, in bubble, bubble.X + hspeed, bubble.Y + vspeed + BubbleWallSlide))
        {
            bubble.Y += BubbleWallSlide;
        }
        else if (MeetsObstacle(world, in bubble, bubble.X + hspeed, bubble.Y + vspeed)
                 && !MeetsObstacle(world, in bubble, bubble.X + hspeed, bubble.Y + vspeed - BubbleWallSlide))
        {
            bubble.Y -= BubbleWallSlide;
        }

        // Each test reads whatever the writes above it left behind, which is why
        // these are three statements rather than one decision.
        if (MeetsObstacle(world, in bubble, bubble.X + bubble.Motion.HSpeed, bubble.Y))
            bubble.Motion.HSpeed = 0;

        if (MeetsObstacle(world, in bubble, bubble.X, bubble.Y + bubble.Motion.VSpeed))
            bubble.Motion.VSpeed = 0;

        if (MeetsObstacle(world, in bubble, bubble.X + bubble.Motion.HSpeed, bubble.Y + bubble.Motion.VSpeed))
            bubble.Motion.Speed = 0;
    }

    /// <summary><c>place_meeting(x, y, Obstacle)</c> for a bubble at a trial position.</summary>
    private static bool MeetsObstacle(CollisionWorld world, in ProjectileState bubble, double x, double y)
    {
        var sprite = ProjectileSprites.For(bubble.Kind);
        var box = Aabb.ForRotatedSprite(
            sprite.Bbox, sprite.OriginX, sprite.OriginY, x, y, bubble.ImageAngle);

        return !world.PlaceFree(box, CollisionFilter.ForSentry());
    }

    /// <summary><c>move_outside_solid</c> against <c>Obstacle</c> alone.</summary>
    /// <remarks>
    /// The stepping rule <see cref="Movement.MoveOutsideSolid"/> measured, over
    /// the <c>wallSetSolid</c> list rather than a character's — and unlike
    /// <see cref="BackOutOfWalls"/>, which asks the map's mask only, this one
    /// sees an <c>Obstacle</c> the map file placed as well.
    /// </remarks>
    private static void BackOutOfObstacles(
        CollisionWorld world,
        ref ProjectileState bubble,
        double directionDegrees,
        double maxDistance)
    {
        var radians = Gml.DegToRad(directionDegrees);
        var stepX = Math.Cos(radians);
        var stepY = -Math.Sin(radians);

        var startX = bubble.X;
        var startY = bubble.Y;
        var steps = CollisionWorld.RoundPosition(maxDistance);

        for (var step = 1; step <= steps; step++)
        {
            if (MeetsObstacle(world, in bubble, startX + (stepX * step), startY + (stepY * step)))
                continue;

            bubble.X = startX + (stepX * step);
            bubble.Y = startY + (stepY * step);
            return;
        }

        bubble.X = startX + (stepX * steps);
        bubble.Y = startY + (stepY * steps);
    }

    /// <summary>
    /// <c>BladeB.events/End Step.xml</c>: every blade in flight drinks a unit
    /// of its owner's magazine.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the Blade's entire ammo cost.</b> Neither click spends
    /// anything at the moment it is made; the magazine drains while a blade is
    /// out and is charged the difference up to <c>refireTime</c> when it lands
    /// (<see cref="WorldState.FreeProjectile"/>). A blade is younger than every
    /// character, so its End Step runs after all of theirs — which is why this
    /// is its own phase rather than a line inside the character loop.
    /// <para>
    /// The reference guards it on <c>instance_exists(owner.currentWeapon)</c>,
    /// and a blade with no owner cannot exist here: the Blade's Destroy event
    /// takes it along.
    /// </para>
    /// </remarks>
    public static void BladeEndStep(ref WorldState state)
    {
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var blade = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (blade.Kind != ProjectileKind.Blade)
                continue;

            if (blade.OwnerSlot == ProjectileState.NoOwner || !state.Alive[blade.OwnerSlot])
                continue;

            state.Weapons[blade.OwnerSlot].AmmoCount -= 1;
            blade.AmmoDrained += 1;
        }
    }
}
