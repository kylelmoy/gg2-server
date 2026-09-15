using System.Text.Json;
using Gg2.Bots;
using Gg2.Host;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The brain, as an operator surface sees it: <c>state</c>'s <c>bots</c> block
/// and the per-slot fields the web panel's Bots card and roster are built from.
/// </summary>
/// <remarks>
/// <b>Gated because nothing else could contradict the claim.</b> The panel says
/// which brain is driving; a page can render a plausible card off fields that
/// have quietly stopped being sent, and the failure only shows up to an operator
/// reading a number that is no longer true. `docs/WEBADMIN.md`, "Which brain the
/// bots run".
/// <para>
/// ⚠️ <b>Over <see cref="ControlCommands.State"/> rather than over the socket.</b>
/// The panel does not add a verb of its own — it reads exactly this string — so
/// the transport is <see cref="ControlPortTests"/>'s subject and the content is
/// this one's.
/// </para>
/// </remarks>
public sealed class BotBrainStateTests
{
    /// <summary>An all-scripted server names the scripted brain, and no tier.</summary>
    /// <remarks>
    /// <b>A brain, not the absence of one.</b> Bot mode <c>scripted</c> is arm 1
    /// and a complete bot; an empty or missing field here would read on the panel
    /// as a server whose brain could not be determined.
    /// </remarks>
    [Fact]
    public void AScriptedServerNamesTheScriptedBrain()
    {
        using var harness = new ServerHarness();

        harness.Server.AddBot("[BOT] 1", Team.Red, PlayerClass.Scout, Scripted());
        harness.Tick(2);

        var bots = Bots(harness);

        Assert.Equal("scripted", bots.GetProperty("mode").GetString());
        Assert.Equal("scripted", bots.GetProperty("brain").GetString());
        Assert.Empty(bots.GetProperty("tiers").EnumerateArray());

        // ⛔ Absent, not "humans". Mode `scripted` has no engagement gate at all,
        // and a field naming one would describe a restriction that does not exist.
        Assert.False(bots.TryGetProperty("engage", out _));

        Assert.Equal("scripted", Bot(harness).GetProperty("brain").GetString());
    }

    /// <summary>
    /// A policy server names its tiers, and each tier's class set comes off a
    /// live controller.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The class set is the half that cannot be re-derived.</b>
    /// <c>IMicroController.Drives</c> is the one place a tier's classes are
    /// decided — the checkpoint's own set, narrowed by <c>[Bots] ClassPolicy</c> —
    /// so the state asks a built controller rather than reading the spec. A second
    /// copy of that resolution would be the copy nothing gates, and it would be
    /// wrong in exactly the configured case.
    /// </remarks>
    [Fact]
    public void APolicyServerNamesEachTierAndWhatItDrives()
    {
        var label = Path.GetFileNameWithoutExtension(PolicyFixture.SniperTrainedCheckpoint);

        using var harness = Policy(PolicyFixture.SniperTrainedCheckpoint);

        var bots = Bots(harness);

        Assert.Equal("policy", bots.GetProperty("mode").GetString());
        Assert.Equal("policy:" + label, bots.GetProperty("brain").GetString());

        var tier = Assert.Single(bots.GetProperty("tiers").EnumerateArray());
        Assert.Equal("policy:" + label, tier.GetProperty("name").GetString());

        // This fixture's set differs from PolicyNetwork.DefaultTrainedClasses by
        // exactly two members — see PolicyFixture. A block that reported the
        // default would be right on three of four names and wrong on the fourth,
        // which is the shape of wrong nobody notices.
        Assert.Equal(
            ["Scout", "Soldier", "Sniper", "Heavy"],
            tier.GetProperty("classes").EnumerateArray().Select(c => c.GetString()!).ToArray());
    }

    /// <summary>
    /// A bot's brain is the tier that will drive its class, and the scripted layer
    /// when no tier will.
    /// </summary>
    /// <remarks>
    /// <b>A class every tier refuses is a real answer, not an error.</b> It falls
    /// back to the scripted layer entirely — the macro layer is a complete bot —
    /// so the panel names the scripted brain rather than leaving the row blank.
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Sniper, true)]
    [InlineData(PlayerClass.Demoman, false)]
    public void ABotsBrainIsTheTierThatWillDriveItsClass(PlayerClass playerClass, bool driven)
    {
        var label = Path.GetFileNameWithoutExtension(PolicyFixture.SniperTrainedCheckpoint);

        using var harness = Policy(PolicyFixture.SniperTrainedCheckpoint, playerClass);

        var bot = Bot(harness);

        Assert.Equal(driven ? "policy:" + label : "scripted", bot.GetProperty("brain").GetString());

        // ⚠️ The counters are what say a named brain is CONSULTED. A tier can be
        // loaded, named on the panel and never admitted; `brain` cannot tell those
        // apart and this pair can. They are sent for every wrapped bot, driven
        // class or not.
        Assert.True(bot.TryGetProperty("microTicks", out _));
        Assert.True(bot.GetProperty("scriptedTicks").GetInt64() > 0);
        Assert.False(bot.GetProperty("driving").GetBoolean());
    }

    /// <summary>
    /// Two policy tiers are told apart on the bot as well as on the card.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>THE REGRESSION GATE for naming a slot's brain.</b> Every
    /// <c>PolicyController</c> answers <c>policy</c> to
    /// <see cref="IMicroController.Name"/> whatever checkpoint it holds, so a
    /// slot named from the controller reports <c>policy</c> for both halves of a
    /// two-tier bundle — indistinguishable, and unmatchable against the tier rows
    /// the panel draws from the spec. The state names it from the spec through
    /// <c>HybridBot.TierIndexFor</c> instead. A single-tier test passes either way.
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Sniper, 0)]
    [InlineData(PlayerClass.Engineer, 1)]
    public void TwoPolicyTiersAreToldApartOnTheBot(PlayerClass playerClass, int tier)
    {
        // Disjoint on the classes asked about: the first drives SNIPER and not
        // ENGINEER, the second the reverse. See PolicyFixture for both sets.
        string[] checkpoints =
            [PolicyFixture.SniperTrainedCheckpoint, PolicyFixture.EightFieldCheckpoint];

        var parsed = CommandLine.Parse(
        [
            "--bot-mode", "policy",
            "--policy", checkpoints[0],
            "--policy", checkpoints[1],
        ]);

        var brain = parsed.Bots.Brain!;

        // The mode and its checkpoints travel together, as the host's own options
        // do: ServerOptions.Validate refuses a policy mode with no checkpoint.
        using var harness = new ServerHarness(o =>
        {
            o.BotMode = parsed.BotMode;
            o.PolicyPaths = parsed.PolicyPaths;
            o.Bots = o.Bots with { Brain = brain };
        });

        harness.Server.AddBot("[BOT] 1", Team.Red, playerClass, brain.Wrap(Scripted()));
        harness.Tick(2);

        Assert.Equal(
            "policy:" + Path.GetFileNameWithoutExtension(checkpoints[tier]),
            Bot(harness).GetProperty("brain").GetString());
    }

    /// <summary>A server that drives bots reports the gate every tier is behind.</summary>
    /// <remarks>
    /// <b>The live server's default is <c>HumansOnly</c> and <c>BotBrainSpec</c>'s
    /// own is <c>Anyone</c></b>, so an operator reading a low share needs to know
    /// which one is in force before concluding anything about the checkpoint.
    /// </remarks>
    [Fact]
    public void TheEngagementGateIsReported()
    {
        using var humans = Policy(PolicyFixture.SniperTrainedCheckpoint);
        Assert.Equal("humans", Bots(humans).GetProperty("engage").GetString());

        using var anyone = Policy(PolicyFixture.SniperTrainedCheckpoint, engagesBots: true);
        Assert.Equal("anyone", Bots(anyone).GetProperty("engage").GetString());
    }

    private static ServerHarness Policy(
        string checkpoint, PlayerClass playerClass = PlayerClass.Sniper, bool engagesBots = false)
    {
        string[] arguments = engagesBots
            ? ["--bot-mode", "policy", "--policy", checkpoint, "--bot-engages-bots"]
            : ["--bot-mode", "policy", "--policy", checkpoint];

        var parsed = CommandLine.Parse(arguments);
        var brain = parsed.Bots.Brain!;

        var harness = new ServerHarness(o =>
        {
            o.BotMode = parsed.BotMode;
            o.PolicyPaths = parsed.PolicyPaths;
            o.Bots = o.Bots with { Brain = brain };
        });

        harness.Server.AddBot("[BOT] 1", Team.Red, playerClass, brain.Wrap(Scripted()));
        harness.Tick(2);

        return harness;
    }

    /// <summary>A macro layer with no nav graph — this asserts JSON, not routing.</summary>
    private static ScriptedBot Scripted() =>
        new(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 0);

    private static JsonElement Bots(ServerHarness harness) =>
        JsonDocument.Parse(ControlCommands.State(harness.Server)).RootElement.GetProperty("bots");

    /// <summary>The last roster entry, which is the bot the helpers add.</summary>
    private static JsonElement Bot(ServerHarness harness)
    {
        var players = JsonDocument.Parse(ControlCommands.State(harness.Server))
            .RootElement.GetProperty("players");

        return players[players.GetArrayLength() - 1];
    }
}
