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

    private MeshSession(MeshLink link, MeshNode node, bool listening)
    {
        _link = link;
        Node = node;
        Listening = listening;
    }

    /// <summary>The rollback node. The world lives here while a mesh is running.</summary>
    public MeshNode Node { get; }

    /// <summary>Whether this node waited for the peer rather than joining it.</summary>
    /// <remarks>
    /// <b>Which half of the roster a node owns has to be decided the same way on
    /// both machines</b>, and the only asymmetry two nodes have for free is which
    /// of them listened. The listener takes the even slots.
    /// </remarks>
    public bool Listening { get; }

    public bool Connected => _link.Connected;

    public MeshLinkStats LinkStats => _link.Stats;

    /// <summary>Ticks where this node and its peer disagreed about the world.</summary>
    public int Disagreements { get; private set; }

    /// <summary>Ticks where they agreed, so a zero above means something.</summary>
    public int Agreements { get; private set; }

    /// <summary>Whether a roster slot is this node's to drive.</summary>
    public bool Owns(int slot) => slot % 2 == (Listening ? 0 : 1);

    public static MeshSession Start(
        ServerOptions options, CollisionWorld collision, in WorldState start, SpawnPoints? spawns)
    {
        ArgumentNullException.ThrowIfNull(options);

        var listening = options.MeshConnect.Length == 0;

        var link = listening
            ? MeshLink.Listen(System.Net.IPAddress.Any, options.MeshListenPort)
            : Connect(options.MeshConnect);

        return new MeshSession(link, new MeshNode(collision, start, new MeshOptions(), spawns), listening);
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

        _link.Service(Node, Compare);
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

    public void Dispose() => _link.Dispose();
}
