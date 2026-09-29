using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

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
    /// <b>Never on the wire, and never a substitute for the slot index.</b>
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
    /// Whether this bot was alive the last time the population manager looked —
    /// how it sees a death, to re-class the bot for its next life.
    /// </summary>
    /// <remarks>
    /// On the entry rather than in a slot-indexed array because the entry moves
    /// with the player when the roster compacts, and a slot would hand one bot's
    /// death to whoever slid into its place.
    /// </remarks>
    internal bool BotWasAlive { get; set; }

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
    /// <see cref="Environment.TickCount64"/> at this player's last accepted
    /// <c>PLAYER_CHANGENAME</c>, or null if they have never renamed.
    /// </summary>
    /// <remarks>
    /// <c>player.lastNamechange</c>, and <b>a MONOTONIC millisecond counter
    /// rather than a wall clock</b> — GM8's <c>current_time</c> is milliseconds
    /// since the program started. A <see cref="DateTime"/> here would be wrong in
    /// a way that never shows up in a test: an NTP correction or a DST step
    /// moving the clock backwards would stall every rename on the server until
    /// real time caught up again.
    /// <para>
    /// <b>Null rather than a sentinel</b>, which is <c>variable_local_exists</c>
    /// exactly: the reference's variable does not exist until the first rename,
    /// so that one is unconditional. A zero default would instead throttle the
    /// first rename of a server that had been up for less than a second, and
    /// subtracting from <see cref="long.MinValue"/> overflows.
    /// </para>
    /// </remarks>
    public long? LastNameChangeMs { get; set; }

    /// <summary>
    /// When this player's last relayed <c>CHAT_BUBBLE</c> went out, on the
    /// server's monotonic clock. Null until they have sent one.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="LastNameChangeMs"/> and for the same reason
    /// the reference gives that one: every accepted bubble is a broadcast to
    /// every connected client, so without a throttle one socket decides how much
    /// the server sends. See <c>GameServer.ExecuteChatBubble</c> — ⚠️ the
    /// reference throttles the rename and does NOT throttle this, so the
    /// departure is deliberate and is written down there.
    /// </remarks>
    public long? LastChatBubbleMs { get; set; }

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
    /// <b>Kept because <c>FULL_UPDATE</c> carries it</b> —
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

    /// <summary>Never reused, so a departed player's id matches nobody.</summary>
    private long _nextStableId;

    /// <summary>
    /// Bumped on every add and remove, so a caller can cache a view of the roster
    /// — <see cref="GameServer.BotSlots"/> — and know when it has gone stale.
    /// </summary>
    public int Version { get; private set; }

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
        Version++;
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
        Version++;
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
