using System.Globalization;
using System.Security.Cryptography;
using Gg2.Maps;

namespace Gg2.Bots;

/// <summary>What a manifest had to say about one graph.</summary>
public enum NavMatch
{
    /// <summary>No manifest, or no line for this graph. Not an error.</summary>
    Unknown,

    /// <summary>The graph was built against exactly this geometry.</summary>
    Match,

    /// <summary>It was not. The graph describes terrain that is not there.</summary>
    Mismatch,
}

/// <summary>
/// <c>nav/manifest.tsv</c>: which map each checked-in nav graph was built
/// against, so a graph and a map that have drifted apart can say so.
/// </summary>
/// <remarks>
/// <b>This closes the one hole a nav cache has always had.</b>
/// <see cref="NavCache.Key"/> is keyed on the map <em>name</em>, deliberately —
/// its remarks explain why the MD5 cannot be the key — and the consequence is
/// that a graph built against an older version of a map loads perfectly and
/// hands bots a description of terrain that no longer exists. The header's mask
/// width and height catch a resize; they catch nothing else, and a geometry
/// edit that keeps the dimensions is invisible. It looks like a bot bug and is
/// not one.
/// <para>
/// <b>The hash is over the WALKMASK AND THE ENTITIES, not the map file.</b>
/// Those are the inputs a nav graph is actually built from, so this fires on the
/// changes that invalidate a graph and stays quiet for the ones that cannot —
/// re-drawn art, a re-encoded PNG, an added text chunk. Hashing the file would
/// cry wolf on all three, and a check that cries wolf gets turned off.
/// <see cref="HashOf(LevelMap)"/> has why the entities had to be in it, which is
/// a measurement rather than a precaution.
/// </para>
/// <para>
/// <b>A missing entry is <see cref="NavMatch.Unknown"/>, never a failure.</b>
/// An operator generating graphs for their own maps has no manifest line for
/// them, and must not be worse off than before this existed —
/// <c>NavCache.TryLoad</c>'s "a miss is not an error" contract, inherited from
/// <c>navCacheLoad.gml</c>, covers this too. The check only ever converts a
/// silent wrong answer into a loud absent one.
/// </para>
/// <para>
/// The checked-in manifest is gated by <c>NavManifestTests</c>, which recomputes
/// every line from <c>maps/</c> and rewrites the file when
/// <c>GG2_UPDATE_NAV_MANIFEST=1</c> is set. ✅ <b>Regenerating nav rewrites this
/// in the same command</b> — <c>gg2-server navgen</c> — which is not a
/// convenience: the previous workflow needed a separate step, and skipping it
/// left the old pairing recorded and every new graph refused at run time.
/// </para>
/// </remarks>
public sealed class NavManifest
{
    /// <summary>The manifest's filename inside a nav directory.</summary>
    public const string FileName = "manifest.tsv";

    private static readonly NavManifest Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

    // Keyed by NavCache.Key — "<map>_a<area>" — because that is the identity of
    // a graph, and the manifest is about graphs rather than about maps.
    private readonly Dictionary<string, string> _hashes;

    private NavManifest(Dictionary<string, string> hashes) => _hashes = hashes;

    /// <summary>How many graphs the manifest names.</summary>
    public int Count => _hashes.Count;

    /// <summary>
    /// The hash of everything a nav graph is built from: an area's walkmask and
    /// the entities standing on it.
    /// </summary>
    /// <remarks>
    /// <b>Pass the map ALREADY SLICED to its area</b> —
    /// <c>LevelMap.ForArea(n)</c> — because that is what the graph was built
    /// from and what the key names.
    /// <para>
    /// <b>The entities are in here because the walkmask alone is not enough.</b>
    /// <c>ForArea</c> slices entities but shares the mask across areas, so a
    /// mask-only hash cannot tell a multi-area map's stages apart and cannot see
    /// a door or a gate move — <c>cp_dirtbowl</c>'s three areas carry genuinely
    /// different graphs (835, 837 and 837 nodes) and must hash differently,
    /// which <c>NavManifestTests</c> asserts.
    /// </para>
    /// <para>
    /// <b>The direction of the trade is deliberate.</b> Hashing every entity
    /// property means a cosmetic entity edit can fail the check even though no
    /// graph changed — a false alarm whose cost is regenerating nav and
    /// re-recording, which is cheap and safe. The opposite error is a stale
    /// graph accepted silently, whose cost is bots pathing through terrain that
    /// is not there and a session spent looking for the bug in the bot layer.
    /// </para>
    /// <para>
    /// <b><see cref="Walkmask.ToCellBytes"/> is the canonical form on
    /// purpose.</b> The mask's own storage is packed words whose tail bits past
    /// the last column are unspecified, so hashing them would make the answer
    /// depend on padding rather than on geometry. One byte per cell is the
    /// representation the phase-0 parity fixture already compares against
    /// <c>gg2-agent/tools/walkmask.js</c>, so it is a shape that has been
    /// checked against something outside this repository.
    /// </para>
    /// <para>
    /// <b>Truncated to 16 bytes.</b> This is a staleness check, not a
    /// security boundary — nobody is constructing a second map to collide with a
    /// nav graph — and 32 hex characters keeps the manifest readable.
    /// </para>
    /// </remarks>
    public static string HashOf(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return HashOf(map.Walkmask.ToCellBytes(), map.Walkmask.Width, map.Walkmask.Height, map.Entities);
    }

    /// <summary>
    /// The same hash over parts, so a test can perturb one of them.
    /// </summary>
    internal static string HashOf(
        ReadOnlySpan<byte> cells,
        int width,
        int height,
        IReadOnlyList<LevelEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // The dimensions go in ahead of the cells so that two different shapes
        // holding the same number of cells cannot hash alike.
        Span<byte> header = stackalloc byte[8];
        BitConverter.TryWriteBytes(header, width);
        BitConverter.TryWriteBytes(header[4..], height);
        digest.AppendData(header);
        digest.AppendData(cells);

        // File order, NOT sorted. The reference walks entities in creation
        // order and LevelMap preserves it (ForArea's remarks), so order is part
        // of what a map IS — two maps differing only in the order of their
        // entities are different maps to the game, and should be to this.
        var text = new System.Text.StringBuilder();

        foreach (var entity in entities)
        {
            text.Clear();
            Describe(text, entity);
            digest.AppendData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset().AsSpan(0, 16));
    }

    /// <summary>
    /// One entity, rendered so that any change to it changes the text.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>LevelEntity.ToString()</c>, which is type and position
    /// only.</b> Using it here would have made scales and every property
    /// invisible to the check — including the gate and door properties a nav
    /// graph reads. "R" round-trips a double exactly, so a coordinate that moved
    /// by a ULP is still a change.
    /// </remarks>
    private static void Describe(System.Text.StringBuilder text, LevelEntity entity)
    {
        text.Append(entity.Type).Append('\x1f')
            .Append(entity.X.ToString("R", CultureInfo.InvariantCulture)).Append('\x1f')
            .Append(entity.Y.ToString("R", CultureInfo.InvariantCulture)).Append('\x1f')
            .Append(entity.XScale.ToString("R", CultureInfo.InvariantCulture)).Append('\x1f')
            .Append(entity.YScale.ToString("R", CultureInfo.InvariantCulture));

        // Properties are a dictionary and its enumeration order is not a
        // promise, so these ARE sorted — unlike the entities themselves, whose
        // order is real.
        foreach (var key in entity.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            text.Append('\x1f').Append(key).Append('=');
            Describe(text, entity.Properties[key]);
        }

        text.Append('\x1e');
    }

    private static void Describe(System.Text.StringBuilder text, GgonValue value)
    {
        switch (value)
        {
            case GgonString s:
                text.Append('"').Append(s.Value).Append('"');
                break;

            case GgonMap map:
                text.Append('{');
                foreach (var key in map.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    text.Append(key).Append(':');
                    Describe(text, map.Entries[key]);
                    text.Append(',');
                }

                text.Append('}');
                break;

            default:
                // A value shape nobody has added yet. Named rather than ignored:
                // a silent default would make a new kind of property invisible
                // to the check, which is the failure this whole file is about.
                text.Append("<unknown ").Append(value.GetType().Name).Append('>');
                break;
        }
    }

    /// <summary>
    /// Reads the manifest in <paramref name="directory"/>, or an empty one.
    /// </summary>
    /// <remarks>
    /// <b>Every failure answers "empty", including a malformed file.</b> A
    /// manifest that cannot be read must not stop a server serving rounds; the
    /// worst it can do is give up the check it was added to provide. An
    /// <see cref="IOException"/> is deliberately not caught — a directory that
    /// cannot be read at all is a real problem and the graph load beside this is
    /// about to hit it too.
    /// </remarks>
    public static NavManifest Load(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var path = Path.Combine(directory, FileName);

        if (!File.Exists(path))
            return Empty;

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            var text = line.AsSpan().Trim();

            if (text.IsEmpty || text[0] == '#')
                continue;

            var tab = text.IndexOf('\t');
            if (tab <= 0)
                continue;

            var key = text[..tab].Trim().ToString();
            var hash = text[(tab + 1)..].Trim().ToString();

            if (key.Length > 0 && hash.Length > 0)
                hashes[key] = hash;
        }

        return new NavManifest(hashes);
    }

    /// <summary>
    /// Writes a manifest naming each graph and the walkmask it was built from.
    /// </summary>
    public static void Write(string path, IEnumerable<(string Key, string Hash)> entries)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(entries);

        var lines = new List<string>
        {
            "# Which map each nav graph in this directory was built against.",
            "# <map>_a<area><TAB><first 16 bytes of SHA-256 over the area's walkmask and entities>",
            "#",
            "# Written by: dotnet run --project src/Gg2.Host -- navgen",
            "# Re-recorded by: GG2_UPDATE_NAV_MANIFEST=1 dotnet test --filter NavManifestTests",
            "# A graph with no line here is not checked. See NavManifest.",
        };

        foreach (var (key, hash) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{key}\t{hash}"));

        // LF explicitly, not File.WriteAllLines, which writes
        // Environment.NewLine: the same maps would otherwise produce different
        // BYTES on Windows and on Linux, and the checked-in manifest would flip
        // back and forth depending on who last re-recorded it. .gitattributes
        // normalises it as well; this makes the file right before git has to.
        File.WriteAllText(path, string.Join('\n', lines) + '\n');
    }

    /// <summary>
    /// What this manifest says about a graph built for <paramref name="key"/>
    /// being used against a walkmask hashing to <paramref name="walkmaskHash"/>.
    /// </summary>
    public NavMatch Check(string key, string walkmaskHash)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(walkmaskHash);

        if (!_hashes.TryGetValue(key, out var expected))
            return NavMatch.Unknown;

        return string.Equals(expected, walkmaskHash, StringComparison.OrdinalIgnoreCase)
            ? NavMatch.Match
            : NavMatch.Mismatch;
    }

    /// <summary>The hash recorded for a graph, or null.</summary>
    public string? HashFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _hashes.GetValueOrDefault(key);
    }
}
