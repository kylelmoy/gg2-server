using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Phase 1 of docs/PLAN.md: a stock client gets through the handshake, joins,
/// and is not disconnected.
/// </summary>
/// <remarks>
/// These drive a real <c>GameServer</c> over a real loopback socket, with the
/// tick supplied by the test rather than a clock. What they cannot check is the
/// only thing that finally settles phase 1 — that the <em>stock GM8 client</em>
/// is happy — so they assert the exact bytes docs/PROTOCOL.md says that client
/// reads, field by field, in order.
/// </remarks>
public sealed class HandshakeTests
{
    [Fact]
    public void GreetsAClientThatSendsTheRightProtocolUuid()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the server greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        Assert.Equal("test server", client.TakeString1());
        Assert.Equal("ctf_truefort", client.TakeString1());

        // An empty MD5 means "built into the client", which is what keeps the
        // whole map-download path out of scope.
        Assert.Equal(string.Empty, client.TakeString1());
        Assert.Equal(0, client.TakeByte());
        Assert.Equal(string.Empty, client.TakeString2());
        Assert.Empty(client.Received);
    }

    [Fact]
    public void RejectsAWrongProtocolUuidAndDropsTheConnection()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHelloWithWrongUuid();
        harness.TickUntil(() => client.Received.Count > 0, "the rejection");

        Assert.Equal(MessageType.IncompatibleProtocol, client.TakeMessageType());

        harness.TickUntil(() => client.IsClosed, "the connection to be dropped");
        Assert.Equal(1, harness.Server.PlayerCount); // slot 0 only
    }

    [Fact]
    public void AsksForAPasswordWhenOneIsSet()
    {
        using var harness = new ServerHarness(o => o.Password = "hunter2");
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the password request");
        Assert.Equal(MessageType.PasswordRequest, client.TakeMessageType());

        client.SendPassword("hunter2");
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        Assert.Equal(MessageType.Hello, client.TakeMessageType());
    }

    /// <summary>
    /// A wrong password is not retryable. <c>serviceJoiningPlayer.gml:70-78</c>
    /// writes <c>PASSWORD_WRONG</c> and sets no next state, so line 186 destroys
    /// the connection — and the client destroys itself on receiving it.
    /// </summary>
    [Fact]
    public void DropsTheConnectionOnAWrongPassword()
    {
        using var harness = new ServerHarness(o => o.Password = "hunter2");
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the password request");
        Assert.Equal(MessageType.PasswordRequest, client.TakeMessageType());

        client.SendPassword("wrong");
        harness.TickUntil(() => client.Received.Count > 0, "the rejection");
        Assert.Equal(MessageType.PasswordWrong, client.TakeMessageType());

        harness.TickUntil(() => client.IsClosed, "the connection to be dropped");
    }

    [Fact]
    public void RequiresReserveSlotBeforePlayerJoin()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.Take(client.Received.Count);

        client.SendPlayerJoin();
        harness.TickUntil(() => client.IsClosed, "the connection to be dropped");

        Assert.Equal(1, harness.Server.PlayerCount);
    }

    /// <summary>
    /// <c>PING</c> keeps a joining connection alive while the client loads
    /// plugins, and needs no prior <c>RESERVE_SLOT</c>.
    /// </summary>
    [Fact]
    public void PingKeepsAJoiningConnectionAlive()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.Take(client.Received.Count);

        for (var i = 0; i < 5; i++)
        {
            client.SendPing();
            harness.Tick(2);
        }

        Assert.False(client.IsClosed);

        client.SendReserveSlot("late");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, client.TakeMessageType());
    }

    [Fact]
    public void RefusesAJoinWhenEverySeatIsTaken()
    {
        using var harness = new ServerHarness(o => o.PlayerLimit = 1);

        harness.JoinClient("first");

        var second = harness.Connect();
        second.SendHello();
        harness.TickUntil(() => second.Received.Count > 0, "the greeting");
        second.Take(second.Received.Count);

        second.SendReserveSlot("second");
        harness.TickUntil(() => second.Received.Count > 0, "the refusal");

        Assert.Equal(MessageType.ServerFull, second.TakeMessageType());
        harness.TickUntil(() => second.IsClosed, "the connection to be dropped");
    }

    /// <summary>
    /// A client that reserves a seat and then vanishes GIVES THE SEAT BACK —
    /// without waiting out the thirty-second idle timeout.
    /// </summary>
    /// <remarks>
    /// <c>RESERVE_SLOT</c> takes a seat before <c>PLAYER_JOIN</c> does anything
    /// (<c>OccupiesSlot</c>, counted by <c>OccupiedSlots</c>), so a connection
    /// abandoned in that window is holding one. <c>JoiningConnection.Service</c>
    /// answers <c>Finished</c> on a closed socket and
    /// <c>ServiceJoiningConnections</c> drops it — but nothing was checking that
    /// the seat came back.
    /// <para>
    /// ⚠️ <b>The failure would be silent and cumulative.</b> Every abandoned
    /// handshake — a player who closes the window on the class screen, a
    /// flaky connection, a port scanner — would cost a seat permanently, and a
    /// long-running server would drift towards refusing real players with
    /// <c>SERVER_FULL</c> while looking empty. That is a production-only,
    /// intermittent symptom, which is exactly the kind this suite cannot
    /// otherwise reach.
    /// </para>
    /// <para>
    /// ⛔ <see cref="AnAbandonedReservationWouldOtherwiseHaveFilledTheServer"/>
    /// is the control: with the ghost still connected the same second client
    /// IS refused, so this test cannot pass merely because the limit never bit.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnAbandonedReservationGivesTheSeatBack()
    {
        using var harness = new ServerHarness(o => o.PlayerLimit = 1);

        var ghost = harness.Connect();
        ghost.SendHello();
        harness.TickUntil(() => ghost.Received.Count > 0, "the greeting");
        ghost.Take(ghost.Received.Count);

        ghost.SendReserveSlot("ghost");
        harness.TickUntil(() => ghost.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, ghost.TakeMessageType());

        // It never sends PLAYER_JOIN — it just goes away holding the seat.
        ghost.Dispose();
        harness.Tick(30);

        // The only seat must be available again.
        harness.JoinClient("real");
        Assert.Equal(Team.Spectator, harness.Server.TeamOf(harness.Server.RosterCount - 1));
    }

    /// <summary>
    /// The control: while the ghost is still connected, that seat really is
    /// taken.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="AnAbandonedReservationGivesTheSeatBack"/> passes
    /// on a server whose <c>PlayerLimit</c> was never enforced at all.
    /// </remarks>
    [Fact]
    public void AnAbandonedReservationWouldOtherwiseHaveFilledTheServer()
    {
        using var harness = new ServerHarness(o => o.PlayerLimit = 1);

        var ghost = harness.Connect();
        ghost.SendHello();
        harness.TickUntil(() => ghost.Received.Count > 0, "the greeting");
        ghost.Take(ghost.Received.Count);

        ghost.SendReserveSlot("ghost");
        harness.TickUntil(() => ghost.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, ghost.TakeMessageType());

        // Still connected, still holding the seat.
        var second = harness.Connect();
        second.SendHello();
        harness.TickUntil(() => second.Received.Count > 0, "the greeting");
        second.Take(second.Received.Count);

        second.SendReserveSlot("second");
        harness.TickUntil(() => second.Received.Count > 0, "the refusal");

        Assert.Equal(MessageType.ServerFull, second.TakeMessageType());
    }

    /// <summary>
    /// Slot 0 is the server's own reserved entry
    /// (<c>GameServerCreate.gml:36-40</c>) and does not count against the player
    /// limit in dedicated mode (<c>getNumberOfOccupiedSlots</c>). Get either
    /// half wrong and every index a client receives is off by one.
    /// </summary>
    [Fact]
    public void SlotZeroIsReservedAndDoesNotCountAgainstTheLimit()
    {
        using var harness = new ServerHarness(o => o.PlayerLimit = 2);

        Assert.Equal(1, harness.Server.PlayerCount);

        harness.JoinClient("one");
        Assert.Equal(2, harness.Server.PlayerCount);

        harness.JoinClient("two");
        Assert.Equal(3, harness.Server.PlayerCount);
    }

    [Fact]
    public void TruncatesAnOverlongNameToTwentyBytes()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.Take(client.Received.Count);

        var longName = new string('x', 40);
        client.SendReserveSlot(longName);
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        client.Take(client.Received.Count);

        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > 1, "the join");

        Assert.Contains(new string('x', GameConstants.MaxPlayerNameLength) + " joined", harness.Log.Lines[^1]);
    }
}
