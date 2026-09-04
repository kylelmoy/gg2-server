using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Why a replay lands a freshly spawned character in the wrong place on some
/// maps: it gives it a Begin Step the reference never gave it.
/// </summary>
/// <remarks>
/// This is the root cause of the divergence
/// <see cref="SecondMapFixtureTests"/> and <see cref="TeamDeathmatchFixtureTests"/>
/// pin on <c>ctf_2dfort</c> and <c>gg_debug</c>.
/// <para>
/// <b>The reference creates a character in the ALARM STAGE</b> —
/// <c>player.alarm[5]</c> fires <c>PlayerSpawn</c>, which runs between Begin
/// Step and Step. So on its spawn tick the character gets a Step and an End Step
/// but <b>no Begin Step</b>, and Begin Step is where gravity is applied. Its
/// very first End Step therefore still sees <c>vspeed == 0</c>, which is the
/// guard on the stair descent
/// (<c>Character.events/End Step.xml</c>): <c>if(place_free(x,y+6))
/// if(!place_free(x,y+7)) y += 6;</c>.
/// <para>
/// So a character spawning within six pixels of the floor is <b>snapped</b> onto
/// it on its first tick, not dropped onto it over four or five.
/// </para>
/// </para>
/// <para>
/// <b>Which is why <c>ctf_truefort</c> never caught this.</b> Its spawn
/// markers are eighteen pixels above the floor — outside the six-pixel snap — so
/// both sides genuinely fall and agree. <c>gg_debug</c> (162 → 168) and
/// <c>ctf_2dfort</c> (894 → 900) are exactly six.
/// </para>
/// </remarks>
public sealed class SpawnTickTimingTests
{
    private static LevelMap Map() =>
        LevelMap.FromPng("gg_debug", ReferenceTree.ReadMap("gg_debug")).ForArea(1);

    /// <summary>The floor under gg_debug's red spawn, and the marker above it.</summary>
    /// <remarks>
    /// Measured from both sides: the engine and
    /// <c>CollisionWorld.PlaceFree</c> agree that a scout box is free at y &lt;= 168
    /// and blocked from 169, and both resolve the marker to (570, 162).
    /// </remarks>
    private const double SpawnY = 162;

    private const double RestingY = 168;

    /// <summary>
    /// <b>The simulation is right</b>: spawned through the alarm stage, as the
    /// reference does, the character is on the floor on its first tick.
    /// </summary>
    /// <remarks>
    /// This is the control for the whole diagnosis. The recording agrees with
    /// it exactly — <c>reference-gg_debug-closedtdm.trace</c> observes slot 1 at
    /// y = 168 with zero vspeed two ticks after the spawn wave, and it stays
    /// there.
    /// </remarks>
    [Fact]
    public void SpawningThroughTheAlarmStageLandsOnTheFloorImmediately()
    {
        var level = Map();
        var world = new CollisionWorld(level);
        var points = new SpawnPoints(level);

        var state = default(WorldState);
        state.GameMode = GameModes.ShapeOf(level).GameMode;
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;
        state.Players[1].Class = PlayerClass.Scout;

        // The reference's own route: arm alarm[5] and let the alarm stage place
        // the character, between Begin Step and Step.
        state.Players[1].RespawnTimer = 1;

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events, points);

        Assert.True(state.IsAlive(1), "the alarm stage did not spawn anybody");
        Assert.Equal(RestingY, state.Characters[1].Y);
        Assert.Equal(0, state.Characters[1].VSpeed);
    }

    /// <summary>
    /// <b>And placing the character before the tick — what a replay does —
    /// does not.</b>
    /// </summary>
    /// <remarks>
    /// <c>Replayer</c> applies a recorded <c>PLAYER_SPAWN</c> by calling
    /// <c>ITraceSimulation.SpawnCharacter</c> <em>before</em> running the tick,
    /// so the character exists for that tick's Begin Step, takes gravity there,
    /// and arrives at End Step with a non-zero <c>vspeed</c>. The stair descent
    /// is skipped and it falls the six pixels over the following ticks instead —
    /// which is the 0.4-pixel residual on <c>ctf_2dfort</c> and the still-falling
    /// 163.2 on <c>gg_debug</c>.
    /// <para>
    /// <b>The fix needs a seam that does not exist yet</b>: a way to spawn at a
    /// <em>given</em> point through the alarm stage. Arming
    /// <c>RespawnTimer</c> alone is not enough, because the stage picks its own
    /// point and would race the recorded choice — which is exactly why
    /// <c>PortedSimulation</c> places directly today (docs/PARITY.md, "a replay
    /// never respawns anybody by itself").
    /// </para>
    /// <para>
    /// <b>If this test fails, the seam has arrived and the bug is fixed</b> —
    /// delete it, and raise the two pinned windows it explains.
    /// </para>
    /// </remarks>
    [Fact]
    public void PlacingTheCharacterBeforeTheTickMakesItFallInstead()
    {
        var level = Map();
        var world = new CollisionWorld(level);
        var points = new SpawnPoints(level);

        var state = default(WorldState);
        state.GameMode = GameModes.ShapeOf(level).GameMode;
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;
        state.Players[1].Class = PlayerClass.Scout;

        // What the replayer does: place it, then run the tick.
        // The marker really is six pixels above the floor, which is what puts
        // this map inside the stair descent's reach and ctf_truefort outside it.
        Assert.True(points.TryGet(Team.Red, 0, 0, out _, out var markerY), "no red spawn point");
        Assert.Equal(SpawnY, markerY);
        Assert.Equal(6, RestingY - SpawnY);

        Assert.True(state.Spawn(points, 1, PlayerClass.Scout, Team.Red, 0, 0),
            "the spawn point did not resolve");

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events, points);

        Assert.True(state.IsAlive(1));

        // Still in the air, and moving — the snap did not happen.
        Assert.True(
            state.Characters[1].Y < RestingY,
            $"the pre-placed character reached {state.Characters[1].Y}, so the "
            + "spawn-tick timing difference is gone — good news, delete this test");

        Assert.NotEqual(0, state.Characters[1].VSpeed);
    }
}
