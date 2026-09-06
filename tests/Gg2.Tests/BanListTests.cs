using System.Net;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Address and CIDR matching, which everything else here rests on.
/// </summary>
public sealed class IpRangeTests
{
    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", true)]
    [InlineData("1.2.3.4", "1.2.3.5", false)]
    [InlineData("1.2.3.0/24", "1.2.3.255", true)]
    [InlineData("1.2.3.0/24", "1.2.4.0", false)]
    [InlineData("1.2.0.0/16", "1.2.255.255", true)]
    [InlineData("0.0.0.0/0", "203.0.113.9", true)]
    [InlineData("1.2.3.0/25", "1.2.3.127", true)]
    [InlineData("1.2.3.0/25", "1.2.3.128", false)]
    public void MatchingCoversWholeAndPartialBytes(string range, string address, bool expected)
    {
        Assert.True(IpRange.TryParse(range, out var parsed));
        Assert.Equal(expected, parsed.Contains(IPAddress.Parse(address)));
    }

    /// <summary>
    /// ⚠️ <b>Host bits are masked off, not rejected.</b> An operator typing
    /// <c>1.2.3.4/24</c> means "the /24 that address is in", and refusing it
    /// over a pedantry about host bits is how a ban does not get applied during
    /// the incident it was needed for.
    /// </summary>
    [Fact]
    public void APrefixWithHostBitsSetIsTheBlockThatAddressIsIn()
    {
        Assert.True(IpRange.TryParse("1.2.3.4/24", out var sloppy));
        Assert.True(IpRange.TryParse("1.2.3.0/24", out var tidy));

        Assert.Equal(tidy, sloppy);
        Assert.Equal("1.2.3.0/24", sloppy.ToString());
    }

    /// <summary>The ban file is written with ToString and read with TryParse.</summary>
    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("203.0.113.0/24")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8::/32")]
    public void ItRoundTripsThroughItsOwnText(string text)
    {
        Assert.True(IpRange.TryParse(text, out var range));
        Assert.True(IpRange.TryParse(range.ToString(), out var again));
        Assert.Equal(range, again);
    }

    /// <summary>
    /// Families never cross, so one entry cannot quietly match two things.
    /// </summary>
    [Fact]
    public void AnIpv4RangeDoesNotMatchAnIpv6Address()
    {
        Assert.True(IpRange.TryParse("0.0.0.0/0", out var everything));
        Assert.False(everything.Contains(IPAddress.Parse("2001:db8::1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4/-1")]
    [InlineData("1.2.3.4/abc")]
    [InlineData(null)]
    public void RubbishIsRefusedRatherThanBecomingAnEmptyRangeThatMatchesNothing(string? text)
    {
        Assert.False(IpRange.TryParse(text, out _));
    }
}

/// <summary>
/// The operator's ban list.
/// </summary>
/// <remarks>
/// ⛔ <b>An address is not a person</b>, and <see cref="BanList"/> says so at
/// length. These tests are about the list behaving as an operator expects, not
/// about bans being an effective control.
/// </remarks>
public sealed class BanListTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gg2-bans-" + Guid.NewGuid().ToString("N")[..8]);

    private string File_ => Path.Combine(_directory, "bans.txt");

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IpRange Range(string text)
    {
        Assert.True(IpRange.TryParse(text, out var range));
        return range;
    }

    [Fact]
    public void ABannedAddressIsFoundAndAnUnbannedOneIsNot()
    {
        var list = new BanList(string.Empty, new RecordingLog());
        list.Add(Range("198.51.100.0/24"), null, "griefing", Now);

        Assert.NotNull(list.Find(IPAddress.Parse("198.51.100.7"), Now));
        Assert.Null(list.Find(IPAddress.Parse("198.51.101.7"), Now));
    }

    /// <summary>A timed ban stops applying on its own.</summary>
    [Fact]
    public void ATimedBanLapses()
    {
        var list = new BanList(string.Empty, new RecordingLog());
        list.Add(Range("198.51.100.7"), TimeSpan.FromMinutes(10), "cooling off", Now);

        Assert.NotNull(list.Find(IPAddress.Parse("198.51.100.7"), Now.AddMinutes(9)));
        Assert.Null(list.Find(IPAddress.Parse("198.51.100.7"), Now.AddMinutes(11)));

        Assert.Equal(1, list.Sweep(Now.AddMinutes(11)));
        Assert.Empty(list.Entries);
    }

    /// <summary>
    /// ⚠️ <b>Unbanning is EXACT.</b> Lifting <c>1.2.3.4</c> must not silently
    /// delete a <c>/24</c> somebody put there on purpose — a whole block quietly
    /// readmitted by an operator who thought they were letting one person back
    /// in is the worst possible surprise from this feature.
    /// </summary>
    [Fact]
    public void UnbanningOneAddressDoesNotLiftABlockThatCoversIt()
    {
        var list = new BanList(string.Empty, new RecordingLog());
        list.Add(Range("198.51.100.0/24"), null, "the whole lot", Now);

        Assert.False(list.Remove(Range("198.51.100.7")));
        Assert.NotNull(list.Find(IPAddress.Parse("198.51.100.7"), Now));

        Assert.True(list.Remove(Range("198.51.100.0/24")));
        Assert.Null(list.Find(IPAddress.Parse("198.51.100.7"), Now));
    }

    /// <summary>Re-banning the same range replaces rather than stacking.</summary>
    [Fact]
    public void ASecondBanOnTheSameRangeReplacesTheFirst()
    {
        var list = new BanList(string.Empty, new RecordingLog());

        list.Add(Range("198.51.100.7"), TimeSpan.FromMinutes(5), "first", Now);
        list.Add(Range("198.51.100.7"), null, "second", Now);

        Assert.Single(list.Entries);
        Assert.Equal("second", list.Entries[0].Reason);
        Assert.Null(list.Entries[0].ExpiresUtc);
    }

    /// <summary>The file round-trips, which is the whole point of having one.</summary>
    [Fact]
    public void ItSurvivesARestart()
    {
        var written = new BanList(File_, new RecordingLog());
        written.Add(Range("198.51.100.0/24"), null, "griefing", Now);
        written.Add(Range("203.0.113.9"), TimeSpan.FromHours(2), "spawn camping", Now);

        var read = new BanList(File_, new RecordingLog());
        read.Load();

        Assert.Equal(2, read.Entries.Count);
        Assert.NotNull(read.Find(IPAddress.Parse("198.51.100.7"), Now));
        Assert.NotNull(read.Find(IPAddress.Parse("203.0.113.9"), Now.AddHours(1)));
        Assert.Null(read.Find(IPAddress.Parse("203.0.113.9"), Now.AddHours(3)));

        // The reason survives too: it is the only record of why, and an operator
        // reading the list back a month later has nothing else to go on.
        Assert.Contains(read.Entries, entry => entry.Reason == "griefing");
    }

    /// <summary>
    /// ⚠️ <b>A typo in one line must not throw away the other ninety-nine, and
    /// must not stop the server coming back up.</b> This file is edited by hand.
    /// </summary>
    [Fact]
    public void AMalformedLineIsSkippedAndReportedRatherThanFatal()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllLines(
            File_,
            [
                "# a comment",
                string.Empty,
                "198.51.100.7\tnever\tfine",
                "not-an-address\tnever\tbroken",
                "203.0.113.9\tnot-a-date\tbroken",
                "203.0.113.10\tnever\talso fine",
            ]);

        var log = new RecordingLog();
        var list = new BanList(File_, log);
        list.Load();

        Assert.Equal(2, list.Entries.Count);
        Assert.NotNull(list.Find(IPAddress.Parse("198.51.100.7"), Now));
        Assert.NotNull(list.Find(IPAddress.Parse("203.0.113.10"), Now));

        // Reported, not silent: a ban an operator wrote and that is not in force
        // is exactly the thing they must be told about.
        Assert.Equal(2, log.Warnings.Count);
    }

    /// <summary>A missing file is a server that has never banned anybody.</summary>
    [Fact]
    public void AMissingFileIsNotAnError()
    {
        var log = new RecordingLog();
        var list = new BanList(File_, log);

        list.Load();

        Assert.Empty(list.Entries);
        Assert.Empty(log.Warnings);
    }

    /// <summary>An in-memory list writes nothing and says it is not persistent.</summary>
    [Fact]
    public void WithoutAFileNothingIsWritten()
    {
        var list = new BanList(string.Empty, new RecordingLog());

        Assert.False(list.IsPersistent);
        list.Add(Range("198.51.100.7"), null, "temporary", Now);

        Assert.False(Directory.Exists(_directory));
    }
}

/// <summary>
/// How fast one address may open connections.
/// </summary>
/// <remarks>
/// ⚠️ <b>Not <c>MultiClientLimit</c>.</b> That bounds concurrent connections
/// from one address; this bounds the rate, which is the shape nothing else
/// catches — connect, get refused, reconnect, with the concurrent count never
/// above one.
/// </remarks>
public sealed class ConnectionRateLimiterTests
{
    private static readonly IPAddress One = IPAddress.Parse("198.51.100.7");
    private static readonly IPAddress Two = IPAddress.Parse("198.51.100.8");

    [Fact]
    public void ABurstIsAllowedAndThenTheAddressWaits()
    {
        var limiter = new ConnectionRateLimiter(burst: 3, per: TimeSpan.FromMinutes(1));

        Assert.True(limiter.Allow(One, 0));
        Assert.True(limiter.Allow(One, 0));
        Assert.True(limiter.Allow(One, 0));
        Assert.False(limiter.Allow(One, 0));
    }

    /// <summary>
    /// ⚠️ <b>The control case.</b> Everything above asserts a refusal, which is
    /// the shape that also passes if the limiter refuses everybody. A second
    /// address must be unaffected by the first one's flood — otherwise this is
    /// not a per-address limit, it is an outage.
    /// </summary>
    [Fact]
    public void OneAddressFloodingDoesNotAffectAnother()
    {
        var limiter = new ConnectionRateLimiter(burst: 2, per: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 50; i++)
            limiter.Allow(One, 0);

        Assert.True(limiter.Allow(Two, 0));
        Assert.True(limiter.Allow(Two, 0));
    }

    [Fact]
    public void TheAllowanceRefillsOverTheWindow()
    {
        var limiter = new ConnectionRateLimiter(burst: 2, per: TimeSpan.FromSeconds(10));

        Assert.True(limiter.Allow(One, 0));
        Assert.True(limiter.Allow(One, 0));
        Assert.False(limiter.Allow(One, 0));

        // Half the window is one token.
        Assert.True(limiter.Allow(One, 5_000));
        Assert.False(limiter.Allow(One, 5_000));
    }

    /// <summary>
    /// A refilled bucket carries no information, so it is dropped — which is
    /// what keeps the table small on a healthy server.
    /// </summary>
    [Fact]
    public void QuietAddressesAreForgotten()
    {
        var limiter = new ConnectionRateLimiter(burst: 2, per: TimeSpan.FromSeconds(10));

        limiter.Allow(One, 0);
        limiter.Allow(Two, 0);
        Assert.Equal(2, limiter.Tracked);

        // A window later both have refilled, and the next call sweeps.
        limiter.Allow(IPAddress.Parse("198.51.100.9"), 60_000);

        Assert.Equal(1, limiter.Tracked);
    }

    /// <summary>
    /// ⚠️ <b>A clock that goes backwards must not drain a bucket.</b> The server
    /// passes a monotonic clock, so this is defence in depth — but a limiter
    /// that a clock can turn into a hard block is worse than none.
    /// </summary>
    [Fact]
    public void AClockThatGoesBackwardsDoesNotLockAnAddressOut()
    {
        var limiter = new ConnectionRateLimiter(burst: 2, per: TimeSpan.FromSeconds(10));

        Assert.True(limiter.Allow(One, 100_000));
        Assert.True(limiter.Allow(One, 50_000));
        Assert.False(limiter.Allow(One, 50_000));
    }

    /// <summary>
    /// ⚠️ <b>It fails OPEN when its table is full, and that is deliberate.</b>
    /// Failing closed would let an attacker with enough addresses lock out every
    /// legitimate player by filling the table — turning a rate limiter into the
    /// denial of service. A distributed flood is answered by the accept cap and
    /// the firewall instead; see docs/HARDENING.md.
    /// </summary>
    [Fact]
    public void AFullTableWavesNewAddressesThroughRatherThanRefusingThem()
    {
        var limiter = new ConnectionRateLimiter(burst: 1, per: TimeSpan.FromHours(1));

        // Fill it. Each address spends its only token, so none is sweepable.
        for (var i = 0; i < ConnectionRateLimiter.MaxTracked; i++)
            limiter.Allow(new IPAddress(i + 0x0A000000L), 0);

        Assert.True(limiter.IsSaturated);

        // A new address is allowed rather than refused.
        Assert.True(limiter.Allow(IPAddress.Parse("203.0.113.200"), 0));

        // And the operator is told — once, not per connection.
        Assert.True(limiter.ShouldWarnSaturated());
        Assert.False(limiter.ShouldWarnSaturated());
    }
}
