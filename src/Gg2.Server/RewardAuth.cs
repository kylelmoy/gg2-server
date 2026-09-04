using System.Security.Cryptography;
using System.Text;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// The Haxxy reward handshake, minus the socket.
/// </summary>
/// <remarks>
/// <c>Documentation/Authentication.txt</c> is the specification and it is
/// accurate; <c>Scripts/Rewards/</c> and <c>processClientCommands.gml:320-336</c>
/// are the code. Three parties:
/// <list type="number">
/// <item>the <b>client</b>, which knows a secret key,</item>
/// <item>the <b>game server</b> — this — which knows neither the key nor the
/// answer,</item>
/// <item>the <b>auth server</b>, which knows every client's key.</item>
/// </list>
/// <para>
/// <b>This server verifies nothing and cannot.</b> It mints a nonce, relays
/// the client's HMAC to the auth server, and broadcasts whatever string comes
/// back. That is not a weakness in the port — it is the design: the game server
/// is deliberately not trusted with anybody's key. Do not add a local check;
/// there is nothing here to check against.
/// </para>
/// <para>
/// <b>Why the client signs the server's IP as well as the challenge.</b>
/// <c>hmac_md5(key, challenge + gameServerIp)</c> binds the response to one game
/// server, so a hostile server cannot replay a client's answer at a different
/// one to impersonate them. The auth server recomputes it using the IP the
/// request arrives from, which is why the game server never sends the address
/// itself and could not usefully lie about it.
/// </para>
/// <para>
/// <b>Every effect is cosmetic</b> — badges, hats, capes, a taunt animation.
/// The <c>hasReward</c> call sites sit in <c>Character</c>'s and <c>Weapon</c>'s
/// simulation events, which looks alarming and is not: they choose sprites, and
/// their <c>random</c> draws are on GM8's free-running generator rather than the
/// seeded stream. A player with rewards does not simulate differently, so
/// nothing here is observable simulation and none of it touches
/// <c>WorldState</c>.
/// </para>
/// </remarks>
public static class RewardAuth
{
    /// <summary>Bytes in a challenge, and in the response to one.</summary>
    /// <remarks>
    /// 16 both ways: <c>rewardCreateChallenge.gml</c> repeats 16 times, and
    /// <c>GameServerDefineCommands.gml</c> gives
    /// <c>REWARD_CHALLENGE_RESPONSE</c> a fixed 16-byte payload. The response is
    /// an MD5-sized HMAC, which is where the second 16 comes from.
    /// </remarks>
    public const int ChallengeBytes = 16;

    /// <summary>
    /// The auth protocol's own UUID, the first 16 bytes of every request.
    /// </summary>
    /// <remarks>
    /// <c>rewardAuthStart.gml</c>. Written big-endian — <c>parseUuid</c> emits
    /// the hex in reading order — which is why this is a literal byte array
    /// rather than a <see cref="Guid"/>: <see cref="Guid.ToByteArray()"/> would
    /// little-endian the first three fields and produce a request the auth
    /// server does not recognise.
    /// </remarks>
    public static ReadOnlySpan<byte> AuthProtocolUuid =>
    [
        0x20, 0x5e, 0x2d, 0x84, 0x48, 0x33, 0x89, 0xd4,
        0x15, 0xd9, 0xc0, 0x24, 0x96, 0x67, 0xdf, 0x1c,
    ];

    /// <summary>
    /// The reference's cap on how many auth checks may be waiting at once.
    /// </summary>
    /// <remarks>
    /// <c>rewardAuthStart.gml</c>, "paranoia!". A request that arrives with the
    /// queue full is dropped silently, and so is one here.
    /// </remarks>
    public const int MaxQueued = 50;

    /// <summary>
    /// A fresh 16-byte challenge.
    /// </summary>
    /// <remarks>
    /// <b>Never from <c>WorldState.Random</c>.</b> That stream is observable
    /// simulation — every seeded weapon puts its draw on the wire and every
    /// client re-derives from it — so minting a nonce out of it would make the
    /// stream's position depend on how many players had reward keys configured.
    /// The reference draws from GM8's free-running generator for the same
    /// reason; a cryptographic source is used here because a predictable
    /// challenge is the one thing that would weaken the handshake, and nothing
    /// anywhere re-derives this value.
    /// </remarks>
    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ChallengeBytes);

    /// <summary>
    /// Builds the request the game server sends to the auth server.
    /// </summary>
    /// <param name="response">The client's 16-byte HMAC.</param>
    /// <param name="challenge">The 16 bytes this server sent it.</param>
    /// <param name="ipCheck">
    /// Whether the auth server should fold the requesting IP into the HMAC it
    /// recomputes. True for a client — that is the replay protection — and false
    /// only on the reference's <em>self</em>-auth path, where a NATed host may
    /// not know its own address.
    /// </param>
    /// <param name="rewardId">The user id the client asked to authenticate as.</param>
    /// <remarks>
    /// <c>rewardAuthStart.gml</c>: the protocol UUID, a <c>ushort</c> length,
    /// then the four fields with no framing of their own. <b>The length
    /// counts everything after itself</b> — response, challenge, the flag byte
    /// and the id — and the id is written raw and unterminated, so the length is
    /// the only thing that says where it ends.
    /// </remarks>
    public static byte[] BuildAuthRequest(
        ReadOnlySpan<byte> response,
        ReadOnlySpan<byte> challenge,
        bool ipCheck,
        string rewardId)
    {
        if (response.Length != ChallengeBytes)
            throw new ArgumentException($"response must be {ChallengeBytes} bytes", nameof(response));

        if (challenge.Length != ChallengeBytes)
            throw new ArgumentException($"challenge must be {ChallengeBytes} bytes", nameof(challenge));

        // The reference measures the id with string_length on a GM8 string,
        // which is bytes rather than characters. Gg2Text is this port's copy of
        // that encoding, so measuring the encoded bytes is the same count.
        var id = Gg2Text.Encode(rewardId);

        var writer = new NetWriter(AuthProtocolUuid.Length + 2 + ChallengeBytes * 2 + 1 + id.Length);

        writer.WriteBytes(AuthProtocolUuid);
        writer.WriteUShort(checked((ushort)(ChallengeBytes + ChallengeBytes + 1 + id.Length)));
        writer.WriteBytes(response);
        writer.WriteBytes(challenge);
        writer.WriteUByte(ipCheck ? (byte)1 : (byte)0);
        writer.WriteBytes(id);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads the auth server's answer.
    /// </summary>
    /// <returns>
    /// The reward string, or <c>null</c> when the request failed and there is
    /// nothing to broadcast.
    /// </returns>
    /// <remarks>
    /// <c>RewardAuthChecker</c>'s Step event reads the whole response and treats
    /// a non-empty body as success — there is no status code and no framing, the
    /// connection closing <em>is</em> the terminator. <b>An empty body is a
    /// failure, not an empty reward set</b>, and the two would otherwise be
    /// indistinguishable: the reference's <c>buffer_size(responseBuffer)&gt;0</c>
    /// guard is this <c>null</c>.
    /// </remarks>
    public static string? ReadAuthResponse(ReadOnlySpan<byte> body) =>
        body.Length == 0 ? null : Gg2Text.Decode(body);
}
