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
/// <b>A projectile's per-tick behaviour is not in its object.</b>
/// <c>Objects/Projectiles/Shot.events/</c> holds a Create, an Alarm 0 and four
/// collision events and nothing else — but that is not the whole of a
/// pellet's life. <c>Scripts/Physics/System/move_all_bullets.gml</c> is, and it
/// is reached from <c>Objects/Networking/GameServer.events/Step.xml:16</c>.
/// This repo read the object and concluded a pellet had no gravity and no wall
/// test; both conclusions were wrong, and <see cref="Move"/> is the corrected
/// transcription.
/// </para>
/// </remarks>
public static partial class Projectiles
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
    /// <b>Not a pellet's 0.15.</b> <c>move_all_bullets.gml</c>'s
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
    /// <b>Only a mine's blast uses it.</b> The three explosions that pop
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
    /// <b>Thirteen times a pellet's 0.03</b>, and — unlike the pellet's — it
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
    /// <b>A mine substeps and a pellet does not.</b> Its whole movement is a
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
    /// <b>That subtraction is why this pass has to run before the characters
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

            // A Bubble has no `with` block in move_all_bullets at all — it
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

        // A snapshot of handles, not a cursor over the live array — and the
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

        // A Mine has no `Collision with Character` event at all — its
        // Objects/Projectiles/Mine.events/ holds Sentry and Generator and
        // nothing else. So a mine flies straight through people, and the only
        // thing it can do to a character is explode near them — or stick to a
        // sentry, which is why it does not skip the sentry loop below.
        if (state.Projectiles[handle.Index].Kind != ProjectileKind.Mine)
        {
            // CREATION order, not slot order, and the difference is a kill.
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
        // Build order, not slot order, for the reason the character loop
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
        // A Rocket and a Flame have no such event at all, so both fly
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
    /// <b>The <c>Shot</c>'s and the flame's generator events have no wall
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
                // 0.36 of the knife's damage, and no line-of-sight
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
            // burning ones and unlike the pellet's, **no wall test**. A
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
    /// game with a character-sized solid and a rocket-shaped probe inside it:
    /// <b>it does not move at all</b>. Centred in the box,
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

        // The MASK, not the box, and this is the line the gg_debug damage
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
}
