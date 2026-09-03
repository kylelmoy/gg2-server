namespace Gg2.Server;

/// <summary>
/// The server's one piece of policy over <c>CHAT_BUBBLE</c>: the April Fools
/// substitution.
/// </summary>
/// <remarks>
/// <c>processClientCommands.gml:194-206</c>. A chat bubble is otherwise relayed
/// untouched — the server does not validate the image id, and neither does this.
/// <para>
/// ⚠️ <b>This is server behaviour, not decoration, even though its effect is
/// cosmetic.</b> The rewrite happens before the broadcast, so what every client
/// draws is decided here; a port that relayed the byte unchanged would show a
/// different bubble from the reference on one day of the year and would relay a
/// bubble the reference drops on the other 364.
/// <c>docs/PROTOCOL.md</c>'s census called <c>calculateAprilFools</c> "a global
/// that only drawing code reads", which was wrong: this is the reader.
/// </para>
/// </remarks>
public static class ChatBubbles
{
    /// <summary>
    /// The one bubble that survives April Fools unrewritten.
    /// </summary>
    /// <remarks>
    /// No name in the reference, which spells it <c>45</c> inline. It is the
    /// index into <c>BubbleS</c>, so what it depicts is a client-side question.
    /// </remarks>
    public const byte ExemptOnAprilFools = 45;

    /// <summary>The bubble every other one becomes on April Fools.</summary>
    public const byte AprilFoolsBubble = 61;

    /// <summary>
    /// Whether <paramref name="local"/> falls on April 1st.
    /// </summary>
    /// <remarks>
    /// <c>Scripts/Holidays/calculateAprilFools.gml</c>, which reads
    /// <c>date_current_date()</c> — the host's <b>local</b> calendar date, not
    /// UTC. Transcribed as local on purpose: a server in UTC+13 keeps the joke
    /// on the day its players call April 1st, which is what the reference does.
    /// </remarks>
    public static bool IsAprilFools(DateTime local) => local is { Month: 4, Day: 1 };

    /// <summary>
    /// Applies the substitution, and says whether the bubble is relayed at all.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the reference drops the command without broadcasting —
    /// its bare <c>break</c> out of the command switch.
    /// </returns>
    /// <remarks>
    /// ⚠️ <b>The two clauses are <c>if</c> / <c>else if</c> and the order
    /// matters.</b> On April Fools a <c>61</c> takes the first branch and is
    /// rewritten to itself, so it is relayed; off April Fools the same byte
    /// reaches the second and is dropped. Flattening this into "drop every 61
    /// that we did not write" would drop a client's own 61 on the one day it is
    /// legitimate.
    /// </remarks>
    public static bool Rewrite(byte requested, bool aprilFools, out byte image)
    {
        if (aprilFools && requested != ExemptOnAprilFools)
        {
            image = AprilFoolsBubble;
            return true;
        }

        if (requested == AprilFoolsBubble && !aprilFools)
        {
            // A client sending the April Fools bubble on another day: dropped
            // outright rather than relayed or corrected.
            image = requested;
            return false;
        }

        image = requested;
        return true;
    }
}
