namespace Gg2.Maps;

/// <summary>
/// The maps compiled into the stock client, and the names it accepts for them.
/// </summary>
/// <remarks>
/// <c>Scripts/Maps/findInternalMapName.gml</c>, transcribed case for case.
/// <para>
/// ⚠️ <b>This is the list that decides whether a map has to be DOWNLOADED.</b>
/// A client already has these 22 files, so the server advertises an empty MD5
/// and the download path is never entered. Anything else is a custom map: the
/// server must advertise its MD5 and be ready to serve the bytes, or a client
/// without it cannot join at all. Getting the membership wrong in either
/// direction is a join failure, not a cosmetic slip.
/// </para>
/// <para>
/// ⚠️ <b>Five of the names are ALIASES, and they are not decoration.</b>
/// <c>ctf_2dfort2</c>, <c>ctf_2dfortremix</c>, <c>dkoth_60s</c>,
/// <c>ctf_2dfort1</c> and <c>ctf_2dfort[0]</c> all resolve to a file under a
/// different name. A rotation naming one of them is naming a built-in map, and
/// treating it as custom would make every client download a file it already has
/// — under a name it will then fail to find.
/// </para>
/// <para>
/// ⚠️ <c>ctf_2dfort[0]</c> has square brackets in it and that is verbatim from
/// the reference, not a transcription slip. It reads like a typo for
/// <c>ctf_2dfort0</c> and is very likely one; the client's switch matches the
/// literal string, so a "correction" here would silently stop accepting a name
/// the stock client accepts.
/// </para>
/// </remarks>
public static class InternalMaps
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["ctf_truefort"] = "ctf_truefort.png",

        ["ctf_2dfort"] = "ctf_2dfort.png",
        ["ctf_2dfort2"] = "ctf_2dfort.png",
        ["ctf_2dfortremix"] = "ctf_2dfort.png",

        ["ctf_conflict"] = "ctf_conflict.png",
        ["ctf_classicwell"] = "ctf_classicwell.png",
        ["ctf_waterway"] = "ctf_waterway.png",
        ["ctf_orange"] = "ctf_orange.png",
        ["ctf_avanti"] = "ctf_avanti.png",
        ["cp_dirtbowl"] = "cp_dirtbowl.png",
        ["cp_egypt"] = "cp_egypt.png",
        ["arena_montane"] = "arena_montane.png",
        ["arena_lumberyard"] = "arena_lumberyard.png",
        ["gen_destroy"] = "gen_destroy.png",
        ["koth_harvest"] = "koth_harvest.png",
        ["koth_valley"] = "koth_valley.png",
        ["koth_corinth"] = "koth_corinth.png",
        ["dkoth_atalia"] = "dkoth_atalia.png",

        ["dkoth_sixties"] = "dkoth_sixties.png",
        ["dkoth_60s"] = "dkoth_sixties.png",

        ["tdm_mantic"] = "tdm_mantic.png",

        // ⚠️ Verbatim, brackets and all — see this type's remarks.
        ["ctf_2dfort[0]"] = "ctf_oldfort.png",
        ["ctf_2dfort1"] = "ctf_oldfort.png",
        ["ctf_oldfort"] = "ctf_oldfort.png",

        ["gg_debug"] = "gg_debug.png",
        ["koth_gallery"] = "koth_gallery.png",
        ["ctf_eiger"] = "ctf_eiger.png",
    };

    /// <summary>
    /// The file a name resolves to inside the client, or null if the client has
    /// no such map and it would have to be downloaded.
    /// </summary>
    public static string? FileFor(string name) => Files.GetValueOrDefault(name);

    /// <summary>Whether the stock client already has this map.</summary>
    public static bool IsInternal(string name) => Files.ContainsKey(name);

    /// <summary>Every name the client accepts, aliases included.</summary>
    public static IReadOnlyCollection<string> Names => Files.Keys;
}
