using GG2.Simulation;
using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The tick's broadcast carries THIS tick's input, as the reference's does.
/// </summary>
/// <remarks>
/// <b>Nothing checked this, and a comment claimed it was checked.</b>
/// <c>GameServer.Tick</c> says the broadcast/simulate order is "the pairing
/// <c>docs/PARITY.md</c>'s replayer calls <c>UpdatesPrecedeTheirTick</c>" — but
/// that is a <c>GG2.Parity.Replayer</c> option for interpreting traces recorded
/// from the real game, and it constrains nothing about what this server emits.
/// The claim named a real thing that was not the thing claimed, which is the
/// exact failure shape <c>CONTRIBUTING.md</c> keeps recording.
/// <para>
/// <b>Why a one-tick slip here is invisible and expensive.</b> Every client
/// dead-reckons each character from the last keys it heard until the next
/// position update, which is every 7th tick. If the wire carries keys one tick
/// stale, every client extrapolates from the wrong input for most of every gap.
/// Nothing on the server looks wrong — the tick budget is fine, the cadence is
/// right, the ordering matches the reference — and the symptom is
/// indistinguishable from lag from the client's seat.
/// </para>
/// <para>
/// <b>The reference, for comparison.</b> <c>processClientCommands</c> writes
/// <c>keyState</c> straight onto the character
/// (<c>GameServerBeginStep.gml:52</c>), and <c>serializeState(INPUTSTATE)</c>
/// runs after it at line 66. This port stores client input in
/// <c>NetworkInputSource.Latest</c> and does not reach
/// <c>CharacterState.KeyState</c> until <c>MatchSimulation.Tick</c>.
/// </para>
/// <para>
/// <b>Read off the WIRE, never off the world.</b> Inspecting
/// <c>KeyState</c> after <c>Tick()</c> returns answers a different question — it
/// reports the end-of-tick state, and the whole question is what the broadcast
/// carried part-way through.
/// </para>
/// </remarks>
public sealed class InputReachesTheWireTests
{
    private const string Map = "ctf_2dfort";

    /// <summary>
    /// The keys the server put on the wire for <paramref name="slot"/> during the
    /// tick just run, decoded from the bytes the client actually received.
    /// </summary>
    /// <returns>Null if that tick's broadcast carried no state update.</returns>
    private static PlayerKeys? WireKeys(
        LoopbackClient client, PlayerClass[] classes, int slot)
    {
        var bytes = client.Take(client.Received.Count);

        for (var i = 0; i < bytes.Length; i++)
        {
            var type = (MessageType)bytes[i];

            if (type is not (MessageType.InputState or MessageType.QuickUpdate))
                continue;

            try
            {
                var reader = new NetReader(bytes.AsSpan(i + 1));
                var update = StateUpdateReader.Read(
                    ref reader, type, GameModes.ShapeOf(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map))), classes);

                if (slot < update.Players.Length && update.Players[slot].Character is { } block)
                    return block.Keys;
            }
            catch (ProtocolException)
            {
                // Not a state update after all — that byte was payload of
                // something else. Keep scanning.
            }
        }

        return null;
    }

    /// <summary>
    /// The whole question in one assertion: press a key, tick once, and ask
    /// what the wire said on that tick.
    /// </summary>
    [Fact]
    public void AKeyPressedThisTickIsOnTheWireThisTick()
    {
        using var harness = new ServerHarness(options => options.Map = Map);

        var client = harness.JoinClient("prober");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Soldier);
        harness.TickUntil(() => harness.Server.IsAlive(1), "the client spawned", 400);

        PlayerClass[] classes = [harness.Server.ClassOf(0), harness.Server.ClassOf(1)];

        // Settle with nothing held, and drain what that produced.
        client.SendInputState(new PlayerInput { Keys = PlayerKeys.None });
        harness.Tick(4);
        client.Take(client.Received.Count);

        // Not vacuous: the quiet baseline has to actually read as "no keys",
        // or an assertion that RIGHT appears proves nothing about timing.
        client.SendInputState(new PlayerInput { Keys = PlayerKeys.None });
        harness.Tick(1);

        var quiet = WireKeys(client, classes, 1);
        Assert.True(quiet is not null, "no state update was decoded on a quiet tick");
        Assert.False(quiet!.Value.HasFlag(PlayerKeys.Right), "the baseline tick already had RIGHT");

        // Press RIGHT, and tick exactly once.
        client.SendInputState(new PlayerInput { Keys = PlayerKeys.Right });
        harness.Tick(1);

        var pressed = WireKeys(client, classes, 1);
        Assert.True(pressed is not null, "no state update was decoded on the pressed tick");

        Assert.True(
            pressed!.Value.HasFlag(PlayerKeys.Right),
            "the broadcast for this tick did NOT carry the key the client pressed for it. "
            + "Clients dead-reckon from these keys for up to 7 ticks, so a one-tick stale "
            + "keystate mispredicts every character for most of every gap and looks exactly "
            + "like lag. The reference writes keyState in processClientCommands "
            + "(GameServerBeginStep.gml:52) and serialises after it, at line 66.");
    }

    /// <summary>
    /// The same question with the SOCKET REMOVED, which is what makes the
    /// answer about ordering rather than about the network.
    /// </summary>
    /// <remarks>
    /// <b>The client test above cannot distinguish two explanations</b>: the
    /// broadcast being built from a stale keystate, or the client's bytes simply
    /// not having arrived before <c>ServiceClients</c> ran on that tick. TCP on
    /// loopback is usually immediate and is not guaranteed to be.
    /// <para>
    /// A bot has no socket. <c>ServiceBots</c> runs in-process immediately before
    /// <c>WriteStateUpdate</c>, so its input is unambiguously available when the
    /// broadcast is built. If a bot's key is ALSO one tick late on the wire, the
    /// network is excluded and only the ordering is left.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotsKeyIsOnTheWireOnTheTickItPressesIt()
    {
        using var harness = new ServerHarness(options => options.Map = Map);

        var client = harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the watcher joined");

        var driver = new HeldKey();
        var slot = harness.Server.AddBot("presser", Team.Red, PlayerClass.Soldier, driver);
        harness.TickUntil(() => harness.Server.IsAlive(slot), "the bot spawned", 400);

        var classes = new PlayerClass[harness.Server.RosterCount];
        for (var i = 0; i < classes.Length; i++)
            classes[i] = harness.Server.ClassOf(i);

        harness.Tick(4);
        client.Take(client.Received.Count);

        harness.Tick(1);
        var quiet = WireKeys(client, classes, slot);
        Assert.True(quiet is not null, "no state update decoded on the quiet tick");
        Assert.False(quiet!.Value.HasFlag(PlayerKeys.Right), "the baseline tick already had RIGHT");

        // From here the bot presses RIGHT on every poll, starting with the poll
        // that happens inside the very next Tick().
        driver.Keys = PlayerKeys.Right;
        harness.Tick(1);

        var pressed = WireKeys(client, classes, slot);
        Assert.True(pressed is not null, "no state update decoded on the pressed tick");

        Assert.True(
            pressed!.Value.HasFlag(PlayerKeys.Right),
            "a BOT's key did not reach the wire on the tick it was pressed, and a bot has no "
            + "socket - so this is the broadcast being built from a stale KeyState, not the "
            + "network. ServiceBots runs immediately before WriteStateUpdate, but ApplyInput "
            + "runs inside MatchSimulation.Tick, which is AFTER it.");
    }

    /// <summary>
    /// A key pressed and released again before the tick reads either input is
    /// still acted on, and is still relayed.
    /// </summary>
    /// <remarks>
    /// <b>Two <c>INPUTSTATE</c>s in one tick is not a contrived case.</b> A
    /// client sends one per virtual tick (<c>PlayerControl</c>'s Begin Step,
    /// under <c>global.run_virtual_ticks</c>), so a delayed packet arriving
    /// beside the next one puts two in the same server tick, and up to ten are
    /// read per player per tick (<c>ClientCommands.PerTickLimit</c>). The key is
    /// then in no <c>keyState</c> the tick ever looks at.
    /// <para>
    /// ⛔ <b>The reference does act on it</b>, because
    /// <c>processClientCommands.gml:315</c> runs <c>event_user(1)</c> per input
    /// rather than per tick, so each one's edge lands in <c>pressedKeys</c>. A
    /// port keeping only the last input would drop the press entirely — a spy's
    /// tapped stab, a tapped jump and a tapped airblast happening on neither the
    /// server nor any client. See <see cref="NetworkInputSource.TakeEdges"/>.
    /// </para>
    /// <para>
    /// ⛔ <b>And it does not relay it</b>, which is the second half: the server
    /// taunts and no client sees the key, so every client draws and simulates a
    /// character that is not frozen while the server holds it still. This port
    /// relays the tapped bit as held for exactly that one update.
    /// </para>
    /// <para>
    /// <see cref="PlayerKeys.Taunt"/> is the probe because a taunt is decided on
    /// the pressed edge alone and is observable on both sides — the server
    /// through <see cref="GameServer.IsTaunting"/>, the client through the key
    /// byte it is sent. docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried
    /// into the port".
    /// </para>
    /// </remarks>
    [Fact]
    public void AKeyTappedBetweenTwoUpdatesIsActedOnAndRelayed()
    {
        using var harness = new ServerHarness(options => options.Map = Map);

        var client = harness.JoinClient("tapper");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Soldier);
        harness.TickUntil(() => harness.Server.IsAlive(1), "the client spawned", 400);

        PlayerClass[] classes = [harness.Server.ClassOf(0), harness.Server.ClassOf(1)];

        // Settle with nothing held. The baseline matters twice over: a character
        // that was already taunting would make the assertion below vacuous.
        client.SendInputState(new PlayerInput { Keys = PlayerKeys.None });
        harness.Tick(4);
        client.Take(client.Received.Count);

        Assert.False(harness.Server.IsTaunting(1), "the baseline was already taunting");

        // The tap: both inputs are queued before the server reads either, so the
        // tick sees TAUNT in no keyState at all.
        client.SendInputState(new PlayerInput { Keys = PlayerKeys.Taunt });
        client.SendInputState(new PlayerInput { Keys = PlayerKeys.None });
        harness.Tick(1);

        Assert.True(
            harness.Server.IsTaunting(1),
            "the server did not act on a key the client pressed and released between two of "
            + "its own updates. The reference does (processClientCommands.gml:315 runs "
            + "event_user(1) per INPUTSTATE), so this is input the port threw away.");

        var relayed = WireKeys(client, classes, 1);
        Assert.True(relayed is not null, "no state update was decoded on the tapped tick");

        Assert.True(
            relayed!.Value.HasFlag(PlayerKeys.Taunt),
            "the server acted on the tap and relayed a keystate without it, so every client "
            + "simulates a character that is not frozen while the server holds it still. "
            + "See GameServer.TryGetCharacter.");
    }

    /// <summary>
    /// The control: <see cref="PlayerKeys.Down"/> is the one key a tap of is NOT
    /// relayed.
    /// </summary>
    /// <remarks>
    /// The server reads it as held only — no <c>downOrPressedKeys</c> arm and no
    /// pressed-edge test — so a tap of it changed nothing on the server, and a
    /// client told it was held for a tick would drop the character through a
    /// dropdown platform the server kept it on. Without this the relay reads as
    /// "pass the tapped bits through", which is one key too many.
    /// </remarks>
    [Fact]
    public void ATappedDownKeyIsNotRelayed()
    {
        using var harness = new ServerHarness(options => options.Map = Map);

        var client = harness.JoinClient("tapper");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Soldier);
        harness.TickUntil(() => harness.Server.IsAlive(1), "the client spawned", 400);

        PlayerClass[] classes = [harness.Server.ClassOf(0), harness.Server.ClassOf(1)];

        client.SendInputState(new PlayerInput { Keys = PlayerKeys.None });
        harness.Tick(4);
        client.Take(client.Received.Count);

        client.SendInputState(new PlayerInput { Keys = PlayerKeys.Down });
        client.SendInputState(new PlayerInput { Keys = PlayerKeys.None });
        harness.Tick(1);

        var relayed = WireKeys(client, classes, 1);
        Assert.True(relayed is not null, "no state update was decoded on the tapped tick");

        Assert.False(
            relayed!.Value.HasFlag(PlayerKeys.Down),
            "a tapped DOWN was relayed as held. The server does not read its pressed edge, so "
            + "this adds a key the server never acted on and drops clients through platforms.");
    }

    /// <summary>An input source that presses whatever it is told to.</summary>
    private sealed class HeldKey : IInputSource
    {
        internal PlayerKeys Keys;

        public PlayerInput Poll(in WorldView world, int slot, int tick) =>
            new() { Keys = Keys };
    }
}
