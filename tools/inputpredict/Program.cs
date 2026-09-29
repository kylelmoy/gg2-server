// How predictable is a player's next few ticks of input?
//
// A rollback mesh (docs/SIMULATION.md, "The same build on two machines", for
// why the mesh is possible at all) has to guess what a remote player is
// pressing while their real input is still in flight. The default guess is
// "whatever they were pressing last". This measures how often that guess is
// wrong, at each horizon, for people and for this project's own bots.
//
// It reads recordings made by --record, which carry every player's keys and aim
// on every tick and label who was a person (docs/BOTAI.md item 3).
//
// ⚠️ A wrong guess is not automatically a visible correction. What a miss costs
// in pixels is a separate measurement this tool does NOT make, and the
// run-length column is the closest thing to it here.
//
// ⛔ Do not reach for `Replayer.InputLag` for that pixel cost — it was tried and
// it answers a different question. That knob feeds a whole replay inputs from a
// shifted tick and grades the CUMULATIVE drift, so its noise floor swamps the
// signal: on `port-gg_debug-anchoronplatform` the p99 trajectory error reads 284
// wire units at lag 0, where a prediction question wants ~0, and the curve
// across lags -1..-5 is non-monotonic (840, 598, 1031, 652). On the fleet's own
// recordings it is worse — 25k to 160k ticks with map rotations and hundreds of
// respawns put the p99 in the thousands at every lag, including zero.
//
// What would measure it: anchor the simulation at tick t, run h ticks twice
// from that same anchor — once on the recorded inputs, once on tick t's inputs
// held — and compare the two resulting positions. That is the counterfactual a
// rollback actually corrects, it needs no recorded position at t+h, and it
// needs a snapshot/restore on `PortedSimulation`, which does not expose one
// today.
using System.Globalization;
using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace InputPredict;

internal static class Program
{
    /// <summary>Horizons in ticks. 2-3 is one way across the Atlantic at 30 Hz.</summary>
    private static readonly int[] Horizons = [1, 2, 3, 4, 5, 7, 10];

    /// <summary>
    /// What a rollback mesh would actually predict: the movement keys. Fire and
    /// special are deliberately excluded — a mispredicted shot cannot be
    /// retracted on a stock client, so the design holds those until they are
    /// confirmed.
    /// </summary>
    private const PlayerKeys Movement =
        PlayerKeys.Left | PlayerKeys.Right | PlayerKeys.Jump | PlayerKeys.Down;

    private const PlayerKeys Action =
        PlayerKeys.Attack | PlayerKeys.Special | PlayerKeys.Taunt;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                """
                inputpredict <trace-or-directory>... [--maps <path>]

                  Measures how often "they are still holding what they were holding"
                  is wrong, at several horizons, split by people and bots.
                """);

            return 2;
        }

        // The pixel cost of a miss, rather than how often one happens.
        if (args.Contains("--cost"))
            return Cost.Run(args);

        var mapDirectory = Flag(args, "--maps") ?? MapDirectory.Resolve();
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

        if (files.Count == 0)
        {
            Console.Error.WriteLine("no .trace files found");
            return 1;
        }

        var human = new Stats("human");
        var bot = new Stats("bot");
        var read = 0;
        var skipped = 0;

        foreach (var file in files)
        {
            try
            {
                Measure(file, mapDirectory, human, bot);
                read++;
            }
            catch (Exception e) when (e is IOException or ProtocolException
                or InvalidDataException or ArgumentException)
            {
                skipped++;
                Console.Error.WriteLine($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        Console.WriteLine($"{read} trace(s) read, {skipped} skipped");
        Console.WriteLine();

        Stats.PrintHeader();
        human.Print();
        bot.Print();

        Console.WriteLine();
        human.PrintRunLengths();
        bot.PrintRunLengths();

        return human.Samples == 0 ? 1 : 0;
    }

    private static void Measure(string file, string mapDirectory, Stats human, Stats bot)
    {
        var trace = Trace.Decode(File.ReadAllBytes(file));

        WorldShape ShapeFor(string name) => GameModes.ShapeOf(
            LevelMap.FromPng(name, File.ReadAllBytes(Path.Combine(mapDirectory, name + ".png")))
                .ForArea(1));

        var ticks = TraceParser.Parse(
            trace, ShapeFor(trace.Header.Map), out _, out _, ShapeFor);

        // A slot is reused: labels are tick-indexed for exactly that reason, so
        // a slot's owner is whoever the newest label at or before this tick
        // says it is. Attributing a whole recording to the last occupant is the
        // mistake TraceRosterNote's remarks name.
        var owner = new TraceParticipant[WorldShapeSlots];
        var noteIndex = 0;
        var notes = trace.Roster.OrderBy(n => n.Tick).ToArray();

        // Per slot, the last input seen and how long it has been held.
        var last = new PlayerInput[WorldShapeSlots];
        var held = new int[WorldShapeSlots];
        var alive = new bool[WorldShapeSlots];

        for (var t = 0; t < ticks.Count; t++)
        {
            while (noteIndex < notes.Length && notes[noteIndex].Tick <= t)
            {
                var note = notes[noteIndex++];
                if (note.Slot < owner.Length)
                    owner[note.Slot] = note.Participant;
            }

            var tick = ticks[t];

            for (var slot = 0; slot < tick.Slots.Length && slot < owner.Length; slot++)
            {
                var here = tick.Slots[slot];

                if (!here.HasCharacter)
                {
                    alive[slot] = false;
                    held[slot] = 0;
                    continue;
                }

                var stats = owner[slot] switch
                {
                    TraceParticipant.Human => human,
                    TraceParticipant.Bot => bot,
                    _ => null,
                };

                if (stats is null)
                {
                    alive[slot] = false;
                    continue;
                }

                if (alive[slot])
                {
                    if ((last[slot].Keys & Movement) == (here.Input.Keys & Movement))
                    {
                        held[slot]++;
                    }
                    else
                    {
                        stats.Run(held[slot] + 1);
                        held[slot] = 0;
                    }
                }

                alive[slot] = true;
                last[slot] = here.Input;

                // The prediction: this tick's keys, held for h more ticks. It is
                // only a fair question while the same character is there the
                // whole way — a death or a departure is not a mispredicted key.
                foreach (var h in Horizons)
                {
                    if (t + h >= ticks.Count)
                        continue;

                    if (!Continuous(ticks, slot, t, h))
                        continue;

                    var future = ticks[t + h].Slots[slot].Input;

                    stats.Observe(
                        h,
                        movementMiss: (future.Keys & Movement) != (here.Input.Keys & Movement),
                        actionMiss: (future.Keys & Action) != (here.Input.Keys & Action),
                        aimDelta: AimDegrees(here.Input.NetAimDirection, future.NetAimDirection));
                }
            }
        }
    }

    /// <summary>
    /// True when the same character is present on every tick of the window, so
    /// a respawn or a disconnect is not counted as a mispredicted key.
    /// </summary>
    private static bool Continuous(IReadOnlyList<TraceTick> ticks, int slot, int from, int horizon)
    {
        for (var i = from; i <= from + horizon; i++)
        {
            var slots = ticks[i].Slots;

            if (slot >= slots.Length || !slots[slot].HasCharacter)
                return false;
        }

        return true;
    }

    /// <summary>Shortest angle between two wire aim directions, in degrees.</summary>
    private static double AimDegrees(ushort a, ushort b)
    {
        var delta = Math.Abs((a * 360.0 / 65536) - (b * 360.0 / 65536));
        return delta > 180 ? 360 - delta : delta;
    }

    private const int WorldShapeSlots = 256;

    private static string? Flag(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private sealed class Stats(string name)
    {
        private readonly long[] _movementMiss = new long[64];
        private readonly long[] _actionMiss = new long[64];
        private readonly long[] _seen = new long[64];
        private readonly List<double>[] _aim = new List<double>[64];
        private readonly List<int> _runs = [];

        public long Samples { get; private set; }

        public void Observe(int horizon, bool movementMiss, bool actionMiss, double aimDelta)
        {
            _seen[horizon]++;
            if (movementMiss) _movementMiss[horizon]++;
            if (actionMiss) _actionMiss[horizon]++;

            (_aim[horizon] ??= []).Add(aimDelta);
            Samples++;
        }

        public void Run(int length) => _runs.Add(length);

        public static void PrintHeader() => Console.WriteLine(
            $"{"who",-6} {"h",3} {"samples",12} {"move miss",10} {"action miss",12} "
            + $"{"aim p50",8} {"aim p90",8} {"aim p99",8}");

        public void Print()
        {
            foreach (var h in Horizons)
            {
                if (_seen[h] == 0)
                    continue;

                var aim = _aim[h] ?? [];
                aim.Sort();

                Console.WriteLine(
                    $"{name,-6} {h,3} {_seen[h],12:N0} "
                    + $"{(double)_movementMiss[h] / _seen[h],9:P2} "
                    + $"{(double)_actionMiss[h] / _seen[h],11:P2} "
                    + $"{Percentile(aim, 0.50),7:0.0}° {Percentile(aim, 0.90),7:0.0}° "
                    + $"{Percentile(aim, 0.99),7:0.0}°");
            }
        }

        public void PrintRunLengths()
        {
            if (_runs.Count == 0)
                return;

            _runs.Sort();
            var asDouble = _runs.ConvertAll(r => (double)r);

            Console.WriteLine(
                $"{name,-6} movement keys held for a median of {Percentile(asDouble, 0.50):0} tick(s), "
                + $"p90 {Percentile(asDouble, 0.90):0}, p99 {Percentile(asDouble, 0.99):0}, "
                + $"mean {asDouble.Average():0.0} ({_runs.Count:N0} runs)");
        }

        private static double Percentile(List<double> sorted, double q) =>
            sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }
}
