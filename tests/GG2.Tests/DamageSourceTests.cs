using System.Text.RegularExpressions;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// The damage-source ids, checked against the script that defines them.
/// </summary>
/// <remarks>
/// <b>These ids exist nowhere as a table, not even in the reference.</b>
/// <c>Scripts/Kill Log/initializeDamageSources.gml</c> calls
/// <c>createGlobalDamageSource</c> once per source, and
/// <c>createDamageSource</c> returns <c>ds_list_size</c> <em>after</em> the add
/// — so the ids are the order of those calls, starting at 1. They reach the
/// client in every <c>PLAYER_DEATH</c>.
/// <para>
/// Which makes this exactly the kind of constant a transcription gets subtly
/// wrong and no fixture notices: inserting a source in the middle of that script
/// renumbers half the list, and the only symptom is the wrong icon in somebody's
/// kill log. So the test parses the script rather than restating it.
/// </para>
/// </remarks>
public sealed class DamageSourceTests
{
    private static string ScriptPath =>
        Path.Combine(ReferenceTree.Root, "Source", "gg2", "Scripts", "Kill Log", "initializeDamageSources.gml");

    /// <summary>
    /// <c>DAMAGE_SOURCE_RIFLE_CHARGED</c> to <c>RifleCharged</c>: the enum drops
    /// the prefix and the underscores.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively at the point of use, because the script is not
    /// consistent about where a word ends: <c>SENTRYTURRET</c> and
    /// <c>ROCKETLAUNCHER</c> are one word each while <c>RIFLE_CHARGED</c> is
    /// two, and the enum spells the first of those <c>SentryTurret</c>. The
    /// count assertion below is what stops the looser match from letting an
    /// extra member through.
    /// </remarks>
    private static string ToMemberName(string constant) =>
        constant["DAMAGE_SOURCE_".Length..].Replace("_", string.Empty);

    [Fact]
    public void EveryIdMatchesTheOrderTheScriptRegistersThemIn()
    {
        ReferenceTree.RequireAvailable();
        Assert.True(File.Exists(ScriptPath), $"the reference tree has no script at {ScriptPath}");

        var registered = Regex
            .Matches(File.ReadAllText(ScriptPath), @"createGlobalDamageSource\(\s*""(DAMAGE_SOURCE_[A-Z_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(registered);

        for (var i = 0; i < registered.Count; i++)
        {
            var member = ToMemberName(registered[i]);
            Assert.True(
                Enum.TryParse<DamageSource>(member, ignoreCase: true, out var value),
                $"{registered[i]} is registered by the reference but DamageSource has no {member}");

            // The first call returns 1, because createDamageSource reads the
            // list size after adding rather than before.
            Assert.Equal(i + 1, (int)value);
        }

        // Nothing extra, either: an id the reference does not issue would be a
        // number this port could send and no client could draw.
        var expected = registered.Count + 1; // the sources, plus None.
        Assert.Equal(expected, Enum.GetValues<DamageSource>().Length);
    }

    /// <summary>
    /// Zero is the reference's <c>lastDamageSource = -1</c>, which it can be
    /// because <c>createDamageSource</c> starts issuing at 1 — its own comment
    /// says it avoids 0 in case 0 means something.
    /// </summary>
    [Fact]
    public void NoneIsZeroAndIsNotARealSource() => Assert.Equal(0, (int)DamageSource.None);
}
