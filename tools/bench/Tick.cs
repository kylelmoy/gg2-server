using System.Diagnostics;
using GG2.Bots;
using GG2.Protocol;

namespace Bench;

public static class TickBench
{
    public static void Run(string maps, string? nav, string map)
    {
        Console.WriteLine($"map={map}");
        Measure("0 characters", maps, nav, map, 0);
        Measure("1 character ", maps, nav, map, 1);
        Measure("2 characters", maps, nav, map, 2);
        Measure("4 characters", maps, nav, map, 4);
        Measure("8 characters", maps, nav, map, 8);
    }

    private static void Measure(string label, string maps, string? nav, string map, int characters)
    {
        const int N = 200_000;
        var world = ScenarioWorld.Load(map, 1, maps, nav);

        for (var i = 0; i < characters; i++)
        {
            world.Place(
                PlayerClass.Soldier,
                (i & 1) == 0 ? Team.Red : Team.Blue,
                600 + (i * 40),
                850);
        }

        for (var i = 0; i < 20_000; i++) world.Step();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < N; i++) world.Step();
        sw.Stop();

        Console.WriteLine($"  {label}  {sw.Elapsed.TotalMilliseconds * 1000 / N:0.00} us/tick");
    }
}
