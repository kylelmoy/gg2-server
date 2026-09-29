using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The <c>FULL_UPDATE</c> tail on every built-in map.
/// </summary>
/// <remarks>
/// The tail is the one part of the state update whose <em>shape</em> changes
/// with the map: which HUD <c>basicRoomSetup.gml</c> would have created decides
/// the block, and each mode's block is a different width with a different number
/// of control-point sub-blocks after it. A wrong width there is invisible until
/// a client joins that particular map and then desynchronises permanently.
/// <para>
/// So this stands a real server up on each of the 22 shipped maps, joins a real
/// client, and reads the burst field by field — asserting the stream ends
/// exactly where the layout says it should.
/// </para>
/// </remarks>
public sealed class FullUpdateLayoutTests
{
    public static TheoryData<MapFixture> Maps => MapFixtures.AsTheoryData();

    [Theory]
    [MemberData(nameof(Maps))]
    public void FullUpdateTailMatchesTheMapsGameMode(MapFixture fixture)
    {
        // ForArea(1), like the server. A tall map is several rounds stacked
        // vertically and only one area's entities exist at a time — so the
        // *unnarrowed* cp_dirtbowl has six control points and the round being
        // played has two. Framing an update by the wrong count desynchronises
        // the stream; see LevelMap.ForArea and GameModes.ShapeOf.
        var map = LevelMap.FromPng(fixture.Name, ReferenceTree.ReadMap(fixture.Name)).ForArea(1);
        var mode = GameModes.Of(map);

        using var harness = new ServerHarness(o => o.Map = fixture.Name);
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.Take(client.Received.Count);

        client.SendReserveSlot("layout");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        client.Take(client.Received.Count);

        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > 1, "the join");

        // JOIN_UPDATE, CHANGE_MAP, then one roster entry (slot 0).
        Assert.Equal(MessageType.JoinUpdate, client.TakeMessageType());
        client.Take(2);
        Assert.Equal(MessageType.ChangeMap, client.TakeMessageType());
        client.TakeString1();
        client.TakeString1();
        Assert.Equal(MessageType.PlayerJoin, client.TakeMessageType());
        client.TakeString1();
        Assert.Equal(MessageType.PlayerChangeClass, client.TakeMessageType());
        client.Take(2);
        Assert.Equal(MessageType.PlayerChangeTeam, client.TakeMessageType());
        client.Take(2);

        Assert.Equal(MessageType.FullUpdate, client.TakeMessageType());
        client.TakeUShort();
        Assert.Equal(1, client.TakeByte()); // playerCount

        // The one existing player: full stats, no rewards, no dominations (it
        // is the only entry), no subobjects.
        client.Take(6);
        client.TakeUShort();
        client.Take(4);
        client.TakeByte();
        Assert.Equal(string.Empty, client.TakeString2());
        Assert.Equal(0, client.TakeByte());

        // MovingPlatform blocks: five bytes each, none on any shipped map.
        Assert.Equal(0, CountOf(map, EntityTypes.MovingPlatform));

        // serialize(IntelligenceRed) then Blue: a ushort count, then six bytes
        // each. Only a CTF-shaped map has any.
        var intelExpected = mode is GameMode.CaptureTheFlag or GameMode.Invasion ? 1 : 0;
        Assert.Equal(intelExpected, client.TakeUShort());
        client.Take(6 * intelExpected);
        Assert.Equal(intelExpected, client.TakeUShort());
        client.Take(6 * intelExpected);

        client.Take(4); // caplimit, redCaps, blueCaps, respawn time

        ReadHudBlock(client, map, mode);

        client.Take(10); // class limits

        // Nothing left over and nothing short: the next byte, if any, must be
        // the start of the following message rather than the middle of this one.
        Assert.True(
            client.Received.Count == 0 || (MessageType)client.Received[0] is MessageType.PlayerJoin,
            $"{fixture.Name}: {client.Received.Count} unexpected trailing byte(s) after the FULL_UPDATE");
    }

    /// <summary>
    /// The gamemode HUD block, written out independently of the server's own
    /// <c>StateUpdateWriter</c> so that agreement means something.
    /// </summary>
    private static void ReadHudBlock(LoopbackClient client, LevelMap map, GameMode mode)
    {
        switch (mode)
        {
            case GameMode.CaptureTheFlag:
                client.TakeByte();  // timeLimitMins
                client.TakeUInt();  // timer
                break;

            case GameMode.Invasion:
                client.TakeByte();
                client.TakeUInt();
                client.TakeUShort(); // setupTimer
                break;

            case GameMode.Generator:
                client.TakeByte();
                client.TakeUInt();
                client.Take(4); // blue generator: hp, shieldHp
                client.Take(4); // red generator
                break;

            case GameMode.Arena:
                // FULL_UPDATE-only prefix: ubyte redWins, ubyte blueWins,
                // ubyte state, byte winners, ushort endCount.
                client.Take(6);
                client.TakeByte();   // timeLimitMins
                client.TakeUInt();   // timer
                client.TakeUShort(); // cpUnlock
                client.TakeByte();   // floor(roundStart / 2)
                TakeControlPoints(client, CountOf(map, EntityTypes.ArenaControlPoint));
                break;

            case GameMode.KingOfTheHill:
                client.TakeUShort(); // cpUnlock
                client.TakeUShort(); // redTimer
                client.TakeUShort(); // blueTimer
                TakeControlPoints(client, CountOf(map, EntityTypes.KothControlPoint));
                break;

            case GameMode.DualKingOfTheHill:
                client.TakeUShort();
                client.TakeUShort();
                client.TakeUShort();
                TakeControlPoints(client,
                    CountOf(map, EntityTypes.KothRedControlPoint) + CountOf(map, EntityTypes.KothBlueControlPoint));
                break;

            case GameMode.ControlPoints:
                client.TakeByte();
                client.TakeUInt();
                client.TakeUShort(); // setupTimer
                TakeControlPoints(client, NumberedControlPoints(map));
                break;

            case GameMode.TeamDeathmatch:
                client.TakeByte();
                client.TakeUInt();
                client.TakeUShort(); // killLimit
                break;

            default:
                Assert.Fail($"no HUD layout for {mode}");
                break;
        }
    }

    /// <summary>byte team, byte cappingTeam, ushort capping.</summary>
    private static void TakeControlPoints(LoopbackClient client, int count) => client.Take(4 * count);

    private static int NumberedControlPoints(LevelMap map) =>
        CountOf(map, EntityTypes.ControlPoint1)
        + CountOf(map, EntityTypes.ControlPoint2)
        + CountOf(map, EntityTypes.ControlPoint3)
        + CountOf(map, EntityTypes.ControlPoint4)
        + CountOf(map, EntityTypes.ControlPoint5);

    private static int CountOf(LevelMap map, string type) => map.OfType(type).Count();
}
