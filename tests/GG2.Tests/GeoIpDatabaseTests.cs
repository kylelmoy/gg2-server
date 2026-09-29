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
