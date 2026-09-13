using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Gg2.Protocol;

// A fleet of synthetic players that join, play, switch teams and classes, and
// leave, over and over, against a running server.
//
// WHY THIS RATHER THAN --bots
//
// A bot is an IInputSource with no socket. It never reaches RESERVE_SLOT, the
// join burst, ClientPlayer.SkipBroadcastBytes' flush ordering, or a disconnect
// driving PLAYER_LEAVE — so `soak-transitions.js --plan churn`, which moves the
// bot fill up and down, exercises roster compaction on the SERVER and nothing on
// the wire. These are real TCP clients speaking the real handshake, so a join and
// a departure cost what a player's costs.
//
// What it is for is CLAUDE.md's "roster indices are positional and shift on
// PLAYER_LEAVE ... off by one, intermittently". A departure from a LOW slot
// renumbers every slot above it, including a watching client's own, and the
// stock client tracks that with one line
// (ClientBeginStep.gml:218, `if(playerID < global.playerID) global.playerID -= 1`).
// Nothing in this repo can check that line is still right; a real client watching
// this fleet churn can.
//
// ⚠️ THIS TOOL IS NOT THE ORACLE. It deliberately does not parse the state
// stream: a second, unverified implementation of the wire would fail in its own
// bugs and report them as the port's. It completes the handshake, then DRAINS
// and discards — draining is not optional, because a client that stops reading
// backs the server's send buffer up until the connection dies for a reason that
// has nothing to do with what is being tested. The oracle is a stock GM8 client
// attached to the same server, watching
//
//     global.myself == ds_list_find_value(global.players, global.playerID)
//
// which is false the instant its own index slips, plus agentDebugDesync for the
// count mismatch. docs/TRAPS-LIVEGAME.md, "Soaking a stock client against the
// port".
//
// ⚠️ SERVER FLAGS ARE REQUIRED, and without them this looks broken rather than
// refused. Every member connects from 127.0.0.1, so two separate limits bite:
//
//     --multi-client-limit 0   concurrent connections one address may hold
//                              (default 3, and a watching GM8 client is one of them)
//     --connect-rate 0         connections one address may open per minute
//                              (default 10, and churn is nothing but reconnects)
//     --players <n>            seats: fleet + bots + the watching client
//
// The tally names whichever one it hit.

var host = Arg("--host") ?? "127.0.0.1";
var port = int.Parse(Arg("--port") ?? "8191");
var fleet = int.Parse(Arg("--players") ?? "4");
var seconds = int.Parse(Arg("--seconds") ?? "300");
var seed = int.Parse(Arg("--seed") ?? "1");
var prefix = Arg("--name-prefix") ?? "soak";
var logPath = Arg("--log");

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var started = Stopwatch.StartNew();
var tally = new Tally();
var logLock = new object();

void Log(string line)
{
    var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";

    lock (logLock)
    {
        Console.WriteLine(stamped);
        if (logPath is not null)
        {
            try { File.AppendAllText(logPath, stamped + Environment.NewLine); }
            catch (IOException) { /* best effort */ }
        }
    }
}

Log($"fleet start: {fleet} player(s) against {host}:{port}, {seconds}s, seed {seed}");

var members = new Task[fleet];
for (var i = 0; i < fleet; i++)
{
    var index = i;
    members[i] = Task.Run(() => Member(index));
}

await Task.WhenAll(members);

Log("--- fleet end ---");
Log(tally.ToString());

if (tally.Refused > 0 || tally.RateLimited > 0)
{
    Log("NOTE: refusals are almost always the per-address limits, not a server fault. "
        + "Run the server with --multi-client-limit 0 --connect-rate 0 and enough --players.");
}

return tally.HandshakeFailures > 0 ? 1 : 0;

// One synthetic player's whole life, repeated until the clock runs out. Each
// member has its own RNG derived from the batch seed, so a run is reproducible
// and two members never move in lockstep.
void Member(int index)
{
    var random = new Random(seed * 7919 + index);
    var name = $"{prefix}{index}";
    var lives = 0;

    while (started.Elapsed.TotalSeconds < seconds)
    {
        lives++;

        // Stagger the first connection so the fleet does not arrive as one burst,
        // which is the shape the rate limiter exists to refuse.
        if (lives == 1)
            Thread.Sleep(random.Next(0, 1500));

        try
        {
            Live(name, index, random);
        }
        catch (Exception e) when (e is SocketException or IOException or EndOfStreamException)
        {
            // A reset mid-life is one of the things being tested, not a tool
            // failure: it is what a player whose game crashed looks like.
            tally.Dropped++;
        }
        catch (ProtocolException e)
        {
            tally.HandshakeFailures++;
            Log($"{name} PROTOCOL {e.Message}");
        }

        // Off the roster for a moment, so the server spends time at a smaller
        // size rather than only ever at its ceiling.
        Thread.Sleep(random.Next(400, 2500));
    }
}

void Live(string name, int index, Random random)
{
    using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    sock.ReceiveTimeout = 15000;
    sock.SendTimeout = 15000;
    sock.Connect(host, port);

    var client = new Wire(sock);

    // HELLO + the 16 bytes of PROTOCOL_UUID, exactly 17.
    var hello = new byte[17];
    hello[0] = (byte)MessageType.Hello;
    GameConstants.ProtocolUuidBytes.CopyTo(hello.AsSpan(1));
    client.Send(hello);

    var greeting = (MessageType)client.U8();

    if (greeting == MessageType.PasswordRequest)
        throw new ProtocolException("the server wants a password; this tool does not send one");

    if (greeting == MessageType.IncompatibleProtocol)
        throw new ProtocolException("INCOMPATIBLE_PROTOCOL - GameConstants.ProtocolUuid disagrees with the server");

    if (greeting != MessageType.Hello)
        throw new ProtocolException($"expected HELLO, got {greeting} ({(int)greeting})");

    client.S1();  // server name
    client.S1();  // current map
    client.S1();  // map md5
    client.U8();  // plugins required
    client.S2();  // plugin list

    client.Send(Message(MessageType.ReserveSlot, w => w.WriteString1(name)));

    var reserved = (MessageType)client.U8();

    if (reserved == MessageType.ServerFull)
    {
        tally.Refused++;
        return;
    }

    if (reserved == MessageType.Kick)
    {
        // KICK_MULTI_CLIENT is the per-address concurrent limit.
        tally.RateLimited++;
        return;
    }

    if (reserved != MessageType.ReserveSlot)
        throw new ProtocolException($"expected RESERVE_SLOT, got {reserved} ({(int)reserved})");

    client.Send(Message(MessageType.PlayerJoin));
    tally.Joins++;

    // From here the server talks constantly and this client only listens. Drain
    // on its own thread so a slow action loop can never stall the socket.
    var draining = true;
    var drainer = new Thread(() =>
    {
        var scratch = new byte[1 << 16];

        while (Volatile.Read(ref draining))
        {
            try
            {
                if (sock.Receive(scratch, SocketFlags.None) == 0) break;
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }) { IsBackground = true };

    drainer.Start();

    // Pick a team on the way in, the way a player does. Spectator is in the mix
    // deliberately: a spectator has no character, and the roster still carries it.
    var team = random.Next(10) == 0 ? Team.Spectator : (random.Next(2) == 0 ? Team.Red : Team.Blue);
    client.Send(Message(MessageType.PlayerChangeTeam, w => w.WriteUByte((byte)team)));
    client.Send(Message(MessageType.PlayerChangeClass, w => w.WriteUByte((byte)RandomClass(random))));

    var life = TimeSpan.FromSeconds(random.Next(6, 45));
    var until = started.Elapsed + life;

    while (started.Elapsed < until && started.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(random.Next(150, 900));

        switch (random.Next(10))
        {
            case 0:
            case 1:
                client.Send(Message(MessageType.PlayerChangeTeam, w => w.WriteUByte((byte)RandomTeam(random))));
                tally.TeamChanges++;
                break;

            case 2:
                client.Send(Message(MessageType.PlayerChangeClass, w => w.WriteUByte((byte)RandomClass(random))));
                tally.ClassChanges++;
                break;

            case 3:
                // A relayed message with its own server-side throttle, so this
                // also walks ConnectionLimits' chat path.
                client.Send(Message(MessageType.ChatBubble, w => w.WriteUByte((byte)random.Next(0, 3))));
                break;

            default:
                // Ordinary play. Keys and aim only; the server simulates the rest.
                var keys = (PlayerKeys)random.Next(0, 64);
                client.Send(Message(MessageType.InputState,
                    w => new PlayerInput(keys, (ushort)random.Next(0, 65536), 0).Write(w)));
                break;
        }
    }

    // Two ways out, and both are worth exercising. A clean PLAYER_LEAVE is the
    // player who quits to the menu; dropping the socket without one is the player
    // whose game crashed, which the server sees as ConnectionReset.
    var polite = random.Next(100) < 70;

    Volatile.Write(ref draining, false);

    if (polite)
    {
        try { client.Send(Message(MessageType.PlayerLeave)); } catch (SocketException) { }
        tally.PoliteLeaves++;

        // FIN. The server logs "peer closed the connection".
        try { sock.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
    }
    else
    {
        tally.AbruptLeaves++;

        // ⚠️ A zero linger is what makes this abrupt. Closing a socket normally
        // sends a FIN, which the server reads as an orderly shutdown — the same
        // path as the polite branch, one message earlier. Only a RST produces
        // ConnectionReset, and that is a distinct path: it is what a crashed
        // game looks like, and it is the one that showed up for real while
        // soaking a boosted client. Without this the tool would claim to cover
        // two departures and cover one.
        try { sock.LingerState = new LingerOption(true, 0); } catch (SocketException) { }
    }
}

static Team RandomTeam(Random random) =>
    random.Next(10) == 0 ? Team.Spectator : (random.Next(2) == 0 ? Team.Red : Team.Blue);

// The ten the wire defines. A value outside it is ignored by the server
// (getCharacterObject() == -1), which is a different test.
static PlayerClass RandomClass(Random random) => (PlayerClass)random.Next(0, 10);

static byte[] Message(MessageType type, Action<NetWriter>? body = null)
{
    var writer = new NetWriter(64);
    writer.WriteMessageType(type);
    body?.Invoke(writer);
    return writer.WrittenSpan.ToArray();
}

/// <summary>Just enough framing to finish a handshake. Nothing parses the state stream.</summary>
file sealed class Wire(Socket socket)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _length;
    private int _position;

    public void Send(ReadOnlySpan<byte> bytes)
    {
        var sent = 0;
        while (sent < bytes.Length)
            sent += socket.Send(bytes[sent..], SocketFlags.None);
    }

    private void Need(int count)
    {
        while (_length - _position < count)
        {
            if (_length == _buffer.Length)
                throw new ProtocolException("handshake reply did not fit 64 KB");

            var read = socket.Receive(_buffer, _length, _buffer.Length - _length, SocketFlags.None);
            if (read == 0)
                throw new EndOfStreamException("server closed the connection during the handshake");

            _length += read;
        }
    }

    public byte U8()
    {
        Need(1);
        return _buffer[_position++];
    }

    public ushort U16()
    {
        Need(2);
        var value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
        _position += 2;
        return value;
    }

    public string S1() => Take(U8());

    public string S2() => Take(U16());

    private string Take(int count)
    {
        Need(count);
        var text = Encoding.Latin1.GetString(_buffer, _position, count);
        _position += count;
        return text;
    }
}

file sealed class Tally
{
    private int _joins, _refused, _rateLimited, _polite, _abrupt, _dropped, _handshake, _teams, _classes;

    public int Joins { get => _joins; set => _joins = value; }

    public int Refused { get => _refused; set => _refused = value; }

    public int RateLimited { get => _rateLimited; set => _rateLimited = value; }

    public int PoliteLeaves { get => _polite; set => _polite = value; }

    public int AbruptLeaves { get => _abrupt; set => _abrupt = value; }

    public int Dropped { get => _dropped; set => _dropped = value; }

    public int HandshakeFailures { get => _handshake; set => _handshake = value; }

    public int TeamChanges { get => _teams; set => _teams = value; }

    public int ClassChanges { get => _classes; set => _classes = value; }

    public override string ToString() =>
        $"joins={_joins} politeLeaves={_polite} abruptLeaves={_abrupt} dropped={_dropped} "
        + $"teamChanges={_teams} classChanges={_classes} "
        + $"refusedServerFull={_refused} refusedPerAddress={_rateLimited} handshakeFailures={_handshake}";
}
