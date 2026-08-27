using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Server;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// <c>botPopulationUpdate</c>: a server that fills itself with bots.
/// </summary>
/// <remarks>
/// ⚠️ <b>The assertions are made from the server's own roster and from the
/// broadcast, not from the population service.</b> The claim is that a bot the
/// manager added is a player like any other — it holds a slot, it has a team and a
/// class, it spawns, and every client is told — and a test that asked the service
/// what it thought it had done would pass with none of that true.
/// <para>
/// ⚠️ <b>No fixture can gate any of this and none ever will.</b> No recording in
/// this project contains a bot, and the reference's own population manager runs in
/// the same event the socket path does, so it is invisible to a trace by
/// construction.
/// </para>
/// </remarks>
public sealed class BotPopulationTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    private static ServerHarness Filled(int fill, int maxBots = 8, int minHumans = 0) =>
        new(o =>
        {
            o.Map = Map;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.Bots = new BotPopulationOptions(
                Enabled: true,
                FillToPlayers: fill,
                MaxBots: maxBots,
                MinHumans: minHumans,
                Difficulty: 3);
        });

    // ------------------------------------------------------------------
    // The policy
    // ------------------------------------------------------------------

    /// <summary>
    /// The target is a fill, and it is zero below the human floor.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is a <em>fill</em> target, not a bot count.</b> A server filling to
    /// 8 with 3 humans on it runs 5 bots and drops one every time somebody joins.
    /// <para>
    /// ⚠️ <b>And below <c>MinHumans</c> it is zero rather than the fill</b>, which
    /// is the first test rather than a clamp on the last: an empty server running
    /// eight bots against each other looks full in the lobby list and is not.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTargetIsAFillAndIsZeroBelowTheHumanFloor()
    {
        var options = new BotPopulationOptions(Enabled: true, FillToPlayers: 8, MaxBots: 8, MinHumans: 1);

        Assert.Equal(0, BotPopulation.Target(0, in options));
        Assert.Equal(7, BotPopulation.Target(1, in options));
        Assert.Equal(5, BotPopulation.Target(3, in options));
        Assert.Equal(0, BotPopulation.Target(8, in options));
        Assert.Equal(0, BotPopulation.Target(20, in options));

        // ⚠️ The ceiling is on top of the fill, not instead of it.
        var capped = options with { MaxBots = 2 };
        Assert.Equal(2, BotPopulation.Target(1, in capped));

        // ⚠️ And a floor of zero really does mean "fill an empty server", which is
        // what a dedicated bot server wants. The control for the MinHumans branch:
        // the same call with a floor of 1 answers 0.
        var open = options with { MinHumans = 0 };
        Assert.Equal(8, BotPopulation.Target(0, in open));

        Assert.Equal(0, BotPopulation.Target(4, options with { Enabled = false }));
    }

    /// <summary>
    /// The class picker never rolls a spy or Quote.
    /// </summary>
    /// <remarks>
    /// Quote is out because it is not part of the normal roster — the draw is
    /// <c>irandom(8)</c>, nine classes, stopping short of it. ⚠️ The spy is out by
    /// request after playtesting, and not because the spy's own code is broken:
    /// almost everything it does is invisible to the rest of the bot model, so a
    /// spy doing it badly is indistinguishable from a bot that is broken and makes
    /// every other behaviour harder to judge.
    /// </remarks>
    [Fact]
    public void TheClassPickerSkipsTheSpyAndQuote()
    {
        var random = new GmlRandom(12345);
        var seen = new HashSet<PlayerClass>();

        for (var i = 0; i < 4000; i++)
            seen.Add(BotPopulation.PickClass(ref random));

        Assert.DoesNotContain(PlayerClass.Spy, seen);
        Assert.DoesNotContain(PlayerClass.Quote, seen);

        // ⚠️ And it really does reach every other class — a rejection loop with the
        // wrong bound would silently narrow the roster instead of excluding one.
        Assert.Equal(8, seen.Count);
    }

    /// <summary>
    /// The team picker takes the smaller team and tosses a coin on a tie.
    /// </summary>
    /// <remarks>
    /// ⚠️ The tie has to be random rather than a fixed side, or the first two bots
    /// on an empty server both land on red and the balance never recovers — every
    /// later pick sees blue smaller and feeds it, so the teams alternate around a
    /// permanent offset of one.
    /// </remarks>
    [Fact]
    public void TheTeamPickerTakesTheSmallerTeamAndTossesACoinOnATie()
    {
        var random = new GmlRandom(7);

        Assert.Equal(Team.Red, BotPopulation.PickTeam(1, 3, ref random));
        Assert.Equal(Team.Blue, BotPopulation.PickTeam(4, 2, ref random));

        var red = 0;
        var blue = 0;

        for (var i = 0; i < 500; i++)
        {
            if (BotPopulation.PickTeam(2, 2, ref random) == Team.Red)
                red++;
            else
                blue++;
        }

        Assert.True(red > 150 && blue > 150, $"the tie is not a coin: {red} red, {blue} blue");
    }

    /// <summary>
    /// A defaulted options value is the reference's defaults, not a zeroed struct.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A record struct's primary-constructor defaults do NOT apply to its
    /// parameterless form.</b> <c>new BotPopulationOptions()</c> resolves to the
    /// implicit zeroing constructor, so a "defaulted" value has a fill target of 0,
    /// a difficulty of 0 and a <b>null</b> name prefix — and every one of those is
    /// silent.
    /// <para>
    /// Measured, by running the thing: a server started with <c>--bots 12</c> named
    /// its bots "1".."12" instead of "[BOT] 1".."[BOT] 12", because the flag handler
    /// built its options with <c>with</c> from <c>ServerOptions.Bots</c>, which was
    /// initialised with <c>new()</c>. Nothing in the suite noticed, because every
    /// test that cared constructed its options explicitly.
    /// </para>
    /// <para>
    /// The same shape as <c>CharacterState.Fresh</c> and <c>PlayerState.Fresh</c>:
    /// in this codebase a zeroed value type is routinely not a valid one.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADefaultedOptionsValueCarriesTheReferencesDefaults()
    {
        var options = new ServerOptions().Bots;

        Assert.False(options.Enabled);
        Assert.Equal(8, options.FillToPlayers);
        Assert.Equal(8, options.MaxBots);
        Assert.Equal(1, options.MinHumans);
        Assert.Equal(3, options.Difficulty);
        Assert.Equal("[BOT] ", options.NamePrefix);

        // ⚠️ The control, and the whole reason this test exists: the zeroed form is
        // reachable and is NOT the same thing. If these two ever agree, the factory
        // has stopped doing anything and the trap is back.
        var zeroed = default(BotPopulationOptions);

        Assert.NotEqual(options, zeroed);
        Assert.Null(zeroed.NamePrefix);
        Assert.Equal(0, zeroed.FillToPlayers);
    }

    /// <summary>
    /// A bot the server added is named with the configured prefix.
    /// </summary>
    /// <remarks>
    /// ⚠️ The end of the same story: the prefix is the only part of the config a
    /// human sees in the scoreboard, and a null one reads as a player called "7".
    /// </remarks>
    [Fact]
    public void ABotIsNamedWithTheConfiguredPrefix()
    {
        using var harness = Filled(fill: 2, maxBots: 2);

        harness.Tick(5);

        var names = harness.Log.Lines.Where(l => l.StartsWith("bot ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, names.Length);
        Assert.All(names, n => Assert.StartsWith("bot [BOT] ", n, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // The server
    // ------------------------------------------------------------------

    /// <summary>
    /// A server with bots enabled fills itself, balanced, and every bot spawns.
    /// </summary>
    /// <remarks>
    /// <b>The gate for the whole item.</b> Nothing is added by hand: the server is
    /// started with a config and ticked, and everything after that — the target,
    /// the team, the class, the join broadcast, the spawn and a role for each — is
    /// the layer under test.
    /// <para>
    /// ⚠️ <b>The control is the same server with <c>Enabled</c> false</b>, which
    /// must stay empty. Without it every assertion here is also satisfied by a
    /// server that fills unconditionally, and "bots off" is the default.
    /// </para>
    /// </remarks>
    [Fact]
    public void AServerWithBotsEnabledFillsItself()
    {
        using var harness = Filled(fill: 8);

        harness.Tick(5);

        var slots = harness.Server.BotSlots;

        Assert.Equal(8, slots.Count);

        // ⚠️ Every bot is a real player: a team that is not spectator, a class, a
        // roster slot of its own, and a character in the world.
        var red = 0;
        var blue = 0;

        foreach (var slot in slots)
        {
            var team = harness.Server.TeamOf(slot);

            Assert.True(team is Team.Red or Team.Blue, $"bot in slot {slot} is on {team}");
            Assert.NotEqual(PlayerClass.Spy, harness.Server.ClassOf(slot));

            if (team == Team.Red)
                red++;
            else
                blue++;
        }

        Assert.Equal(4, red);
        Assert.Equal(4, blue);

        harness.TickUntil(
            () => harness.Server.BotSlots.All(harness.Server.IsAlive),
            "every bot to spawn");

        // ⚠️ Every bot got a policy with a role and an objective. A bot with no
        // objective still walks nowhere, which is the failure this whole item is
        // about and which is invisible from the roster.
        var defenders = 0;

        foreach (var slot in slots)
        {
            var bot = Assert.IsType<ScriptedBot>(harness.Server.SourceOf(slot));

            Assert.NotNull(bot.Objective);

            if (bot.Role == BotRole.Defend)
                defenders++;
        }

        Assert.True(defenders > 0, "a team of eight bots has no defenders at all");
        output.WriteLine($"{slots.Count} bots, {red} red / {blue} blue, {defenders} defending");

        // The control: bots off means no bots.
        using var quiet = new ServerHarness(o => o.Map = Map);
        quiet.Tick(5);

        Assert.Empty(quiet.Server.BotSlots);
    }

    /// <summary>
    /// A bot added by hand survives a server with the population manager off.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>botPopulationUpdate</c> opens with <c>if(!global.botsEnabled) exit;</c>,
    /// which means <b>do nothing</b> and not "the target is zero". Reading it as a
    /// target removes a hand-added bot on the very next tick — measured, it took
    /// out seven of <c>InputSourceTests</c>' eight tests, every one of which adds a
    /// bot to a default server.
    /// </remarks>
    [Fact]
    public void APopulationManagerThatIsOffRemovesNothing()
    {
        using var harness = new ServerHarness(o => o.Map = Map);

        var slot = harness.Server.AddBot(
            "hand-made", Team.Red, PlayerClass.Soldier,
            new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(3)));

        harness.Tick(10);

        Assert.Contains(slot, harness.Server.BotSlots);
    }

    /// <summary>
    /// Bots make room for humans and come back when they leave.
    /// </summary>
    /// <remarks>
    /// The fill target is over the total, so a joining human displaces a bot — and
    /// ⚠️ <b>a dead bot is displaced first.</b> A body vanishing in the middle of a
    /// fight is what the reference's removal timeout exists to avoid; taking the
    /// dead first gets the same outcome without a wall clock, which a fixed-tick
    /// server does not have.
    /// </remarks>
    [Fact]
    public void ABotMakesRoomForAHumanAndComesBack()
    {
        using var harness = Filled(fill: 4);

        harness.Tick(5);
        Assert.Equal(4, harness.Server.BotSlots.Count);

        harness.JoinClient("a human");
        harness.TickUntil(() => harness.Server.HumanCount == 1, "the human to be counted");
        harness.Tick(2);

        Assert.Equal(1, harness.Server.HumanCount);
        Assert.Equal(3, harness.Server.BotSlots.Count);

        // ⚠️ And the survivors are still a coherent roster: every slot the server
        // still calls a bot is driven by one, which is what RemoveSlot's compaction
        // would break if the population manager removed by a stale index.
        foreach (var slot in harness.Server.BotSlots)
            Assert.IsType<ScriptedBot>(harness.Server.SourceOf(slot));
    }

    /// <summary>
    /// A map rotation hands every surviving bot the new graph.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A follower, a route and a blacklist are all node indices into ONE
    /// graph.</b> A bot that crosses a rotation carrying them is holding numbers
    /// that now mean somewhere else — a route through a map that is no longer
    /// loaded, and a blacklist banning edges that were never flown. Its
    /// <c>BotObjective</c> holds the old graph and the old map's spawn points too.
    /// <para>
    /// The reference frees exactly this on a departure (<c>botNavRelease</c>) and
    /// re-teams every bot across a map change (<c>botReteamAll</c>); a rotation is
    /// the case where both matter at once.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARotationHandsEveryBotTheNewGraph()
    {
        // ⚠️ It starts on a TEAM DEATHMATCH map, and that is not incidental. TDM is
        // the only mode with NO OVERTIME — every other one can hang past its clock,
        // and CTF hangs precisely when a flag is off its base at 0:00. This test used
        // to start on ctf_truefort and was flaky for exactly that reason: once the
        // rocket jump landed, two bots reached the enemy flag often enough that some
        // runs went into an unbounded overtime and the wait below gave up after
        // 5,400 ticks with no "round over" in the log at all.
        //
        // gg_debug is a TDM map (basicRoomSetup's chain ends there for a map with no
        // objective) and both it and ctf_truefort have a nav cache, which the
        // assertions need on both sides of the rotation.
        using var harness = new ServerHarness(o =>
        {
            o.Map = "gg_debug";
            o.MapRotation = ["gg_debug", Map];
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.TimeLimitMinutes = 1;
            o.Bots = new BotPopulationOptions(Enabled: true, FillToPlayers: 2, MaxBots: 2, MinHumans: 0);
        });

        harness.Tick(5);
        Assert.Equal(2, harness.Server.BotSlots.Count);

        var bots = harness.Server.BotSlots
            .Select(s => (ScriptedBot)harness.Server.SourceOf(s))
            .ToArray();

        // ⚠️ The OBJECTIVE instances, captured now. Holding the bots and reading
        // `bot.Objective` after the rotation reads the new one — LINQ is lazy and
        // the property is live, so the first draft of this compared each new
        // objective against itself and passed for the wrong reason.
        var before = bots.Select(b => b.Objective).ToArray();

        foreach (var objective in before)
            Assert.NotNull(objective);

        // ⚠️ There has to BE nav state to invalidate, or the assertions below pass
        // on a rotation that does nothing: a bot re-spawned on the new map has no
        // route yet either way. So wait for a real route and plant a real ban.
        //
        // ⚠️ The goal is given BY HAND, because Team Deathmatch has no landmark and
        // the objective layer correctly declines to invent one — so on gg_debug a
        // bot plans nothing at all. That is the objective layer's own claim
        // (BotObjectiveTests.TeamDeathmatchGivesNoGoal) and not this test's; what is
        // under test here is that a rotation invalidates whatever nav state exists.
        var far = harness.Server.BotSlots
            .Select(slot => ((ScriptedBot)harness.Server.SourceOf(slot), slot))
            .ToArray();

        foreach (var (bot, _) in far)
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, "gg_debug", 1);
            var node = graph.NodeCount - 1;

            Assert.True(bot.SetGoal(NavGraph.ColumnWorldX(graph.Nodes[node].X0), graph.StandY(node)));
        }

        harness.TickUntil(
            () => bots.All(b => b.Path.HasPath),
            "both bots to plan a route on the first map",
            maxTicks: 600);

        foreach (var bot in bots)
            bot.Path.BlacklistEdge(fromNode: 3, toNode: 4, tick: 0);

        Assert.All(bots, b => Assert.NotEmpty(b.Path.Blacklist));

        // ⚠️ Wait for the map to be OPEN, not for the round to announce it. The
        // announcement — "round over ... koth_valley area 1 in ten seconds" — lands
        // 300 ticks before the load, so a log match exits the wait while the old
        // map is still running and every assertion below then reads the state this
        // test exists to say has changed.
        harness.TickUntil(
            () => harness.Server.CurrentMap == Map,
            "the map to rotate",
            maxTicks: 60 * 30 * 3);

        Assert.Equal(2, harness.Server.BotSlots.Count);

        foreach (var slot in harness.Server.BotSlots)
        {
            var bot = (ScriptedBot)harness.Server.SourceOf(slot);

            // ⚠️ A fresh objective, not the one built against ctf_truefort's graph.
            Assert.NotNull(bot.Objective);
            Assert.DoesNotContain(bot.Objective, before);

            // ⚠️ And no route or blacklist carried over — those are node indices.
            Assert.False(bot.Path.HasPath, $"bot in slot {slot} kept a route across the rotation");
            Assert.Empty(bot.Path.Blacklist);
        }
    }

    // ------------------------------------------------------------------
    // Item 5: the headroom
    // ------------------------------------------------------------------

    /// <summary>
    /// Twelve bots run at well under the per-tick budget.
    /// </summary>
    /// <remarks>
    /// <b>Phase 7's exit criterion, and the number this whole port exists to
    /// make possible.</b> A GG2 tick is 33.3ms; the assertion here is deliberately
    /// loose against that, because the point is an order of magnitude rather than a
    /// digit and a CI machine under load is not a benchmark.
    /// <para>
    /// ⚠️ <b>What is measured is a real server</b> — the population manager, twelve
    /// policies each planning and fighting, the whole simulation, the state
    /// serialiser and the broadcast — and not a bot in a scenario world. The
    /// difference is most of the cost.
    /// </para>
    /// <para>
    /// ⚠️ <b>The first ticks are not the steady state</b> and are dropped: the map
    /// load, the twelve joins and the first plan for each all land in them, and a
    /// mean over them measures start-up.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwelveBotsRunWellUnderTheTickBudget()
    {
        using var harness = Filled(fill: 12, maxBots: 12);

        harness.Tick(120);

        Assert.Equal(12, harness.Server.BotSlots.Count);

        const int Ticks = 600;
        var clock = Stopwatch.StartNew();

        harness.Tick(Ticks);

        clock.Stop();

        var perTick = clock.Elapsed.TotalMilliseconds / Ticks;
        const double Budget = 1000.0 / GameConstants.TickRate;

        output.WriteLine(
            $"12 bots on {Map}: {perTick:0.000}ms a tick against a {Budget:0.0}ms budget "
            + $"({perTick / Budget:P1} of it)");

        Assert.True(
            perTick < Budget / 4,
            $"{perTick:0.00}ms a tick with 12 bots, against a {Budget:0.0}ms budget");
    }
}
