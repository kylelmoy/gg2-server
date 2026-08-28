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

    /// <summary>
    /// The team <see cref="Path"/> is currently counted against in edge occupancy
    /// — <c>player.botOccupiedTeam</c>, <see cref="Team.Spectator"/> for "counted
    /// nowhere".
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the same as the bot's team now.</b> Autobalance can switch a bot
    /// mid-round and a rotation re-teams every one of them, so a removal that read
    /// the current team would subtract from the wrong map and leave the old one
    /// raised for ever.
    /// <para>
    /// ⚠️ <b>One field where the reference has two.</b> <c>botOccupied</c> and
    /// <c>botOccupiedTeam</c> carry the same information there and can disagree;
    /// here the team IS the flag, so the state that would have to be kept in step
    /// does not exist. <see cref="BotOccupancy"/> owns it; nothing else writes it.
    /// </para>
    /// </remarks>
    public Team OccupiedTeam { get; internal set; } = Team.Spectator;

    /// <summary>Whether <see cref="Path"/> is counted anywhere.</summary>
    /// <remarks>
    /// ⚠️ <b>A route is added once and removed in three places</b> — on every
    /// re-plan, arrival and death; when the bot leaves or the map changes; and by
    /// the planner itself before it searches — so without this an edge two bots
    /// are on would be decremented twice by one of them and the other's count
    /// silently lost.
    /// </remarks>
    public bool Occupied => OccupiedTeam is Team.Red or Team.Blue;

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
    /// This bot's own offset into the re-plan cadence.
    /// </summary>
    /// <remarks>
    /// <c>botPathPlan.gml:53</c>: <c>botReplanAt = frame + BOT_REPLAN_TICKS +
    /// (player mod BOT_REPLAN_TICKS)</c>, where <c>player</c> is the instance id
    /// — so a server full of bots does not do all of its searching on one frame.
    /// <para>
    /// ⚠️ <b>It is ADDED, so it changes the interval and not just the phase</b>,
    /// and that has a consequence nothing in the reference remarks on: a
    /// successful re-plan clears the edge in progress, which zeroes
    /// <see cref="TakeoffTicks"/> on the next tick — so a bot's re-plan interval
    /// is a hard ceiling on its takeoff budget. At a stagger of 0 the interval is
    /// 45 and <c>BOT_TAKEOFF_PATIENCE</c> (60) is <b>unreachable</b>. See
    /// <see cref="PathFollower.Plan"/>.
    /// </para>
    /// <para>
    /// Any per-bot number will do; the roster slot is the obvious one. It is
    /// taken modulo <c>BOT_REPLAN_TICKS</c> at use.
    /// </para>
    /// </remarks>
    public int ReplanStagger { get; set; }

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

    /// <summary>Ticks airborne on the current arc, counted from takeoff.</summary>
    /// <remarks>
    /// ⚠️ <b>Counted rather than read off <c>vspeed</c></b>, which would otherwise
    /// give the phase of the arc for free: <c>vspeed</c> pins at 10 once the fall
    /// reaches terminal velocity, and every phase after that reads as the same
    /// tick.
    /// </remarks>
    public int AirTicks { get; set; }

    /// <summary>
    /// Ticks spent standing at the takeoff column refusing to leave.
    /// </summary>
    /// <remarks>
    /// ⚠️ Counted <em>only</em> at the gate, and cleared by nothing but a takeoff
    /// or a change of edge. The failure it exists for is a bot that shuffles
    /// between the gate and its run-up for ever; counting the approach as well
    /// would spend the budget on a long walk and then fire a jump from the middle
    /// of the run.
    /// <para>
    /// ⚠️ In the reference this was set in four places and incremented in none,
    /// so <c>BOT_TAKEOFF_PATIENCE</c> could never be reached and the escape hatch
    /// was dead code for its whole life.
    /// </para>
    /// </remarks>
    public int TakeoffTicks { get; set; }

    /// <summary>The column being backed up to for a run-up, or -1.</summary>
    /// <remarks>
    /// Resolved once per edge and remembered, because working it out walks the
    /// graph. Cleared on takeoff, on a change of edge, and by the escape hatch.
    /// </remarks>
    public int RunupAt { get; set; } = -1;

    /// <summary>Whether this arc's second impulse has already been spent.</summary>
    /// <remarks>
    /// ⚠️ A once-only flag rather than a bare equality on the tick: <c>&gt;=</c>
    /// with a flag survives a tick that does not reach the branch, where
    /// <c>==</c> would silently fly a double-jump arc as a single jump and land
    /// the bot somewhere the graph never said.
    /// </remarks>
    public bool Rejumped { get; set; }

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

    /// <summary>How many rocket jumps this bot has taken off on.</summary>
    public int RocketJumps { get; set; }

    /// <summary>
    /// This tick's rocket-jump hand-off: aim straight down and fire.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The follower cannot fire.</b> It owns LEFT / RIGHT / JUMP / DOWN and
    /// the aim comes from the combat half alone, so a rocket jump is <em>requested</em>
    /// rather than pressed — <c>botInputUpdate</c> applies it after
    /// <c>botCombatUpdate</c> has run, which is the only place that can see both
    /// halves. <c>ScriptedBot</c> is that place here.
    /// <para>
    /// ⚠️ <b>A bool rather than the reference's <c>botRocketAim</c> direction</b>,
    /// because 270 is the only value it ever holds and that is a decision rather
    /// than a default: <c>vectorfactor</c> in the rocket's blast is
    /// sqrt(sin⁴ + cos⁴), which is 1 only when the impulse is axis-aligned and
    /// falls to 0.707 at 45°, so an angled rocket jump throws away up to 30% of its
    /// impulse. <c>PathFollower.RocketAimDegrees</c> is the number.
    /// </para>
    /// <para>
    /// ⚠️ Cleared at the top of every <c>Step</c>, so a tick that does not reach
    /// the takeoff gate cannot leave last tick's request standing.
    /// </para>
    /// </remarks>
    public bool RocketFire { get; set; }

    /// <summary>
    /// The follower wants a mine placed at its feet this tick — phase one of a
    /// sticky jump.
    /// </summary>
    /// <remarks>
    /// Handed over like <see cref="RocketFire"/> and for the same reason: the
    /// follower owns LEFT / RIGHT / JUMP / DOWN and cannot fire, so the aim and
    /// the trigger are applied by <c>ScriptedBot</c> after the combat half has
    /// run. ⚠️ Straight down, for the same <c>vectorfactor</c> reason a rocket
    /// jump aims down.
    /// </remarks>
    public bool MineFire { get; set; }

    /// <summary>
    /// The follower wants its mine detonated this tick — phase two, and it is
    /// the <em>same</em> tick as the jump.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Right-click, not attack.</b> <c>Minegun</c>'s User Event 2 detonates
    /// every mine the demoman owns, and Begin Step runs the jump branch before
    /// the right-click branch — so pressing both on one tick stacks the blast on
    /// <c>vspeed = -jumpStrength</c>. A tick later would waste the jump.
    /// </remarks>
    public bool MineDetonate { get; set; }

    /// <summary>
    /// Ticks left before the placed mine has settled and the jump can be taken.
    /// </summary>
    /// <remarks>
    /// Counts down from <see cref="NavConstants.SjSetupTicks"/>, and <b>-1 means
    /// no sticky jump is being set up</b> rather than "ready now" — a zeroed
    /// state must not read as a mine that is waiting.
    /// <para>
    /// ⚠️ <b>Reset on every re-plan and on leaving the ground</b>, because a
    /// countdown that survives either is a bot detonating under a route it is no
    /// longer following.
    /// </para>
    /// </remarks>
    public int StickySetup { get; set; } = -1;

    /// <summary>How many sticky jumps this bot has taken.</summary>
    public int StickyJumps { get; set; }

    /// <summary>How many second impulses this bot has spent on double-jump arcs.</summary>
    public int DoubleJumps { get; set; }

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

        // ⚠️ The run-up belongs to the edge that was being prepared for, and
        // that edge is gone with the route. The takeoff budget is NOT reset here
        // — botPathFree.gml does not touch it — and the distinction is not
        // academic: it is zeroed a moment later anyway, by FollowEdge's
        // change-of-edge test, because this just set EdgeFrom to -1. See
        // PathFollower.Plan for why that matters.
        RunupAt = -1;
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
