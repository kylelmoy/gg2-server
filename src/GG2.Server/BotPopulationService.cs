using System.Diagnostics;
using System.Runtime.InteropServices;
using GG2.Bots;
using GG2.Maps;
using GG2.Nav;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

/// <summary>
/// <c>botPopulationUpdate</c> and <c>botReteamAll</c>: keeps a server's bot
/// population at its target and keeps every bot's role in step with the team it
/// is on.
/// </summary>
/// <remarks>
/// The policy is <see cref="BotPopulation"/>'s and lives in <c>GG2.Bots</c>; this
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
    /// why <c>GG2.Nav</c> produces text and does not reference this assembly at
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

        // ⚠️ Zero rather than an early return, and the difference is the whole
        // feature: a suspension has to REMOVE the bots that are already here,
        // where `!options.Enabled` above deliberately leaves them alone. A player
        // who typed /bots off and watched twelve bots carry on playing has been
        // told the command worked and shown that it did not.
        var target = _server.BotsSuspended
            ? 0
            : BotPopulation.Target(_server.HumanCount, in options);
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
        // ⚠️ And Retarget on the way out of it, because everything Retarget
        // derives is derived FROM the team: MapChanged ran it one tick ago
        // against a roster ResetForNewMap had just wiped, so every bot's role was
        // counted across the two teams merged into one list. Not folded into the
        // count check below — a rotation leaves `bots == target` true, which is
        // exactly the case that returns early.
        if (Reteam(slots))
            Retarget();

        // Above the count check for the reason Reteam is: a server at its target
        // returns early, and that is every tick of an ordinary match.
        Reclass(slots, in options);

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
    /// <returns>
    /// Whether any bot actually landed on a team, which is what the caller
    /// re-derives the roles from. A refused request reports false and is retried
    /// next tick, so nothing is re-derived from a placement that did not happen.
    /// </returns>
    private bool Reteam(IReadOnlyList<int> slots)
    {
        var placed = false;

        foreach (var slot in slots)
        {
            if (_server.TeamOf(slot) is Team.Red or Team.Blue)
                continue;

            _server.CountTeams(out var red, out var blue);
            _server.RequestBotTeam(slot, BotPopulation.PickTeam(red, blue, ref _random));

            // Read back rather than assumed: RequestBotTeam runs the reference's
            // balance guard and refuses a request for the bigger team in silence.
            placed |= _server.TeamOf(slot) is Team.Red or Team.Blue;
        }

        return placed;
    }

    /// <summary>
    /// Gives every bot that died since the last call a random class for its next
    /// life, within both sets of class limits, and names it for that class.
    /// </summary>
    /// <remarks>
    /// <b>Only on the tick a death is first seen, and only with the respawn
    /// counting down.</b> <c>PlayerCommands.ChangeClass</c> on a characterless
    /// player with no respawn pending spawns it this tick, so a dead bot outside
    /// that window — waiting out a map change, say — is left alone.
    /// <para>
    /// ⚠️ <b>Both limits, counted differently.</b> <c>[BotClasslimits]</c> counts
    /// the team's bots; <c>[Classlimits]</c> counts everybody on it, and the class
    /// change itself does not check it — neither does the reference's, the stock
    /// client's menu does — so it is asked here, as the menu would. The bot does
    /// not count against its own class, so a bot within its limit may keep it.
    /// </para>
    /// <para>
    /// A bot over its limit — a rotation or the autobalance put it there — is
    /// re-classed on the same terms at the first tick it is dead, so the limit is
    /// broken for at most one life.
    /// </para>
    /// <para>
    /// The draw may land on the class the bot already plays; then nothing is sent.
    /// Its own stream, never <c>WorldState.Random</c> — see the class remarks.
    /// </para>
    /// </remarks>
    private void Reclass(IReadOnlyList<int> slots, in BotPopulationOptions options)
    {
        var changed = false;
        Span<int> botsOfClass = stackalloc int[TeamBalance.ClassCount];
        Span<byte> limits = stackalloc byte[TeamBalance.ClassCount];

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var entry = _server.EntryOf(slot);
            var alive = _server.IsAlive(slot);
            var died = entry.BotWasAlive && !alive;

            entry.BotWasAlive = alive;

            if (entry.Team is not (Team.Red or Team.Blue) || !_server.AwaitingRespawn(slot))
                continue;

            botsOfClass.Clear();
            options.EffectiveClassLimits.CopyTo(limits);

            for (var j = 0; j < slots.Count; j++)
            {
                if (slots[j] != slot && _server.TeamOf(slots[j]) == entry.Team)
                    botsOfClass[(int)_server.ClassOf(slots[j])]++;
            }

            for (var c = 0; c <= (int)PlayerClass.Pyro; c++)
            {
                if (!_server.ClassHasRoom(slot, entry.Team, (PlayerClass)c))
                    limits[c] = 0;
            }

            // ⚠️ A bot OVER its limit is re-classed too, dead or newly dead. A team
            // goes over without anybody dying: a rotation makes every bot a
            // spectator and Reteam deals them back out by team size, not class,
            // and the autobalance moves one mid-life. Measured: without this, three
            // scouts on a team allowed two within 5,200 ticks of gg_debug.
            var own = (int)entry.Class;
            var over = own >= TeamBalance.ClassCount || botsOfClass[own] >= limits[own];

            if (!(died || over))
                continue;

            if (BotPopulation.PickClass(ref _random, limits, botsOfClass) is not { } playerClass
                || playerClass == entry.Class)
            {
                continue;
            }

            _server.RequestBotClass(slot, playerClass);
            _server.RenameBot(slot, options.NamePrefix + playerClass.ToString());
            changed = true;
        }

        // Roles only: a role counts a bot's place among team-mates of its class
        // group, so one bot's change can move the roles of the bots after it.
        // Retarget would also hand every bot a fresh objective, throwing away live
        // objective state on every death.
        if (changed)
            Rerole();
    }

    private bool TryAdd(in BotPopulationOptions options)
    {
        if (_server.RosterCount >= WorldState.MaxSlots)
            return false;

        _server.CountTeams(out var red, out var blue);

        var team = BotPopulation.PickTeam(red, blue, ref _random);

        // [BotClasslimits] counts the BOTS already on the team this one joins.
        // Indexed loop, not foreach: BotSlots is an interface and this runs for
        // every bot added.
        Span<int> botsOfClass = stackalloc int[TeamBalance.ClassCount];
        var slots = _server.BotSlots;

        for (var i = 0; i < slots.Count; i++)
        {
            if (_server.TeamOf(slots[i]) == team)
                botsOfClass[(int)_server.ClassOf(slots[i])]++;
        }

        // ⚠️ Null means every class is at its limit on that team, and the bot is
        // not added. The fill then stops short of its target, which is what the
        // operator configured — ConfigWarnings says so at startup when the limits
        // cannot reach the fill at all.
        if (BotPopulation.PickClass(ref _random, options.EffectiveClassLimits, botsOfClass) is not { } playerClass)
            return false;

        // Named for the class, not numbered: a counter that only ever grows reads
        // as "[BOT] 147" an hour in. Reclass renames a bot whenever it changes
        // class after this; a team change under a binding [Classlimits]
        // (ApplyChangeTeam's reclass) is the one path that still leaves the name
        // stale. Two bots of one class share a name, which the game allows.
        var name = options.NamePrefix + playerClass.ToString();

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

        // And the name follows it: the join broadcast already went out under the
        // class asked for, before [Classlimits] answered.
        if (_server.ClassOf(slot) != playerClass)
            _server.RenameBot(slot, options.NamePrefix + _server.ClassOf(slot).ToString());

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
    /// <summary>
    /// <see cref="Retarget"/>'s role pass alone, leaving every objective in place.
    /// </summary>
    private void Rerole()
    {
        var earlier = new List<PlayerClass>();
        var slots = _server.BotSlots;

        for (var i = 0; i < slots.Count; i++)
        {
            if (ScriptedBot.Of(_server.SourceOf(slots[i])) is not { } bot)
                continue;

            var team = _server.TeamOf(slots[i]);
            earlier.Clear();

            for (var j = 0; j < i; j++)
            {
                if (_server.TeamOf(slots[j]) == team)
                    earlier.Add(_server.ClassOf(slots[j]));
            }

            bot.Role = BotRoles.Assign(_server.ClassOf(slots[i]), CollectionsMarshal.AsSpan(earlier));
        }
    }

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
