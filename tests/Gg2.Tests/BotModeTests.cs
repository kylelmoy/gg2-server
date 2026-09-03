using Gg2.Bots;
using Gg2.Host;

namespace Gg2.Tests;

/// <summary>
/// <c>--bot-mode</c>: the four shipped brains, reachable and distinguishable.
/// </summary>
/// <remarks>
/// ⛔ <b>These assert the TIERS a mode builds, not that the flag parses.</b> A
/// mode that is accepted and then builds the same brain as another is exactly
/// the failure the flag was meant to remove, and a parse test cannot see it —
/// which is why every case below reads <c>Brain.Tiers</c> and why
/// <see cref="EveryModeBuildsADifferentBrain"/> exists as the control.
/// </remarks>
public sealed class BotModeTests
{
    /// <summary>A checkpoint that loads in THIS build.</summary>
    /// <remarks>
    /// ⚠️ <c>MintedForThisBuild</c>, not <c>Checkpoint</c>. The checked-in
    /// fixture is stamp-invalid against this build (<c>docs/ENV.md</c>, "The
    /// checkpoint is a FIXTURE now"), so using it here would mean passing
    /// <c>--policy-ignore-stamp</c> in every case — and a test suite that
    /// routinely bypasses a guard is how the guard stops being one. The minted
    /// copy carries this build's stamp and takes the accept path, which is what
    /// these tests are actually about: the flag wiring, not the checkpoint.
    /// </remarks>
    private static string[] WithPolicy(params string[] args) =>
        [.. args, "--policy", PolicyFixture.MintedForThisBuild];

    private static BotBrainSpec? BrainOf(params string[] args) =>
        Program.Parse(args).Bots.Brain;

    [Fact]
    public void ScriptedIsTheDefaultAndBuildsNoBrainAtAll()
    {
        // ⚠️ Null is the all-scripted arm, NOT a broken configuration — see
        // BotBrainSpec's remarks. An empty tier list would be the broken one.
        Assert.Null(BrainOf());
        Assert.Null(BrainOf("--bot-mode", "scripted"));
        Assert.Null(BrainOf("--bot-mode", "0"));
    }

    [Fact]
    public void SearchBuildsOneSearchTierAndNoArbiter()
    {
        var brain = Assert.IsType<BotBrainSpec>(BrainOf("--bot-mode", "search"));

        Assert.IsType<SearchMicroSpec>(Assert.Single(brain.Tiers));

        // One tier: an arbiter would have nothing to choose between, and paying
        // for one would be pure cost.
        Assert.Null(brain.Arbiter);
    }

    [Fact]
    public void PolicyBuildsOnePolicyTierAndNoArbiter()
    {
        var brain = Assert.IsType<BotBrainSpec>(BrainOf(WithPolicy("--bot-mode", "policy")));

        Assert.IsType<PolicyMicroSpec>(Assert.Single(brain.Tiers));
        Assert.Null(brain.Arbiter);
    }

    /// <summary>
    /// Tiered builds both, search first, and defaults its own budget.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Order is the arbiter's whole input</b> — most expensive first, so a
    /// server under pressure drops to the policy before it drops to the scripted
    /// layer. A test that only counted two tiers would pass with them reversed,
    /// which is a different and worse bot.
    /// <para>
    /// ⚠️ The budget default is a <em>stated guess</em>, not a measured result —
    /// see <see cref="DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds"/>,
    /// which records what would show it wrong. This pins that the default is
    /// applied, not that it is right.
    /// </para>
    /// </remarks>
    [Fact]
    public void TieredBuildsSearchThenPolicyAndDefaultsItsBudget()
    {
        var brain = Assert.IsType<BotBrainSpec>(BrainOf(WithPolicy("--bot-mode", "tiered")));

        Assert.Equal(2, brain.Tiers.Count);
        Assert.IsType<SearchMicroSpec>(brain.Tiers[0]);
        Assert.IsType<PolicyMicroSpec>(brain.Tiers[1]);

        var arbiter = Assert.IsType<DecisionQuotaArbiter>(brain.Arbiter);
        Assert.Equal(DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds, arbiter.UnitsPerTick);
    }

    /// <summary>An explicit <c>--bot-arbiter</c> still wins over the default.</summary>
    [Fact]
    public void AnExplicitArbiterBeatsTheTieredDefault()
    {
        var brain = Assert.IsType<BotBrainSpec>(
            BrainOf(WithPolicy("--bot-mode", "tiered", "--bot-arbiter", "2500")));

        var arbiter = Assert.IsType<DecisionQuotaArbiter>(brain.Arbiter);

        Assert.Equal(2500, arbiter.UnitsPerTick);
        Assert.NotEqual(DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds, arbiter.UnitsPerTick);
    }

    /// <summary>
    /// ⛔ The control: no two modes build the same brain.
    /// </summary>
    /// <remarks>
    /// Every assertion above would still pass if two modes were wired to the same
    /// tier list and only one of them was tested. This compares all four, so a
    /// mode that silently aliases another fails here even if its own case was
    /// never written.
    /// </remarks>
    [Fact]
    public void EveryModeBuildsADifferentBrain()
    {
        var shapes = new[]
        {
            Shape(BrainOf("--bot-mode", "scripted")),
            Shape(BrainOf("--bot-mode", "search")),
            Shape(BrainOf(WithPolicy("--bot-mode", "policy"))),
            Shape(BrainOf(WithPolicy("--bot-mode", "tiered"))),
        };

        Assert.Equal(shapes.Length, shapes.Distinct(StringComparer.Ordinal).Count());
    }

    private static string Shape(BotBrainSpec? brain) =>
        brain is null
            ? "scripted"
            : string.Join('+', brain.Tiers.Select(tier => tier.GetType().Name))
              + (brain.Arbiter is null ? "" : "/arbiter");

    /// <summary>Numbers and names name the same four modes.</summary>
    /// <remarks>
    /// ⚠️ The numbers are contract: a bench harness sweeping 0..3 asks for each
    /// arm in turn, so renumbering <see cref="BotMode"/> would silently change
    /// what it measures.
    /// </remarks>
    [Theory]
    [InlineData("0", "scripted")]
    [InlineData("1", "search")]
    [InlineData("2", "policy")]
    [InlineData("3", "tiered")]
    public void NumbersAndNamesAgree(string number, string word)
    {
        var needsPolicy = word is "policy" or "tiered";

        var byNumber = needsPolicy
            ? BrainOf(WithPolicy("--bot-mode", number))
            : BrainOf("--bot-mode", number);

        var byName = needsPolicy
            ? BrainOf(WithPolicy("--bot-mode", word))
            : BrainOf("--bot-mode", word);

        Assert.Equal(Shape(byNumber), Shape(byName));
    }

    // ------------------------------------------------------------------
    // The refusals
    // ------------------------------------------------------------------

    /// <summary>
    /// A mode that needs a checkpoint and was given none is refused, by name.
    /// </summary>
    [Theory]
    [InlineData("policy")]
    [InlineData("tiered")]
    public void APolicyModeWithoutACheckpointIsRefused(string mode)
    {
        var thrown = Assert.Throws<ArgumentException>(() => Program.Parse(["--bot-mode", mode]));

        Assert.Contains("--policy", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⛔ A checkpoint handed to a mode that cannot consult it is REFUSED, not
    /// ignored.
    /// </summary>
    /// <remarks>
    /// The same rule the multi-tier case has always applied: a checkpoint loaded,
    /// printed and never consulted is a server saying it is doing something it is
    /// not. ⚠️ This is the assertion most likely to be "relaxed" by someone who
    /// hits it — the fix is to name the mode you meant, not to make the flag
    /// silent.
    /// </remarks>
    [Theory]
    [InlineData("scripted")]
    [InlineData("search")]
    public void ACheckpointGivenToAModeThatCannotUseItIsRefused(string mode)
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => Program.Parse(WithPolicy("--bot-mode", mode)));

        Assert.Contains("never consults", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ <c>--policy</c> on its own still means policy mode — the one inference,
    /// kept because <c>docs/BOTS.md</c> documents that invocation.
    /// </summary>
    [Fact]
    public void APolicyOnItsOwnStillSelectsPolicyMode()
    {
        var brain = Assert.IsType<BotBrainSpec>(BrainOf(WithPolicy()));

        Assert.IsType<PolicyMicroSpec>(Assert.Single(brain.Tiers));
    }

    /// <summary>
    /// ⚠️ And the inference is ORDER-INDEPENDENT, because both flags are
    /// collected before anything reconciles them.
    /// </summary>
    /// <remarks>
    /// <c>--search-threads</c> before <c>--search-bots</c> was exactly this bug
    /// once, and it is the reason the brain is built in one place at the end.
    /// </remarks>
    [Fact]
    public void ModeAndPolicyDoNotDependOnFlagOrder()
    {
        var before = BrainOf(
            "--bot-mode", "tiered", "--policy", PolicyFixture.MintedForThisBuild);

        var after = BrainOf(
            "--policy", PolicyFixture.MintedForThisBuild, "--bot-mode", "tiered");

        Assert.Equal(Shape(before), Shape(after));
    }

    [Fact]
    public void AnUnknownModeNamesTheFour()
    {
        var thrown = Assert.Throws<ArgumentException>(() => Program.Parse(["--bot-mode", "clever"]));

        Assert.Contains("scripted", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("tiered", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠️ The flag it replaced is gone, and says so by failing.</summary>
    [Fact]
    public void SearchBotsIsNoLongerAFlag()
    {
        var thrown = Assert.Throws<ArgumentException>(() => Program.Parse(["--search-bots"]));

        Assert.Contains("--search-bots", thrown.Message, StringComparison.Ordinal);
    }
}
