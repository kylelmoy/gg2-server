using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// A character's simulation state as the wire carries it.
/// </summary>
/// <remarks>
/// <b>One copy of this conversion, on purpose.</b> The live server and the
/// parity replayer both quantise characters for the wire, and if they did it
/// separately then a recording could pass every parity gate while the server
/// shipped different bytes — which would make docs/PARITY.md prove the wrong
/// program. The replayer's whole claim is that it exercises what the server
/// runs.
/// </remarks>
public static class CharacterWire
{
    /// <summary>
    /// Quantises a character into the block
    /// <c>Character.events/User Event 12.xml</c> writes.
    /// </summary>
    /// <remarks>
    /// Position is a plain <c>x*5</c> with no GML <c>round()</c> in front of it,
    /// so the extension's own half-away-from-zero rule is the entire
    /// quantisation — unlike <c>Intelligence</c>, which writes
    /// <c>round(x*5)</c>. docs/TRAPS-SIMULATION.md's rounding table has the five cases and why
    /// they differ; this is the one that matters most often.
    /// </remarks>
    /// <param name="state">The character.</param>
    /// <param name="weapon">
    /// Its weapon. The ammo byte and — for three of the ten classes — the
    /// class-specific byte come out of it, so a character cannot be serialised
    /// without one. The reference cannot have a character without one either.
    /// </param>
    public static CharacterBlock ToBlock(in CharacterState state, in WeaponState weapon) => new()
    {
        Keys = state.KeyState,
        NetAimDirection = state.NetAimDirection,
        AimDistance = state.AimDistance,
        HasPosition = true,

        X = WireQuantise.ToUShort(state.X * 5),
        Y = WireQuantise.ToUShort(state.Y * 5),
        HSpeed = WireQuantise.ToByte(state.HSpeed * 8.5),
        VSpeed = WireQuantise.ToByte(state.VSpeed * 8.5),
        Hp = WireQuantise.ToUByte(Math.Ceiling(state.Hp)),
        Ammo = WireQuantise.ToUByte(weapon.AmmoCount),

        // Bit 0 is the cloak; bits 1..3 are moveStatus.
        Flags = (byte)((state.Cloak ? 0x01 : 0x00) | ((state.MoveStatus & 0x07) << 1)),

        HasFullDetail = true,

        // animationOffset picks which of the character's sprite sets the client
        // draws (CHARACTER_ANIMATION_NORMAL / _INTEL). It is a drawing
        // selector, and no phase owns it yet.
        AnimationOffset = 0,

        ClassSpecific = ClassSpecificByte(state, weapon),
        Alarm1 = WireQuantise.ToShort(state.IntelGrabTimer),
        Intel = state.CarryingIntel ? (byte)1 : (byte)0,
        IntelRecharge = WireQuantise.ToShort(state.IntelRecharge),
    };

    /// <summary>
    /// The one byte four classes use for something and six write as zero.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/User Event 12.xml</c>'s <c>switch(player.class)</c>.
    /// Note that three of the four read it off the <em>weapon</em> and one off
    /// the character, and that only the medic's is scaled — the sniper's charge
    /// and the engineer's nuts and bolts go out unscaled because both already
    /// fit in a byte.
    /// </remarks>
    private static byte ClassSpecificByte(in CharacterState state, in WeaponState weapon) => state.Class switch
    {
        PlayerClass.Spy => WireQuantise.ToUByte(state.CloakAlpha * 255),
        PlayerClass.Medic => WireQuantise.ToUByte(weapon.UberCharge * 255 / WeaponState.MaxUberCharge),
        PlayerClass.Engineer => WireQuantise.ToUByte(state.NutsNBolts),
        PlayerClass.Sniper => WireQuantise.ToUByte(weapon.Charge),
        _ => 0,
    };

    /// <summary>
    /// Quantises a weapon into the block that closes a <c>FULL_UPDATE</c>
    /// character.
    /// </summary>
    /// <remarks>
    /// <c>alarm[0]</c> rests at -1 when nothing is pending and the reference
    /// writes it through <c>write_ubyte</c>, which clamps — so an idle weapon
    /// sends 0 and a client reading it back gets "no refire pending", which is
    /// what it is.
    /// </remarks>
    public static WeaponBlock ToWeaponBlock(in WeaponState weapon, PlayerClass playerClass) => new()
    {
        Class = playerClass,
        ReadyToShoot = weapon.ReadyToShoot ? (byte)1 : (byte)0,
        RefireAlarm = WireQuantise.ToUByte(weapon.RefireTimer),
        HealTarget = weapon.HealTarget,
        MinesLobbed = WireQuantise.ToUByte(weapon.Lobbed),
    };

    /// <summary>
    /// Quantises a sentry into the block
    /// <c>Sentry.events/User Event 12.xml</c> writes.
    /// </summary>
    /// <remarks>
    /// The position is <c>round(x*5)</c> — GML's half-to-even <em>then</em> the
    /// extension's write, like <c>Intelligence</c> and a demoman's mines, and
    /// unlike the character's plain <c>x*5</c>. The hp byte packs
    /// <c>built*$80 + min(127, hp)</c>, and the trailing <c>vspeed</c> byte is
    /// self-describing through that top bit: a built sentry has none.
    /// docs/PROTOCOL.md, "Per sentry".
    /// </remarks>
    public static SentryBlock ToSentryBlock(in SentryState sentry) => new()
    {
        HasPlacement = true,
        HasHp = true,
        StartDirection = (sbyte)sentry.StartDirection,
        X = WireQuantise.ToUShort(Gml.Round(sentry.X * 5)),
        Y = WireQuantise.ToUShort(Gml.Round(sentry.Y * 5)),

        // write_ubyte clamps and rounds half away from zero, once, on the whole
        // expression — the min happens in GML on the real hp.
        BuiltAndHp = WireQuantise.ToUByte(
            ((sentry.Built ? 1 : 0) * 128) + Math.Min(127.0, sentry.Hp)),

        VSpeed = sentry.Built ? null : WireQuantise.ToUByte(sentry.Motion.VSpeed),
    };

    /// <summary>
    /// The same block, with a demoman's mines filled in from the projectile
    /// pool.
    /// </summary>
    /// <remarks>
    /// <b>The count and the blocks have to come from the same place.</b>
    /// <see cref="WeaponState.Lobbed"/> is the field the simulation
    /// maintains and this method serialises from it — a divergence here would
    /// put a wrong count on the wire with no mine blocks to match, which is
    /// self-consistent and therefore silent: every client would simply draw
    /// no mines.
    /// <para>
    /// <c>Minegun.events/User Event 12.xml</c> writes <c>lobbed</c> and then
    /// walks <c>with(Mine) if(ownerPlayer == other.ownerPlayer)</c>, so the
    /// blocks are in <b>instance order</b> — creation order, which is what
    /// <see cref="WorldState.LiveProjectileSlot"/> walks. The count is a promise
    /// of that many seven-byte records and a client reads them positionally, so
    /// a disagreement desynchronises everything after it.
    /// </para>
    /// <para>
    /// <b>A mine's speeds are quantised by <c>5</c>, not the character's
    /// 8.5</b>, and every one of its five fields goes through GML's
    /// <c>round()</c> — where a character's position is a plain <c>x*5</c>. Two
    /// of this codebase's six rounding rules meet in one block.
    /// </para>
    /// </remarks>
    public static WeaponBlock ToWeaponBlock(in WorldState state, int slot)
    {
        var playerClass = state.Characters[slot].Class;
        var block = ToWeaponBlock(state.Weapons[slot], playerClass);

        if (playerClass != PlayerClass.Demoman || state.Weapons[slot].Lobbed == 0)
            return block;

        var mines = new MineBlock[state.Weapons[slot].Lobbed];
        var found = 0;

        // The loop is NOT bounded by mines.Length. Stopping at the count
        // would make the check below blind in one direction: a pool holding
        // *more* mines than `lobbed` claims would fill the array, agree with
        // itself and write a short block list. Count them all, then compare.
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref readonly var projectile = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (projectile.Kind != ProjectileKind.Mine || projectile.OwnerSlot != slot)
                continue;

            if (found >= mines.Length)
            {
                found++;
                continue;
            }

            mines[found++] = new MineBlock
            {
                X = WireQuantise.ToUShort(Gml.Round(projectile.X * 5)),
                Y = WireQuantise.ToUShort(Gml.Round(projectile.Y * 5)),
                HSpeed = WireQuantise.ToByte(Gml.Round(projectile.Motion.HSpeed * 5)),
                VSpeed = WireQuantise.ToByte(Gml.Round(projectile.Motion.VSpeed * 5)),
                Stickied = projectile.Stickied ? (byte)1 : (byte)0,
            };
        }

        // `lobbed` is the reference's own counter, incremented by the launcher
        // and decremented by Mine.events/Destroy.xml, so it should always equal
        // the mines in the pool. If it does not, the byte already written
        // promises records that do not exist and every client reads the rest of
        // the update at the wrong offset — the same class of failure as the
        // sentry hp byte (docs/PROTOCOL.md).
        if (found != mines.Length)
        {
            throw new InvalidOperationException(
                $"slot {slot} says {mines.Length} mine(s) lobbed but the pool holds {found}");
        }

        return block with { Mines = mines };
    }
}
