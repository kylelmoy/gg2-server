using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Running out of hp: phase 5 stage 4.
/// </summary>
/// <remarks>
/// <c>PhysicsParityTests.EveryRecordedDeathHappensOnTheTickItWasRecordedFor</c>
/// proves the whole thing against a recording of the real server, which is the
/// claim that matters — but that fixture is two scouts shooting each other, so
/// it exercises exactly one path through the attribution. These pin the rest,
/// and they pin the ones the fixture <em>cannot</em> reach: there is no medic in
/// it, so the assist redirect is untested by it in both directions.
/// <para>
/// Transcribed from <c>Character.events/End Step.xml:34-47</c>,
/// <c>Character.events/Destroy.xml</c> and
/// <c>Scripts/Events/sendEventPlayerDeath.gml</c>.
/// </para>
/// </remarks>
public sealed class DeathTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Open ground, well clear of any wall the stair descent could find.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    private static CollisionWorld World() =>
        new(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static void PutScout(ref WorldState state, int slot, Team team, double hp = 100)
    {
        state.Characters[slot] = new CharacterState
        {
            Class = PlayerClass.Scout,
            Team = team,
            X = OpenX,
            Y = OpenY,
            Hp = hp,
            Facing = 1,
            LastFacing = 1,
            RunPower = ClassStats.For(PlayerClass.Scout).RunPower,
            JumpStrength = ClassStats.BaseJumpStrength,
            CloakAlpha = 1,
            CanGrabIntel = true,
            LastDamageDealer = CharacterState.Nobody,
            SecondToLastDamageDealer = CharacterState.Nobody,
            BurnedBy = CharacterState.Nobody,
            Healer = CharacterState.Nobody,
        };

        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, OpenX, OpenY);
        state.Alive[slot] = true;
    }

    /// <summary>One tick with nobody pressing anything.</summary>
    private static TickEvents Tick(CollisionWorld world, ref WorldState state)
    {
        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);
        return events;
    }

    // --- the check itself -----------------------------------------------------

    /// <summary>
    /// A character on zero hp dies, is reported, and is gone.
    /// </summary>
    /// <remarks>
    /// <c>hp &lt;= 0</c>, not <c>hp &lt; 0</c> — exactly zero is dead.
    /// </remarks>
    [Fact]
    public void ZeroHpIsDeath()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);

        var events = Tick(world, ref state);

        Assert.True(events.Died[1]);
        Assert.Equal(1, events.Deaths[1].Slot);
        Assert.False(state.Alive[1]);
    }

    /// <summary>A character with hp left lives, and reports nothing.</summary>
    [Fact]
    public void PositiveHpIsNotDeath()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 1);

        var events = Tick(world, ref state);

        Assert.False(events.Died[1]);
        Assert.True(state.Alive[1]);
    }

    /// <summary>
    /// The comparison is GML's, so a hair above zero is still dead.
    /// </summary>
    /// <remarks>
    /// <b>Reachable, not theoretical.</b> GML's real comparisons carry a 1e-13
    /// fuzz (<see cref="Gml.Equal"/>) and afterburn subtracts a fractional
    /// amount of hp every tick, so an hp that ordinary cancellation leaves at
    /// <c>+2.7e-14</c> is dead in the reference. A raw <c>&lt;= 0</c> here would
    /// leave that character alive on one hp-worth of nothing, and the divergence
    /// would then be a whole player rather than a last digit.
    /// </remarks>
    [Fact]
    public void HpWithinGmlsFuzzOfZeroIsDead()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 2.7e-14);

        Assert.True(Tick(world, ref state).Died[1]);
        Assert.False(state.Alive[1]);
    }

    /// <summary>
    /// End Step <c>exit</c>s on death, so nothing below the check runs.
    /// </summary>
    /// <remarks>
    /// The observable half of that is the weapon: <c>End Step.xml:64-65</c>
    /// drags it to <c>round(x)</c>, and a character that dies this tick leaves
    /// it wherever it was. It matters because a weapon's position is where the
    /// <em>next</em> tick's shot leaves from — and if a slot could die and
    /// respawn without the weapon being rebuilt, a stale muzzle would fire from
    /// the corpse. <see cref="WorldState.Clear"/> is what actually makes that
    /// safe; this pins the ordering it depends on.
    /// </remarks>
    [Fact]
    public void EndStepStopsAtTheDeathCheck()
    {
        var world = World();
        var character = new CharacterState
        {
            Class = PlayerClass.Scout,
            Team = Team.Red,
            X = OpenX,
            Y = OpenY,
            Hp = 0,
            Facing = 1,
            LastFacing = 1,
            RunPower = ClassStats.For(PlayerClass.Scout).RunPower,
            JumpStrength = ClassStats.BaseJumpStrength,

            // Aiming left, so a surviving character's End Step would flip
            // Facing to -1 on its way past.
            NetAimDirection = (ushort)Math.Round(180 * 65536.0 / 360.0),
        };

        var weapon = WeaponState.Spawn(PlayerClass.Scout, 0, 0);

        Assert.True(CharacterPhysics.EndStep(world, ref character, ref weapon));

        Assert.Equal(1, character.Facing);
        Assert.Equal(0, weapon.X);
        Assert.Equal(0, weapon.Y);
    }

    // --- attribution ----------------------------------------------------------

    /// <summary>
    /// The killer is the last dealer and the assist is the one before it.
    /// </summary>
    [Fact]
    public void TheAssistIsTheSecondToLastDamageDealer()
    {
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);
        state.Characters[1].LastDamageDealer = 5;
        state.Characters[1].SecondToLastDamageDealer = 9;
        state.Characters[1].LastDamageSource = DamageSource.Rocketlauncher;

        var death = Deaths.Resolve(state, 1);

        Assert.Equal(1, death.Slot);
        Assert.Equal(5, death.Killer);
        Assert.Equal(9, death.Assistant);
        Assert.Equal(DamageSource.Rocketlauncher, death.Source);
    }

    /// <summary>
    /// Nobody is 255 in both roster fields, and never slot 0.
    /// </summary>
    /// <remarks>
    /// Slot 0 is the server's own reserved player and always exists, so a
    /// defaulted zero would silently credit it with every unattributed kill —
    /// the specific bug <see cref="CharacterState.Nobody"/> exists to stop.
    /// </remarks>
    [Fact]
    public void AnUnattributedDeathNamesNobodyAndNotSlotZero()
    {
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);

        var death = Deaths.Resolve(state, 1);

        Assert.Equal(CharacterState.Nobody, death.Killer);
        Assert.Equal(CharacterState.Nobody, death.Assistant);
        Assert.Equal(DamageSource.None, death.Source);
    }

    /// <summary>
    /// <b>The killer's medic takes the assist</b>, off the victim's second
    /// dealer entirely.
    /// </summary>
    /// <remarks>
    /// <c>with(lastDamageDealer) if (object) if (object.healer) assistant =
    /// object.healer;</c> — the <c>with</c> is over the <em>killer's</em>
    /// player, so <c>object.healer</c> is the medic beaming <em>them</em>, not
    /// anyone the victim was near. It is a replacement and not a tie-break: the
    /// second dealer loses the assist even though they really did contribute
    /// damage.
    /// <para>
    /// The fixture cannot reach this — two scouts, no medic — so this is the
    /// only thing holding the redirect in place.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheKillersMedicTakesTheAssistFromTheSecondDealer()
    {
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);
        PutScout(ref state, 2, Team.Blue);

        state.Characters[1].LastDamageDealer = 2;
        state.Characters[1].SecondToLastDamageDealer = 9;

        // Slot 2 is being healed by slot 4.
        state.Characters[2].Healer = 4;

        Assert.Equal(4, Deaths.Resolve(state, 1).Assistant);
    }

    /// <summary>
    /// A healer in slot 0 is a real healer, because "nobody" is 255.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="AnUnattributedDeathNamesNobodyAndNotSlotZero"/>:
    /// the redirect asks whether a healer <em>exists</em>, and an implementation
    /// that spelled that as <c>healer != 0</c> would silently drop every assist
    /// earned by the host's own player.
    /// </remarks>
    [Fact]
    public void AHealerInSlotZeroStillTakesTheAssist()
    {
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);
        PutScout(ref state, 2, Team.Blue);

        state.Characters[1].LastDamageDealer = 2;
        state.Characters[1].SecondToLastDamageDealer = 9;
        state.Characters[2].Healer = 0;

        Assert.Equal(0, Deaths.Resolve(state, 1).Assistant);
    }

    /// <summary>
    /// A killer who is already dead skips the redirect rather than dereferencing
    /// nothing.
    /// </summary>
    /// <remarks>
    /// This is <c>if (object)</c>, and it is the case a mutual kill hits every
    /// time: <c>Destroy.xml</c> writes <c>player.object = -1</c>, so the killer
    /// of the second victim in a trade has no character to read a healer from.
    /// The victim's own second dealer keeps the assist.
    /// <para>
    /// It is also the evidence that GM8's truth test rejects -1: were <c>-1</c>
    /// truthy, this guard would evaluate <c>(-1).healer</c> and the reference
    /// would error out on every trade.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeadKillerHasNoMedicToRedirectTo()
    {
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);

        // Slot 2 dealt the damage and has since died: the player is still on the
        // roster, the character is not.
        state.Characters[1].LastDamageDealer = 2;
        state.Characters[1].SecondToLastDamageDealer = 9;
        Assert.False(state.IsAlive(2));

        Assert.Equal(9, Deaths.Resolve(state, 1).Assistant);
    }

    /// <summary>
    /// A trade: both die, each credits the other, and neither gets an assist.
    /// </summary>
    /// <remarks>
    /// The shape the fixture actually contains, as a unit test — and the reason
    /// the killer is read at the moment of death rather than held as a
    /// reference. Slot 1 dies first and is gone by the time slot 2's End Step
    /// runs; slot 2's <c>LastDamageDealer</c> still names slot 1, because it is
    /// a <em>player</em> index and the player is still connected.
    /// </remarks>
    [Fact]
    public void ATradeCreditsBothKillersEvenThoughTheFirstIsAlreadyGone()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);
        PutScout(ref state, 2, Team.Blue, hp: 0);

        state.Characters[1].LastDamageDealer = 2;
        state.Characters[1].LastDamageSource = DamageSource.Scattergun;
        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].LastDamageSource = DamageSource.Scattergun;

        var events = Tick(world, ref state);

        Assert.True(events.Died[1]);
        Assert.True(events.Died[2]);
        Assert.Equal(2, events.Deaths[1].Killer);
        Assert.Equal(1, events.Deaths[2].Killer);
        Assert.Equal(CharacterState.Nobody, events.Deaths[1].Assistant);
        Assert.Equal(CharacterState.Nobody, events.Deaths[2].Assistant);
        Assert.False(state.Alive[1]);
        Assert.False(state.Alive[2]);
    }

    // --- what the body leaves behind ------------------------------------------

    /// <summary>
    /// Death takes the weapon and leaves the sentry and anything already in the
    /// air.
    /// </summary>
    /// <remarks>
    /// The reference's own split, and it is not arbitrary: a weapon is created
    /// by <c>Character.events/Create.xml</c> and destroyed with its owner
    /// (<c>Destroy.xml</c>), while a sentry belongs to the <em>player</em> and
    /// keeps firing while its engineer respawns. A projectile's
    /// <c>ownerPlayer</c> is likewise a player, which is what lets a rocket
    /// still credit a kill to somebody who died before it landed.
    /// </remarks>
    [Fact]
    public void DeathDestroysTheWeaponButNotTheSentryOrTheProjectiles()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);

        // A real sentry, not a zeroed struct: the sentry simulates now, and a
        // default SentryState has 0 hp — which End Step correctly reads as a
        // destruction. Built by the factory, in open air; it falls during the
        // tick but its X never moves. Its owner has to be an engineer of its
        // team in the world's own player state, or the whole of Begin Step's
        // class/team guard — the simulation's since phase 6 — eats it.
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;
        state.Players[1].Class = PlayerClass.Engineer;
        state.HasSentry[1] = true;
        state.Sentries[1] = SentryState.Build(Team.Red, OpenX + 100, OpenY, 1);

        var handle = state.SpawnProjectile(new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            OwnerSlot = 1,
            Team = Team.Red,
            X = OpenX + 200,
            Y = OpenY,
            Lifetime = 30,
        });

        Tick(world, ref state);

        Assert.False(state.Alive[1]);

        // A cleared weapon, not a fresh one: WeaponState.Spawn leaves
        // HealTarget at NoTarget (255) and a full magazine, and `default` has
        // neither.
        Assert.Equal(default, state.Weapons[1]);

        Assert.True(state.HasSentry[1]);
        Assert.Equal(OpenX + 100, state.Sentries[1].X);

        Assert.True(state.TryGetProjectile(handle, out var pellet));
        Assert.Equal(1, pellet.OwnerSlot);
    }

    /// <summary>
    /// Dying clears the slot; it does not compact the roster.
    /// </summary>
    /// <remarks>
    /// <b>The distinction CONTRIBUTING.md warns about, from the side that is easy to
    /// get wrong.</b> A death is not a departure: the player is still connected
    /// and still owns their roster index, so every slot above them has to stay
    /// where it is. Compacting here would hand each of those characters to the
    /// wrong player for as long as the corpse was on the ground.
    /// </remarks>
    [Fact]
    public void DeathDoesNotRenumberTheRoster()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);
        PutScout(ref state, 2, Team.Blue);

        state.Characters[2].LastDamageDealer = 3;

        Tick(world, ref state);

        Assert.False(state.Alive[1]);
        Assert.True(state.Alive[2]);
        Assert.Equal(PlayerClass.Scout, state.Characters[2].Class);

        // Untouched: RemoveSlot renumbers stored roster indices and Clear must
        // not.
        Assert.Equal(3, state.Characters[2].LastDamageDealer);
    }

    /// <summary>
    /// Nothing respawns by itself yet, and that is deliberate.
    /// </summary>
    /// <remarks>
    /// <c>Destroy.xml</c> arms <c>player.alarm[5]</c> — a timer on the
    /// <em>player</em>, of which <see cref="WorldState"/> has none. docs/STATUS.md
    /// phase 6. This is here so that when respawn does land, the change is
    /// visible as a test being rewritten rather than as one quietly starting to
    /// mean something else.
    /// </remarks>
    [Fact]
    public void ADeadSlotStaysEmpty()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Red, hp: 0);

        for (var i = 0; i < 200; i++)
            Tick(world, ref state);

        Assert.False(state.Alive[1]);
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// <c>PLAYER_DEATH</c> is four bytes: victim, killer, assistant, cause.
    /// </summary>
    /// <remarks>
    /// The framing <c>ServerStream</c> already assumed
    /// (<c>case MessageType.PlayerDeath: return Fixed(..., 4)</c>), now written
    /// from the other end so the two cannot drift apart.
    /// </remarks>
    [Fact]
    public void ADeathMessageIsVictimKillerAssistantCause()
    {
        var writer = new NetWriter();

        ServerMessages.PlayerDeath(writer, victim: 3, killer: 7, assistant: 255, cause: DamageSource.Scattergun);

        Assert.Equal(
            new byte[] { (byte)MessageType.PlayerDeath, 3, 7, 255, (byte)DamageSource.Scattergun },
            writer.WrittenSpan.ToArray());
    }
}
