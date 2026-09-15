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

    /// <summary>The roster labels survive the container.</summary>
    [Fact]
    public void RosterLabelsRoundTrip()
    {
        var original = new Trace
        {
            Header = Header(TraceHeader.CurrentFormatVersion),
            Stream = [1, 2, 3],
            Roster =
            [
                new TraceRosterNote
                {
                    Tick = 0,
                    Slot = 0,
                    Participant = TraceParticipant.Bot,
                    Controller = "reserved",
                    Name = "Server",
                },
                new TraceRosterNote
                {
                    Tick = 417,
                    Slot = 3,
                    Participant = TraceParticipant.Human,
                    Controller = string.Empty,
                    Name = "somebody",
                },
            ],
        };

        var decoded = Trace.Decode(original.Encode());

        Assert.Equal(original.Roster, decoded.Roster);
    }

    /// <summary>
    /// A v1 trace still decodes, and still encodes to the bytes it always did.
    /// </summary>
    /// <remarks>
    /// <b>The checked-in fixture corpus is v1 and is not re-recorded.</b> Those
    /// traces came off the running game, so re-taking them to add a field none
    /// of them needs would be days of live measurement — which makes "v1 is
    /// still read" a load-bearing claim rather than politeness, and the
    /// byte-identical half of it is what stops a v2 tail being appended to a
    /// header that says 1.
    /// </remarks>
    [Fact]
    public void AVersionOneTraceIsStillReadAndStillWritesTheSameBytes()
    {
        var old = new Trace { Header = Header(1), Stream = [9, 8, 7] };
        var bytes = old.Encode();

        var decoded = Trace.Decode(bytes);

        Assert.Equal(1, decoded.Header.FormatVersion);
        Assert.Equal(old.Stream, decoded.Stream);
        Assert.Empty(decoded.Roster);

        // No tail: the whole file is the header plus the stream, so a v2 reader
        // handed a v1 file finds nothing after the last stream byte to mistake
        // for a label count.
        Assert.Equal(bytes.Length, decoded.Encode().Length);
        Assert.Equal(bytes, decoded.Encode());
    }

    private static TraceHeader Header(int version) => new()
    {
        FormatVersion = version,
        ReferenceVersion = GameConstants.Version,
        Map = "ctf_truefort",
        MapMd5 = string.Empty,
        Source = "a test",
        RecordedUtc = new DateTime(2026, 8, 24, 12, 34, 56, DateTimeKind.Utc),
    };

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

        // ⚠️ READABLE, not CURRENT. The checked-in corpus is written in v1 and
        // is deliberately not re-recorded: v2 added the roster labels, which say
        // which slots were people, and a fixture of the reference has no bots in
        // it and no use for the answer. Pinning this to the current version
        // would mean re-recording the whole corpus against the running game
        // every time the container gained a field.
        Assert.InRange(
            trace.Header.FormatVersion,
            TraceHeader.OldestReadableFormatVersion,
            TraceHeader.CurrentFormatVersion);
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
        private int _tick;

        public void LoadMap(string map) { }

        public void SetCharacter(int slot, in CharacterBlock observed) => _characters[slot] = observed;

        /// <remarks>
        /// The oracle reads positions out of the trace, so it has no map and
        /// nothing to resolve a spawn point against. False keeps the replayer
        /// on the wire anchor, which is the behaviour this control is testing.
        /// </remarks>
        public bool SpawnCharacter(int slot, int spawnpointId, int group) => false;

        public void ClearCharacter(int slot) => _characters.Remove(slot);

        /// <summary>
        /// Compacts, because a departure renumbers the roster. The oracle would
        /// otherwise disagree with the port for the rest of any trace containing
        /// a PLAYER_LEAVE.
        /// </summary>
        public void RemoveSlot(int slot)
        {
            _characters.Remove(slot);

            foreach (var above in _characters.Keys.Where(k => k > slot).Order().ToArray())
            {
                _characters[above - 1] = _characters[above];
                _characters.Remove(above);
            }
        }

        public void SetPlayerClass(int slot, PlayerClass playerClass) { }

        public void SetPlayerTeam(int slot, Team team) { }

        public void BuildSentry(int slot, double x, double y, int direction) { }

        /// <remarks>The oracle replays recorded positions; it has nothing to seed.</remarks>
        public void SetFireSeed(int slot, ushort seed) { }

        private readonly List<DeathRecord> _deaths = [];

        public IReadOnlyList<DeathRecord> DeathsLastTick => _deaths;

        public IReadOnlyList<UberRecord> UbersLastTick => [];

        public IReadOnlyList<IntelRecord> IntelLastTick => [];

        public IReadOnlyList<SentryRecord> SentriesLastTick => [];

        public bool TryGetControlPoint(int index, out ControlPointBlock point)
        {
            point = default;
            return false;
        }

        public void SetControlPointUnlock(int ticks) { }

        public void SetTdmInvulnerability(int ticks) { }

        public void Tick(ReadOnlySpan<PlayerInput> inputs)
        {
            // The replayer compares before it advances, because one update
            // carries this tick's input and last tick's position. So the state
            // this call leaves behind is the state the *next* tick will be
            // compared against.
            _tick++;

            // Cheating at deaths too, and off the same one-tick offset the
            // replayer reads them at: a PLAYER_DEATH produced during tick i is
            // written after tick i's update and lands in tick i+1's message
            // block. _tick has just been advanced past the tick being run, so it
            // already names that block.
            _deaths.Clear();
            if (_tick < ticks.Count)
            {
                foreach (var message in ticks[_tick].Events)
                {
                    if (message.Type == MessageType.PlayerDeath && message.Payload.Length >= 4)
                    {
                        _deaths.Add(new DeathRecord(
                            message.Payload[0], message.Payload[1], message.Payload[2], message.Payload[3]));
                    }
                }
            }

            var next = _tick + 1;
            if (next >= ticks.Count)
                return;

            var tick = ticks[next];
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
