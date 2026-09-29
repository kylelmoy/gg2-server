using System.Diagnostics;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

public static class GeomBench
{
    public static void Run(string maps, string? nav, string map)
    {
        var world = ScenarioWorld.Load(map, 1, maps, nav);
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var blue = world.Place(PlayerClass.Soldier, Team.Blue, 700, 850);

        // settle
        for (var i = 0; i < 30; i++) world.Step();

        var view = new WorldView(in world.State, world.Collision);
        var buf = new float[ObservationWriter.Length];
        var sw = new Stopwatch();
        const int N = 200_000;

        for (var i = 0; i < 20_000; i++) ObservationWriter.Write(in view, red, buf);
        sw.Restart();
        for (var i = 0; i < N; i++) ObservationWriter.Write(in view, red, buf);
        sw.Stop();
        var whole = sw.Elapsed.TotalMilliseconds * 1000 / N;
        Console.WriteLine($"ObservationWriter.Write   {whole:0.00} us");

        ref readonly var self = ref view.Character(red);
        var collision = world.Collision;
        var sx = self.X; var sy = self.Y;

        // The ray fan, exactly as WriteGeometry does it.
        long samples = 0;
        sw.Restart();
        double sink = 0;
        for (var n = 0; n < N; n++)
        {
            for (var i = 0; i < 24; i++)
            {
                var angle = i * (2 * Math.PI / 24);
                var dx = Math.Cos(angle);
                var dy = Math.Sin(angle);
                var distance = 320.0;
                for (var step = 4.0; step <= 320.0; step += 4.0)
                {
                    if (n == 0) samples++;
                    var x = (int)Math.Round(sx + (dx * step));
                    var y = (int)Math.Round(sy + (dy * step));
                    if (collision.IsSolidAt(x, y)) { distance = step; break; }
                }
                sink += distance;
            }
        }
        sw.Stop();
        var rays = sw.Elapsed.TotalMilliseconds * 1000 / N;
        Console.WriteLine($"  ray fan (24)            {rays:0.00} us   ({rays / whole * 100:0}% of Write) — {samples} samples/obs");

        // Just the 48 trig calls.
        sw.Restart();
        for (var n = 0; n < N; n++)
            for (var i = 0; i < 24; i++)
            {
                var angle = i * (2 * Math.PI / 24);
                sink += Math.Cos(angle) + Math.Sin(angle);
            }
        sw.Stop();
        var trig = sw.Elapsed.TotalMilliseconds * 1000 / N;
        Console.WriteLine($"    of which trig         {trig:0.00} us   ({trig / whole * 100:0}% of Write)");

        // The patch.
        sw.Restart();
        for (var n = 0; n < N; n++)
            for (var row = 0; row < 8; row++)
            for (var column = 0; column < 8; column++)
            {
                var x = (int)Math.Round(sx) + ((column - 4) * 16);
                var y = (int)Math.Round(sy) + ((row - 4) * 16);
                if (collision.IsSolidAt(x, y)) sink += 1;
            }
        sw.Stop();
        var patch = sw.Elapsed.TotalMilliseconds * 1000 / N;
        Console.WriteLine($"  patch (8x8)             {patch:0.00} us   ({patch / whole * 100:0}% of Write)");
        Console.WriteLine($"  everything else        ~{whole - rays - patch:0.00} us");
        if (sink == 12345.6789) Console.WriteLine("unreachable");
    }
}
