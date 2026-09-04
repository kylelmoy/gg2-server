using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The medic is trained on needle combat only: a beaming medic is never handed
/// to a policy, and the policy is never shown a charge it cannot spend.
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
    // The observation column
    // ------------------------------------------------------------------

    /// <summary>
    /// A medic's übercharge does not reach the observation at all.
    /// </summary>
    /// <remarks>
    /// The shared special column was <c>Medic =&gt; UberCharge</c>, and it has been
    /// identically 0.0 in every training row of every run because no trained class
    /// hit a non-<c>_</c> arm. Adding Medic does not populate it either — uber
    /// cannot accrue in a 1v1 duel, since <c>Healing</c>'s only accrual path needs
    /// a patient — so a deployed medic carrying banked charge would have been the
    /// only time that weight ever saw a non-zero input.
    /// <para>
    /// <b>Compared as whole observations rather than by column index</b>, so
    /// this keeps working when the layout moves and cannot quietly start asserting
    /// about the wrong float.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMedicsUberChargeIsInvisibleToThePolicy()
    {
        var empty = ObservationOfMedic(uberCharge: 0);
        var full = ObservationOfMedic(uberCharge: WeaponState.MaxUberCharge);

        Assert.Equal(empty, full);
    }

    /// <summary>
    /// The control: the column is still WIRED, and the test can see it move.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="AMedicsUberChargeIsInvisibleToThePolicy"/> would
    /// pass just as happily against an observation writer that had stopped writing
    /// the column at all, or against a comparison that could not detect a
    /// one-float difference anywhere in a hundred-odd values.
    /// </remarks>
    [Fact]
    public void AnEngineersNutsAndBoltsStillReachTheObservation()
    {
        var empty = ObservationOfEngineer(nutsAndBolts: 0);
        var full = ObservationOfEngineer(nutsAndBolts: CharacterState.MaxNutsNBolts);

        Assert.NotEqual(empty, full);
    }

    /// <summary>
    /// KNOWN GAP, PINNED: a sniper's rifle charge is invisible to the policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WeaponState.Charge</c> is real state and <c>Weapons.RifleBeginStep</c> sets rifle
    /// damage to <c>base + sqrt(Charge / RifleChargeTime) * (max - base)</c> over
    /// 105 ticks — so charge <em>is</em> the sniper's damage. Sniper falls to the
    /// shared column's <c>_</c> arm and Sniper is in
    /// <c>PolicyController.TrainedClasses</c>, so <b>a trained sniper policy fires
    /// without being able to see what its shot will do, and cannot learn to hold
    /// it.</b>
    /// </para>
    /// <para>
    /// <b>This test asserts the WRONG behaviour on purpose.</b> Closing the gap
    /// is an <c>ObservationLayoutVersion</c> bump and a full retrain, which is
    /// <c>gg2-rl</c>'s cost to price and not a free improvement to hand them. The
    /// pin exists so that closing it FAILS HERE and becomes a deliberate change to
    /// a recorded fact — the alternative is a gap that lives in one evening's chat
    /// message, which is exactly how the sandvich went missing for eight phases.
    /// </para>
    /// <para>
    /// <b>When it is closed:</b> delete this test, bump
    /// <c>ObservationLayoutVersion</c>, and say in the commit that every existing
    /// checkpoint is dated by it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASnipersChargeIsInvisibleToThePolicy()
    {
        var empty = ObservationOfSniper(charge: 0);
        var full = ObservationOfSniper(charge: Weapons.RifleChargeTime);

        Assert.Equal(empty, full);
    }

    private static float[] ObservationOfSniper(double charge)
    {
        var state = MedicWithEnemy(out var slot, out _);
        state.Characters[slot].Class = PlayerClass.Sniper;
        state.Weapons[slot] = WeaponState.Spawn(
            PlayerClass.Sniper, state.Characters[slot].X, state.Characters[slot].Y);
        state.Weapons[slot].Charge = charge;

        return Observation(in state, slot);
    }

    private static float[] ObservationOfMedic(double uberCharge)
    {
        var state = MedicWithEnemy(out var medic, out _);
        state.Weapons[medic].UberCharge = uberCharge;

        return Observation(in state, medic);
    }

    private static float[] ObservationOfEngineer(double nutsAndBolts)
    {
        var state = MedicWithEnemy(out var slot, out _);
        state.Characters[slot].Class = PlayerClass.Engineer;
        state.Characters[slot].NutsNBolts = nutsAndBolts;

        return Observation(in state, slot);
    }

    private static float[] Observation(in WorldState state, int slot)
    {
        var observation = new float[ObservationWriter.Length];
        ObservationWriter.Write(new WorldView(in state, Collision), slot, observation);
        return observation;
    }

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
