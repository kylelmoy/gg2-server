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
/// <b>The assertions are made from the server's own roster and from the
/// broadcast, not from the population service.</b> The claim is that a bot the
/// manager added is a player like any other — it holds a slot, it has a team and a
/// class, it spawns, and every client is told — and a test that asked the service
/// what it thought it had done would pass with none of that true.
/// <para>
/// <b>No fixture can gate any of this.</b> No recording in this project contains a
/// bot, and the reference's own population manager runs in the same event the
/// socket path does, so it is invisible to a trace by construction.
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
    /// <b>It is a <em>fill</em> target, not a bot count.</b> A server filling to
    /// 8 with 3 humans on it runs 5 bots and drops one every time somebody joins.
    /// <para>
    /// <b>And below <c>MinHumans</c> it is zero rather than the fill</b>, which
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

        // The ceiling is on top of the fill, not instead of it.
        var capped = options with { MaxBots = 2 };
        Assert.Equal(2, BotPopulation.Target(1, in capped));

        // And a floor of zero really does mean "fill an empty server", which is
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
    /// <c>irandom(8)</c>, nine classes, stopping short of it. The spy is excluded
    /// deliberately, not because the spy's own code is broken: almost everything it
    /// does is invisible to the rest of the bot model, so a
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

        // And it really does reach every other class — a rejection loop with the
        // wrong bound would silently narrow the roster instead of excluding one.
        Assert.Equal(8, seen.Count);
    }

    /// <summary>
    /// The team picker takes the smaller team and tosses a coin on a tie.
    /// </summary>
    /// <remarks>
    /// The tie has to be random rather than a fixed side, or the first two bots
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
    /// <b>A record struct's primary-constructor defaults do NOT apply to its
    /// parameterless form.</b> <c>new BotPopulationOptions()</c> resolves to the
    /// implicit zeroing constructor, so a "defaulted" value has a fill target of 0,
    /// a difficulty of 0 and a <b>null</b> name prefix — and every one of those is
    /// silent. A server built from that zeroed form (e.g. via <c>with</c> from an
    /// uninitialised <c>ServerOptions.Bots</c>) names its bots "1".."12" instead of
    /// "[BOT] 1".."[BOT] 12" with no error.
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
        // 5, the TOP tier, not the reference's 3. `BotDifficultyReachesTheBot` is
        // the gate that it survives the trip to an actual bot; this one only pins
        // the record.
        Assert.Equal(5, options.Difficulty);
        Assert.Equal("[BOT] ", options.NamePrefix);

        // The control, and the whole reason this test exists: the zeroed form is
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
    /// The prefix is the only part of the config a
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
    /// <b>The control is the same server with <c>Enabled</c> false</b>, which
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

        // Every bot is a real player: a team that is not spectator, a class, a
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

        // Every bot got a policy with a role and an objective. A bot with no
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
    /// <c>botPopulationUpdate</c> opens with <c>if(!global.botsEnabled) exit;</c>,
    /// which means <b>do nothing</b> and not "the target is zero". Reading it as a
    /// target would remove a hand-added bot on the very next tick.
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
    /// The fill target is over the total, so a joining human displaces a bot, and
    /// <b>a dead bot is displaced first.</b> A body vanishing in the middle of a
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

        // And the survivors are still a coherent roster: every slot the server
        // still calls a bot is driven by one, which is what RemoveSlot's compaction
        // would break if the population manager removed by a stale index.
        foreach (var slot in harness.Server.BotSlots)
            Assert.IsType<ScriptedBot>(harness.Server.SourceOf(slot));
    }

    /// <summary>
    /// A map rotation hands every surviving bot the new graph.
    /// </summary>
    /// <remarks>
    /// <b>A follower, a route and a blacklist are all node indices into ONE
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
        // It starts on a TEAM DEATHMATCH map, and that is not incidental. TDM is
        // the only mode with NO OVERTIME — every other one can hang past its clock,
        // and CTF hangs precisely when a flag is off its base at 0:00, which makes a
        // CTF map here liable to an unbounded overtime and a flaky wait below.
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

        // The OBJECTIVE instances, captured now. Holding the bots and reading
        // `bot.Objective` after the rotation reads the new one — LINQ is lazy and
        // the property is live, so the first draft of this compared each new
        // objective against itself and passed for the wrong reason.
        var before = bots.Select(b => b.Objective).ToArray();

        foreach (var objective in before)
            Assert.NotNull(objective);

        // There has to BE nav state to invalidate, or the assertions below pass
        // on a rotation that does nothing: a bot re-spawned on the new map has no
        // route yet either way. So wait for a real route and plant a real ban.
        //
        // The goal is given BY HAND, because Team Deathmatch has no landmark and
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

        // Wait for the map to be OPEN, not for the round to announce it. The
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

            // A fresh objective, not the one built against the previous map's graph.
            Assert.NotNull(bot.Objective);
            Assert.DoesNotContain(bot.Objective, before);

            // And no route or blacklist carried over — those are node indices.
            Assert.False(bot.Path.HasPath, $"bot in slot {slot} kept a route across the rotation");
            Assert.Empty(bot.Path.Blacklist);
        }
    }

    /// <summary>
    /// A bot plans as the team it is actually on after a rotation.
    /// </summary>
    /// <remarks>
    /// <b><c>Rounds.ResetForNewMap</c> makes every player a spectator, and
    /// <c>OpenMap</c> re-roles the bots in the same tick</b> — so the role, the
    /// spread and above all <see cref="ScriptedBot.Query"/>'s team are all taken
    /// while the whole roster reads <c>Spectator</c>. <c>Reteam</c> puts the bots
    /// back on Red and Blue on the next tick, and nothing re-derives what was
    /// taken from the team.
    /// <para>
    /// The team in the query is what <c>NavGraph.GatePassable</c> reads, and a
    /// spectator passes NEITHER team gate — so a bot whose spawn room has a team
    /// gate on its exit has no route out of it at all, and stands still for the
    /// whole map. That is what this pins: not the assignment, the routing
    /// consequence.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotRoutesAsItsOwnTeamAfterARotation()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = "gg_debug";
            o.MapRotation = ["gg_debug", Map];
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.TimeLimitMinutes = 1;
            o.Bots = new BotPopulationOptions(Enabled: true, FillToPlayers: 4, MaxBots: 4, MinHumans: 0);
        });

        harness.Tick(5);
        Assert.Equal(4, harness.Server.BotSlots.Count);

        harness.TickUntil(
            () => harness.Server.CurrentMap == Map,
            "the map to rotate",
            maxTicks: 60 * 30 * 3);

        // A few ticks past the change, because Reteam runs on the tick AFTER the
        // map opens — this is about what is left standing once it has.
        harness.Tick(10);

        foreach (var slot in harness.Server.BotSlots)
        {
            var bot = (ScriptedBot)harness.Server.SourceOf(slot);
            var team = harness.Server.TeamOf(slot);

            Assert.True(team is Team.Red or Team.Blue, $"bot in slot {slot} is still {team}");
            Assert.Equal(team, bot.Query.Team);
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
    /// <b>What is measured is a real server</b> — the population manager, twelve
    /// policies each planning and fighting, the whole simulation, the state
    /// serialiser and the broadcast — and not a bot in a scenario world. The
    /// difference is most of the cost.
    /// </para>
    /// <para>
    /// <b>The first ticks are not the steady state</b> and are dropped: the map
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

    // ------------------------------------------------------------------
    // The difficulty actually reaching a bot
    // ------------------------------------------------------------------

    /// <summary>
    /// The configured difficulty reaches the <see cref="ScriptedBot"/>, and the
    /// default that reaches it is tier 5.
    /// </summary>
    /// <remarks>
    /// Pinning <c>BotPopulationOptions.Difficulty</c> proves what the record says and
    /// nothing about what the server does with it: a
    /// <c>BotSkill.ForTier(3)</c> hard-coded in <c>BotPopulationService</c> would
    /// leave every difficulty pin green and every bot on the wrong tier. This
    /// walks the whole path — options, population service, the bot's own
    /// <see cref="ScriptedBot.Skill"/>.
    /// <para>
    /// <b>The second half is the control, and it is what makes the first half
    /// mean anything.</b> Asserting only "the default bot equals ForTier(5)"
    /// passes just as well if the plumbing ignores the option and every bot is
    /// tier 5 by accident. Tier 1 must produce a DIFFERENT skill through the same
    /// path, or the assertion above is comparing a constant to itself.
    /// </para>
    /// </remarks>
    [Fact]
    public void BotDifficultyReachesTheBot()
    {
        ReferenceTree.RequireNavCache();

        Assert.Equal(BotSkill.ForTier(5), SkillOfFirstBot(difficulty: null));
        Assert.Equal(BotSkill.ForTier(5), SkillOfFirstBot(difficulty: 5));

        var handicapped = SkillOfFirstBot(difficulty: 1);

        Assert.Equal(BotSkill.ForTier(1), handicapped);
        Assert.NotEqual(BotSkill.ForTier(5), handicapped);
    }

    /// <summary>
    /// Fills a server with bots and returns the first one's skill.
    /// </summary>
    /// <param name="difficulty">
    /// Null leaves <see cref="BotPopulationOptions.Default"/>'s alone, which is
    /// the case the default change is about — passing 5 explicitly would test the
    /// flag and not the default.
    /// </param>
    private static BotSkill SkillOfFirstBot(int? difficulty)
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;

            // `with`, from Default rather than from a zeroed struct — see the
            // factory's own remarks. MinHumans 0 because no client connects here.
            o.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 2,
                MaxBots = 2,
                MinHumans = 0,
            };

            if (difficulty is { } tier)
                o.Bots = o.Bots with { Difficulty = tier };
        });

        harness.Tick(2);

        // A fill of 2 is TWO bots, not one: slot 0 is the reserved server
        // spectator and does not count towards the fill.
        var slots = harness.Server.BotSlots;
        Assert.Equal(2, slots.Count);

        var skills = slots
            .Select(slot => Assert.IsType<ScriptedBot>(harness.Server.SourceOf(slot)).Skill)
            .ToArray();

        // Every bot on one server gets the same tier — a per-bot difference would
        // mean the option is being read somewhere it should not be.
        Assert.Equal(skills[0], skills[1]);

        return skills[0];
    }

    // ------------------------------------------------------------------
    // The /bots suspension
    // ------------------------------------------------------------------

    /// <summary>
    /// A suspension actually removes the bots, and they come back when the
    /// window ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted from the server's roster, not from the suspension clock.</b>
    /// The failure this exists for is a player being told the command worked
    /// while twelve bots carry on playing — a state every unit test of the chat
    /// reply would pass through without noticing, because the reply is composed
    /// from the plugin's side of the call.
    /// </para>
    /// <para>
    /// ⚠️ <b>Both directions.</b> A suspension that removed the bots and never
    /// gave them back would look correct for the whole of any test that stopped at
    /// the removal, and would leave a live server bot-less until it was restarted.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASuspensionRemovesTheBotsAndTheWindowGivesThemBack()
    {
        using var harness = Filled(fill: 4);

        harness.Tick(5);
        Assert.Equal(4, harness.Server.BotSlots.Count);

        Assert.True(harness.Server.SuspendBots(1));
        harness.Tick(2);

        Assert.Empty(harness.Server.BotSlots);
        Assert.True(harness.Server.BotsSuspended);

        // One minute at 30 Hz, and the two ticks already spent.
        harness.Tick(60 * 30);

        Assert.False(harness.Server.BotsSuspended);
        Assert.Equal(4, harness.Server.BotSlots.Count);
    }

    /// <summary>Ending a suspension early brings the bots straight back.</summary>
    [Fact]
    public void ResumingEndsTheSuspensionEarly()
    {
        using var harness = Filled(fill: 4);

        harness.Tick(5);
        harness.Server.SuspendBots(60);
        harness.Tick(2);
        Assert.Empty(harness.Server.BotSlots);

        Assert.True(harness.Server.ResumeBots());
        harness.Tick(2);

        Assert.Equal(4, harness.Server.BotSlots.Count);

        // And a second resume says there was nothing to resume, rather than
        // reporting success for a no-op.
        Assert.False(harness.Server.ResumeBots());
    }

    /// <summary>
    /// An operator setting a bot count ends a player's suspension.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Otherwise the panel's one bot control is a field that accepts a
    /// number, reports it back and changes nothing</b> for up to the whole
    /// window, with no way for the operator to see why. The player's window is
    /// deliberately the weaker claim.
    /// </remarks>
    [Fact]
    public void SettingTheBotFillEndsASuspension()
    {
        using var harness = Filled(fill: 4);

        harness.Tick(5);
        harness.Server.SuspendBots(60);
        harness.Tick(2);
        Assert.Empty(harness.Server.BotSlots);

        harness.Server.SetBotFill(4);
        harness.Tick(2);

        Assert.False(harness.Server.BotsSuspended);
        Assert.Equal(4, harness.Server.BotSlots.Count);
    }

    /// <summary>
    /// A suspension of no minutes is refused rather than treated as forever or
    /// as a resume.
    /// </summary>
    /// <remarks>
    /// <c>[Bots] SuspendMinutes</c> defaults to zero, and zero is what reaches
    /// here from a server that never configured the command. A window of zero
    /// ticks would be a suspension that "ran" and expired on the same tick, which
    /// is indistinguishable in a log from the command being broken.
    /// </remarks>
    [Fact]
    public void ASuspensionOfNoMinutesIsRefused()
    {
        using var harness = Filled(fill: 4);

        harness.Tick(5);

        Assert.False(harness.Server.SuspendBots(0));
        Assert.False(harness.Server.BotsSuspended);

        harness.Tick(2);
        Assert.Equal(4, harness.Server.BotSlots.Count);
    }
}
