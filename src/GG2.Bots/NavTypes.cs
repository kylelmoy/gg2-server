namespace GG2.Bots;

/// <summary>How a nav edge is traversed. <c>NAV_EDGE_WALK</c> and friends.</summary>
/// <remarks>
/// A typed view of <see cref="GG2.Protocol.NavConstants"/>'s
/// <c>Edge*</c> block, which is where the numbers live and is already pinned to
/// <c>Constants.xml</c> by <c>ConstantsTests</c>. <c>NavTypeTests</c> pins these
/// names to those numbers, so the enum cannot drift away from the table it
/// restates.
/// </remarks>
public enum NavEdgeType
{
    Walk = 0,
    Fall = 1,
    Jump = 2,
    DropThrough = 3,
    MoveBox = 4,
    DoubleJump = 5,

    /// <summary>
    /// An arc that only exists because the bot fires a rocket at its own feet
    /// on the tick it jumps — <c>botInputUpdate.gml</c> negotiates the trigger
    /// and the aim for it, which is the one place a bot's combat and navigation
    /// halves are not independent.
    /// </summary>
    RocketJump = 6,

    StickyJump = 7,
}

/// <summary>
/// What a node or edge asks about the traveller. <c>NAV_GATE_*</c>.
/// </summary>
/// <remarks>
/// <b>Gates are a per-query cost and are deliberately not baked into the
/// graph</b> — the answer depends on who is asking, so one graph serves both
/// teams, carriers and non-carriers alike. See
/// <see cref="NavGraph.GatePassable"/>.
/// </remarks>
public enum NavGate
{
    None = 0,
    TeamRed = 1,
    TeamBlue = 2,
    IntelRed = 3,
    IntelBlue = 4,
    Setup = 5,
}

/// <summary>Which side of a node a droppable floor sits on. <c>NAV_DOOR_*</c>.</summary>
public enum NavDoor
{
    None = 0,
    Left = 1,
    Right = 2,
}
