using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Taunting and the humiliation round — the two features <c>CLAUDE.md</c> named
/// as still absent, landed 2026-08-28.
/// </summary>
/// <remarks>
/// <b>Both are observable simulation rather than presentation</b>, which is why
/// they are here at all. A taunt freezes a character where it stands and the
/// stock client re-derives that from the same key bit off the wire, so a server
/// that ignored the taunt key would walk a character the client has already
/// stopped. Humiliation costs 0.2 of control, which is the same size of effect.
/// <para>
/// Sources: <c>Character.events/Begin Step.xml:95-152</c> (the guarded block,
/// the right click and the cloak), <c>:176-181</c> (the control factor),
/// <c>End Step.xml:90-94</c> (the clock), <c>doEventArenaEndRound.gml:26-27</c>
/// and <c>doEventArenaRestart.gml:11</c>.
/// </para>
/// </remarks>
public sealed class TauntTests
{
    private const string Map = "ctf_truefort";

    private const double OpenX = 2600;

    private const double OpenY = 708;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(Gg2.Maps.LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static int Put(ref WorldState state, PlayerClass playerClass, Team team, int slot = 1)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = OpenX;
        character.Y = OpenY;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, OpenX, OpenY);
        state.Alive[slot] = true;

        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;

        return slot;
    }

    private static void Tick(ref WorldState state, ref TickEvents events, PlayerKeys keys, int slot = 1)
    {
        Span<PlayerInput> inputs = new PlayerInput[slot + 1];
        inputs[slot] = new PlayerInput(keys, 0, 0);
        MatchSimulation.Tick(World, ref state, inputs, ref events);
    }

    /// <summary>The taunt key starts a taunt, and the taunt stops the walk.</summary>
    [Fact]
    public void ATauntingCharacterCannotAccelerate()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        var slot = Put(ref state, PlayerClass.Scout, Team.Red);

        Tick(ref state, ref events, PlayerKeys.Taunt, slot);
        Assert.True(state.Characters[slot].Taunting);

        // Holding right now does nothing at all: the acceleration block re-reads
        // the flag, and friction is outside it, so a standing character stays at
        // zero rather than creeping.
        for (var i = 0; i < 5; i++)
            Tick(ref state, ref events, PlayerKeys.Right, slot);

        Assert.True(state.Characters[slot].Taunting);
        Assert.Equal(0, state.Characters[slot].HSpeed);
        Assert.Equal(OpenX, state.Characters[slot].X);
    }

    /// <summary>
    /// ⚠️ The duration is the class's taunt sprite frame count and nothing else,
    /// so it is different for every class.
    /// </summary>
    /// <remarks>
    /// <c>tauntindex += tauntspeed*0.1</c> with <c>tauntspeed = 3</c>, ending at
    /// <c>&gt;= sprite_get_number(tauntsprite)</c>. A scout's 9 frames is 30
    /// ticks and Quote's 17 is 57 — nearly a factor of two, which is why
    /// <see cref="ClassStats.TauntFrames"/> is per class rather than a shared
    /// constant.
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Scout, 9, 30)]
    [InlineData(PlayerClass.Soldier, 16, 54)]
    [InlineData(PlayerClass.Pyro, 10, 34)]
    [InlineData(PlayerClass.Heavy, 12, 40)]
    [InlineData(PlayerClass.Quote, 17, 57)]
    public void ATauntLastsTheFrameCountOfItsClassesSprite(PlayerClass playerClass, int frames, int ticks)
    {
        Assert.Equal(frames, ClassStats.For(playerClass).TauntFrames);

        var state = default(WorldState);
        var events = default(TickEvents);
        var slot = Put(ref state, playerClass, Team.Red);

        Tick(ref state, ref events, PlayerKeys.Taunt, slot);
        Assert.True(state.Characters[slot].Taunting);

        var lasted = 1;

        while (state.Characters[slot].Taunting && lasted < 200)
        {
            Tick(ref state, ref events, PlayerKeys.None, slot);
            lasted++;
        }

        Assert.Equal(ticks, lasted);
    }

    /// <summary>
    /// ⚠️ <b>The jump on the tick a taunt starts still fires.</b>
    /// </summary>
    /// <remarks>
    /// GML enters <c>if(!taunting and !omnomnomnom)</c> once and sets the flag
    /// inside it, so the jump a few lines below is tested against the value the
    /// block was entered with. Every guard <em>outside</em> that block — the
    /// right click, the cloak, the acceleration — re-reads it and refuses on the
    /// same tick. This is the one asymmetry in the feature and it is the
    /// reference's.
    /// </remarks>
    [Fact]
    public void TauntAndJumpOnTheSameTickStillJumps()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        var slot = Put(ref state, PlayerClass.Scout, Team.Red);

        // Settle onto the floor first so the jump has ground under it.
        for (var i = 0; i < 40; i++)
            Tick(ref state, ref events, PlayerKeys.None, slot);

        Assert.True(state.Characters[slot].OnGround);

        Tick(ref state, ref events, PlayerKeys.Taunt | PlayerKeys.Jump, slot);

        Assert.True(state.Characters[slot].Taunting);
        Assert.True(Gml.Less(state.Characters[slot].VSpeed, 0), "the taunt-tick jump did not launch");

        // And a jump on any LATER tick of the taunt is refused, which is the
        // control for the line above.
        var vspeed = state.Characters[slot].VSpeed;
        Tick(ref state, ref events, PlayerKeys.Jump, slot);
        Assert.True(Gml.Greater(state.Characters[slot].VSpeed, vspeed), "a mid-taunt jump launched");
    }

    /// <summary>A taunting character cannot shoot.</summary>
    [Fact]
    public void ATauntingCharacterCannotFire()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        var slot = Put(ref state, PlayerClass.Soldier, Team.Red);

        Tick(ref state, ref events, PlayerKeys.Taunt, slot);
        Assert.True(state.Characters[slot].Taunting);

        var ammo = state.Weapons[slot].AmmoCount;

        for (var i = 0; i < 10; i++)
            Tick(ref state, ref events, PlayerKeys.Attack, slot);

        Assert.Equal(ammo, state.Weapons[slot].AmmoCount);

        // The control: once the taunt is over the same key empties the magazine.
        while (state.Characters[slot].Taunting)
            Tick(ref state, ref events, PlayerKeys.None, slot);

        for (var i = 0; i < 10; i++)
            Tick(ref state, ref events, PlayerKeys.Attack, slot);

        Assert.True(state.Weapons[slot].AmmoCount < ammo, "the soldier never fired after the taunt");
    }

    /// <summary>
    /// ⚠️ A humiliated character loses 0.2 of control, and it does <b>not</b>
    /// stack with the intel's 0.1.
    /// </summary>
    /// <remarks>
    /// The reference is an if/else-if chain
    /// (<c>Character.events/Begin Step.xml:176-181</c>), so a humiliated flag
    /// carrier is 0.65 and not 0.55. Asserted through the accessor rather than
    /// through a trajectory, because the two arms differ by one branch and a
    /// walk would only show their sum.
    /// </remarks>
    [Fact]
    public void HumiliationCostsControlAndDoesNotStackWithTheIntel()
    {
        var plain = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        var humiliated = plain with { Humiliated = true };
        var carrying = plain with { CarryingIntel = true };
        var both = plain with { Humiliated = true, CarryingIntel = true };

        Assert.Equal(ClassStats.BaseControl, CharacterPhysics.ControlAndFriction(plain).Control);
        Assert.Equal(ClassStats.BaseControl - 0.1, CharacterPhysics.ControlAndFriction(carrying).Control);
        Assert.Equal(ClassStats.BaseControl - 0.2, CharacterPhysics.ControlAndFriction(humiliated).Control);
        Assert.Equal(ClassStats.BaseControl - 0.2, CharacterPhysics.ControlAndFriction(both).Control);
    }

    /// <summary>The losing side of an Arena round cannot shoot, taunt or eat.</summary>
    [Fact]
    public void AHumiliatedCharacterCannotFireOrTaunt()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        var slot = Put(ref state, PlayerClass.Soldier, Team.Red);
        state.Players[slot].Humiliated = true;

        var ammo = state.Weapons[slot].AmmoCount;

        for (var i = 0; i < 10; i++)
            Tick(ref state, ref events, PlayerKeys.Attack | PlayerKeys.Taunt, slot);

        Assert.Equal(ammo, state.Weapons[slot].AmmoCount);
        Assert.False(state.Characters[slot].Taunting);

        // ...and the mirror is what the physics read, so it is set.
        Assert.True(state.Characters[slot].Humiliated);
    }

    /// <summary>A taunting heavy, and a humiliated one, cannot eat.</summary>
    [Fact]
    public void ATauntingOrHumiliatedHeavyCannotEat()
    {
        var state = default(WorldState);
        var slot = Put(ref state, PlayerClass.Heavy, Team.Red);
        state.Characters[slot].CanEat = true;

        state.Characters[slot].Taunting = true;
        Assert.False(PlayerCommands.EatSandvich(ref state, slot));

        state.Characters[slot].Taunting = false;
        state.Players[slot].Humiliated = true;
        Assert.False(PlayerCommands.EatSandvich(ref state, slot));

        // The control: with neither, the same heavy eats.
        state.Players[slot].Humiliated = false;
        Assert.True(PlayerCommands.EatSandvich(ref state, slot));
    }
}
