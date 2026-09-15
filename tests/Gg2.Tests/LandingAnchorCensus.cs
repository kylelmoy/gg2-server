using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// Where a nav node's anchor column sits relative to the pixels a body can
/// actually stand on, and where the in-air tracker therefore aims.
/// </summary>
/// <remarks>
/// A node's anchor is <see cref="NavGraph.ColumnWorldX"/> of its column, and the
/// follower treats that x as a place to be: it is the goal
/// <c>SetGoal</c> snaps to, it is what <c>EdgeFlights</c> starts and finishes a
/// flight at, and on an arc it is where <c>SteerJump</c> clamps <c>TrackX</c>.
/// <para>
/// <b>On a one-column node the anchor is one pixel past the last x a body can
/// stand at.</b> A soldier's box is <c>[x-6, x+6]</c> and needs one pixel of it
/// over floor; a single-column run's floor ends at <c>ColumnWorldX(X1) - 1</c>,
/// so the standable band is <c>ColumnWorldX(X0) - 6 .. ColumnWorldX(X1) - 1</c>
/// and the anchor is the first x outside it.
/// </para>
/// <para>
/// ⚠️ <b>A hazard rather than a failure, and the difference matters.</b> The
/// tracker is a direction hint that gravity and the landing collision finish, so
/// a bot with leftover momentum drifts onto the standable part and lands anyway —
/// <c>EdgeFlightTests</c> strands only about 535 of 31,500 arrivals. It bites
/// when the arc is slow enough and long enough for the tracker to actually hold
/// the bot at the aim point, which is what <c>koth_corinth</c> 202-&gt;176 is.
/// </para>
/// <para>
/// ⚠️ <b>Measured with the SOLDIER box, which is the narrowest at ±6.</b> Widths
/// run 6, 7 and 9 (<see cref="CharacterBox"/>), so a Heavy loses three more
/// pixels on the right and every number here understates every wider class. Same
/// box as <c>EdgeFlightTests.EveryShippedNodeCanHoldACharacter</c>, deliberately,
/// so the two are comparable.
/// </para>
/// <para>
/// ⛔ <b>That sibling test does not cover this and cannot.</b> It asserts
/// <c>PlaceFree</c> — the node is not inside terrain — and never asks whether
/// there is floor under the box. Every node counted here passes it.
/// </para>
/// <para>
/// A measurement rather than a gate, like <see cref="RunupCensus"/>. Numbers in
/// <c>docs/TRAPS-BOTS.md</c>, "the anchor column is not a place to stand".
/// </para>
/// </remarks>
public sealed class LandingAnchorCensus(ITestOutputHelper output)
{
    [Fact(Skip = "Measurement, not a gate. Numbers in docs/TRAPS-BOTS.md.")]
    public void Census()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var maps = Directory.EnumerateFiles(MapDirectory.Resolve(), "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 })
            .Select(n => n!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var box = CharacterBox.For(PlayerClass.Soldier);
        var filter = new CollisionFilter { Team = Team.Red, CarryingIntel = false };

        output.WriteLine(
            "map                     nodes  midNoFloor   oneCol    arcs  aimNoFloor  narrow  narrowNoFloor");

        long gNodes = 0, gMid = 0, gOne = 0, gArcs = 0, gAim = 0, gNarrow = 0, gNarrowAim = 0;

        foreach (var name in maps)
        {
            ScenarioWorld world;

            try
            {
                world = ScenarioWorld.Load(name, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            var graph = world.Nav;

            if (graph is null)
                continue;

            // ⚠️ "Free but no floor under it" rather than "not standable". A node
            // inside a team gate is not free either, and that is a gate rather
            // than geometry — the flight harness swaps teams for exactly those.
            // Counting them here would blur two different things into one number.
            bool NoFloorUnder(double x, double y) =>
                world.Collision.PlaceFree(box, x, y, filter)
                && world.Collision.PlaceFree(box, x, y + 1, filter);

            long nodes = 0, mid = 0, one = 0;

            for (var n = 0; n < graph.NodeCount; n++)
            {
                var node = graph.Nodes[n];
                nodes++;

                if (node.X0 == node.X1)
                    one++;

                if (NoFloorUnder(NavGraph.ColumnWorldX((node.X0 + node.X1) / 2), graph.StandY(n) - 1))
                    mid++;
            }

            long arcs = 0, aim = 0, narrow = 0, narrowAim = 0;

            for (var i = 0; i < graph.EdgeCount; i++)
            {
                ref readonly var e = ref graph.Edges[i];

                if (e.Type is not (NavEdgeType.Jump or NavEdgeType.DoubleJump) || e.Takeoff < 0)
                    continue;

                arcs++;

                var to = graph.Nodes[e.To];
                var jumpDir = to.X0 > e.Takeoff ? 1 : to.X1 < e.Takeoff ? -1 : 1;

                // SteerJump's clamp, verbatim: the furthest the tracker will ever
                // ask for on this arc.
                var aimX = NavGraph.ColumnWorldX(e.Takeoff) + (jumpDir * e.Bucket * e.Ticks);
                var isNarrow = to.X1 - to.X0 <= 1;

                if (isNarrow)
                    narrow++;

                if (!NoFloorUnder(aimX, graph.StandY(e.To) - 1))
                    continue;

                aim++;

                if (isNarrow)
                    narrowAim++;
            }

            gNodes += nodes; gMid += mid; gOne += one;
            gArcs += arcs; gAim += aim; gNarrow += narrow; gNarrowAim += narrowAim;

            output.WriteLine(
                $"{name,-22} {nodes,6} {mid,11} {one,7} {arcs,7} {aim,11} {narrow,7} {narrowAim,14}");
        }

        output.WriteLine("");
        output.WriteLine(
            $"TOTAL nodes {gNodes}, midpoint free with no floor under it {gMid} ({100.0 * gMid / gNodes:0.0}%), "
            + $"one-column {gOne}");
        output.WriteLine(
            $"TOTAL arcs {gArcs}, tracker aim free with no floor under it {gAim} ({100.0 * gAim / gArcs:0.0}%); "
            + $"narrow landings {gNarrow}, of those {gNarrowAim} ({100.0 * gNarrowAim / Math.Max(1, gNarrow):0.0}%)");
    }
}
