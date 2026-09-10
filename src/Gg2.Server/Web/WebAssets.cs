using System.Collections.Concurrent;
using System.Reflection;

namespace Gg2.Server;

/// <summary>
/// The panel's files, compiled into the assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Embedded rather than copied next to the binary</b>, for the same reason
/// the maps travel with a publish: a server deployed by copying one directory
/// must not be able to end up with a working binary and a missing page. There is
/// no <c>--web-root</c> and no directory served off disk, which also means there
/// is no path-traversal surface at all — the routes name the five files by hand.
/// </para>
/// <para>
/// <b>No build step and no dependencies.</b> The page is hand-written HTML,
/// CSS and JavaScript that a browser runs as-is: nothing to <c>npm install</c>,
/// nothing to transpile, and no CDN — which matters because a LAN server's panel
/// has to work on a machine with no route to the internet.
/// </para>
/// </remarks>
internal static class WebAssets
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>One asset's bytes, read once and kept.</summary>
    /// <exception cref="InvalidOperationException">
    /// The build did not embed it. Thrown rather than answered with an empty
    /// body: a panel that serves a zero-byte stylesheet looks like a CSS bug and
    /// is a packaging bug.
    /// </exception>
    public static byte[] Read(string name) => Cache.GetOrAdd(name, static key =>
    {
        var resource = "Gg2.Server.Web.Assets." + key;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"the web panel asset '{key}' was not embedded in this build (looked for "
                + $"'{resource}'). Check the EmbeddedResource item in Gg2.Server.csproj.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });
}
