using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <c>MovingPlatform.events/End Step.xml</c>'s <c>with(Character)</c> block: who
/// a platform picks up, who it shoves, and who it drops.
/// </summary>
/// <remarks>
/// ⛔ <b>362 lines that no test reached at all until 2026-09-02.</b> Confirmed by
/// neutralising <see cref="MovingPlatforms.Carry"/> and re-running: <b>2,601
/// passing before and after</b>. Not an oversight anybody could have spotted from
/// the suite — the code is <em>unreachable</em> through a shipped map.
/// <para>
/// ⛔ <b>Why.</b> A platform's collision shape comes from an embedded resource the
/// map entity names, and <b>every moving platform in every shipped map resolves
/// to a null mask</b> — including all four of <c>gg_debug2</c>'s.
/// <c>MovingPlatformDefinition.Mask</c>'s own remarks say so. A platform with no
/// mask meets nobody, so <c>Carry</c> returns at its first line whatever the map
/// does. The carry is real code for real maps in the wild; this repo just has no
/// map that reaches it.
/// </para>
/// <para>
/// ⚠️ So these tests build the platform rather than loading one, through the
/// explicit-definitions <c>EndStep</c> overload — the same shape
/// <c>KillBoxes.CollisionStep</c> has, and for the same reason.
/// </para>
/// <para>
/// ⛔ <b>WHAT IS STILL UNCOVERED, measured rather than estimated.</b> Neutralising
/// <see cref="MovingPlatforms.Carry"/> now fails seven of the eight tests here
/// (the survivor is <see cref="TheRoundedPairCatchesUpAtTheEndOfTheTick"/>, which
/// is about <c>EndStep</c>'s bookkeeping and correctly does not depend on the
/// carry). But neutralising <c>PushOut</c> <b>alone</b> still leaves the entire
/// suite green — <b>2,609 passing</b>. So:
/// <list type="bullet">
/// <item><b>Uncovered: all of <c>PushOut</c></b> — the platform driving into
/// somebody, the rewind, and the roll-back that puts back anyone who did not end
/// up on top. That roll-back is the subtlety the port's own remarks flag, and
/// nothing tests it.</item>
/// <item><b>Uncovered: <c>StandOn</c>'s blocked branch</b> — a rider pinned
/// against a wall is left where they are rather than shoved into it. Needs a
/// platform placed against real geometry.</item>
/// </list>
/// ⚠️ Both need the character positioned in the swept volume rather than on top,
/// which is a geometry problem this fixture's search helper does not solve.
/// <b>Do not read the seven-of-eight above as covering the carry.</b>
/// </para>
/// </remarks>
public sealed class MovingPlatformCarryTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Open ground, clear of everything — <c>KillBoxTests</c>'s spot.</summary>
    private const double PlatformX = 2280;

    private const double PlatformY = 200;

    private const int MaskWidth = 48;

    private const int MaskHeight = 8;

    private static CollisionWorld? _shared;

    private static CollisionWorld World =>
        _shared ??= new CollisionWorld(
            LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1));

    /// <summary>A solid rectangle, which is what a platform sprite is.</summary>
    private static SpriteMask SolidMask()
    {
        var solid = new bool[MaskWidth * MaskHeight];
        Array.Fill(solid, true);

        return new SpriteMask(MaskWidth, MaskHeight, solid);
    }

    private static MovingPlatformDefinition Definition(
        double top = 64, double left = 0, double trigger = 0, SpriteMask? mask = null) =>
        new()
        {
            Mask = mask ?? SolidMask(),
            Scale = 1,
            StartX = PlatformX,
            StartY = PlatformY,
            Top = top,
            Left = left,
            UpSpeed = 2,
            DownSpeed = 2,
            Trigger = trigger,
        };

    /// <summary>The collider the carry will build, so a test can aim at it.</summary>
    private static PreciseCollision.PlatformCollider ColliderFor(
        in MovingPlatformDefinition definition, in MovingPlatformState platform) =>
        new()
        {
            Mask = definition.Mask,
            X = platform.RoundedX,
            Y = platform.RoundedY,
            XScale = definition.Scale,
            YScale = definition.Scale,
        };

    /// <summary>
    /// Puts <paramref name="slot"/> standing exactly on top of the platform.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Found by search, not by arithmetic.</b> "On top" is
    /// <c>Meets(box, x, y + 1) &amp;&amp; !Meets(box, x, y)</c> — the carry's own
    /// test — and the y that satisfies it depends on the character's box origin,
    /// the mask's origin and the scale. Computing it here would encode a second
    /// opinion about all three, and a test that disagrees with the code about
    /// where "standing on" is tests nothing.
    /// </remarks>
    private static void PutRiderOnTop(
        ref WorldState state,
        int slot,
        in MovingPlatformDefinition definition,
        in MovingPlatformState platform,
        double x = PlatformX)
    {
        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = x;
        character.Facing = 1;

        var collider = ColliderFor(definition, platform);

        for (var y = platform.RoundedY - 64; y <= platform.RoundedY + 64; y++)
        {
            if (!collider.Meets(character.Box, x, y) && collider.Meets(character.Box, x, y + 1))
            {
                character.Y = y;

                state.Characters[slot] = character;
                state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, x, y);
                state.Alive[slot] = true;
                state.AddPlayer(slot);
                state.Players[slot].Team = Team.Red;
                state.Players[slot].Class = PlayerClass.Scout;

                return;
            }
        }

        Assert.Fail("no y puts the character on top of the platform; the fixture is broken");
    }

    private static (WorldState State, MovingPlatformDefinition[] Definitions) Scene(
        double top = 64, double left = 0, double trigger = 0, SpriteMask? mask = null)
    {
        var definitions = new[] { Definition(top, left, trigger, mask) };
        var state = default(WorldState);

        MovingPlatforms.Reset(definitions, ref state);

        return (state, definitions);
    }

    private delegate void Adjust(ref WorldState state);

    /// <summary>
    /// How far the rider moved in y over one tick. <b>Zero means not carried.</b>
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every "not carried" assertion below states its own control through
    /// this.</b> A test that only asserts the rider did not move passes just as
    /// happily when the carry is DELETED — measured, not supposed: neutralising
    /// <c>Carry</c> leaves four of the eight tests in this file green, and they
    /// are exactly the four that assert an absence. Pairing each with the same
    /// scene minus the guard condition is what makes the absence mean something.
    /// </remarks>
    private static double CarriedY(Adjust? adjust = null, double trigger = 0, bool maskless = false)
    {
        var (state, definitions) = Scene(trigger: trigger);

        PutRiderOnTop(ref state, 0, definitions[0], state.MovingPlatforms[0]);

        var startY = state.Characters[0].Y;

        adjust?.Invoke(ref state);

        MovingPlatforms.EndStep(
            World, ref state, maskless ? [definitions[0] with { Mask = null }] : definitions);

        return state.Characters[0].Y - startY;
    }

    /// <summary>
    /// ⛔ The whole point of the carry: a rider moves with the platform.
    /// </summary>
    [Fact]
    public void ARiderMovesWithThePlatform()
    {
        var (state, definitions) = Scene();

        PutRiderOnTop(ref state, 0, definitions[0], state.MovingPlatforms[0]);

        var startY = state.Characters[0].Y;

        // Falling when the platform reaches them, which is the ordinary case.
        state.Characters[0].VSpeed = 3;

        MovingPlatforms.EndStep(World, ref state, definitions);

        var stepY = state.MovingPlatforms[0].RoundedY - PlatformY;

        Assert.True(stepY != 0, "the platform did not move, so nothing was carried");
        Assert.Equal(startY + stepY, state.Characters[0].Y);

        // ⚠️ Zeroed, not preserved: a rider is not falling, and leaving the
        // accumulated gravity would drop them the instant they step off.
        Assert.Equal(0, state.Characters[0].VSpeed);
    }

    /// <summary>
    /// ⛔ Holding DOWN drops you through — "Cannot collide or move along if they
    /// hold down".
    /// </summary>
    [Fact]
    public void HoldingDownDropsARiderThrough()
    {
        var (state, definitions) = Scene();

        PutRiderOnTop(ref state, 0, definitions[0], state.MovingPlatforms[0]);

        var startY = state.Characters[0].Y;
        state.Characters[0].VSpeed = 3;
        state.Characters[0].KeyState |= PlayerKeys.Down;

        MovingPlatforms.EndStep(World, ref state, definitions);

        Assert.Equal(startY, state.Characters[0].Y);
        Assert.Equal(3, state.Characters[0].VSpeed);

        // ⚠️ The control, in the same test: the identical scene without the key
        // IS carried. Without this the assertions above pass on a deleted carry.
        Assert.NotEqual(0, CarriedY(static (ref WorldState s) => s.Characters[0].VSpeed = 3));
    }

    /// <summary>
    /// ⛔ A character rising FASTER than the platform passes through it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference's guard is two-part and the comment says why</b>:
    /// testing only for upward motion would let a rising platform pass through
    /// somebody rising more slowly, and comparing speeds alone would stop a
    /// platform carrying anybody downwards. This is the branch that needs both —
    /// the character is rising (negative vspeed) AND rising faster than the
    /// platform's own dy.
    /// </remarks>
    [Fact]
    public void ACharacterRisingFasterThanThePlatformPassesThrough()
    {
        var (state, definitions) = Scene();

        PutRiderOnTop(ref state, 0, definitions[0], state.MovingPlatforms[0]);

        var startY = state.Characters[0].Y;

        // The platform climbs at 2px a tick, so dy is -2. -9 is faster.
        state.Characters[0].VSpeed = -9;

        MovingPlatforms.EndStep(World, ref state, definitions);

        Assert.Equal(startY, state.Characters[0].Y);
        Assert.Equal(-9, state.Characters[0].VSpeed);

        // The control, and it is the other half of the two-part guard: rising
        // SLOWER than the platform is still carried.
        Assert.NotEqual(0, CarriedY(static (ref WorldState s) => s.Characters[0].VSpeed = -1));
    }

    /// <summary>
    /// ⚠️ ...and one rising SLOWER is still carried, which is the half a
    /// speed-only guard would break.
    /// </summary>
    [Fact]
    public void ACharacterRisingSlowerThanThePlatformIsStillCarried()
    {
        var (state, definitions) = Scene();

        PutRiderOnTop(ref state, 0, definitions[0], state.MovingPlatforms[0]);

        var startY = state.Characters[0].Y;

        // Rising, but at 1px a tick against the platform's 2.
        state.Characters[0].VSpeed = -1;

        MovingPlatforms.EndStep(World, ref state, definitions);

        var stepY = state.MovingPlatforms[0].RoundedY - PlatformY;

        Assert.Equal(startY + stepY, state.Characters[0].Y);
        Assert.Equal(0, state.Characters[0].VSpeed);
    }

    /// <summary>
    /// ⛔ A trigger platform is started by being stood on — and a stopped
    /// platform scans for riders at all only because of the second arm of
    /// <c>EndStep</c>'s guard.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is the one branch where the carry runs on a platform that did not
    /// move. <c>waitingOnATrigger</c> is what gets it there; without it a trigger
    /// platform would sit still for ever because nothing would ever notice the
    /// rider.
    /// </remarks>
    [Fact]
    public void ARiderStartsATriggerPlatform()
    {
        var (state, definitions) = Scene(trigger: 1);

        Assert.Equal(0, state.MovingPlatforms[0].MovementState);

        PutRiderOnTop(ref state, 0, definitions[0], state.MovingPlatforms[0]);

        MovingPlatforms.EndStep(World, ref state, definitions);

        Assert.Equal(1, state.MovingPlatforms[0].MovementState);
    }

    /// <summary>
    /// ⛔ A <c>trigger == 2</c> platform waits for an INTEL CARRIER, and an
    /// ordinary rider does not start it.
    /// </summary>
    [Fact]
    public void OnlyAnIntelCarrierStartsATriggerTwoPlatform()
    {
        var (empty, definitions) = Scene(trigger: 2);

        PutRiderOnTop(ref empty, 0, definitions[0], empty.MovingPlatforms[0]);
        MovingPlatforms.EndStep(World, ref empty, definitions);

        Assert.Equal(0, empty.MovingPlatforms[0].MovementState);

        var (carrying, carrierDefinitions) = Scene(trigger: 2);

        PutRiderOnTop(ref carrying, 0, carrierDefinitions[0], carrying.MovingPlatforms[0]);
        carrying.Characters[0].CarryingIntel = true;

        MovingPlatforms.EndStep(World, ref carrying, carrierDefinitions);

        Assert.Equal(1, carrying.MovingPlatforms[0].MovementState);
    }

    /// <summary>
    /// ⛔ A platform with no mask carries nobody — which is every platform in
    /// every shipped map.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not a degenerate case to be tidied away.</b> GM8 with
    /// <c>sprite_index = -1</c> collides with nothing, so this is the reference's
    /// behaviour and not a guard the port invented. It is also the reason this
    /// whole file has to build its platform by hand.
    /// </remarks>
    [Fact]
    public void APlatformWithNoMaskCarriesNobody()
    {
        var (state, definitions) = Scene();

        // Placed against the masked definition, then run against a maskless one:
        // same geometry, same motion, no carry.
        PutRiderOnTop(ref state, 0, definitions[0], state.MovingPlatforms[0]);

        var startY = state.Characters[0].Y;
        var maskless = new[] { definitions[0] with { Mask = null } };

        MovingPlatforms.EndStep(World, ref state, maskless);

        Assert.True(
            state.MovingPlatforms[0].RoundedY != PlatformY,
            "the platform did not move, so this proves nothing about the mask");

        Assert.Equal(startY, state.Characters[0].Y);

        // The control: the same platform WITH its mask carries the same rider.
        Assert.Equal(0, CarriedY(maskless: true));
        Assert.NotEqual(0, CarriedY());
    }

    /// <summary>
    /// ⚠️ The rounded pair is brought up to date after the carry, not before.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This one is about <c>EndStep</c>'s bookkeeping and not about the
    /// carry</b>, so it stays green with <c>Carry</c> neutralised — correctly.
    /// It is here because the lag it pins is what the carry depends on.
    /// </remarks>
    /// <remarks>
    /// ⛔ <b>The lag is the whole reason <c>RoundedX</c>/<c>RoundedY</c> are
    /// stored rather than derived.</b> Through the carry the platform's mask sits
    /// at its PREVIOUS rounded position while the step describes where it is
    /// going; deriving them from <c>X</c>/<c>Y</c> would put the mask where the
    /// platform is heading and test every rider against the wrong pixels. This
    /// pins the update that closes the tick.
    /// </remarks>
    [Fact]
    public void TheRoundedPairCatchesUpAtTheEndOfTheTick()
    {
        var (state, definitions) = Scene();

        Assert.Equal(PlatformY, state.MovingPlatforms[0].RoundedY);

        MovingPlatforms.EndStep(World, ref state, definitions);

        Assert.Equal(
            Gml.Round(state.MovingPlatforms[0].Y), state.MovingPlatforms[0].RoundedY);

        Assert.True(
            state.MovingPlatforms[0].RoundedY < PlatformY,
            "an outbound platform with Top = 64 should have climbed");
    }
}
