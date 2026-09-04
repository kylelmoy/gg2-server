using System.Globalization;
using Gg2.Bots;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The trained policy, and the gate that says this build runs it the way its
/// trainer does.
/// </summary>
/// <remarks>
/// <b>Phase 4's foundation.</b> `gg2-rl` handed over
/// <c>checkpoints/vlong_s0/ppo_final.npz</c>; it is checked in here rather than
/// read out of that repo's working tree, because a gate that depends on another
/// repo's uncommitted state is not a gate.
/// <para>
/// <b>Its stamp does not match this build's, and nothing here waits for that to
/// be fixed.</b> The moving-platform carry bumped <c>SIM_UUID</c>; the
/// checkpoint still says <c>7ff5940d97aa7bed</c> and always will. Every test
/// below treats it as a <b>numerics fixture</b> — real weights, a real numpy
/// oracle — rather than as a policy this project intends to run, and reaches it
/// through <see cref="PolicyFixture"/>, which is where that decision is written
/// down. The refusal itself is still gated, on a checkpoint minted for this build
/// and then corrupted, so the guard is tested without a live checkpoint existing.
/// </para>
/// <para>
/// <b>The fixture compares LOGITS against numpy, not just the argmax.</b> Two
/// implementations can agree on every head and still differ — a wrong head offset
/// produces a plausible ranking over the wrong actions, which is the failure
/// `gg2-rl` named when it built its own head-gathering. Comparing the raw output
/// block says whether the agreement is structural or lucky.
/// </para>
/// <para>
/// <b>The 64 observations are real states from a duel</b>, not random floats.
/// A forward pass agrees on noise whatever its offsets are; what has to agree is
/// the ranking in states the bot will meet. Across the fixture every head takes
/// several distinct values — 3, 2, 2, 3, 10 and 2 respectively — so a head stuck
/// at a constant would show.
/// </para>
/// </remarks>
public sealed class PolicyNetworkTests
{
    private static string Directory => PolicyFixture.Directory;

    private static string Checkpoint => PolicyFixture.Checkpoint;

    private static PolicyNetwork Load() => PolicyFixture.Load();

    /// <summary>
    /// The checkpoint describes itself, and it describes a simulation that is not
    /// this one.
    /// </summary>
    /// <remarks>
    /// <b>The stamp assertion is the falsification condition for
    /// <see cref="PolicyFixture"/>.</b> Everything that type says rests on this
    /// file being the one `gg2-rl` handed over, trained against
    /// <c>7ff5940d97aa7bed</c>. If a retrained checkpoint is ever dropped in, this
    /// goes red rather than absorbing it — at which point the overrides
    /// <c>PolicyFixture.Load</c> and <c>MintedForThisBuild</c> exist for should
    /// come out, not be updated.
    /// </remarks>
    [Fact]
    public void TheCheckpointDescribesItselfAndItsOwnSimulation()
    {
        var policy = Load();

        Assert.Equal(ObservationWriter.Length, policy.ObservationLength);
        Assert.Equal(PolicyFixture.TrainedStamp, policy.SimulationUuid);
        Assert.Equal(4, policy.ActionRepeat);
        Assert.True(policy.HeadShape.SequenceEqual(ActionDecoder.HeadShape));

        Assert.NotEqual(SimulationStamp.Uuid, policy.SimulationUuid);
    }

    /// <summary>
    /// Loading the checked-in checkpoint without the override is a refusal.
    /// </summary>
    /// <remarks>
    /// <b>The shipped checkpoint does not belong to this build</b>, and
    /// <c>Load</c> says so rather than playing it.
    /// </remarks>
    [Fact]
    public void TheCheckedInCheckpointIsRefusedWithoutTheOverride()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => PolicyNetwork.Load(Checkpoint));

        Assert.Contains(PolicyFixture.TrainedStamp, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(SimulationStamp.Uuid, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A checkpoint carrying this build's stamp loads with no override at all.
    /// </summary>
    /// <remarks>
    /// <b>The accept path, which the test above cannot cover.</b> Without it a
    /// <c>Load</c> that refused <em>every</em> checkpoint would pass everything
    /// here — and the whole suite would still be green, because every other caller
    /// passes the override.
    /// </remarks>
    [Fact]
    public void ACheckpointStampedForThisBuildIsAccepted()
    {
        var policy = PolicyNetwork.Load(PolicyFixture.MintedForThisBuild);

        Assert.Equal(SimulationStamp.Uuid, policy.SimulationUuid);
        Assert.Equal(ObservationWriter.Length, policy.ObservationLength);
        Assert.True(policy.HeadShape.SequenceEqual(ActionDecoder.HeadShape));
    }

    /// <summary>
    /// The forward pass agrees with the trainer's numpy one, logit for logit.
    /// </summary>
    /// <remarks>
    /// <b>The tolerance is float32 summation-order noise and nothing else.</b>
    /// Measured at <b>6.7e-6</b> absolute over the fixture; 1e-4 is the bound
    /// asserted, which is fifteen times the observed worst case and still far
    /// below any logit gap that decides a head. <b>The heads are compared
    /// EXACTLY</b> — a tolerance there would be a tolerance on the answer.
    /// </remarks>
    [Fact]
    public void TheForwardPassAgreesWithTheTrainer()
    {
        var policy = Load();
        var (rows, length, observations) = ReadObservations();
        var expected = File.ReadAllLines(Path.Combine(Directory, "expected-logits.txt"));

        Assert.Equal(ObservationWriter.Length, length);
        Assert.Equal(rows, int.Parse(expected[0].Split(' ')[0], CultureInfo.InvariantCulture));

        var observation = new float[length];
        var heads = new int[ActionDecoder.HeadShape.Length];
        var worstLogit = 0.0;
        var headsCompared = 0;

        for (var row = 0; row < rows; row++)
        {
            observations.AsSpan(row * length, length).CopyTo(observation);

            var mine = policy.Logits(observation).ToArray();

            policy.Act(observation, heads);

            var theirLogits = Numbers(expected[1 + (row * 2)]);
            var theirHeads = expected[2 + (row * 2)]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToArray();

            Assert.Equal(mine.Length, theirLogits.Length);

            for (var i = 0; i < mine.Length; i++)
                worstLogit = Math.Max(worstLogit, Math.Abs(mine[i] - theirLogits[i]));

            for (var head = 0; head < heads.Length; head++)
            {
                Assert.Equal(theirHeads[head], heads[head]);
                headsCompared++;
            }
        }

        Assert.True(worstLogit < 1e-4, $"worst logit deviation {worstLogit:0.###e0} is past float32 noise");

        // The premise: a loop that compared nothing would satisfy everything
        // above.
        Assert.Equal(64 * ActionDecoder.HeadShape.Length, headsCompared);
    }

    /// <summary>
    /// Every head actually varies across the fixture.
    /// </summary>
    /// <remarks>
    /// <b>Without this the comparison above is weaker than it looks.</b> If a
    /// head were constant over all 64 rows, an implementation that always returned
    /// that constant — or that read the wrong slice and happened to argmax to the
    /// same index — would pass. This is the check that the fixture exercises what
    /// it claims to.
    /// </remarks>
    [Fact]
    public void TheFixtureExercisesEveryHead()
    {
        var policy = Load();
        var (rows, length, observations) = ReadObservations();
        var observation = new float[length];
        var heads = new int[ActionDecoder.HeadShape.Length];
        var seen = new HashSet<int>[heads.Length];

        for (var i = 0; i < seen.Length; i++)
            seen[i] = [];

        for (var row = 0; row < rows; row++)
        {
            observations.AsSpan(row * length, length).CopyTo(observation);
            policy.Act(observation, heads);

            for (var head = 0; head < heads.Length; head++)
                seen[head].Add(heads[head]);
        }

        for (var head = 0; head < seen.Length; head++)
            Assert.True(seen[head].Count > 1, $"head {head} took only one value across the fixture");
    }

    /// <summary>Running the policy allocates nothing.</summary>
    /// <remarks>
    /// Warmed past tier-1 promotion (30 calls) before measuring — a short
    /// warm-up puts the rejit inside the measured window and its bookkeeping on
    /// the measuring thread.
    /// </remarks>
    [Fact]
    public void ActingAllocatesNothing()
    {
        var policy = Load();
        var (_, length, observations) = ReadObservations();
        var observation = new float[length];
        var heads = new int[ActionDecoder.HeadShape.Length];

        observations.AsSpan(0, length).CopyTo(observation);

        for (var i = 0; i < 300; i++)
            policy.Act(observation, heads);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 50; i++)
            policy.Act(observation, heads);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// A checkpoint whose stamp is not this build's is refused, not warned about.
    /// </summary>
    /// <remarks>
    /// <b>The <c>PROTOCOL_UUID</c> idiom.</b> A policy running against a
    /// different simulation is not slightly wrong — it is acting on numbers that
    /// do not mean what it learned they meant.
    /// </remarks>
    [Fact]
    public void AForeignStampIsRefused()
    {
        // The MINTED copy, because this has to start from a checkpoint this
        // build accepts. Corrupting the checked-in one proves nothing — it is
        // already refused, and the test would pass on a Load that never looked.
        var bytes = File.ReadAllBytes(PolicyFixture.MintedForThisBuild);
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        var at = text.IndexOf(SimulationStamp.Uuid, StringComparison.Ordinal);

        Assert.True(at > 0, "the stamp should be findable in the minted checkpoint");

        // Flip one character of the stamp in place: same length, different value.
        bytes[at] = bytes[at] == (byte)'0' ? (byte)'1' : (byte)'0';

        using var stream = new MemoryStream(bytes);

        var refusal = Assert.Throws<InvalidOperationException>(() => PolicyNetwork.Load(stream));

        Assert.Contains("trained against simulation", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckpointWithoutMetadataIsRefused()
    {
        using var buffer = new MemoryStream();

        using (var zip = new System.IO.Compression.ZipArchive(
            buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("w0.npy");
        }

        buffer.Position = 0;

        Assert.Throws<InvalidDataException>(() => PolicyNetwork.Load(buffer));
    }

    private static (int Rows, int Length, float[] Values) ReadObservations()
    {
        var raw = File.ReadAllBytes(Path.Combine(Directory, "observations.bin"));
        var rows = BitConverter.ToInt32(raw, 0);
        var length = BitConverter.ToInt32(raw, 4);
        var values = new float[rows * length];

        for (var i = 0; i < values.Length; i++)
            values[i] = BitConverter.ToSingle(raw, 8 + (i * 4));

        return (rows, length, values);
    }

    private static double[] Numbers(string line) =>
        [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture))];
}
