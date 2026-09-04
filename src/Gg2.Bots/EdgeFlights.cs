using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>How a single flight of one real nav edge ended.</summary>
/// <remarks>
/// <b>The first five are SKIPS, not refusals, and keeping them apart is the
/// whole point of this type.</b> A harness whose goal A* routes to another way,
/// or whose hop the follower would steer as some other edge, measures a
/// different edge and then reports the answer against the edge it meant to
/// measure — which is how "zero sticky jumps across 48 real edges" was quoted
/// for a fault that was neither the arm's nor those edges'.
/// <para>
/// <b>Only <see cref="RoutedAround"/> and <see cref="NoRoute"/> actually fire
/// on today's caches</b>, and the other three are the harness refusing to guess
/// — see <c>EdgeFlightTests</c>, which pins both of the zeroes rather than
/// leaving them as an assumption.
/// </para>
/// </remarks>
public enum EdgeFlightOutcome
{
    /// <summary>
    /// The from-node's own midpoint does not resolve back to the from-node.
    /// </summary>
    /// <remarks>
    /// <b>It cannot fire for a midpoint start, and that is a proof rather
    /// than a measurement.</b> <see cref="NavGraph.NodeFromWorld"/> ranks
    /// <c>dy * 4 + dx</c>; a node's own stand y gives it <c>dy = 0</c> exactly,
    /// its own midpoint column gives <c>dx = 0</c>, and no second node can hold
    /// that column on that row — so rank 0 is unique. It is kept because the
    /// start position is the one thing about this harness somebody will want to
    /// change, and a start that is not inside the from-node's span silently
    /// measures a different edge.
    /// </remarks>
    StartUnresolved,

    /// <summary>The to-node's midpoint does not snap back to the to-node.</summary>
    GoalUnresolved,

    /// <summary>The search found no route at all from the from-node to the to-node.</summary>
    NoRoute,

    /// <summary>
    /// A route exists but it is not this one hop — A* went round.
    /// </summary>
    /// <remarks>
    /// <b>Not a refusal.</b> The search costs a jump the same as anything
    /// else, so a two-second walk around a ledge routinely beats the arc over it;
    /// on <c>koth_harvest</c> that is how four of four "flights" arrived without
    /// leaving the ground. The edge is untestable through a route, so it is
    /// skipped rather than counted.
    /// </remarks>
    RoutedAround,

    /// <summary>
    /// The hop is on the route, but a cheaper parallel edge of another kind is
    /// what the follower would steer.
    /// </summary>
    /// <remarks>
    /// The item-A shape, from the other side: 11,615 of the shipped graphs'
    /// 104,087 node pairs carry more than one edge kind. Flying the pair does not
    /// fly this edge, so the result says nothing about it.
    /// <para>
    /// <b>Zero across all 22,528 arc edges of the shipped and sticky caches,
    /// on both teams</b> — no pair carries a strictly <em>cheaper</em> routable
    /// edge of another kind. That is not the same as "parallel edges are
    /// harmless": item A's bug was picking among them by <b>index order</b>, and
    /// cost order merely happens to agree with the arc today. A generator that
    /// re-costs anything can break the coincidence without breaking anything
    /// else.
    /// </para>
    /// </remarks>
    Shadowed,

    /// <summary>The bot arrived on the to-node inside the budget.</summary>
    Arrived,

    /// <summary>The bot did not arrive. This is the only outcome that is a refusal.</summary>
    Stranded,
}

/// <summary>What one flight measured.</summary>
/// <param name="Edge">The edge's index in <see cref="NavGraph.Edges"/>.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Ticks">Ticks to arrival, or the budget if it never did.</param>
/// <param name="Manoeuvres">
/// How many times the arm under test actually fired — mine jumps for a
/// <see cref="NavEdgeType.StickyJump"/>, rocket jumps for a
/// <see cref="NavEdgeType.RocketJump"/>, air jumps for a
/// <see cref="NavEdgeType.DoubleJump"/>, and zero for the kinds that have no
/// counter. <b>Arriving is not flying</b>: two nodes a jump apart are often
/// close enough to walk between, so an arrival with zero manoeuvres is an
/// arrival the arm had no part in — eight of <c>gg_debug</c>'s twenty measurable
/// rocket jumps, and 114 of <c>ctf_truefort</c>'s 328.
/// <para>
/// <b>It counts what the follower ASKED for, not what the weapon fired.</b>
/// <see cref="BotPathState"/>'s counters are incremented inside the takeoff
/// gate, so a driver that drops <see cref="PathFollower.ApplyLaunch"/> still
/// reports manoeuvres: <c>gg_debug</c> went from 20 arrivals and 12 flights to
/// 16 and 8, not to zero. The arrivals are the half that collapses.
/// </para>
/// </param>
/// <param name="Blacklists">
/// Edges the bot gave up on. <b>Not expected to be zero</b> — the off-route
/// machinery gives an arc one retry before banning it, and two of
/// <c>gg_debug</c>'s twenty measurable rocket jumps use it and still arrive. It
/// is a cost to watch rather than a fault.
/// </param>
/// <param name="MissX">How far from the goal it stopped, in pixels.</param>
public readonly record struct EdgeFlight(
    int Edge,
    NavEdgeType Type,
    int From,
    int To,
    EdgeFlightOutcome Outcome,
    int Ticks,
    int Manoeuvres,
    int Blacklists,
    double MissX)
{
    /// <summary>Whether this flight actually posed the question.</summary>
    public bool Measured => Outcome is EdgeFlightOutcome.Arrived or EdgeFlightOutcome.Stranded;

    /// <summary>Whether the edge was flown under its own arm and arrived.</summary>
    public bool Flown => Outcome == EdgeFlightOutcome.Arrived && Manoeuvres > 0;

    public override string ToString() =>
        $"e{Edge} {Type} {From}->{To} {Outcome} t={Ticks} x{Manoeuvres} bl={Blacklists} miss={MissX:0.#}";
}

/// <summary>
/// Flies one real nav edge at a time, on a real map, under the real follower.
/// </summary>
/// <remarks>
/// <b>Why this is a class and not a test.</b> A cache full of a new edge kind is
/// only worth what a bot can actually do with it, and the question "how many of
/// these 4,132 arcs does the follower fly" is asked by whoever generated them —
/// <c>gg2-nav-gen</c> is a separate lane whose output is not checked in here.
/// So the apparatus lives in the library beside <see cref="ScenarioWorld"/>, the
/// gates in the test project point it at the shipped caches, and a sweep of
/// somebody else's cache is one call rather than a rewrite.
/// <para>
/// <b>Three premises, asserted rather than assumed.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>Start at the from-node's MIDPOINT and check it resolves.</b>
/// The takeoff column resolves to the from-node on all 18,396 arc edges of the
/// shipped caches. The midpoint is used because it is <em>provably</em> the
/// from-node (<see cref="EdgeFlightOutcome.StartUnresolved"/>) where the takeoff
/// column only happens to be, and the check stays because the start position is
/// the thing somebody will change.
/// </item>
/// <item>
/// <b>Check the planned route IS this hop.</b> A* costs a jump the same as a
/// walk, so it goes round whenever going round is shorter — and it is the
/// dominant outcome rather than an edge case: <b>47 of <c>gg_debug</c>'s 67
/// rocket jumps and 155 of <c>koth_harvest</c>'s 275 sticky jumps</b>. Driven
/// anyway, each of those is a bot walking somewhere while a counter says the arc
/// was attempted; with the check removed <c>gg_debug</c> reports 67 arrivals out
/// of 67 against 12 real flights.
/// </item>
/// <item>
/// <b>Check the follower would steer THIS edge.</b> A node pair can carry a
/// rocket jump beside a sticky one, and the cheaper wins — see
/// <see cref="PathFollower.TryResolveHop"/>.
/// </item>
/// </list>
/// <para>
/// <b>The bot is driven with the launch hand-off</b>
/// (<see cref="PathFollower.ApplyLaunch"/>), not with the follower's movement
/// keys alone. A harness that keeps only LEFT / RIGHT / JUMP / DOWN gets a
/// follower that requests a rocket or a mine and never fires it, and every arc
/// then reads as refused.
/// </para>
/// </remarks>
public static class EdgeFlights
{
    /// <summary>
    /// Ticks a single hop is given before it counts as stranded.
    /// </summary>
    /// <remarks>
    /// Generous on purpose: the longest arc <c>gg2-nav-gen</c> emits is capped at
    /// <c>NAV_SJ_MAX_TICKS</c> (100) and a mine jump adds
    /// <see cref="NavConstants.SjSetupTicks"/> before it, but the bot also has to
    /// walk from the node's midpoint to the takeoff column, and
    /// <c>BOT_TAKEOFF_PATIENCE</c> plus a re-plan can legitimately cost it two
    /// approaches. It is a budget, not a measurement — nothing should be
    /// pinned to it, and a flight that needs most of it is a flight worth looking
    /// at.
    /// </remarks>
    public const int DefaultBudget = 400;

    /// <summary>
    /// Which class is asked to fly an edge of this kind.
    /// </summary>
    /// <remarks>
    /// Not a preference — for two of these it is the only class that can.
    /// <see cref="RocketModel.Ready"/> wants a rocket launcher and
    /// <see cref="StickyModel.Ready"/> a Minegun, so a demoman on a rocket-jump
    /// edge refuses to leave the ground and the refusal says nothing about the
    /// edge.
    /// </remarks>
    public static PlayerClass ClassFor(NavEdgeType type) => type switch
    {
        NavEdgeType.RocketJump => PlayerClass.Soldier,
        NavEdgeType.StickyJump => PlayerClass.Demoman,
        NavEdgeType.DoubleJump => PlayerClass.Scout,
        _ => PlayerClass.Soldier,
    };

    /// <summary>
    /// The query that class asks the search with.
    /// </summary>
    /// <remarks>
    /// <b><see cref="NavQuery.CanStickyJump"/> has no live derivation and that
    /// is deliberate, not an omission.</b> <see cref="ScriptedBot"/> rebuilds
    /// <see cref="NavQuery.CanDoubleJump"/> and <see cref="NavQuery.CanRocketJump"/>
    /// from the character every tick and never touches the sticky flag, because
    /// the shipped caches carry no type-7 edge for it to matter to — they are
    /// generated only behind a flag, into a cache the stock game never reads
    /// (<see cref="NavQuery.CanAffordStickyJump"/>). So it comes from the caller
    /// here, and a sticky sweep has to say so.
    /// </remarks>
    public static NavQuery QueryFor(NavEdgeType type, Team team) =>
        new(
            team,
            CanDoubleJump: BotClassProfile.AirJump(ClassFor(type)),
            CanRocketJump: type == NavEdgeType.RocketJump,
            CanStickyJump: type == NavEdgeType.StickyJump);

    /// <summary>
    /// Puts a bot on one end of an edge, sends it to the other, and reports what
    /// happened.
    /// </summary>
    /// <param name="world">
    /// A <b>fresh</b> world. One flight leaves a character, a spent weapon and
    /// possibly a live mine behind it, so re-using a world makes the second
    /// flight a measurement of the first one's litter.
    /// </param>
    /// <param name="edgeIndex">Which edge of <c>world.Nav.Edges</c> to fly.</param>
    /// <param name="team">Which team the bot is on, for the gates.</param>
    /// <param name="budget">Ticks before it counts as stranded.</param>
    public static EdgeFlight Fly(ScenarioWorld world, int edgeIndex, Team team = Team.Red, int budget = DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(world);

        var graph = world.Nav
            ?? throw new InvalidOperationException("a flight needs a nav graph; this world has none");

        var edge = graph.Edges[edgeIndex];
        var query = QueryFor(edge.Type, team);
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        EdgeFlight Skipped(EdgeFlightOutcome outcome) =>
            new(edgeIndex, edge.Type, edge.From, edge.To, outcome, 0, 0, 0, double.NaN);

        // Premise 1. The midpoint rather than the takeoff column, and checked
        // rather than trusted — NodeFromWorld ranks height above distance and a
        // node under an overhang can lose its own middle to the one above it.
        var startX = Midpoint(graph, edge.From);
        var startY = graph.StandY(edge.From);

        if (graph.NodeFromWorld(startX, startY) != edge.From)
            return Skipped(EdgeFlightOutcome.StartUnresolved);

        // Premise 1b, the same check on the other end: SetGoal snaps, and a goal
        // that snaps somewhere else is a route to somewhere else.
        if (!follower.SetGoal(state, Midpoint(graph, edge.To), graph.StandY(edge.To))
            || graph.NodeFromWorld(state.GoalX, state.GoalY) != edge.To)
        {
            return Skipped(EdgeFlightOutcome.GoalUnresolved);
        }

        // Premises 2 and 3, and they are asked of the follower's own plan rather
        // than of a search run beside it — the route a bot is handed is the one
        // that matters, PlanQuery included.
        if (!follower.Plan(state, startX, startY, tick: 0, in query))
            return Skipped(EdgeFlightOutcome.NoRoute);

        if (state.Path.Count != 2 || state.Path[0] != edge.From || state.Path[1] != edge.To)
            return Skipped(EdgeFlightOutcome.RoutedAround);

        if (!follower.TryResolveHop(state, edge.From, edge.To, out var steered)
            || steered != edge)
        {
            return Skipped(EdgeFlightOutcome.Shadowed);
        }

        // The plan above was a dry run against coordinates; the bot has to be
        // planned for from where it will actually stand. Re-planning from a fresh
        // state is cheaper to reason about than reconciling the two, and the
        // follower plans on its first Step anyway because SetGoal zeroes ReplanAt.
        var flying = new BotPathState();
        follower.SetGoal(flying, Midpoint(graph, edge.To), graph.StandY(edge.To));

        var slot = world.Place(ClassFor(edge.Type), team, startX, startY);
        world.Drive(slot, new Pilot(follower, flying, query));

        var ticks = 0;
        for (; ticks < budget && !flying.Arrived; ticks++)
            world.Step();

        var miss = Math.Abs(world.State.Characters[slot].X - flying.GoalX);

        return new EdgeFlight(
            edgeIndex,
            edge.Type,
            edge.From,
            edge.To,
            flying.Arrived ? EdgeFlightOutcome.Arrived : EdgeFlightOutcome.Stranded,
            ticks,
            Manoeuvres(flying, edge.Type),
            flying.BlacklistFires,
            miss);
    }

    /// <summary>
    /// Flies every edge of one kind on one map, newest world each time.
    /// </summary>
    /// <param name="world">
    /// Builds a fresh world. Called once to read the graph and once per flight —
    /// see <see cref="Fly"/> on why a world is not re-used.
    /// </param>
    /// <param name="type">The edge kind to sweep.</param>
    /// <param name="team">Which team the bot is on.</param>
    /// <param name="limit">
    /// How many edges to fly at most, or 0 for all of them. A sweep rebuilds
    /// the map and its collision world per flight, so a whole cache is minutes
    /// rather than seconds — a gate wants a limit and a measurement does not.
    /// </param>
    /// <param name="budget">Ticks per flight.</param>
    public static List<EdgeFlight> Sweep(
        Func<ScenarioWorld> world,
        NavEdgeType type,
        Team team = Team.Red,
        int limit = 0,
        int budget = DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(world);

        var graph = world().Nav
            ?? throw new InvalidOperationException("a sweep needs a nav graph; this world has none");

        var flights = new List<EdgeFlight>();

        for (var i = 0; i < graph.EdgeCount; i++)
        {
            if (graph.Edges[i].Type != type)
                continue;

            if (limit > 0 && flights.Count >= limit)
                break;

            flights.Add(Fly(world(), i, team, budget));
        }

        return flights;
    }

    /// <summary>A one-line tally, for a test's output or a tool's stdout.</summary>
    public static string Summarise(IReadOnlyList<EdgeFlight> flights)
    {
        ArgumentNullException.ThrowIfNull(flights);

        var measured = 0;
        var arrived = 0;
        var flown = 0;
        var skips = new Dictionary<EdgeFlightOutcome, int>();

        foreach (var flight in flights)
        {
            if (flight.Measured)
            {
                measured++;
                if (flight.Outcome == EdgeFlightOutcome.Arrived)
                    arrived++;
                if (flight.Flown)
                    flown++;
            }
            else
            {
                skips[flight.Outcome] = skips.GetValueOrDefault(flight.Outcome) + 1;
            }
        }

        var detail = skips.Count == 0
            ? "none"
            : string.Join(", ", skips.OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value}"));

        return $"{flights.Count} candidates, {measured} measured, {arrived} arrived, {flown} flown; skipped: {detail}";
    }

    /// <summary>The world x of a node's middle — where a bot standing on it sits.</summary>
    /// <remarks>
    /// <b>The middle, not <see cref="NavEdge.Takeoff"/>.</b> The takeoff column
    /// is where the arc was proved from and is the obvious place to start from;
    /// on every shipped arc it also sits inside the from-node's span, so the two
    /// agree today. The middle is used because it is the answer that cannot stop
    /// agreeing — see <see cref="EdgeFlightOutcome.StartUnresolved"/> — and
    /// because it costs the bot the short walk to the takeoff column that a
    /// routed bot does anyway, which is the approach the arc's own run-up credit
    /// assumes.
    /// </remarks>
    private static double Midpoint(NavGraph graph, int node) =>
        NavGraph.ColumnWorldX((graph.Nodes[node].X0 + graph.Nodes[node].X1) / 2);

    private static int Manoeuvres(BotPathState state, NavEdgeType type) => type switch
    {
        NavEdgeType.RocketJump => state.RocketJumps,
        NavEdgeType.StickyJump => state.StickyJumps,
        NavEdgeType.DoubleJump => state.DoubleJumps,
        _ => 0,
    };

    /// <summary>
    /// The follower's keys plus the launch hand-off, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="ScriptedBot"/>: the combat half would pick
    /// targets, hold position and dodge, and a flight is about the route. What it
    /// does copy is the two things a route genuinely needs from outside —
    /// <see cref="PathFollower.ApplyLaunch"/>, and the live rebuild of the query's
    /// class arcs, which is where a soldier's rocket jumps go as its hp drops.
    /// </remarks>
    private sealed class Pilot(PathFollower follower, BotPathState state, NavQuery query) : IInputSource
    {
        private NavQuery _query = query;

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            if (!world.IsAlive(slot))
                return PlayerInput.None;

            ref readonly var self = ref world.Character(slot);

            // Rebuilt from live state every tick, exactly as ScriptedBot does.
            // CanRocketJump is the weapon plus an hp test, so a soldier that has
            // just cost itself 30hp sheds its rocket jumps mid-route — and
            // BotPathState.PlanQuery is what stops that unplanning the arc it is
            // already in.
            _query = _query with
            {
                CanDoubleJump = BotClassProfile.AirJump(self.Class),
                CanRocketJump = RocketModel.Ready(in self, in world.Weapon(slot)),
            };

            var keys = follower.Step(state, in self, in world.Weapon(slot), tick, _query);

            var aim = 0.0;
            PathFollower.ApplyLaunch(state, ref keys, ref aim);

            return PlayerInput.FromAim(keys, aim, 0);
        }
    }
}
