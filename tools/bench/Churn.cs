using Gg2.Bots;
using Gg2.Simulation;
using Gg2.Protocol;

namespace Bench;

/// <summary>
/// How often a bot CHANGES its input, which is what a client has to predict.
/// </summary>
/// <remarks>
/// ⛔ <b>This exists because a playtester reported bots stuttering around jumps —
/// "left/right even though the jump should move right" — and could not tell
/// whether the bot was genuinely inputting that or the client was mispredicting
/// it.</b> Those are opposite problems and the difference is measurable here:
/// the server's own input stream either reverses often or it does not, and no
/// client is involved in finding out.
/// <para>
/// ⚠️ <b>Input churn is a NETCODE quantity, not a style one.</b> The wire sends
/// keys and aim; between the position updates (every 7th tick —
/// <c>GameServerBeginStep.gml:63</c>) every client dead-reckons each character
/// forward from the last keys it heard. A character whose keys are stable
/// predicts perfectly. One that reverses horizontal every few ticks is
/// mispredicted for most of the gap, every gap, and looks exactly like lag while
/// the server is comfortably inside its budget.
/// </para>
/// <para>
/// ⚠️ <b>The airborne rows are the ones to read.</b> A ground reversal is a step;
/// an AIRBORNE horizontal reversal changes a ballistic arc the client has already
/// drawn, which is the specific thing that was reported.
/// </para>
/// <para>
/// ⛔ <b>What this CANNOT tell you.</b> It measures the server's inputs only. If
/// churn comes back low, the stutter is the client mispredicting and this is the
/// wrong instrument for it — a parity question, <c>docs/PARITY.md</c>. Say which
/// half the number rules out rather than treating a low number as "no problem".
/// </para>
/// </remarks>
public static class ChurnBench
{
    private sealed class Tally
    {
        internal long Ticks;
        internal long HorizontalChanges;
        internal long HorizontalReversals;
        internal long AirborneTicks;
        internal long AirborneReversals;
        internal long AimChanges;
        internal double AimTotalDelta;

        internal double PerSecond(long n) => Ticks == 0 ? 0 : n / (Ticks / 30.0);
    }

    public static void Run(string maps, string nav, string npz, int perMap)
    {
        var list = Environment.GetEnvironmentVariable("BENCH_FOCUS") is { Length: > 0 } focus
            ? focus.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : ["ctf_2dfort"];

        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "2");
        var bots = int.TryParse(Environment.GetEnvironmentVariable("BENCH_BOTS"), out var b) && b > 0 ? b : 8;

        // ⚠️ The policy is loaded with the stamp check BYPASSED, because this asks
        // what the live server's bots do and the live server is running it that
        // way. A churn number measured on a configuration nobody is playing would
        // answer a question nobody asked.
        var policy = PolicyNetwork.Load(npz, ignoreSimulationStamp: true);

        Console.WriteLine(
            $"input churn per bot-second, {bots} bots, {perMap} matches a map, cap {cap} min");
        Console.WriteLine(
            "arm 1 all-scripted   arm 2 policy hybrid   arm 3 search hybrid");
        Console.WriteLine();

        Console.WriteLine(
            $"{"map",-16} {"arm",-4} {"horiz/s",9} {"reversal/s",11} "
            + $"{"AIR rev/s",10} {"aim chg/s",10} {"deg/chg",8}");

        foreach (var map in list)
        {
            for (var arm = 1; arm <= 3; arm++)
            {
                var tally = Measure(
                    map, maps, nav, perMap, cap, bots,
                    arm == 2 ? policy : null,
                    arm == 3 ? new RolloutOptions(Threads: 4) : null);

                Console.WriteLine(
                    $"{map,-16} {arm,-4} {tally.PerSecond(tally.HorizontalChanges),9:0.00} "
                    + $"{tally.PerSecond(tally.HorizontalReversals),11:0.00} "
                    + $"{(tally.AirborneTicks == 0 ? 0 : tally.AirborneReversals / (tally.AirborneTicks / 30.0)),10:0.00} "
                    + $"{tally.PerSecond(tally.AimChanges),10:0.00} "
                    + $"{(tally.AimChanges == 0 ? 0 : tally.AimTotalDelta / tally.AimChanges),8:0.0}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            "A client dead-reckons each character from the last keys it heard until the next");
        Console.WriteLine(
            "position update, which is every 7th tick - so roughly 4.3 gaps a second. Reversals");
        Console.WriteLine(
            "per second at or above that rate mean the average gap contains a direction change");
        Console.WriteLine(
            "the client cannot know about, and every one of those is a visible correction.");
        Console.WriteLine();
        Console.WriteLine(
            "⚠️ Low churn does NOT clear the bots - it moves the question to whether the client");
        Console.WriteLine(
            "   and server agree on the physics, which this cannot see. docs/PARITY.md.");
    }

    private static Tally Measure(
        string map, string maps, string nav, int count, int cap, int bots,
        PolicyNetwork? policy, RolloutOptions? search)
    {
        var tally = new Tally();

        for (var i = 0; i < count; i++)
        {
            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = bots,
                Seed = (uint)(9000 + (map.Length * 131) + (i * 37)),
                CapLimit = 1,
                TimeLimitMinutes = cap,
                KillLimit = 40,
                MaxTicks = 30 * 60 * cap,
                AllowedClasses = [.. PolicyBot.TrainedClasses],
                Policy = policy,
                SearchMicro = search,
                PolicyTeam = Team.Red,
            };

            var match = HeadlessMatch.Load(options, maps, nav);

            var lastHorizontal = new int[WorldState.MaxSlots];
            var lastAim = new int[WorldState.MaxSlots];
            var seen = new bool[WorldState.MaxSlots];

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (ScriptedBot.Of(match.SourceOf(slot)) is null || !match.State.IsAlive(slot))
                    {
                        seen[slot] = false;
                        continue;
                    }

                    ref readonly var character = ref match.State.Characters[slot];

                    // -1, 0 or +1: what the wire carries and what a client
                    // extrapolates from.
                    var horizontal = (character.KeyState & PlayerKeys.Right) != 0 ? 1
                        : (character.KeyState & PlayerKeys.Left) != 0 ? -1
                        : 0;

                    var aim = character.NetAimDirection;
                    var airborne = !character.OnGround;

                    if (airborne)
                        tally.AirborneTicks++;

                    if (seen[slot])
                    {
                        if (horizontal != lastHorizontal[slot])
                        {
                            tally.HorizontalChanges++;

                            // ⚠️ A REVERSAL is left->right or right->left, not a
                            // release. Releasing a key leaves the client's
                            // extrapolation merely too fast; reversing makes it
                            // point the wrong way, which is the visible one.
                            if (horizontal != 0 && lastHorizontal[slot] != 0)
                            {
                                tally.HorizontalReversals++;

                                if (airborne)
                                    tally.AirborneReversals++;
                            }
                        }

                        if (aim != lastAim[slot])
                        {
                            tally.AimChanges++;

                            var delta = Math.Abs(aim - lastAim[slot]);
                            tally.AimTotalDelta += Math.Min(delta, 65536 - delta) * 360.0 / 65536.0;
                        }
                    }

                    lastHorizontal[slot] = horizontal;
                    lastAim[slot] = aim;
                    seen[slot] = true;
                    tally.Ticks++;
                }
            }
        }

        return tally;
    }
}
