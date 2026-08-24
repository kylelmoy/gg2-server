using System.Buffers;
using System.Buffers.Binary;

namespace Gg2.Protocol;

/// <summary>
/// Growable little-endian write buffer, the port's replacement for
/// <c>global.sendBuffer</c>.
/// </summary>
/// <remarks>
/// A class rather than a struct because it is meant to be <em>held and reused</em>:
/// the tick loop keeps one, fills it, fans the same bytes out to every socket
/// (the server sends one identical stream to all clients — docs/PROTOCOL.md,
/// "Transport"), then calls <see cref="Reset"/>. Steady state allocates nothing,
/// because the backing array settles at the high-water mark.
/// </remarks>
public sealed class NetWriter
{
    private byte[] _buffer;
    private int _count;

    public NetWriter(int initialCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _count = 0;
    }

    /// <summary>Bytes written since the last <see cref="Reset"/>.</summary>
    public int Count => _count;

    public bool IsEmpty => _count == 0;

    /// <summary>Everything written so far. Invalidated by any further write.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _count);

    /// <summary>Rewinds to empty. Keeps the backing array.</summary>
    public void Reset() => _count = 0;

    /// <summary>
    /// Rewinds to <paramref name="count"/> bytes. Used to abandon a partially
    /// written message without disturbing the ones before it.
    /// </summary>
    public void Rewind(int count)
    {
        if ((uint)count > (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(count));
        _count = count;
    }

    private Span<byte> Advance(int count)
    {
        EnsureCapacity(_count + count);
        var span = _buffer.AsSpan(_count, count);
        _count += count;
        return span;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
            return;

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, _buffer.Length * 2));
        _buffer.AsSpan(0, _count).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    public void WriteUByte(byte value) => Advance(1)[0] = value;

    public void WriteByte(sbyte value) => Advance(1)[0] = (byte)value;

    public void WriteUShort(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Advance(2), value);

    public void WriteShort(short value) => BinaryPrimitives.WriteInt16LittleEndian(Advance(2), value);

    public void WriteUInt(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Advance(4), value);

    public void WriteInt(int value) => BinaryPrimitives.WriteInt32LittleEndian(Advance(4), value);

    public void WriteMessageType(MessageType type) => WriteUByte((byte)type);

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Advance(value.Length));

    /// <summary>A <c>ubyte</c> length followed by that many raw bytes.</summary>
    public void WriteString1(string value)
    {
        if (value.Length > byte.MaxValue)
            throw new ProtocolException(
                $"string of {value.Length} chars does not fit a 1-byte length prefix");

        WriteUByte((byte)value.Length);
        Gg2Text.EncodeTo(value, Advance(value.Length));
    }

    /// <summary>A <c>ushort</c> length followed by that many raw bytes.</summary>
    public void WriteString2(string value)
    {
        if (value.Length > ushort.MaxValue)
            throw new ProtocolException(
                $"string of {value.Length} chars does not fit a 2-byte length prefix");

        WriteUShort((ushort)value.Length);
        Gg2Text.EncodeTo(value, Advance(value.Length));
    }

    public void WriteString1Bytes(ReadOnlySpan<byte> value)
    {
        if (value.Length > byte.MaxValue)
            throw new ProtocolException(
                $"string of {value.Length} bytes does not fit a 1-byte length prefix");

        WriteUByte((byte)value.Length);
        WriteBytes(value);
    }

    public void WriteString2Bytes(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
            throw new ProtocolException(
                $"string of {value.Length} bytes does not fit a 2-byte length prefix");

        WriteUShort((ushort)value.Length);
        WriteBytes(value);
    }
}
