using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Which call to ObservationWriter.Write allocates, and how much.
/// </summary>
public static class AllocProbe
{
    public static void Run(string maps)
    {
        var env = new DuelEnv(maps);

        env.Reset(
            new DuelSpec
            {
                Map = "gg_debug",
                RedClass = PlayerClass.Soldier,
                BlueClass = PlayerClass.Scout,
                RedX = 600,
                RedY = 850,
                BlueX = 700,
                BlueY = 850,
                DecisionCap = 100000,
                ActionRepeat = 1,
            },
            seed: 5);

        var buffer = new float[ObservationWriter.Length];

        // Exactly the test's warmup.
        for (var i = 0; i < 5; i++)
        {
            var warm = new WorldView(in env.World.State, env.World.Collision);
            ObservationWriter.Write(in warm, env.RedSlot, buffer);
            env.Step(new EnvAction { Fire = 1, AimDelta = ActionDecoder.AimCentreBin }, EnvAction.Idle);
        }

        var reported = 0;

        for (var i = 0; i < 200_000; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var view = new WorldView(in env.World.State, env.World.Collision);

            ObservationWriter.Write(in view, env.RedSlot, buffer);

            var delta = GC.GetAllocatedBytesForCurrentThread() - before;

            if (delta != 0 && reported < 20)
            {
                Console.WriteLine($"  call {i}: allocated {delta} bytes");
                reported++;
            }
        }

        Console.WriteLine(reported == 0 ? "  no allocation in 200,000 calls" : $"  {reported} allocating calls reported");
    }
}
