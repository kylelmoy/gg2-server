using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// Phase 8's last item: a loopback control surface over the running server.
/// </summary>
/// <remarks>
/// <para>
/// The two claims worth gating are structural rather than functional.
/// <b>A freeze must not stop the network</b> — a frozen server that stopped
/// servicing sockets would drop the clients it was frozen to inspect, and would
/// stop answering the port that froze it, which reads as a hang. And <b>the
/// port must not be able to do anything a client cannot</b>, which is kept true
/// by routing every client-issuable action through the same <c>Execute</c> the
/// socket switch and the bots reach.
/// </para>
/// </remarks>
public sealed class ControlPortTests : IDisposable
{
    private readonly ServerHarness _harness;
    private readonly TcpClient _control;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    public ControlPortTests()
    {
        _harness = new ServerHarness(o =>
        {
            // Port 0: an ephemeral port, read back from the server. A
            // hard-coded one makes the suite un-parallelisable and the failure
            // intermittent.
            o.ControlPort = 0;
            o.ControlPortEnabled = true;
        });

        // The listener is opened in Start(); one tick services the first accept.
        _harness.Tick();

        _control = new TcpClient();
        _control.Connect(IPAddress.Loopback, _harness.Server.ControlBoundPort);

        var stream = _control.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _control.Dispose();
        _harness.Dispose();
    }

    /// <summary>
    /// Sends one command and ticks until its reply arrives.
    /// </summary>
    /// <remarks>
    /// The tick is not incidental: the port is serviced from inside
    /// <c>GameServer.Tick</c>, on the tick thread, so a command sent to a server
    /// nobody is ticking is never read. That is the design — no locks, and every
    /// command lands between two ticks rather than inside one.
    /// </remarks>
    private JsonElement Send(string json)
    {
        _writer.Write(json + "\n");

        for (var attempt = 0; attempt < 200; attempt++)
        {
            _harness.Tick();

            if (_control.Available > 0)
                return JsonDocument.Parse(_reader.ReadLine()!).RootElement;
        }

        Assert.Fail($"no reply to {json}");
        return default;
    }

    private static bool Ok(JsonElement reply) =>
        reply.TryGetProperty("ok", out var ok) && ok.GetBoolean();

    [Fact]
    public void ItAnswersAPing()
    {
        var reply = Send("""{"cmd":"ping"}""");

        Assert.True(Ok(reply));
        Assert.True(reply.GetProperty("frame").GetInt64() > 0);
    }

    [Fact]
    public void AnUnknownCommandIsAnErrorRatherThanASilence()
    {
        var reply = Send("""{"cmd":"nonsense"}""");

        Assert.False(Ok(reply));
        Assert.Contains("nonsense", reply.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>Malformed input does not take the port, or the server, down.</summary>
    [Fact]
    public void RubbishIsRefusedAndTheConnectionSurvives()
    {
        var bad = Send("this is not json");
        Assert.False(Ok(bad));

        var good = Send("""{"cmd":"ping"}""");
        Assert.True(Ok(good));
    }

    /// <summary>
    /// The load-bearing one: a freeze stops the simulation and leaves the
    /// network running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame counter keeps advancing — that is the tick, which still
    /// services sockets, still broadcasts and still answers this very command —
    /// while the simulation does not. If freezing skipped the whole tick the
    /// server would stop reading its clients, and stop reading the control port,
    /// so <c>resume</c> could never arrive: the freeze would be permanent and
    /// would look like a hang.
    /// </para>
    /// <para>
    /// The mutation this catches: freeze by skipping <c>Tick()</c> in
    /// <c>Run</c> rather than skipping <c>AdvanceSimulation()</c> inside it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFreezeStopsTheSimulationAndNotTheNetwork()
    {
        Assert.True(Ok(Send("""{"cmd":"freeze"}""")));

        var before = _harness.Server.Frame;
        var roundBefore = RoundTimer();

        _harness.Tick(30);

        // The tick is still running: frames advance, the port still answers.
        Assert.True(_harness.Server.Frame > before);
        Assert.True(Ok(Send("""{"cmd":"ping"}""")));

        // The simulation is not: the round clock has not moved.
        Assert.Equal(roundBefore, RoundTimer());

        Assert.True(Ok(Send("""{"cmd":"resume"}""")));
        _harness.Tick(30);

        Assert.NotEqual(roundBefore, RoundTimer());
    }

    /// <summary>
    /// And a frozen server still accepts and serves CLIENTS — which is the
    /// half of "the network keeps running" that the round clock cannot see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written because the first version of the freeze test did not catch
    /// the mutation it claimed to.</b> Moving the freeze check to the top of
    /// <c>Tick</c> and returning early still let the control port answer — the
    /// port is serviced before the simulation — so the round clock froze, the
    /// port replied, and every assertion passed. What the early return actually
    /// skipped was <c>AcceptConnections</c>, <c>ServiceJoiningConnections</c>
    /// and the per-client flush: a frozen server that quietly stops accepting
    /// anybody and stops flushing to the clients it already has.
    /// </para>
    /// <para>
    /// A whole join is the sharpest thing to ask for, because it needs the
    /// accept, the joining state machine and the flush all to still be running.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFrozenServerStillAcceptsAndServesClients()
    {
        Assert.True(Ok(Send("""{"cmd":"freeze"}""")));

        var before = _harness.Server.RosterCount;

        // JoinClient drives the whole handshake and asserts each step, so it
        // fails outright if the accept or the flush has stopped.
        var client = _harness.JoinClient("joined-while-frozen");

        Assert.Equal(before + 1, _harness.Server.RosterCount);

        // And the port that froze it is still answering afterwards.
        Assert.True(Ok(Send("""{"cmd":"ping"}""")));
    }

    /// <summary>
    /// <c>step</c> advances exactly the number of simulation ticks asked for and
    /// leaves the server frozen.
    /// </summary>
    /// <remarks>
    /// It freezes first rather than refusing on a running server: a step
    /// against a server still ticking would advance an unknowable number of
    /// times, which is the opposite of what stepping is for.
    /// </remarks>
    [Fact]
    public void StepAdvancesExactlyThatManySimulationTicks()
    {
        // Freeze and read the clock FIRST. RoundTimer() goes through the
        // port, which means it ticks the server — so reading it after the step
        // spends part of the budget being measured, and the first version of
        // this test was off by one for exactly that reason.
        Assert.True(Ok(Send("""{"cmd":"freeze"}""")));
        var before = RoundTimer();

        Assert.True(Ok(Send("""{"cmd":"step","ticks":5}""")));

        // Well more than five ticks of wall time; only five should land.
        _harness.Tick(60);

        Assert.Equal(before - 5, RoundTimer());
        Assert.True(_harness.Server.IsFrozen);

        // And it stays put afterwards.
        var after = RoundTimer();
        _harness.Tick(30);
        Assert.Equal(after, RoundTimer());
    }

    /// <summary><c>state</c> reports the roster and the exact positions.</summary>
    [Fact]
    public void StateReportsTheRoster()
    {
        var client = _harness.JoinClient("someone");
        client.SendChangeTeam(Team.Red);
        _harness.Tick(5);

        var reply = Send("""{"cmd":"state"}""");

        Assert.True(Ok(reply));
        Assert.Equal(_harness.Server.CurrentMap, reply.GetProperty("map").GetString());
        Assert.False(reply.GetProperty("frozen").GetBoolean());

        // Which build is answering. A deploy that copied binaries and restarted
        // is otherwise unverifiable from off the host — `systemctl is-active`
        // says a process is running, not which one. BuildInfoTests covers the
        // format; this covers it reaching the wire the panel and the control
        // port both read.
        var server = reply.GetProperty("server");
        Assert.Equal(BuildInfo.Commit, server.GetProperty("commit").GetString());
        Assert.Equal(BuildInfo.Version, server.GetProperty("version").GetString());

        var players = reply.GetProperty("players");
        Assert.Equal(_harness.Server.RosterCount, players.GetArrayLength());

        var joined = players[players.GetArrayLength() - 1];
        Assert.Equal("Red", joined.GetProperty("team").GetString());
        Assert.True(joined.GetProperty("alive").GetBoolean());
        Assert.True(joined.TryGetProperty("x", out _));
    }

    /// <summary>
    /// The other load-bearing one: a client-issuable action goes through the
    /// shared dispatch, so the port can do exactly what a client can and no more.
    /// </summary>
    /// <remarks>
    /// <c>command</c> submits a <see cref="GG2.Simulation.PlayerCommand"/> to
    /// the same <c>Execute</c> the socket switch and the bots reach — CLAUDE.md's
    /// "three places" rule, honoured by not adding a fourth. A control port that
    /// reimplemented team changes would drift from the socket path, and the
    /// divergence would only show up as a client and an operator getting
    /// different answers to the same request.
    /// </remarks>
    [Fact]
    public void AClientIssuableCommandGoesThroughTheSharedDispatch()
    {
        var client = _harness.JoinClient("someone");
        _harness.Tick();

        var slot = _harness.Server.RosterCount - 1;
        Assert.Equal(Team.Spectator, _harness.Server.TeamOf(slot));

        var reply = Send($$"""{"cmd":"command","slot":{{slot}},"kind":"ChangeTeam","argument":1}""");
        Assert.True(Ok(reply));

        _harness.Tick(3);

        // Blue is 1 on the wire, and the roster is what the socket path would
        // have written too.
        Assert.Equal(Team.Blue, _harness.Server.TeamOf(slot));
    }

    /// <summary>An unknown command kind is refused rather than silently doing nothing.</summary>
    [Fact]
    public void AnUnknownCommandKindIsRefused()
    {
        var reply = Send("""{"cmd":"command","slot":0,"kind":"Enrich"}""");

        Assert.False(Ok(reply));
    }

    /// <summary>Teleport moves a live character and refuses a slot with none.</summary>
    [Fact]
    public void TeleportMovesALiveCharacterAndRefusesAnEmptySlot()
    {
        var client = _harness.JoinClient("someone");
        client.SendChangeTeam(Team.Red);
        _harness.Tick(5);

        var slot = _harness.Server.RosterCount - 1;
        Assert.True(_harness.Server.IsAlive(slot));

        // Frozen, so the character cannot fall out of the assertion.
        Assert.True(Ok(Send("""{"cmd":"freeze"}""")));
        Assert.True(Ok(Send($$"""{"cmd":"teleport","slot":{{slot}},"x":1234.5,"y":600.25}""")));

        Assert.True(_harness.Server.TryGetExactPosition(slot, out var x, out var y, out _));
        Assert.Equal(1234.5, x);
        Assert.Equal(600.25, y);

        // Slot 0 is the server's own reserved player and never has a character.
        Assert.False(Ok(Send("""{"cmd":"teleport","slot":0,"x":1,"y":1}""")));
    }

    /// <summary>The speed can be changed on a running server, including to unpaced.</summary>
    /// <remarks>
    /// <c>GameServer.Run</c> re-reads the interval every tick for this. A
    /// captured one would make <c>--speed</c> a startup-only setting and this
    /// command a silent no-op.
    /// </remarks>
    [Fact]
    public void SpeedCanBeChangedAndAcceptsMax()
    {
        Assert.True(Ok(Send("""{"cmd":"speed","value":4}""")));
        Assert.Equal(TimeSpan.FromSeconds(1.0 / (30 * 4)), _harness.Server.TickInterval);

        Assert.True(Ok(Send("""{"cmd":"speed","value":"max"}""")));
        Assert.Equal(TimeSpan.Zero, _harness.Server.TickInterval);

        // And an impossible one is refused rather than wedging the loop.
        Assert.False(Ok(Send("""{"cmd":"speed","value":0}""")));
        Assert.False(Ok(Send("""{"cmd":"speed","value":"quickly"}""")));
    }

    /// <summary>A map change is refused for a map that does not exist.</summary>
    /// <remarks>
    /// The accepting case goes down the same path a finished round takes —
    /// <c>BeginMapChange</c>, the countdown and a <c>MAP_END</c> — so what a
    /// client sees is a sequence it already knows. <c>MapRotationTests</c> gates
    /// that path itself; this gates the refusal.
    /// </remarks>
    [Fact]
    public void AMapChangeIsRefusedForAMapThatDoesNotExist()
    {
        Assert.False(Ok(Send("""{"cmd":"map","name":"ctf_nowhere"}""")));
        Assert.True(Ok(Send("""{"cmd":"map","name":"koth_valley"}""")));

        // A second one while the first is still counting down is refused.
        Assert.False(Ok(Send("""{"cmd":"map","name":"ctf_2dfort"}""")));
    }

    /// <summary>Off unless it is asked for.</summary>
    /// <remarks>
    /// It executes commands against a live game, which is the same reason the
    /// GM8 bridge is off by default. A server that opened it because a port
    /// happened to default to zero would be one nobody meant to open.
    /// </remarks>
    [Fact]
    public void TheControlPortIsOffUnlessAskedFor()
    {
        using var harness = new ServerHarness();
        harness.Tick();

        Assert.Equal(0, harness.Server.ControlBoundPort);
    }

    /// <summary>
    /// The round clock, read <em>through the port</em> rather than off the
    /// server.
    /// </summary>
    /// <remarks>
    /// It is the cleanest "did the simulation advance" signal there is — a
    /// character standing still does not move, but the clock always runs — and
    /// reading it through <c>state</c> means these tests exercise the surface
    /// they are testing instead of reaching around it.
    /// </remarks>
    private int RoundTimer() => Send("""{"cmd":"state"}""").GetProperty("round").GetProperty("timer").GetInt32();
}
