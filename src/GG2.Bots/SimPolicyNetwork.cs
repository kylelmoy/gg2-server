namespace GG2.Bots;

/// <summary>
/// A <c>gg2-sim</c> actor network, as that repo's <c>python/export_policy.py</c>
/// writes it (<c>.gg2p</c> version 1): fully connected layers, tanh between them,
/// raw logits out, split into four action heads (move, jump, fire, aim), or
/// five in a class layout's net (the sniper's scope level, the engineer's build
/// command, or the Special key for the demoman, pyro, medic and spy).
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

    /// <summary>Each head's action count: move, jump, fire, aim, and a zoom net's scope.</summary>
    public ReadOnlySpan<int> Heads => _heads;

    /// <summary>
    /// A net on gg2-sim's base layout v2 (<c>env/env.h</c>, <c>SentryBlockObs</c>):
    /// any team layout <see cref="SimPolicyObservation.SentryColumns"/> wider, with
    /// the sentry block and the enemies listed as the body sees them. Told apart by
    /// width alone, as gg2-sim's <c>PolicyDriver.V2</c> is: no v1 team width is
    /// another's plus ten.
    /// </summary>
    public bool IsV2 => IsTeamWidth(InputSize - SimPolicyObservation.SentryColumns);

    /// <summary>The input width without a v2 net's sentry block: which layout it is.</summary>
    private int LayoutSize => IsV2 ? InputSize - SimPolicyObservation.SentryColumns : InputSize;

    /// <summary>
    /// A team net (<see cref="SimPolicyObservation.TeamSize"/> inputs, or a class
    /// layout's, either version) lists its own enemies and team-mates; anything
    /// else is a duel net, which observes one opponent the controller picks.
    /// </summary>
    public bool IsTeam => IsTeamWidth(LayoutSize);

    private static bool IsTeamWidth(int width) => width is SimPolicyObservation.TeamSize || IsFiveHeadWidth(width);

    private static bool IsFiveHeadWidth(int width) => width is SimPolicyObservation.ZoomSize or SimPolicyObservation.DemoSize
        or SimPolicyObservation.PyroSize or SimPolicyObservation.MedicSize or SimPolicyObservation.EngineerSize
        or SimPolicyObservation.SpySize;

    /// <summary>
    /// A sniper net in gg2-sim's zoom layout: a team net with
    /// <see cref="SimPolicyObservation.ZoomSize"/> inputs and a fifth head, the
    /// scope level. Detected by both, as gg2-sim's <c>PolicyDriver.Zoom</c> is.
    /// </summary>
    public bool IsZoom => LayoutSize == SimPolicyObservation.ZoomSize && _heads.Length == ZoomHeads;

    /// <summary>
    /// A demoman net in gg2-sim's demo layout (<c>31f42ef</c>): a team net with
    /// <see cref="SimPolicyObservation.DemoSize"/> inputs and a fifth head,
    /// Special — the detonate, a key held like Attack. Detected by both, as
    /// gg2-sim's <c>PolicyDriver.Demo</c> is.
    /// </summary>
    public bool IsDemo => LayoutSize == SimPolicyObservation.DemoSize && _heads.Length == ZoomHeads;

    /// <summary>
    /// A pyro net in gg2-sim's pyro layout: a team net with
    /// <see cref="SimPolicyObservation.PyroSize"/> inputs and a fifth head,
    /// Special — the airblast, held like the demo net's detonate. Detected by
    /// both, as gg2-sim's <c>PolicyDriver.Pyro</c> is.
    /// </summary>
    public bool IsPyro => LayoutSize == SimPolicyObservation.PyroSize && _heads.Length == ZoomHeads;

    /// <summary>
    /// A medic net in gg2-sim's medic layout (<c>4a8d9ca</c>): a team net with
    /// <see cref="SimPolicyObservation.MedicSize"/> inputs and a fifth head,
    /// Special (needles, or the uber), whose relative aim groups 1 and 2 aim at
    /// the listed team-mates. Detected by both, as gg2-sim's
    /// <c>PolicyDriver.Medic</c> is.
    /// </summary>
    public bool IsMedic => LayoutSize == SimPolicyObservation.MedicSize && _heads.Length == ZoomHeads;

    /// <summary>
    /// An engineer net in gg2-sim's engineer layout: a team net with
    /// <see cref="SimPolicyObservation.EngineerSize"/> inputs and a fifth head,
    /// the build command on every tick of the decision (<see cref="IBuildController"/>).
    /// </summary>
    public bool IsEngineer => LayoutSize == SimPolicyObservation.EngineerSize && _heads.Length == ZoomHeads;

    /// <summary>
    /// A spy net in gg2-sim's spy layout: a team net with
    /// <see cref="SimPolicyObservation.SpySize"/> inputs and a fifth head,
    /// Special — the cloak toggle on its press (Attack stabs while cloaked).
    /// </summary>
    public bool IsSpy => LayoutSize == SimPolicyObservation.SpySize && _heads.Length == ZoomHeads;

    /// <summary>A five-head net whose fifth head is the Special key, held for the decision.</summary>
    public bool FifthIsSpecial => IsDemo || IsPyro || IsMedic || IsSpy;

    /// <summary>A class layout's head count (zoom, demo, pyro, medic, engineer, spy); every other net has four.</summary>
    public const int ZoomHeads = 5;

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

        if (layerCount < 1 || headCount is not (4 or ZoomHeads))
            throw new InvalidDataException($"{layerCount} layers and {headCount} heads; expected at least one layer and four or five heads");

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

        if (!IsTeam && InputSize is not (SimPolicyObservation.DuelSize or SimPolicyObservation.DuelSizeNoClasses))
        {
            throw new InvalidDataException(
                $"{InputSize} inputs; expected {SimPolicyObservation.TeamSize} (team), {SimPolicyObservation.ZoomSize} (zoom), "
                + $"{SimPolicyObservation.DemoSize} (demo), {SimPolicyObservation.PyroSize} (pyro), {SimPolicyObservation.MedicSize} (medic), "
                + $"{SimPolicyObservation.EngineerSize} (engineer), {SimPolicyObservation.SpySize} (spy), any of those plus "
                + $"{SimPolicyObservation.SentryColumns} (v2), or {SimPolicyObservation.DuelSize}/{SimPolicyObservation.DuelSizeNoClasses} (duel)");
        }

        // Both or neither: a fifth head on a net that does not observe what it
        // acts on, or a class layout's observation with no head to act on it, is
        // not a layout gg2-sim exports.
        if (IsFiveHeadWidth(LayoutSize) != (_heads.Length == ZoomHeads))
        {
            throw new InvalidDataException(
                $"{InputSize} inputs and {_heads.Length} heads; a zoom net has {SimPolicyObservation.ZoomSize}, a demo net "
                + $"{SimPolicyObservation.DemoSize}, a pyro net {SimPolicyObservation.PyroSize}, a medic net "
                + $"{SimPolicyObservation.MedicSize}, an engineer net {SimPolicyObservation.EngineerSize} and a spy net "
                + $"{SimPolicyObservation.SpySize} ({SimPolicyObservation.SentryColumns} more each on v2), each with {ZoomHeads}");
        }

        if (_heads.Length == ZoomHeads && _heads[4] != 2)
            throw new InvalidDataException($"fifth head is {_heads[4]} wide, expected 2");

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
