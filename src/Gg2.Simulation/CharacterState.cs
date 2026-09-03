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

    /// <summary>
    /// The intensity above which further additions are halved.
    /// </summary>
    /// <remarks>
    /// The reference's own comment: "afterburn intensity after which additional
    /// intsity additions are halved. it's called a 'leg' becauase of a similar
    /// concept in dynamic range compression." Typos and all — it is
    /// <c>Character.events/Create.xml:83</c>.
    /// </remarks>
    public const double BurnLegIntensity = 7;

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

    /// <summary>
    /// <c>yprevious</c>: the y this character finished the previous End Step at.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Written by <c>End Step</c> and read only by the dropdown-platform
    /// push-out</b>, which needs to know whether the character was already
    /// inside a platform before this tick's move. <c>End Step.xml:29-30</c>
    /// assigns <c>xprevious</c> and <c>yprevious</c> explicitly, straight after
    /// the stair descent and <em>before</em> the death exit, so a dead character
    /// gets one too. <c>xprevious</c> is not carried here because nothing reads
    /// it.
    /// </remarks>
    public double YPrevious;

    /// <summary><c>_last_xscale</c>: the facing as of the previous tick's Step.</summary>
    public int LastFacing;

    public bool CarryingIntel;

    /// <summary>Set while a jump is queued by the queue-jump client setting.</summary>
    /// <remarks>
    /// Written only under <see cref="QueueJump"/>, and read only by the arm of
    /// the jump test that same flag guards — so it is inert for a client that
    /// has not turned the setting on. It is cleared by every jump that actually
    /// fires, which is what stops one held press launching a second jump off the
    /// next landing.
    /// </remarks>
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
    /// <remarks>
    /// Counted up in <c>Character.events/Step.xml:25</c> and reset to zero by
    /// every damage path. It drives two things and nothing else: the medic
    /// beam's healing ramp (<see cref="Healing.HealingFactor"/>) and how fast a
    /// medic mends themselves (<see cref="Healing.Regenerate"/>).
    /// </remarks>
    public double TimeUnscathed;

    /// <summary>
    /// <c>deathmatch_invulnerable</c>: Team Deathmatch's spawn protection, and
    /// zero in every other mode.
    /// </summary>
    /// <remarks>
    /// <b>Not a tick count.</b> It is seeded from
    /// <c>global.tdmInvulnerabilityTicks</c>, which
    /// <c>GameServerCreate.gml:88</c> computes as
    /// <c>seconds * 30 * 0.2</c> — a fifth of the ticks — and then spent at
    /// <see cref="DeathmatchInvulnDecay"/> a tick, which starts at 0.2. Five
    /// seconds is 30 units spent at 0.2, which is 150 ticks. The two fifths
    /// cancel, and neither is redundant: <b>moving changes the rate and not the
    /// amount</b>.
    /// <para>
    /// ⚠️ <b>The guard on it is an exact <c>!= 0</c></b>
    /// (<c>dealDamage.gml:6</c>), which is safe only because End Step clamps it
    /// to exactly 0 the moment it goes non-positive
    /// (<c>Character.events/End Step.xml:134</c>). Without that clamp a
    /// character would be invulnerable for ever on a negative residue.
    /// </para>
    /// <para>
    /// ⚠️ It also decides <em>how</em> its owner respawns — see
    /// <see cref="PlayerState.DeathmatchRespawnBypass"/>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// <c>zoomed</c>: a sniper is looking down the scope.
    /// </summary>
    /// <remarks>
    /// Toggled by the <c>TOGGLE_ZOOM</c> client command and by nothing else —
    /// it is not a key in the input byte, which is why
    /// <c>serverToggleZoom.gml</c> exists as its own script for bots to call.
    /// <para>
    /// ⚠️ <b>It is not serialised.</b> A client infers it from the charge byte
    /// instead: <c>Character.events/User Event 13.xml:42</c> is
    /// <c>if(currentWeapon.t) zoomed = true; // Quick hack</c>. So a sniper who
    /// scopes and has not charged a single tick reads as unzoomed on every other
    /// screen, and the <c>TOGGLE_ZOOM</c> broadcast is what actually keeps them
    /// in step.
    /// </para>
    /// <para>
    /// ⚠️ <b>It changes the physics</b>, which is the part that is easy to
    /// miss: see <see cref="RunPower"/> and <see cref="JumpStrength"/>.
    /// </para>
    /// </remarks>
    public bool Zoomed;

    /// <summary>
    /// <c>runPower</c>: the acceleration this character actually uses.
    /// </summary>
    /// <remarks>
    /// <b>Per character rather than per class, and only one thing moves it.</b>
    /// <c>Character.events/Create.xml:158</c> is <c>runPower = baseRunPower</c>
    /// and <c>toggleZoom.gml</c> is the only reassignment in the game: a scoped
    /// sniper walks at <b>0.6</b> whatever their class value was.
    /// <para>
    /// ⚠️ <b><c>basemaxspeed</c> is NOT recomputed.</b>
    /// <c>Create.xml:159</c> derives it from <c>baseRunPower</c> once and
    /// <c>toggleZoom</c> does not touch it, which is why
    /// <see cref="ClassStats.BaseMaxSpeed"/> stays on the class and this does
    /// not.
    /// <para>
    /// ⚠️ <b>And it is an acceleration guard, not a speed cap</b> — easy to
    /// read backwards. A character's terminal speed comes from the friction
    /// balance, so a scoped sniper really does settle two thirds as fast
    /// (measured: 3.4 against 5.1). What not recomputing it changes is whether
    /// the push is applied <em>at all</em>: a scoped sniper travelling above
    /// their own equilibrium — thrown there by a blast — keeps accelerating,
    /// because the test is against the unscoped 5.1.
    /// </para>
    /// </para>
    /// <para>
    /// ⚠️ <b>0.6 is one of the five decimal literals GM8 parses a ULP above
    /// the IEEE nearest</b>, so the scoped value comes from
    /// <see cref="Gml.Gravity"/> and never from a C# <c>0.6</c>.
    /// </para>
    /// </remarks>
    public double RunPower;

    /// <summary>
    /// <c>jumpStrength</c>: what a jump sets <c>vspeed</c> to, negated.
    /// </summary>
    /// <remarks>
    /// <c>baseJumpStrength = 8+(0.6/2)</c> and a scoped sniper's is
    /// <c>6+(0.6/2)</c> — the same halved gravity correction, two units lower.
    /// Per character for the same reason <see cref="RunPower"/> is.
    /// </remarks>
    public double JumpStrength;

    /// <summary>
    /// GM8's instance id, near enough: which character was created first.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Slot order is NOT creation order, and the difference is
    /// observable.</b> A slot whose player has died and respawned holds a
    /// character <em>younger</em> than the ones in higher slots that have not —
    /// so a loop walking slots 0..7 visits them in an order GM8 would not.
    /// <para>
    /// It matters wherever the FIRST hit ends the search.
    /// <c>Projectiles.CollideOne</c> is the case that found it: a rocket
    /// explodes on the first character its collision event fires against, and
    /// GM8 fires those in ascending instance id. Measured on
    /// <c>reference-gg_debug-closedtdm.trace</c> at t509 — slot 6 respawned at
    /// t508 and slot 2 at t509, so the reference's rocket struck slot 6 and the
    /// port, walking slots, struck slot 2. Slot 6 finished the window 25 hp
    /// high: exactly a rocket's contact damage.
    /// </para>
    /// <para>
    /// Zero for a character the parity harness placed directly rather than
    /// spawned, which sorts it before everything and leaves slot order to break
    /// the tie — the behaviour those anchors had before this field existed.
    /// </para>
    /// </remarks>
    public uint CreationOrder;

    public double DeathmatchInvulnerable;

    /// <summary>
    /// <c>deathmatch_invuln_decay</c>: how fast
    /// <see cref="DeathmatchInvulnerable"/> is spent, per tick.
    /// </summary>
    /// <remarks>
    /// <b>0.2 on spawn and 1 the moment you do anything.</b> The reference's own
    /// comment on the 0.2 is "5x the normal invuln period when you first spawn",
    /// and <c>Character.events/Begin Step.xml</c> raises it to 1 on a jump
    /// (:98), a double jump (:109) and on <c>controlling</c> — pressing left or
    /// right (:270). So standing still keeps the protection five times as long
    /// as walking out of the spawn does.
    /// <para>
    /// ⚠️ <b>It is never lowered again.</b> Nothing writes 0.2 outside the
    /// Create event, so one step ends the slow decay for that whole life.
    /// </para>
    /// </remarks>
    public double DeathmatchInvulnDecay;

    /// <summary>
    /// <c>deathmatch_invuln_decay = 0.2</c>, the rate a fresh spawn starts at.
    /// </summary>
    public const double SpawnInvulnDecay = 0.2;

    /// <summary>
    /// <c>deathmatch_invuln_decay = 1</c>: the rate the moment its owner jumps
    /// or walks.
    /// </summary>
    public const double MovingInvulnDecay = 1;

    /// <summary>
    /// The ceiling <see cref="TimeUnscathed"/> counts up to: ten seconds.
    /// </summary>
    /// <remarks>
    /// Well past both thresholds either consumer has, so the cap is invisible —
    /// but it is what the reference writes, and a counter that ran forever would
    /// eventually be a different number in a saved state.
    /// </remarks>
    public const double MaxTimeUnscathed = 10 * 30;

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
    /// <c>realnumflames</c>: how many flames this character's fire is drawn
    /// with — and, not cosmetically, how many loose <c>Flame</c>s an airblast
    /// extinguish throws off them.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/End Step.xml:128</c> recomputes it every tick as
    /// <c>numFlames * burnDuration / maxDuration</c> — after the death exit, so
    /// it is one tick stale when the airblast (a Begin Step) reads it. A real,
    /// and the reference's <c>for (i = 0; i &lt; realnumflames; ...)</c> loops
    /// a fractional bound.
    /// </remarks>
    public double RealNumFlames;

    /// <summary><c>numFlames</c>: the flame count at full burn intensity.</summary>
    public const double NumFlames = 5;

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
    /// Spy: <c>stabbing</c>, whether a knife animation is holding this
    /// character still.
    /// </summary>
    /// <remarks>
    /// It gates the ground jump — <c>Character.events/Begin Step.xml:99</c>,
    /// <c>if(not stabbing)</c> — and it is the reason
    /// <c>player.humiliated</c>'s cloak drop has an exception. Set by
    /// <see cref="Weapons.SpecialStep"/>'s stab arm and cleared when
    /// <see cref="StabFreezeTimer"/> runs out.
    /// </remarks>
    public bool Stabbing;

    /// <summary>
    /// Spy: ticks left of the stab animation that is holding this character
    /// still.
    /// </summary>
    /// <remarks>
    /// <b>The reference spells this as an <em>instance</em> and it is not one
    /// here.</b> <c>StabAnim</c> is created by the Revolver, has no collision
    /// events, no damage and no position of its own, and does exactly one thing
    /// the simulation can see: its Destroy event restores its owner's
    /// <c>runPower</c>, <c>jumpStrength</c> and <c>stabbing</c>. A counter says
    /// the same thing and dies with its character for free — which is what
    /// <c>Revolver.events/Destroy.xml</c>'s <c>with(StabAnim)</c> block does.
    /// A second stab overwrites it, which is also what the reference's "don't
    /// re-allow movement if there is another StabAnim for the same player"
    /// guard amounts to.
    /// <para>
    /// ⚠️ <b>Its length is decided by a drawing variable</b>, which is why it
    /// is a constant here rather than something derivable:
    /// <c>StabAnim.events/Step.xml</c> ends the animation on
    /// <c>image_index &gt;= 32</c> and then fades an <c>alpha</c> out at 0.1 a
    /// tick. See <see cref="Weapons.StabFreezeTicks"/> for the arithmetic and
    /// for the one tick of it that is not certain.
    /// </para>
    /// </remarks>
    public int StabFreezeTimer;

    /// <summary>
    /// Engineer: build resource, 0 to 100. On the wire unscaled in an
    /// engineer's class-specific byte.
    /// </summary>
    public double NutsNBolts;

    /// <summary>
    /// <c>onCabinet</c>: whether this character was standing on a
    /// <c>HealingCabinet</c> at the end of the last tick.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/End Step.xml:104</c>, whose own comment is "for
    /// things polling whether the character is on a medcabinet". Exactly one
    /// thing polls it: <c>serverBuildSentry.gml</c> refuses to build on one.
    /// <para>
    /// ⚠️ <b>It is a tick behind by construction.</b> A build is a client
    /// command, processed outside the tick, so it reads what the previous End
    /// Step left — which is the reference's own behaviour and not a shortcut
    /// here.
    /// </para>
    /// </remarks>
    public bool OnCabinet;

    /// <summary><c>maxNutsNBolts</c>, from <c>Character.events/Create.xml</c>.</summary>
    public const double MaxNutsNBolts = 100;

    /// <summary>
    /// How fast the cloak fades in or out: 0.05 a tick, so twenty ticks each
    /// way.
    /// </summary>
    /// <remarks>
    /// A plain literal rather than a <see cref="Gml"/> constant — 0.05 is not
    /// one of the five decimal literals GM8 parses a ULP above the IEEE
    /// nearest. ⚠️ Twenty subtractions of it from 1 leave <b>exactly</b> 0
    /// here rather than a residue, because the reference clamps with
    /// <c>max(cloakAlpha - 0.05, 0)</c> and not by comparison.
    /// </remarks>
    public const double CloakFadeRate = 0.05;

    // --- intel ---------------------------------------------------------------

    /// <summary>Whether this character may pick the intel up right now.</summary>
    public bool CanGrabIntel;

    /// <summary><c>alarm[1]</c>: ticks until it may again. On the wire as a short.</summary>
    public int IntelGrabTimer;

    /// <summary>
    /// <c>intelRecharge</c>: how long the briefcase this character is carrying
    /// will take to return itself once it is dropped. On the wire as a short.
    /// </summary>
    /// <remarks>
    /// <b>A real, not a counter.</b> <c>Character.events/Begin Step.xml:334</c>
    /// adds <c>INTEL_MAX_TIMER/((3 + abs(min(hspeed, 7))/3.5)*30)</c> per tick,
    /// which is a whole number only when the carrier is standing exactly still.
    /// <see cref="Intel.Drop"/> is where it becomes an integer, through
    /// <see cref="Timers.SetAlarm"/>.
    /// </remarks>
    public double IntelRecharge;

    /// <summary>Roster slot of the medic beaming this character, or <see cref="Nobody"/>.</summary>
    public byte Healer;

    /// <summary>
    /// Medic: <c>alarm[11]</c>, ticks until the next step of self-regeneration.
    /// </summary>
    /// <remarks>
    /// <c>Objects/Characters/Medic.events/Alarm 11.xml</c>, and the only alarm
    /// in the game that belongs to one class alone. It is armed by that class's
    /// Create event and re-armed by its own body, so a living medic always has
    /// it running and every other class leaves it at
    /// <see cref="WeaponState.Inactive"/> forever.
    /// </remarks>
    public int RegenTimer;

    /// <summary>
    /// <c>taunting</c>: the character is playing its taunt animation and can do
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// Set by the taunt key on a fresh press
    /// (<c>Character.events/Begin Step.xml:102-107</c>) and cleared by
    /// <see cref="TauntIndex"/> reaching the end of the class's taunt sprite in
    /// End Step. ⚠️ <b>It is observable simulation, not decoration</b>: it
    /// suppresses acceleration, the attack, the right click and the cloak, and
    /// the stock client re-derives all of that from the same key bit — so a
    /// server that ignored the taunt key would keep walking a character the
    /// client has already stopped.
    /// <para>
    /// ⚠️ <b>The jump is the exception, and only on the tick the taunt
    /// starts.</b> GML evaluates <c>if(!taunting and !omnomnomnom)</c> once, at
    /// the top of the block; the taunt is set inside it and the jump is tested
    /// further down the same block, so a press of taunt-and-jump together jumps.
    /// Everything below that block — the right click at :137, the cloak at :147,
    /// the acceleration at :259 — re-reads the flag and is refused.
    /// </para>
    /// </remarks>
    public bool Taunting;

    /// <summary>
    /// <c>tauntindex</c>: the animation clock that decides when a taunt ends.
    /// </summary>
    /// <remarks>
    /// <c>End Step.xml:90-94</c> adds <c>tauntspeed*0.1</c> — 3 x 0.1, and
    /// written that way rather than as 0.3 because the two are different
    /// doubles and the sum is compared against a small integer — and the taunt
    /// ends at <c>tauntindex &gt;= sprite_get_number(tauntsprite)</c>. So the
    /// duration is the class's taunt sprite frame count, and nothing else:
    /// <see cref="ClassStats.TauntFrames"/>.
    /// <para>
    /// ⚠️ <b>Reset by the key even when the taunt does not start.</b> The
    /// reference sets <c>tauntindex = 0</c> outside the <c>!invisible and
    /// cloakAlpha == 1</c> test, so a cloaked spy pressing taunt rewinds the
    /// counter without taunting.
    /// </para>
    /// </remarks>
    public double TauntIndex;

    /// <summary>
    /// The heavy is eating: <c>omnomnomnom</c>, from <c>serverEatSandvich.gml</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It suppresses movement, not just the attack.</b>
    /// <c>Character.events/Begin Step.xml:237</c> wraps the entire movement
    /// block in <c>if(!taunting &amp;&amp; !omnomnomnom)</c>, so an eating heavy
    /// cannot accelerate left or right at all — and :86, :91 and :124 take the
    /// attack, the taunt and the right click with it. A sandvich that only
    /// healed would be a different item.
    /// </remarks>
    public bool Omnomnomnom;

    /// <summary>
    /// <c>omnomnomnomindex</c>: the animation clock that also decides when
    /// eating stops.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A real, and the eat's whole duration lives in it.</b>
    /// <c>End Step.xml:85</c> adds <b>0.25</b> a tick and stops at
    /// <c>omnomnomnomend</c>, which <c>serverEatSandvich</c> sets to <b>32</b> —
    /// so eating lasts <b>128 ticks</b>, a little over four seconds, and it is
    /// an animation counter rather than a timer that carries it. Not on the
    /// wire: a client starts its own on the <c>OMNOMNOMNOM</c> broadcast.
    /// </remarks>
    public double OmnomnomnomIndex;

    /// <summary>
    /// <c>canEat</c>: the sandvich is off cooldown.
    /// </summary>
    public bool CanEat;

    /// <summary>
    /// <c>alarm[6]</c>: ticks until <see cref="CanEat"/> comes back.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>45 seconds, not the 10 the reference's own comment claims.</b>
    /// <c>Create.xml:125</c> sets <c>eatCooldown = 1350</c> and the client's
    /// <c>OMNOMNOMNOM</c> case is commented "10 second cooldown" beside the line
    /// that uses the variable. The number is the specification; the comment is
    /// wrong in the reference and copying it would import the bug.
    /// <para>
    /// ⚠️ <b>Re-armed every tick of the eat while hurt</b>, not once at the
    /// start — <c>End Step.xml:87-91</c>, whose own comment explains why: "this
    /// should prevent the 'ate and got hit but didn't refresh cooldown' bug". So
    /// a heavy damaged on the last tick of eating pays the full 45 seconds from
    /// there.
    /// </para>
    /// </remarks>
    public int EatTimer;

    /// <summary>
    /// <c>xscale</c>: the facing captured when this heavy started eating.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Eating freezes which way you face, and that is not only cosmetic
    /// here.</b> <c>End Step.xml:86</c> writes <c>image_xscale = xscale</c> on
    /// every tick of the eat, <em>after</em> the line that derives the facing
    /// from the aim — so an eating heavy keeps pointing where they were when
    /// they started. The port has to carry it because <c>Aabb.ForCharacter</c>
    /// inflates a character's box by two pixels on the side it faces, and that
    /// box is what <c>distance_to_object</c> measures splash damage against.
    /// So a frozen facing changes who a rocket hurts.
    /// </remarks>
    public int EatFacing;

    /// <summary>
    /// <c>omnomnomnomindex += 0.25</c> a tick, <c>End Step.xml:85</c>.
    /// </summary>
    public const double OmnomnomnomRate = 0.25;

    /// <summary>
    /// <c>tauntspeed * 0.1</c> per tick — <c>tauntspeed</c> is 3 for every class
    /// (<c>Character.events/Create.xml:113</c>).
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Written as the product, not as 0.3.</b> GM8 holds <c>3*0.1</c> and
    /// the literal <c>0.3</c> as different doubles, and <see cref="TauntIndex"/>
    /// accumulates this thirty to sixty times before being compared against an
    /// integer frame count — so which side of the comparison the last tick falls
    /// on can differ. Transcribe the expression.
    /// </remarks>
    public const double TauntRate = 3 * 0.1;

    /// <summary>
    /// <c>omnomnomnomend = 32</c>, set by <c>serverEatSandvich.gml</c>.
    /// </summary>
    /// <remarks>
    /// With <see cref="OmnomnomnomRate"/> that is <b>128 ticks</b> of eating.
    /// The end is written by the command rather than the Create event, but no
    /// caller ever writes anything else, so it is a constant here.
    /// </remarks>
    public const double OmnomnomnomEnd = 32;

    /// <summary>
    /// <c>hp += 1.6</c> a tick while eating, <c>End Step.xml:93</c>.
    /// </summary>
    /// <remarks>
    /// Over the full 128 ticks that is <b>204.8</b>, comfortably more than a
    /// heavy's 200 — so an uninterrupted sandvich is a full heal from any
    /// survivable amount of damage, and the duration is what limits it rather
    /// than the total.
    /// </remarks>
    public const double OmnomnomnomHeal = 1.6;

    /// <summary>
    /// <c>eatCooldown = 1350</c> — 45 seconds. <c>Create.xml:125</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference's own comment beside the client's use of it says "10
    /// second cooldown" and is wrong.</b> 1350 ticks at 30 Hz is 45 seconds. The
    /// value is the specification.
    /// </remarks>
    public const double EatCooldown = 1350;

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

    /// <summary>
    /// <c>global.mapchanging</c>, mirrored onto every character once a tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A copy of a world flag, and the only one on this struct.</b>
    /// <c>charSetSolids</c> reads a global, and <see cref="Filter"/> has to
    /// answer for it — but the whole of <see cref="CharacterPhysics"/> takes a
    /// <c>ref CharacterState</c> and has no <see cref="WorldState"/> to ask.
    /// Threading the world through every one of those signatures for one bool
    /// that changes twice a match is the worse trade.
    /// <para>
    /// <b>It has exactly one writer</b>, the top of
    /// <see cref="MatchSimulation.Tick"/>, so it cannot drift within a tick.
    /// A test that builds a character by hand and never ticks gets <c>false</c>,
    /// which is the state a match is in for all but ten seconds of it.
    /// </para>
    /// </remarks>
    public bool MapChanging;

    /// <summary>
    /// <c>player.humiliated</c>: this character is on the team that just lost an
    /// Arena round, mirrored beside <see cref="MapChanging"/> and written by the
    /// same line.
    /// </summary>
    /// <remarks>
    /// Arena and nothing else. <c>doEventArenaEndRound.gml:26</c> sets it on
    /// every player not on the winning side and the next
    /// <c>doEventArenaRestart</c> or <c>doEventArenaWaitForPlayers</c> clears
    /// it, so it is true only during the ten seconds the scoreboard is up.
    /// <para>
    /// It costs 0.2 of control (<em>instead of</em> the intel's 0.1, not as
    /// well as), suppresses the attack, the taunt, the right click, the cloak
    /// and the sandvich, and withholds the round-end ammo refill. The state
    /// lives on <see cref="PlayerState.Humiliated"/>; this is the per-tick copy
    /// the physics reads.
    /// </para>
    /// </remarks>
    public bool Humiliated;

    /// <summary>
    /// <c>player.queueJump</c>, mirrored beside <see cref="MapChanging"/> and
    /// written by the same line.
    /// </summary>
    /// <remarks>
    /// The client's queued-jumping preference. It gates both halves of the
    /// feature in <c>Character.events/Begin Step</c>: the block that records
    /// <see cref="WantToJump"/> from the jump key's press and release, and the
    /// <c>or (player.queueJump and wantToJump)</c> arm of the jump test itself.
    /// The state lives on <see cref="PlayerState.QueueJump"/>; this is the
    /// per-tick copy the physics reads.
    /// </remarks>
    public bool QueueJump;

    /// <summary>
    /// <c>areSetupGatesClosed()</c>, mirrored beside <see cref="MapChanging"/>
    /// and written by the same line.
    /// </summary>
    /// <remarks>
    /// <c>global.setupTimer &gt; 0</c>. It had been permanently false since
    /// phase 3 — the port had no setup timer to drive it — so every
    /// <c>ControlPointSetupGate</c> on an Invasion or attack/defend map was open
    /// from the first tick. Nothing noticed, because no fixture is on either
    /// kind of map.
    /// </remarks>
    public bool SetupGatesClosed;

    /// <summary>
    /// <c>ArenaHUD.state == ARENA_STATE_ROUND_END</c>, mirrored beside
    /// <see cref="MapChanging"/> and written by the same line.
    /// </summary>
    /// <remarks>
    /// The reference tests it together with <c>global.mapchanging</c> in every
    /// place either appears, and this is the one of those places that lives on
    /// the character: <c>Begin Step.xml:404</c>'s ammo refill.
    /// </remarks>
    public bool ArenaRoundEnded;

    /// <summary>
    /// <c>cappingPoint</c>: which control point this character is standing on,
    /// or <see cref="Nobody"/>.
    /// </summary>
    /// <remarks>
    /// An index into <see cref="WorldState.ControlPoints"/> rather than the
    /// reference's instance id — the same substitution every roster slot on this
    /// struct makes, and for the same reason. Written in Begin Step by
    /// <see cref="ControlPoints.BeginStep"/> and read in Step by the point
    /// itself.
    /// </remarks>
    public byte CappingPoint;

    /// <summary>A collision filter describing this character.</summary>
    public readonly CollisionFilter Filter => new()
    {
        Team = Team,
        CarryingIntel = CarryingIntel,
        IgnoreDropdown = (KeyState & PlayerKeys.Down) != 0,
        MapChanging = MapChanging,
        SetupGatesClosed = SetupGatesClosed,
    };

    /// <summary>
    /// Applies an arriving <c>INPUTSTATE</c> — the character's
    /// <c>User Event 1</c>.
    /// </summary>
    /// <summary>
    /// A character as <c>Character.events/Create.xml</c> leaves one, before a
    /// position is put on it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A zeroed <see cref="CharacterState"/> is not a valid character, and
    /// the ways it is invalid are all silent.</b> Six of these fields name a
    /// roster slot and their "nobody" is <see cref="Nobody"/> — <b>255</b> — so
    /// a default-constructed character claims that slot 0 last hurt it, assisted
    /// in hurting it, set it on fire and is healing it. Slot 0 is the server's
    /// own reserved player and always exists, so none of that fails loudly; it
    /// just credits the wrong person.
    /// <para>
    /// It exists so that the two places that make a character from nothing —
    /// <see cref="WorldState.Spawn"/> and the parity harness's anchor — cannot
    /// drift apart. They did: the anchor built a bare <c>new CharacterState()</c>
    /// and every character it created carried slot 0 in all six fields, which
    /// surfaced as a <c>PLAYER_DEATH</c> naming an assistant the reference said
    /// was nobody.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠️ <b><see cref="RunPower"/> and <see cref="JumpStrength"/> joined this
    /// list when the sniper's scope landed.</b> They are per-character in the
    /// reference and a zeroed one stands still and never jumps, which is silent
    /// rather than loud — it looks like a physics bug, not a missing field.
    /// </remarks>
    public static CharacterState Fresh(PlayerClass playerClass, Team team) => new()
    {
        Class = playerClass,
        Team = team,
        Hp = ClassStats.For(playerClass).MaxHp,
        Facing = 1,
        LastFacing = 1,

        // Create.xml:124. Every class carries it, because every class runs the
        // same Create event — only a heavy can ever spend it.
        CanEat = true,
        EatTimer = Timers.Inactive,

        // Create.xml:15 and :158 — both start at their base and only
        // toggleZoom ever moves them. A zeroed character would stand still and
        // never jump, which is the CharacterState.Fresh trap in yet another
        // costume.
        RunPower = ClassStats.For(playerClass).RunPower,
        JumpStrength = ClassStats.BaseJumpStrength,

        LastDamageDealer = Nobody,
        SecondToLastDamageDealer = Nobody,
        BurnedBy = Nobody,
        AfterburnSource = DamageSource.None,
        LastDamageSource = DamageSource.None,
        Healer = Nobody,

        // `cappingPoint = noone` — Create.xml never writes it, so it is GM8's
        // uninitialised -1 until the first Begin Step. Nobody here, for the
        // same reason every other roster-shaped field on this struct uses it.
        CappingPoint = Nobody,

        // And the three that start at something other than zero: cloakAlpha = 1
        // (fully visible), canGrabIntel = true, and an engineer spawns with a
        // full 100 nuts and bolts.
        CloakAlpha = 1,
        CanGrabIntel = true,
        NutsNBolts = 100,

        // Character.events/Create.xml:79: `realnumflames = numFlames` — it
        // rests at 5 until the first End Step recomputes it from burnDuration.
        RealNumFlames = NumFlames,

        // Medic.events/Create.xml: `alarm[11] = 30`. Every other class leaves it
        // at GM8's resting -1 — which Timers.Advance also reaches from a
        // defaulted 0 on the first tick, but saying it here means a fresh
        // character never has an alarm that is merely about to become inert.
        RegenTimer = playerClass == PlayerClass.Medic ? 30 : WeaponState.Inactive,
    };

    /// <summary>
    /// <c>Scripts/Damage API/dealFlicker.gml</c>: taking damage shows a spy
    /// through their cloak for three ticks.
    /// </summary>
    /// <remarks>
    /// Applied to every class, as the reference does — a non-spy simply never
    /// has a <see cref="CloakAlpha"/> below 1 for it to raise. The timer is
    /// <c>alarm[7]</c>, and <see cref="Timers"/> guards the class on the way
    /// back out.
    /// </remarks>
    public void DealFlicker()
    {
        CloakAlpha = Math.Max(CloakAlpha, 0.3);
        CloakFlicker = true;
        CloakFlickerTimer = 3;
    }

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
