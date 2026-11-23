namespace GG2.Protocol;

/// <summary>
/// The <c>BOT_*</c> block of <c>Constants.xml</c>: the reference bot layer's
/// tuning set.
/// </summary>
/// <remarks>
/// Carried over verbatim so the port has a known-good baseline to measure
/// against, per docs/STATUS.md phase 7 — the point is to beat these, not to keep
/// them.
/// </remarks>
public static class BotConstants
{
    public const int ReplanTicks = 45;   // BOT_REPLAN_TICKS
    public const int StuckTicks = 12;   // BOT_STUCK_TICKS
    public const int OffpathTicks = 20;   // BOT_OFFPATH_TICKS
    public const int BlacklistTicks = 150;   // BOT_BLACKLIST_TICKS
    public const int ThrashDist = 8;   // BOT_THRASH_DIST
    public const int ArriveTol = 8;   // BOT_ARRIVE_TOL
    public const int SteerTol = 2;   // BOT_STEER_TOL
    public const int EntryLead = 2;   // BOT_ENTRY_LEAD
    public const int JumpLead = 1;   // BOT_JUMP_LEAD
    public const int ObjectivePeriod = 30;   // BOT_OBJECTIVE_PERIOD
    public const int GoalRetargetDist = 48;   // BOT_GOAL_RETARGET_DIST
    public const int AimIterations = 8;   // BOT_AIM_ITERATIONS
    public const int AimMaxTicks = 60;   // BOT_AIM_MAX_TICKS
    public const int TierMin = 1;   // BOT_TIER_MIN
    public const int TierMax = 5;   // BOT_TIER_MAX
    public const int LeadNone = 0;   // BOT_LEAD_NONE
    public const int LeadLinear = 1;   // BOT_LEAD_LINEAR
    public const int LeadFull = 2;   // BOT_LEAD_FULL
    public const int TargetPeriod = 15;   // BOT_TARGET_PERIOD
    public const double AimConeDecay = 0.98819;   // BOT_AIM_CONE_DECAY
    public const int AimSettleDeg = 3;   // BOT_AIM_SETTLE_DEG
    public const int CloseRange = 90;   // BOT_CLOSE_RANGE
    public const double ClosePenalty = 0.6;   // BOT_CLOSE_PENALTY
    public const int FeetOffset = 20;   // BOT_FEET_OFFSET
    public const int SplashSafe = 110;   // BOT_SPLASH_SAFE
    public const int FlameReach = 165;   // BOT_FLAME_REACH
    public const int DodgeRange = 320;   // BOT_DODGE_RANGE
    public const int DodgeTicks = 26;   // BOT_DODGE_TICKS
    public const int BodyHalfw = 12;   // BOT_BODY_HALFW
    public const int BodyUp = 13;   // BOT_BODY_UP
    public const int BodyDown = 23;   // BOT_BODY_DOWN
    public const int RocketRadius = 8;   // BOT_ROCKET_RADIUS
    public const int HealRange = 300;   // BOT_HEAL_RANGE
    public const int UberRange = 200;   // BOT_UBER_RANGE
    public const int MineTrigger = 30;   // BOT_MINE_TRIGGER
    public const int StabRange = 40;   // BOT_STAB_RANGE
    public const int CloakPeriod = 15;   // BOT_CLOAK_PERIOD
    public const int ZoomRange = 400;   // BOT_ZOOM_RANGE
    public const int UnzoomRange = 250;   // BOT_UNZOOM_RANGE
    public const double EatHpFraction = 0.5;   // BOT_EAT_HP_FRACTION
    public const int RoleAttack = 0;   // BOT_ROLE_ATTACK
    public const int RoleDefend = 1;   // BOT_ROLE_DEFEND
    public const int DefendEvery = 3;   // BOT_DEFEND_EVERY
    public const int DefendRadius = 220;   // BOT_DEFEND_RADIUS
    public const int SpotTries = 12;   // BOT_SPOT_TRIES
    public const double SpotBand = 0.75;   // BOT_SPOT_BAND
    public const double SpotHighBonus = 1.5;   // BOT_SPOT_HIGH_BONUS
    public const int HoldRadius = 260;   // BOT_HOLD_RADIUS
    public const int SpreadMax = 96;   // BOT_SPREAD_MAX
    public const double OccupancyCost = 0.25;   // BOT_OCCUPANCY_COST
    public const int MinEngage = 40;   // BOT_MIN_ENGAGE
    public const double PotshotMult = 1.7;   // BOT_POTSHOT_MULT
    public const double PotshotChance = 0.12;   // BOT_POTSHOT_CHANCE
    public const int RegroupTicks = 90;   // BOT_REGROUP_TICKS
    public const int RegroupMates = 1;   // BOT_REGROUP_MATES
    public const int RegroupRadius = 300;   // BOT_REGROUP_RADIUS
    public const int RegroupCrowd = 3;   // BOT_REGROUP_CROWD
    public const int JumpVtol = 1;   // BOT_JUMP_VTOL
    public const int RunupCells = 6;   // BOT_RUNUP_CELLS
    public const int TakeoffPatience = 60;   // BOT_TAKEOFF_PATIENCE
    public const int RunupHops = 8;   // BOT_RUNUP_HOPS
    public const int JumpReachTol = 6;   // BOT_JUMP_REACH_TOL
    public const double RestSpeed = 0.195;   // BOT_REST_SPEED
    public const int ArcSamples = 10;   // BOT_ARC_SAMPLES
    public const int StabLeadMin = 32;   // BOT_STAB_LEAD_MIN
    public const int StabLeadMax = 38;   // BOT_STAB_LEAD_MAX
    public const int StabReach = 37;   // BOT_STAB_REACH
    public const int StabBack = 26;   // BOT_STAB_BACK
    public const int AirblastMinTravel = 60;   // BOT_AIRBLAST_MIN_TRAVEL
    public const int AirblastNear = 28;   // BOT_AIRBLAST_NEAR
    public const int AirblastReach = 126;   // BOT_AIRBLAST_REACH
    public const int AirblastHalfw = 18;   // BOT_AIRBLAST_HALFW
    public const int BallSpd = 0;   // BOT_BALL_SPD
    public const int BallGrav = 1;   // BOT_BALL_GRAV
    public const int BallDrift = 2;   // BOT_BALL_DRIFT
    public const int GenThreat = 4000;   // BOT_GEN_THREAT
    public const int SentryBonus = 200;   // BOT_SENTRY_BONUS
    public const int AttackerBonus = 150;   // BOT_ATTACKER_BONUS
    public const int BlindRange = 260;   // BOT_BLIND_RANGE
    public const double PushFrac = 0.4;   // BOT_PUSH_FRAC
    public const int PushDist = 400;   // BOT_PUSH_DIST
    public const int MedicFollowRange = 700;   // BOT_MEDIC_FOLLOW_RANGE
    public const int MedicOffset = 60;   // BOT_MEDIC_OFFSET
    public const int CpPotshot = 0;   // BOT_CP_POTSHOT
    public const int CpHeals = 1;   // BOT_CP_HEALS
    public const int CpSplash = 2;   // BOT_CP_SPLASH
    public const int CpSpecialFire = 3;   // BOT_CP_SPECIAL_FIRE
    public const int CpAirjump = 4;   // BOT_CP_AIRJUMP
    public const int CpFollow = 5;   // BOT_CP_FOLLOW
    public const int CpDefendEvery = 6;   // BOT_CP_DEFEND_EVERY
    public const int CpSpotMode = 7;   // BOT_CP_SPOT_MODE
    public const int CpSpotHigh = 8;   // BOT_CP_SPOT_HIGH
    public const int CpAimErr = 9;   // BOT_CP_AIM_ERR
    public const int CpAimSettle = 10;   // BOT_CP_AIM_SETTLE
    public const double CpAimErrTight = 0.6;   // BOT_CP_AIM_ERR_TIGHT
    public const double CpAimErrLoose = 1.6;   // BOT_CP_AIM_ERR_LOOSE
    public const int CpSettleLoose = 2;   // BOT_CP_SETTLE_LOOSE
    public const double CpAimErrSniper = 0.3;   // BOT_CP_AIM_ERR_SNIPER
    public const double CpSettleTight = 0.3;   // BOT_CP_SETTLE_TIGHT
    public const int CpSuppress = 11;   // BOT_CP_SUPPRESS
    public const int SuppressHops = 24;   // BOT_SUPPRESS_HOPS
    public const int SpotNone = 0;   // BOT_SPOT_NONE
    public const int SpotStandoff = 1;   // BOT_SPOT_STANDOFF
    public const int SpotChoke = 2;   // BOT_SPOT_CHOKE
    public const int SpotFlank = 3;   // BOT_SPOT_FLANK
    public const int LosAny = 0;   // BOT_LOS_ANY
    public const int LosNeed = 1;   // BOT_LOS_NEED
    public const int LosAvoid = 2;   // BOT_LOS_AVOID
    public const int DefendKeen = 2;   // BOT_DEFEND_KEEN
    public const int DefendAverse = 6;   // BOT_DEFEND_AVERSE

    /// <summary>
    /// A <see cref="DefendEvery"/> period meaning this class never defends.
    /// </summary>
    /// <remarks>
    /// Port-authored, and the one veto in a table the reference deliberately made
    /// out of periods rather than vetoes. <c>BotRoles.Assign</c> tests for it
    /// before the modulo, which is also what keeps a zero period from dividing by
    /// zero. See <c>BotClassProfile.DefendEvery</c> for why the medic gets it.
    /// </remarks>
    public const int DefendNever = 0;
    public const double SpotNearFrac = 0.5;   // BOT_SPOT_NEAR_FRAC
    public const int SpotHighSniper = 3;   // BOT_SPOT_HIGH_SNIPER
    public const int CpCloseIn = 12;   // BOT_CP_CLOSE_IN
    public const int EngageTicks = 150;   // BOT_ENGAGE_TICKS
    public const int EngageGoalNear = 260;   // BOT_ENGAGE_GOAL_NEAR
    public const int EngageStep = 16;   // BOT_ENGAGE_STEP
    public const int EngageDrop = 28;   // BOT_ENGAGE_DROP

    // --- port-authored, with no BOT_* counterpart -----------------------------
    //
    // The reference has no re-post: a bot that arrived there stood on the spot for
    // the rest of the round. docs/TRAPS-BOTS.md, "A bot that arrives stops moving".

    /// <summary>
    /// How long a bot stands completely still before it asks for somewhere else
    /// to stand — <c>ScriptedBot.ParkedTicks</c>.
    /// </summary>
    /// <remarks>
    /// Eight seconds. Long enough never to interrupt a bot holding a firing
    /// position through one engagement, short enough that nobody watching sees a
    /// statue.
    /// </remarks>
    public const int RepostTicks = 240;

    /// <summary>
    /// How long after a hit a bot still counts as under fire —
    /// <c>BotTargeting.UnderFire</c>.
    /// </summary>
    /// <remarks>
    /// Three seconds, measured from the last hit rather than from the first, so
    /// somebody still shooting keeps the window open. Shorter than the
    /// simulation's own <c>ASSIST_TIME</c> because this is "am I in a fight", not
    /// "who gets the kill".
    /// </remarks>
    public const int UnderFireTicks = 90;

    /// <summary>
    /// How far a bot will look for whoever is shooting it, whatever its class's
    /// attention radius is.
    /// </summary>
    /// <remarks>
    /// A class radius says how far a bot goes LOOKING; damage arriving is being
    /// TOLD, and the two are not the same question. A pyro's 200px radius is what
    /// makes it walk past a corner rather than scan the whole map, and it must not
    /// also be what stops it turning round when a sniper hits it from 400px away.
    /// <para>
    /// A sniper's own radius is the ceiling: it is the widest distance anything in
    /// the class table pays attention to, so nothing here can be told about
    /// somebody further off than the game's own longest sightline.
    /// </para>
    /// </remarks>
    public const int RetaliateRange = 900;

    /// <summary>How far a re-post has to move the bot to count as one.</summary>
    /// <remarks>
    /// Above <see cref="GoalRetargetDist"/>, so a re-post is never a goal the
    /// objective throttle would swallow, and an order of magnitude above
    /// <see cref="ArriveTol"/>, so the bot walks rather than shuffles.
    /// </remarks>
    public const int RepostMinMove = 96;
    /// <summary>
    /// How long a bot stands still <em>while being shot</em> before it asks for
    /// somewhere else to stand.
    /// </summary>
    /// <remarks>
    /// Half a second, against <see cref="RepostTicks"/>' eight. The long timer is
    /// for a bot that has quietly become a statue and nothing is at stake in the
    /// meantime; a bot taking fire it is not answering has about three seconds to
    /// live, so the same eight-second patience is a death sentence.
    /// <para>
    /// It cannot interrupt a bot that IS answering: <c>ScriptedBot.ParkedTicks</c>
    /// stops counting while the engage hold is on, so standing to fight pauses this
    /// as it pauses the long one.
    /// </para>
    /// </remarks>
    public const int RepostUnderFireTicks = 15;
}
