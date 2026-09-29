using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// <c>botFindAlly</c>: the team-mate a medic bot should have its beam on, or
/// should be following.
/// </summary>
/// <remarks>
/// <b>Scored rather than simply nearest</b>, and the score has a bucket in front
/// of it:
/// <code>
///   score = 1000 * hp/maxHp + distance - maxHp
///           - (the patient is a person ? HumanPatient : 0)
///           + (the patient is at full health ? FullHealth : 0)
/// </code>
/// Within a bucket the tougher body wins between two equally hurt team-mates,
/// because a medic's job is to keep the biggest thing in the fight alive rather
/// than to top up whoever is closest: a heavy at half health (500 + d - 200)
/// outscores a scout at half health (500 + d - 125) by exactly the difference in
/// what they can absorb.
/// <para>
/// ⚠️ <b><see cref="FullHealth"/> is a PARTITION and the <c>- maxHp</c> term is
/// why it has to be.</b> That term is worth 200 points across the class table and
/// the health term is worth 1000 across a whole bar, so a full-health heavy
/// (1000 - 300) used to outscore a scout anywhere above 82.5% (1000h - 125) — a
/// medic would top up an untouched heavy while a team-mate bled next to it.
/// Anyone hurt now outranks anyone who is not, whatever either of them is and
/// however far away.
/// </para>
/// <para>
/// ⛔ <b>A full-health team-mate is still a candidate, and beaming one is not
/// waste.</b> It charges the uber at 1.75 a tick against a hurt patient's 2.5
/// (<c>Healing</c>, <c>UberChargeHealthy</c>) — and during setup at the FULL 2.5,
/// which is the only way to bank any charge before the gates open. So the rule is
/// "never instead of somebody hurt", not "never".
/// </para>
/// <para>
/// <b><see cref="HumanPatient"/> is a bonus and not a second partition</b>, so a
/// badly hurt bot still outranks a lightly grazed person. It is worth 30% of a
/// health bar: a person at 60% is preferred to a bot at 30%, and a bot at 10% is
/// still preferred to a person at 50%. <c>PlayerState.IsBot</c> is bot-readable
/// by design and defaults to false meaning human, so a slot nobody set is treated
/// as a person — which is the safe direction here as it is in
/// <c>Handover.ShouldEngage</c>.
/// </para>
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

    /// <summary>
    /// What a full-health team-mate is ranked behind everyone hurt by.
    /// </summary>
    /// <remarks>
    /// <b>A bucket, not a weight, and it is sized to be one.</b> The rest of the
    /// expression spans about 1,800 points at the very widest — 1,000 of health,
    /// <see cref="BotConstants.HealRange"/> of distance, 300 of class and 300 of
    /// <see cref="HumanPatient"/> — so anything above that makes the partition
    /// unbridgeable and the exact figure stops mattering. Tuning it is a mistake:
    /// if the intent ever becomes "prefer, but not absolutely", this should become
    /// a term and be sized against the others, not shrunk.
    /// </remarks>
    public const double FullHealth = 4000;

    /// <summary>
    /// What a person is worth over a bot at the same health, in score points.
    /// </summary>
    /// <remarks>
    /// 30% of a health bar, since the health term is 1,000 across a whole one.
    /// <b>Deliberately smaller than the bar</b>: a bot about to die still outranks
    /// a person with a scratch, which is what keeps this a preference rather than
    /// a rule that gets team-mates killed.
    /// </remarks>
    public const double HumanPatient = 300;

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
    /// CLASS.</b> ⚠️ Not because the flag is missing — <c>PlayerState.IsBot</c>
    /// exists and <see cref="Find"/> reads it for <see cref="HumanPatient"/> a few
    /// lines below — but because it is <em>only set where a server sets it</em>.
    /// <c>ScenarioWorld</c> marks nobody, so every bot in a scenario and every
    /// duel reads as a person, and an <c>IsBot</c> test here would stop excluding
    /// anything and put the two-medic deadlock straight back into the harness the
    /// exclusion was built for. A class test answers the same in every world.
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
            // botClassProfile(class, BOT_CP_FOLLOW)`. ⚠️ `PlayerState.IsBot` is
            // read below and could spell the reference's test exactly — do not.
            // It is only populated where a server populates it: ScenarioWorld
            // marks nobody, so in every scenario and every duel the test would be
            // vacuously false, exclude nothing, and restore the deadlock this
            // argument exists to prevent.
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

            // ⛔ Never a cloaked spy, and the reason is on the CLIENT rather than
            // in the heal. `Medigun.events/Draw.xml` draws the beam as a plain
            // draw_line_width_color straight to healTarget.object.x/y, gated on
            // nothing but the medic's own taunt — not on the patient's cloak and
            // not on `invisible`. So beaming a cloaked team-mate paints a 5px
            // line at whatever the spy is creeping up on, and hands the enemy the
            // one thing the class is built out of. The heal itself works fine
            // (Healing has no cloak test either), which is exactly why nothing
            // else stops this.
            if (mate.Cloak)
                continue;

            var distance = Gml.PointDistance(mate.X, mate.Y, self.X, self.Y);

            if (distance > range)
                continue;

            var maxHp = ClassStats.For(mate.Class).MaxHp;
            var score = (1000 * mate.Hp / maxHp) + distance - maxHp;

            // PlayerState.IsBot, which bot code may read and MatchSimulation may
            // not. Default false means human, so an unset slot is treated as a
            // person — the safe direction, the same way Handover.ShouldEngage
            // takes it.
            //
            // ⚠️ That default also means this term DOES NOTHING in a world that
            // sets no flags: ScenarioWorld marks nobody, so every candidate there
            // is a "person" and the bonus cancels out. HeadlessMatch marks every
            // bot, so it cancels out there too. It discriminates only on a live
            // server with both in it, which is the only place it is for.
            if (!world.Player(other).IsBot)
                score -= HumanPatient;

            // The partition. Above the sightline test rather than after it, so a
            // full-health team-mate never displaces a wounded one that the scan
            // has not reached yet.
            if (!Gml.Less(mate.Hp, maxHp))
                score += FullHealth;

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

        // The cloak is re-asked here and not only in Find, because a spy that
        // cloaks mid-beam has to lose it on that tick rather than on the next
        // cadence — up to BOT_TARGET_PERIOD later, which is half a second of a
        // line drawn at a spy that thinks it has just disappeared.
        return world.Character(ally).Hp > 0
            && !world.Character(ally).Cloak
            && world.Player(ally).Team == world.Player(slot).Team;
    }
}
