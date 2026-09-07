using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// An anchor installs an END-OF-TICK state, so <c>yprevious</c> has to travel
/// with it.
/// </summary>
/// <remarks>
/// <b>The stuck slot in <c>docs/PARITY.md</c> was this, and nothing was red.</b>
/// <c>Anchor</c> wrote a character's position from the wire and left
/// <see cref="Gg2.Simulation.CharacterState.YPrevious"/> at whatever the
/// replay's own copy last finished a tick at. The dropdown-platform push-out
/// reads <c>round(y - yprevious)</c> to decide whether the character was
/// already inside the platform before this tick's move, so a stale one does not
/// merely blur a number — it flips that answer, and a character the reference
/// is falling THROUGH gets lifted out on top of the platform. It then sits
/// there with hspeed and vspeed zero, re-seated by every following anchor,
/// which is the constant hundred-odd-unit y error the phase gate reported as a
/// phase problem.
/// <para>
/// ⚠️ <b>Both of these gate the REPLAYER, not the simulation.</b> The trace is
/// this server's own recording and the replay is this build re-simulating it,
/// so a green result here says the harness anchors correctly — it is not a
/// parity claim against the reference. `MeasuredAcross` records what the
/// checked-in trace did on both sides of the fix.
/// </para>
/// </remarks>
public sealed class AnchorYPreviousTests
{
    /// <summary>What the checked-in trace scored before and after the fix.</summary>
    /// <remarks>
    /// p99 y = 90 before, 2 after. Nine of the eleven traces caught by
    /// <c>OutlierHuntTests.Hunt</c> collapsed the same way (90/90/90/90/90,
    /// 120/120, 60, 180 → 2/2/2/2/2, 3/2, 3, 16); two did not move at all
    /// (48 and 74), so the outlier had more than one cause and this closes one
    /// of them.
    /// </remarks>
    private const long MeasuredAcross = 90;

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static CharacterBlock At(ushort y) => new()
    {
        Keys = PlayerKeys.None,
        NetAimDirection = 0,
        AimDistance = 0,
        HasPosition = true,
        X = 1000,
        Y = y,
    };

    /// <summary>
    /// The invariant itself, in both directions: a first anchor onto an empty
    /// slot and a second onto a slot that already has a character.
    /// </summary>
    /// <remarks>
    /// The second is the one that was broken. A fresh character starts at
    /// <c>yprevious = 0</c>, so the first anchor happened to look wrong too —
    /// but it is the SECOND, onto a live character carrying a real previous y
    /// from somewhere else on the map, that produced the stick.
    /// </remarks>
    [Fact]
    public void AnAnchorCarriesYPreviousWithThePosition()
    {
        var simulation = new PortedSimulation(MapLoader);

        simulation.SetCharacter(3, At(4810));
        Assert.True(simulation.TryGetState(3, out var first));
        Assert.Equal(first.Y, first.YPrevious);

        // Anchored a long way from where it was: exactly the case the stale
        // value made into a wrong collision answer rather than a small error.
        simulation.SetCharacter(3, At(5710));
        Assert.True(simulation.TryGetState(3, out var second));
        Assert.Equal(second.Y, second.YPrevious);
        Assert.NotEqual(first.Y, second.Y);
    }

    /// <summary>
    /// And the behaviour, on a trace that reproduced the stick.
    /// </summary>
    /// <remarks>
    /// Kept by <c>OutlierHuntTests.Hunt</c> under load. The threshold is well
    /// inside the gap between the two measured values rather than on either of
    /// them: a re-seated slot reports about <see cref="MeasuredAcross"/>, and a
    /// replay anchoring correctly reports single digits, so anything in between
    /// separates them without pinning ordinary drift.
    /// </remarks>
    [Fact]
    public void AnAnchorDoesNotStrandASlotOnADropdownPlatform()
    {
        const string name = "port-gg_debug-anchoronplatform";
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".trace");
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out _);

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory,
            InputLag = 0,
        }.Replay(name, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        var y = report.PercentileYError(99);

        Assert.True(
            y < 20,
            $"p99 y error {y}; it was {MeasuredAcross} with yprevious left stale. A slot is "
            + "being re-seated on a dropdown platform the recording falls through — check "
            + "that PortedSimulation.SetCharacter still carries YPrevious with the position.");
    }
}
