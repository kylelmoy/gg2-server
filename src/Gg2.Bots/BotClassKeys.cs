using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// Which keys a class presses at a target it is already aiming at —
/// <c>Scripts/Bots/botClassKeys.gml</c>.
/// </summary>
/// <remarks>
/// Called <em>after</em> the aim has been solved and slewed, because three of the
/// rules read the aim: a splash class checks its own arc, a sniper checks its
/// charge against the range, and a spy checks whether the knife would land.
/// <para>
/// ⚠️ <b>The gating happens in <see cref="BotCombat"/>, not here.</b> This says
/// what the class <em>wants</em> to press; the fire delay, the settle test and
/// the hold-fire roll take ATTACK away afterwards. Folding them in would apply
/// them to the reactive specials too.
/// </para>
/// <para>
/// <b>Four refinements are not ported, and each is conservative when absent</b> —
/// deliberately, so that a missing helper makes a bot shoot <em>less</em> rather
/// than shoot wrongly:
/// </para>
/// <list type="bullet">
/// <item><c>botArcClear</c> — a splash class checking that its own arc reaches
/// the target without hitting a wall first. Absent, <c>arcClear</c> is true,
/// which is also the reference's value when the weapon has no ballistics. A
/// soldier therefore fires at a target behind cover it would have declined.</item>
/// <item><c>botIncomingProjectile</c> — the pyro's airblast trigger. Absent, a
/// pyro never airblasts.</item>
/// <item><c>botStabWindow</c> — whether a cloaked spy's knife would land. Absent,
/// a spy never stabs and shoots instead.</item>
/// <item><c>botMinesArmed</c> — whether a demoman has mines worth detonating.
/// Absent, it never detonates them.</item>
/// </list>
/// </remarks>
public static class BotClassKeys
{
    /// <summary>What this class wants to press, before the fire gates.</summary>
    /// <param name="subjectIsAlly">
    /// Whether the thing being aimed at is a team-mate to heal rather than an
    /// enemy to shoot. ⚠️ Only the medic reads it, but it has to be a parameter
    /// rather than something derived here: the ally lives on the combat state and
    /// the decision to use it belongs to the caller, which is the only place that
    /// has already re-validated it this tick.
    /// </param>
    public static PlayerKeys For(
        BotCombatState state, in WorldView world, int slot, double distance, int tick,
        bool subjectIsAlly = false)
    {
        // ⚠️ An ally is a subject even with no target, which is the whole point: a
        // medic with a patient and no enemy in sight still beams.
        if (!state.Target.Exists && !subjectIsAlly)
            return PlayerKeys.None;

        ref readonly var self = ref world.Character(slot);
        ref readonly var weapon = ref world.Weapon(slot);

        switch (self.Class)
        {
            case PlayerClass.Soldier:
                // ⚠️ Never inside the splash-safe band: a rocket at that range
                // kills the shooter. The band is the class's own minimum, so
                // this and the targeting penalty are the same number by
                // construction.
                return distance >= BotTargeting.ClassMinBand(PlayerClass.Soldier)
                    ? PlayerKeys.Attack
                    : PlayerKeys.None;

            case PlayerClass.Demoman:
                // The detonation (SPECIAL) waits on botMinesArmed, which is not
                // ported — see this type's remarks.
                return distance >= BotTargeting.ClassMinBand(PlayerClass.Demoman)
                    ? PlayerKeys.Attack
                    : PlayerKeys.None;

            case PlayerClass.Pyro:
                // ⚠️ Distance rather than the class's attention radius: a flame
                // reaches BOT_FLAME_REACH and a pyro that pulls the trigger
                // further out is burning air and emptying its tank. The airblast
                // waits on botIncomingProjectile.
                return distance <= BotConstants.FlameReach ? PlayerKeys.Attack : PlayerKeys.None;

            case PlayerClass.Medic:
                // ⚠️ The needle gun is on the RIGHT click — the Medigun's left
                // is the heal beam — so a medic with nobody to heal presses SPECIAL
                // to shoot. That is also why the medic is the one class whose SPECIAL
                // is gated by the fire delay.
                if (!subjectIsAlly)
                    return PlayerKeys.Special;

                // The heal beam is the primary. ⚠️ the uber is SPECIAL *while*
                // the primary is held — it is not a separate press — and it is worth
                // its whole charge only with an enemy close enough to matter.
                var beam = PlayerKeys.Attack;

                if (world.Weapon(slot).UberReady
                    && BotTargeting.EnemyWithin(in world, slot, BotConstants.UberRange))
                {
                    beam |= PlayerKeys.Special;
                }

                return beam;

            case PlayerClass.Spy:
                return SpyKeys(state, in self, tick);

            case PlayerClass.Sniper:
                // ⚠️ Unscoped, fire immediately; scoped, wait for a charge
                // proportional to the range. The Rifle's damage IS its range, so
                // a scoped sniper firing early simply cannot reach — and one that
                // waited for a full charge at every distance would never shoot at
                // anything close.
                if (!self.Zoomed)
                    return PlayerKeys.Attack;

                return weapon.Charge
                    >= Weapons.RifleChargeTime
                        * Math.Min(1, distance / BotTargeting.ClassRange(PlayerClass.Sniper))
                    ? PlayerKeys.Attack
                    : PlayerKeys.None;

            default:
                // The scout, the heavy, the engineer and Quote: point and shoot.
                return PlayerKeys.Attack;
        }
    }

    /// <summary>
    /// The spy: cloak when alone, uncloak to fight, and shoot when uncloaked.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>cloak == enemyNear</c> is the whole toggle, and it reads as a typo
    /// until you see it: the spy wants to be cloaked when <em>no</em> enemy is
    /// near and uncloaked when one is, so it presses the key exactly when the two
    /// agree — cloaked with company, or uncloaked alone.
    /// <para>
    /// ⚠️ Refused to an intel carrier, because dropping the cloak costs 25 ticks
    /// of grab — and rate-limited to <c>BOT_CLOAK_PERIOD</c>, or a spy on the
    /// boundary flickers.
    /// </para>
    /// <para>
    /// The stab branch waits on <c>botStabWindow</c>, which is not ported, so a
    /// cloaked spy currently never stabs.
    /// </para>
    /// </remarks>
    private static PlayerKeys SpyKeys(BotCombatState state, in CharacterState self, int tick)
    {
        var enemyNear = state.Target.Kind == BotTargetKind.Character;

        // ⚠️ `canCloak` in the reference is a Character field the spy's Create
        // event sets; here the class IS the test, because the spy is the only one
        // this branch is reached for. The intel refusal is the reference's own
        // and is not redundant: dropping the cloak costs 25 ticks of grab.
        if (self.Cloak == enemyNear && !self.CarryingIntel)
        {
            if (tick - state.CloakAt < BotConstants.CloakPeriod)
                return PlayerKeys.None;

            state.CloakAt = tick;
            return PlayerKeys.Special;
        }

        return self.Cloak ? PlayerKeys.None : PlayerKeys.Attack;
    }
}
