using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// A beaming medic is never handed to a policy that does not heal itself. A
/// controller that heals itself (<see cref="IBeamController"/>, gg2-sim's medic
/// layout) is the exception, made in <see cref="HybridBot"/> rather than here;
/// <c>HybridBotSeamTests</c> covers it.
/// </summary>
/// <remarks>
/// <para>
/// Requested by <c>gg2-rl</c>, who are adding Medic to the trained class set
/// with uber deliberately out of scope. <b>The two halves compose,
/// and that is the point rather than a coincidence:</b> the uber press requires a
/// beam target (<c>BotClassKeys</c>' medic case wants <c>UberReady</c> AND an ally
/// on the beam AND an enemy within <c>UberRange</c>), and a medic on the beam is
/// never handed over — so every tick on which the uber could be pressed is a tick
/// the scripted layer holds the wheel. Without the handover term the scope
/// decision would not actually hold, because a policy driving a beaming medic
/// would be pressing the keys instead.
/// </para>
/// <para>
/// Both halves are behaviour a measurement cannot see: a policy driving a medic
/// away from its patient still plays, and an untrained weight reading an
/// off-distribution column still produces an action. They fail quietly or not at
/// all, which is why they are pinned here.
/// </para>
/// </remarks>
public sealed class MedicPolicyScopeTests
{
    private static readonly CollisionWorld Collision =
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    // ------------------------------------------------------------------
    // The handover term
    // ------------------------------------------------------------------

    /// <summary>
    /// A medic on the beam stays scripted; the same medic with no patient does not.
    /// </summary>
    /// <remarks>
    /// <b>The control is the second half.</b> Asserting only that a beaming
    /// medic is refused would pass just as well if medics were never handed over
    /// at all — which would delete the arm <c>gg2-rl</c> is about to train.
    /// </remarks>
    [Fact]
    public void ABeamingMedicIsNotHandedOverButALoneOneIs()
    {
        var state = MedicWithEnemy(out var medic, out var ally);
        var world = new WorldView(in state, Collision);

        Assert.False(Engage(in world, medic, healTarget: ally));

        // Alone with the enemy: exactly the case the policy is being trained for.
        Assert.True(Engage(in world, medic, healTarget: BotAlly.None));
    }

    /// <summary>
    /// A dead or departed patient is not a patient.
    /// </summary>
    /// <remarks>
    /// The term reuses <c>BotAlly.StillValid</c> rather than testing the slot
    /// against <c>None</c>, because a stale ally slot outlives the ally — and a
    /// medic frozen out of the policy for ever by a patient who died three minutes
    /// ago would be a silent, permanent regression to the scripted arm.
    /// </remarks>
    [Fact]
    public void AStaleHealTargetDoesNotFreezeTheMedicOutOfTheHandover()
    {
        var state = MedicWithEnemy(out var medic, out var ally);
        state.Alive[ally] = false;

        var world = new WorldView(in state, Collision);

        Assert.True(Engage(in world, medic, healTarget: ally));
    }

    /// <summary>
    /// The term is the MEDIC's, not everyone's.
    /// </summary>
    /// <remarks>
    /// <c>BotClassProfile.Heals</c> is medic-only, and a term that leaked to other
    /// classes would quietly switch every hybrid bot with a nearby ally back to
    /// scripted — which is most bots in a firefight, and would look like the
    /// controller simply not working.
    /// </remarks>
    [Fact]
    public void ASoldierWithAnAllyIsStillHandedOver()
    {
        var state = MedicWithEnemy(out var subject, out var ally);
        state.Characters[subject].Class = PlayerClass.Soldier;

        var world = new WorldView(in state, Collision);

        Assert.True(Engage(in world, subject, healTarget: ally));
    }

    private static bool Engage(in WorldView world, int slot, int healTarget) =>
        Handover.ShouldEngage(
            in world,
            slot,
            HybridBot.EngagementRange,
            HandoverRule.Distance,
            wantsToEngage: true,
            EngageOpponents.Anyone,
            healTarget);

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    /// <summary>A red medic, a red ally beside it, and a blue enemy in range.</summary>
    private static WorldState MedicWithEnemy(out int medic, out int ally)
    {
        var state = default(WorldState);

        medic = 0;
        ally = 1;

        Place(ref state, medic, PlayerClass.Medic, Team.Red, 2600);
        Place(ref state, ally, PlayerClass.Soldier, Team.Red, 2620);
        Place(ref state, 2, PlayerClass.Soldier, Team.Blue, 2660);

        return state;
    }

    private static void Place(ref WorldState state, int slot, PlayerClass cls, Team team, double x)
    {
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = cls;

        state.Characters[slot] = CharacterState.Fresh(cls, team);
        state.Characters[slot].X = x;
        state.Characters[slot].Y = 708;
        state.Weapons[slot] = WeaponState.Spawn(cls, x, 708);
        state.Alive[slot] = true;
    }
}
