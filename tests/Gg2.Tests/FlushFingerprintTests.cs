using Gg2.Bots;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// docs/PARITY.md's <b>instrument 2</b>, pointed at this port: which side of the
/// state update each message type rides on.
/// </summary>
/// <remarks>
/// A flush carries <c>[leftovers from the previous tick's End Steps][this tick's
/// pre-update messages][the update][this tick's post-update messages]</c>, so
/// where a message sits relative to the update says which stage produced it.
/// <para>
/// <b>The port is the easy side.</b> Ticking one at a time and draining after
/// each gives the groups with no timing inference at all. Against the reference
/// the same fingerprint needs the <c>.frames</c> sidecar and a heuristic, which
/// is what <see cref="FlushFrames.Group"/> is for and why it reports its own
/// ambiguity.
/// </para>
/// <para>
/// <b>These are TICK groups, and they are only flush groups because
/// <see cref="ATickIsExactlyOneFlush"/> says so.</b> A change that made a tick
/// flush TWICE would put both flushes in one drain and every assertion here
/// would read as if nothing had happened. That is not hypothetical: it was found
/// by mutation — adding a second <c>FlushBroadcast()</c> under
/// <c>WriteStateUpdate</c> while leaving the real one in place passed every test
/// in this file, while <em>moving</em> the flush (the real <c>cdaa62a</c> shape)
/// was caught. <b>The one-flush-per-tick assertion is load-bearing for
/// everything below it</b>, not a tidy extra.
/// </para>
/// <para>
/// <b>What this cannot settle</b>: three message types have call sites on both
/// sides of the flush, so for them "leading" and "trailing" are both legitimate
/// and the fingerprint says nothing. <c>WireStageTests</c> covers those from the
/// port's own stage flags. This covers the types that sit wholly on one side —
/// which is where <c>cdaa62a</c> lived.
/// </para>
/// </remarks>
public sealed class FlushFingerprintTests
{
    private const string Map = "gg_debug";

    private sealed class BroadcastReader
    {
        private readonly ServerStream _stream = new();
        private readonly ReceiveBuffer _buffer = new();

        public BroadcastReader(LevelMap map) => _stream.SetShape(GameModes.ShapeOf(map));

        public List<ServerMessage> Drain(LoopbackClient client)
        {
            client.Poll();
            _buffer.Append(client.Take(client.Received.Count));

            var messages = new List<ServerMessage>();
            while (_stream.TryRead(_buffer, out var message))
                messages.Add(message);

            return messages;
        }
    }

    /// <summary>Six bots fighting on <see cref="Map"/>.</summary>
    /// <remarks>
    /// Shared with <see cref="ATickIsExactlyOneFlush"/> deliberately: that
    /// gate is only meaningful on traffic where ticks APPEND EVENTS after the
    /// state update. On an idle server a stray second flush finds an empty
    /// buffer and returns without counting, so the mutation it exists to catch
    /// passes — measured, not supposed.
    /// </remarks>
    private static ServerHarness BotMatch() =>
        new(options =>
        {
            options.Map = Map;

            // Without the nav graphs the bots never walk anywhere, never meet
            // and never fire — and the fingerprint comes back empty, which reads
            // exactly like agreement. FlushShapeTests shipped that bug once.
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            };
        });

    /// <summary>Ticks a bot match one tick at a time, one flush group per tick.</summary>
    private static FlushFingerprint FingerprintOfABotMatch(int ticks = 900)
    {
        using var harness = BotMatch();

        var client = harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");

        var reader = new BroadcastReader(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

        // Let the join burst and the opening spawn wave drain, so the groups
        // below are steady-state play.
        harness.Tick(120);
        reader.Drain(client);

        var groups = new List<IReadOnlyList<ServerMessage>>();

        for (var tick = 0; tick < ticks; tick++)
        {
            harness.Tick(1);
            groups.Add(reader.Drain(client));
        }

        return FlushFingerprint.Of(groups);
    }

    /// <summary>
    /// A <c>WEAPON_FIRE</c> is appended in a Character's Begin Step, so it
    /// must always TRAIL the update of its own tick and may never lead one.
    /// </summary>
    /// <remarks>
    /// <b>This is the shape of <c>cdaa62a</c>.</b> That bug flushed under
    /// <c>WriteStateUpdate</c>, so every event message waited for the next tick's
    /// flush and led it. A client simulates every projectile itself from
    /// <c>WEAPON_FIRE</c> and the shooter's keys — there is no projectile
    /// message — so a late one starts the client's rocket a tick behind.
    /// </remarks>
    [Fact]
    public void AWeaponFireNeverLeadsAFlush()
    {
        var fingerprint = FingerprintOfABotMatch();

        // Not vacuous: zero fires would satisfy the assertion below trivially.
        Assert.True(
            fingerprint.Total(MessageType.WeaponFire) > 0,
            $"no WEAPON_FIRE in the whole match, so this proves nothing [{fingerprint}]");

        Assert.False(
            fingerprint.Leading.ContainsKey(MessageType.WeaponFire),
            $"a WEAPON_FIRE led a flush, so it shipped a tick late [{fingerprint}]");
    }

    /// <summary>
    /// A <c>PLAYER_DEATH</c> from damage is appended in a Character's End
    /// Step, after the reference has already flushed, so it LEADS the next one.
    /// </summary>
    /// <remarks>
    /// The other direction of the same boundary, and the reason both are here:
    /// a single assertion that events must trail would have been satisfied by the
    /// code as it stood before 2026-09-02, which sent deaths a tick early.
    /// </remarks>
    [Fact]
    public void APlayerDeathLeadsAFlush()
    {
        var fingerprint = FingerprintOfABotMatch();

        Assert.True(
            fingerprint.Total(MessageType.PlayerDeath) > 0,
            $"no PLAYER_DEATH in the whole match, so this proves nothing [{fingerprint}]");

        Assert.True(
            fingerprint.Leading.ContainsKey(MessageType.PlayerDeath),
            $"no PLAYER_DEATH led a flush; the End Step boundary has moved [{fingerprint}]");
    }

    /// <summary>
    /// The coverage ledger: what a bot match on this map never produced.
    /// </summary>
    /// <remarks>
    /// <b>This test is deliberately not a pass/fail on the missing types.</b>
    /// It pins the SET, so that a session which starts producing one — or stops —
    /// has to come here and say so. An absent row in a fingerprint is
    /// indistinguishable from an agreeing row, and that is how a gate goes
    /// vacuous without anybody noticing.
    /// <para>
    /// The five below are the ones docs/PARITY.md predicts a CTF-style bot match
    /// cannot reach: no generator on this map, no arena round, no engineer
    /// building long enough to lose a sentry, no rotation inside the window, and
    /// no team imbalance with a fixed bot fill.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCoverageLedgerIsPinned()
    {
        var fingerprint = FingerprintOfABotMatch();

        MessageType[] cannotReach =
        [
            MessageType.GeneratorDestroy,
            MessageType.ArenaStartRound,
            MessageType.DestroySentry,
            MessageType.MapEnd,
            MessageType.Balance,
        ];

        var unseen = fingerprint.Unseen(cannotReach);

        Assert.True(
            unseen.Count == cannotReach.Length,
            "a bot match now reaches a message type this ledger says it cannot — "
            + $"widen the fingerprint's coverage rather than the ledger. Seen: "
            + $"[{string.Join(", ", cannotReach.Except(unseen))}]");
    }

    /// <summary>
    /// A tick flushes exactly once — the invariant every other test in this
    /// file rests on.
    /// </summary>
    /// <remarks>
    /// <c>GameServerEndStep.gml:7-10</c> writes the buffer to every socket and
    /// clears it once, in one event, so one tick is one packet. This file groups
    /// messages by tick and calls the groups flushes; that is only true while
    /// this holds.
    /// <para>
    /// <b>Equality, not "at most one".</b> <c>WriteStateUpdate</c> writes
    /// something on every advancing tick, so the buffer is never empty and the
    /// flush is never skipped — an inequality would pass on a server that had
    /// stopped broadcasting altogether.
    /// </para>
    /// <para>
    /// <b>Six bots, not an idle server, and that is the whole test.</b>
    /// <c>FlushBroadcast</c> returns without counting when the buffer is empty,
    /// so on an idle server a second flush per tick finds nothing to send and the
    /// count stays right. The first version of this test was idle and the
    /// mutation it exists to catch passed it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATickIsExactlyOneFlush()
    {
        using var harness = BotMatch();

        harness.JoinClient("watcher");
        harness.TickUntil(() => harness.Server.RosterCount > 1, "the client joined");
        harness.Tick(120);

        var before = harness.Server.FlushCount;

        harness.Tick(600);

        Assert.Equal(600, harness.Server.FlushCount - before);
    }

    /// <summary>
    /// Almost every flush carries a state update, because the server writes
    /// one every tick.
    /// </summary>
    /// <remarks>
    /// <b>The grouping's own sanity check.</b> A run where many groups had no
    /// update would mean the groups are not flushes — which, against a reference
    /// recording, is exactly what a badly chosen
    /// <see cref="FlushFrames.Group"/> gap produces. Here the groups are exact by
    /// construction, so this asserts the reader is keeping up rather than the
    /// heuristic being right.
    /// </remarks>
    [Fact]
    public void EveryFlushCarriesItsStateUpdate()
    {
        var fingerprint = FingerprintOfABotMatch(300);

        Assert.Equal(0, fingerprint.GroupsWithoutAnUpdate);
    }
}
