using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Standing on a moving platform: the character's own <c>with(DropdownPlatform)</c>
/// scans, which visit a <c>MovingPlatform</c> because it is a child of
/// <c>DropdownPlatform</c>.
/// </summary>
/// <remarks>
/// <c>Character.events/Begin Step.xml:32-49</c> (the ground test),
/// <c>Step.xml:90-107</c> (the catch and the tenth-pixel lift) and
/// <c>End Step.xml:21</c> (the stair guard). The carry
/// (<see cref="MovingPlatformCarryTests"/>) moves a rider who is already on
/// top; these are what put one there and keep them from falling through.
/// Full <see cref="MatchSimulation.Tick"/>s on <c>gg_debug2</c>'s raft, the
/// one map platform with a mask: it starts at (282, 150) and travels 60px up and
/// back at 1px a tick.
/// </remarks>
public sealed class MovingPlatformStandingTests
{
    private const double ScoutX = 286;

    private static (CollisionWorld World, WorldState State, int Raft) Scene(bool holdDown)
    {
        var map = LevelMap.FromPng("gg_debug2", ReferenceTree.ReadMap("gg_debug2"));
        var world = new CollisionWorld(map);
        var state = default(WorldState);
        MovingPlatforms.Place(ref state, map);

        var raft = -1;
        for (var i = 0; i < world.MovingPlatforms.Length; i++)
        {
            if (world.MovingPlatforms[i].Mask is not null)
                raft = i;
        }

        Assert.True(raft >= 0, "gg_debug2 has no masked platform");

        // In the air above the raft, falling onto it.
        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = ScoutX;
        character.Y = state.MovingPlatforms[raft].RoundedY - 40;
        character.YPrevious = character.Y;
        if (holdDown)
            character.KeyState = PlayerKeys.Down;

        state.Characters[1] = character;
        state.Weapons[1] = WeaponState.Spawn(PlayerClass.Scout, character.X, character.Y);
        state.Alive[1] = true;
        state.Players[1] = new PlayerState { Team = Team.Red, Class = PlayerClass.Scout, RespawnTimer = Timers.Inactive };
        state.HasPlayer[1] = true;

        return (world, state, raft);
    }

    private static void Tick(CollisionWorld world, ref WorldState state, bool holdDown)
    {
        var input = new PlayerInput[WorldState.MaxSlots];
        if (holdDown)
            input[1] = new PlayerInput { Keys = PlayerKeys.Down };

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, input, ref events);
    }

    /// <summary>
    /// A scout dropped onto the raft lands on it and rides a whole cycle,
    /// up 60px and back down, without falling through.
    /// </summary>
    [Fact]
    public void AScoutLandsOnTheRaftAndRidesIt()
    {
        var (world, state, raft) = Scene(holdDown: false);
        var landed = false;

        for (var tick = 0; tick < 150; tick++)
        {
            Tick(world, ref state, holdDown: false);

            ref readonly var scout = ref state.Characters[1];
            ref readonly var platform = ref state.MovingPlatforms[raft];

            Assert.True(state.Alive[1]);
            Assert.True(scout.Y + scout.Box.Bottom < platform.RoundedY,
                $"tick {tick}: scout at y {scout.Y} is not above the raft at y {platform.RoundedY}");

            if (scout.OnGround)
                landed = true;
        }

        Assert.True(landed, "the scout never stood on the raft");

        // It rode: the raft went up and the scout went with it.
        Assert.True(state.MovingPlatforms[raft].MovementState != MovingPlatforms.Stopped);
    }

    /// <summary>
    /// The control: holding down skips every dropdown scan, so the same scout
    /// falls through. Without it the test above could pass on a raft that
    /// stood somewhere the scout never reached.
    /// </summary>
    [Fact]
    public void HoldingDownFallsThroughTheRaft()
    {
        var (world, state, raft) = Scene(holdDown: true);
        var below = false;

        for (var tick = 0; tick < 60 && state.Alive[1]; tick++)
        {
            Tick(world, ref state, holdDown: true);

            if (state.Characters[1].Y > state.MovingPlatforms[raft].RoundedY)
                below = true;
        }

        Assert.True(below, "a scout holding down did not fall through the raft");
    }
}
