using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

public sealed partial class GameServer
{
    /// <summary>
    /// Loads a map and rebuilds everything that depends on it: the geometry, the
    /// spawn points, the HUD placeholders, and the round.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="Start"/> and the map change, so a server on its
    /// tenth round is in the same shape as one that has just started — which is
    /// the property a long-running headless server needs and the one thing a
    /// single-shot <c>Start</c> could never be tested for.
    /// <para>
    /// The world's <em>players</em> and its random stream survive; everything
    /// else about the old map does not. <see cref="Rounds.ResetForNewMap"/> is
    /// what clears the rest, and the caller runs it before calling this.
    /// </para>
    /// </remarks>
    /// <param name="name">The map to load.</param>
    /// <param name="area">
    /// <c>global.currentMapArea</c>, 1-based. A multi-area map (Invasion) keeps
    /// its teams and advances the area instead of rotating.
    /// </param>
    private LevelMap OpenMap(string name, int area)
    {
        // ⚠️ Narrowed to the area first, and everything below sees only that.
        // A tall map can be several rounds stacked vertically and the reference
        // destroys the entities outside the one being played — see
        // LevelMap.ForArea. cp_dirtbowl is the only shipped map it affects, and
        // it is also the only attack/defend map, so the control-point work
        // depends on this.
        // ⚠️ The AREA COUNT is taken from the unnarrowed map, and that is not a
        // nicety: `basicRoomSetup.gml:7` sets global.totalMapAreas =
        // 1+instance_number(NextAreaO) BEFORE the loop at :9-25 destroys the
        // other areas' entities, so the reference's count is always the map's,
        // never the slice's. Counting the slice instead gives cp_dirtbowl
        // 2/3/2 for areas 1/2/3 where the reference says 3/3/3 — which happens
        // to decide all three of this map's advances the same way, and is
        // still the wrong operand to compare CurrentMapArea against.
        var full = LoadMap(name);
        var map = full.ForArea(area);

        _collision = new CollisionWorld(map);
        _spawnPoints = new SpawnPoints(map);
        _match = new MatchState(map, _roster, _options, GameModes.TotalAreas(full))
        {
            CurrentMapArea = (byte)area,
        };

        // Both briefcases on their bases. The map cannot live in the state, so
        // the base positions are copied in here — the same shape as seeding the
        // RNG in Start.
        Intel.Place(ref _world, map);

        // Both generators at full health, for the same reason and in the same
        // way: the map cannot live in the state, so their positions are copied
        // in here.
        Generators.Place(ref _world, map);
        MovingPlatforms.Place(ref _world, map);

        // The round's own configuration, with the ini's own clamps
        // (game_init.gml:96, 101 — both max(1, min(255, ...))). A zero means
        // "no limit" in WorldState and is unreachable in the reference, so the
        // floor is applied here rather than left to chance.
        //
        // ⚠️ GameMode first: ControlPoints.Place reads it to decide which point
        // objects the map places and in what order.
        _world.GameMode = _match.GameMode;
        _world.CapLimit = Math.Max(1, (int)_options.CapLimit);
        _world.TimeLimitMinutes = Math.Max(1, (int)_options.TimeLimitMinutes);

        // game_init.gml:103, the same max(1, ...) floor the other two limits get
        // — 0 means "no limit" in WorldState and is unreachable in the
        // reference.
        _world.KillLimit = Math.Max(1, (int)_options.KillLimit);

        // GameServerCreate.gml:88: `seconds * 30 * 0.2`, a fifth of the ticks it
        // buys, because the countdown is spent at 0.2 a tick. ⚠️ Its own floor
        // is max(0, ...) and not max(1, ...) — zero really does mean "no spawn
        // protection" here, so there is nothing to escape.
        _world.TdmInvulnerabilityTicks = (int)(DuelDefaults.TdmInvulnerabilitySeconds(_options) * 30 * 0.2);

        _world.Autobalance = _options.Autobalance && !_options.Duel; // a duel server pins humans to red; docs/DUEL.md

        // game_init.gml:105 and the [Classlimits] section, ten independent
        // values. ⚠️ A zeroed ClassLimits would mean every class is full, so
        // this is not optional even on a server that enforces nothing — see
        // WorldState.ClassLimits.
        //
        // ⚠️ EffectiveClassLimits, not ClassLimits: MatchState broadcasts the
        // same array and the two must not disagree. See the remarks there.
        var classLimits = _options.EffectiveClassLimits();

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            _world.ClassLimits[i] = classLimits[i];

        // ControlPointHUD.events/Create.xml decides which of the two
        // control-point arrangements a map is by looking for a setup gate.
        _world.AttackDefend = GameModes.HasSetupGate(map);

        // The control points, and the capture zones that name them. The zones
        // are read-only geometry and go to the CollisionWorld; the points are
        // state and go to the world.
        ControlPoints.Place(ref _world, map, out var zones);
        _collision.SetCaptureZones(zones);

        // Everything a gamemode's HUD sets in its Create event: the round clock,
        // and for KotH and DKotH the two hold timers, the pregame lock and (in
        // DKotH) each hill's starting owner. After Place, because it writes to
        // the points.
        Rounds.Start(ref _world);

        _currentMap = name;

        // ⚠️ Every bot's follower, route and blacklist are indices into the graph
        // that was loaded a moment ago, so a rotation has to hand them the new one
        // — see ScriptedBot.SetMap. The reference re-teams every bot across a map
        // change (botReteamAll) and frees the nav state on the way
        // (botNavRelease); this is both, in the one place a map is opened.
        _population?.MapChanged(map, area, _options.NavCacheDirectory);

        // ⚠️ Fires for the FIRST map too — OpenMap is how the server opens at
        // all — so a plugin's per-round state is initialised before any client
        // can connect rather than one round late. _plugins is
        // ServerPlugins.None until LoadPlugins runs, which is a no-op here.
        _plugins.MapChanged(name);

        return map;
    }

    /// <summary>
    /// Opens the ten-second countdown to a map change: the announcement, the
    /// timer, and the area moving to the one about to be served.
    /// </summary>
    /// <remarks>
    /// <b>Both doors go through this one body</b> — a round ending
    /// (<see cref="ServeRoundEnd"/>) and an operator asking
    /// (<see cref="RequestMapChange"/>) — because the third line below is easy
    /// to have in one and not the other, and the failure it causes is silent on
    /// the server and fatal on a client.
    /// <para>
    /// ⛔ <b><c>_match.CurrentMapArea</c> stays on the area being PLAYED, and the
    /// area about to be served lives in <c>_nextArea</c> until
    /// <see cref="OpenMap"/> takes it.</b> That is a deliberate departure from
    /// <c>GameServerBeginStep.gml:74-92</c>, which assigns
    /// <c>global.currentMapArea</c> (<c>+= 1</c> on an advance, <c>= 1</c> on a
    /// rotation) and only then writes the MAP_END that carries it — so upstream a
    /// joiner inside the countdown reads the NEXT area out of
    /// <c>ServerJoinUpdate.gml:6</c> and sets up the wrong stage of the map
    /// everybody else is still playing.
    /// </para>
    /// <para>
    /// The area alone cannot be corrected, because MAP_END is the only other
    /// place the wire ever states it: a joiner told the running area and never
    /// sent a MAP_END has <c>global.currentMapArea</c> frozen for as long as it
    /// stays connected, and stale at 1 across an advance it runs
    /// <c>ClientBeginStep.gml:511-544</c>'s team wipe that the server does not,
    /// reads a character for a slot it now holds as a spectator, and dies in
    /// <c>Character</c>'s Create with <c>unknown team ID: 2</c>. So the joiner is
    /// sent its own MAP_END instead — see
    /// <c>GameServer.CompleteJoin</c>, which is also where the ordering
    /// constraint on it is written down.
    /// <c>MultiAreaMapTests</c> is the gate; <c>docs/PARITY.md</c>, "Multi-area
    /// maps", and docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried into the
    /// port".
    /// </para>
    /// <para>
    /// Safe against the advance decision above re-running on it:
    /// <c>_world.MapChanging</c> is set here and gates that whole block until
    /// <see cref="Rounds.ResetForNewMap"/> clears it, by which point
    /// <see cref="OpenMap"/> has replaced <c>_match</c> outright. The decision
    /// reads <c>_match.CurrentMapArea</c>, so leaving it alone here also means
    /// it no longer depends on running exactly once.
    /// </para>
    /// </remarks>
    private void BeginMapChange(string map, int area)
    {
        _nextMap = map;
        _nextArea = area;

        Rounds.BeginMapChange(ref _world);
        _mapChangeCountdown = Rounds.MapChangeDelay;

        _broadcast.MapEnd(_nextMap, _world.Winner, (byte)_nextArea);
    }

    /// <summary>
    /// <c>GameServerBeginStep.gml:102-131</c>: the round is over, so tell
    /// everybody, then change the map ten seconds later.
    /// </summary>
    /// <remarks>
    /// <b>Run at the top of the tick, after that tick's state update has already
    /// been written and before the simulation runs.</b> That is the reference's
    /// order exactly: the check is in the server's Begin Step, below
    /// <c>serializeState</c>, and the round was decided in the <em>previous</em>
    /// tick's Step (<see cref="Rounds.Step"/>). So a win on tick N is announced
    /// at the start of N+1.
    /// </remarks>
    private void ServeRoundEnd()
    {
        if (_mapChangeCountdown > 0)
            _mapChangeCountdown--;

        if (_world.RoundDecided && !_world.MapChanging)
        {
            // `if(global.winners == TEAM_RED and global.currentMapArea <
            // global.totalMapAreas)` — red pushing through an Invasion map's
            // areas replays the same map with the next area; anything else
            // rotates and goes back to area 1.
            string nextMap;
            int nextArea;

            if (_world.Winner == Team.Red && _match.CurrentMapArea < _match.TotalMapAreas)
            {
                nextArea = _match.CurrentMapArea + 1;
                nextMap = _currentMap;
            }
            else
            {
                nextArea = 1;
                nextMap = TakeNextMapOverride() ?? NextMapInRotation();
            }

            BeginMapChange(nextMap, nextArea);
            _log.Info($"round over: {_world.Winner} wins; {_nextMap} area {_nextArea} in ten seconds");
        }

        if (_mapChangeCountdown != 0)
            return;

        // `if(impendingMapChange == 0)`: the change itself. The reset clears
        // mapchanging, the room reloads, and every player is given
        // `alarm[5] = 1` so the whole server spawns in this same tick's alarm
        // stage.
        _mapChangeCountdown = -1;

        Rounds.ResetForNewMap(ref _world, firstArea: _nextArea == 1);

        // The roster's own copy of the team, which the wire and the joining
        // path read. `with(Player) ... team = TEAM_SPECTATOR` in the reference
        // is one assignment because there is one Player; here the world and the
        // roster both hold it, so both are set.
        if (_nextArea == 1)
        {
            foreach (var entry in _roster.Entries)
                entry.Team = Team.Spectator;
        }

        var map = OpenMap(_nextMap, _nextArea);

        // ServerChangeMap.gml. The MD5 is empty for a built-in map, which is
        // every map this server serves.
        // ⚠️ The md5 the map was loaded WITH. ServerChangeMap.gml carries the
        // same pair the greeting does, so a client already connected across a
        // rotation onto a custom map learns it has to download.
        _broadcast.ChangeMap(map.Name, _currentMapMd5);
        _log.Info($"now playing {map.Name} ({_match.GameMode}), area {_nextArea}");

        // GameServerBeginStep.gml:142-143 — "message lobby to update map name".
        // The listing shows the map, so without this it advertises the previous
        // one for up to 30 seconds after a rotation.
        AnnounceToLobby();

        // ⚠️ A segment is one map, so the header's map name cannot go stale —
        // and the new segment's preamble is taken against the world OpenMap has
        // just built, before anything broadcasts on it.
        _recorder?.StartSegment(map.Name);
    }

    /// <summary>
    /// The map the server opens on: <see cref="ServerOptions.Map"/> when one was
    /// named, otherwise the shuffled rotation's first entry.
    /// </summary>
    /// <remarks>
    /// <c>GameServerCreate.gml:110-113</c> — <c>if(global.launchMap == "")
    /// serverGotoMap(nextMapInRotation()) else serverGotoMap(global.launchMap)</c>.
    /// ⚠️ <b>The rotation index stays at 0 either way</b>, so the map after the
    /// opening one is <c>rotation[1]</c>: opening on <c>rotation[0]</c> must not
    /// also consume it, or the first round would be the only one that map ever
    /// gets and a rotation of two would degenerate to a rotation of one.
    /// <para>
    /// ⚠️ <c>ctf_truefort</c> is the last resort rather than an error, because
    /// an empty <see cref="ServerOptions.Map"/> with an empty rotation is a
    /// caller that expressed no preference at all — and it is the reference's
    /// own first map.
    /// </para>
    /// </remarks>
    private string OpeningMap()
    {
        if (_options.Map.Length != 0)
            return _options.Map;

        return _rotation.Count != 0 ? _rotation[0] : ServerOptions.DefaultMap;
    }

    /// <summary>
    /// <c>nextMapInRotation.gml</c>: the next loadable entry after the current
    /// one, wrapping.
    /// </summary>
    /// <remarks>
    /// The reference calls <c>show_error</c> and exits when nothing in the
    /// rotation is loadable. A headless server stays up instead and repeats the
    /// map it is on — a server that stops serving is the worse failure, and the
    /// log says which map it picked either way.
    /// </remarks>
    private string NextMapInRotation()
    {
        var rotation = _rotation;
        if (rotation.Count == 0)
            return _currentMap;

        for (var i = 1; i <= rotation.Count; i++)
        {
            var index = (_rotationIndex + i) % rotation.Count;
            var candidate = rotation[index];

            if (!MapExists(candidate))
                continue;

            _rotationIndex = index;
            return candidate;
        }

        _log.Warn($"no loadable map in the rotation; staying on {_currentMap}");
        return _currentMap;
    }

    private bool MapExists(string name)
    {
        // An illegal name — a path separator, say — would make LoadMap throw, so
        // it counts as absent rather than reaching the wire in a MAP_END.
        if (name.Length == 0
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return File.Exists(Path.Combine(_options.MapDirectory, name + ".png"));
    }

    // --- map loading ---------------------------------------------------------

    private LevelMap LoadMap(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            // The client rejects a map name with a path separator in it outright
            // (ClientBeginStep.gml:47). Refuse to serve one for the same reason.
            throw new ArgumentException($"illegal map name '{name}'", nameof(name));
        }

        var path = Path.Combine(_options.MapDirectory, name + ".png");
        if (!File.Exists(path))
            throw new FileNotFoundException($"no map at {path}", path);

        var bytes = File.ReadAllBytes(path);

        // serverGotoMap.gml:4-12 — an internal map advertises NO md5 and cannot
        // be downloaded; anything else advertises the file's hash so a client
        // that lacks it knows to ask. InternalMaps is the same switch the client
        // uses, aliases included.
        if (InternalMaps.IsInternal(name))
        {
            _currentMapMd5 = string.Empty;
            _currentMapFile = null;
        }
        else
        {
            _currentMapMd5 = MapDownload.Md5Of(bytes);
            _currentMapFile = bytes;
        }

        return LevelMap.FromPng(name, bytes);
    }
}
