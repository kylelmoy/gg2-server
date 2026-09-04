using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// What a departure destroys, which is very nearly everything.
/// </summary>
/// <remarks>
/// <c>Player.events/Destroy.xml</c>, reached from
/// <c>Scripts/Misc/removePlayer.gml</c> and
/// <c>GameServerBeginStep.gml:68</c>. Two rules in one event:
/// <list type="number">
/// <item>
/// <c>if(object != -1) with(object) instance_destroy();</c> — the departing
/// player's character is destroyed, which is the whole of
/// <c>Character.events/Destroy.xml</c> and not only the intel drop.
/// </item>
/// <item>
/// four <c>with</c> blocks that destroy every <c>Rocket</c>,
/// <c>BurningProjectile</c>, <c>Shot</c> and <c>Needle</c> the player owns, and
/// a fifth for the <c>Sentry</c>.
/// </item>
/// </list>
/// <para>
/// <b>These run the opposite way from the intuition.</b> A rocket outlives
/// its shooter's <em>death</em> on purpose — <c>ownerPlayer</c> is a player and
/// not a character, precisely so a kill can still be credited to somebody who
/// died first. The moment that player is gone there is nobody left to credit,
/// so the same rocket is destroyed. <see cref="WorldStateTests"/>'s
/// <c>ClearingASlotKeepsTheSentryAndAnyProjectilesInFlight</c> is the other half
/// of the pair and has to keep passing for either to mean anything.
/// </para>
/// </remarks>
public sealed class RemoveSlotTests
{
    private static ProjectileState Owned(ProjectileKind kind, byte owner) => new()
    {
        Kind = kind,
        Team = Team.Red,
        OwnerSlot = owner,
        Source = DamageSource.Scattergun,
    };

    /// <summary>Every projectile kind the pool can hold.</summary>
    private static readonly ProjectileKind[] AllKinds =
    [
        ProjectileKind.Shot, ProjectileKind.Rocket, ProjectileKind.Mine,
        ProjectileKind.Flame, ProjectileKind.Needle, ProjectileKind.Flare,
        ProjectileKind.Blade, ProjectileKind.Bubble, ProjectileKind.Stab,
    ];

    private static void PutScout(ref WorldState state, int slot)
    {
        state.Characters[slot] = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, 100, 100);
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = Team.Red;
        state.Players[slot].Class = PlayerClass.Scout;
    }

    // --- the player's own sweep ----------------------------------------------

    /// <summary>
    /// A departing player's projectiles are destroyed, every kind of
    /// them — four of the five families by the player's own sweep and the rest
    /// by the character it destroys first.
    /// </summary>
    /// <remarks>
    /// Guards against a soldier disconnecting and still killing somebody with a
    /// rocket a second later.
    /// </remarks>
    [Theory]
    [InlineData(ProjectileKind.Rocket)]
    [InlineData(ProjectileKind.Shot)]
    [InlineData(ProjectileKind.Needle)]
    [InlineData(ProjectileKind.Flame)]
    [InlineData(ProjectileKind.Flare)]
    [InlineData(ProjectileKind.Mine)]
    [InlineData(ProjectileKind.Blade)]
    [InlineData(ProjectileKind.Bubble)]
    [InlineData(ProjectileKind.Stab)]
    public void ADepartingPlayerTakesEveryKindOfProjectileWithThem(ProjectileKind kind)
    {
        var state = default(WorldState);
        PutScout(ref state, 1);
        PutScout(ref state, 2);
        var handle = state.SpawnProjectile(Owned(kind, 2));

        state.RemoveSlot(2);

        Assert.False(state.TryGetProjectile(handle, out _));
        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>
    /// Nothing of anybody else's goes with them.
    /// </summary>
    /// <remarks>
    /// The control for the owner test in both sweeps: <c>if(ownerPlayer ==
    /// other.id)</c>. Deleting either guard empties the pool, and every other
    /// test here would still pass.
    /// </remarks>
    [Fact]
    public void NobodyElsesProjectilesGoWithThem()
    {
        var state = default(WorldState);
        PutScout(ref state, 1);
        PutScout(ref state, 2);
        PutScout(ref state, 3);

        var mine = state.SpawnProjectile(Owned(ProjectileKind.Rocket, 2));
        var theirs = state.SpawnProjectile(Owned(ProjectileKind.Rocket, 1));
        var above = state.SpawnProjectile(Owned(ProjectileKind.Rocket, 3));

        state.RemoveSlot(2);

        Assert.False(state.TryGetProjectile(mine, out _));

        Assert.True(state.TryGetProjectile(theirs, out var below));
        Assert.Equal(1, below.OwnerSlot);

        // And the survivor above the gap followed the compaction.
        Assert.True(state.TryGetProjectile(above, out var shifted));
        Assert.Equal(2, shifted.OwnerSlot);
    }

    /// <summary>
    /// A player with no character still has their projectiles swept.
    /// </summary>
    /// <remarks>
    /// <c>if(object != -1)</c> guards only the character destruction; the four
    /// <c>with</c> blocks below it are unconditional. A player killed by a
    /// rocket they had already fired, who disconnects before it lands, is the
    /// reachable case.
    /// </remarks>
    [Fact]
    public void ADeadPlayersDepartureStillSweepsWhatIsInTheAir()
    {
        var state = default(WorldState);
        PutScout(ref state, 1);
        state.AddPlayer(2);
        var handle = state.SpawnProjectile(Owned(ProjectileKind.Rocket, 2));

        Assert.False(state.Alive[2]);

        state.RemoveSlot(2);

        Assert.False(state.TryGetProjectile(handle, out _));
    }

    /// <summary>
    /// <b>A departing player's sentry goes with them</b>, and the compaction
    /// is the whole of it.
    /// </summary>
    /// <remarks>
    /// <c>with(Sentry) if(ownerPlayer == other.id) instance_destroy();</c> is the
    /// fifth line of the sweep and the port transcribes none of it — a sentry
    /// lives in its owner's own slot, so shifting the roster down overwrites it.
    /// That is true by construction rather than by intent, which is exactly why
    /// it is asserted: a later change that moved sentries out of the slot array
    /// would break it silently.
    /// </remarks>
    [Fact]
    public void ADepartingPlayersSentryGoesWithThem()
    {
        var state = default(WorldState);
        PutScout(ref state, 1);
        PutScout(ref state, 2);
        PutScout(ref state, 3);

        state.HasSentry[2] = true;
        state.Sentries[2].Hp = 111;
        state.HasSentry[3] = true;
        state.Sentries[3].Hp = 222;

        state.RemoveSlot(2);

        // Slot 3's sentry slid down into 2; the departing player's is gone.
        Assert.True(state.HasSentry[2]);
        Assert.Equal(222, state.Sentries[2].Hp);
        Assert.False(state.HasSentry[3]);
    }

    // --- the character it destroys first -------------------------------------

    /// <summary>
    /// <b>The whole character is destroyed, not only its flag.</b>
    /// </summary>
    /// <remarks>
    /// The distinguishing observable is the medic beam: <c>Clear</c> releases
    /// the patient (<c>Medigun.events/Destroy.xml</c>), and a path that only
    /// dropped the intel would leave the patient naming a slot with no medic in
    /// it — which <c>Deaths.Resolve</c> would then turn into an assist the
    /// reference never gives. It is <em>not</em> caught by the renumbering,
    /// which would rewrite a healer of 2 to nobody; here the healer is slot 1,
    /// below the gap, and survives untouched unless the beam is really cut.
    /// </remarks>
    [Fact]
    public void ADepartingMedicsBeamIsCutRatherThanLeftPointing()
    {
        var state = default(WorldState);
        PutScout(ref state, 1);
        PutScout(ref state, 2);

        // Slot 1 is the medic and slot 2 the patient, so that the *patient*
        // leaving is what has to release it.
        state.Weapons[1].HealTarget = 2;
        state.Characters[2].Healer = 1;

        state.RemoveSlot(2);

        Assert.Equal(WeaponState.NoTarget, state.Weapons[1].HealTarget);
    }

    /// <summary>
    /// A departing carrier drops the briefcase where they stood.
    /// </summary>
    /// <remarks>
    /// The reference's own comment on that line names "if the player just
    /// leaves" as one of the cases it has to cover. Without it a disconnecting
    /// carrier takes the flag out of the world permanently: nobody holds it and
    /// no instance exists to return itself.
    /// </remarks>
    [Fact]
    public void ADepartingCarrierDropsTheBriefcase()
    {
        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);
        var state = default(WorldState);
        Intel.Place(ref state, map);
        PutScout(ref state, 1);

        state.Characters[1].X = 1234;
        state.Characters[1].Y = 567;
        state.Characters[1].CarryingIntel = true;
        state.BlueIntel.OnField = false;

        state.RemoveSlot(1);

        // A red character carries blue's briefcase, and a drop puts an instance
        // back on the field where the body was.
        Assert.True(state.BlueIntel.OnField);
        Assert.Equal(1234, state.BlueIntel.X);
        Assert.Equal(567, state.BlueIntel.Y);
    }

    /// <summary>
    /// A slot with nothing in it is not a crash and not a drop.
    /// </summary>
    /// <remarks>
    /// <c>if(object != -1)</c>. A spectator who never picked a team has no
    /// character, so there is no <c>Character.events/Destroy.xml</c> to run —
    /// and the port must not run <c>Clear</c> on a slot whose
    /// <see cref="CharacterState"/> is a zeroed one, which claims slot 0 is
    /// healing it.
    /// </remarks>
    [Fact]
    public void ASpectatorLeavingDestroysNoCharacter()
    {
        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);
        var state = default(WorldState);
        Intel.Place(ref state, map);
        state.AddPlayer(1);

        var homeX = state.BlueIntel.X;
        var homeY = state.BlueIntel.Y;

        state.RemoveSlot(1);

        // Nothing moved: the flag is still on its base rather than dropped at
        // the origin by a character that never existed.
        Assert.Equal(homeX, state.BlueIntel.X);
        Assert.Equal(homeY, state.BlueIntel.Y);
    }

    // --- the pair ------------------------------------------------------------

    /// <summary>
    /// <b>A death and a departure disagree about five of the nine kinds</b>,
    /// and this is the table that says which.
    /// </summary>
    /// <remarks>
    /// <c>Mine</c>, <c>Blade</c>, <c>Bubble</c> and <c>Stab</c> belong to the
    /// <em>weapon</em>, which dies with the character, so both paths take them.
    /// <c>Rocket</c>, <c>Shot</c>, <c>Needle</c>, <c>Flame</c> and <c>Flare</c>
    /// belong to the <em>player</em> and survive a death — they are in
    /// <c>Player.events/Destroy.xml</c>'s sweep and in no weapon's.
    /// <para>
    /// Written as one table rather than as nine assertions scattered about,
    /// because the interesting property is the split and not any one row: a port
    /// that swept both paths identically would pass every other test in this
    /// file.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeathKeepsWhatBelongsToThePlayerAndADepartureDoesNot()
    {
        ProjectileKind[] weaponOwned =
            [ProjectileKind.Mine, ProjectileKind.Blade, ProjectileKind.Bubble, ProjectileKind.Stab];

        foreach (var kind in AllKinds)
        {
            var state = default(WorldState);
            PutScout(ref state, 1);
            var handle = state.SpawnProjectile(Owned(kind, 1));

            state.Clear(1);

            var survivesADeath = state.TryGetProjectile(handle, out _);
            Assert.Equal(!weaponOwned.Contains(kind), survivesADeath);
        }

        foreach (var kind in AllKinds)
        {
            var state = default(WorldState);
            PutScout(ref state, 1);
            var handle = state.SpawnProjectile(Owned(kind, 1));

            state.RemoveSlot(1);

            Assert.False(state.TryGetProjectile(handle, out _));
        }
    }
}
