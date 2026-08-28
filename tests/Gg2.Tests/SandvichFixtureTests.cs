using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first recording containing a sandvich the port can be held to.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-28: six heavies, three a side on <c>ctf_truefort</c></b>,
/// spawn-anchored by the map-reload recipe, with the eats forced over the bridge
/// (<c>serverEatSandvich</c>, the same script a bot calls) rather than waited for.
/// <para>
/// ⚠️ <b>Forcing them is the whole point, and take one of this recording is why.</b>
/// A heavy's own AI eats when it is hurt and out of the fight, which is late —
/// take one put its eats at t824 and t1531 against an exact window of
/// [t367, t705], so a recording full of sandviches gated <b>none</b> of them.
/// <c>closedheavy</c> has the same shape: three eats at t0, t184 and t1263
/// against a window of [t499, t895]. <b>An event outside the exact window is a
/// message the harness frames and the simulation is never held to.</b> Take two
/// fires the first batch 118 ticks after the anchor.
/// </para>
/// <para>
/// ⚠️ <b>At full health a heavy can eat again immediately.</b> The 45-second
/// cooldown is armed only while <c>hp &lt; maxHp</c>
/// (<c>Character.events/End Step.xml:87-91</c>), so six heavies at 200 hp gave
/// 22 eats in 70 seconds rather than the six the cooldown suggests.
/// </para>
/// </remarks>
public sealed class SandvichFixtureTests
{
    private const string Sandvich = "reference-ctf_truefort-closedsandvich.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Sandvich);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{Sandvich}: {stopped}");
        return (trace, ticks);
    }

    private static (int Exact, int Anchor) ExactWindow(Fields compare)
    {
        var (trace, ticks) = Load();

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Sandvich}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(Sandvich, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        return (firstBad - anchor.Value, anchor.Value);
    }

    /// <summary>
    /// The recording holds eats, and holds them where they can be gated.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Both halves are asserted.</b> A re-recording whose forcing silently
    /// failed would pass every window test below by having nothing to get wrong —
    /// and would pass them with a <em>longer</em> window, which is the direction
    /// nobody looks at.
    /// </remarks>
    [Fact]
    public void TheRecordingHoldsEatsInsideItsExactWindow()
    {
        var (_, ticks) = Load();

        var eats = ticks
            .SelectMany(t => t.Events.Where(m => m.Type == MessageType.Omnomnomnom).Select(_ => t.Index))
            .ToArray();

        Assert.True(eats.Length >= 22, $"only {eats.Length} eats");

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null);

        // The first forced batch: six heavies, 118 ticks after the anchor.
        var inWindow = eats.Count(t => t > anchor!.Value && t < anchor.Value + 687);
        Assert.True(inWindow >= 12, $"only {inWindow} eats inside the trajectory window");
    }

    /// <summary>It is closed-loop, so it carries an exactness claim.</summary>
    [Fact]
    public void EveryCharacterIsSpawnAnchored()
    {
        var (_, ticks) = Load();
        Assert.True(FixtureAnchors.LastExactAnchor(ticks) is not null, $"{Sandvich}: not closed-loop");
    }

    /// <summary>
    /// <b>An eating heavy moves exactly like the reference</b> — which is the
    /// half of the sandvich that is trajectory rather than hp.
    /// </summary>
    /// <remarks>
    /// The eat freezes acceleration and facing for 128 ticks
    /// (<c>Character.events/Begin Step.xml:237-253</c> and
    /// <c>End Step.xml:83-96</c>), so twelve eats inside the window are twelve
    /// characters whose movement the reference and the port have to agree about
    /// while they are frozen.
    /// <para>
    /// ⚠️ <b>687 is longer than any other combat fixture on this map</b>
    /// (<c>closedheavy</c> 396, <c>closedsentrykill</c> 595) and the reason is
    /// that the heavies spend a fifth of the window standing still eating rather
    /// than firing an unseeded Minigun at each other.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEatingHeaviesTrajectoriesAreExactFromTheSpawnAnchor()
    {
        var (exact, anchor) = ExactWindow(Fields.Trajectory);

        Assert.True(
            exact >= 650,
            $"{Sandvich}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }

    /// <summary>
    /// ⚠️ <b>OPEN FINDING: the overshoot comes back down one comparison tick
    /// later in the port than in the reference.</b>
    /// </summary>
    /// <remarks>
    /// A heavy who eats at full health sits at <b>201.6</b> for the whole eat —
    /// the clamp is above the heal in the same event, so it is undone every tick
    /// — and drops to 200 on the tick after the eat ends. Both sides agree on all
    /// of that <em>except the tick it lands on</em>:
    /// <list type="bullet">
    /// <item>The recording says <b>200 at t511</b>, which the harness compares
    /// against the port's state at the end of t510.</item>
    /// <item>The port is still at 201.6 there and clamps during t511.</item>
    /// </list>
    /// It shows up only when the return lands exactly on a position-bearing tick,
    /// which both forced batches did (t511 and t952) — so it is systematic, not a
    /// sampling artefact.
    /// <para>
    /// ✅ <b>Three explanations have been eliminated.</b> (1) The port's eat
    /// length is right: 128 increments of 0.25 to 32, with the heal before the
    /// end check, transcribed from <c>End Step.xml</c> and confirmed frame by
    /// frame against the live game — the reference's last eat tick heals and then
    /// clears the flag, exactly as <c>SandvichTests</c> asserts. (2) It is not
    /// <c>delta_factor</c>: the live watch read 1 on every frame of an eat. (3)
    /// It is <b>not</b> the injection landing a tick late — applying the eat from
    /// the next tick's events, the way fire seeds and spawns are, collapses the
    /// trajectory window from 687 to 120 with x and hspeed wrong by 8 and 4 wire
    /// units at the first eat, because the acceleration freeze then starts a tick
    /// early. <b>The freeze demonstrably starts on the message's own tick; the hp
    /// demonstrably ends a tick before the port's.</b>
    /// </para>
    /// <para>
    /// What is left is the comparison convention itself
    /// (<c>Replayer.UpdatesPrecedeTheirTick</c>) or something in the tick that
    /// clamps hp earlier than this port does. Pinned at the measured 246 so that
    /// closing it fails here and is noticed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHpWindowIsCappedByTheOvershootReturningOneTickLate()
    {
        var (exact, anchor) = ExactWindow(Fields.Trajectory | Fields.Hp);

        Assert.Equal(246, exact);
        Assert.Equal(265, anchor);
    }
}
