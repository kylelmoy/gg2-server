using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// <c>docs/BOTAI.md</c> item 3: a live server can write down what it broadcasts,
/// in the format <c>gg2-parity</c> already replays.
/// </summary>
/// <remarks>
/// <para>
/// The item's requirement is not "log the inputs" — the broadcast stream already
/// carries every player's keys and aim on every tick — it is <b>one recorder,
/// not a second logging path</b>, switchable on in production, with a bounded
/// on-disk format. So what these tests gate is that a server-written recording
/// is indistinguishable from one <c>gg2-parity record</c> produced: it decodes,
/// it frames to its last byte, it opens with an anchor, and a human's keypress
/// is in it.
/// </para>
/// <para>
/// ⚠️ <b>The framing assertion is the load-bearing one</b>, for the reason
/// <see cref="FixtureFramingTests"/> exists: <c>TraceParser.Parse</c> treats a
/// <c>ProtocolException</c> as the end of a recording, so a mis-framed file is
/// indistinguishable from a short one. A recorder that wrote subtly wrong bytes
/// would produce files that load, parse to a handful of ticks, and silently
/// throw away the game — and the first anyone would know is when the recordings
/// of the first public game turned out to be worthless, which is exactly the
/// loss the deadline is about.
/// </para>
/// </remarks>
public sealed class MatchRecorderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gg2-recorder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string[] Traces() =>
        Directory.Exists(_directory)
            ? [.. Directory.EnumerateFiles(_directory, "*.trace").Order()]
            : [];

    /// <summary>Recording is off unless it is asked for.</summary>
    /// <remarks>
    /// ⚠️ Not a formality. Recording a game people are playing is a decision,
    /// and a default that recorded would make every test run, every parity
    /// replay and every bot rollout write files somebody has to notice and
    /// delete.
    /// </remarks>
    [Fact]
    public void AServerRecordsNothingUnlessAsked()
    {
        using var harness = new ServerHarness();
        harness.Tick(10);

        Assert.Empty(Traces());
    }

    /// <summary>
    /// A recording server writes a trace that decodes, frames to its last byte,
    /// and opens with the anchor a replay needs.
    /// </summary>
    [Fact]
    public void ARecordingFramesToItsLastByteAndOpensWithAFullUpdate()
    {
        var ticks = RecordAGame(out var trace, out var start);

        // ⚠️ The anchor, and it comes back as `start` rather than as ticks[0]:
        // TraceParser lifts the opening FULL_UPDATE out of the tick list,
        // because it is the starting point the following ticks are relative to.
        // Teeing only the broadcast would give a file that starts wherever the
        // recording happened to start — parseable, because a flush is always a
        // message boundary, but with nothing to replay against.
        Assert.NotNull(start);
        Assert.NotEmpty(ticks);

        // ⚠️ And the file opens with the join burst rather than with the first
        // broadcast: a JOIN_UPDATE is the first byte.
        Assert.Equal((byte)MessageType.JoinUpdate, trace.Stream[0]);

        Assert.Equal(harnessMap, trace.Header.Map);
        Assert.Equal(TraceHeader.CurrentFormatVersion, trace.Header.FormatVersion);
        Assert.Equal(GameConstants.Version, trace.Header.ReferenceVersion);
    }

    /// <summary>
    /// ⚠️ The point of the whole item: a person's keypress is in the file.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:267</c> — one byte of keys, two of aim, one
    /// of distance, arriving every tick for every human. That is the labelled
    /// dataset item 3 is about, and it reaches disk here without anything
    /// capturing it separately: the server was already broadcasting it.
    /// </remarks>
    [Fact]
    public void AHumansKeysAndAimAreInTheRecording()
    {
        var ticks = RecordAGame(out _, out _);

        var pressed = ticks
            .SelectMany(tick => tick.Slots)
            .Any(slot => slot.Input.Keys != PlayerKeys.None);

        Assert.True(pressed, "no slot in the recording ever pressed a key");

        var aimed = ticks
            .SelectMany(tick => tick.Slots)
            .Any(slot => slot.Input.NetAimDirection != 0);

        Assert.True(aimed, "no slot in the recording ever aimed anywhere");
    }

    /// <summary>
    /// A recording opens with the join burst — the message sequence a real
    /// joiner is sent — rather than with whatever the server broadcast next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The bytes cannot be compared with a real joiner's, and it is worth
    /// saying why rather than leaving it looking untested.</b> A preamble is
    /// taken against the roster as it is at that moment: the recorder's is taken
    /// at startup, when only the server's own slot 0 exists, and any joiner's is
    /// taken later with everybody before it replayed into it. They are supposed
    /// to differ. What is guaranteed is that both come from
    /// <c>GameServer.WriteJoinBurst</c> — there is exactly one method — which is
    /// equivalence by construction, of the same kind this codebase records
    /// elsewhere rather than gating.
    /// </para>
    /// <para>
    /// So what is gated is the <em>shape</em>: JOIN_UPDATE, CHANGE_MAP naming
    /// the map the header names, and a FULL_UPDATE before any INPUTSTATE. That
    /// fails if the recorder is ever changed to tee only the broadcast, which is
    /// the mistake worth catching.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARecordingOpensWithTheJoinBurst()
    {
        RecordAGame(out var trace, out var start);

        Assert.Equal((byte)MessageType.JoinUpdate, trace.Stream[0]);
        Assert.NotNull(start);

        // The CHANGE_MAP is what makes a replay load the right map, and it has
        // to agree with the header the recorder wrote beside it.
        var reader = new NetReader(trace.Stream);
        Assert.Equal(MessageType.JoinUpdate, (MessageType)reader.ReadByte());
        reader.ReadByte(); // player id
        reader.ReadByte(); // map area
        Assert.Equal(MessageType.ChangeMap, (MessageType)reader.ReadByte());
        Assert.Equal(trace.Header.Map, reader.ReadString1());
    }

    /// <summary>
    /// ⚠️ The on-disk bound is a byte total, so a server that rolls segments
    /// often does not spend its cap on rolls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by running it rather than by reading it.</b> The first version
    /// capped at 32 segments, on the reasoning that 32 x
    /// <see cref="MatchRecorder.SegmentBytes"/> is a gigabyte. But a segment
    /// also rolls on a map change — so at <c>--speed max</c> with bots, where a
    /// round ended about once a second, the recorder stopped after
    /// <b>26 seconds</b> having written 8 MB of its allowed 1,024. The cap was
    /// counting the wrong thing.
    /// </para>
    /// <para>
    /// This drives the same shape without the speed: many rolls, and the
    /// recorder still going.
    /// </para>
    /// </remarks>
    [Fact]
    public void RollingASegmentDoesNotSpendTheDiskBound()
    {
        var log = new RecordingLog();
        var recorder = new MatchRecorder(_directory, "test", log, () => [0x01, 0x02, 0x03, 0x04]);

        for (var roll = 0; roll < 200; roll++)
        {
            recorder.StartSegment("ctf_truefort");
            recorder.Append([0x05, 0x06]);
        }

        recorder.Dispose();

        Assert.False(recorder.IsFull, "the recorder stopped after 200 rolls of a few bytes each");
        Assert.Equal(200, recorder.SegmentCount);

        // Six bytes a segment — the four-byte preamble and the two appended.
        Assert.Equal(200 * 6, recorder.TotalBytes);
        Assert.True(recorder.TotalBytes < MatchRecorder.MaxTotalBytes);
    }

    /// <summary>And the bound does stop it, so the test above is not vacuous.</summary>
    [Fact]
    public void TheDiskBoundStopsTheRecorder()
    {
        var log = new RecordingLog();
        var big = new byte[1024 * 1024];
        var recorder = new MatchRecorder(_directory, "test", log, () => []);

        recorder.StartSegment("ctf_truefort");

        // A megabyte at a time until the cap; the segment roll at SegmentBytes
        // happens along the way, which is the interaction worth exercising.
        for (var i = 0; i < (MatchRecorder.MaxTotalBytes / big.Length) + 2; i++)
            recorder.Append(big);

        recorder.Dispose();

        Assert.True(recorder.IsFull, "the recorder never reached its cap");
        Assert.True(
            recorder.TotalBytes <= MatchRecorder.MaxTotalBytes + big.Length,
            $"it overran the cap by more than one append: {recorder.TotalBytes}");

        Assert.Contains(log.Lines, line => line.Contains("recording stopped", StringComparison.Ordinal));
    }

    /// <summary>A map change rolls a new segment, so a segment is always one map.</summary>
    /// <remarks>
    /// ⚠️ Not tidiness: the header carries a single map name, and
    /// <c>TraceParser</c> is handed one starting shape. A segment spanning a
    /// rotation would carry a header naming the map it no longer plays.
    /// </remarks>
    [Fact]
    public void AMapChangeRollsANewSegment()
    {
        using var harness = new ServerHarness(o =>
        {
            o.RecordingDirectory = _directory;
            o.Map = "ctf_truefort";
            o.MapRotation = ["ctf_truefort", "koth_valley"];

            // A one-minute round, so the rotation is reachable in a test.
            o.TimeLimitMinutes = 1;
        });

        // Through the whole round, the victory screen and onto the next map —
        // the same way MapRotationTests drives one, because a real rotation is
        // the only thing that should roll a segment.
        harness.Tick(OneMinute + Gg2.Simulation.Rounds.MapChangeDelay + 16);
        harness.Server.Dispose();

        var traces = Traces();
        Assert.Equal(2, traces.Length);
        Assert.Contains("ctf_truefort", traces[0]);
        Assert.Contains("koth_valley", traces[1]);
    }

    private const string harnessMap = "ctf_truefort";

    private const int OneMinute = 1800;

    /// <summary>
    /// Runs a short game with a client pressing keys, then closes the server and
    /// parses what it wrote.
    /// </summary>
    private IReadOnlyList<TraceTick> RecordAGame(out Trace trace, out StateUpdate? start)
    {
        using var harness = new ServerHarness(o => o.RecordingDirectory = _directory);

        var client = harness.JoinClient("human");
        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Scout);
        harness.Tick(5);
        Assert.True(harness.Server.IsAlive(harness.Server.RosterCount - 1), "the client never spawned");

        // Something a person would actually do: hold right and aim.
        for (var tick = 0; tick < 60; tick++)
        {
            client.SendInputState(new PlayerInput
            {
                Keys = PlayerKeys.Right | PlayerKeys.Jump,
                NetAimDirection = 8192,
                AimDistance = 100,
            });

            harness.Tick();
        }

        // ⚠️ A segment only becomes a readable .trace when it is closed, so the
        // server has to be disposed before the file is read. That is the whole
        // reason Dispose closes the recorder first.
        harness.Server.Dispose();

        var traces = Traces();
        Assert.Single(traces);

        trace = Trace.Decode(File.ReadAllBytes(traces[0]));

        var map = LevelMap
            .FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map))
            .ForArea(1);

        var ticks = TraceParser.Parse(
            trace,
            GameModes.ShapeOf(map),
            out start,
            out var stopped,
            name => GameModes.ShapeOf(
                LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1)));

        Assert.True(stopped is null, $"the server's own recording did not frame: {stopped}");
        Assert.NotNull(start);
        Assert.NotEmpty(ticks);

        return ticks;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }
}
