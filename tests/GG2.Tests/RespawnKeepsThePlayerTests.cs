using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// A respawn replaces the character and leaves the player alone.
/// </summary>
/// <remarks>
/// <b><c>doEventSpawn.gml:30-38</c> destroys <c>spawner.object</c> and creates a
/// new character into it.</b> The <c>Player</c> instance beside it is never
/// touched: it is created on a join (<c>serviceJoiningPlayer.gml:117</c>,
/// <c>GameServerCreate.gml:39</c>) and <c>Player.events/Create.xml:28-40</c> is
/// the only place <c>stats[]</c> is zeroed. So everything a Player owns has to
/// outlive a spawn.
/// <para>
/// ⛔ <b>Every assertion here failed before 2026-09-07</b>, when
/// <see cref="WorldState.Spawn"/> assigned a fresh <c>PlayerState</c>. Nothing
/// else in the suite went red for any of them: no test asserted a stat across a
/// respawn, and the arm measurements that would have caught the
/// <c>IsBot</c> half all run <c>EngageOpponents.Anyone</c> by design.
/// </para>
/// </remarks>
public sealed class RespawnKeepsThePlayerTests
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// The scoreboard survives a respawn — which is what makes <c>Deaths</c>
    /// countable at all.
    /// </summary>
    /// <remarks>
    /// <b><c>Deaths</c> is the one that could never be right.</b> A death awards
    /// it and arms the respawn that used to wipe it, so it read 0 for everybody
    /// for the life of a server — a scoreboard that looks merely quiet.
    /// </remarks>
    [Fact]
    public void TheScoreboardSurvivesARespawn()
    {
        var state = Spawned(out var points);

        Scoring.Award(ref state, 1, StatIndex.Kills, 2);
        Scoring.Award(ref state, 1, StatIndex.Deaths, 1);

        Assert.True(state.Spawn(points, 1, PlayerClass.Scout, Team.Red, 0, 0));

        Assert.Equal(2, Scoring.Get(state, 1, StatIndex.Kills));
        Assert.Equal(1, Scoring.Get(state, 1, StatIndex.Deaths));

        // The Arena-round copy too: `resetRoundStats.gml` is what clears it, and
        // that script only makes sense if a spawn does not.
        Assert.Equal(2, Scoring.GetRound(state, 1, StatIndex.Kills));
    }

    /// <summary>
    /// The three player flags survive: humiliation, queued jumping, and this
    /// port's bot mirror.
    /// </summary>
    /// <remarks>
    /// <b><c>QueueJump</c> is the one that reaches the wire.</b> It is a
    /// CLIENT_SETTINGS preference the reference broadcasts to everybody because
    /// <c>Character.events/Begin Step</c> reads it on every landing — so a server
    /// that dropped it at the first death would diverge from every client's dead
    /// reckoning, silently, and only while re-simulating between updates.
    /// </remarks>
    [Fact]
    public void ThePlayerFlagsSurviveARespawn()
    {
        var state = Spawned(out var points);

        state.Players[1].Humiliated = true;
        state.Players[1].QueueJump = true;
        state.Players[1].IsBot = true;

        Assert.True(state.Spawn(points, 1, PlayerClass.Soldier, Team.Red, 0, 0));

        Assert.True(state.Players[1].Humiliated);
        Assert.True(state.Players[1].QueueJump);
        Assert.True(state.Players[1].IsBot);

        // And the class the spawn was asked for did land, so this is not a test
        // that would pass on a Spawn that wrote nothing at all.
        Assert.Equal(PlayerClass.Soldier, state.Players[1].Class);
    }

    /// <summary>
    /// The deathmatch bypass is the one field a spawn really does consume.
    /// </summary>
    /// <remarks>
    /// <b>The exception that makes the rule checkable.</b> A character that dies
    /// while still invulnerable comes back on the next tick with what was left
    /// (<c>Character.events/Destroy.xml:37</c>); the spawn it feeds spends it,
    /// and the next protected death writes it again. A fix that preserved the
    /// whole player indiscriminately would give a player permanent spawn
    /// protection in Team Deathmatch.
    /// </remarks>
    [Fact]
    public void TheDeathmatchBypassIsSpentByTheSpawnItFeeds()
    {
        var state = Spawned(out var points);

        state.GameMode = GameMode.TeamDeathmatch;
        state.Players[1].DeathmatchRespawnBypass = 40;

        Assert.True(state.Spawn(points, 1, PlayerClass.Scout, Team.Red, 0, 0));

        Assert.Equal(0, state.Players[1].DeathmatchRespawnBypass);
        Assert.Equal(40, state.Characters[1].DeathmatchInvulnerable);
    }

    /// <summary>
    /// A slot that never had a player starts from a spectator scout, not from
    /// what the last occupant left.
    /// </summary>
    /// <remarks>
    /// <b>The half a "preserve the player" fix gets wrong.</b> <c>Spawn</c> is
    /// reached with <c>HasPlayer</c> false by the parity replayer and by every
    /// pre-phase-6 caller, and in-place mutation there would inherit a stale
    /// struct — a new joiner arriving with the previous occupant's kills.
    /// </remarks>
    [Fact]
    public void ASlotWithNoPlayerStartsFresh()
    {
        var state = Spawned(out var points);

        state.AddPlayer(2);
        Scoring.Award(ref state, 2, StatIndex.Kills, 5);
        state.Players[2].IsBot = true;

        // ⚠️ The tail RemoveSlot leaves is `default`, not `Fresh` — a red scout
        // with its alarm at zero, which is the trap PlayerState.Fresh exists for.
        // Spawning into it is what a slot reused after a departure does.
        state.RemoveSlot(2);
        Assert.False(state.HasPlayer[2]);

        Assert.True(state.Spawn(points, 2, PlayerClass.Scout, Team.Red, 0, 0));

        Assert.Equal(0, Scoring.Get(state, 2, StatIndex.Kills));
        Assert.False(state.Players[2].IsBot);
        Assert.Equal(Timers.Inactive, state.Players[2].RespawnTimer);
    }

    /// <summary>A world with one spawned player in slot 1.</summary>
    private static WorldState Spawned(out SpawnPoints points)
    {
        var level = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);
        var state = default(WorldState);

        points = new SpawnPoints(level);

        state.AddPlayer(1);
        Assert.True(state.Spawn(points, 1, PlayerClass.Scout, Team.Red, 0, 0));

        return state;
    }
}
