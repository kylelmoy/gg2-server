using System.Text;

namespace Gg2.Server;

/// <summary>
/// Renders text a client chose so that it cannot forge log structure.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem is real and it is one line of code to have.</b> A player name
/// is a GM8 byte string: up to twenty bytes, any of 0..255, decoded as Latin-1
/// (<see cref="Gg2.Protocol.Gg2Text"/>). Nothing on the join path rejects a
/// <c>\n</c>, because the reference does not and the name has to survive to the
/// wire byte for byte. So a player calling themselves
/// <c>"x\n12:00:00 shutting down"</c> writes a second log line, and an operator
/// reading the log — or a tool grepping it — sees an event that never happened.
/// The same bytes reach a close reason, a kick reason and the recorder's
/// filenames.
/// </para>
/// <para>
/// ⛔ <b>This is a RENDERING concern only.</b> The name on the wire, in the
/// roster and in a <c>FULL_UPDATE</c> stays exactly the bytes the client sent —
/// sanitising there would be a parity bug that desynchronises every other
/// client's roster. Sanitise where the string is being read by a human, never
/// where it is being sent.
/// </para>
/// <para>
/// The web panel needs none of this: every string it emits goes through
/// <c>ControlCommands.Escape</c>, which is <c>JsonEncodedText</c>, and the page
/// renders with a <c>default-src 'self'</c> CSP. The log was the one surface
/// with no encoder on it.
/// </para>
/// </remarks>
public static class LogText
{
    /// <summary>
    /// How much of an untrusted string is rendered before it is cut short.
    /// </summary>
    /// <remarks>
    /// A player name is capped at 20 by the protocol, but this also renders
    /// close reasons and map names, and a bound that does not depend on some
    /// other layer having held is the point.
    /// </remarks>
    public const int MaxLength = 64;

    /// <summary>The stand-in for a string with nothing printable in it.</summary>
    public const string Empty = "(empty)";

    /// <summary>
    /// <paramref name="value"/> with every control character escaped and the
    /// length bounded, safe to interpolate into a log line.
    /// </summary>
    /// <remarks>
    /// Escapes rather than strips, so two players whose names differ only in an
    /// invisible byte do not read as the same player in the log — which is
    /// exactly the impersonation a stripping sanitiser would enable.
    /// </remarks>
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Empty;

        var truncated = value.Length > MaxLength;
        var length = truncated ? MaxLength : value.Length;

        // The overwhelmingly common case: an ordinary name, nothing to do. Worth
        // the scan, because this runs on every join, leave and kick line.
        if (!truncated && !NeedsEscaping(value))
            return value;

        var text = new StringBuilder(length + 8);

        for (var i = 0; i < length; i++)
        {
            var c = value[i];

            // C0, DEL and C1. C1 (0x80-0x9F) is escaped as well as C0 because a
            // terminal reading Latin-1 treats several of them as controls, and
            // because they are not characters anybody types into a name box.
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
                text.Append("\\x").Append(((int)c).ToString("X2"));
            else
                text.Append(c);
        }

        if (truncated)
            text.Append('…');

        return text.ToString();
    }

    private static bool NeedsEscaping(string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
                return true;
        }

        return false;
    }
}
