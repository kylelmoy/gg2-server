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
/// <para>
/// ⚠️ <b>The medic never defends, and it is the only class with a veto rather
/// than a period.</b> <c>BotClassProfile.DefendEvery</c> carries the argument:
/// every other class picks a place to stand and a medic picks a person, so the
/// role and <see cref="BotAlly"/> spend the round pulling a defending medic in
/// two directions and the team loses the heals whichever wins.
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

        // ⛔ Before the modulo, and load-bearing twice over: it is the medic veto
        // (BotClassProfile.DefendEvery says why a medic is a veto and everything
        // else is a period), and `% 0` is a DivideByZeroException rather than a
        // role nobody wanted.
        if (every <= BotConstants.DefendNever)
            return BotRole.Attack;

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

        // Answered without counting, so a class that never defends is not merely
        // never chosen — it is not in a lean group at all. A medic that WERE
        // counted would still come back Attack from the test below, but it would
        // have taken the place its group's next defender was going to come from
        // and the group would end up with none. The `== every` test below cannot
        // match one either, since DefendNever is 0 and no real period is.
        if (every <= BotConstants.DefendNever)
            return BotRole.Attack;

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
    /// ⚠️ <b>And widening THIS does not buy route variety either — measured, and
    /// a dead end.</b> Scaling every bot's offset by 1.5, 2 and 3 moves a team's
    /// match-level route dispersion 0.634 → 0.645 / 0.645 / 0.657: it saturates
    /// almost at once, because the offset moves where a bot ends up
    /// <em>standing</em> and not which corridor it walks there by.
    /// <c>docs/TRAPS-BOTS.md</c>, "Route variety, scored".
    /// </para>
    /// <para>
    /// A bounds check alone (<c>abs(botSpreadX) &lt;= BOT_SPREAD_MAX</c>) is
    /// satisfied even if every bot's offset is silently 0, so the gate here
    /// asserts that the offsets actually differ.
    /// </para>
    /// </remarks>
    public static double SpreadX(int slot) =>
        (((slot % 2) * 2) - 1) * BotConstants.SpreadMax * ((slot * 7 % 11) / 10.0);

    /// <summary>
    /// Which of <paramref name="lanes"/> corridors this bot routes down —
    /// <see cref="BotLanes"/>.
    /// </summary>
    /// <param name="placeInTeam">
    /// <b>This bot's position among its OWN TEAM's bots</b>, in roster order —
    /// not its slot. ⚠️ A slot would be a bug that hides: teams alternate down
    /// the roster, so <c>slot % 4</c> hands red the even lanes and blue the odd
    /// ones, and a team of six would use two of the four corridors while every
    /// count and bound still looked right.
    /// </param>
    /// <param name="lanes">How many the graph was given. At least 1.</param>
    /// <remarks>
    /// <b>Counting rather than rolling, the same as <see cref="Assign"/>, and for
    /// the same reason.</b> A die gives a team of four all-one-lane often enough
    /// to matter, and "the split sometimes just does not happen" is not
    /// debuggable from a screenshot. Straight modulo also guarantees the team is
    /// divided as evenly as its size allows.
    /// <para>
    /// <b>Constant for the bot's life</b>, like the role and the spread: a bot
    /// that re-lanes mid-route reads as one that cannot make up its mind, and the
    /// value of the split is that it looks like team composition from outside.
    /// </para>
    /// </remarks>
    public static int Lane(int placeInTeam, int lanes) =>
        lanes <= 1 ? 0 : ((placeInTeam % lanes) + lanes) % lanes;
}
