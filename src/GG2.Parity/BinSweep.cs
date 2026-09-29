using GG2.Maps;
using GG2.Protocol;

namespace GG2.Parity;

/// <summary>
/// What a sweep across a slot's own quantisation bin found.
/// </summary>
/// <param name="Points">States tried.</param>
/// <param name="Reproduced">
/// How many of them reproduced the target tick with no mismatch at all.
/// </param>
public readonly record struct BinSweepResult(int Points, int Reproduced)
{
    /// <summary>The fraction of the bin that reproduces the reference.</summary>
    public double Coverage => Points == 0 ? 0 : (double)Reproduced / Points;

    public override string ToString() => $"{Reproduced}/{Points} ({Coverage:P0})";
}

/// <summary>
/// Asks whether a divergence is a bug or a threshold: does <em>any</em> state
/// the wire could have been describing reproduce the tick that went wrong?
/// </summary>
/// <remarks>
/// <b>The question a closed-loop mismatch cannot answer on its own.</b> A
/// replay that diverges has either got a rule wrong or landed on the wrong side
/// of a discrete threshold — a <c>place_free</c> boundary, a landing, a stair
/// step — while being well inside the precision the recording carries. The two
/// look identical in a drift report and want completely different responses.
/// <para>
/// This tells them apart. It anchors one slot from the wire and sweeps that
/// anchor over the bin the observation was rounded from — half a wire unit
/// either way, which is 0.1 px of position and 1/17 px/tick of velocity — then
/// counts how many of those states reproduce the target tick exactly. Wide
/// coverage means every rule involved is right and the port is a hair off a
/// threshold. Zero coverage means the reference did something the port cannot
/// do from anywhere in the bin, which is a bug with an address.
/// </para>
/// <para>
/// Measured on <c>reference-ctf_truefort-closedcombat.trace</c>, tick 1673 —
/// the end of the exact window: <b>34 of 49 points reproduce it</b>. The port's
/// <c>x + hspeed</c> going into t1667 is 2117.5087 against a <c>place_free</c>
/// boundary at exactly 2117.5, so it is stopped by a wall 0.0087 px early,
/// loses its <c>hspeed</c> a tick before the reference does, and turns around a
/// tick early — worth 26 wire units of x by the time the next update lands.
/// <c>docs/PARITY.md</c>, "an exact window can also end on a threshold".
/// </para>
/// <para>
/// <b>Open loop, and only the swept slot is meaningful.</b> Every other slot
/// is anchored at the bottom of its own bin and is therefore wrong by up to half
/// a unit, so a sweep bottoms out at the neighbours' residual rather than at
/// zero — see <see cref="PortedSimulation.Nudges"/>. Ask it about the slot whose
/// trajectory diverged.
/// </para>
/// </remarks>
public static class BinSweep
{
    /// <summary>Half a wire unit of position, in world pixels.</summary>
    private const double HalfPositionUnit = 0.1;

    /// <summary>Half a wire unit of velocity, in pixels per tick.</summary>
    private const double HalfVelocityUnit = 0.5 / 8.5;

    /// <summary>
    /// Replays <paramref name="ticks"/> once per grid point, sweeping
    /// <paramref name="slot"/>'s anchored position and horizontal velocity
    /// across their bins, and counts how many reproduce
    /// <paramref name="targetTick"/> exactly.
    /// </summary>
    /// <param name="steps">
    /// Grid points per axis; the total cost is its square, and each point is a
    /// full replay. Odd values put the bin's own centre on the grid.
    /// </param>
    public static BinSweepResult Sweep(
        Func<string, LevelMap> loadMap,
        string traceName,
        string map,
        IReadOnlyList<TraceTick> ticks,
        int slot,
        int targetTick,
        int steps = 7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 2);

        var reproduced = 0;

        for (var xi = 0; xi < steps; xi++)
        {
            for (var hi = 0; hi < steps; hi++)
            {
                var nudgeX = Lerp(-HalfPositionUnit, HalfPositionUnit, xi, steps);
                var nudgeH = Lerp(-HalfVelocityUnit, HalfVelocityUnit, hi, steps);

                var simulation = new PortedSimulation(loadMap);
                simulation.Nudges[slot] = new AnchorNudge(nudgeX, 0, nudgeH, 0);

                // Open loop: the anchor is put back at every observation, so
                // what this measures is one seven-tick prediction window from a
                // state inside the bin — which is exactly the question.
                var report = new Replayer(simulation) { Compare = Fields.Trajectory }
                    .Replay(traceName, map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

                var clean = true;

                foreach (var mismatch in report.Mismatches)
                {
                    if (mismatch.Tick == targetTick && mismatch.Slot == slot)
                    {
                        clean = false;
                        break;
                    }
                }

                if (clean)
                    reproduced++;
            }
        }

        return new BinSweepResult(steps * steps, reproduced);
    }

    private static double Lerp(double low, double high, int index, int steps) =>
        low + ((high - low) * index / (steps - 1));
}
