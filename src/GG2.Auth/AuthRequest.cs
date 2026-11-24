using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GG2.Auth;

internal enum RequestStatus
{
    Complete,
    Incomplete,
    Invalid,
}

/// <summary>
/// One request from a game server, and the check it asks for.
/// </summary>
/// <remarks>
/// The layout is the reference's <c>rewardAuthStart.gml</c>, which
/// <c>GG2.Server.RewardAuth.BuildAuthRequest</c> also writes: the 16-byte
/// protocol UUID in reading order, a little-endian <c>ushort</c> counting
/// everything after itself, the client's 16-byte HMAC, the 16-byte challenge, an
/// ipCheck byte, then the reward id raw and unterminated. The length is the only
/// thing that says where the id ends.
/// </remarks>
internal readonly record struct AuthRequest(byte[] Response, byte[] Challenge, bool IpCheck, string RewardId)
{
    public const int HashBytes = 16;

    /// <summary>
    /// The longest id a stock client can send: <c>ClientBeginStep.gml:166</c>
    /// cuts it to 255, and <c>REWARD_REQUEST</c> carries it behind a one-byte length.
    /// </summary>
    public const int MaxIdBytes = 255;

    private const int HeaderBytes = 16 + 2;
    private const int FixedPayload = HashBytes + HashBytes + 1;

    /// <summary>The largest request a well-formed game server can send.</summary>
    public const int MaxBytes = HeaderBytes + FixedPayload + MaxIdBytes;

    public static ReadOnlySpan<byte> ProtocolUuid =>
    [
        0x20, 0x5e, 0x2d, 0x84, 0x48, 0x33, 0x89, 0xd4,
        0x15, 0xd9, 0xc0, 0x24, 0x96, 0x67, 0xdf, 0x1c,
    ];

    /// <summary>
    /// Parses a request from the start of <paramref name="buffer"/>.
    /// </summary>
    /// <remarks>
    /// <b>Invalid is decided as early as the bytes allow</b>, so a connection
    /// sending garbage is closed after its first 18 bytes rather than held open
    /// until its timeout.
    /// </remarks>
    public static RequestStatus TryParse(ReadOnlySpan<byte> buffer, out AuthRequest request)
    {
        request = default;

        var uuid = Math.Min(buffer.Length, ProtocolUuid.Length);
        if (!buffer[..uuid].SequenceEqual(ProtocolUuid[..uuid]))
            return RequestStatus.Invalid;

        if (buffer.Length < HeaderBytes)
            return RequestStatus.Incomplete;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer[16..]);
        if (length < FixedPayload || length > FixedPayload + MaxIdBytes)
            return RequestStatus.Invalid;

        if (buffer.Length < HeaderBytes + length)
            return RequestStatus.Incomplete;

        var payload = buffer.Slice(HeaderBytes, length);
        var ipCheck = payload[HashBytes * 2];
        if (ipCheck > 1)
            return RequestStatus.Invalid;

        request = new AuthRequest(
            payload[..HashBytes].ToArray(),
            payload.Slice(HashBytes, HashBytes).ToArray(),
            ipCheck == 1,
            // GM8 strings are bytes, so Latin-1 is the one decoding that maps
            // each byte to one char and back again.
            Encoding.Latin1.GetString(payload[FixedPayload..]));

        return RequestStatus.Complete;
    }

    /// <summary>
    /// What the client computed: <c>hmac_md5(key, challenge + ip)</c>, or
    /// <c>hmac_md5(key, challenge)</c> when the game server sent ipCheck = 0.
    /// </summary>
    /// <remarks>
    /// <c>ClientBeginStep.gml:553-561</c> appends <c>socket_remote_ip</c>, the
    /// dotted address the client connected to, as text. The reference's
    /// <c>hmac_md5_bin.gml</c> is textbook HMAC with a 64-byte block, so the
    /// framework's HMACMD5 computes the same value.
    /// </remarks>
    public static byte[] Expected(ReadOnlySpan<byte> key, ReadOnlySpan<byte> challenge, string? ip)
    {
        var message = new byte[challenge.Length + (ip?.Length ?? 0)];
        challenge.CopyTo(message);
        if (ip is not null)
            Encoding.Latin1.GetBytes(ip, message.AsSpan(challenge.Length));

        return HMACMD5.HashData(key, message);
    }
}
