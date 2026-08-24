namespace Gg2.Protocol;

/// <summary>
/// Decodes a state update, the mirror of
/// <c>Scripts/Serialization/deserializeState.gml</c>.
/// </summary>
/// <remarks>
/// A state update has <b>no framing of its own</b>. Its width comes from three
/// places at once — the roster size, the map (via <see cref="WorldShape"/>), and
/// self-describing bytes inside it — so it must be parsed rather than measured.
/// The three self-describing points, each of which a from-scratch reader gets
/// wrong by assuming a fixed size:
/// <list type="bullet">
/// <item>a player's <c>subobjects</c> byte decides whether a character and a
/// sentry block follow;</item>
/// <item>a sentry's hp byte carries "built" in bit 7, and an unbuilt sentry
/// has one extra <c>vspeed</c> byte on a <c>FULL_UPDATE</c>;</item>
/// <item>a demoman's <c>Minegun</c> writes <c>lobbed</c> and then that many
/// seven-byte mine blocks.</item>
/// </list>
/// <para>
/// The per-player weapon block on a <c>FULL_UPDATE</c> is therefore
/// class-dependent, which is why the reader has to be told each player's class.
/// Only two classes write one at all: <c>Medic</c>'s <c>Medigun</c> and
/// <c>Demoman</c>'s <c>Minegun</c> are the only weapons in
/// <c>Objects/Weapons/</c> with a <c>User Event 12</c>.
/// </para>
/// </remarks>
public static class StateUpdateReader
{
    /// <summary>
    /// Reads one state update whose message byte has already been consumed.
    /// </summary>
    /// <param name="reader">Positioned just past the tier byte.</param>
    /// <param name="tier">Which tier the message byte said.</param>
    /// <param name="shape">The map's contribution to the layout.</param>
    /// <param name="classes">
    /// Each roster entry's class, in roster order. Only consulted on a
    /// <c>FULL_UPDATE</c>, and only for players that have a character.
    /// </param>
    public static StateUpdate Read(
        ref NetReader reader,
        MessageType tier,
        in WorldShape shape,
        ReadOnlySpan<PlayerClass> classes)
    {
        var full = tier == MessageType.FullUpdate;
        var quick = tier == MessageType.QuickUpdate;
        var caps = tier == MessageType.CapsUpdate;

        if (!full && !quick && !caps && tier != MessageType.InputState)
            throw new ProtocolException($"{tier} is not a state update");

        var tdmInvulnerability = full ? reader.ReadUShort() : (ushort)0;
        var playerCount = reader.ReadUByte();

        var players = Array.Empty<PlayerBlock>();
        var platforms = Array.Empty<MovingPlatformBlock>();

        if (!caps)
        {
            players = new PlayerBlock[playerCount];
            for (var i = 0; i < playerCount; i++)
            {
                var playerClass = i < classes.Length ? classes[i] : PlayerClass.Scout;
                players[i] = ReadPlayer(ref reader, tier, playerCount, playerClass);
            }

            // One block per platform in instance order, with no count prefix.
            platforms = new MovingPlatformBlock[shape.MovingPlatformCount];
            for (var i = 0; i < platforms.Length; i++)
                platforms[i] = ReadMovingPlatform(ref reader, tier);
        }

        var update = new StateUpdate
        {
            Tier = tier,
            PlayerCount = playerCount,
            TdmInvulnerabilityTicks = tdmInvulnerability,
            Players = players,
            MovingPlatforms = platforms,
        };

        if (!full && !caps)
            return update;

        // The tails. FULL_UPDATE carries the intel entities and the class
        // limits; CAPS_UPDATE carries neither.
        var redIntel = full ? ReadIntel(ref reader) : [];
        var blueIntel = full ? ReadIntel(ref reader) : [];

        var capLimit = full ? reader.ReadUByte() : (byte)0;
        var redCaps = reader.ReadUByte();
        var blueCaps = reader.ReadUByte();
        var respawn = reader.ReadUByte();

        var hud = ReadHud(ref reader, tier, shape, out var controlPoints);

        var classLimits = Array.Empty<byte>();
        if (full)
        {
            classLimits = new byte[10];
            for (var i = 0; i < 10; i++)
                classLimits[i] = reader.ReadUByte();
        }

        return new StateUpdate
        {
            Tier = tier,
            PlayerCount = playerCount,
            TdmInvulnerabilityTicks = tdmInvulnerability,
            Players = players,
            MovingPlatforms = platforms,
            RedIntel = redIntel,
            BlueIntel = blueIntel,
            CapLimit = capLimit,
            RedCaps = redCaps,
            BlueCaps = blueCaps,
            RespawnTimeSeconds = respawn,
            ControlPoints = controlPoints,
            HudScalars = hud,
            ClassLimits = classLimits,
        };
    }

    private static PlayerBlock ReadPlayer(
        ref NetReader reader,
        MessageType tier,
        int playerCount,
        PlayerClass playerClass)
    {
        byte kills = 0, deaths = 0, caps = 0, assists = 0, destruction = 0, stabs = 0;
        ushort healing = 0;
        byte defenses = 0, invulns = 0, bonus = 0, points = 0, queueJump = 0;
        var rewards = string.Empty;
        var dominations = Array.Empty<byte>();

        if (tier == MessageType.FullUpdate)
        {
            kills = reader.ReadUByte();
            deaths = reader.ReadUByte();
            caps = reader.ReadUByte();
            assists = reader.ReadUByte();
            destruction = reader.ReadUByte();
            stabs = reader.ReadUByte();
            healing = reader.ReadUShort();
            defenses = reader.ReadUByte();
            invulns = reader.ReadUByte();
            bonus = reader.ReadUByte();
            points = reader.ReadUByte();
            queueJump = reader.ReadUByte();
            rewards = reader.ReadString2();

            // One count per other roster entry — REVENGE and DOMINATIONS are
            // not sent as stats, this table is.
            dominations = new byte[playerCount - 1];
            for (var i = 0; i < dominations.Length; i++)
                dominations[i] = reader.ReadUByte();
        }

        var subobjects = reader.ReadUByte();

        CharacterBlock? character = null;
        byte? healTarget = null;
        var mines = Array.Empty<MineBlock>();

        if ((subobjects & 0x01) != 0)
        {
            character = ReadCharacter(ref reader, tier);

            if (tier == MessageType.FullUpdate)
                (healTarget, mines) = ReadWeapon(ref reader, playerClass);
        }

        SentryBlock? sentry = null;
        if ((subobjects & 0x02) != 0)
            sentry = ReadSentry(ref reader, tier);

        return new PlayerBlock
        {
            Subobjects = subobjects,
            Character = character,
            Sentry = sentry,
            Kills = kills,
            Deaths = deaths,
            Caps = caps,
            Assists = assists,
            Destruction = destruction,
            Stabs = stabs,
            Healing = healing,
            Defenses = defenses,
            Invulns = invulns,
            Bonus = bonus,
            Points = points,
            QueueJump = queueJump,
            UnparsedRewards = rewards,
            DominationKills = dominations,
            HealTarget = healTarget,
            Mines = mines,
        };
    }

    private static CharacterBlock ReadCharacter(ref NetReader reader, MessageType tier)
    {
        var block = new CharacterBlock
        {
            Keys = (PlayerKeys)reader.ReadUByte(),
            NetAimDirection = reader.ReadUShort(),
            AimDistance = reader.ReadUByte(),
            HasPosition = tier is MessageType.QuickUpdate or MessageType.FullUpdate,
        };

        if (!block.HasPosition)
            return block;

        block = block with
        {
            X = reader.ReadUShort(),
            Y = reader.ReadUShort(),
            HSpeed = reader.ReadByte(),
            VSpeed = reader.ReadByte(),
            Hp = reader.ReadUByte(),
            Ammo = reader.ReadUByte(),
            Flags = reader.ReadUByte(),
        };

        if (tier != MessageType.FullUpdate)
            return block;

        return block with
        {
            HasFullDetail = true,
            AnimationOffset = reader.ReadUByte(),
            ClassSpecific = reader.ReadUByte(),
            Alarm1 = reader.ReadShort(),
            Intel = reader.ReadUByte(),
            IntelRecharge = reader.ReadShort(),
        };
    }

    /// <summary>
    /// The <c>with(currentWeapon) event_user(12)</c> that closes a character's
    /// <c>FULL_UPDATE</c> block.
    /// </summary>
    /// <remarks>
    /// Only <c>Medigun</c> and <c>Minegun</c> define one; every other weapon
    /// writes nothing at all, so the block is zero bytes for eight of the ten
    /// classes.
    /// </remarks>
    private static (byte? HealTarget, MineBlock[] Mines) ReadWeapon(ref NetReader reader, PlayerClass playerClass)
    {
        switch (playerClass)
        {
            case PlayerClass.Medic:
                return (reader.ReadUByte(), []);

            case PlayerClass.Demoman:
            {
                var lobbed = reader.ReadUByte();
                var mines = new MineBlock[lobbed];

                for (var i = 0; i < lobbed; i++)
                {
                    mines[i] = new MineBlock
                    {
                        X = reader.ReadUShort(),
                        Y = reader.ReadUShort(),
                        HSpeed = reader.ReadByte(),
                        VSpeed = reader.ReadByte(),
                        Stickied = reader.ReadUByte(),
                    };
                }

                return (null, mines);
            }

            default:
                return (null, []);
        }
    }

    private static SentryBlock ReadSentry(ref NetReader reader, MessageType tier)
    {
        var full = tier == MessageType.FullUpdate;

        sbyte startDirection = 0;
        ushort x = 0, y = 0;

        if (full)
        {
            startDirection = reader.ReadByte();
            x = reader.ReadUShort();
            y = reader.ReadUShort();
        }

        var builtAndHp = reader.ReadUByte();

        // The trailing vspeed byte exists only while the sentry is falling, and
        // "falling" is bit 7 of the byte just read. Self-describing, but only
        // if the reader looks.
        byte? vspeed = full && (builtAndHp & 0x80) == 0 ? reader.ReadUByte() : null;

        return new SentryBlock
        {
            HasPlacement = full,
            StartDirection = startDirection,
            X = x,
            Y = y,
            BuiltAndHp = builtAndHp,
            VSpeed = vspeed,
        };
    }

    private static MovingPlatformBlock ReadMovingPlatform(ref NetReader reader, MessageType tier)
    {
        if (tier is not (MessageType.QuickUpdate or MessageType.FullUpdate))
            return default;

        return new MovingPlatformBlock(reader.ReadUShort(), reader.ReadUShort(), reader.ReadUByte());
    }

    /// <summary>
    /// <c>serialize(obj)</c>: a ushort instance count, then one block each.
    /// </summary>
    private static IntelBlock[] ReadIntel(ref NetReader reader)
    {
        var count = reader.ReadUShort();
        var intel = new IntelBlock[count];

        for (var i = 0; i < count; i++)
            intel[i] = new IntelBlock(reader.ReadUShort(), reader.ReadUShort(), reader.ReadShort());

        return intel;
    }

    /// <summary>
    /// The gamemode HUD block. Only Arena varies by tier, and only on
    /// <c>FULL_UPDATE</c>.
    /// </summary>
    private static long[] ReadHud(
        ref NetReader reader,
        MessageType tier,
        in WorldShape shape,
        out ControlPointBlock[] controlPoints)
    {
        var scalars = new List<long>(8);
        controlPoints = [];

        switch (shape.GameMode)
        {
            case GameMode.CaptureTheFlag:
                scalars.Add(reader.ReadUByte());
                scalars.Add(reader.ReadUInt());
                break;

            case GameMode.Invasion:
                scalars.Add(reader.ReadUByte());
                scalars.Add(reader.ReadUInt());
                scalars.Add(reader.ReadUShort());
                break;

            case GameMode.Generator:
                scalars.Add(reader.ReadUByte());
                scalars.Add(reader.ReadUInt());
                // Blue then red, in that order.
                scalars.Add(reader.ReadUShort());
                scalars.Add(reader.ReadUShort());
                scalars.Add(reader.ReadUShort());
                scalars.Add(reader.ReadUShort());
                break;

            case GameMode.Arena:
                if (tier == MessageType.FullUpdate)
                {
                    scalars.Add(reader.ReadUByte());  // redWins
                    scalars.Add(reader.ReadUByte());  // blueWins
                    scalars.Add(reader.ReadUByte());  // state
                    scalars.Add(reader.ReadByte());   // winners, signed
                    scalars.Add(reader.ReadUShort()); // endCount
                }

                scalars.Add(reader.ReadUByte());
                scalars.Add(reader.ReadUInt());
                scalars.Add(reader.ReadUShort());
                scalars.Add(reader.ReadUByte());      // floor(roundStart / 2)
                controlPoints = ReadControlPoints(ref reader, shape.ControlPointCount);
                break;

            case GameMode.KingOfTheHill:
            case GameMode.DualKingOfTheHill:
                scalars.Add(reader.ReadUShort());     // cpUnlock
                scalars.Add(reader.ReadUShort());     // redTimer
                scalars.Add(reader.ReadUShort());     // blueTimer
                controlPoints = ReadControlPoints(ref reader, shape.ControlPointCount);
                break;

            case GameMode.ControlPoints:
                scalars.Add(reader.ReadUByte());
                scalars.Add(reader.ReadUInt());
                scalars.Add(reader.ReadUShort());     // setupTimer
                controlPoints = ReadControlPoints(ref reader, shape.ControlPointCount);
                break;

            case GameMode.TeamDeathmatch:
                scalars.Add(reader.ReadUByte());
                scalars.Add(reader.ReadUInt());
                scalars.Add(reader.ReadUShort());     // killLimit
                break;

            default:
                throw new ProtocolException($"no HUD layout for {shape.GameMode}");
        }

        return [.. scalars];
    }

    private static ControlPointBlock[] ReadControlPoints(ref NetReader reader, int count)
    {
        var points = new ControlPointBlock[count];

        for (var i = 0; i < count; i++)
            points[i] = new ControlPointBlock(reader.ReadByte(), reader.ReadByte(), reader.ReadUShort());

        return points;
    }
}
