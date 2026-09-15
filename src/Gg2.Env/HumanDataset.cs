using System.Globalization;
using System.Text;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Env;

/// <summary>
/// Turns recorded traces into <c>(observation, action)</c> rows.
/// </summary>
/// <remarks>
/// <b><c>docs/BOTAI.md</c> item 3's consumer.</b> The recorder writes what the
/// server broadcast; this reconstructs the world that produced it and pairs each
/// tick's state with the action the recording says was taken on it. The
/// reconstruction is <see cref="Replayer"/> — the same replay the parity
/// fixtures run — so there is one path from a trace to a world, not two.
/// <para>
/// <b>Nothing here decides what the data means.</b> It emits columns and a
/// manifest; the modelling choices — which rows to keep, how to weight them,
/// whether a saturated turn is a label or a discard — belong to <c>gg2-rl</c>.
/// What this owes that repo is columns whose meaning is fixed and stated, and a
/// manifest that says what the rows were made from.
/// </para>
/// <para>
/// <b>Open-loop, and that is the conservative choice.</b> A trace this server
/// recorded of itself should replay closed-loop exactly — same build, same
/// inputs — but "should" is the word doing the work, and a reconstruction that
/// had quietly drifted would produce observations of a world no player ever saw.
/// Open-loop puts the recorded positions back every seventh tick, and the drift
/// measured against them is reported beside the rows rather than discarded.
/// </para>
/// </remarks>
public static class HumanDataset
{
    /// <summary>How a trace is turned into rows.</summary>
    public sealed record Options
    {
        /// <summary>Where the maps are.</summary>
        public required string Maps { get; init; }

        /// <summary>Where the <c>.npy</c> files and the manifest go.</summary>
        public required string Output { get; init; }

        /// <summary>
        /// Ticks a decision is held for, which is the stride rows are sampled
        /// at.
        /// </summary>
        /// <remarks>
        /// <b>A row is one decision, not one tick, and the aim delta is measured
        /// over the same span.</b> Sampling every tick and letting a consumer
        /// subsample would be lossless for the observation and wrong for the
        /// action: a policy at repeat k turns once per k ticks against a cap of
        /// <c>ActionDecoder.MaxTurnDegreesPerDecision</c>, so a per-tick delta
        /// compared against that cap saturates k times too rarely and every
        /// conclusion drawn about the cap is off by the same factor.
        /// </remarks>
        public int ActionRepeat { get; init; } = 1;

        /// <summary>
        /// Emit bot rows as well as human ones.
        /// </summary>
        /// <remarks>
        /// <b>Off, because the default has to be the safe direction.</b> The
        /// point of a public recording is the rows nobody in this repo wrote;
        /// mixing this project's own bots into that silently is the failure
        /// <c>TraceParticipant</c> exists to prevent. On, every row still carries
        /// its own participant column, so the two never become
        /// indistinguishable again.
        /// </remarks>
        public bool IncludeBots { get; init; }
    }

    /// <summary>What a run produced, for the caller to print.</summary>
    public sealed record Report
    {
        public required int Traces { get; init; }

        public required int Rows { get; init; }

        public required int Skipped { get; init; }

        public required int Saturated { get; init; }

        /// <summary>Traces refused, with the reason, and never silently dropped.</summary>
        public required IReadOnlyList<string> Refused { get; init; }

        public override string ToString()
        {
            var text = new StringBuilder();

            text.Append(CultureInfo.InvariantCulture,
                $"{Rows} row(s) from {Traces} trace(s); {Skipped} tick(s) skipped");

            if (Rows > 0)
            {
                var share = 100.0 * Saturated / Rows;

                text.Append(CultureInfo.InvariantCulture,
                    $"; {Saturated} ({share:0.0}%) turns exceeded the " +
                    $"{ActionDecoder.MaxTurnDegreesPerDecision:0}deg cap");
            }

            foreach (var refusal in Refused)
                text.Append(CultureInfo.InvariantCulture, $"\n  refused: {refusal}");

            return text.ToString();
        }
    }

    /// <summary>One emitted decision.</summary>
    private readonly record struct Row
    {
        public required int Trace { get; init; }

        public required int Tick { get; init; }

        public required byte Slot { get; init; }

        public required TraceParticipant Participant { get; init; }

        public required int Life { get; init; }

        public required EncodedAction Encoded { get; init; }
    }

    /// <summary>
    /// Reads every trace in <paramref name="traces"/> and writes the dataset.
    /// </summary>
    public static Report Build(IReadOnlyList<string> traces, Options options)
    {
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ActionRepeat < 1)
            throw new ArgumentException("an action repeat is at least 1", nameof(options));

        Directory.CreateDirectory(options.Output);

        var observations = new List<float>();
        var rows = new List<Row>();
        var refused = new List<string>();
        var sources = new List<string>();

        var skipped = 0;

        for (var i = 0; i < traces.Count; i++)
        {
            try
            {
                skipped += Extract(traces[i], i, options, observations, rows);
                sources.Add(Describe(traces[i]));
            }
            catch (Exception e) when (e is ProtocolException or IOException or InvalidDataException)
            {
                // Named, never counted. A trace dropped without its name cannot
                // be chased, and a corpus quietly missing a third of its files
                // reads exactly like a corpus that was always this size.
                refused.Add($"{Path.GetFileName(traces[i])}: {e.Message}");
            }
        }

        var saturated = 0;
        foreach (var row in rows)
        {
            if (row.Encoded.Saturated)
                saturated++;
        }

        Write(options.Output, observations, rows, sources, refused, options);

        return new Report
        {
            Traces = sources.Count,
            Rows = rows.Count,
            Skipped = skipped,
            Saturated = saturated,
            Refused = refused,
        };
    }

    /// <summary>Replays one trace and appends its rows. Returns ticks skipped.</summary>
    private static int Extract(
        string path,
        int index,
        Options options,
        List<float> observations,
        List<Row> rows)
    {
        var trace = Trace.Decode(File.ReadAllBytes(path));

        if (trace.Header.ReferenceVersion != GameConstants.Version)
        {
            throw new InvalidDataException(
                $"recorded against reference version {trace.Header.ReferenceVersion}, " +
                $"this build targets {GameConstants.Version}");
        }

        // ⛔ A v1 trace carries no labels at all, so every slot in it reads as
        // Unknown. Refused rather than treated as human: the whole reason the
        // labels exist is that a bot and a person are indistinguishable once the
        // bytes are on disk, and "assume human" is the exact mistake that makes
        // this project's own scripted layer look like training data.
        if (trace.Roster.Count == 0)
        {
            throw new InvalidDataException(
                "no roster labels — a trace recorded before format v2 cannot say " +
                "which slots were people, and assuming they all were is the error " +
                "the labels exist to prevent");
        }

        var map = LoadMap(trace.Header.Map, options.Maps);
        var shape = GameModes.ShapeOf(map.ForArea(1));

        var ticks = TraceParser.Parse(
            trace, shape, out _, out var stopped, name => ShapeOf(name, options.Maps));

        if (stopped is not null)
        {
            // Reported through the manifest rather than thrown: a capture cut
            // short by a restart is a real recording of everything before the
            // cut, and this is the common case on a server that gets deployed.
            // What must not happen is that it goes unsaid.
            Console.Error.WriteLine($"{Path.GetFileName(path)}: {stopped}");
        }

        var simulation = new PortedSimulation(name => LoadMap(name, options.Maps));

        var labels = new RosterLabels(trace.Roster);
        var aim = new double[WorldState.MaxSlots];
        var seen = new bool[WorldState.MaxSlots];
        var life = new int[WorldState.MaxSlots];
        var skipped = 0;

        var replayer = new Replayer(simulation)
        {
            // Trajectories and hp. The dataset is about what a player saw and
            // did; holding the replay to an ammo count would refuse traces over
            // a field no observation column reads.
            Compare = Fields.Trajectory | Fields.Hp,
            OnTick = tick =>
            {
                labels.Advance(tick);

                if (tick % options.ActionRepeat != 0)
                    return;

                var slots = ticks[tick].Slots;

                for (var slot = 0; slot < slots.Length && slot < WorldState.MaxSlots; slot++)
                {
                    if (!Emit(
                            simulation, options, labels, slots, slot, tick, index,
                            aim, seen, life, observations, rows))
                    {
                        skipped++;
                    }
                }
            },
        };

        // Not stopped at the first bad tick: a divergence is a fact about the
        // reconstruction that the manifest reports, and truncating a
        // half-hour recording at its first disagreement would silently shrink
        // the corpus rather than describe it.
        replayer.Replay(
            Path.GetFileName(path), trace.Header.Map, ticks,
            ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        return skipped;
    }

    /// <summary>Emits one slot's row, or says why it could not.</summary>
    private static bool Emit(
        PortedSimulation simulation,
        Options options,
        RosterLabels labels,
        TraceSlot[] slots,
        int slot,
        int tick,
        int trace,
        double[] aim,
        bool[] seen,
        int[] life,
        List<float> observations,
        List<Row> rows)
    {
        var participant = labels.Of(slot);

        if (participant != TraceParticipant.Human && !options.IncludeBots)
            return false;

        if (participant == TraceParticipant.Unknown)
            return false;

        var collision = simulation.Collision;

        if (collision is null || !simulation.State.IsAlive(slot))
        {
            // A dead or spectating slot breaks the decision chain: the next aim
            // this player has is wherever they respawn pointing, not a turn from
            // where they were. Forgetting the previous aim is what stops a
            // respawn being recorded as a huge flick.
            seen[slot] = false;
            return false;
        }

        var view = new WorldView(in simulation.State, collision);
        ref readonly var character = ref view.Character(slot);

        // The Quote has no column in the observation layout and
        // ObservationWriter throws rather than encode it as no class at all. A
        // public server has people who pick it, so this is a live path, not a
        // theoretical one.
        if (character.Class == PlayerClass.Quote)
            return false;

        var input = slots[slot].Input;

        // The first decision of a life has no previous aim, so it has no delta.
        // Recorded as the anchor for the next one and otherwise dropped: a row
        // whose aim head was invented is a row that teaches an invented turn.
        if (!seen[slot])
        {
            aim[slot] = input.AimDirectionDegrees;
            seen[slot] = true;
            life[slot]++;
            return false;
        }

        var encoded = ActionEncoder.Encode(in input, aim[slot], in character);
        aim[slot] = input.AimDirectionDegrees;

        var at = observations.Count;
        observations.AddRange(new float[ObservationWriter.Length]);

        ObservationWriter.Write(
            in view, slot, System.Runtime.InteropServices.CollectionsMarshal
                .AsSpan(observations)[at..]);

        rows.Add(new Row
        {
            Trace = trace,
            Tick = tick,
            Slot = (byte)slot,
            Participant = participant,
            Life = life[slot],
            Encoded = encoded,
        });

        return true;
    }

    private static LevelMap LoadMap(string map, string directory)
    {
        var png = Path.Combine(directory, map + ".png");

        if (!File.Exists(png))
            throw new IOException($"no map at {png}");

        return LevelMap.FromPng(map, File.ReadAllBytes(png));
    }

    private static WorldShape ShapeOf(string map, string directory)
    {
        try
        {
            return GameModes.ShapeOf(LoadMap(map, directory).ForArea(1));
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static string Describe(string path)
    {
        var trace = Trace.Decode(File.ReadAllBytes(path));

        return string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(path)}\t{trace.Header.Map}\t{trace.Header.Source}\t" +
            $"{trace.Header.RecordedUtc:u}");
    }

    private static void Write(
        string output,
        List<float> observations,
        List<Row> rows,
        List<string> sources,
        List<string> refused,
        Options options)
    {
        var count = rows.Count;
        var width = ObservationWriter.Length;

        var actions = new byte[count * 6];
        var turns = new float[count];
        var saturated = new byte[count];
        var slots = new byte[count];
        var participants = new byte[count];
        var ticks = new int[count];
        var traces = new int[count];
        var lives = new int[count];

        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            var action = row.Encoded.Action;

            // The order EnvProtocol carries the heads in, and the order a
            // trained policy's output block is split in. A column order of its
            // own here would be a second definition of the action space.
            actions[(i * 6) + 0] = action.Horizontal;
            actions[(i * 6) + 1] = action.Jump;
            actions[(i * 6) + 2] = action.Down;
            actions[(i * 6) + 3] = action.Fire;
            actions[(i * 6) + 4] = action.AimDelta;
            actions[(i * 6) + 5] = action.Zoom;

            turns[i] = (float)row.Encoded.TurnDegrees;
            saturated[i] = (byte)(row.Encoded.Saturated ? 1 : 0);
            slots[i] = row.Slot;
            participants[i] = (byte)row.Participant;
            ticks[i] = row.Tick;
            traces[i] = row.Trace;
            lives[i] = row.Life;
        }

        Save(output, "observation.npy", s =>
            NpyWriter.WriteFloat2(s, System.Runtime.InteropServices.CollectionsMarshal
                .AsSpan(observations), count, width));

        Save(output, "action.npy", s => NpyWriter.WriteByte2(s, actions, count, 6));
        Save(output, "turn_degrees.npy", s => NpyWriter.WriteFloat1(s, turns));
        Save(output, "saturated.npy", s => NpyWriter.WriteByte1(s, saturated));
        Save(output, "slot.npy", s => NpyWriter.WriteByte1(s, slots));
        Save(output, "participant.npy", s => NpyWriter.WriteByte1(s, participants));
        Save(output, "tick.npy", s => NpyWriter.WriteInt1(s, ticks));
        Save(output, "trace.npy", s => NpyWriter.WriteInt1(s, traces));
        Save(output, "life.npy", s => NpyWriter.WriteInt1(s, lives));

        File.WriteAllText(
            Path.Combine(output, "manifest.txt"),
            Manifest(count, width, sources, refused, options));
    }

    private static void Save(string directory, string name, Action<Stream> write)
    {
        using var stream = File.Create(Path.Combine(directory, name));
        write(stream);
    }

    /// <summary>
    /// What the rows were made from.
    /// </summary>
    /// <remarks>
    /// <b>The stamp is the load-bearing line.</b> A dataset's observations are
    /// the output of a particular physics and a particular encoder, so a
    /// checkpoint trained on rows from another <c>SIM_UUID</c> is trained on a
    /// different game — the same rule <c>gg2-rl/docs/CONTRACT.md</c> enforces
    /// for a checkpoint, one layer further back.
    /// </remarks>
    private static string Manifest(
        int rows,
        int width,
        List<string> sources,
        List<string> refused,
        Options options)
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"rows\t{rows}\n");
        text.Append(CultureInfo.InvariantCulture, $"observation_width\t{width}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"observation_layout\t{ObservationWriter.LayoutVersion}\n");
        text.Append(CultureInfo.InvariantCulture, $"sim_uuid\t{SimulationStamp.Uuid}\n");
        text.Append(CultureInfo.InvariantCulture, $"action_repeat\t{options.ActionRepeat}\n");
        text.Append(CultureInfo.InvariantCulture, $"aim_bins\t{ActionDecoder.AimBins}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"aim_centre_bin\t{ActionDecoder.AimCentreBin}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"max_turn_degrees\t{ActionDecoder.MaxTurnDegreesPerDecision}\n");
        text.Append(CultureInfo.InvariantCulture, $"include_bots\t{options.IncludeBots}\n");
        text.Append("action_heads\thorizontal,jump,down,fire,aim_delta,zoom\n");
        text.Append("participant\t0=unknown,1=human,2=bot\n");

        foreach (var source in sources)
            text.Append(CultureInfo.InvariantCulture, $"trace\t{source}\n");

        foreach (var refusal in refused)
            text.Append(CultureInfo.InvariantCulture, $"refused\t{refusal}\n");

        return text.ToString();
    }

    /// <summary>
    /// Replays the label table alongside the ticks, so a lookup is a read.
    /// </summary>
    /// <remarks>
    /// <b>The labels are tick-indexed because a slot is reused</b> — the roster
    /// compacts on a departure, so one slot is several people over a recording.
    /// This walks the notes in order and keeps the current occupant per slot,
    /// which is the same shape the recorder wrote them in.
    /// </remarks>
    private sealed class RosterLabels(IReadOnlyList<TraceRosterNote> notes)
    {
        private readonly TraceParticipant[] _current =
            new TraceParticipant[WorldState.MaxSlots];

        private int _at;

        /// <summary>Applies every label that takes effect at or before this tick.</summary>
        public void Advance(int tick)
        {
            while (_at < notes.Count && notes[_at].Tick <= tick)
            {
                var note = notes[_at++];

                if (note.Slot < _current.Length)
                    _current[note.Slot] = note.Participant;
            }
        }

        public TraceParticipant Of(int slot) =>
            slot < _current.Length ? _current[slot] : TraceParticipant.Unknown;
    }
}
