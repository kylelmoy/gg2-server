using System.Collections.Concurrent;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// <c>BENCH_RELEASE=&lt;seeds&gt;</c>: under the shipped handover, when does the
/// policy hold the wheel, and when it does not, which half of the gate said no.
/// </summary>
/// <remarks>
/// <para>
/// Whole matches on <c>BENCH_FOCUS</c> (default: every map <see cref="PerMapEval"/>
/// reads), all classes, tier 5, the shipped two-checkpoint brain under
/// <c>BENCH_DUEL_RULE</c> (default <c>MacroTask</c>), 9,000 ticks a match.
/// Counted over alive ticks of the classes a checkpoint drives.
/// </para>
/// <para>
/// With an enemy within <see cref="HybridBot.EngagementRange"/> and the scripted
/// layer driving, the reason is the <b>first</b> failing term of the engage hold,
/// in <see cref="ScriptedBot"/>'s own order: intel, no target, not visible, beyond
/// the class's range, the <c>BOT_ENGAGE_TICKS</c> timer, near its own goal. "Other"
/// is the hold on and the wheel not taken at a decision boundary.
/// </para>
/// <para>
/// Between 150 and 300px it splits the ticks by whether the policy drove in the
/// last two seconds: an upper bound on what a release band (hysteresis) could
/// keep.
/// </para>
/// </remarks>
public static class ReleaseReasons
{
    public static void Run(string maps, string nav, string checkpoints, int seeds)
    {
        var rule = Enum.Parse<HandoverRule>(Environment.GetEnvironmentVariable("BENCH_DUEL_RULE") ?? nameof(HandoverRule.MacroTask));
        var list = Environment.GetEnvironmentVariable("BENCH_FOCUS") is { Length: > 0 } focus ? focus.Split(',') : PerMapEval.Maps;
        // One brain per thread, as Tiers does: a spec holds the loaded networks
        // and the matches run in parallel.
        var brains = new ThreadLocal<BotBrainSpec>(() => BotBrainSpec.Of(Nets.Shipped(checkpoints)) with
        {
            Rule = rule,
        });

        var total = new ConcurrentDictionary<string, long>();
        var work = from map in list from seed in Enumerable.Range(1, seeds) select (map, seed);

        Console.WriteLine($"{list.Length} maps x {seeds} seeds x 9000 ticks, all classes, tier 5, {rule}");

        Parallel.ForEach(work, item =>
        {
            var options = HeadlessMatchOptions.Default with
            {
                Map = item.map,
                Seed = (uint)item.seed,
                MaxTicks = 9000,
                Brain = brains.Value,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            var probes = new List<Probe>();

            for (var slot = 1; slot < match.SlotCount; slot++)
            {
                if (HybridBot.Of(match.SourceOf(slot)) is not { } hybrid)
                    continue;

                var probe = new Probe(hybrid, match.SlotCount);
                probes.Add(probe);
                match.Drive(slot, probe);
            }

            while (match.Outcome == MatchOutcome.Running && match.Tick < options.MaxTicks)
                match.Step();

            foreach (var probe in probes)
            {
                foreach (var (key, count) in probe.Count)
                    total.AddOrUpdate(key, count, (_, before) => before + count);
            }
        });

        var alive = total.GetValueOrDefault("alive");
        var near = total.Where(x => x.Key.StartsWith("<150", StringComparison.Ordinal)).Sum(x => x.Value);

        Console.WriteLine($"policy-class alive ticks {alive}; an enemy within 150px on {Share(near, alive)}");

        foreach (var (key, count) in total.Where(x => x.Key.StartsWith("<150", StringComparison.Ordinal)).OrderByDescending(x => x.Value))
            Console.WriteLine($"  {key,-40} {count,9}  {Share(count, near)} of in-range, {Share(count, alive)} of alive");

        foreach (var (key, count) in total.Where(x => x.Key.StartsWith("150-300", StringComparison.Ordinal)).OrderByDescending(x => x.Value))
            Console.WriteLine($"  {key,-40} {count,9}  {Share(count, alive)} of alive");

        Console.WriteLine($"  {"the policy drove, any range",-40} {total.GetValueOrDefault("drove"),9}  {Share(total.GetValueOrDefault("drove"), alive)} of alive");
    }

    private static string Share(long part, long whole) =>
        (whole == 0 ? 0 : part * 100.0 / whole).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private sealed class Probe(HybridBot inner, int slots) : IInputSource, ICommandSource
    {
        private int _lastDrove = -1000;

        public Dictionary<string, long> Count { get; } = [];

        public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands) =>
            inner.PollCommands(in world, slot, tick, ref commands);

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            var input = inner.Poll(in world, slot, tick);

            if (!world.IsAlive(slot))
                return input;

            ref readonly var self = ref world.Character(slot);

            if (inner.TierIndexFor(self.Class) < 0)
                return input;

            Add("alive");

            var near = double.PositiveInfinity;

            for (var other = 0; other < slots; other++)
            {
                if (other == slot || !world.IsAlive(other))
                    continue;

                ref readonly var enemy = ref world.Character(other);

                if (enemy.Team != self.Team)
                    near = Math.Min(near, Gml.PointDistance(self.X, self.Y, enemy.X, enemy.Y));
            }

            if (inner.MicroDrove)
            {
                Add("drove");
                _lastDrove = tick;
            }

            if (near <= HybridBot.EngagementRange)
            {
                Add(inner.MicroDrove ? "<150 the policy drove" : "<150 scripted: " + Why(in self, tick));
            }
            else if (near <= 300 && !inner.MicroDrove)
            {
                Add(tick - _lastDrove <= 60 ? "150-300 within 2s of the policy driving" : "150-300 otherwise");
            }

            return input;
        }

        private string Why(in CharacterState self, int tick)
        {
            var scripted = inner.ScriptedBot;
            var target = scripted.Combat.Target;

            if (self.CarryingIntel)
                return "carrying intel";

            if (!target.Exists)
                return "no target";

            if (!scripted.Combat.Visible)
                return "target not visible";

            if (Gml.PointDistance(self.X, self.Y, target.X, target.Y) > BotTargeting.ClassRange(self.Class))
                return "target beyond class range";

            if (tick - scripted.Combat.TargetAt > BotConstants.EngageTicks)
                return "engage timer lapsed";

            if (scripted.Path.HasGoal
                && Gml.PointDistance(self.X, self.Y, scripted.Path.GoalX, scripted.Path.GoalY) <= BotConstants.EngageGoalNear)
            {
                return "near own goal";
            }

            return "other";
        }

        private void Add(string key) => Count[key] = Count.GetValueOrDefault(key) + 1;
    }
}
