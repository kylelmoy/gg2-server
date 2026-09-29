using System.Globalization;
using GG2.Parity;

namespace GG2.Parity;

/// <summary>
/// <c>gg2-parity</c>: record a server's broadcast stream, and diff two
/// recordings.
/// </summary>
/// <remarks>
/// docs/PARITY.md, tier 0. The intended use is to point this at the reference
/// GM8 server and at this port in turn and compare — which turns "the client
/// does not like my stream" from a guess into a byte offset.
/// </remarks>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "record" => Record(args[1..]),
                "diff" => Diff(args[1..]),
                "replay" => Replay(args[1..]),
                "census" => Census(args[1..]),
                "fingerprint" => Fingerprint(args[1..]),
                "recover" => Recover(args[1..]),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        Usage();
        return 2;
    }

    /// <summary>
    /// Where the built-in maps are, without a reference checkout.
    /// </summary>
    /// <remarks>
    /// <b>A parallel copy of <c>GG2.Server.MapDirectory.Resolve</c></b> — see
    /// that type for why the probe order is what it is. This project does not
    /// reference <c>GG2.Server</c>, and the resolver cannot live in
    /// <c>GG2.Maps</c> because <c>GG2.Simulation</c> references that and
    /// CLAUDE.md keeps file access out of everything the simulation can reach.
    /// <b>Change one, change the other.</b>
    /// </remarks>
    private static string DefaultMapDirectory()
    {
        const string sentinel = "ctf_truefort.png";
        bool Holds(string d) => File.Exists(Path.Combine(d, sentinel));

        if (Environment.GetEnvironmentVariable("GG2_MAPS") is { Length: > 0 } explicitPath
            && Holds(explicitPath))
        {
            return explicitPath;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "maps");
        if (Holds(beside))
            return beside;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "maps");
            if (Holds(candidate))
                return candidate;
        }

        if (Environment.GetEnvironmentVariable("GG2_REFERENCE") is { Length: > 0 } reference)
        {
            var included = Path.Combine(reference, "Source", "gg2", "Included Files");
            if (Holds(included))
                return included;
        }

        return string.Empty;
    }

    private static string? Flag(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>
    /// The map out of <c>MatchRecorder</c>'s file name,
    /// <c>&lt;utc&gt;-&lt;utc&gt;-&lt;map&gt;-&lt;nn&gt;.trace.part</c>.
    /// </summary>
    /// <remarks>
    /// Taken from between the second dash and the last, because a map name may
    /// contain dashes and the timestamp and index never do.
    /// </remarks>
    internal static string? MapFromName(string name)
    {
        var stem = name;

        foreach (var suffix in new[] { ".part", ".trace" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                stem = stem[..^suffix.Length];
        }

        var first = stem.IndexOf('-');
        var second = first < 0 ? -1 : stem.IndexOf('-', first + 1);
        var last = stem.LastIndexOf('-');

        if (second < 0 || last <= second)
            return null;

        var map = stem[(second + 1)..last];
        return map.Length == 0 ? null : map;
    }

    private static void Usage() => Console.WriteLine(
        """
        gg2-parity - record, compare and replay server broadcast streams

          record --host <h> --port <n> --seconds <s> --name <n> --out <file>
                 [--source <text>] [--maps <path>]
          census <trace> [--map <name>] [--maps <path>]
          recover <file.trace.part> [--map <name>] [--source <text>]
          diff   <a> <b>
          replay <trace> [--maps <path>] [--no-stop] [--all-fields]
                 [--dump-slot <n>] [--dump-from <tick>] [--dump-to <tick>]

        The recorder joins as a passive spectator and never sends an INPUTSTATE
        of its own, so what it writes down is the server's own broadcast: every
        player's keys and aim each tick, and every player's position, velocity
        and hp each seventh tick. One recording therefore holds both the inputs
        a simulation consumed and the outputs it produced.

        An --out ending in .trace gets the header that makes it replayable.
        Anything else is written raw, which is what a byte diff wants.

        census frames a recording and says what is in it: a message count, the
        roster, the first FULL_UPDATE player by player, and - when framing
        stops - which message it stopped on. A raw recording needs --map.

        Point record at the reference GM8 server and at this port in turn, then
        diff the two. See docs/PARITY.md.
        """);
}
