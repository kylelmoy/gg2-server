using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// One socket cannot make the server broadcast to everybody as fast as it can
/// type.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This is a deliberate departure from the reference and the only one in
/// <see cref="ConnectionLimits"/> a client could notice.</b>
/// <c>processClientCommands.gml:194-206</c> relays every <c>CHAT_BUBBLE</c> it
/// is handed. The reasoning for throttling is the reference's own, applied one
/// case further along: it throttles <c>PLAYER_CHANGENAME</c> to once a second
/// because "every accepted rename is an unbounded broadcast to every connected
/// client", and that is equally true of the bubble in the case beside it —
/// <c>ClientCommands.PerTickLimit</c> lets one socket send ten a tick, which is
/// three hundred broadcasts a second to everybody on the server.
/// </para>
/// <para>
/// <c>ChatBubbleIntervalMs = 0</c> restores the reference exactly, and
/// <see cref="EveryBubbleIsRelayedWhenTheThrottleIsOff"/> is what proves that
/// escape hatch works rather than merely existing.
/// </para>
/// </remarks>
public sealed class ChatBubbleThrottleTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static ServerHarness Harness(int intervalMs) => new(options =>
    {
        options.Map = Map;
        options.MapRotation = [Map];
        options.ShuffleRotation = ShuffleMode.None;
        options.Limits = options.Limits with { ChatBubbleIntervalMs = intervalMs };
    });

    /// <summary>How many CHAT_BUBBLE messages reached the wire.</summary>
    private static int BubblesSeen(MirrorClient mirror) =>
        mirror.Of(MessageType.ChatBubble).Count;

    /// <summary>
    /// Ten bubbles in one tick — the most the command budget allows — reach the
    /// wire as one.
    /// </summary>
    [Fact]
    public void ABurstInOneTickIsRelayedOnce()
    {
        ReferenceTree.RequireAvailable();

        using var harness = Harness(intervalMs: 250);
        var socket = harness.JoinClient("chatty");
        using var mirror = new MirrorClient(socket, Load, Map);

        for (var i = 0; i < 10; i++)
            socket.Send([(byte)MessageType.ChatBubble, 1]);

        harness.Tick(3);
        mirror.Pump();

        Assert.Equal(1, BubblesSeen(mirror));
    }

    /// <summary>
    /// ⚠️ <b>The control case.</b> The test above asserts bubbles did NOT
    /// arrive, which is the shape that also passes if the command never reached
    /// the server. With the throttle off, the same client on the same
    /// connection sending the same bytes must produce all ten.
    /// </summary>
    [Fact]
    public void EveryBubbleIsRelayedWhenTheThrottleIsOff()
    {
        ReferenceTree.RequireAvailable();

        using var harness = Harness(intervalMs: 0);
        var socket = harness.JoinClient("chatty");
        using var mirror = new MirrorClient(socket, Load, Map);

        for (var i = 0; i < 10; i++)
            socket.Send([(byte)MessageType.ChatBubble, 1]);

        harness.Tick(3);
        mirror.Pump();

        Assert.Equal(10, BubblesSeen(mirror));
    }

    /// <summary>
    /// A player who waits gets their bubble; the throttle is a rate, not a
    /// quota.
    /// </summary>
    /// <remarks>
    /// The clock is <c>Environment.TickCount64</c>, which a test cannot move, so
    /// this uses a one-millisecond interval and lets real time pass instead of
    /// asserting on a duration. What it establishes is the shape — the gate
    /// reopens — rather than the exact 250 ms, which
    /// <see cref="ConnectionLimits"/> owns.
    /// </remarks>
    [Fact]
    public void TheGateReopensRatherThanClosingForGood()
    {
        ReferenceTree.RequireAvailable();

        using var harness = Harness(intervalMs: 1);
        var socket = harness.JoinClient("chatty");
        using var mirror = new MirrorClient(socket, Load, Map);

        for (var i = 0; i < 5; i++)
        {
            socket.Send([(byte)MessageType.ChatBubble, 1]);
            harness.Tick(2);

            // ⚠️ 25ms against a 1ms interval, and the margin is the point. The
            // clock here is Environment.TickCount64, which a test cannot move, so
            // this test's verdict depends on the OS scheduler — a participant it
            // does not control. At 5ms it failed under a full-suite run ("only 3
            // of 5 bubbles got through", 2026-09-24), because a sleep that
            // returns late is a window that has not opened yet. Twenty-five needs
            // a 25x scheduling delay to fail rather than a 5x one.
            // ⛔ It is still wall-clock and therefore still not deterministic. The
            // real fix is an injectable clock on the throttle; this buys margin,
            // not certainty, and should not be read as having made it a gate.
            Thread.Sleep(25);
        }

        harness.Tick(2);
        mirror.Pump();

        Assert.True(BubblesSeen(mirror) >= 4, $"only {BubblesSeen(mirror)} of 5 bubbles got through");
    }
}
