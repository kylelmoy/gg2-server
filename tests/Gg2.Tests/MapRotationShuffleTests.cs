using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// <see cref="MapRotationShuffle"/>: <c>GameServerCreate.gml:92-108</c>.
/// </summary>
public sealed class MapRotationShuffleTests
{
    private static readonly string[] Rotation =
    [
        "ctf_truefort", "arena_montane", "koth_valley", "arena_lumberyard",
        "ctf_2dfort", "cp_egypt", "dkoth_atalia", "gen_destroy",
    ];

    [Fact]
    public void NoneReturnsTheRotationUntouched()
    {
        var ordered = MapRotationShuffle.Apply(Rotation, ShuffleMode.None, new Random(1));
        Assert.Equal(Rotation, ordered);
    }

    /// <summary>
    /// A shuffle keeps every entry exactly once and does eventually reorder.
    /// </summary>
    /// <remarks>
    /// <b>"It reordered" cannot be asserted on one seed</b> — a shuffle is
    /// allowed to return the identity, and a test that demanded otherwise would
    /// be flaky by construction rather than wrong. Over a hundred seeds an
    /// implementation that never permutes is what fails, which is the property
    /// actually worth having.
    /// </remarks>
    [Fact]
    public void AShuffleIsAPermutationAndIsNotTheIdentity()
    {
        var moved = 0;

        for (var seed = 0; seed < 100; seed++)
        {
            var shuffled = MapRotationShuffle.Apply(Rotation, ShuffleMode.Any, new Random(seed));

            Assert.Equal(Rotation.Length, shuffled.Count);
            Assert.Equal(Rotation.Order(), shuffled.Order());

            if (!shuffled.SequenceEqual(Rotation))
                moved++;
        }

        Assert.True(moved > 90, $"only {moved} of 100 seeds reordered the rotation");
    }

    /// <summary>
    /// <c>AvoidArenaFirst</c> never opens on an arena map when one other map
    /// exists — the reason the mode is the reference's default.
    /// </summary>
    /// <remarks>
    /// An arena round is one life each with no respawns, so a server that opens
    /// on one greets its first two joiners with a round that ends the moment one
    /// of them dies.
    /// </remarks>
    [Fact]
    public void AvoidArenaFirstNeverOpensOnAnArenaMap()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var shuffled = MapRotationShuffle.Apply(
                Rotation, ShuffleMode.AvoidArenaFirst, new Random(seed));

            Assert.False(
                shuffled[0].StartsWith("arena_", StringComparison.Ordinal),
                $"seed {seed} opened on {shuffled[0]}");

            // Still a permutation: the swap must not drop or duplicate a map.
            Assert.Equal(Rotation.Order(), shuffled.Order());
        }
    }

    /// <summary>
    /// <c>Any</c> really does allow an arena opener, or the two modes would be
    /// the same mode.
    /// </summary>
    [Fact]
    public void AnyAllowsAnArenaOpener()
    {
        var arenaFirst = 0;

        for (var seed = 0; seed < 200; seed++)
        {
            var shuffled = MapRotationShuffle.Apply(Rotation, ShuffleMode.Any, new Random(seed));

            if (shuffled[0].StartsWith("arena_", StringComparison.Ordinal))
                arenaFirst++;
        }

        // Two of eight are arena maps, so ~50 of 200 is the expectation; the
        // assertion only needs it to be non-zero to tell the modes apart.
        Assert.True(arenaFirst > 0, "Any never opened on an arena map in 200 seeds");
    }

    /// <summary>
    /// An all-arena rotation opens on arena, exactly as the reference does.
    /// </summary>
    /// <remarks>
    /// The GML swap loop finds nothing to swap and leaves slot 0 alone. There is
    /// no error and no refusal — an arena-only server is a thing people run.
    /// </remarks>
    [Fact]
    public void AnAllArenaRotationStillOpensOnArena()
    {
        string[] arenaOnly = ["arena_montane", "arena_lumberyard"];

        var shuffled = MapRotationShuffle.Apply(arenaOnly, ShuffleMode.AvoidArenaFirst, new Random(7));

        Assert.StartsWith("arena_", shuffled[0], StringComparison.Ordinal);
        Assert.Equal(arenaOnly.Order(), shuffled.Order());
    }

    /// <summary>
    /// A rotation of nought or one is returned as-is under every mode.
    /// </summary>
    [Theory]
    [InlineData(ShuffleMode.None)]
    [InlineData(ShuffleMode.AvoidArenaFirst)]
    [InlineData(ShuffleMode.Any)]
    public void ARotationTooShortToShuffleIsUnchanged(ShuffleMode mode)
    {
        Assert.Empty(MapRotationShuffle.Apply([], mode, new Random(1)));

        // Including the single arena map: there is nothing to swap it with,
        // and refusing to serve it would be this port inventing a rule.
        Assert.Equal(
            ["arena_montane"],
            MapRotationShuffle.Apply(["arena_montane"], mode, new Random(1)));
    }

    /// <summary>
    /// With no map named, an unshuffled rotation opens on its first entry.
    /// </summary>
    /// <remarks>
    /// <c>GameServerCreate.gml:110-113</c>, <c>serverGotoMap(nextMapInRotation())</c>
    /// from <c>currentMapIndex = -1</c>.
    /// </remarks>
    [Fact]
    public void AnUnshuffledRotationOpensOnItsFirstEntry()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = string.Empty;
            options.MapRotation = ["koth_valley", "ctf_2dfort", "ctf_truefort"];
            options.ShuffleRotation = ShuffleMode.None;
        });

        Assert.Equal("koth_valley", harness.Server.CurrentMap);
    }

    /// <summary>
    /// A shuffled rotation opens on one of its own entries, never on the
    /// <see cref="ServerOptions.DefaultMap"/> fallback.
    /// </summary>
    /// <remarks>
    /// Run over several servers rather than one: an opener that ignored the
    /// shuffle and took <c>rotation[0]</c> would pass a single run, and one that
    /// fell back to <c>ctf_truefort</c> would pass whenever the shuffle happened
    /// to put it first.
    /// </remarks>
    [Fact]
    public void AShuffledRotationOpensOnAnEntryOfItsOwn()
    {
        string[] rotation = ["koth_valley", "ctf_2dfort", "cp_egypt"];
        var openers = new HashSet<string>(StringComparer.Ordinal);

        for (var run = 0; run < 12; run++)
        {
            using var harness = new ServerHarness(options =>
            {
                options.Map = string.Empty;
                options.MapRotation = rotation;
                options.ShuffleRotation = ShuffleMode.Any;
            });

            Assert.Contains(harness.Server.CurrentMap, rotation);
            openers.Add(harness.Server.CurrentMap);
        }

        // Not "exactly 3": twelve draws from three maps missing one is about
        // a 1-in-2000 event, and a shuffle that never moved would give exactly 1.
        // string.Join, not openers.Single(): Assert.True's message argument is
        // evaluated whether or not the assertion holds, so Single() threw on the
        // passing path — a test that fails only when it succeeds.
        Assert.True(
            openers.Count > 1,
            $"twelve servers all opened on the same map: {string.Join(", ", openers)}");
    }

    /// <summary>A named map beats the rotation, which is <c>global.launchMap</c>.</summary>
    [Fact]
    public void ANamedMapWinsOverTheRotation()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = "ctf_truefort";
            options.MapRotation = ["koth_valley", "ctf_2dfort"];
            options.ShuffleRotation = ShuffleMode.Any;
        });

        Assert.Equal("ctf_truefort", harness.Server.CurrentMap);
    }
}
