using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The trace format, the parser and the replayer.
/// </summary>
/// <remarks>
/// The fixture is a real recording of the reference GM8 server with two bots
/// playing <c>ctf_truefort</c> — real physics, real spawns, real deaths, real
/// weapon fire. It is the golden data phase 4 will be held to, and until then it
/// is what proves the harness can hold something to it.
/// </remarks>
public sealed class TraceTests
{
    private const string BotTrace = "reference-ctf_truefort-bots.trace";

    private static Trace LoadTrace(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path), $"missing trace fixture {path}");
        return Trace.Decode(File.ReadAllBytes(path));
    }

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks, StateUpdate? Start) Parse(string name)
    {
        var trace = LoadTrace(name);
        var map = LevelMap.FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map));
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out var start);
        return (trace, ticks, start);
    }

    [Fact]
    public void HeaderRoundTrips()
    {
        var original = new Trace
        {
            Header = new TraceHeader
            {
                FormatVersion = TraceHeader.CurrentFormatVersion,
                ReferenceVersion = GameConstants.Version,
                Map = "ctf_truefort",
                MapMd5 = string.Empty,
                Source = "a test",

                // Whole seconds: the header stores Unix seconds, not ticks.
                RecordedUtc = new DateTime(2026, 8, 24, 12, 34, 56, DateTimeKind.Utc),
            },
            Stream = [1, 2, 3, 4, 5],
        };

        var decoded = Trace.Decode(original.Encode());

        Assert.Equal(original.Header, decoded.Header);
        Assert.Equal(original.Stream, decoded.Stream);
    }

    /// <summary>
    /// A timestamp past 2038 in ticks overflowed an int in the first draft of
    /// the codec, which decoded to an out-of-range <c>DateTime</c> and took the
    /// whole trace with it.
    /// </summary>
    [Fact]
    public void TimestampSurvivesADateAfterTwoThousandThirtyEight()
    {
        var trace = new Trace
        {
            Header = new TraceHeader
            {
                FormatVersion = TraceHeader.CurrentFormatVersion,
                ReferenceVersion = GameConstants.Version,
                Map = "m",
                MapMd5 = string.Empty,
                Source = "s",
                RecordedUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Stream = [],
        };

        Assert.Equal(trace.Header.RecordedUtc, Trace.Decode(trace.Encode()).Header.RecordedUtc);
    }

    [Fact]
    public void RejectsSomethingThatIsNotATrace()
    {
        var error = Assert.Throws<ProtocolException>(() => Trace.Decode(new byte[64]));
        Assert.Contains("magic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FixtureIsARecordingOfTheReferenceAtTheExpectedVersion()
    {
        var trace = LoadTrace(BotTrace);

        Assert.Equal(TraceHeader.CurrentFormatVersion, trace.Header.FormatVersion);
        Assert.Equal(GameConstants.Version, trace.Header.ReferenceVersion);
        Assert.Equal("ctf_truefort", trace.Header.Map);
        Assert.Equal(string.Empty, trace.Header.MapMd5); // built-in
    }

    [Fact]
    public void ParsesIntoTicksAnchoredOnAFullUpdate()
    {
        var (_, ticks, start) = Parse(BotTrace);

        Assert.NotNull(start);
        Assert.True(ticks.Count > 500, $"only {ticks.Count} ticks");

        // Indices are dense and start at zero.
        for (var i = 0; i < ticks.Count; i++)
            Assert.Equal(i, ticks[i].Index);
    }

    /// <summary>
    /// Six <c>INPUTSTATE</c>s to one <c>QUICK_UPDATE</c>, and the
    /// position-bearing ticks land exactly seven apart. Measured on the
    /// reference's own stream, which is the only way this is worth asserting.
    /// </summary>
    [Fact]
    public void PositionBearingTicksAreSevenApart()
    {
        var (_, ticks, _) = Parse(BotTrace);

        var positions = ticks.Where(t => t.HasPositions).Select(t => t.Index).ToArray();
        Assert.True(positions.Length > 50, $"only {positions.Length} position ticks");

        for (var i = 1; i < positions.Length; i++)
            Assert.Equal(7, positions[i] - positions[i - 1]);

        var inputStates = ticks.Count(t => t.Tier == MessageType.InputState);
        Assert.Equal(6.0, (double)inputStates / positions.Length, 1);
    }

    /// <summary>
    /// The whole point of the format: every tick carries every player's action,
    /// and every seventh carries every player's position.
    /// </summary>
    [Fact]
    public void EveryTickCarriesInputsAndOnlyPositionTicksCarryPositions()
    {
        var (_, ticks, _) = Parse(BotTrace);

        var withCharacters = ticks.Where(t => t.Slots.Any(s => s.HasCharacter)).ToArray();
        Assert.NotEmpty(withCharacters);

        foreach (var tick in withCharacters)
        {
            foreach (var slot in tick.Slots.Where(s => s.HasCharacter))
            {
                if (tick.HasPositions)
                    Assert.NotNull(slot.Observed);
                else
                    Assert.Null(slot.Observed);
            }
        }
    }

    /// <summary>
    /// The bots did something. A trace of two characters standing still would
    /// pass every replay test for the wrong reason.
    /// </summary>
    [Fact]
    public void FixtureContainsRealMovement()
    {
        var (_, ticks, _) = Parse(BotTrace);

        var positions = ticks
            .Where(t => t.HasPositions)
            .SelectMany(t => t.Slots.Where(s => s.Observed is not null).Select(s => s.Observed!.Value))
            .ToArray();

        Assert.NotEmpty(positions);

        var minX = positions.Min(p => p.X);
        var maxX = positions.Max(p => p.X);

        // Wire units: 5 per world pixel, so this is a few hundred pixels of
        // travel rather than a jitter.
        Assert.True(maxX - minX > 1000, $"characters only moved {maxX - minX} wire units in x");
        Assert.Contains(positions, p => p.VSpeed != 0);

        // And they pressed things.
        var keys = ticks.SelectMany(t => t.Slots).Select(s => s.Input.Keys).Distinct().ToArray();
        Assert.True(keys.Length > 2, "no variety in the recorded keys");
    }

    [Fact]
    public void FixtureContainsSpawnsAndDeaths()
    {
        var (_, ticks, _) = Parse(BotTrace);

        var events = ticks.SelectMany(t => t.Events).Select(m => m.Type).ToArray();

        Assert.Contains(MessageType.PlayerSpawn, events);
        Assert.Contains(MessageType.PlayerJoin, events);
        Assert.Contains(MessageType.WeaponFire, events);
    }

    /// <summary>
    /// Phase 2's exit criterion in docs/PLAN.md: the replayer runs a trace
    /// against an empty simulation and reports a mismatch with a legible diff.
    /// </summary>
    [Fact]
    public void ReplayAgainstAnEmptySimulationFailsLegibly()
    {
        var (trace, ticks, _) = Parse(BotTrace);

        var report = new Replayer(new EmptySimulation())
            .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop);

        Assert.False(report.Passed);
        Assert.NotEmpty(report.Mismatches);

        // Closed-loop stops at the first bad tick: a hundred downstream
        // mismatches are one bug.
        var firstBadTick = report.Mismatches[0].Tick;
        Assert.All(report.Mismatches, m => Assert.Equal(firstBadTick, m.Tick));

        // A spawn is a resync, not a mismatch.
        Assert.True(report.Resyncs > 0, "no slot was resynchronised on spawn");

        var rendered = report.Render();
        Assert.Contains("mismatch(es), first at tick", rendered, StringComparison.Ordinal);
        Assert.Contains("expected", rendered, StringComparison.Ordinal);
        Assert.Contains("wire units", rendered, StringComparison.Ordinal);

        // The diff has to name the field and both values, or it is not legible.
        var first = report.Mismatches[0];
        Assert.NotEqual(first.Expected, first.Actual);
        Assert.Contains(first.Field, new[] { "x", "y", "hspeed", "vspeed", "hp", "ammo", "flags" });
    }

    /// <summary>
    /// Open-loop resets at every position-bearing tick, so its errors are
    /// bounded by one prediction window rather than growing without limit.
    /// </summary>
    [Fact]
    public void OpenLoopErrorsAreBoundedByOneWindow()
    {
        var (trace, ticks, _) = Parse(BotTrace);

        var open = new Replayer(new EmptySimulation())
            .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.OpenLoop);

        var closed = new Replayer(new EmptySimulation())
            .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(open.Comparisons > 100, $"only {open.Comparisons} open-loop comparisons");

        // Closed-loop never corrects, so a still simulation drifts as far as the
        // characters travelled. Open-loop is re-anchored seven ticks earlier
        // every time, so it cannot.
        Assert.True(open.MaxXError < closed.MaxXError,
            $"open-loop max |dx| {open.MaxXError} was not below closed-loop's {closed.MaxXError}");
    }

    /// <summary>
    /// A simulation that reproduces the trace exactly reports no mismatches —
    /// the harness must be able to say "pass", not only "fail".
    /// </summary>
    [Fact]
    public void ReplayOfAPerfectSimulationPasses()
    {
        var (trace, ticks, _) = Parse(BotTrace);

        var report = new Replayer(new OracleSimulation(ticks))
            .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop);

        Assert.True(report.Passed, report.Render());
        Assert.True(report.Comparisons > 100, $"only {report.Comparisons} comparisons");
        Assert.Contains("no mismatches", report.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A simulation that cheats by reading the answers.
    /// </summary>
    /// <remarks>
    /// Not a physics implementation — a control. It proves the comparison and
    /// the reporting agree with themselves, so that when phase 4's real
    /// simulation fails, the failure is the physics rather than the harness.
    /// </remarks>
    private sealed class OracleSimulation(IReadOnlyList<TraceTick> ticks) : ITraceSimulation
    {
        private readonly Dictionary<int, CharacterBlock> _characters = [];
        private int _tick = -1;

        public void LoadMap(string map) { }

        public void SetCharacter(int slot, in CharacterBlock observed) => _characters[slot] = observed;

        public void ClearCharacter(int slot) => _characters.Remove(slot);

        public void Tick(ReadOnlySpan<PlayerInput> inputs)
        {
            _tick++;
            if (_tick >= ticks.Count)
                return;

            var tick = ticks[_tick];
            if (!tick.HasPositions)
                return;

            for (var slot = 0; slot < tick.Slots.Length; slot++)
                if (tick.Slots[slot].Observed is { } observed)
                    _characters[slot] = observed;
        }

        public bool TryGetCharacter(int slot, out CharacterBlock character) =>
            _characters.TryGetValue(slot, out character);
    }
}
