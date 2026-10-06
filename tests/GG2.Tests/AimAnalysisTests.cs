using GG2.Maps;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="AimAnalysis"/> on scripted encounters: a shooter in slot 1, an
/// enemy in slot 2 walking across in front of them, and an aim chosen per test.
/// </summary>
/// <remarks>
/// The thresholds themselves were calibrated on the fleet's recordings, which are
/// not checked in (they name real players). These tests pin the mechanics the
/// calibration relies on: that an exact lock is counted as one at the right lag,
/// that a person-sized miss is not, that a wall makes an enemy hidden, and that
/// the statistics follow a player through a roster compaction.
/// </remarks>
public sealed class AimAnalysisTests
{
    private const string Map = "ctf_2dfort";

    private static readonly CollisionWorld Collision =
        new(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    /// <summary>A shooter spot with a clear box 170 px to its right and 40 px either way, found by search.</summary>
    private static readonly (double X, double Y) Open = FindOpen();

    [Fact]
    public void AnExactLockOnAMovingEnemyIsFlaggedAtItsLag()
    {
        var analysis = Run(lag: 3, missPixels: 0, ticks: 400);

        Assert.True(analysis.Judge(1).HasFlag(AimSignals.Lock));

        var (hits, of, lag) = analysis.Evidence(1, AimSignals.Lock);
        Assert.Equal(3, lag);
        Assert.True(hits > of * 0.9, $"{hits}/{of}");
    }

    [Fact]
    public void AMissAPersonMakesIsNotALock()
    {
        // Fifteen pixels of jitter, fresh every tick. The fleet's people lock on
        // 3% of tracking ticks at the median and 5.6% at p99, which for a
        // Gaussian miss is a spread of about 15-25 px. Five pixels would NOT
        // pass: it locks on about 16% of ticks, which is what an aimbot with a
        // little noise added looks like, and that is the signal's real limit. A
        // CONSTANT offset would not do — along the target's path it is just
        // another lag, and the analysis rightly finds it there.
        var analysis = Run(lag: 3, missPixels: 15, ticks: 400);

        Assert.Equal(AimSignals.None, analysis.Judge(1) & AimSignals.Lock);

        var (hits, of, _) = analysis.Evidence(1, AimSignals.Lock);
        Assert.True(hits < of * 0.1, $"{hits}/{of}");
    }

    [Fact]
    public void ASmallSampleIsNotJudged()
    {
        // Every tick exact, but fewer tracking ticks than the lock signal's
        // minimum: a short burst of perfect aim is not a statistic.
        var analysis = Run(lag: 0, missPixels: 0, ticks: 150);

        Assert.Equal(AimSignals.None, analysis.Judge(1) & AimSignals.Lock);
    }

    [Fact]
    public void TheLowerBoundGrowsWithTheSample()
    {
        Assert.True(AimAnalysis.LowerBound(5, 5) < AimAnalysis.LowerBound(50, 50));
        Assert.True(AimAnalysis.LowerBound(21, 225) < 0.12, "the fleet's highest human lock session");
        Assert.True(AimAnalysis.LowerBound(60, 225) > 0.12, "a jittered aimbot's rate at the same sample");
    }

    [Fact]
    public void AnEnemyBehindAWallIsHidden()
    {
        var (shooter, behind) = FindWall();
        var analysis = new AimAnalysis();
        var world = default(WorldState);
        bool[] watched = [false, true, false];

        for (var t = 0; t < 400; t++)
        {
            // The hidden enemy paces up and down so its bearing keeps turning.
            var target = (X: behind.X, Y: behind.Y + (t % 40 < 20 ? t % 20 : 20 - t % 20));
            Place(ref world, shooter, target);

            var aim = Degrees(target.X - shooter.X, target.Y - shooter.Y);
            analysis.Observe(in world, Collision, [default, Input(aim), default], watched);
        }

        Assert.True(analysis.Judge(1).HasFlag(AimSignals.Hidden));
        Assert.Equal(AimSignals.None, analysis.Judge(1) & AimSignals.Lock);
    }

    [Fact]
    public void RemovingASlotMovesTheStatisticsAboveIt()
    {
        var analysis = Run(lag: 0, missPixels: 0, ticks: 400);
        var before = analysis.Evidence(1, AimSignals.Lock);

        analysis.Remove(0);

        Assert.Equal(before, analysis.Evidence(0, AimSignals.Lock));
        Assert.Equal(0, analysis.Evidence(1, AimSignals.Lock).Of);
    }

    [Fact]
    public void ASignalIsReportedOnce()
    {
        var analysis = Run(lag: 0, missPixels: 0, ticks: 400);

        Assert.True(analysis.TakeNew(1).HasFlag(AimSignals.Lock));
        Assert.Equal(AimSignals.None, analysis.TakeNew(1));
    }

    /// <summary>
    /// The shooter stands at <see cref="Open"/>; the enemy, 120 px away, rises
    /// and falls across their view (a target moving straight along the line of
    /// sight does not turn its bearing, and is not tracking); the aim points at the enemy as it was
    /// <paramref name="lag"/> ticks ago, plus Gaussian jitter of <paramref name="missPixels"/> on each axis.
    /// </summary>
    private static AimAnalysis Run(int lag, double missPixels, int ticks)
    {
        var analysis = new AimAnalysis();
        var world = default(WorldState);
        bool[] watched = [false, true, false];
        var path = new List<(double X, double Y)>();
        var random = new Random(7);

        for (var t = 0; t < ticks; t++)
        {
            var y = Open.Y - 30 + (t % 60 < 30 ? t % 30 : 30 - t % 30) * 2;
            var target = (X: Open.X + 120, Y: y);
            path.Add(target);
            Place(ref world, Open, target);

            var seen = path[Math.Max(0, t - lag)];
            var aim = Degrees(
                seen.X - Open.X + Gaussian(random) * missPixels,
                seen.Y - Open.Y + Gaussian(random) * missPixels);
            analysis.Observe(in world, Collision, [default, Input(aim), default], watched);
        }

        return analysis;
    }

    private static void Place(ref WorldState world, (double X, double Y) shooter, (double X, double Y) target)
    {
        var self = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
        self.X = shooter.X;
        self.Y = shooter.Y;

        var enemy = CharacterState.Fresh(PlayerClass.Soldier, Team.Blue);
        enemy.X = target.X;
        enemy.Y = target.Y;

        world.Characters[1] = self;
        world.Characters[2] = enemy;
        world.Alive[1] = true;
        world.Alive[2] = true;
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    private static PlayerInput Input(double degrees) =>
        PlayerInput.FromAim(PlayerKeys.None, degrees, 100);

    private static double Degrees(double dx, double dy)
    {
        var degrees = Math.Atan2(-dy, dx) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static (double X, double Y) FindOpen()
    {
        var filter = default(WorldState).BulletFilter;

        for (var y = 200; y < 2000; y += 10)
        {
            for (var x = 100; x < 4000; x += 10)
            {
                var clear = true;
                for (var dy = -40; dy <= 40 && clear; dy += 4)
                    clear = !Collision.CollisionLineBlocksBullets(x, y, x + 170, y + dy, filter);

                if (clear)
                    return (x, y);
            }
        }

        throw new InvalidOperationException($"no open corridor on {Map}");
    }

    private static ((double X, double Y) Shooter, (double X, double Y) Behind) FindWall()
    {
        var filter = default(WorldState).BulletFilter;

        for (var y = 200; y < 2000; y += 10)
        {
            for (var x = 100; x < 4000; x += 10)
            {
                // Blocked at every height the target paces through, and on screen.
                var blocked = true;
                for (var dy = 0; dy <= 20 && blocked; dy += 2)
                    blocked = Collision.CollisionLineBlocksBullets(x, y, x + 200, y + dy, filter);

                if (blocked && !Collision.CollisionLineBlocksBullets(x, y, x + 20, y, filter))
                    return ((x, y), (x + 200, y));
            }
        }

        throw new InvalidOperationException($"no wall on {Map}");
    }
}
