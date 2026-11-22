using GG2.Bots;
using GG2.Host;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// <c>--bot-mode</c>: the two shipped brains, reachable and distinguishable, and
/// the two removed ones refused.
/// </summary>
/// <remarks>
/// <b>These assert the TIERS a mode builds, not that the flag parses.</b> A
/// mode that is accepted and then builds the same brain as another is exactly
/// the failure the flag was meant to remove, and a parse test cannot see it —
/// which is why every case below reads <c>Brain.Tiers</c>.
/// </remarks>
public sealed class BotModeTests
{
    /// <summary>A shipped net, so these test the flag wiring and not a checkpoint.</summary>
    private static string[] WithPolicy(params string[] args) =>
        [.. args, "--policy", PolicyFixture.Checkpoint];

    private static BotBrainSpec? BrainOf(params string[] args) =>
        CommandLine.Parse(args).Bots.Brain;

    [Fact]
    public void ScriptedIsTheDefaultAndBuildsNoBrainAtAll()
    {
        // Null is the all-scripted arm, NOT a broken configuration — see
        // BotBrainSpec's remarks. An empty tier list would be the broken one.
        Assert.Null(BrainOf());
        Assert.Null(BrainOf("--bot-mode", "scripted"));
        Assert.Null(BrainOf("--bot-mode", "0"));
    }

    [Fact]
    public void PolicyBuildsOnePolicyTier()
    {
        var brain = Assert.IsType<BotBrainSpec>(BrainOf(WithPolicy("--bot-mode", "policy")));

        Assert.IsType<SimPolicyMicroSpec>(Assert.Single(brain.Tiers));
    }

    /// <summary>Numbers and names name the same modes.</summary>
    [Fact]
    public void NumbersAndNamesAgree()
    {
        Assert.Null(BrainOf("--bot-mode", "0"));

        var byNumber = Assert.IsType<BotBrainSpec>(BrainOf(WithPolicy("--bot-mode", "2")));
        var byName = Assert.IsType<BotBrainSpec>(BrainOf(WithPolicy("--bot-mode", "policy")));

        Assert.Equal(byName.Name, byNumber.Name);
    }

    // ------------------------------------------------------------------
    // The refusals
    // ------------------------------------------------------------------

    /// <summary>
    /// The removed search and tiered modes are refused by name and by number,
    /// with a message naming the replacement.
    /// </summary>
    /// <remarks>
    /// <b>The numbers matter most.</b> 1 and 3 must never come to mean something
    /// else: a script that still says <c>--bot-mode 3</c> has to fail, not run a
    /// different brain.
    /// </remarks>
    [Theory]
    [InlineData("search")]
    [InlineData("hybrid-search")]
    [InlineData("1")]
    [InlineData("tiered")]
    [InlineData("3")]
    public void ARemovedModeIsRefusedWithItsReplacement(string mode)
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => CommandLine.Parse(WithPolicy("--bot-mode", mode)));

        Assert.Contains("removed", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("policy", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The same refusal from a config file, which is how a fleet host names its mode.</summary>
    [Theory]
    [InlineData("search")]
    [InlineData("tiered")]
    public void ARemovedModeInAConfigFileIsRefused(string mode)
    {
        var setting = ServerSettings.All.Single(s => s.Section == "Bots" && s.Key == "Mode");

        var thrown = Assert.Throws<FormatException>(() => setting.Set(ServerOptions.CreateDefault(), mode));

        Assert.Contains("removed", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The flags that configured the search and the arbiter are gone, and say so by failing.</summary>
    [Theory]
    [InlineData("--search-bots")]
    [InlineData("--search-threads", "4")]
    [InlineData("--bot-arbiter", "10000")]
    [InlineData("--fixed-bot-budget")]
    public void ARemovedFlagIsUnknown(params string[] args)
    {
        var thrown = Assert.Throws<ArgumentException>(() => CommandLine.Parse(args));

        Assert.Contains(args[0], thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mode that needs a checkpoint and was given none is refused, by name.
    /// </summary>
    [Fact]
    public void APolicyModeWithoutACheckpointIsRefused()
    {
        var thrown = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--bot-mode", "policy"]));

        Assert.Contains("--policy", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A checkpoint handed to a mode that cannot consult it is REFUSED, not
    /// ignored.
    /// </summary>
    /// <remarks>
    /// A checkpoint loaded, printed and never consulted is a server saying it is
    /// doing something it is not. This is the assertion most likely to be
    /// "relaxed" by someone who hits it — the fix is to name the mode you meant,
    /// not to make the flag silent.
    /// </remarks>
    [Fact]
    public void ACheckpointGivenToAModeThatCannotUseItIsRefused()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => CommandLine.Parse(WithPolicy("--bot-mode", "scripted")));

        Assert.Contains("never consults", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--policy</c> on its own still means policy mode — the one inference,
    /// kept because <c>docs/BOTS.md</c> documents that invocation.
    /// </summary>
    [Fact]
    public void APolicyOnItsOwnStillSelectsPolicyMode()
    {
        var brain = Assert.IsType<BotBrainSpec>(BrainOf(WithPolicy()));

        Assert.IsType<SimPolicyMicroSpec>(Assert.Single(brain.Tiers));
    }

    /// <summary>
    /// And the inference is ORDER-INDEPENDENT, because both flags are
    /// collected before anything reconciles them.
    /// </summary>
    [Fact]
    public void ModeAndPolicyDoNotDependOnFlagOrder()
    {
        var before = BrainOf("--bot-mode", "policy", "--policy", PolicyFixture.Checkpoint);
        var after = BrainOf("--policy", PolicyFixture.Checkpoint, "--bot-mode", "policy");

        Assert.Equal(before?.Name, after?.Name);
        Assert.NotNull(before);
    }

    [Fact]
    public void AnUnknownModeNamesTheTwo()
    {
        var thrown = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--bot-mode", "clever"]));

        Assert.Contains("scripted", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("policy", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("removed", thrown.Message, StringComparison.Ordinal);
    }
}
