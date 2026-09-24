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
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        return ScenarioWorld.Load(map, area, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);
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

}
