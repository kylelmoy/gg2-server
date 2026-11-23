using System.Buffers.Binary;

namespace GG2.Protocol;

/// <summary>Which of the two wire protocols a connection speaks.</summary>
/// <remarks>
/// <b>Chosen once, by the HELLO, and fixed for the life of the connection.</b>
/// docs/PROTOCOL.md, "Two protocols".
/// </remarks>
public enum WireProtocol : byte
{
    /// <summary>
    /// GG2 v2.9.2's protocol, byte for byte: what the stock Game Maker 8 client
    /// speaks. Frozen — every rule in CONTRIBUTING.md about wire compatibility
    /// is a rule about this one.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// The protocol this server shares with the native client (gg2-client).
    /// Owned by this project and free to change, under a version number.
    /// </summary>
    Native = 1,
}

/// <summary>
/// The native protocol's identity: its UUID, its version, and the hello that
/// asks for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The native hello is the legacy HELLO with a different UUID, followed by a
/// <c>ushort</c> version</b> — 19 bytes:
/// </para>
/// <code>
/// HELLO(1)  NativeUuid(16)  version(ushort LE)
/// </code>
/// <para>
/// Everything a server can answer is something a client already understands.
/// A server that accepts writes exactly what it would have written after a
/// legacy HELLO (<c>PASSWORD_REQUEST</c> or the greeting), so acceptance needs
/// no message of its own. A server that does not — a stock GM8 server, a
/// gg2-server build older than the native protocol, or one whose supported
/// range does not include <c>version</c> — answers the one-byte
/// <c>INCOMPATIBLE_PROTOCOL</c> and closes, and the client reconnects with the
/// legacy HELLO. A stock server reads only the first 17 bytes, so the two
/// version bytes it never reads cost nothing.
/// </para>
/// <para>
/// ⚠️ <b>Bump <see cref="Version"/> on every change to the native wire</b> that
/// an older native peer would misread — a new server message, a changed layout,
/// a new client command. The server and the client are built from one tree, but
/// a released client meets servers of other builds, and the version is the only
/// thing that turns a misframed stream into a clean fallback.
/// </para>
/// </remarks>
public static class NativeProtocol
{
    /// <summary>
    /// The native protocol's version. Raise it with every incompatible change
    /// and say in docs/PROTOCOL.md, "Native protocol versions", what changed.
    /// </summary>
    public const ushort Version = 1;

    /// <summary>
    /// The oldest native version this server still serves. Equal to
    /// <see cref="Version"/> unless a change deliberately keeps an older one
    /// working — and keeping one means the encoders branch on the connection's
    /// version, which nothing does yet.
    /// </summary>
    public const ushort MinVersion = 1;

    /// <summary>The UUID in a native HELLO, where a legacy one carries <see cref="GameConstants.ProtocolUuid"/>.</summary>
    public const string Uuid = "31ad383f-df45-449b-9bb7-63869ac81345";

    /// <summary><see cref="Uuid"/> as it appears on the wire.</summary>
    public static ReadOnlySpan<byte> UuidBytes =>
    [
        0x31, 0xad, 0x38, 0x3f, 0xdf, 0x45, 0x44, 0x9b,
        0x9b, 0xb7, 0x63, 0x86, 0x9a, 0xc8, 0x13, 0x45,
    ];

    /// <summary>Bytes in a native HELLO: the legacy 17 plus the version.</summary>
    public const int HelloLength = ProtocolHello.Length + 2;

    /// <summary>Whether this build serves native <paramref name="version"/>.</summary>
    public static bool Supports(ushort version) => version is >= MinVersion and <= Version;

    /// <summary>The native HELLO, for a client to open a connection with.</summary>
    public static void WriteHello(NetWriter writer, ushort version = Version)
    {
        writer.WriteMessageType(MessageType.Hello);
        writer.WriteBytes(UuidBytes);
        writer.WriteUShort(version);
    }

    /// <summary>The version in the two bytes after a native HELLO's first 17.</summary>
    public static ushort ReadVersion(ReadOnlySpan<byte> twoBytes) =>
        BinaryPrimitives.ReadUInt16LittleEndian(twoBytes);
}

/// <summary>What the first 17 bytes of a connection asked for.</summary>
public enum HelloKind
{
    /// <summary>Neither protocol. Answered with <c>INCOMPATIBLE_PROTOCOL</c>.</summary>
    Incompatible,

    /// <summary>The stock HELLO. The connection speaks <see cref="WireProtocol.Legacy"/>.</summary>
    Legacy,

    /// <summary>A native HELLO, whose two version bytes are still to be read.</summary>
    Native,
}

/// <summary>
/// Classifies the HELLO that opens every connection. Shared by every place a
/// server reads one, so the two protocols are told apart in exactly one way.
/// </summary>
public static class ProtocolHello
{
    /// <summary>Bytes the reference reads before it decides anything: HELLO and a UUID.</summary>
    public const int Length = 17;

    /// <param name="first17">Exactly the first <see cref="Length"/> bytes of the connection.</param>
    public static HelloKind Classify(ReadOnlySpan<byte> first17)
    {
        if (first17.Length != Length || first17[0] != (byte)MessageType.Hello)
            return HelloKind.Incompatible;

        var uuid = first17[1..];

        if (uuid.SequenceEqual(GameConstants.ProtocolUuidBytes))
            return HelloKind.Legacy;

        if (uuid.SequenceEqual(NativeProtocol.UuidBytes))
            return HelloKind.Native;

        return HelloKind.Incompatible;
    }
}
