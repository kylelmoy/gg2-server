using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The round: the clock, who wins, and the ten seconds afterwards.
/// </summary>
/// <remarks>
/// <c>Objects/Overlays/Gamemodes/CTFHUD.events/{Create, Step}.xml</c> and
/// <c>Scripts/GameServer/GameServerBeginStep.gml:102-179</c>.
/// <para>
/// <b>No fixture can reach any of this.</b> A recording is thirty to eighty
/// seconds of a fifteen-minute round with a cap limit of five, so no checked-in
/// trace contains a <c>MAP_END</c>, a <c>CHANGE_MAP</c> or a single tick of
/// <c>global.mapchanging</c> — <c>gg2-parity census</c> confirms it. These and
/// <c>MapRotationTests</c> are the whole gate.
/// </para>
/// </remarks>
public sealed class RoundTests
{
    private const string Map = "ctf_truefort";

    private const double OpenX = 2600;

    private const double OpenY = 708;

    private static LevelMap Level() => LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

    private static CollisionWorld World() => new(Level());

    /// <summary>A CTF world with both briefcases home and a live round.</summary>
    private static WorldState Started(int timeLimitMinutes = 15, int capLimit = 5)
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        state.GameMode = GameMode.CaptureTheFlag;
        state.CapLimit = capLimit;
        state.TimeLimitMinutes = timeLimitMinutes;
        state.RoundTimer = Rounds.TimerFor(timeLimitMinutes);

        return state;
    }

    private static void PutScout(ref WorldState state, int slot, Team team, double x = OpenX, double y = OpenY)
    {
        var character = CharacterState.Fresh(PlayerClass.Scout, team);
        character.X = x;
        character.Y = y;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, x, y);
        state.Alive[slot] = true;
        state.Players[slot] = new PlayerState
        {
            Team = team,
            Class = PlayerClass.Scout,
            RespawnTimer = Timers.Inactive,
        };
        state.HasPlayer[slot] = true;
    }

    private static TickEvents Tick(CollisionWorld world, ref WorldState state, SpawnPoints? points = null)
    {
        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events, points);
        return events;
    }

    // --- the clock ------------------------------------------------------------

    /// <summary>Fifteen minutes is 27,000 ticks, and the clock runs down.</summary>
    /// <remarks>
    /// <c>timeLimit = global.timeLimitMins*30*60</c>. The client reconstructs its
    /// own copy of this from the <c>timeLimitMins</c> byte and then runs it down
    /// itself between updates, so a server whose timer never moved — which this
    /// port was until the round landed — shows a frozen clock that jumps
    /// backwards on every update.
    /// </remarks>
    [Fact]
    public void TheClockCountsDownFromTheTimeLimit()
    {
        var world = World();
        var state = Started();

        Assert.Equal(27000, state.RoundTimer);

        Tick(world, ref state);
        Assert.Equal(26999, state.RoundTimer);

        Tick(world, ref state);
        Assert.Equal(26998, state.RoundTimer);
    }

    /// <summary>The clock stops at zero rather than going negative.</summary>
    /// <remarks>
    /// <c>if (timer > 0) timer -= 1</c>. It matters because the end condition is
    /// <c>timer &lt;= 0</c>, which has to stay true once reached — and because
    /// the field is a <c>uint</c> on the wire.
    /// </remarks>
    [Fact]
    public void TheClockStopsAtZero()
    {
        var world = World();
        var state = Started(timeLimitMinutes: 1);
        state.RoundTimer = 2;

        Tick(world, ref state);
        Tick(world, ref state);
        Tick(world, ref state);

        Assert.Equal(0, state.RoundTimer);
    }

    // --- who wins -------------------------------------------------------------

    /// <summary>Reaching the cap limit ends the round for the leader.</summary>
    [Theory]
    [InlineData(5, 2, Team.Red)]
    [InlineData(1, 5, Team.Blue)]
    public void ReachingTheCapLimitEndsTheRound(int redCaps, int blueCaps, Team winner)
    {
        var world = World();
        var state = Started(capLimit: 5);
        state.RedCaps = redCaps;
        state.BlueCaps = blueCaps;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(winner, state.Winner);
    }

    /// <summary>Below the cap limit, with time left, nothing happens.</summary>
    [Fact]
    public void BelowTheCapLimitTheRoundStaysOpen()
    {
        var world = World();
        var state = Started(capLimit: 5);
        state.RedCaps = 4;

        Tick(world, ref state);

        Assert.False(state.RoundDecided);
        Assert.False(state.Overtime);
    }

    /// <summary>The clock running out ends it for whoever is ahead.</summary>
    [Fact]
    public void TheClockRunningOutEndsTheRoundForTheLeader()
    {
        var world = World();
        var state = Started(timeLimitMinutes: 1);
        state.RoundTimer = 1;
        state.BlueCaps = 1;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Blue, state.Winner);
    }

    /// <summary>
    /// A tie with both briefcases home is a draw, and the reference spells that
    /// <c>TEAM_SPECTATOR</c>.
    /// </summary>
    [Fact]
    public void ATieWithBothFlagsHomeIsADraw()
    {
        var world = World();
        var state = Started(timeLimitMinutes: 1);
        state.RoundTimer = 1;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Spectator, state.Winner);
        Assert.False(state.Overtime);
    }

    /// <summary>
    /// A tie with a briefcase off its base is overtime, not a draw.
    /// </summary>
    /// <remarks>
    /// <b>The "is capping" tests are not about a player.</b>
    /// <c>redIsCapping</c> is true when <em>blue's</em> briefcase is not sitting
    /// on its own base — carried by anybody, or merely lying somewhere else. So
    /// a flag dropped in a corridor at 0:00 holds a drawn match open, which is
    /// the whole point of the rule.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ATieWithAFlagAwayFromBaseIsOvertime(bool moveRed, bool carried)
    {
        var world = World();
        var state = Started(timeLimitMinutes: 1);
        state.RoundTimer = 1;

        ref var flag = ref Intel.Of(ref state, moveRed ? Team.Red : Team.Blue);

        if (carried)
            flag.OnField = false;
        else
            flag.X += 1;

        Tick(world, ref state);

        Assert.False(state.RoundDecided);
        Assert.True(state.Overtime);
    }

    /// <summary>
    /// Overtime ends the moment the flag is home again, on the same rules.
    /// </summary>
    /// <remarks>
    /// The clock is already at zero and stays there, so the decision is retried
    /// every tick until one of the three branches takes.
    /// </remarks>
    [Fact]
    public void OvertimeEndsWhenTheFlagComesHome()
    {
        var world = World();
        var state = Started(timeLimitMinutes: 1);
        state.RoundTimer = 1;
        state.RedIntel.OnField = false;

        Tick(world, ref state);
        Assert.True(state.Overtime);
        Assert.False(state.RoundDecided);

        var events = default(TickEvents);
        Intel.Return(ref state, Team.Red, ref events);

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Spectator, state.Winner);
    }

    /// <summary>
    /// A zeroed limit means "no limit", which is what a replay and a bot rollout
    /// get.
    /// </summary>
    /// <remarks>
    /// The same escape <see cref="WorldState.RespawnTime"/> uses, and for the
    /// same reason: <c>game_init.gml</c> floors both at 1, so 0 is unreachable
    /// in the reference. Without it a default <see cref="WorldState"/> would
    /// have a clock reading 0:00 and would end the round on its first tick —
    /// which is every unit test in this repo and every fork of a bot rollout.
    /// </remarks>
    [Fact]
    public void AZeroedLimitMeansNoLimit()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());
        state.RedCaps = 99;

        for (var i = 0; i < 10; i++)
            Tick(world, ref state);

        Assert.False(state.RoundDecided);
        Assert.False(state.Overtime);
        Assert.Equal(0, state.RoundTimer);
    }

    /// <summary>
    /// Only CTF and Invasion run these rules; the other six have their own HUD.
    /// </summary>
    [Theory]
    [InlineData(GameMode.CaptureTheFlag, true, 26999)]
    [InlineData(GameMode.Invasion, true, 26999)]
    [InlineData(GameMode.KingOfTheHill, false, 27000)]

    // Arena and Team Deathmatch each run a clock of their own — Arena's map
    // timer, which counts down in every state, and TDM's round clock — so their
    // round timers move even though a cap limit means nothing to either. That is
    // what separates "does not use these rules" from "does nothing".
    [InlineData(GameMode.Arena, false, 26999)]
    [InlineData(GameMode.TeamDeathmatch, false, 26999)]
    public void OnlyTheIntelModesUseTheseRules(GameMode mode, bool decides, int timer)
    {
        var world = World();
        var state = Started(capLimit: 1);
        state.GameMode = mode;
        state.RedCaps = 1;

        Tick(world, ref state);

        Assert.Equal(decides, state.RoundDecided);
        Assert.Equal(timer, state.RoundTimer);
    }

    // --- what mapchanging does ------------------------------------------------

    /// <summary>
    /// Every team gate opens to a character while the win is on screen, and so
    /// does an intel gate — but an intel gate stays shut to a <em>bullet</em>.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the reference's and it is easy to miss:
    /// <c>charSetSolids.gml:4</c> wraps both its gate blocks in
    /// <c>if(!global.mapchanging)</c>, and <c>gunSetSolids.gml:5</c> wraps only
    /// its <c>TeamGate</c> block — <c>with(IntelGate) solid = true</c> sits
    /// outside the guard. Built by hand because no built-in map places an intel
    /// gate.
    /// </remarks>
    [Fact]
    public void MapChangingOpensTheGatesExceptForBulletsAtAnIntelGate()
    {
        var team = SolidRect.FromEntity(100, 100, 1, 1, 6, 60, SolidKind.TeamGate, Team.Red);
        var intel = SolidRect.FromEntity(100, 100, 1, 1, 6, 60, SolidKind.IntelGate, Team.Red);

        var enemy = new CollisionFilter { Team = Team.Blue, CarryingIntel = true };
        var bullet = CollisionFilter.ForBullet();

        Assert.True(team.IsSolidTo(enemy));
        Assert.True(intel.IsSolidTo(enemy));
        Assert.True(team.IsSolidTo(bullet));
        Assert.True(intel.IsSolidTo(bullet));

        var enemyChanging = enemy with { MapChanging = true };
        var bulletChanging = CollisionFilter.ForBullet(mapChanging: true);

        Assert.False(team.IsSolidTo(enemyChanging));
        Assert.False(intel.IsSolidTo(enemyChanging));
        Assert.False(team.IsSolidTo(bulletChanging));
        Assert.True(intel.IsSolidTo(bulletChanging));
    }

    /// <summary>
    /// A wall is still a wall: only the doors open.
    /// </summary>
    [Theory]
    [InlineData(SolidKind.Obstacle)]
    [InlineData(SolidKind.PlayerWall)]
    public void MapChangingDoesNotOpenTheWorld(SolidKind kind)
    {
        var wall = SolidRect.FromEntity(100, 100, 1, 1, 6, 60, kind);
        var character = new CollisionFilter { Team = Team.Red, CarryingIntel = false, MapChanging = true };

        Assert.True(wall.IsSolidTo(character));
    }

    /// <summary>The flag is dropped when the round ends, and cannot be taken back.</summary>
    /// <remarks>
    /// <c>Begin Step.xml:366</c>'s <c>(ubered or global.mapchanging)</c> and the
    /// <c>!global.mapchanging</c> on the two grab events. Together they mean the
    /// victory screen cannot also be a capture.
    /// </remarks>
    [Fact]
    public void MapChangingDropsTheFlagAndRefusesAGrab()
    {
        var world = World();
        var state = Started();

        PutScout(ref state, 1, Team.Red);
        state.Characters[1].CarryingIntel = true;
        state.Characters[1].IntelRecharge = 300;
        state.BlueIntel.OnField = false;

        state.MapChanging = true;

        var events = Tick(world, ref state);

        Assert.True(events.DroppedIntel[1]);
        Assert.False(state.Characters[1].CarryingIntel);
        Assert.True(state.BlueIntel.OnField);

        // And it stays on the ground: the flag is at the scout's feet with an
        // alarm well past 1, and the grab is refused outright.
        state.Characters[1].CanGrabIntel = true;
        Assert.False(Tick(world, ref state).GrabbedIntel[1]);
        Assert.True(state.BlueIntel.OnField);
    }

    /// <summary>Everybody has full ammo while the win is on screen.</summary>
    /// <remarks>
    /// <c>Begin Step.xml:404</c>, the same body as the uber's a few lines above
    /// and a separate block. The port refills for the losing team too, because
    /// <c>player.humiliated</c> is set by a drawing object nothing here reads.
    /// </remarks>
    [Fact]
    public void MapChangingRefillsEverybodysMagazine()
    {
        var world = World();
        var state = Started();

        PutScout(ref state, 1, Team.Red);
        state.Weapons[1].AmmoCount = 1;
        state.Weapons[1].ReloadTimer = 20;

        Tick(world, ref state);
        Assert.Equal(1, state.Weapons[1].AmmoCount);

        state.MapChanging = true;
        Tick(world, ref state);

        Assert.Equal(WeaponStats.For(PlayerClass.Scout).MaxAmmo, state.Weapons[1].AmmoCount);
        Assert.Equal(WeaponState.Inactive, state.Weapons[1].ReloadTimer);
    }

    /// <summary>
    /// Nobody respawns while the map is changing — and the alarm is spent
    /// anyway.
    /// </summary>
    /// <remarks>
    /// <c>PlayerSpawn.gml:2</c> puts the guard <em>inside</em> the script, so
    /// the alarm still fires and still resets itself; the player simply does not
    /// appear. That is what makes the map change's blanket
    /// <c>alarm[5] = 1</c> safe to run for everybody at once.
    /// </remarks>
    [Fact]
    public void MapChangingSpendsTheRespawnAlarmWithoutSpawning()
    {
        var world = World();
        var points = new SpawnPoints(Level());
        var state = Started();

        state.Players[1] = new PlayerState
        {
            Team = Team.Red,
            Class = PlayerClass.Scout,
            RespawnTimer = 1,
        };
        state.HasPlayer[1] = true;
        state.MapChanging = true;

        var events = Tick(world, ref state, points);

        Assert.False(events.Spawned[1]);
        Assert.False(state.Alive[1]);
        Assert.Equal(Timers.JustFired, state.Players[1].RespawnTimer);
    }

    // --- the reset ------------------------------------------------------------

    /// <summary>
    /// A new map keeps the players and nothing else, and arms everybody to spawn
    /// in the same tick.
    /// </summary>
    [Fact]
    public void ResetKeepsThePlayersAndClearsEverythingElse()
    {
        var state = Started();

        PutScout(ref state, 1, Team.Red);
        PutScout(ref state, 2, Team.Blue);
        state.Characters[1].CarryingIntel = true;
        state.BlueIntel.OnField = false;
        state.RedCaps = 4;
        state.HasSentry[2] = true;
        state.Sentries[2] = SentryState.Build(Team.Blue, 100, 100, 1);
        state.MapChanging = true;
        state.RoundDecided = true;
        state.Overtime = true;

        Rounds.ResetForNewMap(ref state, firstArea: true);

        Assert.False(state.Alive[1]);
        Assert.False(state.Alive[2]);
        Assert.False(state.HasSentry[2]);
        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(0, state.RedCaps);
        Assert.False(state.MapChanging);
        Assert.False(state.RoundDecided);
        Assert.False(state.Overtime);

        // Kept, and both re-armed for this same tick's alarm stage.
        Assert.True(state.HasPlayer[1]);
        Assert.True(state.HasPlayer[2]);
        Assert.Equal(1, state.Players[1].RespawnTimer);
        Assert.Equal(1, state.Players[2].RespawnTimer);
        Assert.Equal(Team.Spectator, state.Players[1].Team);
        Assert.Equal(Team.Spectator, state.Players[2].Team);
    }

    /// <summary>
    /// A later area of a multi-area map keeps everybody's team.
    /// </summary>
    /// <remarks>
    /// <c>if(global.currentMapArea == 1)</c> guards the stats wipe and the team
    /// reset in <c>GameServerBeginStep.gml:137</c>, so an Invasion map pushing
    /// on to its second area does not send both teams back to the team-select
    /// screen. The respawn arming is outside that guard and always runs.
    /// </remarks>
    [Fact]
    public void ALaterAreaKeepsEverybodysTeam()
    {
        var state = Started();
        PutScout(ref state, 1, Team.Blue);

        Rounds.ResetForNewMap(ref state, firstArea: false);

        Assert.Equal(Team.Blue, state.Players[1].Team);
        Assert.Equal(1, state.Players[1].RespawnTimer);
        Assert.False(state.Alive[1]);
    }
}
