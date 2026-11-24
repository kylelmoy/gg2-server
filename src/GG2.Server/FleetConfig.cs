using System.Security.Cryptography;
using System.Text;

namespace GG2.Server;

/// <summary>
/// One configuration for several servers: a shared <c>common.json</c>, one small
/// overlay per host, and the <c>gg2.json</c> each host is sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout</b>, under a directory that is normally <c>deploy/config</c>:
/// <code>
/// common.json              every setting the servers share
/// hosts/&lt;dest&gt;.json       what one host does differently — its name, its message
/// </code>
/// <c>&lt;dest&gt;</c> is the ssh destination exactly as <c>deploy/hosts</c> names it,
/// passed through <see cref="OverlayFileName"/>. <c>scripts/deploy.sh</c> applies
/// the same mapping in bash; <c>FleetConfigTests</c> pins the two together.
/// </para>
/// <para>
/// <b>A rendered file is <c>common.json</c> with the overlay merged over it, key
/// by key</b> (<see cref="ConfigDocument.Merge"/>), and the server reads the
/// result once. ⚠️ Applying the two files one after the other is NOT the same:
/// an overlay with no <c>Map</c> key empties a <c>Map</c> that
/// <c>common.json</c> set, because a file with a rotation and no <c>Map</c>
/// opens on the rotation.
/// </para>
/// <para>
/// ⚠️ <b>The rendered file is validated here, on the machine that renders it</b>,
/// with the same <see cref="ServerOptions.Validate"/> the server runs at start.
/// A value that would stop a server coming back up is refused before any host
/// is contacted.
/// </para>
/// <para>
/// ⚠️ <b><c>Server.MapRotation</c> is refused.</b> It names a file beside the
/// config, and only the config is pushed; on the host the file would not exist
/// and the reference's silent fallback to <c>Maps</c> would play a rotation
/// nobody wrote. <c>Server.Rotation</c> carries the list inline.
/// </para>
/// </remarks>
public static class FleetConfig
{
    /// <summary>The shared file's name inside the config directory.</summary>
    public const string CommonFileName = "common.json";

    /// <summary>The overlays' directory inside the config directory.</summary>
    public const string HostsDirectoryName = "hosts";

    /// <summary>
    /// Appended to a config file's path to name its push marker —
    /// <c>gg2.json.pushed</c>. It holds the SHA-256 of the file as last pushed.
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
    /// started from — <c>gg2.json.applied</c>.
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
    /// expression. <c>admin@203.0.113.7</c> passes through unchanged; a
    /// <c>host:2222</c> or an IPv6 literal does not, and the two sides must agree
    /// on what it becomes.
    /// </remarks>
    public static string OverlayFileName(string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);

        var name = new StringBuilder(destination.Length + 4);

        foreach (var c in destination)
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '@' or '-' ? c : '_');

        return name.Append(".json").ToString();
    }

    public static string CommonPath(string directory) => Path.Combine(directory, CommonFileName);

    public static string OverlayPath(string directory, string destination) =>
        Path.Combine(directory, HostsDirectoryName, OverlayFileName(destination));

    /// <summary>
    /// The <c>gg2.json</c> one host is sent: <c>common.json</c> with the host's
    /// overlay merged over it, validated.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no <c>common.json</c>.</exception>
    /// <exception cref="FormatException">
    /// A file that is not JSON, a value the server would refuse, or a
    /// <c>Server.MapRotation</c>. The message names the host.
    /// </exception>
    /// <remarks>
    /// A host with no overlay renders as <c>common.json</c> alone — legal, and
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
            File.ReadAllText(common),
            File.Exists(overlay) ? File.ReadAllText(overlay) : null,
            destination);
    }

    /// <summary><see cref="Render(string, string)"/> over files already in memory.</summary>
    public static string Render(
        string common, string? overlay, string destination, string managedBy = "scripts/deploy.sh --config")
    {
        ConfigDocument merged;

        try
        {
            merged = Parse(CommonFileName, common);

            if (overlay is not null)
                merged = merged.Merge(Parse($"{HostsDirectoryName}/{OverlayFileName(destination)}", overlay));
        }
        catch (FormatException e)
        {
            throw new FormatException($"{destination}: {e.Message}", e);
        }

        merged.SetNote(
            $"Rendered from {CommonFileName} and {HostsDirectoryName}/{OverlayFileName(destination)} for {destination}. "
            + $"Managed by {managedBy}. An edit here, or a save from the admin panel, is kept - "
            + "but the next push stops for this server until it is forced.");

        Check(merged, destination);
        return merged.ToText();
    }

    private static ConfigDocument Parse(string name, string text)
    {
        try
        {
            return ConfigDocument.Parse(text);
        }
        catch (FormatException e)
        {
            throw new FormatException($"{name}: {e.Message}", e);
        }
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
        ServerConfigFile.ApplyText(options, rendered, CommonPath(directory));
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

    /// <summary>The overlay as parsed — used by the editor to show what a host overrides.</summary>
    public static ConfigDocument ReadOverlay(string directory, string destination)
    {
        var path = OverlayPath(directory, destination);
        return File.Exists(path) ? ConfigDocument.Parse(File.ReadAllText(path)) : new ConfigDocument();
    }

    private static void Check(ConfigDocument rendered, string destination)
    {
        if (rendered.Text("Server", "MapRotation").Length != 0
            && rendered.Text("Server", "Rotation").Length == 0)
        {
            throw new FormatException(
                $"{destination}: Server.MapRotation names a file, and only the config is pushed - "
                + "on the host that file would not exist. Put the list in Server.Rotation instead.");
        }

        var options = ServerOptions.CreateDefault();

        try
        {
            ServerConfigFile.ApplyDocument(options, rendered, destination);
            options.Validate();
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new FormatException($"{destination}: {e.Message}", e);
        }
    }
}
