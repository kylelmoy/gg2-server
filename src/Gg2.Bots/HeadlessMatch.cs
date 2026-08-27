using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// How a <see cref="HeadlessMatch"/> is set up. A record struct, so a caller
/// varies one field of a known-good configuration with <c>with</c>.
/// </summary>
/// <remarks>
/// ⚠️ <b>Use <see cref="Default"/>, never <c>new()</c>.</b> A record struct's
/// primary-constructor defaults do not apply to the zeroing constructor, so
/// <c>new HeadlessMatchOptions()</c> is a match on a null map with no bots and
/// no time limit — every one of them silent. The same trap as
/// <see cref="BotPopulationOptions.Default"/>, <c>CharacterState.Fresh</c> and
/// <c>PlayerState.Fresh</c>: <b>in this codebase a zeroed value type is
/// routinely not a valid one.</b>
/// </remarks>
public readonly record struct HeadlessMatchOptions(
    string Map,
    int Area,
    int Bots,
    int Difficulty,
    int CapLimit,
    int TimeLimitMinutes,
    int KillLimit,
    int RespawnTimeSeconds,
    int MaxTicks,
    uint Seed,
    int? RedBots = null,
    int? BlueBots = null)
{
    /// <summary>
    /// A short match on <c>ctf_truefort</c> with twelve bots. ⚠️ The values are
    /// spelled out rather than delegating to <c>new()</c> — writing <c>new()</c>
    /// here makes the factory a no-op that reads exactly like a fix.
    /// </summary>
    public static HeadlessMatchOptions Default => new(
        Map: "ctf_truefort",
        Area: 1,
        Bots: 12,
        Difficulty: 3,
        CapLimit: 5,
        TimeLimitMinutes: 15,
        KillLimit: 30,
        RespawnTimeSeconds: 5,

        // ⚠️ A match MUST have a tick ceiling and it is not a safety net. CTF
        // overtime is unbounded — a tie with a flag off its base at 0:00 stays
        // open indefinitely — so a batch of matches with no ceiling has no
        // bound on its own running time. The rotation test in the bots lane hit
        // exactly this once bots started reaching the enemy flag.
        MaxTicks: 30 * 60 * 20,
        Seed: 1);

    /// <summary>
    /// Whether this configuration names the teams explicitly rather than
    /// filling <see cref="Bots"/> evenly.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Nullable rather than "0 means unset"</b>, because <b>0 is a value
    /// somebody wants</b> — <c>RedBots: 8, BlueBots: 0</c> is an uncontested
    /// attack, which is exactly the experiment that separates "the bots cannot
    /// return the flag" from "the bots cannot get past six defenders". An
    /// unspecified team and an empty one have to be different things, and this
    /// codebase's own rule is that a zeroed field meaning "off" is a trap.
    /// </remarks>
    public bool HasExplicitTeams => RedBots is not null || BlueBots is not null;
}

/// <summary>Why a match stopped.</summary>
public enum MatchOutcome
{
    /// <summary>Still running.</summary>
    Running,

    /// <summary>A team won, or the round was drawn — see <see cref="MatchResult.Winner"/>.</summary>
    Decided,

    /// <summary>It hit <see cref="HeadlessMatchOptions.MaxTicks"/> undecided.</summary>
    Exhausted,
}

/// <summary>What a finished match produced.</summary>
/// <remarks>
/// ⚠️ <b><see cref="Winner"/> is only meaningful when <see cref="Outcome"/> is
/// <see cref="MatchOutcome.Decided"/></b>, because <c>Team.Red</c> is 0 — a
/// defaulted result would say red won. The same reason <c>WorldState</c> keeps
/// <c>RoundDecided</c> as a separate flag from <c>Winner</c>. A draw is
/// <c>Team.Spectator</c>, which is how the wire spells it.
/// </remarks>
public readonly record struct MatchResult(
    MatchOutcome Outcome,
    Team Winner,
    int RedCaps,
    int BlueCaps,
    int Ticks,
    ulong Digest)
{
    /// <summary>
    /// ⚠️ <b><see cref="Digest"/> is what makes two results comparable at all.</b>
    /// </summary>
    /// <remarks>
    /// The scoreboard is a very coarse observable: over a few hundred ticks
    /// <em>every</em> match on a large map is
    /// <c>Exhausted, 0-0, MaxTicks</c>, whatever its seed — measured, and it is
    /// what made the first version of these tests both pass and fail for the
    /// wrong reasons. Worse, it is coarse in the direction that matters: two
    /// runs could put every character somewhere different and still tie 0-0, so
    /// a determinism check on the scoreboard alone would miss exactly the
    /// divergence it exists to catch. The digest covers where everybody is.
    /// </remarks>
    public bool SameOutcomeAs(MatchResult other) =>
        Outcome == other.Outcome
        && Winner == other.Winner
        && RedCaps == other.RedCaps
        && BlueCaps == other.BlueCaps;
}

/// <summary>
/// One match, played by bots, with no sockets, no clients and no clock —
/// <c>docs/BOTAI.md</c> item 8 and phase 8's third item.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the step function an environment wraps, not a win-rate harness.</b>
/// <c>docs/PLAN.md</c>'s phase 9 records the decision and why it reaches back
/// into this file: retrofitting a batch harness into a stepped environment runs
/// the expensive direction, which is the same asymmetry <c>docs/BOTAI.md</c>
/// item 1 warns about for <c>WorldState</c>. So the surface here is
/// <see cref="Reset"/> / <see cref="Step()"/> / <see cref="State"/> — one tick
/// at a time, inspectable between ticks — and <see cref="Run"/> is a loop over
/// it rather than the other way round.
/// </para>
/// <para>
/// ⚠️ <b>It deliberately does NOT define an observation.</b> <c>docs/BOTAI.md</c>
/// item 6 leaves the feature vector undesigned on purpose, and inventing one
/// here — before a trainer exists to want anything from it — is the overreach
/// that document warns against. What is exposed is the whole
/// <see cref="WorldState"/>; a <c>WorldView</c> over it already answers
/// nearest-K without allocating.
/// </para>
/// <para>
/// <b>The sibling of <see cref="ScenarioWorld"/>.</b> That is a world with the
/// gamemode switched off, for measuring one behaviour; this is the same
/// construction with the gamemode running. Both exist because
/// <c>MatchSimulation.Tick</c> is a pure function of state and inputs, so
/// nothing about a match needs a server.
/// </para>
/// </remarks>
public sealed class HeadlessMatch
{
    private readonly IInputSource?[] _sources = new IInputSource?[WorldState.MaxSlots];
    private readonly PlayerInput[] _inputs = new PlayerInput[WorldState.MaxSlots];
    private readonly List<PlayerClass> _earlier = [];

    private WorldState _state;
    private TickEvents _events;
    private int _slots;

    private HeadlessMatch(
        HeadlessMatchOptions options,
        LevelMap map,
        CollisionWorld collision,
        SpawnPoints spawns,
        NavGraph? nav)
    {
        Options = options;
        Map = map;
        Collision = collision;
        Spawns = spawns;
        Nav = nav;

        Reset();
    }

    public HeadlessMatchOptions Options { get; }

    public LevelMap Map { get; }

    public CollisionWorld Collision { get; }

    public SpawnPoints Spawns { get; }

    public NavGraph? Nav { get; }

    /// <summary>The world, by reference. A <c>WorldView</c> over it is free.</summary>
    public ref WorldState State => ref _state;

    /// <summary>What the last <see cref="Step()"/> reported.</summary>
    public ref TickEvents Events => ref _events;

    /// <summary>Ticks played since the last <see cref="Reset"/>.</summary>
    public int Tick { get; private set; }

    /// <summary>Where the match is, updated every <see cref="Step()"/>.</summary>
    public MatchOutcome Outcome { get; private set; }

    /// <summary>
    /// Loads a map and its nav cache and builds a match on it.
    /// </summary>
    /// <remarks>
    /// ⚠️ A null or missing <paramref name="navDirectory"/> is not an error, and
    /// means the bots fight whatever comes to them without walking anywhere —
    /// <c>navCacheLoad.gml</c>'s own contract, and the same answer
    /// <c>ScenarioWorld</c> and <c>GameServer</c> give.
    /// </remarks>
    public static HeadlessMatch Load(
        HeadlessMatchOptions options,
        string mapDirectory,
        string? navDirectory)
    {
        var png = File.ReadAllBytes(Path.Combine(mapDirectory, options.Map + ".png"));

        // ⚠️ Narrowed to the area FIRST, and everything downstream sees only
        // that. A tall map is several rounds stacked vertically and only one
        // area's entities exist — cp_dirtbowl writes six control points and
        // plays two.
        var map = LevelMap.FromPng(options.Map, png).ForArea(options.Area);

        NavGraph? nav = null;
        if (navDirectory is { Length: > 0 } && NavCache.TryLoad(navDirectory, options.Map, options.Area, out var loaded))
            nav = loaded;

        return On(options, map, nav);
    }

    /// <summary>
    /// Builds a match on a map and nav graph that are already loaded.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is what makes a batch cheap, and what makes it safe.</b> A
    /// <see cref="LevelMap"/>, its walkmask and a <see cref="NavGraph"/> are
    /// immutable after load, so every match in a batch can share one of each and
    /// skip a PNG decode apiece. <b>The <see cref="CollisionWorld"/> is built per
    /// match and never shared</b>, because <c>SetCaptureZones</c> writes to it —
    /// two matches sharing one would overwrite each other's control points,
    /// which is a wrong answer rather than a crash.
    /// </remarks>
    public static HeadlessMatch On(HeadlessMatchOptions options, LevelMap map, NavGraph? nav) =>
        new(options, map, new CollisionWorld(map), new SpawnPoints(map), nav);

    /// <summary>
    /// Puts the match back to its first tick: a fresh world, a fresh roster of
    /// bots, and the RNG re-seeded from <see cref="HeadlessMatchOptions.Seed"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Everything that rolls is re-seeded here, or a match is not
    /// reproducible.</b> Two streams matter and they are deliberately separate:
    /// <c>WorldState.Random</c> is <em>observable simulation</em> — every seeded
    /// weapon puts its draw on the wire — and each bot's own stream comes off
    /// its stagger. A bot drawing from the world's would make the stream's
    /// position depend on how many bots were thinking.
    /// </remarks>
    public void Reset()
    {
        _state = default;
        _events = default;
        Array.Clear(_sources);
        Tick = 0;
        Outcome = MatchOutcome.Running;

        // The floors are game_init.gml's own (96, 101, 103): a 0 means "no
        // limit" in WorldState and is unreachable in the reference.
        _state.CapLimit = Math.Max(1, Options.CapLimit);
        _state.TimeLimitMinutes = Math.Max(1, Options.TimeLimitMinutes);
        _state.KillLimit = Math.Max(1, Options.KillLimit);
        _state.RespawnTime = Math.Max(1, Options.RespawnTimeSeconds * GameConstants.TickRate);

        // GameServerCreate.gml:88, and its floor really is max(0, ...) — zero
        // means no spawn protection and there is nothing to escape.
        _state.TdmInvulnerabilityTicks = 0;

        // ⚠️ Off. Autobalance moves DEAD players across teams, which in a
        // measured match would silently change what was being compared — the
        // same reason ScenarioWorld disables it.
        _state.Autobalance = false;

        // ⚠️ A zeroed ClassLimits means every class is FULL. 255 is the
        // reference's default and a hand-built world has to say so.
        for (var i = 0; i < TeamBalance.ClassCount; i++)
            _state.ClassLimits[i] = 255;

        // ⚠️ GameMode first: ControlPoints.Place reads it to decide which point
        // objects the map places and in what order.
        _state.GameMode = GameModes.Of(Map);
        _state.AttackDefend = GameModes.HasSetupGate(Map);

        _state.AddPlayer(0);
        _slots = 1;

        Intel.Place(ref _state, Map);
        Generators.Place(ref _state, Map);

        ControlPoints.Place(ref _state, Map, out var zones);
        Collision.SetCaptureZones(zones);

        // After Place, because it writes to the points.
        Rounds.Start(ref _state);

        // ⚠️ Seeded from the options, not from a clock. GameServer randomises
        // here; a match must not, or nothing is reproducible.
        _state.Random = new GmlRandom(Options.Seed);

        AddBots();
    }

    /// <summary>
    /// The input source driving a slot — the same name and shape as
    /// <c>GameServer.SourceOf</c>.
    /// </summary>
    /// <remarks>
    /// A batch that evaluates one bot against another needs to reach the bots it
    /// placed: "which role did red's third bot take" is a question about the
    /// match, not about the world.
    /// </remarks>
    public IInputSource? SourceOf(int slot) =>
        slot >= 0 && slot < _sources.Length ? _sources[slot] : null;

    /// <summary>Advances one tick.</summary>
    /// <remarks>
    /// ⚠️ <b>A decided round ENDS the match here rather than rotating.</b> A
    /// server rotates to the next map and plays on; a match is one round, and
    /// the ten-second victory screen after it is presentation. Running through
    /// it would fold two rounds' results into one.
    /// </remarks>
    public void Step()
    {
        if (Outcome != MatchOutcome.Running)
            return;

        var count = Math.Min(_slots, WorldState.MaxSlots);

        for (var slot = 0; slot < count; slot++)
        {
            _inputs[slot] = _sources[slot] is { } source
                ? source.Poll(new WorldView(in _state, Collision), slot, Tick)
                : PlayerInput.None;
        }

        _events = default;
        MatchSimulation.Tick(Collision, ref _state, _inputs.AsSpan(0, count), ref _events, Spawns);

        Tick++;

        if (_state.RoundDecided)
            Outcome = MatchOutcome.Decided;
        else if (Tick >= Options.MaxTicks)
            Outcome = MatchOutcome.Exhausted;
    }

    /// <summary>Advances until the match ends, and reports what happened.</summary>
    public MatchResult Run()
    {
        while (Outcome == MatchOutcome.Running)
            Step();

        return Result;
    }

    /// <summary>The result as it stands.</summary>
    public MatchResult Result => new(
        Outcome,
        Outcome == MatchOutcome.Decided ? _state.Winner : Team.Spectator,
        _state.RedCaps,
        _state.BlueCaps,
        Tick,
        Digest);

    /// <summary>
    /// A 64-bit digest of the world: every live character's position, velocity
    /// and health, plus the scoreboard and the round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This is the observable that makes "identical results regardless of
    /// how many threads" a real claim.</b> Two runs that agreed on the
    /// scoreboard and disagreed about where every character stood would compare
    /// equal without it — and a shared <c>NavSearch</c>, the failure that clause
    /// exists to forbid, shows up first as bots taking slightly different routes
    /// and only much later, if ever, as a different score.
    /// </para>
    /// <para>
    /// Hashing doubles bit-for-bit is not a shortcut here: the whole project is
    /// bit-exact against the reference by construction, so two runs of the same
    /// seed either agree on every bit or have a bug. FNV-1a, because it needs to
    /// be stable across runs rather than cryptographic.
    /// </para>
    /// </remarks>
    public ulong Digest
    {
        get
        {
            var hash = 14695981039346656037UL;

            void Mix(ref ulong h, ulong value)
            {
                for (var i = 0; i < 8; i++)
                {
                    h ^= (byte)(value >> (i * 8));
                    h *= 1099511628211UL;
                }
            }

            Mix(ref hash, (ulong)Tick);
            Mix(ref hash, (ulong)_state.RedCaps);
            Mix(ref hash, (ulong)_state.BlueCaps);
            Mix(ref hash, (ulong)_state.RoundTimer);

            for (var slot = 0; slot < _slots; slot++)
            {
                Mix(ref hash, (ulong)(long)_state.Players[slot].Team);
                Mix(ref hash, (ulong)(long)_state.Players[slot].Class);
                Mix(ref hash, _state.IsAlive(slot) ? 1UL : 0UL);

                if (!_state.IsAlive(slot))
                    continue;

                ref readonly var character = ref _state.Characters[slot];

                Mix(ref hash, BitConverter.DoubleToUInt64Bits(character.X));
                Mix(ref hash, BitConverter.DoubleToUInt64Bits(character.Y));
                Mix(ref hash, BitConverter.DoubleToUInt64Bits(character.HSpeed));
                Mix(ref hash, BitConverter.DoubleToUInt64Bits(character.VSpeed));
                Mix(ref hash, (ulong)(long)character.Hp);
            }

            return hash;
        }
    }

    /// <summary>
    /// Fills the roster with bots, then assigns roles in a second pass.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The role pass is over a FINISHED roster and never inside the loop
    /// that assigns teams.</b> A role counts this bot's place among its team's
    /// bots that share its class period, so assigning as you go gives every bot
    /// a count that only includes the ones before it — which is a different
    /// answer, not a partial one.
    /// </remarks>
    private void AddBots()
    {
        var skill = BotSkill.ForTier(Options.Difficulty);

        // ⚠️ A stream of its own, not the world's. Which team and class a bot
        // takes must not move the weapon-seed stream — see Reset's remark.
        var picker = new GmlRandom(Options.Seed ^ 0x9E3779B9);

        // Explicit teams, or an even fill. ⚠️ The explicit form is what makes a
        // bot change measurable against another: "red runs the new one, blue
        // runs the old" is the comparison docs/BOTAI.md item 8 exists for, and
        // an evenly-filled match cannot express it.
        var reds = Options.HasExplicitTeams ? Options.RedBots ?? 0 : -1;
        var blues = Options.HasExplicitTeams ? Options.BlueBots ?? 0 : -1;

        var wanted = Options.HasExplicitTeams
            ? Math.Min(reds + blues, WorldState.MaxSlots - 1)
            : Math.Min(Options.Bots, WorldState.MaxSlots - 1);

        for (var i = 0; i < wanted; i++)
        {
            Team team;

            if (Options.HasExplicitTeams)
            {
                // Alternate while both still owe bots, so the roster order does
                // not put one whole team before the other — slot order phases
                // the objective and combat cadences, and a block of one team
                // would give that team every early phase.
                CountTeams(out var placedRed, out var placedBlue);

                team = placedRed < reds && (placedBlue >= blues || placedRed <= placedBlue)
                    ? Team.Red
                    : Team.Blue;
            }
            else
            {
                CountTeams(out var red, out var blue);
                team = BotPopulation.PickTeam(red, blue, ref picker);
            }

            var playerClass = BotPopulation.PickClass(ref picker);

            // ⚠️ The stagger is the roster slot the bot lands in, which is what
            // makes two bots roll differently and one bot roll the same way
            // every run.
            var slot = _slots;

            _state.AddPlayer(slot);
            _state.Players[slot].Team = team;
            _state.Players[slot].Class = playerClass;

            // A characterless change arms alarm[5] = 1: a spawn in this same
            // tick's alarm stage, which is what the reference does.
            _state.Players[slot].RespawnTimer = 1;

            _sources[slot] = new ScriptedBot(Nav, new NavQuery(team), skill, slot);
            _slots++;
        }

        AssignRoles();
    }

    private void AssignRoles()
    {
        for (var slot = 1; slot < _slots; slot++)
        {
            if (_sources[slot] is not ScriptedBot bot)
                continue;

            var team = _state.Players[slot].Team;
            var playerClass = _state.Players[slot].Class;

            _earlier.Clear();
            for (var other = 1; other < slot; other++)
            {
                if (_state.Players[other].Team == team)
                    _earlier.Add(_state.Players[other].Class);
            }

            bot.Role = BotRoles.Assign(playerClass, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_earlier));
            bot.SpreadX = BotRoles.SpreadX(slot);
            bot.Query = new NavQuery(team, CanDoubleJump: BotClassProfile.AirJump(playerClass));

            // ⚠️ A BotObjective holds the graph and the map's spawn points, so
            // one is built per match rather than shared: keeping one across
            // matches is keeping another world's answer.
            bot.Objective = Nav is not null ? new BotObjective(Nav, Spawns) : null;
        }
    }

    private void CountTeams(out int red, out int blue)
    {
        red = 0;
        blue = 0;

        for (var slot = 1; slot < _slots; slot++)
        {
            if (_state.Players[slot].Team == Team.Red)
                red++;
            else if (_state.Players[slot].Team == Team.Blue)
                blue++;
        }
    }
}
