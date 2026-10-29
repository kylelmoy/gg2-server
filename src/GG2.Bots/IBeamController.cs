namespace GG2.Bots;

/// <summary>
/// An <see cref="IMicroController"/> that may be handed a medic while it beams a
/// patient, because it heals and picks patients itself.
/// </summary>
/// <remarks>
/// <b>Optional, like <see cref="IScopeController"/>.</b> Without it,
/// <see cref="Handover.ShouldEngage"/> keeps a beaming medic on the scripted
/// layer, since a controller never trained to heal would drive the body away
/// from its patient. <see cref="HybridBot"/> drops that term only when every
/// eligible tier for the body's class says it owns the beam, so the tier it
/// then chooses is always one that does. gg2-sim's medic layout
/// (<see cref="SimPolicyNetwork.IsMedic"/>) is the one that does, by the user's
/// call on 2026-10-29.
/// </remarks>
public interface IBeamController
{
    /// <summary>Whether this controller heals itself, so a beaming medic may be handed to it.</summary>
    bool OwnsBeam { get; }
}
