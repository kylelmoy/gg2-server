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

    /// <summary>
    /// <c>BubbleS</c>: a twelve-pixel square with the origin in the middle.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The one entry in this table that was not sampled from the running
    /// game</b>, because Quote's weapon landed without a live session. It is
    /// derived rather than guessed: <c>Sprites/Projectiles/BubbleS.xml</c>
    /// declares <c>mode="FULL"</c>, which means the bounding box is the whole
    /// image rather than the alpha's extent, and the image is 12x12.
    /// <para>
    /// The control is <see cref="Mine"/>: <c>MineS.xml</c> is also
    /// <c>mode="FULL"</c>, its image is 6x6, and the engine reported
    /// <c>0, 0, 5, 5</c> — the whole image — when
    /// <c>tools/sample-rotated-box.js</c> asked it. So the rule this entry
    /// leans on is measured even though this entry is not. Sample it anyway at
    /// the next live session; CLAUDE.md's warning about a MANUAL bound being
    /// clamped to the image is a reminder that the XML is not the engine.
    /// </para>
    /// </remarks>
    public static Sprite Bubble => new(new Aabb(0, 0, 11, 11), 6, 6);

    /// <summary>
    /// <c>StabMaskS</c>: the spy's knife, which is much bigger than the knife.
    /// </summary>
    /// <remarks>
    /// An AUTO bound on a 64x64 image about origin (26, 37), so about the
    /// origin it runs <b>2 pixels back, 31 forward, 24 up and 18 down</b> — a
    /// 34x43 box, the largest melee reach in the game. ⚠️ <b>And
    /// <c>image_xscale</c> is ±1</b>, from the stab's direction
    /// (<c>StabMask.events/Step.xml</c>), so a left-facing stab mirrors all of
    /// it — which is also what decides whether a hit is a backstab.
    /// <para>
    /// Read off the PNG's alpha extent rather than sampled, like
    /// <see cref="Bubble"/>. Unlike the bubble's there is no measured control
    /// for AUTO in this table; sample it at the next live session.
    /// </para>
    /// </remarks>
    public static Sprite Stab => new(new Aabb(24, 13, 57, 55), 26, 37);

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
        ProjectileKind.Bubble => Bubble,
        ProjectileKind.Stab => Stab,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no sprite measured for this projectile"),
    };
}
