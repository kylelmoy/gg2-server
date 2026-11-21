// Replays --record traces and writes one CSV row per (enemy rocket, character it
// was fired near): whether the shot was on target when it was first seen, how
// long it had to impact, what happened to it, and when the target first reacted.
//
// The question it exists for is how often a person dodges a rocket that was going
// to hit them, as a function of the warning they had — the curve the bots'
// difficulty has to be tuned against (docs/TRAPS-BOTS.md, "the difficulty chain").
//
// "On target" uses the scripted dodge's own model (BotReflex.RocketDodge): the
// rocket is a straight line at constant speed, the target carries its horizontal
// speed and stays at its height, and the body box is BOT_BODY_* plus the rocket's
// radius. "Impact" is the first tick that model says the rocket enters the box.
//
// Outcome, from the rocket's last observed position:
//   direct  its last tick's segment passes through the target's body box
//   splash  it ended within the 65 px blast radius of the target's origin
//   miss    neither
//
// Reaction: the first tick after the rocket was seen on which the target pressed
// JUMP (an edge) or changed its LEFT/RIGHT keys. Humans strafe all the time, so
// this is an upper bound on how fast they reacted, never a measurement of it.
//
// Output: CSV on stdout. The traces name real players: keep them and the CSV out
// of the repository.

using System.Collections.Concurrent;
using System.Globalization;
using GG2.Maps;
using GG2.Parity;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

internal static class Program
{
    private const int Horizon = 40;
    private const double NearRange = 600;
    private const double BlastRadius = 65;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("dodgescan <trace-or-directory>... [--maps <path>] > out.csv");
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

        Parallel.ForEach(files, file =>
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

        Console.WriteLine(
            "trace,tick,shooter,shooterkind,target,targetkind,targetclass,distance,grounded,impact,outcome,react,life");

        foreach (var row in rows.OrderBy(r => r, StringComparer.Ordinal))
            Console.WriteLine(row);

        Console.Error.WriteLine($"{read} trace(s) scanned, {failed} failed");
        return read == 0 ? 1 : 0;
    }

    private static void Scan(string file, string maps, ConcurrentBag<string> rows)
    {
        var trace = Trace.Decode(File.ReadAllBytes(file));

        if (trace.Roster.Count == 0)
            throw new InvalidDataException("no roster labels");

        LevelMap Load(string name)
        {
            var path = Path.Combine(maps, name + ".png");
            if (!File.Exists(path))
                path = Path.Combine(maps, "duel", name + ".png");

            return LevelMap.FromPng(name, File.ReadAllBytes(path));
        }

        WorldShape ShapeFor(string name) => GameModes.ShapeOf(Load(name).ForArea(1));

        var ticks = TraceParser.Parse(trace, ShapeFor(trace.Header.Map), out _, out _, ShapeFor);
        var simulation = new PortedSimulation(Load);
        var notes = trace.Roster.OrderBy(n => n.Tick).ToArray();
        var name = Path.GetFileName(file);

        // Who is in each slot, as of the latest label for it.
        var who = new (string Name, TraceParticipant Kind)[WorldState.MaxSlots];
        var noteIndex = 0;

        var tracked = new Dictionary<(ProjectileHandle Rocket, int Target), Track>();
        var previousKeys = new PlayerKeys[WorldState.MaxSlots];

        var halfWidth = BotConstants.BodyHalfw + BotConstants.RocketRadius;
        var up = BotConstants.BodyUp + BotConstants.RocketRadius;
        var down = BotConstants.BodyDown + BotConstants.RocketRadius;

        bool InBox(double rx, double ry, double cx, double cy) =>
            Math.Abs(rx - cx) <= halfWidth && ry >= cy - up && ry <= cy + down;

        void Emit(Track t, string outcome)
        {
            rows.Add(string.Join(',',
                name, t.Tick.ToString(CultureInfo.InvariantCulture),
                Csv(t.ShooterName), t.ShooterKind,
                Csv(t.TargetName), t.TargetKind, t.TargetClass,
                t.Distance.ToString("F0", CultureInfo.InvariantCulture),
                t.Grounded ? 1 : 0, t.Impact, outcome, t.React, t.Life));
        }

        var replayer = new Replayer(simulation)
        {
            Compare = Fields.Trajectory,
            OnTick = tick =>
            {
                while (noteIndex < notes.Length && notes[noteIndex].Tick <= tick)
                {
                    var note = notes[noteIndex++];
                    if (note.Slot < WorldState.MaxSlots)
                        who[note.Slot] = (note.Name, note.Participant);
                }

                ref readonly var world = ref simulation.State;
                var traceSlots = ticks[tick].Slots;
                var seen = new HashSet<(ProjectileHandle, int)>();

                for (var i = 0; i < world.LiveProjectileCount; i++)
                {
                    var handle = world.LiveProjectileHandle(i);
                    ref readonly var rocket = ref world.Projectiles[handle.Index];

                    if (rocket.Kind != ProjectileKind.Rocket)
                        continue;

                    for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                    {
                        if (!world.Alive[slot])
                            continue;

                        ref readonly var target = ref world.Characters[slot];
                        if (target.Team == rocket.Team)
                            continue;

                        var key = (handle, slot);

                        if (!tracked.TryGetValue(key, out var track))
                        {
                            var distance = Math.Sqrt(
                                ((rocket.X - target.X) * (rocket.X - target.X))
                                + ((rocket.Y - target.Y) * (rocket.Y - target.Y)));

                            if (distance > NearRange)
                                continue;

                            var impact = -1;
                            for (var t = 1; t <= Horizon; t++)
                            {
                                if (InBox(
                                    rocket.X + (rocket.Motion.HSpeed * t), rocket.Y + (rocket.Motion.VSpeed * t),
                                    target.X + (target.HSpeed * t), target.Y))
                                {
                                    impact = t;
                                    break;
                                }
                            }

                            var shooter = rocket.OwnerSlot < WorldState.MaxSlots ? who[rocket.OwnerSlot] : ("?", TraceParticipant.Unknown);
                            track = new Track
                            {
                                Tick = tick,
                                ShooterName = shooter.Item1 ?? "?",
                                ShooterKind = shooter.Item2,
                                TargetName = who[slot].Name ?? "?",
                                TargetKind = who[slot].Kind,
                                TargetClass = target.Class,
                                Distance = distance,
                                Grounded = target.OnGround,
                                Impact = impact,
                                StartKeys = previousKeys[slot],
                            };
                            tracked[key] = track;
                        }

                        seen.Add(key);
                        track.Life++;
                        track.LastX = rocket.X;
                        track.LastY = rocket.Y;
                        track.LastHSpeed = rocket.Motion.HSpeed;
                        track.LastVSpeed = rocket.Motion.VSpeed;
                        track.TargetX = target.X;
                        track.TargetY = target.Y;

                        var keys = slot < traceSlots.Length ? traceSlots[slot].Input.Keys : PlayerKeys.None;
                        if (track.React < 0 && track.Life > 1)
                        {
                            var jumped = (keys & PlayerKeys.Jump) != 0 && (previousKeys[slot] & PlayerKeys.Jump) == 0;
                            var turned = (keys & (PlayerKeys.Left | PlayerKeys.Right))
                                != (track.StartKeys & (PlayerKeys.Left | PlayerKeys.Right));
                            if (jumped || turned)
                                track.React = track.Life - 1;
                        }
                    }
                }

                // Whatever was tracked and is not here this tick has ended.
                foreach (var (key, track) in tracked.Where(p => !seen.Contains(p.Key)).ToList())
                {
                    tracked.Remove(key);

                    var outcome = "miss";
                    for (var s = 0.0; s <= 1.0; s += 0.125)
                    {
                        if (InBox(track.LastX + (track.LastHSpeed * s), track.LastY + (track.LastVSpeed * s), track.TargetX, track.TargetY))
                        {
                            outcome = "direct";
                            break;
                        }
                    }

                    if (outcome == "miss")
                    {
                        var dx = track.LastX - track.TargetX;
                        var dy = track.LastY - track.TargetY;
                        if ((dx * dx) + (dy * dy) <= BlastRadius * BlastRadius)
                            outcome = "splash";
                    }

                    Emit(track, outcome);
                }

                for (var slot = 0; slot < WorldState.MaxSlots && slot < traceSlots.Length; slot++)
                    previousKeys[slot] = traceSlots[slot].Input.Keys;
            },
        };

        replayer.Replay(name, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);
    }

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string? Flag(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private sealed class Track
    {
        public int Tick;
        public string ShooterName = "";
        public TraceParticipant ShooterKind;
        public string TargetName = "";
        public TraceParticipant TargetKind;
        public PlayerClass TargetClass;
        public double Distance;
        public bool Grounded;
        public int Impact;
        public PlayerKeys StartKeys;
        public int Life;
        public int React = -1;
        public double LastX, LastY, LastHSpeed, LastVSpeed, TargetX, TargetY;
    }
}
