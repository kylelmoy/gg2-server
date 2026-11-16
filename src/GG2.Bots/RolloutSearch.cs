using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// How a <see cref="RolloutSearch"/> is configured.
/// </summary>
/// <param name="HorizonTicks">
/// How far each candidate is played forward. <c>docs/BOTAI.md</c> item 12 says
/// 15-30; 20 is the default.
/// <para>
/// Win rate is non-monotone in horizon: it falls off steadily above 20 (sniper
/// 68.4% -> 22.8%, soldier 93.8% -> 63.7% from horizon 20 to 90), but below 20
/// it peaks rather than continuing to rise — the peak sits somewhere between 12
/// and 20, with 15 and 20 statistically indistinguishable at large sample sizes
/// (864 duels a cell: 91.6% [88.6, 93.8] vs 90.5% [87.6, 92.8]). The default
/// stays at 20 because it sits inside that peak region and nothing currently
/// depends on the exact value.
/// </para>
/// <para>
/// A blind-fraction column measured alongside a win-rate sweep is not
/// comparable across arms that play differently: they visit different states,
/// so their blind fractions come from different instruments and must not be
/// merged with a fixed-step blind measurement.
/// </para>
/// </param>
/// <param name="DecisionTicks">
/// How many ticks one decision covers before the search runs again — the action
/// repeat, and the same one <c>GG2.Env</c>'s <c>DuelEnv</c> uses.
/// <b>Receding horizon</b>: only the first <paramref name="DecisionTicks"/>
/// of the winning candidate are ever played, and the rest is thrown away.
/// </param>
/// <param name="AimBins">
/// Which of <see cref="ActionDecoder.AimBins"/> the search considers. The full
/// fifteen would multiply the candidate count by three for turns a 20-tick
/// horizon cannot tell apart.
/// </param>
/// <param name="CommitTicks">
/// How many ticks a candidate's action is actually held before the rollout
/// continues under a neutral continuation. <b>0 means the whole horizon</b>,
/// which is what this search has always done.
/// <para>
/// <b>Lookahead and commitment are the same number until this is set.</b> At a
/// long horizon the search ends up choosing among plans of the shape "hold this
/// key and this aim for three seconds", so blind fraction and win rate move
/// together even though seeing further and committing longer are conceptually
/// separable.
/// </para>
/// <para>
/// Shortening the commitment under a neutral continuation (keys released,
/// bearing kept) does separate the two, but does not help: it reduces the blind
/// fraction while costing win rate, and the loss is not recovered.
/// </para>
/// <para>
/// <b>Blindness and search strength are largely independent quantities here.</b>
/// A blind fraction is not a reliable proxy for search quality — the horizon
/// sweep's monotone opposition between the two is real but does not license a
/// causal reading.
/// </para>
/// <para>
/// The knob stays and is load-bearing rather than speculative: the measurements
/// behind these conclusions depend on the wire-to-rollout chain actually being
/// intact, which <c>EnvProtocolTests.APolicyLoadedOverTheSocketReachesTheRollout</c>
/// gates — a severed chain would produce the same-looking win column and settle
/// nothing.
/// </para>
/// </param>
/// <param name="Threads">
/// How many threads the candidate loop runs across. <b>1 — serial — is the
/// default and the shipped behaviour</b>; 0 means the same thing.
/// </param>
/// <remarks>
/// <b>A decision is identical at every thread count</b>, gated by
/// <c>RolloutThreadingTests</c> rather than argued: candidates are scored into
/// per-index slots and the winner is picked afterwards in ascending order, so the
/// lexicographic tie-break sees the order it saw serially. It is a cost knob and
/// nothing else.
/// </remarks>
/// <param name="ConsiderSpecial">

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
/// human would, so it finds physics-port bugs by exploiting them. <b>An
/// exploit is a finding for <c>docs/PARITY.md</c>, not something to penalise in
/// the score</b> — <c>gg2-rl/docs/PLAN.md</c> phase 1 says so explicitly, and
/// scoring around a bug hides it.
/// </para>
/// <para>
/// <b>The fork is a <see cref="WorldState"/> copy, and it is ~150 KB.</b>
/// That is the whole of item 2's snapshot/restore: the state holds no reference
/// to anything, so assignment is a snapshot and assignment back is a restore.
/// The two buffers are fields, allocated once, because <c>docs/BOTAI.md</c> item
/// 1 forbids allocating in the tick loop — and a 150 KB <c>stackalloc</c> would
/// be worse than an allocation.
/// </para>
/// <para>
/// <b>The rollout draws from the fork's RNG, never the match's.</b> That is
/// automatic and it is the reason it is safe: <see cref="WorldState.Random"/>
/// travels inside the copy, so a candidate's shots advance a stream that is
/// thrown away. Nothing this class does moves the real one. Its own candidate
/// ordering is deterministic and draws from nothing at all — see
/// <see cref="ScriptedBot.Random"/> for why that rule exists.
/// </para>
/// <para>
/// <b>A horizon shorter than a refire timer makes the search blind.</b> Every
/// candidate scores exactly zero when nothing can fire inside the horizon, the
/// tie goes to the first candidate, and the bot stands still looking broken. A
/// freshly spawned soldier's <c>refireTimer</c> starts at 29 and a scout is
/// nineteen ticks from its first shot, so the first second of every life is
/// like that. It is a real weakness of scoring on damage alone, and the honest
/// fix is a shaping term (distance, facing, ammo), not a longer horizon: 20
/// ticks is already 1,200 simulated ticks a decision.
/// </para>
/// <para>
/// One searcher on <c>gg_debug</c> with the default options, Release build:
/// 2.94 ms per decision — 60 candidates x 20 ticks = 1,200 simulated ticks —
/// which at a decision every 4 ticks is about 0.74 ms of a 33.3 ms tick for a
/// single bot. Name the build when re-measuring; Debug is roughly twice that.
/// <para>
/// Against <c>ScriptedBot</c> at skill tier 5 over twelve duels on the same map:
/// 11 wins, 0 losses, 1 draw. That is a smoke test rather than the exit
/// criterion — <c>gg2-rl/docs/PLAN.md</c> phase 1 asks for 500 duels per class
/// matchup at p &lt; 0.01, and that harness lives there.
/// </para>
/// </para>
/// <para>
/// <b>Expect it to be too slow to ship.</b> That is not a defect;
/// <c>gg2-rl</c> phase 2 is the distillation step that exists because of it, and
/// phase 1's exit criterion asks for the per-decision cost to be recorded
/// rather than to be small. <see cref="LastDecisionTicksSimulated"/> is what to
/// record it with.
/// </para>
/// </remarks>
public sealed class RolloutSearch : IInputSource, ICommandSource
{
    private readonly RolloutOptions _options;

    private readonly ActionDecoder _decoder = new();

    private EnvAction[] _candidates;

    // Per-candidate scores for the decision just taken. Grown to fit, never
    // per-decision — see LastScores.
    private double[] _scores = [];

    private int _scoreCount;

    // The candidate index each entry of _scores belongs to. Identity for an
    // unrestricted search; the restricted set, ascending, otherwise.
    private int[] _searched = [];

    // The candidates the scores are aligned with, so LastCandidates stays a
    // parallel array when the searched set is not a prefix.
    private EnvAction[] _searchedCandidates = [];

    // The restriction for the next decision: one bit per candidate index, or
    // null for "search everything". See RestrictNextDecision.
    private bool[]? _restriction;

    // The candidate-set SIZE _restriction was built against. A restriction is
    // only honoured when this still matches, because index i names a different
    // action in the 60-list and the 120-list — see Search.
    private int _restrictionForCount;

    // Whether _candidates currently carries the zoom dimension. See CandidatesFor.
    private bool _candidatesAreSniper;

    // Fields, not locals: 150 KB each, allocated once. See the remarks.
    private WorldState _baseline;

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
    /// <summary>
    /// The candidate set currently BUILT — which is the class this search last
    /// drove, not the class it is about to.
    /// </summary>
    /// <remarks>
    /// <b>Not the number to validate a restriction against.</b> The constructor
    /// builds without the zoom dimension, so this reads 60 for a sniper until its
    /// first decision has searched. Use
    /// <see cref="CandidateCountFor(RolloutOptions, PlayerClass)"/>, which is a
    /// function of the class rather than of history.
    /// </remarks>
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
    /// Every candidate's score from the decision just taken, aligned with
    /// <see cref="LastCandidates"/>.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole reason a search is a better expert than a reward
    /// signal.</b> <c>gg2-rl/docs/DESIGN.md</c> — "its output is an action
    /// distribution rather than a scalar, so every logged decision carries far
    /// more signal than a reward sample does" — and its collection step logs
    /// <c>(observation, distribution over actions, outcome)</c>. <see cref="Search"/>
    /// scores all 60 (or 120) candidates and every score is kept, not just the
    /// winner's.
    /// <para>
    /// <b>Why it matters concretely:</b> a one-hot on the argmax throws away the
    /// search's own uncertainty. That two candidates scored 70 and 69 is exactly
    /// the information saying "either is fine", and cloning the argmax teaches a
    /// policy to be confident there instead.
    /// </para>
    /// <para>
    /// <b>Written into a pre-allocated buffer</b>, resized only when the
    /// candidate set changes — docs/BOTAI.md item 1 forbids allocating in the tick
    /// loop, and this is written once per candidate per decision.
    /// </para>
    /// <para>
    /// <b>Valid only until the next decision</b>, and a span so it cannot be
    /// stashed. A caller logging a dataset copies it.
    /// </para>
    /// </remarks>
    public ReadOnlySpan<double> LastScores => _scores.AsSpan(0, _scoreCount);

    /// <summary>
    /// The candidate set the scores are aligned with.
    /// </summary>
    /// <remarks>
    /// <b>Handed back with the scores rather than left to be re-derived.</b>
    /// The set is per class — a sniper's carries the zoom dimension and is twice
    /// the size — so a caller that rebuilt it from <see cref="RolloutOptions"/>
    /// would silently mis-align every sniper's distribution against a
    /// soldier-shaped index.
    /// </remarks>
    public ReadOnlySpan<EnvAction> LastCandidates => _searchedCandidates.AsSpan(0, _scoreCount);

    /// <summary>
    /// Which candidate index each entry of <see cref="LastScores"/> belongs to.
    /// </summary>
    /// <remarks>
    /// <b>Identity for an unrestricted search</b> — 0, 1, 2 … — so a caller
    /// that ignores it is correct exactly as long as nobody prunes. Under
    /// <see cref="RestrictNextDecision"/> it is the searched set, and the scores
    /// are a distribution over <em>that</em>, not over the whole candidate list.
    /// <para>
    /// <b>A distribution without these indices is "a ranking over an unnamed
    /// list"</b> — the same argument <see cref="LastCandidates"/> makes, one level
    /// down. It is what makes a pruned search's output usable as a training
    /// target: `gg2-rl` masks the unevaluated candidates out of its
    /// cross-entropy rather than giving them a zero target, because zero
    /// probability means "this action is bad" and the truth is "nobody looked".
    /// </para>
    /// </remarks>
    public ReadOnlySpan<int> LastSearchedIndices => _searched.AsSpan(0, _scoreCount);

    /// <summary>
    /// Restricts the next decision to these candidate indices.
    /// </summary>
    /// <remarks>
    /// <b><c>gg2-rl</c>'s <c>docs/DESIGN.md</c> step 3</b>: a distilled policy
    /// orders and prunes the search so it is stronger at equal cost. Measured
    /// there — the policy's top 8 of 60 contains this search's best action
    /// <b>78.2%</b> of the time against <b>55.9%</b> for a random 8.
    /// <para>
    /// <b>An empty span means "search everything"</b>, which is the default and
    /// the behaviour every existing caller gets.
    /// </para>
    /// <para>
    /// <b>It is a set, not a ranking, and this must never come to depend on the
    /// order it arrives in.</b> The candidates are visited in ascending index
    /// order whatever order they are given, so the decision is a pure function of
    /// the set — which matters because the tie-break is lexicographic and 85.2% of
    /// non-blind decisions have the top two candidates tied, so iteration order
    /// decides a great many of them. Duplicates collapse.
    /// </para>
    /// <para>
    /// <b>And the set is deliberately not the policy's top k.</b> `gg2-rl` sends
    /// top-(k−m) plus m sampled from the remainder, because a search that only
    /// ever evaluates what the policy already likes, feeding a policy trained on
    /// what the search evaluated, is self-confirming: it cannot be corrected where
    /// it is wrong, because where it is wrong is where nothing gets looked at.
    /// Nothing here may assume index 0 is anybody's favourite.
    /// </para>
    /// <para>
    /// <b>Consumed by the next search, not the next Poll.</b> A decision is
    /// held for <see cref="RolloutOptions.DecisionTicks"/> ticks and only its
    /// first tick searches, so a restriction set mid-decision applies to the
    /// decision after it rather than being silently dropped.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An index is negative or past <see cref="CandidateCount"/>. Refused,
    /// never clamped — an index that silently meant a different action would
    /// be a wrong search reported with plausible numbers.
    /// </exception>
    public void RestrictNextDecision(ReadOnlySpan<int> indices, PlayerClass playerClass)
    {
        if (indices.Length == 0)
        {
            _restriction = null;
            return;
        }

        // The count for the class this slot will be searched as, never the
        // instance's CandidateCount — that reads 60 for a sniper until its first
        // decision, which refused a valid index 76 on step 0. See
        // CandidateCountFor.
        var count = CandidateCountFor(_options, playerClass);
        var mask = _restriction;

        if (mask is null || mask.Length < count)
            mask = new bool[count];
        else
            Array.Clear(mask);

        foreach (var index in indices)
        {
            if ((uint)index >= (uint)count)
            {
                _restriction = null;

                throw new ArgumentOutOfRangeException(
                    nameof(indices), index, $"a candidate index is 0..{count - 1}");
            }

            mask[index] = true;
        }

        _restriction = mask;
        _restrictionForCount = count;
    }

    /// <summary>
    /// The action the current decision settled on — the thing a behaviour-cloning
    /// dataset would record beside the observation.
    /// </summary>
    public EnvAction LastAction => _current;

    /// <summary>
    /// Would the next <see cref="Poll"/> for this slot start a NEW decision.
    /// </summary>
    /// <remarks>
    /// <b>For a gate that hands control to this search, so it changes hands on
    /// the search's boundary rather than on one of its own.</b> Two controllers
    /// with independent cadences flap against each other at the threshold, and
    /// each holds state the other does not maintain — the scripted layer's
    /// follower, this search's absolute aim. <c>PolicyBot</c> already refuses to
    /// swap mid-decision for exactly that reason; a caller driving this class has
    /// to be able to ask the same question, and <b>inventing a second counter to
    /// answer it is the two-cadence bug with extra steps.</b>
    /// <para>
    /// <b>Takes the slot, because a slot change forces a boundary.</b>
    /// <see cref="Poll"/> resets the decision when the slot moves, so a
    /// parameterless property would answer for the wrong character on the tick a
    /// bot is re-assigned and would be wrong exactly once, silently.
    /// </para>
    /// </remarks>
    public bool AtDecisionBoundary(int slot) => slot != _currentSlot || _ticksLeftInDecision <= 0;

    /// <summary>
    /// Points this search's decoder at where the character is ACTUALLY aiming.
    /// </summary>
    /// <remarks>
    /// <b>Call this on every handover into the search, or the first decision
    /// after each one turns from a bearing nobody is pointing at.</b> The decoder
    /// carries an absolute aim across ticks and advances it by the turn each
    /// action asks for. While another controller holds the character — the
    /// scripted layer writes the bearing directly — that carried value goes stale,
    /// and <see cref="Poll"/>'s own resync only fires when the <em>slot</em>
    /// changes, which a handover is not.
    /// <para>
    /// <b>This is a wrong-verdict bug rather than a wrong-number bug, which is
    /// why it is public rather than a note.</b> A search aiming from a stale
    /// bearing does not fail; it fights slightly worse, in exactly the engagements
    /// a handover exists to hand it, and reports as "the search hybrid
    /// underperforms" — indistinguishable from a real negative.
    /// </para>
    /// <para>
    /// <b>Takes the world rather than degrees on purpose.</b> The wire's aim is
    /// a <c>ushort</c> over a full turn and the conversion is
    /// <c>* 360.0 / 65536.0</c>; a caller handed a degrees parameter has to know
    /// that, and <c>PolicyBot</c> already carries its own copy of the expression.
    /// One more copy is one more place to get a magic constant wrong.
    /// </para>
    /// <para>
    /// <b>Unreachable in a duel.</b> An <c>EnvDriver.Rollout</c> side has no
    /// handover and no scripted layer writing its bearing, so
    /// <c>_currentSlot</c> never changes hands and the stale path cannot occur.
    /// </para>
    /// </remarks>
    public void ResyncAim(in WorldView world, int slot) =>
        _decoder.SetAim(slot, world.Character(slot).NetAimDirection * 360.0 / 65536.0);

    /// <summary>
    /// The candidate set: every combination the search considers, in a fixed
    /// order.
    /// </summary>
    /// <remarks>
    /// <b>Deterministic and drawn from nothing.</b> A search that broke ties
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
    /// <b>Rebuilt when the class changes, and only the sniper pays for the
    /// zoom dimension.</b> Doubling 60 candidates to 120 doubles the per-decision
    /// cost — measured at 2.94 ms for 60 (Release, gg_debug) — and
    /// <c>PlayerCommands.ToggleZoom</c> refuses every class but the sniper, so
    /// every other class would be paying twice to evaluate the same sixty actions
    /// under two labels.
    /// <para>
    /// <b>Without this the zoom head would be a trap rather than a feature.</b>
    /// <see cref="ScriptedBot"/> also has the scope; a search whose candidate set
    /// was still a five-head enumeration would be measured against a scripted
    /// sniper playing a 75-damage weapon while itself capped at 35, and the
    /// honest reading of that number would be "the search got worse".
    /// </para>
    /// </remarks>
    /// <summary>
    /// The candidate set a search with these options would use for this class,
    /// without needing one to exist.
    /// </summary>
    /// <remarks>
    /// <b>Pure and static on purpose.</b> The instance path rebuilds
    /// <c>_candidates</c> in place, and calling that out of band could shrink the
    /// array under a <see cref="LastCandidates"/> span still describing the
    /// previous class's length. Nothing here touches instance state.
    /// <para>
    /// <b>It exists because a distribution is useless without the actions it
    /// is over.</b> <c>StepScored</c> sends scores aligned with this list and a
    /// consumer that had to reconstruct the enumeration would be holding a second
    /// source of truth about this search — the exact thing the input seam exists
    /// to prevent, and the argument this repo already made about the shadow
    /// slot's argmax before noticing it applied to the whole distribution.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<EnvAction> CandidatesFor(RolloutOptions options, PlayerClass playerClass) =>
        BuildCandidates(options.WithDefaults(), considerZoom: playerClass == PlayerClass.Sniper);

    /// <summary>
    /// How many candidates <see cref="CandidatesFor(RolloutOptions, PlayerClass)"/>
    /// would return, without building them.
    /// </summary>
    /// <remarks>
    /// <b>This exists because a third source for the count shipped a bug.</b>
    /// <c>DuelEnv.CandidatesFor</c> derives the set from the batch spec's class
    /// and answers 120 for a sniper before the first step; the instance
    /// <see cref="CandidateCount"/> answers whatever class was last searched, and
    /// the constructor builds without the zoom dimension — so it answers 60 for a
    /// sniper until its first decision. The restriction validator read the
    /// instance and refused a perfectly valid index 76, on step 0 only, for
    /// snipers only.
    /// <para>
    /// <b>Anything that needs "how many candidates will this slot have" must
    /// come here or to <c>CandidatesFor</c>, never to
    /// <see cref="CandidateCount"/>.</b>
    /// <c>RolloutRestrictionTests.EveryCountSourceAgrees</c> pins the two together
    /// for every class, because an arithmetic count that drifted from the loop
    /// that builds the list would be the same bug wearing a different hat.
    /// </para>
    /// </remarks>
    public static int CandidateCountFor(RolloutOptions options, PlayerClass playerClass)
    {
        var settled = options.WithDefaults();

        // BuildCandidates' loop nest, counted rather than walked: aims, then
        // fire (special or not), then zoom (sniper only), then 3 horizontal by
        // 2 jump.
        return settled.Aims.Length
            * (settled.ConsiderSpecial ? 3 : 2)
            * (playerClass == PlayerClass.Sniper ? 2 : 1)
            * 3
            * 2;
    }

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
    /// <b>The scope, and it is the only thing this search does that is not a
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

        // The decoder carries this slot's absolute aim across ticks, and a
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
    /// <b>The decoder's aim is saved and restored around every candidate.</b>
    /// <c>ActionDecoder.Decode</c> advances the slot's absolute bearing, so a
    /// rollout that turned would leave the real decision starting from a bearing
    /// that only existed inside a simulation nobody played. That is exactly the
    /// class of bug <c>ActionDecoder.Aim</c>'s own remark warns about for
    /// snapshots, and it is silent — the bot simply aims somewhere nobody chose.
    /// </remarks>
    /// <summary>
    /// Everything ONE candidate rollout mutates, so candidates can run apart.
    /// </summary>
    /// <remarks>
    /// <b>The candidate loop is embarrassingly parallel once this exists</b>:
    /// without it every rollout would write the same <c>_fork</c>, the same
    /// <c>_events</c> and the same <c>_decoder</c>. What it reads — the baseline,
    /// the options, the <c>CollisionWorld</c> — is immutable for the duration of
    /// a decision, and <c>GG2.Simulation</c> holds no mutable statics, so
    /// <c>MatchSimulation.Tick</c> is pure over its arguments.
    /// <para>
    /// <b>A fork is a 145 KB <c>WorldState</c> copy, so lanes are allocated once
    /// and reused</b> across decisions. k lanes rather than k forks per decision is
    /// the difference between a pool and a leak.
    /// </para>
    /// <para>
    /// <b>Determinism does not come from the lanes; it comes from the reduction.</b>
    /// Scores are written to a per-candidate slot and the winner is chosen afterwards
    /// in ascending candidate order, so the lexicographic tie-break sees exactly the
    /// order it saw serially — which matters because 85.2% of non-blind decisions have
    /// their top two tied and iteration order decides them.
    /// </para>
    /// </remarks>
    private sealed class Lane
    {
        internal WorldState Fork;
        internal TickEvents Events;
        internal readonly ActionDecoder Decoder = new();
    }

    private readonly Lane _serial = new();

    private Lane[] _lanes = [];

    // Indexed by candidate index, not by search order, so a worker writes its
    // own slot and the reduction reads them back in ascending order. That is the
    // whole determinism argument; see the Lane remarks.
    private double[] _scoreByIndex = [];

    private double[] _aimByIndex = [];

    private EnvAction Search(in WorldView world, int slot)
    {
        _baseline = world.State;

        // Per class, so only a sniper pays for the zoom dimension — and asked
        // here rather than in the constructor because a bot can be re-classed
        // mid-life, exactly as ScriptedBot rebuilds its NavQuery every tick.
        var candidates = CandidatesFor(world.Character(slot).Class);

        // Grown here and not per decision. CandidatesFor can change the length
        // when the class does, so the check is against the current set rather
        // than done once in the constructor.
        if (_scores.Length < candidates.Length)
        {
            _scores = new double[candidates.Length];
            _searched = new int[candidates.Length];
            _searchedCandidates = new EnvAction[candidates.Length];
            _scoreByIndex = new double[candidates.Length];
            _aimByIndex = new double[candidates.Length];
        }

        // The restriction is dropped if the candidate set changed under it, and
        // the check is on the exact size rather than on the buffer being big
        // enough. A class swap moves the list between 60 and 120, and
        // BuildCandidates interleaves zoom inside each (aim, fire) — so index i
        // in the 120-list is a different action from index i in the 60-list, and
        // a mask that merely FITS would silently search the wrong six actions.
        // Searching everything is the safe direction to be wrong in.
        var restriction = _restrictionForCount == candidates.Length ? _restriction : null;

        _restriction = null;
        _restrictionForCount = 0;

        var savedAim = _decoder.Aim(slot);
        var best = EnvAction.Idle;
        var bestScore = double.NegativeInfinity;
        var bestAimError = double.PositiveInfinity;

        // Ascending candidate order whatever order the restriction arrived in,
        // so the decision is a pure function of the SET. The tie-break below is
        // lexicographic and 85.2% of non-blind decisions have their top two tied,
        // so iteration order decides a great many of them — a search whose answer
        // depended on how a client happened to sort its indices would be
        // irreproducible in a way no test would show.
        var kept = 0;

        for (var i = 0; i < candidates.Length; i++)
        {
            if (restriction is null || restriction[i])
                _searched[kept++] = i;
        }

        // Scoring and choosing are separate passes, and that is what makes the
        // threaded path identical to the serial one rather than merely similar.
        // Scores land in per-candidate slots in whatever order the workers finish;
        // the winner is picked below in ascending order, so the lexicographic
        // tie-break sees exactly the sequence it saw when there was one thread.
        if (_options.Threads > 1 && kept > 1)
            ScoreParallel(world.Collision, slot, savedAim, candidates, kept);
        else
            ScoreSerial(world.Collision, slot, savedAim, candidates, kept);

        for (var n = 0; n < kept; n++)
        {
            var i = _searched[n];
            var score = _scoreByIndex[i];

            // Every score is kept, not just the winner's. See LastScores: the
            // distribution is what makes a search a better expert than a reward
            // sample.
            _scores[n] = score;
            _searchedCandidates[n] = candidates[i];

            // Lexicographic: damage first, aim error only as a tie-break. It
            // cannot change any decision where the scores differ, so it cannot
            // trade away the thing the search is actually optimising — it only
            // replaces enumeration order on the decisions that were arbitrary.
            // See the aim-tie-break remarks on Rollout.
            var aimError = _aimByIndex[i];

            if (score > bestScore || (score == bestScore && aimError < bestAimError))
            {
                bestScore = score;
                bestAimError = aimError;
                best = candidates[i];
            }
        }

        _decoder.SetAim(slot, savedAim);

        _scoreCount = kept;

        // Counted from what was searched, not from the candidate list. The
        // whole point of a restriction is that it costs less, and an accounting
        // figure that ignored it would report a budget nobody spent — this is the
        // number docs/BOTAI.md item 9 says to watch.
        LastDecisionTicksSimulated = (long)kept * _options.HorizonTicks;
        LastBestScore = bestScore;

        return best;
    }

    /// <summary>Every kept candidate, scored on this thread.</summary>
    private void ScoreSerial(
        CollisionWorld collision, int slot, double savedAim, EnvAction[] candidates, int kept)
    {
        for (var n = 0; n < kept; n++)
        {
            var i = _searched[n];
            _serial.Decoder.SetAim(slot, savedAim);
            _scoreByIndex[i] = Rollout(collision, slot, candidates[i], _serial, out var aimError);
            _aimByIndex[i] = aimError;
        }
    }

    /// <summary>Every kept candidate, scored across <c>Threads</c> lanes.</summary>
    /// <remarks>
    /// <b>Contiguous chunks, one lane each, no work stealing.</b> A worker owns
    /// its lane for the whole call, so nothing is shared and nothing is locked —
    /// which is the only reason a 145 KB fork per lane is affordable at all. The
    /// split is by count rather than by cost and candidates are not equal-cost (a
    /// candidate that dies early stops at its own <c>IsAlive</c> break), so the tail
    /// is ragged and the speedup is below the thread count before any other effect.
    /// <para>
    /// <b><c>Parallel.For</c> allocates</b> — the closure, the task objects, the
    /// range state — inside what CLAUDE.md calls the tick loop. That is real and it
    /// is deliberately inside the measurement rather than excluded from it: a
    /// threading factor quoted with its own overhead removed is not the factor the
    /// tick budget would see.
    /// </para>
    /// </remarks>
    private void ScoreParallel(
        CollisionWorld collision, int slot, double savedAim, EnvAction[] candidates, int kept)
    {
        var workers = Math.Min(_options.Threads, kept);
        EnsureLanes(workers);

        Parallel.For(0, workers, w =>
        {
            var lane = _lanes[w];
            var from = (int)((long)kept * w / workers);
            var to = (int)((long)kept * (w + 1) / workers);

            for (var n = from; n < to; n++)
            {
                var i = _searched[n];
                lane.Decoder.SetAim(slot, savedAim);
                _scoreByIndex[i] = Rollout(collision, slot, candidates[i], lane, out var aimError);
                _aimByIndex[i] = aimError;
            }
        });
    }

    /// <summary>At least <paramref name="workers"/> lanes exist, and are reused.</summary>
    private void EnsureLanes(int workers)
    {
        if (_lanes.Length >= workers)
            return;

        var grown = new Lane[workers];
        Array.Copy(_lanes, grown, _lanes.Length);

        for (var w = _lanes.Length; w < workers; w++)
            grown[w] = new Lane();

        _lanes = grown;
    }

    /// <summary>
    /// One candidate, played forward and scored.
    /// </summary>
    /// <remarks>
    /// <b>The score is the damage the simulation emits, not a state diff.</b>
    /// <c>docs/BOTAI.md</c> item 7 exists for this: diffing two snapshots is
    /// slow, ambiguous about attribution, and would make the bot re-derive game
    /// rules it must not know. <see cref="TickEvents.DamageDealt"/> is credited
    /// at the moment damage is applied and is exactly the reward a policy would
    /// be trained on, which is what makes this bot's decisions valid data.
    /// <para>
    /// Damage taken is subtracted at equal weight. That is a choice, not a
    /// transcription: it makes the search trade a hit for a hit rather than
    /// chase damage at any cost, and it is the simplest thing that is not
    /// obviously wrong. <b>It is also the first knob to question</b> if the
    /// bot plays suicidally — an asymmetric weight is a one-line change here and
    /// a real behavioural difference.
    /// </para>
    /// <para>
    /// <b>A death ends the rollout early.</b> Without that the search happily
    /// dies on tick 3 to land a hit on tick 12, because a corpse deals no damage
    /// and takes none — so the remaining ticks look free.
    /// </para>
    /// </remarks>
    /// <param name="aimError">
    /// Absolute degrees between where this candidate leaves the searcher pointing
    /// and the bearing to its nearest live enemy, at the end of the rollout.
    /// <see cref="double.PositiveInfinity"/> when there is nobody to point at.
    /// </param>
    /// <remarks>
    /// <b>The tie-break, and it exists because the search is indifferent on
    /// most of its decisions.</b> Measured over a real corpus: 85.2% of
    /// non-blind decisions have the top two candidates tied, a median of 6
    /// actions share the best score, and only 7.8% have a unique best. Ties
    /// went to the first candidate, and
    /// <see cref="BuildCandidates"/> loops aim outermost from bin 0 — a hard left
    /// turn — so the bot took <b>0.966</b> (soldier) and <b>0.929</b> (scout) of
    /// its decisions with the aim swung hard left. <b>Its aiming was enumeration
    /// order, not scoring.</b>
    /// <para>
    /// <b>Damage stays the only objective.</b> This is compared only when the
    /// scores are exactly equal, so no decision the score can settle is changed,
    /// and the search cannot trade a hit for a better bearing. What it replaces is
    /// loop order.
    /// </para>
    /// <para>
    /// <b>Measured at the end of the rollout, not the start.</b> Where a
    /// candidate leaves the character pointing is what the next decision inherits;
    /// the bearing it started from is the same for every candidate and would
    /// discriminate nothing.
    /// </para>
    /// </remarks>
    private double Rollout(
        CollisionWorld collision, int slot, EnvAction action, Lane lane, out double aimError)
    {
        lane.Fork = _baseline;

        // The candidate's scope is applied to the fork before it is played, or
        // the search scores a zoom it never raised — every zoom candidate would
        // simulate identically to its unzoomed twin, the tie would go to the
        // earlier one, and the head would look measured while being inert.
        // PlayerCommands.ToggleZoom is the same method the server calls; it
        // refuses a non-sniper on its own.
        if (ActionDecoder.WantsZoomToggle(action, in lane.Fork.Characters[slot]))
            PlayerCommands.ToggleZoom(ref lane.Fork, slot);

        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];
        var score = 0.0;

        // Per candidate, not per search. Each candidate is an independent
        // forward simulation, so its continuation has to start from the same
        // state — otherwise candidate n+1 inherits candidate n's last policy
        // action and the ordering of the candidate list becomes observable.
        var continuation = EnvAction.Idle;

        for (var tick = 0; tick < _options.HorizonTicks; tick++)
        {
            if (!lane.Fork.IsAlive(slot))
                break;

            // Past the commitment, is this the first tick of a continuation
            // decision? Counted FROM the end of the commitment, which is what the
            // phase guard in RolloutOptions.WithDefaults exists to keep whole.
            var sinceCommit = tick - _options.CommitTicks;
            var deciding = sinceCommit >= 0 && sinceCommit % _options.DecisionTicks == 0;

            for (var other = 0; other < WorldState.MaxSlots; other++)
            {
                if (other != slot)
                {
                    inputs[other] = OpponentInput(in lane.Fork, other);
                    continue;
                }

                // Past the commitment the candidate is not held. The searcher
                // keeps the bearing the action turned it to
                // and releases the keys, which models "I will decide again shortly
                // and am not committed to this key" — the ordinary receding-horizon
                // split. With CommitTicks defaulted to the horizon this is
                // unreachable and the behaviour is exactly what it always was.
                inputs[other] = tick < _options.CommitTicks
                    ? lane.Decoder.Decode(slot, action, firstTickOfDecision: tick == 0)
                    : lane.Decoder.Decode(slot, continuation, firstTickOfDecision: deciding);
            }

            lane.Events = default;
            MatchSimulation.Tick(collision, ref lane.Fork, inputs, ref lane.Events);

            score += lane.Events.DamageDealt[slot] - lane.Events.DamageTaken[slot];
        }

        aimError = AimErrorToNearestEnemy(collision, slot, lane);
        return score;
    }

    /// <summary>
    /// How far off the searcher is pointing from its nearest live enemy, in the
    /// forked world, in absolute degrees.
    /// </summary>
    /// <remarks>
    /// Infinity rather than 0 when there is nobody to aim at, so "no target"
    /// loses every tie-break to "some target, badly aimed" rather than winning
    /// them all. A zero here would make every candidate equal again the moment the
    /// searcher is alone, which is the state this whole tie-break exists to stop
    /// being decided by loop order.
    /// </remarks>
    private double AimErrorToNearestEnemy(CollisionWorld collision, int slot, Lane lane)
    {
        if (!lane.Fork.IsAlive(slot))
            return double.PositiveInfinity;

        var view = new WorldView(in lane.Fork, collision);
        Span<int> found = stackalloc int[1];

        if (view.NearestEnemies(slot, found) == 0)
            return double.PositiveInfinity;

        ref readonly var self = ref lane.Fork.Characters[slot];
        ref readonly var enemy = ref lane.Fork.Characters[found[0]];

        var bearing = Gml.PointDirection(self.X, self.Y, enemy.X, enemy.Y);
        var aim = self.NetAimDirection * 360.0 / 65536.0;

        // Signed shortest angle into (-180, 180], then absolute.
        return Math.Abs(((bearing - aim + 540.0) % 360.0) - 180.0);
    }

    /// <summary>
    /// What the searcher itself is assumed to do after its commitment ends.
    /// </summary>
    /// <remarks>
    /// <b>Keys released, bearing kept.</b> The aim is carried by the decoder
    /// across ticks, so an action with no turn and no keys leaves the character
    /// pointing where the candidate put it and pressing nothing. That is the
    /// neutral continuation: it scores what the action buys inside its
    /// commitment, plus whatever follows from merely being there.
    /// <para>
    /// <b>It is deliberately not hold-last-input.</b> Holding the candidate on
    /// is precisely the behaviour the split exists to remove, so a continuation
    /// that did it would make <c>CommitTicks</c> a no-op that looked implemented.
    /// </para>
    /// </remarks>
    private static EnvAction Continuation => EnvAction.Idle;

    /// <summary>
    /// What a character other than the searcher is assumed to be doing.
    /// </summary>
    /// <remarks>
    /// See <see cref="RolloutOpponent"/> for why this is a named decision rather
    /// than an accident. <b>A dead or absent slot gets
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
