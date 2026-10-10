using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A bot wrapped in a micro-controller is still a bot to everything that asks.
/// </summary>
/// <remarks>
/// <b>`is ScriptedBot` was the server's way of asking "is this slot a bot", and
/// a wrapper silently answers no.</b> Four sites asked it — retargeting, removal
/// (which frees the leaving bot's <see cref="BotOccupancy"/> reservations), the
/// map-change hand-off, and the control port's roster — and none of them would
/// have failed. They would have skipped every wrapped bot, and the visible symptom
/// would have been a bot still holding edges on a nav graph that no longer exists,
/// several map rotations later.
/// <para>
/// <b>The failure mode is what makes this worth a test.</b> Nothing throws,
/// nothing logs, and a short play session looks perfect — the bots walk, because
/// the graph they were given at spawn is still the one they are on. It goes wrong
/// only after a map change, which is exactly the case a quick manual check skips.
/// </para>
/// </remarks>
public sealed class WrappedBotVisibilityTests
{
    private static ScriptedBot Bare() => new(null, new NavQuery(Team.Red), BotSkill.ForTier(3), 0);

    [Fact]
    public void AWrappedBotIsStillFoundByScriptedBotOf()
    {
        var bare = Bare();

        Assert.Same(bare, ScriptedBot.Of(bare));
        Assert.Same(bare, ScriptedBot.Of(new HybridBot(new SearchController(new RolloutOptions()), bare)));
    }

    [Fact]
    public void ANonBotSourceIsNotMistakenForOne()
    {
        Assert.Null(ScriptedBot.Of(new NetworkInputSource()));
        Assert.Null(ScriptedBot.Of(null));
    }

    /// <summary>
    /// The point of the interface: a wrapper that forgets to implement it is
    /// invisible again, so this asserts the implementations rather than the helper.
    /// </summary>
    [Fact]
    public void EveryMicroControllerDeclaresWhatItWraps()
    {
        var wrappers = typeof(ScriptedBot).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(IInputSource).IsAssignableFrom(t)
                        && t != typeof(ScriptedBot)
                        && t.GetConstructors().Any(c =>
                            c.GetParameters().Any(p => p.ParameterType == typeof(ScriptedBot))))
            .ToArray();

        // Not vacuous: HybridBot takes a ScriptedBot today.
        Assert.NotEmpty(wrappers);

        foreach (var type in wrappers)
        {
            Assert.True(
                typeof(IWrapsScriptedBot).IsAssignableFrom(type),
                $"{type.Name} wraps a ScriptedBot but does not implement IWrapsScriptedBot, "
                + "so ScriptedBot.Of cannot see through it and the server will treat the "
                + "slot as though it holds no bot at all");
        }
    }

    /// <summary>
    /// <c>--bot-mode search</c> reaches the population service, and the bots it makes
    /// are still countable, retargetable and removable.
    /// </summary>
    [Fact]
    public void TheServerFillsWithWrappedBotsAndStillSeesThem()
    {
        var options = new ServerOptions
        {
            // ANY FREE PORT. This test bound the default 8190 and passed only
            // while nothing else held it — it went red the moment a real server was
            // running on this machine for a playtest, which is an environment this
            // test has no business caring about. ServerHarness has always done this.
            Port = 0,

            MapDirectory = MapDirectory.Resolve(),
            NavCacheDirectory = MapDirectory.ResolveNav(),
            Map = "ctf_truefort",
            Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
                Brain = BotBrainSpec.Of(new SearchMicroSpec(new RolloutOptions(Threads: 2))),
            },
        };

        using var server = new GameServer(options, new RecordingLog());
        server.Start();

        for (var i = 0; i < 90; i++)
            server.Tick();

        var slots = server.BotSlots;
        Assert.NotEmpty(slots);

        foreach (var slot in slots)
        {
            // The roster holds the WRAPPER (since D3, HumanizedBot.ByTier around
            // the HybridBot by default)...
            Assert.NotNull(HybridBot.Of(server.SourceOf(slot)));

            // ...and every site that wants the macro layer still reaches it.
            Assert.NotNull(ScriptedBot.Of(server.SourceOf(slot)));
        }
    }
}
