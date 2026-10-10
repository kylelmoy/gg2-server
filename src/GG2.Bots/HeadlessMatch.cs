using System.Runtime.InteropServices;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

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
/// <b>It deliberately does NOT define an observation.</b> <c>docs/BOTAI.md</c>
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
public sealed class HeadlessMatch : DrivenWorld
{
    private readonly List<PlayerClass> _earlier = [];

    private readonly NavDistanceSet? _distances;

    private HeadlessMatch(
        HeadlessMatchOptions options,
        LevelMap map,
        CollisionWorld collision,
        SpawnPoints spawns,
        NavGraph? nav,
        NavDistanceSet? distances)
        : base(map, collision, nav)
    {
        Options = options;
        Spawns = spawns;

        // ⚠️ Derived here only when the caller did not, so a LONE match still
        // works — but a batch must pass one in. All matches on a map share its
        // graph, so a per-match derivation is the same table computed `count`
        // times, and at a few hundred ms against a match's own ~0.6 s it would
        // cost more than the heuristic saves. MatchRunner.Run hoists it.
        _distances = distances
            ?? (options.NavDistances && nav is not null ? NavDistanceSet.Derive(nav) : null);

        Reset();
    }

    public HeadlessMatchOptions Options { get; }



    public SpawnPoints Spawns { get; }





    /// <summary>Where the match is, updated every <see cref="Step()"/>.</summary>
    public MatchOutcome Outcome { get; private set; }

    /// <summary>
    /// Loads a map and its nav cache and builds a match on it.
    /// </summary>
    /// <remarks>
    /// A null or missing <paramref name="navDirectory"/> is not an error, and
    /// means the bots fight whatever comes to them without walking anywhere —
    /// <c>navCacheLoad.gml</c>'s own contract, and the same answer
    /// <c>ScenarioWorld</c> and <c>GameServer</c> give.
    /// </remarks>
    public static HeadlessMatch Load(
        HeadlessMatchOptions options,
        string mapDirectory,
        string? navDirectory)
    {
        // Through the SHARED cache, so a batch of matches on one map decodes it
        // once: 18.0 ms against 283.5 ms to play a 9,000-tick match — 6% of every
        // match, paid again for bytes that had not changed. Most of the 18 ms is
        // the nav graph rather than the PNG.
        //
        // Only the deeply immutable half is shared. On() builds a fresh
        // CollisionWorld and SpawnPoints per match, which is what keeps this safe:
        // CollisionWorld.SetCaptureZones writes to it, so it must never be cached.
        // ScenarioResources' own remarks carry the full argument.
        //
        // The cache is NEVER INVALIDATED, which is right for a batch runner and
        // wrong for a long-lived server — a map edited on disk mid-process is not
        // served again. Nothing in GG2.Server reaches this path; GameServer loads
        // its own. On(options, map, nav) is the uncached way in for a caller that
        // needs a re-read.
        //
        // The map is narrowed to the AREA before anything downstream sees it. A
        // tall map is several rounds stacked vertically and only one area's
        // entities exist — cp_dirtbowl writes six control points and plays two.
        // ScenarioResources.Read does that narrowing, and the area is part of its
        // key.
        var (map, nav) = ScenarioResources.Shared.Get(
            options.Map, options.Area, mapDirectory, navDirectory);

        return On(options, map, nav);
    }

    /// <summary>
    /// Builds a match on a map and nav graph that are already loaded.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes a batch cheap, and what makes it safe.</b> A
    /// <see cref="LevelMap"/>, its walkmask and a <see cref="NavGraph"/> are
    /// immutable after load, so every match in a batch can share one of each and
    /// skip a PNG decode apiece. <b>The <see cref="CollisionWorld"/> is built per
    /// match and never shared</b>, because <c>SetCaptureZones</c> writes to it —
    /// two matches sharing one would overwrite each other's control points,
    /// which is a wrong answer rather than a crash.
    /// </remarks>
    /// <param name="distances">
    /// A <see cref="NavDistanceSet"/> already derived for <paramref name="nav"/>,
    /// or null to derive one here when
    /// <see cref="HeadlessMatchOptions.NavDistances"/> asks for it. <b>It is
    /// read-only once built, so a batch shares one across every thread</b> — the
    /// same sharing rule the map and the graph follow, and for the same reason.
    /// </param>
    public static HeadlessMatch On(
        HeadlessMatchOptions options, LevelMap map, NavGraph? nav, NavDistanceSet? distances = null) =>
        new(options, map, new CollisionWorld(map), new SpawnPoints(map), nav, distances);

    /// <summary>
    /// Puts the match back to its first tick: a fresh world, a fresh roster of
    /// bots, and the RNG re-seeded from <see cref="HeadlessMatchOptions.Seed"/>.
    /// </summary>
    /// <remarks>
    /// <b>Everything that rolls is re-seeded here, or a match is not
    /// reproducible.</b> Two streams matter and they are deliberately separate:
    /// <c>WorldState.Random</c> is <em>observable simulation</em> — every seeded
    /// weapon puts its draw on the wire — and each bot's own stream comes off
    /// its stagger. A bot drawing from the world's would make the stream's
    /// position depend on how many bots were thinking.
    /// </remarks>
    public void Reset()
    {
        Clear();
        Outcome = MatchOutcome.Running;

        // The floors are game_init.gml's own (96, 101, 103): a 0 means "no
        // limit" in WorldState and is unreachable in the reference.
        State.CapLimit = Math.Max(1, Options.CapLimit);
        State.TimeLimitMinutes = Math.Max(1, Options.TimeLimitMinutes);
        State.KillLimit = Math.Max(1, Options.KillLimit);
        State.RespawnTime = Math.Max(1, Options.RespawnTimeSeconds * GameConstants.TickRate);

        // GameServerCreate.gml:88, and its floor really is max(0, ...) — zero
        // means no spawn protection and there is nothing to escape.
        State.TdmInvulnerabilityTicks = 0;

        // Off. Autobalance moves DEAD players across teams, which in a
        // measured match would silently change what was being compared — the
        // same reason ScenarioWorld disables it.
        State.Autobalance = false;

        // A zeroed ClassLimits means every class is FULL. 255 is the
        // reference's default and a hand-built world has to say so.
        for (var i = 0; i < TeamBalance.ClassCount; i++)
            State.ClassLimits[i] = 255;

        // GameMode first: ControlPoints.Place reads it to decide which point
        // objects the map places and in what order.
        State.GameMode = GameModes.Of(Map);
        State.AttackDefend = GameModes.HasSetupGate(Map);

        State.AddPlayer(AddSlot());

        Intel.Place(ref State, Map);
        Generators.Place(ref State, Map);
        MovingPlatforms.Place(ref State, Map);

        ControlPoints.Place(ref State, Map, out var zones);
        Collision.SetCaptureZones(zones);

        // After Place, because it writes to the points.
        Rounds.Start(ref State);

        // Seeded from the options, not from a clock. GameServer randomises
        // here; a match must not, or nothing is reproducible.
        State.Random = new GmlRandom(Options.Seed);

        AddBots();
    }


    /// <summary>The hybrid bot in a slot, or null when it is all-scripted.</summary>
    /// <remarks>
    /// Exposed so an evaluation can report how much of a match a controller
    /// actually drove. <b>"The controller was wired in" and "the controller
    /// drove" are different claims</b>, and only the second one is worth anything —
    /// a hybrid whose engagement gate never fired is a scripted bot with extra
    /// steps, and it would win or lose exactly like one.
    /// <para>
    /// <b>ONE accessor for every arm</b>, so a bench summing per-arm ticks into
    /// one column cannot end up with a comment explaining that exactly one of
    /// them is always zero.
    /// </para>
    /// </remarks>
    public HybridBot? HybridBotFor(int slot) => HybridBot.Of(SourceOf(slot));

    /// <summary>Advances one tick.</summary>
    /// <remarks>
    /// <b>A decided round ENDS the match here rather than rotating.</b> A
    /// server rotates to the next map and plays on; a match is one round, and
    /// the ten-second victory screen after it is presentation. Running through
    /// it would fold two rounds' results into one.
    /// </remarks>
    public void Step()
    {
        if (Outcome != MatchOutcome.Running)
            return;

        Advance(Spawns);

        if (State.RoundDecided)
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
        Outcome == MatchOutcome.Decided ? State.Winner : Team.Spectator,
        State.RedCaps,
        State.BlueCaps,
        Tick,
        Digest);

    /// <summary>
    /// A 64-bit digest of the world: every live character's position, velocity
    /// and health, plus the scoreboard and the round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the observable that makes "identical results regardless of
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
            Mix(ref hash, (ulong)State.RedCaps);
            Mix(ref hash, (ulong)State.BlueCaps);
            Mix(ref hash, (ulong)State.RoundTimer);

            for (var slot = 0; slot < SlotCount; slot++)
            {
                Mix(ref hash, (ulong)(long)State.Players[slot].Team);
                Mix(ref hash, (ulong)(long)State.Players[slot].Class);
                Mix(ref hash, State.IsAlive(slot) ? 1UL : 0UL);

                if (!State.IsAlive(slot))
                    continue;

                ref readonly var character = ref State.Characters[slot];

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
    /// <b>The role pass is over a FINISHED roster and never inside the loop
    /// that assigns teams.</b> A role counts this bot's place among its team's
    /// bots that share its class period, so assigning as you go gives every bot
    /// a count that only includes the ones before it — which is a different
    /// answer, not a partial one.
    /// </remarks>
    private void AddBots()
    {
        // ONE set for the whole match, not one per bot: it is derived from the
        // graph and every bot reads the table matching its own class arcs out of
        // it, so a per-bot derivation would be twelve times the cost and the same
        // answer. Null unless asked for, which is what keeps every recorded route
        // identical by default.
        var distances = _distances;

        // A stream of its own, not the world's. Which team and class a bot
        // takes must not move the weapon-seed stream — see Reset's remark.
        var picker = new GmlRandom(Options.Seed ^ 0x9E3779B9);

        // Explicit teams, or an even fill. The explicit form is what makes a
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

            // Restricted only when asked. The unrestricted draw is the
            // reference's and every recorded match depends on its stream.
            var playerClass = Options.AllowedClasses is { Length: > 0 } allowed
                ? BotPopulation.PickClass(ref picker, allowed)
                : BotPopulation.PickClass(ref picker);

            // The stagger is the roster slot the bot lands in, which is what
            // makes two bots roll differently and one bot roll the same way
            // every run.
            var slot = AddSlot();

            State.AddPlayer(slot);

            // Every player in a headless match IS a bot, and saying so keeps
            // EngageOpponents.HumansOnly HONEST here rather than accidentally
            // correct: a match that left this false would report "humans
            // everywhere" and engage the controller on every fight, which is the
            // default behaviour and would hide the setting being ignored.
            // Nothing in MatchSimulation reads it — see PlayerState.IsBot.
            State.Players[slot].IsBot = true;
            State.Players[slot].Team = team;
            State.Players[slot].Class = playerClass;

            // A characterless change arms alarm[5] = 1: a spawn in this same
            // tick's alarm stage, which is what the reference does.
            State.Players[slot].RespawnTimer = 1;

            var tier = Options.HandicapOnly ? Options.Difficulty : Options.DifficultyOf(team);

            Drive(slot, new ScriptedBot(Nav, new NavQuery(team), BotSkill.ForTier(tier), slot)
            {
                Distances = distances,
            });
        }

        AssignRoles();
        WrapInBrain();
        Humanize();
    }

    /// <summary>
    /// Puts <see cref="HumanizedBot"/> outermost on <see cref="HeadlessMatchOptions.HumanizeTeam"/>'s bots.
    /// </summary>
    /// <remarks>
    /// After <see cref="WrapInBrain"/>, so it filters what the whole brain
    /// decided, and seeded from the slot as the scripted layer is.
    /// </remarks>
    private void Humanize()
    {
        if (Options.HumanizeTeam is not { } team)
            return;

        for (var slot = 1; slot < SlotCount; slot++)
        {
            if (State.Players[slot].Team != team || SourceOf(slot) is not { } source || ScriptedBot.Of(source) is null)
                continue;

            Drive(slot, new HumanizedBot(source, Options.HumanizeWith ?? HumanizeOptions.Default, (uint)slot));
        }
    }

    /// <summary>
    /// Puts each bot's micro-controllers in front of its scripted layer.
    /// </summary>
    /// <remarks>
    /// <b><c>gg2-rl</c>'s phase 4 architecture.</b> <b>AFTER
    /// <see cref="AssignRoles"/>, not instead of it.</b> The roles, the spread and
    /// the objective are configured on a <c>ScriptedBot</c> by type, and the
    /// scripted layer keeps every one of those jobs — a controller only takes keys
    /// and aim, and only inside <see cref="HybridBot.EngagementRange"/>.
    /// <para>
    /// <b>Called from <see cref="AddBots"/> so it survives <see cref="Reset"/>.</b>
    /// A method the caller had to remember after every reset is a match that
    /// silently reverts to scripted halfway through an evaluation.
    /// </para>
    /// <para>
    /// <b>A bot whose class no controller will drive is left scripted</b>,
    /// which <see cref="HybridBot"/> also enforces per tick — a class can change
    /// mid-match and the wrapper cannot be rebuilt when it does.
    /// </para>
    /// <para>
    /// <b>ONE loop, whatever the arm.</b> <see cref="BotBrainSpec"/> is one field,
    /// so the arms cannot differ in which slots get wrapped, which team is chosen
    /// or which gate settings apply — the comparison exists to isolate the
    /// controller.
    /// </para>
    /// </remarks>
    private void WrapInBrain()
    {
        if (Options.Brain is not { } brain)
            return;

        for (var slot = 1; slot < SlotCount; slot++)
        {
            if (SourceOf(slot) is not ScriptedBot scripted)
                continue;

            if (Options.HybridTeam is { } team && State.Players[slot].Team != team)
                continue;

            var hybrid = (Options.SpreadTurnTeam == State.Players[slot].Team ? brain.WithSpreadTurn() : brain).Wrap(scripted);

            if (Options.HandicapOnly)
                hybrid.HandicapSkill = BotSkill.ForTier(Options.DifficultyOf(State.Players[slot].Team));

            Drive(slot, hybrid);
        }
    }

    private void AssignRoles()
    {
        for (var slot = 1; slot < SlotCount; slot++)
        {
            if (SourceOf(slot) is not ScriptedBot bot)
                continue;

            var team = State.Players[slot].Team;
            var playerClass = State.Players[slot].Class;

            _earlier.Clear();
            for (var other = 1; other < slot; other++)
            {
                if (State.Players[other].Team == team)
                    _earlier.Add(State.Players[other].Class);
            }

            bot.Assign(slot, team, playerClass, CollectionsMarshal.AsSpan(_earlier), Nav, Spawns);
        }
    }

    private void CountTeams(out int red, out int blue)
    {
        red = 0;
        blue = 0;

        for (var slot = 1; slot < SlotCount; slot++)
        {
            if (State.Players[slot].Team == Team.Red)
                red++;
            else if (State.Players[slot].Team == Team.Blue)
                blue++;
        }
    }
}
