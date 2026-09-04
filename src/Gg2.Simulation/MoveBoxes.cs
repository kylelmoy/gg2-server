using Gg2.Maps;

namespace Gg2.Simulation;

/// <summary>Which way a <c>MoveBox</c> pushes.</summary>
public enum PushDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>One <c>MoveBox</c>, in inclusive world pixels.</summary>
/// <remarks>
/// <c>pushPower</c> is per-instance: <c>MoveBox.events/Create.xml</c> starts it
/// at 5 and <c>User Event 1</c> lets the map override it with a <c>speed</c>
/// property. No shipped map does.
/// </remarks>
public readonly record struct MoveBoxRect(
    int Left, int Top, int Right, int Bottom, PushDirection Direction, double PushPower);

/// <summary>
/// The four boxes that shove whatever stands in them.
/// </summary>
/// <remarks>
/// <c>Character.events/Collision with MoveBox{Up,Down,Left,Right}.xml</c>, each
/// two lines and identical but for the component and the sign:
/// <code>
/// moveStatus = 0;
/// if (global.run_virtual_ticks) hspeed += other.pushPower;
/// </code>
/// <para>
/// <b>It accelerates, it does not set.</b> The write is <c>+=</c> and the
/// event fires on <em>every</em> tick the boxes overlap, so standing in a column
/// of them gains five units of speed a tick without limit — which is what makes
/// them lifts rather than conveyor belts.
/// </para>
/// <para>
/// <b><c>moveStatus = 0</c> is outside the <c>run_virtual_ticks</c>
/// guard</b>, and it is the observable half at any frame rate: it puts a
/// character blasted by a rocket back on ordinary friction and control (see
/// <see cref="CharacterPhysics.BeginStepAccelerate"/>), so a move box cancels a
/// knockback as well as adding to it. <c>run_virtual_ticks</c> is always true at
/// 30 Hz — docs/SIMULATION.md — so the guard itself is not modelled.
/// </para>
/// <para>
/// <b>What a shipped map places</b>, scanned across all 22: <c>tdm_mantic</c>
/// alone, <b>nine up and six down</b>, none left or right and none overriding
/// the default power. The same map that places the six pit falls — see
/// <see cref="KillBoxes"/>.
/// </para>
/// </remarks>
public static class MoveBoxes
{
    /// <summary><c>sprite64</c> once more: the same 42x42 collider.</summary>
    private const int BoxSize = 42;

    /// <summary><c>MoveBox.events/Create.xml</c>: <c>pushPower = 5</c>.</summary>
    public const double DefaultPushPower = 5;

    /// <summary>Every move box the map places.</summary>
    public static MoveBoxRect[] Build(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var boxes = new List<MoveBoxRect>();

        foreach (var entity in map.Entities)
        {
            if (DirectionOf(entity.Type) is not { } direction)
                continue;

            boxes.Add(Of(entity, direction));
        }

        return [.. boxes];
    }

    /// <summary>One entity as a box.</summary>
    /// <remarks>
    /// A seam for the same reason <see cref="Cabinets.Of"/> is one: no shipped
    /// map places a left or right box, or overrides <c>speed</c>, so those arms
    /// are only reachable from a hand-built entity.
    /// </remarks>
    public static MoveBoxRect Of(LevelEntity entity, PushDirection direction)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var rect = SolidRect.FromEntity(
            entity.X, entity.Y, entity.XScale, entity.YScale,
            BoxSize, BoxSize, SolidKind.Obstacle);

        // readProperty(properties, "speed", REAL, pushPower) — a real, with the
        // Create event's 5 as the fallback for anything unparseable.
        return new MoveBoxRect(
            rect.Left, rect.Top, rect.Right, rect.Bottom,
            direction,
            entity.GetReal("speed", DefaultPushPower));
    }

    /// <summary>The direction an entity type names, or null if it is not a move box.</summary>
    public static PushDirection? DirectionOf(string type) => type switch
    {
        EntityTypes.MoveBoxUp => PushDirection.Up,
        EntityTypes.MoveBoxDown => PushDirection.Down,
        EntityTypes.MoveBoxLeft => PushDirection.Left,
        EntityTypes.MoveBoxRight => PushDirection.Right,
        _ => null,
    };

    /// <summary>Every character's collision events with the four move boxes.</summary>
    /// <remarks>
    /// <b>The push is a <em>component</em> write</b>, so it goes through
    /// <see cref="Motion"/>'s component path and re-derives <c>direction</c> and
    /// <c>speed</c> with the snap — not the polar path. A character shoved
    /// straight up out of a standstill ends with an exactly vertical direction
    /// because of it.
    /// <para>
    /// The four boxes' object ids are 756, 758, 759 and 761, which puts them
    /// between <c>FragBox</c> (509) and <c>PitFall</c> (794) in a character's
    /// collision-event order. <b>None of that interleaving is observable</b>:
    /// the killing boxes write <c>hp</c> and <c>lastDamageSource</c> and these
    /// write the velocity pair and <c>moveStatus</c>, and two boxes pushing the
    /// same character compose by addition, which does not care about order —
    /// the final component pair is the same either way and
    /// <c>direction</c>/<c>speed</c> are a pure function of it.
    /// </para>
    /// </remarks>
    public static void CollisionStep(CollisionWorld world, ref WorldState state)
    {
        ArgumentNullException.ThrowIfNull(world);
        CollisionStep(world, ref state, world.MoveBoxes);
    }

    /// <inheritdoc cref="CollisionStep(CollisionWorld, ref WorldState)"/>
    /// <remarks>The overload a test can hand a box the shipped maps do not have.</remarks>
    public static void CollisionStep(
        CollisionWorld world, ref WorldState state, IReadOnlyList<MoveBoxRect> boxes)
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
                // 32 bytes per live character per tick, which is exactly what
                // docs/BOTAI.md item 1 forbids. Indexing an IReadOnlyList costs
                // an interface call per element and allocates nothing, and these
                // lists are a handful of rectangles.
                for (var i = 0; i < boxes.Count; i++)
            {
                var moveBox = boxes[i];

                if (!box.Overlaps(new Aabb(moveBox.Left, moveBox.Top, moveBox.Right, moveBox.Bottom)))
                    continue;

                // Outside the run_virtual_ticks guard in the reference, and the
                // half that matters at any frame rate: a character still flagged
                // as rocket-jumping is put back on ordinary control here.
                character.MoveStatus = 0;

                switch (moveBox.Direction)
                {
                    // Up is negative, as everywhere else in GM8.
                    case PushDirection.Up:
                        character.VSpeed -= moveBox.PushPower;
                        break;

                    case PushDirection.Down:
                        character.VSpeed += moveBox.PushPower;
                        break;

                    case PushDirection.Left:
                        character.HSpeed -= moveBox.PushPower;
                        break;

                    case PushDirection.Right:
                        character.HSpeed += moveBox.PushPower;
                        break;
                }
            }
        }
    }
}
