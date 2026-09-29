using System.Net;
using System.Net.Sockets;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// A minimal client that speaks the handshake, for driving a real
/// <c>GameServer</c> over a real socket in tests.
/// </summary>
/// <remarks>
/// Deliberately not built on the server's own code: a test that shares an
/// implementation with the thing it tests agrees with it by construction. This
/// writes the bytes docs/PROTOCOL.md says a client writes, and reads back raw.
/// <para>
/// It is also the seed of docs/PARITY.md's tier-0 recorder — that is the same
/// program with the reads kept instead of asserted on.
/// </para>
/// </remarks>
public sealed class LoopbackClient : IDisposable
{
    private readonly Socket _socket;
    private readonly List<byte> _received = [];

    public LoopbackClient(int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        _socket.Blocking = false;
    }

    /// <summary>Everything received so far, unframed.</summary>
    public IReadOnlyList<byte> Received => _received;

    /// <summary>HELLO plus the 16 bytes of PROTOCOL_UUID — exactly 17 bytes.</summary>
    public void SendHello()
    {
        var writer = new NetWriter(17);
        writer.WriteMessageType(MessageType.Hello);
        writer.WriteBytes(GameConstants.ProtocolUuidBytes);
        Send(writer.WrittenSpan);
    }

    /// <summary>HELLO with a deliberately wrong UUID.</summary>
    public void SendHelloWithWrongUuid()
    {
        var uuid = GameConstants.ProtocolUuidBytes.ToArray();
        uuid[^1] ^= 0xFF;

        var writer = new NetWriter(17);
        writer.WriteMessageType(MessageType.Hello);
        writer.WriteBytes(uuid);
        Send(writer.WrittenSpan);
    }

    public void SendPassword(string password)
    {
        var writer = new NetWriter(64);
        writer.WriteString1(password);
        Send(writer.WrittenSpan);
    }

    public void SendReserveSlot(string name)
    {
        var writer = new NetWriter(64);
        writer.WriteMessageType(MessageType.ReserveSlot);
        writer.WriteString1(name);
        Send(writer.WrittenSpan);
    }

    public void SendPlayerJoin() => SendCommand(MessageType.PlayerJoin);

    public void SendPing() => SendCommand(MessageType.Ping);

    public void SendCommand(MessageType type)
    {
        Span<byte> one = [(byte)type];
        Send(one);
    }

    public void SendInputState(PlayerInput input)
    {
        var writer = new NetWriter(8);
        writer.WriteMessageType(MessageType.InputState);
        input.Write(writer);
        Send(writer.WrittenSpan);
    }

    public void SendChangeTeam(Team team)
    {
        var writer = new NetWriter(4);
        writer.WriteMessageType(MessageType.PlayerChangeTeam);
        writer.WriteUByte((byte)team);
        Send(writer.WrittenSpan);
    }

    public void SendChangeClass(PlayerClass playerClass)
    {
        var writer = new NetWriter(4);
        writer.WriteMessageType(MessageType.PlayerChangeClass);
        writer.WriteUByte((byte)playerClass);
        Send(writer.WrittenSpan);
    }

    public void Send(ReadOnlySpan<byte> bytes)
    {
        var sent = 0;
        while (sent < bytes.Length)
            sent += _socket.Send(bytes[sent..], SocketFlags.None);
    }

    /// <summary>Drains whatever has arrived. Never blocks.</summary>
    public void Poll()
    {
        var scratch = new byte[8192];

        while (true)
        {
            int read;
            try
            {
                read = _socket.Receive(scratch, SocketFlags.None);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                return;
            }
            catch (SocketException)
            {
                IsClosed = true;
                return;
            }
            catch (ObjectDisposedException)
            {
                // The test disposed this client to simulate a disconnect. The
                // harness still polls every client it handed out, so a disposed
                // one has to answer "closed" rather than throwing — otherwise a
                // departure cannot be tested at all.
                IsClosed = true;
                return;
            }

            if (read == 0)
            {
                IsClosed = true;
                return;
            }

            _received.AddRange(scratch.AsSpan(0, read).ToArray());
        }
    }

    /// <summary>True once the server has closed the connection.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Consumes and returns the next <paramref name="count"/> bytes.</summary>
    public byte[] Take(int count)
    {
        Assert.True(_received.Count >= count,
            $"expected {count} more byte(s), have {_received.Count}");

        var bytes = _received.Take(count).ToArray();
        _received.RemoveRange(0, count);
        return bytes;
    }

    public byte TakeByte() => Take(1)[0];

    public MessageType TakeMessageType() => (MessageType)TakeByte();

    public ushort TakeUShort()
    {
        var bytes = Take(2);
        return (ushort)(bytes[0] | (bytes[1] << 8));
    }

    public uint TakeUInt()
    {
        var bytes = Take(4);
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }

    public string TakeString1() => GG2Text.Decode(Take(TakeByte()));

    public string TakeString2() => GG2Text.Decode(Take(TakeUShort()));

    /// <summary>
    /// Closes the connection. Also how a test simulates a client leaving — the
    /// harness keeps polling it, and <see cref="Poll"/> answers "closed".
    /// </summary>
    public void Dispose()
    {
        IsClosed = true;
        _socket.Dispose();
    }
}
