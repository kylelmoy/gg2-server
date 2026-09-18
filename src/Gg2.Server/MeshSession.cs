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

    private readonly IServerLog _log;
    private readonly CollisionWorld _collision;
    private readonly SpawnPoints? _spawns;
    private readonly WorldState _start;

    private MeshSession(
        MeshLink link, MeshNode node, bool listening,
        CollisionWorld collision, in WorldState start, SpawnPoints? spawns, IServerLog log)
    {
        _link = link;
        _log = log;
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

    /// <summary>
    /// Digests that arrived for a tick this node had not confirmed yet.
    /// </summary>
    /// <remarks>
    /// Not a fault: the two nodes confirm a tick at slightly different moments,
    /// so a sample can simply be early. It is counted because a run where EVERY
    /// sample is early would otherwise look like a run where the two nodes
    /// never compared anything, and those need telling apart.
    /// </remarks>
    public int Unconfirmed { get; private set; }

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
        // ⛔ REFUSED UNTIL THE TWO NODES ARE IN STEP. Before a joiner adopts the
        // match its node is a fresh one near tick zero, so "ten ticks from now"
        // is hundreds of ticks in the peer's past and every command sent from
        // here is refused at the far end — silently, because a refusal is only a
        // counter. Measured on a live two-host run: a joiner filled eight bots
        // on its first tick and all eight were rejected as too late, leaving two
        // servers that never agreed about who was playing.
        if (!Synchronised)
            return Refusal;

        // ⛔ AHEAD OF THE PEER'S CLOCK, NOT OUR OWN, and this is the difference
        // between a mesh that works across the internet and one that does not.
        // A node scheduling ten ticks ahead of ITSELF sends a tick already in
        // the past of a peer that is further along, and the peer refuses it —
        // silently, and unrecoverably, because it applied it here.
        //
        // Measured on a live two-host run over a real link: the joiner sat about
        // nineteen ticks behind the listener, every command it sent landed nine
        // ticks in the listener's past, and the two ran different matches from
        // the first bot onwards.
        var at = Math.Max(Node.Tick, PeerTickNow) + CommandLead;

        if (!Node.Schedule(at, command))
            return Refusal;

        _link.SendCommand(at, command, name);

        // ⛔ Every roster change is logged at BOTH ends, sent and heard, with the
        // join key and the tick. They are rare — a handful per match — and they
        // are the only thing whose loss cannot be recovered from, so the two
        // logs being diffable is what turns "the rosters ended up different" into
        // a specific command that went missing.
        _log.Info(
            $"mesh sent {command.Kind} origin {command.Origin} seq {command.Sequence} "
            + $"slot {command.Slot} for tick {at} (now {Node.Tick}) {LogText.Safe(new string(name))}");

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
    public void Rebuild(CollisionWorld collision, in WorldState start, SpawnPoints? spawns, int atTick)
    {
        ArgumentNullException.ThrowIfNull(collision);

        // ⛔ AT THE TICK THE COMMAND LANDED, never at this node's own tick. The
        // two nodes drain the change at the same tick but are not on the same
        // tick when they do it — one is always a little ahead — so rebuilding
        // "here" restarts the two matches at different numbers, and every input
        // afterwards is applied to a different moment on each side. Measured:
        // the digests disagreed from the rebuild onwards.
        Node = new MeshNode(collision, start, new MeshOptions(), spawns, startTick: atTick);
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

    /// <summary>
    /// Names the peer's players have been given, for this server to show its own
    /// clients.
    /// </summary>
    public Queue<(int Slot, string Name)> NamesFromPeer { get; } = [];

    /// <summary>Tells the peer that one of this node's players was renamed.</summary>
    public void SendName(int slot, ReadOnlySpan<char> name)
    {
        Remember(slot, new string(name));
        _link.SendName(slot, name);
    }

    private void HearName(int slot, string name)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        Remember(slot, name);
        NamesFromPeer.Enqueue((slot, name));
    }

    /// <summary>
    /// Keeps the name table current, so a SNAPSHOT carries what a player is
    /// called now.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Without this a rename is lost to everyone who was not listening when
    /// it happened.</b> The table is written when a join is scheduled and a
    /// snapshot is built from it, so a peer attaching later would be handed the
    /// name every player had at the moment they joined — which on a live run was
    /// every bot, because <c>checkClasslimits</c> renames one the moment it
    /// re-classes it.
    /// </remarks>
    private void Remember(int slot, string name)
    {
        if (Node.TryGetJoinKey(slot, out var origin, out var sequence))
            PeerNames[Key(origin, sequence)] = name;
    }

    private void HearChat(int slot, byte image)
    {
        if ((uint)slot < WorldState.MaxSlots)
            ChatFromPeer.Enqueue((slot, image));
    }

    /// <summary>
    /// Commands from the peer this node could not apply at the tick they name.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Any value but zero means the two nodes are running different
    /// matches.</b> The sender applied it; this node did not; nothing recovers
    /// from that.
    /// </remarks>
    public int RefusedFromPeer { get; private set; }

    /// <summary>How far outside the window the worst refusal was, in ticks.</summary>
    public int WorstRefusalSkew { get; private set; }

    /// <summary>
    /// Where the peer's clock is now, as best this node can tell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tick it last announced, plus the ticks this node has run since.</b>
    /// Both nodes advance at 30 Hz, so the error is the transport delay plus at
    /// most one announcement interval — small, and always an UNDER-estimate of
    /// how far the peer has got, which is the safe direction: scheduling further
    /// ahead costs a little latency, scheduling behind loses the command.
    /// </para>
    /// <para>
    /// ⚠️ Falls back to this node's own tick before anything has been heard, so
    /// a node with no peer schedules exactly as it always did.
    /// </para>
    /// </remarks>
    public int PeerTickNow
    {
        get
        {
            if (_peerTickHeard < 0)
                return Node.Tick;

            // ⛔ CLAMPED, and both ends of the clamp were paid for on a live
            // run. Extrapolating forward covers the transport delay, so a node
            // does not schedule into its peer's past; capping it stops a node
            // that is ticking SLOWER than real time — a loaded server, or a test
            // harness — from assuming its peer has run away, catching up to a
            // moment the peer has not reached, and then scheduling into a future
            // the peer will refuse. Measured both ways round: nine ticks into
            // the past first, then fifty-nine into the future.
            var elapsed = (int)((Environment.TickCount64 - _heardAtMs) * TicksPerMs);

            return _peerTickHeard + Math.Clamp(elapsed, 0, CommandLead);
        }
    }

    /// <summary>
    /// ⛔ Extrapolated on the WALL CLOCK, never on this node's own tick count.
    /// </summary>
    /// <remarks>
    /// Measured, after getting it wrong the other way on a live run: a joiner
    /// catching up advances its own tick in jumps, so an estimate that added
    /// "ticks I have run since" grew every time the catch-up ran — which drove
    /// more catch-up, which grew it further. The estimate ran 59 ticks past the
    /// peer and every command was refused for being too far in its FUTURE,
    /// having previously been refused for being in its past. Wall-clock time
    /// does not move when a node re-simulates, which is exactly the property
    /// this needs.
    /// </remarks>
    private const double TicksPerMs = 30.0 / 1000.0;

    private int _peerTickHeard = -1;
    private long _heardAtMs;

    /// <summary>
    /// Slots this node sent inputs for on the last tick.
    /// </summary>
    /// <remarks>
    /// ⛔ Zero means this node is telling its peer NOTHING about its own
    /// players, which the peer then guesses for ever and can never be corrected
    /// about. The peer's confirmation stalls and the two worlds part company
    /// with no refusal and no late input to show for it.
    /// </remarks>
    public int Owned { get; private set; }

    /// <summary>What <see cref="Schedule"/> returns when it would not be heard.</summary>
    public const int Refusal = -1;

    /// <summary>Snapshots refused as malformed. Zero unless the builds differ.</summary>
    public int Refused { get; private set; }

    private static int Key(int origin, int sequence) => (origin << 16) | (sequence & 0xFFFF);

    public static MeshSession Start(
        ServerOptions options, CollisionWorld collision, in WorldState start, SpawnPoints? spawns,
        IServerLog log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

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
            spawns,
            log);
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
        _link.Service(Node, Compare, Hear, Adopt, HearChat, HearName);

        // ⚠️ Reset when the link goes, so a peer that reconnects is caught up
        // again rather than left running a match it was never told about.
        if (!_link.Connected)
        {
            _sent = false;
            _peerTickHeard = -1;
        }

        // Where the peer said it was, and where this node was when it heard —
        // the two numbers PeerTickNow extrapolates from.
        if (_link.PeerOrigin is { } announced && (int)announced != _peerTickHeard)
        {
            _peerTickHeard = (int)announced;
            _heardAtMs = Environment.TickCount64;
        }

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
        if (!Listening && _peerTickHeard >= 0)
        {
            var behind = Math.Min(PeerTickNow - Node.Tick, new MeshOptions().MaxRollbackTicks - 1);

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
        // ⛔ BOTH ENDS announce their tick, not just the listener. A command is
        // scheduled against the PEER's clock (see Schedule), so each node has to
        // know where the other is — and the node that is behind is not always
        // the joiner.
        if (_link.Connected && Node.Tick % 5 == 0)
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

        Owned = _slots.Count;
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
        // ⛔ ONLY AT A TICK THIS NODE HAS ALSO CONFIRMED. The peer sends a digest
        // of a tick IT has confirmed, but confirmation is per node: this one may
        // still be guessing an input for that tick, and its world then differs
        // for an entirely correct reason that a rollback is about to fix.
        // Comparing anyway reports a divergence on every sample — measured on a
        // live two-host run, where two nodes that were in step and refusing
        // nothing still reported "agreed 0/6" because the samples all landed
        // before this node had confirmed the tick.
        if (tick >= Node.Confirmed)
        {
            Unconfirmed++;
            return;
        }

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
        {
            // ⛔ AN UNRECOVERABLE DIVERGENCE, and it used to be a silent return.
            // The sender applied this to its own node; refusing it here leaves
            // two nodes running different matches, which is the one failure the
            // whole scheme exists to avoid. It cannot be applied at a different
            // tick either — that would be a third match.
            //
            // The skew is kept because it is the only number that says WHY: a
            // command lands outside the window when the two clocks have drifted
            // further apart than CommandLead covers.
            RefusedFromPeer++;

            _log.Warn(
                $"mesh REFUSED {command.Kind} origin {command.Origin} seq {command.Sequence} "
                + $"for tick {tick} (now {Node.Tick}) — the peer applied it and this node cannot");

            var skew = tick - Node.Tick;

            if (Math.Abs(skew) > Math.Abs(WorstRefusalSkew))
                WorstRefusalSkew = skew;

            return;
        }

        // ⚠️ Keyed by WHO SAID IT and their counter, not by a slot: a join
        // carries no slot, and a slot is renumbered the moment somebody below it
        // leaves. NameOf resolves it through the assignment instead.
        if (name.Length > 0)
            PeerNames[(command.Origin << 16) | (command.Sequence & 0xFFFF)] = name;

        // A map change's "name" is the map, and this end has to load it too.
        if (command.Kind == MeshCommandKind.ChangeMap && name.Length > 0)
            _maps[tick] = name;

        _log.Info(
            $"mesh heard {command.Kind} origin {command.Origin} seq {command.Sequence} "
            + $"slot {command.Slot} for tick {tick} (now {Node.Tick}) {LogText.Safe(name)}");

        FromPeer.Enqueue((tick, command, name));
    }

    public void Dispose() => _link.Dispose();
}
