using GG2.Protocol;

namespace GG2.Bots;

/// <summary>
/// Everything a bot's difficulty decides — <c>Scripts/Bots/botSkillApply.gml</c>.
/// </summary>
/// <remarks>
/// <b>The whole difficulty model, and it is entirely about the see → acquire →
/// aim → fire chain.</b> Nothing here makes a bot's <em>decisions</em> worse; it
/// makes its reactions slower, its tracking coarser and its aim looser, which is
/// what a weaker player actually looks like.
/// <para>
/// <b>The server's 1-5 setting is one point on a continuous scale</b>, at the
/// skills in <see cref="TierSkills"/> — spaced so the tiers are evenly apart in
/// play, not evenly apart in skill.
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
/// <param name="LeadMode">How the intercept is solved.</param>
/// <param name="ReflexTicks">
/// How long a projectile has to have been in the air before this bot reacts to
/// it at all — the rocket dodge and the pyro's airblast both. Measured as the
/// distance the projectile has flown at its own speed, so a shot fired from far
/// off is seen in time at every tier and only a close one beats a slow bot.
/// </param>
/// <param name="ReflexMissChance">
/// The chance of never reacting to a given projectile. Decided once per
/// projectile and bot (<see cref="BotReflex"/>), not re-rolled each tick, which
/// would make a miss vanishingly rare over a rocket's flight.
/// </param>
/// <param name="HumanAim">
/// Whether this bot's aim, scripted or policy, goes through
/// <see cref="HumanizeOptions.Tracking2"/>: lagging its target the way people do.
/// True for tiers 1-4 and false for tier 5, which stays the strongest bot there is
/// (<c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, D3). Applied by
/// <see cref="HumanizedBot.ByTier"/>, so a live difficulty change takes at once.
/// </param>
/// <param name="PolicyAimErrorDeg">
/// The half-width of the uniform aim error a <see cref="HybridBot"/> adds to its
/// controller's aim, redrawn every <see cref="AimInterval"/> ticks. The policy
/// has no cone of its own to widen, so this is the one knob that exists only
/// for it; see <see cref="HybridBot"/>, "difficulty inside the window".
/// </param>
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
    BotLeadMode LeadMode,
    int ReflexTicks = 0,
    double ReflexMissChance = 0,
    double PolicyAimErrorDeg = 0,
    bool HumanAim = false)
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
            LeadMode: BotLeadMode.Full,

            // The top tier keeps the perfect reflexes it always had; the rest
            // see a rocket late (9 ticks is 300ms) and sometimes not at all.
            ReflexTicks: (int)Math.Floor(Lerp(skill, 9, 6, 3, 0) + 0.5),
            ReflexMissChance: Lerp(skill, 0.6, 0.35, 0.15, 0),

            // Zero at the top, like every other knob the policy is handicapped
            // by: tier 5 runs the checkpoint exactly as it was trained.
            PolicyAimErrorDeg: Lerp(skill, 12, 6, 2, 0));
    }

    /// <summary>Whether <paramref name="tier"/> is one of the server's difficulty tiers.</summary>
    /// <remarks>
    /// <see cref="ForTier"/> clamps, so a caller that would rather refuse an
    /// out-of-range tier than quietly run the nearest one asks this first.
    /// </remarks>
    public static bool IsTier(int tier) => tier is >= BotConstants.TierMin and <= BotConstants.TierMax;

    /// <summary>The skill each of the server's tiers 1-5 plays at.</summary>
    /// <remarks>
    /// <b>Tuned so each step down costs about the same in a match</b>, measured by
    /// <c>BENCH_TIERS</c> as the opponent's share of a match's deaths against tier 5
    /// (<c>docs/TRAPS-BOTS.md</c>, the difficulty chain). Evenly spaced skills
    /// (0.15, 0.35, 0.55, 0.75) are not evenly spaced in play: the bottom two
    /// steps cost twice what the top two did.
    /// <para>
    /// <b>Shifted up one place on 2026-10-10, when tiers 1-4 began to aim like
    /// people</b> (<see cref="HumanAim"/>, D3). Human aim costs about one step, so
    /// the old skills under it doubled the 4-to-5 step. Each lower tier now plays
    /// the skill the tier above it used to, plus human aim: tier 4 is tier 5's
    /// skill aiming like a person, and the ladder keeps its even steps.
    /// <c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, D3, has both ladders.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<double> TierSkills => [0.30, 0.42, 0.67, 0.95, 0.95];

    /// <summary>The server's 1-5 difficulty as a skill, from <see cref="TierSkills"/>.</summary>
    /// <remarks>
    /// Tiers 1-4 aim like people (<see cref="HumanAim"/>); tier 5 does not, by the
    /// user's call on 2026-10-10: "Max" is the best possible strength.
    /// </remarks>
    public static BotSkill ForTier(int tier)
    {
        var clamped = Math.Clamp(tier, BotConstants.TierMin, BotConstants.TierMax);

        return For(TierSkills[clamped - BotConstants.TierMin]) with { HumanAim = clamped < BotConstants.TierMax };
    }

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
