using System.Runtime.CompilerServices;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// A fixed-capacity inline buffer, one entry per roster slot.
/// </summary>
/// <remarks>
/// <see cref="InlineArrayAttribute"/> rather than <c>T[]</c> on purpose. An
/// array field would make <see cref="WorldState"/> a handle to memory it does
/// not own: copying the state would alias the characters, and docs/BOTAI.md
/// item 1 — the one item it says cannot be retrofitted — asks that cloning a
/// world be a <c>memcpy</c>. Inlining the storage makes the assignment operator
/// the clone.
/// </remarks>
[InlineArray(WorldState.MaxSlots)]
public struct SlotBuffer<T>
{
    private T _element0;
}

/// <summary>
/// A fixed-capacity inline buffer, one entry per projectile pool slot.
/// </summary>
/// <remarks>
/// Separate from <see cref="SlotBuffer{T}"/> only because it is a different
/// length; the reason it is inline is the same one.
/// </remarks>
[InlineArray(WorldState.MaxProjectiles)]
public struct ProjectileBuffer<T>
{
    private T _element0;
}

/// <summary>
/// Everything one match's simulation can read or write, and nothing else.
/// </summary>
/// <remarks>
/// <b>A value type, deliberately, and with no reference in it anywhere.</b>
/// docs/BOTAI.md item 1: rollout search forks the world, plays a candidate
/// action sequence forward, scores it and throws it away, thousands of times per
/// decision — so a clone has to be a copy of bytes, not a graph walk. That rules
/// out a field pointing at a map, a config object, a logger or an event sink.
/// Read-only inputs are passed <em>to</em> <see cref="MatchSimulation.Tick"/>
/// alongside the state (the <see cref="CollisionWorld"/> is exactly that), never
/// held by it.
/// <para>
/// It also makes docs/BOTAI.md item 2 nearly free: a flat state is a
/// serialisable one.
/// </para>
/// <para>
/// <b>Not the whole match yet.</b> Characters, their weapons, their sentries and
/// the projectiles in the air — which is phases 4 and 5. Objectives, scores and
/// the HUD still live in <c>Gg2.Server.MatchState</c>; they belong here as the
/// phases that own them land, and <c>MatchState</c> should shrink as they do
/// rather than the other way round.
/// </para>
/// </remarks>
public struct WorldState
{
    /// <summary>
    /// Roster slots this state has room for.
    /// </summary>
    /// <remarks>
    /// The reference clamps network clients to 48 and neither slot 0 nor a bot
    /// counts against that (<c>getNumberOfOccupiedSlots</c>), so 64 leaves room
    /// for a full server plus its bots. A roster index is a <c>ubyte</c> on the
    /// wire, so 256 is the format's own ceiling if this ever needs raising —
    /// the cost is only the size of a clone.
    /// </remarks>
    public const int MaxSlots = 64;

    /// <summary>
    /// Projectiles that can be in the air at once, across the whole match.
    /// </summary>
    /// <remarks>
    /// <b>The reference has no such limit and this is the one place the port
    /// can lose to it.</b> GM8 creates instances until it runs out of memory;
    /// here a pool full means <see cref="SpawnProjectile"/> hands back
    /// <see cref="ProjectileHandle.None"/> and a shot that the reference would
    /// have fired does not exist. That is a divergence, not a graceful
    /// degradation, so the number is chosen to be unreachable rather than
    /// merely comfortable: a full 64-slot server of heavies firing every other
    /// tick with a 40-tick shot lifetime is about 640 live shots on paper, and
    /// no real server is 64 heavies. Raise it before trimming it.
    /// <para>
    /// The cost is paid on every clone, which is why it is not larger still:
    /// each slot is a <see cref="ProjectileState"/> plus a generation plus an
    /// order entry, and at this capacity the pool is about 43 KB of the whole
    /// state's 66 KB. <c>WorldStateTests.ACloneStaysWithinItsBudget</c> is the
    /// tripwire on that number.
    /// </para>
    /// <para>
    /// <b>The pool is now the majority of a clone, and that changes where to
    /// look for savings.</b> Stage 3 grew a projectile from 48 bytes to 80 —
    /// an <c>image_angle</c>, a lifetime, a damage figure and a perseverance
    /// flag — which put two thirds of the state into a structure that is almost
    /// always mostly empty. Before raising the capacity again, the thing to
    /// weigh is a smaller <see cref="ProjectileState"/> rather than a smaller
    /// pool: the reference has no ceiling, and a shot that does not exist is a
    /// divergence.
    /// </para>
    /// </remarks>
    public const int MaxProjectiles = 512;

    /// <summary>Each slot's character. Only meaningful where <see cref="Alive"/> is set.</summary>
    public SlotBuffer<CharacterState> Characters;

    /// <summary>Whether a slot has a character at all — spectators and the dead do not.</summary>
    public SlotBuffer<bool> Alive;

    /// <summary>
    /// Each slot's weapon. Only meaningful where <see cref="Alive"/> is set.
    /// </summary>
    /// <remarks>
    /// Beside the character rather than inside it because that is what it is:
    /// the reference's <c>currentWeapon</c> is a separate instance created and
    /// destroyed with its owner, and keeping it a separate struct means the
    /// character's own layout does not change when a weapon gains a field.
    /// </remarks>
    public SlotBuffer<WeaponState> Weapons;

    /// <summary>Each slot's sentry. Only meaningful where <see cref="HasSentry"/> is set.</summary>
    public SlotBuffer<SentryState> Sentries;

    /// <summary>
    /// Whether a slot has a sentry built or building.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="Alive"/> on purpose: a sentry outlives its
    /// engineer's character and keeps shooting while they respawn.
    /// </remarks>
    public SlotBuffer<bool> HasSentry;

    /// <summary>
    /// The projectile pool. Sparse: a slot is live only where
    /// <see cref="ProjectileState.Kind"/> is not <see cref="ProjectileKind.None"/>,
    /// and <see cref="ProjectileOrder"/> says which those are.
    /// </summary>
    public ProjectileBuffer<ProjectileState> Projectiles;

    /// <summary>
    /// How many times each pool slot has been handed out. Bumped on every
    /// spawn, so a <see cref="ProjectileHandle"/> into a recycled slot fails to
    /// resolve instead of naming the projectile that took its place.
    /// </summary>
    public ProjectileBuffer<uint> ProjectileGenerations;

    /// <summary>
    /// The live pool slots, dense in <c>[0, <see cref="LiveProjectileCount"/>)</c>
    /// and <b>in creation order</b>.
    /// </summary>
    /// <remarks>
    /// <b>The order is the point of this array.</b> docs/SIMULATION.md: "within
    /// a phase, instances run in creation order", and that is observable
    /// wherever projectiles interact — two rockets reaching the same character
    /// on the same tick, a mine detonated by a blast that also kills its owner.
    /// Pool slots are recycled lowest-index-first, so walking
    /// <see cref="Projectiles"/> directly would walk them in some order that has
    /// nothing to do with when they were fired. Walk this instead.
    /// <para>
    /// docs/SIMULATION.md's remaining open question — instance ordering under
    /// heavy churn — is a question about what creation order <em>means</em> when
    /// things are created and destroyed in the same tick. This structure can
    /// answer it either way; it does not decide it.
    /// </para>
    /// </remarks>
    public ProjectileBuffer<ushort> ProjectileOrder;

    /// <summary>How many projectiles are in the air.</summary>
    public int LiveProjectileCount;

    /// <summary>
    /// The match's own random stream — GM8's global one, as the host uses it.
    /// </summary>
    /// <remarks>
    /// <b>In the state rather than beside it, and it is four bytes.</b> A weapon
    /// draws <c>irandom(65535)</c> from the global stream and broadcasts the
    /// result (<c>Scattergun.events/User Event 1.xml</c>), so the tick needs a
    /// source of seeds. Keeping it here rather than in a field of the server
    /// means <see cref="MatchSimulation.Tick"/> stays a pure function of state
    /// and inputs, and a bot rollout that forks the world forks the stream with
    /// it — otherwise two rollouts from the same state would diverge for a
    /// reason that has nothing to do with the actions being compared
    /// (docs/BOTAI.md items 1 and 2).
    /// <para>
    /// <b>Which seeds come out is free.</b> docs/SIMULATION.md's "server-side
    /// RNG is free" covers the <em>draw</em>: no client can predict it and every
    /// client is told the answer. What is not free is what the seed is then used
    /// for, which is <see cref="Gg2.Protocol.GmlRandom"/>'s business.
    /// </para>
    /// <para>
    /// A zeroed world therefore starts at seed 0, which is a legal GM8 state
    /// (<c>random_set_seed(0)</c>) and makes a default state fully
    /// deterministic.
    /// </para>
    /// </remarks>
    public GmlRandom Random;

    /// <summary>Whether the given slot currently has a character.</summary>
    public readonly bool IsAlive(int slot) => (uint)slot < MaxSlots && Alive[slot];

    /// <summary>Removes a slot's character: it died, left, or went spectator.</summary>
    /// <remarks>
    /// The character is cleared as well as flagged, so a stale body cannot leak
    /// into the next thing that spawns in this slot and so two states that look
    /// the same really are the same bytes — which is what makes docs/BOTAI.md
    /// item 2's "restore to a bit-identical state" checkable.
    /// <para>
    /// <b>The weapon goes and the sentry stays.</b> That is the reference: a
    /// weapon is created by <c>Character.events/Create.xml</c> and dies with it,
    /// while a sentry belongs to the <em>player</em> and keeps firing while its
    /// engineer respawns. Projectiles already in the air also stay, and keep
    /// naming this slot — their <c>ownerPlayer</c> is a player and not a
    /// character, which is what lets a rocket still credit a kill to somebody
    /// who died before it landed.
    /// </para>
    /// </remarks>
    public void Clear(int slot)
    {
        if ((uint)slot >= MaxSlots)
            return;

        Characters[slot] = default;
        Weapons[slot] = default;
        Alive[slot] = false;
    }

    /// <summary>
    /// Removes a slot and shifts every slot above it down by one.
    /// </summary>
    /// <remarks>
    /// <b>Not the same operation as <see cref="Clear"/>, and the difference is a
    /// class of bug this project already has a warning about.</b> A slot index
    /// here <em>is</em> a roster index, and the roster compacts on
    /// <c>PLAYER_LEAVE</c> — both the reference and the client delete the entry
    /// and renumber everything above it (<c>ClientBeginStep.gml:212-221</c>). A
    /// character left in place while the roster shifted underneath it would
    /// belong to whoever moved into that index: the wrong player, only after
    /// somebody leaves, which is exactly the intermittent off-by-one CLAUDE.md
    /// warns about.
    /// <para>
    /// <b>Everything else that names a slot renumbers with it.</b> A projectile's
    /// owner, a character's last damage dealer, the player who set them on fire,
    /// the medic beaming them: each is a roster index and each shifts here. Miss
    /// one and a kill is credited to the wrong player some seconds after an
    /// unrelated disconnect — the same bug wearing a different hat.
    /// </para>
    /// <para>
    /// Slot 0 is the server's own reserved player and never leaves, so this is
    /// never called with it — but nothing here depends on that.
    /// </para>
    /// </remarks>
    public void RemoveSlot(int slot)
    {
        if ((uint)slot >= MaxSlots)
            return;

        for (var i = slot; i < MaxSlots - 1; i++)
        {
            Characters[i] = Characters[i + 1];
            Weapons[i] = Weapons[i + 1];
            Alive[i] = Alive[i + 1];
            Sentries[i] = Sentries[i + 1];
            HasSentry[i] = HasSentry[i + 1];
        }

        Characters[MaxSlots - 1] = default;
        Weapons[MaxSlots - 1] = default;
        Alive[MaxSlots - 1] = false;
        Sentries[MaxSlots - 1] = default;
        HasSentry[MaxSlots - 1] = false;

        RenumberReferences(slot);
    }

    /// <summary>
    /// Rewrites every stored roster index after the slot below it was removed.
    /// </summary>
    private void RenumberReferences(int removed)
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            if (Alive[i])
            {
                ref var character = ref Characters[i];
                character.LastDamageDealer = Renumber(character.LastDamageDealer, removed);
                character.SecondToLastDamageDealer = Renumber(character.SecondToLastDamageDealer, removed);
                character.BurnedBy = Renumber(character.BurnedBy, removed);
                character.Healer = Renumber(character.Healer, removed);
            }

            if (HasSentry[i])
                Sentries[i].LastDamageDealer = Renumber(Sentries[i].LastDamageDealer, removed);

            // A medic's heal target survives its owner's character, so it is
            // renumbered whether or not the slot is alive.
            Weapons[i].HealTarget = Renumber(Weapons[i].HealTarget, removed);
        }

        for (var i = 0; i < LiveProjectileCount; i++)
        {
            ref var projectile = ref Projectiles[ProjectileOrder[i]];
            projectile.OwnerSlot = Renumber(projectile.OwnerSlot, removed);
        }
    }

    /// <summary>
    /// One stored roster index after <paramref name="removed"/> left.
    /// </summary>
    /// <remarks>
    /// A reference to the departing slot itself becomes "nobody" rather than
    /// following the compaction: the player it named is gone, and letting it
    /// point at whoever slid down into their index is the off-by-one this whole
    /// path exists to avoid. 255 is left alone because it already means nobody.
    /// </remarks>
    private static byte Renumber(byte stored, int removed)
    {
        if (stored == CharacterState.Nobody)
            return CharacterState.Nobody;

        if (stored == removed)
            return CharacterState.Nobody;

        return stored > removed ? (byte)(stored - 1) : stored;
    }

    /// <summary>
    /// Puts a freshly spawned character in a slot, at an exact spawn point.
    /// </summary>
    /// <remarks>
    /// Everything here is <c>Character.events/Create.xml</c>'s own initial
    /// state: <c>hp = maxHp</c> (line 37), <c>moveStatus = 0</c> (line 166) and
    /// <c>_last_xscale = image_xscale</c> (line 120) with no assignment to
    /// <c>image_xscale</c> before it, so both facings start at GM8's default of
    /// +1. <c>instance_create</c> leaves both speeds at zero.
    /// <para>
    /// The position comes from the map rather than from anything rounded, which
    /// is the property docs/PARITY.md leans on: a spawn is the one moment a
    /// character's state is known exactly rather than to within a wire
    /// quantisation bin.
    /// </para>
    /// <para>
    /// The weapon is created here too, because the reference creates it in the
    /// same event — the last thing <c>Character.events/Create.xml</c> does
    /// before its cosmetics. A character with no weapon is not a state the
    /// reference can be in, and it would write the wrong bytes if it were.
    /// </para>
    /// </remarks>
    public bool Spawn(SpawnPoints spawnPoints, int slot, PlayerClass playerClass, Team team, int spawnpointId, int group)
    {
        ArgumentNullException.ThrowIfNull(spawnPoints);

        if ((uint)slot >= MaxSlots)
            return false;

        if (!spawnPoints.TryGet(team, group, spawnpointId, out var x, out var y))
            return false;

        Characters[slot] = new CharacterState
        {
            Class = playerClass,
            Team = team,
            X = x,
            Y = y,
            Hp = ClassStats.For(playerClass).MaxHp,
            Facing = 1,
            LastFacing = 1,

            // The sentinels, which are not zero. Create.xml sets
            // lastDamageDealer, secondToLastDamageDealer, burnedBy,
            // afterburnSource and healer to noone or -1; a zeroed struct would
            // have every one of them naming roster slot 0.
            LastDamageDealer = CharacterState.Nobody,
            SecondToLastDamageDealer = CharacterState.Nobody,
            BurnedBy = CharacterState.Nobody,
            AfterburnSource = DamageSource.None,
            LastDamageSource = DamageSource.None,
            Healer = CharacterState.Nobody,

            // Likewise the three that start at something other than zero:
            // cloakAlpha = 1 (fully visible), canGrabIntel = true, and an
            // engineer spawns with a full 100 nuts and bolts.
            CloakAlpha = 1,
            CanGrabIntel = true,
            NutsNBolts = 100,
        };

        // The weapon starts where instance_create put it: on the character's
        // own unrounded coordinates. End Step moves it to round(x) from this
        // tick onward — see WeaponState.X.
        Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        Alive[slot] = true;
        return true;
    }

    // --- projectiles ---------------------------------------------------------

    /// <summary>
    /// Adds a projectile to the pool and returns a handle to it.
    /// </summary>
    /// <remarks>
    /// The new projectile goes on the end of <see cref="ProjectileOrder"/>, so
    /// it is the last thing any creation-order walk reaches — which is what
    /// <c>instance_create</c> gives a GM8 instance.
    /// <para>
    /// Returns <see cref="ProjectileHandle.None"/> if the pool is full. That is
    /// the one failure mode the reference does not have; see
    /// <see cref="MaxProjectiles"/>. Callers should treat it as a bug in the
    /// capacity rather than as a condition to handle quietly.
    /// </para>
    /// </remarks>
    public ProjectileHandle SpawnProjectile(in ProjectileState projectile)
    {
        if (projectile.Kind == ProjectileKind.None)
            throw new ArgumentException("a projectile with no kind is a free pool slot", nameof(projectile));

        var slot = FindFreeProjectileSlot();
        if (slot < 0)
            return ProjectileHandle.None;

        Projectiles[slot] = projectile;

        // Bumped on spawn rather than on free, so the first generation ever
        // issued is 1 and ProjectileHandle.None's zero can never match a live
        // slot — which is what makes a defaulted handle field safe.
        ProjectileGenerations[slot]++;

        ProjectileOrder[LiveProjectileCount] = (ushort)slot;
        LiveProjectileCount++;

        return new ProjectileHandle(slot, ProjectileGenerations[slot]);
    }

    /// <summary>Removes a projectile: the reference's <c>instance_destroy</c>.</summary>
    /// <returns>Whether the handle named a live projectile.</returns>
    public bool FreeProjectile(ProjectileHandle handle)
    {
        if (!IsLive(handle))
            return false;

        Projectiles[handle.Index] = default;

        for (var i = 0; i < LiveProjectileCount; i++)
        {
            if (ProjectileOrder[i] != handle.Index)
                continue;

            // Shifted down rather than swapped with the last entry: a swap
            // would be O(1) and would scramble creation order, which is the
            // only reason this array exists.
            for (var j = i; j < LiveProjectileCount - 1; j++)
                ProjectileOrder[j] = ProjectileOrder[j + 1];

            LiveProjectileCount--;
            ProjectileOrder[LiveProjectileCount] = 0;
            return true;
        }

        return false;
    }

    /// <summary>Whether a handle still names the projectile it was issued for.</summary>
    public readonly bool IsLive(ProjectileHandle handle)
    {
        if (handle.IsNone || (uint)handle.Index >= MaxProjectiles)
            return false;

        return Projectiles[handle.Index].Kind != ProjectileKind.None
               && ProjectileGenerations[handle.Index] == handle.Generation;
    }

    /// <summary>Reads a projectile through a handle, refusing a stale one.</summary>
    public readonly bool TryGetProjectile(ProjectileHandle handle, out ProjectileState projectile)
    {
        if (!IsLive(handle))
        {
            projectile = default;
            return false;
        }

        projectile = Projectiles[handle.Index];
        return true;
    }

    /// <summary>
    /// The pool slot of the <paramref name="index"/>th live projectile in
    /// creation order.
    /// </summary>
    /// <remarks>
    /// Returns the slot rather than the projectile so a caller can take a
    /// <c>ref</c> into <see cref="Projectiles"/> and mutate in place, the way
    /// <see cref="MatchSimulation.Tick"/> already does with
    /// <see cref="Characters"/>.
    /// </remarks>
    public readonly int LiveProjectileSlot(int index)
    {
        if ((uint)index >= (uint)LiveProjectileCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "not a live projectile");

        return ProjectileOrder[index];
    }

    /// <summary>A handle to the <paramref name="index"/>th live projectile in creation order.</summary>
    public readonly ProjectileHandle LiveProjectileHandle(int index)
    {
        var slot = LiveProjectileSlot(index);
        return new ProjectileHandle(slot, ProjectileGenerations[slot]);
    }

    /// <summary>
    /// The lowest free pool slot, or -1.
    /// </summary>
    /// <remarks>
    /// Lowest-first rather than round-robin so that the choice is a function of
    /// the state alone: two worlds holding the same projectiles pick the same
    /// slot for the next one, which keeps a saved and restored world
    /// bit-identical (docs/BOTAI.md item 2). It costs a scan, and nothing
    /// observable depends on which slot is picked — creation order lives in
    /// <see cref="ProjectileOrder"/>, not in the slot numbers.
    /// </remarks>
    private readonly int FindFreeProjectileSlot()
    {
        if (LiveProjectileCount >= MaxProjectiles)
            return -1;

        for (var i = 0; i < MaxProjectiles; i++)
        {
            if (Projectiles[i].Kind == ProjectileKind.None)
                return i;
        }

        return -1;
    }
}
