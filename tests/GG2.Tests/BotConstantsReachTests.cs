using Xunit;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// <c>BotConstants</c> is on the routing surface, and this is the evidence.
/// </summary>
/// <remarks>
/// <b>The claim being kept true:</b> the <c>BOT_*</c> block changes which states
/// a policy is asked about and when it is handed the wheel, never what an action
/// does — so a bot-tuning edit must not strand a checkpoint.
/// <see cref="GG2.Simulation.SimulationStamp"/>'s <c>RoutingSources</c> acts on
/// that; this asserts it.
/// <para>
/// ⛔ <b>It has to be a SOURCE scan, and that is not laziness.</b> A
/// <c>const</c> is inlined by the compiler, so a reference to
/// <c>BotConstants.EngageTicks</c> leaves <em>nothing</em> in the IL — no
/// member reference, and not even an assembly reference if that is the only use.
/// Reflection, <c>Mono.Cecil</c> and an assembly-reference walk are all blind
/// here by construction. Reading the text is the only instrument that works.
/// </para>
/// <para>
/// <b>Deleting this test does not fail anything; it converts a checked claim
/// into an unfalsifiable one</b> — <c>CLAUDE.md</c>'s "a claim the repo makes
/// about itself". If the reach genuinely has to widen, move <c>BotConstants</c>
/// back to the gating surface in the same change and bump the stamp.
/// </para>
/// </remarks>
public sealed class BotConstantsReachTests(ITestOutputHelper output)
{
    /// <summary>
    /// Files that may read <c>BotConstants</c>: the scripted bot layer, the nav
    /// generator, and the stamp itself.
    /// </summary>
    /// <remarks>
    /// Whole projects rather than a file list, because a new file in
    /// <c>src/GG2.Bots/</c> reading the table is ordinary and a new file in
    /// <c>src/GG2.Simulation/</c> doing it is the thing to catch.
    /// </remarks>
    private static readonly string[] Allowed =
    [
        Path.Combine("src", "GG2.Bots"),
        Path.Combine("src", "GG2.Nav"),
        Path.Combine("src", "GG2.Protocol"),
    ];

    /// <summary>
    /// The inference path: what a policy sees, what its action does, and who
    /// hands it the wheel.
    /// </summary>
    /// <remarks>
    /// These live inside <c>src/GG2.Bots/</c>, which is allowed as a whole, so
    /// they are named individually — the project-level rule above is about where
    /// a bot constant may be read at all, and this is about the much narrower
    /// question the routing surface actually turns on.
    /// <para>
    /// <c>Handover</c> takes its range as a parameter rather than reading the
    /// table, which is what lets it stay on this list.
    /// </para>
    /// </remarks>
    private static readonly string[] Inference =
    [
        "ObservationWriter.cs",
        "ActionDecoder.cs",
        "PolicyNetwork.cs",
        "PolicyController.cs",
        "HybridBot.cs",
        "Handover.cs",
    ];

    private static string SourceRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "src");

                if (Directory.Exists(Path.Combine(candidate, "GG2.Simulation")))
                    return candidate;
            }

            throw new DirectoryNotFoundException("no src/ above the test binary");
        }
    }

    private static IEnumerable<(string Path, string Text)> Sources()
    {
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            // bin/ and obj/ carry generated copies that are not the source.
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (file, File.ReadAllText(file));
        }
    }

    /// <summary>
    /// <b>No bot constant reaches the simulation</b>, so none of them can change
    /// what an action does.
    /// </summary>
    /// <remarks>
    /// The load-bearing half. <c>SimulationStamp.cs</c> names the type to put it
    /// on a surface, which is the one mention that has to be allowed.
    /// </remarks>
    [Fact]
    public void TheSimulationReadsNoBotConstant()
    {
        var offenders = Sources()
            .Where(f => f.Path.Contains(Path.Combine("src", "GG2.Simulation"), StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f.Path) != "SimulationStamp.cs")
            .Where(f => f.Text.Contains("BotConstants", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "BotConstants is on the ROUTING surface because nothing in the simulation reads it, so a "
            + "bot-tuning edit cannot change what an action does. These now read it, which breaks that: "
            + string.Join(", ", offenders)
            + ". Either revert them or move BotConstants back to SimulationStamp.ConstantSources and "
            + "bump the pinned stamp.");
    }

    /// <summary>
    /// <b>No bot constant reaches the inference path either.</b>
    /// </summary>
    [Fact]
    public void TheInferencePathReadsNoBotConstant()
    {
        var offenders = Sources()
            .Where(f => Inference.Contains(Path.GetFileName(f.Path), StringComparer.Ordinal))
            .Where(f => f.Text.Contains("BotConstants.", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "a policy's observation, its action decode and the handover must not depend on the BOT_* "
            + "block, or a bot-tuning edit changes what a checkpoint's learned mapping means: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Everywhere else that reads it is a project the split accounts for.
    /// </summary>
    /// <remarks>
    /// The catch-all, so a reader appearing in <c>GG2.Server</c>, <c>GG2.Host</c>
    /// or <c>GG2.Env</c> is a decision rather than a drift. None of those would
    /// be <em>wrong</em> on its own — but each is a place where the "which states,
    /// not what an action does" argument has to be made again, and this is where
    /// somebody is asked to make it.
    /// </remarks>
    [Fact]
    public void EveryOtherReaderIsOnTheAllowedList()
    {
        var readers = Sources()
            .Where(f => f.Text.Contains("BotConstants.", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(SourceRoot, f.Path))
            .ToList();

        output.WriteLine($"{readers.Count} file(s) read BotConstants");

        var outside = readers
            .Where(r => !Allowed.Any(a =>
                Path.Combine("src", r).StartsWith(a + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            outside.Count == 0,
            "BotConstants is read outside the scripted bot layer, the nav generator and its own "
            + "project. That is not automatically wrong, but it is where the routing-surface argument "
            + "has to be made again: " + string.Join(", ", outside));
    }
}
