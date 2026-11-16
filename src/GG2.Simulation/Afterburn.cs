using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// Being on fire: two quantities, a delay, and a fade.
/// </summary>
/// <remarks>
/// <c>Character.events/Begin Step.xml:41-70</c> and
/// <c>Objects/Projectiles/BurningProjectile.events/Collision with
/// Character.xml</c>. The character's own <c>Alarm 0</c> is in
/// <see cref="Timers"/>, where the rest of the alarm stage lives.
/// <para>
/// <b>Heat and fuel, and they are topped up separately.</b>
/// <see cref="CharacterState.BurnIntensity"/> is damage per second and
/// <see cref="CharacterState.BurnDuration"/> is how many ticks are left; a flame
/// adds a fixed 30 ticks of fuel and a variable amount of heat, each with its
/// own ceiling. The reference's own words, from
/// <c>Character.events/Create.xml</c>: <c>burnIntensity</c> is "heat" and
/// <c>burnDuration</c> is "fuel".
/// </para>
/// <para>
/// <b>Where this runs.</b> Near the top of Begin Step — after the ground checks
/// and <em>before</em> the attack key — so a character set alight takes its tick
/// of damage before it gets to fire. It is called first in
/// <see cref="MatchSimulation"/>'s per-slot block for that reason. It can take
/// hp to zero, and nothing here notices: the death check is in End Step, as it
/// is for every other damage source (see <see cref="Deaths"/>).
/// </para>
/// </remarks>
public static class Afterburn
{
    /// <summary>
    /// <c>Character.events/Begin Step.xml:41-70</c>: one tick of burning.
    /// </summary>
    /// <remarks>
    /// The whole block is inside <c>if (global.run_virtual_ticks)</c>, which is
    /// true on every tick a headless server simulates — it is the reference's
    /// frameskip guard for a <em>client</em> catching up, and this port has no
    /// frameskip.
    /// </remarks>
    public static void BeginStep(ref WorldState state, int slot, ref TickEvents events)
    {
        ref var character = ref state.Characters[slot];

        // ⛔ A DELIBERATE DEPARTURE FROM THE REFERENCE, and the only one in this
        // file. `Character.events/Begin Step.xml:60` has no `ubered` test: the
        // block that puts a fire out is two hundred lines further down the same
        // event (CharacterPhysics.BeginStepUberEffects), so a burning character
        // takes one more tick of afterburn on the tick it becomes invulnerable —
        // the tick a medic pops, or the tick a beam target is ubered — and a
        // medic on low health dies inside their own uber. Afterburn is the only
        // damage over time in the game and every direct source already tests
        // `ubered`, so this one test closes it.
        //
        // Safe against the stock client: `Character.events/End Step.xml:34` is
        // `if(global.isHost && hp<=0)`, so no client ever kills a character
        // itself, and hp is on the wire in every QUICK_UPDATE. A client that
        // predicted the tick of damage is corrected within seven ticks and
        // nothing else about the character changes.
        //
        // docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried into the port".
        if (Gml.Greater(character.BurnDuration, 0) && !character.Ubered)
        {
            // The reference's own comment on this guard, citing its bug
            // number: "Don't count if someone else already finished him off, to
            // prevent afterburn from 'stealing' sticky kills (Bug #1021989)."
            // A character whose hp is already at or below zero this tick is
            // waiting for End Step to notice; the fire must not overwrite the
            // attribution in the meantime.
            if (Gml.Greater(character.Hp, 0))
            {
                // The same assist bookkeeping every damage source does, and it
                // runs every tick the fire burns — so a fire keeps renewing the
                // pyro's claim on the kill for as long as it lasts.
                if (character.LastDamageDealer != character.BurnedBy
                    && character.LastDamageDealer != slot)
                {
                    character.SecondToLastDamageDealer = character.LastDamageDealer;
                    character.AssistTimer = character.FinishOffTimer;
                }

                character.FinishOffTimer = GameConstants.AssistTime;
                character.LastDamageDealer = character.BurnedBy;
                character.LastDamageSource = character.AfterburnSource;

                // dealDamage, not damageCharacter — so this does not reset
                // timeUnscathed itself. The line four below does it instead,
                // and it runs whether or not the victim was still alive.
                // Credited to `BurnedBy`, the line above — the pyro who lit
                // the fire, not whoever last hit them. That slot can be
                // CharacterState.Nobody once the lighter has left, and then the
                // burn is taken by somebody and dealt by no one.
                DamageApi.Deal(
                    ref events, slot, character.BurnedBy, ref character, character.BurnIntensity / 30);
            }

            character.BurnDuration -= CharacterState.BurnDurationDecay;
            character.TimeUnscathed = 0;
        }

        // `alarm[0] == -1`, exactly — not `<= 0`. The timer is re-armed to 90
        // by every flame that lands, so this is false for as long as somebody is
        // being sprayed and for ninety ticks afterwards; only then does the heat
        // start bleeding off at the rate Alarm 0 computed. See Timers.Advance
        // for why the resting value had to be -1 and not 0.
        if (character.AfterburnDecayTimer == Timers.Inactive && Gml.Greater(character.BurnIntensity, 0))
            character.BurnIntensity -= character.IntensityDecay;

        // Either one running out ends the fire, and it forgets who lit it. That
        // matters: a fire that expires cannot finish anybody off afterwards, and
        // BurnedBy is the only place the pyro's claim was recorded.
        if (Gml.LessOrEqual(character.BurnDuration, 0) || Gml.LessOrEqual(character.BurnIntensity, 0))
        {
            character.BurnDuration = 0;
            character.BurnIntensity = 0;
            character.BurnedBy = CharacterState.Nobody;
            character.AfterburnSource = DamageSource.None;
        }
    }

    /// <summary>
    /// The afterburn half of
    /// <c>BurningProjectile.events/Collision with Character.xml</c>: what one
    /// flame adds to a victim already alight.
    /// </summary>
    /// <remarks>
    /// <b>The falloff runs the opposite way round from the comment beside
    /// it.</b> The reference says "value higher when life is fresh (alarm 0 is
    /// high)" and "Reduce heat additions of 'old'/distant flames", but the
    /// expression is <c>(flameLife - alarm[0]) / flameLife</c> — which is
    /// <em>small</em> when the alarm is high, i.e. when the flame is young. A
    /// point-blank flame therefore adds <b>0.35</b> of the heat and one at the
    /// end of its fifteen-tick life adds the full amount. The code is the
    /// specification; do not "fix" it to match the comment.
    /// </remarks>
    /// <param name="victim">The character being set alight.</param>
    /// <param name="flame">The projectile that reached them.</param>
    /// <param name="ownerSlot">Who gets blamed for the fire.</param>
    public static void Ignite(ref CharacterState victim, in ProjectileState flame, int ownerSlot)
    {
        victim.BurnDuration += FlameStats.DurationIncrease(flame.Kind);

        // alarm[0] is the flame's own lifetime counting down from flameLife.
        // The reference divides it by global.delta_factor, which is 1 at 30 Hz.
        var falloffAmount = Math.Min(
            1,
            (FlameStats.Life(flame.Kind) - flame.Lifetime) / (double)FlameStats.Life(flame.Kind));

        // afterburnFalloff is true for a Flame and false for a Flare, which is
        // why it is a per-kind flag rather than folded into the arithmetic.
        // 0.65 and 0.35 are two of the literals GM8 parses one ULP above the
        // IEEE nearest, hence Gml rather than plain decimals.
        var falloffFactor = FlameStats.HasFalloff(flame.Kind)
            ? (falloffAmount * Gml.PointSixFive) + Gml.PointThreeFive
            : 1;

        // Above the "leg" the additions are halved — the reference names it
        // after the knee in a dynamic-range compressor. Guarded on the same flag
        // as the falloff, so a Flare ignores it too.
        if (Gml.Greater(victim.BurnIntensity, CharacterState.BurnLegIntensity)
            && FlameStats.HasFalloff(flame.Kind))
            falloffFactor /= 2;

        victim.BurnIntensity += FlameStats.IntensityIncrease(flame.Kind) * falloffFactor;

        // Both ceilings are applied after both additions, not inside them, and
        // the fuel ceiling is the VICTIM's class's: a pyro's is 10, not 210.
        victim.BurnDuration = Math.Min(victim.BurnDuration, victim.Stats.MaxBurnDuration);
        victim.BurnIntensity = Math.Min(victim.BurnIntensity, CharacterState.MaxBurnIntensity);

        victim.BurnedBy = (byte)ownerSlot;
        victim.AfterburnSource = flame.Source;

        // The decay delay, restarted by every flame that lands. Ninety ticks
        // after the last one the character's Alarm 0 computes the fade rate and
        // BeginStep above starts applying it.
        victim.AfterburnDecayTimer = CharacterState.BurnDecayDelay;
    }
}

/// <summary>
/// The numbers a <c>BurningProjectile</c>'s child object defines for it.
/// </summary>
/// <remarks>
/// <c>BurningProjectile.events/Create.xml</c> opens with a comment listing them:
/// "Child objects will need to define: hitDamage, flameLife, burnIncrease,
/// durationIncrease, afterburnFalloff (bool), penetrateCap. Then they need to
/// call the inherited event." So they are per-kind constants rather than state,
/// and they live here for the same reason <see cref="WeaponStats"/> exists.
/// <para>
/// Both children are filled in: the <c>Flame</c>
/// (<c>Flame.events/Create.xml</c>) and the <c>Flare</c>
/// (<c>Flare.events/Create.xml</c>), which arrived with the airblast that
/// throws it.
/// </para>
/// </remarks>
public static class FlameStats
{
    /// <summary>Damage on contact. Flame 3.14, Flare <b>30</b>.</summary>
    public static double HitDamage(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Flame => 3.14,
        ProjectileKind.Flare => 30,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a burning projectile"),
    };

    /// <summary><c>flameLife</c>: <c>alarm[0]</c>, and the falloff's denominator.</summary>
    public static int Life(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Flame => 15,
        ProjectileKind.Flare => 40,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a burning projectile"),
    };

    /// <summary><c>durationIncrease</c>: ticks of fuel one hit adds.</summary>
    public static double DurationIncrease(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Flame => 30,
        ProjectileKind.Flare => 35,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a burning projectile"),
    };

    /// <summary><c>penetrateCap</c>: how many people it burns before dying.</summary>
    /// <remarks>
    /// The count is compared <em>before</em> the victim is recorded, so a
    /// Flame's cap of 1 burns its first target and dies on the second — and the
    /// Flare's cap of <b>0</b> dies on its first. A <c>perseverant</c> one
    /// skips the destroy entirely and can burn everyone once.
    /// </remarks>
    public static int PenetrateCap(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Flame => 1,
        ProjectileKind.Flare => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a burning projectile"),
    };

    /// <summary>
    /// The chance a <c>BurningProjectile</c> is <c>perseverant</c>: one in eight.
    /// </summary>
    /// <remarks>
    /// <c>random(1) &lt; (1/8)</c> in the parent's Create event — a draw off the
    /// global stream, like everything else the Flamethrower does. The pellet's
    /// <c>perseverant</c> is a <c>choose(0, 0, 1)</c> inside a <em>seeded</em>
    /// window and is contract; this one is not, because no seed is broadcast.
    /// </remarks>
    public const double PerseverantChance = 1.0 / 8.0;

    /// <summary><c>burnIncrease</c>: heat one hit adds, before the falloff.</summary>
    public static double IntensityIncrease(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Flame => 1.35,
        ProjectileKind.Flare => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a burning projectile"),
    };

    /// <summary><c>afterburnFalloff</c>: whether the age curve and the leg apply.</summary>
    public static bool HasFalloff(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Flame => true,
        ProjectileKind.Flare => false,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a burning projectile"),
    };
}
