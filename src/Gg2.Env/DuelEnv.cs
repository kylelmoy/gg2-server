using System.Runtime.InteropServices;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Env;

/// <summary>
/// One duel: reset it, step it with actions, and read back reward components.
/// </summary>
/// <remarks>
/// <b><c>docs/ENV.md</c> step 2</b>, wrapping <see cref="ScenarioWorld"/> —
/// which is <c>docs/BOTAI.md</c> item 11, a world with no gamemode in it. That
/// matters more than it looks: a duel must not have a round clock, a capture, an
/// autobalance or a respawn wave happening underneath it, because every one of
/// those would move the reward without the agent doing anything.
/// <para>
/// <b>A death is final here, and that is inherited rather than arranged.</b>
/// <see cref="ScenarioWorld.Step"/> passes no <c>SpawnPoints</c> to the tick, so
/// the alarm stage cannot respawn anybody. That is what makes a damage figure
/// mean one life.
/// </para>
/// <para>
/// <b>Not thread-safe, and deliberately not.</b> <see cref="EnvBatch"/> gets
/// its parallelism by owning one <see cref="DuelEnv"/> per world, each with its
/// own <see cref="CollisionWorld"/> and RNG — the same shape
/// <c>MatchRunner</c> uses. Sharing one env across threads would be a race on
/// <see cref="WorldState"/>.
/// </para>
/// </remarks>
public sealed class DuelEnv
{
    private readonly string _mapDirectory;
    private readonly string? _navDirectory;

    private ScenarioWorld? _world;
    private DuelSpec _spec;
    private int _decisions;
    private Termination _termination;

    // The bot behind a non-client slot, or null. Rebuilt on every Reset
    // rather than reused: ScriptedBot carries a route, a target and four clocks
    // that belong to one life, and a bot that started an episode mid-plan would
    // make the episode depend on the last one.
    // Held apart from _redBot/_blueBot on purpose: a shadow is never installed
    // as an input source. The moment it were, it would be driving.
    private RolloutSearch? _redShadow;

    private RolloutSearch? _blueShadow;

    private IInputSource? _redBot;
    private IInputSource? _blueBot;

    public DuelEnv(string mapDirectory, string? navDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(mapDirectory);

        _mapDirectory = mapDirectory;
        _navDirectory = navDirectory;
    }

    /// <summary>The decoder, whose aim state belongs to this episode.</summary>
    public ActionDecoder Decoder { get; } = new();

    /// <summary>
    /// The macro goal each slot is being asked to pursue.
    /// </summary>
    /// <remarks>
    /// Indexed by roster slot, all <c>default</c> — "engage the nearest enemy" —
    /// until something sets one.
    /// </remarks>
    private readonly AgentGoal[] _goals = new AgentGoal[WorldState.MaxSlots];

    /// <summary>What slot <paramref name="slot"/> is being asked to pursue.</summary>
    public AgentGoal GoalFor(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, WorldState.MaxSlots);

        return _goals[slot];
    }

    /// <summary>
    /// Sets the macro goal the observation's goal block reports for this slot.
    /// </summary>
    /// <remarks>
    /// <b><c>gg2-rl</c> phase 4.</b> The scripted layer decides the goal, the
    /// learned policy always drives keys and aim.
    /// <para>
    /// <b>A goal survives the auto-reset at an episode boundary</b>, and that
    /// is a decision rather than an oversight. A goal is a property of the slot
    /// that the trainer set, not of the episode; clearing it would mean a trainer
    /// had to re-set it after every reset, and <c>EnvBatch.Step</c> resets
    /// <em>inside</em> the call, so the moment to do that is invisible until the
    /// reply is read. Losing a goal silently would train the policy on "engage"
    /// labels while the trainer believed it had asked for something else.
    /// </para>
    /// <para>
    /// <b>Refuses an out-of-range kind</b> rather than clamping it, because
    /// <c>OneHot</c> writes no column at all for one and the policy would read
    /// that as "no goal".
    /// </para>
    /// </remarks>
    public void SetGoal(int slot, AgentGoal goal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, WorldState.MaxSlots);

        goal.Validate();
        _goals[slot] = goal;
    }

    /// <summary>
    /// Restricts this slot's search to these candidate indices for its next
    /// decision.
    /// </summary>
    /// <remarks>
    /// <b><c>gg2-rl</c>'s <c>docs/DESIGN.md</c> step 3</b> — a distilled policy
    /// prunes the search so it is stronger at equal cost. See
    /// <see cref="RolloutSearch.RestrictNextDecision"/>, which holds every rule
    /// that matters: an empty span means "search everything", the indices are a
    /// SET rather than a ranking, and an out-of-range one is refused rather than
    /// clamped.
    /// <para>
    /// <b>Refused when the slot has no search behind it.</b> A restriction on a
    /// client- or bot-driven slot restricts nothing, and accepting it silently
    /// would let a trainer believe it was pruning while it paid for a full search
    /// — which looks like "pruning does not help" and is not.
    /// </para>
    /// </remarks>
    public void RestrictSearch(int slot, ReadOnlySpan<int> indices)
    {
        var search = SearchFor(slot)
            ?? throw new InvalidOperationException($"slot {slot} has no search to restrict");

        // The class comes from the spec, so the bound is right on step 0. See
        // TrySearchShape.
        TrySearchShape(slot, out _, out var playerClass);

        search.RestrictNextDecision(indices, playerClass);
    }

    /// <summary>How many candidates the search behind this slot has, or 0 for none.</summary>
    /// <remarks>
    /// <b>Per class</b> — a sniper's set carries the zoom dimension and is
    /// twice the size — so a caller validating indices has to ask per slot rather
    /// than assume a batch-wide number.
    /// <para>
    /// <b>From the spec, not from the live search.</b> <c>RolloutSearch.CandidateCount</c>
    /// is the set currently built — 60 for a sniper until its first decision — so
    /// reading indices against it would refuse a valid index 76 on step 0 only.
    /// <see cref="TrySearchShape"/>.
    /// </para>
    /// </remarks>
    public int CandidateCountFor(int slot) =>
        TrySearchShape(slot, out var options, out var playerClass)
            ? RolloutSearch.CandidateCountFor(options, playerClass)
            : 0;

    /// <summary>
    /// Which candidate index each entry of <see cref="ScoresFor"/> belongs to.
    /// </summary>
    public ReadOnlySpan<int> SearchedIndicesFor(int slot) =>
        SearchFor(slot) is { } search ? search.LastSearchedIndices : ReadOnlySpan<int>.Empty;

    /// <summary>The red agent's roster slot.</summary>
    public int RedSlot { get; private set; } = -1;

    /// <summary>The blue agent's roster slot.</summary>
    public int BlueSlot { get; private set; } = -1;

    /// <summary>Decisions taken since the last reset.</summary>
    public int Decisions => _decisions;

    /// <summary>Ticks simulated since the last reset.</summary>
    public int Tick => _world?.Tick ?? 0;

    public Termination Termination => _termination;

    /// <summary>
    /// The bot behind a slot, or null when the client drives it.
    /// </summary>
    /// <remarks>
    /// <b>For inspection, not for wiring.</b> A gate needs to be able to ask
    /// whether a scripted opponent really was re-pointed at the agent, and that
    /// is bot-internal state nothing on the wire carries — the same argument
    /// <c>IScenarioSubject.HasTarget</c> exists for. Reaching in here to change a
    /// bot mid-episode would make the episode depend on something outside the
    /// spec and the seed.
    /// </remarks>
    public IInputSource? BotFor(int slot) =>
        slot == RedSlot ? _redBot : slot == BlueSlot ? _blueBot : null;

    /// <summary>
    /// Every candidate's score behind a slot, or empty when nothing searched.
    /// </summary>
    /// <remarks>
    /// <b>Empty rather than zeros for a non-search slot.</b> Zero is a
    /// meaningful score — it is precisely the blind case, see
    /// <see cref="StepResult.BestScore"/> — so a block of them would read as a
    /// search that found nothing rather than as a slot that never searched.
    /// <para>
    /// <b>A span over the search's own buffer, valid until its next
    /// decision.</b> A caller serialising it copies; nothing here allocates.
    /// </para>
    /// </remarks>
    public ReadOnlySpan<double> ScoresFor(int slot) =>
        SearchFor(slot) is { } search ? search.LastScores : ReadOnlySpan<double>.Empty;

    /// <summary>
    /// What the search behind a slot chose — the DRIVER's action for a
    /// <see cref="EnvDriver.Rollout"/> side, and what the shadow WOULD have done
    /// for a client-driven one.
    /// </summary>
    /// <remarks>
    /// <b>Not derivable from the distribution.</b> A median of four candidates
    /// share the best score, and the search picks among them on aim error — so
    /// "the argmax" is not one action and a consumer cannot recover the choice
    /// from the scores alone.
    /// </remarks>
    public EnvAction SearchActionFor(int slot) =>
        SearchFor(slot) is { } search ? search.LastAction : EnvAction.Idle;

    /// <summary>
    /// The candidate actions a rollout slot's scores are aligned with, or empty.
    /// </summary>
    /// <remarks>
    /// <b>Fixed for the whole batch</b>, because a duel's classes are fixed by
    /// its spec — which is why this can be answered once rather than per step.
    /// <para>
    /// <b>Derived from the spec rather than read off the live search</b>, so it
    /// is answerable before the first <see cref="Step"/> and cannot disturb
    /// <c>RolloutSearch.LastCandidates</c>.
    /// </para>
    /// </remarks>
    public ReadOnlySpan<EnvAction> CandidatesFor(int slot) =>
        TrySearchShape(slot, out var options, out var playerClass)
            ? RolloutSearch.CandidatesFor(options, playerClass)
            : ReadOnlySpan<EnvAction>.Empty;

    /// <summary>
    /// The options and class a searching slot's candidate set is shaped by, from
    /// the SPEC rather than from the live search.
    /// </summary>
    /// <remarks>
    /// <b>One decision, used by everything that asks about a slot's candidate
    /// set.</b> <see cref="CandidatesFor"/>, <see cref="CandidateCountFor"/> and
    /// <see cref="RestrictSearch"/> all come here rather than reading the live
    /// <c>RolloutSearch.CandidateCount</c>, which reads 60 for a sniper until its
    /// first decision has searched and would refuse a valid index 76 on step 0.
    /// <para>
    /// Answerable before the first <see cref="Step"/>, which is the property
    /// that makes it usable for validating a restriction that arrives with the
    /// first step.
    /// </para>
    /// </remarks>
    private bool TrySearchShape(int slot, out RolloutOptions options, out PlayerClass playerClass)
    {
        if (slot == RedSlot && (_spec.RedDriver == EnvDriver.Rollout || _spec.RedShadowSearch))
        {
            options = new RolloutOptions(
                HorizonTicks: _spec.RedHorizonTicks,
                Opponent: _spec.RedRolloutOpponent,
                CommitTicks: _spec.RedCommitTicks,
                ContinuationPolicy: _spec.RedContinuationPolicy);
            playerClass = _spec.RedClass;

            return true;
        }

        if (slot == BlueSlot && (_spec.BlueDriver == EnvDriver.Rollout || _spec.BlueShadowSearch))
        {
            options = new RolloutOptions(
                HorizonTicks: _spec.BlueHorizonTicks,
                Opponent: _spec.BlueRolloutOpponent,
                CommitTicks: _spec.BlueCommitTicks,
                ContinuationPolicy: _spec.BlueContinuationPolicy);
            playerClass = _spec.BlueClass;

            return true;
        }

        options = new RolloutOptions();
        playerClass = default;

        return false;
    }

    /// <summary>The live world, for an observation writer to read.</summary>
    public ScenarioWorld World =>
        _world ?? throw new InvalidOperationException("Reset has not been called");

    /// <summary>
    /// Starts a fresh episode.
    /// </summary>
    /// <remarks>
    /// <b>Red is placed before blue, always.</b> A slot index is a roster
    /// index everywhere in this project, and the observation layout names the
    /// agent by slot — so a reset that placed them in a different order on a
    /// different run would hand the policy a mirrored world without saying so.
    /// </remarks>
    public void Reset(DuelSpec spec, uint seed)
    {
        if (spec.ActionRepeat < 1)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.ActionRepeat, "action repeat must be at least 1");

        if (spec.DecisionCap < 1)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.DecisionCap, "decision cap must be at least 1");

        _spec = spec;
        _decisions = 0;
        _termination = Termination.Running;

        spec.Validate();

        var world = ScenarioWorld.Load(spec.Map, area: 1, _mapDirectory, _navDirectory);

        // The match's own stream, seeded per world. Every seeded weapon draws
        // from it and puts the draw on the wire, so two worlds sharing a seed
        // would fire identical spreads — which is exactly what a batch must not
        // do. EnvBatch derives each world's seed from the base and the index, so
        // batch size cannot move any world's stream.
        //
        // `uint`, matching GmlRandom, and not widened here: GML's generator
        // has a 32-bit seed and pretending to accept 64 would throw away half
        // of whatever a trainer sent without saying so.
        //
        // Seeded before placement, which is load-bearing now that a placement
        // can be sampled: a sampled spawn draws from this stream, so seeding
        // afterwards would make the draw come out of whatever the previous
        // episode left and the world would not be reproducible from its seed.
        // The explicit path is unaffected, because `ScenarioWorld.Place` names
        // its spawn point rather than drawing one.
        world.State.Random = new GmlRandom(seed);

        if (spec.SeparationBanded)
        {
            // ScenarioWorld's, not this class's. A banded pair is map and nav
            // knowledge, and it belongs beside PlaceAtSpawn for the same reason
            // that one does: a caller with no business knowing a map's geometry
            // should not have to invent it. It also has to be reachable from the
            // bot layer's own tests, which must not depend on this assembly —
            // see RolloutSearchTests, whose duel figure is banded because a
            // fixed-position one says almost nothing about the bot.
            (RedSlot, BlueSlot) = world.PlaceInBand(
                spec.RedClass, spec.BlueClass, spec.MinSeparation, spec.MaxSeparation);
        }
        else
        {
            RedSlot = Place(world, spec.RedClass, Team.Red, spec.RedX, spec.RedY);
            BlueSlot = Place(world, spec.BlueClass, Team.Blue, spec.BlueX, spec.BlueY);
        }

        // Seeded from this world's own seed, so a scripted opponent's stream is
        // a pure function of (base seed, world, episode) exactly as the match's
        // is. Without it every world in a batch would share the bot's stream and
        // the batch-size independence gate would fail — or worse pass, because a
        // stagger of 0 is deterministic and the dependence would only appear
        // once two worlds took different numbers of decisions.
        //
        // DecisionTicks 1, so the shadow re-searches on every poll and grades
        // every state the client reaches: a labeller wants an opinion about
        // this state, and the default cadence of 4 would repeat the previous
        // decision's answer for three of every four rows and label them as
        // though they were fresh. It also makes the phase guard vacuous here
        // (any commitment is a multiple of 1), which is correct rather than
        // lucky — a shadow's continuation decides every tick because its
        // commitment is measured in single ticks too.
        _redShadow = spec.RedShadowSearch
            ? new RolloutSearch(new RolloutOptions(
                HorizonTicks: spec.RedHorizonTicks, DecisionTicks: 1, Opponent: spec.RedRolloutOpponent,
                CommitTicks: spec.RedCommitTicks, ContinuationPolicy: spec.RedContinuationPolicy))
            : null;

        _blueShadow = spec.BlueShadowSearch
            ? new RolloutSearch(new RolloutOptions(
                HorizonTicks: spec.BlueHorizonTicks, DecisionTicks: 1, Opponent: spec.BlueRolloutOpponent,
                CommitTicks: spec.BlueCommitTicks, ContinuationPolicy: spec.BlueContinuationPolicy))
            : null;

        _redBot = MakeBot(
            spec.RedDriver, spec.RedTier, spec.RedRolloutOpponent, spec.RedHorizonTicks,
            Team.Red, spec.RedClass, world, seed,
            spec.RedCommitTicks, spec.RedContinuationPolicy);
        _blueBot = MakeBot(
            spec.BlueDriver, spec.BlueTier, spec.BlueRolloutOpponent, spec.BlueHorizonTicks,
            Team.Blue, spec.BlueClass, world, seed ^ 0x9E3779B9u,
            spec.BlueCommitTicks, spec.BlueContinuationPolicy);

        // Both agents start facing one another, which is the only neutral
        // choice: facing is observable (it is what the two-pixel box inflation
        // splash is measured against), so "whatever the spawn left" would be a
        // hidden asymmetry.
        //
        // Read off the placed positions, not off the spec. A sampled spec has
        // no coordinates in it, and taking the facing from `spec.RedX <=
        // spec.BlueX` would compare two NaNs — which is false either way, so
        // both agents would face 180 and one of them would start with its back
        // turned.
        var redX = world.State.Characters[RedSlot].X;
        var blueX = world.State.Characters[BlueSlot].X;

        Decoder.SetAim(RedSlot, redX <= blueX ? 0 : 180);
        Decoder.SetAim(BlueSlot, blueX < redX ? 0 : 180);

        // Written onto the characters, not only into the decoder. If it lived
        // in the decoder alone, the first observation of every episode would
        // report the spawn's facing (due east) rather than the one arranged
        // here — aim sin/cos is in the layout. A client-driven slot would hide
        // that, because Decode writes an aim every tick and the character
        // catches up on tick one, but a bot-driven slot supplies its own aim,
        // so an idle opponent would come back having apparently made a 180°
        // turn it never asked for. Facing is observable (it is what the
        // two-pixel box inflation splash is measured against), so "whatever the
        // spawn left" is exactly the hidden asymmetry the paragraph above
        // refuses.
        WriteAim(world, RedSlot, Decoder.Aim(RedSlot));
        WriteAim(world, BlueSlot, Decoder.Aim(BlueSlot));

        _world = world;
    }

    /// <summary>
    /// The bot behind one side, or null when the client drives it.
    /// </summary>
    /// <remarks>
    /// <b><see cref="EnvDriver.Scripted"/> is refused without a nav graph
    /// rather than downgraded.</b> <c>ScriptedBot</c> takes a null graph happily
    /// and then has no follower at all — it fights from where it spawned and
    /// never walks. That is a different opponent, and an experiment reporting
    /// "against the scripted bot" while playing a stationary one is a
    /// misleading result.
    /// </remarks>
    private IInputSource? MakeBot(
        EnvDriver driver, int tier, RolloutOpponent opponent, int horizon, Team team,
        PlayerClass playerClass, ScenarioWorld world, uint seed,
        int commitTicks, PolicyNetwork? continuation)
    {
        switch (driver)
        {
            case EnvDriver.Client:
                return null;

            case EnvDriver.Idle:
                return IdleInput.Instance;

            case EnvDriver.Rollout:
                // Still not the whole RolloutOptions. The aim ladder and the
                // candidate set stay the search's own tuning, because putting them
                // on the wire would make "the search baseline" a different thing
                // per batch. What is exposed is what an experiment has to vary to
                // report a difference: the opponent model, the horizon, and the
                // commitment and the continuation.
                //
                // A zero horizon reaches RolloutOptions.WithDefaults, which is
                // where "0 means the declared default" is already implemented —
                // rather than substituting DefaultHorizonTicks here and making a
                // second place that decides what the default is. The same is true
                // of a zero commitment, which means "the whole horizon" there.
                //
                // DecisionTicks is not passed, so the search decides every 4
                // ticks whatever action repeat the batch asked for. Those are two
                // independent quantities that happen to both be 4 today, which
                // makes the policy continuation's cadence match the batch's by
                // coincidence rather than by construction: if a batch ever runs an
                // action repeat other than 4, the continuation's cadence does not
                // follow, and that is a train/serve mismatch nothing reports. It
                // is also when the measured cost — 1.47 ms at k=20 — stops
                // applying, since that figure is stated in the search's cadence.
                // Wire them together if that case arises.
                return new RolloutSearch(new RolloutOptions(
                    HorizonTicks: horizon,
                    Opponent: opponent,
                    CommitTicks: commitTicks,
                    ContinuationPolicy: continuation));

            case EnvDriver.Scripted:
                if (world.Nav is null)
                {
                    throw new InvalidOperationException(
                        $"a Scripted driver needs a nav graph and {world.Map.Name} has none loaded; "
                        + "give the env a nav directory, or ask for Idle or Rollout");
                }

                // The query's class arcs are rebuilt from live state by
                // ScriptedBot itself every tick, so what is passed here is only
                // the team — see its own remark. Passing CanDoubleJump here would
                // be overwritten on the first tick and read as though it were not.
                return new ScriptedBot(world.Nav, new NavQuery(team), BotSkill.ForTier(tier))
                {
                    Random = new GmlRandom(seed),

                    // The real objective layer, and a duel is a deathmatch.
                    // `ScenarioWorld` places no intel, generator or control point,
                    // so `BotObjective.Decide` falls to its deathmatch branch and
                    // hunts the other agent — the same code path tdm_mantic uses,
                    // rather than a duel-shaped special case. The objective layer
                    // beats a locked chase onto the target in three ways: it
                    // stands at the class's own band instead of walking onto the
                    // target, it picks a real walkable node through the spot
                    // search, and its re-issue is throttled on the resolved node
                    // by BOT_GOAL_RETARGET_DIST rather than by an invented
                    // distance test.
                    Objective = new BotObjective(world.Nav, new SpawnPoints(world.Map)),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(driver), driver, "no such driver");
        }
    }

    /// <summary>
    /// One side's placement: the coordinates the caller named, or a spawn point
    /// drawn from the map.
    /// </summary>
    /// <remarks>
    /// <b>The draw is <c>state.Random.Next(count)</c>, which is exactly what
    /// <c>Timers.PlayerSpawn</c> does for a real respawn</b> — same stream, same
    /// call, so a sampled duel starts where an ordinary spawn would have put
    /// somebody and nothing about the distribution is this project's invention.
    /// </remarks>
    private static int Place(ScenarioWorld world, PlayerClass playerClass, Team team, double x, double y)
    {
        if (!DuelSpec.IsSampled(x))
            return world.Place(playerClass, team, x, y);

        var available = world.SpawnPointCount(team);

        if (available == 0)
            throw new InvalidOperationException(
                $"{world.Map.Name} has no {team} spawn point to sample");

        return world.PlaceAtSpawn(playerClass, team, (int)world.State.Random.Next(available));
    }

    /// <summary>
    /// Takes one decision for each agent and advances the world by the action
    /// repeat.
    /// </summary>
    /// <remarks>
    /// <b>The reward components are accumulated over the repeat's ticks</b>, not
    /// read from the last one. <see cref="TickEvents"/> is cleared every tick,
    /// so reading it after the loop would report the final tick's damage and
    /// silently drop the rest — which at an action repeat of 4 loses three
    /// quarters of the signal, and loses it in a way that still trains.
    /// </remarks>
    public (StepResult Red, StepResult Blue) Step(EnvAction red, EnvAction blue)
    {
        var world = World;

        if (_termination != Termination.Running)
            throw new InvalidOperationException("the episode is over; call Reset");

        double redDealt = 0, redTaken = 0, blueDealt = 0, blueTaken = 0;
        byte redKills = 0, redDeaths = 0, blueKills = 0, blueDeaths = 0;

        // The aim a bot side started this decision at, kept so the action it
        // took can be recovered. A bot does not go through this decoder, so the
        // only way to say what it did in the action space is to measure the turn
        // it made — see StepResult.Action.
        var redAimBefore = Decoder.Aim(RedSlot);
        var blueAimBefore = Decoder.Aim(BlueSlot);

        // What the bots actually pressed on the first tick of the decision, which
        // is the tick the jump head is defined on. Reading the last tick's mask
        // instead would report jump 0 for every jump, because the bit is an edge.
        var redBotInput = PlayerInput.None;
        var blueBotInput = PlayerInput.None;

        for (var tick = 0; tick < _spec.ActionRepeat; tick++)
        {
            var first = tick == 0;

            world.State.Characters[RedSlot].KeyState = default;

            // A bot slot is driven by the bot itself, on its own cadence, and
            // not by re-encoding its choice through this decoder. The opponent is
            // then really that bot rather than one throttled to the action space
            // — which matters most for ScriptedBot, whose aim has no turn-rate cap
            // and would be a materially weaker shot if it were forced through one.
            if (_redBot is null)
            {
                // Decoded fresh every tick — the jump edge and the absolute mask
                // are both properties of doing it here rather than once per
                // decision.
                world.Drive(RedSlot, new FixedInput(Decoder.Decode(RedSlot, red, first), red));
            }
            else
            {
                world.Drive(RedSlot, _redBot);
            }

            if (_blueBot is null)
                world.Drive(BlueSlot, new FixedInput(Decoder.Decode(BlueSlot, blue, first), blue));
            else
                world.Drive(BlueSlot, _blueBot);

            world.Step();

            if (first)
            {
                redBotInput = InputOf(world, RedSlot);
                blueBotInput = InputOf(world, BlueSlot);
            }

            redDealt += world.Events.DamageDealt[RedSlot];
            redTaken += world.Events.DamageTaken[RedSlot];
            blueDealt += world.Events.DamageDealt[BlueSlot];
            blueTaken += world.Events.DamageTaken[BlueSlot];

            if (world.Events.Died[RedSlot])
            {
                redDeaths = 1;
                blueKills = 1;
            }

            if (world.Events.Died[BlueSlot])
            {
                blueDeaths = 1;
                redKills = 1;
            }

            if (redDeaths != 0 || blueDeaths != 0)
            {
                _termination = Termination.Death;
                break;
            }
        }

        _decisions++;

        if (_termination == Termination.Running && _decisions >= _spec.DecisionCap)
            _termination = Termination.TickCap;

        // After the action repeat and before the caller reads observations, so
        // the grade and the observation describe the same instant — "the search's
        // opinion of the state as the policy left it". EnvServer writes its
        // observations from the post-step world, so this ordering is what falls
        // out of the existing structure rather than something arranged.
        RunShadows();

        // The decoder follows a bot slot rather than driving it, and that is
        // what keeps Snapshot's aim meaningful and lets the next decision's turn
        // be measured against where the bot really is now.
        var (redAction, redExact) = ActionTaken(
            _redBot, red, redBotInput, RedSlot, redAimBefore, world.State.Characters[RedSlot].Zoomed);
        var (blueAction, blueExact) = ActionTaken(
            _blueBot, blue, blueBotInput, BlueSlot, blueAimBefore, world.State.Characters[BlueSlot].Zoomed);

        return (
            new StepResult
            {
                DamageDealt = redDealt,
                DamageTaken = redTaken,
                Kills = redKills,
                Deaths = redDeaths,
                Termination = _termination,
                Action = redAction,
                ActionExact = redExact,
                BestScore = ScoreOf(RedSlot),
            },
            new StepResult
            {
                DamageDealt = blueDealt,
                DamageTaken = blueTaken,
                Kills = blueKills,
                Deaths = blueDeaths,
                Termination = _termination,
                Action = blueAction,
                ActionExact = blueExact,
                BestScore = ScoreOf(BlueSlot),
            });
    }

    /// <summary>
    /// The search's winning score behind a slot, or NaN when nothing searched.
    /// </summary>
    /// <remarks>
    /// Only <c>RolloutSearch</c> has one. A scripted bot does not score
    /// candidates at all and a client slot is not a search, so reporting 0 for
    /// them would collide with the one value that carries meaning — see
    /// <see cref="StepResult.BestScore"/>.
    /// </remarks>
    private double ScoreOf(int slot) =>
        SearchFor(slot) is { } search ? search.LastBestScore : double.NaN;

    /// <summary>
    /// Asks each shadow search what it would have done in the state the drivers
    /// just produced.
    /// </summary>
    /// <remarks>
    /// <b>The return value is discarded, and that is the whole point.</b> A
    /// shadow grades; it does not drive. Its keys never reach a character and its
    /// own <c>ActionDecoder</c> is separate, so it cannot disturb the aim of the
    /// slot it is watching.
    /// <para>
    /// <b>Skipped for a dead slot.</b> <c>RolloutSearch.Poll</c> handles that,
    /// but asking it to grade a corpse would still cost a full search and record a
    /// score for a state no policy will ever act in.
    /// </para>
    /// </remarks>
    private void RunShadows()
    {
        var world = World;

        if (_redShadow is not null && world.State.IsAlive(RedSlot))
            _redShadow.Poll(new WorldView(in world.State, world.Collision), RedSlot, _decisions);

        if (_blueShadow is not null && world.State.IsAlive(BlueSlot))
            _blueShadow.Poll(new WorldView(in world.State, world.Collision), BlueSlot, _decisions);
    }

    /// <summary>The search behind a slot, whether it drives or only grades.</summary>
    private RolloutSearch? SearchFor(int slot) =>
        slot == RedSlot ? _redShadow ?? _redBot as RolloutSearch
        : slot == BlueSlot ? _blueShadow ?? _blueBot as RolloutSearch
        : null;

    /// <summary>Points a character where the decoder says it is looking.</summary>
    /// <remarks>
    /// Through <see cref="PlayerInput.FromAim"/> so the angle lands on the same
    /// 65,536 steps a client's would — an agent whose starting bearing was on a
    /// finer grid than the wire carries is aiming somewhere no human can, which is
    /// <c>docs/BOTAI.md</c> item 4.
    /// </remarks>
    private static void WriteAim(ScenarioWorld world, int slot, double degrees)
    {
        var quantised = PlayerInput.FromAim(PlayerKeys.None, degrees, ActionDecoder.AimDistancePixels);

        world.State.Characters[slot].NetAimDirection = quantised.NetAimDirection;
        world.State.Characters[slot].AimDistance = quantised.AimDistance;
    }

    /// <summary>What a slot's input source put on the character this tick.</summary>
    /// <remarks>
    /// Read off <c>CharacterState.KeyState</c> and <c>NetAimDirection</c>
    /// rather than by polling the bot a second time. Polling twice would advance
    /// a stateful bot — <c>RolloutSearch</c> counts down a decision and
    /// <c>ScriptedBot</c> runs four clocks — so the second answer would be to a
    /// question the simulation never asked.
    /// </remarks>
    private static PlayerInput InputOf(ScenarioWorld world, int slot)
    {
        ref readonly var character = ref world.State.Characters[slot];
        return new PlayerInput(character.KeyState, character.NetAimDirection, 0);
    }

    /// <summary>
    /// The action an agent took, and whether it is reproducible.
    /// </summary>
    /// <remarks>
    /// A client side echoes what it sent. A bot side is measured: the four
    /// discrete heads come straight off the key mask, which is exact because they
    /// are the same bits, and the aim head is the bin nearest the turn the bot
    /// made over this decision.
    /// <para>
    /// <b>The aim is the one that can saturate.</b> <c>ScriptedBot</c> points
    /// straight at its target with no turn-rate cap, so a target that moves
    /// quickly — or a fresh acquisition — snaps further in one decision than
    /// <c>ActionDecoder.MaxTurnDegreesPerDecision</c> allows. The bin then clamps
    /// and <c>exact</c> is false, which is the whole reason that flag is reported
    /// per step rather than assumed away.
    /// </para>
    /// <para>
    /// <b>The decoder is then set to where the bot actually is.</b> Leaving it
    /// where the clamp landed would make every following decision's measurement
    /// drift, and would make a Snapshot restore the wrong bearing.
    /// </para>
    /// </remarks>
    private (EnvAction Action, bool Exact) ActionTaken(
        IInputSource? bot, EnvAction sent, PlayerInput taken, int slot, double aimBefore, bool zoomed)
    {
        if (bot is null)
            return (sent, true);

        var keys = taken.Keys;

        byte horizontal = (keys & PlayerKeys.Left) != 0 ? (byte)1
            : (keys & PlayerKeys.Right) != 0 ? (byte)2
            : (byte)0;

        byte fire = (keys & PlayerKeys.Attack) != 0 ? (byte)1
            : (keys & PlayerKeys.Special) != 0 ? (byte)2
            : (byte)0;

        var aimAfter = taken.AimDirectionDegrees;

        // Signed shortest turn into (-180, 180].
        var turn = ((aimAfter - aimBefore + 540.0) % 360.0) - 180.0;

        var step = ActionDecoder.MaxTurnDegreesPerDecision / ActionDecoder.AimCentreBin;
        var wanted = turn / step;
        var bin = (int)Math.Round(wanted, MidpointRounding.AwayFromZero) + ActionDecoder.AimCentreBin;
        var clamped = Math.Clamp(bin, 0, ActionDecoder.AimBins - 1);

        Decoder.SetAim(slot, aimAfter);

        var action = new EnvAction
        {
            Horizontal = horizontal,
            Jump = (keys & PlayerKeys.Jump) != 0 ? (byte)1 : (byte)0,
            Down = (keys & PlayerKeys.Down) != 0 ? (byte)1 : (byte)0,
            Fire = fire,
            AimDelta = (byte)clamped,

            // Read off the character, not off whatever was asked for, and it is
            // exact by construction because the head is a level: whatever a bot
            // did with its scope, "is it scoped now" is the action that reproduces
            // it. This is the one head that cannot saturate the way the aim can.
            Zoom = zoomed ? (byte)1 : (byte)0,
        };

        // Exact means the bin reproduces the turn, not that the angle is
        // identical: a turn always lands between two bins. Half a bin is the most
        // a representable turn can be out by, so that is the test.
        var exact = bin == clamped && Math.Abs(turn - (ActionDecoder.TurnFor((byte)clamped))) <= (step / 2) + 1e-9;

        return (action, exact);
    }

    /// <summary>
    /// The episode's state as raw bytes.
    /// </summary>
    /// <remarks>
    /// <c>docs/BOTAI.md</c> item 2: <b>the bytes ARE the state</b>, because
    /// <see cref="WorldState"/> holds no reference to anything. Nothing is
    /// serialised field by field, so nothing can be forgotten when a field is
    /// added — which is the failure mode a hand-written serialiser has.
    /// <para>
    /// <b>The decoder's aim goes with it.</b> Restoring the world alone would
    /// resume the episode aiming wherever the decoder happened to be pointing,
    /// and that is a silent divergence rather than a crash.
    /// </para>
    /// </remarks>
    public byte[] Snapshot()
    {
        var world = World;

        // Refused rather than half-taken, and this is the honest edge of the
        // "the bytes are the state" claim. WorldState holds no reference to
        // anything, which is what makes the raw round-trip total — but a bot
        // driving a slot is state outside it: ScriptedBot carries a route, a
        // target, four clocks and its own RNG stream, and RolloutSearch carries a
        // decision countdown and a decoder. Restoring the world alone would
        // resume the episode with an opponent mid-way through a plan for a world
        // that no longer exists, and it would do it silently.
        //
        // Lifting this means serialising the bot layer, which is a real piece of
        // work and is not wanted yet — docs/ENV.md says so rather than leaving it
        // to be discovered.
        if (_spec.HasBotDriver)
        {
            throw new InvalidOperationException(
                "a snapshot cannot capture a bot-driven slot: the bot's route, clocks and RNG live "
                + "outside WorldState. Drive both slots from the client to snapshot this episode");
        }

        var state = world.State;

        var stateBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in state));
        var bytes = new byte[stateBytes.Length + (sizeof(double) * 2) + sizeof(int)];

        stateBytes.CopyTo(bytes);

        var tail = bytes.AsSpan(stateBytes.Length);
        BitConverter.TryWriteBytes(tail, Decoder.Aim(RedSlot));
        BitConverter.TryWriteBytes(tail[sizeof(double)..], Decoder.Aim(BlueSlot));
        BitConverter.TryWriteBytes(tail[(sizeof(double) * 2)..], _decisions);

        return bytes;
    }

    /// <summary>Restores a snapshot taken from an episode with the same spec.</summary>
    /// <remarks>
    /// <b>Restoring is not the claim; continuing is.</b> A state that compares
    /// equal and then ticks differently is the failure worth catching, so the
    /// gate steps a restored copy against a control that was never serialised
    /// and compares at the END.
    /// </remarks>
    public void Restore(ReadOnlySpan<byte> bytes)
    {
        var world = World;
        var size = MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in world.State)).Length;

        if (bytes.Length != size + (sizeof(double) * 2) + sizeof(int))
            throw new ArgumentException("snapshot is not the right size for this world", nameof(bytes));

        var restored = MemoryMarshal.Read<WorldState>(bytes);
        world.State = restored;

        var tail = bytes[size..];
        Decoder.SetAim(RedSlot, BitConverter.ToDouble(tail));
        Decoder.SetAim(BlueSlot, BitConverter.ToDouble(tail[sizeof(double)..]));
        _decisions = BitConverter.ToInt32(tail[(sizeof(double) * 2)..]);
        _termination = Termination.Running;
    }

    /// <summary>An <see cref="IInputSource"/> that answers with one fixed input.</summary>
    /// <remarks>
    /// The decode happens before the tick rather than inside the source, so that
    /// the aim advance and the jump edge are decided once and are visible to a
    /// snapshot taken between ticks.
    /// </remarks>
    /// <summary>
    /// One decoded action, held for the tick — keys and aim through
    /// <see cref="IInputSource"/>, the scope through <see cref="ICommandSource"/>.
    /// </summary>
    /// <remarks>
    /// <b>Both interfaces, because the action space spans both.</b>
    /// <see cref="EnvAction.Zoom"/> is not a key — <c>toggleZoom</c> is a client
    /// command — so a client-driven slot reaches it the same way a bot does, via
    /// the one dispatch a socket and the control port also use. Without the second
    /// interface the head would be accepted, echoed back in the reward block, and
    /// silently do nothing.
    /// </remarks>
    private sealed class FixedInput(PlayerInput input, EnvAction action) : IInputSource, ICommandSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => input;

        public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
        {
            if (!world.IsAlive(slot))
                return;

            // The edge detector is ActionDecoder's, not a second copy: the head
            // is a level and the command flips, and that comparison is the whole
            // reason the head is idempotent.
            if (ActionDecoder.WantsZoomToggle(action, in world.Character(slot)))
                commands.Push(PlayerCommand.ToggleZoom());
        }
    }

    /// <summary>Presses nothing, ever, and goes on looking where it was.</summary>
    /// <remarks>
    /// <b><see cref="PlayerInput.None"/> is not "no aim" — it is "aim at
    /// zero degrees", due east.</b> An idle driver built on it snaps the
    /// character's facing on its first tick, and <b>facing is observable</b>: it
    /// is what the two-pixel box inflation splash is measured against, and
    /// <see cref="Reset"/> already goes out of its way to make the starting
    /// facing symmetric for exactly that reason. An "opponent that does nothing"
    /// that silently turns to face east is not the comparison floor anybody
    /// means.
    /// <para>
    /// Caught by <c>DuelEnvTests.AnIdleDriverPressesNothingAndStillEarnsItsRewards</c>,
    /// which saw the recovered action come back as a full left turn — the aim
    /// head faithfully reporting a 180° snap nobody asked for.
    /// </para>
    /// <para>
    /// <b>Stateless, so one instance is shared across every world in a batch
    /// and that is safe</b> — it reads the aim back out of the character rather
    /// than remembering it. Every other driver is per-world precisely because it
    /// is not: <c>ScriptedBot</c> carries a route and an RNG, and sharing one
    /// across a parallel batch would be a race that shows up as a
    /// batch-size-dependent trajectory rather than as a crash.
    /// </para>
    /// </remarks>
    private sealed class IdleInput : IInputSource
    {
        public static readonly IdleInput Instance = new();

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            ref readonly var character = ref world.Character(slot);
            return new PlayerInput(PlayerKeys.None, character.NetAimDirection, character.AimDistance);
        }
    }
}
