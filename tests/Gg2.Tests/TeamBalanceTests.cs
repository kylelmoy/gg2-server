using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Autobalance: the two-stage move that evens out lopsided teams.
/// </summary>
/// <remarks>
/// <c>Scripts/Team Balance/ServerBalanceTeams.gml</c>,
/// <c>Scripts/Maps/Arena/serverArenaRestart.gml</c> and
/// <c>Scripts/Classlimits/checkClasslimits.gml</c>.
/// <para>
/// <b>No fixture reaches any of it.</b> Autobalance needs one team two players
/// larger than the other, and every recording in the project is a balanced bot
/// roster — which is also why the <c>BALANCE</c> framing bug this work found
/// could survive.
/// </para>
/// </remarks>
public sealed class TeamBalanceTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static (WorldState State, CollisionWorld World) Started(bool autobalance = true)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 5;
        state.RespawnTime = 5 * 30;
        state.Autobalance = autobalance;

        // Not optional: a zeroed buffer means every class is full and every
        // team change goes through checkClasslimits' random pick.
        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Intel.Place(ref state, level);
        Rounds.Start(ref state);

        return (state, world);
    }

    /// <summary>A player, alive or not, with a given score.</summary>
    private static void Put(ref WorldState state, int slot, Team team, double points = 0,
        bool alive = false, PlayerClass playerClass = PlayerClass.Soldier)
    {
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;

        Scoring.Award(ref state, slot, StatIndex.Points, points);

        if (!alive)
            return;

        var character = CharacterState.Fresh(playerClass, team);
        character.X = 2000;
        character.Y = 700;
        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, character.X, character.Y);
        state.Alive[slot] = true;
    }

    /// <summary>Runs the fuse forward far enough for stage 2 to be ready.</summary>
    private static void BurnFuse(ref WorldState state)
    {
        state.BalanceCounter = state.RespawnTime + TeamBalance.SwitchDelay;
    }

    // --- the threshold --------------------------------------------------------

    /// <summary>
    /// Two apart, not one — an odd roster is never "unbalanced".
    /// </summary>
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, false)]
    [InlineData(3, 1, true)]
    [InlineData(1, 3, true)]
    [InlineData(4, 4, false)]
    public void OnlyATwoPlayerGapCountsAsUnbalanced(int red, int blue, bool noticed)
    {
        var (state, _) = Started();
        var slot = 1;

        for (var i = 0; i < red; i++)
            Put(ref state, slot++, Team.Red);

        for (var i = 0; i < blue; i++)
            Put(ref state, slot++, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);

        Assert.Equal(noticed, events.BalanceNoticed);
        Assert.Equal(noticed, state.BalanceNoticeGiven);
    }

    /// <summary>Spectators are on neither team and do not tip the balance.</summary>
    [Fact]
    public void SpectatorsCountForNeitherTeam()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);
        Put(ref state, 3, Team.Spectator);
        Put(ref state, 4, Team.Spectator);
        Put(ref state, 5, Team.Spectator);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);

        Assert.False(events.BalanceNoticed);
    }

    /// <summary>Turning autobalance off turns the whole thing off.</summary>
    [Fact]
    public void AutobalanceOffDoesNothing()
    {
        var (state, _) = Started(autobalance: false);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Red);
        Put(ref state, 3, Team.Red);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);

        Assert.False(events.BalanceNoticed);
    }

    /// <summary>
    /// Arena is excluded and balances between rounds instead; a decided round
    /// is excluded too, so nobody is moved during the victory screen.
    /// </summary>
    [Theory]
    [InlineData(GameMode.Arena, false, false)]
    [InlineData(GameMode.CaptureTheFlag, true, false)]
    [InlineData(GameMode.CaptureTheFlag, false, true)]
    public void ArenaAndADecidedRoundAreExcluded(GameMode mode, bool decided, bool noticed)
    {
        var (state, _) = Started();
        state.GameMode = mode;
        state.RoundDecided = decided;

        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Red);
        Put(ref state, 3, Team.Red);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);

        Assert.Equal(noticed, events.BalanceNoticed);
    }

    // --- the two stages -------------------------------------------------------

    /// <summary>
    /// The first call only <em>warns</em>. Nobody moves until the fuse has
    /// burnt for the respawn time plus two seconds.
    /// </summary>
    [Fact]
    public void TheFirstCallOnlyGivesNotice()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Red);
        Put(ref state, 3, Team.Red);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);

        Assert.True(events.BalanceNoticed);
        Assert.Equal(0, state.BalanceCounter);

        // Nobody moved.
        for (var slot = 1; slot <= 3; slot++)
            Assert.Equal(Team.Red, state.Players[slot].Team);
    }

    /// <summary>A second call before the fuse is out does nothing either.</summary>
    [Fact]
    public void ASecondCallTooSoonDoesNothing()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Red);
        Put(ref state, 3, Team.Red);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);

        state.BalanceCounter = state.RespawnTime + TeamBalance.SwitchDelay - 1;

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.False(events.BalanceNoticed);
        Assert.Equal(Team.Red, state.Players[1].Team);
    }

    /// <summary>
    /// The fuse burns only while a notice is outstanding — a balanced server
    /// keeps its counter at zero.
    /// </summary>
    [Fact]
    public void TheFuseBurnsOnlyWhileANoticeIsOutstanding()
    {
        var (state, world) = Started();

        MatchSimulation.Tick(world, ref state, []);
        Assert.Equal(0, state.BalanceCounter);

        state.BalanceNoticeGiven = true;
        MatchSimulation.Tick(world, ref state, []);

        Assert.Equal(1, state.BalanceCounter);
    }

    /// <summary>
    /// Teams that even out on their own cancel the outstanding notice — the
    /// counter and the flag both reset.
    /// </summary>
    [Fact]
    public void TeamsEveningOutCancelTheNotice()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Red);
        Put(ref state, 3, Team.Red);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);
        Assert.True(state.BalanceNoticeGiven);

        BurnFuse(ref state);

        // Somebody joins blue of their own accord.
        Put(ref state, 5, Team.Blue);

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.False(state.BalanceNoticeGiven);
        Assert.Equal(0, state.BalanceCounter);
        Assert.Equal(Team.Red, state.Players[1].Team);
    }

    // --- who moves ------------------------------------------------------------

    /// <summary>
    /// The lowest-scoring dead player on the bigger team goes across, and their
    /// respawn is re-armed.
    /// </summary>
    [Fact]
    public void TheLowestScoringDeadPlayerGoesAcross()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red, points: 9);
        Put(ref state, 2, Team.Red, points: 1);
        Put(ref state, 3, Team.Red, points: 5);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);
        BurnFuse(ref state);

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.True(events.Balanced[2]);
        Assert.Equal(Team.Blue, state.Players[2].Team);

        // The respawn is armed only for a player who had a character:
        // `alarm[5]` is set *inside* `if(balanceplayer.object != -1)`. A player
        // already waiting to respawn keeps the timer their death started.
        Assert.Equal(Timers.Inactive, state.Players[2].RespawnTimer);

        // And the fuse is reset, so the next gap starts from a fresh notice.
        Assert.False(state.BalanceNoticeGiven);
        Assert.Equal(0, state.BalanceCounter);
    }

    /// <summary>
    /// The move goes both ways: a bigger <em>blue</em> team sends somebody to
    /// red.
    /// </summary>
    /// <remarks>
    /// The control for the flip itself. Every other case here is red-to-blue,
    /// which a `team = TEAM_BLUE` that never reads the old value would satisfy.
    /// </remarks>
    [Fact]
    public void ABiggerBlueTeamSendsSomebodyToRed()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Blue, points: 9);
        Put(ref state, 2, Team.Blue, points: 1);
        Put(ref state, 3, Team.Blue, points: 5);
        Put(ref state, 4, Team.Red);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);
        BurnFuse(ref state);

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.True(events.Balanced[2]);
        Assert.Equal(Team.Red, state.Players[2].Team);
    }

    /// <summary>
    /// <b>Only dead players are eligible.</b> A living team with nobody to
    /// spare waits — and the fuse is deliberately <em>not</em> reset, so the very
    /// next call retries.
    /// </summary>
    [Fact]
    public void ALivingTeamIsLeftAloneAndRetriedImmediately()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red, points: 9, alive: true);
        Put(ref state, 2, Team.Red, points: 1, alive: true);
        Put(ref state, 3, Team.Red, points: 5, alive: true);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);
        BurnFuse(ref state);
        var fuse = state.BalanceCounter;

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.Equal(Team.Red, state.Players[2].Team);
        Assert.True(state.BalanceNoticeGiven);
        Assert.Equal(fuse, state.BalanceCounter);

        // One of them dies, and the next call takes them.
        state.Clear(2);

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.Equal(Team.Blue, state.Players[2].Team);
    }

    /// <summary>
    /// A character on no hit points counts as dead for this — which is how
    /// the balance can move the player who has just been killed.
    /// </summary>
    [Fact]
    public void ACharacterOnNoHitPointsCountsAsDead()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red, points: 9, alive: true);
        Put(ref state, 2, Team.Red, points: 1, alive: true);
        Put(ref state, 3, Team.Red, points: 5, alive: true);
        Put(ref state, 4, Team.Blue);

        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);
        BurnFuse(ref state);

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.Equal(Team.Blue, state.Players[2].Team);
    }

    /// <summary>
    /// A living character is destroyed by the move — and it is <b>not</b> a
    /// death: nothing is credited and no <c>PLAYER_DEATH</c> is produced.
    /// </summary>
    [Fact]
    public void TheMoveDestroysTheCharacterWithoutKillingIt()
    {
        var (state, _) = Started();

        // Alive but on no hit points, so they are eligible and still have a body.
        Put(ref state, 1, Team.Red, points: 1, alive: true);
        state.Characters[1].Hp = 0;
        Put(ref state, 2, Team.Red, points: 5);
        Put(ref state, 3, Team.Red, points: 9);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.Check(ref state, ref events);
        BurnFuse(ref state);

        events = default;
        TeamBalance.Check(ref state, ref events);

        Assert.True(events.Balanced[1]);
        Assert.False(state.Alive[1]);
        Assert.False(events.Died[1]);
        Assert.Equal(0, Scoring.Get(state, 1, StatIndex.Deaths));

        // This one *did* have a character, so the respawn is armed — the arm
        // the previous test proves is skipped without one.
        Assert.Equal(state.RespawnTime, state.Players[1].RespawnTimer);
    }

    /// <summary>
    /// The whole path through a real tick: a death calls the balance, exactly as
    /// <c>Character.events/End Step.xml:29-31</c> does.
    /// </summary>
    /// <remarks>
    /// And the player who just died is the one taken, because
    /// <c>doEventPlayerDeath</c> destroyed their character first.
    /// </remarks>
    [Fact]
    public void ADeathIsWhatDrivesTheBalance()
    {
        var (state, world) = Started();
        Put(ref state, 1, Team.Red, points: 9);
        Put(ref state, 2, Team.Red, points: 5);
        Put(ref state, 3, Team.Red, points: 0, alive: true);
        Put(ref state, 4, Team.Blue);

        var events = default(TickEvents);

        // First death: the notice.
        state.Characters[3].Hp = 0;
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.True(events.BalanceNoticed);
        Assert.Equal(Team.Red, state.Players[3].Team);

        // Burn the fuse with ticks, not by hand.
        for (var i = 0; i < state.RespawnTime + TeamBalance.SwitchDelay; i++)
            MatchSimulation.Tick(world, ref state, []);

        // Somebody has to die again for the switch to be reached at all.
        state.Players[3].RespawnTimer = Timers.Inactive;
        Put(ref state, 5, Team.Red, points: 20, alive: true);
        state.Characters[5].Hp = 0;

        events = default;
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.True(events.Balanced[3], "the lowest-scoring dead red was not moved");
        Assert.Equal(Team.Blue, state.Players[3].Team);
    }

    // --- Arena ----------------------------------------------------------------

    /// <summary>
    /// Arena's balance <b>loops</b> until the gap is under two, takes living
    /// players, and does not arm a respawn.
    /// </summary>
    [Fact]
    public void ArenaMovesAsManyPlayersAsItTakes()
    {
        var (state, _) = Started();
        state.GameMode = GameMode.Arena;

        // Five red to one blue: two have to move for the gap to reach one.
        for (var slot = 1; slot <= 5; slot++)
            Put(ref state, slot, Team.Red, points: slot, alive: true);

        Put(ref state, 6, Team.Blue, points: 100);

        var events = default(TickEvents);
        TeamBalance.ArenaRestart(ref state, ref events);

        var (red, blue) = TeamBalance.TeamSizes(state);
        Assert.Equal(3, red);
        Assert.Equal(3, blue);

        // The two lowest-scoring reds, and no respawn armed.
        Assert.True(events.Balanced[1]);
        Assert.True(events.Balanced[2]);
        Assert.Equal(Timers.Inactive, state.Players[1].RespawnTimer);
        Assert.Equal(Timers.Inactive, state.Players[2].RespawnTimer);
    }

    /// <summary>
    /// And an Arena <em>restart</em> is what runs it — driven through
    /// <c>Arena.Step</c>, so that the balance being wired into the transition at
    /// all is part of what this gates.
    /// </summary>
    /// <remarks>
    /// It runs <b>before</b> the round's own state is touched and before
    /// anybody is respawned, which is where <c>serverArenaRestart</c> has it —
    /// above its <c>sendEventArenaRestart</c>.
    /// </remarks>
    [Fact]
    public void AnArenaRestartIsWhatDrivesArenaBalance()
    {
        const string ArenaMap = "arena_lumberyard";
        var level = LevelMap.FromPng(ArenaMap, ReferenceTree.ReadMap(ArenaMap)).ForArea(1);
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 5;
        state.Autobalance = true;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        for (var slot = 1; slot <= 4; slot++)
            Put(ref state, slot, Team.Red, points: slot);

        Put(ref state, 5, Team.Blue, points: 100);

        // One tick of scoreboard left, so this tick restarts the round.
        state.ArenaState = ArenaState.RoundEnd;
        state.ArenaEndCount = 1;

        var events = default(TickEvents);
        Arena.Step(ref state, null, ref events);

        Assert.True(events.ArenaRestarted, "the round never restarted");
        Assert.Equal((3, 2), TeamBalance.TeamSizes(state));
        Assert.True(events.Balanced[1], "the lowest-scoring red was not moved");
    }

    /// <summary>
    /// A balanced Arena roster is left entirely alone — and so is a one-player
    /// gap.
    /// </summary>
    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 2)]
    public void ArenaLeavesABalancedRosterAlone(int red, int blue)
    {
        var (state, _) = Started();
        state.GameMode = GameMode.Arena;

        var slot = 1;
        for (var i = 0; i < red; i++)
            Put(ref state, slot++, Team.Red);

        for (var i = 0; i < blue; i++)
            Put(ref state, slot++, Team.Blue);

        var events = default(TickEvents);
        TeamBalance.ArenaRestart(ref state, ref events);

        Assert.Equal((red, blue), TeamBalance.TeamSizes(state));
    }

    // --- class limits ---------------------------------------------------------

    /// <summary>With no limit set, the class you asked for is the class you get.</summary>
    [Fact]
    public void NoLimitMeansTheDesiredClass()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red, playerClass: PlayerClass.Medic);
        Put(ref state, 2, Team.Blue, playerClass: PlayerClass.Medic);

        Assert.Equal(
            PlayerClass.Medic,
            Classlimits.Pick(ref state, 1, Team.Blue, PlayerClass.Medic));
    }

    /// <summary>
    /// A full class is refused and something else is picked at random from the
    /// classes that are not.
    /// </summary>
    [Fact]
    public void AFullClassIsSwappedForAFreeOne()
    {
        var (state, _) = Started();

        // One medic per team allowed; blue already has one.
        state.ClassLimits[(int)PlayerClass.Medic] = 1;

        Put(ref state, 1, Team.Red, playerClass: PlayerClass.Medic);
        Put(ref state, 2, Team.Blue, playerClass: PlayerClass.Medic);

        var got = Classlimits.Pick(ref state, 1, Team.Blue, PlayerClass.Medic);

        Assert.NotEqual(PlayerClass.Medic, got);
    }

    /// <summary>
    /// The player asking is <b>not counted against their own limit</b>, so a
    /// team change that keeps the class is never refused by the player's own
    /// presence.
    /// </summary>
    [Fact]
    public void ThePlayerAskingDoesNotCountAgainstTheirOwnLimit()
    {
        var (state, _) = Started();
        state.ClassLimits[(int)PlayerClass.Medic] = 1;

        Put(ref state, 1, Team.Red, playerClass: PlayerClass.Medic);

        // Staying a medic on the team they are already on: the only medic there
        // is themselves, and they do not count.
        Assert.Equal(
            PlayerClass.Medic,
            Classlimits.Pick(ref state, 1, Team.Red, PlayerClass.Medic));
    }

    /// <summary>
    /// <b>Quote is excluded from the random pick and then offered as a last
    /// resort</b> — the loop runs to 9, and the line under it hands out the
    /// tenth class anyway.
    /// </summary>
    [Fact]
    public void QuoteIsTheLastResortAndNeverTheRandomChoice()
    {
        var (state, _) = Started();

        // Every class full for blue except Quote, twice over so nobody has room.
        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 1;

        var slot = 1;
        Put(ref state, slot++, Team.Red, playerClass: PlayerClass.Scout);

        foreach (var full in new[]
                 {
                     PlayerClass.Scout, PlayerClass.Pyro, PlayerClass.Soldier,
                     PlayerClass.Heavy, PlayerClass.Demoman, PlayerClass.Medic,
                     PlayerClass.Engineer, PlayerClass.Spy, PlayerClass.Sniper,
                 })
        {
            Put(ref state, slot++, Team.Blue, playerClass: full);
        }

        Assert.Equal(
            PlayerClass.Quote,
            Classlimits.Pick(ref state, 1, Team.Blue, PlayerClass.Scout));
    }

    /// <summary>
    /// Quote is <b>never</b> the random choice, even when it has room and
    /// is the only alternative besides one other free class.
    /// </summary>
    /// <remarks>
    /// The loop is <c>for (class=0; class&lt;9; ...)</c>, so the tenth class is
    /// outside it. Repeated because the pick is a draw: with Quote in the list
    /// it would come out about half the time, so fifty draws that all avoid it
    /// is not luck.
    /// </remarks>
    [Fact]
    public void QuoteIsNeverTheRandomChoiceEvenWhenItHasRoom()
    {
        var (state, _) = Started();

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 1;

        var slot = 1;
        Put(ref state, slot++, Team.Red, playerClass: PlayerClass.Scout);

        // Blue is full except for the sniper and Quote.
        foreach (var full in new[]
                 {
                     PlayerClass.Scout, PlayerClass.Pyro, PlayerClass.Soldier,
                     PlayerClass.Heavy, PlayerClass.Demoman, PlayerClass.Medic,
                     PlayerClass.Engineer, PlayerClass.Spy,
                 })
        {
            Put(ref state, slot++, Team.Blue, playerClass: full);
        }

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(
                PlayerClass.Sniper,
                Classlimits.Pick(ref state, 1, Team.Blue, PlayerClass.Scout));
        }
    }

    /// <summary>
    /// And when even Quote is full it hands back what was asked for, over the
    /// limit. A full server stops enforcing rather than refusing.
    /// </summary>
    [Fact]
    public void AWhollyFullTeamGetsTheClassItAskedForAnyway()
    {
        var (state, _) = Started();

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 1;

        var slot = 1;
        Put(ref state, slot++, Team.Red, playerClass: PlayerClass.Scout);

        foreach (var full in new[]
                 {
                     PlayerClass.Scout, PlayerClass.Pyro, PlayerClass.Soldier,
                     PlayerClass.Heavy, PlayerClass.Demoman, PlayerClass.Medic,
                     PlayerClass.Engineer, PlayerClass.Spy, PlayerClass.Sniper,
                     PlayerClass.Quote,
                 })
        {
            Put(ref state, slot++, Team.Blue, playerClass: full);
        }

        Assert.Equal(
            PlayerClass.Scout,
            Classlimits.Pick(ref state, 1, Team.Blue, PlayerClass.Scout));
    }

    /// <summary>
    /// The random pick is a draw from the <b>shared</b> stream, which every
    /// client re-derives weapon spreads from. Which class comes out is free; the
    /// draw is not.
    /// </summary>
    [Fact]
    public void TheRandomPickAdvancesTheSharedStream()
    {
        var (state, _) = Started();
        state.ClassLimits[(int)PlayerClass.Medic] = 1;

        Put(ref state, 1, Team.Red, playerClass: PlayerClass.Medic);
        Put(ref state, 2, Team.Blue, playerClass: PlayerClass.Medic);

        var before = state.Random;
        Classlimits.Pick(ref state, 1, Team.Blue, PlayerClass.Medic);

        Assert.NotEqual(before, state.Random);
    }

    /// <summary>
    /// And a class that fits draws <b>nothing</b> — the early return is above
    /// the list.
    /// </summary>
    [Fact]
    public void AClassThatFitsDrawsNothing()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red, playerClass: PlayerClass.Medic);

        var before = state.Random;
        Classlimits.Pick(ref state, 1, Team.Blue, PlayerClass.Medic);

        Assert.Equal(before, state.Random);
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// <b><c>BALANCE</c> is variable-length</b>, and this port framed it as a
    /// fixed two body bytes until phase 6's team balance.
    /// </summary>
    /// <remarks>
    /// The notice is one body byte and stops; a switch is two. A reader that
    /// always took two was one byte ahead from the first notice onwards and
    /// would have lost the stream. The following message is read back to prove
    /// each length was right — the fourth message in this project with a
    /// content-dependent length, after the sentry <c>INPUTSTATE</c> block,
    /// <c>ARENA_ENDROUND</c> and the generator HUD block.
    /// </remarks>
    [Fact]
    public void TheNoticeAndTheSwitchAreDifferentLengths()
    {
        var writer = new NetWriter();

        ServerMessages.BalanceNotice(writer);
        ServerMessages.BalanceSwitch(writer, 3, PlayerClass.Pyro);
        ServerMessages.UnlockControlPoints(writer);

        var stream = new ServerStream();
        var buffer = new ReceiveBuffer();
        buffer.Append(writer.WrittenSpan.ToArray());

        Assert.True(stream.TryRead(buffer, out var notice));
        Assert.Equal(MessageType.Balance, notice.Type);
        Assert.Single(notice.Payload);
        Assert.Equal(ServerStream.BalanceNotice, notice.PlayerId!.Value);

        Assert.True(stream.TryRead(buffer, out var moved));
        Assert.Equal(MessageType.Balance, moved.Type);
        Assert.Equal(2, moved.Payload.Length);
        Assert.Equal((byte)3, moved.PlayerId);
        Assert.Equal((byte)PlayerClass.Pyro, moved.Payload[1]);

        // And the stream is still in step.
        Assert.True(stream.TryRead(buffer, out var unlock));
        Assert.Equal(MessageType.UnlockCp, unlock.Type);
    }

    /// <summary>
    /// A live server broadcasts the notice when a departure leaves the teams
    /// lopsided.
    /// </summary>
    /// <remarks>
    /// <b>It has to be a departure and not a team change.</b>
    /// <c>ResolveRequestedTeam</c> already refuses a request for the bigger
    /// team, so a roster cannot be talked into being lopsided — which is
    /// exactly why autobalance exists for the case where somebody leaves.
    /// </remarks>
    [Fact]
    public void ALiveServerNoticesADepartureThatUnbalancesTheTeams()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.Autobalance = true;
        });

        var watcher = harness.JoinClient("watcher");
        var red = harness.JoinClient("red");
        var blueA = harness.JoinClient("blueA");
        var blueB = harness.JoinClient("blueB");

        // Alternating, because a request for the bigger team is refused.
        watcher.SendChangeTeam(Team.Red);
        harness.Tick(2);
        blueA.SendChangeTeam(Team.Blue);
        harness.Tick(2);
        red.SendChangeTeam(Team.Red);
        harness.Tick(2);
        blueB.SendChangeTeam(Team.Blue);
        harness.Tick(2);

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(Level()));
        var buffer = new ReceiveBuffer();
        buffer.Append(watcher.Take(watcher.Received.Count));

        while (stream.TryRead(buffer, out _))
        {
            // Drain the join burst so the reader is in step before the leave.
        }

        // Both blues go, leaving two red against nobody.
        blueA.Dispose();
        blueB.Dispose();
        harness.TickUntil(() => harness.Server.PlayerCount <= 3, "both blues to leave");

        buffer.Append(watcher.Take(watcher.Received.Count));

        var sawNotice = false;
        while (stream.TryRead(buffer, out var message))
        {
            if (message.Type == MessageType.Balance && message.Payload.Length == 1)
                sawNotice = true;
        }

        Assert.True(sawNotice, "no BALANCE notice after the teams came apart");
    }
}
