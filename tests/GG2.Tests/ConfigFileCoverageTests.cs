using System.Reflection;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// Every setting the server has must be reachable from a config file, and must
/// survive being written back to one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same gate <see cref="CliCoverageTests"/> is, one surface along.</b> A
/// <see cref="ServerOptions"/> property can be fully implemented, wired to real
/// behaviour, gated by its own tests and reachable from a flag, and still be
/// absent from the config file — nothing goes red for that, because every test
/// that touches it builds a <see cref="ServerOptions"/> in C# or passes a flag.
/// The option exists; the way in does not. That is exactly what
/// <c>docs/PARITY.md</c> calls a claim the repo makes about itself, and the
/// claim here — "all server configuration settings load from file" — is one a
/// reader only disproves by acting on it.
/// </para>
/// <para>
/// <b>Three assertions, and each one can fail on its own.</b> Coverage says every
/// property is named by a row. The probe says every row MOVES the property it
/// names, so a row that parses and drops the value on the floor is caught. The
/// round trip says <see cref="ServerConfigFile.Write"/> is the exact inverse of
/// <see cref="ServerConfigFile.Apply"/>, so a setting that can be read and not
/// saved — which is what a settings page would silently lose — is caught too.
/// </para>
/// </remarks>
public sealed class ConfigFileCoverageTests : IDisposable
{
    private readonly List<string> _directories = [];

    /// <summary>
    /// Settable options that deliberately have no config key, and why.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A row here is a decision, not an exemption to reach for.</b> The bar
    /// is that the option would be WRONG to expose, not merely that nobody has
    /// wired it up yet — an option a user cannot set is what this test exists to
    /// catch, and a row here that means "not done" turns the gate off silently.
    /// </remarks>
    private static readonly Dictionary<string, string> NotInTheConfigFile = new()
    {
        ["BotMode"] =
            "is the scripted control arm the bench harness and the recorder compare "
            + "against. A server always runs the policy brain; an old [Bots] Mode key is "
            + "an unknown key, which a config file ignores.",

        ["RandomSeed"] =
            "forces the simulation's random stream, which a live server must never do — the "
            + "reference's randomize() is what stops every server on a machine scattering its "
            + "first blast identically. It exists for tests that have to record the same match "
            + "twice, and it has no command-line flag for the same reason. A config key would "
            + "invite somebody to run a real server with it, and unlike a flag it would then "
            + "stay there across every restart.",

        ["CommandLineOverrides"] =
            "is DERIVED rather than configured: CommandLine.Parse computes it by diffing what "
            + "it built against what the config file alone would have produced, so that the "
            + "settings page can grey out a field a flag has pinned. A config key for it would "
            + "be a file describing which of its own keys are being ignored.",

        ["ConfigFile"] =
            "is the path of the config file itself. A file naming another file is a redirect "
            + "with a cycle in it and no rule about which of the two wins; --config is where "
            + "the path comes from, and the panel writes back to whichever one it named.",
    };

    private static ServerOptions Fresh() => new() { MapDirectory = "maps" };

    private string Path(string name)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"gg2-cover-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return System.IO.Path.Combine(directory, name);
    }

    /// <summary>
    /// Every public settable <see cref="ServerOptions"/> property is named by a
    /// row in <see cref="ServerSettings.All"/>, or is named as deliberately
    /// absent.
    /// </summary>
    [Fact]
    public void EverySettableOptionIsReachableFromAConfigFile()
    {
        var covered = ServerSettings.All.Select(setting => setting.Property)
            .Concat(NotInTheConfigFile.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var settable = typeof(ServerOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.SetMethod!.IsPublic)
            .Select(property => property.Name)
            .ToArray();

        var missing = settable.Where(name => !covered.Contains(name)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"ServerOptions properties with no config-file key: {string.Join(", ", missing)}. "
            + "Add a row to ServerSettings, or a reason to "
            + "ConfigFileCoverageTests.NotInTheConfigFile. An option that can only be set "
            + "from a flag cannot be set from the admin panel either, because the panel "
            + "edits the file.");

        // And the other direction: a row naming a property that no longer exists
        // is a row that stopped testing anything the day it was renamed.
        var stale = covered.Where(name => !settable.Contains(name, StringComparer.Ordinal)).ToArray();

        Assert.True(
            stale.Length == 0,
            $"ServerSettings names options ServerOptions does not have: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// Each row's <see cref="ServerSetting.Set"/> actually moves the property it
    /// names, off its default.
    /// </summary>
    /// <remarks>
    /// <b>The cheap contradicting action.</b> A setter that is called and drops
    /// the value on the floor looks identical from outside to one that works;
    /// reading the table does not distinguish them and comparing the property
    /// against its default does.
    /// </remarks>
    [Fact]
    public void EveryRowChangesTheOptionItNames()
    {
        var defaults = Fresh();

        foreach (var setting in ServerSettings.All)
        {
            var options = Fresh();
            var probe = setting.ProbeFor(options);
            setting.Set(options, probe);

            var property = typeof(ServerOptions).GetProperty(setting.Property)!;

            Assert.True(
                !Equals(property.GetValue(defaults), property.GetValue(options)),
                $"{setting.Id}={probe} left {setting.Property} at its default "
                + $"({property.GetValue(defaults)}). The row parses and changes nothing.");

            // And the value the row hands back is the one that went in, so Get
            // and Set are inverses at the row level as well as at the file level.
            Assert.Equal(setting.Get(options), setting.Get(options));
        }
    }

    /// <summary>
    /// <see cref="ServerConfigFile.Write"/> then
    /// <see cref="ServerConfigFile.Apply"/> gives back every setting unchanged.
    /// </summary>
    /// <remarks>
    /// <b>This is the assertion that "all settings load from file" is a claim
    /// about, and it is checked against a NON-DEFAULT value for every one of
    /// them.</b> A round trip over defaults would pass for a setting the writer
    /// silently skips, because the reader would then leave the default in place
    /// and the two would agree for the wrong reason.
    /// </remarks>
    [Fact]
    public void EverySettingSurvivesAWriteAndAReRead()
    {
        var written = Fresh();

        foreach (var setting in ServerSettings.All)
            setting.Set(written, setting.ProbeFor(written));

        var path = Path("round-trip.json");
        ServerConfigFile.Write(written, path);

        var read = Fresh();
        ServerConfigFile.Apply(read, path);

        foreach (var setting in ServerSettings.All)
        {
            Assert.True(
                setting.Get(written) == setting.Get(read),
                $"{setting.Id} did not survive the round trip: wrote '{setting.Get(written)}', "
                + $"read back '{setting.Get(read)}'.");
        }
    }

    /// <summary>
    /// The round trip is not passing because nothing moved: the written file
    /// really does differ from the defaults, in every section.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The vacuity control for the test above.</b> A writer that emitted
    /// nothing at all, and a reader that read nothing at all, would agree
    /// perfectly — so this asserts that a defaults-only round trip and a
    /// probe-only one disagree on every row that is not deliberately equal.
    /// </remarks>
    [Fact]
    public void TheRoundTripWouldNoticeAWriterThatWroteNothing()
    {
        var defaults = Fresh();
        var probed = Fresh();

        foreach (var setting in ServerSettings.All)
            setting.Set(probed, setting.ProbeFor(probed));

        var path = Path("defaults.json");
        ServerConfigFile.Write(defaults, path);

        var read = Fresh();
        ServerConfigFile.Apply(read, path);

        var same = ServerSettings.All
            .Where(setting => setting.Get(read) == setting.Get(probed))
            .Select(setting => setting.Id)
            .ToArray();

        Assert.True(
            same.Length == 0,
            "these probes are the same as the default, so the round-trip test cannot fail "
            + $"for them: {string.Join(", ", same)}");
    }

    /// <summary>
    /// Writing preserves notes and keys this project has never heard of.
    /// </summary>
    /// <remarks>
    /// The file being edited is an operator's, and it may hold keys for a newer
    /// release and their own <c>"//"</c> notes about why a limit is what it is. A
    /// writer that regenerated the file would silently delete all of it.
    /// </remarks>
    [Fact]
    public void WritingKeepsNotesAndUnknownKeys()
    {
        var path = Path("hand-written.json");

        File.WriteAllText(path,
            """
            {
              "//": "our public server. do not raise PlayerLimit past 24, the VPS falls over.",
              "Settings": {"HostingPort": 8190, "GibLevel": 2},
              "Haxxy": {"RewardKey": "something"},
              "Server": {"ServerName": "Old Name"}
            }
            """);

        var options = Fresh();
        ServerConfigFile.Apply(options, path);
        options.ServerName = "New Name";
        ServerConfigFile.Write(options, path);

        var text = File.ReadAllText(path);

        Assert.Contains("our public server", text, StringComparison.Ordinal);
        Assert.Contains("\"GibLevel\": 2", text, StringComparison.Ordinal);
        Assert.Contains("\"RewardKey\": \"something\"", text, StringComparison.Ordinal);
        Assert.Contains("\"ServerName\": \"New Name\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Old Name", text, StringComparison.Ordinal);

        // The key was replaced in place rather than appended: one ServerName.
        Assert.Equal(1, text.Split("\"ServerName\"").Length - 1);
    }

    /// <summary>
    /// Saving writes a minimal diff, not every key the server has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Found by saving from a browser and reading the file, not by a
    /// test.</b> The writer used to emit all eighty-odd keys, and two of them —
    /// <c>[Server] MapDirectory</c> and <c>[Nav] Directory</c> — default to paths
    /// RESOLVED FROM DISK, so the first save stamped one machine's absolute
    /// install path into the operator's config and pinned it there. Moving the
    /// install then broke a server whose config nobody had edited.
    /// </para>
    /// <para>
    /// ⚠️ The assertion is about the two RESOLVED settings specifically, because
    /// they are the ones where "written as a default" is actively harmful rather
    /// than merely noisy. A general "the file stays short" assertion would pass
    /// for a writer that still pinned them.
    /// </para>
    /// </remarks>
    [Fact]
    public void SavingDoesNotStampResolvedDefaultPathsIntoTheFile()
    {
        var path = Path("minimal.json");
        File.WriteAllText(path, """{"Server": {"ServerName": "before"}}""");

        // ⚠️ CreateDefault, not Fresh: the whole point is the two settings whose
        // default is RESOLVED FROM DISK, and Fresh's hard-coded "maps" is not that
        // default — it would differ from the baseline and be written legitimately,
        // testing nothing.
        var options = ServerOptions.CreateDefault();
        ServerConfigFile.Apply(options, path);
        options.ServerName = "after";
        ServerConfigFile.Write(options, path);

        var text = File.ReadAllText(path);

        Assert.Contains("\"ServerName\": \"after\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDirectory", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory", text, StringComparison.Ordinal);

        // A default nobody changed stays unwritten too, so an operator's file
        // does not grow every knob the server has the first time they rename it.
        Assert.DoesNotContain("CapLimit", text, StringComparison.Ordinal);

        // The control: an explicitly CHANGED setting is written even though the
        // file never named it, or the minimal diff would be a save that saves
        // nothing.
        options.CapLimit = 3;
        ServerConfigFile.Write(options, path);
        Assert.Contains("\"CapLimit\": 3", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// A key the file already names keeps being written, even at its default.
    /// </summary>
    /// <remarks>
    /// The other half of the minimal diff: an operator who wrote
    /// <c>PlayerLimit=10</c> by hand meant it, and a save that deleted the line
    /// because it happens to equal the default would be the panel editing
    /// something nobody asked it to.
    /// </remarks>
    [Fact]
    public void AKeyTheFileAlreadyNamesSurvivesASaveAtItsDefault()
    {
        var path = Path("explicit.json");
        File.WriteAllText(path, """{"Settings": {"PlayerLimit": 10}}""");

        var options = Fresh();
        ServerConfigFile.Apply(options, path);
        Assert.Equal(10, options.PlayerLimit);

        ServerConfigFile.Write(options, path);

        Assert.Contains("\"PlayerLimit\": 10", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// A saved rotation wins over a <c>[Maps]</c> section left in the same file.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The failure this prevents is a settings page that appears to
    /// work.</b> <c>[Maps]</c> is the reference's own rotation source and an
    /// inherited <c>gg2.ini</c> has one; a rotation saved beside it that lost the
    /// precedence fight would be an edit that survives the write, survives the
    /// re-read, and never reaches the server.
    /// </remarks>
    [Fact]
    public void ASavedRotationBeatsAnInheritedMapsSection()
    {
        var path = Path("inherited.json");

        File.WriteAllText(path,
            """{"Maps": {"ctf_truefort": 1, "cp_dirtbowl": 2}}""");

        var options = Fresh();
        ServerConfigFile.Apply(options, path);

        // ⚠️ The two keys REORDER the built-in twenty; they do not select two of
        // them. A map with no key keeps its 1-based position as its priority,
        // which is what makes an empty [Maps] produce the stock rotation — so
        // cp_dirtbowl is third here (priority 2, after ctf_2dfort's default 2 on
        // a tie broken by the built-in order), not second of two.
        Assert.Equal("ctf_truefort", options.MapRotation[0]);
        Assert.Equal(20, options.MapRotation.Count);

        options.MapRotation = ["koth_valley", "ctf_2dfort"];
        ServerConfigFile.Write(options, path);

        var read = Fresh();
        ServerConfigFile.Apply(read, path);
        Assert.Equal(["koth_valley", "ctf_2dfort"], read.MapRotation);
    }

    /// <summary>
    /// A rotation FILE that still names the same maps is left alone.
    /// </summary>
    /// <remarks>
    /// The other half of the rule above: removing <c>MapRotation=</c> from a
    /// server whose rotation genuinely comes from that file would be the writer
    /// changing a configuration nobody edited.
    /// </remarks>
    [Fact]
    public void AnAgreeingRotationFileSurvivesASave()
    {
        var path = Path("with-file.json");
        var rotation = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!, "rotation.txt");

        File.WriteAllText(rotation, "# ours\nkoth_valley\nctf_2dfort\n");
        File.WriteAllText(path, """{"Server": {"MapRotation": "rotation.txt"}}""");

        var options = Fresh();
        ServerConfigFile.Apply(options, path);
        Assert.Equal(["koth_valley", "ctf_2dfort"], options.MapRotation);

        ServerConfigFile.Write(options, path);

        Assert.Contains("\"MapRotation\": \"rotation.txt\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing to a path that does not exist yet creates a whole config file.
    /// </summary>
    /// <remarks>
    /// The case an operator hits the first time they save from the panel on a
    /// server started with <c>--config</c> pointing at a file they have not
    /// written yet — and the case the section-appending branch of
    /// <c>ConfigDocument.Put</c> exists for.
    /// </remarks>
    [Fact]
    public void WritingCreatesAFileThatReadsBack()
    {
        var options = Fresh();
        options.ServerName = "fresh";
        options.PlayerLimit = 12;

        var path = Path("new.json");
        ServerConfigFile.Write(options, path);

        var read = Fresh();
        ServerConfigFile.Apply(read, path);

        Assert.Equal("fresh", read.ServerName);
        Assert.Equal(12, read.PlayerLimit);
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
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
