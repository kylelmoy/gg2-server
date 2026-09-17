// What a mispredicted key costs, in pixels.
//
// Program.cs measures how OFTEN "they are still holding what they were
// holding" is wrong. This measures how far wrong the world ends up, which is
// what decides whether a rollback's correction is visible.
//
// The counterfactual, from one anchor:
//
//   at tick t, copy the replayed world into two scratch simulations
//   A runs h ticks on the inputs the recording actually carries
//   B runs h ticks on tick t's inputs, held — the prediction
//   the error is |A - B| per character, in pixels
//
// ⚠️ B is compared against A and never against the recording. The replay's own
// drift, its anchoring cadence and the wire's x*5 quantisation are then common
// to both arms and cancel; what is left is the prediction and nothing else.
// The earlier attempt compared a lagged replay against the recording and
// measured mostly replay drift — see Program.cs's header.
//
// The staleness ramps 0..h-1 across the window, which is the real shape: a node
// re-anchors the moment the true input lands, so error grows from nothing to
// the h-tick figure and resets. The number reported is the end of that cycle.
using System.Globalization;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Server;

namespace InputPredict;

internal static class Cost
{
    /// <summary>
    /// How many ticks to run from the anchor. Reported as STALENESS, which is
    /// one less: the first step of a window is simulated from the input the
    /// node has just received, so it is correct in both arms by construction
    /// and only the steps after it are predicted. A window of 3 is a node whose
    /// newest remote input is 2 ticks old, which is about one way across the
    /// Atlantic at 30 Hz.
    /// </summary>
    private static readonly int[] Windows = [2, 3, 4, 6];

    /// <summary>
    /// How often to start a counterfactual. Every 7th tick is one per
    /// position-bearing tick's worth of the recording — enough samples from a
    /// half-hour segment without simulating the whole trace several times over.
    /// </summary>
    private const int Stride = 7;

    /// <summary>
    /// The control: the prediction arm is fed the recording's own inputs, so
    /// the two arms differ in nothing at all and every error must be exactly
    /// zero. It is what says the instrument is measuring the prediction rather
    /// than two simulations drifting apart on their own — a second
    /// <see cref="PortedSimulation"/> seeded by <c>SetState</c> could carry
    /// less than a whole world and nothing else here would notice.
    /// </summary>
    private static bool _control;

    public static int Run(string[] args)
    {
        _control = args.Contains("--control");
        var mapDirectory = Flag(args, "--maps") ?? MapDirectory.Resolve();
        var limit = int.Parse(Flag(args, "--limit") ?? "6", CultureInfo.InvariantCulture);
        var files = new List<string>();

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            if (Directory.Exists(arg))
                files.AddRange(Directory.EnumerateFiles(arg, "*.trace", SearchOption.AllDirectories));
            else if (File.Exists(arg))
                files.Add(arg);
        }

        files.Sort(StringComparer.Ordinal);

        var chosen = files.Where(HasHuman).Take(limit).ToArray();

        if (chosen.Length == 0)
        {
            Console.Error.WriteLine("no traces with a labelled human");
            return 1;
        }

        var human = new Error("human");
        var bot = new Error("bot");

        foreach (var file in chosen)
        {
            Console.Error.WriteLine($"  {Path.GetFileName(file)}");
            Measure(file, mapDirectory, human, bot);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{chosen.Length} trace(s), a counterfactual every {Stride} ticks"
            + (_control ? " - CONTROL: both arms on the recorded inputs, expect 0.00" : string.Empty));
        Console.WriteLine();
        Error.PrintHeader();
        human.Print();
        bot.Print();

        return human.Any ? 0 : 1;
    }

    private static void Measure(string file, string mapDirectory, Error human, Error bot)
    {
        var trace = Trace.Decode(File.ReadAllBytes(file));
        var maps = 0;

        LevelMap MapLoader(string name)
        {
            maps++;
            return LevelMap.FromPng(name, File.ReadAllBytes(Path.Combine(mapDirectory, name + ".png")));
        }

        WorldShape ShapeFor(string name) => GameModes.ShapeOf(MapLoader(name).ForArea(1));

        var ticks = TraceParser.Parse(trace, ShapeFor(trace.Header.Map), out _, out _, ShapeFor);

        // A scratch world is installed without its map, so both arms have to
        // stay on the map they were seeded from. The recorder opens a segment
        // per map, so a segment that changes map is not one of these — skip it
        // rather than measure a world standing in another map's walls.
        var changesMap = ticks.Count > 0 && ChangesMap(trace, ShapeFor);

        if (changesMap)
        {
            Console.Error.WriteLine($"  (skipped: the segment changes map)");
            return;
        }

        var owner = Owners(trace, ticks.Count);

        var main = new PortedSimulation(MapLoader);
        var truth = new PortedSimulation(MapLoader);
        var predicted = new PortedSimulation(MapLoader);

        truth.LoadMap(trace.Header.Map);
        predicted.LoadMap(trace.Header.Map);

        var inputs = new PlayerInput[WorldSlots];
        var held = new PlayerInput[WorldSlots];

        void Probe(int t)
        {
            if (t % Stride != 0 || t + Windows[^1] >= ticks.Count)
                return;

            // Both arms start as exact copies of the replayed world.
            truth.SetState(main.State);
            predicted.SetState(main.State);

            var here = ticks[t].Slots;

            for (var slot = 0; slot < held.Length; slot++)
                held[slot] = slot < here.Length ? here[slot].Input : PlayerInput.None;

            var next = 0;

            for (var step = 0; step < Windows[^1]; step++)
            {
                var real = ticks[t + step].Slots;

                for (var slot = 0; slot < inputs.Length; slot++)
                    inputs[slot] = slot < real.Length ? real[slot].Input : PlayerInput.None;

                truth.Tick(inputs);

                // The prediction never learns anything: tick t's keys, all the
                // way to the horizon. Under --control it learns everything, and
                // the answer must be zero.
                predicted.Tick(_control ? inputs : held);

                if (next < Windows.Length && step + 1 == Windows[next])
                    Record(Windows[next++] - 1, t);
            }
        }

        void Record(int staleness, int t)
        {
            for (var slot = 0; slot < WorldSlots; slot++)
            {
                if (!truth.TryGetState(slot, out var a) || !predicted.TryGetState(slot, out var b))
                    continue;

                var stats = owner(t, slot) switch
                {
                    TraceParticipant.Human => human,
                    TraceParticipant.Bot => bot,
                    _ => null,
                };

                stats?.Observe(staleness, Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y))));
            }
        }

        new Replayer(main)
        {
            Compare = Fields.Trajectory,
            OnTick = Probe,
        }.Replay(
            Path.GetFileName(file), trace.Header.Map, ticks,
            ReplayMode.ClosedLoop, stopAtFirstBadTick: false);
    }

    /// <summary>True when the recording rotates onto another map.</summary>
    private static bool ChangesMap(Trace trace, Func<string, WorldShape> shapeFor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { trace.Header.Map };

        TraceParser.Parse(trace, shapeFor(trace.Header.Map), out _, out _, name =>
        {
            seen.Add(name);
            return shapeFor(name);
        });

        return seen.Count > 1;
    }

    /// <summary>
    /// Who owned a slot at a tick. A slot is reused, so the labels are
    /// tick-indexed and the newest one at or before the tick is the answer.
    /// </summary>
    private static Func<int, int, TraceParticipant> Owners(Trace trace, int tickCount)
    {
        var notes = trace.Roster.OrderBy(n => n.Tick).ToArray();

        return (tick, slot) =>
        {
            var answer = TraceParticipant.Unknown;

            foreach (var note in notes)
            {
                if (note.Tick > tick)
                    break;

                if (note.Slot == slot)
                    answer = note.Participant;
            }

            return answer;
        };
    }

    private static bool HasHuman(string file)
    {
        try
        {
            return Trace.Decode(File.ReadAllBytes(file))
                .Roster.Any(n => n.Participant == TraceParticipant.Human);
        }
        catch (Exception e) when (e is IOException or ProtocolException or InvalidDataException)
        {
            return false;
        }
    }

    private const int WorldSlots = 64;

    private static string? Flag(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private sealed class Error(string name)
    {
        private readonly Dictionary<int, List<double>> _px = [];

        public bool Any => _px.Count > 0;

        public void Observe(int staleness, double pixels)
        {
            if (!_px.TryGetValue(staleness, out var list))
                _px[staleness] = list = [];

            list.Add(pixels);
        }

        public static void PrintHeader() => Console.WriteLine(
            $"{"who",-6} {"stale",5} {"samples",12} {"mean px",9} {"p50",7} {"p90",7} "
            + $"{"p99",7} {"max",8} {"over 8px",9}");

        public void Print()
        {
            foreach (var (staleness, list) in _px.OrderBy(p => p.Key))
            {
                list.Sort();

                var over = list.Count(p => p > 8);

                Console.WriteLine(
                    $"{name,-6} {staleness,5} {list.Count,12:N0} {list.Average(),9:0.00} "
                    + $"{At(list, 0.50),7:0.00} {At(list, 0.90),7:0.00} {At(list, 0.99),7:0.00} "
                    + $"{list[^1],8:0.0} {(double)over / list.Count,9:P2}");
            }
        }

        private static double At(List<double> sorted, double q) =>
            sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }
}
