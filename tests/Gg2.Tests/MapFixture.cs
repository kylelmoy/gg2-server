using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gg2.Tests;

/// <summary>
/// The checked-in oracle for phase 0: what <c>gg2-agent</c>'s decoders say each
/// built-in map contains.
/// </summary>
/// <remarks>
/// Regenerate with <c>node tools/gen-map-fixture.js</c>. That script's header
/// explains which of the two oracles is strong and which is approximate.
/// </remarks>
public sealed record MapFixture
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("width")] public required int Width { get; init; }

    [JsonPropertyName("height")] public required int Height { get; init; }

    [JsonPropertyName("solidCells")] public required int SolidCells { get; init; }

    /// <summary>SHA-256 of one byte per cell, row-major, 1 where solid.</summary>
    [JsonPropertyName("cellsSha256")] public required string CellsSha256 { get; init; }

    /// <summary>
    /// Null for maps whose entity block the oracle cannot read — see the
    /// generator's <c>NO_ENTITY_ORACLE</c>.
    /// </summary>
    [JsonPropertyName("entities")] public EntityFixture[]? Entities { get; init; }

    public override string ToString() => Name;
}

public sealed record EntityFixture
{
    [JsonPropertyName("type")] public required string Type { get; init; }

    [JsonPropertyName("x")] public required double X { get; init; }

    [JsonPropertyName("y")] public required double Y { get; init; }

    [JsonPropertyName("xscale")] public required double XScale { get; init; }

    [JsonPropertyName("yscale")] public required double YScale { get; init; }

    public override string ToString() => $"{Type} at ({X}, {Y}) scale ({XScale}, {YScale})";
}

public static class MapFixtures
{
    private sealed record Document(
        [property: JsonPropertyName("maps")] MapFixture[] Maps);

    public static IReadOnlyList<MapFixture> All { get; } = Load();

    /// <summary>Every fixture, as xunit theory data.</summary>
    public static TheoryData<MapFixture> AsTheoryData()
    {
        var data = new TheoryData<MapFixture>();
        foreach (var fixture in All)
            data.Add(fixture);
        return data;
    }

    private static IReadOnlyList<MapFixture> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "maps.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"map fixture missing at {path}; run tools/gen-map-fixture.js", path);

        var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException($"{path} is empty");

        return document.Maps;
    }
}
