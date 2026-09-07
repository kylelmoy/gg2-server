using System.Globalization;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// A trained policy: three dense layers, one categorical head per action head.
/// </summary>
/// <remarks>
/// <b><c>gg2-rl</c>'s phase 4 artefact, run in this process.</b> 211 observation
/// floats in, 27 logits out, split into six heads — horizontal, jump, down, fire,
/// aim, zoom — with an argmax per head. ReLU on the hidden layers, raw logits on
/// the output block; **no input normalisation**, the observation goes in as
/// <c>ObservationWriter</c> writes it.
/// <para>
/// <b>No weights format lives in this repo.</b> The risk that would guard against
/// — this repo becoming a second source of truth about what a checkpoint is — is
/// handled by reading the trainer's own file rather than defining one, and by
/// refusing any file whose stamp is not this build's.
/// </para>
/// <para>
/// <b>Nothing here draws from <c>WorldState.Random</c></b> and nothing here is
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
    private readonly PlayerClass[] _trainedClasses;

    private PolicyNetwork(
        float[][] weights,
        float[][] biases,
        int[] widths,
        int observationLength,
        int[] headShape,
        int actionRepeat,
        string simulationUuid,
        PlayerClass[] trainedClasses)
    {
        _trainedClasses = trainedClasses;
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

    /// <summary>
    /// What a checkpoint is assumed to have trained on when it does not say.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This is a FALLBACK for a checkpoint written before the class set was
    /// recorded, not a description of any policy you hold.</b> Ask the network you
    /// loaded — <see cref="TrainedClasses"/> — unless you genuinely have no
    /// checkpoint in hand.
    /// <para>
    /// <b>Five of the nine class columns are constant zero for every decision of
    /// every training run</b>, self block and enemy block, so their first-layer
    /// weights are still the random draw. Driving a class outside the set does not
    /// tell the policy which class it is; it injects untrained noise.
    /// </para>
    /// <para>
    /// <b>The set is not a prefix.</b> Demoman is <see cref="PlayerClass"/> index 3
    /// and Heavy is 6, so the untrained columns here are 2, 4, 5, 7 and 8 —
    /// interleaved. Anything that assumes "the first four" is wrong, and
    /// <c>rest3_s1_iter12000.npz</c> (5, 7, 8) is the fixture that proves it.
    /// </para>
    /// <para>
    /// ⚠️ <b>Sniper was DROPPED from this set on 2026-09-03</b> and demoman added,
    /// at the <c>gg2-rl</c> lane's request. A checkpoint trained before that date
    /// learned a sniper and did not learn a demoman — which is exactly what no
    /// stamp can express and what <see cref="TrainedClasses"/> now carries.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<PlayerClass> DefaultTrainedClasses =>
        [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman, PlayerClass.Heavy];

    /// <summary>The classes THIS checkpoint was trained on.</summary>
    /// <remarks>
    /// <b>A property of the file, because it always was one.</b> It is
    /// <c>meta[7]</c>, written by <c>gg2-rl</c>'s <c>Policy.save</c> from
    /// 2026-09-06 as comma-separated <see cref="PlayerClass"/> names.
    /// <para>
    /// ⛔ <b>What a compile-time constant cost, measured by the training lane
    /// before this landed.</b> Its three shipping checkpoint families train three
    /// different sets. Under the old static, a <c>control</c>-family checkpoint
    /// would have DRIVEN its untrained demoman (mirror 36.08%) and REFUSED its
    /// trained sniper (89.08%) — with no stamp moving, no handshake field
    /// disagreeing and no test failing. The same failure class as
    /// <c>SIM_UUID</c> not covering the bot layer: the information that would
    /// catch it was real, known, and on no surface the loader read.
    /// </para>
    /// <para>
    /// ⚠️ <b>A set that differs is a ROUTING fact, not a safety one.</b> This is
    /// deliberately not on the stamp surface: a policy trained on other classes is
    /// not invalid, it is differently applicable, and refusing to load it would be
    /// wrong. That is why the loader records it and nothing refuses over it.
    /// </para>
    /// <para>
    /// An absent field — every checkpoint written before 2026-09-06 — and an empty
    /// one both fall back to <see cref="DefaultTrainedClasses"/>. The trainer
    /// writes the field unconditionally and uses the empty string for "no set
    /// recorded", so the two cases stay distinguishable in the FILE even though
    /// this build treats them alike.
    /// </para>
    /// <para>
    /// ⚠️⚠️ <b>"TRAINED ON" IS NOT "SHOULD DRIVE", and
    /// <see cref="PolicyController.Drives"/> currently treats them as the same
    /// thing.</b> A class can be trained and still not be worth driving: measured
    /// by the training lane on 2026-09-06, <c>rest3</c> records
    /// <c>ENGINEER,SPY,PYRO</c> and its ENGINEER mirrors at 50.67% against a 50%
    /// null with a 2·SE of 1.74 — statistically indistinguishable from the
    /// scripted engineer it would replace. The set they would SHIP is
    /// <c>{pyro, spy}</c>, a strict subset of the set the file records.
    /// <para>
    /// ⛔ <b>That is a known limitation and NOT a reason to redefine this field.</b>
    /// "What it trained on" is the honest content of <c>meta[7]</c> and a loader
    /// that quietly narrowed it would be lying about the file. A should-drive set,
    /// if one is ever wanted, is a SECOND field — and it is a judgement the
    /// training lane owns, since it comes from their gate and not from anything
    /// this build can see.
    /// </para>
    /// <para>
    /// ⛔ <b>Do not try to price a class's inclusion with the match bench — it is
    /// structurally unable to answer.</b> <c>AllowedClasses</c> and
    /// <see cref="PolicyController.Drives"/> read the SAME set, so the roster on
    /// the field and the classes driven cannot be varied independently: dropping
    /// engineer to price it also removes engineers from the match, which is a
    /// different match rather than a cleaner comparison. The same limit makes one
    /// tier's contribution inside a mixed match, and a multi-tier union,
    /// unmeasurable while the bench loads one checkpoint. Established by the
    /// training lane on 2026-09-06 over 67,200 matches; see
    /// <c>docs/TRAPS-BOTS.md</c>, "should drive" cannot be priced by this bench.
    /// </para>
    /// </para>
    /// </remarks>
    public ReadOnlySpan<PlayerClass> TrainedClasses => _trainedClasses;

    /// <summary>Whether this checkpoint was trained on <paramref name="playerClass"/>.</summary>
    public bool IsTrained(PlayerClass playerClass)
    {
        foreach (var trained in _trainedClasses)
        {
            if (trained == playerClass)
                return true;
        }

        return false;
    }

    /// <summary>Floats this policy expects, which must be <c>ObservationWriter.Length</c>.</summary>
    public int ObservationLength { get; }

    /// <summary>The categorical width of each action head, in wire order.</summary>
    public ReadOnlySpan<int> HeadShape => _headShape;

    /// <summary>Ticks the trainer held each decision for.</summary>
    /// <remarks>
    /// <b>Carried by the checkpoint because a policy trained at one repeat is a
    /// different policy at another.</b> It decides how often the controller
    /// re-runs the network, and a bot that ran it every tick would be acting on a
    /// cadence the policy never saw.
    /// </remarks>
    public int ActionRepeat { get; }

    /// <summary>
    /// The simulation stamp this checkpoint was TRAINED against.
    /// </summary>
    /// <remarks>
    /// Not necessarily this build's — see the <c>ignoreSimulationStamp</c>
    /// argument to <see cref="Load(string, bool)"/>. Compare it against
    /// <c>SimulationStamp.Uuid</c> before trusting anything a policy decides.
    /// </remarks>
    public string SimulationUuid { get; }

    /// <summary>
    /// Loads a checkpoint and refuses it unless it belongs to this build.
    /// </summary>
    /// <remarks>
    /// <b>Every check is a refusal, never a warning.</b> The <c>PROTOCOL_UUID</c>
    /// idiom this project already uses: a policy running against a different
    /// observation layout, action encoding or simulation is not slightly wrong, it
    /// is acting on numbers that do not mean what it learned they meant, and it
    /// should find out in the first millisecond.
    /// <para>
    /// <b>These checks are also what makes <see cref="NpyFile.ReadStrings"/>'s
    /// narrow pickle scan safe.</b> A scan that locked on to the wrong bytes
    /// produces metadata that fails them.
    /// </para>
    /// </remarks>
    public static PolicyNetwork Load(Stream npz, bool ignoreSimulationStamp = false)
    {
        ArgumentNullException.ThrowIfNull(npz);

        var entries = NpyFile.ReadArchive(npz);

        if (!entries.TryGetValue("meta", out var metaBytes))
            throw new InvalidDataException("the checkpoint has no metadata; it cannot be checked and is refused");

        var meta = NpyFile.ReadStrings(metaBytes);

        // ⛔ `< 7`, NOT `!= 7`, and another repository now depends on that.
        // ⚠️ Do not tighten this to an equality check. `gg2-rl` writes an eighth
        // field — the class set a checkpoint was trained on — from 2026-09-06, and
        // has migrated its 256 existing checkpoints to carry it. This build ignores
        // the field (everything below reads by index), which is exactly what lets
        // that land before this side reads it. An equality check here would refuse
        // every checkpoint they now produce, and the refusal would look like a
        // corrupt file rather than a version skew.
        // ⚠️ The gate for this is NOT here yet: `meta` is a pickled object array,
        // so a synthetic eight-field checkpoint would test a pickle this session
        // hand-rolled rather than one the trainer writes. It belongs with the
        // change that reads field 7, using one of their real migrated files.
        // docs/ENV.md; the ask is in gg2-rl's docs/CONTRACT.md, "AN ASK RAISED
        // 2026-09-06".
        if (meta.Length < 7)
            throw new InvalidDataException($"the metadata has {meta.Length} fields, expected at least 7");

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

        // ⚠️ Optional, and the fallback is what lets the trainer ship the field
        // before this side read it. Absent is every checkpoint before 2026-09-06;
        // empty is the trainer saying "no set recorded" for one it migrated
        // without knowing. Neither is an error and both mean the old assumption.
        var trainedClasses = meta.Length > 7 && meta[7].Length > 0
            ? ParseTrainedClasses(meta[7])
            : DefaultTrainedClasses.ToArray();

        if (!string.Equals(simulationUuid, SimulationStamp.Uuid, StringComparison.Ordinal))
        {
            // THE OVERRIDE DOES NOT MAKE THE POLICY VALID, it makes the refusal the
            // caller's decision. The stamp says this checkpoint was trained against
            // a different simulation; whether that difference can REACH the maps
            // being played is a question this type cannot answer and the caller
            // sometimes can. See Gg2.Host's --policy-ignore-stamp, which exists for
            // exactly one case: a simulation change confined to a feature no shipped
            // map contains.
            //
            // It stays an EXCEPTION by default and an explicit argument to bypass,
            // because a flag that defaults to lenient is a guard nobody has.
            if (!ignoreSimulationStamp)
            {
                throw new InvalidOperationException(
                    $"this policy was trained against simulation {simulationUuid}, and this build is {SimulationStamp.Uuid}");
            }
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
            [.. weights], [.. biases], [.. widths], observationLength, headShape, actionRepeat, simulationUuid,
            trainedClasses);
    }

    /// <summary>Reads <c>meta[7]</c>: comma-separated <see cref="PlayerClass"/> names.</summary>
    /// <remarks>
    /// ⚠️ <b>An unparseable name is refused, and refused as METADATA.</b> A
    /// checkpoint naming a class this build does not have is a corrupt or
    /// mistaken field rather than a version skew — the nine classes are fixed by
    /// the protocol and do not grow — so it belongs with the other
    /// <see cref="InvalidDataException"/>s and not with the stamp's
    /// <see cref="InvalidOperationException"/>s.
    /// </remarks>
    private static PlayerClass[] ParseTrainedClasses(string field)
    {
        var names = field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var classes = new PlayerClass[names.Length];

        for (var i = 0; i < names.Length; i++)
        {
            if (!Enum.TryParse(names[i], ignoreCase: true, out PlayerClass parsed))
            {
                throw new InvalidDataException(
                    $"the checkpoint says it was trained on '{names[i]}', which is not a class this build has");
            }

            classes[i] = parsed;
        }

        return classes;
    }

    /// <summary>Loads a checkpoint from a path.</summary>
    public static PolicyNetwork Load(string path, bool ignoreSimulationStamp = false)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var file = File.OpenRead(path);

        return Load(file, ignoreSimulationStamp);
    }

    /// <summary>The raw output block, for a test that compares against the trainer.</summary>
    /// <remarks>
    /// <b>Argmax per head, never over the product.</b> The heads are independent
    /// categoricals — that is what the factorisation means — and a single argmax
    /// over 27 logits would pick one head's index and leave the rest at zero,
    /// which is a legal-looking action nobody chose.
    /// <para>
    /// <b>No allocation.</b> This runs per bot per decision inside a match, and
    /// the scratch buffers are the instance's.
    /// </para>
    /// <para>
    /// Exposed so the cross-language gate can compare LOGITS rather than only the
    /// argmax. Two implementations can agree on every head and still differ, and
    /// the difference is what says whether the agreement is robust or lucky.
    /// </para>
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

            // ReLU on hidden layers only; the output block is logits.
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
