// Replays --record traces through AimAnalysis and writes every session's
// counters at every lag, for the people in them, the bots in them, and four
// synthetic cheats built from each person's own play.
//
// The synthetic arms are the positive set: the fleet has no labelled cheater,
// so what a detector can be checked against is (a) real people, who must not be
// flagged, and (b) the same people's sessions with a cheat substituted for one
// part of their input, which must be. Each cheat aims the way a lagged client
// would, at positions LAG ticks old, through a whole-pixel pointer so it also
// passes AimCheck:
//
//   lock     aim at the nearest visible enemy's origin whenever there is one
//   lock3    the same, with 3 px Gaussian jitter on the point every tick
//   trigger  the person's own aim; attack held exactly while it is on an enemy
//   esp      the person's own aim, except with no visible enemy it follows the
//            nearest hidden one (3 px jitter)
//   lock6, lock10  lock with 6 and 10 px jitter: where the lock signal stops
//
// Output: CSV on stdout, one row per (trace, slot, session, arm, lag).
// docs/HARDENING.md, "Aim analysis", has what the fleet's numbers came to.

using System.Collections.Concurrent;
using System.Globalization;
using GG2.Maps;
using GG2.Parity;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

internal static class Program
{
    private static readonly string[] Arms = ["real", "lock", "lock3", "trigger", "esp", "lock6", "lock10"];

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("aimscan <trace-or-directory>... [--maps <path>] > out.csv");
            return 2;
        }

        var maps = Flag(args, "--maps") ?? MapDirectory.Resolve();
        var files = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--maps")
            {
                i++;
                continue;
            }

            if (Directory.Exists(args[i]))
                files.AddRange(Directory.EnumerateFiles(args[i], "*.trace", SearchOption.AllDirectories));
            else if (File.Exists(args[i]))
                files.Add(args[i]);
        }

        files.Sort(StringComparer.Ordinal);

        var rows = new ConcurrentBag<string>();
        var read = 0;
        var failed = 0;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
        {
            try
            {
                Scan(file, maps, rows);
                Interlocked.Increment(ref read);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"{Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
            }
        });

        Console.WriteLine("trace,slot,name,participant,arm,lag,ticks,tracking,locked,flicks,flicklocks,presses,onset,hiddentracking,hiddenlocked");

        foreach (var row in rows.OrderBy(r => r, StringComparer.Ordinal))
            Console.WriteLine(row);

        Console.Error.WriteLine($"{read} trace(s) scanned, {failed} failed");
        return read == 0 ? 1 : 0;
    }

    private static void Scan(string file, string maps, ConcurrentBag<string> rows)
    {
        var trace = Trace.Decode(File.ReadAllBytes(file));

        if (trace.Roster.Count == 0)
            throw new InvalidDataException("no roster labels");

        LevelMap Load(string name) => LevelMap.FromPng(name, File.ReadAllBytes(Path.Combine(maps, name + ".png")));
        WorldShape ShapeFor(string name) => GameModes.ShapeOf(Load(name).ForArea(1));

        var ticks = TraceParser.Parse(trace, ShapeFor(trace.Header.Map), out _, out _, ShapeFor);
        var simulation = new PortedSimulation(Load);
        var notes = trace.Roster.OrderBy(n => n.Tick).ToArray();
        var name = Path.GetFileName(file);

        var analyses = Arms.Select(_ => new AimAnalysis()).ToArray();
        var inputs = Arms.Select(_ => new PlayerInput[WorldState.MaxSlots]).ToArray();
        var watched = new bool[WorldState.MaxSlots];
        var bots = new bool[WorldState.MaxSlots];
        var sessions = new Session?[WorldState.MaxSlots];
        // Seeded from the file name with a stable hash: string.GetHashCode is
        // randomised per process, and two runs must produce the same CSV.
        var random = new Random(name.Aggregate(17, (hash, c) => unchecked(hash * 31 + c)));
        var noteIndex = 0;

        void Close(int slot)
        {
            if (sessions[slot] is not { } session)
                return;

            for (var arm = 0; arm < Arms.Length; arm++)
            {
                if (arm > 0 && session.Participant != TraceParticipant.Human)
                    continue;

                for (var lag = 0; lag <= AimAnalysis.MaxLag; lag++)
                {
                    ref readonly var c = ref analyses[arm].At(slot, lag);
                    rows.Add(string.Join(',',
                        name, slot.ToString(CultureInfo.InvariantCulture), Csv(session.Name), session.Participant,
                        Arms[arm], lag, session.Ticks,
                        c.Tracking, c.Locked, c.Flicks, c.FlickLocks, c.Presses, c.OnsetPresses,
                        c.HiddenTracking, c.HiddenLocked));
                }
            }

            sessions[slot] = null;
        }

        var replayer = new Replayer(simulation)
        {
            Compare = Fields.Trajectory,
            OnTick = t =>
            {
                // Labels for this tick. A note naming someone who was in another
                // slot is the roster compacting, so their statistics move with them.
                var changed = new List<TraceRosterNote>();
                while (noteIndex < notes.Length && notes[noteIndex].Tick <= t)
                    changed.Add(notes[noteIndex++]);

                foreach (var note in changed.OrderBy(n => n.Slot))
                {
                    var slot = note.Slot;
                    if (slot >= WorldState.MaxSlots)
                        continue;

                    if (sessions[slot] is { } current && current.Name == note.Name && current.Participant == note.Participant)
                        continue;

                    var from = Array.FindIndex(sessions, s => s is { } other && other.Name == note.Name && other.Participant == note.Participant);

                    Close(slot);

                    if (from > slot)
                    {
                        foreach (var analysis in analyses)
                            analysis.Move(from, slot);

                        sessions[slot] = sessions[from];
                        sessions[from] = null;
                        foreach (var analysis in analyses)
                            analysis.Reset(from);
                    }
                    else
                    {
                        foreach (var analysis in analyses)
                            analysis.Reset(slot);

                        sessions[slot] = new Session(note.Name, note.Participant);
                    }
                }

                var traceSlots = ticks[t].Slots;
                ref readonly var world = ref simulation.State;
                var collision = simulation.Collision!;

                for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                {
                    var input = slot < traceSlots.Length ? traceSlots[slot].Input : default;
                    var session = sessions[slot];

                    watched[slot] = session is not null && session.Participant != TraceParticipant.Unknown;
                    bots[slot] = session is not null && session.Participant != TraceParticipant.Human;

                    foreach (var arm in inputs)
                        arm[slot] = input;

                    if (session is not null && world.Alive[slot])
                        session.Ticks++;
                }

                analyses[0].Observe(in world, collision, inputs[0], watched);

                // The cheats see the world through the real analysis's history,
                // which now includes this tick.
                for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                {
                    if (!watched[slot] || bots[slot] || !world.Alive[slot])
                        continue;

                    var lag = 2 + slot % 4;
                    var human = inputs[0][slot];

                    if (analyses[0].NearestEnemy(in world, collision, slot, lag, hidden: false, out var vx, out var vy))
                    {
                        inputs[1][slot] = Point(human.Keys, vx, vy);
                        inputs[2][slot] = Point(human.Keys, vx + Gaussian(random) * 3, vy + Gaussian(random) * 3);
                        inputs[5][slot] = Point(human.Keys, vx + Gaussian(random) * 6, vy + Gaussian(random) * 6);
                        inputs[6][slot] = Point(human.Keys, vx + Gaussian(random) * 10, vy + Gaussian(random) * 10);

                    }
                    else if (analyses[0].NearestEnemy(in world, collision, slot, lag, hidden: true, out var hx, out var hy)
                            && hx * hx + hy * hy < 800 * 800)
                        {
                        inputs[4][slot] = Point(human.Keys, hx + Gaussian(random) * 3, hy + Gaussian(random) * 3);
                    }

                    var onTarget = analyses[0].OnVisibleEnemy(in world, collision, slot, lag, human.AimDirectionDegrees);
                    inputs[3][slot] = human with { Keys = onTarget ? human.Keys | PlayerKeys.Attack : human.Keys & ~PlayerKeys.Attack };
                }

                for (var arm = 1; arm < Arms.Length; arm++)
                    analyses[arm].Observe(in world, collision, inputs[arm], watched);
            },
        };

        replayer.Replay(name, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            Close(slot);
    }

    /// <summary>A whole-pixel pointer at this offset, encoded as ClientInputstate.gml does.</summary>
    private static PlayerInput Point(PlayerKeys keys, double dx, double dy)
    {
        var px = Math.Round(dx);
        var py = Math.Round(dy);
        var degrees = Math.Atan2(-py, px) * 180 / Math.PI;
        if (degrees < 0)
            degrees += 360;

        var direction = Math.Min(65535, Math.Round(degrees * 65536 / 360, MidpointRounding.AwayFromZero));
        var distance = Math.Min(255, Math.Round(Math.Sqrt(px * px + py * py) / 2, MidpointRounding.AwayFromZero));
        return new PlayerInput(keys, (ushort)direction, (byte)distance);
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string? Flag(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private sealed class Session(string name, TraceParticipant participant)
    {
        public string Name { get; } = name;

        public TraceParticipant Participant { get; } = participant;

        public int Ticks { get; set; }
    }
}
