namespace Gg2.Server;

/// <summary>
/// <c>global.shuffleRotation</c>: how the map rotation is ordered at startup.
/// </summary>
/// <remarks>
/// The reference stores this as a real and branches on it twice —
/// <c>if (global.shuffleRotation)</c> then <c>if (global.shuffleRotation == 1)</c>
/// (<c>GameServerCreate.gml:92-108</c>) — so 0, 1 and 2 are three distinct
/// behaviours and anything above 2 is a plain shuffle. The numeric values here
/// match the ini's, because <c>ServerConfigFile</c> reads them straight through.
/// </remarks>
public enum ShuffleMode
{
    /// <summary>Play the rotation in the order it was written.</summary>
    None = 0,

    /// <summary>
    /// Shuffle, then move a non-arena map to the front if the shuffle opened on
    /// an arena one. The reference's own default, and the ini's.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Why arena is special.</b> An arena round starts with one life each
    /// and no respawns, so a server that opens on one greets its first two
    /// joiners with a round that ends the moment one of them dies.
    /// </remarks>
    AvoidArenaFirst = 1,

    /// <summary>Shuffle and take whatever comes out.</summary>
    Any = 2,
}

/// <summary>
/// <c>GameServerCreate.gml:92-108</c>, the rotation shuffle.
/// </summary>
/// <remarks>
/// ⚠️ <b>Separate from <see cref="GameServer"/> so it can be tested with a seeded
/// <see cref="Random"/>.</b> The server itself passes <see cref="Random.Shared"/>:
/// a rotation that came out the same on every run would not be a shuffle, and
/// seeding it from anything stable about the server (the port, say — which is
/// what <c>BotPopulationService</c> does, for the opposite reason) would do
/// exactly that.
/// </remarks>
public static class MapRotationShuffle
{
    /// <summary>The reference's <c>'arena_'</c> prefix test.</summary>
    private const string ArenaPrefix = "arena_";

    /// <summary>
    /// A new list in shuffled order, or the input unchanged under
    /// <see cref="ShuffleMode.None"/>.
    /// </summary>
    public static IReadOnlyList<string> Apply(
        IReadOnlyList<string> rotation, ShuffleMode mode, Random random)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(random);

        if (mode == ShuffleMode.None || rotation.Count < 2)
            return rotation;

        var shuffled = new string[rotation.Count];
        for (var i = 0; i < rotation.Count; i++)
            shuffled[i] = rotation[i];

        random.Shuffle(shuffled);

        if (mode == ShuffleMode.AvoidArenaFirst && IsArena(shuffled[0]))
            MoveNonArenaToFront(shuffled);

        return shuffled;
    }

    /// <summary>
    /// The reference's swap loop, transcribed including the part that looks like
    /// a bug and is not worth diverging over.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It does not stop at the first non-arena map.</b> The GML loop runs
    /// to the end of the list and swaps <em>every</em> non-arena entry it meets
    /// into slot 0, so what actually opens the server is the <b>last</b>
    /// non-arena map in the shuffled order, not the first. The observable
    /// behaviour — "a shuffled rotation that does not open on arena" — is the
    /// same either way, which is why this transcribes rather than corrects: the
    /// rotation order is configuration, not simulation, and a session comparing
    /// the two implementations should find them identical.
    /// <para>
    /// An all-arena rotation leaves slot 0 alone and opens on arena, exactly as
    /// the reference does.
    /// </para>
    /// </remarks>
    private static void MoveNonArenaToFront(string[] shuffled)
    {
        for (var i = 0; i < shuffled.Length; i++)
        {
            if (IsArena(shuffled[i]))
                continue;

            (shuffled[0], shuffled[i]) = (shuffled[i], shuffled[0]);
        }
    }

    /// <summary>
    /// <c>string_copy(map, 0, 6) == 'arena_'</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Ordinal, and case-sensitive.</b> GML's <c>==</c> on strings is a
    /// byte compare, so a map called <c>Arena_x</c> is not an arena map to the
    /// reference either. Every shipped one is lowercase.
    /// </remarks>
    private static bool IsArena(string map) =>
        map.StartsWith(ArenaPrefix, StringComparison.Ordinal);
}
