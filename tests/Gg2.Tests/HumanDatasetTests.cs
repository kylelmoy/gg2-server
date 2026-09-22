using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A recorded public game, read back as training rows.
/// </summary>
/// <remarks>
/// <b>End to end on purpose.</b> The pieces each have their own tests; what
/// none of them can establish is that the chain works — that a server's own
/// recording replays into a world, that the labels line up with the stream, and
/// that the row a person's keypress ends up in is the person's. That claim is
/// only checkable by running it, which is the failure mode <c>CLAUDE.md</c>
/// warns about: reasoning that a path is clean is not the same as walking it.
/// </remarks>
public sealed class HumanDatasetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "gg2-dataset-" + Guid.NewGuid().ToString("N"));

    private string Traces => Path.Combine(_root, "traces");

    private string Output => Path.Combine(_root, "out");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private const string Map = "ctf_truefort";

    /// <summary>
    /// Records a short game with one person holding keys, and returns the
    /// traces.
    /// </summary>
    private string[] RecordAGame(bool withBots)
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.RecordingDirectory = Traces;

            if (withBots)
            {
                o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
                o.Bots = new BotPopulationOptions(
                    Enabled: true, FillToPlayers: 3, MaxBots: 2, MinHumans: 0, Difficulty: 3);
            }
        });

        var client = harness.JoinClient("a-person");
        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Soldier);
        harness.Tick(10);

        // Something a person does: walk, turn, and fire.
        for (var tick = 0; tick < 120; tick++)
        {
            client.SendInputState(new PlayerInput
            {
                Keys = tick % 20 < 10 ? PlayerKeys.Right : PlayerKeys.Left | PlayerKeys.Attack,
                NetAimDirection = (ushort)((tick * 400) % 65536),
                AimDistance = 100,
            });

            harness.Tick();
        }

        harness.Server.Dispose();

        return [.. Directory.EnumerateFiles(Traces, "*.trace").Order()];
    }

    private HumanDataset.Options Options(int repeat = 1, bool bots = false) =>
        new()
        {
            Maps = ReferenceTree.MapsDirectory,
            Output = Output,
            ActionRepeat = repeat,
            IncludeBots = bots,
        };

    /// <summary>
    /// A recording of a person playing produces rows, and every one of them is
    /// that person.
    /// </summary>
    [Fact]
    public void ARecordedGameBecomesHumanRows()
    {
        var traces = RecordAGame(withBots: true);
        Assert.NotEmpty(traces);

        var report = HumanDataset.Build(traces, Options());

        Assert.Empty(report.Refused);
        Assert.True(report.Rows > 0, "the recording produced no rows");

        var participants = NpyFile.ReadArray(
            File.ReadAllBytes(Path.Combine(Output, "participant.npy")));

        Assert.Equal(report.Rows, participants.Rows * participants.Columns);

        // ⛔ The assertion the whole change exists for. With bots on the server
        // and the default filter, a bot row reaching the corpus means the labels
        // are not doing their job — and every such row is a valid-looking row of
        // this project's own scripted layer sold as demonstration data.
        for (var i = 0; i < participants.Values.Length; i++)
        {
            Assert.Equal((float)TraceParticipant.Human, participants.Values[i]);
        }
    }

    /// <summary>
    /// The columns line up: one row of every array per row of every other.
    /// </summary>
    /// <remarks>
    /// <b>A misaligned column is silent.</b> Every array loads, every shape is
    /// plausible, and the observations are paired with somebody else's actions
    /// from that point on. Nothing downstream can notice.
    /// </remarks>
    [Fact]
    public void EveryColumnHasTheSameNumberOfRows()
    {
        var report = HumanDataset.Build(RecordAGame(withBots: false), Options());

        Assert.True(report.Rows > 0, "the recording produced no rows");

        var observation = NpyFile.ReadArray(
            File.ReadAllBytes(Path.Combine(Output, "observation.npy")));

        Assert.Equal(report.Rows, observation.Rows);
        Assert.Equal(ObservationWriter.Length, observation.Columns);

        var action = NpyFile.ReadArray(File.ReadAllBytes(Path.Combine(Output, "action.npy")));
        Assert.Equal(report.Rows, action.Rows);
        Assert.Equal(ActionDecoder.HeadShape.Length, action.Columns);

        foreach (var name in new[] { "turn_degrees", "saturated", "slot", "tick", "trace", "life" })
        {
            var column = NpyFile.ReadArray(File.ReadAllBytes(Path.Combine(Output, name + ".npy")));

            Assert.Equal(report.Rows, column.Rows * column.Columns);
        }
    }

    /// <summary>
    /// Every emitted head is inside the width the policy's output block is
    /// split into.
    /// </summary>
    /// <remarks>
    /// The encoder and the decoder share their constants, so this is really a
    /// gate on the dataset's column order: a transposed pair of columns puts a
    /// three-valued head's value into a two-valued one and shows up here rather
    /// than as a strange training curve.
    /// </remarks>
    [Fact]
    public void NoHeadIsOutsideItsWidth()
    {
        var report = HumanDataset.Build(RecordAGame(withBots: false), Options());
        Assert.True(report.Rows > 0);

        var action = NpyFile.ReadArray(File.ReadAllBytes(Path.Combine(Output, "action.npy")));
        var widths = ActionDecoder.HeadShape;

        for (var row = 0; row < action.Rows; row++)
        {
            for (var head = 0; head < action.Columns; head++)
            {
                var value = action.Values[(row * action.Columns) + head];

                Assert.InRange(value, 0, widths[head] - 1);
            }
        }
    }

    /// <summary>
    /// An unlabelled recording is refused, not read as if everyone in it were a
    /// person.
    /// </summary>
    /// <remarks>
    /// <b>The default has to fail closed.</b> A trace from before the labels
    /// existed is indistinguishable from one of a server with no bots, and
    /// treating the two the same is the mistake the labels were added to
    /// prevent — so the absence of labels is an error rather than an
    /// assumption.
    /// </remarks>
    [Fact]
    public void AnUnlabelledTraceIsRefused()
    {
        var traces = RecordAGame(withBots: false);
        var trace = Trace.Decode(File.ReadAllBytes(traces[0]));

        // The same recording, written back as the format that has no labels.
        var old = new Trace
        {
            Header = trace.Header with { FormatVersion = 1 },
            Stream = trace.Stream,
        };

        var path = Path.Combine(Traces, "v1.trace");
        File.WriteAllBytes(path, old.Encode());

        var report = HumanDataset.Build([path], Options());

        Assert.Equal(0, report.Rows);
        Assert.Single(report.Refused);
        Assert.Contains("roster labels", report.Refused[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// An action repeat samples fewer rows, and measures the turn over the same
    /// span it samples at.
    /// </summary>
    /// <remarks>
    /// <b>Not a subsample.</b> A policy at repeat k turns once per k ticks
    /// against a fixed per-decision cap, so the aim delta has to be measured
    /// over k ticks too. Taking every k-th row of a per-tick dataset would
    /// under-report every turn by a factor of k, and the saturation rate — the
    /// statistic the cap is fitted to — with it.
    /// </remarks>
    [Fact]
    public void AnActionRepeatSamplesDecisionsNotTicks()
    {
        var traces = RecordAGame(withBots: false);

        var every = HumanDataset.Build(traces, Options(repeat: 1));

        var turns = Turns();
        var fine = Mean(turns);

        var coarse = HumanDataset.Build(traces, Options(repeat: 4));
        var wide = Mean(Turns());

        Assert.True(every.Rows > coarse.Rows, "a repeat of 4 did not sample fewer rows");

        // The client turns steadily, so a four-tick decision covers about four
        // times the angle. Loose bounds: the point is the direction, not a
        // constant, and rows at the edges of lives break the exact ratio.
        Assert.True(
            wide > fine * 2,
            $"a 4-tick decision turned {wide:0.00}deg against {fine:0.00}deg per tick");

        double[] Turns()
        {
            var column = NpyFile.ReadArray(
                File.ReadAllBytes(Path.Combine(Output, "turn_degrees.npy")));

            return [.. column.Values.Select(v => (double)v)];
        }

        static double Mean(double[] values) =>
            values.Length == 0 ? 0 : values.Average(Math.Abs);
    }

    /// <summary>
    /// The manifest carries the stamp the rows were made under.
    /// </summary>
    /// <remarks>
    /// The same rule <c>gg2-rl</c> enforces for a checkpoint, one layer back: an
    /// observation is the output of a particular physics and a particular
    /// encoder, so rows from another <c>SIM_UUID</c> describe a different game.
    /// Without the stamp in the file, two corpora merge silently.
    /// </remarks>
    [Fact]
    public void TheManifestSaysWhatTheRowsWereMadeUnder()
    {
        HumanDataset.Build(RecordAGame(withBots: false), Options());

        var manifest = File.ReadAllText(Path.Combine(Output, "manifest.txt"));

        Assert.Contains($"sim_uuid\t{SimulationStamp.Uuid}", manifest, StringComparison.Ordinal);
        Assert.Contains(
            $"observation_layout\t{ObservationWriter.LayoutVersion}",
            manifest,
            StringComparison.Ordinal);

        Assert.Contains("action_heads\thorizontal,jump,down,fire,aim_delta,zoom", manifest, StringComparison.Ordinal);
    }

    /// <summary>
    /// A class chosen before a rotation is the class the rows after it observe,
    /// and the manifest says the reconstruction agreed with the recording.
    /// </summary>
    /// <remarks>
    /// <b>The fleet's shape, end to end.</b> A player keeps its class across a
    /// map change and re-picks only its team, so the new segment names the
    /// class once, in its preamble, ahead of the rotation's teed
    /// <c>CHANGE_MAP</c>. A replay that dropped the roster on that message
    /// rebuilt this soldier as a scout — every row after the rotation carried
    /// the wrong class, and 71 of the fleet's first 149 traces did. The drift
    /// lines are what show it: a wrong class spawns at the wrong max hp, and
    /// the hp column counts it.
    /// </remarks>
    [Fact]
    public void AClassSurvivesARotationIntoTheRows()
    {
        const string second = "ctf_2dfort";

        using (var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.RecordingDirectory = Traces;
        }))
        {
            var client = harness.JoinClient("a-person");
            client.SendChangeTeam(Team.Red);
            client.SendChangeClass(PlayerClass.Soldier);
            harness.Tick(60);

            Assert.True(harness.Server.RequestMapChange(second), "the map change was refused");
            harness.Tick(330);

            // The team only: a map change puts everyone on spectator and keeps
            // the class, and a person rejoining a team does not re-pick it.
            client.SendChangeTeam(Team.Red);

            for (var tick = 0; tick < 240; tick++)
            {
                client.SendInputState(new PlayerInput
                {
                    Keys = tick % 20 < 10 ? PlayerKeys.Right : PlayerKeys.Left,
                    AimDistance = 100,
                });

                harness.Tick();
            }

            harness.Server.Dispose();
        }

        // By map, not by name: both segments can open in the same second, and
        // the timestamp then leaves the map name to decide the order.
        string[] traces =
        [
            .. Directory.EnumerateFiles(Traces, "*.trace")
                .OrderBy(t => Path.GetFileName(t).Contains(second, StringComparison.Ordinal)),
        ];
        Assert.Equal(2, traces.Length);
        Assert.Contains(second, Path.GetFileName(traces[1]), StringComparison.Ordinal);

        var report = HumanDataset.Build(traces, Options());

        Assert.Equal(2, report.Drift.Count);
        foreach (var drift in report.Drift)
            Assert.DoesNotContain(drift.Mismatches, m => m.Field == "hp");

        var observation = NpyFile.ReadArray(
            File.ReadAllBytes(Path.Combine(Output, "observation.npy")));
        var trace = NpyFile.ReadArray(File.ReadAllBytes(Path.Combine(Output, "trace.npy")));

        var afterRotation = 0;
        for (var row = 0; row < trace.Values.Length; row++)
        {
            if (trace.Values[row] != 1)
                continue;

            afterRotation++;

            // The class one-hot opens the self block.
            var at = row * observation.Columns;
            Assert.Equal(1f, observation.Values[at + (int)PlayerClass.Soldier]);
            Assert.Equal(0f, observation.Values[at + (int)PlayerClass.Scout]);
        }

        Assert.True(afterRotation > 0, "no rows were recorded after the rotation");

        var manifest = File.ReadAllText(Path.Combine(Output, "manifest.txt"));
        Assert.Equal(2, manifest.Split('\n').Count(l => l.StartsWith("drift\t", StringComparison.Ordinal)));
    }
}
