using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// An <see cref="IMicroController"/> that works the sniper's scope itself while
/// it drives, in place of the scripted layer's <c>ToggleZoom</c>.
/// </summary>
/// <remarks>
/// <b>Optional, and asked only while the controller holds the wheel.</b>
/// <see cref="HybridBot.PollCommands"/> drops the scripted layer's toggle and
/// sends this one's instead; otherwise every command passes through as before.
/// <para>
/// The scope is a command, not a key (<c>PlayerCommands.ToggleZoom</c>), and the
/// server polls commands BEFORE inputs within a tick. So a controller whose
/// decision sets the scope level has to be able to decide here, early, at a
/// decision boundary, and have its next <see cref="IMicroController.Poll"/>
/// reuse that decision rather than make another.
/// </para>
/// <para>
/// ⚠️ <b>Driving is decided in <c>Poll</c>, after commands</b>, so a handover
/// reaches the scope one tick late: the tick a controller takes the wheel, the
/// scripted layer's toggle still goes out. The handover bubbles have the same
/// lag, and gg2-sim judged it not worth matching.
/// </para>
/// </remarks>
public interface IScopeController
{
    /// <summary>Whether this controller works the scope at all (a zoom net does, others do not).</summary>
    bool OwnsScope { get; }

    /// <summary>
    /// Whether to send <c>ToggleZoom</c> this tick, deciding early if this tick is
    /// a decision boundary.
    /// </summary>
    bool WantsZoomToggle(in WorldView world, int slot, int tick);
}
