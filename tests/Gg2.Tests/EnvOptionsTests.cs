using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <c>gg2-env</c>'s command line, and the composition root it feeds.
/// </summary>
/// <remarks>
/// ⚠️ <b>The gate that was owed for a whole capability.</b> <c>Program.cs</c>
/// parsed <c>--nav</c> and then built <c>new EnvServer(maps)</c> without it, so
/// every served world had a null nav graph and <see cref="EnvDriver.Scripted"/>
/// was refused in every batch — while <c>docs/ENV.md</c> documented the driver
/// and every test upstream passed. Reported by <c>gg2-rl</c>, which could not run
/// phase 1's exit criterion at all.
/// <para>
/// It survived because <b>nothing could call it</b>: <c>Program.cs</c> is
/// top-level statements, <see cref="EnvServer"/>'s nav directory is an optional
/// parameter so dropping it compiles, and every existing test constructs
/// <see cref="EnvServer"/> itself — including <see cref="EnvProtocolTests"/>,
/// which passes no nav directory and therefore could never have noticed.
/// </para>
/// <para>
/// ⚠️ <b>So the gate that matters is the second one below, not the first.</b>
/// Asserting that <c>--nav</c> parses is not the same claim as asserting it
/// reaches the server, and the parse was never the half that was wrong.
/// </para>
/// </remarks>
public sealed class EnvOptionsTests
{
    private static string[] Serve(params string[] extra) => ["serve", .. extra];

    /// <summary>Each option lands in its own field, and an unknown one is refused.</summary>
    [Fact]
    public void TheCommandLineParses()
    {
        Assert.True(EnvOptions.TryParse(
            Serve("--port", "9", "--maps", "m", "--nav", "n"), out var options, out _));

        Assert.Equal("serve", options.Command);
        Assert.Equal(9, options.Port);
        Assert.Equal("m", options.Maps);
        Assert.Equal("n", options.Nav);

        Assert.True(EnvOptions.TryParse(
            ["smoke", "--worlds", "4", "--duels", "7", "--threads", "3"], out var smoke, out _));

        Assert.Equal("smoke", smoke.Command);
        Assert.Equal(4, smoke.Worlds);
        Assert.Equal(7, smoke.Duels);
        Assert.Equal(3, smoke.Threads);

        // ⚠️ Nav is null rather than empty when it is not given, because null is
        // what makes Scripted refuse — see EnvOptions.Nav.
        Assert.Null(smoke.Nav);

        Assert.False(EnvOptions.TryParse(Serve("--wat", "1"), out _, out var unknown));
        Assert.Contains("--wat", unknown, StringComparison.Ordinal);

        // A trailing flag reads past the end of the vector unless it is caught.
        Assert.False(EnvOptions.TryParse(Serve("--nav"), out _, out var dangling));
        Assert.Contains("wants a value", dangling, StringComparison.Ordinal);

        Assert.False(EnvOptions.TryParse(Serve("--port", "eight"), out _, out var notANumber));
        Assert.Contains("--port", notANumber, StringComparison.Ordinal);

        Assert.False(EnvOptions.TryParse(["frobnicate"], out _, out var command));
        Assert.Contains("unknown command", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ <b>A server built from <c>--nav</c> can actually serve a Scripted
    /// side.</b> This is the bug, end to end.
    /// </summary>
    /// <remarks>
    /// It goes through <see cref="EnvOptions.CreateServer"/> — the exact line
    /// that dropped the argument — and then over a real socket, because the
    /// claim being made is about what a caller of the built tool can ask for.
    /// <para>
    /// ⚠️ <b>The control is the same request without <c>--nav</c></b>, which must
    /// be REFUSED rather than served by a bot that cannot walk. Without that
    /// half, an implementation that ignored the driver entirely would pass.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAServerGivenNavCanServeAScriptedSide(bool withNav)
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        string[] args = withNav
            ? Serve("--maps", ReferenceTree.IncludedFiles, "--nav", ReferenceTree.NavCacheDirectory)
            : Serve("--maps", ReferenceTree.IncludedFiles);

        Assert.True(EnvOptions.TryParse(args, out var options, out var error), error);

        using var loopback = new Loopback(options.CreateServer());

        loopback.Send(EnvMessage.Hello, Int(EnvProtocol.Version));
        Assert.Equal((byte)EnvMessage.Hello, loopback.Receive()[0]);

        loopback.Send(EnvMessage.MakeBatch, ScriptedBatch());
        var reply = loopback.Receive();

        if (withNav)
        {
            Assert.Equal((byte)EnvMessage.MakeBatch, reply[0]);
            return;
        }

        Assert.Equal((byte)EnvMessage.Error, reply[0]);

        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1));
        var reason = System.Text.Encoding.UTF8.GetString(reply, 5, length);

        Assert.Contains("nav graph", reason, StringComparison.Ordinal);
    }

    /// <summary>One world on gg_debug with a scripted blue side.</summary>
    private static byte[] ScriptedBatch()
    {
        var name = System.Text.Encoding.UTF8.GetBytes("gg_debug");

        return
        [
            .. Int(1),
            .. Int(name.Length),
            .. name,
            .. Int((int)PlayerClass.Soldier),
            .. Int((int)PlayerClass.Soldier),
            .. Real(600), .. Real(850),
            .. Real(700), .. Real(850),
            .. Int(20),
            .. Int(2),
            .. Int(1234),
            .. Int(1),
            .. Int((int)EnvDriver.Client),
            .. Int((int)EnvDriver.Scripted),
            .. Int(0),
            .. Int(3),
            .. Int((int)RolloutOpponent.HoldLastInput),
            .. Int((int)RolloutOpponent.HoldLastInput),
            .. Real(0),
            .. Real(0),

            // v6's rollout horizons, both 0 = the search's own default.
            .. Int(0),
            .. Int(0),

            // v7's shadow-search flags, both off.
            .. Int(0),
            .. Int(0),
        ];
    }

    private static byte[] Int(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] Real(double value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, value);
        return b;
    }

    /// <summary>A server on a loopback socket, for one exchange.</summary>
    private sealed class Loopback : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public Loopback(EnvServer server)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _serving = Task.Run(() =>
            {
                using var accepted = _listener.AcceptTcpClient();
                using var stream = accepted.GetStream();

                server.ServeOne(stream);
            });

            _client = new TcpClient();
            _client.Connect(IPAddress.Loopback, port);
            _stream = _client.GetStream();
        }

        public void Send(EnvMessage kind, ReadOnlySpan<byte> body)
        {
            var frame = new byte[1 + body.Length];
            frame[0] = (byte)kind;
            body.CopyTo(frame.AsSpan(1));

            EnvProtocol.WriteFrame(_stream, frame);
        }

        public byte[] Receive() =>
            EnvProtocol.ReadFrame(_stream) ?? throw new IOException("the server closed the connection");

        public void Dispose()
        {
            try
            {
                Send(EnvMessage.Close, []);
                Receive();
            }
            catch (IOException)
            {
                // Teardown; the server may already be gone.
            }

            _stream.Dispose();
            _client.Dispose();
            _serving.Wait(TimeSpan.FromSeconds(5));
            _listener.Stop();
        }
    }
}
