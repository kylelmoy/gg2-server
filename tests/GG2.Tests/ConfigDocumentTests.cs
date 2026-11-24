using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The JSON grammar under <see cref="ServerConfigFile"/>: typing, names,
/// in-place edits, the fleet merge, and the converter from a reference-format
/// <c>gg2.ini</c>. <c>docs/CONFIG.md</c>.
/// </summary>
public sealed class ConfigDocumentTests
{
    private static ServerSetting Setting(string id) => ServerSettings.Find(id)!;

    private static ServerOptions Load(string json)
    {
        var options = ServerOptions.CreateDefault();
        ServerConfigFile.ApplyText(options, json, "gg2.json");
        return options;
    }

    [Fact]
    public void ValuesAreWrittenTypedAsTheirSettingIs()
    {
        var config = new ConfigDocument();
        config.Set(Setting("Bots.Enabled"), "1");
        config.Set(Setting("Settings.PlayerLimit"), "16");
        config.Set(Setting("Runtime.Speed"), "1.5");
        config.Set(Setting("Server.Rotation"), "ctf_truefort,koth_valley");
        config.Set(Setting("Server.ServerName"), "16");

        var text = config.ToText();

        Assert.Contains("\"Enabled\": true", text, StringComparison.Ordinal);
        Assert.Contains("\"PlayerLimit\": 16", text, StringComparison.Ordinal);
        Assert.Contains("\"Speed\": 1.5", text, StringComparison.Ordinal);
        Assert.Contains("\"ctf_truefort\",", text, StringComparison.Ordinal);

        // Text stays text even when it looks like a number.
        Assert.Contains("\"ServerName\": \"16\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatIsNotTheKindsShapeIsKeptAsAString()
    {
        // Speed is a number or the word max; the word must survive a write.
        var config = new ConfigDocument();
        config.Set(Setting("Runtime.Speed"), "max");

        Assert.Equal(double.PositiveInfinity, Load(config.ToText()).SpeedMultiplier);
    }

    [Fact]
    public void EveryJsonShapeReadsBackAsTheSettingsText()
    {
        var options = Load("""
            {
              "Settings": {"PlayerLimit": "16"},
              "Server": {"AutoBalance": false, "Rotation": ["koth_valley", "ctf_2dfort"], "CapLimit": 3.0},
              "Bots": {"Enabled": 1}
            }
            """);

        Assert.Equal(16, options.PlayerLimit);
        Assert.False(options.Autobalance);
        Assert.Equal(["koth_valley", "ctf_2dfort"], options.MapRotation);
        Assert.Equal(3, options.CapLimit);
        Assert.True(options.Bots.Enabled);
    }

    [Fact]
    public void NamesAreCaseInsensitiveAndValuesAreNot()
    {
        var options = Load("""{"server": {"servername": "The Fort", "PASSWORD": "Secret"}}""");

        Assert.Equal("The Fort", options.ServerName);
        Assert.Equal("Secret", options.Password);
    }

    [Fact]
    public void NullIsAbsent()
    {
        var options = Load("""{"Settings": {"PlayerLimit": null}}""");

        Assert.Equal(ServerOptions.CreateDefault().PlayerLimit, options.PlayerLimit);
    }

    [Theory]
    [InlineData("[Server]\nServerName=x\n", "not valid JSON")]
    [InlineData("[]", "one JSON object")]
    [InlineData("""{"Server": "x"}""", "must be an object")]
    [InlineData("""{"Server": {"ServerName": {"a": 1}}}""", "got an object")]
    [InlineData("""{"Server": {"Rotation": [["a"]]}}""", "entry 1")]
    [InlineData("""{"Server": {"ServerName": "a", "servername": "b"}}""", "named twice")]
    [InlineData("""{"Server": {}, "SERVER": {}}""", "named twice")]
    [InlineData("""{"Settings": {"PlayerLimit": "sixteen"}}""", "PlayerLimit")]
    public void WhatCannotBeReadIsRefused(string json, string message)
    {
        var e = Assert.Throws<FormatException>(() => Load(json));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSectionsAndKeysAreIgnoredAndKeptByASave()
    {
        // ⚠️ Ignored, so a file written for a newer release still starts an
        // older one: a fleet push reaches hosts that have not been upgraded.
        const string Json = """
            {
              "//": "a note",
              "Future": {"Thing": [1, 2]},
              "Server": {"NotAKeyYet": 5, "ServerName": "before"}
            }
            """;

        var options = Load(Json);
        options.ServerName = "after";

        var saved = ServerConfigFile.Edit(options, Json, "gg2.json");

        Assert.Contains("\"//\": \"a note\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"Future\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"NotAKeyYet\": 5", saved, StringComparison.Ordinal);
        Assert.Contains("\"ServerName\": \"after\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditKeepsTheFilesOrderAndSpelling()
    {
        const string Json = """{"Server": {"welcomemessage": "hi", "ServerName": "a"}, "Settings": {"PlayerLimit": 9}}""";

        var options = Load(Json);
        options.WelcomeMessage = "hello";
        options.Autobalance = false;

        var saved = ServerConfigFile.Edit(options, Json, "gg2.json");

        // Replaced under the file's own spelling, where it stood...
        Assert.Contains("\"welcomemessage\": \"hello\"", saved, StringComparison.Ordinal);
        Assert.True(saved.IndexOf("welcomemessage", StringComparison.Ordinal) < saved.IndexOf("ServerName", StringComparison.Ordinal));

        // ...and a new key at the end of its section, not after Settings.
        Assert.True(saved.IndexOf("AutoBalance", StringComparison.Ordinal) < saved.IndexOf("Settings", StringComparison.Ordinal));
        Assert.Equal(Load(saved).WelcomeMessage, options.WelcomeMessage);
    }

    [Fact]
    public void ATrailingSpaceSurvivesARoundTrip()
    {
        // The ini needed a quoting rule for this, and it is the default.
        var options = ServerOptions.CreateDefault();
        options.Bots = options.Bots with { NamePrefix = "[B] " };

        Assert.Equal("[B] ", Load(ServerConfigFile.Edit(options, string.Empty, "gg2.json")).Bots.NamePrefix);
    }

    [Fact]
    public void AnOverlayWinsKeyByKey()
    {
        var common = ConfigDocument.Parse("""{"Server": {"ServerName": "shared", "WelcomeMessage": "hi"}, "Bots": {"Enabled": true}}""");
        var overlay = ConfigDocument.Parse("""{"server": {"servername": "mine"}, "Bots": {"Enabled": null}, "Chat": {"Votes": false}}""");

        var merged = common.Merge(overlay);

        Assert.Equal("mine", merged.Text("Server", "ServerName"));
        Assert.Equal("hi", merged.Text("Server", "WelcomeMessage"));
        Assert.False(merged.Has("Bots", "Enabled"));
        Assert.Equal("0", merged.Text("Chat", "Votes"));

        // Neither input is changed by the merge.
        Assert.Equal("shared", common.Text("Server", "ServerName"));
    }

    [Fact]
    public void MaskingHidesEverySecretAndNothingElse()
    {
        var masked = FleetConfigEditor.MaskSecrets("""
            {
              "Server": {"Password": "hunter2", "ServerName": "Password"},
              "Web": {"password": 1234, "CertificatePassword": ""},
              "Chat": {"AdminPassword": "x\"y"}
            }
            """);

        Assert.DoesNotContain("hunter2", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("1234", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("x\\\"y", masked, StringComparison.Ordinal);
        Assert.Contains("\"ServerName\": \"Password\"", masked, StringComparison.Ordinal);

        // An unset secret is shown as unset, not as a mask.
        Assert.Contains("\"CertificatePassword\": \"\"", masked, StringComparison.Ordinal);
    }

    // --- the converter -------------------------------------------------------

    [Fact]
    public void AStockIniConvertsToTheSameServer()
    {
        const string Ini = """
            ; a hosted server's gg2.ini
            [Settings]
            HostingPort=8190
            PlayerLimit=16.0
            CrosshairFilename=crosshair.png
            [Server]
            ServerName="The Fort"
            AutoBalance=2
            Time Limit=20
            [Bots]
            NamePrefix="[BOT] "
            [Haxxy]
            RewardKey=abc
            [Maps]
            ctf_2dfort=1
            ctf_truefort=0
            [Server]
            ServerName=Last one wins
            """;

        var result = IniConverter.Convert(Ini);
        var options = Load(result.Json);

        Assert.Equal(16, options.PlayerLimit);
        Assert.Equal("Last one wins", options.ServerName);
        Assert.True(options.Autobalance);
        Assert.Equal(20, options.TimeLimitMinutes);
        Assert.Equal("[BOT] ", options.Bots.NamePrefix);
        Assert.Equal("ctf_2dfort", options.MapRotation[0]);
        Assert.DoesNotContain("ctf_truefort", options.MapRotation);

        Assert.Contains("\"AutoBalance\": true", result.Json, StringComparison.Ordinal);
        Assert.Equal(["[Settings] CrosshairFilename", "[Haxxy] RewardKey"], result.Dropped);
    }

    [Fact]
    public void ConversionFillsInNothing()
    {
        // A Web.Port on its own still turns the panel on, and nothing the ini
        // left out appears: a default stays a default.
        var result = IniConverter.Convert("[Web]\nPort=9000\n");

        Assert.True(Load(result.Json).Web.Enabled);
        Assert.DoesNotContain("\"Enabled\"", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Server\"", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIniTheServerWouldRefuseIsRefusedAtConversion()
    {
        var e = Assert.Throws<FormatException>(() => IniConverter.Convert("[Settings]\nPlayerLimit=lots\n"));
        Assert.Contains("PlayerLimit", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RawConversionKeepsEveryKeyAsText()
    {
        var result = IniConverter.Convert("; members\n[hub]\nUrl=https://203.0.113.7:8191/\nPairing=abc\n", raw: true);
        var config = ConfigDocument.Parse(result.Json);

        Assert.Equal("https://203.0.113.7:8191/", config.Text("hub", "Url"));
        Assert.Equal("abc", config.Text("hub", "Pairing"));
        Assert.Empty(result.Dropped);
    }
}
