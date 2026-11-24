using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GG2.Auth;

/// <summary>One player who can authenticate.</summary>
/// <param name="Id">What the player puts in <c>[Haxxy] RewardId</c>. Not a secret.</param>
/// <param name="Key">The secret, the bytes <c>[Haxxy] RewardKey</c> unhexes to.</param>
/// <param name="Rewards">Colon-separated reward names, exactly what is sent back.</param>
/// <param name="Note">Free text for the operator. Never sent anywhere.</param>
internal sealed record User(string Id, byte[] Key, string Rewards, string Note)
{
    public string KeyHex => Convert.ToHexStringLower(Key);
}

/// <summary>
/// The user table: a tab-separated text file, read whole and written whole.
/// </summary>
/// <remarks>
/// <b>A text file rather than a database</b> because the table is small, edited
/// by one operator through the CLI, and read by one process. A database would
/// be a package, a native library under AOT, and a schema to migrate, for a
/// table that fits in a page.
/// <para>
/// <b>The keys are stored as they are, not hashed.</b> HMAC needs the key
/// itself, so there is nothing a hash could be checked against. The file is the
/// secret; protect it the way you would a private key.
/// </para>
/// <para>
/// <b>Writes are atomic</b>: the whole file goes to a temporary next to it and
/// is moved over the original, so a running server reloading it never reads half
/// a file.
/// </para>
/// </remarks>
internal static class UserFile
{
    public const string Header =
        "# gg2-auth users: id<TAB>key (hex)<TAB>rewards (colon-separated)<TAB>note. docs/AUTH.md.";

    /// <summary>The key length the CLI issues. Any length 1-64 loads.</summary>
    public const int KeyBytes = 16;

    public const int MaxKeyBytes = 64;

    public static List<User> Load(string path)
    {
        var users = new List<User>();
        if (!File.Exists(path))
            return users;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            number++;
            if (line.Length == 0 || line[0] == '#')
                continue;

            var fields = line.Split('\t');
            if (fields.Length is < 2 or > 4)
                throw new FormatException($"{path}:{number}: expected 2 to 4 tab-separated fields, found {fields.Length}");

            var user = new User(
                fields[0],
                ParseKey(fields[1], path, number),
                fields.Length > 2 ? fields[2] : string.Empty,
                fields.Length > 3 ? fields[3] : string.Empty);

            if (ValidateId(user.Id) is { } idError)
                throw new FormatException($"{path}:{number}: {idError}");
            if (ValidateRewards(user.Rewards) is { } rewardError)
                throw new FormatException($"{path}:{number}: {rewardError}");
            if (!ids.Add(user.Id))
                throw new FormatException($"{path}:{number}: id {user.Id} appears twice");

            users.Add(user);
        }

        return users;
    }

    public static void Save(string path, IEnumerable<User> users)
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        foreach (var user in users)
            text.Append(user.Id).Append('\t')
                .Append(user.KeyHex).Append('\t')
                .Append(user.Rewards).Append('\t')
                .Append(user.Note).Append('\n');

        var full = Path.GetFullPath(path);
        var temp = full + ".tmp";
        File.Delete(temp);

        // ⚠️ Owner and group only, set at creation: the move replaces the
        // original, so its mode is this file's, and a umask of 022 would make
        // every key world-readable on the first edit. The group is how the
        // service reads a file the operator's CLI wrote (docs/AUTH.md).
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

        using (var writer = new StreamWriter(temp, new UTF8Encoding(false), options))
            writer.Write(text.ToString());

        File.Move(temp, full, overwrite: true);
    }

    /// <summary>
    /// A fresh key, with no zero byte in it.
    /// </summary>
    /// <remarks>
    /// <b>No zero bytes</b> because the key lives in a GM8 string on the client
    /// (<c>game_init.gml:92</c> unhexes it with <c>chr</c>), and a NUL is the one
    /// byte whose survival through GM8's string handling is not worth betting a
    /// player's key on. It costs about a sixteenth of a bit per byte.
    /// </remarks>
    public static byte[] NewKey()
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        for (var i = 0; i < key.Length; i++)
            while (key[i] == 0)
                key[i] = RandomNumberGenerator.GetBytes(1)[0];
        return key;
    }

    /// <summary>The lowest positive integer above every numeric id in use.</summary>
    /// <remarks>
    /// Integers, as ganggarrison.com's own ids are (<c>user.id</c> in
    /// GG2RewardsArea). An id is not a secret: knowing one gets nobody anything
    /// without its key.
    /// </remarks>
    public static string NextId(IEnumerable<User> users)
    {
        long max = 0;
        foreach (var user in users)
            if (long.TryParse(user.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        return (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    public static string? ValidateId(string id)
    {
        if (id.Length is 0 or > AuthRequest.MaxIdBytes)
            return $"an id is 1 to {AuthRequest.MaxIdBytes} characters";
        foreach (var c in id)
            if (c is <= ' ' or > '~')
                return "an id is printable ASCII with no spaces";
        return null;
    }

    /// <summary>
    /// Reward names are what the client looks up with <c>hasReward</c> and
    /// <c>hasClassReward</c>; the list is <c>initRewards.gml</c> and the call
    /// sites. Only the characters are checked here, not the names, so a name the
    /// client does not know is stored and sent and simply does nothing.
    /// </summary>
    public static string? ValidateRewards(string rewards)
    {
        if (rewards.Length == 0)
            return null;
        foreach (var name in rewards.Split(':'))
            if (ValidateReward(name) is { } error)
                return error;
        return null;
    }

    public static string? ValidateReward(string name)
    {
        if (name.Length == 0)
            return "a reward name is empty (two colons in a row, or one at an end)";
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return $"reward name \"{name}\" may only contain letters, digits and _";
        return null;
    }

    public static string CleanNote(string note) =>
        note.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static byte[] ParseKey(string hex, string path, int number)
    {
        byte[] key;
        try
        {
            key = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new FormatException($"{path}:{number}: the key is not hex");
        }

        if (key.Length is 0 or > MaxKeyBytes)
            throw new FormatException($"{path}:{number}: a key is 1 to {MaxKeyBytes} bytes");
        return key;
    }
}

/// <summary>
/// The user table as the running server sees it: the file, reloaded when it changes.
/// </summary>
/// <remarks>
/// <b>Checked on each request rather than watched.</b> A stat per request costs
/// nothing at this traffic and cannot miss an event the way a
/// <see cref="FileSystemWatcher"/> can, so a key issued with the CLI works on the
/// next join with no restart. <b>A file that fails to parse keeps the last good
/// table</b> and logs once per change.
/// </remarks>
internal sealed class UserStore
{
    private readonly string _path;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();
    private Dictionary<string, User> _users = new(StringComparer.Ordinal);
    private (DateTime Written, long Length) _stamp = (DateTime.MinValue, -1);

    public UserStore(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        Refresh();
    }

    public int Count => _users.Count;

    public User? Find(string id)
    {
        Refresh();
        return _users.GetValueOrDefault(id);
    }

    private void Refresh()
    {
        lock (_lock)
        {
            var info = new FileInfo(_path);
            var stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
            if (stamp == _stamp)
                return;
            _stamp = stamp;

            try
            {
                var users = new Dictionary<string, User>(StringComparer.Ordinal);
                foreach (var user in UserFile.Load(_path))
                    users[user.Id] = user;
                _users = users;
                _log($"loaded {users.Count} user(s) from {_path}");
            }
            catch (FormatException e)
            {
                // Not retried until the file changes again: it would fail the same way.
                _log($"kept the previous {_users.Count} user(s): {e.Message}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Possibly mid-replace. Forget the stamp so the next request retries.
                _stamp = (DateTime.MinValue, -2);
                _log($"kept the previous {_users.Count} user(s): {e.Message}");
            }
        }
    }
}
