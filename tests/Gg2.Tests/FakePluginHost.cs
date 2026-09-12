using Gg2.Protocol;
using Gg2.Server.Plugins;

namespace Gg2.Tests;

/// <summary>
/// An <see cref="IPluginHost"/> with a roster and nothing else, for testing a
/// plugin's decisions without a socket.
/// </summary>
/// <remarks>
/// <b>It reproduces the two host behaviours a plugin can get wrong</b> rather
/// than being a bag of stubs: adding a player appends and hands out a fresh
/// stable id, and <see cref="RemoveAt"/> COMPACTS — so a plugin that keeps state
/// by slot index fails here the same way it would fail in a game.
/// <para>
/// Kicks, team changes, round ends and map changes are recorded rather than
/// performed. The real ones are <see cref="Gg2.Server.GameServer"/>'s and are
/// covered where they live; what these assert is that the plugin asked for the
/// right thing about the right player.
/// </para>
/// </remarks>
public sealed class FakePluginHost : IPluginHost
{
    private sealed record Player(long StableId, string Name, Team Team, string Address, bool Client)
    {
        public string Name { get; set; } = Name;

        public Team Team { get; set; } = Team;

        public int Points { get; set; }
    }

    private readonly List<Player> _players = [];
    /// <summary>
    /// Deliberately NOT starting at zero. A stable id that happened to equal the
    /// slot it was handed out at would let a plugin keeping slot indices pass
    /// every compaction test by coincidence.
    /// </summary>
    private long _nextStableId = 1000;

    /// <summary>Every <c>PluginPacketSendTo</c>, in order.</summary>
    public List<(int Slot, byte[] Data)> Sent { get; } = [];

    /// <summary>Every <c>PluginPacketSend</c>, in order.</summary>
    public List<byte[]> Broadcast { get; } = [];

    public List<string> Logged { get; } = [];

    public List<(int Slot, string Name)> Renames { get; } = [];

    public List<(string Name, string Reason)> Kicked { get; } = [];

    public List<(string Name, Team Team)> TeamChanges { get; } = [];

    public int RoundEnds { get; private set; }

    public List<string> NextMaps { get; } = [];

    public long CurrentTimeMilliseconds { get; set; }

    public int PlayerCount => _players.Count;

    public IReadOnlyList<string> VotableMaps { get; set; } = [];

    public IReadOnlyList<string> MapRotation { get; set; } = [];

    public int CurrentMapIndex { get; set; }

    /// <summary>Seeded, so a shuffle or a tie-break is the same on every run.</summary>
    public Random Random { get; } = new(1234);

    public int Add(string name, Team team, string address = "10.0.0.1", bool client = true)
    {
        _players.Add(new Player(_nextStableId++, name, team, address, client));
        return _players.Count - 1;
    }

    /// <summary>Removes a slot and compacts, exactly as the roster does.</summary>
    public void RemoveAt(int slot) => _players.RemoveAt(slot);

    public string NameOf(int slot) => _players[slot].Name;

    public Team TeamOf(int slot) => _players[slot].Team;

    public string RemoteAddressOf(int slot) => _players[slot].Client ? _players[slot].Address : string.Empty;

    public bool IsClient(int slot) => _players[slot].Client;

    public long StableIdOf(int slot) => _players[slot].StableId;

    public int PointsOf(int slot) => _players[slot].Points;

    /// <summary>Gives a player a score, for the player picker's bracketed number.</summary>
    public void SetPoints(int slot, int points) => _players[slot].Points = points;

    public int SlotOfStableId(long stableId)
    {
        for (var i = 0; i < _players.Count; i++)
            if (_players[i].StableId == stableId)
                return i;

        return -1;
    }

    public void Rename(int slot, string name)
    {
        _players[slot].Name = name;
        Renames.Add((slot, name));
    }

    public bool Kick(int slot, string reason)
    {
        if (!_players[slot].Client)
            return false;

        Kicked.Add((_players[slot].Name, reason));
        return true;
    }

    public void SetTeam(int slot, Team team)
    {
        if (_players[slot].Team == team)
            return;

        _players[slot].Team = team;
        TeamChanges.Add((_players[slot].Name, team));
    }

    public bool EndRound()
    {
        RoundEnds++;
        return true;
    }

    public bool SetNextMap(string map)
    {
        if (!VotableMaps.Contains(map))
            return false;

        NextMaps.Add(map);
        return true;
    }

    public void SendPluginPacketToAll(int packetId, ReadOnlySpan<byte> data) =>
        Broadcast.Add(data.ToArray());

    public void SendPluginPacketTo(int slot, int packetId, ReadOnlySpan<byte> data) =>
        Sent.Add((slot, data.ToArray()));

    public void Log(string message) => Logged.Add(message);

    /// <summary>The messages sent to one slot, decoded from their length prefixes.</summary>
    public List<string> MessagesTo(int slot)
    {
        var messages = new List<string>();
        foreach (var (target, data) in Sent)
            if (target == slot)
                messages.Add(Gg2Text.Decode(data.AsSpan(1, data[0])));

        return messages;
    }

    /// <summary>Everything sent to anybody, in order, deduplicated by content.</summary>
    public List<string> AllMessages()
    {
        var messages = new List<string>();
        foreach (var (_, data) in Sent)
        {
            var text = Gg2Text.Decode(data.AsSpan(1, data[0]));
            if (messages.Count == 0 || messages[^1] != text)
                messages.Add(text);
        }

        return messages;
    }
}
