namespace Gg2.Server;

/// <summary>
/// What one socket is allowed to make the server hold and do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The reference server was written for a game
/// hosted off somebody's desktop for their friends, and it has no bound on any
/// of this because on a LAN there is no one to impose it. Every buffer in the
/// port's transport was the same shape: grow on demand, for ever. On the open
/// internet each of those is a way for one connection to end the server for
/// everybody — and none of them needs a protocol exploit, a malformed packet or
/// even a modified client. Opening a socket and then not reading from it is
/// enough.
/// </para>
/// <para>
/// <b>Every default here is far above what a stock client does and far below
/// what breaks a machine.</b> That is the shape a limit has to have: a bound a
/// real player can reach is an outage the operator has to debug, and a bound an
/// attacker cannot reach is decoration. Each field's remarks say what a real
/// client actually uses, because that ratio is the whole argument for the
/// number and it is the thing that goes stale if the protocol changes.
/// </para>
/// <para>
/// ⚠️ <b>None of this is observable simulation and none of it is on the
/// wire.</b> A client that stays inside every limit — which is every client
/// anybody will ever connect — cannot tell these exist. What changes is what
/// happens to a client that does not: it is disconnected, where before the
/// server grew a buffer for it until something died.
/// </para>
/// </remarks>
public sealed record ConnectionLimits
{
    /// <summary>The defaults, which is what a server that configures nothing gets.</summary>
    public static ConnectionLimits Default { get; } = new();

    /// <summary>
    /// Unread bytes one client may have buffered before it is disconnected.
    /// </summary>
    /// <remarks>
    /// <b>The attack it stops.</b> <c>ProcessCommands</c> handles at most ten
    /// commands per player per tick — flood control the reference already has —
    /// and leaves the rest buffered. A client that sends faster than ten
    /// commands a tick therefore grows this buffer for ever, and the reference's
    /// own flood control is what guarantees it: the faster the flood, the faster
    /// the growth. A single socket on a home connection adds megabytes a second.
    /// <para>
    /// <b>What a real client uses.</b> One <c>INPUTSTATE</c> per tick, six bytes
    /// (<c>ClientInputstate.gml</c>), plus a command now and then. At 30 Hz that
    /// is 180 bytes a second against a buffer that is drained every tick, so the
    /// steady state is a few bytes. The one large legal message is a plugin
    /// packet, which is length-prefixed with a <c>ushort</c> and so can be
    /// 65,538 bytes — the floor under this number, and why it is not 8 KB.
    /// </para>
    /// </remarks>
    public int MaxReceiveBufferBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Bytes read from one socket in one tick, however many are waiting.
    /// </summary>
    /// <remarks>
    /// <b>A separate concern from the buffer size, and it is about the clock
    /// rather than about memory.</b> <c>ClientConnection.Receive</c> drains
    /// until the kernel says it would block, so without this one client with a
    /// fast link decides how long the tick takes — it can hand the server as
    /// much as it can push, and the copying happens on the thread every other
    /// player's physics runs on. Capped, a flood costs a bounded slice of the
    /// tick and fills the buffer above instead, which ends the connection.
    /// <para>
    /// 64 KB a tick is 1.9 MB/s per client, roughly ten thousand times what a
    /// stock client sends.
    /// </para>
    /// </remarks>
    public int MaxReceiveBytesPerTick { get; init; } = 64 * 1024;

    /// <summary>
    /// Bytes queued for one client that the kernel has not taken, before it is
    /// disconnected.
    /// </summary>
    /// <remarks>
    /// <b>The attack it stops, and it is the cheapest one there is.</b> Connect,
    /// join, then stop calling <c>recv</c>. The server keeps broadcasting; the
    /// client's receive window closes; every tick's update lands in
    /// <c>_pending</c> and nothing ever leaves it. No malformed byte is
    /// involved, the client is behaving legally as far as TCP is concerned, and
    /// the server grows the array until the process dies. A handful of sockets
    /// doing this is an out-of-memory kill.
    /// <para>
    /// <b>What a real client uses.</b> Nothing, almost always — the comment on
    /// <c>ClientConnection.PendingSendBytes</c> is right that a client which
    /// cannot keep up with 30 Hz has bigger problems. The number has to clear
    /// two bursts: a join burst, which is a <c>FULL_UPDATE</c> for a full
    /// roster, and a custom map download, which is throttled to 50 KB/s
    /// server-wide (<c>MapDownload.DefaultBytesPerSecond</c>) and whose largest
    /// shipped map is 77 KB. A megabyte is about sixteen seconds of a full
    /// server's broadcast going nowhere, which is far longer than a real hiccup
    /// and far shorter than an outage.
    /// </para>
    /// </remarks>
    public int MaxSendBacklogBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Unrecognised command bytes tolerated on one connection before it is
    /// dropped.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference IGNORES an invalid command byte</b> — it does not
    /// resynchronise and it does not kick (<c>ClientCommands</c>, and
    /// <c>processClientCommands.gml</c> behind it). That behaviour is kept: a
    /// stray byte still costs nothing, and it still does not spend one of the
    /// ten commands a tick allows.
    /// <para>
    /// What is added is a ceiling on how long that tolerance lasts. Skipping is
    /// the one path through the framer that consumes buffered bytes without
    /// producing a command, so a client sending nothing but garbage is not
    /// rate-limited by the ten-command budget at all — it makes the server scan
    /// every byte it sends, every tick, for free. 4096 is generous enough that a
    /// confused client is tolerated for a while, and small enough that a sender
    /// doing nothing else stops being scanned.
    /// </para>
    /// <para>
    /// ⛔ <b>This threshold has been reached by a stock client, once.</b> A soak
    /// against this port lost one at 4099 bytes in 949 s, and what it sent has
    /// never been established. Until it is, <b>the number is not the thing to
    /// change</b>: the reference tolerates invalid bytes for ever and has no
    /// equivalent guard, so raising the ceiling and dropping it are different
    /// bets on the same unknown. <c>InvalidCommandReport</c> is what the kick
    /// line carries so the next occurrence settles it instead of repeating it;
    /// docs/HARDENING.md has the standing entry.
    /// </para>
    /// </remarks>
    public int MaxInvalidCommandBytes { get; init; } = 4096;

    /// <summary>
    /// Connections accepted in one tick, however many are waiting.
    /// </summary>
    /// <remarks>
    /// <b>The accept loop ran until the backlog was empty.</b> Each accept
    /// allocates a connection and walks the existing ones for the per-address
    /// check, so a burst of connections is quadratic work on the tick thread,
    /// and the tick has a 33 ms budget it must not miss for the players already
    /// on the server. Capped, a flood costs a bounded slice and the rest waits
    /// in the kernel's own backlog — which is where a connection flood is
    /// supposed to be absorbed, and where the OS can drop it far more cheaply
    /// than this process can.
    /// <para>
    /// 32 a tick is 960 joins a second against a server whose player limit tops
    /// out at 48.
    /// </para>
    /// </remarks>
    public int MaxAcceptsPerTick { get; init; } = 32;

    /// <summary>
    /// Half-finished handshakes held at once. Beyond this a new connection is
    /// refused immediately.
    /// </summary>
    /// <remarks>
    /// <b>The per-address limit does not cover this.</b>
    /// <c>ServerOptions.MultiClientLimit</c> bounds one address to three, which
    /// is the reference's answer to a household behind one NAT — it is not a
    /// defence, because an attacker with a hundred addresses has three hundred
    /// slots in the joining list and every one of them holds a socket, a receive
    /// buffer and an 8 KB scratch array. This bounds the list itself.
    /// <para>
    /// Sized well above the player limit on purpose: a handshake is several
    /// round trips and a map download can hold one open for a minute, so a busy
    /// server legitimately has more connections joining than it has seats.
    /// </para>
    /// </remarks>
    public int MaxJoiningConnections { get; init; } = 128;

    /// <summary>
    /// The longest a connection may take to finish joining, whatever it says in
    /// between.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference's 30-second timeout is an IDLE timeout and that is
    /// the hole.</b> <c>serviceJoiningPlayer.gml:1</c> drops a connection
    /// <c>current_time - lastContact &gt; 30000</c>, and every byte received
    /// refreshes <c>lastContact</c> — so a client that sends a one-byte
    /// <c>PING</c> every twenty seconds never times out. <c>PING</c> is
    /// explicitly allowed after <c>RESERVE_SLOT</c>, which is the point at which
    /// the connection is counted against the player limit
    /// (<c>OccupiedSlots</c>). Three connections from one address, each pinging,
    /// hold three seats indefinitely and the server advertises itself as fuller
    /// than it is. The idle timeout never fires, because the client is not idle.
    /// <para>
    /// This is the second clock, and it does not reset. Ten minutes is far
    /// beyond any real handshake — the slowest is a custom map download at
    /// 50 KB/s, which is under two seconds for the largest shipped map and would
    /// need a 30 MB map to reach this — and far short of "for ever".
    /// </para>
    /// </remarks>
    public TimeSpan MaxHandshakeDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Connections one address may open back to back before it has to wait.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the same as <c>ServerOptions.MultiClientLimit</c>.</b> That
    /// bounds how many connections one address holds AT ONCE — the reference's
    /// answer to a household behind one NAT. This bounds how FAST it opens
    /// them, which nothing else catches: connect, get refused, reconnect,
    /// repeat, with the concurrent count never above one and every cycle
    /// costing an accept, an allocation and a roster walk.
    /// <para>
    /// Ten in a minute is far above real behaviour — a player whose game
    /// crashed reconnects once, and even a bad connection retrying is a handful
    /// — and far below what a script does. Zero turns it off.
    /// </para>
    /// </remarks>
    public int ConnectionBurstPerAddress { get; init; } = 10;

    /// <summary>How long refilling <see cref="ConnectionBurstPerAddress"/> takes.</summary>
    public TimeSpan ConnectionBurstWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The shortest gap between one player's relayed chat bubbles.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A DELIBERATE DEPARTURE from the reference, and the only one in this
    /// type that a client could notice.</b> Everything else here bounds what the
    /// server holds; this drops a message the reference would have relayed.
    /// <para>
    /// <b>Why.</b> A chat bubble is broadcast to every connected client, and
    /// <c>ClientCommands.PerTickLimit</c> allows ten commands a player a tick —
    /// so one socket can make the server emit three hundred broadcasts a second
    /// to everybody, which on a full server is thousands of messages a second
    /// and a screen of bubbles nobody can play through. The reference throttles
    /// <c>PLAYER_CHANGENAME</c> to once a second for exactly this reason
    /// (<c>processClientCommands.gml:293-296</c>) — "every accepted rename is an
    /// unbounded broadcast" — and simply does not apply the same reasoning to
    /// the bubble beside it. That looks like an oversight rather than a
    /// decision, and on a LAN it never mattered.
    /// </para>
    /// <para>
    /// <b>250 ms, not the rename's 1000.</b> A bubble is a keypress and a human
    /// sends a few in a burst; a second would be felt. Four a second is above
    /// anything a person does with a keyboard and cuts the flood by 75×.
    /// Set to zero to relay every bubble, which is the reference's behaviour
    /// exactly.
    /// </para>
    /// </remarks>
    public int ChatBubbleIntervalMs { get; init; } = 250;

    /// <summary>
    /// Whether to ask TCP to notice a peer that has silently gone away.
    /// </summary>
    /// <remarks>
    /// <b>What breaks without it.</b> A client whose machine loses power, or
    /// whose link drops, sends no FIN and no RST. The server has no
    /// application-level heartbeat to miss — and cannot grow one, because a
    /// <em>spectator</em> legitimately sends nothing at all: only
    /// <c>PlayerControl</c> sends <c>INPUTSTATE</c> every tick, and a spectator
    /// takes the other branch of that event entirely
    /// (<c>PlayerControl.events/Begin Step.xml</c>). So a receive-idle timeout
    /// would kick spectators, and this is the correct mechanism instead: it asks
    /// the kernel, which is the only layer that can tell a quiet peer from an
    /// absent one.
    /// <para>
    /// The server does keep writing to a dead peer, so TCP's own retransmit
    /// eventually gives up — but that is <c>tcp_retries2</c>, around fifteen
    /// minutes on Linux by default, and for those fifteen minutes a ghost holds
    /// a roster slot. The probes below settle it in about a minute.
    /// </para>
    /// </remarks>
    public bool TcpKeepAlive { get; init; } = true;

    /// <summary>Quiet seconds before the first keepalive probe.</summary>
    public int KeepAliveIdleSeconds { get; init; } = 30;

    /// <summary>Seconds between probes once they have started.</summary>
    public int KeepAliveIntervalSeconds { get; init; } = 10;

    /// <summary>Unanswered probes before the connection is declared dead.</summary>
    public int KeepAliveProbeCount { get; init; } = 3;

    /// <summary>Throws if any limit is set to something unusable.</summary>
    /// <remarks>
    /// The floors are not arbitrary: a receive buffer below the largest legal
    /// message would disconnect a client for sending one, which is a limit that
    /// breaks the protocol rather than defending it.
    /// </remarks>
    public void Validate()
    {
        // A ushort-prefixed plugin packet, plus its command byte and length
        // prefix. Anything under this rejects a message the protocol allows.
        const int largestLegalMessage = 3 + ushort.MaxValue;

        if (MaxReceiveBufferBytes < largestLegalMessage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxReceiveBufferBytes),
                MaxReceiveBufferBytes,
                $"must be at least {largestLegalMessage}, the largest message the protocol allows");
        }

        if (MaxReceiveBytesPerTick < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxReceiveBytesPerTick), MaxReceiveBytesPerTick, "must be positive");

        if (MaxSendBacklogBytes < largestLegalMessage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSendBacklogBytes),
                MaxSendBacklogBytes,
                $"must be at least {largestLegalMessage}, the largest message the protocol allows");
        }

        if (ConnectionBurstPerAddress < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConnectionBurstPerAddress), ConnectionBurstPerAddress, "must not be negative; zero means no limit");
        }

        if (ConnectionBurstPerAddress > 0 && ConnectionBurstWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConnectionBurstWindow), ConnectionBurstWindow, "must be positive when a burst is set");
        }

        if (ChatBubbleIntervalMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChatBubbleIntervalMs), ChatBubbleIntervalMs, "must not be negative; zero means no throttle");
        }

        if (MaxInvalidCommandBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxInvalidCommandBytes), MaxInvalidCommandBytes, "must be positive");

        if (MaxAcceptsPerTick < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAcceptsPerTick), MaxAcceptsPerTick, "must be positive");

        if (MaxJoiningConnections < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxJoiningConnections), MaxJoiningConnections, "must be positive");

        if (MaxHandshakeDuration < JoiningConnection.Timeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxHandshakeDuration),
                MaxHandshakeDuration,
                $"must be at least the {JoiningConnection.Timeout.TotalSeconds:0}s idle timeout, or it would be the only one that ever fires");
        }
    }
}
