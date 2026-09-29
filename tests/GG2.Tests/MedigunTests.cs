using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The Medigun, the needle and the uber: phase 5 stage 7.
/// </summary>
/// <remarks>
/// <b>No recording gates most of this, and the parts a recording could gate it
/// cannot gate precisely.</b> A medic's beam reaches the wire as one byte —
/// <c>healTarget</c> — that is written only in a <c>FULL_UPDATE</c>, which goes
/// out on join and therefore never while anybody is alive in any fixture this
/// repo holds. The charge reaches it as <c>uberCharge*255/2000</c>, which
/// saturates. So these tests are the gate, and each was checked by deleting or
/// perturbing the line it is about and watching only its own assertions go red.
/// <para>
/// Transcribed from <c>Objects/Weapons/Medigun.events/{Create, Begin Step, Step,
/// Alarm 5, User Event 1, User Event 2, Destroy}.xml</c>,
/// <c>Objects/Characters/Medic.events/{Create, Alarm 11}.xml</c>,
/// <c>Objects/Projectiles/Needle.events/{Create, Collision with
/// Character}.xml</c>, the <c>with(Needle)</c> block of
/// <c>Scripts/Physics/System/move_all_bullets.gml</c>,
/// <c>Scripts/Misc/calculateHealingFactor.gml</c> and
/// <c>Scripts/Events/{doEventUber, doEventUberReady}.gml</c>.
/// </para>
/// </remarks>
public sealed class MedigunTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position: the floor is directly under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary><c>Medigun.events/Create.xml</c>.</summary>
    private const double HealAmount = 2.0 / 3.0;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    /// <summary>
    /// A character as <c>Character.events/Create.xml</c> leaves one, placed by
    /// hand and armed.
    /// </summary>
    /// <remarks>
    /// Through <see cref="CharacterState.Fresh"/> rather than an object
    /// initialiser, because six of its fields name a roster slot and a zeroed
    /// one silently claims slot 0 — see CLAUDE.md. The weapon is handed a live
    /// <c>readyToShoot</c> so a test does not have to wait out the refire timer
    /// a fresh weapon spawns with.
    /// </remarks>
    private static void Put(
        ref WorldState state,
        int slot,
        PlayerClass playerClass,
        Team team,
        double x,
        double y,
        PlayerKeys keys = PlayerKeys.None,
        double aimDegrees = 0,
        byte aimDistance = 0)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        character.NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0);
        character.AimDistance = aimDistance;
        character.KeyState = keys;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    // --- picking a patient ----------------------------------------------------

    /// <summary>
    /// The beam takes the team-mate under the cursor, not the nearest one.
    /// </summary>
    /// <remarks>
    /// The two score bands do not overlap: anybody within 150 pixels of the
    /// cursor scores in [2, 3] and anybody merely in range scores in [0, 1]. So
    /// a distant team-mate the medic is actually pointing at always beats a
    /// closer one they are not.
    /// </remarks>
    [Fact]
    public void TheBeamTakesTheTeamMateUnderTheCursorAndNotTheNearestOne()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        // Cursor 80 pixels to the right, which is exactly on slot 3.
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 80);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 20, OpenY);
        Put(ref state, 3, PlayerClass.Soldier, Team.Red, OpenX + 80, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(3, state.Weapons[1].HealTarget);
    }

    /// <summary>
    /// With nobody near the cursor the beam falls back to whoever is closest to
    /// the <em>weapon</em>.
    /// </summary>
    [Fact]
    public void WithNobodyUnderTheCursorTheNearestTeamMateWins()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        // aimDistance 0 puts the cursor on the medic itself, so both candidates
        // are outside the 150-pixel mouse radius and score on heal distance.
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 160, OpenY);
        Put(ref state, 3, PlayerClass.Soldier, Team.Red, OpenX + 280, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(2, state.Weapons[1].HealTarget);
    }

    /// <summary>
    /// A tie goes to the candidate examined last.
    /// </summary>
    /// <remarks>
    /// The guard is <c>if(targetScore &lt; bestScore) continue;</c> — strictly
    /// less, so an equal score displaces the incumbent rather than losing to it.
    /// <para>
    /// <b>Which candidate is "last" is an approximation here.</b> The
    /// reference walks <c>with(Character)</c> in instance order, which is
    /// creation order; the port walks roster order, as
    /// <see cref="MatchSimulation"/> does everywhere. The two agree until
    /// somebody respawns. This test pins the comparison operator, which is the
    /// half that is exactly transcribable; the ordering is documented in
    /// <see cref="Healing"/> and cannot be fixed without giving characters a
    /// birth counter.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATieGoesToTheCandidateExaminedLast()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);

        // Two team-mates standing in exactly the same place, which GG2 allows —
        // characters do not collide with each other. Their scores are then
        // bit-for-bit equal and only the operator decides.
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        Put(ref state, 3, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(3, state.Weapons[1].HealTarget);
    }

    /// <summary>An enemy is not a patient, and neither is the medic itself.</summary>
    [Fact]
    public void TheBeamRefusesAnEnemyAndRefusesItself()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);
    }

    /// <summary>
    /// Three hundred pixels is the whole reach, measured from the weapon.
    /// </summary>
    /// <remarks>
    /// The aim ray is the same 300 long, so a team-mate past it fails both
    /// tests — but the range test is the one that rejects them, and it is
    /// checked here at a distance the ray would still have covered had it been
    /// longer.
    /// </remarks>
    [Fact]
    public void TheBeamRefusesSomebodyBeyondThreeHundredPixels()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 255);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 301, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);

        // …and one pixel closer it connects, so the rejection above is the range
        // and not the geometry.
        var reachable = default(WorldState);
        Put(ref reachable, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 255);
        Put(ref reachable, 2, PlayerClass.Soldier, Team.Red, OpenX + 299, OpenY);

        Weapons.FireStep(World, ref reachable, 1, ref events);

        Assert.Equal(2, reachable.Weapons[1].HealTarget);
    }

    /// <summary>A wall between them is a wall.</summary>
    [Fact]
    public void TheBeamWillNotReachThroughTheMap()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        // Straight down, through the floor the medic is standing on.
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDegrees: 270, aimDistance: 60);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX, OpenY + 60);

        // The premise, said out loud: if this stops being true the test below
        // passes for the wrong reason.
        Assert.True(World.CollisionLineHitsMask(OpenX, OpenY, OpenX, OpenY + 60));

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);
    }

    /// <summary>
    /// The aim ray leaves the <b>weapon</b> and ends where the
    /// <b>character</b> is pointing.
    /// </summary>
    /// <remarks>
    /// <c>other.x</c> and <c>owner.x</c> in the same expression, four lines
    /// apart: <c>collision_line(other.x, other.y, aimRayEndX, aimRayEndY, ...)</c>
    /// where <c>aimRayEndX = owner.x + lengthdir_x(maxHealDistance, ...)</c>. In
    /// play the two differ by half a pixel and a tick of travel
    /// (<see cref="WeaponState.X"/>), which is far too small to see in a test —
    /// so this pulls them forty pixels apart and picks a patient that only the
    /// correct segment reaches.
    /// <para>
    /// It is the control the obvious tests cannot be: every other test here
    /// spawns the weapon on top of its owner, where using one for the other
    /// changes nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheAimRayLeavesTheWeaponAndEndsWhereTheCharacterPoints()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        // Aiming straight up, so the ray ends 300 above the *character*.
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDegrees: 90);
        state.Weapons[1].X = OpenX + 40;
        state.Weapons[1].Y = OpenY;

        // Forty up and five short of the weapon: the slanted segment from the
        // weapon to the character's aim point crosses this box, and the vertical
        // segment from the character does not.
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 35, OpenY - 40);

        Assert.False(
            World.CollisionLineHitsMask(OpenX + 40, OpenY, OpenX + 35, OpenY - 40),
            "the probe positions are not in open air any more");

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(2, state.Weapons[1].HealTarget);
    }

    /// <summary>
    /// The cursor is rebuilt from the <b>character</b>, even though the ray it
    /// scores against leaves the weapon.
    /// </summary>
    /// <remarks>
    /// <c>mouseX = owner.x + lengthdir_x(owner.aimDistance, owner.aimDirection)</c>
    /// — <c>owner</c>, where the two <c>collision_line</c> calls below it both
    /// start at <c>other.x</c>. The client measured that distance from its own
    /// character, so rebuilding it from anywhere else puts the cursor somewhere
    /// the player never pointed.
    /// <para>
    /// Like <see cref="TheAimRayLeavesTheWeaponAndEndsWhereTheCharacterPoints"/>
    /// this needs the weapon deliberately displaced, and it needs two candidates
    /// — with one, nothing depends on the score at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCursorIsRebuiltFromTheCharacter()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 60);
        state.Weapons[1].X = OpenX + 60;
        state.Weapons[1].Y = OpenY;

        // Slot 2 sits on the cursor as the character measures it; slot 3 sits on
        // the cursor as the weapon would.
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 60, OpenY);
        Put(ref state, 3, PlayerClass.Soldier, Team.Red, OpenX + 120, OpenY);

        Assert.False(
            World.CollisionLineHitsMask(OpenX + 60, OpenY, OpenX + 120, OpenY),
            "the probe positions are not in open air any more");

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(2, state.Weapons[1].HealTarget);
    }

    /// <summary>
    /// An acquisition costs a tick whether or not it finds anybody.
    /// </summary>
    /// <remarks>
    /// <c>readyToShoot = false; alarm[0] = 1;</c> sit outside the
    /// <c>with(Character)</c> loop, so a medic waving at empty air still spends
    /// the attempt — which is what stops the search running every tick.
    /// </remarks>
    [Fact]
    public void AnAcquisitionCostsATickEvenWhenItFindsNobody()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);
        Assert.False(state.Weapons[1].ReadyToShoot);
        Assert.Equal(1, state.Weapons[1].RefireTimer);
    }

    /// <summary>
    /// A medic whose refire is still running does not re-acquire — but keeps
    /// beaming the patient it already has.
    /// </summary>
    [Fact]
    public void TheAcquisitionIsGatedOnReadyToShootAndTheHealingIsNot()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Characters[2].Hp = 100;
        state.Weapons[1].ReadyToShoot = false;

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);
        Assert.Equal(100, state.Characters[2].Hp);

        // Hand it the patient it was not allowed to find, and the beam runs with
        // readyToShoot still false.
        state.Weapons[1].HealTarget = 2;

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(100 + HealAmount, state.Characters[2].Hp);
    }

    // --- healing --------------------------------------------------------------

    /// <summary>Two thirds of a hit point a tick, which is twenty a second.</summary>
    [Fact]
    public void TheBeamHealsTwoThirdsOfAHitPointATick()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Characters[2].Hp = 50;

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(2, state.Weapons[1].HealTarget);
        Assert.Equal(50 + HealAmount, state.Characters[2].Hp);
        Assert.Equal(1, state.Characters[2].Healer);
        Assert.True(state.Weapons[1].HealedThisStep);
    }

    /// <summary>
    /// <c>calculateHealingFactor</c>: flat for five seconds, then a ramp to
    /// triple over the next two and a half.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(149, 1)]
    [InlineData(150, 1)]
    [InlineData(187.5, 1.5)]
    [InlineData(225, 2)]
    [InlineData(226, 3)]
    [InlineData(300, 3)]
    public void TheHealingRampFollowsTimeUnscathed(double timeUnscathed, double expected)
    {
        Assert.Equal(expected, Healing.HealingFactor(timeUnscathed));
    }

    /// <summary>The ramp is applied to the beam, not just computed.</summary>
    [Fact]
    public void APatientLeftAloneIsHealedThreeTimesAsFast()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Characters[2].Hp = 50;
        state.Characters[2].TimeUnscathed = 300;

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(50 + (HealAmount * 3), state.Characters[2].Hp);
    }

    /// <summary>
    /// Above 125 the beam is throttled to three quarters, and the two factors
    /// stack.
    /// </summary>
    /// <remarks>
    /// The tier is tested against the hp the patient had <em>before</em> this
    /// tick's heal, so a patient sitting on exactly 125 gets one more full-rate
    /// tick before it bites.
    /// </remarks>
    [Fact]
    public void AboveOneHundredAndTwentyFiveTheBeamIsThrottled()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Characters[2].Hp = 125;

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(125 + HealAmount, state.Characters[2].Hp);

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(125 + HealAmount + (HealAmount * 0.75), state.Characters[2].Hp);
    }

    /// <summary>The beam does not overfill.</summary>
    [Fact]
    public void TheBeamStopsAtTheClassMaximum()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(160, state.Characters[2].Hp);
    }

    /// <summary>
    /// A patient who already has a medic keeps the one they had.
    /// </summary>
    /// <remarks>
    /// <c>if (healTarget.object.healer == -1)</c> — claimed, not taken. It
    /// decides who gets the assist when the patient makes a kill, so two medics
    /// on one heavy is first come, first served.
    /// </remarks>
    [Fact]
    public void ASecondMedicDoesNotStealTheHealerCredit()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        Put(ref state, 3, PlayerClass.Medic, Team.Red, OpenX + 80, OpenY, PlayerKeys.Attack, aimDegrees: 180, aimDistance: 40);
        state.Characters[2].Hp = 50;

        Weapons.FireStep(World, ref state, 1, ref events);
        Weapons.FireStep(World, ref state, 3, ref events);

        Assert.Equal(2, state.Weapons[1].HealTarget);
        Assert.Equal(2, state.Weapons[3].HealTarget);

        // Both beams healed; only the first one is the healer.
        Assert.Equal(50 + HealAmount + HealAmount, state.Characters[2].Hp);
        Assert.Equal(1, state.Characters[2].Healer);
    }

    /// <summary>The beam puts a fire out, a tick at a time.</summary>
    [Fact]
    public void TheBeamBurnsOffOneTickOfAfterburn()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Characters[2].BurnDuration = 100;

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(99, state.Characters[2].BurnDuration);
    }

    // --- dropping the beam ----------------------------------------------------

    /// <summary>
    /// Releasing the attack key drops the beam, and it is the weapon's Step
    /// event that notices.
    /// </summary>
    /// <remarks>
    /// Nothing in <c>User Event 1</c> can see the key go up, because it is not
    /// reached at all on a tick the key is up. <c>healedThisStep</c> is the
    /// bridge between the two events, and deleting the Step half leaves a medic
    /// beaming somebody across the map forever.
    /// </remarks>
    [Fact]
    public void ReleasingTheAttackKeyDropsTheBeam()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Characters[2].Hp = 50;

        var held = new PlayerInput[8];
        held[1] = new PlayerInput { Keys = PlayerKeys.Attack, AimDistance = 40 };

        MatchSimulation.Tick(World, ref state, held);

        Assert.Equal(2, state.Weapons[1].HealTarget);
        Assert.Equal(1, state.Characters[2].Healer);

        var released = new PlayerInput[8];
        MatchSimulation.Tick(World, ref state, released);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);
        Assert.Equal(CharacterState.Nobody, state.Characters[2].Healer);
    }

    /// <summary>A patient who walks out of range is dropped mid-beam.</summary>
    [Fact]
    public void APatientWhoLeavesRangeIsDropped()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(2, state.Weapons[1].HealTarget);

        state.Characters[2].X = OpenX + 400;
        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);
        Assert.Equal(CharacterState.Nobody, state.Characters[2].Healer);
    }

    /// <summary>
    /// A medic who dies lets go of their patient.
    /// </summary>
    /// <remarks>
    /// <c>Medigun.events/Destroy.xml</c>. Without it the patient keeps naming a
    /// slot whose medic is gone, and <see cref="Deaths"/>'s assist redirect
    /// hands out a credit the reference does not — quietly, and seconds later.
    /// </remarks>
    [Fact]
    public void AMedicWhoDiesReleasesTheirPatient()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(1, state.Characters[2].Healer);

        state.Clear(1);

        Assert.Equal(CharacterState.Nobody, state.Characters[2].Healer);
    }

    // --- the charge -----------------------------------------------------------

    /// <summary>
    /// 2.5 a tick off a hurt patient and 1.75 off a healthy one — and the test
    /// is against the hp <em>after</em> the heal.
    /// </summary>
    [Fact]
    public void TheChargeRateIsDecidedAfterTheHeal()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Characters[2].Hp = 50;

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(2.5, state.Weapons[1].UberCharge);

        // A patient one tick's heal below full is topped up by this tick and so
        // charges at the *healthy* rate, not the hurt one.
        state.Characters[2].Hp = 160 - (HealAmount / 2);

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(2.5 + 1.75, state.Weapons[1].UberCharge);
    }

    /// <summary>
    /// During a setup timer a FULL-HEALTH patient still charges at the hurt rate.
    /// </summary>
    /// <remarks>
    /// <c>Medigun.events/User Event 1.xml:92-101</c>. <c>setup</c> is
    /// <c>instance_exists(ControlPointSetupGate) &amp;&amp; areSetupGatesClosed()</c>,
    /// which here is <c>state.AttackDefend &amp;&amp; state.SetupTimer &gt; 0</c> —
    /// the map entity is called <c>SetupGate</c> and
    /// <c>builder_init.gml:301</c> builds a <c>ControlPointSetupGate</c> from it.
    /// <para>
    /// <b>The clause is the only way to build charge before the gates open.</b>
    /// During setup nobody has been shot yet, so every patient is at full health
    /// and the healthy rate would mean a medic arrives at the first fight with
    /// 30% less meter than the reference gives them.
    /// </para>
    /// <para>
    /// <b>Both halves are required, and each has its own control below.</b> A
    /// fix that keyed on the timer alone would hand the hurt rate to every
    /// gamemode with a setup timer and no gate; one that keyed on the map alone
    /// would never stop giving it. Reachable on <c>ctf_avanti</c> (Invasion) and
    /// on attack/defend control points, both of which <c>Rounds.Start</c> arms.
    /// </para>
    /// </remarks>
    [Fact]
    public void SetupChargesAFullHealthPatientAtTheHurtRate()
    {
        // The control first: the SAME full-health patient outside setup charges
        // at 1.75, so the 2.5 below is the clause and not the geometry.
        Assert.Equal(1.75, ChargeOffAFullHealthPatient(attackDefend: false, setupTimer: 0));

        Assert.Equal(2.5, ChargeOffAFullHealthPatient(attackDefend: true, setupTimer: 90));

        // Each half on its own is NOT enough — the reference's `setup` is a
        // conjunction, and either of these passing at 2.5 would mean a mode with
        // a setup timer and no gate, or a gate map after setup ended, kept the
        // hurt rate for ever.
        Assert.Equal(1.75, ChargeOffAFullHealthPatient(attackDefend: false, setupTimer: 90));
        Assert.Equal(1.75, ChargeOffAFullHealthPatient(attackDefend: true, setupTimer: 0));
    }

    /// <summary>
    /// One tick of beam onto an undamaged patient; returns the charge gained.
    /// </summary>
    private static double ChargeOffAFullHealthPatient(bool attackDefend, int setupTimer)
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        state.AttackDefend = attackDefend;
        state.SetupTimer = setupTimer;

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);

        // Untouched: Put spawns at full health, and the point of the clause is
        // that there is nothing to heal yet.
        Weapons.FireStep(World, ref state, 1, ref events);

        return state.Weapons[1].UberCharge;
    }

    /// <summary>
    /// The meter fills, announces itself once, and then stops accumulating.
    /// </summary>
    /// <remarks>
    /// 800 ticks of beam at the hurt rate, which is the longest chain of correct
    /// behaviour anything in the medic can be asked for. The
    /// <c>UBER_CHARGED</c> flag is the only thing on the wire that names the
    /// tick it happened on — the class byte saturates.
    /// </remarks>
    [Fact]
    public void AFullMeterAnnouncesItselfExactlyOnce()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDistance: 40);
        Put(ref state, 2, PlayerClass.Heavy, Team.Red, OpenX + 40, OpenY);

        // Hurt enough that 800 ticks of beam cannot fill them: 200 hp, healed at
        // most 2 a tick, and damaged back down to 1 every tick.
        state.Characters[2].Hp = 1;

        var inputs = new PlayerInput[8];
        inputs[1] = new PlayerInput { Keys = PlayerKeys.Attack, AimDistance = 40 };

        var events = default(TickEvents);
        var announcements = 0;
        var announcedOn = -1;

        for (var tick = 0; tick < 900; tick++)
        {
            state.Characters[2].Hp = 1;
            MatchSimulation.Tick(World, ref state, inputs, ref events);

            if (!events.UberCharged[1])
                continue;

            announcements++;
            announcedOn = tick;
        }

        Assert.Equal(1, announcements);

        // 2000 / 2.5 = 800 ticks of beam. The first is spent acquiring — the
        // acquisition and the heal are both in User Event 1 and both run — so
        // the meter fills on the 800th tick, which is index 799.
        Assert.Equal(799, announcedOn);
        Assert.Equal(WeaponState.MaxUberCharge, state.Weapons[1].UberCharge);
        Assert.True(state.Weapons[1].UberReady);
    }

    // --- spending it ----------------------------------------------------------

    /// <summary>
    /// Popping the uber needs the attack key still <em>down</em>, not merely
    /// pressed this tick.
    /// </summary>
    /// <remarks>
    /// The uber arm reads <c>owner.keyState</c> where everything around it
    /// reads the down-or-pressed union, so a click that arrives and is released
    /// inside one tick opens the beam and will not pop the charge.
    /// </remarks>
    [Fact]
    public void PoppingTheUberNeedsTheAttackKeyStillDown()
    {
        var pressedOnly = default(WorldState);
        var events = default(TickEvents);
        Put(ref pressedOnly, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Special);
        pressedOnly.Weapons[1].UberReady = true;
        pressedOnly.Weapons[1].AmmoCount = 0;
        pressedOnly.Characters[1].PressedKeys = PlayerKeys.Attack;

        Weapons.SpecialStep(World, ref pressedOnly, 1, ref events);

        Assert.False(pressedOnly.Weapons[1].Ubering);
        Assert.True(pressedOnly.Weapons[1].UberReady);

        var held = default(WorldState);
        Put(ref held, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Special | PlayerKeys.Attack);
        held.Weapons[1].UberReady = true;
        held.Weapons[1].AmmoCount = 0;

        Weapons.SpecialStep(World, ref held, 1, ref events);

        Assert.True(held.Weapons[1].Ubering);
        Assert.False(held.Weapons[1].UberReady);
        Assert.True(events.Ubered[1]);
    }

    /// <summary>
    /// A medic who can fire a needle fires it <em>instead</em> of popping.
    /// </summary>
    /// <remarks>
    /// The uber is the <c>else</c> arm of the needle, so the way to activate one
    /// is to be beaming somebody — or to be out of ammo.
    /// </remarks>
    [Fact]
    public void ANeedleBeatsTheUber()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Special | PlayerKeys.Attack);
        state.Weapons[1].UberReady = true;

        Weapons.SpecialStep(World, ref state, 1, ref events);

        Assert.False(state.Weapons[1].Ubering);
        Assert.True(state.Weapons[1].UberReady);
        Assert.Equal(1, state.LiveProjectileCount);

        // …and with a patient on the beam, the same inputs pop it.
        var beaming = default(WorldState);
        Put(ref beaming, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Special | PlayerKeys.Attack);
        beaming.Weapons[1].UberReady = true;
        beaming.Weapons[1].HealTarget = 2;

        Weapons.SpecialStep(World, ref beaming, 1, ref events);

        Assert.True(beaming.Weapons[1].Ubering);
        Assert.Equal(0, beaming.LiveProjectileCount);
    }

    /// <summary>
    /// The charge drains at 25/3 a tick — and lasts <b>241</b> ticks, not the
    /// 240 the arithmetic looks like.
    /// </summary>
    /// <remarks>
    /// <b>Two hundred and forty subtractions of <c>25/3</c> from 2000 do not
    /// reach zero.</b> They leave <c>7.02e-12</c>, which is seventy times GML's
    /// own 1e-13 comparison fuzz — so <c>if(uberCharge &lt;= 0)</c> is false and
    /// the uber survives into a 241st tick. The reference performs the identical
    /// repeated subtraction in the identical order, so this is not a port
    /// artefact to be rounded away; it is the length of a GG2 ubercharge.
    /// <para>
    /// The same shape as the rocket's tenth fading pass
    /// (<see cref="Projectiles"/>): an accumulation that "should" land on a
    /// boundary and misses it by an amount the fuzz cannot absorb.
    /// </para>
    /// <para>
    /// And the medic is invulnerable throughout without holding a key or having
    /// a patient: <c>Medigun.events/Begin Step.xml</c> ubers its owner
    /// unconditionally.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheUberLastsTwoHundredAndFortyOneTicks()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY);
        state.Weapons[1].Ubering = true;
        state.Weapons[1].UberCharge = WeaponState.MaxUberCharge;

        var idle = new PlayerInput[8];

        for (var tick = 0; tick < 240; tick++)
        {
            MatchSimulation.Tick(World, ref state, idle);
            Assert.True(state.Characters[1].Ubered);
            Assert.True(state.Weapons[1].Ubering);
        }

        // The residue that buys the extra tick, stated so a change to the
        // arithmetic cannot quietly absorb it.
        Assert.InRange(state.Weapons[1].UberCharge, 1e-13, 1e-11);

        MatchSimulation.Tick(World, ref state, idle);

        Assert.False(state.Weapons[1].Ubering);
        Assert.Equal(0, state.Weapons[1].UberCharge);

        // alarm[2] was set to 3 on the last draining tick and Timers.Advance
        // took it to 2 in the same tick, so it fires on the second tick after.
        MatchSimulation.Tick(World, ref state, idle);
        Assert.True(state.Characters[1].Ubered);

        MatchSimulation.Tick(World, ref state, idle);
        Assert.False(state.Characters[1].Ubered);
    }

    /// <summary>The patient shares the uber only while the beam is on them.</summary>
    [Fact]
    public void ThePatientIsUberedByTheBeamAndNotByTheCharge()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX + 40, OpenY);
        state.Weapons[1].Ubering = true;
        state.Weapons[1].UberCharge = WeaponState.MaxUberCharge;

        var beaming = new PlayerInput[8];
        beaming[1] = new PlayerInput { Keys = PlayerKeys.Attack, AimDistance = 40 };

        MatchSimulation.Tick(World, ref state, beaming);

        Assert.Equal(2, state.Weapons[1].HealTarget);
        Assert.True(state.Characters[2].Ubered);

        // Ubering does not charge: the two are the arms of one if/else.
        Assert.Equal(WeaponState.MaxUberCharge - (25.0 / 3.0), state.Weapons[1].UberCharge);

        // Drop the beam and the patient's share lapses three ticks later while
        // the medic's does not.
        // alarm[2] = 3 is decremented to 2 by the same tick's alarm stage, so
        // it reaches zero on the second tick without a beam.
        var idle = new PlayerInput[8];
        MatchSimulation.Tick(World, ref state, idle);
        Assert.True(state.Characters[2].Ubered);

        MatchSimulation.Tick(World, ref state, idle);
        Assert.False(state.Characters[2].Ubered);
        Assert.True(state.Characters[1].Ubered);
    }

    /// <summary>Being ubered refills the magazine and cancels the reload.</summary>
    /// <remarks>
    /// <c>Character.events/Begin Step.xml:389-394</c>, the one branch of that
    /// event's tail that a medic makes reachable.
    /// </remarks>
    [Fact]
    public void AnUberedCharacterHasAFullMagazine()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);
        state.Characters[1].Ubered = true;
        state.Characters[1].UberTimer = 3;
        state.Weapons[1].AmmoCount = 1;
        state.Weapons[1].ReloadTimer = 12;
        state.Characters[1].BurnDuration = 90;
        state.Characters[1].BurnIntensity = 5;
        state.Characters[1].BurnedBy = 2;

        MatchSimulation.Tick(World, ref state, new PlayerInput[8]);

        Assert.Equal(4, state.Weapons[1].AmmoCount);
        Assert.Equal(WeaponState.Inactive, state.Weapons[1].ReloadTimer);
        Assert.Equal(0, state.Characters[1].BurnDuration);
        Assert.Equal(0, state.Characters[1].BurnIntensity);
        Assert.Equal(CharacterState.Nobody, state.Characters[1].BurnedBy);
    }

    // --- the medic's own regeneration -----------------------------------------

    /// <summary>
    /// A medic mends itself once a second, at a rate that depends on how long it
    /// has been left alone.
    /// </summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(209, 3)]
    [InlineData(210, 4)]
    [InlineData(419, 4)]
    [InlineData(420, 5)]
    public void AMedicMendsItselfOnceASecond(double timeUnscathed, double expected)
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY);
        state.Characters[1].Hp = 50;
        state.Characters[1].RegenTimer = 1;

        // Held fixed, because the Step event counts it up and would cross a
        // threshold under a long test.
        state.Characters[1].TimeUnscathed = timeUnscathed;

        Timers.Advance(ref state);

        Assert.Equal(50 + expected, state.Characters[1].Hp);
        Assert.Equal(30, state.Characters[1].RegenTimer);
    }

    /// <summary>Only a medic has alarm 11.</summary>
    [Fact]
    public void NobodyElseRegenerates()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);
        state.Characters[1].Hp = 50;

        for (var tick = 0; tick < 90; tick++)
            MatchSimulation.Tick(World, ref state, new PlayerInput[8]);

        Assert.Equal(50, state.Characters[1].Hp);
    }

    /// <summary>
    /// A medic's regeneration is unclamped, and End Step is what catches it.
    /// </summary>
    [Fact]
    public void RegenerationCannotPushAMedicOverItsMaximum()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY);
        state.Characters[1].Hp = 119;

        for (var tick = 0; tick < 31; tick++)
            MatchSimulation.Tick(World, ref state, new PlayerInput[8]);

        Assert.Equal(120, state.Characters[1].Hp);
    }

    /// <summary>
    /// <c>timeUnscathed</c> counts up in Step, and it had never been counted
    /// before the beam needed it.
    /// </summary>
    [Fact]
    public void TimeUnscathedCountsUpAndIsCapped()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);

        for (var tick = 0; tick < 10; tick++)
            MatchSimulation.Tick(World, ref state, new PlayerInput[8]);

        Assert.Equal(10, state.Characters[1].TimeUnscathed);

        for (var tick = 0; tick < 400; tick++)
            MatchSimulation.Tick(World, ref state, new PlayerInput[8]);

        Assert.Equal(CharacterState.MaxTimeUnscathed, state.Characters[1].TimeUnscathed);
    }

    // --- the needle -----------------------------------------------------------

    /// <summary>
    /// One needle, four pixels below the weapon, at a speed between 7 and 10.
    /// </summary>
    /// <remarks>
    /// The four pixels are <c>yoffset + 1</c>, and <c>yoffset</c> is
    /// <c>MedigunS</c>'s own y origin of 3 — see
    /// <c>Weapon.events/Create.xml</c>'s <c>yoffset += sprite_yoffset</c>.
    /// </remarks>
    [Fact]
    public void TheNeedleLeavesFourPixelsBelowTheWeapon()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Special, aimDegrees: 30);

        Weapons.SpecialStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);

        var needle = Live(state, 0);
        Assert.Equal(ProjectileKind.Needle, needle.Kind);
        Assert.Equal(OpenX, needle.X);
        Assert.Equal(OpenY + 4, needle.Y);
        Assert.Equal(DamageSource.Needlegun, needle.Source);
        Assert.Equal(4, needle.HitDamage);
        Assert.Equal(40, needle.Lifetime);
        // Not 30: aim crosses the wire as one of 65,536 steps, so the
        // reference's own aimDirection is 29.998… and the needle inherits that.
        Assert.Equal(state.Characters[1].AimDirection, needle.ImageAngle);
        Assert.Equal(state.Characters[1].AimDirection, needle.Motion.Direction);

        // No forward nudge: unlike a pellet the needle is created at the muzzle,
        // so a medic against a wall shoots into it.
        Assert.InRange(needle.Motion.Speed, 7, 10);

        Assert.Equal(39, state.Weapons[1].AmmoCount);
        Assert.Equal(3, state.Weapons[1].RefireTimer);
        Assert.Equal(55, state.Weapons[1].ReloadTimer);
        Assert.False(state.Weapons[1].ReadyToShoot);
    }

    /// <summary>A needle inherits the medic's horizontal speed.</summary>
    [Fact]
    public void ANeedleInheritsTheMedicsHorizontalSpeed()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Special);
        state.Characters[1].HSpeed = 5;

        Weapons.SpecialStep(World, ref state, 1, ref events);

        // Fired straight right, so the whole inherited speed is added to a
        // horizontal component in [7, 10).
        Assert.InRange(Live(state, 0).Motion.HSpeed, 12, 15);
    }

    /// <summary>
    /// A needle falls at 0.2 and a pellet at 0.15, and the difference is the
    /// whole distance between their movement blocks.
    /// </summary>
    [Fact]
    public void ANeedleFallsFasterThanAPellet()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY - 200);

        var needle = new ProjectileState
        {
            Kind = ProjectileKind.Needle,
            X = OpenX,
            Y = OpenY - 200,
            OwnerSlot = 1,
            Team = Team.Red,
            Source = DamageSource.Needlegun,
            HitDamage = 4,
            Lifetime = 40,
        };
        needle.Motion.Direction = 0;
        needle.ImageAngle = 0;
        needle.Motion.Speed = 8;

        state.SpawnProjectile(needle);

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0.2, Live(state, 0).Motion.VSpeed);
        Assert.Equal(OpenY - 200 + 0.2, Live(state, 0).Y);
    }

    /// <summary>
    /// A needle deals four, pushes, and dies on the victim — the Shot's event
    /// with a different number on the instance.
    /// </summary>
    [Fact]
    public void ANeedleDealsFourAndIsDestroyed()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);

        var needle = new ProjectileState
        {
            Kind = ProjectileKind.Needle,
            X = OpenX + 40,
            Y = OpenY,
            OwnerSlot = 1,
            Team = Team.Red,
            Source = DamageSource.Needlegun,
            HitDamage = 4,
            Lifetime = 40,
        };
        needle.Motion.Direction = 0;
        needle.ImageAngle = 0;
        needle.Motion.Speed = 8;

        state.SpawnProjectile(needle);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(156, state.Characters[2].Hp);
        Assert.Equal(1, state.Characters[2].LastDamageDealer);
        Assert.Equal(DamageSource.Needlegun, state.Characters[2].LastDamageSource);
        Assert.Equal(0, state.Characters[2].TimeUnscathed);

        // motion_add(shot.direction, shot.speed * 0.03) — the *shot's* heading
        // and speed, not the victim's.
        Assert.Equal(8 * 0.03, state.Characters[2].HSpeed, 12);
    }

    /// <summary>
    /// The Medigun puts its whole magazine back at once, with no guard on it.
    /// </summary>
    /// <remarks>
    /// <c>Medigun.events/Alarm 5.xml</c> is one unguarded line, where the
    /// Scattergun's puts a single round back and re-arms and the Revolver's
    /// refills only if short.
    /// </remarks>
    [Fact]
    public void TheMedigunReloadsAllFortyAtOnce()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY);
        state.Weapons[1].AmmoCount = 3;
        state.Weapons[1].ReloadTimer = 1;

        Timers.Advance(ref state);

        Assert.Equal(40, state.Weapons[1].AmmoCount);

        // Spent, and — unlike the Scattergun's — not re-armed. Zero rather
        // than -1: GM8 does NOT write -1 before running an alarm's body. A
        // fired alarm reads 0 for the rest of that frame and reaches -1 on the
        // next frame's alarm stage. What lets a handler re-arm its own alarm is
        // simply that nothing wipes the value afterwards. See Timers.Advance.
        Assert.Equal(Timers.JustFired, state.Weapons[1].ReloadTimer);
    }

    /// <summary>
    /// A medic firing a needle advances the shared random stream, because the
    /// spread comes off it.
    /// </summary>
    /// <remarks>
    /// The needle's own speed is server-side RNG the port is free about — no
    /// <c>WEAPON_FIRE</c> carries it — but the <em>draw</em> is not free: the
    /// next weapon that does broadcast a seed takes it from the same stream.
    /// </remarks>
    [Fact]
    public void ANeedleDrawsFromTheSharedStream()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Medic, Team.Red, OpenX, OpenY, PlayerKeys.Special);

        var before = state.Random;

        Weapons.SpecialStep(World, ref state, 1, ref events);

        Assert.NotEqual(before, state.Random);
    }
}
