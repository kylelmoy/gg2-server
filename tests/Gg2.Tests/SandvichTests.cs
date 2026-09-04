using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The heavy's sandvich — <c>serverEatSandvich.gml</c> and the End Step body
/// that does the actual healing.
/// </summary>
/// <remarks>
/// <b>The heal is not in the command.</b> <c>serverEatSandvich</c> only sets
/// the animation going; every hit point arrives from
/// <c>Character.events/End Step.xml:83-96</c>, a tick at a time, and stops if
/// the character dies part way through. Healing on the command would give a
/// heavy 200 hp the instant they pressed the key.
/// </remarks>
public sealed class SandvichTests
{
    // gg_debug is the empty-room map the physics tests use; the sandvich cares
    // about hp and timers rather than terrain, but End Step clamps into the map
    // and needs real bounds to clamp against.
    private const string Map = "gg_debug";

    private static CollisionWorld? _shared;

    private static CollisionWorld World1 => _shared ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static WorldState World(
        PlayerClass playerClass = PlayerClass.Heavy,
        double hp = 200,
        int slot = 1)
    {
        var state = default(WorldState);
        var character = CharacterState.Fresh(playerClass, Team.Red);

        character.X = 100;
        character.Y = 100;
        character.Hp = hp;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, 100, 100);
        state.Alive[slot] = true;

        state.AddPlayer(slot);
        state.Players[slot].Team = Team.Red;
        state.Players[slot].Class = playerClass;

        return state;
    }

    /// <summary>
    /// A fresh character can eat, and every class carries the flag because every
    /// class runs the same Create event.
    /// </summary>
    [Fact]
    public void EveryClassSpawnsAbleToEatAndOnlyAHeavyCan()
    {
        foreach (var playerClass in Enum.GetValues<PlayerClass>())
        {
            var fresh = CharacterState.Fresh(playerClass, Team.Red);

            Assert.True(fresh.CanEat);
            Assert.False(fresh.Omnomnomnom);
            Assert.Equal(Timers.Inactive, fresh.EatTimer);
        }

        var state = World(PlayerClass.Scout);
        Assert.False(PlayerCommands.EatSandvich(ref state, 1));
        Assert.False(state.Characters[1].Omnomnomnom);
    }

    /// <summary>The reference's preconditions, each refused in silence.</summary>
    /// <remarks>
    /// <b>A refusal is <c>false</c>, never an exception.</b>
    /// <c>botServerActions.gml</c> says each server action "re-checks its own
    /// preconditions", and the socket path and the bot path share this one
    /// method precisely so a bot cannot do something a client cannot.
    /// </remarks>
    [Fact]
    public void ADeadHeavyAnAlreadyEatingOneAndOneOnCooldownAreAllRefused()
    {
        var dead = World();
        dead.Alive[1] = false;
        Assert.False(PlayerCommands.EatSandvich(ref dead, 1));

        var eating = World();
        Assert.True(PlayerCommands.EatSandvich(ref eating, 1));

        // Already eating is a refusal, not a restart — without it a heavy
        // holding the key would hold the index at zero and eat for ever.
        eating.Characters[1].OmnomnomnomIndex = 20;
        Assert.False(PlayerCommands.EatSandvich(ref eating, 1));
        Assert.Equal(20, eating.Characters[1].OmnomnomnomIndex);

        var cooling = World();
        cooling.Characters[1].CanEat = false;
        Assert.False(PlayerCommands.EatSandvich(ref cooling, 1));
    }

    /// <summary>
    /// <b>128 ticks of eating</b>, and the animation counter is what ends it.
    /// </summary>
    /// <remarks>
    /// <c>omnomnomnomindex</c> starts at 0, gains <b>0.25</b> a tick and stops at
    /// <b>32</b>. Nothing else measures the duration — there is no timer.
    /// </remarks>
    [Fact]
    public void EatingLastsExactlyOneHundredAndTwentyEightTicks()
    {
        var state = World(hp: 1);
        Assert.True(PlayerCommands.EatSandvich(ref state, 1));

        var ticks = 0;

        while (state.Characters[1].Omnomnomnom)
        {
            CharacterPhysics.EndStep(World1, ref state.Characters[1]);
            ticks++;

            Assert.True(ticks <= 200, "the eat never ended");
        }

        Assert.Equal(128, ticks);
    }

    /// <summary>
    /// <b>The heal overshoots <c>maxHp</c> by 1.6 and stays there for a
    /// tick</b>, because the clamp is earlier in the same event.
    /// </summary>
    /// <remarks>
    /// <c>if (hp &lt;= maxHp) hp += 1.6;</c> — at exactly full health the test
    /// still passes and the heavy ends the tick at <b>201.6</b>. The clamp at
    /// <c>End Step.xml:37</c> runs <em>above</em> this block, so it cannot catch
    /// the overshoot on the tick that made it.
    /// <para>
    /// <b>And on the next tick the clamp is immediately undone</b>, because
    /// the character is still eating: clamp to 200, then <c>200 &lt;= 200</c>
    /// adds 1.6 again. <b>A heavy who eats at full health sits at 201.6 for the
    /// whole 128 ticks</b>, and every state update in that window carries it.
    /// Only when the eat ends does the clamp finally stick.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFullHealthHeavySitsAboveMaxHpForTheWholeEat()
    {
        var state = World(hp: 200);
        Assert.True(PlayerCommands.EatSandvich(ref state, 1));

        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        Assert.Equal(201.6, state.Characters[1].Hp, 10);

        // Clamped at the top of End Step, then pushed straight back over it.
        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        Assert.Equal(201.6, state.Characters[1].Hp, 10);

        while (state.Characters[1].Omnomnomnom)
            CharacterPhysics.EndStep(World1, ref state.Characters[1]);

        Assert.Equal(201.6, state.Characters[1].Hp, 10);

        // The eat is over, so this one sticks.
        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        Assert.Equal(200, state.Characters[1].Hp);
    }

    /// <summary>
    /// 1.6 a tick over 128 ticks is 204.8 — more than a heavy has, so an
    /// uninterrupted sandvich is a full heal from any survivable damage.
    /// </summary>
    [Fact]
    public void AnUninterruptedSandvichFullyHealsAHeavyOnOneHitPoint()
    {
        var state = World(hp: 1);
        Assert.True(PlayerCommands.EatSandvich(ref state, 1));

        for (var i = 0; i < 128; i++)
            CharacterPhysics.EndStep(World1, ref state.Characters[1]);

        Assert.False(state.Characters[1].Omnomnomnom);
        Assert.True(
            state.Characters[1].Hp >= 200,
            $"ended the eat on {state.Characters[1].Hp} hp");
    }

    /// <summary>
    /// <b>The cooldown is re-armed on every tick of the eat while hurt, not
    /// once at the start</b> — and it is <b>1350 ticks</b>, 45 seconds.
    /// </summary>
    /// <remarks>
    /// The reference's own comment explains why: "This should prevent the 'ate
    /// and got hit but didn't refresh cooldown' bug ... cooldown is now reset
    /// continually until fully healed or finished eating." So a heavy who takes a
    /// hit late in the eat pays the full 45 seconds from <em>there</em>, and one
    /// who reaches full health early keeps whatever was left when the re-arming
    /// stopped.
    /// <para>
    /// The reference's other comment, on the client, says "10 second cooldown"
    /// beside the same variable — it is wrong, and copying the comment instead of
    /// the value would have imported the bug.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCooldownIsFortyFiveSecondsAndIsRearmedWhileHurt()
    {
        var state = World(hp: 100);
        Assert.True(PlayerCommands.EatSandvich(ref state, 1));

        CharacterPhysics.EndStep(World1, ref state.Characters[1]);

        Assert.False(state.Characters[1].CanEat);
        Assert.Equal(1350, state.Characters[1].EatTimer);

        // Let the alarm run down a little, then take another End Step while
        // still hurt: it goes straight back to the top.
        state.Characters[1].EatTimer = 900;
        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        Assert.Equal(1350, state.Characters[1].EatTimer);

        // At full health the re-arming stops, so whatever is left keeps running.
        state.Characters[1].Hp = 200;
        state.Characters[1].EatTimer = 900;
        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        Assert.Equal(900, state.Characters[1].EatTimer);
    }

    /// <summary>The alarm's whole body is <c>canEat = true</c>.</summary>
    [Fact]
    public void TheAlarmGivesTheSandvichBack()
    {
        var state = World();
        state.Characters[1].CanEat = false;
        state.Characters[1].EatTimer = 2;

        Timers.Advance(ref state);
        Assert.False(state.Characters[1].CanEat);

        Timers.Advance(ref state);
        Assert.True(state.Characters[1].CanEat);
    }

    /// <summary>
    /// <b>Eating suppresses movement, and friction still applies.</b>
    /// </summary>
    /// <remarks>
    /// <c>Begin Step.xml:237</c> wraps the acceleration and the both-keys cancel
    /// in <c>if(!taunting &amp;&amp; !omnomnomnom)</c> — but <b>not</b> the
    /// friction below them. So a heavy who starts eating at speed coasts to a
    /// stop rather than either stopping dead or sliding on for ever, and cannot
    /// accelerate while doing it.
    /// </remarks>
    [Fact]
    public void AnEatingHeavyCannotAccelerateButStillSlowsDown()
    {
        var state = World();
        state.Characters[1].HSpeed = 3;
        state.Characters[1].KeyState = PlayerKeys.Right;

        Assert.True(PlayerCommands.EatSandvich(ref state, 1));

        var before = state.Characters[1].HSpeed;

        CharacterPhysics.BeginStep(World1, ref state.Characters[1]);

        var after = state.Characters[1].HSpeed;

        Assert.True(after < before, $"friction should have slowed {before}, got {after}");
        Assert.True(after > 0, $"it should coast, not stop dead: {after}");
        Assert.False(state.Characters[1].Controlling);
    }

    /// <summary>
    /// The facing freezes for the whole eat, which decides who a nearby
    /// explosion hurts.
    /// </summary>
    /// <remarks>
    /// <b>Not cosmetic here.</b> <c>End Step.xml:86</c> writes
    /// <c>image_xscale = xscale</c> <em>after</em> the line deriving the facing
    /// from the aim, and <c>Aabb.ForCharacter</c> inflates a character's box by
    /// two pixels on the side it faces — the box <c>distance_to_object</c>
    /// measures splash against.
    /// </remarks>
    [Fact]
    public void TheFacingFreezesForTheWholeEat()
    {
        var state = World();
        state.Characters[1].NetAimDirection = 0;

        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        var facing = state.Characters[1].Facing;

        Assert.True(PlayerCommands.EatSandvich(ref state, 1));
        Assert.Equal(facing, state.Characters[1].EatFacing);

        // Aim the other way; the facing must not follow while eating.
        state.Characters[1].NetAimDirection = (ushort)Math.Round(180 * 65536.0 / 360.0);

        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        Assert.Equal(facing, state.Characters[1].Facing);

        // And it does follow again once the eat is over.
        state.Characters[1].Omnomnomnom = false;
        CharacterPhysics.EndStep(World1, ref state.Characters[1]);
        Assert.NotEqual(facing, state.Characters[1].Facing);
    }
}
