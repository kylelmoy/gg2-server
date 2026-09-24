using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// What one policy forward pass costs in this process.
/// </summary>
/// <remarks>
/// <b>A MEASUREMENT rather than an estimate</b>, so a cost put in front of a
/// trainer is real. Warmed past tier-1 promotion (30 calls) before the measured
/// window, because a short warm-up measures tier-0 code — the trap that makes a
/// duel tick read 23 us at one world and 2.9 us at sixty-four.
/// </remarks>
public static class PolicyCost
{
    public static void Run(string npz, string observations, string maps, string nav)
    {
        // The stamp check is BYPASSED, and for this mode that is not a
        // compromise. A forward pass is the same arithmetic whatever simulation
        // the weights were trained against — the shapes come from the checkpoint,
        // the cost comes from the shapes, and nothing here reads a behaviour.
        // Without this the mode cannot run at all against a stamp-invalid
        // checkpoint (gg2-rl/docs/ENV.md, "The checkpoint is a FIXTURE now"). Churn.cs
        // bypasses for a different reason and says so.
        var policy = PolicyNetwork.Load(npz, ignoreSimulationStamp: true);
        var raw = File.ReadAllBytes(observations);
        var rows = BitConverter.ToInt32(raw, 0);
        var length = BitConverter.ToInt32(raw, 4);
        var values = new float[rows * length];

        for (var i = 0; i < values.Length; i++)
            values[i] = BitConverter.ToSingle(raw, 8 + (i * 4));

        var observation = new float[length];
        var heads = new int[ActionDecoder.HeadShape.Length];

        Console.WriteLine($"policy {length} -> 64 -> 64 -> {string.Join("+", ActionDecoder.HeadShape.ToArray())}");

        // Warm well past tier-1; the rejit is asynchronous.
        for (var i = 0; i < 200_000; i++)
        {
            observations_Copy(values, i % rows, length, observation);
            policy.Act(observation, heads);
        }

        foreach (var count in new[] { 100_000, 1_000_000 })
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();

            for (var i = 0; i < count; i++)
            {
                observations_Copy(values, i % rows, length, observation);
                policy.Act(observation, heads);
            }

            clock.Stop();

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Console.WriteLine(
                $"  {count,9:N0} calls   {clock.Elapsed.TotalMilliseconds * 1_000_000 / count,8:0} ns each"
                + $"   {count / clock.Elapsed.TotalSeconds / 1e6,6:0.00} M/s"
                + $"   allocated {allocated} bytes");
        }

        Console.WriteLine();
        Console.WriteLine($"observation extraction ({ObservationWriter.Length} floats, ctf_truefort)");
        Observation(maps, nav);
    }

    /// <summary>
    /// What ONE observation extraction costs, measured on a real world.
    /// </summary>
    /// <remarks>
    /// <b>`gg2-rl`'s rollout cost model has inference at 40% of a
    /// candidate-tick and observation extraction at 36%.</b> Extraction cost
    /// matters more than inference: if it really is that large, vectorising the
    /// forward pass buys much less than deleting a call.
    /// </remarks>
    public static void Observation(string maps, string nav)
    {
        var world = ScenarioWorld.Load("ctf_truefort", 0, maps, nav);

        // A populated world: an empty one skips the enemy loops entirely and
        // would measure the cheap path.
        world.PlaceAtSpawn(PlayerClass.Scout, Team.Red, 0);
        world.PlaceAtSpawn(PlayerClass.Soldier, Team.Blue, 0);
        world.PlaceAtSpawn(PlayerClass.Sniper, Team.Blue, 0);
        world.Step(60);

        var buffer = new float[ObservationWriter.Length];
        var view = new WorldView(in world.State, world.Collision);

        for (var i = 0; i < 200_000; i++)
            ObservationWriter.Write(in view, 1, buffer);

        foreach (var count in new[] { 100_000, 1_000_000 })
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();

            for (var i = 0; i < count; i++)
                ObservationWriter.Write(in view, 1, buffer);

            clock.Stop();

            Console.WriteLine(
                $"  {count,9:N0} writes  {clock.Elapsed.TotalMilliseconds * 1_000_000 / count,8:0} ns each"
                + $"   allocated {GC.GetAllocatedBytesForCurrentThread() - before} bytes");
        }
    }

    private static void observations_Copy(float[] values, int row, int length, float[] destination) =>
        values.AsSpan(row * length, length).CopyTo(destination);
}
