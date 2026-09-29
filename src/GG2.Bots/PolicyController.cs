using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// A trained <see cref="PolicyNetwork"/> as a micro-controller: observation in,
/// action heads out, decoded to keys and aim.
/// </summary>
/// <remarks>
/// <b>Behaviour-identical to <c>PolicyBot</c>.</b> That class is the anchor every
/// controller comparison is made against, so this split must not move it by a
/// hair. <c>PolicyMatchTests</c>, <c>ScenarioSuiteTests</c> and the pinned
/// baselines are what say it does not.
/// <para>
/// <b>A policy is DATED BY THE PHYSICS it was trained against</b> — see
/// <c>docs/BOTS.md</c>. Two checkpoints for this build ship in
/// <c>checkpoints/</c> as of 2026-09-07 and <c>PolicyNetwork.Load</c>'s stamp
/// refusal is what stops a retired one being run by accident. ⚠️ This paragraph
/// said the opposite — "nothing to run" — for a day after that stopped being
/// true.
/// </para>
/// </remarks>
public sealed class PolicyController : IMicroController
{
    private readonly PolicyNetwork _policy;
    private readonly PlayerClass[]? _drives;
    private readonly ActionDecoder _decoder = new();
    private readonly float[] _observation;
    private readonly int[] _heads;

    private EnvAction _action = EnvAction.Idle;
    private int _ticksLeftInDecision;
    private int _slot = -1;

    /// <summary>The checkpoint driving the classes it was trained on.</summary>
    public PolicyController(PolicyNetwork policy)
        : this(policy, drives: null)
    {
    }

    /// <summary>The checkpoint driving an explicitly configured set of classes.</summary>
    /// <param name="policy">The loaded weights.</param>
    /// <param name="drives">
    /// Which classes this controller answers <see cref="Drives"/> for, or null to
    /// ask the checkpoint. ⛔ <b>An empty array is not null</b>: it means this
    /// controller drives NOTHING, which is what an operator asking for every one
    /// of its classes to stay scripted has asked for, and it must not silently
    /// fall back to the file's own set.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>The set became configurable on 2026-09-07, and the two sources answer
    /// different questions.</b> The checkpoint's field says what the training run
    /// covered — narrowed, since the ship bundle, to what its author intends it to
    /// drive. This parameter is the OPERATOR's answer to the second question, and
    /// it is the only one that can name a class the file does not: a should-drive
    /// set narrowed on the producing side destroys the information that the run
    /// also trained the classes it dropped, and nothing but a person who knows
    /// that can put one back. <c>BotBrain.Build</c> warns when it happens.
    /// </remarks>
    public PolicyController(PolicyNetwork policy, PlayerClass[]? drives)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _policy = policy;

        // Copied, not aliased: a spec is shared by every bot on the server and a
        // caller that later mutated its array would change what a controller
        // already built drives, at an arbitrary tick.
        _drives = drives is null ? null : [.. drives];

        _observation = new float[policy.ObservationLength];
        _heads = new int[ActionDecoder.HeadShape.Length];
    }

    /// <summary>The network this drives. For a diagnostic and for a test.</summary>
    public PolicyNetwork Network => _policy;

    public string Name => "policy";

    /// <summary>
    /// <b>7 µs</b> — 2.2 extracting the observation plus 4.7 in the forward pass.
    /// </summary>
    /// <remarks>
    /// Release, <c>BENCH_POLICYCOST</c>, on the 209→64→64→27 checkpoint: 4,733 ns
    /// a forward pass over a million calls, 2,165 ns an observation. Both allocate
    /// 40 bytes total, not per call. <b>Re-measure if the network's shape
    /// changes</b> — this is linear in it.
    /// </remarks>
    /// <remarks>
    /// <b>Flat in the population, unlike the search.</b> A forward pass is a
    /// fixed-size network over a fixed-length observation; nothing in it walks the
    /// roster. The argument is ignored deliberately rather than by omission.
    /// </remarks>
    public int DecisionCostFor(int liveCharacters) => 7;

    public bool DecidedThisPoll { get; private set; }

    /// <summary>Whether this class is one THIS policy was trained on.</summary>
    /// <remarks>
    /// ⛔ <b>Asks the network, not a constant, and that is the whole point of the
    /// change that landed here on 2026-09-06.</b> The class set is a property of
    /// the checkpoint: the training lane ships three families that train three
    /// different sets, and under the compile-time static a <c>control</c>-family
    /// file would have driven its untrained demoman and refused its trained
    /// sniper, silently. <see cref="PolicyNetwork.TrainedClasses"/> has the
    /// measurement and the reasoning.
    /// <para>
    /// ⚠️ <b>Two controllers over two checkpoints now legitimately disagree</b>,
    /// which is what <see cref="HybridBot"/>'s per-class tier eligibility was
    /// built for and could not exercise while this read a static.
    /// </para>
    /// <para>
    /// ⚠️⚠️ <b>The checkpoint's field equates "trained on" with "should drive",
    /// and the training lane has a measured case where they differ</b> — see
    /// <see cref="PolicyNetwork.TrainedClasses"/>. A class can be trained and
    /// still mirror indistinguishably from the scripted bot it would replace, in
    /// which case driving it buys nothing and costs the policy's decision budget.
    /// </para>
    /// <para>
    /// ✅ <b>Since 2026-09-07 an operator can answer that second question here</b>,
    /// by configuring the set through the constructor rather than accepting the
    /// file's — <c>[Bots] ClassPolicy</c> and <c>--bot-class-policy</c> are what
    /// reach it. When nothing is configured this still asks the checkpoint, which
    /// is the right default: the file's set is its author's own statement of what
    /// it should drive.
    /// </para>
    /// </remarks>
    public bool Drives(PlayerClass playerClass)
    {
        if (_drives is null)
            return _policy.IsTrained(playerClass);

        foreach (var driven in _drives)
        {
            if (driven == playerClass)
                return true;
        }

        return false;
    }


    public bool AtDecisionBoundary(int slot) => slot != _slot || _ticksLeftInDecision <= 0;

    /// <summary>
    /// Resynchronises the decoder's absolute aim to where the character is
    /// actually pointing.
    /// </summary>
    /// <remarks>
    /// The scripted layer writes the bearing directly, so a decoder carrying
    /// the bearing from the last engagement would turn from somewhere nobody is
    /// pointing. <c>RolloutSearch.ResyncAim</c> documents the same trap.
    /// </remarks>
    public void OnHandoverIn(in WorldView world, int slot) =>
        _decoder.SetAim(slot, world.Character(slot).NetAimDirection * 360.0 / 65536.0);

    /// <summary>
    /// Abandons a part-finished decision, so a re-entry starts a fresh one.
    /// </summary>
    /// <remarks>
    /// <b>Different controllers own this cadence differently</b> — one scripted
    /// arm zeroes it on every tick, another zeroes nothing — which is why
    /// <see cref="IMicroController.OnHandoverOut"/> exists at all rather than the
    /// gate resetting a cadence it does not own. See that member's remarks.
    /// </remarks>
    public void OnHandoverOut() => _ticksLeftInDecision = 0;

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        var firstTick = AtDecisionBoundary(slot);

        DecidedThisPoll = firstTick;

        if (firstTick)
        {
            _slot = slot;

            // The network runs once per ActionRepeat ticks, which is the cadence
            // it was trained at and is carried by the checkpoint. Running it every
            // tick would be a policy acting on a rhythm it never saw.
            _ticksLeftInDecision = Math.Max(1, _policy.ActionRepeat);

            Decide(in world, slot);
        }

        _ticksLeftInDecision--;

        return _decoder.Decode(slot, _action, firstTick);
    }

    private void Decide(in WorldView world, int slot)
    {
        ObservationWriter.Write(in world, slot, _observation);
        _policy.Act(_observation, _heads);

        _action = new EnvAction
        {
            Horizontal = (byte)_heads[0],
            Jump = (byte)_heads[1],
            Down = (byte)_heads[2],
            Fire = (byte)_heads[3],
            AimDelta = (byte)_heads[4],
            Zoom = (byte)_heads[5],
        };
    }
}
