using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="ObservationWriter"/> — the v0 layout.
/// </summary>
/// <remarks>
/// <c>docs/ENV.md</c>'s gates for this type: zero allocation, bounded values,
/// and a length that matches the advertised one. All three are here, plus the
/// two properties that would otherwise be silent — the mirrored version
/// constant, and the buffer being cleared.
/// </remarks>
public sealed class ObservationWriterTests
{
    private const string Map = "gg_debug";

    private static DuelEnv Env()
    {
        var env = new DuelEnv(ReferenceTree.IncludedFiles);

        env.Reset(
            new DuelSpec
            {
                Map = Map,
                RedClass = PlayerClass.Soldier,
                BlueClass = PlayerClass.Scout,
                RedX = 600,
                RedY = 850,
                BlueX = 700,
                BlueY = 850,
                DecisionCap = 100,
                ActionRepeat = 1,
            },
            seed: 5);

        return env;
    }

    private static float[] Observe(DuelEnv env, int slot)
    {
        var buffer = new float[ObservationWriter.Length];
        var view = new WorldView(in env.World.State, env.World.Collision);

        ObservationWriter.Write(in view, slot, buffer);
        return buffer;
    }

    /// <summary>
    /// ⚠️ <b>The layout version is mirrored in two places and the compiler
    /// cannot hold them equal.</b>
    /// </summary>
    /// <remarks>
    /// <c>Gg2.Simulation</c> does not depend on <c>Gg2.Env</c> — the simulation
    /// is the thing an env observes, not the other way round — so
    /// <c>SimulationStamp.ObservationLayoutVersion</c> cannot reference
    /// <see cref="ObservationWriter.LayoutVersion"/>. This test is the only
    /// thing standing between a layout change and a stamp that did not move
    /// with it, which is the exact failure the stamp exists to prevent.
    /// </remarks>
    [Fact]
    public void TheStampMirrorsTheWritersLayoutVersion()
    {
        Assert.Equal(ObservationWriter.LayoutVersion, SimulationStamp.ObservationLayoutVersion);
        Assert.Equal(ActionDecoder.AimBins, 15);
    }

    /// <summary>The advertised length is the length actually written.</summary>
    /// <remarks>
    /// Asserted by writing into an oversized buffer with a sentinel past the
    /// end: a writer that overran would be caught, and one that under-filled
    /// leaves the sentinel where the layout says data should be.
    /// </remarks>
    [Fact]
    public void TheWriterFillsExactlyTheAdvertisedLength()
    {
        var env = Env();
        var buffer = new float[ObservationWriter.Length + 4];

        Array.Fill(buffer, float.NaN);

        var view = new WorldView(in env.World.State, env.World.Collision);
        ObservationWriter.Write(in view, env.RedSlot, buffer);

        for (var i = 0; i < ObservationWriter.Length; i++)
            Assert.False(float.IsNaN(buffer[i]), $"index {i} was never written");

        for (var i = ObservationWriter.Length; i < buffer.Length; i++)
            Assert.True(float.IsNaN(buffer[i]), $"index {i} is past the end and was written");
    }

    /// <summary>A short buffer is refused rather than half-filled.</summary>
    [Fact]
    public void AShortBufferIsRefused()
    {
        var env = Env();
        var view = new WorldView(in env.World.State, env.World.Collision);
        var buffer = new float[ObservationWriter.Length - 1];

        try
        {
            ObservationWriter.Write(in view, env.RedSlot, buffer);
            Assert.Fail("a short buffer should have been refused");
        }
        catch (ArgumentException)
        {
            // expected
        }
    }

    /// <summary>
    /// ⚠️ <b>Every value is finite and inside a sane range.</b>
    /// </summary>
    /// <remarks>
    /// A NaN or an infinity in an observation poisons a whole training batch and
    /// does it silently — the loss goes to NaN several thousand steps later and
    /// the cause is nowhere near. The bound is generous rather than tight: the
    /// point is to catch a division by a zero maximum or an unnormalised
    /// coordinate, not to pin the scales.
    /// </remarks>
    [Fact]
    public void EveryValueIsFiniteAndBounded()
    {
        var env = Env();

        for (var step = 0; step < 20; step++)
        {
            foreach (var slot in new[] { env.RedSlot, env.BlueSlot })
            {
                var o = Observe(env, slot);

                for (var i = 0; i < o.Length; i++)
                {
                    Assert.True(float.IsFinite(o[i]), $"index {i} was {o[i]} at step {step}");
                    Assert.InRange(o[i], -50f, 50f);
                }
            }

            env.Step(
                new EnvAction { Horizontal = 2, Fire = 1, AimDelta = 9 },
                new EnvAction { Horizontal = 1, Jump = 1, AimDelta = 4 });
        }
    }

    /// <summary>
    /// ⚠️ <b>The buffer is cleared, so a reused one cannot show a stale enemy.</b>
    /// </summary>
    /// <remarks>
    /// Stale features are worse than absent ones: a presence flag of 0 beside a
    /// stale position teaches the policy that the flag means nothing. This
    /// writes a full observation, then writes a dead slot's over the top of it
    /// and checks nothing survived.
    /// </remarks>
    [Fact]
    public void TheBufferIsClearedBeforeWriting()
    {
        var env = Env();
        var buffer = Observe(env, env.RedSlot);

        Assert.Contains(buffer, v => v != 0f);

        var view = new WorldView(in env.World.State, env.World.Collision);

        // A slot with nobody in it: the observation must be all zeros, not the
        // previous occupant's.
        ObservationWriter.Write(in view, 20, buffer);

        Assert.All(buffer, v => Assert.Equal(0f, v));
    }

    /// <summary>
    /// The self block reads as the agent, and the enemy block as the other one.
    /// </summary>
    /// <remarks>
    /// Spot-checked rather than pinned field by field: the field order is the
    /// writer's own and pinning all of it here would be a copy that goes stale.
    /// What is worth asserting is that the two blocks are not swapped, which is
    /// the mistake that would otherwise train a policy to watch itself.
    /// </remarks>
    [Fact]
    public void TheSelfBlockIsTheAgentAndTheEnemyBlockIsTheOther()
    {
        var env = Env();
        var o = Observe(env, env.RedSlot);

        // Self is a Soldier: its one-hot column is set and the Scout's is not.
        Assert.Equal(1f, o[(int)PlayerClass.Soldier]);
        Assert.Equal(0f, o[(int)PlayerClass.Scout]);

        // Full health, and on the ground.
        const int hp = ObservationWriter.ClassCount;
        Assert.Equal(1f, o[hp], 3);

        // The first enemy is present, is a Scout, and is to the right.
        var enemy = ObservationWriter.ClassCount + 1 + 2 + 1 + 2 + 1 + 1 + 1;

        Assert.Equal(1f, o[enemy]);
        Assert.True(o[enemy + 1] > 0, "blue is to the right of red, so relative x should be positive");
        Assert.Equal(1f, o[enemy + 5 + (int)PlayerClass.Scout]);
    }

    /// <summary>
    /// A dead or empty slot observes all zeros rather than throwing.
    /// </summary>
    [Fact]
    public void AnEmptySlotObservesNothing()
    {
        var env = Env();
        var o = Observe(env, 25);

        Assert.All(o, v => Assert.Equal(0f, v));
    }

    /// <summary>
    /// ⚠️ <b>A Quote is refused rather than silently observed as no class.</b>
    /// </summary>
    /// <remarks>
    /// The one-hot is nine wide and the Quote is the tenth class, so encoding it
    /// would set no column at all — indistinguishable from an empty slot. It is
    /// a joke class a duel never selects, and refusing it loudly is what stops a
    /// policy being trained on a lie if one ever appears.
    /// </remarks>
    [Fact]
    public void AQuoteIsRefusedBecauseItHasNoColumn()
    {
        var env = Env();

        env.World.State.Characters[env.RedSlot].Class = PlayerClass.Quote;

        var buffer = new float[ObservationWriter.Length];

        try
        {
            var view = new WorldView(in env.World.State, env.World.Collision);
            ObservationWriter.Write(in view, env.RedSlot, buffer);
            Assert.Fail("a Quote should have been refused");
        }
        catch (ArgumentException)
        {
            // expected
        }
    }

    /// <summary>
    /// ⚠️ <b>Writing an observation allocates nothing.</b>
    /// </summary>
    /// <remarks>
    /// <c>docs/BOTAI.md</c> item 6: a feature vector is extracted for every
    /// agent on every decision during training, so garbage here is garbage in
    /// the hottest loop the project has. The nearest-K queries take stack
    /// buffers for the same reason.
    /// <para>
    /// ⚠️ Measured the way <c>BotAiConstraintsTests</c> does it — a warm-up pass
    /// first, because the first call through a code path JITs and that is not an
    /// allocation the steady state makes.
    /// </para>
    /// <para>
    /// ⚠️ <b>The warm-up was 5 calls and had to become 200, and the reason is the
    /// same one that made a benchmark lie earlier the same day.</b> .NET promotes
    /// a method to tier-1 after <b>30</b> calls, in the background — so 5 warm-up
    /// calls followed by 50 measured ones puts the promotion <em>inside the
    /// measured window</em>, and the tiering machinery's own bookkeeping lands on
    /// the measuring thread. It surfaced as this test failing roughly one run in
    /// six, with a different byte count each time, on 2026-08-30 when the ray fan
    /// grew — a bigger method takes longer to promote, so the window widened
    /// until it was hit.
    /// </para>
    /// <para>
    /// ⚠️ <b>The property itself was never in doubt and was checked separately:
    /// 200,000 calls in a standalone process allocate zero, at tier-0 and at
    /// tier-1 both.</b> This is a fix to the measurement, not a relaxation of the
    /// claim — the assertion is still <c>== 0</c>. Do not lower the warm-up
    /// again; it has to clear 30 with room for the rejit to land.
    /// </para>
    /// <para>
    /// ⚠️ <b>The promotion warm-up does NOT step the episode</b>, and the first
    /// attempt at this fix did — which ran past <c>DecisionCap</c> of 100 and
    /// threw on every run. Branch coverage needs a few steps; tier-1 needs a few
    /// hundred writes. They are separate loops because they want different things.
    /// </para>
    /// </remarks>
    [Fact]
    public void WritingAnObservationAllocatesNothing()
    {
        var env = Env();
        var buffer = new float[ObservationWriter.Length];

        // Warm up every branch: with an enemy, with projectiles in the air.
        for (var i = 0; i < 5; i++)
        {
            var warm = new WorldView(in env.World.State, env.World.Collision);
            ObservationWriter.Write(in warm, env.RedSlot, buffer);
            env.Step(new EnvAction { Fire = 1, AimDelta = ActionDecoder.AimCentreBin }, EnvAction.Idle);
        }

        // ⚠️ And past tier-1 promotion — 30 calls — WITHOUT stepping. The episode
        // has a decision cap of 100 and a warm-up that stepped this many times
        // would run it out and throw, which is what a first attempt at this fix
        // did on every run. See the remarks.
        for (var i = 0; i < 300; i++)
        {
            var warm = new WorldView(in env.World.State, env.World.Collision);
            ObservationWriter.Write(in warm, env.RedSlot, buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 50; i++)
        {
            var view = new WorldView(in env.World.State, env.World.Collision);
            ObservationWriter.Write(in view, env.RedSlot, buffer);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
