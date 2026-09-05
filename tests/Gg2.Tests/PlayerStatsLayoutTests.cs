using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The scoreboard's bytes, through the real writer and the real reader.
/// </summary>
/// <remarks>
/// <b>The same argument as <c>MineUpdateLayoutTests</c>, and the same blind
/// spot.</b> The stat block is <c>FULL_UPDATE</c>-only; <c>gg2-parity record</c>
/// joins immediately, so the one full update in any recording is the join's,
/// sent before anybody has scored anything. A port that wrote eleven zeroes for
/// ever — which this one did until phase 6's scoreboard — replays every fixture
/// identically. Round-tripping the real writer through the real reader is the
/// only way to see these bytes at all.
/// <para>
/// Transcribed from <c>Objects/InGameElements/Player.events/User Event 12.xml</c>
/// and <c>Scripts/Maps/Arena/serverArenaEndRound.gml</c>.
/// </para>
/// </remarks>
public sealed class PlayerStatsLayoutTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Slot 0 is the server's reserved spectator, so the player is slot 1.</summary>
    private const byte Slot = 1;

    private static WorldShape Shape => GameModes.ShapeOf(
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    /// <summary>Writes a <c>FULL_UPDATE</c> for a world and reads it straight back.</summary>
    private static StateUpdate RoundTrip(in WorldState state)
    {
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var roster = new Roster("server");
        roster.Add(new RosterEntry { Name = "p", Team = Team.Red, Class = PlayerClass.Soldier });
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));

        var captured = state;

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        PlayerStatsBlock Statistics(int slot) => new(
            Scoring.Get(captured, slot, StatIndex.Kills),
            Scoring.Get(captured, slot, StatIndex.Deaths),
            Scoring.Get(captured, slot, StatIndex.Caps),
            Scoring.Get(captured, slot, StatIndex.Assists),
            Scoring.Get(captured, slot, StatIndex.Destruction),
            Scoring.Get(captured, slot, StatIndex.Stabs),
            Scoring.Get(captured, slot, StatIndex.Healing),
            Scoring.Get(captured, slot, StatIndex.Defenses),
            Scoring.Get(captured, slot, StatIndex.Invulns),
            Scoring.Get(captured, slot, StatIndex.Bonus),
            Scoring.Get(captured, slot, StatIndex.Points));

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, NoCharacters, null, null, Statistics)
            .Write(writer, MessageType.FullUpdate);

        // The writer emits the message-type byte and the reader starts at the
        // payload — see MineUpdateLayoutTests.
        var reader = new NetReader(writer.WrittenSpan[1..]);
        PlayerClass[] classes = [PlayerClass.Scout, PlayerClass.Soldier];

        return StateUpdateReader.Read(ref reader, MessageType.FullUpdate, Shape, classes);
    }

    private static WorldState WithStats(params (StatIndex Stat, double Amount)[] awards)
    {
        var state = default(WorldState);
        state.AddPlayer(Slot);

        foreach (var (stat, amount) in awards)
            Scoring.Award(ref state, Slot, stat, amount);

        return state;
    }

    /// <summary>
    /// The eleven counters are written in <b>serialisation order, which is
    /// not <see cref="StatIndex"/> order</b>: DOMINATIONS and REVENGE are
    /// skipped and POINTS follows BONUS.
    /// </summary>
    /// <remarks>
    /// Each is given a different value, so a writer that transposed any two of
    /// them fails rather than passing by symmetry.
    /// </remarks>
    [Fact]
    public void EveryCounterRoundTripsInItsOwnPlace()
    {
        var state = WithStats(
            (StatIndex.Kills, 1),
            (StatIndex.Deaths, 2),
            (StatIndex.Caps, 3),
            (StatIndex.Assists, 4),
            (StatIndex.Destruction, 5),
            (StatIndex.Stabs, 6),
            (StatIndex.Healing, 7000),
            (StatIndex.Defenses, 8),
            (StatIndex.Invulns, 9),
            (StatIndex.Bonus, 10),
            (StatIndex.Points, 11));

        var player = RoundTrip(state).Players[Slot];

        Assert.Equal(1, player.Kills);
        Assert.Equal(2, player.Deaths);
        Assert.Equal(3, player.Caps);
        Assert.Equal(4, player.Assists);
        Assert.Equal(5, player.Destruction);
        Assert.Equal(6, player.Stabs);
        Assert.Equal(7000, player.Healing);
        Assert.Equal(8, player.Defenses);
        Assert.Equal(9, player.Invulns);
        Assert.Equal(10, player.Bonus);
        Assert.Equal(11, player.Points);
    }

    /// <summary>
    /// <b>Healing is the one counter with two bytes</b>, and it needs them: a
    /// medic passes 255 hit points in the first few seconds of a match.
    /// </summary>
    [Fact]
    public void HealingIsAUshortAndNotAByte()
    {
        var player = RoundTrip(WithStats((StatIndex.Healing, 40000))).Players[Slot];

        Assert.Equal(40000, player.Healing);
    }

    /// <summary>
    /// <b>A half point rounds on the wire, so a client's scoreboard can read
    /// one point above the host's.</b> The host draws
    /// <c>floor(stats[POINTS])</c> and the client draws what it was sent, which
    /// is <c>write_ubyte</c>'s half-away-from-zero.
    /// </summary>
    /// <remarks>
    /// The reference's, not a quantisation this port chose — and the reason
    /// <c>ScoreTableController</c>'s <c>floor</c> is not redundant.
    /// </remarks>
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 3)]
    [InlineData(2.4, 2)]
    public void AHalfPointRoundsUpOnTheWire(double points, int sent)
    {
        var player = RoundTrip(WithStats((StatIndex.Points, points))).Players[Slot];

        Assert.Equal(sent, player.Points);
    }

    /// <summary>
    /// A counter past 255 clamps rather than wrapping — <c>write_ubyte</c>'s
    /// clip, which is what keeps a 300-kill player from reading as 44.
    /// </summary>
    [Fact]
    public void ACounterPastAByteClampsRatherThanWrapping()
    {
        var player = RoundTrip(WithStats((StatIndex.Kills, 300))).Players[Slot];

        Assert.Equal(255, player.Kills);
    }

    /// <summary>
    /// A server with no scoreboard lookup at all writes zeroes and still frames
    /// — which is what every layout test built before the stats existed relies
    /// on.
    /// </summary>
    [Fact]
    public void NoScoreboardWritesZeroesAndStillFrames()
    {
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var roster = new Roster("server");
        roster.Add(new RosterEntry { Name = "p", Team = Team.Red, Class = PlayerClass.Soldier });
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, NoCharacters).Write(writer, MessageType.FullUpdate);

        var reader = new NetReader(writer.WrittenSpan[1..]);
        PlayerClass[] classes = [PlayerClass.Scout, PlayerClass.Soldier];
        var update = StateUpdateReader.Read(ref reader, MessageType.FullUpdate, Shape, classes);

        Assert.Equal(0, update.Players[Slot].Kills);
        Assert.Equal(0, update.Players[Slot].Points);
    }

    // --- ARENA_ENDROUND -------------------------------------------------------

    /// <summary>
    /// The MVP records ride behind the two counts, and a following message still
    /// frames — the control that the counts and the records agree.
    /// </summary>
    /// <remarks>
    /// Its counts are what a reader needs to know the length, so
    /// writing three records behind a count of zero would lose the stream from
    /// there on — and no recording could ever catch it, because none has been
    /// made on an arena map.
    /// </remarks>
    [Fact]
    public void ArenaEndRoundFramesItsMvpRecords()
    {
        var writer = new NetWriter();

        ReadOnlySpan<ArenaMvp> red =
        [
            new ArenaMvp(3, 9, 4000, 12),
            new ArenaMvp(1, 2, 0, 5),
        ];

        ReadOnlySpan<ArenaMvp> blue = [new ArenaMvp(7, 1, 65535, 200)];

        ServerMessages.ArenaEndRound(writer, Team.Red, 2, 1, red, blue);

        // A message whose own framing is unambiguous, so reading it back proves
        // the one above consumed exactly its own bytes.
        ServerMessages.UnlockControlPoints(writer);

        var stream = new ServerStream();
        var buffer = new ReceiveBuffer();
        buffer.Append(writer.WrittenSpan.ToArray());

        Assert.True(stream.TryRead(buffer, out var endRound));
        Assert.Equal(MessageType.ArenaEndRound, endRound.Type);

        // 5 header bytes + 3 records of 5. Payload excludes the message byte.
        Assert.Equal(5 + (3 * 5), endRound.Payload.Length);

        Assert.True(stream.TryRead(buffer, out var unlock));
        Assert.Equal(MessageType.UnlockCp, unlock.Type);
    }

    /// <summary>
    /// And an empty round — nobody on either team — still frames, which is what
    /// the port sent for every arena round before the scoreboard landed.
    /// </summary>
    [Fact]
    public void ArenaEndRoundWithNoMvpsStillFrames()
    {
        var writer = new NetWriter();

        ServerMessages.ArenaEndRound(writer, Team.Spectator, 0, 0);
        ServerMessages.UnlockControlPoints(writer);

        var stream = new ServerStream();
        var buffer = new ReceiveBuffer();
        buffer.Append(writer.WrittenSpan.ToArray());

        Assert.True(stream.TryRead(buffer, out var endRound));
        Assert.Equal(5, endRound.Payload.Length);

        Assert.True(stream.TryRead(buffer, out var unlock));
        Assert.Equal(MessageType.UnlockCp, unlock.Type);
    }
}
