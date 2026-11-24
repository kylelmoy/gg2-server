using System.Security.Cryptography;

namespace GG2.Protocol.Rooms;

/// <summary>
/// Room codes: a host prefix and five characters that are the password
/// (<c>docs/ROOMS.md</c>). The room host makes them; a client reads one a
/// player typed and finds its host by the prefix in the <see cref="RoomDirectory"/>.
/// </summary>
public static class RoomCodes
{
    /// <summary>
    /// No <c>0/O</c> and no <c>1/I/L</c>, so a code read aloud or off a phone
    /// cannot be mistyped into another one. 31 characters.
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>The length of a room's secret, which is its password.</summary>
    public const int SecretLength = 5;

    /// <summary>A fresh secret from a cryptographic source.</summary>
    public static string NewSecret()
    {
        Span<char> chars = stackalloc char[SecretLength];

        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }

    /// <summary>
    /// The secret a typed password names on a host with <paramref name="prefix"/>,
    /// or null if it cannot name one. Accepts the five-character secret or the
    /// whole six-character code, in either case, with surrounding spaces.
    /// </summary>
    public static string? Normalise(string typed, char prefix)
    {
        var text = typed.Trim().ToUpperInvariant();

        if (text.Length == SecretLength + 1 && text[0] == prefix)
            text = text[1..];

        return text.Length == SecretLength ? text : null;
    }

    /// <summary>
    /// A whole code as a player typed it to join (<c>"w4k 9pq"</c>), as the
    /// six upper-case characters, or null if it is not one. Spaces and dashes
    /// anywhere are dropped, so a code shared in two halves still reads; every
    /// character must be in <see cref="Alphabet"/>.
    /// </summary>
    public static string? ParseWhole(string typed)
    {
        Span<char> code = stackalloc char[SecretLength + 1];
        var length = 0;

        foreach (var c in typed)
        {
            if (char.IsWhiteSpace(c) || c == '-')
                continue;
            var upper = char.ToUpperInvariant(c);
            if (length == code.Length || Alphabet.IndexOf(upper) < 0)
                return null;
            code[length++] = upper;
        }

        return length == code.Length ? new string(code) : null;
    }
}
