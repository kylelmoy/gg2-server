using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>One entry in <c>global.players</c>.</summary>
/// <remarks>
/// A network client, a bot and the server's own reserved slot are all one of
/// these. Clients cannot tell them apart, which is exactly why the bot layer is
/// free to be rewritten (<c>Scripts/Bots/botAdd.gml</c>).
/// </remarks>
public sealed class RosterEntry
{
    public required string Name { get; set; }

    public Team Team { get; set; } = Team.Spectator;

    public PlayerClass Class { get; set; } = PlayerClass.Scout;

    /// <summary>The socket this player speaks through, or null for a bot or slot 0.</summary>
    public ClientConnection? Connection { get; init; }

    /// <summary>
    /// An id that does <b>not</b> move when the roster compacts.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Never on the wire, and never a substitute for the slot index.</b>
    /// Everything a client sees names a player by position, and that is not
    /// negotiable. This exists for the server's own side of a question that
    /// outlives a departure — "the vote running right now is about whom?" — where
    /// a slot index would silently retarget when somebody unrelated
    /// disconnects.
    /// <para>
    /// The reference's plugin API has exactly this, as <c>Player.gid</c>, which
    /// is some evidence it is the right shape rather than a convenience.
    /// </para>
    /// </remarks>
    public long StableId { get; internal set; }

    public bool IsBot { get; init; }

    /// <summary>
    /// Where this player's per-tick input comes from.
    /// </summary>
    /// <remarks>
    /// <b>The seam.</b> A socket-fed <see cref="NetworkInputSource"/>, a bot, or
    /// — for the reserved slot 0, which nobody plays — a source that never
    /// presses anything. <c>GameServerBeginStep.gml:79-85</c> chooses between
    /// <c>botInputUpdate(player)</c> and <c>processClientCommands(player, i)</c>
    /// at exactly this point in exactly this loop; here there is one call and
    /// the difference is which object is behind it.
    /// <para>
    /// May also implement <see cref="ICommandSource"/>, for the actions that are
    /// not keys, and <see cref="IBatchInputSource"/>, for a policy that would
    /// rather decide for all of its slots at once.
    /// </para>
    /// </remarks>
    public IInputSource Source { get; init; } = new NetworkInputSource();

    /// <summary>
    /// The client's <c>CLIENT_SETTINGS</c> queue-jump preference, echoed back in
    /// every <c>FULL_UPDATE</c>.
    /// </summary>
    public bool QueueJump { get; set; }

}

/// <summary>
/// <c>global.players</c>: a dense, positionally-indexed list.
/// </summary>
/// <remarks>
/// <b>Index 0 is reserved for the server's own player.</b>
/// <c>GameServerCreate.gml:36-40</c> creates it unconditionally, before any
/// client can connect, even in <c>-dedicated</c> mode. A headless port has no
/// local player to simulate but must still occupy the slot, or every index a
/// client receives is off by one.
/// <para>
/// Removal <b>compacts</b>: the client deletes the entry and decrements its own
/// <c>global.playerID</c> if the departing index was lower
/// (<c>ClientBeginStep.gml:212-221</c>). Indices are therefore not stable
/// across a leave, and this port renumbers identically by construction — the
/// index <em>is</em> the position.
/// </para>
/// </remarks>
public sealed class Roster
{
    private readonly List<RosterEntry> _entries = [];

    /// <summary>Never reused, so a departed player's id matches nobody.</summary>
    private long _nextStableId;

    /// <summary>
    /// Creates the roster with slot 0 filled by a permanent spectator standing
    /// in for the reference's local player.
    /// </summary>
    public Roster(string serverPlayerName)
    {
        _entries.Add(new RosterEntry { Name = serverPlayerName, Team = Team.Spectator, StableId = _nextStableId++ });
    }

    public int Count => _entries.Count;

    public RosterEntry this[int index] => _entries[index];

    public IReadOnlyList<RosterEntry> Entries => _entries;

    /// <summary>The reserved slot-0 entry. Never removed.</summary>
    public RosterEntry ServerPlayer => _entries[0];

    /// <summary>Appends an entry; its id is the index it lands at.</summary>
    public int Add(RosterEntry entry)
    {
        entry.StableId = _nextStableId++;
        _entries.Add(entry);
        return _entries.Count - 1;
    }

    /// <summary>The slot holding <paramref name="stableId"/>, or -1 if it left.</summary>
    public int SlotOfStableId(long stableId)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].StableId == stableId)
                return i;

        return -1;
    }

    /// <summary>Removes the entry at <paramref name="index"/>, compacting the list.</summary>
    public void RemoveAt(int index)
    {
        if (index == 0)
            throw new InvalidOperationException("slot 0 is reserved and cannot be removed");

        _entries.RemoveAt(index);
    }

    /// <summary>The index of <paramref name="entry"/>, or -1.</summary>
    public int IndexOf(RosterEntry entry) => _entries.IndexOf(entry);

    /// <summary>
    /// Roster entries that count against the player limit.
    /// </summary>
    /// <remarks>
    /// <c>getNumberOfOccupiedSlots</c> is the roster size <em>minus one in
    /// dedicated mode</em> (the host's own slot is not a seat anyone can take)
    /// and minus every bot, then plus each <c>JoiningPlayer</c> that has already
    /// reserved. This port is always dedicated, so slot 0 never counts — the
    /// caller adds the reserving joiners, which this type does not know about.
    /// </remarks>
    public int OccupiedSlots
    {
        get
        {
            var count = 0;
            for (var i = 1; i < _entries.Count; i++)
                if (!_entries[i].IsBot)
                    count++;
            return count;
        }
    }
}
