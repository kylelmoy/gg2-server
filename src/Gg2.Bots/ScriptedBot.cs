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
public sealed class ScriptedBot : IInputSource
{
    private readonly PathFollower _follower;
    private readonly NavGraph? _graph;

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

    /// <summary>Sends the bot somewhere. Null graph means it cannot go.</summary>
    public bool SetGoal(double worldX, double worldY) =>
        _graph is not null && _follower.SetGoal(Path, worldX, worldY);

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

        var navKeys = _graph is null
            ? PlayerKeys.None
            : _follower.Step(Path, in self, tick, Query);

        var fireKeys = BotCombat.Step(
            Combat, in world, slot, tick, Skill, ref Random, _graph, out var aim, out var aimDistance);

        navKeys = ApplyHolds(world, slot, tick, navKeys);

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
