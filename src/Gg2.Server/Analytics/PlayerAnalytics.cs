using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Gg2.Server;

/// <summary>One player this server has seen, as far as it can tell.</summary>
/// <remarks>
/// ⛔ <b>"A player" here is an ADDRESS, hashed.</b> The protocol has no account
/// and no durable identity — <c>BanList</c> says the same thing at more length —
/// so uniqueness is approximated by the only stable thing a connection carries.
/// It is wrong in both directions: a household behind one address is one
/// player, and one person on a mobile network is a new player every day. Every
/// "unique" number this class produces inherits that, and
/// <c>docs/ANALYTICS.md</c> states it beside the numbers rather than in a
/// footnote.
/// </remarks>
public sealed class AnalyticsPlayer
{
    /// <summary>The salted hash that stands in for the address. See <see cref="PlayerAnalytics.KeyFor"/>.</summary>
    public required string Key { get; init; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// The last calendar day this player was seen on, <c>yyyy-MM-dd</c>.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes "unique players today" survive a restart.</b> The
    /// alternative — a set of keys per day — is the only other way to answer it
    /// and costs days × players to store. One field per player answers the same
    /// question in O(1) and is exact.
    /// </remarks>
    public string LastDay { get; set; } = string.Empty;

    public int Sessions { get; set; }

    public long PlaytimeSeconds { get; set; }

    /// <summary>The most recent name this address played under.</summary>
    public string Name { get; set; } = string.Empty;

    public string CountryCode { get; set; } = string.Empty;

    public string CountryName { get; set; } = string.Empty;

    public string RegionCode { get; set; } = string.Empty;

    public string RegionName { get; set; } = string.Empty;

    /// <summary>Which <see cref="AnalyticsRegion"/> this player is counted in.</summary>
    public string RegionKey { get; set; } = string.Empty;
}

/// <summary>One calendar day, in UTC.</summary>
public sealed class AnalyticsDay
{
    public required string Date { get; init; }

    public int Sessions { get; set; }

    /// <summary>Players seen on this day who had not been seen on it before.</summary>
    public int UniquePlayers { get; set; }

    /// <summary>Of those, the ones this server had never seen at all.</summary>
    public int NewPlayers { get; set; }

    public long PlaytimeSeconds { get; set; }

    public int PeakConcurrent { get; set; }
}

/// <summary>
/// One place players connect from: a country, or a US state.
/// </summary>
/// <remarks>
/// <b>Only the United States is subdivided</b>, which is what was asked for and
/// is also the only subdivision most free databases get consistently right. A
/// German player is counted under <c>DE</c> whether or not the database knows
/// which Land they are in.
/// </remarks>
public sealed class AnalyticsRegion
{
    /// <summary><c>DE</c>, or <c>US-CA</c>.</summary>
    public required string Key { get; init; }

    public required string CountryCode { get; init; }

    public string CountryName { get; set; } = string.Empty;

    public string RegionCode { get; init; } = string.Empty;

    public string RegionName { get; set; } = string.Empty;

    /// <summary>
    /// Unique players first seen here.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Monotone, and it does not fall when the player table is trimmed.</b>
    /// Trimming evicts the least recently seen player records to keep the file
    /// bounded; decrementing this there would make the geography quietly
    /// disagree with the all-time total beside it. The cost is that a player
    /// evicted and later returning is counted twice — the same cost
    /// <see cref="PlayerAnalytics.UniquePlayers"/> pays, and for the same
    /// reason.
    /// </remarks>
    public int Players { get; set; }

    public int Sessions { get; set; }

    public long PlaytimeSeconds { get; set; }
}

/// <summary>A connection currently being timed.</summary>
public sealed class AnalyticsSession
{
    /// <summary>The record this connection's time is added to.</summary>
    public required AnalyticsPlayer Player { get; init; }

    public required DateTime StartUtc { get; init; }

    /// <summary>The day the session STARTED. See <c>PlayerAnalytics.End</c>.</summary>
    public required string Day { get; init; }

    /// <summary>Which <see cref="AnalyticsRegion"/> this session's playtime lands in.</summary>
    public required string RegionKey { get; init; }
}

/// <summary>
/// Who has played on this server, for how long, and roughly from where —
/// <c>gg2-server --analytics &lt;path&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless a path is given</b>, like <c>--record</c> and for the same
/// reason: keeping a durable record of the people who played on a server is a
/// decision an operator makes, not a default they discover. With no path
/// nothing is counted and nothing is written.
/// </para>
/// <para>
/// <b>What is stored, and what deliberately is not.</b> An address is hashed
/// with a per-file random salt and never written down — so the file cannot be
/// turned back into a list of who connected, and the salt going missing turns
/// it into nothing at all. The player's chosen name IS stored, because it is
/// the only thing an operator can recognise somebody by, and it is a name they
/// broadcast to everybody in the server anyway. Geography is stored as a
/// country and, in the United States, a state — never a city, never a
/// coordinate, and never the address that produced it.
/// </para>
/// <para>
/// ⚠️ <b>Everything here runs on the tick thread</b>, called from
/// <c>GameServer</c>'s join and leave paths. There are no locks. The one thing
/// that leaves the thread is the file write — see <see cref="Flush"/>.
/// </para>
/// <para>
/// <b>Bots are never counted.</b> They have no connection, so nothing calls
/// <see cref="Begin"/> for them. A server that fills itself with twelve bots
/// reports the humans.
/// </para>
/// </remarks>
public sealed class PlayerAnalytics : IDisposable
{
    /// <summary>The file's format version. Bumping it makes an older file unreadable, by design.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// How many player records are kept.
    /// </summary>
    /// <remarks>
    /// <b>A bound is not optional here.</b> This grows once per address for the
    /// life of the server, and a public 24/7 server sees a lot of addresses;
    /// unbounded, the file and the flush that writes it grow without limit and
    /// the first anybody knows is a late tick. Twenty thousand records is about
    /// 2 MB of file. The all-time counters are kept separately and are not
    /// affected by trimming.
    /// </remarks>
    public const int DefaultPlayerCap = 20_000;

    /// <summary>How many days of history are kept — a little over two years.</summary>
    private const int DayCap = 800;

    /// <summary>How many places are kept. Comfortably more than there are countries and states.</summary>
    private const int RegionCap = 1_000;

    /// <summary>How many players the panel is sent.</summary>
    private const int TopPlayers = 25;

    /// <summary>
    /// How often the file is rewritten while the server is running.
    /// </summary>
    /// <remarks>
    /// <b>Five minutes is what a hard kill costs.</b> A clean stop flushes, so
    /// this only matters when the process is killed or the machine loses power.
    /// Shortening it does not make the write cheaper — the whole file is
    /// rewritten either way — it just does it more often.
    /// </remarks>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly IServerLog _log;
    private readonly int _playerCap;
    private readonly Dictionary<string, AnalyticsPlayer> _players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalyticsDay> _days = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalyticsRegion> _regions = new(StringComparer.Ordinal);
    private readonly List<AnalyticsSession> _live = [];
    private readonly AnalyticsWriter _writer;

    private byte[] _salt = [];
    private bool _dirty;
    private DateTime _lastFlushUtc = DateTime.MinValue;

    /// <param name="path">Where the file lives. Empty turns the whole feature off.</param>
    /// <param name="log">Told when the file cannot be read or written.</param>
    /// <param name="geo">The geo database, or null for no geography.</param>
    /// <param name="playerCap">Overridden only by tests, which need to see trimming happen.</param>
    public PlayerAnalytics(string path, IServerLog log, GeoIpDatabase? geo, int playerCap = DefaultPlayerCap)
    {
        ArgumentNullException.ThrowIfNull(log);

        _path = path ?? string.Empty;
        _log = log;
        _playerCap = Math.Max(1, playerCap);
        Geo = geo;
        _writer = new AnalyticsWriter(_path, log);
    }

    /// <summary>Whether anything is being counted at all.</summary>
    public bool Enabled => _path.Length != 0;

    /// <summary>The geo database, or null. Exposed so the panel can name it.</summary>
    public GeoIpDatabase? Geo { get; }

    /// <summary>When this file first recorded anything.</summary>
    public DateTime SinceUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// Distinct players ever seen.
    /// </summary>
    /// <remarks>
    /// Counted when a player record is CREATED, not by measuring the table, so
    /// it survives trimming. See <see cref="AnalyticsRegion.Players"/> for what
    /// that costs.
    /// </remarks>
    public long UniquePlayers { get; private set; }

    public long TotalSessions { get; private set; }

    /// <summary>Connected seconds, summed over every finished session.</summary>
    public long TotalPlaytimeSeconds { get; private set; }

    public int PeakConcurrent { get; private set; }

    public DateTime PeakConcurrentUtc { get; private set; }

    /// <summary>Players evicted by the cap. Nonzero means the table has wrapped.</summary>
    public int Trimmed { get; private set; }

    public int TrackedPlayers => _players.Count;

    public IReadOnlyCollection<AnalyticsRegion> Regions => _regions.Values;

    public IReadOnlyCollection<AnalyticsDay> Days => _days.Values;

    /* ---------- the two events -------------------------------------------- */

    /// <summary>
    /// A human has finished joining. Returns the handle <see cref="End"/> wants,
    /// or null when analytics are off.
    /// </summary>
    /// <param name="address">Hashed immediately and not retained.</param>
    /// <param name="name">Whatever they typed into their own client.</param>
    /// <param name="nowUtc">The join time.</param>
    /// <param name="concurrent">
    /// Humans connected INCLUDING this one — what the peak is measured from.
    /// </param>
    public AnalyticsSession? Begin(IPAddress address, string name, DateTime nowUtc, int concurrent)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!Enabled)
            return null;

        var key = KeyFor(address);
        var day = DayOf(nowUtc);
        var location = Geo?.Lookup(address) ?? GeoLocation.Unknown;
        var regionKey = RegionKeyFor(location);

        if (!_players.TryGetValue(key, out var player))
        {
            player = new AnalyticsPlayer { Key = key, FirstSeenUtc = nowUtc };
            _players[key] = player;
            UniquePlayers++;
            Today(day).NewPlayers++;
        }

        // ⚠️ Before LastDay is moved on. A player already seen today is not a
        // new unique for today, and this is the only thing that knows.
        if (!string.Equals(player.LastDay, day, StringComparison.Ordinal))
            Today(day).UniquePlayers++;

        player.LastDay = day;
        player.LastSeenUtc = nowUtc;
        player.Name = name ?? string.Empty;
        player.Sessions++;

        if (location.IsKnown)
        {
            player.CountryCode = location.CountryCode;
            player.CountryName = location.CountryName;
            player.RegionCode = location.RegionCode;
            player.RegionName = location.RegionName;
        }

        Attribute(player, regionKey, location);

        var region = Region(regionKey, location);
        region.Sessions++;

        TotalSessions++;
        Today(day).Sessions++;

        if (concurrent > PeakConcurrent)
        {
            PeakConcurrent = concurrent;
            PeakConcurrentUtc = nowUtc;
        }

        var today = Today(day);

        if (concurrent > today.PeakConcurrent)
            today.PeakConcurrent = concurrent;

        var session = new AnalyticsSession
        {
            Player = player,
            StartUtc = nowUtc,
            Day = day,
            RegionKey = regionKey,
        };

        _live.Add(session);
        _dirty = true;

        return session;
    }

    /// <summary>
    /// That connection has gone. Adds its duration to every total.
    /// </summary>
    /// <remarks>
    /// <b>A session that crosses midnight is charged entirely to the day it
    /// STARTED on.</b> Splitting it would be more accurate and would mean a
    /// day's row could still change after the day ended, which is the property
    /// that makes the daily series safe to draw as a finished bar.
    /// </remarks>
    public void End(AnalyticsSession? session, DateTime nowUtc)
    {
        if (session is null || !Enabled)
            return;

        if (!_live.Remove(session))
            return;

        // A clock that stepped backwards must not subtract playtime.
        var seconds = (long)Math.Max(0, (nowUtc - session.StartUtc).TotalSeconds);

        session.Player.PlaytimeSeconds += seconds;
        session.Player.LastSeenUtc = nowUtc;
        TotalPlaytimeSeconds += seconds;
        Today(session.Day).PlaytimeSeconds += seconds;

        if (_regions.TryGetValue(session.RegionKey, out var region))
            region.PlaytimeSeconds += seconds;

        _dirty = true;
    }

    /// <summary>
    /// Housekeeping: writes the file when it is time to. Cheap on every other
    /// call.
    /// </summary>
    public void Housekeeping(DateTime nowUtc)
    {
        if (!Enabled || !_dirty || nowUtc - _lastFlushUtc < FlushInterval)
            return;

        Flush(nowUtc);
    }

    /* ---------- the panel's payload ---------------------------------------- */

    /// <summary>
    /// Everything the analytics view draws, as the <c>analytics</c> object of a
    /// control-command reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Days and regions go over the wire as arrays of bare values</b>, against
    /// a field list the page holds — the same trade <c>PerfHistory</c> makes,
    /// for the same reason: eight hundred days of six named fields is mostly
    /// repeated key names.
    /// </para>
    /// <para>
    /// <b>Live sessions are added to the totals here rather than being stored.</b>
    /// A player who has been on for an hour has contributed an hour, and a
    /// figure that only moved when somebody left would sit still all evening on
    /// a busy server and jump when it emptied.
    /// </para>
    /// </remarks>
    public string ToJson(DateTime nowUtc)
    {
        var text = new StringBuilder(4096);

        text.Append("\"enabled\":").Append(Enabled ? "true" : "false");

        if (!Enabled)
        {
            // The one thing an operator needs from an "off" reply is how to
            // turn it on, so the panel does not have to carry the flag's name.
            return text
                .Append(",\"hint\":\"pass --analytics <path>, or set [Analytics] File in the config file\"")
                .ToString();
        }

        var liveSeconds = 0L;

        foreach (var session in _live)
            liveSeconds += (long)Math.Max(0, (nowUtc - session.StartUtc).TotalSeconds);

        text.Append(",\"file\":\"").Append(ControlCommands.Escape(_path)).Append('"')
            .Append(",\"format\":").Append(FormatVersion)
            .Append(",\"since\":\"").Append(Iso(SinceUtc)).Append('"')
            .Append(",\"now\":\"").Append(Iso(nowUtc)).Append('"');

        text.Append(",\"totals\":{")
            .Append("\"uniquePlayers\":").Append(UniquePlayers)
            .Append(",\"sessions\":").Append(TotalSessions)
            .Append(",\"playtimeSeconds\":").Append(TotalPlaytimeSeconds + liveSeconds)
            .Append(",\"peakConcurrent\":").Append(PeakConcurrent)
            .Append(",\"peakUtc\":\"").Append(Iso(PeakConcurrentUtc)).Append('"')
            .Append(",\"live\":").Append(_live.Count)
            .Append(",\"tracked\":").Append(_players.Count)
            .Append(",\"cap\":").Append(_playerCap)
            .Append(",\"trimmed\":").Append(Trimmed)
            .Append('}');

        // date, sessions, unique, new, playtimeSeconds, peak
        text.Append(",\"dayFields\":[\"date\",\"sessions\",\"unique\",\"new\",\"playtimeSeconds\",\"peak\"]")
            .Append(",\"days\":[");

        var first = true;

        foreach (var day in _days.Values.OrderBy(d => d.Date, StringComparer.Ordinal))
        {
            if (!first)
                text.Append(',');

            first = false;

            text.Append("[\"").Append(day.Date).Append("\",")
                .Append(day.Sessions).Append(',')
                .Append(day.UniquePlayers).Append(',')
                .Append(day.NewPlayers).Append(',')
                .Append(day.PlaytimeSeconds).Append(',')
                .Append(day.PeakConcurrent).Append(']');
        }

        text.Append(']');

        text.Append(",\"regionFields\":[\"key\",\"country\",\"countryName\",\"region\",\"regionName\","
            + "\"players\",\"sessions\",\"playtimeSeconds\"]")
            .Append(",\"regions\":[");

        first = true;

        foreach (var region in _regions.Values.OrderByDescending(r => r.Players).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            if (!first)
                text.Append(',');

            first = false;

            text.Append("[\"").Append(ControlCommands.Escape(region.Key)).Append("\",\"")
                .Append(ControlCommands.Escape(region.CountryCode)).Append("\",\"")
                .Append(ControlCommands.Escape(region.CountryName)).Append("\",\"")
                .Append(ControlCommands.Escape(region.RegionCode)).Append("\",\"")
                .Append(ControlCommands.Escape(region.RegionName)).Append("\",")
                .Append(region.Players).Append(',')
                .Append(region.Sessions).Append(',')
                .Append(region.PlaytimeSeconds).Append(']');
        }

        text.Append(']');

        AppendTopPlayers(text, nowUtc);
        AppendLive(text, nowUtc);

        text.Append(",\"geoip\":");

        if (Geo is null)
        {
            text.Append("null");
        }
        else
        {
            text.Append('{').Append(Geo.ToJsonFields()).Append('}');
        }

        return text.ToString();
    }

    /// <summary>
    /// The longest-playing players, selected rather than sorted.
    /// </summary>
    /// <remarks>
    /// A bounded insertion over the table, because this runs on the tick thread
    /// and sorting twenty thousand records to show twenty-five of them would be
    /// milliseconds every time the panel refreshed.
    /// </remarks>
    private void AppendTopPlayers(StringBuilder text, DateTime nowUtc)
    {
        var top = new List<AnalyticsPlayer>(TopPlayers + 1);

        // Live time counts, or the player at the top of the board right now is
        // the one who is not on it.
        var extra = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var session in _live)
        {
            var seconds = (long)Math.Max(0, (nowUtc - session.StartUtc).TotalSeconds);
            extra[session.Player.Key] = extra.GetValueOrDefault(session.Player.Key) + seconds;
        }

        long Total(AnalyticsPlayer p) => p.PlaytimeSeconds + extra.GetValueOrDefault(p.Key);

        foreach (var player in _players.Values)
        {
            var seconds = Total(player);
            var at = top.Count;

            while (at > 0 && Total(top[at - 1]) < seconds)
                at--;

            if (at >= TopPlayers)
                continue;

            top.Insert(at, player);

            if (top.Count > TopPlayers)
                top.RemoveAt(top.Count - 1);
        }

        text.Append(",\"top\":[");

        for (var i = 0; i < top.Count; i++)
        {
            if (i != 0)
                text.Append(',');

            var player = top[i];

            text.Append("{\"name\":\"").Append(ControlCommands.Escape(player.Name)).Append('"')
                .Append(",\"sessions\":").Append(player.Sessions)
                .Append(",\"playtimeSeconds\":").Append(Total(player))
                .Append(",\"firstSeen\":\"").Append(Iso(player.FirstSeenUtc)).Append('"')
                .Append(",\"lastSeen\":\"").Append(Iso(player.LastSeenUtc)).Append('"')
                .Append(",\"country\":\"").Append(ControlCommands.Escape(player.CountryCode)).Append('"')
                .Append(",\"region\":\"").Append(ControlCommands.Escape(player.RegionCode)).Append('"')
                .Append(",\"live\":").Append(extra.ContainsKey(player.Key) ? "true" : "false")
                .Append('}');
        }

        text.Append(']');
    }

    private void AppendLive(StringBuilder text, DateTime nowUtc)
    {
        text.Append(",\"connected\":[");

        for (var i = 0; i < _live.Count; i++)
        {
            if (i != 0)
                text.Append(',');

            var session = _live[i];

            text.Append("{\"name\":\"").Append(ControlCommands.Escape(session.Player.Name)).Append('"')
                .Append(",\"seconds\":").Append((long)Math.Max(0, (nowUtc - session.StartUtc).TotalSeconds))
                .Append(",\"country\":\"").Append(ControlCommands.Escape(session.Player.CountryCode)).Append('"')
                .Append(",\"region\":\"").Append(ControlCommands.Escape(session.Player.RegionCode)).Append('"')
                .Append(",\"key\":\"").Append(session.RegionKey).Append('"')
                .Append('}');
        }

        text.Append(']');
    }

    /* ---------- keys, days and places -------------------------------------- */

    /// <summary>
    /// The stand-in for an address: <c>SHA-256(salt || address)</c>, truncated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The salt is what makes the file safe to keep.</b> Without one, a
    /// hashed address is not anonymous at all — the whole IPv4 space is four
    /// billion hashes, which is minutes of work. With a random per-file salt
    /// the table cannot be reversed, and cannot be cross-referenced against any
    /// other server's.
    /// </para>
    /// <para>
    /// <b>96 bits is enough and 256 is waste.</b> At the twenty-thousand-record
    /// cap the chance of any collision is about 10^-17; the file is thirty per
    /// cent smaller for it.
    /// </para>
    /// <para>
    /// ⚠️ <b>This is not a security control.</b> An operator who has the file
    /// AND the salt AND a suspected address can confirm the address is in it.
    /// That is inherent — it is what makes the same address count as the same
    /// player — and it is why the salt is generated per file rather than being
    /// something an operator picks.
    /// </para>
    /// </remarks>
    public string KeyFor(IPAddress address)
    {
        Span<byte> raw = stackalloc byte[16];
        address.TryWriteBytes(raw, out var written);

        Span<byte> message = stackalloc byte[64];
        _salt.CopyTo(message);
        raw[..written].CopyTo(message[_salt.Length..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(message[..(_salt.Length + written)], hash);

        return Convert.ToHexStringLower(hash[..12]);
    }

    private static string DayOf(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Iso(DateTime utc) =>
        utc == default ? string.Empty : utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary><c>US-CA</c> inside the United States, the country code outside it, <c>??</c> for nowhere.</summary>
    private static string RegionKeyFor(in GeoLocation location)
    {
        if (!location.IsKnown)
            return "??";

        return location.CountryCode == "US" && location.RegionCode.Length != 0
            ? $"US-{location.RegionCode}"
            : location.CountryCode;
    }

    private AnalyticsDay Today(string day)
    {
        if (_days.TryGetValue(day, out var existing))
            return existing;

        var created = new AnalyticsDay { Date = day };
        _days[day] = created;

        // Oldest first, so what is dropped is what nobody is looking at.
        while (_days.Count > DayCap)
        {
            var oldest = string.Empty;

            foreach (var date in _days.Keys)
            {
                if (oldest.Length == 0 || string.CompareOrdinal(date, oldest) < 0)
                    oldest = date;
            }

            _days.Remove(oldest);
        }

        return created;
    }

    private AnalyticsRegion Region(string key, in GeoLocation location)
    {
        if (_regions.TryGetValue(key, out var existing))
        {
            // A database rebuild renames places. Take the newest name.
            if (location.CountryName.Length != 0)
                existing.CountryName = location.CountryName;

            if (location.RegionName.Length != 0)
                existing.RegionName = location.RegionName;

            return existing;
        }

        var created = new AnalyticsRegion
        {
            Key = key,
            CountryCode = location.CountryCode,
            CountryName = location.CountryName,
            RegionCode = key.StartsWith("US-", StringComparison.Ordinal) ? location.RegionCode : string.Empty,
            RegionName = key.StartsWith("US-", StringComparison.Ordinal) ? location.RegionName : string.Empty,
        };

        if (_regions.Count < RegionCap)
            _regions[key] = created;

        return created;
    }

    /// <summary>
    /// Moves a player between places, keeping <see cref="AnalyticsRegion.Players"/>
    /// a count of players rather than of sessions.
    /// </summary>
    private void Attribute(AnalyticsPlayer player, string regionKey, in GeoLocation location)
    {
        if (string.Equals(player.RegionKey, regionKey, StringComparison.Ordinal))
            return;

        // An address that used to resolve and now does not is left where it
        // was: "the database no longer covers this address" is not the same
        // event as "this player moved to nowhere", and only one of them should
        // move a dot on the map.
        if (regionKey == "??" && player.RegionKey.Length != 0)
            return;

        if (player.RegionKey.Length != 0 && _regions.TryGetValue(player.RegionKey, out var previous))
            previous.Players = Math.Max(0, previous.Players - 1);

        Region(regionKey, location).Players++;
        player.RegionKey = regionKey;
    }

    /* ---------- the file --------------------------------------------------- */

    /// <summary>
    /// Reads the file, or starts an empty one with a fresh salt.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A malformed line is skipped, not fatal</b> — <c>BanList.Load</c>'s
    /// argument applies here with more force, because this file is written by a
    /// machine and read after a crash: a truncated last line must cost one
    /// player record, not the server's whole history.
    /// </remarks>
    public void Load()
    {
        if (!Enabled)
            return;

        _lastFlushUtc = DateTime.UtcNow;

        if (!File.Exists(_path))
        {
            _salt = RandomNumberGenerator.GetBytes(16);
            SinceUtc = DateTime.UtcNow;
            _log.Info($"analytics: starting a new file at {_path}");

            // ⚠️ Written NOW, empty, rather than at the first flush five minutes
            // from now. Two reasons, and the second is the real one:
            //
            //   - a log line saying a file was started, with no file next to it,
            //     is indistinguishable from a path the server cannot write to —
            //     and this is the moment an operator is watching for exactly
            //     that;
            //   - the salt is what makes an address the same player across
            //     restarts, and it only exists in this file. A process killed
            //     inside its first five minutes would otherwise generate a
            //     different one next time and silently restart identity.
            //
            // ⛔ SYNCHRONOUS, unlike every other write. This one runs at
            // startup, before the listener binds, on the same thread that has
            // just read the directory — and its whole purpose is that the file
            // is there when the next thing looks. Posting it to the writer
            // thread would make "the file exists" true at some unspecified
            // later moment, which is not the property being bought.
            _writer.WriteNow(Render());
            _lastFlushUtc = DateTime.UtcNow;
            return;
        }

        string[] lines;

        try
        {
            lines = File.ReadAllLines(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"could not read the analytics file {_path}: {e.Message}; starting empty");
            _salt = RandomNumberGenerator.GetBytes(16);
            return;
        }

        var bad = 0;

        foreach (var line in lines)
        {
            // ⚠️ NOT trimmed. The last column is the player's chosen name and
            // it may legitimately be empty; Trim() would take the tab before it
            // away, turning every nameless player into a short line this drops
            // as malformed.
            if (line.Length == 0 || line[0] == '#')
                continue;

            var f = line.Split('\t');

            try
            {
                switch (f[0])
                {
                    case "!meta" when f.Length >= 8:
                        _salt = Convert.FromHexString(f[1]);
                        SinceUtc = ParseUtc(f[2]);
                        UniquePlayers = long.Parse(f[3], CultureInfo.InvariantCulture);
                        TotalSessions = long.Parse(f[4], CultureInfo.InvariantCulture);
                        TotalPlaytimeSeconds = long.Parse(f[5], CultureInfo.InvariantCulture);
                        PeakConcurrent = int.Parse(f[6], CultureInfo.InvariantCulture);
                        PeakConcurrentUtc = ParseUtc(f[7]);
                        break;

                    case "!day" when f.Length >= 7:
                        _days[f[1]] = new AnalyticsDay
                        {
                            Date = f[1],
                            Sessions = int.Parse(f[2], CultureInfo.InvariantCulture),
                            UniquePlayers = int.Parse(f[3], CultureInfo.InvariantCulture),
                            NewPlayers = int.Parse(f[4], CultureInfo.InvariantCulture),
                            PlaytimeSeconds = long.Parse(f[5], CultureInfo.InvariantCulture),
                            PeakConcurrent = int.Parse(f[6], CultureInfo.InvariantCulture),
                        };

                        break;

                    case "!region" when f.Length >= 9:
                        _regions[f[1]] = new AnalyticsRegion
                        {
                            Key = f[1],
                            CountryCode = f[2],
                            CountryName = f[3],
                            RegionCode = f[4],
                            RegionName = f[5],
                            Players = int.Parse(f[6], CultureInfo.InvariantCulture),
                            Sessions = int.Parse(f[7], CultureInfo.InvariantCulture),
                            PlaytimeSeconds = long.Parse(f[8], CultureInfo.InvariantCulture),
                        };

                        break;

                    case "!player" when f.Length >= 13:
                        _players[f[1]] = new AnalyticsPlayer
                        {
                            Key = f[1],
                            FirstSeenUtc = ParseUtc(f[2]),
                            LastSeenUtc = ParseUtc(f[3]),
                            LastDay = f[4],
                            Sessions = int.Parse(f[5], CultureInfo.InvariantCulture),
                            PlaytimeSeconds = long.Parse(f[6], CultureInfo.InvariantCulture),
                            CountryCode = f[7],
                            RegionCode = f[8],
                            RegionKey = f[9],
                            CountryName = f[10],
                            RegionName = f[11],
                            Name = f[12],
                        };

                        break;

                    default:
                        bad++;
                        break;
                }
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                bad++;
            }
        }

        if (_salt.Length == 0)
        {
            // No usable !meta line: the file is from a future format, or its
            // first line was lost. A fresh salt makes every returning player
            // look new, which is wrong but recoverable; reusing an empty salt
            // would silently make the hashes reversible.
            _salt = RandomNumberGenerator.GetBytes(16);
            _log.Warn($"{_path}: no salt in the file; player identity restarts from here");
        }

        _log.Info(
            $"analytics: {UniquePlayers:N0} players all time, {_players.Count:N0} tracked, "
            + $"{_days.Count} day(s), {_regions.Count} place(s) from {_path}"
            + (bad == 0 ? string.Empty : $"; {bad} line(s) skipped"));
    }

    /// <summary>
    /// Renders the file and hands it to the writer thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The render is on the tick thread; the disk is not.</b> Building the
    /// text is bounded work over a bounded table — the cap is what bounds it —
    /// but a write is bounded by the disk, which on a VPS under load is not
    /// bounded by anything. <see cref="AnalyticsWriter"/> is one thread with a
    /// one-slot mailbox, so a slow disk costs a stale file rather than a late
    /// tick.
    /// </para>
    /// <para>
    /// ⚠️ <b>Trimming happens here, not on the join path.</b> A join that
    /// occasionally also evicted a thousand records would put a spike on the one
    /// path where a player is waiting for a reply.
    /// </para>
    /// </remarks>
    public void Flush(DateTime nowUtc)
    {
        if (!Enabled)
            return;

        // ⛔ A store that has not read its file must never write one. The salt
        // is generated by Load and by nothing else, so its absence is exactly
        // "this instance does not know what is already on disk" — and writing
        // then would replace a server's whole history with an empty file.
        if (_salt.Length == 0)
            return;

        Trim();

        _writer.Post(Render());
        _lastFlushUtc = nowUtc;
        _dirty = false;
    }

    /// <summary>
    /// The whole file, as text.
    /// </summary>
    /// <remarks>
    /// This is the tick-thread half of a flush and the only part of it that is
    /// not the disk. Measured at the twenty-thousand-record cap: 2.26 MB of
    /// text in 11 ms, once every five minutes. <c>docs/ANALYTICS.md</c>.
    /// </remarks>
    private string Render()
    {
        var text = new StringBuilder(_players.Count * 96 + 4096);

        text.Append("# gg2-server analytics v").Append(FormatVersion).Append('\n')
            .Append("# tab-separated. Player keys are salted hashes of an address, not addresses.\n")
            .Append("!meta\t").Append(Convert.ToHexStringLower(_salt))
            .Append('\t').Append(Iso(SinceUtc))
            .Append('\t').Append(UniquePlayers)
            .Append('\t').Append(TotalSessions)
            .Append('\t').Append(TotalPlaytimeSeconds)
            .Append('\t').Append(PeakConcurrent)
            .Append('\t').Append(Iso(PeakConcurrentUtc))
            .Append('\n');

        foreach (var day in _days.Values.OrderBy(d => d.Date, StringComparer.Ordinal))
        {
            text.Append("!day\t").Append(day.Date)
                .Append('\t').Append(day.Sessions)
                .Append('\t').Append(day.UniquePlayers)
                .Append('\t').Append(day.NewPlayers)
                .Append('\t').Append(day.PlaytimeSeconds)
                .Append('\t').Append(day.PeakConcurrent)
                .Append('\n');
        }

        foreach (var region in _regions.Values)
        {
            text.Append("!region\t").Append(region.Key)
                .Append('\t').Append(region.CountryCode)
                .Append('\t').Append(Field(region.CountryName))
                .Append('\t').Append(region.RegionCode)
                .Append('\t').Append(Field(region.RegionName))
                .Append('\t').Append(region.Players)
                .Append('\t').Append(region.Sessions)
                .Append('\t').Append(region.PlaytimeSeconds)
                .Append('\n');
        }

        foreach (var player in _players.Values)
        {
            text.Append("!player\t").Append(player.Key)
                .Append('\t').Append(Iso(player.FirstSeenUtc))
                .Append('\t').Append(Iso(player.LastSeenUtc))
                .Append('\t').Append(player.LastDay)
                .Append('\t').Append(player.Sessions)
                .Append('\t').Append(player.PlaytimeSeconds)
                .Append('\t').Append(player.CountryCode)
                .Append('\t').Append(player.RegionCode)
                .Append('\t').Append(player.RegionKey)
                .Append('\t').Append(Field(player.CountryName))
                .Append('\t').Append(Field(player.RegionName))

                // ⚠️ LAST, always. It is the one field a player chooses, so it
                // is the one a reader of this file should never have to count
                // columns past.
                .Append('\t').Append(Field(player.Name))
                .Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// A field that cannot forge the format.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A player name is twenty arbitrary bytes including a tab and a
    /// newline</b> — CLAUDE.md's third headline — so a name written raw here
    /// would add columns, or whole records, to the next read.
    /// <see cref="LogText.Safe"/> escapes both, and escapes rather than strips
    /// so two names that differ only invisibly do not merge.
    /// </remarks>
    private static string Field(string value) => value.Length == 0 ? string.Empty : LogText.Safe(value);

    /// <summary>Evicts the least recently seen players down to the cap.</summary>
    private void Trim()
    {
        if (_players.Count <= _playerCap)
            return;

        // A player currently connected is never evicted: their session holds a
        // reference to the record and would write its playtime into a table the
        // record is no longer in.
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var session in _live)
            live.Add(session.Player.Key);

        var candidates = new List<AnalyticsPlayer>(_players.Values);
        candidates.Sort((a, b) => a.LastSeenUtc.CompareTo(b.LastSeenUtc));

        var over = _players.Count - _playerCap;

        foreach (var player in candidates)
        {
            if (over <= 0)
                break;

            if (live.Contains(player.Key))
                continue;

            _players.Remove(player.Key);
            Trimmed++;
            over--;
        }
    }

    private static DateTime ParseUtc(string value) =>
        value.Length == 0
            ? default
            : DateTime.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// Closes every live session and writes the file one last time.
    /// </summary>
    /// <remarks>
    /// <b>The sessions are ended first.</b> Without that, a clean shutdown
    /// would throw away the playtime of everybody who was still connected — on
    /// a busy server, the majority of the last five minutes.
    /// </remarks>
    public void Dispose()
    {
        if (Enabled)
        {
            var now = DateTime.UtcNow;

            foreach (var session in _live.ToArray())
                End(session, now);

            Flush(now);
        }

        _writer.Dispose();
        Geo?.Dispose();
    }
}

/// <summary>
/// The one thread that touches the analytics file.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real thread, not <c>Task.Run</c>.</b> <c>docs/WEBADMIN.md</c>'s known
/// issue is what that costs: a saturated pool delays the continuation after the
/// first <c>await</c> by seconds, and this is a background writer whose whole
/// job is not to be in anybody's way.
/// </para>
/// <para>
/// <b>A one-slot mailbox, newest wins.</b> If the disk is slower than the flush
/// interval, the right thing to write when it catches up is the current state,
/// not a backlog of five-minute-old ones.
/// </para>
/// </remarks>
internal sealed class AnalyticsWriter : IDisposable
{
    private readonly string _path;
    private readonly IServerLog _log;
    private readonly AutoResetEvent _pending = new(false);
    private readonly Lock _gate = new();
    private Thread? _thread;
    private string? _next;
    private bool _stopping;
    private bool _warned;

    public AnalyticsWriter(string path, IServerLog log)
    {
        _path = path;
        _log = log;
    }

    /// <summary>
    /// Writes a whole file on THIS thread, and waits for it.
    /// </summary>
    /// <remarks>
    /// Only for the one write that has to have happened before the call
    /// returns — see <c>PlayerAnalytics.Load</c>, which runs at startup before
    /// the listener binds. Everything else goes through <see cref="Post"/>,
    /// because everything else runs on the tick thread.
    /// </remarks>
    public void WriteNow(string text)
    {
        if (_path.Length == 0)
            return;

        Write(text);
    }

    /// <summary>Queues a whole file. Returns immediately.</summary>
    public void Post(string text)
    {
        if (_path.Length == 0)
            return;

        lock (_gate)
        {
            _next = text;

            _thread ??= StartThread();
        }

        _pending.Set();
    }

    private Thread StartThread()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "gg2-analytics-writer",
        };

        thread.Start();
        return thread;
    }

    private void Run()
    {
        while (true)
        {
            _pending.WaitOne();

            string? text;
            bool stopping;

            lock (_gate)
            {
                text = _next;
                _next = null;
                stopping = _stopping;
            }

            if (text is not null)
                Write(text);

            if (stopping)
                return;
        }
    }

    /// <summary>
    /// ⚠️ <b>Whole file, temp then move</b> — <c>BanList.Save</c>'s argument.
    /// A half-written analytics file read after a crash would silently report a
    /// truncated history as the real one.
    /// </summary>
    private void Write(string text)
    {
        var temporary = _path + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(temporary, text);
            File.Move(temporary, _path, overwrite: true);
            _warned = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Once per episode. A disk that stays full would otherwise write a
            // line every five minutes for ever, which is the log line that
            // pushes the cause of the problem out of the scrollback.
            if (!_warned)
            {
                _warned = true;
                _log.Warn($"could not write the analytics file {_path}: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        Thread? thread;

        lock (_gate)
        {
            _stopping = true;
            thread = _thread;
        }

        if (thread is null)
        {
            _pending.Dispose();
            return;
        }

        _pending.Set();

        // The last write is the point of the whole class; a shutdown that did
        // not wait for it would lose exactly the flush Dispose asked for.
        thread.Join(TimeSpan.FromSeconds(5));
        _pending.Dispose();
    }
}
