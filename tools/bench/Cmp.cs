using System.Globalization;
using GG2.Bots;

namespace Bench;

public static class PolicyCompare
{
    public static void Run(string obsPath, string expectedPath, string npzPath)
    {
        using (var probe = File.OpenRead(npzPath))
        {
            foreach (var kv in NpyFile.ReadArchive(probe))
            {
                var head = string.Join(' ', kv.Value.Take(10).Select(b => b.ToString("x2")));
                Console.WriteLine($"  entry {kv.Key,-6} {kv.Value.Length,7} bytes  first10 {head}");
            }
        }

        var policy = PolicyNetwork.Load(npzPath);
        var raw = File.ReadAllBytes(obsPath);
        var n = BitConverter.ToInt32(raw, 0);
        var len = BitConverter.ToInt32(raw, 4);
        var lines = File.ReadAllLines(expectedPath);

        Console.WriteLine($"policy: obs {policy.ObservationLength}, repeat {policy.ActionRepeat}, uuid {policy.SimulationUuid}");

        var maxAbs = 0.0;
        var maxRel = 0.0;
        var headMismatch = 0;
        var obs = new float[len];
        Span<int> heads = stackalloc int[6];

        for (var r = 0; r < n; r++)
        {
            for (var i = 0; i < len; i++)
                obs[i] = BitConverter.ToSingle(raw, 8 + (((r * len) + i) * 4));

            policy.Act(obs, heads);

            var expLogits = lines[1 + (r * 2)]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var expHeads = lines[2 + (r * 2)]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToArray();

            var mine = policy.Logits(obs);

            for (var i = 0; i < expLogits.Length; i++)
            {
                var d = Math.Abs(mine[i] - expLogits[i]);
                maxAbs = Math.Max(maxAbs, d);
                var scale = Math.Max(1e-6, Math.Abs(expLogits[i]));
                maxRel = Math.Max(maxRel, d / scale);
            }

            for (var h = 0; h < 6; h++)
            {
                if (heads[h] != expHeads[h])
                {
                    headMismatch++;
                    Console.WriteLine($"  row {r} head {h}: mine {heads[h]} numpy {expHeads[h]}");
                }
            }
        }

        Console.WriteLine($"rows {n}   max|logit delta| {maxAbs:0.000000e0}   max relative {maxRel:0.000000e0}");
        Console.WriteLine(headMismatch == 0 ? "ALL HEADS AGREE" : $"{headMismatch} HEAD MISMATCHES");
    }
}
