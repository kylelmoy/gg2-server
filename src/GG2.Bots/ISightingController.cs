using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// An <see cref="IMicroController"/> that keeps a memory of what its body has
/// seen, learned on every tick whether or not it drives.
/// </summary>
/// <remarks>
/// <b>Optional, like <see cref="IScopeController"/>.</b> <see cref="HybridBot.Poll"/>
/// calls <see cref="Sight"/> on every tier that implements it, at the top of the
/// poll, while the scripted layer holds the wheel; a tier that drives learns from
/// its own observations. A gg2-sim base layout v2 net is the one that needs it
/// (<see cref="SimPolicyNetwork.IsV2"/>): its environment observes every row on
/// every step, so the sightings a handover inherits are learned while the body
/// was scripted too — gg2-sim's <c>ref/Matches.cs</c> does the same at the same
/// point in the tick.
/// </remarks>
public interface ISightingController
{
    /// <summary>Learn what the body in <paramref name="slot"/> can see at <paramref name="tick"/>, deciding nothing.</summary>
    void Sight(in WorldView world, int slot, int tick);
}
