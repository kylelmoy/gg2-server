
namespace Gg2.Env;

/// <summary>
/// Who supplies one agent's actions.
/// </summary>
/// <remarks>
/// ⚠️ <b>Requested by <c>gg2-rl</c> 2026-08-28, and it blocks three of its
/// phases.</b> Until this existed <c>MakeBatch</c> could only drive both slots
/// from the client, so a duel between two things that both live in this repo —
/// <c>RolloutSearch</c> against <c>ScriptedBot</c> — could not be asked for over
/// the socket at all. Phase 1's exit criterion is 500 duels per matchup, phase 2
/// collects against fixed opponents, and phase 3's opponent pool is "past
/// checkpoints, the scripted bot, and a noise bot".
/// <para>
/// ⚠️ <b>A past checkpoint is <see cref="Client"/>.</b> The trainer already has
/// the weights and can drive that slot itself; what it cannot do from outside is
/// run the bots, which need the <c>WorldView</c> and the nav graph. So this enum
/// is deliberately short and is not a plug-in seam.
/// </para>
/// <para>
/// ⚠️ <b>The bot is polled AS IT IS, not re-encoded.</b> Each bot drives its slot
/// through its own <c>IInputSource.Poll</c>, on its own decision cadence, so the
/// opponent really is that bot rather than an imitation of it squeezed through
/// the action space. What comes back in <see cref="StepResult.Action"/> is the
/// nearest action to what it did — exact for everything except the aim head on a
/// bot that turns faster than the cap. See <see cref="StepResult.Action"/>.
/// </para>
/// </remarks>
public enum EnvDriver : byte
{
    /// <summary>The action arrives over the socket. The default.</summary>
    Client = 0,

    /// <summary>
    /// Presses nothing and never turns — the comparison floor.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not dead weight and not a placeholder.</b> A win rate against an
    /// idle opponent is what says whether a policy has learned to aim and fire at
    /// all, and it is the only opponent whose behaviour is certain. The same
    /// argument <c>RolloutOpponent.Idle</c> is kept for.
    /// </remarks>
    Idle = 1,

    /// <summary>
    /// <c>ScriptedBot</c> at a skill tier.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Needs a nav graph.</b> Without one its follower is null and it
    /// fights from wherever it spawned, which is a different and much weaker
    /// opponent — so a spec asking for it without a nav directory is refused
    /// rather than quietly downgraded.
    /// </remarks>
    Scripted = 2,

    /// <summary>
    /// <c>RolloutSearch</c>, the forward-search bot.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It costs about 2.94 ms a decision</b> (Release, one searcher on
    /// <c>gg_debug</c>) against a scripted bot's microseconds, so a batch driven
    /// by it is roughly three orders of magnitude slower per world. That is the
    /// point of it — it is an evaluation baseline, not a data-collection
    /// opponent — but it is worth knowing before making a batch of 64.
    /// </remarks>
    Rollout = 3,
}
