using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The four boxes that shove whatever stands in them.
/// </summary>
/// <remarks>
/// <c>Character.events/Collision with MoveBox{Up,Down,Left,Right}.xml</c> and
/// <c>MoveBox.events/{Create, User Event 1}.xml</c>.
/// <para>
/// <b>One shipped map places any</b> — <c>tdm_mantic</c>, nine up and six down,
/// the same map that places the six pit falls. No left or right box and no
/// <c>speed</c> override exists anywhere, so both of those arms are only
/// reachable from a hand-built entity, which is what <see cref="MoveBoxes.Of"/>
/// is for. No recording can gate any of it: no fixture is on
/// <c>tdm_mantic</c>.
/// </para>
/// </remarks>
public sealed class MoveBoxTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Open ground on <c>ctf_truefort</c>, clear of everything.</summary>
    private const double X = 2280;

    private const double Y = 200;

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??= new CollisionWorld(Level());

    private static MoveBoxRect[] BoxAt(
        PushDirection direction, double x = X, double y = Y, string? speed = null)
    {
        var properties = new Dictionary<string, GgonValue>();

        if (speed is not null)
            properties["speed"] = new GgonString(speed);

        return
        [
            MoveBoxes.Of(
                new LevelEntity
                {
                    Type = EntityTypes.MoveBoxUp,
                    X = x,
                    Y = y,
                    Properties = properties,
                },
                direction),
        ];
    }

    private static void Put(ref WorldState state, int slot, double x = X, double y = Y)
    {
        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = x;
        character.Y = y;
        character.Facing = 1;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, x, y);
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = Team.Red;
        state.Players[slot].Class = PlayerClass.Scout;
    }

    // --- what the maps place --------------------------------------------------

    /// <summary>
    /// ⚠️ <b>One shipped map places move boxes: <c>tdm_mantic</c>, nine up and
    /// six down.</b>
    /// </summary>
    /// <remarks>
    /// Asserted rather than commented, the way the killing boxes' count is —
    /// the previous count in this project's notes had been wrong for several
    /// sessions.
    /// </remarks>
    [Fact]
    public void OnlyTeamDeathmatchManticPlacesAnyMoveBoxes()
    {
        var withBoxes = new List<string>();

        foreach (var path in Directory.GetFiles(ReferenceTree.IncludedFiles, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path)!;
            var boxes = MoveBoxes.Build(LevelMap.FromPng(name, ReferenceTree.ReadMap(name)));

            if (boxes.Length == 0)
                continue;

            withBoxes.Add(name);

            Assert.Equal(9, boxes.Count(b => b.Direction == PushDirection.Up));
            Assert.Equal(6, boxes.Count(b => b.Direction == PushDirection.Down));
            Assert.Equal(0, boxes.Count(b => b.Direction is PushDirection.Left or PushDirection.Right));

            // And every one on the Create event's default power.
            Assert.All(boxes, b => Assert.Equal(MoveBoxes.DefaultPushPower, b.PushPower));
        }

        Assert.Equal(["tdm_mantic"], withBoxes);
    }

    /// <summary>The same 42x42 collider as the spawn room and the killing boxes.</summary>
    [Fact]
    public void AMoveBoxIsTheSameFortyTwoPixelSquare()
    {
        var box = BoxAt(PushDirection.Up)[0];

        Assert.Equal(41, box.Right - box.Left);
        Assert.Equal(41, box.Bottom - box.Top);
    }

    // --- the push -------------------------------------------------------------

    /// <summary>
    /// Each of the four pushes its own way, by the box's power.
    /// </summary>
    /// <remarks>
    /// Up is negative, as everywhere else in GM8.
    /// </remarks>
    [Theory]
    [InlineData(PushDirection.Up, 0, -5)]
    [InlineData(PushDirection.Down, 0, 5)]
    [InlineData(PushDirection.Left, -5, 0)]
    [InlineData(PushDirection.Right, 5, 0)]
    public void EachBoxPushesItsOwnWay(PushDirection direction, double hspeed, double vspeed)
    {
        var state = default(WorldState);
        Put(ref state, 1);

        MoveBoxes.CollisionStep(World, ref state, BoxAt(direction));

        Assert.Equal(hspeed, state.Characters[1].HSpeed);
        Assert.Equal(vspeed, state.Characters[1].VSpeed);
    }

    /// <summary>
    /// ⚠️ <b>It accelerates rather than setting</b>, and fires on every tick the
    /// boxes overlap — which is what makes a column of them a lift.
    /// </summary>
    /// <remarks>
    /// The control against a port that wrote <c>vspeed = -pushPower</c>: that
    /// would give -5 however many times it ran.
    /// </remarks>
    [Fact]
    public void StandingInABoxKeepsAccelerating()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        for (var i = 0; i < 4; i++)
            MoveBoxes.CollisionStep(World, ref state, BoxAt(PushDirection.Up));

        Assert.Equal(-20, state.Characters[1].VSpeed);
    }

    /// <summary>Two overlapping boxes both apply.</summary>
    [Fact]
    public void TwoBoxesCompose()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        MoveBoxRect[] both = [BoxAt(PushDirection.Up)[0], BoxAt(PushDirection.Right)[0]];

        MoveBoxes.CollisionStep(World, ref state, both);

        Assert.Equal(-5, state.Characters[1].VSpeed);
        Assert.Equal(5, state.Characters[1].HSpeed);
    }

    /// <summary>
    /// ⚠️ <b>The push is a <em>component</em> write</b>, so it goes through
    /// <see cref="Motion"/>'s component path and re-derives the polar pair with
    /// the snap.
    /// </summary>
    /// <remarks>
    /// The observable: a character shoved straight up out of a standstill ends
    /// with <c>direction</c> exactly 90 and <c>speed</c> exactly 5, where a port
    /// that wrote the components without re-deriving would leave both at
    /// whatever they were.
    /// </remarks>
    [Fact]
    public void ThePushRederivesDirectionAndSpeed()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].Motion.SetPolar(0, 0);

        MoveBoxes.CollisionStep(World, ref state, BoxAt(PushDirection.Up));

        Assert.Equal(5, state.Characters[1].Motion.Speed, 12);
        Assert.Equal(90, state.Characters[1].Motion.Direction, 12);
    }

    /// <summary>
    /// ⚠️ <b><c>moveStatus = 0</c> is outside the frame-rate guard</b>, so a
    /// move box cancels a rocket-jump's control override as well as shoving.
    /// </summary>
    [Fact]
    public void AMoveBoxClearsMoveStatus()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].MoveStatus = 1;

        MoveBoxes.CollisionStep(World, ref state, BoxAt(PushDirection.Up));

        Assert.Equal(0, state.Characters[1].MoveStatus);
    }

    /// <summary>A character beside a box is untouched.</summary>
    [Fact]
    public void ACharacterBesideABoxIsUntouched()
    {
        var state = default(WorldState);
        Put(ref state, 1, x: X + 400);
        state.Characters[1].MoveStatus = 1;

        MoveBoxes.CollisionStep(World, ref state, BoxAt(PushDirection.Up));

        Assert.Equal(0, state.Characters[1].VSpeed);
        Assert.Equal(1, state.Characters[1].MoveStatus);
    }

    /// <summary>
    /// A slot with no character is not pushed.
    /// </summary>
    /// <remarks>
    /// The reference's event fires on <em>instances</em>, and a dead character
    /// has none — but a cleared slot in this port still holds a
    /// <see cref="CharacterState"/> with coordinates in it, so the liveness
    /// guard has to be written rather than implied. The control the mutation
    /// audit asked for: dropping the guard survived every other test here.
    /// </remarks>
    [Fact]
    public void ADeadSlotIsNotPushed()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Alive[1] = false;

        MoveBoxes.CollisionStep(World, ref state, BoxAt(PushDirection.Up));

        Assert.Equal(0, state.Characters[1].VSpeed);
    }

    // --- the speed property ---------------------------------------------------

    /// <summary>
    /// A map may override the push with a <c>speed</c> property, and anything
    /// unparseable falls back to the Create event's 5.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>readProperty(..., REAL, pushPower)</c> — a REAL, not a BOOL, so
    /// unlike the cabinet's switches this one accepts any number the map writes.
    /// No shipped map writes one.
    /// </remarks>
    [Theory]
    [InlineData("12", 12)]
    [InlineData("2.5", 2.5)]
    [InlineData("-3", -3)]
    [InlineData("fast", MoveBoxes.DefaultPushPower)]
    [InlineData(null, MoveBoxes.DefaultPushPower)]
    public void TheSpeedPropertyOverridesThePushPower(string? written, double expected)
    {
        Assert.Equal(expected, BoxAt(PushDirection.Right, speed: written)[0].PushPower);
    }

    // --- through a whole tick -------------------------------------------------

    /// <summary>
    /// <c>tdm_mantic</c>'s lifts really lift, through a real tick.
    /// </summary>
    /// <remarks>
    /// The control for the wiring — every other test here calls
    /// <see cref="MoveBoxes.CollisionStep"/> directly, which gates the rule and
    /// not the phase it runs in. A character in an up-box has to end the tick
    /// rising <em>against</em> gravity, which is what makes this more than a
    /// field being written.
    /// </remarks>
    [Fact]
    public void ATickLiftsWhoeverIsStandingInAnUpBox()
    {
        const string Mantic = "tdm_mantic";
        var level = LevelMap.FromPng(Mantic, ReferenceTree.ReadMap(Mantic)).ForArea(1);
        var world = new CollisionWorld(level);

        // ⚠️ Not just any up box. A character's box is 34 pixels tall and a
        // move box is 42, so one standing in the middle of a lift can also
        // overlap a *down* box stacked beside it — and 5 up plus 5 down is
        // exactly nothing. The first up box on tdm_mantic is one of those, which
        // is how this test first failed. Pick one that is unambiguous.
        var up = world.MoveBoxes.First(candidate =>
        {
            if (candidate.Direction != PushDirection.Up)
                return false;

            var at = Aabb.ForCharacter(
                CharacterState.Fresh(PlayerClass.Scout, Team.Red).Box,
                (candidate.Left + candidate.Right) / 2.0,
                (candidate.Top + candidate.Bottom) / 2.0);

            return !world.MoveBoxes.Any(other =>
                other.Direction != PushDirection.Up
                && at.Overlaps(new Aabb(other.Left, other.Top, other.Right, other.Bottom)));
        });

        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);
        Put(ref state, 1, (up.Left + up.Right) / 2.0, (up.Top + up.Bottom) / 2.0);

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);

        // Up is negative. Gravity pulls the other way, so a character that is
        // rising at the end of the tick can only have been pushed.
        Assert.True(state.Characters[1].VSpeed < 0,
            $"the lift did not lift: vspeed {state.Characters[1].VSpeed}");
    }
}
