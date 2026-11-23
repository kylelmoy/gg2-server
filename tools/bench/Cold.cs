using System.Diagnostics;
using GG2.Bots;
using GG2.Maps;

namespace Bench;

/// <summary>
/// The FIRST load in a cold process — which is what a training worker pays
/// when it rebuilds its whole pool every iteration.
/// </summary>
/// <remarks>
/// Everything else in this bench warms up first and so measures steady state.
/// A process that lives ~20 seconds and is then discarded never reaches steady
/// state on its startup path, so this is the number that matters for bring-up.
/// </remarks>
public static class ColdBench
{
    public static void Run(string maps, string? nav, string map, int worlds)
    {
        var total = Stopwatch.StartNew();

        // 1. The raw file reads, cold.
        var sw = Stopwatch.StartNew();
        var png = File.ReadAllBytes(Path.Combine(maps, map + ".png"));
        var pngMs = sw.Elapsed.TotalMilliseconds;

        // 2. Decode the map — which now builds the clearance field inside
        //    Walkmask's constructor. Split so the field's share is visible.
        sw.Restart();
        var level = LevelMap.FromPng(map, png).ForArea(1);
        var decodeMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var clearance = level.Walkmask.Clearance.Length;
        var clearanceMs = sw.Elapsed.TotalMilliseconds;

        // 2b. An independent chamfer over the same mask, COLD, so the share of
        //     the decode that my eager clearance field costs is visible rather
        //     than inferred. Same algorithm, different code, so it pays its own
        //     tier-0 JIT exactly as Walkmask's does.
        sw.Restart();
        var independent = Chamfer.ClearanceFor(level.Walkmask);
        var chamferMs = sw.Elapsed.TotalMilliseconds;

        // 3. The nav cache.
        var navMs = 0.0;
        if (nav is not null)
        {
            sw.Restart();
            NavCache.TryLoad(nav, map, 1, out _);
            navMs = sw.Elapsed.TotalMilliseconds;
        }

        // 4. What MakeBatch actually does: N worlds through the cached path.
        sw.Restart();
        var first = ScenarioWorld.Load(map, 1, maps, nav);
        var firstLoadMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (var i = 1; i < worlds; i++)
            ScenarioWorld.Load(map, 1, maps, nav);
        var restMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"COLD process, map={map}, {worlds} worlds, nav={(nav is null ? "no" : "yes")}");
        Console.WriteLine($"  File.ReadAllBytes            {pngMs,7:0.00} ms");
        Console.WriteLine($"  LevelMap.FromPng + ForArea   {decodeMs,7:0.00} ms   (includes the clearance field)");
        Console.WriteLine($"  - clearance already built    {clearanceMs,7:0.00} ms   ({clearance:N0} cells)");
        Console.WriteLine($"  - same chamfer, cold, again  {chamferMs,7:0.00} ms   ({independent:N0} cells) <- what it adds to the decode");
        Console.WriteLine($"  NavCache.TryLoad             {navMs,7:0.00} ms");
        Console.WriteLine($"  ScenarioWorld.Load, 1st      {firstLoadMs,7:0.00} ms   (a SECOND decode: the cache is keyed separately)");
        Console.WriteLine($"  ScenarioWorld.Load, x{worlds - 1,-3}     {restMs,7:0.00} ms   ({restMs / Math.Max(1, worlds - 1),5:0.00} ms each, cached)");
        Console.WriteLine($"  ---------------------------------------");
        Console.WriteLine($"  everything above             {total.Elapsed.TotalMilliseconds,7:0.00} ms");
    }
}

/// <summary>An independent chamfer clearance field, for the cold-decode comparison.</summary>
internal static class Chamfer
{
    public static int ClearanceFor(Walkmask mask) => Clearance(mask).Length;

    private static byte[] Clearance(Walkmask mask)
    {
        var w = mask.Width;
        var h = mask.Height;
        var d = new byte[w * h];
        const byte Max = 40;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            d[(y * w) + x] = mask.IsSolid(x, y) ? (byte)0 : Max;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var best = d[(y * w) + x];
            if (best == 0) continue;
            if (x > 0) best = Math.Min(best, (byte)(d[(y * w) + x - 1] + 1));
            if (y > 0) best = Math.Min(best, (byte)(d[((y - 1) * w) + x] + 1));
            if (x > 0 && y > 0) best = Math.Min(best, (byte)(d[((y - 1) * w) + x - 1] + 1));
            if (x + 1 < w && y > 0) best = Math.Min(best, (byte)(d[((y - 1) * w) + x + 1] + 1));
            d[(y * w) + x] = best;
        }

        for (var y = h - 1; y >= 0; y--)
        for (var x = w - 1; x >= 0; x--)
        {
            var best = d[(y * w) + x];
            if (best == 0) continue;
            if (x + 1 < w) best = Math.Min(best, (byte)(d[(y * w) + x + 1] + 1));
            if (y + 1 < h) best = Math.Min(best, (byte)(d[((y + 1) * w) + x] + 1));
            if (x + 1 < w && y + 1 < h) best = Math.Min(best, (byte)(d[((y + 1) * w) + x + 1] + 1));
            if (x > 0 && y + 1 < h) best = Math.Min(best, (byte)(d[((y + 1) * w) + x - 1] + 1));
            d[(y * w) + x] = best;
        }

        return d;
    }
}
