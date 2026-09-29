using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <c>MovingPlatform.events/End Step.xml</c>'s <c>with(Character)</c> block: who
/// a platform picks up, who it shoves, and who it drops.
/// </summary>
/// <remarks>
/// <b>362 lines that no test reached at all.</b> Confirmed by
/// neutralising <see cref="MovingPlatforms.Carry"/> and re-running: <b>2,601
/// passing before and after</b>. Not an oversight anybody could have spotted from
/// the suite — the code is <em>unreachable</em> through a shipped map.
/// <para>
/// <b>Why.</b> A platform's collision shape comes from an embedded resource the
/// map entity names, and <b>every moving platform in every shipped map resolves
/// to a null mask</b> — including all four of <c>gg_debug2</c>'s.
/// <c>MovingPlatformDefinition.Mask</c>'s own remarks say so. A platform with no
/// mask meets nobody, so <c>Carry</c> returns at its first line whatever the map
/// does. The carry is real code for real maps in the wild; this repo just has no
/// map that reaches it.
/// </para>
/// <para>
/// So these tests build the platform rather than loading one, through the
/// explicit-definitions <c>EndStep</c> overload — the same shape
/// <c>KillBoxes.CollisionStep</c> has, and for the same reason.
/// </para>
/// <para>
/// <b>COVERAGE, measured rather than estimated.</b> Neutralising the carry
/// fails <b>nine of the eleven</b> tests here. The two survivors are meant to
/// survive: <see cref="TheRoundedPairCatchesUpAtTheEndOfTheTick"/> is about
/// <c>EndStep</c>'s bookkeeping and correctly does not depend on the carry, and
/// <see cref="ThePushOutRollsBackSomebodyItMerelyBrushes"/> asserts an absence
/// whose meaning comes from a mutation instead — see its own remarks.
/// </para>
/// <para>
/// Each branch has been killed on its own, and the failure each produces is the
/// bug it exists to prevent:
/// <list type="bullet">
/// <item>Neutralising the push-out fails the carried case.</item>
/// <item>Deleting the roll-back's restore leaves the character displaced by
/// <b>0.39px</b> — the teleport that branch prevents.</item>
/// <item>Making <c>StandOn</c> ignore <c>PlaceFree</c> shoves a blocked rider
/// <b>2px into a ceiling</b> (220 → 218).</item>
/// </list>
/// </para>
/// <para>
/// <b>Four of the eleven assert an ABSENCE</b> and would pass on a deleted
/// carry, so each states its own control. That is not decoration: an earlier
/// draft had four such tests with no controls, all of them passed, and
/// neutralising the carry failed only four of eight.
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
    /// <b>Found by search, not by arithmetic.</b> "On top" is
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
    /// <b>Every "not carried" assertion below states its own control through
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
    /// The whole point of the carry: a rider moves with the platform.
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

        // Zeroed, not preserved: a rider is not falling, and leaving the
        // accumulated gravity would drop them the instant they step off.
        Assert.Equal(0, state.Characters[0].VSpeed);
    }

    /// <summary>
    /// Holding DOWN drops you through — "Cannot collide or move along if they
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

        // The control, in the same test: the identical scene without the key
        // IS carried. Without this the assertions above pass on a deleted carry.
        Assert.NotEqual(0, CarriedY(static (ref WorldState s) => s.Characters[0].VSpeed = 3));
    }

    /// <summary>
    /// A character rising FASTER than the platform passes through it.
    /// </summary>
    /// <remarks>
    /// <b>The reference's guard is two-part and the comment says why</b>:
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
    /// ...and one rising SLOWER is still carried, which is the half a
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
    /// A trigger platform is started by being stood on — and a stopped
    /// platform scans for riders at all only because of the second arm of
    /// <c>EndStep</c>'s guard.
    /// </summary>
    /// <remarks>
    /// This is the one branch where the carry runs on a platform that did not
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
    /// A <c>trigger == 2</c> platform waits for an INTEL CARRIER, and an
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
    /// A platform with no mask carries nobody — which is every platform in
    /// every shipped map.
    /// </summary>
    /// <remarks>
    /// <b>Not a degenerate case to be tidied away.</b> GM8 with
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

    /// <summary>The integer step this platform is about to take.</summary>
    /// <remarks>
    /// Taken by running <see cref="MovingPlatforms.Step"/> on a COPY rather
    /// than by assuming a sign convention. The step goes through
    /// <c>point_direction</c> and <c>cos</c>/<c>sin</c>, so a "purely vertical"
    /// platform's horizontal component is 6.1e-17 rather than 0 — deriving it
    /// here would be a second opinion about arithmetic the port is deliberately
    /// faithful to.
    /// </remarks>
    private static (double X, double Y) StepOf(
        in MovingPlatformDefinition definition, in MovingPlatformState platform)
    {
        var probe = platform;
        MovingPlatforms.Step(definition, ref probe);

        return (Gml.Round(probe.X) - platform.RoundedX, Gml.Round(probe.Y) - platform.RoundedY);
    }

    /// <summary>
    /// A spot in the platform's <b>swept volume</b>: clear of it now, not
    /// standing on it, and overlapping where it is about to be.
    /// </summary>
    /// <param name="fromAbove">
    /// True to want a spot the platform reaches by moving VERTICALLY into it —
    /// the character ends up on top and is carried. False to want one it reaches
    /// HORIZONTALLY, where the character ends up beside it and is rolled back.
    /// </param>
    /// <remarks>
    /// <b>These are <see cref="MovingPlatforms"/>' own three entry conditions
    /// for the push-out</b>, in its order — <c>!Meets(x, y)</c>,
    /// <c>!Meets(x, y + 1)</c>, <c>Meets(x - stepX, y - stepY)</c>. Searched
    /// rather than computed, for the same reason <see cref="PutRiderOnTop"/>
    /// searches: the answer depends on three different origin conventions, and a
    /// fixture that re-derives them is a fixture that can disagree with the code
    /// about what it is testing.
    /// </remarks>
    private static (double X, double Y)? FindSweptSpot(
        in MovingPlatformDefinition definition,
        in MovingPlatformState platform,
        double stepX,
        double stepY,
        bool fromAbove)
    {
        var probe = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        var box = probe.Box;
        var collider = ColliderFor(definition, platform);

        for (var dy = -64.0; dy <= 64; dy++)
        {
            for (var dx = -64.0; dx <= 64; dx++)
            {
                var x = platform.RoundedX + dx;
                var y = platform.RoundedY + dy;

                if (collider.Meets(box, x, y) || collider.Meets(box, x, y + 1))
                    continue;

                if (!collider.Meets(box, x - stepX, y - stepY))
                    continue;

                // Which shift alone produces the overlap tells the two apart.
                var vertical = collider.Meets(box, x, y - stepY);

                if (vertical == fromAbove)
                    return (x, y);
            }
        }

        return null;
    }

    private static void Put(ref WorldState state, int slot, double x, double y, double vspeed)
    {
        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = x;
        character.Y = y;
        character.Facing = 1;
        character.VSpeed = vspeed;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, x, y);
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = Team.Red;
        state.Players[slot].Class = PlayerClass.Scout;
    }

    /// <summary>
    /// The push-out's carried branch: a rising platform drives into somebody
    /// just above it, backs them out onto its top, and takes them with it.
    /// </summary>
    /// <remarks>
    /// <b>The rewind moves them the "wrong" way on purpose.</b>
    /// <c>good_move_contact_solid</c> heads towards
    /// <c>(x - stepX, y - stepY)</c> — <em>against</em> the platform's motion —
    /// until it touches the platform at its OLD position. For a rising platform
    /// that is downwards, which lands the character on the old top surface; the
    /// carry then lifts them by the step. The net move is therefore SMALLER than
    /// the step, and that is the signature this asserts rather than a clean
    /// one-step lift.
    /// </remarks>
    [Fact]
    public void ThePushOutCarriesSomebodyARisingPlatformDrivesInto()
    {
        var (state, definitions) = Scene();
        var platform = state.MovingPlatforms[0];
        var (stepX, stepY) = StepOf(definitions[0], platform);

        var spot = FindSweptSpot(definitions[0], platform, stepX, stepY, fromAbove: true);

        Assert.True(spot is not null, "no spot in the rising platform's swept volume");

        Put(ref state, 0, spot!.Value.X, spot.Value.Y, vspeed: 3);
        MovingPlatforms.EndStep(World, ref state, definitions);

        // Carried: lifted, and the vspeed zeroed exactly as a rider's is.
        Assert.True(
            state.Characters[0].Y < spot.Value.Y,
            $"the platform drove into them and left them at {state.Characters[0].Y}, "
            + $"not above {spot.Value.Y}");

        Assert.Equal(0, state.Characters[0].VSpeed);
    }

    /// <summary>
    /// The push-out's ROLL-BACK: somebody a platform merely brushes is put
    /// back exactly where they were.
    /// </summary>
    /// <remarks>
    /// <b>The trap this branch exists for.</b> The rewind has already moved
    /// the character by the time the "did they end up on top" test runs, so
    /// leaving them there would teleport anyone a platform passed near. The
    /// reference restores the original position and the port has to as well.
    /// <para>
    /// <b>This assertion is an ABSENCE, so it needs its meaning from
    /// elsewhere</b> — an unchanged position is also what a push-out that never
    /// ran produces. Two things supply it: the carried case above proves the
    /// fixture reaches the push-out at all, and the roll-back was
    /// mutation-checked — deleting the restore leaves the character displaced and
    /// fails this test. Neither is visible from the assertion alone, which is why
    /// it is written down here.
    /// </para>
    /// <para>
    /// The vspeed is asserted UNTOUCHED, which is the half that separates a
    /// roll-back from a carry: only the carried branch zeroes it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePushOutRollsBackSomebodyItMerelyBrushes()
    {
        // A platform travelling sideways: whoever it drives into ends up beside
        // it, never on top, so the roll-back is the only reachable outcome.
        var (state, definitions) = Scene(top: 0, left: 64);
        var platform = state.MovingPlatforms[0];
        var (stepX, stepY) = StepOf(definitions[0], platform);

        Assert.True(stepX != 0, "the sideways platform did not move sideways");

        var spot = FindSweptSpot(definitions[0], platform, stepX, stepY, fromAbove: false);

        Assert.True(spot is not null, "no spot beside the sideways platform's swept volume");

        Put(ref state, 0, spot!.Value.X, spot.Value.Y, vspeed: 3);
        MovingPlatforms.EndStep(World, ref state, definitions);

        Assert.Equal(spot.Value.X, state.Characters[0].X);
        Assert.Equal(spot.Value.Y, state.Characters[0].Y);
        Assert.Equal(3, state.Characters[0].VSpeed);
    }

    /// <summary>
    /// The gap between a platform's rounded y and the y a rider stands at, taken
    /// once on open ground so it can be reused anywhere on the map.
    /// </summary>
    /// <remarks>
    /// Measured through the same search <see cref="PutRiderOnTop"/> uses, for
    /// the same reason: it is the carry's own definition of "standing on", not a
    /// second opinion about box origins.
    /// </remarks>
    private static double OnTopOffset()
    {
        var (state, definitions) = Scene();
        var platform = state.MovingPlatforms[0];

        PutRiderOnTop(ref state, 0, definitions[0], platform);

        return state.Characters[0].Y - platform.RoundedY;
    }

    /// <summary>
    /// Somewhere on the real map a character fits, but cannot rise by
    /// <paramref name="lift"/> — a ceiling.
    /// </summary>
    /// <remarks>
    /// <b>Searched on the shipped map rather than invented.</b> This is the one
    /// branch of the carry that cannot be reached on open ground: it needs a
    /// destination that is genuinely not <c>PlaceFree</c>, which means real
    /// geometry. Scanning for it is cheaper and far more honest than hard-coding
    /// a coordinate that a future map edit would silently turn into open air —
    /// the test would still pass and would be testing nothing.
    /// </remarks>
    private static (double X, double Y)? FindCeiling(double lift)
    {
        var probe = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        var box = probe.Box;
        var filter = probe.Filter;
        var map = World.Map;

        for (var y = 64.0; y < map.Height - 64; y += 4)
        {
            for (var x = 64.0; x < map.Width - 64; x += 4)
            {
                if (!World.PlaceFree(box, x, y, filter))
                    continue;

                if (!World.PlaceFree(box, x, y - lift, filter))
                    return (x, y);
            }
        }

        return null;
    }

    /// <summary>
    /// A rider whose destination is blocked is LEFT WHERE THEY ARE, not shoved
    /// into the wall.
    /// </summary>
    /// <remarks>
    /// <c>charSetSolids()</c>/<c>charUnsetSolids()</c> around a <c>place_free</c>:
    /// the reference moves the rider only if the destination is clear. A platform
    /// rising into a ceiling therefore slides out from under whoever is standing
    /// on it rather than pushing them through the map.
    /// <para>
    /// <b>The vspeed is asserted UNTOUCHED as well as the position.</b> The
    /// zeroing lives inside the same <c>if</c>, so a blocked rider keeps whatever
    /// they had — and checking only the position would pass on an implementation
    /// that skipped the move but still zeroed the fall.
    /// </para>
    /// <para>
    /// The control is <see cref="CarriedY()"/>: the same rider on open ground
    /// IS moved. Without it this is another absence that a deleted carry would
    /// satisfy.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARiderWhoseDestinationIsBlockedIsNotMoved()
    {
        var (state, definitions) = Scene();
        var (stepX, stepY) = StepOf(definitions[0], state.MovingPlatforms[0]);

        Assert.True(stepY < 0, "the fixture platform should be rising");

        // The rider has to be unable to follow the platform up.
        var ceiling = FindCeiling(-stepY);

        Assert.True(ceiling is not null, "no ceiling anywhere on the map; the search is broken");

        var offset = OnTopOffset();

        // Put the platform where standing on it puts the rider under the ceiling.
        state.MovingPlatforms[0].X = ceiling!.Value.X;
        state.MovingPlatforms[0].Y = ceiling.Value.Y - offset;
        state.MovingPlatforms[0].RoundedX = ceiling.Value.X;
        state.MovingPlatforms[0].RoundedY = ceiling.Value.Y - offset;

        Put(ref state, 0, ceiling.Value.X, ceiling.Value.Y, vspeed: 3);

        // Assert the rider really is on top before running, or a fixture that
        // drifted would test the "not touching it" path and still look green.
        var collider = ColliderFor(definitions[0], state.MovingPlatforms[0]);
        var box = state.Characters[0].Box;

        Assert.False(collider.Meets(box, ceiling.Value.X, ceiling.Value.Y));
        Assert.True(collider.Meets(box, ceiling.Value.X, ceiling.Value.Y + 1));

        MovingPlatforms.EndStep(World, ref state, definitions);

        Assert.Equal(ceiling.Value.X, state.Characters[0].X);
        Assert.Equal(ceiling.Value.Y, state.Characters[0].Y);
        Assert.Equal(3, state.Characters[0].VSpeed);

        // The control: an unblocked rider on open ground is carried.
        Assert.NotEqual(0, CarriedY(static (ref WorldState s) => s.Characters[0].VSpeed = 3));
    }

    /// <summary>
    /// The rounded pair is brought up to date after the carry, not before.
    /// </summary>
    /// <remarks>
    /// <b>This one is about <c>EndStep</c>'s bookkeeping and not about the
    /// carry</b>, so it stays green with <c>Carry</c> neutralised — correctly.
    /// It is here because the lag it pins is what the carry depends on.
    /// </remarks>
    /// <remarks>
    /// <b>The lag is the whole reason <c>RoundedX</c>/<c>RoundedY</c> are
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
