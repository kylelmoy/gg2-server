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

    public ServerHarness(Action<ServerOptions>? configure = null)
    {
        ReferenceTree.RequireAvailable();

        Options = new ServerOptions
        {
            Port = 0, // any free port
            ServerName = "test server",
            Map = "ctf_truefort",
            MapDirectory = ReferenceTree.IncludedFiles,
        };

        configure?.Invoke(Options);

        Server = new GameServer(Options, Log);
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
    public LoopbackClient JoinClient(string name)
    {
        var client = Connect();

        client.SendHello();
        TickUntil(() => client.Received.Count > 0, "the server greeting");

        Assert.Equal(Gg2.Protocol.MessageType.Hello, client.TakeMessageType());
        Assert.Equal(Options.ServerName, client.TakeString1());
        Assert.Equal(Options.Map, client.TakeString1());
        Assert.Equal(string.Empty, client.TakeString1()); // built-in map: no MD5
        Assert.Equal(0, client.TakeByte());               // pluginsRequired
        Assert.Equal(string.Empty, client.TakeString2()); // pluginList

        client.SendReserveSlot(name);
        TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(Gg2.Protocol.MessageType.ReserveSlot, client.TakeMessageType());

        var before = Server.PlayerCount;
        client.SendPlayerJoin();
        TickUntil(() => Server.PlayerCount > before, "the join to complete");

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
public sealed class RecordingLog : IServerLog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public void Info(string message) => _lines.Add(message);

    public bool Contains(string fragment) =>
        _lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));
}
