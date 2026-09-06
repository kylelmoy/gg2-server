using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The trained class set is a property of the CHECKPOINT, not of this build.
/// </summary>
/// <remarks>
/// <b>Landed 2026-09-06 at the <c>gg2-rl</c> lane's request</b>, replacing a
/// compile-time static. Their case, measured before it landed: three shipping
/// checkpoint families train three different sets, and under the static a
/// <c>control</c>-family file drives its untrained demoman (mirror 36.08% against
/// a 50% null) and refuses its trained sniper (89.08%) — with no stamp moving, no
/// handshake field disagreeing and no test failing.
/// <para>
/// ⛔ <b>Every fixture here is real <c>Policy.save</c> output off a
/// 12,000-iteration run.</b> The metadata is a pickled object array, so a
/// synthetic checkpoint would exercise a pickle this repository wrote rather than
/// one the trainer writes, and would pass whether or not the two agree.
/// </para>
/// <para>
/// ⚠️ <b>A differing set is a ROUTING fact, not a safety one.</b> Nothing here
/// refuses a checkpoint over its class set, and nothing should: a policy trained
/// on other classes is not invalid, it is differently applicable. That is why the
/// set is deliberately NOT on the <c>SIM_UUID</c> stamp surface.
/// </para>
/// </remarks>
public sealed class TrainedClassesTests
{
    /// <summary>The set is read off the file, including a non-prefix one.</summary>
    /// <remarks>
    /// <c>ENGINEER,SPY,PYRO</c> is indices 5, 7, 8 — non-prefix, non-contiguous,
    /// and a different LENGTH from the default four. ⛔ <b>That is why this is the
    /// fixture to assert the read against</b>: an implementation that takes "the
    /// first N", a fixed count or a contiguous range passes on the other two
    /// checkpoints and fails only here.
    /// </remarks>
    [Fact]
    public void ANonPrefixSetIsReadOffTheCheckpoint()
    {
        var network = PolicyNetwork.Load(PolicyFixture.EightFieldCheckpoint);

        Assert.Equal(
            [PlayerClass.Engineer, PlayerClass.Spy, PlayerClass.Pyro],
            network.TrainedClasses.ToArray());

        Assert.True(network.IsTrained(PlayerClass.Engineer));
        Assert.True(network.IsTrained(PlayerClass.Spy));
        Assert.True(network.IsTrained(PlayerClass.Pyro));

        // The default's own members, none of which this file trained — so a read
        // that silently fell back would fail here rather than look right.
        Assert.False(network.IsTrained(PlayerClass.Scout));
        Assert.False(network.IsTrained(PlayerClass.Soldier));
        Assert.False(network.IsTrained(PlayerClass.Demoman));
        Assert.False(network.IsTrained(PlayerClass.Heavy));
    }

    /// <summary>
    /// ⛔ The regression gate: the checkpoint the old static mis-gated.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they fail in opposite directions. Under the static
    /// this file's SNIPER — which it trained, and which mirrors at 89.08% — was
    /// refused, and its DEMOMAN — which it never trained — was driven. Either
    /// assertion alone would have missed one of the two.
    /// </remarks>
    [Fact]
    public void TheCheckpointTheStaticMisGatedIsNowGatedByItsOwnSet()
    {
        var network = PolicyNetwork.Load(PolicyFixture.SniperTrainedCheckpoint);
        var controller = new PolicyController(network);

        Assert.True(
            controller.Drives(PlayerClass.Sniper),
            "this checkpoint trained a sniper; the old static refused it");

        Assert.False(
            controller.Drives(PlayerClass.Demoman),
            "this checkpoint never trained a demoman; the old static drove it");
    }

    /// <summary>The no-op control: a checkpoint whose set equals the default.</summary>
    /// <remarks>
    /// <b>If this one moves, the change is wrong.</b> Reading the field must give
    /// exactly what assuming the default gave, for the family that was never
    /// mis-gated — otherwise the mechanism has moved something it was not supposed
    /// to and the regression gate above proves nothing on its own.
    /// </remarks>
    [Fact]
    public void ACheckpointWhoseSetEqualsTheDefaultBehavesExactlyAsBefore()
    {
        var network = PolicyNetwork.Load(PolicyFixture.DefaultSetCheckpoint);

        Assert.Equal(PolicyNetwork.DefaultTrainedClasses.ToArray(), network.TrainedClasses.ToArray());

        var controller = new PolicyController(network);

        foreach (var playerClass in Enum.GetValues<PlayerClass>())
        {
            Assert.Equal(
                PolicyNetwork.DefaultTrainedClasses.Contains(playerClass),
                controller.Drives(playerClass));
        }
    }

    /// <summary>A checkpoint written before the field existed falls back.</summary>
    /// <remarks>
    /// ⛔ <b>The shipped fixture is the case, not a contrivance</b> —
    /// <c>ppo_final.npz</c> has seven metadata fields, so it IS a
    /// pre-2026-09-06 checkpoint and this is the path every one of them takes.
    /// The fallback has to be today's default exactly, or the change breaks every
    /// checkpoint that predates it.
    /// </remarks>
    [Fact]
    public void ACheckpointWithNoClassSetFallsBackToTheDefault()
    {
        var meta = NpyFile.ReadStrings(
            NpyFile.ReadArchive(File.OpenRead(PolicyFixture.Checkpoint))["meta"]);

        // The premise: if the shipped fixture ever gains the field this test stops
        // covering the fallback, and should be pointed at something that has not.
        Assert.Equal(7, meta.Length);

        var network = PolicyFixture.Load();

        Assert.Equal(PolicyNetwork.DefaultTrainedClasses.ToArray(), network.TrainedClasses.ToArray());
    }

    /// <summary>
    /// Two controllers over two checkpoints disagree, which is the point.
    /// </summary>
    /// <remarks>
    /// <b>This is what <see cref="HybridBot"/>'s per-class tier eligibility was
    /// built for and could not exercise.</b> While <c>Drives</c> read a static,
    /// two <see cref="PolicyController"/> instances could not differ, so a tiered
    /// bot could never route a class to the checkpoint that trained it.
    /// </remarks>
    [Fact]
    public void TwoControllersOverTwoCheckpointsDisagreeAboutTheSameClass()
    {
        var sniperTrained = new PolicyController(PolicyNetwork.Load(PolicyFixture.SniperTrainedCheckpoint));
        var demomanTrained = new PolicyController(PolicyNetwork.Load(PolicyFixture.DefaultSetCheckpoint));

        Assert.True(sniperTrained.Drives(PlayerClass.Sniper));
        Assert.False(demomanTrained.Drives(PlayerClass.Sniper));

        Assert.False(sniperTrained.Drives(PlayerClass.Demoman));
        Assert.True(demomanTrained.Drives(PlayerClass.Demoman));
    }

    /// <summary>A class name this build does not have is refused as metadata.</summary>
    /// <remarks>
    /// ⚠️ <b><see cref="InvalidDataException"/>, not
    /// <see cref="InvalidOperationException"/>.</b> The nine classes are fixed by
    /// the protocol and do not grow, so a name outside them is a corrupt or
    /// mistaken field rather than a version skew — it belongs with the archive and
    /// metadata refusals, not with the stamp's.
    /// </remarks>
    [Fact]
    public void AClassNameThisBuildDoesNotHaveIsRefused()
    {
        var bytes = File.ReadAllBytes(PolicyFixture.EightFieldCheckpoint);
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        var at = text.IndexOf("ENGINEER,SPY,PYRO", StringComparison.Ordinal);

        Assert.True(at > 0, "the class set should be findable in the checkpoint");

        // Same length, so nothing else in the archive moves: ENGINEER -> ENGINEEX.
        bytes[at + 7] = (byte)'X';

        using var stream = new MemoryStream(bytes);

        var refusal = Assert.Throws<InvalidDataException>(() => PolicyNetwork.Load(stream));

        Assert.Contains("ENGINEEX", refusal.Message, StringComparison.Ordinal);
    }
}
