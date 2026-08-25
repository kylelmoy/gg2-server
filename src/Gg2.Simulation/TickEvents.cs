namespace Gg2.Simulation;

/// <summary>
/// One weapon firing: what <c>sendEventFireWeapon.gml</c> puts on the wire.
/// </summary>
/// <remarks>
/// The position and speeds are the <b>character's</b> and are taken at the
/// moment of the shot — Begin Step, before this tick's acceleration — because
/// that is when the reference reads them. They are not the muzzle: a shot
/// leaves from <see cref="WeaponState.X"/>, which trails the character by a
/// tick and a rounding.
/// </remarks>
/// <param name="Slot">The roster index that fired.</param>
/// <param name="Seed">
/// The 16-bit seed the shot's spread was drawn from. Every client re-derives
/// the pellets from it, so it is contract rather than a diagnostic — see
/// <see cref="Gg2.Protocol.GmlRandom"/>.
/// </param>
public readonly record struct WeaponFireEvent(
    int Slot,
    ushort Seed,
    double X,
    double Y,
    double HSpeed,
    double VSpeed);

/// <summary>
/// What one tick did that the world state alone does not record.
/// </summary>
/// <remarks>
/// <b>Not part of <see cref="WorldState"/>, on purpose.</b> A weapon firing is
/// an event, not a fact about the world afterwards: the state carries the
/// pellets and the emptied magazine, and nothing in it says a
/// <c>WEAPON_FIRE</c> has to be broadcast. Keeping the two apart is what lets a
/// bot rollout run thousands of speculative ticks without accumulating a
/// message queue it will throw away, and keeps the state a plain <c>memcpy</c>
/// (docs/BOTAI.md item 1).
/// <para>
/// It is a value type with inline storage for the same reason
/// <see cref="WorldState"/> is: the caller owns it, reuses it across ticks, and
/// nothing here allocates.
/// </para>
/// </remarks>
public struct TickEvents
{
    // --- inputs ---------------------------------------------------------------
    //
    // The one thing a tick is *told* rather than deciding: which seed a shot
    // must use. The live server never sets these and draws from
    // WorldState.Random instead.

    /// <summary>
    /// Whether <see cref="ForcedFireSeed"/> holds a seed for this slot's next
    /// shot.
    /// </summary>
    /// <remarks>
    /// <b>The parity harness's injection point, and it has to exist.</b> The
    /// host draws <c>irandom(65535)</c> and broadcasts it
    /// (<c>Scattergun.events/User Event 1.xml</c>), so a recording contains the
    /// seed the reference used — and a replay that drew its own would scatter
    /// its pellets differently and disagree about hp for reasons that have
    /// nothing to do with the code under test. Which seed is drawn is
    /// server-side RNG the port is free about (docs/PARITY.md); what is done
    /// with it is not.
    /// <para>
    /// A separate flag rather than a sentinel value, because every one of the
    /// 65,536 seeds is legal and a defaulted zero is a perfectly good one.
    /// </para>
    /// </remarks>
    public SlotBuffer<bool> HasForcedFireSeed;

    /// <summary>The seed to use for this slot's next shot, where forced.</summary>
    public SlotBuffer<ushort> ForcedFireSeed;

    // --- outputs --------------------------------------------------------------

    /// <summary>Whether this slot fired during the tick just run.</summary>
    /// <remarks>
    /// Indexed by slot rather than appended to a list because a character can
    /// fire at most once per tick — its weapon's <c>readyToShoot</c> is cleared
    /// by the shot and only an alarm puts it back. Walking slots in ascending
    /// order therefore reproduces the order the reference's Begin Steps wrote
    /// the messages in.
    /// </remarks>
    public SlotBuffer<bool> Fired;

    /// <summary>What each slot that fired fired, where <see cref="Fired"/> is set.</summary>
    public SlotBuffer<WeaponFireEvent> Fires;

    /// <summary>Forces the seed a slot's next shot uses.</summary>
    public void ForceFireSeed(int slot, ushort seed)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        HasForcedFireSeed[slot] = true;
        ForcedFireSeed[slot] = seed;
    }

    /// <summary>
    /// Clears the outputs, leaving the forced seeds alone.
    /// </summary>
    /// <remarks>
    /// Called at the top of every tick, so a caller that keeps one of these
    /// across a whole match never sees last tick's shots. The seeds survive
    /// because they are supplied <em>before</em> the tick that consumes them;
    /// each is cleared as it is used.
    /// </remarks>
    public void ClearOutputs()
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            Fired[slot] = false;
    }
}
