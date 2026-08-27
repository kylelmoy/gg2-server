using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The player behind a roster slot — the state that survives its character.
/// </summary>
/// <remarks>
/// The reference's <c>Player</c> object (<c>Objects/InGameElements/Player.xml</c>)
/// carries far more than this — stats, rewards, the whole bot-AI state — but
/// only three things on it feed the simulation: <c>team</c>, <c>class</c> and
/// <c>alarm[5]</c>, the respawn timer. <c>object</c> (the character, or -1) is
/// <see cref="WorldState.Alive"/>; <c>sentry</c> is
/// <see cref="WorldState.HasSentry"/>; <c>humiliated</c> and the stats arrive
/// with the features that read them.
/// <para>
/// ⚠️ <b>Make a player with <see cref="Fresh"/>, never <c>default</c>.</b>
/// <c>Team.Red</c> is 0, so a zeroed player is a red scout ready to respawn —
/// where <c>Player.events/Create.xml</c> starts every player a
/// <b>spectator</b> with the respawn alarm at rest. The same trap class as
/// <see cref="CharacterState.Fresh"/>.
/// </para>
/// </remarks>
public struct PlayerState
{
    /// <summary><c>player.team</c>. Spectators have no character and never respawn.</summary>
    public Team Team;

    /// <summary><c>player.class</c>: what the next spawn will be.</summary>
    public PlayerClass Class;

    /// <summary>
    /// <c>player.alarm[5]</c>: ticks until <c>PlayerSpawn()</c>. Rests at
    /// <see cref="Timers.Inactive"/>.
    /// </summary>
    /// <remarks>
    /// Armed to <see cref="WorldState.RespawnTime"/> by a death
    /// (<c>Character.events/Destroy.xml</c>) and by a team change with a live
    /// character (<c>processClientCommands.gml:175</c>), and to <b>1</b> — spawn
    /// in this same tick's alarm stage — when a characterless player picks a
    /// class or team (<c>:107, :177</c>).
    /// </remarks>
    public int RespawnTimer;

    /// <summary>
    /// <c>player.deathmatch_respawn_bypass</c>: the invulnerability a character
    /// still had when it died, carried across to its replacement.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is a respawn rule as well as a carried value.</b>
    /// <c>Character.events/Destroy.xml:37</c> is
    /// <c>if (place_meeting(x,y,SpawnRoom) or deathmatch_invulnerable != 0)</c>
    /// — a character that dies <em>while still invulnerable</em> arms
    /// <c>alarm[5] = 1</c> and comes back on the <b>next tick</b> with whatever
    /// was left, instead of waiting out the respawn time. So killing somebody
    /// through their spawn protection buys nothing at all.
    /// <para>
    /// Zero everywhere but Team Deathmatch, and cleared by any death that was
    /// not protected. The <c>SpawnRoom</c> half of that guard is still not
    /// modelled — see <see cref="Deaths"/>.
    /// </para>
    /// </remarks>
    public double DeathmatchRespawnBypass;

    /// <summary>
    /// <c>player.stats[]</c>: the scoreboard for this map, indexed by
    /// <see cref="Gg2.Protocol.StatIndex"/>.
    /// </summary>
    /// <remarks>
    /// Eleven of the thirteen are on the wire in every <c>FULL_UPDATE</c>. Award
    /// through <see cref="Scoring.Award"/> and never by writing here: every
    /// award in the reference writes <see cref="RoundStats"/> too.
    /// </remarks>
    public StatBuffer Stats;

    /// <summary>
    /// <c>player.roundStats[]</c>: the same counters for the current Arena round
    /// only.
    /// </summary>
    /// <remarks>
    /// Written by every award and read by exactly one thing —
    /// <c>ARENA_ENDROUND</c>'s MVP lists. Reset at every Arena round start and
    /// at a map change; never at a CTF or KotH round end, because those end the
    /// map.
    /// </remarks>
    public StatBuffer RoundStats;

    /// <summary><c>Player.events/Create.xml</c>: a spectator scout, alarm at rest.</summary>
    public static PlayerState Fresh => new()
    {
        Team = Team.Spectator,
        Class = PlayerClass.Scout,
        RespawnTimer = Timers.Inactive,
    };
}
