using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The per-class movement numbers, from <c>Objects/Characters/*.events/Create.xml</c>
/// with the shared defaults from <c>Character.events/Create.xml:168-175</c>.
/// </summary>
public readonly record struct ClassStats
{
    /// <summary>
    /// <c>Character.events/Create.xml:168</c> writes <c>0.85</c>. This is the
    /// double Game Maker 8 actually ends up holding, which is one ULP higher —
    /// see <see cref="Gml"/>.
    /// </summary>
    public const double BaseControl = Gml.PointEightFive;

    /// <summary>
    /// Every class shares this too. It must never be 0 or 1 — the reference
    /// says so in a comment, because <see cref="BaseMaxSpeed"/> divides by
    /// <c>baseFriction - 1</c>, and that subtraction is where the ULP below
    /// stops being cosmetic.
    /// </summary>
    public const double BaseFriction = Gml.OnePointOneFive;

    /// <summary>
    /// <c>8 + (0.6/2)</c>, exactly as <c>Character.events/Create.xml:29</c>
    /// writes it. The half-gravity term compensates for the split gravity: half
    /// a tick's worth is applied before the move and half after, so the launch
    /// has to be larger by the half that no longer lands on the first frame. Get
    /// this wrong and every jump in the game is the wrong height.
    /// </summary>
    public const double BaseJumpStrength = 8 + (Gravity / 2);

    /// <summary>Gravity per tick, normal.</summary>
    public const double Gravity = Gml.PointSix;

    /// <summary>Gravity per tick while rocket-jumping — lighter, so the arc carries.</summary>
    /// <remarks><c>0.54</c> is one of the literals GM8 parses to the IEEE nearest.</remarks>
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
    /// How many frames the class's taunt sprite has, which is the whole of a
    /// taunt's duration.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/Create.xml:79</c> takes <c>tauntsprite =
    /// getCharacterSpriteId(player.class, player.team, "Taunt")</c> and
    /// <c>End Step.xml:93</c> ends the taunt at <c>tauntindex &gt;=
    /// sprite_get_number(tauntsprite)</c>. Counted from the reference tree —
    /// each <c>Sprites/Characters/&lt;class&gt;/&lt;class&gt;&lt;team&gt;TauntS.images</c>
    /// directory, red and blue identical for every class — rather than measured
    /// live, because a frame count is a file count and not a number GM8 can get
    /// wrong.
    /// <para>
    /// <b>The classes differ by nearly a factor of two</b> — a scout's taunt
    /// is 9 frames and Quote's is 17 — so a taunt is 30 ticks for one and 57 for
    /// the other. It is not a shared constant and must not be made into one.
    /// </para>
    /// </remarks>
    public required int TauntFrames { get; init; }

    /// <summary>
    /// <c>abs(baseRunPower * baseControl / (baseFriction - 1))</c> — the speed
    /// at which acceleration and friction balance.
    /// </summary>
    public double BaseMaxSpeed => Math.Abs(RunPower * BaseControl / (BaseFriction - 1));

    public static ClassStats For(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Scout => new ClassStats { RunPower = 1.4, MaxHp = 100, CanDoubleJump = true, TauntFrames = 9 },
        PlayerClass.Soldier => new ClassStats { RunPower = 0.9, MaxHp = 160, TauntFrames = 16 },
        PlayerClass.Sniper => new ClassStats { RunPower = 0.9, MaxHp = 120, TauntFrames = 13 },
        PlayerClass.Demoman => new ClassStats { RunPower = 1.0, MaxHp = 120, TauntFrames = 11 },
        PlayerClass.Medic => new ClassStats { RunPower = 1.09, MaxHp = 120, TauntFrames = 12 },
        PlayerClass.Engineer => new ClassStats { RunPower = 1.0, MaxHp = 120, TauntFrames = 13 },
        PlayerClass.Heavy => new ClassStats { RunPower = 0.8, MaxHp = 200, TauntFrames = 12 },
        PlayerClass.Spy => new ClassStats { RunPower = 1.08, MaxHp = 100, CanCloak = true, TauntFrames = 11 },
        PlayerClass.Pyro => new ClassStats { RunPower = 1.1, MaxHp = 120, TauntFrames = 10 },
        PlayerClass.Quote => new ClassStats { RunPower = 1.07, MaxHp = 140, TauntFrames = 17 },
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
