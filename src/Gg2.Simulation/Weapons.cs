using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Pulling the trigger: the path from a held attack key to projectiles in the
/// air.
/// </summary>
/// <remarks>
/// Four of the reference's events collapse into this file, because in GML they
/// are four objects passing control to each other and here they are one
/// function:
/// <list type="number">
/// <item>
/// <c>Character.events/Begin Step.xml:96-100</c> — the attack key, and
/// <c>with(currentWeapon) event_user(1)</c>.
/// </item>
/// <item>
/// <c>Objects/Weapons/Scattergun.events/User Event 1.xml</c> — the host checks
/// <c>readyToShoot</c> and the magazine, draws a seed, broadcasts it and then
/// fires locally with it.
/// </item>
/// <item>
/// <c>Scripts/Events/doEventFireWeapon.gml</c> — <c>random_set_seed(seed)</c>
/// around <c>event_user(3)</c>, restoring the outer seed afterwards.
/// </item>
/// <item>
/// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c> — the six pellets.
/// </item>
/// </list>
/// <para>
/// <b>Where this runs is load-bearing.</b> It is a Begin Step, and it happens
/// <em>before</em> the acceleration loop in the same event — so
/// <c>shot.hspeed += owner.hspeed</c> adds the speed the character carried out
/// of the previous tick, not the one it is about to have. It is also before
/// <see cref="Timers.Advance"/>, so the refire counter this tick tests is the
/// one last tick left behind.
/// </para>
/// <para>
/// <b>The seeded window is a contract.</b> Every draw between
/// <c>random_set_seed</c> and the end of <c>event_user(3)</c> is re-run by every
/// client from the seed on the wire, so the <em>number</em> of draws matters as
/// much as their use: a pellet costs exactly three advances — the
/// <c>choose(0, 0, 1)</c> in <c>Shot.events/Create.xml</c>, the speed jitter and
/// the direction jitter — and dropping the first because nothing reads
/// <c>perseverant</c> yet would desync every later pellet.
/// </para>
/// </remarks>
public static class Weapons
{
    /// <summary>Pellets one blast of a scattergun throws.</summary>
    private const int ScattergunPellets = 6;

    /// <summary>A scattergun pellet's muzzle speed, before the jitter.</summary>
    private const double ScattergunPelletSpeed = 13;

    /// <summary>
    /// How far forward a pellet is placed before it first moves.
    /// </summary>
    /// <remarks>
    /// The reference's own comment: "Move shot forward to avoid immediate
    /// collision with a wall behind the character". Applied along the pellet's
    /// <em>scattered</em> direction, with <c>lengthdir</c> — which snaps, so a
    /// pellet fired near an axis lands on an exact offset. See
    /// <see cref="Gml.LengthDirX"/>.
    /// </remarks>
    private const double ScattergunNudge = 15;

    /// <summary>Damage a scattergun pellet deals. <c>Shot.events/Create.xml</c>.</summary>
    private const double ShotHitDamage = 8;

    /// <summary>A <c>Shot</c>'s default lifetime, before a weapon overrides it.</summary>
    private const int ShotLifetime = 40;

    /// <summary>
    /// One character's fire step: the part of Begin Step that can create
    /// projectiles.
    /// </summary>
    /// <remarks>
    /// Called immediately before <see cref="CharacterPhysics.BeginStep"/> for
    /// the same slot rather than as a pass of its own, and the two orderings are
    /// not equivalent. In the reference this code sits inside Begin Step, a few
    /// lines above the acceleration loop; nothing it reads is written by the
    /// lines above it and nothing it writes is read by them, so hoisting it to
    /// the front of the same slot's Begin Step is exact — while hoisting it to
    /// the front of the <em>tick</em> would let one character's shot see another
    /// character's stale position.
    /// </remarks>
    public static void FireStep(ref WorldState state, int slot, ref TickEvents events)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        // Character.events/Begin Step.xml:96 — `downOrPressedKeys`, the union,
        // so a click that arrives and is released inside one tick still fires.
        var downOrPressed = character.KeyState | character.PressedKeys;

        if ((downOrPressed & PlayerKeys.Attack) == 0)
            return;

        // Not modelled, and each would suppress the shot: `taunting`,
        // `omnomnomnom` (the heavy's sandvich) and `player.humiliated` (the
        // humiliation round). None of the three can be entered by anything
        // phase 5 implements, so there is no state in which the guard would
        // differ — they arrive with the features that set them.

        // Scattergun.events/User Event 1.xml. `global.isHost` is always true
        // here: this *is* the host, and a client never fires for itself.
        if (!weapon.ReadyToShoot || !Gml.Greater(weapon.AmmoCount, 0))
            return;

        // Every other weapon's User Event 3 arrives with that weapon. Bailing
        // *before* the seed is drawn rather than after is deliberate: a shot
        // that is announced and not simulated is worse than one that never
        // happens, because the client would draw pellets the server has no
        // record of and then snap them away.
        if (WeaponStats.KindFor(character.Class) != WeaponKind.Scattergun)
            return;

        var seed = DrawSeed(ref state, slot, ref events);

        events.Fired[slot] = true;
        events.Fires[slot] = new WeaponFireEvent(
            slot,
            seed,
            character.X,
            character.Y,
            character.HSpeed,
            character.VSpeed);

        // doEventFireWeapon.gml saves the global seed, sets the shot's, and puts
        // the global one back. A local generator is the same thing with none of
        // the bookkeeping: WorldState.Random is simply never touched below.
        var random = new GmlRandom(seed);
        FireScattergun(ref state, slot, ref random);
    }

    /// <summary>
    /// <c>Character.events/End Step.xml:64-65</c>: the weapon trails its owner.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="CharacterPhysics.EndStep"/>'s
    /// body — it is called from there, at the point the reference does it, which
    /// is <b>before</b> the map clamp a few lines below. A character standing on
    /// the map edge would otherwise arm its weapon at the clamped position.
    /// </remarks>
    public static void FollowOwner(in CharacterState character, ref WeaponState weapon)
    {
        weapon.X = Gml.Round(character.X);
        weapon.Y = Gml.Round(character.Y);
    }

    /// <summary>
    /// The seed this shot's spread is drawn from: the recorded one if the
    /// harness supplied it, otherwise a fresh draw.
    /// </summary>
    private static ushort DrawSeed(ref WorldState state, int slot, ref TickEvents events)
    {
        if (events.HasForcedFireSeed[slot])
        {
            // Consumed, so the next shot from this slot needs its own — a
            // replay that ran out of recorded seeds must fall through to a draw
            // and diverge visibly, rather than silently reusing the last one.
            events.HasForcedFireSeed[slot] = false;
            return events.ForcedFireSeed[slot];
        }

        return (ushort)state.Random.NextInclusive(65535);
    }

    /// <summary>
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c>: six pellets,
    /// eighteen random draws.
    /// </summary>
    private static void FireScattergun(ref WorldState state, int slot, ref GmlRandom random)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Scattergun);

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var aim = character.AimDirection;
        var muzzleX = weapon.X;
        var muzzleY = weapon.Y;
        // Hoisted out of the repeat(6), where the reference recomputes it. It
        // reads only the aim and the owner's hspeed, neither of which the loop
        // touches, and it draws nothing from the seeded stream — so this is the
        // same six values and the same eighteen advances.
        var lifetime = ScattergunPelletLifetime(aim, character.HSpeed);

        for (var pellet = 0; pellet < ScattergunPellets; pellet++)
        {
            var shot = CreateShot(
                muzzleX,
                muzzleY,
                ProjectileKind.Shot,
                DamageSource.Scattergun,
                aim,
                ScattergunPelletSpeed,
                slot,
                character.Team,
                ref random);

            // Order matters and so does the representation each step writes
            // through: a component write, then a speed write, then a direction
            // write. Each round trip re-derives the other pair and snaps it
            // (see Motion), so collapsing these into one polar assignment gives
            // different last bits and, near an axis, a different answer.
            shot.Motion.HSpeed += character.HSpeed;
            shot.Motion.Speed += random.Next(4) - 2;
            shot.Motion.Direction += random.Next(15) - 7.5;

            // lengthdir, not cos/sin: the engine's snaps and a bare cosine does
            // not, and a pellet fired at 90 degrees must land exactly 15 pixels
            // above the muzzle.
            shot.X += Gml.LengthDirX(ScattergunNudge, shot.Motion.Direction);
            shot.Y += Gml.LengthDirY(ScattergunNudge, shot.Motion.Direction);

            shot.Lifetime = lifetime;

            state.SpawnProjectile(shot);
        }

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
    }

    /// <summary>
    /// How long a scattergun pellet lives, given the shooter's aim and
    /// horizontal speed.
    /// </summary>
    /// <remarks>
    /// <b>A range compensation, and it looks like a mistake until you see what
    /// it is for.</b> Every pellet inherits the shooter's <c>hspeed</c>, so a
    /// scout running forwards throws pellets that travel further per tick and
    /// would otherwise out-range a standing one. The factor
    /// <c>(min(1, forward/actual) - 1)/2 + 1</c> shortens the fuse by up to
    /// half when the inherited speed helps, and is exactly 1 when it does not.
    /// <para>
    /// The reference spells it out at
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c>, and it is
    /// transcribed rather than simplified: <c>abs</c> appears on the numerator
    /// and on the whole denominator but not on <c>cos</c> inside the
    /// denominator, so the two are not the same quantity and cancelling them
    /// changes the answer whenever <c>hspeed</c> opposes the aim.
    /// </para>
    /// <para>
    /// The apparent division by zero at a vertical aim does not happen: GM8's
    /// <c>cos(degtorad(90))</c> is 6.1e-17 rather than 0, so numerator and
    /// denominator are both tiny and the ratio is 1. The port's is the same
    /// 6.1e-17, for the same reason — <see cref="Gml.DegToRad"/> is one multiply.
    /// </para>
    /// </remarks>
    public static int ScattergunPelletLifetime(double aimDirection, double hspeed)
    {
        var cos = Math.Cos(Gml.DegToRad(aimDirection));
        var forward = Math.Abs(cos) * ScattergunPelletSpeed;
        var actual = Math.Abs((cos * ScattergunPelletSpeed) + hspeed);

        return Timers.SetAlarm(35 * (((Math.Min(1, forward / actual) - 1) / 2) + 1));
    }

    /// <summary>
    /// <c>Scripts/Object Creation/createShot.gml</c>, and the projectile's own
    /// Create event with it.
    /// </summary>
    /// <remarks>
    /// Returned by value rather than added to the pool here, because the
    /// reference's caller keeps editing the instance after
    /// <c>createShot</c> returns — the Scattergun writes <c>hspeed</c>,
    /// <c>speed</c>, <c>direction</c>, <c>x</c>, <c>y</c> and <c>alarm[0]</c>
    /// before the pellet ever moves. Putting it in the pool first and mutating
    /// through a handle would work equally well and read worse.
    /// <para>
    /// ⚠️ <b><c>image_angle</c> is set here and never again.</b> The Scattergun
    /// then scatters <c>direction</c> and leaves the angle alone, so a pellet's
    /// bounding box is aligned with the <em>aim</em> rather than with its own
    /// flight. See <see cref="ProjectileState.ImageAngle"/>.
    /// </para>
    /// </remarks>
    private static ProjectileState CreateShot(
        double x,
        double y,
        ProjectileKind kind,
        DamageSource source,
        double direction,
        double speed,
        int ownerSlot,
        Team team,
        ref GmlRandom random)
    {
        var shot = new ProjectileState
        {
            Kind = kind,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Team = team,
            Source = source,
        };

        // instance_create runs the Create event before createShot's own `with`
        // block, so the choose() draw comes first in the seeded stream. Its
        // position in the order is what the client re-derives against.
        shot.Perseverant = random.ChooseIndex(3) == 2;
        shot.HitDamage = ShotHitDamage;
        shot.Lifetime = ShotLifetime;

        // direction first, then speed: with speed still zero the direction write
        // leaves both components at zero, and it is the speed write that turns
        // the pair into a velocity. Writing speed first would point the pellet
        // along the previous direction, which for a fresh instance is 0 degrees.
        shot.Motion.Direction = direction;
        shot.ImageAngle = direction;
        shot.Motion.Speed = speed;

        return shot;
    }
}
