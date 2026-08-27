using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// Every checked-in recording frames to its last byte.
/// </summary>
/// <remarks>
/// <b>The gate the harness did not have, and the one that would have caught the
/// sentry bug the day the fixture landed.</b>
/// <c>TraceParser.Parse</c> treats a <c>ProtocolException</c> as the end of the
/// recording, because a capture really can stop mid-message when the socket
/// closes between two reads. The consequence is that a <em>desynchronised
/// reader</em> is indistinguishable from a short recording: the trace comes back
/// as however many ticks were framed before the slip, every test that loads it
/// passes, and nothing says the rest of the file was dropped.
/// <para>
/// On 2026-08-26 an engineer's sentry did exactly that —
/// <c>reference-ctf_truefort-hotsupport.trace</c> framed 26 of its 1,570
/// messages and threw away 59,000 bytes. So the assertion here is not "it
/// parsed", it is <b>"it consumed everything"</b>, and it is a
/// <see cref="Theory"/> over every fixture in the directory rather than a list,
/// so a recording added later cannot quietly skip it.
/// </para>
/// </remarks>
public sealed class FixtureFramingTests
{
    public static TheoryData<string> Fixtures
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            var data = new TheoryData<string>();

            foreach (var path in Directory.EnumerateFiles(directory, "*.trace"))
                data.Add(Path.GetFileName(path));

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFixtureFramesToItsLastByte(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));

        // ⚠️ NARROWED to area 1, and the stream cannot be read without it. A
        // tall map is several rounds stacked vertically and only one area's
        // entities exist — cp_dirtbowl writes six control points and plays two —
        // and the HUD block carries no count prefix, so a reader framing by the
        // wrong number desynchronises on the first update. Measured on
        // reference-cp_dirtbowl-closedcp.trace: 458 of 138,406 bytes framed
        // un-narrowed, all of them narrowed.
        //
        // A no-op for every single-area map, which is every other fixture here.
        var map = LevelMap
            .FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map))
            .ForArea(1);

        // ⚠️ And re-shaped on CHANGE_MAP, or a recording that rotates to
        // another gamemode desynchronises the moment a CAPS_UPDATE or
        // FULL_UPDATE arrives on the new map — the only two tiers that carry
        // the HUD block. See ModeChangeFramingTests.
        var ticks = TraceParser.Parse(
            trace,
            GameModes.ShapeOf(map),
            out var start,
            out var stopped,
            name => GameModes.ShapeOf(
                LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1)));

        Assert.True(stopped is null, $"{fixture}: {stopped}");

        // And it is a recording rather than a fragment, so the assertion above
        // cannot be satisfied by an empty file.
        Assert.NotNull(start);
        Assert.True(ticks.Count > 100, $"{fixture}: only {ticks.Count} ticks");
    }

    /// <summary>
    /// The check above can actually fail: a trace truncated mid-message reports
    /// where it stopped.
    /// </summary>
    /// <remarks>
    /// Without this, "no fixture reports a framing stop" would be satisfied just
    /// as well by a parser that never reported one — which is precisely the
    /// state the harness was in before <c>stopped</c> existed.
    /// </remarks>
    [Fact]
    public void ATruncatedTraceReportsWhereItStopped()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-ctf_truefort-bots.trace");
        var whole = Trace.Decode(File.ReadAllBytes(path));
        var map = LevelMap.FromPng(whole.Header.Map, ReferenceTree.ReadMap(whole.Header.Map));
        var shape = GameModes.ShapeOf(map);

        TraceParser.Parse(whole, shape, out _, out var clean);
        Assert.Null(clean);

        // Chop the stream in the middle of a message rather than at its end.
        var cut = new Trace
        {
            Header = whole.Header,
            Stream = whole.Stream[..(whole.Stream.Length / 2 + 1)],
        };

        TraceParser.Parse(cut, shape, out _, out var stopped);
        Assert.NotNull(stopped);
        Assert.Contains("framing stopped at byte", stopped);
    }
}
