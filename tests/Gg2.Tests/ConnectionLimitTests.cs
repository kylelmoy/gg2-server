using System.Net;
using System.Net.Sockets;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// What one socket can make the server hold and do.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Every case here is an attack that needs no modified client and no
/// malformed byte.</b> Opening a socket and not reading from it, or reserving a
/// slot and pinging, are things a stock client does as part of its normal
/// operation — just never for ever. That is why none of this was caught by the
/// parity work: there is nothing wrong with the bytes.
/// </para>
/// <para>
/// ⚠️ <b>Each limit is driven from a deliberately tiny configuration.</b> The
/// shipped defaults are megabytes and minutes, so a test that used them would
/// have to move real volume to prove anything and would be slow and flaky. The
/// mechanism is the same at either size; <see cref="TheShippedDefaultsClearWhatARealClientDoes"/>
/// is what pins the shipped numbers themselves.
/// </para>
/// </remarks>
public sealed class ConnectionLimitTests
{
    /// <summary>A socket pair over loopback, with the server end wrapped.</summary>
    /// <remarks>
    /// A real socket rather than a stub: the whole point of these limits is what
    /// happens against kernel buffers that fill, and a fake socket would confirm
    /// the arithmetic while proving nothing about the behaviour.
    /// </remarks>
    private sealed class Pair : IDisposable
    {
        private readonly Socket _listener;

        public Pair(ConnectionLimits limits)
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(1);

            Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Client.Connect((IPEndPoint)_listener.LocalEndPoint!);

            Server = new ClientConnection(_listener.Accept(), limits);
        }

        public Socket Client { get; }

        public ClientConnection Server { get; }

        public void Dispose()
        {
            Server.Dispose();
            Client.Dispose();
            _listener.Dispose();
        }
    }

    /// <summary>
    /// THE cheapest denial of service against this server: connect, join, never
    /// call <c>recv</c>.
    /// </summary>
    /// <remarks>
    /// The client is behaving legally as far as TCP is concerned — it simply has
    /// a closed receive window — so nothing else on the path notices. Before
    /// this limit the pending array doubled until the process was killed, and a
    /// handful of sockets doing it is an out-of-memory kill on any machine.
    /// </remarks>
    [Fact]
    public void AClientThatStopsReadingIsDroppedRatherThanBufferedForEver()
    {
        var limits = ConnectionLimits.Default with { MaxSendBacklogBytes = 128 * 1024 };
        using var pair = new Pair(limits);

        var chunk = new byte[16 * 1024];

        // Push until the kernel's send buffer, the peer's receive window and
        // then the server's own queue are all full. The client never reads.
        for (var i = 0; i < 2000 && !pair.Server.IsFaulted; i++)
        {
            pair.Server.Send(chunk);
            pair.Server.Flush();
        }

        Assert.True(pair.Server.IsFaulted, "the connection should have been faulted, not grown");
        Assert.Contains("not reading", pair.Server.CloseReason, StringComparison.Ordinal);

        // ⚠️ The bound actually held: the queue never went past the limit. A
        // fault raised after the array had already doubled to a gigabyte would
        // pass the assertion above and fix nothing.
        Assert.True(
            pair.Server.PendingSendBytes <= limits.MaxSendBacklogBytes,
            $"queued {pair.Server.PendingSendBytes} bytes against a {limits.MaxSendBacklogBytes} limit");
    }

    /// <summary>
    /// The other direction: a client that sends faster than the server drains.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference's own flood control is what makes this unbounded.</b>
    /// <c>ProcessCommands</c> handles ten commands per player per tick and
    /// leaves the rest buffered, so the faster the flood the faster the buffer
    /// grows. Dropping the excess is not an option — losing bytes out of the
    /// middle of a command stream desynchronises the framer permanently — so the
    /// connection ends instead.
    /// </remarks>
    [Fact]
    public void AClientThatSendsFasterThanItIsDrainedIsDroppedRatherThanBuffered()
    {
        var limits = ConnectionLimits.Default with
        {
            MaxReceiveBufferBytes = 96 * 1024,
            MaxReceiveBytesPerTick = 32 * 1024,
        };

        using var pair = new Pair(limits);
        var buffer = new ReceiveBuffer();
        var chunk = new byte[8 * 1024];

        pair.Client.Blocking = false;

        for (var i = 0; i < 400 && !pair.Server.IsFaulted; i++)
        {
            try
            {
                pair.Client.Send(chunk);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                // The client's own send buffer is full, which means the server
                // has stopped taking bytes. That is the state under test.
            }

            // Exactly what a tick does: receive once, then consume nothing —
            // standing in for a stream of commands the ten-per-tick budget
            // cannot keep up with.
            pair.Server.Receive(buffer);
        }

        Assert.True(pair.Server.IsFaulted, "the connection should have been faulted, not grown");
        Assert.Contains("receive buffer full", pair.Server.CloseReason, StringComparison.Ordinal);
        Assert.True(
            buffer.Count <= limits.MaxReceiveBufferBytes,
            $"buffered {buffer.Count} bytes against a {limits.MaxReceiveBufferBytes} limit");
    }

    /// <summary>
    /// One socket cannot decide how long a tick takes.
    /// </summary>
    /// <remarks>
    /// <c>Receive</c> used to drain until the kernel would block, on the thread
    /// every other player's physics runs on. This is a separate concern from the
    /// buffer ceiling — that one is about memory, this one is about the 33 ms
    /// budget the server owes everybody already connected.
    /// </remarks>
    [Fact]
    public void OneSocketCannotSpendMoreThanItsShareOfATick()
    {
        var limits = ConnectionLimits.Default with { MaxReceiveBytesPerTick = 4096 };
        using var pair = new Pair(limits);
        var buffer = new ReceiveBuffer();

        pair.Client.Send(new byte[64 * 1024]);

        // Wait for the bytes to actually arrive, or the assertion below would
        // pass on an empty socket and prove nothing about the cap.
        var waited = 0;
        while (pair.Server.Receive(buffer) == 0 && waited++ < 100)
            Thread.Sleep(10);

        Assert.True(buffer.Count > 0, "no bytes arrived; the test could not observe the cap");
        Assert.True(
            buffer.Count <= limits.MaxReceiveBytesPerTick,
            $"took {buffer.Count} bytes in one call against a {limits.MaxReceiveBytesPerTick} cap");
    }

    /// <summary>
    /// ⚠️ <b>The control case.</b> Every other assertion here is that a
    /// connection was killed, which is the shape that also passes if the limits
    /// kill everything. An ordinary exchange — well under every bound — must go
    /// through untouched.
    /// </summary>
    [Fact]
    public void AnOrdinaryExchangeIsNotAffectedByAnyOfThis()
    {
        using var pair = new Pair(ConnectionLimits.Default);
        var buffer = new ReceiveBuffer();

        // A stock client's per-tick traffic: one six-byte INPUTSTATE.
        pair.Client.Send([(byte)MessageType.InputState, 0, 0, 0, 0, 0]);

        var waited = 0;
        while (buffer.Count < 6 && waited++ < 100)
        {
            pair.Server.Receive(buffer);
            Thread.Sleep(10);
        }

        Assert.Equal(6, buffer.Count);
        Assert.False(pair.Server.IsFaulted);

        pair.Server.Send(new byte[2048]);
        pair.Server.Flush();

        Assert.False(pair.Server.IsFaulted);
        Assert.Equal(0, pair.Server.PendingSendBytes);
    }

    /// <summary>
    /// The shipped numbers, against what the protocol and a real client
    /// actually need.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the test that fails if somebody tunes a default down to
    /// something that breaks a legal client.</b> The limits above are all driven
    /// from tiny custom values, so none of them says anything about the
    /// defaults — and a default that rejects a message the protocol allows is a
    /// limit defending nothing and breaking the game.
    /// </remarks>
    [Fact]
    public void TheShippedDefaultsClearWhatARealClientDoes()
    {
        var limits = ConnectionLimits.Default;

        // A ushort-prefixed plugin packet is the largest legal message: command
        // byte, two length bytes, 65,535 of payload. Both directions must hold
        // one without the connection dying for sending it.
        const int largestLegalMessage = 3 + ushort.MaxValue;

        Assert.True(limits.MaxReceiveBufferBytes >= largestLegalMessage);
        Assert.True(limits.MaxSendBacklogBytes >= largestLegalMessage);

        // ⚠️ The per-tick allowance does NOT have to clear one, and asserting
        // that it did was wrong. The receive buffer persists across ticks and
        // the framer returns "not yet" until the message is whole, so a maximal
        // plugin packet simply arrives over two ticks — 66 ms. What matters is
        // that it arrives in a bounded and small number of them, rather than
        // being throttled into something a client would experience as a stall.
        Assert.True(
            (largestLegalMessage + limits.MaxReceiveBytesPerTick - 1) / limits.MaxReceiveBytesPerTick <= 4,
            "the largest legal message should assemble within a few ticks");

        // The joining queue must be able to hold more connections than the
        // server has seats: a handshake is several round trips and a map
        // download holds one open, so joining legitimately outnumbers joined.
        Assert.True(limits.MaxJoiningConnections > 48, "48 is the highest PlayerLimit allows");

        // And the handshake deadline has to be longer than the idle timeout it
        // sits behind, or it would be the only one that ever fired.
        Assert.True(limits.MaxHandshakeDuration > JoiningConnection.Timeout);

        limits.Validate();
    }

    /// <summary>
    /// A limit low enough to reject a legal message is refused at startup rather
    /// than at three in the morning.
    /// </summary>
    [Fact]
    public void ALimitThatWouldBreakTheProtocolIsRejectedAtStartup()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (ConnectionLimits.Default with { MaxReceiveBufferBytes = 1024 }).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => (ConnectionLimits.Default with { MaxSendBacklogBytes = 1024 }).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => (ConnectionLimits.Default with { MaxAcceptsPerTick = 0 }).Validate());

        // A deadline shorter than the idle timeout would make the idle timeout
        // dead code, which is a configuration that silently means something else.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (ConnectionLimits.Default with
            {
                MaxHandshakeDuration = TimeSpan.FromSeconds(5),
            }).Validate());
    }
}

/// <summary>
/// The framer's tolerance for bytes it does not recognise, and its ceiling.
/// </summary>
public sealed class InvalidCommandByteTests
{
    /// <summary>
    /// ⚠️ <b>The reference behaviour, which is kept.</b> An unrecognised command
    /// byte is skipped: the stream is not resynchronised, the client is not
    /// kicked, and it does not spend one of the ten commands a tick allows.
    /// A stray byte must still cost nothing.
    /// </summary>
    [Fact]
    public void AStrayInvalidByteIsStillSkippedAndStillFree()
    {
        var stream = new CommandStream();
        var buffer = new ReceiveBuffer();

        // 0xFE is not in the command table; the DROP_INTEL after it is.
        buffer.Append([0xFE, (byte)MessageType.DropIntel]);

        Assert.True(stream.TryRead(buffer, out var command));
        Assert.Equal(MessageType.DropIntel, command.Type);
        Assert.Equal(1, stream.InvalidBytesSkipped);
    }

    /// <summary>
    /// The counter exists because skipping is the one path that consumes bytes
    /// without producing a command — so the ten-per-tick budget does not
    /// rate-limit it at all.
    /// </summary>
    [Fact]
    public void SkippedBytesAreCountedSoTheServerCanNoticeAFlood()
    {
        var stream = new CommandStream();
        var buffer = new ReceiveBuffer();

        buffer.Append(new byte[5000].Select(_ => (byte)0xFE).ToArray());

        Assert.False(stream.TryRead(buffer, out _));
        Assert.Equal(5000, stream.InvalidBytesSkipped);
        Assert.True(buffer.IsEmpty, "the whole flood was consumed in one pass, which is the cost");
    }
}
