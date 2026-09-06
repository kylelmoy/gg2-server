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
    private readonly List<Team> _teams = [];

    /// <summary>
    /// The <c>BALANCE</c> id that means "no player": the message is the notice
    /// rather than a switch, and carries nothing after it.
    /// </summary>
    /// <remarks>
    /// 255 and not 0, like every other "nobody" on this wire — roster slot 0 is
    /// the server's own player and always exists.
    /// </remarks>
    public const byte BalanceNotice = 255;

    private WorldShape _shape;
    private bool _haveShape;

    /// <summary>Roster size as this reader believes it to be.</summary>
    public int PlayerCount => _names.Count;

    /// <summary>Roster names, in order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>
    /// Roster teams, in order, as a CLIENT would believe them.
    /// </summary>
    /// <remarks>
    /// <b>This is the field that makes a client-disagreement bug visible</b>: if
    /// <c>PLAYER_CHANGETEAM</c> read the id and skipped the team byte instead of
    /// tracking it, nothing would notice the server failing to tell a client its
    /// team. <c>docs/PARITY.md</c>, "nothing compares what a client was TOLD".
    /// <para>
    /// <b>Maintained by the reference's rules, not by mirroring the server</b>
    /// — including the one that makes rotations dangerous: <c>CHANGE_MAP</c> on
    /// area 1 sets <em>every</em> player to <see cref="Team.Spectator"/>
    /// (<c>ClientBeginStep.gml:514-544</c>), so a server that re-teams anybody
    /// afterwards and does not say so leaves this reader — and every real client
    /// — believing they are a spectator.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Team> Teams => _teams;

    /// <summary>The map named by the most recent <c>CHANGE_MAP</c>.</summary>
    public string? CurrentMap { get; private set; }

    /// <summary>
    /// This reader's own roster index — the client's <c>global.playerID</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is not fixed at <c>JOIN_UPDATE</c>.</b> Both sides compact on a
    /// departure, so a <c>PLAYER_LEAVE</c> naming a slot <em>below</em> this one
    /// moves it down: <c>ClientBeginStep.gml:218-220</c> is
    /// <c>if(playerID &lt; global.playerID) global.playerID -= 1</c>. Holding the
    /// joined-at value would make this reader name a different player from every
    /// stock client the moment anybody underneath it left.
    /// </remarks>
    public byte? OwnPlayerId { get; private set; }

    /// <summary>
    /// Whether a <c>PLAYER_JOIN</c> has landed on <see cref="OwnPlayerId"/> —
    /// the client's <c>global.myself</c> being bound.
    /// </summary>
    /// <remarks>
    /// <c>ClientBeginStep.gml:206-208</c> binds <c>global.myself</c> on exactly
    /// one condition: the <c>PLAYER_JOIN</c> being processed lands at
    /// <c>ds_list_size(global.players)-1 == global.playerID</c>. It is not
    /// retried, so a client whose roster is out of step at that moment leaves
    /// <c>global.myself</c> at the <c>-1</c> <c>ClientCreate.gml:20</c> gave it
    /// <b>for the rest of the session</b>.
    /// <para>
    /// ⚠️ <b>It is legitimately false for one frame at every join</b>, on this
    /// port and on the reference alike: the burst is written on the joining
    /// socket while the joiner's own <c>PLAYER_JOIN</c> goes into the shared
    /// broadcast, which is flushed at the TOP of the next tick
    /// (<c>GameServerEndStep.gml:7-10</c> flushes, <c>:18-20</c> then services
    /// joins). <see cref="PlayerCount"/> is what distinguishes the two: a roster
    /// that has grown past <see cref="OwnPlayerId"/> and still has this false is
    /// a client that will never know which player it is.
    /// docs/TRAPS-LIVEGAME.md, "the frame a joining client does not know itself".
    /// </para>
    /// </remarks>
    public bool SelfBound { get; private set; }

    /// <summary>
    /// Whether a <c>PLAYER_LEAVE</c> has named this reader's own slot after
    /// <see cref="SelfBound"/>.
    /// </summary>
    /// <remarks>
    /// The stock client does not special-case it: <c>removePlayer</c> destroys
    /// the instance <c>global.myself</c> points at and leaves the global pointing
    /// at the corpse, so every later <c>global.myself.object</c> is a GM8 error.
    /// A server has no reason to send a client its own departure, which is
    /// exactly why nothing would notice if one started to.
    /// </remarks>
    public bool SelfDestroyed { get; private set; }

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

                // ClientBeginStep.gml:514-544 — on area 1 the client wipes
                // every player's stats and sets every team to TEAM_SPECTATOR,
                // itself, without being told to. Reproduced here because a
                // reader that did not would disagree with every real client
                // about the one moment this is hardest to get right.
                if (CurrentMapArea == 1)
                    for (var i = 0; i < _teams.Count; i++)
                        _teams[i] = Team.Spectator;

                return Done(type, ref reader, start, text: CurrentMap);
            }

            // --- roster -------------------------------------------------------
            case MessageType.PlayerJoin:
            {
                var name = reader.ReadString1();
                _names.Add(name);
                _classes.Add(PlayerClass.Scout);

                // ClientBeginStep.gml:206-208. The bind is tested once, on the
                // join that happens to land at this index, and never again.
                if (_names.Count - 1 == OwnPlayerId)
                    SelfBound = true;

                // ServerPlayerJoin carries only the name. A joining player is a
                // spectator until a PLAYER_CHANGETEAM says otherwise, which is
                // what Player.events/Create leaves them as.
                _teams.Add(Team.Spectator);

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
                    _teams.RemoveAt(id);
                }

                // ClientBeginStep.gml:218-220 — and it is the client's OWN index
                // that moves, not just the roster. Strictly below: a leave naming
                // this reader destroys its player rather than renumbering it.
                if (OwnPlayerId is { } own)
                {
                    if (id < own)
                        OwnPlayerId = (byte)(own - 1);
                    else if (id == own && SelfBound)
                        SelfDestroyed = true;
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
            {
                var id = reader.ReadUByte();
                var team = (Team)reader.ReadUByte();

                if (id < _teams.Count)
                    _teams[id] = team;

                return Done(type, ref reader, start, playerId: id);
            }

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
            {
                // NOT a fixed-width message. There are two shapes:
                //
                //   BALANCE, 255                     the "teams are uneven" notice
                //   BALANCE, playerId, class         somebody was actually moved
                //
                // The client reads one byte and only then decides whether to
                // read a second (`ClientBeginStep.gml:244-253`), so a reader
                // that always takes two is one byte ahead from the first notice
                // onwards.
                var who = reader.ReadUByte();

                if (who != BalanceNotice)
                    reader.Skip(1);

                return Done(type, ref reader, start, playerId: who);
            }

            case MessageType.BuildSentry:
                return Fixed(type, ref reader, start, 6);

            case MessageType.SentryPosition:
                return Fixed(type, ref reader, start, 5);

            case MessageType.WeaponFire:
            {
                // Body is [player, x, y, hspeed, vspeed, seed]
                // (sendEventFireWeapon.gml) — nine bytes. The position and the
                // seed stay in Payload for the caller; naming the player here
                // matches every other message that names one.
                var id = reader.ReadUByte();
                reader.Skip(8);
                return Done(type, ref reader, start, playerId: id);
            }

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
            case MessageType.ArenaRestart:
            case MessageType.UnlockCp:
            case MessageType.RedTeamCap:
            case MessageType.BlueTeamCap:
                return Fixed(type, ref reader, start, 0);

            // --- variable ------------------------------------------------------
            case MessageType.ArenaEndRound:
            {
                // NOT a bodyless message. `serverArenaEndRound.gml` writes five
                // bytes — the winning team, the two MVP counts, and both round
                // scores — and then five more per MVP, up to three a side:
                //
                //   ubyte  win
                //   ubyte  mvpsRed, ubyte mvpsBlue
                //   ubyte  redWins, ubyte blueWins
                //   then mvpsRed + mvpsBlue × {
                //       ubyte playerId, ubyte kills, ushort healing, ubyte points
                //   }
                //
                // The client reads it in exactly that order
                // (`clientArenaEndRound.gml`), so a reader that skipped the body
                // would lose the stream from here on.
                var win = reader.ReadUByte();
                var mvpsRed = reader.ReadUByte();
                var mvpsBlue = reader.ReadUByte();
                reader.Skip(2); // redWins, blueWins
                reader.Skip(5 * (mvpsRed + mvpsBlue));

                return Done(type, ref reader, start, playerId: win);
            }

            case MessageType.MessageString:
                return Done(type, ref reader, start, text: reader.ReadString1());

            case MessageType.MapEnd:
            {
                var next = reader.ReadString1();
                reader.Skip(1); // winners

                // THE AREA COMES FROM HERE. <c>ClientBeginStep.gml:461-469</c>
                // assigns <c>global.currentMapArea</c> out of MAP_END, ten
                // seconds before the CHANGE_MAP that acts on it — and CHANGE_MAP
                // carries only the map NAME, so this is the only place on the
                // wire the area is ever stated after a join.
                //
                // Two things depend on it: the entity narrowing a reader does
                // with ForArea, and the rule that a CHANGE_MAP wipes every team
                // — which the client applies on area 1 ONLY. A reader stuck on
                // area 1 on a multi-area map like cp_dirtbowl would clear every
                // team on each later CHANGE_MAP and disagree with every real
                // client about who was on which side.
                CurrentMapArea = reader.ReadUByte();

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
