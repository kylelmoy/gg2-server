using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GG2.Server;

/// <summary>Where one address resolved to. Every field may be empty.</summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2, uppercase — <c>US</c>, <c>DE</c>.</param>
/// <param name="CountryName">The database's English name for it.</param>
/// <param name="RegionCode">
/// The first subdivision's code without its country prefix — <c>CA</c> for
/// California. Empty unless the database carries subdivisions. For a US state
/// with no code in the database (DB-IP), derived from its name by
/// <see cref="GeoIpDatabase.UsStateCode"/>.
/// </param>
/// <param name="RegionName">That subdivision's English name.</param>
public readonly record struct GeoLocation(
    string CountryCode,
    string CountryName,
    string RegionCode,
    string RegionName)
{
    public static readonly GeoLocation Unknown = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool IsKnown => CountryCode.Length != 0;
}

/// <summary>
/// A reader for the MaxMind DB (<c>.mmdb</c>) file format, which is how every
/// free offline IP-geolocation database ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is written out rather than taken from a package.</b> This
/// project has no NuGet dependencies and the admin panel exists partly to
/// demonstrate that a LAN server needs none — see <c>WebAdminServer</c>, which
/// declined ASP.NET Core for eight endpoints. The format is a metadata map, a
/// binary search tree over the bits of an address, and a small tagged-value
/// encoding; it is about three hundred lines and it does not change.
/// <see href="https://maxmind.github.io/MaxMind-DB/">The specification</see> is
/// versioned and this reads major version 2.
/// </para>
/// <para>
/// <b>No database ships with this repo, deliberately.</b> Nothing freely
/// redistributable is small enough at US-state resolution — the country-level
/// files are several megabytes and the city-level ones are hundreds — and every
/// one of them carries an attribution licence and a monthly rebuild. So the
/// operator supplies the file (<c>--geoip</c>) and the analytics work without
/// one, minus the geography. <c>docs/ANALYTICS.md</c> names the two free
/// sources.
/// </para>
/// <para>
/// <b>Read with <see cref="RandomAccess"/>, not into memory and not mapped.</b>
/// A lookup is one syscall per tree node, and in exchange a 600 MB city
/// database costs the same resident memory as a 2 MB country one. Reading the
/// file in would put the operator's choice of database into this server's
/// working set, which is the wrong thing for a process budgeted in megabytes.
/// </para>
/// <para>
/// ⚠️ <b>That is not free, and the number is worth knowing: 176 us a lookup</b>
/// against DB-IP's 8.3 MB country database, Release, warm cache. It is 0.5% of
/// a 33.3 ms tick and it happens ONCE PER JOIN — bounded above by
/// <c>ConnectionRateLimiter</c>, and small beside the <c>FULL_UPDATE</c> the
/// same join serialises. <b>It must never move onto a path that runs per
/// tick.</b> <c>docs/ANALYTICS.md</c> has the table.
/// </para>
/// <para>
/// ⚠️ <b>Every lookup happens on the tick thread</b>, from
/// <c>GameServer.CompleteJoin</c>. That is allowed for the same reason
/// <c>BanList</c>'s file write is: it is per-operator-action rate, not per-tick.
/// It must never be moved onto a path that runs every tick.
/// </para>
/// </remarks>
public sealed class GeoIpDatabase : IDisposable
{
    /// <summary>What marks the start of the metadata section.</summary>
    private static readonly byte[] Marker =
        [0xAB, 0xCD, 0xEF, .. "MaxMind.com"u8.ToArray()];

    /// <summary>
    /// How far back from the end of the file the marker is looked for.
    /// </summary>
    /// <remarks>
    /// The specification's own bound. A file whose metadata is further back
    /// than this is malformed, and scanning the whole of a 600 MB database to
    /// find that out is the failure mode this avoids.
    /// </remarks>
    private const int MetadataSearchWindow = 128 * 1024;

    /// <summary>The 16 bytes of zero between the search tree and the data section.</summary>
    private const int DataSectionSeparator = 16;

    private readonly SafeFileHandle _handle;
    private readonly long _nodeCount;
    private readonly int _recordSize;
    private readonly int _nodeSize;
    private readonly long _searchTreeSize;
    private readonly long _dataSectionStart;
    private readonly int _ipVersion;

    /// <summary>
    /// The node an IPv4 walk starts at in an IPv6 database — where
    /// <c>::ffff:0:0/96</c> ends.
    /// </summary>
    /// <remarks>
    /// Walked once, at construction, because it is identical for every IPv4
    /// address and is otherwise three quarters of every lookup's syscalls. If
    /// that prefix is itself a leaf — a database with no IPv4 data under it —
    /// this holds the leaf and the per-lookup walk terminates immediately,
    /// which is the right answer rather than a special case.
    /// </remarks>
    private readonly long _ipv4Root;

    private GeoIpDatabase(
        SafeFileHandle handle,
        string path,
        long nodeCount,
        int recordSize,
        int ipVersion,
        string databaseType,
        DateTime builtUtc)
    {
        _handle = handle;
        _nodeCount = nodeCount;
        _recordSize = recordSize;
        _nodeSize = recordSize * 2 / 8;
        _searchTreeSize = nodeCount * _nodeSize;
        _dataSectionStart = _searchTreeSize + DataSectionSeparator;
        _ipVersion = ipVersion;

        Path = path;
        DatabaseType = databaseType;
        BuiltUtc = builtUtc;

        // Last: it walks the tree, so everything above has to be set.
        _ipv4Root = ipVersion == 4 ? 0 : WalkIpv4Prefix();
    }

    /// <summary>Walks <c>::ffff:0:0/96</c> once. See <see cref="_ipv4Root"/>.</summary>
    private long WalkIpv4Prefix()
    {
        Span<byte> prefix = stackalloc byte[16];
        prefix[10] = 0xFF;
        prefix[11] = 0xFF;

        long node = 0;

        for (var i = 0; i < 96 && node < _nodeCount; i++)
        {
            var bit = (prefix[i >> 3] >> (7 - (i & 7))) & 1;
            node = ReadRecord(node, bit);
        }

        return node;
    }

    /// <summary>The file this was opened from.</summary>
    public string Path { get; }

    /// <summary>
    /// The publisher's own name for the database — <c>GeoLite2-Country</c>,
    /// <c>DBIP-City-Lite</c>.
    /// </summary>
    /// <remarks>
    /// Reported to the operator rather than interpreted. <see cref="HasRegions"/>
    /// is the one thing derived from it, and it is derived by asking the data
    /// instead wherever a lookup has already happened.
    /// </remarks>
    public string DatabaseType { get; }

    /// <summary>When the publisher built it. A geo database goes stale.</summary>
    public DateTime BuiltUtc { get; }

    /// <summary>
    /// Whether this database is one that carries subdivisions, so US states can
    /// be resolved.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A guess from the name, and the panel presents it as one.</b> The
    /// format has no field that declares which keys a record has, so the only
    /// alternative would be to look one up and see — and "no subdivision for
    /// this address" is not the same claim as "this database has none". A
    /// country-level database reports United States with no state and the
    /// panel says so.
    /// </remarks>
    public bool HasRegions =>
        DatabaseType.Contains("City", StringComparison.OrdinalIgnoreCase)
        || DatabaseType.Contains("Enterprise", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a database, or returns null and says why in the log.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Never throws, and that is the contract.</b> This is opened at
    /// startup from a path an operator typed. A missing or truncated file must
    /// cost the geography and nothing else — a server that will not start
    /// because a monthly geo database was not re-downloaded is a worse outcome
    /// than a map with no dots on it.
    /// </remarks>
    public static GeoIpDatabase? Open(string path, IServerLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrEmpty(path))
            return null;

        SafeFileHandle? handle = null;

        try
        {
            // FileOptions.RandomAccess describes what this actually does — a
            // scattered walk of 6-8 byte nodes, the opposite of the sequential
            // readahead the cache manager assumes. ⚠️ It measured FLAT here
            // (176.6 us against 176.2), which is expected: the measurement was
            // against an 8 MB file already wholly in the OS cache, the one case
            // where the hint cannot help. Kept because it is the correct
            // descriptor for a 500 MB city database on a cold cache, not
            // because it was shown to be faster.
            handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
            var length = RandomAccess.GetLength(handle);

            var metadata = FindMetadata(handle, length);

            if (metadata < 0)
            {
                log.Warn($"{path} is not a MaxMind DB file (no metadata marker); geography is off");
                handle.Dispose();
                return null;
            }

            var reader = new Decoder(handle, dataSectionStart: metadata);
            var offset = metadata;

            if (!reader.TryReadMap(ref offset, out var fields))
            {
                log.Warn($"{path}: the metadata section is not a map; geography is off");
                handle.Dispose();
                return null;
            }

            var major = (int)fields.UInt("binary_format_major_version", reader, 0);

            if (major != 2)
            {
                log.Warn($"{path}: MaxMind DB format version {major}, and this reads 2; geography is off");
                handle.Dispose();
                return null;
            }

            var nodeCount = (long)fields.UInt("node_count", reader, 0);
            var recordSize = (int)fields.UInt("record_size", reader, 0);
            var ipVersion = (int)fields.UInt("ip_version", reader, 6);
            var type = fields.Text("database_type", reader);
            var epoch = (long)fields.UInt("build_epoch", reader, 0);

            if (nodeCount <= 0 || recordSize is not (24 or 28 or 32))
            {
                log.Warn($"{path}: node_count {nodeCount}, record_size {recordSize}; geography is off");
                handle.Dispose();
                return null;
            }

            var database = new GeoIpDatabase(
                handle, path, nodeCount, recordSize, ipVersion, type,
                DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime);

            if (database._dataSectionStart >= length)
            {
                log.Warn($"{path}: the search tree runs past the end of the file; geography is off");
                database.Dispose();
                return null;
            }

            log.Info(
                $"geoip: {type} built {database.BuiltUtc:yyyy-MM-dd}, "
                + $"{nodeCount:N0} nodes, IPv{ipVersion}"
                + (database.HasRegions ? ", with subdivisions" : ", countries only"));

            return database;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Warn($"could not open the geoip database {path}: {e.Message}; geography is off");
            handle?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Where <paramref name="address"/> is, or <see cref="GeoLocation.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A private or loopback address resolves to nothing, and that is not
    /// a failure.</b> A LAN server's whole roster is on 192.168/16, so the
    /// common case for this method is "no answer" and the panel has to read as
    /// correct in it.
    /// </remarks>
    public GeoLocation Lookup(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            var record = FindRecord(address);

            return record < 0 ? GeoLocation.Unknown : ReadLocation(record);
        }
        catch (Exception e) when (e is IOException or ArgumentException or EndOfStreamException)
        {
            // A truncated or corrupt database costs the geography for this
            // connection, not the connection.
            return GeoLocation.Unknown;
        }
    }

    public void Dispose() => _handle.Dispose();

    /* ---------- the search tree ------------------------------------------ */

    /// <summary>
    /// Walks the tree bit by bit and returns the absolute offset of the record,
    /// or -1 for an address the database has nothing for.
    /// </summary>
    /// <remarks>
    /// <b>An IPv4 address in an IPv6 database is walked as <c>::ffff:a.b.c.d</c></b>,
    /// which is where the format puts IPv4 data and is what makes one tree
    /// answer both families. Walking the four bytes directly against a v6 tree
    /// would resolve every address to whatever sits under <c>0.0.0.0/0</c>'s
    /// first byte — wrong answers rather than no answers, which is worse.
    /// </remarks>
    private long FindRecord(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        int bits;
        long start;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            address.TryWriteBytes(bytes, out _);
            bits = 32;

            // ⚠️ An IPv4 address in an IPv6 database lives under ::ffff:0:0/96,
            // and those 96 bits are the SAME 96 node reads for every address —
            // one syscall each, walked again on every lookup. Measured against
            // DB-IP's 8.3 MB country database: 291 us a lookup with the prefix
            // walked, 66 us with it cached. Starting the walk at the node that
            // prefix ends on is the whole difference, and it is one field.
            start = _ipVersion == 4 ? 0 : _ipv4Root;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // An IPv4-only database has nothing to say about an IPv6 address,
            // and saying so is better than walking a 32-bit tree with 128 bits.
            if (_ipVersion == 4)
                return -1;

            address.TryWriteBytes(bytes, out _);
            bits = 128;
            start = 0;
        }
        else
        {
            return -1;
        }

        var node = start;

        for (var i = 0; i < bits && node < _nodeCount; i++)
        {
            var bit = (bytes[i >> 3] >> (7 - (i & 7))) & 1;
            node = ReadRecord(node, bit);
        }

        // Exactly node_count means "the tree deliberately has nothing here" —
        // the format's own empty value — and anything above it is a data
        // pointer biased by the node count and the separator.
        if (node == _nodeCount)
            return -1;

        return node > _nodeCount ? _searchTreeSize + node - _nodeCount : -1;
    }

    /// <summary>One side of one node.</summary>
    private long ReadRecord(long node, int side)
    {
        Span<byte> raw = stackalloc byte[8];
        var span = raw[.._nodeSize];

        RandomAccess.Read(_handle, span, node * _nodeSize);

        return _recordSize switch
        {
            24 => side == 0
                ? (span[0] << 16) | (span[1] << 8) | span[2]
                : (span[3] << 16) | (span[4] << 8) | span[5],

            // The middle byte is SHARED: its high nibble is the left record's
            // top four bits and its low nibble is the right record's.
            //
            // Every term is widened to long BEFORE the shift. Shifting an int
            // and or-ing it into a long sign-extends bit 31, which for a
            // 28-bit record is unreachable — and would be a wrong answer rather
            // than an exception if it ever were reached.
            28 => side == 0
                ? (((long)span[3] & 0xF0) << 20) | ((long)span[0] << 16) | ((long)span[1] << 8) | span[2]
                : (((long)span[3] & 0x0F) << 24) | ((long)span[4] << 16) | ((long)span[5] << 8) | span[6],

            _ => side == 0
                ? ((long)span[0] << 24) | ((long)span[1] << 16) | ((long)span[2] << 8) | span[3]
                : ((long)span[4] << 24) | ((long)span[5] << 16) | ((long)span[6] << 8) | span[7],
        };
    }

    /* ---------- the record ------------------------------------------------ */

    /// <summary>
    /// Pulls the four fields this project wants out of a GeoIP2-shaped record.
    /// </summary>
    /// <remarks>
    /// <b>Navigated rather than materialised.</b> A city record is a nested map
    /// with a dozen languages in it; decoding the whole thing to read two
    /// strings would allocate several kilobytes per connection. The decoder
    /// walks to a key and skips everything else.
    /// <para>
    /// <c>registered_country</c> is the fallback because it is what an anycast
    /// or satellite address has instead of <c>country</c> — the network's
    /// registration rather than a physical place. It is a weaker claim and the
    /// panel does not distinguish it, which is a deliberate simplification:
    /// this is a "where are my players" chart, not a compliance control.
    /// </para>
    /// </remarks>
    private GeoLocation ReadLocation(long record)
    {
        var reader = new Decoder(_handle, _dataSectionStart);
        var offset = record;

        if (!reader.TryReadMap(ref offset, out var root))
            return GeoLocation.Unknown;

        var country = root.Find("country", reader);

        if (country < 0)
            country = root.Find("registered_country", reader);

        var code = string.Empty;
        var name = string.Empty;

        if (country >= 0)
        {
            var at = country;

            if (reader.TryReadMap(ref at, out var fields))
            {
                code = fields.Text("iso_code", reader).ToUpperInvariant();
                name = EnglishName(reader, fields.Find("names", reader));
            }
        }

        var regionCode = string.Empty;
        var regionName = string.Empty;
        var subdivisions = root.Find("subdivisions", reader);

        if (subdivisions >= 0)
        {
            var at = subdivisions;

            // The first entry is the largest subdivision — the state, with the
            // county after it where a database has both.
            if (reader.TryReadArray(ref at, out var count) && count > 0)
            {
                var first = at;

                if (reader.TryReadMap(ref first, out var fields))
                {
                    regionCode = fields.Text("iso_code", reader).ToUpperInvariant();
                    regionName = EnglishName(reader, fields.Find("names", reader));
                }
            }
        }

        // ⚠️ DB-IP's City Lite names a subdivision and gives it no iso_code, so
        // without this every American lands in "United States, no state" with a
        // city database loaded. Measured against dbip-city-lite 2026-09.
        if (regionCode.Length == 0 && code == "US")
            regionCode = UsStateCode(regionName);

        return code.Length == 0 && regionCode.Length == 0
            ? GeoLocation.Unknown
            : new GeoLocation(code, name, regionCode, regionName);
    }

    /// <summary>
    /// The USPS code for a state's English name, or empty for any other name.
    /// </summary>
    /// <remarks>
    /// The 50 states and DC: exactly the places DB-IP City Lite names inside
    /// the United States, and exactly the US view's markers in
    /// <c>world.json</c>. <c>GeoIpStateTests</c> holds the table to the map.
    /// </remarks>
    public static string UsStateCode(string name) =>
        UsStates.TryGetValue(name, out var code) ? code : string.Empty;

    private static readonly Dictionary<string, string> UsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alabama"] = "AL", ["Alaska"] = "AK", ["Arizona"] = "AZ", ["Arkansas"] = "AR",
        ["California"] = "CA", ["Colorado"] = "CO", ["Connecticut"] = "CT", ["Delaware"] = "DE",
        ["District of Columbia"] = "DC", ["Florida"] = "FL", ["Georgia"] = "GA", ["Hawaii"] = "HI",
        ["Idaho"] = "ID", ["Illinois"] = "IL", ["Indiana"] = "IN", ["Iowa"] = "IA",
        ["Kansas"] = "KS", ["Kentucky"] = "KY", ["Louisiana"] = "LA", ["Maine"] = "ME",
        ["Maryland"] = "MD", ["Massachusetts"] = "MA", ["Michigan"] = "MI", ["Minnesota"] = "MN",
        ["Mississippi"] = "MS", ["Missouri"] = "MO", ["Montana"] = "MT", ["Nebraska"] = "NE",
        ["Nevada"] = "NV", ["New Hampshire"] = "NH", ["New Jersey"] = "NJ", ["New Mexico"] = "NM",
        ["New York"] = "NY", ["North Carolina"] = "NC", ["North Dakota"] = "ND", ["Ohio"] = "OH",
        ["Oklahoma"] = "OK", ["Oregon"] = "OR", ["Pennsylvania"] = "PA", ["Rhode Island"] = "RI",
        ["South Carolina"] = "SC", ["South Dakota"] = "SD", ["Tennessee"] = "TN", ["Texas"] = "TX",
        ["Utah"] = "UT", ["Vermont"] = "VT", ["Virginia"] = "VA", ["Washington"] = "WA",
        ["West Virginia"] = "WV", ["Wisconsin"] = "WI", ["Wyoming"] = "WY",
    };

    private static string EnglishName(in Decoder reader, long namesOffset)
    {
        if (namesOffset < 0)
            return string.Empty;

        var at = namesOffset;

        return reader.TryReadMap(ref at, out var names) ? names.Text("en", reader) : string.Empty;
    }

    /* ---------- metadata -------------------------------------------------- */

    /// <summary>The offset just past the last metadata marker, or -1.</summary>
    private static long FindMetadata(SafeFileHandle handle, long length)
    {
        var window = (int)Math.Min(length, MetadataSearchWindow);
        var buffer = new byte[window];
        var start = length - window;

        if (RandomAccess.Read(handle, buffer, start) < window)
            return -1;

        // Backwards: a database whose DATA happens to contain the marker bytes
        // would otherwise win over the real metadata section.
        for (var i = window - Marker.Length; i >= 0; i--)
        {
            if (buffer.AsSpan(i, Marker.Length).SequenceEqual(Marker))
                return start + i + Marker.Length;
        }

        return -1;
    }

    /* ---------- the value encoding ---------------------------------------- */

    private enum MmdbType
    {
        Extended = 0,
        Pointer = 1,
        Utf8String = 2,
        Double = 3,
        Bytes = 4,
        Uint16 = 5,
        Uint32 = 6,
        Map = 7,
        Int32 = 8,
        Uint64 = 9,
        Uint128 = 10,
        Array = 11,
        DataCacheContainer = 12,
        EndMarker = 13,
        Boolean = 14,
        Float = 15,
    }

    /// <summary>A decoded map: where it starts and how many pairs it has.</summary>
    private readonly record struct MapCursor(long Start, int Count)
    {
        /// <summary>The offset of <paramref name="key"/>'s value, or -1.</summary>
        public long Find(string key, in Decoder reader)
        {
            var at = Start;

            for (var i = 0; i < Count; i++)
            {
                var matched = reader.KeyEquals(ref at, key);

                if (matched)
                    return at;

                reader.Skip(ref at);
            }

            return -1;
        }

        public string Text(string key, in Decoder reader)
        {
            var at = Find(key, reader);
            return at < 0 ? string.Empty : reader.ReadString(at);
        }

        public ulong UInt(string key, in Decoder reader, ulong fallback)
        {
            var at = Find(key, reader);
            return at < 0 ? fallback : reader.ReadUInt(at, fallback);
        }
    }

    /// <summary>
    /// The tagged-value reader. A struct so a lookup allocates nothing but the
    /// two or three strings it returns.
    /// </summary>
    private readonly struct Decoder(SafeFileHandle handle, long dataSectionStart)
    {
        private readonly SafeFileHandle _handle = handle;
        private readonly long _dataStart = dataSectionStart;

        /// <summary>
        /// Reads a control byte: the type, the payload size, and where the
        /// payload starts.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>A pointer's size field is not a size</b> — its low three bits
        /// are part of the pointer's value and the two above them say how many
        /// bytes follow. That is handled by the caller, which is why this
        /// returns the raw size for that type.
        /// </remarks>
        private (MmdbType Type, int Size, long Payload) Control(long offset)
        {
            Span<byte> head = stackalloc byte[5];
            var read = RandomAccess.Read(_handle, head, offset);

            if (read < 1)
                throw new EndOfStreamException("past the end of the database");

            var type = (MmdbType)(head[0] >> 5);
            var at = offset + 1;
            var used = 1;

            if (type == MmdbType.Extended)
            {
                type = (MmdbType)(head[1] + 7);
                at++;
                used = 2;
            }

            var size = head[0] & 0x1F;

            if (type == MmdbType.Pointer)
                return (type, size, at);

            switch (size)
            {
                case 29:
                    size = 29 + head[used];
                    at += 1;
                    break;

                case 30:
                    size = 285 + (head[used] << 8) + head[used + 1];
                    at += 2;
                    break;

                case 31:
                    size = 65821 + (head[used] << 16) + (head[used + 1] << 8) + head[used + 2];
                    at += 3;
                    break;
            }

            return (type, size, at);
        }

        /// <summary>
        /// Follows a pointer to whatever it names, leaving non-pointers alone.
        /// </summary>
        /// <remarks>
        /// Pointers do not chain in a well-formed file, but a single hop is not
        /// assumed: a bounded loop costs nothing and a malformed file that
        /// pointed at a pointer would otherwise be decoded as garbage.
        /// </remarks>
        private long Resolve(long offset)
        {
            // Outside the loop: eight iterations of a four-byte stackalloc is
            // harmless, but the analyser is right in general and one buffer
            // reused is what the loop wants anyway.
            Span<byte> raw = stackalloc byte[4];

            for (var hop = 0; hop < 8; hop++)
            {
                var (type, size, payload) = Control(offset);

                if (type != MmdbType.Pointer)
                    return offset;

                var width = (size >> 3) & 0x3;
                var value = (long)(size & 0x7);

                RandomAccess.Read(_handle, raw[..(width + 1)], payload);

                for (var i = 0; i <= width; i++)
                    value = (value << 8) | raw[i];

                // The four widths carry a bias each, so an 11-bit pointer and a
                // 19-bit one cannot both name the same place.
                value += width switch { 0 => 0, 1 => 2048, 2 => 526336, _ => 0 };

                // A four-byte pointer stores the whole value and ignores the
                // three bits in the control byte.
                if (width == 3)
                {
                    value = 0;

                    for (var i = 0; i < 4; i++)
                        value = (value << 8) | raw[i];
                }

                offset = _dataStart + value;
            }

            return offset;
        }

        /// <summary>Steps <paramref name="offset"/> past one whole value.</summary>
        public void Skip(ref long offset)
        {
            var (type, size, payload) = Control(offset);

            switch (type)
            {
                case MmdbType.Pointer:
                    // The pointer's own bytes, not what it points at.
                    offset = payload + ((size >> 3) & 0x3) + 1;
                    return;

                case MmdbType.Map:
                    offset = payload;

                    for (var i = 0; i < size; i++)
                    {
                        Skip(ref offset);
                        Skip(ref offset);
                    }

                    return;

                case MmdbType.Array:
                    offset = payload;

                    for (var i = 0; i < size; i++)
                        Skip(ref offset);

                    return;

                case MmdbType.Boolean:
                    // The size IS the value; there is no payload.
                    offset = payload;
                    return;

                default:
                    offset = payload + size;
                    return;
            }
        }

        /// <summary>
        /// Reads a map header, following a pointer to it if that is what is
        /// there.
        /// </summary>
        public bool TryReadMap(ref long offset, out MapCursor map)
        {
            var (type, size, payload) = Control(Resolve(offset));

            if (type != MmdbType.Map)
            {
                map = default;
                return false;
            }

            map = new MapCursor(payload, size);
            offset = payload;
            return true;
        }

        /// <summary>Reads an array header, leaving <paramref name="offset"/> on its first element.</summary>
        public bool TryReadArray(ref long offset, out int count)
        {
            var (type, size, payload) = Control(Resolve(offset));

            if (type != MmdbType.Array)
            {
                count = 0;
                return false;
            }

            count = size;
            offset = payload;
            return true;
        }

        /// <summary>
        /// Compares the key at <paramref name="offset"/> and steps past it
        /// either way.
        /// </summary>
        /// <remarks>
        /// <b>Compared as bytes, not as a decoded string.</b> Every key this
        /// looks for is ASCII, so the comparison needs no allocation at all —
        /// and a map with forty keys in it would otherwise allocate forty
        /// strings to find one.
        /// </remarks>
        public bool KeyEquals(ref long offset, string key)
        {
            var resolved = Resolve(offset);
            var (type, size, payload) = Control(resolved);

            Skip(ref offset);

            if (type != MmdbType.Utf8String || size != key.Length || size > 64)
                return false;

            Span<byte> raw = stackalloc byte[64];
            var span = raw[..size];

            if (RandomAccess.Read(_handle, span, payload) < size)
                return false;

            for (var i = 0; i < size; i++)
            {
                if (span[i] != (byte)key[i])
                    return false;
            }

            return true;
        }

        /// <summary>A string value, or empty for anything else.</summary>
        /// <remarks>
        /// Capped at 256 bytes. Every value this reads is a country or
        /// subdivision name; a longer one is a database this does not
        /// understand, and truncating is better than allocating whatever a
        /// malformed length field asked for.
        /// </remarks>
        public string ReadString(long offset)
        {
            var (type, size, payload) = Control(Resolve(offset));

            if (type != MmdbType.Utf8String || size == 0)
                return string.Empty;

            var wanted = Math.Min(size, 256);
            Span<byte> raw = stackalloc byte[256];
            var span = raw[..wanted];
            var read = RandomAccess.Read(_handle, span, payload);

            return read <= 0 ? string.Empty : Encoding.UTF8.GetString(span[..read]);
        }

        /// <summary>An unsigned value of any width, or <paramref name="fallback"/>.</summary>
        public ulong ReadUInt(long offset, ulong fallback)
        {
            var (type, size, payload) = Control(Resolve(offset));

            if (type is not (MmdbType.Uint16 or MmdbType.Uint32 or MmdbType.Uint64) || size > 8)
                return fallback;

            // The format stores an unsigned integer with its leading zero bytes
            // removed, so a size of 0 is a legitimate way to write zero.
            if (size == 0)
                return 0;

            Span<byte> raw = stackalloc byte[8];
            var span = raw[..size];

            if (RandomAccess.Read(_handle, span, payload) < size)
                return fallback;

            ulong value = 0;

            for (var i = 0; i < size; i++)
                value = (value << 8) | span[i];

            return value;
        }
    }

    /// <summary>What the panel is told about the database, as JSON fields.</summary>
    internal string ToJsonFields() =>
        $"\"path\":\"{ControlCommands.Escape(Path)}\","
        + $"\"type\":\"{ControlCommands.Escape(DatabaseType)}\","
        + $"\"built\":\"{BuiltUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\","
        + $"\"regions\":{(HasRegions ? "true" : "false")}";
}
