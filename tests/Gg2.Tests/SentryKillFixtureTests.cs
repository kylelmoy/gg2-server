using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The first recording in the project containing a <c>DESTROY_SENTRY</c>.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: three engineers against three soldiers on
/// <c>ctf_truefort</c></b>, spawn-anchored by the map-reload recipe. Six
/// sentries built, three shot down, 125 seeded rocket fires.
/// <para>
/// ⚠️ <b>The roster is the design again.</b> <c>closedsupport</c> has engineers
/// and two sentries in it, but its other classes are medic and pyro — nothing
/// there hits hard enough to take a sentry down, so it holds no
/// <c>DESTROY_SENTRY</c> at all. A rocket deals 25 + 30 to one and is seeded, so
/// soldiers give both the kill and a reproducible one.
/// </para>
/// <para>
/// ⚠️ <b>This fixture is the reason <see cref="Fields.Sentries"/> exists.</b>
/// The harness injected <c>BUILD_SENTRY</c> like a spawn but compared no sentry
/// state whatever, so a port that destroyed the wrong sentry — or none — replayed
/// every recording without a murmur. A sentry writes <b>no bytes at all</b> on an
/// <c>INPUTSTATE</c>, not even its hp (docs/PROTOCOL.md, "Per sentry"), so on six
/// ticks in seven a recording is silent about one, and on the seventh a destroyed
/// sentry is an unset subobject bit that names nobody.
/// </para>
/// </remarks>
public sealed class SentryKillFixtureTests
{
    private const string SentryKill = "reference-ctf_truefort-closedsentrykill.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", SentryKill);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{SentryKill}: {stopped}");
        return (trace, ticks);
    }

    private static int Count(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Count(t => t.Events.Any(m => m.Type == type));

    private static (int Exact, int Anchor) ExactWindow(Fields compare)
    {
        var (trace, ticks) = Load();

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{SentryKill}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(SentryKill, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        return (firstBad - anchor.Value, anchor.Value);
    }

    /// <summary>
    /// The recording holds sentries being built and shot down.
    /// </summary>
    /// <remarks>
    /// A re-recording in which the soldiers never reached a sentry would pass
    /// the window tests below by having nothing to get wrong — and, as always,
    /// would pass them with a <em>longer</em> window.
    /// </remarks>
    [Fact]
    public void TheRecordingHoldsSentriesBuiltAndDestroyed()
    {
        var (_, ticks) = Load();

        Assert.True(Count(ticks, MessageType.BuildSentry) >= 5, "not enough sentries built");
        Assert.True(Count(ticks, MessageType.SentryPosition) >= 4, "not enough sentries landed");

        // The message no recording in this project had ever contained.
        Assert.True(Count(ticks, MessageType.DestroySentry) >= 3, "no sentry was destroyed");

        Assert.True(Count(ticks, MessageType.WeaponFire) >= 100, "not enough seeded fire");
        Assert.True(Count(ticks, MessageType.PlayerDeath) >= 8, "not enough deaths");
        Assert.True(ticks.Count > 3000, $"only {ticks.Count} ticks");
    }

    /// <summary>It is closed-loop, so it carries an exactness claim.</summary>
    [Fact]
    public void EveryCharacterIsSpawnAnchored()
    {
        var (_, ticks) = Load();
        Assert.True(FixtureAnchors.LastExactAnchor(ticks) is not null, $"{SentryKill}: not closed-loop");
    }

    /// <summary>
    /// Engineers and soldiers move exactly like the reference from their spawn
    /// anchors.
    /// </summary>
    [Fact]
    public void TheTrajectoriesAreExactFromTheirSpawnAnchors()
    {
        var (exact, anchor) = ExactWindow(Fields.Trajectory);

        // 595 today, t427 to t1022. Shorter than the airblast fixture's 1,266
        // because there is far more of everything happening here — 125 rocket
        // fires against 38, eleven deaths against five — not because engineers
        // are modelled worse than pyros.
        //
        // ⚠️ Raised 550 -> 585 on 2026-08-27, when modelling a rotated
        // projectile's precise mask (ProjectileMask) took this window from 581
        // to 595. The floor moves with the measurement on purpose: a gain left
        // unprotected by its bound is a gain that can be lost silently, which is
        // this project's "a bound nobody re-measures decays into a formality"
        // running the other way.
        Assert.True(
            exact >= 585,
            $"{SentryKill}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }

    /// <summary>
    /// <b>The port destroys the sentry the soldier shot down, on the same tick,
    /// crediting the same player.</b>
    /// </summary>
    /// <remarks>
    /// This is what the recording was made for. Its three
    /// <c>DESTROY_SENTRY</c>s are not alike:
    /// <list type="bullet">
    /// <item><b>t496</b> — <c>killer 255, assistant 255, cause 0</c>: the Begin
    /// Step <b>self-destruct</b>.</item>
    /// <item><b>t1691</b> — <c>killer 4, assistant 255, cause 9</c>: a soldier
    /// shot it down. <b>The port reproduces this one exactly</b>, on the tick,
    /// with the killer, and it is inside the trajectory window.</item>
    /// <item><b>t2296</b> — another self-destruct.</item>
    /// </list>
    /// ⚠️ <b>The port reproduces neither self-destruct, and one of them is a
    /// real divergence.</b> See
    /// <see cref="ThePortStillMissesBothOfTheReferencesSentrySelfDestructs"/>,
    /// which pins it so that fixing it fails loudly.
    /// </remarks>
    [Fact]
    public void ThePortShootsDownTheSameSentryOnTheSameTick()
    {
        var (trace, ticks) = Load();

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.Sentries }
            .Replay(SentryKill, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        // ⚠️ Reported against the PRECEDING tick, like a death: the turret fires
        // from End Step, which runs after the server has serialised the tick.
        // So the destruction the recording puts at t1691 is reconciled at t1690.
        Assert.DoesNotContain(report.Mismatches, m => m.Tick == 1690);

        // And the recording really does contain a weapon kill, so the assertion
        // above cannot pass by there being nothing there.
        var weaponKills = ticks
            .SelectMany(t => t.Events)
            .Count(m => m.Type == MessageType.DestroySentry && m.Payload.Length >= 4 && m.Payload[3] != 0);

        Assert.Equal(1, weaponKills);
    }

    /// <summary>
    /// ✅ <b>ANSWERED 2026-08-28: both self-destructs are the sentry's
    /// <c>Collision with TeamGate</c> event, and the port reproduces them on
    /// the tick.</b>
    /// </summary>
    /// <remarks>
    /// This was pinned as a known divergence for five sessions, on the reading
    /// that <c>killer 255, assistant 255, cause 0</c> could only be
    /// <c>Sentries.BeginStep</c>'s three-clause guard, whose only literal 0 it
    /// is. <b>That reading was wrong in one step</b>: the four collision events
    /// leave <c>lastDamageSource</c> at the <c>-1</c>
    /// <c>Sentry.events/Create.xml</c> set, and <c>-1</c> reaches the wire
    /// through <c>write_ubyte</c> as <b>0</b> as well. So an End Step
    /// destruction with nobody to blame is byte-identical to the guard's.
    /// <para>
    /// The evidence is positional and it is total. Across
    /// <c>reference-ctf_truefort-closedsentryteam.trace</c>'s twelve builds,
    /// <b>all five that die overlap a <c>TeamGate</c> rectangle and all seven
    /// that live overlap nothing</b> — and the two here are a thirteenth and a
    /// fourteenth of the same shape. <c>docs/TRAPS-SIMULATION.md</c>, "the
    /// sentry's gate collision", has the table.
    /// </para>
    /// <para>
    /// ⚠️ <b>The live session that "eliminated position" was right about its
    /// own experiment and wrong about the conclusion.</b> It rebuilt at the
    /// trace's coordinates and watched the sentry die — but the control at a
    /// known-good position died too, both of them to the server resetting a
    /// lone test player to <c>TEAM_SPECTATOR</c>, so the experiment could not
    /// see a gate even where there was one. A contaminated experiment does not
    /// eliminate a hypothesis; it fails to test it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTwoSentriesBuiltInATeamGateAreDestroyedOnTheSameTick()
    {
        var (trace, ticks) = Load();

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.Sentries }
            .Replay(SentryKill, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        // ⚠️ Reported against the preceding tick: the destruction is broadcast
        // from End Step, after the server has serialised the tick. The
        // recording's t496 and t2296 are reconciled at t495 and t2295.
        Assert.DoesNotContain(report.Mismatches, m => m.Tick is 495 or 2295);

        // Both directions, and this is the half that fails if the gate step is
        // deleted: the recording really does hold two of them, so the line
        // above cannot pass by there being nothing to reproduce.
        var selfDestructs = ticks
            .SelectMany(t => t.Events)
            .Count(m => m.Type == MessageType.DestroySentry && m.Payload.Length >= 4
                        && m.Payload[1] == 255 && m.Payload[3] == 0);

        Assert.Equal(2, selfDestructs);

        // And the whole comparison is clean now, which is the stronger claim:
        // no sentry the port keeps that the reference killed, and none it kills
        // that the reference kept.
        Assert.Empty(report.Mismatches);
    }
}
