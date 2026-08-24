namespace Gg2.Protocol;

/// <summary>
/// One player's complete action for one tick: the four bytes of an
/// <c>INPUTSTATE</c> payload.
/// </summary>
/// <remarks>
/// This is the whole action space, for everyone. A network client produces it
/// (<c>Scripts/GameServer/processClientCommands.gml</c>) and so does a bot
/// (<c>Scripts/Bots/botInputUpdate.gml</c>), which is what lets both sit behind
/// one input seam — and, per docs/BOTAI.md, what keeps a logged human action a
/// valid bot action. Bots get no privileged float aim and no extra keys.
/// <para>
/// The quantisation is part of that contract, so it is stored quantised and
/// decoded on demand rather than the other way round.
/// </para>
/// </remarks>
public readonly record struct PlayerInput
{
    public PlayerInput(PlayerKeys keys, ushort netAimDirection, byte aimDistance)
    {
        Keys = keys;
        NetAimDirection = netAimDirection;
        AimDistance = aimDistance;
    }

    /// <summary>The <c>keyState</c> byte.</summary>
    public PlayerKeys Keys { get; init; }

    /// <summary>Aim direction as <c>degrees * 65536 / 360</c>.</summary>
    public ushort NetAimDirection { get; init; }

    /// <summary>
    /// Aim distance as world distance / 2, so the wire tops out at 510 px.
    /// </summary>
    public byte AimDistance { get; init; }

    public static PlayerInput None => default;

    /// <summary>
    /// Aim direction in degrees, <c>[0, 360)</c>, measured counter-clockwise
    /// with y pointing down — GM8's <c>point_direction</c> convention.
    /// </summary>
    /// <remarks>
    /// Getting the sign of this wrong mirrors the whole game
    /// (docs/SIMULATION.md, "Float rules").
    /// </remarks>
    public double AimDirectionDegrees => NetAimDirection * 360.0 / 65536.0;

    /// <summary>Aim distance in world pixels.</summary>
    public int AimDistancePixels => AimDistance * 2;

    public bool IsDown(PlayerKeys key) => (Keys & key) != 0;

    public bool Left => IsDown(PlayerKeys.Left);

    public bool Right => IsDown(PlayerKeys.Right);

    public bool Jump => IsDown(PlayerKeys.Jump);

    public bool Down => IsDown(PlayerKeys.Down);

    public bool Attack => IsDown(PlayerKeys.Attack);

    public bool Special => IsDown(PlayerKeys.Special);

    public bool Taunt => IsDown(PlayerKeys.Taunt);

    /// <summary>
    /// Quantises a direction in degrees and a distance in world pixels the way
    /// the client does before sending them.
    /// </summary>
    /// <remarks>
    /// Reproduces what the client writes
    /// (<c>Scripts/Message Builders/ClientInputstate.gml</c>):
    /// <code>
    /// write_ushort(s, point_direction(...) * 65536 / 360);
    /// write_ubyte (s, min(255, point_distance(...) / 2));
    /// </code>
    /// Neither is rounded in GML, so both go through the networking extension's
    /// conversion — round half away from zero, then clamp — which is what
    /// <see cref="WireQuantise"/> does. The explicit <c>min(255, …)</c> on the
    /// distance is therefore belt and braces: the cast would clamp anyway.
    /// <para>
    /// A direction of exactly 360 degrees quantises to 65536, which clamps to
    /// 65535 rather than wrapping to 0 — so the angle is normalised into
    /// <c>[0, 360)</c> first, as <c>point_direction</c> already guarantees.
    /// </para>
    /// </remarks>
    public static PlayerInput FromAim(PlayerKeys keys, double directionDegrees, double distancePixels)
    {
        var wrapped = directionDegrees % 360.0;
        if (wrapped < 0)
            wrapped += 360.0;

        return new PlayerInput(
            keys,
            WireQuantise.ToUShort(wrapped * 65536.0 / 360.0),
            WireQuantise.ToUByte(Math.Min(255, distancePixels / 2.0)));
    }

    /// <summary>Reads the four payload bytes of an <c>INPUTSTATE</c>.</summary>
    public static PlayerInput Read(ref NetReader reader) =>
        new((PlayerKeys)reader.ReadUByte(), reader.ReadUShort(), reader.ReadUByte());

    /// <summary>Writes the four payload bytes of an <c>INPUTSTATE</c>.</summary>
    public void Write(NetWriter writer)
    {
        writer.WriteUByte((byte)Keys);
        writer.WriteUShort(NetAimDirection);
        writer.WriteUByte(AimDistance);
    }

    public override string ToString() =>
        $"{Keys} aim {AimDirectionDegrees:0.##}deg dist {AimDistancePixels}";
}
