using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// <see cref="ServerConfigFile"/>: a reference-format <c>gg2.ini</c>.
/// </summary>
/// <remarks>
/// The keys and their clamps come from <c>Scripts/Game/game_init.gml:66-106</c>
/// and <c>Scripts/Classlimits/readClasslimitFromIni.gml</c>; the rotation from
/// <c>Scripts/Game/load_map_rotation.gml</c> and the checkout's own
/// <c>sampleMapRotation.txt</c>.
/// </remarks>
public sealed class ServerConfigFileTests : IDisposable
{
    private readonly List<string> _paths = [];

    private string Write(string name, string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gg2-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        _paths.Add(directory);
        return path;
    }

    private static ServerOptions Load(string path)
    {
        var options = new ServerOptions { MapDirectory = "maps" };
        ServerConfigFile.Apply(options, path);
        return options;
    }

    [Fact]
    public void TheServerSectionReachesEveryOptionItNames()
    {
        var options = Load(Write("gg2.ini",
            """
            [Settings]
            HostingPort=8195
            PlayerLimit=16
            MultiClientLimit=1
            PlayerName=Referee

            [Server]
            ServerName=A Real Server
            WelcomeMessage=be nice
            Password=hunter2
            CapLimit=3
            Time Limit=20
            Deathmatch Kill Limit=50
            Team Deathmatch Invulnerability Seconds=2
            Respawn Time=8
            AutoBalance=0
            ShuffleRotation=2
            Total bandwidth limit for map downloads in bytes per second=12345
            """));

        Assert.Equal(8195, options.Port);
        Assert.Equal(16, options.PlayerLimit);
        Assert.Equal(1, options.MultiClientLimit);
        Assert.Equal("Referee", options.ServerPlayerName);
        Assert.Equal("A Real Server", options.ServerName);
        Assert.Equal("be nice", options.WelcomeMessage);
        Assert.Equal("hunter2", options.Password);
        Assert.Equal(3, options.CapLimit);
        Assert.Equal(20, options.TimeLimitMinutes);
        Assert.Equal(50, options.KillLimit);
        Assert.Equal(2, options.TdmInvulnerabilitySeconds);
        Assert.Equal(8, options.RespawnTimeSeconds);
        Assert.False(options.Autobalance);
        Assert.Equal(ShuffleMode.Any, options.ShuffleRotation);
        Assert.Equal(12345, options.MapDownloadBytesPerSecond);
    }

    /// <summary>
    /// <c>UseLobby</c> is honoured when written and never inherited.
    /// </summary>
    /// <remarks>
    /// The reference's ini defaults it to 1. This port must not publish its
    /// address because a config file it was handed happened to say so — see
    /// <see cref="ServerOptions.AnnounceToLobby"/>. Writing the key is a
    /// decision and is obeyed; leaving it out is not.
    /// </remarks>
    [Fact]
    public void UseLobbyIsObeyedWhenWrittenAndOffWhenAbsent()
    {
        Assert.False(Load(Write("a.ini", "[Server]\nServerName=x")).AnnounceToLobby);
        Assert.True(Load(Write("b.ini", "[Settings]\nUseLobby=1")).AnnounceToLobby);
        Assert.False(Load(Write("c.ini", "[Settings]\nUseLobby=0")).AnnounceToLobby);
    }

    /// <summary>
    /// The reference's clamps, which are not decoration: a 0 becomes a 1.
    /// </summary>
    /// <remarks>
    /// <c>game_init.gml:96, 101, 103</c> wrap each read in
    /// <c>max(1, min(..., ...))</c>, so <c>CapLimit=0</c> in a stock server's ini
    /// is a ONE-capture round and not an endless one. TDM invulnerability is
    /// the exception — its floor is 0 — and getting that one wrong would silently
    /// give every deathmatch a third of a second of spawn protection nobody asked
    /// for.
    /// </remarks>
    [Fact]
    public void LimitsAreClampedTheWayTheReferenceClampsThem()
    {
        var floored = Load(Write("gg2.ini",
            """
            [Server]
            CapLimit=0
            Time Limit=0
            Deathmatch Kill Limit=0
            Team Deathmatch Invulnerability Seconds=0
            """));

        Assert.Equal(1, floored.CapLimit);
        Assert.Equal(1, floored.TimeLimitMinutes);
        Assert.Equal(1, floored.KillLimit);
        Assert.Equal(0, floored.TdmInvulnerabilitySeconds);

        var capped = Load(Write("gg2.ini",
            """
            [Server]
            Time Limit=9999
            Deathmatch Kill Limit=999999
            Team Deathmatch Invulnerability Seconds=99999
            """));

        Assert.Equal(255, capped.TimeLimitMinutes);
        Assert.Equal(65535, capped.KillLimit);
        Assert.Equal(3600, capped.TdmInvulnerabilitySeconds);
    }

    /// <summary>
    /// <c>[Classlimits]</c>, by name, with 255 for anything unmentioned.
    /// </summary>
    /// <remarks>
    /// The ini's key order is not <see cref="PlayerClass"/>'s numeric order —
    /// the reference lists Scout, Pyro, Soldier, Heavy … and the enum is Scout,
    /// Soldier, Sniper, Demoman … so a reader that paired them positionally would
    /// cap the wrong four classes and nothing would say so.
    /// </remarks>
    [Fact]
    public void ClassLimitsArePairedByNameNotByPosition()
    {
        var options = Load(Write("gg2.ini",
            """
            [Classlimits]
            Pyro=1
            Heavy=2
            Quote=0
            """));

        Assert.Equal(1, options.ClassLimits[(int)PlayerClass.Pyro]);
        Assert.Equal(2, options.ClassLimits[(int)PlayerClass.Heavy]);
        Assert.Equal(0, options.ClassLimits[(int)PlayerClass.Quote]);

        // The two the enum would have hit if this were positional.
        Assert.Equal(255, options.ClassLimits[(int)PlayerClass.Soldier]);
        Assert.Equal(255, options.ClassLimits[(int)PlayerClass.Sniper]);
    }

    /// <summary>
    /// An empty <c>[Maps]</c> section still produces the stock twenty-map
    /// rotation, which is the difference between a config file and no config.
    /// </summary>
    /// <remarks>
    /// <c>game_init.gml:205-224</c> seeds every built-in map with its 1-based
    /// position as a default priority, so "no [Maps] section" and "every map at
    /// its default" are the same rotation. That is why a stock dedicated server
    /// rotates and a bare <c>gg2-server</c> does not.
    /// </remarks>
    [Fact]
    public void AnEmptyMapsSectionIsTheStockTwentyMapRotation()
    {
        var options = Load(Write("gg2.ini", "[Server]\nServerName=x"));

        Assert.Equal(20, options.MapRotation.Count);
        Assert.Equal("ctf_truefort", options.MapRotation[0]);
        Assert.Equal("ctf_eiger", options.MapRotation[^1]);

        // Twenty, not twenty-two: ctf_oldfort and gg_debug ship with the port
        // and are NOT in the reference's default rotation.
        Assert.DoesNotContain("ctf_oldfort", options.MapRotation);
        Assert.DoesNotContain("gg_debug", options.MapRotation);
    }

    /// <summary>
    /// <c>[Maps]</c> is a priority queue drained lowest-first, and 0 removes.
    /// </summary>
    [Fact]
    public void MapsAreOrderedByPriorityAndZeroRemovesThem()
    {
        var options = Load(Write("gg2.ini",
            """
            [Maps]
            ctf_eiger=1
            ctf_truefort=2
            ctf_2dfort=0
            arena_montane=0
            arena_lumberyard=0
            """));

        // Line order is irrelevant; the numbers decide.
        Assert.Equal("ctf_eiger", options.MapRotation[0]);
        Assert.Equal("ctf_truefort", options.MapRotation[1]);

        Assert.DoesNotContain("ctf_2dfort", options.MapRotation);
        Assert.DoesNotContain("arena_montane", options.MapRotation);
    }

    /// <summary>
    /// A rotation file beats <c>[Maps]</c>, and repeats are kept in order.
    /// </summary>
    [Fact]
    public void ARotationFileWinsAndKeepsItsOrderAndRepeats()
    {
        var config = Write("gg2.ini", "[Server]\nMapRotation=rotation.txt");

        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(config)!, "rotation.txt"),
            """
            # a comment, ignored
            koth_valley

              ctf_2dfort
            koth_valley
            """);

        var options = Load(config);

        Assert.Equal(["koth_valley", "ctf_2dfort", "koth_valley"], options.MapRotation);
    }

    /// <summary>
    /// A rotation file that does not exist falls back to <c>[Maps]</c>,
    /// silently — because the reference does.
    /// </summary>
    /// <remarks>
    /// <c>load_map_rotation.gml</c> tests <c>file_exists</c> and drops to the
    /// priority list when it fails. Turning that into an error here would make
    /// the same ini behave differently on the two implementations, which is a
    /// worse trap than the quiet fallback: an operator debugging a rotation
    /// would get one answer from the stock server and another from this one.
    /// </remarks>
    [Fact]
    public void AMissingRotationFileFallsBackToTheMapsSection()
    {
        var options = Load(Write("gg2.ini", "[Server]\nMapRotation=no-such-file.txt"));

        Assert.Equal(20, options.MapRotation.Count);
        Assert.Equal("ctf_truefort", options.MapRotation[0]);
    }

    /// <summary>
    /// Sections and keys are case-insensitive; a value is data and is not.
    /// </summary>
    [Fact]
    public void SectionsAndKeysAreCaseInsensitiveAndValuesAreNot()
    {
        var options = Load(Write("gg2.ini",
            """
            [server]
            servername="Mixed Case Name"
            caplimit=4
            """));

        Assert.Equal("Mixed Case Name", options.ServerName);
        Assert.Equal(4, options.CapLimit);
    }

    /// <summary>
    /// A key that must be a number and is not stops the server.
    /// </summary>
    /// <remarks>
    /// The alternative — ignoring it and keeping the default — is a server
    /// quietly running a configuration nobody wrote, which is precisely the
    /// class of failure this repo keeps finding. GM8's <c>ini_read_real</c> does
    /// return 0 for garbage; that is a behaviour worth diverging from, because a
    /// dedicated server has an operator to tell and a game does not.
    /// </remarks>
    [Fact]
    public void ANonNumericNumberIsRefusedAndNamesTheKey()
    {
        var thrown = Assert.Throws<FormatException>(
            () => Load(Write("gg2.ini", "[Server]\nCapLimit=lots")));

        Assert.Contains("CapLimit", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("lots", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown key and an unknown section are ignored, not refused.
    /// </summary>
    /// <remarks>
    /// A real <c>gg2.ini</c> off a hosted server is mostly client preferences —
    /// crosshairs, gib level, HUD positions — plus <c>[Haxxy]</c> and the plugin
    /// keys, none of which this server implements. Refusing them would mean the
    /// one file an operator already has is the one file that does not work.
    /// </remarks>
    [Fact]
    public void ClientPreferencesAndUnknownSectionsAreIgnored()
    {
        var options = Load(Write("gg2.ini",
            """
            [Settings]
            Gib Level=3
            CrosshairFilename=crosshair.png
            Kill Cam=1
            PlayerLimit=12

            [Haxxy]
            RewardKey=deadbeef

            [SomePlugin]
            Whatever=1
            """));

        Assert.Equal(12, options.PlayerLimit);
    }

    /// <summary>
    /// The <c>[Server]</c> keys the port does not implement are ignored rather
    /// than half-honoured.
    /// </summary>
    /// <remarks>
    /// <c>docs/PROTOCOL.md</c> lists rewards and UPnP as deliberately absent.
    /// <c>ServerPluginList</c> and related keys are covered separately, by
    /// <see cref="PluginConfigTests"/>, which asserts the opposite for those two
    /// keys.
    /// </remarks>
    [Fact]
    public void UnimplementedServerKeysAreIgnored()
    {
        var options = Load(Write("gg2.ini",
            """
            [Server]
            ServerName=x
            Attempt UPnP Forwarding=1
            """));

        Assert.Equal("x", options.ServerName);
    }

    public void Dispose()
    {
        foreach (var directory in _paths)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that will not delete is not a test failure.
            }
        }
    }
}
