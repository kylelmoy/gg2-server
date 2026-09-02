using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The three message types with a call site on <em>both</em> sides of the
/// reference's flush, and the flags that tell them apart.
/// </summary>
/// <remarks>
/// ⛔ <b>`GameServerEndStep` is the FIRST End Step of the frame</b>
/// (docs/TRAPS-SIMULATION.md, "The GameServer is the OLDEST instance"), so
/// anything appended at or after another object's End Step misses that frame's
/// flush and ships on the next tick. Most message types sit wholly on one side
/// of that line. <b>Three do not</b>, and a broadcast keyed on the message
/// <em>type</em> rather than on the stage that produced it puts one of the two
/// on the wrong tick whichever side it picks:
/// <list type="bullet">
/// <item><c>PLAYER_DEATH</c> — <c>processClientCommands.gml:88</c> (pre-flush)
/// against <c>Character.events/End Step.xml:34-47</c> (post-flush).</item>
/// <item><c>RETURN_INTEL</c> — <c>Intelligence.events/Alarm 0.xml</c>
/// (pre-flush) against <c>Character.events/Destroy.xml:20-32</c>, reached from
/// <c>doEventPlayerDeath</c> (post-flush).</item>
/// <item><c>DESTROY_SENTRY</c> — <c>Sentry.events/Begin Step.xml:1-3</c>
/// (pre-flush) against <c>Sentry.events/End Step.xml</c>'s <c>hp &lt;= 0</c>
/// (post-flush).</item>
/// </list>
/// <para>
/// ⛔ <b>Both of the latter two were wrong, and the suite did not move when they
/// were fixed.</b> That is the alarm CLAUDE.md describes — a real behavioural
/// change that turns nothing red says the paths are ungated. This file is that
/// gate. It asserts at the point the distinction is <em>made</em>, because by
/// the time a message reaches the wire the two are indistinguishable.
/// </para>
/// <para>
/// ⚠️ <b>What this does NOT do</b>: it cannot check that the stage
/// classification itself is right. That came from reading the reference and from
/// a live measurement, and it lives in docs/PARITY.md, "Which flush a message
/// rides". This file only gates that the port routes each stage the way that
/// table says.
/// </para>
/// </remarks>
public sealed class WireStageTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static CollisionWorld? _shared;

    private static CollisionWorld World => _shared ??= new CollisionWorld(Level());

    /// <summary>
    /// A world whose flags have a base. ⚠️ Without one <see cref="Intel.Return"/>
    /// silently does nothing — <c>if (!flag.HasBase) return</c> — so a test on a
    /// bare <c>default(WorldState)</c> asserts against an event that was never
    /// raised and passes for the wrong reason.
    /// </summary>
    private static WorldState WithFlagBases()
    {
        var state = default(WorldState);

        state.RedIntel.HasBase = true;
        state.BlueIntel.HasBase = true;

        return state;
    }

    /// <summary>An engineer with a built sentry, both on open ground.</summary>
    private static void PutEngineerWithSentry(ref WorldState state, int slot, double x, double y)
    {
        var character = CharacterState.Fresh(PlayerClass.Engineer, Team.Red);
        character.X = x;
        character.Y = y;
        character.Facing = 1;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Engineer, x, y);
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = Team.Red;
        state.Players[slot].Class = PlayerClass.Engineer;

        state.Sentries[slot] = SentryState.Build(Team.Red, x, y, 1);
        state.Sentries[slot].Built = true;
        state.Sentries[slot].Hp = SentryState.MaxHp;
        state.HasSentry[slot] = true;
    }

    /// <summary>
    /// ⛔ A sentry that dies of damage is an END STEP destruction, so its
    /// message ships on the following tick.
    /// </summary>
    [Fact]
    public void ASentryKilledByDamageIsFlaggedAsAnEndStepDestruction()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        PutEngineerWithSentry(ref state, 0, 2280, 200);

        // Straight to zero, which only the hp<=0 arm of the End Step can act on.
        state.Sentries[0].Hp = 0;

        // ⚠️ The producer directly, not a whole tick. The stage classification is
        // made here, and driving MatchSimulation.Tick only adds setup that can
        // fail for reasons this test is not about.
        Sentries.EndStep(World, ref state, 0, ref events);

        Assert.True(events.SentryDestroyed[0], "the sentry did not die at all");
        Assert.True(
            events.SentryDestroyedInEndStep[0],
            "an hp<=0 destruction is Sentry.events/End Step.xml and must ship on the next tick");
    }

    /// <summary>
    /// ⛔ A sentry destroyed because its owner stopped being an engineer of that
    /// team is a BEGIN STEP destruction, and rides its own tick's flush.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is the arm that today's flush fix silently made a tick LATE, and
    /// nothing caught it — <c>Sentry.events/Begin Step.xml:1-3</c> was folded in
    /// with the End Step one because they share a message type.
    /// </remarks>
    [Fact]
    public void ASentryWhoseOwnerLeftTheClassIsFlaggedAsABeginStepDestruction()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        PutEngineerWithSentry(ref state, 0, 2280, 200);

        // `ownerPlayer.class != CLASS_ENGINEER`, the first disjunct of the guard.
        state.Players[0].Class = PlayerClass.Soldier;
        state.Characters[0].Class = PlayerClass.Soldier;

        Sentries.BeginStep(World, ref state, 0, ref events);

        Assert.True(events.SentryDestroyed[0], "the sentry survived its owner leaving the class");
        Assert.False(
            events.SentryDestroyedInEndStep[0],
            "a Begin Step destruction makes its own tick's flush and must not be marked late");
    }

    /// <summary>
    /// ⛔ The recovery timer's return is an ALARM-stage append and rides its own
    /// tick.
    /// </summary>
    /// <remarks>
    /// <c>Intelligence.events/Alarm 0.xml</c>. The alarm stage sits between Begin
    /// Step and Step, both comfortably above the GameServer's End Step.
    /// </remarks>
    [Fact]
    public void TheRecoveryTimersReturnIsNotFlaggedAsEndStep()
    {
        var state = WithFlagBases();
        var events = default(TickEvents);

        // ⚠️ Through ReturnStep, the real alarm-stage caller — NOT Intel.Return
        // directly, which would only assert that passing the default sets the
        // default. The gate has to be on the CALL SITE's choice.
        state.RedIntel.OnField = true;
        state.RedIntel.RecoverTimer = 1;

        Intel.ReturnStep(ref state, ref events);

        Assert.True(events.ReturnedRedIntel, "the recovery timer did not bring the flag home");
        Assert.False(
            events.ReturnedRedIntelInEndStep,
            "the alarm-stage return makes its own tick's flush");
    }

    /// <summary>
    /// ⛔ The return that comes out of a dying carrier is an END STEP append, so
    /// it ships on the following tick — <b>behind the PLAYER_DEATH that caused
    /// it</b>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Character.events/Destroy.xml</c> is reached from inside
    /// <c>doEventPlayerDeath</c>, which the Character's End Step calls. Before
    /// this was split out, the port broadcast it above the flush — a tick early,
    /// and after 2026-09-02 put PLAYER_DEATH below the flush, <em>ahead</em> of
    /// the death that caused it. The reference sends the death first.
    /// </remarks>
    [Fact]
    public void ADyingCarriersReturnIsFlaggedAsEndStep()
    {
        var state = WithFlagBases();
        var events = default(TickEvents);

        // ⚠️ Through Deaths.Kill, the real End Step caller. Calling Intel.Return
        // with inEndStep: true would assert nothing except that the parameter is
        // wired to the field — it would stay green if Deaths.Kill stopped passing
        // it, which is the regression that matters.
        var carrier = CharacterState.Fresh(PlayerClass.Scout, Team.Blue);
        carrier.X = 2280;
        carrier.Y = 200;
        carrier.CarryingIntel = true;
        carrier.LastDamageSource = DamageSource.Pitfall;
        carrier.Hp = 0;

        state.Characters[0] = carrier;
        state.Weapons[0] = WeaponState.Spawn(PlayerClass.Scout, carrier.X, carrier.Y);
        state.Alive[0] = true;
        state.AddPlayer(0);
        state.Players[0].Team = Team.Blue;
        state.Players[0].Class = PlayerClass.Scout;

        // A blue carrier holds the RED flag, which is what comes home.
        Deaths.Kill(ref state, 0, ref events, World);

        Assert.True(events.Died[0], "the carrier did not die");
        Assert.True(events.ReturnedRedIntel, "the pit death did not return the flag");
        Assert.True(
            events.ReturnedRedIntelInEndStep,
            "a return raised from Character.events/Destroy.xml must ship on the next tick");
    }

    /// <summary>
    /// ⚠️ The two flags are per team and independent, because one tick can carry
    /// a recovery-timer return for one flag and a death-driven return for the
    /// other.
    /// </summary>
    /// <remarks>
    /// Not hypothetical bookkeeping: a single team field would force the two into
    /// one bucket and put whichever lost on the wrong side of the flush. This is
    /// the same reason <c>ReturnedRedIntel</c> and <c>ReturnedBlueIntel</c> are
    /// two flags rather than one, which <c>TickEvents</c> already says.
    /// </remarks>
    [Fact]
    public void TheTwoTeamsReturnStagesAreIndependent()
    {
        var state = WithFlagBases();
        var events = default(TickEvents);

        Intel.Return(ref state, Team.Red, ref events);
        Intel.Return(ref state, Team.Blue, ref events, inEndStep: true);

        Assert.False(events.ReturnedRedIntelInEndStep);
        Assert.True(events.ReturnedBlueIntelInEndStep);
    }

    /// <summary>
    /// ⛔ Clearing a tick's events clears the stage flags with them.
    /// </summary>
    /// <remarks>
    /// ⚠️ A stage flag that survived into the next tick would re-route a message
    /// that had nothing to do with it — and would do so only on the tick after a
    /// death, which is the hardest kind of bug to see. <c>ClearOutputs</c> is
    /// hand-written per field, so this is a real hazard rather than a formality.
    /// </remarks>
    [Fact]
    public void ClearingATicksEventsClearsTheStageFlags()
    {
        var state = WithFlagBases();
        var events = default(TickEvents);

        Intel.Return(ref state, Team.Red, ref events, inEndStep: true);
        Intel.Return(ref state, Team.Blue, ref events, inEndStep: true);
        events.SentryDestroyedInEndStep[0] = true;

        events.ClearOutputs();

        Assert.False(events.ReturnedRedIntelInEndStep);
        Assert.False(events.ReturnedBlueIntelInEndStep);
        Assert.False(events.SentryDestroyedInEndStep[0]);
    }
}
