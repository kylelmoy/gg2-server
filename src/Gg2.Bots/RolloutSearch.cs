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
/// ⚠️ <b>The values are EXPLICIT because this enum goes on the wire.</b>
/// <c>EnvServer.MakeBatch</c> reads it as an <c>i32</c>, so the declaration order
/// IS the protocol — reordering these two members would be a silent wire change
/// that compiles, runs, and quietly selects the other opponent model. `gg2-rl`'s
/// drift gate reads this enum positionally out of this file and is the only thing
/// that would catch it; the numbers mean it has nothing to catch.
/// <para>
/// ⚠️ Same class as the <c>Idle</c>-declared-first bug this seam already hit once,
/// recorded in the remark below. <c>EnvDriver</c> and <c>PlayerClass</c> were
/// already explicit; this was the one that was not.
/// </para>
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
    HoldLastInput = 0,

    /// <summary>
    /// ⚠️ <b>The opponent presses nothing.</b> Cheapest, and <b>optimistic</b>:
    /// a search that believes nobody will shoot back walks into fire.
    /// </summary>
    /// <remarks>
    /// Kept because it is the honest floor for a comparison — if a search is not
    /// better with a moving opponent than with a statue, the opponent model is
    /// not what is wrong. Not the default.
    /// </remarks>
    Idle = 1,
}

/// <summary>
/// How a <see cref="RolloutSearch"/> is configured.
/// </summary>
/// <param name="HorizonTicks">
/// How far each candidate is played forward. <c>docs/BOTAI.md</c> item 12 says
/// 15-30; 20 is the default.
/// ⚠️ <b>That default is a DESIGN-TIME RANGE, not a measurement, and the only
/// direction anyone has measured says it should come down.</b> <c>gg2-rl</c>'s
/// sweep (2026-08-29) took it from 20 out to 90 and found the win rate falling
/// monotonically in every class — sniper 68.4% → 22.8%, soldier 93.8% → 63.7%.
/// <b>Nobody has run it BELOW 20</b>, and 15 is inside item 12's own range, so
/// the whole measured curve points into an untested region.
/// <para>
/// ⛔ <b>Do not lower it on that reasoning alone.</b> The monotone stretch is
/// 20-90, and extrapolating off the end of a measured range is how the blind
/// fraction came to be minimised in the first place.
/// </para>
/// <para>
/// ✅ <b>RUN 2026-08-31, and the refusal above was right for the right reason:
/// THE CURVE IS NOT MONOTONE BELOW THE DEFAULT.</b> Four classes, in-range band,
/// tier 5, 288 duels a cell — horizon 8 wins <b>80.4%</b> [73.6, 85.8], 12 wins
/// 88.5%, 15 wins <b>93.1%</b> [88.1, 96.1], 20 wins 90.8%. So the upward sweep's
/// monotone fall was <b>one side of a peak</b>, not a slope running off to the
/// left, and "shorter is simply better" would have been wrong at 8 — where the
/// interval is cleanly disjoint from 15's.
/// </para>
/// <para>
/// ⛔ <b>"15 beats 20" DID NOT HOLD, and the re-run is why this paragraph never
/// said it did.</b> At 288 a cell it read 93.1% against 90.8%, a clean +2.3 with
/// the interesting sign. At <b>864</b> a cell: <b>91.6%</b> [88.6, 93.8] against
/// <b>90.5%</b> [87.6, 92.8] — +1.1, overlapping. ⚠️ <b>The default's cell was
/// the stable one</b> (90.8 → 90.5) and the 15 cell was the noisy one
/// (93.1 → 91.6), so the apparent effect was almost entirely one arm being lucky.
/// </para>
/// <para>
/// ✅ <b>So the default STAYS AT 20, and now for a measured reason rather than an
/// unexamined one</b>: it sits inside the peak region, and the region below it has
/// been looked at. The peak's location is somewhere between 12 and 20 on present
/// evidence, with 15 and 20 indistinguishable at 864 a cell. Pinning it further
/// would want many more duels or seeds and <b>nothing currently depends on the
/// exact value</b>.
/// </para>
/// <para>
/// ⚠️ <b>That sweep's BLIND column is not comparable and should not be used.</b>
/// It was measured over the decisions each arm actually generated, and the arms
/// play differently, so they visit different states — it is non-monotone in a way
/// that gives it away (8 reads less blind than 12, which no mechanism predicts).
/// The upward sweep's blind numbers stepped a fixed count precisely to avoid
/// that. <b>The two sets of blind fractions are from different instruments and
/// must not be merged.</b>
/// </para>
/// <para>
/// ⚠️ <b>And it reproduced at 864 a cell — 15 blinder than 20, 62.1% against
/// 54.7%, at both sample sizes — which does NOT redeem it.</b> A biased
/// instrument is biased consistently; agreement between two runs of the same
/// wrong measurement is not evidence about the quantity. If the blind axis ever
/// matters for this question it wants the fixed-step harness, not a by-product of
/// a win-rate run.
/// </para>
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
/// <param name="CommitTicks">
/// How many ticks a candidate's action is actually HELD before the rollout
/// continues under a neutral continuation. <b>0 means the whole horizon</b>,
/// which is what this search has always done.
/// ⚠️ <b>Lookahead and commitment are the same number until this is set</b>, and
/// <c>gg2-rl</c>'s horizon sweep is what showed that mattered: blind fraction and
/// win rate oppose monotonically for every class, because at horizon 90 the
/// search is choosing among plans of the shape "hold this key and this aim for
/// three seconds". Seeing further and committing longer are separable, and this
/// is the separation.
/// <para>
/// ❌ <b>MEASURED 2026-08-29, AND IT DOES NOT HELP.</b> The signature of a real
/// fix was agreed in advance — the blind fraction falling <em>without</em> the
/// win rate falling. It does the opposite. <c>ctf_2dfort</c> banded, tier 5,
/// <b>24 duels a cell</b> — see the sample-size caveat below:
/// </para>
/// <list type="bullet">
/// <item>sniper, horizon 60: commit=horizon <b>0.523 blind / 0.375 win</b>;
/// commit=4 <b>0.965 / 0.625</b>; commit=8 <b>0.962 / 0.667</b>.</item>
/// <item>soldier, horizon 60: commit=horizon <b>0.562 / 0.750</b>; commit=8
/// <b>0.851 / 0.792</b>.</item>
/// </list>
/// <para>
/// ⚠️ <b>Shortening the commitment puts the blindness straight back</b> — to
/// 0.965 for a sniper, worse than horizon 20's 0.933 — while recovering only part
/// of the win rate. It slides back along the same trade rather than breaking it,
/// and the reason is the same refire arithmetic: a candidate scores from damage
/// landing inside the window where it is acting, and shortening the commitment
/// shortens that window.
/// </para>
/// <para>
/// ⚠️ <b>AND THE WIN-RATE HALF IS THIN — 24 duels a cell.</b> `gg2-rl` found
/// four results on 2026-08-31 that moved substantially when the sample was
/// widened, one of them from −4.5 to −13.7 points at four times the duels, and
/// concluded its default duel count had been chosen for a task that no longer
/// existed. <b>These cells are smaller than that default.</b> The BLIND fractions
/// here are ratios over many decisions and are not in doubt; 0.523 → 0.965 is not
/// a sampling artefact. The win rates are: 0.375 against 0.625 at n = 24 barely
/// separate. So <b>"it slides back along the same trade" is solid on the blind
/// axis and provisional on the win axis</b>, and anybody about to act on the win
/// half should widen it first.
/// </para>
/// <para>
/// ⚠️ <b>This is a negative result about THIS continuation, not about the
/// idea.</b> The continuation here is neutral — keys released, bearing kept —
/// because holding the candidate on is exactly what the split removes. A
/// continuation that kept <em>acting</em>, such as a distilled policy, is a
/// different experiment and is the one <c>gg2-rl</c> named for phase 3. The knob
/// is kept so that experiment is a one-line change rather than a
/// re-implementation, and so this negative is reproducible.
/// </para>
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
    bool ConsiderSpecial = false,
    int CommitTicks = 0)
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

        // ⚠️ 0 means "commit for the whole horizon", which is the behaviour this
        // search has always had — see the parameter's own remarks. It is resolved
        // here rather than at the use site so there is one place that decides.
        CommitTicks = CommitTicks == 0
            ? (HorizonTicks == 0 ? DefaultHorizonTicks : HorizonTicks)
            : CommitTicks,
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
    /// <summary>
    /// The candidate set currently BUILT — which is the class this search last
    /// drove, not the class it is about to.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the number to validate a restriction against.</b> The constructor
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
    /// ⚠️ <b>This is the whole reason a search is a better expert than a reward
    /// signal, and until 2026-08-29 it did not survive the method that computed
    /// it.</b> <c>gg2-rl/docs/DESIGN.md</c> — "its output is an ACTION
    /// DISTRIBUTION rather than a scalar, so every logged decision carries far
    /// more signal than a reward sample does" — and its collection step logs
    /// <c>(observation, distribution over actions, outcome)</c>. <see cref="Search"/>
    /// scored all 60 (or 120) candidates and kept only <c>bestScore</c>; every
    /// other score was discarded inside the loop that produced it.
    /// <para>
    /// ⚠️ <b>Nothing was inconsistent, which is why it took a phase needing the
    /// data to surface it.</b> This class did exactly what it documented and that
    /// document said exactly what it wanted, and no test on either side compared
    /// the two. Fourth instance of that shape in three days and the first that was
    /// nobody's bug.
    /// </para>
    /// <para>
    /// <b>Why it matters concretely:</b> a one-hot on the argmax throws away the
    /// search's own uncertainty. That two candidates scored 70 and 69 is exactly
    /// the information saying "either is fine", and cloning the argmax teaches a
    /// policy to be confident there instead.
    /// </para>
    /// <para>
    /// ⚠️ <b>Written into a pre-allocated buffer</b>, resized only when the
    /// candidate set changes — docs/BOTAI.md item 1 forbids allocating in the tick
    /// loop, and this is written once per candidate per decision.
    /// </para>
    /// <para>
    /// ⚠️ <b>Valid only until the next decision</b>, and a span so it cannot be
    /// stashed. A caller logging a dataset copies it.
    /// </para>
    /// </remarks>
    public ReadOnlySpan<double> LastScores => _scores.AsSpan(0, _scoreCount);

    /// <summary>
    /// The candidate set the scores are aligned with.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Handed back with the scores rather than left to be re-derived.</b>
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
    /// ⚠️ <b>Identity for an unrestricted search</b> — 0, 1, 2 … — so a caller
    /// that ignores it is correct exactly as long as nobody prunes. Under
    /// <see cref="RestrictNextDecision"/> it is the searched set, and the scores
    /// are a distribution over <em>that</em>, not over the whole candidate list.
    /// <para>
    /// ⚠️ <b>A distribution without these indices is "a ranking over an unnamed
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
    /// ⚠️ <b>An empty span means "search everything"</b>, which is the default and
    /// the behaviour every existing caller gets.
    /// </para>
    /// <para>
    /// ⚠️ <b>It is a SET, not a ranking, and this must never come to depend on the
    /// order it arrives in.</b> The candidates are visited in ascending index
    /// order whatever order they are given, so the decision is a pure function of
    /// the set — which matters because the tie-break is lexicographic and 85.2% of
    /// non-blind decisions have the top two candidates tied, so iteration order
    /// decides a great many of them. Duplicates collapse.
    /// </para>
    /// <para>
    /// ⚠️ <b>And the set is deliberately NOT the policy's top k.</b> `gg2-rl` sends
    /// top-(k−m) plus m sampled from the remainder, because a search that only
    /// ever evaluates what the policy already likes, feeding a policy trained on
    /// what the search evaluated, is self-confirming: it cannot be corrected where
    /// it is wrong, because where it is wrong is where nothing gets looked at.
    /// **Nothing here may assume index 0 is anybody's favourite.**
    /// </para>
    /// <para>
    /// ⚠️ <b>Consumed by the next SEARCH, not the next Poll.</b> A decision is
    /// held for <see cref="RolloutOptions.DecisionTicks"/> ticks and only its
    /// first tick searches, so a restriction set mid-decision applies to the
    /// decision after it rather than being silently dropped.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An index is negative or past <see cref="CandidateCount"/>. ⚠️ <b>Refused,
    /// never clamped</b> — an index that silently meant a different action would
    /// be a wrong search reported with plausible numbers.
    /// </exception>
    public void RestrictNextDecision(ReadOnlySpan<int> indices, PlayerClass playerClass)
    {
        if (indices.Length == 0)
        {
            _restriction = null;
            return;
        }

        // ⚠️ The count for the class this slot WILL be searched as, never the
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
    /// <summary>
    /// The candidate set a search with these options would use for this class,
    /// without needing one to exist.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Pure and static on purpose.</b> The instance path rebuilds
    /// <c>_candidates</c> in place, and calling that out of band could shrink the
    /// array under a <see cref="LastCandidates"/> span still describing the
    /// previous class's length. Nothing here touches instance state.
    /// <para>
    /// ⚠️ <b>It exists because a distribution is useless without the actions it
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
    /// ⛔ <b>This exists because a THIRD source for the count shipped a bug.</b>
    /// <c>DuelEnv.CandidatesFor</c> derives the set from the batch spec's class
    /// and answers 120 for a sniper before the first step; the instance
    /// <see cref="CandidateCount"/> answers whatever class was last SEARCHED, and
    /// the constructor builds without the zoom dimension — so it answers 60 for a
    /// sniper until its first decision. The restriction validator read the
    /// instance and refused a perfectly valid index 76, on step 0 only, for
    /// snipers only. Found by <c>gg2-rl</c> within an hour of it shipping.
    /// <para>
    /// ⚠️ <b>Anything that needs "how many candidates will this SLOT have" must
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

        // ⚠️ Grown here and not per decision. CandidatesFor can change the length
        // when the class does, so the check is against the current set rather
        // than done once in the constructor.
        if (_scores.Length < candidates.Length)
        {
            _scores = new double[candidates.Length];
            _searched = new int[candidates.Length];
            _searchedCandidates = new EnvAction[candidates.Length];
        }

        // ⚠️ The restriction is DROPPED if the candidate set changed under it, and
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
        var kept = 0;

        // ⚠️ ASCENDING candidate order whatever order the restriction arrived in,
        // so the decision is a pure function of the SET. The tie-break below is
        // lexicographic and 85.2% of non-blind decisions have their top two tied,
        // so iteration order decides a great many of them — a search whose answer
        // depended on how a client happened to sort its indices would be
        // irreproducible in a way no test would show.
        for (var i = 0; i < candidates.Length; i++)
        {
            if (restriction is not null && !restriction[i])
                continue;

            _decoder.SetAim(slot, savedAim);

            var score = Rollout(world.Collision, slot, candidates[i], out var aimError);

            // ⚠️ EVERY score is kept, not just the winner's. See LastScores: the
            // distribution is the thing that makes a search a better expert than
            // a reward sample, and it used to be discarded inside this loop.
            _scores[kept] = score;
            _searched[kept] = i;
            _searchedCandidates[kept] = candidates[i];
            kept++;

            // ⚠️ LEXICOGRAPHIC: damage first, aim error only as a TIE-BREAK. It
            // cannot change any decision where the scores differ, so it cannot
            // trade away the thing the search is actually optimising — it only
            // replaces enumeration order on the decisions that were arbitrary.
            // See the aim-tie-break remarks on Rollout.
            if (score > bestScore || (score == bestScore && aimError < bestAimError))
            {
                bestScore = score;
                bestAimError = aimError;
                best = candidates[i];
            }
        }

        _decoder.SetAim(slot, savedAim);

        _scoreCount = kept;

        // ⚠️ Counted from what was SEARCHED, not from the candidate list. The
        // whole point of a restriction is that it costs less, and an accounting
        // figure that ignored it would report a budget nobody spent — this is the
        // number docs/BOTAI.md item 9 says to watch.
        LastDecisionTicksSimulated = (long)kept * _options.HorizonTicks;
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
    /// <param name="aimError">
    /// Absolute degrees between where this candidate leaves the searcher pointing
    /// and the bearing to its nearest live enemy, at the end of the rollout.
    /// <see cref="double.PositiveInfinity"/> when there is nobody to point at.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>The tie-break, and it exists because the search is INDIFFERENT on
    /// most of its decisions.</b> Measured by <c>gg2-rl</c> over a real corpus:
    /// <b>85.2%</b> of non-blind decisions have the top two candidates tied, a
    /// median of <b>6</b> actions share the best score, and only <b>7.8%</b> have
    /// a unique best. Ties went to the first candidate, and
    /// <see cref="BuildCandidates"/> loops aim outermost from bin 0 — a hard left
    /// turn — so the bot took <b>0.966</b> (soldier) and <b>0.929</b> (scout) of
    /// its decisions with the aim swung hard left. <b>Its aiming was enumeration
    /// order, not scoring.</b>
    /// <para>
    /// ⚠️ <b>Damage stays the only objective.</b> This is compared only when the
    /// scores are exactly equal, so no decision the score can settle is changed,
    /// and the search cannot trade a hit for a better bearing. What it replaces is
    /// loop order.
    /// </para>
    /// <para>
    /// ⚠️ <b>Measured at the END of the rollout, not the start.</b> Where a
    /// candidate leaves the character pointing is what the next decision inherits;
    /// the bearing it started from is the same for every candidate and would
    /// discriminate nothing.
    /// </para>
    /// </remarks>
    private double Rollout(CollisionWorld collision, int slot, EnvAction action, out double aimError)
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
                if (other != slot)
                {
                    inputs[other] = OpponentInput(in _fork, other);
                    continue;
                }

                // ⚠️ Past the commitment the candidate is NOT held. The searcher
                // keeps the bearing the action turned it to and releases the
                // keys, which models "I will decide again shortly and am not
                // committed to this key" — the ordinary receding-horizon split.
                // With CommitTicks defaulted to the horizon this is unreachable
                // and the behaviour is exactly what it always was.
                inputs[other] = tick < _options.CommitTicks
                    ? _decoder.Decode(slot, action, firstTickOfDecision: tick == 0)
                    : _decoder.Decode(slot, Continuation, firstTickOfDecision: false);
            }

            _events = default;
            MatchSimulation.Tick(collision, ref _fork, inputs, ref _events);

            score += _events.DamageDealt[slot] - _events.DamageTaken[slot];
        }

        aimError = AimErrorToNearestEnemy(collision, slot);
        return score;
    }

    /// <summary>
    /// How far off the searcher is pointing from its nearest live enemy, in the
    /// forked world, in absolute degrees.
    /// </summary>
    /// <remarks>
    /// ⚠️ Infinity rather than 0 when there is nobody to aim at, so "no target"
    /// loses every tie-break to "some target, badly aimed" rather than winning
    /// them all. A zero here would make every candidate equal again the moment the
    /// searcher is alone, which is the state this whole tie-break exists to stop
    /// being decided by loop order.
    /// </remarks>
    private double AimErrorToNearestEnemy(CollisionWorld collision, int slot)
    {
        if (!_fork.IsAlive(slot))
            return double.PositiveInfinity;

        var view = new WorldView(in _fork, collision);
        Span<int> found = stackalloc int[1];

        if (view.NearestEnemies(slot, found) == 0)
            return double.PositiveInfinity;

        ref readonly var self = ref _fork.Characters[slot];
        ref readonly var enemy = ref _fork.Characters[found[0]];

        var bearing = Gml.PointDirection(self.X, self.Y, enemy.X, enemy.Y);
        var aim = self.NetAimDirection * 360.0 / 65536.0;

        // Signed shortest angle into (-180, 180], then absolute.
        return Math.Abs(((bearing - aim + 540.0) % 360.0) - 180.0);
    }

    /// <summary>
    /// What the searcher itself is assumed to do after its commitment ends.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Keys released, bearing kept.</b> The aim is carried by the decoder
    /// across ticks, so an action with no turn and no keys leaves the character
    /// pointing where the candidate put it and pressing nothing. That is the
    /// neutral continuation: it scores what the action buys inside its
    /// commitment, plus whatever follows from merely being there.
    /// <para>
    /// ⚠️ <b>It is deliberately NOT hold-last-input.</b> Holding the candidate on
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
