
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
