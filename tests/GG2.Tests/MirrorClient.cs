using GG2.Maps;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// A client that keeps its own roster from the broadcast, so a test can ask what
/// a client was <em>told</em> rather than what the server believes.
/// </summary>
/// <remarks>
/// <b>The gate <c>docs/PARITY.md</c> asks for.</b> Every other automated check
/// in this project reads the server — the replayer compares the port against a
/// recording, the scenario suite reads <c>WorldState</c>, the server tests assert
/// on a <c>GameServer</c>'s own roster. None of them can see a bug that lives
/// entirely in the difference between the two.
/// <para>
/// <b>It must derive its roster from the RULES A CLIENT FOLLOWS, never from
/// the server's state.</b> A mirror that copied the server would agree with it by
/// construction and gate nothing. So the state here comes out of
/// <see cref="ServerStream"/>, which is transcribed from
/// <c>Scripts/Client/ClientBeginStep.gml</c> — including the rule that makes
/// rotations dangerous: a <c>CHANGE_MAP</c> on area 1 sets every player to
/// spectator, client-side, with nothing on the wire saying so.
/// </para>
/// <para>
/// <b>It is not a stock client and does not pretend to be.</b> It reads the
/// roster stream and the state updates' framing; it does not simulate physics or
/// render. What it can catch is disagreement about <em>who is who</em> — team,
/// class, name, roster size — which is the category, not every possible desync.
/// </para>
/// </remarks>
public sealed class MirrorClient : IDisposable
{
    private readonly ReceiveBuffer _buffer = new();
    private readonly ServerStream _stream = new();
    private readonly List<ServerMessage> _messages = [];
    private readonly List<string> _impossible = [];
    private readonly Func<string, LevelMap> _maps;


    public MirrorClient(LoopbackClient socket, Func<string, LevelMap> maps, string firstMap)
    {
        Socket = socket;
        _maps = maps;

        // The greeting names the map before any state update arrives, and a
        // state update cannot be framed without its shape.
        Reshape(firstMap);
    }

    public LoopbackClient Socket { get; }

    /// <summary>The roster this client believes in: one team per slot.</summary>
    public IReadOnlyList<Team> Teams => _stream.Teams;

    /// <summary>The names this client believes in.</summary>
    public IReadOnlyList<string> Names => _stream.Names;

    public int PlayerCount => _stream.PlayerCount;

    public string? CurrentMap => _stream.CurrentMap;

    /// <summary>
    /// The roster index this client believes it occupies — the stock client's
    /// <c>global.playerID</c>, which moves down when a lower slot leaves.
    /// </summary>
    public byte? OwnPlayerId => _stream.OwnPlayerId;

    /// <summary>
    /// Whether this client ever worked out which player it is — the stock
    /// client's <c>global.myself</c>. See <see cref="ServerStream.SelfBound"/>
    /// for the one frame in which false is legitimate.
    /// </summary>
    public bool SelfBound => _stream.SelfBound;

    /// <summary>
    /// The map area this client believes it is playing, 1-based.
    /// </summary>
    /// <remarks>
    /// From <c>MAP_END</c>, which is the only message that ever states it after a
    /// join — <c>CHANGE_MAP</c> carries the map name alone. It decides both how
    /// the reader narrows entities and whether a <c>CHANGE_MAP</c> wipes teams.
    /// </remarks>
    public int CurrentMapArea => _stream.CurrentMapArea;

    /// <summary>Every message framed so far.</summary>
    public IReadOnlyList<ServerMessage> Messages => _messages;

    /// <summary>
    /// Frames everything that has arrived since the last call.
    /// </summary>
    /// <remarks>
    /// <b>Reshapes on <c>CHANGE_MAP</c> before framing anything after it.</b>
    /// A state update's width depends on the map, so a reader that kept the old
    /// shape across a rotation would desynchronise on the first update of the new
    /// map and every later assertion would be about garbage. That is a property
    /// of the wire, not of this helper — a real client reloads the room for the
    /// same reason.
    /// </remarks>
    public void Pump()
    {
        Socket.Poll();

        // DRAINS the socket rather than indexing into it. LoopbackClient.Take
        // REMOVES bytes from its own list, so a mirror that remembered "I have
        // read the first N" would silently skip or re-read the moment any test
        // also took bytes from the same client — a harness that mis-reads
        // quietly is worse than no harness, because it still goes green.
        //
        // The consequence is a rule: a LoopbackClient with a mirror on it
        // belongs to the mirror. Read messages through Messages/Of, never
        // through Socket.Take.
        if (Socket.Received.Count > 0)
            _buffer.Append(Socket.Take(Socket.Received.Count));

        while (_stream.TryRead(_buffer, out var message))
        {
            _messages.Add(message);

            if (message.Type == MessageType.ChangeMap && message.Text is { } map)
                Reshape(map);

            CheckSpawnable(message);
        }
    }

    /// <summary>
    /// Every slot the server has given a character to while this client believes
    /// they are a spectator.
    /// </summary>
    /// <remarks>
    /// <b>This is the crash, exactly.</b> A real GM8 client that reads a state
    /// update with the character bit set for a player it holds as
    /// <see cref="Team.Spectator"/> runs <c>instance_create(getCharacterObject
    /// (class))</c> and dies in <c>Character</c>'s Create with
    /// <c>Attempted to get a sprite for unknown team ID: 2</c> — the sprite
    /// lookup is by team and there is no spectator sprite.
    /// <para>
    /// <b>Sharper than comparing rosters</b>, and the difference matters: the
    /// two rosters can agree that somebody is a spectator and the server can
    /// still be sending a character for them. Team equality would call that
    /// healthy. This asks the question the client's own code asks.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Impossible => _impossible;

    private void CheckSpawnable(ServerMessage message)
    {
        if (message.State is not { } state)
            return;

        // deserializeState.gml:11-13 — the client compares the update's declared
        // player count against its OWN roster and shows "Wrong number of players
        // while deserializing state" when they differ.
        //
        // ⛔ THE PORT'S READER DOES NOT MAKE THAT COMPARISON. StateUpdateReader
        // takes the count off the wire and reads exactly that many blocks, so a
        // disagreement is invisible to it — and therefore invisible to every
        // test that reads through this mirror. That is the blind spot
        // docs/PARITY.md describes, and it sits on a live bug: the stock client
        // only *warns* here and then keeps deserialising, now misaligned, until
        // a character bit lands on a slot it holds as a spectator and
        // Character's Create event kills it with "unknown team ID: 2".
        //
        // Both 2026-09-03 client deaths were preceded by exactly this dialog, so
        // the count mismatch is the cause and the sprite error is the symptom.
        if (state.PlayerCount != _stream.PlayerCount)
        {
            _impossible.Add(
                $"{message.Type}: declares {state.PlayerCount} players, "
                + $"this client holds {_stream.PlayerCount}");
        }

        // ClientBeginStep.gml:206-208 — the client binds global.myself on the one
        // PLAYER_JOIN that lands at its own id, and never retries. Unbound is
        // legitimate for the single frame between a joiner's burst and the
        // broadcast that carries its own PLAYER_JOIN, so the question is asked
        // only once the roster has grown past that id: at that point every stock
        // client either knows which player it is or never will.
        //
        // Live on 2026-09-05 this is Spectator's End Step and AmmoCounter's Draw
        // failing on `global.myself.object` with "Unknown variable object" every
        // frame — the same -1 ClientCreate.gml:20 set.
        if (_stream.OwnPlayerId is { } own && !_stream.SelfBound && _stream.PlayerCount > own)
        {
            _impossible.Add(
                $"{message.Type}: this client holds {_stream.PlayerCount} players and was "
                + $"told it is player {own}, but no PLAYER_JOIN ever landed there");
        }

        if (_stream.SelfDestroyed)
            _impossible.Add($"{message.Type}: this client was sent its own PLAYER_LEAVE");

        for (var slot = 0; slot < state.Players.Length; slot++)
        {
            if (state.Players[slot].Character is null)
                continue;

            var team = slot < _stream.Teams.Count ? _stream.Teams[slot] : Team.Spectator;

            if (team is Team.Red or Team.Blue)
                continue;

            _impossible.Add(
                $"{message.Type}: slot {slot} has a character, and this client has them as {team}");
        }
    }

    /// <summary>Messages of one type, in arrival order.</summary>
    public IReadOnlyList<ServerMessage> Of(MessageType type) =>
        [.. _messages.Where(m => m.Type == type)];

    /// <summary>Forgets the messages seen so far, keeping the roster.</summary>
    /// <remarks>
    /// The roster is the point and must survive; the message log is a convenience
    /// for asserting on what arrived in a window.
    /// </remarks>
    public void ClearLog()
    {
        _messages.Clear();
        _impossible.Clear();
    }

    private void Reshape(string map) =>
        _stream.SetShape(GameModes.ShapeOf(_maps(map).ForArea(_stream.CurrentMapArea)));

    public void Dispose() => Socket.Dispose();
}
