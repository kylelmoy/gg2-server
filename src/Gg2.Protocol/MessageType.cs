namespace Gg2.Protocol;

/// <summary>
/// Every message id allocated by the reference implementation, transcribed from
/// <c>Gang-Garrison-2/Source/gg2/Constants.xml</c>.
/// </summary>
/// <remarks>
/// The same numeric space carries server-to-client messages, client-to-server
/// commands and a few reason codes, exactly as the reference does. The gaps
/// (26, 29, 48, 49) are unallocated there too; do not reuse them.
/// <para>
/// The reference's <c>STATE_UPDATE</c> is <b>not</b> here. It is an alias of
/// <c>FULL_UPDATE</c> — same value, same message — and a second enum member
/// sharing a value only makes <c>ToString()</c> pick unpredictably between the
/// two names. It lives as <see cref="GameConstants.StateUpdate"/> instead, so
/// the transcription is still complete.
/// </para>
/// </remarks>
public enum MessageType : byte
{
    Hello = 0,
    PlayerJoin = 1,
    PlayerLeave = 2,
    PlayerChangeTeam = 3,
    PlayerChangeClass = 4,
    PlayerSpawn = 5,
    InputState = 6,
    ChangeMap = 7,
    FullUpdate = 8,
    QuickUpdate = 9,
    PlayerDeath = 10,
    ServerFull = 11,
    RedTeamCap = 12,
    BlueTeamCap = 13,
    MapEnd = 14,
    ChatBubble = 15,
    BuildSentry = 16,
    DestroySentry = 17,
    Balance = 18,
    GrabIntel = 19,
    ScoreIntel = 20,
    DropIntel = 21,
    UberCharged = 22,
    Uber = 23,
    Omnomnomnom = 24,
    PasswordRequest = 25,
    PasswordWrong = 27,
    CapsUpdate = 28,
    CpCaptured = 30,
    PlayerChangeName = 31,
    GeneratorDestroy = 32,
    ArenaWaitForPlayers = 33,
    ArenaEndRound = 34,
    ArenaRestart = 35,
    UnlockCp = 36,
    ServerKick = 37,
    Kick = 38,
    KickName = 39,
    ArenaStartRound = 40,
    ToggleZoom = 41,
    ReturnIntel = 42,
    IncompatibleProtocol = 43,
    JoinUpdate = 44,
    DownloadMap = 45,
    SentryPosition = 46,
    RewardUpdate = 47,
    RewardRequest = 50,
    RewardChallengeCode = 51,
    RewardChallengeResponse = 52,
    MessageString = 53,
    WeaponFire = 54,
    PluginPacket = 55,
    KickBadPluginPacket = 56,
    Ping = 57,
    ClientSettings = 58,
    KickMultiClient = 59,
    ReserveSlot = 60,
}

/// <summary>Payload byte of a <see cref="MessageType.Kick"/>.</summary>
/// <remarks>
/// These share the message-id space in the reference; they are split out here
/// because they are only ever read as a kick's single payload byte
/// (<c>Scripts/Client/ClientBeginStep.gml</c>, case <c>KICK</c>).
/// </remarks>
public enum KickReason : byte
{
    Name = 39,
    BadPluginPacket = 56,
    MultiClient = 59,
}
