using System.Globalization;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The wall stop that <c>gg_debug</c>'s t523 residue is born at — reduced to a
/// scenario with nothing in it, and pinned to the last bit.
/// </summary>
/// <remarks>
/// <b>What this file is for.</b> <c>GgDebugSplashOpenFindingTests</c> pins the
/// divergence at t523; a trace carries x to only a fifth of a pixel, so the
/// reference's exact position is unrecoverable from any recording. This
/// reproduces the whole of the interesting part — slot 4's walk from t383 —
/// with no trace, no replayer and no other player, so that the engine can be
/// asked the same question directly: <c>gg2-samplers/probes/probe-wall-stop.js</c>
/// runs exactly this scenario in GM8 and prints the difference.
/// <para>
/// <b>The stop is a WALL, not friction.</b> The recording holds key 0x20 —
/// RIGHT, held — on every tick from t388 through t406, and <c>hspeed</c> goes
/// to zero at t404 anyway; the acceleration formula itself, which
/// <see cref="PhysicsSampleTests"/> already measures exact, is not the cause.
/// </para>
/// <para>
/// <b>And the "+0.4865 residual that grows and then freezes" is not a
/// divergence at all.</b> It is <c>x*5 - 3147</c> for the port's own resting
/// position: the character comes to rest at 629.4972928…, whose <c>x*5</c> is
/// 3147.4865. It grows while the character walks because the character is
/// walking, and freezes when it stops because it has stopped. Nothing in it is
/// a measurement of the reference, and reading it as one is what made the
/// residue look like it was born during the walk.
/// </para>
/// </remarks>
public sealed class WallStopTests
{
    private const string Map = "gg_debug";

    /// <summary>Slot 4's position at t383: raw x 2940, raw y 840.</summary>
    private const double StartX = 588;

    private const double StartY = 168;

    /// <summary>
    /// Where the port comes to rest, exactly. Shared with
    /// <c>gg2-samplers/probes/probe-wall-stop.js</c>, which prints the engine's answer
    /// beside it.
    /// </summary>
    private const double RestX = 629.4972928249938;

    private sealed class Held(PlayerInput input) : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => input;
    }

    private static (ScenarioWorld World, int Slot) Walk(int ticks)
    {
        var scenario = ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, null);
        var slot = scenario.Place(PlayerClass.Soldier, Team.Red, StartX, StartY);

        scenario.Drive(slot, new Held(new PlayerInput(PlayerKeys.Right, 0, 0)));
        scenario.Step(ticks);

        return (scenario, slot);
    }

    /// <summary>
    /// <b>The free/blocked boundary at that wall is exactly x = 629.5 — a
    /// rounding tie</b>, and the character rests 0.0027 px short of it.
    /// </summary>
    /// <remarks>
    /// <c>RoundPosition</c> is half-to-even, so 629.5 becomes 630, which is
    /// inside the wall. That places the resting position on the far side of a
    /// tie from the first solid cell, which is worth knowing for two reasons:
    /// it is why <c>round(x*5)</c> is 3147 for the port and for the reference no
    /// matter which rung of the back-off ladder either stops on, and it is why
    /// row 6c's half-tie candidate looked so plausible before it was falsified.
    /// </remarks>
    [Fact]
    public void TheWallsFreeBlockedBoundaryIsExactlyAtARoundingTie()
    {
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var world = new CollisionWorld(map);
        var box = CharacterBox.For(PlayerClass.Soldier);

        Assert.True(world.PlaceFreeInMask(box, 629.4, StartY));
        Assert.True(world.PlaceFreeInMask(box, 629.49, StartY));
        Assert.False(world.PlaceFreeInMask(box, 629.5, StartY));
        Assert.False(world.PlaceFreeInMask(box, 630, StartY));
    }

    /// <summary>
    /// A soldier holding RIGHT from (588, 168) hits the wall on tick 13 and then
    /// <b>creeps into it for four more ticks with <c>hspeed</c> already zero</b>.
    /// </summary>
    /// <remarks>
    /// The creep is <c>good_move_contact_solid</c>'s back-off ladder: the walk
    /// keys are still held, so the character re-accelerates a fraction every
    /// tick, moves until <c>place_free</c> fails, and backs off in
    /// <c>(i/8)²</c> rungs. Each tick gains one rung less than the last —
    /// 0.1717, then 0.0107 three times — and then the smallest rung no longer
    /// fits and the position is fixed for ever.
    /// <para>
    /// <b>The ladder is not an uncovered arm.</b> Measured by counting it
    /// inside <see cref="PhysicsSampleTests"/>: the 64 engine
    /// scenarios enter the back-off branch 1,354 times and come to rest on a
    /// rung 198 times, and the port is exact on all of them. So the arithmetic
    /// below is gated against the engine everywhere except at this particular
    /// tie.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSoldierHitsTheWallOnTickThirteenAndThenCreepsFourMoreTimes()
    {
        // Fourteen steps, so the last one is tick 13 counting from zero — the
        // tick the wall takes hspeed away outright.
        var (scenario, slot) = Walk(14);

        Assert.Equal(0, scenario.State.Characters[slot].HSpeed);
        var atWall = scenario.State.Characters[slot].X;
        Assert.Equal(629.2933979831873, atWall);

        var seen = new List<double>();

        for (var i = 0; i < 6; i++)
        {
            scenario.Step();
            seen.Add(scenario.State.Characters[slot].X);
        }

        // Four gains, then nothing — with hspeed zero throughout.
        Assert.Equal(
            [629.4650989026032, 629.4758302100668, 629.4865615175303, RestX, RestX, RestX],
            seen);

        Assert.Equal(0, scenario.State.Characters[slot].HSpeed);
    }

    /// <summary>
    /// The resting position, pinned to the last bit — this is the number
    /// <c>gg2-samplers/probes/probe-wall-stop.js</c> compares the engine against.
    /// </summary>
    /// <remarks>
    /// <b>If this test fails, do not repair it and move on.</b> It is the
    /// baseline of an open measurement: the probe's whole output is
    /// <c>engine − 629.4972928249938</c>, and a port change that moves this
    /// number invalidates a comparison somebody may already have run. Re-run the
    /// probe, put the new pair in both places, and say so.
    /// <para>
    /// Both simulations report <c>round(x*5) = 3147</c> from anywhere in
    /// [629.3, 629.5), which is the whole reason no fixture can see this.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRestingPositionIsPinnedToTheLastBitAndTheWireCannotSeeIt()
    {
        var (scenario, slot) = Walk(30);
        var x = scenario.State.Characters[slot].X;

        Assert.Equal(RestX, x);
        Assert.Equal("629.4972928249938", x.ToString("R", CultureInfo.InvariantCulture));

        // What the wire would have said, and what the recording did say at t404
        // and t411: 3147, from either side of every ladder rung.
        Assert.Equal(3147, (long)Math.Round(x * 5, MidpointRounding.ToEven));
    }
}
