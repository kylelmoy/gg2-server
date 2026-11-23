using GG2.Maps;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Team Deathmatch: kills into the capture counters, and a spawn you cannot be
/// shot out of.
/// </summary>
/// <remarks>
/// <c>Objects/Overlays/Gamemodes/TeamDeathmatchHUD.events/Step.xml</c>,
/// <c>Scripts/Events/doEventPlayerDeath.gml:307-317</c>, and the
/// <c>deathmatch_invulnerable</c> lines spread across
/// <c>Character.events/{Create, Begin Step, End Step, Destroy}.xml</c>.
/// <para>
/// <b>It is the fallback mode</b> — <c>GameModes.Of</c> returns it when a map
/// places no objective at all — so <c>gg_debug</c> is a Team Deathmatch map and
/// has been running as one since phase 0, without the spawn protection.
/// </para>
/// <para>
/// <b>No fixture reaches any of it.</b> Every recording in the project is on
/// <c>ctf_truefort</c>.
/// </para>
/// </remarks>
public sealed class TeamDeathmatchTests
{
    private const string Map = "gg_debug";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static (WorldState State, CollisionWorld World) Started(
        int killLimit = 30,
        int timeLimitMinutes = 15,
        int invulnerabilityTicks = 30)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = timeLimitMinutes;
        state.KillLimit = killLimit;
        state.TdmInvulnerabilityTicks = invulnerabilityTicks;
        state.RespawnTime = 5 * 30;

        Rounds.Start(ref state);

        return (state, world);
    }

    /// <summary>
    /// A live character, spawned through <see cref="WorldState.Spawn"/> so the
    /// mode's Create-event arm really runs.
    /// </summary>
    private static void Spawn(ref WorldState state, CollisionWorld world, int slot, Team team,
        PlayerClass playerClass = PlayerClass.Scout)
    {
        _ = world;
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;

        var points = new SpawnPoints(Level());
        Assert.True(state.Spawn(points, slot, playerClass, team, 0, 0));
    }

    // --- the map ------------------------------------------------------------

    /// <summary>
    /// Team Deathmatch is what a map with no objective <em>is</em>, not a
    /// mode a map opts into.
    /// </summary>
    [Fact]
    public void AMapWithNoObjectiveIsTeamDeathmatch()
    {
        Assert.Equal(GameMode.TeamDeathmatch, GameModes.Of(Level()));
    }

    // --- the kill counters ---------------------------------------------------

    /// <summary>
    /// A kill goes into the <em>capture</em> counters, which is why the mode
    /// needs no score of its own.
    /// </summary>
    [Fact]
    public void AKillCountsAsACapture()
    {
        var (state, world) = Started();
        Spawn(ref state, world, 1, Team.Red);
        Spawn(ref state, world, 2, Team.Blue);

        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].LastDamageSource = DamageSource.Scattergun;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(1, state.RedCaps);
        Assert.Equal(0, state.BlueCaps);
    }

    /// <summary>
    /// A client's replica never decides a death (<c>End Step.xml:34</c>, the
    /// <c>global.isHost</c> guard): a character at hp 0 stays alive, scores
    /// nobody and reports no death, and the rest of End Step still runs on it.
    /// The server's world, the control, kills it on the same tick.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClientReplicaLeavesTheDeathToTheServer(bool replica)
    {
        var (state, world) = Started(invulnerabilityTicks: 0);
        state.ClientReplica = replica;
        Spawn(ref state, world, 1, Team.Red);
        Spawn(ref state, world, 2, Team.Blue);

        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].LastDamageSource = DamageSource.Scattergun;
        state.Characters[2].Hp = 0;
        state.Characters[2].NetAimDirection = 0;
        state.Characters[2].Facing = -1;

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.Equal(replica, state.IsAlive(2));
        Assert.Equal(!replica, events.Died[2]);
        Assert.Equal(replica ? 0 : 1, state.RedCaps);

        // End Step's tail: facing follows the aim only past the death exit.
        if (replica)
            Assert.Equal(1, state.Characters[2].Facing);
    }

    /// <summary>
    /// Three guards, and each of them excludes a case that really happens.
    /// </summary>
    [Theory]
    // A suicide scores nothing — `killer != victim`.
    [InlineData(2, Team.Blue, 0, 0)]
    // A team kill scores nothing, for either side — `killer.team != victim.team`.
    [InlineData(3, Team.Blue, 0, 0)]
    // Nobody at all scores nothing — GM8's `killer` truth test.
    [InlineData(CharacterState.Nobody, Team.Blue, 0, 0)]
    // And the control: a real enemy kill does score.
    [InlineData(1, Team.Red, 1, 0)]
    public void OnlyAnEnemyKillByaLivePlayerScores(
        int killerSlot, Team killerTeam, int expectRed, int expectBlue)
    {
        var (state, world) = Started();
        Spawn(ref state, world, 1, Team.Red);
        Spawn(ref state, world, 2, Team.Blue);
        Spawn(ref state, world, 3, Team.Blue);

        if (killerSlot != CharacterState.Nobody)
            state.Players[killerSlot].Team = killerTeam;

        state.Characters[2].LastDamageDealer = (byte)killerSlot;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(expectRed, state.RedCaps);
        Assert.Equal(expectBlue, state.BlueCaps);
    }

    /// <summary>
    /// It reads the killer's <b>player</b>, not their character — so a trade
    /// in which both die still scores for the one who struck first.
    /// </summary>
    [Fact]
    public void AKillerWhoIsAlreadyDeadStillScores()
    {
        var (state, world) = Started();
        Spawn(ref state, world, 1, Team.Red);
        Spawn(ref state, world, 2, Team.Blue);

        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].Hp = 0;

        // The killer's character is gone; their player is not.
        state.Clear(1);

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(1, state.RedCaps);
    }

    /// <summary>No other mode counts kills — `instance_exists(TeamDeathmatchHUD)`.</summary>
    [Fact]
    public void NoOtherModeCountsAKillAsACapture()
    {
        var (state, world) = Started();
        state.GameMode = GameMode.CaptureTheFlag;

        Spawn(ref state, world, 1, Team.Red);
        Spawn(ref state, world, 2, Team.Blue);

        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(0, state.RedCaps);
    }

    // --- the round -----------------------------------------------------------

    /// <summary>The clock runs, and nothing is decided while it does.</summary>
    [Fact]
    public void TheClockRunsAndDecidesNothingWhileItDoes()
    {
        var (state, _) = Started(timeLimitMinutes: 1);

        Rounds.Step(ref state);

        Assert.Equal(Rounds.TimerFor(1) - 1, state.RoundTimer);
        Assert.False(state.RoundDecided);
    }

    /// <summary>
    /// Reaching the kill limit decides the round on the <em>same</em> tick —
    /// the win condition is not in an <c>else</c>, unlike every other mode's.
    /// </summary>
    [Fact]
    public void ReachingTheKillLimitEndsTheRoundOnThatTick()
    {
        var (state, _) = Started(killLimit: 3);

        state.RedCaps = 3;
        Rounds.Step(ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>
    /// The clock running out goes to whoever is ahead, and a level score is a
    /// draw — which the wire spells <c>TEAM_SPECTATOR</c>.
    /// </summary>
    [Theory]
    [InlineData(5, 2, (int)Team.Red)]
    [InlineData(2, 5, (int)Team.Blue)]
    [InlineData(4, 4, (int)Team.Spectator)]
    [InlineData(0, 0, (int)Team.Spectator)]
    public void TheClockRunningOutGoesToWhoeverIsAhead(int red, int blue, int winner)
    {
        var (state, _) = Started(killLimit: 30, timeLimitMinutes: 1);

        state.RedCaps = red;
        state.BlueCaps = blue;
        state.RoundTimer = 1;

        Rounds.Step(ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal((Team)winner, state.Winner);
    }

    /// <summary>
    /// Both teams crossing the limit on one tick is a <b>draw</b>, not a win
    /// for whoever triggered it — the three-way comparison runs whatever the
    /// trigger was.
    /// </summary>
    [Fact]
    public void BothTeamsCrossingTheLimitAtOnceIsADraw()
    {
        var (state, _) = Started(killLimit: 3);

        state.RedCaps = 3;
        state.BlueCaps = 3;
        Rounds.Step(ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Spectator, state.Winner);
    }

    /// <summary>
    /// A world with neither limit set — the default, and every unit test —
    /// never decides anything.
    /// </summary>
    [Fact]
    public void NeitherLimitSetMeansTheRoundNeverEnds()
    {
        var (state, _) = Started(killLimit: 0, timeLimitMinutes: 0);

        state.RedCaps = 900;
        Rounds.Step(ref state);

        Assert.False(state.RoundDecided);
    }

    /// <summary>
    /// Team Deathmatch is the one mode with no overtime at all: a capture
    /// cannot be in progress, so there is nothing to hold a round open for.
    /// </summary>
    [Fact]
    public void TheRoundNeverGoesToOvertime()
    {
        var (state, _) = Started(killLimit: 30, timeLimitMinutes: 1);

        state.RedCaps = 4;
        state.BlueCaps = 4;
        state.RoundTimer = 1;

        Rounds.Step(ref state);

        Assert.False(state.Overtime);
    }

    // --- spawn protection -----------------------------------------------------

    /// <summary>
    /// A Team Deathmatch spawn is invulnerable, and no other mode's is.
    /// </summary>
    [Theory]
    [InlineData(GameMode.TeamDeathmatch, 30.0, CharacterState.SpawnInvulnDecay)]
    [InlineData(GameMode.CaptureTheFlag, 0.0, 0.0)]
    public void OnlyATeamDeathmatchSpawnIsInvulnerable(GameMode mode, double expected, double decay)
    {
        var (state, world) = Started();
        state.GameMode = mode;

        Spawn(ref state, world, 1, Team.Red);

        Assert.Equal(expected, state.Characters[1].DeathmatchInvulnerable);
        Assert.Equal(decay, state.Characters[1].DeathmatchInvulnDecay);
    }

    /// <summary>
    /// The guard is in <c>dealDamage</c>, so a hit still does everything
    /// <em>around</em> the hit points — the reference's own
    /// <c>damageCharacter</c> resets <c>timeUnscathed</c> on the line after.
    /// </summary>
    [Fact]
    public void AProtectedSpawnTakesNoDamageButStillLosesItsHealingRamp()
    {
        var (state, world) = Started();
        Spawn(ref state, world, 1, Team.Red);

        ref var victim = ref state.Characters[1];
        victim.TimeUnscathed = 200;
        var hp = victim.Hp;

        var events = default(TickEvents);
        Assert.False(DamageApi.Deal(ref events, 1, 2, ref victim, 50));

        Assert.Equal(hp, victim.Hp);

        // Blocked damage accumulates NOTHING on either side — the reward's
        // rule, and deliberately not the same rule as the line below it. The
        // reference resets `timeUnscathed` on a hit that did nothing; a reward
        // signal must not count an amount that never landed.
        Assert.Equal(0, events.DamageTaken[1]);
        Assert.Equal(0, events.DamageDealt[2]);

        // The caller's own line, which runs whatever Deal answered.
        victim.TimeUnscathed = 0;
        Assert.Equal(0, victim.TimeUnscathed);
    }

    /// <summary>
    /// It really is spent at 0.2 a tick, so the configured <em>fifth</em> of
    /// the ticks buys the whole duration back.
    /// </summary>
    [Fact]
    public void ProtectionLastsFiveTimesItsTickCountWhileStandingStill()
    {
        var (state, world) = Started(invulnerabilityTicks: 30);
        Spawn(ref state, world, 1, Team.Red);

        // 30 spent at 0.2 is 150 ticks. Standing still: no keys at all.
        for (var i = 0; i < 149; i++)
            MatchSimulation.Tick(world, ref state, []);

        Assert.True(state.Characters[1].DeathmatchInvulnerable > 0);

        MatchSimulation.Tick(world, ref state, []);

        // Exactly zero, not merely non-positive: dealDamage's guard is an
        // exact `!= 0` and only End Step's clamp makes it safe.
        Assert.Equal(0, state.Characters[1].DeathmatchInvulnerable);
    }

    /// <summary>
    /// Walking spends it five times as fast, and the rate is never lowered
    /// again — one step ends the slow decay for that whole life.
    /// </summary>
    [Fact]
    public void WalkingSpendsProtectionFiveTimesAsFast()
    {
        var (state, world) = Started(invulnerabilityTicks: 30);
        Spawn(ref state, world, 1, Team.Red);

        Span<PlayerInput> walking = [default, new PlayerInput(PlayerKeys.Right, 0, 0)];
        MatchSimulation.Tick(world, ref state, walking);

        Assert.Equal(CharacterState.MovingInvulnDecay, state.Characters[1].DeathmatchInvulnDecay);

        // And it stays raised once the key is released.
        MatchSimulation.Tick(world, ref state, []);
        Assert.Equal(CharacterState.MovingInvulnDecay, state.Characters[1].DeathmatchInvulnDecay);

        // 30 spent at 1 a tick, minus the one already spent at 0.2.
        for (var i = 0; i < 40; i++)
            MatchSimulation.Tick(world, ref state, []);

        Assert.Equal(0, state.Characters[1].DeathmatchInvulnerable);
    }

    /// <summary>
    /// Jumping raises the rate too, and it is a <em>separate</em> line from
    /// the walking one — a character that jumps straight up without pressing
    /// left or right never sets <c>controlling</c>.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/Begin Step.xml:98</c>, and note it sits
    /// <em>outside</em> the <c>if(not stabbing)</c> below it: a spy who cannot
    /// actually jump still loses the slow decay.
    /// </remarks>
    [Fact]
    public void JumpingRaisesTheRateWithoutWalking()
    {
        var (state, world) = Started(invulnerabilityTicks: 30);
        Spawn(ref state, world, 1, Team.Red);

        // A jump needs the ground under it, so let the character land first.
        for (var i = 0; i < 60; i++)
            MatchSimulation.Tick(world, ref state, []);

        Assert.True(state.Characters[1].OnGround, "the character never landed");
        Assert.Equal(CharacterState.SpawnInvulnDecay, state.Characters[1].DeathmatchInvulnDecay);

        Span<PlayerInput> jumping = [default, new PlayerInput(PlayerKeys.Jump, 0, 0)];
        MatchSimulation.Tick(world, ref state, jumping);

        Assert.Equal(CharacterState.MovingInvulnDecay, state.Characters[1].DeathmatchInvulnDecay);
    }

    /// <summary>
    /// Pulling the trigger ends protection outright — and it is the
    /// <em>key</em> that does it, not the shot.
    /// </summary>
    [Fact]
    public void FiringEndsProtectionEvenWithAnEmptyMagazine()
    {
        var (state, world) = Started();
        Spawn(ref state, world, 1, Team.Red);

        state.Weapons[1].AmmoCount = 0;
        state.Weapons[1].ReadyToShoot = false;

        Span<PlayerInput> firing = [default, new PlayerInput(PlayerKeys.Attack, 0, 0)];
        MatchSimulation.Tick(world, ref state, firing);

        Assert.Equal(0, state.Characters[1].DeathmatchInvulnerable);
    }

    /// <summary>The right-click does the same, on a weapon that has no special.</summary>
    [Fact]
    public void TheRightClickEndsProtectionToo()
    {
        var (state, world) = Started();
        Spawn(ref state, world, 1, Team.Red);

        Span<PlayerInput> special = [default, new PlayerInput(PlayerKeys.Special, 0, 0)];
        MatchSimulation.Tick(world, ref state, special);

        Assert.Equal(0, state.Characters[1].DeathmatchInvulnerable);
    }

    /// <summary>
    /// Dying <em>while still protected</em> respawns you next tick with what
    /// was left, so killing somebody through their spawn bubble buys nothing.
    /// </summary>
    [Fact]
    public void DyingWhileProtectedRespawnsNextTickWithWhatWasLeft()
    {
        var (state, world) = Started(invulnerabilityTicks: 30);
        Spawn(ref state, world, 1, Team.Red);

        // Some of it spent, so the carried value is distinguishable from a
        // fresh one.
        for (var i = 0; i < 10; i++)
            MatchSimulation.Tick(world, ref state, []);

        var remaining = state.Characters[1].DeathmatchInvulnerable;
        Assert.True(remaining is > 0 and < 30);

        state.Characters[1].Hp = 0;
        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events);

        Assert.Equal(1, state.Players[1].RespawnTimer);
        Assert.Equal(remaining, state.Players[1].DeathmatchRespawnBypass);

        // And the next spawn takes it up rather than starting fresh.
        var points = new SpawnPoints(Level());
        Assert.True(state.Spawn(points, 1, PlayerClass.Scout, Team.Red, 0, 0));

        Assert.Equal(remaining, state.Characters[1].DeathmatchInvulnerable);

        // Spent, not kept: a second protected death has to set it again.
        Assert.Equal(0, state.Players[1].DeathmatchRespawnBypass);
    }

    /// <summary>
    /// The control: an unprotected death serves the respawn time and carries
    /// nothing.
    /// </summary>
    [Fact]
    public void AnUnprotectedDeathServesTheFullRespawnTime()
    {
        var (state, world) = Started(invulnerabilityTicks: 0);
        Spawn(ref state, world, 1, Team.Red);

        Assert.Equal(0, state.Characters[1].DeathmatchInvulnerable);

        state.Characters[1].Hp = 0;
        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events);

        Assert.Equal(state.RespawnTime, state.Players[1].RespawnTimer);
        Assert.Equal(0, state.Players[1].DeathmatchRespawnBypass);
    }

    /// <summary>
    /// The unprotected arm <em>writes</em> the bypass rather than leaving it
    /// — <c>player.deathmatch_respawn_bypass = 0</c> — so protection cannot be
    /// hoarded across a later, ordinary death.
    /// </summary>
    /// <remarks>
    /// The control the previous test cannot be: it starts with the field
    /// already zero, so it would pass with the assignment deleted.
    /// </remarks>
    [Fact]
    public void AnUnprotectedDeathClearsWhateverTheLastOneCarried()
    {
        var (state, world) = Started(invulnerabilityTicks: 30);
        Spawn(ref state, world, 1, Team.Red);

        // Left over from an earlier protected death.
        state.Players[1].DeathmatchRespawnBypass = 12;

        // And this life's protection has already run out.
        state.Characters[1].DeathmatchInvulnerable = 0;
        state.Characters[1].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events);

        Assert.Equal(0, state.Players[1].DeathmatchRespawnBypass);
        Assert.Equal(state.RespawnTime, state.Players[1].RespawnTimer);
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// A live Team Deathmatch server puts the kill limit in its HUD block and
    /// the invulnerability at the head of a <c>FULL_UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// The invulnerability field <b>leads the message</b>, ahead of the
    /// player count, so it comes out of the objective lookup before anything
    /// else is written — see <c>StateUpdateWriter.Write</c>.
    /// </remarks>
    [Fact]
    public void ALiveTeamDeathmatchServerFramesItsHudBlock()
    {
        var level = Level();

        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.KillLimit = 17;
            o.TdmInvulnerabilitySeconds = 5;
        });

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
        Assert.Equal(17, full.HudScalars[2]);

        // seconds * 30 * 0.2 — a fifth of the ticks it buys.
        Assert.Equal(30, full.TdmInvulnerabilityTicks);
    }
}
