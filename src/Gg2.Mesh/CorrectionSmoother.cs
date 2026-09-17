namespace Gg2.Mesh;

/// <summary>
/// Chooses the position to SEND a client, given where the world really is and
/// where that client currently thinks it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>A node knows exactly what its clients believe.</b> A GG2 client
/// dead-reckons every character from the keys it was last told and snaps when a
/// position arrives, and that dead reckoning is the same deterministic
/// simulation the node is running — so the node can reproduce a client's belief
/// rather than guess at it. That is what makes this possible at all.
/// </para>
/// <para>
/// Given the belief, a correction does not have to be a teleport. Sending a
/// position part of the way from the belief toward the truth closes a fixed
/// fraction of the error each time, so the character walks to where it belongs
/// over a few updates instead of jumping there in one.
/// </para>
/// <para>
/// ⛔ <b>This deliberately sends a position that is NOT the server's.</b> It is
/// the one place in this project where that is on purpose, it is bounded, and it
/// is temporary: the error shrinks geometrically and the client is inside the
/// wire's own quantisation within a few updates. The justification is that the
/// client is ALREADY somewhere wrong — the choice is between arriving at the
/// truth abruptly or smoothly, not between wrong and right.
/// </para>
/// <para>
/// ⚠️ <b>A large correction is NOT smoothed, and that is the important case.</b>
/// A respawn moves a character across the map; sliding it there over half a
/// second would show a body gliding through walls, and worse, the client
/// simulates from whatever position it is given — so the intermediate positions
/// have to be ones the character could plausibly be at. Beyond
/// <see cref="TeleportAbove"/> the truth is sent as-is.
/// </para>
/// </remarks>
public sealed class CorrectionSmoother
{
    /// <summary>
    /// How much of the remaining error to leave in place on each correction.
    /// </summary>
    /// <remarks>
    /// 0 sends the truth and is what an unsmoothed server does. 0.5 halves the
    /// error each update — at the wire's seven-tick cadence that is inside the
    /// <c>x*5</c> quantum after about six updates, a fifth of a second. Above
    /// about 0.7 the tail is long enough that a player can see the character
    /// lagging its own inputs, which is the thing being traded for.
    /// </remarks>
    public double Retain { get; init; } = 0.5;

    /// <summary>
    /// The error beyond which a correction is sent whole rather than smoothed.
    /// </summary>
    /// <remarks>
    /// <b>64px is about four character widths</b>, far past anything a
    /// mispredicted key produces — measured, the 99th percentile of a two-tick
    /// prediction error is 14px and the worst seen is 34px — and comfortably
    /// below a respawn, which crosses the map. So this separates "the prediction
    /// was wrong" from "this character is somewhere else entirely".
    /// </remarks>
    public double TeleportAbove { get; init; } = 64;

    /// <summary>
    /// The position to send for one character.
    /// </summary>
    /// <param name="truthX">Where the node says the character is.</param>
    /// <param name="truthY">Where the node says the character is.</param>
    /// <param name="beliefX">Where this client currently thinks it is.</param>
    /// <param name="beliefY">Where this client currently thinks it is.</param>
    public (double X, double Y) Correct(
        double truthX, double truthY, double beliefX, double beliefY)
    {
        var dx = beliefX - truthX;
        var dy = beliefY - truthY;

        if (Math.Sqrt((dx * dx) + (dy * dy)) > TeleportAbove)
            return (truthX, truthY);

        return (truthX + (dx * Retain), truthY + (dy * Retain));
    }

    /// <summary>
    /// How far a client would visibly jump if it were sent this correction.
    /// </summary>
    /// <remarks>
    /// The number a player actually experiences, and the one worth reporting:
    /// the error itself is what the node had to fix, and this is how much of it
    /// the client is asked to swallow at once.
    /// </remarks>
    public double Jump(double truthX, double truthY, double beliefX, double beliefY)
    {
        var (x, y) = Correct(truthX, truthY, beliefX, beliefY);

        return Math.Sqrt(((x - beliefX) * (x - beliefX)) + ((y - beliefY) * (y - beliefY)));
    }
}
