// Runs a bots-only server unpaced and records it with --record, so a bot
// configuration produces traces in exactly the format the public fleet's do.
//
// Why a server and not HeadlessMatch: the recorder sits on GameServer and its
// traces carry the roster labels (human/bot, controller) that gg2-rl's
// `gg2-env dataset` and human-likeness instruments read. A headless match has no
// wire and nothing to record. Same reason the bots engage each other here
// (BotEngagesBotsToo): with no humans present a live server's default keeps the
// controller off, and the recording would be of the scripted layer whatever
// --mode said.
//
//   botrecord --out <dir> --maps a,b,c --ticks-per-map 18000
//             [--mode policy|tiered|scripted] [--policy x.npz]... [--bots 12]
//             [--humanize [--humanize-preset v3|strokes|lazy|hold|tracking|tracking2|tracking3|tracking4|tracking5|tracking6|tracking7]] [--spread-turn]
//             [--difficulty 1-5] [--no-human-aim] [--map-dir <path>] [--nav <path>]
//
// One server per map, run for --ticks-per-map ticks (30 a second), so a list of
// maps gives the same play time on each.

using System.Globalization;
using GG2.Bots;
using GG2.Server;

var outDir = Flag("--out") ?? throw new ArgumentException("--out <dir> is required");
var maps = (Flag("--maps") ?? "ctf_truefort").Split(',');
var ticks = int.Parse(Flag("--ticks-per-map") ?? "18000", CultureInfo.InvariantCulture);
var bots = int.Parse(Flag("--bots") ?? "12", CultureInfo.InvariantCulture);
var humanize = args.Contains("--humanize");
var spreadTurn = args.Contains("--spread-turn");
var difficulty = int.Parse(Flag("--difficulty") ?? "5", CultureInfo.InvariantCulture);
var humanAim = !args.Contains("--no-human-aim");
var preset = Flag("--humanize-preset") ?? "v3";
_ = HumanizeOptions.ForPreset(preset);
var mode = Flag("--mode") ?? "policy";
var policies = Repeated("--policy");
var root = FindRoot();
var mapDir = Flag("--map-dir") ?? Path.Combine(root, "maps");
var navDir = Flag("--nav") ?? Path.Combine(root, "nav");

Directory.CreateDirectory(outDir);
Console.WriteLine($"{maps.Length} map(s) x {ticks} ticks, {bots} bots, mode {mode}, humanize {humanize} ({preset}), spread turn {spreadTurn}, difficulty {difficulty}, human aim {humanAim}, policies [{string.Join(", ", policies)}]");

foreach (var map in maps)
{
    var options = new ServerOptions
    {
        Port = 0,
        ServerName = humanize ? "botrecord humanized" : "botrecord",
        Map = map,
        MapDirectory = mapDir,
        NavCacheDirectory = navDir,
        RecordingDirectory = outDir,
        Bots = new BotPopulationOptions(Enabled: true, FillToPlayers: bots, MaxBots: bots, MinHumans: 0, Difficulty: difficulty),
        BotMode = mode switch
        {
            "scripted" => BotMode.Scripted,
            "tiered" => BotMode.Tiered,
            _ => BotMode.Policy,
        },
        PolicyPaths = policies,
        BotEngagesBotsToo = true,
        BotHumanize = humanize,
        BotHumanizePreset = preset,
        BotSpreadTurn = spreadTurn,
        BotHumanAim = humanAim,
    };

    BotBrain.Build(options, Console.WriteLine);

    var log = new Quiet();
    using (var server = new GameServer(options, log))
    {
        server.Start();

        for (var t = 0; t < ticks; t++)
            server.Tick();
    }

    Console.WriteLine($"{map}: {ticks} ticks recorded, {log.Warnings} warning(s), {log.Errors} error(s)");

    if (log.Errors > 0)
        return 1;
}

return 0;

string? Flag(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

List<string> Repeated(string name)
{
    var found = new List<string>();

    for (var i = 0; i + 1 < args.Length; i++)
    {
        if (args[i] == name)
            found.Add(args[i + 1]);
    }

    return found;
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "GG2.sln")))
            return dir.FullName;
    }

    return Directory.GetCurrentDirectory();
}

/// <summary>Counts warnings and errors, prints only errors.</summary>
internal sealed class Quiet : IServerLog
{
    public int Warnings { get; private set; }

    public int Errors { get; private set; }

    public void Info(string message)
    {
    }

    public void Warn(string message) => Warnings++;

    public void Error(string message, Exception? error = null)
    {
        Errors++;
        Console.Error.WriteLine($"error: {message} {error}");
    }
}
