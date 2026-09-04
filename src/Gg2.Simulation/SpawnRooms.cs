using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>One <c>SpawnRoom</c> rectangle, in inclusive world pixels.</summary>
/// <remarks>
/// <c>SpawnRoom.xml</c> is the emptiest object in the game — <b>no events at
/// all</b>, invisible, not solid. It exists only to be asked about, by four
/// callers that between them ask in <em>two different ways</em>; see
/// <see cref="SpawnRooms"/>.
/// </remarks>
public readonly record struct SpawnRoomRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// One <c>HealingCabinet</c>: a rectangle and the three switches a map may set
/// on it.
/// </summary>
/// <remarks>
/// <c>HealingCabinet.events/{Create, User Event 1}.xml</c>: <c>heal</c> and
/// <c>refill</c> default true, <c>uber</c> defaults false, and each is a
/// <c>BOOL</c> property — which <c>readProperty.gml</c> accepts only as the
/// exact strings <c>"true"</c> and <c>"false"</c>, falling back to the default
/// for anything else.
/// </remarks>
public readonly record struct HealingCabinet(
    int Left, int Top, int Right, int Bottom, bool Heal, bool Refill, bool Uber);

/// <summary>
/// The spawn room, and the four questions asked of it.
/// </summary>
/// <remarks>
/// <b>The reference asks about a spawn room in two different ways and they
/// do not agree.</b> A class change and a sentry build use
/// <c>collision_point(x, y, SpawnRoom, 0, 0)</c> — the character's
/// <em>origin</em>, which is at its feet — and the fast respawn uses
/// <c>place_meeting(x, y, SpawnRoom)</c>, the whole <em>box</em>. So a
/// character standing with its feet outside the room and its head inside gets
/// the quick respawn but is still killed by a class change. Both are
/// transcribed as written.
/// <para>
/// <c>sprite64</c> is 42x42 with its origin at (0, 0) and a <c>RECTANGLE</c>
/// mask on <b><c>FULL</c></b> bounds — the whole image — so the rectangle is
/// <see cref="SolidRect.FromEntity"/>'s phase-3 rule with a different size,
/// exactly as <c>CaptureZoneS</c> is. Not a new inference.
/// </para>
/// </remarks>
public static class SpawnRooms
{
    /// <summary><c>sprite64</c>, the spawn room's collider.</summary>
    private const int RoomSize = 42;

    /// <summary>Every <c>spawnroom</c> entity the map places, as a rectangle.</summary>
    public static SpawnRoomRect[] Build(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var rooms = new List<SpawnRoomRect>();

        foreach (var entity in map.Entities)
        {
            if (!string.Equals(entity.Type, EntityTypes.SpawnRoom, StringComparison.Ordinal))
                continue;

            rooms.Add(Of(entity));
        }

        return [.. rooms];
    }

    /// <summary>One <c>spawnroom</c> entity as a rectangle.</summary>
    /// <remarks>
    /// A seam for the same reason <see cref="Cabinets.Of"/> is one: <b>no
    /// shipped map places an unscaled spawn room</b>, so the only way to gate
    /// the sprite's own size is to build one.
    /// </remarks>
    public static SpawnRoomRect Of(LevelEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var rect = SolidRect.FromEntity(
            entity.X, entity.Y, entity.XScale, entity.YScale,
            RoomSize, RoomSize, SolidKind.Obstacle);

        return new SpawnRoomRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}

/// <summary>
/// The healing cabinet: a full heal, an extinguish, an ammo refill and — where
/// a map asks for it — an instant ubercharge.
/// </summary>
/// <remarks>
/// <c>Character.events/Collision with HealingCabinet.xml</c>. It is a
/// <em>character's</em> collision event, so it runs in the collision stage; the
/// cabinet's own object has only a Create and a property reader.
/// <para>
/// <b>The extinguish is outside the <c>heal</c> guard</b>, so a cabinet a
/// map has switched healing off on still puts a burning character out. It is
/// also outside the ammo guard, so it happens whatever else the cabinet is for.
/// </para>
/// <para>
/// <c>sprite74</c> is 32x48, origin (0, 0), <c>AUTO</c> bounds — and its alpha
/// covers the whole image, so AUTO and FULL give the same rectangle and this is
/// <see cref="SolidRect.FromEntity"/>'s rule again.
/// </para>
/// </remarks>
public static class Cabinets
{
    /// <summary><c>sprite74</c>, the cabinet's collider.</summary>
    private const int CabinetWidth = 32;

    private const int CabinetHeight = 48;

    /// <summary>
    /// Team Deathmatch heals ten hit points a second instead of filling the bar.
    /// </summary>
    /// <remarks>
    /// <c>hp += global.delta_factor/30*10</c>, which at 30 Hz is a third of a
    /// hit point a tick. <b>And that arm does nothing else</b> — no nuts and
    /// bolts, no sandvich reset — so a Team Deathmatch engineer gets no build
    /// resource from a cabinet at all.
    /// </remarks>
    public const double DeathmatchHealPerTick = 10.0 / 30.0;

    /// <summary>Every <c>medCabinet</c> entity the map places.</summary>
    public static HealingCabinet[] Build(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var cabinets = new List<HealingCabinet>();

        foreach (var entity in map.Entities)
        {
            if (!string.Equals(entity.Type, EntityTypes.MedCabinet, StringComparison.Ordinal))
                continue;

            cabinets.Add(Of(entity));
        }

        return [.. cabinets];
    }

    /// <summary>
    /// One <c>medCabinet</c> entity as a cabinet — the rectangle and the three
    /// switches.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Build"/> the way
    /// <see cref="ColliderSprites.Of"/> is, so the property parsing can be
    /// gated without a whole map: no shipped map overrides any of the three
    /// defaults, so the only way to reach the non-default arms is to build an
    /// entity by hand.
    /// </remarks>
    public static HealingCabinet Of(LevelEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var rect = SolidRect.FromEntity(
            entity.X, entity.Y, entity.XScale, entity.YScale,
            CabinetWidth, CabinetHeight, SolidKind.Obstacle);

        return new HealingCabinet(
            rect.Left, rect.Top, rect.Right, rect.Bottom,
            Flag(entity, "heal", true),
            Flag(entity, "refill", true),
            Flag(entity, "uber", false));
    }

    /// <summary>
    /// <c>readProperty(properties, key, BOOL, fallback)</c>.
    /// </summary>
    /// <remarks>
    /// <b>Only the exact strings <c>"true"</c> and <c>"false"</c> count.</b>
    /// <c>readProperty.gml</c>'s BOOL arm returns the default for anything else
    /// — "TRUE", "1" and "yes" all fall through — so a map that spells it any
    /// other way gets the default rather than an error.
    /// </remarks>
    private static bool Flag(LevelEntity entity, string key, bool fallback) =>
        entity.GetString(key) switch
        {
            "true" => true,
            "false" => false,
            _ => fallback,
        };

    /// <summary>
    /// Every character's <c>Collision with HealingCabinet</c> event, for the
    /// whole roster.
    /// </summary>
    /// <remarks>
    /// <b>The whole event is inside <c>if (!global.mapchanging)</c></b>, so a
    /// cabinet does nothing at all during the victory screen — the one map
    /// object that switches off with the round rather than opening up.
    /// <para>
    /// Its place in the collision stage is the reference's: GM8 keys a
    /// character's collision events by the other object's id, and
    /// <c>HealingCabinet</c> is 126 — between <c>IntelligenceBase*</c> (104/105)
    /// and <c>Intelligence*</c> (133/134). So a capture resolves before a
    /// cabinet and a cabinet before a grab. Neither ordering is observable: a
    /// cabinet moves nobody and changes nothing a grab or a capture reads.
    /// </para>
    /// </remarks>
    public static void CollisionStep(
        CollisionWorld world, ref WorldState state, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);
        CollisionStep(world.Cabinets, ref state, ref events);
    }

    /// <inheritdoc cref="CollisionStep(CollisionWorld, ref WorldState, ref TickEvents)"/>
    /// <remarks>
    /// The overload a test can hand a cabinet the shipped maps do not contain.
    /// </remarks>
    public static void CollisionStep(
        IReadOnlyList<HealingCabinet> cabinets, ref WorldState state, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(cabinets);

        if (cabinets.Count == 0 || state.MapChanging)
            return;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var character = ref state.Characters[slot];
            var box = Aabb.ForCharacter(character.Box, character.X, character.Y);

                // Indexed rather than `foreach`: this list arrives as an
                // INTERFACE, and enumerating one allocates a boxed enumerator —
                // measured at 32 bytes per live character per tick, every tick,
                // which is exactly what docs/BOTAI.md item 1 forbids. Indexing an
                // IReadOnlyList costs an interface call per element and allocates
                // nothing, and these lists are a handful of rectangles.
                for (var i = 0; i < cabinets.Count; i++)
            {
                var cabinet = cabinets[i];

                if (!box.Overlaps(new Aabb(cabinet.Left, cabinet.Top, cabinet.Right, cabinet.Bottom)))
                    continue;

                Touch(ref state, slot, in cabinet, ref events);
            }
        }
    }

    /// <summary>One character against one cabinet.</summary>
    private static void Touch(
        ref WorldState state, int slot, in HealingCabinet cabinet, ref TickEvents events)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = ClassStats.For(character.Class);

        // `if ((hp < maxHp or nutsNBolts < 100 or !canEat) and other.heal)`.
        //
        // The third clause is load-bearing now that the sandvich exists: a
        // heavy at full health with full ammo and a spent sandvich still
        // *enters* this branch, purely to have the cooldown cleared. Before the
        // sandvich was modelled it was a no-op and was transcribed as a comment.
        if (cabinet.Heal
            && (Gml.Less(character.Hp, stats.MaxHp)
                || Gml.Less(character.NutsNBolts, CharacterState.MaxNutsNBolts)
                || !character.CanEat))
        {
            if (state.GameMode == GameMode.TeamDeathmatch)
            {
                // A trickle, not a fill — and nothing else. See
                // DeathmatchHealPerTick. Note it does NOT clear the sandvich
                // cooldown either, so a Team Deathmatch heavy who steps on a
                // cabinet to reset their sandvich gets a third of a hit point
                // and keeps waiting.
                character.Hp += DeathmatchHealPerTick;
            }
            else
            {
                character.Hp = stats.MaxHp;
                character.NutsNBolts = CharacterState.MaxNutsNBolts;

                // `alarm[6] = 0; canEat = true;` — the reference's own FIXME
                // asks whether the second is redundant with the first. It is
                // not redundant, it is the other way round: an alarm set to 0
                // decrements to -1 without ever firing, so clearing the timer
                // alone would leave `canEat` false for ever.
                character.EatTimer = 0;
                character.CanEat = true;
            }
        }

        // Outside the heal guard, and outside the ammo guard: a cabinet with
        // `heal:false` and `refill:false` still puts a burning character out.
        if (Gml.Greater(character.BurnIntensity, 0) || Gml.Greater(character.BurnDuration, 0))
        {
            character.BurnIntensity = 0;
            character.BurnDuration = 0;
            character.BurnedBy = CharacterState.Nobody;
            character.AfterburnSource = DamageSource.None;
        }

        var kind = WeaponStats.KindFor(character.Class);

        if (cabinet.Refill && Gml.Less(weapon.AmmoCount, WeaponStats.For(kind).MaxAmmo))
        {
            weapon.AmmoCount = WeaponStats.For(kind).MaxAmmo;

            // `if (alarm[5] > 0) event_perform(ev_alarm, 5); alarm[5] = -1;` for
            // five of the ten weapons.
            //
            // The event_perform is a no-op and only the cancel is real. Each
            // of those five alarm-5 bodies either guards on
            // `ammoCount < maxAmmo` — false, the line above just filled it — or,
            // for the Medigun, assigns the maximum unconditionally, which is the
            // same value again. The reference's own comment on it is "stop the
            // animation". What is observable is the reload timer being cleared,
            // so a cabinet does not leave a round arriving a moment later.
            if (kind is WeaponKind.Rocketlauncher or WeaponKind.Scattergun
                or WeaponKind.Shotgun or WeaponKind.Revolver or WeaponKind.Medigun)
            {
                weapon.ReloadTimer = WeaponState.Inactive;
            }
        }

        // `if (other.uber and currentWeapon.object_index == Medigun)`.
        //
        // No charge is added — the cabinet hands over a *finished* uber, and
        // only to a medic who is neither already holding one nor spending one.
        // `global.isHost` is always true here.
        if (cabinet.Uber && kind == WeaponKind.Medigun && !weapon.UberReady && !weapon.Ubering)
        {
            // doEventUberReady: the same pair Healing.BeamStep writes when a
            // charge fills on its own.
            weapon.UberReady = true;
            weapon.UberCharge = WeaponState.MaxUberCharge;
            events.UberCharged[slot] = true;
        }
    }
}
