using GG2.Protocol;
using GG2.Server;
using GG2.Server.Plugins;

namespace GG2.Tests;

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
/// performed. The real ones are <see cref="GG2.Server.GameServer"/>'s and are
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

        public bool Alive { get; set; } = true;
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

    /// <summary>Every line handed to the panel's chat history, in order.</summary>
    public List<(int Slot, string Message, bool TeamOnly)> Heard { get; } = [];

    public void ChatHeard(int slot, string message, bool teamOnly) => Heard.Add((slot, message, teamOnly));

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

    // --- the bot suspension and the join line --------------------------------
    //
    // Modelled rather than stubbed, for the same reason RemoveAt compacts: what a
    // /bots test can get wrong is answering a player about a state the server is
    // not actually in, so the state lives here and the two calls move it.

    /// <summary><c>[Server] ChatJoinMessage</c>.</summary>
    public string ChatJoinMessage { get; set; } = string.Empty;

    /// <summary><c>[Plugins] chat_*</c>.</summary>
    public ChatSettings ChatSettings { get; set; } = new();

    /// <summary><c>[Bots] SuspendMinutes</c>. Zero, the default, is "no such command".</summary>
    public int SuspendMinutes { get; set; }

    /// <summary>Whether the population manager is running at all.</summary>
    public bool BotsManaged { get; set; }

    /// <summary>How many roster entries a <c>/bots</c> reply should call bots.</summary>
    public int BotCount { get; set; }

    /// <summary>What is left of a suspension, in seconds.</summary>
    public int BotSuspendSecondsLeft { get; set; }

    /// <summary>Every accepted <see cref="SuspendBots"/>, in minutes.</summary>
    public List<int> Suspensions { get; } = [];

    /// <summary>How many times a suspension was ended early.</summary>
    public int Resumes { get; private set; }

    public BotStatus Bots => new(
        CommandAllowed: SuspendMinutes > 0,
        SuspendMinutes: SuspendMinutes,
        Managed: BotsManaged,
        Count: BotCount,
        SecondsLeft: BotSuspendSecondsLeft);

    public bool SuspendBots()
    {
        if (SuspendMinutes <= 0)
            return false;

        Suspensions.Add(SuspendMinutes);
        BotSuspendSecondsLeft = SuspendMinutes * 60;

        // The real server removes them, so the fake does too: a command that
        // reported success while the count stayed put is the bug under test.
        BotCount = 0;
        return true;
    }

    public bool ResumeBots()
    {
        if (BotSuspendSecondsLeft == 0)
            return false;

        BotSuspendSecondsLeft = 0;
        Resumes++;
        return true;
    }

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

    /// <summary>Everyone is alive unless <see cref="SetAlive"/> says otherwise.</summary>
    public bool IsAlive(int slot) => _players[slot].Alive;

    public void SetAlive(int slot, bool alive) => _players[slot].Alive = alive;

    /// <summary><c>global.mapchanging</c>. Set by a test; <see cref="EndRound"/> only records.</summary>
    public bool RoundOver { get; set; }

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
                messages.Add(GG2Text.Decode(data.AsSpan(1, data[0])));

        return messages;
    }

    /// <summary>Everything sent to anybody, in order, deduplicated by content.</summary>
    public List<string> AllMessages()
    {
        var messages = new List<string>();
        foreach (var (_, data) in Sent)
        {
            var text = GG2Text.Decode(data.AsSpan(1, data[0]));
            if (messages.Count == 0 || messages[^1] != text)
                messages.Add(text);
        }

        return messages;
    }
}
