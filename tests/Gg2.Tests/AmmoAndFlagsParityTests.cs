using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The two wire fields no test had ever compared: a character's <b>ammo</b> byte
/// and its <b>flags</b> byte.
/// </summary>
/// <remarks>
/// ⚠️ <b>Found by census, not by suspicion.</b> After <c>Fields.Ubers</c> turned
/// out to be used by no test at all, the obvious follow-up was to count which
/// members of <see cref="Fields"/> any test ever passes to a <c>Replayer</c>.
/// <c>Trajectory</c>, <c>Hp</c>, <c>Deaths</c>, <c>Ubers</c>, <c>Intel</c>,
/// <c>ControlPoints</c>, <c>Sentries</c> and <c>Position</c> all appear.
/// <b><c>Ammo</c> and <c>Flags</c> appeared nowhere</b> — every phase of this
/// project has serialised them and nothing has ever held the port to either.
/// <para>
/// They are not decorative. <b>Ammo</b> is the whole of the port's magazine
/// accounting on the wire — refire, reload, the pyro's tank resting at 201.8 and
/// the heavy's at 201 — and <b>flags</b> carries the cloak bit and
/// <c>moveStatus</c>, so a rocket jump and a cloaked spy are visible in it and
/// nowhere else.
/// </para>
/// <para>
/// ⚠️ Each gate has a <b>liveness control</b> beside it, for the reason the last
/// audit established: <c>Assert(mismatches.Length == 0)</c> is satisfied just as
/// well by a comparison that never runs. docs/PARITY.md, "every comparison family
/// is now pinned to its tick AND proved alive".
/// </para>
/// </remarks>
public sealed class AmmoAndFlagsParityTests
{
    /// <summary>Scout, soldier and demoman — every weapon in it broadcasts a seed.</summary>
    private const string Combat = "reference-ctf_truefort-closedcombat.trace";

    /// <summary>Six spies, and the only recording that cloaks.</summary>
    private const string Spy = "reference-ctf_truefort-closedspy.trace";

    /// <summary>The heavy, whose Minigun broadcasts nothing.</summary>
    private const string Heavy = "reference-ctf_truefort-closedheavy.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks, int Anchor) Load(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{fixture}: {stopped}");

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{fixture}: not closed-loop");

        return (trace, ticks, anchor!.Value);
    }

    private static Mismatch[] Replay(
        string fixture, Trace trace, IReadOnlyList<TraceTick> ticks, Fields compare, int anchor) =>
        new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false)
            .Mismatches
            .Where(m => m.Tick > anchor)
            .ToArray();

    /// <summary>
    /// Every observed character block with its ammo and flags put beyond anything
    /// the port could produce, so a live comparison must object.
    /// </summary>
    private static IReadOnlyList<TraceTick> Doctored(IReadOnlyList<TraceTick> ticks) =>
        ticks
            .Select(t => new TraceTick
            {
                Index = t.Index,
                Tier = t.Tier,
                ControlPoints = t.ControlPoints,
                ControlPointUnlock = t.ControlPointUnlock,
                Events = t.Events,
                Slots = t.Slots
                    .Select(slot => slot.Observed is { } o
                        ? slot with { Observed = o with { Ammo = (byte)(o.Ammo ^ 0xFF), Flags = (byte)(o.Flags ^ 0xFF) } }
                        : slot)
                    .ToArray(),
            })
            .ToList();

    /// <summary>
    /// ✅ <b>The port's ammo matches the reference for 1,325 ticks</b> on the
    /// sharpest combat fixture.
    /// </summary>
    /// <remarks>
    /// <c>closedcombat</c> is scout, soldier and demoman — <b>every weapon in it
    /// broadcasts a seed</b>, so unlike an unseeded one there is no excuse
    /// available for a divergence. 1,325 ticks is the same span over which its hp
    /// agrees, and it covers six scattergun blasts, rockets and mines: refire,
    /// magazine and reload accounting, all of it on the wire as one byte a
    /// character.
    /// </remarks>
    [Fact]
    public void ThePortsAmmoMatchesTheReferenceThroughAFight()
    {
        var (trace, ticks, anchor) = Load(Combat);

        var mismatches = Replay(Combat, trace, ticks, Fields.Ammo, anchor);

        Assert.True(
            mismatches.Length == 0,
            $"{Combat}: ammo diverges — "
                + string.Join(
                    "; ",
                    mismatches.Take(4).Select(m => $"t{m.Tick} slot {m.Slot} exp {m.Expected} got {m.Actual}")));

        // The window really is the whole recording after the anchor: 1,325 ticks.
        Assert.True(ticks[^1].Index - anchor > 1300, "the fixture is shorter than it was measured to be");

        // ⚠️ Liveness: a comparison that never runs reports nothing either.
        Assert.True(
            Replay(Combat, trace, Doctored(ticks), Fields.Ammo, anchor).Length > 0,
            "the ammo comparison reported nothing against doctored blocks — it is not running");
    }

    /// <summary>
    /// ✅ <b>The port's flags byte matches for 1,829 ticks on the spy
    /// recording</b> — cloak bit included.
    /// </summary>
    /// <remarks>
    /// The flags byte is bit 0 <c>cloaked</c> and bits 1-3 <c>moveStatus</c>
    /// (docs/PROTOCOL.md). <c>closedspy</c> is the only recording in which anybody
    /// cloaks, and the cloak was never read back from a real server before that
    /// fixture existed — so this is the first time the port has been held to it.
    /// </remarks>
    [Fact]
    public void ThePortsFlagsMatchTheReferenceIncludingTheCloak()
    {
        var (trace, ticks, anchor) = Load(Spy);

        var mismatches = Replay(Spy, trace, ticks, Fields.Flags, anchor);

        Assert.True(
            mismatches.Length == 0,
            $"{Spy}: flags diverge — "
                + string.Join(
                    "; ",
                    mismatches.Take(4).Select(m => $"t{m.Tick} slot {m.Slot} exp {m.Expected} got {m.Actual}")));

        // Somebody really does cloak in it, so bit 0 is exercised rather than
        // being zero on both sides for the whole recording.
        Assert.Contains(
            ticks,
            t => t.Slots.Any(s => s.Observed is { Cloaked: true }));

        // ⚠️ Liveness.
        Assert.True(
            Replay(Spy, trace, Doctored(ticks), Fields.Flags, anchor).Length > 0,
            "the flags comparison reported nothing against doctored blocks — it is not running");
    }

    /// <summary>
    /// ⚠️ <b>An unseeded weapon caps an AMMO window too, and not the flags one
    /// beside it.</b>
    /// </summary>
    /// <remarks>
    /// The rule was established for hp and then for trajectory (docs/TRAPS-SIMULATION.md, "an
    /// unseeded weapon caps a fixture's hp window"); this is the third field it
    /// applies to, and the pair makes the mechanism plain rather than assumed.
    /// <c>closedheavy</c>'s Minigun calls <c>randomize()</c> and broadcasts no
    /// seed, so where its pellets go — and therefore who shoots, and how much
    /// ammo they spend — cannot be reproduced. Measured: <b>ammo 760 ticks,
    /// flags 1,744</b>, on the same recording from the same anchor.
    /// <para>
    /// Asserted rather than commented because it is the cheapest available
    /// demonstration that the ammo comparison is sensitive to something real. If
    /// the two ever converge, either the Minigun became reproducible — which it
    /// cannot — or the ammo comparison stopped working.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHeavysAmmoWindowIsMuchShorterThanItsFlagsWindow()
    {
        var (trace, ticks, anchor) = Load(Heavy);

        var ammo = Replay(Heavy, trace, ticks, Fields.Ammo, anchor);
        var flags = Replay(Heavy, trace, ticks, Fields.Flags, anchor);

        var ammoWindow = (ammo.Length == 0 ? ticks[^1].Index : ammo.Min(m => m.Tick)) - anchor;
        var flagsWindow = (flags.Length == 0 ? ticks[^1].Index : flags.Min(m => m.Tick)) - anchor;

        // 760 and 1,744 today.
        Assert.True(ammoWindow < 1000, $"the heavy's ammo window is {ammoWindow}, longer than measured");
        Assert.True(flagsWindow > 1500, $"the heavy's flags window is only {flagsWindow}");
    }
}
