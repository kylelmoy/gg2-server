using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

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
/// <item><b>Five of the shape "built at T, self-destructed at T+1"</b> —
/// ❌ missed, and pinned below.</item>
/// </list>
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
    /// ⚠️ <b>OPEN, and the sharpest form the sentry self-destruct finding has
    /// ever had: five sentries the reference builds and destroys one tick
    /// later, which the port keeps.</b>
    /// </summary>
    /// <remarks>
    /// The five are at t281, t327, t338, t867 and t1103, each exactly one tick
    /// after that slot's own <c>BUILD_SENTRY</c>, each <c>killer 255, assistant
    /// 255, cause 0</c>.
    /// <para>
    /// <b>What this recording established, none of which was known before:</b>
    /// <list type="bullet">
    /// <item>✅ <b>It is the Begin Step guard, not the damage path.</b> The other
    /// site that broadcasts a destruction is <c>End Step</c>'s <c>hp &lt;= 0</c>,
    /// which passes <c>lastDamageSource</c> — initialised to <b>-1</b> in
    /// <c>Sentry.events/Create.xml</c>, never 0. Cause 0 can only be the guard.</item>
    /// <item>✅ <b>Not a leftover from before the map reload.</b> Measured live on
    /// 2026-08-28: <c>serverGotoMap</c> destroys every <c>Sentry</c>
    /// (<c>instance_number(Sentry)</c> 0 afterwards) and leaves
    /// <c>player.sentry</c> at -4.</item>
    /// <item>⚠️ <b>The first one cannot be the 25 px circle.</b> At t281 it is the
    /// only sentry alive in the recording — the first <c>BUILD_SENTRY</c> is the
    /// one at t280 — and <c>collision_circle(x, y+10, 25, Sentry, false, true)</c>
    /// excludes the calling instance. So either an arm reads differently from how
    /// the port reads it, or the reference had a sentry the wire never
    /// announced.</item>
    /// <item>⚠️ <b>The sixth mismatch is a cascade, not a sixth cause.</b> At
    /// t1102 the port destroys slot 2's sentry while the reference destroys slot
    /// 6's — because the port still holds the slot 2 sentry it failed to destroy
    /// at t867, and slot 6 then builds 10 px away from it.</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ <b>Pinned so that fixing it fails loudly</b>, in both directions: the
    /// five root misses and the one cascade. A fix that closes the five closes the
    /// sixth with them.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortStillMissesTheFiveSelfDestructsThatFollowABuild()
    {
        var mismatches = Replay(Fields.Sentries).Mismatches;

        int[] missed = [280, 326, 337, 866, 1102];

        foreach (var tick in missed)
            Assert.Contains(mismatches, m => m.Tick == tick);

        // The cascade: the port destroys the wrong sentry of the pair at t1102.
        Assert.Equal(6, mismatches.Count(m => m.Tick is 280 or 326 or 337 or 866 or 1102));

        // And nothing else disagrees, so the five are the whole of it.
        Assert.Equal(6, mismatches.Count);
    }
}
