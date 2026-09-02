using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="HeadlessMatch.Load"/> shares the decoded map and nav graph between
/// matches, and sharing them changes nothing.
/// </summary>
/// <remarks>
/// ⛔ <b>This is the gate on a shared-mutable-state bug that would be invisible
/// otherwise.</b> The map decode plus nav parse measured <b>18.0 ms</b> against
/// <b>283.5 ms</b> to play a 9,000-tick match — 6% of every match, paid again for
/// bytes that had not changed — so <c>Load</c> goes through
/// <see cref="ScenarioResources.Shared"/>. The saving is real (18.36 ms to
/// 0.08 ms on the second ask) and so is the risk: <b>anything mutable that leaks
/// into that cache makes one match's state visible to the next</b>, which shows up
/// as an irreproducible result long after the change that caused it.
/// <para>
/// ⚠️ <b>The division is the whole safety argument.</b> <see cref="LevelMap"/> and
/// <see cref="NavGraph"/> are deeply immutable and are shared;
/// <c>CollisionWorld</c> is NOT, because <c>SetCaptureZones</c> writes to it, and
/// <c>SpawnPoints</c> is not either. <c>HeadlessMatch.On</c> builds those two per
/// match. <c>ScenarioResources</c>' own remarks carry the argument in full; these
/// tests are what stop it becoming aspirational.
/// </para>
/// </remarks>
public sealed class SharedMapDataTests
{
    private const string Map = "ctf_truefort";

    private static HeadlessMatchOptions Options(uint seed) =>
        HeadlessMatchOptions.Default with
        {
            Map = Map,
            Bots = 8,
            Seed = seed,
            CapLimit = 1,
            TimeLimitMinutes = 1,
            KillLimit = 20,
            MaxTicks = 1800,
        };

    private static HeadlessMatch Load(uint seed) =>
        HeadlessMatch.Load(Options(seed), ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    /// <summary>Plays a match out and reports everything that could differ.</summary>
    private static (int Ticks, int Red, int Blue, MatchOutcome Outcome) Play(HeadlessMatch match)
    {
        int ticks = 0, red = 0, blue = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();
            ticks++;

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (!match.Events.Died[slot])
                    continue;

                if (match.State.Players[slot].Team == Team.Red)
                    red++;
                else
                    blue++;
            }
        }

        return (ticks, red, blue, match.Outcome);
    }

    /// <summary>
    /// ⛔ The same seed on a SHARED map plays exactly the same match.
    /// </summary>
    /// <remarks>
    /// <b>The second match runs on the first match's <c>LevelMap</c> and
    /// <c>NavGraph</c> instances</b>, so anything either match wrote into them
    /// would show here as a diverging tick count or death tally. ⚠️ <b>Deaths as
    /// well as ticks</b>: a match can reach the same tick ceiling by a completely
    /// different route, so the tick count alone is a weak witness.
    /// </remarks>
    [Fact]
    public void TheSameSeedPlaysTheSameMatchOnASharedMap()
    {
        var first = Play(Load(4242));
        var second = Play(Load(4242));

        Assert.Equal(first, second);

        // ⚠️ The control, and it is stated as a PROPERTY rather than a threshold:
        // both tallies are non-zero, so both arms of the comparison were exercised
        // and the match really happened. Two runs of a match that never got going
        // would agree trivially — on zero at the tick ceiling — and say nothing
        // about sharing.
        //
        // ⛔ A tuned number would be worse than this, not better. The first draft
        // asked for more than five deaths, which a 900-tick match did not reach;
        // raising the tick count until the number passed would be fitting the bar
        // to the data.
        Assert.True(first.Red > 0 && first.Blue > 0, $"deaths were {first.Red}/{first.Blue}");
    }

    /// <summary>
    /// A different seed really does play a different match.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The other control, and it is not redundant.</b> Without it, a
    /// <c>Play</c> that silently returned a constant would satisfy the equality
    /// above forever.
    /// </remarks>
    [Fact]
    public void ADifferentSeedPlaysADifferentMatch()
    {
        Assert.NotEqual(Play(Load(4242)), Play(Load(1717)));
    }

    /// <summary>
    /// ⛔ The mutable half is NOT shared: every match gets its own
    /// <c>CollisionWorld</c> and <c>SpawnPoints</c>.
    /// </summary>
    /// <remarks>
    /// <b><c>CollisionWorld.SetCaptureZones</c> writes to the object</b>, so a
    /// shared one would carry one match's capture zones into the next. This asserts
    /// the split directly rather than trusting that nobody widens the cache later —
    /// the failure it prevents is a control point that belongs to the previous
    /// match.
    /// </remarks>
    [Fact]
    public void TheMutableHalfIsNotSharedAndTheImmutableHalfIs()
    {
        var first = Load(4242);
        var second = Load(4242);

        // Immutable: shared, which is where the 18 ms goes.
        Assert.Same(first.Map, second.Map);
        Assert.Same(first.Nav, second.Nav);

        // Mutable: never shared.
        Assert.NotSame(first.Collision, second.Collision);
        Assert.NotSame(first.Spawns, second.Spawns);
    }

}
