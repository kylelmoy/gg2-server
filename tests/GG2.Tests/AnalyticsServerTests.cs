using System.Text.Json;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The analytics store as a running server actually drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <see cref="PlayerAnalyticsTests"/>.</b> Those
/// drive <c>PlayerAnalytics</c> directly, so every one of them would still pass
/// if <c>GameServer</c> never called it at all — the exact shape of failure
/// CLAUDE.md names: a described capability that no test can contradict because
/// every test builds the thing itself. These tests connect a real client to a
/// real server over a real socket and then ask what was counted.
/// </para>
/// <para>
/// ⚠️ <b>A bot must never appear in here.</b> A bot has no connection, so
/// nothing calls <c>Begin</c> for it — and a server that filled itself with
/// twelve bots and reported thirteen players would make every figure on the
/// panel meaningless without changing any of them visibly.
/// </para>
/// </remarks>
public sealed class AnalyticsServerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"gg2-analytics-server-{Guid.NewGuid():N}");

    public AnalyticsServerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Path_ => System.IO.Path.Combine(_directory, "analytics.tsv");

    private ServerHarness Harness(Action<ServerOptions>? extra = null) =>
        new(options =>
        {
            options.AnalyticsFile = Path_;
            extra?.Invoke(options);
        });

    /// <summary>
    /// A real join is a session, and a real disconnect ends it.
    /// </summary>
    /// <remarks>
    /// The disconnect half is the one that could rot quietly: a session that
    /// was begun and never ended still shows on the panel as a live player, so
    /// a server would report a growing population and no playtime at all.
    /// </remarks>
    [Fact]
    public void AJoinAndADisconnectAreCounted()
    {
        using var harness = Harness();

        Assert.True(harness.Server.Analytics.Enabled);

        var client = harness.JoinClient("alice");

        Assert.Equal(1, harness.Server.Analytics.UniquePlayers);
        Assert.Equal(1, harness.Server.Analytics.TotalSessions);

        client.Dispose();
        harness.TickUntil(() => Live(harness.Server.Analytics) == 0, "the disconnect to be noticed");

        // Still one session and one player: a departure closes a session, it
        // does not retract it.
        Assert.Equal(1, harness.Server.Analytics.TotalSessions);
        Assert.Equal(1, harness.Server.Analytics.UniquePlayers);
    }

    /// <summary>
    /// ⛔ <b>Bots are not players.</b> They have no connection, so they are
    /// never counted — and this is the assertion that would go red if a future
    /// change started counting them.
    /// </summary>
    [Fact]
    public void BotsAreNeverCounted()
    {
        using var harness = Harness(options =>
        {
            options.Bots = options.Bots with { Enabled = true, FillToPlayers = 6, MinHumans = 0 };
        });

        harness.TickUntil(() => harness.Server.BotSlots.Count > 0, "a bot to be added");
        harness.Tick(30);

        Assert.NotEmpty(harness.Server.BotSlots);
        Assert.Equal(0, harness.Server.Analytics.UniquePlayers);
        Assert.Equal(0, harness.Server.Analytics.TotalSessions);

        // The negative control: a human on the same server IS counted, so the
        // zero above is "bots are excluded" rather than "nothing is counted".
        harness.JoinClient("a person");

        Assert.Equal(1, harness.Server.Analytics.UniquePlayers);
    }

    /// <summary>
    /// <b>Everything survives the server, not just the store.</b> The unit test
    /// beside this one reloads a store; this one stops a whole
    /// <c>GameServer</c> and starts another over the same file, which is what a
    /// restart actually is.
    /// </summary>
    [Fact]
    public void TheHistorySurvivesAServerRestart()
    {
        using (var first = Harness())
        {
            first.JoinClient("alice");
            first.Tick(60);
        }

        using var second = Harness();

        Assert.Equal(1, second.Server.Analytics.UniquePlayers);
        Assert.Equal(1, second.Server.Analytics.TotalSessions);

        // ⚠️ NOT asserted here: that the interrupted session's playtime was
        // counted. This harness ticks without advancing a wall clock, so a
        // session that spanned the whole test lasted under a second and rounds
        // to zero whether or not Dispose closed it — an assertion that could
        // not fail. PlayerAnalyticsTests.DisposeCountsTimeStillBeingPlayed
        // owns that claim, where the start time can be set.
        second.JoinClient("alice again");

        Assert.Equal(2, second.Server.Analytics.TotalSessions);

        // Same machine, same loopback address: the salt came back with the file,
        // so this is the same player rather than a second one.
        Assert.Equal(1, second.Server.Analytics.UniquePlayers);
    }

    /// <summary>
    /// ⛔ <b>With no <c>--analytics</c>, no file appears anywhere.</b> The
    /// opt-in is the feature's whole privacy argument, and "off" has to mean
    /// nothing is written rather than nothing is shown.
    /// </summary>
    [Fact]
    public void WithoutTheFlagNothingIsWritten()
    {
        using (var harness = new ServerHarness())
        {
            Assert.False(harness.Server.Analytics.Enabled);
            harness.JoinClient("alice");
            harness.Tick(30);
        }

        Assert.Empty(Directory.GetFiles(_directory));
    }

    /// <summary>
    /// The control port's <c>analytics</c> verb answers with what the panel
    /// draws.
    /// </summary>
    /// <remarks>
    /// Reachability is gated by <c>WebAdminTests</c>'s verb theory; this is the
    /// content. It is the same call <c>/api/analytics</c> makes, so a panel
    /// that renders nothing and a server that reports nothing are told apart.
    /// </remarks>
    [Fact]
    public void TheAnalyticsVerbReportsTheJoin()
    {
        using var harness = Harness();

        harness.JoinClient("alice");
        harness.Tick(5);

        var reply = JsonDocument.Parse(ControlCommands.Dispatch(harness.Server, """{"cmd":"analytics"}""")).RootElement;

        Assert.True(reply.GetProperty("ok").GetBoolean());

        var analytics = reply.GetProperty("analytics");

        Assert.True(analytics.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, analytics.GetProperty("totals").GetProperty("uniquePlayers").GetInt64());
        Assert.Equal(1, analytics.GetProperty("totals").GetProperty("live").GetInt32());
        Assert.Equal("alice", analytics.GetProperty("top")[0].GetProperty("name").GetString());

        // One day's row, and it is today's.
        var day = Assert.Single(analytics.GetProperty("days").EnumerateArray());

        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), day[0].GetString());
    }

    private static int Live(PlayerAnalytics analytics) =>
        JsonDocument.Parse("{" + analytics.ToJson(DateTime.UtcNow) + "}")
            .RootElement.GetProperty("totals").GetProperty("live").GetInt32();
}
