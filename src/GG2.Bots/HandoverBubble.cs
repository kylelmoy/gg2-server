namespace GG2.Bots;

/// <summary>
/// The chat bubbles a bot puts up when the wheel changes hands, so a brain can
/// be watched from inside the game rather than off a panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>A debugging aid, off unless asked for.</b> <c>HybridBot.Bubbles</c> gates
/// it and defaults to false; the live server reaches that through
/// <c>--bot-handover-bubbles</c> or <c>[Bots] HandoverBubbles</c>. It is off by
/// default because a bubble is a broadcast to every connected client: on a
/// server with people on it, eight bots handing over is eight extra messages a
/// tick and a screen full of speech balloons.
/// </para>
/// <para>
/// <b>Nothing here reaches observable simulation.</b> A bubble is a broadcast
/// and a client-side sprite; unlike a taunt it does not freeze the character or
/// touch its keys, so turning this on cannot change what a bot does. It travels
/// as a <c>PlayerCommand</c> through the same <c>ExecuteChatBubble</c> a socket
/// reaches, so the April Fools rewrite and the per-player throttle apply — which
/// is deliberate. ⚠️ <b>On April 1st every one of these becomes bubble 61</b>
/// and the aid is useless for the day. Special-casing a bot to avoid that is the
/// one thing the bot layer may never do.
/// </para>
/// </remarks>
public static class HandoverBubble
{
    /// <summary>
    /// <c>z1</c>: a trained checkpoint took the wheel.
    /// </summary>
    /// <remarks>
    /// The three indices are the client's own <c>z</c> menu, which is where an
    /// operator watching a game will recognise them from:
    /// <c>Objects/Overlays/Interfaces/BubbleMenuZ.events/Key {1,2,3} pressed.xml</c>
    /// set <c>bubbleImage</c> to 20, 21 and 22 and send <c>CHAT_BUBBLE</c>.
    /// </remarks>
    public const byte TookPolicy = 20;

    /// <summary><c>z2</c>: the scripted layer took the wheel back.</summary>
    public const byte LeftAController = 21;

    /// <summary><c>z3</c>: the rollout search took the wheel.</summary>
    public const byte TookSearch = 22;

    /// <summary>
    /// Which bubble a handover asks for, or null for one nothing depicts.
    /// </summary>
    /// <param name="taking">
    /// Whoever is taking the wheel, or null for the scripted layer.
    /// </param>
    /// <remarks>
    /// <b>Keyed off <see cref="IMicroController.Name"/>, which is what that
    /// member is for</b> — "a diagnostic, a control-port roster or a bench
    /// column". A type test would tie this file to the two shipped controllers
    /// and would answer nothing for a third; the name is the seam's own identity
    /// and a new controller declares it as a matter of course.
    /// <para>
    /// ⚠️ <b>A prefix match, because a controller may be labelled.</b>
    /// <c>PolicyController</c> answers the bare word today, but
    /// <c>MicroControllerSpec</c> already carries <c>policy:&lt;checkpoint&gt;</c>
    /// and a controller that grew the same habit should keep its bubble.
    /// </para>
    /// <para>
    /// ⚠️ <b>An unrecognised controller gets NO bubble, and that is a gap to
    /// close rather than a design.</b> Silence is the safe failure for a
    /// debugging aid — it cannot mislabel a brain — but a fourth controller
    /// arriving without a line here is invisible to this aid and nothing goes
    /// red. Add its bubble when you add it.
    /// </para>
    /// </remarks>
    public static byte? For(IMicroController? taking)
    {
        if (taking is null)
            return LeftAController;

        if (taking.Name.StartsWith("policy", StringComparison.Ordinal))
            return TookPolicy;

        if (taking.Name.StartsWith("search", StringComparison.Ordinal))
            return TookSearch;

        return null;
    }
}
