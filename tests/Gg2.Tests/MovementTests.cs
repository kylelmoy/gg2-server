using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The movement primitives and the solver, on geometry built for the purpose.
/// </summary>
/// <remarks>
/// Synthetic maps rather than shipped ones: a flat floor with a known lip is a
/// far better test of step-up than hunting for one on <c>ctf_truefort</c>, and
/// it says what it is testing. The <em>numbers</em> these produce are pinned
/// against the real game by the parity harness once phase 4 lands; what is
/// pinned here is the behaviour — that a one-cell lip is walkable and a
/// two-cell one is not.
/// </remarks>
public sealed class MovementTests
{
    private const int Cell = 6;

    /// <summary>
    /// Builds a map from an ASCII picture, one character per mask cell.
    /// <c>#</c> is solid, anything else is open.
    /// </summary>
    private static CollisionWorld World(params string[] rows)
    {
        var height = rows.Length;
        var width = rows[0].Length;
        Assert.All(rows, r => Assert.Equal(width, r.Length));

        // The walkmask bitstream: row-major, six bits per character, most
        // significant first, each stored as chr(value + 32).
        var bits = new System.Text.StringBuilder();
        var value = 0;
        var filled = 0;

        foreach (var row in rows)
        {
            foreach (var c in row)
            {
                value = (value << 1) | (c == '#' ? 1 : 0);
                if (++filled != 6)
                    continue;

                bits.Append((char)(value + 32));
                value = 0;
                filled = 0;
            }
        }

        if (filled > 0)
        {
            value <<= 6 - filled;
            bits.Append((char)(value + 32));
        }

        var levelData = string.Join('\n',
            "{WALKMASK}", width.ToString(), height.ToString(), bits.ToString(), "{END WALKMASK}",
            "{ENTITIES}", "[]", "{END ENTITIES}");

        return new CollisionWorld(LevelMap.FromLevelData("synthetic", levelData, width, height));
    }

    private static CharacterBox Scout => CharacterBox.For(PlayerClass.Scout);

    private static CollisionFilter Filter => CollisionFilter.Everything;

    /// <summary>
    /// A character standing on a floor, at the given cell column, with its feet
    /// exactly on top of the floor row.
    /// </summary>
    private static MoverState StandingOn(int cellColumn, int floorRow, in CharacterBox box) =>
        StandingAt(cellColumn * Cell + 3, floorRow, box);

    /// <summary>
    /// The same, at an exact world x — for tests that need the character a known
    /// distance from something.
    /// </summary>
    private static MoverState StandingAt(double x, int floorRow, in CharacterBox box) => new()
    {
        X = x,
        // Bottom offset is inclusive, so the last covered pixel is y + Bottom;
        // the floor's first solid pixel is floorRow * Cell.
        Y = floorRow * Cell - box.Bottom - 1,
    };

    /// <summary>
    /// The x at which the character's right edge is exactly one pixel clear of
    /// the cell column <paramref name="wallColumn"/>.
    /// </summary>
    private static double JustLeftOf(int wallColumn, in CharacterBox box) =>
        wallColumn * Cell - box.Right - 1;

    [Fact]
    public void GoodMoveContactSolidStopsAtAWall()
    {
        // Twelve cells of floor with a wall two cells from the right.
        var world = World(
            "............",
            "............",
            "............",
            "............",
            "............",
            "..........#.",
            "############");

        var mover = StandingOn(2, 6, Scout);
        var startX = mover.X;

        var moved = Movement.GoodMoveContactSolid(
            world, Scout, Filter, ref mover, directionDegrees: 0, maxDistance: 60, out var contact);

        Assert.True(contact.Happened, "expected to hit the wall");
        Assert.True(moved > 0, "expected to move at all");
        Assert.True(moved < 60, $"expected to be stopped short of 60, moved {moved}");
        Assert.True(mover.X > startX);

        // And it ended up somewhere legal.
        Assert.True(world.PlaceFree(Scout, mover.X, mover.Y, Filter));
    }

    [Fact]
    public void GoodMoveContactSolidCoversTheWholeDistanceWhenNothingIsInTheWay()
    {
        var world = World(
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "####################");

        var mover = StandingOn(2, 6, Scout);
        var moved = Movement.GoodMoveContactSolid(
            world, Scout, Filter, ref mover, directionDegrees: 0, maxDistance: 30, out var contact);

        Assert.False(contact.Happened);
        Assert.Equal(30, moved, 6);
    }

    [Fact]
    public void GoodMoveContactSolidDoesNothingForZeroDistance()
    {
        var world = World("......", "######");
        var mover = StandingOn(2, 1, Scout);
        var before = mover;

        Assert.Equal(0, Movement.GoodMoveContactSolid(
            world, Scout, Filter, ref mover, 0, 0, out var contact));

        Assert.False(contact.Happened);
        Assert.Equal(before.X, mover.X);
    }

    /// <summary>
    /// A one-cell lip is walkable: the solver lifts the character over it.
    /// Six pixels is one mask cell, which is what makes this the boundary.
    /// </summary>
    [Fact]
    public void OneCellLipIsSteppedUp()
    {
        var world = World(
            "..............",
            "..............",
            "..............",
            "..............",
            "..............",
            "..............",
            ".......#######",
            "##############");

        // Right up against the lip, so the step happens within one tick of
        // movement rather than the character running out of speed first.
        var mover = StandingAt(JustLeftOf(7, Scout), 7, Scout);
        mover.HSpeed = 8;

        var startY = mover.Y;
        CharacterHitObstacle.Resolve(world, Scout, Filter, ref mover);

        Assert.Equal(startY - Cell, mover.Y, 6);
        Assert.True(world.PlaceFree(Scout, mover.X, mover.Y, Filter));
        Assert.NotEqual(0, mover.HSpeed);
    }

    /// <summary>
    /// Two cells is a wall, not a step: horizontal movement stops dead.
    /// </summary>
    [Fact]
    public void TwoCellLipIsAWall()
    {
        var world = World(
            "..............",
            "..............",
            "..............",
            "..............",
            "..............",
            ".......#######",
            ".......#######",
            "##############");

        var mover = StandingAt(JustLeftOf(7, Scout), 7, Scout);
        mover.HSpeed = 8;

        var startY = mover.Y;
        CharacterHitObstacle.Resolve(world, Scout, Filter, ref mover);

        Assert.Equal(0, mover.HSpeed);
        Assert.Equal(startY, mover.Y, 6);
    }

    /// <summary>
    /// Landing on a floor zeroes vertical speed and resets <c>moveStatus</c>;
    /// hitting a ceiling zeroes the speed but leaves <c>moveStatus</c> alone,
    /// because being knocked into a ceiling should not end a rocket jump.
    /// </summary>
    [Fact]
    public void FloorResetsMoveStatusAndCeilingDoesNot()
    {
        var floor = World(
            "........",
            "........",
            "........",
            "........",
            "........",
            "........",
            "........",
            "########");

        var falling = StandingOn(3, 7, Scout);
        falling.Y -= 10;
        falling.VSpeed = 14;
        falling.MoveStatus = 1;

        CharacterHitObstacle.Resolve(floor, Scout, Filter, ref falling);

        Assert.Equal(0, falling.VSpeed);
        Assert.Equal(0, falling.MoveStatus);

        var ceiling = World(
            "########",
            "........",
            "........",
            "........",
            "........",
            "........",
            "........",
            "########");

        var rising = StandingOn(3, 7, Scout);
        rising.VSpeed = -14;
        rising.MoveStatus = 1;

        CharacterHitObstacle.Resolve(ceiling, Scout, Filter, ref rising);

        Assert.Equal(0, rising.VSpeed);
        Assert.Equal(1, rising.MoveStatus);
    }

    /// <summary>
    /// A character that starts inside a wall is pushed out, and ends up
    /// somewhere legal.
    /// </summary>
    [Fact]
    public void StuckInAWallSlidesOut()
    {
        // A thin pillar, with room either side. The escape allowance is half the
        // box - six pixels sideways for a scout - so the wall has to be
        // genuinely thin or nothing can get out of it, which is the reference's
        // behaviour too.
        var world = World(
            "..............",
            "..............",
            "..............",
            "......#.......",
            "......#.......",
            "..............",
            "..............",
            "##############");

        // Overlapping the pillar by a pixel and clear of the floor: the escape
        // allowance is half the box width, so a character cannot climb out of
        // anything thicker than that - which is the reference's behaviour too.
        var mover = new MoverState { X = 5 * Cell, Y = 3 * Cell };
        Assert.False(world.PlaceFree(Scout, mover.X, mover.Y, Filter), "the test needs to start stuck");

        CharacterHitObstacle.Resolve(world, Scout, Filter, ref mover);

        Assert.True(world.PlaceFree(Scout, mover.X, mover.Y, Filter),
            $"still stuck at ({mover.X}, {mover.Y})");
    }

    /// <summary>
    /// Buried too deep to escape, the position is restored rather than left
    /// somewhere arbitrary.
    /// </summary>
    [Fact]
    public void HopelesslyStuckIsLeftWhereItWas()
    {
        var world = World(
            "##########",
            "##########",
            "##########",
            "##########",
            "##########",
            "##########");

        var mover = new MoverState { X = 5 * Cell, Y = 3 * Cell };
        var before = mover;

        CharacterHitObstacle.Resolve(world, Scout, Filter, ref mover);

        Assert.Equal(before.X, mover.X);
        Assert.Equal(before.Y, mover.Y);
    }

    /// <summary>
    /// The solver terminates. The reference gives up after ten iterations and
    /// calls itself stuck, and geometry that provokes that must not spin.
    /// </summary>
    [Fact]
    public void SolverTerminatesOnAwkwardGeometry()
    {
        var world = World(
            "#.#.#.#.#.",
            ".#.#.#.#.#",
            "#.#.#.#.#.",
            ".#.#.#.#.#",
            "#.#.#.#.#.");

        var mover = new MoverState { X = 5 * Cell, Y = 2 * Cell, HSpeed = 15, VSpeed = 15 };

        // The assertion is simply that this returns.
        CharacterHitObstacle.Resolve(world, Scout, Filter, ref mover);
    }

    /// <summary>
    /// <c>point_direction</c> is measured counter-clockwise with y pointing
    /// down. Getting the sign wrong mirrors the whole game.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 10, 0, 0)]      // right
    [InlineData(0, 0, 0, -10, 90)]    // up, because y grows downward
    [InlineData(0, 0, -10, 0, 180)]   // left
    [InlineData(0, 0, 0, 10, 270)]    // down
    [InlineData(0, 0, 10, -10, 45)]
    public void PointDirectionUsesGm8Convention(double x1, double y1, double x2, double y2, double expected) =>
        Assert.Equal(expected, CharacterHitObstacle.PointDirection(x1, y1, x2, y2), 6);

    /// <summary>
    /// <c>move_outside_solid</c> steps whole pixels and keeps the fractional
    /// part of where it started — measured against the running game.
    /// </summary>
    [Fact]
    public void MoveOutsideSolidKeepsTheFractionalOffset()
    {
        var world = World(
            "....................",
            "....................",
            "....................",
            "###.................",
            "###.................",
            "###.................",
            "....................",
            "####################");

        var mover = new MoverState { X = 2 * Cell + 0.25, Y = 3 * Cell };
        Assert.False(world.PlaceFree(Scout, mover.X, mover.Y, Filter), "the test needs to start stuck");

        Movement.MoveOutsideSolid(world, Scout, Filter, ref mover, directionDegrees: 0, maxDistance: 40);

        Assert.True(world.PlaceFree(Scout, mover.X, mover.Y, Filter));

        // Whole pixels only, so the .25 survives.
        Assert.Equal(0.25, mover.X - Math.Floor(mover.X), 9);
    }
}
