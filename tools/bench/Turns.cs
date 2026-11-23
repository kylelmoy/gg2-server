using System.Collections.Concurrent;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// Where a bot's aim turns come from, in range: <c>BENCH_TURNS</c>.
/// </summary>
/// <remarks>
/// <b>A diagnostic.</b> Within 150px a bot turns about
/// 1.7 times as much a tick as a person, and twice as often by more than 10°,
/// though its targets move no faster. This attributes every turn of a red bot with
/// a visible enemy within 150px to what the brain was doing on that tick:
/// <list type="bullet">
/// <item><c>policy</c>: a controller drove;</item>
/// <item><c>no-target</c>: the scripted layer had no target, so the aim followed the walk;</item>
/// <item><c>other</c>: its target is not the nearest enemy;</item>
/// <item><c>unseen</c>: its target is out of sight and the aim holds the last position;</item>
/// <item><c>acquiring</c>: inside the acquire hold, when the aim does not move;</item>
/// <item><c>tracking</c>: everything else.</item>
/// </list>
/// Red is wrapped in <see cref="HumanizedBot"/> (<c>BENCH_TURNS_PRESET</c>, v3 by
/// default), as the fleet would run it; blue is the same brain unfiltered. The turn
/// is the change in the character's wire aim between consecutive ticks of one life.
/// </remarks>
public static class Turns
{
    private static readonly PlayerClass[] Classes =
        [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman, PlayerClass.Heavy, PlayerClass.Pyro];

    private static readonly string[] Categories = ["policy", "no-target", "other", "unseen", "acquiring", "tracking"];

    private sealed class Tally
    {
        public long Ticks;
        public double Turn;
        public long Big;
        public double BigTurn;
        public long Jumps;
        public long Dodges;
        public long Keyed;
        public double Speed;
        public long Still;
        public double Accel;
        public long Reversals;
        public long Grounded;
    }

    public static void Run(string maps, string nav, string checkpoints, int perMap)
    {
        var preset = Environment.GetEnvironmentVariable("BENCH_TURNS_PRESET") ?? "v3";
        var tier = int.Parse(Environment.GetEnvironmentVariable("BENCH_TURNS_TIER") ?? "5", CultureInfo.InvariantCulture);
        var humanize = HumanizeOptions.ForPreset(preset);

        // D6's fit: BENCH_LEGS=memory,start,stop,keep,refractory[,reverse,idle] overrides the
        // preset's legs; the last two are D7's.
        if (Environment.GetEnvironmentVariable("BENCH_LEGS") is { Length: > 0 } legs)
        {
            var v = legs.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            humanize = humanize with
            {
                LegMemory = v[0],
                LegStartIntent = v[1],
                LegStopIntent = v[2],
                JumpKeep = v[3],
                JumpRefractoryTicks = (int)v[4],
                LegReverseIntent = v.Length > 5 ? v[5] : double.NaN,
                LegIdleActivity = v.Length > 6 ? v[6] : 0.2,
            };
            preset += $" legs {legs}";
        }
        var list = Environment.GetEnvironmentVariable("BENCH_FOCUS") is { Length: > 0 } focus ? focus.Split(',') : PerMapEval.Maps;
        var brains = new ThreadLocal<BotBrainSpec>(() => BotBrainSpec.Of(Nets.Shipped(checkpoints)));
        var acquire = BotSkill.ForTier(tier).AcquireTicks;
        var totals = new ConcurrentDictionary<string, Tally>();
        var work = from map in list from i in Enumerable.Range(0, perMap) select (map, i);

        Console.WriteLine($"{list.Length} maps x {perMap} matches, 12 bots, tier {tier}, red humanized ({preset}), 5-minute cap");

        Parallel.ForEach(work, item =>
        {
            var (map, i) = item;
            var local = Categories.ToDictionary(c => c, _ => new Tally());
            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = 12,
                Seed = (uint)(7300 + (map.Length * 131) + (i * 37)),
                CapLimit = 1,
                TimeLimitMinutes = 5,
                KillLimit = 40,
                MaxTicks = 30 * 60 * 5,
                AllowedClasses = Classes,
                Brain = brains.Value,
                Difficulty = tier,
                HumanizeTeam = Team.Red,
                HumanizeWith = humanize,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            var previous = new double[WorldState.MaxSlots];
            var wasAlive = new bool[WorldState.MaxSlots];
            var wasGrounded = new bool[WorldState.MaxSlots];
            var speed = new double[WorldState.MaxSlots];
            var heading = new int[WorldState.MaxSlots];
            var dodgedLast = new bool[WorldState.MaxSlots];

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();
                ref readonly var state = ref match.State;

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    var alive = state.Alive[slot];
                    var aim = state.Characters[slot].AimDirection;
                    var continuing = alive && wasAlive[slot];
                    ref readonly var body = ref state.Characters[slot];

                    // A jump is leaving the ground going up; a fall off a ledge
                    // leaves it going down or level.
                    var jumped = continuing && wasGrounded[slot] && !body.OnGround && body.VSpeed < 0;
                    wasGrounded[slot] = alive && body.OnGround;

                    // O2's reversal: a direction held until the speed passes 2.5px
                    // a tick the other way.
                    var accel = Math.Abs(body.HSpeed - speed[slot]);
                    speed[slot] = body.HSpeed;
                    var now = body.HSpeed > 2.5 ? 1 : body.HSpeed < -2.5 ? -1 : 0;
                    var reversed = continuing && now != 0 && heading[slot] != 0 && now != heading[slot];

                    if (!continuing)
                        heading[slot] = 0;

                    if (now != 0)
                        heading[slot] = now;
                    var turn = Math.Abs((((aim - previous[slot]) % 360) + 540) % 360 - 180);
                    previous[slot] = aim;
                    wasAlive[slot] = alive;
                    var dodged = dodgedLast[slot];
                    dodgedLast[slot] = match.HybridBotFor(slot) is { } h && h.ScriptedBot.Dodged;

                    if (!continuing || state.Players[slot].Team != Team.Red || match.HybridBotFor(slot) is not { } hybrid)
                        continue;

                    var nearest = Nearest(in state, slot, out var distance);

                    if (nearest < 0 || distance > 150)
                        continue;

                    var combat = hybrid.ScriptedBot.Combat;
                    var category =
                        hybrid.MicroDrove ? "policy"
                        : !combat.Target.Exists ? "no-target"
                        : combat.Target.Kind != BotTargetKind.Character || combat.Target.Slot != nearest ? "other"
                        : !combat.Visible ? "unseen"
                        : match.Tick - combat.TargetAt < acquire ? "acquiring"
                        : "tracking";

                    var tally = local[category];
                    tally.Ticks++;
                    tally.Turn += turn;
                    tally.Speed += Math.Abs(body.HSpeed);
                    tally.Accel += accel;

                    if (Math.Abs(body.HSpeed) < 0.5)
                        tally.Still++;

                    if (body.OnGround)
                        tally.Grounded++;

                    if (reversed)
                        tally.Reversals++;

                    if (jumped)
                    {
                        tally.Jumps++;

                        if (((body.KeyState | body.PressedKeys) & PlayerKeys.Jump) != 0)
                            tally.Keyed++;

                        // The dodge pressed on this poll or the one before.
                        if (!hybrid.MicroDrove && (dodged || dodgedLast[slot]))
                            tally.Dodges++;
                    }

                    if (turn > 10)
                    {
                        tally.Big++;
                        tally.BigTurn += turn;
                    }
                }
            }

            foreach (var (category, tally) in local)
            {
                var total = totals.GetOrAdd(category, _ => new Tally());

                lock (total)
                {
                    total.Ticks += tally.Ticks;
                    total.Turn += tally.Turn;
                    total.Big += tally.Big;
                    total.BigTurn += tally.BigTurn;
                    total.Jumps += tally.Jumps;
                    total.Dodges += tally.Dodges;
                    total.Keyed += tally.Keyed;
                    total.Speed += tally.Speed;
                    total.Still += tally.Still;
                    total.Accel += tally.Accel;
                    total.Reversals += tally.Reversals;
                    total.Grounded += tally.Grounded;
                }
            }
        });

        var ticks = totals.Values.Sum(t => t.Ticks);
        var turnSum = totals.Values.Sum(t => t.Turn);
        var bigSum = totals.Values.Sum(t => t.Big);

        Console.WriteLine();
        Console.WriteLine("red bots with a visible enemy within 150px, per tick of the character's aim");
        Console.WriteLine("category        ticks   share   |turn|  share of turn   big%   share of big turns   jumps/30t  |hspeed|  dodges/30t  still   |dv|  revs/30t  keyed/30t  ground");

        foreach (var category in Categories)
        {
            var t = totals.GetValueOrDefault(category) ?? new Tally();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{category,-12} {t.Ticks,9:N0}  {100.0 * t.Ticks / Math.Max(1, ticks),5:0.0}%  {t.Turn / Math.Max(1, t.Ticks),6:0.00}  {100.0 * t.Turn / Math.Max(1e-9, turnSum),12:0.0}%  {100.0 * t.Big / Math.Max(1, t.Ticks),5:0.0}%  {100.0 * t.Big / Math.Max(1, bigSum),12:0.0}%  {30.0 * t.Jumps / Math.Max(1, t.Ticks),9:0.00}  {t.Speed / Math.Max(1, t.Ticks),8:0.00}  {30.0 * t.Dodges / Math.Max(1, t.Ticks),10:0.00}  {(double)t.Still / Math.Max(1, t.Ticks),5:0.00}  {t.Accel / Math.Max(1, t.Ticks),5:0.00}  {30.0 * t.Reversals / Math.Max(1, t.Ticks),8:0.00}  {30.0 * t.Keyed / Math.Max(1, t.Ticks),9:0.00}  {(double)t.Grounded / Math.Max(1, t.Ticks),6:0.000}"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{"all",-12} {ticks,9:N0}  100.0%  {turnSum / Math.Max(1, ticks),6:0.00}  {100.0,12:0.0}%  {100.0 * bigSum / Math.Max(1, ticks),5:0.0}%  {"",12}  {30.0 * totals.Values.Sum(t => t.Jumps) / Math.Max(1, ticks),9:0.00}  {totals.Values.Sum(t => t.Speed) / Math.Max(1, ticks),8:0.00}  {30.0 * totals.Values.Sum(t => t.Dodges) / Math.Max(1, ticks),10:0.00}  {(double)totals.Values.Sum(t => t.Still) / Math.Max(1, ticks),5:0.00}  {totals.Values.Sum(t => t.Accel) / Math.Max(1, ticks),5:0.00}  {30.0 * totals.Values.Sum(t => t.Reversals) / Math.Max(1, ticks),8:0.00}  {"",9}  {(double)totals.Values.Sum(t => t.Grounded) / Math.Max(1, ticks),6:0.000}"));
    }

    private static int Nearest(in WorldState state, int slot, out double distance)
    {
        var team = state.Players[slot].Team;
        ref readonly var self = ref state.Characters[slot];
        var best = -1;
        distance = double.PositiveInfinity;

        for (var other = 1; other < WorldState.MaxSlots; other++)
        {
            if (other == slot || !state.Alive[other] || state.Players[other].Team == team
                || state.Players[other].Team is not (Team.Red or Team.Blue))
                continue;

            ref readonly var them = ref state.Characters[other];
            var d = Gml.PointDistance(self.X, self.Y, them.X, them.Y);

            if (d < distance)
            {
                distance = d;
                best = other;
            }
        }

        return best;
    }
}
