using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// <c>botObjectiveUpdate</c>: where a bot should be heading, given the game mode's
/// objective.
/// </summary>
/// <remarks>
/// The "where" that <c>ScriptedBot.SetGoal</c> only ever answered by hand. It
/// mirrors the same <c>instance_exists</c> cascade <c>basicRoomSetup</c> uses to
/// pick a mode, because in the reference the mode itself is never stored anywhere
/// — <b>this port does store it (<c>WorldState.GameMode</c>) and still asks the
/// entities</b>, because a mode is not the same question as "what is on this map":
/// <c>ScenarioWorld</c> is TeamDeathmatch with nothing placed, and a CTF map whose
/// briefcase is being carried has no <c>Intelligence</c> instance either.
/// <para>
/// CTF/Invasion and every control-point-family mode get real objective-seeking.
/// Team Deathmatch has no landmark at all, so bots there keep fighting whatever
/// wanders into range and are given no goal.
/// </para>
/// <para>
/// Five things compose on top, in this order: <b>role</b> (a defender's anchor is
/// its own objective rather than the enemy's — the same cascade, mirrored),
/// <b>held</b> (once the objective is ours, a defender takes ground overlooking it
/// and an attacker walks out toward the enemy spawn), <b>spot</b> (for a goal that
/// is "a place from which I can do X", <see cref="BotGoalSpot"/> picks the node),
/// <b>class</b> (a sniper perches back, an engineer builds on the route, a spy
/// stages out of sight) and <b>spread</b> (a per-bot lateral offset so a team does
/// not converge on one pixel and stand in each other's line of fire).
/// </para>
/// <para>
/// <b>The gate on the class opinion is "would standing off lose the round", and
/// it is not decoration.</b> The objective in CTF is a thing to <em>pick up</em>: a
/// class that stops 300px short of the enemy intel and waits never captures, and if
/// a whole team happens to roll that class — <c>botPopulationUpdate</c> picks with
/// <c>irandom(8)</c>, so it can — the round simply never ends. So a spot mode never
/// overrides a goal the bot has to physically reach, and the classes whose job
/// <em>is</em> reaching it (a scout at <c>capStrength</c> 2) have no spot mode.
/// </para>
/// <para>
/// <b>The hold-ground trigger is a DISTANCE to the objective, not "have I
/// arrived", and that is not a detail.</b> Arrival is cleared by issuing a goal, so
/// a trigger keyed to it flips off the moment it fires: bot arrives, takes high
/// ground, arrival clears, next objective tick it no longer qualifies, walks back
/// to the point, arrives, and oscillates between the two for ever at 30-tick
/// intervals. A distance test is stable because the spot it chooses is itself
/// inside the radius that chose it.
/// </para>
/// <para>
/// <b>The medic's follow-goal goes FIRST and returns early</b>, ahead of the
/// whole mode cascade, because a medic following a team-mate is doing its job in
/// every mode and the objective is that team-mate's problem. With nobody to follow
/// it falls through and behaves like everyone else, which is the right thing to do
/// with a medic that has spawned alone.
/// <para>
/// <b>Its goal is a moving INSTANCE rather than a world point at all</b>, which
/// is why it does not feed the spread and spot machinery below — neither means
/// anything against a target that walks. It is the same shape as the flag-carrier
/// chase further down, and for the same reason.
/// </para>
/// </remarks>
public sealed class BotObjective
{
    private readonly NavGraph _graph;
    private readonly SpawnPoints _spawns;
    private readonly BotGoalSpot _spot;


    public BotObjective(NavGraph graph, SpawnPoints spawns)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(spawns);

        _graph = graph;
        _spawns = spawns;
        _spot = new BotGoalSpot(graph);
    }

    /// <summary>
    /// Decides this bot's destination.
    /// </summary>
    /// <param name="role">Assigned once at join — see <see cref="BotRoles"/>.</param>
    /// <param name="spreadX">
    /// This bot's constant lateral offset. Applied last and dropped entirely on a
    /// goal that has to be touched.
    /// </param>
    /// <param name="allowSpot">
    /// False while a spot goal is banned — see <see cref="BotGoal.IsSpot"/>. The
    /// caller owns that ban because only it can see a goal that never produced a
    /// path.
    /// </param>
    /// <param name="target">
    /// What the bot's combat half is looking at, if anything — the medic's follow
    /// position is measured relative to it. Null is "nothing", and the follow
    /// position is then simply the ally's own.
    /// </param>
    /// <param name="reposting">
    /// True when the caller has watched this bot stand completely still with
    /// nothing to walk to for <see cref="BotConstants.RepostTicks"/> — see
    /// <c>ScriptedBot.ParkedTicks</c>. It asks for a <em>different</em> place to
    /// stand. The caller owns the decision because only it can see the keys the
    /// bot actually pressed.
    /// </param>
    /// <param name="repostSkip">
    /// How many re-post candidates to pass over — see
    /// <see cref="BotGoalSpot.Find"/>. The caller counts the ones that turned out
    /// to have no route.
    /// </param>
    public BotGoal Decide(
        in WorldView world, int slot, BotRole role, double spreadX, bool allowSpot,
        int? target = null, bool reposting = false, int repostSkip = 0)
    {
        if (!world.IsAlive(slot))
            return BotGoal.None;

        ref readonly var self = ref world.Character(slot);
        ref readonly var state = ref world.State;

        var team = world.Player(slot).Team;
        var playerClass = world.Player(slot).Class;
        var defending = role == BotRole.Defend;

        // --- the medic's follow-goal -----------------------------------------
        //
        // A medic is the one class for which NEVER ENGAGING is correct play, and
        // the thing it should be following is not a fixed world point at all.
        //
        // An intel carrier does not follow. There is exactly one thing to
        // do with an enemy flag and it is not "escort somebody else while holding
        // it" — the carry branch below owns that case.
        if (BotClassProfile.Follows(playerClass) && !self.CarryingIntel)
        {
            // skipFollowers TRUE here and false in the combat half. Two
            // medics who pick each other deadlock: each one's goal is where the
            // other is standing, both are already there, and neither ever leaves
            // spawn. The BEAM has no such problem, which is why it is an argument
            // rather than a rule — see BotAlly.
            var ally = BotAlly.Find(in world, slot, BotConstants.MedicFollowRange, skipFollowers: true);

            if (ally != BotAlly.None)
            {
                ref readonly var mate = ref world.Character(ally);

                // Keep the ally between me and the enemy: step to the far side of
                // them from whatever this bot is currently looking at.
                //
                // Horizontal only. The goal is a place to STAND, and
                // standing is a one-dimensional choice on a given surface.
                var wantX = mate.X;

                if (target is { } foe && world.IsAlive(foe))
                    wantX += world.Character(foe).X > mate.X ? -BotConstants.MedicOffset : BotConstants.MedicOffset;

                // BOT_GOAL_RETARGET_DIST throttles the re-issue for free. A
                // moving ally naturally trips the 48px test — which is exactly the
                // case that check was written for — while one standing still does
                // not, and the goal is left alone.
                return new BotGoal(true, wantX, mate.Y, IsSpot: false, MustTouch: true);
            }

            // Nobody to follow: fall through to the mode cascade and walk the
            // objective like everyone else, healing whoever it meets on the way.
        }

        var found = false;
        var useSpot = false;
        var held = false;
        var mustTouch = false;
        var contested = false;

        double wx = 0, wy = 0, anchorX = 0, anchorY = 0;
        double spotMin = 0, spotMax = 0, spotHigh = 0;
        var spotLos = BotLos.Need;

        // Which team's carrier to run at, if the flag this bot wants turns out to
        // be in somebody's hands rather than on the ground. Null is "no chase",
        // which is every mode except CTF and most of CTF too.
        Team? chase = null;

        if (state.RedIntel.HasBase || state.BlueIntel.HasBase)
        {
            Intel(in state, in self, team, defending, ref wx, ref wy, ref found, ref mustTouch, ref chase);
        }
        else if (state.RedGenerator.Exists || state.BlueGenerator.Exists)
        {
            found = Generator(
                in state, playerClass, team, defending,
                ref wx, ref wy, ref anchorX, ref anchorY, ref spotMin, ref spotMax, ref spotHigh,
                ref useSpot);
        }
        else if (state.ControlPointCount > 0)
        {
            found = ControlPoint(
                in world, in self, team, defending, ref wx, ref wy, ref held, ref contested);
        }
        else
        {
            // Team Deathmatch, and the ONLY mode with nothing in the world to
            // walk to.
            found = Deathmatch(
                in world, slot, in self, playerClass, target,
                ref wx, ref wy, ref anchorX, ref anchorY,
                ref spotMin, ref spotMax, ref spotHigh, ref useSpot);
        }

        // --- the flag is in somebody's hands ---------------------------------
        //
        // A carried flag has no instance at all, so every branch above that asks
        // for one comes back empty and the cascade has nothing to offer. The bot
        // HOLDING it is fine — it took the carry branch — but everyone else on both
        // teams was left with no goal: a defender whose flag was just stolen, and
        // an attacker whose team-mate beat it to the grab. Between them that is
        // most of a round's most interesting minute spent standing still.
        //
        // The goal is a moving instance, so it returns here rather than feeding
        // a world point into the spread and spot machinery below. Neither means
        // anything against a target that walks — a standoff band around a running
        // player is a bot orbiting it, and a spread is a deliberate miss of the one
        // thing worth intercepting.
        //
        // Both directions of the chase reach here: running down the enemy who has
        // OUR flag, which every role does, and escorting the team-mate who has
        // theirs.
        if (chase is { } chaseTeam)
        {
            var carrier = Carrier(in world, chaseTeam);

            if (carrier >= 0)
            {
                ref readonly var it = ref world.Character(carrier);
                return new BotGoal(true, it.X, it.Y, IsSpot: false, MustTouch: true);
            }

            // No carrier resolvable — the flag changed hands or was returned
            // between the test above and here — so fall through and keep whatever
            // goal the bot already had, which is what this does with a mode it
            // cannot read.
        }

        if (!found)
            return BotGoal.None;

        // --- somebody is taking our point ------------------------------------
        //
        // Every "stand somewhere better than the objective" behaviour below is
        // correct only while nothing is happening on the objective itself. A
        // control point is captured by BODIES INSIDE THE ZONE and by nothing else,
        // so the moment an enemy is standing in it, an overlook 250px up the hill
        // and a push 400px out toward their spawn are the same mistake: the bot is
        // looking at the point it is losing.
        //
        // held drops for everyone, which sends attacker and defender alike back to
        // the zone centre — an attacker that had pushed out stops holding forward
        // ground and comes back to the fight, which is where the round is being
        // decided.
        if (contested)
            held = false;

        // --- does this bot's class want to stand somewhere else? --------------
        //
        // Decided before the hold/push block because the engineer's chokepoint and
        // the attacker's push are the same offset — out toward where the enemy will
        // come from — and computing it once is what keeps the two from drifting
        // apart. They are mutually exclusive at runtime (a chokepoint is a
        // defender's goal and the push is an attacker's), so nothing here has to
        // choose between them.
        var spotMode = BotClassProfile.SpotMode(playerClass);
        var classWants = false;

        if (spotMode != BotSpotMode.None && !self.CarryingIntel)
        {
            classWants = spotMode switch
            {
                // Only where the objective is something to cover rather than to
                // reach: guarding it, holding it once it is ours, or shooting it.
                // An attacker walking at a point nobody holds yet is doing the one
                // job a standoff cannot do.
                BotSpotMode.Standoff => defending || held || useSpot,
                BotSpotMode.Choke => defending,
                BotSpotMode.Flank => !mustTouch,
                _ => false,
            };
        }

        // A defender's class opinion does not survive a point being taken. A
        // sniper perched 400px back and an engineer sat on a chokepoint are both
        // doing something useful right up until the thing they are covering is
        // being stood on by the enemy — at which point the only move that touches
        // the outcome is being in the zone. Attackers KEEP their class positioning:
        // their job was never to stand on it, and pulling a whole team onto one
        // tile is how a demoman clears four bots at once.
        if (contested && defending)
            classWants = false;

        // A class with its own positioning opinion does not ALSO take the
        // generic push. They are two answers to the same question, and applying
        // both silently anchors the class's answer on the other one's output: the
        // spy's flank came out measured from a point 400px advanced toward the
        // enemy spawn, so the sightline it was avoiding was to somewhere the
        // objective was not. Caught live in the reference; nothing about it looks
        // wrong in the code, since both halves are individually doing their job.
        var wantPush = (held && !defending && !classWants)
            || (classWants && spotMode == BotSpotMode.Choke);

        double pushX = 0, pushY = 0;

        if (wantPush && NearestEnemySpawn(team, wx, wy, out var spawnX, out var spawnY))
        {
            pushX = (spawnX - wx) * BotConstants.PushFrac;
            pushY = (spawnY - wy) * BotConstants.PushFrac;

            var length = Gml.PointDistance(0, 0, pushX, pushY);

            // Capped on purpose: loitering outside an enemy spawn is a bad
            // strategy, not an aggressive one — players retreat inside, heal to
            // full and walk back out, while the bot has given up the thing it was
            // holding.
            if (length > BotConstants.PushDist)
            {
                pushX = pushX * BotConstants.PushDist / length;
                pushY = pushY * BotConstants.PushDist / length;
            }
        }

        // --- the point is already ours: hold it, or push past it --------------
        //
        // Arrival is a STATE, not a terminus. A capture zone never moves, so
        // without this a bot that reaches an already-held point freezes on it for
        // ever — the route ends and nothing re-tasks it. A random jitter stood in
        // for this once; what a player actually does is one of two things, split by
        // role: a defender takes ground that OVERLOOKS the point rather than
        // standing in the open on it, and an attacker walks out toward where the
        // enemy will come from, so the point is defended in front of itself rather
        // than on top of itself.
        if (held)
        {
            if (defending)
            {
                anchorX = wx;
                anchorY = wy;
                spotMin = BotConstants.ArriveTol;
                spotMax = BotConstants.HoldRadius;
                spotHigh = BotConstants.SpotHighBonus;
                useSpot = true;
            }
            else
            {
                wx += pushX;
                wy += pushY;
            }
        }

        // --- per-class positioning -------------------------------------------
        //
        // The class's opinion REPLACES whatever band the shared logic arrived at,
        // because the two are answers to the same question and the class one is the
        // more specific: a defending heavy told to stand within BOT_HOLD_RADIUS of
        // the point and a defending heavy told to hold high ground 190-375px off it
        // are not two goals to combine, they are one goal at two levels of detail.
        //
        // The band is derived from BotTargeting's own range table rather than
        // written out per class, so it stays correct when a weapon's reach is
        // retuned. The floor under both is the class's minimum band, so a standoff
        // can never put a bot inside its own splash radius.
        if (classWants)
        {
            anchorX = wx;
            anchorY = wy;
            spotMax = BotTargeting.ClassRange(playerClass) * BotConstants.SpotBand;
            spotMin = Math.Max(BotTargeting.ClassMinBand(playerClass), 1);
            spotHigh = BotClassProfile.SpotHigh(playerClass);
            spotLos = BotLos.Need;
            useSpot = true;

            switch (spotMode)
            {
                case BotSpotMode.Standoff:
                    spotMin = Math.Max(spotMin, spotMax * BotConstants.SpotNearFrac);
                    break;

                case BotSpotMode.Choke:
                    // The ANCHOR moves, not the bot: a chokepoint is a place on
                    // the route between what is being guarded and where the attack
                    // comes from, so the sentry covers the approach instead of the
                    // thing itself.
                    anchorX += pushX;
                    anchorY += pushY;

                    // ...and unlike every other anchor here, that one is a bare
                    // point on a route rather than an object. Asking for a sightline
                    // to it is asking the wrong question — nothing is going to be
                    // shot AT the chokepoint, the sentry covers whatever walks
                    // through it — and the point can perfectly well land inside a
                    // wall or above a roof, where almost nothing can see it.
                    // Measured on koth_valley: 26 nodes in the band and only 6 with
                    // a clear line, so the search spent its whole BOT_SPOT_TRIES
                    // budget and returned nothing, and the engineer fell back to
                    // standing on the point it was supposed to be covering.
                    // Proximity is the whole requirement here.
                    spotLos = BotLos.Any;
                    break;

                case BotSpotMode.Flank:
                    // Near the objective but with no sightline to it: the approach
                    // nobody is watching.
                    spotLos = BotLos.Avoid;
                    break;
            }
        }

        // --- a place from which to do something, or the thing itself ----------

        // --- the bot has been standing on this goal doing nothing -------------
        //
        // Arrival frees the path and clears the goal, so an arrived bot presses no
        // movement key at all — and every later decision hands it back the same
        // point, which resolves to the same node, which it is already standing on.
        // Nothing in the loop moves it again for the rest of the round.
        //
        // ⚠️ It reads as a defender's bug and is not one: any bot whose goal is a
        // place to STAND rather than a thing to touch parks the same way.
        // Defenders show it because their objective never moves. Measured on
        // ctf_orange, twelve bots, four seeds: 3-5 of them frozen on one pixel for
        // 900-3,945 consecutive ticks of a 4,000-tick match, across Demoman,
        // Engineer, Heavy, Medic, Pyro, Sniper and Soldier alike. Reported from
        // play as a bot standing still holding the trigger — the trigger is the
        // combat half, which goes on working perfectly while the bot cannot move.
        //
        // ⚠️ A goal that has to be TOUCHED is NOT excluded, and the reasoning is
        // worth keeping. The obvious rule is to exclude it — re-posting away from
        // the enemy intel is how a round never ends, which is the mistake the
        // spread carries its own exception for. But the two are not the same case:
        // the spread applies to a bot that is WALKING to the intel and would stop
        // short of it, and this applies to one that has been standing motionless
        // for eight seconds having already arrived. It is not about to touch it.
        //
        // Measured on ctf_avanti, which is Invasion and has an 1,800-tick SETUP
        // timer: eight of twelve bots walked to the enemy intel, arrived 3px off
        // it, could not take it until setup expired, and stood there for the whole
        // 1,600 ticks in between — then moved the instant the timer hit zero. The
        // exclusion was the only thing holding them, and it can only ever hold a
        // bot whose grab is already failing: one that CAN take the intel takes it
        // on arrival and never parks.
        var avoidRadius = 0.0;
        var avoidNode = -1;

        if (reposting)
        {
            avoidRadius = BotConstants.RepostMinMove;
            avoidNode = _graph.NodeFromWorld(self.X, self.Y);

            // The hold band, REPLACING whatever the class asked for rather than
            // narrowing it. It is the same band a defender on a held control point
            // already gets, and for the same reason: ground near the thing rather
            // than the pixel it sits on.
            //
            // ⚠️ Replacing rather than intersecting is what makes this work at
            // all. Keeping the class band and only excluding the bot's own node
            // was measured first and mostly missed — 133 of 166 asks on a
            // ctf_truefort match found nothing, because a standoff band is narrow,
            // it demands a sightline, and BOT_SPOT_TRIES stops the search after
            // twelve line tests. A class that has stood still for eight seconds
            // has already shown its perch is not paying for itself.
            //
            // ⚠️ The anchor stays on the OBJECTIVE even after a re-post has come
            // back with no route. Moving it to the bot on the retry is the obvious
            // next idea — if the objective is unreachable then so is everything
            // near it — and it was tried and is REFUTED: the frozen count went 7
            // to 8. Whatever holds those six is not the anchor, so do not reach
            // for this again without a measurement.
            anchorX = wx;
            anchorY = wy;
            spotMin = BotConstants.ArriveTol;
            spotMax = BotConstants.HoldRadius;
            spotHigh = BotConstants.SpotHighBonus;

            // Proximity is the whole requirement, as at the engineer's
            // chokepoint. This is a place to wait, not a place to shoot the anchor
            // from, and demanding a sightline to a briefcase lying on the floor of
            // a base rejects most of the base — and a search with no sightline
            // test cannot spend its try budget, so it answers or the band is
            // empty.
            spotLos = BotLos.Any;
            useSpot = true;
        }

        if (useSpot && allowSpot)
        {
            var spot = _spot.Find(
                self.X, self.Y, anchorX, anchorY, spotMin, spotMax, spotLos, spotHigh,
                world.Collision, CollisionFilter.ForBullet(), avoidRadius, avoidNode,
                reposting ? repostSkip : 0);

            if (spot.Node >= 0)
                return new BotGoal(true, spot.X, spot.Y, IsSpot: true, mustTouch);

            // Nothing qualified: fall through to the objective's own point, which
            // is what this did before the goal layer existed.
        }

        // The spread never applies to a goal that has to be TOUCHED. Standing
        // 96px from a control point still captures it and standing 96px from a
        // generator still shoots it, which is what makes the spread free everywhere
        // else — but the intel is picked up by collision and the capture is scored
        // by collision, so an offset there is the difference between playing the
        // game mode and standing next to it. Measured in the reference on
        // ctf_truefort: bots walked the whole map to the enemy flag, stopped 37-67px
        // short of it (arrival tolerance is 8, so they stopped PRECISELY), and stood
        // there — no grab, no cap, 0-0 after a full round with both teams parked
        // around a flag neither would touch.
        //
        // A contested point is the same rule for a sharper reason: a capture zone
        // is 125px wide on koth_valley, the spread is 96 either way and a defender
        // adds another 220 on top. Measured live, a defending sniper sent to contest
        // its own point stood at 2544 while the zone ended at 2393, contesting
        // nothing. The bodies of several bots converging on one column push each
        // other apart on their own, which is spread enough for a target this size.
        if (mustTouch || contested)
            spreadX = 0;
        else if (defending)
            spreadX += spreadX >= 0 ? BotConstants.DefendRadius : -BotConstants.DefendRadius;

        return new BotGoal(true, wx + spreadX, wy, IsSpot: false, mustTouch);
    }

    /// <summary>
    /// Resolves a decided goal to a nav node's own stand position.
    /// </summary>
    /// <remarks>
    /// <b>Every objective in the game is an OBJECT position, and map objects are
    /// not anchored the way a character is</b> — a capture zone's marker can sit
    /// 70px above the floor it belongs to and there is no fixed offset that
    /// generalises. So the rule is: resolve the objective to a node, then re-derive
    /// the goal from <em>that node's own</em> canonical stand position, which
    /// guarantees the follower resolves it back to the same node later.
    /// <para>
    /// <b>The objective's own node is resolved first and the spread only earns its
    /// way in.</b> Getting that the other way round is a defect the reference shipped
    /// for one build: the snap searches DOWNWARD, so snapping an already-offset
    /// position can land on a floor several storeys below the objective — the offset
    /// steps off the edge of the platform the intel is on and the search falls all
    /// the way to the ground. A bot then walks confidently to a place with nothing to
    /// do with its objective, which is far worse than not spreading at all. An offset
    /// snap is accepted only when it lands within a body's height of the objective's
    /// own node, which makes it "the same level" rather than "the same surface" — so
    /// a spread can still cross between two runs that meet at a step.
    /// </para>
    /// </remarks>
    public bool TryResolve(in BotGoal goal, double spreadX, out double x, out double y)
    {
        x = 0;
        y = 0;

        if (!goal.Found)
            return false;

        var baseX = goal.X - spreadX;
        var node = _graph.SnapObjective(baseX, goal.Y).Node;

        if (node < 0)
            return false;

        if (spreadX != 0)
        {
            var offset = _graph.SnapObjective(goal.X, goal.Y).Node;

            if (offset >= 0
                && Math.Abs(_graph.StandY(offset) - _graph.StandY(node)) <= NavConstants.BoxH * NavConstants.CellSize)
            {
                node = offset;
            }
            else
            {
                spreadX = 0;
            }
        }

        // Whichever of that node's columns is nearest the goal's own x — the
        // follower's clamp then spreads the bot along that node's columns for free
        // even when the offset snap was refused.
        var n = _graph.Nodes[node];
        var column = Math.Clamp(NavGraph.AnchorColumn(baseX + spreadX), n.X0, n.X1);

        x = NavGraph.ColumnWorldX(column);
        y = _graph.StandY(node);

        return true;
    }

    private void Intel(
        in WorldState state, in CharacterState self, Team team, bool defending,
        ref double wx, ref double wy, ref bool found, ref bool mustTouch, ref Team? chase)
    {
        var enemy = team == Team.Red ? Team.Blue : Team.Red;

        ref readonly var ownIntel = ref team == Team.Red ? ref state.RedIntel : ref state.BlueIntel;
        ref readonly var enemyIntel = ref team == Team.Red ? ref state.BlueIntel : ref state.RedIntel;

        if (self.CarryingIntel)
        {
            // Carrying: get it home. Roles do not apply — there is exactly one thing
            // to do with an enemy intel and it is not "guard something else while
            // holding it". A capture is a collision with the base, exactly like the
            // grab at the other end of the run, so this is the second goal that has
            // to be walked into rather than covered.
            if (ownIntel.HasBase)
            {
                wx = ownIntel.HomeX;
                wy = ownIntel.HomeY;
                found = true;
                mustTouch = true;
            }

            return;
        }

        if (defending)
        {
            // Guard our own flag — wherever the flag currently IS, including dropped
            // in the open halfway across the map, which is exactly the moment a
            // defender earns its place.
            if (ownIntel.OnField)
            {
                wx = ownIntel.X;
                wy = ownIntel.Y;
                found = true;
                return;
            }

            // Our flag is not on the ground, so an enemy is carrying it — the
            // single most urgent thing that happens to a defender in this mode.
            chase = enemy;
            return;
        }

        // Our own flag being carried OUTRANKS fetching theirs, and is tested
        // first for that reason: every role answers it, not just a defender — a
        // team that happens to roll no defenders (role count runs down the
        // roster, a short team can land none) must still have somebody chase the
        // thief. An attacker halfway to the enemy base is still the closest thing
        // our flag has to a defender, and a capture ends the round whatever else
        // that attacker was about to achieve.
        if (!ownIntel.OnField && ownIntel.HasBase)
        {
            chase = enemy;
        }
        else if (enemyIntel.OnField)
        {
            wx = enemyIntel.X;
            wy = enemyIntel.Y;
            found = true;

            // The one goal in the game that has to be walked into rather than merely
            // covered, which is what forbids a per-class standoff below.
            mustTouch = true;
        }
        else if (enemyIntel.HasBase)
        {
            // The flag this bot was going to fetch is already in a team-mate's hands,
            // so there is nothing left at the far end to walk to. Escort the carrier
            // home instead: it is the play, and it is also the only thing that keeps
            // the rest of an attacking team moving — measured on ctf_truefort, three
            // of four blue bots stood at the empty red stand for the whole time their
            // team-mate was running the flag back.
            chase = team;
        }
    }

    /// <summary>
    /// Team Deathmatch: the objective is the other team, so the goal is a place
    /// to shoot one of them from.
    /// </summary>
    /// <remarks>
    /// <b>The design question the pin in <c>MatchActuallyHappensTests</c> left
    /// open</b> — hunt the nearest enemy, patrol, or hold a chokepoint. It is
    /// answered as <b>hunt</b>, because the other two need a notion of territory
    /// that no mode without an objective has, and because a deathmatch's whole
    /// content is finding somebody.
    /// <para>
    /// <b>A SPOT anchored on the enemy, never the enemy's own position.</b>
    /// That distinction is the entire difficulty and it was measured twice.
    /// <c>BotClassKeys</c> refuses to fire inside
    /// <see cref="BotTargeting.ClassMinBand"/> — "a rocket at that range kills the
    /// shooter" — and it explicitly does <em>not</em> move the bot away, because
    /// "where this bot stands is the objective layer's decision rather than this
    /// one's". <b>So a goal of "walk onto the enemy" walks a soldier to point
    /// blank and leaves it there, permanently, not firing</b>: measured in
    /// <c>Gg2.Env</c>'s duels as 250 decisions of walking, arrival 59px away
    /// against a band of 110, and zero shots.
    /// </para>
    /// <para>
    /// The spot search is what fixes it, and it is the machinery that already
    /// exists: the band's floor is the class's own minimum, so a standoff can
    /// never put a bot inside its own splash radius, and it returns a real
    /// walkable node rather than an offset that may be in mid-air. <b>Offsetting
    /// the enemy's position by the band was tried first and is worse</b> — the
    /// offset lands off whatever surface the target is standing on and A* refuses
    /// it, so the bot stops dead with no route at all.
    /// </para>
    /// <para>
    /// <b>The combat target first, the nearest enemy second.</b> Preferring
    /// whatever the bot is already shooting at keeps the movement half and the
    /// firing half pointed at one player; picking independently makes a bot walk
    /// toward one enemy while shooting another, which is how it ends up in the
    /// open between two of them. The nearest is the fallback for a bot that has
    /// not acquired anything yet, and is what starts the hunt.
    /// </para>
    /// <para>
    /// <b>No role split, and that is deliberate.</b> <see cref="BotRole"/>
    /// divides a team into attack and defend, which means nothing when there is
    /// nothing to defend — a "defending" TDM bot is a bot told to guard empty
    /// ground. Both roles hunt.
    /// </para>
    /// </remarks>
    private static bool Deathmatch(
        in WorldView world, int slot, in CharacterState self, PlayerClass playerClass, int? target,
        ref double wx, ref double wy, ref double anchorX, ref double anchorY,
        ref double spotMin, ref double spotMax, ref double spotHigh, ref bool useSpot)
    {
        var foe = -1;

        if (target is { } acquired && world.IsAlive(acquired))
        {
            foe = acquired;
        }
        else
        {
            // NearestEnemies is ordered, so the first live one is the nearest.
            // It skips empty slots inside its own sweep, so this costs the same
            // whatever the population — see docs/ENV.md's extraction measurement.
            Span<int> nearest = stackalloc int[4];
            var count = world.NearestEnemies(slot, nearest);

            for (var i = 0; i < count; i++)
            {
                if (!world.IsAlive(nearest[i]))
                    continue;

                foe = nearest[i];
                break;
            }
        }

        // Nobody to hunt is BotGoal.None, exactly as before. A bot alone on the
        // map should stand still rather than walk to where somebody used to be —
        // and this is the state every round starts in for one tick.
        if (foe < 0)
            return false;

        ref readonly var enemy = ref world.Character(foe);

        wx = enemy.X;
        wy = enemy.Y;

        anchorX = wx;
        anchorY = wy;

        // The same band the generator's firing position uses, and for the same
        // reason: comfortably inside the weapon's reach rather than on the edge of
        // it, where a target taking one step is out of range. The floor is the
        // class's own minimum band — the line that stops this being the point-blank
        // standoff described above.
        spotMin = Math.Max(BotTargeting.ClassMinBand(playerClass), 1);
        spotMax = BotTargeting.ClassRange(playerClass) * BotConstants.SpotBand;

        // Height is worth having here where it is not worth having against a
        // generator: a generator cannot shoot back. This is the one goal in the
        // cascade whose anchor is an opponent, so the class's own height
        // preference is exactly the right question to ask.
        spotHigh = BotClassProfile.SpotHigh(playerClass);
        useSpot = true;

        return true;
    }

    private bool Generator(
        in WorldState state, PlayerClass playerClass, Team team, bool defending,
        ref double wx, ref double wy, ref double anchorX, ref double anchorY,
        ref double spotMin, ref double spotMax, ref double spotHigh, ref bool useSpot)
    {
        // The objective here is to DESTROY a generator, not to touch one — each team
        // both defends its own and attacks the other's, and a generator is shot from
        // wherever a bot has line of sight to it. So this branch is the goal layer's
        // whole reason for existing: the anchor is the generator, and the goal is a
        // place to shoot it from.
        var want = defending
            ? team == Team.Red ? Team.Red : Team.Blue
            : team == Team.Red ? Team.Blue : Team.Red;

        ref readonly var generator = ref want == Team.Red ? ref state.RedGenerator : ref state.BlueGenerator;

        if (!generator.Exists)
            return false;

        // A generator's origin is not its middle, and the middle is what range and
        // sight want to be measured to — the same correction the capture zones get.
        var box = Generators.BodyBox(in generator);
        wx = (box.Left + box.Right) / 2.0;
        wy = (box.Top + box.Bottom) / 2.0;

        // A defender wants to be NEAR its own generator rather than lined up on it,
        // so only the attacker's goal is a firing position. The band keeps that
        // position comfortably inside the weapon's reach instead of on the very edge
        // of it, where a target taking one step is out of range.
        if (!defending)
        {
            anchorX = wx;
            anchorY = wy;
            spotMin = Math.Max(BotTargeting.ClassMinBand(playerClass), 1);
            spotMax = BotTargeting.ClassRange(playerClass) * BotConstants.SpotBand;
            spotHigh = 0;
            useSpot = true;
        }

        return true;
    }

    private static bool ControlPoint(
        in WorldView world, in CharacterState self, Team team, bool defending,
        ref double wx, ref double wy, ref bool held, ref bool contested)
    {
        ref readonly var state = ref world.State;

        // Everyone heads for whichever unlocked point is nearest to them, which
        // naturally spreads bots across a multi-point map instead of everyone piling
        // onto the same one.
        //
        // Every point is locked for the first 900 ticks of a KotH round and 1,800
        // of an Arena one; a bot should be standing on the point the moment it
        // opens, the way a human walks out immediately. So the nearest point is
        // tracked regardless of lock state as a fallback, and used only once no
        // unlocked one exists.
        //
        // A defender prefers a point its own team already holds — the same loop
        // with everything that is NOT ours pushed 2,000px away, which is more than
        // any map is wide. Penalising the others rather than rewarding ours keeps
        // every distance >= 0, which is what lets -1 go on meaning "no point found"
        // in both accumulators. On a single-point map (every KotH map) it resolves to
        // the same point either way, and the split then shows up in what a defender
        // does once it is there rather than in where it goes.
        var best = -1.0;
        var target = -1;
        var bestAny = -1.0;
        var any = -1;

        for (var i = 0; i < state.ControlPointCount; i++)
        {
            if (!TryZoneCentre(world.Collision, i, out var zx, out var zy))
                continue;

            ref readonly var point = ref state.ControlPoints[i];
            var d = Gml.PointDistance(self.X, self.Y, zx, zy);

            if (defending && (!point.Owned || point.Team != team))
                d += 2000;

            if (bestAny < 0 || d < bestAny)
            {
                bestAny = d;
                any = i;
            }

            if (!point.Locked && (best < 0 || d < best))
            {
                best = d;
                target = i;
            }
        }

        if (target < 0)
            target = any;

        // DUAL KOTH IS WON BY HOLDING THE ENEMY'S HILL, and "nearest unlocked
        // point" is always your own — so without this override an attacker never
        // contests the enemy's hill at all. docs/TRAPS-BOTS.md, "Dual King of the
        // Hill is unwinnable by bots".
        //
        // THE ONE PLACE THIS FILE READS GameMode, and the reason is the one
        // `gg2-rl` drew: entity identification works while the objective is a
        // property of the LAYOUT, and dkoth's is a property of the SCORING.
        // Two capture points is an identical entity signature to a two-point
        // Control Points map; what differs is that Rounds.StepDkoth runs your
        // clock while the ENEMY's point is yours. No amount of looking at
        // entities can recover that, because the entities are the same.
        //
        // Both entity-shaped alternatives were rejected for being right by
        // accident: "prefer a point we do not own" breaks single KotH, where
        // holding the hill IS the objective; and "chase whichever point is making
        // the enemy's clock run" has no signal in exactly the state these matches
        // sit in for ten minutes, where both clocks are stopped.
        //
        // A DEFENDER still wants its own hill — losing it starts the enemy's
        // clock — so this is the attacker's rule only, and the +2000 penalty above
        // already gives the defender the right answer.
        if (!defending
            && state.GameMode == GameMode.DualKingOfTheHill
            && state.ControlPointCount >= 2)
        {
            // Index 0 is KothRedControlPoint and index 1 is KothBlueControlPoint —
            // ControlPoints.Place puts them in that order and Rounds.StepDkoth
            // keys the clocks on it.
            target = team == Team.Red ? 1 : 0;
        }

        if (target < 0 || !TryZoneCentre(world.Collision, target, out wx, out wy))
            return false;

        ref readonly var chosen = ref state.ControlPoints[target];

        held = chosen.Owned && chosen.Team == team
            && Gml.PointDistance(self.X, self.Y, wx, wy) <= BotConstants.HoldRadius;

        contested = Contested(in chosen, team);
        return true;
    }

    /// <summary>
    /// The middle of a point's capture zone.
    /// </summary>
    /// <remarks>
    /// <b>The middle, not the origin.</b> A capture zone's origin is its top-left
    /// corner, so aiming there asks the follower to stop on the exact boundary of the
    /// thing it is trying to stand in — and arrival is only accurate to
    /// <c>BOT_ARRIVE_TOL</c>. Measured in the reference on koth_valley: the bot
    /// climbed the whole four-jump chain, stopped 3px short of the zone's left edge,
    /// and captured nothing. The zone is 125px wide; its centre is not a near miss.
    /// <para>
    /// <b>The zone is the only position this port has for a control point</b>, where
    /// the reference has the point instance's own <c>x</c>/<c>y</c> as well and uses
    /// it to rank the nearest point before overriding it with the zone centre. Nothing
    /// stores a point's position here — <c>ControlPoints.Place</c> discards it once the
    /// zones are matched — and a capture reads the zone and nothing else, so the zone
    /// is the honest anchor. The difference is the offset between a marker sprite and
    /// its own zone's centre, and it can only reorder two points that are nearly
    /// equidistant. A point with no zone is not a goal at all, which is why the
    /// gate asserts every shipped map gives each of its points one.
    /// </para>
    /// </remarks>
    private static bool TryZoneCentre(CollisionWorld collision, int point, out double x, out double y)
    {
        x = 0;
        y = 0;

        foreach (var zone in collision.CaptureZones)
        {
            if (zone.Point != point)
                continue;

            x = (zone.Left + zone.Right) / 2.0;
            y = (zone.Top + zone.Bottom) / 2.0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>botPointContested</c>: an enemy is on our point, or has already put
    /// progress on it.
    /// </summary>
    /// <remarks>
    /// <b>PRESENCE is the signal that matters, not the capture meter.</b> Presence
    /// is recomputed every step from the characters standing in the zone, so it is
    /// true on the frame an enemy steps inside — while the meter takes a second to
    /// become non-zero, and by the time it reads as a capture the point is a third
    /// gone. A defender that only reacts to the meter is a defender that arrives
    /// after the fight.
    /// <para>
    /// The presence values are 0 (nobody), 1 (only an ubered enemy, who cannot cap
    /// but does deny) and 2 (real cappers), and all three want the same answer here.
    /// </para>
    /// <para>
    /// The meter is still consulted, because a capture already under way is contested
    /// even in the moment nobody is standing on it — progress decays slowly and a bot
    /// that walks away the instant the zone empties gives the same enemy their
    /// progress back for free.
    /// </para>
    /// <para>
    /// A LOCKED point cannot be captured by anybody, so nothing about it is
    /// contested and a defender is better off where its own logic wants to be.
    /// </para>
    /// </remarks>
    private static bool Contested(in ControlPointState point, Team team)
    {
        if (point.Locked)
            return false;

        var enemyPresence = team == Team.Red ? point.BluePresence : point.RedPresence;

        if (enemyPresence > 0)
            return true;

        return point.Capping > 0 && point.BeingCaptured && point.CappingTeam != team;
    }

    /// <summary>
    /// <c>botEnemySpawn</c>: the enemy spawn point nearest a place.
    /// </summary>
    /// <remarks>
    /// Maps author up to five spawn points per team, and which of them is "the" enemy
    /// spawn depends on where you are standing — on a multi-stage map they are
    /// scattered across the whole level. Nearest-to-the-caller is the answer that
    /// means "the direction the enemy is coming from", which is what every caller
    /// actually wants.
    /// </remarks>
    private bool NearestEnemySpawn(Team team, double fromX, double fromY, out double x, out double y)
    {
        var enemy = team == Team.Red ? Team.Blue : Team.Red;

        x = 0;
        y = 0;
        var best = -1.0;

        for (var group = 0; group < SpawnPoints.GroupCount; group++)
        {
            var count = _spawns.Count(enemy, group);

            for (var i = 0; i < count; i++)
            {
                if (!_spawns.TryGet(enemy, group, i, out var sx, out var sy))
                    continue;

                var d = Gml.PointDistance(sx, sy, fromX, fromY);

                if (best >= 0 && d >= best)
                    continue;

                best = d;
                x = sx;
                y = sy;
            }
        }

        return best >= 0;
    }

    /// <summary>Who on this team is carrying a briefcase, or -1.</summary>
    /// <remarks>One flag each, so team alone identifies the carrier.</remarks>
    private static int Carrier(in WorldView world, Team team)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (world.IsAlive(slot)
                && world.Player(slot).Team == team
                && world.Character(slot).CarryingIntel)
            {
                return slot;
            }
        }

        return -1;
    }
}
