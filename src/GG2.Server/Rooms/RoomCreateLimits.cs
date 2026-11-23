using System.Net;
using System.Net.Sockets;

namespace GG2.Server.Rooms;

/// <summary>
/// The per-address bounds on <c>POST /rooms</c> (<c>docs/ROOMS.md</c>, "Create
/// limits"): how many live rooms one address may hold, and how many it may
/// create in an hour.
/// </summary>
/// <remarks>
/// <b>This stands in for the device check</b> (step 4), and is weaker in the
/// ways an address is: a phone network puts many people behind one address,
/// and somebody with many addresses gets many allowances. It bounds what one
/// ordinary connection can make the host hold, which is what a public API
/// needs before it has anything better.
/// <para>
/// <b>An IPv6 address counts as its /64</b>, the block one subscriber is
/// usually given, or one machine would have 2^64 allowances. An IPv4 address
/// that arrives mapped into IPv6 counts as the IPv4 address.
/// </para>
/// <para>
/// <b>Only a room that was made counts against the hourly allowance</b>: a
/// refused or malformed request costs the host nothing worth limiting.
/// Used on the host's tick thread only, through <see cref="RoomHost.Invoke{T}"/>,
/// so it needs no lock.
/// </para>
/// </remarks>
public sealed class RoomCreateLimits(int maxLivePerAddress, int maxPerHour, Func<DateTime> utcNow)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Past this many tracked addresses, a create sweeps out the expired ones.</summary>
    private const int SweepAbove = 4096;

    private readonly Dictionary<string, Queue<DateTime>> _created = new(StringComparer.Ordinal);

    public int MaxLivePerAddress { get; } = maxLivePerAddress;

    public int MaxPerHour { get; } = maxPerHour;

    /// <summary>Addresses with a create inside the window, for a test.</summary>
    internal int TrackedAddresses => _created.Count;

    /// <summary>What the limits are keyed by: the address, or an IPv6 address's /64.</summary>
    public static string Key(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// Null when <paramref name="key"/> may create a room now; otherwise why
    /// not, and how many seconds until it may.
    /// </summary>
    /// <param name="live">How many live rooms <paramref name="key"/> already holds.</param>
    public (string Reason, int RetryAfterSeconds)? Refusal(string key, int live)
    {
        if (MaxLivePerAddress > 0 && live >= MaxLivePerAddress)
            return ($"you already have {live} room{(live == 1 ? "" : "s")} open here", 60);

        if (MaxPerHour <= 0 || !_created.TryGetValue(key, out var times))
            return null;

        var now = utcNow();
        Expire(times, now);

        if (times.Count < MaxPerHour)
            return null;

        var wait = times.Peek() + Window - now;
        return ("too many rooms created from this address; try again later", Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
    }

    /// <summary>Counts a room <paramref name="key"/> created.</summary>
    public void Created(string key)
    {
        if (MaxPerHour <= 0)
            return;

        var now = utcNow();

        if (_created.Count > SweepAbove)
            Sweep(now);

        if (!_created.TryGetValue(key, out var times))
            _created[key] = times = new Queue<DateTime>();

        times.Enqueue(now);
    }

    private static void Expire(Queue<DateTime> times, DateTime now)
    {
        while (times.Count > 0 && now - times.Peek() >= Window)
            times.Dequeue();
    }

    private void Sweep(DateTime now)
    {
        List<string>? empty = null;

        foreach (var (key, times) in _created)
        {
            Expire(times, now);

            if (times.Count == 0)
                (empty ??= []).Add(key);
        }

        if (empty is null)
            return;

        foreach (var key in empty)
            _created.Remove(key);
    }
}
