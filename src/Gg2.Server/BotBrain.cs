using System.Runtime.InteropServices;
using System.Text;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// The one place a <see cref="BotBrainSpec"/> is built out of the five settings
/// that describe one.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>ONE PLACE BUILDS THE BRAIN, at the end, from inputs collected first.</b>
/// The flag handlers used to write the brain directly, which made
/// <c>--search-threads</c> order-dependent: given before <c>--search-bots</c> it
/// was picked up, given after it had to re-write the option it had just found.
/// Collecting first and building once removes the ordering entirely, and it is
/// what lets a config file and the command line reach the same reconcile instead
/// of growing two copies of it — CLAUDE.md's "three places" rule one level up,
/// the same argument <see cref="ControlCommands"/> makes for the operator verbs.
/// </para>
/// <para>
/// ⚠️ <b>This is why <see cref="ServerOptions.BotMode"/> and the four settings
/// beside it exist as options at all.</b> A brain holds a loaded
/// <c>PolicyNetwork</c> — megabytes of weights with no textual form — so it
/// cannot be written back to a config file and cannot round-trip. Its inputs
/// can, so the inputs are the configuration and the brain is derived.
/// </para>
/// </remarks>
public static class BotBrain
{
    /// <summary>
    /// <c>--search-threads</c>'s default: a third of the machine's cores, 1..8.
    /// </summary>
    /// <remarks>
    /// A property rather than a constant because it reads
    /// <see cref="Environment.ProcessorCount"/>, and a <c>const</c> would bake
    /// the build machine's core count into every deployment.
    /// </remarks>
    public static int DefaultSearchThreads => Math.Clamp(Environment.ProcessorCount / 3, 1, 8);

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
    /// A mode that needs a checkpoint was given none, or a checkpoint was given
    /// to a mode that never consults one. ⛔ <b>Refused rather than ignored</b>:
    /// a checkpoint loaded, printed and never used is a server saying it is doing
    /// something it is not.
    /// </exception>
    public static void Build(ServerOptions options, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mode = options.BotMode;
        var policyPaths = options.PolicyPaths;

        if (mode.UsesPolicy() && policyPaths.Count == 0)
        {
            throw new ArgumentException(
                $"bot mode '{mode.ToString().ToLowerInvariant()}' drives bots with a trained "
                + "checkpoint and none was given; add --policy <path> (or a [Bots] Policy key), "
                + "or use bot mode scripted or search.");
        }

        if (!mode.UsesPolicy() && policyPaths.Count > 0)
        {
            throw new ArgumentException(
                $"a policy checkpoint was given but bot mode '{mode.ToString().ToLowerInvariant()}' "
                + "never consults one; use bot mode policy or tiered, or drop the checkpoint.");
        }

        var arbiterUnits = options.BotArbiterMicroseconds;

        // ⚠️ Tiered DEFAULTS its budget rather than refusing without one. Refusing
        // made the mode unreachable without a number nobody has tuned, which is a
        // worse default than a stated guess — see the constant, which says what
        // would show it is wrong.
        // ⚠️ EVERY mode with a controller gets a budget, not just tiered. The
        // reasoning that "one tier has nothing to choose between, so an arbiter is
        // pure cost" was wrong: an arbiter's second job is to GATE, and with one
        // tier it still returns -1 and drops the bot to the scripted layer once the
        // budget is spent. Without one, search mode let all twelve bots search on
        // the same tick — 12 x ~4,800 us against a 33,300 us tick, which is over
        // budget before the simulation runs at all.
        if (mode.NeedsArbiter())
            arbiterUnits ??= DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds;

        var tiers = new List<MicroControllerSpec>();

        if (mode.UsesSearch())
            tiers.Add(new SearchMicroSpec(new RolloutOptions(Threads: options.SearchThreads)));

        // The operator's per-class assignments, parsed and validated before a
        // single checkpoint is opened so a typo costs no file I/O and, more to the
        // point, so the error names the setting rather than a load failure.
        var assigned = ParseClassPolicies(options.BotClassPolicies);

        if (mode.UsesPolicy())
        {
            // Which tier already claimed each class, so an OVERLAP is named rather
            // than silently resolved by list order.
            var claimedBy = new Dictionary<PlayerClass, string>();
            var labels = new List<string>();

            foreach (var policyPath in policyPaths)
            {
                var policy = PolicyNetwork.Load(policyPath, options.PolicyIgnoreStamp);
                var label = Path.GetFileNameWithoutExtension(policyPath);

                labels.Add(label);

                // ⛔ THE RESOLUTION, and the ORDER of the two halves is the whole
                // design. An explicit assignment wins outright — including the
                // negative one, `Class=scripted`, which is why membership in
                // `assigned` and not its value is what excludes a class from the
                // file's own set. The file's set then fills in every class the
                // operator did not mention, which is what makes "the policies drive
                // their intended classes" the default rather than something that
                // has to be configured.
                //
                // ⚠️ A consequence worth knowing: naming a class explicitly also
                // resolves an overlap, because the class is then in `assigned` and
                // every other tier drops it. The overlap warning below can only
                // fire for classes nobody assigned.
                var drives = new List<PlayerClass>();

                foreach (var playerClass in policy.TrainedClasses)
                {
                    if (!assigned.ContainsKey(playerClass))
                        drives.Add(playerClass);
                }

                // ⚠️ Collected rather than printed here, so every warning lands AFTER
                // the stamp line it is about. A warning naming a tier the reader has
                // not been introduced to yet is one they have to re-read.
                var warnings = new List<string>();

                foreach (var (playerClass, target) in assigned)
                {
                    if (!string.Equals(target, label, StringComparison.OrdinalIgnoreCase))
                        continue;

                    drives.Add(playerClass);

                    // ⛔ Warned, not refused, and the warning cannot say which of two
                    // things happened. A checkpoint's one class set does two jobs —
                    // what the run trained and what its author intends it to drive —
                    // and the ship bundle narrows the second into the first, which
                    // destroys the record of what else was trained. So "the file does
                    // not list this class" means EITHER the run never saw it (driving
                    // it is untrained input arriving as a legal-looking observation)
                    // OR the author narrowed it out and the operator is putting back
                    // something real. Nothing in either repository can tell those
                    // apart; a person who knows the checkpoint can.
                    if (!policy.IsTrained(playerClass))
                    {
                        warnings.Add(
                            $"WARNING: {label} is configured to drive {playerClass}, which its "
                            + "own class set does not list. That is either a class it never "
                            + "trained on, or one a narrowed should-drive set dropped — the "
                            + "file cannot say which. docs/TRAPS-BOTS.md, 'a should-drive set lives "
                            + "in a trained-on field'.");
                    }
                }

                // ⛔ A tier driving NOTHING is loaded, reported and never consulted —
                // the thing the arbiter invariant below refuses in its own case. Not
                // refused here, because turning one tier off to see what the rest do
                // is a legitimate experiment and it should not need the file removed;
                // but it is megabytes of weights and a stamp check bought for no
                // behaviour, and an operator who did it by accident would otherwise
                // see a completely ordinary startup.
                if (drives.Count == 0)
                {
                    warnings.Add(
                        $"WARNING: {label} drives no classes at all, so it is loaded and never "
                        + "consulted. Every class its file claims was assigned elsewhere by "
                        + "Bots.ClassPolicy — drop the checkpoint if that was deliberate.");
                }

                // Stable and readable rather than insertion-ordered: the assigned
                // half above arrives in dictionary order, and a stamp line whose
                // class list reshuffles between runs is a diff nobody can read.
                drives.Sort((left, right) => ((int)left).CompareTo((int)right));

                // ⛔ ALWAYS reported, not only when overridden. A run whose stamps
                // agree says so; a run that bypassed the guard says THAT, loudly, on
                // the same line. A bypassed guard that leaves no trace in the output
                // is worse than no guard, because the next reader cannot tell it
                // happened.
                //
                // ⚠️ The CLASS SET is on the same line, and it has to be. Since
                // 2026-09-06 the set is a property of the FILE, and since 2026-09-07
                // a shipped checkpoint may name a should-drive set NARROWER than what
                // it trained on — so the only half either repo can check is what this
                // server will actually drive, and that is what is printed.
                report?.Invoke(
                    $"policy {Path.GetFileName(policyPath)}: trained against "
                    + $"{policy.SimulationUuid}, this build is {SimulationStamp.Uuid}"

                    // ⚠️ Reported, never compared. NavConstants left the gating
                    // surface on 2026-09-03 so a nav edit stops stranding checkpoints
                    // — but removing it from the GATE must not remove it from the
                    // RECORD, or "the bots play differently and the stamp matches"
                    // becomes unattributable.
                    + $", routing {SimulationStamp.RoutingUuid}"
                    + (policy.SimulationUuid == SimulationStamp.Uuid
                        ? " (match)"
                        : "  <-- STAMP MISMATCH, BYPASSED. Everything this policy decides was "
                          + "learned on a different simulation.")
                    + $", drives {Classes(CollectionsMarshal.AsSpan(drives))}"

                    // ⚠️ Only when the two differ, so the ordinary line stays short
                    // and the configured one is visibly the exception. Without it a
                    // ClassPolicy typo that silenced a tier would print a plausible
                    // line naming a smaller set and look like the file's own.
                    + (Classes(CollectionsMarshal.AsSpan(drives)) == Classes(policy.TrainedClasses)
                        ? string.Empty
                        : $" (CONFIGURED; the file says {Classes(policy.TrainedClasses)})"));

                foreach (var warning in warnings)
                    report?.Invoke(warning);

                foreach (var playerClass in drives)
                {
                    // ⛔ Reported, NOT refused. Two tiers overlapping is a legitimate
                    // fallback ladder — the arbiter drops to the later one under
                    // pressure — but it is also the only thing that makes the ORDER
                    // they were given in change behaviour, and the shipped bundle's
                    // whole claim is that its two sets are disjoint so order cannot.
                    // An operator who overlaps them by accident has to be told which
                    // class and which two files, because nothing downstream ever
                    // mentions it again.
                    if (claimedBy.TryGetValue(playerClass, out var owner))
                    {
                        report?.Invoke(
                            $"WARNING: {playerClass} is driven by both {owner} and {label}. "
                            + $"{owner} is the earlier tier and takes it whenever the arbiter "
                            + "admits it, so the order these were given in now changes what "
                            + "the server plays.");
                    }
                    else
                    {
                        claimedBy[playerClass] = label;
                    }
                }

                // ⚠️ AFTER the search, so tier 0 is the more expensive controller and
                // an arbiter under pressure drops to the policy before it drops to the
                // scripted layer. Ordering is the arbiter's whole input — HybridBot.
                //
                // ⛔ The set is passed EXPLICITLY even when it equals the file's, so
                // there is exactly one place a tier's class set is decided. A spec
                // that sometimes carried null and sometimes a set would have two, and
                // the null branch would be the one nothing configured ever exercises.
                tiers.Add(new PolicyMicroSpec(policy, label, [.. drives]));
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
        else if (assigned.Count > 0)
        {
            // Same argument as the checkpoint-with-no-policy-mode refusal above: a
            // setting that is read, validated and then cannot possibly apply is a
            // server saying it is doing something it is not.
            throw new ArgumentException(
                $"bot class policies were configured but bot mode "
                + $"'{mode.ToString().ToLowerInvariant()}' runs no policy; use bot mode policy "
                + "or tiered, or drop them.");
        }

        // ⛔ Unreachable from the settings now — NeedsArbiter defaults the budget
        // for every mode with a controller — and kept as the invariant it always
        // was. If a fifth mode arrives with two tiers and no default, this is what
        // stops it shipping a controller that is configured, reported and never
        // consulted.
        //
        // ⚠️ "The rest are dead weight" is the WORST case, not the only one, since
        // several policy tiers became possible on 2026-09-07: with DISJOINT class
        // sets HybridBot.Choose picks the most capable ELIGIBLE tier, so an
        // arbiter-less pair would both drive, each on its own classes. The refusal
        // stays because nothing here checks disjointness, and the overlapping case
        // — the one this sentence describes — is still silent without it.
        if (tiers.Count > 1 && arbiterUnits is null)
        {
            throw new ArgumentException(
                $"{tiers.Count} bot controllers were configured but no arbiter budget; "
                + "without one the first tier always drives and the rest are dead weight");
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

                    // ⛔ The server's default is the OPPOSITE of BotBrainSpec's,
                    // deliberately — see ServerOptions.BotEngagesBotsToo.
                    Engage = options.BotEngagesBotsToo
                        ? EngageOpponents.Anyone
                        : EngageOpponents.HumansOnly,

                    // A debugging aid, and the only setting here that changes
                    // nothing a bot decides — see ServerOptions.
                    Bubbles = options.BotHandoverBubbles,

                    // ⛔ ONE arbiter for the whole server, shared by every bot. A
                    // per-bot one cannot see the coinciding decisions that make the
                    // tick tail, which is the only reason it exists.
                    Arbiter = arbiterUnits is { } units ? new DecisionQuotaArbiter(units) : null,
                },
        };
    }

    /// <summary>
    /// The reserved target that means "no controller — leave it to the scripted
    /// layer".
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is reserved, so a checkpoint file called <c>scripted.npz</c> cannot
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

            // ⚠️ The same class ceiling ParseClasses uses: PlayerClass runs past
            // Pyro into values no bot can be, and Enum.TryParse accepts them.
            if (!Enum.TryParse(name, ignoreCase: true, out PlayerClass playerClass)
                || playerClass > PlayerClass.Pyro)
            {
                throw new ArgumentException(
                    $"bot class policy '{entry}': '{name}' is not a class a bot can be. "
                    + "Scout, Pyro, Soldier, Heavy, Demoman, Medic, Engineer, Spy or Sniper.");
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
