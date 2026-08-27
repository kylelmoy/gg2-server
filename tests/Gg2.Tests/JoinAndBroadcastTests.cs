using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The join burst and the state-update cadence — the two things a stock client
/// silently depends on and loudly breaks over.
/// </summary>
/// <remarks>
/// The client's <c>deserializeState</c> reads fixed-width fields with no
/// framing of any kind, so one wrong byte does not corrupt one message: it
/// shifts every message after it forever. There is no recovery and no error
/// message except "Wrong number of players while deserializing state", which is
/// why these tests read every field rather than sampling.
/// </remarks>
public sealed class JoinAndBroadcastTests
{
    /// <summary>
    /// <c>Scripts/Message Builders/ServerJoinUpdate.gml</c> sends far more than
    /// a <c>JOIN_UPDATE</c>: the whole existing roster is replayed, and a
    /// <c>CHANGE_MAP</c> is what makes the client load the map at all.
    /// </summary>
    [Fact]
    public void JoinBurstReplaysTheWholeRosterBeforeTheFullUpdate()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.Take(client.Received.Count);

        client.SendReserveSlot("joiner");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        client.Take(client.Received.Count);

        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > 1, "the join");

        // 1. JOIN_UPDATE: the joiner's id is the roster size *before* the join,
        //    so with only slot 0 present it is 1.
        Assert.Equal(MessageType.JoinUpdate, client.TakeMessageType());
        Assert.Equal(1, client.TakeByte());
        Assert.Equal(1, client.TakeByte()); // currentMapArea

        // 2. CHANGE_MAP, with an empty MD5 meaning "built-in".
        Assert.Equal(MessageType.ChangeMap, client.TakeMessageType());
        Assert.Equal("ctf_truefort", client.TakeString1());
        Assert.Equal(string.Empty, client.TakeString1());

        // 3. The existing roster, one entry at a time: slot 0 only.
        Assert.Equal(MessageType.PlayerJoin, client.TakeMessageType());
        Assert.Equal("Server", client.TakeString1());

        Assert.Equal(MessageType.PlayerChangeClass, client.TakeMessageType());
        Assert.Equal(0, client.TakeByte());
        Assert.Equal((byte)PlayerClass.Scout, client.TakeByte());

        Assert.Equal(MessageType.PlayerChangeTeam, client.TakeMessageType());
        Assert.Equal(0, client.TakeByte());
        Assert.Equal((byte)Team.Spectator, client.TakeByte());

        // 4. FULL_UPDATE, for the one player the client now knows about.
        ReadFullUpdate(client, playerCount: 1);

        // 5. Only now does the joiner hear about itself, on the broadcast.
        harness.TickUntil(() => client.Received.Count > 0, "the join broadcast");
        Assert.Equal(MessageType.PlayerJoin, client.TakeMessageType());
        Assert.Equal("joiner", client.TakeString1());
    }

    /// <summary>
    /// Six ticks out of seven carry only inputs; the seventh carries positions.
    /// The client dead-reckons between them, so the cadence is contract, not
    /// implementation (<c>GameServerBeginStep.gml:88-95</c>).
    /// </summary>
    [Fact]
    public void SendsQuickUpdateEverySeventhTickAndInputStateOtherwise()
    {
        using var harness = new ServerHarness();
        var client = harness.JoinClient("watcher");

        // Get to a known point in the stream, then read exactly 21 updates.
        harness.Tick();
        client.Take(client.Received.Count);

        var startFrame = harness.Server.Frame;
        var tiers = new List<MessageType>();

        for (var i = 0; i < 21; i++)
        {
            harness.Tick();
            tiers.Add(ReadStateUpdate(client, playerCount: 2));
            Assert.Empty(client.Received);
        }

        for (var i = 0; i < tiers.Count; i++)
        {
            var frame = startFrame + 1 + i;
            var expected = frame % 7 == 0 ? MessageType.QuickUpdate : MessageType.InputState;
            Assert.Equal(expected, tiers[i]);
        }

        Assert.Contains(MessageType.QuickUpdate, tiers);
    }

    /// <summary>
    /// Every client gets the same bytes: one serialisation, N sends
    /// (docs/PROTOCOL.md, "Transport").
    /// </summary>
    [Fact]
    public void EveryClientReceivesTheIdenticalBroadcastStream()
    {
        using var harness = new ServerHarness();
        var first = harness.JoinClient("first");
        var second = harness.JoinClient("second");

        harness.Tick();
        first.Take(first.Received.Count);
        second.Take(second.Received.Count);

        harness.Tick(14);

        Assert.NotEmpty(first.Received);
        Assert.Equal(first.Received, second.Received);
    }

    [Fact]
    public void PlayerLeaveIsBroadcastAndTheRosterCompacts()
    {
        using var harness = new ServerHarness();
        var first = harness.JoinClient("first");
        var second = harness.JoinClient("second");

        harness.Tick();
        second.Take(second.Received.Count);

        Assert.Equal(3, harness.Server.PlayerCount);

        // "first" is roster index 1; index 2 becomes index 1 on both sides.
        first.SendCommand(MessageType.PlayerLeave);
        harness.TickUntil(() => harness.Server.PlayerCount == 2, "the leave to be processed");

        Assert.Equal(MessageType.PlayerLeave, second.TakeMessageType());
        Assert.Equal(1, second.TakeByte());

        // The state update on that same tick must already carry the new count,
        // or the client aborts deserialising.
        Assert.Equal(2, PlayerCountOfNextUpdate(second));
    }

    [Fact]
    public void ChangeTeamIsEchoedToEveryone()
    {
        using var harness = new ServerHarness();
        var first = harness.JoinClient("first");
        var second = harness.JoinClient("second");

        harness.Tick();
        first.Take(first.Received.Count);
        second.Take(second.Received.Count);

        first.SendChangeTeam(Team.Red);
        harness.Tick(2);

        Assert.Equal(MessageType.PlayerChangeTeam, second.TakeMessageType());
        Assert.Equal(1, second.TakeByte());
        Assert.Equal((byte)Team.Red, second.TakeByte());
    }

    /// <summary>
    /// An unrecognised command byte is ignored and the stream carries on — the
    /// reference does not resynchronise and does not kick
    /// (<c>processClientCommands.gml</c>). A stricter port would disconnect
    /// clients the reference tolerates.
    /// </summary>
    [Fact]
    public void GarbageBytesAreIgnoredWithoutBreakingTheStream()
    {
        using var harness = new ServerHarness();
        var client = harness.JoinClient("noisy");
        var other = harness.JoinClient("other");

        harness.Tick();
        other.Take(other.Received.Count);

        // 26, 29, 48, 49 are unallocated in Constants.xml; 200 is far outside.
        client.Send(new byte[] { 26, 29, 48, 49, 200 });
        client.SendChangeTeam(Team.Blue);
        harness.Tick(3);

        Assert.False(client.IsClosed);
        Assert.Equal(3, harness.Server.PlayerCount);

        Assert.Equal(MessageType.PlayerChangeTeam, other.TakeMessageType());
        Assert.Equal(1, other.TakeByte());
        Assert.Equal((byte)Team.Blue, other.TakeByte());
    }

    /// <summary>
    /// At most ten completed commands per player per tick; the rest stay
    /// buffered rather than being dropped.
    /// </summary>
    [Fact]
    public void ProcessesAtMostTenCommandsPerTick()
    {
        using var harness = new ServerHarness();
        var client = harness.JoinClient("flooder");
        var other = harness.JoinClient("other");

        harness.Tick();
        other.Take(other.Received.Count);

        // Twenty commands that each produce exactly one broadcast message.
        for (var i = 0; i < 20; i++)
            client.SendChangeTeam(i % 2 == 0 ? Team.Red : Team.Blue);

        harness.Tick();
        Assert.Equal(10, CountChangeTeamMessages(other, playerCount: 3));

        harness.Tick();
        Assert.Equal(10, CountChangeTeamMessages(other, playerCount: 3));
    }

    [Fact]
    public void StaysConnectedOverFiveMinutesOfTicks()
    {
        using var harness = new ServerHarness();
        var client = harness.JoinClient("patient");

        // Five minutes at 30 Hz. The point is the handshake timeout does not
        // reach an established player and nothing in the loop trips over itself.
        for (var i = 0; i < 30 * 60 * 5; i++)
        {
            harness.Server.Tick();

            // Drain periodically so the socket buffer cannot fill and stall.
            if (i % 30 == 0)
            {
                client.Poll();
                client.Take(client.Received.Count);
            }
        }

        client.Poll();
        Assert.False(client.IsClosed);
        Assert.Equal(2, harness.Server.PlayerCount);
    }

    // --- readers -------------------------------------------------------------

    /// <summary>
    /// Reads one state update and returns its tier, failing if a single field is
    /// the wrong width.
    /// </summary>
    private static MessageType ReadStateUpdate(LoopbackClient client, int playerCount)
    {
        var tier = client.TakeMessageType();

        switch (tier)
        {
            case MessageType.InputState:
            case MessageType.QuickUpdate:
                Assert.Equal(playerCount, client.TakeByte());
                for (var i = 0; i < playerCount; i++)
                    TakeSubobjects(client, tier);
                return tier;

            case MessageType.FullUpdate:
                ReadFullUpdateBody(client, playerCount);
                return tier;

            default:
                Assert.Fail($"expected a state update, got {tier}");
                return tier;
        }
    }

    private static void ReadFullUpdate(LoopbackClient client, int playerCount)
    {
        Assert.Equal(MessageType.FullUpdate, client.TakeMessageType());
        ReadFullUpdateBody(client, playerCount);
    }

    private static void ReadFullUpdateBody(LoopbackClient client, int playerCount)
    {
        client.TakeUShort(); // tdmInvulnerabilityTicks
        Assert.Equal(playerCount, client.TakeByte());

        for (var i = 0; i < playerCount; i++)
        {
            client.Take(6);      // kills..stabs
            client.TakeUShort(); // healing
            client.Take(4);      // defenses, invulns, bonus, points
            client.TakeByte();   // queueJump
            Assert.Equal(string.Empty, client.TakeString2()); // rewards
            client.Take(playerCount - 1);                     // domination kills
            TakeSubobjects(client, MessageType.FullUpdate);
        }

        // No moving platforms on any shipped map, so no platform blocks.

        // ctf_truefort: one red intel and one blue.
        Assert.Equal(1, client.TakeUShort());
        client.Take(6);
        Assert.Equal(1, client.TakeUShort());
        client.Take(6);

        client.Take(4); // caplimit, redCaps, blueCaps, respawn time

        // CTFHUD: ubyte timeLimitMins, uint timer.
        client.TakeByte();
        client.TakeUInt();

        client.Take(10); // class limits
    }

    /// <summary>
    /// Consumes one player's subobjects byte and whatever it says follows.
    /// </summary>
    /// <remarks>
    /// A player who has picked a team has a character now — phase 4 wired the
    /// simulation into the server — so this is no longer the single zero byte it
    /// was through phases 1 to 3. Sentries are still phase 5, so bit 1 never
    /// appears.
    /// </remarks>
    private static void TakeSubobjects(LoopbackClient client, MessageType tier)
    {
        var subobjects = client.TakeByte();
        Assert.True(subobjects is 0 or 1, $"unexpected subobjects byte {subobjects}");

        if (subobjects == 0)
            return;

        client.Take(4); // keys, aimDirection, aimDistance

        if (tier is not (MessageType.QuickUpdate or MessageType.FullUpdate))
            return;

        client.Take(9); // x, y, hspeed, vspeed, hp, ammo, flags

        if (tier != MessageType.FullUpdate)
            return;

        client.Take(7); // animation offset, class-specific, alarm[1], intel, recharge

        // Every player in these tests is a scout, and only Medigun and Minegun
        // write a weapon block — so there is nothing more to consume here.
    }

    private static int PlayerCountOfNextUpdate(LoopbackClient client)
    {
        var tier = client.TakeMessageType();
        Assert.True(tier is MessageType.InputState or MessageType.QuickUpdate, $"unexpected {tier}");
        return client.TakeByte();
    }

    private static int CountChangeTeamMessages(LoopbackClient client, int playerCount)
    {
        var count = 0;

        while (client.Received.Count > 0)
        {
            if ((MessageType)client.Received[0] == MessageType.PlayerChangeTeam)
            {
                client.Take(3);
                count++;
                continue;
            }

            // Picking a team spawns a character, and the spawn is announced
            // before the next update goes out.
            if ((MessageType)client.Received[0] == MessageType.PlayerSpawn)
            {
                client.Take(4);
                continue;
            }

            // A team change with a live character is a death since phase 6 —
            // BID_FAREWELL, announced ahead of the change, as the reference
            // does.
            if ((MessageType)client.Received[0] == MessageType.PlayerDeath)
            {
                client.Take(5);
                continue;
            }

            ReadStateUpdate(client, playerCount);
        }

        return count;
    }
}
