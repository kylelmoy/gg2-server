using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GG2.Server;

namespace GG2.Tests;

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
    [InlineData("common.js")]
    [InlineData("analytics.js")]
    [InlineData("settings.js")]
    [InlineData("editor.html")]
    [InlineData("editor.css")]
    [InlineData("editor.js")]
    [InlineData("favicon.svg")]
    [InlineData("world.json")]
    public void EveryAssetIsEmbedded(string name) => Assert.NotEmpty(Asset(name));

    /// <summary>The panel's scripts, in the order index.html loads them.</summary>
    private static readonly string[] PanelScripts = ["common.js", "analytics.js", "settings.js", "app.js"];

    /// <summary>The settings page's scripts, in the order editor.html loads them.</summary>
    private static readonly string[] EditorScripts = ["common.js", "settings.js", "editor.js"];

    /// <summary>
    /// Both pages load the scripts their script tags name, in that order — the
    /// lists above are what the two checks below scan, so a page that stopped
    /// loading one would otherwise be checked against a set it does not run.
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("editor.html")]
    public void TheScriptListsAreWhatThePagesLoad(string page)
    {
        var loaded = Regex.Matches(Asset(page), @"<script src=""/([^""]+)""").Select(m => m.Groups[1].Value);
        Assert.Equal(page == "index.html" ? PanelScripts : EditorScripts, loaded);
    }

    /// <summary>The settings page's scripts declare no name twice, as the panel's do not.</summary>
    [Fact]
    public void TheSettingsPageDeclaresNoNameTwice()
    {
        var declared = Declarations(EditorScripts.Select(name => (name, Asset(name))).ToArray());
        Assert.True(declared > 30, $"only {declared} top-level names found — the scan is broken");
    }

    /// <summary>Every element the settings page's scripts ask for by a literal id is in editor.html.</summary>
    [Fact]
    public void EveryIdTheSettingsPageAsksForIsInIt()
    {
        var present = Ids(Asset("editor.html"));
        var wanted = WantedIds(string.Concat(EditorScripts.Select(Asset)));

        Assert.True(wanted.Count > 25, $"only {wanted.Count} literal ids found — the scan is broken");

        var missing = wanted.Where(id => !present.Contains(id)).ToList();

        Assert.True(missing.Count == 0, $"not in editor.html: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The line <c>scripts/fleet-analytics.sh</c> cuts the analytics sheet out
    /// of index.html from, up to the first <c>&lt;/dialog&gt;</c> after it.
    /// </summary>
    private const string AnalyticsSheetStart = "<dialog class=\"sheet sheet-wide\" id=\"analytics\"";

    private static string RepoFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    /// <summary>
    /// A repo file that may not be there, because <c>/scripts/</c> is gitignored
    /// operator tooling.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Absent from a fresh clone, and from any copy taken through git.</b>
    /// Reading one unguarded turned "this machine has never run a deploy" into a
    /// red suite with a <c>DirectoryNotFoundException</c> that said nothing
    /// about the cross-check it was doing. The same shape as
    /// <c>FleetConfigTests</c>: the checks that need only what git carries run
    /// everywhere, and the cross-check against the operator's scripts runs where
    /// those scripts are — which is the machine where a drift can be introduced.
    /// </remarks>
    private static string? OperatorFile(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// ⛔ <b>No two top-level declarations share a name, across every script a
    /// page loads.</b> Classic scripts share one global scope, so a second
    /// <c>function</c> replaces the first and its callers break somewhere else
    /// entirely, and a second <c>const</c> stops the whole later file running.
    /// </summary>
    [Fact]
    public void NoTopLevelNameIsDeclaredTwice()
    {
        var panel = PanelScripts.Select(name => (name, Asset(name))).ToArray();
        var declared = Declarations(panel);

        // The vacuity control: this only means anything if the scan found the
        // declarations at all.
        Assert.True(declared > 50, $"only {declared} top-level names found — the scan is broken");

        // The fleet page loads common.js, analytics.js and its own fleet.js.
        if (OperatorFile("scripts", "fleet-analytics", "fleet.js") is { } fleet)
            Declarations([panel[0], panel[1], ("fleet.js", fleet)]);
    }

    private static int Declarations((string Name, string Text)[] scripts)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var (file, script) in scripts)
        {
            var line = 0;

            foreach (var text in script.Split('\n'))
            {
                line++;

                // Column zero only: anything indented is inside a function or a
                // block, where shadowing is legal and often deliberate.
                var match = Regex.Match(text, @"^(?:async\s+)?(?:function|const|let|var)\s+([A-Za-z_$][\w$]*)");

                if (!match.Success)
                    continue;

                var name = match.Groups[1].Value;

                if (declared.TryGetValue(name, out var first))
                    duplicates.Add($"'{name}' at {file}:{line} was already declared at {first}");
                else
                    declared[name] = $"{file}:{line}";
            }
        }

        Assert.True(duplicates.Count == 0, string.Join("; ", duplicates));
        return declared.Count;
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
        var present = Ids(Asset("index.html"));
        var wanted = WantedIds(string.Concat(PanelScripts.Select(Asset)));

        Assert.True(wanted.Count > 40, $"only {wanted.Count} literal ids found — the scan is broken");

        var missing = wanted.Where(id => !present.Contains(id)).ToList();

        Assert.True(missing.Count == 0, $"not in index.html: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// ⛔ <b>The fleet page's cut of index.html has every id its scripts ask
    /// for.</b> That page is the analytics sheet ALONE, so an id the view uses
    /// that lives anywhere else in index.html passes the test above and throws
    /// on the fleet page.
    /// </summary>
    /// <remarks>
    /// The other ids allowed are the fleet page's own: the two JSON blocks the
    /// build script writes, and the elements fleet.js creates.
    /// </remarks>
    [Fact]
    public void TheFleetPageHasEveryIdItsScriptsAskFor()
    {
        var html = Asset("index.html");
        var start = html.IndexOf(AnalyticsSheetStart, StringComparison.Ordinal);

        Assert.True(start >= 0, "the analytics sheet's opening line is not in index.html; scripts/fleet-analytics.sh cuts on it");
        Assert.Equal(start, html.LastIndexOf(AnalyticsSheetStart, StringComparison.Ordinal));

        var sheet = html[start..html.IndexOf("</dialog>", start, StringComparison.Ordinal)];

        if (OperatorFile("scripts", "fleet-analytics", "fleet.js") is not { } fleet
            || OperatorFile("scripts", "fleet-analytics.sh") is not { } build)
        {
            // Everything above this line checked index.html, which git carries.
            return;
        }

        // The script cuts on the same line and writes the two blocks it is credited with.
        Assert.Contains(AnalyticsSheetStart, build, StringComparison.Ordinal);
        Assert.Contains("id=\"fleet-data\"", build, StringComparison.Ordinal);
        Assert.Contains("id=\"fleet-world\"", build, StringComparison.Ordinal);

        var present = Ids(sheet);
        present.UnionWith(["fleet-data", "fleet-world"]);
        present.UnionWith(Regex.Matches(fleet, @"\.id = '([^']+)'").Select(m => m.Groups[1].Value));

        var wanted = WantedIds(Asset("analytics.js") + fleet);

        Assert.True(wanted.Count > 15, $"only {wanted.Count} literal ids found — the scan is broken");

        var missing = wanted.Where(id => !present.Contains(id)).ToList();

        Assert.True(missing.Count == 0, $"not in the fleet page: {string.Join(", ", missing)}");
    }

    private static HashSet<string> Ids(string html) =>
        new(Regex.Matches(html, @"\sid=""([^""]+)""").Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static List<string> WantedIds(string script) =>
        Regex.Matches(script, @"\$\('([^']+)'\)")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(script, @"getElementById\('([^']+)'\)").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GG2.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("no GG2.sln above the test binaries");
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
