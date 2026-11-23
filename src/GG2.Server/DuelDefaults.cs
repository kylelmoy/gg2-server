using GG2.Bots;

namespace GG2.Server;

/// <summary>
/// What <c>--duel</c> changes about settings nobody set: the ten duel nets and
/// the settings they were trained and scored under (<c>docs/DUEL.md</c>, "The
/// duel nets").
/// </summary>
/// <remarks>
/// <para>
/// <b>Defaults, not overrides.</b> Each applies only when the setting was not
/// given explicitly, by a config-file key or a flag
/// (<see cref="ServerOptions.ExplicitSettings"/>), so a duel server can still
/// run greedy decisions or a different handover. A server that is not a duel
/// server reads every setting as configured.
/// </para>
/// <para>
/// <b>Resolved where each setting is read, never written back</b>, for the
/// reason <see cref="BotBrain.Build"/> gives for the shipped bundle: the options
/// say what was configured, and the admin panel writes them to the file.
/// </para>
/// </remarks>
public static class DuelDefaults
{
    /// <summary>The duel nets drive the whole cell: past a cell's 1,082 px diagonal.</summary>
    public const int HandoverRange = 100_000;

    /// <summary>
    /// The handover rule a bot uses: <see cref="HandoverRule.Distance"/> on a
    /// duel server, which with <see cref="HandoverRange"/> means the net always drives.
    /// </summary>
    public static HandoverRule Rule(ServerOptions options) =>
        Applies(options, nameof(ServerOptions.BotHandoverRule)) ? HandoverRule.Distance : options.BotHandoverRule;

    /// <summary>How near an opponent must be for the handover.</summary>
    public static int Range(ServerOptions options) =>
        Applies(options, nameof(ServerOptions.BotHandoverRange)) ? HandoverRange : options.BotHandoverRange;

    /// <summary>
    /// Whether policy decisions are drawn from each head's softmax: on for a duel
    /// server, because the duel nets were trained and scored sampled.
    /// </summary>
    public static bool Sampled(ServerOptions options) =>
        Applies(options, nameof(ServerOptions.BotPolicySampled)) || options.BotPolicySampled;

    /// <summary>
    /// Team Deathmatch spawn protection, in seconds: none on a duel server, where
    /// a respawn lands in a one-on-one cell with the bot that just won.
    /// </summary>
    public static int TdmInvulnerabilitySeconds(ServerOptions options) =>
        Applies(options, nameof(ServerOptions.TdmInvulnerabilitySeconds)) ? 0 : options.TdmInvulnerabilitySeconds;

    private static bool Applies(ServerOptions options, string property) =>
        options.Duel && !options.ExplicitSettings.Contains(property);
}
