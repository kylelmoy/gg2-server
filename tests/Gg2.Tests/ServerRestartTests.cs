using System.Text.Json;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The <c>config-set</c> and <c>restart</c> verbs, and the loop that makes them
/// mean something together.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim these gate is "a setting changed from the panel takes effect",
/// and it is exactly the kind this repo keeps catching itself getting wrong.</b>
/// Every part can be green on its own — the file is written, the verb is
/// reachable, the flag exists — while the change never reaches a running server,
/// because nothing inside the repo contradicts a false claim about its own
/// capability. So these tests do the whole round: save, stop, re-read, and
/// assert the SERVER came back different.
/// </para>
/// <para>
/// ⚠️ <b>Not <see cref="ServerHarness"/>, because that harness is not allowed to
/// stop.</b> These own their own <see cref="GameServer"/> and the config file
/// under it, which is why the restart verb is deliberately absent from
/// <c>WebAdminTests.EveryControlPortVerbIsReachableFromThePanel</c>.
/// </para>
/// </remarks>
public sealed class ServerRestartTests : IDisposable
{
    private readonly string _directory;

    public ServerRestartTests()
    {
        ReferenceTree.RequireAvailable();

        _directory = Path.Combine(Path.GetTempPath(), $"gg2-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    private string ConfigPath => Path.Combine(_directory, "gg2.ini");

    /// <summary>A server on an ephemeral port, reading the config file this test owns.</summary>
    private GameServer Start(string contents)
    {
        File.WriteAllText(ConfigPath, contents);

        var options = ServerOptions.CreateDefault();
        ServerConfigFile.Apply(options, ConfigPath);
        options.ConfigFile = ConfigPath;
        options.Port = 0;
        options.MapDirectory = ReferenceTree.IncludedFiles;

        var server = new GameServer(options, new RecordingLog());
        server.Start();
        return server;
    }

    private static JsonElement Run(GameServer server, string command) =>
        JsonDocument.Parse(ControlCommands.Dispatch(server, command)).RootElement;

    /// <summary>
    /// A setting saved from the panel is in the file, and a restarted server
    /// reads it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The second half is the one that matters.</b> Asserting the file
    /// changed proves the writer works and says nothing about whether the server
    /// would ever see it — which is the shape of claim <c>docs/PARITY.md</c> warns
    /// about, and the reason the restart button exists at all.
    /// </remarks>
    [Fact]
    public void ASettingSavedFromThePanelSurvivesARestart()
    {
        using var server = Start("[Server]\nServerName=before\n");
        Assert.Equal("before", server.Options.ServerName);

        var saved = Run(server, """
            {"cmd":"config-set","settings":{"Server.ServerName":"after","Settings.PlayerLimit":"24"}}
            """);

        Assert.True(saved.GetProperty("ok").GetBoolean(), saved.ToString());
        Assert.Equal(2, saved.GetProperty("saved").GetInt32());
        Assert.True(saved.GetProperty("restartRequired").GetBoolean());

        // ⚠️ The RUNNING server is deliberately unchanged. Every subsystem read
        // its options at Start, so a half-applied change is the failure this
        // design refuses to have — and a test that let this pass would be
        // licensing exactly that.
        Assert.Equal("before", server.Options.ServerName);

        // What Program.Main's loop does, in the two lines that matter.
        var next = ServerOptions.CreateDefault();
        ServerConfigFile.Apply(next, ConfigPath);

        Assert.Equal("after", next.ServerName);
        Assert.Equal(24, next.PlayerLimit);
    }

    /// <summary>
    /// <c>restart</c> ends <see cref="GameServer.Run"/> and says it was a restart
    /// rather than a shutdown.
    /// </summary>
    /// <remarks>
    /// <b>The distinction is the whole of what <c>Program.Main</c> reads.</b> A
    /// restart that cancelled the host's own token would be indistinguishable
    /// from a Ctrl-C, and the process would exit instead of coming back.
    /// </remarks>
    [Fact]
    public async Task RestartEndsTheRunLoopAndIsNotAShutdown()
    {
        using var server = Start("[Server]\nServerName=restart me\n");
        Assert.False(server.RestartRequested);

        using var stopping = new CancellationTokenSource();

        // ⛔ NOT Task.Run: GameServer.Run is a blocking tick loop, and on the
        // pool a saturated one can leave it unstarted past the wait below —
        // measured, this test timed out at 13s against its own 10s deadline
        // while a full suite ran alongside it. LongRunning is the right tool
        // *here* precisely because Run is synchronous (`void Run(...)`), so the
        // dedicated thread covers the whole body; the RewardAuthClient bug was
        // LongRunning on an ASYNC method, where it covers only the synchronous
        // prefix and every continuation goes back to the pool.
        var loop = Task.Factory.StartNew(
            () => server.Run(stopping.Token),
            TaskCreationOptions.LongRunning);

        Assert.True(Run(server, """{"cmd":"restart"}""").GetProperty("ok").GetBoolean());
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(server.RestartRequested);
        Assert.False(stopping.IsCancellationRequested);
    }

    /// <summary>
    /// A restart requested while the server is FROZEN still happens.
    /// </summary>
    /// <remarks>
    /// ⚠️ The freeze branch sits inside the tick loop, so a restart flag checked
    /// only by the loop condition still gets there — but a frozen server is
    /// exactly the state an operator reaches for the restart button in, and
    /// "works unless you froze it first" is the kind of gap nobody finds until
    /// they need it. <see cref="GameServer.RequestRestart"/> resumes first.
    /// </remarks>
    [Fact]
    public async Task ARestartWorksOnAFrozenServer()
    {
        using var server = Start("[Server]\nServerName=frozen\n");

        using var stopping = new CancellationTokenSource();
        // ⛔ NOT Task.Run: GameServer.Run is a blocking tick loop, and on the
        // pool a saturated one can leave it unstarted past the wait below —
        // measured, this test timed out at 13s against its own 10s deadline
        // while a full suite ran alongside it. LongRunning is the right tool
        // *here* precisely because Run is synchronous (`void Run(...)`), so the
        // dedicated thread covers the whole body; the RewardAuthClient bug was
        // LongRunning on an ASYNC method, where it covers only the synchronous
        // prefix and every continuation goes back to the pool.
        var loop = Task.Factory.StartNew(
            () => server.Run(stopping.Token),
            TaskCreationOptions.LongRunning);

        Run(server, """{"cmd":"freeze"}""");
        Run(server, """{"cmd":"restart"}""");

        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(server.IsFrozen);
    }

    /// <summary>
    /// A generated admin password is available to be carried into the next start,
    /// and a configured one is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>The one failure a restart button must not have.</b>
    /// <c>WebAdminServer</c> generates a password when none is configured and
    /// prints it to the CONSOLE — which an operator who just pressed "restart" in
    /// a browser on another machine cannot see. Without the carry,
    /// <c>--web</c> with no <c>--web-password</c> means the restart button locks
    /// out whoever used it. <c>Program.Main</c> is the caller;
    /// <c>GameServer.GeneratedWebPassword</c> is the accessor.
    /// </para>
    /// <para>
    /// ⚠️ <b>Null for a CONFIGURED password is the other half</b>, and it is the
    /// security half: nothing may use this as a way to read an operator's own
    /// password back out of a running server.
    /// </para>
    /// </remarks>
    [Fact]
    public void AGeneratedAdminPasswordIsCarriedAndAConfiguredOneIsNot()
    {
        var options = ServerOptions.CreateDefault();
        options.Port = 0;
        options.MapDirectory = ReferenceTree.IncludedFiles;
        options.Web = WebAdminOptions.Disabled with { Enabled = true, Port = 0 };

        using (var generated = new GameServer(options, new RecordingLog()))
        {
            generated.Start();
            Assert.False(string.IsNullOrEmpty(generated.GeneratedWebPassword));
        }

        options.Web = options.Web with { Password = "chosen-by-the-operator" };

        using var configured = new GameServer(options, new RecordingLog());
        configured.Start();

        Assert.Null(configured.GeneratedWebPassword);
    }

    /// <summary>
    /// A server with no <c>--config</c> refuses to save, and says what to do.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Refused rather than kept in memory.</b> A save that appeared to work
    /// and was lost at the next restart is worse than one that did not happen —
    /// the operator would find out by restarting, which is the very next thing
    /// the page tells them to do.
    /// </remarks>
    [Fact]
    public void SavingWithNoConfigFileIsRefusedByName()
    {
        var options = ServerOptions.CreateDefault();
        options.Port = 0;
        options.MapDirectory = ReferenceTree.IncludedFiles;

        using var server = new GameServer(options, new RecordingLog());
        server.Start();

        var reply = Run(server, """{"cmd":"config-set","settings":{"Server.ServerName":"x"}}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("--config", reply.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value that would not validate is refused and the file is left alone.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The failure this prevents is a server that cannot come back.</b> The
    /// panel's save and its restart are two clicks apart; a config written with
    /// <c>PlayerLimit=100</c> would be accepted, saved, and then refused by
    /// <c>ServerOptions.Validate</c> at the restart — with the old server already
    /// stopped and nothing to fall back to.
    /// </remarks>
    [Fact]
    public void AnInvalidValueIsRefusedAndTheFileIsUntouched()
    {
        using var server = Start("[Server]\nServerName=safe\n");
        var before = File.ReadAllText(ConfigPath);

        var reply = Run(server, """{"cmd":"config-set","settings":{"Settings.PlayerLimit":"100"}}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("1..48", reply.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    /// <summary>
    /// A bot mode that disagrees with the checkpoint list is refused at the save,
    /// not at the restart.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This took a live server down.</b> An operator switched <c>Mode</c>
    /// from policy to search on a host whose file still carried <c>Policy</c>;
    /// the save validated, the restart reached <c>BotBrain.Build</c>, and the
    /// process exited 2 into a systemd crash loop. The file here carries no
    /// <c>Mode</c>, so the server starts, and the save supplies both halves.
    /// </remarks>
    [Theory]
    [InlineData("""{"Bots.Mode":"search","Bots.Policy":"checkpoints/a.npz"}""", "never consults one")]
    [InlineData("""{"Bots.Mode":"policy"}""", "none was given")]
    public void ABotModeTheServerWouldRefuseIsRefusedAtTheSave(string settings, string expected)
    {
        using var server = Start("[Server]\nServerName=safe\n");
        var before = File.ReadAllText(ConfigPath);

        var reply = Run(server, $$"""{"cmd":"config-set","settings":{{settings}}}""");

        Assert.False(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        Assert.Contains(expected, reply.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    /// <summary>An unknown setting id is refused rather than silently dropped.</summary>
    [Fact]
    public void AnUnknownSettingIsRefusedByName()
    {
        using var server = Start("[Server]\nServerName=safe\n");

        var reply = Run(server, """{"cmd":"config-set","settings":{"Server.NoSuchThing":"x"}}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "Server.NoSuchThing", reply.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Saving one setting does not promote the command line's overrides into the
    /// file.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The candidate is built from the FILE, not from the running
    /// options.</b> Building it from the running options would write every flag
    /// on the command line into the config the first time anybody saved anything
    /// — turning a temporary <c>--speed max</c> into a permanent one, silently.
    /// </remarks>
    [Fact]
    public void SavingDoesNotWriteTheRunningServersCommandLineOverridesIntoTheFile()
    {
        using var server = Start("[Server]\nServerName=before\n");

        // What a flag does: the running options differ from the file.
        server.Options.SpeedMultiplier = 4.0;

        Run(server, """{"cmd":"config-set","settings":{"Server.ServerName":"after"}}""");

        var next = ServerOptions.CreateDefault();
        ServerConfigFile.Apply(next, ConfigPath);

        Assert.Equal("after", next.ServerName);
        Assert.Equal(1.0, next.SpeedMultiplier);
    }

    /// <summary>
    /// <c>config</c> reports the file, its writability, and every setting.
    /// </summary>
    [Fact]
    public void TheConfigVerbReportsEverySetting()
    {
        using var server = Start("[Server]\nServerName=reported\n");

        var reply = Run(server, """{"cmd":"config"}""");

        Assert.True(reply.GetProperty("writable").GetBoolean());
        Assert.Equal(ConfigPath, reply.GetProperty("file").GetString());

        var settings = reply.GetProperty("settings").EnumerateArray().ToArray();
        Assert.Equal(ServerSettings.All.Count, settings.Length);

        var name = settings.Single(s => s.GetProperty("id").GetString() == "Server.ServerName");
        Assert.Equal("reported", name.GetProperty("value").GetString());
    }

    /// <summary>
    /// <c>config</c> reports what is in the FILE, and marks what the running
    /// server has not picked up yet.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Found in a browser, not in a test, and it made every save look
    /// discarded.</b> Nothing is applied live, so a page that read the running
    /// server's options after a save showed the operator the OLD value back with
    /// a "saved" toast beside it. The fields have to edit what the next start
    /// will use, and <c>pending</c> is how the page says a restart is the
    /// outstanding step rather than leaving the two silently different.
    /// </remarks>
    [Fact]
    public void TheConfigVerbReportsTheFileAndFlagsWhatIsWaitingForARestart()
    {
        using var server = Start("[Server]\nServerName=before\n");

        Run(server, """{"cmd":"config-set","settings":{"Server.ServerName":"after"}}""");

        var name = Run(server, """{"cmd":"config"}""")
            .GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "Server.ServerName");

        Assert.Equal("after", name.GetProperty("value").GetString());
        Assert.True(name.GetProperty("pending").GetBoolean());

        // The control: a setting nobody touched is not pending, so the flag is
        // known to be able to be absent rather than being on every row.
        var welcome = Run(server, """{"cmd":"config"}""")
            .GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "Server.WelcomeMessage");

        Assert.False(welcome.TryGetProperty("pending", out _));
    }

    /// <summary>
    /// A setting a flag has pinned is <c>overridden</c>, never <c>pending</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A false alarm that could never be cleared.</b> A flag makes the file
    /// and the running value differ for ever, and a restart does not reconcile
    /// them — so marking it "restart pending" would leave a badge on the page
    /// that no action removes, next to the badge that actually explains it. The
    /// two states are mutually exclusive by construction.
    /// </remarks>
    [Fact]
    public void APinnedSettingIsNotAlsoReportedAsWaitingForARestart()
    {
        using var server = Start("[Server]\nServerName=from the file\n");

        // What a flag does: the running options differ from the file, and the
        // host records the id so the page can say why.
        server.Options.ServerName = "from the flag";
        server.Options.CommandLineOverrides = ["Server.ServerName"];

        var reply = Run(server, """{"cmd":"config"}""");

        Assert.Equal(
            ["Server.ServerName"],
            reply.GetProperty("overridden").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var name = reply.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "Server.ServerName");

        Assert.Equal("from the file", name.GetProperty("value").GetString());
        Assert.False(name.TryGetProperty("pending", out _));

        // The control: without the override record the very same difference IS
        // pending, so the exclusion is doing the work rather than the values
        // happening to agree.
        server.Options.CommandLineOverrides = [];

        Assert.True(Run(server, """{"cmd":"config"}""")
            .GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "Server.ServerName")
            .GetProperty("pending").GetBoolean());
    }

    /// <summary>
    /// A config file with a rotation and no <c>Map</c> key opens on the rotation.
    /// </summary>
    /// <remarks>
    /// <c>GameServerCreate.gml:110-113</c>, applied by
    /// <see cref="ServerConfigFile.Apply"/> rather than only by the command line.
    /// ⚠️ <b>It is here because of what it does to the settings page:</b> without
    /// it, a file-loaded <c>Map</c> of <c>ctf_truefort</c> was compared against a
    /// running <c>Map</c> of <c>""</c> and every server with a rotation reported
    /// its opening map as changed by a command line that never mentioned it.
    /// </remarks>
    [Fact]
    public void ARotationWithNoMapKeyEmptiesTheOpeningMap()
    {
        var options = ServerOptions.CreateDefault();
        var path = Path.Combine(_directory, "rotation.ini");

        File.WriteAllText(path, "[Server]\nRotation=koth_valley,ctf_2dfort\n");
        ServerConfigFile.Apply(options, path);

        Assert.Equal(string.Empty, options.Map);

        // A file that NAMES a map keeps it, which is the half that makes the rule
        // a rule rather than "rotations delete the map".
        var pinned = ServerOptions.CreateDefault();
        var pinnedPath = Path.Combine(_directory, "pinned.ini");

        File.WriteAllText(pinnedPath, "[Server]\nMap=ctf_orange\nRotation=koth_valley,ctf_2dfort\n");
        ServerConfigFile.Apply(pinned, pinnedPath);

        Assert.Equal("ctf_orange", pinned.Map);
    }

    /// <summary>
    /// ⛔ No secret's value is ever in the <c>config</c> reply.
    /// </summary>
    /// <remarks>
    /// The panel is the one surface in this project meant to be reachable from
    /// the network, and two of the five secrets are the admin password and the
    /// certificate's. An endpoint that echoed them would turn a stolen session
    /// cookie into the credential itself. This checks for the actual strings, so
    /// it fails on any future field that starts including one.
    /// </remarks>
    [Fact]
    public void TheConfigVerbNeverEchoesASecret()
    {
        // ⚠️ No CertificatePassword here, and its absence is itself a finding:
        // WebAdminOptions.Validate refuses one with no certificate to open, so a
        // server carrying that combination cannot start at all. Its secrecy is
        // covered structurally below instead.
        using var server = Start(
            """
            [Server]
            Password=game-secret-4f2a
            [Web]
            Port=0
            Password=admin-secret-9c1b
            """);

        var reply = ControlCommands.Dispatch(server, """{"cmd":"config"}""");

        Assert.DoesNotContain("game-secret-4f2a", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret-9c1b", reply, StringComparison.Ordinal);

        var settings = JsonDocument.Parse(reply).RootElement
            .GetProperty("settings").EnumerateArray().ToArray();

        // The control: it does say a secret is SET, which is what a settings page
        // needs and is not the value.
        Assert.True(settings.Single(s => s.GetProperty("id").GetString() == "Web.Password")
            .GetProperty("set").GetBoolean());

        // The negative control, so "set" is known to be able to be false rather
        // than a constant: the game password is set and nothing configured a
        // certificate password.
        Assert.False(settings.Single(s => s.GetProperty("id").GetString() == "Web.CertificatePassword")
            .GetProperty("set").GetBoolean());

        // ⛔ And structurally, for every secret rather than the two this file
        // happens to set: a secret carries no "value" field at all. A future
        // secret added to the table is covered by this without anybody
        // remembering to extend the string checks above.
        foreach (var setting in ServerSettings.All.Where(s => s.Secret))
        {
            var row = settings.Single(s => s.GetProperty("id").GetString() == setting.Id);

            Assert.False(
                row.TryGetProperty("value", out _),
                $"{setting.Id} is a secret and the config reply carries its value");
        }
    }

    /// <summary>
    /// A secret can still be CHANGED from the panel, even though it cannot be
    /// read back.
    /// </summary>
    /// <remarks>
    /// The half that would be easy to lose while making the reply safe: a
    /// write-only field is the correct shape for a password, and a field that is
    /// neither readable nor writable is a setting the panel cannot manage at all.
    /// </remarks>
    [Fact]
    public void ASecretCanBeWrittenEvenThoughItCannotBeRead()
    {
        using var server = Start("[Server]\nServerName=x\n");

        var reply = Run(server, """{"cmd":"config-set","settings":{"Server.Password":"new-one"}}""");
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());

        var next = ServerOptions.CreateDefault();
        ServerConfigFile.Apply(next, ConfigPath);
        Assert.Equal("new-one", next.Password);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not a test failure.
        }
    }
}
