using System.Collections.Generic;
using System.IO;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// <see cref="PortedSimulation.SetState"/> installs a whole world, which is
/// what lets a counterfactual run beside a replay: seed a second simulation
/// from the first, feed it inputs the recording does not contain, and the
/// difference is what those inputs did.
/// </summary>
/// <remarks>
/// <b>The instrument built on it cannot tell a partial copy from a real
/// result.</b> If <c>SetState</c> carried less than a whole world, two arms
/// seeded from one state would drift apart on their own and every number it
/// produced would be that drift wearing the answer's clothes. So the gate is
/// the degenerate case: identical inputs must give identical worlds, exactly,
/// and a differing input must not.
/// </remarks>
public sealed class PortedSimulationStateTests
{
    private const string Fixture = "reference-ctf_truefort-bots.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    /// <summary>
    /// Replays the fixture far enough in that the world is busy — characters
    /// moving, weapons fired — and hands back the simulation at that point.
    /// </summary>
    private static (PortedSimulation Sim, IReadOnlyList<TraceTick> Ticks, string Map) Midway(int tick)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.Null(stopped);
        Assert.True(ticks.Count > tick, $"fixture has {ticks.Count} ticks, needs more than {tick}");

        var simulation = new PortedSimulation(MapLoader);
        var reached = 0;

        new Replayer(simulation)
        {
            Compare = Fields.Trajectory,
            OnTick = t => reached = t,
        }.Replay(
            Fixture, trace.Header.Map, Take(ticks, tick), ReplayMode.ClosedLoop,
            stopAtFirstBadTick: false);

        Assert.Equal(tick - 1, reached);

        return (simulation, ticks, trace.Header.Map);
    }

    private static IReadOnlyList<TraceTick> Take(IReadOnlyList<TraceTick> ticks, int count)
    {
        var taken = new List<TraceTick>(count);

        for (var i = 0; i < count; i++)
            taken.Add(ticks[i]);

        return taken;
    }

    /// <summary>
    /// Two simulations seeded from one state and fed the same inputs stay
    /// identical, to the bit, for as long as they are fed.
    /// </summary>
    [Fact]
    public void ACopiedWorldFedTheSameInputsStaysIdentical()
    {
        ReferenceTree.RequireAvailable();

        var (source, ticks, map) = Midway(300);

        var a = new PortedSimulation(MapLoader);
        var b = new PortedSimulation(MapLoader);

        a.LoadMap(map);
        b.LoadMap(map);
        a.SetState(source.State);
        b.SetState(source.State);

        var inputs = new PlayerInput[64];
        var moved = false;

        for (var step = 0; step < 30; step++)
        {
            var slots = ticks[300 + step].Slots;

            for (var slot = 0; slot < inputs.Length; slot++)
                inputs[slot] = slot < slots.Length ? slots[slot].Input : PlayerInput.None;

            a.Tick(inputs);
            b.Tick(inputs);

            for (var slot = 0; slot < inputs.Length; slot++)
            {
                if (!a.TryGetState(slot, out var left))
                {
                    Assert.False(b.TryGetState(slot, out _), $"slot {slot} alive in one arm only");
                    continue;
                }

                Assert.True(b.TryGetState(slot, out var right), $"slot {slot} alive in one arm only");

                // Bit for bit: a copy that carried less than a whole world
                // would show up here as a divergence that grows.
                Assert.Equal(left.X, right.X);
                Assert.Equal(left.Y, right.Y);
                Assert.Equal(left.HSpeed, right.HSpeed);
                Assert.Equal(left.VSpeed, right.VSpeed);

                moved |= left.HSpeed != 0 || left.VSpeed != 0;
            }
        }

        // The control on the control: a world where nothing moves would satisfy
        // every assertion above while proving nothing at all.
        Assert.True(moved, "nothing moved in 30 ticks, so identical proves nothing");
    }

    /// <summary>
    /// And the gate bites: one differing input makes the two worlds differ.
    /// </summary>
    /// <remarks>
    /// Without this, a <c>SetState</c> that installed nothing — leaving both
    /// arms on an empty world where every slot is dead — would pass the test
    /// above perfectly.
    /// </remarks>
    [Fact]
    public void ACopiedWorldFedADifferentInputDiverges()
    {
        ReferenceTree.RequireAvailable();

        var (source, ticks, map) = Midway(300);

        var a = new PortedSimulation(MapLoader);
        var b = new PortedSimulation(MapLoader);

        a.LoadMap(map);
        b.LoadMap(map);
        a.SetState(source.State);
        b.SetState(source.State);

        // Find a slot that is alive and standing on something, so pressing a
        // direction actually moves it.
        var subject = -1;

        for (var slot = 0; slot < 64 && subject < 0; slot++)
        {
            if (a.TryGetState(slot, out _))
                subject = slot;
        }

        Assert.True(subject >= 0, "the fixture has no live character at tick 300");

        var real = new PlayerInput[64];
        var forced = new PlayerInput[64];

        var diverged = false;

        for (var step = 0; step < 30 && !diverged; step++)
        {
            var slots = ticks[300 + step].Slots;

            for (var slot = 0; slot < real.Length; slot++)
            {
                real[slot] = slot < slots.Length ? slots[slot].Input : PlayerInput.None;
                forced[slot] = real[slot];
            }

            // One slot holds left, whatever the recording said.
            forced[subject] = new PlayerInput(
                PlayerKeys.Left, real[subject].NetAimDirection, real[subject].AimDistance);

            a.Tick(real);
            b.Tick(forced);

            if (a.TryGetState(subject, out var left) && b.TryGetState(subject, out var right))
                diverged = left.X != right.X || left.Y != right.Y;
        }

        Assert.True(diverged, "holding left for 30 ticks changed nothing, so the gate cannot fail");
    }
}
