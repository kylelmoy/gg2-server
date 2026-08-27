using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>The open finding that stops <c>reference-gg_debug-closedtdm.trace</c>
/// at a hundred ticks: the port's rocket blasts land a few pixels short, and
/// slot 4 lives through a death the reference recorded.</b>
/// </summary>
/// <remarks>
/// <b>Narrowed on 2026-08-27; not settled.</b> Implementing dropdown platforms
/// took this recording's window from 23 ticks to 100 with the trajectories
/// inside it exact (<c>|dx| max 0, |dy| max 0</c>), and what stopped it next was
/// damage rather than position — see
/// <see cref="TeamDeathmatchFixtureTests.GgDebugReplaysPastItsDropdownPlatformsAndStopsOnDamage"/>.
/// <para>
/// <b>What is established.</b> All six rockets in the window are fired by the
/// same players on the same ticks in both, all six explode on the same ticks,
/// and the reference's <c>PLAYER_DEATH</c> names the killer and the assistant
/// the port's own bookkeeping picked. Nothing about <em>which</em> rocket hit
/// <em>whom</em> is in dispute. The port simply deals a little less: over ticks
/// 475 to 480 it deals slot 2 97.4 where the reference dealt 99 to 100, slot 6
/// 53.9 where the reference dealt 56 to 57, and slot 4 68.4 where the reference
/// dealt at least 70 — a kill.
/// </para>
/// <para>
/// <b>Slot 6 is the clean measurement</b>, and it is the reason this is a damage
/// finding rather than a physics one: it never moves in the window, its position
/// is exact on every observation, and it still ends three hp higher here. Its
/// three hits are the t475 blast, a point-blank t476 blast already at the
/// falloff floor, and the t480 wall blast — so the shortfall has to be in the
/// t475 blast, and pinning slot 6's arithmetic to <c>[56, 57)</c> puts that
/// blast's <c>distance_to_object</c> at <b>30 or 31</b> where the port computes
/// <b>36</b> — and nothing else, because 29 would leave slot 6 on 13 and 32
/// would leave it on 15. Slot 4's death needs the same thing independently:
/// 25 + 30 at t476 leaves it needing 15 from t475, which is a distance of 32.5
/// or less.
/// </para>
/// <para>
/// ⚠️ <b>And that is why this is not a tuning error in the back-out.</b>
/// <see cref="NoBackOutThatFreesTheRocketFromSlotTwoCanKillSlotFour"/> walks
/// every whole-pixel step <c>really_move_contact_solid</c> could stop on and
/// shows the distance is <b>never</b> below 36 — because the escape is
/// horizontal, so the rocket lands with its right edge just left of slot 2's
/// box, and slot 4's box starts 36 pixels beyond that <em>whatever shape the
/// rocket is</em>. Making the rocket longer or shorter moves the stopping place
/// and the measured gap by the same amount.
/// </para>
/// <para>
/// <b>Where that leaves it.</b> A distance of 30 or 31 is the rocket stopping
/// <b>four or five pixels back</b> instead of the port's ten — which is to say
/// it exploded <em>while still inside slot 2's bounding box</em>. So the
/// back-out is not stopping too late by a tuning margin; it is being asked the
/// wrong question. Three candidates, and two of them are already dead:
/// </para>
/// <list type="number">
/// <item>❌ <b>What <c>distance_to_object</c> returns</b> — ruled out.
/// <c>PrimitiveSampleTests.DistanceToObjectMatchesTheEngineExactly</c> holds
/// <see cref="Aabb.Distance"/> against the engine over 361 rocket offsets
/// spanning ±64 px, exactly, and this gap is 36.</item>
/// <item>❌ <b>The two-pixel <c>image_xscale = -1</c> inflation</b>
/// (<see cref="Aabb.ForCharacter"/>) — ruled out, and checked rather than
/// assumed: widening it to eight <em>does</em> produce this blast's missing
/// distance of exactly 30, and it breaks four live-engine oracles
/// (<c>PrimitiveSampleTests.FacingChangesTheSplashDistanceButNotTheOverlap</c>,
/// two in <c>CollisionLineSampleTests</c>, and <c>RocketTests</c>). The
/// arithmetic is right and the cause is not.</item>
/// <item>⬅️ <b>Whether <c>place_free</c> uses the bounding box or the precise
/// mask for a <em>rotated</em> projectile.</b> The prime suspect, and the one
/// thing here no oracle can see: every rocket in <c>primitive-samples.json</c>
/// is unrotated — 361 of 361 with a box of 8x2 — so <c>place_meeting</c> has
/// only ever been asked about an axis-aligned rocket, where the two answers
/// agree. <c>rotated-box-samples.json</c> gates what <c>bbox_*</c> <em>reports</em>
/// for a rotated sprite (1,008 boxes) and not what the overlap primitives
/// <em>use</em>. This rocket is at 351°, its bbox is 8x3, and it overlaps slot 2
/// by exactly one pixel row along the box's top edge — where a rocket pointing
/// down-right has no sprite pixels at all. A precise mask would free it several
/// steps early, which is the shape of the missing four-to-six pixels.</item>
/// </list>
/// <para>
/// ⚠️ <b>Owed to the next live session</b>, and it is one cheap probe rather
/// than a re-derivation: create a <c>Rocket</c>, set <c>image_angle</c> to
/// something off-axis, and walk it across a pinned character asking both
/// <c>place_meeting</c> and <c>bbox_*</c>. If the two disagree, the answer is
/// here. <c>tools/probes/</c> is where it goes, and extending
/// <c>tools/sample-primitives.js</c> to sweep the angle is the durable version.
/// </para>
/// </remarks>
public sealed class GgDebugSplashOpenFindingTests
{
    private const string Fixture = "reference-gg_debug-closedtdm.trace";

    /// <summary>
    /// The state at the moment of the t475 blast, read out of the closed-loop
    /// replay. Every one of these is the port's own value on a tick the wire
    /// still agrees with, so it is the reference's too.
    /// </summary>
    private const double RocketX = 588.8292332513191;

    private const double RocketY = 164.41659646377096;

    /// <summary>The rocket's <c>direction</c>, which is also its <c>image_angle</c>.</summary>
    private const double RocketDirection = 351.002197265625;

    /// <summary><c>really_move_contact_solid(direction, speed)</c>'s allowance.</summary>
    private const double RocketSpeed = 12.051499581482329;

    /// <summary>Slot 2, the character the rocket struck head-on.</summary>
    private const double VictimX = 591.7125484719396;

    private const double VictimY = 140.209375;

    /// <summary>Slots 4 and 6, standing on the same pixel, 36 px to the right.</summary>
    private const double BystanderX = 629.4972928249938;

    private const double BystanderY = 168.40539721120302;

    /// <summary>Both are facing left, so both boxes take the inflation.</summary>
    private const int FacingLeft = -1;

    /// <summary>Slot 4's hp when the t475 blast reached it.</summary>
    private const double SlotFourHpBeforeTheBlast = 70;

    /// <summary>
    /// What the t476 rocket deals slot 4 head-on: 25 on contact plus the whole
    /// 30 of the blast, because the contact reposition pulls the distance under
    /// one pixel and <c>Explosions.SplashOne</c> floors it to zero.
    /// </summary>
    private const double SlotFourDamageAtT476 = 55;

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static IReadOnlyList<TraceTick> Ticks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        return TraceParser.Parse(trace, GameModes.ShapeOf(MapLoader(trace.Header.Map)), out _, out _);
    }

    /// <summary>
    /// The recording really does kill slot 4 on tick 476, with slot 5's rocket
    /// and slot 3 assisting — which is exactly the pair the port's own damage
    /// bookkeeping chose.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The tick is 476 and the message is in 477's block.</b> A death is
    /// reported in the following tick's events for the same reason a fire seed
    /// is — <c>GameServerBeginStep.gml</c> has already serialised the tick that
    /// killed it. See <see cref="Replayer"/>.
    /// <para>
    /// This is the half of the finding that comes from the wire rather than from
    /// the port, so it is asserted separately: if a re-recording ever changes it,
    /// every number in this file's remarks is stale.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheReferenceKillsSlotFourOnTickFourHundredAndSeventySix()
    {
        var deaths = Ticks()
            .Where(t => t.Index == 477)
            .SelectMany(t => t.Events)
            .Where(m => m.Type == MessageType.PlayerDeath)
            .ToArray();

        var death = Assert.Single(deaths);

        // [victim, killer, assistant, damage source]; 9 is the rocketlauncher.
        Assert.Equal<byte[]>([4, 5, 3, 9], death.Payload.ToArray());
    }

    /// <summary>
    /// ⚠️ <b>The first thing the port gets wrong after the spawn wave is that
    /// death, and nothing before it.</b>
    /// </summary>
    /// <remarks>
    /// Held on <see cref="Fields.All"/> rather than on <c>Trajectory</c>, which
    /// is the point: ninety-five ticks of positions, velocities, hp, ammo, flags,
    /// deaths, ubers, the objective, sentries and control points all agree, and
    /// then one character survives.
    /// <para>
    /// <b>This fails when the finding is fixed</b>, like
    /// <c>SentryKillFixtureTests.ThePortStillMissesBothOfTheReferencesSentrySelfDestructs</c>.
    /// That is the intent: the next session should not have to rediscover which
    /// tick to look at, and a fix that moves the divergence somewhere else is
    /// news either way.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortStillMissesThatDeathAndGetsEverythingBeforeItRight()
    {
        var ticks = Ticks();
        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.All }
            .Replay(Fixture, "gg_debug", ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        Assert.NotEmpty(after);

        var first = after.MinBy(m => m.Tick);

        Assert.Equal(381, anchor);
        Assert.Equal(476, first.Tick);
        Assert.Equal(4, first.Slot);
        Assert.Equal("death", first.Field);
        Assert.Equal(1, first.Expected);
        Assert.Equal(0, first.Actual);
    }

    /// <summary>
    /// ⚠️ <b>And it cannot be fixed by moving the back-out</b>: every stopping
    /// place that frees the rocket from slot 2 leaves it at least 36 pixels from
    /// slot 4, where the kill needs 32.5 or less.
    /// </summary>
    /// <remarks>
    /// The escape runs along <c>direction + 180</c>, which is almost due left
    /// here, so freeing the rocket means clearing slot 2's box on the left — and
    /// slot 4's box begins a fixed 36 pixels beyond slot 2's left edge. Making
    /// the rocket's own box longer or shorter moves both the stopping place and
    /// the measured gap by the same amount, so the gap does not change. That is
    /// why the remarks above look past the back-out to the two box conventions
    /// and to whether a one-row overlap frees at all.
    /// <para>
    /// ⚠️ <b>The enumeration is not vacuous and the last assertion is what says
    /// so.</b> A test that walked twelve steps and found none of them free would
    /// pass this happily while proving nothing — so the step the port actually
    /// stops on is asserted to be one of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoBackOutThatFreesTheRocketFromSlotTwoCanKillSlotFour()
    {
        var soldier = CharacterBox.For(PlayerClass.Soldier);

        // place_free's box for the character being backed out of: no facing
        // inflation. Explosions measures the bystander with it, so it has one.
        var victim = Aabb.ForCharacter(soldier, VictimX, VictimY);
        var bystander = Aabb.ForCharacter(soldier, BystanderX, BystanderY, FacingLeft);

        var blastRadius = Explosions.BlastRadiusFor(ProjectileKind.Rocket);
        var explosionDamage = Explosions.ExplosionDamageFor(ProjectileKind.Rocket);

        // What the t475 blast would have to deal for the t476 rocket to finish
        // slot 4 off: 15, which is a distance of exactly half the blast radius.
        var needed = SlotFourHpBeforeTheBlast - SlotFourDamageAtT476;

        var radians = Gml.DegToRad(RocketDirection + 180);
        var stepX = Math.Cos(radians);
        var stepY = -Math.Sin(radians);
        var steps = CollisionWorld.RoundPosition(RocketSpeed);

        var freed = new List<int>();

        for (var step = 0; step <= steps; step++)
        {
            var rocket = new ProjectileState
            {
                Kind = ProjectileKind.Rocket,
                X = RocketX + (stepX * step),
                Y = RocketY + (stepY * step),
                ImageAngle = RocketDirection,
            };

            if (rocket.Box.Overlaps(victim))
                continue;

            freed.Add(step);

            var distance = rocket.Box.Distance(bystander);
            var damage = explosionDamage * (1 - (distance / blastRadius));

            Assert.True(
                damage < needed,
                $"step {step} frees the rocket at distance {distance} and deals {damage} to slot 4, "
                    + "which is enough to kill it — the gg_debug damage finding may be fixed. "
                    + "Re-measure the window and rewrite this file's remarks.");
        }

        // The control: the port really does stop somewhere in that list, so the
        // loop above tested something.
        Assert.Contains(10, freed);
    }
}
