using System.Linq;
using GG2.Maps;
using GG2.Parity;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The ported physics against a recording of the real GM8 server.
/// </summary>
/// <remarks>
/// docs/PARITY.md tier 2, with the real simulation behind the seam. The fixture
/// is thirty seconds of two bots playing <c>ctf_truefort</c> — real spawns,
/// real jumps, real falls — recorded from the reference and replayed here with
/// no game running.
/// </remarks>
public sealed class PhysicsParityTests
{
    private const string BotTrace = "reference-ctf_truefort-bots.trace";

    /// <summary>
    /// Six bots including a medic and a demoman, and — uniquely among the
    /// fixtures — a player who leaves.
    /// </summary>
    private const string CombatTrace = "reference-ctf_truefort-combat.trace";

    /// <summary>
    /// Six bots mid-fight: recorded thirty seconds after the map started, with
    /// the class limits set so only the three classes the port implements can be
    /// picked.
    /// </summary>
    /// <remarks>
    /// The fixture the other two are not. Twenty-one weapon fires, three deaths,
    /// three respawns and an intel grab in 1,495 ticks, against the combat
    /// trace's eight fires in 748 with the first on tick 647.
    /// </remarks>
    private const string HotCombatTrace = "reference-ctf_truefort-hotcombat.trace";

    /// <summary>
    /// The same six bots, but with the map reloaded <em>during</em> the
    /// recording so that every character has a <c>PLAYER_SPAWN</c> anchor.
    /// </summary>
    /// <remarks>
    /// The only combat fixture that can be replayed closed-loop, and the reason
    /// is in <c>docs/PARITY.md</c>: a character that spawns during a recording is
    /// anchored on an exact integer coordinate rather than a quantised broadcast
    /// position, and so are the projectiles it goes on to fire.
    /// </remarks>
    private const string ClosedCombatTrace = "reference-ctf_truefort-closedcombat.trace";

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", BotTrace);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = LevelMap.FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map));
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        return (trace, ticks);
    }

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static DriftReport Replay(
        IReadOnlyList<TraceTick> ticks,
        string map,
        ReplayMode mode,
        double nudgeX = 0,
        double nudgeY = 0,
        bool stopAtFirstBadTick = true)
    {
        var simulation = new PortedSimulation(MapLoader);
        if (nudgeX != 0 || nudgeY != 0)
            simulation.Nudges[0] = new AnchorNudge(nudgeX, nudgeY, 0, 0);

        return new Replayer(simulation) { Compare = Fields.Trajectory | Fields.Hp }
            .Replay(BotTrace, map, ticks, mode, stopAtFirstBadTick);
    }

    /// <summary>
    /// The first tick of this trace on which any character has taken damage.
    /// </summary>
    /// <remarks>
    /// Phase 4 is trajectories, and it implements no combat at all — so this is
    /// the tick from which the recording stops being a statement about the
    /// physics and starts being one about weapons. Derived from the fixture
    /// rather than hard-coded, so a re-recording moves it without a mystery.
    /// </remarks>
    private static int FirstDamagedTick(IReadOnlyList<TraceTick> ticks)
    {
        foreach (var tick in ticks)
        {
            foreach (var slot in tick.Slots)
            {
                // Zero is a dead character's block, not a damaged one.
                if (slot.Observed is { } observed && observed.Hp is not 100 and not 0)
                    return tick.Index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// The ported simulation reproduces the reference <b>exactly</b>,
    /// closed-loop, for the whole recording — combat included.
    /// </summary>
    /// <remarks>
    /// Closed-loop: anchored once, then never told the answer again, so errors
    /// compound instead of being wiped every seventh tick. Both axes are zero —
    /// not "within a wire unit", zero — and so is every hp, across 898 ticks of
    /// two scouts running, jumping, falling, landing and shooting each other.
    /// <para>
    /// This runs to the end of the fixture rather than stopping at the first
    /// damage, because the six blasts in this recording exercise the muzzle
    /// position, the seeded RNG stream, <c>lengthdir</c>'s snap, the rotated
    /// bounding box, the overlap test, the damage and the knockback all at
    /// once. Any one of them wrong shows up here.
    /// </para>
    /// <para>
    /// <b>The anchor is what makes a gate of zero reachable.</b> A replay
    /// anchored from a broadcast position carries a fifth of a pixel of
    /// position and <c>1/8.5</c> px/tick of velocity, so it would begin
    /// somewhere inside a bin rather than on the state the reference was in.
    /// Resolving <c>PLAYER_SPAWN</c> against the map's own spawn points instead
    /// (<see cref="SpawnPoints"/>) starts them on an integer coordinate with both
    /// speeds zero — a state that passed through no wire.
    /// </para>
    /// </remarks>
    [Fact]
    public void ClosedLoopIsExactAcrossTheWholeTrace()
    {
        var (trace, ticks) = Load();
        var damaged = FirstDamagedTick(ticks);

        Assert.True(damaged < ticks.Count, "the fixture has no combat in it at all");

        var report = Replay(ticks, trace.Header.Map, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(report.Comparisons > 100, $"only {report.Comparisons} comparisons");
        Assert.Equal(ticks.Count, report.TicksReplayed);

        Assert.Empty(report.Mismatches);
        Assert.Equal(0, report.MaxXError);
        Assert.Equal(0, report.MaxYError);

        // The recording really does contain the combat this claim is about, so
        // a re-recording without any cannot quietly turn this back into the
        // phase-4 gate.
        Assert.True(damaged > 380, $"combat started at tick {damaged}, earlier than expected");
    }

    /// <summary>
    /// Every death the reference reported, the port produces — same tick, same
    /// killer, same assistant, same cause.
    /// </summary>
    /// <remarks>
    /// <b>The claim <see cref="ClosedLoopIsExactAcrossTheWholeTrace"/> could not
    /// make.</b> A dead character is simply absent from the state updates, and
    /// those arrive every seventh tick — so before this gate existed, a port
    /// that never killed anybody replayed this fixture with zero mismatches:
    /// the replayer cleared the two slots itself at tick 435, four ticks after
    /// the reference had, and nothing noticed. Nothing in a state update names a
    /// killer at all.
    /// <para>
    /// The recording's two deaths are a mutual scattergun kill: slot 2 dies on
    /// tick 431 and slot 3 on 432, each crediting the other, neither with an
    /// assist, both to <c>DAMAGE_SOURCE_SCATTERGUN</c>. Getting the tick right
    /// is a statement about the whole chain — the muzzle, the seed, the spread,
    /// the pellets' flight, the overlap test and the damage — because a pellet
    /// landing one tick early or late moves the death with it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRecordedDeathHappensOnTheTickItWasRecordedFor()
    {
        var (trace, ticks) = Load();

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory | Fields.Hp | Fields.Deaths,
        }.Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.Empty(report.Mismatches);

        // The fixture really does contain deaths, so a re-recording without any
        // cannot quietly turn this into a test of nothing.
        var recorded = ticks
            .SelectMany(t => t.Events)
            .Count(m => m.Type == MessageType.PlayerDeath);

        Assert.Equal(2, recorded);
    }

    /// <summary>
    /// The death gate can fail: a simulation that kills nobody is caught.
    /// </summary>
    /// <remarks>
    /// The control for the test above, and the reason it is worth writing. The
    /// wrapper hides the port's deaths and changes nothing else — so the
    /// position, velocity and hp comparisons all still pass, and the only thing
    /// that reports a divergence is <see cref="Fields.Deaths"/>. That is
    /// precisely the blind spot this stage closed.
    /// </remarks>
    [Fact]
    public void ASimulationThatKillsNobodyFailsTheDeathGateAndNothingElse()
    {
        var (trace, ticks) = Load();

        var report = new Replayer(new NobodyEverDies(new PortedSimulation(MapLoader)))
        {
            Compare = Fields.Trajectory | Fields.Hp | Fields.Deaths,
        }.Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.Equal(2, report.Mismatches.Count);
        Assert.All(report.Mismatches, m => Assert.Equal("death", m.Field));
        Assert.All(report.Mismatches, m => Assert.Equal(1, m.Expected));
        Assert.All(report.Mismatches, m => Assert.Equal(0, m.Actual));
    }

    /// <summary>
    /// A projectile is state the anchor cannot reach, and a mine is the case
    /// where that matters.
    /// </summary>
    /// <remarks>
    /// <b>An open-loop replay resynchronises characters and nothing else.</b>
    /// Every seventh tick a <c>QUICK_UPDATE</c> puts each character back where
    /// the reference has it — but a projectile's position is on the wire only in
    /// a <c>FULL_UPDATE</c> (<c>Character.events/User Event 12.xml:23,48</c>
    /// puts <c>with(currentWeapon) event_user(12)</c> inside the full-update
    /// branch), and a recording contains exactly one of those, on join.
    /// <para>
    /// For a pellet that does not matter: a <c>Shot</c> lives about 35 ticks, so
    /// it is created and gone well inside the error a resync would have
    /// corrected. <b>A mine lives until somebody detonates it</b>, so its error
    /// has no bound — and if the port loses one the reference still has, no
    /// later message can put it back.
    /// </para>
    /// <para>
    /// This test does not assert the residual away; it pins the shape of it, so
    /// that a real weapon regression is still distinguishable from the known
    /// limit. The port genuinely deals most of the recording's damage — turning
    /// the Minegun off takes the unaccounted hp from 89 to 201 and the missed
    /// deaths from one to three — while at the ticks it misses badly it has
    /// <em>nothing in the air at all</em>, which is the signature of lost
    /// projectile state rather than of a shot that missed.
    /// </para>
    /// </remarks>
    [Fact]
    public void AProjectileIsStateTheAnchorCannotReach()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", HotCombatTrace);
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        // Exactly one FULL_UPDATE, and it is the only message in the recording
        // that carries a projectile's position.
        Assert.Equal(1, Count(ticks, MessageType.FullUpdate));
        // A QUICK_UPDATE closes a tick rather than sitting in its events, so it
        // is the tick's Tier — this is the resynchronisation the characters get
        // and the projectiles do not.
        Assert.True(ticks.Count(t => t.Tier == MessageType.QuickUpdate) > 100);

        // Every shot the reference took, the port takes — so what follows is
        // about where projectiles went, not about whether they were created.
        var simulation = new PortedSimulation(MapLoader);
        new Replayer(simulation)
            .Replay(HotCombatTrace, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.Equal(Count(ticks, MessageType.WeaponFire), simulation.SeedsSupplied);
        Assert.Equal(0, simulation.SeedsUnused);
    }

    /// <summary>
    /// A character the replay anchors from the wire starts with "nobody" in the
    /// six fields that name a roster slot — not with slot 0.
    /// </summary>
    /// <remarks>
    /// <b>The trap CONTRIBUTING.md names, from the direction it is easy to miss.</b>
    /// <c>Nobody</c> is <b>255</b>, so a zeroed <see cref="CharacterState"/>
    /// claims that slot 0 last hurt it, assisted in hurting it, set it on fire
    /// and is healing it. Slot 0 is the server's own reserved player and always
    /// exists, so none of that throws — it credits the wrong person, quietly.
    /// <para>
    /// <c>WorldState.Spawn</c> always set them; <c>PortedSimulation.SetCharacter</c>
    /// built a bare <c>new CharacterState()</c> and did not. Both now go through
    /// <see cref="CharacterState.Fresh"/>, so the list cannot drift apart again.
    /// Found on the hot-combat trace, as a <c>PLAYER_DEATH</c> naming an
    /// assistant the reference said was nobody.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWireAnchoredCharacterCreditsNobodyUntilSomebodyHurtsIt()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerClass(1, PlayerClass.Soldier);
        simulation.SetPlayerTeam(1, Team.Blue);

        simulation.SetCharacter(1, Block(PlayerKeys.None));
        Assert.True(simulation.TryGetState(1, out var anchored));

        Assert.Equal(CharacterState.Nobody, anchored.LastDamageDealer);
        Assert.Equal(CharacterState.Nobody, anchored.SecondToLastDamageDealer);
        Assert.Equal(CharacterState.Nobody, anchored.BurnedBy);
        Assert.Equal(CharacterState.Nobody, anchored.Healer);
        Assert.Equal(DamageSource.None, anchored.LastDamageSource);
        Assert.Equal(DamageSource.None, anchored.AfterburnSource);

        // The three that are not zero either. A cloakAlpha of 0 would make every
        // wire-anchored character invisible to the flicker logic.
        Assert.Equal(1, anchored.CloakAlpha);
        Assert.True(anchored.CanGrabIntel);
        Assert.Equal(100, anchored.NutsNBolts);

        // And the consequence, which is what the recording actually showed: a
        // character that dies without anybody having touched it names nobody
        // twice over, rather than handing slot 0 a kill and an assist.
        simulation.SetCharacter(1, Block(PlayerKeys.None) with { Hp = 0 });
        simulation.Tick([default, new PlayerInput { Keys = PlayerKeys.None }]);

        var death = Assert.Single(simulation.DeathsLastTick);
        Assert.Equal(1, death.Slot);
        Assert.Equal(CharacterState.Nobody, death.Killer);
        Assert.Equal(CharacterState.Nobody, death.Assistant);
    }

    /// <summary>
    /// Anchoring a character on the tick a key is first pressed still lets the
    /// press through.
    /// </summary>
    /// <remarks>
    /// <b>A one-tick-in-seven bug, and the sort a bounded fixture hides.</b>
    /// Jump, the demoman's right-click and the spy's cloak all fire on
    /// <c>KeyState &amp; ~LastKeyState</c>. A state update carries <em>this</em>
    /// tick's <c>keys</c> alongside <em>last</em> tick's position, so
    /// <c>PortedSimulation.SetCharacter</c> anchoring <c>LastKeyState</c> from
    /// it meant that a key first pressed on an update tick arrived already
    /// "held" — and the edge that would have fired was computed as empty.
    /// <para>
    /// Found on <c>reference-ctf_truefort-combat.trace</c>, whose demoman
    /// presses Jump for exactly one tick and that tick is an update tick. The
    /// reference climbs 43 px over the next seven ticks; the port climbed 6.
    /// Every other fixture holds its jumps down for several ticks, where the
    /// edge on the first one being lost costs nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnAnchorDoesNotSwallowAFreshKeyPress()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerClass(1, PlayerClass.Scout);
        simulation.SetPlayerTeam(1, Team.Red);

        // Anchor it once with no keys, then let it settle onto the ground.
        simulation.SetCharacter(1, Block(PlayerKeys.None));

        // Two entries, because MatchSimulation only applies an input to a slot
        // inside the span — a one-element array would leave slot 1 with no input
        // at all and this test would pass for the wrong reason.
        PlayerInput[] idle = [default, new PlayerInput { Keys = PlayerKeys.None }];
        PlayerInput[] jumping = [default, new PlayerInput { Keys = PlayerKeys.Jump }];

        for (var tick = 0; tick < 30; tick++)
            simulation.Tick(idle);

        Assert.True(simulation.TryGetState(1, out var settled));
        Assert.Equal(0, settled.VSpeed);

        // Now the case: an update lands on the same tick as the first press, so
        // the anchor and the input both carry Jump.
        Assert.True(simulation.TryGetCharacter(1, out var observed));
        simulation.SetCharacter(1, observed with { Keys = PlayerKeys.Jump });
        simulation.Tick(jumping);

        Assert.True(simulation.TryGetState(1, out var jumped));
        Assert.True(jumped.VSpeed < 0, $"the anchor swallowed the press (vspeed {jumped.VSpeed})");
    }

    /// <summary>A character block at a standing position on <c>ctf_truefort</c>.</summary>
    private static CharacterBlock Block(PlayerKeys keys) => new()
    {
        Keys = keys,
        NetAimDirection = 0,
        AimDistance = 0,
        HasPosition = true,
        X = 2600 * 5,
        Y = 700 * 5,
        HSpeed = 0,
        VSpeed = 0,
        Hp = 100,
        Ammo = 6,
        Flags = 0,
    };

    /// <summary>
    /// Given an exact anchor, the port reproduces a real fight <b>exactly</b> —
    /// trajectories, hp and deaths — for hundreds of ticks.
    /// </summary>
    /// <remarks>
    /// <b>The gate the combat work never had.</b> Every other combat fixture is
    /// open loop and can only assert a bound, because its characters were alive
    /// before the recording started and are therefore anchored on quantised
    /// broadcast positions (<c>docs/PARITY.md</c>). This one was recorded with
    /// the recorder connected <em>first</em> and the map reloaded underneath it,
    /// so all six bots spawn inside the recording and every anchor is an exact
    /// integer coordinate — for the characters and, by construction, for the
    /// projectiles they fire.
    /// <para>
    /// From the end of the spawn wave the replay is exact for <b>335 ticks</b> —
    /// eleven seconds containing <b>nine weapon fires and a death</b> — across
    /// position, velocity, hp and death attribution. The first divergence is at
    /// tick 1253 and is small (2 hp, then the trajectory that follows from it),
    /// which is a real lead rather than an anchoring artefact: with an exact
    /// anchor there is no quantisation left to blame.
    /// </para>
    /// <para>
    /// The ticks before the spawn wave are <em>not</em> asserted. The recorder
    /// joins a match already in progress, so until the reload the characters are
    /// wire-anchored and diverge exactly as the other fixtures do.
    /// </para>
    /// </remarks>
    [Fact]
    public void WithAnExactAnchorThePortReproducesAFightExactly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", ClosedCombatTrace);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        // The end of the spawn wave: the last first-spawn, after which every
        // character the recording still observes is exactly anchored. Shared
        // with the support fixture tests — see FixtureAnchors, and note that
        // this fixture's slot 6 is a pre-reload ghost the rule has to admit.
        var spawns = ticks
            .Where(t => t.Events.Any(m => m.Type == MessageType.PlayerSpawn))
            .Select(t => t.Index)
            .ToArray();

        Assert.True(spawns.Length >= 5, $"only {spawns.Length} spawns; this fixture needs the whole roster");

        var anchorTick = FixtureAnchors.LastExactAnchor(ticks);
        Assert.NotNull(anchorTick);
        var waveEnd = anchorTick.Value;

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory | Fields.Hp | Fields.Deaths,
        }.Replay(ClosedCombatTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > waveEnd).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);
        var exact = firstBad - waveEnd;

        // The window has to contain a real fight, or "exact" means nothing.
        var fires = ticks.Count(t => t.Index > waveEnd && t.Index < firstBad
            && t.Events.Any(m => m.Type == MessageType.WeaponFire));
        var deaths = ticks.Count(t => t.Index > waveEnd && t.Index < firstBad
            && t.Events.Any(m => m.Type == MessageType.PlayerDeath));

        // 357 today, from tick 917 to tick 1274. Asserted *first*, because a
        // regression collapses the window and every other assertion here then
        // fails for the derived reason rather than the real one.
        //
        // This bound is meant to be raised: every fix that pushes the first
        // divergence later should push this with it — that is what stops the
        // fixture decaying into a formality.
        //
        // But do not go hunting for a bug at tick 1274. It is a rounding
        // boundary, not a divergence: the port has slot 1 at 14125.5014 wire
        // units and the reference rounded to 14125, so the two disagree about an
        // integer while being 0.0014 wire units — 0.0003 world pixels — apart,
        // and the replay is clean again for the next four hundred ticks.
        // Measured by SubWireProbe; ThePortStaysWithinAWireUnitOfARealFight
        // below is the metric that does not move when a residual grazes a
        // boundary, and docs/PARITY.md has why both are kept.
        Assert.True(
            exact >= 340,
            $"exact for only {exact} ticks after the spawn wave (t{waveEnd} to t{firstBad})");

        // And the window has to contain a real fight, or "exact" means nothing.
        Assert.True(fires >= 8, $"only {fires} weapon fires in the exact window");
        Assert.True(deaths >= 1, "no deaths in the exact window");
    }

    /// <summary>
    /// The port reproduces a real fight's <b>hp</b> exactly for 1,204 ticks —
    /// three and a half times as long as it reproduces the trajectory.
    /// </summary>
    /// <remarks>
    /// <b>The strongest claim in the project about damage, and it went
    /// unmeasured until the pyro forced the question.</b> Hp survives the
    /// trajectory divergence at t1274 by four hundred ticks and the real one at
    /// t1673 by another four hundred, because neither is large enough to change
    /// what a pellet or a rocket hits. Forty seconds of a six-bot fight, every
    /// scattergun blast, rocket, mine and death, with no disagreement about any
    /// character's hp byte.
    /// <para>
    /// <b>This fixture can carry the claim and the support one cannot, and
    /// the difference is which weapons are in it.</b> Every class here — scout,
    /// soldier, demoman — fires a weapon that broadcasts its seed, so a replay
    /// re-derives the same spread and hits the same people. A pyro does not:
    /// its spread comes off <c>randomize()</c> and is never sent, so no replay
    /// can reproduce which of its flames connect. See
    /// <c>docs/PARITY.md</c>, "an unseeded weapon caps a fixture's hp window".
    /// </para>
    /// <para>
    /// Meant to be raised, like every other bound here — but note it is bounded
    /// by the recording's length as much as by the port: the first hp
    /// disagreement is 120 ticks from the end.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortReproducesAFightsDamageForFarLongerThanItsTrajectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", ClosedCombatTrace);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        // The shared anchor derivation — see FixtureAnchors.
        var anchorTick = FixtureAnchors.LastExactAnchor(ticks);
        Assert.NotNull(anchorTick);
        var waveEnd = anchorTick.Value;

        // Hp alone. Deliberately not `| Fields.Trajectory`: the trajectory ends
        // the window at t1274 on a rounding boundary, which says nothing about
        // whether the damage model is right.
        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.Hp }
            .Replay(ClosedCombatTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > waveEnd).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);
        var exact = firstBad - waveEnd;

        var fires = ticks.Count(t => t.Index > waveEnd && t.Index < firstBad
            && t.Events.Any(m => m.Type == MessageType.WeaponFire));
        var deaths = ticks.Count(t => t.Index > waveEnd && t.Index < firstBad
            && t.Events.Any(m => m.Type == MessageType.PlayerDeath));

        // 1204 today, t917 to t2121. The first disagreement is slot 4 at 72
        // against 80 — one scattergun pellet, four hundred ticks after the
        // trajectory stopped being exact, which is where a pellet starts landing
        // somewhere else.
        Assert.True(
            exact >= 1150,
            $"hp exact for only {exact} ticks after the spawn wave (t{waveEnd} to t{firstBad})");

        // 19 today: ticks that carry a WEAPON_FIRE, not fires — two on one tick
        // count once, and the fixture's 34 are spread either side of the window.
        Assert.True(fires >= 15, $"only {fires} weapon fires in the hp window");
        // Only one — the fixture's five deaths cluster in its first four hundred
        // ticks, before this window opens. The fires are what carry it.
        Assert.True(deaths >= 1, $"only {deaths} deaths in the hp window");
    }

    /// <summary>
    /// Loads a fixture, finds the end of its spawn wave, and replays it
    /// closed-loop behind a <see cref="SubWireProbe"/>.
    /// </summary>
    /// <remarks>
    /// The wave ends at <see cref="FixtureAnchors.LastExactAnchor"/> — the last
    /// first-spawn, after which everything still observed is exactly anchored.
    /// A fixture with no usable anchor has no wave and every tick counts.
    /// </remarks>
    private static (SubWireProbe Probe, DriftReport Report, IReadOnlyList<TraceTick> Ticks, int WaveEnd)
        ProbeClosedLoop(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        // Shared derivation — see FixtureAnchors. A fixture with no usable
        // anchor has no wave and every tick counts.
        var waveEnd = FixtureAnchors.LastExactAnchor(ticks) ?? int.MinValue;

        var probe = new SubWireProbe(new PortedSimulation(MapLoader), ticks);
        var report = new Replayer(probe)
        {
            Compare = Fields.Trajectory | Fields.Hp | Fields.Deaths,
        }.Replay(fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        return (probe, report, ticks, waveEnd);
    }

    /// <summary>
    /// The sub-wire probe, at a tolerance of half a wire unit, reports exactly
    /// the tick the replayer's own integer comparison does.
    /// </summary>
    /// <remarks>
    /// <b>The probe's calibration, and the reason to trust the number it gives
    /// at any other tolerance.</b> An observation is
    /// <c>round(reference * 5)</c>, half away from zero, so the port agrees
    /// about that integer <em>if and only if</em> its own <c>x * 5</c> lies
    /// within half a unit of it. The two metrics are therefore the same
    /// statement at 0.5, and this asserts they say so — which is the assertion
    /// that fails if the probe's tick cursor slips, because
    /// <c>SubWireProbe</c> counts <c>Tick</c> calls to work out which
    /// observation a sample belongs to and the replayer compares <em>before</em>
    /// it advances. Off by one and the window moves by seven.
    /// <para>
    /// Both closed-loop fixtures, because they fail it differently: the bots
    /// trace never diverges at all, so it checks that "no excursion" and "no
    /// mismatch" agree, and the combat trace checks that a real one lands on the
    /// same tick.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(BotTrace)]
    [InlineData(ClosedCombatTrace)]
    public void TheSubWireProbeAgreesWithTheIntegerComparison(string fixture)
    {
        var (probe, report, _, waveEnd) = ProbeClosedLoop(fixture);

        // Position and velocity only: hp and deaths are compared by the replayer
        // and are not positions, so the probe has nothing to say about them.
        var trajectory = report.Mismatches
            .Where(m => m.Tick > waveEnd && m.Field is "x" or "y" or "hspeed" or "vspeed")
            .ToArray();

        var expected = trajectory.Length == 0 ? int.MaxValue : trajectory.Min(m => m.Tick);

        Assert.Equal(expected, probe.FirstOutsideBin(0.5, waveEnd));

        // And it saw something. A probe that sampled nothing would pass the
        // equality above by agreeing that there is no news.
        Assert.True(
            probe.Samples.Count(s => s.Tick > waveEnd) > 100,
            $"only {probe.Samples.Count(s => s.Tick > waveEnd)} sub-wire samples after the spawn wave");
    }

    /// <summary>
    /// Through a real fight, no character is ever more than <b>one wire unit</b>
    /// — a fifth of a world pixel — from where the reference had it, for twice
    /// as long as the exact window lasts.
    /// </summary>
    /// <remarks>
    /// <b>The metric that does not move when a residual grazes a boundary.</b>
    /// <see cref="WithAnExactAnchorThePortReproducesAFightExactly"/> measures
    /// the first tick on which the port and the reference disagree about an
    /// <em>integer</em>, and where that falls is partly luck: an observation is
    /// the reference's own position rounded to a fifth of a pixel, so a port a
    /// ten-thousandth of a unit past the boundary reports a mismatch and the
    /// same port a ten-thousandth the other side reports nothing. That is
    /// literally what happens at tick 1274 — 0.0014 wire units, and then four
    /// hundred more clean ticks.
    /// <para>
    /// So this asks the continuous question instead. The answer is <b>756
    /// ticks</b> — t917 to t1673, containing <b>fourteen weapon fires,
    /// a death and a respawn</b> across five characters and 514 comparisons —
    /// against the exact window's 357.
    /// </para>
    /// <para>
    /// <b>The tolerance is not a fudge factor, and the second assertion is what
    /// proves it.</b> Inside the window the worst any character strays outside
    /// the reference's rounding bin is 0.0014 wire units; the excursion that
    /// ends it is <b>12.7</b>. There is nothing in between — a divergence in
    /// this simulation is a cliff, because a wrong velocity is re-applied every
    /// tick — so no tolerance between a thousandth and a unit would choose a
    /// different tick, and one at a unit is not hiding a slow drift. If that
    /// second assertion ever fails, the tolerance <em>has</em> started hiding
    /// something and the number to look at is the drift, not this bound.
    /// </para>
    /// <para>
    /// It is not strictly stronger than the exact window and does not replace
    /// it: a boundary is the one place an integer comparison is more sensitive
    /// than any tolerance, which is worth keeping — dropping the one-ULP
    /// correction on GM8's <c>0.6</c> literal (<see cref="Gml.PointSix"/>, which
    /// is gravity) collapses the exact window from 357 to 28 and leaves this one
    /// at 756. Reverting the projectile pass to after the character move takes
    /// this one to 336. docs/PARITY.md, "an exact window can end on a rounding
    /// boundary".
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortStaysWithinAWireUnitOfARealFight()
    {
        // One wire unit: a fifth of a world pixel of position, or 1/8.5 px/tick
        // of velocity. Half of that is the rounding bin itself, so this allows
        // half a unit of genuine sub-wire error on top of it.
        const double Tolerance = 1.0;

        var (probe, _, ticks, waveEnd) = ProbeClosedLoop(ClosedCombatTrace);

        var firstReal = probe.FirstOutsideBin(Tolerance, waveEnd);
        var window = firstReal == int.MaxValue ? ticks[^1].Index - waveEnd : firstReal - waveEnd;

        // Asserted first, because a regression collapses the window and then
        // every assertion below it fails for the derived reason.
        //
        // Meant to be raised, like the exact window it sits beside. 756
        // today; reverting the projectile-pass ordering takes it to 336.
        Assert.True(
            window >= 700,
            $"within a wire unit for only {window} ticks after the spawn wave " +
            $"(t{waveEnd} to t{firstReal})");

        // The tolerance is honest: what it lets through is a thousand times
        // smaller than what ends the window.
        var worst = probe.WorstOvershoot(waveEnd, firstReal);
        Assert.True(
            worst < 0.01,
            $"worst excursion inside the window is {worst} wire units, which is a drift rather " +
            "than a rounding boundary — the tolerance is now hiding something");

        if (firstReal != int.MaxValue)
        {
            var ends = probe.Samples.Where(s => s.Tick == firstReal).Max(s => s.Overshoot);
            Assert.True(
                ends > 100 * worst,
                $"the window ends on an excursion of {ends} against {worst} inside it; " +
                "the divergence is no longer a cliff, so the tolerance is doing real work");
        }

        // And the window has to contain a real fight, or the span means nothing.
        int Count(MessageType type) => ticks.Count(t =>
            t.Index > waveEnd && t.Index < firstReal && t.Events.Any(m => m.Type == type));

        Assert.True(Count(MessageType.WeaponFire) >= 12, $"only {Count(MessageType.WeaponFire)} fires");
        Assert.True(Count(MessageType.PlayerDeath) >= 1, "no deaths in the window");

        // Five characters, not one lucky slot standing still.
        var slots = probe.Samples
            .Where(s => s.Tick > waveEnd && s.Tick < firstReal)
            .Select(s => s.Slot)
            .Distinct()
            .Count();

        Assert.True(slots >= 5, $"only {slots} characters compared in the window");
    }

    /// <summary>
    /// The divergence that ends the window is a <b>threshold</b>, not a wrong
    /// rule: most of the states the recording could have been describing
    /// reproduce it exactly.
    /// </summary>
    /// <remarks>
    /// <b>What actually happens at tick 1673, measured.</b> Slot 1 — a soldier
    /// running right into a wall — has <c>x + hspeed == 2117.5087</c> going into
    /// t1667, and the <c>place_free</c> boundary is at exactly
    /// <c>2117.5</c>: the box origin rounds half to even, so 2117.4999 is free
    /// and 2117.5 is not. The port overshoots it by <b>0.0087 world pixels</b>,
    /// is stopped by <c>characterHitObstacle</c>, and has its <c>hspeed</c>
    /// zeroed one tick before the reference does. It therefore turns around a
    /// tick early, and the signature is unmistakable: the reference's
    /// <em>horizontal</em> state at t1673 and t1680 matches the port's at t1672
    /// and t1679 (dx -0.10, dhs -0.04, both far inside a wire unit) while the
    /// <em>vertical</em> is in step. By the second update that lag is worth 26
    /// wire units of x.
    /// <para>
    /// <b>0.0087 px is twenty times finer than the wire</b>, so no observation
    /// between the spawn anchor and t1667 can say which side of it the reference
    /// was on — and this test is how that was established rather than assumed.
    /// Sweeping slot 1's anchor across its own quantisation bin, <b>34 of the
    /// 49 grid points reproduce t1673 exactly</b>, which means
    /// <c>place_free</c>, <c>characterHitObstacle</c>,
    /// <c>good_move_contact_solid</c> and the acceleration loop are all right
    /// and the port is simply on the wrong side of a threshold.
    /// </para>
    /// <para>
    /// <b>So this is not a bug to fix, and the test exists to stop it being
    /// chased again.</b> It is the closed-loop form of the amplifier
    /// docs/PARITY.md already describes for open-loop residuals: a fraction of a
    /// pixel decides a discrete question, and the discrete answer is worth whole
    /// pixels. If a real rule regresses, the reference's behaviour stops being
    /// reachable from anywhere in the bin and the coverage collapses — dropping
    /// the doubled <c>i / MAX_I</c> factor in <c>good_move_contact_solid</c>
    /// takes it from 34/49 to <b>0/49</b> — and moves the divergence to t1218,
    /// where it correctly reports slot 4 instead.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDivergenceThatEndsTheWindowIsAThresholdRatherThanAWrongRule()
    {
        var (probe, _, ticks, waveEnd) = ProbeClosedLoop(ClosedCombatTrace);

        var firstReal = probe.FirstOutsideBin(1.0, waveEnd);
        Assert.True(firstReal != int.MaxValue, "no divergence to explain — raise the window bound instead");

        // Whichever slot actually diverged, so a re-recording or a fix that
        // moves the divergence does not leave this asking about the wrong one.
        var slot = probe.Samples
            .Where(s => s.Tick == firstReal)
            .OrderByDescending(s => s.Overshoot)
            .First()
            .Slot;

        var trace = Trace.Decode(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", ClosedCombatTrace)));

        var sweep = BinSweep.Sweep(MapLoader, ClosedCombatTrace, trace.Header.Map, ticks, slot, firstReal);

        // 34/49 today. A majority, not merely a few lucky corners: the bin is
        // two-dimensional and the threshold cuts it as a diagonal, so a rule
        // that is right leaves most of it on the reproducing side.
        Assert.True(
            sweep.Coverage > 0.5,
            $"only {sweep} of slot {slot}'s quantisation bin reproduces t{firstReal}; " +
            "the reference is doing something the port cannot do from anywhere the wire allows, " +
            "which is a rule error rather than a threshold");
    }

    /// <summary>
    /// The port tracks a real fight to within a bounded drift, and reproduces
    /// what the fight did.
    /// </summary>
    /// <remarks>
    /// <b>Recorded to close a hole the other two fixtures could not.</b> The bot
    /// trace is two scouts and no roster events; the combat trace's first
    /// <c>WEAPON_FIRE</c> of any kind is on tick 647 of 748, so 86% of it is
    /// walking. This one was recorded thirty seconds into a running match with
    /// <c>[Classlimits]</c> set so bots could only pick Scout, Soldier and
    /// Demoman — the three the port implements — and it opens with three of the
    /// six already wounded.
    /// <para>
    /// <b>Open loop, and it has to be.</b> Nobody spawns before the recording
    /// starts, so every character is anchored from a quantised broadcast
    /// position; <c>docs/PARITY.md</c> has why that can never be exact. What it
    /// measures is one seven-tick prediction window at a time, which is what a
    /// client actually experiences.
    /// </para>
    /// <para>
    /// <b>The bound is deliberately loose and the numbers are recorded here so
    /// that an improvement is visible.</b> Max <c>|dx|</c> is 77 and max
    /// <c>|dy|</c> is 75; both p99s are 2 — the maxima are single discrete
    /// events, while the p99s track how well the weapons land. Those maxima are
    /// much worse than the combat trace's p99 of 2, and that is the point of
    /// having it: the residual is combat and
    /// airborne physics, which nothing else exercises. Known leads: a repeating
    /// six-pixel vertical offset, an assist attribution that produces 0 where
    /// the reference says 255, and windows where the port under-damages.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortTracksARealFightWithinABound()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", HotCombatTrace);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        // The fixture contains the fight it claims to. A re-recording that came
        // out quiet would otherwise pass this test by having nothing to get
        // wrong, which is exactly how the combat trace misled a session.
        Assert.True(
            Count(ticks, MessageType.WeaponFire) >= 20,
            $"only {Count(ticks, MessageType.WeaponFire)} weapon fires");
        Assert.True(
            Count(ticks, MessageType.PlayerDeath) >= 3,
            $"only {Count(ticks, MessageType.PlayerDeath)} deaths");
        Assert.True(
            Count(ticks, MessageType.PlayerSpawn) >= 3,
            $"only {Count(ticks, MessageType.PlayerSpawn)} spawns");

        // And it starts hot: somebody is already hurt on the first update.
        var opening = ticks.First(t => t.HasPositions);
        Assert.Contains(
            opening.Slots,
            slot => slot.Observed is { } o && o.Hp > 0 && o.Hp < 100);

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Position,
        }.Replay(HotCombatTrace, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.True(report.Comparisons > 900, $"only {report.Comparisons} comparisons");

        // 77 and 75 today. Two hundred wire units is forty pixels — comfortably
        // above anything seven ticks of quantised drift plus the known residual
        // produces, and far below a character being confused with somebody else.
        Assert.True(report.MaxXError < 200, $"max |dx| {report.MaxXError} wire units");
        Assert.True(report.MaxYError < 200, $"max |dy| {report.MaxYError} wire units");

        // And the weapons land. This is the assertion that a weapon regression
        // trips: the port must account for most of the damage the recording
        // shows, and it cannot do that with a weapon switched off.
        //
        // A *bound* rather than an equality, because the rest is unanchorable —
        // see AProjectileIsStateTheAnchorCannotReach. 89 hp of the recording's
        // damage goes unaccounted for today; with the Minegun alone disabled it
        // is 201, and with it the missed deaths go from one to three.
        var damage = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Hp,
        }.Replay(HotCombatTrace, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        var unaccounted = damage.Mismatches.Sum(m => m.Actual - m.Expected);
        Assert.True(unaccounted < 150, $"{unaccounted} hp of damage the port never dealt");
    }

    private static int Count(IReadOnlyList<TraceTick> ticks, MessageType type)
    {
        var total = 0;

        foreach (var tick in ticks)
        {
            foreach (var message in tick.Events)
            {
                if (message.Type == type)
                    total++;
            }
        }

        return total;
    }

    /// <summary>
    /// A player leaving renumbers the roster, and a replay that misses it starts
    /// comparing each remaining player against their neighbour.
    /// </summary>
    /// <remarks>
    /// <b>The bug this catches shipped, and no fixture could see it.</b>
    /// <c>Replayer</c> handled <c>PLAYER_LEAVE</c> with <c>ClearCharacter</c> —
    /// the <em>death</em> semantics — where a departure has to compact, because
    /// a roster index is positional and the server's own state updates arrive
    /// already compacted. CONTRIBUTING.md warns about exactly this and calls it
    /// intermittent; it is invisible until a recording contains a departure, and
    /// <c>reference-ctf_truefort-bots.trace</c> does not.
    /// <para>
    /// <c>reference-ctf_truefort-combat.trace</c> does: its medic leaves on tick
    /// 13, and from the next update onwards every remaining bot was compared
    /// against the one above it. The x drift went to <b>22,649 wire units</b> —
    /// four and a half thousand pixels, a character being confused with somebody
    /// on the far side of the map — and fell to 34 when the compaction landed.
    /// </para>
    /// <para>
    /// <b>Open loop, deliberately.</b> This recording cannot be replayed
    /// closed-loop at all: all six of its bots were alive before it started, so
    /// there is no <c>PLAYER_SPAWN</c> to anchor on and every character begins
    /// from a wire-quantised position — see
    /// <see cref="AnchoringOnTheWireInsteadOfASpawnPointBreaksAnExactTrace"/>,
    /// which proves that is enough on its own. So this asserts a <em>bound</em>
    /// on seven ticks of drift rather than exactness, and the bound is set where
    /// it separates "quantisation and two unimplemented weapons" from "the wrong
    /// player".
    /// </para>
    /// </remarks>
    [Fact]
    public void APlayerLeavingDoesNotShiftTheComparisonOntoTheirNeighbour()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", CombatTrace);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Position,
        }.Replay(CombatTrace, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        // The recording really does contain the departure this is about, so a
        // re-recorded fixture without one fails loudly instead of passing
        // vacuously.
        Assert.Contains(
            ticks,
            t => t.Events.Any(m => m.Type == MessageType.PlayerLeave));

        // Sanity: it got past the leave on tick 13 and kept comparing.
        Assert.True(report.Comparisons > 400, $"only {report.Comparisons} comparisons");

        // 34 with the compaction, 22,649 without. Five hundred wire units is a
        // hundred pixels — far above anything seven ticks of quantised drift can
        // produce and far below being handed the wrong player.
        Assert.True(
            report.MaxXError < 500,
            $"max |dx| {report.MaxXError} wire units: the roster looks shifted");

        // And the vertical, which is a separate bug's gate:
        // AnAnchorDoesNotSwallowAFreshKeyPress. 20 with the fix, 217 without —
        // a demoman's whole jump. A hundred is between them and well clear of
        // the couple of wire units quantised drift produces.
        Assert.True(
            report.MaxYError < 100,
            $"max |dy| {report.MaxYError} wire units: somebody is not jumping");
    }

    /// <summary>
    /// Anchoring on a broadcast position instead of a spawn point is enough, on
    /// its own, to stop the bot trace being exact.
    /// </summary>
    /// <remarks>
    /// <b>The controlled experiment behind a claim this repo would otherwise be
    /// asserting.</b> <c>reference-ctf_truefort-combat.trace</c> diverges at
    /// tick 9 and the explanation on offer is that its six bots were already
    /// alive when the recording started, so every one is anchored from a
    /// wire-quantised <c>QUICK_UPDATE</c> rather than from a <c>PLAYER_SPAWN</c>
    /// — a fifth of a pixel of position and of velocity, seven ticks before the
    /// first comparison. That is a claim about the <em>recording</em>, not about
    /// the physics, and the way to tell the two apart is to change only the
    /// anchor.
    /// <para>
    /// So: the same 898-tick recording, the same simulation, the same
    /// comparisons — with <see cref="ITraceSimulation.SpawnCharacter"/> refusing
    /// every spawn, which is exactly what the replayer sees for a character it
    /// cannot place and which sends it down the <c>Reconcile</c> path that
    /// anchors from the wire. It stops being exact. The physics did not change,
    /// so the anchor is the cause.
    /// </para>
    /// <para>
    /// The consequence is worth stating plainly, because it decides what the
    /// combat trace can ever be: <b>a recording whose characters were alive
    /// before it started cannot be replayed closed-loop</b>, by this port or by
    /// any other. It is an open-loop fixture — resynchronised at every
    /// comparison, measuring drift over the gap — and no amount of correctness
    /// will make it exact.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnchoringOnTheWireInsteadOfASpawnPointBreaksAnExactTrace()
    {
        var (trace, ticks) = Load();

        var fromSpawnPoints = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory | Fields.Hp,
        }.Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var fromTheWire = new Replayer(new NeverResolvesASpawnPoint(new PortedSimulation(MapLoader)))
        {
            Compare = Fields.Trajectory | Fields.Hp,
        }.Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        // The control: with spawn points, this recording is exact. If this ever
        // fails the experiment below proves nothing, because the baseline moved.
        Assert.Empty(fromSpawnPoints.Mismatches);

        // And with the identical physics, anchored from the wire, it is not.
        Assert.NotEmpty(fromTheWire.Mismatches);

        // The difference is the anchor and not a shorter run. Wire anchoring
        // spends each character's *first* observation resynchronising instead of
        // comparing — four characters, four fewer comparisons — so the counts
        // differ by the roster and not by an order of magnitude.
        Assert.True(fromTheWire.Resyncs > fromSpawnPoints.Resyncs);
        Assert.InRange(
            fromTheWire.Comparisons,
            fromSpawnPoints.Comparisons - 8,
            fromSpawnPoints.Comparisons);
    }

    /// <summary>
    /// A real simulation that cannot place a spawn point, so every character has
    /// to be anchored from a broadcast position instead.
    /// </summary>
    /// <remarks>
    /// Returning false is not a lie the harness has to be told: it is what
    /// <see cref="PortedSimulation.SpawnCharacter"/> itself returns for a spawn
    /// point id the map does not resolve, and what
    /// <c>Replayer.Reconcile</c> is written to cope with.
    /// </remarks>
    private sealed class NeverResolvesASpawnPoint(PortedSimulation inner) : ITraceSimulation
    {
        public void LoadMap(string map) => inner.LoadMap(map);

        public void SetCharacter(int slot, in CharacterBlock observed) => inner.SetCharacter(slot, observed);

        public bool SpawnCharacter(int slot, int spawnpointId, int group) => false;

        public void ClearCharacter(int slot) => inner.ClearCharacter(slot);

        public void RemoveSlot(int slot) => inner.RemoveSlot(slot);

        public void SetPlayerClass(int slot, PlayerClass playerClass) => inner.SetPlayerClass(slot, playerClass);

        public void SetPlayerTeam(int slot, Team team) => inner.SetPlayerTeam(slot, team);
        public void BuildSentry(int slot, double x, double y, int direction) => inner.BuildSentry(slot, x, y, direction);

        public void SetFireSeed(int slot, ushort seed) => inner.SetFireSeed(slot, seed);

        public void Tick(ReadOnlySpan<PlayerInput> inputs) => inner.Tick(inputs);

        public IReadOnlyList<DeathRecord> DeathsLastTick => inner.DeathsLastTick;

        public IReadOnlyList<UberRecord> UbersLastTick => inner.UbersLastTick;

        public IReadOnlyList<IntelRecord> IntelLastTick => inner.IntelLastTick;

        public IReadOnlyList<SentryRecord> SentriesLastTick => inner.SentriesLastTick;

        public bool TryGetControlPoint(int index, out ControlPointBlock point)
        {
            return inner.TryGetControlPoint(index, out point);
        }

        public void SetControlPointUnlock(int ticks) => inner.SetControlPointUnlock(ticks);

        public void SetTdmInvulnerability(int ticks) => inner.SetTdmInvulnerability(ticks);

        public bool TryGetCharacter(int slot, out CharacterBlock character) =>
            inner.TryGetCharacter(slot, out character);
    }

    /// <summary>
    /// A real simulation that keeps its deaths to itself.
    /// </summary>
    private sealed class NobodyEverDies(PortedSimulation inner) : ITraceSimulation
    {
        public void LoadMap(string map) => inner.LoadMap(map);

        public void SetCharacter(int slot, in CharacterBlock observed) => inner.SetCharacter(slot, observed);

        public bool SpawnCharacter(int slot, int spawnpointId, int group) =>
            inner.SpawnCharacter(slot, spawnpointId, group);

        public void ClearCharacter(int slot) => inner.ClearCharacter(slot);

        public void RemoveSlot(int slot) => inner.RemoveSlot(slot);

        public void SetPlayerClass(int slot, PlayerClass playerClass) => inner.SetPlayerClass(slot, playerClass);

        public void SetPlayerTeam(int slot, Team team) => inner.SetPlayerTeam(slot, team);
        public void BuildSentry(int slot, double x, double y, int direction) => inner.BuildSentry(slot, x, y, direction);

        public void SetFireSeed(int slot, ushort seed) => inner.SetFireSeed(slot, seed);

        public void Tick(ReadOnlySpan<PlayerInput> inputs) => inner.Tick(inputs);

        public IReadOnlyList<DeathRecord> DeathsLastTick => [];

        public IReadOnlyList<UberRecord> UbersLastTick => [];

        public IReadOnlyList<IntelRecord> IntelLastTick => [];

        public IReadOnlyList<SentryRecord> SentriesLastTick => [];

        public bool TryGetControlPoint(int index, out ControlPointBlock point)
        {
            point = default;
            return false;
        }

        public void SetControlPointUnlock(int ticks) { }

        public void SetTdmInvulnerability(int ticks) { }

        public bool TryGetCharacter(int slot, out CharacterBlock character) =>
            inner.TryGetCharacter(slot, out character);
    }

    /// <summary>
    /// Every shot the reference took, the port takes — on the same tick, with
    /// the same seed.
    /// </summary>
    /// <remarks>
    /// The direct form of a claim the position comparison only makes indirectly.
    /// The replayer hands over a seed for each recorded <c>WEAPON_FIRE</c>, and
    /// a seed is consumed only by a shot the simulation decided to take by
    /// itself — so an unused seed means the port did not fire when the reference
    /// did, and a shot without a seed means it fired when the reference did not.
    /// Both are zero.
    /// </remarks>
    [Fact]
    public void EveryRecordedShotIsFiredOnTheTickItWasRecordedFor()
    {
        var (trace, ticks) = Load();

        var simulation = new PortedSimulation(MapLoader);
        new Replayer(simulation) { Compare = Fields.Trajectory | Fields.Hp }
            .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(simulation.SeedsSupplied > 0, "the fixture contains no weapon fire at all");
        Assert.Equal(0, simulation.SeedsUnused);
        Assert.Equal(simulation.SeedsSupplied, simulation.ShotsFired);
    }

    /// <summary>
    /// The seed is load-bearing: replaying with the wrong one diverges, and it
    /// diverges exactly where combat starts.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="ClosedLoopIsExactAcrossTheWholeTrace"/> could
    /// pass for an uninteresting reason — six pellets fired point-blank might
    /// all land whatever the spread. They do not: flipping the seed moves enough
    /// pellets that hp, and the knockback that comes with it, disagree from the
    /// first damaged tick on. So the exact pass really is a statement about
    /// <c>GmlRandom</c> and about the order the scattergun draws from it.
    /// </remarks>
    [Fact]
    public void ADifferentSeedProducesADifferentFight()
    {
        var (trace, ticks) = Load();
        var damaged = FirstDamagedTick(ticks);

        var report = new Replayer(new MangledSeeds(new PortedSimulation(MapLoader)))
        {
            Compare = Fields.Trajectory | Fields.Hp,
        }.Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.NotEmpty(report.Mismatches);
        Assert.Equal(damaged, report.Mismatches.Min(m => m.Tick));
    }

    /// <summary>
    /// A real simulation with the recorded seeds replaced by wrong ones.
    /// </summary>
    /// <remarks>
    /// A delegating wrapper rather than a flag on <c>PortedSimulation</c>: the
    /// ability to corrupt a replay belongs to the one test that wants it, not to
    /// the harness every other test shares.
    /// </remarks>
    private sealed class MangledSeeds(PortedSimulation inner) : ITraceSimulation
    {
        public void LoadMap(string map) => inner.LoadMap(map);

        public void SetCharacter(int slot, in CharacterBlock observed) => inner.SetCharacter(slot, observed);

        public bool SpawnCharacter(int slot, int spawnpointId, int group) =>
            inner.SpawnCharacter(slot, spawnpointId, group);

        public void ClearCharacter(int slot) => inner.ClearCharacter(slot);

        public void RemoveSlot(int slot) => inner.RemoveSlot(slot);

        public void SetPlayerClass(int slot, PlayerClass playerClass) => inner.SetPlayerClass(slot, playerClass);

        public void SetPlayerTeam(int slot, Team team) => inner.SetPlayerTeam(slot, team);
        public void BuildSentry(int slot, double x, double y, int direction) => inner.BuildSentry(slot, x, y, direction);

        // Still a legal 16-bit seed, and still supplied on the right tick — so
        // the only thing this changes is where the pellets go.
        public void SetFireSeed(int slot, ushort seed) => inner.SetFireSeed(slot, (ushort)(seed ^ 0x5A5A));

        public void Tick(ReadOnlySpan<PlayerInput> inputs) => inner.Tick(inputs);

        public IReadOnlyList<DeathRecord> DeathsLastTick => inner.DeathsLastTick;

        public IReadOnlyList<UberRecord> UbersLastTick => inner.UbersLastTick;

        public IReadOnlyList<IntelRecord> IntelLastTick => inner.IntelLastTick;

        public IReadOnlyList<SentryRecord> SentriesLastTick => inner.SentriesLastTick;

        public bool TryGetControlPoint(int index, out ControlPointBlock point)
        {
            return inner.TryGetControlPoint(index, out point);
        }

        public void SetControlPointUnlock(int ticks) => inner.SetControlPointUnlock(ticks);

        public void SetTdmInvulnerability(int ticks) => inner.SetTdmInvulnerability(ticks);

        public bool TryGetCharacter(int slot, out CharacterBlock character) =>
            inner.TryGetCharacter(slot, out character);
    }

    /// <summary>
    /// The stretch before any damage, on its own: exact on both axes.
    /// </summary>
    /// <remarks>
    /// <b>Subsumed by <see cref="ClosedLoopIsExactAcrossTheWholeTrace"/> and
    /// kept anyway, because it splits a failure in half.</b> This segment
    /// contains no weapon fire at all, so if it fails the fault is in the
    /// character physics; if it passes and the whole-trace gate does not, the
    /// fault is in combat. Without it, one regression looks exactly like the
    /// other and the first thing anyone would do is re-derive this split by
    /// hand.
    /// </remarks>
    [Fact]
    public void ClosedLoopReachesCombatWithNoPositionErrorOnEitherAxis()
    {
        var (trace, ticks) = Load();
        var damaged = FirstDamagedTick(ticks);

        // Truncated at the first damage, so the gate is a plain "zero on both
        // axes" over everything phase 4 claims to reproduce. Replaying past it
        // would fold the knockback this phase does not implement into the same
        // number and there would be nothing to assert.
        var beforeCombat = ticks.Take(damaged).ToList();

        var report = Replay(beforeCombat, trace.Header.Map, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(report.Comparisons > 100, $"only {report.Comparisons} comparisons");
        Assert.Empty(report.Mismatches);
        Assert.Equal(0, report.MaxXError);
        Assert.Equal(0, report.MaxYError);
    }

    /// <summary>
    /// The anchor is on the reference's own value, not merely inside the right
    /// quantisation bin — so moving it can only make things worse.
    /// </summary>
    /// <remarks>
    /// With a wire-anchored replay, "is the residual the arithmetic or the
    /// anchor?" was answered by sweeping each slot's anchor across its own
    /// quantisation bin to see the divergence move later. A spawn-anchored
    /// replay has no bin left to sweep: a spawn resolves to an integer
    /// coordinate with both speeds zero.
    /// <para>
    /// So the experiment inverts. Nudging now starts a character somewhere the
    /// reference never was, and every offset must be strictly worse than none.
    /// A nudge that <em>helped</em> would mean the spawn lookup is putting
    /// characters in the wrong place and something else is absorbing the error.
    /// </para>
    /// </remarks>
    [Fact]
    public void NudgingTheAnchorOffTheSpawnPointOnlyMakesItWorse()
    {
        var (trace, ticks) = Load();
        var baseline = Replay(ticks, trace.Header.Map, ReplayMode.ClosedLoop);

        // Well inside a wire quantisation bin, so this is not a test that a
        // large displacement breaks things — a fiftieth of a pixel is far below
        // the resolution the wire could even express.
        foreach (var offset in new[] { -0.1, -0.02, 0.02, 0.1 })
        {
            var nudged = Run(ticks, trace.Header.Map, new Dictionary<int, AnchorNudge>
            {
                [2] = new AnchorNudge(0, offset, 0, 0),
            });

            Assert.True(nudged.TicksReplayed <= baseline.TicksReplayed,
                $"nudging slot 2 by {offset} reached tick {nudged.TicksReplayed}, " +
                $"further than the un-nudged {baseline.TicksReplayed} — the spawn anchor is not on the true value");
        }
    }

    private static DriftReport Run(
        IReadOnlyList<TraceTick> ticks,
        string map,
        IReadOnlyDictionary<int, AnchorNudge> nudges)
    {
        var simulation = new PortedSimulation(MapLoader);
        foreach (var (slot, nudge) in nudges)
            simulation.Nudges[slot] = nudge;

        return new Replayer(simulation) { Compare = Fields.Trajectory | Fields.Hp }
            .Replay(BotTrace, map, ticks, ReplayMode.ClosedLoop);
    }

    /// <summary>
    /// Open-loop: the prediction error a player would actually see, measured at
    /// every correction over the whole recording.
    /// </summary>
    /// <remarks>
    /// Reported as a distribution rather than pass/fail, per docs/PARITY.md.
    /// The gate is loose on the maximum and tight on the 99th percentile,
    /// because the interesting number is what happens on a typical tick — one
    /// bad window is a discrete event to go and find, not a reason to fail the
    /// suite while the fixture set is this small.
    /// </remarks>
    [Fact]
    public void OpenLoopPredictionErrorIsSubPixelOnATypicalTick()
    {
        var (trace, ticks) = Load();
        var report = Replay(ticks, trace.Header.Map, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.True(report.Comparisons > 150, $"only {report.Comparisons} comparisons");

        // Five wire units is one world pixel, and this test's name is a claim
        // about exactly that. 2 and 3 today, over 207 comparisons. One pixel is
        // the claim; hold it to it.
        Assert.True(report.PercentileXError(99) <= 5,
            $"p99 |dx| was {report.PercentileXError(99)}: {report.Render()}");
        Assert.True(report.PercentileYError(99) <= 5,
            $"p99 |dy| was {report.PercentileYError(99)}: {report.Render()}");
    }

    /// <summary>
    /// One update carries this tick's input and last tick's position. Replaying
    /// as though it were the other way round makes a correct simulation look
    /// systematically slow, which is how the pairing was settled.
    /// </summary>
    [Fact]
    public void PairingInputsWithTheWrongTickIsMeasurablyWorse()
    {
        var (trace, ticks) = Load();

        var right = Replay(ticks, trace.Header.Map, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        var wrong = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory | Fields.Hp,
            UpdatesPrecedeTheirTick = false,
        }.Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.True(wrong.PercentileXError(99) > right.PercentileXError(99),
            $"off-by-one p99 |dx| {wrong.PercentileXError(99)} was not worse than {right.PercentileXError(99)}");
    }

    /// <summary>
    /// The harness can still fail. Without this, a bug that made every
    /// comparison vacuous would show up as a clean pass.
    /// </summary>
    [Fact]
    public void TheEmptySimulationStillFailsTheSameTrace()
    {
        var (trace, ticks) = Load();

        var report = new Replayer(new EmptySimulation()) { Compare = Fields.Trajectory | Fields.Hp }
            .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop);

        Assert.False(report.Passed);
        Assert.True(report.MaxXError > 50, $"a still simulation only drifted {report.MaxXError}");
    }

    /// <summary>
    /// A <c>PLAYER_SPAWN</c> anchors on the map's own spawn point, exactly,
    /// rather than on the next broadcast position.
    /// </summary>
    /// <remarks>
    /// This is the change that makes a closed-loop gate of zero reachable, and
    /// the assertion is deliberately about <em>exactness</em> rather than
    /// closeness. The wire the test feeds in says the character is somewhere
    /// else entirely; the replay must ignore that and take the spawn point,
    /// because that is what the reference did and what every client does
    /// (<c>doEventSpawn.gml</c>).
    /// <para>
    /// Note the ordering it also pins: the spawn is applied before the tick's
    /// own comparison, so the character exists to be compared from its first
    /// tick instead of being resynchronised into place.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASpawnAnchorsOnTheSpawnPointAndNotOnTheWire()
    {
        const int Slot = 0;

        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerTeam(Slot, Team.Red);
        simulation.SetPlayerClass(Slot, PlayerClass.Scout);

        // Spawn point 2 of red's group 0. Deliberately not the first, so an
        // implementation that ignores the id and takes the head of the list
        // fails here.
        simulation.SpawnCharacter(Slot, 2, 0);

        Assert.True(simulation.TryGetState(Slot, out var state));

        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));
        var expected = new SpawnPoints(map);
        Assert.True(expected.TryGet(Team.Red, 0, 2, out var x, out var y));

        // Exact, not near: these came from the map, not through the wire's
        // fifths of a pixel.
        Assert.Equal(x, state.X);
        Assert.Equal(y, state.Y);
        Assert.Equal(0, state.HSpeed);
        Assert.Equal(0, state.VSpeed);

        // 1656, from the entity block — the value a wire anchor could only have
        // reached to within a fifth of a pixel.
        Assert.Equal(1656, state.X);
        Assert.Equal(456, state.Y);
    }

    /// <summary>
    /// An unresolvable spawn leaves the replay on its old behaviour rather than
    /// placing a character somewhere wrong.
    /// </summary>
    /// <remarks>
    /// Silently falling back is right here: a trace can name a spawn group a
    /// map does not have, and anchoring on the next observed position is what
    /// the harness did for all of phase 2. Placing a character at (0, 0)
    /// instead would report a wall of mismatches that say nothing.
    /// </remarks>
    [Fact]
    public void AnUnresolvableSpawnPlacesNothing()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerTeam(0, Team.Red);

        Assert.False(simulation.SpawnCharacter(0, 99, 0));
        Assert.False(simulation.TryGetCharacter(0, out _));

        Assert.False(simulation.SpawnCharacter(0, 0, 4));
        Assert.False(simulation.TryGetCharacter(0, out _));
    }

    /// <summary>
    /// A spawned character starts on its class's full health, as
    /// <c>Character.events/Create.xml:37</c> does it.
    /// </summary>
    [Fact]
    public void ASpawnedCharacterStartsOnFullHealth()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerTeam(0, Team.Red);
        simulation.SetPlayerClass(0, PlayerClass.Heavy);

        Assert.True(simulation.SpawnCharacter(0, 0, 0));
        Assert.True(simulation.TryGetCharacter(0, out var block));
        Assert.Equal(200, block.Hp);
    }
}
