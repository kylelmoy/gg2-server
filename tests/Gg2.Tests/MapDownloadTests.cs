using System.Security.Cryptography;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Serving a custom map to a client that does not have it.
/// </summary>
/// <remarks>
/// <c>serviceJoiningPlayer.gml:131-144</c> (the answer) and <c>:8-24</c> (the
/// rate-limited pump).
/// </remarks>
public sealed class MapDownloadTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gg2-mapdownload-" + Guid.NewGuid().ToString("n"));

    public MapDownloadTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// 32 lowercase hex characters, settled from the DLL's own source.
    /// </summary>
    /// <remarks>
    /// <c>CustomMapGetMapMD5</c> calls the compiled <c>GG2DLL_compute_MD5</c>,
    /// so the format could not be read off the GML — but the extension ships its
    /// source at <c>Extensions/GG2DLL/GG2DLL/GG2DLL.cpp:485-516</c>, which
    /// formats with <c>sprintf("%02x")</c>. Uppercase would compare unequal on
    /// every client, and the symptom would be a download that completes and is
    /// then rejected.
    /// </remarks>
    [Fact]
    public void TheMd5IsLowercaseHexOfTheWholeFile()
    {
        var bytes = "the quick brown fox"u8.ToArray();

        var md5 = MapDownload.Md5Of(bytes);

        Assert.Equal(32, md5.Length);
        Assert.Equal(md5.ToLowerInvariant(), md5);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes)), md5);
    }

    /// <summary>The 22 built-in names, and the five aliases among them.</summary>
    /// <remarks>
    /// The aliases are the part worth testing. Treating <c>dkoth_60s</c> as a
    /// custom map would make every client download a file it already has, under a
    /// name it then cannot find.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort", "ctf_truefort.png")]
    [InlineData("ctf_2dfort2", "ctf_2dfort.png")]
    [InlineData("ctf_2dfortremix", "ctf_2dfort.png")]
    [InlineData("dkoth_60s", "dkoth_sixties.png")]
    [InlineData("ctf_2dfort1", "ctf_oldfort.png")]
    [InlineData("ctf_2dfort[0]", "ctf_oldfort.png")]
    public void AnInternalNameResolvesToItsFile(string name, string file)
    {
        Assert.True(InternalMaps.IsInternal(name));
        Assert.Equal(file, InternalMaps.FileFor(name));
    }

    [Fact]
    public void AnUnknownNameIsNotInternal()
    {
        Assert.False(InternalMaps.IsInternal("ctf_somebodys_custom_map"));
        Assert.Null(InternalMaps.FileFor("ctf_somebodys_custom_map"));
    }

    /// <summary>
    /// Every shipped map is an internal name, so none of them is ever offered for
    /// download.
    /// </summary>
    /// <remarks>
    /// The two lists are independent — one is the client's compiled-in switch,
    /// the other is what is on disk — and a mismatch is silent. A shipped map
    /// missing from <see cref="InternalMaps"/> would be advertised with an MD5 and
    /// downloaded by every client that already had it.
    /// </remarks>
    [Fact]
    public void EveryShippedMapIsInternal()
    {
        ReferenceTree.RequireAvailable();

        var notInternal = Directory
            .GetFiles(ReferenceTree.IncludedFiles, "*.png")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Where(name => !InternalMaps.IsInternal(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(notInternal);
    }

    /// <summary>A built-in map advertises no MD5, so the download path stays shut.</summary>
    [Fact]
    public void ABuiltInMapAdvertisesNoMd5()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        client.TakeString1();                        // server name
        Assert.Equal("ctf_truefort", client.TakeString1());
        Assert.Equal(string.Empty, client.TakeString1());
    }

    /// <summary>A custom map advertises its hash and then serves the bytes.</summary>
    /// <remarks>
    /// End to end over a real loopback socket: greet, ask, read the uint length,
    /// then collect chunks until the whole file has arrived and check its MD5
    /// against what was advertised. That last comparison is the one the client
    /// makes, and it is the reason the hash format had to be settled exactly.
    /// </remarks>
    [Fact]
    public void ACustomMapIsAdvertisedAndServed()
    {
        var map = CustomMap("ctf_testmap");

        using var harness = new ServerHarness(options =>
        {
            options.MapDirectory = _directory;
            options.Map = "ctf_testmap";
        });

        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        client.TakeString1();
        Assert.Equal("ctf_testmap", client.TakeString1());

        var advertised = client.TakeString1();
        Assert.Equal(MapDownload.Md5Of(map), advertised);

        Assert.Equal(0, client.TakeByte());          // pluginsRequired
        client.TakeString2();                        // plugin list

        client.SendCommand(MessageType.DownloadMap);
        harness.TickUntil(() => client.Received.Count >= 4, "the download length");

        var length = client.TakeUInt();
        Assert.Equal((uint)map.Length, length);

        harness.TickUntil(
            () => client.Received.Count >= (int)length,
            "the whole map",
            maxTicks: 2000);

        var served = client.Take((int)length);

        Assert.Equal(map, served);
        Assert.Equal(advertised, MapDownload.Md5Of(served));
    }

    /// <summary>
    /// The JOIN BURST's <c>CHANGE_MAP</c> carries the same hash the greeting
    /// advertised — on a custom map, a non-empty one.
    /// </summary>
    /// <remarks>
    /// <b>This is the message that makes a joining client load the map</b>
    /// (<c>ServerJoinUpdate.gml:8</c>: <c>ServerChangeMap(global.currentMap,
    /// global.currentMapMD5, socket)</c>), and it is NOT the greeting — the two
    /// are separate messages carrying the same pair.
    /// <para>
    /// ⛔ <b>An empty MD5 is not "no hash handy", it means INTERNAL MAP.</b>
    /// <c>ClientBeginStep.gml:476</c> branches on <c>currentMapMD5 == ""</c> and
    /// calls <c>findInternalMapName</c>; a custom map is not in that list, so the
    /// client shows <c>"Server went to invalid internal map: &lt;name&gt;#Exiting."</c>
    /// and drops to the menu. It can never join.
    /// </para>
    /// <para>
    /// ⚠️ <b>The port hardcoded <c>string.Empty</c> here until 2026-09-05</b>, so
    /// every custom map was unjoinable by a stock client — measured live on
    /// <c>gg_debug2</c>, two clients, both killed on arrival with that exact
    /// dialog. Every test in this file passed throughout, because they all check
    /// the <em>greeting</em>, which was always right. Nothing read the burst.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheJoinBurstCarriesTheMapHashAndNotAnEmptyOne()
    {
        var map = CustomMap("ctf_joinhash");

        using var harness = new ServerHarness(options =>
        {
            options.MapDirectory = _directory;
            options.Map = "ctf_joinhash";
            options.MapRotation = ["ctf_joinhash"];
        });

        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        client.TakeString1();                                   // server name
        Assert.Equal("ctf_joinhash", client.TakeString1());
        var advertised = client.TakeString1();
        Assert.Equal(MapDownload.Md5Of(map), advertised);
        Assert.NotEqual(string.Empty, advertised);
        client.TakeByte();                                      // pluginsRequired
        client.TakeString2();                                   // plugin list

        client.SendReserveSlot("joiner");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, client.TakeMessageType());

        var before = harness.Server.RosterCount;
        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.RosterCount > before, "the join to complete");

        // The burst opens with JOIN_UPDATE, then the CHANGE_MAP under test.
        Assert.Equal(MessageType.JoinUpdate, client.TakeMessageType());
        client.TakeByte();                                      // player id
        client.TakeByte();                                      // map area

        Assert.Equal(MessageType.ChangeMap, client.TakeMessageType());
        Assert.Equal("ctf_joinhash", client.TakeString1());
        Assert.Equal(advertised, client.TakeString1());
    }

    /// <summary>
    /// And on a BUILT-IN map the same burst carries an empty hash, because there
    /// the empty string is the correct answer.
    /// </summary>
    /// <remarks>
    /// The control for the test above: without it, "carries the hash" would be
    /// satisfied by a server that always sent something non-empty, which would
    /// send every client off to download a map it already has — under a name
    /// <c>findInternalMapName</c> would then resolve, so it would fail as a
    /// pointless transfer rather than loudly.
    /// </remarks>
    [Fact]
    public void TheJoinBurstOnABuiltInMapCarriesAnEmptyHash()
    {
        using var harness = new ServerHarness();
        var client = harness.JoinClient("joiner");

        Assert.Equal(MessageType.JoinUpdate, client.TakeMessageType());
        client.TakeByte();
        client.TakeByte();

        Assert.Equal(MessageType.ChangeMap, client.TakeMessageType());
        Assert.Equal("ctf_truefort", client.TakeString1());
        Assert.Equal(string.Empty, client.TakeString1());
    }

    /// <summary>
    /// The transfer is rate-limited, and the limit is real rather than
    /// nominal.
    /// </summary>
    /// <remarks>
    /// At the default 50000 bytes a second over a 30 Hz tick, one client gets
    /// about 1666 bytes a tick — so a file several times that cannot arrive in
    /// one tick. A pump that ignored the budget would pass every other test in
    /// this file and quietly let one joining client saturate the uplink.
    /// </remarks>
    [Fact]
    public void TheTransferIsSpreadAcrossTicks()
    {
        CustomMap("ctf_bigmap", size: 40_000);

        using var harness = new ServerHarness(options =>
        {
            options.MapDirectory = _directory;
            options.Map = "ctf_bigmap";
        });

        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.Take(client.Received.Count);

        client.SendCommand(MessageType.DownloadMap);
        harness.TickUntil(() => client.Received.Count >= 4, "the download length");
        client.TakeUInt();

        // One further tick must not deliver the whole 40 KB.
        harness.Tick();

        Assert.True(
            client.Received.Count < 40_000,
            $"a single tick delivered {client.Received.Count} bytes, so the budget is not being applied");
    }

    /// <summary>
    /// Asking to download a built-in map ends the connection, as the reference's
    /// fall-through does.
    /// </summary>
    [Fact]
    public void AskingToDownloadABuiltInMapDropsTheConnection()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");

        client.SendCommand(MessageType.DownloadMap);
        harness.TickUntil(() => client.IsClosed, "the disconnect");

        Assert.True(client.IsClosed);
    }

    /// <summary>Writes a fake custom map and returns its bytes.</summary>
    /// <remarks>
    /// Not a real PNG, and it does not need to be: nothing on the download
    /// path parses the file. It is opened as a map only when the server loads it,
    /// which is why the two tests that serve one never let the server pick it up
    /// as the level — <c>ServerHarness</c> would throw on a malformed PNG. The
    /// map that IS loaded is a copy of a real one under a custom name.
    /// </remarks>
    private byte[] CustomMap(string name, int size = 0)
    {
        var source = ReferenceTree.ReadMap("ctf_truefort");
        var bytes = size == 0 ? source : Pad(source, size);

        File.WriteAllBytes(Path.Combine(_directory, name + ".png"), bytes);
        return bytes;
    }

    /// <summary>
    /// A real map with trailing bytes, so it still loads but is a chosen size.
    /// </summary>
    /// <remarks>
    /// A PNG reader stops at IEND, so bytes after it are ignored by the loader
    /// and counted by the download.
    /// </remarks>
    private static byte[] Pad(byte[] png, int size)
    {
        if (size <= png.Length)
            return png;

        var padded = new byte[size];
        png.CopyTo(padded, 0);
        return padded;
    }
}
