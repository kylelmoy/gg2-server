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
/// <b>Every</b> class writes one — two bytes for eight of them, one for a medic,
/// and three plus seven per mine for a demoman. See <c>ReadWeapon</c>, which
/// carries the measurement and the mistake it corrects.
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
        var weapon = default(WeaponBlock);

        if ((subobjects & 0x01) != 0)
        {
            character = ReadCharacter(ref reader, tier);

            if (tier == MessageType.FullUpdate)
                weapon = ReadWeapon(ref reader, playerClass);
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
            HealTarget = weapon.HealTarget,
            Mines = weapon.Mines ?? [],
            ReadyToShoot = weapon.ReadyToShoot,
            RefireAlarm = weapon.RefireAlarm,
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
    /// <b>Every class writes one.</b> <c>Objects/Weapons/</c> defines a
    /// <c>User Event 12</c> only on <c>Medigun</c> and <c>Minegun</c>, but GM8
    /// <b>inherits an event a child does not define</b>, and every weapon has
    /// <c>&lt;parent&gt;Weapon&lt;/parent&gt;</c> — so the other eight run
    /// <c>Objects/InGameElements/Weapon.events/User Event 12.xml</c>, which
    /// writes <c>readyToShoot</c> and <c>alarm[0]</c>. The client's own
    /// <c>Weapon.events/User Event 13.xml</c> confirms it from the other side:
    /// <c>receiveCompleteMessage(..., 2, ...)</c>.
    /// </para>
    /// <para>
    /// Measured on the running engine by swapping
    /// <c>global.serializeBuffer</c> for a fresh one and reading
    /// <c>buffer_size</c> either side of the call:
    /// </para>
    /// <code>
    /// Medigun      1     own event, and the only one with NO inherited action
    /// Minegun      3 + 7n   inherited action, then lobbed, then n mine blocks
    /// every other  2     inherited from Weapon
    /// </code>
    /// <para>
    /// The difference between <c>Medigun</c> and <c>Minegun</c> is a single
    /// <c>action_inherited</c> in the event's XML, which is invisible in the GML
    /// text — <c>Minegun</c>'s <c>User Event 12</c> has one and
    /// <c>Medigun</c>'s does not.
    /// </para>
    /// </remarks>
    private static WeaponBlock ReadWeapon(ref NetReader reader, PlayerClass playerClass)
    {
        // Medigun overrides without inheriting, so it is the one class that does
        // not open with the parent's two bytes.
        if (playerClass == PlayerClass.Medic)
            return new WeaponBlock { HealTarget = reader.ReadUByte() };

        var block = new WeaponBlock
        {
            ReadyToShoot = reader.ReadUByte(),
            RefireAlarm = reader.ReadUByte(),
        };

        if (playerClass != PlayerClass.Demoman)
            return block;

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

        return block with { Mines = mines };
    }

    /// <summary>What the weapon block contributed to a player's block.</summary>
    private readonly record struct WeaponBlock
    {
        public byte? ReadyToShoot { get; init; }

        public byte? RefireAlarm { get; init; }

        public byte? HealTarget { get; init; }

        public MineBlock[] Mines { get; init; }
    }

    private static SentryBlock ReadSentry(ref NetReader reader, MessageType tier)
    {
        var full = tier == MessageType.FullUpdate;

        // An INPUTSTATE-tier update writes NOTHING AT ALL for a sentry, not
        // even its hp. Sentry.events/User Event 12.xml guards each group on the
        // tier and the guards between them cover only FULL_UPDATE and
        // QUICK_UPDATE, so on an INPUTSTATE every branch is skipped and the
        // player's block ends at its subobjects byte.
        //
        // Reading the hp byte unconditionally desynchronises the whole stream
        // from the first INPUTSTATE tick on which anybody owns a sentry — one
        // byte per sentry, every tick that is not a quick update
        // (GameServerBeginStep.gml:99). It looks like an unknown message type
        // several hundred bytes later, which is nowhere near the cause.
        // docs/PARITY.md, "when adding a fixture, ask what state it cannot
        // reach".
        var hasHp = tier is MessageType.QuickUpdate or MessageType.FullUpdate;

        sbyte startDirection = 0;
        ushort x = 0, y = 0;

        if (full)
        {
            startDirection = reader.ReadByte();
            x = reader.ReadUShort();
            y = reader.ReadUShort();
        }

        byte builtAndHp = 0;
        if (hasHp)
            builtAndHp = reader.ReadUByte();

        // The trailing vspeed byte exists only while the sentry is falling, and
        // "falling" is bit 7 of the byte just read. Self-describing, but only
        // if the reader looks.
        byte? vspeed = full && (builtAndHp & 0x80) == 0 ? reader.ReadUByte() : null;

        return new SentryBlock
        {
            HasPlacement = full,
            HasHp = hasHp,
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

                // Two fields per *surviving* generator, blue first — not two
                // per team. A destroyed one contributes nothing, so the count
                // has to come from the shape and a consumer has to decrement it
                // on GENERATOR_DESTROY. See WorldShape.GeneratorCount.
                for (var i = 0; i < shape.GeneratorCount; i++)
                {
                    scalars.Add(reader.ReadUShort());
                    scalars.Add(reader.ReadUShort());
                }

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
