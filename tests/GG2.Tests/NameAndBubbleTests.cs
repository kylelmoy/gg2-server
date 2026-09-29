using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The two client commands whose server-side handling is policy rather than
/// relay: <c>PLAYER_CHANGENAME</c> and <c>CHAT_BUBBLE</c>.
/// </summary>
/// <remarks>
/// Both looked like pass-throughs and neither is.
/// <c>processClientCommands.gml:279-317</c> kicks an over-long rename and
/// throttles the rest to one a second; <c>:194-206</c> rewrites bubbles on April
/// Fools and drops one particular byte on every other day.
/// </remarks>
public sealed class NameAndBubbleTests
{
    // --- the name ------------------------------------------------------------

    /// <summary>
    /// A rename past the protocol's limit is a kick, not a truncation.
    /// </summary>
    /// <remarks>
    /// <b>The join path truncates and this one does not</b>
    /// (<c>serviceJoiningPlayer.gml:176</c> against
    /// <c>processClientCommands.gml:283-287</c>). The asymmetry is the
    /// reference's; a port that truncated here would keep a client connected
    /// that the reference disconnects, and would broadcast a name the reference
    /// never sends.
    /// </remarks>
    [Fact]
    public void AnOverLongRenameIsKicked()
    {
        using var harness = new ServerHarness();

        var client = harness.JoinClient("player");
        harness.Tick(2);
        client.Take(client.Received.Count);

        // Held as the entry object rather than as a slot index: the kick
        // removes the player, so the index is stale by the time this is checked.
        // That is the same hazard the reward path has to survive, for the same
        // reason — a roster index is a position, not an identity.
        var entry = harness.Server.EntryOf(harness.Server.RosterCount - 1);

        var name = new string('x', GameConstants.MaxPlayerNameLength + 1);
        client.Send([(byte)MessageType.PlayerChangeName, (byte)name.Length, .. GG2Text.Encode(name)]);

        harness.TickUntil(() => client.IsClosed, "the kick");

        var seen = client.Take(client.Received.Count);
        var at = Array.IndexOf(seen, (byte)MessageType.Kick);

        Assert.True(at >= 0 && at + 1 < seen.Length, "the client was dropped with no reason byte");
        Assert.Equal((byte)KickReason.Name, seen[at + 1]);

        Assert.Equal("player", entry.Name);
        Assert.True(harness.Log.Contains("PLAYER_CHANGENAME of"));
    }

    /// <summary>
    /// A name exactly at the limit is accepted.
    /// </summary>
    /// <remarks>
    /// The off-by-one control. The reference's test is <c>&gt;</c>, so 20 is
    /// legal and 21 is not; a <c>&gt;=</c> here would kick clients the reference
    /// serves and no other test would notice.
    /// </remarks>
    [Fact]
    public void ANameAtTheLimitIsAccepted()
    {
        using var harness = new ServerHarness();

        var client = harness.JoinClient("player");
        harness.Tick(2);

        var slot = harness.Server.RosterCount - 1;

        var name = new string('x', GameConstants.MaxPlayerNameLength);
        client.Send([(byte)MessageType.PlayerChangeName, (byte)name.Length, .. GG2Text.Encode(name)]);
        harness.Tick(2);

        Assert.False(client.IsClosed, "a legal rename must not drop the connection");
        Assert.Equal(name, harness.Server.EntryOf(slot).Name);
    }

    /// <summary>
    /// A second rename inside a second is dropped.
    /// </summary>
    /// <remarks>
    /// <b>This is flood control, not tidiness.</b> Every accepted rename is a
    /// broadcast to every connected client, so without the throttle one socket
    /// can make the server send as fast as it can read — and the ten-commands-per
    /// -tick limit does not help, because ten broadcasts a tick is already the
    /// attack.
    /// </remarks>
    [Fact]
    public void RenamesAreThrottledToOneASecond()
    {
        using var harness = new ServerHarness();

        var client = harness.JoinClient("player");
        harness.Tick(2);

        var slot = harness.Server.RosterCount - 1;

        Rename(client, "first");
        harness.Tick(2);
        Assert.Equal("first", harness.Server.EntryOf(slot).Name);

        // Immediately after, inside the same second.
        Rename(client, "second");
        harness.Tick(2);
        Assert.Equal("first", harness.Server.EntryOf(slot).Name);

        // The clock is real and monotonic (GM8's current_time), so a test
        // cannot inject one — it clears the only state the throttle reads
        // instead of sleeping a second. Null is "has never renamed", which is
        // the reference's variable_local_exists.
        harness.Server.EntryOf(slot).LastNameChangeMs = null;

        Rename(client, "third");
        harness.Tick(2);
        Assert.Equal("third", harness.Server.EntryOf(slot).Name);
    }

    private static void Rename(LoopbackClient client, string name) =>
        client.Send([(byte)MessageType.PlayerChangeName, (byte)name.Length, .. GG2Text.Encode(name)]);

    // --- the bubble ----------------------------------------------------------

    /// <summary>
    /// Off April Fools, a bubble is relayed unchanged — except the one that is
    /// dropped.
    /// </summary>
    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(45, true, 45)]
    [InlineData(60, true, 60)]
    [InlineData(61, false, 0)]
    public void AnOrdinaryDayRelaysEverythingButSixtyOne(byte requested, bool relayed, byte expected)
    {
        Assert.Equal(relayed, ChatBubbles.Rewrite(requested, aprilFools: false, out var image));

        if (relayed)
            Assert.Equal(expected, image);
    }

    /// <summary>
    /// On April Fools every bubble but 45 becomes 61.
    /// </summary>
    /// <remarks>
    /// <b>Including 61 itself</b>, which is the clause the <c>if</c>/
    /// <c>else if</c> ordering exists for: a 61 takes the first branch on April
    /// Fools and is relayed, and reaches the second branch on every other day and
    /// is dropped. Flattening the two into one test would invert the one day the
    /// byte is legitimate.
    /// </remarks>
    [Theory]
    [InlineData(0, 61)]
    [InlineData(45, 45)]
    [InlineData(60, 61)]
    [InlineData(61, 61)]
    public void AprilFoolsRewritesEverythingButFortyFive(byte requested, byte expected)
    {
        Assert.True(ChatBubbles.Rewrite(requested, aprilFools: true, out var image));
        Assert.Equal(expected, image);
    }

    /// <summary>
    /// The date test is the calendar's, and it is local.
    /// </summary>
    /// <remarks>
    /// <c>calculateAprilFools.gml</c> reads <c>date_current_date()</c>. Using UTC
    /// would put the joke on the wrong day for everybody more than a few hours
    /// off Greenwich, which is most of the player base.
    /// </remarks>
    [Fact]
    public void AprilFoolsIsTheFirstOfApril()
    {
        Assert.True(ChatBubbles.IsAprilFools(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Local)));
        Assert.True(ChatBubbles.IsAprilFools(new DateTime(2026, 4, 1, 23, 59, 59, DateTimeKind.Local)));
        Assert.False(ChatBubbles.IsAprilFools(new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Local)));
        Assert.False(ChatBubbles.IsAprilFools(new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Local)));
        Assert.False(ChatBubbles.IsAprilFools(new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Local)));
    }

    /// <summary>
    /// The server reads the flag on the reference's cadence, and the substitution
    /// reaches the wire.
    /// </summary>
    /// <remarks>
    /// The pure function above is where the logic is tested; this is the wiring,
    /// which is the half that was missing rather than wrong.
    /// </remarks>
    [Fact]
    public void TheServerAppliesTheSubstitution()
    {
        using var harness = new ServerHarness();

        harness.Server.LocalClock = static () => new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Local);

        var sender = harness.JoinClient("sender");
        var watcher = harness.JoinClient("watcher");

        // The flag is refreshed in the same 30-second block as the lobby
        // re-announcement, so run to the next one rather than assuming startup
        // caught it.
        harness.TickUntil(
            () => harness.Server.Frame % LobbyRegistration.ReRegisterIntervalTicks == 0
                  && harness.Server.Frame > 0,
            "the 30-second block",
            LobbyRegistration.ReRegisterIntervalTicks + 2);

        watcher.Take(watcher.Received.Count);

        sender.Send([(byte)MessageType.ChatBubble, 7]);
        harness.Tick(2);

        var seen = watcher.Take(watcher.Received.Count);

        for (var i = 0; i + 2 < seen.Length; i++)
        {
            if (seen[i] != (byte)MessageType.ChatBubble)
                continue;

            Assert.Equal(ChatBubbles.AprilFoolsBubble, seen[i + 2]);
            return;
        }

        Assert.Fail("no CHAT_BUBBLE reached the other client");
    }
}
