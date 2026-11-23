using GG2.Maps;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// A real client's roster surviving somebody ELSE disconnecting — the
/// compaction path, watched by a reader that is not the server.
/// </summary>
/// <remarks>
/// <b>`CONTRIBUTING.md`: "Roster indices are positional and shift on `PLAYER_LEAVE`
/// ... get this wrong and every message that names a player is off by one,
/// intermittently."</b> That failure is only observable to somebody watching a
/// slot OTHER than the one that left, which is why a single-client test can
/// never see it: the departing client is gone, and nothing it was told matters
/// any more.
/// <para>
/// ⚠️ <b>A bot removal is NOT this path.</b> The population manager dropping a
/// bot and a socket closing are different code, and the soak's
/// <c>--plan churn</c> exercises only the first. This is the second.
/// </para>
/// <para>
/// The survivor is deliberately given state that is expensive to renumber
/// wrongly — it is <em>carrying the intel</em> — because
/// <c>WorldState.RemoveSlot</c> renumbers "a projectile's owner, a last damage
/// dealer, who set somebody on fire, a medic's heal target", and a carrier is
/// the cheapest of those to set up and the loudest to get wrong.
/// </para>
/// </remarks>
public sealed class RosterCompactionClientTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    /// <summary>
    /// A client joining while the bot population is being CHANGED under it never
    /// disagrees with the server about how many players there are.
    /// </summary>
    /// <remarks>
    /// <b>This is the shape the live game produced on 2026-09-05.</b> The server
    /// logged, in the same second:
    /// <code>
    /// bot [BOT] 5 removed from player 5
    /// Player joined as player 6; 6 of 10 slots used
    /// </code>
    /// and that client took 197 GML errors four seconds later —
    /// <c>global.myself.object</c> reporting "Unknown variable object", and
    /// <c>doEventPlayerDeath</c> resolving a victim it could not read. A client
    /// that joined the same way a minute earlier took none. The population
    /// manager frees a seat for an arriving human, so a roster compaction lands
    /// inside the join window.
    /// <para>
    /// ⚠️ <b>A bot removal and a client departure are different paths</b>, which
    /// is why this is separate from
    /// <see cref="JoinsAndDeparturesInterleavedNeverDisagreeAboutThePlayerCount"/>.
    /// That one drives joins against <em>client</em> disconnects and passes;
    /// this one drives them against <c>SetBotFill</c>.
    /// </para>
    /// <para>
    /// The tick order is <c>_population.Service()</c> (GameServer.cs:715) well
    /// ahead of <c>AcceptConnections</c>/<c>ServiceJoiningConnections</c>
    /// (:821-822), so the burst should already see the post-removal roster.
    /// This test exists to hold that true rather than to assume it.
    /// </para>
    /// </remarks>
    [Fact]
    public void JoiningWhileTheBotPopulationChangesNeverDisagreesAboutThePlayerCount()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
            options.PlayerLimit = 10;
        });

        var live = new List<(LoopbackClient Socket, MirrorClient Mirror)>();
        var sawBots = false;

        try
        {
            // Fills chosen so the manager has to ADD on some rounds and REMOVE on
            // others, and so an arriving human displaces a bot.
            foreach (var fill in new[] { 6, 3, 7, 4, 8, 2 })
            {
                harness.Server.SetBotFill(fill);

                // Join in the same window the fill change is being serviced,
                // rather than after it has settled — the race is the point.
                harness.Tick(1);
                var socket = harness.JoinClient($"racer{live.Count}");
                var mirror = new MirrorClient(socket, Load, Map);
                live.Add((socket, mirror));

                harness.Tick(25);

                if (harness.Server.BotSlots.Count > 0)
                    sawBots = true;

                foreach (var (_, m) in live)
                {
                    m.Pump();
                    Assert.Empty(m.Impossible);
                }
            }

            // ⛔ Without bots the whole exercise is a plain join test wearing a
            // misleading name — SetBotFill would have been a no-op.
            Assert.True(sawBots, "no bot ever existed, so no bot was ever removed under a joiner");

            var updates = live.Sum(l =>
                l.Mirror.Of(MessageType.FullUpdate).Count + l.Mirror.Of(MessageType.QuickUpdate).Count);

            Assert.True(updates > 0, "no state update reached any client, so the count was never checked");
        }
        finally
        {
            foreach (var (socket, mirror) in live)
            {
                mirror.Dispose();
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// Joins and departures interleaved, with several clients watching at once:
    /// no state update ever disagrees with a client about how many players
    /// there are.
    /// </summary>
    /// <remarks>
    /// <b>This is the oracle for the open <c>cp_dirtbowl</c> client fatal.</b>
    /// The launcher's saved dialog from 2026-09-03 shows the death was preceded
    /// by <c>"Wrong number of players while deserializing state"</c> —
    /// <c>deserializeState.gml:11-13</c>, where the client compares the update's
    /// declared player count against its own roster, warns, and then
    /// <em>carries on deserialising anyway</em>. Everything after that read is
    /// misaligned, until a character bit lands on a slot the client holds as a
    /// spectator and <c>Character</c>'s Create event kills it with
    /// <c>unknown team ID: 2</c>. The sprite error is the symptom; the count
    /// disagreement is the cause.
    /// <para>
    /// One of the two logged deaths came <b>six seconds after launch</b>, which
    /// is at or near JOIN rather than after an area advance — so this drives
    /// joins and departures against each other rather than map changes.
    /// </para>
    /// <para>
    /// ⚠️ <b>Every roster size reached is asserted to have varied</b>, because a
    /// run where the roster never changed would satisfy the count check
    /// trivially. And <see cref="MirrorClient"/> only gained the ability to
    /// notice a mismatch on 2026-09-05 — before that its reader took the count
    /// off the wire and trusted it, exactly as <c>StateUpdateReader</c> still
    /// does in production.
    /// </para>
    /// </remarks>
    [Fact]
    public void JoinsAndDeparturesInterleavedNeverDisagreeAboutThePlayerCount()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
            options.PlayerLimit = 8;
        });

        var live = new List<(LoopbackClient Socket, MirrorClient Mirror)>();
        var sizes = new HashSet<int>();

        try
        {
            // A fixed interleaving rather than a random one, so a failure is
            // reproducible from the test alone.
            foreach (var drop in new[] { false, false, true, false, true, false, false, true, true, false })
            {
                var socket = harness.JoinClient($"j{live.Count}{(drop ? "d" : "")}");
                var mirror = new MirrorClient(socket, Load, Map);
                live.Add((socket, mirror));

                harness.Tick(12);

                // A team change makes the server write characters into the
                // updates, so the blocks after the count actually carry weight.
                socket.SendChangeTeam(live.Count % 2 == 0 ? Team.Red : Team.Blue);
                harness.Tick(18);

                if (drop && live.Count > 1)
                {
                    // Drop the OLDEST, so every survivor's slot shifts underneath
                    // it while the next join is arriving.
                    var (goneSocket, goneMirror) = live[0];
                    live.RemoveAt(0);
                    goneMirror.Dispose();
                    goneSocket.Dispose();
                    harness.Tick(15);
                }

                sizes.Add(harness.Server.RosterCount);

                foreach (var (_, mirror2) in live)
                {
                    mirror2.Pump();
                    Assert.Empty(mirror2.Impossible);
                }
            }

            Assert.True(
                sizes.Count > 1,
                $"the roster never changed size (always {string.Join(',', sizes)}), so nothing was churned");

            // ⛔ The count check only runs on a message that CARRIES a state
            // block. Without this, a run in which no update ever arrived would
            // report an empty Impossible list and prove nothing at all.
            var updates = live.Sum(l =>
                l.Mirror.Of(MessageType.FullUpdate).Count + l.Mirror.Of(MessageType.QuickUpdate).Count);

            Assert.True(updates > 0, "no state update reached any client, so the count was never checked");
        }
        finally
        {
            foreach (var (socket, mirror) in live)
            {
                mirror.Dispose();
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// When an EARLIER slot disconnects, the survivor slides down one and takes
    /// its character, its team and the briefcase with it.
    /// </summary>
    /// <remarks>
    /// The position check is the point. <c>WorldState.Clear</c> removes a
    /// character without compacting and <c>RemoveSlot</c> removes it <em>and</em>
    /// compacts; using the first where the second belongs hands a character to
    /// the wrong player. Asserting the survivor's coordinates are unchanged
    /// across the departure is what distinguishes "the roster shrank" from "the
    /// roster shrank and everybody kept their own body".
    /// </remarks>
    [Fact]
    public void AnEarlierSlotLeavingSlidesTheSurvivorDownWithItsCharacterAndIntel()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
        });

        // Order matters: the LEAVER must sit before the survivor, or nothing
        // shifts and the test is vacuous.
        var leaver = harness.JoinClient("leaver");
        var survivor = harness.JoinClient("survivor");
        using var mirror = new MirrorClient(survivor, Load, Map);

        var leaverSlot = harness.Server.RosterCount - 2;
        var survivorSlot = harness.Server.RosterCount - 1;
        Assert.True(leaverSlot < survivorSlot);

        harness.Tick(5);
        leaver.SendChangeTeam(Team.Blue);
        survivor.SendChangeTeam(Team.Red);

        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(survivorSlot, out _, out _, out _),
            "the survivor to spawn",
            maxTicks: 400);

        // Red steals blue's briefcase.
        var blue = harness.Server.IntelOf(Team.Blue);
        Assert.True(blue.HasBase, $"{Map} places no blue intel, so this test checks nothing");

        harness.TickUntil(
            () =>
            {
                harness.Server.Teleport(survivorSlot, blue.HomeX, blue.HomeY);
                return harness.Server.IsCarryingIntel(survivorSlot);
            },
            "the survivor to pick the briefcase up",
            maxTicks: 400);

        Assert.True(harness.Server.TryGetExactPosition(survivorSlot, out var x, out var y, out _));
        Assert.Equal(Team.Red, harness.Server.TeamOf(survivorSlot));

        mirror.Pump();
        mirror.ClearLog();
        var before = harness.Server.RosterCount;

        leaver.Dispose();
        harness.TickUntil(
            () => harness.Server.RosterCount < before,
            "the leaver to be removed from the roster",
            maxTicks: 400);

        // The survivor slid down into the departed slot.
        var moved = survivorSlot - 1;
        Assert.Equal(moved, harness.Server.RosterCount - 1);

        // ...and took everything with it.
        Assert.Equal(Team.Red, harness.Server.TeamOf(moved));
        Assert.True(harness.Server.IsCarryingIntel(moved));
        Assert.Equal(moved, harness.Server.IntelCarrier(Team.Blue));

        Assert.True(harness.Server.TryGetExactPosition(moved, out var x2, out var y2, out _));
        Assert.Equal(x, x2);
        Assert.Equal(y, y2);

        // Nobody is left holding the old index.
        Assert.False(harness.Server.IsCarryingIntel(survivorSlot));

        // And the client that stayed agrees, without ever being told something
        // it cannot render.
        harness.Tick(30);
        mirror.Pump();

        Assert.Equal(harness.Server.RosterCount, mirror.PlayerCount);
        Assert.Equal("survivor", mirror.Names[moved]);
        Assert.Equal(Team.Red, mirror.Teams[moved]);
        Assert.Empty(mirror.Impossible);
        Assert.False(survivor.IsClosed);
    }
}
