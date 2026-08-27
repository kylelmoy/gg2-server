using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// One bot's combat state: what it is shooting at, where it is pointing, and the
/// four clocks the difficulty model runs on.
/// </summary>
/// <remarks>
/// The combat half of <c>Player.events/Create.xml</c>'s <c>bot*</c> fields, and
/// the sibling of <see cref="BotPathState"/>. Outside
/// <c>Gg2.Simulation.WorldState</c> for the same reason: none of it is
/// simulation, nothing is serialised from it, and no client re-derives it.
/// </remarks>
public sealed class BotCombatState
{
    /// <summary>What this bot is shooting at, if anything.</summary>
    public BotTarget Target { get; set; } = BotTarget.None;

    /// <summary>The tick the current target was picked.</summary>
    /// <remarks>
    /// ⚠️ The whole acquire/fire chain is measured from here, not from when the
    /// bot started shooting — so re-acquiring genuinely re-pays the delay, which
    /// is what makes a weak bot slow to react rather than slow to hit.
    /// </remarks>
    public int TargetAt { get; set; }

    /// <summary>The last tick the target was actually visible.</summary>
    public int LastSeenAt { get; set; }

    /// <summary>Whether the sightline to the target is currently clear.</summary>
    /// <remarks>
    /// ⚠️ A target is <em>kept</em> for thirty ticks after the sightline breaks,
    /// with this false. The aim is then held on the last known position and not
    /// refreshed through the wall, so the bot engages the moment the line opens
    /// rather than re-paying the whole acquire chain then.
    /// </remarks>
    public bool Visible { get; set; }

    /// <summary>
    /// Whether the current target was acquired at the widened potshot radius.
    /// </summary>
    /// <remarks>
    /// ⚠️ Such a target is outside the class's ordinary radius <em>by
    /// construction</em>, so it has to be allowed to stay outside it — the same
    /// band that let it be picked. Without that the shot is taken and the target
    /// dropped on the very next tick, which re-pays the whole chain for every
    /// potshot and means none of them ever leaves the barrel.
    /// </remarks>
    public bool Potshot { get; set; }

    /// <summary>Where the aim actually points, in degrees.</summary>
    /// <remarks>
    /// Slewed towards <see cref="AimWant"/> at the skill's turn rate. ⚠️ There is
    /// no filter and no smoothing: tracking lag, overshoot and settling are all
    /// consequences of the three numbers (perceive, aim interval, turn rate) and
    /// nothing else.
    /// </remarks>
    public double AimDir { get; set; }

    /// <summary>Where the aim is trying to point.</summary>
    public double AimWant { get; set; }

    /// <summary>The point the aim was solved for, for the aim <em>distance</em> byte.</summary>
    public double AimX { get; set; }

    /// <summary>The point the aim was solved for, for the aim distance byte.</summary>
    public double AimY { get; set; }

    /// <summary>The next tick the target snapshot may be refreshed.</summary>
    public int PerceiveAt { get; set; }

    /// <summary>The next tick the desired direction may be recomputed.</summary>
    public int AimAt { get; set; }

    /// <summary>What the snapshot was taken of, so a change of subject refreshes it at once.</summary>
    public BotTarget AimSubject { get; set; } = BotTarget.None;

    /// <summary>Where the target was when the snapshot was taken.</summary>
    public double SeeX { get; set; }

    /// <summary>Where the target was when the snapshot was taken.</summary>
    public double SeeY { get; set; }

    /// <summary>How fast it was going when the snapshot was taken.</summary>
    public double SeeVx { get; set; }

    /// <summary>How fast it was going when the snapshot was taken.</summary>
    public double SeeVy { get; set; }

    /// <summary>
    /// Quake III's fire throttle: this aim interval's roll said not to shoot.
    /// </summary>
    /// <remarks>
    /// ⚠️ Rolled once per aim interval rather than per tick, so it withholds a
    /// <em>burst</em> rather than stuttering the trigger — which is what a player
    /// hesitating looks like.
    /// </remarks>
    public bool HoldFire { get; set; }

    /// <summary>The last tick the spy toggled its cloak.</summary>
    public int CloakAt { get; set; }

    /// <summary>The keys the combat half asked for last tick.</summary>
    /// <remarks>
    /// Reported rather than used internally: the navigation half ORs with it, and
    /// the two cannot conflict because this one owns ATTACK and SPECIAL and the
    /// follower owns LEFT/RIGHT/JUMP/DOWN.
    /// </remarks>
    public PlayerKeys AttackKeys { get; set; }

    /// <summary>
    /// This bot's own offset into the shared cadences.
    /// </summary>
    /// <remarks>
    /// The reference staggers by the Player instance id — <c>(tick + player) mod
    /// BOT_TARGET_PERIOD</c> — so a server full of bots does not run every target
    /// search on one frame. ⚠️ Unlike <see cref="BotPathState.ReplanStagger"/>
    /// this one is a genuine <em>phase</em>: it shifts which tick the cadence
    /// fires on and does not change its period.
    /// </remarks>
    public int Stagger { get; set; }

    /// <summary>
    /// The team-mate this medic is beaming, or <see cref="BotAlly.None"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Refreshed on the target cadence like everything else, but
    /// RE-VALIDATED every tick — an ally can die, change team or leave between
    /// refreshes, and a stale slot index names whoever the roster compacted into it.
    /// </remarks>
    public int Ally { get; set; } = BotAlly.None;

    /// <summary>
    /// The node this bot is denying while it cannot see its target, or
    /// <see cref="BotSuppress.NoSpot"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Re-used between refreshes, which is also honest about what it is: <b>a
    /// place, not a moving target</b>.
    /// </remarks>
    public int SuppressNode { get; set; } = BotSuppress.NoSpot;

    /// <summary>The tick the suppression spot may next be recomputed.</summary>
    /// <remarks>
    /// ⚠️ On its own clock, NOT on the aim interval. The walk is a graph search
    /// and the aim interval is 2 ticks at the top tier, so asking it there would be a
    /// search per bot per two frames on a server already near its budget with twelve
    /// bots.
    /// </remarks>
    public int SuppressAt { get; set; }

    /// <summary>Forgets the target and everything derived from it.</summary>
    public void DropTarget()
    {
        Target = BotTarget.None;
        Potshot = false;
        AimSubject = BotTarget.None;
    }
}
