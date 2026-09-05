using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The domination kill table: who has killed whom, and how many times since
/// last dying to them.
/// </summary>
/// <remarks>
/// <c>Scripts/Dominations/</c>,
/// <c>Scripts/Events/doEventPlayerDeath.gml:81-96</c>,
/// <c>Objects/InGameElements/Player.events/{Destroy, User Event 12}.xml</c>.
/// <para>
/// <b>No fixture reaches it.</b> The table rides in a <c>FULL_UPDATE</c>, and a
/// recording holds exactly one — sent on the join path before anybody has killed
/// anybody. A port that wrote zeroes would replay every fixture identically.
/// </para>
/// </remarks>
public sealed class DominationTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static (WorldState State, CollisionWorld World) Started()
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 5;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Intel.Place(ref state, level);
        Rounds.Start(ref state);

        return (state, world);
    }

    private static void Put(ref WorldState state, int slot, Team team, bool alive = true)
    {
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = PlayerClass.Soldier;

        if (!alive)
            return;

        var character = CharacterState.Fresh(PlayerClass.Soldier, team);
        character.X = 2000;
        character.Y = 700;
        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Soldier, character.X, character.Y);
        state.Alive[slot] = true;
    }

    /// <summary>Kills <paramref name="victim"/> at the hands of the given slots.</summary>
    private static void Kill(ref WorldState state, int victim, int killer, int assistant = -1)
    {
        if (!state.IsAlive(victim))
            Put(ref state, victim, state.Players[victim].Team);

        state.Characters[victim].LastDamageDealer = (byte)killer;

        state.Characters[victim].SecondToLastDamageDealer =
            assistant < 0 ? CharacterState.Nobody : (byte)assistant;

        state.Characters[victim].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, victim, ref events);
    }

    // --- counting -------------------------------------------------------------

    /// <summary>A kill is one on the killer's row, in the victim's column.</summary>
    [Fact]
    public void AKillCountsOnTheKillersRow()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        Kill(ref state, victim: 2, killer: 1);

        Assert.Equal(1, Dominations.Get(state, killer: 1, victim: 2));
        Assert.Equal(0, Dominations.Get(state, killer: 2, victim: 1));
    }

    /// <summary>And they accumulate.</summary>
    [Fact]
    public void RepeatedKillsAccumulate()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        for (var i = 0; i < 5; i++)
            Kill(ref state, victim: 2, killer: 1);

        Assert.Equal(5, Dominations.Get(state, killer: 1, victim: 2));
    }

    /// <summary>
    /// <b>One kill back wipes the count entirely.</b>
    /// <c>domination_kills_delete</c>, not a decrement — so the counter is
    /// "kills since they last killed me", and a single revenge ends a
    /// domination however long it ran.
    /// </summary>
    [Fact]
    public void OneKillBackWipesTheWholeCount()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        for (var i = 0; i < 9; i++)
            Kill(ref state, victim: 2, killer: 1);

        Assert.Equal(9, Dominations.Get(state, killer: 1, victim: 2));

        Kill(ref state, victim: 1, killer: 2);

        Assert.Equal(0, Dominations.Get(state, killer: 1, victim: 2));
        Assert.Equal(1, Dominations.Get(state, killer: 2, victim: 1));
    }

    /// <summary>
    /// <b>An assistant is dominating the victim just as much as the
    /// shooter</b> — both go into <c>killersForDomination</c>.
    /// </summary>
    [Fact]
    public void AnAssistantCountsAsAKillerForDominations()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);
        Put(ref state, 3, Team.Red);

        Kill(ref state, victim: 2, killer: 1, assistant: 3);

        Assert.Equal(1, Dominations.Get(state, killer: 1, victim: 2));
        Assert.Equal(1, Dominations.Get(state, killer: 3, victim: 2));
    }

    /// <summary>
    /// And the assistant's own count is wiped by a kill in return, exactly like
    /// the shooter's.
    /// </summary>
    [Fact]
    public void AnAssistantsCountIsWipedByAKillBack()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);
        Put(ref state, 3, Team.Red);

        Kill(ref state, victim: 2, killer: 1, assistant: 3);
        Kill(ref state, victim: 3, killer: 2);

        Assert.Equal(0, Dominations.Get(state, killer: 3, victim: 2));
        Assert.Equal(1, Dominations.Get(state, killer: 2, victim: 3));

        // The shooter's is untouched: they were not the one killed.
        Assert.Equal(1, Dominations.Get(state, killer: 1, victim: 2));
    }

    /// <summary>
    /// A suicide records nothing — the killer only joins
    /// <c>killersForDomination</c> inside <c>if (killer != victim)</c>.
    /// </summary>
    [Fact]
    public void ASuicideRecordsNoDomination()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);

        Kill(ref state, victim: 1, killer: 1);

        Assert.Equal(0, Dominations.Get(state, killer: 1, victim: 1));
    }

    /// <summary>A death with no killer records nothing either.</summary>
    [Fact]
    public void ADeathWithNoKillerRecordsNothing()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        Kill(ref state, victim: 1, killer: CharacterState.Nobody);

        for (var i = 0; i < WorldState.MaxSlots; i++)
            Assert.Equal(0, Dominations.Get(state, i, 1));
    }

    /// <summary>
    /// The count saturates rather than wrapping, so a client can never be told a
    /// killer is on 0 after 256 kills.
    /// </summary>
    [Fact]
    public void TheCountSaturatesRatherThanWrapping()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red);
        Put(ref state, 2, Team.Blue);

        state.DominationKills[1][2] = byte.MaxValue;
        Dominations.RecordKill(ref state, victim: 2, killer: 1);

        Assert.Equal(byte.MaxValue, Dominations.Get(state, 1, 2));
    }

    // --- clearing -------------------------------------------------------------

    /// <summary>
    /// A <b>team change</b> clears both directions — the dominations held and
    /// the ones held over you.
    /// </summary>
    [Fact]
    public void ATeamChangeClearsBothDirections()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red, alive: false);
        Put(ref state, 2, Team.Blue, alive: false);
        Put(ref state, 3, Team.Blue, alive: false);

        state.DominationKills[1][2] = 7;
        state.DominationKills[3][1] = 4;
        state.DominationKills[3][2] = 2;

        var events = default(TickEvents);
        PlayerCommands.ChangeTeamWithBalance(ref state, 1, Team.Blue, ref events, out _);

        Assert.Equal(0, Dominations.Get(state, 1, 2));
        Assert.Equal(0, Dominations.Get(state, 3, 1));

        // Everybody else's dealings with each other are untouched.
        Assert.Equal(2, Dominations.Get(state, 3, 2));
    }

    /// <summary>
    /// A <b>class</b> change does not — only a team change cancels a
    /// domination.
    /// </summary>
    [Fact]
    public void AClassChangeDoesNotClearThem()
    {
        var (state, _) = Started();
        Put(ref state, 1, Team.Red, alive: false);
        Put(ref state, 2, Team.Blue, alive: false);

        state.DominationKills[1][2] = 7;

        PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Pyro);

        Assert.Equal(7, Dominations.Get(state, 1, 2));
    }

    // --- the roster shift -----------------------------------------------------

    /// <summary>
    /// <b>The matrix is indexed by roster slot on both axes, so a departure
    /// has to shift it twice.</b> It is the only two-dimensional thing in the
    /// state, and a one-dimensional renumbering would leave every column one to
    /// the right of where it belongs.
    /// </summary>
    [Fact]
    public void ADepartureShiftsBothAxes()
    {
        var (state, _) = Started();

        for (var slot = 0; slot < 5; slot++)
            Put(ref state, slot, Team.Red, alive: false);

        // A distinctive pattern: slot 3 has killed slot 4 twice, and slot 4 has
        // killed slot 3 five times.
        state.DominationKills[3][4] = 2;
        state.DominationKills[4][3] = 5;

        // And something involving the departing slot, which must not survive.
        state.DominationKills[1][4] = 9;
        state.DominationKills[4][1] = 8;

        state.RemoveSlot(1);

        // 3 and 4 became 2 and 3.
        Assert.Equal(2, Dominations.Get(state, killer: 2, victim: 3));
        Assert.Equal(5, Dominations.Get(state, killer: 3, victim: 2));

        // Nothing anywhere still names the departed player's dealings.
        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            for (var j = 0; j < WorldState.MaxSlots; j++)
            {
                if ((i, j) is (2, 3) or (3, 2))
                    continue;

                Assert.Equal(0, Dominations.Get(state, i, j));
            }
        }
    }

    /// <summary>
    /// The whole thing through a real departure on a live server: the roster
    /// compacts and the table goes with it.
    /// </summary>
    [Fact]
    public void ALiveDepartureLeavesNoStaleCounts()
    {
        var (state, _) = Started();

        for (var slot = 0; slot < 4; slot++)
            Put(ref state, slot, slot % 2 == 0 ? Team.Red : Team.Blue, alive: false);

        state.DominationKills[2][3] = 4;

        state.RemoveSlot(0);

        Assert.Equal(4, Dominations.Get(state, killer: 1, victim: 2));
        Assert.Equal(0, Dominations.Get(state, killer: 2, victim: 3));
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// A player's row is one byte per <b>other</b> roster entry, in roster
    /// order, with <em>self skipped</em> — so it is one byte shorter than the
    /// roster and carries no count of its own.
    /// </summary>
    /// <remarks>
    /// Each cell is given a different value so a writer that transposed the
    /// matrix, or wrote self as a zero, fails rather than passing by symmetry.
    /// </remarks>
    [Fact]
    public void EachPlayersRowSkipsThemselvesAndRoundTrips()
    {
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var roster = new Roster("server");
        roster.Add(new RosterEntry { Name = "a", Team = Team.Red, Class = PlayerClass.Soldier });
        roster.Add(new RosterEntry { Name = "b", Team = Team.Blue, Class = PlayerClass.Soldier });
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));

        var state = default(WorldState);
        for (var slot = 0; slot < 3; slot++)
            state.AddPlayer(slot);

        // Row 0: [-, 1, 2]. Row 1: [3, -, 4]. Row 2: [5, 6, -].
        state.DominationKills[0][1] = 1;
        state.DominationKills[0][2] = 2;
        state.DominationKills[1][0] = 3;
        state.DominationKills[1][2] = 4;
        state.DominationKills[2][0] = 5;
        state.DominationKills[2][1] = 6;

        var captured = state;

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        byte Dominating(int killer, int victim) => Dominations.Get(captured, killer, victim);

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, NoCharacters, null, null, null, Dominating)
            .Write(writer, MessageType.FullUpdate);

        var reader = new NetReader(writer.WrittenSpan[1..]);
        PlayerClass[] classes = [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Soldier];

        var shape = GameModes.ShapeOf(map);
        var update = StateUpdateReader.Read(ref reader, MessageType.FullUpdate, shape, classes);

        Assert.Equal(new byte[] { 1, 2 }, update.Players[0].DominationKills);
        Assert.Equal(new byte[] { 3, 4 }, update.Players[1].DominationKills);
        Assert.Equal(new byte[] { 5, 6 }, update.Players[2].DominationKills);
    }

    /// <summary>
    /// A server with no matrix at all writes zeroes and still frames — what
    /// every layout test built before this relies on.
    /// </summary>
    [Fact]
    public void NoMatrixWritesZeroesAndStillFrames()
    {
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var roster = new Roster("server");
        roster.Add(new RosterEntry { Name = "a", Team = Team.Red, Class = PlayerClass.Soldier });
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, NoCharacters).Write(writer, MessageType.FullUpdate);

        var reader = new NetReader(writer.WrittenSpan[1..]);
        PlayerClass[] classes = [PlayerClass.Scout, PlayerClass.Soldier];
        var update = StateUpdateReader.Read(
            ref reader, MessageType.FullUpdate, GameModes.ShapeOf(map), classes);

        Assert.Equal(new byte[] { 0 }, update.Players[0].DominationKills);
    }
}
