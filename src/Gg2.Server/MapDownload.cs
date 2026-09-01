using System.Security.Cryptography;

namespace Gg2.Server;

/// <summary>
/// Serving a custom map's bytes to a client that does not have it.
/// </summary>
/// <remarks>
/// <c>serviceJoiningPlayer.gml:131-144</c> answers <c>DOWNLOAD_MAP</c> with a
/// <c>uint</c> length and then streams the file; <c>:8-24</c> is the rate-limited
/// pump that follows.
/// <para>
/// ⚠️ <b>Only reachable for a map the client does NOT have built in.</b> The
/// server advertises an empty MD5 for one of <see cref="Gg2.Maps.InternalMaps"/>'
/// 22, and the reference's own guard (<c>advertisedMapMd5 != ""</c>) then refuses
/// to serve it. So the whole path turns on that list being right.
/// </para>
/// </remarks>
public static class MapDownload
{
    /// <summary>
    /// The reference's default bandwidth cap across all running downloads, in
    /// bytes per second.
    /// </summary>
    /// <remarks>
    /// <c>game_init.gml:94</c>, "Total bandwidth limit for map downloads in bytes
    /// per second", default 50000. ⚠️ <b>Total, not per client</b> — the
    /// reference divides it by the number of running downloads.
    /// </remarks>
    public const int DefaultBytesPerSecond = 50000;

    /// <summary>
    /// The smallest write the reference will make, so a download cannot be
    /// starved into single bytes.
    /// </summary>
    /// <remarks>
    /// <c>serviceJoiningPlayer.gml:12-15</c>: it waits until at least 400 bytes
    /// have accrued, and then sends at least 400 whatever the budget says.
    /// </remarks>
    public const int MinimumChunk = 400;

    /// <summary>
    /// A map file's MD5, in the form the client compares against.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>32 LOWERCASE hex characters, and this is measured rather than
    /// assumed.</b> <c>CustomMapGetMapMD5</c> calls <c>GG2DLL_compute_MD5</c>,
    /// which is a compiled extension — but its source ships in the reference at
    /// <c>Extensions/GG2DLL/GG2DLL/GG2DLL.cpp:485-516</c>, and it formats the
    /// digest with <c>sprintf("%02x")</c>. Uppercase would compare unequal on
    /// every client and every join of a custom map would turn into a download
    /// that then still mismatched.
    /// </remarks>
    public static string Md5Of(ReadOnlySpan<byte> file) =>
        Convert.ToHexStringLower(MD5.HashData(file));
}

/// <summary>
/// One client's in-progress map download.
/// </summary>
/// <remarks>
/// ⚠️ <b>The budget is a real, not an integer, and it accumulates.</b> The
/// reference carries <c>cumulativeMapBytes</c> as a fractional per-tick share and
/// only sends once at least <see cref="MapDownload.MinimumChunk"/> has built up.
/// Rounding the share to an integer per tick would stall a slow download
/// completely at low bandwidth — 50000/30 is 1666 bytes a tick shared between
/// however many clients are downloading, so four of them is 416 each and five
/// would truncate to a number below the minimum and never send.
/// </remarks>
public sealed class MapTransfer
{
    private readonly byte[] _file;

    private int _sent;

    private double _budget;

    public MapTransfer(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
    }

    /// <summary>Total size, which the client is told before the first byte.</summary>
    public int Length => _file.Length;

    public bool Complete => _sent >= _file.Length;

    /// <summary>
    /// Accrues this tick's share and returns what should go out now, empty if it
    /// is not yet worth sending.
    /// </summary>
    /// <param name="share">
    /// This client's slice of the tick's global budget, in bytes.
    /// </param>
    /// <param name="remainingThisTick">
    /// What is left of the whole server's per-tick allowance, which caps the
    /// write however much this client has accrued.
    /// </param>
    public ReadOnlySpan<byte> Next(double share, ref double remainingThisTick)
    {
        _budget += share;

        if (_budget < MapDownload.MinimumChunk || remainingThisTick <= 0)
            return default;

        // serviceJoiningPlayer.gml:15 — round(min(max(remaining, 400), budget)).
        // ⚠️ The max() is INSIDE the min(), so the floor of 400 applies to the
        // server-wide remainder and not to the result: a nearly exhausted budget
        // still sends 400 rather than dribbling. Transcribed in that order.
        var allowed = Math.Round(Math.Min(Math.Max(remainingThisTick, MapDownload.MinimumChunk), _budget));
        var count = (int)Math.Min(allowed, _file.Length - _sent);

        if (count <= 0)
            return default;

        var chunk = _file.AsSpan(_sent, count);

        _sent += count;
        _budget -= count;
        remainingThisTick -= count;

        return chunk;
    }
}
