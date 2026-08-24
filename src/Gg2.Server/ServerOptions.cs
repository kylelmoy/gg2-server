using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// How the server is configured. Defaults match the reference's <c>gg2.ini</c>
/// fallbacks in <c>Scripts/Game/game_init.gml:66-106</c>.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Shown in the client's server browser and on join.</summary>
    public string ServerName { get; set; } = "My Server";

    /// <summary>
    /// The name of the roster slot-0 entry — the reference's local player, which
    /// a headless server keeps as a permanent spectator.
    /// </summary>
    public string ServerPlayerName { get; set; } = "Server";

    /// <summary>Listen port. Zero means "any free port", which tests use.</summary>
    public int Port { get; set; } = GameConstants.DefaultPort;

    /// <summary>Empty means no password, and no <c>PASSWORD_REQUEST</c>.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Sent as a <c>MESSAGE_STRING</c> to each joiner. Empty means none.</summary>
    public string WelcomeMessage { get; set; } = string.Empty;

    /// <summary>
    /// Seats for network clients. The reference clamps this to 48; slot 0 and
    /// bots do not count against it (<c>getNumberOfOccupiedSlots</c>).
    /// </summary>
    public int PlayerLimit { get; set; } = 10;

    /// <summary>
    /// Connections allowed from one address. Not enforced for loopback, which is
    /// how the reference lets a host run clients beside its own server.
    /// </summary>
    public int MultiClientLimit { get; set; } = 3;

    /// <summary>Which built-in map to serve.</summary>
    public string Map { get; set; } = "ctf_truefort";

    public byte CapLimit { get; set; } = 5;

    public byte TimeLimitMinutes { get; set; } = 15;

    public byte RespawnTimeSeconds { get; set; } = 5;

    public ushort KillLimit { get; set; } = 30;

    public int TdmInvulnerabilitySeconds { get; set; } = 5;

    /// <summary>Per-class limit applied to all ten classes. 255 means no limit.</summary>
    public byte ClassLimit { get; set; } = 255;

    /// <summary>
    /// Where the built-in map PNGs live — the reference checkout's
    /// <c>Source/gg2/Included Files</c>.
    /// </summary>
    public string MapDirectory { get; set; } = string.Empty;

    public void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "port must be 0..65535");

        // The reference clamps at 48 (game_init.gml:152). Beyond 255 the roster
        // index stops fitting in the ubyte every message uses to name a player.
        if (PlayerLimit is < 1 or > 48)
            throw new ArgumentOutOfRangeException(nameof(PlayerLimit), PlayerLimit, "player limit must be 1..48");

        if (MapDirectory.Length == 0)
            throw new InvalidOperationException($"{nameof(MapDirectory)} must point at the built-in map PNGs");
    }
}
