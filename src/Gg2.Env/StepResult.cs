using Gg2.Bots;

namespace Gg2.Env;

/// <summary>
/// What one world hands back for one decision step.
/// </summary>
/// <remarks>
/// ⚠️ <b>Components, not a scalar</b> — <c>docs/ENV.md</c>'s third scope call.
/// The trainer folds these into a reward, so iterating on the reward function
/// never needs a C# rebuild, and a run's reward definition stays visible in the
/// trainer's own config rather than being baked into a binary somebody has to
/// go and read.
/// </remarks>
public readonly record struct StepResult
{
    /// <summary>Hit points this agent dealt over the decision's ticks.</summary>
    public double DamageDealt { get; init; }

    /// <summary>Hit points this agent lost over the decision's ticks.</summary>
    public double DamageTaken { get; init; }

    /// <summary>Deaths this agent caused. 0 or 1 in a duel.</summary>
    public byte Kills { get; init; }

    /// <summary>Deaths this agent suffered.</summary>
    public byte Deaths { get; init; }

    public Termination Termination { get; init; }

    /// <summary>
    /// The action this agent actually took.
    /// </summary>
    /// <remarks>
    /// For a <see cref="EnvDriver.Client"/> side this is exactly what was passed
    /// in, echoed so a trainer logging transitions does not have to correlate its
    /// own send buffer with the reply.
    /// <para>
    /// ⚠️ <b>For a bot side it is the nearest action to what the bot did, and the
    /// aim head is the one that can be lossy.</b> The four discrete heads are
    /// recovered exactly from the key mask — they are the same bits — but the aim
    /// is recovered as the bin nearest the turn the bot actually made, and
    /// <c>ScriptedBot</c> aims straight at its target with no turn-rate cap at
    /// all. When it snaps further than <c>ActionDecoder.MaxTurnDegreesPerDecision</c>
    /// the bin saturates, so replaying that action would turn less than the bot
    /// did.
    /// </para>
    /// <para>
    /// ⚠️ <b>This matters for imitation and not for logging.</b> Cloning a
    /// scripted bot from these actions learns a throttled version of it; using
    /// them to record what an opponent did is exact enough. <b>Measured</b>, and
    /// the number is in <c>docs/ENV.md</c> — do not assume it is rare.
    /// <see cref="ActionExact"/> says so per step rather than leaving it to be
    /// inferred.
    /// </para>
    /// </remarks>
    public EnvAction Action { get; init; }

    /// <summary>
    /// Whether <see cref="Action"/> reproduces what this agent did.
    /// </summary>
    /// <remarks>
    /// Always true for a client side and for any bot whose turn fitted inside the
    /// cap. False means the aim head saturated — the discrete heads are still
    /// exact. ⚠️ <b>A flag rather than a footnote</b>, because the alternative is
    /// a trainer discovering the lossiness as an unexplained gap between its
    /// cloned policy and the bot it cloned.
    /// </remarks>
    public bool ActionExact { get; init; }

    /// <summary>
    /// The winning candidate's score, for a <see cref="EnvDriver.Rollout"/> slot.
    /// <b>NaN</b> for every other driver.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>ZERO LABELS A BLIND DECISION, and that is what this field is for.</b>
    /// <c>RolloutSearch</c>'s own remark has always said that when nothing can
    /// score inside the horizon every candidate scores exactly 0, the tie goes to
    /// the first, and the first is <c>[0,0,0,0,0,0]</c> — "turn hard left, do
    /// nothing". <c>gg2-rl</c> measured what that does to a training corpus:
    /// <b>35.2% of 1,960 collected rows are that exact action</b>, and the share
    /// tracks refire time against the 20-tick horizon — scout (refire 8) 15.3%,
    /// heavy (12) 24.2%, soldier (30) 39.5%, <b>sniper (40) 46.2%</b>.
    /// <para>
    /// ⚠️ <b>Every one of those rows is VALID</b> — correct action, correctly
    /// recorded, 99.8% exact — so nothing about the dataset looks wrong. Cloning
    /// it teaches a policy to turn hard left and freeze whenever it is unsure, and
    /// it would have surfaced as an inexplicable leftward bias <em>after</em> a
    /// training run. A consumer cannot filter it by value: a genuinely chosen
    /// "hold still" is a sensible action for a sniper at range and is
    /// indistinguishable from the fallback.
    /// </para>
    /// <para>
    /// ⚠️ <b>NaN, not 0, when there is no search behind the slot</b> — the two
    /// mean opposite things here, and 0 is the meaningful value. The same
    /// reasoning as <see cref="DuelSpec.Sampled"/>: NaN is safe as a sentinel
    /// precisely because no real score can collide with it.
    /// </para>
    /// </remarks>
    public double BestScore { get; init; }

    public bool Done => Termination != Termination.Running;
}
