using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The Medigun: the beam, the charge, and the eight seconds it buys.
/// </summary>
/// <remarks>
/// Four of the reference's events collapse into this file, because in GML they
/// are one instance's event list and here they are one weapon's rules:
/// <list type="number">
/// <item>
/// <c>Objects/Weapons/Medigun.events/User Event 1.xml</c> — reached from
/// <c>Character.events/Begin Step.xml:96-100</c>, the <em>attack</em> key. For
/// every other weapon that event fires a shot; for this one it picks a patient
/// and heals them, and no <c>WEAPON_FIRE</c> is ever broadcast for it.
/// </item>
/// <item>
/// <c>Objects/Weapons/Medigun.events/Begin Step.xml</c> — the drain, which is a
/// separate instance's event and so runs <em>after</em> the whole of its
/// owner's Begin Step. See <see cref="UberDrainStep"/>.
/// </item>
/// <item>
/// <c>Objects/Weapons/Medigun.events/Step.xml</c> — the beam falls off the
/// moment a tick goes by without it healing. See <see cref="DecayStep"/>.
/// </item>
/// <item>
/// <c>Objects/Characters/Medic.events/Alarm 11.xml</c> — the medic's own
/// regeneration, which is a property of the class rather than of the weapon but
/// belongs to the same feature. See <see cref="Regenerate"/>.
/// </item>
/// </list>
/// <para>
/// <b>The beam is not a projectile and it is not a hitscan.</b> It is a stored
/// target — <c>healTarget</c>, a <em>player</em> — reacquired only when there is
/// none, and re-validated every tick against range and line of sight. That
/// makes it the one weapon whose per-tick effect depends on a decision taken
/// many ticks earlier, and the reason <see cref="WeaponState.HealTarget"/> is on
/// the wire at all.
/// </para>
/// <para>
/// ⚠️ <b>Two of the three distances in the acquisition are measured from
/// different points, and they are not interchangeable.</b> The aim ray leaves
/// the <em>weapon</em> (<c>other.x</c> inside the <c>with(Character)</c>, and a
/// weapon trails its owner by a tick and a rounding — see
/// <see cref="WeaponState.X"/>), while the reconstructed mouse position is
/// measured from the <em>character</em>. Using one for both moves the beam by up
/// to half a pixel plus a tick of travel, which is enough to pick a different
/// patient out of a crowd.
/// </para>
/// </remarks>
public static class Healing
{
    /// <summary>How far the beam reaches, and the range of the acquisition ray.</summary>
    private const double MaxHealDistance = 300;

    /// <summary>
    /// The radius around the cursor inside which a candidate is scored on
    /// <em>mouse</em> distance rather than on healer distance.
    /// </summary>
    /// <remarks>
    /// The reference's own comment: "Heal target selection will favor the
    /// character closest to the mouse, as long as it is within this distance of
    /// the mouse cursor." The two score bands do not overlap — a candidate near
    /// the cursor scores in [2, 3] and one merely in range scores in [0, 1] — so
    /// anybody under the cursor beats everybody who is not.
    /// </remarks>
    private const double MaxMouseSelectDistance = 150;

    /// <summary>Health per tick before the ramp and the tier factor. 20 a second.</summary>
    private const double HealAmount = 2.0 / 3.0;

    /// <summary>Above this the beam slows down.</summary>
    private const double HealTierHealth = 125;

    /// <summary>What it slows down to. Stacks with the ramp.</summary>
    private const double HealTierAmount = 0.75;

    /// <summary>Charge per tick while the patient is hurt.</summary>
    private const double UberChargeHurt = 2.5;

    /// <summary>Charge per tick while the patient is already full.</summary>
    private const double UberChargeHealthy = 1.75;

    /// <summary>
    /// Charge spent per tick while ubering: 2000 of it lasts 240 ticks, eight
    /// seconds.
    /// </summary>
    private const double UberDrain = 25.0 / 3.0;

    /// <summary>
    /// <c>alarm[2]</c>, re-armed every tick of the uber so that it expires three
    /// ticks after the beam or the drain stops.
    /// </summary>
    private const int UberedTicks = 3;

    /// <summary>
    /// <c>alarm[0]</c> after an acquisition attempt: the medic may try again on
    /// the tick after next.
    /// </summary>
    /// <remarks>
    /// One rather than the Medigun's <c>refireTime</c> of three, because the
    /// acquisition is not a shot — <c>User Event 1</c> sets
    /// <c>alarm[0] = 1</c> itself. Firing a needle uses the refire time instead.
    /// </remarks>
    private const int TargetSelectRefire = 1;

    /// <summary>Ticks between the medic's own regeneration steps.</summary>
    private const int RegenPeriod = 30;

    /// <summary>Under this much time unscathed a medic regenerates 3.</summary>
    private const double RegenFirstThreshold = 7 * 30;

    /// <summary>Under this much, 4; over it, 5.</summary>
    private const double RegenSecondThreshold = 14 * 30;

    /// <summary>
    /// <c>Objects/Weapons/Medigun.events/User Event 1.xml</c>: acquire, then
    /// heal.
    /// </summary>
    /// <remarks>
    /// Runs where every other weapon's <c>User Event 1</c> runs — inside the
    /// owner's Begin Step, on the attack key — and is reached from
    /// <see cref="Weapons.FireStep"/>. It is <em>not</em> guarded by that
    /// method's <c>readyToShoot and ammoCount &gt; 0</c>: that pair is the
    /// Scattergun's guard and the Medigun has neither, which is why a medic with
    /// an empty magazine still heals.
    /// </remarks>
    public static void BeamStep(CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        // `if(!instance_exists(healTarget) and readyToShoot)`. healTarget is a
        // player, and a player exists for as long as they are on the roster —
        // so the test is "no target", not "the target is alive". A patient who
        // died is dropped further down, by the `healTarget.object != -1` arm.
        if (state.Weapons[slot].HealTarget == WeaponState.NoTarget && state.Weapons[slot].ReadyToShoot)
            SelectTarget(world, ref state, slot);

        if (state.Weapons[slot].HealTarget != WeaponState.NoTarget)
            Beam(world, ref state, slot, ref events);
    }

    /// <summary>
    /// The <c>with(Character)</c> that picks a patient.
    /// </summary>
    /// <remarks>
    /// <b>Ties go to the last candidate examined</b> — the guard is
    /// <c>targetScore &lt; bestScore</c>, not <c>&lt;=</c>, so an equal score
    /// replaces the incumbent. The reference examines characters in instance
    /// order, which is creation order; this walks roster order, the same
    /// approximation <see cref="MatchSimulation"/> makes everywhere else. It can
    /// only differ when two team-mates score bit-for-bit identically, which
    /// needs them equidistant from both the weapon and the cursor.
    /// <para>
    /// <b>The acquisition happens whether or not it succeeds.</b>
    /// <c>readyToShoot</c> is cleared and <c>alarm[0]</c> re-armed outside the
    /// loop, so a medic who is pointing at nothing still pays the tick — and a
    /// medic pointing at a wall retries every other tick rather than every tick.
    /// </para>
    /// </remarks>
    private static void SelectTarget(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var medic = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        var aim = medic.AimDirection;

        // The client sends how far away it was pointing as one byte
        // (`aimDistance`) and the server rebuilds the cursor from it. lengthdir,
        // not cos/sin: it snaps, and a cursor held straight up has to land
        // exactly above the medic (see Gml.LengthDirX).
        var mouseX = medic.X + Gml.LengthDirX(medic.AimDistance, aim);
        var mouseY = medic.Y + Gml.LengthDirY(medic.AimDistance, aim);

        // ⚠️ Measured from the character, but the ray below is walked from the
        // *weapon*. That is the reference: `owner.x` here, `other.x` there.
        var rayEndX = medic.X + Gml.LengthDirX(MaxHealDistance, aim);
        var rayEndY = medic.Y + Gml.LengthDirY(MaxHealDistance, aim);

        var best = WeaponState.NoTarget;
        var bestScore = 0.0;

        for (var candidate = 0; candidate < WorldState.MaxSlots; candidate++)
        {
            // `if(id == other.owner or team != other.owner.team) continue;`
            if (!state.Alive[candidate] || candidate == slot)
                continue;

            ref var patient = ref state.Characters[candidate];

            if (patient.Team != medic.Team)
                continue;

            // Both distances run from the *candidate*, and only the first of the
            // two ends at the weapon.
            var healDistance = Gml.PointDistance(patient.X, patient.Y, weapon.X, weapon.Y);
            var mouseDistance = Gml.PointDistance(patient.X, patient.Y, mouseX, mouseY);

            if (Gml.Greater(healDistance, MaxHealDistance))
                continue;

            var score = Gml.LessOrEqual(mouseDistance, MaxMouseSelectDistance)
                ? 3 - (mouseDistance / MaxMouseSelectDistance)
                : 1 - (healDistance / MaxHealDistance);

            if (Gml.Less(score, bestScore))
                continue;

            // `collision_line(other.x, other.y, aimRayEndX, aimRayEndY, id, true,
            // false)` — is the medic pointing at this particular character? Note
            // it is the *character's* collider and not a place_meeting box: a
            // hitscan cannot reach the two pixels the facing inflation adds to
            // bbox_left, and LineCollider.ForCharacter is the half of the pair
            // that knows that.
            if (!CollisionLine.Hits(
                    LineCollider.ForCharacter(patient.Box, patient.X, patient.Y, patient.Facing),
                    default(CollisionLine.FilledMask),
                    weapon.X,
                    weapon.Y,
                    rayEndX,
                    rayEndY))
                continue;

            // `collision_line(..., Obstacle, true, true)` — anything in the way.
            // Obstacle is map geometry and nothing else: gates, player walls and
            // bullet walls are separate objects, so a medic can beam a team-mate
            // through their own gate.
            if (world.CollisionLineHitsMask(weapon.X, weapon.Y, patient.X, patient.Y))
                continue;

            best = (byte)candidate;
            bestScore = score;
        }

        weapon.HealTarget = best;
        weapon.ReadyToShoot = false;
        weapon.RefireTimer = TargetSelectRefire;
    }

    /// <summary>
    /// The second half of <c>User Event 1</c>: re-validate, then heal.
    /// </summary>
    /// <remarks>
    /// Every one of the three failure arms in the reference is a <em>drop</em>,
    /// and two of the three also clear the patient's <c>healer</c>. The one that
    /// does not is the dead patient — there is no character left to clear it on.
    /// </remarks>
    private static void Beam(CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ref var weapon = ref state.Weapons[slot];
        var target = weapon.HealTarget;

        // `if(healTarget.object != -1) ... else healTarget = noone;`
        if (!state.Alive[target])
        {
            weapon.HealTarget = WeaponState.NoTarget;
            return;
        }

        ref var patient = ref state.Characters[target];

        // Out of range, or something solid in the way. Two separate arms in the
        // reference with identical bodies; the order they are asked in is not
        // observable because both are pure.
        if (Gml.Greater(Gml.PointDistance(weapon.X, weapon.Y, patient.X, patient.Y), MaxHealDistance)
            || world.CollisionLineHitsMask(weapon.X, weapon.Y, patient.X, patient.Y))
        {
            patient.Healer = CharacterState.Nobody;
            weapon.HealTarget = WeaponState.NoTarget;
            return;
        }

        // The flag DecayStep reads at the end of the tick. Set here and nowhere
        // else: a beam that reached its patient this tick keeps the target,
        // and any tick that does not reach it drops the target instead.
        weapon.HealedThisStep = true;

        // `if (healTarget.object.healer == -1)` — claimed, not taken. Two medics
        // beaming one patient means the first one to arrive is the one credited
        // with the assist when the patient gets a kill.
        if (patient.Healer == CharacterState.Nobody)
            patient.Healer = (byte)slot;

        // The beam puts out fires slowly: one tick of afterburn per tick of
        // healing, on top of the burn's own decay.
        if (Gml.Greater(patient.BurnDuration, 0))
            patient.BurnDuration -= 1;

        var healthGained = HealAmount * HealingFactor(patient.TimeUnscathed);

        // ⚠️ The tier test is against the hp the patient had *before* this
        // tick's heal, and the uber-charge test below is against the hp they
        // have *after* it. Same variable, two different readings, four lines
        // apart.
        if (Gml.Greater(patient.Hp, HealTierHealth))
            healthGained *= HealTierAmount;

        var maxHp = patient.Stats.MaxHp;
        patient.Hp = Math.Min(patient.Hp + healthGained, maxHp);

        // Medigun.events/User Event 1.xml:73-75. ⚠️ The stat takes the *gross*
        // amount, not the amount that actually landed — a medic beaming a
        // full-health patient banks every point of it. `hphealed` is the
        // weapon's own running total towards a score point, and the two lines
        // are always written together.
        weapon.HpHealed += healthGained;
        Scoring.Award(ref state, slot, StatIndex.Healing, healthGained);

        if (!weapon.Ubering && Gml.Less(weapon.UberCharge, WeaponState.MaxUberCharge))
        {
            // `if(healTarget.object.hp < healTarget.object.maxHp or setup)`.
            //
            // ⛔ KNOWN GAP: the `setup` clause is NOT implemented, so during a
            // setup timer a medic beaming a full-health patient charges at 1.75
            // where the reference charges at 2.5.
            //
            // ⚠️ The comment here used to say "No mode this port runs has a
            // ControlPointSetupGate", and that has been FALSE since phase 6.
            // Medigun User Event 1.xml:92-98 computes `setup` as
            // `instance_exists(ControlPointSetupGate) && areSetupGatesClosed()`,
            // and both halves exist here: the map entity is `SetupGate`
            // (builder_init.gml:301 creates a ControlPointSetupGate from it), so
            // the first half is `state.AttackDefend`, and the second is
            // `state.SetupTimer > 0` — already mirrored onto every character as
            // `SetupGatesClosed` by MatchSimulation. It is reachable on shipped
            // maps: Rounds.Start arms the timer for Invasion (ctf_avanti) and
            // for attack/defend control points.
            //
            // Closing it is one condition — `|| (state.AttackDefend &&
            // character.SetupGatesClosed)` — and it is left open deliberately
            // rather than for want of state: it is OBSERVABLE simulation, so it
            // lands with the parity fixtures re-run, the three pinned bot
            // measurements re-recorded, and a HAND bump of
            // SimulationStamp.SimulationBehaviourVersion.
            //
            // ⛔ The stamp will NOT catch this one by itself. Its surface is
            // "chosen, not swept" — constants and tables, not branch conditions
            // — so a changed charge rate here moves no hash and invalidates no
            // checkpoint unless somebody bumps that version deliberately.
            weapon.UberCharge += Gml.Less(patient.Hp, maxHp) ? UberChargeHurt : UberChargeHealthy;

            if (Gml.GreaterOrEqual(weapon.UberCharge, WeaponState.MaxUberCharge))
            {
                // sendEventUberReady then doEventUberReady, which sets both of
                // these on every Medigun belonging to that player — here, the
                // one weapon in the slot.
                weapon.UberCharge = WeaponState.MaxUberCharge;
                weapon.UberReady = true;
                events.UberCharged[slot] = true;
            }
        }
        else if (weapon.Ubering)
        {
            // The patient's share of the uber, renewed every tick the beam is on
            // them. Drop the beam and they lose it three ticks later; the medic
            // keeps theirs from UberDrainStep for as long as the charge lasts.
            patient.Ubered = true;
            patient.UberTimer = UberedTicks;
        }

        // Medigun.events/User Event 1.xml:104-108, below the uber block rather
        // than beside the accumulator.
        //
        // ⚠️ `hphealed -= HP_PER_POINT` rather than `= 0`, so the overshoot
        // carries into the next point. A medic who heals 1,599 in one burst gets
        // one point and starts the next at 799, not at 0.
        //
        // ⚠️ And it is an `if`, not a `while` — a single tick can never bank
        // two points. It cannot in practice either, at 6 hp a tick.
        if (Gml.GreaterOrEqual(weapon.HpHealed, GameConstants.HpPerPoint))
        {
            Scoring.Award(ref state, slot, StatIndex.Points, 1);
            weapon.HpHealed -= GameConstants.HpPerPoint;
        }
    }

    /// <summary>
    /// <c>Objects/Weapons/Medigun.events/Begin Step.xml</c>: spending the
    /// charge.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A separate instance's Begin Step, so it runs after the whole of the
    /// owner's.</b> A weapon is created by its character's Create event and
    /// therefore carries a higher instance id, and GM8 runs one event across
    /// instances in id order — so within a tick the order is character, then
    /// that character's weapon, then the next character. That is why this is
    /// called at the end of the per-slot Begin Step block in
    /// <see cref="MatchSimulation"/> rather than beside
    /// <see cref="BeamStep"/>.
    /// <para>
    /// The medic ubers themselves from here <em>unconditionally</em> — no beam,
    /// no patient and no key required. Once <c>ubering</c> is set the eight
    /// seconds run on their own.
    /// </para>
    /// </remarks>
    public static void UberDrainStep(ref WorldState state, int slot)
    {
        ref var weapon = ref state.Weapons[slot];

        if (!weapon.Ubering)
            return;

        ref var medic = ref state.Characters[slot];

        medic.Ubered = true;
        medic.UberTimer = UberedTicks;

        weapon.UberCharge -= UberDrain;

        if (Gml.LessOrEqual(weapon.UberCharge, 0))
        {
            weapon.UberCharge = 0;
            weapon.Ubering = false;
        }
    }

    /// <summary>
    /// <c>Objects/Weapons/Medigun.events/Step.xml</c>: a beam that did not heal
    /// this tick is over.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes releasing the attack key drop the beam</b>, and it
    /// is the only thing that does — <see cref="BeamStep"/> is never reached on
    /// a tick the key is up, so nothing in it can notice. It is a Step event and
    /// so runs in the same stage as <c>move_all_bullets</c> and the characters'
    /// own movement; it touches only <c>healTarget</c> and <c>healer</c>, which
    /// nothing else in that stage reads, so its position within the stage is not
    /// observable.
    /// </remarks>
    public static void DecayStep(ref WorldState state, int slot)
    {
        ref var weapon = ref state.Weapons[slot];

        if (!weapon.HealedThisStep && weapon.HealTarget != WeaponState.NoTarget)
        {
            if (state.Alive[weapon.HealTarget])
                state.Characters[weapon.HealTarget].Healer = CharacterState.Nobody;

            weapon.HealTarget = WeaponState.NoTarget;
        }

        weapon.HealedThisStep = false;
    }

    /// <summary>
    /// <c>Objects/Weapons/Medigun.events/Destroy.xml</c>: the beam dies with the
    /// medic.
    /// </summary>
    /// <remarks>
    /// A weapon is destroyed with its character, so a medic who is killed
    /// mid-beam has to release their patient's <c>healer</c> on the way out —
    /// otherwise that patient keeps naming a slot whose medic is gone, and the
    /// next kill they make is credited with an assist that the reference does
    /// not award. Called from <see cref="WorldState.Clear"/>, which is the
    /// port's death path.
    /// </remarks>
    public static void ReleasePatient(ref WorldState state, int slot)
    {
        var target = state.Weapons[slot].HealTarget;

        if (target != WeaponState.NoTarget && state.Alive[target])
            state.Characters[target].Healer = CharacterState.Nobody;
    }

    /// <summary>
    /// <c>Objects/Characters/Medic.events/Alarm 11.xml</c>: a medic mends
    /// themselves once a second.
    /// </summary>
    /// <remarks>
    /// <b>Unclamped, deliberately.</b> The reference adds to <c>hp</c> without a
    /// <c>min</c>, and the clamp in <c>Character.events/End Step.xml</c> catches
    /// it later in the same tick — which is also where the port clamps
    /// (<see cref="CharacterPhysics.EndStep"/>). Clamping here as well would be
    /// harmless today and wrong the moment something reads hp in between.
    /// <para>
    /// The alarm re-arms itself, so it runs for as long as the medic lives; it
    /// is started by <c>Medic.events/Create.xml</c>, which means the first step
    /// lands thirty ticks after the spawn and not on it.
    /// </para>
    /// </remarks>
    public static void Regenerate(ref CharacterState medic)
    {
        medic.Hp += Gml.Less(medic.TimeUnscathed, RegenFirstThreshold) ? 3
            : Gml.Less(medic.TimeUnscathed, RegenSecondThreshold) ? 4
            : 5;

        medic.RegenTimer = RegenPeriod;
    }

    /// <summary>
    /// <c>Scripts/Misc/calculateHealingFactor.gml</c>: the beam gets stronger
    /// the longer the patient has been left alone.
    /// </summary>
    /// <remarks>
    /// Five seconds unscathed and it starts ramping; seven and a half and it is
    /// at triple. Both comparisons are GML's fuzzy ones, so a patient sitting
    /// exactly on a threshold takes the branch the engine takes.
    /// </remarks>
    public static double HealingFactor(double timeUnscathed)
    {
        const double firstThreshold = 5 * 30;
        const double lastThreshold = 7.5 * 30;

        if (Gml.Less(timeUnscathed, firstThreshold))
            return 1;

        if (Gml.Greater(timeUnscathed, lastThreshold))
            return 3;

        return ((timeUnscathed - firstThreshold) * 2 / firstThreshold) + 1;
    }
}
