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
/// ⛔ <b>The gate this project did not have.</b> Seven <see cref="ServerOptions"/>
/// properties — the deathmatch kill limit, TDM invulnerability, class limits,
/// autobalance, the map rotation, the multi-client limit and the map-download
/// bandwidth — were fully implemented, wired to real behaviour and gated by
/// their own tests, and could not be set by anybody running the binary. Nothing
/// went red, because every test that touched them built a
/// <see cref="ServerOptions"/> in C#. The option existed; the way in did not.
/// </para>
/// <para>
/// ⚠️ <b>It asserts the property MOVES, not that the flag parses.</b> A flag
/// that is accepted and then dropped on the floor — which is what
/// <c>--search-threads</c> used to be when it came before <c>--search-bots</c>, the
/// flag <c>--bot-mode</c> replaced —
/// looks identical from outside to one that works. Comparing the parsed value
/// against the default is the cheap contradicting action; reading the switch
/// statement is not.
/// </para>
/// <para>
/// ⚠️ <b>Adding an option means adding a row here.</b> The reflection half fails
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
        ("SpeedMultiplier", ["--speed", "max"], o => o.SpeedMultiplier),
        ("RecordingDirectory", ["--record", "some/traces"], o => o.RecordingDirectory),
        ("ControlPort", ["--control-port", "9001"], o => o.ControlPort),
        ("ControlPortEnabled", ["--control-port", "0"], o => o.ControlPortEnabled),
        ("TickStats", ["--tick-stats"], o => o.TickStats),
        ("Bots", ["--bots", "8"], o => o.Bots),
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

        // ⚠️ And the other direction: a row naming a property that no longer
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
        var defaults = Program.Parse([]);

        foreach (var (property, args, read) in Table)
        {
            var parsed = Program.Parse(args);

            Assert.True(
                !Equals(read(defaults), read(parsed)),
                $"{string.Join(' ', args)} left {property} at its default "
                + $"({read(defaults)}). The flag parses and changes nothing.");
        }
    }

    /// <summary>
    /// ⛔ An out-of-range value is a usage error, not a crash.
    /// </summary>
    /// <remarks>
    /// <c>ServerOptions.Validate</c> ran in the <c>GameServer</c> constructor,
    /// which <c>Main</c> called <em>after</em> its parse guard and outside the
    /// one around <c>Start</c> — so <c>--players 100</c> printed an unhandled
    /// <see cref="ArgumentOutOfRangeException"/> and a stack trace instead of the
    /// message <c>Validate</c> had written for exactly this moment. Parse now
    /// validates, so the throw lands where the usage text is printed.
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
        // ⚠️ The type is the whole point; do not relax it to ThrowsAny<Exception>.
        var thrown = Assert.ThrowsAny<ArgumentException>(() => Program.Parse([flag, value]));

        // ⚠️ It names the OPTION, not the flag — "player limit must be 1..48
        // (Parameter 'PlayerLimit')" for `--players 100`. Worth knowing before
        // somebody "fixes" a test that looks like it should match the flag.
        Assert.NotEmpty(thrown.Message);
    }

    /// <summary>A bad flag name is a usage error too, and names itself.</summary>
    [Fact]
    public void AnUnknownFlagNamesItself()
    {
        var thrown = Assert.Throws<ArgumentException>(() => Program.Parse(["--no-such-flag"]));
        Assert.Contains("--no-such-flag", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--class-limit</c> takes one number for every class, or named pairs.
    /// </summary>
    [Fact]
    public void ClassLimitTakesOneNumberOrNamedPairs()
    {
        var uniform = Program.Parse(["--class-limit", "2"]).ClassLimits;
        Assert.Equal(10, uniform.Length);
        Assert.All(uniform, limit => Assert.Equal(2, limit));

        var named = Program.Parse(["--class-limit", "medic=1,spy=0"]).ClassLimits;
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
        var thrown = Assert.ThrowsAny<Exception>(() => Program.Parse(["--class-limit", text]));
        Assert.True(thrown is ArgumentException or FormatException, $"unexpected {thrown.GetType()}");
    }

    /// <summary>
    /// ⚠️ A flag beats <c>--config</c> whichever side of it the flag appears on.
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

        var before = Program.Parse(["--name", "from the flag", "--config", config]);
        var after = Program.Parse(["--config", config, "--name", "from the flag"]);

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
        var rotated = Program.Parse(["--rotation", "koth_valley,ctf_2dfort"]);
        Assert.Equal(string.Empty, rotated.Map);

        var pinned = Program.Parse(["--rotation", "koth_valley,ctf_2dfort", "--map", "ctf_truefort"]);
        Assert.Equal("ctf_truefort", pinned.Map);

        // No rotation at all leaves the default rather than emptying it, or a
        // bare `gg2-server` would have no map to open.
        Assert.Equal(ServerOptions.DefaultMap, Program.Parse([]).Map);
    }

    /// <summary>A missing config file says which file, without the usage dump.</summary>
    [Fact]
    public void AMissingConfigFileIsAnIOException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gg2-no-such-{Guid.NewGuid():N}.ini");
        var thrown = Assert.Throws<FileNotFoundException>(() => Program.Parse(["--config", path]));
        Assert.Contains(path, thrown.Message, StringComparison.Ordinal);
    }

    internal static string WriteConfig(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gg2-test-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, contents);
        return path;
    }
}
