using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// Everything a bot's difficulty decides — <c>Scripts/Bots/botSkillApply.gml</c>.
/// </summary>
/// <remarks>
/// <b>The whole difficulty model, and it is entirely about the see → acquire →
/// aim → fire chain.</b> Nothing here makes a bot's <em>decisions</em> worse; it
/// makes its reactions slower, its tracking coarser and its aim looser, which is
/// what a weaker player actually looks like.
/// <para>
/// <b>The server's 1-5 setting is one point on a continuous scale</b>, in even
/// steps, so tiers 1, 4 and 5 land exactly on the anchors the tuning tables are
/// quoted at: <c>0.15 + (tier - 1) × 0.2</c>.
/// </para>
/// </remarks>
/// <param name="AcquireTicks">
/// How long a bot ignores a target completely after picking it. It does not
/// even turn towards it — which is what makes a weak bot look <em>slow</em>
/// rather than blind.
/// </param>
/// <param name="FireDelayTicks">Extra ticks after acquisition before the trigger is allowed.</param>
/// <param name="PerceiveTicks">How often the bot re-reads where the target is and how fast it is going.</param>
/// <param name="AimInterval">How often the desired direction is recomputed from that snapshot.</param>
/// <param name="TurnRate">Degrees a tick the actual aim slews towards the desired one.</param>
/// <param name="AimErrorDeg">The floor of the aim error cone.</param>
/// <param name="AimConeDeg">The part of the cone that decays as the target is watched.</param>
/// <param name="MoveErrorMultiplier">What the error is scaled by when the target is moving.</param>
/// <param name="HoldFireChance">Quake III's fire throttle: the chance of simply not shooting this aim interval.</param>
/// <param name="SplashAim">Whether this bot knows to aim at a grounded target's feet.</param>
public readonly record struct BotSkill(
    int AcquireTicks,
    int FireDelayTicks,
    int PerceiveTicks,
    int AimInterval,
    double TurnRate,
    double AimErrorDeg,
    double AimConeDeg,
    double MoveErrorMultiplier,
    double HoldFireChance,
    bool SplashAim,
    BotLeadMode LeadMode)
{
    /// <summary>
    /// The knobs for a skill in <c>[0, 1]</c> — <c>botSkillApply.gml</c>.
    /// </summary>
    /// <remarks>
    /// The tick counts are <c>floor(x + 0.5)</c>, which is round-half-up and
    /// <b>not</b> GM8's <c>round()</c> — the reference writes it out that way, and
    /// on a value landing exactly on <c>.5</c> the two disagree by a whole tick.
    /// Transcribed as written.
    /// </remarks>
    public static BotSkill For(double skill)
    {
        skill = Math.Clamp(skill, 0, 1);

        return new BotSkill(
            AcquireTicks: (int)Math.Floor(Lerp(skill, 30, 15, 9, 6) + 0.5),
            FireDelayTicks: (int)Math.Floor(Lerp(skill, 45, 21, 10, 0) + 0.5),
            PerceiveTicks: Math.Max(1, (int)Math.Floor(Lerp(skill, 15, 10, 5, 1) + 0.5)),
            AimInterval: Math.Max(1, (int)Math.Floor(Lerp(skill, 8, 5, 3, 2) + 0.5)),
            TurnRate: Lerp(skill, 6, 7.5, 9, 24),
            AimErrorDeg: Lerp(skill, 4, 1.5, 0.5, 0.2),
            AimConeDeg: Lerp(skill, 20, 10, 4, 1),
            MoveErrorMultiplier: Lerp(skill, 1, 0.6, 0.4, 0.3),
            HoldFireChance: Lerp(skill, 0.35, 0.15, 0.05, 0),

            // A step rather than a ramp: knowing to aim at somebody's feet is
            // a thing you have learnt or have not.
            SplashAim: skill >= 0.6,

            // Every tier solves the full intercept; what differs is how often it
            // re-solves and how far off it then aims.
            LeadMode: BotLeadMode.Full);
    }

    /// <summary>The server's 1-5 difficulty as a skill.</summary>
    /// <remarks>
    /// <c>botAdd.gml</c>: <c>0.15 + (clamped tier - 1) × 0.2</c>, so the five
    /// tiers are 0.15, 0.35, 0.55, 0.75, 0.95 — and 0.15 and 0.95 are exactly the
    /// ends of <see cref="Lerp"/>'s flat regions.
    /// </remarks>
    public static BotSkill ForTier(int tier) =>
        For(0.15 + ((Math.Clamp(tier, BotConstants.TierMin, BotConstants.TierMax) - 1) * 0.2));

    /// <summary>
    /// A four-anchor piecewise ramp — <c>botSkillLerp.gml</c>.
    /// </summary>
    /// <remarks>
    /// <b>Flat below 0.15 and above 0.95, and the three segments between are
    /// not the same width</b>: 0.15-0.45, 0.45-0.75, then 0.75-0.95. The last is
    /// two thirds the width of the others, so the top tier moves further per
    /// point of skill than the ones below it — which is deliberate, and is why
    /// this is a table rather than a formula.
    /// </remarks>
    public static double Lerp(double skill, double v0, double v1, double v2, double v3)
    {
        if (skill <= 0.15)
            return v0;

        if (skill >= 0.95)
            return v3;

        if (skill <= 0.45)
            return v0 + ((v1 - v0) * (skill - 0.15) / 0.3);

        if (skill <= 0.75)
            return v1 + ((v2 - v1) * (skill - 0.45) / 0.3);

        return v2 + ((v3 - v2) * (skill - 0.75) / 0.2);
    }
}
