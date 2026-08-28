using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The recording that gives a sentry self-destruct a <b>broadcast cause</b>.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-28: six engineers, three a side on <c>ctf_truefort</c></b>,
/// spawn-anchored by the map-reload recipe, with two causes provoked deliberately
/// over the bridge — an engineer's <b>team</b> switched and another's <b>class</b>
/// switched, each with a sentry standing. Twelve sentries built, eight destroyed.
/// <para>
/// ⚠️ <b>Why it was recorded.</b> <c>closedsentrykill</c> holds two self-destructs
/// whose cause is not on the wire at all — "a full dump of every non-INPUTSTATE,
/// non-FULL_UPDATE message in t488-500 contains exactly two entries, the
/// BUILD_SENTRY and the DESTROY_SENTRY" — so five sessions of inference could not
/// settle what fired. This recording contains the causes.
/// </para>
/// <para>
/// <b>The eight destructions are of four kinds</b>, and the port's score on them
/// is the point of this file:
/// <list type="bullet">
/// <item><b>t1243, after a PLAYER_CHANGETEAM at t1242</b> — ✅ reproduced.</item>
/// <item><b>t2007, after a PLAYER_CHANGECLASS at t2006</b> — ✅ reproduced.</item>
/// <item><b>t2366, after a BALANCE switch at t2365</b> — ✅ reproduced, but only
/// once the replayer started injecting <c>BALANCE</c> at all; this fixture is
/// what found that. See <see cref="ClientCommandInjectionTests"/>.</item>
/// <item><b>Five of the shape "built at T, self-destructed at T+1"</b> — ✅
/// reproduced since 2026-08-28, and this fixture is what explained them: every
/// one was built inside a <c>TeamGate</c>, and the sentry's two gate collision
/// events were missing from the port. See
/// <see cref="Sentries.GateCollisionStep"/>.</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>What made it hard was a byte.</b> <c>cause 0</c> was read as proof of
/// the Begin Step guard, the only site passing a literal 0 — but
/// <c>lastDamageSource</c> is <c>-1</c> and <c>write_ubyte</c> puts that out as
/// 0 too, so an End Step destruction with no dealer is indistinguishable on the
/// wire. Five sessions of inference ran down the guard's three arms because of
/// it.
/// </para>
/// </remarks>
public sealed class SentryCauseFixtureTests
{
    private const string SentryTeam = "reference-ctf_truefort-closedsentryteam.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", SentryTeam);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{SentryTeam}: {stopped}");
        return (trace, ticks);
    }

    private static int Count(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Sum(t => t.Events.Count(m => m.Type == type));

    private static DriftReport Replay(Fields compare)
    {
        var (trace, ticks) = Load();

        return new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(SentryTeam, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);
    }

    /// <summary>
    /// The recording holds the causes it was made to hold.
    /// </summary>
    /// <remarks>
    /// ⚠️ Asserted because the provocations are hand-driven: a re-recording where
    /// the bridge calls silently did nothing would leave every test below passing
    /// on an ordinary engineer match.
    /// </remarks>
    [Fact]
    public void TheRecordingHoldsSentriesAndTheCausesThatKillThem()
    {
        var (_, ticks) = Load();

        Assert.True(Count(ticks, MessageType.BuildSentry) >= 12, "not enough sentries built");
        Assert.True(Count(ticks, MessageType.DestroySentry) >= 8, "not enough sentries destroyed");

        // The provoked causes, each landing one tick before a destruction.
        Assert.Contains(ticks, t => t.Index == 1242 && t.Events.Any(m => m.Type == MessageType.PlayerChangeTeam));
        Assert.Contains(ticks, t => t.Index == 2006 && t.Events.Any(m => m.Type == MessageType.PlayerChangeClass));
        Assert.Contains(ticks, t => t.Index == 2365 && t.Events.Any(m => m.Type == MessageType.Balance));

        // Every one of them is a self-destruct: killer 255, assistant 255, cause 0.
        var selfDestructs = ticks
            .SelectMany(t => t.Events)
            .Count(m => m.Type == MessageType.DestroySentry && m.Payload.Length >= 4
                        && m.Payload[1] == 255 && m.Payload[3] == 0);

        Assert.Equal(8, selfDestructs);
    }

    /// <summary>It is closed-loop, so it carries an exactness claim.</summary>
    [Fact]
    public void EveryCharacterIsSpawnAnchored()
    {
        var (_, ticks) = Load();
        Assert.True(FixtureAnchors.LastExactAnchor(ticks) is not null, $"{SentryTeam}: not closed-loop");
    }

    /// <summary>Six engineers move exactly like the reference from their spawn anchors.</summary>
    /// <remarks>
    /// 331 today, t264 to t595. Short for the usual reason — twelve sentry turrets
    /// firing unseeded is a lot of knockback — and it is not what this fixture is
    /// for; the sentry comparison below runs over the whole recording.
    /// </remarks>
    [Fact]
    public void TheTrajectoriesAreExactFromTheSpawnAnchor()
    {
        var (_, ticks) = Load();
        var anchor = FixtureAnchors.LastExactAnchor(ticks);

        var after = Replay(Fields.Trajectory).Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        Assert.True(
            firstBad - anchor!.Value >= 300,
            $"{SentryTeam}: exact for only {firstBad - anchor.Value} ticks after t{anchor}");
    }

    /// <summary>
    /// ✅ <b>The port scraps the sentry when the owner's team, class or
    /// autobalance takes it away — on the tick, three times.</b>
    /// </summary>
    /// <remarks>
    /// These are three separate arms of the same guard
    /// (<c>Sentry.events/Begin Step.xml</c>: <c>ownerPlayer.class !=
    /// CLASS_ENGINEER or team != ownerPlayer.team or ...</c>), and until this
    /// recording none of them had ever been gated by a recording — only by unit
    /// tests over a hand-built world.
    /// <para>
    /// ⚠️ <b>Reported against the preceding tick</b>, like a death: the guard runs
    /// in Begin Step, before the server serialises the tick.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortReproducesTheThreeBroadcastCauses()
    {
        var mismatches = Replay(Fields.Sentries).Mismatches;

        Assert.DoesNotContain(mismatches, m => m.Tick == 1242);
        Assert.DoesNotContain(mismatches, m => m.Tick == 2006);
        Assert.DoesNotContain(mismatches, m => m.Tick == 2365);
    }

    /// <summary>
    /// ✅ <b>ANSWERED 2026-08-28. The five "built at T, destroyed at T+1"
    /// sentries were built in a doorway</b> —
    /// <c>Sentry.events/Collision with TeamGate.xml</c>, an event the port did
    /// not have.
    /// </summary>
    /// <remarks>
    /// The recording discriminates perfectly, which is why this fixture settled
    /// in one pass what five sessions of inference could not. Taking each
    /// <c>BUILD_SENTRY</c>'s broadcast coordinates and the sentry's own mask
    /// box: <b>all five that die overlap a <c>TeamGate</c> rectangle, and all
    /// seven that live overlap nothing.</b>
    /// <list type="table">
    /// <item><term>t280 slot 3</term><description>(1574.2, 474.4) — red gate [1578,444]-[1595,497] — died</description></item>
    /// <item><term>t326 slot 2</term><description>(3454.8, 474.4) — blue gate [3438,444]-[3455,497] — died</description></item>
    /// <item><term>t337 slot 6</term><description>(3582.4, 570.4) — blue gate [3558,546]-[3575,593] — died</description></item>
    /// <item><term>t866 slot 2</term><description>(3444.8, 474.4) — blue gate [3438,444]-[3455,497] — died</description></item>
    /// <item><term>t1102 slot 6</term><description>(3454.8, 474.4) — blue gate [3438,444]-[3455,497] — died</description></item>
    /// <item><term>t294, t338, t342, t790, t1482, t1612, t1646</term><description>no gate — all lived</description></item>
    /// </list>
    /// <para>
    /// ⚠️ <b>The step that made this look impossible was the cause byte.</b>
    /// "Cause 0 can only be the guard, because <c>lastDamageSource</c> is
    /// <c>-1</c> and never 0" — true of the field, false on the wire.
    /// <c>write_ubyte</c> puts <c>-1</c> out as <b>0</b>, so an End Step
    /// destruction with no dealer is byte-identical to
    /// <c>sendEventDestruction(ownerPlayer, noone, noone, 0)</c>. Every other
    /// elimination in the old note stands and none of them mattered.
    /// </para>
    /// <para>
    /// ⚠️ <b>Three of the five are a sentry in its <em>own</em> team's
    /// doorway.</b> Neither gate event looks at <c>team</c>. See
    /// <see cref="Sentries.GateCollisionStep"/>.
    /// </para>
    /// <para>
    /// The cascade at t1102 went with them, as predicted: the port only
    /// destroyed the wrong sentry of that pair because it was still holding the
    /// one it should have lost at t867.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFiveSentriesBuiltInATeamGateDieOnTheSameTickAsTheReferences()
    {
        var mismatches = Replay(Fields.Sentries).Mismatches;

        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The other direction, so the test above cannot pass on an empty
    /// recording: the five builds really are in a gate and the seven survivors
    /// really are not.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the mutation control.</b> Deleting
    /// <see cref="Sentries.GateCollisionStep"/> leaves this green and the test
    /// above red, which is the pair that says the fix is the reason the replay
    /// agrees rather than an accident of the comparison.
    /// </remarks>
    [Fact]
    public void EveryBuildThatDiesIsInAGateAndEveryBuildThatLivesIsNot()
    {
        var (_, ticks) = Load();
        var map = MapLoader("ctf_truefort");
        var world = new CollisionWorld(map);

        var destroyed = ticks
            .SelectMany(t => t.Events.Select(m => (t.Index, m)))
            .Where(e => e.m.Type == MessageType.DestroySentry && e.m.Payload.Length >= 4)
            .Select(e => (Tick: e.Index, Slot: e.m.Payload[0]))
            .ToArray();

        var builds = ticks
            .SelectMany(t => t.Events.Select(m => (t.Index, m)))
            .Where(e => e.m.Type == MessageType.BuildSentry && e.m.Payload.Length >= 6)
            .Select(e => (
                Tick: e.Index,
                Slot: e.m.Payload[0],
                X: (e.m.Payload[1] | (e.m.Payload[2] << 8)) / 5.0,
                Y: (e.m.Payload[3] | (e.m.Payload[4] << 8)) / 5.0))
            .ToArray();

        Assert.Equal(12, builds.Length);

        var inGate = 0;

        foreach (var build in builds)
        {
            var box = Aabb.ForCharacter(Sentries.Box, build.X, build.Y);
            var gated = false;

            foreach (var rect in world.Rects)
                if (rect.Kind is SolidKind.TeamGate or SolidKind.IntelGate
                    && rect.Overlaps(box.Left, box.Right, box.Top, box.Bottom))
                    gated = true;

            // Destroyed on the very next tick — the shape the whole finding is
            // about, as opposed to the four later deaths with a broadcast cause.
            var diedNextTick = destroyed.Any(d => d.Slot == build.Slot && d.Tick == build.Tick + 1);

            Assert.Equal(gated, diedNextTick);

            if (gated)
                inGate++;
        }

        Assert.Equal(5, inGate);
    }
}
