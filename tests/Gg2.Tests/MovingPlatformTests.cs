using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="MovingPlatforms"/> against
/// <c>Objects/Map elements/Colliders/MovingPlatform.events</c>.
/// </summary>
/// <remarks>
/// ⚠️ <b>Every entity here is hand-built, and it has to be: no shipped map
/// places a moving platform.</b> A census of the checked-in map fixture found 4
/// <c>dropdownPlatform</c> and <b>0</b> <c>movingPlatform</c> across all 22 maps,
/// so there is no fixture to load and nothing in the parity corpus that would
/// ever exercise this. That also means these tests are the only thing standing
/// behind the feature — there is no trace replay underneath them.
/// <para>
/// ⚠️ <b>What is deliberately NOT tested: the carry.</b> The reference's End Step
/// also picks up whoever is standing on the platform, and that is not ported —
/// a <c>MovingPlatform</c> has no sprite of its own, so its collision box comes
/// from an embedded resource in a custom map and this port cannot know it.
/// <see cref="MovingPlatforms"/>'s remarks carry the detail. A test asserting
/// "nobody is carried" would pass for the wrong reason and would go red the day
/// the feature lands, so there is not one.
/// </para>
/// </remarks>
public sealed class MovingPlatformTests
{
    private const double StartX = 300;
    private const double StartY = 500;

    /// <summary>A platform that rises 60px at 3px a tick, which is the Create event's defaults.</summary>
    private static MovingPlatformDefinition Vertical(params (string Key, string Value)[] properties) =>
        MovingPlatforms.Of(Entity(properties));

    private static LevelEntity Entity(params (string Key, string Value)[] properties) =>
        EntityAt(StartX, StartY, properties);

    private static LevelEntity EntityAt(double x, double y, params (string Key, string Value)[] properties)
    {
        var bag = new Dictionary<string, GgonValue>();

        foreach (var (key, value) in properties)
            bag[key] = new GgonString(value);

        return new LevelEntity
        {
            Type = EntityTypes.MovingPlatform,
            X = x,
            Y = y,
            Properties = bag,
        };
    }

    private static WorldState Started(params MovingPlatformDefinition[] definitions)
    {
        var state = default(WorldState);
        MovingPlatforms.Reset(definitions, ref state);
        return state;
    }

    [Fact]
    public void TheCreateEventsDefaultsAreCarried()
    {
        var platform = Vertical();

        // MovingPlatform.events/Create.xml:5-9.
        Assert.Equal(60, platform.Top);
        Assert.Equal(0, platform.Left);
        Assert.Equal(3, platform.UpSpeed);
        Assert.Equal(3, platform.DownSpeed);
        Assert.Equal(0, platform.Trigger);
        Assert.Equal(StartX, platform.StartX);
        Assert.Equal(StartY, platform.StartY);
    }

    [Fact]
    public void TheMapOverridesEveryOneOfThem()
    {
        // User Event 1's readProperty names, which are all lower case on the
        // wire even where the Create event spells them in camel.
        var platform = Vertical(
            ("top", "120"), ("left", "-40"),
            ("upspeed", "1.5"), ("downspeed", "9"), ("trigger", "2"));

        Assert.Equal(120, platform.Top);
        Assert.Equal(-40, platform.Left);
        Assert.Equal(1.5, platform.UpSpeed);
        Assert.Equal(9, platform.DownSpeed);
        Assert.Equal(2, platform.Trigger);
    }

    /// <summary>
    /// An untriggered platform is already moving on its first tick; a triggered
    /// one waits.
    /// </summary>
    /// <remarks>
    /// Create.xml sets <c>movementState = 1</c> and User Event 1's last block
    /// overrides it to 0 when a <c>trigger</c> was given.
    /// </remarks>
    [Theory]
    [InlineData(null, MovingPlatforms.Up)]
    [InlineData("0", MovingPlatforms.Up)]
    [InlineData("1", MovingPlatforms.Stopped)]
    [InlineData("2", MovingPlatforms.Stopped)]
    public void ATriggerDecidesWhetherAPlatformStartsMoving(string? trigger, byte expected)
    {
        var definition = trigger is null ? Vertical() : Vertical(("trigger", trigger));
        var state = Started(definition);

        Assert.Equal(1, state.MovingPlatformCount);
        Assert.Equal(expected, state.MovingPlatforms[0].MovementState);
        Assert.Equal(StartX, state.MovingPlatforms[0].X);
        Assert.Equal(StartY, state.MovingPlatforms[0].Y);
    }

    [Fact]
    public void ItRisesAtItsUpSpeedAndTurnsRoundAtTheTop()
    {
        var definition = Vertical();
        var state = Started(definition);

        // 60px at 3px a tick: the twentieth step is the one that arrives, and
        // the turn happens on that same tick rather than the one after.
        for (var tick = 1; tick <= 20; tick++)
        {
            MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

            Assert.Equal(StartY - (3 * tick), state.MovingPlatforms[0].Y, 9);
            Assert.Equal(
                tick < 20 ? MovingPlatforms.Up : MovingPlatforms.Down,
                state.MovingPlatforms[0].MovementState);
        }
    }

    [Fact]
    public void ItReturnsToTheStartAndSetsOffAgain()
    {
        var definition = Vertical();
        var state = Started(definition);

        for (var tick = 0; tick < 39; tick++)
            MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

        // One short of home: still descending, 3px above the start.
        Assert.Equal(MovingPlatforms.Down, state.MovingPlatforms[0].MovementState);
        Assert.Equal(StartY - 3, state.MovingPlatforms[0].Y, 9);

        MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

        // ⚠️ EXACTLY the start, not merely close to it. The reference assigns
        // startX/startY rather than leaving the step where it landed, so the
        // cycle is exactly 40 ticks for ever instead of drifting.
        Assert.Equal(StartY, state.MovingPlatforms[0].Y);
        Assert.Equal(StartX, state.MovingPlatforms[0].X);
        Assert.Equal(MovingPlatforms.Up, state.MovingPlatforms[0].MovementState);
    }

    /// <summary>
    /// A triggered platform that has been set moving parks itself when it gets
    /// home rather than setting off again.
    /// </summary>
    [Fact]
    public void ATriggeredPlatformStopsWhenItGetsBack()
    {
        var definition = Vertical(("trigger", "1"));
        var state = Started(definition);

        // Nothing can start it in this port — that is the carry's job — so drive
        // it by hand to test the arm that brings it home.
        state.MovingPlatforms[0].MovementState = MovingPlatforms.Up;

        for (var tick = 0; tick < 40; tick++)
            MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

        Assert.Equal(MovingPlatforms.Stopped, state.MovingPlatforms[0].MovementState);
        Assert.Equal(StartY, state.MovingPlatforms[0].Y);
    }

    /// <summary>
    /// ⚠️ The reference clamps at the start and not at the far end, and the
    /// asymmetry is real.
    /// </summary>
    /// <remarks>
    /// With a travel that is not a whole number of steps the platform overshoots
    /// its stated top, turns round from there, and only then gets snapped back
    /// when it reaches the bottom. A "tidier" implementation that clamped both
    /// ends would put the platform a fraction of a pixel from where every client
    /// draws it for the whole descent.
    /// </remarks>
    [Fact]
    public void ItOvershootsTheTopButNeverTheBottom()
    {
        // 50px at 3px a tick: seventeen steps is 51.
        var definition = Vertical(("top", "50"));
        var state = Started(definition);

        for (var tick = 0; tick < 17; tick++)
            MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

        Assert.Equal(MovingPlatforms.Down, state.MovingPlatforms[0].MovementState);
        Assert.Equal(StartY - 51, state.MovingPlatforms[0].Y, 9);

        // And the descent lands home exactly, from 51 away at 3 a tick.
        for (var tick = 0; tick < 17; tick++)
            MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

        Assert.Equal(StartY, state.MovingPlatforms[0].Y);
        Assert.Equal(MovingPlatforms.Up, state.MovingPlatforms[0].MovementState);
    }

    /// <summary>A platform told to travel nowhere parks itself on its start.</summary>
    /// <remarks>
    /// <c>totalDist &lt;= 0</c> is also the divide-by-zero guard for the
    /// projection below it, which is why it is worth a test of its own.
    /// </remarks>
    [Fact]
    public void APlatformWithNowhereToGoStops()
    {
        var definition = Vertical(("top", "0"), ("left", "0"));
        var state = Started(definition);

        MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

        Assert.Equal(MovingPlatforms.Stopped, state.MovingPlatforms[0].MovementState);
        Assert.Equal(StartX, state.MovingPlatforms[0].X);
        Assert.Equal(StartY, state.MovingPlatforms[0].Y);
    }

    /// <summary>
    /// ⚠️ <b>The guard on the degree round-trip — and a correction to what this
    /// file first claimed about it.</b>
    /// </summary>
    /// <remarks>
    /// The reference takes a <c>point_direction</c> and feeds it back through
    /// <c>degtorad</c> into <c>cos</c>/<c>sin</c>, so a platform moving straight
    /// up has a horizontal step of <c>cos(90°)</c> — 6.1e-17 rather than 0.
    /// Normalising <c>(left, -top)</c> directly would be arithmetically nicer and
    /// would make that step exactly 0.
    /// <para>
    /// ⚠️ <b>I wrote that this makes <c>x_</c> creep. It does not, and the test
    /// said so.</b> At a real map coordinate the residue is ~14 orders of
    /// magnitude below the position's own ULP, so <c>x_ += 1.8e-16</c> at
    /// <c>x_ = 300</c> is exactly <c>x_</c>. The drift is real per step and
    /// unobservable once added — which is why the residue has to be shown at
    /// <c>x = 0</c>, where the addition survives.
    /// </para>
    /// <para>
    /// It stays transcribed the reference's way regardless: "unobservable at the
    /// coordinates shipped maps use" is a weaker claim than "identical", and the
    /// second one is free here.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDegreeRoundTripLeavesAResidueThatThePositionSwallows()
    {
        // At the origin the addition survives, so the residue is visible.
        var atOrigin = MovingPlatforms.Of(EntityAt(0, 0));
        var state = Started(atOrigin);

        for (var tick = 0; tick < 10; tick++)
            MovingPlatforms.Step(atOrigin, ref state.MovingPlatforms[0]);

        var residue = state.MovingPlatforms[0].X;

        Assert.NotEqual(0, residue);
        Assert.True(Math.Abs(residue) < 1e-14, $"{residue} is too large to be the cos(90°) residue");

        // Ten steps of cos(90°)*3, accumulated the way the reference does.
        var expected = 0.0;
        for (var tick = 0; tick < 10; tick++)
            expected += Math.Cos(Gml.DegToRad(Gml.PointDirection(0, 0, 0, -60))) * 3;

        Assert.Equal(expected, residue);

        // And at a real map coordinate the same platform does not move at all
        // horizontally, because the residue is below the ULP of 300.
        var onAMap = Vertical();
        var mapState = Started(onAMap);

        for (var tick = 0; tick < 10; tick++)
            MovingPlatforms.Step(onAMap, ref mapState.MovingPlatforms[0]);

        Assert.Equal(StartX, mapState.MovingPlatforms[0].X);
    }

    /// <summary>A horizontal platform, to prove the offset is not hard-wired to <c>top</c>.</summary>
    [Fact]
    public void ItTravelsAlongLeftToo()
    {
        var definition = Vertical(("top", "0"), ("left", "30"), ("upspeed", "10"));
        var state = Started(definition);

        MovingPlatforms.Step(definition, ref state.MovingPlatforms[0]);

        Assert.Equal(StartX + 10, state.MovingPlatforms[0].X, 9);
        Assert.Equal(StartY, state.MovingPlatforms[0].Y, 9);
        Assert.Equal(MovingPlatforms.Up, state.MovingPlatforms[0].MovementState);
    }

    /// <summary>
    /// The buffer's ceiling drops platforms rather than growing, and says so.
    /// </summary>
    /// <remarks>
    /// ⚠️ The same shape as <c>WorldState.MaxProjectiles</c>: a custom map that
    /// placed more would silently lose them, and the fix is to raise the ceiling
    /// rather than to handle the overflow. This pins the behaviour so that
    /// "silently" at least means "deterministically the first N".
    /// </remarks>
    [Fact]
    public void MorePlatformsThanTheBufferHoldsAreDroppedFromTheEnd()
    {
        var definitions = new MovingPlatformDefinition[WorldState.MaxMovingPlatforms + 4];
        Array.Fill(definitions, Vertical());

        var state = Started(definitions);

        Assert.Equal(WorldState.MaxMovingPlatforms, state.MovingPlatformCount);
    }

    /// <summary>The tick runs the platforms, not just the unit under test.</summary>
    /// <remarks>
    /// ⚠️ <b>The stage's position is the point.</b> A platform is a map object,
    /// so GM8 runs its End Step before any character's — the same instance-order
    /// rule that puts <c>Generators.BeginStep</c> ahead of the character loop.
    /// This asserts the platform moved during a real tick; it cannot assert the
    /// ordering, which is why <see cref="MatchSimulation"/> carries the reason in
    /// a comment beside the call.
    /// <para>
    /// ⚠️ <b>The map has to carry the entity, not just the state.</b> The tick
    /// reads the definitions off the <see cref="CollisionWorld"/> and the
    /// positions off the <see cref="WorldState"/>, so a hand-populated state on a
    /// world with no platforms steps nothing — which is exactly what the first
    /// draft of this test did, and it looked like the stage was missing.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARealTickAdvancesThem()
    {
        var map = WithOnePlatform();
        var world = new CollisionWorld(map);

        var state = default(WorldState);
        MovingPlatforms.Place(ref state, map);

        Assert.Equal(1, state.MovingPlatformCount);

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.Equal(StartY - 3, state.MovingPlatforms[0].Y, 9);
    }

    /// <summary>A shipped map with one hand-placed moving platform on it.</summary>
    private static LevelMap WithOnePlatform()
    {
        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);

        return map.WithEntities([.. map.Entities, Entity()]);
    }

    /// <summary>No shipped map places one, and the census that says so is mechanical.</summary>
    /// <remarks>
    /// ⚠️ This is the claim the rest of this file rests on — every "hand-built
    /// because there is no fixture" remark, and the whole argument that the
    /// missing carry is unreachable. If a map ever gains one, this goes red and
    /// those remarks need re-reading rather than the test relaxing.
    /// <para>
    /// ⛔ <b>It is also the gate on a <c>SIM_UUID</c> exemption, which is the
    /// expensive half.</b> <see cref="SimulationStamp"/> did NOT bump for the
    /// moving-platform stage, on the grounds that the stage is structurally
    /// unreachable while this test passes. <b>If this goes red, bump
    /// <c>SimulationBehaviourVersion</c> — do not reason about whether the new
    /// map's platform matters.</b> A checkpoint trained before 2026-09-01 would
    /// otherwise be playing a different simulation with nothing to say so.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoShippedMapPlacesAMovingPlatform()
    {
        ReferenceTree.RequireAvailable();

        var withPlatforms = new List<string>();

        foreach (var path in Directory.GetFiles(ReferenceTree.IncludedFiles, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path)!;

            if (MovingPlatforms.Build(LevelMap.FromPng(name, ReferenceTree.ReadMap(name))).Length > 0)
                withPlatforms.Add(name);
        }

        Assert.Empty(withPlatforms);
    }
}
