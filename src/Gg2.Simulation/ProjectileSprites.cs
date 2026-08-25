namespace Gg2.Simulation;

/// <summary>
/// The sprite each projectile wears, as GM8 measured it.
/// </summary>
/// <remarks>
/// <b>A projectile's bounding box is a function of its sprite and its
/// <c>image_angle</c>, and both halves are needed at once.</b>
/// <c>Scripts/Object Creation/createShot.gml</c> sets <c>image_angle = dir</c>,
/// so the box is the rotated one — see <see cref="Aabb.ForRotatedSprite"/> —
/// and rotating it needs the sprite's own bbox and origin. Those are sprite
/// metadata, which this repo otherwise does not keep, so they live here as a
/// small measured table rather than being read back out of the reference tree.
/// <para>
/// Every number below was read out of the running game by
/// <c>tools/sample-rotated-box.js</c> and is checked against that fixture by
/// <c>ProjectileSpriteTests</c>, so the table cannot drift away from the
/// oracle it was taken from.
/// </para>
/// <para>
/// The bounds are the sprite's own <c>sprite_get_bbox_*</c> in sprite pixels,
/// <em>not</em> relative to the origin: <see cref="Aabb.ForRotatedSprite"/>
/// subtracts the origin itself, because the scale is applied after the
/// subtraction and before the rotation.
/// </para>
/// </remarks>
public static class ProjectileSprites
{
    /// <summary>One projectile sprite: its bounding box and its origin.</summary>
    public readonly record struct Sprite(Aabb Bbox, int OriginX, int OriginY);

    /// <summary>
    /// <c>ShotS</c>. Eighteen pixels long and two tall, with the origin at the
    /// <b>tip</b> — the box runs backwards from the position, which is why a
    /// pellet's <c>bbox_right</c> is 0 at zero degrees.
    /// </summary>
    public static Sprite Shot => new(new Aabb(2, 2, 19, 3), 19, 4);

    public static Sprite Rocket => new(new Aabb(6, 3, 13, 4), 7, 4);

    public static Sprite Needle => new(new Aabb(2, 1, 19, 4), 0, 0);

    public static Sprite Flare => new(new Aabb(10, 12, 13, 13), 12, 12);

    public static Sprite Mine => new(new Aabb(0, 0, 5, 5), 3, 3);

    public static Sprite Flame => new(new Aabb(9, 8, 11, 10), 10, 9);

    public static Sprite Blade => new(new Aabb(7, 7, 23, 23), 14, 13);

    /// <summary>The sprite a pooled projectile of this kind wears.</summary>
    public static Sprite For(ProjectileKind kind) => kind switch
    {
        ProjectileKind.Shot => Shot,
        ProjectileKind.Rocket => Rocket,
        ProjectileKind.Needle => Needle,
        ProjectileKind.Flare => Flare,
        ProjectileKind.Mine => Mine,
        ProjectileKind.Flame => Flame,
        ProjectileKind.Blade => Blade,

        // Bubble is a Quote projectile with no rotation and no sample yet; it
        // arrives with the Blade in a later stage rather than being guessed at.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no sprite measured for this projectile"),
    };
}
