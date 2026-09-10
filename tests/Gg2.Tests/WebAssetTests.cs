using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The panel's own files, checked the way a build step would if it had one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>WebAssets</c> is deliberately a no-build-step,
/// no-dependency page — a LAN server's panel has to work on a machine with no
/// route to the internet — and the cost of that is that nothing at all checks
/// the JavaScript. Two mistakes are both mechanical and both silent, and both
/// have actually happened here:
/// </para>
/// <list type="bullet">
/// <item><b>A second top-level declaration of an existing name</b> replaces the
/// first. Adding a function called <c>kpis</c> to the analytics view replaced
/// the performance card's <c>kpis</c>, which then threw on every state
/// snapshot — in a card the author was not looking at.</item>
/// <item><b>A <c>$('id')</c> for an element that is not in the HTML</b> returns
/// null and throws on the next property access, and the id is usually one
/// character different from a real one.</item>
/// </list>
/// <para>
/// ⚠️ <b>What this does NOT catch, and cannot.</b> Anything that needs the code
/// to run: a local variable shadowing a helper it later calls, an event handler
/// wired to nothing, a canvas drawn in the wrong place. The panel is verified
/// by opening it, and <c>docs/WEBADMIN.md</c> says so. These two are here
/// because they are the ones a regular expression can settle.
/// </para>
/// </remarks>
public sealed class WebAssetTests
{
    private static string Asset(string name) => Encoding.UTF8.GetString(WebAssets.Read(name));

    /// <summary>
    /// Every asset the routes name is actually in the build.
    /// </summary>
    /// <remarks>
    /// <c>WebAssets.Read</c> throws rather than serving an empty body, so a
    /// missing <c>EmbeddedResource</c> line is a failure here rather than a
    /// blank page nobody notices until a deployment.
    /// </remarks>
    [Theory]
    [InlineData("index.html")]
    [InlineData("app.css")]
    [InlineData("app.js")]
    [InlineData("favicon.svg")]
    [InlineData("world.json")]
    public void EveryAssetIsEmbedded(string name) => Assert.NotEmpty(Asset(name));

    /// <summary>
    /// ⛔ <b>No two top-level declarations share a name.</b> The page is one
    /// script in one scope, so the second one wins and the first one's callers
    /// break somewhere else entirely.
    /// </summary>
    [Fact]
    public void NoTopLevelNameIsDeclaredTwice()
    {
        var declared = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        var line = 0;

        foreach (var text in Asset("app.js").Split('\n'))
        {
            line++;

            // Column zero only: anything indented is inside a function or a
            // block, where shadowing is legal and often deliberate.
            var match = Regex.Match(text, @"^(?:function|const|let|var)\s+([A-Za-z_$][\w$]*)");

            if (!match.Success)
                continue;

            var name = match.Groups[1].Value;

            if (declared.TryGetValue(name, out var first))
                duplicates.Add($"'{name}' at line {line} was already declared at line {first}");
            else
                declared[name] = line;
        }

        Assert.True(duplicates.Count == 0, string.Join("; ", duplicates));

        // The vacuity control: this only means anything if the scan found the
        // declarations at all.
        Assert.True(declared.Count > 50, $"only {declared.Count} top-level names found — the scan is broken");
    }

    /// <summary>
    /// Every element the script asks for by a literal id is in the page.
    /// </summary>
    /// <remarks>
    /// Literal ids only. The settings editor builds ids from a template
    /// (<c>set:${setting.id}</c>) and those elements are created at runtime, so
    /// they are neither in the HTML nor findable here.
    /// </remarks>
    [Fact]
    public void EveryIdTheScriptAsksForIsInThePage()
    {
        var html = Asset("index.html");

        var present = new HashSet<string>(
            Regex.Matches(html, @"\sid=""([^""]+)""").Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);

        var script = Asset("app.js");

        var wanted = Regex.Matches(script, @"\$\('([^']+)'\)")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(script, @"getElementById\('([^']+)'\)").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(wanted.Count > 40, $"only {wanted.Count} literal ids found — the scan is broken");

        var missing = wanted.Where(id => !present.Contains(id)).ToList();

        Assert.True(missing.Count == 0, $"not in index.html: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The map geometry is the shape the panel draws it as.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is a checked-in generated file</b> (<c>tools/gen-geo-assets.js</c>),
    /// so a regeneration that changed its shape — a renamed view, a points table
    /// that came back empty because a source's property names moved — would
    /// otherwise reach a browser before anybody noticed.
    /// </remarks>
    [Fact]
    public void TheMapGeometryHasBothViews()
    {
        var world = JsonDocument.Parse(Asset("world.json")).RootElement;

        Assert.Equal(10000, world.GetProperty("grid").GetInt32());

        foreach (var name in new[] { "world", "us" })
        {
            var view = world.GetProperty("views").GetProperty(name);

            Assert.InRange(view.GetProperty("aspect").GetDouble(), 1.0, 3.0);
            Assert.True(view.GetProperty("rings").GetArrayLength() > 40, $"{name}: too few rings");
            Assert.True(view.GetProperty("points").EnumerateObject().Count() > 40, $"{name}: too few markers");

            // Every ring is a flat x,y list, so an odd length is a truncated one.
            foreach (var ring in view.GetProperty("rings").EnumerateArray())
                Assert.True(ring.GetArrayLength() % 2 == 0, $"{name}: a ring has an odd number of numbers");
        }

        // The two views the panel actually looks places up in.
        var countries = world.GetProperty("views").GetProperty("world").GetProperty("points");
        var states = world.GetProperty("views").GetProperty("us").GetProperty("points");

        foreach (var code in new[] { "US", "GB", "DE", "BR", "SG", "PR" })
            Assert.True(countries.TryGetProperty(code, out _), $"no marker for the country {code}");

        // ⚠️ Alaska and Hawaii are the two that are not simply "in the box" —
        // they are moved into insets by the generator, and a change there that
        // dropped them would leave two states nothing could ever place.
        foreach (var code in new[] { "CA", "TX", "NY", "AK", "HI", "DC" })
            Assert.True(states.TryGetProperty(code, out _), $"no marker for the state {code}");
    }

    /// <summary>
    /// ⛔ <b>No ring crosses the whole map.</b>
    /// </summary>
    /// <remarks>
    /// Natural Earth cuts a landmass that crosses 180° into points at both
    /// -180 and +180, which in this projection are opposite edges — so the
    /// segment between them draws as a line straight across the world.
    /// Measured in a browser, not reasoned about: Antarctica, Chukotka, Wrangel
    /// Island and a Fijian island each put one there.
    /// <see cref="TheMapGeometryHasBothViews"/> would not notice, because the
    /// file is perfectly well-formed with them in it.
    /// </remarks>
    [Fact]
    public void NoRingJumpsAcrossTheAntimeridian()
    {
        var world = JsonDocument.Parse(Asset("world.json")).RootElement;
        var grid = world.GetProperty("grid").GetInt32();
        var offenders = new List<string>();

        foreach (var view in world.GetProperty("views").EnumerateObject())
        {
            var index = 0;

            foreach (var ring in view.Value.GetProperty("rings").EnumerateArray())
            {
                var points = ring.EnumerateArray().Select(v => v.GetInt32()).ToArray();

                for (var i = 0; i + 3 < points.Length; i += 2)
                {
                    if (Math.Abs(points[i + 2] - points[i]) > grid / 2)
                        offenders.Add($"{view.Name} ring {index} at point {i / 2}");
                }

                index++;
            }
        }

        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }
}
