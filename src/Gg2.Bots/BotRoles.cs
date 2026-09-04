using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>Whether a bot's job is to take the objective or to keep it.</summary>
public enum BotRole
{
    Attack = BotConstants.RoleAttack,
    Defend = BotConstants.RoleDefend,
}

/// <summary>
/// <c>botRoleAssign</c>: which half of the team this bot is on, and how far to
/// one side of the objective it stands.
/// </summary>
/// <remarks>
/// Nothing in the bot layer distinguished attacking from defending before this,
/// which is why every mode read the same way — the entire team running at the
/// enemy objective in single file and nobody ever behind them. Generator is where
/// the two jobs are most explicitly separate (each team both defends its own and
/// attacks the other's), but CTF wants it just as much: a flag nobody is guarding
/// is a flag that leaves and does not come back.
/// <para>
/// <b>Assigned once, when the bot joins or is re-teamed, never re-decided per
/// tick.</b> A role that changes while a bot is walking somewhere is
/// indistinguishable from a bot that cannot make up its mind, and the whole value
/// of the split is that it reads as team composition from the outside.
/// </para>
/// <para>
/// <b>Counting rather than rolling a die is deliberate.</b> With three bots on
/// a team a 1-in-3 roll gives all-attack about 30% of the time, and "the split
/// sometimes just does not happen" is not a thing anyone can debug from a
/// screenshot.
/// </para>
/// </remarks>
public static class BotRoles
{
    /// <summary>
    /// The role for a bot whose lean-group peers ahead of it on the roster are
    /// <paramref name="placeInGroup"/> many.
    /// </summary>
    /// <param name="playerClass">Whose period decides the rate.</param>
    /// <param name="placeInGroup">
    /// <b>This bot's POSITION among its team's bots that share its period</b> —
    /// not how many team-mates it has, and not its position on the whole team.
    /// <para>
    /// Both of those are wrong in ways worth naming. A count of team-mates is the
    /// same number for every bot on the team, so on a team of three it would make
    /// all three defenders at once and on a team of four none. And counting the
    /// whole team makes the period meaningless in a mixed one: two engineers
    /// separated by four scouts sit at positions 0 and 5 and neither lands on a
    /// multiple of 2. Sharing a period is what makes a lean group — engineer and
    /// heavy count together as one defensive pool, scout and spy as one offensive
    /// pool, everyone else as the third.
    /// </para>
    /// <para>
    /// A position also makes the first bot on a team an attacker every time, which
    /// is the right default when there is only one, and it is stable: a bot's
    /// position only changes when someone ahead of it leaves.
    /// </para>
    /// </param>
    public static BotRole Assign(PlayerClass playerClass, int placeInGroup)
    {
        var every = BotClassProfile.DefendEvery(playerClass);

        return (placeInGroup + 1) % every == 0 ? BotRole.Defend : BotRole.Attack;
    }

    /// <summary>
    /// Counts <paramref name="mine"/>'s place among the earlier bots on its team
    /// that share its period, and returns its role.
    /// </summary>
    /// <param name="earlier">
    /// The classes of this team's bots that come before this one on the roster, in
    /// roster order. Anything not on this bot's team, and anything that is not a
    /// bot, is the caller's to leave out.
    /// </param>
    public static BotRole Assign(PlayerClass mine, ReadOnlySpan<PlayerClass> earlier)
    {
        var every = BotClassProfile.DefendEvery(mine);
        var place = 0;

        foreach (var other in earlier)
        {
            if (BotClassProfile.DefendEvery(other) == every)
                place++;
        }

        return Assign(mine, place);
    }

    /// <summary>
    /// The per-bot lateral goal offset, so a team heading for one objective
    /// arrives spread along it rather than stacked on one column.
    /// </summary>
    /// <remarks>
    /// <b>It must be constant for the bot's whole life.</b> A spread that
    /// changes re-issues the goal every time it is read, which fights the re-plan
    /// timer the follower was built around.
    /// <para>
    /// <c>((id mod 2) * 2 - 1) * BOT_SPREAD_MAX * (((id * 7) mod 11) / 10)</c>.
    /// Consecutive bots differ by one, so <c>mod 2</c> alternates the side and the
    /// second term walks the magnitude around an 11-cycle — enough spread for any
    /// team size a GG2 server runs, and exactly reproducible from the bot's
    /// identity. In the reference the identity is a GM8 instance id and here it
    /// is the roster slot; the arithmetic is the same and the sequence differs,
    /// which is free (nothing observes which bot took which offset).
    /// </para>
    /// <para>
    /// <b>Do not derive a per-bot route seed the same way.</b> A cost jitter fed
    /// from such a seed produces no route variety: per-edge noise averages out
    /// over a long path and leaves the ordering between routes untouched
    /// (measured on ctf_truefort — all eight bots got the identical route). Route
    /// variety comes from a shared occupancy penalty instead.
    /// </para>
    /// <para>
    /// A bounds check alone (<c>abs(botSpreadX) &lt;= BOT_SPREAD_MAX</c>) is
    /// satisfied even if every bot's offset is silently 0, so the gate here
    /// asserts that the offsets actually differ.
    /// </para>
    /// </remarks>
    public static double SpreadX(int slot) =>
        (((slot % 2) * 2) - 1) * BotConstants.SpreadMax * ((slot * 7 % 11) / 10.0);
}
