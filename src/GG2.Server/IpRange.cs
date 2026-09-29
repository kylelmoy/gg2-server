using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace GG2.Server;

/// <summary>
/// One address, or a CIDR block of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A block and not just an address, because a single address is rarely the
/// unit an operator wants.</b> Somebody reconnecting from a phone moves inside
/// a /24 or smaller between attempts, and the alternative to banning a block is
/// banning the same person twelve times. The cost is that a block is a blunt
/// instrument — see <see cref="BanList"/> on why that is the operator's call to
/// make and not something to do automatically.
/// </para>
/// <para>
/// <b>Both families, even though the server binds IPv4.</b>
/// <c>TcpListener(IPAddress.Any)</c> is IPv4-only today, so every
/// <c>RemoteAddress</c> is an IPv4 one — but a ban file is written by hand and
/// outlives that decision, and a parser that rejected an IPv6 entry would make
/// the file wrong the day the bind changes rather than the day it is written.
/// </para>
/// </remarks>
public readonly struct IpRange : IEquatable<IpRange>
{
    private readonly byte[] _address;
    private readonly int _prefixBits;

    private IpRange(byte[] address, int prefixBits)
    {
        _address = address;
        _prefixBits = prefixBits;
    }

    /// <summary>Whether this range was parsed from anything at all.</summary>
    public bool IsEmpty => _address is null;

    /// <summary>Whether it names exactly one address.</summary>
    public bool IsSingleAddress => _address is not null && _prefixBits == _address.Length * 8;

    /// <summary>
    /// Parses <c>1.2.3.4</c> or <c>1.2.3.0/24</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A prefix that does not cover the whole address is NOT an error and
    /// the host bits are NOT required to be zero.</b> An operator typing
    /// <c>1.2.3.4/24</c> means "the /24 that address is in", and refusing it
    /// over a pedantry about host bits is how a ban does not get applied during
    /// the incident it was needed for. The bits below the prefix are masked off
    /// here so that <c>1.2.3.4/24</c> and <c>1.2.3.0/24</c> are the same range
    /// and cannot both sit in the list.
    /// </remarks>
    public static bool TryParse(string? text, out IpRange range)
    {
        range = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? text : text[..slash];

        if (!IPAddress.TryParse(addressText, out var address))
            return false;

        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;

        var bytes = address.GetAddressBytes();
        var full = bytes.Length * 8;
        var prefix = full;

        if (slash >= 0
            && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > full))
        {
            return false;
        }

        Mask(bytes, prefix);
        range = new IpRange(bytes, prefix);
        return true;
    }

    /// <summary>Whether <paramref name="address"/> falls inside this range.</summary>
    public bool Contains(IPAddress address)
    {
        if (_address is null || address is null)
            return false;

        var other = address.GetAddressBytes();

        // Different families never match. Mapping an IPv4-in-IPv6 address down
        // is deliberately NOT done: it would make one text entry match two
        // different things, and an operator reading the list back could not tell
        // which of them a given connection had hit.
        if (other.Length != _address.Length)
            return false;

        var whole = _prefixBits / 8;

        for (var i = 0; i < whole; i++)
        {
            if (other[i] != _address[i])
                return false;
        }

        var spare = _prefixBits % 8;

        if (spare == 0)
            return true;

        var mask = (byte)(0xFF << (8 - spare));
        return (other[whole] & mask) == _address[whole];
    }

    private static void Mask(byte[] bytes, int prefixBits)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var bit = i * 8;

            if (bit >= prefixBits)
            {
                bytes[i] = 0;
                continue;
            }

            var spare = prefixBits - bit;

            if (spare < 8)
                bytes[i] &= (byte)(0xFF << (8 - spare));
        }
    }

    public bool Equals(IpRange other)
    {
        if (_address is null || other._address is null)
            return _address is null && other._address is null;

        return _prefixBits == other._prefixBits && _address.AsSpan().SequenceEqual(other._address);
    }

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is IpRange other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_prefixBits);

        if (_address is not null)
        {
            foreach (var b in _address)
                hash.Add(b);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// The canonical text: an address, or <c>address/prefix</c> for a block.
    /// </summary>
    /// <remarks>
    /// Round-trips through <see cref="TryParse"/>, which is what the ban file
    /// depends on — it is written with this and read with that.
    /// </remarks>
    public override string ToString()
    {
        if (_address is null)
            return string.Empty;

        var address = new IPAddress(_address).ToString();
        return IsSingleAddress ? address : $"{address}/{_prefixBits}";
    }

    public static bool operator ==(IpRange left, IpRange right) => left.Equals(right);

    public static bool operator !=(IpRange left, IpRange right) => !left.Equals(right);
}
