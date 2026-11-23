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

    /// <summary>The map a duel server opens on when none is given.</summary>
    public const string Map = "duel_01";

    /// <summary>
    /// Points a duel server at the shipped <c>maps/duel/</c> and <c>nav/duel/</c>,
    /// and at <see cref="Map"/>, wherever nothing else was chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written into the options, once, by the <see cref="GameServer"/>
    /// constructor</b>, unlike the settings above: the directories are read in a
    /// dozen places, and the constructor is the one funnel every server passes
    /// through before any of them runs.
    /// </para>
    /// <para>
    /// <b>Three conditions on each</b>: a duel server, a setting nobody gave
    /// (<see cref="ServerOptions.ExplicitSettings"/>), and a value still at the
    /// built-in default — so a directory set in code, as the tests do, is left
    /// alone, and the subdirectory is only taken when it exists.
    /// </para>
    /// </remarks>
    public static void ApplyPaths(ServerOptions options)
    {
        if (!options.Duel)
            return;

        var maps = Path.Combine(MapDirectory.Resolve(), "duel");

        if (Applies(options, nameof(ServerOptions.MapDirectory))
            && options.MapDirectory == MapDirectory.Resolve()
            && Directory.Exists(maps))
        {
            options.MapDirectory = maps;
        }

        if (options.NavCacheDirectory.Length > 0
            && Applies(options, nameof(ServerOptions.NavCacheDirectory))
            && options.NavCacheDirectory == MapDirectory.ResolveNav()
            && Directory.Exists(Path.Combine(options.NavCacheDirectory, "duel")))
        {
            options.NavCacheDirectory = Path.Combine(options.NavCacheDirectory, "duel");
        }

        if (Applies(options, nameof(ServerOptions.Map))
            && !options.ExplicitSettings.Contains(nameof(ServerOptions.MapRotation))
            && options.MapRotation.Count == 0
            && options.Map == ServerOptions.DefaultMap
            && File.Exists(Path.Combine(options.MapDirectory, Map + ".png")))
        {
            options.Map = Map;
        }
    }

    private static bool Applies(ServerOptions options, string property) =>
        options.Duel && !options.ExplicitSettings.Contains(property);
}
