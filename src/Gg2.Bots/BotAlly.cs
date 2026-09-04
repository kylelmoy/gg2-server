using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// <c>botFindAlly</c>: the team-mate a medic bot should have its beam on, or
/// should be following.
/// </summary>
/// <remarks>
/// <b>Scored rather than simply nearest</b>: the hurt come first, and between two
/// equally healthy team-mates the tougher one wins, because a medic's job is to
/// keep the biggest body in the fight alive rather than to top up whoever is
/// closest.
/// <code>
///   score = 1000 * hp/maxHp + distance - maxHp
/// </code>
/// so a heavy at half health (500 + d - 200) outscores a scout at half health
/// (500 + d - 125) by exactly the difference in what they can absorb, and anyone
/// actually wounded outscores anyone who is not by far more than distance can make
/// up.
/// <para>
/// <b>Line of sight is <c>collision_line</c> against <c>Obstacle</c>, NOT
/// <c>collision_line_bulletblocking</c>.</b> A heal beam is stopped by walls the
/// same way a bullet is, but the two predicates are not the same object set —
/// gates, player walls and bullet walls are separate objects — and the beam's is
/// the one that decides whether the heal actually connects. <c>Healing</c> uses the
/// same predicate for the real thing.
/// </para>
/// </remarks>
public static class BotAlly
{
    /// <summary>Nobody, in the roster's own spelling.</summary>
    public const int None = CharacterState.Nobody;

    /// <param name="skipFollowers">
    /// Drop every candidate that is itself a bot of a following class.
    /// </param>
    /// <remarks>
    /// <b><paramref name="skipFollowers"/> exists because two medics who pick
    /// each other deadlock.</b> The follow goal is the ally's own position, so medic
    /// A's goal is where medic B is standing and B's is where A is standing: both
    /// are already there, both report arrived, and neither ever leaves spawn. Seen
    /// live on koth_valley — two red medics parked at x=460 with the rest of their
    /// team at the point.
    /// <para>
    /// <b>It is an ARGUMENT rather than a rule inside this method</b>, because
    /// the <em>beam</em> has no such problem: a medic healing another medic is
    /// correct play, and the combat half passes false so that keeps working. Only
    /// the thing that turns an ally into a <em>destination</em> needs the exclusion.
    /// </para>
    /// <para>
    /// <b>Filtering during the search rather than rejecting the winner
    /// afterwards</b> is what lets the second-best candidate win: a medic beside a
    /// wounded medic and a healthy heavy still follows the heavy, instead of giving
    /// up on following at all.
    /// </para>
    /// <para>
    /// <b>The reference excludes bots of a following class; this excludes the
    /// CLASS.</b> See the comment at the test itself — <c>WorldState</c> holds no
    /// "is this a bot" flag and must not grow one for a branch the tick never reads.
    /// </para>
    /// </remarks>
    public static int Find(in WorldView world, int slot, double range, bool skipFollowers)
    {
        if (!world.IsAlive(slot))
            return None;

        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;

        var best = None;
        var bestScore = double.MaxValue;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (other == slot || !world.IsAlive(other))
                continue;

            ref readonly var mate = ref world.Character(other);

            if (world.Player(other).Team != team || mate.Hp <= 0)
                continue;

            // By CLASS alone, where the reference asks `player.isBot and
            // botClassProfile(class, BOT_CP_FOLLOW)`. WorldState holds no "is this a
            // bot" flag and must not grow one for this: it is not simulation state,
            // nothing in the tick reads it, and docs/BOTAI.md's whole point is that
            // read-only inputs are passed to Tick rather than held by it.
            //
            // What that costs is naming a HUMAN medic as a follower and skipping it
            // too. The reference deliberately does not — a person walks somewhere on
            // their own, so following one is a destination that actually moves and no
            // cycle can form. The degradation is bounded and conservative: a bot
            // medic whose only candidate is a human medic finds nobody, falls through
            // to the objective cascade and walks the objective like everyone else,
            // which is exactly what the reference does with a medic that finds nobody
            // to follow.
            if (skipFollowers && BotClassProfile.Follows(world.Player(other).Class))
                continue;

            var distance = Gml.PointDistance(mate.X, mate.Y, self.X, self.Y);

            if (distance > range)
                continue;

            var maxHp = ClassStats.For(mate.Class).MaxHp;
            var score = (1000 * mate.Hp / maxHp) + distance - maxHp;

            // The reference walks a ds_priority and pops the minimum, testing
            // the sightline only on each pop and stopping at the first that
            // passes. Here the sightline is cheap enough to fold into the scan —
            // it is one collision_line against the map — so this keeps the best
            // candidate that HAS a line rather than the best candidate outright.
            // Same answer, one pass. Ties go to the lower slot, where the
            // reference's ds_priority tie rule is unspecified; nothing observes
            // which, and a stable rule is what keeps a run reproducible.
            if (score >= bestScore)
                continue;

            if (world.Collision.CollisionLineHitsMask(self.X, self.Y, mate.X, mate.Y))
                continue;

            best = other;
            bestScore = score;
        }

        return best;
    }

    /// <summary>Is this still a team-mate worth beaming.</summary>
    /// <remarks>
    /// Re-asked every tick where <see cref="Find"/> runs on a cadence, because an
    /// ally can die, change team or leave between refreshes — and a stale slot
    /// index names whoever the roster compacted into it.
    /// </remarks>
    public static bool StillValid(in WorldView world, int slot, int ally)
    {
        if (ally == None || !world.IsAlive(ally) || !world.IsAlive(slot))
            return false;

        return world.Character(ally).Hp > 0
            && world.Player(ally).Team == world.Player(slot).Team;
    }
}
