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
    private PlayerInput _latest;
    private PlayerKeys _pressed;
    private PlayerKeys _released;

    /// <summary>
    /// The most recent input this player sent.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Assigning it is how an <c>INPUTSTATE</c> is delivered, and the
    /// setter does more than store it</b> — it also accumulates the key edges
    /// this input creates against the one before it. Read
    /// <see cref="TakeEdges"/> before believing that only the last input of a
    /// tick matters.
    /// </remarks>
    public PlayerInput Latest
    {
        get => _latest;
        set
        {
            // `event_user(1)` per INPUTSTATE, exactly as
            // processClientCommands.gml:315 runs it inside the `with(player.object)`
            // — `pressedKeys |= keyState & ~lastKeyState; releasedKeys |=
            // ~keyState & lastKeyState; lastKeyState = keyState`. Here the
            // "last" is the previous input rather than a character field,
            // because this runs whether or not the player has a character.
            _pressed |= value.Keys & ~_latest.Keys;
            _released |= ~value.Keys & _latest.Keys;
            _latest = value;
        }
    }

    public PlayerInput Poll(in WorldView world, int slot, int tick) => _latest;

    /// <summary>
    /// The key edges every <c>INPUTSTATE</c> since the last call created,
    /// clearing them.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This is the whole reason a socket's input source is not just a
    /// "last value wins" box, and skipping it silently drops input.</b> More than
    /// one <c>INPUTSTATE</c> reaches the server in one tick whenever a client's
    /// packets coalesce — a delayed one arriving beside the next, which is
    /// ordinary over the internet — and up to ten are read per player per tick
    /// (<c>ClientCommands.PerTickLimit</c>). A key pressed and released across
    /// two of them is not in the last one's <c>keyState</c> at all, so a server
    /// that only looked at <see cref="Latest"/> would never see the press: a
    /// spy's tapped stab, a tapped jump and a tapped airblast simply would not
    /// happen. The reference does see them, because its <c>keyState</c> write
    /// runs <c>event_user(1)</c> per input rather than per tick.
    /// <para>
    /// The pressed mask is a superset of what
    /// <see cref="Gg2.Simulation.CharacterState.ApplyInput"/> derives from
    /// <see cref="Latest"/> alone, so the caller ORs both and nothing is counted
    /// twice.
    /// </para>
    /// <para>
    /// docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried into the port".
    /// </para>
    /// </remarks>
    public (PlayerKeys Pressed, PlayerKeys Released) TakeEdges()
    {
        var edges = (_pressed, _released);
        _pressed = PlayerKeys.None;
        _released = PlayerKeys.None;
        return edges;
    }
}
