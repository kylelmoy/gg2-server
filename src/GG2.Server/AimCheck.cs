namespace GG2.Server;

/// <summary>What the server does about an <c>INPUTSTATE</c> a stock client cannot send.</summary>
/// <remarks>
/// <see cref="Log"/> by default, not <see cref="Kick"/>: a stock client never trips
/// <see cref="AimCheck.IsReachable"/>, but a client-side plugin is free to change how
/// the aim bytes are computed, and only an operator can tell such a plugin from a cheat.
/// The log says how often it fires on a real population before kicking is turned on.
/// </remarks>
public enum AimCheckMode
{
    /// <summary>No check.</summary>
    Off,

    /// <summary>One line per connection, the first time it sends an impossible aim.</summary>
    Log,

    /// <summary>The line, then a kick after <see cref="AimCheck.KickAfter"/> impossible aims.</summary>
    Kick,
}

/// <summary>
/// Whether an <c>INPUTSTATE</c>'s aim bytes could have come from a mouse.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stock client's aim is a whole-pixel vector.</b> <c>ClientInputstate.gml</c>
/// sends <c>point_direction</c> and <c>point_distance / 2</c> from the view's centre
/// to the pointer. Measured in a stock client (2026-10-05): <c>view_xview</c> is
/// stored as an integer, every view width and height in <c>rooms_fix_views.gml</c>
/// is even so the centre is integral, and <c>mouse_x</c> stays integral with the
/// window scaled (floored, 266.4 to 266). So the offset is an integer
/// <c>(dx, dy)</c>, and only directions that some integer offset at that distance
/// produces can arrive. An aimbot that writes the angle to a target's centre (a
/// fractional position) almost never lands on one: at distance byte <c>b</c> about
/// <c>25b</c> of the 65536 direction values are reachable.
/// </para>
/// <para>
/// <b>The encoding</b>, measured: <c>write_ushort</c> and <c>write_ubyte</c> round
/// half away from zero and clamp at the top (65535.6 and 65536 both write 65535).
/// The pointer at the centre writes 0 and 0.
/// <c>Fixtures/aim-encoding-samples.txt</c> is the client's own output over a grid
/// of offsets, and every row of it must pass.
/// </para>
/// <para>
/// ⚠️ <b>There is no distance ceiling.</b> The pointer is tracked outside the
/// window (measured: offset 1500,900 in an 800x600 window), so a distance byte of
/// 255 — "509 px or more" — is legal in every direction and always passes. A cheat
/// that sends 255, or that moves the real pointer, is not caught here at all.
/// </para>
/// <para>
/// A small slack on both rounding boundaries absorbs the difference between
/// GM8's x87 <c>arctan2</c> and <see cref="Math.Atan2"/>: a false kick costs far
/// more than a one-ULP miss.
/// </para>
/// </remarks>
public static class AimCheck
{
    /// <summary>Impossible aims a connection may send before <see cref="AimCheckMode.Kick"/> kicks it.</summary>
    /// <remarks>
    /// A stock client sends none, so this is not a tolerance for real players; it
    /// is enough evidence in the log line that an operator reading it is not
    /// looking at one corrupted packet.
    /// </remarks>
    public const int KickAfter = 30;

    private const double Slack = 1e-6;

    /// <summary>The ini's and the flag's spelling of a mode.</summary>
    public static string Name(AimCheckMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>Reads <c>off</c>, <c>log</c> or <c>kick</c>; <paramref name="what"/> names the source in the error.</summary>
    public static AimCheckMode ParseMode(string value, string what) => value.Trim().ToLowerInvariant() switch
    {
        "off" or "0" => AimCheckMode.Off,
        "log" or "1" => AimCheckMode.Log,
        "kick" or "2" => AimCheckMode.Kick,
        _ => throw new FormatException($"{what}: '{value}' is not an aim check mode. off, log or kick."),
    };

    /// <summary>Whether some integer pointer offset encodes to exactly these bytes.</summary>
    public static bool IsReachable(ushort direction, byte distance)
    {
        if (distance == byte.MaxValue)
            return true;

        // Distance below one pixel is the origin, whose direction GM8 reports as 0.
        if (distance == 0)
            return direction == 0;

        // round(d / 2) == b  <=>  d in [2b - 1, 2b + 1).
        var rMin = 2.0 * distance - 1 - Slack;
        var rMax = 2.0 * distance + 1 + Slack;

        // The wedge one direction value covers is under 0.03 px wide at this
        // range, so walking the dominant axis across the ring and testing the
        // nearest few pixels on the other axis visits every candidate.
        var theta = direction * (2 * Math.PI / 65536);
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);

        // Screen y points down: direction = atan2(-dy, dx).
        if (Math.Abs(cos) >= Math.Abs(sin))
        {
            var sign = cos >= 0 ? 1 : -1;
            var slope = -sin / cos;

            for (var a = (int)Math.Floor(rMin * Math.Abs(cos)) - 1; a <= (int)Math.Ceiling(rMax * Math.Abs(cos)) + 1; a++)
            {
                var dx = sign * a;
                var dy = (int)Math.Round(dx * slope);

                for (var k = dy - 1; k <= dy + 1; k++)
                    if (Encodes(dx, k, direction, distance))
                        return true;
            }
        }
        else
        {
            var sign = sin >= 0 ? -1 : 1;
            var slope = -cos / sin;

            for (var a = (int)Math.Floor(rMin * Math.Abs(sin)) - 1; a <= (int)Math.Ceiling(rMax * Math.Abs(sin)) + 1; a++)
            {
                var dy = sign * a;
                var dx = (int)Math.Round(dy * slope);

                for (var k = dx - 1; k <= dx + 1; k++)
                    if (Encodes(k, dy, direction, distance))
                        return true;
            }
        }

        return false;
    }

    /// <summary>Whether the client would write these bytes for this offset, within the slack.</summary>
    private static bool Encodes(int dx, int dy, ushort direction, byte distance)
    {
        var halfDistance = Math.Min(255.0, Math.Sqrt((double)dx * dx + (double)dy * dy) / 2);

        if (halfDistance < distance - 0.5 - Slack || halfDistance >= distance + 0.5 + Slack)
            return false;

        var degrees = Math.Atan2(-dy, dx) * (180 / Math.PI);
        if (degrees < 0)
            degrees += 360;

        var value = degrees * 65536 / 360;

        // write_ushort clamps, so the top value also covers [65535.5, 65536).
        if (direction == ushort.MaxValue)
            return value >= direction - 0.5 - Slack;

        return value >= direction - 0.5 - Slack && value < direction + 0.5 + Slack;
    }
}
