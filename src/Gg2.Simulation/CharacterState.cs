using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// One character's simulation state.
/// </summary>
/// <remarks>
/// A flat struct of value types with no references out of it, so cloning a world
/// is a <c>memcpy</c> rather than a graph walk — docs/BOTAI.md item 1, which is
/// the one item on that list that cannot be retrofitted.
/// <para>
/// <b><c>double</c> everywhere.</b> GML has exactly one numeric type and it is an
/// IEEE-754 double; a <c>float</c> here is a bug, not an optimisation
/// (docs/SIMULATION.md, "Float rules").
/// </para>
/// </remarks>
public struct CharacterState
{
    public PlayerClass Class;

    public Team Team;

    public double X;

    public double Y;

    public double HSpeed;

    public double VSpeed;

    /// <summary>
    /// Gravity accumulated this tick, applied half before the move and half
    /// after, then zeroed. This is what makes the arc framerate-independent.
    /// </summary>
    public double AppliedGravity;

    public double Hp;

    // --- input ---------------------------------------------------------------

    public PlayerKeys KeyState;

    /// <summary>Keys that went down since the last input was applied.</summary>
    /// <remarks>
    /// Movement uses <c>keyState | pressedKeys</c>, not <c>keyState</c> alone,
    /// so a key tapped and released inside one tick still moves the character.
    /// Cleared at the end of Begin Step.
    /// </remarks>
    public PlayerKeys PressedKeys;

    public PlayerKeys ReleasedKeys;

    public PlayerKeys LastKeyState;

    public ushort NetAimDirection;

    public byte AimDistance;

    // --- movement state ------------------------------------------------------

    /// <summary>0 normal, 1 self rocket jump, 2 enemy, 3 airblast, 4 team.</summary>
    public int MoveStatus;

    public bool OnGround;

    public bool ObstacleBelow;

    public bool DoubleJumpUsed;

    public bool Spinjumping;

    /// <summary>Whether a direction key is held and not cancelled by the other.</summary>
    public bool Controlling;

    /// <summary>
    /// <c>image_xscale</c>: +1 facing right, -1 facing left. Set from the aim
    /// direction in End Step.
    /// </summary>
    /// <remarks>
    /// It does <b>not</b> affect the collision box — see
    /// <see cref="CollisionWorld.PlaceFree(in CharacterBox, double, double, in CollisionFilter)"/>.
    /// It is here because the spinjump test compares it against its previous
    /// value.
    /// </remarks>
    public int Facing;

    /// <summary><c>_last_xscale</c>: the facing as of the previous tick's Step.</summary>
    public int LastFacing;

    public bool CarryingIntel;

    /// <summary>Set while a jump is queued by the queue-jump client setting.</summary>
    public bool WantToJump;

    public readonly ClassStats Stats => ClassStats.For(Class);

    public readonly CharacterBox Box => CharacterBox.For(Class);

    /// <summary>GM8's <c>speed</c>: derived from the two components, never stored.</summary>
    public readonly double Speed => Math.Sqrt(HSpeed * HSpeed + VSpeed * VSpeed);

    /// <summary>A collision filter describing this character.</summary>
    public readonly CollisionFilter Filter => new()
    {
        Team = Team,
        CarryingIntel = CarryingIntel,
        IgnoreDropdown = (KeyState & PlayerKeys.Down) != 0,
    };

    /// <summary>
    /// Applies an arriving <c>INPUTSTATE</c> — the character's
    /// <c>User Event 1</c>.
    /// </summary>
    public void ApplyInput(PlayerInput input)
    {
        KeyState = input.Keys;
        NetAimDirection = input.NetAimDirection;
        AimDistance = input.AimDistance;

        PressedKeys |= KeyState & ~LastKeyState;
        ReleasedKeys |= ~KeyState & LastKeyState;
        LastKeyState = KeyState;
    }

    /// <summary>Aim direction in degrees, <c>[0, 360)</c>.</summary>
    public readonly double AimDirection => NetAimDirection * 360.0 / 65536.0;
}
