using System.Buffers.Binary;
using System.Text;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The two lobby datagrams, byte for byte against
/// <c>Scripts/GameServer/sendLobbyRegistration.gml</c> and
/// <c>sendLobbyUnreg.gml</c>.
/// </summary>
/// <remarks>
/// <b>These prove this port agrees with the GML. They do NOT prove the live
/// lobby accepts it</b> — <c>ganggarrison.com:29944</c> is a third-party service,
/// is not part of the reference tree, and was not contacted. If a server run with
/// <c>--lobby</c> never shows up in the list, the layout below is the thing that
/// is already verified and the service is the thing that is not.
/// </remarks>
public sealed class LobbyRegistrationTests
{
    private static readonly byte[] ServerId =
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
    ];

    private static LobbyServerInfo Info(
        string name = "My Server",
        string map = "ctf_truefort",
        ushort port = 8190,
        ushort playerLimit = 10,
        ushort occupied = 3,
        ushort bots = 0,
        bool passworded = false) =>
        new()
        {
            ServerId = ServerId,
            ServerName = name,
            Map = map,
            Port = port,
            PlayerLimit = playerLimit,
            OccupiedSlots = occupied,
            Bots = bots,
            Passworded = passworded,
        };

    /// <summary>
    /// The whole fixed head of the packet, in order, exactly as the GML writes
    /// it.
    /// </summary>
    [Fact]
    public void TheHeaderIsTheTwoUuidsAndTheServerId()
    {
        var packet = LobbyRegistration.BuildRegister(Info());

        // 16 bytes of message type "register".
        Assert.Equal(
            Convert.FromHexString("b5dae2e8424f9ed00fcb8c21c7ca1352"),
            packet[..16]);

        // Then the server's own id, unchanged.
        Assert.Equal(ServerId, packet[16..32]);

        // Then GG2_LOBBY_UUID, which says which game's list this belongs in.
        Assert.Equal(
            Convert.FromHexString(GameConstants.Gg2LobbyUuid.Replace("-", string.Empty)),
            packet[32..48]);

        // 0 is TCP.
        Assert.Equal(0, packet[48]);
    }

    /// <summary>
    /// The five counters are BIG-endian, and this is the test that says so.
    /// </summary>
    /// <remarks>
    /// <c>sendLobbyRegistration.gml:9</c> is an explicit
    /// <c>set_little_endian(lobbyBuffer, false)</c> — the opposite of the game
    /// protocol every other byte in this repo is written in. A little-endian
    /// packet is not rejected loudly; the server simply never appears in the
    /// list, which looks exactly like the feature being switched off.
    /// </remarks>
    [Fact]
    public void TheCountersAreBigEndian()
    {
        var packet = LobbyRegistration.BuildRegister(
            Info(port: 0x1234, playerLimit: 10, occupied: 3, passworded: true));

        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(49, 2)));
        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(51, 2)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(53, 2)));

        // Bots, in their own field. Zero here because this packet has none.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(55, 2)));

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(57, 2)));

        // Seven key/value pairs follow.
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(59, 2)));

        // The port byte-pair is 0x12 0x34 and not 0x34 0x12. Spelled out because
        // reading it back with the same endianness the writer used would pass
        // either way.
        Assert.Equal(0x12, packet[49]);
        Assert.Equal(0x34, packet[50]);
    }

    /// <summary>
    /// Bots go in their own counter and are NOT added into the human count.
    /// </summary>
    /// <remarks>
    /// <c>LobbyController.events/Step.xml:78-82</c> renders the two as
    /// <c>players + "+" + bots</c>, so a bot counted into both would show up
    /// twice. The reference sends a hard zero here because its server has no
    /// bots; this port sends what it is running — see
    /// <see cref="LobbyServerInfo.Bots"/>.
    /// </remarks>
    [Fact]
    public void BotsAreAnnouncedInTheirOwnCounter()
    {
        var packet = LobbyRegistration.BuildRegister(Info(occupied: 3, bots: 7));

        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(53, 2)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(55, 2)));
    }

    [Fact]
    public void APasswordlessServerSaysZero()
    {
        var packet = LobbyRegistration.BuildRegister(Info(passworded: false));

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(57, 2)));
    }

    /// <summary>
    /// The seven pairs, in the reference's order, each a byte-counted key and a
    /// short-counted value.
    /// </summary>
    [Fact]
    public void TheSevenPairsAreInOrderAndCorrectlyFramed()
    {
        var packet = LobbyRegistration.BuildRegister(Info(name: "My Server", map: "ctf_truefort"));

        var offset = 61;
        var pairs = new List<(string Key, byte[] Value)>();

        for (var i = 0; i < 7; i++)
        {
            var keyLength = packet[offset++];
            var key = Encoding.UTF8.GetString(packet, offset, keyLength);
            offset += keyLength;

            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            offset += 2;

            pairs.Add((key, packet[offset..(offset + valueLength)]));
            offset += valueLength;
        }

        // The packet ends exactly where the seventh pair does: no trailing bytes.
        Assert.Equal(packet.Length, offset);

        Assert.Equal(
            ["name", "game", "game_short", "game_ver", "game_url", "map", "protocol_id"],
            pairs.Select(p => p.Key));

        Assert.Equal("My Server", Encoding.UTF8.GetString(pairs[0].Value));

        // game and game_short both name this software, not the game: the web
        // status page reads game, the in-client browser prefers game_short, and
        // both columns should say the same thing. Neither is the reference's
        // GAME_NAME_STRING, which ConstantsTests pins separately.
        Assert.Equal("gg2-server", Encoding.UTF8.GetString(pairs[1].Value));
        Assert.Equal("gg2-server", Encoding.UTF8.GetString(pairs[2].Value));
        Assert.NotEqual(GameConstants.GameNameString, Encoding.UTF8.GetString(pairs[1].Value));

        // game_ver is this server's version, not GG2's — both readers join the
        // pairs into "gg2-server v1.0.0". It is read from the assembly, so what
        // is asserted is its shape and that it is not the game's version; a
        // literal here would be an edit every release has to remember.
        var advertisedVersion = Encoding.UTF8.GetString(pairs[3].Value);
        Assert.Matches(@"^v\d+\.\d+\.\d+", advertisedVersion);
        Assert.NotEqual(GameConstants.GameVersionString, advertisedVersion);

        // game_url is the Game column's link target on the web status page, and
        // it points at this project rather than at the reference's GAME_URL_STRING.
        Assert.Equal("https://github.com/kylelmoy/gg2-server", Encoding.UTF8.GetString(pairs[4].Value));
        Assert.NotEqual(GameConstants.GameUrlString, Encoding.UTF8.GetString(pairs[4].Value));
        Assert.Equal("ctf_truefort", Encoding.UTF8.GetString(pairs[5].Value));

        // The seventh value is 16 RAW BYTES, not text. It is written by hand
        // in the reference for exactly that reason, and running it through
        // writeKeyValue would have been the obvious tidy-up and would have been
        // wrong.
        Assert.Equal(GameConstants.ProtocolUuidBytes.ToArray(), pairs[6].Value);
    }

    /// <summary>
    /// The UUIDs go on the wire in TEXTUAL order, which is not what .NET's
    /// <see cref="Guid"/> would produce.
    /// </summary>
    /// <remarks>
    /// <c>Guid.ToByteArray()</c> byte-reverses the first three groups, so parsing
    /// these through <c>Guid</c> would misplace four bytes of the message type
    /// and the lobby would never recognise the packet.
    /// </remarks>
    [Fact]
    public void UuidsAreWrittenInTextualOrderNotGuidOrder()
    {
        var packet = LobbyRegistration.BuildRegister(Info());

        Assert.Equal(0xb5, packet[0]);
        Assert.Equal(0xda, packet[1]);
        Assert.Equal(0xe2, packet[2]);
        Assert.Equal(0xe8, packet[3]);

        // What Guid would have written, so the difference is on the record.
        Assert.NotEqual(
            new Guid(LobbyRegistration.RegisterMessageUuid).ToByteArray(),
            packet[..16]);
    }

    /// <summary>The unregister packet is the message type and the id, and nothing else.</summary>
    /// <remarks>
    /// It carries no port and no name: the lobby keys on the server id alone,
    /// which is why <c>NewServerId</c> must be generated once per process and not
    /// per registration.
    /// </remarks>
    [Fact]
    public void TheUnregisterPacketIsThirtyTwoBytes()
    {
        var packet = LobbyRegistration.BuildUnregister(ServerId);

        Assert.Equal(32, packet.Length);
        Assert.Equal(Convert.FromHexString("488984ac45dc86e1990198dd1c01c064"), packet[..16]);
        Assert.Equal(ServerId, packet[16..]);
    }

    [Fact]
    public void AServerIdThatIsNotSixteenBytesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => LobbyRegistration.BuildUnregister(new byte[15]));
        Assert.Throws<ArgumentException>(
            () => LobbyRegistration.BuildRegister(Info() with { ServerId = new byte[17] }));
    }

    /// <summary>Two server ids differ, and are the right length.</summary>
    /// <remarks>
    /// Thin, and deliberately so: what matters about a server id is that it is
    /// stable for a process and unique between them. The stability half is
    /// <c>GameServer</c>'s and is asserted there.
    /// </remarks>
    [Fact]
    public void AFreshServerIdIsSixteenRandomBytes()
    {
        var first = LobbyRegistration.NewServerId();
        var second = LobbyRegistration.NewServerId();

        Assert.Equal(LobbyRegistration.ServerIdLength, first.Length);
        Assert.Equal(LobbyRegistration.ServerIdLength, second.Length);
        Assert.NotEqual(first, second);
    }

    /// <summary>A long server name is truncated rather than overflowing the length prefix.</summary>
    [Fact]
    public void AnOverlongValueCannotOverflowItsLengthPrefix()
    {
        var packet = LobbyRegistration.BuildRegister(Info(name: new string('x', 70_000)));

        var valueLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(66, 2));

        Assert.Equal(ushort.MaxValue, valueLength);
    }
}
