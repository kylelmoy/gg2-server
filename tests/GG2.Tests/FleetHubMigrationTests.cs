using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A fleet hub started on a directory written before config files were JSON
/// converts it itself: <see cref="FleetHub.MigrateLegacyFiles"/>.
/// </summary>
public sealed class FleetHubMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg2-hub-migrate-" + Guid.NewGuid().ToString("N"));

    public FleetHubMigrationTests() => Directory.CreateDirectory(Path.Combine(_dir, FleetConfig.HostsDirectoryName));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly string Pairing = WebIdentity.PairingCode(
        "0123456789abcdef0123456789abcdef0123456789ab",
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");

    [Fact]
    public void AnIniFleetDirectoryKeepsItsMembersAndSettings()
    {
        File.WriteAllText(Path.Combine(_dir, "members.ini"), $"[gg2-us-east]\nUrl=https://100.64.0.2:8191/\nPairing={Pairing}\n");
        File.WriteAllText(Path.Combine(_dir, "common.ini"), "[Server]\nServerName=Fleet\n[Bots]\nMaxBots=4\n");
        File.WriteAllText(Path.Combine(_dir, FleetConfig.HostsDirectoryName, "gg2-us-east.ini"), "[Server]\nServerName=East\n");

        var lines = FleetHub.MigrateLegacyFiles(_dir);
        Assert.Equal(3, lines.Count(line => line.StartsWith("converted", StringComparison.Ordinal)));

        using var hub = new FleetHub(_dir);
        var member = Assert.Single(hub.Members);
        Assert.Equal("gg2-us-east", member.Name);
        Assert.Equal("https://100.64.0.2:8191/", member.Url.ToString());

        Assert.Contains("East", hub.Render("gg2-us-east"), StringComparison.Ordinal);

        // The ini files stay, so a rollback still finds them.
        Assert.True(File.Exists(Path.Combine(_dir, "members.ini")));

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, FleetHub.MembersFile)));
    }

    [Fact]
    public void AnExistingJsonFileIsNeverOverwritten()
    {
        File.WriteAllText(Path.Combine(_dir, "common.ini"), "[Server]\nServerName=Old\n");
        File.WriteAllText(Path.Combine(_dir, "common.json"), """{"Server": {"ServerName": "New"}}""");

        Assert.Empty(FleetHub.MigrateLegacyFiles(_dir));
        Assert.Contains("New", File.ReadAllText(Path.Combine(_dir, "common.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void ABadIniIsReportedAndTheRestStillConverts()
    {
        File.WriteAllText(Path.Combine(_dir, "common.ini"), "[Bots]\nMaxBots=lots\n");
        File.WriteAllText(Path.Combine(_dir, "members.ini"), $"[a]\nUrl=https://127.0.0.1:1/\nPairing={Pairing}\n");

        var lines = FleetHub.MigrateLegacyFiles(_dir);

        Assert.Contains(lines, line => line.StartsWith("could not convert", StringComparison.Ordinal) && line.Contains("common.ini", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_dir, FleetHub.MembersFile)));
        Assert.False(File.Exists(Path.Combine(_dir, "common.json")));
    }
}
