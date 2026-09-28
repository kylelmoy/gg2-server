using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A client joining on the same tick something happened is not told about it
/// twice.
/// </summary>
/// <remarks>
/// <b>The join burst is written at the very bottom of the tick</b>
/// (<c>ServiceJoiningConnections</c>, <c>GameServer.cs:822</c>) and its
/// <c>FULL_UPDATE</c> therefore carries the world as <c>AdvanceSimulation</c>
/// left it — that tick's deaths included. The broadcast buffer the joiner
/// inherits was filled between the flush (<c>:802</c>) and that point, by
/// <c>BroadcastEndStepEvents</c> (<c>:807</c>) and by the control port and web
/// admin executing against the live game (<c>:812-814</c>). Sending both tells
/// the joiner the same thing twice.
/// <para>
/// ⚠️ <b>The reference does not have to solve this because its burst PREDATES
/// the events.</b> <c>GameServerEndStep.gml:18-20</c> services a joining player
/// in the GameServer's End Step, which is the first End Step of the frame, so
/// <c>Character.events/End Step.xml:43</c>'s <c>doEventPlayerDeath</c> has not
/// run and the dying character is still in the <c>serializeState</c> the joiner
/// is handed. The port fuses Step and End Step into one
/// <c>MatchSimulation.Tick</c> and cannot serialise between them, so it closes
/// the gap from the other end: the joiner is not sent what its burst already
/// described.
/// </para>
/// <para>
/// <b>What a stock client does with the duplicate</b>:
/// <c>doEventPlayerDeath.gml:41</c> reads
/// <c>victim.object.currentWeapon.object_index</c>, and a victim the client
/// never had a character for holds <c>object == -1</c> — GM8's "Unknown
/// variable currentWeapon". Four of those were logged live on 2026-09-05 by a
/// client that joined while bots were fighting, and nothing in this repository
/// could see it, because every gate here reads the SERVER.
/// </para>
/// </remarks>
public sealed class JoinDuringDeathTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    /// <summary>
    /// A client that joins on a tick somebody died is never sent that death.
    /// </summary>
    /// <remarks>
    /// ⛔ <b><c>racesExercised</c> is the load-bearing half.</b> "No blind death
    /// arrived" is satisfied just as well by a run where no join ever landed on
    /// a death tick — so the second assertion is that the race actually
    /// happened, counted from the SERVER: a slot alive before the joining tick
    /// and dead after it. Without it this test would keep passing if the bots
    /// stopped fighting, if the clustering stopped working, or if joins stopped
    /// completing.
    /// <para>
    /// The bots are teleported onto one spot every tick so both teams are
    /// permanently in each other's faces. That is what turns a death every few
    /// hundred ticks into one every few, and it is why a bounded number of
    /// attempts reliably lands on the race.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClientJoiningOnTheTickSomebodyDiedIsNotSentThatDeath()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
            options.PlayerLimit = 40;

            // ⛔ Seeded, because the vacuity guard at the bottom is a MEASUREMENT
            // of this match and not a property of the code. Whether five joins
            // land on a death tick inside 400 attempts depends on whether the
            // bots kill each other, which depends on the simulation's random
            // stream — and GameServer.Start seeds that from the clock. Measured:
            // one full-suite run in three ended with "only 0 join(s) landed on a
            // tick somebody died", which is the guard correctly refusing to
            // report a pass it had not earned, on a run where nothing was wrong.
            // ⚠️ A seed is not a licence to relax the guard: the point is that
            // the same match runs every time and either exercises the race or
            // visibly does not.
            options.RandomSeed = 20260906;

            // ⚠️ FillToPlayers far above MaxBots ON PURPOSE, rather than
            // SetBotFill(12). SetBotFill makes the two equal, so every arriving
            // human DISPLACES a bot — and a bot removal compacts the roster, which
            // renumbers every slot above it. This test decides that the race was
            // exercised by watching a slot go from alive to dead across the
            // joining tick, and a compaction on that same tick makes every slot
            // above the departure look like a death. Holding the bot count flat
            // is what keeps that reading honest; join-versus-bot-removal is
            // RosterCompactionClientTests' subject, not this one's.
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 60,
                MaxBots = 20,
                MinHumans = 1,

            };
        });

        // BotPopulation.Target's MinHumans is 1, so an empty server runs no bots
        // at all until a human is on the roster.
        var anchor = harness.JoinClient("anchor");
        anchor.SendChangeTeam(Team.Red);
        harness.Tick(300);

        var bots = harness.Server.BotSlots.ToArray();
        Assert.True(bots.Length > 4, $"only {bots.Length} bots, so there is no firefight to join into");

        double px = 0, py = 0;

        foreach (var slot in bots)
        {
            if (harness.Server.TryGetExactPosition(slot, out var x, out var y, out _))
            {
                px = x;
                py = y;
                break;
            }
        }

        bool[] Alive()
        {
            var alive = new bool[harness.Server.RosterCount];

            for (var s = 0; s < alive.Length; s++)
                alive[s] = harness.Server.IsAlive(s);

            return alive;
        }

        // ⚠️ TWO piles a short way apart, not one. Piled on a single point the
        // bots barely kill each other at all — measured: 20 soldiers and
        // demomen standing inside one another produced ZERO deaths over 400
        // joins, because splash weapons are not fired at zero range. Separated
        // by a corridor's width both teams have a target to shoot at and the
        // death rate is what makes the race land often enough to assert on.
        void Cluster()
        {
            foreach (var slot in harness.Server.BotSlots)
                harness.Server.Teleport(slot, harness.Server.TeamOf(slot) == Team.Red ? px : px + 90, py);
        }

        var racesExercised = 0;
        var deathsDelivered = 0;
        var blind = new List<string>();

        for (var attempt = 0; attempt < 400 && racesExercised < 5; attempt++)
        {
            // The handshake is driven by hand rather than through
            // ServerHarness.JoinClient because the tick the join COMPLETES on is
            // the thing under test and has to be identifiable.
            var socket = harness.Connect();
            socket.SendHello();
            harness.TickUntil(() => socket.Received.Count > 0, "the server greeting");
            socket.TakeMessageType();
            socket.TakeString1();
            socket.TakeString1();
            socket.TakeString1();
            socket.TakeByte();
            socket.TakeString2();

            socket.SendReserveSlot($"j{attempt}");
            harness.TickUntil(() => socket.Received.Count > 0, "the slot reservation");
            socket.TakeMessageType();

            // ⚠️ NOT RosterCount. An arriving human displaces a bot
            // (BotPopulation.Target counts a mere reservation), so the roster
            // size can stay flat across the join it is supposed to detect —
            // ServerHarness.JoinClient carries the same warning.
            int Humans() => harness.Server.RosterCount - harness.Server.BotSlots.Count;

            var before = Humans();
            socket.SendPlayerJoin();

            var joined = false;
            var diedOnTheJoinTick = false;

            for (var i = 0; i < 20 && !joined; i++)
            {
                Cluster();
                var was = Alive();
                harness.Tick();
                var now = Alive();

                joined = Humans() > before;

                if (!joined)
                    continue;

                for (var s = 0; s < Math.Min(was.Length, now.Length); s++)
                {
                    if (was[s] && !now[s])
                        diedOnTheJoinTick = true;
                }
            }

            Assert.True(joined, $"attempt {attempt} never completed its join");

            if (diedOnTheJoinTick)
                racesExercised++;

            using var mirror = new MirrorClient(socket, Load, Map);

            // What this client holds for each slot's character, which is exactly
            // what a GM8 client's Player.object holds.
            var hasCharacter = new Dictionary<int, bool>();

            for (var i = 0; i < 6; i++)
            {
                Cluster();
                harness.Tick();
                mirror.Pump();

                foreach (var message in mirror.Messages)
                {
                    if (message.State is { } state)
                    {
                        for (var s = 0; s < state.Players.Length; s++)
                            hasCharacter[s] = state.Players[s].Character is not null;

                        continue;
                    }

                    if (message.Type != MessageType.PlayerDeath)
                        continue;

                    deathsDelivered++;
                    var victim = message.Payload[0];

                    if (hasCharacter.TryGetValue(victim, out var had) && !had)
                    {
                        blind.Add(
                            $"attempt {attempt}: PLAYER_DEATH for slot {victim}, which this "
                            + "client holds with no character — doEventPlayerDeath.gml:41 "
                            + "dereferences victim.object.currentWeapon on -1");
                    }
                }

                mirror.ClearLog();
                Assert.Empty(mirror.Impossible);
            }

            // ⛔ THE ADJACENT FAILURE THIS FIX COULD CAUSE, asserted rather than
            // assumed. The joiner's own PLAYER_JOIN is appended to the broadcast
            // immediately AFTER its skip mark is taken, so it must survive the
            // skip — a mark one message too long would swallow it, and a stock
            // client that never sees its own PLAYER_JOIN never binds
            // global.myself (ClientBeginStep.gml:205-208) and is broken for the
            // rest of the session with nothing on the server to show for it.
            Assert.Contains($"j{attempt}", mirror.Names);

            socket.Dispose();
            harness.Tick(2);
        }

        // ⛔ Both controls, and the test is worthless without them: the first
        // says a join really did land on a death tick, the second that deaths
        // were reaching joiners at all rather than being suppressed wholesale.
        Assert.True(
            racesExercised >= 5,
            $"only {racesExercised} join(s) landed on a tick somebody died, so the race was "
            + "barely exercised and a clean run says nothing");

        Assert.True(deathsDelivered > 0, "no PLAYER_DEATH reached any joiner, so nothing was checked");

        Assert.Empty(blind);
    }
}
