namespace Gg2.Protocol;

/// <summary>
/// Indices into a player's stat array, from <c>Constants.xml</c>.
/// </summary>
/// <remarks>
/// Not all of these reach the wire, and the ones that do are not sent in this
/// order — <c>Player.events/User Event 12.xml</c> is the serialisation order.
/// </remarks>
public enum StatIndex
{
    Kills = 0,
    Deaths = 1,
    Caps = 2,
    Assists = 3,
    Destruction = 4,
    Stabs = 5,
    Healing = 6,
    Defenses = 7,
    Invulns = 8,
    Bonus = 9,
    Dominations = 10,
    Revenge = 11,
    Points = 12,
}
