namespace GG2.Protocol;

/// <summary>
/// A grow-able FIFO of received bytes that hands out spans without copying.
/// </summary>
/// <remarks>
/// This is the port's stand-in for GM8's socket receive buffer, and it exists
/// because both readers on a connection ask the same question the reference's
/// <c>tcp_receive(socket, n)</c> asks: "are n bytes here yet, and if so give me
/// them". The handshake state machine wants raw counts; the in-game command
/// framer wants the same thing one command at a time.
/// </remarks>
public sealed class ReceiveBuffer
{
    private byte[] _buffer;
    private int _start;
    private int _end;

    public ReceiveBuffer(int initialCapacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = new byte[initialCapacity];
    }

    /// <summary>Bytes received but not yet consumed.</summary>
    public int Count => _end - _start;

    public bool IsEmpty => Count == 0;

    /// <summary>Adds freshly received bytes.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        EnsureRoom(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_end));
        _end += bytes.Length;
    }

    /// <summary>
    /// Consumes <paramref name="count"/> bytes if they are all here.
    /// </summary>
    /// <remarks>
    /// The returned span points into this buffer and is invalidated by the next
    /// <see cref="Append"/> or <see cref="TryTake"/>. Copy anything that must
    /// outlive the call. A <paramref name="count"/> of zero always succeeds with
    /// an empty span — which is how a zero-payload command completes.
    /// </remarks>
    public bool TryTake(int count, out ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (Count < count)
        {
            bytes = default;
            return false;
        }

        bytes = _buffer.AsSpan(_start, count);
        _start += count;
        if (_start == _end)
            _start = _end = 0;

        return true;
    }

    /// <summary>The next byte without consuming it. Only valid when not empty.</summary>
    public byte Peek() => _buffer[_start];

    /// <summary>
    /// Everything buffered, without consuming any of it.
    /// </summary>
    /// <remarks>
    /// For readers whose message length is not known until the message has been
    /// parsed — a state update's width depends on bytes inside it — the only
    /// way to frame one is to try, and to consume nothing if it turns out to be
    /// truncated. Parse this span, then <see cref="Consume"/> what the parse
    /// used. Invalidated by any mutation.
    /// </remarks>
    public ReadOnlySpan<byte> Buffered => _buffer.AsSpan(_start, Count);

    /// <summary>Drops <paramref name="count"/> bytes from the front.</summary>
    public void Consume(int count)
    {
        if ((uint)count > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(count));

        _start += count;
        if (_start == _end)
            _start = _end = 0;
    }

    /// <summary>Discards everything buffered.</summary>
    public void Clear() => _start = _end = 0;

    private void EnsureRoom(int count)
    {
        if (_end + count <= _buffer.Length)
            return;

        // Slide before growing: a long-lived connection would otherwise walk the
        // write cursor off the end of a buffer that is mostly consumed.
        if (_start > 0)
        {
            _buffer.AsSpan(_start, Count).CopyTo(_buffer);
            _end -= _start;
            _start = 0;
        }

        if (_end + count <= _buffer.Length)
            return;

        Array.Resize(ref _buffer, Math.Max(_end + count, _buffer.Length * 2));
    }
}
