using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// When a demoman presses SPECIAL — <c>botMinesArmed</c>.
/// </summary>
/// <remarks>
/// <b>Every test here that names a distance pairs it with what the engine's own
/// blast actually does at that distance</b>, by detonating the mine and reading
/// the victim's hp. That is deliberate: the reference's first version of this
/// triggered at 60px because it read the mine's <em>affect</em> radius, and the
/// resulting bot blew its whole stock the moment an enemy came near and dealt
/// nothing at all. A test that only asserted "30 arms and 40 does not" would pass
/// against 60 as happily as against 30 — it is the tie to the damage that bites.
/// </remarks>
public sealed class BotMinesTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>The measured wide floor — see <c>ScriptedBotTests</c>.</summary>
    private const double StandX = 3570;

    private const double StandY = 325;

    private static ScenarioWorld Load() =>
        ScenarioWorld.Load(Map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

    private static WorldView View(ScenarioWorld world) => new(in world.State, world.Collision);

    /// <summary>What a mine deals to somebody standing on it.</summary>
    private static readonly double FullBlast = Explosions.ExplosionDamageFor(ProjectileKind.Mine);

    /// <summary>A mine at a place, owned by a slot.</summary>
    /// <remarks>
    /// <c>SplashThreshold</c> is what <c>move_all_bullets</c> CLEARS on landing,
    /// so <c>inFlight: false</c> — the default, and what a mined doorway is — means
    /// the mine has <b>no quarter-radius cutoff</b>. The two forms deal different
    /// damage at the same distance and the tests measure both.
    /// </remarks>
    private static ProjectileHandle Mine(
        ScenarioWorld world, double x, double y, int owner, Team team, bool inFlight = false)
    {
        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            X = x,
            Y = y,
            OwnerSlot = (byte)owner,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = DamageSource.Minegun,
            Knockback = 4,
            Stickied = true,
            SplashThreshold = inFlight,
            Lifetime = Timers.Inactive,
            HasMoved = true,
        };

        mine.Motion.Speed = 0;
        return world.State.SpawnProjectile(mine);
    }

    /// <summary>Where <c>Place</c> actually put somebody.</summary>
    /// <remarks>
    /// <b><c>ScenarioWorld.Place</c> snaps downward to a nav node, so the
    /// coordinate handed to it is a request rather than a position.</b> Every
    /// distance in this file is measured from where the character ended up and the
    /// MINE is placed relative to that — the mine's own position is the only one
    /// under a test's control. A first draft measured from the requested
    /// coordinates and asserted a 45px gap that was really about 27.
    /// </remarks>
    private static (double X, double Y) Where(ScenarioWorld world, int slot) =>
        (world.State.Characters[slot].X, world.State.Characters[slot].Y);

    /// <summary>What one mine's blast takes off a character, in hp.</summary>
    private static double DamageDealt(ScenarioWorld world, ProjectileHandle mine, int victim)
    {
        var before = world.State.Characters[victim].Hp;
        Explosions.DetonateMine(world.Collision, ref world.State, mine, ref DiscardedEvents.Events);
        return before - world.State.Characters[victim].Hp;
    }

    /// <summary>
    /// A mine with an enemy standing on it is armed; the same mine with the enemy
    /// a stride away is not.
    /// </summary>
    [Fact]
    public void AMineUnderAnEnemyIsArmedAndOneAStrideAwayIsNot()
    {
        var near = Load();
        var demo = near.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);
        var scout = near.Place(PlayerClass.Scout, Team.Blue, StandX + 60, StandY);
        var (ex, ey) = Where(near, scout);

        // Under their feet.
        var mine = Mine(near, ex, ey, demo, Team.Red);
        Assert.True(BotMines.Armed(View(near), demo));

        // The same enemy, a stride away from the same mine.
        var far = Load();
        var demo2 = far.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);
        var scout2 = far.Place(PlayerClass.Scout, Team.Blue, StandX + 60, StandY);
        var (fx, fy) = Where(far, scout2);

        Mine(far, fx - BotConstants.MineTrigger - 10, fy, demo2, Team.Red);
        Assert.False(BotMines.Armed(View(far), demo2));

        // The premise the arming is about: the near one really is inside the blast.
        var dealt = DamageDealt(near, mine, scout);
        output.WriteLine($"a mine under the enemy's feet deals {dealt:0.0}");
        Assert.True(dealt > 0, "the armed fixture's mine deals nothing");
    }

    /// <summary>
    /// The trigger is the mine's DAMAGE radius, not its affect radius.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole behaviour, and the reference paid for it in play.</b>
    /// The first version of that script triggered at <b>60</b> px because it read
    /// the mine's <c>affectRadius</c> (65) rather than its <c>blastRadius</c> (40),
    /// so the bot detonated the moment an enemy came near, blew its whole stock and
    /// dealt nothing. Reported from play as "detonates too early, doesn't damage
    /// enemies", which was exactly right.
    /// <para>
    /// The assertion is on the DAMAGE rather than on the constant: at 60px the
    /// blast deals <b>zero</b>, in flight and stuck alike, so a mutation that puts
    /// the trigger back fails on what the number is <em>for</em>.
    /// </para>
    /// <para>
    /// <b>Measured here, and the reference's own comment is only half of it: a
    /// STUCK mine has no quarter-radius cutoff</b>, so the "takes nothing at all
    /// past 30 of box distance" it quotes is true of one still in flight and not of
    /// one lying in a doorway. Past 30px of box distance a stuck mine keeps dealing
    /// a smoothly vanishing amount — 3.4 of 45 at 45px centre-to-centre — and an
    /// in-flight one is skipped outright. Either way it is not worth eight mines.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(45.0, false)]
    [InlineData(45.0, true)]
    [InlineData(60.0, false)]
    [InlineData(60.0, true)]
    public void TheTriggerIsTheDamageRadiusAndNotTheAffectRadius(double gap, bool inFlight)
    {
        var world = Load();
        var demo = world.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);
        var scout = world.Place(PlayerClass.Scout, Team.Blue, StandX + 60, StandY);
        var (ex, ey) = Where(world, scout);

        Assert.True(gap < 65, "the fixture is outside the affect radius, so it gates nothing");
        Assert.True(gap > BotConstants.MineTrigger);

        var mine = Mine(world, ex - gap, ey, demo, Team.Red, inFlight);

        Assert.False(BotMines.Armed(View(world), demo));

        var dealt = DamageDealt(world, mine, scout);
        output.WriteLine($"{(inFlight ? "an airborne" : "a stuck")} mine {gap:0}px away deals {dealt:0.0}");

        if (inFlight || gap >= 60)
            Assert.Equal(0, dealt);
        else
            Assert.True(dealt < FullBlast * 0.1, $"{dealt:0.0} is not a negligible share of {FullBlast}");
    }

    /// <summary>
    /// At the trigger itself the blast is still worth spending, in both forms.
    /// </summary>
    /// <remarks>
    /// The other half of the constant, and the one that stops a mutation simply
    /// lowering it to nothing: <see cref="BotConstants.MineTrigger"/> is a
    /// conservative reading of the damage radius, not its edge — 30px
    /// centre-to-centre is about 22 of box distance, which is nearly half the full
    /// blast and is inside the quarter-radius cutoff an airborne mine still has.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtTheTriggerItselfTheBlastIsStillWorthSpending(bool inFlight)
    {
        var world = Load();
        var demo = world.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);
        var scout = world.Place(PlayerClass.Scout, Team.Blue, StandX + 60, StandY);
        var (ex, ey) = Where(world, scout);

        var mine = Mine(world, ex - BotConstants.MineTrigger, ey, demo, Team.Red, inFlight);

        Assert.True(BotMines.Armed(View(world), demo));

        var dealt = DamageDealt(world, mine, scout);
        output.WriteLine($"{(inFlight ? "an airborne" : "a stuck")} mine at the trigger deals {dealt:0.0}");

        Assert.True(dealt > FullBlast * 0.33, $"{dealt:0.0} of {FullBlast} is not worth the stock");
    }

    /// <summary>
    /// A mine somebody else lobbed does not arm this bot's SPECIAL.
    /// </summary>
    /// <remarks>
    /// The Minegun detonates every mine <em>this player</em> owns, so another
    /// demoman's are not this bot's to spring — and pressing would waste its own
    /// stock while leaving theirs where it is.
    /// </remarks>
    [Fact]
    public void AnotherDemomansMineDoesNotArmThisOne()
    {
        var world = Load();
        var mine = world.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);
        var other = world.Place(PlayerClass.Demoman, Team.Red, StandX - 40, StandY);
        var scout = world.Place(PlayerClass.Scout, Team.Blue, StandX + 60, StandY);
        var (ex, ey) = Where(world, scout);

        Mine(world, ex, ey, other, Team.Red);

        Assert.False(BotMines.Armed(View(world), mine));

        // The control: the identical mine, owned by the bot asking.
        Mine(world, ex, ey, mine, Team.Red);
        Assert.True(BotMines.Armed(View(world), mine));
    }

    /// <summary>
    /// A team-mate, a corpse and a cloaked spy are all worth nothing.
    /// </summary>
    /// <remarks>
    /// The cloak clause is the one worth having a control for. Nothing else in
    /// the bot layer can see a cloaked spy — target selection skips one — so
    /// detonating on one would be the single place a bot acted on information it
    /// does not have.
    /// </remarks>
    [Theory]
    [InlineData("ally")]
    [InlineData("dead")]
    [InlineData("cloaked")]
    public void NobodyWorthSpringingOnDoesNotArm(string kind)
    {
        var world = Load();
        var demo = world.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);

        var team = kind == "ally" ? Team.Red : Team.Blue;
        var victim = world.Place(
            kind == "cloaked" ? PlayerClass.Spy : PlayerClass.Scout, team, StandX + 60, StandY);

        var (ex, ey) = Where(world, victim);

        if (kind == "dead")
            world.State.Characters[victim].Hp = 0;

        if (kind == "cloaked")
            world.State.Characters[victim].Cloak = true;

        Mine(world, ex, ey, demo, Team.Red);

        Assert.False(BotMines.Armed(View(world), demo));

        // The control: the same body, live, enemy and visible.
        world.State.Characters[victim].Team = Team.Blue;
        world.State.Characters[victim].Hp = 100;
        world.State.Characters[victim].Cloak = false;

        Assert.True(BotMines.Armed(View(world), demo));
    }

    /// <summary>
    /// A sentry is asked with the BOX metric, and a character with the centre one.
    /// </summary>
    /// <remarks>
    /// <b>Not a refinement — it is the only reading that works.</b> The
    /// reference's script scanned characters and nothing else, so a demoman that
    /// had chosen a sentry as its target put its whole stock on that sentry and
    /// then never pressed SPECIAL, because no character was ever standing next to
    /// them. Reported from play as "shooting grenades at sentries, but not
    /// detonating".
    /// <para>
    /// The fixture is a sentry whose <em>centre</em> is outside the trigger and
    /// whose <em>box</em> is inside it, asserted both ways — so a port that reused
    /// the character's centre-to-centre test answers false and this fails.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASentryIsAskedWithTheBoxMetric()
    {
        var world = Load();
        var demo = world.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);

        var mineX = StandX + 20;
        var sentryX = mineX + 36;

        world.State.HasSentry[demo] = true;
        world.State.Sentries[demo] = new SentryState
        {
            Team = Team.Blue,
            X = sentryX,
            Y = StandY,
            Hp = 100,
            StartDirection = 1,
            Built = true,
        };

        var handle = Mine(world, mineX, StandY, demo, Team.Red);
        var box = Sentries.ReportedBox(world.State.Sentries[demo])
            .Distance(world.State.Projectiles[handle.Index].Box);

        var centre = Gml.PointDistance(mineX, StandY, sentryX, StandY);
        output.WriteLine($"sentry centre-to-centre {centre:0.0}, box-to-box {box:0.0}");

        Assert.True(
            centre > BotConstants.MineTrigger,
            "the centre distance is inside too, so the two metrics agree and this gates nothing");

        Assert.True(box < BotConstants.MineTrigger, "the box distance is outside, so nothing can arm");

        Assert.True(BotMines.Armed(View(world), demo));

        // The control: a sentry of the bot's own team is not worth springing on.
        world.State.Sentries[demo].Team = Team.Red;
        Assert.False(BotMines.Armed(View(world), demo));
    }

    /// <summary>
    /// The enemy generator arms it, and this bot's own does not.
    /// </summary>
    [Fact]
    public void TheEnemyGeneratorArmsItAndOursDoesNot()
    {
        var world = Load();
        var demo = world.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);

        world.State.BlueGenerator = new GeneratorState
        {
            Exists = true,
            X = StandX + 40,
            Y = StandY,
            Hp = 2100,
            ShieldHp = 300,
        };

        Mine(world, StandX + 20, StandY, demo, Team.Red);

        Assert.True(BotMines.Armed(View(world), demo));

        // The control is the SAME generator moved to red, not moved away: it is
        // the team test being gated, and a blast that hurts nobody is not worth
        // spending eight mines on.
        world.State.RedGenerator = world.State.BlueGenerator;
        world.State.BlueGenerator = default;

        Assert.False(BotMines.Armed(View(world), demo));
    }

    /// <summary>
    /// A demoman presses SPECIAL when a mine is armed and not otherwise — and it
    /// does not wait for a target.
    /// </summary>
    /// <remarks>
    /// <b>No <c>hasSubject</c> test on purpose.</b> A mined doorway is worth
    /// springing on whoever walks into it, and that is very often not the thing
    /// this demoman is aiming at. Every other branch in
    /// <see cref="BotClassKeys"/> that fires carries one; this is the exception,
    /// so it gets the control.
    /// <para>
    /// <c>BotClassKeys.For</c> rather than the whole combat step: the trigger
    /// this file is about would otherwise be measured through the fire delay, the
    /// aim settle and the hold-fire roll, and the last of those makes a pipeline
    /// test intermittent.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADemomanPressesSpecialWhenArmedEvenWithNothingToAimAt()
    {
        var world = Load();
        var demo = world.Place(PlayerClass.Demoman, Team.Red, StandX, StandY);
        var scout = world.Place(PlayerClass.Scout, Team.Blue, StandX + 60, StandY);
        var (ex, ey) = Where(world, scout);

        var state = new BotCombatState();

        Assert.Equal(
            PlayerKeys.None,
            BotClassKeys.For(state, View(world), demo, 0, 0) & PlayerKeys.Special);

        Mine(world, ex, ey, demo, Team.Red);

        Assert.True(
            (BotClassKeys.For(state, View(world), demo, 0, 0) & PlayerKeys.Special) != 0,
            "a demoman with an armed mine and no target did not detonate");
    }
}
