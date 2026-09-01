using System.Diagnostics;
using Gg2.Bots;
using Gg2.Env;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

public static class RayBench
{
    private const int Rays = 24;
    private const double Range = 320.0;

    private static readonly (double Dx, double Dy)[] Directions = Build();

    private static (double, double)[] Build()
    {
        var d = new (double, double)[Rays];
        for (var i = 0; i < Rays; i++)
        {
            var angle = i * (2 * Math.PI / Rays);
            d[i] = (Math.Cos(angle), Math.Sin(angle));
        }
        return d;
    }

    public static void Run(string maps, string? nav, string map)
    {
        var world = ScenarioWorld.Load(map, 1, maps, nav);
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 700, 850);
        for (var i = 0; i < 30; i++) world.Step();

        var collision = world.Collision;
        var mask = world.Map.Walkmask;
        var sx = world.State.Characters[red].X;
        var sy = world.State.Characters[red].Y;
        var clearance = Clearance(mask);
        var outp = new double[Rays];

        Console.WriteLine($"map={map} at ({sx:0.0},{sy:0.0})  mask {mask.Width}x{mask.Height} cells");

        Check(collision, mask, clearance);

        Console.WriteLine("  --- at the duel position ---");
        Time("V0 current            ", () => V0(collision, sx, sy, outp));
        Time("V1 + trig table       ", () => V1(collision, sx, sy, outp));
        Time("V2 + mask local       ", () => V2(mask, sx, sy, outp));
        Time("V3 + clearance skip   ", () => V3(mask, clearance, sx, sy, outp));

        // ⚠️ One position is not the workload. Duels place agents all over the
        // map, and a ray fan in the open costs many times one in a corridor.
        var spots = Spots(mask, 512);
        Console.WriteLine($"  --- averaged over {spots.Length} reachable positions ---");
        Sweep("V0 current            ", spots, p => V0(collision, p.X, p.Y, outp));
        Sweep("V1 + trig table       ", spots, p => V1(collision, p.X, p.Y, outp));
        Sweep("V2 + mask local       ", spots, p => V2(mask, p.X, p.Y, outp));
        Sweep("V3 + clearance skip   ", spots, p => V3(mask, clearance, p.X, p.Y, outp));

        // The whole writer, over the same positions, so fan-as-a-share is exact.
        var obs = new float[ObservationWriter.Length];
        var slot2 = red;
        Sweep("WHOLE ObservationWriter", spots, p =>
        {
            world.State.Characters[slot2].X = p.X;
            world.State.Characters[slot2].Y = p.Y;
            var v = new WorldView(in world.State, world.Collision);
            ObservationWriter.Write(in v, slot2, obs);
        });

        // Probe counts, naive against skipping.
        long naive = 0, skipped = 0;
        foreach (var s2 in spots)
        {
            naive += CountNaive(collision, s2.X, s2.Y);
            skipped += CountSkip(mask, clearance, s2.X, s2.Y);
        }

        Console.WriteLine($"  probes per fan: naive {naive / (double)spots.Length:0.0}  skipping {skipped / (double)spots.Length:0.0}");
    }

    private static int CountNaive(CollisionWorld collision, double sx, double sy)
    {
        var n = 0;
        for (var i = 0; i < Rays; i++)
        {
            var (dx, dy) = Directions[i];
            for (var step = 4.0; step <= Range; step += 4.0)
            {
                n++;
                if (collision.IsSolidAt((int)Math.Round(sx + (dx * step)), (int)Math.Round(sy + (dy * step)))) break;
            }
        }
        return n;
    }

    private static int CountSkip(Walkmask mask, byte[] clearance, double sx, double sy)
    {
        var w = mask.Width;
        var h = mask.Height;
        var n = 0;

        for (var i = 0; i < Rays; i++)
        {
            var (dx, dy) = Directions[i];
            for (var k = 1; k * 4.0 <= Range; k++)
            {
                n++;
                var step = k * 4.0;
                var cx = CollisionWorld.CellOf((int)Math.Round(sx + (dx * step)));
                var cy = CollisionWorld.CellOf((int)Math.Round(sy + (dy * step)));
                if ((uint)cx < (uint)w && (uint)cy < (uint)h)
                {
                    var c = clearance[(cy * w) + cx];
                    if (c == 0) break;
                    var skip = ((6 * c) - 7) / 4;
                    if (skip > 1) k += skip - 1;
                }
            }
        }
        return n;
    }

    private static (double X, double Y)[] Spots(Walkmask mask, int n)
    {
        var rng = new Random(11);
        var list = new List<(double, double)>();

        while (list.Count < n)
        {
            var x = rng.NextDouble() * mask.WorldWidth;
            var y = rng.NextDouble() * mask.WorldHeight;

            // Standable-ish: free where the feet are, solid a little below.
            if (!mask.IsSolid((int)x / 6, (int)y / 6))
                list.Add((x, y));
        }

        return [.. list];
    }

    private static void Sweep(string label, (double X, double Y)[] spots, Action<(double X, double Y)> a)
    {
        const int Passes = 400;
        for (var i = 0; i < 40; i++) foreach (var s in spots) a(s);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < Passes; i++) foreach (var s in spots) a(s);
        sw.Stop();
        Console.WriteLine($"  {label} {sw.Elapsed.TotalMilliseconds * 1000 / (Passes * (double)spots.Length):0.00} us");
    }

    private static void Time(string label, Action a)
    {
        const int N = 300_000;
        for (var i = 0; i < 30_000; i++) a();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < N; i++) a();
        sw.Stop();
        Console.WriteLine($"  {label} {sw.Elapsed.TotalMilliseconds * 1000 / N:0.00} us");
    }

    private static void Check(CollisionWorld c, Walkmask m, byte[] clearance)
    {
        var a = new double[Rays];
        var b = new double[Rays];
        var d = new double[Rays];
        var e = new double[Rays];
        var rng = new Random(7);

        for (var t = 0; t < 20000; t++)
        {
            var x = rng.NextDouble() * m.WorldWidth;
            var y = rng.NextDouble() * m.WorldHeight;

            V0(c, x, y, a);
            V1(c, x, y, b);
            V2(m, x, y, d);
            V3(m, clearance, x, y, e);

            for (var i = 0; i < Rays; i++)
            {
                if (a[i] != b[i] || a[i] != d[i] || a[i] != e[i])
                    throw new Exception($"mismatch at ({x},{y}) ray {i}: {a[i]} {b[i]} {d[i]} {e[i]}");
            }
        }

        Console.WriteLine("  all variants bit-identical over 20,000 random positions");
    }

    private static void V0(CollisionWorld collision, double sx, double sy, double[] o)
    {
        for (var i = 0; i < Rays; i++)
        {
            var angle = i * (2 * Math.PI / Rays);
            var dx = Math.Cos(angle);
            var dy = Math.Sin(angle);
            var distance = Range;

            for (var step = 4.0; step <= Range; step += 4.0)
            {
                var x = (int)Math.Round(sx + (dx * step));
                var y = (int)Math.Round(sy + (dy * step));

                if (collision.IsSolidAt(x, y))
                {
                    distance = step;
                    break;
                }
            }

            o[i] = distance;
        }
    }

    private static void V1(CollisionWorld collision, double sx, double sy, double[] o)
    {
        for (var i = 0; i < Rays; i++)
        {
            var (dx, dy) = Directions[i];
            var distance = Range;

            for (var step = 4.0; step <= Range; step += 4.0)
            {
                var x = (int)Math.Round(sx + (dx * step));
                var y = (int)Math.Round(sy + (dy * step));

                if (collision.IsSolidAt(x, y))
                {
                    distance = step;
                    break;
                }
            }

            o[i] = distance;
        }
    }

    private static void V2(Walkmask mask, double sx, double sy, double[] o)
    {
        for (var i = 0; i < Rays; i++)
        {
            var (dx, dy) = Directions[i];
            var distance = Range;

            for (var step = 4.0; step <= Range; step += 4.0)
            {
                var x = CollisionWorld.CellOf((int)Math.Round(sx + (dx * step)));
                var y = CollisionWorld.CellOf((int)Math.Round(sy + (dy * step)));

                if (mask.IsSolid(x, y))
                {
                    distance = step;
                    break;
                }
            }

            o[i] = distance;
        }
    }

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

    private static void V3(Walkmask mask, byte[] clearance, double sx, double sy, double[] o)
    {
        var w = mask.Width;
        var h = mask.Height;

        for (var i = 0; i < Rays; i++)
        {
            var (dx, dy) = Directions[i];
            var distance = Range;

            for (var k = 1; k * 4.0 <= Range; k++)
            {
                var step = k * 4.0;
                var cx = CollisionWorld.CellOf((int)Math.Round(sx + (dx * step)));
                var cy = CollisionWorld.CellOf((int)Math.Round(sy + (dy * step)));

                if ((uint)cx < (uint)w && (uint)cy < (uint)h)
                {
                    var c = clearance[(cy * w) + cx];

                    if (c == 0)
                    {
                        distance = step;
                        break;
                    }

                    var skip = ((6 * (c - 2)) + 5) / 4;
                    if (skip > 1) k += skip - 1;
                }
            }

            o[i] = distance;
        }
    }
}
