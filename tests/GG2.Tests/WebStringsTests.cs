using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// ⛔ <b>The panel's text is in <c>strings.js</c> and nowhere else.</b>
/// </summary>
/// <remarks>
/// <para>
/// The table exists so that rewording or translating the panel is one file.
/// That only stays true if every key the pages ask for is in it (a missing one
/// renders as the key itself), every key in it is asked for (a stale entry is a
/// translator's wasted work), and no new text is written straight into the
/// markup or a script's DOM assignment beside it.
/// </para>
/// <para>
/// ⚠️ <b>What this does NOT catch.</b> Text built in a script by any route the
/// regular expressions below do not recognise — a literal passed to a helper
/// that sets it, a string concatenated into a template. The markup check is
/// exhaustive; the script check is the common shape only.
/// </para>
/// </remarks>
public sealed class WebStringsTests
{
    private static string Asset(string name) => Encoding.UTF8.GetString(WebAssets.Read(name));

    private static readonly string[] Pages = ["index.html", "editor.html"];

    private static readonly string[] Scripts =
        WebAssetTests.PanelScripts.Union(WebAssetTests.EditorScripts).Where(name => name != "strings.js").ToArray();

    private static readonly HashSet<string> PluralCategories = ["zero", "one", "two", "few", "many", "other"];

    /// <summary>The table, parsed: between <c>const STRINGS =</c> and the final <c>;</c> is strict JSON.</summary>
    private static JsonElement Table()
    {
        var text = Asset("strings.js");
        const string opening = "const STRINGS = ";
        var start = text.IndexOf(opening, StringComparison.Ordinal);

        Assert.True(start >= 0, "strings.js does not declare `const STRINGS = `");

        start += opening.Length;
        var end = text.LastIndexOf(';');

        return JsonDocument.Parse(text[start..end]).RootElement;
    }

    /// <summary>Every leaf key, dotted, with its value (a string or a plural object).</summary>
    private static Dictionary<string, JsonElement> Leaves()
    {
        var leaves = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Walk(Table(), "");
        return leaves;

        void Walk(JsonElement element, string prefix)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix + property.Name;

                if (property.Value.ValueKind == JsonValueKind.String || IsPlural(property.Value))
                    leaves[key] = property.Value;
                else if (property.Value.ValueKind == JsonValueKind.Object)
                    Walk(property.Value, key + ".");
                else
                    Assert.Fail($"strings.js: '{key}' is neither a string, a plural object nor a group");
            }
        }
    }

    private static bool IsPlural(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("other", out var other)
        && other.ValueKind == JsonValueKind.String
        && value.EnumerateObject().All(p => PluralCategories.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.String);

    /// <summary>Every key named literally: data-t attributes in the pages, t('…') in the scripts.</summary>
    private static HashSet<string> UsedKeys()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var page in Pages)
            used.UnionWith(Regex.Matches(Asset(page), @"\sdata-t(?:-[a-z-]+)?=""([^""]+)""").Select(m => m.Groups[1].Value));

        foreach (var script in Scripts)
            used.UnionWith(Regex.Matches(Asset(script), @"\bt\((['""])([\w.]+)\1").Select(m => m.Groups[2].Value));

        return used;
    }

    /// <summary>Every prefix a script looks up by template: <c>t(`mode.${x}`)</c> uses all of <c>mode.</c>.</summary>
    private static HashSet<string> UsedPrefixes() =>
        new(Scripts.SelectMany(script => Regex.Matches(Asset(script), @"\bt\(`([\w.]+\.)\$\{").Select(m => m.Groups[1].Value)),
            StringComparer.Ordinal);

    [Fact]
    public void TheTableIsStrictJsonAndNamesItsLanguage()
    {
        var table = Table();
        Assert.Equal(JsonValueKind.String, table.GetProperty("lang").ValueKind);
    }

    [Fact]
    public void EveryKeyUsedIsInTheTable()
    {
        var leaves = Leaves();
        var used = UsedKeys();

        // The vacuity control: the scan has to be finding the keys at all.
        Assert.True(used.Count > 200, $"only {used.Count} keys found in use — the scan is broken");

        var missing = used.Where(key => !leaves.ContainsKey(key)).Order().ToList();
        Assert.True(missing.Count == 0, $"not in strings.js: {string.Join(", ", missing)}");

        var groups = UsedPrefixes().Where(prefix => !leaves.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        Assert.True(groups.Count == 0, $"looked up by template but empty in strings.js: {string.Join(", ", groups)}");
    }

    [Fact]
    public void EveryKeyInTheTableIsUsed()
    {
        var used = UsedKeys();
        var prefixes = UsedPrefixes();

        var unused = Leaves().Keys
            .Where(key => key != "lang")
            .Where(key => !used.Contains(key) && !prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            .Order()
            .ToList();

        Assert.True(unused.Count == 0, $"in strings.js but used nowhere: {string.Join(", ", unused)}");
    }

    /// <summary>
    /// Every visible piece of text in both pages comes from the table: no text
    /// node and no text-bearing attribute with a letter in it.
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("editor.html")]
    public void ThePagesHoldNoTextOfTheirOwn(string page)
    {
        var html = Asset(page);

        // Comments, scripts, styles and the drawn marks are not text a reader sees.
        html = Regex.Replace(html, @"<!--.*?-->|<script\b.*?</script>|<style\b.*?</style>|<svg\b.*?</svg>", "", RegexOptions.Singleline);

        var text = Regex.Matches(html, @">([^<>]*\p{L}[^<>]*)<")
            .Select(m => m.Groups[1].Value.Trim());

        var attributes = Regex.Matches(html, @"\s(title|placeholder|aria-label|alt)=""([^""]*\p{L}[^""]*)""")
            .Select(m => $"{m.Groups[1].Value}=\"{m.Groups[2].Value}\"");

        var found = text.Concat(attributes).ToList();

        Assert.True(found.Count == 0, $"{page} holds text outside strings.js: {string.Join(" | ", found)}");
    }

    /// <summary>
    /// No script assigns a literal with a letter in it to anything a reader sees.
    /// </summary>
    [Fact]
    public void TheScriptsSetNoLiteralText()
    {
        var found = new List<string>();

        foreach (var script in Scripts)
        {
            var line = 0;

            foreach (var text in Asset(script).Split('\n'))
            {
                line++;

                if (Regex.IsMatch(text, @"\.(textContent|innerHTML|title|placeholder|ariaLabel)\s*=\s*(['""`])[^'""`]*\p{L}"))
                    found.Add($"{script}:{line}: {text.Trim()}");
            }
        }

        Assert.True(found.Count == 0, string.Join("\n", found));
    }
}
