using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The engineer's sentry: building it, its fall to the floor, the turret's
/// targeting and fire, and its destruction.
/// </summary>
/// <remarks>
/// Transcribed from <c>Objects/InGameElements/Sentry.events/{Create, Begin
/// Step, End Step, User Event 2, Destroy}.xml</c>,
/// <c>Objects/Weapons/SentryTurret.events/{Create, User Event 0, End Step,
/// Alarm 0}.xml</c>, <c>Scripts/GameServer/serverBuildSentry.gml</c>,
/// <c>Scripts/Object Creation/buildSentry.gml</c> and
/// <c>Scripts/Damage/damageSentry.gml</c>.
/// <para>
/// <b>What is deliberately not here.</b> Sounds, the build animation
/// (<c>image_index</c> — the hitbox does not animate, see docs/SIMULATION.md),
/// the humiliation round's <c>event_user(1)</c>, the <c>NoticeO</c> HUD
/// notice, the <c>SentryGibs</c> debris and the 50 nuts a team-mate can scrape
/// off them, and the kill-log bookkeeping of <c>doEventDestruction.gml</c> —
/// all presentation or scoring no client re-simulates. The spy branches of the
/// turret's End Step (cloak exclusion, the cloaked-spy-in-the-way redirect)
/// collapse because nothing can cloak yet; they arrive with the spy.
/// </para>
/// <para>
/// <b>All of <c>serverBuildSentry</c>'s preconditions are modelled</b>,
/// including the two that arrived with the map objects they need — the spawn-room
/// test and <c>onCabinet</c>. See <see cref="CanBuild"/>, which checks both.
/// </para>
/// </remarks>
public static class Sentries
{
    /// <summary>
    /// The sentry's collision box about its origin, constant at every frame.
    /// </summary>
    /// <remarks>
    /// <c>Sentry.xml</c> declares <c>&lt;mask&gt;SentryMaskS&lt;/mask&gt;</c>,
    /// a one-frame mask that overrides the twelve-frame build animation for all
    /// collision. Measured live at every <c>image_index</c> —
    /// docs/SIMULATION.md, "the sentry's collision box does not animate".
    /// </remarks>
    public static readonly CharacterBox Box = new(-8, 7, -7, 12);

    /// <summary>
    /// The turret sprite's own bbox in sprite pixels — <c>SentryTurretS</c>,
    /// bbox 3,4,33,15 about origin (15, 12), giving -12,-8,18,3. It is drawn
    /// and collides <em>rotated</em>, so it goes through
    /// <see cref="Aabb.ForRotatedSprite"/>.
    /// </summary>
    private static readonly Aabb TurretSpriteBbox = new(3, 4, 33, 15);

    private const double TurretOriginX = 15;

    private const double TurretOriginY = 12;

    /// <summary>The build precondition's radius: no sentry within 50 of the builder.</summary>
    private const int BuildClearance = 50;

    /// <summary>The self-destruct guard's radius about <c>(x, y+10)</c>.</summary>
    private const int OverlapClearance = 25;

    /// <summary>What building costs, and the exact stock it demands.</summary>
    private const double BuildCost = 100;

    /// <summary>
    /// What a sentry loses on touching a team gate or an intel gate —
    /// <c>hp -= 999</c>, the same raw subtraction the kill boxes use.
    /// </summary>
    private const double GateDamage = 999;

    /// <summary>
    /// The sentry's box as <c>place_meeting</c> and <c>collision_line</c> see
    /// it: the mask, with no facing inflation.
    /// </summary>
    public static Aabb BodyBox(in SentryState sentry) =>
        Aabb.ForCharacter(Box, sentry.X, sentry.Y);

    /// <summary>
    /// The sentry's box as GM8 <em>reports</em> it, which is what
    /// <c>distance_to_object</c> and <c>collision_circle</c> consume: two
    /// pixels wider on the left when it faces left. Measured —
    /// docs/SIMULATION.md.
    /// </summary>
    public static Aabb ReportedBox(in SentryState sentry) =>
        Aabb.ForCharacter(Box, sentry.X, sentry.Y, sentry.StartDirection);

    /// <summary>
    /// The turret's box for overlap tests: the rotated sprite bound, mirrored
    /// by its <c>image_xscale</c>.
    /// </summary>
    public static Aabb TurretBodyBox(in SentryState sentry) =>
        Aabb.ForRotatedSprite(
            TurretSpriteBbox,
            TurretOriginX,
            TurretOriginY,
            sentry.X,
            sentry.Y,
            sentry.TurretImageAngle,
            sentry.TurretXScale);

    /// <summary>
    /// The turret's box as reported, for <c>distance_to_object</c> and
    /// <c>collision_circle</c>: the rotated bound with the flipped-sprite
    /// inflation applied <em>before</em> the rotation.
    /// </summary>
    /// <remarks>
    /// Measured (<c>gg2-samplers/probes/probe-airblast.js</c>): three flipped
    /// rotations of <c>SentryTurretS</c> all match the pre-rotation model.
    /// See <see cref="Aabb.ForRotatedSpriteReported"/>.
    /// </remarks>
    public static Aabb TurretReportedBox(in SentryState sentry) =>
        Aabb.ForRotatedSpriteReported(
            TurretSpriteBbox,
            TurretOriginX,
            TurretOriginY,
            sentry.X,
            sentry.Y,
            sentry.TurretImageAngle,
            sentry.TurretXScale);

    /// <summary>
    /// GM8 <c>collision_circle</c> against one box —
    /// <see cref="Aabb.CircleHits"/>, whose floored centre is now measured
    /// (a sentry's centre is a real position).
    /// </summary>
    private static bool CircleHitsBox(double cx, double cy, double r, in Aabb box) =>
        Aabb.CircleHits(cx, cy, r, in box);

    /// <summary>
    /// Whether any sentry's reported box is within <paramref name="r"/> of the
    /// centre. <paramref name="excludeSlot"/> is the caller's own, for the
    /// guard's <c>notme</c>.
    /// </summary>
    private static bool AnySentryInCircle(in WorldState state, double cx, double cy, double r, int excludeSlot)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasSentry[slot] || slot == excludeSlot)
                continue;

            if (CircleHitsBox(cx, cy, r, ReportedBox(state.Sentries[slot])))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>serverBuildSentry.gml</c>'s preconditions, minus the two the port
    /// cannot ask (spawn room, cabinet — see the class remarks).
    /// </summary>
    /// <remarks>
    /// <b>The stock check is equality, not sufficiency</b>:
    /// <c>player.object.nutsNBolts == 100</c>, through GML's fuzzy compare. The
    /// regeneration adds 0.1 per tick and clamps at 100, so the value really
    /// does sit at exactly 100 — but an engineer one tick short of full is
    /// refused, not rounded up.
    /// </remarks>
    /// <param name="world">
    /// The map, for the spawn-room precondition. <b>Null means "no spawn
    /// rooms"</b>, the same convention <see cref="Deaths.Kill"/> uses.
    /// </param>
    public static bool CanBuild(in WorldState state, int slot, CollisionWorld? world = null)
    {
        if (!state.IsAlive(slot))
            return false;

        ref readonly var character = ref state.Characters[slot];

        // `collision_point(x, y, SpawnRoom, 0, 0) < 0` — the origin again,
        // matching the class-change swap and not the fast respawn. And
        // `!player.object.onCabinet`, which is `place_meeting` and was written
        // by the *previous* tick's End Step, because a build is a client command
        // processed outside the tick. See CharacterState.OnCabinet.
        if (character.OnCabinet)
            return false;

        if (world is not null && world.PointInSpawnRoom(character.X, character.Y))
            return false;

        return character.Class == PlayerClass.Engineer
            && !state.HasSentry[slot]
            && Gml.Equal(character.NutsNBolts, BuildCost)
            && !AnySentryInCircle(state, character.X, character.Y, BuildClearance, excludeSlot: -1);
    }

    /// <summary>
    /// <c>Scripts/Object Creation/buildSentry.gml</c>: place the sentry and
    /// take the nuts and bolts.
    /// </summary>
    /// <remarks>
    /// Unconditional, like the script — the preconditions are the caller's
    /// (<see cref="CanBuild"/> on the live server; a replayed
    /// <c>BUILD_SENTRY</c> is the reference's own acceptance and is injected
    /// as-is). The script's <c>else</c> arm repositions an existing sentry;
    /// unreachable through <c>serverBuildSentry</c>'s <c>!player.sentry</c>
    /// guard, but transcribed because this method is also the client-side
    /// <c>doEventBuildSentry</c> path a replay drives.
    /// </remarks>
    public static void Build(ref WorldState state, int slot, double x, double y, int startDirection)
    {
        // `team = ownerPlayer.team` in buildSentry.gml — the player's team at
        // the moment of building, frozen into the sentry. Reading it here
        // rather than taking a parameter was one of the two seams phase 6's
        // player state absorbed.
        var team = state.Players[slot].Team;

        if (!state.HasSentry[slot])
        {
            state.Sentries[slot] = SentryState.Build(team, x, y, startDirection);

            // GM8 issues an instance id here, and the order it induces decides
            // which of two overlapping sentries a projectile explodes on. See
            // SentryState.CreationOrder.
            state.Sentries[slot].CreationOrder = ++state.NextInstanceOrder;
            state.HasSentry[slot] = true;
        }
        else
        {
            state.Sentries[slot].X = x;
            state.Sentries[slot].Y = y;
        }

        state.Sentries[slot].StartDirection = startDirection;

        // player.object.nutsNBolts -= 100 — unguarded in the script, but its
        // callers all checked player.object first, and this port's callers all
        // hold a live character too.
        if (state.IsAlive(slot))
            state.Characters[slot].NutsNBolts -= BuildCost;
    }

    /// <summary>
    /// <c>Scripts/Damage/damageSentry.gml</c> plus the attribution its callers
    /// all perform: hp off, the build slowed for good, and the dealer recorded.
    /// </summary>
    /// <remarks>
    /// The <c>deathmatch_invulnerable</c> guard in <c>dealDamage</c> is inert —
    /// a sentry never has the variable — and <c>global.dealDamageFunction</c>
    /// is the empty string outside mods. Note there is no
    /// <c>timeUnscathed</c> here: that is a character field, and sentry damage
    /// does not touch the <em>owner's</em>.
    /// </remarks>
    public static void Damage(ref WorldState state, int slot, double amount, byte dealer, DamageSource source)
    {
        ref var sentry = ref state.Sentries[slot];

        sentry.Hp -= amount;
        sentry.BuildRate = SentryState.DamagedBuildRate;
        sentry.LastDamageDealer = dealer;
        sentry.LastDamageSource = source;
    }

    /// <summary>
    /// <c>Sentry.events/Begin Step.xml</c>: the overlap guard, the build, the
    /// turret's birth, and the fall.
    /// </summary>
    /// <remarks>
    /// The opening guard is the reference's whole three-clause one:
    /// <c>ownerPlayer.class != CLASS_ENGINEER or team != ownerPlayer.team or
    /// collision_circle(...)</c>. All three fire here, on the tick <em>after</em>
    /// the change, which is the reference's own timing.
    /// </remarks>
    public static void BeginStep(CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref var sentry = ref state.Sentries[slot];

        // ownerPlayer.class != CLASS_ENGINEER or team != ownerPlayer.team or
        // collision_circle(x, y+10, 25, Sentry, false, true) >= 0: the owner
        // stopped being an engineer of this team, or another sentry is too
        // close (one fell onto the other). Destroyed with no killer and damage
        // source 0 — sendEventDestruction(ownerPlayer, noone, noone, 0).
        if (state.Players[slot].Class != PlayerClass.Engineer
            || sentry.Team != state.Players[slot].Team
            || AnySentryInCircle(state, sentry.X, sentry.Y + 10, OverlapClearance, slot))
        {
            // inEndStep: FALSE. This is the Begin Step destruction
            // (Sentry.events/Begin Step.xml:1-3), which makes its own tick's
            // flush — unlike the hp<=0 one below.
            ReportDestruction(ref state, slot, ref events,
                CharacterState.Nobody, CharacterState.Nobody, DamageSource.None,
                inEndStep: false);
            Destroy(world, ref state, slot);
            return;
        }

        if (!sentry.Built && Gml.Equal(sentry.Motion.VSpeed, 0))
        {
            if (Gml.Less(sentry.Hp, SentryState.MaxHp))
                sentry.Hp += sentry.BuildRate;
            else
                sentry.Built = true;

            // buildrate = min(buildrate, buildrate + 0.6/45) is a no-op — see
            // SentryState.InitialBuildRate — and image_index/playsound are
            // drawing and sound.
        }

        // The turret is created on the same tick `built` flips, and its Create
        // and User Event 0 run inside the instance_create.
        if (sentry.Built && !sentry.HasTurret)
        {
            sentry.HasTurret = true;
            sentry.Hp = SentryState.MaxHp;

            // SentryTurret.events/Create.xml + User Event 0.xml.
            sentry.ReadyToShoot = false;
            sentry.NoticedTarget = true;
            sentry.RefireTimer = SentryState.RefireTime;
            sentry.TurretDirection = sentry.StartDirection > 0 ? 0 : 180;
            sentry.RotateIndex = sentry.RotateStart;
            sentry.Rotating = false;

            // image_xscale is derived at the top of the first End Step; +1 is
            // GM8's instance default until then.
            sentry.TurretXScale = 1;
            sentry.TurretImageAngle = 0;
        }

        // Begin Step.xml:51, immediately below the build branch:
        // `if (humiliated == 1 and built == 1 and currentWeapon != -1)
        // event_user(1)`, and the user event destroys currentWeapon.
        //
        // So a humiliated sentry re-creates its turret above and scraps it
        // here, every tick, for the whole of the scoreboard — which also means
        // the branch above heals it to full every tick. Both are the
        // reference's; neither is a shortcut.
        if (sentry.Humiliated && sentry.Built && sentry.HasTurret)
            sentry.HasTurret = false;

        // wallSetSolid() ... wallUnsetSolid(): only Obstacle is solid to a
        // sentry — not player walls, not bullet walls, not any gate.
        var filter = CollisionFilter.ForSentry();
        var mover = new MoverState { X = sentry.X, Y = sentry.Y };

        // Try to get outside of the wallmask if we're in it.
        if (!world.PlaceFree(Box, mover.X, mover.Y, filter))
            Movement.MoveOutsideSolid(world, Box, filter, ref mover, 0, 6);
        if (!world.PlaceFree(Box, mover.X, mover.Y, filter))
            Movement.MoveOutsideSolid(world, Box, filter, ref mover, 180, 12);
        if (!world.PlaceFree(Box, mover.X, mover.Y, filter))
        {
            Movement.MoveOutsideSolid(world, Box, filter, ref mover, 0, 6);
            Movement.MoveOutsideSolid(world, Box, filter, ref mover, 90, 100);
        }

        sentry.X = mover.X;
        sentry.Y = mover.Y;

        // Gravity. 0.6 is one of the literals GM8 parses a ULP high — the same
        // constant as character gravity.
        var hspeed = sentry.Motion.HSpeed;
        var vspeed = sentry.Motion.VSpeed;

        if (world.PlaceFree(Box, sentry.X + hspeed, sentry.Y + vspeed + Gml.PointSix, filter))
            sentry.Motion.VSpeed += Gml.PointSix;

        if (!world.PlaceFree(Box, sentry.X + sentry.Motion.HSpeed, sentry.Y + sentry.Motion.VSpeed, filter)
            && world.PlaceFree(Box, sentry.X, sentry.Y, filter))
        {
            Land(world, ref sentry, filter);
        }

        if (Gml.Greater(sentry.Motion.VSpeed, 10))
            sentry.Motion.VSpeed = 10;

        // The landing broadcast: vspeed just became zero.
        if (Gml.NotEqual(sentry.OldVSpeed, 0) && Gml.Equal(sentry.Motion.VSpeed, 0))
            events.SentryLanded[slot] = true;

        sentry.OldVSpeed = sentry.Motion.VSpeed;

        // x += hspeed; y += vspeed; x -= hspeed; y -= vspeed — the reference's
        // delta-factor dance. Adding and subtracting the same double is not a
        // no-op in the last bit, and the automatic movement pass carries on
        // from the result, so it is transcribed (CLAUDE.md, "the x -= hspeed
        // cancellation does not belong on the incidental list").
        sentry.X = sentry.X + sentry.Motion.HSpeed - sentry.Motion.HSpeed;
        sentry.Y = sentry.Y + sentry.Motion.VSpeed - sentry.Motion.VSpeed;
    }

    /// <summary>
    /// <c>Sentry.events/User Event 2.xml</c>: contact the floor and stop.
    /// </summary>
    /// <remarks>
    /// <c>really_move_contact_solid(direction, speed)</c> — the derived motion
    /// pair, which for a falling sentry is (270, vspeed) — then
    /// <c>if (!place_free(x, y+1)) vspeed = 0</c>. Unlike the rocket's use of
    /// the same script, the sentry is <em>not</em> already in collision here
    /// (the caller checked <c>place_free(x, y)</c>), so the
    /// <c>move_contact_solid</c> half really moves and both halves are needed.
    /// </remarks>
    private static void Land(CollisionWorld world, ref SentryState sentry, in CollisionFilter filter)
    {
        var mover = new MoverState
        {
            X = sentry.X,
            Y = sentry.Y,
            HSpeed = sentry.Motion.HSpeed,
            VSpeed = sentry.Motion.VSpeed,
        };

        Movement.GoodMoveContactSolid(
            world, Box, filter, ref mover, sentry.Motion.Direction, sentry.Motion.Speed, out _);
        Movement.MoveOutsideSolid(
            world, Box, filter, ref mover, sentry.Motion.Direction + 180, sentry.Motion.Speed);

        sentry.X = mover.X;
        sentry.Y = mover.Y;

        if (!world.PlaceFree(Box, sentry.X, sentry.Y + 1, filter))
            sentry.Motion.VSpeed = 0;
    }

    /// <summary>
    /// The automatic movement pass: GM8 adds the speeds to the position after
    /// every Step event, and a sentry has no Step event to interfere.
    /// </summary>
    public static void Move(ref WorldState state, int slot)
    {
        ref var sentry = ref state.Sentries[slot];
        sentry.X += sentry.Motion.HSpeed;
        sentry.Y += sentry.Motion.VSpeed;
    }

    /// <summary>
    /// <c>Sentry.events/Collision with {TeamGate, IntelGate}.xml</c>: a sentry
    /// standing in a gate loses 999 hp and dies in its own End Step.
    /// </summary>
    /// <remarks>
    /// <b>This is what an engineer who builds in a doorway gets, and it is
    /// the whole of the "built at T, self-destructed at T+1" finding</b> —
    /// five of them in <c>reference-ctf_truefort-closedsentryteam.trace</c>.
    /// The build preconditions in <see cref="CanBuild"/> say nothing about a gate, so the
    /// reference accepts the build, broadcasts it, and then kills the gun on
    /// the next tick. See docs/TRAPS-SIMULATION.md, "the sentry's gate
    /// collision".
    /// <para>
    /// <b>Both events are two lines and identical</b>: <c>hp -= 999;
    /// lastDamageDealer = noone;</c> — the same shape as
    /// <see cref="KillBoxes"/>' pair, and like those they leave
    /// <c>lastDamageSource</c> alone. It is <c>-1</c> from
    /// <c>Sentry.events/Create.xml</c> and reaches the wire as <b>0</b>
    /// (<see cref="DamageSource.None"/>), which is why one of these looks
    /// exactly like the Begin Step guard's <c>sendEventDestruction(...,
    /// noone, noone, 0)</c> in a recording.
    /// </para>
    /// <para>
    /// <b>Any gate, not just the enemy's.</b> Neither event looks at
    /// <c>team</c>, so a red engineer's sentry dies in a red team gate — three
    /// of the five in that recording are a sentry in its own team's doorway.
    /// And <b>a setup gate is not a gate</b>: <c>ControlPointSetupGate</c>
    /// (object 536) has no parent, so it is neither a <c>TeamGate</c> (110)
    /// nor an <c>IntelGate</c> (516) and a sentry may stand in one.
    /// </para>
    /// <para>
    /// The two events are keyed by the other object's id like every collision
    /// event, so strictly <c>TeamGate</c> (110) runs before <c>KillBox</c>
    /// (506) and <c>IntelGate</c> (516) after <c>FragBox</c> (509). Both write
    /// the same two fields and neither reads anything, so the interleaving is
    /// not observable and they are done together here.
    /// </para>
    /// </remarks>
    public static void GateCollisionStep(CollisionWorld world, ref WorldState state)
    {
        ArgumentNullException.ThrowIfNull(world);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasSentry[slot])
                continue;

            ref var sentry = ref state.Sentries[slot];

            // The mask, not the reported box: a collision event is a mask
            // overlap, and SentryMaskS does not inflate when the gun faces
            // left. Same call shape as the kill boxes above it.
            var body = BodyBox(sentry);
            var rects = world.RectSpan;

            for (var i = 0; i < rects.Length; i++)
            {
                ref readonly var rect = ref rects[i];

                if (rect.Kind is not (SolidKind.TeamGate or SolidKind.IntelGate))
                    continue;

                if (!rect.Overlaps(body.Left, body.Right, body.Top, body.Bottom))
                    continue;

                // Once per overlapping gate, exactly as GM8 runs the event
                // once per colliding instance. 999 already kills it, so the
                // repeat is only visible in hp, which nothing reads again.
                sentry.Hp -= GateDamage;
                sentry.LastDamageDealer = CharacterState.Nobody;
            }
        }
    }

    /// <summary>
    /// <c>Sentry.events/End Step.xml</c>: die if out of hp, else clamp.
    /// </summary>
    public static void EndStep(CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref var sentry = ref state.Sentries[slot];

        if (Gml.LessOrEqual(sentry.Hp, 0))
        {
            // The killer's healer takes the assist — the same shape as a
            // character death's redirect (Deaths.Resolve), minus the
            // second-to-last dealer a sentry does not track.
            var killer = sentry.LastDamageDealer;
            var assistant = CharacterState.Nobody;

            if (killer != CharacterState.Nobody && state.IsAlive(killer))
            {
                var healer = state.Characters[killer].Healer;
                if (healer != CharacterState.Nobody)
                    assistant = healer;
            }

            ReportDestruction(
                ref state, slot, ref events, killer, assistant, sentry.LastDamageSource,
                inEndStep: true);
            Destroy(world, ref state, slot);
            return;
        }

        if (Gml.Greater(sentry.Hp, SentryState.MaxHp))
            sentry.Hp = SentryState.MaxHp;

        // The idleTimer / alarm[5] block below this in the reference plays the
        // idle sound and is not simulation.
    }

    private static void ReportDestruction(
        ref WorldState state,
        int slot,
        ref TickEvents events,
        byte killer,
        byte assistant,
        DamageSource source,
        bool inEndStep)
    {
        events.SentryDestroyed[slot] = true;
        events.SentryDestroyedInEndStep[slot] = inEndStep;
        events.SentryDestructions[slot] = new SentryDestroyedEvent(slot, killer, assistant, source);

        // doEventDestruction.gml:20-24. Not awarded for an owner detonating
        // their own — and this path is only reached for a *simulated*
        // destruction anyway, which is the same split DESTROY_SENTRY has.
        Scoring.RecordDestruction(ref state, slot, killer);
    }

    /// <summary>
    /// <c>Sentry.events/Destroy.xml</c>: the mines stuck to it drop, the owner
    /// is knocked back, and the sentry — turret included — stops existing.
    /// </summary>
    /// <remarks>
    /// Public because an owner's own <c>DESTROY_SENTRY</c> client command is a
    /// bare <c>instance_destroy</c> handled outside the tick
    /// (<c>processClientCommands.gml</c>), and a class or team change destroys
    /// it from the roster seam.
    /// </remarks>
    public static void Destroy(CollisionWorld world, ref WorldState state, int slot)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref var sentry = ref state.Sentries[slot];
        var sentryBody = BodyBox(in sentry);

        // with(Mine) if (place_meeting(x, y, other.id)) stickied = false —
        // every mine touching the sentry, whoever owns it.
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var projectile = ref state.Projectiles[state.LiveProjectileSlot(i)];

            // The MASK, not the box. A mine in flight carries image_angle
            // from createShot, and a rotated box is a strict over-estimate — so
            // the box unsticks mines that are not touching the sentry at all.
            // Order does not matter here (the `with` visits every mine, and
            // none of them is destroyed), but membership does.
            if (projectile.Kind == ProjectileKind.Mine
                && ProjectileMask.Overlaps(in projectile, in sentryBody))
            {
                projectile.Stickied = false;
            }
        }

        // with(ownerPlayer.object): the self-knockback — and only while the
        // owner is airborne. A grounded engineer does not move at all.
        if (state.IsAlive(slot))
            KnockBackOwner(world, ref state.Characters[slot], in sentry);

        state.HasSentry[slot] = false;
        state.Sentries[slot] = default;
    }

    /// <summary>
    /// <c>SentryTurret.events/End Step.xml</c>: face the aim, find a target,
    /// and shoot it.
    /// </summary>
    /// <remarks>
    /// <b>An End Step, and the only weapon in the game that fires from one.</b>
    /// The turret is created after its sentry and after every character alive
    /// at build time, so its End Step runs after theirs — a character killed
    /// this tick is already gone when the scan runs, and the damage it deals
    /// lands <em>after</em> this tick's death checks, so a victim it finishes
    /// dies in their own End Step next tick.
    /// <para>
    /// The reference's <c>ds_priority</c> pops the minimum distance;
    /// on a tie this port keeps insertion order (characters in slot order,
    /// then sentries), which is the natural behaviour of a first-found-minimum
    /// scan and unmeasured in GM8 — a tie needs two targets at the identical
    /// distance.
    /// </para>
    /// </remarks>
    public static void TurretEndStep(
        CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref var sentry = ref state.Sentries[slot];

        // (The `if (ownerPlayer.sentry) x = ... else instance_destroy` sync is
        // structural: the turret has no position of its own here, and it exists
        // exactly while the sentry does.)

        // if(((direction+270) mod 360)>180) — facing right for a direction in
        // (270, 360) or [0, 90); image_angle is the direction itself on the
        // right and direction+180 on the left, so the sprite always points
        // along the aim. The box the scan below uses is built from these.
        var direction = sentry.TurretDirection;
        if (Gml.Greater((direction + 270) % 360, 180))
        {
            sentry.TurretXScale = 1;
            sentry.TurretImageAngle = direction;
        }
        else
        {
            sentry.TurretXScale = -1;
            sentry.TurretImageAngle = direction + 180;
        }

        var turretReported = TurretReportedBox(in sentry);
        var turretBody = TurretBodyBox(in sentry);

        // The priority queue: characters first, then sentries, both in slot
        // order — the reference's two `with` blocks.
        Span<int> candidate = stackalloc int[WorldState.MaxSlots * 2];
        Span<double> distance = stackalloc double[WorldState.MaxSlots * 2];
        var count = 0;

        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            if (!state.Alive[i])
                continue;

            ref readonly var character = ref state.Characters[i];

            // !cloak: nothing can cloak until the spy lands. distance_to_object
            // from the character — its facing-inflated box against the turret's
            // reported one.
            var gap = Aabb.ForCharacter(character.Box, character.X, character.Y, character.Facing)
                .Distance(turretReported);

            if (Gml.LessOrEqual(gap, SentryState.TurretRange))
            {
                candidate[count] = i;
                distance[count++] = gap;
            }
        }

        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            // with(Sentry) has no team or self filter at this stage — the own
            // sentry enters the queue at distance zero and is rejected by the
            // team test below, exactly as the reference wastes a pop on it.
            if (!state.HasSentry[i])
                continue;

            var gap = ReportedBox(state.Sentries[i]).Distance(turretReported);

            if (Gml.LessOrEqual(gap, SentryState.TurretRange))
            {
                candidate[count] = WorldState.MaxSlots + i;
                distance[count++] = gap;
            }
        }

        // gunSetSolids() wraps the whole while loop in the reference; here the
        // bullet filter is passed where it is used.
        var nearestTarget = -1;

        while (nearestTarget < 0)
        {
            // ds_priority_delete_min: the closest candidate not yet popped.
            var best = -1;
            for (var i = 0; i < count; i++)
            {
                if (distance[i] >= 0 && (best < 0 || distance[i] < distance[best]))
                    best = i;
            }

            if (best < 0)
                break;

            var picked = candidate[best];
            distance[best] = -1;

            var isSentry = picked >= WorldState.MaxSlots;
            var pickedSlot = isSentry ? picked - WorldState.MaxSlots : picked;

            double targetX, targetY, targetHp;
            Team targetTeam;
            Aabb targetBody;

            if (isSentry)
            {
                ref readonly var target = ref state.Sentries[pickedSlot];
                targetX = target.X;
                targetY = target.Y;
                targetHp = target.Hp;
                targetTeam = target.Team;
                targetBody = BodyBox(in target);
            }
            else
            {
                ref readonly var target = ref state.Characters[pickedSlot];
                targetX = target.X;
                targetY = target.Y;
                targetHp = target.Hp;
                targetTeam = target.Team;
                targetBody = Aabb.ForCharacter(target.Box, target.X, target.Y);
            }

            // The angle restriction: within 45 degrees of horizontal on either
            // side — or overlapping the turret, which is the exception for
            // somebody standing directly on the gun. place_meeting, so the
            // *uninflated* boxes.
            var targetAngle = Gml.PointDirection(sentry.X, sentry.Y, targetX, targetY);

            var inArc = Gml.LessOrEqual(targetAngle, 45)
                || Gml.GreaterOrEqual(targetAngle, 315)
                || (Gml.GreaterOrEqual(targetAngle, 135) && Gml.LessOrEqual(targetAngle, 225))
                || turretBody.Overlaps(targetBody);

            if (!inArc
                || targetTeam == sentry.Team
                || !Gml.Greater(targetHp, 0)
                || world.CollisionLineBlocksBullets(
                    sentry.X, sentry.Y, targetX, targetY, state.BulletFilter))
            {
                continue;
            }

            // "it might be obscured by a sentry" — any sentry that is neither
            // this turret's own nor the target itself, tested with
            // collision_line at prec = false, which against a rectangle mask is
            // the pixel walk over the whole box.
            var obscured = false;

            for (var i = 0; i < WorldState.MaxSlots && !obscured; i++)
            {
                if (!state.HasSentry[i] || i == slot || (isSentry && i == pickedSlot))
                    continue;

                ref readonly var blocker = ref state.Sentries[i];

                obscured = CollisionLine.Hits(
                    LineCollider.ForCharacter(Box, blocker.X, blocker.Y, blocker.StartDirection),
                    default(CollisionLine.FilledMask),
                    sentry.X,
                    sentry.Y,
                    targetX,
                    targetY);
            }

            if (!obscured)
                nearestTarget = picked;
        }

        if (nearestTarget < 0)
        {
            sentry.NoticedTarget = true;
            sentry.TurretDirection = sentry.TurretXScale > 0 ? 0 : 180;

            if (sentry.TurretXScale != sentry.StartDirection)
                sentry.Rotating = true;
        }
        else
        {
            // (The cloaked-spy-in-the-way redirect goes here when the spy
            // lands; nothing can cloak yet.)
            var isSentry = nearestTarget >= WorldState.MaxSlots;
            var targetSlot = isSentry ? nearestTarget - WorldState.MaxSlots : nearestTarget;
            var targetX = isSentry ? state.Sentries[targetSlot].X : state.Characters[targetSlot].X;
            var targetY = isSentry ? state.Sentries[targetSlot].Y : state.Characters[targetSlot].Y;

            if ((Gml.Less(targetX, sentry.X) && sentry.TurretXScale == 1)
                || (Gml.Greater(targetX, sentry.X) && sentry.TurretXScale == -1))
            {
                sentry.Rotating = true;
            }

            // Acquiring a target out of idle costs a double-length delay — the
            // "alert" pause before the first shot.
            if (sentry.NoticedTarget)
            {
                sentry.ReadyToShoot = false;
                sentry.RefireTimer = SentryState.RefireTime * 2;
            }

            sentry.NoticedTarget = false;

            if (sentry.ReadyToShoot && !sentry.Rotating)
                Fire(ref state, ref sentry, slot, isSentry, targetSlot, targetX, targetY, ref events);
        }

        // The flip animation: 0.3 of a frame per tick across four frames, so a
        // turn takes fourteen ticks, and the direction actually swings only on
        // the tick it completes.
        if (sentry.Rotating)
        {
            sentry.RotateIndex += 0.3;

            if (Gml.GreaterOrEqual(sentry.RotateIndex, sentry.RotateStart + 4))
            {
                sentry.TurretXScale = -sentry.TurretXScale;
                sentry.TurretDirection = (sentry.TurretDirection + 180) % 360;
                sentry.Rotating = false;
                sentry.RotateIndex = sentry.RotateStart;
            }
        }
    }

    /// <summary>The firing block of the turret's End Step.</summary>
    private static void Fire(
        ref WorldState state,
        ref SentryState sentry,
        int slot,
        bool targetIsSentry,
        int targetSlot,
        double targetX,
        double targetY,
        ref TickEvents events)
    {
        sentry.TurretDirection = Gml.PointDirection(sentry.X, sentry.Y, targetX, targetY);

        // "Prevent the turret from turning too high even if it is shooting in
        // that direction" — the aim is clamped back to the 45-degree arc that
        // the on-the-gun exception let it leave.
        var direction = sentry.TurretDirection;
        if (Gml.GreaterOrEqual(direction, 45) && Gml.Less(direction, 90))
            sentry.TurretDirection = 45;
        else if (Gml.GreaterOrEqual(direction, 90) && Gml.Less(direction, 135))
            sentry.TurretDirection = 135;
        else if (Gml.GreaterOrEqual(direction, 225) && Gml.Less(direction, 270))
            sentry.TurretDirection = 225;
        else if (Gml.GreaterOrEqual(direction, 270) && Gml.Less(direction, 315))
            sentry.TurretDirection = 315;

        // (playsound, the idle-timer reset and the Blood instance are sound
        // and drawing.)

        var ubered = !targetIsSentry && state.Characters[targetSlot].Ubered;

        if (!ubered)
        {
            if (targetIsSentry)
            {
                // damageAuto -> damageSentry, then the unguarded dealer/source
                // writes — which Damage already performs. The
                // `nearestTarget.alarm[3]` write lands on a sentry alarm no
                // event ever reads.
                Damage(ref state, targetSlot, SentryState.TurretHitDamage,
                    (byte)slot, DamageSource.SentryTurret);
            }
            else
            {
                ref var victim = ref state.Characters[targetSlot];

                // damageAuto -> damageCharacter -> dealDamage.
                // Credited to the sentry's OWNER, not to the sentry. A
                // sentry belongs to a player and outlives their character, so
                // the slot here is the one a DESTROY_SENTRY would name.
                DamageApi.Deal(
                    ref events, targetSlot, slot, ref victim, SentryState.TurretHitDamage);
                victim.TimeUnscathed = 0;

                // The same assist bookkeeping every weapon performs, spelled
                // with the turret's own guards: the demoted dealer inherits
                // what is left of the finish-off window.
                if (victim.LastDamageDealer != slot && victim.LastDamageDealer != targetSlot)
                {
                    victim.SecondToLastDamageDealer = victim.LastDamageDealer;
                    victim.AssistTimer = victim.FinishOffTimer;
                }

                victim.FinishOffTimer = GameConstants.AssistTime;
                victim.LastDamageDealer = (byte)slot;
                victim.LastDamageSource = DamageSource.SentryTurret;

                // dealFlicker sits under `global.gibLevel > 0`, which defaults
                // to on; it only matters to a cloaked spy anyway.
                victim.DealFlicker();
            }
        }

        // shooting = true is drawing. The refire alarm re-arms whether or not
        // the target was ubered.
        sentry.ReadyToShoot = false;
        sentry.RefireTimer = SentryState.RefireTime;
    }

    /// <summary>The owner's shove, from the sentry's own Destroy event.</summary>
    private static void KnockBackOwner(CollisionWorld world, ref CharacterState owner, in SentryState sentry)
    {
        // distance_to_object(other) from the character: both boxes as GM8
        // reports them, facing inflation included on each side.
        var ownerBox = Aabb.ForCharacter(owner.Box, owner.X, owner.Y, owner.Facing);
        var distance = ownerBox.Distance(ReportedBox(in sentry));

        if (!Gml.Less(distance, SentryState.BlastRadius))
            return;

        // !place_meeting(x, y+1, Obstacle): only an airborne owner is shoved —
        // the map mask only, the same grounded test as the rocket blast's
        // team-mate exemption.
        if (!world.PlaceFreeInMask(owner.Box, owner.X, owner.Y + 1))
            return;

        // The quarter-radius cutoff is a `continue` inside the with, so the
        // outer quarter gets nothing.
        if (Gml.LessOrEqual(1 - (distance / SentryState.BlastRadius), 0.25))
            return;

        var centreX = owner.X + ((owner.Box.Left + owner.Box.Right) / 2.0);
        var centreY = owner.Y + ((owner.Box.Top + owner.Box.Bottom) / 2.0);
        var pushDirection = Gml.PointDirection(sentry.X, sentry.Y, centreX, centreY);

        var radians = Gml.DegToRad(pushDirection);
        var vectorFactor = Gml.PointDistance(
            0,
            0,
            Math.Pow(Math.Sin(radians), 2),
            Math.Pow(Math.Cos(radians), 2));

        var push = Math.Min(
            15.0,
            SentryState.Knockback - (SentryState.Knockback * (distance / SentryState.BlastRadius)));

        owner.Motion.MotionAdd(pushDirection, push * vectorFactor);
    }
}
