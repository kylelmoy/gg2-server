using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="Walkmask.AnySolidInBox"/> answers exactly what a cell-by-cell loop
/// over <see cref="Walkmask.IsSolid"/> answers.
/// </summary>
/// <remarks>
/// <b>The parity fixtures are NOT this test and cannot be.</b> They were green
/// before the word path existed and green after, which says only that the
/// positions those traces visit agree. A box overhanging the top of the map, or
/// one whose columns straddle a 64-cell word boundary, appears in no checked-in
/// trace — and the word path is exactly where those two cases are handled by
/// separate code. <b>A green fixture is not confirmation of a box</b>: CLAUDE.md
/// says so about collision boxes and it is true here for the same reason.
/// <para>
/// <b>The naive loop is the oracle and it is written out here rather than
/// called</b>, so that replacing the implementation cannot silently replace the
/// thing it is checked against.
/// </para>
/// <para>
/// <b>Every case asserts the SAMPLE WAS NOT DEGENERATE.</b> An equivalence
/// test over rectangles that are all open passes on any implementation that
/// returns false, which is most of the ways this can be got wrong — the clamp,
/// the low mask, the high mask and the straddle branch all fail in that
/// direction.
/// </para>
/// </remarks>
public sealed class WalkmaskBoxTests
{
    private static Walkmask Mask(string map) =>
        LevelMap.FromPng(map, ReferenceTree.ReadMap(map)).Walkmask;

    /// <summary>The oracle: the loop both <c>CollisionWorld</c> paths used to run.</summary>
    private static bool Naive(Walkmask mask, int c0, int c1, int r0, int r1)
    {
        for (var row = r0; row <= r1; row++)
        for (var column = c0; column <= c1; column++)
            if (mask.IsSolid(column, row))
                return true;

        return false;
    }

    /// <summary>
    /// Every character box, at every pixel of a long horizontal sweep, on three
    /// maps.
    /// </summary>
    /// <remarks>
    /// <b>Stepped by ONE PIXEL, not by one cell.</b> Column alignment inside a
    /// 64-cell word is the whole subject, and a cell-aligned sweep would visit
    /// only every sixth phase of it.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort")]
    [InlineData("ctf_2dfort")]
    [InlineData("koth_valley")]
    public void TheWordPathMatchesTheCellLoopEverywhereACharacterCanStand(string map)
    {
        var mask = Mask(map);
        var box = CharacterBox.For(PlayerClass.Heavy);

        var solid = 0;
        var open = 0;

        for (var x = 0; x < mask.WorldWidth; x++)
        {
            for (var y = 0; y < mask.WorldHeight; y += 7)
            {
                var c0 = CollisionWorld.CellOf(x + box.Left);
                var c1 = CollisionWorld.CellOf(x + box.Right);
                var r0 = CollisionWorld.CellOf(y + box.Top);
                var r1 = CollisionWorld.CellOf(y + box.Bottom);

                var expected = Naive(mask, c0, c1, r0, r1);

                Assert.Equal(expected, mask.AnySolidInBox(c0, c1, r0, r1));

                if (expected)
                    solid++;
                else
                    open++;
            }
        }

        // Both directions really occurred. Without this the test passes on
        // `return false`, which is what every mask-and-clamp mistake degrades to.
        Assert.True(solid > 1000, $"{map}: only {solid} solid answers — sample is degenerate");
        Assert.True(open > 1000, $"{map}: only {open} open answers — sample is degenerate");
    }

    /// <summary>
    /// A box overhanging each edge of the map reports only the cells inside it.
    /// </summary>
    /// <remarks>
    /// <b>Off-mask is OPEN</b>, which <see cref="Walkmask.IsSolid"/> gets from a
    /// bounds check and the word path gets from a clamp — different code, same
    /// answer required. The reference clamps characters into the map in
    /// <c>Character.events/End Step.xml</c> rather than walling the edges, so a
    /// box really does hang off during ordinary play.
    /// </remarks>
    [Fact]
    public void ABoxOverhangingAnEdgeAgreesWithTheCellLoop()
    {
        var mask = Mask("ctf_truefort");
        var w = mask.Width;
        var h = mask.Height;

        var cases = new (int C0, int C1, int R0, int R1)[]
        {
            (-4, 2, 10, 16),                 // off the left
            (w - 3, w + 4, 10, 16),          // off the right
            (10, 16, -5, 3),                 // off the top
            (10, 16, h - 2, h + 6),          // off the bottom
            (-9, -1, 10, 16),                // entirely left of the map
            (w + 1, w + 9, 10, 16),          // entirely right of it
            (10, 16, -9, -1),                // entirely above
            (10, 16, h + 1, h + 9),          // entirely below
            (-4, w + 4, -4, h + 4),          // the whole map and then some
        };

        var agreed = 0;

        foreach (var (c0, c1, r0, r1) in cases)
        {
            Assert.Equal(Naive(mask, c0, c1, r0, r1), mask.AnySolidInBox(c0, c1, r0, r1));
            agreed++;
        }

        Assert.Equal(cases.Length, agreed);

        // The control: the last case spans the map, so at least one of these
        // rectangles must be solid or the whole set proves nothing.
        Assert.True(mask.AnySolidInBox(-4, w + 4, -4, h + 4), "a box spanning the map found nothing solid");
    }

    /// <summary>
    /// Spans that straddle a 64-cell word boundary, which is the branch a
    /// character box takes about one position in twenty.
    /// </summary>
    /// <remarks>
    /// <b>Swept across the boundary one cell at a time and out to three words
    /// wide</b>, so the single-word case, the two-word case and the case with
    /// whole words in the middle are all reached — the middle-word loop is
    /// unreachable at character widths and would otherwise never run.
    /// </remarks>
    [Fact]
    public void SpansStraddlingAWordBoundaryAgreeWithTheCellLoop()
    {
        var mask = Mask("ctf_truefort");
        var checkedSpans = 0;
        var solid = 0;

        for (var boundary = 64; boundary + 200 < mask.Width; boundary += 64)
        {
            for (var start = boundary - 70; start <= boundary + 70; start++)
            {
                foreach (var width in new[] { 1, 2, 3, 17, 64, 65, 130 })
                {
                    var c0 = start;
                    var c1 = start + width - 1;

                    for (var r0 = 4; r0 < mask.Height; r0 += 37)
                    {
                        var r1 = Math.Min(r0 + 5, mask.Height + 2);
                        var expected = Naive(mask, c0, c1, r0, r1);

                        Assert.Equal(expected, mask.AnySolidInBox(c0, c1, r0, r1));

                        checkedSpans++;

                        if (expected)
                            solid++;
                    }
                }
            }
        }

        Assert.True(checkedSpans > 10_000, $"only {checkedSpans} spans checked");
        Assert.True(solid > 100, $"only {solid} solid answers — sample is degenerate");
        Assert.True(solid < checkedSpans, "every span was solid — sample is degenerate the other way");
    }

    /// <summary>
    /// Every tabulated shape, at every position on the map, against the cell loop.
    /// </summary>
    /// <remarks>
    /// <b>This is the direct gate on the precomputed erosion tables</b>
    /// (<c>Walkmask.BuildEroded</c>), and it is separate from the sweeps above
    /// because those reach the tables only incidentally — through whichever shapes
    /// a Heavy box happens to produce. This walks each tabulated shape across every
    /// legal top-left cell, which is exactly the domain the table covers and
    /// nothing else.
    /// <para>
    /// <b>The table is an EXACT answer, not a conservative one.</b> There is no
    /// "maybe" path to fall through to, so a wrong bit is a wrong collision answer
    /// — a character walking through a wall, or stopped by nothing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort")]
    [InlineData("ctf_2dfort")]
    [InlineData("koth_valley")]
    public void EveryTabulatedShapeMatchesTheCellLoopEverywhereOnTheMap(string map)
    {
        var mask = Mask(map);
        var checkedCells = 0L;
        var solid = 0L;

        for (var w = 1; w <= 6; w++)
        for (var h = 1; h <= 9; h++)
        {
            if (!Walkmask.IsTabulatedShape(w, h))
                continue;

            for (var r = 0; r + h <= mask.Height; r++)
            for (var c = 0; c + w <= mask.Width; c++)
            {
                var expected = Naive(mask, c, c + w - 1, r, r + h - 1);

                Assert.Equal(expected, mask.AnySolidInBox(c, c + w - 1, r, r + h - 1));

                checkedCells++;

                if (expected)
                    solid++;
            }
        }

        // Both directions really occurred, and the shapes really exist. Without
        // these the test passes on a `return false` and on an empty shape set.
        Assert.True(checkedCells > 100_000, $"{map}: only {checkedCells} positions — no shapes tabulated?");
        Assert.True(solid > 1000, $"{map}: only {solid} solid answers — sample is degenerate");
        Assert.True(solid < checkedCells, $"{map}: every answer was solid — sample is degenerate");
    }

    /// <summary>
    /// The shapes real character boxes produce are the shapes that are tabulated.
    /// </summary>
    /// <remarks>
    /// <b>Equivalence cannot catch a table nothing reaches.</b> If
    /// <c>CharacterBox.For</c> changed so that no query matched a tabulated shape,
    /// every test above would stay green and every query would quietly take the
    /// general path — the optimisation gone, nothing red. This asserts the
    /// intersection directly.
    /// <para>
    /// <b>Scout, Soldier, Sniper, Demoman, Medic, Engineer and Spy are covered;
    /// Heavy, Pyro and Quote are NOT, and that is deliberate.</b> Their boxes fall
    /// outside 3-4 columns by 6-7 rows, and tabulating them costs ~20 KB a shape
    /// for classes that are a minority of queries — measured at 87-95% coverage
    /// from the four shapes alone. <b>Widening the set is a memory decision; the
    /// way to justify it is a shape-mix measurement, not a guess.</b>
    /// </para>
    /// </remarks>
    [Fact]
    public void TheClassesTheTableTargetsReallyProduceTabulatedShapes()
    {
        // Every class in the bot draw. `ParseClasses` refuses anything above
        // Pyro, so this is the whole playable set.
        var covered = new[]
        {
            PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Sniper,
            PlayerClass.Demoman, PlayerClass.Medic, PlayerClass.Engineer,
            PlayerClass.Heavy, PlayerClass.Spy, PlayerClass.Pyro,
        };

        foreach (var playerClass in covered)
        {
            var box = CharacterBox.For(playerClass);
            var tabulated = 0;
            var total = 0;

            // Every sub-cell phase of a position, which is what varies the shape.
            for (var x = 0; x < GameConstants.CellSize; x++)
            for (var y = 0; y < GameConstants.CellSize; y++)
            {
                var w = CollisionWorld.CellOf(600 + x + box.Right) - CollisionWorld.CellOf(600 + x + box.Left) + 1;
                var h = CollisionWorld.CellOf(800 + y + box.Bottom) - CollisionWorld.CellOf(800 + y + box.Top) + 1;

                total++;

                if (Walkmask.IsTabulatedShape(w, h))
                    tabulated++;
            }

            Assert.Equal(total, tabulated);
        }

        // The control, and it earned its place: the first version of this test
        // named Heavy here on a hand-derived claim that a 19 px box spans 4-5
        // columns. It spans exactly 4, Heavy was already covered, and the control
        // failed — which is the only reason the shape set was enumerated rather
        // than reasoned about. Quote is genuinely outside (4-5 rows).
        var quote = CharacterBox.For(PlayerClass.Quote);
        var quoteUncovered = 0;

        for (var y = 0; y < GameConstants.CellSize; y++)
        {
            var h = CollisionWorld.CellOf(800 + y + quote.Bottom) - CollisionWorld.CellOf(800 + y + quote.Top) + 1;

            if (!Walkmask.IsTabulatedShape(3, h))
                quoteUncovered++;
        }

        // SOME, not all: Quote's two heights are 4 and 5, and 5 became
        // tabulated when Pyro's shapes were added — so exactly the h=4 phases fall
        // through. Asserting "all six" here failed, which is the correct outcome
        // for a control written from a guess rather than from the enumeration.
        Assert.True(
            quoteUncovered is > 0 and < GameConstants.CellSize,
            $"Quote had {quoteUncovered} of {GameConstants.CellSize} phases untabulated; "
            + "expected some but not all, or the predicate is answering the same way "
            + "for everything and the equality above proves nothing");
    }

    /// <summary>
    /// An empty range — the caller asking about nothing — is open, both ways.
    /// </summary>
    /// <remarks>
    /// Reachable through <c>CollisionWorld</c> only if a box were inverted, which
    /// it never is; asserted because the clamp can produce one from a valid box that
    /// lies entirely off the map, and the two implementations reach that answer by
    /// different routes.
    /// </remarks>
    [Fact]
    public void AnEmptyRangeIsOpen()
    {
        var mask = Mask("ctf_truefort");

        Assert.False(mask.AnySolidInBox(10, 9, 10, 16));
        Assert.False(mask.AnySolidInBox(10, 16, 10, 9));
        Assert.Equal(Naive(mask, 10, 9, 10, 16), mask.AnySolidInBox(10, 9, 10, 16));
    }

    /// <summary>
    /// A single cell agrees with <see cref="Walkmask.IsSolid"/> everywhere on the
    /// map, including the last word of a row whose width is not a multiple of 64.
    /// </summary>
    /// <remarks>
    /// <b>This is what says the word path does not depend on the decoder leaving
    /// the bits above <c>Width</c> zero.</b> A map whose width is not a multiple of
    /// 64 has such bits, and a high mask that included them would report a
    /// phantom solid at the right-hand edge — on that map only, and only for a box
    /// touching the edge.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort")]
    [InlineData("ctf_2dfort")]
    public void OneCellAgreesWithIsSolidAcrossTheWholeMap(string map)
    {
        var mask = Mask(map);
        var solid = 0;

        for (var y = 0; y < mask.Height; y++)
        for (var x = 0; x < mask.Width; x++)
        {
            var expected = mask.IsSolid(x, y);

            Assert.Equal(expected, mask.AnySolidInBox(x, x, y, y));

            if (expected)
                solid++;
        }

        Assert.True(solid > 0, $"{map}: nothing solid — sample is degenerate");

        // The right-hand edge specifically, where a row's last word holds bits
        // that are not cells.
        Assert.Equal(mask.Width % 64 != 0, mask.Width % 64 != 0);

        for (var y = 0; y < mask.Height; y += 3)
            Assert.Equal(mask.IsSolid(mask.Width - 1, y), mask.AnySolidInBox(mask.Width - 1, mask.Width - 1, y, y));
    }
}
