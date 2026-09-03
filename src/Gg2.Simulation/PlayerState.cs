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
/// <see cref="WorldState.HasSentry"/>; the stats arrive with the features that
/// read them.
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
    /// <c>player.humiliated</c>: this player was on the losing side of the
    /// Arena round now on the scoreboard.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Arena and nothing else, and it is a <em>player</em> flag rather
    /// than a character one</b> — it survives the death it is usually earned by.
    /// <c>doEventArenaEndRound.gml:26</c> is <c>with Player if team != win
    /// humiliated = 1</c>, which takes spectators with it because their team is
    /// not the winner either; <c>doEventArenaRestart.gml:11</c> and
    /// <c>doEventArenaWaitForPlayers.gml:12</c> clear it on everybody, as does
    /// <c>basicRoomSetup.gml:33</c> on a map load.
    /// <para>
    /// <see cref="CharacterState.Humiliated"/> is the per-tick copy the physics
    /// reads, mirrored the same way <c>MapChanging</c> is.
    /// </para>
    /// </remarks>
    public bool Humiliated;

    /// <summary>
    /// Whether this roster slot is driven by a bot rather than by a socket.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>NOTHING IN <c>MatchSimulation</c> MAY READ THIS, ever.</b> "The
    /// simulation never special-cases a bot" is one of the three rules that
    /// outrank everything in the bot layer (<c>CLAUDE.md</c>): a bot is an
    /// <c>IInputSource</c> like a socket, and a simulation that branched on this
    /// would let a bot do something a client cannot.
    /// <c>BotIsNotVisibleToTheSimulationTests</c> is the gate — it runs the same
    /// scripted inputs with the flag set every way and requires the resulting
    /// world to be identical.
    /// <para>
    /// It is here so that <b>bot code</b> can tell a human opponent from another
    /// bot — <c>Handover.ShouldEngage</c> under
    /// <c>EngageOpponents.HumansOnly</c>. It lives in <see cref="WorldState"/>
    /// rather than being read off the server's roster so that a bot's decisions
    /// stay a pure function of the state: a handover that depended on
    /// server-side data no snapshot carries could not be replayed, which is the
    /// property <c>docs/BOTS.md</c> asks of every bot input.
    /// </para>
    /// <para>
    /// ⚠️ <b>Default false means "human", which is the safe direction.</b> A slot
    /// nobody set is treated as a person, so the worst a missed assignment can do
    /// is engage the controller more often than intended — never less. The
    /// opposite default would silently downgrade real players to scripted
    /// opponents.
    /// </para>
    /// </remarks>
    public bool IsBot;

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
