using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Nav;

/// <summary>Which grid an instance stamps into, and how.</summary>
/// <remarks>
/// A flat enum rather than an object hierarchy: the generator asks exactly one
/// question of an instance — "which of the five grids do you write, and with
/// what value" — and the reference's <c>with(Parent)</c> reach is expressed by
/// mapping every entity type straight onto its answer.
/// </remarks>
internal enum NavStamp
{
    /// <summary>Not stamped, but its <c>y</c> splits a multi-stage map.</summary>
    None,

    /// <summary><c>PlayerWall</c> and <c>PlayerWallHorizontal</c> — solid.</summary>
    Solid,

    /// <summary><c>DropdownPlatform</c> — supports, and can be dropped through.</summary>
    Platform,

    /// <summary><c>KillBox</c>, <c>PitFall</c>, <c>FragBox</c> — never stand here.</summary>
    Lethal,

    /// <summary><c>LeftDoor</c> — blocks leftward crossing only.</summary>
    DoorLeft,

    /// <summary><c>RightDoor</c> — blocks rightward crossing only.</summary>
    DoorRight,

    /// <summary>A gate, whose code is on the instance.</summary>
    Gate,

    /// <summary>A <c>MoveBox</c>, whose push direction is on the instance.</summary>
    MoveBox,
}

/// <summary>
/// One map entity as the instance the reference's builder would have made:
/// a bounding box, and what it stamps.
/// </summary>
internal readonly record struct NavInstance(
    string Type,
    NavStamp Stamp,
    double Y,
    bool HasBox,
    double Left,
    double Right,
    double Top,
    double Bottom,
    int GateCode,
    double PushX,
    double PushY);

/// <summary>
/// <c>navMarkInstances</c>, offline: the map objects the walkmask does not
/// contain.
/// </summary>
/// <remarks>
/// The walkmask is only terrain, so a graph built from it alone routes bots
/// straight through player walls, treats every drop-through platform as thin
/// air, happily paths across a killbox, and walks <c>LeftDoor</c>/<c>RightDoor</c>
/// as freely passable both ways. In the game those are ordinary instances and
/// <c>with(PlayerWall)</c> finds them; here they are reconstructed from the
/// entity list.
/// <para>
/// ⚠️ <b>The sprite sizes are <see cref="Gg2.Simulation"/>'s, restated rather
/// than shared.</b> This project deliberately does not reference the simulation
/// — a generator has no business reaching into the tick — so the four collider
/// sizes appear here as well as in <c>ColliderSprites</c>, and
/// <c>NavInstanceTests</c> asserts the two agree and that the reference's own
/// sprite XML still declares origin (0, 0) with a full-image RECTANGLE mask.
/// That last check is what <c>gg2-nav-gen</c> did at run time by reading the
/// checkout; this port cannot, because it ships without one.
/// </para>
/// <para>
/// ⚠️ <b><c>MovingPlatform</c> is skipped rather than guessed at.</b> In the game
/// it is stamped wherever it happens to be sitting when the graph is built,
/// which is not a fact about the map at all — a moving platform reads as a
/// static ledge at whichever end of its travel the build caught it. Offline
/// there is no "happens to be", so inventing one would make the graph disagree
/// with the game for a reason nothing could reproduce. No shipped map uses one.
/// </para>
/// </remarks>
internal static class NavInstances
{
    // The four collider sprites, each a plain filled rectangle with its origin
    // at (0, 0) — see ColliderSprites, which carries the same numbers for the
    // simulation's side of the same objects.
    private const int BarLong = 60;   // sprite44 is 60x6, sprite45 is 6x60
    private const int BarShort = 6;
    private const int Box = 42;       // sprite64
    private const int SetupGate = 32; // SetupGateS

    /// <summary>What <c>MoveBox*</c> pushes with when the map does not say.</summary>
    /// <remarks><c>MoveBox.events/User Event 1</c>: <c>speed</c>, defaulting to 5.</remarks>
    private const double DefaultPushPower = 5;

    /// <summary>
    /// Every nav-relevant entity as an instance, in level-data order.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>File order is preserved and matters.</b> It is creation order, which
    /// is the order <c>with()</c> visits instances in, which is what decides who
    /// wins where two stamps overlap.
    /// </remarks>
    public static List<NavInstance> Instantiate(IReadOnlyList<LevelEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var instances = new List<NavInstance>();

        foreach (var entity in entities)
        {
            if (Describe(entity) is { } instance)
                instances.Add(instance);
        }

        return instances;
    }

    private static NavInstance? Describe(LevelEntity entity)
    {
        // The entity -> object table is builder_init.gml's addEntity block,
        // narrowed to the types that reach a nav grid. Everything else (spawns,
        // cabinets, control points, the objective markers) is invisible here.
        var (stamp, gate, spriteWidth, spriteHeight) = entity.Type switch
        {
            EntityTypes.PlayerWall => (NavStamp.Solid, 0, BarShort, BarLong),
            EntityTypes.PlayerWallHorizontal => (NavStamp.Solid, 0, BarLong, BarShort),
            EntityTypes.DropdownPlatform => (NavStamp.Platform, 0, BarLong, BarShort),

            // ⚠️ MovingPlatform is a DropdownPlatform in the object tree and is
            // still not stamped — its sprite is an embedded resource in the map,
            // so there is no size to stamp with even if there were a position
            // worth stamping. See the remarks above.
            EntityTypes.MovingPlatform => (NavStamp.None, 0, 0, 0),

            EntityTypes.KillBox or EntityTypes.PitFall or EntityTypes.FragBox =>
                (NavStamp.Lethal, 0, Box, Box),

            EntityTypes.LeftDoor => (NavStamp.DoorLeft, 0, BarShort, BarLong),
            EntityTypes.RightDoor => (NavStamp.DoorRight, 0, BarShort, BarLong),

            // Gates. The "2" and "horizontal" variants are floors rather than
            // walls, so they wear sprite44.
            //
            // ⚠️ IntelGateHorizontal and IntelGateVertical are TEAM_ANY and stamp
            // as BLUE. That is navMarkInstances' own quirk: it tests
            // `team == TEAM_RED` and takes the blue branch otherwise. Faithful,
            // quirk included — changing it would put this generator and every
            // graph already checked in one apart.
            EntityTypes.RedTeamGate => (NavStamp.Gate, NavConstants.GateTeamRed, BarShort, BarLong),
            EntityTypes.BlueTeamGate => (NavStamp.Gate, NavConstants.GateTeamBlue, BarShort, BarLong),
            EntityTypes.RedTeamGate2 => (NavStamp.Gate, NavConstants.GateTeamRed, BarLong, BarShort),
            EntityTypes.BlueTeamGate2 => (NavStamp.Gate, NavConstants.GateTeamBlue, BarLong, BarShort),
            EntityTypes.RedIntelGate => (NavStamp.Gate, NavConstants.GateIntelRed, BarShort, BarLong),
            EntityTypes.BlueIntelGate => (NavStamp.Gate, NavConstants.GateIntelBlue, BarShort, BarLong),
            EntityTypes.RedIntelGate2 => (NavStamp.Gate, NavConstants.GateIntelRed, BarLong, BarShort),
            EntityTypes.BlueIntelGate2 => (NavStamp.Gate, NavConstants.GateIntelBlue, BarLong, BarShort),
            EntityTypes.IntelGateHorizontal => (NavStamp.Gate, NavConstants.GateIntelBlue, BarLong, BarShort),
            EntityTypes.IntelGateVertical => (NavStamp.Gate, NavConstants.GateIntelBlue, BarShort, BarLong),
            EntityTypes.SetupGate => (NavStamp.Gate, NavConstants.GateSetup, SetupGate, SetupGate),

            EntityTypes.MoveBoxUp or EntityTypes.MoveBoxDown
                or EntityTypes.MoveBoxLeft or EntityTypes.MoveBoxRight =>
                (NavStamp.MoveBox, 0, Box, Box),

            // Not stamped, but its y is what splits a multi-stage map into
            // areas, so it has to survive into the instance list.
            EntityTypes.NextArea => (NavStamp.None, 0, 0, 0),

            _ => (default(NavStamp), -1, 0, 0),
        };

        if (gate < 0)
            return null;

        var push = 0.0;
        var pushX = 0.0;
        var pushY = 0.0;

        if (stamp == NavStamp.MoveBox)
        {
            push = entity.GetReal("speed", DefaultPushPower);

            switch (entity.Type)
            {
                case EntityTypes.MoveBoxUp: pushY = -push; break;
                case EntityTypes.MoveBoxDown: pushY = push; break;
                case EntityTypes.MoveBoxLeft: pushX = -push; break;
                default: pushX = push; break;
            }
        }

        if (spriteWidth == 0)
        {
            return new NavInstance(
                entity.Type, stamp, entity.Y, HasBox: false, 0, 0, 0, 0, gate, pushX, pushY);
        }

        // GM8's bounding box for a sprite whose mask is the whole image at
        // origin (0, 0): the box spans the scaled image and is inclusive at both
        // ends, so a 60x6 sprite at scale 1 covers x .. x + 59. Negative scales
        // mirror, which the map editor can produce even though no shipped map
        // does — hence the min/max rather than a bare addition.
        //
        // ⚠️ ROUNDED, the way GM8 rounds an instance's position before deriving
        // its bbox — `SolidRect.FromEntity` does the same, and NavInstanceTests
        // asserts the two agree. gg2-nav-gen used the raw doubles, which differs
        // by up to a pixel on a FRACTIONAL scale (a 60x6 bar at yscale 0.3 ends
        // at 607.8, not 608). Measured 2026-09-03: rounding changes NOTHING on
        // any of the 25 shipped graphs, so the two were free to differ and did.
        // Rounding is the one to keep — it is what the reference's own
        // navMarkInstances read, since it ran inside GM8 against live instances,
        // and it means a custom map cannot have the graph believing a wall is in
        // one cell while the physics puts it in the next.
        var x0 = (double)Round(entity.X);
        var x1 = (double)Round(entity.X + (spriteWidth * entity.XScale));
        var y0 = (double)Round(entity.Y);
        var y1 = (double)Round(entity.Y + (spriteHeight * entity.YScale));

        return new NavInstance(
            entity.Type,
            stamp,
            entity.Y,
            HasBox: true,
            Left: Math.Min(x0, x1),
            Right: Math.Max(x0, x1) - 1,
            Top: Math.Min(y0, y1),
            Bottom: Math.Max(y0, y1) - 1,
            gate,
            pushX,
            pushY);
    }

    /// <summary>
    /// Stamps every instance into the five grids, in the reference's order.
    /// </summary>
    /// <remarks>
    /// One region write per instance, exactly as <c>navMarkInstances</c> does it:
    /// there are dozens of instances against up to 560,000 cells, so a per-cell
    /// test would be four orders of magnitude of wasted work.
    /// <para>
    /// ⚠️ <b>Gates go last</b>, so where two overlap the later stamp wins — and
    /// team gates before intel gates, which is the reference's order even though
    /// the two are siblings and no instance is caught twice.
    /// </para>
    /// </remarks>
    public static void StampInto(in NavGrids grids, List<NavInstance> instances, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(instances);

        foreach (var instance in instances)
        {
            if (!instance.HasBox)
                continue;

            switch (instance.Stamp)
            {
                case NavStamp.Solid:
                    Region(grids.Solid, instance, width, height, 1);
                    break;
                case NavStamp.Platform:
                    Region(grids.Platform, instance, width, height, 1);
                    break;
                case NavStamp.Lethal:
                    Region(grids.Lethal, instance, width, height, 1);
                    break;
                case NavStamp.DoorLeft:
                    Region(grids.Door, instance, width, height, NavConstants.DoorLeft);
                    break;
                case NavStamp.DoorRight:
                    Region(grids.Door, instance, width, height, NavConstants.DoorRight);
                    break;
            }
        }

        foreach (var instance in instances)
        {
            if (instance is { HasBox: true, Stamp: NavStamp.Gate }
                && instance.GateCode is NavConstants.GateTeamRed or NavConstants.GateTeamBlue)
            {
                GateRegion(grids.Gate, instance, width, height);
            }
        }

        foreach (var instance in instances)
        {
            if (instance is { HasBox: true, Stamp: NavStamp.Gate }
                && instance.GateCode is NavConstants.GateIntelRed or NavConstants.GateIntelBlue)
            {
                GateRegion(grids.Gate, instance, width, height);
            }
        }

        foreach (var instance in instances)
        {
            if (instance is { HasBox: true, Stamp: NavStamp.Gate, GateCode: NavConstants.GateSetup })
                GateRegion(grids.Gate, instance, width, height);
        }
    }

    /// <summary>
    /// The integer position GM8 collides at, for a real one.
    /// </summary>
    /// <remarks>
    /// Round half to even, as <c>CollisionWorld.RoundPosition</c> does — restated
    /// rather than shared because a generator does not reference the simulation.
    /// ⚠️ There are three roundings in this codebase and they are not all the same
    /// one; the networking extension's is half away from zero.
    /// </remarks>
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.ToEven);

    private static void Region(byte[] grid, in NavInstance box, int width, int height, byte value)
    {
        const int Cell = NavConstants.CellSize;

        var x0 = Math.Max(0, (int)Math.Floor(box.Left / Cell));
        var y0 = Math.Max(0, (int)Math.Floor(box.Top / Cell));
        var x1 = Math.Min(width - 1, (int)Math.Floor(box.Right / Cell));
        var y1 = Math.Min(height - 1, (int)Math.Floor(box.Bottom / Cell));

        if (x0 > x1 || y0 > y1)
            return;

        for (var y = y0; y <= y1; y++)
            grid.AsSpan((y * width) + x0, x1 - x0 + 1).Fill(value);
    }

    /// <summary>
    /// A gate's footprint, dilated up and left by the character box.
    /// </summary>
    /// <remarks>
    /// <c>navGateStamp</c>. The question a gate cell answers is not "is this cell
    /// gate" but "would a character <em>anchored</em> here be standing in the
    /// gate" — and an anchor is the top-left of a
    /// <see cref="NavConstants.BoxW"/> x <see cref="NavConstants.BoxH"/> body.
    /// </remarks>
    private static void GateRegion(byte[] gate, in NavInstance box, int width, int height)
    {
        const int Cell = NavConstants.CellSize;

        var x0 = Math.Max(0, (int)Math.Floor(box.Left / Cell) - (NavConstants.BoxW - 1));
        var y0 = Math.Max(0, (int)Math.Floor(box.Top / Cell) - (NavConstants.BoxH - 1));
        var x1 = Math.Min(width - 1, (int)Math.Floor(box.Right / Cell));
        var y1 = Math.Min(height - 1, (int)Math.Floor(box.Bottom / Cell));

        if (x0 > x1 || y0 > y1)
            return;

        for (var y = y0; y <= y1; y++)
            gate.AsSpan((y * width) + x0, x1 - x0 + 1).Fill((byte)box.GateCode);
    }
}
