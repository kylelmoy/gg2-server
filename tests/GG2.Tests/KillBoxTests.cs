using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The three map boxes that kill whatever stands in them, and the flag-return
/// arm they unlock.
/// </summary>
/// <remarks>
/// <c>Character.events/Collision with {KillBox, FragBox, PitFall}.xml</c>,
/// <c>Sentry.events/Collision with {KillBox, FragBox}.xml</c>, and
/// <c>Character.events/Destroy.xml</c>'s
/// <c>lastDamageSource in (KILL_BOX, FRAG_BOX, PITFALL)</c> branch.
/// <para>
/// <b>Only one shipped map places any of them</b> — <c>tdm_mantic</c>, six pit
/// falls — so a kill box and a frag box exist only on custom maps, and every
/// test here that needs one builds it. No recording can gate any of it: no
/// fixture is on <c>tdm_mantic</c>, and the boxes send no message of their own.
/// </para>
/// </remarks>
public sealed class KillBoxTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Open ground on <c>ctf_truefort</c>, clear of everything.</summary>
    private const double X = 2280;

    private const double Y = 200;

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??= new CollisionWorld(Level());

    /// <summary>A world whose only killing box is of the given kind, at (X, Y).</summary>
    /// <remarks>
    /// Built rather than loaded: <c>ctf_truefort</c> places none, and the one
    /// map that does is Team Deathmatch and has no intel — so the return arm
    /// cannot be reached from a shipped map at all.
    /// </remarks>
    private static KillBoxRect[] BoxAt(KillBoxKind kind, double x = X, double y = Y) =>
    [
        KillBoxes.Of(
            new LevelEntity
            {
                Type = EntityTypes.KillBox,
                X = x,
                Y = y,
                Properties = new Dictionary<string, GgonValue>(),
            },
            kind),
    ];

    private static void Put(
        ref WorldState state, int slot, double x = X, double y = Y, Team team = Team.Red)
    {
        var character = CharacterState.Fresh(PlayerClass.Scout, team);
        character.X = x;
        character.Y = y;
        character.Facing = 1;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, x, y);
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = PlayerClass.Scout;
    }

    // --- what the maps actually place -----------------------------------------

    /// <summary>
    /// <b>One shipped map places boxes, and its six are all pit falls.</b>
    /// </summary>
    /// <remarks>
    /// It is six <em>boxes</em> on <em>one</em> map. Asserted rather than
    /// commented so the claim cannot rot.
    /// </remarks>
    [Fact]
    public void OnlyTeamDeathmatchManticPlacesAnyBoxes()
    {
        var withBoxes = new List<string>();

        foreach (var name in ReferenceTree.ShippedMapNames())
        {
            var map = LevelMap.FromPng(name, ReferenceTree.ReadMap(name));
            var boxes = KillBoxes.Build(map);

            if (boxes.Length == 0)
                continue;

            withBoxes.Add(name);

            Assert.Equal(6, boxes.Length);
            Assert.All(boxes, b => Assert.Equal(KillBoxKind.Pit, b.Kind));
        }

        Assert.Equal(["tdm_mantic"], withBoxes);
    }

    /// <summary>All three share <c>sprite64</c>, the spawn room's 42x42 collider.</summary>
    [Fact]
    public void AllThreeBoxesAreTheSameFortyTwoPixelSquare()
    {
        foreach (var kind in Enum.GetValues<KillBoxKind>())
        {
            var box = BoxAt(kind)[0];

            Assert.Equal(41, box.Right - box.Left);
            Assert.Equal(41, box.Bottom - box.Top);
        }
    }

    // --- what a box does to a character ---------------------------------------

    /// <summary>
    /// <b>A box does not kill; it sets <c>hp = 0</c></b> and lets the
    /// victim's own End Step do the rest — so the kill log, the scoring and the
    /// respawn all behave exactly as they do for a bullet.
    /// </summary>
    [Theory]
    [InlineData(KillBoxKind.Kill, DamageSource.KillBox)]
    [InlineData(KillBoxKind.Pit, DamageSource.Pitfall)]
    [InlineData(KillBoxKind.Frag, DamageSource.FragBox)]
    public void ABoxZeroesHitPointsAndNamesItself(KillBoxKind kind, DamageSource expected)
    {
        var state = default(WorldState);
        Put(ref state, 1);

        KillBoxes.CollisionStep(World, ref state, BoxAt(kind));

        Assert.Equal(0, state.Characters[1].Hp);
        Assert.Equal(expected, state.Characters[1].LastDamageSource);

        // Still alive: the death is End Step's business.
        Assert.True(state.IsAlive(1));
    }

    /// <summary>
    /// <b>Somebody else's claim survives the box.</b> If anyone but the
    /// victim hurt them recently the kill is handed over — as a <em>gib</em> for
    /// a frag box and a plain finish-off for the other two — and the dealer is
    /// not cleared, so they keep the credit.
    /// </summary>
    [Theory]
    [InlineData(KillBoxKind.Kill, DamageSource.FinishedOff)]
    [InlineData(KillBoxKind.Pit, DamageSource.FinishedOff)]
    [InlineData(KillBoxKind.Frag, DamageSource.FinishedOffGib)]
    public void ARecentDealerTakesTheKillFromTheBox(KillBoxKind kind, DamageSource expected)
    {
        var state = default(WorldState);
        Put(ref state, 1);
        Put(ref state, 2, x: X + 400, team: Team.Blue);
        state.Characters[1].LastDamageDealer = 2;

        KillBoxes.CollisionStep(World, ref state, BoxAt(kind));

        Assert.Equal(expected, state.Characters[1].LastDamageSource);
        Assert.Equal(2, state.Characters[1].LastDamageDealer);
    }

    /// <summary>
    /// And a victim whose only recent dealer is themselves is the box's own —
    /// the guard is <c>lastDamageDealer != player</c>, not merely "somebody".
    /// </summary>
    [Fact]
    public void SelfInflictedDamageDoesNotTakeTheKillFromTheBox()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].LastDamageDealer = 1;

        KillBoxes.CollisionStep(World, ref state, BoxAt(KillBoxKind.Pit));

        Assert.Equal(DamageSource.Pitfall, state.Characters[1].LastDamageSource);
    }

    /// <summary>A character outside the box is untouched.</summary>
    [Fact]
    public void ACharacterBesideABoxIsUntouched()
    {
        var state = default(WorldState);
        Put(ref state, 1, x: X + 400);

        KillBoxes.CollisionStep(World, ref state, BoxAt(KillBoxKind.Kill));

        Assert.Equal(ClassStats.For(PlayerClass.Scout).MaxHp, state.Characters[1].Hp);
    }

    // --- what a box does to a sentry ------------------------------------------

    /// <summary>
    /// <b>A pit fall leaves a sentry alone and the other two destroy it.</b>
    /// <c>Sentry.events/</c> has no <c>Collision with PitFall</c> at all, so an
    /// engineer's gun survives what its owner does not.
    /// </summary>
    [Theory]
    [InlineData(KillBoxKind.Kill, true)]
    [InlineData(KillBoxKind.Frag, true)]
    [InlineData(KillBoxKind.Pit, false)]
    public void OnlyAKillOrFragBoxDestroysASentry(KillBoxKind kind, bool destroyed)
    {
        var state = default(WorldState);
        Put(ref state, 1, x: X + 400, team: Team.Red);
        state.Characters[1].NutsNBolts = 100;

        Sentries.Build(ref state, 1, X, Y, 1);
        Assert.True(state.HasSentry[1]);

        state.Sentries[1].LastDamageDealer = 2;
        var before = state.Sentries[1].Hp;

        KillBoxes.CollisionStep(World, ref state, BoxAt(kind));

        if (destroyed)
        {
            Assert.True(state.Sentries[1].Hp <= 0, $"sentry survived on {state.Sentries[1].Hp}");

            // `lastDamageDealer = noone` — a sentry a box destroys credits
            // nobody, even if somebody had been shooting it.
            Assert.Equal(CharacterState.Nobody, state.Sentries[1].LastDamageDealer);
        }
        else
        {
            Assert.Equal(before, state.Sentries[1].Hp);
            Assert.Equal(2, state.Sentries[1].LastDamageDealer);
        }
    }

    // --- the flag-return arm the boxes unlock ---------------------------------

    /// <summary>
    /// <b>A carrier killed by a box sends the briefcase home instead of
    /// leaving it in the pit.</b> The Destroy event drops it first and then
    /// returns it, and the return is <em>broadcast</em> where the drop
    /// deliberately is not.
    /// </summary>
    /// <remarks>
    /// Not reachable on any shipped map — the only map with boxes has no intel.
    /// </remarks>
    [Fact]
    public void ACarrierKilledByABoxSendsTheFlagHome()
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        Assert.True(state.BlueIntel.HasBase, "the map placed no blue briefcase");

        Put(ref state, 1);
        state.Characters[1].CarryingIntel = true;
        state.BlueIntel.OnField = false;

        KillBoxes.CollisionStep(World, ref state, BoxAt(KillBoxKind.Pit));

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events, World);

        Assert.True(state.BlueIntel.OnField);
        Assert.Equal(state.BlueIntel.HomeX, state.BlueIntel.X);
        Assert.Equal(state.BlueIntel.HomeY, state.BlueIntel.Y);
    }

    /// <summary>
    /// The control: an ordinary death leaves the flag exactly where the body
    /// fell.
    /// </summary>
    /// <remarks>
    /// Without it the test above passes for a port that always sends the flag
    /// home, which would quietly break every CTF recording.
    /// </remarks>
    [Fact]
    public void AnOrdinaryDeathLeavesTheFlagWhereItFell()
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        Put(ref state, 1);
        state.Characters[1].CarryingIntel = true;
        state.Characters[1].Hp = 0;
        state.Characters[1].LastDamageSource = DamageSource.Scattergun;
        state.BlueIntel.OnField = false;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events, World);

        Assert.True(state.BlueIntel.OnField);
        Assert.Equal(X, state.BlueIntel.X);
        Assert.NotEqual(state.BlueIntel.HomeX, state.BlueIntel.X);
    }

    /// <summary>
    /// <b>A box kill that somebody else took the credit for sends the flag home
    /// too</b>, which is a deliberate departure from the reference.
    /// </summary>
    /// <remarks>
    /// Upstream the arm reads <c>lastDamageSource</c>, and a box that finds an
    /// enemy's claim on the victim from the last <c>ASSIST_TIME</c> has
    /// overwritten it with <c>FINISHED_OFF</c> — so an assisted pit death leaves
    /// the briefcase at the bottom of the pit for thirty seconds and an
    /// unassisted one sends it home. The port asks
    /// <see cref="CharacterState.DiedToHazard"/>, which the box sets beside the
    /// source, so both go home.
    /// <para>
    /// <b>The premise is asserted, not assumed</b>: if the box ever stopped
    /// handing the kill over, this would pass for the wrong reason and say
    /// nothing about the departure it exists to pin.
    /// </para>
    /// <para>
    /// <see cref="AnOrdinaryDeathLeavesTheFlagWhereItFell"/> is what stops this
    /// passing for a port that always sends the flag home.
    /// docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried into the port".
    /// </para>
    /// </remarks>
    [Fact]
    public void AStolenBoxKillAlsoSendsTheFlagHome()
    {
        var state = default(WorldState);
        Intel.Place(ref state, Level());

        Put(ref state, 1);
        Put(ref state, 2, x: X + 400, team: Team.Blue);
        state.Characters[1].CarryingIntel = true;
        state.Characters[1].LastDamageDealer = 2;
        state.BlueIntel.OnField = false;

        KillBoxes.CollisionStep(World, ref state, BoxAt(KillBoxKind.Pit));

        // The premise: the box handed the kill over, so the kill log says
        // FINISHED_OFF and only DiedToHazard still names the box.
        Assert.Equal(DamageSource.FinishedOff, state.Characters[1].LastDamageSource);
        Assert.True(state.Characters[1].DiedToHazard);

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events, World);

        Assert.True(state.BlueIntel.OnField);
        Assert.Equal(state.BlueIntel.HomeX, state.BlueIntel.X);
        Assert.Equal(state.BlueIntel.HomeY, state.BlueIntel.Y);
    }

    /// <summary>
    /// The other control: an ordinary death does not set
    /// <see cref="CharacterState.DiedToHazard"/>, so nothing but a box can
    /// reach the return arm.
    /// </summary>
    [Fact]
    public void AnOrdinaryDeathDoesNotCountAsAHazard()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        Assert.False(state.Characters[1].DiedToHazard);

        state.Characters[1].Hp = 0;
        state.Characters[1].LastDamageSource = DamageSource.FinishedOffGib;

        Assert.False(state.Characters[1].DiedToHazard);
    }

    // --- through a whole tick -------------------------------------------------

    /// <summary>
    /// A pit kills through a real tick: the box zeroes the hit points in the
    /// collision stage and End Step turns that into a death.
    /// </summary>
    /// <remarks>
    /// The control for the wiring — every other test here calls
    /// <see cref="KillBoxes.CollisionStep"/> directly, which gates the rule and
    /// not the phase it runs in.
    /// </remarks>
    [Fact]
    public void ATickKillsWhoeverIsStandingInAPit()
    {
        const string Mantic = "tdm_mantic";
        var level = LevelMap.FromPng(Mantic, ReferenceTree.ReadMap(Mantic)).ForArea(1);
        var world = new CollisionWorld(level);

        Assert.Equal(6, world.KillBoxes.Count);

        var pit = world.KillBoxes[0];

        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);
        Put(ref state, 1, (pit.Left + pit.Right) / 2.0, (pit.Top + pit.Bottom) / 2.0);

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.True(events.Died[1], "standing in a pit did not kill");
        Assert.Equal(DamageSource.Pitfall, events.Deaths[1].Source);
    }
}
