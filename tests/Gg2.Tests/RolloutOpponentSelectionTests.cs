using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <see cref="DuelSpec.RedRolloutOpponent"/> reaches the search, and changes it.
/// </summary>
/// <remarks>
/// <c>DuelEnv.MakeBot</c> answered <see cref="EnvDriver.Rollout"/> with
/// <c>new RolloutSearch()</c>, so no batch could ask for anything but whatever
/// that constructor happened to pick — and what it picked was
/// <see cref="RolloutOpponent.Idle"/>, the opposite of the documented default,
/// because <see cref="RolloutOpponent"/> declared <c>Idle</c> first and
/// <c>default(RolloutOptions)</c> is all zeroes. Both halves are fixed; see the
/// enum's own remark, which now says the ordering is load-bearing.
/// <para>
/// ⚠️ <b>The number this exists for is a DIFFERENCE, not a level.</b>
/// <c>gg2-rl</c>'s phase 1 reports the search baseline under both models: if the
/// win rate barely moves the search is not really using its opponent model and
/// the horizon or the scoring is the binding constraint; if it moves a lot, that
/// is the argument for paying for self-play rollouts.
/// </para>
/// <para>
/// ⚠️⚠️ <b>AND THE MAP DECIDES WHETHER THAT DIFFERENCE IS VISIBLE AT ALL.</b>
/// Measured over 36 scenarios — four classes, three maps, three seeds — the two
/// models chose <b>identical action sequences in 26 of them</b>. Every
/// divergence but one was on <c>ctf_2dfort</c>; <c>gg_debug</c> diverged
/// <b>zero</b> times, at every separation tried and against both a scripted and
/// a rollout opponent, even with the opponent pressing keys on 34 of 37
/// decisions. The opponent's damage is very nearly a constant across candidates,
/// so it cancels in the argmax unless the geometry gives a candidate somewhere to
/// be that the opponent's fire does not reach — which flat one-room
/// <c>gg_debug</c> does not. <b>An experiment that reports "the opponent model
/// changes nothing" from <c>gg_debug</c> has measured the map.</b>
/// </para>
/// </remarks>
public sealed class RolloutOpponentSelectionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// A rollout bot against a scripted one, banded so they are in range.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The opponent has to MOVE for the two models to differ at all</b>, so
    /// blue is <see cref="EnvDriver.Scripted"/> rather than
    /// <see cref="EnvDriver.Idle"/>: the two models make the same prediction
    /// about a genuinely idle opponent, and a test built on one would pass with
    /// the field ignored.
    /// <para>
    /// ⚠️ And <c>ctf_2dfort</c> rather than <c>gg_debug</c>, for the reason in
    /// this class's remarks. That choice is a measurement, not a preference.
    /// </para>
    /// </remarks>
    private static DuelSpec Spec(RolloutOpponent model) => new()
    {
        Map = "ctf_2dfort",
        RedClass = PlayerClass.Scout,
        BlueClass = PlayerClass.Soldier,
        RedX = DuelSpec.Sampled,
        RedY = DuelSpec.Sampled,
        BlueX = DuelSpec.Sampled,
        BlueY = DuelSpec.Sampled,
        DecisionCap = 50,
        ActionRepeat = 4,
        RedDriver = EnvDriver.Rollout,
        BlueDriver = EnvDriver.Scripted,
        BlueTier = 5,
        RedRolloutOpponent = model,
        MinSeparation = 150,
        MaxSeparation = 350,
    };

    private static List<(byte H, byte J, byte D, byte F, byte A)> Actions(RolloutOpponent model, uint seed)
    {
        var env = new DuelEnv(ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);
        var spec = Spec(model);

        env.Reset(spec, seed);

        var taken = new List<(byte, byte, byte, byte, byte)>();

        for (var i = 0; i < spec.DecisionCap; i++)
        {
            var (red, _) = env.Step(EnvAction.Idle, EnvAction.Idle);

            // The action the bot actually took, which protocol v2 put in the
            // reward block precisely so this is observable from outside.
            taken.Add((
                red.Action.Horizontal,
                red.Action.Jump,
                red.Action.Down,
                red.Action.Fire,
                red.Action.AimDelta));

            if (red.Done)
                break;
        }

        return taken;
    }

    /// <summary>
    /// ⚠️ <b>Asking for a different opponent model produces a different
    /// search</b>, and asking for the same one twice reproduces exactly.
    /// </summary>
    /// <remarks>
    /// Both halves are the gate. Without the second, a search that was merely
    /// non-deterministic would "pass" the first; without the first, dropping the
    /// field on the floor would pass.
    /// <para>
    /// ⚠️ <b>Behaviour rather than a getter.</b> Adding an accessor purely so a
    /// test could read back what <c>RolloutSearch</c> was handed would gate the
    /// assignment rather than the effect — which is the exact shape of test that
    /// passed while <c>--nav</c> was being dropped one layer up. See
    /// <see cref="EnvOptionsTests"/>.
    /// </para>
    /// <para>
    /// ⚠️ <b>Measured 15 of 50 decisions differ at seed 1.</b> Pinned as
    /// "at least one" rather than 15: what is being asserted is that the field
    /// arrives, and the exact count is a property of the fight.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOpponentModelReachesTheSearchAndChangesIt()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var hold = Actions(RolloutOpponent.HoldLastInput, seed: 1);
        var idle = Actions(RolloutOpponent.Idle, seed: 1);

        var differences = 0;
        for (var i = 0; i < Math.Min(hold.Count, idle.Count); i++)
        {
            if (hold[i] != idle[i])
                differences++;
        }

        output.WriteLine($"hold {hold.Count} decisions, idle {idle.Count}, {differences} differ");

        Assert.True(
            differences > 0 || hold.Count != idle.Count,
            "the two opponent models produced identical searches; the field is not reaching RolloutSearch");

        // The control: the same model twice is the same search, so the difference
        // above is the model and not noise. RolloutSearch draws from nothing and
        // the world is seeded, so this is exact.
        Assert.Equal(hold, Actions(RolloutOpponent.HoldLastInput, seed: 1));
    }

    /// <summary>
    /// ⚠️ <b><c>new RolloutSearch()</c> searches against the model its own
    /// documentation calls the default.</b>
    /// </summary>
    /// <remarks>
    /// It did not. <see cref="RolloutOpponent"/> declared <c>Idle</c> first, so
    /// it was zero, so <c>default(RolloutOptions)</c> carried it — and
    /// <see cref="RolloutOptions.WithDefaults"/> cannot repair an enum, because
    /// "unset" and "explicitly Idle" are the same value. Every caller that took
    /// the default got the opposite of it, this repo's own 11-0-1 smoke figure
    /// included.
    /// <para>
    /// ⚠️ <b>Asserted on the zero value rather than on behaviour</b>, and that is
    /// deliberate: the defect was in the numbering, the fix is the declaration
    /// order, and behaviour is exactly what did NOT change when it was fixed —
    /// every pinned number in <c>RolloutSearchTests</c> survived the reorder,
    /// which is more evidence about <c>gg_debug</c>'s blindness to the model than
    /// about the fix.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDefaultOpponentModelIsTheDocumentedOne()
    {
        Assert.Equal(RolloutOpponent.HoldLastInput, default(RolloutOptions).Opponent);
        Assert.Equal(RolloutOpponent.HoldLastInput, new RolloutOptions().WithDefaults().Opponent);
        Assert.Equal(RolloutOpponent.HoldLastInput, default(DuelSpec).RedRolloutOpponent);
        Assert.Equal(RolloutOpponent.HoldLastInput, default(DuelSpec).BlueRolloutOpponent);

        // ⚠️ And it is zero on the wire too, so a v3 client that leaves the field
        // alone gets the default rather than the floor.
        Assert.Equal(0, (int)RolloutOpponent.HoldLastInput);
    }

    /// <summary>An opponent model outside the enum is refused rather than defaulted.</summary>
    /// <remarks>
    /// ⚠️ The same rule the skill tier follows: silently giving a bad value the
    /// default would make an experiment report a matchup that never happened.
    /// </remarks>
    [Fact]
    public void AnUnknownOpponentModelIsRefused()
    {
        var spec = Spec(RolloutOpponent.Idle) with { BlueRolloutOpponent = (RolloutOpponent)77 };

        Assert.Throws<ArgumentOutOfRangeException>(spec.Validate);
    }
}
