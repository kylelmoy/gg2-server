using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The per-class movement numbers, from <c>Objects/Characters/*.events/Create.xml</c>
/// with the shared defaults from <c>Character.events/Create.xml:168-175</c>.
/// </summary>
public readonly record struct ClassStats
{
    /// <summary>Every class shares this. <c>Character.events/Create.xml:168</c>.</summary>
    public const double BaseControl = 0.85;

    /// <summary>
    /// Every class shares this too. It must never be 0 or 1 — the reference
    /// says so in a comment, because <see cref="BaseMaxSpeed"/> divides by
    /// <c>baseFriction - 1</c>.
    /// </summary>
    public const double BaseFriction = 1.15;

    /// <summary>
    /// <c>8 + 0.6/2</c>. The half-gravity term compensates for the split
    /// gravity: half a tick's worth is applied before the move and half after,
    /// so the launch has to be larger by the half that no longer lands on the
    /// first frame. Get this wrong and every jump in the game is the wrong
    /// height.
    /// </summary>
    public const double BaseJumpStrength = 8 + (0.6 / 2);

    /// <summary>Gravity per tick, normal.</summary>
    public const double Gravity = 0.6;

    /// <summary>Gravity per tick while rocket-jumping — lighter, so the arc carries.</summary>
    public const double RocketJumpGravity = 0.54;

    /// <summary>Terminal fall speed. Note it clamps only downward.</summary>
    public const double TerminalVSpeed = 10;

    /// <summary>Per-axis speed clamp, applied independently to each.</summary>
    public const double MaxSpeed = 15;

    /// <summary>
    /// Below this, and not holding a direction, horizontal speed is zeroed.
    /// The reference calls it "avoid ice skating".
    /// </summary>
    public const double RestSpeed = 0.195;

    public required double RunPower { get; init; }

    public required int MaxHp { get; init; }

    public bool CanDoubleJump { get; init; }

    public bool CanCloak { get; init; }

    /// <summary>
    /// <c>abs(baseRunPower * baseControl / (baseFriction - 1))</c> — the speed
    /// at which acceleration and friction balance.
    /// </summary>
    public double BaseMaxSpeed => Math.Abs(RunPower * BaseControl / (BaseFriction - 1));

    public static ClassStats For(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Scout => new ClassStats { RunPower = 1.4, MaxHp = 100, CanDoubleJump = true },
        PlayerClass.Soldier => new ClassStats { RunPower = 0.9, MaxHp = 160 },
        PlayerClass.Sniper => new ClassStats { RunPower = 0.9, MaxHp = 120 },
        PlayerClass.Demoman => new ClassStats { RunPower = 1.0, MaxHp = 120 },
        PlayerClass.Medic => new ClassStats { RunPower = 1.09, MaxHp = 120 },
        PlayerClass.Engineer => new ClassStats { RunPower = 1.0, MaxHp = 120 },
        PlayerClass.Heavy => new ClassStats { RunPower = 0.8, MaxHp = 200 },
        PlayerClass.Spy => new ClassStats { RunPower = 1.08, MaxHp = 100, CanCloak = true },
        PlayerClass.Pyro => new ClassStats { RunPower = 1.1, MaxHp = 120 },
        PlayerClass.Quote => new ClassStats { RunPower = 1.07, MaxHp = 140 },
        _ => throw new ArgumentOutOfRangeException(nameof(playerClass), playerClass, "no stats for this class"),
    };

    /// <summary>
    /// <c>delta_mult_skip(f)</c> at 30 Hz: <c>f * 0.5 + 0.5</c>.
    /// </summary>
    /// <remarks>
    /// The reference's <c>skip_delta_factor</c> is 1/2 at 30 fps, so the
    /// friction divisor is half-way between 1 and <c>f</c> — because friction is
    /// applied twice per tick by the frameskip loop.
    /// </remarks>
    public static double DeltaMultSkip(double factor) => factor * 0.5 + 0.5;
}
