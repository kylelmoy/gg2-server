using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// One bot's navigation state: its route, where it is along it, and the counters
/// that decide when to give up on something.
/// </summary>
/// <remarks>
/// The <c>bot*</c> fields <c>Player.events/Create.xml</c> declares, minus the
/// ones that belong to combat. A class rather than a struct, and deliberately
/// <em>not</em> in <c>Gg2.Simulation.WorldState</c>: a route is a few hundred
/// bytes of list per bot and is not simulation at all — no client re-derives it,
/// nothing is serialised from it, and a rollout that forks the world does not
/// want to fork twelve routes with it.
/// <para>
/// ⚠️ <b>Which means a rollout has to decide what to do about it.</b> Forking a
/// <see cref="Gg2.Simulation.WorldState"/> and re-using one of these gives the
/// forked world a bot that is part-way along a route planned in the parent. That
/// is usually what you want for a short horizon and never what you want for a
/// long one. Whoever builds the search picks; nothing here can pick for them.
/// </para>
/// </remarks>
public sealed class BotPathState
{
    /// <summary>The route, node by node, or empty when there is none.</summary>
    /// <remarks>
    /// ⚠️ <b>An empty route and a route the bot has finished are different
    /// states.</b> <see cref="HasGoal"/> is what says whether the bot is trying
    /// to go anywhere; arriving clears it. A bot with no route and a goal is one
    /// whose search failed and will try again.
    /// </remarks>
    public List<int> Path { get; } = [];

    /// <summary>How far along <see cref="Path"/> the bot has got.</summary>
    /// <remarks>
    /// ⚠️ Advanced forward only. A bot only ever moves along its own route, and
    /// looking backwards would let a long surface the route touches twice pull
    /// it into a loop.
    /// </remarks>
    public int PathAt { get; set; }

    /// <summary>Whether the bot is trying to go anywhere at all.</summary>
    public bool HasGoal { get; set; }

    /// <summary>Where it is trying to go, in world pixels.</summary>
    public double GoalX { get; set; }

    /// <summary>Where it is trying to go, in world pixels.</summary>
    public double GoalY { get; set; }

    /// <summary>
    /// The route is to a goal that has already been replaced.
    /// </summary>
    /// <remarks>
    /// ⚠️ Kept only so the bot keeps moving until a plan for the new goal exists.
    /// Its last node is somewhere the bot was sent before, so <b>reaching the end
    /// of it is not an arrival at anything</b> — declaring one would clear
    /// <see cref="HasGoal"/> and throw away the objective the bot was just given.
    /// </remarks>
    public bool PathStale { get; set; }

    /// <summary>Set on the tick the bot reaches its goal and stops.</summary>
    public bool Arrived { get; set; }

    /// <summary>The tick <see cref="Arrived"/> was set on.</summary>
    /// <remarks>
    /// Read against the tick the goal was issued, this is how long the leg took —
    /// the one number a navigation change can be compared on. ⚠️ It cannot be
    /// recovered from outside: arrival clears the goal, so a bot that has arrived
    /// and a bot that never had one look identical a moment later.
    /// </remarks>
    public int ArrivedAt { get; set; } = -1;

    /// <summary>The next tick on which the re-plan timer is due.</summary>
    public int ReplanAt { get; set; }

    /// <summary>
    /// A failed search parks the next one until this tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only while a route is actually installed. A failed search <em>keeps</em>
    /// the old route, so every event-driven caller would otherwise fire again on
    /// the next tick — a full A* per bot per frame, for a goal that cannot be
    /// reached from a pixel away either. A bot with nothing to follow has to keep
    /// trying, and the ordinary re-plan timer is the throttle for that.
    /// </remarks>
    public int PlanFailUntil { get; set; }

    /// <summary>The edge being traversed, or -1.</summary>
    public int EdgeFrom { get; set; } = -1;

    /// <summary>The edge being traversed, or -1.</summary>
    public int EdgeTo { get; set; } = -1;

    /// <summary>Ticks pressing something and going nowhere.</summary>
    public int StuckTicks { get; set; }

    /// <summary>Ticks standing somewhere that resolves to no node at all.</summary>
    public int OffPathTicks { get; set; }

    /// <summary>Where the bot was last tick, for the moved test.</summary>
    public double LastX { get; set; }

    /// <summary>Where the bot was last tick, for the moved test.</summary>
    public double LastY { get; set; }

    /// <summary>Where the last re-plan happened, for the thrash test.</summary>
    public double LastReplanX { get; set; }

    /// <summary>Where the last re-plan happened, for the thrash test.</summary>
    public double LastReplanY { get; set; }

    /// <summary>Consecutive re-plans within <c>BOT_THRASH_DIST</c> of each other.</summary>
    public int ReplanNear { get; set; }

    /// <summary>The edge the last two off-route landings blamed.</summary>
    public int FailFrom { get; set; } = -1;

    /// <summary>The edge the last two off-route landings blamed.</summary>
    public int FailTo { get; set; } = -1;

    /// <summary>
    /// How many consecutive times that edge has put the bot somewhere the route
    /// does not mention.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Two, not one.</b> A lot of real edges are marginal — a three-column
    /// ledge reached by a slow steep arc — and those are flown stochastically.
    /// Banning one on its first miss costs five seconds of wandering for a
    /// perfectly good edge, which measured as ~250 frames to complete a hop that
    /// takes about 30: fifteen off-route events produced fifteen blacklists, and
    /// the blacklist was the whole delay.
    /// </remarks>
    public int FailCount { get; set; }

    /// <summary>
    /// Whether the bot is part-way through an arc the generator validated.
    /// </summary>
    /// <remarks>
    /// ⚠️ Cleared at the top of every follower tick and set only by the in-flight
    /// branch, so every early return leaves it false. The evasion behaviours read
    /// it: an extra jump or a step in the air permanently ruins a
    /// position-per-tick trajectory.
    /// </remarks>
    public bool FlyingEdge { get; set; }

    /// <summary>
    /// Alternates so a bot that still wants to jump gets a fresh press every
    /// other tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Jump is edge-triggered, not held.</b>
    /// <c>Character.events/Begin Step.xml</c> jumps on <c>pressedKeys &amp; $80</c>
    /// — a rising edge — so a bot holding JUMP down jumps exactly once and then
    /// never again.
    /// </remarks>
    public bool JumpHeld { get; set; }

    /// <summary>The edges this bot is refusing to be routed over.</summary>
    /// <remarks>
    /// Per bot, which is why it is here rather than on the graph — and a set,
    /// because that is what <see cref="NavSearch.Find"/> takes and building one
    /// per plan would be an allocation on the one path a bot walks every
    /// forty-five ticks.
    /// <para>
    /// The expiries live in <see cref="BlacklistOrder"/> beside the keys rather
    /// than in a second map: it is insertion-ordered, so a sweep off the front is
    /// the whole expiry mechanism, and the only moment a stale entry could affect
    /// anything is a re-plan.
    /// </para>
    /// </remarks>
    public HashSet<long> Blacklist { get; } = [];

    /// <summary>The bans in the order they were imposed, with their expiries.</summary>
    public List<(long Key, int Expiry)> BlacklistOrder { get; } = [];

    // --- diagnostics ---------------------------------------------------------
    //
    // Counters rather than logs. ⚠️ `Replans` is deliberately NOT something to
    // cap in a scenario: it is a measure of how long the leg took rather than of
    // anything going wrong.

    public int Replans { get; set; }

    public int StuckFires { get; set; }

    public int OffRouteFires { get; set; }

    public int BlacklistFires { get; set; }

    /// <summary>
    /// How many ticks the follower asked for an edge kind it cannot fly.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Zero once the jump branch lands.</b> Until then a jump, double jump
    /// or rocket jump on a route is steered by nothing, and the bot falls back on
    /// the machinery that already exists for an edge that turns out to be
    /// untraversable — the stuck detector takes it away and the search routes
    /// round it. This counter is what stops that being silent.
    /// </remarks>
    public int UnsupportedEdgeTicks { get; set; }

    /// <summary>Whether a route is installed.</summary>
    public bool HasPath => Path.Count > 0;

    /// <summary>
    /// Gives up the route and everything that belongs to it.
    /// </summary>
    /// <remarks>
    /// <c>botPathFree</c>. ⚠️ The per-route counters go with the route — the edge
    /// in progress, the off-route and stuck counters — which is why this is one
    /// call and not a caller's job to remember.
    /// </remarks>
    public void FreePath()
    {
        Path.Clear();
        PathAt = 0;
        EdgeFrom = -1;
        EdgeTo = -1;
        StuckTicks = 0;
        OffPathTicks = 0;
        PathStale = false;
    }

    /// <summary>
    /// Bans an edge for <c>BOT_BLACKLIST_TICKS</c> — <c>botBlacklistEdge.gml</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ An edge already banned is <b>not</b> re-armed. The reference exits on
    /// the existence test, so a ban runs from when it was first imposed and a bot
    /// that keeps failing the same edge cannot extend it indefinitely. Negative
    /// nodes are ignored, which is what makes it safe to call with
    /// <see cref="EdgeFrom"/> when no edge was running.
    /// </remarks>
    public void BlacklistEdge(int fromNode, int toNode, int tick)
    {
        if (fromNode < 0 || toNode < 0)
            return;

        var key = NavGraph.EdgeKey(fromNode, toNode);

        if (!Blacklist.Add(key))
            return;

        BlacklistOrder.Add((key, tick + BotConstants.BlacklistTicks));
        BlacklistFires++;
    }

    /// <summary>Drops expired bans off the front of the insertion-ordered list.</summary>
    public void SweepBlacklist(int tick)
    {
        var expired = 0;

        while (expired < BlacklistOrder.Count)
        {
            var (key, expiry) = BlacklistOrder[expired];

            // ⚠️ `> tick`, so an entry survives until the tick after its expiry —
            // the reference's `break` is on the same comparison. Insertion order
            // is expiry order because every ban is the same length, which is what
            // makes stopping at the first live entry correct.
            if (expiry > tick)
                break;

            Blacklist.Remove(key);
            expired++;
        }

        if (expired > 0)
            BlacklistOrder.RemoveRange(0, expired);
    }
}
