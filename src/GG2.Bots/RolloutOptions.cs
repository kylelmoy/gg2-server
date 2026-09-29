
namespace GG2.Bots;

/// Whether the right-click head is searched. Off by default: for most classes it
/// is a reload or nothing, and it triples nothing useful.
/// </param>
public readonly record struct RolloutOptions(
    int HorizonTicks = 20,
    int DecisionTicks = 4,
    RolloutOpponent Opponent = RolloutOpponent.HoldLastInput,
    byte[]? AimBins = null,
    bool ConsiderSpecial = false,
    int CommitTicks = 0,
    PolicyNetwork? ContinuationPolicy = null,
    int Threads = 0)
{
    /// <summary><c>docs/BOTAI.md</c> item 12 says 15-30 ticks; this is the middle.</summary>
    public const int DefaultHorizonTicks = 20;

    /// <summary>The same action repeat <c>GG2.Env</c>'s duels use.</summary>
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
    public RolloutOptions WithDefaults()
    {
        var horizon = HorizonTicks == 0 ? DefaultHorizonTicks : HorizonTicks;
        var decision = DecisionTicks == 0 ? DefaultDecisionTicks : DecisionTicks;

        // ⚠️ 0 means "commit for the whole horizon", which is the behaviour this
        // search has always had — see the parameter's own remarks. It is resolved
        // here rather than at the use site so there is one place that decides.
        var commit = CommitTicks == 0 ? horizon : CommitTicks;

        // ⛔ THE PHASE GUARD, and it is here — where CommitTicks is SET — rather
        // than asserted where it is read, because a fix belongs at the point that
        // makes the mistake unavailable. `gg2-rl` asked for exactly this and
        // sharpened its own first proposal (a read-site assert) to get it.
        //
        // A continuation re-decides every DecisionTicks ticks counting from the
        // end of the commitment. If the commitment is not a whole number of
        // decisions, the policy's decision boundaries fall out of phase with the
        // one the search actually plays — so a candidate is scored under a
        // continuation whose cadence it will never experience in the real match,
        // and the whole point of matching the training action_repeat is lost.
        //
        // ⚠️ Enforced whether or not a POLICY is attached -- that was the ask, and
        // a rule that only applies sometimes is one nobody can rely on.
        //
        // ⛔ But NOT enforced when no continuation runs at all, and the first
        // version of this guard got that wrong. `commit == horizon` is the
        // default and means the candidate is held for the whole rollout, so the
        // `tick >= CommitTicks` branch is never reached and there are no
        // continuation decisions to be out of phase. Requiring a multiple there
        // is a constraint on the HORIZON wearing a commitment's clothes, and it
        // refused eight existing configurations -- `HorizonTicks: 10,
        // DecisionTicks: 4` among them -- that have no continuation and never did.
        //
        // ⚠️ This is CLAUDE.md's "check that a criterion could actually fire" in
        // its mirror image: not a gate that can never fire, but one firing where
        // the thing it protects does not exist. Both are the same mistake about
        // what a check is FOR, and the tests caught this one in under a minute.
        if (commit < horizon && commit % decision != 0)
        {
            throw new ArgumentException(
                $"CommitTicks {commit} is not a multiple of DecisionTicks {decision} "
                + $"(horizon {horizon}, so a continuation does run). "
                + "A continuation re-decides on the decision cadence counting from the end of "
                + "the commitment, so a partial decision at the seam puts every later one out "
                + "of phase with the cadence the policy was trained at.",
                nameof(CommitTicks));
        }

        var threads = Threads == 0 ? 1 : Threads;

        if (threads < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Threads), Threads, "a search runs on at least one thread");
        }

        // ⛔ See the Threads remarks: PolicyNetwork forwards through shared scratch,
        // so a threaded search with a continuation is a data race on the hidden
        // layer. It would not throw and it would not look wrong — it would just
        // decide from a half-written activation, which is the shape of bug this
        // repo has no instrument for. Refused where the combination is chosen.
        if (threads > 1 && ContinuationPolicy is not null)
        {
            throw new ArgumentException(
                $"Threads {threads} cannot be combined with a ContinuationPolicy: "
                + "PolicyNetwork.Act writes through per-instance hidden-layer scratch and "
                + "is not re-entrant, so concurrent candidates would race on it.",
                nameof(Threads));
        }

        return this with
        {
            HorizonTicks = horizon,
            DecisionTicks = decision,
            CommitTicks = commit,
            Threads = threads,
        };
    }
}
