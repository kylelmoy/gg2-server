using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// What happens when something inside a tick throws.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Before this there was no guard anywhere.</b> <c>Run</c> called
/// <c>Tick</c> bare, so an exception from any stage reached <c>Main</c> and
/// killed the process. On a desktop with the host watching, that is a crash
/// they see; on a 24/7 server it is an outage until somebody notices, and the
/// cause is a stack trace nobody kept.
/// </para>
/// <para>
/// ⚠️ <b>What these tests prove and what they do not.</b> The containment is
/// proven end to end: a faulting decision source is removed and the server
/// carries on with everyone else still playing. The <em>escalation</em> —
/// <c>Run</c> stopping the server once faults become a pattern — is proven at
/// <see cref="TickFaultBudget"/>, which is the decision, not at <c>Run</c>,
/// which is the six lines that call it. Nothing here drives a real server into
/// its stop path, and saying so is the point: a test that did would need a fault
/// injector in production code, and the reason to want one is that this gap is
/// exactly the kind a green suite hides.
/// </para>
/// </remarks>
public sealed class TickFaultTests
{
    /// <summary>A decision source that throws the moment it is asked anything.</summary>
    private sealed class FaultingBot : IInputSource, ICommandSource
    {
        public int Polls { get; private set; }

        public PlayerInput Poll(in WorldView world, int slot, int tick) => default;

        public void PollCommands(
            in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
        {
            Polls++;
            throw new InvalidOperationException("this bot is broken");
        }
    }

    private static ServerHarness Harness() => new(options =>
    {
        options.Map = "ctf_truefort";
        options.MapRotation = ["ctf_truefort"];
        options.ShuffleRotation = ShuffleMode.None;
    });

    /// <summary>
    /// One faulting decision source costs that one player, not the server.
    /// </summary>
    /// <remarks>
    /// The bot is removed rather than retried: a source that threw on this
    /// tick's world state will throw on the next one's, and a stack trace every
    /// 33 ms is the second outage.
    /// </remarks>
    [Fact]
    public void AFaultingBotIsRemovedAndTheServerKeepsTicking()
    {
        using var harness = Harness();
        var broken = new FaultingBot();

        var slot = harness.Server.AddBot("broken", Team.Red, PlayerClass.Scout, broken);
        var before = harness.Server.RosterCount;

        harness.Tick(5);

        Assert.DoesNotContain(slot, harness.Server.BotSlots);
        Assert.Equal(before - 1, harness.Server.RosterCount);

        // Polled once and then never again — the removal is what stops the
        // repeat, and a guard that merely swallowed would show a climbing count.
        Assert.Equal(1, broken.Polls);

        // ⛔ Logged at ERROR with the exception, not swallowed. "It kept running"
        // and "nothing was wrong" are different claims and only the first is
        // true.
        Assert.Contains(
            harness.Log.Errors,
            line => line.Contains("removing bot", StringComparison.Ordinal));

        Assert.Contains(
            harness.Log.Exceptions,
            e => e is InvalidOperationException { Message: "this bot is broken" });

        // And no tick was lost: the fault never reached Run's net.
        Assert.Equal(0, harness.Server.TickFaults);
    }

    /// <summary>
    /// ⚠️ <b>The control case.</b> The test above asserts a bot was removed,
    /// which is the shape that also passes if the guard removes every bot. A
    /// healthy bot on the same server, through the same pass, must survive.
    /// </summary>
    [Fact]
    public void OnlyTheFaultingBotGoes()
    {
        using var harness = Harness();

        harness.Server.AddBot("fine", Team.Blue, PlayerClass.Scout, new SilentBot());
        harness.Server.AddBot("broken", Team.Red, PlayerClass.Scout, new FaultingBot());
        harness.Server.AddBot("also-fine", Team.Blue, PlayerClass.Soldier, new SilentBot());

        harness.Tick(5);

        Assert.Equal(2, harness.Server.BotSlots.Count);
        Assert.Equal(0, harness.Server.TickFaults);
    }

    /// <summary>A source that does nothing and never throws.</summary>
    private sealed class SilentBot : IInputSource, ICommandSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => default;

        public void PollCommands(
            in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
        {
        }
    }
}

/// <summary>
/// The rule for how much failure the server absorbs before it stops.
/// </summary>
/// <remarks>
/// ⚠️ <b>Catching everything for ever is worse than crashing, not better.</b>
/// A tick that threw left the simulation part-way through a stage; if that keeps
/// happening the server is up, listed in the lobby, accepting players and
/// simulating nothing, thirty times a second. Nobody is paged for that — the
/// players just leave, and the log says the same line for ever.
/// </remarks>
public sealed class TickFaultBudgetTests
{
    /// <summary>A single fault, or a few, is absorbed.</summary>
    [Fact]
    public void FaultsWithinTheAllowanceAreAbsorbed()
    {
        var budget = new TickFaultBudget();

        for (var i = 0; i < TickFaultBudget.Allowance; i++)
            Assert.True(budget.RecordAndContinue(1000), $"fault {i} should be survivable");

        Assert.Equal(TickFaultBudget.Allowance, budget.Total);
    }

    /// <summary>Past the allowance, inside the window, the server stops.</summary>
    [Fact]
    public void APatternOfFaultsStopsTheServer()
    {
        var budget = new TickFaultBudget();

        for (var i = 0; i < TickFaultBudget.Allowance; i++)
            Assert.True(budget.RecordAndContinue(1000));

        Assert.False(budget.RecordAndContinue(1000));
    }

    /// <summary>
    /// The window rolls, so a fault an hour ago does not count against one now.
    /// </summary>
    /// <remarks>
    /// Without this a server that survived three unrelated hiccups over a week
    /// would stop on the fourth — which is a budget that grows more brittle the
    /// longer the server stays up, exactly backwards.
    /// </remarks>
    [Fact]
    public void FaultsFromAnOlderWindowDoNotCount()
    {
        var budget = new TickFaultBudget();
        var window = (long)TickFaultBudget.Window.TotalMilliseconds;

        for (var i = 0; i < TickFaultBudget.Allowance; i++)
            Assert.True(budget.RecordAndContinue(0));

        // One past the allowance, but a window later.
        Assert.True(budget.RecordAndContinue(window + 1));

        // The total is cumulative even though the window reset: it is what the
        // operator is shown, and "three faults today" must not read as "one".
        Assert.Equal(TickFaultBudget.Allowance + 1, budget.Total);
    }

    /// <summary>
    /// A clock that steps backwards cannot widen the window.
    /// </summary>
    /// <remarks>
    /// The server passes <c>Environment.TickCount64</c>, which is monotonic, so
    /// this is defence in depth — but a budget that can be reset by a clock is
    /// not a budget.
    /// </remarks>
    [Fact]
    public void AClockThatGoesBackwardsDoesNotResetTheWindow()
    {
        var budget = new TickFaultBudget();

        for (var i = 0; i < TickFaultBudget.Allowance; i++)
            Assert.True(budget.RecordAndContinue(100_000));

        Assert.False(budget.RecordAndContinue(0));
    }
}
