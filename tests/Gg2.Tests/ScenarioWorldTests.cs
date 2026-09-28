using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The gamemode-less scenario world and the runner over it — docs/BOTAI.md item
/// 11.
/// </summary>
/// <remarks>
/// "Spawn two characters of given classes at given positions on a given map,
/// with no gamemode running, and step them." Duel training and rollout search
/// both want a fight rather than a match, and that is where the sample budget
/// actually goes.
/// <para>
/// The sources here are deliberately stupid — hold a key, hold the trigger —
/// because what is under test is the harness, not a bot. A scenario suite whose
/// harness is only exercised through the bot it is meant to measure cannot tell
/// a broken bot from a broken runner.
/// </para>
/// </remarks>
public sealed class ScenarioWorldTests
{
    /// <summary>Presses one thing for ever.</summary>
    private sealed class Holds(PlayerKeys keys, double aimDegrees = 0) : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) =>
            PlayerInput.FromAim(keys, aimDegrees, 200);
    }

    private static ScenarioWorld Load(string map, int area = 1)
    {
        ReferenceTree.RequireNavCache();

        return ScenarioWorld.Load(map, area, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
    }

    /// <summary>
    /// A character is placed exactly where it was asked for, with its weapon.
    /// </summary>
    /// <remarks>
    /// <b>The weapon assertion is the one that bites.</b> A weapon starts on
    /// its owner's unrounded coordinates and only reaches <c>round(x)</c> in End
    /// Step, and a shot leaves from the <em>weapon</em> — so a character
    /// teleported without its weapon fires its first shot from wherever it
    /// spawned, which on a big map is across the room. It is invisible until
    /// something measures damage.
    /// </remarks>
    [Fact]
    public void PlacePutsACharacterAndItsWeaponWhereItWasAsked()
    {
        var world = Load("koth_valley");
        var slot = world.Place(PlayerClass.Sniper, Team.Blue, 1234.5, 678.25);

        Assert.True(world.State.Alive[slot]);
        Assert.Equal(1234.5, world.State.Characters[slot].X);
        Assert.Equal(678.25, world.State.Characters[slot].Y);
        Assert.Equal(PlayerClass.Sniper, world.State.Characters[slot].Class);
        Assert.Equal(Team.Blue, world.State.Players[slot].Team);
        Assert.Equal(ClassStats.For(PlayerClass.Sniper).MaxHp, world.State.Characters[slot].Hp);

        Assert.Equal(1234.5, world.State.Weapons[slot].X);
        Assert.Equal(678.25, world.State.Weapons[slot].Y);

        // Asserted HERE, at placement, and not after a run: spawn protection
        // decays to nothing within a few seconds, so a run of any length hides
        // a world that grants it. WorldState.Spawn hands a TDM character
        // TdmInvulnerabilityTicks of it, and a non-zero value would make every
        // combat scenario's damage figure wrong for its opening seconds and read
        // as a bot that could not shoot.
        Assert.Equal(0, world.State.Characters[slot].DeathmatchInvulnerable);

        // Slot 0 is the server's reserved player and is never a scenario
        // character — the invariant that makes CharacterState.Nobody 255.
        Assert.NotEqual(0, slot);
        Assert.False(world.State.Alive[0]);
    }

    /// <summary>
    /// No gamemode runs: no clock, no winner, no respawn, and no objective.
    /// </summary>
    /// <remarks>
    /// The whole claim of item 11, and every clause of it is a "0 means off"
    /// escape that is easy to leave out. Two thousand ticks is well past the
    /// shortest round the reference permits, so a world that had a limit set
    /// would have ended one by here — and a round ending opens every gate, which
    /// would quietly change what a routing scenario measures.
    /// </remarks>
    [Fact]
    public void NoGamemodeRunsAndNothingEndsTheRound()
    {
        var world = Load("koth_valley");
        world.Place(PlayerClass.Soldier, Team.Red, 1880, 859);
        world.Place(PlayerClass.Heavy, Team.Blue, 2080, 859);

        world.Step(2000);

        Assert.False(world.State.RoundDecided);
        Assert.False(world.State.MapChanging);
        Assert.Equal(0, world.State.RoundTimer);
        Assert.Equal(0, world.State.RedCaps);
        Assert.Equal(0, world.State.BlueCaps);
        Assert.Equal(0, world.State.ControlPointCount);
        Assert.False(world.State.RedGenerator.Exists);
        Assert.False(world.State.BlueGenerator.Exists);

    }

    /// <summary>
    /// A death in a scenario is final.
    /// </summary>
    /// <remarks>
    /// Two things have to be off for this, and each alone is enough to break
    /// it: <see cref="WorldState.RespawnTime"/> at zero, and no
    /// <c>SpawnPoints</c> handed to the tick. Without them a killed dummy comes
    /// back and a damage figure silently becomes a measurement of two lives.
    /// </remarks>
    [Fact]
    public void ADeathIsFinal()
    {
        var world = Load("koth_valley");
        var slot = world.Place(PlayerClass.Scout, Team.Blue, 1880, 859);

        world.Step();
        world.State.Characters[slot].Hp = 0;
        world.Step(); // the End Step that notices

        Assert.False(world.State.Alive[slot]);

        // Checked on the tick of the death, because that is the only tick on
        // which RespawnTime is readable: Deaths.Kill arms the timer from it, and
        // a timer armed at 150 is back at zero 150 ticks later whether or not
        // anybody respawned. A test that looked afterwards would pass with the
        // respawn left switched on.
        Assert.True(
            world.State.Players[slot].RespawnTimer <= 0,
            $"the death armed a respawn timer of {world.State.Players[slot].RespawnTimer}");

        world.Step(300); // twice the 150 ticks every fixture measures
        Assert.False(world.State.Alive[slot]);
    }

    /// <summary>
    /// A driven slot presses what its source says; an undriven one presses
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The control for every combat scenario: a "dummy" is a character with no
    /// source, which is what makes the damage measured unambiguously the
    /// subject's doing. In the reference a dummy is an acquisition delay and
    /// a locked goal — ordinary knobs, so a training dummy needs no test-only
    /// code in the game. "No source" is the same thing here, and needs none
    /// either.
    /// </remarks>
    [Fact]
    public void OnlyADrivenSlotMoves()
    {
        var world = Load("ctf_truefort");

        var driven = world.Place(PlayerClass.Scout, Team.Red, 1607, 456);
        var dummy = world.Place(PlayerClass.Scout, Team.Red, 1500, 456);

        world.Drive(driven, new Holds(PlayerKeys.Left, 180));

        var drivenStart = world.State.Characters[driven].X;
        var dummyStart = world.State.Characters[dummy].X;

        world.Step(60);

        Assert.True(
            world.State.Characters[driven].X < drivenStart - 50,
            $"the driven scout barely moved: {drivenStart} to {world.State.Characters[driven].X}");

        Assert.Equal(dummyStart, world.State.Characters[dummy].X);
    }

    /// <summary>
    /// The same world stepped the same way twice gives bit-identical results.
    /// </summary>
    /// <remarks>
    /// docs/BOTAI.md's determinism requirement, from the scenario side: a
    /// self-play evaluator that cannot reproduce a run cannot attribute a
    /// difference to a change. <see cref="WorldState.Random"/> starts at seed
    /// 0 in a default state, which is exactly what makes this hold — and is why
    /// <c>randomize()</c> is deliberately not transcribed anywhere in this port.
    /// </remarks>
    [Fact]
    public void TwoIdenticalRunsAgreeExactly()
    {
        static (double X, double Y, double Hp) RunOnce()
        {
            var world = Load("koth_valley");
            var subject = world.Place(PlayerClass.Soldier, Team.Red, 1880, 859);
            var target = world.Place(PlayerClass.Heavy, Team.Blue, 2080, 859);

            world.Drive(subject, new Holds(PlayerKeys.Attack));
            world.Step(200);

            return (world.State.Characters[subject].X,
                world.State.Characters[subject].Y,
                world.State.Alive[target] ? world.State.Characters[target].Hp : 0);
        }

        Assert.Equal(RunOnce(), RunOnce());
    }

    /// <summary>
    /// The runner measures damage, and a dummy with nothing driving it deals
    /// none back.
    /// </summary>
    /// <remarks>
    /// This is <c>soldier-holds-and-fires</c> run with the stupidest possible
    /// source — hold the trigger, aim right — so what it gates is the harness:
    /// the placement, the weapon following its owner, the tick loop, and the
    /// damage arithmetic. The real scenario asserts <c>acquired</c> as well,
    /// which needs a bot that picks targets; that half waits for item 4.
    /// <para>
    /// The control is the null source: with nobody pulling the trigger the same
    /// scenario must measure exactly zero, or the number is coming from
    /// somewhere other than the subject.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRunnerMeasuresDamageAndZeroWithoutASource()
    {
        var scenario = Scenarios.ByName("soldier-holds-and-fires");
        Assert.NotNull(scenario);

        var firing = ScenarioRunner.Run(
            scenario, Load(scenario.Map), new Holds(PlayerKeys.Attack));

        Assert.True(firing.Damage > 0, "a soldier holding the trigger at a dummy 200px away dealt nothing");
        Assert.Equal(scenario.Hold, firing.Ticks);

        var idle = ScenarioRunner.Run(scenario, Load(scenario.Map), subject: null);

        Assert.Equal(0, idle.Damage);
        Assert.True(idle.Survived);
    }

    /// <summary>
    /// A kill counts for the victim's whole hit-point bar.
    /// </summary>
    /// <remarks>
    /// The arithmetic that makes a damage figure comparable across runs: without
    /// it a scenario that <em>killed</em> its dummy measures less damage than
    /// one that left it on a sliver, which inverts the thing being measured.
    /// <para>
    /// A scout rather than the heavy the real scenario uses, because 125 hit
    /// points is inside what a soldier holding the trigger removes in the four
    /// hundred ticks the scenario allows and 300 is not.
    /// </para>
    /// </remarks>
    [Fact]
    public void AKillCountsForTheWholeBar()
    {
        var duel = new Scenario(
            name: "synthetic-kill-the-dummy",
            map: "koth_valley",
            about:
                "Not a behaviour scenario: a held trigger against a dummy soft enough to die, "
                + "so that the damage arithmetic has a gate covering a kill and not only a "
                + "wound.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(1880, 859),
            hold: 400,
            enemies: [new ScenarioEnemy(PlayerClass.Scout, new ScenarioPoint(2080, 859))]);

        var world = Load(duel.Map);
        var result = ScenarioRunner.Run(duel, world, new Holds(PlayerKeys.Attack));

        Assert.False(world.State.Alive[2], "the dummy survived, so this gates nothing");
        Assert.Equal(ClassStats.For(PlayerClass.Scout).MaxHp, result.Damage);
    }

    /// <summary>
    /// A travel run reports arrival, and stops on the tick it arrives.
    /// </summary>
    /// <remarks>
    /// Arrival is what a budget is <em>for</em>: "a leg that takes longer is a
    /// fail even if it eventually arrives — 'gets there in the end' is how a bot
    /// that re-plans in a loop looks from the outside." So the runner has to
    /// stop early and report the tick, not run the budget out and check the
    /// position afterwards.
    /// <para>
    /// The scenario here is synthetic and the source is a held key, because the
    /// fourteen real ones need a bot. LEFT: red spawns on the <em>right</em>
    /// of ctf_truefort and a scout holding RIGHT walks into the back of its own
    /// base.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATravelRunStopsOnArrivalAndReportsIt()
    {
        var walk = new Scenario(
            name: "synthetic-walk-west",
            map: "ctf_truefort",
            about:
                "Not a behaviour scenario: a held key over flat ground, so that the runner's "
                + "arrival detection and its early stop have a gate that does not need a bot.",
            playerClass: PlayerClass.Scout,
            team: Team.Red,
            from: new ScenarioPoint(1607, 456),
            to: new ScenarioPoint(1450, 456),
            budget: 400);

        var arrived = ScenarioRunner.Run(walk, Load(walk.Map), new Holds(PlayerKeys.Left, 180));

        Assert.True(arrived.Arrived, $"did not arrive; finished {arrived.GoalDistance:0.#}px away");
        Assert.True(arrived.Ticks < walk.Budget, $"ran the whole budget ({arrived.Ticks} ticks)");
        Assert.True(arrived.GoalDistance <= ScenarioRunner.ArrivalRadius);
        Assert.True(arrived.Moved > 100, $"only moved {arrived.Moved:0.#}px");

        // The control: standing still never arrives and burns the whole budget.
        var still = ScenarioRunner.Run(walk, Load(walk.Map), subject: null);

        Assert.False(still.Arrived);
        Assert.Equal(walk.Budget, still.Ticks);
        Assert.True(still.GoalDistance > ScenarioRunner.ArrivalRadius);
    }

    /// <summary>
    /// A scenario's coordinates are snapped, and the snapped position is what is
    /// placed.
    /// </summary>
    /// <remarks>
    /// The reference's runner does the same and reports the snapped position,
    /// because a raw coordinate usually does not resolve to a node.
    /// <c>valley-shaft-crate-to-point</c>'s comment records the trap directly:
    /// its <c>from</c> is deliberately <em>above</em> the crate, because the
    /// search goes downward and a point level with the surface you mean resolves
    /// to whatever is under it instead.
    /// <para>
    /// The assertion is on a scenario whose snapped x <em>differs</em> from
    /// its raw one, which most do not: an anchor column is six pixels wide and
    /// <c>valley-shaft-crate-to-point</c>'s 2250 lands exactly on a column
    /// centre, so placing it raw and placing it snapped agree by coincidence and
    /// gate nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AScenarioIsPlacedAtItsSnappedPosition()
    {
        var scenario = Scenarios.ByName("soldier-holds-and-fires");
        Assert.NotNull(scenario);

        var world = Load(scenario.Map);
        var snap = world.Snap(scenario.From.X, scenario.From.Y);

        Assert.True(snap.Node >= 0);

        // x only. The scenario's y is already a node's exact stand position —
        // these coordinates were chosen by asking the reference's graph — so the
        // snap leaves it alone and asserting it moved would fail for the right
        // reason at the wrong place. The x is two pixels off a column centre,
        // which is what makes it a gate.
        Assert.NotEqual(scenario.From.X, snap.GoalX);

        // Nothing driving it and a dummy for company, so it stands where it was
        // put and the x it ends on is the x it was placed at.
        ScenarioRunner.Run(scenario, world, subject: null);

        // Slot 1 is the subject: the runner places it first.
        Assert.Equal(snap.GoalX, world.State.Characters[1].X);
    }
}
