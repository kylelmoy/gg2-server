using System.Linq;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>That the replayer reads each message family from the right tick —
/// asked with a synthetic trace, so the answer does not depend on a fixture
/// agreeing for a thousand ticks first.</b>
/// </summary>
/// <remarks>
/// <b>This exists because a real one was lost.</b>
/// <c>SupportClassFixtureTests.ThePortEmitsNoUberMessageTheReferenceDidNot</c>
/// was the project's only check that <see cref="Fields.Ubers"/> is compared
/// against the correct tick, and it worked by finding an <c>UBER_CHARGED</c>
/// 1,155 ticks past where its recording still agreed with the port at all. On
/// 2026-08-27 a simulation fix moved that charge by one tick, a 30-tick drift
/// tolerance had to go in, and a harness reading a tick late now looks exactly
/// like the drift.
/// <para>
/// ⚠️ <b>All four event families were in that condition</b>, not just Ubers —
/// deaths, the objective and sentries were each gated only by a fixture
/// happening to reach them, which is one unrelated fix away from the same
/// problem. They are covered here together.
/// </para>
/// <para>
/// ⚠️ <b>The off-by-one this guards is real and the project has already paid for
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
/// ⚠️ <b>Control points are deliberately absent.</b> They are not an event
/// family: the block rides on a <c>CAPS_UPDATE</c> and is compared <em>before</em>
/// the tick runs rather than out of the next tick's messages, so it needs a
/// different shape of test. It is also the one that was already caught once.
/// </para>
/// </remarks>
public sealed class ReplayerAlignmentTests
{
    /// <summary>The tick the stand-in simulation announces on.</summary>
    private const int EventTick = 5;

    /// <summary>The roster slot every announcement names.</summary>
    private const byte Slot = 2;

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
    /// ⚠️ <b>Counted from <see cref="Tick"/> calls rather than from a tick index
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
    /// ✅ An event on tick 5 is matched by the message in tick 6's block, and by
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
    /// ⚠️ <b>The controls, and they are the whole test.</b> Put the same message
    /// one tick either side and the comparison must object.
    /// </summary>
    /// <remarks>
    /// Without these, the test above passes just as well against a comparison
    /// that never runs — which is precisely how <see cref="Fields.Ubers"/> came
    /// to be unused for several phases with nothing noticing.
    /// <para>
    /// ⚠️ Each shift produces <b>two</b> mismatches, not one: the recorded
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
}
