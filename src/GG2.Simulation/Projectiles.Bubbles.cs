using GG2.Protocol;

namespace GG2.Simulation;

public static partial class Projectiles
{
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
    /// ✅ <b>MEASURED live 2026-08-27</b> (a live-game probe),
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
    /// moves nobody. It does reset <c>timeUnscathed</c> and shorten a sandvich,
    /// because the event's damage line is <c>damageCharacter</c>
    /// (<c>Collision with Character.xml:6</c>), which does both.
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

        DamageApi.DamageCharacter(ref events, slot, bubble.OwnerSlot, ref victim, bubble.HitDamage);

        if (victim.LastDamageDealer != bubble.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = bubble.OwnerSlot;
        victim.LastDamageSource = bubble.Source;

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
}
