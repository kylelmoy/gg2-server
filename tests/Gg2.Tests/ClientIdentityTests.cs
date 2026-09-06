using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Whether a client can work out <em>which player it is</em>, and keep knowing
/// it while the roster moves underneath it.
/// </summary>
/// <remarks>
/// <b>The stock client answers that question exactly once.</b>
/// <c>ClientBeginStep.gml:206-208</c> binds <c>global.myself</c> on the single
/// <c>PLAYER_JOIN</c> that lands at <c>ds_list_size(global.players)-1 ==
/// global.playerID</c>; there is no retry and no correction. A client that is
/// out of step at that instant keeps the <c>-1</c> <c>ClientCreate.gml:20</c>
/// gave it for the rest of the session, and then every
/// <c>global.myself.object</c> in the game is a GM8 error — which is what
/// <c>Spectator</c>'s End Step and <c>AmmoCounter</c>'s Draw and Begin Step do
/// on their first frame.
/// <para>
/// <b>Nothing in this repo modelled that before.</b> <c>ServerStream</c> read
/// <c>JOIN_UPDATE</c> into <c>OwnPlayerId</c> and then left it alone, so it went
/// stale the moment a lower slot left — a dormant accessor nothing consumed,
/// which is exactly the shape <c>CLAUDE.md</c> warns about: no test could go red
/// over it. docs/PARITY.md, "The gap every automated gate has".
/// </para>
/// <para>
/// ⚠️ <b>These are client-identity tests, not roster tests.</b>
/// <see cref="RosterCompactionClientTests"/> asks whether the roster compacts
/// correctly; this file asks whether the client still knows where it is in it.
/// The two can disagree — a roster that compacts perfectly still leaves a client
/// pointing at the wrong player if its own index does not move with it.
/// </para>
/// </remarks>
public sealed class ClientIdentityTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static ServerHarness Harness(int bots = 0) =>
        new(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
            options.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            options.Bots = new BotPopulationOptions(
                Enabled: bots > 0,
                FillToPlayers: bots + 1,
                MaxBots: bots,
                MinHumans: 0,
                Difficulty: 3);
        });

    /// <summary>
    /// A joining client is told an id, and a <c>PLAYER_JOIN</c> lands on it.
    /// </summary>
    /// <remarks>
    /// The control for everything below. It also pins the ordering the whole
    /// join depends on: the joiner's own <c>PLAYER_JOIN</c> must be the FIRST
    /// message it is sent after its burst, because the bind is positional. A
    /// roster message slipping in front of it — from
    /// <c>BroadcastEndStepEvents</c>, the control port or the web admin, which
    /// are the only writers between the flush and the burst — would move the
    /// count under it and leave <c>global.myself</c> unbound for ever.
    /// <c>ClientPlayer.SkipBroadcastBytes</c> is what keeps that window shut.
    /// </remarks>
    [Fact]
    public void AJoiningClientBindsItselfToTheSlotTheServerNamed()
    {
        using var harness = Harness(bots: 4);

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, Map);

        harness.Tick(30);
        mirror.Pump();

        var slot = harness.Server.RosterCount - 1;
        Assert.Equal((byte)slot, mirror.OwnPlayerId);
        Assert.True(mirror.SelfBound, "the client never bound global.myself");
        Assert.Empty(mirror.Impossible);

        // Not vacuous: the id has to be a real slot with this client's name on
        // it, or "byte 4 == byte 4" would pass against an empty roster.
        Assert.Equal("watcher", mirror.Names[slot]);
        Assert.True(slot >= 4, $"expected the bots ahead of the joiner; slot was {slot}");

        // The first thing after the burst's FULL_UPDATE is the joiner's own
        // PLAYER_JOIN. This is the ordering the bind depends on.
        var full = mirror.Messages
            .Select((m, i) => (m, i))
            .Last(t => t.m.Type == MessageType.FullUpdate)
            .i;

        Assert.Equal(MessageType.PlayerJoin, mirror.Messages[full + 1].Type);
        Assert.Equal("watcher", mirror.Messages[full + 1].Text);
    }

    /// <summary>
    /// An earlier slot leaving moves this client's own id down with it.
    /// </summary>
    /// <remarks>
    /// <c>ClientBeginStep.gml:218-220</c>:
    /// <c>if(playerID &lt; global.playerID) global.playerID -= 1</c>. Both sides
    /// compact, so a client that held its joined-at id would name a different
    /// player from the server for the rest of the session — and, because the
    /// bind has already happened, it would do so silently.
    /// <para>
    /// ⛔ <b>The mutation check</b>: deleting the decrement in
    /// <c>ServerStream</c>'s <c>PLAYER_LEAVE</c> case turns this red.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEarlierSlotLeavingMovesThisClientsOwnIdDownWithIt()
    {
        using var harness = Harness();

        // The leaver must sit BELOW the survivor or the index never moves and
        // the assertion is satisfied by doing nothing.
        var leaver = harness.JoinClient("leaver");
        var survivor = harness.JoinClient("survivor");
        using var mirror = new MirrorClient(survivor, Load, Map);

        harness.Tick(10);
        mirror.Pump();

        var joinedAt = harness.Server.RosterCount - 1;
        Assert.Equal((byte)joinedAt, mirror.OwnPlayerId);
        Assert.True(mirror.SelfBound);

        var before = harness.Server.RosterCount;
        leaver.Dispose();
        harness.TickUntil(
            () => harness.Server.RosterCount < before,
            "the leaver to be removed from the roster",
            maxTicks: 400);

        harness.Tick(10);
        mirror.Pump();

        // The server slid the survivor down one; the client's own id followed.
        var moved = joinedAt - 1;
        Assert.Equal(moved, harness.Server.RosterCount - 1);
        Assert.Equal((byte)moved, mirror.OwnPlayerId);
        Assert.Equal("survivor", mirror.Names[mirror.OwnPlayerId!.Value]);
        Assert.Empty(mirror.Impossible);
    }

    /// <summary>
    /// A LATER slot leaving does not move this client's id.
    /// </summary>
    /// <remarks>
    /// The other half of the strict inequality, and the reason it is strict. A
    /// reader that decremented on <c>&lt;=</c>, or on every departure, would
    /// pass the test above and be wrong here — and wrong in the direction that
    /// makes a client believe it is the player below itself.
    /// </remarks>
    [Fact]
    public void ALaterSlotLeavingLeavesThisClientsOwnIdAlone()
    {
        using var harness = Harness();

        var survivor = harness.JoinClient("survivor");
        var leaver = harness.JoinClient("leaver");
        using var mirror = new MirrorClient(survivor, Load, Map);

        harness.Tick(10);
        mirror.Pump();

        var joinedAt = harness.Server.RosterCount - 2;
        Assert.Equal((byte)joinedAt, mirror.OwnPlayerId);

        var before = harness.Server.RosterCount;
        leaver.Dispose();
        harness.TickUntil(
            () => harness.Server.RosterCount < before,
            "the leaver to be removed from the roster",
            maxTicks: 400);

        harness.Tick(10);
        mirror.Pump();

        Assert.Equal((byte)joinedAt, mirror.OwnPlayerId);
        Assert.Equal("survivor", mirror.Names[joinedAt]);
        Assert.Empty(mirror.Impossible);
    }

    /// <summary>
    /// A kick in flight does not cost a joiner its identity.
    /// </summary>
    /// <remarks>
    /// <c>docs/PARITY.md</c>, "Multi-area maps", named an operator kick as the
    /// one known way to put a roster message between the broadcast flush
    /// (<c>GameServer.cs:802</c>) and a joiner's burst (<c>:822</c>). It is not:
    /// <c>KickPlayer</c> only closes the socket, and the <c>PLAYER_LEAVE</c> is
    /// written by <c>RemoveClient</c> out of <c>ServiceClients</c> at
    /// <c>:697</c> — above the flush, in the following tick. This test is what
    /// says so from the joiner's own point of view.
    /// <para>
    /// ⚠️ <b>The assertion is the joiner's identity, not the roster count.</b>
    /// A count that ends up right hides an ordering that was wrong: the bind is
    /// positional and happens once, so a <c>PLAYER_LEAVE</c> arriving one
    /// message too early is invisible to a count taken afterwards and fatal to
    /// the client.
    /// </para>
    /// </remarks>
    [Fact]
    public void AKickInFlightDoesNotCostAJoinerItsIdentity()
    {
        using var harness = Harness(bots: 4);

        var doomed = harness.JoinClient("doomed");
        harness.Tick(10);

        var doomedSlot = harness.Server.RosterCount - 1;
        Assert.Equal(KickOutcome.Kicked, harness.Server.KickPlayer(doomedSlot, "test"));

        // The join is driven from the tick the kick was issued in, so the
        // joiner's burst and the departing client's PLAYER_LEAVE are in flight
        // together.
        var joiner = harness.JoinClient("joiner");
        using var mirror = new MirrorClient(joiner, Load, Map);

        harness.TickUntil(
            () => doomed.IsClosed,
            "the kicked client to be dropped",
            maxTicks: 400);

        harness.Tick(30);
        mirror.Pump();

        var slot = harness.Server.RosterCount - 1;
        Assert.Equal((byte)slot, mirror.OwnPlayerId);
        Assert.True(mirror.SelfBound, "the joiner never bound global.myself across the kick");
        Assert.Equal("joiner", mirror.Names[slot]);
        Assert.Equal(harness.Server.RosterCount, mirror.PlayerCount);
        Assert.Empty(mirror.Impossible);
    }
}
