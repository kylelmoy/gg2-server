using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// The lobby URLs a server announces to: <c>udp://host[:port]</c> for the
/// ganggarrison.com lobby protocol (<see cref="LobbyAnnouncer"/>) and
/// <c>http(s)://…</c> for a gg2-lobby (<see cref="HttpLobbyAnnouncer"/>,
/// <c>docs/LOBBY.md</c>).
/// </summary>
/// <remarks>
/// The list says WHERE; <see cref="ServerOptions.AnnounceToLobby"/> says
/// WHETHER, and is off by default. A default list therefore publishes nothing
/// on its own.
/// </remarks>
public static class LobbyTargets
{
    /// <summary>The original lobby, which the stock client's server browser reads.</summary>
    public static readonly string Original = $"udp://{GameConstants.LobbyServerHost}:{GameConstants.LobbyServerPort}";

    /// <summary>The public gg2-lobby.</summary>
    public const string Gg2Lobby = "http://gg2server.net:29945";

    /// <summary>Both, which is what <c>UseLobby=1</c> announces to unless told otherwise.</summary>
    public static IReadOnlyList<string> Default { get; } = [Original, Gg2Lobby];

    /// <summary>
    /// Splits a comma-separated list and checks every entry.
    /// </summary>
    /// <exception cref="FormatException">An entry is not a lobby URL; the message names <paramref name="source"/>.</exception>
    public static IReadOnlyList<string> Parse(string source, string text)
    {
        var urls = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var url in urls)
        {
            if (!TryParse(url, out _))
                throw new FormatException($"{source}: '{url}' is not a lobby URL (udp://host[:port] or http(s)://host[:port])");
        }

        return urls;
    }

    /// <summary>
    /// Parses one entry. A <c>udp://</c> URL with no port gets
    /// <see cref="GameConstants.LobbyServerPort"/>.
    /// </summary>
    public static bool TryParse(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0)
        {
            if (parsed.Scheme is "http" or "https")
            {
                uri = parsed;
                return true;
            }

            if (parsed.Scheme == "udp" && parsed.AbsolutePath is "" or "/")
            {
                uri = parsed.Port > 0 ? parsed : new UriBuilder(parsed) { Port = GameConstants.LobbyServerPort }.Uri;
                return true;
            }
        }

        uri = null!;
        return false;
    }
}
