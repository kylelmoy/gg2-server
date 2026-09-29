using GG2.Maps;
using GG2.Protocol;

namespace GG2.Simulation;

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
/// The carry is the <c>with(Character)</c> block that picks up riders and
/// shoves whoever the platform drives into. Every branch of it is a
/// <c>place_meeting</c> against the platform's own mask, and <b>a
/// <c>MovingPlatform</c> has no sprite</b> (<c>MovingPlatform.xml</c> declares
/// <c>&lt;sprite/&gt;</c> empty, unlike its parent <c>DropdownPlatform</c>'s
/// <c>sprite44</c>). The mask comes from an embedded resource in the map's own
/// metadata, decoded via <see cref="PreciseCollision"/>.
/// <para>
/// <b>A platform with no usable <c>resource</c> still carries nobody</b>, and
/// that is the reference's behaviour rather than a shortcut: GM8 leaves
/// <c>sprite_index = -1</c> and collides with nothing. All four of
/// <c>gg_debug2</c>'s original platforms are like that; its raft is not.
/// </para>
/// <para>
/// <b>The parent still matters, and it is why STANDING needed no new rule.</b>
/// Because <c>MovingPlatform</c>'s parent IS <c>DropdownPlatform</c>, every
/// <c>with(DropdownPlatform)</c> in the character physics already means "or a
/// moving platform". What is here is only the part that has no equivalent: moving
/// a character <em>with</em> the platform.
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

            platforms.Add(Of(entity) with
            {
                Mask = MaskOf(map, entity),
                Scale = PreciseCollision.ScaleOf(ScaleOf(entity)),
            });
        }

        return [.. platforms];
    }

    /// <summary>
    /// The platform's collision shape, decoded from the map's own metadata.
    /// </summary>
    /// <remarks>
    /// <c>stringToResource.gml</c>: the entity names a <c>resource</c>, the value
    /// is the raw bytes of a PNG or GIF stored as a string in the <c>meta</c>
    /// block, and <c>sprite_add(name, 1, 1, 0, 0, 0)</c> turns it into a sprite.
    /// <para>
    /// <b>Every failure here returns null, and every one of them is also what
    /// GM8 does.</b> No <c>resource</c>, a name the metadata does not carry, or
    /// bytes neither decoder accepts all leave <c>sprite_index = -1</c> — a
    /// platform that moves and collides with nothing. <c>ctf_2dfort-ani</c>'s
    /// <c>animap</c> is a real instance: nine bytes of truncated GIF.
    /// </para>
    /// </remarks>
    private static SpriteMask? MaskOf(LevelMap map, LevelEntity entity)
    {
        var name = entity.GetString("resource");
        if (string.IsNullOrEmpty(name))
            return null;

        var resource = map.Metadata?.GetString(name);
        if (resource is null)
            return null;

        // The level-data text is decoded Latin-1, so each char is one byte.
        var bytes = new byte[resource.Length];
        for (var i = 0; i < resource.Length; i++)
            bytes[i] = (byte)resource[i];

        return SpriteMask.Decode(bytes);
    }

    /// <summary>
    /// The entity's scale, which a map may set three different ways.
    /// </summary>
    /// <remarks>
    /// User Event 1 reads a <c>scale</c> property into <c>image_xscale</c> and
    /// <c>image_yscale</c>; the entity loader separately applies <c>xscale</c> and
    /// <c>yscale</c>. Real maps use one, the other, or both — <c>cp_scrap</c> sets
    /// all three to 3.
    /// </remarks>
    private static double ScaleOf(LevelEntity entity) =>
        Math.Max(entity.GetReal("scale", 1), Math.Max(entity.XScale, entity.YScale));

    /// <summary>One entity as a definition.</summary>
    /// <remarks>
    /// <b><c>top</c> is the distance travelled UP, so the offset is
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

                // Create.xml: x = round(x_) at birth, so the lagging copy starts
                // in step rather than at zero. A zeroed RoundedX would make the
                // first tick's step the platform's whole map coordinate.
                RoundedX = Gml.Round(definitions[i].StartX),
                RoundedY = Gml.Round(definitions[i].StartY),

                MovementState = definitions[i].Trigger == 0 ? Up : Stopped,
            };
        }
    }

    /// <summary>Every platform's End Step, in instance order.</summary>
    /// <remarks>
    /// Motion then carry, per platform, which is the reference's order inside one
    /// End Step — and platforms run in instance order, so an earlier one has
    /// already moved its riders before a later one looks at them.
    /// </remarks>
    public static void EndStep(CollisionWorld world, ref WorldState state)
    {
        ArgumentNullException.ThrowIfNull(world);
        EndStep(world, ref state, world.MovingPlatforms);
    }

    /// <inheritdoc cref="EndStep(CollisionWorld, ref WorldState)"/>
    /// <param name="definitions">
    /// The platforms to run, which is <see cref="CollisionWorld.MovingPlatforms"/>
    /// in play.
    /// </param>
    /// <remarks>
    /// <b>Taken explicitly so a test can supply a platform with a MASK.</b>
    /// A definition's mask comes from an embedded resource the map entity names,
    /// and <b>every moving platform in every shipped map resolves to null</b> —
    /// so a platform loaded from a map carries nobody and the whole of
    /// <see cref="Carry"/> is unreachable through <see cref="CollisionWorld"/>.
    /// That is why the carry went 362 lines with no coverage at all. The same
    /// shape as <c>KillBoxes.CollisionStep</c>, and for the same reason: the
    /// shipped maps cannot reach the code.
    /// </remarks>
    public static void EndStep(
        CollisionWorld world,
        ref WorldState state,
        ReadOnlySpan<MovingPlatformDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(world);

        var count = Math.Min(state.MovingPlatformCount, definitions.Length);

        for (var i = 0; i < count; i++)
        {
            ref var platform = ref state.MovingPlatforms[i];
            ref readonly var definition = ref definitions[i];

            var before = platform;
            Step(definition, ref platform);

            // colldx/colldy: the integer step, measured against the LAGGING
            // rounded position. The reference computes both before assigning
            // x/y, which is why RoundedX still holds last tick's value here.
            var stepX = Gml.Round(platform.X) - before.RoundedX;
            var stepY = Gml.Round(platform.Y) - before.RoundedY;

            // MovingPlatform/End Step.xml — the block runs when the platform
            // moved, OR when a trigger platform is parked waiting to be stood on.
            // That second arm is why a stopped trigger platform still scans:
            // it is how it notices a rider and starts.
            var moved = stepX != 0 || stepY != 0;
            var waitingOnATrigger = platform.MovementState == Stopped && definition.Trigger > 0;

            if (moved || waitingOnATrigger)
            {
                Carry(world, ref state, in definition, ref platform, in before, stepX, stepY);

                platform.RoundedX = Gml.Round(platform.X);
                platform.RoundedY = Gml.Round(platform.Y);
            }
        }
    }

    /// <summary>
    /// <c>with(Character)</c>: pick up the riders, and shove whoever the platform
    /// drove into.
    /// </summary>
    /// <remarks>
    /// <b>Every test here is against the platform at its PREVIOUS rounded
    /// position</b> (<paramref name="before"/>), because the reference does not
    /// assign <c>x</c>/<c>y</c> until after this block. The platform is where it
    /// was; <paramref name="stepX"/>/<paramref name="stepY"/> is where it is
    /// going.
    /// <para>
    /// <b>A platform with no mask carries nobody</b>, and the early return is
    /// not an optimisation — GM8 with <c>sprite_index = -1</c> collides with
    /// nothing, so every branch below would be false anyway.
    /// </para>
    /// </remarks>
    private static void Carry(
        CollisionWorld world,
        ref WorldState state,
        in MovingPlatformDefinition definition,
        ref MovingPlatformState platform,
        in MovingPlatformState before,
        double stepX,
        double stepY)
    {
        if (definition.Mask is not { } mask)
            return;

        var collider = new PreciseCollision.PlatformCollider
        {
            Mask = mask,
            X = before.RoundedX,
            Y = before.RoundedY,
            XScale = definition.Scale,
            YScale = definition.Scale,
        };

        // The vertical component of this tick's motion, which the "passing
        // through" refusal compares a rising character's vspeed against.
        var dy = platform.Y - before.Y;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var character = ref state.Characters[slot];
            var box = character.Box;

            // Holding DOWN drops you through: "Cannot collide or move along if
            // they hold down".
            if ((character.KeyState & PlayerKeys.Down) != 0)
                continue;

            // Two-part on purpose, and the reference comments on it: checking
            // only for upward motion would let a rising platform pass through
            // somebody rising more slowly, and comparing vspeeds alone would stop
            // a platform carrying anyone downwards.
            if (character.VSpeed < 0 && character.VSpeed < dy)
                continue;

            // Already inside it: not picked up.
            if (collider.Meets(box, character.X, character.Y))
                continue;

            if (collider.Meets(box, character.X, character.Y + 1))
            {
                StandOn(world, ref state, in definition, ref platform, ref character, slot, stepX, stepY);
                continue;
            }

            // Not standing on it, but the platform has driven into them. Back
            // them out along the platform's own motion and see whether they end
            // up on top.
            if (!collider.Meets(box, character.X - stepX, character.Y - stepY))
                continue;

            PushOut(world, ref state, in definition, ref platform, ref character, slot,
                stepX, stepY, in collider);
        }
    }

    /// <summary>A rider: move them with the platform, unless that would bury them.</summary>
    private static void StandOn(
        CollisionWorld world,
        ref WorldState state,
        in MovingPlatformDefinition definition,
        ref MovingPlatformState platform,
        ref CharacterState character,
        int slot,
        double stepX,
        double stepY)
    {
        var box = character.Box;
        var filter = character.Filter;

        // charSetSolids()/charUnsetSolids() around a place_free: the rider is
        // moved only if the destination is clear, so somebody pinned against a
        // wall is left where they are rather than pushed into it.
        if (world.PlaceFree(box, character.X + stepX, character.Y + stepY, filter))
        {
            character.X += stepX;
            character.Y += stepY;

            // Zeroed, not preserved. A rider is not falling, and leaving the
            // accumulated gravity would make them drop the instant they step off.
            character.VSpeed = 0;
        }

        // The trigger, which only a rider can pull: 1 is anybody, 2 is an intel
        // carrier only.
        if (platform.MovementState != Stopped)
            return;

        var carriesIntel = state.Characters[slot].CarryingIntel;

        if (definition.Trigger == 1 || (definition.Trigger == 2 && carriesIntel))
            platform.MovementState = Up;
    }

    /// <summary>
    /// The platform drove into somebody: back them out, and carry them if they
    /// ended up on top.
    /// </summary>
    /// <remarks>
    /// <b>Rewind, test, then commit or roll back.</b> The reference moves the
    /// character with <c>good_move_contact_solid</c> against the platform made
    /// briefly solid, checks whether they now stand on it, and <b>restores their
    /// original position if they do not</b>. Leaving them where the rewind put
    /// them would teleport anyone a platform merely brushed.
    /// </remarks>
    private static void PushOut(
        CollisionWorld world,
        ref WorldState state,
        in MovingPlatformDefinition definition,
        ref MovingPlatformState platform,
        ref CharacterState character,
        int slot,
        double stepX,
        double stepY,
        in PreciseCollision.PlatformCollider collider)
    {
        var box = character.Box;
        var filter = character.Filter;

        var originalX = character.X;
        var originalY = character.Y;

        var mover = new MoverState { X = character.X, Y = character.Y };

        Movement.GoodMoveContactSolid(
            world, box, filter, ref mover,
            CharacterHitObstacle.PointDirection(character.X, character.Y, character.X - stepX, character.Y - stepY),
            CharacterHitObstacle.PointDistance(character.X, character.Y, character.X - stepX, character.Y - stepY),
            out _,
            collider);

        character.X = mover.X;
        character.Y = mover.Y;

        var carried = false;

        if (collider.Meets(box, character.X, character.Y + 1)
            && world.PlaceFree(box, character.X + stepX, character.Y + stepY, filter))
        {
            character.X += stepX;
            character.Y += stepY;
            character.VSpeed = 0;
            carried = true;
        }

        if (!carried)
        {
            character.X = originalX;
            character.Y = originalY;
            return;
        }

        if (platform.MovementState != Stopped)
            return;

        var carriesIntel = state.Characters[slot].CarryingIntel;

        if (definition.Trigger == 1 || (definition.Trigger == 2 && carriesIntel))
            platform.MovementState = Up;
    }

    /// <summary>
    /// One platform's End Step: move, then decide whether this was the tick it
    /// turned round.
    /// </summary>
    /// <remarks>
    /// <b>The direction round-trip through degrees is deliberate and must not
    /// be simplified.</b> The reference takes a <c>point_direction</c>, converts
    /// it back with <c>degtorad</c> and feeds <c>cos</c>/<c>sin</c> — so a
    /// vertical platform's horizontal step is <c>cos(90°)</c>, which is 6.1e-17
    /// rather than 0, and <c>x_</c> drifts by that much every tick. Normalising
    /// <c>(left, -top)</c> directly would be arithmetically nicer and would put
    /// this port's <c>x_</c> a few ULPs from the client's, which the wire's
    /// <c>x_*5</c> would eventually disagree about. CLAUDE.md: transcribe GML
    /// arithmetic, not GML structure.
    /// <para>
    /// <b>No motion snap.</b> <c>Gml.SnapToNearInteger</c> applies to
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
        // This is also the divide-by-zero guard for the projection below, so
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
            // Snapped back to the start exactly, not left where the step
            // landed. The overshoot is discarded rather than carried into the
            // next leg, so a platform's period is an exact number of ticks and
            // does not drift.
            platform.X = definition.StartX;
            platform.Y = definition.StartY;
            platform.MovementState = definition.Trigger == 0 ? Up : Stopped;
        }
        else if (platform.MovementState == Up && Gml.GreaterOrEqual(distance, totalDistance))
        {
            // And NOT snapped at the far end — the reference only clamps at
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
    /// <summary>The platform's collision shape, or null when it has none.</summary>
    /// <remarks>
    /// <b>Null is the common case and is not a failure.</b> A
    /// <c>MovingPlatform</c> has no sprite of its own; the mask comes from an
    /// embedded resource the entity names, and a platform with no
    /// <c>resource</c>, a name that resolves to nothing, or bytes no decoder
    /// accepts keeps <c>sprite_index = -1</c> in GM8 too. Such a platform moves
    /// and carries nobody — which is all four of <c>gg_debug2</c>'s original
    /// platforms.
    /// </remarks>
    public SpriteMask? Mask { get; init; }

    /// <summary><c>image_xscale</c>/<c>image_yscale</c>; real maps set 1 to 6.</summary>
    public int Scale { get; init; }

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
    /// <b>A real, not an enum, and it is only ever compared against 0 here.</b>
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
/// <b><see cref="X"/> and <see cref="Y"/> are the reference's <c>x_</c> and
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

    /// <summary>
    /// GM8's <c>x</c> — the rounded copy it draws and collides with.
    /// </summary>
    /// <remarks>
    /// <b>STORED, and it LAGS <see cref="X"/> by one step during the End
    /// Step.</b> The reference computes <c>colldx = round(x_) - round(x)</c>
    /// after moving <c>x_</c> but before assigning <c>x</c>, so throughout the
    /// carry the platform's mask sits at its PREVIOUS rounded position while
    /// <c>colldx</c> describes the step it is about to take. Deriving this from
    /// <c>X</c> instead would put the mask where the platform is going rather
    /// than where it is, and every rider would be tested against the wrong
    /// pixels.
    /// </remarks>
    public double RoundedX;

    /// <summary>GM8's <c>y</c>. Lags <see cref="Y"/> the same way.</summary>
    public double RoundedY;
}
