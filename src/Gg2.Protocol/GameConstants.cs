namespace Gg2.Protocol;

/// <summary>
/// The scalar half of <c>Gang-Garrison-2/Source/gg2/Constants.xml</c> — the
/// values that are not an id space of their own.
/// </summary>
/// <remarks>
/// Facts here were extracted on 2026-08-24 from GG2 v2.9.2. If the reference
/// fork moves, re-check <see cref="Version"/> and <see cref="ProtocolUuid"/>
/// first: those two decide whether any client can connect at all.
/// </remarks>
public static class GameConstants
{
    public const int Version = 29200;
    public const string GameVersionString = "v2.9.2";
    public const string GameNameString = "Gang Garrison 2";

    /// <summary>
    /// Sent as its 16 bytes in textual order, hyphens removed
    /// (<c>Scripts/Versioning/parseUuid.gml</c>). All 16 are compared by the
    /// server; a single wrong byte means no stock client ever connects.
    /// </summary>
    public const string ProtocolUuid = "b31c2209-4256-9a19-d0ef-c71c5373bd75";

    public const string Gg2LobbyUuid = "1ccf16b1-436d-856f-504d-cc1af306aaa7";

    /// <summary>Default listen port (<c>gg2.ini</c>, <c>HostingPort</c>).</summary>
    public const int DefaultPort = 8190;

    public const string LobbyServerHost = "ganggarrison.com";
    public const int LobbyServerPort = 29944;
    public const string AuthServerHost = "ganggarrison.com";
    public const int AuthServerPort = 45022;

    public const int MaxPlayerNameLength = 20;

    /// <summary>
    /// The reference's <c>STATE_UPDATE</c>, which is an alias of
    /// <c>FULL_UPDATE</c> rather than a message of its own — see
    /// <see cref="MessageType"/>.
    /// </summary>
    public const byte StateUpdate = (byte)MessageType.FullUpdate;

    public const int CharacterAnimationNormal = 0;
    public const int CharacterAnimationIntel = 2;
    public const int CharacterAnimationLen = 4;

    /// <summary>Ticks a damage contribution still counts for an assist.</summary>
    public const int AssistTime = 120;

    public const int HpPerPoint = 800;
    public const int IntelMaxTimer = 900;

    /// <summary>
    /// One mask cell is six world pixels square. The reference calls this
    /// <c>NAV_CELL_SIZE</c>, but it is not a bot-only number: it is the scale
    /// on the map's collision instance
    /// (<c>Objects/Collision maps/CollisionMapO.events/Create.xml</c>) and so
    /// the resolution of the entire collision world.
    /// </summary>
    public const int CellSize = 6;

    /// <summary>
    /// The fixed server tick rate. The reference supports 30 and 60 and threads
    /// four scaling globals through the physics to do it; a headless server has
    /// no reason to run at 60, so those globals are folded into constants here.
    /// See docs/SIMULATION.md, "Fix the tick rate at 30 Hz".
    /// </summary>
    public const int TickRate = 30;

    /// <summary>
    /// The reference's <c>PROTOCOL_UUID</c> as the 16 bytes that go on the wire.
    /// </summary>
    public static ReadOnlySpan<byte> ProtocolUuidBytes =>
    [
        0xb3, 0x1c, 0x22, 0x09, 0x42, 0x56, 0x9a, 0x19,
        0xd0, 0xef, 0xc7, 0x1c, 0x53, 0x73, 0xbd, 0x75,
    ];
}
