using System.Linq;
using GG2.Maps;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// The whole round loop through the live server: the clock on the wire, the
/// <c>MAP_END</c>, the ten seconds, and the map actually changing.
/// </summary>
/// <remarks>
/// <b>Nothing else can gate this.</b> No checked-in recording is long enough to
/// contain a round ending — the shortest limit the reference allows is a minute
/// and the longest fixture is eighty seconds — so there is no parity evidence
/// for any of it and there never will be from the recordings this project has.
/// What is here instead is the reference read closely and the server driven end
/// to end: a real <c>GameServer</c>, a real client socket, and the broadcast
/// decoded by the same <see cref="ServerStream"/> the parity recorder uses, so a
/// layout mistake fails as a decode error rather than as a wrong number.
/// <para>
/// A minute of game time is 1,800 ticks and the victory screen is another 300.
/// The harness ticks with no clock, so that is a fraction of a second — and it
/// is the shortest round the reference's own <c>max(1, ...)</c> clamp permits.
/// </para>
/// </remarks>
public sealed class MapRotationTests
{
    private const string Map = "ctf_truefort";

    /// <summary>One minute, the reference's floor. 30 × 60 ticks.</summary>
    private const int OneMinute = 1800;

    private static LevelMap LoadMap() => LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

    /// <summary>
    /// Decodes everything the server has broadcast, keeping the roster the way a
    /// real client does.
    /// </summary>
    private sealed class BroadcastReader
    {
        private readonly ServerStream _stream = new();
        private readonly ReceiveBuffer _buffer = new();

        public BroadcastReader(LevelMap map) => _stream.SetShape(GameModes.ShapeOf(map));

        /// <summary>Tells the framer the shape changed, as a client does on CHANGE_MAP.</summary>
        public void SetShape(LevelMap map) => _stream.SetShape(GameModes.ShapeOf(map));

        public List<ServerMessage> Drain(LoopbackClient client)
        {
            client.Poll();
            _buffer.Append(client.Take(client.Received.Count));

            var messages = new List<ServerMessage>();
            while (_stream.TryRead(_buffer, out var message))
                messages.Add(message);

            return messages;
        }
    }

    /// <summary>
    /// The round timer reaches the client and goes down, rather than sitting at
    /// its starting value.
    /// </summary>
    /// <remarks>
    /// The client runs its own copy of the round timer down between updates, so
    /// this field must actually count down rather than sit frozen at
    /// <c>MatchState.Timer</c>'s starting value.
    /// </remarks>
    [Fact]
    public void TheRoundTimerReachesTheClientAndCountsDown()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(map);

        harness.Tick();
        var first = reader.Drain(client)
            .Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        // The HUD block rides in a FULL_UPDATE and a CAPS_UPDATE and nowhere
        // else, and a FULL_UPDATE is sent to a joining connection alone — not
        // broadcast. So the second reading comes from a *second* client's own
        // join, a hundred ticks later. The only other source is the 3,600-frame
        // CAPS_UPDATE heartbeat, two minutes away.
        harness.Tick(100);

        var late = harness.JoinClient("late");
        var lateReader = new BroadcastReader(map);
        harness.Tick();

        var later = lateReader.Drain(late)
            .Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        // HudScalars for CTF is [timeLimitMins, timer] — see
        // StateUpdateReader.ReadHud, which keeps the block untyped on purpose.
        Assert.Equal(15, first.HudScalars[0]);

        // Near the limit rather than exactly on it: joining costs a handful of
        // ticks, and the round starts when the server does.
        Assert.InRange(first.HudScalars[1], 15 * OneMinute - 30, 15 * OneMinute);

        // The claim worth making is the *rate*: one tick off the clock per tick
        // of the server, over the hundred-odd ticks between the two readings.
        // A frozen timer gives a drop of 0; one decremented per state update
        // gives about fifteen.
        Assert.InRange(first.HudScalars[1] - later.HudScalars[1], 100, 112);
    }

    /// <summary>
    /// The clock running out ends the round, tells everybody, and changes the map
    /// ten seconds later.
    /// </summary>
    /// <remarks>
    /// The whole loop in one test, because the pieces are only meaningful in
    /// order: <c>MAP_END</c> names the next map and the winner,
    /// <c>global.mapchanging</c> holds for exactly
    /// <see cref="GG2.Simulation.Rounds.MapChangeDelay"/> ticks, then
    /// <c>CHANGE_MAP</c> goes out and a fresh round starts.
    /// <para>
    /// With nobody having captured, the round is a draw — <c>TEAM_SPECTATOR</c>,
    /// which is 2 on the wire.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheClockRunningOutEndsTheRoundAndChangesTheMap()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 1;
        });

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(map);

        // The round is decided in the Step of the tick the clock reaches zero
        // and broadcast at the start of the next, so the message lands within a
        // tick or two of the limit rather than exactly on it.
        var end = default(ServerMessage);
        var endTick = 0;

        for (var tick = 1; tick <= OneMinute + 30 && end is null; tick++)
        {
            harness.Tick();

            foreach (var message in reader.Drain(client))
            {
                if (message.Type != MessageType.MapEnd)
                    continue;

                end = message;
                endTick = tick;
            }
        }

        Assert.NotNull(end);

        // Within a few ticks of the limit: the round starts when the server
        // does, and the client joins a handful of ticks later, so the loop's
        // own count runs slightly behind the clock.
        Assert.InRange(endTick, OneMinute - 30, OneMinute + 4);

        // [len, name..., winners, currentMapArea] — sendEventMapEnd's body.
        var nameLength = end!.Payload[0];
        Assert.Equal(Map, System.Text.Encoding.ASCII.GetString(end.Payload, 1, nameLength));
        Assert.Equal((byte)Team.Spectator, end.Payload[1 + nameLength]);
        Assert.Equal(1, end.Payload[2 + nameLength]);

        // Ten seconds of victory screen, then the map change. Nothing before the
        // last tick of it may carry a CHANGE_MAP.
        //
        // The literal 300 is deliberate. Written as
        // `Rounds.MapChangeDelay - 1` this asserted nothing at all: changing the
        // constant moved the test with it, which a mutation run caught. A number
        // taken from the reference has to be spelled out where it is checked.
        var early = new List<ServerMessage>();
        for (var i = 0; i < 299; i++)
        {
            harness.Tick();
            early.AddRange(reader.Drain(client));
        }

        Assert.DoesNotContain(early, m => m.Type == MessageType.ChangeMap);

        harness.Tick();
        var changed = reader.Drain(client).SingleOrDefault(m => m.Type == MessageType.ChangeMap);

        Assert.NotNull(changed);
        Assert.Equal(Map, System.Text.Encoding.ASCII.GetString(changed!.Payload, 1, changed.Payload[0]));
    }

    /// <summary>
    /// The new round starts clean: a full clock, no captures, and everybody back
    /// on the team-select screen.
    /// </summary>
    /// <remarks>
    /// <c>with(Player) { ... team = TEAM_SPECTATOR; alarm[5] = 1 }</c>. A player
    /// who was on a team before the change is a spectator after it and stays
    /// characterless until they pick again — which is what the client does too,
    /// because <c>CHANGE_MAP</c> sends it back to the menu.
    /// </remarks>
    [Fact]
    public void TheNewRoundStartsCleanAndEverybodyIsASpectator()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 1;
        });

        var client = harness.JoinClient("player");
        var reader = new BroadcastReader(map);

        harness.Tick();
        client.SendChangeTeam(Team.Red);
        harness.Tick(2);

        var slot = reader.Drain(client)
            .Single(m => m.Type == MessageType.PlayerSpawn).Payload[0];

        // Through the whole round and the victory screen.
        harness.Tick(OneMinute + GG2.Simulation.Rounds.MapChangeDelay + 8);
        reader.Drain(client);

        // A few more ticks so the fresh map's first update goes out.
        harness.Tick(8);
        var update = reader.Drain(client).LastOrDefault(m => m.State is not null)?.State;

        Assert.NotNull(update);

        // The spawn wave the reset arms spawns nobody, because everybody is a
        // spectator again — so the slot that had a character has none. The
        // subobjects byte is in every tier, which is why this is the assertion
        // an ordinary update can carry; the fresh clock is gated by
        // TheServerKeepsRotatingRoundAfterRound below.
        Assert.True(slot < update!.Players.Length);
        Assert.Null(update.Players[slot].Character);
    }

    /// <summary>
    /// The server plays a second round after the first, on its own.
    /// </summary>
    /// <remarks>
    /// <b>The claim a single-shot <c>Start</c> could never make.</b> Everything
    /// a map change rebuilds — the collision world, the spawn points, the
    /// briefcases, the round — has to come back in the same shape it had at
    /// startup, or the second round is subtly different from the first and
    /// nothing says so until a player notices. Two <c>MAP_END</c>s a round
    /// apart is the cheapest statement that it did.
    /// </remarks>
    [Fact]
    public void TheServerKeepsRotatingRoundAfterRound()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 1;
        });

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(map);

        // The join itself sends this client a CHANGE_MAP, so drain that before
        // counting or the first round appears to end twice.
        harness.Tick();
        reader.Drain(client);

        var ends = new List<int>();
        var changes = 0;

        // Two rounds plus both victory screens, with room to spare.
        var total = 2 * (OneMinute + GG2.Simulation.Rounds.MapChangeDelay) + 60;

        for (var tick = 1; tick <= total; tick++)
        {
            harness.Tick();

            foreach (var message in reader.Drain(client))
            {
                if (message.Type == MessageType.MapEnd)
                    ends.Add(tick);
                else if (message.Type == MessageType.ChangeMap)
                    changes++;
            }
        }

        Assert.True(ends.Count == 2, "rounds ended at " + string.Join(", ", ends));
        Assert.Equal(2, changes);

        // The second round is a full round long, measured from the change that
        // started it — one minute of play plus the ten seconds before it.
        Assert.Equal(OneMinute + GG2.Simulation.Rounds.MapChangeDelay, ends[1] - ends[0]);
    }

    /// <summary>
    /// The rotation moves to the next loadable map and skips one that is not
    /// there.
    /// </summary>
    /// <remarks>
    /// <c>nextMapInRotation.gml</c> walks forward from the current index and
    /// takes the first entry that exists, wrapping. The reference exits when
    /// none does; this server stays up on the map it is on, because a headless
    /// server that stops serving is the worse failure.
    /// </remarks>
    [Fact]
    public void TheRotationSkipsAMapThatIsNotThere()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 1;
            o.MapRotation = ["no_such_map", "ctf_2dfort", Map];
        });

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(map);

        harness.Tick(OneMinute + 8);

        var end = reader.Drain(client).SingleOrDefault(m => m.Type == MessageType.MapEnd);

        Assert.NotNull(end);
        Assert.Equal(
            "ctf_2dfort",
            System.Text.Encoding.ASCII.GetString(end!.Payload, 1, end.Payload[0]));
    }

    /// <summary>
    /// A client joining after a rotation is told the map the server is actually
    /// on.
    /// </summary>
    /// <remarks>
    /// Both the <c>HELLO</c> and the join path's <c>CHANGE_MAP</c> must name the
    /// map the server is <em>currently</em> on, not <c>ServerOptions.Map</c> (the
    /// map it <em>started</em> on) — those are the same string until the first
    /// rotation and different afterwards, so a stale reference would join every
    /// later player to the wrong map and silently desync their simulation from
    /// everybody else's, since nothing in the protocol re-states the map.
    /// <para>
    /// The map change reuses <c>OpenMap</c>, the same path <c>Start</c> uses, so
    /// both are on the same footing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClientJoiningAfterARotationIsToldTheNewMap()
    {
        const string Second = "ctf_2dfort";

        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 1;
            o.MapRotation = [Second];
        });

        // Round one, its victory screen, and a few ticks of round two.
        harness.Tick(OneMinute + GG2.Simulation.Rounds.MapChangeDelay + 20);

        // The handshake by hand, because ServerHarness.JoinClient asserts the
        // greeting names Options.Map — which is exactly what must no longer be
        // true here.
        var client = harness.Connect();
        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the server greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        Assert.Equal(harness.Options.ServerName, client.TakeString1());
        Assert.Equal(Second, client.TakeString1());
        Assert.Equal(string.Empty, client.TakeString1()); // built-in map: no MD5
        Assert.Equal(0, client.TakeByte());               // pluginsRequired
        Assert.Equal(string.Empty, client.TakeString2()); // pluginList

        client.SendReserveSlot("late");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, client.TakeMessageType());

        // The join block is the *other* place the map name goes out, and it is
        // the one that actually makes the client load a map. Both were stale.
        var before = harness.Server.PlayerCount;
        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > before, "the join");

        var reader = new BroadcastReader(LevelMap.FromPng(Second, ReferenceTree.ReadMap(Second)));
        var change = reader.Drain(client).First(m => m.Type == MessageType.ChangeMap);

        Assert.Equal(
            Second,
            System.Text.Encoding.ASCII.GetString(change.Payload, 1, change.Payload[0]));
    }

    /// <summary>
    /// A rotation naming nothing loadable keeps the current map rather than
    /// taking the server down.
    /// </summary>
    [Fact]
    public void ARotationWithNothingLoadableKeepsTheCurrentMap()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 1;

            // The second is not a missing map but an illegal name — a path
            // traversal, which LoadMap would throw on. It must never reach the
            // wire in a MAP_END.
            o.MapRotation = ["no_such_map", "../../etc/passwd"];
        });

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(map);

        harness.Tick(OneMinute + 8);
        var end = reader.Drain(client).SingleOrDefault(m => m.Type == MessageType.MapEnd);

        Assert.NotNull(end);
        Assert.Equal(Map, System.Text.Encoding.ASCII.GetString(end!.Payload, 1, end.Payload[0]));
    }
    /// <summary>
    /// <b><c>--time-limit 0</c> is a ONE-MINUTE round, not an unlimited
    /// one</b> — and that is correct, because the reference cannot express "no
    /// limit" at all.
    /// </summary>
    /// <remarks>
    /// <c>game_init.gml:96</c> reads the ini through <c>max(1, min(255, ...))</c>,
    /// so a zero there is a one-minute round and the shortest match the
    /// reference permits is a minute. <c>GameServer</c> applies the same floor
    /// (see its round-configuration block), which is what keeps the port
    /// wire-compatible with an operator who writes <c>Time Limit = 0</c>.
    /// <para>
    /// <b>Zero means "no limit" one layer down, in
    /// <c>WorldState.TimeLimitMinutes</c>, and only there.</b> That escape exists
    /// so a defaulted world — every unit test and every bot rollout — is not a
    /// round whose clock already reads 0:00. Confusing the two conventions is
    /// not academic: <c>gg2-server --time-limit 0</c> expecting an endless round
    /// instead gets put back on the team-select screen about fifty seconds in,
    /// which looks like an AFK timeout unless the map change is traced back to
    /// this floor.
    /// </para>
    /// <para>
    /// The gate is the wire reading as much as the <c>MAP_END</c>: if the floor
    /// were dropped the HUD would carry 0 and no round would ever end, so both
    /// halves of this fail. <see cref="ALimitAboveTheFloorIsNotFlooredToIt"/> is
    /// the control proving it is the floor being tested and not "any server ends
    /// a round at 1,800 ticks".
    /// </para>
    /// </remarks>
    [Fact]
    public void AZeroTimeLimitIsFlooredToOneMinuteRatherThanMeaningNoLimit()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 0;
        });

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(map);

        harness.Tick();
        var joined = reader.Drain(client)
            .Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        // The floor as the client sees it. A zero here would be a clock the
        // client draws as 0:00 and a round that never ends.
        Assert.Equal(1, joined.HudScalars[0]);

        harness.Tick(OneMinute + 8);
        var end = reader.Drain(client).SingleOrDefault(m => m.Type == MessageType.MapEnd);

        Assert.True(end is not null, "--time-limit 0 produced a round that never ends");
    }

    /// <summary>
    /// The control: a limit above the floor is left alone, so the test above is
    /// measuring the floor rather than the harness.
    /// </summary>
    /// <remarks>
    /// Without this, deleting the <c>max(1, ...)</c> and hard-coding a
    /// one-minute round everywhere would pass
    /// <see cref="AZeroTimeLimitIsFlooredToOneMinuteRatherThanMeaningNoLimit"/>.
    /// </remarks>
    [Fact]
    public void ALimitAboveTheFloorIsNotFlooredToIt()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.TimeLimitMinutes = 2;
        });

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(map);

        harness.Tick();
        var joined = reader.Drain(client)
            .Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        Assert.Equal(2, joined.HudScalars[0]);

        // Well past where the floored round would have ended, and nothing yet.
        harness.Tick(OneMinute + 8);
        Assert.DoesNotContain(reader.Drain(client), m => m.Type == MessageType.MapEnd);
    }
}
