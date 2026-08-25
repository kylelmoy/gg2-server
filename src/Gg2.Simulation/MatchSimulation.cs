using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// One tick of the match, as a pure function.
/// </summary>
/// <remarks>
/// docs/ARCHITECTURE.md's <c>Tick(WorldState, ReadOnlySpan&lt;PlayerInput&gt;)</c>,
/// and the rule the rest of the project hangs off: no sockets, no clock, no
/// <c>async</c>, no allocation on the steady-state path. Everything the tick
/// needs is in the state, the inputs and the read-only world handed to it;
/// everything it produces is in the state.
/// <para>
/// This is deliberately the <em>only</em> place the phase ordering below is
/// written down. The parity replayer and the live server both call it, so a
/// recording proves the same code the server runs — which is the whole point of
/// docs/PARITY.md and stops being true the moment there are two copies.
/// </para>
/// </remarks>
public static class MatchSimulation
{
    /// <summary>
    /// Advances every living character by one tick at a fixed 30 Hz.
    /// </summary>
    /// <remarks>
    /// <b>The phase split across characters is observable and is not an
    /// implementation detail.</b> Game Maker runs an event for every instance
    /// before moving to the next event, so every character finishes Begin Step
    /// before any starts Step. Running one character all the way through and
    /// then the next changes what each sees of the others and is a different
    /// simulation — see <see cref="CharacterPhysics"/>.
    /// <para>
    /// Slots are walked in ascending order within each phase, which is the
    /// reference's instance order: a character's instance is created when its
    /// player spawns, and the roster is positional.
    /// </para>
    /// </remarks>
    /// <param name="world">Collision geometry. Read-only, and not part of the state.</param>
    /// <param name="state">The match state, advanced in place.</param>
    /// <param name="inputs">
    /// One entry per slot. Slots beyond its length keep the input they had,
    /// which is what a player who sent nothing this tick should get.
    /// </param>
    public static void Tick(CollisionWorld world, ref WorldState state, ReadOnlySpan<PlayerInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(world);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot] && slot < inputs.Length)
                state.Characters[slot].ApplyInput(inputs[slot]);
        }

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot])
                CharacterPhysics.BeginStep(world, ref state.Characters[slot]);
        }

        // Game Maker's alarm stage sits between Begin Step and Step, and it is
        // where a weapon becomes able to fire again — so an input arriving this
        // tick is tested against a refire counter that has already advanced.
        Timers.Advance(ref state);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot])
                CharacterPhysics.Step(world, ref state.Characters[slot]);
        }

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot])
                CharacterPhysics.EndStep(world, ref state.Characters[slot]);
        }
    }
}
