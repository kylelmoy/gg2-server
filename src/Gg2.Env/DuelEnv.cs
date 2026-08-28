using System.Runtime.InteropServices;
using Gg2.Bots;
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

    public bool Done => Termination != Termination.Running;
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

        var world = ScenarioWorld.Load(spec.Map, area: 1, _mapDirectory, _navDirectory);

        RedSlot = world.Place(spec.RedClass, Team.Red, spec.RedX, spec.RedY);
        BlueSlot = world.Place(spec.BlueClass, Team.Blue, spec.BlueX, spec.BlueY);

        // ⚠️ The match's own stream, seeded per world. Every seeded weapon draws
        // from it and puts the draw on the wire, so two worlds sharing a seed
        // would fire identical spreads — which is exactly what a batch must not
        // do. EnvBatch derives each world's seed from the base and the index, so
        // batch size cannot move any world's stream.
        //
        // ⚠️ `uint`, matching GmlRandom, and not widened here: GML's generator
        // has a 32-bit seed and pretending to accept 64 would throw away half
        // of whatever a trainer sent without saying so.
        world.State.Random = new GmlRandom(seed);

        // Both agents start facing one another, which is the only neutral
        // choice: facing is observable (it moves the two-pixel box inflation
        // splash is measured against) so "whatever the spawn left" would be a
        // hidden asymmetry.
        Decoder.SetAim(RedSlot, spec.RedX <= spec.BlueX ? 0 : 180);
        Decoder.SetAim(BlueSlot, spec.BlueX < spec.RedX ? 0 : 180);

        _world = world;
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

        for (var tick = 0; tick < _spec.ActionRepeat; tick++)
        {
            var first = tick == 0;

            world.State.Characters[RedSlot].KeyState = default;

            // Decoded fresh every tick — the jump edge and the absolute mask are
            // both properties of doing it here rather than once per decision.
            var redInput = Decoder.Decode(RedSlot, red, first);
            var blueInput = Decoder.Decode(BlueSlot, blue, first);

            world.Drive(RedSlot, new FixedInput(redInput));
            world.Drive(BlueSlot, new FixedInput(blueInput));

            world.Step();

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

        return (
            new StepResult
            {
                DamageDealt = redDealt,
                DamageTaken = redTaken,
                Kills = redKills,
                Deaths = redDeaths,
                Termination = _termination,
            },
            new StepResult
            {
                DamageDealt = blueDealt,
                DamageTaken = blueTaken,
                Kills = blueKills,
                Deaths = blueDeaths,
                Termination = _termination,
            });
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
    private sealed class FixedInput(PlayerInput input) : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => input;
    }
}
