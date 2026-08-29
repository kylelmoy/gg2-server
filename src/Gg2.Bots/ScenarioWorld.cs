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
    private PlayerCommandBuffer _commands;
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

    /// <summary>
    /// Places two opposing characters on walkable nodes drawn from this map,
    /// a chosen distance apart.
    /// </summary>
    /// <param name="minSeparation">The closest the pair may be, in pixels.</param>
    /// <param name="maxSeparation">The furthest the pair may be, in pixels.</param>
    /// <remarks>
    /// ⚠️ <b>This is what makes a batch of N worlds N SAMPLES.</b> Placing at
    /// fixed coordinates gives two deterministic bots the identical duel in every
    /// world — <c>gg2-rl</c> measured 8 worlds returning 74 decisions and 165
    /// damage eight times over, seed-invariant, and a sixteen-duel win rate
    /// computed off a sample size of one. Banded, the same matchup returns 15
    /// distinct episode lengths out of 16.
    /// <para>
    /// ⚠️ <b>And a fixed position does not merely repeat, it MISLEADS.</b> Twelve
    /// duels of <c>RolloutSearch</c> against a tier-5 <c>ScriptedBot</c> on
    /// <c>gg_debug</c> read <b>11-1-0 at x=600 and 4-2-6 at x=1500</b> — same map,
    /// same classes, same budget, budget-invariant out to 5,400 ticks. One spot
    /// in one room is a rout and another is half stalemates, a swing an order of
    /// magnitude past anything else measured about that bot. <b>A duel figure
    /// names its position or it names nothing.</b>
    /// </para>
    /// <para>
    /// ⚠️ <b>A PAIR placement, not two draws.</b> A separation is a property of
    /// the two together, so it cannot be expressed as one <see cref="Place"/>
    /// followed by another.
    /// </para>
    /// <para>
    /// The draw is red first, uniformly over the nodes red may stand on; then
    /// blue uniformly over the nodes blue may stand on whose stand position is
    /// inside the band. ⚠️ <b>Red first, and out of the world's own
    /// <see cref="WorldState.Random"/></b>: a slot index is a roster index, and a
    /// sampled placement must be a pure function of the world's seed. Seed the
    /// world <em>before</em> calling this.
    /// </para>
    /// <para>
    /// ⚠️ <b>Gate-filtered per team.</b> A node whose gate that team cannot pass
    /// is a node that team cannot legitimately stand on, and placing there is
    /// what produced <c>gg2-rl</c>'s four maps at 0 of 8 engagements: the pair
    /// started behind their own team gates and could not route to one another.
    /// <c>hasIntel</c> is false because a duel has no briefcase and
    /// <c>setupGatesClosed</c> is false because this world zeroes the setup timer.
    /// </para>
    /// <para>
    /// ⚠️ <b>The node's MIDPOINT, not a column drawn inside it.</b> A node spans
    /// several columns and drawing one would add a few pixels of variety on top
    /// of a draw that already has hundreds of nodes in it — while making every
    /// placement's own resolution back to its node a thing to check rather than a
    /// certainty (see <see cref="EdgeFlights"/>, which pays for that property).
    /// </para>
    /// <para>
    /// ⚠️ <b>Straight-line distance, deliberately not path length.</b> What "in
    /// range" means here is what a weapon and a rollout horizon reach, which is a
    /// line; a path metric would call two agents either side of a thin wall 0px
    /// apart.
    /// </para>
    /// <para>
    /// ⚠️ <b>Retries the red draw rather than widening the band.</b> A band with
    /// nothing in it for one red node usually has plenty for another, and
    /// widening silently would answer a question the caller did not ask. After
    /// <see cref="BandAttempts"/> reds it gives up loudly, naming the map and the
    /// band, because a band no pair on the map satisfies is a caller error and
    /// not a rare draw.
    /// </para>
    /// </remarks>
    public (int Red, int Blue) PlaceInBand(
        PlayerClass redClass, PlayerClass blueClass, double minSeparation, double maxSeparation)
    {
        var graph = Nav
            ?? throw new InvalidOperationException(
                $"a separation band needs a nav graph and {Map.Name} has none loaded; "
                + "load this world with a nav directory, or place at explicit coordinates");

        var red = Standable(graph, Team.Red);
        var blue = Standable(graph, Team.Blue);

        if (red.Count == 0 || blue.Count == 0)
        {
            throw new InvalidOperationException(
                $"{Map.Name} has no nodes a {(red.Count == 0 ? Team.Red : Team.Blue)} character may stand on");
        }

        var candidates = new List<int>();

        for (var attempt = 0; attempt < BandAttempts; attempt++)
        {
            var redNode = red[(int)_state.Random.Next(red.Count)];
            var redX = NodeMidpoint(graph, redNode);
            var redY = graph.StandY(redNode);

            candidates.Clear();

            foreach (var node in blue)
            {
                var d = Gml.PointDistance(redX, redY, NodeMidpoint(graph, node), graph.StandY(node));

                if (d >= minSeparation && d <= maxSeparation)
                    candidates.Add(node);
            }

            if (candidates.Count == 0)
                continue;

            var blueNode = candidates[(int)_state.Random.Next(candidates.Count)];

            return (
                Place(redClass, Team.Red, redX, redY),
                Place(blueClass, Team.Blue, NodeMidpoint(graph, blueNode), graph.StandY(blueNode)));
        }

        throw new InvalidOperationException(
            $"no pair of walkable nodes on {Map.Name} is {minSeparation}-{maxSeparation}px "
            + $"apart after {BandAttempts} draws; widen the band or pick another map");
    }

    /// <summary>How many red nodes are tried before a band is called empty.</summary>
    /// <remarks>
    /// ⚠️ It is a caller error rather than a rare draw that this catches, so it is
    /// small on purpose: a band a map really supports is satisfied by the first
    /// draw almost always, and a band it does not is not going to be satisfied by
    /// the thousandth.
    /// </remarks>
    private const int BandAttempts = 32;

    /// <summary>The nodes a character of this team may legitimately stand on.</summary>
    private static List<int> Standable(NavGraph graph, Team team)
    {
        var nodes = new List<int>(graph.NodeCount);

        for (var i = 0; i < graph.NodeCount; i++)
        {
            if (NavGraph.GatePassable(graph.Nodes[i].Gate, team, hasIntel: false, setupGatesClosed: false))
                nodes.Add(i);
        }

        return nodes;
    }

    /// <summary>Where a character standing in the middle of a node sits.</summary>
    private static double NodeMidpoint(NavGraph graph, int node) =>
        NavGraph.ColumnWorldX((graph.Nodes[node].X0 + graph.Nodes[node].X1) / 2);

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
            RunCommands();

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
    /// Runs the commands every driven slot asked for this tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Until 2026-08-29 this world DROPPED every command a source
    /// emitted</b>, because it only ever polled <see cref="IInputSource"/>.
    /// <c>GameServer</c> and <see cref="HeadlessMatch"/> both poll
    /// <see cref="ICommandSource"/>; a duel and a scenario did not, so a bot in
    /// one of them could press keys and nothing else. That is not a small gap —
    /// <b>everything a player can do that is not a key is a command</b>: the
    /// zoom, the sandvich, dropping the intel, building a sentry. A scenario is
    /// where those get measured.
    /// <para>
    /// ⚠️ <b>BEFORE the poll, not after</b>, matching <see cref="HeadlessMatch"/>
    /// and the server: a command is the reference's <c>processClientCommands</c>,
    /// which runs ahead of the tick that reads its effect. A zoom issued after
    /// the input poll would not change the aim that same tick, and the sniper's
    /// charge would start a tick late for ever.
    /// </para>
    /// <para>
    /// ⚠️ <b>Every arm dispatches to the same <see cref="PlayerCommands"/> method
    /// the server calls.</b> CLAUDE.md's rule about the three places a command
    /// lives: the body is shared or a bot can do something a client cannot.
    /// Unhandled kinds are silently skipped rather than thrown, exactly as
    /// <see cref="HeadlessMatch"/> does — a scenario places no intel and no
    /// sentry, so those are unreached rather than unsupported.
    /// </para>
    /// </remarks>
    private void RunCommands()
    {
        for (var slot = 0; slot < _slots; slot++)
        {
            if (_sources[slot] is not ICommandSource source)
                continue;

            _commands.Clear();
            source.PollCommands(new WorldView(in _state, Collision), slot, Tick, ref _commands);

            for (var i = 0; i < _commands.Count; i++)
            {
                switch (_commands[i].Kind)
                {
                    case PlayerCommandKind.ToggleZoom:
                        PlayerCommands.ToggleZoom(ref _state, slot);
                        break;

                    case PlayerCommandKind.EatSandvich:
                        PlayerCommands.EatSandvich(ref _state, slot);
                        break;

                    // ⚠️ Unreached rather than unsupported, and the distinction
                    // is HeadlessMatch's: a scenario world places no briefcase,
                    // no sentry site and no roster to change class on, so the
                    // rest cannot arise here. When one can, add it against the
                    // simulation method the server already calls — never a
                    // second copy of the body.
                    default:
                        break;
                }
            }
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
