using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Dual King of the Hill: two hills, and each team's clock runs on the other
/// team's.
/// </summary>
/// <remarks>
/// <c>Objects/Overlays/Gamemodes/DKothHUD.events/{Create, Step}.xml</c> and the
/// <c>mode == 4</c> branch of <c>ControlPoint.events/Step.xml:12-19</c>.
/// <para>
/// ⚠️ <b>Reading it as "KotH with two points" gets every condition backwards.</b>
/// <c>redTimer</c> counts down while <b>blue's</b> hill is red's: you win by
/// holding the enemy's hill, and your own is only ever something to lose.
/// </para>
/// <para>
/// <b>No fixture reaches it</b>, like every other mode — every recording in the
/// project is <c>ctf_truefort</c>.
/// </para>
/// </remarks>
public sealed class DualKothTests
{
    /// <summary>Two hills, two capture zones — one each.</summary>
    private const string Map = "dkoth_sixties";

    /// <summary>Index of <c>KothRedControlPoint</c>, the reference's <c>cp = 1</c>.</summary>
    private const int RedHill = 0;

    /// <summary>Index of <c>KothBlueControlPoint</c>, <c>cp = 2</c>.</summary>
    private const int BlueHill = 1;

    private static LevelMap Level() => LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

    private static (WorldState State, CollisionWorld World) Started(bool unlocked = true)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;

        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        if (unlocked)
        {
            state.ControlPointUnlock = 0;

            for (var i = 0; i < state.ControlPointCount; i++)
                state.ControlPoints[i].Locked = false;
        }

        return (state, world);
    }

    /// <summary>Puts a character in the middle of the zone belonging to a hill.</summary>
    private static void PutOnHill(ref WorldState state, CollisionWorld world, int slot, Team team, int hill)
    {
        var zone = world.CaptureZones.First(z => z.Point == hill);

        var character = CharacterState.Fresh(PlayerClass.Soldier, team);
        character.X = (zone.Left + zone.Right) / 2.0;
        character.Y = (zone.Top + zone.Bottom) / 2.0;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Soldier, character.X, character.Y);
        state.Alive[slot] = true;
        state.Players[slot] = new PlayerState
        {
            Team = team,
            Class = PlayerClass.Soldier,
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

    // --- starting -------------------------------------------------------------

    /// <summary>
    /// Both hills start owned by their own team, which no other mode does.
    /// </summary>
    /// <remarks>
    /// <c>DKothHUD.events/Create.xml</c>, not <c>User Event 0</c>. Without it
    /// both hills read neutral and the cross-lock — which asks who owns the
    /// <em>other</em> one — never engages.
    /// </remarks>
    [Fact]
    public void BothHillsStartOwnedByTheirOwnTeam()
    {
        var (state, _) = Started();

        Assert.Equal(2, state.ControlPointCount);

        Assert.True(state.ControlPoints[RedHill].Owned);
        Assert.Equal(Team.Red, state.ControlPoints[RedHill].Team);

        Assert.True(state.ControlPoints[BlueHill].Owned);
        Assert.Equal(Team.Blue, state.ControlPoints[BlueHill].Team);
    }

    /// <summary>Red's hill comes first, which is the order the wire wants.</summary>
    /// <remarks>
    /// <c>DKothHUD.events/User Event 12.xml</c> writes
    /// <c>with KothRedControlPoint</c> then <c>with KothBlueControlPoint</c>,
    /// and the blocks carry no index — so getting the order wrong swaps two
    /// hills on every client with no error anywhere.
    /// </remarks>
    [Fact]
    public void RedsHillIsSerialisedFirst()
    {
        var level = Level();
        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);

        ControlPoints.Place(ref state, level, out _);
        Rounds.Start(ref state);

        Assert.Equal((sbyte)Team.Red, ControlPoints.ToBlock(state.ControlPoints[0]).Team);
        Assert.Equal((sbyte)Team.Blue, ControlPoints.ToBlock(state.ControlPoints[1]).Team);
    }

    // --- the cross-lock -------------------------------------------------------

    /// <summary>
    /// Taking the enemy's hill locks your own, so only one is ever in play.
    /// </summary>
    /// <remarks>
    /// <c>if (cp == 1) { if (global.cp[2].team == TEAM_RED) locked = true }</c>
    /// and its mirror. Whoever is ahead cannot be counter-attacked at home.
    /// </remarks>
    [Fact]
    public void HoldingTheEnemyHillLocksYourOwn()
    {
        var (state, world) = Started();

        Assert.False(state.ControlPoints[RedHill].Locked);
        Assert.False(state.ControlPoints[BlueHill].Locked);

        // Red takes blue's hill.
        state.ControlPoints[BlueHill].Team = Team.Red;
        Tick(world, ref state);

        Assert.True(state.ControlPoints[RedHill].Locked);
        Assert.False(state.ControlPoints[BlueHill].Locked);

        // And it comes back the moment blue takes it back.
        state.ControlPoints[BlueHill].Team = Team.Blue;
        Tick(world, ref state);

        Assert.False(state.ControlPoints[RedHill].Locked);
    }

    /// <summary>The mirror: blue holding red's hill locks blue's.</summary>
    [Fact]
    public void TheCrossLockWorksBothWays()
    {
        var (state, world) = Started();

        state.ControlPoints[RedHill].Team = Team.Blue;
        Tick(world, ref state);

        Assert.True(state.ControlPoints[BlueHill].Locked);
        Assert.False(state.ControlPoints[RedHill].Locked);
    }

    /// <summary>
    /// A locked hill cannot be captured, so a team that is ahead is safe at home.
    /// </summary>
    [Fact]
    public void ALockedHillCannotBeTaken()
    {
        var (state, world) = Started();

        state.ControlPoints[BlueHill].Team = Team.Red;
        PutOnHill(ref state, world, 1, Team.Blue, RedHill);

        Tick(world, ref state, 100);

        Assert.True(state.ControlPoints[RedHill].Locked);
        Assert.Equal(0, state.ControlPoints[RedHill].Capping);
        Assert.Equal(Team.Red, state.ControlPoints[RedHill].Team);
    }

    /// <summary>Both hills are locked for the whole pregame.</summary>
    [Fact]
    public void BothHillsAreLockedDuringThePregame()
    {
        var (state, world) = Started(unlocked: false);

        Tick(world, ref state, 100);

        Assert.True(state.ControlPoints[RedHill].Locked);
        Assert.True(state.ControlPoints[BlueHill].Locked);
    }

    /// <summary>
    /// A hill unlocked by anything during the pregame is locked again on the
    /// next tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The line this pins is belt-and-braces on a server and load-bearing
    /// on a client.</b> <c>ControlPoint.events/Step.xml</c> re-asserts
    /// <c>locked = true</c> from <c>DKothHUD.cpUnlock &gt; 0</c> every tick, and
    /// nothing in a headless server ever clears a hill's lock early — the points
    /// are created locked and only the <c>cpUnlock == 0</c> branch opens them.
    /// A client is different: <c>doEventUnlockCP</c> arrives from the network
    /// and is <c>with ControlPoint locked = 0</c> with no timing check at all, so
    /// an early or duplicated <c>UNLOCKCP</c> would open the hills and this is
    /// what shuts them again.
    /// <para>
    /// Removing the line leaves every other test in this file green — a mutation
    /// run found that — so the invariant is asserted directly rather than
    /// through a behaviour that happens to depend on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePregameRelocksAHillThatSomethingElseOpened()
    {
        var (state, world) = Started(unlocked: false);

        state.ControlPoints[RedHill].Locked = false;
        state.ControlPoints[BlueHill].Locked = false;

        Tick(world, ref state);

        Assert.True(state.ControlPoints[RedHill].Locked);
        Assert.True(state.ControlPoints[BlueHill].Locked);
    }

    /// <summary>
    /// The hills unlock themselves, and DKotH therefore never sends
    /// <c>UNLOCKCP</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The one wire difference between the two hill modes, and it falls
    /// out of instance order.</b> A <c>ControlPoint</c> is a map instance and a
    /// HUD is created by <c>basicRoomSetup</c> afterwards, so the points' Steps
    /// run first: on the tick <c>cpUnlock</c> reaches zero they clear their own
    /// lock from the <c>mode == 4</c> branch, and the HUD's
    /// <c>if (both locked)</c> is already false when it runs. KotH's hill has no
    /// such branch — its <c>mode == 3</c> is excluded — so KotH does send the
    /// message. Clients reach the same state either way, from their own copy of
    /// the same line.
    /// </remarks>
    [Fact]
    public void TheHillsUnlockThemselvesWithoutAMessage()
    {
        var (state, world) = Started(unlocked: false);

        Tick(world, ref state, Rounds.KothUnlockTicks);

        Assert.True(state.ControlPoints[RedHill].Locked);
        Assert.Equal(0, state.ControlPointUnlock);

        Tick(world, ref state);

        Assert.False(state.ControlPoints[RedHill].Locked);
        Assert.False(state.ControlPoints[BlueHill].Locked);
        Assert.False(state.UnlockedControlPointsThisTick);
    }

    /// <summary>The five-second scoreboard update still goes out.</summary>
    /// <remarks>
    /// It is what keeps the clients' own <c>cpUnlock</c> in step, which in this
    /// mode is the only thing that unlocks their hills.
    /// </remarks>
    [Fact]
    public void ThePregameStillAsksForAScoreboardUpdate()
    {
        var (state, world) = Started(unlocked: false);

        Tick(world, ref state, Rounds.KothUnlockTicks - 150);
        Assert.False(state.ScoreboardSyncRequested);

        Tick(world, ref state);
        Assert.True(state.ScoreboardSyncRequested);
    }

    // --- the clocks -----------------------------------------------------------

    /// <summary>
    /// Your clock runs on the <em>enemy's</em> hill, not your own.
    /// </summary>
    [Fact]
    public void YourClockRunsOnTheEnemysHill()
    {
        var (state, world) = Started();

        // Nobody has taken anything: neither clock moves.
        Tick(world, ref state, 50);
        Assert.Equal(Rounds.KothHoldTicks, state.RedTimer);
        Assert.Equal(Rounds.KothHoldTicks, state.BlueTimer);

        // Red takes blue's hill: red's clock runs.
        state.ControlPoints[BlueHill].Team = Team.Red;
        Tick(world, ref state, 50);

        Assert.Equal(Rounds.KothHoldTicks - 50, state.RedTimer);
        Assert.Equal(Rounds.KothHoldTicks, state.BlueTimer);
    }

    /// <summary>Holding your own hill does nothing for your clock.</summary>
    [Fact]
    public void HoldingYourOwnHillDoesNothing()
    {
        var (state, world) = Started();

        // The starting position is exactly this, and it is a standoff.
        Tick(world, ref state, 100);

        Assert.Equal(Rounds.KothHoldTicks, state.RedTimer);
        Assert.Equal(Rounds.KothHoldTicks, state.BlueTimer);
    }

    /// <summary>Neither clock runs while both hills are locked.</summary>
    [Fact]
    public void NoClockRunsWhileBothHillsAreLocked()
    {
        var (state, world) = Started(unlocked: false);
        state.ControlPoints[BlueHill].Team = Team.Red;

        Tick(world, ref state, 100);

        Assert.Equal(Rounds.KothHoldTicks, state.RedTimer);
    }

    // --- winning --------------------------------------------------------------

    /// <summary>
    /// Running your clock out while holding the enemy hill wins the round.
    /// </summary>
    [Fact]
    public void RunningYourClockOutOnTheEnemyHillWins()
    {
        var (state, world) = Started();

        state.ControlPoints[BlueHill].Team = Team.Red;
        state.RedTimer = 2;

        Tick(world, ref state);
        Assert.False(state.RoundDecided);

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>
    /// A defender standing on their own hill at 0:00 blocks the win.
    /// </summary>
    /// <remarks>
    /// <c>blueTimer == 0 ... and KothBlueControlPoint.bluePresence == 0</c> —
    /// and the meter is frozen rather than reverting, because the hill is
    /// contested. The same endgame KotH has, one hill over.
    /// </remarks>
    [Fact]
    public void ADefenderOnTheirOwnHillBlocksTheWin()
    {
        var (state, world) = Started();

        state.ControlPoints[BlueHill].Team = Team.Red;
        state.RedTimer = 1;

        PutOnHill(ref state, world, 1, Team.Red, BlueHill);
        PutOnHill(ref state, world, 2, Team.Blue, BlueHill);

        Tick(world, ref state, 30);

        Assert.Equal(0, state.RedTimer);
        Assert.Equal(0, state.ControlPoints[BlueHill].Capping);
        Assert.False(state.RoundDecided);

        state.Alive[2] = false;
        Tick(world, ref state, 2);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    // --- the idle timer -------------------------------------------------------

    /// <summary>
    /// A standoff runs the idle timer out, and a close one is a draw.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A margin, not a tie.</b> Within thirty seconds of each other
    /// (<c>abs(redTimer - blueTimer) &lt; 30*30</c>) the round is drawn; outside
    /// that <c>if (redTimer &lt; blueTimer) global.winners = TEAM_RED</c> — the
    /// <em>lower</em> clock wins, because a clock only runs down while its team
    /// is holding the enemy hill. So the team that has <b>spent more</b> of its
    /// three minutes is the one ahead, which is the opposite of how a countdown
    /// usually reads.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, Team.Spectator)]
    [InlineData(0, 899, Team.Spectator)]
    [InlineData(0, 900, Team.Blue)]
    [InlineData(900, 0, Team.Red)]
    [InlineData(1800, 300, Team.Red)]
    public void AStandoffIsDecidedByTheMargin(int redSpent, int blueSpent, Team winner)
    {
        var (state, world) = Started();

        state.RedTimer = Rounds.KothHoldTicks - redSpent;
        state.BlueTimer = Rounds.KothHoldTicks - blueSpent;
        state.RoundTimer = 2;

        Tick(world, ref state);
        Assert.False(state.RoundDecided);

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(winner, state.Winner);
    }

    /// <summary>
    /// The idle timer resets to full whenever either hill is locked.
    /// </summary>
    /// <remarks>
    /// Which includes the whole pregame — so it only starts counting once both
    /// hills are in play and both are home. Without the reset an idle server
    /// would draw its first round thirty seconds early.
    /// </remarks>
    [Fact]
    public void ALockedHillResetsTheIdleTimer()
    {
        var (state, world) = Started();
        state.RoundTimer = 60;

        Tick(world, ref state, 10);
        Assert.Equal(50, state.RoundTimer);

        // Red takes blue's hill, which locks red's — and the idle timer goes
        // back to the full time limit.
        state.ControlPoints[BlueHill].Team = Team.Red;
        Tick(world, ref state);

        Assert.True(state.ControlPoints[RedHill].Locked);
        Assert.Equal(Rounds.TimerFor(15), state.RoundTimer);
    }

    /// <summary>Somebody standing on either hill stops the idle timer.</summary>
    /// <remarks>
    /// ⚠️ The reference's comments call these clauses "blu uncontested" and
    /// "red uncontested", but each checks the presence of the hill's <em>own</em>
    /// team. Together with the two <c>capping == 0</c> clauses — which an enemy
    /// would break — it means nobody at all is on either hill, which is what an
    /// idle timer wants. The comments are the misleading part.
    /// </remarks>
    [Theory]
    [InlineData(Team.Red, RedHill)]
    [InlineData(Team.Blue, BlueHill)]
    public void SomebodyOnEitherHillStopsTheIdleTimer(Team team, int hill)
    {
        var (state, world) = Started();
        state.RoundTimer = 100;

        PutOnHill(ref state, world, 1, team, hill);

        Tick(world, ref state, 30);

        Assert.Equal(100, state.RoundTimer);
        Assert.False(state.RoundDecided);
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// A live DKotH server frames both hills and the three scalars.
    /// </summary>
    /// <remarks>
    /// The layout check: the block is <c>cpUnlock</c>, <c>redTimer</c>,
    /// <c>blueTimer</c> and then <b>two</b> point blocks with no count. One
    /// block too few or too many desynchronises the stream rather than showing a
    /// wrong number, which is why this decodes through the real
    /// <see cref="ServerStream"/>.
    /// </remarks>
    [Fact]
    public void ALiveDkothServerFramesBothHills()
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

        Assert.Equal(3, full.HudScalars.Length);
        Assert.Equal(Rounds.KothHoldTicks, full.HudScalars[1]);
        Assert.Equal(Rounds.KothHoldTicks, full.HudScalars[2]);

        Assert.Equal(2, full.ControlPoints.Length);
        Assert.Equal((sbyte)Team.Red, full.ControlPoints[0].Team);
        Assert.Equal((sbyte)Team.Blue, full.ControlPoints[1].Team);
    }
}
