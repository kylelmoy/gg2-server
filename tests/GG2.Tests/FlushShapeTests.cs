using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A tick's packet leads with that tick's state update, as the reference's does.
/// </summary>
/// <remarks>
/// <b>Nothing tested WHEN the broadcast buffer is flushed, and that let a
/// second one-tick delivery bug live beside the first.</b>
/// <c>GameServerEndStep.gml:7-8</c> writes <c>global.sendBuffer</c> to every
/// socket and clears it in the server's END Step. So the reference's packet for
/// tick N is <c>[state update N][events N]</c>.
/// <para>
/// <b>"After everything that frame has been appended" is what this comment
/// used to say, and it is FALSE.</b> The GameServer is the oldest instance in
/// the game, so its End Step is the FIRST End Step of the frame: a
/// <c>PLAYER_DEATH</c>, a sentry's <c>DESTRUCTION</c> and the <c>BALANCE</c> a
/// death triggers are all appended in a Character's or a Sentry's own End Step,
/// miss that flush, and ship on tick N+1. Measured against the running game on
/// 2026-09-02 — see <c>GameServer.BroadcastEndStepEvents</c> for the probe and
/// the byte arithmetic. <b>So "events N" above means the Begin Step, alarm and
/// collision-stage ones only.</b>
/// </para>
/// <para>
/// This port flushed directly under <c>WriteStateUpdate</c>, before the
/// simulation ran. Everything <c>AdvanceSimulation</c> appends — WEAPON_FIRE,
/// PLAYER_DEATH, ubers, intel drops, generator destruction, arena — therefore
/// waited for the NEXT tick's flush, arriving a tick late and <em>ahead</em> of
/// the update it should have followed: <c>[events N][state update N+1]</c>.
/// </para>
/// <para>
/// <b>That is the discriminator, and it needs no framing to check.</b> The two
/// orderings differ in which message a flush LEADS with — an event, or the state
/// update. Reading the first byte of each tick's bytes is enough, so this test
/// does not have to parse the stream to catch the regression.
/// </para>
/// <para>
/// <b>A closed-loop replay cannot see this and did not.</b> A trace records
/// the byte stream, and the stream's contents and order are identical either
/// way — only the moment it leaves changes. A 3,599-tick replay of a live GM8
/// session came back with ZERO physics divergence while this was still wrong.
/// <b>Parity of the recorded stream is not parity of delivery.</b>
/// </para>
/// <para>
/// <b>Why it is worth a test rather than a comment.</b> A client simulates
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

            // Bots need the nav graphs or they never walk anywhere, never
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
        var leadingDeaths = 0;

        for (var tick = 0; tick < 600; tick++)
        {
            harness.Tick(1);

            var bytes = client.Take(client.Received.Count);

            if (bytes.Length == 0)
                continue;

            ticksWithBytes++;

            // Scoped to WEAPON_FIRE, which a Character appends in its BEGIN
            // Step and which must therefore ride in its own tick's packet.
            //
            // PLAYER_DEATH is deliberately NOT in this set any more, and its
            // absence is the whole 2026-09-02 finding: a death is appended in the
            // Character's END Step, after the GameServer's End Step flush, so it
            // LEADS the next tick's packet in the reference too. Counted below
            // instead, and required to happen.
            //
            // PLAYER_JOIN likewise legitimately leads: ServiceJoiningConnections
            // runs after the flush, and moving the flush past it changes what a
            // JOINING client sees — 23 tests in JoinAndBroadcastTests and
            // FullUpdateLayoutTests say so. The join sequence is a separate
            // contract this fix is not licensed to move.
            if ((MessageType)bytes[0] is MessageType.WeaponFire)
                leadingEvents++;

            if ((MessageType)bytes[0] is MessageType.PlayerDeath)
                leadingDeaths++;
        }

        // Not vacuous: a run where the client received nothing, or where no
        // tick carried an event, would pass the assertion below while proving
        // nothing at all.
        Assert.True(ticksWithBytes > 400, $"only {ticksWithBytes} ticks sent anything");

        Assert.Equal(0, leadingEvents);

        // The other direction, and it is the half that would go quiet if the
        // End Step broadcasts were folded back above the flush: a death has to
        // LEAD a packet, because the reference's flush has already gone by the
        // time a Character's End Step appends one. Zero here means either the
        // bots stopped killing each other — check the companion test — or the
        // boundary moved back.
        Assert.True(
            leadingDeaths > 0,
            "no packet led with a PLAYER_DEATH; the End Step flush boundary has moved");
    }

    /// <summary>
    /// The gate above is only meaningful if the traffic it watched actually
    /// contained the event messages that were arriving late.
    /// </summary>
    [Fact]
    public void TheWatchedTrafficReallyContainsEvents()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = "gg_debug";

            // Bots need the nav graphs or they never walk anywhere, never
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
