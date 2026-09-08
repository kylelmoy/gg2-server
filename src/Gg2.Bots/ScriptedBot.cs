using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// A bot: the navigation half and the combat half, ORed into one input.
/// </summary>
/// <remarks>
/// <c>Scripts/Bots/botInputUpdate.gml</c>, and the thing every other item in
/// phase 7 was building towards. It is an ordinary <see cref="IInputSource"/>, so
/// <c>GameServer.AddBot</c> takes it and no client can tell it from a socket.
/// <para>
/// <b>The two halves are ORed and cannot conflict</b>: <see cref="BotCombat"/>
/// owns ATTACK and SPECIAL, <see cref="PathFollower"/> owns LEFT / RIGHT / JUMP /
/// DOWN. The aim comes from combat alone — the follower has no opinion about it.
/// </para>
/// <para>
/// <b>Combat runs every tick, and so does the follower.</b> Aiming may lag and
/// look human; steering may not — a jump missed by one tick is a bot standing at
/// the edge of a gap for ever. What is cadenced is the <em>target search</em> and
/// the <em>re-plan</em>, both inside their own halves.
/// </para>
/// <para>
/// <b>Three behaviours live here rather than in either half</b>, because here is
/// the only place that can see both: the regroup hold, the engage hold and the
/// back-off band. Each of them overrides the follower's horizontal keys, and each
/// is gated on being <em>on the ground and not part-way through a validated
/// arc</em> — stripping LEFT/RIGHT during a jump is not a pause, it is a
/// different jump, and the follower is holding a position-per-tick trajectory
/// that needs a key almost every tick to stay on.
/// </para>
/// <para>
/// <b>Everything this type's three behaviours needed is ported now.</b> The
/// rocket dodge (<c>botRocketDodge</c>) landed with the projectile reflexes, the
/// objective layer (<c>botObjectiveUpdate</c>) with the goal cascade, and the
/// close-in step (<c>botStepSafe</c>) last — so a pyro walks at its target
/// instead of holding where the engage caught it, and refuses the step when the
/// ground runs out.
/// </para>
/// </remarks>
public sealed class ScriptedBot : IInputSource, ICommandSource, IScenarioSubject
{

    /// <summary>
    /// The <see cref="ScriptedBot"/> driving a slot, through any micro-controller
    /// wrapped around it — or null if the slot is not a bot at all.
    /// </summary>
    /// <remarks>
    /// <b>Use this instead of <c>source is ScriptedBot</c>.</b> A slot driven by
    /// <see cref="SearchBot"/> or <see cref="PolicyBot"/> IS a bot, and the direct
    /// type test says it is not — silently, at four sites in the live server, one
    /// of which frees nav reservations on removal and another of which hands every
    /// bot the new graph on a map change. See <see cref="IWrapsScriptedBot"/>.
    /// </remarks>
    public static ScriptedBot? Of(IInputSource? source) => source switch
    {
        ScriptedBot bot => bot,
        IWrapsScriptedBot wrapper => wrapper.Scripted,
        _ => null,
    };
    /// <summary>How long a spot that never planned is banned for.</summary>
    /// <remarks>The reference's own literal 300, ten seconds.</remarks>
    private const int SpotBanTicks = 300;

    /// <summary>
    /// How long a bot stands completely still before it asks for somewhere else
    /// to stand — see <see cref="ParkedTicks"/>.
    /// </summary>
    /// <remarks>
    /// Eight seconds. Long enough never to interrupt a bot holding a firing
    /// position through one engagement, short enough that nobody watching sees a
    /// statue.
    /// <para>
    /// ⚠️ <b>Here rather than in <c>BotConstants</c>, and not for tidiness.</b>
    /// That table is on <c>SimulationStamp</c>'s GATING surface, so adding a
    /// constant to it moves <c>SIM_UUID</c> and refuses every shipped checkpoint —
    /// measured, 75 tests red on a stamp mismatch. A port-authored bot-tuning
    /// number that cannot change what an action does does not belong on that
    /// surface. <see cref="CarrierStuckTicks"/> is the same call.
    /// </para>
    /// </remarks>
    public const int RepostTicks = 240;

    /// <summary>How far a re-post has to move the bot to count as one.</summary>
    /// <remarks>
    /// Above <c>BOT_GOAL_RETARGET_DIST</c>, so a re-post is never a goal the
    /// objective throttle would swallow, and an order of magnitude above the
    /// arrival tolerance, so the bot walks rather than shuffles.
    /// </remarks>
    public const int RepostMinMove = 96;

    private PathFollower _follower;
    private NavGraph? _graph;

    private BotLanes? _lanes;

    private int _lane;

    private NavDistanceSet? _distances;

    private bool _goalIsSpot;

    /// <summary>
    /// Builds a bot for one map.
    /// </summary>
    /// <param name="graph">
    /// The map's nav graph, or null. <b>Null means the bot never walks
    /// anywhere</b> — it still aims and fires, which is exactly what a scenario
    /// about combat wants and exactly what a routing one must not have.
    /// </param>
    /// <param name="stagger">
    /// This bot's offset into the shared cadences, normally its roster slot.
    /// <b>The two staggers are different things.</b>
    /// <see cref="BotCombatState.Stagger"/> is a phase; the path one is
    /// <em>added</em> to the re-plan period, and at zero the interval is 45 ticks
    /// against a 60-tick takeoff budget — so the jump escape hatch can never
    /// fire. See docs/TRAPS-BOTS.md, "Phase 7 item 4b-ii".
    /// </param>
    public ScriptedBot(NavGraph? graph, NavQuery query, BotSkill skill, int stagger = 0)
    {
        _graph = graph;
        _follower = graph is null ? null! : new PathFollower(graph);

        Query = query;
        Skill = skill;
        Path = new BotPathState { ReplanStagger = stagger };
        Combat = new BotCombatState { Stagger = stagger };

        // Seeded from the stagger, so two bots on one team roll differently
        // and one bot rolls the same way every run. Both halves matter: the
        // regroup hold is randomised per life precisely so a whole team does not
        // leave spawn on the same frame, and docs/BOTAI.md item 2 wants a run to
        // be reproducible.
        Random = new GmlRandom((uint)stagger);
    }

    /// <summary>Who this bot is, for the search's gate and class rules.</summary>
    public NavQuery Query { get; set; }

    /// <summary>How good it is.</summary>
    public BotSkill Skill { get; set; }

    /// <summary>Its route and the counters that decide when to give up on one.</summary>
    public BotPathState Path { get; }

    /// <summary>Its target, its aim and the four clocks the difficulty runs on.</summary>
    public BotCombatState Combat { get; }

    /// <summary>
    /// A tick where the whole team may not leave spawn at once.
    /// </summary>
    /// <remarks>
    /// <b>Bots arrive in clumps rather than in single file.</b> They stream
    /// toward the objective one at a time and get killed piecemeal because each
    /// leaves the instant it can; a human waits a moment for whoever else is
    /// about to spawn. The hold is short, randomised per life so a whole team
    /// does not leave on the same frame, and ends early the moment enough
    /// team-mates are actually nearby — which is the condition it is really
    /// waiting for.
    /// <para>
    /// <b>Only movement is held.</b> The bot still aims, still shoots and
    /// still dodges, so it never reads as frozen.
    /// </para>
    /// </remarks>
    public int RegroupUntil { get; set; }

    /// <summary>Whether the bot was alive last tick, so a respawn starts a new hold.</summary>
    public bool AliveLast { get; set; }

    /// <summary>
    /// This bot's own random stream.
    /// </summary>
    /// <remarks>
    /// <b>Its own, and NOT <see cref="WorldState.Random"/>, and that is a rule
    /// rather than a convenience.</b> The match's stream is <em>observable
    /// simulation</em>: every seeded weapon puts its draw on the wire and every
    /// client re-derives the spread from it (docs/TRAPS-SIMULATION.md, "GM8's RNG is Delphi's").
    /// A bot drawing from it would make the stream's position depend on how many
    /// bots were thinking, which is a divergence no client could follow and which
    /// the parity harness would report as a wrong shot.
    /// <para>
    /// <b>It is also why <see cref="WorldView"/> being read-only is not in the
    /// way here.</b> A first draft copied <c>world.State.Random</c> into a local
    /// and passed it by reference: the copy advanced, the world's did not, and
    /// every tick drew the <em>same</em> numbers — an aim error that never
    /// changed and a hold-fire roll that never varied. Silent, and exactly the
    /// kind of thing a determinism test passes.
    /// </para>
    /// <para>
    /// <b>It has to be part of any snapshot.</b> docs/BOTAI.md item 2 asks for
    /// restore-to-here, and a restored world with a bot whose stream carried on
    /// is not the same run.
    /// </para>
    /// </remarks>
    public GmlRandom Random;

    /// <summary>
    /// What decides where this bot goes, or null for a bot that is told from
    /// outside.
    /// </summary>
    /// <remarks>
    /// <b>Null is not a degraded mode.</b> A scenario, a test and a rollout
    /// evaluator all want a bot they can point at something, and the reference has
    /// the same escape (<c>botGoalLocked</c>). What null costs is exactly the
    /// objective layer and nothing else.
    /// </remarks>
    public BotObjective? Objective { get; set; }

    /// <summary>
    /// The team-wide edge crowding this bot plans against, or null for a bot that
    /// plans in isolation.
    /// </summary>
    /// <remarks>
    /// <b>Shared between every bot on the server</b>, not per bot — that is
    /// what makes it model bots bunching up rather than approximating it with
    /// per-bot noise. Null is the honest default for a hand-built bot in a test:
    /// with nothing else walking, occupancy answers nothing anyway.
    /// </remarks>
    public BotOccupancy? Occupancy { get; set; }

    /// <summary>
    /// Which half of the team this bot is on. <b>Assigned once at join, never
    /// per tick</b> — see <see cref="BotRoles"/>.
    /// </summary>
    public BotRole Role { get; set; }

    /// <summary>
    /// The corridors this bot may be assigned to, for the graph it is on now.
    /// </summary>
    /// <remarks>
    /// <b>Shared between every bot on the server, like <see cref="Occupancy"/>,
    /// and for the opposite reason.</b> Occupancy is shared because it has to
    /// aggregate what the team is doing; this is shared because it is derived
    /// from the graph and there is no per-bot part of it to compute — one
    /// derivation serves everyone, and each bot picks a lane out of it.
    /// <para>
    /// ⚠️ <b>Dropped by <see cref="SetMap"/>, exactly like the route and the
    /// occupancy counts, and for the same reason</b>: a lane's costs are indexed
    /// by edge position, and an edge index only means anything to one build of a
    /// graph. Whoever changes the map owes the bots a fresh derivation.
    /// </para>
    /// </remarks>
    public BotLanes? Lanes
    {
        get => _lanes;
        set
        {
            _lanes = value;
            ApplyLane();
        }
    }

    /// <summary>
    /// Which of <see cref="Lanes"/> this bot routes down. <b>Assigned once at
    /// join, never per tick</b> — <see cref="BotRoles.Lane"/>.
    /// </summary>
    public int Lane
    {
        get => _lane;
        set
        {
            _lane = value;
            ApplyLane();
        }
    }

    /// <summary>
    /// The precomputed A\* distance tables, or null for the straight-line
    /// heuristic. <c>--nav-distances</c>; off unless asked for.
    /// </summary>
    /// <remarks>
    /// <b>Shared between every bot on the server</b>, like <see cref="Lanes"/>
    /// and for the same reason: it is derived from the graph and has no per-bot
    /// part. Unlike a lane it is not even indexed per bot — every asker reads
    /// the table matching its own class arcs out of the one set.
    /// <para>
    /// ⚠️ <b>Dropped by <see cref="SetMap"/></b>: the tables are indexed by node
    /// and a node index only means anything to one build of a graph.
    /// </para>
    /// </remarks>
    public NavDistanceSet? Distances
    {
        get => _distances;
        set
        {
            _distances = value;
            ApplyDistances();
        }
    }

    /// <summary>
    /// Whether this bot decided, on its most recent poll, to stop walking and
    /// fight — its engage hold.
    /// </summary>
    /// <remarks>
    /// <b>Exposed so PolicyBot can hand the wheel over on INTENT rather than on
    /// proximity</b>, and assigned from the same expression the hold itself uses
    /// rather than recomputed. A second copy of this predicate would be the
    /// sniper-candidate-count bug again: two places that should agree about one
    /// decision, disagreeing.
    /// <para>
    /// <b>It is time-bounded</b> — <c>BotConstants.EngageTicks</c> after the
    /// target was acquired the hold lapses and the bot resumes its route. That
    /// bound is the property a distance rule does not have.
    /// </para>
    /// </remarks>
    public bool WantsToEngage { get; private set; }

    /// <summary>
    /// Everything a bot is told about its place in the roster, in one call: its
    /// role, its lateral spread, its nav query and its objective layer.
    /// </summary>
    /// <remarks>
    /// Shared by the server's population manager and <see cref="HeadlessMatch"/>,
    /// so a bot configured by one path cannot diverge from the other and play a
    /// different game in a measurement from the one it played on the server.
    /// <para>
    /// <b>The objective is built fresh, never shared.</b> A
    /// <see cref="BotObjective"/> holds the graph and the map's spawn points, so
    /// keeping one across a map change or between matches is keeping another
    /// world's answer. Null when either is missing: a bot with no graph fights
    /// whatever reaches it and walks nowhere.
    /// </para>
    /// </remarks>
    /// <param name="slot">The roster slot, which is what staggers the spread.</param>
    /// <param name="earlierTeammates">
    /// The classes of the same team's bots in lower slots, ascending — what
    /// <see cref="BotRoles.Assign"/> balances a new role against.
    /// </param>
    public void Assign(
        int slot,
        Team team,
        PlayerClass playerClass,
        ReadOnlySpan<PlayerClass> earlierTeammates,
        NavGraph? nav,
        SpawnPoints? spawns)
    {
        Role = BotRoles.Assign(playerClass, earlierTeammates);
        SpreadX = BotRoles.SpreadX(slot);
        Query = new NavQuery(team, CanDoubleJump: BotClassProfile.AirJump(playerClass));
        Objective = nav is not null && spawns is not null ? new BotObjective(nav, spawns) : null;
    }

    /// <summary>
    /// This bot's constant lateral goal offset — <see cref="BotRoles.SpreadX"/>.
    /// </summary>
    /// <remarks>
    /// It has to be constant for the bot's whole life: a spread that changes
    /// re-issues the goal every time it is read, and re-issuing forces an immediate
    /// re-plan.
    /// </remarks>
    public double SpreadX { get; set; }

    /// <summary>
    /// Suspends the objective layer, so a goal set from outside stays set.
    /// </summary>
    /// <remarks>
    /// <c>botGoalLocked</c>. Nothing in the game sets it; it is here for the same
    /// reason it is there — a harness that has already decided where the bot should
    /// go.
    /// </remarks>
    public bool GoalLocked { get; set; }

    /// <summary>
    /// A tick before which a <see cref="BotGoal.IsSpot"/> goal will not be tried.
    /// </summary>
    /// <remarks>
    /// <b>A spot is chosen without checking reachability</b>
    /// (<see cref="BotGoalSpot"/>), so one in another component of the map presents
    /// as a goal that never plans. This is the only place that can notice — a goal
    /// still held with no route behind it — and the answer is to stop trying spots
    /// for a while rather than leave the bot standing at spawn wanting something it
    /// cannot walk to.
    /// <para>
    /// <b>The state is not reachable from any shipped map</b> — all 24 cached
    /// graphs are connected for the team that owns the ground. <c>SpotBanTests</c>
    /// gates it on a real graph with one node's edges stripped instead: a genuine
    /// two-component graph over real geometry, so the spot search sees exactly the
    /// candidates it always saw and the route to them is the only thing that
    /// changed.
    /// </para>
    /// </remarks>
    public int SpotBanUntil { get; set; }

    /// <summary>
    /// Consecutive ticks this bot has stood on the ground with nothing to walk to
    /// and no enemy to hold for.
    /// </summary>
    /// <remarks>
    /// <b>What stops a bot becoming a statue.</b> Arrival frees the path and
    /// clears the goal, so an arrived bot presses no movement key; the objective
    /// layer then re-decides the same point and resolves it to the node the bot is
    /// standing on, and nothing ever moves it again. At <see cref="RepostTicks"/>
    /// this asks <see cref="BotObjective.Decide"/> for somewhere else to stand —
    /// see that parameter for the measurement.
    /// <para>
    /// <b>A bot pressing a direction and going nowhere is NOT parked</b>, it is
    /// stuck, and <see cref="BotPathState.StuckTicks"/> already owns that case
    /// with a blacklist behind it. This one is pressing nothing, which is why
    /// that counter cannot see it.
    /// </para>
    /// </remarks>
    public int ParkedTicks { get; private set; }

    /// <summary>
    /// Where the bot was on the previous poll, for <see cref="ParkedTicks"/>.
    /// </summary>
    /// <remarks>
    /// NaN rather than zero when there is no previous poll: zero is a world
    /// position, and comparing against it would call a bot standing at the origin
    /// parked on its first tick.
    /// </remarks>
    private double _parkedX = double.NaN;

    /// <inheritdoc cref="_parkedX"/>
    private double _parkedY = double.NaN;

    /// <summary>
    /// Moves this bot to a new map.
    /// </summary>
    /// <remarks>
    /// <b>A follower, a route and a blacklist are all indices into ONE graph</b>,
    /// so a bot that survives a map rotation carrying them is holding node numbers
    /// that now mean somewhere else entirely — a route through a map that is no
    /// longer loaded, and a blacklist banning edges that were never flown. The
    /// reference frees exactly this on a departure (<c>botNavRelease</c>) and
    /// re-teams every bot on a map change (<c>botReteamAll</c>); a rotation is the
    /// case where both matter at once.
    /// <para>
    /// The objective goes too, because a <see cref="BotObjective"/> holds the graph
    /// and the map's spawn points. The caller supplies a fresh one.
    /// </para>
    /// </remarks>
    public void SetMap(NavGraph? graph)
    {
        _graph = graph;
        _follower = graph is null ? null! : new PathFollower(graph);

        // The counts go with the route: keys are node indices, which only mean
        // anything to ONE build of the graph.
        //
        // `botOccupancyReset` clears the shared maps AND every bot's flag, because
        // there they are two separate operations and dropping the maps alone would
        // leave a bot believing its route is still counted — so its next removal
        // declines to decrement and the route is added a SECOND time on the next
        // plan. Here the removal is what clears the flag, so one call does both,
        // and the shared Reset is the population manager's half.
        Occupancy?.Remove(Path);

        Path.FreePath();
        Path.HasGoal = false;
        Path.Blacklist.Clear();
        Path.BlacklistOrder.Clear();

        Objective = null;
        _goalIsSpot = false;
        SpotBanUntil = 0;
        ParkedTicks = 0;
        _parkedX = double.NaN;
        _parkedY = double.NaN;

        // The lane goes with the graph it was derived for. Dropping it rather
        // than carrying it is what makes the failure safe: a bot with no lane
        // routes on the graph's own costs, where a bot holding the old map's
        // field prices the new map's edges with numbers that mean nothing.
        _lanes = null;
        ApplyLane();

        // Same rule, same reason: the tables are indexed by NODE, so a set held
        // across a map change would hand the search a bound derived from a graph
        // the bot is no longer standing on — and an over-estimate is not a slow
        // route, it is the wrong one.
        _distances = null;
        ApplyDistances();
    }

    /// <summary>Hands the follower this bot's lane, or nothing.</summary>
    private void ApplyLane()
    {
        if (_follower is not null)
            _follower.Lane = _lanes is null ? default : _lanes.Costs(_lane);
    }

    /// <summary>Hands the follower the distance tables, or nothing.</summary>
    private void ApplyDistances()
    {
        if (_follower is not null)
            _follower.Distances = _distances;
    }

    /// <summary>Sends the bot somewhere. Null graph means it cannot go.</summary>
    public bool SetGoal(double worldX, double worldY) =>
        _graph is not null && _follower.SetGoal(Path, worldX, worldY);

    /// <summary>
    /// Is a target held right now — <see cref="IScenarioSubject"/>'s half of a
    /// combat scenario.
    /// </summary>
    public bool HasTarget => Combat.Target.Exists;

    /// <summary>
    /// One tick: plan, steer, aim, fire, and hand back the four input bytes.
    /// </summary>
    /// <remarks>
    /// Every draw comes from <see cref="Random"/>, this bot's own stream — see
    /// the warning there for why it must not be the match's.
    /// </remarks>
    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        if (!world.IsAlive(slot))
        {
            // A dead bot gives up its route rather than resuming one from
            // where it died: it will come back somewhere else entirely — and its
            // edges stop being crowded the moment it stops walking them.
            Occupancy?.Remove(Path);
            Path.FreePath();
            AliveLast = false;
            ParkedTicks = 0;

            // So the first tick of the next life is not compared against where
            // this one ended.
            _parkedX = double.NaN;
            _parkedY = double.NaN;

            return PlayerInput.None;
        }

        ref readonly var self = ref world.Character(slot);

        // The follower needs the terrain for one decision — whether a rocket aimed
        // straight down has anything under it to go off against. Handed over here
        // rather than at construction because the world outlives neither the bot
        // nor a map change, and this is the only place that has it.
        // ⚠️ Through a local: the field is `null!` on a graphless bot, so testing
        // it directly teaches the compiler it can be null and every later use in
        // this method turns into a warning-as-error.
        var follower = _follower;

        if (follower is not null)
            follower.Collision = world.Collision;

        TrackCarrierProgress(in self, tick);

        // A fresh life starts a fresh regroup hold, randomised so a whole team
        // does not leave on one frame.
        if (!AliveLast)
            RegroupUntil = tick + (int)Random.Next(BotConstants.RegroupTicks);

        AliveLast = true;

        UpdateObjective(in world, slot, tick);

        // The query is rebuilt from LIVE state every tick, not held. Both class
        // arcs are conditional on things that change under a bot: a scout can be
        // re-classed mid-life, and CanRocketJump is the WEAPON plus an hp test, so a
        // soldier's routes shed their rocket jumps on their own as it takes damage
        // — `hp > NAV_RJ_DAMAGE`, strictly greater, because the damage lands before
        // the bot does.
        Query = Query with
        {
            CanDoubleJump = BotClassProfile.AirJump(self.Class),
            CanRocketJump = RocketModel.Capable(in self, in world.Weapon(slot)),
        };

        var navKeys = _graph is null
            ? PlayerKeys.None
            : _follower.Step(Path, in self, in world.Weapon(slot), tick, Query, Occupancy);

        var fireKeys = BotCombat.Step(
            Combat, in world, slot, tick, Skill, ref Random, _graph, out var aim, out var aimDistance);

        // The launch hand-off, and it goes AFTER the combat half because this
        // is the only place that can see both. The follower owns LEFT / RIGHT /
        // JUMP / DOWN and cannot fire; combat owns ATTACK and the aim and knows
        // nothing about the route. A rocket jump needs one key from each, and a
        // sticky jump needs two on two different ticks.
        //
        // The body lives on PathFollower rather than here because a second
        // copy is a harness that measures a different bot — see
        // PathFollower.ApplyLaunch and EdgeFlights.
        PathFollower.ApplyLaunch(Path, ref fireKeys, ref aim);

        navKeys = ApplyHolds(world, slot, tick, navKeys);

        // Dodge an incoming rocket. GROUNDED ONLY, which keeps it out of every
        // planned jump arc without needing to know anything about that arc — and
        // never throttled, because it is a reflex to a thing already in the air.
        //
        // ORing is safe because JUMP is EDGE-TRIGGERED in Begin Step: a press
        // cannot double a jump the follower already wants.
        //
        // And it is BotReflex.RocketDodge, not a proximity test. A radius cannot
        // tell a rocket coming in at the knees (jump) from one passing over the head
        // (jumping is what puts the bot in front of it), and getting that backwards
        // made a bot a guaranteed hit for anyone who aimed high.
        //
        // This is the ONLY thing that presses JUMP outside the path follower. The
        // reference's voluntary combat hops — a blocked-shot peek, an ambient
        // fidget, the scout's mid-air second jump — are deliberately omitted:
        // between them they fired often enough, precisely where the follower has
        // arrived and is returning no movement keys at all, that bots in a
        // firefight read as stuck in place jumping.
        if (self.OnGround && BotReflex.RocketDodge(in world, slot))
            navKeys |= PlayerKeys.Jump;

        // Grounded, nothing to walk to, and not deliberately standing to fight:
        // this bot is parked rather than busy. Read here rather than anywhere
        // earlier because this is the only line that has seen both halves decide
        // — a bot held by the engage rule or by the regroup hold is standing still
        // on purpose, and neither is a bot that needs a new destination.
        //
        // ⚠️ NOT cleared where a goal is issued, which is the trap this walked
        // into once. A parked bot's objective tick re-issues the point it is
        // already standing on every BOT_OBJECTIVE_PERIOD — arrival cleared the
        // goal, so the retarget throttle has nothing to compare against and never
        // fires. Clearing on the issue capped the counter at 30 and no re-post
        // ever happened. This line clears itself instead, the moment the bot
        // moves.
        //
        // ⚠️ It asks what the bot DID, not what it believed, and the difference
        // is a whole second class of statue. A bot holding a live route it is
        // making no progress on presses nothing and reports no goal trouble at
        // all — measured on ctf_truefort, a defending pyro sat on leg 7 of 8 with
        // `HasPath`, `StuckTicks` 0 and no key pressed for 1,629 ticks, waiting on
        // a descent that never came. `StuckTicks` cannot see it: that detector
        // counts a bot pressing a direction and going nowhere, and this one is
        // pressing nothing. Any test keyed on the follower's own state shares the
        // follower's blind spot, so this one is keyed on the position.
        var idle = self.OnGround
            && !WantsToEngage
            && tick >= RegroupUntil
            && (navKeys & (PlayerKeys.Left | PlayerKeys.Right)) == 0
            && Gml.Equal(self.X, _parkedX)
            && Gml.Equal(self.Y, _parkedY);

        _parkedX = self.X;
        _parkedY = self.Y;

        ParkedTicks = idle ? ParkedTicks + 1 : 0;

        return PlayerInput.FromAim(navKeys | fireKeys, aim, aimDistance);
    }

    /// <summary>
    /// The three behaviours that override the follower's horizontal keys.
    /// </summary>
    /// <remarks>
    /// <b>Order matters and the back-off is last.</b> The engage hold and the
    /// back-off disagree only between a class's minimum band and its close-in
    /// distance, and there the retreat is right: a pyro closing on somebody
    /// already in its face should back off first and close second, not alternate.
    /// </remarks>
    /// <summary>
    /// The objective cadence: decide where to go, and reissue the goal only if it
    /// actually moved.
    /// </summary>
    /// <remarks>
    /// <b>Reissuing is deliberately cheap to skip.</b> Setting a goal forces an
    /// immediate re-plan, so calling it every tick with an unchanged destination
    /// would fight the very re-plan timer the follower was built around — hence the
    /// <c>BOT_GOAL_RETARGET_DIST</c> test rather than an unconditional set. A
    /// stationary objective is set once and then left alone, and a moving one (a
    /// flag carrier) naturally trips the 48px test, which is the case it was written
    /// for.
    /// </remarks>
    private void UpdateObjective(in WorldView world, int slot, int tick)
    {
        if (Objective is null || GoalLocked)
            return;

        // Notice a spot that never planned BEFORE deciding again, or the ban is
        // always one objective tick behind the goal that earned it. HasGoal is what
        // separates "planning failed" from "arrived": arrival clears the goal and
        // frees the path together, so a bot that got there has neither.
        if (_goalIsSpot && Path.HasGoal && !Path.HasPath)
            SpotBanUntil = tick + SpotBanTicks;

        // The reference's `(tick + player) mod BOT_OBJECTIVE_PERIOD == 0`: staggered
        // per bot so a server full of them does not pay every objective scan on one
        // frame.
        if ((tick + Combat.Stagger) % BotConstants.ObjectivePeriod != 0)
            return;

        // The combat half's target is handed over, and only the medic reads it:
        // its follow position is on the far side of its patient FROM the enemy. It
        // is passed rather than looked up because BotCombat has already re-validated
        // it this tick, and a second lookup could disagree with the one the bot is
        // actually aiming at.
        var foe = Combat.Target.Kind == BotTargetKind.Character ? Combat.Target.Slot : (int?)null;

        var reposting = ParkedTicks >= RepostTicks;

        var goal = Objective.Decide(
            in world, slot, Role, SpreadX, allowSpot: tick >= SpotBanUntil, target: foe,
            reposting: reposting);

        // No spread on a re-post. The search has already picked the node to stand
        // on and excluded everything near the bot; a lateral offset of up to
        // BOT_SPREAD_MAX would put the goal back inside the radius the search just
        // cleared, and the bot would not move.
        var spread = goal.MustTouch || reposting ? 0 : SpreadX;

        if (!goal.Found || !Objective.TryResolve(in goal, spread, out var x, out var y))
            return;

        // The throttle is on the RESOLVED node position, not on the raw
        // objective. Two objectives a pixel apart resolve to the same node's same
        // column, and it is the column the follower will actually walk to — so
        // comparing anything else re-issues a goal that has not moved.
        if (Path.HasGoal && Gml.PointDistance(x, y, Path.GoalX, Path.GoalY) <= BotConstants.GoalRetargetDist)
            return;

        // Counted only when the new goal is somewhere else. A re-post whose spot
        // search found nothing falls through to the objective's own point, which
        // for a parked bot is the node it is already standing on — issuing that
        // is harmless and is not a re-post.
        if (reposting
            && Gml.PointDistance(x, y, world.Character(slot).X, world.Character(slot).Y)
                > BotConstants.GoalRetargetDist)
        {
            Path.Reposts++;
        }

        _goalIsSpot = goal.IsSpot;
        SetGoal(x, y);
    }

    private PlayerKeys ApplyHolds(in WorldView world, int slot, int tick, PlayerKeys navKeys)
    {
        ref readonly var self = ref world.Character(slot);

        // On the ground and not part-way through a validated arc, for every
        // one of these. Stripping LEFT/RIGHT while the follower is tracking a
        // jump is not a pause, it is a different jump: the arc needs a key almost
        // every tick to stay on, and a bot that stops pressing at the apex lands
        // short of where the arc was proved to reach.
        var free = self.OnGround && !Path.FlyingEdge;

        if (!free)
            return navKeys;

        if (tick < RegroupUntil)
        {
            var mates = CountMatesNear(in world, slot);

            // Wait for company, not for a crowd. BOT_REGROUP_MATES is 1 rather
            // than 2 so the unit that forms is a PAIR: with 2 every bot on a
            // freshly spawned team satisfied the test on the same frame and the
            // whole team left as one body — which is the clumping the hold exists
            // to stop, and a team moving as one body also has one decision to
            // make and nothing to break a tie with.
            if (mates >= BotConstants.RegroupCrowd || mates >= BotConstants.RegroupMates)
            {
                RegroupUntil = 0;
            }
            else
            {
                navKeys &= ~(PlayerKeys.Left | PlayerKeys.Right);

                // And clear the stuck counter, which is the other half of the
                // same mistake. The follower runs its detector on the keys it
                // INTENDS to press and then returns them for this line to throw
                // away — so a bot in the hold is standing still on purpose while
                // the follower believes it is pressing a direction and going
                // nowhere, and every BOT_STUCK_TICKS it blacklists one of its own
                // spawn room's exits. A 90-tick hold is seven of those windows.
                Path.StuckTicks = 0;
            }
        }

        if (!Combat.Target.Exists || !Combat.Visible)
        {
            // ⚠️ CLEARED, not left alone. `WantsToEngage` is written only by the
            // assignment below, so returning here used to leave the last live
            // answer standing for as long as the bot had nothing in sight — which
            // is the rest of the round for a defender in a quiet sector. The
            // property documents itself as time-bounded by BOT_ENGAGE_TICKS and
            // was not: `HybridBot`'s MacroTask handover reads it as a conjunct and
            // was getting a constant true, and `ParkedTicks` could never start.
            //
            // Only this arm. The `free` return above is a bot in the air, where
            // the hold cannot apply either way and the answer from the ground it
            // left is still the right one to hand a controller mid-arc.
            WantsToEngage = false;
            return navKeys;
        }

        var target = Combat.Target;
        var distance = Gml.PointDistance(self.X, self.Y, target.X, target.Y);

        // Stand and fight instead of walking past. Before this the two halves
        // never negotiated at all — the follower contains no reference to a
        // target — so a bot with a live enemy in front of it kept walking its
        // route and fired sideways as it went. Reported from play as bots running
        // past a player rather than engaging.
        //
        // Four things stop it deciding the round on its own: a carrier never
        // holds (a capture is what ends a round), a bot near its own goal keeps
        // walking (close to the point, taking the point beats winning the fight),
        // the hold expires BOT_ENGAGE_TICKS after the target was ACQUIRED (two
        // bots either side of a gap would otherwise stand still for the rest of
        // the round — and measuring from acquisition rather than from when the
        // hold began is what stops a bot renewing its own stalemate by looking
        // away and back), and a class with a close-in distance walks at its
        // target instead.
        // There is no exception here for "the target is inside my own minimum
        // band, where BotClassKeys will not let me fire". Adding one is the
        // obvious mirror of the pyro's close-in press below — too far to shoot
        // walks in, so too close to shoot should walk out — but it changes
        // nothing measurable: not the scenario suite's exact tick costs, not the
        // intel run, and not the case it was written for. Something else pins a
        // bot that starts inside the band, and until that is found this condition
        // would be an ungated change to shipped behaviour with no demonstrated
        // effect. docs/TRAPS-BOTS.md, "Team Deathmatch", has the measurement and
        // the open question.
        WantsToEngage = !self.CarryingIntel
            && distance <= BotTargeting.ClassRange(self.Class)
            && tick - Combat.TargetAt <= BotConstants.EngageTicks
            && GoalDistance(in self) > BotConstants.EngageGoalNear;

        if (WantsToEngage)
        {
            navKeys &= ~(PlayerKeys.Left | PlayerKeys.Right);
            Path.StuckTicks = 0;

            // A class that has to be NEARER than its attention band before it
            // can shoot walks at the target rather than stopping where the hold
            // caught it. Only the pyro today — its flame reaches a fraction of the
            // distance it can see, so holding at acquisition range is holding out
            // of range for ever.
            //
            // And it is the one press in this file that no route validated,
            // which is why it goes through BotStep: everything else that moves a
            // bot sideways is either an edge the nav generator proved or a single
            // step of back-off, and ctf_truefort has a pit beside the route.
            var closeIn = BotClassProfile.CloseIn(self.Class);

            if (closeIn > 0 && distance > closeIn)
            {
                var stepDir = target.X < self.X ? -1 : 1;

                if (BotStep.Safe(world.Collision, in self, stepDir))
                    navKeys |= stepDir > 0 ? PlayerKeys.Right : PlayerKeys.Left;
            }
        }

        // Back away from anything inside this class's minimum band. Two bots
        // that walk into each other otherwise stand nose to nose with a
        // degenerate aim solve and neither can shoot, and a soldier pinned at
        // point-blank range cannot fire at all without killing itself. It
        // REPLACES the horizontal keys rather than being ORed with them, since
        // pressing both directions at once is pressing neither.
        var minBand = BotTargeting.ClassMinBand(self.Class);

        if (minBand > 0 && distance < minBand)
        {
            navKeys &= ~(PlayerKeys.Left | PlayerKeys.Right);
            navKeys |= target.X > self.X ? PlayerKeys.Left : PlayerKeys.Right;
        }

        return navKeys;
    }

    /// <summary>How far the bot is from its goal, or past the engage threshold.</summary>
    /// <remarks>
    /// A bot with no goal is treated as <em>far</em> from one, so the engage
    /// hold applies. The alternative — treating it as arrived — would make a bot
    /// with nothing to do refuse to stand and fight.
    /// </remarks>
    private double GoalDistance(in CharacterState self) =>
        Path.HasGoal
            ? Gml.PointDistance(self.X, self.Y, Path.GoalX, Path.GoalY)
            : BotConstants.EngageGoalNear + 1;

    /// <summary>Live team-mates within the regroup radius, excluding this bot.</summary>
    private static int CountMatesNear(in WorldView world, int slot)
    {
        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;
        var mates = 0;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (other == slot || !world.State.Alive[other])
                continue;

            ref readonly var mate = ref world.Character(other);

            if (world.Player(other).Team != team || mate.Hp <= 0)
                continue;

            if (Gml.PointDistance(mate.X, mate.Y, self.X, self.Y) <= BotConstants.RegroupRadius)
                mates++;
        }

        return mates;
    }

    // --- the stuck-carrier failsafe ------------------------------------------

    /// <summary>
    /// How long a carrier may make no headway before it puts the briefcase down.
    /// </summary>
    /// <remarks>
    /// Ten seconds. Long enough that a firefight, a regroup hold or a jump
    /// window never trips it — a healthy carry on <c>ctf_truefort</c> is about
    /// 1,760 ticks of continuous movement — and short enough that a round is not
    /// spent waiting.
    /// </remarks>
    public const int CarrierStuckTicks = 300;

    /// <summary>How far it must get in that time to count as headway.</summary>
    private const double CarrierProgressDistance = 24.0;

    /// <summary>
    /// The hard ceiling on holding a briefcase, however much the bot is moving.
    /// </summary>
    /// <remarks>
    /// <b>The headway check is not enough on its own, and this is the case
    /// that proves it.</b> A carrier can circle a pair of surfaces indefinitely
    /// — genuinely covering ground, so the 24px test never fires — and hold the
    /// intel for a whole round. Measured on <c>ctf_truefort</c>: three of ten
    /// uncontested seeds, the carrier ranging up to 1,666px around the *enemy*
    /// base for ~26,000 ticks, capturing nothing.
    /// <para>
    /// Four thousand ticks is a bit over two minutes. A healthy carry is about
    /// 1,760 ticks, and the longest carry measured in *contested* play — where a
    /// carrier legitimately holds it through fights — was 3,180. So this cannot
    /// fire on a bot that is making a real attempt; it fires on one that has
    /// stopped making progress toward anything.
    /// </para>
    /// </remarks>
    public const int CarrierHoldCeiling = 4000;

    private double _carrierMarkX;
    private double _carrierMarkY;
    private int _carrierStuckSince = -1;
    private int _carryingSince = -1;
    private bool _dropIntel;

    /// <summary>Ticks the bot has been carrying without headway.</summary>
    public int CarrierStuckTicksNow { get; private set; }

    /// <summary>
    /// Watches a carrier for a total stall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failsafe, not a fix.</b> The bug it exists for — three re-plan
    /// recovery paths passing the goal's <c>y</c> as the bot's own, so the start
    /// node resolved to nothing and every re-plan failed for ever — is fixed in
    /// <c>PathFollower</c>. This catches the next one. A briefcase held by a
    /// motionless bot is the worst failure the objective has: it does not end the
    /// round, it does not show up as a loss, and it removes the objective from
    /// play until the map changes.
    /// </para>
    /// <para>
    /// <b>It lives in the BOT and not in the simulation, and that is not a
    /// preference.</b> A bot pressing "drop intel" is a thing a player can do —
    /// <c>PLAYER_DROP_INTEL</c>, through the same command dispatch a socket
    /// reaches. A rule in <c>MatchSimulation</c> that took a briefcase off a
    /// motionless carrier would be behaviour the reference does not have, which
    /// is a wire-visible divergence from the stock game: CLAUDE.md's line is
    /// compatible in observable simulation, free everywhere else, and this is
    /// squarely on the observable side.
    /// </para>
    /// </remarks>
    private void TrackCarrierProgress(in CharacterState self, int tick)
    {
        if (!self.CarryingIntel)
        {
            _carrierStuckSince = -1;
            _carryingSince = -1;
            CarrierStuckTicksNow = 0;
            return;
        }

        if (_carryingSince < 0)
            _carryingSince = tick;

        // The hard ceiling, checked before the headway test: a bot circling a
        // pair of surfaces is moving, so only this one catches it.
        if (tick - _carryingSince >= CarrierHoldCeiling)
        {
            _dropIntel = true;
            _carryingSince = tick;
            _carrierStuckSince = -1;
            CarrierStuckTicksNow = 0;
            return;
        }

        if (_carrierStuckSince < 0
            || Gml.PointDistance(self.X, self.Y, _carrierMarkX, _carrierMarkY) > CarrierProgressDistance)
        {
            _carrierMarkX = self.X;
            _carrierMarkY = self.Y;
            _carrierStuckSince = tick;
            CarrierStuckTicksNow = 0;
            return;
        }

        CarrierStuckTicksNow = tick - _carrierStuckSince;

        if (CarrierStuckTicksNow < CarrierStuckTicks)
            return;

        // Ask once, then re-arm from wherever the drop leaves it.
        _dropIntel = true;
        _carrierStuckSince = -1;
        CarrierStuckTicksNow = 0;
    }

    /// <summary>
    /// The commands this bot wants run — the carrier failsafe, and the sniper's
    /// scope.
    /// </summary>
    /// <remarks>
    /// Through <see cref="ICommandSource"/> and therefore through the one
    /// dispatch a socket and the control port also reach, so the bot cannot do
    /// anything a client could not. Anything added here has to hold that.
    /// </remarks>
    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
    {
        if (_dropIntel)
        {
            _dropIntel = false;
            commands.Push(PlayerCommand.DropIntel());
        }

        if (WantsZoom(in world, slot))
            commands.Push(PlayerCommand.ToggleZoom());
    }

    /// <summary>
    /// Whether the scope should be toggled this tick.
    /// </summary>
    /// <remarks>
    /// <b><c>toggleZoom</c> is not a key, it is a client command</b> — and a
    /// bot's <see cref="PlayerInput"/> is keys plus an aim, so nothing else here
    /// asks for it; this is what has to. Without it a sniper fires at the
    /// unscoped flat <b>35</b> forever instead of the scoped ramp of <b>45 to
    /// 75</b> over <c>RifleChargeTime</c> (<c>Weapons.RifleBeginStep</c>), and
    /// <see cref="BotClassKeys"/>' charge-proportional-to-range arm — which reads
    /// <c>self.Zoomed</c> — never runs.
    /// <para>
    /// <b>The rule is a TOGGLE, so it must be edge-shaped, not level-shaped.</b>
    /// <c>PlayerCommands.ToggleZoom</c> flips; pushing it every tick a sniper has
    /// a target would scope and unscope at 30 Hz and hold a charge of zero for
    /// ever — which is a worse sniper than the unscoped one this replaces, and is
    /// the obvious way to write this wrong. So: zoom in only while unzoomed and
    /// holding a target, out only while zoomed and holding none.
    /// </para>
    /// <para>
    /// <b>Unscoping when the target is gone is not tidiness.</b> Scoped
    /// <c>RunPower</c> is 0.6 against a sniper's normal run, so a bot that stayed
    /// scoped would cross the map at a third speed and never reach its objective.
    /// The reference's own <c>toggleZoom</c> is what sets that, and the walk back
    /// is what the objective layer assumes.
    /// </para>
    /// <para>
    /// <b>Only the sniper.</b> <c>PlayerCommands.ToggleZoom</c> refuses every
    /// other class, so pushing it elsewhere is a silent no-op rather than an
    /// error — checked here so the buffer is not spent on a command that cannot
    /// run.
    /// </para>
    /// </remarks>
    private bool WantsZoom(in WorldView world, int slot)
    {
        if (!world.IsAlive(slot))
            return false;

        ref readonly var self = ref world.Character(slot);

        if (self.Class != PlayerClass.Sniper)
            return false;

        // The combat half's own answer about whether there is anything to
        // shoot, rather than a second opinion invented here — including its
        // thirty-tick grace after the sightline breaks, so a sniper does not
        // unscope and rescope every time the target steps behind a post.
        return Combat.Target.Exists != self.Zoomed;
    }
}
