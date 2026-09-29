using System.Linq;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The capture point, and King of the Hill on top of it.
/// </summary>
/// <remarks>
/// <c>ControlPoint.events/{User Event 0, Step, User Event 12}.xml</c>,
/// <c>CaptureZone.events/Alarm 0.xml</c>, <c>Scripts/Maps/pointCapture.gml</c>
/// and <c>KothHUD.events/{Create, Step}.xml</c>.
/// <para>
/// <b>No fixture reaches any of it.</b> Every checked-in recording is
/// <c>ctf_truefort</c> — there has never been a KotH, CP or Arena trace in this
/// project — so this file and the reference are the whole gate, the same
/// position <c>RoundTests</c> is in.
/// </para>
/// </remarks>
public sealed class ControlPointTests
{
    /// <summary>One hill, one capture zone.</summary>
    private const string KothMap = "koth_valley";

    private static LevelMap Level(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    /// <summary>
    /// A KotH world with the hill placed and the pregame lock already off.
    /// </summary>
    private static (WorldState State, CollisionWorld World) Started(
        string map = KothMap,
        bool unlocked = true)
    {
        var level = Level(map);
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;

        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);

        // The same call GameServer.OpenMap makes, so a test world and a live one
        // start from one place.
        Rounds.Start(ref state);

        if (unlocked)
        {
            state.ControlPointUnlock = 0;

            for (var i = 0; i < state.ControlPointCount; i++)
                state.ControlPoints[i].Locked = false;
        }

        return (state, world);
    }

    /// <summary>Puts a character in the middle of the first capture zone.</summary>
    private static void PutOnPoint(
        ref WorldState state,
        CollisionWorld world,
        int slot,
        Team team,
        PlayerClass playerClass = PlayerClass.Soldier)
    {
        var zone = world.CaptureZones[0];

        var character = CharacterState.Fresh(playerClass, team);
        character.X = (zone.Left + zone.Right) / 2.0;
        character.Y = (zone.Top + zone.Bottom) / 2.0;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, character.X, character.Y);
        state.Alive[slot] = true;
        state.Players[slot] = new PlayerState
        {
            Team = team,
            Class = playerClass,
            RespawnTimer = Timers.Inactive,
        };
        state.HasPlayer[slot] = true;
    }

    private static void Tick(CollisionWorld world, ref WorldState state, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var events = default(TickEvents);
            MatchSimulation.Tick(world, ref state, [], ref events);
        }
    }

    // --- placing --------------------------------------------------------------

    /// <summary>
    /// A KotH map has one hill and its capture zones point at it.
    /// </summary>
    [Fact]
    public void AKothMapHasOneHill()
    {
        var (state, world) = Started();

        Assert.Equal(1, state.ControlPointCount);
        Assert.NotEmpty(world.CaptureZones);
        Assert.All(world.CaptureZones, z => Assert.Equal(0, z.Point));

        // User Event 0's own initial state, and the trap it exists to avoid:
        // nobody owns it, nobody is capturing, and neither of those is "red".
        Assert.False(state.ControlPoints[0].Owned);
        Assert.False(state.ControlPoints[0].BeingCaptured);
        Assert.Equal(0, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// A map with several points keeps them in numbered order, and each zone
    /// belongs to the point it is nearest.
    /// </summary>
    /// <remarks>
    /// <c>ControlPointHUD</c> walks <c>global.cp[1..n]</c> by number, so the
    /// serialisation order is <c>controlPoint1</c> first however the map file
    /// lists them; <c>CaptureZone.events/Alarm 0.xml</c> is
    /// <c>instance_nearest</c>. <c>cp_egypt</c> has five points and five zones,
    /// which is the only shipped map where a wrong pairing would be invisible in
    /// a count.
    /// </remarks>
    [Fact]
    public void EveryZoneBelongsToTheNearestPoint()
    {
        var level = Level("cp_egypt");
        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);

        ControlPoints.Place(ref state, level, out var zones);

        Assert.Equal(5, state.ControlPointCount);
        Assert.Equal(5, zones.Length);

        // Five zones and five points, and no two zones on the same point: on
        // this map each point has exactly one.
        Assert.Equal(5, zones.Select(z => z.Point).Distinct().Count());
    }

    /// <summary>A CTF map has no control points at all, and nothing throws.</summary>
    [Fact]
    public void ACtfMapHasNoControlPoints()
    {
        var level = Level("ctf_truefort");
        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);

        ControlPoints.Place(ref state, level, out var zones);

        Assert.Equal(0, state.ControlPointCount);
        Assert.Empty(zones);
    }

    // --- standing on it -------------------------------------------------------

    /// <summary>
    /// A character inside a capture zone is on its point, and one outside is
    /// not.
    /// </summary>
    /// <remarks>
    /// <c>collision_point(x, y, CaptureZone, 0, 0)</c> — the character is a
    /// <em>point</em>, not a box, so its origin has to be inside the zone.
    /// Standing with your feet over the edge does not count.
    /// </remarks>
    [Fact]
    public void OnlyACharacterWhoseOriginIsInsideTheZoneIsOnThePoint()
    {
        var (state, world) = Started();
        var zone = world.CaptureZones[0];

        PutOnPoint(ref state, world, 1, Team.Red);
        Tick(world, ref state);
        Assert.Equal(0, state.Characters[1].CappingPoint);

        // One pixel past the right edge. The character's box still overlaps the
        // zone by six pixels; its origin does not.
        state.Characters[1].X = zone.Right + 1;
        Tick(world, ref state);
        Assert.Equal(CharacterState.Nobody, state.Characters[1].CappingPoint);
    }

    /// <summary>A cloaked spy, or one still fading in, is not on the point.</summary>
    /// <remarks>
    /// <c>zone >= 0 and !cloak and cloakAlpha == 1</c>. The second clause is the
    /// one that is easy to miss: the seconds after decloaking do not count
    /// either, so a spy cannot flicker in and start capturing at once.
    /// </remarks>
    [Theory]
    [InlineData(true, 1.0, false)]
    [InlineData(false, 0.4, false)]
    [InlineData(false, 1.0, true)]
    public void ACloakedOrFadingSpyIsNotOnThePoint(bool cloaked, double alpha, bool counts)
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red, PlayerClass.Spy);

        state.Characters[1].Cloak = cloaked;
        state.Characters[1].CloakAlpha = alpha;

        Tick(world, ref state);

        Assert.Equal(counts ? 0 : CharacterState.Nobody, state.Characters[1].CappingPoint);
    }

    // --- capturing ------------------------------------------------------------

    /// <summary>
    /// One ordinary player captures at 1 a tick and takes the point at 300.
    /// </summary>
    [Fact]
    public void OneCapperTakesThePointInTenSeconds()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 299);

        Assert.False(state.ControlPoints[0].Owned);
        Assert.Equal(299, state.ControlPoints[0].Capping);
        Assert.True(state.ControlPoints[0].BeingCaptured);
        Assert.Equal(Team.Red, state.ControlPoints[0].CappingTeam);

        Tick(world, ref state);

        Assert.True(state.ControlPoints[0].Owned);
        Assert.Equal(Team.Red, state.ControlPoints[0].Team);
        Assert.Equal(0, state.ControlPoints[0].Capping);
    }

    /// <summary>A scout captures twice as fast as anybody else.</summary>
    /// <remarks>
    /// <c>capStrength = 2</c>, the only class override in the game
    /// (<c>Scout.events/Create.xml:58</c>).
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Scout, 20)]
    [InlineData(PlayerClass.Soldier, 10)]
    [InlineData(PlayerClass.Heavy, 10)]
    public void AScoutCapturesTwiceAsFast(PlayerClass playerClass, double after10Ticks)
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red, playerClass);

        Tick(world, ref state, 10);

        Assert.Equal(after10Ticks, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// Past two cappers the rate is halved, so four players are slower than four
    /// times one.
    /// </summary>
    /// <remarks>
    /// <c>cappers &lt;= 2 ? cappers : cappers/2 + 1</c>. Two soldiers cap at 2 a
    /// tick and four cap at 3, not 4.
    /// </remarks>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2.5)]
    [InlineData(4, 3)]
    public void PastTwoCappersTheRateIsHalved(int players, double perTick)
    {
        var (state, world) = Started();

        for (var i = 0; i < players; i++)
            PutOnPoint(ref state, world, i + 1, Team.Red);

        Tick(world, ref state);

        Assert.Equal(perTick, state.ControlPoints[0].Capping);
    }

    /// <summary>An empty point reverts at 1 a tick, whoever was capping it.</summary>
    [Fact]
    public void AnEmptyPointReverts()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 50);
        Assert.Equal(50, state.ControlPoints[0].Capping);

        state.Alive[1] = false;

        Tick(world, ref state, 20);
        Assert.Equal(30, state.ControlPoints[0].Capping);

        // And it stops at zero, taking cappingTeam with it.
        Tick(world, ref state, 40);
        Assert.Equal(0, state.ControlPoints[0].Capping);
        Assert.False(state.ControlPoints[0].BeingCaptured);
    }

    /// <summary>A contested point does not move in either direction.</summary>
    /// <remarks>
    /// The first branch of the three: with both teams present the reference
    /// plays a sound and does nothing else. Not a slow revert, not a stalemate
    /// timer — frozen.
    /// </remarks>
    [Fact]
    public void AContestedPointIsFrozen()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 40);
        Assert.Equal(40, state.ControlPoints[0].Capping);

        PutOnPoint(ref state, world, 2, Team.Blue);

        Tick(world, ref state, 60);
        Assert.Equal(40, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// Reverting the enemy's progress is faster than capturing, and the two
    /// happen in that order.
    /// </summary>
    /// <remarks>
    /// <c>capping -= (1 + cappers*0.5)</c> against the capture's
    /// <c>+cappers</c>. Two soldiers revert at 2 a tick and then cap at 2 a
    /// tick, which is why taking a half-captured point costs twice as long as
    /// taking a fresh one.
    /// </remarks>
    [Fact]
    public void AnEnemyCaptureMustBeRevertedBeforeCapturing()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 30);
        Assert.Equal(30, state.ControlPoints[0].Capping);
        Assert.Equal(Team.Red, state.ControlPoints[0].CappingTeam);

        // Red leaves, blue arrives: one blue soldier reverts at 1 + 1*0.5.
        state.Alive[1] = false;
        PutOnPoint(ref state, world, 2, Team.Blue);

        Tick(world, ref state, 10);
        Assert.Equal(15, state.ControlPoints[0].Capping);
        Assert.Equal(Team.Red, state.ControlPoints[0].CappingTeam);

        // Ten more ticks wipes the last 15 of red's — and leaves the meter at
        // zero for one tick. `cappingTeam` is still Red here: the
        // `capping <= 0` branch clears `BeingCaptured` and does not touch it,
        // which is why the wire block reads the flag and not the team.
        Tick(world, ref state, 10);
        Assert.Equal(0, state.ControlPoints[0].Capping);
        Assert.False(state.ControlPoints[0].BeingCaptured);

        // Only on the tick after that does blue start its own.
        Tick(world, ref state);
        Assert.True(state.ControlPoints[0].BeingCaptured);
        Assert.Equal(Team.Blue, state.ControlPoints[0].CappingTeam);
        Assert.Equal(1, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// An ubered player cannot capture, but does stop their team's progress
    /// reverting.
    /// </summary>
    /// <remarks>
    /// The presence field is 2 for an ordinary player and 1 for an ubered
    /// one, so "only ubered players are here" is a state of its own. The
    /// reference's comment: "He can't cap, but he will prevent the point from
    /// reverting if his team has partially capped it already."
    /// </remarks>
    [Fact]
    public void AnUberedPlayerHoldsProgressWithoutCapturing()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 40);
        Assert.Equal(40, state.ControlPoints[0].Capping);

        state.Characters[1].Ubered = true;

        Tick(world, ref state, 30);

        Assert.Equal(1, state.ControlPoints[0].RedPresence);
        Assert.Equal(40, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// A lone ubered player of the <em>other</em> team lets the point revert.
    /// </summary>
    [Fact]
    public void AnUberedEnemyDoesNotHoldSomebodyElsesProgress()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 40);

        state.Alive[1] = false;
        PutOnPoint(ref state, world, 2, Team.Blue);
        state.Characters[2].Ubered = true;

        Tick(world, ref state, 10);

        Assert.Equal(30, state.ControlPoints[0].Capping);
    }

    /// <summary>Standing on a point you already own does nothing.</summary>
    [Fact]
    public void StandingOnYourOwnPointDoesNothing()
    {
        var (state, world) = Started();
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;

        PutOnPoint(ref state, world, 1, Team.Red);
        Tick(world, ref state, 50);

        Assert.Equal(0, state.ControlPoints[0].Capping);
        Assert.False(state.ControlPoints[0].BeingCaptured);
    }

    /// <summary>A locked point cannot be captured, and loses the meter.</summary>
    /// <remarks>
    /// <c>capping = 0; exit;</c> — locking a point mid-capture <b>discards</b>
    /// the progress rather than pausing it.
    /// </remarks>
    [Fact]
    public void LockingAPointDiscardsTheMeter()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 100);
        Assert.Equal(100, state.ControlPoints[0].Capping);

        state.ControlPoints[0].Locked = true;
        Tick(world, ref state);

        Assert.Equal(0, state.ControlPoints[0].Capping);
    }

    /// <summary>The victory screen locks every point, whatever the mode.</summary>
    /// <remarks>
    /// <c>if (arenaRoundEnd || global.mapchanging) locked = true;</c> — the
    /// reference's own comment calls it "autolock if the round is over,
    /// regardless of game mode".
    /// </remarks>
    [Fact]
    public void MapChangingLocksEveryPoint()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 50);

        state.MapChanging = true;
        Tick(world, ref state);

        Assert.True(state.ControlPoints[0].Locked);
        Assert.Equal(0, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// A moving capture meter asks for an extra scoreboard update at a tenth, a
    /// quarter and three quarters of the way — and nowhere else.
    /// </summary>
    /// <remarks>
    /// <c>ControlPoint.events/Step.xml:217</c>: 30, 75 and 225 at the standard
    /// <c>capTime</c> of 300, plus one when the point is taken. It is an
    /// <b>equality on <c>floor(capping)</c></b>, not a crossing — a meter moving
    /// two a tick can step straight over an odd threshold and never ask. A lone
    /// soldier moves one a tick and therefore lands on all three.
    /// <para>
    /// Bandwidth rather than correctness, but the extra <c>CAPS_UPDATE</c>s are
    /// bytes on the wire, so a byte diff against the reference would see them.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCaptureMeterAsksForAScoreboardUpdateAtThreeMarks()
    {
        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Red);

        var asked = new List<int>();

        for (var tick = 1; tick <= 300; tick++)
        {
            Tick(world, ref state);

            if (!state.ScoreboardSyncRequested)
                continue;

            asked.Add(tick);

            // The server clears it when it sends the message; do the same, or
            // every later tick reads a stale flag.
            state.ScoreboardSyncRequested = false;
        }

        Assert.Equal([30, 75, 225, 300], asked);
    }

    // --- King of the Hill -----------------------------------------------------

    /// <summary>
    /// The hill is locked for thirty seconds, and unlocking is announced once.
    /// </summary>
    /// <remarks>
    /// <b>901 ticks, not 900.</b> The counter is read and <em>then</em>
    /// decremented, so a <c>cpUnlock</c> of 900 is 900 ticks of counting down
    /// and one more to notice the zero. Every "n ticks later" number in this
    /// file is the reference's arithmetic rather than the obvious one.
    /// </remarks>
    [Fact]
    public void TheHillUnlocksAfterThePregame()
    {
        var (state, world) = Started(unlocked: false);
        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, Rounds.KothUnlockTicks);

        Assert.True(state.ControlPoints[0].Locked);
        Assert.Equal(0, state.ControlPoints[0].Capping);
        Assert.Equal(0, state.ControlPointUnlock);
        Assert.False(state.UnlockedControlPointsThisTick);

        Tick(world, ref state);

        Assert.False(state.ControlPoints[0].Locked);
        Assert.True(state.UnlockedControlPointsThisTick);

        // And the capture starts on the tick after that, not this one — the
        // point's Step runs before the HUD's, so it saw the lock still on.
        Assert.Equal(0, state.ControlPoints[0].Capping);

        Tick(world, ref state);
        Assert.Equal(1, state.ControlPoints[0].Capping);
    }

    /// <summary>
    /// The five-second warning asks for a scoreboard update.
    /// </summary>
    /// <remarks>
    /// <c>else if (cpUnlock == 150) GameServer.syncTimer = 1;</c>. It is an
    /// <c>else if</c> on the unlock, and both are inside
    /// <c>if (locked and !mapchanging)</c>.
    /// </remarks>
    [Fact]
    public void ThePregameAsksForAScoreboardUpdateFiveSecondsOut()
    {
        var (state, world) = Started(unlocked: false);

        // 750 ticks of counting down leaves it at 150; the 751st reads it.
        Tick(world, ref state, Rounds.KothUnlockTicks - 150);
        Assert.Equal(150, state.ControlPointUnlock);
        Assert.False(state.ScoreboardSyncRequested);

        Tick(world, ref state);
        Assert.True(state.ScoreboardSyncRequested);
    }

    /// <summary>
    /// Holding the hill runs your own countdown down, and the enemy's stands
    /// still.
    /// </summary>
    [Fact]
    public void HoldingTheHillRunsOnlyYourOwnTimerDown()
    {
        var (state, world) = Started();
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Blue;

        Tick(world, ref state, 100);

        Assert.Equal(Rounds.KothHoldTicks - 100, state.BlueTimer);
        Assert.Equal(Rounds.KothHoldTicks, state.RedTimer);
    }

    /// <summary>Nobody's timer runs while the hill is locked.</summary>
    [Fact]
    public void NoTimerRunsDuringThePregame()
    {
        var (state, world) = Started(unlocked: false);
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;

        Tick(world, ref state, 100);

        Assert.Equal(Rounds.KothHoldTicks, state.RedTimer);
    }

    /// <summary>Running your timer out while holding the hill wins the round.</summary>
    [Fact]
    public void RunningTheTimerOutWinsTheRound()
    {
        var (state, world) = Started();
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;
        state.RedTimer = 2;

        Tick(world, ref state);
        Assert.False(state.RoundDecided);

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>
    /// An enemy standing on your hill at 0:00 stops you winning, even though
    /// they are not capturing.
    /// </summary>
    /// <remarks>
    /// <b>The mode's whole endgame.</b> The win needs
    /// <c>capping == 0 and bluePresence == 0</c> as well as the timer and the
    /// ownership — and a contested point has <c>capping == 0</c>, because the
    /// meter is frozen. So the blue player is blocking the win purely by
    /// standing there.
    /// </remarks>
    [Fact]
    public void AnEnemyOnTheHillBlocksTheWin()
    {
        var (state, world) = Started();
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;
        state.RedTimer = 1;

        PutOnPoint(ref state, world, 1, Team.Red);
        PutOnPoint(ref state, world, 2, Team.Blue);

        Tick(world, ref state, 30);

        Assert.Equal(0, state.RedTimer);
        Assert.Equal(0, state.ControlPoints[0].Capping);
        Assert.False(state.RoundDecided);

        // They die, and red wins on the next tick.
        state.Alive[2] = false;
        Tick(world, ref state, 2);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>
    /// An idle round ends as a draw when the host timer runs out.
    /// </summary>
    /// <remarks>
    /// The only thing that stops an empty KotH server sitting on one map for
    /// ever. It needs the hill owned and quiet: a pregame server does not burn
    /// it, and neither does one where somebody is standing on the point.
    /// </remarks>
    [Fact]
    public void AnIdleRoundEndsAsADraw()
    {
        var (state, world) = Started();
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;
        state.RoundTimer = 2;

        Tick(world, ref state);
        Assert.False(state.RoundDecided);

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Spectator, state.Winner);
    }

    /// <summary>Somebody standing on the hill stops the idle timer.</summary>
    [Fact]
    public void SomebodyOnTheHillStopsTheIdleTimer()
    {
        var (state, world) = Started();
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;
        state.RoundTimer = 2;

        PutOnPoint(ref state, world, 1, Team.Red);

        Tick(world, ref state, 30);

        Assert.Equal(2, state.RoundTimer);
        Assert.False(state.RoundDecided);
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// A neutral point writes -1 for both teams, and an owned one writes the
    /// team.
    /// </summary>
    /// <remarks>
    /// <b>A capture does not clear <c>cappingTeam</c>.</b>
    /// <c>pointCapture</c> writes <c>team = cappingTeam; capping = 0</c> and
    /// leaves <c>cappingTeam</c> where it was; only the <em>next</em> tick's
    /// <c>capping &lt;= 0</c> branch clears it. So the tick a point changes
    /// hands puts both on the wire, and that is the reference's.
    /// </remarks>
    [Fact]
    public void APointsWireBlockSaysNeutralWithMinusOne()
    {
        var neutral = ControlPoints.ToBlock(ControlPoints.Neutral);

        Assert.Equal(-1, neutral.Team);
        Assert.Equal(-1, neutral.CappingTeam);
        Assert.Equal(0, neutral.Capping);

        var (state, world) = Started();
        PutOnPoint(ref state, world, 1, Team.Blue);
        Tick(world, ref state, 300);

        var captured = ControlPoints.ToBlock(state.ControlPoints[0]);

        Assert.Equal((sbyte)Team.Blue, captured.Team);
        Assert.Equal((sbyte)Team.Blue, captured.CappingTeam);
        Assert.Equal(0, captured.Capping);
    }

    /// <summary>
    /// A live KotH server frames its HUD block, announces the unlock, and sends
    /// the extra scoreboard update five seconds before it.
    /// </summary>
    /// <remarks>
    /// The end-to-end half, and the only thing that gates the wire layout: the
    /// KotH block is <c>cpUnlock</c>, <c>redTimer</c>, <c>blueTimer</c> and then
    /// one block per point with no count prefix, and a client frames it from its
    /// own copy of the map. A wrong width here does not lose a field — it
    /// desynchronises the stream, which is why the broadcast is decoded with the
    /// real <see cref="ServerStream"/> rather than counted by hand.
    /// <para>
    /// The <c>CAPS_UPDATE</c> that arrives at <c>cpUnlock == 150</c> is what
    /// makes the HUD block readable at all: it and a <c>FULL_UPDATE</c> are the
    /// only tiers that carry one, and a recording holds one full update.
    /// </para>
    /// </remarks>
    [Fact]
    public void AKothServerAnnouncesTheUnlockAndFramesItsHudBlock()
    {
        var level = Level(KothMap);

        using var harness = new ServerHarness(o => o.Map = KothMap);
        var client = harness.JoinClient("watcher");

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(level));
        var buffer = new ReceiveBuffer();

        List<ServerMessage> Drain()
        {
            client.Poll();
            buffer.Append(client.Take(client.Received.Count));

            var messages = new List<ServerMessage>();
            while (stream.TryRead(buffer, out var message))
                messages.Add(message);

            return messages;
        }

        // The join's own FULL_UPDATE: the hill is neutral and the pregame is
        // running. Framing this at all is the layout check.
        harness.Tick();
        var full = Drain().Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        Assert.Equal(3, full.HudScalars.Length);
        Assert.InRange(full.HudScalars[0], Rounds.KothUnlockTicks - 30, Rounds.KothUnlockTicks);
        Assert.Equal(Rounds.KothHoldTicks, full.HudScalars[1]);
        Assert.Equal(Rounds.KothHoldTicks, full.HudScalars[2]);

        var hill = Assert.Single(full.ControlPoints);
        Assert.Equal(-1, hill.Team);
        Assert.Equal(-1, hill.CappingTeam);

        // Run past the unlock, collecting what comes out.
        var unlocks = 0;
        var caps = 0;

        for (var tick = 0; tick < Rounds.KothUnlockTicks + 10; tick++)
        {
            harness.Tick();

            foreach (var message in Drain())
            {
                if (message.Type == MessageType.UnlockCp)
                    unlocks++;
                else if (message.State is { Tier: MessageType.CapsUpdate })
                    caps++;
            }
        }

        // Once, not once per tick — the flag is cleared as it is sent.
        Assert.Equal(1, unlocks);

        // At least the five-second warning. (A cap would ask for another, and
        // nobody is on the hill.)
        Assert.True(caps >= 1, $"expected a CAPS_UPDATE before the unlock, got {caps}");
    }

    /// <summary>The meter is <c>round(capping)</c>, GML's half to even.</summary>
    [Theory]
    [InlineData(2.5, 2)]
    [InlineData(3.5, 4)]
    [InlineData(150.5, 150)]
    public void TheMeterQuantisesWithGmlsOwnRounding(double capping, int expected)
    {
        var point = ControlPoints.Neutral;
        point.Capping = capping;

        Assert.Equal(expected, ControlPoints.ToBlock(point).Capping);
    }
}
