using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

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
/// <c>docs/BOTS.md</c>. The only checkpoint that exists is stamp-invalid for this
/// build and is a test fixture, so this class ships with nothing to run: it is
/// here for the next checkpoint, and <c>PolicyNetwork.Load</c>'s stamp refusal is
/// what stops an old one being run by accident.
/// </para>
/// </remarks>
public sealed class PolicyController : IMicroController
{
    private readonly PolicyNetwork _policy;
    private readonly ActionDecoder _decoder = new();
    private readonly float[] _observation;
    private readonly int[] _heads;

    private EnvAction _action = EnvAction.Idle;
    private int _ticksLeftInDecision;
    private int _slot = -1;

    public PolicyController(PolicyNetwork policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _policy = policy;
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

    /// <summary>
    /// The classes the policy was trained on, and therefore the only ones it may
    /// drive.
    /// </summary>
    /// <remarks>
    /// <b>Five of the nine class columns are constant zero for every decision of
    /// every training run</b>, self block and enemy block, so their first-layer
    /// weights are still the random draw. Driving a sniper, medic, engineer, spy
    /// or pyro does not tell the policy which class it is; it injects untrained
    /// noise.
    /// <para>
    /// <b>The trained set is not a prefix.</b> Demoman is <c>PlayerClass</c>
    /// index 3 and Heavy is 6, so the untrained columns are 2, 4, 5, 7 and 8 —
    /// interleaved. Anything that assumes "the first four" is wrong.
    /// </para>
    /// <para>
    /// <b>Sniper was DROPPED from the set on 2026-09-03</b> and demoman added, at
    /// the <c>gg2-rl</c> lane's request. The mirror evidence is why: the policy's
    /// sniper already beat the charge-seeing scripted sniper 65.5% at the
    /// <c>(0, 300)</c> band, so the charge column that
    /// <see cref="ObservationWriter"/> still declines to encode was never what
    /// held that class back. ⚠️ <b>A checkpoint trained before this date learned
    /// a sniper and did not learn a demoman</b>, which no stamp can express —
    /// the class set is not on the stamp surface.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<PlayerClass> TrainedClasses =>
        [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman, PlayerClass.Heavy];

    /// <summary>Whether this class is one the policy was trained on.</summary>
    public static bool IsTrained(PlayerClass playerClass)
    {
        foreach (var trained in TrainedClasses)
        {
            if (trained == playerClass)
                return true;
        }

        return false;
    }

    public bool Drives(PlayerClass playerClass) => IsTrained(playerClass);

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
