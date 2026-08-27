using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Arena: a sequence of elimination rounds on one map.
/// </summary>
/// <remarks>
/// <c>Objects/Overlays/Gamemodes/ArenaHUD.events/{Create, Step}.xml</c> and
/// <c>Scripts/Maps/Arena/</c>.
/// <para>
/// <b>The only mode where nobody respawns.</b> <c>spawningAllowed()</c> is true
/// only while waiting for players or during the setup period, so a death in the
/// round proper is final — which is what makes eliminating the enemy team a win
/// condition alongside taking the point.
/// </para>
/// <para>
/// <b>No fixture reaches it</b>, like every other mode: every recording in the
/// project is on one Capture the Flag map.
/// </para>
/// </remarks>
public sealed class ArenaTests
{
    private const string Map = "arena_lumberyard";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static (WorldState State, CollisionWorld World) Started(int capLimit = 5)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = capLimit;

        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        return (state, world);
    }

    /// <summary>Adds a player, with or without a character.</summary>
    private static void Put(ref WorldState state, int slot, Team team, bool alive = true)
    {
        state.Players[slot] = new PlayerState
        {
            Team = team,
            Class = PlayerClass.Soldier,
            RespawnTimer = Timers.Inactive,
        };
        state.HasPlayer[slot] = true;

        if (!alive)
            return;

        var character = CharacterState.Fresh(PlayerClass.Soldier, team);
        character.X = 2000;
        character.Y = 700;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Soldier, character.X, character.Y);
        state.Alive[slot] = true;
    }

    private static TickEvents Tick(CollisionWorld world, ref WorldState state, int count = 1)
    {
        var events = default(TickEvents);

        for (var i = 0; i < count; i++)
        {
            events = default;
            MatchSimulation.Tick(world, ref state, [], ref events);
        }

        return events;
    }

    // --- the state machine ----------------------------------------------------

    /// <summary>A fresh map waits for somebody on each team.</summary>
    [Fact]
    public void AFreshMapWaitsForPlayers()
    {
        var (state, world) = Started();

        Assert.Equal(ArenaState.Waiting, state.ArenaState);

        Tick(world, ref state, 100);
        Assert.Equal(ArenaState.Waiting, state.ArenaState);

        // One team is not enough.
        Put(ref state, 1, Team.Red);
        Tick(world, ref state, 10);
        Assert.Equal(ArenaState.Waiting, state.ArenaState);
    }

    /// <summary>One player a side starts the setup period.</summary>
    [Fact]
    public void OnePlayerASideStartsARound()
    {
        var (state, world) = Started();

        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        var events = Tick(world, ref state);

        Assert.Equal(ArenaState.RoundSetup, state.ArenaState);
        Assert.Equal(Arena.RoundSetupTicks, state.ArenaRoundStart);
        Assert.True(events.ArenaRestarted);
    }

    /// <summary>The setup period runs down and the round proper begins.</summary>
    /// <remarks>
    /// ⚠️ <b>The point's unlock counter is frozen during setup</b>
    /// (<c>if(state != ARENA_STATE_ROUND_SETUP and cpUnlock > 0)</c>) and is
    /// reset again when the round starts — so the minute before the point opens
    /// is a minute of the <em>round</em>, not of the setup.
    /// </remarks>
    [Fact]
    public void TheSetupPeriodRunsDownIntoTheRound()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        Tick(world, ref state);
        Assert.Equal(ArenaState.RoundSetup, state.ArenaState);
        Assert.Equal(Arena.UnlockTicks, state.ControlPointUnlock);

        // Frozen through the whole setup. ⚠️ The counter is read and then
        // spent, so 299 more ticks leave one on it — the same off-by-one every
        // countdown in this codebase has.
        Tick(world, ref state, Arena.RoundSetupTicks - 1);
        Assert.Equal(ArenaState.RoundSetup, state.ArenaState);
        Assert.Equal(1, state.ArenaRoundStart);
        Assert.Equal(Arena.UnlockTicks, state.ControlPointUnlock);

        var events = Tick(world, ref state);

        Assert.Equal(ArenaState.RoundProper, state.ArenaState);
        Assert.True(events.ArenaRoundStarted);
        Assert.Equal(Arena.UnlockTicks, state.ControlPointUnlock);

        // And now it moves.
        Tick(world, ref state, 10);
        Assert.Equal(Arena.UnlockTicks - 10, state.ControlPointUnlock);
    }

    /// <summary>Nobody respawns during the round proper.</summary>
    /// <remarks>
    /// <c>spawningAllowed()</c>, and it is the whole mode: without it there is
    /// no such thing as eliminating a team. ⚠️ The alarm still fires and is
    /// still spent — the guard is inside <c>PlayerSpawn</c>.
    /// </remarks>
    [Theory]
    [InlineData(ArenaState.Waiting, true)]
    [InlineData(ArenaState.RoundSetup, true)]
    [InlineData(ArenaState.RoundProper, false)]
    [InlineData(ArenaState.RoundEnd, false)]
    public void NobodyRespawnsOnceTheRoundHasStarted(ArenaState phase, bool spawns)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var points = new SpawnPoints(level);
        var (state, _) = Started();

        state.ArenaState = phase;

        // Hold each phase still: a setup period with no countdown left starts
        // the round on this tick, and a scoreboard with none left ends it — and
        // both sweep, which would swallow what this is measuring.
        state.ArenaRoundStart = Arena.RoundSetupTicks;
        state.ArenaEndCount = Arena.RoundEndTicks;

        Put(ref state, 1, Team.Red, alive: false);
        state.Players[1].RespawnTimer = 1;

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events, points);

        Assert.Equal(spawns, events.Spawned[1]);
        Assert.Equal(spawns, state.Alive[1]);

        // Spent either way.
        Assert.Equal(Timers.JustFired, state.Players[1].RespawnTimer);
    }

    // --- winning a round ------------------------------------------------------

    /// <summary>Wiping the enemy team out wins the round.</summary>
    [Fact]
    public void EliminatingTheEnemyWinsTheRound()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundProper;
        state.ArenaRoundStart = 0;

        Tick(world, ref state);
        Assert.Equal(ArenaState.RoundProper, state.ArenaState);

        state.Alive[2] = false;

        var events = Tick(world, ref state);

        Assert.Equal(ArenaState.RoundEnd, state.ArenaState);
        Assert.Equal(Team.Red, state.ArenaWinner);
        Assert.Equal(1, state.ArenaRedWins);
        Assert.Equal(0, state.ArenaBlueWins);
        Assert.True(events.ArenaRoundEnded);
        Assert.Equal(Team.Red, events.ArenaRoundWinner);
    }

    /// <summary>Taking the point wins the round too, and beats an elimination.</summary>
    /// <remarks>
    /// The point is tested first, so a tick where a team both completes a
    /// capture and loses its last player goes to whoever holds the point.
    /// </remarks>
    [Fact]
    public void TakingThePointWinsTheRound()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundProper;
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Blue;

        Tick(world, ref state);

        Assert.Equal(ArenaState.RoundEnd, state.ArenaState);
        Assert.Equal(Team.Blue, state.ArenaWinner);
        Assert.Equal(1, state.ArenaBlueWins);
    }

    /// <summary>
    /// The scoreboard holds for ten seconds and then the next round's setup
    /// begins.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The map is not swept when a round ends.</b> The bodies, the
    /// sentries and anything in the air stay for the whole scoreboard period —
    /// only the next restart clears them.
    /// </remarks>
    [Fact]
    public void TheScoreboardHoldsAndThenTheNextRoundStarts()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundProper;
        Tick(world, ref state);
        state.Alive[2] = false;
        Tick(world, ref state);

        Assert.Equal(ArenaState.RoundEnd, state.ArenaState);
        Assert.Equal(Arena.RoundEndTicks, state.ArenaEndCount);

        // Red's body is still standing through the whole scoreboard.
        Tick(world, ref state, Arena.RoundEndTicks - 1);
        Assert.Equal(ArenaState.RoundEnd, state.ArenaState);
        Assert.True(state.Alive[1]);

        var events = Tick(world, ref state);

        Assert.Equal(ArenaState.RoundSetup, state.ArenaState);
        Assert.True(events.ArenaRestarted);
    }

    /// <summary>A restart sweeps the map and respawns everybody.</summary>
    /// <remarks>
    /// <c>destroyGameplayObjects()</c> then <c>respawnEveryone()</c> — and the
    /// respawn is a destroy followed by a spawn, so every character runs its
    /// whole Destroy event on the way.
    /// </remarks>
    [Fact]
    public void ARestartSweepsTheMap()
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var points = new SpawnPoints(level);
        var (state, _) = Started();

        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.HasSentry[1] = true;
        state.Sentries[1] = SentryState.Build(Team.Red, 100, 100, 1);

        state.SpawnProjectile(new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            OwnerSlot = 1,
            Team = Team.Red,
            Reflector = ProjectileState.NoOwner,
        });

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events, points);

        Assert.True(events.ArenaRestarted);
        Assert.False(state.HasSentry[1]);
        Assert.Equal(0, state.LiveProjectileCount);

        // ⚠️ Both respawned on *this* tick. `respawnEveryone()` calls
        // `PlayerSpawn()` directly rather than arming `alarm[5]`, and the alarm
        // stage has already run by the time a HUD's Step does — so arming it
        // would put everybody in a new round a tick late.
        Assert.True(events.Spawned[1]);
        Assert.True(events.Spawned[2]);
        Assert.True(state.Alive[1]);
        Assert.True(state.Alive[2]);
    }

    /// <summary>A team emptying out drops the map back to waiting.</summary>
    [Fact]
    public void ATeamEmptyingOutGoesBackToWaiting()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundEnd;
        state.ArenaEndCount = 1;

        // Blue leaves between rounds.
        state.HasPlayer[2] = false;
        state.Alive[2] = false;

        var events = Tick(world, ref state);

        Assert.Equal(ArenaState.Waiting, state.ArenaState);
        Assert.True(events.ArenaWaitingForPlayers);
    }

    // --- winning the map ------------------------------------------------------

    /// <summary>Winning enough rounds wins the map.</summary>
    /// <remarks>
    /// ⚠️ <b>Only ever checked at the end of a round</b>, so a running round
    /// always gets to finish — the reference's own summary says so.
    /// </remarks>
    [Fact]
    public void WinningEnoughRoundsWinsTheMap()
    {
        var (state, world) = Started(capLimit: 3);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundEnd;
        state.ArenaEndCount = 1;
        state.ArenaRedWins = 3;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>
    /// The map clock running out is only acted on between rounds — except while
    /// waiting.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>ARENA_STATE_WAITING</c> is the one place the map can end without a
    /// round finishing: an empty server whose clock runs out is a draw
    /// immediately.
    /// </remarks>
    [Theory]
    [InlineData(ArenaState.Waiting, true)]
    [InlineData(ArenaState.RoundProper, false)]
    public void TheMapClockEndsTheMapOnlyBetweenRounds(ArenaState phase, bool decided)
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = phase;
        state.RoundTimer = 1;

        // In Waiting the presence of both teams would otherwise start a round,
        // so the clock has to be checked first — and it is.
        Tick(world, ref state);

        Assert.Equal(decided, state.RoundDecided);

        if (decided)
            Assert.Equal(Team.Spectator, state.Winner);
    }

    /// <summary>The clock runs in every state, and overtime latches.</summary>
    /// <remarks>
    /// ⚠️ Unlike the control-point modes', Arena's overtime is a latch: set once
    /// the clock is out and cleared only by a fresh round.
    /// </remarks>
    [Fact]
    public void OvertimeLatchesUntilTheNextRound()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundProper;
        state.RoundTimer = 1;

        Tick(world, ref state, 5);
        Assert.True(state.Overtime);

        // Killing blue ends the round; the scoreboard keeps the latch...
        state.Alive[2] = false;
        Tick(world, ref state);
        Assert.True(state.Overtime);

        // ...and it is a restart that clears it. (Which cannot happen here,
        // because the clock is out and the map ends instead — so assert the
        // restart path directly.)
        var (fresh, freshWorld) = Started();
        Put(ref fresh, 1, Team.Red);
        Put(ref fresh, 2, Team.Blue);
        fresh.Overtime = true;

        Tick(freshWorld, ref fresh);
        Assert.False(fresh.Overtime);
    }

    // --- capping power --------------------------------------------------------

    /// <summary>
    /// The arena point is captured faster the fewer of the enemy are alive.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The teams are crossed</b>: blue's capping power is computed from
    /// <em>red's</em> numbers. With every enemy alive it is 1 and with none it
    /// is <see cref="Arena.MaxCappingPower"/> — which is what stops a wiped-out
    /// team stalling a round by hiding. <c>max(1, reds)</c> keeps the division
    /// safe on an empty team.
    /// </remarks>
    [Theory]
    [InlineData(4, 4, 1.0)]
    [InlineData(4, 0, 4.0)]
    [InlineData(4, 2, 1.6)]
    [InlineData(0, 0, 4.0)]
    [InlineData(1, 1, 1.0)]
    public void TheArenaPointIsTakenFasterAsTheEnemyDies(int enemies, int alive, double power)
    {
        Assert.Equal(power, Arena.CappingPower(enemies, alive), 10);
    }

    /// <summary>
    /// A capture on an arena map really runs at that rate.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Half the enemy team, not all of it</b> — wiping them out ends the
    /// round before a capture can matter, so the only reachable case is a
    /// partial one. Two blues with one alive gives
    /// <c>2 / (0.75×1 + 0.25×2) = 1.6</c>.
    /// <para>
    /// ⚠️ And it applies from the <b>first</b> tick of a capture. The reference
    /// computes both teams' powers unconditionally and the capture branch reads
    /// the one belonging to the team it has just assigned — so a first pass that
    /// used 1 because nothing was being captured yet would be a tick short on
    /// every point taken.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCappingPowerReachesTheMeter()
    {
        var (state, world) = Started();

        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);
        Put(ref state, 3, Team.Blue, alive: false);

        state.ArenaState = ArenaState.RoundProper;
        state.ControlPointUnlock = 0;
        state.ControlPoints[0].Locked = false;

        // Stand the red soldier on the point.
        var zone = world.CaptureZones[0];
        state.Characters[1].X = (zone.Left + zone.Right) / 2.0;
        state.Characters[1].Y = (zone.Top + zone.Bottom) / 2.0;

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.Equal(Team.Red, state.ControlPoints[0].CappingTeam);
        Assert.Equal(1.6, state.ControlPoints[0].Capping, 10);

        MatchSimulation.Tick(world, ref state, [], ref events);
        Assert.Equal(3.2, state.ControlPoints[0].Capping, 10);
    }

    // --- the round-end guards -------------------------------------------------

    /// <summary>The scoreboard locks the point and stops it counting people.</summary>
    /// <remarks>
    /// <c>if (arenaRoundEnd || global.mapchanging) locked = true</c> and
    /// <c>if(not arenaRoundEnd)</c> around the presence scan — the same two
    /// things the victory screen does.
    /// </remarks>
    [Fact]
    public void TheScoreboardLocksThePoint()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundEnd;
        state.ArenaEndCount = 100;
        state.ControlPoints[0].Locked = false;
        state.ControlPoints[0].Capping = 50;

        var zone = world.CaptureZones[0];
        state.Characters[1].X = (zone.Left + zone.Right) / 2.0;
        state.Characters[1].Y = (zone.Top + zone.Bottom) / 2.0;

        Tick(world, ref state);

        Assert.True(state.ControlPoints[0].Locked);
        Assert.Equal(0, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// The round proper always begins with a full minute before the point
    /// opens, whatever the setup period left on the counter.
    /// </summary>
    /// <remarks>
    /// <c>doEventArenaStartRound</c> writes <c>cpUnlock = 1800</c> again, which
    /// in the ordinary flow is redundant — the counter is frozen through setup,
    /// so it is already there. A mutation run showed nothing else notices, so
    /// the invariant is asserted directly: it is what guarantees the minute is
    /// a minute of the <em>round</em> however the setup period was entered.
    /// </remarks>
    [Fact]
    public void TheRoundProperAlwaysStartsWithAFullUnlockCounter()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        Tick(world, ref state);
        Assert.Equal(ArenaState.RoundSetup, state.ArenaState);

        state.ControlPointUnlock = 7;

        Tick(world, ref state, Arena.RoundSetupTicks);

        Assert.Equal(ArenaState.RoundProper, state.ArenaState);
        Assert.Equal(Arena.UnlockTicks, state.ControlPointUnlock);
    }

    /// <summary>Everybody has full ammo during the scoreboard.</summary>
    /// <remarks>
    /// <c>Begin Step.xml:404</c>'s <c>(arenaRoundEnd or global.mapchanging)</c>,
    /// the arena half of a refill the port already had.
    /// </remarks>
    [Fact]
    public void TheScoreboardRefillsEverybodysMagazine()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.ArenaState = ArenaState.RoundProper;
        state.Weapons[1].AmmoCount = 1;

        Tick(world, ref state);
        Assert.Equal(1, state.Weapons[1].AmmoCount);

        state.ArenaState = ArenaState.RoundEnd;
        state.ArenaEndCount = 100;
        Tick(world, ref state);

        Assert.Equal(WeaponStats.For(PlayerClass.Soldier).MaxAmmo, state.Weapons[1].AmmoCount);
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// A live Arena server frames its five extra <c>FULL_UPDATE</c> fields, the
    /// shared scalars and the point.
    /// </summary>
    /// <remarks>
    /// ⚠️ Arena is the only mode whose HUD block differs between tiers: the
    /// round scores, the state, the round winner and the scoreboard countdown
    /// ride on a <c>FULL_UPDATE</c> only. A reader that took them on a
    /// <c>CAPS_UPDATE</c> too would be six bytes out.
    /// </remarks>
    [Fact]
    public void ALiveArenaServerFramesItsHudBlock()
    {
        var level = Level();

        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("watcher");

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(level));
        var buffer = new ReceiveBuffer();

        harness.Tick();
        client.Poll();
        buffer.Append(client.Take(client.Received.Count));

        var messages = new List<ServerMessage>();
        while (stream.TryRead(buffer, out var message))
            messages.Add(message);

        var full = messages.Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        // [redWins, blueWins, state, winners, endCount, timeLimitMins, timer,
        //  cpUnlock, floor(roundStart/2)]
        Assert.Equal(9, full.HudScalars.Length);
        Assert.Equal(0, full.HudScalars[0]);
        Assert.Equal(0, full.HudScalars[1]);
        Assert.Equal((long)ArenaState.Waiting, full.HudScalars[2]);
        Assert.Equal((long)Team.Spectator, full.HudScalars[3]);
        Assert.Equal(0, full.HudScalars[4]);
        Assert.Equal(15, full.HudScalars[5]);
        // Near a full minute rather than exactly one: the counter runs from
        // the server's first tick and joining costs a handful.
        Assert.InRange(full.HudScalars[7], Arena.UnlockTicks - 30, Arena.UnlockTicks);

        Assert.Single(full.ControlPoints);
    }

    /// <summary>
    /// <c>ARENA_ENDROUND</c> is not a bodyless message, and the framer knows it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It was framed as bodyless until phase 6's arena work</b>, which
    /// would have lost the stream the first time a round ended. It carries the
    /// winner, both MVP counts and both round scores, then five bytes per MVP —
    /// so the counts have to be read to know how long the message is.
    /// <para>
    /// It survived because no recording has ever been made on an arena map. The
    /// same reason the sentry <c>INPUTSTATE</c> bug survived six phases; see
    /// docs/PROTOCOL.md.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(1, 0)]
    public void ArenaEndRoundIsFramedByItsMvpCounts(int redMvps, int blueMvps)
    {
        var writer = new NetWriter();

        writer.WriteMessageType(MessageType.ArenaEndRound);
        writer.WriteUByte((byte)Team.Red);
        writer.WriteUByte((byte)redMvps);
        writer.WriteUByte((byte)blueMvps);
        writer.WriteUByte(2); // redWins
        writer.WriteUByte(1); // blueWins

        for (var i = 0; i < redMvps + blueMvps; i++)
        {
            writer.WriteUByte((byte)i); // player
            writer.WriteUByte(7);       // kills
            writer.WriteUShort(1234);   // healing
            writer.WriteUByte(9);       // points
        }

        // A message the framer must not swallow into the previous one.
        ServerMessages.ArenaStartRound(writer);

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(Level()));

        var buffer = new ReceiveBuffer();
        buffer.Append(writer.WrittenSpan);

        Assert.True(stream.TryRead(buffer, out var end));
        Assert.Equal(MessageType.ArenaEndRound, end.Type);
        Assert.Equal(5 + (5 * (redMvps + blueMvps)), end.Payload.Length);

        // The proof that the length was right: the next message still frames.
        Assert.True(stream.TryRead(buffer, out var next));
        Assert.Equal(MessageType.ArenaStartRound, next.Type);
    }
}
