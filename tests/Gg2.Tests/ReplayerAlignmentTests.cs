using System.Linq;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>That the replayer reads a message family from the right tick — asked
/// with a synthetic trace, so the answer does not depend on a fixture agreeing
/// for a thousand ticks first.</b>
/// </summary>
/// <remarks>
/// <b>This exists because a real one was lost.</b>
/// <c>SupportClassFixtureTests.ThePortEmitsNoUberMessageTheReferenceDidNot</c>
/// was the project's only check that <see cref="Fields.Ubers"/> is compared
/// against the correct tick, and it worked by finding an <c>UBER_CHARGED</c>
/// 1,155 ticks past where its recording still agreed with the port at all. On
/// 2026-08-27 a simulation fix moved that charge by one tick, a 30-tick drift
/// tolerance had to go in, and a harness reading a tick late now looks exactly
/// like the drift. The check had to move somewhere that has no drift in it.
/// <para>
/// ⚠️ <b>The off-by-one it guards is real and this project has already paid for
/// it once.</b> The control-point comparison was reading a tick late for
/// several phases, which presented as the port starting every capture two ticks
/// early — read as a simulation bug and pinned as an open finding before anyone
/// looked at the harness. docs/PARITY.md, "the capture meter was compared one
/// tick late".
/// </para>
/// <para>
/// <b>Why the +1 is right</b>, rather than merely current:
/// <c>GameServerBeginStep.gml</c> serialises the state in the server's own Begin
/// Step, and a medic's charge is announced from a Begin Step that runs after it
/// — so the message describing tick T rides in tick T+1's block, the same
/// off-by-one a fire seed, a death and a spawn all have. See
/// <see cref="Replayer"/>.
/// </para>
/// </remarks>
public sealed class ReplayerAlignmentTests
{
    /// <summary>The tick the stand-in simulation charges on.</summary>
    private const int ChargeTick = 5;

    private const byte Medic = 2;

    /// <summary>
    /// A simulation that does nothing except announce one ubercharge, on a tick
    /// chosen by the test.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Counted from <see cref="Tick"/> calls rather than from a tick index
    /// the replayer hands over</b>, because it does not hand one over — which is
    /// the point. The simulation cannot know what the harness thinks the time
    /// is, so a harness that lines the two up wrongly has nothing to correct it.
    /// </remarks>
    private sealed class ChargesOnce : ITraceSimulation
    {
        private int _current = -1;

        public void Tick(ReadOnlySpan<PlayerInput> inputs) => _current++;

        public IReadOnlyList<UberRecord> UbersLastTick =>
            _current == ChargeTick ? [new UberRecord(Medic, UberEvent.Charged)] : [];

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

        public IReadOnlyList<IntelRecord> IntelLastTick => [];

        public IReadOnlyList<SentryRecord> SentriesLastTick => [];

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

    /// <summary>
    /// Ten ticks carrying nothing, except one <c>UBER_CHARGED</c> in the block
    /// of <paramref name="messageTick"/>.
    /// </summary>
    /// <remarks>
    /// No slots are observed, so <see cref="Fields.Position"/> and its
    /// neighbours have nothing to say and cannot contribute a mismatch — the
    /// comparison under test is the only one running.
    /// </remarks>
    private static TraceTick[] Trace(int messageTick) =>
        [.. Enumerable.Range(0, 10).Select(i => new TraceTick
        {
            Index = i,
            Tier = MessageType.QuickUpdate,
            Slots = new TraceSlot[8],
            Events = i == messageTick
                ? [new ServerMessage
                    {
                        Type = MessageType.UberCharged,
                        Payload = [Medic],
                        PlayerId = Medic,
                    }]
                : [],
        })];

    private static Mismatch[] Replay(int messageTick) =>
        [.. new Replayer(new ChargesOnce()) { Compare = Fields.Ubers }
            .Replay("synthetic", "ctf_truefort", Trace(messageTick), ReplayMode.ClosedLoop,
                stopAtFirstBadTick: false)
            .Mismatches];

    /// <summary>
    /// ✅ A charge on tick 5 is matched by an <c>UBER_CHARGED</c> in tick 6's
    /// block, and by nothing else.
    /// </summary>
    [Fact]
    public void AnUberIsComparedAgainstTheFollowingTicksMessages()
    {
        Assert.Empty(Replay(ChargeTick + 1));
    }

    /// <summary>
    /// ⚠️ <b>The controls, and they are the whole test.</b> Put the same message
    /// one tick either side and the comparison must object.
    /// </summary>
    /// <remarks>
    /// Without these, <see cref="AnUberIsComparedAgainstTheFollowingTicksMessages"/>
    /// passes just as well against a comparison that never runs — which is
    /// precisely how <see cref="Fields.Ubers"/> came to be unused for several
    /// phases with nothing noticing.
    /// <para>
    /// Each shift produces <b>two</b> mismatches, not one: the recorded message
    /// nobody claimed (<c>expected 1, got 0</c>) and the port's charge nobody
    /// recorded (<c>expected 0, got 1</c>). Asserting the pair rather than
    /// "some mismatch" is what distinguishes a misaligned comparison from a
    /// comparison that has simply stopped matching anything.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ChargeTick)]      // in the charging tick's own block: a tick early
    [InlineData(ChargeTick + 2)]  // a tick late
    public void AMessageOneTickEitherSideIsNotMatched(int messageTick)
    {
        var mismatches = Replay(messageTick);

        Assert.Equal(2, mismatches.Length);
        Assert.Contains(mismatches, m => m.Field == "ubercharged" && m is { Expected: 1, Actual: 0 });
        Assert.Contains(mismatches, m => m.Field == "ubercharged" && m is { Expected: 0, Actual: 1 });
        Assert.All(mismatches, m => Assert.Equal(Medic, m.Slot));
    }

    /// <summary>
    /// The comparison is off entirely unless <see cref="Fields.Ubers"/> asks for
    /// it, so a replay that does not select it cannot fail for uber reasons.
    /// </summary>
    /// <remarks>
    /// The other half of "a comparison that never runs": this asserts the switch
    /// works in the direction that makes the tests above meaningful. Held on
    /// <c>Fields.All &amp; ~Ubers</c> rather than <c>Fields.None</c> so that a
    /// family accidentally sharing Ubers' bit would show up here.
    /// </remarks>
    [Fact]
    public void NothingObjectsWhenUbersAreNotBeingCompared()
    {
        var report = new Replayer(new ChargesOnce()) { Compare = Fields.All & ~Fields.Ubers }
            .Replay("synthetic", "ctf_truefort", Trace(ChargeTick), ReplayMode.ClosedLoop,
                stopAtFirstBadTick: false);

        Assert.Empty(report.Mismatches);
    }
}
