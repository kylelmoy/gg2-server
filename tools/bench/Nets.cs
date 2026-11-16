using GG2.Bots;
using GG2.Protocol;

namespace Bench;

/// <summary>
/// The gg2-sim nets a bench arm drives with.
/// </summary>
/// <remarks>
/// A <c>.gg2p</c> names no classes, so a tier needs an assignment. A shipped
/// net gets the one the fleet runs (<c>MapDirectory.ShippedClassPolicies</c>,
/// copied here because the bench does not reference the server); any other file
/// takes the classes in <c>BENCH_CLASSES</c> (comma-separated), and is refused
/// without them rather than becoming a tier that drives nothing.
/// </remarks>
internal static class Nets
{
    private static readonly (string File, PlayerClass Class)[] ShippedSet =
    [
        ("ppo-warm-soldier-s1.gg2p", PlayerClass.Soldier),
        ("ppo-finetune-scout-s1.gg2p", PlayerClass.Scout),
        ("ppo-finetune2-heavy-s1.gg2p", PlayerClass.Heavy),
        ("ppo-finetune2-sniper-s1.gg2p", PlayerClass.Sniper),
        ("ppo-freeze-demoman-s1.gg2p", PlayerClass.Demoman),
        ("ppo-finetune2-pyro-s1.gg2p", PlayerClass.Pyro),
        ("ppo-freeze-medic-s1.gg2p", PlayerClass.Medic),
        ("ppo-freeze-engineer-s1.gg2p", PlayerClass.Engineer),
        ("ppo-freeze-spy-s1.gg2p", PlayerClass.Spy),
    ];

    /// <summary>One net as a tier, driving its shipped class or <c>BENCH_CLASSES</c>.</summary>
    public static SimPolicyMicroSpec Load(string path)
    {
        var name = Path.GetFileName(path);
        PlayerClass[] drives;

        if (Environment.GetEnvironmentVariable("BENCH_CLASSES") is { Length: > 0 } listed)
        {
            drives = [.. listed.Split(',').Select(c => Enum.Parse<PlayerClass>(c.Trim(), ignoreCase: true))];
        }
        else
        {
            var match = ShippedSet.Where(entry => entry.File == name).ToArray();

            if (match.Length == 0)
                throw new ArgumentException($"{name} is not a shipped net; set BENCH_CLASSES to the classes it drives");

            drives = [match[0].Class];
        }

        return new SimPolicyMicroSpec(SimPolicyNetwork.Load(path), drives, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>All nine shipped nets, one tier each, as the fleet runs them.</summary>
    public static MicroControllerSpec[] Shipped(string checkpoints) =>
        [.. ShippedSet.Select(entry => (MicroControllerSpec)Load(Path.Combine(checkpoints, entry.File)))];

    /// <summary>Every class a tier list drives.</summary>
    public static PlayerClass[] Classes(IEnumerable<MicroControllerSpec> tiers) =>
        [.. tiers.OfType<SimPolicyMicroSpec>().SelectMany(t => t.Drives).Distinct().Order()];
}
