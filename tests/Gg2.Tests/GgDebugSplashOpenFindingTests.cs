using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <b>The divergence that found the rotated-mask bug — fixed 2026-08-27.</b>
/// <c>reference-gg_debug-closedtdm.trace</c> replayed for 100 ticks after its
/// spawn anchor and then let slot 4 live through a death the reference
/// recorded. It now runs to 142, on every field.
/// </summary>
/// <remarks>
/// <b>What it was.</b> GM8's two collision primitives disagree about the same
/// pair of instances: <c>distance_to_object</c> is computed from the bounding
/// boxes and <c>place_free</c> from the precise masks. Unrotated the two
/// coincide, so six phases never noticed; rotated, the box is a large
/// over-estimate. The rocket that struck slot 2 on tick 475 overlapped it on its
/// top row alone, and at that heading the top row is <b>one pixel</b> inside an
/// eight-wide box — so <c>really_move_contact_solid</c> freed it six pixels
/// earlier than the port did, and the blast landed six pixels nearer.
/// <see cref="ProjectileMask"/> holds the model and
/// <see cref="RotatedMaskSampleTests"/> the measurement.
/// </remarks>
/// <remarks>
/// <b>How it was found, which is the part worth keeping.</b> Slot 6 never moves
/// in the window and its position is exact on every observation, so its 3-hp
/// shortfall was arithmetic rather than physics: through <c>ceil(hp)</c> on the
/// wire it pinned the t475 blast's <c>distance_to_object</c> to <b>30 or 31</b>
/// where the port computed <b>36</b>, and slot 4's death demanded the same
/// number independently. <see cref="TheBoxCanNeverKillSlotFourAndTheMaskDoes"/>
/// is that arithmetic, and it also shows the answer was unreachable by tuning:
/// the escape is horizontal, so on the box the rocket always lands at 36
/// whatever shape it is.
/// <para>
/// <b>Two candidates were killed by running oracles rather than by argument,
/// and the second is the one to remember.</b> Widening the two-pixel
/// <c>image_xscale = -1</c> inflation to eight reproduces the missing distance
/// of <em>exactly</em> 30 — the number the arithmetic demanded, to the pixel —
/// and breaks four live-engine tests. <b>Fitting the number is not
/// evidence.</b>
/// </para>
/// <para>
/// <b>And a second bug was hiding behind the first.</b> With the mask in, the
/// divergence moved to t481: slot 4 respawned at t478 and the port took eleven
/// hp off it where the reference took none. Team Deathmatch's spawn
/// invulnerability rides on the <c>FULL_UPDATE</c> — this recording says 30
/// ticks — and the parity harness had never read it, so every replayed
/// respawn was mortal from its first frame. <see cref="ITraceSimulation.SetTdmInvulnerability"/>.
/// </para>
/// <para>
/// <b>And a THIRD bug was behind the second.</b> The next divergence was slot 6
/// finishing 25 hp high — exactly a rocket's contact damage. The reference's
/// t509 rocket struck slot 6 and the port's struck slot 2, because
/// <c>CollideOne</c> walked characters in <b>slot</b> order where GM8 fires a
/// collision event in ascending <b>instance id</b>. Those agree until somebody
/// respawns: slot 6 came back at t508 and slot 2 at t509, so the port visited
/// the younger character first and exploded on it. 25 + 30 splash = 55, and
/// 140 − 55 = the 85 the recording holds.
/// <see cref="CharacterState.CreationOrder"/>.
/// </para>
/// <para>
/// <b>What stops it now, at t523,</b> is a different order of thing entirely:
/// slot 4's x is one wire unit — a fifth of a pixel — from the reference's.
/// Three missing kills became a rounding residue.
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
    /// <b>The tick is 476 and the message is in 477's block.</b> A death is
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
    /// <b>The port reproduces that death, and everything else, for 142 ticks
    /// after the spawn wave.</b>
    /// </summary>
    /// <remarks>
    /// Held on <see cref="Fields.All"/>: positions, velocities, hp, ammo, flags,
    /// deaths, ubers, the objective, sentries and control points, none of them
    /// disagreeing until t523. It was 95 before the rotated mask, 100 with the
    /// mask but without the spawn invulnerability, and 135 before characters
    /// were walked in creation order.
    /// <para>
    /// <b>The tick is pinned exactly rather than bounded below</b>, because
    /// both directions are news here. Shrinking means one of the three fixes has
    /// regressed — 135, 100 and 95 each name a different one; growing means the
    /// t523 sub-pixel residue has been closed, and this recording's 52 deaths
    /// and 262 seeded fires start becoming assertions.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortReproducesThatDeathAndRunsToTick523()
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
        Assert.Equal(523, first.Tick);

        // Nothing about slot 4's death at t476 is wrong any more, and asserting
        // that separately is the point: the tick above could move for reasons
        // that have nothing to do with the finding this file is named after.
        //
        // Bounded by the divergence, not by the recording. Past t516 the two
        // simulations have parted and every later death disagrees; asserting
        // over the whole trace would be asserting about drift.
        Assert.DoesNotContain(after, m => m.Field == "death" && m.Tick <= first.Tick);
    }

    /// <summary>
    /// <b>Team Deathmatch's spawn invulnerability is on the wire, and this
    /// recording says thirty ticks.</b>
    /// </summary>
    /// <remarks>
    /// The control for the second half of the fix. Without it
    /// <see cref="ThePortReproducesThatDeathAndRunsToTick523"/> would still pass
    /// at a shorter window and nothing would say why — the harness read no
    /// invulnerability at all for six phases, so every replayed respawn could be
    /// damaged on its first frame.
    /// <para>
    /// <b>It rides on a <c>FULL_UPDATE</c> and a recording holds exactly
    /// one</b>, at the join, so this is non-null on a single tick of nearly
    /// three thousand. A reader that looked for it anywhere else would find
    /// nothing and conclude the feature was off.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRecordingCarriesThirtyTicksOfSpawnInvulnerability()
    {
        var carried = Ticks()
            .Where(t => t.TdmInvulnerability is not null)
            .Select(t => (t.Index, t.TdmInvulnerability))
            .ToArray();

        Assert.Equal([(0, 30)], carried);
    }

    /// <summary>
    /// <b>The whole finding in one test: on the bounding box no stopping
    /// place can kill slot 4, and on the mask the rocket stops four pixels back
    /// and does.</b>
    /// </summary>
    /// <remarks>
    /// <b>The box half is a proof, not a sample.</b> The escape from a direct hit
    /// runs along <c>direction + 180</c>, almost due left here, so freeing the
    /// rocket means clearing slot 2's box on the left — and slot 4's box starts a
    /// fixed 36 pixels beyond slot 2's left edge. Making the rocket's own box
    /// longer or shorter moves the stopping place and the measured gap by the
    /// same amount, so 36 is not a number that could be tuned to 30. That is what
    /// sent the search past the back-out and into the primitives.
    /// <para>
    /// <b>The mask half is the fix.</b> At this heading the rocket's top row is
    /// one pixel wide, so <c>place_free</c> frees it at step 4 rather than step
    /// 10, six pixels nearer, and <c>distance_to_object</c> — which is still the
    /// box, and still right — reads 30. That is exactly the 30 slot 6's
    /// <c>ceil(hp)</c> demanded and exactly what kills slot 4.
    /// </para>
    /// <para>
    /// <b>This replicates the back-out loop rather than calling it</b>, because
    /// <c>Projectiles.BackOutOfCharacter</c> is private and the shape of the
    /// argument is what is being asserted. The production path is gated by the
    /// fixture instead — see <see cref="ThePortReproducesThatDeathAndRunsToTick523"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBoxCanNeverKillSlotFourAndTheMaskDoes()
    {
        var soldier = CharacterBox.For(PlayerClass.Soldier);

        // place_free's box for the character being backed out of: no facing
        // inflation. Explosions measures the bystander with it, so it has one.
        var victim = Aabb.ForCharacter(soldier, VictimX, VictimY);
        var bystander = Aabb.ForCharacter(soldier, BystanderX, BystanderY, FacingLeft);

        var blastRadius = Explosions.BlastRadiusFor(ProjectileKind.Rocket);
        var explosionDamage = Explosions.ExplosionDamageFor(ProjectileKind.Rocket);

        // What the t475 blast has to deal for the t476 rocket to finish slot 4
        // off: 15, which is a distance of exactly half the blast radius.
        var needed = SlotFourHpBeforeTheBlast - SlotFourDamageAtT476;

        var radians = Gml.DegToRad(RocketDirection + 180);
        var stepX = Math.Cos(radians);
        var stepY = -Math.Sin(radians);
        var steps = CollisionWorld.RoundPosition(RocketSpeed);

        int? boxStop = null;
        int? maskStop = null;

        for (var step = 0; step <= steps; step++)
        {
            var rocket = new ProjectileState
            {
                Kind = ProjectileKind.Rocket,
                X = RocketX + (stepX * step),
                Y = RocketY + (stepY * step),
                ImageAngle = RocketDirection,
            };

            if (boxStop is null && !rocket.Box.Overlaps(victim))
            {
                boxStop = step;

                // The proof: whatever the box frees on, it is never close enough.
                var distance = rocket.Box.Distance(bystander);
                Assert.Equal(36, distance);
                Assert.True(
                    explosionDamage * (1 - (distance / blastRadius)) < needed,
                    $"the box freed the rocket at {distance}, which would kill slot 4");
            }

            if (maskStop is null && !ProjectileMask.Overlaps(in rocket, in victim))
            {
                maskStop = step;

                // The fix: the mask frees six pixels earlier, at the distance
                // slot 6's hp and slot 4's death both point at.
                var distance = rocket.Box.Distance(bystander);
                Assert.Equal(30, distance);
                Assert.True(
                    explosionDamage * (1 - (distance / blastRadius)) >= needed,
                    $"the mask freed the rocket at {distance}, which would not kill slot 4");
            }
        }

        // Both really did stop somewhere inside the allowance, so neither branch
        // above passed by never running — the shape docs/PARITY.md warns about.
        Assert.Equal(10, boxStop);
        Assert.Equal(4, maskStop);
    }
}
