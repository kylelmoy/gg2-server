using Gg2.Protocol;

namespace Gg2.Parity;

/// <summary>
/// One compared character's distance from the reference, in units the wire
/// cannot express.
/// </summary>
/// <remarks>
/// The components are <c>port * 5 - observed</c> in wire units — a fifth of a
/// world pixel each — and the observation is an integer the reference produced
/// by rounding its own real position (<see cref="WireQuantise"/>, half away from
/// zero). So a port that reproduced the reference <em>exactly</em> would not
/// report zeroes here: it would report the reference's own discarded fraction,
/// spread across <c>[-0.5, 0.5]</c>.
/// <para>
/// That is the whole point. A residual outside that interval is the only thing
/// a recording can say about a sub-wire-unit error, and how far outside is a
/// lower bound on it.
/// </para>
/// </remarks>
/// <param name="Tick">The trace tick whose update this compared against.</param>
/// <param name="Slot">The roster index.</param>
/// <param name="X">Horizontal position residual, fifths of a pixel.</param>
/// <param name="Y">Vertical position residual, fifths of a pixel.</param>
/// <param name="HSpeed">Horizontal velocity residual, 1/8.5 px/tick.</param>
/// <param name="VSpeed">Vertical velocity residual, 1/8.5 px/tick.</param>
public readonly record struct SubWireSample(
    int Tick,
    int Slot,
    double X,
    double Y,
    double HSpeed,
    double VSpeed)
{
    /// <summary>How far outside the reference's rounding bin, or zero.</summary>
    public double Overshoot => Math.Max(0, Worst - 0.5);

    /// <summary>The largest of the four residuals, by magnitude.</summary>
    public double Worst => Math.Max(
        Math.Max(Math.Abs(X), Math.Abs(Y)),
        Math.Max(Math.Abs(HSpeed), Math.Abs(VSpeed)));

    public override string ToString() =>
        $"tick {Tick} slot {Slot} dx={X:0.######} dy={Y:0.######} " +
        $"dhs={HSpeed:0.######} dvs={VSpeed:0.######}";
}

/// <summary>
/// Measures how far the port is from the reference <em>between</em> wire units.
/// </summary>
/// <remarks>
/// <b>Why the exact-window metric needs this.</b> A closed-loop replay reports
/// the tick on which the port and the reference first disagree about an integer,
/// and that number is a cliff: it says nothing until it says everything. Worse,
/// where the cliff falls is partly luck. A port whose position is a
/// ten-thousandth of a wire unit past a rounding boundary reports a mismatch;
/// the same port a ten-thousandth of a unit the other side reports nothing, for
/// hundreds more ticks. Measured, on
/// <c>reference-ctf_truefort-closedcombat.trace</c>: the closed-loop window ends
/// at tick 1274 with slot 1 at <c>14125.5014</c> wire units against a reference
/// below <c>14125.5</c> — <b>0.0014 wire units, 0.0003 world pixels</b> — and is
/// then clean again for another four hundred ticks.
/// <para>
/// This reports the continuous quantity underneath instead, so an improvement in
/// the physics is visible before it happens to flip an integer, and a regression
/// is visible before it does.
/// </para>
/// <para>
/// <b>A decorator, deliberately.</b> Reimplementing the replay loop to get at
/// the doubles is how two earlier investigations went wrong — the fire seeds
/// live one tick later in the stream than the shots they belong to, and
/// <c>Reconcile</c> clears slots the trace has no character for. So this wraps
/// <see cref="ITraceSimulation"/> and lets the real <see cref="Replayer"/> drive
/// it, and it hooks <see cref="TryGetCharacter"/> because that call <em>is</em>
/// the comparison: the replayer makes it once per compared slot per tick and
/// nowhere else, so a sample is taken exactly when a mismatch could have been
/// reported.
/// </para>
/// <para>
/// It needs the concrete <see cref="PortedSimulation"/> rather than the
/// interface, because the doubles are deliberately not on the seam — everything
/// crossing <see cref="ITraceSimulation"/> is in wire units so that the
/// comparison is about the integers a client can observe. This is the one
/// diagnostic that has to see through that, and
/// <see cref="PortedSimulation.TryGetState"/> is where it looks.
/// </para>
/// </remarks>
public sealed class SubWireProbe(PortedSimulation inner, IReadOnlyList<TraceTick> ticks) : ITraceSimulation
{
    private readonly List<SubWireSample> _samples = [];

    /// <summary>
    /// Ticks already advanced, which is also the index of the tick the replayer
    /// is reconciling: <c>Replayer</c> compares before it advances, because one
    /// update carries this tick's input with last tick's position.
    /// </summary>
    private int _advanced;

    /// <summary>Every comparison the replay made, in order.</summary>
    public IReadOnlyList<SubWireSample> Samples => _samples;

    /// <summary>
    /// The first tick after <paramref name="afterTick"/> on which any slot left
    /// the reference's rounding bin by more than <paramref name="tolerance"/>
    /// wire units, or <see cref="int.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// The tolerance is the claim being made: 0.5 exactly is "inside the bin the
    /// reference rounded from", and anything above it is how much sub-wire error
    /// the port is being allowed. It is not a fudge factor for a wrong answer —
    /// a real divergence grows without bound within a few ticks, and no
    /// tolerance worth writing down hides one.
    /// </remarks>
    public int FirstOutsideBin(double tolerance, int afterTick = int.MinValue)
    {
        var first = int.MaxValue;

        foreach (var sample in _samples)
        {
            if (sample.Tick <= afterTick)
                continue;

            if (sample.Worst > tolerance)
                first = Math.Min(first, sample.Tick);
        }

        return first;
    }

    /// <summary>
    /// The largest overshoot strictly inside <c>(afterTick, beforeTick)</c>, in
    /// wire units.
    /// </summary>
    public double WorstOvershoot(int afterTick, int beforeTick)
    {
        var worst = 0.0;

        foreach (var sample in _samples)
        {
            if (sample.Tick > afterTick && sample.Tick < beforeTick)
                worst = Math.Max(worst, sample.Overshoot);
        }

        return worst;
    }

    public void LoadMap(string map)
    {
        inner.LoadMap(map);

        // A CHANGE_MAP restarts the simulation, so anything measured against the
        // old world is about a different one. The replayer's own tick cursor
        // does not restart, though, so _advanced must not either.
        _samples.Clear();
    }

    public void SetCharacter(int slot, in CharacterBlock observed) => inner.SetCharacter(slot, observed);

    public bool SpawnCharacter(int slot, int spawnpointId, int group) =>
        inner.SpawnCharacter(slot, spawnpointId, group);

    public void ClearCharacter(int slot) => inner.ClearCharacter(slot);

    public void RemoveSlot(int slot) => inner.RemoveSlot(slot);

    public void SetPlayerClass(int slot, PlayerClass playerClass) => inner.SetPlayerClass(slot, playerClass);

    public void SetPlayerTeam(int slot, Team team) => inner.SetPlayerTeam(slot, team);
    public void BuildSentry(int slot, double x, double y, int direction) => inner.BuildSentry(slot, x, y, direction);

    public void SetFireSeed(int slot, ushort seed) => inner.SetFireSeed(slot, seed);

    public IReadOnlyList<DeathRecord> DeathsLastTick => inner.DeathsLastTick;

    public IReadOnlyList<UberRecord> UbersLastTick => inner.UbersLastTick;

    public IReadOnlyList<IntelRecord> IntelLastTick => inner.IntelLastTick;

    public IReadOnlyList<SentryRecord> SentriesLastTick => inner.SentriesLastTick;

    public bool TryGetControlPoint(int index, out ControlPointBlock point)
    {
        return inner.TryGetControlPoint(index, out point);
    }

    public void SetControlPointUnlock(int ticks) => inner.SetControlPointUnlock(ticks);

    public void SetTdmInvulnerability(int ticks) => inner.SetTdmInvulnerability(ticks);

    public void Tick(ReadOnlySpan<PlayerInput> inputs)
    {
        inner.Tick(inputs);
        _advanced++;
    }

    /// <summary>
    /// Answers the replayer, and takes a sample while it is here.
    /// </summary>
    /// <remarks>
    /// Only when the answer is <c>true</c>. A <c>false</c> makes the replayer
    /// anchor the slot from the wire instead of comparing it, and the residual
    /// of a value that has just been assigned measures nothing.
    /// </remarks>
    public bool TryGetCharacter(int slot, out CharacterBlock character)
    {
        if (!inner.TryGetCharacter(slot, out character))
            return false;

        Sample(slot);
        return true;
    }

    private void Sample(int slot)
    {
        if ((uint)_advanced >= (uint)ticks.Count)
            return;

        var tick = ticks[_advanced];

        if ((uint)slot >= (uint)tick.Slots.Length)
            return;

        if (tick.Slots[slot].Observed is not { HasPosition: true } observed)
            return;

        if (!inner.TryGetState(slot, out var state))
            return;

        // The same four expressions CharacterWire hands the wire quantiser,
        // with the rounding left off. A Character's position goes out as plain
        // x*5 — no GML round() first, unlike Intelligence — and its velocity as
        // v*8.5 into a *signed byte*.
        //
        // ⚠️ Which is why a saturated velocity is skipped rather than compared.
        // WireQuantise clamps instead of wrapping, so an observed +/-127 says
        // only "at least 14.94 px/tick" and the fraction it was rounded from is
        // gone. Comparing against it would report a residual of whatever the
        // port's excess happened to be, which measures nothing.
        _samples.Add(new SubWireSample(
            tick.Index,
            slot,
            (state.X * 5) - observed.X,
            (state.Y * 5) - observed.Y,
            Saturated(observed.HSpeed) ? 0 : (state.HSpeed * 8.5) - observed.HSpeed,
            Saturated(observed.VSpeed) ? 0 : (state.VSpeed * 8.5) - observed.VSpeed));
    }

    private static bool Saturated(sbyte velocity) =>
        velocity == sbyte.MaxValue || velocity == sbyte.MinValue;
}
