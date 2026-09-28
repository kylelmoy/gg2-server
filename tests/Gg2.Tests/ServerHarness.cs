using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// A <c>GameServer</c> on a loopback port, driven by explicit ticks rather than
/// by a clock.
/// </summary>
/// <remarks>
/// No sleeping, no wall-clock waits, no flakiness: a test says how many ticks
/// pass. This is the same property the simulation will have for a much better
/// reason, and it is worth having here first.
/// </remarks>
public sealed class ServerHarness : IDisposable
{
    private readonly List<LoopbackClient> _clients = [];

    public ServerHarness(Action<ServerOptions>? configure = null, Action<GameServer>? configureServer = null)
    {
        Options = new ServerOptions
        {
            Port = 0, // any free port
            ServerName = "test server",
            Map = "ctf_truefort",
            MapDirectory = ReferenceTree.MapsDirectory,
        };

        configure?.Invoke(Options);

        Server = new GameServer(Options, Log);

        // Before Start(), which is where plugin hashes are resolved. A test
        // that let that fall through to the default source would be reaching
        // ganggarrison.com over HTTP from a unit test.
        configureServer?.Invoke(Server);

        Server.Start();
    }

    public ServerOptions Options { get; }

    public GameServer Server { get; }

    public RecordingLog Log { get; } = new();

    public int Port => Server.BoundPort;

    /// <summary>Connects a client and returns it. Disposed with the harness.</summary>
    public LoopbackClient Connect()
    {
        var client = new LoopbackClient(Port);
        _clients.Add(client);
        return client;
    }

    /// <summary>Runs <paramref name="count"/> ticks, polling every client after each.</summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Server.Tick();
            foreach (var client in _clients)
                client.Poll();
        }
    }

    /// <summary>
    /// Ticks until <paramref name="condition"/> holds, up to
    /// <paramref name="maxTicks"/>. Fails with <paramref name="description"/>
    /// rather than hanging.
    /// </summary>
    public void TickUntil(Func<bool> condition, string description, int maxTicks = 200)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (condition())
                return;
            Tick();
        }

        Assert.Fail($"gave up after {maxTicks} ticks waiting for {description}");
    }

    /// <summary>Completes the whole handshake for a new client and returns it.</summary>
    /// <summary>Roster entries that are neither a bot nor the server's own slot 0.</summary>
    private int HumanRosterCount => Server.RosterCount - Server.BotSlots.Count - 1;

    public LoopbackClient JoinClient(string name)
    {
        var client = Connect();

        client.SendHello();
        TickUntil(() => client.Received.Count > 0, "the server greeting");

        Assert.Equal(Gg2.Protocol.MessageType.Hello, client.TakeMessageType());
        Assert.Equal(Options.ServerName, client.TakeString1());
        Assert.Equal(Options.Map, client.TakeString1());
        Assert.Equal(string.Empty, client.TakeString1()); // built-in map: no MD5
        // Read from the server rather than hardcoded to "no plugins". These
        // two fields sit between the map hash and everything after it, so a
        // harness that assumed them would mis-frame every later assertion the
        // moment a test configured a plugin list.
        Assert.Equal(Options.ServerPluginsRequired ? (byte)1 : (byte)0, client.TakeByte());
        Assert.Equal(Server.AdvertisedPlugins, client.TakeString2());

        client.SendReserveSlot(name);
        TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(Gg2.Protocol.MessageType.ReserveSlot, client.TakeMessageType());

        // Humans ON THE ROSTER, which is neither of the two obvious counts.
        // PlayerCount includes bots, so on a server with the population manager
        // running it moves for reasons that have nothing to do with this join — a
        // bot displaced by the joining human puts it straight back where it
        // started. And HumanCount is getNumberOfOccupiedSlots, which counts a
        // joiner that has merely RESERVED a slot: it went up on the reservation a
        // few lines above and does not move again here.
        var before = HumanRosterCount;
        client.SendPlayerJoin();
        TickUntil(() => HumanRosterCount > before, "the join to complete");

        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Dispose();

        Server.Dispose();
    }
}

/// <summary>Keeps the server's log lines so a test can assert on them.</summary>
/// <remarks>
/// ⚠️ <b><see cref="Lines"/> is every level flattened, deliberately.</b> Dozens
/// of existing tests assert on it, and re-levelling a line should not make one
/// of them stop seeing a message it still expects. A test that cares about
/// severity asks <see cref="Warnings"/> or <see cref="Errors"/>, which are the
/// same lines filtered rather than a second recording.
/// </remarks>
public sealed class RecordingLog : IServerLog
{
    private readonly List<(LogLevel Level, string Text)> _lines = [];

    public IReadOnlyList<string> Lines => _lines.Select(line => line.Text).ToList();

    public IReadOnlyList<string> Warnings => AtLeast(LogLevel.Warning);

    public IReadOnlyList<string> Errors => AtLeast(LogLevel.Error);

    /// <summary>The exceptions handed to <see cref="Error"/>, in order.</summary>
    public List<Exception> Exceptions { get; } = [];

    public void Info(string message) => _lines.Add((LogLevel.Info, message));

    public void Warn(string message) => _lines.Add((LogLevel.Warning, message));

    public void Error(string message, Exception? error = null)
    {
        _lines.Add((LogLevel.Error, message));

        if (error is not null)
            Exceptions.Add(error);
    }

    public bool Contains(string fragment) =>
        _lines.Any(line => line.Text.Contains(fragment, StringComparison.Ordinal));

    private IReadOnlyList<string> AtLeast(LogLevel level) =>
        _lines.Where(line => line.Level >= level).Select(line => line.Text).ToList();
}
