// Replays --record traces and writes one row per jump take-off made out of
// combat, for the people in them and the bots, so the way each side gets over
// an obstacle can be compared on the same maps.
//
// A take-off is a tick that starts on the ground and ends off it going up (a
// jump, not a walk off a ledge). "Out of combat" is no live enemy within 300px
// at the take-off, so a bot's row is its path follower's work, not its fight.
//
// Per take-off:
//   context      what is ahead in the direction of travel: "step" (solid to
//                jump onto within 48px), "gap" (the floor ends within 48px),
//                or "flat"; with the distance to it in px
//   approach     |hspeed| at take-off, the most in the 10 ticks before (so
//                braking shows as the difference), ticks nearly still in the
//                15 before, and direction reversals in the 30 before
//   flight       ticks in the air, left/right key changes per air tick, the
//                share of air ticks with neither key, speed reversals in the
//                air, mean |hspeed| in the air
//   landing      the height gained (px, up positive), and for a jump that
//                follows a landing within 60 ticks, the ticks on the ground
//                between them (stairs)
//
// Output: CSV on stdout. gg2-rl docs/PLAN.md, the navigation study, reads it.

using System.Collections.Concurrent;
using System.Globalization;
using GG2.Maps;
using GG2.Parity;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

internal static class Program
{
    private const int History = 32;
    private const double CombatRange = 300;
    private const int Look = 48;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("navscan <trace-or-directory>... [--maps <path>] > out.csv");
            return 2;
        }

        var maps = Flag(args, "--maps") ?? MapDirectory.Resolve();
        var files = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--maps")
            {
                i++;
                continue;
            }

            if (Directory.Exists(args[i]))
                files.AddRange(Directory.EnumerateFiles(args[i], "*.trace", SearchOption.AllDirectories));
            else if (File.Exists(args[i]))
                files.Add(args[i]);
        }

        files.Sort(StringComparer.Ordinal);
        var rows = new ConcurrentBag<string>();
        var read = 0;
        var failed = 0;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
        {
            try
            {
                Scan(file, maps, rows);
                Interlocked.Increment(ref read);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"{Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
            }
        });

        Console.WriteLine("trace,map,slot,participant,controller,class,context,feature_px,rise,speed,speed_max_pre,still_pre,reversals_pre,air_ticks,air_key_changes,air_no_key,air_reversals,air_speed,ground_gap,enemy_px");

        foreach (var row in rows.OrderBy(r => r, StringComparer.Ordinal))
            Console.WriteLine(row);

        Console.Error.WriteLine($"{read} trace(s) scanned, {failed} failed");
        return read == 0 ? 1 : 0;
    }

    private sealed class Slot
    {
        public TraceParticipant Participant;
        public string Controller = "";
        public readonly double[] Speed = new double[History];
        public int Seen;
        public bool WasGround;
        public Flight? Air;
        public int LandedAt = -1000;
    }

    private sealed class Flight
    {
        public required string Prefix;
        public required string Approach;
        public required double Enemy;
        public required double Y0;
        public required int Dir;
        public required int GroundGap;
        public int Ticks;
        public int KeyChanges;
        public int NoKey;
        public int Reversals;
        public double SpeedSum;
        public PlayerKeys LastKeys;
        public int LastSign;
    }

    private static void Scan(string file, string maps, ConcurrentBag<string> rows)
    {
        var trace = Trace.Decode(File.ReadAllBytes(file));

        if (trace.Roster.Count == 0)
            throw new InvalidDataException("no roster labels");

        LevelMap Load(string name) => LevelMap.FromPng(name, File.ReadAllBytes(Path.Combine(maps, name + ".png")));
        WorldShape ShapeFor(string name) => GameModes.ShapeOf(Load(name).ForArea(1));

        var ticks = TraceParser.Parse(trace, ShapeFor(trace.Header.Map), out _, out _, ShapeFor);
        var simulation = new PortedSimulation(Load);
        var notes = trace.Roster.OrderBy(n => n.Tick).ToArray();
        var name = Path.GetFileName(file);
        var map = trace.Header.Map;
        var slots = Enumerable.Range(0, WorldState.MaxSlots).Select(_ => new Slot()).ToArray();
        var noteIndex = 0;

        var replayer = new Replayer(simulation)
        {
            Compare = Fields.Trajectory,
            OnTick = t =>
            {
                while (noteIndex < notes.Length && notes[noteIndex].Tick <= t)
                {
                    var note = notes[noteIndex++];
                    if (note.Slot >= WorldState.MaxSlots)
                        continue;

                    slots[note.Slot] = new Slot { Participant = note.Participant, Controller = note.Controller };
                }

                ref readonly var world = ref simulation.State;
                var collision = simulation.Collision!;
                var traceSlots = ticks[t].Slots;

                for (var i = 0; i < WorldState.MaxSlots; i++)
                {
                    var s = slots[i];

                    if (s.Participant == TraceParticipant.Unknown || !world.Alive[i])
                    {
                        s.Seen = 0;
                        s.Air = null;
                        continue;
                    }

                    ref readonly var c = ref world.Characters[i];
                    var keys = i < traceSlots.Length ? traceSlots[i].Input.Keys : PlayerKeys.None;

                    if (s.Air is { } air)
                    {
                        if (c.OnGround)
                        {
                            if (air.Ticks > 0)
                            {
                                rows.Add(string.Join(',',
                                    air.Prefix,
                                    F(air.Y0 - c.Y),
                                    air.Approach,
                                    air.Ticks,
                                    F((double)air.KeyChanges / air.Ticks),
                                    F((double)air.NoKey / air.Ticks),
                                    air.Reversals,
                                    F(air.SpeedSum / air.Ticks),
                                    air.GroundGap,
                                    F(air.Enemy)));
                            }

                            s.Air = null;
                            s.LandedAt = t;
                        }
                        else if (air.Ticks < 90)
                        {
                            air.Ticks++;
                            var lr = keys & (PlayerKeys.Left | PlayerKeys.Right);
                            if (air.Ticks > 1 && lr != (air.LastKeys & (PlayerKeys.Left | PlayerKeys.Right)))
                                air.KeyChanges++;
                            if (lr == PlayerKeys.None)
                                air.NoKey++;
                            air.LastKeys = keys;
                            var sign = c.HSpeed > 1 ? 1 : c.HSpeed < -1 ? -1 : 0;
                            if (sign != 0 && air.LastSign != 0 && sign != air.LastSign)
                                air.Reversals++;
                            if (sign != 0)
                                air.LastSign = sign;
                            air.SpeedSum += Math.Abs(c.HSpeed);
                        }
                        else
                        {
                            s.Air = null;
                        }
                    }
                    else if (s.WasGround && !c.OnGround && c.VSpeed < 0 && s.Seen >= History)
                    {
                        var enemy = NearestEnemy(in world, i);
                        if (enemy > CombatRange)
                        {
                            var prior = s.Speed[(s.Seen - 1) % History];
                            var dir = prior > 0.5 ? 1 : prior < -0.5 ? -1 : (keys & PlayerKeys.Right) != 0 ? 1 : (keys & PlayerKeys.Left) != 0 ? -1 : 0;
                            var (context, featurePx) = Ahead(collision, in c, dir);
                            var speedMax = 0.0;
                            var still = 0;
                            var reversals = 0;
                            var last = 0;
                            for (var k = 1; k <= 30; k++)
                            {
                                var v = s.Speed[(s.Seen - k) % History];
                                if (k <= 10)
                                    speedMax = Math.Max(speedMax, Math.Abs(v));
                                if (k <= 15 && Math.Abs(v) < 0.5)
                                    still++;
                                var sign = v > 1 ? 1 : v < -1 ? -1 : 0;
                                if (sign != 0 && last != 0 && sign != last)
                                    reversals++;
                                if (sign != 0)
                                    last = sign;
                            }

                            var groundGap = t - s.LandedAt <= 60 ? t - s.LandedAt : -1;
                            var prefix = string.Join(',',
                                name, map, i, s.Participant, Csv(s.Controller), c.Class, context, featurePx);
                            s.Air = new Flight
                            {
                                Prefix = prefix,
                                Approach = string.Join(',', F(Math.Abs(prior)), F(speedMax), still, reversals),
                                Enemy = Math.Min(enemy, 9999),
                                Y0 = c.Y,
                                Dir = dir,
                                GroundGap = groundGap,
                                LastKeys = keys,
                                LastSign = dir,
                            };
                        }
                    }

                    s.Speed[s.Seen % History] = c.HSpeed;
                    s.Seen++;
                    s.WasGround = c.OnGround;
                }
            },
        };

        replayer.Replay(name, map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);
    }

    private static double NearestEnemy(in WorldState world, int self)
    {
        ref readonly var me = ref world.Characters[self];
        var best = double.PositiveInfinity;
        for (var j = 0; j < WorldState.MaxSlots; j++)
        {
            if (j == self || !world.Alive[j])
                continue;
            ref readonly var other = ref world.Characters[j];
            if (other.Team == me.Team)
                continue;
            var d = Math.Sqrt(((other.X - me.X) * (other.X - me.X)) + ((other.Y - me.Y) * (other.Y - me.Y)));
            best = Math.Min(best, d);
        }

        return best;
    }

    /// <summary>What is ahead of a take-off: something to jump onto, the end of the floor, or neither.</summary>
    private static (string Context, int Px) Ahead(CollisionWorld collision, in CharacterState c, int dir)
    {
        if (dir == 0)
            return ("still", 0);

        var box = CharacterBox.For(c.Class);
        var feet = (int)Math.Floor(c.Y) + box.Bottom;
        var front = (int)Math.Floor(c.X) + (dir > 0 ? box.Right : box.Left);

        for (var d = 1; d <= Look; d++)
        {
            var x = front + (dir * d);

            // A face at shin to head height: something to get onto or over.
            for (var h = 4; h <= 40; h += 2)
            {
                if (collision.IsSolidAt(x, feet - h))
                    return ("step", d);
            }

            // No floor within a step down: the ground ends here.
            var floor = false;
            for (var h = 1; h <= 12; h++)
            {
                if (collision.IsSolidAt(x, feet + h))
                {
                    floor = true;
                    break;
                }
            }

            if (!floor)
                return ("gap", d);
        }

        return ("flat", 0);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string? Flag(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
