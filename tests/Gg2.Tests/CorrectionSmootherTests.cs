using Gg2.Mesh;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// What a client is asked to swallow when a node corrects it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is that a correction can be spread without being lost.</b> Two
/// things have to be true together: each update moves the client less than the
/// whole error (or smoothing is doing nothing), and the client still ends up at
/// the truth (or smoothing has replaced a snap with a permanent lie). Either one
/// alone is trivially satisfiable — sending the belief back unchanged passes the
/// first, sending the truth passes the second.
/// </para>
/// <para>
/// ⚠️ These are geometry, not physics. Whether a 7px step reads as a snap or as
/// nothing is a question about a person watching a screen, and no assertion here
/// answers it — `docs/MESH.md` says so.
/// </para>
/// </remarks>
public sealed class CorrectionSmootherTests
{
    /// <summary>
    /// The client converges on the truth, and gets there in a fraction of a
    /// second.
    /// </summary>
    /// <remarks>
    /// A position update rides every seventh tick, so six updates is about a
    /// fifth of a second. The floor is the wire's own <c>x*5</c> quantisation:
    /// closer than 0.2px is not a distinction a client can be told about.
    /// </remarks>
    [Fact]
    public void AClientThatIsNotMovingConvergesOnTheTruth()
    {
        var smoother = new CorrectionSmoother();

        // A believed position 14px out — the measured 99th percentile of a
        // two-tick prediction error.
        var beliefX = 414.0;
        const double TruthX = 400;

        var updates = 0;

        while (Math.Abs(beliefX - TruthX) > 0.2 && updates < 20)
        {
            (beliefX, _) = smoother.Correct(TruthX, 0, beliefX, 0);
            updates++;
        }

        Assert.True(Math.Abs(beliefX - TruthX) <= 0.2, $"still {beliefX - TruthX:0.00}px out");
        Assert.True(updates <= 7, $"took {updates} updates, which is over a fifth of a second");
    }

    /// <summary>
    /// And it moves less in one step than the error it is fixing — otherwise
    /// nothing has been smoothed.
    /// </summary>
    [Fact]
    public void NoSingleStepIsTheWholeError()
    {
        var smoother = new CorrectionSmoother();

        foreach (var error in new[] { 1.0, 8.0, 14.0, 34.0 })
        {
            var jump = smoother.Jump(400, 0, 400 + error, 0);

            Assert.True(
                jump < error,
                $"a {error}px error moved the client {jump}px in one update, which is all of it");

            Assert.Equal(error * (1 - smoother.Retain), jump, 6);
        }
    }

    /// <summary>
    /// ⛔ The control: an unsmoothed node moves the client by the whole error.
    /// </summary>
    /// <remarks>
    /// Without this, every assertion above would pass against a smoother that
    /// was secretly doing nothing — the "jump" and the "error" would be the same
    /// number and the comparison would still hold in the cases that matter.
    /// </remarks>
    [Fact]
    public void WithoutSmoothingTheStepIsTheWholeError()
    {
        var raw = new CorrectionSmoother { Retain = 0 };

        Assert.Equal(14, raw.Jump(400, 0, 414, 0), 6);
        Assert.Equal((400, 0), raw.Correct(400, 0, 414, 0));
    }

    /// <summary>
    /// ⛔ A correction too large to be a mispredicted key is sent whole.
    /// </summary>
    /// <remarks>
    /// <b>The case that makes smoothing dangerous rather than merely useless.</b>
    /// A respawn moves a character across the map, and the client simulates from
    /// whatever position it is given — so gliding it there would put a body
    /// through walls on the way. Anything past four character widths is treated
    /// as "somewhere else entirely" rather than "slightly wrong".
    /// </remarks>
    [Fact]
    public void ARespawnSizedCorrectionIsNotSmoothed()
    {
        var smoother = new CorrectionSmoother();

        Assert.Equal((400.0, 268.0), smoother.Correct(400, 268, 900, 120));

        // ...and the boundary is where it says it is.
        var justInside = smoother.TeleportAbove - 1;
        var (x, _) = smoother.Correct(400, 0, 400 + justInside, 0);

        Assert.True(x > 400, "a correction just inside the teleport bound was sent whole");
    }

    /// <summary>
    /// A client that is already right is left alone.
    /// </summary>
    /// <remarks>
    /// The degenerate case, asserted because the alternative is a node that
    /// nudges every character every update and turns a still world into a
    /// shimmering one.
    /// </remarks>
    [Fact]
    public void AClientThatAgreesIsNotMoved()
    {
        var smoother = new CorrectionSmoother();

        Assert.Equal((400.0, 268.0), smoother.Correct(400, 268, 400, 268));
        Assert.Equal(0, smoother.Jump(400, 268, 400, 268), 6);
    }
}
