using GG2.Maps;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The objective's bytes, through the real writer and the real reader.
/// </summary>
/// <remarks>
/// The same argument as <c>MineUpdateLayoutTests</c>: a field the server writes
/// and nothing reads is invisible until a client desynchronises, and a demoman's
/// mine blocks went unwritten for four phases exactly that way. Round-tripping
/// through <see cref="StateUpdateReader"/> is the only way to see them.
/// <para>
/// It matters more here than the byte count suggests, because the objective is
/// the <b>only</b> part of a state update <see cref="IntelFixtureTests"/> cannot
/// reach: a briefcase rides in a <c>FULL_UPDATE</c> and a recording holds
/// exactly one, sent on the join path before anything has happened.
/// </para>
/// </remarks>
public sealed class IntelUpdateLayoutTests
{
    private const string Map = "ctf_truefort";

    private static readonly WorldShape Shape =
        GameModes.ShapeOf(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    /// <summary>
    /// Writes an update for a world and reads it straight back, with the
    /// objective plumbed the way <c>GameServer</c> plumbs it.
    /// </summary>
    private static StateUpdate RoundTrip(in WorldState state, MessageType tier)
    {
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var roster = new Roster("server");
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));

        var captured = state;

        ObjectiveBlock Objective() => new(
            Intel.ToBlock(captured.RedIntel),
            Intel.ToBlock(captured.BlueIntel),
            captured.RedCaps,
            captured.BlueCaps);

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, NoCharacters, null, Objective).Write(writer, tier);

        // The writer emits the message-type byte and the reader starts at the
        // payload, so the first byte is skipped — see MineUpdateLayoutTests.
        var reader = new NetReader(writer.WrittenSpan[1..]);
        return StateUpdateReader.Read(ref reader, tier, Shape, []);
    }

    private static WorldState Placed()
    {
        var state = default(WorldState);
        Intel.Place(ref state, LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));
        return state;
    }

    /// <summary>
    /// A briefcase on the map round-trips its position and its return alarm.
    /// </summary>
    /// <remarks>
    /// The position is <c>round(x*5)</c>, so a briefcase dropped on a fractional
    /// coordinate loses a fifth of a pixel and nothing else.
    /// </remarks>
    [Fact]
    public void ABriefcaseOnTheMapRoundTripsItsPositionAndAlarm()
    {
        var state = Placed();
        state.BlueIntel.X = 1234.4;
        state.BlueIntel.Y = 567.6;
        state.BlueIntel.RecoverTimer = 812;

        var update = RoundTrip(state, MessageType.FullUpdate);

        Assert.Single(update.RedIntel);
        var red = update.RedIntel[0];
        Assert.Equal((ushort)Gml.Round(state.RedIntel.HomeX * 5), red.X);
        Assert.Equal((ushort)Gml.Round(state.RedIntel.HomeY * 5), red.Y);
        Assert.Equal(-1, red.RecoverTimer);

        var blue = Assert.Single(update.BlueIntel);
        Assert.Equal(6172, blue.X);
        Assert.Equal(2838, blue.Y);
        Assert.Equal(812, blue.RecoverTimer);
    }

    /// <summary>
    /// A carried briefcase writes a count of zero, and the update after it still
    /// frames.
    /// </summary>
    /// <remarks>
    /// <c>serialize(IntelligenceRed)</c> writes an instance count first and the
    /// client destroys and recreates exactly that many, so "carried" is a
    /// <em>shorter</em> message rather than a flagged one. Writing a block
    /// anyway would slip six bytes and surface hundreds of bytes later as a
    /// framing error, which is the failure mode docs/PROTOCOL.md's sentry
    /// section is about.
    /// </remarks>
    [Fact]
    public void ACarriedBriefcaseWritesNoBlockAndTheTailStillFrames()
    {
        var state = Placed();
        state.BlueIntel.OnField = false;
        state.RedCaps = 3;
        state.BlueCaps = 1;

        var update = RoundTrip(state, MessageType.FullUpdate);

        Assert.Single(update.RedIntel);
        Assert.Empty(update.BlueIntel);

        // Everything after the two instance lists: if the count had been wrong
        // these would be garbage rather than merely absent.
        Assert.Equal(3, update.RedCaps);
        Assert.Equal(1, update.BlueCaps);
        Assert.Equal(10, update.ClassLimits.Length);
    }

    /// <summary>
    /// A <c>CAPS_UPDATE</c> carries the two counters and nothing about the
    /// briefcases.
    /// </summary>
    /// <remarks>
    /// The extra tier <c>GameServerBeginStep.gml:88</c> sends when the scoreboard
    /// moves — a capture sets <c>GameServer.syncTimer = 1</c> — and every 3600
    /// frames regardless. It is written <em>in addition</em> to that tick's
    /// ordinary update, so a client sees two state messages on those ticks; the
    /// port only started sending it with the objective, and this is the gate on
    /// its layout.
    /// </remarks>
    [Fact]
    public void ACapsUpdateCarriesTheScoresAlone()
    {
        var state = Placed();
        state.RedCaps = 2;
        state.BlueCaps = 5;

        var update = RoundTrip(state, MessageType.CapsUpdate);

        Assert.Equal(2, update.RedCaps);
        Assert.Equal(5, update.BlueCaps);
        Assert.Empty(update.RedIntel);
        Assert.Empty(update.BlueIntel);
    }

    /// <summary>
    /// A cap count past 255 saturates rather than wrapping.
    /// </summary>
    /// <remarks>
    /// <c>write_ubyte</c> clamps, which is why <see cref="WorldState.RedCaps"/>
    /// is an <c>int</c> and the clamp lives at the wire. Unreachable in a real
    /// match — the round ends at <c>global.capLimit</c> — but a wrap would put a
    /// team back on zero, and this costs nothing.
    /// </remarks>
    [Fact]
    public void ACapCountPastAByteSaturates()
    {
        var state = Placed();
        state.RedCaps = 300;

        Assert.Equal(255, RoundTrip(state, MessageType.CapsUpdate).RedCaps);
    }
}
