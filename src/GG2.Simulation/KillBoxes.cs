using GG2.Maps;
using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>Which of the three killing boxes a rectangle is.</summary>
/// <remarks>
/// All three are the same object with a different name and a different
/// <c>lastDamageSource</c>: no events of their own, invisible, not solid, and
/// all three on <c>sprite64</c> — the same 42x42 collider a
/// <see cref="SpawnRoomRect"/> uses.
/// </remarks>
public enum KillBoxKind
{
    /// <summary><c>killbox</c>: kills a character and a sentry.</summary>
    Kill,

    /// <summary>
    /// <c>pitfall</c>: kills a character and <b>leaves a sentry alone</b>.
    /// </summary>
    Pit,

    /// <summary><c>fragbox</c>: kills a character with a gib source, and a sentry.</summary>
    Frag,
}

/// <summary>One killing box, in inclusive world pixels.</summary>
public readonly record struct KillBoxRect(int Left, int Top, int Right, int Bottom, KillBoxKind Kind);

/// <summary>
/// The three map boxes that kill whatever stands in them.
/// </summary>
/// <remarks>
/// <c>Character.events/Collision with {KillBox, FragBox, PitFall}.xml</c> and
/// <c>Sentry.events/Collision with {KillBox, FragBox}.xml</c>. None of the three
/// objects has any events of its own; like <c>SpawnRoom</c> they exist only to
/// be collided with.
/// <para>
/// <b>What a shipped map actually places</b>, scanned across all 22:
/// <c>tdm_mantic</c> places <b>six pitfalls</b> and nothing else places
/// anything. So a <c>killbox</c> and a <c>fragbox</c> are reachable only on a
/// custom map — and <b>the flag-returning arm they unlock is reachable on no
/// shipped map at all</b>, because the one map with boxes is Team Deathmatch and
/// has no intel. See <see cref="Deaths"/>.
/// </para>
/// <para>
/// These are <em>character</em> collision events, so GM8 keys them by the
/// other object's id and they run <b>after</b> the objective and the cabinet:
/// <c>IntelligenceBase*</c> 104/105, <c>HealingCabinet</c> 126,
/// <c>Intelligence*</c> 133/134, then <c>KillBox</c> 506, <c>FragBox</c> 509,
/// <c>PitFall</c> 794. That order is observable in one place: a character
/// standing in a pit <em>on</em> a briefcase grabs it and then dies, which is
/// what makes the return arm reachable at all.
/// </para>
/// </remarks>
public static class KillBoxes
{
    /// <summary><c>sprite64</c> again — the same collider the spawn room uses.</summary>
    private const int BoxSize = 42;

    /// <summary>What a sentry loses on touching a kill or frag box.</summary>
    /// <remarks>
    /// <c>hp -= 999</c>, a raw subtraction rather than <c>damageSentry</c> — so
    /// it takes no build-rate penalty and credits nobody. The same shape as the
    /// generator explosion's <c>hp -= 999</c>.
    /// </remarks>
    private const double SentryDamage = 999;

    /// <summary>Every killing box the map places.</summary>
    public static KillBoxRect[] Build(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var boxes = new List<KillBoxRect>();

        foreach (var entity in map.Entities)
        {
            if (KindOf(entity.Type) is not { } kind)
                continue;

            boxes.Add(Of(entity, kind));
        }

        return [.. boxes];
    }

    /// <summary>One entity as a box, for a test that needs one the maps do not have.</summary>
    public static KillBoxRect Of(LevelEntity entity, KillBoxKind kind)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var rect = SolidRect.FromEntity(
            entity.X, entity.Y, entity.XScale, entity.YScale,
            BoxSize, BoxSize, SolidKind.Obstacle);

        return new KillBoxRect(rect.Left, rect.Top, rect.Right, rect.Bottom, kind);
    }

    /// <summary>The kind an entity type names, or null if it is not a box.</summary>
    public static KillBoxKind? KindOf(string type) => type switch
    {
        EntityTypes.KillBox => KillBoxKind.Kill,
        EntityTypes.PitFall => KillBoxKind.Pit,
        EntityTypes.FragBox => KillBoxKind.Frag,
        _ => null,
    };

    /// <summary>
    /// The damage source a box writes when nobody else has a claim on the
    /// victim.
    /// </summary>
    public static DamageSource SourceOf(KillBoxKind kind) => kind switch
    {
        KillBoxKind.Kill => DamageSource.KillBox,
        KillBoxKind.Pit => DamageSource.Pitfall,
        KillBoxKind.Frag => DamageSource.FragBox,
        _ => DamageSource.None,
    };

    /// <summary>
    /// The source a box writes when somebody <em>else</em> hurt the victim
    /// recently.
    /// </summary>
    /// <remarks>
    /// <b>A frag box hands the kill over as a <em>gib</em> and the other two
    /// as a plain finish-off.</b> The three events are otherwise identical, and
    /// this and <see cref="SourceOf"/> are the whole difference — plus the frag
    /// box's explosion sound, which is presentation.
    /// </remarks>
    public static DamageSource StolenSourceOf(KillBoxKind kind) =>
        kind == KillBoxKind.Frag ? DamageSource.FinishedOffGib : DamageSource.FinishedOff;

    /// <summary>
    /// Every character's and every sentry's collision events with the three
    /// boxes.
    /// </summary>
    /// <remarks>
    /// <b>The box does not kill; it sets <c>hp = 0</c>.</b> The death happens
    /// in the victim's own End Step, later in the same tick, through exactly the
    /// path any other lethal damage takes — which is what makes the kill log,
    /// the scoring and the respawn all behave normally.
    /// <para>
    /// <b>And a pit fall does not touch a sentry.</b>
    /// <c>Sentry.events/</c> has a <c>Collision with KillBox</c> and a
    /// <c>Collision with FragBox</c> and no <c>Collision with PitFall</c>, so an
    /// engineer can build in a pit and the gun survives what its owner does not.
    /// </para>
    /// </remarks>
    public static void CollisionStep(CollisionWorld world, ref WorldState state)
    {
        ArgumentNullException.ThrowIfNull(world);
        CollisionStep(world, ref state, world.KillBoxes);
    }

    /// <inheritdoc cref="CollisionStep(CollisionWorld, ref WorldState)"/>
    /// <remarks>
    /// The overload a test can hand a box the shipped maps do not contain —
    /// which is all of them except a pit fall on <c>tdm_mantic</c>.
    /// </remarks>
    public static void CollisionStep(
        CollisionWorld world, ref WorldState state, IReadOnlyList<KillBoxRect> boxes)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(boxes);

        if (boxes.Count == 0)
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
                for (var i = 0; i < boxes.Count; i++)
            {
                var killBox = boxes[i];

                if (!box.Overlaps(new Aabb(killBox.Left, killBox.Top, killBox.Right, killBox.Bottom)))
                    continue;

                character.Hp = 0;

                // The port's own field, and the reason it exists is the line
                // below: the source a box writes is not always the box. See
                // CharacterState.DiedToHazard and Deaths.Kill.
                character.DiedToHazard = true;

                // `if (instance_exists(lastDamageDealer) && lastDamageDealer != player)`
                // — somebody else's claim survives the box, and the box only
                // names itself when there is nobody to hand the kill to. Note it
                // does *not* clear the dealer, so the credit still goes to them.
                character.LastDamageSource =
                    character.LastDamageDealer != CharacterState.Nobody
                    && character.LastDamageDealer != slot
                        ? StolenSourceOf(killBox.Kind)
                        : SourceOf(killBox.Kind);
            }
        }

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasSentry[slot])
                continue;

            ref var sentry = ref state.Sentries[slot];
            var body = Sentries.BodyBox(sentry);

            for (var i = 0; i < boxes.Count; i++)
            {
                var killBox = boxes[i];

                // A pit fall has no Sentry event at all.
                if (killBox.Kind == KillBoxKind.Pit)
                    continue;

                if (!body.Overlaps(new Aabb(killBox.Left, killBox.Top, killBox.Right, killBox.Bottom)))
                    continue;

                // A raw subtraction, not damageSentry: no build-rate penalty and
                // no dealer. The reference then clears the dealer outright, so a
                // sentry a box destroys credits nobody even if somebody had been
                // shooting it.
                sentry.Hp -= SentryDamage;
                sentry.LastDamageDealer = CharacterState.Nobody;
            }
        }
    }
}
