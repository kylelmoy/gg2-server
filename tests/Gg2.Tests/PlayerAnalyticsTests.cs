using System.Net;
using System.Text.Json;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The persistent player analytics — <c>gg2-server --analytics &lt;path&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claims worth gating are the ones a reader would ACT on.</b> Three of
/// them: that the numbers survive a restart (or the feature is a session
/// counter with a file beside it), that the file does not contain the addresses
/// it was built from (or the privacy claim in <c>docs/ANALYTICS.md</c> is
/// false), and that a store which has not read its file cannot write one (or a
/// mis-ordered startup silently replaces a year of history with an empty file).
/// </para>
/// <para>
/// Every test disposes the store before reading the file: the disk is a
/// background thread's, and <c>Dispose</c> is what joins it.
/// </para>
/// </remarks>
public sealed class PlayerAnalyticsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"gg2-analytics-{Guid.NewGuid():N}");

    private string Path_ => System.IO.Path.Combine(_directory, "analytics.tsv");

    private static readonly DateTime Noon = new(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);

    public PlayerAnalyticsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A background writer that has not quite let go is not a test
            // failure; the temp directory is the operating system's problem.
        }
    }

    private PlayerAnalytics New(GeoIpDatabase? geo = null, int cap = PlayerAnalytics.DefaultPlayerCap)
    {
        var store = new PlayerAnalytics(Path_, new RecordingLog(), geo, cap);
        store.Load();
        return store;
    }

    private static IPAddress Address(int last) => IPAddress.Parse($"198.51.100.{last}");

    private static JsonElement Read(PlayerAnalytics store, DateTime now) =>
        JsonDocument.Parse("{" + store.ToJson(now) + "}").RootElement;

    /* ---------- the off state -------------------------------------------- */

    /// <summary>
    /// ⛔ <b>With no path, nothing is counted and nothing is written.</b> That
    /// is the whole opt-in, and a store that quietly counted in memory would
    /// make the panel show numbers an operator never asked for.
    /// </summary>
    [Fact]
    public void WithNoFileNothingIsCounted()
    {
        using var store = new PlayerAnalytics(string.Empty, new RecordingLog(), geo: null);
        store.Load();

        Assert.False(store.Enabled);
        Assert.Null(store.Begin(Address(1), "someone", Noon, 1));
        Assert.Equal(0, store.UniquePlayers);

        var json = Read(store, Noon);

        Assert.False(json.GetProperty("enabled").GetBoolean());

        // The reply's one job in this state is to name the setting that turns
        // it on, so the page does not have to carry the flag's spelling.
        Assert.Contains("--analytics", json.GetProperty("hint").GetString()!, StringComparison.Ordinal);
    }

    /* ---------- counting -------------------------------------------------- */

    [Fact]
    public void AConnectionIsASessionAndItsDurationIsPlaytime()
    {
        using var store = New();

        var session = store.Begin(Address(1), "alice", Noon, 1);
        Assert.NotNull(session);

        store.End(session, Noon.AddMinutes(25));

        Assert.Equal(1, store.UniquePlayers);
        Assert.Equal(1, store.TotalSessions);
        Assert.Equal(25 * 60, store.TotalPlaytimeSeconds);
    }

    /// <summary>
    /// ⛔ <b>Identity is the ADDRESS.</b> Two connections from one address are
    /// one player and two from different addresses are two, whatever names they
    /// gave. Both halves matter: the first is what makes "unique players" mean
    /// anything, and the second is what stops one busy player looking like a
    /// community.
    /// </summary>
    [Fact]
    public void OneAddressIsOnePlayerAndTwoAddressesAreTwo()
    {
        using var store = New();

        store.End(store.Begin(Address(1), "alice", Noon, 1), Noon.AddMinutes(5));
        store.End(store.Begin(Address(1), "alice on another day", Noon.AddHours(1), 1), Noon.AddHours(2));

        Assert.Equal(1, store.UniquePlayers);
        Assert.Equal(2, store.TotalSessions);

        store.End(store.Begin(Address(2), "alice", Noon.AddHours(3), 1), Noon.AddHours(4));

        Assert.Equal(2, store.UniquePlayers);
    }

    /// <summary>
    /// A day's unique count is players, not joins.
    /// </summary>
    /// <remarks>
    /// This is what <c>AnalyticsPlayer.LastDay</c> exists for, and the failure
    /// it prevents — every reconnect counted as another unique player — is one
    /// that inflates the headline number rather than breaking anything visibly.
    /// </remarks>
    [Fact]
    public void ASecondJoinOnTheSameDayIsNotASecondUniquePlayer()
    {
        using var store = New();

        store.End(store.Begin(Address(1), "alice", Noon, 1), Noon.AddMinutes(5));
        store.End(store.Begin(Address(1), "alice", Noon.AddHours(2), 1), Noon.AddHours(3));
        store.End(store.Begin(Address(2), "bob", Noon.AddHours(4), 1), Noon.AddHours(5));

        var today = Assert.Single(store.Days, day => day.Date == "2026-03-04");

        Assert.Equal(2, today.UniquePlayers);
        Assert.Equal(2, today.NewPlayers);
        Assert.Equal(3, today.Sessions);
    }

    /// <summary>The next day starts a fresh unique count and no fresh new count.</summary>
    [Fact]
    public void ANewDayCountsAReturningPlayerAsUniqueButNotAsNew()
    {
        using var store = New();

        store.End(store.Begin(Address(1), "alice", Noon, 1), Noon.AddMinutes(5));
        store.End(store.Begin(Address(1), "alice", Noon.AddDays(1), 1), Noon.AddDays(1).AddMinutes(5));

        var second = Assert.Single(store.Days, day => day.Date == "2026-03-05");

        Assert.Equal(1, second.UniquePlayers);
        Assert.Equal(0, second.NewPlayers);
        Assert.Equal(1, store.UniquePlayers);
    }

    [Fact]
    public void PeakConcurrencyIsTheHighestEverSeen()
    {
        using var store = New();

        store.Begin(Address(1), "a", Noon, 1);
        store.Begin(Address(2), "b", Noon, 2);
        var third = store.Begin(Address(3), "c", Noon, 3);
        store.End(third, Noon.AddMinutes(1));
        store.Begin(Address(4), "d", Noon.AddMinutes(2), 3);

        Assert.Equal(3, store.PeakConcurrent);
    }

    /// <summary>
    /// ⚠️ <b>Time still being played counts.</b> Without it a busy evening
    /// reports the same total for hours and then jumps when the server empties,
    /// which reads as a broken counter rather than as a design decision.
    /// </summary>
    [Fact]
    public void TimeStillBeingPlayedIsInTheTotal()
    {
        using var store = New();

        store.Begin(Address(1), "alice", Noon, 1);

        var json = Read(store, Noon.AddMinutes(30));

        Assert.Equal(30 * 60, json.GetProperty("totals").GetProperty("playtimeSeconds").GetInt64());
        Assert.Equal(1, json.GetProperty("totals").GetProperty("live").GetInt32());

        // And the player is on the board while still connected, flagged as live.
        var top = json.GetProperty("top")[0];

        Assert.Equal("alice", top.GetProperty("name").GetString());
        Assert.True(top.GetProperty("live").GetBoolean());
    }

    /* ---------- across a restart ------------------------------------------ */

    /// <summary>
    /// <b>The claim the whole feature rests on.</b> Everything survives a
    /// restart — including the salt, without which the same address would be a
    /// different player after every restart and "unique players all time" would
    /// count reboots.
    /// </summary>
    [Fact]
    public void EverythingSurvivesARestart()
    {
        string key;

        using (var first = New())
        {
            key = first.KeyFor(Address(1));
            first.End(first.Begin(Address(1), "alice", Noon, 1), Noon.AddMinutes(40));
            first.End(first.Begin(Address(2), "bob", Noon.AddHours(1), 2), Noon.AddHours(1).AddMinutes(20));
        }

        using var second = New();

        Assert.Equal(2, second.UniquePlayers);
        Assert.Equal(2, second.TotalSessions);
        Assert.Equal(60 * 60, second.TotalPlaytimeSeconds);
        Assert.Equal(2, second.PeakConcurrent);

        // The salt came back, so the same address is the same player.
        Assert.Equal(key, second.KeyFor(Address(1)));

        // And a return visit on the same day is still not a second unique.
        second.End(second.Begin(Address(1), "alice", Noon.AddHours(3), 1), Noon.AddHours(4));

        Assert.Equal(2, second.UniquePlayers);

        // The day's row came back too, so the returning join did not re-count
        // anybody as unique-today or as new.
        var day = Assert.Single(second.Days, d => d.Date == "2026-03-04");

        Assert.Equal(2, day.UniquePlayers);
        Assert.Equal(2, day.NewPlayers);
        Assert.Equal(3, day.Sessions);
    }

    /// <summary>
    /// ⚠️ <b>A new file exists as soon as it is started</b>, not five minutes
    /// later.
    /// </summary>
    /// <remarks>
    /// The salt only lives in this file and is what makes an address the same
    /// player across restarts, so a process killed inside its first flush
    /// interval would otherwise generate a different one next time and restart
    /// every player's identity with nothing said. It also means a log line
    /// claiming a file was started can be checked against a file.
    /// </remarks>
    [Fact]
    public void ANewFileIsWrittenAsSoonAsItIsStarted()
    {
        string key;

        using (var store = New())
        {
            key = store.KeyFor(Address(1));
            Assert.True(File.Exists(Path_), "no file after Load created one");
        }

        // The salt in it is the one that instance was using.
        using var second = New();

        Assert.Equal(key, second.KeyFor(Address(1)));
    }

    /// <summary>
    /// ⛔ <b>Disposing counts the time of everybody still connected.</b>
    /// </summary>
    /// <remarks>
    /// Without it a clean shutdown throws away the playtime of the whole
    /// roster, which on a busy server is most of the last five minutes on every
    /// restart — and it would look like nothing at all, because the number it
    /// loses is one nobody has a second source for.
    /// <para>
    /// The session is begun half an hour in the past so <c>Dispose</c>, which
    /// reads the real clock, produces a duration big enough to tell from zero.
    /// </para>
    /// </remarks>
    [Fact]
    public void DisposeCountsTimeStillBeingPlayed()
    {
        using (var store = New())
        {
            store.Begin(Address(1), "alice", DateTime.UtcNow.AddMinutes(-30), 1);

            // ⛔ No End. This is a server being stopped with somebody on it.
        }

        using var reloaded = New();

        Assert.InRange(reloaded.TotalPlaytimeSeconds, 29 * 60, 31 * 60);
    }

    /// <summary>
    /// ⛔ <b>The file does not contain the addresses it was built from.</b>
    /// This is the claim <c>docs/ANALYTICS.md</c> makes to an operator deciding
    /// whether to turn the feature on, and it is the one claim in this file
    /// that nothing else would catch going wrong.
    /// </summary>
    [Fact]
    public void TheFileHoldsNoAddresses()
    {
        using (var store = New())
        {
            store.End(store.Begin(IPAddress.Parse("203.0.113.77"), "alice", Noon, 1), Noon.AddMinutes(5));
            store.End(store.Begin(IPAddress.Parse("2001:db8::dead:beef"), "bob", Noon, 2), Noon.AddMinutes(6));
        }

        var text = File.ReadAllText(Path_);

        Assert.DoesNotContain("203.0.113.77", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8", text, StringComparison.OrdinalIgnoreCase);

        // The negative control: the file is not empty and does hold the names,
        // so "no addresses" is a real absence rather than a missing write.
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.Contains("bob", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ <b>A player name is twenty arbitrary bytes including a tab and a
    /// newline</b>, and this file is tab-separated with one record per line. A
    /// name written raw would add columns — or whole records — to the next read.
    /// </summary>
    [Fact]
    public void ANameCannotForgeTheFileFormat()
    {
        using (var store = New())
        {
            store.End(store.Begin(Address(1), "evil\tname\n!meta\tforged", Noon, 1), Noon.AddMinutes(5));
        }

        var lines = File.ReadAllLines(Path_);

        Assert.Single(lines, line => line.StartsWith("!player", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("!meta", StringComparison.Ordinal));

        using var reloaded = New();

        // One player, one session — the forged records did not become records.
        Assert.Equal(1, reloaded.UniquePlayers);
        Assert.Equal(1, reloaded.TotalSessions);

        var name = Read(reloaded, Noon).GetProperty("top")[0].GetProperty("name").GetString()!;

        // Escaped rather than stripped, so two names that differ only invisibly
        // do not merge into one player on the board.
        Assert.Contains("\\x09", name, StringComparison.Ordinal);
        Assert.DoesNotContain('\t', name);
        Assert.DoesNotContain('\n', name);
    }

    /// <summary>
    /// ⛔ <b>A store that has not read its file must never write one.</b>
    /// Without this guard, constructing a store and disposing it — which is
    /// exactly what <c>GameServer</c> does with its pre-<c>Start</c> placeholder
    /// — replaces a year of history with an empty file, and nothing anywhere
    /// says so.
    /// </summary>
    [Fact]
    public void AStoreThatNeverReadTheFileDoesNotWriteOne()
    {
        using (var store = New())
            store.End(store.Begin(Address(1), "alice", Noon, 1), Noon.AddMinutes(5));

        var before = File.ReadAllText(Path_);

        // Constructed over the same path, never loaded, disposed.
        using (var reckless = new PlayerAnalytics(Path_, new RecordingLog(), geo: null))
        {
            reckless.Flush(Noon);
        }

        Assert.Equal(before, File.ReadAllText(Path_));
    }

    /* ---------- the bound -------------------------------------------------- */

    /// <summary>
    /// The player table is capped, and the all-time count is not.
    /// </summary>
    /// <remarks>
    /// A public server sees more addresses than it can keep. What must not
    /// happen is the headline number falling when the table wraps — a total
    /// that goes DOWN is the one number an operator would report as a bug.
    /// </remarks>
    [Fact]
    public void TheTableIsCappedAndTheAllTimeTotalIsNot()
    {
        using var store = New(cap: 4);

        for (var i = 1; i <= 10; i++)
        {
            var at = Noon.AddMinutes(i);
            store.End(store.Begin(Address(i), $"player{i}", at, 1), at.AddSeconds(30));
        }

        store.Flush(Noon.AddHours(1));

        Assert.Equal(10, store.UniquePlayers);
        Assert.Equal(4, store.TrackedPlayers);
        Assert.Equal(6, store.Trimmed);

        // Least recently seen goes first, so the newest four are what is left.
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var player in Read(store, Noon.AddHours(1)).GetProperty("top").EnumerateArray())
            names.Add(player.GetProperty("name").GetString()!);

        Assert.Contains("player10", names);
        Assert.DoesNotContain("player1", names);
    }

    /// <summary>A player still connected is never evicted out from under their session.</summary>
    [Fact]
    public void ALivePlayerIsNotTrimmed()
    {
        using var store = New(cap: 2);

        // Oldest, and still connected.
        var live = store.Begin(Address(1), "still here", Noon, 1);

        for (var i = 2; i <= 8; i++)
        {
            var at = Noon.AddMinutes(i);
            store.End(store.Begin(Address(i), $"player{i}", at, 2), at.AddSeconds(10));
        }

        store.Flush(Noon.AddHours(1));
        store.End(live, Noon.AddHours(1));

        // The record survived the trim, so its playtime landed somewhere.
        Assert.Equal(60 * 60, store.TotalPlaytimeSeconds - 70);
    }

    /* ---------- geography --------------------------------------------------- */

    /// <summary>
    /// An address the database cannot place is counted, not dropped.
    /// </summary>
    /// <remarks>
    /// A LAN server's whole roster is unresolvable, so "unknown" has to be a
    /// place on the chart rather than a hole in the totals — otherwise the
    /// geography and the player count disagree and neither can be trusted.
    /// </remarks>
    [Fact]
    public void AnAddressWithNoGeographyIsStillCounted()
    {
        using var store = New();

        store.End(store.Begin(IPAddress.Parse("192.168.1.40"), "on the lan", Noon, 1), Noon.AddMinutes(10));

        Assert.Equal(1, store.UniquePlayers);

        var unknown = Assert.Single(store.Regions, r => r.Key == "??");

        Assert.Equal(1, unknown.Players);
        Assert.Equal(600, unknown.PlaytimeSeconds);
    }

    /* ---------- the payload -------------------------------------------------- */

    /// <summary>
    /// ⚠️ <b>Every row must match its declared field list.</b> Days and regions
    /// go over the wire as arrays of bare values against a <c>fields</c> list,
    /// and a column added to one and not the other silently shifts every value
    /// after it — the same trap <c>PerfHistory</c> has, gated the same way.
    /// </summary>
    [Fact]
    public void EveryRowMatchesItsDeclaredFields()
    {
        using var store = New();

        store.End(store.Begin(IPAddress.Parse("216.160.83.56"), "american", Noon, 1), Noon.AddMinutes(10));

        var json = Read(store, Noon.AddMinutes(11));

        Check(json, "dayFields", "days");
        Check(json, "regionFields", "regions");

        static void Check(JsonElement json, string fieldsName, string rowsName)
        {
            var width = json.GetProperty(fieldsName).GetArrayLength();

            Assert.True(width > 0, $"{fieldsName} is empty");

            var rows = json.GetProperty(rowsName);

            Assert.True(rows.GetArrayLength() > 0, $"{rowsName} is empty, so this gate is vacuous");

            foreach (var row in rows.EnumerateArray())
                Assert.Equal(width, row.GetArrayLength());
        }
    }

    /// <summary>
    /// The panel is told when there is no geo database.
    /// </summary>
    /// <remarks>
    /// ⚠️ An empty map with a country-level database loaded and an empty map
    /// with no database at all look identical, and only one of them is fixed by
    /// downloading a file. The loaded case needs a real <c>.mmdb</c>, which this
    /// repo does not carry, so it is verified by opening the panel.
    /// </remarks>
    [Fact]
    public void ThePayloadSaysWhenThereIsNoGeoDatabase()
    {
        using var store = New();

        Assert.Equal(JsonValueKind.Null, Read(store, Noon).GetProperty("geoip").ValueKind);
    }
}
