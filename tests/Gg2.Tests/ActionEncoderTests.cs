using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Reading a recorded action back as the heads that would have produced it.
/// </summary>
/// <remarks>
/// <b>The gate that matters is the round trip</b>, because
/// <see cref="ActionEncoder"/> and <c>ActionDecoder</c> are inverses and a pair
/// that disagrees produces a dataset labelled in an action space no policy is
/// trained in — every row valid, every row wrong, and nothing downstream able
/// to say so.
/// </remarks>
public sealed class ActionEncoderTests
{
    private static CharacterState Scout() =>
        CharacterState.Fresh(PlayerClass.Scout, Team.Red);

    /// <summary>
    /// Anything the decoder can emit, the encoder reads back unchanged.
    /// </summary>
    /// <remarks>
    /// Driven through <c>ActionDecoder.Decode</c> rather than by constructing a
    /// <c>PlayerInput</c> by hand: the claim is about the pair, so the input
    /// side has to come from the real decoder including its own aim
    /// accumulation and its <c>PlayerInput.FromAim</c> quantisation.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, 0, 0, ActionDecoder.AimCentreBin)]
    [InlineData(1, 0, 0, 0, ActionDecoder.AimCentreBin)]
    [InlineData(2, 1, 1, 1, 0)]
    [InlineData(0, 0, 1, 2, 14)]
    [InlineData(1, 1, 0, 2, 3)]
    [InlineData(2, 0, 0, 1, 11)]
    public void EveryActionTheDecoderCanEmitReadsBackAsItself(
        byte horizontal, byte jump, byte down, byte fire, byte aim)
    {
        var decoder = new ActionDecoder();
        const int slot = 1;

        decoder.SetAim(slot, 90.0);
        var before = decoder.Aim(slot);

        var sent = new EnvAction
        {
            Horizontal = horizontal,
            Jump = jump,
            Down = down,
            Fire = fire,
            AimDelta = aim,
            Zoom = 0,
        };

        var input = decoder.Decode(slot, sent, firstTickOfDecision: true);
        var read = ActionEncoder.Encode(in input, before, Scout());

        Assert.Equal(sent.Horizontal, read.Action.Horizontal);
        Assert.Equal(sent.Jump, read.Action.Jump);
        Assert.Equal(sent.Down, read.Action.Down);
        Assert.Equal(sent.Fire, read.Action.Fire);
        Assert.Equal(sent.AimDelta, read.Action.AimDelta);

        // A turn the decoder produced is by definition inside the cap, so
        // nothing round-tripped can be saturated. If this ever fires the bin
        // arithmetic has drifted from TurnFor's.
        Assert.False(read.Saturated, $"a decoder-produced turn of {read.TurnDegrees} read as saturated");
    }

    /// <summary>
    /// A turn across due-east is a small turn, not a 358-degree flick.
    /// </summary>
    /// <remarks>
    /// <b>The failure this catches is a corrupted distribution, not a crash.</b>
    /// Aim is an angle in [0, 360) and a player tracking through zero produces
    /// an arithmetic difference of nearly a full circle. Left unwrapped, the
    /// most ordinary tracking tick there is lands in the saturated tail — and
    /// the saturation rate is the statistic phase 5's turn-rate cap is fitted
    /// to, so the cap would be fitted to an artefact of the subtraction.
    /// </remarks>
    [Fact]
    public void ATurnAcrossZeroIsTheShortWayRound()
    {
        var input = PlayerInput.FromAim(PlayerKeys.None, 2.0, 100);
        var read = ActionEncoder.Encode(in input, 358.0, Scout());

        Assert.InRange(read.TurnDegrees, 3.0, 5.0);
        Assert.False(read.Saturated);

        var back = PlayerInput.FromAim(PlayerKeys.None, 358.0, 100);
        var other = ActionEncoder.Encode(in back, 2.0, Scout());

        Assert.InRange(other.TurnDegrees, -5.0, -3.0);
    }

    /// <summary>
    /// A flick no head can express is recorded as one, and its real size
    /// survives.
    /// </summary>
    /// <remarks>
    /// <b>This is the measurement, not an error case.</b> A mouse is unbounded
    /// and the aim head is not, so a person's fast turn has no head that
    /// reproduces it. Keeping the clamped bin alone would silently rewrite a
    /// 90-degree flick as a 30-degree one and teach a policy that people do not
    /// flick.
    /// </remarks>
    [Fact]
    public void AFlickBeyondTheCapIsFlaggedAndItsSizeKept()
    {
        var input = PlayerInput.FromAim(PlayerKeys.None, 90.0, 100);
        var read = ActionEncoder.Encode(in input, 0.0, Scout());

        Assert.True(read.Saturated, "a 90-degree turn did not exceed the 30-degree cap");
        Assert.InRange(read.TurnDegrees, 89.0, 91.0);

        // Clamped to the extreme bin rather than wrapping round to a left turn.
        Assert.Equal(ActionDecoder.AimBins - 1, read.Action.AimDelta);
    }

    /// <summary>
    /// Both horizontal bits at once resolve the way the game resolves them.
    /// </summary>
    /// <remarks>
    /// A client can send left and right together and the reference's Begin Step
    /// tests left first. The head has no value for "both", so the encoder has to
    /// pick — and picking the other one would mislabel exactly the ticks on
    /// which a player was changing direction, which is the behaviour a
    /// key-flip statistic is about.
    /// </remarks>
    [Fact]
    public void BothDirectionsAtOnceResolveLeft()
    {
        var input = PlayerInput.FromAim(PlayerKeys.Left | PlayerKeys.Right, 0.0, 100);
        var read = ActionEncoder.Encode(in input, 0.0, Scout());

        Assert.Equal(1, read.Action.Horizontal);
    }

    /// <summary>The scope level is read off the character, not off the keys.</summary>
    /// <remarks>
    /// Zoom is not a key — it is a <c>PlayerCommands.ToggleZoom</c> command and
    /// the head is a level. A row that derived it from <c>keyState</c> would
    /// report every sniper as unscoped.
    /// </remarks>
    [Fact]
    public void TheScopeLevelComesFromTheCharacter()
    {
        var sniper = CharacterState.Fresh(PlayerClass.Sniper, Team.Red);
        sniper.Zoomed = true;

        var input = PlayerInput.FromAim(PlayerKeys.None, 0.0, 100);

        Assert.Equal(1, ActionEncoder.Encode(in input, 0.0, sniper).Action.Zoom);

        sniper.Zoomed = false;
        Assert.Equal(0, ActionEncoder.Encode(in input, 0.0, sniper).Action.Zoom);
    }

    /// <summary>
    /// Turns are binned by rounding, so no-turn is not over-reported.
    /// </summary>
    /// <remarks>
    /// Truncation would push every turn towards <c>AimCentreBin</c> — a bias on
    /// the exact statistic this data is collected to measure, and one that looks
    /// like a finding about how people aim.
    /// </remarks>
    [Fact]
    public void ASmallTurnIsRoundedNotTruncatedToNoTurn()
    {
        // One bin is 30/7 degrees, so three degrees is most of one and must not
        // read as standing still.
        var input = PlayerInput.FromAim(PlayerKeys.None, 3.0, 100);
        var read = ActionEncoder.Encode(in input, 0.0, Scout());

        Assert.NotEqual(ActionDecoder.AimCentreBin, read.Action.AimDelta);
    }
}
