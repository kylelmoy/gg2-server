using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// A tick's packet leads with that tick's state update, as the reference's does.
/// </summary>
/// <remarks>
/// ⛔ <b>Nothing tested WHEN the broadcast buffer is flushed, and that let a
/// second one-tick delivery bug live beside the first.</b>
/// <c>GameServerEndStep.gml:7-8</c> writes <c>global.sendBuffer</c> to every
/// socket and clears it in the server's END Step — after everything that frame
/// has been appended. So the reference's packet for tick N is
/// <c>[state update N][events N]</c>.
/// <para>
/// This port flushed directly under <c>WriteStateUpdate</c>, before the
/// simulation ran. Everything <c>AdvanceSimulation</c> appends — WEAPON_FIRE,
/// PLAYER_DEATH, ubers, intel drops, generator destruction, arena — therefore
/// waited for the NEXT tick's flush, arriving a tick late and <em>ahead</em> of
/// the update it should have followed: <c>[events N][state update N+1]</c>.
/// </para>
/// <para>
/// ⚠️ <b>That is the discriminator, and it needs no framing to check.</b> The two
/// orderings differ in which message a flush LEADS with — an event, or the state
/// update. Reading the first byte of each tick's bytes is enough, so this test
/// does not have to parse the stream to catch the regression.
/// </para>
/// <para>
/// ⛔ <b>A closed-loop replay cannot see this and did not.</b> A trace records
/// the byte stream, and the stream's contents and order are identical either
/// way — only the moment it leaves changes. A 3,599-tick replay of a live GM8
/// session came back with ZERO physics divergence while this was still wrong.
/// <b>Parity of the recorded stream is not parity of delivery.</b>
/// </para>
/// <para>
/// ⚠️ <b>Why it is worth a test rather than a comment.</b> A client simulates
/// every projectile itself from WEAPON_FIRE and the shooter's keys — there is no
/// projectile message — so a late WEAPON_FIRE starts the client's rocket a tick
/// behind the server's, which is invisible from inside the server.
/// </para>
/// </remarks>
public sealed class FlushShapeTests
{
    /// <summary>
    /// Every tick that sends anything leads with that tick's state update.
    /// </summary>
    [Fact]
    public void EveryTicksPacketLeadsWithItsStateUpdate()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = "gg_debug";

            // ⚠️ Bots need the nav graphs or they never walk anywhere, never
            // meet, and never fire — which makes the traffic this watches empty
            // and the gate vacuous. The first version of this fixture had no nav
            // and its own vacuity check is what caught it.
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Map = "gg_debug";
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            };
        });

        var client = harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        // Let the bots spawn, arm and start shooting, so the ticks below contain
        // real WEAPON_FIRE and PLAYER_DEATH traffic rather than only updates.
        harness.Tick(120);
        client.Take(client.Received.Count);

        var ticksWithBytes = 0;
        var leadingEvents = 0;

        for (var tick = 0; tick < 600; tick++)
        {
            harness.Tick(1);

            var bytes = client.Take(client.Received.Count);

            if (bytes.Length == 0)
                continue;

            ticksWithBytes++;

            // ⚠️ Scoped to the messages AdvanceSimulation appends, which is what
            // the flush position governs. PLAYER_JOIN legitimately leads the next
            // packet: ServiceJoiningConnections runs after the flush, and moving
            // the flush past it changes what a JOINING client sees — 23 tests in
            // JoinAndBroadcastTests and FullUpdateLayoutTests say so. The join
            // sequence is a separate contract this fix is not licensed to move.
            if ((MessageType)bytes[0] is MessageType.WeaponFire or MessageType.PlayerDeath)
                leadingEvents++;
        }

        // ⚠️ Not vacuous: a run where the client received nothing, or where no
        // tick carried an event, would pass the assertion below while proving
        // nothing at all.
        Assert.True(ticksWithBytes > 400, $"only {ticksWithBytes} ticks sent anything");

        Assert.Equal(0, leadingEvents);
    }

    /// <summary>
    /// ⛔ The gate above is only meaningful if the traffic it watched actually
    /// contained the event messages that were arriving late.
    /// </summary>
    [Fact]
    public void TheWatchedTrafficReallyContainsEvents()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = "gg_debug";

            // ⚠️ Bots need the nav graphs or they never walk anywhere, never
            // meet, and never fire — which makes the traffic this watches empty
            // and the gate vacuous. The first version of this fixture had no nav
            // and its own vacuity check is what caught it.
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Map = "gg_debug";
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            };
        });

        var client = harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");
        harness.Tick(120);
        client.Take(client.Received.Count);

        var events = 0;

        for (var tick = 0; tick < 600; tick++)
        {
            harness.Tick(1);

            foreach (var b in client.Take(client.Received.Count))
            {
                if ((MessageType)b is MessageType.WeaponFire or MessageType.PlayerDeath)
                    events++;
            }
        }

        // A crude scan — a payload byte can coincide with a message type — but it
        // cannot be zero on 600 ticks of six bots fighting, and zero is the only
        // reading that would make the gate above vacuous.
        Assert.True(events > 0, "600 ticks of six bots fighting produced no event bytes at all");
    }
}
