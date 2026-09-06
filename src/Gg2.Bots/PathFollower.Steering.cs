using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

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
    private static void SteerFall(
        BotPathState state, bool onGround, in NavEdge edge,
        in NavNode from, in NavNode to, ref Steering steer)
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
            return;

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
                steer.Abandon = true;
                return;
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

        if (Math.Abs(column - takeoff) > BotConstants.JumpLead)
        {
            Approach(state, character, takeoff, needVx, along, column, in query, ref steer);
            return;
        }

        Gate(
            state, in character, in weapon, in edge, from: current, to: next, in from,
            takeoff, needVx, flightTicks, jumpDir, along, tick, in query, occupancy, ref steer);
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
        double along, int tick, in NavQuery query,
        BotOccupancy? occupancy, ref Steering steer)
    {
        // ⚠️ Counted here and nowhere else. The budget is ticks spent AT the gate
        // refusing to leave; counting the approach as well would spend it on a
        // long walk and fire a jump from the middle of the run, which is what
        // broke valley-spawn-to-point the first time the reference wrote this.
        state.TakeoffTicks++;

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

                    Launch(state, ref steer);
                    steer.MineDetonate = true;
                    state.StickySetup = -1;
                    state.StickyJumps++;
                }

                return;
            }

            state.Launches++;

            if (underspeed)
                state.LaunchesUnderspeed++;

            Launch(state, ref steer);

            // The hand-off: ScriptedBot applies this after the combat half has run,
            // which is the only place that can see both. ⚠️ Straight down is not a
            // simplification of a richer choice — `vectorfactor` in the rocket's
            // blast is sqrt(sin⁴ + cos⁴), which is 1 only when the impulse is
            // axis-aligned and falls to 0.707 at 45°, so an angled rocket jump
            // throws away up to 30% of its impulse. Down is also the one direction
            // with guaranteed geometry to detonate against: there is a floor under
            // the takeoff column by construction, and there may be nothing at all
            // down-and-left of it.
            if (isRocket)
            {
                steer.RocketFire = true;
                state.RocketJumps++;
            }
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
            // ⚠️ This used to clamp the run-up into the node's OWN columns, and
            // that clamp was the bug the whole change is about: a node one anchor
            // column wide, which is what the top step of a staircase is, resolves
            // the clamp back to the column the bot is already standing on, so the
            // run-up was a no-op and the jump happened from a standstill anyway.
            // JumpModel.RunupColumn follows WALK edges instead.
            state.RunupAt = state.RunupAt >= 0
                ? state.RunupAt
                : JumpModel.RunupColumn(
                    _graph, from, takeoff, jumpDir, BotConstants.RunupCells, in query);

            steer.TargetColumn = state.RunupAt;
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
            Launch(state, ref steer);
        }
    }

    /// <summary>Commits to the arc: ask for the press and reset the per-arc state.</summary>
    private static void Launch(BotPathState state, ref Steering steer)
    {
        steer.WantJump = true;
        state.AirTicks = 0;
        state.TakeoffTicks = 0;
        state.RunupAt = -1;
        state.Rejumped = false;
    }
}
