using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// One team's generator: the only objective in the game with hit points.
/// </summary>
/// <remarks>
/// <c>Objects/Map elements/Gamemode-specific/Generator/Generator.events/</c>.
/// A generator is a map object — placed by the level, older than the HUD and
/// older than every character — that sits still, absorbs damage behind a
/// regenerating shield, and ends the match when it runs out.
/// <para>
/// <b>No attribution at all.</b> <c>damageGenerator</c> ends at
/// <c>dealDamage</c>, which is <c>hp -= damage</c> and a plugin hook; there is
/// no <c>lastDamageDealer</c>, no assist window and no
/// <c>DESTROY_GENERATOR</c>-style credit. Whoever fires the last shot gets
/// nothing but the win, and the wire message names the <em>losing</em> team.
/// </para>
/// </remarks>
public struct GeneratorState
{
    /// <summary>
    /// <c>instance_exists(GeneratorRed)</c>: the map placed one and it has not
    /// been destroyed.
    /// </summary>
    /// <remarks>
    /// The generator is the one objective entity that can stop existing
    /// mid-match, and it is observable on the wire: a HUD block carries one
    /// two-field record per <em>surviving</em> generator, so a destroyed one
    /// shortens the message by four bytes. See <see cref="Generators.Destroy"/>.
    /// </remarks>
    public bool Exists;

    /// <summary>Where the map put it. Fixed for the life of the map.</summary>
    public double X;

    /// <inheritdoc cref="X"/>
    public double Y;

    /// <summary>
    /// <c>hp</c>, out of <see cref="Generators.MaxHp"/>. A real, and the wire
    /// carries <c>ceil(hp)</c> — a generator on 0.1 hp is still standing.
    /// </summary>
    public double Hp;

    /// <summary>
    /// <c>shieldHp</c>, out of <see cref="Generators.MaxShieldHp"/>.
    /// </summary>
    /// <remarks>
    /// <b>The shield halves damage rather than absorbing it.</b>
    /// <c>damageGenerator.gml:13</c> is
    /// <c>dealDamage(..., damage * shieldResistance)</c> <em>and</em>
    /// <c>shieldHp -= damage</c> — so a shot that the shield covers still takes
    /// half its value off the generator's own hp, and spends its full value off
    /// the shield.
    /// </remarks>
    public double ShieldHp;

    /// <summary>
    /// <c>isShieldRegenerating</c>: three seconds have passed since the last
    /// hit and the shield is coming back.
    /// </summary>
    /// <remarks>
    /// <b>It is also the rate.</b> <c>Step.xml:15</c> reads
    /// <c>shieldHp + isShieldRegenerating*global.delta_factor</c>, and GM8's
    /// <c>true</c> is 1 — so the shield returns at one point a tick and a full
    /// 300 takes ten seconds. Not a typo to tidy: writing a named constant here
    /// would be inventing a number the reference does not have.
    /// </remarks>
    public bool IsShieldRegenerating;

    /// <summary>
    /// <c>alarm[0]</c>: ticks until the shield starts coming back. Rests at
    /// <see cref="Timers.Inactive"/>.
    /// </summary>
    /// <remarks>
    /// Re-armed to <see cref="Generators.RegenerationBuffer"/> by every hit, so
    /// while a generator is under fire it never runs. <b>It is also read as
    /// a flag by the round</b>: <c>GeneratorHUD.events/Step.xml</c> breaks a
    /// stage tie at 0:00 by asking <c>alarm[0] &gt; -1</c>, which is "this
    /// generator has been hit in the last three seconds".
    /// </remarks>
    public int RegenerationAlarm;
}

/// <summary>
/// The generator objective: damage, the shield, and the explosion that ends the
/// match.
/// </summary>
public static class Generators
{
    /// <summary><c>maxHp = 2100</c>. <c>Generator.events/Create.xml</c>.</summary>
    public const double MaxHp = 2100;

    /// <summary><c>maxShieldHp = 300</c>.</summary>
    public const double MaxShieldHp = 300;

    /// <summary>
    /// <c>shieldResistance = 0.5</c>: the fraction of a shielded hit that
    /// reaches the generator anyway.
    /// </summary>
    public const double ShieldResistance = 0.5;

    /// <summary>
    /// <c>regenerationBuffer = 3 * 30</c>: ticks of quiet before the shield
    /// starts coming back.
    /// </summary>
    public const int RegenerationBuffer = 3 * 30;

    /// <summary>
    /// <c>blastRadius = 400</c> — six times a rocket's, and the largest number
    /// in the game.
    /// </summary>
    public const double BlastRadius = 400;

    /// <summary>
    /// Damage the dying generator deals to everything it reaches.
    /// </summary>
    /// <remarks>
    /// <c>hp -= 999</c>, written straight rather than through
    /// <c>dealDamage</c> — so it skips the deathmatch invulnerability check and
    /// the plugin hook that every other damage path runs.
    /// </remarks>
    public const double ExplosionDamage = 999;

    /// <summary>
    /// The generator's collision box about its origin.
    /// </summary>
    /// <remarks>
    /// <c>GeneratorRedS.xml</c> and <c>GeneratorBlueS.xml</c> are identical: a
    /// <c>RECTANGLE</c> mask on <c>MANUAL</c> bounds
    /// <c>left 2, top 14, right 43, bottom 41</c> with origin <c>(23, 21)</c>,
    /// which is <c>-21, -7, +20, +20</c> about that origin.
    /// <para>
    /// <b>Read from the sprite XML, not measured against the running
    /// game</b> — the same standing as the briefcase's box, and the same
    /// caveat: a fixture cannot confirm it, because no recording has ever been
    /// made on a generator map. The one trap that would apply is checked: a
    /// MANUAL bound is clamped to the image, and at 46x42 pixels neither
    /// <c>right 43</c> nor <c>bottom 41</c> is out of range, so nothing is
    /// clamped away.
    /// </para>
    /// <para>
    /// The sprite has 48 frames and the mask does not animate
    /// (<c>separate = false</c>), so this is the box at every
    /// <c>image_index</c> — the same arrangement the sentry's mask has.
    /// </para>
    /// </remarks>
    public static readonly CharacterBox Box = new(left: -21, right: 20, top: -7, bottom: 20);

    /// <summary>One team's generator, by team.</summary>
    /// <remarks>
    /// A static taking the state by reference for the reason
    /// <see cref="Intel.Of"/> is one: C# will not let a struct hand out a
    /// reference to its own field (CS8170), and copying a
    /// <see cref="GeneratorState"/> out and back at every call site is exactly
    /// the silent aliasing <see cref="WorldState"/> exists to avoid.
    /// </remarks>
    public static ref GeneratorState Of(ref WorldState state, Team team) =>
        ref (team == Team.Blue ? ref state.BlueGenerator : ref state.RedGenerator);

    /// <summary>
    /// The box GM8 reports for a generator, which is what
    /// <c>distance_to_object</c> and <c>place_meeting</c> both consume.
    /// </summary>
    /// <remarks>
    /// One box for both, unlike a character or a sentry: a generator's
    /// <c>image_xscale</c> is never assigned, so it stays at GM8's default of
    /// +1 and the two-pixel <c>bbox_left</c> inflation that splits those two
    /// primitives apart never applies.
    /// </remarks>
    public static Aabb BodyBox(in GeneratorState generator) =>
        Aabb.ForCharacter(Box, generator.X, generator.Y);

    /// <summary>
    /// Puts the map's generators in the state. Called once, when a map loads.
    /// </summary>
    public static void Place(ref WorldState state, LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        state.RedGenerator = default;
        state.BlueGenerator = default;

        foreach (var entity in map.Entities)
        {
            switch (entity.Type)
            {
                // First of each, like Intel.Place: a map with two red
                // generators would give the reference two instances that every
                // `GeneratorRed.hp` in the game reads as one. No shipped map
                // has more than one of each.
                case EntityTypes.GeneratorRed when !state.RedGenerator.Exists:
                    state.RedGenerator = Fresh(entity.X, entity.Y);
                    break;

                case EntityTypes.GeneratorBlue when !state.BlueGenerator.Exists:
                    state.BlueGenerator = Fresh(entity.X, entity.Y);
                    break;
            }
        }
    }

    /// <summary><c>Generator.events/Create.xml</c>, in full.</summary>
    private static GeneratorState Fresh(double x, double y) => new()
    {
        Exists = true,
        X = x,
        Y = y,
        Hp = MaxHp,
        ShieldHp = MaxShieldHp,
        IsShieldRegenerating = false,

        // Not zero: a GM8 alarm rests at -1, and this one is read *as* a flag
        // by the round's stage tie-break, where a resting 0 would read as
        // "damaged in the last three seconds" for ever. See
        // GeneratorState.RegenerationAlarm.
        RegenerationAlarm = Timers.Inactive,
    };

    /// <summary>
    /// <c>Generator.events/Begin Step.xml</c>: the only thing on it the server
    /// can observe — a generator on no hit points takes the match with it.
    /// </summary>
    /// <remarks>
    /// <b>Where this sits in the tick is observable.</b> A generator is a map
    /// object, so its Begin Step runs before every character's — the explosion
    /// therefore reads each victim at its <em>end-of-previous-tick</em>
    /// position, and the knockback it deals is in the state by the time that
    /// character accelerates. The damage that triggered it landed in last
    /// tick's collision stage.
    /// <para>
    /// Red before blue, which is only observable if both reach zero on the
    /// same tick — the second one to go still ends the match, and
    /// <c>global.winners</c> keeps the first one's answer
    /// (<c>doEventGeneratorDestroy.gml:14</c> is <c>if(global.winners == -1)</c>).
    /// The reference's order is whichever instance the map created first, which
    /// this port does not record.
    /// </para>
    /// <para>
    /// The rest of the event is animation: a smoke counter, the damage-stage
    /// sprite range and a particle emitter, none of which a headless server
    /// has. The <c>frame mod 1800</c> scoreboard sync is real but needs the
    /// server's frame counter, so it lives in <c>GameServer</c>.
    /// </para>
    /// </remarks>
    public static void BeginStep(CollisionWorld world, ref WorldState state, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (state.RedGenerator.Exists && Gml.LessOrEqual(state.RedGenerator.Hp, 0))
        {
            events.RedGeneratorDestroyed = true;
            Destroy(world, ref state, Team.Red, ref events);
        }

        if (state.BlueGenerator.Exists && Gml.LessOrEqual(state.BlueGenerator.Hp, 0))
        {
            events.BlueGeneratorDestroyed = true;
            Destroy(world, ref state, Team.Blue, ref events);
        }
    }

    /// <summary>
    /// <c>Generator.events/Step.xml</c>: the shield coming back.
    /// </summary>
    /// <remarks>
    /// The <c>shieldIndex</c> animation counter beside it is drawing and is not
    /// carried.
    /// </remarks>
    public static void Step(ref WorldState state)
    {
        StepOne(ref state.RedGenerator);
        StepOne(ref state.BlueGenerator);
    }

    private static void StepOne(ref GeneratorState generator)
    {
        if (!generator.Exists || !generator.IsShieldRegenerating)
            return;

        // `min(shieldHp + isShieldRegenerating*delta_factor, maxShieldHp)`, and
        // the flag really is the rate — see GeneratorState.IsShieldRegenerating.
        generator.ShieldHp = Math.Min(generator.ShieldHp + 1, MaxShieldHp);

        // GML's `==` is fuzzy to 1e-13, and both sides here are exact integers
        // anyway; written through Gml.Equal because every real-to-real
        // comparison in this assembly is.
        if (Gml.Equal(generator.ShieldHp, MaxShieldHp))
            generator.IsShieldRegenerating = false;
    }

    /// <summary>
    /// <c>Generator.events/Alarm 0.xml</c>: three quiet seconds are up.
    /// </summary>
    /// <remarks>
    /// In <see cref="Timers.Advance(ref WorldState, Maps.SpawnPoints?, ref TickEvents)"/>'s
    /// alarm-0 stage rather than here at the call site, because the stage is
    /// index-major across every instance in the room; this is the body.
    /// </remarks>
    internal static void ShieldBufferElapsed(ref GeneratorState generator) =>
        generator.IsShieldRegenerating = true;

    /// <summary>
    /// <c>Scripts/Damage API/damageGenerator.gml</c>, in full.
    /// </summary>
    /// <param name="team">Whose generator is being hit.</param>
    /// <param name="damage">
    /// What the weapon dealt, before the shield takes its cut.
    /// </param>
    /// <remarks>
    /// <b>The shield is a discount, not a barrier.</b> A hit the shield
    /// covers still costs the generator half the damage in real hp, and costs
    /// the shield the <em>full</em> amount; a hit that overruns the shield pays
    /// half on the covered part and full on the rest. So 300 points of shield
    /// buy 150 points of generator and nothing else.
    /// <para>
    /// <b>Every hit re-arms the buffer</b>, so a generator under sustained
    /// fire never begins regenerating — and the round's stage tie-break reads
    /// that same alarm to decide whether a drawn match is still live.
    /// </para>
    /// </remarks>
    public static void Damage(ref WorldState state, Team team, double damage)
    {
        ref var generator = ref Of(ref state, team);

        if (!generator.Exists)
            return;

        // `alarm[0] = regenerationBuffer / global.delta_factor` — the division
        // is by 1 on a server running at its nominal rate, which is the only
        // rate this port has.
        generator.RegenerationAlarm = RegenerationBuffer;
        generator.IsShieldRegenerating = false;

        if (Gml.Greater(damage, generator.ShieldHp))
        {
            // "allow overkill to be applied directly to the target", the
            // reference's own comment.
            generator.Hp -= (damage - generator.ShieldHp) + (generator.ShieldHp * ShieldResistance);
            generator.ShieldHp = 0;
            return;
        }

        generator.Hp -= damage * ShieldResistance;
        generator.ShieldHp -= damage;
    }

    /// <summary>
    /// <c>doEventGeneratorDestroy</c> and the <c>Destroy</c> event it triggers.
    /// </summary>
    /// <param name="team">The team whose generator has been destroyed.</param>
    /// <remarks>
    /// <b>The message names the loser, and its own docstring says
    /// otherwise.</b> <c>sendEventGeneratorDestroy.gml:2</c> reads "The team who
    /// destroyed the bomb", but its one caller passes the generator's <em>own</em>
    /// team and <c>doEventGeneratorDestroy</c> reads it as the team that lost
    /// one. The comment is the wrong half.
    /// <para>
    /// <b>The explosion is indiscriminate.</b> The <c>with(Character)</c>
    /// loop has no team test, no uber test and no line of sight: everything
    /// within 400 pixels of a dying generator takes 999 damage and a shove,
    /// including the team that just won. The same goes for the
    /// <c>with(Sentry)</c> loop.
    /// </para>
    /// <para>
    /// <b>It writes <c>hp</c> directly rather than calling
    /// <c>damageSentry</c></b>, so a sentry caught in it does not take the
    /// permanent build-rate penalty on the way out. That only matters because
    /// the sentry survives the round.
    /// </para>
    /// <para>
    /// <b>Not modelled as loops</b>: the <c>DeadGuy</c>, <c>Gib</c> and
    /// <c>Bubble</c> <c>with()</c> blocks. The first two are client-side debris.
    /// The bubble is <c>ProjectileKind.Bubble</c>, and the explosion really does
    /// pop the ones in its blast: <see cref="Explosions.PopBubblesByBox"/>,
    /// called below. What is not transcribed is the GML loop, which is
    /// CLAUDE.md's rule rather than an omission.
    /// </para>
    /// </remarks>
    public static void Destroy(
        CollisionWorld world, ref WorldState state, Team team, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref var generator = ref Of(ref state, team);
        if (!generator.Exists)
            return;

        // `if(global.winners == -1) global.winners = winTeam;` — the first
        // generator to go decides the match, and a second one going later
        // cannot overturn it.
        if (!state.RoundDecided)
            Rounds.Decide(ref state, team == Team.Red ? Team.Blue : Team.Red);

        var box = BodyBox(generator);
        var x = generator.X;
        var y = generator.Y;

        // Cleared before the blast, not after. In the reference the instance is
        // still alive while its own Destroy event runs, so a rocket the blast
        // sets off could `damageGenerator` the corpse — which is unobservable,
        // because the instance is removed the moment the event returns and
        // nothing has read its hp in between. Clearing first is the same
        // simulation with one fewer way to re-enter this method.
        generator.Exists = false;

        BlastCharacters(ref state, box, x, y);
        BlastSentries(ref state, box);
        BlastProjectiles(world, ref state, box, ref events);
    }

    /// <summary><c>with (Character)</c>: 999 damage and a shove, to everybody.</summary>
    private static void BlastCharacters(ref WorldState state, in Aabb box, double x, double y)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var victim = ref state.Characters[slot];

            // distance_to_object from the character: its facing-inflated box,
            // which is the box GM8 reports and the one every splash in the game
            // is measured against.
            var gap = Aabb.ForCharacter(victim.Box, victim.X, victim.Y, victim.Facing).Distance(box);

            if (!Gml.Less(gap, BlastRadius))
                continue;

            // point_direction from the *generator's* origin to the character's,
            // not between the two boxes.
            victim.Motion.MotionAdd(
                Gml.PointDirection(x, y, victim.X, victim.Y),
                15 - (15 * (gap / BlastRadius)));

            victim.Hp -= ExplosionDamage;

            // `if(instance_exists(lastDamageDealer) && lastDamageDealer !=
            // player)`: somebody else hurt them last, so they are finished off
            // and gibbed and that somebody keeps the kill. Otherwise the
            // generator takes it, and the dealer is cleared so Deaths.Resolve
            // credits nobody.
            if (victim.LastDamageDealer != CharacterState.Nobody && victim.LastDamageDealer != slot)
            {
                victim.LastDamageSource = DamageSource.FinishedOffGib;
                continue;
            }

            victim.LastDamageDealer = CharacterState.Nobody;
            victim.LastDamageSource = DamageSource.GeneratorExplosion;
        }
    }

    /// <summary><c>with (Sentry)</c>: 999 damage, and no build-rate penalty.</summary>
    private static void BlastSentries(ref WorldState state, in Aabb box)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasSentry[slot])
                continue;

            ref var sentry = ref state.Sentries[slot];

            if (!Gml.Less(Sentries.ReportedBox(sentry).Distance(box), BlastRadius))
                continue;

            sentry.Hp -= ExplosionDamage;
            sentry.LastDamageDealer = CharacterState.Nobody;
            sentry.LastDamageSource = DamageSource.GeneratorExplosion;
        }
    }

    /// <summary>
    /// <c>with (Rocket)</c> then <c>with (Mine)</c>: everything in the air that
    /// can go off, does.
    /// </summary>
    /// <remarks>
    /// Collected before anything is set off, because both bodies free pool
    /// slots and compact the creation-order array underneath a walk. Rockets
    /// before mines, which is the order the event writes them in and is
    /// observable: a rocket's blast can push a character into or out of a mine's.
    /// </remarks>
    private static void BlastProjectiles(
        CollisionWorld world, ref WorldState state, in Aabb box, ref TickEvents events)
    {
        Span<ProjectileHandle> rockets = stackalloc ProjectileHandle[WorldState.MaxProjectiles];
        Span<ProjectileHandle> mines = stackalloc ProjectileHandle[WorldState.MaxProjectiles];
        var rocketCount = 0;
        var mineCount = 0;

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            var handle = state.LiveProjectileHandle(i);
            ref readonly var projectile = ref state.Projectiles[handle.Index];

            if (projectile.Kind is not (ProjectileKind.Rocket or ProjectileKind.Mine))
                continue;

            if (!Gml.Less(projectile.Box.Distance(box), BlastRadius))
                continue;

            if (projectile.Kind == ProjectileKind.Rocket)
                rockets[rocketCount++] = handle;
            else
                mines[mineCount++] = handle;
        }

        for (var i = 0; i < rocketCount; i++)
        {
            // `characterHit = -1; event_user(5);` — a wall-style explosion with
            // no direct victim.
            if (state.IsLive(rockets[i]))
                Explosions.Explode(world, ref state, rockets[i], CharacterState.Nobody, ref events);
        }

        for (var i = 0; i < mineCount; i++)
        {
            // `event_user(2)`, which chains into any mine this one reaches.
            if (state.IsLive(mines[i]))
                Explosions.DetonateMine(world, ref state, mines[i], ref events);
        }

        // `with(Bubble) if (distance_to_object(other) < other.blastRadius)` —
        // the *whole* 400, where a rocket's own bubble sweep is scaled to
        // 0.66 of a much smaller radius. A generator going up clears every
        // bubble on the map short of the far corner.
        Explosions.PopBubblesByBox(ref state, box, BlastRadius);
    }

    /// <summary>
    /// One generator as <c>Generator.events/User Event 12.xml</c> writes it, or
    /// null where there is no instance to serialise.
    /// </summary>
    /// <remarks>
    /// <b>A destroyed generator writes nothing at all</b>, not a zeroed
    /// block: the HUD's <c>with GeneratorBlue event_user(12)</c> runs over
    /// however many instances there are, so the block is four bytes shorter for
    /// each one that has gone. Same shape as a carried briefcase's instance
    /// count of zero.
    /// </remarks>
    public static GeneratorBlock? ToBlock(in GeneratorState generator) =>
        generator.Exists
            ? new GeneratorBlock(
                WireQuantise.ToUShort(Gml.Ceil(generator.Hp)),
                WireQuantise.ToUShort(Gml.Ceil(generator.ShieldHp)))
            : null;
}
