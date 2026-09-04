using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="CharacterBox.For"/>, against the engine.
/// </summary>
/// <remarks>
/// Measured against <c>tools/probes/probe-character-boxes.js</c>, against all
/// ten classes. Every fixture that depends on this table depends on it only
/// indirectly, so a wrong edge would show up as a mysterious trajectory
/// divergence rather than as a wrong box.
/// <para>
/// The engine's own definition, <c>Character.events/Create.xml:12-15</c>, is
/// four subtractions on the class sprite:
/// <c>sprite_get_bbox_left(sprite_index) - sprite_get_xoffset(sprite_index)</c>
/// and so on. **A character's box is a pure property of its sprite** — which is
/// why the probe needs no character, no bot and no map.
/// </para>
/// <para>
/// <c>tools/sample-physics.js</c> derives the bounds the same way Create.xml
/// does. It previously overrode the engine's values with a hardcoded table of
/// its own that had <c>top = -6</c> for the Scout and the Heavy, where the
/// engine derives <b>-10</b> and <b>-12</b> — a divergence <c>PhysicsSampleTests</c>
/// missed because no scenario in 48 × 48 ticks had an outcome that turned on
/// the top edge. The fixture itself still wants regenerating, which needs a
/// live game with a character.
/// </para>
/// <para>
/// <b>Red and blue agree on every class</b>, checked rather than assumed — a
/// per-team difference would be invisible to every fixture, because a recording
/// only ever shows one team's box being right.
/// </para>
/// </remarks>
public sealed class CharacterBoxTests
{
    /// <summary>
    /// The engine's reading, class by class. Left, right, top, bottom, relative
    /// to the instance's position.
    /// </summary>
    [Theory]
    [InlineData(PlayerClass.Scout, -6, 6, -10, 23)]
    [InlineData(PlayerClass.Soldier, -6, 6, -8, 23)]
    [InlineData(PlayerClass.Sniper, -6, 6, -8, 23)]
    [InlineData(PlayerClass.Demoman, -7, 7, -10, 23)]
    [InlineData(PlayerClass.Medic, -7, 7, -8, 23)]
    [InlineData(PlayerClass.Engineer, -6, 6, -10, 23)]
    [InlineData(PlayerClass.Heavy, -9, 9, -12, 23)]
    [InlineData(PlayerClass.Spy, -6, 6, -10, 23)]
    [InlineData(PlayerClass.Pyro, -7, 7, -6, 23)]
    [InlineData(PlayerClass.Quote, -7, 7, -12, 11)]
    public void EveryClassBoxIsTheOneTheEngineDerives(
        PlayerClass playerClass, int left, int right, int top, int bottom)
    {
        var box = CharacterBox.For(playerClass);

        Assert.Equal(left, box.Left);
        Assert.Equal(right, box.Right);
        Assert.Equal(top, box.Top);
        Assert.Equal(bottom, box.Bottom);
    }

    /// <summary>
    /// <b>Only the Pyro and the Quote are symmetric top-to-bottom in the way
    /// a careless reader assumes, and nine of the ten share a bottom of 23.</b>
    /// </summary>
    /// <remarks>
    /// This is here because the shared 23 is exactly what makes a wrong
    /// <em>top</em> hard to notice: every class stands on the floor the same
    /// way, so ground contact — which is most of what a fixture exercises —
    /// cannot tell the boxes apart at all. The Quote is the one exception and
    /// the reason to state it.
    /// </remarks>
    [Fact]
    public void EveryClassButTheQuoteSharesABottomOfTwentyThree()
    {
        foreach (var playerClass in Enum.GetValues<PlayerClass>())
        {
            var box = CharacterBox.For(playerClass);

            if (playerClass == PlayerClass.Quote)
                Assert.Equal(11, box.Bottom);
            else
                Assert.Equal(23, box.Bottom);

            // And every one is symmetric left-to-right, which is what lets
            // Aabb.ForCharacter skip the flip inflation that
            // SpriteBoxSampleTests measures on the non-character sprites.
            Assert.Equal(-box.Left, box.Right);
        }
    }
}
