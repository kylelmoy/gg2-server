namespace Gg2.Protocol;

/// <summary>
/// The <c>NAV_*</c> block of <c>Constants.xml</c>: nav-graph layout, cache
/// format and the jump/rocket-jump model the offline generator uses.
/// </summary>
/// <remarks>
/// These are transcribed verbatim, prefix stripped, because the values are a
/// tuned set with real playtesting behind them (docs/ARCHITECTURE.md, "Bots").
/// The C# bot layer is free to replace the algorithms; it should not re-derive
/// these numbers.
/// <para>
/// <c>NAV_RJ_IMPULSE</c> in particular was <em>measured</em>, not derived. The
/// long comment above it in <c>Constants.xml</c> explains why 5.285 rather than
/// Rocket's nominal knockback of 8; read it before touching
/// <see cref="RjImpulse"/>.
/// </para>
/// </remarks>
public static class NavConstants
{
    public const int CellSize = 6;   // NAV_CELL_SIZE
    public const int BoxW = 4;   // NAV_BOX_W
    public const int BoxH = 6;   // NAV_BOX_H
    public const int NodeFields = 6;   // NAV_NODE_FIELDS
    public const int NodeY = 0;   // NAV_NODE_Y
    public const int NodeX0 = 1;   // NAV_NODE_X0
    public const int NodeX1 = 2;   // NAV_NODE_X1
    public const int NodeFlags = 3;   // NAV_NODE_FLAGS
    public const int NodeDoor = 4;   // NAV_NODE_DOOR
    public const int NodeGate = 5;   // NAV_NODE_GATE
    public const int DoorNone = 0;   // NAV_DOOR_NONE
    public const int DoorLeft = 1;   // NAV_DOOR_LEFT
    public const int DoorRight = 2;   // NAV_DOOR_RIGHT
    public const int GateNone = 0;   // NAV_GATE_NONE
    public const int GateTeamRed = 1;   // NAV_GATE_TEAM_RED
    public const int GateTeamBlue = 2;   // NAV_GATE_TEAM_BLUE
    public const int GateIntelRed = 3;   // NAV_GATE_INTEL_RED
    public const int GateIntelBlue = 4;   // NAV_GATE_INTEL_BLUE
    public const int GateSetup = 5;   // NAV_GATE_SETUP
    public const int EdgeFields = 9;   // NAV_EDGE_FIELDS
    public const int EdgeFrom = 0;   // NAV_EDGE_FROM
    public const int EdgeTo = 1;   // NAV_EDGE_TO
    public const int EdgeType = 2;   // NAV_EDGE_TYPE
    public const int EdgeBucket = 3;   // NAV_EDGE_BUCKET
    public const int EdgeTicks = 4;   // NAV_EDGE_TICKS
    public const int EdgeCost = 5;   // NAV_EDGE_COST
    public const int EdgeGate = 6;   // NAV_EDGE_GATE
    public const int EdgeTakeoff = 7;   // NAV_EDGE_TAKEOFF
    public const int EdgeRejump = 8;   // NAV_EDGE_REJUMP
    public const int EdgeWalk = 0;   // NAV_EDGE_WALK
    public const int EdgeFall = 1;   // NAV_EDGE_FALL
    public const int EdgeJump = 2;   // NAV_EDGE_JUMP
    public const int EdgeDropthrough = 3;   // NAV_EDGE_DROPTHROUGH
    public const int EdgeMovebox = 4;   // NAV_EDGE_MOVEBOX
    public const int EdgeDoublejump = 5;   // NAV_EDGE_DOUBLEJUMP
    public const int EdgeRocketjump = 6;   // NAV_EDGE_ROCKETJUMP
    public const int EdgeStickyjump = 7;   // NAV_EDGE_STICKYJUMP
    public const int CacheVersion = 15;   // NAV_CACHE_VERSION
    public const int BuildIdle = 0;   // NAV_BUILD_IDLE
    public const int BuildDone = 9;   // NAV_BUILD_DONE
    public const int MaxFall = 150;   // NAV_MAX_FALL
    public const double JumpV0 = 8.3;   // NAV_JUMP_V0
    public const double JumpGravity = 0.6;   // NAV_JUMP_GRAVITY
    public const double JumpVx = 4.53;   // NAV_JUMP_VX
    public const int JumpTermVy = 10;   // NAV_JUMP_TERM_VY
    public const int JumpMaxTicks = 60;   // NAV_JUMP_MAX_TICKS
    public const int JumpSamples = 10;   // NAV_JUMP_SAMPLES
    public const int JumpPenalty = 6;   // NAV_JUMP_PENALTY
    public const int JumpMaxPerSide = 4;   // NAV_JUMP_MAX_PER_SIDE
    public const int JumpDiversityRows = 6;   // NAV_JUMP_DIVERSITY_ROWS
    public const int JumpTakeoffTries = 4;   // NAV_JUMP_TAKEOFF_TRIES
    public const int JumpLandLead = 2;   // NAV_JUMP_LAND_LEAD
    public const int DjMaxTicks = 90;   // NAV_DJ_MAX_TICKS
    public const int DjTickTries = 5;   // NAV_DJ_TICK_TRIES
    public const int DjMaxPerSide = 2;   // NAV_DJ_MAX_PER_SIDE
    public const int DjPenalty = 40;   // NAV_DJ_PENALTY
    public const int DjSkipHops = 3;   // NAV_DJ_SKIP_HOPS
    public const double RjImpulse = 5.285;   // NAV_RJ_IMPULSE
    public const double RjSpeedup = 1.06;   // NAV_RJ_SPEEDUP
    public const double RjGravity = 0.54;   // NAV_RJ_GRAVITY
    public const double RjControl = 0.65;   // NAV_RJ_CONTROL
    public const int RjFriction = 1;   // NAV_RJ_FRICTION
    public const int RjDamage = 30;   // NAV_RJ_DAMAGE

    /// <summary>
    /// How much A\* inflates its heuristic. 1.0 is an ordinary optimal search.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This is a deliberate quality-for-speed trade and it has no reference
    /// behind it</b> — <c>navFindPath.gml</c> searches at 1.0 and re-opens closed
    /// nodes. CLAUDE.md puts the bot layer outside the "the reference is the
    /// specification" rule, and the user asked for the speed.
    /// <para>
    /// <b>Measured 2026-09-02</b>, twelve bots on <c>ctf_truefort</c>, against 1.0
    /// with re-opening: the bots' whole <c>Poll</c> costs <b>−17.3%</b> and the
    /// tick <b>−10.1%</b>, for graph routes <b>4.0% more expensive on average</b>
    /// (worst seen 1.71×). ✅ <b>Every route that existed is still found</b> — the
    /// found-counts over all 25 shipped graphs are identical, 19,468 routes.
    /// </para>
    /// <para>
    /// ⛔ <b>The statistic that chose 1.3 is the SUITE TOTAL, because every
    /// per-leg number is noise.</b> Summed walking time over
    /// <c>ScenarioSuiteTests</c>' fourteen legs: <b>6,259 ticks at 1.0 and 6,142
    /// at 1.3</b>, with 6,261 / 6,234 / 6,255 / 6,306 at 1.0-skip / 1.05 / 1.1 /
    /// 1.2 — a ±2% band that does not move in one direction — and <b>no leg fails
    /// at any weight tried</b>. At <b>1.5 it is 6,416, +2.5%</b>, and that is
    /// where it starts to cost.
    /// </para>
    /// <para>
    /// ⚠️ <b>Two single-leg pins disagree at 1.3 and both are inside that band</b>:
    /// <c>BotObjectiveTests</c>' intel run got 6% FASTER (1,697 → 1,598) and
    /// <c>PathFollowerFixtureTests</c>' leg 14% slower (1,572 → 1,786). **Do not
    /// tune on either.** ⛔ Nor on the suite's rocket-jump count: it is 6 at 1.0,
    /// 7 at 1.2, 7 at 1.25, 6 again at 1.3, 7 at 1.35, then 9 at 1.4 — it wobbles
    /// inside a band before it steps, so a weight picked because it landed on 6
    /// is fitted to noise. What the step at 1.4 is good for is staying clear of
    /// it.
    /// </para>
    /// <para>
    /// ⚠️ <b>What would show this is wrong</b>: bots visibly detouring, or taking
    /// rocket jumps they do not need — the arcs cost hp, and an inflated heuristic
    /// commits to greedy-looking routes earlier. Lower it before looking anywhere
    /// else.
    /// </para>
    /// </remarks>
    public const double SearchWeight = 1.3;
    public const int RjMaxTicks = 100;   // NAV_RJ_MAX_TICKS
    public const int RjMaxPerSide = 2;   // NAV_RJ_MAX_PER_SIDE
    public const int RjPenalty = 10;   // NAV_RJ_PENALTY
    public const int RjSkipHops = 1;   // NAV_RJ_SKIP_HOPS

    // ⚠️ The sticky-jump family, added to the fork by `gg2-nav-gen` on
    // 2026-08-28. These are the values the FORK DECLARES, carried faithfully
    // because ConstantsTests compares them against Constants.xml — they are not
    // this port's opinion of them.
    //
    // ⚠️ SjImpulse WAS 10 UNTIL THIS PORT MEASURED IT. The generator derived it,
    // said so, and asked for the launch velocity to be measured before the edges
    // were trusted to a bot; MineJumpTests flew the manoeuvre here and read
    // -14.460 px/tick, an apex of 193.6 px where 10 implies 18.3 and ~310. The
    // fork was corrected to 6.16 the same day — 14.46 minus the 8.3 jump — and
    // SjMaxTicks came down 130 to 100 with it.
    //
    // ⚠️ So this value now AGREES with the measurement, and that agreement is
    // young. `StickyModel.Launch` is what the follower actually flies on, and it
    // is the measured post-tick velocity rather than anything derived from this.
    public const double SjImpulse = 6.16;   // NAV_SJ_IMPULSE
    public const int SjDamage = 25;   // NAV_SJ_DAMAGE
    public const int SjMaxTicks = 100;   // NAV_SJ_MAX_TICKS
    public const int SjMaxPerSide = 2;   // NAV_SJ_MAX_PER_SIDE
    public const int SjPenalty = 40;   // NAV_SJ_PENALTY
    public const int SjSkipHops = 3;   // NAV_SJ_SKIP_HOPS
    public const int SjSetupTicks = 5;   // NAV_SJ_SETUP_TICKS
}
