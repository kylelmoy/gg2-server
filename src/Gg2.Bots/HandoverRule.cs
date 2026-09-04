
namespace Gg2.Bots;

public enum HandoverRule
{
    /// <summary>An enemy within <see cref="HybridBot.EngagementRange"/>.</summary>
    /// <remarks>
    /// ⛔ <b>Measured to cost the objective.</b> The 300px came from a DUEL
    /// measurement, where every tick is a combat tick and there is nothing else
    /// the bot could be doing. In CTF "an enemy is near" means "stop pursuing the
    /// objective", because that is all a combat controller knows to do with the
    /// wheel.
    /// </remarks>
    Distance,

    /// <summary>
    /// The scripted layer would itself stop and fight — its own engage hold.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Intent AND range</b>, reusing the predicate the macro layer already
    /// had rather than inventing a second one. Crucially the hold is
    /// <b>time-bounded</b>: it lapses <c>BotConstants.EngageTicks</c> after the
    /// target was acquired, so control returns and the route resumes. The distance
    /// rule has no timer at all.
    /// <para>
    /// ⚠️ <b>The range half is not redundant.</b> The scripted hold fires out to
    /// <c>BotTargeting.ClassRange</c> — 800px for a soldier, 900 for a sniper — so
    /// intent alone hands the wheel far past where a policy's observation
    /// saturates. See <see cref="Handover.ShouldEngage"/> for what that measured.
    /// </para>
    /// </remarks>
    MacroTask,

    /// <summary>
    /// No handover at all: the scripted layer always drives MOVEMENT, the
    /// controller always drives AIM and FIRE.
    /// </summary>
    /// <remarks>
    /// <b><c>gg2-rl</c>'s arm A, and the argument for it is that a parameter we
    /// keep getting wrong is a parameter worth deleting.</b> Proximity was wrong;
    /// intent was better; intent alone was catastrophic until it was conjoined
    /// with range. A head-level split has no trigger to get wrong, and the two
    /// concerns separate cleanly — the scripted layer never loses its path state
    /// or its objective, and the controller contributes only what it is measurably
    /// good at.
    /// <para>
    /// ⚠️ <b>What would falsify it</b>: the death share sliding back toward the
    /// all-scripted 50.5% would mean the policy's movement was carrying the combat
    /// gain after all.
    /// </para>
    /// </remarks>
    HeadSplit,
}
