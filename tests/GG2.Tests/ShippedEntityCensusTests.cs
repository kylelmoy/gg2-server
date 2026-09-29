using GG2.Maps;

namespace GG2.Tests;

/// <summary>
/// Which level entities the 22 shipped maps actually place, and how many.
/// </summary>
/// <remarks>
/// <b>"What do the shipped maps never contain?" is a question this port keeps
/// needing.</b> Checked across all 22 maps: <c>moving_platform</c> appears zero
/// times, and the full census below is asserted in both directions.
/// <para>
/// <b>A test should justify itself by what it checks, not by what currently
/// depends on it</b> — dependants move, and a rationale that names one rots the
/// moment it does. <c>moving_platform</c> is a real GG2 entity type this port
/// knows (<see cref="EntityTypes.MovingPlatform"/>) and implements nothing for;
/// that is reason enough to notice if a map ever starts placing one.
/// </para>
/// <para>
/// <b>The census is the point, not the platform.</b> This is
/// <see cref="MessageCoverageTests"/>'s question asked of map content: a type
/// appearing is news, and a type vanishing means a map changed under us. Either
/// direction is worth a failure, because "no shipped map does X" is load-bearing
/// for more than one decision here and nothing else checks it.
/// </para>
/// <para>
/// <b><c>gg_debug</c> is why this reads maps rather than
/// <c>Fixtures/maps.json</c>, and the reason generalises.</b> The oracle covers
/// 21 of 22 — gg_debug has no entity list in it — so a census taken from the
/// oracle would have "confirmed" this on 21 maps and been silent about the 22nd.
/// <para>
/// <b>A coverage gap is not a random sample.</b> It is the cases that were hard
/// to cover, and hard-to-cover usually means unusual — which is the same
/// property that makes a case likely to be the counterexample. <c>gg_debug</c>
/// is absent from the oracle <em>because</em> it is odd, and is worth checking
/// <em>because</em> it is odd. It contributes four entity types nothing else
/// does (<c>blueteamgate2</c>, <c>redteamgate2</c>, <c>bulletwall_horizontal</c>,
/// <c>playerwall_horizontal</c>): a hole that correlates with exoticism is a
/// systematic blind spot wearing the costume of one missing row.
/// </para>
/// <para>
/// <b>So "21 of 22 passed" is a much weaker statement than it reads as</b>, and
/// the question to ask of an instrument is not "how much does it cover?" but
/// <b>"what is it about the uncovered cases that kept them out?"</b> — because
/// that answer usually names the case you most need. This is distinct from
/// asking whether a check could fail: a check can be perfectly falsifiable and
/// pointed at the wrong 95%.
/// </para>
/// <para>
/// <b>The discriminator: is the oddity ASSERTED or EXCUSED?</b>
/// <list type="bullet">
/// <item><b>Excused</b> — the case is skipped, and the hole is enriched for
/// counterexamples. That is the <c>gg_debug</c> entity gap in
/// <c>Fixtures/maps.json</c> that this class exists to route around.</item>
/// <item><b>Asserted</b> — the case is carried, with its own different expected
/// values. That is
/// <c>WalkmaskTests.MapBoundsMatchTheWalkmaskOnEveryMapButGgDebug</c>, which
/// names the exception in its own title and then asserts gg_debug's 720x384 and
/// that its walkmask width does NOT match. <b>That is coverage of a second
/// behaviour and is better than a uniform check weakened until the odd case
/// fits.</b></item>
/// </list>
/// <b>A rule that flagged every named exception would be worse than no
/// rule</b> — work with no yield, then ignored, then unavailable on the day it
/// mattered. Run over this suite it found one real hole and one look-alike
/// already handled well, no third — which is what a good rule's yield looks
/// like, worth saying because the instinctive test of a rule is "did it find a
/// lot", which selects for rules that fire too often.
/// </para>
/// </remarks>
public sealed class ShippedEntityCensusTests
{
    /// <summary>
    /// Every entity type the shipped maps place, with its total, measured over
    /// all 22.
    /// </summary>
    private static readonly (string Type, int Count)[] Expected =
    [
        ("ArenaControlPoint", 2),
        ("CapturePoint", 30),
        ("GeneratorBlue", 1),
        ("GeneratorRed", 1),
        ("KothBlueControlPoint", 2),
        ("KothControlPoint", 4),
        ("KothRedControlPoint", 2),
        ("MoveBoxDown", 6),
        ("MoveBoxUp", 9),
        ("NextAreaO", 2),
        ("SetupGate", 12),
        ("blueintel", 9),
        ("bluespawn", 116),
        ("blueteamgate", 41),
        ("blueteamgate2", 1),
        ("bulletwall", 5),
        ("bulletwall_horizontal", 1),
        ("controlPoint1", 4),
        ("controlPoint2", 4),
        ("controlPoint3", 1),
        ("controlPoint4", 1),
        ("controlPoint5", 1),
        ("dropdownPlatform", 6),
        ("leftdoor", 7),
        ("medCabinet", 56),
        ("pitfall", 6),
        ("playerwall", 13),
        ("playerwall_horizontal", 1),
        ("redintel", 9),
        ("redspawn", 113),
        ("redteamgate", 47),
        ("redteamgate2", 1),
        ("rightdoor", 7),
        ("spawnroom", 75),
    ];

    private static SortedDictionary<string, int> Census()
    {
        var maps = Directory.GetFiles(ReferenceTree.MapsDirectory, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            // A SHIPPED-map census: the port-authored maps are deliberately
            // outside it, or adding one would silently change what "no shipped
            // map does X" means. GgDebug2MapTests carries gg_debug2's own
            // exact census instead.
            .Where(n => !ReferenceTree.PortAuthoredMaps.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Asserted, because the census below is a statement about a map SET.
        // Losing a map would quietly shrink every count and still look tidy.
        Assert.Equal(22, maps.Length);

        var census = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var name in maps)
        {
            var map = LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

            foreach (var entity in map.Entities)
            {
                census.TryGetValue(entity.Type, out var count);
                census[entity.Type] = count + 1;
            }
        }

        return census;
    }

    /// <summary>
    /// No shipped map places a <c>moving_platform</c>.
    /// </summary>
    /// <remarks>
    /// Its own test rather than a line of the census, because the census answers
    /// "did anything change" and this answers a specific question that has been
    /// asked once and will be again: <b>does this port have to simulate moving
    /// platforms?</b> Today it does not, because nothing places one. <b>If this
    /// ever fails the answer has changed</b> — a shipped map now needs behaviour
    /// this port does not implement, and <see cref="EntityTypes.MovingPlatform"/>
    /// being a known type is not the same as it being a simulated one.
    /// </remarks>
    [Fact]
    public void NoShippedMapPlacesAMovingPlatform()
    {
        var placed = Census().TryGetValue(EntityTypes.MovingPlatform, out var count) ? count : 0;

        Assert.True(
            placed == 0,
            $"{placed} moving_platform(s) are now placed by a shipped map. Anything "
            + "resting on 'no shipped map places one' needs re-checking rather than "
            + "re-explaining — and this port would owe the behaviour itself.");
    }

    /// <summary>
    /// The shipped maps place exactly the entities they placed when measured.
    /// </summary>
    /// <remarks>
    /// Both directions on purpose, the way <see cref="MessageCoverageTests"/>
    /// does it. A new type is news and should be read as such; a vanished one
    /// means a map moved under this port, which after 2026-09-01 also means
    /// <c>maps/</c> and the reference have diverged — see
    /// <see cref="MapCopyTests"/>, which says which.
    /// </remarks>
    [Fact]
    public void TheShippedMapsPlaceExactlyTheseEntities()
    {
        var census = Census();

        var appeared = census.Keys.Except(Expected.Select(e => e.Type)).ToArray();
        var vanished = Expected.Select(e => e.Type).Except(census.Keys).ToArray();

        Assert.True(
            appeared.Length == 0,
            "a shipped map now places entity type(s) none used to: " + string.Join(", ", appeared));

        Assert.True(
            vanished.Length == 0,
            "no shipped map places these any more: " + string.Join(", ", vanished));

        foreach (var (type, count) in Expected)
            Assert.Equal(count, census[type]);
    }
}
