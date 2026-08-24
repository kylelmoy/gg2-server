namespace Gg2.Protocol;

/// <summary>Team ids, from <c>Constants.xml</c>.</summary>
/// <remarks>
/// <see cref="Any"/> never appears on the wire as a team a player is on: the
/// server treats any <c>PLAYER_CHANGETEAM</c> value that is not Red, Blue or
/// Spectator as "put me anywhere"
/// (<c>Scripts/GameServer/processClientCommands.gml</c>).
/// </remarks>
public enum Team : byte
{
    Red = 0,
    Blue = 1,
    Spectator = 2,
    Any = 3,
}

/// <summary>Class ids, from <c>Constants.xml</c>.</summary>
public enum PlayerClass : byte
{
    Scout = 0,
    Soldier = 1,
    Sniper = 2,
    Demoman = 3,
    Medic = 4,
    Engineer = 5,
    Heavy = 6,
    Spy = 7,
    Pyro = 8,
    Quote = 9,
}
