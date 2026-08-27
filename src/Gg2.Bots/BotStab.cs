using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// <c>botStabWindow</c>: would a spy pressing ATTACK <em>now</em> actually connect?
/// </summary>
/// <remarks>
/// ⚠️ <b>The obvious test — "the enemy is within <c>BOT_STAB_RANGE</c> right now"
/// — is aimed a full second early.</b> The mechanic, out of the Revolver's cloaked
/// branch and its alarms:
/// <list type="number">
/// <item>the press <b>freezes</b> the spy (<c>runPower</c> and
/// <c>jumpStrength</c> to 0), spawns the animation facing
/// <c>aimDirection</c>, and arms <c>alarm[1] = 32</c>;</item>
/// <item>thirty-two ticks later that alarm creates the hitbox — 200 damage, lethal
/// from any angle, pinned to the owner every step so it travels nowhere of its
/// own;</item>
/// <item>the hitbox lives <c>alarm[0] = 6</c> more ticks.</item>
/// </list>
/// So the damage window is <b>t+32 to t+38</b> and the spy cannot move during it.
/// A press keyed on where the enemy is <em>now</em> is close to a guaranteed miss
/// against anything moving, and it spends 38 frozen ticks in the open to take it.
/// <para>
/// ⚠️ <b>The box is the sprite, not a radius.</b> <c>StabMaskS</c> is 64×64 with
/// origin (26, 37), and the mask's Step sets <c>image_xscale</c> from
/// <c>direction</c> without ever touching <c>image_angle</c> — so it is
/// axis-aligned and merely flipped: 37px the way the spy faces, 26px behind, 37px
/// up and 26px down. <c>BOT_STAB_RANGE</c> therefore stops being the trigger and
/// stays what it always described, the reach.
/// </para>
/// <para>
/// ⚠️ <b>The target is grown by its own bounds rather than treated as a point</b>,
/// because the mask collides mask-to-mask: a character whose centre is just
/// outside the box still overlaps it by a shoulder.
/// </para>
/// <para>
/// Deliberately says nothing about whether the spy is cloaked, has
/// <c>readyToStab</c>, or is looking at an ally. Those are
/// <see cref="BotClassKeys"/>' preconditions and they are cheaper than this is.
/// </para>
/// </remarks>
public static class BotStab
{
    /// <summary>
    /// Whether <paramref name="target"/> is predicted to be inside the knife while
    /// the knife exists.
    /// </summary>
    /// <param name="graph">
    /// For the floor under the target. Null is the same answer a target over a pit
    /// gets — no floor — which is what <see cref="BotAim.NoFloor"/> means.
    /// </param>
    public static bool Window(
        NavGraph? graph, in CharacterState self, in CharacterState target)
    {
        // The mask in world coordinates around where the spy is standing — which is
        // also where it will still be standing, since the press is what freezes it.
        //
        // ⚠️ Inclusive at BOTH ends, so an aim of exactly 90 or exactly 270 faces
        // left. That is the reference's own test and it decides which way the box
        // is 37 long on a spy looking straight up or straight down.
        var facing = self.AimDirection >= 90 && self.AimDirection <= 270 ? -1 : 1;

        var maskLeft = facing > 0
            ? self.X - BotConstants.StabBack
            : self.X - BotConstants.StabReach;

        var maskRight = facing > 0
            ? self.X + BotConstants.StabReach
            : self.X + BotConstants.StabBack;

        var maskTop = self.Y - BotConstants.StabReach;
        var maskBottom = self.Y + BotConstants.StabBack;

        // ⚠️ The floor the target cannot be predicted THROUGH. A target running off
        // a ledge is exactly the case a straight-line prediction gets wrong, and at
        // knife range it is common. No node below is the same as no floor.
        var floor = BotAim.FloorUnder(graph, target.X, target.Y);

        var box = target.Box;
        var vx = target.Motion.HSpeed;

        for (var t = BotConstants.StabLeadMin; t <= BotConstants.StabLeadMax; t++)
        {
            // Constant horizontal velocity, and the same fall model the aim solver
            // leads with — gated in BotAimTests rather than a second time here.
            var px = target.X + (vx * t);
            var py = BotAim.FallPredict(target.Y, target.Motion.VSpeed, t, floor);

            // ⚠️ Fuzzy comparisons, because the reference's are: GML's `>=` is
            // fuzzy to 1e-13, and a target whose shoulder lands exactly on the
            // mask's edge is a real outcome rather than a rounding artefact.
            if (Gml.GreaterOrEqual(px + box.Right, maskLeft)
                && Gml.LessOrEqual(px + box.Left, maskRight)
                && Gml.GreaterOrEqual(py + box.Bottom, maskTop)
                && Gml.LessOrEqual(py + box.Top, maskBottom))
            {
                return true;
            }
        }

        return false;
    }
}
