using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

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

    /// <summary>
    /// Neither team can put a body on the from-node's stand position.
    /// </summary>
    /// <remarks>
    /// <b>A team gate is solid to the other team, and a node inside one is a
    /// place that team can never stand.</b> Measured 2026-09-05: 148 flights were
    /// being scored <c>Stranded</c> for this — a red bot placed inside blue's
    /// spawn gate, unable to move in either direction for the whole budget, on a
    /// node every one of which a blue bot walks off freely. So
    /// <see cref="Fly"/> flies an edge as a team that can actually be there
    /// (<see cref="EdgeFlight.TeamSwapped"/>) and this outcome is only for a start
    /// NEITHER team can occupy — which no shipped graph currently produces.
    /// <para>
    /// ⚠️ <b>It is checked against the body, not the graph.</b>
    /// <see cref="StartUnresolved"/> asks whether the nav graph agrees the start
    /// is the from-node; this asks whether the simulation will let a character be
    /// there at all, which is a different question and was the one nobody was
    /// asking.
    /// </para>
    /// </remarks>
    StartBlocked,

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
/// arrival the arm had no part in — nine of <c>gg_debug</c>'s twenty-one
/// measurable rocket jumps, and 158 of <c>ctf_truefort</c>'s 377
/// (both re-measured 2026-09-05, no tables).
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
/// <param name="Team">
/// The team that actually flew it, which is not always the one asked for — see
/// <paramref name="TeamSwapped"/>.
/// </param>
/// <param name="TeamSwapped">
/// The requested team could not stand on the from-node, so the other one flew
/// it. <b>Visible rather than silent</b>: a harness that quietly changes who is
/// flying is a harness measuring something the caller did not ask for.
/// </param>
/// <param name="RanEdge">
/// Ticks the follower spent steering <em>this</em> edge.
/// </param>
/// <remarks>
/// ⛔ <b>Zero with an arrival is the flight going somewhere else.</b> The
/// premises are checked before the bot moves — the plan IS the single hop — but
/// the follower re-plans while it flies, and a re-plan can replace that hop with a
/// multi-hop walk to the same node. It then arrives, and without this counter the
/// arc is credited for a walk it had no part in. Measured over 591 arc arrivals:
/// <b>117 (19.8%) never ran the edge they were credited to</b>, every one of them a
/// rocket jump, walking chains of four and five ordinary edges instead.
/// <para>
/// This is <see cref="EdgeFlightOutcome.RoutedAround"/>'s shape arriving a second
/// time, from inside the flight rather than from the plan — which is why the check
/// could not live with the other premises.
/// </para>
/// </remarks>
public readonly record struct EdgeFlight(
    int Edge,
    NavEdgeType Type,
    int From,
    int To,
    EdgeFlightOutcome Outcome,
    int Ticks,
    int Manoeuvres,
    int Blacklists,
    double MissX,
    Team Team = Team.Red,
    bool TeamSwapped = false,
    int RanEdge = 0)
{
    /// <summary>Whether this flight actually posed the question.</summary>
    public bool Measured => Outcome is EdgeFlightOutcome.Arrived or EdgeFlightOutcome.Stranded;

    /// <summary>
    /// Arrived, under this edge's own arm, having actually steered this edge.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>All three clauses are load-bearing and each was added after the
    /// previous definition was caught crediting something.</b> Arriving is not
    /// flying — two nodes a jump apart are often walkable. Firing is not arriving
    /// — the counter is incremented by the takeoff gate, not by the weapon. And
    /// <see cref="RanEdge"/> is the third: an arrival that never steered this edge
    /// walked some other way to the same node.
    /// </remarks>
    public bool Flown => Outcome == EdgeFlightOutcome.Arrived && Manoeuvres > 0 && RanEdge > 0;

    /// <summary>Arrived at the node without ever steering this edge.</summary>
    public bool Diverted => Outcome == EdgeFlightOutcome.Arrived && RanEdge == 0;

    public override string ToString() =>
        $"e{Edge} {Type} {From}->{To} {Outcome} t={Ticks} x{Manoeuvres} bl={Blacklists} "
        + $"miss={MissX:0.#} ran={RanEdge}{(TeamSwapped ? $" as {Team}" : string.Empty)}";
}

/// <summary>The counts a sweep is read by.</summary>
/// <param name="Candidates">Edges of the wanted kinds that were considered.</param>
/// <param name="Measured">
/// Flights that actually posed the question — arrived plus stranded.
/// <b>This is the denominator</b>, and it is a minority of the candidates: the
/// skips below are 68% of the shipped corpus.
/// </param>
/// <param name="Arrived">Reached the to-node inside the budget.</param>
/// <param name="Flown">
/// Arrived with the arm having fired at least once. <b>Arriving is not
/// flying</b> — see <see cref="EdgeFlight.Manoeuvres"/>.
/// </param>
/// <param name="Stranded">
/// Did not arrive. ⚠️ <b>Relative to the budget</b>, and the two must be quoted
/// together.
/// </param>
/// <param name="RoutedAround">A* preferred a longer way, so the hop is untestable.</param>
/// <param name="Shadowed">A cheaper parallel edge is what the follower would steer.</param>
/// <param name="NoRoute">No route between the two nodes at all — usually an island.</param>
/// <param name="StartUnresolved">The from-node's midpoint is not the from-node.</param>
/// <param name="GoalUnresolved">The to-node's midpoint does not snap back.</param>
/// <param name="Ticks">Simulated ticks across the measured flights.</param>
/// <param name="Blacklists">Edges given up on across the measured flights.</param>
/// <param name="StartBlocked">Starts no team can occupy.</param>
/// <param name="Diverted">
/// Arrivals that never steered the edge they were posed — the bot re-planned
/// mid-flight and walked to the node another way. <b>These are counted inside
/// <see cref="Arrived"/> and excluded from <see cref="Flown"/></b>, so the two
/// numbers disagree by design and the gap is this.
/// </param>
/// <param name="TeamSwapped">
/// Flights the other team had to fly because the requested one cannot stand on
/// the from-node. <b>Pinned</b>, because it is a fact about the graph's gates
/// rather than about the follower, and a change in it means the gate structure
/// moved.
/// </param>
public readonly record struct EdgeFlightTally(
    int Candidates,
    int Measured,
    int Arrived,
    int Flown,
    int Stranded,
    int RoutedAround,
    int Shadowed,
    int NoRoute,
    int StartUnresolved,
    int GoalUnresolved,
    long Ticks,
    long Blacklists,
    int StartBlocked = 0,
    int TeamSwapped = 0,
    int Diverted = 0)
{
    /// <summary>Counts one set of flights.</summary>
    public static EdgeFlightTally Of(IEnumerable<EdgeFlight> flights)
    {
        ArgumentNullException.ThrowIfNull(flights);

        var tally = default(EdgeFlightTally);

        foreach (var flight in flights)
            tally = tally.Add(in flight);

        return tally;
    }

    /// <summary>This tally with one more flight in it.</summary>
    public EdgeFlightTally Add(in EdgeFlight flight) => this with
    {
        Candidates = Candidates + 1,
        Measured = Measured + (flight.Measured ? 1 : 0),
        Arrived = Arrived + (flight.Outcome == EdgeFlightOutcome.Arrived ? 1 : 0),
        Flown = Flown + (flight.Flown ? 1 : 0),
        Stranded = Stranded + (flight.Outcome == EdgeFlightOutcome.Stranded ? 1 : 0),
        RoutedAround = RoutedAround + (flight.Outcome == EdgeFlightOutcome.RoutedAround ? 1 : 0),
        Shadowed = Shadowed + (flight.Outcome == EdgeFlightOutcome.Shadowed ? 1 : 0),
        NoRoute = NoRoute + (flight.Outcome == EdgeFlightOutcome.NoRoute ? 1 : 0),
        StartUnresolved = StartUnresolved + (flight.Outcome == EdgeFlightOutcome.StartUnresolved ? 1 : 0),
        GoalUnresolved = GoalUnresolved + (flight.Outcome == EdgeFlightOutcome.GoalUnresolved ? 1 : 0),
        Ticks = Ticks + (flight.Measured ? flight.Ticks : 0),
        Blacklists = Blacklists + (flight.Measured ? flight.Blacklists : 0),
        StartBlocked = StartBlocked + (flight.Outcome == EdgeFlightOutcome.StartBlocked ? 1 : 0),
        TeamSwapped = TeamSwapped + (flight.TeamSwapped ? 1 : 0),
        Diverted = Diverted + (flight.Diverted ? 1 : 0),
    };

    /// <summary>
    /// Every field, because this is what a failed pin prints.
    /// </summary>
    /// <remarks>
    /// <b>Ticks and blacklists are in here deliberately.</b> A gate that asserts
    /// the whole tally reports its diff through this string, and a re-record
    /// cannot be judged from counts whose cost half is missing — a change that
    /// leaves every outcome identical while doubling the ticks is exactly the
    /// kind that should be looked at rather than waved through.
    /// </remarks>
    public override string ToString() =>
        $"cand={Candidates} measured={Measured} arrived={Arrived} flown={Flown} "
        + $"stranded={Stranded} | routed-around={RoutedAround} shadowed={Shadowed} "
        + $"no-route={NoRoute} unresolved={StartUnresolved + GoalUnresolved} "
        + $"start-blocked={StartBlocked} team-swapped={TeamSwapped} diverted={Diverted} "
        + $"| ticks={Ticks} blacklists={Blacklists}";
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
    /// <param name="distances">
    /// The next-hop tables the search plans with, or null for the weighted
    /// straight-line heuristic.
    /// </param>
    /// <remarks>
    /// ⚠️ <b><paramref name="distances"/> selects the ARM, not the speed.</b>
    /// The search is weighted, so the exact heuristic picks the route as well as
    /// the time it takes to find one — and which single hops A* prefers is
    /// exactly what <see cref="EdgeFlightOutcome.RoutedAround"/> counts. A sweep
    /// with no table measures the arm <see cref="ScenarioWorld"/> has always
    /// measured; a sweep with one measures what a live server and <c>eval</c>
    /// run (docs/TRAPS-BOTS.md, "The gates measure a DIFFERENT ARM").
    /// <b>Null is the default so that every pre-existing caller keeps measuring
    /// what it measured before</b>, which is why the parameter is last and
    /// optional rather than required.
    /// </remarks>
    public static EdgeFlight Fly(
        ScenarioWorld world,
        int edgeIndex,
        Team team = Team.Red,
        int budget = DefaultBudget,
        NavDistanceSet? distances = null,
        bool warmWeapon = false,
        bool heldFlight = true,
        Action<int, ScenarioWorld, BotPathState>? onTick = null,
        PlayerClass? flyAs = null,
        bool heldDoubles = true)
    {
        ArgumentNullException.ThrowIfNull(world);

        var graph = world.Nav
            ?? throw new InvalidOperationException("a flight needs a nav graph; this world has none");

        var edge = graph.Edges[edgeIndex];
        var follower = new PathFollower(graph)
        {
            Distances = distances, Collision = world.Collision, HeldFlight = heldFlight, HeldDoubles = heldDoubles,
        };
        var cls = flyAs ?? ClassFor(edge.Type);
        var state = new BotPathState();

        EdgeFlight Skipped(EdgeFlightOutcome outcome) =>
            new(edgeIndex, edge.Type, edge.From, edge.To, outcome, 0, 0, 0, double.NaN, team, false, 0);

        // Premise 1. The midpoint rather than the takeoff column, and checked
        // rather than trusted — NodeFromWorld ranks height above distance and a
        // node under an overhang can lose its own middle to the one above it.
        var startX = Midpoint(graph, edge.From);
        var startY = StandingY(graph, edge.From);

        if (graph.NodeFromWorld(startX, startY) != edge.From)
            return Skipped(EdgeFlightOutcome.StartUnresolved);

        // Premise 1c, and it is asked of the SIMULATION rather than of the graph:
        // a team gate is solid to the other team, so a node inside one is a place
        // that team can never be. Fly it as somebody who can — see
        // EdgeFlightOutcome.StartBlocked for the 148 flights this was costing.
        var box = CharacterBox.For(cls);
        var flying = team;
        var swapped = false;

        if (!world.Collision.PlaceFree(box, startX, startY, FilterFor(team)))
        {
            var other = team == Team.Red ? Team.Blue : Team.Red;

            if (!world.Collision.PlaceFree(box, startX, startY, FilterFor(other)))
                return Skipped(EdgeFlightOutcome.StartBlocked);

            flying = other;
            swapped = true;
        }

        var query = QueryFor(edge.Type, flying) with { CanDoubleJump = BotClassProfile.AirJump(cls) };

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
        var flightState = new BotPathState();
        follower.SetGoal(flightState, Midpoint(graph, edge.To), graph.StandY(edge.To));

        var slot = world.Place(cls, flying, startX, startY);

        // ⛔ OFF by default, and the default is the pinned arm. `ScenarioWorld.Place`
        // spawns a character, and `Weapon.events/Create.xml` starts a fresh weapon
        // with ReadyToShoot FALSE and the refire alarm already running — so every
        // rocket-jump flight in this sweep begins with a launcher that is roughly
        // 45 ticks from being able to fire, which a bot that has been alive in a
        // match for any time at all is not. Whether that costs the corpus anything
        // is a MEASUREMENT and not an assumption, which is what this switch is for.
        if (warmWeapon)
        {
            world.State.Weapons[slot].ReadyToShoot = true;
            world.State.Weapons[slot].RefireTimer = 0;
        }

        world.Drive(slot, new Pilot(follower, flightState, query));

        var ticks = 0;
        var ran = 0;

        for (; ticks < budget && !flightState.Arrived; ticks++)
        {
            world.Step();
            onTick?.Invoke(slot, world, flightState);

            // Which edge the follower is steering RIGHT NOW. A re-plan mid-flight
            // can put the bot on a different route to the same node, and the
            // premises — checked before it moved — cannot see that.
            if (flightState.EdgeFrom == edge.From && flightState.EdgeTo == edge.To)
                ran++;
        }

        var miss = Math.Abs(world.State.Characters[slot].X - flightState.GoalX);

        return new EdgeFlight(
            edgeIndex,
            edge.Type,
            edge.From,
            edge.To,
            flightState.Arrived ? EdgeFlightOutcome.Arrived : EdgeFlightOutcome.Stranded,
            ticks,
            Manoeuvres(flightState, edge.Type),
            flightState.BlacklistFires,
            miss,
            flying,
            swapped,
            ran);
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
    /// How many edges to fly at most, or 0 for all of them.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>The limit is for narrowing a question, not for affording one.</b>
    /// This parameter's own documentation used to say a whole cache was "minutes
    /// rather than seconds"; measured 2026-09-05 it is <b>5.0 s for all 116,316
    /// edges of all 25 shipped graphs</b>, serial in Release, because
    /// <see cref="ScenarioResources"/> caches the decoded map and the graph and a
    /// flight is now a collision world plus ~55 ticks. That is what let the corpus
    /// sweep become a gate — see <see cref="SweepAll"/>.
    /// </remarks>
    /// <param name="budget">Ticks per flight.</param>
    /// <param name="distances">The tables to plan with — see <see cref="Fly"/>.</param>
    public static List<EdgeFlight> Sweep(
        Func<ScenarioWorld> world,
        NavEdgeType type,
        Team team = Team.Red,
        int limit = 0,
        int budget = DefaultBudget,
        NavDistanceSet? distances = null)
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

            flights.Add(Fly(world(), i, team, budget, distances));
        }

        return flights;
    }

    /// <summary>
    /// Every edge of every graph in a nav directory, flown.
    /// </summary>
    /// <remarks>
    /// <b>The corpus sweep lives here rather than in its two callers</b> —
    /// <c>EdgeFlightTests</c> pins its tally and <c>BENCH_FLIGHT</c> prints its
    /// detail, and a number a gate pins and a bench reports must come from one
    /// implementation or the two drift into disagreeing about the same word.
    /// <para>
    /// ⚠️ <b><paramref name="distances"/> selects the ARM.</b> See
    /// <see cref="Fly"/>: with the tables the search prefers a different route,
    /// so a different set of hops is posable at all. It is not a speed switch and
    /// the two tallies are not comparable as runs of one thing.
    /// </para>
    /// <para>
    /// ⚠️ <b><paramref name="threads"/> may not change a single number.</b> A
    /// flight owns its world, its follower and its state, reads a
    /// <see cref="NavGraph"/> and a <see cref="LevelMap"/> that nothing writes,
    /// and draws from no shared stream — so the work is embarrassingly parallel
    /// and the result is ordered by graph and then by edge index whatever the
    /// thread count. <c>EdgeFlightTests.TheSweepIsTheSameHoweverManyThreads</c>
    /// is what holds that rather than this paragraph. The only shared thing
    /// touched is <see cref="ScenarioResources.Shared"/>, which is a
    /// <c>ConcurrentDictionary</c> over entries nobody writes.
    /// </para>
    /// </remarks>
    /// <param name="mapDirectory">Where the map PNGs are.</param>
    /// <param name="navDirectory">
    /// Where the graphs are. Every <c>(map, area)</c>
    /// <see cref="NavCache.List"/> finds there is swept.
    /// </param>
    /// <param name="types">
    /// Which edge kinds to fly, or null for all of them. <c>StickyJump</c> costs
    /// nothing to ask for on a cache that carries none.
    /// </param>
    /// <param name="team">Which team the bot is on, for the gates.</param>
    /// <param name="budget">
    /// Ticks per flight. ⚠️ <b>A stranding is relative to this</b> — 289 of the
    /// 1,022 strandings at 400 arrive by 1,600, so a stranded count quoted
    /// without its budget says nothing.
    /// </param>
    /// <param name="distances">
    /// True to plan with the shipped next-hop tables, deriving them where a
    /// graph has no file — the arm a live server and <c>eval</c> run.
    /// </param>
    /// <param name="threads">How many flights at once. 0 means one per core.</param>
    /// <param name="include">
    /// Which <c>(map, area)</c> to sweep, or null for every one the directory
    /// holds. <b>It filters the WORK, not the output</b> — a tool narrowing to one
    /// map should not pay for the other twenty-four and then hide them.
    /// </param>
    /// <param name="onGraph">
    /// Called once per graph, in order, with that graph's flights — for a tool
    /// that wants a per-map row without holding the whole sweep to print it.
    /// </param>
    public static List<EdgeFlight> SweepAll(
        string mapDirectory,
        string navDirectory,
        IReadOnlyList<NavEdgeType>? types = null,
        Team team = Team.Red,
        int budget = DefaultBudget,
        bool distances = false,
        int threads = 1,
        Func<string, int, bool>? include = null,
        Action<string, int, IReadOnlyList<EdgeFlight>>? onGraph = null,
        bool warmWeapon = false,
        bool heldFlight = true,
        PlayerClass? flyAs = null,
        bool heldDoubles = true)
    {
        ArgumentNullException.ThrowIfNull(mapDirectory);
        ArgumentNullException.ThrowIfNull(navDirectory);

        types ??= Enum.GetValues<NavEdgeType>();

        var wanted = new bool[Enum.GetValues<NavEdgeType>().Length];
        foreach (var type in types)
            wanted[(int)type] = true;

        // Every (graph, edge) pair up front, so the threads see one flat work
        // list rather than 25 nested loops of wildly different sizes —
        // cp_dirtbowl carries 7,942 edges against gg_debug's 495, and a
        // parallel-over-graphs split would spend most of its wall clock on one
        // map.
        var graphs = new List<(string Map, int Area, Func<ScenarioWorld> World, NavDistanceSet? Distances, int First, int Count)>();
        var work = new List<(int Graph, int Edge)>();

        foreach (var (map, area) in NavCache.List(navDirectory))
        {
            var mapName = map;
            var mapArea = area;

            if (include is not null && !include(mapName, mapArea))
                continue;

            Func<ScenarioWorld> world = () => ScenarioWorld.Load(mapName, mapArea, mapDirectory, navDirectory);

            var graph = world().Nav;
            if (graph is null)
                continue;

            var set = distances
                ? NavDistanceSet.LoadOrDerive(navDirectory, mapName, mapArea, graph, out _)
                : null;

            var first = work.Count;

            for (var i = 0; i < graph.EdgeCount; i++)
            {
                if (wanted[(int)graph.Edges[i].Type])
                    work.Add((graphs.Count, i));
            }

            graphs.Add((mapName, mapArea, world, set, first, work.Count - first));
        }

        var flights = new EdgeFlight[work.Count];

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads <= 0 ? Environment.ProcessorCount : threads,
        };

        Parallel.For(0, work.Count, options, i =>
        {
            var (graphIndex, edgeIndex) = work[i];
            var graph = graphs[graphIndex];
            flights[i] = Fly(
                graph.World(), edgeIndex, team, budget, graph.Distances, warmWeapon, heldFlight,
                flyAs: flyAs, heldDoubles: heldDoubles);
        });

        if (onGraph is not null)
        {
            foreach (var graph in graphs)
                onGraph(graph.Map, graph.Area, new ArraySegment<EdgeFlight>(flights, graph.First, graph.Count));
        }

        return [.. flights];
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

    /// <summary>
    /// The y a character is PLACED at to start on a node — one pixel above
    /// <see cref="NavGraph.StandY"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b><see cref="NavGraph.StandY"/> is a goal, not a body position, and
    /// standing a character on it puts one row of its box inside the floor.</b>
    /// The box's bottom offset is +23 and <c>StandY</c> is <c>FloorY - 23</c>,
    /// both inclusive, so the body's last row lands exactly on the floor's first
    /// solid row. Measured 2026-09-05 over every node of all 25 shipped graphs:
    /// <b>7,541 of 11,327 (66.6%) fail <c>PlaceFreeInMask</c> at <c>StandY</c>,
    /// and every single one is free one pixel up</b> — nothing is still blocked,
    /// on any map. Dropping a soldier onto a node and letting it settle rests it
    /// at <c>StandY - 0.55</c>, so one pixel is also the smallest integer
    /// clearance that agrees with where the simulation actually puts a body.
    /// <para>
    /// <b>What it was costing.</b> An overlapping character cannot move at all —
    /// every candidate step keeps the overlap, so <c>good_move_contact_solid</c>
    /// refuses each one and gravity has nothing to resolve. Those flights sat
    /// still for the whole budget and were scored <c>Stranded</c>: <b>123 of the
    /// 970 strandings were this and not the edge</b>, and 162 flights never moved
    /// a pixel. A harness that starts a body somewhere the game never puts one
    /// measures the harness.
    /// </para>
    /// <para>
    /// ⚠️ <b>It does not change which edges are testable</b> — <c>Measured</c>,
    /// <c>RoutedAround</c>, <c>Shadowed</c> and <c>NoRoute</c> are identical
    /// across the change (32,048 / 72,806 / 10,844 / 618), because the premise
    /// checks are asked of the graph and the plan rather than of the body. So the
    /// two tallies before and after ARE comparable, which is not true of the
    /// two routing arms.
    /// </para>
    /// <para>
    /// <c>EdgeFlightTests.EveryShippedNodeCanHoldACharacter</c> gates the census
    /// this rests on, so a graph that starts emitting nodes a body cannot occupy
    /// says so here rather than as a drift in the stranding count.
    /// </para>
    /// </remarks>
    public static double StandingY(NavGraph graph, int node)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return graph.StandY(node) - 1;
    }

    /// <summary>What a fresh bot of this team collides with.</summary>
    /// <remarks>
    /// Built rather than read off a placed character, so the check can run before
    /// anything is placed. <c>CarryingIntel</c> is false because a flight starts
    /// empty-handed, and that is the only field on a character's filter that a
    /// bot on a route can change.
    /// </remarks>
    private static CollisionFilter FilterFor(Team team) =>
        new() { Team = team, CarryingIntel = false };

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

            // Rebuilt from live state every tick, as ScriptedBot does.
            // CanRocketJump is the weapon plus an hp test, so a soldier that has
            // just cost itself 30hp sheds its rocket jumps mid-route — and
            // BotPathState.PlanQuery is what stops that unplanning the arc it is
            // already in.
            //
            // ⚠️ The two GATE terms ScriptedBot also rebuilds are deliberately NOT
            // rebuilt here, and it costs this harness nothing: a scenario world
            // runs no setup timer and a flight starts empty-handed, so both would
            // be false every tick anyway. QueryFor owns this query, and a flight
            // is a question about an arc rather than about a round.
            _query = _query with
            {
                CanDoubleJump = BotClassProfile.AirJump(self.Class),
                CanRocketJump = RocketModel.Capable(in self, in world.Weapon(slot)),
            };

            var keys = follower.Step(state, in self, in world.Weapon(slot), tick, _query);

            var aim = 0.0;
            PathFollower.ApplyLaunch(state, ref keys, ref aim);

            return PlayerInput.FromAim(keys, aim, 0);
        }
    }
}
