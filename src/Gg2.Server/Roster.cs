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

    /// <summary>
    /// When this player's last accepted <c>PLAYER_CHANGENAME</c> was processed.
    /// </summary>
    /// <remarks>
    /// <c>player.lastNamechange</c>. ⚠️ <b><see cref="DateTime.MinValue"/> is the
    /// right default</b>, not "now": the reference's field does not exist until
    /// the first rename, and <c>variable_local_exists</c> makes that first one
    /// unconditional. Defaulting to the join instant would silently swallow a
    /// rename sent in the first second after connecting, which is exactly when
    /// a client sends one.
    /// </remarks>
    public DateTime LastNameChange { get; set; } = DateTime.MinValue;

    /// <summary>
    /// The user id this client asked to authenticate as, or null if it never
    /// sent a <c>REWARD_REQUEST</c>.
    /// </summary>
    /// <remarks>
    /// <c>player.rewardId</c>. Untrusted: it is whatever the client typed into
    /// its own ini, and it is not this server's job to check it — the auth
    /// server is the only party that can.
    /// </remarks>
    public string? RewardId { get; set; }

    /// <summary>
    /// The 16-byte challenge sent to this client, or null if none is
    /// outstanding.
    /// </summary>
    /// <remarks>
    /// <c>player.challenge</c>. Per connection rather than per server: two
    /// clients must not be handed the same nonce, or one could replay the
    /// other's answer.
    /// </remarks>
    public byte[]? RewardChallenge { get; set; }

    /// <summary>
    /// Whether this player has already had an auth check started for them.
    /// </summary>
    /// <remarks>
    /// <c>player.rewardAuthAlreadyStarted</c>, and it is one-way for the life of
    /// the connection: without it a client could make the server open an
    /// outbound connection per packet it feels like sending, which is the whole
    /// point of the reference's guard.
    /// </remarks>
    public bool RewardAuthStarted { get; set; }

    /// <summary>
    /// The reward string this player authenticated for, or empty.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Kept because <c>FULL_UPDATE</c> carries it</b> —
    /// <c>Player.events/User Event 12</c> writes <c>unparseRewards(rewards)</c>
    /// per player — so a client joining after somebody authenticated still draws
    /// their badges. Without it the <c>REWARD_UPDATE</c> broadcast would reach
    /// only whoever was already connected, and cosmetics would depend on join
    /// order.
    /// <para>
    /// Held as the string the auth server sent rather than as a parsed set. The
    /// reference round-trips it through a <c>ds_map</c>, so its re-emitted order
    /// need not match what it received; the value is a set either way and this
    /// server has no use for the members.
    /// </para>
    /// </remarks>
    public string Rewards { get; set; } = string.Empty;
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

    /// <summary>
    /// Creates the roster with slot 0 filled by a permanent spectator standing
    /// in for the reference's local player.
    /// </summary>
    public Roster(string serverPlayerName)
    {
        _entries.Add(new RosterEntry { Name = serverPlayerName, Team = Team.Spectator });
    }

    public int Count => _entries.Count;

    public RosterEntry this[int index] => _entries[index];

    public IReadOnlyList<RosterEntry> Entries => _entries;

    /// <summary>The reserved slot-0 entry. Never removed.</summary>
    public RosterEntry ServerPlayer => _entries[0];

    /// <summary>Appends an entry; its id is the index it lands at.</summary>
    public int Add(RosterEntry entry)
    {
        _entries.Add(entry);
        return _entries.Count - 1;
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
