using System;
using System.Collections.Generic;
using System.IO;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The messages a replay can only learn from a broadcast — the three client
/// commands (zoom, build, eat) and autobalance — reach the simulation, because
/// nothing in an <c>INPUTSTATE</c> ever will.
/// </summary>
/// <remarks>
/// ⚠️ <b>The reference has exactly three commands that are not key bits</b>, and
/// <c>Scripts/Bots/botServerActions.gml</c> names them in one line: "Zoom, build
/// and eat are client commands". <c>processClientCommands.gml</c> handles all
/// three in adjacent cases (212, 232, 236) and each broadcasts at the moment the
/// server accepts it.
/// <para>
/// ⚠️ <b>The replayer injected one of the three for nine phases.</b>
/// <c>BUILD_SENTRY</c> was wired up when the sentry landed; <c>TOGGLE_ZOOM</c>
/// and <c>OMNOMNOMNOM</c> were not, while their messages sat in the checked-in
/// corpus the whole time — 31 zooms across four fixtures and four eats across
/// two. Both are physics: zoom writes <c>runPower</c> (0.6) and
/// <c>jumpStrength</c> (6.3), and an eat freezes acceleration and facing for 128
/// ticks while healing 1.6 hp a tick. The cost was visible only as two anomalous
/// rows in <c>ClientPredictionGateTests</c>, which that file had written down as
/// "worth looking at some day" without knowing what they were.
/// </para>
/// <para>
/// ⚠️ <b>Counting the injections is the point of this file.</b> A message family
/// the harness silently drops looks exactly like a physics bug, and the only
/// thing that distinguishes them is a count of how often the injection actually
/// fired.
/// </para>
/// </remarks>
public sealed class ClientCommandInjectionTests
{
    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static WorldShape ShapeOf(string map) => GameModes.ShapeOf(MapLoader(map));

    private static PortedSimulation Replay(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".trace");
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = TraceParser.Parse(trace, ShapeOf(trace.Header.Map), out _, out var stopped, ShapeOf);

        Assert.True(stopped is null, $"{fixture}: {stopped}");

        var simulation = new PortedSimulation(MapLoader);

        new Replayer(simulation) { Compare = Fields.Trajectory }
            .Replay(fixture, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        return simulation;
    }

    /// <summary>
    /// Every recorded <c>TOGGLE_ZOOM</c> is accepted by the port's own
    /// preconditions.
    /// </summary>
    /// <remarks>
    /// A refusal means the replay disagrees with the recording about whether the
    /// slot is alive and a sniper — the state a zoom needs — which is a
    /// divergence the trajectory comparison would report only indirectly, as a
    /// shorter exact window somewhere downstream.
    /// </remarks>
    [Theory]
    [InlineData("reference-ctf_truefort-closedsniper")]
    [InlineData("reference-cp_egypt-uncontestedcp")]
    [InlineData("reference-gen_destroy-rotation")]
    public void EveryRecordedZoomIsAccepted(string fixture) =>
        Assert.Equal(0, Replay(fixture).ZoomsRefused);

    /// <summary>
    /// ⚠️ <b>One recorded eat is refused, and it is the recording's own first
    /// tick.</b>
    /// </summary>
    /// <remarks>
    /// <c>closedheavy</c> is a mid-round recording: slot 5's heavy was already
    /// alive and already eating when <c>gg2-parity record</c> connected, so its
    /// <c>OMNOMNOMNOM</c> is in the t0 block — before any <c>PLAYER_SPAWN</c> has
    /// told the replay that slot 5 has a character at all. The port refuses it
    /// because there is nobody to feed.
    /// <para>
    /// ✅ <b>Deferring the injection until after the anchor was tried and does not
    /// help</b>: the anchor resets characters, it does not create them, so slot 5
    /// still has none. This is the same limit as
    /// <c>docs/PARITY.md</c>'s "a recording of players who were already alive is
    /// open-loop only" — a bootstrap artefact of recording mid-round, not a bug
    /// in the eat.
    /// </para>
    /// <para>
    /// Pinned at exactly one so that a second refusal, which would be a real
    /// disagreement about the 45-second cooldown, fails here.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOnlyRefusedEatIsTheOneInTheRecordingsFirstTick()
    {
        var simulation = Replay("reference-ctf_truefort-closedheavy");

        Assert.Equal(1, simulation.EatsRefused);
        Assert.Equal(0, simulation.ZoomsRefused);
    }

    /// <summary>
    /// ⚠️ <b>The fourth message the replayer was throwing away, and the one that
    /// is not a client command at all: <c>BALANCE</c>.</b>
    /// </summary>
    /// <remarks>
    /// <c>ServerBalanceTeams.gml</c> moves a player to the other team and
    /// destroys their character with <c>instance_destroy()</c> — <b>no
    /// PLAYER_DEATH is broadcast</b> — so this two-byte message is the entire
    /// record of the decision. A replay that ignored it kept the player on the
    /// old team with a live character, and nothing else would ever correct it.
    /// <para>
    /// ✅ <b>Found by <c>closedsentryteam</c></b>, where an autobalance switch at
    /// t2365 self-destructs the moved engineer's sentry a tick later and the port
    /// missed it. Applying the switch fixed that, and took
    /// <c>ctf_2dfort-autobalance</c>'s open-loop p99 from <b>10/3 to 2/2</b> —
    /// the worst row in <c>ClientPredictionGateTests</c> apart from the two the
    /// zoom and the eat already explained.
    /// </para>
    /// <para>
    /// ⚠️ <b>Stage 1's notice is not a switch.</b> <c>BALANCE 255</c> announces
    /// the countdown and moves nobody; only the two-byte form is applied.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("reference-ctf_2dfort-autobalance", 2)]
    [InlineData("reference-ctf_truefort-closedsentryteam", 1)]
    public void EveryRecordedBalanceSwitchIsApplied(string fixture, int switches) =>
        Assert.Equal(switches, Replay(fixture).BalanceSwitches);

    /// <summary>
    /// The corpus still contains the commands this file is about.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The direction that matters.</b> Everything above passes vacuously if
    /// a re-recording comes out with no zooms and no eats in it, exactly as
    /// <c>MessageCoverageTests</c> warns for whole message families. These are the
    /// counts measured 2026-08-28; a re-recording that changes them should change
    /// this list deliberately.
    /// </remarks>
    [Fact]
    public void TheCorpusStillContainsTheCommandsThisFileGates()
    {
        var zooms = 0;
        var eats = 0;

        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.trace"))
        {
            var trace = Trace.Decode(File.ReadAllBytes(path));
            var ticks = TraceParser.Parse(trace, ShapeOf(trace.Header.Map), out _, out _, ShapeOf);

            foreach (var tick in ticks)
            {
                foreach (var message in tick.Events)
                {
                    if (message.Type == MessageType.ToggleZoom)
                        zooms++;
                    else if (message.Type == MessageType.Omnomnomnom)
                        eats++;
                }
            }
        }

        Assert.Equal(31, zooms);
        Assert.Equal(26, eats);
    }
}
