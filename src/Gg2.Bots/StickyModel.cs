using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// The demoman's mine jump, as a <see cref="NavEdgeType.StickyJump"/> edge needs
/// it: how fast it leaves the ground, whether it can be attempted, and how far
/// it carries.
/// </summary>
/// <remarks>
/// <b>The manoeuvre is two-phase and that is what makes it different from every
/// other edge.</b> A rocket jump is one press: `Character.events/Begin Step.xml`
/// runs its attack branch before its jump branch and the rocket detonates in the
/// same step. A mine has to be <em>placed</em>, <em>settle</em>, and only then be
/// jumped on — <see cref="NavConstants.SjSetupTicks"/> apart.
/// <para>
/// ⚠️ <b>The jump and the detonation are the SAME tick.</b> Begin Step's jump
/// branch (line 108) runs before its right-click branch (line 137), so
/// <c>vspeed = -jumpStrength</c> is already in place when the blast's
/// <c>motion_add</c> lands on top of it. Detonating a tick later throws the jump
/// away and the arc is an ordinary hop.
/// </para>
/// <para>
/// ⚠️ <b><see cref="Launch"/> is MEASURED, not derived</b>, and the distinction
/// has already cost this feature once. <c>gg2-nav-gen</c> costed its first 6,497
/// edges against a derived 18.3 px/tick and a ~310 px apex; flying the manoeuvre
/// in this port gave <b>-14.460</b> and <b>193.6 px</b>, and the fork's
/// <c>NAV_SJ_IMPULSE</c> was corrected from 10 to 6.16 the same day.
/// <c>MineJumpTests</c> pins it, and the follower gates on this rather than on
/// anything the cache implies — so a stale cache produces edges this port
/// refuses rather than arcs it flies into a wall.
/// </para>
/// </remarks>
public static class StickyModel
{
    /// <summary>
    /// Vertical speed one tick after the jump-and-detonate, in px/tick, negative
    /// because up is negative.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Measured in this port, identical on four maps to three decimals</b>
    /// — a mine fired straight down settles at the demoman's feet, so the
    /// geometry that sets the impulse is the character's own and not the map's.
    /// <see cref="MineJumpTests"/> is the measurement; if it moves, every
    /// sticky-jump edge in every cache needs regenerating.
    /// </remarks>
    public const double Launch = -14.46;

    /// <summary>The apex this launch reaches with nothing in the way: v² / 2g.</summary>
    /// <remarks>
    /// Under <c>moveStatus</c> 1, which the mine's self-blast sets exactly as a
    /// rocket does — so the gravity is <see cref="ClassStats.RocketJumpGravity"/>
    /// and not the ordinary one. <b>193.6 px.</b>
    /// </remarks>
    public static double Apex => Launch * Launch / (2 * ClassStats.RocketJumpGravity);

    /// <summary>
    /// Whether this character could take a sticky jump right now.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="RocketModel.Ready"/> and for the same
    /// reason: the follower refuses to leave the ground rather than leaving
    /// badly, because an arc flown without its impulse lands somewhere the route
    /// does not mention and costs a perfectly good edge a blacklist.
    /// <para>
    /// ⚠️ <c>&gt;</c> and not <c>&gt;=</c> on the hp, matching
    /// <see cref="NavQuery.CanAffordRocketJump"/>: at exactly
    /// <see cref="NavConstants.SjDamage"/> the demoman dies in the air.
    /// </para>
    /// <para>
    /// ⚠️ <b>Ammo is checked but the mine in flight is not.</b> Whether a mine is
    /// already placed is the follower's state to know, not this model's — see
    /// <c>BotPathState.StickySetup</c>.
    /// </para>
    /// </remarks>
    public static bool Ready(in CharacterState character, in WeaponState weapon) =>
        character.Hp > NavConstants.SjDamage
        && WeaponStats.KindFor(character.Class) == WeaponKind.Minegun
        && weapon.ReadyToShoot
        && weapon.AmmoCount > 0;

    /// <summary>
    /// How far along the arc this character gets — the same integration a rocket
    /// jump uses.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Deliberately delegated rather than copied.</b> A mine jump flies
    /// under <c>moveStatus</c> 1 exactly as a rocket jump does, so the control
    /// factor is <see cref="NavConstants.RjControl"/> and the friction
    /// <see cref="NavConstants.RjFriction"/> — the same two numbers
    /// <see cref="RocketModel.Reach"/> already integrates. The one thing a rocket
    /// has that a mine does not is <c>NAV_RJ_SPEEDUP</c>, and
    /// <c>RocketModel.Reach</c> does not use it. A second copy of this
    /// integration would be a second thing to keep correct.
    /// </remarks>
    public static double Reach(in CharacterState character, double needVx, double flightTicks, double v0) =>
        RocketModel.Reach(in character, needVx, flightTicks, v0);
}
