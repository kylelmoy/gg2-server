using System.Linq;
using GG2.Parity;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// That the replayer reads each message family from the right tick —
/// asked with a synthetic trace, so the answer does not depend on a fixture
/// agreeing for a thousand ticks first.
/// </summary>
/// <remarks>
/// <b>This exists because a real one was lost.</b>
/// <c>SupportClassFixtureTests.ThePortEmitsNoUberMessageTheReferenceDidNot</c>
/// was the project's only check that <see cref="Fields.Ubers"/> is compared
/// against the correct tick, and it worked by finding an <c>UBER_CHARGED</c>
/// 1,155 ticks past where its recording still agreed with the port at all. A
/// simulation fix that moved a charge by one tick needed a 30-tick drift
/// tolerance, and a harness reading a tick late now looks exactly like the
/// drift.
/// <para>
/// <b>All four event families were in that condition</b>, not just Ubers —
/// deaths, the objective and sentries were each gated only by a fixture
/// happening to reach them, which is one unrelated fix away from the same
/// problem. They are covered here together.
/// </para>
/// <para>
/// <b>The off-by-one this guards is real and the project has already paid for
/// it once.</b> The control-point comparison read a tick late for several
/// phases, which presented as the port starting every capture two ticks early —
/// diagnosed as a simulation bug and pinned as an open finding before anyone
/// looked at the harness. docs/PARITY.md, "the capture meter was compared one
/// tick late".
/// </para>
/// <para>
/// <b>Why the +1 is right</b>, rather than merely current:
/// <c>GameServerBeginStep.gml</c> serialises the state in the server's own Begin
/// Step, and every one of these four is announced from a Begin Step or later —
/// so the message describing tick T rides in tick T+1's block, the same
/// off-by-one a fire seed and a spawn have. See <see cref="Replayer"/>.
/// </para>
/// <para>
/// <b>Control points are here too, and they take a different shape.</b> They
/// are not an event family: the block rides on a <c>CAPS_UPDATE</c> and is
/// compared <em>before</em> the tick runs rather than out of the next tick's
/// messages. They are also the one this project already got wrong —
/// the comparison read a tick late for several phases. See
/// <see cref="TheCaptureMeterIsComparedAgainstTheEndOfThePreviousTick"/>.
/// </para>
/// </remarks>
public sealed class ReplayerAlignmentTests
{
    /// <summary>The tick the stand-in simulation announces on.</summary>
    private const int EventTick = 5;

    /// <summary>The roster slot every announcement names.</summary>
    private const byte Slot = 2;

    /// <summary>
    /// What the capture meter starts at, so an observation one tick behind tick
    /// zero is still a representable number.
    /// </summary>
    private const int MeterBase = 100;

    public enum Family
    {
        Ubers,
        Deaths,
        Intel,
        Sentries,
    }

    /// <summary>
    /// A simulation that does nothing except announce one event of one family,
    /// on a tick chosen by the test.
    /// </summary>
    /// <remarks>
    /// <b>Counted from <see cref="Tick"/> calls rather than from a tick index
    /// the replayer hands over</b>, because it does not hand one over — which is
    /// the point. The simulation cannot know what the harness thinks the time
    /// is, so a harness that lines the two up wrongly has nothing to correct it.
    /// </remarks>
    private sealed class AnnouncesOnce(Family family) : ITraceSimulation
    {
        private int _current = -1;

        private bool Now => _current == EventTick;

        public void Tick(ReadOnlySpan<PlayerInput> inputs) => _current++;

        public IReadOnlyList<UberRecord> UbersLastTick =>
            Now && family == Family.Ubers ? [new UberRecord(Slot, UberEvent.Charged)] : [];

        public IReadOnlyList<DeathRecord> DeathsLastTick =>
            Now && family == Family.Deaths ? [new DeathRecord(Slot, 3, 4, 9)] : [];

        public IReadOnlyList<IntelRecord> IntelLastTick =>
            Now && family == Family.Intel ? [new IntelRecord(Slot, IntelEvent.Grabbed)] : [];

        public IReadOnlyList<SentryRecord> SentriesLastTick =>
            Now && family == Family.Sentries ? [new SentryRecord(Slot, 3, 4, 1)] : [];

        public void LoadMap(string map) { }

        public void SetCharacter(int slot, in CharacterBlock observed) { }

        public bool SpawnCharacter(int slot, int spawnpointId, int group) => false;

        public void ClearCharacter(int slot) { }

        public void RemoveSlot(int slot) { }

        public void SetPlayerClass(int slot, PlayerClass playerClass) { }

        public void SetPlayerTeam(int slot, Team team) { }

        public void BuildSentry(int slot, double x, double y, int direction) { }

        public void SetFireSeed(int slot, ushort seed) { }

        public void SetControlPointUnlock(int ticks) { }

        public void SetTdmInvulnerability(int ticks) { }

        public bool TryGetCharacter(int slot, out CharacterBlock character)
        {
            character = default;
            return false;
        }

        public bool TryGetControlPoint(int index, out ControlPointBlock point)
        {
            point = default;
            return false;
        }
    }

    /// <summary>The wire message that should match what the stub announced.</summary>
    private static ServerMessage MessageFor(Family family) => family switch
    {
        Family.Ubers => new ServerMessage
        {
            Type = MessageType.UberCharged,
            Payload = [Slot],
            PlayerId = Slot,
        },

        // [victim, killer, assistant, cause], and all four are compared.
        Family.Deaths => new ServerMessage
        {
            Type = MessageType.PlayerDeath,
            Payload = [Slot, 3, 4, 9],
        },

        Family.Intel => new ServerMessage
        {
            Type = MessageType.GrabIntel,
            Payload = [Slot],
            PlayerId = Slot,
        },

        _ => new ServerMessage
        {
            Type = MessageType.DestroySentry,
            Payload = [Slot, 3, 4, 1],
        },
    };

    /// <summary>The mismatch field each family reports under.</summary>
    private static string FieldFor(Family family) => family switch
    {
        Family.Ubers => "ubercharged",
        Family.Deaths => "death",
        Family.Intel => "grabintel",
        _ => "destroysentry",
    };

    private static Fields CompareFor(Family family) => family switch
    {
        Family.Ubers => Fields.Ubers,
        Family.Deaths => Fields.Deaths,
        Family.Intel => Fields.Intel,
        _ => Fields.Sentries,
    };

    /// <summary>
    /// Ten ticks carrying nothing, except one message in the block of
    /// <paramref name="messageTick"/>.
    /// </summary>
    /// <remarks>
    /// No slots are observed, so the positional comparisons have nothing to say
    /// and cannot contribute a mismatch — the family under test is the only one
    /// that can speak.
    /// </remarks>
    private static TraceTick[] Trace(Family family, int messageTick) =>
        [.. Enumerable.Range(0, 10).Select(i => new TraceTick
        {
            Index = i,
            Tier = MessageType.QuickUpdate,
            Slots = new TraceSlot[8],
            Events = i == messageTick ? [MessageFor(family)] : [],
        })];

    private static Mismatch[] Replay(Family family, int messageTick, Fields? compare = null) =>
        [.. new Replayer(new AnnouncesOnce(family)) { Compare = compare ?? CompareFor(family) }
            .Replay("synthetic", "ctf_truefort", Trace(family, messageTick), ReplayMode.ClosedLoop,
                stopAtFirstBadTick: false)
            .Mismatches];

    /// <summary>
    /// An event on tick 5 is matched by the message in tick 6's block, and by
    /// nothing else — for every family that is read out of the following tick.
    /// </summary>
    [Theory]
    [InlineData(Family.Ubers)]
    [InlineData(Family.Deaths)]
    [InlineData(Family.Intel)]
    [InlineData(Family.Sentries)]
    public void AnEventIsComparedAgainstTheFollowingTicksMessages(Family family)
    {
        Assert.Empty(Replay(family, EventTick + 1));
    }

    /// <summary>
    /// <b>The controls, and they are the whole test.</b> Put the same message
    /// one tick either side and the comparison must object.
    /// </summary>
    /// <remarks>
    /// Without these, the test above passes just as well against a comparison
    /// that never runs — which is precisely how <see cref="Fields.Ubers"/> came
    /// to be unused for several phases with nothing noticing.
    /// <para>
    /// Each shift produces <b>two</b> mismatches, not one: the recorded
    /// message nobody claimed (<c>expected 1, got 0</c>) and the port's event
    /// nobody recorded (<c>expected 0, got 1</c>). Asserting the pair rather
    /// than "some mismatch" is what distinguishes a misaligned comparison from
    /// one that has simply stopped matching anything.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(Family.Ubers, EventTick)]
    [InlineData(Family.Ubers, EventTick + 2)]
    [InlineData(Family.Deaths, EventTick)]
    [InlineData(Family.Deaths, EventTick + 2)]
    [InlineData(Family.Intel, EventTick)]
    [InlineData(Family.Intel, EventTick + 2)]
    [InlineData(Family.Sentries, EventTick)]
    [InlineData(Family.Sentries, EventTick + 2)]
    public void AMessageOneTickEitherSideIsNotMatched(Family family, int messageTick)
    {
        var mismatches = Replay(family, messageTick);
        var field = FieldFor(family);

        Assert.Equal(2, mismatches.Length);
        Assert.Contains(mismatches, m => m.Field == field && m is { Expected: 1, Actual: 0 });
        Assert.Contains(mismatches, m => m.Field == field && m is { Expected: 0, Actual: 1 });
        Assert.All(mismatches, m => Assert.Equal(Slot, m.Slot));
    }

    /// <summary>
    /// Each comparison is off entirely unless its own <see cref="Fields"/> bit
    /// asks for it.
    /// </summary>
    /// <remarks>
    /// The other half of "a comparison that never runs": this asserts the switch
    /// works in the direction that makes the tests above meaningful. Held on
    /// <c>All</c> minus the one bit rather than on <c>None</c>, so a family
    /// accidentally sharing another's bit shows up here.
    /// </remarks>
    [Theory]
    [InlineData(Family.Ubers)]
    [InlineData(Family.Deaths)]
    [InlineData(Family.Intel)]
    [InlineData(Family.Sentries)]
    public void NothingObjectsWhenThatFamilyIsNotBeingCompared(Family family)
    {
        Assert.Empty(Replay(family, EventTick, Fields.All & ~CompareFor(family)));
    }

    // --- control points, which are not an event family -----------------------

    /// <summary>
    /// A simulation whose capture meter is simply the number of ticks it has
    /// run, so the harness's idea of "when" is the only thing under test.
    /// </summary>
    private sealed class CountsCaptureTicks : ITraceSimulation
    {
        private ushort _ticksRun;

        public void Tick(ReadOnlySpan<PlayerInput> inputs) => _ticksRun++;

        public bool TryGetControlPoint(int index, out ControlPointBlock point)
        {
            // Offset so the meter never has to represent a negative. It is a
            // ushort on the wire, so an observation a tick BEHIND tick zero
            // would wrap to 65535 and the comparison would report a difference
            // of 65535 rather than of one — true, and useless as a signature.
            point = new ControlPointBlock(
                Team: 0,
                CappingTeam: 1,
                Capping: (ushort)(MeterBase + _ticksRun));

            return index == 0;
        }

        public void LoadMap(string map) { }

        public void SetCharacter(int slot, in CharacterBlock observed) { }

        public bool SpawnCharacter(int slot, int spawnpointId, int group) => false;

        public void ClearCharacter(int slot) { }

        public void RemoveSlot(int slot) { }

        public void SetPlayerClass(int slot, PlayerClass playerClass) { }

        public void SetPlayerTeam(int slot, Team team) { }

        public void BuildSentry(int slot, double x, double y, int direction) { }

        public void SetFireSeed(int slot, ushort seed) { }

        public void SetControlPointUnlock(int ticks) { }

        public void SetTdmInvulnerability(int ticks) { }

        public IReadOnlyList<DeathRecord> DeathsLastTick => [];

        public IReadOnlyList<UberRecord> UbersLastTick => [];

        public IReadOnlyList<IntelRecord> IntelLastTick => [];

        public IReadOnlyList<SentryRecord> SentriesLastTick => [];

        public bool TryGetCharacter(int slot, out CharacterBlock character)
        {
            character = default;
            return false;
        }
    }

    /// <summary>
    /// Ten ticks, each observing a capture meter of <c>index + offset</c>.
    /// </summary>
    private static TraceTick[] CapTrace(int offset) =>
        [.. Enumerable.Range(0, 10).Select(i => new TraceTick
        {
            Index = i,
            Tier = MessageType.CapsUpdate,
            Slots = new TraceSlot[8],
            Events = [],
            ControlPoints =
            [
                new ControlPointBlock(
                    Team: 0,
                    CappingTeam: 1,
                    Capping: (ushort)(MeterBase + i + offset)),
            ],
        })];

    private static Mismatch[] ReplayCaps(int offset) =>
        [.. new Replayer(new CountsCaptureTicks()) { Compare = Fields.ControlPoints }
            .Replay("synthetic", "ctf_truefort", CapTrace(offset), ReplayMode.ClosedLoop,
                stopAtFirstBadTick: false)
            .Mismatches];

    /// <summary>
    /// <b>The capture meter is compared against the END OF THE PREVIOUS TICK,
    /// which is what the observation describes.</b>
    /// </summary>
    /// <remarks>
    /// <c>GameServerBeginStep.gml</c> serialises the HUD in the server's own
    /// Begin Step, so a <c>CAPS_UPDATE</c> arriving with tick T carries the
    /// meter as it stood at the end of T-1 — exactly like the positions beside
    /// it. The replayer therefore compares <b>before</b> <c>Advance</c>.
    /// <para>
    /// <b>Comparing after it instead is a real bug this project shipped</b>:
    /// it holds the port's end-of-this-tick meter against the reference's
    /// end-of-last-tick one, so the port reads high by exactly one tick of
    /// capture. That presented as the port starting every capture two ticks
    /// early on a soldier recording and four on a scout one, was read as a
    /// simulation bug, and was pinned as an open finding before anybody looked
    /// at the harness. docs/PARITY.md, "the capture meter was compared one
    /// tick late".
    /// </para>
    /// <para>
    /// The stand-in's meter is just its tick count, so nothing here depends on
    /// capture arithmetic being right — only on the two clocks agreeing.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCaptureMeterIsComparedAgainstTheEndOfThePreviousTick()
    {
        Assert.Empty(ReplayCaps(0));
    }

    /// <summary>
    /// <b>The control, and it carries the signature of the original bug:</b>
    /// an observation one tick out makes every comparison read high — or low —
    /// by exactly one.
    /// </summary>
    /// <remarks>
    /// Unlike the event families above, the sensitivity cannot be shown by
    /// moving a message between blocks: there is one observation per tick and it
    /// is compared against the tick it arrived with. So the offset goes into the
    /// <em>value</em>, which is the same thing a one-tick misalignment does to
    /// it.
    /// <para>
    /// <b><c>Expected - Actual = -offset</c> on every tick is the assertion
    /// that matters.</b> "Some mismatch" would also pass for a comparison that
    /// had stopped reading the meter at all; a uniform one-tick lead is the
    /// shape a misaligned clock actually produces.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void AnObservationOneTickOutMakesEveryComparisonReadByExactlyOne(int offset)
    {
        var mismatches = ReplayCaps(offset);

        // Every one of the ten ticks, not merely some of them.
        Assert.Equal(10, mismatches.Length);
        Assert.All(mismatches, m => Assert.Equal("cpcapping", m.Field));
        Assert.All(mismatches, m => Assert.Equal(offset, m.Expected - m.Actual));
    }

    /// <summary>
    /// The meter is not compared unless <see cref="Fields.ControlPoints"/> asks.
    /// </summary>
    /// <remarks>
    /// <b>Not a formality here.</b> <c>Fields.ControlPoints</c> spent two
    /// gamemodes doing nothing: <c>StateUpdateReader</c> parsed the block and
    /// <c>TraceParser</c> threw it away, so a port that never captured the point
    /// replayed a King of the Hill recording exactly as well as a correct one.
    /// </remarks>
    [Fact]
    public void NothingObjectsWhenControlPointsAreNotBeingCompared()
    {
        var report = new Replayer(new CountsCaptureTicks())
            { Compare = Fields.All & ~Fields.ControlPoints }
            .Replay("synthetic", "ctf_truefort", CapTrace(1), ReplayMode.ClosedLoop,
                stopAtFirstBadTick: false);

        Assert.Empty(report.Mismatches);
    }
}
