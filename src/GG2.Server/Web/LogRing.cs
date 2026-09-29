namespace GG2.Server;

/// <summary>
/// The last few hundred log lines, so the web panel can show a scrollback
/// instead of only what happened since the browser connected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locked, unlike everything else the server owns.</b> Almost all logging
/// happens on the tick thread, but not all of it — <see cref="LobbyAnnouncer"/>
/// and <see cref="RewardAuthClient"/> both log from their own — and a panel
/// thread reads while any of them writes. This is the cheapest correct answer
/// and logging is nowhere near a hot path.
/// </para>
/// <para>
/// <b>Sequence numbers, not indices.</b> A browser polls "everything after
/// N"; if it asked by index it would silently skip lines whenever the ring
/// wrapped between two polls, and the lines it lost would be exactly the ones a
/// busy server produced. <see cref="Since"/> reports how many were dropped
/// instead of pretending.
/// </para>
/// </remarks>
public sealed class LogRing
{
    /// <summary>
    /// Lines kept.
    /// </summary>
    /// <remarks>
    /// 500 is about ten minutes of an idle server and about one minute of a busy
    /// one. Larger costs nothing measurable; the reason not to grow it is that
    /// the panel re-renders the whole tail.
    /// </remarks>
    public const int Capacity = 500;

    private readonly Lock _gate = new();
    private readonly Entry[] _entries = new Entry[Capacity];
    private long _next;

    /// <summary>One line and the sequence number it was written at.</summary>
    public readonly record struct Entry(long Sequence, DateTime Utc, string Text);

    /// <summary>The sequence number the next line will take.</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
                return _next;
        }
    }

    public void Add(string text)
    {
        lock (_gate)
        {
            _entries[(int)(_next % Capacity)] = new Entry(_next, DateTime.UtcNow, text);
            _next++;
        }
    }

    /// <summary>
    /// Every line written after <paramref name="after"/>, oldest first.
    /// </summary>
    /// <param name="after">
    /// The caller's last sequence number, or -1 for "everything you still have".
    /// </param>
    /// <param name="dropped">
    /// How many lines fell out of the ring before the caller came back for them.
    /// Reported rather than hidden: a gap the panel does not know about is a
    /// panel quietly showing an incomplete log.
    /// </param>
    public IReadOnlyList<Entry> Since(long after, out long dropped)
    {
        lock (_gate)
        {
            var oldest = Math.Max(0, _next - Capacity);
            var from = after < 0 ? oldest : Math.Max(oldest, after + 1);

            dropped = after < 0 ? 0 : Math.Max(0, oldest - (after + 1));

            var lines = new List<Entry>((int)Math.Min(Capacity, _next - from));

            for (var sequence = from; sequence < _next; sequence++)
                lines.Add(_entries[(int)(sequence % Capacity)]);

            return lines;
        }
    }
}

/// <summary>
/// Writes each line to both of two logs.
/// </summary>
/// <remarks>
/// Exists so that turning the web panel on does not turn the console log off.
/// The console is still the record an operator has when the panel is the thing
/// that has broken.
/// </remarks>
public sealed class TeeLog(IServerLog first, LogRing second) : IServerLog
{
    public void Info(string message)
    {
        first.Info(message);
        second.Add(message);
    }

    // ⚠️ Both levels are forwarded EXPLICITLY rather than left to the interface's
    // defaults. A default would call this type's own Info, which tees correctly
    // — but it would tee the *folded* text, so the console would print
    // "[error] x" instead of reaching its own Error override and its stack
    // trace. Forwarding keeps the inner sink's own rendering.
    public void Warn(string message)
    {
        first.Warn(message);
        second.Add($"[warn] {message}");
    }

    public void Error(string message, Exception? error = null)
    {
        first.Error(message, error);

        // The panel gets the message and the exception's type and text, never
        // the stack trace: the ring is 500 lines and one trace would be a
        // meaningful fraction of the operator's scrollback.
        second.Add(
            $"[error] {message}"
            + (error is null ? string.Empty : $" — {error.GetType().Name}: {error.Message}"));
    }
}
