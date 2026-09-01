using System.Globalization;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// A trained policy: three dense layers, one categorical head per action head.
/// </summary>
/// <remarks>
/// <b><c>gg2-rl</c>'s phase 4 artefact, run in this process.</b> 209 observation
/// floats in, 27 logits out, split into six heads — horizontal, jump, down, fire,
/// aim, zoom — with an argmax per head. ReLU on the hidden layers, raw logits on
/// the output block; **no input normalisation**, the observation goes in as
/// <c>ObservationWriter</c> writes it.
/// <para>
/// ⚠️ <b>This reopens <c>docs/ENV.md</c>'s "no ML runtime, no inference path, no
/// weights format", deliberately.</b> That entry was written before a policy
/// existed. What it was protecting against — this repo becoming a second source
/// of truth about what a checkpoint is — is handled by reading the trainer's own
/// file rather than defining one, and by refusing any file whose stamp is not
/// this build's.
/// </para>
/// <para>
/// ⚠️ <b>Nothing here draws from <c>WorldState.Random</c></b> and nothing here is
/// in <c>SIM_UUID</c>. A policy is a bot, and this project's bots are its own —
/// the observation layout it consumes is stamped, and that is the part that must
/// not drift.
/// </para>
/// </remarks>
public sealed class PolicyNetwork
{
    private readonly float[][] _weights;
    private readonly float[][] _biases;
    private readonly int[] _widths;
    private readonly int[] _headShape;
    private readonly float[] _hiddenA;
    private readonly float[] _hiddenB;
    private readonly float[] _output;

    private PolicyNetwork(
        float[][] weights,
        float[][] biases,
        int[] widths,
        int observationLength,
        int[] headShape,
        int actionRepeat,
        string simulationUuid)
    {
        _weights = weights;
        _biases = biases;
        _widths = widths;

        ObservationLength = observationLength;
        _headShape = headShape;
        ActionRepeat = actionRepeat;
        SimulationUuid = simulationUuid;

        var widest = 0;

        foreach (var width in widths)
            widest = Math.Max(widest, width);

        _hiddenA = new float[widest];
        _hiddenB = new float[widest];
        _output = new float[widths[^1]];
    }

    /// <summary>Floats this policy expects, which must be <c>ObservationWriter.Length</c>.</summary>
    public int ObservationLength { get; }

    /// <summary>The categorical width of each action head, in wire order.</summary>
    public ReadOnlySpan<int> HeadShape => _headShape;

    /// <summary>Ticks the trainer held each decision for.</summary>
    /// <remarks>
    /// ⚠️ <b>Carried by the checkpoint because a policy trained at one repeat is a
    /// different policy at another.</b> It decides how often <see cref="PolicyBot"/>
    /// re-runs the network, and a bot that ran it every tick would be acting on a
    /// cadence the policy never saw.
    /// </remarks>
    public int ActionRepeat { get; }

    /// <summary>The simulation stamp this policy was trained against.</summary>
    public string SimulationUuid { get; }

    /// <summary>
    /// Loads a checkpoint and refuses it unless it belongs to this build.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Every check is a refusal, never a warning.</b> The <c>PROTOCOL_UUID</c>
    /// idiom this project already uses: a policy running against a different
    /// observation layout, action encoding or simulation is not slightly wrong, it
    /// is acting on numbers that do not mean what it learned they meant, and it
    /// should find out in the first millisecond.
    /// <para>
    /// ⚠️ <b>These checks are also what makes <see cref="NpyFile.ReadStrings"/>'s
    /// narrow pickle scan safe.</b> A scan that locked on to the wrong bytes
    /// produces metadata that fails them.
    /// </para>
    /// </remarks>
    public static PolicyNetwork Load(Stream npz)
    {
        ArgumentNullException.ThrowIfNull(npz);

        var entries = NpyFile.ReadArchive(npz);

        if (!entries.TryGetValue("meta", out var metaBytes))
            throw new InvalidDataException("the checkpoint has no metadata; it cannot be checked and is refused");

        var meta = NpyFile.ReadStrings(metaBytes);

        if (meta.Length < 7)
            throw new InvalidDataException($"the metadata has {meta.Length} fields, expected 7");

        // The order gg2-rl's Policy.save writes: policy version, simulation uuid,
        // action encoding, observation layout, head shape, observation length,
        // action repeat.
        var simulationUuid = meta[1];
        var actionEncoding = int.Parse(meta[2], CultureInfo.InvariantCulture);
        var layout = int.Parse(meta[3], CultureInfo.InvariantCulture);
        var headShape = meta[4]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => int.Parse(w, CultureInfo.InvariantCulture))
            .ToArray();
        var observationLength = int.Parse(meta[5], CultureInfo.InvariantCulture);
        var actionRepeat = int.Parse(meta[6], CultureInfo.InvariantCulture);

        if (!string.Equals(simulationUuid, SimulationStamp.Uuid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"this policy was trained against simulation {simulationUuid}, and this build is {SimulationStamp.Uuid}");
        }

        if (actionEncoding != SimulationStamp.ActionEncodingVersion)
        {
            throw new InvalidOperationException(
                $"policy action encoding v{actionEncoding} against this build's v{SimulationStamp.ActionEncodingVersion}");
        }

        if (layout != SimulationStamp.ObservationLayoutVersion)
        {
            throw new InvalidOperationException(
                $"policy observation layout v{layout} against this build's v{SimulationStamp.ObservationLayoutVersion}");
        }

        if (observationLength != ObservationWriter.Length)
        {
            throw new InvalidOperationException(
                $"policy wants {observationLength} observation floats, this build writes {ObservationWriter.Length}");
        }

        var expectedHeads = ActionDecoder.HeadShape;

        if (!headShape.AsSpan().SequenceEqual(expectedHeads))
        {
            throw new InvalidOperationException(
                $"policy head shape [{string.Join(',', headShape)}] against this build's "
                + $"[{string.Join(',', expectedHeads.ToArray())}]");
        }

        // Layers, in order, for as many as the file carries.
        var weights = new List<float[]>();
        var biases = new List<float[]>();
        var widths = new List<int>();
        var inputs = observationLength;

        for (var layer = 0; entries.ContainsKey($"w{layer}"); layer++)
        {
            var w = NpyFile.ReadArray(entries[$"w{layer}"]);
            var b = NpyFile.ReadArray(entries[$"b{layer}"]);

            if (w.Rows != inputs)
            {
                throw new InvalidDataException(
                    $"layer {layer} takes {w.Rows} inputs where the previous layer gives {inputs}");
            }

            if (b.Values.Length != w.Columns)
            {
                throw new InvalidDataException(
                    $"layer {layer} has {w.Columns} outputs and {b.Values.Length} biases");
            }

            weights.Add(w.Values);
            biases.Add(b.Values);
            widths.Add(w.Columns);
            inputs = w.Columns;
        }

        if (weights.Count == 0)
            throw new InvalidDataException("the checkpoint has no layers");

        var outputs = headShape.Sum();

        if (widths[^1] != outputs)
        {
            throw new InvalidOperationException(
                $"the output layer is {widths[^1]} wide and the heads want {outputs}");
        }

        return new PolicyNetwork(
            [.. weights], [.. biases], [.. widths], observationLength, headShape, actionRepeat, simulationUuid);
    }

    /// <summary>Loads a checkpoint from a path.</summary>
    public static PolicyNetwork Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var file = File.OpenRead(path);

        return Load(file);
    }

    /// <summary>
    /// Runs the network and writes the greedy index of each head.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Argmax per head, never over the product.</b> The heads are
    /// independent categoricals — that is what the factorisation means — and a
    /// single argmax over 27 logits would pick one head's index and leave the rest
    /// at zero, which is a legal-looking action nobody chose.
    /// <para>
    /// ⚠️ <b>No allocation.</b> This runs per bot per decision inside a match, and
    /// the scratch buffers are the instance's.
    /// </para>
    /// </remarks>
    /// <summary>The raw output block, for a test that compares against the trainer.</summary>
    /// <remarks>
    /// ⚠️ Exposed so the cross-language gate can compare LOGITS rather than only
    /// the argmax. Two implementations can agree on every head and still differ,
    /// and the difference is what says whether the agreement is robust or lucky.
    /// </remarks>
    public ReadOnlySpan<float> Logits(ReadOnlySpan<float> observation)
    {
        Span<int> scratch = stackalloc int[_headShape.Length];

        Act(observation, scratch);

        return _output;
    }

    public void Act(ReadOnlySpan<float> observation, Span<int> heads)
    {
        if (observation.Length != ObservationLength)
            throw new ArgumentException($"the policy wants {ObservationLength} floats", nameof(observation));

        if (heads.Length < _headShape.Length)
            throw new ArgumentException($"there are {_headShape.Length} heads", nameof(heads));

        var current = observation;
        var scratchA = _hiddenA.AsSpan();
        var scratchB = _hiddenB.AsSpan();

        for (var layer = 0; layer < _weights.Length; layer++)
        {
            var width = _widths[layer];
            var destination = layer == _weights.Length - 1
                ? _output.AsSpan(0, width)
                : (layer % 2 == 0 ? scratchA : scratchB)[..width];

            Dense(current, _weights[layer], _biases[layer], destination);

            // ReLU on the hidden layers only; the output block is logits.
            if (layer < _weights.Length - 1)
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    if (destination[i] < 0)
                        destination[i] = 0;
                }
            }

            current = destination;
        }

        var at = 0;

        for (var head = 0; head < _headShape.Length; head++)
        {
            var width = _headShape[head];
            var best = 0;

            for (var i = 1; i < width; i++)
            {
                if (current[at + i] > current[at + best])
                    best = i;
            }

            heads[head] = best;
            at += width;
        }
    }

    private static void Dense(ReadOnlySpan<float> input, float[] weights, float[] bias, Span<float> output)
    {
        var columns = output.Length;

        bias.AsSpan(0, columns).CopyTo(output);

        for (var i = 0; i < input.Length; i++)
        {
            var x = input[i];

            if (x == 0)
                continue;

            var row = i * columns;

            for (var j = 0; j < columns; j++)
                output[j] += x * weights[row + j];
        }
    }
}
