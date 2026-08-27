using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// <c>botMinesArmed</c>: has any mine this bot has out got an enemy inside the
/// radius that actually hurts?
/// </summary>
/// <remarks>
/// That is exactly the moment SPECIAL is worth pressing, because the Minegun
/// detonates <b>every</b> mine the player owns at once — so the question is never
/// "is this mine worth it" but "is any of them".
/// <para>
/// ⚠️ <b><see cref="BotConstants.MineTrigger"/> is measured against the mine's
/// <em>damage</em> radius, not its affect radius, and the difference is the whole
/// behaviour.</b> <c>Mine.events/User Event 2.xml</c> damages through
/// <c>blastRadius</c> (40) and not <c>affectRadius</c> (65), and skips a character
/// outright when <c>1 - distance/blastRadius &lt;= 0.25</c> — so anything at a
/// box distance of 30 or more takes <em>nothing at all</em>, and the 45 damage
/// falls off to 11 across the 30px that are left. The reference used to trigger at
/// 60 centre-to-centre, which is outside the blast on every axis: the bot
/// detonated the moment an enemy came near, blew its whole stock and dealt zero.
/// Reported from play as "detonates too early, doesn't damage enemies".
/// </para>
/// <para>
/// ⚠️ <b>A sentry and a generator are asked the same question with a different
/// metric, and that is not a refinement — it is the only reading that works.</b>
/// The reference's script scanned characters and nothing else, so a demoman that
/// had chosen a <em>sentry</em> as its target (<see cref="BotTargeting"/> takes
/// sentry and generator targets, and <see cref="BotClassKeys"/> lobs at them) put
/// its whole stock on that sentry and then never pressed SPECIAL, because no
/// character was ever standing next to them. Reported from play as "shooting
/// grenades at sentries, but not detonating". The blast damages both.
/// </para>
/// <para>
/// The metric for those two is <c>distance_to_object</c> — box to box — because
/// that is the test the engine's own damage block uses on them, with the same
/// quarter-radius skip. It is the same 30 and needs no second constant, but it is
/// read as a <em>box</em> distance there and as a <em>centre</em> distance for a
/// character, which is right: a character is ~10px of box either side of its
/// centre, while a sentry is a wide object whose centre may be 30px from a mine
/// sitting against its base.
/// </para>
/// </remarks>
public static class BotMines
{
    /// <summary>Whether this bot has a mine worth detonating right now.</summary>
    public static bool Armed(in WorldView world, int slot)
    {
        if (!world.IsAlive(slot))
            return false;

        // ⚠️ The CHARACTER's team, which is what the reference reads. A player's
        // and their character's agree everywhere except across the tick a team
        // change lands, and the mines belong to the body that lobbed them.
        var team = world.Character(slot).Team;

        ref readonly var state = ref world.State;

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            var handle = state.LiveProjectileHandle(i);
            ref readonly var mine = ref state.Projectiles[handle.Index];

            // `ownerPlayer == player`: a projectile's owner is a roster slot rather
            // than a character, which is what lets one outlive its shooter — but a
            // mine dies with its owner, so this can only ever be a live demoman's.
            if (mine.Kind != ProjectileKind.Mine || mine.OwnerSlot != slot)
                continue;

            if (AnyCharacterOn(in world, in mine, team)
                || AnySentryOn(in world, in mine, team)
                || AnyGeneratorOn(in world, in mine, team))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>with(Character)</c>: centre to centre, and <c>&lt;=</c>.</summary>
    private static bool AnyCharacterOn(in WorldView world, in ProjectileState mine, Team team)
    {
        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (!world.IsAlive(other))
                continue;

            ref readonly var candidate = ref world.Character(other);

            // ⚠️ A cloaked spy is not a reason to spend the stock. It is the same
            // exclusion the target search makes, and for the same reason: nothing
            // else in the bot layer can see one either, so detonating on one would
            // be the only place a bot acted on information it does not have.
            if (candidate.Team == team || candidate.Cloak || !Gml.Greater(candidate.Hp, 0))
                continue;

            if (Gml.LessOrEqual(
                    Gml.PointDistance(candidate.X, candidate.Y, mine.X, mine.Y),
                    BotConstants.MineTrigger))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>with(Sentry)</c>: box to box, and <c>&lt;</c>.</summary>
    private static bool AnySentryOn(in WorldView world, in ProjectileState mine, Team team)
    {
        var mineBox = mine.Box;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (!world.State.HasSentry[other])
                continue;

            ref readonly var sentry = ref world.State.Sentries[other];

            if (sentry.Team == team || !Gml.Greater(sentry.Hp, 0))
                continue;

            // ⚠️ The REPORTED box, which is the one distance_to_object answers with
            // — a sentry's mask does not animate and its bbox_left carries the
            // two-pixel facing inflation (docs/TRAPS-SIMULATION.md, "the sentry's box").
            if (Gml.Less(Sentries.ReportedBox(sentry).Distance(mineBox), BotConstants.MineTrigger))
                return true;
        }

        return false;
    }

    /// <summary><c>with(Generator)</c>: box to box, and <c>&lt;</c>.</summary>
    /// <remarks>
    /// Both are walked rather than "the enemy's", because that is what the
    /// <c>with</c> does — and on a generator map only one of the two can be the
    /// enemy's anyway, so the team test is what picks it.
    /// </remarks>
    private static bool AnyGeneratorOn(in WorldView world, in ProjectileState mine, Team team)
    {
        var mineBox = mine.Box;

        return GeneratorOn(in world.State.RedGenerator, Team.Red, team, mineBox)
            || GeneratorOn(in world.State.BlueGenerator, Team.Blue, team, mineBox);
    }

    private static bool GeneratorOn(
        in GeneratorState generator, Team generatorTeam, Team team, in Aabb mineBox)
    {
        if (!generator.Exists || generatorTeam == team || !Gml.Greater(generator.Hp, 0))
            return false;

        return Gml.Less(Generators.BodyBox(generator).Distance(mineBox), BotConstants.MineTrigger);
    }
}
