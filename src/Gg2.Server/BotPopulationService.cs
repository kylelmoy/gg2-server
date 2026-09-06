using System.Diagnostics;
using System.Runtime.InteropServices;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Nav;
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
/// <b>It draws from its own stream, never <c>WorldState.Random</c>.</b> The
/// match's stream is observable simulation — every seeded weapon puts its draw on
/// the wire — so a server that added a bot would shift the position every client
/// re-derives its spread from. Same rule as <c>ScriptedBot.Random</c>, one layer
/// up.
/// </para>
/// <para>
/// <b>A bot with no nav graph is still a bot.</b> A map with no graph gets bots
/// that fight whatever comes to them and never walk anywhere, which is the same
/// degradation <c>NavCache.TryLoad</c> is built for — a server should keep serving
/// a map it cannot route on rather than refuse to fill.
/// <para>
/// <b>It is also the last resort now rather than the ordinary outcome.</b>
/// <see cref="Generate"/> builds a graph for a map that has none, so the
/// degradation is reached only by <c>--no-navgen</c> or by a map the generator
/// throws on.
/// </para>
/// </para>
/// </remarks>
public sealed class BotPopulationService
{
    private readonly GameServer _server;
    private readonly IServerLog _log;

    // Its own, and not the match's. See the remarks above.
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
    /// <b>One store for the whole server, and it lives here because it is the
    /// only object that outlives a bot and knows about all of them.</b> Sharing is
    /// the point: it is what makes the mechanism model bots bunching up rather
    /// than approximating it with per-bot noise, and it is why the penalty is the
    /// same for every bot asking at the same moment.
    /// </remarks>
    public BotOccupancy Occupancy { get; } = new();

    /// <summary>
    /// The precomputed A\* distance tables for <see cref="Nav"/>, or null when
    /// <see cref="ServerOptions.NavDistances"/> is off — which is the default.
    /// </summary>
    /// <remarks>
    /// Held here beside <see cref="Nav"/> and <see cref="Occupancy"/> because it
    /// has exactly their lifetime: derived at a map change, handed to every bot,
    /// and meaningless the moment the graph is replaced.
    /// </remarks>
    public NavDistanceSet? Distances { get; private set; }

    // What Distances was built for. A graph is re-parsed at every round, so the
    // object is never the same twice and reference equality cannot answer this;
    // the map, area and graph size can. ⚠️ The assumption is that one map+area
    // yields one graph, which holds because a graph is either a file that is not
    // changing under the running server or a deterministic build of that map —
    // and the sizes are carried so a regenerated graph of a different shape
    // still misses. A regenerated graph of the SAME shape but different edges
    // would go stale here; nothing observed can produce one.
    private string? _distancesKey;

    /// <summary>The spawn points the objective layer measures a push against.</summary>
    public SpawnPoints? Spawns { get; private set; }

    /// <summary>
    /// Points the population at a newly loaded map.
    /// </summary>
    /// <remarks>
    /// <b>Per map <em>and</em> per area.</b> A tall map is several rounds
    /// stacked vertically with one graph each, and only one area's entities exist
    /// — <c>cp_dirtbowl</c> has three graphs over one mask.
    /// <para>
    /// <b>Existing bots are re-roled here, and that is <c>botReteamAll</c>'s
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

        // ⚠️ Whether the graph on disk was refused rather than absent. Both end
        // with no graph, but only one of them means the file is wrong, and the
        // generated replacement should be SAVED over a stale graph and merely held
        // in memory when there was never one — see Generate.
        var refused = false;

        if (navCacheDirectory is { Length: > 0 })
        {
            // The hash goes INTO TryLoad, which is what actually refuses a
            // stale graph, and the manifest is consulted again here only to say
            // which of the two failures happened. The duplicated read is a
            // manifest-sized file against a 9 ms parse, and it buys a message an
            // operator can act on: "no graph" and "the graph is for a different
            // map" have completely different fixes, and before this they were
            // the same silent shrug. NavManifest.
            var fingerprint = NavManifest.HashOf(map);

            if (NavManifest.Load(navCacheDirectory).Check(NavCache.Key(map.Name, area), fingerprint)
                == NavMatch.Mismatch)
            {
                refused = true;

                _log.Info(
                    $"nav graph for {map.Name} area {area} was built against different geometry "
                    + "and has been refused. Rebuild it with `gg2-server navgen --map "
                    + $"{map.Name}`, or delete nav/manifest.tsv's line for it to load it anyway");
            }
            else if (!NavCache.TryLoad(navCacheDirectory, map.Name, area, out var graph, fingerprint))
            {
                // Quiet: Generate below is about to make one, and saying "no nav
                // cache" a line before "built a nav graph" describes a problem the
                // operator does not have. It stays loud when generation is off.
                if (!_server.Options.NavGenerate)
                    _log.Warn($"no nav cache for {map.Name} area {area}; bots on it will not navigate");
            }
            else
            {
                Nav = graph;

                // Both containers for one map means one of them is being
                // ignored, and the loser is invisible from the outside. The
                // usual cause is an outside generator run into a directory that ships
                // .txt.gz: the fresh .txt wins, which is what was wanted, but
                // saying so is the difference between a regeneration that
                // worked and one that only appeared to. NavCache.FindFile.
                if (NavCache.HasBothContainers(navCacheDirectory, map.Name, area))
                {
                    _log.Info(
                        $"{map.Name} area {area} has both a .txt and a .txt.gz nav graph; "
                        + "using the .txt. Delete one — `gg2-server navgen` writes only "
                        + "the container this repository ships");
                }
            }
        }

        // Everything a graph needs is in the map's own PNG, so a map with no
        // graph is missing data nothing had to be missing. This is the whole of
        // what makes a CUSTOM map navigable: without it a server serving anything
        // outside the checked-in graphs has bots that fight whatever reaches them
        // and walk nowhere, which reads as a broken bot layer.
        if (Nav is null && _server.Options.NavGenerate)
            Nav = Generate(map, area, navCacheDirectory, refused);

        // Hand every surviving bot the new graph BEFORE re-roling them. A
        // follower, a route and a blacklist are all node indices into one graph,
        // so a bot that crosses a rotation carrying them is holding numbers that
        // now mean somewhere else — and its BotObjective holds the old graph and
        // the old map's spawn points too.
        // The counts go first, and the bots' flags go with SetMap. Occupancy
        // keys are node indices, which only mean anything to ONE build of the
        // graph, so counts from the previous map would otherwise sit here making
        // arbitrary edges of the new one look busy — and dropping the maps without
        // clearing the flags is worse still, because a bot that believes its route
        // is counted declines the next removal and is then added twice.
        Occupancy.Reset();

        // Derived here for the same reason the graph is generated here: a map
        // change is the one moment nothing is being simulated, and this costs
        // tens of milliseconds. It must also come BEFORE SetMap hands the bots
        // the new graph, so no bot can plan a route against the old set.
        Distances = LoadDistances(map, area, navCacheDirectory);

        foreach (var slot in _server.BotSlots)
        {
            if (ScriptedBot.Of(_server.SourceOf(slot)) is { } bot)
            {
                bot.SetMap(Nav);
                bot.Distances = Distances;
            }
        }

        Retarget();
    }

    /// <summary>
    /// Builds the distance tables for the new graph, when asked for.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>On the tick thread at the map change, like the nav generator, and
    /// logged for the same reason</b> — an operator who turned this on should be
    /// able to read what it cost rather than infer it from a hitch. The table
    /// count is worth printing beside the size: it is not eight, and how far
    /// below eight it is depends on which class arcs the graph actually has.
    /// </remarks>
    private NavDistanceSet? LoadDistances(LevelMap map, int area, string? navCacheDirectory)
    {
        if (!_server.Options.NavDistances || Nav is null)
        {
            _distancesKey = null;
            return null;
        }

        // ⚠️ MapChanged runs at every ROUND, not only at every map change — a
        // rotation that stays on one map reloads its graph each time — so
        // without this the server pays a full derivation every round end. Found
        // by running the server and reading its log, not by a test: every gate
        // here builds a set once and could not see a rebuild.
        var key = $"{map.Name}/{area}/{Nav.NodeCount}/{Nav.EdgeCount}";

        // ⚠️ No "and Distances is not null" here: a graph this REFUSED must stay
        // refused for that graph, or the failing case retries a several-hundred-
        // millisecond derivation at every round end — the same bug the key was
        // added to fix, wearing the failure path as a disguise.
        if (_distancesKey == key)
            return Distances;

        var started = Stopwatch.GetTimestamp();
        NavDistanceSet set;
        string how;

        try
        {
            // ⚠️ Reads <key>.dist.gz beside the graph and DERIVES only when there
            // is not a usable one. The file stores the next-hop half alone and
            // the cost half is rebuilt from it, which is 5.7x cheaper than the
            // Dijkstras — so a map that ships tables costs single-digit
            // milliseconds here and a custom map, whose graph was built moments
            // ago and has no file, still works at the old price.
            set = NavDistanceSet.LoadOrDerive(navCacheDirectory, map.Name, area, Nav, out how);
        }
        catch (ArgumentException e)
        {
            // A table is N**2 floats, so a large enough CUSTOM map overflows the
            // index and NavDistances.Derive refuses. Degrade to the straight-line
            // heuristic rather than refusing to serve the map — that is the same
            // answer NavCache.TryLoad and Generate give, and it is the DEFAULT
            // behaviour, so nothing is lost but the speed-up.
            _distancesKey = key;
            _log.Info(
                $"could not precompute nav distances for {map.Name} area {area}: {e.Message}. "
                + "Bots on it use the straight-line heuristic, as they do with --no-nav-distances");

            return null;
        }

        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        _distancesKey = key;

        _log.Info(
            $"nav distances for {map.Name} area {area}: {how} — "
            + $"{set.TableCount} table(s), {set.Bytes / 1048576.0:0.0} MB, {ms:0} ms"
            + (how == "loaded"
                ? string.Empty
                : ". Run `gg2-server navgen` to ship the tables and skip this next time"));

        return set;
    }

    /// <summary>
    /// Builds the graph for a map that has none, and optionally saves it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It runs on the tick thread, at the map change, and it is not
    /// free</b> — a few hundred milliseconds on the largest maps. That is
    /// deliberate placement rather than an oversight: a map change is the one
    /// moment nothing is being simulated and every client is loading, so a stall
    /// there disturbs nothing, where a background build would have to hand a graph
    /// to bots that had already planned routes without one. The elapsed time is
    /// logged so an operator can see the cost instead of inferring it from a hitch.
    /// <para>
    /// ⚠️ <b>The text goes through <c>NavCache.Parse</c> rather than into a graph
    /// directly.</b> That is the same call a file off disk gets, so a generated
    /// graph and a generated file are the same graph by construction — and it is
    /// why <c>Gg2.Nav</c> produces text and does not reference this assembly at
    /// all.
    /// </para>
    /// <para>
    /// ⚠️ <b>A refused graph is overwritten even when saving is off.</b> The file
    /// on disk describes terrain that is not there; leaving it means every restart
    /// refuses it again and rebuilds, and any other tool reading the directory
    /// still gets the wrong graph. A map that simply had none is different — that
    /// is not a fault to correct, and writing into an operator's directory
    /// uninvited is a surprise, so it is held in memory unless
    /// <see cref="ServerOptions.NavGenerateWrite"/> asks otherwise.
    /// </para>
    /// </remarks>
    private NavGraph? Generate(LevelMap map, int area, string? navCacheDirectory, bool refused)
    {
        NavBuildResult built;

        try
        {
            built = NavGenerator.Build(map, area);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            // ⚠️ A map this generator cannot handle must not take the server down
            // with it. The degradation is the one NavCache.TryLoad already
            // promises — bots that do not navigate — and it is strictly better
            // than refusing to serve the map.
            _log.Warn($"could not build a nav graph for {map.Name} area {area}: {e.Message}");
            return null;
        }

        NavGraph graph;

        try
        {
            graph = NavCache.Parse(built.Text, map.Name, area);
        }
        catch (FormatException e)
        {
            _log.Warn($"the nav graph built for {map.Name} area {area} did not parse: {e.Message}");
            return null;
        }

        var saved = string.Empty;

        if (navCacheDirectory is { Length: > 0 } && (_server.Options.NavGenerateWrite || refused))
        {
            try
            {
                NavCacheWriter.Write(navCacheDirectory, NavCache.Key(map.Name, area), built.Text, compress: true);

                // The manifest with it: a graph with no line is a graph nothing
                // checks against its map afterwards, which is the silent half of
                // the failure the manifest exists to make loud.
                RecordFingerprint(navCacheDirectory, NavCache.Key(map.Name, area), NavManifest.HashOf(map));
                saved = $", saved to {navCacheDirectory}";
            }
            catch (IOException e)
            {
                // A read-only install directory is a perfectly reasonable thing to
                // deploy into. The graph is already built and in memory.
                _log.Warn($"could not save the nav graph for {map.Name} area {area}: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                _log.Warn($"could not save the nav graph for {map.Name} area {area}: {e.Message}");
            }
        }

        _log.Info(
            $"built a nav graph for {map.Name} area {area} in "
            + $"{built.Elapsed.TotalMilliseconds:F0}ms — {built.Nodes} nodes, {built.Edges} edges{saved}");

        return graph;
    }

    /// <summary>
    /// Adds or replaces one graph's line in a nav directory's manifest.
    /// </summary>
    /// <remarks>
    /// Read-modify-write over the whole file, because <c>NavManifest.Write</c>
    /// writes a whole file: a server that saved one generated graph must not drop
    /// the other twenty-five lines on its way past.
    /// </remarks>
    private static void RecordFingerprint(string directory, string key, string hash)
    {
        var manifest = NavManifest.Load(directory);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = hash };

        foreach (var (map, area) in NavCache.List(directory))
        {
            var other = NavCache.Key(map, area);

            if (!string.Equals(other, key, StringComparison.Ordinal)
                && manifest.HashFor(other) is { } existing)
            {
                lines[other] = existing;
            }
        }

        NavManifest.Write(
            Path.Combine(directory, NavManifest.FileName),
            lines.Select(entry => (entry.Key, entry.Value)));
    }

    /// <summary>
    /// One population tick: add or remove bots to reach the target.
    /// </summary>
    /// <remarks>
    /// <b>Removal is <em>not</em> the mirror of addition.</b> The reference
    /// prefers to drop a bot that is already dead — a body vanishing mid-fight is
    /// worse than a slot staying full for a few seconds — and this port does the
    /// same by taking dead bots first. The reference's deferred-removal timeout is
    /// deliberately not transcribed: it is wall-clock (<c>current_time</c>) inside
    /// a fixed-tick server, and the same intent falls out of preferring the dead.
    /// </remarks>
    public void Service()
    {
        var options = _server.Options.Bots;

        // `if(!global.botsEnabled) exit;` means DO NOTHING, not "target zero",
        // and the difference is not academic: a bot added by hand on a server with
        // the population manager switched off must survive. Reading it as a target
        // of zero would remove it on the very next tick.
        if (!options.Enabled)
            return;

        var target = BotPopulation.Target(_server.HumanCount, in options);
        var slots = _server.BotSlots;
        var bots = slots.Count;

        // ABOVE the count check: a spectating bot is still a bot, so
        // `bots == target` holds and the early return below would leave it
        // spectating forever.
        //
        // `Rounds.ResetForNewMap` makes every player a spectator on a map change
        // — the reference's `with(Player) team = TEAM_SPECTATOR`. A human then
        // re-picks from the team-select screen; nothing re-picks for a bot, so
        // without this call every bot stops playing after the first rotation and
        // the server carries on ticking, rotating and reporting matches as
        // completed with nobody in them.
        Reteam(slots);

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

    /// <summary>
    /// Puts any spectating bot back on a team.
    /// </summary>
    /// <remarks>
    /// <b>Normally a no-op, and it has to be cheap for that reason</b> — it
    /// runs every tick. The scan is over <see cref="IGameServer.BotSlots"/>,
    /// which the caller has already materialised, and the team counts are taken
    /// only when there is actually somebody to place.
    /// <para>
    /// <b>Counted afresh for each bot</b>, not once for the batch: placing the
    /// first bot changes what the second should be given, and a batch that read
    /// one count would put every spectator on the same team after a rotation.
    /// </para>
    /// <para>
    /// <b>The request can be REFUSED and that is correct.</b>
    /// <c>RequestBotTeam</c> runs the reference's balance guard, which turns down
    /// a request for the bigger team — so a bot that cannot be placed this tick
    /// is simply tried again on the next one, and the loop must not assume it
    /// succeeded. Re-reading the team on the next call is what makes that safe.
    /// </para>
    /// </remarks>
    private void Reteam(IReadOnlyList<int> slots)
    {
        foreach (var slot in slots)
        {
            if (_server.TeamOf(slot) is Team.Red or Team.Blue)
                continue;

            _server.CountTeams(out var red, out var blue);
            _server.RequestBotTeam(slot, BotPopulation.PickTeam(red, blue, ref _random));
        }
    }

    private bool TryAdd(in BotPopulationOptions options)
    {
        if (_server.RosterCount >= WorldState.MaxSlots)
            return false;

        _server.CountTeams(out var red, out var blue);

        var team = BotPopulation.PickTeam(red, blue, ref _random);
        // Restricted only when asked. The unrestricted draw is the reference's
        // irandom(8) and every recorded match depends on its stream — the same rule
        // HeadlessMatch follows, for the same reason.
        var playerClass = options.AllowedClasses is { Length: > 0 } allowed
            ? BotPopulation.PickClass(ref _random, allowed)
            : BotPopulation.PickClass(ref _random);

        _nameCounter++;
        var name = options.NamePrefix + _nameCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The stagger is the roster slot the bot is about to land in, and it is
        // load-bearing twice over: it phases the objective and combat cadences so a
        // full server does not pay every bot's scan on one frame, and it is added
        // to the follower's re-plan period, where zero makes BOT_TAKEOFF_PATIENCE
        // unreachable. See docs/TRAPS-BOTS.md, "Phase 7 item 4b-ii".
        var stagger = _server.RosterCount;

        var bot = new ScriptedBot(Nav, new NavQuery(team), BotSkill.ForTier(options.Difficulty), stagger)
        {
            SpreadX = BotRoles.SpreadX(stagger),

            // ⚠️ Null rather than an empty store when the flag is off, and the
            // difference is not cosmetic: NavSearch hoists `occupancy is { Count:
            // > 0 }` once per search, so a null store costs nothing per edge,
            // where an empty one still costs the key arithmetic and an
            // interface-dispatched lookup on every edge the frontier touches.
            Occupancy = _server.Options.BotOccupancy ? Occupancy : null,

            // ⚠️ Here as well as in MapChanged, and both are needed: that hands
            // the set to the bots that SURVIVED a rotation, this to the ones
            // that join afterwards. A bot filled in mid-round without it would
            // silently route on the straight-line heuristic while its team-mates
            // used the exact one — a mixed configuration nothing would report.
            Distances = Distances,
        };

        // Null where the map has no cache, which leaves the bot fighting whatever
        // comes to it rather than refusing to exist.
        if (Nav is not null && Spawns is not null)
            bot.Objective = new BotObjective(Nav, Spawns);

        // THE WRAPPER GOES ON BEFORE AddBot, so the slot never briefly holds a
        // bare ScriptedBot that something else could latch onto, and it wraps the
        // SAME instance the lines above configured — a controller owns keys and aim
        // inside its engagement window and delegates everything else back.
        //
        // Every site that wants the macro layer asks ScriptedBot.Of, never
        // `is ScriptedBot`. See IWrapsScriptedBot for what breaks otherwise, and
        // note the Query assignment below still reaches the right object.
        //
        // ONE field, so there is nothing to rank and nothing to refuse.
        // BotBrainSpec carries an ORDERED LIST instead, and IMicroArbiter is what
        // picks between its tiers per decision.
        IInputSource source = options.Brain is { } brain ? brain.Wrap(bot) : bot;

        var slot = _server.AddBot(name, team, playerClass, source);

        // The query has to carry the class the server actually gave it, which
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

        // Prefer a bot that is already dead. A body vanishing in the middle of
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

        // `botNavRelease`'s half of the bookkeeping: a bot that is gone is no
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
    /// <b>A second pass over a finished roster, never inside the first.</b> A
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

            // Assigned rather than defaulted, because SetMap clears the
            // objective: a rotation must hand every bot the new map's answer.
            bot.Assign(slot, team, playerClass, CollectionsMarshal.AsSpan(earlier), Nav, Spawns);
        }
    }
}
