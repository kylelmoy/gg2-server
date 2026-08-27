namespace Gg2.Protocol;

/// <summary>
/// A character's block, as it appears on the wire.
/// </summary>
/// <remarks>
/// Kept in <b>wire units</b>, unscaled. Position is 1/5 of a world pixel and
/// velocity is 1/8.5 px/tick, and converting on the way in would throw away
/// exactly the thing a parity comparison is about: the port and the reference
/// must agree on these integers, not on the doubles behind them.
/// <para>
/// From <c>Objects/InGameElements/Character.events/User Event 12.xml</c>.
/// </para>
/// </remarks>
public readonly record struct CharacterBlock
{
    // Present on every tier.
    public required PlayerKeys Keys { get; init; }

    public required ushort NetAimDirection { get; init; }

    /// <summary>Aim distance / 2, as sent.</summary>
    public required byte AimDistance { get; init; }

    /// <summary>False on an <c>INPUTSTATE</c>, where only the fields above are sent.</summary>
    public required bool HasPosition { get; init; }

    // QUICK_UPDATE and FULL_UPDATE only.

    /// <summary>x * 5.</summary>
    public ushort X { get; init; }

    /// <summary>y * 5.</summary>
    public ushort Y { get; init; }

    /// <summary>hspeed * 8.5, saturating.</summary>
    public sbyte HSpeed { get; init; }

    /// <summary>vspeed * 8.5, saturating.</summary>
    public sbyte VSpeed { get; init; }

    /// <summary><c>ceil(hp)</c>.</summary>
    public byte Hp { get; init; }

    public byte Ammo { get; init; }

    /// <summary>Bit 0 is cloak; bits 1..3 are <c>moveStatus &amp; 0x07</c>.</summary>
    public byte Flags { get; init; }

    public bool Cloaked => (Flags & 0x01) != 0;

    public int MoveStatus => (Flags >> 1) & 0x07;

    // FULL_UPDATE only.

    public bool HasFullDetail { get; init; }

    public byte AnimationOffset { get; init; }

    /// <summary>Spy cloak alpha, medic uber, engineer nuts and bolts, sniper charge, or 0.</summary>
    public byte ClassSpecific { get; init; }

    public short Alarm1 { get; init; }

    public byte Intel { get; init; }

    public short IntelRecharge { get; init; }
}

/// <summary>A sentry's block. <c>Objects/InGameElements/Sentry.events/User Event 12.xml</c>.</summary>
public readonly record struct SentryBlock
{
    /// <summary>Present only on a <c>FULL_UPDATE</c>.</summary>
    public bool HasPlacement { get; init; }

    /// <summary>
    /// Whether <see cref="BuiltAndHp"/> was on the wire at all.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An <c>INPUTSTATE</c>-tier update carries no sentry bytes
    /// whatsoever</b> — not the placement, and not even the hp.
    /// <c>Sentry.events/User Event 12.xml</c> guards each group of writes on
    /// the update tier and between them they cover only <c>FULL_UPDATE</c> and
    /// <c>QUICK_UPDATE</c>, so on an <c>INPUTSTATE</c> the player's block ends
    /// at its <c>subobjects</c> byte with bit 1 still set. False here means
    /// <see cref="BuiltAndHp"/>, <see cref="Built"/> and <see cref="Hp"/> are
    /// defaults rather than observations.
    /// </remarks>
    public bool HasHp { get; init; }

    public sbyte StartDirection { get; init; }

    /// <summary><c>round(x * 5)</c>.</summary>
    public ushort X { get; init; }

    public ushort Y { get; init; }

    /// <summary>
    /// Bit 7 is "built"; the low seven bits are <c>min(127, hp)</c>. The two
    /// share a byte, which is also how a reader knows whether the trailing
    /// <c>vspeed</c> byte is there. Meaningful only when <see cref="HasHp"/>.
    /// </summary>
    public required byte BuiltAndHp { get; init; }

    public bool Built => (BuiltAndHp & 0x80) != 0;

    public int Hp => BuiltAndHp & 0x7F;

    /// <summary>Present on a <c>FULL_UPDATE</c> only while the sentry is still falling.</summary>
    public byte? VSpeed { get; init; }
}

/// <summary>A demoman's mine. <c>Objects/Projectiles/Mine.events/User Event 12.xml</c>.</summary>
public readonly record struct MineBlock
{
    /// <summary><c>round(x * 5)</c>.</summary>
    public required ushort X { get; init; }

    public required ushort Y { get; init; }

    /// <summary><c>round(hspeed * 5)</c> — note 5, not the character's 8.5.</summary>
    public required sbyte HSpeed { get; init; }

    public required sbyte VSpeed { get; init; }

    public required byte Stickied { get; init; }
}

/// <summary>One roster entry's block within a state update.</summary>
public sealed class PlayerBlock
{
    /// <summary>0x01 = has a character, 0x02 = has a sentry.</summary>
    public byte Subobjects { get; init; }

    public CharacterBlock? Character { get; init; }

    public SentryBlock? Sentry { get; init; }

    // FULL_UPDATE only.

    public byte Kills { get; init; }

    public byte Deaths { get; init; }

    public byte Caps { get; init; }

    public byte Assists { get; init; }

    public byte Destruction { get; init; }

    public byte Stabs { get; init; }

    public ushort Healing { get; init; }

    public byte Defenses { get; init; }

    public byte Invulns { get; init; }

    public byte Bonus { get; init; }

    public byte Points { get; init; }

    public byte QueueJump { get; init; }

    public string UnparsedRewards { get; init; } = string.Empty;

    /// <summary>One count per <em>other</em> roster entry, in roster order.</summary>
    public byte[] DominationKills { get; init; } = [];

    /// <summary>The medic's heal target's roster index, or 255 for nobody.</summary>
    public byte? HealTarget { get; init; }

    /// <summary>A demoman's lobbed mines.</summary>
    public MineBlock[] Mines { get; init; } = [];

    /// <summary>
    /// Whether the weapon may fire this tick — <c>Weapon.events/User Event
    /// 12.xml</c>'s first byte.
    /// </summary>
    /// <remarks>
    /// Null for a medic, whose <c>Medigun</c> is the one weapon that overrides
    /// <c>User Event 12</c> <em>without</em> an inherited action and so writes
    /// neither of the parent's two bytes.
    /// </remarks>
    public byte? ReadyToShoot { get; init; }

    /// <summary>
    /// Ticks left on the weapon's refire alarm, <c>alarm[0]</c>.
    /// </summary>
    /// <remarks>
    /// Written as a <c>ubyte</c>, so a reload longer than 255 ticks would clamp
    /// — none is. Null for a medic, for the same reason as
    /// <see cref="ReadyToShoot"/>.
    /// </remarks>
    public byte? RefireAlarm { get; init; }
}

/// <summary>A <c>MovingPlatform</c> block. Absent from an <c>INPUTSTATE</c>.</summary>
public readonly record struct MovingPlatformBlock(ushort X, ushort Y, byte MovementState);

/// <summary>An intel entity. <c>Intelligence.events/User Event 10.xml</c>.</summary>
public readonly record struct IntelBlock(ushort X, ushort Y, short RecoverTimer);

/// <summary>A control point. <c>ControlPoint.events/User Event 12.xml</c>.</summary>
public readonly record struct ControlPointBlock(sbyte Team, sbyte CappingTeam, ushort Capping);

/// <summary>
/// One line of an <c>ARENA_ENDROUND</c>'s MVP list.
/// </summary>
/// <remarks>
/// <c>serverArenaEndRound.gml:43-46</c>: the roster index, then three of that
/// player's <b>round</b> statistics. Up to three a side, best first.
/// </remarks>
public readonly record struct ArenaMvp(byte PlayerId, byte Kills, ushort Healing, byte Points);

/// <summary>
/// A generator. <c>Generator.events/User Event 12.xml</c>: <c>ceil(hp)</c> then
/// <c>ceil(shieldHp)</c>.
/// </summary>
/// <remarks>
/// ⚠️ <b>There is one of these per <em>surviving</em> generator, not one per
/// team.</b> The HUD block writes them with
/// <c>with GeneratorBlue event_user(12)</c>, which visits however many
/// instances exist — so a destroyed generator makes the message four bytes
/// shorter, and a reader that always takes two loses the stream.
/// </remarks>
public readonly record struct GeneratorBlock(ushort Hp, ushort ShieldHp);

/// <summary>
/// A decoded state update.
/// </summary>
/// <remarks>
/// Which fields are populated depends on <see cref="Tier"/>, exactly as the
/// wire does. A <c>CAPS_UPDATE</c> carries no players at all.
/// </remarks>
public sealed class StateUpdate
{
    public required MessageType Tier { get; init; }

    /// <summary>
    /// The count the update declares. The client aborts deserialising if this
    /// disagrees with its own roster size, so it is worth checking rather than
    /// trusting.
    /// </summary>
    public required int PlayerCount { get; init; }

    /// <summary><c>FULL_UPDATE</c> only.</summary>
    public ushort TdmInvulnerabilityTicks { get; init; }

    public PlayerBlock[] Players { get; init; } = [];

    public MovingPlatformBlock[] MovingPlatforms { get; init; } = [];

    // FULL_UPDATE and CAPS_UPDATE tails.

    public IntelBlock[] RedIntel { get; init; } = [];

    public IntelBlock[] BlueIntel { get; init; } = [];

    public byte CapLimit { get; init; }

    public byte RedCaps { get; init; }

    public byte BlueCaps { get; init; }

    public byte RespawnTimeSeconds { get; init; }

    public ControlPointBlock[] ControlPoints { get; init; } = [];

    /// <summary>
    /// The gamemode HUD block's scalars, in the order that mode writes them.
    /// </summary>
    /// <remarks>
    /// Deliberately untyped. Nothing downstream of the parity harness reads
    /// them yet, and naming eight modes' worth of fields before anything wants
    /// one is how a decoder acquires a schema it cannot change.
    /// </remarks>
    public long[] HudScalars { get; init; } = [];

    /// <summary><c>FULL_UPDATE</c> only. Ten of them.</summary>
    public byte[] ClassLimits { get; init; } = [];
}

/// <summary>
/// The block a character's <c>currentWeapon</c> writes after it, as a value
/// type for the serialiser.
/// </summary>
/// <remarks>
/// The same four fields <see cref="PlayerBlock"/> flattens on the reading side.
/// A separate struct because the writer runs every tick and must not allocate,
/// and <see cref="PlayerBlock"/> is a class.
/// <para>
/// <b>Three shapes, not one.</b> A medic writes the heal target alone, a demoman
/// writes the pair plus a mine count plus a block per mine, and everybody else
/// writes the pair. See <c>Gg2.Protocol.StateUpdateReader.ReadWeapon</c> for the
/// measurement and the four phases this repo had it wrong for.
/// </para>
/// </remarks>
public readonly record struct WeaponBlock
{
    /// <summary>
    /// Needed only because <see cref="Mines"/> has an initializer: C# requires
    /// a struct with one to declare a constructor explicitly.
    /// </summary>
    public WeaponBlock()
    {
    }

    /// <summary>Which of the three shapes this is.</summary>
    public required PlayerClass Class { get; init; }

    /// <summary><c>readyToShoot</c>. Not written for a medic.</summary>
    public byte ReadyToShoot { get; init; }

    /// <summary><c>alarm[0]</c>, saturating into a ubyte. Not written for a medic.</summary>
    public byte RefireAlarm { get; init; }

    /// <summary>A medic's heal target, or 255. Written only for a medic.</summary>
    public byte HealTarget { get; init; }

    /// <summary>A demoman's <c>lobbed</c>, and so how many mine blocks follow.</summary>
    public byte MinesLobbed { get; init; }

    /// <summary>
    /// The mines themselves, in instance order.
    /// </summary>
    /// <remarks>
    /// Its length must equal <see cref="MinesLobbed"/> — the count is a promise
    /// of exactly that many seven-byte records and a client reads them
    /// positionally. Empty for every class but a demoman.
    /// </remarks>
    public MineBlock[] Mines { get; init; } = [];
}
