namespace GG2.Server;

/// <summary>Why a server stopped itself.</summary>
public enum SelfExitReason
{
    /// <summary>It has not.</summary>
    None,

    /// <summary><see cref="ServerOptions.ExitWhenEmptySeconds"/> ran out.</summary>
    Empty,

    /// <summary><see cref="ServerOptions.MaxLifetimeMinutes"/> ran out.</summary>
    Lifetime,
}

/// <summary>
/// Decides when a temporary server stops itself: after
/// <see cref="ServerOptions.ExitWhenEmptySeconds"/> with nobody on it, or
/// after <see cref="ServerOptions.MaxLifetimeMinutes"/> whatever is happening.
/// This is what makes a private room clean up after itself
/// (<c>docs/ROOMS.md</c>).
/// </summary>
/// <remarks>
/// <b>The empty clock starts at construction</b>, so a room nobody joins exits
/// after the same interval as one everybody left. One knob is used for both on
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
/// ⛔ <b>The lifetime is here and not systemd's <c>RuntimeMaxSec</c></b>:
/// systemd counts a <c>RuntimeMaxSec</c> stop as a timeout, and
/// <c>Restart=on-failure</c> restarts a timeout — measured on systemd 255, two
/// restarts in seven seconds against a two-second limit. A room would come back
/// empty every time its lifetime ran out. Exiting 0 from inside is the one ending
/// that unit leaves alone.
/// </para>
/// <para>
/// A pure function of the times it is given, so tests need no sleeping.
/// </para>
/// </remarks>
internal sealed class EmptyServerExit
{
    private readonly long _emptyLimitMs;
    private readonly long _lifetimeEndMs;
    private long _lastOccupiedMs;

    /// <param name="emptySeconds">Zero for never.</param>
    /// <param name="lifetimeMinutes">Zero for never.</param>
    /// <param name="nowMilliseconds">The server's start.</param>
    public EmptyServerExit(int emptySeconds, int lifetimeMinutes, long nowMilliseconds)
    {
        _emptyLimitMs = emptySeconds * 1000L;
        _lifetimeEndMs = lifetimeMinutes > 0 ? nowMilliseconds + lifetimeMinutes * 60_000L : long.MaxValue;
        _lastOccupiedMs = nowMilliseconds;
    }

    /// <summary>How long the server may stay empty, in seconds; zero for for ever.</summary>
    public int EmptySeconds => (int)(_emptyLimitMs / 1000);

    /// <summary>
    /// Records whether anybody is on the server now, and says whether it should
    /// stop, and why.
    /// </summary>
    public SelfExitReason Check(long nowMilliseconds, bool occupied)
    {
        if (nowMilliseconds >= _lifetimeEndMs)
            return SelfExitReason.Lifetime;

        if (occupied)
        {
            _lastOccupiedMs = nowMilliseconds;
            return SelfExitReason.None;
        }

        return _emptyLimitMs > 0 && nowMilliseconds - _lastOccupiedMs >= _emptyLimitMs
            ? SelfExitReason.Empty
            : SelfExitReason.None;
    }
}
