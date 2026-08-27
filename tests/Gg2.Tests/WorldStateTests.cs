using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The state a match lives in: roster slots, weapons, sentries and the
/// projectile pool.
/// </summary>
/// <remarks>
/// Two properties are load-bearing beyond the obvious ones and are what most of
/// this file is about.
/// <para>
/// <b>A slot index is a roster index.</b> Every field that names another player
/// stores one, and the roster compacts on <c>PLAYER_LEAVE</c>. CLAUDE.md's
/// warning — "off by one, intermittently" — is precisely the failure mode of
/// forgetting to renumber one of them, so each is checked here.
/// </para>
/// <para>
/// <b>Projectiles are walked in creation order.</b> Pool slots are recycled and
/// so say nothing about when a projectile was fired; docs/SIMULATION.md makes
/// creation order observable wherever instances interact.
/// </para>
/// </remarks>
public sealed class WorldStateTests
{
    private static ProjectileState Shot(byte owner, Team team = Team.Red) => new()
    {
        Kind = ProjectileKind.Shot,
        Team = team,
        OwnerSlot = owner,
        Source = DamageSource.Scattergun,
    };

    // --- the projectile pool -------------------------------------------------

    [Fact]
    public void SpawnedProjectilesResolveThroughTheirHandles()
    {
        var state = default(WorldState);
        var handle = state.SpawnProjectile(Shot(3));

        Assert.True(state.TryGetProjectile(handle, out var projectile));
        Assert.Equal(ProjectileKind.Shot, projectile.Kind);
        Assert.Equal(3, projectile.OwnerSlot);
        Assert.Equal(1, state.LiveProjectileCount);
    }

    /// <summary>
    /// The whole reason a handle carries a generation. Freeing and respawning
    /// reuses the slot, and the old handle must not follow it.
    /// </summary>
    [Fact]
    public void AStaleHandleDoesNotResolveToTheProjectileThatReplacedIt()
    {
        var state = default(WorldState);
        var first = state.SpawnProjectile(Shot(1));
        Assert.True(state.FreeProjectile(first));

        var second = state.SpawnProjectile(Shot(2));

        Assert.Equal(first.Index, second.Index);
        Assert.False(state.IsLive(first));
        Assert.False(state.TryGetProjectile(first, out _));
        Assert.True(state.TryGetProjectile(second, out var projectile));
        Assert.Equal(2, projectile.OwnerSlot);
    }

    /// <summary>
    /// Generation 0 is never issued, so a defaulted handle field — a mine that
    /// nothing has reflected — cannot accidentally name pool slot 0.
    /// </summary>
    [Fact]
    public void TheDefaultHandleNamesNothingEvenWhenSlotZeroIsLive()
    {
        var state = default(WorldState);
        var handle = state.SpawnProjectile(Shot(0));

        Assert.Equal(0, handle.Index);
        Assert.True(ProjectileHandle.None.IsNone);
        Assert.False(state.IsLive(ProjectileHandle.None));
        Assert.False(state.IsLive(default));
    }

    [Fact]
    public void FreeingIsIdempotentAndReportsIt()
    {
        var state = default(WorldState);
        var handle = state.SpawnProjectile(Shot(1));

        Assert.True(state.FreeProjectile(handle));
        Assert.False(state.FreeProjectile(handle));
        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>
    /// Removing from the middle keeps the rest in the order they were fired —
    /// which is what a swap-with-last would have thrown away.
    /// </summary>
    [Fact]
    public void TheLiveWalkStaysInCreationOrderAcrossAFreeInTheMiddle()
    {
        var state = default(WorldState);
        var handles = new ProjectileHandle[5];
        for (byte i = 0; i < 5; i++)
            handles[i] = state.SpawnProjectile(Shot(i));

        Assert.True(state.FreeProjectile(handles[1]));
        Assert.True(state.FreeProjectile(handles[3]));

        Assert.Equal([0, 2, 4], Owners(state));

        // The recycled slots are handed back out lowest-first, and the new
        // arrivals go on the end of the walk rather than into the gaps.
        state.SpawnProjectile(Shot(9));
        Assert.Equal([0, 2, 4, 9], Owners(state));
    }

    [Fact]
    public void AFullPoolReturnsTheNullHandleRatherThanOverwriting()
    {
        var state = default(WorldState);
        for (var i = 0; i < WorldState.MaxProjectiles; i++)
            Assert.False(state.SpawnProjectile(Shot(0)).IsNone);

        Assert.True(state.SpawnProjectile(Shot(0)).IsNone);
        Assert.Equal(WorldState.MaxProjectiles, state.LiveProjectileCount);
    }

    [Fact]
    public void AProjectileWithNoKindIsRejected() =>
        Assert.Throws<ArgumentException>(() =>
        {
            var state = default(WorldState);
            state.SpawnProjectile(default);
        });

    // --- cloning -------------------------------------------------------------

    /// <summary>
    /// docs/BOTAI.md item 1: a clone is a copy of bytes. If any part of the
    /// state were behind a reference, mutating the copy would be visible through
    /// the original — and rollout search would silently score the wrong world.
    /// </summary>
    [Fact]
    public void ACloneSharesNothingWithItsOriginal()
    {
        var original = default(WorldState);
        original.Characters[2].Hp = 100;
        original.Weapons[2].AmmoCount = 6;
        original.Sentries[2].Hp = 25;
        var handle = original.SpawnProjectile(Shot(2));

        var clone = original;
        clone.Characters[2].Hp = 1;
        clone.Weapons[2].AmmoCount = 0;
        clone.Sentries[2].Hp = 1;
        Assert.True(clone.FreeProjectile(handle));

        Assert.Equal(100, original.Characters[2].Hp);
        Assert.Equal(6, original.Weapons[2].AmmoCount);
        Assert.Equal(25, original.Sentries[2].Hp);
        Assert.Equal(1, original.LiveProjectileCount);
        Assert.True(original.IsLive(handle));
    }

    // --- roster compaction ---------------------------------------------------

    [Fact]
    public void RemovingASlotShiftsCharactersWeaponsAndSentriesTogether()
    {
        var state = default(WorldState);
        for (var slot = 0; slot < 3; slot++)
        {
            state.Alive[slot] = true;
            state.Characters[slot].Hp = 10 + slot;
            state.Weapons[slot].AmmoCount = 20 + slot;
            state.HasSentry[slot] = true;
            state.Sentries[slot].Hp = 30 + slot;
        }

        state.RemoveSlot(1);

        Assert.Equal(10, state.Characters[0].Hp);
        Assert.Equal(12, state.Characters[1].Hp);
        Assert.Equal(22, state.Weapons[1].AmmoCount);
        Assert.Equal(32, state.Sentries[1].Hp);
        Assert.False(state.Alive[2]);
        Assert.False(state.HasSentry[2]);
    }

    /// <summary>
    /// The intermittent off-by-one CLAUDE.md warns about, in every field that
    /// can carry it: a stored roster index above the departing slot moves down
    /// with the roster.
    /// </summary>
    [Fact]
    public void EveryStoredRosterIndexRenumbersWhenSomebodyLeaves()
    {
        var state = default(WorldState);
        state.Alive[0] = true;
        state.Characters[0].LastDamageDealer = 5;
        state.Characters[0].SecondToLastDamageDealer = 4;
        state.Characters[0].BurnedBy = 6;
        state.Characters[0].Healer = 3;
        state.HasSentry[0] = true;
        state.Sentries[0].LastDamageDealer = 5;
        state.Weapons[0].HealTarget = 5;
        var handle = state.SpawnProjectile(Shot(5));

        state.RemoveSlot(2);

        Assert.Equal(4, state.Characters[0].LastDamageDealer);
        Assert.Equal(3, state.Characters[0].SecondToLastDamageDealer);
        Assert.Equal(5, state.Characters[0].BurnedBy);
        Assert.Equal(2, state.Characters[0].Healer);
        Assert.Equal(4, state.Sentries[0].LastDamageDealer);
        Assert.Equal(4, state.Weapons[0].HealTarget);
        Assert.True(state.TryGetProjectile(handle, out var projectile));
        Assert.Equal(4, projectile.OwnerSlot);
    }

    /// <summary>
    /// A reference to the player who actually left becomes nobody. Following the
    /// compaction instead would hand their claim — and the kill it is about to
    /// get — to whoever slid into their index.
    /// </summary>
    /// <remarks>
    /// ⚠️ The <em>owner</em> of a projectile used to be this test's second half,
    /// and it was asserting the port's bug: a departing player's shot cannot be
    /// left ownerless because it no longer exists — <c>Player.events/Destroy.xml</c>
    /// destroys it. See <see cref="RemoveSlotTests"/>. The rule itself is still
    /// live, on every other stored index a projectile carries; a mine reflected
    /// by the departing pyro and owned by somebody else is the reachable case,
    /// because a reflector outlives the projectile's owner and the reference has
    /// no sweep for it.
    /// </remarks>
    [Fact]
    public void AReferenceToTheDepartingSlotBecomesNobody()
    {
        var state = default(WorldState);
        state.Alive[0] = true;
        state.Characters[0].LastDamageDealer = 2;

        var mine = Shot(4);
        mine.Kind = ProjectileKind.Mine;
        mine.Reflector = 2;
        var handle = state.SpawnProjectile(mine);

        state.RemoveSlot(2);

        Assert.Equal(CharacterState.Nobody, state.Characters[0].LastDamageDealer);
        Assert.True(state.TryGetProjectile(handle, out var projectile));
        Assert.Equal(CharacterState.Nobody, projectile.Reflector);

        // And the owner still followed the compaction, since 4 was above 2.
        Assert.Equal(3, projectile.OwnerSlot);
    }

    [Fact]
    public void NobodyStaysNobody()
    {
        var state = default(WorldState);
        state.Alive[0] = true;
        state.Characters[0].LastDamageDealer = CharacterState.Nobody;
        state.Characters[0].Healer = CharacterState.Nobody;

        state.RemoveSlot(1);

        Assert.Equal(CharacterState.Nobody, state.Characters[0].LastDamageDealer);
        Assert.Equal(CharacterState.Nobody, state.Characters[0].Healer);
    }

    /// <summary>
    /// <c>Clear</c> is a death, not a departure: the weapon dies with the
    /// character, the sentry does not, and neither does a rocket already in the
    /// air.
    /// </summary>
    [Fact]
    public void ClearingASlotKeepsTheSentryAndAnyProjectilesInFlight()
    {
        var state = default(WorldState);
        state.Alive[1] = true;
        state.Characters[1].Hp = 100;
        state.Weapons[1].AmmoCount = 4;
        state.HasSentry[1] = true;
        state.Sentries[1].Hp = 100;
        var handle = state.SpawnProjectile(Shot(1));

        state.Clear(1);

        Assert.False(state.Alive[1]);
        Assert.Equal(0, state.Characters[1].Hp);
        Assert.Equal(0, state.Weapons[1].AmmoCount);
        Assert.True(state.HasSentry[1]);
        Assert.Equal(100, state.Sentries[1].Hp);
        Assert.True(state.TryGetProjectile(handle, out var projectile));
        Assert.Equal(1, projectile.OwnerSlot);
    }

    /// <summary>
    /// Not a correctness test — a budget. Rollout search clones this thousands
    /// of times per decision (docs/BOTAI.md item 1), so its size is a cost that
    /// should move deliberately rather than by accident. If this fails, decide
    /// whether the field that grew it is worth the clone before raising the
    /// number.
    /// </summary>
    [Fact]
    public void ACloneStaysWithinItsBudget()
    {
        var size = System.Runtime.CompilerServices.Unsafe.SizeOf<WorldState>();

        // 53,896 bytes at the end of phase 5 stage 2; 67,208 after stage 3 grew
        // a projectile by the fields a Shot needs; 99,976 after stage 5 gave one
        // a rocket's leash, knockback, fade and pass-through set; 132,016 after
        // phase 6's scoreboard; 143,304 after Quote's Blade, which cost a
        // projectile its `ammoDrained` and its `bubbled` flag and a weapon four
        // small fields. ⚠️ That last 11 KB is what overflowed the stack in
        // ScoringTests — a struct this size is not free to pass by value, and
        // the budget below is not the only thing its growth can break.
        // The ceiling is a budget rather than a
        // measurement, so it does not move with each addition — but a change
        // that doubled the state would trip it, which is the point.
        //
        // It moved from 96 KB to 128 KB deliberately, and the rocket's fields
        // were audited first rather than after: blastRadius and explosionDamage
        // came back out as per-kind lookups (Explosions), and image_alpha became
        // a four-byte tick count, because nothing writes any of the three to a
        // per-instance value. What is left — knockback, the leash anchor, the
        // travel budget, three alarms and the passed-through set — the reference
        // genuinely mutates per rocket.
        //
        // It moved again, 128 KB to 160 KB, for the scoreboard, and that one was
        // audited the same way. Two StatBuffers per player is 13,312 bytes — a
        // tenth of the state — and every part of it is load-bearing:
        //
        //  * Both arrays are real. Every award in the reference writes stats[]
        //    and roundStats[] together, and ARENA_ENDROUND reads the second.
        //  * Doubles, not integers: an assist is worth half a point and healing
        //    accumulates fractional hit points.
        //  * Thirteen entries and not eleven. DOMINATIONS and REVENGE are
        //    vestigial in the reference too, and the 2,048 bytes they cost buys
        //    away an index mapping between StatIndex and the buffer — which is
        //    the same class of silent off-by-one as a roster index, and this
        //    file exists to keep those out.
        //
        // Four fifths of the state is still the 512-slot projectile pool, so the
        // next real lever is that number, and shrinking it is a behaviour
        // change: see WorldState.MaxProjectiles.
        Assert.True(size < 160 * 1024, $"WorldState is {size} bytes");
    }

    private static byte[] Owners(in WorldState state)
    {
        var owners = new byte[state.LiveProjectileCount];
        for (var i = 0; i < owners.Length; i++)
            owners[i] = state.Projectiles[state.LiveProjectileSlot(i)].OwnerSlot;

        return owners;
    }

    // --- creation order ------------------------------------------------------

    /// <summary>
    /// ⚠️ <b>A respawn makes slot order stop being creation order, and the
    /// ordering helper follows the respawn rather than the slot.</b>
    /// </summary>
    /// <remarks>
    /// <b>This is the whole of the rule, and it is observable.</b> A rocket
    /// explodes on the <em>first</em> character its collision event fires
    /// against, and GM8 fires those in ascending instance id — so a low slot
    /// that died and came back is visited after a high slot that never did.
    /// Measured on <c>reference-gg_debug-closedtdm.trace</c>: see
    /// <see cref="CharacterState.CreationOrder"/>.
    /// <para>
    /// ⚠️ <b>The last assertion is the control.</b> Without it a helper that
    /// simply returned the live slots in slot order would pass the first two —
    /// slot order and creation order agree until something respawns, which is
    /// exactly why this was wrong for six phases and no test noticed.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARespawnMovesACharacterToTheBackOfTheCreationOrder()
    {
        var state = default(WorldState);

        // Four characters created in slot order, so the two orders agree.
        for (var slot = 0; slot < 4; slot++)
        {
            state.Characters[slot] = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
            state.Characters[slot].CreationOrder = ++state.NextCharacterOrder;
            state.Alive[slot] = true;
        }

        Span<byte> order = stackalloc byte[WorldState.MaxSlots];

        Assert.Equal(4, state.LiveCharactersInCreationOrder(order));
        Assert.Equal<byte[]>([0, 1, 2, 3], order[..4].ToArray());

        // Slot 1 dies and comes back, so its character is now the youngest.
        state.Clear(1);
        state.Characters[1] = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
        state.Characters[1].CreationOrder = ++state.NextCharacterOrder;
        state.Alive[1] = true;

        var count = state.LiveCharactersInCreationOrder(order);

        Assert.Equal(4, count);
        Assert.Equal<byte[]>([0, 2, 3, 1], order[..4].ToArray());
    }

    /// <summary>
    /// <see cref="WorldState.Spawn"/> issues the creation order itself, and
    /// every character gets a distinct one.
    /// </summary>
    /// <remarks>
    /// The helper above is only right if something fills the field. Pre-
    /// incremented, so no spawned character can share zero with one the parity
    /// harness placed directly.
    /// </remarks>
    [Fact]
    public void SpawningIssuesAFreshCreationOrder()
    {
        var level = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));
        var spawns = new SpawnPoints(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.RespawnTime = 0;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        foreach (var slot in new[] { 3, 1, 2 })
        {
            state.AddPlayer(slot);
            Assert.True(state.Spawn(spawns, slot, PlayerClass.Soldier, Team.Red, 0, 0));
        }

        // Issued in the order Spawn was called, not in slot order.
        Assert.Equal(1u, state.Characters[3].CreationOrder);
        Assert.Equal(2u, state.Characters[1].CreationOrder);
        Assert.Equal(3u, state.Characters[2].CreationOrder);

        Span<byte> order = stackalloc byte[WorldState.MaxSlots];
        Assert.Equal(3, state.LiveCharactersInCreationOrder(order));
        Assert.Equal<byte[]>([3, 1, 2], order[..3].ToArray());
    }
}
