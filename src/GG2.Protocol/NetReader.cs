using System.Buffers.Binary;

namespace GG2.Protocol;

/// <summary>
/// Sequential little-endian reader over a received buffer.
/// </summary>
/// <remarks>
/// Little-endian is not a choice: <c>Scripts/Game/game_init.gml:23</c> calls
/// <c>set_little_endian_global(true)</c>, and the one exception in the whole
/// codebase is the lobby registration buffer, which a private server never
/// sends.
/// <para>
/// A <c>ref struct</c> so it can sit over a pooled buffer or a stack span with
/// no allocation and no chance of outliving the memory it points at.
/// </para>
/// </remarks>
public ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public NetReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>Bytes consumed so far.</summary>
    public readonly int Position => _position;

    /// <summary>Bytes not yet consumed.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    public readonly bool IsEmpty => Remaining == 0;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (Remaining < count)
            throw new ProtocolException(
                $"read of {count} byte(s) at offset {_position} runs past the end of a {_buffer.Length}-byte buffer");

        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    public byte ReadUByte() => Take(1)[0];

    public sbyte ReadByte() => (sbyte)Take(1)[0];

    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short ReadShort() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public MessageType ReadMessageType() => (MessageType)ReadUByte();

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    /// <summary>A <c>ubyte</c> length followed by that many raw bytes.</summary>
    public string ReadString1() => GG2Text.Decode(ReadString1Bytes());

    /// <summary>A <c>ushort</c> length followed by that many raw bytes.</summary>
    public string ReadString2() => GG2Text.Decode(ReadString2Bytes());

    public ReadOnlySpan<byte> ReadString1Bytes() => Take(ReadUByte());

    public ReadOnlySpan<byte> ReadString2Bytes() => Take(ReadUShort());

    /// <summary>Skips <paramref name="count"/> bytes without decoding them.</summary>
    public void Skip(int count) => Take(count);

    /// <summary>
    /// An arbitrary window of the underlying buffer, ignoring the read
    /// position.
    /// </summary>
    /// <remarks>
    /// For a framer that wants to report a message's whole body after decoding
    /// it field by field. Does not move the position.
    /// </remarks>
    public readonly ReadOnlySpan<byte> Window(int start, int length) => _buffer.Slice(start, length);
}
