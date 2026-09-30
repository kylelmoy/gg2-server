using System.Buffers;
using System.Text;

namespace GG2.Server;

/// <summary>
/// Just enough HTTP/1.1 to serve one small admin page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than <c>HttpListener</c>.</b> On Windows,
/// <c>HttpListener</c> can only bind a prefix other than <c>localhost</c> if an
/// administrator has added a URL ACL with <c>netsh http add urlacl</c> — so the
/// one thing this feature is for, reaching the panel from another machine, would
/// have needed an elevated command nobody expects to run to host a web page.
/// ASP.NET Core would solve it and would be the project's first NuGet
/// dependency, for a page with four endpoints on it.
/// </para>
/// <para>
/// <b>It is a subset, deliberately, and the omissions are the security
/// argument.</b> No chunked request bodies, no pipelining, no ranges, no
/// upgrades, no multipart. Everything it accepts is bounded before it is
/// allocated: <see cref="MaxHeaderBytes"/> for the head,
/// <see cref="MaxBodyBytes"/> for the body, <see cref="MaxHeaderCount"/> header
/// lines. A parser that grows a buffer until the client stops sending is the
/// classic way a hand-written HTTP server becomes a denial of service, and every
/// limit here exists so that this one cannot.
/// </para>
/// </remarks>
internal sealed class HttpRequest
{
    /// <summary>The request line plus all headers. 16 KB is generous for this page.</summary>
    public const int MaxHeaderBytes = 16 * 1024;

    /// <summary>A body. Nothing the panel posts is larger than a few hundred bytes.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public const int MaxHeaderCount = 64;

    public required string Method { get; init; }

    /// <summary>The path, percent-decoded, without the query string.</summary>
    public required string Path { get; init; }

    public required Dictionary<string, string> Query { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public required string Body { get; init; }

    /// <summary>Whether the client asked to keep the connection open.</summary>
    public bool KeepAlive =>
        !string.Equals(Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.GetValueOrDefault(name);

    /// <summary>One cookie's value, or null.</summary>
    /// <remarks>
    /// Hand-split rather than parsed with <c>System.Net.Cookie</c>, which
    /// wants a container and a URI. A request header is a <c>;</c>-separated list
    /// of <c>name=value</c> and nothing else is legal in it.
    /// </remarks>
    public string? Cookie(string name)
    {
        var header = Header("Cookie");

        if (header is null)
            return null;

        foreach (var part in header.Split(';', StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0 && part.AsSpan(0, equals).SequenceEqual(name))
                return part[(equals + 1)..];
        }

        return null;
    }

    public string? Q(string name) => Query.GetValueOrDefault(name);

    /// <summary>
    /// Reads one request, or null when the connection ended or broke a limit.
    /// </summary>
    /// <remarks>
    /// <b>Null for "stop", never an exception.</b> A closed connection is the
    /// normal end of every keep-alive session and a browser closes them all the
    /// time, so it must not be logged as a fault or the log fills with them.
    /// </remarks>
    public static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);

        try
        {
            var filled = 0;
            var headEnd = -1;

            while (headEnd < 0)
            {
                if (filled == MaxHeaderBytes)
                    return null;

                var read = await stream.ReadAsync(buffer.AsMemory(filled, MaxHeaderBytes - filled), token);

                if (read <= 0)
                    return null;

                // Search from three bytes back, not from zero: the terminator
                // can straddle two reads, and rescanning the whole buffer every
                // time is quadratic on a slow client — which is a client that can
                // choose to be slow.
                var from = Math.Max(0, filled - 3);
                filled += read;
                headEnd = Find(buffer.AsSpan(0, filled), from);
            }

            var head = Encoding.UTF8.GetString(buffer, 0, headEnd);
            var lines = head.Split("\r\n");
            var request = lines[0].Split(' ');

            if (request.Length != 3 || lines.Length > MaxHeaderCount)
                return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':', StringComparison.Ordinal);

                if (colon > 0)
                    headers[lines[i][..colon]] = lines[i][(colon + 1)..].Trim();
            }

            // Refused rather than ignored. A body this parser does not
            // understand, silently treated as absent, is a request smuggling
            // primitive: the next read would start mid-body and be parsed as a
            // fresh request line.
            if (headers.ContainsKey("Transfer-Encoding"))
                return null;

            var length = 0;

            if (headers.TryGetValue("Content-Length", out var text)
                && (!int.TryParse(text, out length) || length is < 0 or > MaxBodyBytes))
            {
                return null;
            }

            var bodyStart = headEnd + 4;
            var body = await ReadBodyAsync(stream, buffer, bodyStart, filled, length, token);

            if (body is null)
                return null;

            var (path, query) = SplitTarget(request[1]);

            return new HttpRequest
            {
                Method = request[0],
                Path = path,
                Query = query,
                Headers = headers,
                Body = body,
            };
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string?> ReadBodyAsync(
        Stream stream,
        byte[] buffer,
        int start,
        int filled,
        int length,
        CancellationToken token)
    {
        if (length == 0)
            return string.Empty;

        var body = new byte[length];
        var have = Math.Min(length, filled - start);

        buffer.AsSpan(start, have).CopyTo(body);

        while (have < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(have, length - have), token);

            if (read <= 0)
                return null;

            have += read;
        }

        return Encoding.UTF8.GetString(body);
    }

    private static int Find(ReadOnlySpan<byte> bytes, int from)
    {
        for (var i = from; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                return i;
        }

        return -1;
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var mark = target.IndexOf('?', StringComparison.Ordinal);
        var path = mark < 0 ? target : target[..mark];

        if (mark >= 0)
        {
            foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);

                query[Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals])] =
                    equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return (Uri.UnescapeDataString(path), query);
    }
}

/// <summary>Writes replies. See <see cref="HttpRequest"/> for why this is by hand.</summary>
internal static class HttpResponse
{
    /// <summary>
    /// Headers every reply carries.
    /// </summary>
    /// <remarks>
    /// <b>The CSP is why the page's CSS and JavaScript are separate routes
    /// rather than inline.</b> <c>default-src 'self'</c> with no
    /// <c>unsafe-inline</c> means a reflected string can never become script,
    /// which for a panel that renders player-chosen names into the DOM is worth
    /// the two extra files. <c>frame-ancestors 'none'</c> is the modern
    /// <c>X-Frame-Options</c>, and both are sent because both are still read.
    /// </remarks>
    private const string SecurityHeaders =
        "X-Content-Type-Options: nosniff\r\n"
        + "X-Frame-Options: DENY\r\n"
        + "Referrer-Policy: no-referrer\r\n"
        + "Content-Security-Policy: default-src 'self'; img-src 'self' data:; "
        + "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n";

    public static Task TextAsync(
        Stream stream,
        int status,
        string contentType,
        string body,
        CancellationToken token,
        string extra = "",
        bool keepAlive = true) =>
        WriteAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), token, extra, keepAlive);

    public static async Task WriteAsync(
        Stream stream,
        int status,
        string contentType,
        ReadOnlyMemory<byte> body,
        CancellationToken token,
        string extra = "",
        bool keepAlive = true)
    {
        var head = new StringBuilder(512);

        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        head.Append(SecurityHeaders);
        head.Append(extra);
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    /// <summary>Opens a server-sent-events stream. The caller writes the events.</summary>
    public static async Task StartEventStreamAsync(Stream stream, CancellationToken token)
    {
        var head =
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: text/event-stream; charset=utf-8\r\n"
            + "Cache-Control: no-store\r\n"

            // Nginx buffers a proxied response by default, which for an event
            // stream means the panel updates in bursts minutes apart and looks
            // broken. Meaningless when nothing is in front of the server, and
            // free.
            + "X-Accel-Buffering: no\r\n"
            + "Connection: keep-alive\r\n"
            + SecurityHeaders
            + "\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head), token);
        await stream.FlushAsync(token);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Status",
    };
}
