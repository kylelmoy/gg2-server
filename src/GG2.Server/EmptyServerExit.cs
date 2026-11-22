namespace GG2.Server;

/// <summary>
/// Decides when a server with <see cref="ServerOptions.ExitWhenEmptySeconds"/>
/// set has gone long enough without a person on it to stop. This is what makes
/// a private room clean up after itself (<c>docs/ROOMS.md</c>).
/// </summary>
/// <remarks>
/// <b>The clock starts at construction</b>, so a room nobody joins exits after
/// the same interval as one everybody left. One knob is used for both on
/// purpose: the dispatcher hands out the address the moment the process is
/// up, so its creator is seconds away, not minutes.
/// <para>
/// <b>A person is a connection, not a roster entry.</b> Bots never count — a
/// room of bots with nobody watching has nobody to serve. A connection still
/// part-way through its handshake counts, so a joiner arriving at the last
/// second is never cut off. A spectator counts too.
/// </para>
/// <para>
/// <b>Wall clock, not ticks.</b> An empty server with no bots idles at 2 Hz,
/// so a tick count would stretch the interval fifteenfold.
/// </para>
/// <para>
/// A pure function of the times it is given, so tests need no sleeping.
/// </para>
/// </remarks>
internal sealed class EmptyServerExit
{
    private readonly long _limitMs;
    private long _lastOccupiedMs;

    public EmptyServerExit(int seconds, long nowMilliseconds)
    {
        _limitMs = seconds * 1000L;
        _lastOccupiedMs = nowMilliseconds;
    }

    /// <summary>How long the server may stay empty, in seconds.</summary>
    public int Seconds => (int)(_limitMs / 1000);

    /// <summary>
    /// Records whether anybody is on the server now, and says whether it has
    /// been empty for the whole interval.
    /// </summary>
    public bool ShouldExit(long nowMilliseconds, bool occupied)
    {
        if (occupied)
        {
            _lastOccupiedMs = nowMilliseconds;
            return false;
        }

        return nowMilliseconds - _lastOccupiedMs >= _limitMs;
    }
}
