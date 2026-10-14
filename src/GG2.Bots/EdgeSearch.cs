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
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(graph);

        var node = graph.Nodes[to];
        goalX = Math.Clamp(goalX, NavGraph.ColumnWorldX(node.X0), NavGraph.ColumnWorldX(node.X1));
        var goalY = graph.StandY(to);
        var kept = start.KeyState & (PlayerKeys.Attack | PlayerKeys.Special);

        // The trail: each stepped state's parent and the keys that made it, so a
        // plan is read back from the arrival without carrying it in every entry.
        var parents = new List<int>();
        var keysOf = new List<PlayerKeys>();

        var frontier = new List<Entry> { new(start, -1, false, 0) };
        var next = new List<Entry>();
        var seen = new HashSet<(int, int, int, int, int)>();
        Span<int> dirs = [1, 0, -1];
        long expanded = 0;

        for (var tick = 1; tick <= maxTicks && frontier.Count > 0; tick++)
        {
            next.Clear();
            seen.Clear();

            foreach (var entry in frontier)
            {
                foreach (var dir in dirs)
                {
                    for (var press = 0; press < 2; press++)
                    {
                        var jump = press == 1;
                        if (jump && entry.JumpHeld)
                            continue;

                        var keys = (dir > 0 ? PlayerKeys.Right : dir < 0 ? PlayerKeys.Left : PlayerKeys.None)
                            | (jump ? PlayerKeys.Jump : PlayerKeys.None) | kept;

                        var c = entry.Character;
                        c.ApplyInput(new PlayerInput(keys, c.NetAimDirection, c.AimDistance));
                        CharacterPhysics.BeginStepInput(world, ref c);
                        CharacterPhysics.BeginStepAccelerate(world, ref c);
                        CharacterPhysics.Step(world, ref c);
                        CharacterPhysics.HeavyStep(ref c);
                        expanded++;

                        if (CharacterPhysics.EndStep(world, ref c))
                            continue;

                        parents.Add(entry.Trail);
                        keysOf.Add(keys);
                        var trail = parents.Count - 1;

                        if (c.OnGround
                            && (!stop || (Math.Abs(c.HSpeed) < 1 && Math.Abs(c.X - goalX) <= BotConstants.ArriveTol))
                            && graph.NodeFromWorld(c.X, c.Y) == to)
                        {
                            return new Result(true, tick, Read(parents, keysOf, trail), expanded);
                        }

                        var key = (
                            (int)Math.Round(c.X * 2),
                            (int)Math.Round(c.Y * 2),
                            (int)Math.Round(c.HSpeed * 4),
                            (int)Math.Round(c.VSpeed * 2),
                            (c.OnGround ? 1 : 0) | (c.DoubleJumpUsed ? 2 : 0) | (jump ? 4 : 0));

                        if (!seen.Add(key))
                            continue;

                        var dx = c.X - goalX;
                        var dy = c.Y - goalY;
                        next.Add(new Entry(c, trail, jump, Math.Sqrt((dx * dx) + (dy * dy))));
                    }
                }
            }

            if (next.Count > beam)
            {
                next.Sort(static (a, b) => a.Score.CompareTo(b.Score));
                next.RemoveRange(beam, next.Count - beam);
            }

            (frontier, next) = (next, frontier);
        }

        return new Result(false, 0, [], expanded);
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
