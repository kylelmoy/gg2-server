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
/// ⚠️ <b>The two halves are ORed and cannot conflict</b>: <see cref="BotCombat"/>
/// owns ATTACK and SPECIAL, <see cref="PathFollower"/> owns LEFT / RIGHT / JUMP /
/// DOWN. The aim comes from combat alone — the follower has no opinion about it.
/// </para>
/// <para>
/// ⚠️ <b>Combat runs every tick, and so does the follower.</b> Aiming may lag and
/// look human; steering may not — a jump missed by one tick is a bot standing at
/// the edge of a gap for ever. What is cadenced is the <em>target search</em> and
/// the <em>re-plan</em>, both inside their own halves.
/// </para>
/// <para>
/// <b>Three behaviours live here rather than in either half</b>, because here is
/// the only place that can see both: the regroup hold, the engage hold and the
/// back-off band. Each of them overrides the follower's horizontal keys, and each
/// is gated on being <em>on the ground and not part-way through a validated
/// arc</em> — ⚠️ stripping LEFT/RIGHT during a jump is not a pause, it is a
/// different jump, and the follower is holding a position-per-tick trajectory
/// that needs a key almost every tick to stay on.
/// </para>
/// <para>
/// <b>Not ported, each conservative when absent:</b> the rocket dodge
/// (<c>botRocketDodge</c>) so a bot never hops away from an incoming rocket; the
/// close-in step (<c>botStepSafe</c>) so a pyro holds where the engage caught it
/// rather than walking at its target; and the objective layer
/// (<c>botObjectiveUpdate</c>) so a goal has to be given from outside.
/// </para>
/// </remarks>
public sealed class ScriptedBot : IInputSource, IScenarioSubject
{
    /// <summary>How long a spot that never planned is banned for.</summary>
    /// <remarks>The reference's own literal 300, ten seconds.</remarks>
    private const int SpotBanTicks = 300;

    private PathFollower _follower;
    private NavGraph? _graph;

    private bool _goalIsSpot;

    /// <summary>
    /// Builds a bot for one map.
    /// </summary>
    /// <param name="graph">
    /// The map's nav graph, or null. ⚠️ <b>Null means the bot never walks
    /// anywhere</b> — it still aims and fires, which is exactly what a scenario
    /// about combat wants and exactly what a routing one must not have.
    /// </param>
    /// <param name="stagger">
    /// This bot's offset into the shared cadences, normally its roster slot.
    /// ⚠️ <b>The two staggers are different things.</b>
    /// <see cref="BotCombatState.Stagger"/> is a phase; the path one is
    /// <em>added</em> to the re-plan period, and at zero the interval is 45 ticks
    /// against a 60-tick takeoff budget — so the jump escape hatch can never
    /// fire. See CLAUDE.md, "Phase 7 item 4b-ii".
    /// </param>
    public ScriptedBot(NavGraph? graph, NavQuery query, BotSkill skill, int stagger = 0)
    {
        _graph = graph;
        _follower = graph is null ? null! : new PathFollower(graph);

        Query = query;
        Skill = skill;
        Path = new BotPathState { ReplanStagger = stagger };
        Combat = new BotCombatState { Stagger = stagger };

        // ⚠️ Seeded from the stagger, so two bots on one team roll differently
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
    /// ⚠️ <b>Bots arrive in clumps rather than in single file.</b> They stream
    /// toward the objective one at a time and get killed piecemeal because each
    /// leaves the instant it can; a human waits a moment for whoever else is
    /// about to spawn. The hold is short, randomised per life so a whole team
    /// does not leave on the same frame, and ends early the moment enough
    /// team-mates are actually nearby — which is the condition it is really
    /// waiting for.
    /// <para>
    /// ⚠️ <b>Only movement is held.</b> The bot still aims, still shoots and
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
    /// ⚠️ <b>Its own, and NOT <see cref="WorldState.Random"/>, and that is a rule
    /// rather than a convenience.</b> The match's stream is <em>observable
    /// simulation</em>: every seeded weapon puts its draw on the wire and every
    /// client re-derives the spread from it (CLAUDE.md, "GM8's RNG is Delphi's").
    /// A bot drawing from it would make the stream's position depend on how many
    /// bots were thinking, which is a divergence no client could follow and which
    /// the parity harness would report as a wrong shot.
    /// <para>
    /// ⚠️ <b>It is also why <see cref="WorldView"/> being read-only is not in the
    /// way here.</b> A first draft copied <c>world.State.Random</c> into a local
    /// and passed it by reference: the copy advanced, the world's did not, and
    /// every tick drew the <em>same</em> numbers — an aim error that never
    /// changed and a hold-fire roll that never varied. Silent, and exactly the
    /// kind of thing a determinism test passes.
    /// </para>
    /// <para>
    /// ⚠️ <b>It has to be part of any snapshot.</b> docs/BOTAI.md item 2 asks for
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
    /// ⚠️ <b>Null is not a degraded mode.</b> A scenario, a test and a rollout
    /// evaluator all want a bot they can point at something, and the reference has
    /// the same escape (<c>botGoalLocked</c>). What null costs is exactly the
    /// objective layer and nothing else.
    /// </remarks>
    public BotObjective? Objective { get; set; }

    /// <summary>
    /// Which half of the team this bot is on. ⚠️ <b>Assigned once at join, never
    /// per tick</b> — see <see cref="BotRoles"/>.
    /// </summary>
    public BotRole Role { get; set; }

    /// <summary>
    /// This bot's constant lateral goal offset — <see cref="BotRoles.SpreadX"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ It has to be constant for the bot's whole life: a spread that changes
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
    /// ⚠️ <b>A spot is chosen without checking reachability</b>
    /// (<see cref="BotGoalSpot"/>), so one in another component of the map presents
    /// as a goal that never plans. This is the only place that can notice — a goal
    /// still held with no route behind it — and the answer is to stop trying spots
    /// for a while rather than leave the bot standing at spawn wanting something it
    /// cannot walk to.
    /// <para>
    /// ⚠️ <b>UNGATED, and deliberately recorded as such.</b> Deleting the ban leaves
    /// every test green, checked by mutation. What it needs is a spot that resolves
    /// and cannot be reached, which needs a nav graph with two components — and every
    /// shipped graph is connected for the team that owns the ground, so the state is
    /// not reachable from a real map. Building a synthetic two-component graph would
    /// gate it; nothing else will, and a green suite is not evidence this works.
    /// </para>
    /// </remarks>
    public int SpotBanUntil { get; set; }

    /// <summary>
    /// Moves this bot to a new map.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A follower, a route and a blacklist are all indices into ONE graph</b>,
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

        Path.FreePath();
        Path.HasGoal = false;
        Path.Blacklist.Clear();
        Path.BlacklistOrder.Clear();

        Objective = null;
        _goalIsSpot = false;
        SpotBanUntil = 0;
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
            // ⚠️ A dead bot gives up its route rather than resuming one from
            // where it died: it will come back somewhere else entirely.
            Path.FreePath();
            AliveLast = false;
            return PlayerInput.None;
        }

        ref readonly var self = ref world.Character(slot);

        // A fresh life starts a fresh regroup hold, randomised so a whole team
        // does not leave on one frame.
        if (!AliveLast)
            RegroupUntil = tick + (int)Random.Next(BotConstants.RegroupTicks);

        AliveLast = true;

        UpdateObjective(in world, slot, tick);

        // ⚠️ The query is rebuilt from LIVE state every tick, not held. Both class
        // arcs are conditional on things that change under a bot: a scout can be
        // re-classed mid-life, and CanRocketJump is the WEAPON plus an hp test, so a
        // soldier's routes shed their rocket jumps on their own as it takes damage
        // — `hp > NAV_RJ_DAMAGE`, strictly greater, because the damage lands before
        // the bot does.
        Query = Query with
        {
            CanDoubleJump = BotClassProfile.AirJump(self.Class),
            CanRocketJump = RocketModel.Ready(in self, in world.Weapon(slot)),
        };

        var navKeys = _graph is null
            ? PlayerKeys.None
            : _follower.Step(Path, in self, in world.Weapon(slot), tick, Query);

        var fireKeys = BotCombat.Step(
            Combat, in world, slot, tick, Skill, ref Random, _graph, out var aim, out var aimDistance);

        // ⚠️ The rocket-jump hand-off, and it goes AFTER the combat half because
        // this is the only place that can see both. The follower owns LEFT / RIGHT /
        // JUMP / DOWN and cannot fire; combat owns ATTACK and the aim and knows
        // nothing about the route. A rocket jump needs one key from each.
        //
        // ⚠️ It OVERRIDES the combat aim rather than being ORed with it. Whatever
        // the bot was shooting at, the arc it is about to fly is worth more than the
        // shot, and half an aim is a rocket into a wall beside its own feet.
        // Character's Begin Step reads aimDirection when it fires the weapon and
        // reads the ATTACK bit in the same event on the same tick, so the aim must
        // be down BEFORE the trigger is seen — writing both here is exactly that.
        if (Path.RocketFire)
        {
            aim = PathFollower.RocketAimDegrees;
            fireKeys |= PlayerKeys.Attack;
        }

        navKeys = ApplyHolds(world, slot, tick, navKeys);

        // ⚠️ Dodge an incoming rocket. GROUNDED ONLY, which keeps it out of every
        // planned jump arc without needing to know anything about that arc — and
        // never throttled, because it is a reflex to a thing already in the air.
        //
        // ⚠️ ORing is safe because JUMP is EDGE-TRIGGERED in Begin Step: a press
        // cannot double a jump the follower already wants.
        //
        // ⚠️ And it is BotReflex.RocketDodge, not a proximity test. A radius cannot
        // tell a rocket coming in at the knees (jump) from one passing over the head
        // (jumping is what puts the bot in front of it), and getting that backwards
        // made a bot a guaranteed hit for anyone who aimed high.
        //
        // This is the ONLY thing that presses JUMP outside the path follower. The
        // voluntary combat hops that used to sit here in the reference — a
        // blocked-shot peek, an ambient fidget, the scout's mid-air second jump —
        // were removed: between them they fired often enough, precisely where the
        // follower has arrived and is returning no movement keys at all, that bots
        // in a firefight read as stuck in place jumping.
        if (self.OnGround && BotReflex.RocketDodge(in world, slot))
            navKeys |= PlayerKeys.Jump;

        return PlayerInput.FromAim(navKeys | fireKeys, aim, aimDistance);
    }

    /// <summary>
    /// The three behaviours that override the follower's horizontal keys.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Order matters and the back-off is last.</b> The engage hold and the
    /// back-off disagree only between a class's minimum band and its close-in
    /// distance, and there the retreat is right: a pyro closing on somebody
    /// already in its face should back off first and close second, not alternate.
    /// </remarks>
    /// <summary>
    /// The objective cadence: decide where to go, and reissue the goal only if it
    /// actually moved.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Reissuing is deliberately cheap to skip.</b> Setting a goal forces an
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

        // ⚠️ Notice a spot that never planned BEFORE deciding again, or the ban is
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

        // ⚠️ The combat half's target is handed over, and only the medic reads it:
        // its follow position is on the far side of its patient FROM the enemy. It
        // is passed rather than looked up because BotCombat has already re-validated
        // it this tick, and a second lookup could disagree with the one the bot is
        // actually aiming at.
        var foe = Combat.Target.Kind == BotTargetKind.Character ? Combat.Target.Slot : (int?)null;

        var goal = Objective.Decide(
            in world, slot, Role, SpreadX, allowSpot: tick >= SpotBanUntil, target: foe);

        if (!goal.Found || !Objective.TryResolve(in goal, goal.MustTouch ? 0 : SpreadX, out var x, out var y))
            return;

        // ⚠️ The throttle is on the RESOLVED node position, not on the raw
        // objective. Two objectives a pixel apart resolve to the same node's same
        // column, and it is the column the follower will actually walk to — so
        // comparing anything else re-issues a goal that has not moved.
        if (Path.HasGoal && Gml.PointDistance(x, y, Path.GoalX, Path.GoalY) <= BotConstants.GoalRetargetDist)
            return;

        _goalIsSpot = goal.IsSpot;
        SetGoal(x, y);
    }

    private PlayerKeys ApplyHolds(in WorldView world, int slot, int tick, PlayerKeys navKeys)
    {
        ref readonly var self = ref world.Character(slot);

        // ⚠️ On the ground and not part-way through a validated arc, for every
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

            // ⚠️ Wait for company, not for a crowd. BOT_REGROUP_MATES is 1 rather
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

                // ⚠️ And clear the stuck counter, which is the other half of the
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
            return navKeys;

        var target = Combat.Target;
        var distance = Gml.PointDistance(self.X, self.Y, target.X, target.Y);

        // ⚠️ Stand and fight instead of walking past. Before this the two halves
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
        if (!self.CarryingIntel
            && distance <= BotTargeting.ClassRange(self.Class)
            && tick - Combat.TargetAt <= BotConstants.EngageTicks
            && GoalDistance(in self) > BotConstants.EngageGoalNear)
        {
            navKeys &= ~(PlayerKeys.Left | PlayerKeys.Right);
            Path.StuckTicks = 0;

            // The close-in step waits on botStepSafe, which is not ported, so a
            // pyro holds where the engage caught it.
        }

        // ⚠️ Back away from anything inside this class's minimum band. Two bots
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
    /// ⚠️ A bot with no goal is treated as <em>far</em> from one, so the engage
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
}
