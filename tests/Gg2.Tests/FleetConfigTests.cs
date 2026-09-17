using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// One configuration for several servers: <see cref="FleetConfig"/>'s rendering,
/// <see cref="FleetConfigEditor"/>'s saves, <see cref="PushedConfigWatcher"/>'s
/// decision to apply a push, and <see cref="ConfigEditorServer"/>'s guards.
/// </summary>
public sealed class FleetConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gg2-fleet-{Guid.NewGuid():N}");

    public FleetConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ConfigDir => Path.Combine(_root, "config");

    private string HostsFile => Path.Combine(_root, "hosts");

    private void Common(string text)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(FleetConfig.CommonPath(ConfigDir), text);
    }

    private void Overlay(string host, string text)
    {
        var path = FleetConfig.OverlayPath(ConfigDir, host);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Hosts(params string[] hosts) =>
        File.WriteAllText(HostsFile, "# a comment\n\n" + string.Join("\n", hosts.Select(h => h + "   # trailing")) + "\n");

    private ServerOptions Rendered(string host) =>
        FleetConfig.Load(FleetConfig.Render(ConfigDir, host), ConfigDir);

    // --- rendering -------------------------------------------------------------

    [Fact]
    public void TheOverlayWinsAndEverythingElseComesFromCommon()
    {
        Common(
            """
            [Settings]
            PlayerLimit=20

            [Server]
            ServerName=Shared Name
            WelcomeMessage=shared message
            Rotation=koth_valley,cp_dirtbowl

            [Bots]
            FillToPlayers=11
            """);

        Overlay("kyle@10.0.0.2",
            """
            [Server]
            ServerName=US West
            WelcomeMessage=hello from the west
            """);

        var west = Rendered("kyle@10.0.0.2");

        Assert.Equal("US West", west.ServerName);
        Assert.Equal("hello from the west", west.WelcomeMessage);

        // Non-default values, so a render that dropped common.ini would not pass
        // by landing on the defaults.
        Assert.Equal(20, west.PlayerLimit);
        Assert.Equal(11, west.Bots.FillToPlayers);
        Assert.Equal(["koth_valley", "cp_dirtbowl"], west.MapRotation);
    }

    /// <summary>
    /// ⚠️ The reason a render is a concatenation and not two <c>Apply</c> calls:
    /// applying an overlay with no <c>Map</c> key to options that have a rotation
    /// empties <c>Map</c>, so the host would open on the rotation instead of the
    /// map <c>common.ini</c> names.
    /// </summary>
    [Fact]
    public void AnOverlayThatDoesNotNameTheMapKeepsCommonsMap()
    {
        Common(
            """
            [Server]
            Map=ctf_eiger
            Rotation=koth_valley,cp_dirtbowl
            """);

        Overlay("barbara", "[Server]\nServerName=Barbara\n");

        Assert.Equal("ctf_eiger", Rendered("barbara").Map);

        // The control arm: applying the two files one after the other really
        // does lose it, so the assertion above is testing something.
        var sequential = ServerOptions.CreateDefault();
        ServerConfigFile.Apply(sequential, FleetConfig.CommonPath(ConfigDir));
        ServerConfigFile.Apply(sequential, FleetConfig.OverlayPath(ConfigDir, "barbara"));
        Assert.Equal(string.Empty, sequential.Map);
    }

    [Fact]
    public void AHostWithNoOverlayGetsCommonAndIsWarnedAbout()
    {
        Common("[Server]\nServerName=Same\n");

        Assert.Equal("Same", Rendered("a").ServerName);

        var warnings = FleetConfig.Warnings(ConfigDir, ["a", "b"]);
        Assert.Contains(warnings, w => w.Contains("a has no", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("both named \"Same\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host whose rendered configuration asks for something it cannot deliver
    /// is warned about BEFORE the push.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The server says the same thing at startup, and that is too late.</b>
    /// It lands in a log on a machine nobody is watching at the moment the
    /// configuration arrives; this is the render an operator is looking at.
    /// <c>ConfigWarnings</c> owns the wording, so the three surfaces cannot drift.
    /// </remarks>
    [Fact]
    public void AHostThatSetsAChatSettingWithNoChatPluginIsWarnedAboutBeforeThePush()
    {
        Common("[Server]\nServerName=Solo\nChatJoinMessage=type /bots\n");
        Overlay("a", "[Bots]\nSuspendMinutes=15\n");

        var warnings = FleetConfig.Warnings(ConfigDir, ["a"]);

        Assert.Contains(warnings, w => w.Contains("a: ChatJoinMessage", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("a: Bots.SuspendMinutes", StringComparison.Ordinal));

        // And the half that keeps it from being a line nobody can clear: adding
        // the plugin silences both, so it is the configuration being described
        // and not the feature existing.
        Common("[Server]\nServerName=Solo\nChatJoinMessage=type /bots\n"
            + "ServerPluginList=chat_v2_pr\n");

        Assert.DoesNotContain(
            FleetConfig.Warnings(ConfigDir, ["a"]),
            w => w.Contains("no chat plugin", StringComparison.Ordinal));
    }

    [Fact]
    public void ARotationFileIsRefusedBecauseOnlyTheConfigIsPushed()
    {
        Common("[Server]\nMapRotation=rotation.txt\n");

        var refusal = Assert.Throws<FormatException>(() => FleetConfig.Render(ConfigDir, "h"));
        Assert.Contains("[Server] Rotation", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueTheServerWouldRefuseIsRefusedAtRenderNamingTheHost()
    {
        Common("[Settings]\nPlayerLimit=16\n");
        Overlay("us-east", "[Settings]\nPlayerLimit=99\n");

        var refusal = Assert.Throws<FormatException>(() => FleetConfig.Render(ConfigDir, "us-east"));
        Assert.StartsWith("us-east:", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenderedBytesAreLfUtf8WithNoBom()
    {
        Common("[Server]\r\nServerName=Crlf\r\n");

        var bytes = FleetConfig.Encode(FleetConfig.Render(ConfigDir, "h"));

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }

    [Fact]
    public void TheHashIsWhatSha256sumPrints()
    {
        // The FIPS 180-2 test vector for "abc".
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            FleetConfig.Hash("abc"u8));
    }

    [Fact]
    public void TheHostsFileIsReadTheWayDeployShReadsIt()
    {
        File.WriteAllText(HostsFile, "# header\nbarbara   # a box\n\n  kyle@1.2.3.4\t# tab\n# kyle@gone\n");

        Assert.Equal(["barbara", "kyle@1.2.3.4"], FleetConfig.ReadHosts(HostsFile));
    }

    /// <summary>
    /// The overlay's file name is computed twice — here and in
    /// <c>scripts/deploy.sh</c>'s <c>overlay_name</c> — and the two must agree
    /// or a push reads a file the editor never wrote.
    /// </summary>
    [Fact]
    public void TheOverlayNameMatchesDeployShsSedExpression()
    {
        // The rule both sides implement, as a literal. This half runs
        // everywhere, so the invariant is gated on any machine — including one
        // that has never seen the deploy script.
        const string CharacterClass = "[^A-Za-z0-9._@-]";

        var rule = new Regex(CharacterClass);
        string[] destinations = ["barbara", "kyle@203.0.113.7", "host:2222", "[::1]", "a b", "Ω"];

        foreach (var destination in destinations)
            Assert.Equal(rule.Replace(destination, "_") + ".ini", FleetConfig.OverlayFileName(destination));

        // ⚠️ `/scripts/` is GITIGNORED — the operator's fleet tooling, not the
        // project's — so the script is absent from a fresh clone and from any
        // copy taken through git. Reading it unguarded turned "this machine has
        // never run a deploy" into a red suite with a DirectoryNotFoundException
        // that said nothing about the cross-check it was doing. Where the script
        // IS present the drift check still runs, which is the machine where a
        // drift could be introduced.
        var path = Path.Combine(RepoRoot(), "scripts", "deploy.sh");

        if (!File.Exists(path))
            return;

        var match = Regex.Match(
            File.ReadAllText(path),
            @"overlay_name\(\) \{ printf '%s' ""\$1"" \| sed 's/(\[\^[^\]]+\])/_/g'");

        Assert.True(match.Success, "deploy.sh no longer defines overlay_name the way this test reads it");

        // The script's own class, against the literal above: the two drifting
        // apart is the failure this test exists for.
        Assert.Equal(CharacterClass, match.Groups[1].Value);
    }

    // --- the editor ------------------------------------------------------------

    [Fact]
    public void ASaveThatWouldBreakOneHostWritesNothingAtAll()
    {
        Common("[Settings]\nPlayerLimit=16\n");
        Hosts("a", "b");
        var before = File.ReadAllText(FleetConfig.CommonPath(ConfigDir));

        var reply = new FleetConfigEditor(ConfigDir, HostsFile).Save(
            """
            {"common": {"Settings.PlayerLimit": "20"},
             "hosts": {"b": {"set": {"Settings.PlayerLimit": "99"}}}}
            """);

        using var json = JsonDocument.Parse(reply);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(before, File.ReadAllText(FleetConfig.CommonPath(ConfigDir)));
        Assert.False(File.Exists(FleetConfig.OverlayPath(ConfigDir, "b")));
    }

    [Fact]
    public void AnOverrideIsWrittenToTheHostAndClearingItRemovesTheKey()
    {
        Common("[Server]\nServerName=Shared\n");
        Hosts("a", "b");
        var editor = new FleetConfigEditor(ConfigDir, HostsFile);

        Assert.Contains("\"ok\":true", editor.Save(
            """{"hosts": {"a": {"set": {"Server.ServerName": "A's server", "Server.WelcomeMessage": "hi"}}}}"""),
            StringComparison.Ordinal);

        Assert.Equal("A's server", Rendered("a").ServerName);
        Assert.Equal("hi", Rendered("a").WelcomeMessage);
        Assert.Equal("Shared", Rendered("b").ServerName);

        Assert.Contains("\"ok\":true", editor.Save(
            """{"hosts": {"a": {"clear": ["Server.WelcomeMessage"]}}}"""), StringComparison.Ordinal);

        Assert.Equal("A's server", Rendered("a").ServerName);
        Assert.Equal(string.Empty, Rendered("a").WelcomeMessage);
        Assert.DoesNotContain("WelcomeMessage", File.ReadAllText(FleetConfig.OverlayPath(ConfigDir, "a")), StringComparison.Ordinal);
    }

    [Fact]
    public void ACommonSaveReachesEveryHostThatDoesNotOverrideIt()
    {
        Common("[Settings]\nPlayerLimit=16\n");
        Overlay("b", "[Settings]\nPlayerLimit=10\n");
        Hosts("a", "b");

        new FleetConfigEditor(ConfigDir, HostsFile).Save("""{"common": {"Settings.PlayerLimit": "24"}}""");

        Assert.Equal(24, Rendered("a").PlayerLimit);
        Assert.Equal(10, Rendered("b").PlayerLimit);
    }

    [Fact]
    public void TheModelNeverCarriesASecretAndAnEmptySecretLeavesItAlone()
    {
        Common("[Web]\nPassword=correct-horse\n[Server]\nPassword=battery-staple\n");
        Hosts("a");
        var editor = new FleetConfigEditor(ConfigDir, HostsFile);

        var model = editor.Model();
        Assert.DoesNotContain("correct-horse", model, StringComparison.Ordinal);
        Assert.DoesNotContain("battery-staple", model, StringComparison.Ordinal);

        editor.Save("""{"common": {"Web.Password": "", "Server.ServerName": "x"}}""");
        Assert.Equal("correct-horse", Rendered("a").Web.Password);

        var preview = editor.Preview("a");
        Assert.DoesNotContain("correct-horse", preview, StringComparison.Ordinal);
        Assert.Contains("Password=********", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskingLeavesEverythingButSecretsAlone()
    {
        const string text = "[Server]\nServerName=Password=looks-like-one\nPassword=\n[Web]\nPassword=\"quoted\"\nPort=8080";

        Assert.Equal(
            "[Server]\nServerName=Password=looks-like-one\nPassword=\n[Web]\nPassword=********\nPort=8080",
            FleetConfigEditor.MaskSecrets(text));
    }

    // --- applying a push -----------------------------------------------------

    private string ServerConfig(string text)
    {
        var path = Path.Combine(_root, "gg2.ini");
        File.WriteAllText(path, text);
        return path;
    }

    private static void Push(string path, string text)
    {
        File.WriteAllText(path, text);
        File.WriteAllText(path + FleetConfig.PushedSuffix, FleetConfig.Hash(File.ReadAllBytes(path)) + "\n");
    }

    private static readonly long Later = (long)PushedConfigWatcher.CheckInterval.TotalMilliseconds + 1;

    [Fact]
    public void APushIsAppliedOnlyOnceNobodyIsConnected()
    {
        var path = ServerConfig("[Server]\nServerName=Before\n");
        var log = new RecordingLog();
        var watcher = new PushedConfigWatcher(path, log, 0);

        Push(path, "[Server]\nServerName=After\n");

        Assert.False(watcher.ShouldRestart(Later, anybodyConnected: true));
        Assert.True(log.Contains("waiting"));
        Assert.True(watcher.Describe().WaitingToApply);

        // Not before the interval, even once empty.
        Assert.False(watcher.ShouldRestart(Later + 1, anybodyConnected: false));
        Assert.True(watcher.ShouldRestart(Later * 2 + 1, anybodyConnected: false));
    }

    [Fact]
    public void AServerStartedWithoutAMarkerNeverRestartsItself()
    {
        var path = ServerConfig("[Server]\nServerName=Before\n");
        var watcher = new PushedConfigWatcher(path, new RecordingLog(), 0);

        File.WriteAllText(path, "[Server]\nServerName=Saved from the panel\n");

        Assert.False(watcher.ShouldRestart(Later, anybodyConnected: false));
        Assert.False(File.Exists(path + FleetConfig.AppliedSuffix));
        Assert.False(watcher.Describe().Managed);
    }

    [Fact]
    public void AFileEditedOnTheHostAfterThePushIsNotApplied()
    {
        var path = ServerConfig("[Server]\nServerName=Before\n");
        var watcher = new PushedConfigWatcher(path, new RecordingLog(), 0);

        Push(path, "[Server]\nServerName=Pushed\n");
        File.WriteAllText(path, "[Server]\nServerName=Pushed, then edited in the panel\n");

        Assert.False(watcher.ShouldRestart(Later, anybodyConnected: false));
        Assert.True(watcher.Describe().EditedSincePush);
    }

    [Fact]
    public void APushTheServerCouldNotStartOnIsNotApplied()
    {
        var path = ServerConfig("[Server]\nServerName=Before\n");
        var log = new RecordingLog();
        var watcher = new PushedConfigWatcher(path, log, 0);

        Push(path, "[Settings]\nPlayerLimit=not a number\n");

        Assert.False(watcher.ShouldRestart(Later, anybodyConnected: false));
        Assert.Single(log.Warnings);
    }

    [Fact]
    public void AServerStartedOnAPushedFileRecordsItAsAppliedAndDoesNotRestartForIt()
    {
        var path = ServerConfig(string.Empty);
        Push(path, "[Server]\nServerName=Pushed\n");

        var watcher = new PushedConfigWatcher(path, new RecordingLog(), 0);

        Assert.Equal(watcher.StartedFrom, File.ReadAllText(path + FleetConfig.AppliedSuffix).Trim());
        Assert.False(watcher.ShouldRestart(Later, anybodyConnected: false));
        Assert.Equal((true, false, false), watcher.Describe());
    }

    // --- the editor's HTTP guards ----------------------------------------------

    [Fact]
    public async Task TheEditorRefusesAMissingTokenAndAForeignHost()
    {
        Common("[Server]\nServerName=x\n");
        Hosts("a");

        using var server = new ConfigEditorServer(new FleetConfigEditor(ConfigDir, HostsFile), 0);
        server.Start();
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}") };

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/model")).StatusCode);

        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/model"))
        {
            wrong.Headers.Add("X-Editor-Token", "0000");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
        }

        using (var right = new HttpRequestMessage(HttpMethod.Get, "/api/model"))
        {
            right.Headers.Add("X-Editor-Token", server.Token);
            var reply = await http.SendAsync(right);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            Assert.Contains("\"ok\":true", await reply.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A DNS-rebinding page reaches the same socket under its own name.
        using (var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/model"))
        {
            rebound.Headers.Host = "attacker.example";
            rebound.Headers.Add("X-Editor-Token", server.Token);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rebound)).StatusCode);
        }

        await stop.CancelAsync();
        await running;
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gg2.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("no Gg2.sln above the test binaries");
    }
}
