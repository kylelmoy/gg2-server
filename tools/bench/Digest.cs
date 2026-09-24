using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// A digest over everything an env hands a trainer, so two builds can be
/// compared byte for byte.
/// </summary>
/// <remarks>
/// The point is the OBSERVATIONS and the reward components, not just "it ran":
/// a performance change to the ray fan or to the map load is only legitimate if
/// the numbers that leave the seam are the same ones.
/// </remarks>
public static class DigestBench
{
    public static void Run(string maps, string? nav, string map, int worlds, int rounds)
    {
        var spec = new DuelSpec
        {
            Map = map,
            RedClass = PlayerClass.Soldier,
            BlueClass = PlayerClass.Scout,
            RedX = 600,
            RedY = 850,
            BlueX = 700,
            BlueY = 850,
            DecisionCap = 120,
            ActionRepeat = 2,
        };

        var batch = new EnvBatch(worlds, spec, 20260830, maps, nav, threads: 1);
        var red = new EnvAction[worlds];
        var blue = new EnvAction[worlds];
        var redOut = new StepResult[worlds];
        var blueOut = new StepResult[worlds];
        var rng = new Random(20260830);

        var stride = ObservationWriter.Length;
        var obs = new float[worlds * 2 * stride];

        using var sha = SHA256.Create();
        var scratch = new byte[obs.Length * 4];

        for (var r = 0; r < rounds; r++)
        {
            for (var i = 0; i < worlds; i++)
            {
                red[i] = Rand();
                blue[i] = Rand();
            }

            batch.Step(red, blue, redOut, blueOut);

            for (var i = 0; i < worlds; i++)
            {
                var world = batch.World(i);
                var view = new WorldView(in world.World.State, world.World.Collision);

                ObservationWriter.Write(in view, world.RedSlot, obs.AsSpan(((i * 2) + 0) * stride, stride));
                ObservationWriter.Write(in view, world.BlueSlot, obs.AsSpan(((i * 2) + 1) * stride, stride));
            }

            MemoryMarshal.AsBytes(obs.AsSpan()).CopyTo(scratch);
            sha.TransformBlock(scratch, 0, scratch.Length, null, 0);

            for (var i = 0; i < worlds; i++)
            {
                var components = Components(redOut[i]).Concat(Components(blueOut[i])).ToArray();
                sha.TransformBlock(components, 0, components.Length, null, 0);
            }
        }

        sha.TransformFinalBlock([], 0, 0);

        Console.WriteLine($"map={map} worlds={worlds} rounds={rounds} nav={(nav is null ? "no" : "yes")}");
        Console.WriteLine($"digest = {Convert.ToHexString(sha.Hash!)}");
        Console.WriteLine($"floats hashed = {(long)rounds * obs.Length:N0}");

        EnvAction Rand() => new()
        {
            Horizontal = (byte)rng.Next(3),
            Jump = (byte)rng.Next(2),
            Down = (byte)rng.Next(2),
            Fire = (byte)rng.Next(3),
            AimDelta = (byte)rng.Next(ActionDecoder.AimBins),
            Zoom = (byte)rng.Next(2),
        };
    }

    private static byte[] Components(StepResult r)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{r.DamageDealt:R}|{r.DamageTaken:R}|{r.Kills}|{r.Deaths}|{(int)r.Termination}|"
            + $"{r.Action.Horizontal}{r.Action.Jump}{r.Action.Down}{r.Action.Fire}{r.Action.AimDelta}{r.Action.Zoom}|"
            + $"{r.BestScore:R};");

        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}
