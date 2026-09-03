using System.Runtime.InteropServices;
using Gg2.Bots;
using Gg2.Host;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Maps;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="EngageOpponents"/>: hand the wheel to a micro-controller for a
/// human, and stay scripted for another bot.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>What this buys is COST, not skill.</b> The expensive controllers exist
/// to be good against people; a 4,800 µs search decision spent on two bots
/// shooting at each other in an empty corner is most of what a populated server
/// pays for. ⛔ A controller measurement taken under <c>HumansOnly</c> with no
/// humans present is measuring the scripted layer, whatever the mode said.
/// </para>
/// <para>
/// ⛔ <b>Which is why the library default is <see cref="EngageOpponents.Anyone"/>
/// and only the live server defaults to <c>HumansOnly</c></b> — a headless
/// bot-vs-bot match has no humans in it, so the other way round would have made
/// every recorded arm measurement silently measure the all-scripted control.
/// <see cref="TheLibraryDefaultIsAnyoneSoHeadlessMeasurementsStillEngage"/> is
/// the gate on that, and it is the most important test in this file.
/// </para>
/// </remarks>
public sealed class HumanOnlyHandoverTests
{
    // ------------------------------------------------------------------
    // The two defaults, which deliberately disagree
    // ------------------------------------------------------------------

    /// <summary>
    /// ⛔ The library default is <see cref="EngageOpponents.Anyone"/>.
    /// </summary>
    /// <remarks>
    /// If this ever flips, every headless arm comparison starts measuring the
    /// scripted layer against itself and reporting it as a controller result.
    /// The failure is invisible in the numbers — both arms would simply converge
    /// — so this assertion is the only thing standing between that and a
    /// published finding.
    /// </remarks>
    [Fact]
    public void TheLibraryDefaultIsAnyoneSoHeadlessMeasurementsStillEngage()
    {
        // A real tier: HybridBot refuses an empty list, which is itself the
        // right behaviour — a hybrid with no controller is a scripted bot
        // wearing a costume.
        Assert.Equal(
            EngageOpponents.Anyone,
            new HybridBot([new SearchController(new RolloutOptions(Threads: 1))], Scripted(), null).Engage);

        Assert.Equal(
            EngageOpponents.Anyone,
            BotBrainSpec.Of(new SearchMicroSpec(new RolloutOptions(Threads: 1))).Engage);
    }

    /// <summary>The live server defaults to <c>HumansOnly</c>, and can be told not to.</summary>
    [Fact]
    public void TheServerDefaultsToHumansOnlyAndTheFlagRestoresTheOldBehaviour()
    {
        Assert.False(new ServerOptions().BotEngagesBotsToo);

        var humansOnly = Program.Parse(["--bot-mode", "search"]).Bots.Brain;
        Assert.Equal(EngageOpponents.HumansOnly, Assert.IsType<BotBrainSpec>(humansOnly).Engage);

        var anyone = Program.Parse(["--bot-mode", "search", "--bot-engages-bots"]).Bots.Brain;
        Assert.Equal(EngageOpponents.Anyone, Assert.IsType<BotBrainSpec>(anyone).Engage);
    }

    // ------------------------------------------------------------------
    // The scan itself
    // ------------------------------------------------------------------

    /// <summary>
    /// ⛔ A nearer bot does not hide a human standing behind it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The bug this rules out is the obvious implementation.</b> Asking
    /// <c>NearestEnemies</c> for one result and then rejecting it if it is a bot
    /// reports "no human in range" whenever any bot happens to be closer — which,
    /// on a server filled to twelve with two people on it, is most of the time.
    /// The handover would then almost never fire and the mode would look broken
    /// for a reason nothing logged.
    /// </remarks>
    [Fact]
    public void ANearerBotDoesNotHideAHumanBehindIt()
    {
        var state = TwoEnemies(nearIsBot: true, nearX: 100, farX: 140);
        var world = View(ref state);

        // Anyone: the bot at 100 is 20px away and engages.
        Assert.Equal(20, Handover.DistanceToNearestEnemy(in world, 0, EngageOpponents.Anyone), 6);

        // HumansOnly: it must skip past the bot and find the human at 140.
        Assert.Equal(60, Handover.DistanceToNearestEnemy(in world, 0, EngageOpponents.HumansOnly), 6);
    }

    /// <summary>With only bots in range, <c>HumansOnly</c> finds nothing.</summary>
    [Fact]
    public void AllBotsMeansNoEngagementUnderHumansOnly()
    {
        var state = TwoEnemies(nearIsBot: true, nearX: 100, farX: 140);
        state.Players[2].IsBot = true;

        var world = View(ref state);

        Assert.Equal(20, Handover.DistanceToNearestEnemy(in world, 0, EngageOpponents.Anyone), 6);

        Assert.Equal(
            double.PositiveInfinity,
            Handover.DistanceToNearestEnemy(in world, 0, EngageOpponents.HumansOnly));
    }

    /// <summary>
    /// The whole point, at the <c>ShouldEngage</c> level: a bot-only fight stays
    /// scripted and a human in the same place does not.
    /// </summary>
    [Fact]
    public void AHumanEngagesTheControllerAndABotDoesNot()
    {
        var state = OneEnemy(isBot: true, x: 100);
        var world = View(ref state);

        Assert.False(Handover.ShouldEngage(
            in world, 0, HybridBot.EngagementRange, HandoverRule.Distance, true,
            EngageOpponents.HumansOnly));

        // The control: the SAME geometry with a person in it does engage, so the
        // false above is about who the enemy is and not about where they stand.
        var human = OneEnemy(isBot: false, x: 100);
        var humanWorld = View(ref human);

        Assert.True(Handover.ShouldEngage(
            in humanWorld, 0, HybridBot.EngagementRange, HandoverRule.Distance, true,
            EngageOpponents.HumansOnly));
    }

    // ------------------------------------------------------------------
    // The rule that outranks the feature
    // ------------------------------------------------------------------

    /// <summary>
    /// ⛔ <b>The simulation cannot see the flag.</b>
    /// </summary>
    /// <remarks>
    /// "The simulation never special-cases a bot" is one of the three rules that
    /// outrank everything in the bot layer. <see cref="PlayerState.IsBot"/> lives
    /// in <see cref="WorldState"/> so that bot code can read it from a snapshot,
    /// which puts it one careless <c>if</c> away from letting a bot do something
    /// a client cannot. This runs identical scripted inputs with the flag set
    /// every way and requires the resulting worlds to be indistinguishable.
    /// <para>
    /// ⚠️ It compares the full <see cref="WorldState"/>, not a summary: a
    /// difference in one character's sub-pixel position is exactly the kind of
    /// divergence that would otherwise surface as unexplained client
    /// rubberbanding months later.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSimulationCannotSeeTheBotFlag()
    {
        var asHumans = RunScriptedMatch(markBots: false);
        var asBots = RunScriptedMatch(markBots: true);

        // The flag itself differs, so the states are not trivially equal — clear
        // it before comparing, or this test proves nothing about the physics.
        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            asHumans.Players[i].IsBot = false;
            asBots.Players[i].IsBot = false;
        }

        // ⚠️ Compared as BYTES, not with Assert.Equal(WorldState, WorldState).
        // xunit's structural comparer recurses through the InlineArray slot
        // buffers and overflows the stack — measured, it aborts the whole test
        // host. The byte compare is also the stronger assertion, and it is only
        // available because WorldState holds no reference to anything
        // (docs/BOTAI.md item 1); BotAiConstraintsTests relies on the same
        // property.
        Assert.True(
            Bytes(in asHumans).SequenceEqual(Bytes(in asBots)),
            "the simulation produced a different world when the players were marked as bots");
    }

    /// <summary>
    /// ⚠️ A departure renumbers the flag with everything else that names a player.
    /// </summary>
    /// <remarks>
    /// <c>RemoveSlot</c> compacts the roster, and <c>CLAUDE.md</c> is explicit
    /// that anything naming a player has to be renumbered there too. This one is
    /// carried by the wholesale <c>Players[i] = Players[i + 1]</c> shift rather
    /// than by a line of its own — which is why it is worth a test: the next
    /// person to add a per-slot flag may not put it somewhere that already moves,
    /// and the symptom is a bot being treated as a human some seconds after an
    /// unrelated disconnect.
    /// </remarks>
    [Fact]
    public void ADepartureRenumbersTheBotFlag()
    {
        var state = default(WorldState);

        for (var slot = 0; slot < 4; slot++)
        {
            state.AddPlayer(slot);
            state.Players[slot].IsBot = slot is 2 or 3;
        }

        // Slot 1 leaves: 2 and 3 shift down to 1 and 2, and their flags with them.
        state.RemoveSlot(1);

        Assert.False(state.Players[0].IsBot);
        Assert.True(state.Players[1].IsBot);
        Assert.True(state.Players[2].IsBot);
        Assert.False(state.Players[3].IsBot);
    }

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private static ReadOnlySpan<byte> Bytes(in WorldState state) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in state));

    private static ScriptedBot Scripted() =>
        new(null, new NavQuery(Team.Red), BotSkill.ForTier(5));

    /// <summary>
    /// ⚠️ A real <see cref="CollisionWorld"/>: <see cref="WorldView"/> refuses a
    /// null one, and the handover's own scan never touches it — the geometry
    /// below is placed in open air so nothing here depends on the map.
    /// </summary>
    private static readonly CollisionWorld Collision =
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    private static WorldView View(ref WorldState state) => new(in state, Collision);

    /// <summary>Slot 0 red at x=80; one blue enemy at <paramref name="x"/>.</summary>
    private static WorldState OneEnemy(bool isBot, double x)
    {
        var state = default(WorldState);

        Place(ref state, 0, Team.Red, 80, isBot: true);
        Place(ref state, 1, Team.Blue, x, isBot);

        return state;
    }

    /// <summary>Slot 0 red at x=80; two blue enemies, the nearer one at <c>nearX</c>.</summary>
    private static WorldState TwoEnemies(bool nearIsBot, double nearX, double farX)
    {
        var state = default(WorldState);

        Place(ref state, 0, Team.Red, 80, isBot: true);
        Place(ref state, 1, Team.Blue, nearX, nearIsBot);
        Place(ref state, 2, Team.Blue, farX, isBot: false);

        return state;
    }

    private static void Place(ref WorldState state, int slot, Team team, double x, bool isBot)
    {
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].IsBot = isBot;

        state.Characters[slot] = CharacterState.Fresh(PlayerClass.Scout, team);
        state.Characters[slot].X = x;
        state.Characters[slot].Y = 200;
        state.Alive[slot] = true;
    }

    /// <summary>
    /// A short match driven by fixed inputs — no bots, so nothing in it could
    /// legitimately read the flag.
    /// </summary>
    private static WorldState RunScriptedMatch(bool markBots)
    {
        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));
        var world = new CollisionWorld(map);
        var state = default(WorldState);

        state.Random = new GmlRandom(12345);

        for (var slot = 0; slot < 4; slot++)
        {
            var team = slot % 2 == 0 ? Team.Red : Team.Blue;

            state.AddPlayer(slot);
            state.Players[slot].Team = team;
            state.Players[slot].Class = PlayerClass.Soldier;
            state.Players[slot].IsBot = markBots;

            // ⛔ LIVE characters, and this is the whole difference between a gate
            // and a decoration. The first version of this test added players and
            // never spawned them, so both runs simulated an empty world and the
            // comparison was two default states — it passed against a deliberate
            // `if (IsBot) x += 0.0001` planted in MatchSimulation. Measured, not
            // reasoned: the mutant is how the hole was found.
            state.Characters[slot] = CharacterState.Fresh(PlayerClass.Soldier, team);
            state.Characters[slot].X = 2560 + (slot * 24);
            state.Characters[slot].Y = 708;
            state.Weapons[slot] = WeaponState.Spawn(
                PlayerClass.Soldier, state.Characters[slot].X, state.Characters[slot].Y);
            state.Alive[slot] = true;
        }

        var events = default(TickEvents);
        Span<PlayerInput> inputs = stackalloc PlayerInput[4];

        for (var tick = 0; tick < 200; tick++)
        {
            for (var slot = 0; slot < 4; slot++)
            {
                // A fixed, slot-dependent input pattern: enough movement and
                // firing that the two runs would diverge on any real difference.
                inputs[slot] = new PlayerInput
                {
                    Keys = (tick + slot) % 3 == 0 ? PlayerKeys.Right : PlayerKeys.Left,
                    NetAimDirection = (ushort)((tick * 7 + slot * 31) % 360),
                    AimDistance = 40,
                };
            }

            MatchSimulation.Tick(world, ref state, inputs, ref events);
        }

        return state;
    }
}
