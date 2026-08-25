using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Which weapon a class carries. One per <c>Objects/Weapons/*</c>.
/// </summary>
/// <remarks>
/// A class has exactly one — <c>weapons[0]</c>, set in its Create event and
/// never reassigned — so this is a function of <see cref="PlayerClass"/> and
/// not state. <see cref="SentryTurret"/> is the exception: it belongs to a
/// <see cref="SentryState"/> rather than to a character.
/// </remarks>
public enum WeaponKind : byte
{
    Scattergun,
    Rocketlauncher,
    Rifle,
    Minegun,
    Medigun,
    Shotgun,
    Minigun,
    Revolver,
    Flamethrower,
    Blade,
    SentryTurret,
}

/// <summary>
/// The per-weapon numbers that are constant for the whole match, from each
/// <c>Objects/Weapons/*.events/Create.xml</c>.
/// </summary>
/// <remarks>
/// Constants live here rather than in <see cref="WeaponState"/> so that a slot's
/// weapon state stays small — <see cref="WorldState"/> carries one per roster
/// slot and is cloned wholesale by the bot search (docs/BOTAI.md item 1).
/// <para>
/// <b>Only the numbers that reach the wire or the simulation are here.</b> Every
/// weapon's Create event also sets sprite ids, animation lengths and image
/// speeds; those are the client drawing itself and are outside the contract —
/// CLAUDE.md, "Compatible on the wire and in observable simulation. Free
/// everywhere else."
/// </para>
/// </remarks>
public readonly record struct WeaponStats
{
    /// <summary>
    /// Ticks between shots. <c>Weapon.events/Create.xml</c> starts the weapon
    /// with this already on the clock, so a character cannot fire on the tick
    /// it spawns.
    /// </summary>
    public required int RefireTime { get; init; }

    /// <summary>Ticks per reload step. 0 where the weapon has no reload cycle.</summary>
    public required int ReloadTime { get; init; }

    /// <summary>
    /// Ticks after a shot before the reload cycle starts. 0 where the weapon
    /// has none.
    /// </summary>
    public required int ReloadBuffer { get; init; }

    /// <summary>
    /// Full magazine, and the value <c>ammoCount</c> starts at. A sniper's is
    /// genuinely 0 — the rifle has no ammo and writes a zero byte all match.
    /// </summary>
    public required int MaxAmmo { get; init; }

    public static WeaponKind KindFor(PlayerClass playerClass) => playerClass switch
    {
        PlayerClass.Scout => WeaponKind.Scattergun,
        PlayerClass.Soldier => WeaponKind.Rocketlauncher,
        PlayerClass.Sniper => WeaponKind.Rifle,
        PlayerClass.Demoman => WeaponKind.Minegun,
        PlayerClass.Medic => WeaponKind.Medigun,
        PlayerClass.Engineer => WeaponKind.Shotgun,
        PlayerClass.Heavy => WeaponKind.Minigun,
        PlayerClass.Spy => WeaponKind.Revolver,
        PlayerClass.Pyro => WeaponKind.Flamethrower,
        PlayerClass.Quote => WeaponKind.Blade,
        _ => throw new ArgumentOutOfRangeException(nameof(playerClass), playerClass, "no weapon for this class"),
    };

    public static WeaponStats For(PlayerClass playerClass) => For(KindFor(playerClass));

    public static WeaponStats For(WeaponKind kind) => kind switch
    {
        WeaponKind.Scattergun => new WeaponStats { RefireTime = 20, ReloadTime = 15, ReloadBuffer = 20, MaxAmmo = 6 },
        WeaponKind.Shotgun => new WeaponStats { RefireTime = 20, ReloadTime = 15, ReloadBuffer = 20, MaxAmmo = 6 },
        WeaponKind.Rocketlauncher => new WeaponStats { RefireTime = 30, ReloadTime = 22, ReloadBuffer = 30, MaxAmmo = 4 },
        WeaponKind.Minegun => new WeaponStats { RefireTime = 26, ReloadTime = 15, ReloadBuffer = 26, MaxAmmo = 8 },
        WeaponKind.Revolver => new WeaponStats { RefireTime = 18, ReloadTime = 45, ReloadBuffer = 18, MaxAmmo = 6 },
        WeaponKind.Medigun => new WeaponStats { RefireTime = 3, ReloadTime = 55, ReloadBuffer = 0, MaxAmmo = 40 },
        WeaponKind.Blade => new WeaponStats { RefireTime = 5, ReloadTime = 55, ReloadBuffer = 0, MaxAmmo = 90 },

        // The rifle reloads on a timer of its own and has no magazine at all:
        // maxAmmo = 0, so the ammo byte in a sniper's character block is always
        // zero and the charge rides the class-specific byte instead.
        WeaponKind.Rifle => new WeaponStats { RefireTime = 40, ReloadTime = 40, ReloadBuffer = 0, MaxAmmo = 0 },

        // Minigun and Flamethrower refill continuously rather than reloading, so
        // they set reloadBuffer without a reloadTime.
        WeaponKind.Minigun => new WeaponStats { RefireTime = 2, ReloadTime = 0, ReloadBuffer = 10, MaxAmmo = 200 },
        WeaponKind.Flamethrower => new WeaponStats { RefireTime = 1, ReloadTime = 0, ReloadBuffer = 7, MaxAmmo = 200 },

        // A turret is not a character's weapon; it has no ammo and no reload.
        WeaponKind.SentryTurret => new WeaponStats { RefireTime = 5, ReloadTime = 0, ReloadBuffer = 0, MaxAmmo = 0 },

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no stats for this weapon"),
    };
}
