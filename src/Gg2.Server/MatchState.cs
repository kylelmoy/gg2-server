using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>Intel position and its return countdown.</summary>
public sealed class IntelState
{
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>
    /// <c>alarm[0]</c>: ticks until dropped intel returns itself. At a fixed
    /// 30 Hz this is a plain tick counter, and -1 means "not running", which is
    /// GM8's value for an unset alarm.
    /// </summary>
    public short RecoverTimer { get; set; } = -1;
}

/// <summary>One control point, whatever kind of point the gamemode uses.</summary>
public sealed class ControlPointState
{
    /// <summary>Owning team, or -1 for neutral. Written as a signed byte.</summary>
    public sbyte Team { get; set; } = -1;

    /// <summary>Team currently capturing, or -1.</summary>
    public sbyte CappingTeam { get; set; } = -1;

    /// <summary>Capture progress.</summary>
    public ushort Capping { get; set; }
}

public sealed class GeneratorState
{
    public ushort Hp { get; set; }

    public ushort ShieldHp { get; set; }
}

public sealed class MovingPlatformState
{
    public double X { get; set; }

    public double Y { get; set; }

    public byte MovementState { get; set; }
}

/// <summary>
/// Everything a state update needs that is not a character.
/// </summary>
/// <remarks>
/// <b>A placeholder, deliberately.</b> Phase 1 of docs/PLAN.md is a server a
/// client can join with no simulation behind it, so this holds the objective and
/// HUD fields at their initial values and nothing advances them. It is a
/// reference-shaped bag of globals, not the flat, cheaply-copyable
/// <c>WorldState</c> that docs/BOTAI.md requires — that one belongs in
/// <c>Gg2.Simulation</c>, and this type should shrink to nothing when it
/// arrives rather than growing into it.
/// </remarks>
public sealed class MatchState
{
    public MatchState(LevelMap map, Roster roster, ServerOptions options)
    {
        Map = map;
        Roster = roster;
        GameMode = GameModes.Of(map);
        TotalMapAreas = GameModes.TotalAreas(map);
        CurrentMapArea = 1;

        CapLimit = options.CapLimit;
        TimeLimitMinutes = options.TimeLimitMinutes;
        RespawnTimeSeconds = options.RespawnTimeSeconds;
        KillLimit = options.KillLimit;
        Array.Fill(ClassLimits, options.ClassLimit);

        // global.tdmInvulnerabilityTicks = seconds * 30 * 0.2 — GameServerCreate.gml.
        TdmInvulnerabilityTicks = (ushort)(options.TdmInvulnerabilitySeconds * 30 * 0.2);

        BuildEntities();
    }

    public LevelMap Map { get; }

    public Roster Roster { get; }

    public GameMode GameMode { get; }

    public int TotalMapAreas { get; }

    public byte CurrentMapArea { get; set; }

    // --- objective entities --------------------------------------------------

    public IntelState? RedIntel { get; private set; }

    public IntelState? BlueIntel { get; private set; }

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

    public List<MovingPlatformState> MovingPlatforms { get; } = [];

    public GeneratorState RedGenerator { get; } = new();

    public GeneratorState BlueGenerator { get; } = new();

    // --- scoreboard and HUD --------------------------------------------------

    public byte CapLimit { get; set; }

    public byte RedCaps { get; set; }

    public byte BlueCaps { get; set; }

    public byte RespawnTimeSeconds { get; set; }

    public byte TimeLimitMinutes { get; set; }

    public ushort KillLimit { get; set; }

    public ushort TdmInvulnerabilityTicks { get; }

    /// <summary>Round timer, in ticks. A <c>uint</c> on the wire.</summary>
    public uint Timer { get; set; }

    public ushort SetupTimer { get; set; }

    public ushort ControlPointUnlock { get; set; }

    public ushort RedTimer { get; set; }

    public ushort BlueTimer { get; set; }

    public byte RedWins { get; set; }

    public byte BlueWins { get; set; }

    public byte ArenaState { get; set; }

    /// <summary>Winning team, or -1 while the round is live. Signed on the wire.</summary>
    public sbyte ArenaWinners { get; set; } = -1;

    public ushort ArenaEndCount { get; set; }

    public int ArenaRoundStart { get; set; }

    /// <summary>Per-class limits, ten of them, sent on every <c>FULL_UPDATE</c>.</summary>
    public byte[] ClassLimits { get; } = new byte[10];

    private void BuildEntities()
    {
        foreach (var entity in Map.Entities)
        {
            switch (entity.Type)
            {
                // The intel sits at its base until someone moves it.
                case EntityTypes.RedIntel:
                    RedIntel ??= new IntelState { X = entity.X, Y = entity.Y };
                    break;
                case EntityTypes.BlueIntel:
                    BlueIntel ??= new IntelState { X = entity.X, Y = entity.Y };
                    break;

                case EntityTypes.MovingPlatform:
                    MovingPlatforms.Add(new MovingPlatformState { X = entity.X, Y = entity.Y });
                    break;
            }
        }

        BuildControlPoints();
    }

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
