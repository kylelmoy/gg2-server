using System.Globalization;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// The reward auth servers a game server asks, in order: <c>tcp://host[:port]</c>,
/// or a bare <c>host[:port]</c>, with the port defaulting to
/// <see cref="GameConstants.AuthServerPort"/>.
/// </summary>
/// <remarks>
/// <b>Every entry is asked at once, and the list order is the priority.</b>
/// Each auth server holds its own users, and an empty reply means only that this
/// one does not know the player (the protocol cannot say more,
/// <c>docs/AUTH.md</c>), so the answer used is the first entry's that has
/// rewards, once every entry before it has come back empty or failed. Asking in
/// parallel means a slow or dead server early in the list costs its own timeout
/// once, not a timeout added to every later one. The cost is that every listed
/// server sees every player's reward id. The list says WHERE;
/// <see cref="ServerOptions.Rewards"/> says WHETHER, and is off by default, so
/// a default list contacts nobody.
/// </remarks>
public static class RewardAuthTargets
{
    /// <summary>ganggarrison.com, which issued every stock player's key.</summary>
    public static readonly string Original = $"tcp://{GameConstants.AuthServerHost}:{GameConstants.AuthServerPort}";

    /// <summary>This project's gg2-auth (<c>docs/AUTH.md</c>).</summary>
    public const string Gg2Auth = "tcp://gg2server.net:45022";

    /// <summary>Both, the original first, which is what <c>Rewards=1</c> asks unless told otherwise.</summary>
    public static IReadOnlyList<string> Default { get; } = [Original, Gg2Auth];

    /// <summary>
    /// Splits a comma-separated list and checks every entry.
    /// </summary>
    /// <exception cref="FormatException">An entry is not an auth server; the message names <paramref name="source"/>.</exception>
    public static IReadOnlyList<string> Parse(string source, string text)
    {
        var urls = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var url in urls)
        {
            if (!TryParse(url, out _, out _))
                throw new FormatException($"{source}: '{url}' is not an auth server (tcp://host[:port] or host[:port])");
        }

        return urls;
    }

    /// <summary>Parses one entry into the host and port to connect to.</summary>
    /// <remarks>
    /// A bare IPv6 address needs brackets, <c>[::1]:45022</c>, as in a URL;
    /// otherwise its last group would be read as the port.
    /// </remarks>
    public static bool TryParse(string entry, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var text = entry.Contains("://", StringComparison.Ordinal) ? entry : "tcp://" + entry;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != "tcp"
            || uri.Host.Length == 0
            || uri.AbsolutePath is not ("" or "/")
            || uri.Query.Length > 0
            || uri.UserInfo.Length > 0)
        {
            return false;
        }

        host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        port = uri.IsDefaultPort || uri.Port < 0 ? GameConstants.AuthServerPort : uri.Port;
        return port is > 0 and <= 65535;
    }

    /// <summary><paramref name="entry"/> as host:port, for a log line.</summary>
    public static string Describe(string entry) =>
        TryParse(entry, out var host, out var port)
            ? string.Create(CultureInfo.InvariantCulture, $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}")
            : entry;
}
