using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The two hooks a gate needs to hand control to a <see cref="RolloutSearch"/>.
/// </summary>
/// <remarks>
/// Added for `gg2-rl`'s arm 3 — a search-driven micro-controller behind the same
/// 150px handover <c>PolicyBot</c> already implements — and both exist because a
/// caller cannot do the job without them.
/// <para>
/// ⚠️ <b>The cadence had to have ONE owner and it had to be the search.</b> The
/// alternative — <c>DecisionTicks: 1</c> with the gate holding the rhythm — is not
/// available: <c>ActionDecoder</c> emits the jump bit only on the first tick of a
/// decision, so the held ticks are a <em>different decode of the same action</em>
/// rather than a repeat of one <c>PlayerInput</c>. A gate polling only on its own
/// boundaries either starves the search of the held ticks or re-implements the
/// edge logic somewhere else, which is CLAUDE.md's "GG2 jumps on the PRESSED
/// edge" scar a second time.
/// </para>
/// </remarks>
public sealed class SearchHandoverTests
{
    private static ScenarioWorld World(out int red)
    {
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, null);

        red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        return world;
    }

    /// <summary>
    /// The boundary a gate must swap on is the search's, and it is visible.
    /// </summary>
    [Fact]
    public void TheDecisionBoundaryIsVisibleAndFollowsTheCadence()
    {
        var world = World(out var red);
        var view = new WorldView(in world.State, world.Collision);
        var search = new RolloutSearch(new RolloutOptions(HorizonTicks: 8, DecisionTicks: 4));

        // Nothing polled yet: the next Poll starts a decision.
        Assert.True(search.AtDecisionBoundary(red));

        search.Poll(in view, red, 0);

        // Mid-decision for the next three ticks, then a boundary again.
        Assert.False(search.AtDecisionBoundary(red));
        search.Poll(in view, red, 1);
        Assert.False(search.AtDecisionBoundary(red));
        search.Poll(in view, red, 2);
        Assert.False(search.AtDecisionBoundary(red));
        search.Poll(in view, red, 3);

        Assert.True(search.AtDecisionBoundary(red));
    }

    /// <summary>
    /// ⚠️ A different slot is always a boundary, because <c>Poll</c> makes it one.
    /// </summary>
    /// <remarks>
    /// The reason the hook takes a slot rather than being a property: a
    /// parameterless version would answer for the character the search last drove
    /// and be wrong exactly once, on the tick a bot is re-assigned, silently.
    /// </remarks>
    [Fact]
    public void AnotherSlotIsAlwaysABoundary()
    {
        var world = World(out var red);
        var view = new WorldView(in world.State, world.Collision);
        var search = new RolloutSearch(new RolloutOptions(HorizonTicks: 8, DecisionTicks: 4));

        search.Poll(in view, red, 0);

        Assert.False(search.AtDecisionBoundary(red));
        Assert.True(search.AtDecisionBoundary(red + 1));
    }

    /// <summary>
    /// ⛔ Without a resync, the first decision after a handover turns from a
    /// bearing nobody is pointing at.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This test demonstrates the BUG, not the method.</b> It moves the
    /// character's aim behind the search's back — which is exactly what the
    /// scripted layer does while it holds control, writing the bearing directly —
    /// and shows that the search's next decision is decoded from the stale value
    /// until <see cref="RolloutSearch.ResyncAim"/> is called.
    /// <para>
    /// The failure it prevents is a wrong <em>verdict</em> rather than a wrong
    /// number: a search aiming from a stale bearing does not throw, it fights
    /// slightly worse in exactly the engagements a handover hands it, and reports
    /// as "the search hybrid underperforms".
    /// </para>
    /// </remarks>
    [Fact]
    public void WithoutAResyncTheSearchAimsFromAStaleBearing()
    {
        var world = World(out var red);
        var search = new RolloutSearch(new RolloutOptions(HorizonTicks: 8, DecisionTicks: 4));

        // The search takes one decision, which seeds its decoder from the wire.
        // What Poll RETURNS is the aim it wants, which is the observable thing -
        // no internal state is read anywhere in this test.
        var first = new WorldView(in world.State, world.Collision);
        var seeded = search.Poll(in first, red, 0);

        // Another controller now turns the character right round, the way the
        // scripted layer does when it writes a bearing directly.
        world.State.Characters[red].NetAimDirection =
            (ushort)((seeded.NetAimDirection + 32768) % 65536);

        var after = new WorldView(in world.State, world.Collision);

        // Hand control back WITHOUT resyncing. Finish the held decision, then take
        // a fresh one: the decoder is still carrying the pre-turn bearing, so what
        // it asks for is nowhere near where the character is actually pointing.
        PlayerInput reclaimed = default;

        for (var tick = 1; tick <= 4; tick++)
            reclaimed = search.Poll(in after, red, tick);

        var stale = Separation(Degrees(reclaimed.NetAimDirection), Wire(world, red));

        Assert.True(
            stale > 90.0,
            $"expected the search to be aiming from a stale bearing, but it is {stale:0.0} "
            + "degrees from where the character actually points — if this is small the staleness "
            + "this hook exists to fix does not happen and the hook is unnecessary");

        // Now resync, then take a fresh decision. The aim starts from the truth,
        // so a decision that asks for no turn lands on the character's own bearing
        // and any turn it does ask for is bounded by one decision's cap.
        search.ResyncAim(in after, red);

        var resynced = search.Poll(in after, red, 5);

        Assert.True(
            Separation(Degrees(resynced.NetAimDirection), Wire(world, red))
                <= ActionDecoder.MaxTurnDegreesPerDecision + 1e-6,
            "after a resync the search's aim must start from the character's actual bearing, "
            + "so it can be at most one decision's turn away from it");
    }

    private static double Wire(ScenarioWorld world, int slot) =>
        Degrees(world.State.Characters[slot].NetAimDirection);

    private static double Degrees(ushort net) => net * 360.0 / 65536.0;

    /// <summary>Absolute shortest angle between two bearings, in degrees.</summary>
    private static double Separation(double a, double b) =>
        Math.Abs(((a - b + 540.0) % 360.0) - 180.0);
}
