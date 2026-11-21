using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// How late, and how reliably, a policy sees an enemy rocket: the difficulty's
/// share of the net's dodge.
/// </summary>
/// <remarks>
/// <b>A net dodges from its observation, so this is where its reaction time has
/// to live.</b> Neither observation layout shows an enemy's aim, so the rocket
/// slots are the only warning a net gets, and with them exact every decision its
/// dodge has no reaction time at all. Measured on the duel server's soldier
/// recordings (<c>tools/dodgescan</c>): a policy bot changed its keys within two
/// ticks of a rocket appearing 39-58% of the time against a person's 14-16%, which
/// is a person's ordinary key-change rate; people visibly react from about five
/// to eight ticks. Every ladder level dodged the same, because nothing here was
/// applied.
/// <para>
/// <b>Two effects, both from <see cref="BotSkill"/>'s reflex knobs</b>, so the net
/// and the scripted dodge (<see cref="BotReflex.RocketDodge"/>) share one reaction:
/// </para>
/// <list type="bullet">
/// <item><b>Noticed late or never</b>: an enemy rocket is left out of the slots
/// until <see cref="BotReflex.Noticed"/> passes it, exactly as the scripted dodge
/// ignores it. An empty slot is "no rocket", which a net has seen in every
/// rollout, so a hidden rocket is never out of distribution.</item>
/// <item><b>Seen where it was</b>: once noticed, it is written
/// <see cref="LagTicks"/> ticks back along its own line. A rocket flies straight at
/// constant speed with no gravity, so the rewind is exact, and the net reacts to an
/// old picture rather than to a rocket appearing in front of it.</item>
/// </list>
/// <para>
/// Only enemy rockets. The net's own rockets steer its rocket jumps and stay
/// exact; mines, flares and blades in the team layout are not touched.
/// </para>
/// <para>
/// <b><see cref="None"/> is the trained observation</b> and what every caller that
/// does not ask gets — tier 5, the policy fixtures, <c>eval</c> — so it moves no
/// pin.
/// </para>
/// </remarks>
/// <param name="LagTicks">How long a rocket is hidden, and how far back it is shown: <see cref="BotSkill.ReflexTicks"/>.</param>
/// <param name="MissChance">The chance a rocket is never shown: <see cref="BotSkill.ReflexMissChance"/>.</param>
public readonly record struct RocketPerception(int LagTicks, double MissChance)
{
    /// <summary>Every rocket, exactly where it is.</summary>
    public static RocketPerception None => default;

    /// <summary>The perception a bot playing at <paramref name="skill"/> has.</summary>
    public static RocketPerception Of(in BotSkill skill) => new(skill.ReflexTicks, skill.ReflexMissChance);

    /// <summary>Whether this changes anything at all.</summary>
    public bool IsNone => LagTicks <= 0 && MissChance <= 0;

    /// <summary>Whether <paramref name="projectile"/> is one this perception applies to.</summary>
    internal static bool Applies(in ProjectileState projectile, Team viewer) =>
        projectile.Kind == ProjectileKind.Rocket && projectile.Team != viewer;

    /// <summary>Whether the viewer in <paramref name="slot"/> sees this enemy rocket at all.</summary>
    internal bool Sees(in WorldView world, in ProjectileState rocket, ProjectileHandle handle, int slot) =>
        IsNone || BotReflex.Noticed(in world, in rocket, handle, slot, LagTicks, MissChance);

    /// <summary>Where the viewer sees this enemy rocket.</summary>
    internal (double X, double Y) Position(in ProjectileState rocket) =>
        LagTicks <= 0
            ? (rocket.X, rocket.Y)
            : (rocket.X - (rocket.Motion.HSpeed * LagTicks), rocket.Y - (rocket.Motion.VSpeed * LagTicks));
}
