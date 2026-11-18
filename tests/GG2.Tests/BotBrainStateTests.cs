using System.Text.Json;
using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

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
    /// decided — from <c>[Bots] ClassPolicy</c> — so the state asks a built controller rather than reading the spec. A second
    /// copy of that resolution would be the copy nothing gates, and it would be
    /// wrong in exactly the configured case.
    /// </remarks>
    [Fact]
    public void APolicyServerNamesEachTierAndWhatItDrives()
    {
        using var harness = Policy();

        var bots = Bots(harness);

        Assert.Equal("policy", bots.GetProperty("mode").GetString());
        Assert.Equal("gg2-sim:" + PolicyFixture.Label, bots.GetProperty("brain").GetString());

        var tier = Assert.Single(bots.GetProperty("tiers").EnumerateArray());
        Assert.Equal("gg2-sim:" + PolicyFixture.Label, tier.GetProperty("name").GetString());

        // Three classes assigned, so a block that reported only the first, or a
        // default, would be visibly wrong. Quote is past the fill's Pyro ceiling
        // and is listed: the duel server makes Quote bots.
        Assert.Equal(
            ["Soldier", "Heavy", "Quote"],
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
    [InlineData(PlayerClass.Soldier, true)]
    [InlineData(PlayerClass.Demoman, false)]
    public void ABotsBrainIsTheTierThatWillDriveItsClass(PlayerClass playerClass, bool driven)
    {
        using var harness = Policy(playerClass);

        var bot = Bot(harness);

        Assert.Equal(driven ? "gg2-sim:" + PolicyFixture.Label : "scripted", bot.GetProperty("brain").GetString());

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
    /// ⛔ <b>THE REGRESSION GATE for naming a slot's brain.</b> A controller's
    /// own <see cref="IMicroController.Name"/> does not say which checkpoint it
    /// holds, so a slot named from the controller would be indistinguishable
    /// across tiers and unmatchable against the tier rows the panel draws from the
    /// spec. The state names it from the spec through
    /// <c>HybridBot.TierIndexFor</c> instead. A single-tier test passes either way.
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Soldier, 0)]
    [InlineData(PlayerClass.Scout, 1)]
    public void TwoPolicyTiersAreToldApartOnTheBot(PlayerClass playerClass, int tier)
    {
        string[] checkpoints = [PolicyFixture.Checkpoint, PolicyFixture.Second];

        var parsed = CommandLine.Parse(
        [
            "--bot-mode", "policy",
            "--policy", checkpoints[0],
            "--policy", checkpoints[1],
            "--bot-class-policy", PolicyFixture.SoldierAssignment + "," + PolicyFixture.ScoutAssignment,
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
            "gg2-sim:" + Path.GetFileNameWithoutExtension(checkpoints[tier]),
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
        using var humans = Policy();
        Assert.Equal("humans", Bots(humans).GetProperty("engage").GetString());

        using var anyone = Policy(engagesBots: true);
        Assert.Equal("anyone", Bots(anyone).GetProperty("engage").GetString());
    }

    private static ServerHarness Policy(PlayerClass playerClass = PlayerClass.Soldier, bool engagesBots = false)
    {
        string[] arguments =
        [
            "--bot-mode", "policy", "--policy", PolicyFixture.Checkpoint,
            "--bot-class-policy", PolicyFixture.SoldierAssignment + ",Heavy=" + PolicyFixture.Label + ",Quote=" + PolicyFixture.Label,
            .. engagesBots ? new[] { "--bot-engages-bots" } : [],
        ];

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
