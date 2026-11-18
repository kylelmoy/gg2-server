using System.Security.Cryptography;

namespace GG2.Server.Plugins;

/// <summary>
/// Where a plugin's zip hash comes from when the operator has not pinned one.
/// </summary>
/// <remarks>
/// A seam, not an abstraction for its own sake: the default implementation
/// reaches ganggarrison.com over HTTP, and a test that did that would be
/// measuring the internet.
/// </remarks>
public interface IPluginHashSource
{
    /// <summary>
    /// The md5 of the current <c>name.zip</c>, as 32 lowercase hex digits.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No such plugin, or the source could not be reached.
    /// </exception>
    string Fetch(string name);
}

/// <summary>
/// <c>getpluginhashes.gml</c>'s HTTP half: <c>GET PLUGIN_SOURCE + name + ".md5"</c>.
/// </summary>
/// <remarks>
/// <b>Synchronous and at startup only.</b> The reference blocks in a
/// <c>http_step</c> spin loop for exactly this
/// (<c>getpluginhashes.gml:57-60</c>), before the listening socket exists, so
/// nothing is being starved. Do not move this onto the tick thread's path.
/// <para>
/// The reference reads the first 32 bytes of the body and asks no further
/// questions (<c>:73</c>, <c>read_string(body, 32)</c>). This validates the hex,
/// because a headless server that advertises an html error page as a hash sends
/// every client on a 404 hunt.
/// </para>
/// </remarks>
public sealed class GanggarrisonPluginHashes : IPluginHashSource, IDisposable
{
    /// <summary>
    /// <c>Constants.xml</c>'s <c>PLUGIN_SOURCE</c>, character for character
    /// — the client builds its download URL from the same constant, so a server
    /// pointing somewhere else would advertise hashes for files no client can
    /// fetch.
    /// </summary>
    public const string PluginSource = "http://www.ganggarrison.com/plugins/";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _source;

    public GanggarrisonPluginHashes(string source = PluginSource) => _source = source;

    public string Fetch(string name)
    {
        var url = _source + name + ".md5";

        HttpResponseMessage response;
        try
        {
            response = _http.GetAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                $"could not fetch the hash for plugin '{name}' from {url}: {e.Message}", e);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"no plugin named '{name}' at {url} (404). Plugin names are always lowercase, "
                    + "and the list must have no spaces around its commas.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"could not fetch the hash for plugin '{name}': "
                    + $"{(int)response.StatusCode} {response.ReasonPhrase}");

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var hash = body.Length >= 32 ? body[..32].ToLowerInvariant() : body.Trim().ToLowerInvariant();

            if (hash.Length != 32 || !IsHex(hash))
                throw new InvalidOperationException(
                    $"the hash served for plugin '{name}' is not 32 hex digits: '{hash}'");

            return hash;
        }
    }

    public void Dispose() => _http.Dispose();

    private static bool IsHex(string value)
    {
        foreach (var c in value)
            if (!char.IsAsciiHexDigitLower(c))
                return false;

        return true;
    }
}

/// <summary>
/// Resolves each configured plugin to the exact zip its clients will run.
/// </summary>
public static class PluginHashes
{
    /// <summary>
    /// The reference's local-override directory
    /// (<c>getpluginhashes.gml:43-47</c>): a <c>name.zip</c> here is hashed
    /// instead of the site's.
    /// </summary>
    /// <remarks>
    /// Relative to the working directory, and the client looks in its own
    /// copy of the same directory. The reference warns the operator out loud
    /// that clients need the same file; this port logs the same warning, because
    /// the failure otherwise is "nobody can connect" with no cause on screen.
    /// </remarks>
    public const string DebugDirectory = "ServerPluginsDebug";

    /// <summary>
    /// Picks a hash per spec, in the order that keeps a running server working.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>An <c>@hash</c> the operator pinned. Their call, and it wins.</item>
    /// <item>
    /// <c>ServerPluginsDebug/name.zip</c>, hashed locally — the reference's own
    /// first choice, and how a plugin is developed.
    /// </item>
    /// <item>
    /// <b>The hash this port's C# implementation was written against</b>, when
    /// there is one. This is NOT what the reference does, and the difference is
    /// the point: the reference downloads the plugin's GML and runs both halves
    /// of it, so a new upstream release changes both halves together. Here the
    /// server half is C# pinned to one version of the protocol, and fetching the
    /// latest zip would hand the clients a GML the server may no longer agree
    /// with — silently, and only for whichever packets changed.
    /// </item>
    /// <item>The site, exactly as <c>getpluginhashes.gml</c> does.</item>
    /// </list>
    /// When 1, 2 or 4 disagree with an implementation's pin, the mismatch is
    /// logged rather than swallowed. It is the one condition under which the
    /// server half and the client half are running different protocols.
    /// </remarks>
    public static IReadOnlyList<LoadedPlugin> Resolve(
        IReadOnlyList<PluginSpec> specs,
        IPluginHashSource source,
        IServerLog log,
        string? debugDirectory = null,
        Func<string, IServerPlugin?>? native = null)
    {
        var loaded = new List<LoadedPlugin>(specs.Count);
        debugDirectory ??= DebugDirectory;

        foreach (var spec in specs)
        {
            // A server-owned implementation (the native chat on `chat`) first;
            // otherwise the catalogue's.
            var implementation = native?.Invoke(spec.Name) ?? ServerPluginCatalogue.Create(spec.Name);
            var pinned = ServerPluginCatalogue.PinnedMd5(spec.Name);

            string md5;
            string origin;

            var debugZip = Path.Combine(debugDirectory, spec.Name + ".zip");

            if (spec.Md5 is not null)
            {
                md5 = spec.Md5;
                origin = "pinned in the plugin list";
            }
            else if (File.Exists(debugZip))
            {
                md5 = Md5Of(debugZip);
                origin = debugZip;

                log.Info(
                    $"WARNING: plugin '{spec.Name}' is being advertised from {debugZip}. "
                    + "Every client needs that same file, or it will be unable to connect.");
            }
            else if (pinned is not null)
            {
                md5 = pinned;
                origin = "the version this server's implementation was written against";
            }
            else
            {
                md5 = source.Fetch(spec.Name);
                origin = GanggarrisonPluginHashes.PluginSource;
            }

            if (pinned is not null && !string.Equals(md5, pinned, StringComparison.Ordinal))
            {
                log.Info(
                    $"WARNING: plugin '{spec.Name}' is advertised as {md5} ({origin}), but this "
                    + $"server's implementation of it was written against {pinned}. Clients will "
                    + "run a different version of the plugin's GML from the one the server half "
                    + "agrees with; re-check the packet formats before relying on this.");
            }

            if (implementation is null)
            {
                log.Info(
                    $"WARNING: plugin '{spec.Name}' has no server-side implementation in this port. "
                    + "It is advertised to clients so its client half loads, and packets it sends "
                    + "the server are accepted and discarded. A plugin whose server half does "
                    + "anything will not work.");
            }

            loaded.Add(new LoadedPlugin(spec.Name, md5, implementation));
        }

        return loaded;
    }

    /// <summary>
    /// <c>GG2DLL_compute_MD5(file)</c>: lowercase hex of the file's md5.
    /// </summary>
    public static string Md5Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }
}
