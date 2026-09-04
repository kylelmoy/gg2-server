using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first Dual King of the Hill recording — the last gamemode with a shipped
/// map and no fixture.
/// </summary>
/// <remarks>
/// Three soldiers a side on <c>dkoth_atalia</c>, spawn-anchored by the
/// map-reload recipe, with the teams forced <em>after</em> the reload.
/// <para>
/// DKotH's rules are the ones most easily got backwards: the clocks are
/// <b>crossed</b> — <c>redTimer</c> runs down while <em>blue's</em> hill is
/// red's, so a team wins by holding the enemy's hill — both hills start owned
/// by their own team, and taking one locks your own. <c>DualKothTests</c> gates
/// these at the unit level; this fixture gates them end to end.
/// </para>
/// <para>
/// <b>It frames to the last byte with two control points.</b> The
/// control-point block carries <b>no count prefix</b>, so a reader rebuilds the
/// points from the map in the same order or every update after the first is
/// misframed; <c>koth_valley</c> proved that for one point, and this is the
/// first two-point <em>King of the Hill</em> map to be read back.
/// </para>
/// </remarks>
public sealed class DualKothFixtureTests
{
    private const string Fixture = "reference-dkoth_atalia-closeddkoth.trace";

    /// <summary>The KotH recording, as the control for the message below.</summary>
    private const string Koth = "reference-koth_valley-closedkoth.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{fixture}: {stopped}");
        return (trace, ticks);
    }

    private static int CountMessages(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Sum(t => t.Events.Count(m => m.Type == type));

    /// <summary>It is Dual King of the Hill, with two hills, and it frames.</summary>
    [Fact]
    public void ItIsDualKingOfTheHillWithTwoPointsAndFramesCleanly()
    {
        var (trace, ticks) = Load(Fixture);
        var shape = GameModes.ShapeOf(MapLoader("dkoth_atalia"));

        Assert.Equal("dkoth_atalia", trace.Header.Map);
        Assert.Equal(GameMode.DualKingOfTheHill, shape.GameMode);

        // The number the framing depends on, and the reader takes it from the
        // map rather than from the wire.
        Assert.Equal(2, shape.ControlPointCount);

        Assert.True(ticks.Count > 4000, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// <b>The gate this recording exists for: DKotH never sends
    /// <c>UNLOCKCP</c>, and KotH always does.</b>
    /// </summary>
    /// <remarks>
    /// It is the one message that tells the two modes apart on the wire
    /// (docs/SIMULATION.md, "Settled: Dual King of the Hill"): a KotH hill is
    /// unlocked by <c>KothHUD</c> calling <c>doEventUnlockCP</c>, and DKotH's
    /// hills unlock themselves in their own Step, which runs before the HUD's, so
    /// nothing is broadcast.
    /// <para>
    /// The KotH fixture is the control, and it is what makes this assertion mean
    /// something: without it, "no <c>UNLOCKCP</c>" is equally well explained by a
    /// recorder that never receives one, or a reader that cannot parse one.
    /// </para>
    /// </remarks>
    [Fact]
    public void DualKingOfTheHillNeverSendsUnlockCpAndKingOfTheHillDoes()
    {
        var (_, dual) = Load(Fixture);
        Assert.Equal(0, CountMessages(dual, MessageType.UnlockCp));

        var (_, koth) = Load(Koth);
        Assert.True(CountMessages(koth, MessageType.UnlockCp) >= 1, "the control has no UNLOCKCP either");
    }

    /// <summary>
    /// <b>Both hills start owned by their own team</b>, which is
    /// <c>DKothHUD</c>'s Create event rather than <c>User Event 0</c>.
    /// </summary>
    /// <remarks>
    /// Every other mode starts its points neutral or locked-and-neutral, so this
    /// is the one place a port could reasonably have got it wrong and no other
    /// recording could say. The hills are also the thing the crossed clocks are
    /// measured against — you win by taking the <em>other</em> one — so a port
    /// that started them the other way round would run both timers backwards.
    /// </remarks>
    [Fact]
    public void BothHillsStartOwnedByTheirOwnTeam()
    {
        var (_, ticks) = Load(Fixture);

        var first = ticks.First(t => t.ControlPoints.Length == 2);
        var teams = first.ControlPoints.Select(p => p.Team).ToArray();

        // One of each, rather than both neutral or both one team.
        Assert.Contains((sbyte)Team.Red, teams);
        Assert.Contains((sbyte)Team.Blue, teams);
    }

    /// <summary>
    /// The control on the framing: two is the only point count that reads this
    /// recording.
    /// </summary>
    /// <remarks>
    /// <b>Without this, "it framed" is equally consistent with a reader that
    /// never looked at the control-point block.</b> The block has no count prefix
    /// and no length, so the only thing standing between a correct reader and a
    /// desynchronised one is the number it took from the map — and a wrong
    /// <c>ControlPointsFor</c> fails a hill fixture's framing and <em>no other
    /// fixture at all</em>. The same argument as
    /// <c>GeneratorFixtureTests.TheGeneratorBlockDoesNotShortenWhenAGeneratorDies</c>.
    /// </remarks>
    [Fact]
    public void NoOtherPointCountCanFrameThisRecording()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var shape = GameModes.ShapeOf(MapLoader("dkoth_atalia"));

        TraceParser.Parse(trace, shape, out _, out var atTwo);
        Assert.Null(atTwo);

        TraceParser.Parse(trace, shape with { ControlPointCount = 1 }, out _, out var atOne);
        Assert.NotNull(atOne);

        TraceParser.Parse(trace, shape with { ControlPointCount = 3 }, out _, out var atThree);
        Assert.NotNull(atThree);
    }

    /// <summary>It is a real match, with the mode's own traffic in it.</summary>
    /// <remarks>
    /// The <c>CAPS_UPDATE</c> count is what separates a hill recording from a
    /// capture-the-flag one: a CTF fixture holds one or two, and this holds
    /// seventeen, because two meters move.
    /// </remarks>
    [Fact]
    public void TheRecordingHoldsAFightAndBothMetersMove()
    {
        var (_, ticks) = Load(Fixture);

        Assert.True(CountMessages(ticks, MessageType.CapsUpdate) >= 10, "not enough caps updates");
        Assert.True(CountMessages(ticks, MessageType.WeaponFire) >= 100, "not enough fire");
        Assert.True(CountMessages(ticks, MessageType.PlayerDeath) >= 10, "not enough deaths");

        // Both points are observed, repeatedly — the framing above says the
        // reader can find them, and this says the recording carries them.
        Assert.True(
            ticks.Count(t => t.ControlPoints.Length == 2) >= 10,
            "the two hills are barely observed");
    }
}
