using System.Diagnostics;
using Gg2.Bots;

namespace Bench;

public static class ResetBench
{
    public static void Run(string maps, string? nav)
    {
        var MAP = Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug";
        Console.WriteLine($"map={MAP} nav={nav ?? "(none)"}");
        var sw = new Stopwatch();

        // warm
        for (var i = 0; i < 20; i++) ScenarioWorld.Load(MAP, 1, maps, nav);

        sw.Restart();
        for (var i = 0; i < 200; i++) ScenarioWorld.Load(MAP, 1, maps, null);
        sw.Stop();
        Console.WriteLine($"ScenarioWorld.Load (no nav)  {sw.Elapsed.TotalMilliseconds / 200:0.000} ms");

        if (nav is not null)
        {
            sw.Restart();
            for (var i = 0; i < 200; i++) ScenarioWorld.Load(MAP, 1, maps, nav);
            sw.Stop();
            Console.WriteLine($"ScenarioWorld.Load (nav)     {sw.Elapsed.TotalMilliseconds / 200:0.000} ms");
        }

        var bytes = File.ReadAllBytes(Path.Combine(maps, MAP + ".png"));
        sw.Restart();
        for (var i = 0; i < 200; i++) File.ReadAllBytes(Path.Combine(maps, MAP + ".png"));
        sw.Stop();
        Console.WriteLine($"  File.ReadAllBytes          {sw.Elapsed.TotalMilliseconds / 200:0.000} ms ({bytes.Length} bytes)");

        sw.Restart();
        for (var i = 0; i < 200; i++) Gg2.Maps.LevelMap.FromPng(MAP, bytes).ForArea(1);
        sw.Stop();
        Console.WriteLine($"  LevelMap.FromPng+ForArea   {sw.Elapsed.TotalMilliseconds / 200:0.000} ms");

        var level = Gg2.Maps.LevelMap.FromPng(MAP, bytes).ForArea(1);
        sw.Restart();
        for (var i = 0; i < 200; i++) _ = new Gg2.Simulation.CollisionWorld(level);
        sw.Stop();
        Console.WriteLine($"  new CollisionWorld         {sw.Elapsed.TotalMilliseconds / 200:0.000} ms");
    }
}
