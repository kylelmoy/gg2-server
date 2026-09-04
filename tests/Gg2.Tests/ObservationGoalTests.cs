using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Goal conditioning — <c>gg2-rl</c> phase 4's per-slot macro goal.
/// </summary>
/// <remarks>
/// <b>The load-bearing claim is the DEFAULT, not the feature.</b> <c>gg2-rl</c>
/// asked for this shape specifically because an unchanged default means every
/// existing checkpoint keeps seeing byte-identical observations — no
/// <c>ObservationWriter.LayoutVersion</c> bump, no <c>SIM_UUID</c> move, no
/// retrain of 83 checkpoints. Its words: "I would rather have that than a cleaner
/// API."
/// <para>
/// So the first test here is the boring one, and it is the one that matters.
/// If it ever goes red, <b>that is a layout bump and a retrain</b>, not a test to
/// update.
/// </para>
/// </remarks>
public sealed class ObservationGoalTests
{
    private const string Map = "gg_debug";

    /// <summary>
    /// Where the goal block starts: the last <c>3 + GoalKinds</c> floats.
    /// </summary>
    private static int GoalAt => ObservationWriter.Length - (2 + 1 + ObservationWriter.GoalKinds);

    private static ScenarioWorld World(out int red, out int blue)
    {
        var world = ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, null);

        red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        blue = world.Place(PlayerClass.Scout, Team.Blue, 900, 820);

        return world;
    }

    /// <summary>
    /// A default goal writes exactly what the writer wrote before goals existed.
    /// </summary>
    /// <remarks>
    /// <b>Checked against a transcription of the old block, not against
    /// another call to the new one.</b> Comparing <c>Write(view, slot, buffer)</c>
    /// with <c>Write(view, slot, buffer, default)</c> would pass however wrong
    /// both were — they are the same code path, and the optional parameter makes
    /// them literally the same call. The point is agreement with what SHIPPED.
    /// </remarks>
    [Fact]
    public void ADefaultGoalIsTheNearestEnemyBlockThatShipped()
    {
        var world = World(out var red, out var blue);
        var buffer = new float[ObservationWriter.Length];
        var random = new Random(4);

        for (var trial = 0; trial < 500; trial++)
        {
            world.State.Characters[red].X = random.NextDouble() * 1200;
            world.State.Characters[red].Y = random.NextDouble() * 900;
            world.State.Characters[blue].X = random.NextDouble() * 1200;
            world.State.Characters[blue].Y = random.NextDouble() * 900;

            var view = new WorldView(in world.State, world.Collision);

            ObservationWriter.Write(in view, red, buffer);

            var expected = ShippedGoalBlock(in view, red);

            for (var i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], buffer[GoalAt + i]);
        }
    }

    /// <summary>
    /// The goal block as it was written before <see cref="AgentGoal"/> existed:
    /// direction and distance to the nearest enemy, kind 0.
    /// </summary>
    /// <remarks>
    /// <b>A transcription, kept deliberately verbatim.</b> Sharing anything
    /// with the writer — its scale constant, its <c>OneHot</c> — would make this
    /// agree with the implementation by construction rather than by being right.
    /// </remarks>
    private static float[] ShippedGoalBlock(in WorldView view, int slot)
    {
        var block = new float[2 + 1 + ObservationWriter.GoalKinds];
        var at = 0;

        Span<int> found = stackalloc int[1];
        var count = view.NearestEnemies(slot, found);

        if (count > 0)
        {
            ref readonly var self = ref view.Character(slot);
            ref readonly var enemy = ref view.Character(found[0]);

            var dx = enemy.X - self.X;
            var dy = enemy.Y - self.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            if (distance > 0)
            {
                block[at++] = (float)(dy / distance);
                block[at++] = (float)(dx / distance);
            }
            else
            {
                at += 2;
            }

            block[at++] = (float)Math.Min(distance / 400.0, 1.0);
        }
        else
        {
            at += 3;
        }

        block[at + 0] = 1f;

        return block;
    }

    [Fact]
    public void ATargetedGoalReportsThatPointInsteadOfTheEnemy()
    {
        var world = World(out var red, out _);
        var buffer = new float[ObservationWriter.Length];
        var view = new WorldView(in world.State, world.Collision);

        // Straight up from the character, 200 pixels. Chosen so the answer is
        // arithmetic rather than something to read off the map: dy = -200.
        ObservationWriter.Write(in view, red, buffer, AgentGoal.At(2, 600, 650));

        Assert.Equal(-1f, buffer[GoalAt + 0]);
        Assert.Equal(0f, buffer[GoalAt + 1]);
        Assert.Equal(0.5f, buffer[GoalAt + 2]);

        // And the kind one-hot moved off column 0.
        Assert.Equal(0f, buffer[GoalAt + 3 + 0]);
        Assert.Equal(1f, buffer[GoalAt + 3 + 2]);
    }

    /// <summary>
    /// A goal target does not have to be near an enemy, and having one stops the
    /// enemy being consulted at all.
    /// </summary>
    /// <remarks>
    /// <b>The enemy is MOVED between the two reads and the block does not
    /// change.</b> Asserting only that the goal block matches the target would
    /// pass if the writer were still secretly mixing the enemy in — this is the
    /// premise check, the same shape as <c>docs/ENV.md</c>'s per-slot driver
    /// tests, where four passed with the driver ignored entirely.
    /// </remarks>
    [Fact]
    public void ATargetedGoalIgnoresWhereTheEnemyIs()
    {
        var world = World(out var red, out var blue);
        var before = new float[ObservationWriter.Length];
        var after = new float[ObservationWriter.Length];
        var goal = AgentGoal.At(1, 300, 700);

        var view = new WorldView(in world.State, world.Collision);
        ObservationWriter.Write(in view, red, before, goal);

        world.State.Characters[blue].X = 1100;
        world.State.Characters[blue].Y = 400;

        var moved = new WorldView(in world.State, world.Collision);
        ObservationWriter.Write(in moved, red, after, goal);

        for (var i = 0; i < 3 + ObservationWriter.GoalKinds; i++)
            Assert.Equal(before[GoalAt + i], after[GoalAt + i]);

        // And the premise: moving the enemy DID change the observation
        // elsewhere. Without this the test above passes on a writer that ignores
        // the world entirely.
        Assert.False(before.AsSpan().SequenceEqual(after), "moving the enemy changed nothing at all");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeKindIsRefusedRatherThanClamped(int kind)
    {
        var env = new DuelEnv(ReferenceTree.IncludedFiles);

        env.Reset(Spec(), seed: 1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => env.SetGoal(env.RedSlot, new AgentGoal { Kind = kind }));
    }

    /// <summary>
    /// A goal outlives the episode it was set in.
    /// </summary>
    /// <remarks>
    /// <b>The half of this feature most likely to surprise, so it is pinned.</b>
    /// <c>EnvBatch.Step</c> auto-resets a finished world inside the call, so a
    /// trainer has no moment at which to re-set a goal; clearing it would train
    /// the policy on "engage" while the trainer believed otherwise.
    /// </remarks>
    [Fact]
    public void AGoalSurvivesAReset()
    {
        var env = new DuelEnv(ReferenceTree.IncludedFiles);

        env.Reset(Spec(), seed: 1);
        env.SetGoal(env.RedSlot, AgentGoal.At(3, 123, 456));

        env.Reset(Spec(), seed: 2);

        var goal = env.GoalFor(env.RedSlot);

        Assert.Equal(3, goal.Kind);
        Assert.True(goal.HasTarget);
        Assert.Equal(123, goal.TargetX);
        Assert.Equal(456, goal.TargetY);
    }

    private static DuelSpec Spec() => new()
    {
        Map = Map,
        RedClass = PlayerClass.Soldier,
        BlueClass = PlayerClass.Scout,
        RedX = 600,
        RedY = 850,
        BlueX = 900,
        BlueY = 820,
        DecisionCap = 100,
        ActionRepeat = 1,
    };
}
