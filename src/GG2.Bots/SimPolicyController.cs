using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// A <c>gg2-sim</c> policy (<see cref="SimPolicyNetwork"/>, a <c>.gg2p</c>) as a
/// <see cref="HybridBot"/> tier: the scripted layer keeps routing and objectives,
/// the policy takes keys and aim inside the engagement window.
/// </summary>
/// <remarks>
/// ⛔ <b>Behaviour-identical to gg2-sim's <c>ref/Matches.cs</c>
/// <c>SimPolicyController</c> with its defaults</b> (greedy, nearest-enemy
/// target), so a headless match through this tier reproduces gg2-sim's
/// <c>ref matches</c> on the same seed. The decision cadence, the hold when
/// there is no enemy, both aim decodes and the two nearest-enemy searches —
/// <see cref="NearestEnemy"/> by <see cref="Gml.PointDistance"/> over the
/// roster's teams, <see cref="SimPolicyObservation.Nearest"/> by squared
/// distance over the characters' — are that file's and <c>PolicyDriver.cs</c>'s,
/// and they differ from each other on purpose. Its statistics hooks
/// (<c>Engagements</c>) only read the world and are left out.
/// <para>
/// <b>A team net</b> lists its own enemies; aim bins past
/// <see cref="AbsoluteAimBins"/> are groups of <see cref="RelativeAimBins"/> at
/// the k-th listed enemy, or the nearest live one if that one has died. <b>A duel
/// net</b> observes the nearest live enemy at each decision, aims relative to it,
/// and holds still for the rest of the decision if it dies.
/// </para>
/// <para>
/// <b>A zoom net</b> (<see cref="SimPolicyNetwork.IsZoom"/>) is a team net whose
/// fifth head is the scope as a LEVEL. <see cref="WantsZoomToggle"/> is asked
/// before the tick's keys, decides early at a boundary, and <see cref="Poll"/>
/// reuses that decision — gg2-sim's <c>SimPolicyController.WantsZoomToggle</c>.
/// </para>
/// </remarks>
public sealed class SimPolicyController : IMicroController, IScopeController
{
    /// <summary>Ticks a decision is held for: the cadence every gg2-sim policy trained at.</summary>
    public const int ActionRepeat = 3;

    public const int AbsoluteAimBins = 24;

    public const int RelativeAimBins = 25;

    public const int TeamAimActions = AbsoluteAimBins + (SimPolicyObservation.ListedEnemies * RelativeAimBins);

    public const int DuelAimActions = AbsoluteAimBins + RelativeAimBins;

    private const double RelativeStep = 2.0;

    private readonly SimPolicyNetwork _network;
    private readonly PlayerClass[] _drives;
    private readonly float[] _observation;
    private readonly float[] _scratchA;
    private readonly float[] _scratchB;
    private readonly int[] _action;
    private readonly int[] _listed = [-1, -1, -1];

    private int _ticksLeft;
    private int _slot = -1;
    private int _op = -1;

    // A decision WantsZoomToggle made this tick, before the keys, for Poll to use.
    private bool _decidedEarly;

    /// <param name="network">The weights, shared read-only by every bot playing them.</param>
    /// <param name="drives">
    /// The classes this tier drives. A <c>.gg2p</c> names none, so this is always
    /// configured; an empty array drives nothing.
    /// </param>
    /// <param name="label">What to call it after <c>gg2-sim:</c>, or null for the bare name.</param>
    public SimPolicyController(SimPolicyNetwork network, PlayerClass[] drives, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(drives);

        _network = network;
        _drives = drives;
        _observation = new float[network.InputSize];
        _scratchA = new float[network.Widest];
        _scratchB = new float[network.Widest];
        _action = new int[network.Heads.Length];
        Name = label is null ? "gg2-sim" : "gg2-sim:" + label;
    }

    public string Name { get; }

    public SimPolicyNetwork Network => _network;

    /// <summary>
    /// Observation plus forward pass, Release, one core of an i7-13700K
    /// (2026-10-27): 72 µs for the 253-256-256-106 team net, 57 µs for the
    /// 169-256-256-56 duel net, scalar by design (see <see cref="SimPolicyNetwork"/>)
    /// — ten times a gg2-rl decision. The larger is used for both. Re-measure for
    /// a wider net.
    /// </summary>
    public int DecisionCostFor(int liveCharacters) => 75;

    public bool DecidedThisPoll { get; private set; }

    public bool Drives(PlayerClass playerClass) => Array.IndexOf(_drives, playerClass) >= 0;

    public bool AtDecisionBoundary(int slot) => slot != _slot || _ticksLeft <= 0;

    // The aim is decided from scratch at every decision and never carried, so
    // there is nothing to resynchronise.
    public void OnHandoverIn(in WorldView world, int slot)
    {
    }

    // ⚠️ Clears an early decision too, which gg2-sim's ref does not: there the
    // controller is never released between WantsZoomToggle and Poll, and here
    // the hybrid can release it in that same tick's Poll. Kept, it would hand a
    // stale decision to the next engagement's first poll.
    public void OnHandoverOut()
    {
        _ticksLeft = 0;
        _decidedEarly = false;
    }

    public bool OwnsScope => _network.IsZoom;

    public bool WantsZoomToggle(in WorldView world, int slot, int tick)
    {
        if (!_network.IsZoom || !world.IsAlive(slot))
            return false;

        if (AtDecisionBoundary(slot) && !_decidedEarly)
        {
            Begin(in world, slot);
            _decidedEarly = true;
        }

        ref readonly var self = ref world.Character(slot);
        return self.Class == PlayerClass.Sniper && (_action[4] != 0) != self.Zoomed;
    }

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        var first = _decidedEarly || AtDecisionBoundary(slot);
        DecidedThisPoll = first;

        if (first && !_decidedEarly)
            Begin(in world, slot);

        _decidedEarly = false;
        _ticksLeft--;

        // A duel net's opponent that died mid-decision: hold still rather than aim
        // at a corpse. A team net re-aims at the nearest live enemy itself.
        if (_op < 0 || (!_network.IsTeam && !world.IsAlive(_op)))
            return new PlayerInput(PlayerKeys.None, world.Character(slot).NetAimDirection, 128);

        return _network.IsTeam
            ? TeamInput(in world, _action, slot, _listed)
            : DuelInput(in world, _action, slot, _op);
    }

    private void Begin(in WorldView world, int slot)
    {
        _slot = slot;
        _ticksLeft = ActionRepeat;
        _op = NearestEnemy(in world, slot);

        if (_op < 0)
            Array.Clear(_action);
        else
            Decide(in world, slot, _op);
    }

    private void Decide(in WorldView world, int me, int op)
    {
        if (_network.IsTeam)
            SimPolicyObservation.Team(in world, me, _observation, _listed, _network.IsZoom);
        else
            SimPolicyObservation.Duel(in world, me, op, _observation, _network.InputSize);

        _network.Decide(_observation, _scratchA, _scratchB, _action);
    }

    private static PlayerKeys Keys(int[] a)
    {
        var keys = PlayerKeys.None;

        if (a[0] == 1)
            keys |= PlayerKeys.Left;
        else if (a[0] == 2)
            keys |= PlayerKeys.Right;

        if (a[1] != 0)
            keys |= PlayerKeys.Jump;

        if (a[2] != 0)
            keys |= PlayerKeys.Attack;

        return keys;
    }

    // gg2-sim's PolicyDriver.Input, recomputed every tick as there.
    private static PlayerInput DuelInput(in WorldView world, int[] a, int meSlot, int opSlot)
    {
        var keys = Keys(a);

        if (a[3] < AbsoluteAimBins)
            return new PlayerInput(keys, (ushort)(a[3] * 65536 / AbsoluteAimBins), 128);

        ref readonly var me = ref world.Character(meSlot);
        ref readonly var op = ref world.Character(opSlot);
        var deg = Gml.PointDirection(me.X, me.Y, op.X, op.Y) + ((a[3] - AbsoluteAimBins - (RelativeAimBins / 2)) * RelativeStep);
        deg = ((deg % 360) + 360) % 360;
        return new PlayerInput(keys, (ushort)((uint)(deg * 65536.0 / 360.0) & 0xFFFF), 128);
    }

    // gg2-sim's PolicyDriver.TeamInput.
    private static PlayerInput TeamInput(in WorldView world, int[] a, int meSlot, int[] listed)
    {
        var keys = Keys(a);
        var bin = Math.Clamp(a[3], 0, TeamAimActions - 1);

        if (bin < AbsoluteAimBins)
            return new PlayerInput(keys, (ushort)(bin * 65536 / AbsoluteAimBins), 128);

        int group = (bin - AbsoluteAimBins) / RelativeAimBins, offset = (bin - AbsoluteAimBins) % RelativeAimBins;
        var target = listed[group];

        if (target < 0 || !world.IsAlive(target))
        {
            Span<int> one = stackalloc int[1];
            target = SimPolicyObservation.Nearest(in world, meSlot, mates: false, one) > 0 ? one[0] : -1;
        }

        if (target < 0)
            return new PlayerInput(keys, 0, 128);

        ref readonly var me = ref world.Character(meSlot);
        ref readonly var op = ref world.Character(target);
        var deg = Gml.PointDirection(me.X, me.Y, op.X, op.Y) + ((offset - (RelativeAimBins / 2)) * RelativeStep);
        deg = ((deg % 360) + 360) % 360;
        return new PlayerInput(keys, (ushort)((uint)(deg * 65536.0 / 360.0) & 0xFFFF), 128);
    }

    // gg2-sim's SimPolicyController.NearestEnemy: the roster's teams and
    // PointDistance, where the observation's Nearest uses the characters' teams
    // and squared distance. Kept apart rather than unified — see the remarks.
    private static int NearestEnemy(in WorldView world, int slot)
    {
        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (other == slot || !world.IsAlive(other) || world.Player(other).Team == team)
                continue;

            // Not in gg2-sim's ref: a no-op unless the view carries an opponents
            // table, which only the duel server builds.
            if (!world.MayOppose(slot, other))
                continue;

            ref readonly var c = ref world.Character(other);
            var d = Gml.PointDistance(self.X, self.Y, c.X, c.Y);

            if (d < bestDistance)
            {
                bestDistance = d;
                best = other;
            }
        }

        return best;
    }
}

/// <summary>A gg2-sim checkpoint driving keys and aim, one controller per bot.</summary>
/// <param name="Network">The loaded weights, SHARED by every bot — see <see cref="MicroControllerSpec"/>.</param>
/// <param name="Drives">The classes it drives; a <c>.gg2p</c> names none of its own.</param>
/// <param name="Label">The checkpoint's file name, for a result table.</param>
public sealed record SimPolicyMicroSpec(SimPolicyNetwork Network, PlayerClass[] Drives, string? Label = null)
    : MicroControllerSpec
{
    public override string Name => Label is null ? "gg2-sim" : "gg2-sim:" + Label;

    public override IMicroController Create() => new SimPolicyController(Network, Drives, Label);
}
