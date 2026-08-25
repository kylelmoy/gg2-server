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
/// <para>
/// <b>Where a field names another player it is a roster slot, never a
/// reference.</b> That is what keeps this a value type, and it inherits the
/// reference's own guarantee: the fields below hold <em>player</em> instances in
/// the GML, not characters, so they stay meaningful after the player they name
/// has died. <see cref="Nobody"/> is the empty one.
/// </para>
/// </remarks>
public struct CharacterState
{
    /// <summary>
    /// No roster slot: the reference's <c>noone</c>, and 255 on the wire.
    /// </summary>
    /// <remarks>
    /// Zero would name roster slot 0, which is the server's own reserved player
    /// and exists even in a headless server — so a "nobody" that defaulted to 0
    /// would quietly credit it with every kill.
    /// </remarks>
    public const byte Nobody = 255;

    /// <summary>Afterburn intensity ceiling, in damage per second.</summary>
    public const double MaxBurnIntensity = 13;

    /// <summary>Afterburn duration ceiling, in ticks.</summary>
    public const double MaxBurnDuration = 210;

    /// <summary>Ticks from the last ignition before the intensity starts falling.</summary>
    public const int BurnDecayDelay = 90;

    /// <summary>Ticks the intensity takes to reach zero once it starts falling.</summary>
    public const int BurnDecayDuration = 90;

    /// <summary>Ticks of duration burned off per tick.</summary>
    public const double BurnDurationDecay = 1;

    public PlayerClass Class;

    public Team Team;

    public double X;

    public double Y;

    /// <summary>
    /// GM8's <c>hspeed</c>, <c>vspeed</c>, <c>direction</c> and <c>speed</c>,
    /// all four of them and all four stored.
    /// </summary>
    /// <remarks>
    /// Public so a rollout can copy a character wholesale, but write through
    /// <see cref="HSpeed"/> and <see cref="VSpeed"/> rather than reaching past
    /// them — assigning a component is what keeps <c>direction</c> honest, and
    /// phase 5's knockback reads it
    /// (<c>Rocket.events/User Event 5.xml</c>'s <c>motion_add</c>).
    /// </remarks>
    public Motion Motion;

    /// <summary>GM8's <c>hspeed</c>. Writing it rewrites direction and speed.</summary>
    public double HSpeed
    {
        readonly get => Motion.HSpeed;
        set => Motion.HSpeed = value;
    }

    /// <summary>GM8's <c>vspeed</c>. Writing it rewrites direction and speed.</summary>
    public double VSpeed
    {
        readonly get => Motion.VSpeed;
        set => Motion.VSpeed = value;
    }

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
    /// <para>
    /// It does affect two combat answers, in opposite directions:
    /// <c>distance_to_object</c> measures against the facing-inflated box and
    /// <c>collision_line</c> effectively does not. See
    /// <see cref="Aabb"/> and <see cref="LineCollider"/>.
    /// </para>
    /// </remarks>
    public int Facing;

    /// <summary><c>_last_xscale</c>: the facing as of the previous tick's Step.</summary>
    public int LastFacing;

    public bool CarryingIntel;

    /// <summary>Set while a jump is queued by the queue-jump client setting.</summary>
    public bool WantToJump;

    // --- damage bookkeeping --------------------------------------------------
    //
    // Who gets the kill, who gets the assist, and what the kill log draws. All
    // of it is written by whatever dealt the damage and expired by the two
    // timers; the GM8 event comments on Character.events/Alarm 3.xml and
    // Alarm 4.xml name them "Finish Off Expiration" and "Kill Assist
    // Expiration".

    /// <summary>Roster slot of the last player to damage this character, or <see cref="Nobody"/>.</summary>
    public byte LastDamageDealer;

    /// <summary>What that damage was. On the wire in the <c>PLAYER_DEATH</c> that follows.</summary>
    public DamageSource LastDamageSource;

    /// <summary>Roster slot of the dealer before that one — the assist — or <see cref="Nobody"/>.</summary>
    public byte SecondToLastDamageDealer;

    /// <summary>
    /// <c>alarm[3]</c>: ticks until <see cref="LastDamageDealer"/> stops
    /// counting. Every hit resets it to
    /// <see cref="GameConstants.AssistTime"/>.
    /// </summary>
    public int FinishOffTimer;

    /// <summary>
    /// <c>alarm[4]</c>: ticks until <see cref="SecondToLastDamageDealer"/> stops
    /// counting.
    /// </summary>
    /// <remarks>
    /// Set from whatever <see cref="FinishOffTimer"/> had left rather than from
    /// <see cref="GameConstants.AssistTime"/>: the demoted dealer inherits the
    /// remainder of the window it already had, it does not get a fresh one.
    /// </remarks>
    public int AssistTimer;

    /// <summary>Ticks since this character last took damage.</summary>
    public double TimeUnscathed;

    // --- afterburn -----------------------------------------------------------
    //
    // Two quantities and not one: "heat" decides damage per second and "fuel"
    // decides how long it lasts, and a fresh flame tops the two up
    // independently (Character.events/Create.xml).

    /// <summary>
    /// <c>burnIntensity</c>: afterburn damage per second, capped at
    /// <see cref="MaxBurnIntensity"/>.
    /// </summary>
    public double BurnIntensity;

    /// <summary><c>burnDuration</c>: ticks of afterburn left.</summary>
    public double BurnDuration;

    /// <summary>
    /// <c>intensityDecay</c>: how fast the intensity bleeds off once
    /// <see cref="AfterburnDecayTimer"/> has run out. Recomputed as
    /// <c>burnIntensity / <see cref="BurnDecayDuration"/></c> when it does.
    /// </summary>
    public double IntensityDecay;

    /// <summary>
    /// <c>alarm[0]</c>: ticks from the last ignition until the intensity starts
    /// falling.
    /// </summary>
    public int AfterburnDecayTimer;

    /// <summary>Roster slot that set the fire, or <see cref="Nobody"/>.</summary>
    /// <remarks>
    /// Deliberately not <see cref="LastDamageDealer"/>: afterburn keeps ticking
    /// after somebody else has hit the victim, and it still has to credit the
    /// pyro when it finishes them off.
    /// </remarks>
    public byte BurnedBy;

    /// <summary>What to blame the afterburn on — flamethrower or flare.</summary>
    public DamageSource AfterburnSource;

    // --- uber, cloak, and the class-specific wire byte -------------------------

    /// <summary><c>ubered</c>: invulnerable. Every damage path in the reference tests it.</summary>
    public bool Ubered;

    /// <summary><c>alarm[2]</c>: ticks of uber left.</summary>
    public int UberTimer;

    /// <summary>Spy: whether the cloak is engaged.</summary>
    public bool Cloak;

    /// <summary>
    /// Spy: how far the cloak has faded, in <c>[0, 1]</c>. On the wire as
    /// <c>cloakAlpha*255</c> in a spy's class-specific byte.
    /// </summary>
    public double CloakAlpha;

    /// <summary>Spy: whether a hit has just flickered the cloak back into view.</summary>
    public bool CloakFlicker;

    /// <summary><c>alarm[7]</c>: ticks until the flicker clears.</summary>
    public int CloakFlickerTimer;

    /// <summary>
    /// Engineer: build resource, 0 to 100. On the wire unscaled in an
    /// engineer's class-specific byte.
    /// </summary>
    public double NutsNBolts;

    // --- intel ---------------------------------------------------------------

    /// <summary>Whether this character may pick the intel up right now.</summary>
    public bool CanGrabIntel;

    /// <summary><c>alarm[1]</c>: ticks until it may again. On the wire as a short.</summary>
    public int IntelGrabTimer;

    /// <summary><c>intelRecharge</c>. On the wire as a short.</summary>
    public int IntelRecharge;

    /// <summary>Roster slot of the medic beaming this character, or <see cref="Nobody"/>.</summary>
    public byte Healer;

    public readonly ClassStats Stats => ClassStats.For(Class);

    public readonly CharacterBox Box => CharacterBox.For(Class);

    /// <summary>
    /// GM8's <c>speed</c>. Equal to <c>sqrt(hspeed^2 + vspeed^2)</c> for as long
    /// as nothing assigns it directly, which is every path phase 4 has.
    /// </summary>
    public readonly double Speed => Motion.Speed;

    /// <summary>
    /// GM8's <c>direction</c>, in degrees. <b>Not the aim direction</b>: this is
    /// where the character is travelling and <see cref="AimDirection"/> is where
    /// it is pointing.
    /// </summary>
    public readonly double Direction => Motion.Direction;

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
