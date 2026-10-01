using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// The two reactions to something already in the air: the pyro's airblast and the
/// rocket dodge.
/// </summary>
/// <remarks>
/// <b>Neither question can be answered by measuring a distance</b> —
/// the airblast's trigger volume is a narrow beam an order of magnitude smaller
/// than the radius it used, and whether jumping helps depends on where the rocket
/// is aimed rather than on how near it is. So every test here is about the SHAPE
/// of the answer, and each has a fixture that a radius test would get backwards.
/// </remarks>
public sealed class BotReflexTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>The measured wide floor — see <c>ScriptedBotTests</c>.</summary>
    private const double StandX = 3570;

    private const double StandY = 325;

    private static ScenarioWorld Load() =>
        ScenarioWorld.Load(Map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

    private static WorldView View(ScenarioWorld world) => new(in world.State, world.Collision);

    /// <summary>Points a character's aim, in degrees.</summary>
    /// <remarks>
    /// <c>AimDirection</c> is derived and read-only — the stored field is the
    /// wire's <c>ushort</c>, <c>direction * 65536 / 360</c>. Writing the derived
    /// property is not possible, and a test that reached for it would be reaching
    /// for a number the simulation never stores.
    /// </remarks>
    private static void Aim(ScenarioWorld world, int slot, double degrees) =>
        world.State.Characters[slot].NetAimDirection = (ushort)(degrees * 65536.0 / 360.0);

    /// <summary>
    /// An enemy rocket at a place, moving at a velocity, that has already flown.
    /// </summary>
    /// <remarks>
    /// <c>LastKnownX/Y</c> is where the travelled distance is measured from —
    /// this port derives it rather than storing a counter, exactly as
    /// <c>move_all_bullets</c> does. Putting it far away is what makes the rocket
    /// eligible for a reflect.
    /// </remarks>
    private static ProjectileHandle Rocket(
        ScenarioWorld world, double x, double y, double vx, double vy,
        Team team = Team.Blue, double travelled = 500,
        ProjectileKind kind = ProjectileKind.Rocket)
    {
        var projectile = new ProjectileState
        {
            Kind = kind,
            Team = team,
            OwnerSlot = ProjectileState.NoOwner,
            X = x,
            Y = y,
            LastKnownX = x - travelled,
            LastKnownY = y,
        };

        projectile.Motion.HSpeed = vx;
        projectile.Motion.VSpeed = vy;

        return world.State.SpawnProjectile(in projectile);
    }

    // ------------------------------------------------------------------
    // The airblast beam
    // ------------------------------------------------------------------

    /// <summary>
    /// The blast volume is a beam with a near edge, not a circle.
    /// </summary>
    /// <remarks>
    /// <b>There IS a near edge and it is not a rounding detail</b>: a rocket
    /// already at the pyro's feet is UNDER the beam and cannot be blasted at all.
    /// The three constants come off the running exe — the poof spawns 25px along the
    /// aim, its AUTO mask reads left 8 / right 96 / top −13 / bottom 14, and
    /// <c>collision_circle</c> adds 5.
    /// <para>
    /// Every one of these four points is inside a 150px circle, so a radius test
    /// answers "yes" to all four while the beam answers "yes" to only one.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBlastVolumeIsABeamWithANearEdge()
    {
        // Aiming straight right: forward is (1, 0), perpendicular (0, 1).
        const double Fx = 1;
        const double Fy = 0;
        const double Sx = 0;
        const double Sy = 1;

        static bool At(double dx, double dy) =>
            BotReflex.InBlastBeam(0, 0, dx, dy, Fx, Fy, Sx, Sy);

        // In the beam: comfortably along the aim and on the line.
        Assert.True(At(80, 0));

        // At the pyro's own feet — under the beam, not in it.
        Assert.False(At(10, 0));
        Assert.False(At(BotConstants.AirblastNear - 1, 0));
        Assert.True(At(BotConstants.AirblastNear, 0));

        // Past the far edge.
        Assert.True(At(BotConstants.AirblastReach, 0));
        Assert.False(At(BotConstants.AirblastReach + 1, 0));

        // Off-axis: 80px out and 40px to the side is well inside a 150px circle
        // and nowhere near the beam.
        Assert.False(At(80, 40));
        Assert.True(At(80, BotConstants.AirblastHalfw));
        Assert.False(At(80, BotConstants.AirblastHalfw + 1));

        // Symmetric about the aim line, because the offset is compared unsigned.
        // The real mask is very slightly not (−13/+14); the smaller half-width is
        // the conservative reading, and declining a blast costs nothing.
        Assert.Equal(At(80, BotConstants.AirblastHalfw), At(80, -BotConstants.AirblastHalfw));

        // And BEHIND the pyro is never in it, at any distance — the forward
        // projection is signed.
        Assert.False(At(-80, 0));
    }

    /// <summary>
    /// A pyro blasts a rocket in the beam and ignores one beside it.
    /// </summary>
    /// <remarks>
    /// <b>The press is not free.</b> It sets <c>readyToBlast</c> false for 40
    /// ticks whether or not anything was there, and a rocket 150px out is about 12
    /// ticks from impact — so a wasted blast is still on cooldown when the thing it
    /// was meant for arrives. That is why the volume has to be the weapon's own and
    /// not a generous circle.
    /// </remarks>
    [Fact]
    public void APyroBlastsWhatIsInTheBeamAndIgnoresWhatIsBesideIt()
    {
        var world = Load();
        var pyro = world.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

        Aim(world, pyro, 0);

        // 80px along the aim, dead on the line.
        Rocket(world, StandX + 80, StandY, -12, 0);

        Assert.True(BotReflex.IncomingProjectile(View(world), pyro, BotConstants.AirblastMinTravel));

        // The control: the same rocket 40px off the aim line. Inside a 150px
        // circle, outside the beam.
        var beside = Load();
        var besidePyro = beside.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

        Aim(beside, besidePyro, 0);
        Rocket(beside, StandX + 80, StandY + 40, -12, 0);

        Assert.False(
            BotReflex.IncomingProjectile(View(beside), besidePyro, BotConstants.AirblastMinTravel),
            "a rocket 40px off the aim line was blasted at");
    }

    /// <summary>
    /// A rocket that has just left the barrel is not reflected.
    /// </summary>
    /// <remarks>
    /// <b>The airblast has no delay of any kind</b> — it is re-evaluated every
    /// tick and the bit is returned the frame a rocket appears — so without this a
    /// rocket fired point-blank would come back on the frame it was born.
    /// <para>
    /// <b>It is a DISTANCE rather than an age counter</b>, twice over: it says
    /// "do not reflect a rocket the instant it leaves the barrel" directly rather
    /// than by proxy, and it is frame-rate independent. 60px is about five ticks of
    /// rocket travel.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketFreshFromTheBarrelIsNotReflected()
    {
        var world = Load();
        var pyro = world.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

        Aim(world, pyro, 0);

        // In the beam, but only 10px from where it was fired.
        Rocket(world, StandX + 80, StandY, -12, 0, travelled: 10);

        Assert.False(BotReflex.IncomingProjectile(View(world), pyro, BotConstants.AirblastMinTravel));

        // The control: the identical rocket, further from its shooter.
        var flown = Load();
        var flownPyro = flown.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

        Aim(flown, flownPyro, 0);
        Rocket(flown, StandX + 80, StandY, -12, 0, travelled: BotConstants.AirblastMinTravel);

        Assert.True(BotReflex.IncomingProjectile(View(flown), flownPyro, BotConstants.AirblastMinTravel));
    }

    /// <summary>
    /// A team-mate's rocket, and an unreflectable one, are both left alone.
    /// </summary>
    /// <remarks>
    /// The reflectable set is Rocket, Flare and Mine — <c>Flamethrower</c>'s own
    /// <c>User Event 2</c>. A flame or a bullet is not on it, so a pyro that
    /// pressed SPECIAL at one would spend 40 ticks of cooldown on nothing.
    /// </remarks>
    [Fact]
    public void OnlyAnEnemyReflectableCounts()
    {
        var friendly = Load();
        var a = friendly.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

        Aim(friendly, a, 0);
        Rocket(friendly, StandX + 80, StandY, -12, 0, team: Team.Red);

        Assert.False(
            BotReflex.IncomingProjectile(View(friendly), a, BotConstants.AirblastMinTravel),
            "a pyro tried to blast its own team's rocket");

        // The same place, an unreflectable kind.
        var pellet = Load();
        var b = pellet.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

        Aim(pellet, b, 0);
        Rocket(pellet, StandX + 80, StandY, -12, 0, kind: ProjectileKind.Shot);

        Assert.False(BotReflex.IncomingProjectile(View(pellet), b, BotConstants.AirblastMinTravel));

        // The control: a mine in the same place IS reflectable.
        var mine = Load();
        var c = mine.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

        Aim(mine, c, 0);
        Rocket(mine, StandX + 80, StandY, -12, 0, kind: ProjectileKind.Mine);

        Assert.True(BotReflex.IncomingProjectile(View(mine), c, BotConstants.AirblastMinTravel));
    }

    // ------------------------------------------------------------------
    // The dodge
    // ------------------------------------------------------------------

    /// <summary>
    /// A rocket at the chest is dodged; one over the head is not.
    /// </summary>
    /// <remarks>
    /// A rocket passing above is exactly the case where rising 57px
    /// is what CREATES the collision, so a bot that jumps at anything nearby is a
    /// guaranteed hit for anyone who aims high.
    /// <para>
    /// Both rockets are the same distance away and travelling at the same speed,
    /// so a proximity test answers identically for both — which is exactly what a
    /// proximity test cannot do.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketAtTheChestIsDodgedAndOneOverTheHeadIsNot()
    {
        var chest = Load();
        var a = chest.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        // Coming in level with the origin, from 120px right.
        Rocket(chest, StandX + 120, StandY, -12, 0);

        Assert.True(BotReflex.RocketDodge(View(chest), a), "a rocket at the chest was not dodged");

        // The same rocket 60px higher: it misses a standing bot entirely, and a
        // jumping one rises into it.
        var over = Load();
        var b = over.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        Rocket(over, StandX + 120, StandY - 60, -12, 0);

        Assert.False(BotReflex.RocketDodge(View(over), b), "the bot jumped into a rocket over its head");
    }

    /// <summary>
    /// A rocket that hits either way is not dodged.
    /// </summary>
    /// <remarks>
    /// <b>The other half of the verdict, and it is easy to leave ungated.</b>
    /// "The standing bot is hit at some tick AND the jumping bot is hit at none" — a
    /// test built only from a rocket at the chest and one over the head never
    /// exercises the second clause, because the over-the-head one misses the standing
    /// bot too and so fails the FIRST. Checked by mutation: dropping
    /// <c>!hitJumping</c> entirely left both of those green.
    /// <para>
    /// The fixture is a rocket arriving on the very next tick at chest height. The
    /// standing body box spans −21 to +31 about the origin and one tick of jump
    /// rises 8px, so the jumping box spans −29 to +23 — and a rocket at +10 is inside
    /// both. Jumping buys nothing, so the bot should not.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketThatHitsEitherWayIsNotDodged()
    {
        var world = Load();
        var slot = world.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        // One tick out, and low enough that 8px of rise does not clear it.
        Rocket(world, StandX + 12, StandY + 10, -12, 0);

        Assert.False(
            BotReflex.RocketDodge(View(world), slot),
            "the bot jumped at a rocket that hits it either way");

        // The control — the SAME rocket one tick further out, where the
        // jump has had two ticks to rise and clears it. Without it this passes on a
        // dodge that never fires.
        var clears = Load();
        var clearsSlot = clears.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        Rocket(clears, StandX + 120, StandY + 25, -12, 0);

        Assert.True(
            BotReflex.RocketDodge(View(clears), clearsSlot),
            "a rocket at the shins was not dodged");
    }

    /// <summary>
    /// A rocket that misses anyway is not dodged.
    /// </summary>
    /// <remarks>
    /// The verdict is "the standing bot is hit at some tick AND the jumping bot is
    /// hit at none" — so a shot that was never going to connect fails the first half
    /// and the bot stands still. Without that a bot hops at every rocket in the
    /// air, which is both wasted and, on a ledge, dangerous.
    /// </remarks>
    [Fact]
    public void ARocketThatMissesAnywayIsNotDodged()
    {
        var world = Load();
        var slot = world.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        // Level, in range — and travelling AWAY.
        Rocket(world, StandX + 120, StandY, +12, 0);

        Assert.False(BotReflex.RocketDodge(View(world), slot), "the bot dodged a rocket going the other way");

        // Well below its feet, coming in level: never in the body box.
        var low = Load();
        var lowSlot = low.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        Rocket(low, StandX + 120, StandY + 200, -12, 0);

        Assert.False(BotReflex.RocketDodge(View(low), lowSlot));

        // The control: the same geometry as the first, aimed at the chest, IS
        // dodged. Without it this passes on a dodge that never fires at all.
        var real = Load();
        var realSlot = real.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        Rocket(real, StandX + 120, StandY, -12, 0);

        Assert.True(BotReflex.RocketDodge(View(real), realSlot));
    }

    /// <summary>
    /// A team-mate's rocket is not dodged, and neither is one out of range.
    /// </summary>
    [Fact]
    public void OnlyAnEnemyRocketInRangeIsDodged()
    {
        var friendly = Load();
        var a = friendly.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        Rocket(friendly, StandX + 120, StandY, -12, 0, team: Team.Red);

        Assert.False(BotReflex.RocketDodge(View(friendly), a));

        // Beyond BOT_DODGE_RANGE. It would connect eventually, but not inside
        // BOT_DODGE_TICKS, and reacting now is reacting to nothing.
        var far = Load();
        var b = far.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        Rocket(far, StandX + BotConstants.DodgeRange + 50, StandY, -12, 0);

        Assert.False(BotReflex.RocketDodge(View(far), b));
    }

    // ------------------------------------------------------------------
    // The wiring
    // ------------------------------------------------------------------

    /// <summary>
    /// A driven bot presses JUMP at an incoming rocket, and only on the ground.
    /// </summary>
    /// <remarks>
    /// <b>Grounded only, which is what keeps it out of every planned jump arc</b>
    /// without needing to know anything about that arc. And ORing is safe because
    /// JUMP is edge-triggered in Begin Step: a press cannot double a jump the
    /// follower already wants.
    /// <para>
    /// This is the ONLY thing that presses JUMP outside the path follower.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADrivenBotJumpsAtAnIncomingRocketAndOnlyOnTheGround()
    {
        var world = Load();
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            AliveLast = true,
        };

        var slot = world.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);
        world.Drive(slot, bot);

        // Let it settle on the ground first.
        world.Step(30);
        Assert.True(world.State.Characters[slot].OnGround, "the bot never landed");

        Rocket(world, StandX + 120, StandY, -12, 0);

        var input = bot.Poll(View(world), slot, 31);

        Assert.True((input.Keys & PlayerKeys.Jump) != 0, "the bot did not dodge");

        // The control: airborne, the same rocket presses nothing. Without it this
        // passes on a dodge with no ground gate, which would fire in the middle of a
        // validated arc.
        world.State.Characters[slot].OnGround = false;

        var airborne = bot.Poll(View(world), slot, 32);

        Assert.Equal(PlayerKeys.None, airborne.Keys & PlayerKeys.Jump);
    }

    /// <summary>
    /// A driven pyro with NOTHING to shoot at still blasts a rocket in its beam.
    /// </summary>
    /// <remarks>
    /// A reflect is a reaction to something already in the air and has nothing to do
    /// with having a target — so the combat half must not return no keys at all just
    /// because there is nothing to aim at, or the class policy never runs and a
    /// pyro with an empty screen cannot deflect a rocket.
    /// <para>
    /// `botCombatUpdate`'s own no-subject branch calls `botClassKeys`
    /// anyway — "nothing to aim at is a state a class can still have an opinion
    /// about: a spy re-cloaks, an engineer builds, a heavy eats" — and only the
    /// aiming is skipped. Every branch that would otherwise fire at empty air now
    /// carries its own subject test, which is where the reference puts them.
    /// </para>
    /// <para>
    /// The airblast is on SPECIAL, which for a pyro is not part of firing,
    /// so the combat half's fire suppression leaves it alone. A reflect a second and
    /// a half late is not a reflect.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADrivenPyroWithNoTargetStillBlastsARocketInItsBeam()
    {
        var world = Load();
        var bot = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            AliveLast = true,
        };

        var pyro = world.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);
        world.Drive(pyro, bot);

        // Long enough for the blast to come off its own cooldown. A freshly
        // spawned weapon starts NOT ready — the same way ReadyToShoot does — so a
        // shorter settle asserts nothing and looks exactly like a broken reflex.
        world.Step(60);

        Assert.True(world.State.Weapons[pyro].ReadyToBlast, "the blast never came off cooldown");

        // Nothing on the map but the pyro.
        Assert.False(bot.Combat.Target.Exists, "something got acquired, so this is not the no-target case");

        // Aiming right along the floor, and a rocket 80px that way.
        Aim(world, pyro, 0);

        var quiet = bot.Poll(View(world), pyro, 61);
        Assert.Equal(PlayerKeys.None, quiet.Keys & PlayerKeys.Special);

        Rocket(world, StandX + 80, StandY, -12, 0);

        var input = bot.Poll(View(world), pyro, 62);

        output.WriteLine($"aim {world.State.Characters[pyro].AimDirection:0.#}, keys {input.Keys}");

        Assert.True((input.Keys & PlayerKeys.Special) != 0, "the pyro did not airblast");

        // ...and it does NOT hold the flame trigger. With no subject the
        // distance is 0, which is inside the flame's reach — so without a subject
        // test on that branch a pyro with an empty screen burns its tank for ever.
        Assert.Equal(PlayerKeys.None, input.Keys & PlayerKeys.Attack);
    }

    /// <summary>
    /// A pyro with an empty or reloading tank does not press at all.
    /// </summary>
    /// <remarks>
    /// <c>readyToBlast</c> and the ammo are checked FIRST and are not
    /// decoration. The blast is on a 40-tick cooldown and the reflect test runs only
    /// on the frame of the press — so the cost of a press that cannot reflect is not
    /// the ammo, it is that the next rocket, the one that was going to land, arrives
    /// while the weapon is still reloading. They are also much the cheapest of the
    /// three tests and gate the beam scan regardless of geometry.
    /// </remarks>
    [Fact]
    public void APyroWithNoBlastReadyDoesNotPress()
    {
        static PlayerKeys Keys(double ammo, bool ready)
        {
            var world = Load();
            var pyro = world.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

            Aim(world, pyro, 0);
            Rocket(world, StandX + 80, StandY, -12, 0);

            world.State.Weapons[pyro].AmmoCount = ammo;
            world.State.Weapons[pyro].ReadyToBlast = ready;

            return BotClassKeys.For(new BotCombatState(), View(world), pyro, 0, 0);
        }

        // The control first: loaded and off cooldown, it presses.
        Assert.True((Keys(200, ready: true) & PlayerKeys.Special) != 0);

        Assert.Equal(PlayerKeys.None, Keys(200, ready: false) & PlayerKeys.Special);
        Assert.Equal(PlayerKeys.None, Keys(39, ready: true) & PlayerKeys.Special);
        Assert.True((Keys(40, ready: true) & PlayerKeys.Special) != 0);
    }

    // ------------------------------------------------------------------
    // Difficulty
    // ------------------------------------------------------------------

    /// <summary>
    /// The reflex gets slower and less reliable down the tiers, and the top tier
    /// is the perfect one.
    /// </summary>
    /// <remarks>
    /// Tier 5 at zero and zero is not decoration: every pinned bot measurement
    /// runs at tier 5, and a nonzero miss there would move all of them.
    /// </remarks>
    [Fact]
    public void ReflexesWeakenDownTheTiers()
    {
        var top = BotSkill.ForTier(5);

        Assert.Equal(0, top.ReflexTicks);
        Assert.Equal(0, top.ReflexMissChance);

        for (var tier = BotConstants.TierMin; tier < BotConstants.TierMax; tier++)
        {
            var lower = BotSkill.ForTier(tier);
            var upper = BotSkill.ForTier(tier + 1);

            Assert.True(lower.ReflexTicks > upper.ReflexTicks, $"tier {tier} reacts no slower than {tier + 1}");
            Assert.True(lower.ReflexMissChance > upper.ReflexMissChance, $"tier {tier} misses no more than {tier + 1}");
        }
    }

    /// <summary>
    /// A rocket fired close is too quick for a slow bot, and one fired from far
    /// off is not.
    /// </summary>
    [Fact]
    public void ASlowBotDoesNotReactToAPointBlankRocket()
    {
        var reflex = BotSkill.ForTier(1).ReflexTicks;

        bool Dodges(double travelled)
        {
            var world = Load();
            var slot = world.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

            Rocket(world, StandX + 120, StandY, -12, 0, travelled: travelled);

            return BotReflex.RocketDodge(View(world), slot, reflex);
        }

        bool Blasts(double travelled)
        {
            var world = Load();
            var pyro = world.Place(PlayerClass.Pyro, Team.Red, StandX, StandY);

            Aim(world, pyro, 0);
            Rocket(world, StandX + 80, StandY, -12, 0, travelled: travelled);

            return BotReflex.IncomingProjectile(View(world), pyro, BotConstants.AirblastMinTravel, reflex);
        }

        // 12px a tick: one tick short of the reflex is unseen, the full reflex is seen.
        Assert.False(Dodges((reflex - 1) * 12));
        Assert.True(Dodges(reflex * 12));
        Assert.False(Blasts((reflex - 1) * 12));
        Assert.True(Blasts(reflex * 12));

        // The control: a perfect reflex takes the same point-blank rocket.
        var control = Load();
        var controlSlot = control.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        Rocket(control, StandX + 120, StandY, -12, 0, travelled: 0);

        Assert.True(BotReflex.RocketDodge(View(control), controlSlot));
    }

    /// <summary>
    /// The miss is decided once per rocket, at the rate the tier asks for.
    /// </summary>
    /// <remarks>
    /// <b>Once per rocket is the property that matters.</b> A miss re-rolled each
    /// tick would almost never survive a whole flight, so asking the same rocket
    /// twice has to give the same answer — and over many rockets the rate has to
    /// land near the chance, or the knob does nothing.
    /// </remarks>
    [Fact]
    public void AMissIsDecidedOncePerRocketAtTheTiersRate()
    {
        var chance = BotSkill.ForTier(1).ReflexMissChance;
        var world = Load();
        var slot = world.Place(PlayerClass.Soldier, Team.Red, StandX, StandY);

        const int Rockets = 2000;
        var dodged = 0;

        for (var i = 0; i < Rockets; i++)
        {
            var handle = Rocket(world, StandX + 120, StandY, -12, 0);
            var first = BotReflex.RocketDodge(View(world), slot, 0, chance);

            Assert.Equal(first, BotReflex.RocketDodge(View(world), slot, 0, chance));

            if (first)
                dodged++;

            world.State.FreeProjectile(handle);
        }

        var missed = 1 - ((double)dodged / Rockets);

        output.WriteLine($"missed {missed:F3} of {Rockets} against a chance of {chance:F3}");
        Assert.InRange(missed, chance - 0.05, chance + 0.05);

        // And a certain miss sees nothing.
        Rocket(world, StandX + 120, StandY, -12, 0);
        Assert.False(BotReflex.RocketDodge(View(world), slot, 0, 1));
    }
}
