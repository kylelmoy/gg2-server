using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// <see cref="WorldView.MayOppose"/>: a view built with an opponents table lets
/// each viewer consider only its own entry as an enemy, and a view without one
/// changes nothing. <c>docs/DUEL.md</c>.
/// </summary>
public sealed class OpponentFilterTests
{
    private const int Bot = 1;
    private const int Near = 2;
    private const int Far = 3;

    /// <summary>A blue bot with two red enemies, one near and one farther off, all on open ground.</summary>
    private static (CollisionWorld World, WorldState State) ThreeOnTruefort()
    {
        var level = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);
        var spawns = new SpawnPoints(level);
        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Rounds.Start(ref state);
        state.AddPlayer(0);

        foreach (var (slot, team) in new[] { (Bot, Team.Blue), (Near, Team.Red), (Far, Team.Red) })
        {
            state.AddPlayer(slot);
            Assert.True(state.Spawn(spawns, slot, PlayerClass.Soldier, team, 0, 0));
        }

        // Side by side on the same floor as the bot's spawn, so the targeting
        // range and nothing else separates them.
        var x = state.Characters[Bot].X;
        var y = state.Characters[Bot].Y;
        state.Characters[Near].X = x + 60;
        state.Characters[Near].Y = y;
        state.Characters[Far].X = x + 120;
        state.Characters[Far].Y = y;

        return (new CollisionWorld(level), state);
    }

    private static byte[] Only(int viewer, int opponent)
    {
        var table = new byte[WorldState.MaxSlots];
        Array.Fill(table, WorldView.AnyOpponent);
        table[viewer] = (byte)opponent;
        return table;
    }

    [Fact]
    public void WithoutATableEveryEnemyIsAnOpponentAndTheNearestComesFirst()
    {
        var (world, state) = ThreeOnTruefort();
        var view = new WorldView(in state, world);
        Span<int> found = stackalloc int[4];

        Assert.True(view.MayOppose(Bot, Near));
        Assert.Equal(2, view.NearestEnemies(Bot, found));
        Assert.Equal(Near, found[0]);
        Assert.Equal(Near, BotTargeting.Find(in view, Bot, 1000).Slot);
    }

    [Fact]
    public void WithATableOnlyTheNamedOpponentIsSeenByThatViewer()
    {
        var (world, state) = ThreeOnTruefort();
        var view = new WorldView(in state, world, Only(Bot, Far));
        Span<int> found = stackalloc int[4];

        Assert.False(view.MayOppose(Bot, Near));
        Assert.True(view.MayOppose(Bot, Far));
        Assert.Equal(1, view.NearestEnemies(Bot, found));
        Assert.Equal(Far, found[0]);
        Assert.Equal(Far, BotTargeting.Find(in view, Bot, 1000).Slot);

        // Other viewers are untouched by the bot's entry.
        Assert.True(view.MayOppose(Near, Bot));
    }

    [Fact]
    public void AllowedAlliesAreNotNarrowed()
    {
        var (world, state) = ThreeOnTruefort();
        var view = new WorldView(in state, world, Only(Near, Bot));
        Span<int> found = stackalloc int[4];

        Assert.Equal(1, view.NearestAllies(Near, found));
        Assert.Equal(Far, found[0]);
    }
}
