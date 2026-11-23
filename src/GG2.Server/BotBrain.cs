using System.Runtime.InteropServices;
using System.Text;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

/// <summary>
/// The one place a <see cref="BotBrainSpec"/> is built out of the five settings
/// that describe one.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>ONE PLACE BUILDS THE BRAIN, at the end, from inputs collected first.</b>
/// Collecting first and building once keeps the flags order-independent, and it
/// is what lets a config file and the command line reach the same reconcile instead
/// of growing two copies of it — CONTRIBUTING.md's "three places" rule one level up,
/// the same argument <see cref="ControlCommands"/> makes for the operator verbs.
/// </para>
/// <para>
/// ⚠️ <b>This is why <see cref="ServerOptions.PolicyPaths"/> and the settings
/// beside it exist as options at all.</b> A brain holds a loaded
/// <c>SimPolicyNetwork</c> — megabytes of weights with no textual form — so it
/// cannot be written back to a config file and cannot round-trip. Its inputs
/// can, so the inputs are the configuration and the brain is derived.
/// </para>
/// <para>
/// <b>A server always runs the policy brain.</b> With no <c>--policy</c> it
/// loads the shipped bundle (<see cref="MapDirectory.ShippedCheckpoints"/>) and,
/// with no <c>--bot-class-policy</c> either, the bundle's class assignment. Only
/// tooling's control arm sets <see cref="ServerOptions.BotMode"/> to scripted.
/// </para>
/// </remarks>
public static class BotBrain
{
    /// <summary>
    /// Reconciles the brain settings and writes the result into
    /// <see cref="BotPopulationOptions.Brain"/>.
    /// </summary>
    /// <param name="options">The options to reconcile, changed in place.</param>
    /// <param name="report">
    /// Where to print the checkpoint's stamp line, or null to print nothing.
    /// <b>Passed in rather than assumed to be the console</b>: the host prints it
    /// before any log sink exists, and a server restarted from the panel prints
    /// it into the log an operator is actually reading.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A checkpoint was given to the scripted control arm, which never consults
    /// one. ⛔ <b>Refused rather than ignored</b>:
    /// a checkpoint loaded, printed and never used is a server saying it is doing
    /// something it is not.
    /// </exception>
    /// <param name="loadPolicy">
    /// Opens a checkpoint, or null for <see cref="SimPolicyNetwork.Load(string)"/>.
    /// A room host passes its <see cref="SharedPolicies"/> so every room holds the
    /// same weights.
    /// </param>
    public static void Build(
        ServerOptions options, Action<string>? report = null, Func<string, SimPolicyNetwork>? loadPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        CheckSettings(options);

        var mode = options.BotMode;
        var policyPaths = options.PolicyPaths;
        var classPolicies = options.BotClassPolicies;

        // No checkpoint configured: the shipped bundle. Resolved here and NOT
        // written back, because the options say what was CONFIGURED and the panel
        // writes them to the file — a resolved absolute path saved there would pin
        // this machine's install directory into the config.
        if (mode.UsesPolicy() && policyPaths.Count == 0)
        {
            // A duel server's bundle is the ten duel nets (DuelDefaults).
            policyPaths = options.Duel ? MapDirectory.ResolveDuelCheckpoints() : MapDirectory.ResolveCheckpoints();
            var bundleClasses = options.Duel ? MapDirectory.ShippedDuelClassPolicies : MapDirectory.ShippedClassPolicies;

            // The bundle's assignment, with the operator's entries applied OVER it
            // class by class: `Soldier=scripted` alone hands the soldier back to the
            // scripted layer and leaves the other eight nets driving. Replacing the
            // list instead would load nine nets that drive nothing.
            var overridden = ParseClassPolicies(classPolicies).Keys.ToHashSet();

            classPolicies =
            [
                .. bundleClasses.Where(entry =>
                    !overridden.Contains(Enum.Parse<PlayerClass>(entry[..entry.IndexOf('=')]))),
                .. classPolicies,
            ];

            if (policyPaths.Count == 0)
            {
                // ⚠️ Said out loud rather than refused: a build without the
                // checkpoints still runs a complete bot, just not the trained one.
                report?.Invoke(
                    "WARNING: no checkpoints/ directory with the shipped policy nets was found, "
                    + "so the bots run the scripted layer alone.");
            }
        }

        var tiers = new List<MicroControllerSpec>();

        // The operator's per-class assignments, parsed and validated before a
        // single checkpoint is opened so a typo costs no file I/O and, more to the
        // point, so the error names the setting rather than a load failure.
        Dictionary<PlayerClass, string> assigned = policyPaths.Count == 0 ? new() : ParseClassPolicies(classPolicies);

        if (mode.UsesPolicy())
        {
            var labels = new List<string>();

            foreach (var policyPath in policyPaths)
            {
                var label = Path.GetFileNameWithoutExtension(policyPath);

                labels.Add(label);

                tiers.Add(SimTier(policyPath, label, assigned, report, loadPolicy ?? SimPolicyNetwork.Load)
                    with { Sampled = DuelDefaults.Sampled(options) });
            }

            // ONE line for the whole set, not one per net: ten lines buried the
            // startup log.
            if (tiers.Count > 0)
            {
                var driven = tiers.Cast<SimPolicyMicroSpec>().SelectMany(tier => tier.Drives).Order().ToArray();
                var folders = policyPaths.Select(Path.GetDirectoryName).Distinct().ToArray();

                report?.Invoke(
                    $"bot policy: {tiers.Count} net(s)"
                    + (folders.Length == 1 ? $" from {folders[0]}" : "")
                    + $", driving {Classes(driven)}");
            }

            // ⛔ AFTER the loop, so the message can name the labels that DO exist.
            // A typo here is the failure mode this whole setting invites: it looks
            // like a working server whose bots are quietly all scripted.
            foreach (var (playerClass, target) in assigned)
            {
                if (target is Scripted || labels.Contains(target, StringComparer.OrdinalIgnoreCase))
                    continue;

                throw new ArgumentException(
                    $"bot class policy '{playerClass}={target}': no checkpoint is loaded under "
                    + $"that name. A name is a checkpoint's file name without its extension — "
                    + $"loaded: {(labels.Count == 0 ? "none" : string.Join(", ", labels))} — or "
                    + $"'{Scripted}' to leave the class to the scripted layer.");
            }
        }

        // ⚠️ CLEARED when there is no controller, not left alone. Reconciling twice
        // — which a restart does — must be idempotent, and a brain left over from a
        // previous mode is a server running a controller its own settings say it is
        // not.
        options.Bots = options.Bots with
        {
            Brain = tiers.Count == 0
                ? null
                : new BotBrainSpec
                {
                    Tiers = tiers,
                    Rule = DuelDefaults.Rule(options),
                    Range = DuelDefaults.Range(options),

                    // ⛔ The server's default is the OPPOSITE of BotBrainSpec's,
                    // deliberately — see ServerOptions.BotEngagesBotsToo.
                    Engage = options.BotEngagesBotsToo
                        ? EngageOpponents.Anyone
                        : EngageOpponents.HumansOnly,

                    // A debugging aid, and the only setting here that changes
                    // nothing a bot decides — see ServerOptions.
                    Bubbles = options.BotHandoverBubbles,
                },
        };
    }

    /// <summary>
    /// A gg2-sim checkpoint's tier: <see cref="SimPolicyMicroSpec"/>, driving exactly
    /// the classes <c>[Bots] ClassPolicy</c> assigns to its label.
    /// </summary>
    /// <remarks>
    /// <b>A <c>.gg2p</c> names no classes and carries no stamp</b>, so there is
    /// no file set to fill in from and nothing to compare against
    /// <see cref="SimulationStamp.Uuid"/>. The stamp line says both rather than
    /// printing a match, and a tier nobody assigned a class is warned about — it
    /// is the one way to load one that drives nothing by accident. Every class it
    /// drives is an explicit assignment, which no other policy tier can then also
    /// claim, so two policy tiers cannot overlap.
    /// </remarks>
    private static SimPolicyMicroSpec SimTier(
        string policyPath,
        string label,
        Dictionary<PlayerClass, string> assigned,
        Action<string>? report,
        Func<string, SimPolicyNetwork> loadPolicy)
    {
        var network = loadPolicy(policyPath);

        var drives = assigned
            .Where(entry => string.Equals(entry.Value, label, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .Order()
            .ToArray();

        if (drives.Length == 0)
        {
            report?.Invoke(
                $"WARNING: {label} drives no classes at all, so it is loaded and never consulted. "
                + $"A .gg2p names none of its own; assign them with Bots.ClassPolicy, e.g. "
                + $"--bot-class-policy Soldier={label}.");
        }

        return new SimPolicyMicroSpec(network, drives, label);
    }

    /// <summary>
    /// The refusals <see cref="Build"/> makes from the settings alone, with no
    /// checkpoint opened.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Called from <see cref="ServerOptions.Validate"/>, and that is the
    /// point.</b> The panel's save, the fleet renderer and the pushed-config
    /// watcher all decide "this file will start" with <c>Validate</c> alone, so a
    /// refusal only <see cref="Build"/> made would let the panel write a mode
    /// beside a <c>Policy</c> key it cannot use, and "Save &amp; restart" would
    /// then end the process with exit 2. Nothing here may touch the
    /// disk: the renderer validates on a machine where the checkpoint paths do not
    /// exist.
    /// </remarks>
    public static void CheckSettings(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mode = options.BotMode;
        var policyPaths = options.PolicyPaths;

        // Only gg2-sim nets are loaded. Refused here, from the name alone, so the
        // panel and the fleet renderer refuse it before a restart does.
        foreach (var policyPath in policyPaths)
        {
            if (!policyPath.EndsWith(".gg2p", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"policy checkpoint '{policyPath}': only gg2-sim .gg2p nets are supported.");
            }
        }

        if (!mode.UsesPolicy() && policyPaths.Count > 0)
        {
            throw new ArgumentException(
                "a policy checkpoint was given to the scripted control arm, which never "
                + "consults one; drop the checkpoint.");
        }

        var assigned = ParseClassPolicies(options.BotClassPolicies);

        if (!mode.UsesPolicy() && assigned.Count > 0)
        {
            // Same argument as the checkpoint-with-no-policy-mode refusal above: a
            // setting that is read, validated and then cannot possibly apply is a
            // server saying it is doing something it is not.
            throw new ArgumentException(
                "bot class policies were given to the scripted control arm, which runs no "
                + "policy; drop them.");
        }
    }

    /// <summary>
    /// The reserved target that means "no controller — leave it to the scripted
    /// layer".
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is reserved, so a checkpoint file called <c>scripted.gg2p</c> cannot
    /// be named per class.</b> Said out loud rather than left to be discovered: the
    /// failure would be a class silently going scripted when the operator asked for
    /// weights, which is the one outcome this whole setting exists to make visible.
    /// Rename the file.
    /// </remarks>
    public const string Scripted = "scripted";

    /// <summary>
    /// <c>Class=target</c> entries into a map, refusing what cannot be applied.
    /// </summary>
    /// <remarks>
    /// <b>Class names only, never a number.</b> <c>--class-limit</c> takes a bare
    /// integer as "every class", and a reader who assumed the same shape here would
    /// write something that parses somewhere else. There is no bare form.
    /// <para>
    /// ⛔ <b>A class named twice is refused, not last-wins.</b> Two lines assigning
    /// one class to two different tiers have no defensible resolution, and
    /// last-wins would make the file's line ORDER decide which weights play a
    /// class — the exact property the disjointness of the shipped bundle exists to
    /// avoid needing.
    /// </para>
    /// </remarks>
    private static Dictionary<PlayerClass, string> ParseClassPolicies(
        IReadOnlyList<string> entries)
    {
        var assigned = new Dictionary<PlayerClass, string>();

        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw new ArgumentException(
                    $"bot class policy '{entry}': expected Class=name, where name is a "
                    + $"checkpoint's file name without its extension or '{Scripted}'.");
            }

            var name = entry[..separator].Trim();
            var target = entry[(separator + 1)..].Trim();

            // Quote is the ceiling, not Pyro as in ParseClasses: the fill never
            // draws a Quote bot, but the duel server makes one for a Quote human,
            // and gg2-sim trains a Quote net for it. Enum.TryParse also accepts
            // any number, hence the bound at all.
            if (!Enum.TryParse(name, ignoreCase: true, out PlayerClass playerClass)
                || playerClass > PlayerClass.Quote)
            {
                throw new ArgumentException(
                    $"bot class policy '{entry}': '{name}' is not a class a bot can be. "
                    + "Scout, Pyro, Soldier, Heavy, Demoman, Medic, Engineer, Spy, Sniper or Quote.");
            }

            if (assigned.TryGetValue(playerClass, out var already))
            {
                throw new ArgumentException(
                    $"bot class policy '{entry}': {playerClass} is already assigned to "
                    + $"'{already}'. One class, one controller — assigning it twice would make "
                    + "the order these were written in decide which weights play it.");
            }

            assigned[playerClass] = string.Equals(target, Scripted, StringComparison.OrdinalIgnoreCase)
                ? Scripted
                : target;
        }

        return assigned;
    }

    /// <summary>
    /// A checkpoint's class set, for the stamp line.
    /// </summary>
    /// <remarks>
    /// Comma-separated. <c>ReadOnlySpan</c> in, so no LINQ and no copy.
    /// </remarks>
    private static string Classes(ReadOnlySpan<PlayerClass> classes)
    {
        var text = new StringBuilder();

        foreach (var playerClass in classes)
        {
            if (text.Length > 0)
                text.Append(',');

            text.Append(playerClass);
        }

        return text.Length == 0 ? "nothing" : text.ToString();
    }
}
