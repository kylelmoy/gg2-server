using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Capture the flag: phase 6's objective.
/// </summary>
/// <remarks>
/// <c>IntelFixtureTests</c> proves the grab against four recordings of the real
/// server, which is the claim that matters — but a recording can only show what
/// its bots happened to do, and between them the six fixtures contain no drop,
/// no return and one capture that no replay can reach. These pin the rest, and
/// they pin the conditions a bot walking into a briefcase never exercises: the
/// team-gate refusal, the cooldown, the uber, and every edge of the box.
/// <para>
/// Transcribed from <c>Scripts/Events/doEvent{Grab,Drop,Return,Score}Intel.gml</c>,
/// the four <c>Character.events/Collision with Intelligence*.xml</c>, and
/// <c>Character.events/Begin Step.xml:303-369</c>.
/// </para>
/// </remarks>
public sealed class IntelTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Open ground, the same spot <c>DeathTests</c> uses.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary>Inside a red team gate on <c>ctf_truefort</c>, found by search below.</summary>
    private static LevelMap Level() => LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

    private static CollisionWorld World() => new(Level());

    private static void PutScout(
        ref WorldState state,
        int slot,
        Team team,
        double x = OpenX,
        double y = OpenY)
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

    /// <summary>
    /// A world with both briefcases placed and the blue one moved next to a red
    /// scout in slot 1.
    /// </summary>
    /// <remarks>
    /// The flag is moved to the character rather than the other way round so the
    /// tests keep <c>DeathTests</c>' known-clear ground: the real bases sit on
    /// map geometry, and a character dropped there falls through the stair
    /// descent and the gates before anything here has run.
    /// </remarks>
    private static WorldState WorldWithFlagOn(int slot, Team carrierTeam = Team.Red)
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());
        PutScout(ref state, slot, carrierTeam);

        ref var flag = ref Intel.Of(ref state, carrierTeam == Team.Red ? Team.Blue : Team.Red);
        flag.X = OpenX;
        flag.Y = OpenY;

        return state;
    }

    private static TickEvents Collide(CollisionWorld world, ref WorldState state)
    {
        var events = default(TickEvents);
        Intel.CollisionStep(world, ref state, ref events);
        return events;
    }

    private static TickEvents Tick(CollisionWorld world, ref WorldState state)
    {
        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);
        return events;
    }

    // --- placing --------------------------------------------------------------

    /// <summary>
    /// A CTF map puts both briefcases on their bases, at rest and grabbable.
    /// </summary>
    /// <remarks>
    /// <c>ctf_truefort</c>'s two <c>redintel</c>/<c>blueintel</c> entities, which
    /// in the reference are <c>IntelligenceBase{Red,Blue}</c> and each create
    /// their own <c>Intelligence*</c> at the same coordinates.
    /// </remarks>
    [Fact]
    public void BothBriefcasesStartOnTheirBases()
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        Assert.True(state.RedIntel.HasBase);
        Assert.True(state.RedIntel.OnField);
        Assert.Equal(state.RedIntel.HomeX, state.RedIntel.X);
        Assert.Equal(state.RedIntel.HomeY, state.RedIntel.Y);
        Assert.Equal(Timers.Inactive, state.RedIntel.RecoverTimer);
        Assert.True(state.RedIntel.Grabbable);

        Assert.True(state.BlueIntel.HasBase);
        Assert.True(state.BlueIntel.Grabbable);
        Assert.NotEqual(state.RedIntel.HomeX, state.BlueIntel.HomeX);
    }

    /// <summary>
    /// A map with no intel leaves both inert, and nothing here throws on it.
    /// </summary>
    /// <remarks>
    /// <c>doEventReturnIntel</c>'s <c>if(!instance_exists(intelBaseObj)) exit;</c>.
    /// <c>arena_lumberyard</c> is an Arena map and places neither.
    /// </remarks>
    [Fact]
    public void AMapWithNoIntelHasNoBriefcases()
    {
        var state = default(WorldState);
        Intel.Place(ref state, LevelMap.FromPng("arena_lumberyard", ReferenceTree.ReadMap("arena_lumberyard")));

        Assert.False(state.RedIntel.HasBase);
        Assert.False(state.RedIntel.OnField);
        Assert.False(state.BlueIntel.HasBase);

        var events = default(TickEvents);
        Intel.Return(ref state, Team.Red, ref events);

        Assert.False(state.RedIntel.OnField);
        Assert.False(events.ReturnedRedIntel);
    }

    // --- grabbing -------------------------------------------------------------

    /// <summary>
    /// An enemy standing on the briefcase takes it, and the instance goes away.
    /// </summary>
    [Fact]
    public void AnEnemyTouchingTheFlagTakesIt()
    {
        var world = World();
        var state = WorldWithFlagOn(1);

        var events = Collide(world, ref state);

        Assert.True(events.GrabbedIntel[1]);
        Assert.True(state.Characters[1].CarryingIntel);
        Assert.False(state.BlueIntel.OnField);
        Assert.Equal(Timers.Inactive, state.BlueIntel.RecoverTimer);
    }

    /// <summary>Standing on your own team's briefcase does nothing.</summary>
    /// <remarks>
    /// <c>if(team == TEAM_BLUE ...)</c> in the red flag's collision event: only
    /// the enemy can take it, and a defender standing on it is what the whole
    /// mode is about.
    /// </remarks>
    [Fact]
    public void ATeamMateCannotTakeItsOwnFlag()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());
        PutScout(ref state, 1, Team.Blue);

        state.BlueIntel.X = OpenX;
        state.BlueIntel.Y = OpenY;

        var events = Collide(world, ref state);

        Assert.False(events.GrabbedIntel[1]);
        Assert.True(state.BlueIntel.OnField);
    }

    /// <summary>An ubered player cannot pick the flag up.</summary>
    /// <remarks>
    /// <c>and !ubered</c>. The counterpart of the ubered carrier being made to
    /// drop it in Begin Step — between them, an invulnerable player can never
    /// hold it.
    /// </remarks>
    [Fact]
    public void AnUberedPlayerCannotTakeIt()
    {
        var world = World();
        var state = WorldWithFlagOn(1);
        state.Characters[1].Ubered = true;

        Assert.False(Collide(world, ref state).GrabbedIntel[1]);
        Assert.True(state.BlueIntel.OnField);
    }

    /// <summary>A cloaked spy cannot pick it up either.</summary>
    [Fact]
    public void ACloakedPlayerCannotTakeIt()
    {
        var world = World();
        var state = WorldWithFlagOn(1);
        state.Characters[1].Cloak = true;

        Assert.False(Collide(world, ref state).GrabbedIntel[1]);
    }

    /// <summary>
    /// A player inside a team gate cannot grab, even their <em>own</em> team's
    /// gate — which is not solid to them.
    /// </summary>
    /// <remarks>
    /// <c>!place_meeting(x, y, TeamGate)</c> is a plain mask overlap and asks
    /// nothing about solidity, so the gate a red player walks straight through
    /// still refuses them the briefcase. That is what stops a defender reaching
    /// out of their own spawn door.
    /// <para>
    /// The gate is found from the map rather than hard-coded so the test says
    /// what it means: any team gate, either team's.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(Team.Red)]
    [InlineData(Team.Blue)]
    public void APlayerStandingInATeamGateCannotGrab(Team gateTeam)
    {
        var world = World();

        var gate = world.Rects.First(r => r.Kind == SolidKind.TeamGate && r.Team == gateTeam);
        var x = (gate.Left + gate.Right) / 2.0;
        var y = (gate.Top + gate.Bottom) / 2.0;

        // The grabber is on the gate's own team, so the gate is not solid to
        // them and place_free would say the spot is clear.
        var state = default(WorldState);
        Intel.Place(ref state, Level());
        PutScout(ref state, 1, gateTeam, x, y);

        ref var flag = ref Intel.Of(ref state, gateTeam == Team.Red ? Team.Blue : Team.Red);
        flag.X = x;
        flag.Y = y;

        Assert.True(world.PlaceFree(state.Characters[1].Box, x, y, state.Characters[1].Filter));
        Assert.False(Collide(world, ref state).GrabbedIntel[1]);
    }

    /// <summary>
    /// A player still on their drop cooldown cannot take it back — unless its own
    /// alarm is about to fire anyway.
    /// </summary>
    /// <remarks>
    /// <c>(canGrabIntel or other.alarm[0] &lt;= 1)</c>. The <c>&lt;= 1</c> arm is
    /// what makes a briefcase at its base always grabbable: a base flag rests at
    /// -1.
    /// </remarks>
    [Theory]
    [InlineData(300, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(0, true)]
    [InlineData(Timers.Inactive, true)]
    public void TheCooldownYieldsToAnExpiringAlarm(int alarm, bool grabs)
    {
        var world = World();
        var state = WorldWithFlagOn(1);

        state.Characters[1].CanGrabIntel = false;
        state.BlueIntel.RecoverTimer = alarm;

        Assert.Equal(grabs, Collide(world, ref state).GrabbedIntel[1]);
    }

    /// <summary>
    /// The carrier inherits the briefcase's remaining return timer, and a flag
    /// taken off its base gives them nothing.
    /// </summary>
    /// <remarks>
    /// <c>intelRecharge = max(0, alarm[0])</c>: the resting -1 becomes 0, so a
    /// player who takes the flag from its base and is killed a tick later leaves
    /// it with a dead alarm. See <see cref="IntelState.RecoverTimer"/>.
    /// </remarks>
    [Theory]
    [InlineData(450, 450)]
    [InlineData(Timers.Inactive, 0)]
    public void TheCarrierInheritsTheReturnTimer(int alarm, double inherited)
    {
        var world = World();
        var state = WorldWithFlagOn(1);
        state.BlueIntel.RecoverTimer = alarm;

        Collide(world, ref state);

        Assert.Equal(inherited, state.Characters[1].IntelRecharge);
    }

    /// <summary>
    /// The briefcase's box: touching by a single pixel is a grab, and one more
    /// pixel away is not.
    /// </summary>
    /// <remarks>
    /// The edge <c>IntelFixtureTests</c>' recorded grabs land on, and the
    /// asymmetry is the point: a scout's box is <c>-6..+6</c> about its origin
    /// and the briefcase's is <c>-9..+10</c>, because the sprite's origin
    /// (9, 12) is not its centre. So the last overlapping offset is +15 to the
    /// right and -16 to the left. Touching counts — <c>place_meeting</c> is true
    /// for boxes sharing a single pixel column, measured in
    /// <see cref="Aabb.Overlaps"/>.
    /// </remarks>
    [Theory]
    [InlineData(15, true)]
    [InlineData(16, false)]
    [InlineData(-16, true)]
    [InlineData(-17, false)]
    public void TheGrabHappensExactlyWhereTheBoxesTouch(int offset, bool grabs)
    {
        var world = World();
        var state = WorldWithFlagOn(1);
        state.BlueIntel.X = OpenX + offset;

        Assert.Equal(grabs, Collide(world, ref state).GrabbedIntel[1]);
    }

    // --- carrying -------------------------------------------------------------

    /// <summary>
    /// The return timer charges fastest standing still and slowest at a scout's
    /// top speed, and stops at <c>INTEL_MAX_TIMER</c>.
    /// </summary>
    /// <remarks>
    /// <c>INTEL_MAX_TIMER/((3 + abs(min(hspeed, 7))/3.5)*30)</c>: a full charge
    /// in three seconds at rest, five at speed 7 and above. The comment beside
    /// it in the reference describes the rate the right way round, unlike the
    /// afterburn falloff's.
    /// </remarks>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(-7, 6)]
    [InlineData(7, 6)]
    [InlineData(20, 6)]
    public void TheReturnTimerChargesFasterWhenStandingStill(double hspeed, double expected)
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());
        PutScout(ref state, 1, Team.Red);

        state.Characters[1].CarryingIntel = true;
        state.Characters[1].HSpeed = hspeed;

        var events = default(TickEvents);
        Intel.BeginStep(ref state, 1, ref events);

        Assert.Equal(expected, state.Characters[1].IntelRecharge, 10);
    }

    /// <summary>The charge stops at 900 and does not run past it.</summary>
    [Fact]
    public void TheReturnTimerIsCappedAtItsMaximum()
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());
        PutScout(ref state, 1, Team.Red);

        state.Characters[1].CarryingIntel = true;
        state.Characters[1].IntelRecharge = 899;

        var events = default(TickEvents);
        Intel.BeginStep(ref state, 1, ref events);

        Assert.Equal(Intel.MaxTimer, state.Characters[1].IntelRecharge);
    }

    /// <summary>An ubered carrier is made to drop it, and that one is broadcast.</summary>
    /// <remarks>
    /// <c>Character.events/Begin Step.xml:366</c>. Reached through the whole
    /// tick rather than by calling <see cref="Intel.BeginStep"/> directly,
    /// because where it sits in the event order is the point.
    /// </remarks>
    [Fact]
    public void AnUberedCarrierDropsIt()
    {
        var world = World();
        var state = WorldWithFlagOn(1);
        state.Characters[1].CarryingIntel = true;
        state.Characters[1].IntelRecharge = 120;
        state.BlueIntel.OnField = false;
        state.Characters[1].Ubered = true;

        var events = Tick(world, ref state);

        Assert.True(events.DroppedIntel[1]);
        Assert.False(state.Characters[1].CarryingIntel);
        Assert.True(state.BlueIntel.OnField);
    }

    // --- dropping -------------------------------------------------------------

    /// <summary>
    /// A drop puts the briefcase where the carrier stood, with the timer they
    /// charged, and starts their ten-second cooldown.
    /// </summary>
    /// <remarks>
    /// <c>alarm[1] = 300</c> and <c>canGrabIntel = false</c>: a carrier cannot
    /// juggle the flag to keep its return alarm from running.
    /// </remarks>
    [Fact]
    public void ADropLeavesTheFlagWhereTheCarrierStood()
    {
        var state = WorldWithFlagOn(1);
        state.Characters[1].CarryingIntel = true;
        state.Characters[1].IntelRecharge = 123.4;
        state.Characters[1].X = 1234.5;
        state.Characters[1].Y = 567.25;
        state.BlueIntel.OnField = false;

        var events = default(TickEvents);
        Intel.Drop(ref state, 1, ref events);

        Assert.True(events.DroppedIntel[1]);
        Assert.True(state.BlueIntel.OnField);
        Assert.Equal(1234.5, state.BlueIntel.X);
        Assert.Equal(567.25, state.BlueIntel.Y);
        Assert.Equal(123, state.BlueIntel.RecoverTimer);
        Assert.False(state.Characters[1].CanGrabIntel);
        Assert.Equal(Intel.GrabCooldown, state.Characters[1].IntelGrabTimer);
    }

    /// <summary>
    /// A carrier who dies drops it, and no <c>DROP_INTEL</c> goes out.
    /// </summary>
    /// <remarks>
    /// <c>sendEventDropIntel</c> is not called from
    /// <c>Character.events/Destroy.xml</c>, on purpose: it would be a nested
    /// event inside the death and the client would process the death first and
    /// then error out dropping a flag for a character it had destroyed. This is
    /// the one drop that is silent, and a port that broadcast it would
    /// disconnect every client.
    /// </remarks>
    [Fact]
    public void ADeadCarrierDropsItSilently()
    {
        var world = World();
        var state = WorldWithFlagOn(1);
        state.Characters[1].CarryingIntel = true;
        state.Characters[1].IntelRecharge = 200;
        state.Characters[1].Hp = 0;
        state.BlueIntel.OnField = false;

        var events = Tick(world, ref state);

        Assert.True(events.Died[1]);
        Assert.False(events.DroppedIntel[1]);
        Assert.True(state.BlueIntel.OnField);

        // 210, not the 200 it was set to: the carrier's Begin Step charges the
        // timer by a standing tick's 10 before its End Step notices the hp. Both
        // are the same tick, and the order between them is the reference's.
        Assert.Equal(210, state.BlueIntel.RecoverTimer);
    }

    /// <summary>
    /// A carrier who disconnects drops it too, rather than taking it out of the
    /// world.
    /// </summary>
    /// <remarks>
    /// <c>Destroy.xml</c>'s own comment names "if the player just leaves" as one
    /// of the ways a carrier's character is destroyed. Without this the flag is
    /// gone for good: nobody holds it and no instance exists to return itself.
    /// </remarks>
    [Fact]
    public void ADepartingCarrierDropsIt()
    {
        var state = WorldWithFlagOn(1);
        state.Characters[1].CarryingIntel = true;
        state.BlueIntel.OnField = false;

        state.RemoveSlot(1);

        Assert.True(state.BlueIntel.OnField);
    }

    // --- returning ------------------------------------------------------------

    /// <summary>
    /// A dropped briefcase goes home the tick its alarm expires, and not before.
    /// </summary>
    [Fact]
    public void ADroppedFlagReturnsWhenItsAlarmExpires()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        state.BlueIntel.X = OpenX;
        state.BlueIntel.Y = OpenY;
        state.BlueIntel.RecoverTimer = 3;

        Assert.False(Tick(world, ref state).ReturnedBlueIntel);
        Assert.False(Tick(world, ref state).ReturnedBlueIntel);

        var events = Tick(world, ref state);

        Assert.True(events.ReturnedBlueIntel);
        Assert.False(events.ReturnedRedIntel);
        Assert.Equal(state.BlueIntel.HomeX, state.BlueIntel.X);
        Assert.Equal(state.BlueIntel.HomeY, state.BlueIntel.Y);
        Assert.Equal(Timers.Inactive, state.BlueIntel.RecoverTimer);
    }

    /// <summary>
    /// A briefcase dropped with a zero alarm never returns itself.
    /// </summary>
    /// <remarks>
    /// GM8's alarm rule, and it is reachable rather than theoretical: a player
    /// who takes the flag off its base inherits <c>max(0, -1) = 0</c> and drops
    /// it with a 0, which counts straight down to -1 without firing. It lies
    /// there until somebody touches it.
    /// </remarks>
    [Fact]
    public void AFlagDroppedWithAZeroAlarmNeverReturns()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        state.BlueIntel.X = OpenX;
        state.BlueIntel.Y = OpenY;
        state.BlueIntel.RecoverTimer = 0;

        for (var i = 0; i < 10; i++)
            Assert.False(Tick(world, ref state).ReturnedBlueIntel);

        Assert.Equal(OpenX, state.BlueIntel.X);
    }

    // --- capturing ------------------------------------------------------------

    /// <summary>
    /// A carrier reaching their own base scores, and the briefcase reappears on
    /// <em>its</em> base rather than theirs.
    /// </summary>
    /// <remarks>
    /// <c>instance_create(IntelligenceBaseBlue.x, ..., IntelligenceBlue)</c> for
    /// a red capper — the flag goes home, not to the base it was carried to. The
    /// new instance's alarm is GM8's default rest.
    /// </remarks>
    [Fact]
    public void ACaptureScoresAndSendsTheFlagHome()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        PutScout(ref state, 1, Team.Red, state.RedIntel.HomeX, state.RedIntel.HomeY);
        state.Characters[1].CarryingIntel = true;
        state.Characters[1].IntelRecharge = 400;
        state.BlueIntel.OnField = false;

        var events = Collide(world, ref state);

        Assert.True(events.ScoredIntel[1]);
        Assert.Equal(1, state.RedCaps);
        Assert.Equal(0, state.BlueCaps);
        Assert.False(state.Characters[1].CarryingIntel);
        Assert.True(state.BlueIntel.OnField);
        Assert.Equal(state.BlueIntel.HomeX, state.BlueIntel.X);
        Assert.Equal(Timers.Inactive, state.BlueIntel.RecoverTimer);
    }

    /// <summary>
    /// A capture leaves the capper able to take the next one immediately.
    /// </summary>
    /// <remarks>
    /// <c>doEventScoreIntel</c> touches neither <c>canGrabIntel</c> nor
    /// <c>alarm[1]</c>, which is what makes it different from a drop. Nothing in
    /// the reference re-arms them.
    /// </remarks>
    [Fact]
    public void ACaptureDoesNotStartAGrabCooldown()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        PutScout(ref state, 1, Team.Red, state.RedIntel.HomeX, state.RedIntel.HomeY);
        state.Characters[1].CarryingIntel = true;
        state.BlueIntel.OnField = false;

        // A value no code path here would produce, so "unchanged" is a claim
        // rather than a coincidence: a drop would write Intel.GrabCooldown.
        const int Untouched = 42;
        state.Characters[1].IntelGrabTimer = Untouched;

        Collide(world, ref state);

        Assert.True(state.Characters[1].CanGrabIntel);
        Assert.Equal(Untouched, state.Characters[1].IntelGrabTimer);
    }

    /// <summary>Standing on your own base without the flag does nothing.</summary>
    [Fact]
    public void StandingOnYourOwnBaseEmptyHandedScoresNothing()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        PutScout(ref state, 1, Team.Red, state.RedIntel.HomeX, state.RedIntel.HomeY);

        Assert.False(Collide(world, ref state).ScoredIntel[1]);
        Assert.Equal(0, state.RedCaps);
    }

    /// <summary>A blue capper scores for blue.</summary>
    [Fact]
    public void EachTeamScoresIntoItsOwnCounter()
    {
        var world = World();
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        PutScout(ref state, 1, Team.Blue, state.BlueIntel.HomeX, state.BlueIntel.HomeY);
        state.Characters[1].CarryingIntel = true;
        state.RedIntel.OnField = false;

        Collide(world, ref state);

        Assert.Equal(1, state.BlueCaps);
        Assert.Equal(0, state.RedCaps);
        Assert.True(state.RedIntel.OnField);
    }

    // --- the flag as a physics input ------------------------------------------

    /// <summary>
    /// An intel gate is solid to a carrier and open to everybody else.
    /// </summary>
    /// <remarks>
    /// The only way the objective changes a trajectory, and the reason
    /// <see cref="CharacterState.CarryingIntel"/> is on the collision filter:
    /// <c>solid = (team != other.team and other.intel)</c>, so a gate refuses
    /// the enemy's carrier and nobody else — including that team's own carrier,
    /// who has nothing of theirs to take out.
    /// <para>
    /// Built by hand rather than read off a map, because <b>no built-in map
    /// places an intel gate</b> — checked across all 22 in <c>maps.json</c>,
    /// where the only gates are 46 red and 40 blue <em>team</em> gates. The
    /// entity type exists and <c>ColliderSprites</c> handles it, so a custom map
    /// can use one; nothing shipped does.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(Team.Red, Team.Blue, true)]
    [InlineData(Team.Red, Team.Red, false)]
    [InlineData(Team.Any, Team.Blue, true)]
    [InlineData(Team.Any, Team.Red, true)]
    public void AnIntelGateIsSolidOnlyToAnEnemyCarrier(Team gateTeam, Team carrierTeam, bool blocked)
    {
        var gate = SolidRect.FromEntity(100, 100, 1, 1, 6, 60, SolidKind.IntelGate, gateTeam);

        var empty = new CollisionFilter { Team = carrierTeam, CarryingIntel = false };
        var carrying = new CollisionFilter { Team = carrierTeam, CarryingIntel = true };

        Assert.False(gate.IsSolidTo(empty));
        Assert.Equal(blocked, gate.IsSolidTo(carrying));
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// A briefcase on the map serialises as one instance and a carried one as
    /// none.
    /// </summary>
    /// <remarks>
    /// <c>serialize(IntelligenceRed)</c> writes an instance count and the client
    /// destroys and recreates exactly that many, so a carried flag really is
    /// zero rather than a hidden one.
    /// </remarks>
    [Fact]
    public void ACarriedBriefcaseSerialisesAsNoInstance()
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        Assert.NotNull(Intel.ToBlock(state.RedIntel));

        state.RedIntel.OnField = false;
        Assert.Null(Intel.ToBlock(state.RedIntel));
    }

    /// <summary>
    /// The briefcase's position is quantised with GML's own <c>round</c>, half to
    /// even — not the character's plain <c>x*5</c>.
    /// </summary>
    /// <remarks>
    /// <c>Intelligence.events/User Event 10.xml</c> writes
    /// <c>round(x*5)</c> where <c>Character</c>'s writes <c>x*5</c>. At
    /// <c>x = 100.1</c> the product is 500.5 and half-to-even gives 500, where
    /// the wire extension's own half-away-from-zero would give 501. docs/TRAPS-SIMULATION.md's
    /// rounding table, rows one and two.
    /// </remarks>
    [Fact]
    public void TheBriefcaseQuantisesWithGmlsOwnRounding()
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        state.RedIntel.X = 100.1;
        state.RedIntel.Y = 100.3;
        state.RedIntel.RecoverTimer = 450;

        var block = Intel.ToBlock(state.RedIntel);

        Assert.NotNull(block);
        Assert.Equal(500, block!.Value.X);
        Assert.Equal(502, block.Value.Y);
        Assert.Equal(450, block.Value.RecoverTimer);
    }
}
