using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// The fastest key sequence found from a standing start to the follower's
/// arrival test on a node: a beam search, tick by tick, over the same physics
/// steps <see cref="FlightPredictor"/> takes.
/// </summary>
/// <remarks>
/// A measurement first (<c>gg2-rl</c> <c>docs/PLAN.md</c>, "Navigation by search",
/// NS): how far the follower is from the fastest inputs on an edge.
/// <para>
/// ⚠️ <b>A beam is not exhaustive.</b> A found plan is an upper bound on the
/// fastest, and a plan not found is "not found", not "impossible".
/// </para>
/// <para>
/// Per tick the moves are left, right or none, each with or without a jump press;
/// a press needs a tick without one before it, because the game jumps on the edge
/// and a held bit does nothing (<c>Character</c> Begin Step). States are merged on
/// a rounded (x, y, hspeed, vspeed, ground, second jump used, jump held), and the
/// beam keeps those nearest the goal.
/// </para>
/// </remarks>
public static class EdgeSearch
{
    /// <summary>What a search found.</summary>
    /// <param name="Found">Whether any plan reached the arrival test.</param>
    /// <param name="Ticks">Ticks of the plan found, or 0.</param>
    /// <param name="Plan">The keys, one per tick, or empty.</param>
    /// <param name="Expanded">States stepped, for cost.</param>
    public readonly record struct Result(bool Found, int Ticks, PlayerKeys[] Plan, long Expanded);

    private readonly record struct Entry(CharacterState Character, int Trail, bool JumpHeld, double Score);

    /// <summary>
    /// Searches from <paramref name="start"/> to standing on <paramref name="to"/>
    /// within <see cref="BotConstants.ArriveTol"/> of <paramref name="goalX"/>, at
    /// under 1 px/tick: the follower's own arrival test. With
    /// <paramref name="stop"/> false, standing on <paramref name="to"/> at any
    /// speed and anywhere on it, for a node a route goes on from.
    /// </summary>
    public static Result Fastest(
        CollisionWorld world, NavGraph graph, in CharacterState start, int to, double goalX,
        int maxTicks = 400, int beam = 2000, bool stop = true)
    {
        var run = new Run(world, graph, in start, to, goalX, maxTicks, beam, stop);
        run.Advance(long.MaxValue);
        return run.Result;
    }

    /// <summary>
    /// <see cref="Fastest"/>, stepped a budget of states at a time, so a bot can
    /// search across ticks: the same plan as <see cref="Fastest"/> whatever the
    /// budgets.
    /// </summary>
    /// <remarks>
    /// Counted in states and not in time, so a run is deterministic.
    /// </remarks>
    public sealed class Run
    {
        private readonly CollisionWorld _world;
        private readonly NavGraph _graph;
        private readonly int _to;
        private readonly double _goalX;
        private readonly double _goalY;
        private readonly PlayerKeys _kept;
        private readonly int _maxTicks;
        private readonly int _beam;
        private readonly bool _stop;

        // The trail: each stepped state's parent and the keys that made it, so a
        // plan is read back from the arrival without carrying it in every entry.
        private readonly List<int> _parents = [];
        private readonly List<PlayerKeys> _keysOf = [];

        private List<Entry> _frontier;
        private List<Entry> _next = [];
        private readonly HashSet<(int, int, int, int, int)> _seen = [];
        private int _tick = 1;
        private int _at;

        public Run(
            CollisionWorld world, NavGraph graph, in CharacterState start, int to, double goalX,
            int maxTicks = 400, int beam = 2000, bool stop = true)
        {
            ArgumentNullException.ThrowIfNull(world);
            ArgumentNullException.ThrowIfNull(graph);

            _world = world;
            _graph = graph;
            _to = to;
            _maxTicks = maxTicks;
            _beam = beam;
            _stop = stop;

            var node = graph.Nodes[to];
            _goalX = Math.Clamp(goalX, NavGraph.ColumnWorldX(node.X0), NavGraph.ColumnWorldX(node.X1));
            _goalY = graph.StandY(to);
            _kept = start.KeyState & (PlayerKeys.Attack | PlayerKeys.Special);
            _frontier = [new(start, -1, false, 0)];
        }

        /// <summary>Whether the run has finished, found or not.</summary>
        public bool Done { get; private set; }

        /// <summary>What it found; meaningful once <see cref="Done"/>.</summary>
        public Result Result { get; private set; }

        /// <summary>States stepped so far.</summary>
        public long Expanded { get; private set; }

        /// <summary>
        /// Steps states until <paramref name="budget"/> is spent or the run ends,
        /// and returns <see cref="Done"/>. A state's six moves are stepped
        /// together, so a call can overrun its budget by five.
        /// </summary>
        public bool Advance(long budget)
        {
            var stopAt = Expanded + budget;
            Span<int> dirs = [1, 0, -1];

            while (!Done)
            {
                if (_tick > _maxTicks || _frontier.Count == 0)
                {
                    Finish(new Result(false, 0, [], Expanded));
                    break;
                }

                if (_at == _frontier.Count)
                {
                    if (_next.Count > _beam)
                    {
                        _next.Sort(static (a, b) => a.Score.CompareTo(b.Score));
                        _next.RemoveRange(_beam, _next.Count - _beam);
                    }

                    (_frontier, _next) = (_next, _frontier);
                    _next.Clear();
                    _seen.Clear();
                    _at = 0;
                    _tick++;
                    continue;
                }

                if (Expanded >= stopAt)
                    break;

                var entry = _frontier[_at++];

                foreach (var dir in dirs)
                {
                    for (var press = 0; press < 2; press++)
                    {
                        var jump = press == 1;
                        if (jump && entry.JumpHeld)
                            continue;

                        var keys = (dir > 0 ? PlayerKeys.Right : dir < 0 ? PlayerKeys.Left : PlayerKeys.None)
                            | (jump ? PlayerKeys.Jump : PlayerKeys.None) | _kept;

                        var c = entry.Character;
                        Expanded++;

                        if (StepOnce(_world, ref c, keys))
                            continue;

                        _parents.Add(entry.Trail);
                        _keysOf.Add(keys);
                        var trail = _parents.Count - 1;

                        if (c.OnGround
                            && (!_stop || (Math.Abs(c.HSpeed) < 1 && Math.Abs(c.X - _goalX) <= BotConstants.ArriveTol))
                            && _graph.NodeFromWorld(c.X, c.Y) == _to)
                        {
                            Finish(new Result(true, _tick, Read(_parents, _keysOf, trail), Expanded));
                            return true;
                        }

                        var key = (
                            (int)Math.Round(c.X * 2),
                            (int)Math.Round(c.Y * 2),
                            (int)Math.Round(c.HSpeed * 4),
                            (int)Math.Round(c.VSpeed * 2),
                            (c.OnGround ? 1 : 0) | (c.DoubleJumpUsed ? 2 : 0) | (jump ? 4 : 0));

                        if (!_seen.Add(key))
                            continue;

                        var dx = c.X - _goalX;
                        var dy = c.Y - _goalY;
                        _next.Add(new Entry(c, trail, jump, Math.Sqrt((dx * dx) + (dy * dy))));
                    }
                }
            }

            return Done;
        }

        private void Finish(Result result)
        {
            Result = result;
            Done = true;
            _frontier.Clear();
            _next.Clear();
            _seen.Clear();
        }
    }

    /// <summary>
    /// The position after each tick of <paramref name="plan"/> from
    /// <paramref name="start"/>, stepped as the search steps, so a bot playing a
    /// plan can check it is still on it.
    /// </summary>
    public static (double X, double Y)[] Trace(CollisionWorld world, in CharacterState start, PlayerKeys[] plan)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(plan);

        var track = new (double X, double Y)[plan.Length];
        var c = start;

        for (var i = 0; i < plan.Length; i++)
        {
            StepOnce(world, ref c, plan[i]);
            track[i] = (c.X, c.Y);
        }

        return track;
    }

    // One tick of the character's physics under keys; true if it died.
    private static bool StepOnce(CollisionWorld world, ref CharacterState c, PlayerKeys keys)
    {
        c.ApplyInput(new PlayerInput(keys, c.NetAimDirection, c.AimDistance));
        CharacterPhysics.BeginStepInput(world, ref c);
        CharacterPhysics.BeginStepAccelerate(world, ref c);
        CharacterPhysics.Step(world, ref c);
        CharacterPhysics.HeavyStep(ref c);
        return CharacterPhysics.EndStep(world, ref c);
    }

    private static PlayerKeys[] Read(List<int> parents, List<PlayerKeys> keysOf, int trail)
    {
        var plan = new List<PlayerKeys>();

        for (var at = trail; at >= 0; at = parents[at])
            plan.Add(keysOf[at]);

        plan.Reverse();
        return [.. plan];
    }
}
