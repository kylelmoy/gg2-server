using GG2.Bots;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// <see cref="ActionDecoder"/> — heads to <see cref="PlayerInput"/>.
/// </summary>
/// <remarks>
/// <c>gg2-rl/docs/ENV.md</c>'s gate list names three properties for this type, and all
/// three are here because each one is a bug this project has already paid for
/// somewhere else.
/// </remarks>
public sealed class ActionDecoderTests
{
    private static EnvAction Action(
        byte horizontal = 0, byte jump = 0, byte down = 0, byte fire = 0,
        byte aim = ActionDecoder.AimCentreBin) =>
        new() { Horizontal = horizontal, Jump = jump, Down = down, Fire = fire, AimDelta = aim };

    /// <summary>
    /// <b>Jump is emitted on the first tick of a decision and no other.</b>
    /// </summary>
    /// <remarks>
    /// GG2 jumps on the PRESSED edge, so a held bit fires once and then does
    /// nothing. CLAUDE.md records the cost of learning that the hard way: a
    /// driven client with the jump bit held "climbed exactly one step and then
    /// sat there looking wedged", and several minutes went into suspecting the
    /// bridge. Under an action repeat of k, emitting the bit on all k ticks
    /// spends one jump and wastes k-1 ticks of the head.
    /// </remarks>
    [Fact]
    public void JumpIsAnEdgeAndNotAHold()
    {
        var decoder = new ActionDecoder();
        var jump = Action(jump: 1);

        Assert.True(decoder.Decode(1, jump, firstTickOfDecision: true).Jump);
        Assert.False(decoder.Decode(1, jump, firstTickOfDecision: false).Jump);
        Assert.False(decoder.Decode(1, jump, firstTickOfDecision: false).Jump);

        // A fresh decision presses again.
        Assert.True(decoder.Decode(1, jump, firstTickOfDecision: true).Jump);
    }

    /// <summary>
    /// <b>The mask is built fresh every tick, so no bit can survive a change
    /// of direction.</b>
    /// </summary>
    /// <remarks>
    /// The same trap CLAUDE.md records for driving a client through
    /// <c>gg2_watch</c>: <c>heldMask = heldMask | 32</c> leaves an earlier
    /// <c>left</c> bit set and the character walks the wrong way — measured, and
    /// it looked like the physics being wrong. A decoder that accumulated would
    /// reproduce it exactly.
    /// </remarks>
    [Fact]
    public void TheMaskIsAbsoluteSoAnOldDirectionCannotSurvive()
    {
        var decoder = new ActionDecoder();

        var left = decoder.Decode(1, Action(horizontal: 1), true);
        Assert.True(left.Left);
        Assert.False(left.Right);

        var right = decoder.Decode(1, Action(horizontal: 2), true);
        Assert.True(right.Right);
        Assert.False(right.Left);

        var nothing = decoder.Decode(1, Action(), true);
        Assert.False(nothing.Left);
        Assert.False(nothing.Right);
        Assert.False(nothing.Jump);
        Assert.False(nothing.Attack);
        Assert.False(nothing.Down);
    }

    /// <summary>Each head maps to the bit it claims, and to no other.</summary>
    [Fact]
    public void EachHeadSetsItsOwnBit()
    {
        var decoder = new ActionDecoder();

        Assert.True(decoder.Decode(1, Action(down: 1), true).Down);
        Assert.True(decoder.Decode(1, Action(fire: 1), true).Attack);
        Assert.False(decoder.Decode(1, Action(fire: 1), true).Special);
        Assert.True(decoder.Decode(1, Action(fire: 2), true).Special);
        Assert.False(decoder.Decode(1, Action(fire: 2), true).Attack);

        // Taunt has no head at all: it is not implemented in the port, so a
        // head for it would be a head that trains on nothing.
        Assert.False(decoder.Decode(1, Action(fire: 1), true).Taunt);
    }

    /// <summary>
    /// The aim turns by at most the cap per decision, in either direction, and
    /// the middle bin does not turn at all.
    /// </summary>
    [Fact]
    public void TheAimTurnsAtMostTheCapPerDecision()
    {
        Assert.Equal(0, ActionDecoder.TurnFor(ActionDecoder.AimCentreBin));
        Assert.Equal(-ActionDecoder.MaxTurnDegreesPerDecision, ActionDecoder.TurnFor(0));
        Assert.Equal(ActionDecoder.MaxTurnDegreesPerDecision, ActionDecoder.TurnFor(14));

        var decoder = new ActionDecoder();
        decoder.SetAim(1, 0);

        decoder.Decode(1, Action(aim: 14), true);
        Assert.Equal(ActionDecoder.MaxTurnDegreesPerDecision, decoder.Aim(1), 9);

        decoder.Decode(1, Action(aim: 0), true);
        Assert.Equal(0, decoder.Aim(1), 9);
    }

    /// <summary>
    /// <b>The turn happens once per DECISION, not once per tick.</b>
    /// </summary>
    /// <remarks>
    /// Repeating it across an action repeat of k would make the effective cap k
    /// times larger and quietly undo the honesty property the cap exists for —
    /// the policy would be able to spin four times as fast simply because the
    /// trainer chose a longer repeat.
    /// </remarks>
    [Fact]
    public void TheTurnIsNotRepeatedAcrossAnActionRepeat()
    {
        var decoder = new ActionDecoder();
        decoder.SetAim(1, 0);

        var turn = Action(aim: 14);

        decoder.Decode(1, turn, firstTickOfDecision: true);
        decoder.Decode(1, turn, firstTickOfDecision: false);
        decoder.Decode(1, turn, firstTickOfDecision: false);
        decoder.Decode(1, turn, firstTickOfDecision: false);

        Assert.Equal(ActionDecoder.MaxTurnDegreesPerDecision, decoder.Aim(1), 9);
    }

    /// <summary>
    /// <b>A spread turn arrives at the same place in equal steps</b>, under the
    /// same cap: gg2-rl phase 5, C0.
    /// </summary>
    [Fact]
    public void ASpreadTurnArrivesInEqualStepsAtTheSameTotal()
    {
        var once = new ActionDecoder();
        var spread = new ActionDecoder();
        once.SetAim(1, 350);
        spread.SetAim(1, 350);

        var turn = Action(aim: 13);
        var step = ActionDecoder.TurnFor(13) / 4;

        for (var tick = 0; tick < 4; tick++)
        {
            var before = spread.Aim(1);
            spread.Decode(1, turn, firstTickOfDecision: tick == 0, turnTicks: 4);
            once.Decode(1, turn, firstTickOfDecision: tick == 0);

            var moved = ((spread.Aim(1) - before) % 360 + 360) % 360;
            Assert.Equal(step, moved, 9);
        }

        Assert.Equal(once.Aim(1), spread.Aim(1), 9);
    }

    /// <summary>
    /// <b>One tick of spread is the old decoder exactly</b>, so every checkpoint
    /// that trained without it decodes the same.
    /// </summary>
    [Fact]
    public void ASpreadOfOneIsTheDecoderEveryCheckpointTrainedWith()
    {
        var plain = new ActionDecoder();
        var one = new ActionDecoder();
        var random = new Random(7);

        for (var tick = 0; tick < 400; tick++)
        {
            var action = Action(horizontal: (byte)random.Next(3), jump: (byte)random.Next(2), fire: (byte)random.Next(3), aim: (byte)random.Next(15));
            var first = tick % 4 == 0;

            Assert.Equal(plain.Decode(1, action, first), one.Decode(1, action, first, turnTicks: 1));
        }
    }

    /// <summary>
    /// The aim reaches the wire on the same 65,536 steps a client's does.
    /// </summary>
    /// <remarks>
    /// <c>docs/BOTAI.md</c> item 4: a bot that aimed on a finer grid than the
    /// wire carries would be aiming somewhere no human can. Going through
    /// <see cref="PlayerInput.FromAim"/> is what guarantees it, and this asserts
    /// the round trip rather than the call.
    /// </remarks>
    [Fact]
    public void TheAimIsQuantisedToTheWiresGrid()
    {
        var decoder = new ActionDecoder();
        decoder.SetAim(1, 0);

        // One bin is 30/7 degrees, which is not a whole number of wire steps.
        var input = decoder.Decode(1, Action(aim: 8), true);

        var expected = (ushort)Math.Round(decoder.Aim(1) * 65536.0 / 360.0) % 65536;
        Assert.Equal(expected, input.NetAimDirection);

        // And the decoded angle is within half a wire step of the tracked one.
        //
        // Asserted as a bound rather than with Assert.Equal's decimal places:
        // a step is 360/65536 = 0.00549 degrees, and comparing to two places
        // makes the test a question about which side of a rounding boundary the
        // value fell on rather than about accuracy. The first draft did that and
        // failed on 4.2857 versus 4.2847.
        const double step = 360.0 / 65536.0;

        Assert.True(
            Math.Abs(decoder.Aim(1) - input.AimDirectionDegrees) <= step / 2,
            $"tracked {decoder.Aim(1)} but the wire carries {input.AimDirectionDegrees}, "
                + $"which is more than half a step ({step / 2}) away");
    }

    /// <summary>
    /// Two slots keep separate aims, so one agent turning cannot move the other.
    /// </summary>
    [Fact]
    public void EachSlotHasItsOwnAim()
    {
        var decoder = new ActionDecoder();
        decoder.SetAim(1, 0);
        decoder.SetAim(2, 180);

        decoder.Decode(1, Action(aim: 14), true);

        Assert.Equal(ActionDecoder.MaxTurnDegreesPerDecision, decoder.Aim(1), 9);
        Assert.Equal(180, decoder.Aim(2), 9);
    }

    /// <summary>The aim wraps rather than growing without bound.</summary>
    [Fact]
    public void TheAimStaysInOneTurn()
    {
        var decoder = new ActionDecoder();
        decoder.SetAim(1, 350);

        decoder.Decode(1, Action(aim: 14), true);

        Assert.InRange(decoder.Aim(1), 0, 360);
        Assert.Equal(20, decoder.Aim(1), 9);

        decoder.SetAim(1, 5);
        decoder.Decode(1, Action(aim: 0), true);

        Assert.InRange(decoder.Aim(1), 0, 360);
        Assert.Equal(335, decoder.Aim(1), 9);
    }
}
