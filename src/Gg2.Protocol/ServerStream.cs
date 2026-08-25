namespace Gg2.Protocol;

/// <summary>One framed server-to-client message.</summary>
/// <remarks>
/// Only the fields the message actually carries are populated. Anything the
/// stream reader does not decode into a named field is available raw in
/// <see cref="Payload"/>.
/// </remarks>
public sealed class ServerMessage
{
    public required MessageType Type { get; init; }

    /// <summary>Bytes after the message byte, before any decoding.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Set for the four state-update tiers.</summary>
    public StateUpdate? State { get; init; }

    /// <summary>The roster index this message names, where it names one.</summary>
    public byte? PlayerId { get; init; }

    /// <summary>A name or map name, where the message carries one.</summary>
    public string? Text { get; init; }

    public override string ToString() =>
        $"{Type}({Payload.Length} bytes)" + (PlayerId is { } id ? $" player {id}" : string.Empty);
}

/// <summary>
/// Frames a server's broadcast stream into messages, tracking the roster as it
/// goes.
/// </summary>
/// <remarks>
/// The stream is <b>not self-delimiting</b>. A state update's width depends on
/// the roster size, on the map, and on bytes inside the update itself, so
/// framing means decoding — and decoding means keeping the same roster the
/// client keeps. That is why this type is stateful and why it must be fed a
/// stream from its beginning.
/// <para>
/// The message widths are transcribed from <c>Scripts/Client/ClientBeginStep.gml</c>,
/// which is the only complete list: each <c>case</c>'s
/// <c>receiveCompleteMessage</c> is that message's payload size. Sizes inferred
/// from a message's name would be guesses, and docs/PROTOCOL.md says not to.
/// </para>
/// </remarks>
public sealed class ServerStream
{
    private readonly List<PlayerClass> _classes = [];
    private readonly List<string> _names = [];

    private WorldShape _shape;
    private bool _haveShape;

    /// <summary>Roster size as this reader believes it to be.</summary>
    public int PlayerCount => _names.Count;

    /// <summary>Roster names, in order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The map named by the most recent <c>CHANGE_MAP</c>.</summary>
    public string? CurrentMap { get; private set; }

    /// <summary>This reader's own roster index, from <c>JOIN_UPDATE</c>.</summary>
    public byte? OwnPlayerId { get; private set; }

    public byte CurrentMapArea { get; private set; } = 1;

    /// <summary>
    /// Tells the reader what the loaded map contributes to a state update's
    /// shape. Must be set before the first state update, and again after every
    /// <c>CHANGE_MAP</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately pushed in rather than derived: <c>Gg2.Protocol</c> has no
    /// map decoder and should not grow one. <c>Gg2.Maps.GameModes.ShapeOf</c>
    /// produces this.
    /// </remarks>
    public void SetShape(in WorldShape shape)
    {
        _shape = shape;
        _haveShape = true;
    }

    /// <summary>
    /// Frames the next message out of <paramref name="source"/>, consuming it
    /// only if it is complete. Returns false when more bytes are needed.
    /// </summary>
    public bool TryRead(ReceiveBuffer source, out ServerMessage message)
    {
        message = null!;

        if (source.IsEmpty)
            return false;

        var reader = new NetReader(source.Buffered);

        try
        {
            message = Read(ref reader);
        }
        catch (ProtocolException)
        {
            // Ran off the end: the message is not all here yet. Nothing was
            // consumed, so the next call sees the same bytes plus more.
            return false;
        }

        source.Consume(reader.Position);
        return true;
    }

    /// <summary>
    /// Frames one message. Throws <see cref="ProtocolException"/> if the buffer
    /// ends mid-message.
    /// </summary>
    public ServerMessage Read(ref NetReader reader)
    {
        var start = reader.Position;
        var type = reader.ReadMessageType();

        switch (type)
        {
            // --- state updates ------------------------------------------------
            case MessageType.InputState:
            case MessageType.QuickUpdate:
            case MessageType.FullUpdate:
            case MessageType.CapsUpdate:
            {
                if (!_haveShape)
                    throw new InvalidOperationException(
                        "SetShape() must be called before a state update can be framed");

                var state = StateUpdateReader.Read(ref reader, type, _shape, GetClasses());
                return Done(type, ref reader, start, state: state);
            }

            // --- the join burst -----------------------------------------------
            case MessageType.JoinUpdate:
                OwnPlayerId = reader.ReadUByte();
                CurrentMapArea = reader.ReadUByte();
                return Done(type, ref reader, start, playerId: OwnPlayerId);

            case MessageType.ChangeMap:
            {
                CurrentMap = reader.ReadString1();
                reader.ReadString1(); // md5; empty means built-in
                return Done(type, ref reader, start, text: CurrentMap);
            }

            // --- roster -------------------------------------------------------
            case MessageType.PlayerJoin:
            {
                var name = reader.ReadString1();
                _names.Add(name);
                _classes.Add(PlayerClass.Scout);
                return Done(type, ref reader, start, text: name);
            }

            case MessageType.PlayerLeave:
            {
                var id = reader.ReadUByte();

                // Both sides compact. An index above the departing one shifts
                // down by exactly one — get this wrong and every later message
                // names the wrong player, intermittently.
                if (id < _names.Count)
                {
                    _names.RemoveAt(id);
                    _classes.RemoveAt(id);
                }

                return Done(type, ref reader, start, playerId: id);
            }

            case MessageType.PlayerChangeClass:
            {
                var id = reader.ReadUByte();
                var playerClass = (PlayerClass)reader.ReadUByte();

                if (id < _classes.Count)
                    _classes[id] = playerClass;

                return Done(type, ref reader, start, playerId: id);
            }

            case MessageType.PlayerChangeTeam:
            case MessageType.ChatBubble:
            case MessageType.ClientSettings:
            {
                var id = reader.ReadUByte();
                reader.Skip(1);
                return Done(type, ref reader, start, playerId: id);
            }

            case MessageType.PlayerChangeName:
            {
                var id = reader.ReadUByte();
                var name = reader.ReadString1();

                if (id < _names.Count)
                    _names[id] = name;

                return Done(type, ref reader, start, playerId: id, text: name);
            }

            case MessageType.PlayerSpawn:
            {
                // Body is [player, spawnpointId, group] (sendEventSpawn.gml).
                // The id and group stay in Payload for the caller to resolve
                // against the map's spawn point lists; naming the player here
                // just matches every other message that names one.
                var id = reader.ReadUByte();
                reader.Skip(2);
                return Done(type, ref reader, start, playerId: id);
            }

            // --- fixed-width bodies -------------------------------------------
            // Sizes are the receiveCompleteMessage in each ClientBeginStep case.
            case MessageType.PlayerDeath:
            case MessageType.DestroySentry:
                return Fixed(type, ref reader, start, 4);

            case MessageType.Balance:
                // Two separate one-byte reads, not one two-byte read.
                return Fixed(type, ref reader, start, 2);

            case MessageType.BuildSentry:
                return Fixed(type, ref reader, start, 6);

            case MessageType.SentryPosition:
                return Fixed(type, ref reader, start, 5);

            case MessageType.WeaponFire:
                return Fixed(type, ref reader, start, 9);

            case MessageType.GrabIntel:
            case MessageType.ScoreIntel:
            case MessageType.DropIntel:
            case MessageType.ReturnIntel:
            case MessageType.GeneratorDestroy:
            case MessageType.UberCharged:
            case MessageType.Uber:
            case MessageType.Omnomnomnom:
            case MessageType.ToggleZoom:
            case MessageType.Kick:
            case MessageType.CpCaptured:
                return Fixed(type, ref reader, start, 1);

            case MessageType.RewardChallengeCode:
                return Fixed(type, ref reader, start, 16);

            // --- no body -------------------------------------------------------
            case MessageType.PasswordRequest:
            case MessageType.PasswordWrong:
            case MessageType.IncompatibleProtocol:
            case MessageType.ServerFull:
            case MessageType.ReserveSlot:
            case MessageType.ArenaWaitForPlayers:
            case MessageType.ArenaStartRound:
            case MessageType.ArenaEndRound:
            case MessageType.ArenaRestart:
            case MessageType.UnlockCp:
            case MessageType.RedTeamCap:
            case MessageType.BlueTeamCap:
                return Fixed(type, ref reader, start, 0);

            // --- variable ------------------------------------------------------
            case MessageType.MessageString:
                return Done(type, ref reader, start, text: reader.ReadString1());

            case MessageType.MapEnd:
            {
                var next = reader.ReadString1();
                reader.Skip(2); // winners, currentMapArea
                return Done(type, ref reader, start, text: next);
            }

            case MessageType.RewardUpdate:
            {
                var id = reader.ReadUByte();
                reader.ReadString2();
                return Done(type, ref reader, start, playerId: id);
            }

            case MessageType.PluginPacket:
            {
                var length = reader.ReadUShort();
                reader.Skip(length);
                return Done(type, ref reader, start);
            }

            default:
                // The client's own answer to this is promptRestartOrQuit("The
                // Server sent unexpected data."). Nothing sensible can be done
                // with the rest of the stream, so say which byte broke it.
                throw new ProtocolException(
                    $"no framing for server message {(byte)type} at offset {start}");
        }
    }

    private ReadOnlySpan<PlayerClass> GetClasses() => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_classes);

    private static ServerMessage Fixed(MessageType type, ref NetReader reader, int start, int payloadLength)
    {
        reader.Skip(payloadLength);
        return Done(type, ref reader, start);
    }

    private static ServerMessage Done(
        MessageType type,
        ref NetReader reader,
        int start,
        StateUpdate? state = null,
        byte? playerId = null,
        string? text = null)
    {
        // Re-sliced from the message's own extent rather than accumulated, so a
        // decoder that reads a field in pieces still reports the whole body.
        // Allocates: this is the harness, not the tick loop.
        var length = reader.Position - start - 1;

        return new ServerMessage
        {
            Type = type,
            Payload = reader.Window(start + 1, length).ToArray(),
            State = state,
            PlayerId = playerId,
            Text = text,
        };
    }
}
