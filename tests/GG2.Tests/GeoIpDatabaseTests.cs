using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The MaxMind DB reader's refusals.
/// </summary>
/// <remarks>
/// ⚠️ <b>Lookups are not tested here.</b> Testing them needs a real
/// <c>.mmdb</c> as an oracle — an encoder written beside the reader would pass
/// whenever both halves misread the specification the same way — and this repo
/// carries no database. A change to the reader is verified by pointing
/// <c>--geoip</c> at a downloaded one and opening the panel's Analytics view.
/// </remarks>
public sealed class GeoIpDatabaseTests
{
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
}

/// <summary>
/// The name-to-code table for US states, held to the map it feeds.
/// </summary>
/// <remarks>
/// DB-IP's City Lite names a US state without a code, and a state's players are
/// drawn at <c>world.json</c>'s marker keyed by that code. So every marker's
/// name has to resolve to its own key, or that state's players are counted and
/// never drawn.
/// </remarks>
public sealed class GeoIpStateTests
{
    [Fact]
    public void EveryUsMarkerNameResolvesToItsOwnCode()
    {
        using var world = System.Text.Json.JsonDocument.Parse(WebAssets.Read("world.json"));
        var points = world.RootElement.GetProperty("views").GetProperty("us").GetProperty("points");

        var checkedCount = 0;

        foreach (var point in points.EnumerateObject())
        {
            var name = point.Value[2].GetString()!;
            Assert.Equal(point.Name, GeoIpDatabase.UsStateCode(name));
            checkedCount++;
        }

        // The vacuity control: the 50 states and DC.
        Assert.Equal(51, checkedCount);
    }

    [Fact]
    public void ANameThatIsNotAStateResolvesToNothing()
    {
        Assert.Equal(string.Empty, GeoIpDatabase.UsStateCode(string.Empty));
        Assert.Equal(string.Empty, GeoIpDatabase.UsStateCode("Bavaria"));
    }
}
