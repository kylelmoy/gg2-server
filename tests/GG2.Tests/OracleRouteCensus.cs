using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The route-level ceiling for flying by search: every travel scenario run by
/// the shipping bot and by an oracle that, at each edge, searches the fastest
/// keys from where the bot actually is to the next node and plays them.
/// </summary>
/// <remarks>
/// A measurement, not a gate; skipped in the suite. The oracle searches live and
/// is far too slow to ship; it asks how much a plan cache could save on routes,
/// entered at speed.
/// </remarks>
public sealed class OracleRouteCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The shipping bot's route and goal, with the keys replaced by searched plans.</summary>
    private sealed class Oracle(ScriptedBot bot, NavGraph graph, CollisionWorld world, int beam, int horizon) : IInputSource, IScenarioSubject
    {
        private readonly PathFollower _edges = new(graph);
        private PlayerKeys[] _plan = [];
        private int _at;
        private int _from = -1;

        public int Searches { get; private set; }
        public int Fallbacks { get; private set; }

        public bool SetGoal(double worldX, double worldY) => bot.SetGoal(worldX, worldY);

        public bool HasTarget => bot.HasTarget;

        public PlayerInput Poll(in WorldView view, int slot, int tick)
        {
            var input = bot.Poll(in view, slot, tick);
            ref readonly var c = ref view.Character(slot);

            // Receding horizon: a plan runs until the bot stands on a node other
            // than the one it was made on, then the next is searched from there.
            if (_at < _plan.Length && !(c.OnGround && _at > 0 && graph.NodeFromWorld(c.X, c.Y) is var now && now >= 0 && now != _from))
                return new PlayerInput(_plan[_at++], input.NetAimDirection, input.AimDistance);

            _plan = [];

            var path = bot.Path;
            if (!path.HasPath || !c.OnGround)
                return input;

            var here = graph.NodeFromWorld(c.X, c.Y);
            var at = path.Path.IndexOf(here, path.PathAt);
            if (at < 0)
                return input;

            var ahead = Math.Min(at + horizon, path.Path.Count - 1);
            var last = ahead == path.Path.Count - 1;
            var to = path.Path[Math.Max(ahead, at)];
            if (to == here && !last)
                return input;

            for (var i = at; i < ahead; i++)
            {
                if (_edges.TryResolveHop(path, path.Path[i], path.Path[i + 1], out var edge)
                    && edge.Type is NavEdgeType.RocketJump or NavEdgeType.StickyJump)
                {
                    return input;
                }
            }

            var goalX = last ? path.GoalX : NavGraph.ColumnWorldX((graph.Nodes[to].X0 + graph.Nodes[to].X1) / 2);
            var found = EdgeSearch.Fastest(world, graph, in c, to, goalX, beam: beam, stop: last);
            Searches++;

            if (!found.Found)
            {
                Fallbacks++;
                return input;
            }

            _plan = found.Plan;
            _at = 0;
            _from = here;
            return new PlayerInput(_plan[_at++], input.NetAimDirection, input.AimDistance);
        }
    }

    [Fact(Skip = "a census: run by hand")]
    public void Census()
    {
        ReferenceTree.RequireNavCache();

        long botTicks = 0, oracleTicks = 0;
        int both = 0;

        foreach (var horizon in new[] { 1, 2, 3 })
        {
        botTicks = 0; oracleTicks = 0; both = 0;
        output.WriteLine($"-- horizon {horizon} node(s)");
        foreach (var s in Scenarios.All.Where(s => s.Mode == ScenarioMode.Travel && s.Enemies.Count == 0))
        {
            var world = ScenarioWorld.Load(s.Map, s.Area, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
            var shipped = ScenarioRunner.Run(s, world, new ScriptedBot(world.Nav, new NavQuery(s.Team), BotSkill.ForTier(5), stagger: 1));

            var world2 = ScenarioWorld.Load(s.Map, s.Area, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
            var inner = new ScriptedBot(world2.Nav, new NavQuery(s.Team), BotSkill.ForTier(5), stagger: 1);
            var oracle = new Oracle(inner, world2.Nav!, world2.Collision, beam: 1000, horizon: horizon);
            var searched = ScenarioRunner.Run(s, world2, oracle);

            output.WriteLine(
                $"{s.Name,-34} shipped {(shipped.Arrived ? shipped.Ticks.ToString() : "--"),5}  oracle {(searched.Arrived ? searched.Ticks.ToString() : "--"),5}"
                + $"  searches {oracle.Searches,3} fallbacks {oracle.Fallbacks,2}");

            if (shipped.Arrived && searched.Arrived)
            {
                both++;
                botTicks += shipped.Ticks;
                oracleTicks += searched.Ticks;
            }
        }

        output.WriteLine($"both arrived on {both} legs: shipped {botTicks} ticks, oracle {oracleTicks}, saved {100.0 * (botTicks - oracleTicks) / Math.Max(1, botTicks):0.0}%");
        }
    }
}
