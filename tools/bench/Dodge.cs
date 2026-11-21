using System.Collections.Concurrent;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// <c>BENCH_DODGE</c>: how often a bot at each tier takes a rocket that was going
/// to hit it.
/// </summary>
/// <remarks>
/// <b>The duel server's setting</b>, as <c>BENCH_VERSUS_DUEL</c> builds it: soldier
/// against soldier on <c>maps/duel</c>, the duel nets sampled, the controller
/// always driving. The shooter is tier 5; the subject plays each tier in
/// <c>BENCH_DODGE_TIERS</c> (default 5,4,3,2,1) on the same placements, so the
/// tiers are paired by seed.
/// <para>
/// <b>A rocket counts once it is on target</b>: first seen, then the scripted
/// dodge's model (<see cref="BotReflex.RocketDodge"/> — a straight line, the
/// subject carrying its horizontal speed at its height, the BOT_BODY_* box plus
/// the rocket's radius) says it enters the subject inside 40 ticks. The tick it
/// would is its warning. It is a <b>direct hit</b> when its last tick's segment
/// passes through the subject's box. This is <c>tools/dodgescan</c>'s measurement,
/// so the bench reads on the same scale as the duel server's recordings.
/// </para>
/// <para>
/// Tier 5 here is the trained observation and tier 4 differs from it only by the
/// human reflex floor (no <c>HumanizedBot</c> wraps either), so the 5-to-4 step is
/// the rocket perception alone.
/// </para>
/// </remarks>
internal static class Dodge
{
    private const int Horizon = 40;

    private sealed record Row(int Tier, int OnTarget, int[] Warned, int[] Direct, int Ticks, double Score);

    private static readonly (int Lo, int Hi)[] Buckets = [(1, 4), (5, 8), (9, 12), (13, 40)];

    public static void Run(string maps, string nav, string checkpoints, int perMap)
    {
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_DUEL_TICKS") ?? "900", CultureInfo.InvariantCulture);
        int[] tiers = Environment.GetEnvironmentVariable("BENCH_DODGE_TIERS") is { Length: > 0 } listed
            ? [.. listed.Split(',').Select(t => int.Parse(t, CultureInfo.InvariantCulture))]
            : [5, 4, 3, 2, 1];
        var mapDirectory = Path.Combine(maps, "duel");
        var navDirectory = Path.Combine(nav, "duel");
        string[] list = [.. Enumerable.Range(1, 8).Select(n => $"duel_{n:00}")];

        static BotBrainSpec Always(MicroControllerSpec[] specs) => BotBrainSpec.Of(specs) with
        {
            Rule = HandoverRule.Distance,
            Range = 100_000,
            ReleaseRange = 100_000,
        };

        var policy = new ThreadLocal<BotBrainSpec>(() => Always(Nets.Duel(Path.Combine(checkpoints, "duel"))));
        var tables = new ConcurrentDictionary<string, Lazy<NavDistanceSet?>>();
        var work = from map in list from i in Enumerable.Range(0, perMap) from tier in tiers select (map, i, tier);
        var results = new ConcurrentBag<Row>();

        Console.WriteLine($"{list.Length} duel maps x {perMap} soldier duels x tiers {string.Join(",", tiers)}, shooter tier 5, {cap}-tick cap, duel nets always driving");

        var halfWidth = BotConstants.BodyHalfw + BotConstants.RocketRadius;
        var up = BotConstants.BodyUp + BotConstants.RocketRadius;
        var down = BotConstants.BodyDown + BotConstants.RocketRadius;

        bool InBox(double rx, double ry, double cx, double cy) =>
            Math.Abs(rx - cx) <= halfWidth && ry >= cy - up && ry <= cy + down;

        Parallel.ForEach(work, item =>
        {
            var (map, i, tier) = item;
            var subjectTeam = i % 2 == 0 ? Team.Red : Team.Blue;
            var (world, red, blue) = Tiers.PlaceInSight(
                mapDirectory, navDirectory, map, (uint)(9700 + (map.Length * 131) + (i * 37)), PlayerClass.Soldier);
            var graph = world.Nav;
            var distances = tables.GetOrAdd(map, m => new Lazy<NavDistanceSet?>(() =>
                graph is null ? null : NavDistanceSet.LoadOrDerive(navDirectory, m, 1, graph, out _))).Value;

            var subject = subjectTeam == Team.Red ? red : blue;
            var shooter = subjectTeam == Team.Red ? blue : red;
            var shooterTeam = subjectTeam == Team.Red ? Team.Blue : Team.Red;

            world.Drive(subject, policy.Value!.Wrap(
                new ScriptedBot(world.Nav, new NavQuery(subjectTeam), BotSkill.ForTier(tier), subject) { Distances = distances }));
            world.Drive(shooter, policy.Value!.Wrap(
                new ScriptedBot(world.Nav, new NavQuery(shooterTeam), BotSkill.ForTier(5), shooter) { Distances = distances }));

            // Per rocket: its warning (0 if it was never on target), and where it
            // and the subject last were.
            var tracked = new Dictionary<ProjectileHandle, (int Warning, double X, double Y, double H, double V, double Sx, double Sy)>();
            var warned = new int[Buckets.Length];
            var direct = new int[Buckets.Length];
            var onTarget = 0;

            void Resolve(int warning, double x, double y, double h, double v, double sx, double sy)
            {
                if (warning <= 0)
                    return;

                var b = Array.FindIndex(Buckets, k => warning >= k.Lo && warning <= k.Hi);
                warned[b]++;
                onTarget++;

                for (var s = 0.0; s <= 1.0; s += 0.125)
                {
                    if (InBox(x + (h * s), y + (v * s), sx, sy))
                    {
                        direct[b]++;
                        return;
                    }
                }
            }

            bool subjectDied = false, shooterDied = false;
            var ticks = 0;
            var subjectStart = world.State.Characters[subject].Hp;
            var shooterStart = world.State.Characters[shooter].Hp;
            var seen = new HashSet<ProjectileHandle>();

            while (ticks < cap && !subjectDied && !shooterDied)
            {
                world.Step();
                ticks++;
                subjectDied = world.Events.Died[subject];
                shooterDied = world.Events.Died[shooter];

                ref readonly var state = ref world.State;
                seen.Clear();

                if (state.Alive[subject])
                {
                    ref readonly var body = ref state.Characters[subject];

                    for (var p = 0; p < state.LiveProjectileCount; p++)
                    {
                        var handle = state.LiveProjectileHandle(p);
                        ref readonly var rocket = ref state.Projectiles[handle.Index];

                        if (rocket.Kind != ProjectileKind.Rocket || rocket.Team != shooterTeam)
                            continue;

                        seen.Add(handle);

                        var warning = 0;
                        if (tracked.TryGetValue(handle, out var known))
                        {
                            warning = known.Warning;
                        }
                        else
                        {
                            for (var t = 1; t <= Horizon; t++)
                            {
                                if (InBox(rocket.X + (rocket.Motion.HSpeed * t), rocket.Y + (rocket.Motion.VSpeed * t),
                                    body.X + (body.HSpeed * t), body.Y))
                                {
                                    warning = t;
                                    break;
                                }
                            }
                        }

                        tracked[handle] = (warning, rocket.X, rocket.Y, rocket.Motion.HSpeed, rocket.Motion.VSpeed, body.X, body.Y);
                    }
                }

                foreach (var handle in tracked.Keys.Where(h => !seen.Contains(h)).ToList())
                {
                    var r = tracked[handle];
                    tracked.Remove(handle);
                    Resolve(r.Warning, r.X, r.Y, r.H, r.V, r.Sx, r.Sy);
                }
            }

            foreach (var r in tracked.Values)
                Resolve(r.Warning, r.X, r.Y, r.H, r.V, r.Sx, r.Sy);

            var subjectLost = subjectDied ? 1.0 : 1 - (world.State.Characters[subject].Hp / subjectStart);
            var shooterLost = shooterDied ? 1.0 : 1 - (world.State.Characters[shooter].Hp / shooterStart);
            var score = subjectDied != shooterDied ? (shooterDied ? 1.0 : 0.0)
                : subjectDied || subjectLost + shooterLost <= 0 ? 0.5
                : shooterLost / (subjectLost + shooterLost);

            results.Add(new Row(tier, onTarget, warned, direct, ticks, score));
        });

        Console.WriteLine();
        Console.WriteLine("direct-hit share of on-target rockets by warning (ticks), subject at the tier; score is the subject's (kill 1, else damage share)");
        Console.WriteLine($"{"tier",4} {"duels",6} {"score",6} {"rockets",8} {"direct",7}   " + string.Join("  ", Buckets.Select(b => $"{b.Lo}-{b.Hi}t".PadLeft(12))));

        foreach (var tier in tiers)
        {
            var rows = results.Where(r => r.Tier == tier).ToList();
            var n = rows.Sum(r => r.OnTarget);
            var d = rows.Sum(r => r.Direct.Sum());
            var cells = Buckets.Select((_, b) =>
            {
                var w = rows.Sum(r => r.Warned[b]);
                var h = rows.Sum(r => r.Direct[b]);
                return (w == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{(double)h / w:0.00} ({w})")).PadLeft(12);
            });

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{tier,4} {rows.Count,6} {rows.Average(r => r.Score),6:0.000} {n,8} {(n == 0 ? 0 : (double)d / n),7:0.000}   {string.Join("  ", cells)}"));
        }
    }
}
