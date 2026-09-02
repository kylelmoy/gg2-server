using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// <c>botPopulationUpdate</c> and <c>botReteamAll</c>: keeps a server's bot
/// population at its target and keeps every bot's role in step with the team it
/// is on.
/// </summary>
/// <remarks>
/// The policy is <see cref="BotPopulation"/>'s and lives in <c>Gg2.Bots</c>; this
/// is the half that needs a roster, a config and a socket to broadcast a join on.
/// <para>
/// ⚠️ <b>It draws from its own stream, never <c>WorldState.Random</c>.</b> The
/// match's stream is observable simulation — every seeded weapon puts its draw on
/// the wire — so a server that added a bot would shift the position every client
/// re-derives its spread from. Same rule as <c>ScriptedBot.Random</c>, one layer
/// up.
/// </para>
/// <para>
/// ⚠️ <b>A bot with no nav graph is still a bot.</b> A map with no cache generated
/// gets bots that fight whatever comes to them and never walk anywhere, which is
/// the same degradation <c>NavCache.TryLoad</c> is built for — a server should
/// keep serving a map it cannot route on rather than refuse to fill.
/// </para>
/// </remarks>
public sealed class BotPopulationService
{
    private readonly GameServer _server;
    private readonly IServerLog _log;

    // ⚠️ Its own, and not the match's. See the remarks above.
    private GmlRandom _random;

    private int _nameCounter;

    public BotPopulationService(GameServer server, IServerLog log, uint seed)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(log);

        _server = server;
        _log = log;
        _random = new GmlRandom(seed);
    }

    /// <summary>The map's nav graph, or null where no cache was found.</summary>
    public NavGraph? Nav { get; private set; }

    /// <summary>
    /// The edge crowding every bot on this server plans against.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>One store for the whole server, and it lives here because it is the
    /// only object that outlives a bot and knows about all of them.</b> Sharing is
    /// the point: it is what makes the mechanism model bots bunching up rather
    /// than approximating it with per-bot noise, and it is why the penalty is the
    /// same for every bot asking at the same moment.
    /// </remarks>
    public BotOccupancy Occupancy { get; } = new();

    /// <summary>The spawn points the objective layer measures a push against.</summary>
    public SpawnPoints? Spawns { get; private set; }

    /// <summary>
    /// Points the population at a newly loaded map.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Per map <em>and</em> per area.</b> A tall map is several rounds
    /// stacked vertically with one graph each, and only one area's entities exist
    /// — <c>cp_dirtbowl</c> has three graphs over one mask.
    /// <para>
    /// ⚠️ <b>Existing bots are re-roled here, and that is <c>botReteamAll</c>'s
    /// second pass.</b> A role counts the bots ahead of this one on its team, so a
    /// team that is still half-built gives the wrong count — every bot would see
    /// only the ones assigned before it and the every-Nth rule would land on the
    /// wrong bots. It is a second pass over a finished roster for exactly that
    /// reason.
    /// </para>
    /// </remarks>
    public void MapChanged(LevelMap map, int area, string? navCacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(map);

        Spawns = new SpawnPoints(map);
        Nav = null;

        if (navCacheDirectory is { Length: > 0 })
        {
            if (!NavCache.TryLoad(navCacheDirectory, map.Name, area, out var graph))
                _log.Info($"no nav cache for {map.Name} area {area}; bots on it will not navigate");

            Nav = graph;
        }

        // ⚠️ Hand every surviving bot the new graph BEFORE re-roling them. A
        // follower, a route and a blacklist are all node indices into one graph,
        // so a bot that crosses a rotation carrying them is holding numbers that
        // now mean somewhere else — and its BotObjective holds the old graph and
        // the old map's spawn points too.
        // ⚠️ The counts go first, and the bots' flags go with SetMap. Occupancy
        // keys are node indices, which only mean anything to ONE build of the
        // graph, so counts from the previous map would otherwise sit here making
        // arbitrary edges of the new one look busy — and dropping the maps without
        // clearing the flags is worse still, because a bot that believes its route
        // is counted declines the next removal and is then added twice.
        Occupancy.Reset();

        foreach (var slot in _server.BotSlots)
        {
            if (ScriptedBot.Of(_server.SourceOf(slot)) is { } bot)
                bot.SetMap(Nav);
        }

        Retarget();
    }

    /// <summary>
    /// One population tick: add or remove bots to reach the target.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Removal is <em>not</em> the mirror of addition.</b> The reference
    /// prefers to drop a bot that is already dead — a body vanishing mid-fight is
    /// worse than a slot staying full for a few seconds — and this port does the
    /// same by taking dead bots first. The reference's deferred-removal timeout is
    /// deliberately not transcribed: it is wall-clock (<c>current_time</c>) inside
    /// a fixed-tick server, and the same intent falls out of preferring the dead.
    /// </remarks>
    public void Service()
    {
        var options = _server.Options.Bots;

        // ⚠️ `if(!global.botsEnabled) exit;` means DO NOTHING, not "target zero",
        // and the difference is not academic: a bot added by hand on a server with
        // the population manager switched off must survive. Reading it as a target
        // of zero removes it on the very next tick — which is exactly what happened
        // here, and it took out seven tests that add a bot to a default server.
        if (!options.Enabled)
            return;

        var target = BotPopulation.Target(_server.HumanCount, in options);
        var bots = _server.BotSlots.Count;

        if (bots == target)
            return;

        if (bots < target)
        {
            for (var i = bots; i < target; i++)
            {
                if (!TryAdd(in options))
                    break;
            }
        }
        else
        {
            for (var i = bots; i > target; i--)
                RemoveOne();
        }

        Retarget();
    }

    private bool TryAdd(in BotPopulationOptions options)
    {
        if (_server.RosterCount >= WorldState.MaxSlots)
            return false;

        _server.CountTeams(out var red, out var blue);

        var team = BotPopulation.PickTeam(red, blue, ref _random);
        var playerClass = BotPopulation.PickClass(ref _random);

        _nameCounter++;
        var name = options.NamePrefix + _nameCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ⚠️ The stagger is the roster slot the bot is about to land in, and it is
        // load-bearing twice over: it phases the objective and combat cadences so a
        // full server does not pay every bot's scan on one frame, and it is added
        // to the follower's re-plan period, where zero makes BOT_TAKEOFF_PATIENCE
        // unreachable. See docs/TRAPS-BOTS.md, "Phase 7 item 4b-ii".
        var stagger = _server.RosterCount;

        var bot = new ScriptedBot(Nav, new NavQuery(team), BotSkill.ForTier(options.Difficulty), stagger)
        {
            SpreadX = BotRoles.SpreadX(stagger),
            Occupancy = Occupancy,
        };

        // Null where the map has no cache, which leaves the bot fighting whatever
        // comes to it rather than refusing to exist.
        if (Nav is not null && Spawns is not null)
            bot.Objective = new BotObjective(Nav, Spawns);

        // ⛔ THE WRAPPER GOES ON BEFORE AddBot, so the slot never briefly holds a
        // bare ScriptedBot that something else could latch onto, and it wraps the
        // SAME instance the lines above configured — the search owns keys and aim
        // inside its engagement window and delegates everything else back.
        //
        // ⚠️ Every site that wants the macro layer asks ScriptedBot.Of, never
        // `is ScriptedBot`. See IWrapsScriptedBot for what breaks otherwise, and
        // note the Query assignment below still reaches the right object.
        IInputSource source = options.SearchMicro is { } search
            ? new SearchBot(new RolloutSearch(search), bot)
            : bot;

        var slot = _server.AddBot(name, team, playerClass, source);

        // ⚠️ The query has to carry the class the server actually gave it, which
        // is checkClasslimits' answer and not the one asked for — the search's
        // double-jump and rocket-jump arcs are per class, and a scout routed as a
        // heavy walks past every edge it could have flown.
        bot.Query = new NavQuery(_server.TeamOf(slot), CanDoubleJump: BotClassProfile.AirJump(_server.ClassOf(slot)));

        return true;
    }

    private void RemoveOne()
    {
        var slots = _server.BotSlots;

        if (slots.Count == 0)
            return;

        // ⚠️ Prefer a bot that is already dead. A body vanishing in the middle of
        // a fight is the thing the reference's removal timeout exists to avoid,
        // and taking the dead first gets the same outcome without a wall clock.
        var pick = -1;

        foreach (var slot in slots)
        {
            if (!_server.IsAlive(slot))
            {
                pick = slot;
                break;
            }
        }

        if (pick < 0)
            pick = slots[^1];

        // ⚠️ `botNavRelease`'s half of the bookkeeping: a bot that is gone is no
        // longer walking its route, and leaving the counts up charges every future
        // search on that team extra for an edge nothing is on. This is the third
        // of the three removal sites the idempotence flag exists for.
        if (ScriptedBot.Of(_server.SourceOf(pick)) is { } leaving)
            Occupancy.Remove(leaving.Path);

        _server.RemoveBot(pick);
    }

    /// <summary>
    /// Re-runs the role assignment across every bot.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A second pass over a finished roster, never inside the first.</b> A
    /// role counts this bot's place among its team's bots that share its class
    /// period, so assigning as you go gives every bot a count that only includes
    /// the ones before it — and the every-Nth rule then lands on the wrong bots.
    /// <para>
    /// It runs after every add and every removal as well as after a map change,
    /// because <see cref="WorldState.RemoveSlot"/> compacts the roster: a
    /// departure two slots up renumbers everyone above it and every bot's place in
    /// its lean group with them.
    /// </para>
    /// </remarks>
    public void Retarget()
    {
        var earlier = new List<PlayerClass>();

        foreach (var slot in _server.BotSlots)
        {
            var team = _server.TeamOf(slot);
            var playerClass = _server.ClassOf(slot);

            if (ScriptedBot.Of(_server.SourceOf(slot)) is not { } bot)
                continue;

            earlier.Clear();

            foreach (var other in _server.BotSlots)
            {
                if (other >= slot)
                    break;

                if (_server.TeamOf(other) == team)
                    earlier.Add(_server.ClassOf(other));
            }

            bot.Role = BotRoles.Assign(playerClass, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(earlier));
            bot.SpreadX = BotRoles.SpreadX(slot);
            bot.Query = new NavQuery(team, CanDoubleJump: BotClassProfile.AirJump(playerClass));

            // ⚠️ Assigned rather than defaulted, because SetMap clears it: a
            // BotObjective holds the graph and the map's spawn points, so keeping
            // one across a rotation is keeping the previous map's answer.
            bot.Objective = Nav is not null && Spawns is not null
                ? new BotObjective(Nav, Spawns)
                : null;
        }
    }
}
