using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The two server-owned <c>CAPS_UPDATE</c> triggers fire on the tick AFTER the
/// one that asks, which is the reference's phase.
/// </summary>
/// <remarks>
/// <c>GameServerBeginStep.gml:55</c> tests <c>syncTimer</c>, and <b>nothing
/// inside that event ever sets it</b>. All nine setters live on objects younger
/// than the GameServer — the generator, the control point, four HUDs and the two
/// intel-base collisions — so every one of them runs after line 55 has already
/// been tested for that frame, and the extra scoreboard message lands on the
/// FOLLOWING tick.
/// <para>
/// The port honours that everywhere the request comes out of the simulation,
/// as <c>GameServer._capsSyncPending</c>'s comment says. The two triggers keyed
/// on the server's own counters need the same discipline: setting and consuming
/// the flag inside the same <c>WriteStateUpdate</c> call collapses the one-tick
/// delay this file exists to pin.
/// </para>
/// <para>
/// This is invisible to a closed-loop replay: it compares the stream, and a
/// CAPS_UPDATE one tick out of phase is a well-formed CAPS_UPDATE in a
/// well-formed stream. Only a test that knows which tick it should have been on
/// can tell.
/// </para>
/// </remarks>
public sealed class CapsSyncPhaseTests
{
    /// <summary>
    /// Whether the packet this tick sent LED with a <c>CAPS_UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// Reading the first byte is enough, and that is not a shortcut: the
    /// flush leads with the tick's own state update (<see cref="FlushShapeTests"/>),
    /// and <c>WriteStateUpdate</c> writes the CAPS_UPDATE ahead of the tiered
    /// one. So a tick that owed a scoreboard sync leads with CAPS_UPDATE and
    /// every other tick leads with INPUTSTATE or QUICK_UPDATE.
    /// </remarks>
    private static bool LedWithCaps(LoopbackClient client)
    {
        var bytes = client.Take(client.Received.Count);
        return bytes.Length > 0 && (MessageType)bytes[0] == MessageType.CapsUpdate;
    }

    /// <summary>
    /// The whole question for the generator: frame 1800 asks, frame 1801
    /// answers.
    /// </summary>
    /// <remarks>
    /// <c>Generator.events/Begin Step.xml:24-25</c> is
    /// <c>else if GameServer.frame mod 1800 == 0 -&gt; GameServer.syncTimer = 1</c>,
    /// and a <c>Generator</c> is a room object while the GameServer is
    /// persistent and older. So the frame whose number is the multiple of 1800
    /// is the frame that ASKS, and the next one is the frame that SENDS.
    /// <para>
    /// Mutation-checked: putting the request back inside
    /// <c>WriteStateUpdate</c> moves the CAPS_UPDATE onto 1800 and fails both
    /// assertions at once.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGeneratorsMinuteSyncLandsOnTheTickAfterTheMinute()
    {
        using var harness = NewGeneratorServer();
        var client = harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        RunTo(harness, client, 1799);

        harness.Tick(1);
        var atTheMinute = LedWithCaps(client);

        harness.Tick(1);
        var afterTheMinute = LedWithCaps(client);

        Assert.Equal(1801, harness.Server.Frame);
        Assert.False(atTheMinute, "frame 1800 sent the CAPS_UPDATE it should only have asked for");
        Assert.True(afterTheMinute, "frame 1801 never sent the CAPS_UPDATE frame 1800 asked for");
    }

    /// <summary>
    /// At two minutes the reference sends TWO scoreboard updates on
    /// consecutive ticks.
    /// </summary>
    /// <remarks>
    /// 3600 is a multiple of 1800, so the heartbeat at
    /// <c>GameServerBeginStep.gml:55</c> and the generator's request both come
    /// due — but a frame apart, because the generator's is read on the frame
    /// after it is set. Folding the generator's trigger into the same <c>||</c>
    /// as the heartbeat silently merged them.
    /// <para>
    /// Not a restatement of the test above: that one locates a message, this
    /// one counts them, and only counting catches the merge.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTwoMinuteHeartbeatAndTheMinuteSyncAreTwoUpdatesNotOne()
    {
        using var harness = NewGeneratorServer();
        var client = harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        RunTo(harness, client, 3599);

        harness.Tick(1);
        var heartbeat = LedWithCaps(client);

        harness.Tick(1);
        var minute = LedWithCaps(client);

        harness.Tick(1);
        var after = LedWithCaps(client);

        Assert.Equal(3602, harness.Server.Frame);
        Assert.True(heartbeat, "frame 3600 missed the two-minute heartbeat");
        Assert.True(minute, "frame 3601 missed the generator's minute sync");
        Assert.False(after, "frame 3602 sent a CAPS_UPDATE nothing had asked for");
    }

    /// <summary>
    /// The setup timer's trigger, and it carries its own proof of phase.
    /// </summary>
    /// <remarks>
    /// <c>GameServerBeginStep.gml:55</c>'s third disjunct is
    /// <c>global.setupTimer == 180</c>, and <c>global.setupTimer</c> is
    /// decremented in the HUD's Step (<c>ControlPointHUD.events/Step.xml:21-22</c>),
    /// younger than the GameServer — so the frame that decrements it to 180 is
    /// not the frame that sees 180.
    /// <para>
    /// <b>The CAPS_UPDATE serialises the setup timer, so the message names
    /// the tick it went out on</b> — which is what this asserts, rather than
    /// frame arithmetic that would need to know when the round started.
    /// </para>
    /// <para>
    /// <b>What this does NOT pin, checked rather than assumed.</b> It cannot
    /// tell the placement above — after <c>MatchSimulation.Tick</c> — from a
    /// set-and-consume inside <c>WriteStateUpdate</c>. Serialisation precedes
    /// the simulation, so the setup timer is the SAME value on both sides of
    /// that boundary: both placements emit one CAPS_UPDATE, on the same tick,
    /// reading 180. The placement here is chosen to match the reference's shape
    /// and to keep <c>WriteStateUpdate</c>'s "nothing sets the flag from in
    /// here" invariant whole, and this test is honest that it is not the thing
    /// enforcing it.
    /// </para>
    /// <para>
    /// <b>Mutation-checked in the direction that IS observable:</b> moving
    /// the call ABOVE <c>MatchSimulation.Tick</c> makes it read the previous
    /// tick's value, sets the flag a tick late, and the update then carries
    /// <b>179</b>, measured rather than predicted.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSetupTimersSyncCarriesTheOneHundredAndEightyThatFiredIt()
    {
        const string map = "cp_dirtbowl";

        using var harness = new ServerHarness(options =>
        {
            options.Map = map;
            options.MapRotation = [map];
        });

        var client = harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        // ControlPointCount deliberately zeroed. The ControlPoints HUD writes
        // its points AFTER the setup timer, and how many of them a CAPS_UPDATE
        // carries depends on which AREA of a multi-area map is open — which
        // ShapeOf, given the whole PNG, cannot know. Zero stops the reader at the
        // field this test is about instead of running off the end of the tail it
        // is not testing.
        var shape = GameModes.ShapeOf(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)))
            with { ControlPointCount = 0 };

        // The scalar index below is only meaningful for this HUD layout.
        Assert.Equal(GameMode.ControlPoints, shape.GameMode);

        var setupTimers = new List<long>();

        // The whole setup phase and past its end. AttackDefendSetupTicks is 900,
        // so 180 is reached comfortably inside this.
        for (var tick = 0; tick < 1000; tick++)
        {
            harness.Tick(1);

            var bytes = client.Take(client.Received.Count);

            if (bytes.Length == 0 || (MessageType)bytes[0] != MessageType.CapsUpdate)
                continue;

            var reader = new NetReader(bytes.AsSpan(1));
            var update = StateUpdateReader.Read(ref reader, MessageType.CapsUpdate, shape, []);

            // ControlPoints' HUD is [timeLimitMinutes, roundTimer, setupTimer].
            setupTimers.Add(update.HudScalars[2]);
        }

        // Not vacuous: a run whose setup phase produced no CAPS_UPDATE at all
        // would satisfy "every one of them reads 180" trivially.
        var duringSetup = setupTimers.Where(timer => timer > 0).ToList();

        Assert.True(
            duringSetup.Count == 1,
            $"expected one CAPS_UPDATE during setup; the run's timers were [{string.Join(", ", setupTimers)}]");

        Assert.Equal(180, duringSetup[0]);
    }

    private static ServerHarness NewGeneratorServer() =>
        new(options =>
        {
            // The rotation is pinned to the one map so that a round ending
            // inside the 3,600 ticks below cannot rotate onto a map with no
            // generator and quietly stop the trigger under test.
            options.Map = "gen_destroy";
            options.MapRotation = ["gen_destroy"];
        });

    /// <summary>
    /// Ticks up to <paramref name="frame"/>, throwing away the traffic on the
    /// way so the caller's next read is one tick's worth.
    /// </summary>
    private static void RunTo(ServerHarness harness, LoopbackClient client, long frame)
    {
        while (harness.Server.Frame < frame)
        {
            harness.Tick(1);
            client.Take(client.Received.Count);
        }

        Assert.Equal(frame, harness.Server.Frame);
    }
}
