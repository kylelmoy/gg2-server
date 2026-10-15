using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// The stranding rescue (NS3): where the follower would ban the edge it is on,
/// it searches the keys over it instead, from where the bot stands.
/// </summary>
/// <remarks>
/// <c>gg2-rl</c> <c>docs/PLAN.md</c>, "Navigation by search". From a standing
/// start <see cref="EdgeSearch"/> finds a plan for most of the edges the
/// follower strands on, so a stranding is mostly the follower's fault and not
/// the edge's.
/// <para>
/// ⚠️ <b>The search runs from a state the bot holds still</b>: it lets go of the
/// keys and waits to stand still on the ground, searches across ticks while it
/// stands, then plays the plan from exactly that state. A bot that moves while
/// it searches, or leaves the plan's predicted track by more than
/// <see cref="RescueTolerance"/> while it plays (a hit, a push, a key the combat
/// layer added), bans the edge as it would have.
/// </para>
/// </remarks>
public sealed partial class PathFollower
{
    /// <summary>Ticks a rescue waits for the bot to stand still.</summary>
    private const int RescueSettleTicks = 30;

    /// <summary>The rescue search's beam and its cap in ticks.</summary>
    private const int RescueBeam = 100;

    /// <inheritdoc cref="RescueBeam"/>
    private const int RescueCapTicks = 200;

    /// <summary>
    /// States the rescue search steps a tick: counted, not timed, so the bot
    /// layer stays deterministic.
    /// </summary>
    private const int RescueBudget = 2000;

    /// <summary>How far, in pixels, the bot may stray from the plan's track.</summary>
    private const double RescueTolerance = 0.5;

    /// <summary>
    /// Whether a stranding is searched before the edge is banned (NS3). Off by
    /// default; needs <see cref="Collision"/>, and does nothing without it.
    /// </summary>
    public bool Rescues { get; set; }

    /// <summary>
    /// Starts a rescue of <paramref name="from"/> → <paramref name="to"/> in
    /// place of its ban, and says whether it did.
    /// </summary>
    /// <remarks>
    /// Walks, falls, jumps and double jumps only: a rocket or sticky jump's
    /// launch is a weapon the search does not fire, and a drop-through needs a
    /// key it does not press. At most once per edge per ban length, so an edge
    /// a rescue cannot fly is banned the next time, as before.
    /// </remarks>
    private bool TryRescue(BotPathState state, int from, int to, int tick)
    {
        if (!Rescues || Collision is null || state.Rescuing || from < 0 || to < 0)
            return false;

        if (!TryFindEdge(_graph, state, from, to, out var edge)
            || edge.Type is not (NavEdgeType.Walk or NavEdgeType.Fall or NavEdgeType.Jump or NavEdgeType.DoubleJump))
        {
            return false;
        }

        var key = NavGraph.EdgeKey(from, to);
        if (state.RescuedAt.TryGetValue(key, out var at) && tick - at < BotConstants.BlacklistTicks)
            return false;

        if (state.RescuedAt.Count >= 64)
        {
            foreach (var (old, when) in state.RescuedAt)
            {
                if (tick - when >= BotConstants.BlacklistTicks)
                    state.RescuedAt.Remove(old);
            }
        }

        state.RescuedAt[key] = tick;
        state.Rescues++;
        state.EndRescue();
        state.RescueFrom = from;
        state.RescueTo = to;
        return true;
    }

    /// <summary>
    /// A tick of a running rescue: wait to stand still, search, or play. Returns
    /// true with the keys while it runs or as it gives up; false once it has
    /// arrived this tick and the follower should carry on from a fresh plan.
    /// </summary>
    private bool Rescue(
        BotPathState state, in CharacterState character, int tick,
        in NavQuery query, BotOccupancy? occupancy, out PlayerKeys keys)
    {
        keys = PlayerKeys.None;
        var x = character.X;
        var y = character.Y;

        if (state.RescuePlan is null)
        {
            if (state.RescueSearch is null)
            {
                if (!(character.OnGround && character.HSpeed == 0 && character.VSpeed == 0))
                {
                    if (++state.RescueSettle <= RescueSettleTicks)
                        return true;

                    state.RescuesUnsettled++;
                    return GiveUp(state, x, y, tick, query, occupancy);
                }

                var to = state.RescueTo;
                var goalX = NavGraph.ColumnWorldX((_graph.Nodes[to].X0 + _graph.Nodes[to].X1) / 2);
                state.RescueX = x;
                state.RescueY = y;
                state.RescueSearch = new EdgeSearch.Run(
                    Collision!, _graph, in character, to, goalX, RescueCapTicks, RescueBeam, stop: false);
            }

            // Searched from this exact state, so the bot must still be in it.
            if (x != state.RescueX || y != state.RescueY || character.HSpeed != 0 || !character.OnGround)
            {
                state.RescuesMoved++;
                return GiveUp(state, x, y, tick, query, occupancy);
            }

            var run = state.RescueSearch;
            var before = run.Expanded;
            var done = run.Advance(RescueBudget);
            state.RescueSearchTicks++;
            state.RescueStates += run.Expanded - before;

            if (!done)
                return true;

            if (!run.Result.Found)
                return GiveUp(state, x, y, tick, query, occupancy);

            state.RescuesFound++;
            state.RescuePlan = run.Result.Plan;
            state.RescueTrack = EdgeSearch.Trace(Collision!, in character, run.Result.Plan);
            state.RescueAt = 0;
            state.RescueSearch = null;
        }

        if (state.RescueAt > 0)
        {
            var (px, py) = state.RescueTrack![state.RescueAt - 1];

            if (Math.Abs(x - px) > RescueTolerance || Math.Abs(y - py) > RescueTolerance)
            {
                state.RescuesAbandoned++;
                return GiveUp(state, x, y, tick, query, occupancy);
            }
        }

        if (state.RescueAt == state.RescuePlan.Length)
        {
            state.RescuesArrived++;
            state.EndRescue();
            Plan(state, x, y, tick, query, occupancy);
            return false;
        }

        keys = state.RescuePlan[state.RescueAt++] & (PlayerKeys.Left | PlayerKeys.Right | PlayerKeys.Jump);
        return true;
    }

    // The ban the rescue stood in for, and a re-plan without the edge.
    private bool GiveUp(BotPathState state, double x, double y, int tick, in NavQuery query, BotOccupancy? occupancy)
    {
        state.BlacklistEdge(state.RescueFrom, state.RescueTo, tick);
        state.EndRescue();
        Plan(state, x, y, tick, query, occupancy);
        return true;
    }
}
