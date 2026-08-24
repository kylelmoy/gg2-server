using System.Text;

namespace Gg2.Protocol;

/// <summary>
/// GM8 strings are byte strings: <c>ord</c> and <c>chr</c> are a 1:1 map between
/// a byte and a char in 0..255. Names, chat and map names on the wire are
/// therefore Latin-1, <em>not</em> UTF-8, and decoding them as UTF-8 corrupts
/// every non-ASCII player name.
/// </summary>
/// <remarks>
/// <c>Scripts/Networking/read_binstring.gml</c> and <c>write_binstring.gml</c>
/// spell this out: they build a string one <c>chr(read_ubyte(...))</c> at a
/// time precisely so that arbitrary bytes survive the round trip.
/// </remarks>
public static class Gg2Text
{
    /// <summary>Latin-1: byte <c>b</c> becomes char <c>b</c>, always.</summary>
    public static readonly Encoding Encoding = Encoding.Latin1;

    public static string Decode(ReadOnlySpan<byte> bytes) => Encoding.GetString(bytes);

    /// <summary>
    /// Encodes to bytes. Any char above U+00FF has no byte representation and is
    /// rejected rather than silently replaced — a '?' in a player name is a bug
    /// that survives to the wire.
    /// </summary>
    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        EncodeTo(text, bytes);
        return bytes;
    }

    /// <summary>
    /// Encodes <paramref name="text"/> into <paramref name="destination"/>,
    /// which must be at least <c>text.Length</c> bytes. Returns the byte count,
    /// which always equals <c>text.Length</c>.
    /// </summary>
    public static int EncodeTo(string text, Span<byte> destination)
    {
        if (destination.Length < text.Length)
            throw new ArgumentException("destination too small", nameof(destination));

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c > 0xFF)
                throw new ProtocolException(
                    $"character U+{(int)c:X4} at index {i} is not representable in a GM8 byte string");
            destination[i] = (byte)c;
        }

        return text.Length;
    }
}
