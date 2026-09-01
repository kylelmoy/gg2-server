using Gg2.Maps;

namespace Gg2.Tests;

/// <summary>
/// Which level entities the 22 shipped maps actually place, and how many.
/// </summary>
/// <remarks>
/// ⚠️ <b>"What do the shipped maps never contain?" is the question that licenses
/// a simulation stage doing nothing</b>, and until this existed it was answered
/// by looking. <c>SimulationStamp.SimulationBehaviourVersion</c> went 1 → 2 on
/// 2026-09-01 for moving platforms, noting that <b>no shipped map places one</b>
/// — and its author was right to bump anyway, on the grounds that <em>"I could
/// not find a map that reaches it" is reasoning, not a demonstration</em>. This
/// is the demonstration. Checked across all 22 maps: <c>moving_platform</c>
/// appears zero times.
/// <para>
/// ⚠️ <b>The census is the point, not the platform.</b> This is
/// <see cref="MessageCoverageTests"/>'s question asked of map content: a type
/// appearing is news, and a type vanishing means a map changed under us. Either
/// direction is worth a failure, because "no shipped map does X" is load-bearing
/// for more than one decision here and nothing else checks it.
/// </para>
/// <para>
/// ⚠️ <b><c>gg_debug</c> is why this reads maps rather than
/// <c>Fixtures/maps.json</c>.</b> The oracle covers 21 of 22 — gg_debug has no
/// entity list in it — and gg_debug is the map most likely to place something
/// exotic, being the debug map. It contributes four types nothing else does
/// (<c>blueteamgate2</c>, <c>redteamgate2</c>, <c>bulletwall_horizontal</c>,
/// <c>playerwall_horizontal</c>). A census taken from the oracle would have
/// missed exactly the map worth checking.
/// </para>
/// </remarks>
public sealed class ShippedEntityCensusTests
{
    /// <summary>
    /// Every entity type the shipped maps place, with its total, measured
    /// 2026-09-01 over all 22.
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
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // ⚠️ Asserted, because the census below is a statement about a map SET.
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
    /// ⚠️ <b>No shipped map places a <c>moving_platform</c>.</b>
    /// </summary>
    /// <remarks>
    /// Its own test rather than a line of the census, because this one is
    /// *depended on*: it is the reason <c>MovingPlatforms.EndStep</c> does
    /// nothing on any shipped map, and therefore the reason the stamp bump that
    /// added it was expected to be behaviour-neutral in practice. ⚠️ <b>If this
    /// ever fails, that expectation is void</b> — a map now reaches the stage,
    /// and anything that assumed the stage was inert has to be re-checked rather
    /// than re-explained.
    /// </remarks>
    [Fact]
    public void NoShippedMapPlacesAMovingPlatform()
    {
        var placed = Census().TryGetValue(EntityTypes.MovingPlatform, out var count) ? count : 0;

        Assert.True(
            placed == 0,
            $"{placed} moving_platform(s) are now placed by a shipped map. "
            + "MovingPlatforms.EndStep is no longer inert on the shipped set, so anything "
            + "resting on 'no shipped map places one' — SimulationStamp's behaviour-2 note "
            + "among them — needs re-checking rather than re-explaining.");
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
