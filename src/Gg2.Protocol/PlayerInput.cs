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
    /// Distance saturates at 255 rather than wrapping — the client writes
    /// <c>min(255, distance / 2)</c> (<c>Scripts/Message Builders/ClientInputstate.gml</c>),
    /// and a wrap would turn a long-range shot into a point-blank one.
    /// <para>
    /// <b>The rounding here is not verified against GM8.</b> The client passes a
    /// fractional real to the Faucet Networking <c>write_ushort</c>/<c>write_ubyte</c>
    /// and how that extension converts to an integer has not been checked
    /// (docs/SIMULATION.md, "What is genuinely uncertain"). It does not affect
    /// wire compatibility: inbound aim is carried verbatim and decoded exactly,
    /// and this direction is only used when the port <em>chooses</em> an aim for
    /// a bot — where any consistent quantisation is a valid action.
    /// </para>
    /// </remarks>
    public static PlayerInput FromAim(PlayerKeys keys, double directionDegrees, double distancePixels)
    {
        var wrapped = directionDegrees % 360.0;
        if (wrapped < 0)
            wrapped += 360.0;

        var net = (ushort)((int)Math.Round(wrapped * 65536.0 / 360.0) & 0xFFFF);
        var distance = (byte)Math.Clamp(Math.Round(distancePixels / 2.0), 0, 255);

        return new PlayerInput(keys, net, distance);
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
