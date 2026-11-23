using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace GG2.Lobby;

/// <summary>Where a listed server is: its address as 128 bits, and its game port.</summary>
/// <remarks>IPv4 is stored mapped into IPv6, so the two families share one key space.</remarks>
internal readonly record struct Endpoint(UInt128 Address, ushort Port)
{
    public static UInt128 Key(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.MapToIPv6().TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }
}

internal enum UpsertResult
{
    Listed,
    Full,
}

/// <summary>
/// The list itself, and the one cached HTTP response that serves it.
/// </summary>
/// <remarks>
/// <b>A GET takes no lock and does no work.</b> The full response — status
/// line, headers and body, plus a gzipped twin and a 304 — is built once per
/// change and handed out as a byte array to send. <see cref="Snapshot.ValidUntil"/>
/// is the earliest moment any entry could expire, so until then the cached
/// bytes are correct by construction and a reader only compares two longs.
/// <para>
/// <b>A re-announce that changes nothing rebuilds nothing.</b> Servers
/// re-announce every 30 seconds whether or not anything moved, so an entry
/// whose new fragment is byte-identical to its old one only has its expiry
/// pushed out. In a steady state the snapshot is rebuilt when a player joins,
/// leaves or a map changes, and at no other time.
/// </para>
/// <para>
/// Expiry is lazy, as in Faucet-Lobby: nothing runs on a timer. Entries are
/// swept when a GET or an announce arrives after <c>_nextExpiry</c>.
/// </para>
/// </remarks>
internal sealed class Registry
{
    private sealed class Entry
    {
        public required UInt128 Id;
        public required Endpoint Endpoint;
        public required byte[] Fragment;
        public required string Label;
        public long ExpiresAt;
    }

    public sealed class Snapshot
    {
        public required byte[] Plain;
        public required byte[] Gzip;
        public required byte[] NotModified;
        public required byte[] ETag;
        public required int Count;
        public long ValidUntil;
    }

    private readonly object _gate = new();
    private readonly Dictionary<UInt128, Entry> _byId = [];
    private readonly Dictionary<Endpoint, Entry> _byEndpoint = [];
    private readonly Dictionary<UInt128, int> _perAddress = [];
    private readonly long _ttlMs;
    private readonly int _maxServers;
    private readonly int _maxPerAddress;
    private readonly Action<string> _log;

    // Distinguishes this process's ETags from the last one's, so a client's
    // cached list from before a restart is not mistaken for this one's.
    private readonly string _epoch = Convert.ToHexString(BitConverter.GetBytes(Environment.TickCount64 ^ Environment.ProcessId))[..8];

    private long _generation;
    private long _nextExpiry = long.MaxValue;
    private volatile Snapshot? _snapshot;

    public Registry(TimeSpan ttl, int maxServers, int maxPerAddress, Action<string> log)
    {
        _ttlMs = (long)ttl.TotalMilliseconds;
        _maxServers = maxServers;
        _maxPerAddress = maxPerAddress;
        _log = log;
    }

    public int TtlSeconds => (int)(_ttlMs / 1000);

    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>
    /// Whether an announce has to prove it can be reached before it is listed.
    /// </summary>
    /// <remarks>
    /// Only a server the lobby already lists at this exact endpoint skips the
    /// probe. A new id, a moved endpoint and an expired entry all probe, so the
    /// steady 30-second re-announce costs no outbound connection.
    /// </remarks>
    public bool NeedsProbe(UInt128 id, Endpoint endpoint, long now)
    {
        lock (_gate)
            return !(_byId.TryGetValue(id, out var e) && e.Endpoint == endpoint && e.ExpiresAt > now);
    }

    /// <summary>Lists, or refreshes, a server.</summary>
    /// <remarks>
    /// <b>An endpoint another id holds is TAKEN, not refused.</b> Faucet-Lobby
    /// refuses it, which keeps a restarted server — fresh id, same port — out
    /// of the list for up to the whole TTL if its unregister was lost. Here the
    /// caller has already probed the port (<see cref="NeedsProbe"/> is always
    /// true for an endpoint this id does not hold), the announce came from that
    /// address over TCP, and only one process can listen on a port, so the
    /// newcomer is the server that is actually there.
    /// </remarks>
    public UpsertResult Upsert(UInt128 id, Endpoint endpoint, byte[] fragment, string label, long now)
    {
        lock (_gate)
        {
            SweepLocked(now);

            if (_byEndpoint.TryGetValue(endpoint, out var squatter) && squatter.Id != id)
            {
                RemoveLocked(squatter);
                _log($"- {squatter.Label} (replaced)");
            }

            if (_byId.TryGetValue(id, out var entry))
            {
                if (entry.Endpoint != endpoint)
                {
                    RemoveLocked(entry);
                    entry = null;
                }
            }

            if (entry is null)
            {
                _perAddress.TryGetValue(endpoint.Address, out var onAddress);
                if (_byId.Count >= _maxServers || onAddress >= _maxPerAddress)
                    return UpsertResult.Full;

                entry = new Entry { Id = id, Endpoint = endpoint, Fragment = fragment, Label = label };
                _byId[id] = entry;
                _byEndpoint[endpoint] = entry;
                _perAddress[endpoint.Address] = onAddress + 1;
                _snapshot = null;
                _log($"+ {label}");
            }
            else if (!entry.Fragment.AsSpan().SequenceEqual(fragment))
            {
                entry.Fragment = fragment;
                entry.Label = label;
                _snapshot = null;
            }

            entry.ExpiresAt = now + _ttlMs;
            if (entry.ExpiresAt < _nextExpiry)
                _nextExpiry = entry.ExpiresAt;

            return UpsertResult.Listed;
        }
    }

    public bool Remove(UInt128 id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var entry))
                return false;

            RemoveLocked(entry);
            _log($"- {entry.Label} (withdrawn)");
            return true;
        }
    }

    /// <summary>The list response, current as of <paramref name="now"/>.</summary>
    public Snapshot GetSnapshot(long now)
    {
        var snapshot = _snapshot;
        if (snapshot is not null && now < Volatile.Read(ref snapshot.ValidUntil))
            return snapshot;

        lock (_gate)
        {
            SweepLocked(now);

            snapshot = _snapshot ??= BuildLocked();
            Volatile.Write(ref snapshot.ValidUntil, _nextExpiry);
            return snapshot;
        }
    }

    private void SweepLocked(long now)
    {
        if (now < _nextExpiry)
            return;

        var next = long.MaxValue;
        List<Entry>? expired = null;

        foreach (var entry in _byId.Values)
        {
            if (entry.ExpiresAt <= now)
                (expired ??= []).Add(entry);
            else if (entry.ExpiresAt < next)
                next = entry.ExpiresAt;
        }

        if (expired is not null)
        {
            foreach (var entry in expired)
            {
                RemoveLocked(entry);
                _log($"- {entry.Label} (expired)");
            }
        }

        _nextExpiry = next;
    }

    private void RemoveLocked(Entry entry)
    {
        _byId.Remove(entry.Id);
        _byEndpoint.Remove(entry.Endpoint);

        var left = _perAddress[entry.Endpoint.Address] - 1;
        if (left == 0)
            _perAddress.Remove(entry.Endpoint.Address);
        else
            _perAddress[entry.Endpoint.Address] = left;

        _snapshot = null;
    }

    private Snapshot BuildLocked()
    {
        // {"servers":[ + fragments + commas between them + ]}
        var bodyLength = 12 + 2 + Math.Max(_byId.Count - 1, 0);
        foreach (var entry in _byId.Values)
            bodyLength += entry.Fragment.Length;

        var body = new byte[bodyLength];
        "{\"servers\":["u8.CopyTo(body);
        var at = 12;

        var first = true;
        foreach (var entry in _byId.Values)
        {
            if (!first)
                body[at++] = (byte)',';
            first = false;
            entry.Fragment.CopyTo(body, at);
            at += entry.Fragment.Length;
        }

        "]}"u8.CopyTo(body.AsSpan(at));

        var etag = Encoding.ASCII.GetBytes($"\"{_epoch}-{++_generation}\"");

        byte[] gzipped;
        using (var compressed = new MemoryStream(body.Length / 3 + 64))
        {
            using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                gzip.Write(body);
            gzipped = compressed.ToArray();
        }

        // A tiny list compresses to MORE bytes than it started with, and then
        // the plain response is the better one to send to everybody.
        var plain = Response(body, etag, gzip: false);
        var zipped = gzipped.Length < body.Length ? Response(gzipped, etag, gzip: true) : plain;

        return new Snapshot
        {
            Plain = plain,
            Gzip = zipped,
            NotModified = Encoding.ASCII.GetBytes(
                "HTTP/1.1 304 Not Modified\r\n" +
                $"ETag: {Encoding.ASCII.GetString(etag)}\r\n" +
                "Cache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\nVary: Accept-Encoding\r\n\r\n"),
            ETag = etag,
            Count = _byId.Count,
        };
    }

    private static byte[] Response(byte[] body, byte[] etag, bool gzip)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            (gzip ? "Content-Encoding: gzip\r\n" : string.Empty) +
            $"Content-Length: {body.Length}\r\n" +
            $"ETag: {Encoding.ASCII.GetString(etag)}\r\n" +
            "Cache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\nVary: Accept-Encoding\r\n\r\n");

        var response = new byte[head.Length + body.Length];
        head.CopyTo(response, 0);
        body.CopyTo(response, head.Length);
        return response;
    }
}
