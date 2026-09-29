using GG2.Bots;
using GG2.Protocol;
using GG2.Maps;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The line walk's clearance skip against the same walk with the skip switched
/// off.
/// </summary>
/// <remarks>
/// <b>This is the gate on a performance change that claims to be exact.</b>
/// <c>CollisionLine.Walk</c> samples one pixel per pixel of the segment, which is
/// what made a soldier's arc test — ten segments of a rocket's whole flight —
/// 9% of a twelve-bot tick. It now skips open ground using
/// <see cref="Walkmask.Clearance"/>, and that is only legitimate if the answer
/// does not move.
/// <para>
/// <b>The parity fixtures cannot be this gate and passing them proves
/// nothing here.</b> A hitscan in a recorded trace is a short line between two
/// characters in a firefight; the skip only engages over open ground, and an
/// over-long skip would step over a thin ledge in the middle of a map that no
/// trace ever shoots across.
/// </para>
/// <para>
/// <b>The control is the production walk with a mask that refuses to
/// promise</b>, not a transcription of it. <see cref="NoSkip"/> answers
/// <c>IsSolid</c> identically and returns zero from
/// <c>SafeSkipPixels</c>, which is the documented "walk pixel by pixel" answer —
/// so the two runs differ in the skip and in nothing else, and there is no second
/// copy of the rounding, the major-axis choice or the cell mapping to go stale.
/// </para>
/// </remarks>
public sealed class CollisionLineSkipTests
{
    /// <summary>
    /// The walkmask with the skip refused, counting the probes it is asked for.
    /// See the remarks on the class.
    /// </summary>
    /// <remarks>
    /// The count is the witness that the skip is doing anything at all. Two
    /// runs that agree prove nothing if the fast one never skipped, and no
    /// arrangement of segments can fake a probe count.
    /// </remarks>
    private readonly struct Counting(Walkmask mask, int[] probes, bool skip) : CollisionLine.IMask
    {
        public bool IsSolid(int x, int y)
        {
            probes[0]++;
            return mask.IsSolid(x, y);
        }

        public int SafeSkipPixels(int x, int y)
        {
            if (!skip || (uint)x >= (uint)mask.Width || (uint)y >= (uint)mask.Height)
                return 0;

            return Walkmask.SafeSkipPixels(mask.Clearance[(y * mask.Width) + x]);
        }
    }

    /// <summary>
    /// Segments swept across three whole maps agree with the unskipped walk.
    /// </summary>
    /// <remarks>
    /// <b>Random endpoints over the whole map rather than a fixture's two
    /// positions.</b> The skip is worth most in the open and is most likely to be
    /// wrong where the open ends, so a shot between two characters standing on a
    /// floor exercises neither. Three maps, including an area-narrowed one,
    /// because the clearance field is built per mask.
    /// </remarks>
    [Theory]
    [InlineData("gg_debug", 1)]
    [InlineData("ctf_truefort", 1)]
    [InlineData("cp_dirtbowl", 2)]
    public void TheSkipAgreesWithTheUnskippedWalk(string map, int area)
    {
        var world = ScenarioWorld.Load(map, area, ReferenceTree.MapsDirectory, null);
        var collision = world.Collision;
        var mask = collision.Mask;
        var collider = collision.MapCollider;

        // Deliberately wider than the map, so segments that leave it and come
        // back are covered too — off-mask carries no clearance and is the one
        // place the skip must refuse.
        var width = mask.WorldWidth;
        var height = mask.WorldHeight;

        var random = new Random(20260902);
        var blocked = 0;
        var open = 0;
        var walked = new int[1];
        var skippedProbes = new int[1];

        for (var i = 0; i < 20_000; i++)
        {
            var x1 = (random.NextDouble() * width * 1.2) - (width * 0.1);
            var y1 = (random.NextDouble() * height * 1.2) - (height * 0.1);

            // A mix of short shots and map-crossing ones: the skip does nothing
            // on a ten-pixel line and everything on a five-hundred-pixel one.
            var reach = (i % 3) switch
            {
                0 => 24.0,
                1 => 160.0,
                _ => 900.0,
            };

            var angle = random.NextDouble() * 360;
            var x2 = x1 + Gml.LengthDirX(reach, angle);
            var y2 = y1 + Gml.LengthDirY(reach, angle);

            // The production lookup, not the counting one, so what ships is
            // what is under test. The counting pair below is the control and the
            // witness.
            var shipped = CollisionLine.Hits(
                collider, new CollisionWorld.WalkmaskLookup(mask), x1, y1, x2, y2);

            var skipped = CollisionLine.Hits(
                collider, new Counting(mask, skippedProbes, skip: true), x1, y1, x2, y2);

            var naive = CollisionLine.Hits(
                collider, new Counting(mask, walked, skip: false), x1, y1, x2, y2);

            Assert.Equal(naive, shipped);
            Assert.Equal(naive, skipped);

            if (naive)
                blocked++;
            else
                open++;
        }

        // Every case above agrees, and agreement is worthless if the sample is
        // degenerate. Both bars below are PROPERTIES rather than thresholds fitted
        // to what the run happened to produce — the first says the sweep asks the
        // question both ways round, the second says the thing under test is
        // switched on.
        Assert.True(blocked > 0 && open > 0, $"{map}: {blocked} blocked, {open} open");

        // The witness, and the reason this test is not vacuous: the skipping
        // walk must probe the mask far less than the pixel-by-pixel one. A skip
        // that never engaged would make these equal and every Assert.Equal above
        // would still pass.
        Assert.True(
            skippedProbes[0] * 2 < walked[0],
            $"{map}: the skip saved nothing — {skippedProbes[0]} probes against {walked[0]}");
    }

    /// <summary>
    /// The bound is shared with the geometry fan rather than written twice.
    /// </summary>
    /// <remarks>
    /// Two readers march this mask now. The fan's own gate,
    /// <c>ObservationRayFanTests</c>, proves the bound is safe; this proves the
    /// walk is reading the same one, which is the part that could rot silently
    /// when somebody "tidies" a duplicate constant.
    /// </remarks>
    [Fact]
    public void TheWalkAndTheGeometryFanShareOneBound()
    {
        for (var clearance = 0; clearance <= 32; clearance++)
            Assert.Equal(Walkmask.SafeSkipPixels(clearance), ObservationWriter.SafeSkipPixels(clearance));
    }
}
