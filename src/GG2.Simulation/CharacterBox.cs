using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// A character's collision rectangle, as integer offsets from its origin.
/// </summary>
/// <remarks>
/// Characters collide as <b>rectangles</b>, not as their drawn sprites. The
/// numbers come from the sprite's manual mask bounds relative to its origin
/// (<c>Character.events/Create.xml:5-8</c>,
/// <c>sprite_get_bbox_* - sprite_get_*offset</c>), and they are integers
/// because a GM8 sprite's bounding box is.
/// <para>
/// Both ends are <b>inclusive</b>: a box whose right edge lands exactly on the
/// first solid pixel of a wall is blocked. Measured — see
/// <see cref="CollisionWorld.PlaceFree"/>.
/// </para>
/// </remarks>
public readonly record struct CharacterBox
{
    public CharacterBox(int left, int right, int top, int bottom)
    {
        if (left > right || top > bottom)
            throw new ArgumentException($"degenerate box ({left},{top})..({right},{bottom})");

        Left = left;
        Right = right;
        Top = top;
        Bottom = bottom;
    }

    /// <summary>Offset of the leftmost covered pixel from the origin. Negative.</summary>
    public int Left { get; }

    /// <summary>Offset of the rightmost covered pixel. Inclusive.</summary>
    public int Right { get; }

    public int Top { get; }

    /// <summary>Offset of the lowest covered pixel. Inclusive.</summary>
    public int Bottom { get; }

    public int Width => Right - Left + 1;

    public int Height => Bottom - Top + 1;

    /// <summary>
    /// The per-class boxes, read out of the running game.
    /// </summary>
    /// <remarks>
    /// <c>sprite_get_bbox_* - sprite_get_*offset</c> for each class's own
    /// sprite, queried live rather than inferred: the classes do <b>not</b>
    /// follow a pattern. Widths are 6, 7 or 9; tops range from -6 to -12; and
    /// Quote is a different shape entirely, ending at +11 where every other
    /// class ends at +23.
    /// <para>
    /// Red and blue share a mask on every class, so team does not enter into
    /// it — checked, not assumed. Quote's sprites are named
    /// <c>Querly*</c>, which is why they do not turn up under a
    /// <c>&lt;Class&gt;&lt;Team&gt;S</c> search.
    /// </para>
    /// </remarks>
    public static CharacterBox For(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Scout => new CharacterBox(-6, 6, -10, 23),
        PlayerClass.Soldier => new CharacterBox(-6, 6, -8, 23),
        PlayerClass.Sniper => new CharacterBox(-6, 6, -8, 23),
        PlayerClass.Demoman => new CharacterBox(-7, 7, -10, 23),
        PlayerClass.Medic => new CharacterBox(-7, 7, -8, 23),
        PlayerClass.Engineer => new CharacterBox(-6, 6, -10, 23),
        PlayerClass.Heavy => new CharacterBox(-9, 9, -12, 23),
        PlayerClass.Spy => new CharacterBox(-6, 6, -10, 23),
        PlayerClass.Pyro => new CharacterBox(-7, 7, -6, 23),
        PlayerClass.Quote => new CharacterBox(-7, 7, -12, 11),
        _ => throw new ArgumentOutOfRangeException(nameof(playerClass), playerClass, "no box for this class"),
    };
}
