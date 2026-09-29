using System.Text.RegularExpressions;
using GG2.Server;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// Every settings key reaches <c>docs/CONFIG.md</c>'s key reference.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>That file says its key reference is "generated from
/// <c>ServerSettings.All</c>", and nothing generated it.</b> It is written by
/// hand, which is fine — but the claim was unfalsifiable from inside the repo,
/// and it had already gone stale: the whole <c>[Mesh]</c> section was missing,
/// so three settings an operator can set were documented nowhere. This is the
/// gate that makes the claim checkable.
/// </para>
/// <para>
/// ⚠️ <b>It checks presence, not prose.</b> A key can be listed here and
/// described wrongly, and nothing mechanical can see that. What it removes is
/// the failure that actually happened — a whole section of settings added to
/// the code and never written down at all.
/// </para>
/// <para>
/// ⛔ <b>The fix when this goes red is to write the row, not to widen the
/// test.</b> An operator reading the config reference is the only person who
/// finds out otherwise, and they find out by not finding the key.
/// </para>
/// </remarks>
public sealed class ConfigKeyReferenceTests
{
    /// <summary>
    /// <c>[Maps]</c> is a map name per key rather than a fixed set, and the file
    /// says so where it describes the rotation.
    /// </summary>
    private static readonly HashSet<string> SectionsDescribedInProse = ["Maps"];

    private static string Reference() =>
        File.ReadAllText(Path.Combine(ReferenceTree.RepositoryRoot, "docs", "CONFIG.md"));

    [Fact]
    public void EverySettingIsInTheKeyReference()
    {
        var text = Reference();
        var missing = new List<string>();

        foreach (var setting in ServerSettings.All)
        {
            if (SectionsDescribedInProse.Contains(setting.Section))
                continue;

            // Looked for under the key's OWN section, so a name that appears
            // somewhere else in the file does not count as documented.
            if (!HasKey(text, setting.Section, setting.Key))
                missing.Add($"[{setting.Section}] {setting.Key}");
        }

        Assert.True(
            missing.Count == 0,
            "settings docs/CONFIG.md's key reference does not name: "
                + string.Join(", ", missing)
                + ". Add each one under its own section heading. That file is the only place "
                + "an operator can look a key up, and a setting missing from it is a setting "
                + "nobody outside this repo knows exists.");
    }

    /// <summary>
    /// ⛔ The control: the gate can fail. A key nothing documents is reported.
    /// </summary>
    /// <remarks>
    /// <b>A presence check that matched too loosely would pass against
    /// anything</b> — the first draft of this searched the whole file and would
    /// have accepted a key mentioned in an unrelated paragraph. This asserts the
    /// matcher says no to a name that is genuinely absent, and says no to a real
    /// key looked for under the wrong section.
    /// </remarks>
    [Fact]
    public void TheGateReportsAKeyThatIsNotThere()
    {
        var text = Reference();

        Assert.False(
            HasKey(text, "Runtime", "NoSuchKeyExistsAnywhere"),
            "the matcher accepted a key that is not in the file");

        // A real key, under a section it does not belong to.
        Assert.True(HasKey(text, "Runtime", "ControlPort"));
        Assert.False(
            HasKey(text, "Web", "ControlPort"),
            "the matcher found a key outside its own section, so it is not "
                + "section-aware and would accept a row filed anywhere");
    }

    /// <summary>
    /// Whether the reference names this key under its own section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's sections are <c>### `[Section]`</c> headings. Scoped to
    /// the text between one heading and the next, so a key named under a
    /// different section does not count — that scoping is what
    /// <see cref="TheGateReportsAKeyThatIsNotThere"/> pins.
    /// </para>
    /// <para>
    /// ⚠️ <b>A table row OR the key named in the section's prose.</b> Most
    /// sections are tables, but <c>[Classlimits]</c> and <c>[BotClasslimits]</c>
    /// are nineteen keys that differ only by class name and are written as a
    /// sentence listing them — which is better documentation than nineteen
    /// near-identical rows, and would be worse for being reformatted to satisfy
    /// a test.
    /// </para>
    /// </remarks>
    private static bool HasKey(string text, string section, string key)
    {
        var heading = $"### `[{section}]`";
        var start = text.IndexOf(heading, StringComparison.Ordinal);

        if (start < 0)
            return false;

        var next = text.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
        var body = next < 0 ? text[start..] : text[start..next];

        // Backticked, so a key is matched where it is being NAMED rather than
        // wherever its letters happen to appear in a sentence.
        return Regex.IsMatch(body, "`" + Regex.Escape(key) + "`");
    }
}
