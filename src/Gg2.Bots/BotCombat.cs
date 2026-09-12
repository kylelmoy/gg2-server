using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// One tick of a bot's combat brain — <c>Scripts/Bots/botCombatUpdate.gml</c>.
/// </summary>
/// <remarks>
/// Returns the key bits it wants pressed. <b>The caller ORs them with the
/// navigation half's</b>, and the two cannot conflict because this one owns
/// ATTACK and SPECIAL and <see cref="PathFollower"/> owns LEFT/RIGHT/JUMP/DOWN.
/// <para>
/// <b>The see → acquire → aim-settled → fire chain the whole difficulty model is
/// built around.</b> Four separate gates, all read off <see cref="BotSkill"/>:
/// </para>
/// <list type="number">
/// <item><b>see</b> — a target is picked on a staggered cadence.</item>
/// <item><b>acquire</b> — the bot ignores it entirely for
/// <see cref="BotSkill.AcquireTicks"/>. It does not even turn towards it,
/// which is what makes a weak bot look <em>slow</em> rather than blind.</item>
/// <item><b>aim</b> — the desired direction is recomputed every
/// <see cref="BotSkill.AimInterval"/> ticks from a snapshot that itself only
/// refreshes every <see cref="BotSkill.PerceiveTicks"/>, and the actual aim slews
/// at <see cref="BotSkill.TurnRate"/> degrees a tick. Tracking lag, overshoot and
/// settling all come out of those three numbers; there is no filter and no
/// smoothing.</item>
/// <item><b>fire</b> — ATTACK is withheld until
/// <see cref="BotSkill.FireDelayTicks"/> after acquisition <em>and</em> the slew
/// has settled, and is dropped for an aim interval whenever the hold-fire roll
/// comes up.</item>
/// </list>
/// <para>
/// <b>SPECIAL is gated on acquisition but not on the fire delay</b>, because
/// the special-key behaviours are reactive — a pyro's airblast, a demoman
/// detonating under somebody's feet — and a reaction that arrives a second and a
/// half late is not a reaction. The one exception is a medic, whose needle gun
/// <em>is</em> its right click.
/// </para>
/// <para>
/// <b>Aim error is uniform, not Gaussian.</b> Quake III's <c>crandom()</c> is
/// uniform and so is every error term built on it. Two multipliers ride on top:
/// the moving-target scale, and Quake III's close-range penalty, which makes
/// <em>every</em> tier worse at point-blank range. That one is an
/// anti-frustration measure rather than a difficulty knob — without it bots are
/// unbeatable in a brawl.
/// </para>
/// <para>
/// <b>Not ported yet, each a single branch and each conservative when absent:</b>
/// the medic's ally lookup (<c>botFindAlly</c>) so a medic shoots needles rather
/// than healing; the heavy's suppression fire (<c>botSuppressSpot</c>) so it
/// holds its aim on the last known position instead of on a doorway; and the
/// four helper-gated refinements in <see cref="BotClassKeys"/>.
/// </para>
/// </remarks>
public static class BotCombat
{
    /// <summary>
    /// Advances one bot's combat state by a tick and returns ATTACK / SPECIAL.
    /// </summary>
    /// <param name="random">
    /// The match's own stream. <see cref="WorldState.Random"/> and not a
    /// private one: docs/BOTAI.md item 2 wants a forked world to fork every
    /// stream with it, and <c>randomize()</c> is deliberately not transcribed
    /// anywhere in this port.
    /// </param>
    /// <param name="aim">
    /// Where the bot ends up pointing, ready for a <see cref="PlayerInput"/>.
    /// </param>
    public static PlayerKeys Step(
        BotCombatState state,
        in WorldView world,
        int slot,
        int tick,
        in BotSkill skill,
        ref GmlRandom random,
        NavGraph? graph,
        out double aim,
        out double aimDistance)
    {
        ArgumentNullException.ThrowIfNull(state);

        aim = state.AimDir;
        aimDistance = 0;

        if (!world.IsAlive(slot))
        {
            state.AttackKeys = PlayerKeys.None;
            return PlayerKeys.None;
        }

        ref readonly var self = ref world.Character(slot);

        // Widened while somebody is shooting at this bot — see
        // BotTargeting.AttentionRange. Both the search and the keep test read the
        // same number, so a target acquired because the bot was hit is dropped by
        // the ordinary rule the moment the window closes, with no second piece of
        // state to keep in step.
        var range = BotTargeting.AttentionRange(in world, slot);

        KeepOrDropTarget(state, in world, slot, tick, range, in skill);
        LookForOne(state, in world, slot, tick, range, ref random);

        // --- the medic exception: a hurt team-mate outranks an enemy ---------
        //
        // The ally REPLACES the subject rather than adding to it, and
        // everything downstream then treats it as the thing being aimed at. That is
        // what makes a medic stop shooting and start healing: BotClassKeys' medic
        // case reads the flag and presses ATTACK (the beam) instead of SPECIAL (the
        // needles).
        var subjectIsAlly = false;

        if (BotClassProfile.Heals(self.Class))
        {
            if ((tick + state.Stagger) % BotConstants.TargetPeriod == 0)
            {
                // false: a medic beaming another medic is correct play. Only the
                // follow GOAL in BotObjective has to exclude them, and it says why.
                state.Ally = BotAlly.Find(in world, slot, BotConstants.HealRange, skipFollowers: false);
            }

            if (BotAlly.StillValid(in world, slot, state.Ally))
                subjectIsAlly = true;
            else
                state.Ally = BotAlly.None;
        }

        // The medic exception is decided ABOVE this, because an ally is a
        // subject in its own right: a medic with a patient and no enemy anywhere
        // still beams, and deciding it after the no-target return would make a medic
        // heal only while something was shooting at it.
        if (!state.Target.Exists)
        {
            // With nothing to shoot at the aim follows the walk, so a bot
            // crossing a map faces the way it is going. The threshold is
            // BOT_REST_SPEED — the same "is it really moving" floor GG2's own
            // ice-skating snap uses — so a bot standing still keeps whatever it
            // was last pointing at rather than snapping to the right.
            if (!subjectIsAlly)
            {
                if (self.HSpeed > BotConstants.RestSpeed)
                    state.AimWant = 0;
                else if (self.HSpeed < -BotConstants.RestSpeed)
                    state.AimWant = 180;

                Slew(state, in skill);
                aim = state.AimDir;

                // The class policy STILL RUNS with nothing to aim at —
                // botCombatUpdate's own no-subject branch does the same, because
                // "nothing to aim at is a state a class can still have an opinion
                // about: a spy re-cloaks, an engineer builds, a heavy eats". Only
                // the aiming is skipped.
                //
                // The case that made it matter here is the pyro's AIRBLAST: a
                // reflect is a reaction to something already in the air and has
                // nothing to do with having a target. Returning None here left a
                // pyro with an empty screen unable to deflect a rocket at all.
                // Every branch that would otherwise fire at nothing carries its own
                // subject test — see BotClassKeys.
                var idle = BotClassKeys.For(state, in world, slot, 0, tick, graph: graph);

                state.AttackKeys = idle;
                return idle;
            }
        }

        var target = subjectIsAlly ? AllyAsSubject(in world, state.Ally) : state.Target;
        var distance = Gml.PointDistance(self.X, self.Y, target.X, target.Y);
        var sinceSeen = tick - state.TargetAt;

        // The acquire gate returns BEFORE the slew, so the aim does not even
        // begin to move. That is the difference between a slow bot and a blind
        // one, and folding it in after the slew would make every tier turn at the
        // same speed and only differ in when it shot.
        // An ally is NOT acquired. Every difficulty gate on this path —
        // the acquire hold, the fire delay, the aim settle, the hold-fire roll — is
        // about shooting a person who is trying not to be shot. A medic that made its
        // own team-mate wait a second and a half before pointing the beam at them
        // would read as broken rather than as bad, and the reference bypasses all
        // four for exactly that reason.
        if (!subjectIsAlly && sinceSeen < skill.AcquireTicks)
        {
            aim = state.AimDir;
            state.AttackKeys = PlayerKeys.None;
            return PlayerKeys.None;
        }

        if (subjectIsAlly)
        {
            // Straight at them, with NO lead solve. The beam has no travel
            // time — botAimSolve exists to put a projectile where a target will be —
            // and leading a team-mate points the beam at empty air beside them.
            state.AimX = target.X;
            state.AimY = target.Y;
            state.AimWant = Gml.PointDirection(self.X, self.Y, target.X, target.Y);
        }
        else
        {
            Perceive(state, in world, tick, in skill);
            SolveAim(state, in world, slot, tick, sinceSeen, distance, in skill, ref random, graph, subjectIsAlly);
        }
        Slew(state, in skill);

        aim = state.AimDir;
        aimDistance = Math.Min(510, Gml.PointDistance(self.X, self.Y, state.AimX, state.AimY));

        var settled = Math.Abs(BotTargeting.AngleDelta(state.AimWant, state.AimDir))
            <= BotConstants.AimSettleDeg * BotClassProfile.AimSettle(self.Class);

        var keys = BotClassKeys.For(state, in world, slot, distance, tick, subjectIsAlly, graph);

        var canFire = sinceSeen >= skill.AcquireTicks + skill.FireDelayTicks;

        // `!subjectIsAlly &&` on the whole suppression, not on each clause.
        // A beam is not a shot: it costs nothing to be wrong about, it cannot miss,
        // and holding it back on a settle tolerance is a medic that stops healing
        // every time its patient walks round a corner.
        if (!subjectIsAlly && (!canFire || !settled || state.HoldFire))
        {
            keys &= ~PlayerKeys.Attack;

            // Only for a class whose SPECIAL is part of firing. Everyone
            // else's is a reaction and is not gated on the fire delay at all.
            if (BotClassProfile.SpecialIsFire(self.Class))
                keys &= ~PlayerKeys.Special;
        }

        state.AttackKeys = keys;
        return keys;
    }

    /// <summary>An ally, in the shape the aim and the keys already understand.</summary>
    /// <remarks>
    /// <see cref="BotTarget.Visible"/> is true by construction — the ally
    /// search would not have returned a slot with no line to it. Leaving it false
    /// would make the blind-fallback machinery downstream treat a team-mate standing
    /// in the open as somebody remembered through a wall.
    /// </remarks>
    private static BotTarget AllyAsSubject(in WorldView world, int ally)
    {
        ref readonly var mate = ref world.Character(ally);

        return new BotTarget(BotTargetKind.Character, ally, mate.X, mate.Y, Visible: true);
    }

    /// <summary>
    /// Keeps the current target if it is still worth shooting at, or drops it.
    /// </summary>
    /// <remarks>
    /// <b>The line-of-sight test is on the perceive cadence, not every
    /// tick</b>, and a broken sightline does not drop the target for thirty
    /// ticks. That memory window is what lets a bot hold its aim on a doorway
    /// somebody has just stepped behind and engage the moment they step back out.
    /// </remarks>
    private static void KeepOrDropTarget(
        BotCombatState state, in WorldView world, int slot, int tick, double range, in BotSkill skill)
    {
        if (!state.Target.Exists)
            return;

        // A potshot target is outside the ordinary radius by construction, so
        // it is kept inside the same widened band that let it be picked.
        var keepRange = state.Potshot ? range * BotConstants.PotshotMult : range;

        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;

        if (!StillWorthShootingAt(state, in world, team, out var x, out var y))
        {
            state.DropTarget();
            return;
        }

        // Measured to the same point the search measured to when it picked
        // this target — the centre of a map object's body, the chest of a
        // character. Mixing the two would let a generator be acquired at its
        // centre and dropped on its origin the next tick, over and over, on any
        // map where the two are far apart.
        state.Target = state.Target with { X = x, Y = y };

        if (Gml.PointDistance(self.X, self.Y, x, y) > keepRange)
        {
            state.DropTarget();
            return;
        }

        if ((tick + state.Stagger) % skill.PerceiveTicks != 0)
            return;

        if (world.Collision.CollisionLineBlocksBullets(self.X, self.Y, x, y, world.State.BulletFilter))
        {
            state.Visible = false;
            state.Target = state.Target with { Visible = false };

            if (tick - state.LastSeenAt > BotConstants.TargetPeriod * 2)
                state.DropTarget();

            return;
        }

        state.Visible = true;
        state.Target = state.Target with { Visible = true };
        state.LastSeenAt = tick;
    }

    /// <summary>Is the target still alive, still an enemy, and still not cloaked.</summary>
    /// <remarks>
    /// A sentry and a generator have hp and a team and nothing else this chain
    /// reads — no cloak, no ground, no player — so their checks are a character's
    /// minus the fields they do not have. In GM8 getting that wrong is not a
    /// crash, it is a silent read of some other instance's variable.
    /// </remarks>
    private static bool StillWorthShootingAt(
        BotCombatState state, in WorldView world, Team team, out double x, out double y)
    {
        x = 0;
        y = 0;

        switch (state.Target.Kind)
        {
            case BotTargetKind.Character:
            {
                var other = state.Target.Slot;

                if (!world.State.Alive[other])
                    return false;

                ref readonly var candidate = ref world.Character(other);

                if (candidate.Hp <= 0 || candidate.Cloak || world.Player(other).Team == team)
                    return false;

                x = candidate.X;
                y = candidate.Y;
                return true;
            }

            case BotTargetKind.Sentry:
            {
                var owner = state.Target.Slot;

                if (!world.State.HasSentry[owner])
                    return false;

                ref readonly var sentry = ref world.State.Sentries[owner];

                if (sentry.Hp <= 0 || sentry.Team == team)
                    return false;

                var box = Sentries.BodyBox(sentry);
                x = (box.Left + box.Right) / 2.0;
                y = (box.Top + box.Bottom) / 2.0;
                return true;
            }

            case BotTargetKind.Generator:
            {
                ref readonly var generator = ref team == Team.Red
                    ? ref world.State.BlueGenerator
                    : ref world.State.RedGenerator;

                if (!generator.Exists || generator.Hp <= 0)
                    return false;

                var body = Generators.BodyBox(generator);
                x = (body.Left + body.Right) / 2.0;
                y = (body.Top + body.Bottom) / 2.0;
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Looks for a target on the shared cadence, with a potshot roll when the
    /// ordinary radius is empty.
    /// </summary>
    /// <remarks>
    /// <b>Only the SEARCH widens.</b> Everything afterwards, including the fire
    /// gates, is identical for a potshot — it is an ordinary target that happens
    /// to be far away.
    /// </remarks>
    private static void LookForOne(
        BotCombatState state, in WorldView world, int slot, int tick, double range, ref GmlRandom random)
    {
        if (state.Target.Exists || (tick + state.Stagger) % BotConstants.TargetPeriod != 0)
            return;

        ref readonly var self = ref world.Character(slot);

        var found = BotTargeting.Find(in world, slot, range);
        state.Potshot = false;

        if (!found.Exists && BotClassProfile.Potshots(self.Class)
            && random.Next(1) < BotConstants.PotshotChance)
        {
            found = BotTargeting.Find(in world, slot, range * BotConstants.PotshotMult);
            state.Potshot = found.Exists;
        }

        if (!found.Exists)
            return;

        state.Target = found;
        state.TargetAt = tick;
        state.LastSeenAt = tick;
        state.Visible = true;
        state.HoldFire = false;
    }

    /// <summary>
    /// Refreshes the snapshot of where the target is and how fast it is going.
    /// </summary>
    /// <remarks>
    /// <b>Only while the target is visible.</b> A bot does not learn where
    /// somebody went while they were behind a wall — which is the whole reason
    /// the aim stays on the last known position rather than tracking through it.
    /// <para>
    /// And a change of <em>subject</em> refreshes it immediately, whatever the
    /// cadence says: a stale snapshot of the previous target would aim the first
    /// shot at the wrong place entirely.
    /// </para>
    /// </remarks>
    private static void Perceive(BotCombatState state, in WorldView world, int tick, in BotSkill skill)
    {
        if (tick < state.PerceiveAt && state.AimSubject == state.Target)
            return;

        if (!state.Visible)
            return;

        state.PerceiveAt = tick + skill.PerceiveTicks;
        state.AimSubject = state.Target;
        state.SeeX = state.Target.X;
        state.SeeY = state.Target.Y;

        // A sentry and a generator do not move, so their snapshot velocity is
        // zero and the solver's lead collapses to the gravity correction.
        if (state.Target.Kind == BotTargetKind.Character)
        {
            ref readonly var candidate = ref world.Character(state.Target.Slot);
            state.SeeVx = candidate.HSpeed;
            state.SeeVy = candidate.VSpeed;
        }
        else
        {
            state.SeeVx = 0;
            state.SeeVy = 0;
        }
    }

    /// <summary>Recomputes the desired direction on the aim cadence.</summary>
    private static void SolveAim(
        BotCombatState state, in WorldView world, int slot, int tick,
        int sinceSeen, double distance, in BotSkill skill, ref GmlRandom random, NavGraph? graph,
        bool subjectIsAlly = false)
    {
        if (tick < state.AimAt)
            return;

        state.AimAt = tick + skill.AimInterval;

        ref readonly var self = ref world.Character(slot);

        var aimX = state.SeeX;
        var aimY = state.SeeY;

        // --- suppression -------------------------------------------------------
        //
        // Holding a target that cannot be seen, the snapshot above is deliberately
        // frozen at the last position actually SEEN — which is, by construction,
        // behind the cover they went into. For a class whose weapon can deny a
        // doorway the better answer is the first point on the route they would have
        // to walk back along that this bot can actually see.
        //
        // On its own clock, not on the aim interval. BotSuppress.Spot is a
        // graph walk and the aim interval is 2 ticks at the top tier.
        // state.Visible, NOT state.Target.Visible. The first is the LIVE flag —
        // maintained on the perceive cadence and false exactly while the sightline
        // is broken — and the second is only what the search reported at
        // acquisition, which never changes again. Reading the target's made the
        // whole branch fire on the acquisition tick and never afterwards, and it is
        // `!player.botVisible` in the reference.
        if (!subjectIsAlly
            && !state.Visible
            && state.Target.Kind == BotTargetKind.Character
            && BotClassProfile.Suppresses(self.Class)
            && graph is not null)
        {
            if (tick >= state.SuppressAt)
            {
                state.SuppressAt = tick + BotConstants.TargetPeriod;
                state.SuppressNode = BotSuppress.Spot(
                    graph, world.Collision, in self, state.SeeX, state.SeeY,
                    world.Player(slot).Team == Team.Red ? Team.Blue : Team.Red);
            }

            if (state.SuppressNode >= 0)
            {
                (aimX, aimY) = BotSuppress.Centre(graph, state.SuppressNode);

                // The velocity that came with the snapshot belongs to a body
                // that was moving when it was last seen; A DOORWAY IS NOT MOVING.
                // Leading a fixed point by a dead man's velocity is how a suppression
                // shot ends up in the far wall.
                state.SeeVx = 0;
                state.SeeVy = 0;
            }
        }
        else
        {
            state.SuppressNode = BotSuppress.NoSpot;
        }

        // The splash classes aim at a grounded target's FEET, and only at a
        // skill high enough to know to. BOT_FEET_OFFSET below the chest is where
        // a rocket does the most damage to somebody standing on the floor.
        if (state.Target.Kind == BotTargetKind.Character
            && skill.SplashAim
            && BotClassProfile.Splash(self.Class)
            && world.Character(state.Target.Slot).OnGround)
        {
            aimY += BotConstants.FeetOffset;
        }

        state.AimX = aimX;
        state.AimY = aimY;

        state.AimWant = BotAim.Solve(
            in self, aimX, aimY, state.SeeVx, state.SeeVy, skill.LeadMode,
            // <b>The floor under the TARGET, and it is load-bearing.</b>
            // BotAim.FallPredict applies gravity whatever the target's vertical
            // speed is, so without the clamp a target standing perfectly still is
            // predicted to keep falling — and the bot aims BELOW its feet, into
            // the ground. A first draft passed BotAim.NoFloor here on the
            // reasoning that a grounded target is snapshotted with zero vertical
            // speed and so cannot move: that is wrong, and the symptom was a
            // soldier that acquired, fired, and never damaged anything.
            //
            // A null graph gives NoFloor, which is the right answer over a pit
            // and the only one available without one.
            BotAim.FloorUnder(graph, aimX, aimY));

        var moving = Math.Abs(state.SeeVx) > 0.5 || Math.Abs(state.SeeVy) > 0.5;
        var spread = Spread(
            skill.AimErrorDeg * BotClassProfile.AimError(self.Class),
            skill.AimConeDeg * BotClassProfile.AimError(self.Class),
            skill.MoveErrorMultiplier, moving, sinceSeen, distance);

        // Uniform on [-1, 1], not Gaussian: Quake III's crandom() is uniform
        // and so is every error term built on it.
        state.AimWant += (random.Next(2) - 1) * spread;

        // Rolled once per aim interval rather than per tick, so it withholds a
        // burst rather than stuttering the trigger.
        state.HoldFire = random.Next(1) < skill.HoldFireChance;
    }

    /// <summary>
    /// How far off the aim may be — <c>botAimSpread.gml</c>.
    /// </summary>
    /// <remarks>
    /// A floor plus a cone that <b>decays the longer the target is watched</b>,
    /// scaled down when it is moving and — <b>scaled UP at close range, for
    /// every tier</b>. That last one is Quake III's close-range penalty and is an
    /// anti-frustration measure rather than a difficulty knob: without it bots are
    /// unbeatable in a brawl.
    /// <para>
    /// The close factor <em>divides</em>, and it is below 1 near the target —
    /// so dividing makes the spread larger. Reading it as a multiplier inverts
    /// the whole intent.
    /// </para>
    /// </remarks>
    public static double Spread(
        double errorDegrees, double coneDegrees, double moveMultiplier,
        bool moving, int sinceSeen, double distance)
    {
        var spread = errorDegrees
            + (coneDegrees * Math.Pow(BotConstants.AimConeDecay, Math.Max(0, sinceSeen)));

        if (moving)
            spread *= moveMultiplier;

        var closeFactor = BotConstants.ClosePenalty
            + (Math.Min(Math.Max(0, distance), BotConstants.CloseRange) / BotConstants.CloseRange
               * (1 - BotConstants.ClosePenalty));

        return spread / closeFactor;
    }

    /// <summary>
    /// Turns the actual aim towards the desired one at the skill's turn rate.
    /// </summary>
    /// <remarks>
    /// It <b>snaps</b> when the remaining difference is within one tick's turn,
    /// rather than always stepping — otherwise the aim oscillates either side of
    /// the target for ever and the settle gate never opens.
    /// </remarks>
    private static void Slew(BotCombatState state, in BotSkill skill)
    {
        var delta = BotTargeting.AngleDelta(state.AimWant, state.AimDir);

        state.AimDir = Math.Abs(delta) <= skill.TurnRate
            ? state.AimWant
            : state.AimDir + (Math.Sign(delta) * skill.TurnRate);

        // Normalised into [0, 360) the way GM8 keeps a direction.
        state.AimDir = ((state.AimDir % 360) + 360) % 360;
    }
}
