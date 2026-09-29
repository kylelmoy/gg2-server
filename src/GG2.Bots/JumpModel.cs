using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// The arithmetic behind the takeoff gate: would this arc reach, and where does
/// the run-up start.
/// </summary>
/// <remarks>
/// <c>botJumpReach.gml</c> and <c>botRunupCol.gml</c>. Both exist because a jump
/// edge is flown as a <em>trajectory</em> and the generator proved that
/// trajectory as a <b>constant</b> horizontal velocity applied from tick zero —
/// while a GG2 character accelerates, and starts from whatever it is already
/// carrying, which after a climb up a staircase is nothing or is pointing the
/// wrong way.
/// </remarks>
public static class JumpModel
{
    /// <summary>
    /// How far along an arc this character would actually get, in world pixels,
    /// leaving the takeoff column carrying <paramref name="v0"/> along the jump.
    /// </summary>
    /// <remarks>
    /// <b>The arithmetic the takeoff gate was missing.</b> The tracker presses
    /// toward where the plan says the bot should be by now, so it does catch up —
    /// but only out of the surplus between the arc's speed and this class's own
    /// ceiling. An arc that asks for most of that ceiling has no surplus, the
    /// deficit built up over the acceleration ramp is never repaid, and the bot
    /// lands short every single time. Which is how the same edge gets
    /// blacklisted, un-blacklisted and flown again for a whole round.
    /// <para>
    /// Two measurements decided the shape, both offline over the twenty-three
    /// cached graphs: <b>3,310 of 62,069 jump edges are unflyable by a heavy</b>
    /// from the run-up its source node offers (<c>NAV_JUMP_VX</c> of 4.53 is
    /// literally a heavy's ceiling, and on koth_corinth 96% of a heavy's routes
    /// to the point cross one) — and <b>91.9% of jump edges need no takeoff speed
    /// at all</b>. That second number is why the gate asks this rather than
    /// demanding the arc's full speed: demanding it was tried and lost, 647 → 868
    /// and 674 → 997 ticks on the valley scenarios with no more arrivals, because
    /// it forces a run-up on every jump in the game.
    /// </para>
    /// <para>
    /// <b>The motion law is <c>Character.events/Begin Step.xml</c> in its own
    /// order</b>: the run key adds first and only while under
    /// <c>basemaxspeed</c>, friction divides afterwards, and the snap to rest
    /// applies only with nothing held. The control factor is read live rather
    /// than assumed, because Begin Step sets it from <c>moveStatus</c> and not
    /// from <c>onground</c> — the same value applies in the air, which is the
    /// whole reason the tracker can correct a slow takeoff.
    /// </para>
    /// <para>
    /// <b>The tracker replayed here is <see cref="PathFollower"/>'s own.</b>
    /// Anything that changes there has to change here, or the gate is answering a
    /// question about a flight the follower does not make.
    /// </para>
    /// <para>
    /// <b>Not monotonic in <paramref name="v0"/>, and that is real rather than
    /// a rounding artefact</b>: on koth_gallery's n56 → n44 arc, 3.50 clears, 3.75
    /// and 4.00 fail, and 4.25 clears again — because arriving <em>ahead</em> of a
    /// constant-velocity schedule makes the tracker brake. So a caller must not
    /// bisect this for a threshold, and the takeoff gate must have an escape hatch
    /// for a bot that lands in a band it cannot leave.
    /// </para>
    /// </remarks>
    public static double Reach(in CharacterState character, double needVx, double flightTicks, double v0)
    {
        var (control, _) = CharacterPhysics.ControlAndFriction(character);

        var acceleration = character.RunPower * control;
        var maxSpeed = ClassStats.For(character.Class).BaseMaxSpeed;
        var friction = ClassStats.BaseFriction;

        // Character's own Create warns that baseFriction may be neither 0 nor 1
        // or the divide blows up. It is 1.15 on every class; this is here so a
        // caller that hands over something that is not a character fails as a bad
        // answer rather than as a division.
        if (friction <= 0)
            return 0;

        var total = needVx * flightTicks;
        var speed = v0;
        var travelled = 0.0;

        // `round`, and it is GM8's — half to even. A fractional tick count
        // rounding the other way is one whole tick of an arc that may only have
        // seventeen.
        var ticks = Math.Max(1, (int)Gml.Round(flightTicks));

        for (var air = 1; air <= ticks; air++)
        {
            var want = Math.Min(total, needVx * air);

            var press = travelled < want - 1 ? 1
                : travelled > want + 1 ? -1
                : 0;

            if (press > 0 && speed <= maxSpeed)
                speed += acceleration;
            else if (press < 0 && speed >= -maxSpeed)
                speed -= acceleration;

            speed /= friction;

            // The "ice skating" snap, and it applies only with nothing held.
            if (Math.Abs(speed) < BotConstants.RestSpeed && press == 0)
                speed = 0;

            travelled += speed;
        }

        return travelled;
    }

    /// <summary>
    /// Where a bot should back up to before an arc it cannot currently make —
    /// <c>botRunupCol.gml</c>.
    /// </summary>
    /// <remarks>
    /// <b>This follows WALK edges through the GRAPH, not the route, and that
    /// is the whole point.</b> Clamping the run-up into the source node's own
    /// columns fails on a node one anchor column wide — the top step of a
    /// staircase — because that clamp resolves back to the column the bot is
    /// already standing on: the run-up becomes a no-op and the jump happens from
    /// a standstill anyway. Modelled offline: <b>3,310 unflyable jump edges → 253,
    /// and koth_gallery's 105 → 0.</b>
    /// <para>
    /// Walk edges are one continuous floor by construction, so following them
    /// backwards is following the floor; it stops at the last column with
    /// something under it. The floor a bot needs is often a surface the route
    /// never mentions, and that case is exactly the one worth allowing.
    /// </para>
    /// <para>
    /// Gates are honoured, because a run-up through a gate the bot cannot pass
    /// is a run-up into a wall.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The anchor column to back up to. Falls back to
    /// <paramref name="takeoffColumn"/> — no run-up — when there is nowhere to go.
    /// </returns>
    public static int RunupColumn(
        NavGraph graph, int node, int takeoffColumn, int jumpDirection, int maxCells, in NavQuery query)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if ((uint)node >= (uint)graph.NodeCount || jumpDirection == 0)
            return takeoffColumn;

        var want = takeoffColumn - (jumpDirection * maxCells);
        var current = node;

        // The frontier is the far edge of the floor found so far, measured
        // against the jump: the end a bot backing up would reach next.
        var frontier = jumpDirection > 0 ? graph.Nodes[current].X0 : graph.Nodes[current].X1;

        for (var hop = 0; hop < BotConstants.RunupHops; hop++)
        {
            if (jumpDirection > 0 ? frontier <= want : frontier >= want)
                break;

            var best = -1;
            var bestSpan = 0;

            foreach (var edge in graph.EdgesFrom(current))
            {
                // Walk edges only. A fall or a jump backwards is not a run-up,
                // it is leaving the floor the run-up is being built on.
                if (edge.Type != NavEdgeType.Walk)
                    continue;

                if (!graph.EdgePassable(edge, query.Team, query.HasIntel, query.SetupGatesClosed))
                    continue;

                var to = graph.Nodes[edge.To];

                if (jumpDirection > 0)
                {
                    // Adjacent on the left, and not overlapping forward.
                    if (to.X1 < frontier - 1 || to.X1 >= frontier)
                        continue;

                    if (best < 0 || to.X0 < bestSpan)
                    {
                        best = edge.To;
                        bestSpan = to.X0;
                    }
                }
                else
                {
                    if (to.X0 > frontier + 1 || to.X0 <= frontier)
                        continue;

                    if (best < 0 || to.X1 > bestSpan)
                    {
                        best = edge.To;
                        bestSpan = to.X1;
                    }
                }
            }

            if (best < 0)
                break;

            current = best;
            frontier = bestSpan;
        }

        // Never further than asked for, and never further than the floor goes.
        return jumpDirection > 0 ? Math.Max(want, frontier) : Math.Min(want, frontier);
    }
}
