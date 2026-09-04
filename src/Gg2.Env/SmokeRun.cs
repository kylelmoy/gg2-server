using Gg2.Bots;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gg2.Protocol;

namespace Gg2.Env;

/// <summary>
/// A random-head agent driving the env over a real socket.
/// </summary>
/// <remarks>
/// <b><c>docs/ENV.md</c>'s v0 exit criteria 1 and 3 in one run</b>: a policy that
/// presses buttons at random completes N duels with no crash and no hang, and
/// the decision-steps-per-second it manages is the number that decides whether
/// shared memory ever gets built.
/// <para>
/// <b>Over the real socket, not by calling <see cref="EnvBatch"/>.</b> The
/// throughput question is about the boundary — framing, copying, the round trip
/// — and measuring the batch directly would answer a question nobody asked.
/// </para>
/// <para>
/// <b>Random is not a weak test here.</b> A random policy reaches states a
/// scripted one never does — firing into its own feet, walking off every ledge —
/// which is exactly the coverage a smoke run wants. What it cannot do is check
/// that the numbers are <em>right</em>; that is the gates' job.
/// </para>
/// </remarks>
public static class SmokeRun
{
    public readonly record struct Result(
        int Duels, int DecisionSteps, double Seconds, int Worlds)
    {
        /// <summary>Decision steps per second, summed over the batch's worlds.</summary>
        public double StepsPerSecond => Seconds <= 0 ? 0 : DecisionSteps / Seconds;

        public override string ToString() =>
            $"{Duels} duels, {DecisionSteps} decision steps over {Worlds} world(s) "
            + $"in {Seconds:0.00}s = {StepsPerSecond:0} steps/sec";
    }

    /// <summary>
    /// Runs until <paramref name="duels"/> episodes have terminated.
    /// </summary>
    /// <param name="maxSteps">
    /// A hard ceiling on decision steps. <b>Exposed so the guard can be
    /// gated.</b> A safety net nobody has watched trip is indistinguishable from
    /// one that does not work, and the default is far too generous to reach in a
    /// test — which is correct for the default and useless for a test.
    /// </param>
    public static Result Run(
        string mapDirectory, int worlds, int duels, uint seed, int threads = 1, int maxSteps = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(() =>
        {
            using var accepted = listener.AcceptTcpClient();

            // As EnvServer.Serve does, and for the same reason — this run exists
            // to report the throughput a real client sees.
            accepted.NoDelay = true;

            using var stream = accepted.GetStream();

            new EnvServer(mapDirectory).ServeOne(stream);
        });

        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.NoDelay = true;

            using var stream = client.GetStream();

            return Drive(stream, worlds, duels, seed, threads, maxSteps);
        }
        finally
        {
            listener.Stop();
            server.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private static Result Drive(
        Stream stream, int worlds, int duels, uint seed, int threads, int maxSteps)
    {
        Handshake(stream);
        MakeBatch(stream, worlds, seed, threads);

        var random = new Random(unchecked((int)seed));
        var actions = new byte[worlds * 2 * EnvProtocol.ActionBytes];

        var finished = 0;
        var steps = 0;

        var clock = Stopwatch.StartNew();

        // A hard ceiling on the loop, not just on the duel count. A world that
        // stopped terminating would otherwise hang the smoke run for ever, which
        // is the one failure a smoke run must report rather than exhibit: a hung
        // run in CI is indistinguishable from an infrastructure problem, and
        // somebody restarts it instead of reading it.
        var ceiling = maxSteps > 0 ? maxSteps : duels * 1000;

        while (finished < duels && steps < ceiling)
        {
            for (var i = 0; i < worlds; i++)
            {
                var pair = i * 2 * EnvProtocol.ActionBytes;

                Fill(actions.AsSpan(pair, EnvProtocol.ActionBytes), random);
                Fill(actions.AsSpan(pair + EnvProtocol.ActionBytes, EnvProtocol.ActionBytes), random);
            }

            Send(stream, EnvMessage.Step, actions);

            var reply = Receive(stream);

            if (reply[0] != (byte)EnvMessage.Step)
                throw new InvalidOperationException($"the env answered {reply[0]} to a Step");

            finished += CountTerminations(reply, worlds);
            steps += worlds;
        }

        clock.Stop();

        if (finished < duels)
        {
            throw new InvalidOperationException(
                $"only {finished} of {duels} duels ended in {steps} steps; a world is not terminating");
        }

        Send(stream, EnvMessage.Close, []);
        Receive(stream);

        return new Result(finished, steps, clock.Elapsed.TotalSeconds, worlds);
    }

    private static void Fill(Span<byte> heads, Random random)
    {
        heads[0] = (byte)random.Next(3);
        heads[1] = (byte)random.Next(2);
        heads[2] = (byte)random.Next(2);
        heads[3] = (byte)random.Next(3);
        heads[4] = (byte)random.Next(ActionDecoder.AimBins);

        // v4's Zoom head, and the random agent does exercise it. The smoke run
        // is the random-head criterion: a head it never set would be a head the
        // socket path was never measured on, which is how a field arrives that is
        // accepted, echoed and inert.
        heads[5] = (byte)random.Next(2);
    }

    /// <summary>
    /// How many agents reported a terminal step. Counted from the red agent
    /// only — in a duel both sides end together, and counting both would report
    /// twice as many duels as happened.
    /// </summary>
    private static int CountTerminations(byte[] reply, int worlds)
    {
        var componentsAt = 1 + 4 + (worlds * 2 * ObservationWriter.Length * 4);
        var ended = 0;

        for (var i = 0; i < worlds; i++)
        {
            var red = componentsAt + (i * 2 * EnvProtocol.ComponentBytes);
            var termination = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(red + 16));

            if (termination != (int)Termination.Running)
                ended++;
        }

        return ended;
    }

    private static void Handshake(Stream stream)
    {
        var hello = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(hello, EnvProtocol.Version);

        Send(stream, EnvMessage.Hello, hello);

        var reply = Receive(stream);

        if (reply[0] != (byte)EnvMessage.Hello)
            throw new InvalidOperationException("the env refused the handshake");
    }

    private static void MakeBatch(Stream stream, int worlds, uint seed, int threads)
    {
        var name = System.Text.Encoding.UTF8.GetBytes("gg_debug");
        // Sized from EnvProtocol.MakeBatchFixedBytes rather than by adding a
        // term per version. A hand-summed form has to be edited in step with
        // every bump and silently writes past its end when a version adds
        // fields — an ArgumentOutOfRangeException from BinaryPrimitives, three
        // layers from the cause. The count and the name length are the only
        // parts that are not fixed.
        var body = new byte[4 + 4 + name.Length + EnvProtocol.MakeBatchFixedBytes];
        var at = 0;

        void Int(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at), v);
            at += 4;
        }

        void Real(double v)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(at), v);
            at += 8;
        }

        Int(worlds);
        Int(name.Length);
        name.CopyTo(body, at);
        at += name.Length;

        Int((int)PlayerClass.Soldier);
        Int((int)PlayerClass.Soldier);
        Real(600);
        Real(850);
        Real(700);
        Real(850);
        Int(120);
        Int(2);
        Int((int)seed);
        Int(threads);

        // v2's drivers. The smoke run is the random-head agent's exit
        // criterion, so both sides stay Client here on purpose — a bot-driven
        // side would be measuring the bot rather than the socket.
        Int((int)EnvDriver.Client);
        Int((int)EnvDriver.Client);
        Int(0);
        Int(0);

        // v3's four, all at their defaults. The rollout opponents are unread
        // with no Rollout side, and the separation band is OFF — the smoke run
        // names its coordinates, and a band is refused with explicit
        // placements rather than ignored. Written out rather than left off
        // because the frame length is checked as a whole.
        Int((int)RolloutOpponent.HoldLastInput);
        Int((int)RolloutOpponent.HoldLastInput);
        Real(0);
        Real(0);

        // v6's horizons, both 0 = the search's own default. The smoke run has no
        // Rollout side, so they are unread; written because the frame length is
        // checked as a whole.
        Int(0);
        Int(0);

        // v7's shadow flags, both off. The smoke run measures the socket, and a
        // shadow search on it would be measuring a search.
        Int(0);
        Int(0);

        // v8's commitments and continuation handles, all 0. A continuation is a
        // forward pass per candidate per decision, so one here would make this
        // measure a policy rather than the socket.
        Int(0);
        Int(0);
        Int(0);
        Int(0);

        Send(stream, EnvMessage.MakeBatch, body);

        var reply = Receive(stream);

        if (reply[0] != (byte)EnvMessage.MakeBatch)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1));
            throw new InvalidOperationException(
                "MakeBatch refused: " + System.Text.Encoding.UTF8.GetString(reply, 5, length));
        }
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
