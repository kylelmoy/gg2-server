using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

public sealed partial class PathFollower
{
    /// <summary>What the per-edge steering decided this tick.</summary>
    /// <remarks>
    /// One struct rather than six locals threaded through arguments, because the
    /// takeoff gate writes four of them and the reference's own version is a
    /// block of <c>var</c>s shared by every branch.
    /// </remarks>
    private struct Steering
    {
        /// <summary>The anchor column to walk toward.</summary>
        public int TargetColumn;

        /// <summary>Press against your own motion instead of steering.</summary>
        public bool Braking;

        /// <summary>Ask for a jump this tick. Edge-triggered — see <see cref="Press"/>.</summary>
        public bool WantJump;

        /// <summary>A held flight's key: <see cref="HoldDir"/> replaces the steering keys.</summary>
        public bool Hold;

        /// <inheritdoc cref="Hold"/>
        public int HoldDir;

        /// <summary>Fly the plan instead of steering: <see cref="TrackX"/> replaces the keys.</summary>
        public bool Tracking;

        /// <summary>Where the validated trajectory says the bot should be by now.</summary>
        public double TrackX;

        /// <summary>
        /// Press nothing at all this tick.
        /// </summary>
        /// <remarks>
        /// ⚠️ The reference's mid-air bail is `return 0` from the whole of
        /// botPathKeys — no keys, not "no steering". Returning to the default
        /// steering instead leaves the bot walking toward the takeoff column while
        /// it falls, which is a press on an arc this branch exists to stop steering.
        /// </remarks>
        public bool Abandon;

        /// <summary>Apply the rocket arc's speed gate to the tracker's presses.</summary>
        public bool TrackGate;

        /// <summary>The arc's own horizontal velocity, for that gate.</summary>
        public double TrackNeedVx;

        /// <summary>The bot's speed along the jump, for that gate.</summary>
        public double TrackAlong;

        /// <summary>Which way the arc travels, for that gate.</summary>
        public int TrackDir;

        /// <summary>Ask the combat half to aim here and fire, for a rocket jump.</summary>
        /// <remarks>
        /// ⚠️ <b>The follower cannot fire.</b> It owns LEFT / RIGHT / JUMP / DOWN
        /// and the aim comes from combat alone, so a rocket jump has to be handed
        /// over rather than pressed — <c>botInputUpdate</c> applies both AFTER
        /// <c>botCombatUpdate</c> has run, which is the only place that can see
        /// both halves.
        /// </remarks>
        public bool RocketFire;

        /// <summary>Place a mine at the feet this tick — a sticky jump's phase one.</summary>
        public bool MineFire;

        /// <summary>
        /// Detonate this tick — phase two, and the same tick as the jump.
        /// </summary>
        public bool MineDetonate;
    }

    /// <summary>Walks off the end the edge names, then holds that column.</summary>
    /// <remarks>
    /// ⚠️ <b>Which end cannot be re-derived here</b>, which is why the edge
    /// carries it: "the column just past the run is inside the landing node" is
    /// true of BOTH ends wherever the landing surface reaches under the whole
    /// run — what a platform standing on a wider ledge looks like — and the old
    /// guess picked the right-hand end whenever it was ambiguous. On ctf_avanti
    /// that walked a bot into the solid block against its right end and wedged it
    /// there (stuck, edge blacklisted, ~1,100 ticks for a leg priced at 545) when
    /// the fall it wanted was off the left.
    /// <para>
    /// ⚠️ Off the edge the modelled trajectory is a STRAIGHT VERTICAL DROP from
    /// the takeoff column — the generator swept straight down from it and
    /// credited whatever surface it met — so the bot's job in the air is to hold
    /// that column, exactly the way a jump holds its arc. Without this a fall is
    /// the one edge kind flown with no plan at all: the bot walks off at whatever
    /// speed it had, keeps it, and sails past any landing narrower than the
    /// drift.
    /// </para>
    /// <para>
    /// ⚠️ The column held is the one the EDGE carries, not the walk-off hint:
    /// that is deliberately past the end of the run, and holding it would aim the
    /// drop a cell wide of the sweep that proved it.
    /// </para>
    /// </remarks>
    private void SteerFall(
        BotPathState state, in CharacterState character, bool onGround, in NavEdge edge,
        in NavNode from, in NavNode to, int next, ref Steering steer)
    {
        var takeoff = edge.Takeoff;

        if (takeoff < 0)
        {
            // A graph cached before falls carried a takeoff. The same guess as
            // before, which is right whenever only one end works: the column just
            // past the run, if that column is inside the landing.
            takeoff = from.X1 + 1 >= to.X0 && from.X1 + 1 <= to.X1
                ? from.X1 + 1
                : from.X0 - 1;
        }

        steer.TargetColumn = takeoff > from.X1
            ? from.X1 + BotConstants.EntryLead
            : from.X0 - BotConstants.EntryLead;

        if (onGround)
        {
            state.FallDir = 0;
            state.FallTracked = false;
            return;
        }

        // N12: held, as a jump is. The tracker holds the take-off column in the
        // air, so a bot that walked off at speed braked back over the edge: falls
        // kept 0.37 of their speed and braked hard on 28% of their air ticks
        // (gg2-rl docs/PLAN.md, N12). Now the bot keeps a key that the predictor
        // lands on the destination, and the tracker flies the rest of a fall only
        // once none does.
        if (HeldFalls && !state.FallTracked && Collision is { } world)
        {
            if (state.FallDir == 0)
            {
                state.FallDir = character.HSpeed > 0.5 ? 1
                    : character.HSpeed < -0.5 ? -1
                    : NavGraph.ColumnWorldX((to.X0 + to.X1) / 2) >= character.X ? 1 : -1;
                state.FallKey = state.FallDir;
            }

            Span<int> order = [state.FallKey, state.FallDir, 0, -state.FallDir];

            foreach (var key in order)
            {
                if (FlightPredictor.Land(world, in character, key, false, out var x, out var y, out _)
                    && _graph.NodeFromWorld(x, y) == next)
                {
                    state.FallKey = key;
                    state.HeldFallTicks++;
                    steer.Hold = true;
                    steer.HoldDir = key;
                    state.FlyingEdge = true;
                    return;
                }
            }

            state.FallTracked = true;
            state.HeldFallFallbacks++;
        }

        steer.TrackX = NavGraph.ColumnWorldX(takeoff);
        steer.Tracking = true;
        state.FlyingEdge = true;
    }

    /// <summary>
    /// A jump or double-jump arc: the takeoff gate on the ground, the
    /// position tracker in the air.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An arc is flown as a TRAJECTORY, not as a speed.</b> The generator
    /// validated one specific arc — leave the takeoff column, cover
    /// <see cref="NavEdge.Bucket"/> pixels of ground a tick, be over the landing
    /// when it comes back down — and every clearance cell it checked and every
    /// surface it ruled out is about that arc and no other. Holding a speed
    /// instead is what the reference used to do and is not the same thing: the
    /// bot starts from a standstill, takes four or five ticks to reach a slow
    /// arc's speed, and any nudge in the air is permanent. Tracking a
    /// <em>position</em> corrects both, because being behind is a reason to press
    /// and being ahead is a reason to brake. <b>Measured over every class, rise,
    /// ledge width and run-up: 12-19% of jumps landed on the node they were aimed
    /// at before, 100% after.</b>
    /// </remarks>
    private void SteerJump(
        BotPathState state, in CharacterState character, in WeaponState weapon, in NavEdge edge,
        in NavNode from, in NavNode to, int current, int next,
        int column, int dirToNext, int tick,
        in NavQuery query, BotOccupancy? occupancy, ref Steering steer)
    {
        // ⚠️ The takeoff column is carried ON THE EDGE because the generator
        // SEARCHED for it rather than assuming it. It is usually the end of the
        // run facing the landing — but where the landing surface is also what
        // ends the run (climbing onto a crate) the body is flush against it there
        // and the only flyable arcs start a few cells back. Re-deriving it from
        // the node's own columns would jump from a column the build already
        // rejected and quietly turn those edges back into fictions.
        var takeoff = edge.Takeoff >= 0 ? edge.Takeoff : (dirToNext >= 0 ? from.X1 : from.X0);

        // ⚠️ Ticks are counted rather than read off vspeed, which would otherwise
        // give the phase of the arc for free: vspeed pins at 10 once the fall
        // reaches terminal velocity, and every phase after that reads as the same
        // tick.
        var needVx = edge.Bucket;
        var flightTicks = edge.Ticks;
        var jumpDistance = needVx * flightTicks;

        // ⚠️ The generator's own direction, not the one inferred from node
        // midpoints: it is the side of the takeoff column the landing is on, and
        // a wide surface can straddle the midpoint test.
        var jumpDir = dirToNext;

        if (to.X0 > takeoff)
            jumpDir = 1;
        else if (to.X1 < takeoff)
            jumpDir = -1;

        // ⚠️ Speed ALONG THE JUMP, not speed. This is the whole of the run-up
        // problem: the old gate was `abs(hspeed) <= needVx + 1`, which is
        // satisfied by a bot drifting the wrong way at 2px/tick — so it left the
        // ground with backwards momentum and the tracker spent the arc undoing
        // it, and GG2 gives almost no authority in the air. Reported from play at
        // ctf_truefort 4470,840: "the bot has momentum going left, and can't gain
        // enough speed going right to make the jump to the right stairs."
        var along = character.HSpeed * jumpDir;
        var isRocket = edge.Type == NavEdgeType.RocketJump;

        // Where the planned arc comes down. A held flight may land anywhere on
        // the destination from its near end up to a little past this, and no
        // further: the route after it was planned from about here, and a body
        // that lands far past it at full speed has to come back for the next
        // edge — cp_dirtbowl 335 -> 320 -> 323 overran the small ledge it was
        // walking back to and fell, every attempt.
        var landPlan = NavGraph.ColumnWorldX(takeoff) + (jumpDir * jumpDistance);

        // ...unless the route goes on the same way, where landing further on is
        // landing closer to the next edge, which is what a person does over a gap.
        var overshoot = HeldOvershoot(state, landPlan, jumpDir);

        if (!character.OnGround)
        {
            state.AirTicks++;

            // ⚠️ The rocket did not go off, so this is not the arc that was
            // planned. `moveStatus` 1 is "rocketing/mining myself" and is set by
            // the blast itself, so a rocket-jump edge in the air without it is an
            // ORDINARY jump — 57px of rise against the arc's 276 — and steering it
            // toward a trajectory it cannot be on only carries the bot further
            // from where an ordinary jump was always going to put it.
            //
            // The landing is then adjudicated by machinery that already exists and
            // already has the right policy: it comes down on a surface the route
            // does not mention, the off-route branch fires, and that branch gives
            // the edge ONE retry before blacklisting it — which is exactly right
            // here, because the common causes are transient (an enemy walked into
            // the rocket, an evasive hop got there first) and the uncommon one is
            // not.
            //
            // ⚠️ `AirTicks > 1` rather than `>= 1`: the blast lands in the same
            // tick's simulation as the takeoff press, so tick one is always
            // moveStatus 0.
            if ((isRocket || edge.Type == NavEdgeType.StickyJump)
                && state.AirTicks > 1
                && character.MoveStatus != 1)
            {
                // ⚠️ Once per attempt, not once per tick. AirTicks > 1 is the
                // condition above, so tick two is the first that can see a dud and
                // counting only there makes this a count of ARCS rather than of
                // ticks spent in one — the mistake that made the first reading of
                // this counter say "1036% of takeoffs".
                if (state.AirTicks == 2)
                    state.RocketDuds++;

                steer.Abandon = true;
                return;
            }

            // A held flight keeps its key while that still lands on the
            // destination, then coasts, then counter-steers; the tracker takes
            // over only if none of them lands. A scout's second jump goes when
            // its plan says (N3), and is re-timed, or found, if the plan stops
            // landing. First, so the generator's own second jump below is the
            // tracker's alone.
            if (state.HeldDir != 0
                && (edge.Type == NavEdgeType.Jump || (HeldDoubles && edge.Type == NavEdgeType.DoubleJump))
                && Collision is { } air)
            {
                if (HeldKey(air, state, in character, in query, next, landPlan, jumpDir, overshoot, out var press) is { } key)
                {
                    if (press)
                    {
                        steer.WantJump = true;
                        state.Rejumped = true;
                        state.DoubleJumps++;
                    }

                    steer.Hold = true;
                    steer.HoldDir = key;
                    state.FlyingEdge = true;
                    return;
                }

                state.HeldDir = 0;
                state.HeldFallbacks++;
            }

            // ⚠️ The second impulse, on a double-jump arc. Rejump is the tick the
            // generator proved the arc at, counted from takeoff the same way
            // AirTicks is, and -1 on every other edge kind — so this is inert for
            // the others without testing the type.
            //
            // ⚠️ It is a PRESS, not a hold. Begin Step takes the second jump on
            // the rising edge and refuses it while `vspeed <= -jumpStrength`, so
            // holding JUMP through the arc would consume the edge on the takeoff
            // tick and leave nothing to press. The bit was released the moment
            // WantJump went false at takeoff, which is what makes setting it again
            // here an edge.
            //
            // ⚠️ CanDoubleJump is checked even though the search will not hand
            // these edges to anything else: a route outlives the plan that made
            // it, and a bot can change class mid-life while a path is still
            // installed. Pressing JUMP on a class with no second jump is a wasted
            // bit rather than a bug, but the arc it is flying is then a fiction
            // and the sooner it fails the off-route test the better.
            if (edge.Rejump >= 0 && !state.Rejumped && query.CanDoubleJump
                && state.AirTicks >= edge.Rejump)
            {
                steer.WantJump = true;
                state.Rejumped = true;
                state.DoubleJumps++;
            }

            // Where the plan says to be now, clamped at the landing so a bot that
            // is late keeps pressing toward it rather than aiming past it.
            steer.TrackX = NavGraph.ColumnWorldX(takeoff)
                + (jumpDir * Math.Min(jumpDistance, needVx * state.AirTicks));

            steer.Tracking = true;

            // ⚠️ The rocket arc's tracker is SPEED-GATED where every other arc's
            // is not, and it is the same gate RocketModel.Reach models. With no
            // friction under it, a press that has already reached the arc's own vx
            // gains nothing — an unfrictioned character just holds it — and
            // pressing on from there is what made the real tracker limit-cycle.
            //
            // ⚠️ The test is on speed ALONG THE JUMP, so which of the tracker's two
            // branches is catching up and which is braking depends on jumpDir: an
            // arc travelling left is behind when the bot is to the RIGHT of the
            // plan. `along` already carries that sign.
            steer.TrackGate = isRocket;
            steer.TrackNeedVx = needVx;
            steer.TrackAlong = along;
            steer.TrackDir = jumpDir;

            state.FlyingEdge = true;
            return;
        }

        state.HeldDir = 0;
        state.HeldRejump = -1;
        state.HeldSwitchAt = 0;

        // N1: jump where a person would. Near the takeoff, if jumping now and
        // holding toward the landing comes down on the destination, go: no
        // speed ceiling, no braking, no run-up. JumpHeld is checked because a
        // press on the tick after one is not a press, and the prediction assumed
        // one. N3: a scout's plans include a second jump, timed as people time
        // theirs, on double-jump arcs and plain ones alike. N4: and where no
        // one key lands, rise first and steer onto the ledge after.
        if (HeldFlight
            && (edge.Type == NavEdgeType.Jump || (HeldDoubles && edge.Type == NavEdgeType.DoubleJump))
            && Collision is { } ground
            && !state.JumpHeld
            && GroundUnder(ground, in character)
            && Math.Abs(column - takeoff) <= BotConstants.RunupCells)
        {
            Span<int> options = stackalloc int[MaxLaunchOptions];
            options = options[..LaunchOptions(state, HeldDoubles && query.CanDoubleJump, current, next, options)];

            // N5: or walk up to the step and hop from standing, as people do.
            if (FaceHops && FaceHop(state, ground, in character, jumpDir, current, next, takeoff, landPlan, overshoot, options, ref steer))
                return;

            if (CanLaunchHeld(ground, in character, jumpDir, next, landPlan, overshoot, options, out var rejump, out var early, out var switchAt))
            {
                // N2: a held jump would land, but a person would not take it yet
                // if running on still gains speed and the jump will still be there
                // a tick from now. Keep running toward the landing instead: the
                // approach below would brake to the arc's own speed.
                if (!LastOrFastest(ground, in character, jumpDir, current, next, landPlan, overshoot, options))
                {
                    state.HeldDeferred++;
                    steer.Hold = true;
                    steer.HoldDir = jumpDir;
                    return;
                }

                Launch(state, ref steer, rocket: false);
                state.HeldDir = jumpDir;
                state.HeldKey = jumpDir;
                state.HeldRejump = rejump;
                state.HeldEarly = early;
                state.HeldSwitchAt = switchAt;
                state.HeldLaunches++;
                state.Launches++;

                if (rejump >= 0)
                    state.HeldDoubles++;

                steer.Hold = true;
                steer.HoldDir = jumpDir;
                return;
            }
        }

        if (Math.Abs(column - takeoff) > BotConstants.JumpLead)
        {
            Approach(state, character, takeoff, needVx, along, column, in query, ref steer);
            return;
        }

        Gate(
            state, in character, in weapon, in edge, from: current, to: next, in from,
            takeoff, needVx, flightTicks, jumpDir, along, column, tick, in query, occupancy,
            ref steer);
    }

    /// <summary>Walking toward the takeoff column, or backing up to a run-up mark.</summary>
    private void Approach(
        BotPathState state, in CharacterState character, int takeoff, double needVx, double along,
        int column, in NavQuery query, ref Steering steer)
    {
        if (state.RunupAt >= 0)
        {
            // Backing up. Two halves, and the switch between them is what makes
            // it a run-up rather than a retreat: walk to the mark, then turn round
            // and accelerate at the takeoff column over the whole distance just
            // walked.
            //
            // Arrival is BOT_ENTRY_LEAD rather than exact, for the same reason
            // every other target column is: a cell is six world pixels and a bot
            // can sit inside any sane tolerance of a column without being on it.
            if (Math.Abs(column - state.RunupAt) <= BotConstants.EntryLead)
            {
                state.RunupAt = -1;
                steer.TargetColumn = takeoff;
            }
            else
            {
                steer.TargetColumn = state.RunupAt;
            }

            return;
        }

        // Approaching. Arrive at about the arc's speed rather than sprinting at
        // the gate and braking on top of it: a bot that arrives at 9 and only then
        // starts shedding speed drifts past the one-cell window while it does,
        // backs up, and comes at it again.
        //
        // ⚠️ Only over the last stretch, and that distance is load-bearing.
        // Bleeding speed for the whole leg makes every walk between two jumps
        // happen at 2-4 px/tick instead of 7-9 — measured, it cost 30-50% on all
        // three valley scenarios while changing nothing about whether they
        // arrived. BOT_RUNUP_CELLS does double duty: the distance a bot needs to
        // build the arc's speed is also the distance it needs to shed it.
        if (along > needVx + BotConstants.JumpVtol
            && Math.Abs(column - takeoff) <= BotConstants.RunupCells)
        {
            steer.Braking = true;
        }
        else
        {
            steer.TargetColumn = takeoff;
        }
    }

    /// <summary>
    /// At the takeoff column. Three ways this can go, and only one is a jump.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The rule is "not moving away from the jump", not "moving at exactly
    /// the arc's speed", and the difference is measured.</b> GG2 applies the same
    /// control factor in the air as on the ground, so a bot that leaves slow can
    /// be pressed back up to the arc's speed within two or three ticks by the
    /// tracker — which is why slow takeoffs were already landing. A bot that
    /// leaves with speed <em>against</em> the jump cannot: it spends those ticks
    /// undoing the drift and covers a good ten pixels the wrong way first, on an
    /// arc whose height is already fixed. Demanding the full speed at the gate was
    /// tried and costs a lot for nothing — 647 → 868 and 674 → 997 ticks on the
    /// valley scenarios, with no more arrivals.
    /// <para>
    /// ⚠️ <c>along &gt;= 0</c> accepts a STANDSTILL, which is right for nine arcs
    /// in ten and catastrophic for the rest — an arc asking for most of the
    /// class's ceiling has no surplus for the tracker to make the acceleration
    /// ramp back out of. Measured over the cached graphs, <b>3,310 of 62,069 jump
    /// edges are unflyable by a heavy that way, and on koth_corinth 96% of a
    /// heavy's routes to the point cross one</b>. So the speed test is
    /// <see cref="JumpModel.Reach"/>, which replays the tracker against the speed
    /// the bot has right now — and <c>along &gt;= 0</c> is kept on top of it,
    /// because the reach model is honest about distance but knows nothing about
    /// the clearance cells the generator sampled along the arc, and a bot that
    /// leaves backwards and catches up flies through none of them.
    /// </para>
    /// </remarks>
    private void Gate(
        BotPathState state, in CharacterState character, in WeaponState weapon, in NavEdge edge,
        int from, int to,
        in NavNode fromNode, int takeoff, double needVx, double flightTicks, int jumpDir,
        double along, int column, int tick, in NavQuery query,
        BotOccupancy? occupancy, ref Steering steer)
    {
        // ⚠️ Counted here and nowhere else. The budget is ticks spent AT the gate
        // refusing to leave; counting the approach as well would spend it on a
        // long walk and fire a jump from the middle of the run, which is what
        // broke valley-spawn-to-point the first time the reference wrote this.
        state.TakeoffTicks++;

        // Re-key the run-up count if this is a different arc from the one it was
        // counting for. Done here rather than on the change-of-edge test so it
        // survives a re-plan onto the same edge — see BotPathState.RunupsStarted.
        if (state.RunupsEdgeFrom != from || state.RunupsEdgeTo != to)
        {
            state.RunupsEdgeFrom = from;
            state.RunupsEdgeTo = to;
            state.RunupsStarted = 0;
        }

        // ⚠️ How much ground this jump has to cover, measured from the takeoff
        // COLUMN and not from the bot's x — the difference is not cosmetic. The
        // tracker's target is clamped at needVx * flightTicks from the anchor, so
        // that is a hard ceiling on how far the bot can be steered. Plenty of arcs
        // are costed with no margin at all (koth_gallery n86 → n58 is 0.70 px/tick
        // over 17.2 ticks against a 12px gap: exactly 12 for exactly 12), so
        // asking from x — up to BOT_JUMP_LEAD cells further out at the gate —
        // asks for 22px of an arc that can never deliver more than 12. That
        // refuses the jump on every approach, runs up, comes back and refuses
        // again, silently: nothing blacklisted and nothing off route, because the
        // bot is doing exactly what it was told.
        var landingColumn = jumpDir > 0 ? _graph.Nodes[to].X0 : _graph.Nodes[to].X1;
        var need = Math.Abs(NavGraph.ColumnWorldX(landingColumn) - NavGraph.ColumnWorldX(takeoff));

        // ...minus whatever ground the bot has already covered INSIDE the takeoff
        // column.
        //
        // ⚠️ A column is not a point. Every x across one whole cell — six world
        // pixels — resolves to the same anchor column, and ColumnWorldX hands back
        // the NEAR end of that run rather than its middle, so the line above
        // assumes the bot is at the near end of a 6px band. Crediting the
        // difference is sound because the tracker aims at an ABSOLUTE world x, not
        // at a displacement from wherever the bot left the ground: leaving from
        // further along does not move the landing point, it means there is less
        // ground between the bot and it.
        //
        // Worth having rather than a rounding tidy-up: a one-column perch that
        // overhangs the way it is jumping gives no run-up at all, so every arc off
        // one is gated at a standstill, and six pixels of floor is the difference
        // between 0 and about 2.3 px/tick for a heavy. Modelled over the
        // twenty-four cached graphs, 209 of the 293 arcs otherwise refused become
        // flyable once the gate stops asking for six pixels the bot has already
        // walked.
        //
        // ⚠️ Only ever a credit, never a debit, and clamped to one cell. Behind
        // the anchor the honest number is bigger, and asking for it is the mistake
        // above: an arc costed with no margin can never deliver more, and a gate
        // that asks for it refuses on every approach for ever.
        var lead = jumpDir * (character.X - NavGraph.ColumnWorldX(takeoff));
        lead = Math.Clamp(lead, 0, NavConstants.CellSize);
        need = Math.Max(0, need - lead);

        var isRocket = edge.Type == NavEdgeType.RocketJump;

        // ⚠️ Below the arc's own takeoff speed. The gate accepts this — `along >= 0`
        // admits a standstill and the reach model is what decides whether the
        // tracker can make it up — so this is not a refusal, it is a record of how
        // often the arc is being flown on the model's word rather than on speed.
        var underspeed = along < needVx - BotConstants.JumpVtol;

        // ⚠️ Asked of the TERRAIN, not of the graph. A node's span ends for gates
        // and walls as well as for the edge of a run, so "the takeoff column is the
        // node's last column" is true of most arcs — used as the test it made the
        // corpus sweep 38% worse, because it re-aimed the arcs that already worked.
        // What matters is only whether the blast has anything to go off against:
        // the rocket is created 20px along the aim, so aimed down it spawns at the
        // body's centre, and at the lip of a run there is nothing under it.
        var rocketAim = RocketAimDegrees;

        if (isRocket && Collision is { } terrain && !FloorUnder(terrain, character.X, character.Y))
            rocketAim = RocketAimDegrees - (jumpDir * LipAimDegrees);

        // ⚠️ WHICH reach model, and it is not a detail. A rocket-jumping character
        // is under moveStatus 1, where Begin Step's switch gives controlFactor 0.65
        // and frictionFactor 1 — and a friction of 1 means horizontal speed does not
        // bleed at all for the whole flight. JumpModel models the ordinary
        // 0.85/1.15 and would be wrong in BOTH directions here: it under-rates how
        // well a slow takeoff catches up (nothing takes the speed back off) and
        // over-rates how easily a hot one sheds. RocketModel's own remarks have the
        // numbers.
        var isSticky = edge.Type == NavEdgeType.StickyJump;

        // ⚠️ A mine jump flies under moveStatus 1 exactly as a rocket jump does —
        // controlFactor 0.65, friction 1 — so it takes the SAME reach model. The
        // one thing a rocket has that a mine does not is NAV_RJ_SPEEDUP, which
        // RocketModel.Reach does not use. StickyModel.Reach delegates rather
        // than copying, so there is one integration to keep correct.
        var reach = isRocket
            ? RocketModel.Reach(in character, needVx, flightTicks, along)
            : isSticky
                ? StickyModel.Reach(in character, needVx, flightTicks, along)
                : JumpModel.Reach(in character, needVx, flightTicks, along);

        // ⚠️ Jumping without the rocket is the WORST available outcome. The arc
        // rises 276px where an ordinary jump rises 57, so the bot would leave the
        // ground, track a trajectory it cannot be on, and land somewhere the route
        // does not mention — which costs an off-route fire and, on the second one,
        // a blacklist on a perfectly good edge. So the gate refuses to leave rather
        // than leaving badly.
        var rocketReady = !isRocket || RocketModel.Ready(in character, in weapon);

        // ⚠️ The same refusal for a sticky jump, and it matters MORE here: the
        // arc needs a mine that has already been placed and settled, so leaving
        // the ground without one is not merely a short jump, it is a short jump
        // that also wasted the setup.
        //
        // ⚠️⚠️ BUT ONLY TO *START* ONE. Once StickySetup is running the mine is
        // already on the ground, and the launcher's readiness has nothing to do
        // with the arc — which matters because PLACING the mine puts the weapon
        // straight onto its refire cooldown. Requiring readiness throughout made
        // the gate close on the tick after the placement, every single time, and
        // the abandon branch below then threw the setup away. Zero sticky jumps
        // across 48 real edges on four maps, and the synthetic unit test could
        // not see it because its fixture weapon is
        // permanently ready.
        var stickyReady = !isSticky
            || state.StickySetup >= 0
            || StickyModel.Ready(in character, in weapon);

        if (rocketReady
            && stickyReady
            && along >= 0
            && along <= needVx + BotConstants.JumpVtol
            && reach + BotConstants.JumpReachTol >= need)
        {
            // ⚠️ A STICKY JUMP DOES NOT LEAVE ON THE TICK ITS GATE OPENS, and it
            // is the only edge kind that does not. The mine has to be placed and
            // has to settle first, so the gate opening starts a countdown and the
            // launch happens NavConstants.SjSetupTicks later. Everything else
            // here — the reach model, the run-up, the patience timer — is shared,
            // which is why this is a branch inside the gate rather than a second
            // gate beside it.
            if (isSticky)
            {
                if (state.StickySetup < 0)
                {
                    // Phase one: put the mine down and stand still. ⚠️ -1 rather
                    // than 0 is "not set up", so a zeroed state cannot read as a
                    // mine that has already settled.
                    steer.MineFire = true;
                    state.StickySetup = NavConstants.SjSetupTicks;
                }
                else if (--state.StickySetup > 0)
                {
                    // ⚠️ Decrement THEN test, so the launch lands exactly
                    // SjSetupTicks after the placing tick. Testing first and
                    // decrementing after waits one tick longer than the cost the
                    // generator charged for the mine's travel, and an arc costed
                    // for five ticks flown after six is a different arc.
                }
                else
                {
                    // Phase two, and both presses are THIS tick: Begin Step runs
                    // its jump branch before its right-click branch, so the
                    // blast's motion_add lands on top of `vspeed = -jumpStrength`.
                    // A tick later would throw the jump away.
                    state.Launches++;

                    if (underspeed)
                        state.LaunchesUnderspeed++;

                    Launch(state, ref steer, rocket: false);
                    steer.MineDetonate = true;
                    state.StickySetup = -1;
                    state.StickyJumps++;
                }

                return;
            }

            state.Launches++;

            if (underspeed)
                state.LaunchesUnderspeed++;

            Launch(state, ref steer, isRocket, rocketAim);
        }
        else if (isSticky && state.StickySetup >= 0)
        {
            // ⚠️ The gate closed with a mine already down. Abandon rather than
            // stand on a live mine waiting for a window that has moved: the
            // countdown is only meaningful for the approach that started it, and
            // the off-route machinery gives the edge one retry before blacklisting
            // it, which is the right policy for a transient.
            state.StickySetup = -1;
            steer.Abandon = true;
        }
        // ⚠️ Waiting for the launcher, and NOT falling through to the run-up below.
        // Refire is 30 ticks against BOT_TAKEOFF_PATIENCE's 60, so the overwhelmingly
        // common reason to be here is a rocket that is a second away — and the answer
        // is to stand on the takeoff column and wait for it. Letting this case reach
        // the run-up would walk the bot away from a jump it is about to be able to
        // make and back again, which is the shuffle the patience budget exists to end
        // rather than a thing to spend it on.
        //
        // Standing still is safe here where it is not elsewhere: TakeoffTicks is
        // counting, so this cannot become a bot waiting for ever, and the stuck
        // detector is not fooled — it fires only when keys are pressed and nothing
        // moves, and braking to a stop presses nothing once the bot is already still.
        // ⚠️ AND a sticky jump whose launcher is cold, which this branch was
        // rocket-only for and should never have been. A demoman arriving at a
        // takeoff just after firing — or freshly spawned, where the refire starts
        // cold — has exactly the rocket's problem: the manoeuvre is a second away
        // and the right answer is to stand on the takeoff column and wait for it.
        // Falling through to the run-up instead walks the bot off the column and
        // back, burning the takeoff patience budget until the edge is blacklisted,
        // which is a refusal with no bad arc anywhere in it.
        else if ((isRocket && !rocketReady) || (isSticky && !stickyReady))
        {
            // ⚠️ BRAKING, where the reference writes `targetCol = takeoffCol`, and
            // the two are the same behaviour in two codebases whose default
            // steering differs. The reference's default is a pure column
            // comparison, so aiming at the column the bot is already on presses
            // nothing — which is what its own comment relies on ("braking to a stop
            // presses nothing once the bot is already still", and "the stuck
            // detector is not fooled"). This port's default is a DIRECTION HINT
            // rather than a place to stop: it presses on along the route even when
            // the bot is exactly on its target column, on purpose, because a cell is
            // six world pixels and standing still on top of one is how a bot never
            // quite reaches the node. Measured: aiming at the column here pressed
            // RIGHT for twelve consecutive ticks and the stuck detector banned a
            // perfectly good edge at tick 12, forty-eight ticks before the patience
            // budget it was supposed to be spending.
            //
            // Braking is the behaviour the reference describes: press against your
            // own motion, and press nothing once still. A bot that has drifted off
            // the column stops here and is walked back by Approach on the next tick,
            // once it is more than BOT_JUMP_LEAD cells away.
            state.GateColdWeapon++;
            steer.Braking = true;
        }
        else if (along > needVx + BotConstants.JumpVtol)
        {
            // Faster than the arc allows. Worth bleeding off even though the
            // tracker can correct it: with no key held GG2 sheds horizontal speed
            // by only about 13% a tick, so a bot arriving at 9 is still over 3
            // some eight ticks later.
            state.GateTooFast++;
            steer.Braking = true;
        }
        else
        {
            // Split for the diagnostic only — the answer below is the same one
            // either way, but "leaving backwards" and "cannot reach" are different
            // faults and the repair for them is not the same.
            if (along < 0)
                state.GateBackwards++;
            else
                state.GateShort++;

            // Moving away from the jump, or too slow for this arc. ⚠️ Backing up
            // is the only answer that works: pressing forward from here would walk
            // the bot off the end of the run it is standing on, which is usually a
            // ledge, and jumping anyway is what produced the short arcs in the
            // first place — the bot arcs out with its old momentum still on it and
            // lands back where it started.
            //
            // ⚠️ A bot still SHORT OF the takeoff column and still gaining on the
            // jump does not need a run-up. It needs the rest of the run it is
            // already on, and starting a run-up here throws that run away.
            //
            // The gate opens BOT_JUMP_LEAD cells out, so a class whose
            // acceleration needs the whole run is judged up to a cell before it
            // gets there — and the refusal is self-perpetuating, because the
            // run-up puts it back at the same mark to arrive at the same speed.
            // Measured on ctf_truefort's staircase to the intel: a heavy comes off
            // the full six-cell run-up at 3.19 px/tick at column 147 against the
            // 3.5 that edge 597->557 wants, and clears 3.5 about two ticks later
            // at the takeoff column itself. Before this it shuttled over the same
            // 22px five times, ~106 ticks, and only left when a re-plan routed
            // round it.
            //
            // ⚠️ Walking the last cell is safe in the way that walking on FROM the
            // takeoff column is not. The comment above is about the lip: the
            // takeoff column is where the run ends, and pressing forward there
            // walks off it. This is the cell BEHIND that, which is still floor —
            // it is the approach, arriving one tick late.
            //
            // ⚠️ Only once a run-up has already been spent AND has stopped
            // helping, and both halves are load-bearing.
            //
            // On a first approach a bot may be short for any number of reasons and
            // a run-up is the right answer; walking to the lip first to find out is
            // a wasted round trip, and without the count it cost gallery-pit-climb
            // 216 ticks -> 260 and truefort-spawn-to-intel 1,568 -> 1,606.
            //
            // ⚠️ Tried and dropped: also requiring that the last run-up failed to
            // improve the speed at the gate, so a run-up still doing its job could
            // repeat. It sounds strictly better and measures worse — census repeats
            // 2,152 -> 2,163, koth_gallery's own repeats 57 -> 66, and
            // truefort-spawn-to-intel 1,548 -> 1,609 — because an approach that
            // merely TIES the best is the common case and the extra condition
            // mostly just delays the fix by a lap. docs/TRAPS-BOTS.md has the table.
            var shortOfTakeoff = (takeoff - column) * jumpDir > 0;

            if (along > 0 && shortOfTakeoff && state.RunupsStarted > 0)
            {
                steer.TargetColumn = takeoff;
            }
            else
            {
                // ⚠️ This used to clamp the run-up into the node's OWN columns, and
                // that clamp was the bug the whole change is about: a node one anchor
                // column wide, which is what the top step of a staircase is, resolves
                // the clamp back to the column the bot is already standing on, so the
                // run-up was a no-op and the jump happened from a standstill anyway.
                // JumpModel.RunupColumn follows WALK edges instead.
                if (state.RunupAt < 0)
                {
                    state.RunupAt = JumpModel.RunupColumn(
                        _graph, from, takeoff, jumpDir, BotConstants.RunupCells, in query);

                    state.RunupsStarted++;
                }

                steer.TargetColumn = state.RunupAt;
            }
        }

        // ⚠️ The escape hatch, because a gate that can refuse is a gate that can
        // refuse FOR EVER — a bot on a run too short to build the speed, or wedged
        // against a wall it cannot back away from, would otherwise work the gate
        // for the rest of the round with every other detector reading it as
        // healthy: it is not stuck (it is moving) and it is not off route (it is
        // standing exactly where the route says). Jump on whatever it has instead.
        // That either works, or it lands back where it started and the off-route
        // machinery already knows what to do with an edge that keeps doing that.
        if (state.TakeoffTicks >= BotConstants.TakeoffPatience)
        {
            // ⚠️ Two seconds at the gate with no rocket is not a bot to shove off
            // the edge — it would fly an ordinary jump on a 276px arc. The edge is
            // taken away instead, which is the honest description of it:
            // untraversable right now, by a bot that has been trying for two
            // seconds. The search re-plans without it, the ban expires in
            // BOT_BLACKLIST_TICKS, and by then the launcher has reloaded several
            // times over.
            if (isRocket && !rocketReady)
            {
                state.BlacklistEdge(from, to, tick);
                state.BlacklistFires++;
                Plan(state, character.X, character.Y, tick, query, occupancy);
                return;
            }

            state.Launches++;

            if (underspeed)
                state.LaunchesUnderspeed++;

            steer.Braking = false;

            // ⚠️⚠️ WITH the rocket, and this line is a bug fix rather than a
            // tidy-up. The hand-off used to live at the gate-open call site only,
            // so a rocket arc whose launcher was READY but whose speed clause
            // refused for two seconds left the ground here with no rocket — an
            // ordinary 57px jump on an arc that rises 276, which this method's own
            // remarks call the worst available outcome and which the cold-launcher
            // branch above exists to prevent. Measured on cp_dirtbowl e583: the bot
            // jumped at moveStatus 0, rose 56px against the 101 the arc needed, took
            // no self-damage, landed back where it started, and did it again.
            Launch(state, ref steer, isRocket, rocketAim);
        }
    }

    /// <summary>
    /// Is there anything under the muzzle for a straight-down blast to hit?
    /// </summary>
    /// <remarks>
    /// The muzzle is <c>RocketMuzzleOffset</c> (20px) below the origin when the aim
    /// is straight down, and the floor a grounded character stands on is about 24px
    /// below it — so the scan starts at the muzzle and runs a little past the feet.
    /// ⚠️ It asks at the body's own x because that is where the rocket is created;
    /// asking at the anchor column would answer for a point the blast never
    /// occupies, which is the whole of this bug.
    /// </remarks>
    private static bool FloorUnder(CollisionWorld terrain, double x, double y)
    {
        var column = (int)Gml.Round(x);

        for (var below = 20; below <= 32; below++)
        {
            if (terrain.IsSolidAt(column, (int)Gml.Round(y) + below))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The key a held flight presses this tick: the one it pressed last if that
    /// still lands on <paramref name="next"/>, else the held direction, else
    /// none, else the other way; null when nothing lands there.
    /// </summary>
    /// <remarks>
    /// Keeping last tick's key first is what makes the flight hold rather than
    /// flip: a coast that lands is not traded back for a press that also lands.
    /// Every key is flown with the planned second jump, if one is still to come.
    /// When none lands, a scout with its second jump left tries it now, a little
    /// later, or not at all, the way a person rescues a jump (N3).
    /// <paramref name="press"/> is whether the second jump goes this tick.
    /// </remarks>
    private int? HeldKey(
        CollisionWorld world, BotPathState state, in CharacterState character, in NavQuery query, int next,
        double landPlan, int jumpDir, double overshoot, out bool press)
    {
        press = false;
        var pending = 0;

        if (state.HeldRejump >= 0 && !state.Rejumped)
        {
            pending = Math.Max(1, state.HeldRejump - state.AirTicks + 1);

            // A press on the tick after a press is a hold: put it off a tick.
            if (pending == 1 && state.JumpHeld)
                pending = 2;
        }

        // N4: a plan still in its first phase (let go, or pressed away, until
        // the switch tick) keeps it while it still lands.
        if (state.HeldSwitchAt > state.AirTicks)
        {
            if (HeldLands(
                    world, in character, state.HeldEarly, state.HeldEarly, state.HeldSwitchAt - state.AirTicks, state.HeldKey,
                    jump: false, pending, next, landPlan, jumpDir, overshoot))
            {
                press = pending == 1;
                return state.HeldEarly;
            }
        }

        state.HeldSwitchAt = 0;

        if (HeldKeyWith(world, state, in character, next, landPlan, jumpDir, overshoot, pending) is { } key)
        {
            press = pending == 1;
            return key;
        }

        // N4: no one key lands. Rise first, then steer onto the ledge.
        Span<int> earlies = [0, -state.HeldDir];

        foreach (var early in earlies[..(HeldSwitchPlans ? 2 : 0)])
        {
            foreach (var k in AirSwitchTicks)
            {
                if (HeldLands(world, in character, early, early, k, state.HeldDir, jump: false, pending, next, landPlan, jumpDir, overshoot))
                {
                    state.HeldEarly = early;
                    state.HeldSwitchAt = state.AirTicks + k;
                    state.HeldKey = state.HeldDir;
                    state.HeldSwitches++;
                    press = pending == 1;
                    return early;
                }
            }
        }

        if (!HeldDoubles || !query.CanDoubleJump || character.DoubleJumpUsed || state.Rejumped)
            return null;

        foreach (var r in RescueTicks)
        {
            if (r == pending || (r == 1 && state.JumpHeld))
                continue;

            if (HeldKeyWith(world, state, in character, next, landPlan, jumpDir, overshoot, r) is { } rescued)
            {
                state.HeldRejump = r == 0 ? -1 : state.AirTicks + r - 1;
                state.HeldRescues++;
                press = r == 1;
                return rescued;
            }
        }

        return null;
    }

    /// <summary>
    /// The second jumps tried when a held flight's plan stops landing, as
    /// predictor ticks: now, two, four or six ticks on, or none.
    /// </summary>
    private static readonly int[] RescueTicks = [1, 3, 5, 7, 0];

    /// <summary>How long a flight's first phase may last when re-planned in the air, in ticks (N4).</summary>
    /// <remarks>Longest first: steering over once clear of the ledge lands slowest, and nearest the plan.</remarks>
    private static readonly int[] AirSwitchTicks = [13, 10, 7, 5, 3, 1];

    /// <summary>The air tick a take-off's first phase may last until (N4).</summary>
    /// <remarks>Longest first, as <see cref="AirSwitchTicks"/>.</remarks>
    private static readonly int[] LaunchSwitchTicks = [14, 11, 8, 6, 4, 2];

    /// <summary><see cref="HeldKey"/>'s key order, flown with a second jump on predictor tick <paramref name="rejump"/> (0 none).</summary>
    private int? HeldKeyWith(
        CollisionWorld world, BotPathState state, in CharacterState character, int next, double landPlan, int jumpDir,
        double overshoot, int rejump)
    {
        Span<int> order = [state.HeldKey, state.HeldDir, 0, -state.HeldDir];

        foreach (var key in order)
        {
            if (HeldLands(world, in character, key, key, 1, key, jump: false, rejump, next, landPlan, jumpDir, overshoot))
            {
                state.HeldKey = key;
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a take-off now, pressed toward the landing, has a key plan that
    /// comes down in the landing window: held, let go, or reversed after the
    /// first tick, with each of <paramref name="options"/>' second jumps in
    /// turn. The flight then picks between them tick by tick. If none lands,
    /// let go or pressed away until a switch tick, then toward (N4), with no
    /// second jump and with the first planned one.
    /// </summary>
    /// <param name="rejump">The air tick of the planned second jump, or −1 for none.</param>
    /// <param name="early">The key before the switch tick.</param>
    /// <param name="switchAt">The air tick the first phase lasts until, or 0 for a one-phase plan.</param>
    private bool CanLaunchHeld(
        CollisionWorld world, in CharacterState character, int jumpDir, int next, double landPlan, double overshoot,
        ReadOnlySpan<int> options, out int rejump, out int early, out int switchAt)
    {
        var c = character;
        var drawn = -1;
        foreach (var r in options)
        {
            if (r >= 0)
            {
                drawn = r;
                break;
            }
        }

        early = 0;
        switchAt = 0;

        // N7: where the draw puts a second jump first, every second-jump plan,
        // one key and two-phase, goes before any single one. Otherwise a single
        // held jump that lands wins over the rise, jump again and steer over
        // that a person makes from standing at a step. The single plan is last.
        if (options.Length > 1 && options[0] >= 0)
        {
            if (Constant(options[..^1], out rejump))
                return true;

            if (Switched(drawn, out early, out switchAt))
            {
                rejump = drawn;
                return true;
            }

            if (Constant(options[^1..], out rejump))
                return true;

            rejump = -1;
            return Switched(-1, out early, out switchAt);
        }

        if (Constant(options, out rejump))
            return true;

        rejump = -1;
        if (Switched(-1, out early, out switchAt))
            return true;

        if (drawn >= 0 && Switched(drawn, out early, out switchAt))
        {
            rejump = drawn;
            return true;
        }

        return false;

        // Held, let go, or reversed after the first tick, with each second jump
        // in turn. Air tick r is predictor tick r + 1: tick 1 is the take-off.
        bool Constant(ReadOnlySpan<int> rejumps, out int found)
        {
            Span<int> then = [jumpDir, 0, -jumpDir];

            foreach (var r in rejumps)
            {
                foreach (var key in then)
                {
                    if (HeldLands(world, in c, jumpDir, key, 1, key, jump: true, r < 0 ? 0 : r + 1, next, landPlan, jumpDir, overshoot))
                    {
                        found = r;
                        return true;
                    }
                }
            }

            found = -1;
            return false;
        }

        // N4: let go or pressed away until air tick S, then toward. Air tick S
        // is predictor tick S + 1, so the first phase covers predictor ticks 2
        // to S: earlyTicks is S.
        bool Switched(int r, out int e, out int s)
        {
            Span<int> earlies = [0, -jumpDir];

            if (HeldSwitchPlans)
            {
                foreach (var candidate in earlies)
                {
                    foreach (var at in LaunchSwitchTicks)
                    {
                        if (HeldLands(world, in c, jumpDir, candidate, at, jumpDir, jump: true, r < 0 ? 0 : r + 1, next, landPlan, jumpDir, overshoot))
                        {
                            e = candidate;
                            s = at;
                            return true;
                        }
                    }
                }
            }

            e = 0;
            s = 0;
            return false;
        }
    }

    /// <summary>
    /// The second jumps a take-off may plan, in the order tried, as air ticks
    /// (−1 for none). Written into <paramref name="options"/>; returns how many.
    /// </summary>
    /// <remarks>
    /// A class with no second jump plans one jump. A scout draws, once per
    /// attempt at an edge, whether to try a double jump first
    /// (<see cref="DoubleShare"/>) and when to take it
    /// (<see cref="RejumpStyle"/>), then tries ticks around that one. The fleet's
    /// people double jump on 46% of their out-of-combat jumps, over steps, gaps
    /// and flat ground alike, mostly a little before the top of the arc
    /// (<c>gg2-rl</c> <c>docs/PLAN.md</c>, N3). The draw hashes the bot, the edge
    /// and its launch count, so it holds through an approach and a sweep stays
    /// deterministic.
    /// </remarks>
    private static int LaunchOptions(BotPathState state, bool doubles, int current, int next, Span<int> options)
    {
        if (!doubles)
        {
            options[0] = -1;
            return 1;
        }

        var h = Mix((uint)state.ReplanStagger, (uint)current, (uint)next, (uint)state.Launches);
        var drawn = RejumpStyle[(int)(h % (uint)RejumpStyle.Length)];
        var doubleFirst = ((h >> 16) & 0xFFFF) < DoubleShare * 65536;
        var count = 0;

        if (!doubleFirst)
            options[count++] = -1;

        foreach (var d in RejumpSpread)
        {
            if (drawn + d >= 2)
                options[count++] = drawn + d;
        }

        if (doubleFirst)
            options[count++] = -1;

        return count;
    }

    /// <summary>The share of scout jumps that try a double jump first: the fleet's people's.</summary>
    private const double DoubleShare = 0.46;

    /// <summary>
    /// The air tick of a person's second jump, at about evenly spaced quantiles
    /// of the fleet's scouts (5th to 95th percentile).
    /// </summary>
    private static readonly int[] RejumpStyle = [5, 6, 7, 8, 9, 10, 11, 12, 14, 16, 19, 23];

    /// <summary>Offsets from the drawn tick tried after it, nearest first.</summary>
    private static readonly int[] RejumpSpread = [0, -2, 2, -4, 4, 8];

    /// <summary>One plan with no second jump and every <see cref="RejumpSpread"/>.</summary>
    private const int MaxLaunchOptions = 7;

    private static uint Mix(uint a, uint b, uint c, uint d)
    {
        var h = (a * 0x9E3779B1u) ^ (b * 0x85EBCA77u) ^ (c * 0xC2B2AE3Du) ^ (d * 0x27D4EB2Fu);
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    /// <summary>
    /// Whether a feasible held take-off should go now: running on would gain
    /// almost nothing, would leave the node it jumps from, or would leave no held
    /// jump that lands (N2).
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Leaving the node is not leaving the ground.</b> On ctf_truefort's
    /// stairs (edge 70 -> 9) the take-off is a one-column step, and a tick more
    /// toward the landing walks the bot down onto the step below, still on the
    /// ground: it lost the edge, re-planned and climbed back, eight blacklists
    /// and 190 extra ticks.
    /// </remarks>
    private bool LastOrFastest(
        CollisionWorld world, in CharacterState character, int jumpDir, int current, int next, double landPlan,
        double overshoot, ReadOnlySpan<int> options)
    {
        var ahead = character;

        if (!FlightPredictor.Run(world, ref ahead, jumpDir) || !GroundUnder(world, in ahead)
            || _graph.NodeFromWorld(ahead.X, ahead.Y) != current)
        {
            return true;
        }

        if ((ahead.HSpeed - character.HSpeed) * jumpDir < AtSpeed)
            return true;

        return !CanLaunchHeld(world, in ahead, jumpDir, next, landPlan, overshoot, options, out _, out _, out _);
    }

    /// <summary>A tick's speed gain below this is running flat out, in px/tick.</summary>
    private const double AtSpeed = 0.15;

    /// <summary>
    /// A hop while running along the route (N8): drawn at
    /// <see cref="RunHopStart"/> a tick, or chained after the last one landed,
    /// and taken at the first tick within <see cref="RunHopWait"/> that a held hop
    /// comes down on the route ahead (N8c).
    /// </summary>
    /// <remarks>
    /// People hop on flat ground while running, 2.5 times per 1,000 running ticks
    /// out of combat, and 55% of those follow another jump's landing, after a
    /// median of 18 ground ticks (<c>gg2-rl</c> <c>docs/PLAN.md</c>, N8). The
    /// draws hash the bot, the tick and the route position, so a run stays
    /// deterministic.
    /// <para>
    /// ⚠️ <b>Most running is toward a jump's take-off, not along a walk edge</b>
    /// (71% against 24%), and a floor is often several short nodes, so a hop may
    /// land anywhere on the route ahead that walk edges reach (N8b). Where the
    /// route leaves that node by a jump, the landing must leave the run-up for it.
    /// </para>
    /// <para>
    /// ⚠️ <b>A draw is not a hop.</b> Only a third of draws had room at the tick
    /// they were drawn, so a drawn hop waits for room, as a person does who has
    /// decided to hop (N8c).
    /// </para>
    /// </remarks>
    private void RunHop(
        BotPathState state, CollisionWorld world, in CharacterState character, int dir, int tick, ref Steering steer)
    {
        if (state.RunHopChain >= 0)
        {
            if (state.RunHopChain-- > 0)
                return;

            state.RunHopPending = RunHopChainWait;
            state.RunHopPendingChained = true;
        }

        if (state.RunHopPending < 0)
        {
            var h = Mix((uint)state.ReplanStagger ^ RunHopSalt, (uint)tick, (uint)state.PathAt, (uint)state.Path[state.PathAt]);
            if ((h & 0xFFFF) >= RunHopStart * 65536)
                return;

            state.RunHopPending = RunHopWait;
            state.RunHopPendingChained = false;
        }

        // Counted down here, so a pending hop that never finds room lapses.
        state.RunHopPending--;

        if (state.JumpHeld || character.HSpeed * dir <= RunHopSpeed || !GroundUnder(world, in character)
            || !FlightPredictor.Land(world, in character, dir, dir, jump: true, out var x, out var y, out _)
            || !RunHopLands(state, x, y, dir))
        {
            return;
        }

        steer.WantJump = true;
        steer.Hold = true;
        steer.HoldDir = dir;
        state.RunHopAir = true;
        state.RunHopDir = dir;
        state.RunHopPending = -1;
        state.RunHopsTaken++;

        if (state.RunHopPendingChained)
            state.RunHopsChained++;
    }

    /// <summary>
    /// Whether a running hop landing at (<paramref name="x"/>, <paramref name="y"/>)
    /// is on the route, reached from here by walk edges alone, with room left.
    /// </summary>
    private bool RunHopLands(BotPathState state, double x, double y, int dir)
    {
        var at = _graph.NodeFromWorld(x, y);
        if (at < 0)
            return false;

        var path = state.Path;

        for (var j = state.PathAt; j < path.Count && j <= state.PathAt + RunHopReach; j++)
        {
            if (j > state.PathAt && !(TryFindEdge(_graph, state, path[j - 1], path[j], out var walked) && walked.Type == NavEdgeType.Walk))
                return false;

            if (path[j] != at)
                continue;

            var node = _graph.Nodes[at];

            if (j + 1 < path.Count && TryFindEdge(_graph, state, path[j], path[j + 1], out var leaving)
                && leaving.Type is NavEdgeType.Jump or NavEdgeType.DoubleJump or NavEdgeType.RocketJump or NavEdgeType.StickyJump)
            {
                var to = _graph.Nodes[path[j + 1]];
                var takeoff = leaving.Takeoff >= 0 ? leaving.Takeoff
                    : (to.X0 + to.X1) >= (node.X0 + node.X1) ? node.X1 : node.X0;

                return (NavGraph.ColumnWorldX(takeoff) - x) * dir >= RunHopRunup;
            }

            var end = dir > 0 ? NavGraph.ColumnWorldX(node.X1) - x : x - NavGraph.ColumnWorldX(node.X0);
            return end >= RunHopRoom;
        }

        return false;
    }

    /// <summary>A running hop has landed: draw whether another follows, and after how long.</summary>
    private static void RunHopLanded(BotPathState state, int tick)
    {
        state.RunHopAir = false;

        var h = Mix((uint)state.ReplanStagger ^ RunHopSalt, (uint)tick, (uint)state.RunHopsTaken, 0x9E37u);
        state.RunHopChain = ((h >> 16) & 0xFFFF) < RunHopChainShare * 65536
            ? RunHopGaps[(int)((h & 0xFFFF) % (uint)RunHopGaps.Length)]
            : -1;
    }

    /// <summary>
    /// The chance a tick of running draws a hop. People's rate before chains is
    /// about 0.0011 a running tick; this allows for the running a hop is not
    /// offered on and draws that find no room (N8c, N8d: 1.27 at 0.0033, 2.32
    /// here, people 2.50, same-level flat hops a 1,000 running ticks).
    /// </summary>
    private const double RunHopStart = 0.008;

    /// <summary>Ticks a drawn hop waits for room, and a chained one.</summary>
    private const int RunHopWait = 30;

    /// <inheritdoc cref="RunHopWait"/>
    private const int RunHopChainWait = 10;

    /// <summary>The share of running hops followed by another, as people's.</summary>
    private const double RunHopChainShare = 0.55;

    /// <summary>Ground ticks before a chained hop, at about people's quantiles (median 18).</summary>
    private static readonly int[] RunHopGaps = [3, 5, 7, 10, 14, 18, 22, 27, 33, 47];

    /// <summary>Running is faster than this toward the next node, in px/tick.</summary>
    private const double RunHopSpeed = 2;

    /// <summary>A hop must land at least this far before its node's far end, in px.</summary>
    private const double RunHopRoom = 24;

    /// <summary>...and this far short of the take-off of a jump that leaves its node, in px.</summary>
    private const double RunHopRunup = 48;

    /// <summary>How many nodes on along the route a hop may land.</summary>
    private const int RunHopReach = 4;

    /// <summary>Keeps the running-hop draws apart from the others.</summary>
    private const uint RunHopSalt = 0x40B5C0DEu;

    /// <summary>
    /// Whether a jump pressed now leaves the ground (N6): solid one pixel under the
    /// body, which is the physics' own start-of-tick test.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not <see cref="CharacterState.OnGround"/>, which is set at the start of
    /// a tick, before its move.</b> After the run past a lip it still reads true,
    /// and a press there is a scout's mid-air jump: the predictor lands it, so N2
    /// deferred onto it, and 52% of scouts' gap second jumps came on the take-off
    /// tick, spent before the planned one (<c>gg2-rl</c> <c>docs/PLAN.md</c>, N6).
    /// </remarks>
    private static bool GroundUnder(CollisionWorld world, in CharacterState c) =>
        !world.PlaceFree(c.Box, c.X, c.Y + 1, c.Filter);

    /// <summary>
    /// A face hop (N5): run into the step, stand, then hop from standing. Returns
    /// whether it steered this tick; false hands the tick to the running launch.
    /// </summary>
    /// <remarks>
    /// A third of people's out-of-combat step jumps start within a pixel of the
    /// step's face and from a standstill, after a median of 9 still ticks
    /// (<c>gg2-rl</c> <c>docs/PLAN.md</c>, N5). Drawn once per attempt at an edge
    /// with <see cref="FaceShare"/>, and kept only if the predictor finds the
    /// face (running toward the landing stops against a wall, on the ground and
    /// in the take-off node) and a held plan that lands from standing there.
    /// <para>
    /// ⚠️ <b>The wait is with no keys.</b> Held toward the wall, the stuck test
    /// counts a pressed key that does not move the bot and blacklists the edge
    /// after <see cref="BotConstants.StuckTicks"/>, shorter than many waits.
    /// </para>
    /// </remarks>
    private bool FaceHop(
        BotPathState state, CollisionWorld world, in CharacterState character, int jumpDir, int current, int next,
        int takeoff, double landPlan, double overshoot, ReadOnlySpan<int> options, ref Steering steer)
    {
        if (state.FaceFrom != current || state.FaceTo != next || state.FaceLaunch != state.Launches)
        {
            state.FaceFrom = current;
            state.FaceTo = next;
            state.FaceLaunch = state.Launches;
            state.FaceTicks = 0;
            state.FaceStill = 0;

            var h = Mix((uint)state.ReplanStagger ^ FaceSalt, (uint)next, (uint)current, (uint)state.Launches);
            state.FaceWait = FaceWaits[(int)(h % (uint)FaceWaits.Length)];
            state.FacePlan = ((h >> 16) & 0xFFFF) < FaceShare * 65536
                && FaceReachable(world, in character, jumpDir, current, next, takeoff, landPlan, overshoot, options);

            if (state.FacePlan)
                state.FaceHops++;
        }

        if (!state.FacePlan)
            return false;

        if (++state.FaceTicks > FacePatience)
        {
            state.FacePlan = false;
            return false;
        }

        // At the face: a tick pressed toward it would not move the bot.
        var ahead = character;
        var moves = !FlightPredictor.Run(world, ref ahead, jumpDir) || Math.Abs(ahead.X - character.X) >= FaceStillPx;

        if (moves || !character.OnGround || Math.Abs(character.HSpeed) >= FaceStillPx)
        {
            if (!ahead.OnGround || _graph.NodeFromWorld(ahead.X, ahead.Y) != current)
            {
                state.FacePlan = false;
                return false;
            }

            state.FaceStill = 0;
            steer.Hold = true;
            steer.HoldDir = moves ? jumpDir : 0;
            return true;
        }

        if (++state.FaceStill <= state.FaceWait)
        {
            steer.Hold = true;
            steer.HoldDir = 0;
            return true;
        }

        state.FacePlan = false;

        if (!CanLaunchHeld(world, in character, jumpDir, next, landPlan, overshoot, options, out var rejump, out var early, out var switchAt))
            return false;

        Launch(state, ref steer, rocket: false);
        state.HeldDir = jumpDir;
        state.HeldKey = jumpDir;
        state.HeldRejump = rejump;
        state.HeldEarly = early;
        state.HeldSwitchAt = switchAt;
        state.HeldLaunches++;
        state.FaceLaunches++;
        state.Launches++;

        if (rejump >= 0)
            state.HeldDoubles++;

        steer.Hold = true;
        steer.HoldDir = jumpDir;
        return true;
    }

    /// <summary>
    /// Whether running toward the landing from here stops against a face, on the
    /// ground in <paramref name="current"/> and near the take-off, and a held plan
    /// lands from standing there.
    /// </summary>
    private bool FaceReachable(
        CollisionWorld world, in CharacterState character, int jumpDir, int current, int next, int takeoff,
        double landPlan, double overshoot, ReadOnlySpan<int> options)
    {
        var c = character;

        for (var t = 0; t < FaceReach; t++)
        {
            var before = c.X;

            if (!FlightPredictor.Run(world, ref c, jumpDir) || !c.OnGround || _graph.NodeFromWorld(c.X, c.Y) != current)
                return false;

            if (Math.Abs(c.X - before) < FaceStillPx && Math.Abs(c.HSpeed) < FaceStillPx)
            {
                if (Math.Abs(NavGraph.AnchorColumn(c.X) - takeoff) > BotConstants.RunupCells)
                    return false;

                // Stood there with no keys, as the bot will.
                for (var s = 0; s < 2; s++)
                {
                    if (!FlightPredictor.Run(world, ref c, 0))
                        return false;
                }

                return CanLaunchHeld(world, in c, jumpDir, next, landPlan, overshoot, options, out _, out _, out _);
            }
        }

        return false;
    }

    /// <summary>
    /// The share of valid face hops taken. All of them (N5b): a face hop is valid on
    /// only part of the step jumps, and at 0.35 bots stood at the face for 15% of
    /// them against people's 33%; at 1.0, 37%.
    /// </summary>
    private const double FaceShare = 1.0;

    /// <summary>Ticks stood at the face, at about people's quantiles (median 9; a quarter past 15).</summary>
    private static readonly int[] FaceWaits = [2, 3, 4, 6, 8, 10, 13, 16, 19, 24];

    /// <summary>Ticks of running the face must be found within, and a face hop launched within.</summary>
    private const int FaceReach = 40;

    /// <inheritdoc cref="FaceReach"/>
    private const int FacePatience = 60;

    /// <summary>Movement below this, in px or px/tick, is standing.</summary>
    private const double FaceStillPx = 0.5;

    /// <summary>Keeps the face draw apart from <see cref="LaunchOptions"/>' on the same edge.</summary>
    private const uint FaceSalt = 0x5A17F00Du;

    /// <summary>
    /// Whether a held plan comes down in the landing window and, where the route
    /// does not go on the way the jump goes, can stop there.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Landing on the node is not staying on it.</b> A two-phase plan
    /// (N4) that let go early and pressed toward late came down 8px from the
    /// far end of cp_dirtbowl's node 355 at 4.9 px/tick, slid off, and fell to
    /// the floor below: edge 352 -> 355, 41 ticks before and stranded after.
    /// So the landing is stepped on, braking, until the bot is all but still,
    /// and it must stay on the node.
    /// </remarks>
    private bool HeldLands(
        CollisionWorld world, in CharacterState character, int firstDir, int earlyDir, int earlyTicks, int dir, bool jump,
        int rejump, int next, double landPlan, int jumpDir, double overshoot)
    {
        if (!FlightPredictor.Fly(world, in character, firstDir, earlyDir, earlyTicks, dir, jump, rejump, out var landed, out _)
            || !InLandingWindow(landed.X, landed.Y, next, landPlan, jumpDir, overshoot))
        {
            return false;
        }

        if (double.IsPositiveInfinity(overshoot))
            return true;

        // Where the route turns back, the window is about where the bot comes
        // to rest, not only where it touches down: ctf_waterway 382 -> 344 ->
        // 351 landed inside it at 5.4 px/tick, stopped past it, and the run
        // back overran the narrow 351 and fell, every attempt.
        return StopsOn(world, _graph, landed, next, out var restX)
            && (restX - landPlan) * jumpDir <= overshoot;
    }

    /// <summary>Whether a character that has just landed brakes to a stop without leaving <paramref name="node"/>.</summary>
    /// <remarks>
    /// The node, not merely the floor: tried as "does not fall", the shipping
    /// arm strands 455 and the scout arm 190; as "stays on the node", 467 and
    /// 167.
    /// </remarks>
    private static bool StopsOn(CollisionWorld world, NavGraph graph, CharacterState c, int node, out double restX)
    {
        var y0 = c.Y;
        restX = c.X;

        for (var t = 0; t < StopTicks; t++)
        {
            // On the ground: braking in the air off the far end passes through
            // zero speed too (gg_debug 28 -> 12).
            if (Math.Abs(c.HSpeed) < Stopped && c.OnGround)
            {
                restX = c.X;
                return true;
            }

            if (!FlightPredictor.Run(world, ref c, c.HSpeed > 0 ? -1 : 1))
                return false;

            var at = graph.NodeFromWorld(c.X, c.Y);

            if (at != node && (at >= 0 || c.Y > y0 + (2 * NavConstants.CellSize)))
                return false;
        }

        return false;
    }

    /// <summary>Ticks of braking allowed after a landing, and the speed that counts as stopped (px/tick).</summary>
    private const int StopTicks = 30;

    /// <inheritdoc cref="StopTicks"/>
    private const double Stopped = 0.5;

    /// <summary>On the destination, and no more than <paramref name="overshoot"/> past the planned landing.</summary>
    private bool InLandingWindow(double x, double y, int next, double landPlan, int jumpDir, double overshoot) =>
        _graph.NodeFromWorld(x, y) == next && (x - landPlan) * jumpDir <= overshoot;

    /// <summary>
    /// How far past the planned landing a held flight may come down, in px.
    /// </summary>
    /// <remarks>
    /// Anywhere on the destination when the route goes on the way the jump goes,
    /// or the goal lies further on; otherwise two cells, because the next edge
    /// turns back and a body that lands far past the plan at full speed overruns
    /// it (cp_dirtbowl 335 -> 320 -> 323). Two cells too where a rocket or sticky
    /// jump follows within <see cref="StandingLookahead"/> edges. A finite window
    /// also asks where the bot comes to rest (<see cref="HeldLands"/>).
    /// </remarks>
    private double HeldOvershoot(BotPathState state, double landPlan, int jumpDir)
    {
        var after = state.PathAt + 2;

        if (after < state.Path.Count)
        {
            var node = _graph.Nodes[state.Path[after]];
            var afterX = NavGraph.ColumnWorldX((node.X0 + node.X1) / 2);

            if ((afterX - landPlan) * jumpDir <= 0)
                return HeldSlack;

            // ...but not when a standing take-off is close ahead: a rocket or
            // sticky jump is gated from the take-off column at the arc's own
            // speed, and a bot that lands at full speed runs past it. ctf_truefort
            // 365 -> 299, then two one-step walks to 285's rocket jump: overran,
            // climbed on to 276, and banned two edges.
            for (var i = state.PathAt + 1; i + 1 < state.Path.Count && i <= state.PathAt + StandingLookahead; i++)
            {
                if (TryFindEdge(_graph, state, state.Path[i], state.Path[i + 1], out var ahead)
                    && ahead.Type is NavEdgeType.RocketJump or NavEdgeType.StickyJump)
                {
                    return HeldSlack;
                }
            }

            return double.PositiveInfinity;
        }

        return Math.Max(HeldSlack, ((state.GoalX - landPlan) * jumpDir) + HeldSlack);
    }

    /// <summary>The overshoot allowed where the route turns back, in px: two cells.</summary>
    private const double HeldSlack = 12;

    /// <summary>How many edges past the landing are checked for a standing take-off.</summary>
    private const int StandingLookahead = 4;

    /// <summary>Commits to the arc: ask for the press and reset the per-arc state.</summary>
    /// <param name="rocket">
    /// Ask for the rocket as well. ⛔ <b>Not optional, and it is a parameter rather
    /// than the caller's business precisely because it used to be.</b> Two call
    /// sites launched — the gate opening and the patience hatch — and only the
    /// first fired; the second flew rocket arcs as ordinary jumps for as long as
    /// the code has existed.
    /// </param>
    /// <remarks>
    /// ⚠️ Straight down is not a simplification of a richer choice —
    /// <c>vectorfactor</c> in the rocket's blast is sqrt(sin⁴ + cos⁴), which is 1
    /// only when the impulse is axis-aligned and falls to 0.707 at 45°, so an
    /// angled rocket jump throws away up to 30% of its impulse. Down is also the
    /// one direction with guaranteed geometry to detonate against: there is a
    /// floor under the takeoff column by construction, and there may be nothing at
    /// all down-and-left of it. The aim itself is applied by
    /// <see cref="PathFollower.ApplyLaunch"/>, which is the hand-off
    /// <c>ScriptedBot</c> runs after its combat half — the only place that can see
    /// both.
    /// </remarks>
    private static void Launch(BotPathState state, ref Steering steer, bool rocket, double aim = RocketAimDegrees)
    {
        steer.WantJump = true;

        if (rocket)
        {
            steer.RocketFire = true;
            state.RocketAim = aim;
            state.RocketJumps++;
        }

        state.AirTicks = 0;
        state.TakeoffTicks = 0;
        state.RunupAt = -1;
        state.Rejumped = false;

        // The arc was flown, so whatever it took to get off the ground is spent
        // and the next approach to it starts again.
        state.RunupsStarted = 0;
        state.RunupsEdgeFrom = -1;
        state.RunupsEdgeTo = -1;
    }
}
