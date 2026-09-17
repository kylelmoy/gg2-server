using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// A 64-bit summary of a whole match state, for asking whether two simulations
/// are in the same place.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers is "are these two worlds the same", and the
/// only useful answer is exact.</b> Two nodes running the same inputs either
/// agree on every bit or one of them has a bug — that is the whole premise of
/// running a simulation in more than one place — so this hashes the doubles
/// bit-for-bit rather than to a tolerance.
/// </para>
/// <para>
/// ⚠️ <b>It covers what a divergence would show up in first, not everything the
/// state holds.</b> Positions, velocities, health, the scoreboard, the round
/// clock and the RNG stream: a physics disagreement reaches a position within a
/// tick or two, and a disagreement about the RNG reaches every seeded weapon.
/// A field that is NOT in here can still differ — so a match of digests is
/// strong evidence of agreement and not a proof of it, and anything that starts
/// depending on a field should add it.
/// </para>
/// <para>
/// ⛔ <b>The RNG is in here deliberately.</b> Two worlds standing in identical
/// places with different random streams will disagree on the next shot that
/// draws, and that failure would otherwise appear ticks later, somewhere else,
/// looking like a physics bug.
/// </para>
/// <para>
/// FNV-1a, for the reason <c>HeadlessMatch.Digest</c> gives: it has to be
/// stable across runs and machines, not cryptographic. ⚠️ Stable across
/// machines <em>of one operating system</em> — docs/SIMULATION.md, "The same
/// build on two machines".
/// </para>
/// </remarks>
public static class StateDigest
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Digests a whole world.</summary>
    public static ulong Of(in WorldState state)
    {
        var hash = Offset;

        hash = Mix(hash, (ulong)(long)state.RedCaps);
        hash = Mix(hash, (ulong)(long)state.BlueCaps);
        hash = Mix(hash, (ulong)(long)state.RoundTimer);
        hash = Mix(hash, (ulong)(long)state.SetupTimer);
        hash = Mix(hash, state.RoundDecided ? 1UL : 0UL);
        hash = Mix(hash, (ulong)(long)state.Winner);

        // The stream's position, not a draw from it: asking the generator for a
        // number here would advance it and change the thing being measured.
        hash = Mix(hash, state.Random.Seed);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            hash = Mix(hash, state.HasPlayer[slot] ? 1UL : 0UL);

            if (!state.HasPlayer[slot])
                continue;

            hash = Mix(hash, (ulong)(long)state.Players[slot].Team);
            hash = Mix(hash, (ulong)(long)state.Players[slot].Class);
            hash = Mix(hash, state.Alive[slot] ? 1UL : 0UL);

            if (!state.Alive[slot])
                continue;

            ref readonly var character = ref state.Characters[slot];

            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(character.X));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(character.Y));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(character.HSpeed));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(character.VSpeed));
            hash = Mix(hash, (ulong)(long)character.Hp);
            hash = Mix(hash, (ulong)(long)character.KeyState);
        }

        // Projectiles in their pool order, which is part of the state: two
        // worlds holding the same rockets in different slots would disagree on
        // the next spawn.
        for (var index = 0; index < WorldState.MaxProjectiles; index++)
        {
            ref readonly var projectile = ref state.Projectiles[index];

            hash = Mix(hash, (ulong)(long)projectile.Kind);

            if (projectile.Kind == ProjectileKind.None)
                continue;

            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(projectile.X));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(projectile.Y));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(projectile.Motion.HSpeed));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(projectile.Motion.VSpeed));
            hash = Mix(hash, projectile.OwnerSlot);
        }

        return hash;
    }

    /// <summary>Mixes one 64-bit value in, low byte first.</summary>
    public static ulong Mix(ulong hash, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= Prime;
        }

        return hash;
    }
}
