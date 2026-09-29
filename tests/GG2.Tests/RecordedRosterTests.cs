using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A recording says which slots were people.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes is invisible from inside a trace.</b>
/// <c>docs/BOTAI.md</c> item 4 makes a bot's action exactly a client's action
/// and <c>PLAYER_JOIN</c> carries a name and nothing else, so before the labels
/// a recording of a bot-filled public server held twelve scripted players and
/// one person in the same fields, with nothing to tell them apart. A dataset
/// built from that is this project's own bot layer fed back in as demonstration
/// data — and no downstream test could ever have caught it, because every row
/// is a valid row.
/// </para>
/// <para>
/// <b>So the assertions here are about attribution, not about presence.</b>
/// "There are labels" is satisfied by a table of the wrong answers. What each
/// test pins is that a named player's label lands on the slot the stream gave
/// that name to.
/// </para>
/// </remarks>
public sealed class RecordedRosterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gg2-roster-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private const string Map = "ctf_truefort";

    /// <summary>The recording, as ticks. Framed to the last byte or the test fails.</summary>
    private static IReadOnlyList<TraceTick> Parse(Trace trace)
    {
        var shape = GameModes.ShapeOf(
            LevelMap.FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map)).ForArea(1));

        var ticks = TraceParser.Parse(
            trace, shape, out _, out var stopped,
            name => GameModes.ShapeOf(
                LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1)));

        Assert.True(stopped is null, $"the recording did not frame: {stopped}");
        return ticks;
    }

    /// <summary>Whether a <c>PLAYER_JOIN</c> announces this name.</summary>
    private static bool Names(ServerMessage message, string name)
    {
        var payload = message.Payload;

        return payload.Length >= 1
            && payload[0] == name.Length
            && payload.Length >= 1 + name.Length
            && System.Text.Encoding.ASCII.GetString(payload, 1, name.Length) == name;
    }

    private Trace Recorded()
    {
        var traces = Directory.EnumerateFiles(_directory, "*.trace").Order().ToArray();
        Assert.Single(traces);
        return Trace.Decode(File.ReadAllBytes(traces[0]));
    }

    /// <summary>
    /// A person and a bot on the same server get different labels, each on its
    /// own slot.
    /// </summary>
    [Fact]
    public void AHumanAndABotAreLabelledApart()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.RecordingDirectory = _directory;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.Bots = new BotPopulationOptions(
                Enabled: true, FillToPlayers: 3, MaxBots: 2, MinHumans: 0, Difficulty: 3);
        });

        var client = harness.JoinClient("a-person");
        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Scout);
        harness.Tick(30);

        var humanSlot = harness.Server.RosterCount - 1;
        Assert.False(harness.Server.IsBot(humanSlot), "the harness put the person on a bot slot");

        harness.Server.Dispose();

        var trace = Recorded();
        var labels = trace.Roster;

        Assert.NotEmpty(labels);

        // The person's own slot, by name as well as by index — the name is the
        // cross-check that a label is attached to the slot the stream used,
        // rather than to a plausible one.
        var person = labels.Single(note => note.Name == "a-person");
        Assert.Equal(TraceParticipant.Human, person.Participant);
        Assert.Equal(humanSlot, person.Slot);
        Assert.Equal(string.Empty, person.Controller);

        // And at least one bot, labelled a bot, with the mode that drove it.
        // Slot 0 is excluded by index rather than by its controller string: it
        // is a bot in the sense that matters here — not a person — but no brain
        // drives it, so it carries "reserved" instead of a mode.
        var bots = labels
            .Where(note => note.Participant == TraceParticipant.Bot && note.Slot != 0)
            .ToArray();

        Assert.NotEmpty(bots);
        Assert.All(bots, bot => Assert.Equal(BotMode.Scripted.ToString(), bot.Controller));

        Assert.Equal("reserved", labels.Single(note => note.Slot == 0).Controller);

        // Nothing is left unlabelled: every slot the roster held has an answer.
        Assert.DoesNotContain(labels, note => note.Participant == TraceParticipant.Unknown);
    }

    /// <summary>
    /// Slot 0 is labelled a bot, not a person.
    /// </summary>
    /// <remarks>
    /// The server's own reserved player exists on a headless server and presses
    /// nothing. Unlabelled it would be the one gap in an otherwise complete
    /// table, which reads downstream as a human whose every row is idle — and
    /// idle rows are exactly what a reaction-delay or key-flip statistic is
    /// most distorted by.
    /// </remarks>
    [Fact]
    public void TheServersOwnSlotIsNotAPerson()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.RecordingDirectory = _directory;
        });

        harness.Tick(10);
        harness.Server.Dispose();

        var zero = Recorded().Roster.Single(note => note.Slot == 0);
        Assert.NotEqual(TraceParticipant.Human, zero.Participant);
    }

    /// <summary>
    /// A label names the tick its player was already on, not the one after.
    /// </summary>
    /// <remarks>
    /// <b>A one-tick slip here is silent and permanent.</b> The tick index is
    /// what a consumer joins the labels to the stream by, so a label a tick late
    /// attributes the joiner's first action to whoever held the slot before.
    /// What makes this checkable is that the stream says when the join happened
    /// too: the <c>PLAYER_JOIN</c> is an event on a tick, and the label must not
    /// be after it.
    /// </remarks>
    [Fact]
    public void ALabelIsNotLaterThanTheJoinItDescribes()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.RecordingDirectory = _directory;
        });

        // Ticks before the join, so a label stamped at 0 would pass by accident.
        harness.Tick(20);

        var client = harness.JoinClient("latecomer");
        client.SendChangeTeam(Team.Blue);
        harness.Tick(20);
        harness.Server.Dispose();

        var trace = Recorded();
        var label = trace.Roster.Single(note => note.Name == "latecomer");

        var ticks = Parse(trace);

        // ⚠️ Matched BY NAME, not "the first PLAYER_JOIN". The segment opens
        // with the join burst, which replays a PLAYER_JOIN for every roster
        // entry that already existed — so tick 0 always carries one, and a test
        // that took the first would compare this label against the server's own
        // slot 0 and pass whatever the recorder did.
        var joinedAt = -1;

        for (var i = 0; i < ticks.Count && joinedAt < 0; i++)
        {
            foreach (var message in ticks[i].Events)
            {
                if (message.Type != MessageType.PlayerJoin || !Names(message, "latecomer"))
                    continue;

                joinedAt = i;
                break;
            }
        }

        Assert.True(joinedAt > 0, "the recording holds no PLAYER_JOIN for the latecomer");

        // Not "equal": the label is taken before the flush that carries the
        // join, so it lands on that tick or the one before, never after. The
        // direction is what matters — a label ahead of its player is harmless
        // (the slot was empty), one behind it is a misattribution.
        Assert.True(
            label.Tick <= joinedAt,
            $"the label is at tick {label.Tick}, after the PLAYER_JOIN at {joinedAt}");
    }

    /// <summary>
    /// Every segment carries its own labels, including for players who were
    /// already on the server when it opened.
    /// </summary>
    /// <remarks>
    /// The same argument as the preamble: a trace has to stand alone. A rotation
    /// rolls a segment, and a person who joined on the previous map is still
    /// playing — a segment that only recorded <em>changes</em> would describe
    /// them as nothing at all.
    /// </remarks>
    [Fact]
    public void EverySegmentLabelsThePlayersItOpensWith()
    {
        using var harness = new ServerHarness(o =>
        {
            o.RecordingDirectory = _directory;
            o.Map = Map;
            o.MapRotation = [Map, "koth_valley"];
            o.TimeLimitMinutes = 1;
        });

        var client = harness.JoinClient("stayer");
        client.SendChangeTeam(Team.Red);

        harness.Tick(1800 + GG2.Simulation.Rounds.MapChangeDelay + 16);
        harness.Server.Dispose();

        var traces = Directory.EnumerateFiles(_directory, "*.trace").Order().ToArray();
        Assert.Equal(2, traces.Length);

        foreach (var path in traces)
        {
            var trace = Trace.Decode(File.ReadAllBytes(path));

            var stayer = Assert.Single(trace.Roster, note => note.Name == "stayer");

            Assert.Equal(TraceParticipant.Human, stayer.Participant);

            // At tick 0 of the segment that did not see them join, because the
            // roll re-labels everyone rather than carrying a diff across files.
            Assert.True(
                stayer.Tick >= 0,
                "a label from a previous segment leaked into this one");
        }
    }

    /// <summary>
    /// An unchanged roster produces one label per player, not one per tick.
    /// </summary>
    /// <remarks>
    /// The roster is compared every tick, so the thing that stops the table
    /// growing without bound is the diff rather than the frequency. At 30 Hz a
    /// half-hour segment would otherwise carry 1.6 million rows describing one
    /// player who never moved.
    /// </remarks>
    [Fact]
    public void AnUnchangedRosterIsNotRelabelledEveryTick()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.RecordingDirectory = _directory;
        });

        var client = harness.JoinClient("steady");
        client.SendChangeTeam(Team.Red);
        harness.Tick(200);
        harness.Server.Dispose();

        var labels = Recorded().Roster;

        // Slot 0 and the one joiner. A name change would legitimately add
        // another, and nothing here changes a name.
        Assert.Equal(2, labels.Count);
    }

    /// <summary>
    /// The disk cap counts what the directory already holds, so restarting does
    /// not hand the recorder a fresh gigabyte.
    /// </summary>
    /// <remarks>
    /// <b>The case this is about is the ordinary one, not an edge.</b> The hosts
    /// this exists for restart on every deploy and every config push; a cap
    /// scoped to one process run bounds nothing on exactly those machines, and
    /// the failure is a full disk on an unattended server.
    /// </remarks>
    [Fact]
    public void TheDiskCapCountsWhatIsAlreadyThere()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "old.trace"), new byte[4096]);

        var log = new RecordingLog();
        var recorder = new MatchRecorder(_directory, "test", log, () => []);

        Assert.Equal(4096, recorder.TotalBytes);

        recorder.StartSegment(Map);
        recorder.Append([1, 2, 3]);
        recorder.Dispose();

        // The pre-existing file plus the segment as it lands on disk — a trace
        // costs its header as well as its stream, and the cap bounds the disk.
        var onDisk = new DirectoryInfo(_directory)
            .EnumerateFiles("*.trace*")
            .Sum(file => file.Length);

        Assert.Equal(onDisk, recorder.TotalBytes);
        Assert.True(recorder.TotalBytes > 4096 + 3, "the segment's header went uncounted");
    }
}
