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
    /// <remarks>
    /// The overload for a caller with nothing to do with the tick's events. It
    /// zeroes a <see cref="TickEvents"/> on the stack per call, which is a few
    /// kilobytes — fine for a test and wrong inside a rollout loop, where the
    /// buffer should be hoisted and the four-argument form used.
    /// </remarks>
    public static void Tick(CollisionWorld world, ref WorldState state, ReadOnlySpan<PlayerInput> inputs)
    {
        var events = default(TickEvents);
        Tick(world, ref state, inputs, ref events);
    }

    /// <summary>
    /// Advances the match by one tick, reporting what it did that the state does
    /// not record.
    /// </summary>
    /// <param name="world">Collision geometry. Read-only, and not part of the state.</param>
    /// <param name="state">The match state, advanced in place.</param>
    /// <param name="inputs">
    /// One entry per slot. Slots beyond its length keep the input they had,
    /// which is what a player who sent nothing this tick should get.
    /// </param>
    /// <param name="events">
    /// Cleared of last tick's outputs and filled with this tick's. Also carries
    /// the one input a tick can be <em>told</em> rather than deciding — a forced
    /// weapon-fire seed, which is how a parity replay reproduces a recorded
    /// shot. See <see cref="TickEvents"/>.
    /// </param>
    public static void Tick(
        CollisionWorld world,
        ref WorldState state,
        ReadOnlySpan<PlayerInput> inputs,
        ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        events.ClearOutputs();

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot] && slot < inputs.Length)
                state.Characters[slot].ApplyInput(inputs[slot]);
        }

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            // Firing is part of this character's Begin Step and sits a few lines
            // above the acceleration loop, so it must run inside this pass and
            // immediately before the physics for the same slot — not as a
            // sweep of its own. Weapons.FireStep says why that is exact.
            Weapons.FireStep(ref state, slot, ref events);
            CharacterPhysics.BeginStep(world, ref state.Characters[slot]);
        }

        // Game Maker's alarm stage sits between Begin Step and Step. Note the
        // consequence for a weapon: Begin Step ran *first*, so the shot above
        // was tested against the refire counter last tick left behind, and the
        // decrement that may arm the weapon happens here, after the chance to
        // use it has passed.
        Timers.Advance(ref state);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot])
                CharacterPhysics.Step(world, ref state.Characters[slot]);
        }

        // The engine's automatic-movement pass, which runs between Step and the
        // collision events. Characters are absent from it because their move is
        // folded into Step — see Projectiles.Move.
        Projectiles.Move(ref state);

        // Collision events, which is where all damage happens. After the move,
        // so a hit is decided on where things ended up; before End Step, so a
        // knockback lands in the state End Step then reads.
        Projectiles.Collide(world, ref state);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot])
                CharacterPhysics.EndStep(world, ref state.Characters[slot], ref state.Weapons[slot]);
        }
    }
}
