using System.Globalization;
using System.Net;

namespace Gg2.Server;

/// <summary>One banned address or block.</summary>
/// <param name="Range">Who it covers.</param>
/// <param name="ExpiresUtc">When it lapses, or null for "until removed".</param>
/// <param name="Reason">Free text, shown to nobody but the operator.</param>
public sealed record BanEntry(IpRange Range, DateTime? ExpiresUtc, string Reason)
{
    public bool HasExpired(DateTime nowUtc) => ExpiresUtc is { } expiry && expiry <= nowUtc;
}

/// <summary>
/// Addresses the server refuses at accept time.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>READ THIS BEFORE RELYING ON IT: an address is not a person.</b> The
/// protocol has no account and no durable identity — a name is twenty bytes the
/// client chooses, and nothing else on the wire distinguishes one player from
/// another. So the only thing a ban can key on is the address, and that is
/// wrong in both directions: it catches everybody behind a shared address or a
/// carrier NAT, and it catches nobody whose address changes between one
/// connection and the next, which on a mobile network is most people. It is a
/// moderation tool for the case where somebody is being a nuisance from one
/// place, and it is not a security control.
/// </para>
/// <para>
/// <b>Which is why nothing adds to this automatically.</b> Every entry comes
/// from an operator — the control port, the panel, or the file — and the rate
/// limiter beside it (<see cref="ConnectionRateLimiter"/>) is what answers a
/// machine hammering the port, because that lapses on its own and this does
/// not. An automatic ban is how a shared address gets a whole school locked out
/// of a server nobody is even attacking.
/// </para>
/// <para>
/// <b>Checked on the tick thread only.</b> The control port is serviced inline
/// and the panel marshals its commands onto the tick thread, so there is no
/// lock here — see <c>WebAdminServer</c>'s threading rule.
/// </para>
/// </remarks>
public sealed class BanList
{
    private readonly List<BanEntry> _entries = [];
    private readonly string _path;
    private readonly IServerLog _log;

    /// <param name="path">
    /// Where to load from and save to, or empty for memory only.
    /// </param>
    /// <param name="log">Told about a malformed line rather than skipping it silently.</param>
    public BanList(string path, IServerLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _path = path ?? string.Empty;
        _log = log;
    }

    /// <summary>Whether bans survive a restart.</summary>
    public bool IsPersistent => _path.Length != 0;

    /// <summary>Every ban, expired ones included until they are swept.</summary>
    public IReadOnlyList<BanEntry> Entries => _entries;

    /// <summary>
    /// The ban covering <paramref name="address"/>, or null.
    /// </summary>
    /// <remarks>
    /// A linear walk, deliberately. This runs once per accept against a list an
    /// operator typed by hand — tens of entries, not thousands — and a
    /// prefix-tree would be a data structure nobody could debug protecting a
    /// cost nobody can measure. If a list ever gets big enough to matter, the
    /// answer is a firewall rule, not a better index here.
    /// </remarks>
    public BanEntry? Find(IPAddress address, DateTime nowUtc)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];

            if (!entry.HasExpired(nowUtc) && entry.Range.Contains(address))
                return entry;
        }

        return null;
    }

    /// <summary>
    /// Adds or replaces a ban. Returns the entry that is now in force.
    /// </summary>
    /// <remarks>
    /// Replaces rather than stacks: two bans on the same range are a list an
    /// operator cannot reason about, and the newest instruction is the one they
    /// meant.
    /// </remarks>
    public BanEntry Add(IpRange range, TimeSpan? duration, string reason, DateTime nowUtc)
    {
        var entry = new BanEntry(
            range,
            duration is { } window ? nowUtc + window : null,
            string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim());

        _entries.RemoveAll(existing => existing.Range == range);
        _entries.Add(entry);

        Save();
        return entry;
    }

    /// <summary>Removes the ban on exactly this range. False if there was none.</summary>
    /// <remarks>
    /// ⚠️ <b>Exact range, not "whatever covers this address".</b> Unbanning
    /// <c>1.2.3.4</c> must not silently delete a <c>1.2.3.0/24</c> that an
    /// operator put there on purpose — the surprise would be a whole block
    /// quietly readmitted by somebody who thought they were letting one person
    /// back in.
    /// </remarks>
    public bool Remove(IpRange range)
    {
        if (_entries.RemoveAll(entry => entry.Range == range) == 0)
            return false;

        Save();
        return true;
    }

    /// <summary>Drops lapsed entries. Returns how many went.</summary>
    public int Sweep(DateTime nowUtc)
    {
        var removed = _entries.RemoveAll(entry => entry.HasExpired(nowUtc));

        if (removed > 0)
            Save();

        return removed;
    }

    /// <summary>
    /// Reads the ban file, if there is one. Never throws.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A missing file is not an error</b> — it is what a server that has
    /// never banned anybody looks like, and refusing to start over it would make
    /// <c>--ban-file</c> a flag you cannot turn on until you have created a file
    /// by hand.
    /// <para>
    /// ⛔ <b>A malformed line is reported and skipped, not fatal.</b> This file
    /// is edited by hand; a typo in one line must not throw away the other
    /// ninety-nine, and it must not stop the server coming back up.
    /// </para>
    /// </remarks>
    public void Load()
    {
        if (!IsPersistent || !File.Exists(_path))
            return;

        string[] lines;

        try
        {
            lines = File.ReadAllLines(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"could not read the ban file {_path}: {e.Message}");
            return;
        }

        _entries.Clear();
        var bad = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split('\t');

            if (parts.Length < 2 || !IpRange.TryParse(parts[0], out var range))
            {
                bad++;
                _log.Warn($"{_path}:{i + 1}: not a ban entry, skipped");
                continue;
            }

            DateTime? expires = null;

            if (!parts[1].Equals("never", StringComparison.OrdinalIgnoreCase))
            {
                if (!DateTime.TryParse(
                        parts[1],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var parsed))
                {
                    bad++;
                    _log.Warn($"{_path}:{i + 1}: \"{parts[1]}\" is not a date or \"never\", skipped");
                    continue;
                }

                expires = parsed;
            }

            _entries.Add(new BanEntry(range, expires, parts.Length > 2 ? parts[2] : string.Empty));
        }

        _log.Info(
            $"loaded {_entries.Count} ban(s) from {_path}"
            + (bad == 0 ? string.Empty : $"; {bad} line(s) skipped"));
    }

    /// <summary>
    /// Writes the file, if there is one. Never throws.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Written whole, through a temporary file and a move.</b> A ban list
    /// half-written because the disk filled is worse than one that did not
    /// change: on the next start it would silently readmit whoever was on the
    /// lost half.
    /// <para>
    /// This does file I/O on the tick thread, which is allowed here — a ban is
    /// an operator action a few times a day, and the list is tens of lines. It
    /// would not be allowed on a per-connection path.
    /// </para>
    /// </remarks>
    private void Save()
    {
        if (!IsPersistent)
            return;

        var temporary = _path + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(temporary, append: false))
            {
                writer.WriteLine(
                    "# gg2-server bans, tab-separated: address-or-CIDR, expiry (UTC, or the word never), reason");

                foreach (var entry in _entries)
                {
                    var expiry = entry.ExpiresUtc is { } when
                        ? when.ToString("o", CultureInfo.InvariantCulture)
                        : "never";

                    // Tabs are the separator, so a reason containing one would
                    // read back as a fourth field. Spaces instead: the reason is
                    // free text for a human and nothing parses it.
                    writer.WriteLine($"{entry.Range}\t{expiry}\t{entry.Reason.Replace('\t', ' ')}");
                }
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"could not write the ban file {_path}: {e.Message}");
        }
    }
}
