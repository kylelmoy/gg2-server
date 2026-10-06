using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// <see cref="AimCheck.IsReachable"/> against the stock client's own encoding.
/// </summary>
/// <remarks>
/// The fixture is what a stock GM8 client wrote for each pointer offset, so the
/// first test is the false-positive gate: any row it rejects is a real player the
/// server would call a cheat. The others say the check is not vacuous — a check
/// that accepted everything would pass the first one too.
/// </remarks>
public sealed class AimCheckTests
{
    private static readonly string Samples =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "aim-encoding-samples.txt");

    [Fact]
    public void EveryEncodingTheStockClientWroteIsReachable()
    {
        var rows = 0;

        foreach (var line in File.ReadLines(Samples))
        {
            if (line.StartsWith('#'))
                continue;

            var parts = line.Split(' ');
            var direction = ushort.Parse(parts[2]);
            var distance = byte.Parse(parts[3]);

            Assert.True(
                AimCheck.IsReachable(direction, distance),
                $"offset {parts[0]},{parts[1]} wrote {direction}/{distance}, which the check rejects");
            rows++;
        }

        Assert.True(rows > 10_000, $"only {rows} rows read");
    }

    [Fact]
    public void ThePointerAtTheCentreIsReachable()
    {
        Assert.True(AimCheck.IsReachable(0, 0));
        Assert.False(AimCheck.IsReachable(1, 0));
    }

    [Fact]
    public void AnAngleAimedAtAFractionalTargetIsAlmostNeverReachable()
    {
        // What an aimbot writes: the exact angle and distance to a character's
        // position, which is a double. Deterministic stream, so the count is pinned
        // by the bound rather than flaking.
        var random = new Random(1234);
        var reachable = 0;
        const int Trials = 10_000;

        for (var i = 0; i < Trials; i++)
        {
            var dx = (random.NextDouble() - 0.5) * 800;
            var dy = (random.NextDouble() - 0.5) * 600;
            var (direction, distance) = Encode(dx, dy);

            if (distance < 255 && AimCheck.IsReachable(direction, distance))
                reachable++;
        }

        // 515 of 10,000 at this seed: about 5% of fabricated aims pass, so one is
        // caught 95% of the time and AimCheck.KickAfter of them are certain. The
        // band is wide enough for a change to the slack and narrow enough that a
        // check accepting everything, or nothing, goes red.
        Assert.InRange(reachable, 300, 800);
    }

    [Fact]
    public void EveryIntegerOffsetInsideTheViewIsReachable()
    {
        // The whole 800x600 view at a coarse stride, re-derived here rather than
        // read from the fixture, so a check that only memorised the fixture's grid
        // would still fail.
        for (var dx = -400; dx <= 400; dx += 13)
        {
            for (var dy = -300; dy <= 300; dy += 11)
            {
                var (direction, distance) = Encode(dx, dy);
                Assert.True(AimCheck.IsReachable(direction, distance), $"offset {dx},{dy}");
            }
        }
    }

    [Fact]
    public void DistanceByte255IsAlwaysAccepted()
    {
        // The pointer is tracked outside the window, so 255 is legal in every direction.
        for (var direction = 0; direction <= ushort.MaxValue; direction += 997)
            Assert.True(AimCheck.IsReachable((ushort)direction, 255));
    }

    [Fact]
    public void KickModeKicksAfterTheThresholdAndNotBefore()
    {
        using var harness = new ServerHarness(options => options.AimCheck = AimCheckMode.Kick);

        var client = harness.JoinClient("cheat");
        harness.Tick(2);
        client.Take(client.Received.Count);

        var impossible = Unreachable(distance: 100);

        for (var i = 0; i < AimCheck.KickAfter - 1; i++)
            client.SendInputState(impossible);

        harness.Tick(10);
        Assert.False(client.IsClosed, "kicked before AimCheck.KickAfter impossible aims");
        Assert.Single(harness.Log.Warnings, line => line.Contains("is not reachable"));

        client.SendInputState(impossible);
        harness.TickUntil(() => client.IsClosed, "the kick");

        var seen = client.Take(client.Received.Count);
        var at = Array.IndexOf(seen, (byte)MessageType.Kick);
        Assert.True(at >= 0 && at + 1 < seen.Length, "dropped with no KICK");
        Assert.Equal((byte)KickReason.Operator, seen[at + 1]);
        Assert.True(harness.Log.Contains($"{AimCheck.KickAfter} impossible aim inputs"));
    }

    [Fact]
    public void AStockClientsAimIsNeverReported()
    {
        // The default mode, fed what a stock client sends while its pointer
        // sweeps the view: nothing may be logged, and in kick mode nothing kicked.
        using var harness = new ServerHarness(options => options.AimCheck = AimCheckMode.Kick);

        var client = harness.JoinClient("player");
        harness.Tick(2);

        for (var i = 0; i < 200; i++)
        {
            var (direction, distance) = Encode(-400 + i * 4, 300 - i * 3);
            client.SendInputState(new PlayerInput(PlayerKeys.None, direction, distance));

            if (i % 10 == 9)
                harness.Tick(1);
        }

        harness.Tick(5);

        Assert.False(client.IsClosed);
        Assert.DoesNotContain(harness.Log.Warnings, line => line.Contains("is not reachable"));
    }

    [Fact]
    public void LogModeIsTheDefaultAndNeverKicks()
    {
        Assert.Equal(AimCheckMode.Log, new ServerOptions().AimCheck);

        using var harness = new ServerHarness();

        var client = harness.JoinClient("cheat");
        harness.Tick(2);

        var impossible = Unreachable(distance: 100);
        for (var i = 0; i < AimCheck.KickAfter * 3; i++)
            client.SendInputState(impossible);

        harness.Tick(15);

        Assert.False(client.IsClosed);
        Assert.Single(harness.Log.Warnings, line => line.Contains("is not reachable"));
    }

    /// <summary>An input at this distance whose direction no pointer offset produces.</summary>
    private static PlayerInput Unreachable(byte distance)
    {
        for (var direction = 1; direction < ushort.MaxValue; direction++)
        {
            if (!AimCheck.IsReachable((ushort)direction, distance))
                return new PlayerInput(PlayerKeys.None, (ushort)direction, distance);
        }

        throw new InvalidOperationException("every direction reachable");
    }

    /// <summary><c>ClientInputstate.gml</c>'s encoding, with GM8's half-away rounding and clamp.</summary>
    private static (ushort Direction, byte Distance) Encode(double dx, double dy)
    {
        var degrees = Math.Atan2(-dy, dx) * (180 / Math.PI);
        if (degrees < 0)
            degrees += 360;

        var direction = Math.Min(65535.0, Math.Round(degrees * 65536 / 360, MidpointRounding.AwayFromZero));
        var distance = Math.Min(255.0, Math.Round(Math.Min(255, Math.Sqrt(dx * dx + dy * dy) / 2), MidpointRounding.AwayFromZero));

        return ((ushort)direction, (byte)distance);
    }
}
