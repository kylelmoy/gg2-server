using GG2.Maps;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>One control point, whatever kind of point the gamemode uses.</summary>
public sealed class ControlPointState
{
    /// <summary>Owning team, or -1 for neutral. Written as a signed byte.</summary>
    public sbyte Team { get; set; } = -1;

    /// <summary>Team currently capturing, or -1.</summary>
    public sbyte CappingTeam { get; set; } = -1;

    /// <summary>
    /// Capture progress. A real in the reference, rounded on the way out.
    /// </summary>
    public double Capping { get; set; }
}

/// <summary>
/// Everything a state update needs that is not a character.
/// </summary>
/// <remarks>
/// <b>A placeholder, deliberately.</b> Phase 1 of docs/STATUS.md is a server a
/// client can join with no simulation behind it, so this holds the objective and
/// HUD fields at their initial values and nothing advances them. It is a
/// reference-shaped bag of globals, not the flat, cheaply-copyable
/// <c>WorldState</c> that docs/BOTAI.md requires — that one belongs in
/// <c>GG2.Simulation</c>, and this type should shrink to nothing when it
/// arrives rather than growing into it.
/// </remarks>
public sealed class MatchState
{
    /// <param name="totalMapAreas">
    /// The area count of the <b>unnarrowed</b> map — <c>global.totalMapAreas</c>,
    /// which <c>basicRoomSetup.gml:7</c> computes before it destroys the other
    /// areas' entities. Counting <paramref name="map"/> instead would count one
    /// slice's <c>NextAreaO</c> markers, which is a different quantity.
    /// </param>
    public MatchState(LevelMap map, Roster roster, ServerOptions options, int totalMapAreas)
    {
        Map = map;
        Roster = roster;
        GameMode = GameModes.Of(map);
        TotalMapAreas = totalMapAreas;
        CurrentMapArea = 1;

        CapLimit = options.CapLimit;
        RespawnTimeSeconds = options.RespawnTimeSeconds;
        // EffectiveClassLimits, because this is the array that reaches the wire
        // and GameServer.ApplyOptions enforces the same one — a limit shown on
        // the client's HUD that the server does not apply is invisible from
        // inside either side.
        options.EffectiveClassLimits().AsSpan(0, ClassLimits.Length).CopyTo(ClassLimits);

        BuildEntities();
    }

    public LevelMap Map { get; }

    public Roster Roster { get; }

    public GameMode GameMode { get; }

    public int TotalMapAreas { get; }

    public byte CurrentMapArea { get; set; }

    // --- objective entities --------------------------------------------------
    //
    // The intel, round timer, time limit, control points, KotH/DKotH timers,
    // arena round state and generators are all simulated in
    // GG2.Simulation.WorldState now; StateUpdateWriter reads them through an
    // ObjectiveLookup rather than out of this bag.

    /// <summary>
    /// The points the HUD block serialises, already in the order it wants them.
    /// </summary>
    /// <remarks>
    /// Which order that is depends on the mode: <c>ControlPointHUD</c> walks
    /// <c>global.cp[1..totalControlPoints]</c> by number, <c>DKothHUD</c> writes
    /// red then blue, and the others use instance order. Building the list once,
    /// here, keeps that decision in one place.
    /// </remarks>
    public List<ControlPointState> ControlPoints { get; } = [];

    // --- scoreboard and HUD --------------------------------------------------

    public byte CapLimit { get; set; }

    public byte RespawnTimeSeconds { get; set; }

    public ushort SetupTimer { get; set; }

    public byte RedWins { get; set; }

    public byte BlueWins { get; set; }

    public byte ArenaState { get; set; }

    /// <summary>Winning team, or -1 while the round is live. Signed on the wire.</summary>
    public sbyte ArenaWinners { get; set; } = -1;

    public ushort ArenaEndCount { get; set; }

    public int ArenaRoundStart { get; set; }

    /// <summary>Per-class limits, ten of them, sent on every <c>FULL_UPDATE</c>.</summary>
    public byte[] ClassLimits { get; } = new byte[10];

    /// <summary>
    /// Nothing left to build from the entity list.
    /// </summary>
    /// <remarks>
    /// Moving platforms now live and advance in <c>GG2.Simulation.WorldState</c>;
    /// this method stays only as the seam <see cref="BuildControlPoints"/> is
    /// reached through.
    /// </remarks>
    private void BuildEntities() => BuildControlPoints();

    private void BuildControlPoints()
    {
        switch (GameMode)
        {
            case GameMode.Arena:
                AddPointsOfType(EntityTypes.ArenaControlPoint);
                break;

            case GameMode.KingOfTheHill:
                AddPointsOfType(EntityTypes.KothControlPoint);
                break;

            case GameMode.DualKingOfTheHill:
                // DKothHUD writes red then blue, in that order.
                AddPointsOfType(EntityTypes.KothRedControlPoint);
                AddPointsOfType(EntityTypes.KothBlueControlPoint);
                break;

            case GameMode.ControlPoints:
                // ControlPointHUD walks global.cp[1..n] by number, not by
                // instance order, so the numbered types go in numbered order.
                AddPointsOfType(EntityTypes.ControlPoint1);
                AddPointsOfType(EntityTypes.ControlPoint2);
                AddPointsOfType(EntityTypes.ControlPoint3);
                AddPointsOfType(EntityTypes.ControlPoint4);
                AddPointsOfType(EntityTypes.ControlPoint5);
                break;
        }
    }

    private void AddPointsOfType(string type)
    {
        foreach (var entity in Map.Entities)
            if (string.Equals(entity.Type, type, StringComparison.Ordinal))
                ControlPoints.Add(new ControlPointState());
    }
}
