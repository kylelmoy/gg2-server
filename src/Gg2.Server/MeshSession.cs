using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// A running server's half of a match happening in two places: the rollback
/// node, the link to the peer, and the bookkeeping between them.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Nothing here exists unless the operator asked for it.</b> A server with
/// no mesh setting never constructs one, and its tick runs
/// <c>MatchSimulation.Tick</c> exactly as it always has —
/// <c>MeshIsOptionalTests</c> is what holds that down. This type is the whole of
/// the difference between the two modes.
/// </para>
/// <para>
/// <b>It owns the world while it exists.</b> A <see cref="MeshNode"/> keeps the
/// authoritative copy so it can rewind to it, so a meshed server reads its world
/// from here rather than from its own field. That is the part a standalone
/// server must not pay for, and does not: the copy back is one struct assignment
/// per tick, and only in this mode.
/// </para>
/// </remarks>
public sealed class MeshSession : IDisposable
{
    private readonly MeshLink _link;
    private readonly List<int> _slots = [];
    private readonly List<PlayerInput> _inputs = [];

    private readonly CollisionWorld _collision;
    private readonly SpawnPoints? _spawns;
    private readonly WorldState _start;

    private MeshSession(
        MeshLink link, MeshNode node, bool listening,
        CollisionWorld collision, in WorldState start, SpawnPoints? spawns)
    {
        _link = link;
        _collision = collision;
        _spawns = spawns;
        _start = start;
        Node = node;
        Listening = listening;

        // The listener names the tick and is therefore synchronised from the
        // start; the joiner has to be told.
        Synchronised = listening;
    }

    /// <summary>The rollback node. The world lives here while a mesh is running.</summary>
    public MeshNode Node { get; private set; }

    /// <summary>
    /// Whether the two nodes agree what tick it is.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Two nodes must be on the SAME tick, and a local counter each is not
    /// agreement.</b> Measured before this existed: a server scheduled a join
    /// ten ticks ahead of its own count, its peer was already past that tick,
    /// and the command was refused as too late — so the peer never heard about a
    /// player who had already joined. The listener names its tick, the joiner
    /// adopts it by rebuilding its node at that number, and both advance one per
    /// tick from there.
    /// </remarks>
    public bool Synchronised { get; private set; }

    /// <summary>Whether this node waited for the peer rather than joining it.</summary>
    /// <remarks>
    /// <b>Which half of the roster a node owns has to be decided the same way on
    /// both machines</b>, and the only asymmetry two nodes have for free is which
    /// of them listened. The listener takes the even slots.
    /// </remarks>
    public bool Listening { get; }

    public bool Connected => _link.Connected;

    /// <summary>
    /// The port this node is waiting on, which matters when zero was asked for.
    /// </summary>
    public int Port => _link.Port;

    public MeshLinkStats LinkStats => _link.Stats;

    /// <summary>Ticks where this node and its peer disagreed about the world.</summary>
    public int Disagreements { get; private set; }

    /// <summary>Ticks where they agreed, so a zero above means something.</summary>
    public int Agreements { get; private set; }

    /// <summary>
    /// How far ahead a roster change is scheduled, in ticks.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Must exceed the link's worst one-way delay plus its jitter.</b> The
    /// peer has to have the command in hand before it reaches the tick, because
    /// a command for a tick already simulated is refused rather than applied on
    /// one machine only. Ten ticks is a third of a second — far past the two or
    /// three a transatlantic link costs, and cheap: the only thing it delays is
    /// how soon a joining player appears, which nobody can perceive against the
    /// seconds a GG2 join already takes.
    /// </remarks>
    public const int CommandLead = 10;

    /// <summary>
    /// Names the peer has told this node about, by slot.
    /// </summary>
    /// <remarks>
    /// <b>A peer's player has to be nameable here</b>, because this node tells
    /// its OWN clients who joined and a slot number is not a person. Kept beside
    /// the node rather than inside it: a <c>WorldState</c> holds no references,
    /// and a name is a string.
    /// </remarks>
    public Dictionary<int, string> PeerNames { get; } = [];

    /// <summary>
    /// The name the peer gave a slot's player, if that player is theirs.
    /// </summary>
    /// <remarks>
    /// Resolved through the assignment rather than stored against a slot: a join
    /// has no slot when it is sent, and a removal renumbers every slot above it.
    /// </remarks>
    public string NameOf(int slot)
    {
        foreach (var (key, name) in PeerNames)
        {
            if (Node.TryGetAssignedSlot(key >> 16, key & 0xFFFF, out var at) && at == slot)
                return name;
        }

        return string.Empty;
    }

    /// <summary>
    /// Roster changes the peer scheduled, in the order they were heard, for the
    /// server to mirror into its own roster.
    /// </summary>
    public Queue<(int Tick, MeshCommand Command, string Name)> FromPeer { get; } = [];

    /// <summary>This node's number: the listener is 0.</summary>
    public int Origin => Listening ? 0 : 1;

    /// <summary>
    /// Whether a roster slot is this node's to drive.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Read off who issued the join, never off the slot number.</b> A join
    /// takes the lowest free place in a dense roster, so this node's own player
    /// can land on any slot at all. Guessing from parity would have a node
    /// predicting inputs for a player it is itself driving — and never sending
    /// that player's keys to the peer, which is the same player going silent on
    /// the other side of the link.
    /// </remarks>
    public bool Owns(int slot) => Node.OriginOf(slot) == Origin;

    /// <summary>
    /// Schedules a roster change on this node and tells the peer, so both apply
    /// it at the same tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Both halves or neither.</b> Scheduling locally without sending
    /// leaves the peer in a different match; sending without scheduling leaves
    /// this node in one. There is no ordering between them that makes a
    /// half-done roster change safe, which is why they are one call.
    /// </remarks>
    public int Schedule(in MeshCommand command, ReadOnlySpan<char> name = default)
    {
        var at = Node.Tick + CommandLead;

        Node.Schedule(at, command);
        _link.SendCommand(at, command, name);

        return at;
    }

    public static MeshSession Start(
        ServerOptions options, CollisionWorld collision, in WorldState start, SpawnPoints? spawns)
    {
        ArgumentNullException.ThrowIfNull(options);

        var listening = options.MeshConnect.Length == 0;

        var link = listening
            ? MeshLink.Listen(System.Net.IPAddress.Any, options.MeshListenPort)
            : Connect(options.MeshConnect);

        return new MeshSession(
            link,
            new MeshNode(collision, start, new MeshOptions(), spawns),
            listening,
            collision,
            in start,
            spawns);
    }

    private static MeshLink Connect(string target)
    {
        var colon = target.LastIndexOf(':');

        if (colon <= 0)
            throw new ArgumentException($"--mesh-connect wants host:port, not '{target}'");

        return MeshLink.Connect(
            target[..colon],
            int.Parse(target[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Submits this node's own players' inputs, sends them on, takes whatever
    /// the peer has said, and advances one tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Send before servicing.</b> What is queued goes out in the same call
    /// that reads, so a tick's inputs leave the machine on the tick they belong
    /// to rather than one later — a tick of avoidable staleness on a link whose
    /// whole cost is staleness.
    /// </remarks>
    public void Tick(ReadOnlySpan<PlayerInput> inputs, int liveSlots)
    {
        // ⛔ Serviced FIRST, always: this is where a listener ACCEPTS its peer,
        // so a tick that checked for a connection before servicing would never
        // find one. Measured as a deadlock — the server sat for 200 ticks with a
        // peer waiting on the other end of a socket it had not accepted.
        _link.Service(Node, Compare, Hear);

        // ⚠️ A joiner does not advance until the two nodes agree what tick it
        // is. Advancing would be simulating a match the peer is not in, and
        // every tick of it is thrown away by the rebuild.
        if (!Synchronised)
        {
            if (_link.PeerOrigin is not { } theirs)
                return;

            Node = new MeshNode(
                _collision, _start, new MeshOptions(), _spawns, startTick: (int)theirs);

            Synchronised = true;
        }

        // ⚠️ A listener keeps running with no peer — it is a server, and its own
        // clients are playing on it. It announces its tick while one is
        // connected: a Hello is nine bytes and hearing it twice is harmless,
        // which is cheaper than a handshake with states in it.
        if (Listening && _link.Connected && Node.Tick % 5 == 0)
            _link.SendHello(Node.Tick);

        var tick = Node.Tick;

        _slots.Clear();
        _inputs.Clear();

        for (var slot = 0; slot < liveSlots && slot < inputs.Length; slot++)
        {
            if (!Owns(slot) || !Node.State.HasPlayer[slot])
                continue;

            Node.Submit(tick, slot, inputs[slot]);
            _slots.Add(slot);
            _inputs.Add(inputs[slot]);
        }

        _link.SendInputs(tick, _slots.ToArray(), _inputs.ToArray());

        // A digest a second, of a tick this node has confirmed — so the peer
        // compares against something that can no longer change.
        if (tick % 30 == 0 && Node.Confirmed > 0 && Node.DigestAt(Node.Confirmed - 1) is { } mine)
            _link.SendDigest(Node.Confirmed - 1, mine);

        Node.Advance();
    }

    /// <summary>
    /// Checks a peer's digest against this node's own for that same tick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Against the same TICK, never the live world.</b> Both ends are still
    /// guessing about each other's most recent ticks, so two nodes in complete
    /// agreement about the match differ at the head. A tick outside the window
    /// is counted as neither — it is unchecked, not agreed.
    /// </remarks>
    private void Compare(int tick, ulong theirs)
    {
        if (Node.DigestAt(tick) is not { } ours)
            return;

        if (ours == theirs)
        {
            Agreements++;
            return;
        }

        Disagreements++;
        _link.NoteDisagreement();
    }

    /// <summary>
    /// Takes a roster change the peer scheduled: applies it to this node and
    /// queues it for the server to mirror into its own roster.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Refused by the node means refused here too, and it is counted
    /// rather than papered over.</b> A command that arrived too late to be
    /// applied at its agreed tick cannot be applied at a different one: the two
    /// nodes would then be running different matches, which is the failure the
    /// whole scheme exists to avoid.
    /// </remarks>
    private void Hear(int tick, MeshCommand command, string name)
    {
        if (!Node.Schedule(tick, command))
            return;

        // ⚠️ Keyed by WHO SAID IT and their counter, not by a slot: a join
        // carries no slot, and a slot is renumbered the moment somebody below it
        // leaves. NameOf resolves it through the assignment instead.
        if (name.Length > 0)
            PeerNames[(command.Origin << 16) | (command.Sequence & 0xFFFF)] = name;

        FromPeer.Enqueue((tick, command, name));
    }

    public void Dispose() => _link.Dispose();
}
