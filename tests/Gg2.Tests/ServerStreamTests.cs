using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Framing a server's broadcast stream.
/// </summary>
/// <remarks>
/// The strongest test here reads a recording of the <b>real GM8 server</b>,
/// captured with <c>gg2-parity record</c> on <c>ctf_truefort</c> with a single
/// passive spectator. Framing it is a genuine check: the reference wrote those
/// bytes without knowing anything about this port, and a reader that gets a
/// single field's width wrong runs off the end or finishes with bytes to spare.
/// </remarks>
public sealed class ServerStreamTests
{
    private const string ReferenceTrace = "reference-ctf_truefort-spectator.bin";

    private static byte[] LoadTrace(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path), $"missing trace fixture {path}");
        return File.ReadAllBytes(path);
    }

    private static WorldShape ShapeOf(string map) =>
        GameModes.ShapeOf(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)));

    /// <summary>
    /// Every byte of the reference recording is accounted for, with nothing
    /// left over and nothing read past the end.
    /// </summary>
    [Fact]
    public void FramesTheReferenceRecordingExactly()
    {
        var trace = LoadTrace(ReferenceTrace);

        var stream = new ServerStream();
        stream.SetShape(ShapeOf("ctf_truefort"));

        var reader = new NetReader(trace);
        var messages = new List<ServerMessage>();

        while (reader.Remaining > 0)
            messages.Add(stream.Read(ref reader));

        Assert.Equal(trace.Length, reader.Position);
        Assert.True(reader.IsEmpty);
        Assert.NotEmpty(messages);
    }

    /// <summary>
    /// The join burst, in the order <c>ServerJoinUpdate.gml</c> writes it. This
    /// is what a port that sends only a <c>JOIN_UPDATE</c> header gets wrong.
    /// </summary>
    [Fact]
    public void ReferenceRecordingOpensWithTheJoinBurst()
    {
        var stream = new ServerStream();
        stream.SetShape(ShapeOf("ctf_truefort"));

        var reader = new NetReader(LoadTrace(ReferenceTrace));

        var join = stream.Read(ref reader);
        Assert.Equal(MessageType.JoinUpdate, join.Type);
        Assert.Equal((byte)1, join.PlayerId);
        Assert.Equal(1, stream.CurrentMapArea);

        var changeMap = stream.Read(ref reader);
        Assert.Equal(MessageType.ChangeMap, changeMap.Type);
        Assert.Equal("ctf_truefort", changeMap.Text);
        Assert.Equal("ctf_truefort", stream.CurrentMap);

        // The existing roster, replayed one entry at a time. Only slot 0 here —
        // the reference's own reserved player, in dedicated mode.
        Assert.Equal(MessageType.PlayerJoin, stream.Read(ref reader).Type);
        Assert.Equal(MessageType.PlayerChangeClass, stream.Read(ref reader).Type);
        Assert.Equal(MessageType.PlayerChangeTeam, stream.Read(ref reader).Type);
        Assert.Equal(1, stream.PlayerCount);

        var full = stream.Read(ref reader);
        Assert.Equal(MessageType.FullUpdate, full.Type);
        Assert.NotNull(full.State);
        Assert.Equal(1, full.State.PlayerCount);

        // Then, and only then, the joiner hears about itself on the broadcast.
        var self = stream.Read(ref reader);
        Assert.Equal(MessageType.PlayerJoin, self.Type);
        Assert.Equal("recorder", self.Text);
        Assert.Equal(2, stream.PlayerCount);
    }

    /// <summary>
    /// What the reference's <c>FULL_UPDATE</c> actually said, decoded. These are
    /// the real server's numbers, not this port's.
    /// </summary>
    [Fact]
    public void ReferenceFullUpdateDecodesToTheExpectedWorld()
    {
        var stream = new ServerStream();
        stream.SetShape(ShapeOf("ctf_truefort"));

        var reader = new NetReader(LoadTrace(ReferenceTrace));
        StateUpdate? full = null;

        while (reader.Remaining > 0 && full is null)
            full = stream.Read(ref reader).State is { Tier: MessageType.FullUpdate } s ? s : null;

        Assert.NotNull(full);

        // global.tdmInvulnerabilityTicks = seconds * 30 * 0.2, and the ini's
        // default is 5 seconds.
        Assert.Equal(30, full.TdmInvulnerabilityTicks);

        // Nobody has a character: the roster is one dedicated-mode spectator.
        var player = Assert.Single(full.Players);
        Assert.Equal(0, player.Subobjects);
        Assert.Null(player.Character);
        Assert.Null(player.Sentry);
        Assert.Empty(player.DominationKills);
        Assert.Equal(string.Empty, player.UnparsedRewards);

        // ctf_truefort has one intel each and no moving platforms.
        Assert.Single(full.RedIntel);
        Assert.Single(full.BlueIntel);
        Assert.Empty(full.MovingPlatforms);
        Assert.Empty(full.ControlPoints);

        // The ini defaults: CapLimit 5, Respawn Time 5, Time Limit 15.
        Assert.Equal(5, full.CapLimit);
        Assert.Equal(0, full.RedCaps);
        Assert.Equal(0, full.BlueCaps);
        Assert.Equal(5, full.RespawnTimeSeconds);

        // CTFHUD: timeLimitMins then a uint timer.
        Assert.Equal(2, full.HudScalars.Length);
        Assert.Equal(15, full.HudScalars[0]);

        Assert.Equal(10, full.ClassLimits.Length);
        Assert.All(full.ClassLimits, limit => Assert.Equal(255, limit));
    }

    /// <summary>
    /// Six ticks out of seven carry only inputs. Counted on the reference's own
    /// stream, so the cadence is measured rather than assumed.
    /// </summary>
    [Fact]
    public void ReferenceCadenceIsSixInputStatesToOneQuickUpdate()
    {
        var stream = new ServerStream();
        stream.SetShape(ShapeOf("ctf_truefort"));

        var reader = new NetReader(LoadTrace(ReferenceTrace));
        var inputStates = 0;
        var quickUpdates = 0;

        while (reader.Remaining > 0)
        {
            var message = stream.Read(ref reader);
            if (message.Type == MessageType.InputState) inputStates++;
            if (message.Type == MessageType.QuickUpdate) quickUpdates++;
        }

        Assert.True(quickUpdates > 5, $"only {quickUpdates} QUICK_UPDATEs in the trace");

        // Six to one, give or take where the recording started and stopped.
        var ratio = (double)inputStates / quickUpdates;
        Assert.InRange(ratio, 5.5, 6.5);
    }

    /// <summary>
    /// A truncated stream must consume nothing rather than half-frame a message
    /// — otherwise a live reader loses its place the first time a message
    /// spans two reads.
    /// </summary>
    [Fact]
    public void TryReadConsumesNothingWhenAMessageIsIncomplete()
    {
        var trace = LoadTrace(ReferenceTrace);

        var stream = new ServerStream();
        stream.SetShape(ShapeOf("ctf_truefort"));

        var buffer = new ReceiveBuffer();

        // One byte at a time: the worst case a socket can produce.
        var framed = 0;
        foreach (var b in trace)
        {
            buffer.Append([b]);
            while (stream.TryRead(buffer, out _))
                framed++;
        }

        Assert.True(buffer.IsEmpty, $"{buffer.Count} byte(s) left unframed");
        Assert.True(framed > 100, $"only framed {framed} messages");
    }

    [Fact]
    public void RosterCompactsOnPlayerLeave()
    {
        var stream = new ServerStream();
        stream.SetShape(new WorldShape
        {
            GameMode = GameMode.TeamDeathmatch,
            ControlPointCount = 0,
            MovingPlatformCount = 0,
        });

        var writer = new NetWriter();
        ServerMessages.PlayerJoin(writer, "zero");
        ServerMessages.PlayerJoin(writer, "one");
        ServerMessages.PlayerJoin(writer, "two");
        ServerMessages.PlayerChangeClass(writer, 2, PlayerClass.Demoman);
        ServerMessages.PlayerLeave(writer, 1);

        var reader = new NetReader(writer.WrittenSpan);
        while (reader.Remaining > 0)
            stream.Read(ref reader);

        // "two" was index 2 and is now index 1, and its class moved with it —
        // which matters, because the class decides whether a FULL_UPDATE
        // carries a weapon block for that slot.
        Assert.Equal(new[] { "zero", "two" }, stream.Names);
    }

    /// <summary>
    /// A demoman's <c>FULL_UPDATE</c> carries a mine count and then that many
    /// blocks. Nothing else in the update says how long it is, so a reader that
    /// does not track each player's class cannot frame the stream at all.
    /// </summary>
    [Fact]
    public void MineBlocksAreFramedByTheMinegunsOwnCount()
    {
        var writer = new NetWriter();

        // A FULL_UPDATE for one demoman with two mines out.
        writer.WriteMessageType(MessageType.FullUpdate);
        writer.WriteUShort(30);       // tdmInvulnerabilityTicks
        writer.WriteUByte(1);         // playerCount

        // Stats: six bytes, a ushort healing, four bytes, then queueJump.
        for (var i = 0; i < 6; i++) writer.WriteUByte(0);
        writer.WriteUShort(0);
        for (var i = 0; i < 4; i++) writer.WriteUByte(0);
        writer.WriteUByte(0);
        writer.WriteString2(string.Empty);                   // rewards
        writer.WriteUByte(0x01);                             // subobjects: a character

        // Character, FULL_UPDATE detail.
        writer.WriteUByte(0);
        writer.WriteUShort(0);
        writer.WriteUByte(0);
        writer.WriteUShort(1000);
        writer.WriteUShort(2000);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteUByte(120);
        writer.WriteUByte(4);
        writer.WriteUByte(0);
        writer.WriteUByte(0);
        writer.WriteUByte(0);
        writer.WriteShort(-1);
        writer.WriteUByte(0);
        writer.WriteShort(0);

        // Minegun: lobbed, then two seven-byte mines.
        writer.WriteUByte(2);
        for (var i = 0; i < 2; i++)
        {
            writer.WriteUShort((ushort)(500 + i));
            writer.WriteUShort((ushort)(600 + i));
            writer.WriteByte(1);
            writer.WriteByte(2);
            writer.WriteUByte(1);
        }

        // Tail: no intel, no platforms, caps block, TDM HUD, class limits.
        writer.WriteUShort(0);
        writer.WriteUShort(0);
        writer.WriteUByte(5);
        writer.WriteUByte(0);
        writer.WriteUByte(0);
        writer.WriteUByte(5);
        writer.WriteUByte(15);
        writer.WriteUInt(0);
        writer.WriteUShort(30);
        for (var i = 0; i < 10; i++) writer.WriteUByte(255);

        var stream = new ServerStream();
        stream.SetShape(new WorldShape
        {
            GameMode = GameMode.TeamDeathmatch,
            ControlPointCount = 0,
            MovingPlatformCount = 0,
        });

        // Teach the reader that slot 0 is a demoman, the way the wire would.
        var setup = new NetWriter();
        ServerMessages.PlayerJoin(setup, "demo");
        ServerMessages.PlayerChangeClass(setup, 0, PlayerClass.Demoman);
        var setupReader = new NetReader(setup.WrittenSpan);
        while (setupReader.Remaining > 0)
            stream.Read(ref setupReader);

        var reader = new NetReader(writer.WrittenSpan);
        var message = stream.Read(ref reader);

        Assert.True(reader.IsEmpty, $"{reader.Remaining} byte(s) unread: the mine blocks were mis-sized");
        Assert.NotNull(message.State);

        var player = Assert.Single(message.State.Players);
        Assert.Equal(2, player.Mines.Length);
        Assert.Equal(500, player.Mines[0].X);
        Assert.Equal(601, player.Mines[1].Y);
        Assert.Equal(120, player.Character!.Value.Hp);
    }

    /// <summary>
    /// An unbuilt sentry carries one extra byte on a <c>FULL_UPDATE</c>, and
    /// whether it is there is bit 7 of the byte before it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SentryTrailingByteFollowsTheBuiltBit(bool built)
    {
        var writer = new NetWriter();
        writer.WriteMessageType(MessageType.QuickUpdate);
        writer.WriteUByte(1);
        writer.WriteUByte(0x02);   // subobjects: a sentry, no character
        writer.WriteUByte((byte)((built ? 0x80 : 0x00) | 90));

        var stream = new ServerStream();
        stream.SetShape(new WorldShape
        {
            GameMode = GameMode.TeamDeathmatch,
            ControlPointCount = 0,
            MovingPlatformCount = 0,
        });

        var reader = new NetReader(writer.WrittenSpan);
        var message = stream.Read(ref reader);

        // On a QUICK_UPDATE the trailing byte is never present, built or not —
        // it is a FULL_UPDATE-only field.
        Assert.True(reader.IsEmpty);

        var sentry = Assert.Single(message.State!.Players).Sentry;
        Assert.NotNull(sentry);
        Assert.Equal(built, sentry.Value.Built);
        Assert.Equal(90, sentry.Value.Hp);
        Assert.Null(sentry.Value.VSpeed);
    }
}
