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
/// <para>
/// <b>A demo net</b> (<see cref="SimPolicyNetwork.IsDemo"/>) is a team net whose
/// fifth head is Special, the detonate: a key pressed with the decision's other
/// keys and held for its <see cref="ActionRepeat"/> ticks, with no edge logic —
/// gg2-sim's <c>PolicyDriver.TeamInput</c>. <b>A pyro net</b>
/// (<see cref="SimPolicyNetwork.IsPyro"/>) uses the fifth head the same way:
/// Special is the airblast, and with Attack also held, the flare. <b>A medic
/// net</b> (<see cref="SimPolicyNetwork.IsMedic"/>) too (needles, or the uber
/// when ready with Attack held), and its relative aim groups 1 and 2 aim at the
/// team-mates its last observation listed, with the aim distance on them so the
/// beam's mouse-point preference picks that patient. <b>A spy net</b>
/// (<see cref="SimPolicyNetwork.IsSpy"/>) presses Special too: the cloak toggle.
/// <b>An engineer net</b> (<see cref="SimPolicyNetwork.IsEngineer"/>) sends its
/// fifth head as the build command on every tick of the decision
/// (<see cref="WantsBuild"/>, asked before the keys like the scope).
/// </para>
/// <para>
/// <b>A base layout v2 net</b> (<see cref="SimPolicyNetwork.IsV2"/>) keeps one
/// <see cref="SimPolicyMemory"/> for its body: learned at each observation, and on
/// every third tick while the scripted layer drives (<see cref="Sight"/>, gg2-sim's
/// <c>SimPolicyController.Sight</c>). A listed enemy the body cannot see now is
/// aimed at where the observation listed it, and the fallback when a listed one has
/// died is the nearest enemy it can see.
/// </para>
/// <para>
/// <b>A terrain net</b> (<see cref="SimPolicyNetwork.IsTerrain"/>, the duel nets)
/// routes its observation on the graph and next-hop tables its body's scripted
/// layer routes with, handed over every tick (<see cref="SetRoute"/>).
/// </para>
/// <para>
/// <b>Sampled</b> (<see cref="Sampled"/>), each decision draws every head from its
/// softmax with the controller's own <see cref="Random"/>, seeded from the first
/// roster slot it decides for, as gg2-sim's <c>PolicyDriver(net, new Random(seed))</c>
/// does. Greedy is the default and the cross-checked arm: on a static scene its
/// argmax is a fixed point, and gg2-sim's duel nets stop firing at an idle target
/// in about 6% of cells greedy against under 2% sampled (gg2-sim
/// <c>docs/HANDOFF-SERVER-DUEL.md</c>).
/// </para>
/// </remarks>
public sealed class SimPolicyController
    : IMicroController, IScopeController, IBeamController, IBuildController, ISightingController, IRouteController
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

    // The team-mates the last team observation listed: a medic net's ally aim.
    private readonly int[] _listedAllies = [-1, -1];

    // A v2 net's sightings (null on v1), and the roster slot they were learned in.
    private readonly SimPolicyMemory? _memory;
    private int _memorySlot = -1;

    private int _ticksLeft;
    private int _slot = -1;
    private int _op = -1;

    // A decision WantsZoomToggle made this tick, before the keys, for Poll to use.
    private bool _decidedEarly;

    // A terrain net's route source: the body's scripted layer's, every tick.
    private NavGraph? _graph;
    private NavDistanceSet? _tables;

    // A sampled controller's stream (null until its first decision, or greedy).
    private Random? _sample;

    /// <param name="network">The weights, shared read-only by every bot playing them.</param>
    /// <param name="drives">
    /// The classes this tier drives. A <c>.gg2p</c> names none, so this is always
    /// configured; an empty array drives nothing.
    /// </param>
    /// <param name="label">What to call it after <c>gg2-sim:</c>, or null for the bare name.</param>
    /// <param name="sampled">Draw each head from its softmax rather than take the argmax.</param>
    public SimPolicyController(SimPolicyNetwork network, PlayerClass[] drives, string? label = null, bool sampled = false)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(drives);

        _network = network;
        _drives = drives;
        _observation = new float[network.InputSize];
        _scratchA = new float[network.Widest];
        _scratchB = new float[network.Widest];
        _action = new int[network.Heads.Length];
        _memory = network.IsV2 ? new SimPolicyMemory() : null;
        Sampled = sampled;
        Name = label is null ? "gg2-sim" : "gg2-sim:" + label;
    }

    /// <summary>Whether each decision is drawn from the heads' softmax (gg2-sim's <c>sampled:</c> driver).</summary>
    public bool Sampled { get; }

    public void SetRoute(NavGraph? graph, NavDistanceSet? tables)
    {
        _graph = graph;
        _tables = tables;
    }

    /// <summary>The graph the last <see cref="SetRoute"/> handed over, for a test of the wiring.</summary>
    internal NavGraph? RouteGraph => _graph;

    public string Name { get; }

    public SimPolicyNetwork Network => _network;

    /// <summary>
    /// Observation plus forward pass, Release, one core of an i7-13700K
    /// (2026-10-27): 72 µs for the 253-256-256-106 team net, 57 µs for the
    /// 169-256-256-56 duel net, scalar by design (see <see cref="SimPolicyNetwork"/>)
    /// — ten times a gg2-rl decision. The larger is used for both. A terrain net
    /// (2026-11-17, same machine, sampled): 102-107 µs for the 439-256-256-106
    /// duel soldier net against 71 µs for the 263-wide v2 soldier net, the blocks
    /// and the wider first layer. Re-measure for a wider net.
    /// </summary>
    public int DecisionCostFor(int liveCharacters) => _network.IsTerrain ? 110 : 75;

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

    /// <summary>A medic net heals and picks its patient itself, so it may take a beaming medic.</summary>
    public bool OwnsBeam => _network.IsMedic;

    public bool WantsZoomToggle(in WorldView world, int slot, int tick)
    {
        if (!_network.IsZoom || !world.IsAlive(slot))
            return false;

        DecideEarly(in world, slot, tick);
        ref readonly var self = ref world.Character(slot);
        return self.Class == PlayerClass.Sniper && (_action[4] != 0) != self.Zoomed;
    }

    public bool OwnsBuild => _network.IsEngineer;

    /// <summary>
    /// gg2-sim's <c>SimPolicyController.WantsBuild</c>: the held decision's build
    /// head, on every tick it holds, deciding early at a boundary.
    /// </summary>
    public bool WantsBuild(in WorldView world, int slot, int tick)
    {
        if (!_network.IsEngineer || !world.IsAlive(slot))
            return false;

        DecideEarly(in world, slot, tick);
        return world.Character(slot).Class == PlayerClass.Engineer && _action[4] != 0;
    }

    /// <summary>
    /// gg2-sim's <c>SimPolicyController.Sight</c>: a v2 net's sightings on the
    /// environment's step cadence, every <see cref="ActionRepeat"/> ticks, so the
    /// memory a handover inherits is the one gg2-sim's environment would hold.
    /// </summary>
    public void Sight(in WorldView world, int slot, int tick)
    {
        if (_memory is null || tick % ActionRepeat != 0)
            return;

        SimPolicyObservation.Sight(in world, slot, Memory(slot), tick);
    }

    private void DecideEarly(in WorldView world, int slot, int tick)
    {
        if (AtDecisionBoundary(slot) && !_decidedEarly)
        {
            Begin(in world, slot, tick);
            _decidedEarly = true;
        }
    }

    // Not in gg2-sim's ref, where a body never changes slot: sightings are by
    // roster slot, and a body renumbered by a departure starts over.
    private SimPolicyMemory Memory(int slot)
    {
        if (slot != _memorySlot)
        {
            _memory!.Clear();
            _memorySlot = slot;
        }

        return _memory!;
    }

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        var first = _decidedEarly || AtDecisionBoundary(slot);
        DecidedThisPoll = first;

        if (first && !_decidedEarly)
            Begin(in world, slot, tick);

        _decidedEarly = false;
        _ticksLeft--;

        // A duel net's opponent that died mid-decision: hold still rather than aim
        // at a corpse. A team net re-aims at the nearest live enemy itself.
        if (_op < 0 || (!_network.IsTeam && !world.IsAlive(_op)))
            return new PlayerInput(PlayerKeys.None, world.Character(slot).NetAimDirection, 128);

        return _network.IsTeam
            ? TeamInput(in world, _action, slot, _listed, _network.FifthIsSpecial, _network.IsMedic ? _listedAllies : null, _memory)
            : DuelInput(in world, _action, slot, _op);
    }

    private void Begin(in WorldView world, int slot, int tick)
    {
        _slot = slot;
        _ticksLeft = ActionRepeat;
        _op = NearestEnemy(in world, slot);

        if (_op < 0)
            Array.Clear(_action);
        else
            Decide(in world, slot, _op, tick);
    }

    private void Decide(in WorldView world, int me, int op, int tick)
    {
        if (_network.IsTeam)
            SimPolicyObservation.Team(
                in world, me, _observation, _listed, _network.IsZoom, _network.IsDemo, _network.IsPyro, _network.IsMedic, _listedAllies,
                _network.IsEngineer, _network.IsSpy, _memory is null ? null : Memory(me), tick, _network.IsTerrain, _graph, _tables);
        else
            SimPolicyObservation.Duel(in world, me, op, _observation, _network.InputSize);

        // Seeded once, by the first slot decided for, so a headless match is
        // reproducible and two bots on one server do not share a stream.
        if (Sampled)
            _sample ??= new Random(unchecked((me * 104729) ^ 0x5A3C7E1));

        _network.Decide(_observation, _scratchA, _scratchB, _action, _sample);
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

    // gg2-sim's PolicyDriver.TeamInput. In the demo, pyro, medic and spy layouts
    // the fifth head is Special. With listedAllies (the medic layout) groups 1 and
    // 2 aim at the listed team-mates 0 and 1, or the nearest live one, with the
    // aim distance on them, truncated and capped at 255. With a v2 memory a listed
    // enemy the body cannot see now is aimed at where it was listed, and the
    // fallback is the nearest enemy it can see.
    internal static PlayerInput TeamInput(
        in WorldView world, int[] a, int meSlot, int[] listed, bool special, int[]? listedAllies, SimPolicyMemory? memory)
    {
        var keys = Keys(a);

        if (special && a[4] != 0)
            keys |= PlayerKeys.Special;
        var bin = Math.Clamp(a[3], 0, TeamAimActions - 1);

        if (bin < AbsoluteAimBins)
            return new PlayerInput(keys, (ushort)(bin * 65536 / AbsoluteAimBins), 128);

        int group = (bin - AbsoluteAimBins) / RelativeAimBins, offset = (bin - AbsoluteAimBins) % RelativeAimBins;
        var ally = listedAllies is not null && group > 0;
        var target = ally ? listedAllies![group - 1] : listed[group];

        var isListed = target >= 0 && world.IsAlive(target);

        if (!isListed)
        {
            Span<int> one = stackalloc int[1];
            target = SimPolicyObservation.Nearest(in world, meSlot, mates: ally, one, seen: memory is not null && !ally) > 0 ? one[0] : -1;
        }

        if (target < 0)
            return new PlayerInput(keys, 0, 128);

        ref readonly var me = ref world.Character(meSlot);
        ref readonly var op = ref world.Character(target);
        double tx = op.X, ty = op.Y;

        if (memory is not null && !ally && isListed && !BotVisibility.CanSee(in me, in op))
        {
            tx = memory.ListedX[group];
            ty = memory.ListedY[group];
        }

        var deg = Gml.PointDirection(me.X, me.Y, tx, ty) + ((offset - (RelativeAimBins / 2)) * RelativeStep);
        deg = ((deg % 360) + 360) % 360;
        var distance = ally ? (byte)(int)Math.Min(255.0, Gml.PointDistance(me.X, me.Y, op.X, op.Y)) : (byte)128;
        return new PlayerInput(keys, (ushort)((uint)(deg * 65536.0 / 360.0) & 0xFFFF), distance);
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
/// <param name="Sampled">Each bot's decisions drawn from the heads' softmax; see <see cref="SimPolicyController"/>.</param>
public sealed record SimPolicyMicroSpec(SimPolicyNetwork Network, PlayerClass[] Drives, string? Label = null, bool Sampled = false)
    : MicroControllerSpec
{
    public override string Name => Label is null ? "gg2-sim" : "gg2-sim:" + Label;

    public override IMicroController Create() => new SimPolicyController(Network, Drives, Label, Sampled);
}
