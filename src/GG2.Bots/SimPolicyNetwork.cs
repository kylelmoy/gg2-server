namespace GG2.Bots;

/// <summary>
/// A <c>gg2-sim</c> actor network, as that repo's <c>python/export_policy.py</c>
/// writes it (<c>.gg2p</c> version 1): fully connected layers, tanh between them,
/// raw logits out, split into four action heads (move, jump, fire, aim).
/// </summary>
/// <remarks>
/// <b>Not a <see cref="PolicyNetwork"/>, and not convertible into one.</b> The
/// observation (<see cref="SimPolicyObservation"/>) and the action space
/// (<see cref="SimPolicyController"/>) are gg2-sim's own, not gg2-rl's
/// <c>ObservationWriter</c>/<c>ActionDecoder</c> layout.
/// <para>
/// ⛔ <b>The forward pass is gg2-sim's <c>ref/PolicyDriver.cs</c> <c>Decide</c>
/// operation for operation</b>: float32, a bias then one multiply-add per input
/// in input order, <see cref="MathF.Tanh"/> on every layer but the last, and a
/// strict greater-than argmax per head so a tie goes to the lower index. Do not
/// vectorise it — a reordered sum moves an action on a near tie, and gg2-sim's
/// headless <c>matches</c> are the result this has to reproduce.
/// </para>
/// <para>
/// ⚠️ <b>The file carries no simulation stamp.</b> A <c>.gg2p</c> is dated by the
/// gg2-server commit gg2-sim pinned when it trained, which only gg2-sim records;
/// <c>BotBrain</c> says so on the stamp line rather than claiming a match.
/// </para>
/// </remarks>
public sealed class SimPolicyNetwork
{
    /// <summary><c>GG2P</c>, little-endian.</summary>
    public const uint Magic = 0x50324747;

    public const uint Version = 1;

    private readonly Layer[] _layers;
    private readonly int[] _heads;

    private SimPolicyNetwork(Layer[] layers, int[] heads)
    {
        _layers = layers;
        _heads = heads;

        foreach (var layer in layers)
            Widest = Math.Max(Widest, Math.Max(layer.Inputs, layer.Outputs));
    }

    /// <summary>The observation width the first layer reads.</summary>
    public int InputSize => _layers[0].Inputs;

    /// <summary>Each head's action count: move, jump, fire, aim.</summary>
    public ReadOnlySpan<int> Heads => _heads;

    /// <summary>
    /// A team net (<see cref="SimPolicyObservation.TeamSize"/> inputs) lists its
    /// own enemies and team-mates; anything else is a duel net, which observes one
    /// opponent the controller picks.
    /// </summary>
    public bool IsTeam => InputSize == SimPolicyObservation.TeamSize;

    /// <summary>The widest layer edge, for a caller's scratch buffers.</summary>
    public int Widest { get; }

    public static SimPolicyNetwork Load(string path)
    {
        using var stream = File.OpenRead(path);

        try
        {
            return Load(stream);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    public static SimPolicyNetwork Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
            throw new InvalidDataException("not a .gg2p version 1 policy");

        var layerCount = reader.ReadInt32();
        var headCount = reader.ReadInt32();

        if (layerCount < 1 || headCount != 4)
            throw new InvalidDataException($"{layerCount} layers and {headCount} heads; expected at least one layer and four heads");

        var heads = new int[headCount];

        for (var i = 0; i < heads.Length; i++)
            heads[i] = reader.ReadInt32();

        var layers = new Layer[layerCount];

        for (var l = 0; l < layers.Length; l++)
        {
            int outputs = reader.ReadInt32(), inputs = reader.ReadInt32();

            if (outputs <= 0 || inputs <= 0 || (l > 0 && inputs != layers[l - 1].Outputs))
                throw new InvalidDataException($"layer {l} is {outputs}x{inputs}, which does not chain");

            var weights = new float[outputs * inputs];

            for (var i = 0; i < weights.Length; i++)
                weights[i] = reader.ReadSingle();

            var biases = new float[outputs];

            for (var i = 0; i < biases.Length; i++)
                biases[i] = reader.ReadSingle();

            layers[l] = new Layer(outputs, inputs, weights, biases);
        }

        if (stream.CanSeek && stream.Position != stream.Length)
            throw new InvalidDataException($"{stream.Length - stream.Position} trailing bytes");

        var network = new SimPolicyNetwork(layers, heads);
        network.CheckShape();
        return network;
    }

    // Refused at load rather than discovered as an out-of-range action on a live
    // server: the head sizes are what SimPolicyController decodes against.
    private void CheckShape()
    {
        var total = 0;

        foreach (var head in _heads)
            total += head;

        if (total != _layers[^1].Outputs)
            throw new InvalidDataException($"heads sum to {total}, the last layer has {_layers[^1].Outputs} outputs");

        if (_heads[0] != 3 || _heads[1] != 2 || _heads[2] != 2)
            throw new InvalidDataException($"move/jump/fire heads are {_heads[0]}/{_heads[1]}/{_heads[2]}, expected 3/2/2");

        var aim = IsTeam ? SimPolicyController.TeamAimActions : SimPolicyController.DuelAimActions;

        if (InputSize is not (SimPolicyObservation.TeamSize or SimPolicyObservation.DuelSize or SimPolicyObservation.DuelSizeNoClasses))
        {
            throw new InvalidDataException(
                $"{InputSize} inputs; expected {SimPolicyObservation.TeamSize} (team) or "
                + $"{SimPolicyObservation.DuelSize}/{SimPolicyObservation.DuelSizeNoClasses} (duel)");
        }

        if (_heads[3] != aim)
            throw new InvalidDataException($"aim head is {_heads[3]} wide, a {(IsTeam ? "team" : "duel")} net's is {aim}");
    }

    /// <summary>
    /// The greedy action for <paramref name="observation"/>, one index per head.
    /// </summary>
    /// <param name="a">Scratch of at least <see cref="Widest"/>.</param>
    /// <param name="b">Scratch of at least <see cref="Widest"/>.</param>
    public void Decide(ReadOnlySpan<float> observation, Span<float> a, Span<float> b, Span<int> action)
    {
        observation[..InputSize].CopyTo(a);
        var x = a;
        var y = b;

        for (var l = 0; l < _layers.Length; l++)
        {
            var (o, n, w, bias) = _layers[l];
            var last = l == _layers.Length - 1;

            for (var i = 0; i < o; i++)
            {
                var s = bias[i];

                for (var j = 0; j < n; j++)
                    s += w[(i * n) + j] * x[j];

                y[i] = last ? s : MathF.Tanh(s);
            }

            var swap = x;
            x = y;
            y = swap;
        }

        for (int h = 0, at = 0; h < _heads.Length; at += _heads[h], h++)
        {
            var best = 0;

            for (var k = 1; k < _heads[h]; k++)
            {
                if (x[at + k] > x[at + best])
                    best = k;
            }

            action[h] = best;
        }
    }

    private readonly record struct Layer(int Outputs, int Inputs, float[] Weights, float[] Biases);
}
