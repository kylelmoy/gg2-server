using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <c>docs/ENV.md</c>'s <c>MakeBatch</c> row promised "explicit positions <b>or
/// sampled</b>" from the first draft, and only the first half existed.
/// </summary>
/// <remarks>
/// ⚠️ <b>A documented capability that was never built is the same trap as a
/// gap recorded only in a code comment</b> — <c>CLAUDE.md</c> names the second
/// and this is the first. Nothing was wrong with the code; the document was
/// describing a surface that did not exist, and a reader who trusted it wrote a
/// trainer around it.
/// <para>
/// <b>What it cost, measured by <c>gg2-rl</c> before it was built:</b> a duel on
/// any map required the caller to know that map's coordinates. Driving
/// <c>gg_debug</c>'s (600,850) at four other maps, 8 worlds, 199 steps:
/// <c>gg_debug</c>, <c>arena_lumberyard</c> and <c>koth_harvest</c> ran every
/// episode to the cap with no deaths, <c>ctf_2dfort</c> killed intermittently,
/// and <b><c>tdm_mantic</c> killed both characters every episode</b> — 32 deaths
/// across 16, exactly 2.0 each. That is the map's six pit falls, not combat, and
/// a trainer scoring damage would have learned to avoid the map rather than to
/// fight.
/// </para>
/// </remarks>
public sealed class SampledSpawnTests
{
    private static DuelSpec Spec(string map, bool sampled) => new()
    {
        Map = map,
        RedClass = PlayerClass.Soldier,
        BlueClass = PlayerClass.Soldier,
        RedX = sampled ? DuelSpec.Sampled : 600,
        RedY = sampled ? DuelSpec.Sampled : 850,
        BlueX = sampled ? DuelSpec.Sampled : 700,
        BlueY = sampled ? DuelSpec.Sampled : 850,
        DecisionCap = 120,
        ActionRepeat = 4,
    };

    private static DuelEnv Env() => new(ReferenceTree.IncludedFiles, null);

    /// <summary>
    /// ✅ <b>A sampled duel stands on a real spawn point on every shipped map</b>,
    /// and needs no coordinate from the caller.
    /// </summary>
    /// <remarks>
    /// Asserted on the map that used to be lethal plus one of each shape. The
    /// claim is deliberately weak — both characters exist and are on their feet
    /// after a second — because the strong version ("a good duel starts here")
    /// is a judgement no test can hold.
    /// </remarks>
    [Theory]
    [InlineData("tdm_mantic")]
    [InlineData("gg_debug")]
    [InlineData("ctf_truefort")]
    [InlineData("koth_harvest")]
    [InlineData("arena_lumberyard")]
    public void ASampledDuelStartsOnItsFeetAnywhere(string map)
    {
        var env = Env();
        env.Reset(Spec(map, sampled: true), seed: 4);

        Assert.True(env.World.State.IsAlive(env.RedSlot));
        Assert.True(env.World.State.IsAlive(env.BlueSlot));

        // Thirty ticks of gravity and nothing else: enough to fall into a pit if
        // the placement put somebody over one.
        env.World.Step(30);

        Assert.True(env.World.State.IsAlive(env.RedSlot), $"{map}: red died on its own spawn");
        Assert.True(env.World.State.IsAlive(env.BlueSlot), $"{map}: blue died on its own spawn");
    }

    /// <summary>
    /// ⚠️ <b>The control, and the reason this was built:</b> the explicit
    /// coordinates a caller would have had to guess kill both characters on
    /// <c>tdm_mantic</c>.
    /// </summary>
    /// <remarks>
    /// This is <c>gg2-rl</c>'s measurement reproduced as a test. It is pinned
    /// rather than fixed because there is nothing to fix — (600,850) is over a
    /// pit fall on that map, and a coordinate that is fine on one map being
    /// lethal on another is precisely the argument for sampling.
    /// </remarks>
    [Fact]
    public void TheExplicitCoordinatesThatWorkOnOneMapAreLethalOnAnother()
    {
        // ⚠️ 200 ticks, not 60. They are in OPEN AIR at (600,850) and the fall
        // is about 1,400 pixels: y reaches 2,267 by t150 and both die at t152,
        // together, which is what makes gg2-rl's 2.0 deaths per episode
        // environmental rather than combat. A shorter budget finds them alive
        // and mid-fall, and the first draft of this test asserted at t60 and
        // failed for that reason.
        var env = Env();
        env.Reset(Spec("tdm_mantic", sampled: false), seed: 4);
        env.World.Step(200);

        Assert.False(env.World.State.IsAlive(env.RedSlot));
        Assert.False(env.World.State.IsAlive(env.BlueSlot));

        // ...and the same map, sampled, is fine — which is the whole finding in
        // two lines.
        var sampled = Env();
        sampled.Reset(Spec("tdm_mantic", sampled: true), seed: 4);
        sampled.World.Step(200);

        Assert.True(sampled.World.State.IsAlive(sampled.RedSlot));
        Assert.True(sampled.World.State.IsAlive(sampled.BlueSlot));
    }

    /// <summary>
    /// A sampled placement is reproducible from the seed, and different seeds
    /// really do move it.
    /// </summary>
    /// <remarks>
    /// ⚠️ The draw comes out of the match's own stream, which is seeded
    /// <em>before</em> placement for exactly this reason. Seeding afterwards —
    /// which is what the code did until sampling existed — would take the draw
    /// from whatever the previous episode left behind.
    /// </remarks>
    [Fact]
    public void ASampledPlacementIsReproducibleAndSeedDependent()
    {
        static (double X, double Y) RedAt(uint seed)
        {
            var env = Env();
            env.Reset(Spec("ctf_truefort", sampled: true), seed);
            ref readonly var red = ref env.World.State.Characters[env.RedSlot];
            return (red.X, red.Y);
        }

        Assert.Equal(RedAt(4), RedAt(4));

        // Truefort has several spawn points a side, so some seed lands
        // elsewhere. Scanning rather than asserting one pair keeps this a claim
        // about the draw being live, not about which point a seed happens to hit.
        var first = RedAt(4);
        var moved = false;

        for (uint seed = 5; seed < 40 && !moved; seed++)
            moved = RedAt(seed) != first;

        Assert.True(moved, "every seed placed red in the same spot; the draw is not live");
    }

    /// <summary>
    /// Explicit placement is untouched — the same coordinates, to the bit.
    /// </summary>
    [Fact]
    public void ExplicitPlacementStillLandsExactlyWhereItIsTold()
    {
        var env = Env();
        env.Reset(Spec("gg_debug", sampled: false), seed: 4);

        Assert.Equal(600, env.World.State.Characters[env.RedSlot].X);
        Assert.Equal(850, env.World.State.Characters[env.RedSlot].Y);
        Assert.Equal(700, env.World.State.Characters[env.BlueSlot].X);
        Assert.Equal(850, env.World.State.Characters[env.BlueSlot].Y);
    }

    /// <summary>
    /// ⚠️ <b>Half a sampled pair is refused</b>, which is what makes a NaN
    /// sentinel safe to have at all.
    /// </summary>
    /// <remarks>
    /// One NaN of a pair is meaningless and is the shape a marshalling bug
    /// produces. Refused loudly here rather than reaching a placement
    /// half-understood, which would put a character at NaN and make every
    /// comparison about it quietly false.
    /// </remarks>
    [Fact]
    public void HalfASampledPairIsRefused()
    {
        var env = Env();

        var half = Spec("gg_debug", sampled: false) with { RedX = DuelSpec.Sampled };
        Assert.Throws<ArgumentException>(() => env.Reset(half, seed: 4));

        var otherHalf = Spec("gg_debug", sampled: false) with { BlueY = DuelSpec.Sampled };
        Assert.Throws<ArgumentException>(() => env.Reset(otherHalf, seed: 4));
    }

    /// <summary>Both agents face each other, however they were placed.</summary>
    /// <remarks>
    /// ⚠️ The facing used to be read off the <em>spec</em>, and a sampled spec
    /// holds two NaNs — <c>NaN &lt;= NaN</c> is false either way, so both agents
    /// would have faced 180 and one would start with its back turned. Reading
    /// the placed positions instead is what fixes it, and this is the assertion
    /// that would have caught it.
    /// </remarks>
    [Fact]
    public void BothAgentsFaceEachOtherWhenSampled()
    {
        var env = Env();
        env.Reset(Spec("ctf_truefort", sampled: true), seed: 4);

        var redX = env.World.State.Characters[env.RedSlot].X;
        var blueX = env.World.State.Characters[env.BlueSlot].X;

        Assert.Equal(redX <= blueX ? 0 : 180, env.Decoder.Aim(env.RedSlot));
        Assert.Equal(blueX < redX ? 0 : 180, env.Decoder.Aim(env.BlueSlot));
    }
}
