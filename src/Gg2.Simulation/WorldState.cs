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
/// <b>Not the whole match yet.</b> This is characters, which is what phase 4
/// simulates. Objectives, scores and the HUD still live in
/// <c>Gg2.Server.MatchState</c>; they belong here as the phases that own them
/// land, and <c>MatchState</c> should shrink as they do rather than the other
/// way round.
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

    /// <summary>Each slot's character. Only meaningful where <see cref="Alive"/> is set.</summary>
    public SlotBuffer<CharacterState> Characters;

    /// <summary>Whether a slot has a character at all — spectators and the dead do not.</summary>
    public SlotBuffer<bool> Alive;

    /// <summary>Whether the given slot currently has a character.</summary>
    public readonly bool IsAlive(int slot) => (uint)slot < MaxSlots && Alive[slot];

    /// <summary>Removes a slot's character: it died, left, or went spectator.</summary>
    /// <remarks>
    /// The character is cleared as well as flagged, so a stale body cannot leak
    /// into the next thing that spawns in this slot and so two states that look
    /// the same really are the same bytes — which is what makes docs/BOTAI.md
    /// item 2's "restore to a bit-identical state" checkable.
    /// </remarks>
    public void Clear(int slot)
    {
        if ((uint)slot >= MaxSlots)
            return;

        Characters[slot] = default;
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
            Alive[i] = Alive[i + 1];
        }

        Characters[MaxSlots - 1] = default;
        Alive[MaxSlots - 1] = false;
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
        };

        Alive[slot] = true;
        return true;
    }
}
