using System.Diagnostics;
using System.Runtime.CompilerServices;
using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Replicates DuelEnv.Step's inner loop so the pieces can be timed apart:
/// decode, drive, MatchSimulation.Tick, and the per-decision bookkeeping.
/// </summary>
public static class DuelStepBench
{
    private sealed class Fixed(PlayerInput input) : IInputSource
    {
        public PlayerInput Value = input;

        public PlayerInput Poll(in WorldView view, int slot, int tick) => Value;
    }

    public static void Run(string maps, string? nav, string map, int worlds, int rounds)
    {
        Console.WriteLine($"sizeof(WorldState) = {Unsafe.SizeOf<WorldState>():N0} bytes");
        Console.WriteLine($"{worlds} worlds = {Unsafe.SizeOf<WorldState>() * (long)worlds / 1024.0:N0} KiB of state");

        var ws = new ScenarioWorld[worlds];
        var decoders = new ActionDecoder[worlds];
        var redSlot = new int[worlds];
        var blueSlot = new int[worlds];
        var srcR = new Fixed[worlds];
        var srcB = new Fixed[worlds];

        for (var i = 0; i < worlds; i++)
        {
            ws[i] = ScenarioWorld.Load(map, 1, maps, nav);
            redSlot[i] = ws[i].Place(PlayerClass.Soldier, Team.Red, 600, 850);
            blueSlot[i] = ws[i].Place(PlayerClass.Soldier, Team.Blue, 700, 850);
            decoders[i] = new ActionDecoder();
            srcR[i] = new Fixed(PlayerInput.None);
            srcB[i] = new Fixed(PlayerInput.None);
            ws[i].Drive(redSlot[i], srcR[i]);
            ws[i].Drive(blueSlot[i], srcB[i]);
        }

        var rng = new Random(3);
        var sw = new Stopwatch();
        double tDecode = 0, tTick = 0;
        long projectiles = 0;

        EnvAction Rand() => new()
        {
            Horizontal = (byte)rng.Next(3),
            Jump = (byte)rng.Next(2),
            Down = (byte)rng.Next(2),
            Fire = (byte)rng.Next(3),
            AimDelta = (byte)rng.Next(ActionDecoder.AimBins),
            Zoom = (byte)rng.Next(2),
        };

        for (var warm = 0; warm < 200; warm++)
        {
            for (var i = 0; i < worlds; i++)
            {
                srcR[i].Value = decoders[i].Decode(redSlot[i], Rand(), true);
                srcB[i].Value = decoders[i].Decode(blueSlot[i], Rand(), true);
                ws[i].Step();
            }
        }

        for (var r = 0; r < rounds; r++)
        {
            sw.Restart();
            for (var i = 0; i < worlds; i++)
            {
                srcR[i].Value = decoders[i].Decode(redSlot[i], Rand(), true);
                srcB[i].Value = decoders[i].Decode(blueSlot[i], Rand(), true);
            }
            sw.Stop();
            tDecode += sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            for (var i = 0; i < worlds; i++)
                ws[i].Step();
            sw.Stop();
            tTick += sw.Elapsed.TotalMilliseconds;

            for (var i = 0; i < worlds; i++)
                projectiles += ws[i].State.LiveProjectileCount;
        }

        var live = 0;
        for (var i = 0; i < worlds; i++)
            if (ws[i].State.IsAlive(redSlot[i])) live++;

        Console.WriteLine($"  decode x2      {tDecode / rounds * 1000 / worlds:0.00} us/world");
        Console.WriteLine($"  ScenarioWorld.Step {tTick / rounds * 1000 / worlds:0.00} us/world-tick");
        Console.WriteLine($"  live projectiles {projectiles / (double)(rounds * worlds):0.0} per world per tick");
        Console.WriteLine($"  (red alive in {live}/{worlds} worlds at the end)");
    }
}
