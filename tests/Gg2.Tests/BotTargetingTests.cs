using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Picking what to shoot at, and where to point —
/// <c>botFindTarget.gml</c>, <c>botAimSolve.gml</c> and the scripts behind them.
/// </summary>
/// <remarks>
/// The targeting tests build a world by hand, because every claim is about the
/// <em>ordering</em> of candidates and the only way to see an ordering is to
/// construct one where the answers disagree. Threat is scored in pixels, so
/// each bonus is asserted by putting the favoured candidate <em>further away</em>
/// than the one it has to beat — a test where the preferred target is also the
/// nearest gates nothing.
/// </remarks>
public sealed class BotTargetingTests
{
    private const string Map = "ctf_truefort";

    /// <summary>An open stretch of the map, so line of sight is not the variable.</summary>
    /// <remarks>
    /// Every targeting test here runs on real geometry, because
    /// <see cref="BotTargeting.Find"/>'s last act is a real
    /// <c>collision_line_bulletblocking</c>. A hand-built empty world would make
    /// every candidate visible and the blind-fallback branch unreachable.
    /// </remarks>
    private static CollisionWorld Collision()
    {
        ReferenceTree.RequireAvailable();
        return new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));
    }

    /// <summary>A world with characters placed exactly where asked.</summary>
    private static WorldState World(params (Team Team, PlayerClass Class, double X, double Y)[] people)
    {
        var state = default(WorldState);

        for (var slot = 0; slot < people.Length; slot++)
        {
            state.AddPlayer(slot);
            state.Players[slot].Team = people[slot].Team;
            state.Players[slot].Class = people[slot].Class;

            // Fresh, never `new CharacterState()` — six of its fields name a
            // roster slot and a zeroed one claims slot 0 last hurt it, which here
            // would hand slot 0 the attacker bonus in every test.
            var character = CharacterState.Fresh(people[slot].Class, people[slot].Team);
            character.X = people[slot].X;
            character.Y = people[slot].Y;

            state.Characters[slot] = character;
            state.Alive[slot] = true;
        }

        return state;
    }

    /// <summary>
    /// A clear stretch of ctf_truefort, and a wall just below it.
    /// </summary>
    /// <remarks>
    /// <b>Found by sweeping the map for the widest unblocked horizontal
    /// sightline, not read off a screenshot.</b> A candidate that looks like open
    /// floor can still put the <em>further</em> threat-bonus candidate behind
    /// geometry, so the near one wins by default and the test measures a wall
    /// instead of the bonus.
    /// <para>
    /// Measured: from (380, 268) the sightline is clear for at least 1,000px
    /// horizontally, and blocked 80px straight down — which is what makes the
    /// blind-fallback fixture below a real blocked line rather than a contrived
    /// one.
    /// </para>
    /// </remarks>
    private const double FloorY = 268;

    private const double LeftX = 380;

    /// <summary>The class radius is an attention radius, and it is per class.</summary>
    /// <remarks>
    /// A sniper looks four times as far as a pyro. Getting this from the
    /// weapon's range instead would give a soldier 1,000 and a pyro 165, and the
    /// numbers are a tuned set rather than a derivation.
    /// </remarks>
    [Fact]
    public void TheAttentionRadiusIsPerClass()
    {
        Assert.Equal(900, BotTargeting.ClassRange(PlayerClass.Sniper));
        Assert.Equal(800, BotTargeting.ClassRange(PlayerClass.Soldier));
        Assert.Equal(220, BotTargeting.ClassRange(PlayerClass.Scout));
        Assert.Equal(200, BotTargeting.ClassRange(PlayerClass.Pyro));

        // Every class the port knows has a row; the fallback is for nothing.
        foreach (var playerClass in Enum.GetValues<PlayerClass>())
            Assert.NotEqual(375, BotTargeting.ClassRange(playerClass));
    }

    /// <summary>
    /// The splash classes keep their distance and three classes have no minimum
    /// at all.
    /// </summary>
    /// <remarks>
    /// The pyro, the spy and the medic are zero because point blank is where
    /// all three of them work — a minimum band would make a pyro back away from
    /// the only range its weapon reaches.
    /// </remarks>
    [Fact]
    public void OnlyTheSplashClassesKeepTheirDistance()
    {
        Assert.Equal(BotConstants.SplashSafe, BotTargeting.ClassMinBand(PlayerClass.Soldier));
        Assert.Equal(BotConstants.SplashSafe, BotTargeting.ClassMinBand(PlayerClass.Demoman));

        Assert.Equal(0, BotTargeting.ClassMinBand(PlayerClass.Pyro));
        Assert.Equal(0, BotTargeting.ClassMinBand(PlayerClass.Spy));
        Assert.Equal(0, BotTargeting.ClassMinBand(PlayerClass.Medic));

        Assert.Equal(BotConstants.MinEngage, BotTargeting.ClassMinBand(PlayerClass.Scout));
    }

    /// <summary>The nearest enemy wins when nothing else distinguishes them.</summary>
    [Fact]
    public void TheNearestEnemyWinsWhenNothingElseDiffers()
    {
        var state = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 400, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 200, FloorY));

        var target = BotTargeting.Find(new WorldView(in state, Collision()), 0, 800);

        Assert.Equal(BotTargetKind.Character, target.Kind);
        Assert.Equal(2, target.Slot);
        Assert.True(target.Visible);

        // Team-mates are never candidates, whatever the distance.
        var friendly = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Red, PlayerClass.Scout, LeftX + 100, FloorY));

        Assert.False(BotTargeting.Find(new WorldView(in friendly, Collision()), 0, 800).Exists);
    }

    /// <summary>
    /// Each threat bonus is worth its own number of pixels, and beats a nearer
    /// enemy by exactly that much.
    /// </summary>
    /// <remarks>
    /// <b>The favoured candidate is placed FURTHER AWAY on purpose.</b> Every
    /// term is in pixels, and the whole point of the scoring is that it is
    /// willing to walk past someone harmless — so a test where the preferred
    /// target is also the nearest passes with the entire bonus deleted.
    /// </remarks>
    [Theory]
    // Carrying the intel is worth 300px: 250 further away and still chosen.
    [InlineData(250, PlayerClass.Scout, true, false, false, 1)]
    [InlineData(350, PlayerClass.Scout, true, false, false, 2)]
    // A medic is worth 120.
    [InlineData(100, PlayerClass.Medic, false, false, false, 1)]
    [InlineData(150, PlayerClass.Medic, false, false, false, 2)]
    // An engineer is worth 60.
    [InlineData(40, PlayerClass.Engineer, false, false, false, 1)]
    [InlineData(80, PlayerClass.Engineer, false, false, false, 2)]
    // Already wounded is worth 100.
    [InlineData(80, PlayerClass.Scout, false, true, false, 1)]
    [InlineData(120, PlayerClass.Scout, false, true, false, 2)]
    // Whoever is shooting me is worth BOT_ATTACKER_BONUS, 150.
    [InlineData(120, PlayerClass.Scout, false, false, true, 1)]
    [InlineData(180, PlayerClass.Scout, false, false, true, 2)]
    public void EachThreatBonusIsWorthItsOwnPixels(
        double extraDistance, PlayerClass favouredClass,
        bool intel, bool wounded, bool attacker, int expected)
    {
        // Slot 1 is the favoured candidate, further away; slot 2 is a plain scout
        // at 200. The bonus has to cover `extraDistance` for slot 1 to win.
        var state = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, favouredClass, LeftX + 200 + extraDistance, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 200, FloorY));

        if (intel)
            state.Characters[1].CarryingIntel = true;

        if (wounded)
            state.Characters[1].Hp = ClassStats.For(favouredClass).MaxHp * 0.3;

        if (attacker)
            state.Characters[0].LastDamageDealer = 1;

        var target = BotTargeting.Find(new WorldView(in state, Collision()), 0, 800);

        Assert.Equal(expected, target.Slot);
    }

    /// <summary>
    /// An enemy inside the class's own minimum band is ranked a whole attention
    /// radius worse.
    /// </summary>
    /// <remarks>
    /// <b>This is a LOCK problem, not a range problem.</b> A soldier's band is
    /// 110-800px and it fires throughout — but with
    /// threat as pure distance it always locked whoever was nearest, and the
    /// combat layer does not look for a new target while it holds a valid one. An
    /// enemy that closes inside <c>BOT_SPLASH_SAFE</c> therefore pins a soldier
    /// permanently: it keeps the lock, refuses to fire because a rocket at that
    /// range kills the shooter, and a second enemy at a perfectly shootable 300px
    /// is never considered.
    /// <para>
    /// The penalty is <c>+range</c>, so it is always enough to lose to anything
    /// inside the radius — which is the property, not the number.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEnemyInsideTheMinimumBandLosesToOneFurtherOut()
    {
        var state = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 60, FloorY),   // inside 110
            (Team.Blue, PlayerClass.Scout, LeftX + 400, FloorY));

        Assert.Equal(2, BotTargeting.Find(new WorldView(in state, Collision()), 0, 800).Slot);

        // The control: a pyro has no minimum band, so the same geometry gives
        // the opposite answer. Point blank is where a pyro works.
        var pyro = World(
            (Team.Red, PlayerClass.Pyro, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 60, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 150, FloorY));

        Assert.Equal(1, BotTargeting.Find(new WorldView(in pyro, Collision()), 0, 200).Slot);
    }

    /// <summary>A cloaked spy is not a candidate at all.</summary>
    /// <remarks>
    /// Not merely a worse one: a bot that ranked a cloaked spy last would still
    /// pick it when it was the only enemy in the room, and shoot at nothing.
    /// </remarks>
    [Fact]
    public void ACloakedSpyIsNotACandidate()
    {
        var state = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Spy, LeftX + 100, FloorY));

        Assert.True(BotTargeting.Find(new WorldView(in state, Collision()), 0, 800).Exists);

        state.Characters[1].Cloak = true;
        Assert.False(BotTargeting.Find(new WorldView(in state, Collision()), 0, 800).Exists);
    }

    /// <summary>
    /// A target behind a wall is still a target, inside the blind radius.
    /// </summary>
    /// <remarks>
    /// <b>Losing every candidate to line of sight does not mean there is
    /// nothing to fight.</b> A blocked search falls back to the nearest
    /// candidate inside <c>BOT_BLIND_RANGE</c> and returns it with
    /// <see cref="BotTarget.Visible"/> false — the combat layer holds its aim on
    /// the last known position and engages the moment the sightline opens.
    /// <para>
    /// And the radius is short on purpose: a blind target across the map is a
    /// bot aiming at scenery. The second half of this test is what fails if the
    /// fallback is made unconditional.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATargetBehindAWallIsStillATargetButOnlyNearby()
    {
        var collision = Collision();

        // 120px straight down from the open stretch is inside the map's own
        // geometry — measured, see FloorY.
        var near = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX, FloorY + 120));

        var blocked = collision.CollisionLineBlocksBullets(
            LeftX, FloorY, LeftX, FloorY + 120, near.BulletFilter);

        Assert.True(blocked, "the fixture's sightline is not actually blocked; pick another pair");

        var blind = BotTargeting.Find(new WorldView(in near, collision), 0, 800);

        Assert.True(blind.Exists, "a blocked candidate inside the blind radius was dropped");
        Assert.False(blind.Visible);
        Assert.Equal(1, blind.Slot);

        // Past BOT_BLIND_RANGE the same blocked candidate is nothing at all.
        var far = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX, FloorY + BotConstants.BlindRange + 200));

        Assert.False(BotTargeting.Find(new WorldView(in far, collision), 0, 900).Exists);
    }

    /// <summary>
    /// An enemy sentry is a candidate, and outranks a player at the same
    /// distance.
    /// </summary>
    /// <remarks>
    /// A sentry must be a candidate in its own right, distinct from characters and
    /// the generator, or a bot walks into one, dies, and respawns to do it again
    /// with no term anywhere in the model for the thing that killed it. It ranks
    /// ahead of a player at the same distance because a player can be walked away
    /// from and a sentry covers its ground until somebody removes it.
    /// <para>
    /// And a sentry is aimed at by the <b>centre of its box</b>, not its
    /// origin — the reference makes the same distinction with
    /// <c>botIsCharacter</c>, and getting it wrong aims a bot at the sentry's
    /// feet.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEnemySentryOutranksAPlayerAtTheSameDistance()
    {
        var state = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 300, FloorY));

        state.AddPlayer(2);
        state.Players[2].Team = Team.Blue;
        state.Sentries[2] = SentryState.Build(Team.Blue, LeftX + 300, FloorY, 1);
        state.HasSentry[2] = true;

        var target = BotTargeting.Find(new WorldView(in state, Collision()), 0, 800);

        Assert.Equal(BotTargetKind.Sentry, target.Kind);
        Assert.Equal(2, target.Slot);

        // The aim point is the box's CENTRE, and a sentry's box is
        // `-8, -7, 7, 12` about its origin — so the centre is 2.5px *below* it,
        // not above. Asserted as the number rather than as an inequality,
        // because "somewhere near the sentry" is satisfied by aiming at the
        // origin and gates nothing.
        Assert.Equal(FloorY + 2.5, target.Y, 9);

        // A friendly sentry is not a candidate.
        state.Sentries[2].Team = Team.Red;
        state.Players[2].Team = Team.Red;

        Assert.Equal(
            BotTargetKind.Character,
            BotTargeting.Find(new WorldView(in state, Collision()), 0, 800).Kind);
    }

    /// <summary>
    /// The generator is only ever chosen when nothing live is available.
    /// </summary>
    /// <remarks>
    /// <c>BOT_GEN_THREAT</c> is 4,000 — far past any distance term inside any
    /// class's radius. A bot that keeps shooting a wall while somebody kills
    /// it is worse than one that never shoots the wall.
    /// </remarks>
    [Fact]
    public void TheGeneratorIsTheLastResort()
    {
        var state = World((Team.Red, PlayerClass.Soldier, LeftX, FloorY));

        state.BlueGenerator.Exists = true;
        state.BlueGenerator.X = LeftX + 100;
        state.BlueGenerator.Y = FloorY;
        state.BlueGenerator.Hp = 2100;

        Assert.Equal(
            BotTargetKind.Generator,
            BotTargeting.Find(new WorldView(in state, Collision()), 0, 800).Kind);

        // Anything alive beats it, even at the far edge of the radius.
        var withEnemy = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 700, FloorY));

        withEnemy.BlueGenerator = state.BlueGenerator;

        Assert.Equal(
            BotTargetKind.Character,
            BotTargeting.Find(new WorldView(in withEnemy, Collision()), 0, 800).Kind);
    }

    /// <summary>Nothing outside the attention radius is a candidate.</summary>
    [Fact]
    public void NothingOutsideTheRadiusIsACandidate()
    {
        var state = World(
            (Team.Red, PlayerClass.Pyro, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 300, FloorY));

        // A pyro looks 200px.
        Assert.False(
            BotTargeting.Find(
                new WorldView(in state, Collision()), 0,
                BotTargeting.ClassRange(PlayerClass.Pyro)).Exists);

        // A soldier looks 800.
        Assert.True(
            BotTargeting.Find(
                new WorldView(in state, Collision()), 0,
                BotTargeting.ClassRange(PlayerClass.Soldier)).Exists);
    }

    /// <summary><c>botEnemyWithin</c> answers about live, uncloaked enemies only.</summary>
    [Fact]
    public void EnemyWithinIgnoresTheCloakedAndTheFriendly()
    {
        var state = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Spy, LeftX + 100, FloorY),
            (Team.Red, PlayerClass.Scout, LeftX + 50, FloorY));

        var collision = Collision();

        Assert.True(BotTargeting.EnemyWithin(new WorldView(in state, collision), 0, 200));

        state.Characters[1].Cloak = true;
        Assert.False(BotTargeting.EnemyWithin(new WorldView(in state, collision), 0, 200));

        state.Characters[1].Cloak = false;
        Assert.False(BotTargeting.EnemyWithin(new WorldView(in state, collision), 0, 50));
    }

    /// <summary>The signed angle difference wraps the short way round.</summary>
    /// <remarks>
    /// The <c>+ 540</c> is what makes it work for a negative first difference
    /// rather than decoration: GML's <c>mod</c> keeps the sign of the dividend
    /// and so does C#'s <c>%</c>, so <c>(10 - 350) % 360</c> is -340 and not 20.
    /// </remarks>
    [Theory]
    [InlineData(10, 350, 20)]
    [InlineData(350, 10, -20)]
    // Exactly opposite gives -180, not +180: the range is [-180, 180). The
    // arithmetic wraps 360 back to 0 before the subtraction, so the half turn
    // lands on the low end. Worth pinning because "which way does a bot turn to
    // face directly behind it" is decided here.
    [InlineData(0, 180, -180)]
    [InlineData(90, 0, 90)]
    [InlineData(0, 90, -90)]
    [InlineData(720 + 45, 45, 0)]
    public void TheAngleDifferenceWrapsTheShortWayRound(double a, double b, double expected) =>
        Assert.Equal(expected, BotTargeting.AngleDelta(a, b), 9);
}
