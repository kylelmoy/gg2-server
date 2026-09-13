using System.Security.Cryptography;
using System.Text;

namespace Gg2.Server;

/// <summary>
/// One configuration for several servers: a shared <c>common.ini</c>, one small
/// overlay per host, and the <c>gg2.ini</c> each host is sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout</b>, under a directory that is normally <c>deploy/config</c>:
/// <code>
/// common.ini              every setting the servers share
/// hosts/&lt;dest&gt;.ini       what one host does differently — its name, its message
/// </code>
/// <c>&lt;dest&gt;</c> is the ssh destination exactly as <c>deploy/hosts</c> names it,
/// passed through <see cref="OverlayFileName"/>. <c>scripts/deploy.sh</c> applies
/// the same mapping in bash; <c>FleetConfigTests</c> pins the two together.
/// </para>
/// <para>
/// <b>A rendered file is the two files one after the other, and nothing
/// cleverer.</b> The reader keeps the LAST value of a repeated key and merges a
/// repeated section into the first (<see cref="ServerConfigFile.Parse"/>), so the
/// concatenation reads exactly as "the overlay wins". A merge that rewrote keys
/// would be a second implementation of the reader's rules, and would get the
/// rotation's precedence or the empty-<c>Map</c> rule subtly different from the
/// one the server applies. It also keeps the host's file readable: an operator
/// who opens it over ssh sees the shared block, then the host's own block.
/// </para>
/// <para>
/// ⚠️ <b>The rendered file is validated here, on the machine that renders it</b>,
/// with the same <see cref="ServerOptions.Validate"/> the server runs at start.
/// A value that would stop a server coming back up is refused before any host
/// is contacted.
/// </para>
/// <para>
/// ⚠️ <b><c>[Server] MapRotation</c> is refused.</b> It names a file beside the
/// config, and only the config is pushed; on the host the file would not exist
/// and the reference's silent fallback to <c>[Maps]</c> would play a rotation
/// nobody wrote. <c>[Server] Rotation</c> carries the list inline.
/// </para>
/// </remarks>
public static class FleetConfig
{
    /// <summary>The shared file's name inside the config directory.</summary>
    public const string CommonFileName = "common.ini";

    /// <summary>The overlays' directory inside the config directory.</summary>
    public const string HostsDirectoryName = "hosts";

    /// <summary>
    /// Appended to a config file's path to name its push marker —
    /// <c>gg2.ini.pushed</c>. It holds the SHA-256 of the file as last pushed.
    /// </summary>
    /// <remarks>
    /// Written only by <c>scripts/deploy.sh --config</c>. A file whose hash no
    /// longer matches was edited on the host since — from the admin panel or by
    /// hand — and the next push stops for it rather than silently discarding the
    /// edit.
    /// </remarks>
    public const string PushedSuffix = ".pushed";

    /// <summary>
    /// Appended to a config file's path to name the hash the RUNNING server
    /// started from — <c>gg2.ini.applied</c>.
    /// </summary>
    /// <remarks>
    /// Written by the server at start, and only beside a file that has a push
    /// marker, so a developer's own <c>--config</c> never grows one. It is how
    /// <c>deploy.sh --status</c> tells "pushed and in force" from "pushed and
    /// waiting for the server to empty" without a port into the server.
    /// </remarks>
    public const string AppliedSuffix = ".applied";

    /// <summary>
    /// The ssh destinations in a <c>deploy/hosts</c> file, in order.
    /// </summary>
    /// <remarks>
    /// The same rule as <c>deploy.sh</c>'s loop: everything from <c>#</c> to the
    /// end of the line is a comment, and ALL whitespace is removed, not only the
    /// ends.
    /// </remarks>
    public static IReadOnlyList<string> ReadHosts(string path)
    {
        var hosts = new List<string>();

        foreach (var raw in File.ReadLines(path))
        {
            var hash = raw.IndexOf('#', StringComparison.Ordinal);
            var line = hash >= 0 ? raw[..hash] : raw;
            var destination = new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray());

            if (destination.Length != 0)
                hosts.Add(destination);
        }

        return hosts;
    }

    /// <summary>
    /// An ssh destination as a file name: every character outside
    /// <c>[A-Za-z0-9._@-]</c> becomes <c>_</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Duplicated in <c>scripts/deploy.sh</c></b> as a <c>sed</c>
    /// expression. <c>kyle@203.0.113.7</c> passes through unchanged; a
    /// <c>host:2222</c> or an IPv6 literal does not, and the two sides must agree
    /// on what it becomes.
    /// </remarks>
    public static string OverlayFileName(string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);

        var name = new StringBuilder(destination.Length + 4);

        foreach (var c in destination)
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '@' or '-' ? c : '_');

        return name.Append(".ini").ToString();
    }

    public static string CommonPath(string directory) => Path.Combine(directory, CommonFileName);

    public static string OverlayPath(string directory, string destination) =>
        Path.Combine(directory, HostsDirectoryName, OverlayFileName(destination));

    /// <summary>
    /// The <c>gg2.ini</c> one host is sent: <c>common.ini</c>, then the host's
    /// overlay, validated.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no <c>common.ini</c>.</exception>
    /// <exception cref="FormatException">
    /// A value the server would refuse, or a <c>[Server] MapRotation</c>. The
    /// message names the host.
    /// </exception>
    /// <remarks>
    /// A host with no overlay renders as <c>common.ini</c> alone — legal, and
    /// what <see cref="Warnings"/> reports, because three servers all called the
    /// same thing in the lobby is almost never what was meant.
    /// </remarks>
    public static string Render(string directory, string destination)
    {
        var common = CommonPath(directory);

        if (!File.Exists(common))
            throw new FileNotFoundException($"no {CommonFileName} in {Path.GetFullPath(directory)}", common);

        var overlay = OverlayPath(directory, destination);

        return Render(
            File.ReadAllLines(common),
            File.Exists(overlay) ? File.ReadAllLines(overlay) : null,
            destination);
    }

    /// <summary><see cref="Render(string, string)"/> over files already in memory.</summary>
    public static string Render(string[] common, string[]? overlay, string destination)
    {
        var text = new StringBuilder(4096);

        text.Append("; Rendered from ").Append(CommonFileName).Append(" and ")
            .Append(HostsDirectoryName).Append('/').Append(OverlayFileName(destination))
            .Append(" for ").Append(destination).Append(".\n");
        text.Append("; Managed by scripts/deploy.sh --config. An edit here, or a save from the admin\n");
        text.Append("; panel, is kept - but the next push stops for this server until it is forced.\n");
        text.Append("; Where a key appears twice, the LAST one is the one in force.\n\n");

        AppendLines(text, common);

        if (overlay is not null)
        {
            text.Append("\n; ---- ").Append(destination).Append(" only - these win over everything above ----\n\n");
            AppendLines(text, overlay);
        }

        var rendered = text.ToString();
        Check(rendered, destination);
        return rendered;
    }

    /// <summary>Things about a host's rendered configuration worth saying before pushing it.</summary>
    public static IReadOnlyList<string> Warnings(string directory, IReadOnlyList<string> destinations)
    {
        var warnings = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var destination in destinations)
        {
            if (!File.Exists(OverlayPath(directory, destination)))
            {
                warnings.Add(
                    $"{destination} has no {HostsDirectoryName}/{OverlayFileName(destination)}, "
                    + $"so it gets {CommonFileName} unchanged");
            }

            var options = Load(Render(directory, destination), directory);
            var name = options.ServerName;

            if (names.TryGetValue(name, out var other))
                warnings.Add($"{destination} and {other} are both named \"{name}\" in the server browser");
            else
                names[name] = destination;

            // ⛔ Before the push, not after the restart. This is the deploy path;
            // the server's own copy of these lines lands in a log on a machine
            // nobody is watching at the moment the configuration reaches it.
            foreach (var warning in ConfigWarnings.For(options))
                warnings.Add($"{destination}: {warning}");
        }

        return warnings;
    }

    /// <summary>The options a rendered file produces, as the server would read it.</summary>
    public static ServerOptions Load(string rendered, string directory)
    {
        var options = ServerOptions.CreateDefault();
        ServerConfigFile.ApplyLines(options, SplitLines(rendered), CommonPath(directory));
        return options;
    }

    /// <summary>Lower-case hex SHA-256 — what <c>sha256sum</c> prints.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The bytes a rendered file is written as: UTF-8, no BOM, LF.</summary>
    /// <remarks>
    /// ⚠️ Fixed rather than left to the platform. Rendering on Windows would
    /// otherwise push CRLF to a Linux host, and the hash the server compares
    /// against the marker is over these exact bytes.
    /// </remarks>
    public static byte[] Encode(string rendered) => new UTF8Encoding(false).GetBytes(rendered);

    /// <summary>The overlay's own lines — used by the editor to show what a host overrides.</summary>
    public static Dictionary<string, Dictionary<string, string>> ReadOverlay(string directory, string destination)
    {
        var path = OverlayPath(directory, destination);
        return File.Exists(path)
            ? ServerConfigFile.Parse(File.ReadAllLines(path))
            : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Check(string rendered, string destination)
    {
        var ini = ServerConfigFile.Parse(SplitLines(rendered));

        if (ini.TryGetValue("Server", out var server)
            && server.TryGetValue("MapRotation", out var file) && file.Length != 0
            && (!server.TryGetValue("Rotation", out var inline) || inline.Length == 0))
        {
            throw new FormatException(
                $"{destination}: [Server] MapRotation names a file, and only the config is pushed - "
                + "on the host that file would not exist. Put the list in [Server] Rotation instead.");
        }

        var options = ServerOptions.CreateDefault();

        try
        {
            ServerConfigFile.ApplyLines(options, SplitLines(rendered), destination);
            options.Validate();
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new FormatException($"{destination}: {e.Message}", e);
        }
    }

    private static void AppendLines(StringBuilder text, string[] lines)
    {
        foreach (var line in lines)
            text.Append(line).Append('\n');
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
