using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="MovingPlatforms"/> against
/// <c>Objects/Map elements/Colliders/MovingPlatform.events</c>.
/// </summary>
/// <remarks>
/// <b>Every entity here is hand-built, and it has to be: no shipped map
/// places a moving platform.</b> A census of the checked-in map fixture found 4
/// <c>dropdownPlatform</c> and <b>0</b> <c>movingPlatform</c> across all 22 maps,
/// so there is no fixture to load and nothing in the parity corpus that would
/// ever exercise this. That also means these tests are the only thing standing
/// behind the feature — there is no trace replay underneath them.
/// <para>
/// <b>What is deliberately NOT tested: the carry.</b> The reference's End Step
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

        // EXACTLY the start, not merely close to it. The reference assigns
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
    /// The reference clamps at the start and not at the far end, and the
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
    /// <b>The guard on the degree round-trip.</b>
    /// </summary>
    /// <remarks>
    /// The reference takes a <c>point_direction</c> and feeds it back through
    /// <c>degtorad</c> into <c>cos</c>/<c>sin</c>, so a platform moving straight
    /// up has a horizontal step of <c>cos(90°)</c> — 6.1e-17 rather than 0.
    /// Normalising <c>(left, -top)</c> directly would be arithmetically nicer and
    /// would make that step exactly 0.
    /// <para>
    /// <b>This does not make <c>x_</c> creep.</b> At a real map coordinate the
    /// residue is ~14 orders of magnitude below the position's own ULP, so
    /// <c>x_ += 1.8e-16</c> at <c>x_ = 300</c> is exactly <c>x_</c>. The drift is
    /// real per step and unobservable once added — which is why the residue has
    /// to be shown at <c>x = 0</c>, where the addition survives.
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
    /// The same shape as <c>WorldState.MaxProjectiles</c>: a custom map that
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
    /// <b>The stage's position is the point.</b> A platform is a map object,
    /// so GM8 runs its End Step before any character's — the same instance-order
    /// rule that puts <c>Generators.BeginStep</c> ahead of the character loop.
    /// This asserts the platform moved during a real tick; it cannot assert the
    /// ordering, which is why <see cref="MatchSimulation"/> carries the reason in
    /// a comment beside the call.
    /// <para>
    /// <b>The map has to carry the entity, not just the state.</b> The tick
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

    /// <summary>
    /// No built-in map puts a platform that could carry anyone where anyone
    /// can be: a platform naming a <c>resource</c> must sweep only cells no
    /// spawn can reach.
    /// </summary>
    /// <remarks>
    /// This is the claim the rest of this file rests on — every "hand-built
    /// because there is no fixture" remark, and the whole argument that the
    /// missing carry is unreachable in play.
    /// <para>
    /// <b>It is also the gate on a <c>SIM_UUID</c> exemption, which is the
    /// expensive half.</b> <see cref="SimulationStamp"/> did NOT bump for the
    /// moving-platform stage; that type's remarks carry the reasoning and name
    /// this test. <b>If this goes red, bump <c>SimulationBehaviourVersion</c> —
    /// do not reason about whether the new map's platform matters.</b> A
    /// checkpoint trained before this gate landed would otherwise be playing a
    /// different simulation with nothing to say so.
    /// </para>
    /// <para>
    /// <b>Narrowed from "no platform names a resource at all"</b> when
    /// <c>gg_debug2</c> gained the raft — a resourced platform inside a sealed
    /// chamber that exists precisely to give the mask pipeline a real embedded
    /// sprite to verify against. The narrowing is mechanical, not a name
    /// carve-out: the platform's whole swept extent (start to far end, plus its
    /// decoded sprite size) must be unreachable by a flood fill over open cells
    /// from every spawn. The fill is a superset of what a character can reach,
    /// so "unreached" soundly means "no character can walk, jump or fall to it".
    /// <b>Unreachable does NOT mean no character can ever be there</b> —
    /// placement paths exist that draw positions from nav nodes rather than
    /// walking anywhere, and nav-gen emits nodes inside sealed regions. That
    /// is why this gate licenses only "a shaped platform may exist while
    /// platforms can touch nothing", and why porting the carry (or the
    /// standing box) bumps unconditionally — <see cref="SimulationStamp"/>
    /// carries the whole argument, owed to <c>gg2-rl</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoShippedMapPlacesAPlatformThatCouldCarryAnyone()
    {
        ReferenceTree.RequireAvailable();

        var carriers = new List<string>();

        foreach (var (name, map) in ShippedMaps())
        {
            bool[]? reachable = null;

            foreach (var entity in map.Entities)
            {
                if (entity.Type != EntityTypes.MovingPlatform)
                    continue;

                // No resource means no sprite, which means no mask, which means
                // nothing can stand on it — in GM8 as much as here.
                var resourceName = entity.GetString("resource");
                if (string.IsNullOrEmpty(resourceName))
                    continue;

                // A resource that names nothing, or bytes SpriteMask rejects,
                // is a platform with sprite_index = -1: shapeless, permitted.
                var resource = map.Metadata?.GetString(resourceName);
                var mask = resource is null
                    ? null
                    : SpriteMask.Decode(System.Text.Encoding.Latin1.GetBytes(resource));

                if (mask is null)
                    continue;

                reachable ??= ReachableFromSpawns(map);

                // The swept extent: everywhere any pixel of the sprite can be
                // over a full cycle. The offset is (left, -top).
                var left = entity.GetReal("left", 0);
                var top = entity.GetReal("top", 60);

                // SCALED, and leaving this out made the gate fail OPEN.
                // A map sets the footprint three different ways — the `scale`
                // property that User Event 1 reads into image_xscale, and the
                // `xscale`/`yscale` the entity loader applies — and real maps go
                // up to 6 (`cp_scrap`, `surf_mup`). At 6 a 24x12 sprite covers
                // 144x72, so an unscaled extent checks a fraction of what the
                // platform really sweeps and can pass one that reaches a spawn.
                // The largest of the three is used because over-estimating the
                // extent fails CLOSED, which is the only safe direction here.
                var scale = Math.Max(
                    entity.GetReal("scale", 1),
                    Math.Max(entity.XScale, entity.YScale));

                if (scale < 1)
                    scale = 1;

                var width = mask.Width * scale;
                var height = mask.Height * scale;

                var x0 = Math.Min(entity.X, entity.X + left);
                var x1 = Math.Max(entity.X, entity.X + left) + width;
                var y0 = Math.Min(entity.Y, entity.Y - top);
                var y1 = Math.Max(entity.Y, entity.Y - top) + height;

                for (var cy = (int)Math.Floor(y0 / 6); cy <= (int)Math.Floor(y1 / 6); cy++)
                for (var cx = (int)Math.Floor(x0 / 6); cx <= (int)Math.Floor(x1 / 6); cx++)
                {
                    if (cx < 0 || cy < 0 || cx >= map.Walkmask.Width || cy >= map.Walkmask.Height)
                        continue;

                    if (reachable[(cy * map.Walkmask.Width) + cx])
                    {
                        carriers.Add(
                            $"{name}: platform with resource '{resourceName}' sweeps cell ({cx}, {cy}), "
                            + "which a character can reach from a spawn");
                        cx = map.Walkmask.Width;
                        cy = map.Walkmask.Height;
                    }
                }
            }
        }

        Assert.Empty(carriers);
    }

    /// <summary>
    /// Open cells a flood fill from every spawn entity can reach — a superset
    /// of anywhere a character can actually get without a teleport.
    /// </summary>
    private static bool[] ReachableFromSpawns(LevelMap map)
    {
        var mask = map.Walkmask;
        var reached = new bool[mask.Width * mask.Height];
        var frontier = new Queue<(int X, int Y)>();

        foreach (var entity in map.Entities)
        {
            if (!entity.Type.StartsWith("redspawn", StringComparison.Ordinal)
                && !entity.Type.StartsWith("bluespawn", StringComparison.Ordinal))
            {
                continue;
            }

            var x = (int)entity.X / 6;
            var y = (int)entity.Y / 6;

            if (x < 0 || y < 0 || x >= mask.Width || y >= mask.Height || mask.IsSolid(x, y))
                continue;

            if (!reached[(y * mask.Width) + x])
            {
                reached[(y * mask.Width) + x] = true;
                frontier.Enqueue((x, y));
            }
        }

        while (frontier.TryDequeue(out var at))
        {
            Span<(int X, int Y)> next =
                [(at.X - 1, at.Y), (at.X + 1, at.Y), (at.X, at.Y - 1), (at.X, at.Y + 1)];

            foreach (var (x, y) in next)
            {
                if (x < 0 || y < 0 || x >= mask.Width || y >= mask.Height)
                    continue;

                if (mask.IsSolid(x, y) || reached[(y * mask.Width) + x])
                    continue;

                reached[(y * mask.Width) + x] = true;
                frontier.Enqueue((x, y));
            }
        }

        return reached;
    }

    /// <summary>
    /// Every map this server can serve — the client's built-in 22 <b>and</b> this
    /// repo's own <c>maps/</c>.
    /// </summary>
    /// <remarks>
    /// <b>Both, and that is the whole point of this helper.</b> The first
    /// version of the test above scanned only the reference's <c>Included
    /// Files</c>, so a map added to <c>maps/</c> could never trip it — and
    /// <c>gg_debug2</c> places four platforms. A gate that cannot fire reads as
    /// diligence while checking nothing, and this one is load-bearing:
    /// <see cref="SimulationStamp"/>'s exemption names it.
    /// </remarks>
    private static IEnumerable<(string Name, LevelMap Map)> ShippedMaps()
    {
        var directories = new List<string> { ReferenceTree.IncludedFiles };

        var own = Path.Combine(RepositoryRoot(), "maps");
        if (Directory.Exists(own))
            directories.Add(own);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        foreach (var path in Directory.GetFiles(directory, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path)!;
            if (!seen.Add(name))
                continue;

            yield return (name, LevelMap.FromPng(name, File.ReadAllBytes(path)));
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gg2.sln")))
                return directory.FullName;
        }

        return AppContext.BaseDirectory;
    }
}
