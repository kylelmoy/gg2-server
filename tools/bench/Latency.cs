using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;

namespace Bench;

/// <summary>
/// Per-round-trip latency over the real socket, against the same work done in
/// process.
/// </summary>
/// <remarks>
/// <b>The question is latency, not throughput</b>, and the two are different
/// here. gg2-rl's training leaves the machine 91% idle — its env workers use 4%
/// of a core each — so what its loop is waiting on is round trips, not
/// simulation. A throughput benchmark cannot see that: it pipelines nothing and
/// measures a steady stream, where a trainer pays one full stall per step.
/// </remarks>
public static class LatencyBench
{
    public static void Run(string maps, string? nav, string map, int worlds, int rounds)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var noDelay = Environment.GetEnvironmentVariable("BENCH_NODELAY") == "1";

        var server = Task.Run(() =>
        {
            using var accepted = listener.AcceptTcpClient();

            accepted.NoDelay = noDelay;

            Stream stream = accepted.GetStream();

            // A read buffer means the body read is served from memory instead of
            // a second recv, because the peer coalesces its writes.
            if (Environment.GetEnvironmentVariable("BENCH_BUFFERED") == "1")
                stream = new BufferedStream(stream, 1 << 20);

            using (stream)
                new EnvServer(maps, nav).ServeOne(stream);
        });

        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.NoDelay = Environment.GetEnvironmentVariable("BENCH_NODELAY") == "1";

            Stream stream = client.GetStream();

            if (Environment.GetEnvironmentVariable("BENCH_BUFFERED") == "1")
                stream = new BufferedStream(stream, 1 << 20);

            using (stream)
                Drive(stream, map, worlds, rounds, client);
        }
        finally
        {
            listener.Stop();
            server.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private static void Drive(Stream stream, string map, int worlds, int rounds, TcpClient client)
    {
        Send(stream, EnvMessage.Hello, Int(EnvProtocol.Version));
        Receive(stream);

        Send(stream, EnvMessage.MakeBatch, MakeBatchBody(map, worlds));

        var made = Receive(stream);

        if (made[0] != (byte)EnvMessage.MakeBatch)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(made.AsSpan(1));
            throw new InvalidOperationException(
                "MakeBatch refused: " + System.Text.Encoding.UTF8.GetString(made, 5, len));
        }

        var actions = new byte[worlds * 2 * EnvProtocol.ActionBytes];
        var random = new Random(9);
        var samples = new double[rounds];

        for (var warm = 0; warm < 3000; warm++)
        {
            Fill(actions, random);
            Send(stream, EnvMessage.Step, actions);
            Receive(stream);
        }

        for (var r = 0; r < rounds; r++)
        {
            Fill(actions, random);

            var t0 = Stopwatch.GetTimestamp();

            Send(stream, EnvMessage.Step, actions);
            Receive(stream);

            samples[r] = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
        }

        Send(stream, EnvMessage.Close, []);
        Receive(stream);

        Array.Sort(samples);

        var mean = 0.0;
        foreach (var s in samples) mean += s;
        mean /= samples.Length;

        Console.WriteLine(
            $"socket round trip, {worlds} worlds, NoDelay={client.NoDelay}, {rounds:N0} timed");
        Console.WriteLine($"  mean   {mean,8:0.0} us");
        Console.WriteLine($"  p50    {samples[rounds / 2],8:0.0} us");
        Console.WriteLine($"  p90    {samples[(int)(rounds * 0.90)],8:0.0} us");
        Console.WriteLine($"  p99    {samples[(int)(rounds * 0.99)],8:0.0} us");
        Console.WriteLine($"  max    {samples[^1],8:0.0} us");

        var over1 = 0;
        var over5 = 0;
        var over10 = 0;
        foreach (var v in samples)
        {
            if (v > 1000) over1++;
            if (v > 5000) over5++;
            if (v > 10000) over10++;
        }

        Console.WriteLine($"  stalls >1ms {over1,5}  >5ms {over5,4}  >10ms {over10,4}   of {rounds:N0}");
    }

    private static void Fill(byte[] actions, Random random)
    {
        for (var i = 0; i < actions.Length; i += EnvProtocol.ActionBytes)
        {
            actions[i + 0] = (byte)random.Next(3);
            actions[i + 1] = (byte)random.Next(2);
            actions[i + 2] = (byte)random.Next(2);
            actions[i + 3] = (byte)random.Next(3);
            actions[i + 4] = (byte)random.Next(ActionDecoder.AimBins);
            actions[i + 5] = (byte)random.Next(2);
        }
    }

    private static byte[] Int(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] MakeBatchBody(string map, int worlds)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(map);
        var body = new byte[(4 * 2) + name.Length + EnvProtocol.MakeBatchFixedBytes];
        var at = 0;

        void I(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at), v);
            at += 4;
        }

        void R(double v)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(at), v);
            at += 8;
        }

        I(worlds);
        I(name.Length);
        name.CopyTo(body, at);
        at += name.Length;

        I((int)PlayerClass.Soldier);
        I((int)PlayerClass.Soldier);
        R(600); R(850);
        R(700); R(850);
        I(120);
        I(2);
        I(1234);
        I(1);
        I((int)EnvDriver.Client); I((int)EnvDriver.Client);
        I(0); I(0);
        I((int)RolloutOpponent.HoldLastInput); I((int)RolloutOpponent.HoldLastInput);
        R(0); R(0);
        I(0); I(0);
        I(0); I(0);

        return body;
    }

    private static void Send(Stream stream, EnvMessage kind, ReadOnlySpan<byte> body)
    {
        var frame = new byte[1 + body.Length];
        frame[0] = (byte)kind;
        body.CopyTo(frame.AsSpan(1));
        EnvProtocol.WriteFrame(stream, frame);
    }

    private static byte[] Receive(Stream stream) =>
        EnvProtocol.ReadFrame(stream) ?? throw new IOException("the env closed the connection");
}
