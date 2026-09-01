using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The map's <c>MovingPlatform</c> instances: a platform that walks out to an
/// offset and back, on a loop or on a trigger.
/// </summary>
/// <remarks>
/// <c>Objects/Map elements/Colliders/MovingPlatform.events</c> — Create for the
/// defaults, User Event 1 for the map's overrides, End Step for the motion, and
/// User Events 10 and 11 for the five bytes each one puts on every
/// <c>QUICK_UPDATE</c> and <c>FULL_UPDATE</c>.
/// <para>
/// ⚠️ <b>The carry is NOT here, and that is a real gap rather than a
/// simplification.</b> The reference's End Step ends with a <c>with(Character)</c>
/// block that picks up whoever is standing on the platform and pushes whoever it
/// runs into. Every branch of it is a <c>place_meeting</c> against the platform's
/// own collision mask — and <b>a <c>MovingPlatform</c> has no sprite</b>
/// (<c>MovingPlatform.xml</c> declares <c>&lt;sprite/&gt;</c> empty, unlike its
/// parent <c>DropdownPlatform</c>'s <c>sprite44</c>). Its mask arrives at run
/// time from an embedded sprite named by the entity's <c>resource</c> property
/// and decoded out of the map's metadata by
/// <c>CustomMapCreateEntitiesFromEntityData.gml:68-88</c>. **That pipeline is not
/// ported**, so this port cannot know a platform's box, and GM8 with
/// <c>sprite_index = -1</c> collides with nothing — every branch of the carry is
/// a no-op there. What is written below is therefore the whole of the reference's
/// behaviour for every map this port can currently load, not a subset of it.
/// **Porting the carry is gated on the custom-map resource path**, and
/// <c>docs/TRAPS-SIMULATION.md</c>, "moving platforms", says so with the
/// falsification condition attached.
/// </para>
/// <para>
/// ⚠️ <b>The parent matters for the other direction.</b> Because
/// <c>MovingPlatform</c>'s parent IS <c>DropdownPlatform</c>, every
/// <c>with(DropdownPlatform)</c> and <c>place_meeting(…, DropdownPlatform)</c> in
/// the character physics already means "or a moving platform". So the day a box
/// exists, a character stands on one through
/// <see cref="CharacterPhysics"/>'s existing dropdown path and no new standing
/// rule is needed — only the carry.
/// </para>
/// </remarks>
public static class MovingPlatforms
{
    /// <summary>Stopped. <c>movementState == 0</c>.</summary>
    public const byte Stopped = 0;

    /// <summary>Moving towards <c>(left, -top)</c>. <c>movementState == 1</c>.</summary>
    public const byte Up = 1;

    /// <summary>Moving back towards the start. <c>movementState == 2</c>.</summary>
    public const byte Down = 2;

    // Create.xml's own values, and the fallbacks User Event 1 passes to
    // readProperty for anything the map does not override.
    private const double DefaultTop = 60;
    private const double DefaultLeft = 0;
    private const double DefaultUpSpeed = 3;
    private const double DefaultDownSpeed = 3;
    private const double DefaultTrigger = 0;

    /// <summary>Every <c>MovingPlatform</c> the map places, in level-data order.</summary>
    public static MovingPlatformDefinition[] Build(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var platforms = new List<MovingPlatformDefinition>();

        foreach (var entity in map.Entities)
        {
            if (entity.Type != EntityTypes.MovingPlatform)
                continue;

            platforms.Add(Of(entity));
        }

        return [.. platforms];
    }

    /// <summary>One entity as a definition.</summary>
    /// <remarks>
    /// ⚠️ <b><c>top</c> is the distance travelled UP, so the offset is
    /// <c>(left, -top)</c></b> — a positive <c>top</c> moves the platform towards
    /// smaller y. The sign lives in the offset rather than in the property, which
    /// is why every expression below carries a <c>-def.Top</c> rather than
    /// negating once at the door: <c>point_direction(0, 0, left, -top)</c> is
    /// what the reference writes, and moving the negation changes nothing here
    /// but would diverge the moment somebody reused the value.
    /// </remarks>
    public static MovingPlatformDefinition Of(LevelEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new MovingPlatformDefinition
        {
            StartX = entity.X,
            StartY = entity.Y,
            Top = entity.GetReal("top", DefaultTop),
            Left = entity.GetReal("left", DefaultLeft),
            UpSpeed = entity.GetReal("upspeed", DefaultUpSpeed),
            DownSpeed = entity.GetReal("downspeed", DefaultDownSpeed),
            Trigger = entity.GetReal("trigger", DefaultTrigger),
        };
    }

    /// <summary>
    /// The state every platform starts a round in, from the map.
    /// </summary>
    /// <remarks>
    /// The <c>Place</c> name and the shape of the call match
    /// <see cref="Generators.Place"/> and <see cref="ControlPoints.Place"/>,
    /// because it belongs beside them at all four sites that build a world.
    /// <para>
    /// <see cref="Build"/> runs here and again in the
    /// <see cref="CollisionWorld"/> constructor. It is a pure function of the
    /// map, so the two agree; doing it twice at load rather than threading one
    /// array through four call sites is the cheaper trade.
    /// </para>
    /// </remarks>
    public static void Place(ref WorldState state, LevelMap map) => Reset(Build(map), ref state);

    /// <summary>
    /// The state every platform starts a round in.
    /// </summary>
    /// <remarks>
    /// Create.xml sets <c>movementState = 1</c> and User Event 1 overrides it to
    /// 0 when the map gave a <c>trigger</c> — so an untriggered platform is
    /// already moving on the first tick and a triggered one waits.
    /// </remarks>
    public static void Reset(ReadOnlySpan<MovingPlatformDefinition> definitions, ref WorldState state)
    {
        var count = Math.Min(definitions.Length, WorldState.MaxMovingPlatforms);
        state.MovingPlatformCount = count;

        for (var i = 0; i < count; i++)
        {
            state.MovingPlatforms[i] = new MovingPlatformState
            {
                X = definitions[i].StartX,
                Y = definitions[i].StartY,
                MovementState = definitions[i].Trigger == 0 ? Up : Stopped,
            };
        }
    }

    /// <summary>Every platform's End Step, in instance order.</summary>
    public static void EndStep(ReadOnlySpan<MovingPlatformDefinition> definitions, ref WorldState state)
    {
        var count = Math.Min(state.MovingPlatformCount, definitions.Length);

        for (var i = 0; i < count; i++)
            Step(definitions[i], ref state.MovingPlatforms[i]);
    }

    /// <summary>
    /// One platform's End Step: move, then decide whether this was the tick it
    /// turned round.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The direction round-trip through degrees is deliberate and must not
    /// be simplified.</b> The reference takes a <c>point_direction</c>, converts
    /// it back with <c>degtorad</c> and feeds <c>cos</c>/<c>sin</c> — so a
    /// vertical platform's horizontal step is <c>cos(90°)</c>, which is 6.1e-17
    /// rather than 0, and <c>x_</c> drifts by that much every tick. Normalising
    /// <c>(left, -top)</c> directly would be arithmetically nicer and would put
    /// this port's <c>x_</c> a few ULPs from the client's, which the wire's
    /// <c>x_*5</c> would eventually disagree about. CLAUDE.md: transcribe GML
    /// arithmetic, not GML structure.
    /// <para>
    /// ⚠️ <b>No motion snap.</b> <c>Gml.SnapToNearInteger</c> applies to
    /// <c>lengthdir_*</c> and to the four motion variables when they are
    /// recomputed from each other; this is a bare <c>cos</c> into a plain
    /// instance variable, which is on that helper's explicit "does NOT apply"
    /// list.
    /// </para>
    /// <para>
    /// <c>global.delta_factor</c> is 1 at 30 Hz and is dropped here as it is
    /// everywhere else in this project.
    /// </para>
    /// </remarks>
    public static void Step(in MovingPlatformDefinition definition, ref MovingPlatformState platform)
    {
        double speed;
        double direction;

        if (platform.MovementState == Up)
        {
            speed = definition.UpSpeed;
            direction = Gml.PointDirection(0, 0, definition.Left, -definition.Top);
        }
        else if (platform.MovementState == Down)
        {
            speed = definition.DownSpeed;
            direction = Gml.PointDirection(definition.Left, -definition.Top, 0, 0);
        }
        else
        {
            speed = 0;
            direction = 0;
        }

        platform.X += Math.Cos(Gml.DegToRad(direction)) * speed;
        platform.Y += -Math.Sin(Gml.DegToRad(direction)) * speed;

        var totalDistance = Gml.PointDistance(0, 0, definition.Left, -definition.Top);

        // A platform told to travel nowhere parks itself on the start and stops.
        // ⚠️ This is also the divide-by-zero guard for the projection below, so
        // it has to stay above it rather than folding into the arms.
        if (Gml.LessOrEqual(totalDistance, 0))
        {
            platform.MovementState = Stopped;
            platform.X = definition.StartX;
            platform.Y = definition.StartY;
            return;
        }

        // How far along (left, -top) the platform now is, as a length: the dot
        // product with that vector over its own length.
        var distance =
            (((platform.X - definition.StartX) * definition.Left) +
             ((platform.Y - definition.StartY) * -definition.Top)) / totalDistance;

        if (platform.MovementState == Down && Gml.LessOrEqual(distance, 0))
        {
            // ⚠️ Snapped back to the start exactly, not left where the step
            // landed. The overshoot is discarded rather than carried into the
            // next leg, so a platform's period is an exact number of ticks and
            // does not drift.
            platform.X = definition.StartX;
            platform.Y = definition.StartY;
            platform.MovementState = definition.Trigger == 0 ? Up : Stopped;
        }
        else if (platform.MovementState == Up && Gml.GreaterOrEqual(distance, totalDistance))
        {
            // ⚠️ And NOT snapped at the far end — the reference only clamps at
            // the start. A platform therefore turns round a fraction of a pixel
            // past its stated top and the asymmetry is real, not a slip here.
            platform.MovementState = Down;
        }
    }
}

/// <summary>
/// One <c>MovingPlatform</c>'s map-given configuration: where it starts, how far
/// it goes, how fast, and what starts it.
/// </summary>
/// <remarks>
/// Read-only for the life of a map, so it is passed <em>to</em> the tick beside
/// the <see cref="CollisionWorld"/> rather than living in
/// <see cref="WorldState"/> — docs/BOTAI.md item 1.
/// </remarks>
public readonly record struct MovingPlatformDefinition
{
    /// <summary>Where the entity sits in the map, and where a full cycle returns it to.</summary>
    public required double StartX { get; init; }

    /// <inheritdoc cref="StartX"/>
    public required double StartY { get; init; }

    /// <summary>How far up the platform travels. The offset is <c>(Left, -Top)</c>.</summary>
    public required double Top { get; init; }

    /// <summary>How far left the platform travels. Positive is towards larger x.</summary>
    public required double Left { get; init; }

    /// <summary>Pixels per tick on the way out.</summary>
    public required double UpSpeed { get; init; }

    /// <summary>Pixels per tick on the way back.</summary>
    public required double DownSpeed { get; init; }

    /// <summary>
    /// 0 runs the platform on a loop; 1 waits to be stood on; 2 waits to be stood
    /// on by an intel carrier.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A real, not an enum, and it is only ever compared against 0 here.</b>
    /// Telling 1 from 2 is the carry's job — the reference reads
    /// <c>other.trigger == 2 and intel</c> inside the <c>with(Character)</c>
    /// block — and that is the part this port does not have. A non-zero trigger
    /// therefore parks the platform for ever rather than waiting for a rider,
    /// which is exactly what the reference does when nothing can stand on it.
    /// </remarks>
    public required double Trigger { get; init; }
}

/// <summary>One <c>MovingPlatform</c>'s per-tick state: the three values on the wire.</summary>
/// <remarks>
/// ⚠️ <b><see cref="X"/> and <see cref="Y"/> are the reference's <c>x_</c> and
/// <c>y_</c>, not its <c>x</c> and <c>y</c>.</b> The underscored pair is the real
/// position the motion accumulates into; GM8's <c>x</c>/<c>y</c> are
/// <c>round()</c>ed copies it draws and collides with. The wire carries the
/// underscored pair (<c>User Event 10</c>), so the rounded copy is derived where
/// it is needed rather than stored.
/// </remarks>
public struct MovingPlatformState
{
    /// <summary>The reference's <c>x_</c>.</summary>
    public double X;

    /// <summary>The reference's <c>y_</c>.</summary>
    public double Y;

    /// <summary>0 stopped, 1 outbound, 2 returning.</summary>
    public byte MovementState;

    /// <summary>GM8's <c>x</c> — the rounded copy it draws and collides with.</summary>
    public readonly double RoundedX => Gml.Round(X);

    /// <summary>GM8's <c>y</c>.</summary>
    public readonly double RoundedY => Gml.Round(Y);
}
