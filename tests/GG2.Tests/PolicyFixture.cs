using GG2.Bots;

namespace GG2.Tests;

/// <summary>
/// The shipped gg2-sim nets the tests drive bots with.
/// </summary>
/// <remarks>
/// Real <c>checkpoints/</c> files, not synthetic ones: a test that loads a brain
/// through <c>BotBrain.Build</c> reads the same bytes a deployed server does. A
/// <c>.gg2p</c> names no classes of its own, so every tier built from one needs
/// a <c>Bots.ClassPolicy</c> assignment (<see cref="SoldierAssignment"/>) or it
/// drives nothing. Behavioural results read off these are the nets' own and
/// move when the nets are replaced; <c>SimPolicyTests</c> writes synthetic nets
/// where a test needs a fixed answer.
/// </remarks>
internal static class PolicyFixture
{
    private static string Directory => Path.Combine(ReferenceTree.RepositoryRoot, "checkpoints");

    /// <summary>The soldier net. Label <see cref="Label"/>.</summary>
    internal static string Checkpoint => Path.Combine(Directory, "ppo-warm-soldier-s1.gg2p");

    /// <summary>The scout net: a second file, for anything that needs two tiers.</summary>
    internal static string Second => Path.Combine(Directory, "ppo-finetune-scout-s1.gg2p");

    /// <summary>The name a checkpoint is configured by: its file name without the extension.</summary>
    internal static string Label => Path.GetFileNameWithoutExtension(Checkpoint);

    /// <summary><see cref="Second"/>'s label.</summary>
    internal static string SecondLabel => Path.GetFileNameWithoutExtension(Second);

    /// <summary>The <c>ClassPolicy</c> entry that makes <see cref="Checkpoint"/> drive soldiers.</summary>
    internal static string SoldierAssignment => "Soldier=" + Label;

    /// <summary>The <c>ClassPolicy</c> entry that makes <see cref="Second"/> drive scouts.</summary>
    internal static string ScoutAssignment => "Scout=" + SecondLabel;

    /// <summary><see cref="Checkpoint"/>, loaded.</summary>
    internal static SimPolicyNetwork Load() => SimPolicyNetwork.Load(Checkpoint);

    /// <summary>A soldier tier over <see cref="Checkpoint"/>, as <c>BotBrain</c> would build it.</summary>
    internal static SimPolicyMicroSpec Tier() =>
        new(Load(), [GG2.Protocol.PlayerClass.Soldier], Label);
}
