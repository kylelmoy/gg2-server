using System.Text.Json;
using System.Text.Json.Serialization;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="CollisionLine"/> against the running engine.
/// </summary>
/// <remarks>
/// The last of phase 5's three oracles and by far the least guessable: the
/// other two turned out to be one-line formulas, and this one is a clip, a
/// rounding and a pixel walk whose order decides the answer. It is the Rifle's
/// entire hitscan and it gates every rocket fired, so a wrong answer here is a
/// shot that hits a wall the reference shot through.
/// <para>
/// The fixture is 13,702 segments asked of the running game
/// (<c>node tools/sample-collision-line.js</c>): seven probe boxes at both
/// coordinate parities, at positive, fractional and negative image scales and
/// with an off-centre sprite origin; four classes of live bot at both facings;
/// and the map's own precise 6× mask. Each probe records the bounding box the
/// engine actually gave it, so a row is a self-contained statement about a
/// rectangle the engine named.
/// </para>
/// <para>
/// <b>Both parities of every edge are in here on purpose.</b> The walk rounds
/// half to even, so whether a coordinate ending in .5 reaches a box depends on
/// whether the pixel it would round to is even — and the two 1×1 probes, one at
/// an even coordinate and one at an odd one, are what make that visible. A
/// fixture with only the six-pixel bars would pass against three different
/// wrong rounding rules.
/// </para>
/// </remarks>
public sealed class CollisionLineSampleTests
{
    private sealed record Sprite
    {
        /// <summary>
        /// The sprite's full extent. Equal to the mask's for every sprite in
        /// the fixture — these are the runtime bars the game builds for map
        /// entities, and their masks fill them.
        /// </summary>
        [JsonPropertyName("width")] public required int Width { get; init; }

        [JsonPropertyName("height")] public required int Height { get; init; }

        [JsonPropertyName("originX")] public required int OriginX { get; init; }

        [JsonPropertyName("originY")] public required int OriginY { get; init; }

        /// <summary>The sprite's mask bounds, in sprite pixels: left, top, right, bottom.</summary>
        [JsonPropertyName("mask")] public required int[] Mask { get; init; }
    }

    private sealed record Probe
    {
        [JsonPropertyName("label")] public required string Label { get; init; }

        [JsonPropertyName("x")] public required double X { get; init; }

        [JsonPropertyName("y")] public required double Y { get; init; }

        [JsonPropertyName("xscale")] public required double XScale { get; init; }

        [JsonPropertyName("yscale")] public required double YScale { get; init; }

        /// <summary>Which entry of <see cref="Document.Sprites"/> is the mask.</summary>
        [JsonPropertyName("sprite")] public required string Sprite { get; init; }

        /// <summary>The instance bounding box the engine reported: left, top, right, bottom.</summary>
        [JsonPropertyName("box")] public required int[] Box { get; init; }

        /// <summary>One row per query: x1, y1, x2, y2, hit.</summary>
        [JsonPropertyName("rows")] public required double[][] Rows { get; init; }
    }

    private sealed record CharacterProbe
    {
        [JsonPropertyName("label")] public required string Label { get; init; }

        /// <summary>The class name, as the sampler's own table spells it.</summary>
        [JsonPropertyName("class")] public required string Class { get; init; }

        [JsonPropertyName("x")] public required double X { get; init; }

        [JsonPropertyName("y")] public required double Y { get; init; }

        /// <summary><c>image_xscale</c>: 1 facing right, -1 facing left.</summary>
        [JsonPropertyName("xscale")] public required int XScale { get; init; }

        /// <summary>The instance bounding box the engine reported.</summary>
        [JsonPropertyName("box")] public required int[] Box { get; init; }

        [JsonPropertyName("rows")] public required double[][] Rows { get; init; }
    }

    private sealed record Walkmask
    {
        [JsonPropertyName("box")] public required int[] Box { get; init; }

        [JsonPropertyName("rows")] public required double[][] Rows { get; init; }
    }

    private sealed record Document
    {
        [JsonPropertyName("map")] public required string Map { get; init; }

        [JsonPropertyName("sprites")] public required Dictionary<string, Sprite> Sprites { get; init; }

        [JsonPropertyName("probes")] public required Probe[] Probes { get; init; }

        [JsonPropertyName("characters")] public required CharacterProbe[] Characters { get; init; }

        [JsonPropertyName("walkmask")] public required Walkmask Walkmask { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "collision-line-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    private static Aabb ToAabb(int[] box) => new(box[0], box[1], box[2], box[3]);

    /// <summary>
    /// Every rectangle-masked probe agrees with the engine on every segment.
    /// </summary>
    /// <remarks>
    /// Rectangle masks are every collider in GG2 except the map — characters,
    /// gates, obstacles and bullet walls all use sprites whose mask shape is
    /// <c>RECTANGLE</c>, so the mask lookup is the bounds test and nothing else.
    /// </remarks>
    [Fact]
    public void RectangleMaskedProbesMatchTheEngine()
    {
        var document = Load();
        var checkedSamples = 0;

        foreach (var probe in document.Probes)
        {
            var sprite = document.Sprites[probe.Sprite];

            var collider = new LineCollider(
                probe.X,
                probe.Y,
                probe.XScale,
                probe.YScale,
                ToAabb(probe.Box),
                sprite.OriginX,
                sprite.OriginY,
                ToAabb(sprite.Mask));

            foreach (var row in probe.Rows)
            {
                var actual = CollisionLine.Hits(
                    collider, default(CollisionLine.FilledMask), row[0], row[1], row[2], row[3]);

                Assert.True(
                    actual == (row[4] != 0),
                    $"{probe.Label} ({row[0]}, {row[1]}) -> ({row[2]}, {row[3]}): " +
                    $"engine {row[4] != 0}, port {actual}");

                checkedSamples++;
            }
        }

        Assert.True(checkedSamples > 7000, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// A map's own solid rectangle presents the engine's collider, and answers
    /// the engine's own segments through it.
    /// </summary>
    /// <remarks>
    /// The test above pins the <em>algorithm</em> using colliders assembled from
    /// the fixture. This pins the <em>port's</em> path to one: every probe in
    /// that fixture is a literal <c>BulletWall</c> the sampler created and
    /// scaled, wearing the 6x60 runtime sprite that <b>every</b> solid a map
    /// places wears — gates, player walls and bullet walls alike. So each probe
    /// can be rebuilt through <see cref="SolidRect.FromEntity"/> and run through
    /// <see cref="LineCollider.ForSolidRect"/>, and the engine's recorded
    /// answers apply unchanged.
    /// <para>
    /// It is a real gate on two separate things. Rebuilding the probe checks
    /// that <c>FromEntity</c> reproduces the bounding box GM8 reported for the
    /// same instance — at scales of 1/6, 1, and 1.5 by 0.6, which is the range
    /// the shipped maps use. Running the rows checks that the collider handed to
    /// the walk maps world pixels to mask pixels the way the engine does, which
    /// is where the scale has to survive as a divisor rather than being folded
    /// into the bounds.
    /// </para>
    /// <para>
    /// The <c>dup</c>-sprite probe is excluded, and that exclusion is the point
    /// rather than a convenience: its sprite origin is (3, 30), and no level
    /// entity has a non-zero origin — measured 2026-08-25 across all eight solid
    /// level-entity object types. <see cref="LineCollider.ForSolidRect"/>
    /// hard-codes an origin of zero on the strength of that, so a probe that
    /// does not share the assumption has nothing to say about it.
    /// </para>
    /// </remarks>
    [Fact]
    public void SolidRectanglesPresentTheColliderTheEngineMeasured()
    {
        var document = Load();
        var checkedSamples = 0;
        var checkedProbes = 0;

        foreach (var probe in document.Probes)
        {
            var sprite = document.Sprites[probe.Sprite];

            if (sprite.OriginX != 0 || sprite.OriginY != 0)
                continue;

            var rect = SolidRect.FromEntity(
                probe.X,
                probe.Y,
                probe.XScale,
                probe.YScale,
                sprite.Width,
                sprite.Height,
                SolidKind.BulletWall);

            var collider = LineCollider.ForSolidRect(rect);

            Assert.True(
                collider.Bbox == ToAabb(probe.Box),
                $"{probe.Label}: engine box {ToAabb(probe.Box)}, port {collider.Bbox}");

            foreach (var row in probe.Rows)
            {
                var actual = CollisionLine.Hits(
                    collider, default(CollisionLine.FilledMask), row[0], row[1], row[2], row[3]);

                Assert.True(
                    actual == (row[4] != 0),
                    $"{probe.Label} ({row[0]}, {row[1]}) -> ({row[2]}, {row[3]}): " +
                    $"engine {row[4] != 0}, port {actual}");

                checkedSamples++;
            }

            checkedProbes++;
        }

        Assert.True(checkedProbes >= 5, $"only {checkedProbes} zero-origin probes");
        Assert.True(checkedSamples > 5000, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// Real characters of four classes, at both facings, agree with the engine.
    /// </summary>
    /// <remarks>
    /// The probes above are BulletWalls shaped to order, which pins the
    /// algorithm but says nothing about whether this repo hands a <em>real</em>
    /// character to it correctly. The Rifle's whole point is hitting
    /// characters, so this closes that gap with live bots: Scout for the
    /// ordinary box, Heavy for the widest, Quote for the one that is a
    /// different shape entirely, and Soldier.
    /// <para>
    /// It also checks the box <see cref="LineCollider.ForCharacter"/> builds
    /// against the one the engine reported, which is what would catch the
    /// facing inflation being applied to the wrong side or not at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void CharactersMatchTheEngine()
    {
        var document = Load();
        var checkedSamples = 0;

        foreach (var probe in document.Characters)
        {
            var playerClass = Enum.Parse<PlayerClass>(probe.Class, ignoreCase: true);
            var box = CharacterBox.For(playerClass);
            var collider = LineCollider.ForCharacter(box, probe.X, probe.Y, probe.XScale);

            Assert.True(
                collider.Bbox == ToAabb(probe.Box),
                $"{probe.Label}: engine box {ToAabb(probe.Box)}, port {collider.Bbox}");

            foreach (var row in probe.Rows)
            {
                var actual = CollisionLine.Hits(
                    collider, default(CollisionLine.FilledMask), row[0], row[1], row[2], row[3]);

                Assert.True(
                    actual == (row[4] != 0),
                    $"{probe.Label} ({row[0]}, {row[1]}) -> ({row[2]}, {row[3]}): " +
                    $"engine {row[4] != 0}, port {actual}");

                checkedSamples++;
            }
        }

        Assert.Equal(8, document.Characters.Length);
        Assert.True(checkedSamples > 4000, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// Facing decides which end of a character rounds away, and it takes a
    /// diagonal to see it.
    /// </summary>
    /// <remarks>
    /// GM8 <em>divides</em> the walk step by <c>image_xscale</c> to index the
    /// mask, so flipping the sign flips which end of the span is open:
    /// <c>[left, right + 1)</c> facing right against <c>(left - 1, right]</c>
    /// facing left. On a vertical or horizontal line that is invisible, because
    /// the walk rounds its endpoints to integers first and the two spans hold
    /// the same integers. It shows on a <b>diagonal</b>, where the minor axis
    /// interpolates to a fraction: a sample at x = 3009.5 floors to mask column
    /// 9 facing right and to -10 facing left. Every case below was read off a
    /// live Heavy at both facings in the same tick, not derived.
    /// <para>
    /// The corollary is the one to remember: the two pixels
    /// <c>image_xscale = -1</c> adds to <c>bbox_left</c> are <b>not</b>
    /// reachable. They are in the box the engine reports, the broad-phase check
    /// lets them through, and the mask lookup then rejects them. Sizing a
    /// hitscan off <c>bbox_left</c> would make a left-facing character two
    /// pixels wider to shoot at than the engine makes it.
    /// </para>
    /// </remarks>
    [Fact]
    public void FacingDecidesWhichEndRoundsAway()
    {
        var box = CharacterBox.For(PlayerClass.Heavy);

        var right = LineCollider.ForCharacter(box, 3000, 620, 1);
        var left = LineCollider.ForCharacter(box, 3000, 620, -1);

        static bool Hits(in LineCollider c, double x1, double y1, double x2, double y2) =>
            CollisionLine.Hits(c, default(CollisionLine.FilledMask), x1, y1, x2, y2);

        // The engine's boxes differ, and only on the left.
        Assert.Equal(new Aabb(2991, 608, 3009, 643), right.Bbox);
        Assert.Equal(new Aabb(2989, 608, 3009, 643), left.Bbox);

        // A near-vertical line grazing the right edge: its walk passes through
        // x = 3009.5, which is inside facing right and outside facing left.
        Assert.True(Hits(right, 3009, 600, 3010, 620));
        Assert.False(Hits(left, 3009, 600, 3010, 620));

        // And the mirror, grazing the left edge through x = 2990.5.
        Assert.False(Hits(right, 2991, 600, 2990, 620));
        Assert.True(Hits(left, 2991, 600, 2990, 620));

        // The two pixels the flip adds to bbox_left are in the box and out of
        // reach. A vertical line's x is whatever it rounds to, so these are the
        // engine's own answer for the columns the inflation invented.
        Assert.False(Hits(left, left.Bbox.Left, 600, left.Bbox.Left, 640));
        Assert.False(Hits(left, left.Bbox.Left + 1, 600, left.Bbox.Left + 1, 640));
        Assert.True(Hits(left, left.Bbox.Left + 2, 600, left.Bbox.Left + 2, 640));
    }

    /// <summary>
    /// The map's precise mask agrees with the engine, including off every edge.
    /// </summary>
    /// <remarks>
    /// The only probe whose mask is not a filled rectangle, so the only one that
    /// exercises the per-pixel lookup — and the one the Rifle actually asks
    /// about most often, because a wall is what usually stops a shot. The
    /// off-the-edge rows matter more than they look: the walk indexes the
    /// sprite directly, and a negative index that wrapped would report a wall in
    /// mid-air.
    /// </remarks>
    [Fact]
    public void TheWalkmaskMatchesTheEngine()
    {
        var document = Load();
        var world = new CollisionWorld(
            LevelMap.FromPng(document.Map, ReferenceTree.ReadMap(document.Map)));

        Assert.Equal(ToAabb(document.Walkmask.Box), world.MapCollider.Bbox);

        var checkedSamples = 0;

        foreach (var row in document.Walkmask.Rows)
        {
            var actual = world.CollisionLineHitsMask(row[0], row[1], row[2], row[3]);

            Assert.True(
                actual == (row[4] != 0),
                $"walkmask ({row[0]}, {row[1]}) -> ({row[2]}, {row[3]}): " +
                $"engine {row[4] != 0}, port {actual}");

            checkedSamples++;
        }

        Assert.True(checkedSamples > 1800, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// The fixture contains both answers on every probe, and by a wide margin.
    /// </summary>
    /// <remarks>
    /// A guard against the failure that made the <c>FULL_UPDATE</c> weapon block
    /// wrong for four phases: a fixture that only ever recorded one answer would
    /// pass against a function that always returned it. Asserted per probe, not
    /// in total, because the map rows alone would carry the total.
    /// </remarks>
    [Fact]
    public void EveryProbeRecordedBothAnswers()
    {
        var document = Load();

        foreach (var probe in document.Probes)
        {
            var hits = probe.Rows.Count(row => row[4] != 0);

            Assert.True(hits > probe.Rows.Length / 8, $"{probe.Label}: only {hits} hits");
            Assert.True(
                probe.Rows.Length - hits > probe.Rows.Length / 8,
                $"{probe.Label}: only {probe.Rows.Length - hits} misses");
        }
    }
}
