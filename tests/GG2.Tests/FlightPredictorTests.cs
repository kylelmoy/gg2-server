using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="FlightPredictor"/> lands where the live world lands, to the bit.
/// </summary>
/// <remarks>
/// A character is put on a spread of nav nodes on three maps, runs for a few
/// ticks, then jumps and holds a key, and the predictor's landing taken just
/// before the jump is compared with where the world puts it. The path follower
/// launches on these predictions (<c>gg2-rl</c> <c>docs/PLAN.md</c>, navigation,
/// N1), so a disagreement here is a bot jumping at something it cannot reach.
/// </remarks>
public sealed class FlightPredictorTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Plan(int runTicks, int runDir, int holdDir) : IInputSource
    {
        public int JumpTick { get; set; } = int.MaxValue;

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            var keys = tick < JumpTick ? Key(runDir) : Key(holdDir);

            if (tick == JumpTick)
                keys |= PlayerKeys.Jump;

            return new PlayerInput(keys, 0, 0);
        }

        public int RunTicks => runTicks;

        private static PlayerKeys Key(int dir) => dir > 0 ? PlayerKeys.Right : dir < 0 ? PlayerKeys.Left : PlayerKeys.None;
    }

    [Theory]
    [InlineData("gg_debug")]
    [InlineData("ctf_truefort")]
    [InlineData("koth_corinth")]
    public void APredictedJumpLandsWhereTheWorldLandsIt(string map)
    {
        ReferenceTree.RequireNavCache();

        var probe = ScenarioWorld.Load(map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var graph = probe.Nav!;
        var nodes = graph.Nodes.Length;
        var compared = 0;
        var landed = 0;

        for (var n = 0; n < nodes; n += Math.Max(1, nodes / 40))
        {
            foreach (var cls in new[] { PlayerClass.Scout, PlayerClass.Heavy })
            {
                foreach (var (run, runDir, holdDir) in new[] { (0, 0, 1), (8, 1, 1), (8, -1, -1), (8, 1, 0), (12, 1, -1) })
                {
                    var world = ScenarioWorld.Load(map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
                    var node = graph.Nodes[n];
                    var x = NavGraph.ColumnWorldX((node.X0 + node.X1) / 2);
                    var slot = world.Place(cls, Team.Red, x, EdgeFlights.StandingY(graph, n));
                    var plan = new Plan(run, runDir, holdDir);
                    world.Drive(slot, plan);
                    world.Step(5 + run);

                    if (!world.State.Alive[slot] || !world.State.Characters[slot].OnGround)
                        continue;

                    plan.JumpTick = world.Tick;
                    var predicted = FlightPredictor.Land(
                        world.Collision, in world.State.Characters[slot], holdDir, jump: true,
                        out var px, out var py, out var pt);

                    var actual = false;
                    var ax = 0.0;
                    var ay = 0.0;
                    var at = 0;
                    var air = false;

                    for (var t = 1; t <= FlightPredictor.MaxTicks; t++)
                    {
                        world.Step();

                        if (!world.State.Alive[slot])
                            break;

                        ref readonly var c = ref world.State.Characters[slot];

                        if (!c.OnGround)
                        {
                            air = true;
                        }
                        else if (air)
                        {
                            actual = true;
                            ax = c.X;
                            ay = c.Y;
                            at = t;
                            break;
                        }
                        else if (t >= 2)
                        {
                            break;
                        }
                    }

                    compared++;

                    Assert.True(predicted == actual, $"{map} node {n} {cls} run {run}/{runDir} hold {holdDir}: predicted {predicted}, world {actual}");

                    if (actual)
                    {
                        landed++;
                        Assert.True(px == ax && py == ay && pt == at,
                            $"{map} node {n} {cls} run {run}/{runDir} hold {holdDir}: predicted ({px}, {py}) at {pt}, world ({ax}, {ay}) at {at}");
                    }
                }
            }
        }

        output.WriteLine($"{map}: {compared} jumps compared, {landed} landed");
        Assert.True(landed > 100, $"{map}: only {landed} jumps landed, too few to say the predictor agrees");
    }
}
