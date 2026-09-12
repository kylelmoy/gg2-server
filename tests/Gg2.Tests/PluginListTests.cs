using Gg2.Server;
using Gg2.Server.Plugins;

namespace Gg2.Tests;

/// <summary>
/// Parsing <c>ServerPluginList</c>, resolving each plugin's zip hash, and the
/// per-player table every plugin keeps its state in.
/// </summary>
/// <remarks>
/// The advertised string is the entire binding between the two halves of a
/// plugin: the client splits it on <c>,</c> then on <c>@</c>, downloads each
/// zip by hash, and gives each plugin the <b>index</b> it appeared at as its
/// packet id. Nothing on the wire names a plugin afterwards, so a formatting
/// slip here is a cross-wiring, not a parse error.
/// </remarks>
public sealed class PluginListTests
{
    private const string ChatMd5 = "46d0937c9206dcf27e036100d1ea8685";
    private const string ChatV6Md5 = "bad8081d64f0b808a2bb3a6d88978fb5";

    private sealed class FixedHashes(string hash) : IPluginHashSource
    {
        public List<string> Fetched { get; } = [];

        public string Fetch(string name)
        {
            Fetched.Add(name);
            return hash;
        }
    }

    private sealed class RefusingHashes : IPluginHashSource
    {
        public string Fetch(string name) =>
            throw new InvalidOperationException($"the network must not be reached for '{name}'");
    }

    // --- names ---------------------------------------------------------------

    [Theory]
    [InlineData("chat", true)]
    [InlineData("chat_v2_pr", true)]
    [InlineData("a", true)]
    [InlineData("0", true)]
    [InlineData("_", true)]
    [InlineData("", false)]
    [InlineData("Chat", false)]        // checkpluginname's alphabet has no uppercase
    [InlineData("chat v2", false)]
    [InlineData("chat-v2", false)]
    [InlineData("chat.v2", false)]
    public void NamesFollowCheckPluginName(string name, bool valid) =>
        Assert.Equal(valid, PluginNames.IsValid(name));

    [Fact]
    public void AnEmptyListIsNoPluginsRatherThanOneNamelessOne()
    {
        Assert.Empty(PluginNames.ParseList(string.Empty));
        Assert.Empty(PluginNames.ParseList("   "));
    }

    [Fact]
    public void EntriesAreTrimmedTheWayGetPluginHashesTrimsThem()
    {
        var specs = PluginNames.ParseList(" chat_v2_pr , other ");

        Assert.Equal(["chat_v2_pr", "other"], specs.Select(s => s.Name));
        Assert.All(specs, s => Assert.Null(s.Md5));
    }

    /// <summary>
    /// A duplicate is refused rather than deduplicated, because the ids are
    /// positional: two entries with one name would give one plugin two ids on
    /// the client and one on the server.
    /// </summary>
    [Fact]
    public void DuplicatesAreRefused()
    {
        var error = Assert.Throws<ArgumentException>(
            () => PluginNames.ParseList("chat_v2_pr,chat_v2_pr"));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnInvalidNameIsRefusedRatherThanAdvertised()
    {
        var error = Assert.Throws<ArgumentException>(() => PluginNames.ParseList("Chat_V2"));
        Assert.Contains("[a-z0-9_]+", error.Message, StringComparison.Ordinal);
    }

    /// <summary><c>game_init.gml:99</c>, transcribed.</summary>
    [Fact]
    public void AListOverTwoHundredAndFiftyFourCharactersIsRefused()
    {
        var tooLong = string.Join(',', Enumerable.Range(0, 40).Select(i => $"plugin_{i:00}"));
        Assert.True(tooLong.Length > PluginNames.MaxConfiguredLength);

        Assert.Throws<ArgumentException>(() => PluginNames.ParseList(tooLong));
    }

    [Fact]
    public void APinnedHashIsParsedAndLowercased()
    {
        var specs = PluginNames.ParseList($"chat_v2_pr@{ChatMd5.ToUpperInvariant()}");

        Assert.Equal("chat_v2_pr", specs[0].Name);
        Assert.Equal(ChatMd5, specs[0].Md5);
    }

    [Fact]
    public void APinThatIsNotThirtyTwoHexDigitsIsRefused()
    {
        Assert.Throws<ArgumentException>(() => PluginNames.ParseList("chat_v2_pr@nope"));
        Assert.Throws<ArgumentException>(() => PluginNames.ParseList("chat_v2_pr@" + new string('g', 32)));
    }

    // --- the advertised string ----------------------------------------------

    /// <summary>
    /// <c>getpluginhashes.gml:79-83</c> builds <c>name@hash</c> joined by bare
    /// commas.
    /// </summary>
    /// <remarks>
    /// No space after the comma. The client splits on <c>,</c> and uses each
    /// piece as a name, so " chat_v2_pr" would be downloaded as a plugin called
    /// " chat_v2_pr" and 404.
    /// </remarks>
    [Fact]
    public void TheAdvertisedStringHasNoSpaces()
    {
        var advertised = PluginNames.Advertise(
        [
            new LoadedPlugin("chat_v2_pr", ChatMd5, null),
            new LoadedPlugin("other", new string('a', 32), null),
        ]);

        Assert.Equal($"chat_v2_pr@{ChatMd5},other@{new string('a', 32)}", advertised);
    }

    // --- hash resolution -----------------------------------------------------

    /// <summary>
    /// The divergence from <c>getpluginhashes.gml</c>, and the reason for it.
    /// </summary>
    /// <remarks>
    /// The reference always asks the site. This port prefers the hash its C#
    /// implementation was written against, because the server half here is C#
    /// pinned to one version of the plugin's protocol while the client half is
    /// still the site's GML — so fetching "latest" would hand the clients a
    /// plugin the server may no longer agree with.
    /// <para>
    /// <b>This test is also what stops the pin going stale silently.</b> If
    /// the catalogue's hash is ever changed, this fails and names it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnImplementedPluginUsesItsPinnedHashAndNeverTouchesTheNetwork()
    {
        var log = new RecordingLog();
        var loaded = PluginHashes.Resolve(
            PluginNames.ParseList("chat_v2_pr"), new RefusingHashes(), log, NoDebugDirectory());

        Assert.Equal(ChatMd5, loaded[0].Md5);
        Assert.NotNull(loaded[0].Implementation);
    }

    /// <summary>
    /// The same for <c>chat</c>, which is a separate plugin with a separate
    /// hash rather than a newer <c>chat_v2_pr</c>.
    /// </summary>
    [Fact]
    public void TheOtherChatPluginHasItsOwnPin()
    {
        var log = new RecordingLog();
        var loaded = PluginHashes.Resolve(
            PluginNames.ParseList("chat"), new RefusingHashes(), log, NoDebugDirectory());

        Assert.Equal(ChatV6Md5, loaded[0].Md5);
        Assert.NotNull(loaded[0].Implementation);
        Assert.NotEqual(ChatMd5, ChatV6Md5);
    }

    [Fact]
    public void AnUnimplementedPluginIsFetchedAndFlagged()
    {
        var log = new RecordingLog();
        var source = new FixedHashes(new string('b', 32));

        var loaded = PluginHashes.Resolve(
            PluginNames.ParseList("some_other_plugin"), source, log, NoDebugDirectory());

        Assert.Equal(["some_other_plugin"], source.Fetched);
        Assert.Null(loaded[0].Implementation);
        Assert.True(
            log.Contains("no server-side implementation"),
            "an unimplemented plugin must be called out, or its packets vanish silently");
    }

    /// <summary>
    /// A pin the operator wrote wins, and the mismatch against the
    /// implementation's own pin is reported rather than swallowed.
    /// </summary>
    /// <remarks>
    /// This is the one condition under which the two halves of a plugin are
    /// running different protocols, and nothing else in the system would notice
    /// — the packets have no version field.
    /// </remarks>
    [Fact]
    public void AnOperatorsPinWinsAndTheMismatchIsLogged()
    {
        var log = new RecordingLog();
        var other = new string('c', 32);

        var loaded = PluginHashes.Resolve(
            PluginNames.ParseList($"chat_v2_pr@{other}"), new RefusingHashes(), log, NoDebugDirectory());

        Assert.Equal(other, loaded[0].Md5);
        Assert.True(
            log.Contains("was written against"),
            "a hash that disagrees with the implementation's pin must be reported");
    }

    /// <summary>
    /// <c>getpluginhashes.gml:43-47</c>: a local <c>ServerPluginsDebug</c> zip is
    /// hashed instead of the site's, and the operator is warned that every client
    /// needs the same file.
    /// </summary>
    [Fact]
    public void ADebugZipIsHashedLocallyAndWarnedAbout()
    {
        var directory = Directory.CreateTempSubdirectory("gg2-plugin-debug").FullName;
        try
        {
            var zip = Path.Combine(directory, "some_other_plugin.zip");
            File.WriteAllBytes(zip, [1, 2, 3, 4]);

            var log = new RecordingLog();
            var loaded = PluginHashes.Resolve(
                PluginNames.ParseList("some_other_plugin"), new RefusingHashes(), log, directory);

            Assert.Equal(PluginHashes.Md5Of(zip), loaded[0].Md5);
            Assert.True(log.Contains("ServerPluginsDebug") || log.Contains(zip),
                "the debug override must name the file it came from");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --- the per-player table ------------------------------------------------

    /// <summary>
    /// The trap <see cref="PluginPlayerTable{T}"/> exists for: a removal
    /// COMPACTS, so state indexed by slot has to move with it.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is not a crash. It is a mute, a name or a chat
    /// flag landing on an unrelated player some seconds after a disconnect
    /// nobody connected it to — the same shape as the
    /// <c>WorldState.RemoveSlot</c> trap one layer down.
    /// </remarks>
    [Fact]
    public void RemovingASlotCompactsTheTable()
    {
        var next = 0;
        var table = new PluginPlayerTable<int>(() => next++);

        table.Add(0);
        table.Add(1);
        table.Add(2);
        Assert.Equal([0, 1, 2], new[] { table[0], table[1], table[2] });

        table.RemoveAt(1);

        Assert.Equal(2, table.Count);
        Assert.Equal(0, table[0]);
        Assert.Equal(2, table[1]);   // slot 2's state, now at slot 1
        Assert.False(table.Has(2));
    }

    [Fact]
    public void AddingOutOfOrderIsRefusedRatherThanSilentlyMisaligning()
    {
        var table = new PluginPlayerTable<int>(() => 0);
        table.Add(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Add(2));
    }

    /// <summary>A directory guaranteed not to hold a debug zip.</summary>
    private static string NoDebugDirectory() =>
        Path.Combine(Path.GetTempPath(), "gg2-no-such-plugin-debug-directory");
}
