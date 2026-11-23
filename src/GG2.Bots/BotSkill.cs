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
/// <see cref="HumanizeOptions.Tracking9"/>: lagging its target the way people do.
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
/// <param name="ReversalCooldownTicks">
/// How long, on ticks the policy drives, a bot's legs keep their direction before
/// a reversal passes — the turn-around time of <see cref="HumanizeOptions.ReversalCooldown"/>.
/// Applies only where <see cref="HumanAim"/> does, so tier 5 runs the net's keys exactly.
/// </param>
/// <param name="PolicyReflexTicks">
/// The policy's reaction to an enemy rocket, in ticks, fractional
/// (<see cref="RocketPerception"/>); NaN means "the same as <see cref="ReflexTicks"/>".
/// Read it through <see cref="PolicyReflex"/>.
/// </param>
/// <param name="HumanAimStrength">
/// Where <see cref="HumanAim"/> is set, the share of <see cref="HumanizedBot"/>'s aim
/// lag that reaches the wire: 1 is all of it (tiers 1-4), and the duel ladder
/// fades it to nothing between levels 7 and 8 (<see cref="Human(double)"/>).
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
    bool HumanAim = false,
    int ReversalCooldownTicks = 0,
    double PolicyReflexTicks = double.NaN,
    double HumanAimStrength = 1)
{
    /// <summary>
    /// How late a policy sees an enemy rocket, in ticks: <see cref="PolicyReflexTicks"/>,
    /// or <see cref="ReflexTicks"/> where that is not set.
    /// </summary>
    /// <remarks>
    /// Fractional, because a rocket's line is exact at any delay
    /// (<see cref="RocketPerception"/>): a level between two tiers sees a rocket
    /// between their delays rather than at one of them.
    /// </remarks>
    public double PolicyReflex => double.IsNaN(PolicyReflexTicks) ? ReflexTicks : PolicyReflexTicks;

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
            PolicyAimErrorDeg: Lerp(skill, 12, 6, 2, 0),

            // 300ms at the bottom to 67ms at the top: a slower player turns round
            // later. Floored at 2 rather than 0 because the top skill is tier 4's
            // too, and tier 4 moves like a person; tier 5 skips the filter.
            ReversalCooldownTicks: (int)Math.Floor(Lerp(skill, 10, 6, 4, 2) + 0.5),

            // Shorter than the scripted dodge's at the bottom: a net's dodge is
            // its whole movement, and at ReflexTicks' 7-9 ticks a rocket with the
            // usual 5-8 ticks of warning was never seen in time, so tiers 1-2 were
            // hit more often than any person on the duel server (RocketPerception).
            PolicyReflexTicks: Lerp(skill, PolicyReflexAnchors[0], PolicyReflexAnchors[1], PolicyReflexAnchors[2], 0));
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
    /// <para>
    /// <b>Tiers 1-3 spread on 2026-10-11, when tiers 1-4 began to move like people</b>
    /// (<see cref="HumanizeOptions.Tracking7"/>, D10). Tier 4 already plays the top
    /// skill, so the legs' cost widened the 4-to-5 step and no skill could close it;
    /// tiers 1-3 were spaced further apart (from 0.30, 0.42, 0.67) so every step is
    /// within half a step of the others. Easy got easier.
    /// </para>
    /// <para>
    /// <b>D12 replaced those legs with a reversal cooldown</b>
    /// (<see cref="HumanizeOptions.Tracking9"/>, <see cref="ReversalCooldownTicks"/>),
    /// because D10's averaging made a bot run a second or more past its opponent.
    /// <c>docs/TRAPS-BOTS.md</c>, the difficulty chain, has the ladder it measured.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<double> TierSkills => [0.20, 0.37, 0.61, 0.95, 0.95];

    /// <summary>The server's 1-5 difficulty as a skill, from <see cref="TierSkills"/>.</summary>
    /// <remarks>
    /// Tiers 1-4 aim like people (<see cref="HumanAim"/>); tier 5 does not, by the
    /// user's call on 2026-10-10: "Max" is the best possible strength.
    /// </remarks>
    public static BotSkill ForTier(int tier)
    {
        var clamped = Math.Clamp(tier, BotConstants.TierMin, BotConstants.TierMax);

        return For(TierSkills[clamped - BotConstants.TierMin]).Human(clamped < BotConstants.TierMax);
    }

    /// <summary>
    /// The shortest reaction to a projectile a bot that plays like a person has:
    /// 3 ticks, 100ms.
    /// </summary>
    /// <remarks>
    /// <b>Without it tier 4 dodged like tier 5.</b> Tier 4 plays the top skill, where
    /// <see cref="ReflexTicks"/> is 0, so the one tier meant to be the best PERSON had
    /// the reflexes of no person at all. People on the duel server's soldier
    /// recordings react from about five ticks (<see cref="RocketPerception"/>); a
    /// policy decides every three on top of this, so 3 lands a fast human's median.
    /// </remarks>
    public const int HumanReflexFloorTicks = 3;

    /// <summary>The policy's rocket reaction at skills 0.15, 0.45 and 0.75, in ticks; 0 at 0.95.</summary>
    /// <remarks>
    /// <b>Fitted, by <c>BENCH_DODGE</c>, to the duel server's people.</b> At 5-8 ticks of
    /// warning they took 0.19 and 0.56 of the rockets that were going to hit them;
    /// these put tier 1 at 0.58, tier 4 at 0.24 and tier 5 at 0.18
    /// (<c>docs/TRAPS-BOTS.md</c>, "The policy's rocket dodge"). The share is steep
    /// in the delay — about 0.29 at 4 ticks and 0.55 at 5 — so a change of a few
    /// tenths here moves a tier a long way.
    /// </remarks>
    internal static ReadOnlySpan<double> PolicyReflexAnchors => [5.1, 4.3, 3.8];

    /// <summary>
    /// This skill with <see cref="HumanAim"/> set as <paramref name="human"/> says, and
    /// a human's reflex floor (<see cref="HumanReflexFloorTicks"/>) when it is set.
    /// </summary>
    /// <remarks>The one way a ladder (tiers, duel levels) marks the levels that play like people.</remarks>
    public BotSkill Human(bool human) => Human(human ? 1.0 : 0.0);

    /// <summary>
    /// This skill pushed past <see cref="Lerp"/>'s floor by <paramref name="depth"/>,
    /// 0 (unchanged) to 2: the duel ladder's Novice levels.
    /// </summary>
    /// <remarks>
    /// <b>Why a separate knob</b>: every ramp in <see cref="For"/> is flat below
    /// skill 0.15, so the bottom of a ladder cannot be made weaker by skill. These
    /// are the knobs that reach a bot whose net always drives (the duel server's):
    /// <see cref="HybridBot"/>'s handicap (the delay before the first shot, the held
    /// aim error, the hold-fire roll), the turn-around and the rocket perception.
    /// At depth 1 the bot holds fire half the time and aims up to 18 degrees off; at
    /// depth 2, 70% and 36 degrees, and it waits two and a half seconds before its
    /// first shot. The aim error and hold-fire grow faster than linearly because a
    /// linear ramp flattened between depths 1.5 and 2. The net's movement is
    /// otherwise untouched. Applied after <see cref="Human(double)"/>, so the
    /// turn-around it lengthens is the human one.
    /// <para>
    /// Fitted by <c>BENCH_DUEL_LADDER=160 BENCH_DUEL_OPPONENT=3</c> (1,280 duels a
    /// level, against the old bottom level): depth 1 scores 0.336 and depth 2 0.167
    /// (0.336 and 0.168 on <c>BENCH_SEED_BASE=500000</c>), steps of 0.75 and 0.92 in
    /// log-odds against the ladder's 0.5-0.7 above. Bot against bot; what would show
    /// it wrong is a Novice player still losing most duels at level 1.
    /// </para>
    /// </remarks>
    public BotSkill Novice(double depth)
    {
        if (depth <= 0)
            return this;

        var d = Math.Min(2.0, depth);

        return this with
        {
            FireDelayTicks = FireDelayTicks + (int)Math.Floor((15 * d) + 0.5),
            PolicyAimErrorDeg = PolicyAimErrorDeg + (6 * d * d),
            HoldFireChance = Math.Min(0.9, HoldFireChance + (0.025 * d * (5 + d))),
            ReversalCooldownTicks = ReversalCooldownTicks + (int)Math.Floor((3 * d) + 0.5),
            PolicyReflexTicks = PolicyReflex + (1.5 * d),
            ReflexMissChance = Math.Min(0.9, ReflexMissChance + (0.1 * d)),
        };
    }

    /// <summary>
    /// This skill played that much like a person, from 0 (not at all: tier 5) to 1
    /// (fully: tiers 1-4).
    /// </summary>
    /// <remarks>
    /// <b>What lets the duel ladder's top level be a slope rather than a cliff.</b>
    /// Tier 4 is tier 5's skill played like a person, so between them only these
    /// three differ, and each scales with <paramref name="strength"/>: the reflex
    /// floor (<see cref="HumanReflexFloorTicks"/>), the turn-around
    /// (<see cref="ReversalCooldownTicks"/>) and the share of
    /// <see cref="HumanizedBot"/>'s aim lag that reaches the wire
    /// (<see cref="HumanAimStrength"/>). At 1 this is exactly tiers 1-4; at 0 or
    /// below, exactly tier 5.
    /// </remarks>
    public BotSkill Human(double strength)
    {
        if (strength <= 0)
            return this with { HumanAim = false };

        var s = Math.Min(1.0, strength);
        var floor = HumanReflexFloorTicks * s;

        return this with
        {
            HumanAim = true,
            HumanAimStrength = s,
            ReflexTicks = Math.Max(ReflexTicks, (int)Math.Floor(floor + 0.5)),
            PolicyReflexTicks = Math.Max(PolicyReflex, floor),
            ReversalCooldownTicks = (int)Math.Floor((ReversalCooldownTicks * s) + 0.5),
        };
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
