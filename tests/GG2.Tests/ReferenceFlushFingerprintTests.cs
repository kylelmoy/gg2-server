using GG2.Maps;
using GG2.Parity;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// docs/PARITY.md's instrument 2 pointed at the <b>reference</b>, which is the
/// only test in this repo that grades the stage table against GG2 itself.
/// </summary>
/// <remarks>
/// <b>Everything else about the flush boundary is graded against a table
/// written by reading GML.</b> <c>WireStageTests</c> and
/// <c>FlushFingerprintTests</c> both check that the port routes each stage the
/// way docs/PARITY.md says; neither can notice if docs/PARITY.md is wrong. This
/// one reads the reference's own broadcast and asks what it actually did.
/// <para>
/// <b>The recording.</b> <c>reference-ctf_truefort-framed.trace</c> — 40 seconds
/// of six bots fighting on a live GG2 2.9.2 server, captured by
/// <c>gg2-parity record</c>, with the <c>.frames</c> sidecar of read boundaries
/// beside it. 48 KB, 1,188 reads, 1,196 state updates.
/// </para>
/// <para>
/// <b>Flush boundaries are inferred from arrival time and cannot be
/// otherwise</b> — the byte stream is identical whichever side of the flush a
/// message was appended on, which is the whole reason this class of bug survived
/// six phases. The inference is sound here because the reference sends one
/// packet per tick: 1,188 reads against 1,196 updates is 0.7% coalescing.
/// <see cref="TheGroupingIsInsensitiveToTheGap"/> is the guard on that.
/// </para>
/// </remarks>
public sealed class ReferenceFlushFingerprintTests
{
    private const string Fixture = "reference-ctf_truefort-framed";

    private static FlushFingerprint Read(double gapMs, out int groups, out int updates)
    {
        var tracePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture + ".trace");
        var framesPath = FlushFrames.PathFor(tracePath);

        Assert.True(File.Exists(tracePath), $"missing {tracePath}");
        Assert.True(File.Exists(framesPath), $"missing {framesPath}");

        var trace = Trace.Decode(File.ReadAllBytes(tracePath));
        var frames = FlushFrames.Decode(File.ReadAllBytes(framesPath));
        var ranges = frames.Group(TimeSpan.FromMilliseconds(gapMs), out _);

        var map = LevelMap.FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map));
        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(map.ForArea(1)));

        // One buffer across every group: a message is attributed to the group its
        // LAST byte arrived in, which is when a client would have acted on it.
        var buffer = new ReceiveBuffer();
        var parsed = new List<IReadOnlyList<ServerMessage>>();

        foreach (var range in ranges)
        {
            buffer.Append(trace.Stream.AsSpan()[range]);

            var messages = new List<ServerMessage>();
            while (stream.TryRead(buffer, out var message))
                messages.Add(message);

            parsed.Add(messages);
        }

        groups = ranges.Count;
        updates = parsed.Sum(g => g.Count(m =>
            m.Type is MessageType.InputState or MessageType.QuickUpdate));

        return FlushFingerprint.Of(parsed);
    }

    /// <summary>
    /// The End Step finding, confirmed from the reference's own TCP
    /// stream. Every <c>PLAYER_DEATH</c> in 40 seconds of live GG2 leads its
    /// packet; every <c>WEAPON_FIRE</c> trails.
    /// </summary>
    /// <remarks>
    /// This is independent confirmation of an in-game measurement that read
    /// <c>buffer_size(global.sendBuffer)</c> and found 12 bytes of leftover
    /// after two deaths; this one never touches the game and reads the same
    /// fact off the wire. <b>Two instruments, no shared assumption but the
    /// reference itself.</b>
    /// <para>
    /// A death is appended in <c>Character.events/End Step.xml</c>, after
    /// <c>GameServerEndStep</c> has already flushed, so it waits for the next
    /// tick and arrives ahead of that tick's update. A weapon fire is appended in
    /// the Character's Begin Step, before the flush, so it rides its own tick
    /// behind its own update.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortsStageTableMatchesWhatTheReferenceActuallyDid()
    {
        var fingerprint = Read(10, out _, out _);

        // Not vacuous: a recording with no deaths or no shots would satisfy
        // every assertion below by having nothing to disagree with.
        Assert.True(fingerprint.Total(MessageType.PlayerDeath) > 0, "no deaths in the fixture");
        Assert.True(fingerprint.Total(MessageType.WeaponFire) > 0, "no shots in the fixture");

        Assert.Equal(0, fingerprint.Trailing.GetValueOrDefault(MessageType.PlayerDeath));
        Assert.Equal(0, fingerprint.Leading.GetValueOrDefault(MessageType.WeaponFire));
    }

    /// <summary>
    /// The alarm and collision stages ride their own tick, behind their update.
    /// </summary>
    /// <remarks>
    /// <c>PLAYER_SPAWN</c> comes from <c>Player.alarm[5]</c> and <c>GRAB_INTEL</c>
    /// from a collision event; both stages sit above the GameServer's End Step,
    /// so both trail. They are here because a table that got only the two
    /// dramatic rows right would still be a table nobody should trust.
    /// </remarks>
    [Fact]
    public void TheAlarmAndCollisionStagesTrailTheirUpdate()
    {
        var fingerprint = Read(10, out _, out _);

        Assert.True(fingerprint.Total(MessageType.PlayerSpawn) > 0, "no spawns in the fixture");
        Assert.True(fingerprint.Total(MessageType.GrabIntel) > 0, "no intel grabs in the fixture");

        Assert.Equal(0, fingerprint.Leading.GetValueOrDefault(MessageType.PlayerSpawn));
        Assert.Equal(0, fingerprint.Leading.GetValueOrDefault(MessageType.GrabIntel));
    }

    /// <summary>
    /// The grouping heuristic's own guard: the answer must not depend on the
    /// gap.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the two tests above readable at all.</b> They
    /// rest on a timing inference, and an inference that flipped its answer when
    /// the threshold moved would be measuring the threshold. Across a 14x range,
    /// 2, 5 and 10ms all give 1,187 groups and 20 and 28ms give 1,141 — and the
    /// fingerprint is byte-for-byte identical at all five. The reference sends
    /// one packet a tick, so there is a wide plateau to sit on.
    /// <para>
    /// <c>FlushFrames.Group</c>'s ambiguity count is what says which end of
    /// the range is safe: 0 boundaries near the threshold at 2 and 5ms, 950 at
    /// 20ms. A fingerprint taken at a gap with a high count is not to be trusted
    /// even when it agrees.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(2.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void TheGroupingIsInsensitiveToTheGap(double gapMs)
    {
        var fingerprint = Read(gapMs, out var groups, out var updates);

        Assert.Equal(0, fingerprint.Trailing.GetValueOrDefault(MessageType.PlayerDeath));
        Assert.Equal(0, fingerprint.Leading.GetValueOrDefault(MessageType.WeaponFire));

        // Every group carries an update, which is the sanity check on the
        // grouping itself: the reference flushes once a tick and always has an
        // update to send, so a group without one is a group that is not a flush.
        Assert.Equal(0, fingerprint.GroupsWithoutAnUpdate);

        // And the groups track the ticks. 5% of slack absorbs the coalescing the
        // wider gaps cause without admitting a grouping that has collapsed.
        Assert.True(
            groups > updates * 0.95,
            $"{groups} groups for {updates} updates at {gapMs}ms — the grouping has merged flushes");
    }
}
