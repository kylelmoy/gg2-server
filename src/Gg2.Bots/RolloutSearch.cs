using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// What the search assumes the other characters do while it is looking ahead.
/// </summary>
/// <remarks>
/// ⚠️ <b>This is <c>docs/BOTAI.md</c> item 12's "one open design question", made
/// explicit on purpose.</b> The item's instruction is to decide it and write it
/// down, because *"leaving it implicit is how a search ends up with a hidden
/// opponent model nobody chose"* — and this project already has the cautionary
/// example: <c>botAimLead.gml</c> assumes constant velocity plus gravity, which
/// is the assumption the whole learning programme exists because it is wrong.
/// </remarks>
public enum RolloutOpponent
{
    /// <summary>
    /// <b>The default.</b> Every other character holds the input it is
    /// <em>already</em> holding, for the whole horizon.
    /// </summary>
    /// <remarks>
    /// Their <c>keyState</c> and <c>aimDirection</c> are in
    /// <see cref="CharacterState"/>, put there by the tick that just ran, so
    /// this needs no history, no allocation and no model — it is the wire's own
    /// last word about them replayed forward.
    /// <para>
    /// It is chosen over <see cref="Idle"/> because it is right about the thing
    /// that matters over a 20-tick horizon: somebody running left keeps running
    /// left, and somebody holding fire keeps firing. It is wrong about
    /// <em>reactions</em>, and it is wrong in the safe direction — it assumes
    /// the opponent goes on doing what they were doing rather than that they
    /// stop.
    /// </para>
    /// <para>
    /// ⚠️ <b>What it is NOT is self-play</b>, which is what item 12 points at as
    /// the answer that needs no data. Rolling the opponent out with the same
    /// search costs a nested search per candidate per tick and is deliberately
    /// out of v0. The seam for it is this enum: a third member and a branch in
    /// <see cref="RolloutSearch.OpponentInput"/>.
    /// </para>
    /// <para>
    /// ⚠️ <b>It is FIRST, so that it is zero, and that is a bug fix rather than
    /// a tidy-up.</b> <see cref="Idle"/> used to be declared first, so
    /// <c>default(RolloutOptions).Opponent</c> was <see cref="Idle"/> — and
    /// <c>new RolloutSearch()</c> takes <c>default</c>. <b>Every caller who took
    /// the documented default got the opposite of it</b>, including
    /// <c>Gg2.Env</c>'s duels and this repo's own "11-0-1 against tier 5"
    /// figure. It is exactly the trap <see cref="RolloutOptions.WithDefaults"/>
    /// was written for and it caught two of the three fields; an enum cannot be
    /// fixed there at all, because "unset" and "explicitly Idle" are the same
    /// zero. Ordering is the only fix, so the order is load-bearing: <b>the
    /// default member of an enum that rides in a <c>default</c>-able struct must
    /// be the zero one.</b>
    /// </para>
    /// </remarks>
    HoldLastInput,

    /// <summary>
    /// ⚠️ <b>The opponent presses nothing.</b> Cheapest, and <b>optimistic</b>:
    /// a search that believes nobody will shoot back walks into fire.
    /// </summary>
    /// <remarks>
    /// Kept because it is the honest floor for a comparison — if a search is not
    /// better with a moving opponent than with a statue, the opponent model is
    /// not what is wrong. Not the default.
    /// </remarks>
    Idle,
}

/// <summary>
/// How a <see cref="RolloutSearch"/> is configured.
/// </summary>
/// <param name="HorizonTicks">
/// How far each candidate is played forward. <c>docs/BOTAI.md</c> item 12 says
/// 15-30; 20 is the default.
/// </param>
/// <param name="DecisionTicks">
/// How many ticks one decision covers before the search runs again — the action
/// repeat, and the same one <c>Gg2.Env</c>'s <c>DuelEnv</c> uses.
/// ⚠️ <b>Receding horizon</b>: only the first <paramref name="DecisionTicks"/>
/// of the winning candidate are ever played, and the rest is thrown away.
/// </param>
/// <param name="AimBins">
/// Which of <see cref="ActionDecoder.AimBins"/> the search considers. The full
/// fifteen would multiply the candidate count by three for turns a 20-tick
/// horizon cannot tell apart.
/// </param>
/// <param name="ConsiderSpecial">
/// Whether the right-click head is searched. Off by default: for most classes it
/// is a reload or nothing, and it triples nothing useful.
/// </param>
public readonly record struct RolloutOptions(
    int HorizonTicks = 20,
    int DecisionTicks = 4,
    RolloutOpponent Opponent = RolloutOpponent.HoldLastInput,
    byte[]? AimBins = null,
    bool ConsiderSpecial = false)
{
    /// <summary><c>docs/BOTAI.md</c> item 12 says 15-30 ticks; this is the middle.</summary>
    public const int DefaultHorizonTicks = 20;

    /// <summary>The same action repeat <c>Gg2.Env</c>'s duels use.</summary>
    public const int DefaultDecisionTicks = 4;

    /// <summary>
    /// The default aim ladder: hard left, half left, straight on, half right,
    /// hard right.
    /// </summary>
    /// <remarks>
    /// Bin 7 is no turn and the cap is ±30 degrees per decision
    /// (<see cref="ActionDecoder.MaxTurnDegreesPerDecision"/>), so these are
    /// -30, -17, 0, +17 and +30.
    /// </remarks>
    public static byte[] DefaultAimBins => [0, 3, 7, 11, 14];

    public byte[] Aims => AimBins ?? DefaultAimBins;

    /// <summary>
    /// This record with its zeroes filled in.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>new RolloutOptions()</c> is NOT the primary constructor.</b> A
    /// record struct's parameterless form is <c>default</c> — every field zero,
    /// none of the declared defaults applied — so a caller who writes
    /// <c>default</c> or <c>new()</c> gets a zero horizon and a zero decision
    /// length, which are the two values <see cref="RolloutSearch"/> refuses. The
    /// declared defaults still work through the primary constructor
    /// (<c>new RolloutOptions(ConsiderSpecial: true)</c>), so the two forms
    /// disagree, and this is what makes them agree.
    /// </remarks>
    public RolloutOptions WithDefaults() => this with
    {
        HorizonTicks = HorizonTicks == 0 ? DefaultHorizonTicks : HorizonTicks,
        DecisionTicks = DecisionTicks == 0 ? DefaultDecisionTicks : DecisionTicks,
    };
}

/// <summary>
/// <c>docs/BOTAI.md</c> item 12: a bot that decides by <b>playing the game
/// forward</b> rather than by following a script.
/// </summary>
/// <remarks>
/// On each decision it forks the world, plays every candidate action to the
/// horizon, scores each from the damage the simulation <em>emits</em>, restores,
/// and plays the first <see cref="RolloutOptions.DecisionTicks"/> ticks of the
/// best one — then replans rather than committing to the rest of the plan.
/// <para>
/// <b>Why it is a bot and not a trainer.</b> It is a better bot than this repo
/// ships, it is the permanent evaluation baseline every later policy is measured
/// against, and it is useful with <c>gg2-rl</c> deleted. Its action space is
/// <see cref="EnvAction"/> — the same heads a policy emits — so a decision it
/// logs is valid training data and a policy's action is a valid candidate here.
/// </para>
/// <para>
/// <b>It is also a parity fuzzer.</b> A search systematically tries what no
/// human would, so it finds physics-port bugs by exploiting them. ⚠️ <b>An
/// exploit is a finding for <c>docs/PARITY.md</c>, not something to penalise in
/// the score</b> — <c>gg2-rl/docs/PLAN.md</c> phase 1 says so explicitly, and
/// scoring around a bug hides it.
/// </para>
/// <para>
/// ⚠️ <b>The fork is a <see cref="WorldState"/> copy, and it is ~150 KB.</b>
/// That is the whole of item 2's snapshot/restore: the state holds no reference
/// to anything, so assignment is a snapshot and assignment back is a restore.
/// The two buffers are fields, allocated once, because <c>docs/BOTAI.md</c> item
/// 1 forbids allocating in the tick loop — and a 150 KB <c>stackalloc</c> would
/// be worse than an allocation.
/// </para>
/// <para>
/// ⚠️ <b>The rollout draws from the FORK's RNG, never the match's.</b> That is
/// automatic and it is the reason it is safe: <see cref="WorldState.Random"/>
/// travels inside the copy, so a candidate's shots advance a stream that is
/// thrown away. Nothing this class does moves the real one. Its own candidate
/// ordering is deterministic and draws from nothing at all — see
/// <see cref="ScriptedBot.Random"/> for why that rule exists.
/// </para>
/// <para>
/// ⚠️ <b>A horizon shorter than a refire timer makes the search blind.</b> Every
/// candidate scores exactly zero when nothing can fire inside the horizon, the
/// tie goes to the first candidate, and the bot stands still looking broken. A
/// freshly spawned soldier's <c>refireTimer</c> starts at <b>29</b> and a scout
/// is nineteen ticks from its first shot, so the first second of every life is
/// like that — measured, and it cost two tests here before it was understood.
/// It is a real weakness of scoring on damage alone, and the honest fix is a
/// shaping term (distance, facing, ammo), not a longer horizon: 20 ticks is
/// already 1,200 simulated ticks a decision.
/// </para>
/// <para>
/// <b>Measured 2026-08-28, Release</b>, one searcher on <c>gg_debug</c> with
/// the default options: <b>2.94 ms per decision</b> — 60 candidates x 20 ticks
/// = 1,200 simulated ticks — which at a decision every 4 ticks is about 0.74 ms
/// of a 33.3 ms tick for a single bot. ⚠️ <b>Name the build when re-measuring</b>;
/// Debug is roughly twice that, and <c>CLAUDE.md</c> records a stale
/// unattributed figure sitting in four documents.
/// <para>
/// Against <c>ScriptedBot</c> at skill tier 5 over twelve duels on the same map:
/// <b>11 wins, 0 losses, 1 draw</b>. That is a smoke test rather than the exit
/// criterion — <c>gg2-rl/docs/PLAN.md</c> phase 1 asks for 500 duels per class
/// matchup at p &lt; 0.01, and that harness lives there.
/// </para>
/// </para>
/// <para>
/// ⚠️ <b>Expect it to be too slow to ship.</b> That is not a defect;
/// <c>gg2-rl</c> phase 2 is the distillation step that exists because of it, and
/// phase 1's exit criterion asks for the per-decision cost to be *recorded*
/// rather than to be small. <see cref="LastDecisionTicksSimulated"/> is what to
/// record it with.
/// </para>
/// </remarks>
public sealed class RolloutSearch : IInputSource, ICommandSource
{
    private readonly RolloutOptions _options;

    private readonly ActionDecoder _decoder = new();

    private EnvAction[] _candidates;

    // Whether _candidates currently carries the zoom dimension. See CandidatesFor.
    private bool _candidatesAreSniper;

    // ⚠️ Fields, not locals: 150 KB each, allocated once. See the remarks.
    private WorldState _baseline;

    private WorldState _fork;

    private TickEvents _events;

    private int _ticksLeftInDecision;

    private EnvAction _current;

    private int _currentSlot = -1;

    public RolloutSearch(RolloutOptions options = default)
    {
        _options = options.WithDefaults();

        if (_options.HorizonTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _options.HorizonTicks, "horizon must be at least one tick");

        if (_options.DecisionTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _options.DecisionTicks, "a decision must cover at least one tick");

        _candidates = BuildCandidates(_options, considerZoom: false);
        _candidatesAreSniper = false;
        _current = EnvAction.Idle;
    }

    /// <summary>How many candidate actions each decision evaluates.</summary>
    public int CandidateCount => _candidates.Length;

    /// <summary>
    /// Simulation ticks the last decision cost — <c>candidates x horizon</c>.
    /// </summary>
    /// <remarks>
    /// Zero on a tick that reused the previous decision. This is the number
    /// <c>gg2-rl</c> phase 1's exit criterion asks to be recorded, and it is
    /// reported in ticks rather than in milliseconds so that it does not go
    /// stale when the machine changes.
    /// </remarks>
    public long LastDecisionTicksSimulated { get; private set; }

    /// <summary>The score the winning candidate earned, for diagnostics.</summary>
    public double LastBestScore { get; private set; }

    /// <summary>
    /// The action the current decision settled on — the thing a behaviour-cloning
    /// dataset would record beside the observation.
    /// </summary>
    public EnvAction LastAction => _current;

    /// <summary>
    /// The candidate set: every combination the search considers, in a fixed
    /// order.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Deterministic and drawn from nothing.</b> A search that broke ties
    /// with a random draw would not be reproducible from a seed, and
    /// reproducibility is what makes it usable as an evaluation baseline. Ties
    /// go to the earlier candidate, which is the one nearer "stand still and do
    /// nothing" because of the order these loops run in.
    /// </remarks>
    private static EnvAction[] BuildCandidates(RolloutOptions options, bool considerZoom)
    {
        var aims = options.Aims;
        var fires = options.ConsiderSpecial ? new byte[] { 0, 1, 2 } : [0, 1];
        byte[] zooms = considerZoom ? [0, 1] : [0];
        var list = new List<EnvAction>(aims.Length * fires.Length * zooms.Length * 6);

        foreach (var aim in aims)
        {
            foreach (var fire in fires)
            {
                foreach (var zoom in zooms)
                {
                    for (byte horizontal = 0; horizontal < 3; horizontal++)
                    {
                        for (byte jump = 0; jump < 2; jump++)
                        {
                            list.Add(new EnvAction
                            {
                                Horizontal = horizontal,
                                Jump = jump,
                                Down = 0,
                                Fire = fire,
                                AimDelta = aim,
                                Zoom = zoom,
                            });
                        }
                    }
                }
            }
        }

        return [.. list];
    }

    /// <summary>
    /// The candidate set for the class this search is currently driving.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Rebuilt when the class changes, and ONLY the sniper pays for the
    /// zoom dimension.</b> Doubling 60 candidates to 120 doubles the per-decision
    /// cost — measured at 2.94 ms for 60 (Release, gg_debug) — and
    /// <c>PlayerCommands.ToggleZoom</c> refuses every class but the sniper, so
    /// every other class would be paying twice to evaluate the same sixty actions
    /// under two labels.
    /// <para>
    /// ⚠️ <b>Without this the zoom head would be a trap rather than a feature.</b>
    /// <see cref="ScriptedBot"/> gained the scope on the same day; a search whose
    /// candidate set was still a five-head enumeration would have been measured
    /// against a scripted sniper playing a 75-damage weapon while itself capped at
    /// 35, and the honest reading of that number would have been "the search got
    /// worse". Raised by <c>gg2-rl</c> before the measurement was taken, which is
    /// the only reason it is here rather than in a post-mortem.
    /// </para>
    /// </remarks>
    private EnvAction[] CandidatesFor(PlayerClass playerClass)
    {
        var wantsZoom = playerClass == PlayerClass.Sniper;

        if (_candidatesAreSniper != wantsZoom)
        {
            _candidates = BuildCandidates(_options, wantsZoom);
            _candidatesAreSniper = wantsZoom;
        }

        return _candidates;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ <b>The scope, and it is the only thing this search does that is not a
    /// key.</b> The chosen candidate's <see cref="EnvAction.Zoom"/> is a level, so
    /// the command is issued only when it differs from the character — see
    /// <see cref="ActionDecoder.WantsZoomToggle"/>, which is the one edge detector
    /// and is shared with the client path.
    /// </remarks>
    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
    {
        if (!world.IsAlive(slot))
            return;

        if (ActionDecoder.WantsZoomToggle(_current, in world.Character(slot)))
            commands.Push(PlayerCommand.ToggleZoom());
    }

    /// <inheritdoc />
    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        LastDecisionTicksSimulated = 0;

        // ⚠️ The decoder carries this slot's absolute aim across ticks, and a
        // fresh character has to start from where the wire says it is pointing
        // or the first decision turns from stale bearing.
        if (slot != _currentSlot)
        {
            _currentSlot = slot;
            _ticksLeftInDecision = 0;
            _decoder.SetAim(slot, world.Character(slot).NetAimDirection * 360.0 / 65536.0);
        }

        var firstTickOfDecision = _ticksLeftInDecision <= 0;

        if (firstTickOfDecision)
        {
            _current = Search(in world, slot);
            _ticksLeftInDecision = _options.DecisionTicks;
        }

        _ticksLeftInDecision--;

        return _decoder.Decode(slot, _current, firstTickOfDecision);
    }

    /// <summary>
    /// Plays every candidate to the horizon and returns the best.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The decoder's aim is saved and restored around every candidate.</b>
    /// <c>ActionDecoder.Decode</c> advances the slot's absolute bearing, so a
    /// rollout that turned would leave the real decision starting from a bearing
    /// that only existed inside a simulation nobody played. That is exactly the
    /// class of bug <c>ActionDecoder.Aim</c>'s own remark warns about for
    /// snapshots, and it is silent — the bot simply aims somewhere nobody chose.
    /// </remarks>
    private EnvAction Search(in WorldView world, int slot)
    {
        _baseline = world.State;

        // ⚠️ Per CLASS, so only a sniper pays for the zoom dimension — and asked
        // here rather than in the constructor because a bot can be re-classed
        // mid-life, exactly as ScriptedBot rebuilds its NavQuery every tick.
        var candidates = CandidatesFor(world.Character(slot).Class);

        var savedAim = _decoder.Aim(slot);
        var best = EnvAction.Idle;
        var bestScore = double.NegativeInfinity;

        for (var i = 0; i < candidates.Length; i++)
        {
            _decoder.SetAim(slot, savedAim);

            var score = Rollout(world.Collision, slot, candidates[i]);

            if (score > bestScore)
            {
                bestScore = score;
                best = candidates[i];
            }
        }

        _decoder.SetAim(slot, savedAim);

        LastDecisionTicksSimulated = (long)candidates.Length * _options.HorizonTicks;
        LastBestScore = bestScore;

        return best;
    }

    /// <summary>
    /// One candidate, played forward and scored.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The score is the damage the simulation EMITS, not a state diff.</b>
    /// <c>docs/BOTAI.md</c> item 7 exists for this: diffing two snapshots is
    /// slow, ambiguous about attribution, and would make the bot re-derive game
    /// rules it must not know. <see cref="TickEvents.DamageDealt"/> is credited
    /// at the moment damage is applied and is exactly the reward a policy would
    /// be trained on, which is what makes this bot's decisions valid data.
    /// <para>
    /// Damage taken is subtracted at equal weight. That is a choice, not a
    /// transcription: it makes the search trade a hit for a hit rather than
    /// chase damage at any cost, and it is the simplest thing that is not
    /// obviously wrong. ⚠️ <b>It is also the first knob to question</b> if the
    /// bot plays suicidally — an asymmetric weight is a one-line change here and
    /// a real behavioural difference.
    /// </para>
    /// <para>
    /// ⚠️ <b>A death ends the rollout early.</b> Without that the search happily
    /// dies on tick 3 to land a hit on tick 12, because a corpse deals no damage
    /// and takes none — so the remaining ticks look free.
    /// </para>
    /// </remarks>
    private double Rollout(CollisionWorld collision, int slot, EnvAction action)
    {
        _fork = _baseline;

        // ⚠️ The candidate's scope is applied to the FORK before it is played, or
        // the search scores a zoom it never raised — every zoom candidate would
        // simulate identically to its unzoomed twin, the tie would go to the
        // earlier one, and the head would look measured while being inert.
        // PlayerCommands.ToggleZoom is the same method the server calls; it
        // refuses a non-sniper on its own.
        if (ActionDecoder.WantsZoomToggle(action, in _fork.Characters[slot]))
            PlayerCommands.ToggleZoom(ref _fork, slot);

        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];
        var score = 0.0;

        for (var tick = 0; tick < _options.HorizonTicks; tick++)
        {
            if (!_fork.IsAlive(slot))
                break;

            for (var other = 0; other < WorldState.MaxSlots; other++)
            {
                inputs[other] = other == slot
                    ? _decoder.Decode(slot, action, firstTickOfDecision: tick == 0)
                    : OpponentInput(in _fork, other);
            }

            _events = default;
            MatchSimulation.Tick(collision, ref _fork, inputs, ref _events);

            score += _events.DamageDealt[slot] - _events.DamageTaken[slot];
        }

        return score;
    }

    /// <summary>
    /// What a character other than the searcher is assumed to be doing.
    /// </summary>
    /// <remarks>
    /// See <see cref="RolloutOpponent"/> for why this is a named decision rather
    /// than an accident. ⚠️ <b>A dead or absent slot gets
    /// <see cref="PlayerInput.None"/></b>, which matters because the input span
    /// covers every slot and a stale key mask for somebody who is not there
    /// would be read by nothing but is one more thing to reason about.
    /// </remarks>
    private PlayerInput OpponentInput(in WorldState state, int slot)
    {
        if (_options.Opponent == RolloutOpponent.Idle || !state.IsAlive(slot))
            return PlayerInput.None;

        ref readonly var character = ref state.Characters[slot];

        return new PlayerInput(character.KeyState, character.NetAimDirection, character.AimDistance);
    }
}
