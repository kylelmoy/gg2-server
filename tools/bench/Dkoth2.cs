using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>Where do the bots actually stand in a dual-KotH match?</summary>
public static class DkothWhere
{
    public static void Run(string maps, string nav)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "dkoth_atalia", Bots = 8, Seed = 909,
            CapLimit = 1, TimeLimitMinutes = 10, KillLimit = 400, MaxTicks = 30 * 60 * 10,
        };

        var match = HeadlessMatch.Load(options, maps, nav);
        long redOnRed = 0, redOnBlue = 0, blueOnBlue = 0, blueOnRed = 0;
        var ownerChanges = 0;
        var lastOwner0 = Team.Red;
        var lastOwner1 = Team.Blue;
        var lockedTicks0 = 0;
        var lockedTicks1 = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            ref readonly var red = ref match.State.ControlPoints[0];
            ref readonly var blue = ref match.State.ControlPoints[1];

            redOnRed += red.RedPresence;
            blueOnRed += red.BluePresence;
            redOnBlue += blue.RedPresence;
            blueOnBlue += blue.BluePresence;

            if (red.Locked) lockedTicks0++;
            if (blue.Locked) lockedTicks1++;

            if (red.Team != lastOwner0) { ownerChanges++; lastOwner0 = red.Team; }
            if (blue.Team != lastOwner1) { ownerChanges++; lastOwner1 = blue.Team; }
        }

        Console.WriteLine($"dkoth_atalia, {match.Tick} ticks ({match.Tick / 30.0 / 60:0.0} min), outcome {match.Outcome}");
        Console.WriteLine($"  bot-ticks standing on RED's hill:   red {redOnRed,8}   blue {blueOnRed,8}");
        Console.WriteLine($"  bot-ticks standing on BLUE's hill:  red {redOnBlue,8}   blue {blueOnBlue,8}");
        Console.WriteLine($"  hill owner changes: {ownerChanges}");
        Console.WriteLine($"  ticks locked: red hill {lockedTicks0}, blue hill {lockedTicks1}");
        Console.WriteLine($"  final timers: red {match.State.RedTimer}, blue {match.State.BlueTimer} (start {Rounds.KothHoldTicks})");
    }
}
