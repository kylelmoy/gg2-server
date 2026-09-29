using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// A bot's route respects the gates that are solid to the body walking it.
/// </summary>
/// <remarks>
/// <see cref="NavGraph.GatePassable"/> is one half of a rule whose other half is
/// <see cref="SolidRect.IsSolidTo"/>. The graph stamps every edge that crosses a
/// gate; the simulation decides whether that gate is solid right now. They agree
/// only if the <see cref="NavQuery"/> a bot plans with carries the same live
/// facts the collision filter does, so <b>this file is about the query going
/// stale, not about the graph</b>.
/// <para>
/// Full write-up in <c>docs/TRAPS-BOTS.md</c>, "a route is planned against a
/// gate the body does not agree about".
/// </para>
/// </remarks>
public sealed class BotGateTests(ITestOutputHelper output)
{
    /// <summary>
    /// The setup gates are shut for the whole setup phase, and a bot knows it.
    /// </summary>
    /// <remarks>
    /// <b>ctf_avanti is the map because it is the corpus's Invasion map</b>: 1,800
    /// setup ticks, eight <c>SetupGate</c> entities and 76 <c>NavGate.Setup</c>
    /// edges, against cp_dirtbowl's 900 ticks and five. Every other shipped map
    /// has none at all, which is why nothing else moves when this is fixed.
    /// <para>
    /// ⚠️ <b>Both directions, and the second is what stops the cheap fix.</b>
    /// Reporting the gates shut for ever also empties the counter, and would
    /// strand every bot behind a gate that has opened — so the flag is asserted
    /// true during setup AND false after it.
    /// </para>
    /// <para>
    /// The behavioural half counts bot-ticks spent pressing a direction, on the
    /// ground, going nowhere: the player-visible symptom of a route through
    /// something solid. <b>540 on the unfixed code and 5 on the fixed one</b>, so
    /// the threshold is nowhere near either. It is a second gate rather than a
    /// restatement of the first: a query that reports the gates shut and a
    /// follower that walks into them anyway would pass the assertion above.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotDoesNotPlanThroughASetupGateThatIsStillShut()
    {
        var maps = MapDirectory.Resolve();
        var nav = MapDirectory.ResolveNav();

        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_avanti", Seed = 1, MaxTicks = 2400,
        };

        var match = HeadlessMatch.Load(options, maps, nav);

        Assert.True(
            match.State.SetupTimer > 0,
            "ctf_avanti no longer opens with a setup timer, so this measures nothing");

        var lastX = new double[WorldState.MaxSlots];
        var polledWith = new bool[WorldState.MaxSlots];
        var aliveAtPoll = new bool[WorldState.MaxSlots];
        var shovingInSetup = 0;
        var checkedDuringSetup = 0;
        var checkedAfterSetup = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            // ⚠️ Snapshotted BEFORE the step, because the query is built inside
            // Poll and the tick rewrites the mirrored flag afterwards
            // (MatchSimulation, "the tick's one writer of the two mirrored
            // globals"). Comparing against the post-tick value is the same
            // one-tick skew as "everything derived from a bot's team is derived
            // one tick too early", and it fails on tick 0 whatever the code does.
            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                aliveAtPoll[slot] = match.State.IsAlive(slot);
                polledWith[slot] = match.State.Characters[slot].SetupGatesClosed;
            }

            var setupShut = match.State.SetupTimer > 0;

            match.Step();

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (match.SourceOf(slot) is not ScriptedBot bot
                    || !aliveAtPoll[slot] || !match.State.IsAlive(slot))
                    continue;

                ref readonly var character = ref match.State.Characters[slot];

                // The query the bot planned this tick's route with has to carry
                // the same fact the collision filter that tick's movement used
                // was built from.
                Assert.Equal(polledWith[slot], bot.Query.SetupGatesClosed);

                if (setupShut)
                    checkedDuringSetup++;
                else
                    checkedAfterSetup++;

                var pressing = (character.KeyState & (PlayerKeys.Left | PlayerKeys.Right)) != 0;
                var stuckX = Math.Abs(character.X - lastX[slot]) < 0.01;
                lastX[slot] = character.X;

                if (setupShut && pressing && character.OnGround && stuckX)
                    shovingInSetup++;
            }
        }

        output.WriteLine(
            $"bot-ticks checked: {checkedDuringSetup} in setup, {checkedAfterSetup} after; "
            + $"shoving a wall during setup: {shovingInSetup}");

        // Both sides of the timer are exercised, or the assertion above is
        // vacuous in one direction.
        Assert.True(checkedDuringSetup > 1000, $"only {checkedDuringSetup} bot-ticks inside setup");
        Assert.True(checkedAfterSetup > 1000, $"only {checkedAfterSetup} bot-ticks after setup");

        Assert.True(
            shovingInSetup < 100,
            $"{shovingInSetup} bot-ticks spent pressing into a shut setup gate (unfixed: 540)");
    }

    /// <summary>
    /// A bot carrying the intel knows its own team gate has shut behind it.
    /// </summary>
    /// <remarks>
    /// <c>SolidKind.TeamGate</c> is solid to a character of its OWN team once that
    /// character is carrying, which is the <c>&amp;&amp; !hasIntel</c> in
    /// <see cref="NavGate.TeamRed"/> and <see cref="NavGate.TeamBlue"/>. No
    /// shipped graph carries an <c>IntelRed</c>/<c>IntelBlue</c> edge, so the team
    /// gates are the whole of what <see cref="NavQuery.HasIntel"/> decides here —
    /// but ctf_truefort carries 117 and 118 of them, so it decides plenty.
    /// <para>
    /// This asserts the query rather than a walk because the grab is the
    /// expensive part and <see cref="BotObjectiveTests"/> already pins it: the
    /// defect is one stale field, and the walk home through a gate it may not use
    /// is downstream of it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACarrierPlansAsSomebodyItsOwnTeamGateWillNotLetThrough()
    {
        var maps = MapDirectory.Resolve();
        var nav = MapDirectory.ResolveNav();

        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort", Seed = 1, MaxTicks = 9000,
        };

        var match = HeadlessMatch.Load(options, maps, nav);

        var carriedTicks = 0;
        var emptyHandedTicks = 0;
        var polledWith = new bool[WorldState.MaxSlots];
        var aliveAtPoll = new bool[WorldState.MaxSlots];

        while (match.Outcome == MatchOutcome.Running)
        {
            // Before the step, for the reason the sibling test states. ⚠️ Alive
            // before it too: Poll returns early for a dead bot without touching
            // the query, so on the tick a bot RESPAWNS the query is still the one
            // it last planned with — which, for a carrier that was shot, says it
            // is carrying. Asserting there measures the early return, not the
            // refresh.
            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                aliveAtPoll[slot] = match.State.IsAlive(slot);
                polledWith[slot] = match.State.Characters[slot].CarryingIntel;
            }

            match.Step();

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (match.SourceOf(slot) is not ScriptedBot bot || !aliveAtPoll[slot])
                    continue;

                Assert.Equal(polledWith[slot], bot.Query.HasIntel);

                if (polledWith[slot])
                    carriedTicks++;
                else
                    emptyHandedTicks++;
            }
        }

        output.WriteLine($"bot-ticks carrying {carriedTicks}, empty-handed {emptyHandedTicks}");

        // ⚠️ Without this the test passes on the unfixed code, because the
        // unfixed query is false and so is every sample. Somebody has to actually
        // pick the briefcase up for the assertion above to mean anything.
        Assert.True(carriedTicks > 0, "no bot ever carried the intel, so the assertion never fired");
    }
}
