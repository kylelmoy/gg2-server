using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// One recorded wire action, read back as the heads a policy would have had to
/// emit to produce it.
/// </summary>
/// <remarks>
/// <b>The aim head is the lossy one and the loss is the measurement.</b>
/// Everything else is a bit: a key was held or it was not. Aim is a bounded
/// delta (<see cref="ActionDecoder.MaxTurnDegreesPerDecision"/> over
/// <see cref="ActionDecoder.AimBins"/> bins) and a mouse is not bounded at all,
/// so a person's flick has no head that reproduces it. Both the real delta and
/// the bin are kept, and <see cref="Saturated"/> says which rows are the ones
/// the action space could not express.
/// </remarks>
public readonly record struct EncodedAction
{
    /// <summary>The heads, as <c>ActionDecoder.Decode</c> would consume them.</summary>
    public required EnvAction Action { get; init; }

    /// <summary>
    /// The turn actually taken, in degrees, signed and in (-180, 180].
    /// </summary>
    /// <remarks>
    /// <b>Unclamped, unbinned, and the only lossless record of a human's aim.</b>
    /// Phase 5's turn-rate cap is fitted to the distribution of this number, so
    /// storing only the bin would destroy the very thing the recording was made
    /// to measure.
    /// </remarks>
    public required double TurnDegrees { get; init; }

    /// <summary>
    /// Whether <see cref="TurnDegrees"/> exceeded what one head can express, so
    /// <see cref="Action"/>'s aim bin is a clamp rather than a description.
    /// </summary>
    /// <remarks>
    /// <b>The saturation rate over a corpus is the answer to "is 30 degrees a
    /// decision the right cap".</b> A cap that no human ever reaches is not
    /// constraining anything; one that a human exceeds on a third of decisions
    /// is not a turn-rate cap, it is a different game. Neither is visible from
    /// the binned action alone, which is why this is a field and not a comment.
    /// </remarks>
    public required bool Saturated { get; init; }
}

/// <summary>
/// Turns a recorded <see cref="PlayerInput"/> back into action heads.
/// </summary>
/// <remarks>
/// <b>Beside <see cref="ActionDecoder"/> because it is its inverse, and a pair
/// that disagrees is undetectable downstream.</b> Every constant here is read
/// from that class rather than restated: the bin count, the centre bin and the
/// turn cap are the encoding, and a second copy of any of them would produce a
/// dataset labelled in an action space no policy is trained in.
/// <para>
/// <b>This is a read of somebody else's action, not a decision.</b> It holds no
/// state of its own — the previous aim comes in as an argument — because a
/// trace is replayed for several slots at once and a decoder's per-slot aim
/// array would have to be kept in step with a roster that compacts.
/// </para>
/// </remarks>
public static class ActionEncoder
{
    /// <summary>
    /// Encodes the action <paramref name="input"/> represents, given where this
    /// character was aiming before it.
    /// </summary>
    /// <param name="input">The recorded wire action.</param>
    /// <param name="previousAimDegrees">
    /// The aim this character held on the previous decision, in degrees.
    /// </param>
    /// <param name="character">
    /// The character acting, for the sniper's scope level. Zoom is a
    /// <em>level</em> on the wire's side too — <c>CharacterState.Zoomed</c> —
    /// so it is read rather than derived from the keys.
    /// </param>
    public static EncodedAction Encode(
        in PlayerInput input, double previousAimDegrees, in CharacterState character)
    {
        var keys = input.Keys;

        // Left beats right when a client sends both, which is what the
        // reference's own Begin Step does: it tests left first and the horizontal
        // head has no "both" value to encode it into. A row that silently picked
        // the other one would teach the opposite of what the player did on
        // exactly the ticks where they were changing direction.
        var horizontal = (byte)(keys.HasFlag(PlayerKeys.Left) ? 1
            : keys.HasFlag(PlayerKeys.Right) ? 2
            : 0);

        // Attack beats special, for the same reason and with the same caveat.
        var fire = (byte)(keys.HasFlag(PlayerKeys.Attack) ? 1
            : keys.HasFlag(PlayerKeys.Special) ? 2
            : 0);

        var turn = SignedDelta(input.AimDirectionDegrees, previousAimDegrees);

        var cap = ActionDecoder.MaxTurnDegreesPerDecision;
        var saturated = Math.Abs(turn) > cap;

        // Rounded to the nearest bin rather than truncated: the bins are
        // symmetric about AimCentreBin and truncation would bias every turn
        // towards no-turn, which is a bias on the exact statistic phase 5 fits.
        var step = cap / ActionDecoder.AimCentreBin;
        var offset = (int)Math.Round(Math.Clamp(turn, -cap, cap) / step,
            MidpointRounding.AwayFromZero);

        var bin = (byte)Math.Clamp(
            ActionDecoder.AimCentreBin + offset, 0, ActionDecoder.AimBins - 1);

        return new EncodedAction
        {
            Action = new EnvAction
            {
                Horizontal = horizontal,

                // ⚠️ The JUMP HEAD IS AN EDGE and this cannot see one. The bit
                // is set for as long as the player holds the key, while the head
                // means "jump now" and the decoder emits it on the first tick of
                // a decision only (ActionDecoder.Decode). So a held key encodes
                // as a press on every decision it spans — which is what the
                // player asked for and NOT what the game did, because
                // Character's Begin Step jumps on pressedKeys. A consumer
                // counting jumps from this over-counts a held key; one training
                // on it is training the head's own meaning, which is correct.
                Jump = (byte)(keys.HasFlag(PlayerKeys.Jump) ? 1 : 0),
                Down = (byte)(keys.HasFlag(PlayerKeys.Down) ? 1 : 0),
                Fire = fire,
                AimDelta = bin,
                Zoom = (byte)(character.Zoomed ? 1 : 0),
            },
            TurnDegrees = turn,
            Saturated = saturated,
        };
    }

    /// <summary>
    /// The shortest signed turn from <paramref name="from"/> to
    /// <paramref name="to"/>, in (-180, 180].
    /// </summary>
    /// <remarks>
    /// <b>Shortest, not arithmetic.</b> A player crossing due-east goes from 359
    /// degrees to 1; subtracting reads that as a 358-degree flick, which would
    /// put an ordinary tracking tick in the saturated tail and corrupt the very
    /// distribution the recording exists to measure.
    /// </remarks>
    private static double SignedDelta(double to, double from)
    {
        var delta = (to - from) % 360.0;

        if (delta > 180.0)
            delta -= 360.0;
        else if (delta <= -180.0)
            delta += 360.0;

        return delta;
    }
}
