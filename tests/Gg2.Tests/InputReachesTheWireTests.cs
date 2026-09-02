using Gg2.Simulation;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The tick's broadcast carries THIS tick's input, as the reference's does.
/// </summary>
/// <remarks>
/// ⛔ <b>Nothing checked this, and a comment claimed it was checked.</b>
/// <c>GameServer.Tick</c> says the broadcast/simulate order is "the pairing
/// <c>docs/PARITY.md</c>'s replayer calls <c>UpdatesPrecedeTheirTick</c>" — but
/// that is a <c>Gg2.Parity.Replayer</c> option for interpreting traces recorded
/// from the real game, and it constrains nothing about what this server emits.
/// The claim named a real thing that was not the thing claimed, which is the
/// exact failure shape <c>CLAUDE.md</c> keeps recording.
/// <para>
/// ⛔ <b>Why a one-tick slip here is invisible and expensive.</b> Every client
/// dead-reckons each character from the last keys it heard until the next
/// position update, which is every 7th tick. If the wire carries keys one tick
/// stale, every client extrapolates from the wrong input for most of every gap.
/// Nothing on the server looks wrong — the tick budget is fine, the cadence is
/// right, the ordering matches the reference — and the symptom is
/// indistinguishable from lag from the client's seat.
/// </para>
/// <para>
/// ⚠️ <b>The reference, for comparison.</b> <c>processClientCommands</c> writes
/// <c>keyState</c> straight onto the character
/// (<c>GameServerBeginStep.gml:52</c>), and <c>serializeState(INPUTSTATE)</c>
/// runs after it at line 66. This port stores client input in
/// <c>NetworkInputSource.Latest</c> and does not reach
/// <c>CharacterState.KeyState</c> until <c>MatchSimulation.Tick</c>.
/// </para>
/// <para>
/// ⚠️ <b>Read off the WIRE, never off the world.</b> Inspecting
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
    /// ⛔ The whole question in one assertion: press a key, tick once, and ask
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

        // ⚠️ Not vacuous: the quiet baseline has to actually read as "no keys",
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
    /// ⛔ The same question with the SOCKET REMOVED, which is what makes the
    /// answer about ordering rather than about the network.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The client test above cannot distinguish two explanations</b>: the
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

    /// <summary>An input source that presses whatever it is told to.</summary>
    private sealed class HeldKey : IInputSource
    {
        internal PlayerKeys Keys;

        public PlayerInput Poll(in WorldView world, int slot, int tick) =>
            new() { Keys = Keys };
    }
}
