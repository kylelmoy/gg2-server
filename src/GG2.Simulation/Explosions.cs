using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// <c>Objects/Projectiles/Rocket.events/User Event 5.xml</c>: a projectile
/// stops existing and everything nearby finds out.
/// </summary>
/// <remarks>
/// <b>The largest single event in the reference, and almost all of it is one
/// <c>with(Character)</c> loop.</b> The direct hit above it is a footnote by
/// comparison: 25 damage and an attribution, the same shape a scattergun pellet
/// deals. The loop is where rocket jumping, knockback, team-mate boosting and
/// the falloff curve all live, and every branch of it is reachable from a single
/// rocket.
/// <para>
/// Seven more <c>with</c> blocks follow the character one — <c>Sentry</c>,
/// <c>Generator</c>, <c>DeadGuy</c>, <c>Gib</c>, <c>Mine</c>, <c>LooseSheet</c>
/// and <c>Bubble</c>. None of those objects exists in the port yet; three of
/// them (<c>DeadGuy</c>, <c>Gib</c>, <c>LooseSheet</c>) never will, because they
/// are debris the client draws and nothing on the server can observe.
/// </para>
/// </remarks>
public static class Explosions
{
    /// <summary>
    /// How far a blast reaches. <c>Rocket.events/Create.xml:19</c>.
    /// </summary>
    /// <remarks>
    /// A lookup rather than a field on <see cref="ProjectileState"/> because
    /// nothing in the reference ever reassigns <c>blastRadius</c> — checked
    /// across <c>Objects/</c> and <c>Scripts/</c>, where the only writes are the
    /// Create events of <c>Rocket</c> (65), <c>Mine</c> (40), <c>Sentry</c> (65)
    /// and <c>Generator</c> (400). The pyro's airblast, which is the only thing
    /// in the game that edits a projectile in flight, does not touch it — see
    /// <see cref="ExplosionDamageFor"/> for the same argument spelled out.
    /// </remarks>
    public static double BlastRadiusFor(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Rocket => 65,
        ProjectileKind.Mine => 40,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "this projectile does not explode"),
    };

    /// <summary>
    /// Splash damage at the centre of the blast, falling off linearly to the
    /// edge. <c>Rocket.events/Create.xml:18</c>.
    /// </summary>
    /// <remarks>
    /// A lookup for the same reason as <see cref="BlastRadiusFor"/>: no write
    /// outside a Create event ever changes it. The pyro's airblast does assign
    /// <c>explosionDamage = 30</c> to a rocket it reflects
    /// (<c>Flamethrower.events/User Event 2.xml:67</c>), but 30 is what a rocket
    /// already has — the line restores a value nothing has moved. If a later
    /// weapon genuinely varies it, this becomes a field on
    /// <see cref="ProjectileState"/> like <see cref="ProjectileState.Knockback"/>,
    /// which the same airblast really does change.
    /// </remarks>
    public static double ExplosionDamageFor(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Rocket => 30,
        ProjectileKind.Mine => 45,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "this projectile does not explode"),
    };

    /// <summary>
    /// The blast, and the destruction of the projectile that caused it. True if
    /// it went off.
    /// </summary>
    /// <param name="directVictim">
    /// The slot the projectile struck head-on, or <see cref="CharacterState.Nobody"/>
    /// for a rocket that hit the map or ran out of range. The reference's
    /// <c>characterHit</c>.
    /// </param>
    /// <remarks>
    /// <b>It can decline, and then the rocket flies on.</b> The event's outer
    /// guard is <c>if (characterHit.id != ownerPlayer.object)</c> and the
    /// <c>instance_destroy()</c> is the last line <em>inside</em> it — so a
    /// rocket that somehow reports its own owner as the thing it hit is neither
    /// exploded nor deleted.
    /// <para>
    /// The guard also reads as nonsense until you know that <b>GML's
    /// <c>self</c> is -1</b>. <c>characterHit</c> starts at -1, so on a wall
    /// explosion <c>characterHit.id</c> resolves to the <em>rocket's own</em> id
    /// and the comparison is trivially true. It is not testing the sentinel.
    /// </para>
    /// </remarks>
    public static bool Explode(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        byte directVictim,
        ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        var rocket = state.Projectiles[handle.Index];

        // `ownerPlayer.object`: the owner's *current* character, which is -1
        // once they are dead. Every comparison the event makes against the owner
        // is against this and not against the player, so a rocket outliving its
        // shooter stops counting them as its owner — it can splash their corpse's
        // replacement and it stops being a rocket jump.
        var ownerCharacter = rocket.OwnerSlot != ProjectileState.NoOwner && state.Alive[rocket.OwnerSlot]
            ? rocket.OwnerSlot
            : CharacterState.Nobody;

        if (directVictim != CharacterState.Nobody && directVictim == ownerCharacter)
            return false;

        if (directVictim != CharacterState.Nobody)
            DealDirectHit(ref state, in rocket, directVictim, ownerCharacter, ref events);

        SplashCharacters(world, ref state, in rocket, ownerCharacter, ref events);
        SplashSentries(ref state, in rocket);
        SplashGenerators(ref state, in rocket);
        PopBubblesByBox(ref state, rocket.Box, BlastRadiusFor(rocket.Kind) * RocketBubblePopFactor);

        state.FreeProjectile(handle);
        events.Exploded = true;
        return true;
    }

    /// <summary>
    /// The same event with a <em>sentry</em> as the direct victim —
    /// <c>Rocket.events/Collision with Sentry.xml</c> sets
    /// <c>characterHit = other.id</c> and fires <c>event_user(5)</c>.
    /// </summary>
    /// <remarks>
    /// The direct hit is <c>damageAuto</c>, which dispatches to
    /// <c>damageSentry</c> — the assist bookkeeping between it and the dealer
    /// writes is guarded on <c>object_is_ancestor(..., Character)</c> and is
    /// skipped. Note the sentry then takes the splash as well, at distance
    /// zero: a direct rocket deals 25 + 30. There is no contact reposition and
    /// no <c>motion_add</c> shove — the Sentry collision event has neither.
    /// </remarks>
    public static bool ExplodeOnSentry(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int sentrySlot,
        ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        var rocket = state.Projectiles[handle.Index];

        // `if (characterHit.id != ownerPlayer.object)`: a sentry's instance id
        // is never the owner's character, so the guard passes. The ubered test
        // passes too — a sentry's `ubered` is 0 from its Create event.
        var ownerCharacter = rocket.OwnerSlot != ProjectileState.NoOwner && state.Alive[rocket.OwnerSlot]
            ? rocket.OwnerSlot
            : CharacterState.Nobody;

        Sentries.Damage(ref state, sentrySlot, rocket.HitDamage, rocket.OwnerSlot, rocket.Source);

        SplashCharacters(world, ref state, in rocket, ownerCharacter, ref events);
        SplashSentries(ref state, in rocket);
        SplashGenerators(ref state, in rocket);
        PopBubblesByBox(ref state, rocket.Box, BlastRadiusFor(rocket.Kind) * RocketBubblePopFactor);

        state.FreeProjectile(handle);
        events.Exploded = true;
        return true;
    }

    /// <summary>
    /// How much of a rocket's blast radius reaches a bubble:
    /// <c>distance_to_object(other) &lt; other.blastRadius*0.66</c>.
    /// </summary>
    private const double RocketBubblePopFactor = 0.66;

    /// <summary>
    /// <c>with(Bubble)</c> for an explosion that measures with
    /// <c>distance_to_object</c> — the rocket's and the generator's.
    /// </summary>
    /// <remarks>
    /// <b>The three explosions that pop bubbles use three different
    /// formulas, and none of them is the one it uses on characters.</b> A
    /// rocket asks <c>distance_to_object &lt; blastRadius * 0.66</c>; a
    /// generator asks <c>distance_to_object &lt; blastRadius</c>, its full 400;
    /// and a mine asks <c>point_distance &lt; blastRadius + selfPopRadius</c>,
    /// centre to centre and <em>widened</em>. Reading any of them as "the blast
    /// radius" would be wrong for two of the three.
    /// <para>
    /// None of the three has a team test: a blast pops its own side's bubbles
    /// as readily as the enemy's.
    /// </para>
    /// </remarks>
    public static void PopBubblesByBox(ref WorldState state, in Aabb sourceBox, double radius)
    {
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var handle = state.LiveProjectileHandle(i);
            ref readonly var bubble = ref state.Projectiles[handle.Index];

            if (bubble.Kind != ProjectileKind.Bubble
                || !Gml.Less(bubble.Box.Distance(sourceBox), radius))
            {
                i++;
                continue;
            }

            state.FreeProjectile(handle);
        }
    }

    /// <inheritdoc cref="PopBubblesByBox"/>
    /// <summary>
    /// <c>with(Bubble)</c> for the one explosion that measures centre to
    /// centre: a mine's.
    /// </summary>
    public static void PopBubblesByPoint(ref WorldState state, double x, double y, double radius)
    {
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var handle = state.LiveProjectileHandle(i);
            ref readonly var bubble = ref state.Projectiles[handle.Index];

            if (bubble.Kind != ProjectileKind.Bubble
                || !Gml.Less(Gml.PointDistance(bubble.X, bubble.Y, x, y), radius))
            {
                i++;
                continue;
            }

            state.FreeProjectile(handle);
        }
    }

    /// <summary>
    /// <c>with (Sentry)</c> in the rocket's blast: damage with the same linear
    /// falloff as a character's, and nothing else — no knockback, no
    /// quarter-radius cutoff, no grounded exemption.
    /// </summary>
    private static void SplashSentries(ref WorldState state, in ProjectileState rocket)
    {
        var blastRadius = BlastRadiusFor(rocket.Kind);
        var rocketBox = rocket.Box;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasSentry[slot])
                continue;

            // distance_to_object from the sentry: its reported (facing-
            // inflated) box against the rocket's rotated one.
            var gap = Sentries.ReportedBox(state.Sentries[slot]).Distance(rocketBox);

            if (!Gml.Less(gap, blastRadius) || state.Sentries[slot].Team == rocket.Team)
                continue;

            Sentries.Damage(
                ref state,
                slot,
                ExplosionDamageFor(rocket.Kind) * (1 - (gap / blastRadius)),
                rocket.OwnerSlot,
                rocket.Source);
        }
    }

    /// <summary>
    /// <c>with (Generator)</c> in the rocket's blast: the same linear falloff a
    /// sentry takes, and an enemy-only test.
    /// </summary>
    /// <remarks>
    /// Unlike the mine's generator arm below it, this one has <b>no
    /// quarter-radius cutoff</b> — a rocket that lands at the very edge of its
    /// blast still scratches a generator, where a stuck mine's outer quarter
    /// does nothing at all.
    /// </remarks>
    private static void SplashGenerators(ref WorldState state, in ProjectileState rocket)
    {
        SplashGenerator(ref state, in rocket, Team.Red, quarterCutoff: false);
        SplashGenerator(ref state, in rocket, Team.Blue, quarterCutoff: false);
    }

    /// <summary>One team's generator inside a blast.</summary>
    /// <param name="quarterCutoff">
    /// Whether the mine's <c>1 - distance/radius &lt;= 0.25 and
    /// other.splashThreshhold</c> <c>continue</c> applies. A rocket's arm does
    /// not have it and a mine's does.
    /// </param>
    private static void SplashGenerator(
        ref WorldState state,
        in ProjectileState projectile,
        Team team,
        bool quarterCutoff)
    {
        ref var generator = ref Generators.Of(ref state, team);

        if (!generator.Exists || team == projectile.Team)
            return;

        var blastRadius = BlastRadiusFor(projectile.Kind);
        var gap = Generators.BodyBox(generator).Distance(projectile.Box);

        if (!Gml.Less(gap, blastRadius))
            return;

        if (quarterCutoff
            && Gml.LessOrEqual(1 - (gap / blastRadius), 0.25)
            && projectile.SplashThreshold)
        {
            return;
        }

        Generators.Damage(ref state, team, ExplosionDamageFor(projectile.Kind) * (1 - (gap / blastRadius)));
    }

    /// <summary>The <c>if (characterHit != -1)</c> block: the head-on hit.</summary>
    private static void DealDirectHit(
        ref WorldState state,
        in ProjectileState rocket,
        byte victimSlot,
        byte ownerCharacter,
        ref TickEvents events)
    {
        ref var victim = ref state.Characters[victimSlot];

        // (enemy or the owner themselves) and not ubered.
        //
        // The reference writes `A || B and C`, and **GML gives `&&`, `||` and
        // `^^` the same precedence**, left to right — so it means `(A || B) && C`
        // and not the C reading `A || (B && C)`. Under the C reading an ubered
        // enemy would take the full 25.
        var enemyOrSelf = victim.Team != rocket.Team || victimSlot == ownerCharacter;
        if (!enemyOrSelf || victim.Ubered)
            return;

        // damageAuto -> damageCharacter -> dealDamage.
        DamageApi.Deal(ref events, victimSlot, rocket.OwnerSlot, ref victim, rocket.HitDamage);
        victim.TimeUnscathed = 0;

        // The same assist bookkeeping a pellet does, and the demoted dealer
        // inherits what is left of the finish-off window rather than a fresh one.
        if (victim.LastDamageDealer != rocket.OwnerSlot && victim.LastDamageDealer != victimSlot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = rocket.OwnerSlot;
        victim.LastDamageSource = rocket.Source;
    }

    /// <summary>
    /// <c>with (Character) { ... }</c>: everybody inside the blast radius.
    /// </summary>
    /// <remarks>
    /// Walked in slot order, which is the reference's instance order. Nothing in
    /// the loop reads another character, so the order is not observable today —
    /// but it will be the moment a death inside a blast can change what a later
    /// character sees, and that is one respawn away.
    /// </remarks>
    private static void SplashCharacters(
        CollisionWorld world,
        ref WorldState state,
        in ProjectileState rocket,
        byte ownerCharacter,
        ref TickEvents events)
    {
        var blastRadius = BlastRadiusFor(rocket.Kind);
        var rocketBox = rocket.Box;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var victim = ref state.Characters[slot];

            // `distance_to_object` takes the **facing-inflated** box, unlike
            // `place_meeting` two lines away in Projectiles.CollideOne. A
            // left-facing character's box starts two pixels further left, so
            // splash damage in GG2 genuinely depends on which way the victim is
            // looking. Measured — see Aabb.Distance.
            var victimBox = Aabb.ForCharacter(victim.Box, victim.X, victim.Y, victim.Facing);
            var distance = victimBox.Distance(rocketBox);

            if (!Gml.Less(distance, blastRadius))
                continue;

            // ...and !(team == other.team and id != other.ownerPlayer.object
            //          and place_meeting(x, y+1, Obstacle))
            //
            // A team-mate who is not the shooter and who has ground under them
            // is exempt from the whole blast — no knockback and no boost. Only
            // `Obstacle` is asked, not the character's full solid list, so a
            // team-mate standing on a player wall is still thrown.
            var grounded = !world.PlaceFreeInMask(victim.Box, victim.X, victim.Y + 1);
            if (victim.Team == rocket.Team && slot != ownerCharacter && grounded)
                continue;

            SplashOne(ref state, in rocket, slot, ownerCharacter, blastRadius, distance, ref events);
        }
    }

    /// <summary>One character inside the radius.</summary>
    private static void SplashOne(
        ref WorldState state,
        in ProjectileState rocket,
        int slot,
        byte ownerCharacter,
        double blastRadius,
        double distance,
        ref TickEvents events)
    {
        ref var victim = ref state.Characters[slot];

        // point_direction from the rocket to the centre of the victim's box.
        // The offsets are the sprite's own, set once in Character's Create event
        // — *not* the facing-inflated pair the distance above used. Two
        // different boxes of the same character, four lines apart.
        var centreX = victim.X + ((victim.Box.Left + victim.Box.Right) / 2.0);
        var centreY = victim.Y + ((victim.Box.Top + victim.Box.Bottom) / 2.0);
        var pushDirection = Gml.PointDirection(rocket.X, rocket.Y, centreX, centreY);

        // vectorfactor = point_distance(0, 0, sin(rdir)^2, cos(rdir)^2), which
        // is sqrt(sin^4 + cos^4): exactly 1 along an axis and 0.707 at 45
        // degrees. So a blast directly below you throws you further than one
        // below and to the side, by up to thirty per cent. Transcribed rather
        // than simplified — the identity that would tidy it away is the one for
        // sin^2 + cos^2, and this is not that.
        var radians = Gml.DegToRad(pushDirection);
        var vectorFactor = Gml.PointDistance(
            0,
            0,
            Math.Pow(Math.Sin(radians), 2),
            Math.Pow(Math.Cos(radians), 2));

        // if(fake_distance < 1) fake_distance = 0;
        //
        // The reference's own comment: "fix distance of rockets to players that
        // they collide with, now that it's handled properly". A direct hit is
        // pulled to exactly zero so it takes the full falloff-free damage,
        // rather than the pixel or so the contact reposition leaves behind.
        var falloffDistance = Gml.Less(distance, 1) ? 0 : distance;
        var falloff = falloffDistance / blastRadius;

        // Everything past a quarter of the way in is skipped entirely — no
        // knockback either, because this is a `continue` inside the `with`.
        if (Gml.LessOrEqual(1 - falloff, 0.25))
            return;

        var push = Math.Min(15, rocket.Knockback - (rocket.Knockback * falloff)) * vectorFactor;
        victim.Motion.MotionAdd(pushDirection, push);

        if (victim.Ubered || !Gml.Greater(victim.Hp, 0))
        {
            // The reference's own "//just in case": an ubered shooter still gets
            // the rocket jump, at a slightly smaller boost to compensate for
            // being invulnerable while doing it.
            if (Gml.Greater(victim.Hp, 0) && slot == ownerCharacter && rocket.Team == victim.Team && victim.Ubered)
            {
                victim.MoveStatus = 1;
                victim.Motion.Speed *= 1.055;
            }

            return;
        }

        if (rocket.Team != victim.Team || slot == ownerCharacter)
            DealSplashDamage(ref state, in rocket, slot, ownerCharacter, falloff, ref events);

        ApplyMoveStatus(ref state, in rocket, slot, ownerCharacter, falloff);
    }

    /// <summary>The damage half of one splashed character.</summary>
    private static void DealSplashDamage(
        ref WorldState state,
        in ProjectileState rocket,
        int slot,
        byte ownerCharacter,
        double falloff,
        ref TickEvents events)
    {
        ref var victim = ref state.Characters[slot];

        DamageApi.Deal(
            ref events, slot, rocket.OwnerSlot, ref victim,
            ExplosionDamageFor(rocket.Kind) * (1 - falloff));
        victim.TimeUnscathed = 0;

        if (slot == ownerCharacter
            && victim.LastDamageDealer != CharacterState.Nobody
            && victim.LastDamageDealer != rocket.OwnerSlot)
        {
            // Killed by your own rocket while somebody else had already hurt
            // you: the kill log calls that being finished off, and the credit
            // stays with them — LastDamageDealer is deliberately not touched.
            victim.LastDamageSource = DamageSource.FinishedOffGib;
        }
        else
        {
            if (victim.LastDamageDealer != rocket.OwnerSlot && victim.LastDamageDealer != slot)
            {
                victim.SecondToLastDamageDealer = victim.LastDamageDealer;
                victim.AssistTimer = victim.FinishOffTimer;
            }

            // The reference guards this with `if (other.ownerPlayer != id)`,
            // which compares the shooter's **player** instance with the victim's
            // **character** instance. Those are instances of different objects,
            // so their ids can never be equal and the guard is always true. A
            // port that reads it as "unless the victim is the shooter" would
            // skip the window on every self-damaging rocket jump, which is the
            // one case the line looks like it is about.
            victim.FinishOffTimer = GameConstants.AssistTime;

            victim.LastDamageDealer = rocket.OwnerSlot;
            victim.LastDamageSource = rocket.Source;
        }

        // dealFlicker: a hit spy shows through their cloak.
        victim.DealFlicker();
    }

    /// <summary>
    /// The movement half: which of the three knockback flavours this is, and the
    /// upward boost that rides two of them.
    /// </summary>
    /// <remarks>
    /// <c>moveStatus</c> 1, 2 and 4 all mean "airborne under rocket-jump
    /// gravity" to <see cref="CharacterPhysics"/>, and all three reach the wire
    /// (<see cref="CharacterWire"/>), so the client draws the right one. They
    /// differ in friction — <c>Character.events/Begin Step.xml:159</c> switches
    /// on the value.
    /// </remarks>
    private static void ApplyMoveStatus(
        ref WorldState state,
        in ProjectileState rocket,
        int slot,
        byte ownerCharacter,
        double falloff)
    {
        ref var victim = ref state.Characters[slot];
        var ownRocket = slot == ownerCharacter;
        var friendly = rocket.Team == victim.Team;

        if (ownRocket && friendly)
        {
            // A rocket jump. No upward boost: the shooter is expected to have
            // aimed downwards and the blast itself is doing the work.
            victim.MoveStatus = 1;
            victim.Motion.Speed *= 1.06;
            return;
        }

        victim.MoveStatus = friendly ? 4 : 2;

        // Two point_directions with different offsets, and both have to agree:
        // the first asks whether the blast is below-ish from five pixels down,
        // the second whether it is below at all. Together they mean "the
        // explosion is under you", which is what earns the extra lift.
        if (!Gml.Greater(Gml.PointDirection(victim.X, victim.Y + 5, rocket.X, rocket.Y - 5), 210))
            return;

        if (!Gml.Less(Gml.PointDirection(victim.X, victim.Y, rocket.X, rocket.Y), 330))
            return;

        // A component write then a polar one, in that order — each rewrites the
        // other representation and snaps it, so the pair cannot be collapsed.
        victim.Motion.VSpeed -= 4 * (1 - falloff);
        victim.Motion.Speed *= 1.3;
    }

    // ======================================================================
    // The mine's blast. Objects/Projectiles/Mine.events/User Event 2.xml.
    // ======================================================================
    //
    // It is NOT the rocket's with different numbers, and five things differ
    // in ways that change who dies:
    //
    //   1. vectorfactor is point_distance(0, 0, sin(rdir), cos(rdir)*0.8) —
    //      the components are *not* squared and the vertical one is scaled. The
    //      rocket's is sqrt(sin^4 + cos^4), which never exceeds 1; this one
    //      reaches 1.0 sideways and 0.8 vertically and is a different curve
    //      everywhere between.
    //   2. There is no `if(fake_distance < 1) fake_distance = 0`, so a mine
    //      touching somebody still applies its falloff.
    //   3. The quarter-radius cutoff is conditional on splashThreshhold, so a
    //      *stuck* mine has no cutoff at all.
    //   4. Self-damage is multiplied by 5/9.
    //   5. moveStatus is 1 for your own mine and 4 for everybody else's —
    //      never 2 — and there is no speed boost and no upward kick.
    //
    // dealFlicker is also outside the damage branch here, so a team-mate the
    // blast merely shoves is still flickered.

    /// <summary>How much of a mine's own blast its owner takes.</summary>
    /// <remarks>
    /// The reference's comment is "self damage resistance". Written as the
    /// fraction it is rather than 0.5555…, because <c>5/9</c> in GML is a
    /// division of two integers evaluated at full double precision and the
    /// decimal is not the same number.
    /// </remarks>
    private const double MineSelfDamage = 5.0 / 9.0;

    /// <summary>
    /// Detonates one mine and everything its blast reaches.
    /// </summary>
    /// <remarks>
    /// <b>Depth-first, because the reference is.</b> A mine's <c>with(Mine)</c>
    /// block calls <c>event_user(2)</c> on each qualifying neighbour, and that
    /// call runs the neighbour's whole event — its character damage and its own
    /// chain — before the first mine looks at its next neighbour. Since the
    /// knockback is a sequence of <c>motion_add</c>s on the same characters, the
    /// order is observable in the last bits, so this walks the same order with
    /// an explicit stack rather than recursing.
    /// <para>
    /// An exploding mine stays in the pool until its own event ends, exactly as
    /// the reference's <c>instance_destroy()</c> is its last line — which is why
    /// <see cref="ProjectileState.Exploded"/> has to exist. Freeing it up front
    /// instead would be simpler and would change which mines a chain reaches.
    /// </para>
    /// </remarks>
    public static void DetonateMine(
        CollisionWorld world, ref WorldState state, ProjectileHandle handle, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        Span<ProjectileHandle> pending = stackalloc ProjectileHandle[WorldState.MaxProjectiles];
        var depth = 0;

        pending[depth++] = handle;

        while (depth > 0)
        {
            var current = pending[--depth];

            if (!state.TryGetProjectile(current, out var mine) || mine.Exploded)
                continue;

            state.Projectiles[current.Index].Exploded = true;
            events.Exploded = true;

            SplashCharactersFromMine(world, ref state, in mine, ref events);
            SplashSentriesFromMine(ref state, in mine);

            // The mine's generator arm keeps the quarter-radius `continue`
            // that its character arm has and the rocket's generator arm does
            // not — Mine.events/User Event 2.xml:88.
            SplashGenerator(ref state, in mine, Team.Red, quarterCutoff: true);
            SplashGenerator(ref state, in mine, Team.Blue, quarterCutoff: true);

            // The mine's bubble sweep is the odd one of the three: a plain
            // `point_distance(x, y, other.x, other.y)` centre-to-centre rather
            // than `distance_to_object`, and the radius is *widened* by the
            // bubble's own selfPopRadius instead of being scaled down.
            PopBubblesByPoint(
                ref state, mine.X, mine.Y,
                BlastRadiusFor(mine.Kind) + Projectiles.BubbleSelfPopRadius);

            // The chain, pushed in reverse so it pops in creation order.
            var radius = BlastRadiusFor(mine.Kind);
            var first = depth;

            for (var i = 0; i < state.LiveProjectileCount; i++)
            {
                var other = state.LiveProjectileHandle(i);
                ref var candidate = ref state.Projectiles[other.Index];

                if (candidate.Kind != ProjectileKind.Mine || other == current || candidate.Exploded)
                    continue;

                // (other.team != team or other.ownerPlayer == ownerPlayer):
                // an enemy's mine, or one of your own. A team-mate's mine is
                // left alone.
                if (candidate.Team == mine.Team && candidate.OwnerSlot != mine.OwnerSlot)
                    continue;

                var gap = candidate.Box.Distance(mine.Box);
                if (!Gml.Less(gap, radius))
                    continue;

                if (Gml.LessOrEqual(1 - (gap / radius), 0.25) && mine.SplashThreshold)
                    continue;

                // A mine an earlier blast already queued is queued again here,
                // and this copy pops first: the mine goes off inside this
                // subtree, as the reference's nested event_user(2) does. The
                // older copy lower down would only pop after that and be
                // skipped, so it is dropped now — the order is unchanged and
                // the stack never holds a mine twice, which is what keeps it
                // within MaxProjectiles for a dense cluster of both teams'.
                for (var j = 0; j < first; j++)
                {
                    if (pending[j] != other)
                        continue;

                    pending[(j + 1)..depth].CopyTo(pending[j..]);
                    depth--;
                    first--;
                    break;
                }

                pending[depth++] = other;
            }

            pending[first..depth].Reverse();

            state.FreeProjectile(current);
        }
    }

    /// <summary><c>with (Character)</c> for a mine's blast.</summary>
    private static void SplashCharactersFromMine(
        CollisionWorld world, ref WorldState state, in ProjectileState mine, ref TickEvents events)
    {
        var radius = BlastRadiusFor(mine.Kind);
        var damage = ExplosionDamageFor(mine.Kind);
        var mineBox = mine.Box;

        var ownerCharacter = mine.OwnerSlot != ProjectileState.NoOwner && state.Alive[mine.OwnerSlot]
            ? mine.OwnerSlot
            : CharacterState.Nobody;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var victim = ref state.Characters[slot];

            // The facing-inflated box, as distance_to_object takes it.
            var victimBox = Aabb.ForCharacter(victim.Box, victim.X, victim.Y, victim.Facing);
            var gap = victimBox.Distance(mineBox);

            if (!Gml.Less(gap, radius))
                continue;

            var grounded = !world.PlaceFreeInMask(victim.Box, victim.X, victim.Y + 1);
            if (victim.Team == mine.Team && slot != ownerCharacter && grounded)
                continue;

            var falloff = gap / radius;

            // `and other.splashThreshhold`: a stuck mine has none of this.
            if (Gml.LessOrEqual(1 - falloff, 0.25) && mine.SplashThreshold)
                continue;

            // vectorfactor = point_distance(0, 0, sin(rdir), cos(rdir)*0.8).
            // Not the rocket's, and not squared — see the note above this
            // section.
            var centreX = victim.X + ((victim.Box.Left + victim.Box.Right) / 2.0);
            var centreY = victim.Y + ((victim.Box.Top + victim.Box.Bottom) / 2.0);
            var pushDirection = Gml.PointDirection(mine.X, mine.Y, centreX, centreY);
            var radians = Gml.DegToRad(pushDirection);

            var vectorFactor = Gml.PointDistance(
                0,
                0,
                Math.Sin(radians),
                Math.Cos(radians) * 0.8);

            victim.Motion.MotionAdd(
                pushDirection,
                Math.Min(15, mine.Knockback - (mine.Knockback * falloff)) * vectorFactor);

            var hurtable = (mine.Team != victim.Team || slot == ownerCharacter)
                           && !victim.Ubered
                           && Gml.Greater(victim.Hp, 0);

            if (hurtable)
            {
                var dealt = damage * (1 - falloff);

                if (slot == ownerCharacter)
                    dealt *= MineSelfDamage;

                DamageApi.Deal(ref events, slot, mine.OwnerSlot, ref victim, dealt);
                victim.TimeUnscathed = 0;

                Attribute(ref victim, in mine, slot, ownerCharacter);

                // 1 for your own mine, 4 for anyone else's. Never 2 — a mine
                // has no separate "thrown by an enemy" flavour, so an enemy's
                // mine gives the same friction as a team-mate's.
                victim.MoveStatus = slot == ownerCharacter ? 1 : 4;
            }
            else if (Gml.Greater(victim.Hp, 0) && slot == ownerCharacter && victim.Ubered)
            {
                // The reference's own comment: "Special case allowing you to
                // minejump even when ubered".
                victim.MoveStatus = 1;
            }

            // Outside the damage branch, unlike the rocket's — so a
            // team-mate who is only shoved is still flickered.
            victim.DealFlicker();
        }
    }

    /// <summary>
    /// <c>with (Sentry)</c> in the mine's blast: half again the damage a
    /// character takes, and the flying mine's quarter-radius cutoff applies.
    /// </summary>
    /// <remarks>
    /// The reference's own comment explains the 3/2: "we want sentries to take
    /// ~11% extra damage so that two stickies will do 100 instead of 90 dmg. we
    /// don't change the base damage so scouts can't be 2-hitted." The
    /// expression is transcribed in its own order —
    /// <c>explosionDamage*3/2*(1-falloff)</c>, left to right — because
    /// re-association changes the last bits.
    /// </remarks>
    private static void SplashSentriesFromMine(ref WorldState state, in ProjectileState mine)
    {
        var radius = BlastRadiusFor(mine.Kind);
        var mineBox = mine.Box;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasSentry[slot])
                continue;

            var gap = Sentries.ReportedBox(state.Sentries[slot]).Distance(mineBox);

            if (!Gml.Less(gap, radius) || state.Sentries[slot].Team == mine.Team)
                continue;

            var falloff = gap / radius;

            // `and other.splashThreshhold`: a stuck mine has no cutoff, same as
            // the character loop.
            if (Gml.LessOrEqual(1 - falloff, 0.25) && mine.SplashThreshold)
                continue;

            Sentries.Damage(
                ref state,
                slot,
                ExplosionDamageFor(mine.Kind) * 3 / 2 * (1 - falloff),
                mine.OwnerSlot,
                DamageSource.Minegun);
        }
    }

    /// <summary>Who the kill log will credit for this mine's damage.</summary>
    /// <remarks>
    /// <c>Mine.events/User Event 2.xml</c>'s attribution block, including the
    /// <c>reflector</c> arms the airblast made reachable:
    /// <list type="bullet">
    /// <item>a reflected mine that reaches its own owner is never the
    /// finish-off-gib case, and the blame lands on the <em>pyro</em> as
    /// <see cref="DamageSource.ReflectedSticky"/>;</item>
    /// <item>the demotion clause carries <c>other.reflector !=
    /// lastDamageDealer</c> — the pyro cannot be demoted to an assist by their
    /// own reflected mine. With no reflector both sides are <c>noone</c> when
    /// nobody has dealt damage, so the clause also skips the demotion then;
    /// the write it skips was <see cref="CharacterState.Nobody"/> over
    /// <see cref="CharacterState.Nobody"/>, which is why this was invisible
    /// before the airblast.</item>
    /// </list>
    /// </remarks>
    private static void Attribute(ref CharacterState victim, in ProjectileState mine, int slot, byte ownerCharacter)
    {
        if (slot == ownerCharacter
            && victim.LastDamageDealer != CharacterState.Nobody
            && victim.LastDamageDealer != mine.OwnerSlot
            && mine.Reflector == ProjectileState.NoOwner)
        {
            victim.LastDamageSource = DamageSource.FinishedOffGib;
            return;
        }

        if (victim.LastDamageDealer != mine.OwnerSlot
            && victim.LastDamageDealer != slot
            && victim.LastDamageDealer != mine.Reflector)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        // `if (other.ownerPlayer != id ...)` again compares the owner's
        // *player* with the victim's *character*, so it is always true — the
        // same reference quirk the rocket's blast has. See DealSplashDamage.
        victim.FinishOffTimer = GameConstants.AssistTime;

        victim.LastDamageDealer = mine.OwnerSlot;
        victim.LastDamageSource = mine.Source;

        // if (id == other.ownerPlayer.object and instance_exists(other.reflector))
        if (slot == ownerCharacter && mine.Reflector != ProjectileState.NoOwner)
        {
            victim.LastDamageDealer = mine.Reflector;
            victim.LastDamageSource = DamageSource.ReflectedSticky;
        }
    }
}
