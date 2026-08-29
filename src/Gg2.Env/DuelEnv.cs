using System.Runtime.InteropServices;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Env;

/// <summary>How an episode ended, or that it has not.</summary>
public enum Termination : byte
{
    Running = 0,

    /// <summary>Somebody died. In a duel that is the episode.</summary>
    Death = 1,

    /// <summary>The tick cap was reached with both still alive.</summary>
    TickCap = 2,
}

/// <summary>
/// What one world hands back for one decision step.
/// </summary>
/// <remarks>
/// ⚠️ <b>Components, not a scalar</b> — <c>docs/ENV.md</c>'s third scope call.
/// The trainer folds these into a reward, so iterating on the reward function
/// never needs a C# rebuild, and a run's reward definition stays visible in the
/// trainer's own config rather than being baked into a binary somebody has to
/// go and read.
/// </remarks>
public readonly record struct StepResult
{
    /// <summary>Hit points this agent dealt over the decision's ticks.</summary>
    public double DamageDealt { get; init; }

    /// <summary>Hit points this agent lost over the decision's ticks.</summary>
    public double DamageTaken { get; init; }

    /// <summary>Deaths this agent caused. 0 or 1 in a duel.</summary>
    public byte Kills { get; init; }

    /// <summary>Deaths this agent suffered.</summary>
    public byte Deaths { get; init; }

    public Termination Termination { get; init; }

    /// <summary>
    /// The action this agent actually took.
    /// </summary>
    /// <remarks>
    /// For a <see cref="EnvDriver.Client"/> side this is exactly what was passed
    /// in, echoed so a trainer logging transitions does not have to correlate its
    /// own send buffer with the reply.
    /// <para>
    /// ⚠️ <b>For a bot side it is the nearest action to what the bot did, and the
    /// aim head is the one that can be lossy.</b> The four discrete heads are
    /// recovered exactly from the key mask — they are the same bits — but the aim
    /// is recovered as the bin nearest the turn the bot actually made, and
    /// <c>ScriptedBot</c> aims straight at its target with no turn-rate cap at
    /// all. When it snaps further than <c>ActionDecoder.MaxTurnDegreesPerDecision</c>
    /// the bin saturates, so replaying that action would turn less than the bot
    /// did.
    /// </para>
    /// <para>
    /// ⚠️ <b>This matters for imitation and not for logging.</b> Cloning a
    /// scripted bot from these actions learns a throttled version of it; using
    /// them to record what an opponent did is exact enough. <b>Measured</b>, and
    /// the number is in <c>docs/ENV.md</c> — do not assume it is rare.
    /// <see cref="ActionExact"/> says so per step rather than leaving it to be
    /// inferred.
    /// </para>
    /// </remarks>
    public EnvAction Action { get; init; }

    /// <summary>
    /// Whether <see cref="Action"/> reproduces what this agent did.
    /// </summary>
    /// <remarks>
    /// Always true for a client side and for any bot whose turn fitted inside the
    /// cap. False means the aim head saturated — the discrete heads are still
    /// exact. ⚠️ <b>A flag rather than a footnote</b>, because the alternative is
    /// a trainer discovering the lossiness as an unexplained gap between its
    /// cloned policy and the bot it cloned.
    /// </remarks>
    public bool ActionExact { get; init; }

    /// <summary>
    /// The winning candidate's score, for a <see cref="EnvDriver.Rollout"/> slot.
    /// <b>NaN</b> for every other driver.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>ZERO LABELS A BLIND DECISION, and that is what this field is for.</b>
    /// <c>RolloutSearch</c>'s own remark has always said that when nothing can
    /// score inside the horizon every candidate scores exactly 0, the tie goes to
    /// the first, and the first is <c>[0,0,0,0,0,0]</c> — "turn hard left, do
    /// nothing". <c>gg2-rl</c> measured what that does to a training corpus:
    /// <b>35.2% of 1,960 collected rows are that exact action</b>, and the share
    /// tracks refire time against the 20-tick horizon — scout (refire 8) 15.3%,
    /// heavy (12) 24.2%, soldier (30) 39.5%, <b>sniper (40) 46.2%</b>.
    /// <para>
    /// ⚠️ <b>Every one of those rows is VALID</b> — correct action, correctly
    /// recorded, 99.8% exact — so nothing about the dataset looks wrong. Cloning
    /// it teaches a policy to turn hard left and freeze whenever it is unsure, and
    /// it would have surfaced as an inexplicable leftward bias <em>after</em> a
    /// training run. A consumer cannot filter it by value: a genuinely chosen
    /// "hold still" is a sensible action for a sniper at range and is
    /// indistinguishable from the fallback.
    /// </para>
    /// <para>
    /// ⚠️ <b>NaN, not 0, when there is no search behind the slot</b> — the two
    /// mean opposite things here, and 0 is the meaningful value. The same
    /// reasoning as <see cref="DuelSpec.Sampled"/>: NaN is safe as a sentinel
    /// precisely because no real score can collide with it.
    /// </para>
    /// </remarks>
    public double BestScore { get; init; }

    public bool Done => Termination != Termination.Running;
}

/// <summary>
/// Who supplies one agent's actions.
/// </summary>
/// <remarks>
/// ⚠️ <b>Requested by <c>gg2-rl</c> 2026-08-28, and it blocks three of its
/// phases.</b> Until this existed <c>MakeBatch</c> could only drive both slots
/// from the client, so a duel between two things that both live in this repo —
/// <c>RolloutSearch</c> against <c>ScriptedBot</c> — could not be asked for over
/// the socket at all. Phase 1's exit criterion is 500 duels per matchup, phase 2
/// collects against fixed opponents, and phase 3's opponent pool is "past
/// checkpoints, the scripted bot, and a noise bot".
/// <para>
/// ⚠️ <b>A past checkpoint is <see cref="Client"/>.</b> The trainer already has
/// the weights and can drive that slot itself; what it cannot do from outside is
/// run the bots, which need the <c>WorldView</c> and the nav graph. So this enum
/// is deliberately short and is not a plug-in seam.
/// </para>
/// <para>
/// ⚠️ <b>The bot is polled AS IT IS, not re-encoded.</b> Each bot drives its slot
/// through its own <c>IInputSource.Poll</c>, on its own decision cadence, so the
/// opponent really is that bot rather than an imitation of it squeezed through
/// the action space. What comes back in <see cref="StepResult.Action"/> is the
/// nearest action to what it did — exact for everything except the aim head on a
/// bot that turns faster than the cap. See <see cref="StepResult.Action"/>.
/// </para>
/// </remarks>
public enum EnvDriver : byte
{
    /// <summary>The action arrives over the socket. The default.</summary>
    Client = 0,

    /// <summary>
    /// Presses nothing and never turns — the comparison floor.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not dead weight and not a placeholder.</b> A win rate against an
    /// idle opponent is what says whether a policy has learned to aim and fire at
    /// all, and it is the only opponent whose behaviour is certain. The same
    /// argument <c>RolloutOpponent.Idle</c> is kept for.
    /// </remarks>
    Idle = 1,

    /// <summary>
    /// <c>ScriptedBot</c> at a skill tier.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Needs a nav graph.</b> Without one its follower is null and it
    /// fights from wherever it spawned, which is a different and much weaker
    /// opponent — so a spec asking for it without a nav directory is refused
    /// rather than quietly downgraded.
    /// </remarks>
    Scripted = 2,

    /// <summary>
    /// <c>RolloutSearch</c>, the forward-search bot.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It costs about 2.94 ms a decision</b> (Release, one searcher on
    /// <c>gg_debug</c>) against a scripted bot's microseconds, so a batch driven
    /// by it is roughly three orders of magnitude slower per world. That is the
    /// point of it — it is an evaluation baseline, not a data-collection
    /// opponent — but it is worth knowing before making a batch of 64.
    /// </remarks>
    Rollout = 3,
}

/// <summary>How a duel is set up.</summary>
public readonly record struct DuelSpec
{
    public required string Map { get; init; }

    public required PlayerClass RedClass { get; init; }

    public required PlayerClass BlueClass { get; init; }

    public required double RedX { get; init; }

    public required double RedY { get; init; }

    public required double BlueX { get; init; }

    public required double BlueY { get; init; }

    /// <summary>
    /// Decision steps before the episode is cut off.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Counted in DECISIONS, not ticks.</b> A cap in ticks would silently
    /// change length when the action repeat changed, and the trainer's episode
    /// budget is in decisions.
    /// </remarks>
    public int DecisionCap { get; init; }

    /// <summary>Ticks each action is held for. 1 means every tick is a decision.</summary>
    public int ActionRepeat { get; init; }

    /// <summary>Who drives red. <see cref="EnvDriver.Client"/> by default.</summary>
    public EnvDriver RedDriver { get; init; }

    /// <summary>Who drives blue. <see cref="EnvDriver.Client"/> by default.</summary>
    public EnvDriver BlueDriver { get; init; }

    /// <summary>
    /// The skill tier for a <see cref="EnvDriver.Scripted"/> side, 0-5.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Zero is a real tier, not "unset".</b> <c>BotSkill.ForTier(0)</c> is
    /// the most handicapped bot rather than the default one, so a caller that
    /// leaves this alone gets the weakest scripted opponent — which is the safe
    /// direction to be wrong in, and is stated because the opposite reading costs
    /// an experiment.
    /// </remarks>
    public int RedTier { get; init; }

    /// <summary>The skill tier for a <see cref="EnvDriver.Scripted"/> blue side.</summary>
    public int BlueTier { get; init; }

    /// <summary>
    /// Which opponent model a <see cref="EnvDriver.Rollout"/> red side searches
    /// against. <see cref="RolloutOpponent.HoldLastInput"/> by default.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The number this exists for is a difference, not a level.</b>
    /// <c>gg2-rl</c>'s phase 1 wants the search baseline reported under
    /// <see cref="RolloutOpponent.HoldLastInput"/> <em>and</em> under
    /// <see cref="RolloutOpponent.Idle"/>: if the win rate barely moves between
    /// them the search is not really using its opponent model and the horizon or
    /// the scoring is the binding constraint; if it moves a lot, that is the
    /// argument for paying for self-play rollouts. It is wanted before a policy
    /// is distilled from the search, because the policy inherits whatever the
    /// search assumed.
    /// <para>
    /// ⚠️ <b>A separate field rather than an overload of <see cref="RedTier"/>.</b>
    /// The two are never both meaningful — a side is Scripted or it is Rollout —
    /// so sharing one integer is tempting. It is refused because both are small
    /// non-negative values with no sentinel between them, so a marshalling slip
    /// would silently ask for tier 1 instead of HoldLastInput and the experiment
    /// would report a matchup that never happened. <see cref="Sampled"/> is
    /// overloaded onto a coordinate precisely because NaN <em>has</em> no legal
    /// collision; this has one.
    /// </para>
    /// </remarks>
    public RolloutOpponent RedRolloutOpponent { get; init; }

    /// <summary>The opponent model a <see cref="EnvDriver.Rollout"/> blue side searches against.</summary>
    public RolloutOpponent BlueRolloutOpponent { get; init; }

    /// <summary>
    /// The closest two sampled placements may be, in pixels. 0 for no floor.
    /// </summary>
    /// <remarks>See <see cref="MaxSeparation"/>, which is the switch.</remarks>
    public double MinSeparation { get; init; }

    /// <summary>
    /// The furthest two sampled placements may be, in pixels — and the switch
    /// that moves sampling off the spawn points and onto the nav graph.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Zero means off, and off means spawn points.</b> Sampling from spawn
    /// points is what a real respawn does and is the right default; it is also
    /// unusable for a duel, and that was measured rather than argued.
    /// <c>gg2-rl</c>, <c>RolloutSearch</c> against a stationary <c>Idle</c>, 90
    /// decisions:
    /// <list type="bullet">
    /// <item><c>gg_debug</c>, explicit spawns: 4 distinct actions, 16-0-0.</item>
    /// <item><c>arena_lumberyard</c> and <c>ctf_2dfort</c>, sampled: <b>2</b>
    /// distinct actions, 0 damage, tick cap.</item>
    /// </list>
    /// No candidate can produce damage inside a 20-tick horizon at that range,
    /// so every candidate scores zero, the tie goes to the first, and the bot
    /// stands still. <c>RolloutSearch</c> has no objective layer and no follower
    /// — nothing in it walks toward anybody — so it is not a search bug, it is
    /// out of range.
    /// <para>
    /// And the other half: <c>gg_debug</c> has ONE spawn point per team, so
    /// sampling there draws the same duel every time. Sixteen duels reported
    /// 100.0% [70.7%, 100.0%] p=3.1e-05 off a sample size of one — they genuinely
    /// happened, and they were one duel counted sixteen times. **A map that gives
    /// engagement gave no variety and a map that gave variety gave no
    /// engagement**, so no map satisfied phase 1's exit criterion.
    /// </para>
    /// <para>
    /// ⚠️ <b>With it set, placements are drawn from the NAV GRAPH's walkable
    /// nodes rather than from spawn points</b>, filtered per team by
    /// <see cref="NavGraph.GatePassable"/> and paired inside the band. Gate
    /// filtering is not decoration: <c>koth_harvest</c>, <c>ctf_2dfort</c>,
    /// <c>koth_valley</c> and <c>dkoth_atalia</c> engaged 0 of 8 sampled duels
    /// because the pair started behind their own team gates and could not route
    /// to each other. A node the team may not pass is a node they may not be
    /// placed on.
    /// </para>
    /// <para>
    /// ⚠️ <b>It needs a nav graph and is refused without one</b>, the same way
    /// <see cref="EnvDriver.Scripted"/> is, and for the same reason: quietly
    /// falling back to spawn points would answer the request with the exact
    /// behaviour it was asked to replace.
    /// </para>
    /// </remarks>
    public double MaxSeparation { get; init; }

    /// <summary>Whether placements are drawn from the nav graph inside a band.</summary>
    public bool SeparationBanded => MaxSeparation > 0;

    /// <summary>Whether either side is driven from inside this process.</summary>
    public bool HasBotDriver => RedDriver != EnvDriver.Client || BlueDriver != EnvDriver.Client;

    /// <summary>
    /// Put this in a coordinate to mean <b>"pick a spawn point off the map"</b>
    /// instead of naming one.
    /// </summary>
    /// <remarks>
    /// <c>docs/ENV.md</c>'s <c>MakeBatch</c> row has said "explicit positions or
    /// sampled" since the first draft and only the first half existed until
    /// 2026-08-28. Without the second, a duel on any map required the caller to
    /// know that map's coordinates — a second source of truth about the maps,
    /// which is the thing <c>SIM_UUID</c> exists to prevent, and one that keeps
    /// working right up until a map changes.
    /// <para>
    /// ⚠️ <b>A sentinel rather than a new field, deliberately.</b> It rides in
    /// the four doubles <c>MakeBatch</c> already carries, so the frame layout is
    /// unchanged and <see cref="EnvProtocol.Version"/> does <b>not</b> move — a
    /// client built against v1 goes on working, and one that wants sampling
    /// sends a NaN. NaN is the honest choice for it because no map coordinate
    /// can ever be one: there is no legal value it collides with.
    /// </para>
    /// <para>
    /// ⚠️ <b>And a NaN that is not understood must not flow onward</b>, which is
    /// the standing risk with this sentinel — an unnoticed NaN coordinate would
    /// place a character at NaN and every comparison about it would read false.
    /// <see cref="Validate"/> is what stops that: half a sampled pair is
    /// refused, loudly, rather than half-applied.
    /// </para>
    /// </remarks>
    public static double Sampled => double.NaN;

    /// <summary>Whether a coordinate is the <see cref="Sampled"/> sentinel.</summary>
    public static bool IsSampled(double coordinate) => double.IsNaN(coordinate);

    /// <summary>Whether red's position is to be drawn from the map.</summary>
    public bool RedIsSampled => IsSampled(RedX) && IsSampled(RedY);

    /// <summary>Whether blue's position is to be drawn from the map.</summary>
    public bool BlueIsSampled => IsSampled(BlueX) && IsSampled(BlueY);

    /// <summary>
    /// Refuses a spec whose coordinates are half sampled.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The whole reason a NaN sentinel is safe.</b> One NaN of a pair is
    /// meaningless — a caller either knows where it wants a character or does
    /// not — and it is the shape a marshalling bug produces. Refusing it here
    /// means the sentinel can never reach a placement half-understood, which is
    /// the failure mode that would otherwise put a character at NaN and make
    /// every comparison about it quietly false.
    /// </remarks>
    public void Validate()
    {
        if (IsSampled(RedX) != IsSampled(RedY))
            throw new ArgumentException("red's x and y must both be sampled or both be explicit", nameof(RedX));

        if (IsSampled(BlueX) != IsSampled(BlueY))
            throw new ArgumentException("blue's x and y must both be sampled or both be explicit", nameof(BlueX));

        // ⚠️ Refused rather than clamped. A tier outside the table is a trainer
        // bug, and silently giving it tier 5 would make an experiment report a
        // matchup that never happened.
        if (RedDriver == EnvDriver.Scripted && (uint)RedTier > 5)
            throw new ArgumentOutOfRangeException(nameof(RedTier), RedTier, "a skill tier is 0 to 5");

        if (BlueDriver == EnvDriver.Scripted && (uint)BlueTier > 5)
            throw new ArgumentOutOfRangeException(nameof(BlueTier), BlueTier, "a skill tier is 0 to 5");

        if (!Enum.IsDefined(RedDriver))
            throw new ArgumentOutOfRangeException(nameof(RedDriver), RedDriver, "no such driver");

        if (!Enum.IsDefined(BlueDriver))
            throw new ArgumentOutOfRangeException(nameof(BlueDriver), BlueDriver, "no such driver");

        if (!Enum.IsDefined(RedRolloutOpponent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(RedRolloutOpponent), RedRolloutOpponent, "no such rollout opponent");
        }

        if (!Enum.IsDefined(BlueRolloutOpponent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlueRolloutOpponent), BlueRolloutOpponent, "no such rollout opponent");
        }

        if (MinSeparation < 0 || MaxSeparation < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinSeparation), MinSeparation, "a separation band is not negative");
        }

        if (SeparationBanded && MinSeparation > MaxSeparation)
        {
            throw new ArgumentException(
                $"the separation band is empty: min {MinSeparation} is past max {MaxSeparation}",
                nameof(MinSeparation));
        }

        // ⚠️ Refused rather than ignored, and the argument is the half-NaN one
        // above. A band between one sampled placement and one the caller named is
        // half-specified — there is nothing to draw for the named side — and
        // ignoring it silently would give back exactly the spawn-point sampling
        // the band exists to replace, which is the failure the band was asked for.
        if (SeparationBanded && !(RedIsSampled && BlueIsSampled))
        {
            throw new ArgumentException(
                "a separation band needs BOTH placements sampled; it has nothing to draw for an explicit one",
                nameof(MaxSeparation));
        }

        if (!SeparationBanded && MinSeparation > 0)
        {
            throw new ArgumentException(
                "a minimum separation without a maximum does not switch banded sampling on; set MaxSeparation",
                nameof(MinSeparation));
        }
    }
}

/// <summary>
/// One duel: reset it, step it with actions, and read back reward components.
/// </summary>
/// <remarks>
/// <b><c>docs/ENV.md</c> step 2</b>, wrapping <see cref="ScenarioWorld"/> —
/// which is <c>docs/BOTAI.md</c> item 11, a world with no gamemode in it. That
/// matters more than it looks: a duel must not have a round clock, a capture, an
/// autobalance or a respawn wave happening underneath it, because every one of
/// those would move the reward without the agent doing anything.
/// <para>
/// ⚠️ <b>A death is final here, and that is inherited rather than arranged.</b>
/// <see cref="ScenarioWorld.Step"/> passes no <c>SpawnPoints</c> to the tick, so
/// the alarm stage cannot respawn anybody. That is what makes a damage figure
/// mean one life.
/// </para>
/// <para>
/// ⚠️ <b>Not thread-safe, and deliberately not.</b> <see cref="EnvBatch"/> gets
/// its parallelism by owning one <see cref="DuelEnv"/> per world, each with its
/// own <see cref="CollisionWorld"/> and RNG — the same shape
/// <c>MatchRunner</c> uses. Sharing one env across threads would be a race on
/// <see cref="WorldState"/>.
/// </para>
/// </remarks>
public sealed class DuelEnv
{
    private readonly string _mapDirectory;
    private readonly string? _navDirectory;

    private ScenarioWorld? _world;
    private DuelSpec _spec;
    private int _decisions;
    private Termination _termination;

    // The bot behind a non-client slot, or null. ⚠️ Rebuilt on every Reset
    // rather than reused: ScriptedBot carries a route, a target and four clocks
    // that belong to one life, and a bot that started an episode mid-plan would
    // make the episode depend on the last one.
    private IInputSource? _redBot;
    private IInputSource? _blueBot;

    public DuelEnv(string mapDirectory, string? navDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(mapDirectory);

        _mapDirectory = mapDirectory;
        _navDirectory = navDirectory;
    }

    /// <summary>The decoder, whose aim state belongs to this episode.</summary>
    public ActionDecoder Decoder { get; } = new();

    /// <summary>The red agent's roster slot.</summary>
    public int RedSlot { get; private set; } = -1;

    /// <summary>The blue agent's roster slot.</summary>
    public int BlueSlot { get; private set; } = -1;

    /// <summary>Decisions taken since the last reset.</summary>
    public int Decisions => _decisions;

    /// <summary>Ticks simulated since the last reset.</summary>
    public int Tick => _world?.Tick ?? 0;

    public Termination Termination => _termination;

    /// <summary>
    /// The bot behind a slot, or null when the client drives it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>For inspection, not for wiring.</b> A gate needs to be able to ask
    /// whether a scripted opponent really was re-pointed at the agent, and that
    /// is bot-internal state nothing on the wire carries — the same argument
    /// <c>IScenarioSubject.HasTarget</c> exists for. Reaching in here to change a
    /// bot mid-episode would make the episode depend on something outside the
    /// spec and the seed.
    /// </remarks>
    public IInputSource? BotFor(int slot) =>
        slot == RedSlot ? _redBot : slot == BlueSlot ? _blueBot : null;

    /// <summary>The live world, for an observation writer to read.</summary>
    public ScenarioWorld World =>
        _world ?? throw new InvalidOperationException("Reset has not been called");

    /// <summary>
    /// Starts a fresh episode.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Red is placed before blue, always.</b> A slot index is a roster
    /// index everywhere in this project, and the observation layout names the
    /// agent by slot — so a reset that placed them in a different order on a
    /// different run would hand the policy a mirrored world without saying so.
    /// </remarks>
    public void Reset(DuelSpec spec, uint seed)
    {
        if (spec.ActionRepeat < 1)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.ActionRepeat, "action repeat must be at least 1");

        if (spec.DecisionCap < 1)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.DecisionCap, "decision cap must be at least 1");

        _spec = spec;
        _decisions = 0;
        _termination = Termination.Running;

        spec.Validate();

        var world = ScenarioWorld.Load(spec.Map, area: 1, _mapDirectory, _navDirectory);

        // ⚠️ The match's own stream, seeded per world. Every seeded weapon draws
        // from it and puts the draw on the wire, so two worlds sharing a seed
        // would fire identical spreads — which is exactly what a batch must not
        // do. EnvBatch derives each world's seed from the base and the index, so
        // batch size cannot move any world's stream.
        //
        // ⚠️ `uint`, matching GmlRandom, and not widened here: GML's generator
        // has a 32-bit seed and pretending to accept 64 would throw away half
        // of whatever a trainer sent without saying so.
        //
        // ⚠️ SEEDED BEFORE PLACEMENT, and that is load-bearing now that a
        // placement can be sampled: a sampled spawn draws from this stream, so
        // seeding afterwards would make the draw come out of whatever the
        // previous episode left and the world would not be reproducible from its
        // seed. It moved above the two Place calls on 2026-08-28; the explicit
        // path is unaffected, because `ScenarioWorld.Place` names its spawn
        // point rather than drawing one.
        world.State.Random = new GmlRandom(seed);

        if (spec.SeparationBanded)
        {
            // ⚠️ ScenarioWorld's, not this class's. A banded pair is map and nav
            // knowledge, and it belongs beside PlaceAtSpawn for the same reason
            // that one does: a caller with no business knowing a map's geometry
            // should not have to invent it. It also has to be reachable from the
            // bot layer's own tests, which must not depend on this assembly —
            // see RolloutSearchTests, whose duel figure is banded because a
            // fixed-position one says almost nothing about the bot.
            (RedSlot, BlueSlot) = world.PlaceInBand(
                spec.RedClass, spec.BlueClass, spec.MinSeparation, spec.MaxSeparation);
        }
        else
        {
            RedSlot = Place(world, spec.RedClass, Team.Red, spec.RedX, spec.RedY);
            BlueSlot = Place(world, spec.BlueClass, Team.Blue, spec.BlueX, spec.BlueY);
        }

        // ⚠️ Seeded from THIS world's seed, so a scripted opponent's own stream
        // is a pure function of (base seed, world, episode) exactly as the
        // match's is. Without it every world in a batch would share the bot's
        // stream and the batch-size independence gate would fail — or worse,
        // pass, because a stagger of 0 is deterministic and the dependence would
        // only appear once two worlds took different numbers of decisions.
        _redBot = MakeBot(
            spec.RedDriver, spec.RedTier, spec.RedRolloutOpponent, Team.Red, spec.RedClass, world, seed);
        _blueBot = MakeBot(
            spec.BlueDriver, spec.BlueTier, spec.BlueRolloutOpponent, Team.Blue, spec.BlueClass, world,
            seed ^ 0x9E3779B9u);

        // Both agents start facing one another, which is the only neutral
        // choice: facing is observable (it moves the two-pixel box inflation
        // splash is measured against) so "whatever the spawn left" would be a
        // hidden asymmetry.
        //
        // ⚠️ Read off the PLACED positions, not off the spec. A sampled spec has
        // no coordinates in it, and taking the facing from `spec.RedX <=
        // spec.BlueX` would compare two NaNs — which is false either way, so
        // both agents would face 180 and one of them would start with its back
        // turned.
        var redX = world.State.Characters[RedSlot].X;
        var blueX = world.State.Characters[BlueSlot].X;

        Decoder.SetAim(RedSlot, redX <= blueX ? 0 : 180);
        Decoder.SetAim(BlueSlot, blueX < redX ? 0 : 180);

        // ⚠️ And written onto the CHARACTERS, not only into the decoder. Until
        // 2026-08-28 this facing existed in the decoder alone, so:
        //
        //   - the first observation of every episode reported the SPAWN's facing
        //     (due east) rather than the one this method says it arranges, and
        //     aim sin/cos is in the layout;
        //   - a client-driven slot hid it, because Decode writes an aim every
        //     tick and the character caught up on tick one;
        //   - a bot-driven slot did not, because a bot supplies its own aim — an
        //     idle opponent came back having apparently made a 180° turn it never
        //     asked for.
        //
        // Facing is observable (it is what the two-pixel box inflation splash is
        // measured against), so "whatever the spawn left" is exactly the hidden
        // asymmetry the paragraph above refuses.
        WriteAim(world, RedSlot, Decoder.Aim(RedSlot));
        WriteAim(world, BlueSlot, Decoder.Aim(BlueSlot));

        _world = world;
    }

    /// <summary>
    /// The bot behind one side, or null when the client drives it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="EnvDriver.Scripted"/> is refused without a nav graph
    /// rather than downgraded.</b> <c>ScriptedBot</c> takes a null graph happily
    /// and then has no follower at all — it fights from where it spawned and
    /// never walks. That is a different opponent, and an experiment reporting
    /// "against the scripted bot" while playing a stationary one is the kind of
    /// wrong this project has already paid for twice.
    /// </remarks>
    private IInputSource? MakeBot(
        EnvDriver driver, int tier, RolloutOpponent opponent, Team team, PlayerClass playerClass,
        ScenarioWorld world, uint seed)
    {
        switch (driver)
        {
            case EnvDriver.Client:
                return null;

            case EnvDriver.Idle:
                return IdleInput.Instance;

            case EnvDriver.Rollout:
                // ⚠️ Only the opponent model is exposed, not the whole
                // RolloutOptions. The horizon and the aim ladder are the
                // search's own tuning and moving them from the wire would make
                // "the search baseline" a different thing per batch; the
                // opponent model is the one knob phase 1 has to report a
                // difference across. See DuelSpec.RedRolloutOpponent.
                return new RolloutSearch(new RolloutOptions(Opponent: opponent));

            case EnvDriver.Scripted:
                if (world.Nav is null)
                {
                    throw new InvalidOperationException(
                        $"a Scripted driver needs a nav graph and {world.Map.Name} has none loaded; "
                        + "give the env a nav directory, or ask for Idle or Rollout");
                }

                // ⚠️ The query's class arcs are rebuilt from live state by
                // ScriptedBot itself every tick, so what is passed here is only
                // the team — see its own remark. Passing CanDoubleJump here would
                // be overwritten on the first tick and read as though it were not.
                return new ScriptedBot(world.Nav, new NavQuery(team), BotSkill.ForTier(tier))
                {
                    Random = new GmlRandom(seed),

                    // ⚠️ The REAL objective layer, and a duel is a deathmatch.
                    // `ScenarioWorld` places no intel, generator or control point,
                    // so `BotObjective.Decide` falls to its deathmatch branch and
                    // hunts the other agent — which is the same code path
                    // tdm_mantic uses, rather than a duel-shaped special case.
                    //
                    // ⚠️ This replaced a `GoalLocked` chase written here first,
                    // and the objective layer is better at it in three separate
                    // ways: it stands at the class's own band instead of walking
                    // onto the target (the chase parked a soldier at 59px against
                    // a band of 110 and it never fired again), it picks a real
                    // walkable node through the spot search, and its re-issue is
                    // throttled on the RESOLVED node by BOT_GOAL_RETARGET_DIST
                    // rather than by a distance test invented here.
                    Objective = new BotObjective(world.Nav, new SpawnPoints(world.Map)),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(driver), driver, "no such driver");
        }
    }

    /// <summary>
    /// One side's placement: the coordinates the caller named, or a spawn point
    /// drawn from the map.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The draw is <c>state.Random.Next(count)</c>, which is exactly what
    /// <c>Timers.PlayerSpawn</c> does for a real respawn</b> — same stream, same
    /// call, so a sampled duel starts where an ordinary spawn would have put
    /// somebody and nothing about the distribution is this project's invention.
    /// </remarks>
    private static int Place(ScenarioWorld world, PlayerClass playerClass, Team team, double x, double y)
    {
        if (!DuelSpec.IsSampled(x))
            return world.Place(playerClass, team, x, y);

        var available = world.SpawnPointCount(team);

        if (available == 0)
            throw new InvalidOperationException(
                $"{world.Map.Name} has no {team} spawn point to sample");

        return world.PlaceAtSpawn(playerClass, team, (int)world.State.Random.Next(available));
    }

    /// <summary>
    /// Takes one decision for each agent and advances the world by the action
    /// repeat.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reward components are ACCUMULATED over the repeat's ticks</b>,
    /// not read from the last one. <see cref="TickEvents"/> is cleared every
    /// tick, so reading it after the loop would report the final tick's damage
    /// and silently drop the rest — which at an action repeat of 4 loses three
    /// quarters of the signal, and loses it in a way that still trains.
    /// </remarks>
    public (StepResult Red, StepResult Blue) Step(EnvAction red, EnvAction blue)
    {
        var world = World;

        if (_termination != Termination.Running)
            throw new InvalidOperationException("the episode is over; call Reset");

        double redDealt = 0, redTaken = 0, blueDealt = 0, blueTaken = 0;
        byte redKills = 0, redDeaths = 0, blueKills = 0, blueDeaths = 0;

        // ⚠️ The aim a bot side started this decision at, kept so the action it
        // took can be recovered. A bot does not go through this decoder, so the
        // only way to say what it did in the action space is to measure the turn
        // it made — see StepResult.Action.
        var redAimBefore = Decoder.Aim(RedSlot);
        var blueAimBefore = Decoder.Aim(BlueSlot);

        // What the bots actually pressed on the FIRST tick of the decision, which
        // is the tick the jump head is defined on. ⚠️ Reading the last tick's mask
        // instead would report jump 0 for every jump, because the bit is an edge.
        var redBotInput = PlayerInput.None;
        var blueBotInput = PlayerInput.None;

        for (var tick = 0; tick < _spec.ActionRepeat; tick++)
        {
            var first = tick == 0;

            world.State.Characters[RedSlot].KeyState = default;

            // ⚠️ A bot slot is driven by the bot ITSELF, on its own cadence, and
            // not by re-encoding its choice through this decoder. The opponent is
            // then really that bot rather than one throttled to the action space
            // — which matters most for ScriptedBot, whose aim has no turn-rate cap
            // and would be a materially weaker shot if it were forced through one.
            if (_redBot is null)
            {
                // Decoded fresh every tick — the jump edge and the absolute mask
                // are both properties of doing it here rather than once per
                // decision.
                world.Drive(RedSlot, new FixedInput(Decoder.Decode(RedSlot, red, first), red));
            }
            else
            {
                world.Drive(RedSlot, _redBot);
            }

            if (_blueBot is null)
                world.Drive(BlueSlot, new FixedInput(Decoder.Decode(BlueSlot, blue, first), blue));
            else
                world.Drive(BlueSlot, _blueBot);

            world.Step();

            if (first)
            {
                redBotInput = InputOf(world, RedSlot);
                blueBotInput = InputOf(world, BlueSlot);
            }

            redDealt += world.Events.DamageDealt[RedSlot];
            redTaken += world.Events.DamageTaken[RedSlot];
            blueDealt += world.Events.DamageDealt[BlueSlot];
            blueTaken += world.Events.DamageTaken[BlueSlot];

            if (world.Events.Died[RedSlot])
            {
                redDeaths = 1;
                blueKills = 1;
            }

            if (world.Events.Died[BlueSlot])
            {
                blueDeaths = 1;
                redKills = 1;
            }

            if (redDeaths != 0 || blueDeaths != 0)
            {
                _termination = Termination.Death;
                break;
            }
        }

        _decisions++;

        if (_termination == Termination.Running && _decisions >= _spec.DecisionCap)
            _termination = Termination.TickCap;

        // ⚠️ The decoder FOLLOWS a bot slot rather than driving it, and that is
        // what keeps Snapshot's aim meaningful and lets the next decision's turn
        // be measured against where the bot really is now.
        var (redAction, redExact) = ActionTaken(
            _redBot, red, redBotInput, RedSlot, redAimBefore, world.State.Characters[RedSlot].Zoomed);
        var (blueAction, blueExact) = ActionTaken(
            _blueBot, blue, blueBotInput, BlueSlot, blueAimBefore, world.State.Characters[BlueSlot].Zoomed);

        return (
            new StepResult
            {
                DamageDealt = redDealt,
                DamageTaken = redTaken,
                Kills = redKills,
                Deaths = redDeaths,
                Termination = _termination,
                Action = redAction,
                ActionExact = redExact,
                BestScore = ScoreOf(_redBot),
            },
            new StepResult
            {
                DamageDealt = blueDealt,
                DamageTaken = blueTaken,
                Kills = blueKills,
                Deaths = blueDeaths,
                Termination = _termination,
                Action = blueAction,
                ActionExact = blueExact,
                BestScore = ScoreOf(_blueBot),
            });
    }

    /// <summary>
    /// The search's winning score behind a slot, or NaN when nothing searched.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only <c>RolloutSearch</c> has one. A scripted bot does not score
    /// candidates at all and a client slot is not a search, so reporting 0 for
    /// them would collide with the one value that carries meaning — see
    /// <see cref="StepResult.BestScore"/>.
    /// </remarks>
    private static double ScoreOf(IInputSource? bot) =>
        bot is RolloutSearch search ? search.LastBestScore : double.NaN;

    /// <summary>Points a character where the decoder says it is looking.</summary>
    /// <remarks>
    /// ⚠️ Through <see cref="PlayerInput.FromAim"/> so the angle lands on the same
    /// 65,536 steps a client's would — an agent whose starting bearing was on a
    /// finer grid than the wire carries is aiming somewhere no human can, which is
    /// <c>docs/BOTAI.md</c> item 4.
    /// </remarks>
    private static void WriteAim(ScenarioWorld world, int slot, double degrees)
    {
        var quantised = PlayerInput.FromAim(PlayerKeys.None, degrees, ActionDecoder.AimDistancePixels);

        world.State.Characters[slot].NetAimDirection = quantised.NetAimDirection;
        world.State.Characters[slot].AimDistance = quantised.AimDistance;
    }

    /// <summary>What a slot's input source put on the character this tick.</summary>
    /// <remarks>
    /// ⚠️ Read off <c>CharacterState.KeyState</c> and <c>NetAimDirection</c>
    /// rather than by polling the bot a second time. Polling twice would advance
    /// a stateful bot — <c>RolloutSearch</c> counts down a decision and
    /// <c>ScriptedBot</c> runs four clocks — so the second answer would be to a
    /// question the simulation never asked.
    /// </remarks>
    private static PlayerInput InputOf(ScenarioWorld world, int slot)
    {
        ref readonly var character = ref world.State.Characters[slot];
        return new PlayerInput(character.KeyState, character.NetAimDirection, 0);
    }

    /// <summary>
    /// The action an agent took, and whether it is reproducible.
    /// </summary>
    /// <remarks>
    /// A client side echoes what it sent. A bot side is measured: the four
    /// discrete heads come straight off the key mask, which is exact because they
    /// are the same bits, and the aim head is the bin nearest the turn the bot
    /// made over this decision.
    /// <para>
    /// ⚠️ <b>The aim is the one that can saturate.</b> <c>ScriptedBot</c> points
    /// straight at its target with no turn-rate cap, so a target that moves
    /// quickly — or a fresh acquisition — snaps further in one decision than
    /// <c>ActionDecoder.MaxTurnDegreesPerDecision</c> allows. The bin then clamps
    /// and <c>exact</c> is false, which is the whole reason that flag is reported
    /// per step rather than assumed away.
    /// </para>
    /// <para>
    /// ⚠️ <b>The decoder is then set to where the bot actually is.</b> Leaving it
    /// where the clamp landed would make every following decision's measurement
    /// drift, and would make a Snapshot restore the wrong bearing.
    /// </para>
    /// </remarks>
    private (EnvAction Action, bool Exact) ActionTaken(
        IInputSource? bot, EnvAction sent, PlayerInput taken, int slot, double aimBefore, bool zoomed)
    {
        if (bot is null)
            return (sent, true);

        var keys = taken.Keys;

        byte horizontal = (keys & PlayerKeys.Left) != 0 ? (byte)1
            : (keys & PlayerKeys.Right) != 0 ? (byte)2
            : (byte)0;

        byte fire = (keys & PlayerKeys.Attack) != 0 ? (byte)1
            : (keys & PlayerKeys.Special) != 0 ? (byte)2
            : (byte)0;

        var aimAfter = taken.AimDirectionDegrees;

        // Signed shortest turn into (-180, 180].
        var turn = ((aimAfter - aimBefore + 540.0) % 360.0) - 180.0;

        var step = ActionDecoder.MaxTurnDegreesPerDecision / ActionDecoder.AimCentreBin;
        var wanted = turn / step;
        var bin = (int)Math.Round(wanted, MidpointRounding.AwayFromZero) + ActionDecoder.AimCentreBin;
        var clamped = Math.Clamp(bin, 0, ActionDecoder.AimBins - 1);

        Decoder.SetAim(slot, aimAfter);

        var action = new EnvAction
        {
            Horizontal = horizontal,
            Jump = (keys & PlayerKeys.Jump) != 0 ? (byte)1 : (byte)0,
            Down = (keys & PlayerKeys.Down) != 0 ? (byte)1 : (byte)0,
            Fire = fire,
            AimDelta = (byte)clamped,

            // ⚠️ Read off the CHARACTER, not off whatever was asked for, and it is
            // exact by construction because the head is a LEVEL: whatever a bot
            // did with its scope, "is it scoped now" is the action that reproduces
            // it. This is the one head that cannot saturate the way the aim can.
            Zoom = zoomed ? (byte)1 : (byte)0,
        };

        // ⚠️ Exact means the BIN reproduces the turn, not that the angle is
        // identical: a turn always lands between two bins. Half a bin is the most
        // a representable turn can be out by, so that is the test.
        var exact = bin == clamped && Math.Abs(turn - (ActionDecoder.TurnFor((byte)clamped))) <= (step / 2) + 1e-9;

        return (action, exact);
    }

    /// <summary>
    /// The episode's state as raw bytes.
    /// </summary>
    /// <remarks>
    /// <c>docs/BOTAI.md</c> item 2: <b>the bytes ARE the state</b>, because
    /// <see cref="WorldState"/> holds no reference to anything. Nothing is
    /// serialised field by field, so nothing can be forgotten when a field is
    /// added — which is the failure mode a hand-written serialiser has.
    /// <para>
    /// ⚠️ <b>The decoder's aim goes with it.</b> Restoring the world alone would
    /// resume the episode aiming wherever the decoder happened to be pointing,
    /// and that is a silent divergence rather than a crash.
    /// </para>
    /// </remarks>
    public byte[] Snapshot()
    {
        var world = World;

        // ⚠️ REFUSED rather than half-taken, and this is the honest edge of the
        // "the bytes ARE the state" claim. WorldState holds no reference to
        // anything, which is what makes the raw round-trip total — but a bot
        // driving a slot is state OUTSIDE it: ScriptedBot carries a route, a
        // target, four clocks and its own RNG stream, and RolloutSearch carries a
        // decision countdown and a decoder. Restoring the world alone would
        // resume the episode with an opponent mid-way through a plan for a world
        // that no longer exists, and it would do it silently.
        //
        // Lifting this means serialising the bot layer, which is a real piece of
        // work and is not wanted yet — docs/ENV.md says so rather than leaving it
        // to be discovered.
        if (_spec.HasBotDriver)
        {
            throw new InvalidOperationException(
                "a snapshot cannot capture a bot-driven slot: the bot's route, clocks and RNG live "
                + "outside WorldState. Drive both slots from the client to snapshot this episode");
        }

        var state = world.State;

        var stateBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in state));
        var bytes = new byte[stateBytes.Length + (sizeof(double) * 2) + sizeof(int)];

        stateBytes.CopyTo(bytes);

        var tail = bytes.AsSpan(stateBytes.Length);
        BitConverter.TryWriteBytes(tail, Decoder.Aim(RedSlot));
        BitConverter.TryWriteBytes(tail[sizeof(double)..], Decoder.Aim(BlueSlot));
        BitConverter.TryWriteBytes(tail[(sizeof(double) * 2)..], _decisions);

        return bytes;
    }

    /// <summary>Restores a snapshot taken from an episode with the same spec.</summary>
    /// <remarks>
    /// ⚠️ <b>Restoring is not the claim; continuing is.</b> A state that compares
    /// equal and then ticks differently is the failure worth catching, so the
    /// gate steps a restored copy against a control that was never serialised
    /// and compares at the END.
    /// </remarks>
    public void Restore(ReadOnlySpan<byte> bytes)
    {
        var world = World;
        var size = MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in world.State)).Length;

        if (bytes.Length != size + (sizeof(double) * 2) + sizeof(int))
            throw new ArgumentException("snapshot is not the right size for this world", nameof(bytes));

        var restored = MemoryMarshal.Read<WorldState>(bytes);
        world.State = restored;

        var tail = bytes[size..];
        Decoder.SetAim(RedSlot, BitConverter.ToDouble(tail));
        Decoder.SetAim(BlueSlot, BitConverter.ToDouble(tail[sizeof(double)..]));
        _decisions = BitConverter.ToInt32(tail[(sizeof(double) * 2)..]);
        _termination = Termination.Running;
    }

    /// <summary>An <see cref="IInputSource"/> that answers with one fixed input.</summary>
    /// <remarks>
    /// The decode happens before the tick rather than inside the source, so that
    /// the aim advance and the jump edge are decided once and are visible to a
    /// snapshot taken between ticks.
    /// </remarks>
    /// <summary>
    /// One decoded action, held for the tick — keys and aim through
    /// <see cref="IInputSource"/>, the scope through <see cref="ICommandSource"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Both interfaces, because the action space spans both.</b>
    /// <see cref="EnvAction.Zoom"/> is not a key — <c>toggleZoom</c> is a client
    /// command — so a client-driven slot reaches it the same way a bot does, via
    /// the one dispatch a socket and the control port also use. Without the second
    /// interface the head would be accepted, echoed back in the reward block, and
    /// silently do nothing, which is the exact shape of the eight-phase gap it was
    /// added to close.
    /// </remarks>
    private sealed class FixedInput(PlayerInput input, EnvAction action) : IInputSource, ICommandSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => input;

        public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
        {
            if (!world.IsAlive(slot))
                return;

            // ⚠️ The edge detector is ActionDecoder's, not a second copy: the head
            // is a LEVEL and the command FLIPS, and that comparison is the whole
            // reason the head is idempotent.
            if (ActionDecoder.WantsZoomToggle(action, in world.Character(slot)))
                commands.Push(PlayerCommand.ToggleZoom());
        }
    }

    /// <summary>Presses nothing, ever, and goes on looking where it was.</summary>
    /// <remarks>
    /// ⚠️ <b><see cref="PlayerInput.None"/> is NOT "no aim" — it is "aim at
    /// zero degrees", due east.</b> An idle driver built on it snaps the
    /// character's facing on its first tick, and <b>facing is observable</b>: it
    /// is what the two-pixel box inflation splash is measured against, and
    /// <see cref="Reset"/> already goes out of its way to make the starting
    /// facing symmetric for exactly that reason. An "opponent that does nothing"
    /// that silently turns to face east is not the comparison floor anybody
    /// means.
    /// <para>
    /// Caught by <c>DuelEnvTests.AnIdleDriverPressesNothingAndStillEarnsItsRewards</c>,
    /// which saw the recovered action come back as a full left turn — the aim
    /// head faithfully reporting a 180° snap nobody asked for.
    /// </para>
    /// <para>
    /// ⚠️ <b>Stateless, so one instance is shared across every world in a batch
    /// and that is safe</b> — it reads the aim back out of the character rather
    /// than remembering it. Every other driver is per-world precisely because it
    /// is not: <c>ScriptedBot</c> carries a route and an RNG, and sharing one
    /// across a parallel batch would be a race that shows up as a
    /// batch-size-dependent trajectory rather than as a crash.
    /// </para>
    /// </remarks>
    private sealed class IdleInput : IInputSource
    {
        public static readonly IdleInput Instance = new();

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            ref readonly var character = ref world.Character(slot);
            return new PlayerInput(PlayerKeys.None, character.NetAimDirection, character.AimDistance);
        }
    }
}
