using System.Globalization;

namespace GG2.Maps;

/// <summary>
/// One entry of a map's <c>{ENTITIES}</c> block: a type name, a world position,
/// a scale, and whatever extra properties that type defines.
/// </summary>
/// <remarks>
/// Deliberately not a class hierarchy. The entity block is data, and the
/// consumers of it differ by phase — collision wants the rectangles, spawning
/// wants the spawn points, the gamemode layer wants the objective entities.
/// Typing it up front would mean guessing all three.
/// <para>
/// Coordinates are <b>world pixels</b>, not mask cells, and are not necessarily
/// multiples of <c>CellSize</c>.
/// </para>
/// </remarks>
public sealed class LevelEntity
{
    public required string Type { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    /// <summary>
    /// Horizontal scale, defaulting to 1. GM8's <c>image_xscale</c>, applied
    /// only when the property is present
    /// (<c>CustomMapCreateEntitiesFromEntityData.gml</c>).
    /// </summary>
    public double XScale { get; init; } = 1;

    /// <summary>Vertical scale, defaulting to 1.</summary>
    public double YScale { get; init; } = 1;

    /// <summary>
    /// Every key/value pair as written, including <c>type</c>, <c>x</c>,
    /// <c>y</c> and the scales.
    /// </summary>
    /// <remarks>
    /// GGON has no numbers, so these are all strings — a per-type reader parses
    /// what it needs. The reference does the same thing in each entity's
    /// <c>event_user(1)</c>.
    /// </remarks>
    public required IReadOnlyDictionary<string, GgonValue> Properties { get; init; }

    /// <summary>
    /// Whether <see cref="Type"/> is one the reference's map builder registers.
    /// </summary>
    /// <remarks>
    /// The game silently ignores unknown types
    /// (<c>getEntityData</c> returns -1). This port keeps them and flags them
    /// instead, so a map that fails to behave can be diagnosed rather than
    /// quietly missing a wall.
    /// </remarks>
    public bool IsKnownType => EntityTypes.IsKnown(Type);

    /// <summary>
    /// A property as a GML <c>real</c>, or <paramref name="fallback"/> if it is
    /// absent or is a map rather than a string.
    /// </summary>
    public double GetReal(string key, double fallback = 0) =>
        Properties.TryGetValue(key, out var value) && value.AsString is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            ? real
            : fallback;

    /// <summary>A property as a string, or null.</summary>
    public string? GetString(string key) =>
        Properties.TryGetValue(key, out var value) ? value.AsString : null;

    public override string ToString() =>
        $"{Type} at ({X.ToString(CultureInfo.InvariantCulture)}, {Y.ToString(CultureInfo.InvariantCulture)})";
}
