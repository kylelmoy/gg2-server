using Gg2.Bots;
using Gg2.Env;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The geometry fan's skipping march against a transcription of the naive one.
/// </summary>
/// <remarks>
/// <b>This is the gate on a performance change that claims to be exact.</b>
/// The naive form of <c>ObservationWriter.WriteGeometry</c> probes the walkmask
/// every four pixels out to 320 — up to 80 probes a ray, 24 rays an observation,
/// and 88% of what writing one costs. The real writer instead skips open ground
/// using <see cref="Walkmask.Clearance"/>, which is only legitimate if the
/// answer does not move.
/// <para>
/// <b>"Identical" and "close enough" are different claims and only the first
/// one was made.</b> A ray value is a distance to the first solid probe; an
/// optimisation that skipped one thin ledge would shift a single float in a
/// 211-float vector, train perfectly happily, and be invisible to every other
/// test in this repo — the exact shape <c>CLAUDE.md</c> warns about. So the naive
/// loop is transcribed here and the two are compared bit for bit.
/// </para>
/// <para>
/// <b>Positions are swept across whole maps, not taken from a duel.</b> The
/// skip is worth most in the open and is most likely to be wrong near geometry,
/// so a fixture position — which is one arbitrary spot on a floor — would exercise
/// neither. Three maps, including a tall one, because the clearance field is
/// built per mask and an area-narrowed map has its own.
/// </para>
/// </remarks>
public sealed class ObservationRayFanTests
{
    /// <summary>
    /// Where the ray block starts, derived from the layout's public constants
    /// and its documented tail order: rays, then the patch, then the goal block.
    /// </summary>
    /// <remarks>
    /// Computed from the tail rather than by re-adding the head's blocks. The
    /// head's widths are private, and a test that restated them would be a second
    /// copy of the layout that goes stale silently — which is the failure this
    /// file exists to prevent, not one to introduce.
    /// </remarks>
    private static int RaysAt =>
        ObservationWriter.Length
        - (2 + 1 + ObservationWriter.GoalKinds)
        - (ObservationWriter.PatchSide * ObservationWriter.PatchSide)
        - ObservationWriter.Rays;

    [Theory]
    [InlineData("gg_debug", 1)]
    [InlineData("ctf_truefort", 1)]
    [InlineData("cp_dirtbowl", 2)]
    public void SkippingMarchEqualsTheNaiveOne(string map, int area)
    {
        var world = ScenarioWorld.Load(map, area, ReferenceTree.IncludedFiles, null);
        var mask = world.Collision.Mask;
        var slot = world.Place(PlayerClass.Soldier, Team.Red, 100, 100);

        var buffer = new float[ObservationWriter.Length];
        var expected = new double[ObservationWriter.Rays];
        var random = new Random(20260830);
        var compared = 0;

        for (var trial = 0; trial < 4000; trial++)
        {
            var x = random.NextDouble() * mask.WorldWidth;
            var y = random.NextDouble() * mask.WorldHeight;

            // Placed by writing the coordinates rather than by moving properly:
            // this is a question about the observation at a position, and letting
            // the physics choose the positions would only ever ask it on a floor.
            world.State.Characters[slot].X = x;
            world.State.Characters[slot].Y = y;

            var view = new WorldView(in world.State, world.Collision);

            ObservationWriter.Write(in view, slot, buffer);
            NaiveFan(world.Collision, x, y, expected);

            for (var ray = 0; ray < ObservationWriter.Rays; ray++)
            {
                // Exact equality, on the float the layout actually carries.
                // A tolerance here would pass the one thing being tested for.
                Assert.Equal((float)(expected[ray] / 320.0), buffer[RaysAt + ray]);
            }

            compared++;
        }

        Assert.Equal(4000, compared);
    }

    /// <summary>
    /// A ray fan whose clearance is never consulted, so the skip cannot be
    /// right by construction.
    /// </summary>
    /// <remarks>
    /// <b>Kept as a transcription of the loop that was replaced</b>, down to
    /// recomputing the trig per ray. If it is ever "tidied" to share the writer's
    /// direction table or its constants it stops being an independent statement
    /// of the answer and this file stops testing anything.
    /// </remarks>
    private static void NaiveFan(CollisionWorld collision, double originX, double originY, double[] into)
    {
        for (var i = 0; i < ObservationWriter.Rays; i++)
        {
            var angle = i * (2 * Math.PI / ObservationWriter.Rays);
            var dx = Math.Cos(angle);
            var dy = Math.Sin(angle);

            var distance = 320.0;

            for (var step = 4.0; step <= 320.0; step += 4.0)
            {
                if (collision.IsSolidAt(
                    (int)Math.Round(originX + (dx * step)),
                    (int)Math.Round(originY + (dy * step))))
                {
                    distance = step;
                    break;
                }
            }

            into[i] = distance;
        }
    }

    /// <summary>
    /// The clearance field says what it claims to: the Chebyshev cell distance
    /// to the nearest solid cell, capped.
    /// </summary>
    /// <remarks>
    /// <b>Checked against a brute-force search, not against itself.</b> The
    /// two-pass chamfer is the kind of code that is subtly wrong at a border and
    /// right everywhere else, and the ray test above would only notice if the
    /// error made it <em>over</em>-estimate. An under-estimate is invisible there
    /// — it just skips less — so it needs its own check, in both directions.
    /// </remarks>
    [Theory]
    [InlineData("gg_debug")]
    [InlineData("ctf_truefort")]
    public void ClearanceIsTheChebyshevDistanceToSolid(string map)
    {
        var world = ScenarioWorld.Load(map, 1, ReferenceTree.IncludedFiles, null);
        var mask = world.Collision.Mask;
        var clearance = mask.Clearance;
        var random = new Random(7);

        Assert.Equal(mask.Width * mask.Height, clearance.Length);

        for (var trial = 0; trial < 3000; trial++)
        {
            var x = random.Next(mask.Width);
            var y = random.Next(mask.Height);

            Assert.Equal(Brute(mask, x, y), clearance[(y * mask.Width) + x]);
        }
    }

    private static byte Brute(Walkmask mask, int x, int y)
    {
        for (var radius = 0; radius < Walkmask.MaxClearance; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                // Only the ring at exactly this radius; the inside was covered by
                // an earlier one.
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                    continue;

                if (mask.IsSolid(x + dx, y + dy))
                    return (byte)radius;
            }
        }

        return Walkmask.MaxClearance;
    }

    /// <summary>
    /// The FIRST write against a mask nothing has touched allocates nothing.
    /// </summary>
    /// <remarks>
    /// <b>This is a regression test for a bug that passed twice before it
    /// failed.</b> <see cref="Walkmask.Clearance"/> was lazy at first, so the
    /// first <c>Write</c> on a map built the field and allocated
    /// <c>Width * Height</c> bytes inside a method <c>docs/ENV.md</c> gates as
    /// zero-allocation. Because the mask is shared through
    /// <c>ScenarioResources</c>, whether the field already existed depended on
    /// <b>which test had run first</b> — so
    /// <c>ObservationWriterTests.WritingAnObservationAllocatesNothing</c> passed
    /// on two runs and failed on the third, with nothing changed between them.
    /// <para>
    /// <b><c>resources: null</c> is the whole point of this test.</b> Going
    /// through the cache would hand back a mask some other test had already
    /// warmed, which is exactly the condition that hid the bug. A fresh decode is
    /// the only way to ask the question this is asking, and that is why the
    /// uncached overload is worth keeping.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFirstWriteAgainstAFreshMaskAllocatesNothing()
    {
        // Uncached, so this really is a mask whose clearance field has never
        // been asked for.
        var world = ScenarioWorld.Load("ctf_2dfort", 1, ReferenceTree.IncludedFiles, null, resources: null);
        var slot = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var buffer = new float[ObservationWriter.Length];
        var view = new WorldView(in world.State, world.Collision);

        // Everything except the write itself: the JIT, the direction table's
        // static constructor, the buffer.
        ObservationWriter.Write(in view, slot, buffer);

        var fresh = ScenarioWorld.Load("ctf_orange", 1, ReferenceTree.IncludedFiles, null, resources: null);
        var freshSlot = fresh.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var freshView = new WorldView(in fresh.State, fresh.Collision);

        var before = GC.GetAllocatedBytesForCurrentThread();

        ObservationWriter.Write(in freshView, freshSlot, buffer);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// The skip's arithmetic, checked exhaustively rather than sampled: a probe
    /// moved by up to <c>6c - 7</c> pixels cannot leave the block of cells a
    /// clearance of <c>c</c> vouches for.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that actually pins the constant, and it replaced a
    /// map sweep that did not.</b> The sweep placed origins at random and compared
    /// against the naive fan; mutating the bound from <c>6c - 7</c> to
    /// <c>6c - 1</c> — six pixels too far — still passed it, because catching that
    /// needs a solid pixel to sit at exactly the offset the mutant over-reaches
    /// to, and 4,000 random positions on three maps never produced one. A gate
    /// that only fails on gross errors is not a gate on a bound.
    /// <para>
    /// So the property is stated as what it is: integer arithmetic over
    /// <see cref="CollisionWorld.CellOf"/>, with no map involved. Every clearance
    /// value, every position of a probe within its cell, every offset out to the
    /// full reach. <b>Mutating the bound by one pixel fails this immediately</b>,
    /// which is the sensitivity the sweep lacked.
    /// </para>
    /// <para>
    /// <b>And the bound is deliberately one pixel tighter than the algebra
    /// forces.</b> <c>6c - 6</c> is also safe; the march uses <c>6c - 7</c>
    /// because the tight value has no margin at all and buys a quarter of one
    /// probe. The tightness half below records that, so nobody "fixes" the
    /// constant upward believing it was a slip — and so anybody who raises it
    /// finds out at <c>6c - 5</c>, which is genuinely wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSkipCannotLeaveTheCellsClearanceVouchesFor()
    {
        for (int clearance = 2; clearance <= Walkmask.MaxClearance; clearance++)
        {
            var reach = ObservationWriter.SafeSkipPixels(clearance);

            // A clearance of c guarantees every cell within Chebyshev distance
            // c - 1 is open, so a probe that moves by `reach` must not reach a
            // cell further away than that.
            for (var remainder = 0; remainder < GameConstants.CellSize; remainder++)
            {
                // An arbitrary cell, plus where inside it the probe sits.
                var pixel = (1000 * GameConstants.CellSize) + remainder;
                var cell = CollisionWorld.CellOf(pixel);

                for (var offset = -reach; offset <= reach; offset++)
                {
                    var moved = Math.Abs(CollisionWorld.CellOf(pixel + offset) - cell);

                    Assert.True(
                        moved <= clearance - 1,
                        $"clearance {clearance} reaches {reach}px, but an offset of {offset} "
                        + $"from remainder {remainder} moved {moved} cells, past the {clearance - 1} vouched for");
                }
            }
        }
    }

    /// <summary>
    /// And the bound is not loose by much: one pixel past <c>6c - 6</c> can
    /// leave the vouched-for block.
    /// </summary>
    /// <remarks>
    /// <b>The other direction, so the pair brackets the constant.</b> Without
    /// this, <c>6c - 700</c> would satisfy the safety test perfectly and make the
    /// skip useless — a correctness gate that a do-nothing implementation passes
    /// is not measuring the thing anybody cares about.
    /// </remarks>
    [Fact]
    public void OnePixelPastTheAlgebraIsGenuinelyUnsafe()
    {
        var witnessed = false;

        for (int clearance = 2; clearance <= Walkmask.MaxClearance; clearance++)
        {
            var overreach = ObservationWriter.SafeSkipPixels(clearance) + 2;

            for (var remainder = 0; remainder < GameConstants.CellSize && !witnessed; remainder++)
            {
                var pixel = (1000 * GameConstants.CellSize) + remainder;
                var cell = CollisionWorld.CellOf(pixel);

                for (var offset = -overreach; offset <= overreach; offset++)
                {
                    if (Math.Abs(CollisionWorld.CellOf(pixel + offset) - cell) > clearance - 1)
                    {
                        witnessed = true;
                        break;
                    }
                }
            }
        }

        Assert.True(witnessed, "6c - 5 never left the vouched-for cells; the bound is looser than believed");
    }
}
