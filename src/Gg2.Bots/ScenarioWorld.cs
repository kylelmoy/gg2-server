using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// A map, a few characters placed exactly where you asked, and no gamemode.
/// </summary>
/// <remarks>
/// docs/BOTAI.md item 11: "spawn two characters of given classes at given
/// positions on a given map, with no gamemode running, and step them". Duel
/// training and rollout search both want a <em>fight</em>, not a match, and the
/// sample budget is spent here rather than in whole rounds.
/// <para>
/// It is also the scenario harness — <c>gg2-agent/tools/botscenario.js</c>'s job
/// in this port, minus the running game. The reason that runner lives outside
/// GML is that every new script costs a full Game Maker build; here the same
/// reasoning says it should be a library rather than a test fixture, because a
/// self-play evaluator wants exactly this and must not have to reach into a test
/// assembly for it.
/// </para>
/// <para>
/// <b>What "no gamemode" means concretely.</b> The mode is
/// <see cref="GameMode.TeamDeathmatch"/> with every limit at zero, which is not
/// a fudge: docs/TRAPS-SIMULATION.md, "Team Deathmatch is what a map with no objective
/// <em>is</em>" — <c>basicRoomSetup</c>'s chain ends there — and every limit in
/// the port carries a "0 means off" escape the reference reaches by flooring at
/// one. So the round clock never starts, no limit is ever reached, nobody
/// respawns and nothing is placed to capture. The terrain and the gates are
/// real, because a route that ignores a team gate is not a route — and so are
/// the generators, which are a thing to shoot rather than a mode to play; see
/// the comment on <c>Generators.Place</c> in the constructor.
/// </para>
/// </remarks>
public sealed class ScenarioWorld
{
    private readonly IInputSource?[] _sources = new IInputSource?[WorldState.MaxSlots];
    private WorldState _state;
    private TickEvents _events;
    private int _slots;

    private ScenarioWorld(LevelMap map, CollisionWorld collision, NavGraph? nav)
    {
        Map = map;
        Collision = collision;
        Nav = nav;

        // ⚠️ Every one of these is a "0 means off" escape, and each is load
        // bearing on its own:
        //
        //   GameMode          TeamDeathmatch is the objectiveless mode —
        //                     basicRoomSetup's chain ends there, so it is what a
        //                     map with no objective IS.
        //                     ⚠️ But it is NOT what makes this world inert, and
        //                     it would be easy to believe it is: every mode's
        //                     Step reads entities this world never places, so
        //                     KingOfTheHill with no control points and CTF with
        //                     no briefcase are equally quiet. Measured, by
        //                     setting the mode to each and watching nothing
        //                     change. The work is done by the zeroed limits
        //                     below and by nothing being placed; the mode is set
        //                     because it is the honest description, not because
        //                     a test can tell.
        //   TimeLimitMinutes  0 is unreachable in the reference (floored at 1 in
        //                     game_init.gml) and means "no limit" here, so the
        //                     round never ends and never opens the gates.
        //   KillLimit         the same escape, and the one TDM would otherwise
        //                     end on.
        //   RespawnTime       0 disables autonomous respawn outright. A killed
        //                     dummy must stay dead, or a damage measurement
        //                     silently becomes a measurement of two lives.
        //   TdmInvulnerable   ⚠️ NOT zero by accident. WorldState.Spawn gives a
        //                     TDM character this many ticks of spawn protection,
        //                     so a non-zero value would make every damage figure
        //                     in every scenario wrong for the first seconds and
        //                     look like a bot that could not shoot.
        //   Autobalance       ⚠️ moves DEAD players across teams. In a scenario
        //                     that would take the dummy the bot just killed and
        //                     put it on the bot's own team, which is a silent
        //                     wrong answer rather than a failure.
        _state.GameMode = GameMode.TeamDeathmatch;
        _state.TimeLimitMinutes = 0;
        _state.CapLimit = 0;
        _state.KillLimit = 0;
        _state.RespawnTime = 0;
        _state.TdmInvulnerabilityTicks = 0;
        _state.Autobalance = false;
        _state.SetupTimer = 0;

        // ⚠️ A zeroed ClassLimits means every class is FULL — 255 is the
        // default the reference uses and a hand-built world has to say so.
        // Nothing in a scenario asks Classlimits.Pick today; it is set because
        // the day something does, the failure is a class silently swapped.
        for (var i = 0; i < TeamBalance.ClassCount; i++)
            _state.ClassLimits[i] = 255;

        // ⚠️ The one objective a scenario world DOES place, and the exception is
        // not an inconsistency. A generator is not something a bot stands on or
        // carries — it is a 2,100 hp thing that gets SHOT, so it is a combat
        // target rather than a gamemode, and `soldier-shells-generator` exists
        // precisely because it was invisible to every bot in the game and the
        // symptom was silent: the round simply never ended.
        //
        // It is inert here for the ordinary reason. Rounds.StepGenerator is
        // reached only under GameMode.Generator, and this world is
        // TeamDeathmatch — so the generators take damage, regenerate their
        // shields exactly as they would in a real match, and decide nothing.
        // A map with no generators gets none and nothing changes.
        Generators.Place(ref _state, Map);

        // Slot 0 is the server's own reserved player and always exists, which is
        // what makes CharacterState.Nobody 255 rather than 0. A scenario has no
        // server, but the invariant is the state's, not the server's.
        _state.AddPlayer(0);
        _slots = 1;
    }

    /// <summary>The map, narrowed to its area.</summary>
    public LevelMap Map { get; }

    /// <summary>The collision geometry the tick is given.</summary>
    public CollisionWorld Collision { get; }

    /// <summary>The nav graph for this map and area, if a cache was found.</summary>
    public NavGraph? Nav { get; }

    /// <summary>The world, by reference — it is a value type and copying it is a fork.</summary>
    public ref WorldState State => ref _state;

    /// <summary>What the last <see cref="Step"/> reported.</summary>
    public ref TickEvents Events => ref _events;

    /// <summary>Ticks stepped since the world was built.</summary>
    public int Tick { get; private set; }

    /// <summary>
    /// Builds a scenario world on a built-in map.
    /// </summary>
    /// <param name="mapDirectory">The reference's <c>Included Files</c>.</param>
    /// <param name="navDirectory">
    /// Where the nav caches live, or null for none. A missing cache leaves
    /// <see cref="Nav"/> null rather than failing — <c>navCacheLoad.gml</c>'s own
    /// contract, and a scenario about physics does not need a graph.
    /// </param>
    /// <remarks>
    /// ⚠️ <b><see cref="LevelMap.ForArea"/> at the load site, once.</b> A tall
    /// map is several rounds stacked vertically and only one area's entities
    /// exist; narrowing here is what makes everything downstream — the
    /// collision world included — see the right one. The nav graph is loaded for
    /// the same area for the same reason.
    /// </remarks>
    public static ScenarioWorld Load(string map, int area, string mapDirectory, string? navDirectory)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mapDirectory);

        var path = Path.Combine(mapDirectory, map + ".png");
        var level = LevelMap.FromPng(map, File.ReadAllBytes(path)).ForArea(area);

        NavGraph? nav = null;
        if (navDirectory is not null)
            NavCache.TryLoad(navDirectory, map, area, out nav);

        return new ScenarioWorld(level, new CollisionWorld(level), nav);
    }

    /// <summary>
    /// Puts a character of this class and team at this exact position, and
    /// returns its roster slot.
    /// </summary>
    /// <remarks>
    /// <b>Spawn, then teleport</b>, rather than building a character by hand.
    /// <see cref="WorldState.Spawn"/> is the one transcription of
    /// <c>Character.events/Create.xml</c> — the hp, the weapon, the facing, the
    /// six fields that name a roster slot — and duplicating it here would be a
    /// second copy that goes stale the first time that event is re-read. So a
    /// scenario character is spawned properly at a real spawn point and then
    /// moved, which is also what the reference's own runner does to a bot
    /// (<c>botscenario.js</c> places by writing <c>x</c>/<c>y</c>).
    /// <para>
    /// ⚠️ <b>The weapon is moved with it.</b> A weapon starts on its owner's
    /// unrounded coordinates and only reaches <c>round(x)</c> in End Step — so a
    /// character teleported without its weapon fires its first shot from
    /// wherever it spawned, which on a big map is across the room.
    /// <c>Character.events/End Step.xml:64-65</c>, and docs/TRAPS-SIMULATION.md's "a shot
    /// leaves from the <em>weapon</em>".
    /// </para>
    /// </remarks>
    public int Place(PlayerClass playerClass, Team team, double x, double y)
    {
        if (_slots >= WorldState.MaxSlots)
            throw new InvalidOperationException($"a scenario world holds {WorldState.MaxSlots} slots");

        var slot = _slots++;
        var spawnPoints = new SpawnPoints(Map);

        if (!_state.Spawn(spawnPoints, slot, playerClass, team, spawnpointId: 0, group: 0))
        {
            throw new InvalidOperationException(
                $"{Map.Name} has no spawn point for {team}; a scenario character cannot be created");
        }

        _state.Characters[slot].X = x;
        _state.Characters[slot].Y = y;
        _state.Weapons[slot].X = x;
        _state.Weapons[slot].Y = y;

        return slot;
    }

    /// <summary>
    /// How many spawn points this map offers a team, for a caller that wants to
    /// pick one.
    /// </summary>
    public int SpawnPointCount(Team team, int group = 0) =>
        new SpawnPoints(Map).Count(team, group);

    /// <summary>
    /// Places a character <b>at the map's own spawn point</b> rather than at
    /// coordinates the caller had to know.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is <see cref="Place"/> without the override.</b> `Place` calls
    /// <see cref="WorldState.Spawn"/>, which already puts the character on a
    /// real spawn point, and then moves it to the caller's x and y — so the only
    /// difference here is the two lines that do not run.
    /// <para>
    /// It exists because a caller that has no business knowing a map's geometry
    /// should not have to invent coordinates. <c>docs/ENV.md</c> promised
    /// "explicit positions <b>or sampled</b>" from the first draft and only the
    /// first half was built, so a duel on any map meant the trainer holding a
    /// coordinate table — a second source of truth about the maps, which is what
    /// <c>SIM_UUID</c> exists to prevent, and one that would go on working right
    /// up until a map changed.
    /// </para>
    /// <para>
    /// ⚠️ <b>The caller draws the index, not this method.</b> Whose random
    /// stream a placement comes out of is a decision with consequences — see
    /// <see cref="DuelEnv"/>, which draws from the match's own so that a world's
    /// placement is reproducible from its seed.
    /// </para>
    /// </remarks>
    public int PlaceAtSpawn(PlayerClass playerClass, Team team, int spawnpointId, int group = 0)
    {
        if (_slots >= WorldState.MaxSlots)
            throw new InvalidOperationException($"a scenario world holds {WorldState.MaxSlots} slots");

        var slot = _slots++;

        if (!_state.Spawn(new SpawnPoints(Map), slot, playerClass, team, spawnpointId, group))
        {
            throw new InvalidOperationException(
                $"{Map.Name} has no spawn point {spawnpointId} in group {group} for {team}");
        }

        return slot;
    }

    /// <summary>Gives a slot an input source. Null means it presses nothing.</summary>
    public void Drive(int slot, IInputSource? source) => _sources[slot] = source;

    /// <summary>
    /// Advances the world, polling every driven slot once per tick.
    /// </summary>
    /// <remarks>
    /// The same order the live server uses: poll first, then tick — so a source
    /// decides against the state the previous tick left, exactly as an
    /// <c>INPUTSTATE</c> that arrived before the server's own service pass does.
    /// <para>
    /// ⚠️ <b>No <see cref="SpawnPoints"/> is passed to the tick</b>, which is
    /// what stops the alarm stage respawning anybody. Between that and
    /// <see cref="WorldState.RespawnTime"/> at zero, a death in a scenario is
    /// final — which is what makes a damage figure mean one life.
    /// </para>
    /// </remarks>
    public void Step(int ticks = 1)
    {
        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];

        for (var i = 0; i < ticks; i++)
        {
            for (var slot = 0; slot < _slots; slot++)
            {
                inputs[slot] = _sources[slot] is { } source
                    ? source.Poll(new WorldView(in _state, Collision), slot, Tick)
                    : PlayerInput.None;
            }

            MatchSimulation.Tick(Collision, ref _state, inputs[.._slots], ref _events);
            Tick++;
        }
    }

    /// <summary>
    /// <c>botNodeSnap.gml</c>: the nearest node below a point, or -1.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A raw objective coordinate usually does not resolve to a node on its
    /// own.</b> Map objects are not anchored the way a character is, by
    /// different amounts on different maps — a capture zone's marker can float
    /// sixty pixels above the floor it belongs to — so a scenario's coordinates
    /// are snapped downward and the snapped position is what is used. Placing on
    /// the raw coordinate instead plans nothing and the bot never moves, which
    /// reads as a broken bot.
    /// <para>
    /// The search breaks on the <em>nearest</em> match, so widening
    /// <c>NAV_MAX_FALL</c> can only find something a shorter limit missed; it
    /// cannot change an answer this already had.
    /// </para>
    /// </remarks>
    public NavSnap Snap(double worldX, double worldY) =>
        Nav is null ? new NavSnap(-1, -1, worldX, worldY) : Nav.SnapObjective(worldX, worldY);
}
