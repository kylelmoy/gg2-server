using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="HeadlessMatchOptions.RedBrain"/> and <see cref="HeadlessMatchOptions.BlueBrain"/>:
/// each team's bots get their own brain, so two controllers meet in one match.
/// <c>tools/bench</c>'s <c>BENCH_VERSUS</c> is built on it.
/// </summary>
public sealed class PerTeamBrainTests
{
    private sealed class Stub(string name) : IMicroController
    {
        public string Name => name;

        public int DecisionCost => 1;

        public int DecisionCostFor(int liveCharacters) => 1;

        public bool DecidedThisPoll => false;

        public bool Drives(PlayerClass playerClass) => true;

        public bool AtDecisionBoundary(int slot) => true;

        public void OnHandoverIn(in WorldView world, int slot)
        {
        }

        public void OnHandoverOut()
        {
        }

        public PlayerInput Poll(in WorldView world, int slot, int tick) => default;
    }

    private sealed record StubSpec(string Label) : MicroControllerSpec
    {
        public override string Name => Label;

        public override IMicroController Create() => new Stub(Label);
    }

    private static List<(Team Team, HybridBot? Hybrid)> Bots(HeadlessMatchOptions options)
    {
        var match = HeadlessMatch.Load(
            options with { Map = "ctf_truefort", Bots = 6 }, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var bots = new List<(Team, HybridBot?)>();

        for (var slot = 1; slot < match.SlotCount; slot++)
        {
            var team = match.State.Players[slot].Team;

            if (team is Team.Red or Team.Blue)
                bots.Add((team, match.HybridBotFor(slot)));
        }

        Assert.Equal(6, bots.Count);
        return bots;
    }

    private static string Controller((Team Team, HybridBot? Hybrid) bot) => Assert.Single(bot.Hybrid!.Tiers).Name;

    [Fact]
    public void ATeamBrainWrapsOnlyThatTeam()
    {
        var bots = Bots(HeadlessMatchOptions.Default with { RedBrain = BotBrainSpec.Of(new StubSpec("red")) });

        Assert.All(bots.Where(b => b.Team == Team.Red), b => Assert.Equal("red", Controller(b)));
        Assert.All(bots.Where(b => b.Team == Team.Blue), b => Assert.Null(b.Hybrid));
    }

    [Fact]
    public void ATeamBrainOverridesTheSharedOne()
    {
        var bots = Bots(HeadlessMatchOptions.Default with
        {
            Brain = BotBrainSpec.Of(new StubSpec("shared")),
            BlueBrain = BotBrainSpec.Of(new StubSpec("blue")),
        });

        Assert.Contains(bots, b => b.Team == Team.Red);
        Assert.Contains(bots, b => b.Team == Team.Blue);
        Assert.All(bots.Where(b => b.Team == Team.Red), b => Assert.Equal("shared", Controller(b)));
        Assert.All(bots.Where(b => b.Team == Team.Blue), b => Assert.Equal("blue", Controller(b)));
    }
}
