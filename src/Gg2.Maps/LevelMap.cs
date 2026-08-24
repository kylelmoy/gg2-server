using System.Globalization;

namespace Gg2.Maps;

/// <summary>
/// A decoded GG2 map: its collision mask and its entity list.
/// </summary>
/// <remarks>
/// Nothing here touches the filesystem — the caller supplies the PNG's bytes.
/// That keeps <c>Gg2.Maps</c> pure, which is what lets the parity harness and
/// the bot iteration loop load maps with no server running
/// (docs/ARCHITECTURE.md).
/// </remarks>
public sealed class LevelMap
{
    private LevelMap(
        string name,
        Walkmask walkmask,
        IReadOnlyList<LevelEntity> entities,
        GgonMap? metadata,
        int artCellsWide,
        int artCellsHigh)
    {
        Name = name;
        Walkmask = walkmask;
        Entities = entities;
        Metadata = metadata;
        Width = artCellsWide * Gg2.Protocol.GameConstants.CellSize;
        Height = artCellsHigh * Gg2.Protocol.GameConstants.CellSize;
    }

    /// <summary>The map's name, as the caller supplied it (e.g. "ctf_truefort").</summary>
    public string Name { get; }

    public Walkmask Walkmask { get; }

    /// <summary>
    /// Entities in the order the level data lists them, which is the order the
    /// reference creates the instances in.
    /// </summary>
    /// <remarks>
    /// Creation order is observable in GM8 — instances within an event phase run
    /// in it (docs/SIMULATION.md, "GM8 semantics that will bite") — so this
    /// order is preserved rather than sorted or grouped.
    /// <para>
    /// For a GGON <em>map</em> (rather than list) entity block, "the order the
    /// level data lists them" is the order <c>ggon_map_to_list</c> produces:
    /// numeric index order, not the textual order of the keys. <c>gg_debug</c>
    /// is written that way and the two differ.
    /// </para>
    /// </remarks>
    public IReadOnlyList<LevelEntity> Entities { get; }

    /// <summary>
    /// The <c>type: meta</c> pseudo-entity, if the map has one. Carries
    /// background colour, embedded sprite resources and the background scale.
    /// </summary>
    public GgonMap? Metadata { get; }

    /// <summary>
    /// <c>map_width()</c> in world pixels — the map <b>art</b>'s width times
    /// six, not the walkmask's.
    /// </summary>
    /// <remarks>
    /// <c>Scripts/Maps/CustomMaps/map_width.gml</c> returns
    /// <c>background_width[7] * background_xscale[7]</c>, which is the
    /// background image. That is the number
    /// <c>Character.events/End Step.xml</c> clamps positions into, so it is the
    /// one the physics wants — and it is <b>not</b>
    /// <see cref="Walkmask"/>'s extent. Twenty-one of the twenty-two shipped
    /// maps have them equal, which is exactly why the difference is worth a
    /// name: <c>gg_debug</c> is 120x64 of art over a 118x69 mask, so its
    /// bounds reach past the mask horizontally and stop short of it vertically.
    /// </remarks>
    public int Width { get; }

    /// <summary><c>map_height()</c> in world pixels. See <see cref="Width"/>.</summary>
    public int Height { get; }

    /// <summary>Decodes a map from the bytes of its PNG.</summary>
    public static LevelMap FromPng(string name, ReadOnlySpan<byte> png)
    {
        var (artWidth, artHeight) = LevelData.ReadImageSize(png);
        return FromLevelData(name, LevelData.Extract(png), artWidth, artHeight);
    }

    /// <summary>
    /// Decodes a map from already-extracted level-data text.
    /// </summary>
    /// <param name="artCellsWide">
    /// The map PNG's pixel width. Defaults to the walkmask's, which is right for
    /// every shipped map but <c>gg_debug</c> — pass it when the PNG is to hand.
    /// </param>
    public static LevelMap FromLevelData(
        string name,
        string levelData,
        int artCellsWide = 0,
        int artCellsHigh = 0)
    {
        var walkmask = Walkmask.Decode(levelData);
        var (entities, metadata) = ParseEntities(LevelData.GetBlock(levelData, "ENTITIES"));

        return new LevelMap(
            name,
            walkmask,
            entities,
            metadata,
            artCellsWide > 0 ? artCellsWide : walkmask.Width,
            artCellsHigh > 0 ? artCellsHigh : walkmask.Height);
    }

    /// <summary>
    /// Parses an <c>{ENTITIES}</c> block body, in either of the two formats the
    /// reference accepts.
    /// </summary>
    /// <remarks>
    /// <c>CustomMapCreateEntitiesFromEntityData.gml</c> switches on the first
    /// character: <c>{</c> or <c>[</c> means GGON, anything else means the
    /// pre-GGON format of newline-separated <c>type / x / y</c> triples. Every
    /// map shipped with v2.9.2 is GGON; the old format is kept because
    /// player-made maps predating it still load in the stock client, and a
    /// server that rejects one the client accepts is a compatibility bug.
    /// </remarks>
    public static (IReadOnlyList<LevelEntity> Entities, GgonMap? Metadata) ParseEntities(string entityBlock)
    {
        var trimmedStart = entityBlock.AsSpan().TrimStart();
        if (trimmedStart.Length == 0)
            return ([], null);

        return trimmedStart[0] is '{' or '['
            ? ParseGgonEntities(entityBlock)
            : (ParseLegacyEntities(entityBlock), null);
    }

    private static (IReadOnlyList<LevelEntity>, GgonMap?) ParseGgonEntities(string entityBlock)
    {
        var root = Ggon.Decode(entityBlock).AsMap
            ?? throw new MapFormatException("{ENTITIES} block decoded to a string, not a list or map");

        var entities = new List<LevelEntity>();
        GgonMap? metadata = null;

        foreach (var item in root.ToList())
        {
            if (item?.AsMap is not { } properties)
                continue;

            var type = properties.GetString("type");
            if (type is null)
                continue;

            // The metadata pseudo-entity is not an instance; the reference hands
            // it to loadMetadata() after every real entity has been created.
            if (type == EntityTypes.Meta)
            {
                metadata = properties;
                continue;
            }

            entities.Add(new LevelEntity
            {
                Type = type,
                X = RequireReal(properties, "x", type),
                Y = RequireReal(properties, "y", type),
                XScale = OptionalReal(properties, "xscale", 1),
                YScale = OptionalReal(properties, "yscale", 1),
                Properties = properties.Entries,
            });
        }

        return (entities, metadata);
    }

    private static double RequireReal(GgonMap properties, string key, string type)
    {
        var text = properties.GetString(key)
            ?? throw new MapFormatException($"entity '{type}' has no {key}");

        return ParseReal(text, key, type);
    }

    private static double OptionalReal(GgonMap properties, string key, double fallback)
    {
        var text = properties.GetString(key);
        return text is null ? fallback : ParseReal(text, key, "entity");
    }

    private static double ParseReal(string text, string key, string type)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new MapFormatException($"entity '{type}' has a non-numeric {key}: '{text}'");
        return value;
    }

    /// <summary>
    /// The pre-GGON format: newline-separated triples of type, x, y, running to
    /// the end of the block.
    /// </summary>
    /// <remarks>
    /// The reference implements this with a <c>string_replace</c> walk that is
    /// hard to read but amounts to "take three newline-delimited fields at a
    /// time". A trailing partial triple is dropped, which is what its
    /// <c>string_pos(DIVIDER, ...) != 0</c> loop condition does.
    /// </remarks>
    private static List<LevelEntity> ParseLegacyEntities(string entityBlock)
    {
        var fields = entityBlock.Split('\n');
        var entities = new List<LevelEntity>();

        for (var i = 0; i + 2 < fields.Length; i += 3)
        {
            var type = fields[i];
            if (type.Length == 0)
                break;

            entities.Add(new LevelEntity
            {
                Type = type,
                X = ParseReal(fields[i + 1], "x", type),
                Y = ParseReal(fields[i + 2], "y", type),
                Properties = new Dictionary<string, GgonValue>(StringComparer.Ordinal)
                {
                    ["type"] = new GgonString(type),
                    ["x"] = new GgonString(fields[i + 1]),
                    ["y"] = new GgonString(fields[i + 2]),
                },
            });
        }

        return entities;
    }

    /// <summary>Every entity of the given type, in level-data order.</summary>
    public IEnumerable<LevelEntity> OfType(string type) =>
        Entities.Where(e => string.Equals(e.Type, type, StringComparison.Ordinal));
}
