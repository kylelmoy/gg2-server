using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The scoreboard: what each player has done, and what a <c>FULL_UPDATE</c>
/// says about it.
/// </summary>
/// <remarks>
/// <c>Objects/InGameElements/Player.events/{Create, User Event 12}.xml</c> and
/// the awards spread across <c>Scripts/Events/doEvent*.gml</c>,
/// <c>Scripts/Maps/pointCapture.gml</c> and
/// <c>Objects/Weapons/Medigun.events/User Event 1.xml</c>.
/// <para>
/// <b>No fixture reaches any of it, and none can.</b> The stat block is
/// <c>FULL_UPDATE</c>-only and a recording holds exactly one, sent on the join
/// path before anything has happened — the same blind spot the objective has
/// (docs/PARITY.md). These tests and
/// <see cref="PlayerStatsLayoutTests"/> are the whole gate.
/// </para>
/// </remarks>
public sealed class ScoringTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    // ⚠️ The world comes back by return and the state by "out", deliberately.
    // A WorldState is well over a hundred kilobytes and these two helpers nest,
    // so returning it in a tuple put half a dozen copies on one stack — and that
    // overflowed the stack the next time the state grew (Quote's two projectile
    // fields did it). An "out" parameter is a byref: the body below writes the
    // caller's own variable and no copy exists at all.
    private static CollisionWorld Started(out WorldState state)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        state = default;

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 5;

        Intel.Place(ref state, level);
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        return world;
    }

    private static void Put(ref WorldState state, int slot, Team team,
        PlayerClass playerClass = PlayerClass.Soldier, bool alive = true)
    {
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;

        if (!alive)
            return;

        var character = CharacterState.Fresh(playerClass, team);
        character.X = 2000;
        character.Y = 700;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, character.X, character.Y);
        state.Alive[slot] = true;
    }

    private static double Stat(in WorldState state, int slot, StatIndex stat) =>
        Scoring.Get(state, slot, stat);

    private static double Round(in WorldState state, int slot, StatIndex stat) =>
        Scoring.GetRound(state, slot, stat);

    // --- the primitive --------------------------------------------------------

    /// <summary>
    /// ⚠️ Every award writes <em>both</em> arrays. That is not a convenience —
    /// all fourteen award sites in the reference write the pair.
    /// </summary>
    [Fact]
    public void AnAwardWritesBothTheMapAndTheRoundArray()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);

        Scoring.Award(ref state, 1, StatIndex.Kills, 1);

        Assert.Equal(1, Stat(state, 1, StatIndex.Kills));
        Assert.Equal(1, Round(state, 1, StatIndex.Kills));
    }

    /// <summary>
    /// A fresh player has an empty scoreboard, and an award to a slot with no
    /// player goes nowhere rather than throwing.
    /// </summary>
    [Fact]
    public void AnAwardToNobodyIsDropped()
    {
        Started(out var state);

        Scoring.Award(ref state, 5, StatIndex.Kills, 1);

        Assert.Equal(0, Stat(state, 5, StatIndex.Kills));
    }

    // --- a death --------------------------------------------------------------

    /// <summary>
    /// ⚠️ <c>victim.stats[DEATHS] += 1</c> is unconditional — a suicide, a pit
    /// and a generator explosion all count.
    /// </summary>
    [Fact]
    public void EveryDeathCountsAgainstItsVictimHoweverItHappened()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);

        state.Characters[1].Hp = 0;
        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events);

        Assert.Equal(1, Stat(state, 1, StatIndex.Deaths));
        Assert.Equal(0, Stat(state, 1, StatIndex.Kills));
    }

    /// <summary>An ordinary kill is one kill and one point.</summary>
    [Fact]
    public void AKillIsWorthAKillAndAPoint()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].LastDamageSource = DamageSource.Rocketlauncher;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(1, Stat(state, 1, StatIndex.Kills));
        Assert.Equal(1, Stat(state, 1, StatIndex.Points));
        Assert.Equal(1, Stat(state, 2, StatIndex.Deaths));
    }

    /// <summary>
    /// ⚠️ An assist is worth <b>half</b> a point — the only fractional award in
    /// the game, and the reason these counters are reals.
    /// </summary>
    [Fact]
    public void AnAssistIsWorthHalfAPoint()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);
        Put(ref state, 3, Team.Red);

        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].SecondToLastDamageDealer = 3;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(1, Stat(state, 3, StatIndex.Assists));
        Assert.Equal(0.5, Stat(state, 3, StatIndex.Points));
    }

    /// <summary>
    /// Killing a briefcase carrier is a defence, worth a second point on top of
    /// the kill.
    /// </summary>
    /// <remarks>
    /// ⚠️ It reads the victim's <em>character</em>, so it has to be awarded
    /// before the body is cleared — which is the whole reason
    /// <c>Scoring.RecordDeath</c> runs where it does in <c>Deaths.Kill</c>.
    /// </remarks>
    [Fact]
    public void KillingACarrierIsADefence()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.Characters[2].CarryingIntel = true;
        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(1, Stat(state, 1, StatIndex.Defenses));

        // The kill's point and the defence's.
        Assert.Equal(2, Stat(state, 1, StatIndex.Points));
    }

    /// <summary>
    /// Killing a medic sitting on a full charge is a bonus, and ⚠️ the clause is
    /// <em>outside</em> the <c>killer != victim</c> test — so a medic who kills
    /// themselves collects it.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]  // an enemy takes it
    [InlineData(2, 1)]  // and so does the medic, by their own hand
    public void KillingAChargedMedicIsABonus(int killer, int expectedBonus)
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue, PlayerClass.Medic);

        state.Weapons[2].UberReady = true;
        state.Characters[2].LastDamageDealer = (byte)killer;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(expectedBonus, Stat(state, killer, StatIndex.Bonus));
    }

    /// <summary>The control: a medic without a charge is worth no bonus.</summary>
    [Fact]
    public void KillingAnUnchargedMedicIsNoBonus()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue, PlayerClass.Medic);

        state.Weapons[2].UberReady = false;
        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(0, Stat(state, 1, StatIndex.Bonus));
        Assert.Equal(1, Stat(state, 1, StatIndex.Points));
    }

    /// <summary>
    /// ⚠️ A suicide scores no <c>KILLS</c> — <c>killer != victim</c> — though
    /// the death still counts.
    /// </summary>
    [Fact]
    public void ASuicideScoresNoKill()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);

        state.Characters[1].LastDamageDealer = 1;
        state.Characters[1].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events);

        Assert.Equal(0, Stat(state, 1, StatIndex.Kills));
        Assert.Equal(0, Stat(state, 1, StatIndex.Points));
        Assert.Equal(1, Stat(state, 1, StatIndex.Deaths));
    }

    // --- the objective --------------------------------------------------------

    /// <summary>A capture is one cap and two points.</summary>
    [Fact]
    public void ACaptureIsWorthTwoPoints()
    {
        var world = Started(out var state);
        Put(ref state, 1, Team.Red);

        state.Characters[1].CarryingIntel = true;
        state.Characters[1].X = state.RedIntel.HomeX;
        state.Characters[1].Y = state.RedIntel.HomeY;

        var events = default(TickEvents);
        Intel.CollisionStep(world, ref state, ref events);

        Assert.True(events.ScoredIntel[1]);
        Assert.Equal(1, Stat(state, 1, StatIndex.Caps));
        Assert.Equal(2, Stat(state, 1, StatIndex.Points));
    }

    /// <summary>Popping an ubercharge is one invuln and one point.</summary>
    [Fact]
    public void PoppingAnUberIsWorthAPoint()
    {
        var world = Started(out var state);
        Put(ref state, 1, Team.Red, PlayerClass.Medic);

        state.Weapons[1].UberReady = true;
        state.Characters[1].KeyState = PlayerKeys.Special | PlayerKeys.Attack;

        var events = default(TickEvents);
        Weapons.SpecialStep(world, ref state, 1, ref events);

        Assert.True(events.Ubered[1]);
        Assert.Equal(1, Stat(state, 1, StatIndex.Invulns));
        Assert.Equal(1, Stat(state, 1, StatIndex.Points));
    }

    /// <summary>
    /// Pulling down somebody else's sentry is one destruction and one point, and
    /// ⚠️ pulling down your own is worth nothing.
    /// </summary>
    /// <remarks>
    /// Driven through <see cref="Sentries"/> so that the award being wired into
    /// the destruction at all is part of what this gates — calling
    /// <c>RecordDestruction</c> directly passes with the call site deleted.
    /// </remarks>
    [Theory]
    [InlineData(1, 0)]  // the owner: nothing
    [InlineData(2, 1)]  // anybody else: a point
    public void OnlySomebodyElsesSentryScores(int killer, int expected)
    {
        var world = Started(out var state);
        Put(ref state, 1, Team.Red, PlayerClass.Engineer);
        Put(ref state, 2, Team.Blue);

        var sentry = SentryState.Build(Team.Red, 2000, 700, 1);
        sentry.Built = true;
        sentry.Hp = SentryState.MaxHp;
        state.Sentries[1] = sentry;
        state.HasSentry[1] = true;

        // Shot to pieces by whoever the case names.
        Sentries.Damage(ref state, 1, SentryState.MaxHp + 1,
            (byte)killer, DamageSource.Scattergun);

        // ⚠️ The hp check is in the sentry's End Step, not its Begin Step —
        // Sentries.EndStep, beside the character death check.
        var events = default(TickEvents);
        Sentries.EndStep(world, ref state, 1, ref events);

        Assert.True(events.SentryDestroyed[1], "the sentry survived");
        Assert.Equal(expected, Stat(state, killer, StatIndex.Destruction));
        Assert.Equal(expected, Stat(state, killer, StatIndex.Points));
    }

    // --- healing --------------------------------------------------------------

    /// <summary>
    /// A medic banks one point per 800 hit points healed, and ⚠️ the overshoot
    /// <b>carries</b> into the next point rather than being thrown away.
    /// </summary>
    /// <remarks>
    /// Driven through the real beam rather than by moving the accumulator by
    /// hand: a test that did the subtraction itself would pass with
    /// <c>hphealed -= HP_PER_POINT</c> replaced by <c>= 0</c>, which is the
    /// whole rule.
    /// </remarks>
    [Fact]
    public void HealingBanksAPointEveryEightHundredAndKeepsTheRemainder()
    {
        var world = Beaming(out var state);

        // Started just short of the threshold, on a value the beam's own
        // increment cannot land exactly on. Without the offset the total
        // reaches precisely 800 and the remainder is 0 either way — which would
        // make this pass with `hphealed = 0` in place of `hphealed -= 800`, and
        // that subtraction is the whole rule.
        const double Head = 795;
        state.Weapons[1].HpHealed = Head;
        Scoring.Award(ref state, 1, StatIndex.Healing, Head);

        var events = default(TickEvents);

        for (var i = 0; i < 60; i++)
        {
            state.Characters[2].Hp = 10;
            state.Characters[2].TimeUnscathed = CharacterState.MaxTimeUnscathed;
            Healing.BeamStep(world, ref state, 1, ref events);

            if (Stat(state, 1, StatIndex.Points) > 0)
                break;
        }

        Assert.Equal(1, Stat(state, 1, StatIndex.Points));

        // What is left over is the overshoot, carried — not zero, and not the
        // whole total.
        var banked = Stat(state, 1, StatIndex.Healing);
        Assert.True(banked > GameConstants.HpPerPoint, $"only banked {banked}");
        Assert.Equal(banked - GameConstants.HpPerPoint, state.Weapons[1].HpHealed, 9);
        Assert.True(state.Weapons[1].HpHealed > 0, "the remainder was thrown away");
    }

    /// <summary>A medic and a hurt team-mate, beam connected.</summary>
    private static CollisionWorld Beaming(out WorldState state)
    {
        var world = Started(out state);
        Put(ref state, 1, Team.Red, PlayerClass.Medic);
        Put(ref state, 2, Team.Red);

        state.Characters[2].X = state.Characters[1].X + 10;
        state.Characters[2].Y = state.Characters[1].Y;
        state.Characters[2].Hp = 10;
        state.Weapons[1].X = state.Characters[1].X;
        state.Weapons[1].Y = state.Characters[1].Y;
        state.Characters[1].KeyState = PlayerKeys.Attack;
        state.Characters[1].NetAimDirection = 0;
        state.Weapons[1].ReadyToShoot = true;
        state.Weapons[1].RefireTimer = WeaponState.Inactive;

        return world;
    }

    /// <summary>
    /// The real beam, end to end: a medic healing a hurt team-mate banks
    /// fractional hit points into the stat every tick.
    /// </summary>
    [Fact]
    public void TheBeamBanksHealingEveryTick()
    {
        var world = Started(out var state);
        Put(ref state, 1, Team.Red, PlayerClass.Medic);
        Put(ref state, 2, Team.Red);

        // Beside each other, and the patient hurt so the beam has something to
        // do.
        state.Characters[2].X = state.Characters[1].X + 10;
        state.Characters[2].Y = state.Characters[1].Y;
        state.Characters[2].Hp = 10;
        state.Weapons[1].X = state.Characters[1].X;
        state.Weapons[1].Y = state.Characters[1].Y;
        state.Characters[1].KeyState = PlayerKeys.Attack;

        // Aiming due right, at the patient. The beam picks its target by the
        // reconstructed cursor, so the aim has to point somewhere real — and
        // the weapon has to be able to acquire one at all, which is what
        // readyToShoot guards (only the acquisition; the beam itself has no
        // such test — see Weapons.FireStep).
        state.Characters[1].NetAimDirection = 0;
        state.Weapons[1].ReadyToShoot = true;
        state.Weapons[1].RefireTimer = WeaponState.Inactive;

        var events = default(TickEvents);
        Healing.BeamStep(world, ref state, 1, ref events);

        Assert.True(Stat(state, 1, StatIndex.Healing) > 0,
            "the medic banked nothing; the beam did not connect");
        Assert.Equal(Stat(state, 1, StatIndex.Healing), state.Weapons[1].HpHealed);
    }

    // --- the resets -----------------------------------------------------------

    /// <summary>
    /// ⚠️ A map change wipes both arrays — but <b>only on area 1</b>, so a
    /// multi-area Invasion map carries its scoreboard between areas.
    /// </summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    public void AMapChangeClearsTheScoreboardOnlyOnTheFirstArea(bool firstArea, double expected)
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);

        Scoring.Award(ref state, 1, StatIndex.Kills, 3);

        Rounds.ResetForNewMap(ref state, firstArea);

        Assert.Equal(expected, Stat(state, 1, StatIndex.Kills));
        Assert.Equal(expected, Round(state, 1, StatIndex.Kills));
    }

    /// <summary>
    /// An Arena round start clears the round array and ⚠️ leaves the map one
    /// alone — which is the entire reason there are two.
    /// </summary>
    /// <remarks>
    /// Driven through <c>Arena.Step</c>'s own transition, so that the reset
    /// being wired into a round start is part of what this gates.
    /// </remarks>
    [Fact]
    public void AnArenaRoundStartClearsOnlyTheRoundArray()
    {
        const string ArenaMap = "arena_lumberyard";
        var level = LevelMap.FromPng(ArenaMap, ReferenceTree.ReadMap(ArenaMap)).ForArea(1);
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 5;
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);
        Scoring.Award(ref state, 1, StatIndex.Kills, 4);

        // One tick of setup left, so this tick starts the round proper.
        state.ArenaState = ArenaState.RoundSetup;
        state.ArenaRoundStart = 1;

        var events = default(TickEvents);
        Arena.Step(ref state, null, ref events);

        Assert.True(events.ArenaRoundStarted, "the round never started");
        Assert.Equal(4, Stat(state, 1, StatIndex.Kills));
        Assert.Equal(0, Round(state, 1, StatIndex.Kills));
    }

    /// <summary>
    /// Taking a control point is two points to <em>every</em> team-mate standing
    /// on it, and nothing to anybody else.
    /// </summary>
    /// <remarks>
    /// ⚠️ The test is <c>player.team == point.cappingTeam</c> and not "helped
    /// capture": a team-mate who walked on this tick scores the same two points
    /// as the one who spent thirty seconds on it.
    /// <para>
    /// Driven through <c>ControlPoints.Step</c>, because the award has to happen
    /// on the tick the meter completes and while <c>cappingPoint</c> still names
    /// the point.
    /// </para>
    /// </remarks>
    [Fact]
    public void TakingAPointScoresForEveryTeamMateOnIt()
    {
        const string KothMap = "koth_valley";
        var level = LevelMap.FromPng(KothMap, ReferenceTree.ReadMap(KothMap));
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        state.ControlPointUnlock = 0;
        state.ControlPoints[0].Locked = false;

        var zone = world.CaptureZones[0];
        var x = (zone.Left + zone.Right) / 2.0;
        var y = (zone.Top + zone.Bottom) / 2.0;

        // Two reds on the hill and a blue well away from it.
        foreach (var slot in new[] { 1, 2 })
        {
            Put(ref state, slot, Team.Red);
            state.Characters[slot].X = x;
            state.Characters[slot].Y = y;
        }

        // And a blue standing on it too. ⚠️ Reachable, and it is what makes the
        // team test load-bearing: a *contested* point is frozen rather than
        // reverting, but a meter that is already full still completes on the
        // tick an enemy arrives — the freeze is in Advance and the completion
        // check is below it.
        Put(ref state, 3, Team.Blue);
        state.Characters[3].X = x;
        state.Characters[3].Y = y;

        // Already full, so this Step completes it.
        state.ControlPoints[0].BeingCaptured = true;
        state.ControlPoints[0].CappingTeam = Team.Red;
        state.ControlPoints[0].Capping = state.ControlPoints[0].CapTime;

        // The characters' Begin Step is what sets cappingPoint.
        for (var slot = 1; slot <= 3; slot++)
        {
            if (state.Alive[slot])
                ControlPoints.BeginStep(world, ref state.Characters[slot]);
        }

        ControlPoints.Step(ref state);

        Assert.True(state.ControlPoints[0].Owned, "the point was not taken");
        Assert.Equal(1, Stat(state, 1, StatIndex.Caps));
        Assert.Equal(2, Stat(state, 1, StatIndex.Points));
        Assert.Equal(1, Stat(state, 2, StatIndex.Caps));
        Assert.Equal(2, Stat(state, 2, StatIndex.Points));
        // The blue was standing on it and scores nothing.
        Assert.Equal(0, Stat(state, 3, StatIndex.Caps));
        Assert.Equal(0, Stat(state, 3, StatIndex.Points));
    }

    // --- the Arena MVP lists --------------------------------------------------

    /// <summary>
    /// The top three of a team by round points, best first.
    /// </summary>
    [Fact]
    public void TheMvpListIsTheTopThreeByRoundPoints()
    {
        Started(out var state);

        for (var slot = 1; slot <= 5; slot++)
            Put(ref state, slot, Team.Red);

        // Deliberately not in slot order, so a list that merely walks the roster
        // fails.
        Scoring.Award(ref state, 1, StatIndex.Points, 2);
        Scoring.Award(ref state, 2, StatIndex.Points, 9);
        Scoring.Award(ref state, 3, StatIndex.Points, 5);
        Scoring.Award(ref state, 4, StatIndex.Points, 7);
        Scoring.Award(ref state, 5, StatIndex.Points, 1);

        Span<ArenaMvp> mvps = stackalloc ArenaMvp[Scoring.MaxMvps];
        var count = Scoring.SelectMvps(ref state, Team.Red, mvps);

        Assert.Equal(3, count);
        Assert.Equal(2, mvps[0].PlayerId);
        Assert.Equal(4, mvps[1].PlayerId);
        Assert.Equal(3, mvps[2].PlayerId);
        Assert.Equal(9, mvps[0].Points);
    }

    /// <summary>
    /// ⚠️ Every player on the team goes in whatever they scored, so a team of
    /// two has two MVPs with nothing to their name — and a team of none has an
    /// empty list.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(5, 3)]
    public void EverybodyOnTheTeamIsACandidate(int players, int expected)
    {
        Started(out var state);

        for (var slot = 1; slot <= players; slot++)
            Put(ref state, slot, Team.Blue);

        Span<ArenaMvp> mvps = stackalloc ArenaMvp[Scoring.MaxMvps];

        Assert.Equal(expected, Scoring.SelectMvps(ref state, Team.Blue, mvps));
    }

    /// <summary>
    /// ⚠️ Three a side is the ceiling even when the caller offers more room —
    /// <c>while ds_priority_size &gt; 3 delete_min</c>, and the wire counts are
    /// bytes the client reads three of at most.
    /// </summary>
    [Fact]
    public void NoMoreThanThreeMvpsHoweverMuchRoomThereIs()
    {
        Started(out var state);

        for (var slot = 1; slot <= 6; slot++)
        {
            Put(ref state, slot, Team.Red);
            Scoring.Award(ref state, slot, StatIndex.Points, slot);
        }

        Span<ArenaMvp> roomy = stackalloc ArenaMvp[8];

        Assert.Equal(Scoring.MaxMvps, Scoring.SelectMvps(ref state, Team.Red, roomy));
    }

    /// <summary>
    /// It reads the <em>round</em> array, not the map one — the whole point of
    /// keeping both.
    /// </summary>
    [Fact]
    public void TheMvpListReadsTheRoundArrayAndNotTheMapOne()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Red);

        Scoring.Award(ref state, 1, StatIndex.Points, 10);
        Scoring.Award(ref state, 2, StatIndex.Points, 1);

        // A new round: slot 1's ten points stay on the map scoreboard and leave
        // the round one.
        Scoring.ResetRound(ref state);
        Scoring.Award(ref state, 2, StatIndex.Points, 4);

        Span<ArenaMvp> mvps = stackalloc ArenaMvp[Scoring.MaxMvps];
        Scoring.SelectMvps(ref state, Team.Red, mvps);

        Assert.Equal(2, mvps[0].PlayerId);
    }

    /// <summary>
    /// ⚠️ Selecting the MVPs <b>writes back</b> a rounded healing total —
    /// <c>redMVP[i].roundStats[HEALING] = round(...)</c> is an assignment, not a
    /// cast on the way out.
    /// </summary>
    [Fact]
    public void SelectingMvpsRoundsTheHealingStatInPlace()
    {
        Started(out var state);
        Put(ref state, 1, Team.Red, PlayerClass.Medic);

        Scoring.Award(ref state, 1, StatIndex.Healing, 1234.5);

        Span<ArenaMvp> mvps = stackalloc ArenaMvp[Scoring.MaxMvps];
        Scoring.SelectMvps(ref state, Team.Red, mvps);

        // GML's round is half to even, so 1234.5 goes to 1234 and not 1235.
        Assert.Equal(1234, Round(state, 1, StatIndex.Healing));
        Assert.Equal(1234, mvps[0].Healing);

        // And the map-long total is untouched: the write-back is to roundStats.
        Assert.Equal(1234.5, Stat(state, 1, StatIndex.Healing));
    }
}
