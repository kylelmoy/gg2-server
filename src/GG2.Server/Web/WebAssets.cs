using System.Collections.Concurrent;
using System.Reflection;

namespace GG2.Server;

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
    /// <summary>
    /// The font files <c>app.css</c> names, by URL path, to their resource names.
    /// </summary>
    /// <remarks>
    /// A fixed table rather than a pattern on the path, like every other route
    /// here: nothing a request says is ever joined into a resource name. Both
    /// servers that serve <c>app.css</c> serve these, because a stylesheet whose
    /// fonts 404 silently falls back to system faces.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Fonts { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/fonts/oswald.woff2"] = "fonts.oswald.woff2",
        ["/fonts/rubik.woff2"] = "fonts.rubik.woff2",
        ["/fonts/space-mono-400.woff2"] = "fonts.space-mono-400.woff2",
        ["/fonts/space-mono-700.woff2"] = "fonts.space-mono-700.woff2",
    };

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>One asset's bytes, read once and kept.</summary>
    /// <exception cref="InvalidOperationException">
    /// The build did not embed it. Thrown rather than answered with an empty
    /// body: a panel that serves a zero-byte stylesheet looks like a CSS bug and
    /// is a packaging bug.
    /// </exception>
    public static byte[] Read(string name) => Cache.GetOrAdd(name, static key =>
    {
        var resource = "GG2.Server.Web.Assets." + key;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"the web panel asset '{key}' was not embedded in this build (looked for "
                + $"'{resource}'). Check the EmbeddedResource item in GG2.Server.csproj.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });
}
