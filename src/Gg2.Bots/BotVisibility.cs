using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// Whether a bot is allowed to see a character at all — the cloak, and the one
/// thing that pulls a cloaked spy back into view.
/// </summary>
/// <remarks>
/// <b>The bot layer's rule is that a bot acts only on what a player at the same
/// place would see</b>, and the spy is the only thing in the game that a live
/// enemy cannot see. Every candidate loop in the scripted layer used to spell
/// that as <c>candidate.Cloak</c> and stop there, which is right for a spy
/// walking and wrong for a spy stabbing.
/// <para>
/// ⛔ <b>The scripted layer only.</b> The policy observation reaches its enemies
/// through <see cref="WorldView.NearestEnemies"/>, which has no visibility test
/// of any kind, so a <c>PolicyNetwork</c> is handed a fully cloaked spy's
/// position and velocity. Fixing that means changing what the observation floats
/// mean, which dates every checkpoint trained against them —
/// <c>docs/TRAPS-BOTS.md</c>, "the cloak is not the whole of what a bot cannot
/// see", has what would show it has been dealt with.
/// </para>
/// <para>
/// <b>A stab is drawn over the cloak.</b> The knife is a separate instance —
/// <c>Objects/Projectiles/StabAnim.events/Draw.xml</c> — and it draws the spy's
/// torso and legs at its <em>own</em> alpha, which owes nothing to
/// <c>cloakAlpha</c>: its Step ramps that alpha up 0.1 a tick from 0.01, holds it
/// at 1 while <c>image_index</c> climbs to 32, then fades it back out. The
/// character's own Draw meanwhile subtracts the same alpha squared from its body
/// (<c>Character.events/Draw.xml:36</c>), so the two cross-fade and what a player
/// sees is a fully opaque spy standing still. That is the whole of the
/// <see cref="Weapons.StabFreezeTicks"/> freeze — the knife's hitbox does not
/// arrive until tick 32 of it (<see cref="Weapons.StabWindUp"/>), so a bot that
/// can see the animation has about a second to answer it.
/// </para>
/// <para>
/// ⚠️ <b>And the animation is hidden from exactly the player being stabbed.</b>
/// Both <c>StabAnim</c>'s Draw and its End Step gate on <c>owner.invisible</c>,
/// which <c>Objects/Characters/Spy.events/End Step.xml</c> sets per viewer: an
/// enemy spy whose <c>x</c> is on the side the viewer's <c>image_xscale</c> is
/// pointing away from is not drawn at all. That is GG2's spy — invisible from
/// behind, cloak or no cloak — and it is why <see cref="CanSee"/> is a question
/// about a pair rather than about a character. Without it, seeing the animation
/// would hand every bot the one thing the backstab is built out of.
/// </para>
/// <para>
/// <b>Deliberately NOT modelled: the fade, and the "behind you" rule for a spy
/// that is not stabbing.</b> A cloak takes 20 ticks to fade out and a hit
/// flickers it back to 0.3 for three (<c>dealFlicker</c>), so this is a tick or
/// two pessimistic at each edge of a cloak toggle and blind to the flicker; and a
/// bot still sees an uncloaked spy standing behind it, which a player does not.
/// The second is the load-bearing one: a player shot in the back turns round and
/// the spy appears, and a bot has no such reflex — its facing follows its aim,
/// which follows a target it would no longer be allowed to have. Hiding that spy
/// without giving the bot a reason to turn would make the blind spot permanent
/// rather than momentary. <c>docs/TRAPS-BOTS.md</c>, "the cloak is not the whole
/// of what a bot cannot see".
/// </para>
/// </remarks>
public static class BotVisibility
{
    /// <summary>
    /// Can <paramref name="self"/> see <paramref name="subject"/>: true for
    /// anyone uncloaked, and for a cloaked spy only while its knife animation is
    /// running in front of the viewer.
    /// </summary>
    /// <remarks>
    /// Says nothing about team, hp, range or line of sight — every caller tests
    /// those itself, and a cloaked team-mate is visible to its own side
    /// (<c>Character.events/Draw.xml:30</c> draws one at <c>cloakAlpha/2 +
    /// 0.5</c>) for whatever that is worth to a bot that will skip it anyway.
    /// </remarks>
    public static bool CanSee(in CharacterState self, in CharacterState subject)
    {
        if (!subject.Cloak)
            return true;

        // A cloaked intel carrier is unreachable rather than merely unlikely:
        // CharacterPhysics.CloakStep refuses the toggle to a carrier and
        // Intel.cs drops the cloak on pickup, so the reference's `and !intel`
        // exception to `invisible` cannot be hit from here and is not repeated.
        return subject.Stabbing && !IsBehind(in self, in subject);
    }

    /// <summary>
    /// <c>sign(x - myself.x) == -sign(myself.image_xscale)</c>: is the subject on
    /// the side the viewer's back is turned to.
    /// </summary>
    /// <remarks>
    /// <b>Purely horizontal, and neither distance nor height is in it</b> — a spy
    /// directly overhead shares the viewer's <c>x</c>, <c>sign</c> gives 0, and 0
    /// is not −1, so it is in front. <see cref="CharacterState.Facing"/> is
    /// <c>image_xscale</c> and is already ±1; <see cref="Gml.Sign"/> rather than
    /// <see cref="System.Math.Sign"/> because the x difference carries the
    /// engine's fuzz around zero and this is the same comparison the reference
    /// makes.
    /// </remarks>
    private static bool IsBehind(in CharacterState viewer, in CharacterState subject)
        => Gml.Sign(subject.X - viewer.X) == -Gml.Sign(viewer.Facing);
}
