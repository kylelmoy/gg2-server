using System.Buffers.Text;
using System.Text;

namespace GG2.Lobby;

internal enum RequestMethod : byte
{
    Other,
    Get,
    Head,
    Put,
    Delete,
}

internal enum ParseStatus : byte
{
    Complete,
    Incomplete,
    Bad,
    TooLarge,
}

/// <summary>
/// The head of one HTTP/1.x request: just the parts the lobby routes on.
/// </summary>
/// <remarks>
/// <b>This is not a general HTTP parser and does not try to be.</b> It reads
/// the request line and the five headers the lobby acts on, and every offset
/// is relative to the request's first byte so nothing is copied out of the
/// receive buffer. What it rejects rather than handles: chunked bodies (an
/// announce is a few hundred bytes and its sender knows the length), header
/// continuation lines (obsolete since RFC 7230), and any version but 1.0/1.1.
/// </remarks>
internal struct LobbyRequest
{
    public RequestMethod Method;
    public int PathStart;
    public int PathLength;
    public int HeaderLength;
    public int ContentLength;
    public bool KeepAlive;
    public bool AcceptsGzip;
    public bool Chunked;
    public int IfNoneMatchStart;
    public int IfNoneMatchLength;

    public readonly int TotalLength => HeaderLength + ContentLength;

    public static ParseStatus TryParse(ReadOnlySpan<byte> data, int maxHeader, out LobbyRequest request)
    {
        request = default;

        var end = data.IndexOf("\r\n\r\n"u8);
        if (end < 0)
            return data.Length > maxHeader ? ParseStatus.TooLarge : ParseStatus.Incomplete;
        if (end > maxHeader)
            return ParseStatus.TooLarge;

        request.HeaderLength = end + 4;
        var head = data[..end];

        var lineEnd = head.IndexOf("\r\n"u8);
        if (lineEnd < 0)
            lineEnd = head.Length;

        var line = head[..lineEnd];
        var sp1 = line.IndexOf((byte)' ');
        var sp2 = line.LastIndexOf((byte)' ');
        if (sp1 <= 0 || sp2 <= sp1 + 1)
            return ParseStatus.Bad;

        request.Method = line[..sp1] switch
        {
            var m when m.SequenceEqual("GET"u8) => RequestMethod.Get,
            var m when m.SequenceEqual("HEAD"u8) => RequestMethod.Head,
            var m when m.SequenceEqual("PUT"u8) => RequestMethod.Put,
            var m when m.SequenceEqual("DELETE"u8) => RequestMethod.Delete,
            _ => RequestMethod.Other,
        };

        request.PathStart = sp1 + 1;
        request.PathLength = sp2 - sp1 - 1;

        var version = line[(sp2 + 1)..];
        if (version.SequenceEqual("HTTP/1.1"u8))
            request.KeepAlive = true;
        else if (!version.SequenceEqual("HTTP/1.0"u8))
            return ParseStatus.Bad;

        var at = lineEnd + 2;
        while (at < head.Length)
        {
            var rest = head[at..];
            var next = rest.IndexOf("\r\n"u8);
            var header = next < 0 ? rest : rest[..next];

            var colon = header.IndexOf((byte)':');
            if (colon <= 0)
                return ParseStatus.Bad;

            var name = header[..colon];
            var valueOffset = colon + 1;
            while (valueOffset < header.Length && header[valueOffset] is (byte)' ' or (byte)'\t')
                valueOffset++;
            var value = header[valueOffset..].TrimEnd((byte)' ');

            if (Ascii.EqualsIgnoreCase(name, "content-length"u8))
            {
                if (!Utf8Parser.TryParse(value, out int length, out var used) || used != value.Length || length < 0)
                    return ParseStatus.Bad;
                request.ContentLength = length;
            }
            else if (Ascii.EqualsIgnoreCase(name, "connection"u8))
            {
                if (ContainsIgnoreCase(value, "close"u8))
                    request.KeepAlive = false;
                else if (ContainsIgnoreCase(value, "keep-alive"u8))
                    request.KeepAlive = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "accept-encoding"u8))
            {
                request.AcceptsGzip = ContainsIgnoreCase(value, "gzip"u8);
            }
            else if (Ascii.EqualsIgnoreCase(name, "transfer-encoding"u8))
            {
                request.Chunked = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "if-none-match"u8))
            {
                request.IfNoneMatchStart = at + valueOffset;
                request.IfNoneMatchLength = value.Length;
            }

            if (next < 0)
                break;
            at += next + 2;
        }

        return ParseStatus.Complete;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerNeedle)
    {
        if (haystack.Length > 256)
            return haystack.IndexOf(lowerNeedle) >= 0;

        Span<byte> lowered = stackalloc byte[haystack.Length];
        Ascii.ToLower(haystack, lowered, out _);
        return lowered.IndexOf(lowerNeedle) >= 0;
    }
}
