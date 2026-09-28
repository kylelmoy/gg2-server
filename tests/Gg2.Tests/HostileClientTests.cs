using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A client that sends well-formed messages with hostile values at hostile
/// times, and the port ignoring each one exactly where the reference ignores it.
/// </summary>
/// <remarks>
/// <b>The oracle is NOT "does the server crash".</b> A hostile client that
/// crashes the server is a denial of service; a hostile client the port
/// <em>obeys</em> where the reference would have ignored it is a parity bug, and
/// it desynchronises every OTHER client on the server — the same failure class
/// as both client fatals in <c>docs/PARITY.md</c>. So each case here is settled
/// by reading <c>processClientCommands.gml</c> and asserting the port does the
/// same thing, not by asserting the process survived.
/// <para>
/// <b>Why not byte fuzz.</b> Random bytes die at the framing layer and never
/// reach a command, so they exercise <c>ServerStream</c> and nothing else
/// (<c>ServerStreamTests</c> already covers truncation). The interesting inputs
/// are grammatically valid: the right message type, a legal payload, sent by a
/// player for whom the action is not allowed.
/// </para>
/// <para>
/// ⚠️ <b>The control case is load-bearing.</b> Every assertion below is that
/// something did NOT arrive, which is exactly the shape that passes when the
/// command never reached the server at all — a broken test harness and a
/// correct guard look identical. <see cref="TheSameCommandIsObeyedOnceItsPreconditionsHold"/>
/// is the control: the same <c>ToggleZoom</c>, from the same client, on the same
/// connection, DOES produce a broadcast once the class is right.
/// </para>
/// </remarks>
public sealed class HostileClientTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static ServerHarness Harness() => new(options =>
    {
        options.Map = Map;
        options.MapRotation = [Map];
        options.ShuffleRotation = ShuffleMode.None;
    });

    /// <summary>
    /// Five commands whose preconditions all fail, sent before spawning and again
    /// as the wrong class, reach the wire as nothing at all.
    /// </summary>
    /// <remarks>
    /// The reference's guards, each read from
    /// <c>Scripts/GameServer/processClientCommands.gml</c>:
    /// <list type="bullet">
    /// <item><c>BUILD_SENTRY</c> (:211) — <c>player.object != -1</c> and
    /// <c>class == CLASS_ENGINEER</c> and four more clauses.</item>
    /// <item><c>DESTROY_SENTRY</c> (:232) — <c>with(player.sentry)</c>, which is
    /// a no-op on a player who has none; GML's <c>with</c> over nothing runs
    /// nothing.</item>
    /// <item><c>DROP_INTEL</c> (:237) — <c>player.object != -1</c> and
    /// <c>player.object.intel</c>.</item>
    /// <item><c>OMNOMNOMNOM</c> (:247) — six clauses including
    /// <c>class == CLASS_HEAVY</c>.</item>
    /// <item><c>TOGGLE_ZOOM</c> (:267) — <c>class == CLASS_SNIPER</c>.</item>
    /// </list>
    /// A scout carrying nothing fails every one of them, and a client that has not
    /// spawned yet fails the outer `player.object != -1` guard on top.
    /// </remarks>
    [Fact]
    public void CommandsWhosePreconditionsFailReachTheWireAsNothing()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("hostile");
        using var mirror = new MirrorClient(socket, Load, Map);

        harness.Tick(5);
        var slot = Slot(harness);

        mirror.Pump();
        mirror.ClearLog();

        // Half one: NO CHARACTER at all. A freshly joined client is a spectator
        // with `player.object == -1`, which is the outer guard on four of the
        // five commands — reachable deterministically, where killing a spawned
        // player would not be.
        Assert.False(harness.Server.TryGetExactPosition(slot, out _, out _, out _));
        SendTheBattery(socket);
        harness.Tick(30);

        // Half two: alive, but as the WRONG CLASS for every command in the
        // battery, holding no intel and owning no sentry. That is the inner
        // guard rather than the outer one.
        socket.SendChangeTeam(Team.Red);
        socket.SendChangeClass(PlayerClass.Scout);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the hostile client to spawn", maxTicks: 400);

        Assert.Equal(PlayerClass.Scout, harness.Server.ClassOf(slot));
        Assert.False(harness.Server.IsCarryingIntel(slot));

        SendTheBattery(socket);
        harness.Tick(30);

        mirror.Pump();

        Assert.Empty(mirror.Of(MessageType.BuildSentry));
        Assert.Empty(mirror.Of(MessageType.DestroySentry));
        Assert.Empty(mirror.Of(MessageType.DropIntel));
        Assert.Empty(mirror.Of(MessageType.Omnomnomnom));
        Assert.Empty(mirror.Of(MessageType.ToggleZoom));

        // The connection is still healthy: silence must be the guard refusing,
        // not the server having dropped this client.
        Assert.False(socket.IsClosed);
        Assert.True(harness.Server.IsConnected(slot));

        // And no client was told something it cannot render.
        Assert.Empty(mirror.Impossible);
    }

    /// <summary>
    /// The control: the same command, the same client, obeyed once its
    /// precondition holds.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="CommandsWhosePreconditionsFailReachTheWireAsNothing"/>
    /// is satisfied by a harness that never delivered the command — the failure
    /// mode `docs/PARITY.md` calls out, where a green assertion establishes
    /// nothing because the path under test was never live.
    /// </remarks>
    [Fact]
    public void TheSameCommandIsObeyedOnceItsPreconditionsHold()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("sniper");
        using var mirror = new MirrorClient(socket, Load, Map);

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        socket.SendChangeClass(PlayerClass.Sniper);
        harness.TickUntil(() => harness.Server.TryGetExactPosition(Slot(harness), out _, out _, out _),
            "the sniper to spawn", maxTicks: 400);

        Assert.Equal(PlayerClass.Sniper, harness.Server.ClassOf(Slot(harness)));

        mirror.Pump();
        mirror.ClearLog();

        socket.SendCommand(MessageType.ToggleZoom);
        harness.Tick(30);
        mirror.Pump();

        Assert.NotEmpty(mirror.Of(MessageType.ToggleZoom));
    }

    /// <summary>
    /// Going SPECTATOR while carrying the briefcase drops it on the field — it
    /// does not vanish, and it does not stay attached to a spectator.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:151-186</c>: an accepted team change with a
    /// live character fires a death first
    /// (<c>DAMAGE_SOURCE_BID_FAREWELL</c>), and a death is what drops the
    /// intel. Spectator is explicitly allowed through the
    /// <c>getCharacterObject(player.class) != -1 or newTeam==TEAM_SPECTATOR</c>
    /// guard, so this is a reachable path and not an exotic one.
    /// <para>
    /// ⛔ <b>Both failure modes are bad and they are opposites.</b> A briefcase
    /// that vanishes strands the round with no intel to capture; one that stays
    /// carried leaves a <em>spectator</em> holding it, which is the same family
    /// as the live watchdog's code 1 and draws a marker on somebody with no
    /// character. Asserting only "nobody is carrying it" would pass on the
    /// first, so <c>OnField</c> is asserted too.
    /// </para>
    /// </remarks>
    [Fact]
    public void GoingSpectatorWhileCarryingTheIntelDropsItRatherThanLosingIt()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("defector");
        using var mirror = new MirrorClient(socket, Load, Map);

        var slot = Slot(harness);

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the defector to spawn",
            maxTicks: 400);

        var blue = harness.Server.IntelOf(Team.Blue);
        Assert.True(blue.HasBase, $"{Map} places no blue intel, so this test checks nothing");

        harness.TickUntil(
            () =>
            {
                harness.Server.Teleport(slot, blue.HomeX, blue.HomeY);
                return harness.Server.IsCarryingIntel(slot);
            },
            "the defector to pick the briefcase up",
            maxTicks: 400);

        // While carried it is NOT on the field — the control that makes the
        // OnField assertion below mean something.
        Assert.False(harness.Server.IntelOf(Team.Blue).OnField);

        socket.SendChangeTeam(Team.Spectator);
        harness.TickUntil(
            () => harness.Server.TeamOf(slot) == Team.Spectator,
            "the defector to reach spectator",
            maxTicks: 200);

        harness.Tick(30);

        Assert.False(harness.Server.IsCarryingIntel(slot));
        Assert.Equal(-1, harness.Server.IntelCarrier(Team.Blue));
        Assert.True(
            harness.Server.IntelOf(Team.Blue).OnField,
            "the briefcase neither dropped nor returned — it vanished");

        mirror.Pump();
        Assert.Empty(mirror.Impossible);
    }

    /// <summary>
    /// Asking for the team you are ALREADY on is refused, so spamming it does
    /// not kill you over and over.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:151</c>'s guard is
    /// <c>if(balance != newTeam and newTeam != player.team)</c>. The second
    /// clause is the one here: without it every repeat would take the accepted
    /// path, and that path <b>fires a death</b> before switching. A client
    /// holding the key down would therefore die continuously, which is a denial
    /// of service a stock client can trigger by accident.
    /// <para>
    /// The character surviving at the same coordinates is the assertion —
    /// "still on red" alone would pass even if it had died and respawned.
    /// </para>
    /// </remarks>
    [Fact]
    public void AskingForTheTeamYouAreAlreadyOnIsRefusedRatherThanKillingYou()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("spammer");

        var slot = Slot(harness);

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the spammer to spawn",
            maxTicks: 400);

        // Let it settle on the ground first, so a later position comparison is
        // about the team change rather than about falling.
        harness.Tick(60);
        Assert.True(harness.Server.TryGetExactPosition(slot, out var x, out var y, out _));

        for (var i = 0; i < 20; i++)
        {
            socket.SendChangeTeam(Team.Red);
            harness.Tick(2);
        }

        Assert.Equal(Team.Red, harness.Server.TeamOf(slot));
        Assert.True(
            harness.Server.TryGetExactPosition(slot, out var x2, out var y2, out _),
            "the spammer has no character, so a repeat request killed it");
        Assert.Equal(x, x2);
        Assert.Equal(y, y2);
    }

    private static void SendTheBattery(LoopbackClient socket)
    {
        socket.SendCommand(MessageType.BuildSentry);    // not an engineer
        socket.SendCommand(MessageType.DestroySentry);  // owns no sentry
        socket.SendCommand(MessageType.DropIntel);      // carrying nothing
        socket.SendCommand(MessageType.Omnomnomnom);    // not a heavy
        socket.SendCommand(MessageType.ToggleZoom);     // not a sniper
    }

    /// <summary>The last roster slot, which is the client that just joined.</summary>
    private static int Slot(ServerHarness harness) => harness.Server.RosterCount - 1;
}
