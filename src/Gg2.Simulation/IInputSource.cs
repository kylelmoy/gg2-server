using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Where one player's input for one tick comes from.
/// </summary>
/// <remarks>
/// docs/ARCHITECTURE.md, "Bots": a network client and a bot are both one of
/// these, and the server asks for a <see cref="PlayerInput"/> per slot per tick
/// without caring which. <b>This is a transcription of a shape the reference
/// already has, not an invention</b> — <c>GameServerBeginStep.gml:79-85</c>
/// calls <c>botInputUpdate(player)</c> in exactly the position, and in the same
/// roster loop, that it calls <c>processClientCommands(player, i)</c> for a
/// socket, and <c>Scripts/Bots/botInputUpdate.gml</c> writes
/// <c>keyState</c>/<c>aimDirection</c> exactly as
/// <c>processClientCommands.gml:267</c> does.
/// <para>
/// <b>The return type is the whole action space, for everyone</b> — four bytes
/// of <c>INPUTSTATE</c>. docs/BOTAI.md item 4: bots get no privileged float
/// aim, no extra keys and no direct state writes, so a logged human action is a
/// valid bot action and a behaviour-cloned policy targets the exact
/// quantisation the game runs on. Everything a player can do that is
/// <em>not</em> a key is a <see cref="PlayerCommand"/> and arrives through
/// <see cref="ICommandSource"/> instead.
/// </para>
/// <para>
/// A source is polled once per slot per tick, before the state update is
/// serialised — so a bot's input for tick <c>n</c> rides in tick <c>n</c>'s
/// update, exactly as an <c>INPUTSTATE</c> that arrived on the socket before
/// the same point does.
/// </para>
/// </remarks>
public interface IInputSource
{
    /// <summary>
    /// This slot's four input bytes for this tick.
    /// </summary>
    /// <param name="world">
    /// The match, read-only. A <c>ref struct</c>, so it cannot be stored — see
    /// <see cref="WorldView"/>.
    /// </param>
    /// <param name="slot">The roster slot being asked. A slot index is a roster index.</param>
    /// <param name="tick">The server's frame counter, for cadenced decisions.</param>
    PlayerInput Poll(in WorldView world, int slot, int tick);
}

/// <summary>
/// A source that would rather decide for all of its slots at once.
/// </summary>
/// <remarks>
/// docs/BOTAI.md item 5, and the whole of its reasoning: "a small MLP is
/// microseconds, but eight separate single-row matrix multiplies waste most of
/// the throughput available from one batched call" — and the same is true of a
/// rollout-search source amortising forked worlds across every bot deciding on
/// the same tick.
/// <para>
/// It extends <see cref="IInputSource"/> rather than standing beside it so
/// every caller keeps a per-slot fallback and nothing has to know which shape a
/// source prefers. <b>The reason it exists now, with nothing implementing it,
/// is that adding it later is a wide change</b>: every call site that assumed
/// per-slot polling has to be rewritten, and the seam is the one thing in this
/// phase that other lanes' files depend on.
/// </para>
/// </remarks>
public interface IBatchInputSource : IInputSource
{
    /// <summary>
    /// Decides for every slot in <paramref name="slots"/> at once.
    /// </summary>
    /// <param name="outputs">
    /// Written one-for-one with <paramref name="slots"/>. At least as long.
    /// </param>
    void PollMany(in WorldView world, ReadOnlySpan<int> slots, Span<PlayerInput> outputs, int tick);
}
