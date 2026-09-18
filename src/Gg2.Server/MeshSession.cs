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

    /// <summary>Whether the attached peer has been handed this node's match.</summary>
    private bool _sent;

    /// <summary>Whether this node waited for the peer rather than joining it.</summary>
    /// <remarks>
    /// <b>A node's <see cref="Origin"/> has to be decided the same way on both
    /// machines</b>, and the only asymmetry two nodes have for free is which of
    /// them listened: the listener is origin 0.
    /// <para>
    /// ⚠️ <b>Which SLOTS a node owns is not a parity.</b> A slot belongs to
    /// whoever's <c>AddPlayer</c> created it — <see cref="Owns"/> reads
    /// <c>MeshNode.OriginOf</c> — because a join takes the lowest free place and
    /// a departure renumbers everything above it. The <c>mesh</c> subcommand
    /// does split a fixed roster by parity, but that is a synthetic match with
    /// nobody joining or leaving.
    /// </para>
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

        // ⚠️ OUR OWN joins are remembered too, not just the peer's. A snapshot
        // has to name every player in the match, and this node's own names live
        // in the GameServer's roster where a snapshot cannot reach them.
        if (command.Kind == MeshCommandKind.AddPlayer && name.Length > 0)
            PeerNames[Key(command.Origin, command.Sequence)] = new string(name);

        if (command.Kind == MeshCommandKind.ChangeMap && name.Length > 0)
            _maps[at] = new string(name);

        return at;
    }

    /// <summary>
    /// This node's whole match, for a peer that has just attached.
    /// </summary>
    private MeshSnapshot Snapshot() =>
        new()
        {
            Tick = Node.Tick,
            State = Node.State,
            Members = Node.Assignments()
                .Select(join => new MeshMember(
                    join.Origin,
                    join.Sequence,
                    join.Slot,
                    PeerNames.TryGetValue(Key(join.Origin, join.Sequence), out var name)
                        ? name
                        : string.Empty))
                .ToArray(),
        };

    /// <summary>
    /// Takes on the match a peer is already running, replacing this node
    /// wholesale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Returns the new node so the LINK can keep parsing into it.</b> The
    /// frames after a snapshot in the same read are the inputs for the tick it
    /// was taken at; handed to the node being discarded they are gone, and a
    /// lost input is a permanent divergence rather than a late correction.
    /// </para>
    /// <para>
    /// ⚠️ <b>A listener never adopts, and nobody adopts twice.</b> One end holds
    /// the match and the other takes it; two nodes each adopting the other would
    /// be two nodes each waiting to be told.
    /// </para>
    /// </remarks>
    private MeshNode? Adopt(byte[] bytes)
    {
        if (Listening || Synchronised)
            return null;

        if (MeshSnapshot.Decode(bytes) is not { } snapshot)
        {
            // A frame that is the right shape and the wrong content. Saying so
            // is the difference between "the peer is old" and "nothing
            // happened".
            Refused++;
            return null;
        }

        Node = new MeshNode(
            _collision, snapshot.State, new MeshOptions(), _spawns, startTick: snapshot.Tick);

        Node.Adopt(snapshot.Members);

        // Every player in the snapshot is the peer's, by definition: this node
        // had none. Their names are what this server's own clients will see.
        foreach (var member in snapshot.Members)
        {
            if (member.Name.Length > 0)
                PeerNames[Key(member.Origin, member.Sequence)] = member.Name;
        }

        Synchronised = true;

        return Node;
    }

    /// <summary>
    /// The map each scheduled change names, by the tick it lands at.
    /// </summary>
    /// <remarks>
    /// A <see cref="MeshCommand"/> is five bytes with nowhere to put a name, so
    /// the map rides in the frame's name field and is remembered here — by both
    /// ends, because both have to load it.
    /// </remarks>
    private readonly Dictionary<int, string> _maps = [];

    /// <summary>The map a change landing at this tick names, or null.</summary>
    public string? MapChangeAt(int tick) => _maps.TryGetValue(tick, out var map) ? map : null;

    /// <summary>
    /// Replaces the node around a new map, keeping the tick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A map change is a BARRIER, and the rollback history goes with it.</b>
    /// Rewinding across one would mean restoring a world belonging to a map that
    /// is no longer loaded. Both nodes rebuild at the same tick from the same
    /// reset world, so there is nothing before it either of them still needs.
    /// </remarks>
    public void Rebuild(CollisionWorld collision, in WorldState start, SpawnPoints? spawns)
    {
        ArgumentNullException.ThrowIfNull(collision);

        Node = new MeshNode(collision, start, new MeshOptions(), spawns, startTick: Node.Tick);
    }

    /// <summary>
    /// Chat bubbles the peer's players made, for this server to show its own
    /// clients.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Forwarded rather than scheduled.</b> A bubble never reaches
    /// <c>WorldState</c>, so there is nothing for two nodes to agree about and
    /// nothing a rewind could unmake — it is not held either. What it needs is
    /// to arrive, so that a player in one region can see one from the other.
    /// </remarks>
    public Queue<(int Slot, byte Image)> ChatFromPeer { get; } = [];

    /// <summary>Tells the peer about a bubble one of this node's players made.</summary>
    public void SendChat(int slot, byte image) => _link.SendChat(slot, image);

    private void HearChat(int slot, byte image)
    {
        if ((uint)slot < WorldState.MaxSlots)
            ChatFromPeer.Enqueue((slot, image));
    }

    /// <summary>Snapshots refused as malformed. Zero unless the builds differ.</summary>
    public int Refused { get; private set; }

    private static int Key(int origin, int sequence) => (origin << 16) | (sequence & 0xFFFF);

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
        _link.Service(Node, Compare, Hear, Adopt, HearChat);

        // ⚠️ Reset when the link goes, so a peer that reconnects is caught up
        // again rather than left running a match it was never told about.
        if (!_link.Connected)
            _sent = false;

        // ⚠️ A joiner does not advance until it has been handed the match.
        // Advancing would be simulating one the peer is not in, and every tick
        // of it is thrown away by the adoption.
        if (!Synchronised)
            return;

        // ⛔ A JOINER CATCHES UP, and this is not a nicety. Adopting a snapshot
        // leaves it on the tick the snapshot was taken while the peer has moved
        // on, and a node that stays behind schedules its roster changes against
        // its OWN clock — so a command it sends for "ten ticks from now" is
        // already in the peer's past by however far behind it is, and is refused
        // as too late. Measured as a join whose team change never arrived.
        //
        // ⚠️ The catch-up ticks are simulated on GUESSED inputs, which is what
        // rollback is for: the peer's real ones are still in flight and correct
        // them when they land. Bounded by the window, because beyond it there is
        // nothing to roll back to.
        if (!Listening && _link.PeerOrigin is { } theirs)
        {
            var behind = Math.Min((int)theirs - Node.Tick, new MeshOptions().MaxRollbackTicks - 1);

            for (var i = 0; i < behind; i++)
                Node.Advance();
        }

        // ⛔ Queued BEFORE this tick's inputs, on the first tick a peer is
        // attached, so the snapshot reaches them ahead of any input frame. The
        // other order hands a joiner inputs for a match it has not got yet, and
        // an input parsed by the node about to be discarded is lost for good.
        if (Listening && _link.Connected && !_sent)
        {
            _link.SendState(Snapshot().Encode());
            _sent = true;
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

        // A map change's "name" is the map, and this end has to load it too.
        if (command.Kind == MeshCommandKind.ChangeMap && name.Length > 0)
            _maps[tick] = name;

        FromPeer.Enqueue((tick, command, name));
    }

    public void Dispose() => _link.Dispose();
}
