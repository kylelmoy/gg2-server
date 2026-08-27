using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// Where a class likes to stand when it is not going anywhere —
/// <c>BOT_SPOT_*</c>.
/// </summary>
public enum BotSpotMode
{
    None = 0,
    Standoff = 1,
    Choke = 2,
    Flank = 3,
}

/// <summary>
/// The per-class behaviour table — <c>Scripts/Bots/botClassProfile.gml</c>.
/// </summary>
/// <remarks>
/// <b>One table rather than a knob per script.</b> The reference collected these
/// from four scripts (<c>botCombatUpdate</c> had four, <c>botInputUpdate</c> and
/// <c>botObjectiveUpdate</c> one each) precisely so that "what is different about
/// a pyro" is answerable by reading one file.
/// <para>
/// ⚠️ <b>Every row is a deliberate exception, and the default is the interesting
/// part.</b> A class that is not named in a property behaves like everyone else,
/// so adding a class needs no edit here at all — and a row that names <em>every</em>
/// class is a property that should not be in this table.
/// </para>
/// </remarks>
public static class BotClassProfile
{
    /// <summary>
    /// Will this class take a shot at something outside its ordinary attention
    /// radius.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not the pyro or the medic: a flame does not reach and a medic's needles
    /// are not what it is for. Everyone else will.
    /// </remarks>
    public static bool Potshots(PlayerClass playerClass) =>
        playerClass is not (PlayerClass.Pyro or PlayerClass.Medic);

    /// <summary>Does this class heal rather than shoot when a team-mate is near.</summary>
    public static bool Heals(PlayerClass playerClass) => playerClass == PlayerClass.Medic;

    /// <summary>
    /// Does this class aim at a grounded target's <em>feet</em>.
    /// </summary>
    /// <remarks>
    /// The splash classes, and only at a skill high enough to know to.
    /// <c>BOT_FEET_OFFSET</c> below the chest is where a rocket does the most
    /// damage to somebody standing on the floor.
    /// </remarks>
    public static bool Splash(PlayerClass playerClass) =>
        playerClass is PlayerClass.Soldier or PlayerClass.Demoman;

    /// <summary>
    /// Is this class's <em>special</em> key part of firing rather than a
    /// reaction.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only the medic, whose needle gun is on the right click. That is what
    /// makes the fire gates apply to SPECIAL for a medic and not for anybody
    /// else: a pyro's airblast and a demoman's detonation are <em>reactions</em>,
    /// and a reaction that arrives a second and a half late is not a reaction.
    /// </remarks>
    public static bool SpecialIsFire(PlayerClass playerClass) => playerClass == PlayerClass.Medic;

    /// <summary>Does this class have a second jump in the air.</summary>
    public static bool AirJump(PlayerClass playerClass) => playerClass == PlayerClass.Scout;

    /// <summary>Does this class follow a team-mate rather than the objective.</summary>
    public static bool Follows(PlayerClass playerClass) => playerClass == PlayerClass.Medic;

    /// <summary>Does this class shoot at where it last saw somebody.</summary>
    /// <remarks>
    /// Only the heavy: a minigun is the one weapon whose whole point is putting
    /// rounds through a doorway somebody is behind.
    /// </remarks>
    public static bool Suppresses(PlayerClass playerClass) => playerClass == PlayerClass.Heavy;

    /// <summary>How much of the difficulty's aim error this class carries.</summary>
    /// <remarks>
    /// ⚠️ A multiplier on the knob rather than a knob of its own, so a weak
    /// sniper is still relatively accurate and a strong heavy is still relatively
    /// sloppy — which is what makes the classes feel different at every tier
    /// rather than only at the top.
    /// </remarks>
    public static double AimError(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Soldier => BotConstants.CpAimErrTight,
        PlayerClass.Heavy => BotConstants.CpAimErrLoose,
        PlayerClass.Sniper => BotConstants.CpAimErrSniper,
        _ => 1,
    };

    /// <summary>How settled this class's aim must be before it will fire.</summary>
    /// <remarks>
    /// A multiplier on <c>BOT_AIM_SETTLE_DEG</c>. ⚠️ A heavy is allowed to fire
    /// while still turning — a minigun sprays — and a sniper is not.
    /// </remarks>
    public static double AimSettle(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Heavy => BotConstants.CpSettleLoose,
        PlayerClass.Sniper => BotConstants.CpSettleTight,
        _ => 1,
    };

    /// <summary>
    /// How near this class wants to be before it stops walking at a target.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only the pyro, and it is the one thing that makes the engage hold walk
    /// <em>toward</em> an enemy instead of standing where the hold caught it.
    /// Zero is "no opinion".
    /// </remarks>
    public static int CloseIn(PlayerClass playerClass) =>
        playerClass == PlayerClass.Pyro ? BotConstants.FlameReach : 0;

    /// <summary>Where this class likes to stand.</summary>
    public static BotSpotMode SpotMode(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Sniper or PlayerClass.Heavy or PlayerClass.Demoman => BotSpotMode.Standoff,
        PlayerClass.Engineer => BotSpotMode.Choke,
        PlayerClass.Spy => BotSpotMode.Flank,
        _ => BotSpotMode.None,
    };

    /// <summary>
    /// One bot in every N of this class's lean group defends.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The class term is a PERIOD, not a veto, and that distinction is the
    /// whole design.</b> The obvious rule — "Engineers and Heavies defend, Scouts
    /// and Spies attack" — breaks at both ends: a team of nine Engineers would
    /// have nobody attacking and a team of nine Scouts nobody defending, and
    /// <c>botPopulationUpdate</c> picks a class with <c>irandom(8)</c>, so it can
    /// produce either. Making the class choose <em>how often</em> keeps a split at
    /// every composition — an all-Engineer team runs half defenders, an all-Scout
    /// team one in six — and with every class on the default it reduces exactly to
    /// the old behaviour.
    /// <para>
    /// Keen because a sentry is a fixed emplacement and 200 hp at run 0.8 is
    /// ground-holding rather than objective-running; averse because
    /// <c>capStrength</c> 2 makes the scout the fastest capper in the game, and a
    /// spy standing next to its own flag is a spy doing a job any class could do.
    /// </para>
    /// </remarks>
    public static int DefendEvery(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Engineer or PlayerClass.Heavy => BotConstants.DefendKeen,
        PlayerClass.Scout or PlayerClass.Spy => BotConstants.DefendAverse,
        _ => BotConstants.DefendEvery,
    };

    /// <summary>
    /// How much this class values height when choosing where to stand, in
    /// <c>BotGoalSpot</c>'s <c>highBonus</c> units.
    /// </summary>
    /// <remarks>
    /// A sniper values height more than anything else does: elevation is what
    /// turns a long sightline into one nobody can walk out of. A heavy wants it
    /// for the same reason at a shorter range, an engineer only mildly — a
    /// sentry's value is the corridor it covers, not the storey it stands on —
    /// and a spy not at all, since a flank is about being unseen rather than
    /// about being above.
    /// </remarks>
    public static double SpotHigh(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Sniper => BotConstants.SpotHighSniper,
        PlayerClass.Heavy => BotConstants.SpotHighBonus,
        PlayerClass.Engineer => BotConstants.SpotHighBonus / 2,
        _ => 0,
    };
}
