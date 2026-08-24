using Gg2.Maps;

namespace Gg2.Tests;

/// <summary>
/// The <c>{ENTITIES}</c> half of phase 0.
/// </summary>
/// <remarks>
/// The oracle here (<c>gg2-agent/tools/navgraph.js</c>) splits GGON with a
/// regex instead of parsing it, so it is a cross-check rather than a
/// specification: it agrees on the shipped list-form maps, which is enough to
/// catch an off-by-one or a dropped entity, and it cannot read <c>gg_debug</c>
/// at all. The parser itself is checked against the reference's own grammar in
/// <see cref="GgonTests"/>.
/// </remarks>
public sealed class LevelEntityTests
{
    public static TheoryData<MapFixture> Maps => MapFixtures.AsTheoryData();

    [Theory]
    [MemberData(nameof(Maps))]
    public void MatchesTheEntityOracle(MapFixture expected)
    {
        if (expected.Entities is null)
            return; // No oracle for this map; covered by GgDebugEntitiesAreReadInIndexOrder.

        var map = LevelMap.FromPng(expected.Name, ReferenceTree.ReadMap(expected.Name));

        Assert.Equal(expected.Entities.Length, map.Entities.Count);

        for (var i = 0; i < expected.Entities.Length; i++)
        {
            var want = expected.Entities[i];
            var got = map.Entities[i];

            Assert.Equal(want.Type, got.Type);
            Assert.Equal(want.X, got.X);
            Assert.Equal(want.Y, got.Y);
            Assert.Equal(want.XScale, got.XScale);
            Assert.Equal(want.YScale, got.YScale);
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void EveryShippedEntityTypeIsRegistered(MapFixture expected)
    {
        var map = LevelMap.FromPng(expected.Name, ReferenceTree.ReadMap(expected.Name));

        // A shipped map should never contain a type the builder does not
        // register — if it does, either the transcription of builder_init.gml is
        // incomplete or the reference has moved.
        var unknown = map.Entities.Where(e => !e.IsKnownType).Select(e => e.Type).Distinct().ToArray();
        Assert.Empty(unknown);
    }

    /// <summary>
    /// <c>gg_debug</c> is the one shipped map whose entity block is a GGON
    /// <em>map</em> rather than a list, so its entities come back in numeric
    /// index order — which is not the order the keys appear in the text.
    /// </summary>
    [Fact]
    public void GgDebugEntitiesAreReadInIndexOrder()
    {
        var map = LevelMap.FromPng("gg_debug", ReferenceTree.ReadMap("gg_debug"));

        Assert.Equal(18, map.Entities.Count);

        // Keys 0, 1, 2 come first even though the text lists 0, 1, 10, 11, ...
        Assert.Equal(EntityTypes.SpawnRoom, map.Entities[0].Type);
        Assert.Equal(558, map.Entities[0].X);
        Assert.Equal(108, map.Entities[0].Y);

        Assert.Equal(EntityTypes.SpawnRoom, map.Entities[2].Type);
        Assert.Equal(600, map.Entities[2].X);
        Assert.Equal(150, map.Entities[2].Y);

        Assert.Equal(EntityTypes.RedSpawn, map.Entities[5].Type);
        Assert.Equal(EntityTypes.BlueSpawn, map.Entities[6].Type);
        Assert.Equal(EntityTypes.RedTeamGate, map.Entities[7].Type);
        Assert.Equal(EntityTypes.DropdownPlatform, map.Entities[16].Type);
    }

    /// <summary>
    /// Every CTF map must have both intel spawns, every KOTH map its control
    /// point. A sanity check on the whole pipeline rather than on the parser:
    /// if the entity list came back empty or scrambled these would not hold.
    /// </summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public void ObjectiveEntitiesArePresentForTheMapPrefix(MapFixture expected)
    {
        var map = LevelMap.FromPng(expected.Name, ReferenceTree.ReadMap(expected.Name));

        if (expected.Name.StartsWith("ctf_", StringComparison.Ordinal))
        {
            Assert.NotEmpty(map.OfType(EntityTypes.RedIntel));
            Assert.NotEmpty(map.OfType(EntityTypes.BlueIntel));
        }

        if (expected.Name.StartsWith("koth_", StringComparison.Ordinal))
            Assert.NotEmpty(map.OfType(EntityTypes.KothControlPoint));

        if (expected.Name.StartsWith("dkoth_", StringComparison.Ordinal))
        {
            Assert.NotEmpty(map.OfType(EntityTypes.KothRedControlPoint));
            Assert.NotEmpty(map.OfType(EntityTypes.KothBlueControlPoint));
        }

        if (expected.Name.StartsWith("gen_", StringComparison.Ordinal))
        {
            Assert.NotEmpty(map.OfType(EntityTypes.GeneratorRed));
            Assert.NotEmpty(map.OfType(EntityTypes.GeneratorBlue));
        }

        // Every playable map needs somewhere for both teams to appear.
        if (expected.Name != "gg_debug")
        {
            Assert.NotEmpty(map.OfType(EntityTypes.RedSpawn));
            Assert.NotEmpty(map.OfType(EntityTypes.BlueSpawn));
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void MetadataIsNotAnEntity(MapFixture expected)
    {
        var map = LevelMap.FromPng(expected.Name, ReferenceTree.ReadMap(expected.Name));

        Assert.DoesNotContain(map.Entities, e => e.Type == EntityTypes.Meta);
    }

    [Fact]
    public void ScalesDefaultToOneWhenAbsent()
    {
        var (entities, metadata) = LevelMap.ParseEntities("[{type:playerwall,x:10,y:20}]");

        var entity = Assert.Single(entities);
        Assert.Equal(1, entity.XScale);
        Assert.Equal(1, entity.YScale);
        Assert.Null(metadata);
    }

    [Fact]
    public void MetadataIsSeparatedFromEntities()
    {
        var (entities, metadata) = LevelMap.ParseEntities(
            "[{background:ffffff,type:meta},{type:redspawn,x:1,y:2}]");

        Assert.Single(entities);
        Assert.NotNull(metadata);
        Assert.Equal("ffffff", metadata.GetString("background"));
    }

    /// <summary>
    /// The pre-GGON format: newline-separated <c>type / x / y</c> triples. No
    /// shipped map uses it, but the stock client still loads maps that do, and
    /// a server that rejects one the client accepts is a compatibility bug.
    /// </summary>
    [Fact]
    public void LegacyTripleFormatIsAccepted()
    {
        var (entities, metadata) = LevelMap.ParseEntities(
            string.Join('\n', "redspawn", "120", "240", "bluespawn", "300", "240"));

        Assert.Null(metadata);
        Assert.Equal(2, entities.Count);

        Assert.Equal(EntityTypes.RedSpawn, entities[0].Type);
        Assert.Equal(120, entities[0].X);
        Assert.Equal(240, entities[0].Y);

        Assert.Equal(EntityTypes.BlueSpawn, entities[1].Type);
        Assert.Equal(300, entities[1].X);
    }

    [Fact]
    public void EmptyEntityBlockIsNotAnError()
    {
        var (entities, metadata) = LevelMap.ParseEntities("");

        Assert.Empty(entities);
        Assert.Null(metadata);
    }
}
