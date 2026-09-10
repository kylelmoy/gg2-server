using System.Net;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The MaxMind DB reader, against MaxMind's own test database.
/// </summary>
/// <remarks>
/// <para>
/// <b>An oracle, not a mirror.</b> The obvious way to test a binary reader is
/// to write an encoder beside it and assert a round trip — and that passes
/// whenever both halves misread the specification the same way, which for a
/// format with three record widths, four pointer widths and a shared middle
/// byte is the likely failure rather than an unlikely one. This file was built
/// by the format's authors.
/// <c>Fixtures/geoip/README.md</c> has its provenance and licence.
/// </para>
/// <para>
/// The addresses below are the ones MaxMind's own client test suites assert on,
/// and every one of them is from a documentation or test range.
/// </para>
/// </remarks>
public sealed class GeoIpDatabaseTests
{
    private static string Fixture =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "geoip", "GeoIP2-City-Test.mmdb");

    private static GeoIpDatabase Open()
    {
        var database = GeoIpDatabase.Open(Fixture, new RecordingLog());

        Assert.NotNull(database);
        return database;
    }

    [Fact]
    public void TheMetadataIsRead()
    {
        using var database = Open();

        Assert.Equal("GeoIP2-City", database.DatabaseType);
        Assert.True(database.HasRegions);

        // Not a specific date — the fixture is refreshed upstream — but a build
        // epoch of 0 or of next century means the field was decoded wrongly,
        // and both are what a mis-sized integer produces.
        Assert.InRange(database.BuiltUtc, new DateTime(2013, 1, 1), DateTime.UtcNow.AddYears(1));
    }

    /// <summary>
    /// A country and a subdivision, which is the whole feature.
    /// </summary>
    /// <remarks>
    /// <c>216.160.83.56</c> is the fixture's Milton, Washington entry — the one
    /// address in it that exercises country, subdivision code and subdivision
    /// name together, which is exactly the shape the US-state view needs.
    /// </remarks>
    [Fact]
    public void AnAmericanAddressResolvesToItsState()
    {
        using var database = Open();

        var location = database.Lookup(IPAddress.Parse("216.160.83.56"));

        Assert.True(location.IsKnown);
        Assert.Equal("US", location.CountryCode);
        Assert.Equal("United States", location.CountryName);
        Assert.Equal("WA", location.RegionCode);
        Assert.Equal("Washington", location.RegionName);
    }

    /// <summary>
    /// A country outside the United States, where the subdivision is not used.
    /// </summary>
    [Theory]
    [InlineData("2.125.160.216", "GB", "United Kingdom")]
    [InlineData("81.2.69.142", "GB", "United Kingdom")]
    [InlineData("89.160.20.112", "SE", "Sweden")]
    [InlineData("175.16.199.0", "CN", "China")]
    public void ACountryResolves(string address, string code, string name)
    {
        using var database = Open();

        var location = database.Lookup(IPAddress.Parse(address));

        Assert.Equal(code, location.CountryCode);
        Assert.Equal(name, location.CountryName);
    }

    /// <summary>
    /// An IPv6 address resolves out of the same tree.
    /// </summary>
    /// <remarks>
    /// The fixture maps <c>::/64</c>-shaped test ranges alongside the IPv4 ones.
    /// This is here because the two families take different paths through
    /// <c>FindRecord</c> — 128 bits walked directly against 32 bits walked
    /// under <c>::ffff:0:0/96</c> — and only one of them is exercised by every
    /// other test in this file.
    /// </remarks>
    [Fact]
    public void AnIpV6AddressResolves()
    {
        using var database = Open();

        var location = database.Lookup(IPAddress.Parse("::81.2.69.142"));

        Assert.Equal("GB", location.CountryCode);
    }

    /// <summary>
    /// ⚠️ <b>A private address resolving to nothing is the COMMON case, not a
    /// failure.</b> A LAN server's whole roster is on 192.168/16, so this is
    /// what the panel renders most of the time on the machine it is developed
    /// on, and it has to be a clean "unknown" rather than a wrong answer.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.40")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    public void APrivateAddressResolvesToNothing(string address)
    {
        using var database = Open();

        var location = database.Lookup(IPAddress.Parse(address));

        Assert.False(location.IsKnown);
        Assert.Equal(string.Empty, location.CountryCode);
    }

    /// <summary>
    /// ⛔ <b>Open never throws.</b> This runs at startup on a path an operator
    /// typed, and a server that will not start because a monthly geo database
    /// was not re-downloaded is a worse outcome than a map with no dots on it.
    /// </summary>
    [Fact]
    public void AMissingFileIsRefusedRatherThanThrown()
    {
        var log = new RecordingLog();

        Assert.Null(GeoIpDatabase.Open(
            Path.Combine(Path.GetTempPath(), "gg2-no-such-database.mmdb"), log));

        Assert.Null(GeoIpDatabase.Open(string.Empty, log));
    }

    /// <summary>A file that is not a MaxMind DB at all is refused by name.</summary>
    [Fact]
    public void AFileThatIsNotADatabaseIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gg2-not-a-db-{Guid.NewGuid():N}.mmdb");
        File.WriteAllBytes(path, new byte[4096]);

        try
        {
            var log = new RecordingLog();

            Assert.Null(GeoIpDatabase.Open(path, log));
            Assert.Contains(log.Lines, line => line.Contains("MaxMind", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The negative control for every assertion above.
    /// </summary>
    /// <remarks>
    /// Without it, a reader that answered <c>Unknown</c> for absolutely
    /// everything would pass the private-address tests and fail nothing else
    /// that says so. This asserts the fixture and the reader between them can
    /// produce more than one distinct country.
    /// </remarks>
    [Fact]
    public void TheReaderCanProduceMoreThanOneAnswer()
    {
        using var database = Open();

        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var address in new[] { "216.160.83.56", "81.2.69.142", "89.160.20.112", "175.16.199.0" })
            codes.Add(database.Lookup(IPAddress.Parse(address)).CountryCode);

        Assert.True(codes.Count >= 3, $"expected several distinct countries, got: {string.Join(',', codes)}");
    }
}
