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
    /// <summary>
    /// What one airblast costs, and so what the tank must hold to attempt one.
    /// </summary>
    /// <remarks>
    /// <c>Flamethrower/User Event 2</c>'s own <c>ammoCount &gt;= 40</c>.
    /// </remarks>
    private const double AirblastAmmo = 40;

    public static PlayerKeys For(
        BotCombatState state, in WorldView world, int slot, double distance, int tick,
        bool subjectIsAlly = false)
    {
        // ⚠️ NOT a blanket "no subject, no keys". `botCombatUpdate`'s own
        // no-subject branch still calls this — "nothing to aim at is a state a class
        // can still have an opinion about: a spy re-cloaks, an engineer builds, a
        // heavy eats" — and the pyro's AIRBLAST is the case that made it matter here.
        // A pyro with nothing to shoot at must still be able to reflect a rocket,
        // and a top guard sent it home before the branch that decides that.
        //
        // So every branch below that would otherwise fire at nothing carries its own
        // `subject` test, exactly as the reference's do. ⚠️ the pyro's flame
        // needs one because `distance` is 0 with no target and 0 is inside the flame's
        // reach — without it a pyro with no target holds the trigger for ever.
        var hasSubject = state.Target.Exists || subjectIsAlly;

        // Does the shot this bot is lined up on actually have a path to what it is
        // aimed at?
        //
        // ⚠️ Nothing else in the bot layer ever asks. Every sight test is a straight
        // chest-to-chest line, while the aim solver deliberately aims ABOVE the
        // target to compensate for drop — ~178px of it at 500px for a Minegun round.
        // So a demoman with a clear straight line to a target across a room is aiming
        // a whole storey above it, and indoors that is the reported "sometimes
        // shooting at the ceiling". See BotArc.
        //
        // ⚠️ Only for the two classes whose shot kills them if it goes off next to
        // them, and only with a subject. Everyone else pays nothing: a scattergun
        // round that clips a lintel costs ammo the class has plenty of, and
        // withholding would make a scout stop firing in exactly the corridors it
        // wants to be firing in.
        //
        // Computed once here rather than in the two branches, because it is the same
        // test for both.
        var arcClear = true;

        if (state.Target.Exists
            && world.Character(slot).Class is PlayerClass.Soldier or PlayerClass.Demoman)
        {
            var (speed, gravity, _) = BotAim.Ballistics(in world.Character(slot));

            if (speed > 0)
            {
                arcClear = BotArc.Clear(
                    world.Collision, in world.Character(slot),
                    world.Character(slot).AimDirection, speed, gravity,
                    state.Target.X, state.Target.Y);
            }
        }

        ref readonly var self = ref world.Character(slot);
        ref readonly var weapon = ref world.Weapon(slot);

        switch (self.Class)
        {
            case PlayerClass.Soldier:
                // ⚠️ Never inside the splash-safe band: a rocket at that range
                // kills the shooter. The band is the class's own minimum, so
                // this and the targeting penalty are the same number by
                // construction.
                // ⚠️ Rockets fly flat, so arcClear is very nearly the straight-line
                // test the target search already passed — but NOT quite, because a
                // rocket fired with lead does not go down the line the target was
                // cleared on.
                //
                // ⚠️ UNGATED for the soldier, and deliberately recorded as such:
                // dropping `arcClear` from this branch alone leaves every test green,
                // because the only fixtures that separate it from the straight line
                // need a MOVING target for the aim solver to lead. The demoman's half
                // is gated (BotArcTests), and the two share the one computation
                // above, so a change that breaks this breaks that.
                return hasSubject && arcClear
                    && distance >= BotTargeting.ClassMinBand(PlayerClass.Soldier)
                    ? PlayerKeys.Attack
                    : PlayerKeys.None;

            case PlayerClass.Demoman:
                // The detonation (SPECIAL) waits on botMinesArmed, which is not
                // ported — see this type's remarks.
                // ⚠️ Withholding rather than re-solving for a flatter shot is
                // deliberate: a flatter solution generally means closing the
                // distance, and where this bot stands is the objective layer's
                // decision rather than this one's.
                return hasSubject && arcClear
                    && distance >= BotTargeting.ClassMinBand(PlayerClass.Demoman)
                    ? PlayerKeys.Attack
                    : PlayerKeys.None;

            case PlayerClass.Pyro:
            {
                // ⚠️ Distance rather than the class's attention radius: a flame
                // reaches BOT_FLAME_REACH and a pyro that pulls the trigger
                // further out is burning air and emptying its tank.
                var flame = hasSubject && distance <= BotConstants.FlameReach
                    ? PlayerKeys.Attack
                    : PlayerKeys.None;

                // ⚠️ The airblast is a REACTION and is not gated by the fire delay
                // — a reflect a second and a half late is not a reflect. It is on
                // SPECIAL, which for a pyro is not part of firing, so the
                // suppression in BotCombat leaves it alone.
                //
                // ⚠️ And it is asked of a BEAM rather than a radius. The trigger
                // volume is 28-126px along the aim and 18px either side of it, and
                // a press costs 40 ticks of cooldown whether or not anything was
                // there — see BotReflex.
                // ⚠️ readyToBlast and the ammo are checked FIRST and are not
                // decoration. The blast is on a 40-tick cooldown and the reflect test
                // inside User Event 2 runs only on the frame of the press, so the cost
                // of a press aimed at nothing is not the ammo — it is that the next
                // rocket, the one that was going to land, arrives while the weapon is
                // still reloading. They are also much the cheapest of the three tests
                // and gate the beam scan regardless of geometry.
                if (weapon.ReadyToBlast
                    && weapon.AmmoCount >= AirblastAmmo
                    && BotReflex.IncomingProjectile(in world, slot, BotConstants.AirblastMinTravel))
                {
                    flame |= PlayerKeys.Special;
                }

                return flame;
            }

            case PlayerClass.Medic:
                // ⚠️ The needle gun is on the RIGHT click — the Medigun's left
                // is the heal beam — so a medic with nobody to heal presses SPECIAL
                // to shoot. That is also why the medic is the one class whose SPECIAL
                // is gated by the fire delay.
                // ⚠️ ...but only at something. A medic with neither a patient
                // nor an enemy would otherwise hold the right click on empty air.
                if (!subjectIsAlly)
                    return hasSubject ? PlayerKeys.Special : PlayerKeys.None;

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
                if (!hasSubject)
                    return PlayerKeys.None;

                if (!self.Zoomed)
                    return PlayerKeys.Attack;

                return weapon.Charge
                    >= Weapons.RifleChargeTime
                        * Math.Min(1, distance / BotTargeting.ClassRange(PlayerClass.Sniper))
                    ? PlayerKeys.Attack
                    : PlayerKeys.None;

            default:
                // The scout, the heavy, the engineer and Quote: point and shoot.
                // ⚠️ At something. With no subject these hold the trigger on empty
                // air, which is the one thing the no-subject branch must not do.
                return hasSubject ? PlayerKeys.Attack : PlayerKeys.None;
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
