using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>What kind of thing a bot has decided to shoot at.</summary>
public enum BotTargetKind
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>An enemy character, named by roster slot.</summary>
    Character,

    /// <summary>An enemy sentry, named by its owner's roster slot.</summary>
    Sentry,

    /// <summary>The enemy generator.</summary>
    Generator,
}

/// <summary>One thing worth shooting at, and where to aim.</summary>
/// <param name="Slot">
/// The roster slot for a character or a sentry; meaningless for a generator.
/// </param>
/// <param name="X">Where to aim. For a sentry or a generator this is the box's
/// <em>centre</em>, not its origin — see <see cref="BotTargeting.Find"/>.</param>
/// <param name="Visible">
/// False for a target chosen through a wall. The bot still has a target — it
/// just cannot see it <em>from here</em> — and the combat layer holds its aim on
/// the last known position rather than refreshing it through the wall.
/// </param>
public readonly record struct BotTarget(
    BotTargetKind Kind, int Slot, double X, double Y, bool Visible)
{
    public static BotTarget None => default;

    public bool Exists => Kind != BotTargetKind.None;
}

/// <summary>
/// Picking what to shoot at — <c>Scripts/Bots/botFindTarget.gml</c>.
/// </summary>
/// <remarks>
/// The same shape as the sentry turret's own target selection — score every
/// candidate, then check line of sight on the best one — with no firing-arc
/// restriction, a per-class radius, and scored by <b>threat</b> rather than by
/// raw distance:
/// <code>
/// threat = distance - 300 × carrying the intel
///                   - 150 × shot me recently
///                   - 120 × Medic - 60 × Engineer
///                   - 100 × already wounded
///                   + range × inside my class's minimum band
/// </code>
/// <para>
/// <b>Every term is in pixels, which is what makes them comparable.</b> Taking
/// the intel carrier means being willing to walk 300px past someone harmless to
/// reach them, and a medic is worth 120px of that because leaving one alive
/// undoes everything else the bot is about to do.
/// </para>
/// </remarks>
public static class BotTargeting
{
    /// <summary>
    /// How far a bot of this class looks for something to shoot —
    /// <c>botClassRange.gml</c>.
    /// </summary>
    /// <remarks>
    /// Transcribed rather than derived from the weapon: it is an <em>attention</em>
    /// radius, not a range, and the numbers are a tuned set.
    /// </remarks>
    public static int ClassRange(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Scout => 220,
        PlayerClass.Soldier => 800,
        PlayerClass.Sniper => 900,
        PlayerClass.Demoman => 500,
        PlayerClass.Medic => 300,
        PlayerClass.Engineer => 400,
        PlayerClass.Heavy => 500,
        PlayerClass.Spy => 300,
        PlayerClass.Pyro => 200,
        PlayerClass.Quote => 300,
        _ => 375,
    };

    /// <summary>
    /// Has this bot been hit recently by something that was not itself.
    /// </summary>
    /// <remarks>
    /// <b>Read off the simulation's own fields, so nothing has to be remembered
    /// per bot.</b> <c>TimeUnscathed</c> is reset by every damage path and counted
    /// up in the character's Step, and <c>LastDamageDealer</c> is a roster slot
    /// that <c>WorldState.RemoveSlot</c> renumbers — which is exactly what a
    /// bot-side timer would get wrong after a disconnect.
    /// <para>
    /// <b>A rocket jump is not being under fire.</b> Self-damage names the jumper
    /// as its own dealer, so the <c>!= slot</c> test is what keeps a soldier from
    /// reacting to its own launch. Afterburn names whoever lit the fire, which IS
    /// being under fire and is meant to count.
    /// </para>
    /// </remarks>
    public static bool UnderFire(in WorldView world, int slot)
    {
        if (!world.IsAlive(slot))
            return false;

        ref readonly var self = ref world.Character(slot);

        return self.TimeUnscathed <= BotConstants.UnderFireTicks
            && self.LastDamageDealer != CharacterState.Nobody
            && self.LastDamageDealer != slot;
    }

    /// <summary>
    /// How far this bot is looking for something to shoot <em>right now</em>.
    /// </summary>
    /// <remarks>
    /// <see cref="ClassRange"/>, widened to <see cref="BotConstants.RetaliateRange"/>
    /// while something is shooting at it. The class radius is an attention budget
    /// — how far a bot scans on a quiet tick — and a bot being hit is not scanning,
    /// it is being told. Without this a pyro (200px) or a scout (220px) shot from
    /// 400px has no candidate at all: <see cref="Find"/> drops everything past the
    /// radius before the attacker bonus is ever applied, so the whole
    /// <c>shot me recently</c> term is unreachable at exactly the distance it is
    /// for.
    /// <para>
    /// <b>The whole radius widens, not just the attacker.</b> Ranking is by threat
    /// in pixels and the attacker already carries
    /// <see cref="BotConstants.AttackerBonus"/>, so a bot that is being shot picks
    /// whoever is worst for it — which may well be a nearer body than the shooter.
    /// Widening one slot instead would have made the answer depend on the order the
    /// roster happens to be in.
    /// </para>
    /// <para>
    /// It widens the SEARCH and the keep test in <c>BotCombat</c>, and nothing
    /// else. The engage hold, the class's standoff band and every fire gate stay
    /// on <see cref="ClassRange"/> — a bot that has turned round to look at a
    /// sniper 500px away must not also stop walking its route, and a pyro must not
    /// flame at air. <c>docs/TRAPS-BOTS.md</c>, "standing still under fire".
    /// </para>
    /// </remarks>
    public static double AttentionRange(in WorldView world, int slot)
    {
        var range = (double)ClassRange(world.Character(slot).Class);

        return UnderFire(in world, slot) ? Math.Max(range, BotConstants.RetaliateRange) : range;
    }

    /// <summary>
    /// How close is too close for this class — <c>botClassMinBand.gml</c>.
    /// </summary>
    /// <remarks>
    /// The splash classes keep their distance because their own weapon would
    /// kill them; the pyro, the spy and the medic have <b>no</b> minimum at all,
    /// because point blank is where all three of them work. Everyone else gets
    /// <c>BOT_MIN_ENGAGE</c>, which is about not standing inside somebody.
    /// </remarks>
    public static int ClassMinBand(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Soldier => BotConstants.SplashSafe,
        PlayerClass.Demoman => BotConstants.SplashSafe,
        PlayerClass.Pyro => 0,
        PlayerClass.Spy => 0,
        PlayerClass.Medic => 0,
        _ => BotConstants.MinEngage,
    };

    /// <summary>
    /// Is any live, uncloaked enemy within this radius —
    /// <c>botEnemyWithin.gml</c>.
    /// </summary>
    public static bool EnemyWithin(in WorldView world, int slot, double range)
    {
        if (!world.IsAlive(slot))
            return false;

        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (other == slot || !world.State.Alive[other])
                continue;

            ref readonly var candidate = ref world.Character(other);

            if (world.Player(other).Team == team || candidate.Cloak || candidate.Hp <= 0)
                continue;

            if (Gml.PointDistance(candidate.X, candidate.Y, self.X, self.Y) <= range)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The signed difference between two angles, in <c>[-180, 180)</c> —
    /// <c>botAngleDelta.gml</c>.
    /// </summary>
    /// <remarks>
    /// GML's <c>mod</c> keeps the sign of the dividend, so the <c>+ 540</c> is
    /// what makes this work for a negative first difference rather than
    /// decoration. C#'s <c>%</c> behaves the same way, which is why the
    /// transcription is literal.
    /// <para>
    /// Exactly opposite answers <b>-180, not +180</b>: the wrap takes 360 back
    /// to 0 before the subtraction, so the half turn lands on the low end. That
    /// decides which way a bot turns to face directly behind it.
    /// </para>
    /// </remarks>
    public static double AngleDelta(double a, double b)
    {
        var d = (a - b) % 360;
        return ((d + 540) % 360) - 180;
    }

    /// <summary>
    /// Picks the thing this bot should shoot at, or nothing.
    /// </summary>
    /// <remarks>
    /// <b>Line of sight is checked when a candidate is <em>popped</em>, not
    /// when it is pushed</b>, so the collision line runs once for the best
    /// candidate instead of once per enemy on the map.
    /// <para>
    /// <b>Losing every candidate to line of sight does not mean there is
    /// nothing to fight</b>, and treating it that way is the "bots walk straight
    /// past enemies" report. A bot whose target is behind a crate 80px away has a
    /// target; it just cannot see it <em>from here</em>. So a blocked search
    /// falls back to the nearest candidate inside <c>BOT_BLIND_RANGE</c> and
    /// returns it with <see cref="BotTarget.Visible"/> false — the combat layer
    /// holds its aim on the last known position and engages the moment the
    /// sightline opens, instead of re-paying the whole acquire chain then. The
    /// radius is short on purpose; a blind target across the map is a bot aiming
    /// at scenery.
    /// </para>
    /// <para>
    /// <b>Threat is floored at 1</b> rather than allowed to go negative — not
    /// because anything downstream cares, but because a negative one reads like a
    /// bug to whoever prints it.
    /// </para>
    /// </remarks>
    public static BotTarget Find(in WorldView world, int slot, double range)
    {
        if (!world.IsAlive(slot))
            return BotTarget.None;

        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;
        var minBand = ClassMinBand(self.Class);

        // Scored candidates, best (lowest threat) first. A roster is 64 and a map
        // has two generators, so the list is short and an insertion sort beats a
        // heap — and unlike a heap it is stable, which is what keeps ties
        // resolving by slot rather than by whatever the heap happened to do.
        Span<int> order = stackalloc int[WorldState.MaxSlots * 2 + 1];
        Span<double> threats = stackalloc double[WorldState.MaxSlots * 2 + 1];
        var count = 0;

        // Characters. A cloaked spy is not a candidate at all — not merely a
        // worse one.
        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (other == slot || !world.State.Alive[other])
                continue;

            ref readonly var candidate = ref world.Character(other);

            if (candidate.Cloak || world.Player(other).Team == team || candidate.Hp <= 0)
                continue;

            var distance = Gml.PointDistance(candidate.X, candidate.Y, self.X, self.Y);
            if (distance > range)
                continue;

            var threat = distance;

            if (candidate.CarryingIntel)
                threat -= 300;

            // Whoever is currently shooting *this* bot is a live threat rather
            // than a scoring opportunity, and answering them is what a player
            // does before anything else. LastDamageDealer is a roster slot and is
            // cleared on its own timer, so this decays without a timer here.
            if (other == self.LastDamageDealer)
                threat -= BotConstants.AttackerBonus;

            if (candidate.Class == PlayerClass.Medic)
                threat -= 120;
            else if (candidate.Class == PlayerClass.Engineer)
                threat -= 60;

            // Finishing the wounded is worth less than either but more than
            // nothing: a target already low is the cheapest kill available.
            if (candidate.Hp <= ClassStats.For(candidate.Class).MaxHp * 0.4)
                threat -= 100;

            // The lock-breaker: a *lock* problem, not a range one. A soldier's band
            // is 110-800px and it fires throughout — but with threat as pure
            // distance it always locks whoever is nearest, and the combat layer
            // does not look for a new target while it holds a valid one. An enemy
            // that closes inside BOT_SPLASH_SAFE therefore pins a soldier
            // permanently: it keeps the lock, refuses to fire because a rocket at
            // that range kills the shooter, and a second enemy at a perfectly
            // shootable 300px is never considered. Ranking anyone inside the band a
            // full attention radius worse breaks the lock without adding a rule.
            if (distance < minBand)
                threat += range;

            Insert(order, threats, ref count, other, Math.Max(1, threat));
        }

        // Sentries: a turret with 100hp that shoots back, and without a term for
        // it in the model a bot walks into one, dies, and respawns to do it again.
        // It ranks a little ahead of a player at the same distance, because a
        // player can be walked away from and a sentry covers its ground until
        // somebody removes it.
        for (var owner = 0; owner < WorldState.MaxSlots; owner++)
        {
            if (!world.State.HasSentry[owner])
                continue;

            ref readonly var sentry = ref world.State.Sentries[owner];

            if (sentry.Team == team || sentry.Hp <= 0)
                continue;

            var distance = Gml.PointDistance(sentry.X, sentry.Y, self.X, self.Y);
            if (distance > range)
                continue;

            var threat = distance - BotConstants.SentryBonus;

            if (distance < minBand)
                threat += range;

            Insert(order, threats, ref count, WorldState.MaxSlots + owner, Math.Max(1, threat));
        }

        // The enemy generator, at BOT_GEN_THREAT — far past any distance term, so
        // it is only ever chosen when nothing live is available. A bot that
        // keeps shooting a wall while somebody kills it is worse than one that
        // never shoots the wall.
        ref readonly var generator = ref team == Team.Red
            ? ref world.State.BlueGenerator
            : ref world.State.RedGenerator;

        if (generator.Exists && generator.Hp > 0)
        {
            var box = Generators.BodyBox(generator);
            var centreX = (box.Left + box.Right) / 2.0;
            var centreY = (box.Top + box.Bottom) / 2.0;

            var distance = Gml.PointDistance(centreX, centreY, self.X, self.Y);

            if (distance <= range)
            {
                Insert(
                    order, threats, ref count,
                    WorldState.MaxSlots * 2, BotConstants.GenThreat + distance);
            }
        }

        var filter = world.State.BulletFilter;
        var blind = BotTarget.None;
        var blindDistance = 0.0;

        for (var i = 0; i < count; i++)
        {
            var candidate = Resolve(in world, order[i], team);

            if (!world.Collision.CollisionLineBlocksBullets(
                    self.X, self.Y, candidate.X, candidate.Y, filter))
            {
                return candidate with { Visible = true };
            }

            var distance = Gml.PointDistance(candidate.X, candidate.Y, self.X, self.Y);

            if (distance > BotConstants.BlindRange)
                continue;

            if (!blind.Exists || distance < blindDistance)
            {
                blind = candidate;
                blindDistance = distance;
            }
        }

        return blind;
    }

    /// <summary>Turns an encoded candidate index back into a target.</summary>
    /// <remarks>
    /// A sentry and a generator are aimed at by the <b>centre of the box</b>
    /// and a character by its origin, which is its chest. The reference makes the
    /// same distinction with <c>botIsCharacter</c>, and getting it wrong aims a
    /// bot at a sentry's feet.
    /// </remarks>
    private static BotTarget Resolve(in WorldView world, int encoded, Team team)
    {
        if (encoded < WorldState.MaxSlots)
        {
            ref readonly var character = ref world.Character(encoded);
            return new BotTarget(BotTargetKind.Character, encoded, character.X, character.Y, false);
        }

        if (encoded < WorldState.MaxSlots * 2)
        {
            var owner = encoded - WorldState.MaxSlots;
            var box = Sentries.BodyBox(world.State.Sentries[owner]);

            return new BotTarget(
                BotTargetKind.Sentry, owner,
                (box.Left + box.Right) / 2.0, (box.Top + box.Bottom) / 2.0, false);
        }

        ref readonly var generator = ref team == Team.Red
            ? ref world.State.BlueGenerator
            : ref world.State.RedGenerator;

        var body = Generators.BodyBox(generator);

        return new BotTarget(
            BotTargetKind.Generator, -1,
            (body.Left + body.Right) / 2.0, (body.Top + body.Bottom) / 2.0, false);
    }

    /// <summary>Insertion into the sorted prefix, lowest threat first.</summary>
    /// <remarks>
    /// Strictly <c>&lt;</c>, so an equal threat leaves the earlier candidate in
    /// front. The reference uses a <c>ds_priority</c> whose tie rule is
    /// unspecified; a defined one is what makes a bot's choice reproducible,
    /// which docs/BOTAI.md needs and the reference does not.
    /// </remarks>
    private static void Insert(Span<int> order, Span<double> threats, ref int count, int value, double threat)
    {
        var at = count;

        while (at > 0 && threat < threats[at - 1])
            at--;

        for (var shift = count; shift > at; shift--)
        {
            threats[shift] = threats[shift - 1];
            order[shift] = order[shift - 1];
        }

        threats[at] = threat;
        order[at] = value;
        count++;
    }
}
