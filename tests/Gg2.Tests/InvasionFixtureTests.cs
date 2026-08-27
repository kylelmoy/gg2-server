using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first Invasion recording — and the correction of a claim this project
/// made one day earlier.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: three soldiers a side on <c>ctf_avanti</c></b>,
/// spawn-anchored by the map-reload recipe, teams forced after the reload.
/// <para>
/// ⚠️ <b>"Invasion has no shipped map" was WRONG, and the reason is worth more
/// than the fixture.</b> It was concluded by looking for an <c>inv_*</c> map and
/// finding none. Invasion is not a map-name prefix at all: it is <b>a Capture
/// the Flag map that places a <c>ControlPointSetupGate</c></b>
/// (<c>GameModes.HasSetupGate</c>, from <c>ControlPointHUD.events/Create.xml</c>),
/// and the only shipped map that does is called <c>ctf_avanti</c>.
/// <b>Classify a map by asking the code, never by reading its name.</b>
/// </para>
/// <para>
/// ⚠️ <b>Invasion is not Capture the Flag with gates</b> (docs/SIMULATION.md):
/// red wins on the cap limit, <b>blue wins by surviving the clock</b>, and its
/// <c>redIsCapping</c> is "carried, or dropped with a live return timer" rather
/// than "away from base". The port ran it through CTF's round rules until phase
/// 6, and <c>RoundTests</c> has been its whole gate ever since.
/// </para>
/// </remarks>
public sealed class InvasionFixtureTests
{
    private const string Fixture = "reference-ctf_avanti-closedinvasion.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");
        return (trace, ticks);
    }

    private static int CountMessages(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Sum(t => t.Events.Count(m => m.Type == type));

    /// <summary>
    /// ⚠️ <b>A map called <c>ctf_avanti</c> plays Invasion</b>, and it frames.
    /// </summary>
    [Fact]
    public void AMapCalledCtfPlaysInvasionAndItFramesCleanly()
    {
        var (trace, ticks) = Load();
        var map = MapLoader("ctf_avanti");

        Assert.Equal("ctf_avanti", trace.Header.Map);
        Assert.Equal(GameMode.Invasion, GameModes.ShapeOf(map).GameMode);

        // The entity that decides it, and nothing else does.
        Assert.True(GameModes.HasSetupGate(map));

        Assert.True(ticks.Count > 4000, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// ⚠️ <b>Exactly one shipped map plays Invasion, and it is not named for
    /// it.</b>
    /// </summary>
    /// <remarks>
    /// Asserted across all 22 maps rather than commented, because the claim that
    /// none did was made and believed for a day. This is also the control for the
    /// test above: <c>ctf_truefort</c> and the other seven <c>ctf_</c> maps place no
    /// setup gate and are Capture the Flag, so the mode really does come from the
    /// entity and not from the prefix.
    /// </remarks>
    [Fact]
    public void ExactlyOneShippedMapPlaysInvasion()
    {
        var invasion = new List<string>();
        var captureTheFlag = 0;

        foreach (var path in Directory.GetFiles(ReferenceTree.IncludedFiles, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path)!;
            var mode = GameModes.ShapeOf(MapLoader(name)).GameMode;

            if (mode == GameMode.Invasion)
                invasion.Add(name);
            else if (mode == GameMode.CaptureTheFlag)
                captureTheFlag++;
        }

        Assert.Equal(["ctf_avanti"], invasion);

        // The eight other ctf_ maps, which are what they look like.
        Assert.Equal(8, captureTheFlag);
    }

    /// <summary>
    /// It is a real Invasion round: red captures, repeatedly, which is the only
    /// way red can win.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Blue wins by surviving the clock</b> — there is no blue capture in
    /// this mode — so a recording that held no <c>SCORE_INTEL</c> would exercise
    /// none of the round rules that make Invasion different from Capture the
    /// Flag. Three of them here, with their grabs.
    /// </remarks>
    [Fact]
    public void RedCapturesRepeatedly()
    {
        var (_, ticks) = Load();

        Assert.True(CountMessages(ticks, MessageType.ScoreIntel) >= 3, "not enough captures");
        Assert.True(CountMessages(ticks, MessageType.GrabIntel) >= 3, "not enough grabs");

        // And it is a fight rather than an unopposed walk, so the setup period
        // really did end.
        Assert.True(CountMessages(ticks, MessageType.WeaponFire) >= 50, "not enough fire");
        Assert.True(CountMessages(ticks, MessageType.PlayerDeath) >= 5, "not enough deaths");
    }

    /// <summary>The soldiers move like the reference from the last spawn anchor.</summary>
    /// <remarks>
    /// ⚠️ Invasion opens with a <b>full minute</b> of setup
    /// (<c>Rounds.InvasionSetupTicks</c>, against attack/defend's thirty
    /// seconds), during which the setup gates are solid — so this is also the
    /// first recording in which a character is held behind one.
    /// </remarks>
    [Fact]
    public void TheTrajectoriesAreExactFromTheSpawnAnchor()
    {
        var (trace, ticks) = Load();

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: not closed-loop");

        var report = new Replayer(new PortedSimulation(MapLoader))
            { Compare = Fields.Trajectory }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        Assert.True(
            firstBad - anchor.Value >= 40,
            $"{Fixture}: exact for only {firstBad - anchor.Value} ticks after t{anchor}");
    }
}
