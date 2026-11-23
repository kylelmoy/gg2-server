using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="CharacterBox.For"/>, against the engine.
/// </summary>
/// <remarks>
/// Measured against <c>gg2-samplers/probes/probe-character-boxes.js</c>, against all
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
/// <c>gg2-samplers/sample-physics.js</c> derives the bounds the same way Create.xml
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

    /// <summary>
    /// In the Christmas window Quote is one pixel taller, and nobody else changes.
    /// </summary>
    /// <remarks>
    /// <c>xmasTime.gml</c> <c>sprite_assign</c>s the Christmas sprites, bbox and
    /// origin included. Measured live: after <c>xmasTime()</c>,
    /// <c>QuerlyRedS</c> and <c>QuerlyBlueS</c> read -7, 7, -13, 11 and
    /// <c>ScoutRedS</c> is unchanged.
    /// </remarks>
    [Fact]
    public void ChristmasMakesQuoteOnePixelTaller()
    {
        Assert.Equal(new CharacterBox(-7, 7, -13, 11), CharacterBox.For(PlayerClass.Quote, christmasSprites: true));
        Assert.Equal(new CharacterBox(-7, 7, -12, 11), CharacterBox.For(PlayerClass.Quote, christmasSprites: false));

        foreach (var playerClass in Enum.GetValues<PlayerClass>())
        {
            if (playerClass == PlayerClass.Quote || !Enum.IsDefined(playerClass))
                continue;

            CharacterBox normal;
            try { normal = CharacterBox.For(playerClass); }
            catch (ArgumentOutOfRangeException) { continue; }

            Assert.Equal(normal, CharacterBox.For(playerClass, christmasSprites: true));
        }
    }

    /// <summary>A spawned character takes the world's Christmas flag.</summary>
    [Fact]
    public void ASpawnedQuoteTakesTheWorldsChristmasBox()
    {
        var level = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));
        var spawns = new SpawnPoints(level);
        var state = default(WorldState);
        state.ChristmasSprites = true;

        Assert.True(state.Spawn(spawns, 1, PlayerClass.Quote, Team.Red, spawnpointId: 0, group: 0));
        Assert.Equal(-13, state.Characters[1].Box.Top);

        state.ChristmasSprites = false;
        Assert.True(state.Spawn(spawns, 2, PlayerClass.Quote, Team.Blue, spawnpointId: 0, group: 0));
        Assert.Equal(-12, state.Characters[2].Box.Top);
    }

    /// <summary><c>calculateMonthAndDay.gml</c>'s window, both ends.</summary>
    [Theory]
    [InlineData(12, 23, false)]
    [InlineData(12, 24, true)]
    [InlineData(12, 31, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(1, 3, false)]
    [InlineData(7, 1, false)]
    public void TheChristmasWindowIsTheReferences(int month, int day, bool christmas) =>
        Assert.Equal(christmas, GG2.Server.GameServer.IsChristmas(new DateTime(2026, month, day)));
}
