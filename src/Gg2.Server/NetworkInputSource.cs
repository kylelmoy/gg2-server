using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// The input source behind a socket: whatever <c>INPUTSTATE</c> arrived most
/// recently.
/// </summary>
/// <remarks>
/// This is what makes docs/ARCHITECTURE.md's claim — "a network client and a
/// bot are both an <see cref="IInputSource"/>" — literally true rather than
/// aspirational, and it is why the server has one input-gathering path instead
/// of a branch on <c>isBot</c>.
/// <para>
/// <b>It ignores the world and the tick, and holding the last input is the
/// point.</b> <c>processClientCommands.gml:267</c> writes <c>keyState</c> when
/// the packet arrives and nothing clears it, so a player whose packet was lost
/// or delayed keeps pressing what they last pressed — the client dead-reckons
/// on the same assumption. <see cref="MatchSimulation.Tick"/> says the same
/// thing from the other side: a slot past the end of the input span keeps the
/// input it had. Zeroing here would make every dropped packet a stutter.
/// </para>
/// <para>
/// Slot 0 — the reference's local player, which a headless server never
/// simulates — gets one of these with nothing ever written to it, so it presses
/// nothing for ever.
/// </para>
/// </remarks>
public sealed class NetworkInputSource : IInputSource
{
    /// <summary>The most recent input this player sent.</summary>
    public PlayerInput Latest { get; set; }

    public PlayerInput Poll(in WorldView world, int slot, int tick) => Latest;
}
