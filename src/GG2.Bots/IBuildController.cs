using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// An <see cref="IMicroController"/> that builds the engineer's sentry itself
/// while it drives, in place of the scripted layer's <c>BuildSentry</c>.
/// </summary>
/// <remarks>
/// <b>Optional, and asked only while the controller holds the wheel</b>, the same
/// way and for the same reason as <see cref="IScopeController"/>: building is a
/// command, commands are polled before inputs, so at a decision boundary the
/// controller decides in <see cref="WantsBuild"/> and its next
/// <see cref="IMicroController.Poll"/> reuses that decision. The scripted
/// layer's other commands still pass through, and nothing destroys a sentry.
/// gg2-sim's engineer layout (<see cref="SimPolicyNetwork.IsEngineer"/>) sends
/// its build head on every tick of the decision, refused by the simulation
/// unless a sentry can be built, as gg2-sim's environment sends it.
/// </remarks>
public interface IBuildController
{
    /// <summary>Whether this controller builds the sentry at all.</summary>
    bool OwnsBuild { get; }

    /// <summary>Whether to send <c>BuildSentry</c> this tick, deciding early if this tick is a decision boundary.</summary>
    bool WantsBuild(in WorldView world, int slot, int tick);
}
