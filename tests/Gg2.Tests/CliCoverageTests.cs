using System.Reflection;
using Gg2.Host;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Every setting the server has must be reachable from the command line.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate this project needs.</b> A <see cref="ServerOptions"/> property can
/// be fully implemented, wired to real behaviour and gated by its own tests, and
/// still be unreachable from the command line — nothing goes red for that,
/// because every test that touches it builds a <see cref="ServerOptions"/> in
/// C#. The option exists; the way in does not.
/// </para>
/// <para>
/// <b>It asserts the property MOVES, not that the flag parses.</b> A flag that
/// is accepted and then dropped on the floor looks identical from outside to one
/// that works. Comparing the parsed value against the default is the cheap
/// contradicting action; reading the switch statement is not.
/// </para>
/// <para>
/// <b>Adding an option means adding a row here.</b> The reflection half fails
/// on an unlisted property, so the failure names the option rather than leaving
/// a gap. If an option really is not meant to be settable from the command line,
/// put it in <see cref="NotOnTheCommandLine"/> with the reason — a documented
/// omission is a decision, and an undocumented one is this bug again.
/// </para>
/// </remarks>
public sealed class CliCoverageTests
{
    /// <summary>
    /// Settable options that deliberately have no flag, and why.
    /// </summary>
    /// <remarks>
    /// Empty today. It exists so the next person with a good reason has
    /// somewhere to write it down, rather than deleting a row from the table.
    /// </remarks>
    private static readonly Dictionary<string, string> NotOnTheCommandLine = [];

    /// <summary>
    /// One row per option: the flags that set it, and how to read it back.
    /// </summary>
    /// <remarks>
    /// The projection exists because three of these are reference types whose
    /// default <c>Equals</c> is identity — an array, a list and a record — and a
    /// reference comparison would pass for a flag that replaced the value with
    /// an identical one, and fail for one that mutated it in place.
    /// </remarks>
    private static readonly (string Property, string[] Args, Func<ServerOptions, object?> Read)[] Table =
    [
        ("ServerName", ["--name", "a test server"], o => o.ServerName),
        ("ServerPlayerName", ["--server-player", "Referee"], o => o.ServerPlayerName),
        ("Port", ["--port", "8191"], o => o.Port),
        ("Password", ["--password", "hunter2"], o => o.Password),
        ("WelcomeMessage", ["--welcome", "hello"], o => o.WelcomeMessage),
        ("ServerPluginList", ["--plugins", "chat_v2_pr"], o => o.ServerPluginList),
        ("ServerPluginsRequired", ["--plugins-required"], o => o.ServerPluginsRequired),
        ("VotableMaps", ["--votable-maps", "ctf_truefort,koth_valley"], o => string.Join(',', o.VotableMaps)),
        ("PlayerLimit", ["--players", "24"], o => o.PlayerLimit),
        ("MultiClientLimit", ["--multi-client-limit", "1"], o => o.MultiClientLimit),
        ("Map", ["--map", "koth_valley"], o => o.Map),
        ("MapRotation", ["--rotation", "koth_valley,ctf_2dfort"], o => string.Join(',', o.MapRotation)),
        ("CapLimit", ["--cap-limit", "3"], o => o.CapLimit),
        ("TimeLimitMinutes", ["--time-limit", "20"], o => o.TimeLimitMinutes),
        ("RespawnTimeSeconds", ["--respawn-time", "8"], o => o.RespawnTimeSeconds),
        ("KillLimit", ["--kill-limit", "50"], o => o.KillLimit),
        ("TdmInvulnerabilitySeconds", ["--tdm-invuln", "0"], o => o.TdmInvulnerabilitySeconds),
        ("ClassLimits", ["--class-limit", "medic=1"], o => string.Join(',', o.ClassLimits)),
        ("ShuffleRotation", ["--shuffle", "any"], o => o.ShuffleRotation),
        ("Autobalance", ["--no-autobalance"], o => o.Autobalance),
        ("BotEngagesBotsToo", ["--bot-engages-bots"], o => o.BotEngagesBotsToo),
        ("IdleWhenEmpty", ["--no-idle-when-empty"], o => o.IdleWhenEmpty),
        ("AdaptiveBotBudget", ["--fixed-bot-budget"], o => o.AdaptiveBotBudget),
        ("AnnounceToLobby", ["--lobby"], o => o.AnnounceToLobby),
        ("Rewards", ["--rewards"], o => o.Rewards),
        ("AuthServerHost", ["--auth-server", "auth.example:45022"], o => o.AuthServerHost),
        ("AuthServerPort", ["--auth-server", "auth.example:45099"], o => o.AuthServerPort),
        ("MapDownloadBytesPerSecond", ["--map-download-limit", "12345"], o => o.MapDownloadBytesPerSecond),
        ("MapDirectory", ["--maps", "some/other/maps"], o => o.MapDirectory),
        ("NavCacheDirectory", ["--botnav", "some/other/nav"], o => o.NavCacheDirectory),
        ("NavGenerate", ["--no-navgen"], o => o.NavGenerate),
        ("NavGenerateWrite", ["--navgen-save"], o => o.NavGenerateWrite),
        // ⚠️ The NEGATIVE flag is the one that moves, because the tables ship on.
        // --nav-distances is still accepted and is deliberately a no-op, so it
        // cannot be the row here: this test asserts a flag moves its option, and
        // an alias for the default never will.
        ("NavDistances", ["--no-nav-distances"], o => o.NavDistances),
        ("BotOccupancy", ["--no-bot-occupancy"], o => o.BotOccupancy),
        ("SpeedMultiplier", ["--speed", "max"], o => o.SpeedMultiplier),
        ("RecordingDirectory", ["--record", "some/traces"], o => o.RecordingDirectory),
        ("ControlPort", ["--control-port", "9001"], o => o.ControlPort),
        ("ControlPortEnabled", ["--control-port", "0"], o => o.ControlPortEnabled),
        ("TickStats", ["--tick-stats"], o => o.TickStats),
        ("Bots", ["--bots", "8"], o => o.Bots),

        // One row per LIMITS FLAG, for the reason the Web rows below give: a
        // single row would leave four of the five ungated, and a flag that
        // parses and then does nothing is exactly what this class exists to
        // catch. ConnectionLimits is a record, so the comparison is structural.
        //
        // ⚠️ The rest of ConnectionLimits deliberately has no flag:
        // MaxReceiveBytesPerTick, MaxAcceptsPerTick, MaxInvalidCommandBytes and
        // the three keepalive intervals are tuning against the 33 ms tick and
        // the protocol's own shape, not against anything an operator knows about
        // their machine. Nothing gates that decision, because this test's unit
        // is the ServerOptions property and Limits is one property — so it is
        // written here instead.
        ("Limits", ["--max-send-backlog", "2097152"], o => o.Limits),
        ("Limits", ["--max-receive-buffer", "524288"], o => o.Limits.MaxReceiveBufferBytes),
        ("Limits", ["--max-joining", "64"], o => o.Limits.MaxJoiningConnections),
        ("Limits", ["--handshake-deadline", "120"], o => o.Limits.MaxHandshakeDuration),
        ("Limits", ["--no-tcp-keepalive"], o => o.Limits.TcpKeepAlive),

        ("BanFile", ["--ban-file", "bans.txt"], o => o.BanFile),
        ("Limits", ["--connect-rate", "3"], o => o.Limits.ConnectionBurstPerAddress),
        ("Limits", ["--connect-rate-window", "30"], o => o.Limits.ConnectionBurstWindow),
        ("LogFile", ["--log-file", "logs/server.log"], o => o.LogFile),
        ("LogMaxBytes", ["--log-max-bytes", "1048576"], o => o.LogMaxBytes),
        ("LogKeep", ["--log-keep", "9"], o => o.LogKeep),

        // One row per WEB FLAG, not one per property, and they all name the
        // same property. Web is a record so its structural equality makes the
        // comparison real, but a single row would leave four of the five flags
        // ungated — and --web-password silently doing nothing is the failure
        // this whole test class exists for.
        ("Web", ["--web", "8191"], o => o.Web),
        ("Web", ["--web", "0", "--web-bind", "0.0.0.0"], o => o.Web.Bind),
        ("Web", ["--web", "0", "--web-password", "hunter2"], o => o.Web.Password),
        ("Web", ["--web", "0", "--web-tls", "some/cert.pfx"], o => o.Web.CertificatePath),
        ("Web", ["--web", "0", "--web-tls", "some/cert.pfx", "--web-tls-password", "pw"],
            o => o.Web.CertificatePassword),
    ];

    /// <summary>
    /// Every public settable <see cref="ServerOptions"/> property is in the
    /// table, or is named as deliberately absent.
    /// </summary>
    [Fact]
    public void EverySettableOptionIsCoveredByAFlag()
    {
        var covered = Table.Select(row => row.Property)
            .Concat(NotOnTheCommandLine.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var settable = typeof(ServerOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.SetMethod!.IsPublic)
            .Select(property => property.Name)
            .ToArray();

        var missing = settable.Where(name => !covered.Contains(name)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"ServerOptions properties with no command-line flag: {string.Join(", ", missing)}. "
            + "Add a flag and a row to CliCoverageTests.Table, or a reason to "
            + "CliCoverageTests.NotOnTheCommandLine. An option nobody running the binary can "
            + "set is the bug this test exists for.");

        // And the other direction: a row naming a property that no longer
        // exists is a row that stopped testing anything the day it was renamed.
        var stale = covered.Where(name => !settable.Contains(name, StringComparer.Ordinal)).ToArray();

        Assert.True(
            stale.Length == 0,
            $"CliCoverageTests names options ServerOptions does not have: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// Each flag actually moves the option it claims to, off its default.
    /// </summary>
    [Fact]
    public void EveryFlagChangesTheOptionItNames()
    {
        var defaults = CommandLine.Parse([]);

        foreach (var (property, args, read) in Table)
        {
            var parsed = CommandLine.Parse(args);

            Assert.True(
                !Equals(read(defaults), read(parsed)),
                $"{string.Join(' ', args)} left {property} at its default "
                + $"({read(defaults)}). The flag parses and changes nothing.");
        }
    }

    /// <summary>
    /// An out-of-range value is a usage error, not a crash.
    /// </summary>
    /// <remarks>
    /// Parse validates, so an out-of-range value such as <c>--players 100</c>
    /// throws where the usage text is printed, rather than surfacing later as an
    /// unhandled <see cref="ArgumentOutOfRangeException"/> and a stack trace.
    /// </remarks>
    [Theory]
    [InlineData("--players", "100")]   // 1..48
    [InlineData("--players", "0")]
    [InlineData("--port", "99999")]    // 0..65535
    [InlineData("--speed", "0")]       // > 0, or infinity
    [InlineData("--speed", "-1")]
    public void AnOutOfRangeValueThrowsAnArgumentExceptionFromParse(string flag, string value)
    {
        // ArgumentOutOfRangeException derives from ArgumentException, which is
        // what Main's catch filter names — so this IS the assertion that the
        // error reaches the usage text rather than the console's crash handler.
        // The type is the whole point; do not relax it to ThrowsAny<Exception>.
        var thrown = Assert.ThrowsAny<ArgumentException>(() => CommandLine.Parse([flag, value]));

        // It names the OPTION, not the flag — "player limit must be 1..48
        // (Parameter 'PlayerLimit')" for `--players 100`. Worth knowing before
        // somebody "fixes" a test that looks like it should match the flag.
        Assert.NotEmpty(thrown.Message);
    }

    /// <summary>
    /// A <c>--web-*</c> setting with no <c>--web</c> is refused, not ignored.
    /// </summary>
    /// <remarks>
    /// The failure it prevents is <c>--web-password</c> on a server with no
    /// panel: accepted, dropped on the floor, and indistinguishable from working
    /// until somebody needs the password. The same rule <c>--policy</c> without a
    /// mode that consults it follows.
    /// </remarks>
    [Theory]
    [InlineData("--web-bind", "0.0.0.0")]
    [InlineData("--web-password", "hunter2")]
    [InlineData("--web-tls", "cert.pfx")]
    [InlineData("--web-tls-password", "pw")]
    public void AWebSettingWithoutAPanelIsRefused(string flag, string value)
    {
        var thrown = Assert.Throws<ArgumentException>(() => CommandLine.Parse([flag, value]));

        Assert.Contains(flag, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("--web", thrown.Message, StringComparison.Ordinal);

        // And the control: with a panel to configure, the same flag is fine.
        // --web-tls is in the control line for every case, because
        // --web-tls-password on its own is refused by WebAdminOptions.Validate
        // for a different and equally deliberate reason: a certificate password
        // with no certificate to open.
        CommandLine.Parse(["--web", "0", "--web-tls", "cert.pfx", flag, value]);
    }

    /// <summary>
    /// <c>--web</c> alone binds LOOPBACK. Making it remote is a second,
    /// explicit decision.
    /// </summary>
    /// <remarks>
    /// This is the one default in the project whose being wrong would put an
    /// admin console on the internet, so it is asserted rather than left to the
    /// record struct's initialiser.
    /// </remarks>
    [Fact]
    public void TheWebPanelIsNotRemoteUnlessItIsToldToBe()
    {
        var web = CommandLine.Parse(["--web", "8191"]).Web;

        Assert.True(web.Enabled);
        Assert.Equal("127.0.0.1", web.Bind);
        Assert.False(web.IsRemote);

        Assert.True(CommandLine.Parse(["--web", "8191", "--web-bind", "0.0.0.0"]).Web.IsRemote);

        // And off entirely without the flag.
        Assert.False(CommandLine.Parse([]).Web.Enabled);
    }

    /// <summary>A bad flag name is a usage error too, and names itself.</summary>
    [Fact]
    public void AnUnknownFlagNamesItself()
    {
        var thrown = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--no-such-flag"]));
        Assert.Contains("--no-such-flag", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--class-limit</c> takes one number for every class, or named pairs.
    /// </summary>
    [Fact]
    public void ClassLimitTakesOneNumberOrNamedPairs()
    {
        var uniform = CommandLine.Parse(["--class-limit", "2"]).ClassLimits;
        Assert.Equal(10, uniform.Length);
        Assert.All(uniform, limit => Assert.Equal(2, limit));

        var named = CommandLine.Parse(["--class-limit", "medic=1,spy=0"]).ClassLimits;
        Assert.Equal(1, named[(int)PlayerClass.Medic]);
        Assert.Equal(0, named[(int)PlayerClass.Spy]);

        // Everything unnamed stays unlimited rather than being zeroed, which is
        // the difference between "one medic each" and "medics only".
        Assert.Equal(255, named[(int)PlayerClass.Scout]);
        Assert.Equal(255, named[(int)PlayerClass.Heavy]);
    }

    [Theory]
    [InlineData("medic")]              // no '='
    [InlineData("wizard=1")]           // not a class
    [InlineData("medic=nine")]         // not a number
    public void ABadClassLimitNamesWhatIsWrong(string text)
    {
        var thrown = Assert.ThrowsAny<Exception>(() => CommandLine.Parse(["--class-limit", text]));
        Assert.True(thrown is ArgumentException or FormatException, $"unexpected {thrown.GetType()}");
    }

    /// <summary>
    /// A flag beats <c>--config</c> whichever side of it the flag appears on.
    /// </summary>
    /// <remarks>
    /// Handling <c>--config</c> in the switch like every other flag would make
    /// the two orderings two different servers, and the one that silently loses
    /// the flag is the one somebody will write. The pre-pass is what stops that,
    /// and this is the test that would catch it being "simplified" back.
    /// </remarks>
    [Fact]
    public void AFlagBeatsTheConfigFileInEitherOrder()
    {
        var config = WriteConfig(
            """
            [Server]
            ServerName=from the file
            CapLimit=9
            """);

        var before = CommandLine.Parse(["--name", "from the flag", "--config", config]);
        var after = CommandLine.Parse(["--config", config, "--name", "from the flag"]);

        Assert.Equal("from the flag", before.ServerName);
        Assert.Equal("from the flag", after.ServerName);

        // And the key the flag did not touch still comes from the file.
        Assert.Equal(9, before.CapLimit);
        Assert.Equal(9, after.CapLimit);
    }

    /// <summary>
    /// With a rotation and no <c>--map</c>, the rotation picks the opening map.
    /// </summary>
    /// <remarks>
    /// <c>GameServerCreate.gml:110-113</c>. Expressed by emptying
    /// <see cref="ServerOptions.Map"/>, which is <c>global.launchMap == ""</c> —
    /// see <c>GameServer.OpeningMap</c>.
    /// </remarks>
    [Fact]
    public void ARotationWithoutAMapFlagOpensOnTheRotation()
    {
        var rotated = CommandLine.Parse(["--rotation", "koth_valley,ctf_2dfort"]);
        Assert.Equal(string.Empty, rotated.Map);

        var pinned = CommandLine.Parse(["--rotation", "koth_valley,ctf_2dfort", "--map", "ctf_truefort"]);
        Assert.Equal("ctf_truefort", pinned.Map);

        // No rotation at all leaves the default rather than emptying it, or a
        // bare `gg2-server` would have no map to open.
        Assert.Equal(ServerOptions.DefaultMap, CommandLine.Parse([]).Map);
    }

    /// <summary>A missing config file says which file, without the usage dump.</summary>
    [Fact]
    public void AMissingConfigFileIsAnIOException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gg2-no-such-{Guid.NewGuid():N}.ini");
        var thrown = Assert.Throws<FileNotFoundException>(() => CommandLine.Parse(["--config", path]));
        Assert.Contains(path, thrown.Message, StringComparison.Ordinal);
    }

    internal static string WriteConfig(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gg2-test-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, contents);
        return path;
    }
}
