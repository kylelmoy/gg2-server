using GG2.Bots;
using GG2.Protocol;

namespace Bench;

/// <summary>
/// Runs every checkpoint-driven mode at its smallest size and reports which ones
/// threw.
/// </summary>
/// <remarks>
/// ⛔ <b>Not a measurement and not a gate on any NUMBER — it asks only whether
/// each mode still runs.</b> The numbers it produces are one match on one map and
/// mean nothing; the exit status is the whole output.
/// <para>
/// <b>Why it exists.</b> On 2026-09-06 three faults landed in this directory
/// inside one day, none caught by review: the class roster still read from a
/// constant after the mechanism that supplied it had been fixed; two null
/// dereferences on the control arm, which crash <c>BENCH_PERMAP</c> and
/// <c>BENCH_SWEEP</c> on their first match; and <c>BENCH_EVAL</c> handing its
/// control arm a different roster from its policy arm — silent, plausible and
/// publishable. Two were introduced by the change that fixed the first.
/// ⚠️ <b>Every one was found by RUNNING a mode, and one had already been
/// "verified" by building.</b>
/// </para>
/// <para>
/// ⛔ <b>It stays OUT of <c>GG2.sln</c> and out of CI, deliberately</b>
/// (<c>CLAUDE.md</c>: "dotnet test should not build a benchmark and CI should not
/// run one"). This is one command a person runs after touching this directory,
/// not a test. That is the whole design: the rule was never the problem — nobody
/// having a cheap way to obey it was.
/// </para>
/// <para>
/// ⚠️⚠️ <b>IT PROVES ALMOST NOTHING WITH A <c>combat4</c>-FAMILY CHECKPOINT</b>,
/// whose trained set IS <see cref="PolicyNetwork.DefaultTrainedClasses"/>. All
/// three of the faults above are invisible when the constant and the file agree —
/// every broken path stays green and every number stays plausible. It says so
/// loudly below rather than passing quietly, because a smoke run that cannot fail
/// is worse than none.
/// </para>
/// </remarks>
public static class Smoke
{
    /// <summary>One mode, its name, and how to run it at minimum size.</summary>
    private sealed record Mode(string Name, Action Run);

    public static int Run(string maps, string nav, string npz)
    {
        // Smallest everything. BENCH_FOCUS is honoured by the modes that take a
        // map list; the cap bounds the ones that play a timed match.
        Environment.SetEnvironmentVariable("BENCH_FOCUS", "gg_debug");
        Environment.SetEnvironmentVariable("BENCH_MAP", "gg_debug");
        Environment.SetEnvironmentVariable("BENCH_CAP", "1");
        Environment.SetEnvironmentVariable("BENCH_CONFIRM", "1");

        var checkpoint = PolicyNetwork.Load(npz);
        var trained = checkpoint.TrainedClasses.ToArray();
        var fallback = PolicyNetwork.DefaultTrainedClasses.ToArray();
        var isDefault = trained.Length == fallback.Length && trained.All(fallback.Contains);

        Console.WriteLine($"BENCH_SMOKE against {Path.GetFileName(npz)}");
        Console.WriteLine($"  trained set: {string.Join(',', trained)}");
        Console.WriteLine();

        if (isDefault)
        {
            // ⛔ A warning rather than a refusal: somebody may deliberately be
            // smoking a combat4 checkpoint because it is the one they have. But
            // they have to be told what it does not cover, or a green run here
            // reads as the coverage a rest3 run would have given.
            Console.WriteLine(
                "⚠️  THIS CHECKPOINT'S SET IS THE DEFAULT, so this run cannot see the class of");
            Console.WriteLine(
                "   fault this mode exists for: a mode reading the constant instead of the file");
            Console.WriteLine(
                "   behaves identically here. Use a checkpoint whose set differs — the suite ships");
            Console.WriteLine(
                "   rest3_s1_iter12000.npz (ENGINEER,SPY,PYRO) for exactly this.");
            Console.WriteLine();
        }

        Mode[] modes =
        [
            new("BENCH_PERMAP", () => PerMapEval.Run(maps, nav, npz, 1)),
            new("BENCH_SWEEP", () => RangeSweep.Run(maps, nav, npz, 1)),
            new("BENCH_ARMS", () => ArmsEval.Run(maps, nav, npz, 1)),
            new("BENCH_CHURN", () => ChurnBench.Run(maps, nav, npz, 1)),
            new("BENCH_EVAL", () => PolicyEval.Run(maps, nav, npz, 1)),
            new("BENCH_THREADS", () => ThreadsBench.Run(maps, nav, 1)),
        ];

        var failures = new List<string>();

        foreach (var mode in modes)
        {
            Console.WriteLine($"--- {mode.Name}");

            try
            {
                mode.Run();
                Console.WriteLine($"--- {mode.Name} OK");
            }
            catch (Exception e)
            {
                // ⚠️ Caught and carried on rather than thrown, so one broken mode
                // does not hide the state of the other five — which is the case
                // that actually happened: PERMAP and SWEEP were broken together
                // and ARMS, CHURN and EVAL were fine.
                failures.Add($"{mode.Name}: {e.GetType().Name}: {e.Message}");
                Console.WriteLine($"--- {mode.Name} FAILED: {e.GetType().Name}: {e.Message}");
            }

            Console.WriteLine();
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"all {modes.Length} modes ran.");
            Console.WriteLine(
                isDefault
                    ? "⚠️  …but against a default-set checkpoint, which is the weak version of this."
                    : "The roster line above each mode is the other half: read it against the set.");

            return 0;
        }

        Console.WriteLine($"{failures.Count} of {modes.Length} modes FAILED:");

        foreach (var failure in failures)
            Console.WriteLine($"  {failure}");

        return 1;
    }
}
