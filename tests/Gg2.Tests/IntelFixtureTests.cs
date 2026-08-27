using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The objective against the recordings that contain one.
/// </summary>
/// <remarks>
/// <b>Four of the six fixtures hold an intel message, and none of them was
/// being compared until phase 6's objective landed.</b> Three
/// <c>GRAB_INTEL</c>s in <c>closedcombat</c>, one each in <c>hotcombat</c> and
/// <c>closedsupport</c>, and a grab plus a <c>SCORE_INTEL</c> in
/// <c>hotsupport</c> — <c>gg2-parity census</c> prints them.
/// <para>
/// <b>A grab is a much stronger claim than it looks.</b> Nothing about the
/// briefcase is in an <c>INPUTSTATE</c> or a <c>QUICK_UPDATE</c>: its position
/// and its alarm ride in a <c>FULL_UPDATE</c>, of which a recording holds
/// exactly one, and so does the carrier's <c>intel</c> byte. So a port with no
/// objective at all replays every one of these fixtures with the same drift it
/// had before — which is exactly what it did. The message is the only
/// observation, and it lands on the tick a character's box first overlaps the
/// flag's, which means reproducing it needs the trajectory, the intel box and
/// the team-gate test all right at once.
/// </para>
/// <para>
/// ⚠️ <b>Two of the four recorded events are out of reach, for reasons that
/// belong to the recordings rather than to the port</b>, and both are written
/// up in docs/PARITY.md:
/// <list type="bullet">
/// <item>
/// <c>hotsupport</c>'s <c>SCORE_INTEL</c> on tick 59 is a bot that was already
/// carrying when the recorder connected. The only statement of that is the join
/// <c>FULL_UPDATE</c>, which the replayer does not apply — see
/// <c>PortedSimulation.SetCharacter</c>.
/// </item>
/// <item>
/// <c>closedcombat</c> loses <b>every</b> character at once on tick 767 with no
/// message of any kind — measured, not inferred: slots 4 and 5 are the only
/// live ones and both vanish between one <c>INPUTSTATE</c> and the next, then
/// respawn together 150 ticks later. A round reset, which in the reference also
/// puts the briefcase back on its base. The port sees only two characters
/// disappearing, treats each as a destruction (which drops the flag) and then
/// returns it 899 ticks later — so <c>closedcombat</c> cannot gate a return.
/// </item>
/// </list>
/// The grabs are held to in all four, and that is the part worth having.
/// </para>
/// </remarks>
public sealed class IntelFixtureTests
{
    private const string ClosedCombatTrace = "reference-ctf_truefort-closedcombat.trace";

    private const string ClosedSupportTrace = "reference-ctf_truefort-closedsupport.trace";

    private const string HotCombatTrace = "reference-ctf_truefort-hotcombat.trace";

    private const string HotSupportTrace = "reference-ctf_truefort-hotsupport.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{fixture}: {stopped}");
        return (trace, ticks);
    }

    /// <summary>Every intel message a recording holds, with the tick it arrived on.</summary>
    /// <remarks>
    /// A message is written after the state update of the tick that produced it,
    /// so <see cref="TraceParser"/> files it under the <em>following</em> tick —
    /// the same offset a <c>WEAPON_FIRE</c> seed and a <c>PLAYER_DEATH</c> have.
    /// Subtracting one here puts each event on the tick it actually happened.
    /// </remarks>
    private static List<(int Tick, MessageType Type, byte Id)> RecordedIntel(
        IReadOnlyList<TraceTick> ticks)
    {
        var found = new List<(int, MessageType, byte)>();

        foreach (var tick in ticks)
        foreach (var message in tick.Events)
        {
            if (message.Type is MessageType.GrabIntel or MessageType.DropIntel
                or MessageType.ScoreIntel or MessageType.ReturnIntel
                && message.Payload.Length >= 1)
            {
                found.Add((tick.Index - 1, message.Type, message.Payload[0]));
            }
        }

        return found;
    }

    /// <summary>
    /// The fixtures really do contain the objective messages this file claims,
    /// so a green run below cannot be a green run over nothing.
    /// </summary>
    /// <remarks>
    /// The control every "does the port reproduce X" test owes: an assertion
    /// that X is in the recording at all. Without it, deleting
    /// <c>Intel.CollisionStep</c> would leave this class passing.
    /// <para>
    /// Spelled out to the tick rather than counted, because the ticks are the
    /// evidence docs/PARITY.md's table is written from and a recording is the
    /// one thing here that cannot be re-derived by reading code.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ClosedCombatTrace, "t133 GrabIntel[3] t746 GrabIntel[4] t2177 GrabIntel[1]")]
    [InlineData(ClosedSupportTrace, "t2232 GrabIntel[1]")]
    [InlineData(HotCombatTrace, "t67 GrabIntel[5]")]
    [InlineData(HotSupportTrace, "t59 ScoreIntel[6] t819 GrabIntel[3]")]
    public void EachFixtureHoldsTheIntelMessagesItIsBeingCheckedFor(string fixture, string expected)
    {
        var (_, ticks) = Load(fixture);

        var actual = string.Join(
            " ",
            RecordedIntel(ticks).Select(e => $"t{e.Tick} {e.Type}[{e.Id}]"));

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The port picks the flag up on the reference's own tick, in the fixture
    /// where every character has an exact spawn anchor.
    /// </summary>
    /// <remarks>
    /// <c>closedsupport</c> is the only recording all six of whose bots spawn
    /// inside it, so it is the only one whose characters are on exact integer
    /// coordinates rather than inside a wire quantisation bin. Closed loop, with
    /// <see cref="Fields.Intel"/> as the only comparison: the trajectory window
    /// this fixture passes is 489 ticks and the grab is well past it, so
    /// comparing positions as well would stop the replay before it got there.
    /// <para>
    /// That is a stronger claim than the shorter window makes it sound — a grab
    /// happens on the tick two boxes first touch, so it needs the character to
    /// be within a couple of pixels of the reference's after a thousand ticks of
    /// closed-loop replay.
    /// </para>
    /// <para>
    /// ⚠️ <b>It is not, however, much of a gate on the briefcase's box.</b>
    /// Measured by mutation: <see cref="Intel.BoxLeft"/> has to move two pixels
    /// before this test notices, and <see cref="Intel.BoxRight"/>,
    /// <see cref="Intel.BoxTop"/> and <see cref="Intel.BoxBottom"/> can each
    /// move <b>sixteen</b> without it noticing at all — the bot approaches from
    /// one side and never crosses the other three edges. Do not read a green run
    /// here as confirmation of a box the sprite files were only read for.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSpawnAnchoredFixturesGrabLandsOnItsRecordedTick()
    {
        var (trace, ticks) = Load(ClosedSupportTrace);

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.Intel }
            .Replay(ClosedSupportTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(
            report.Mismatches.Count == 0,
            string.Join(
                ", ",
                report.Mismatches.Take(8).Select(m =>
                    $"t{m.Tick} slot {m.Slot} {m.Field} {m.Expected}->{m.Actual}")));
    }

    /// <summary>
    /// Every recorded <c>GRAB_INTEL</c> is taken by the same player in the port,
    /// in all four fixtures that hold one.
    /// </summary>
    /// <remarks>
    /// Open loop, and the ticks are deliberately not compared: an open-loop
    /// replay resynchronises every seventh tick from a position quantised to a
    /// fifth of a pixel, so a bot walking into the briefcase can cross the
    /// boundary a tick either side of the reference. What must not change is
    /// <em>who</em> picks it up and how often.
    /// <para>
    /// Grabs only. The other three messages each have a reason they cannot be
    /// held to across all four recordings — see this class's remarks — and are
    /// gated by <c>IntelTests</c> instead.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ClosedCombatTrace)]
    [InlineData(ClosedSupportTrace)]
    [InlineData(HotCombatTrace)]
    [InlineData(HotSupportTrace)]
    public void EveryRecordedGrabIsTakenByTheSamePlayer(string fixture)
    {
        var (trace, ticks) = Load(fixture);

        var produced = new List<(MessageType Type, byte Id)>();
        new Replayer(new IntelWatcher(new PortedSimulation(MapLoader), produced)) { Compare = Fields.None }
            .Replay(fixture, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        var expected = RecordedIntel(ticks)
            .Where(e => e.Type == MessageType.GrabIntel)
            .Select(e => e.Id)
            .Order()
            .ToArray();

        var actual = produced
            .Where(e => e.Type == MessageType.GrabIntel)
            .Select(e => e.Id)
            .Order()
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Records every intel message the simulation produces, and is otherwise the
    /// simulation.
    /// </summary>
    /// <remarks>
    /// The same decorator shape <c>SupportClassFixtureTests</c> uses for uber
    /// charges: the replayer owns the tick loop, so the only way to watch an
    /// output across a whole replay is to sit between the two.
    /// </remarks>
    private sealed class IntelWatcher(
        ITraceSimulation inner,
        List<(MessageType Type, byte Id)> produced) : ITraceSimulation
    {
        public void LoadMap(string map) => inner.LoadMap(map);

        public void SetCharacter(int slot, in CharacterBlock observed) => inner.SetCharacter(slot, observed);

        public bool SpawnCharacter(int slot, int spawnpointId, int group) =>
            inner.SpawnCharacter(slot, spawnpointId, group);

        public void ClearCharacter(int slot) => inner.ClearCharacter(slot);

        public void RemoveSlot(int slot) => inner.RemoveSlot(slot);

        public void SetPlayerClass(int slot, PlayerClass playerClass) => inner.SetPlayerClass(slot, playerClass);

        public void SetPlayerTeam(int slot, Team team) => inner.SetPlayerTeam(slot, team);

        public void BuildSentry(int slot, double x, double y, int direction) =>
            inner.BuildSentry(slot, x, y, direction);

        public void SetFireSeed(int slot, ushort seed) => inner.SetFireSeed(slot, seed);

        public void Tick(ReadOnlySpan<PlayerInput> inputs)
        {
            inner.Tick(inputs);

            foreach (var intel in inner.IntelLastTick)
                produced.Add((TypeOf(intel.Event), intel.Id));
        }

        private static MessageType TypeOf(IntelEvent which) => which switch
        {
            IntelEvent.Grabbed => MessageType.GrabIntel,
            IntelEvent.Dropped => MessageType.DropIntel,
            IntelEvent.Scored => MessageType.ScoreIntel,
            _ => MessageType.ReturnIntel,
        };

        public IReadOnlyList<DeathRecord> DeathsLastTick => inner.DeathsLastTick;

        public IReadOnlyList<UberRecord> UbersLastTick => inner.UbersLastTick;

        public IReadOnlyList<IntelRecord> IntelLastTick => inner.IntelLastTick;

        public IReadOnlyList<SentryRecord> SentriesLastTick => inner.SentriesLastTick;

        public bool TryGetControlPoint(int index, out ControlPointBlock point)
        {
            return inner.TryGetControlPoint(index, out point);
        }

        public void SetControlPointUnlock(int ticks) => inner.SetControlPointUnlock(ticks);

        public void SetTdmInvulnerability(int ticks) => inner.SetTdmInvulnerability(ticks);

        public bool TryGetCharacter(int slot, out CharacterBlock character) =>
            inner.TryGetCharacter(slot, out character);
    }
}
