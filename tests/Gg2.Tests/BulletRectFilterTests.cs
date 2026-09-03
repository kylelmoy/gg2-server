using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Filtering the solid rects once for a whole arc answers what asking each
/// segment about every rect answers.
/// </summary>
/// <remarks>
/// <b>The gate on <see cref="CollisionWorld.BulletRectsIn"/>, and it exists
/// because nothing else is one.</b> <c>BotArc.Clear</c> tests ten segments of a
/// parabola and used to walk every solid rect on the map ten times, building a
/// <c>LineCollider</c> per rect per segment. It now filters the rects once
/// against the box that contains the whole arc.
/// <para>
/// ⛔ <b>The mutation that made this necessary:</b> making
/// <c>BulletRectsIn</c> keep only its first rect left <b>all 2,643 tests
/// green</b>. A bot would happily shoot through a spawn gate and no fixture
/// covers it — the parity traces are recorded firefights, not arcs across a
/// doorway. That is the same shape as the clearance skip's gate, and it is why
/// a performance change that "only filters" still needs one.
/// </para>
/// <para>
/// ⚠️ <b>The control is the production overload that takes no candidates</b>, not
/// a transcription. The two share <c>RectBlocks</c>, so what is under test is
/// exactly the filtering and nothing else.
/// </para>
/// </remarks>
public sealed class BulletRectFilterTests
{
    /// <summary>The arc's shape, from <c>BotConstants.ArcSamples</c>.</summary>
    private const int Segments = 10;

    /// <summary>
    /// Polylines swept across three maps get the same answer filtered and
    /// unfiltered.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Polylines rather than single segments</b>, because the filter is
    /// applied to the box round a whole arc and the interesting failure is a rect
    /// that one segment reaches and the enclosing box was thought not to.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort", 1)]
    [InlineData("ctf_2dfort", 1)]
    [InlineData("cp_dirtbowl", 2)]
    public void FilteringOnceForTheWholeArcChangesNoAnswer(string map, int area)
    {
        var world = ScenarioWorld.Load(map, area, ReferenceTree.IncludedFiles, null);
        var collision = world.Collision;
        var filter = CollisionFilter.ForBullet();

        var width = collision.Mask.WorldWidth;
        var height = collision.Mask.WorldHeight;

        var random = new Random(20260902);
        var px = new double[Segments + 1];
        var py = new double[Segments + 1];
        var candidates = new int[collision.RectCount];

        var blockedByRect = 0;
        var narrowed = 0;
        var arcs = 0;

        for (var a = 0; a < 4_000; a++)
        {
            // A parabola's shape: a launch direction, a speed and a sag, sampled
            // the way BotArc samples one.
            var sx = random.NextDouble() * width;
            var sy = random.NextDouble() * height;
            var angle = random.NextDouble() * 360;
            var speed = 6 + (random.NextDouble() * 8);
            var gravity = random.NextDouble() * 0.25;
            var flight = 10 + (random.NextDouble() * 50);

            var vx = Gml.LengthDirX(speed, angle);
            var vy = Gml.LengthDirY(speed, angle);

            px[0] = sx;
            py[0] = sy;

            for (var i = 1; i <= Segments; i++)
            {
                var t = flight * i / Segments;
                px[i] = sx + (vx * t);
                py[i] = sy + (vy * t) + (gravity * t * (t + 1) / 2);
            }

            var left = double.MaxValue;
            var top = double.MaxValue;
            var right = double.MinValue;
            var bottom = double.MinValue;

            for (var i = 0; i < px.Length; i++)
            {
                left = Math.Min(left, px[i]);
                right = Math.Max(right, px[i]);
                top = Math.Min(top, py[i]);
                bottom = Math.Max(bottom, py[i]);
            }

            var count = collision.BulletRectsIn(left, top, right, bottom, in filter, candidates);
            var span = candidates.AsSpan(0, count);

            if (count < collision.RectCount)
                narrowed++;

            arcs++;

            for (var i = 1; i < px.Length; i++)
            {
                var filtered = collision.CollisionLineBlocksBullets(
                    px[i - 1], py[i - 1], px[i], py[i], filter, span);

                var everyRect = collision.CollisionLineBlocksBullets(
                    px[i - 1], py[i - 1], px[i], py[i], filter);

                Assert.Equal(everyRect, filtered);

                // A segment the MAP does not block but a rect does: the only
                // case where dropping a candidate could change the answer.
                if (everyRect && !collision.CollisionLineHitsMask(px[i - 1], py[i - 1], px[i], py[i]))
                    blockedByRect++;
            }
        }

        // ⛔ Agreement is worthless if the rect path never decided anything. These
        // are properties, not thresholds fitted to the run: the sweep has to
        // contain segments that only a RECT blocks, and the filter has to be
        // discarding rects at least sometimes — otherwise it is a no-op that
        // agrees with itself.
        Assert.True(blockedByRect > 0, $"{map}: no segment was blocked by a rect alone");
        Assert.True(narrowed > 0, $"{map}: the filter never narrowed the rect set");
        Assert.True(collision.RectCount > 0, $"{map}: has no solid rects, so this proves nothing");
        Assert.Equal(4_000, arcs);
    }
}
