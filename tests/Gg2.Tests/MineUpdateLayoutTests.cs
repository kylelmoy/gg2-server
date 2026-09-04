using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A demoman's mines on the wire, written and read back.
/// </summary>
/// <remarks>
/// <b>The bug this exists for was silent by construction.</b>
/// <see cref="WeaponState"/> carried <em>two</em> fields for
/// one quantity: <c>Lobbed</c>, which the Minegun maintained, and
/// <c>MinesLobbed</c>, which the serialiser read and which nothing ever wrote.
/// So a demoman holding eight mines put a <b>0</b> on the wire and no mine
/// blocks — self-consistent, so no client ever desynchronised and no framing
/// error ever fired. Every client simply drew no mines.
/// <para>
/// It could not be caught by a recording either: <c>gg2-parity record</c> joins
/// immediately and the weapon block is <c>FULL_UPDATE</c>-only, so no fixture
/// contains a demoman with mines out in a full update. These tests round-trip
/// the real writer through the real reader instead, which is the only way to see
/// bytes that were never written.
/// </para>
/// <para>
/// Transcribed from <c>Objects/Weapons/Minegun.events/User Event 12.xml</c> and
/// <c>Objects/Projectiles/Mine.events/User Event 12.xml</c>.
/// </para>
/// </remarks>
public sealed class MineUpdateLayoutTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Slot 0 is the reserved spectator, so the demoman is slot 1.</summary>
    private const byte Slot = 1;

    private static WorldShape Shape => GameModes.ShapeOf(
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    /// <summary>
    /// A demoman holding the given mines.
    /// </summary>
    /// <remarks>
    /// In slot <b>1</b>: a <see cref="Roster"/> reserves slot 0 for the server's
    /// own permanent spectator, exactly as the reference does, so a test that
    /// used slot 0 would be serialising the wrong player.
    /// </remarks>
    private static WorldState WorldWithMines(params (double X, double Y, double HSpeed, double VSpeed, bool Stuck)[] mines)
    {
        var state = default(WorldState);

        var character = CharacterState.Fresh(PlayerClass.Demoman, Team.Red);
        character.X = 100;
        character.Y = 200;
        state.Characters[Slot] = character;
        state.Weapons[Slot] = WeaponState.Spawn(PlayerClass.Demoman, 100, 200);
        state.Alive[Slot] = true;

        foreach (var (x, y, hspeed, vspeed, stuck) in mines)
        {
            var mine = new ProjectileState
            {
                Kind = ProjectileKind.Mine,
            Reflector = ProjectileState.NoOwner,
                X = x,
                Y = y,
                OwnerSlot = Slot,
                Team = Team.Red,
                Source = DamageSource.Minegun,
                Stickied = stuck,
            };

            mine.Motion.HSpeed = hspeed;
            mine.Motion.VSpeed = vspeed;

            state.SpawnProjectile(mine);
            state.Weapons[Slot].Lobbed++;
        }

        return state;
    }

    /// <summary>Writes a FULL_UPDATE for that world and reads it straight back.</summary>
    private static StateUpdate RoundTrip(in WorldState state)
    {
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var roster = new Roster("server");
        roster.Add(new RosterEntry { Name = "demo", Team = Team.Red, Class = PlayerClass.Demoman });
        var match = new MatchState(map, roster, new ServerOptions());

        var captured = state;

        bool Lookup(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            if (!captured.IsAlive(slot))
            {
                character = default;
                weapon = default;
                return false;
            }

            character = CharacterWire.ToBlock(captured.Characters[slot], captured.Weapons[slot]);
            weapon = CharacterWire.ToWeaponBlock(captured, slot);
            return true;
        }

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, Lookup).Write(writer, MessageType.FullUpdate);

        // StateUpdateWriter.Write emits the message-type byte and
        // StateUpdateReader.Read starts at the payload — the tier is already a
        // parameter — so the first byte has to be skipped or the FULL_UPDATE's
        // leading tdm-invulnerability ushort swallows it and every field after
        // is off by one.
        var reader = new NetReader(writer.WrittenSpan[1..]);
        PlayerClass[] classes = [PlayerClass.Scout, PlayerClass.Demoman];

        return StateUpdateReader.Read(ref reader, MessageType.FullUpdate, Shape, classes);
    }

    /// <summary>
    /// Three mines out means a count of three and three blocks that read back.
    /// </summary>
    /// <remarks>
    /// The direct control on the bug: before the fix this wrote a zero and the
    /// reader found no mines at all, while <c>Lobbed</c> said three.
    /// </remarks>
    [Fact]
    public void ADemomanWithMinesOutPutsThemOnTheWire()
    {
        var state = WorldWithMines(
            (300, 400, 2, -3, false),
            (310.4, 405.6, 0, 0, true),
            (320, 410, -1.5, 4, false));

        var update = RoundTrip(state);

        var mines = update.Players[Slot].Mines;
        Assert.Equal(3, mines.Length);

        // round(x * 5), GML's round — not the character's plain x*5.
        Assert.Equal(1500, mines[0].X);
        Assert.Equal(2000, mines[0].Y);

        // Quantised by 5, not the character's 8.5.
        Assert.Equal(10, mines[0].HSpeed);
        Assert.Equal(-15, mines[0].VSpeed);
        Assert.Equal(0, mines[0].Stickied);

        Assert.Equal(1, mines[1].Stickied);
    }

    /// <summary>
    /// A demoman with no mines writes a zero and nothing after it.
    /// </summary>
    /// <remarks>
    /// The other half of the invariant: the count is a promise, so an empty
    /// magazine must not leave a stray record behind. Read back through the same
    /// reader, which would throw or misalign if it did.
    /// </remarks>
    [Fact]
    public void ADemomanWithNoMinesWritesAZero()
    {
        var update = RoundTrip(WorldWithMines());

        Assert.Empty(update.Players[Slot].Mines);
    }

    /// <summary>
    /// The blocks come out in creation order, which is what the reference's
    /// <c>with(Mine)</c> walks.
    /// </summary>
    /// <remarks>
    /// A client reads the records positionally and matches them to the mines it
    /// is already drawing, so the order is part of the format rather than a
    /// detail. Pool order is <em>not</em> creation order once a slot has been
    /// recycled, which is what this asserts against.
    /// </remarks>
    [Fact]
    public void TheBlocksAreInCreationOrderAndNotPoolOrder()
    {
        var state = WorldWithMines(
            (100, 100, 0, 0, false),
            (200, 200, 0, 0, false),
            (300, 300, 0, 0, false));

        // Free the first mine and lob another, so the recycled pool slot holds
        // the newest mine while creation order puts it last.
        //
        // No manual decrement: WorldState's free path already lowers the
        // owner's `lobbed` for a mine, exactly as Mine.events/Destroy.xml does.
        // Decrementing again here is how this test first went red.
        var first = state.LiveProjectileHandle(0);
        state.FreeProjectile(first);

        var late = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            Reflector = ProjectileState.NoOwner,
            X = 400,
            Y = 400,
            OwnerSlot = Slot,
            Team = Team.Red,
            Source = DamageSource.Minegun,
        };
        state.SpawnProjectile(late);
        state.Weapons[Slot].Lobbed++;

        var mines = RoundTrip(state).Players[Slot].Mines;

        Assert.Equal([1000, 1500, 2000], mines.Select(m => (int)m.X).ToArray());
    }

    /// <summary>
    /// Only this demoman's mines are written, not the whole pool's.
    /// </summary>
    /// <remarks>
    /// <c>with(Mine) if(ownerPlayer == other.ownerPlayer)</c>. Getting this
    /// wrong would put another player's mines under this player's count, which
    /// the count would then contradict.
    /// </remarks>
    [Fact]
    public void AnotherPlayersMinesAreNotWrittenUnderThisOne()
    {
        var state = WorldWithMines((300, 400, 0, 0, false));

        var stranger = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            Reflector = ProjectileState.NoOwner,
            X = 900,
            Y = 900,
            OwnerSlot = 3,
            Team = Team.Blue,
            Source = DamageSource.Minegun,
        };
        state.SpawnProjectile(stranger);

        var mines = RoundTrip(state).Players[Slot].Mines;

        var mine = Assert.Single(mines);
        Assert.Equal(1500, mine.X);
    }

    /// <summary>
    /// A pool holding <em>more</em> mines than the count claims is refused too.
    /// </summary>
    /// <remarks>
    /// The direction an over-eager loop bound hides: filling exactly
    /// <c>lobbed</c> blocks and stopping agrees with itself no matter how many
    /// mines are really out, and writes a short list that the client then reads
    /// past. The count must be checked against the whole walk.
    /// </remarks>
    [Fact]
    public void APoolWithMoreMinesThanTheCountIsRefused()
    {
        var state = WorldWithMines(
            (300, 400, 0, 0, false),
            (310, 410, 0, 0, false),
            (320, 420, 0, 0, false));

        state.Weapons[Slot].Lobbed = 2;

        Assert.Throws<InvalidOperationException>(() => RoundTrip(state));
    }

    /// <summary>
    /// A count that disagrees with the pool is refused rather than written.
    /// </summary>
    /// <remarks>
    /// The count and the records have to come from one walk. If they ever part —
    /// a mine freed without decrementing, say — every client reads the rest of
    /// the update at the wrong offset, and the symptom appears hundreds of bytes
    /// later as a framing error against an unrelated message. Failing loudly
    /// here is the whole lesson of <see cref="SentryUpdateLayoutTests"/>.
    /// </remarks>
    [Fact]
    public void ACountThatDisagreesWithThePoolIsRefused()
    {
        var state = WorldWithMines((300, 400, 0, 0, false));
        state.Weapons[Slot].Lobbed = 4;

        Assert.Throws<InvalidOperationException>(() => RoundTrip(state));
    }
}
