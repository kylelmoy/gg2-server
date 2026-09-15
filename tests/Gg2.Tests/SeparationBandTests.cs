using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// Sampled placements that are within engagement range of each other.
/// </summary>
/// <remarks>
/// Sampling from spawn points is the right default — it is what a real respawn
/// does — but it fails two ways:
/// <list type="bullet">
/// <item><c>gg_debug</c> has ONE spawn point per team, so every sampled duel is
/// the same duel regardless of seed.</item>
/// <item>A real map gives variety but no engagement: at spawn separation no
/// candidate can reach damage inside a 20-tick horizon, so every candidate
/// scores zero and the tie goes to the first.</item>
/// </list>
/// <see cref="DuelSpec.MaxSeparation"/> is the answer: draw from the nav graph's
/// walkable nodes, gate-filtered per team, paired inside a band.
/// </remarks>
public sealed class SeparationBandTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static DuelSpec Banded(string map, double min, double max) => new()
    {
        Map = map,
        RedClass = PlayerClass.Soldier,
        BlueClass = PlayerClass.Soldier,
        RedX = DuelSpec.Sampled,
        RedY = DuelSpec.Sampled,
        BlueX = DuelSpec.Sampled,
        BlueY = DuelSpec.Sampled,
        DecisionCap = 120,
        ActionRepeat = 4,
        MinSeparation = min,
        MaxSeparation = max,
    };

    private static DuelEnv Env() =>
        new(ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    private static double Separation(DuelEnv env)
    {
        ref readonly var red = ref env.World.State.Characters[env.RedSlot];
        ref readonly var blue = ref env.World.State.Characters[env.BlueSlot];

        return Gml.PointDistance(red.X, red.Y, blue.X, blue.Y);
    }

    /// <summary>
    /// A banded placement lands inside its band, on every shape of map.
    /// </summary>
    /// <remarks>
    /// <c>koth_harvest</c>, <c>ctf_2dfort</c>, <c>koth_valley</c> and
    /// <c>dkoth_atalia</c> are included because a spawn-sampled pair on them starts
    /// behind their own team gates and cannot route to one another, so they engage
    /// 0 of 8 sampled duels — the case banding exists to fix.
    /// </remarks>
    [Theory]
    [InlineData("koth_harvest")]
    [InlineData("ctf_2dfort")]
    [InlineData("koth_valley")]
    [InlineData("dkoth_atalia")]
    [InlineData("gg_debug")]
    [InlineData("ctf_truefort")]
    [InlineData("arena_lumberyard")]
    public void ABandedPlacementLandsInsideItsBand(string map)
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var env = Env();

        for (var seed = 1u; seed <= 24; seed++)
        {
            env.Reset(Banded(map, 120, 400), seed);

            var apart = Separation(env);

            Assert.True(
                apart is >= 120 and <= 400,
                $"{map} seed {seed}: placed {apart:0.#}px apart, outside 120-400");

            Assert.True(env.World.State.IsAlive(env.RedSlot));
            Assert.True(env.World.State.IsAlive(env.BlueSlot));

            // Standing on it, not falling past it. A nav node is a walkable
            // surface, so a placement that needs gravity to find the floor means
            // the stand y is wrong — and a duel that starts with a fall is a
            // duel whose first second is not a fight.
            env.World.Step(30);

            Assert.True(env.World.State.IsAlive(env.RedSlot), $"{map} seed {seed}: red died on placement");
            Assert.True(env.World.State.IsAlive(env.BlueSlot), $"{map} seed {seed}: blue died on placement");
        }
    }

    /// <summary>
    /// The half that spawn-point sampling could not give: variety.
    /// </summary>
    /// <remarks>
    /// On <c>gg_debug</c> spawn sampling has one point per team, so twenty-four
    /// seeds produce one duel twenty-four times. The control is exactly that: the
    /// same map and the same seeds with the band off.
    /// </remarks>
    [Fact]
    public void ABandedDrawVariesWithTheSeedWhereSpawnSamplingDoesNot()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var env = Env();
        var banded = new HashSet<(double, double, double, double)>();
        var spawned = new HashSet<(double, double, double, double)>();

        for (var seed = 1u; seed <= 24; seed++)
        {
            env.Reset(Banded("gg_debug", 120, 400), seed);
            banded.Add(Positions(env));

            env.Reset(Banded("gg_debug", 0, 0) with { MinSeparation = 0, MaxSeparation = 0 }, seed);
            spawned.Add(Positions(env));
        }

        output.WriteLine($"gg_debug over 24 seeds: banded {banded.Count} distinct, spawn-sampled {spawned.Count}");

        // The control, and it is the measured complaint: one spawn point per team
        // means one duel however many seeds are spent on it.
        Assert.Single(spawned);

        Assert.True(
            banded.Count >= 8,
            $"a banded draw gave only {banded.Count} distinct placements over 24 seeds; that is not variety");

        static (double, double, double, double) Positions(DuelEnv env) =>
            (env.World.State.Characters[env.RedSlot].X,
             env.World.State.Characters[env.RedSlot].Y,
             env.World.State.Characters[env.BlueSlot].X,
             env.World.State.Characters[env.BlueSlot].Y);
    }

    /// <summary>
    /// The half that variety could not give: engagement. Two scripted
    /// bots placed in a band actually fight; placed on spawn points they do not.
    /// </summary>
    /// <remarks>
    /// Measured as damage dealt.
    /// <para>
    /// The pinned numbers are a floor and a ceiling, not an equality.
    /// A fight's exact damage depends on every weapon draw in it; what is being
    /// asserted is the difference between fighting and not, which is the size of
    /// the gap rather than its digits.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ctf_2dfort")]
    [InlineData("koth_harvest")]
    [InlineData("arena_lumberyard")]
    public void BandedPlacementsFightAndSpawnPlacementsDoNot(string map)
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var banded = Damage(map, Banded(map, 150, 350));
        var spawned = Damage(map, Banded(map, 0, 0) with { MinSeparation = 0, MaxSeparation = 0 });

        output.WriteLine($"{map}: banded {banded} damage over 8 duels, spawn-sampled {spawned}");

        Assert.True(banded > 0, $"{map}: a banded pair of scripted bots dealt no damage in 8 duels");

        Assert.True(
            banded > spawned * 2,
            $"{map}: banded {banded} is not decisively more than spawn-sampled {spawned}");

        // Pinned exactly: this gates the DRAW rather than the fight. Everything
        // here is seeded — the world's GmlRandom, the scripted bots' streams, the
        // physics — so the total is a pure function of the sequence of
        // Random.Next calls the placement makes.
        //
        // A failure here is the RE-RECORD, and it means the placement draw
        // moved — which makes every duel figure taken against the old one
        // incomparable. Say so in the commit.
        // Re-recorded when BotObjective stopped re-deciding a spot from the bot's
        // live position (BotGoalSpot.Find, the sticky-node block). All three totals
        // rose — ctf_2dfort 1313.33 -> 1536.15, arena_lumberyard 1241.45 -> 1343.14,
        // koth_harvest 791.14 -> 1007.01 — which is the direction that change
        // predicts: a bot that is not walking back and forth between two candidate
        // spots spends the time fighting instead. Scripted duels reaching a death
        // went 18/24 to 22/24 over the same change.
        var expected = map switch
        {
            "ctf_2dfort" => 1478.6076793534305,
            "arena_lumberyard" => 1343.1363440377258,
            "koth_harvest" => 1007.0099686656572,
            _ => throw new ArgumentOutOfRangeException(nameof(map), map, "no recorded total for this map"),
        };

        Assert.Equal(expected, banded);
    }

    /// <summary>Total damage over eight duels of two scripted bots.</summary>
    private static double Damage(string map, DuelSpec spec)
    {
        var env = Env();
        var total = 0.0;

        spec = spec with
        {
            RedDriver = EnvDriver.Scripted,
            BlueDriver = EnvDriver.Scripted,
            RedTier = 5,
            BlueTier = 5,
            DecisionCap = 90,
        };

        for (var seed = 1u; seed <= 8; seed++)
        {
            env.Reset(spec, seed);

            for (var i = 0; i < spec.DecisionCap; i++)
            {
                var (red, blue) = env.Step(EnvAction.Idle, EnvAction.Idle);
                total += red.DamageDealt + blue.DamageDealt;

                if (red.Done)
                    break;
            }
        }

        return total;
    }

    /// <summary>
    /// Every way a band can be half-specified is refused, loudly.
    /// </summary>
    /// <remarks>
    /// Refused rather than ignored, for the same reason as the half-NaN case
    /// <see cref="DuelSpec.Sampled"/> already makes: silently ignoring a band
    /// gives back exactly the spawn-point sampling the band exists to replace,
    /// which is the failure it was asked for.
    /// </remarks>
    [Fact]
    public void AHalfSpecifiedBandIsRefused()
    {
        var explicitPlacement = Banded("gg_debug", 100, 300) with { RedX = 600, RedY = 850 };
        Assert.Throws<ArgumentException>(explicitPlacement.Validate);

        var inverted = Banded("gg_debug", 400, 100);
        Assert.Throws<ArgumentException>(inverted.Validate);

        var negative = Banded("gg_debug", -1, 100);
        Assert.Throws<ArgumentOutOfRangeException>(negative.Validate);

        // A minimum without a maximum does not switch banding on, so it would
        // be read as "sample spawn points, at least 200px apart" — which nothing
        // enforces. Saying so beats honouring half of it.
        var floorOnly = Banded("gg_debug", 200, 0);
        Assert.Throws<ArgumentException>(floorOnly.Validate);
    }

    /// <summary>
    /// A band without a nav graph is refused, not downgraded to spawn points.
    /// </summary>
    /// <remarks>
    /// The same rule <see cref="EnvDriver.Scripted"/> follows, for the same
    /// reason: answering a request with the exact behaviour it asked to replace
    /// is worse than answering it with an error.
    /// </remarks>
    [Fact]
    public void ABandWithoutANavGraphIsRefused()
    {
        ReferenceTree.RequireAvailable();

        var env = new DuelEnv(ReferenceTree.IncludedFiles, null);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => env.Reset(Banded("gg_debug", 120, 400), seed: 1));

        Assert.Contains("nav graph", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A band no map can satisfy fails with the band in the message rather than
    /// looping or quietly widening.
    /// </summary>
    [Fact]
    public void AnUnsatisfiableBandSaysSo()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var env = Env();

        var thrown = Assert.Throws<InvalidOperationException>(
            () => env.Reset(Banded("gg_debug", 90_000, 100_000), seed: 1));

        Assert.Contains("90000", thrown.Message, StringComparison.Ordinal);
    }
}
