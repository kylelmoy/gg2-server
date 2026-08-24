using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The wire reader and writer: little-endian everywhere, length-prefixed byte
/// strings, no NUL termination.
/// </summary>
public sealed class NetBufferTests
{
    [Fact]
    public void MultiByteIntegersAreLittleEndian()
    {
        var writer = new NetWriter();
        writer.WriteUShort(0x1234);
        writer.WriteUInt(0xDEADBEEF);

        // set_little_endian_global(true) — Scripts/Game/game_init.gml:23.
        Assert.Equal(new byte[] { 0x34, 0x12, 0xEF, 0xBE, 0xAD, 0xDE }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void ScalarsRoundTrip()
    {
        var writer = new NetWriter();
        writer.WriteUByte(200);
        writer.WriteByte(-100);
        writer.WriteUShort(65535);
        writer.WriteShort(-32768);
        writer.WriteUInt(4000000000);
        writer.WriteInt(-2000000000);

        var reader = new NetReader(writer.WrittenSpan);
        Assert.Equal(200, reader.ReadUByte());
        Assert.Equal(-100, reader.ReadByte());
        Assert.Equal(65535, reader.ReadUShort());
        Assert.Equal(-32768, reader.ReadShort());
        Assert.Equal(4000000000u, reader.ReadUInt());
        Assert.Equal(-2000000000, reader.ReadInt());
        Assert.True(reader.IsEmpty);
    }

    [Fact]
    public void StringsAreLengthPrefixedNotNulTerminated()
    {
        var writer = new NetWriter();
        writer.WriteString1("Kyle");
        writer.WriteString2("a longer one");

        Assert.Equal(4, writer.WrittenSpan[0]);

        var reader = new NetReader(writer.WrittenSpan);
        Assert.Equal("Kyle", reader.ReadString1());
        Assert.Equal("a longer one", reader.ReadString2());
        Assert.True(reader.IsEmpty);
    }

    /// <summary>
    /// GM8 strings are byte strings: <c>chr</c>/<c>ord</c> map 1:1 over 0..255,
    /// so a name with a high byte in it must survive unchanged. Decoding as
    /// UTF-8 would turn two bytes into one char and shift every field after it.
    /// </summary>
    [Fact]
    public void StringsAreLatin1NotUtf8()
    {
        var writer = new NetWriter();
        writer.WriteString1("Bjørnÿ");

        // Six chars, six bytes — not the eight UTF-8 would need.
        Assert.Equal(6, writer.WrittenSpan[0]);
        Assert.Equal(7, writer.Count);

        var reader = new NetReader(writer.WrittenSpan);
        Assert.Equal("Bjørnÿ", reader.ReadString1());
    }

    [Fact]
    public void NulBytesSurviveInsideAString()
    {
        var writer = new NetWriter();
        writer.WriteString1("a\0b");

        var reader = new NetReader(writer.WrittenSpan);
        Assert.Equal("a\0b", reader.ReadString1());
    }

    [Fact]
    public void CharactersAboveLatin1AreRejected()
    {
        var writer = new NetWriter();
        var error = Assert.Throws<ProtocolException>(() => writer.WriteString1("snow☃man"));

        Assert.Contains("U+2603", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StringTooLongForItsPrefixIsRejected()
    {
        var writer = new NetWriter();
        Assert.Throws<ProtocolException>(() => writer.WriteString1(new string('x', 256)));
    }

    [Fact]
    public void ReadingPastTheEndThrows()
    {
        var reader = new NetReader(new byte[] { 1, 2, 3 });
        reader.ReadUByte();

        var error = Assert.Throws<ProtocolException>(() =>
        {
            var r = new NetReader(new byte[] { 1, 2, 3 });
            r.ReadUInt();
        });

        Assert.Contains("runs past the end", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, reader.Remaining);
    }

    [Fact]
    public void WriterGrowsPastItsInitialCapacity()
    {
        var writer = new NetWriter(initialCapacity: 8);
        for (var i = 0; i < 10_000; i++)
            writer.WriteUByte((byte)i);

        Assert.Equal(10_000, writer.Count);

        var reader = new NetReader(writer.WrittenSpan);
        for (var i = 0; i < 10_000; i++)
            Assert.Equal((byte)i, reader.ReadUByte());
    }

    [Fact]
    public void ResetKeepsTheBufferAndRewindsToEmpty()
    {
        var writer = new NetWriter();
        writer.WriteUInt(1);
        writer.Reset();

        Assert.True(writer.IsEmpty);
        Assert.Equal(0, writer.Count);
    }

    /// <summary>
    /// Abandoning a half-written message must not disturb the ones already in
    /// the broadcast buffer.
    /// </summary>
    [Fact]
    public void RewindDropsOnlyWhatCameAfterTheMark()
    {
        var writer = new NetWriter();
        writer.WriteMessageType(MessageType.Ping);
        var mark = writer.Count;

        writer.WriteMessageType(MessageType.PlayerJoin);
        writer.WriteString1("half a message");
        writer.Rewind(mark);

        Assert.Equal(new byte[] { (byte)MessageType.Ping }, writer.WrittenSpan.ToArray());
    }
}
