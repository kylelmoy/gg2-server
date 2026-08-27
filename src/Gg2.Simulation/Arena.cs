using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Which phase of an Arena map the match is in.
/// </summary>
/// <remarks>
/// <c>ArenaHUD.events/Create.xml</c>'s four constants, and they go on the wire
/// as a byte in the <c>FULL_UPDATE</c> tail — so the numbering is contract.
/// </remarks>
public enum ArenaState : byte
{
    /// <summary>Not enough players to start: at least one a side is needed.</summary>
    Waiting = 0,

    /// <summary>Ten seconds in which people can spawn and change team or class.</summary>
    RoundSetup = 1,

    /// <summary>The round proper. Nobody respawns.</summary>
    RoundProper = 2,

    /// <summary>Ten seconds of scoreboard before the next round.</summary>
    RoundEnd = 3,
}

/// <summary>
/// Arena: a sequence of elimination rounds on one map.
/// </summary>
/// <remarks>
/// <c>Objects/Overlays/Gamemodes/ArenaHUD.events/{Create, Step}.xml</c> and
/// <c>Scripts/Maps/Arena/</c>. The reference's own summary, from the top of the
/// Create event: a round is a fixed setup period, then the round proper, won by
/// eliminating the enemy team <em>or</em> by taking the control point (which
/// unlocks a minute in), then a fixed scoreboard period. A map ends when one
/// team has won <c>caplimit</c> rounds or the map timer runs out — and either
/// way only after a round has finished, so a running round always gets to end.
/// <para>
/// ⚠️ <b>This is the only mode where nobody respawns.</b>
/// <c>spawningAllowed()</c> is true only in <see cref="ArenaState.Waiting"/> and
/// <see cref="ArenaState.RoundSetup"/>, so a death in the round proper is final
/// until the next round — which is what makes elimination a win condition.
/// </para>
/// </remarks>
public static class Arena
{
    /// <summary>Ticks of setup before a round starts. <c>roundStart = 300</c>.</summary>
    public const int RoundSetupTicks = 300;

    /// <summary>Ticks of scoreboard after a round. <c>endCount = 300</c>.</summary>
    public const int RoundEndTicks = 300;

    /// <summary>
    /// Ticks before the control point opens — a full minute, twice KotH's.
    /// </summary>
    public const int UnlockTicks = 1800;

    /// <summary>
    /// The reference's <c>maxCappingPower</c>: the most the capture rate can be
    /// multiplied by when the enemy team is wiped out.
    /// </summary>
    public const double MaxCappingPower = 4;

    /// <summary>
    /// How fast a team captures the arena point, given how much of the
    /// <em>enemy</em> team is still alive.
    /// </summary>
    /// <remarks>
    /// <c>ControlPoint.events/Step.xml:88-116</c>, and it is the one place in
    /// the game the capture rate is not 1.
    /// <code>
    /// cappingPower[BLUE] = max(1, reds) / ((1 - 1/4)*livereds + (1/4)*max(1, reds))
    /// </code>
    /// ⚠️ <b>The teams are crossed.</b> Blue's capping power is computed from
    /// <em>red's</em> numbers: the fewer of the enemy are still alive, the
    /// faster you take the point. With every enemy alive it is 1; with none it
    /// is 4. That is what stops a wiped-out team stalling the round by hiding.
    /// <para>
    /// <c>max(1, reds)</c> guards a team with nobody on it, so the division is
    /// never by zero even before anybody has joined.
    /// </para>
    /// </remarks>
    /// <param name="enemies">Players on the capturing team's opponent.</param>
    /// <param name="liveEnemies">How many of those have a character.</param>
    public static double CappingPower(int enemies, int liveEnemies)
    {
        var total = Math.Max(1, enemies);

        return total / (((1 - (1 / MaxCappingPower)) * liveEnemies) + ((1 / MaxCappingPower) * total));
    }

    /// <summary>
    /// <c>spawningAllowed()</c>: whether the alarm-5 respawn does anything.
    /// </summary>
    /// <remarks>
    /// <c>Scripts/Maps/Arena/spawningAllowed.gml</c>, and it returns true
    /// unconditionally for every mode that is not Arena. ⚠️ The alarm still
    /// fires and is still spent when this is false — the guard is inside
    /// <c>PlayerSpawn</c>, the same shape as the map-changing one.
    /// </remarks>
    public static bool SpawningAllowed(in WorldState state) =>
        state.GameMode != GameMode.Arena
        || state.ArenaState is ArenaState.Waiting or ArenaState.RoundSetup;

    /// <summary>
    /// <c>ArenaHUD.state == ARENA_STATE_ROUND_END</c>, which three unrelated
    /// pieces of the simulation consult.
    /// </summary>
    /// <remarks>
    /// It locks every control point, stops the point counting who is standing on
    /// it, and gives everybody full ammo — the same three things
    /// <c>global.mapchanging</c> does, and in each case the reference tests them
    /// together.
    /// </remarks>
    public static bool RoundEnded(in WorldState state) =>
        state.GameMode == GameMode.Arena && state.ArenaState == ArenaState.RoundEnd;

    /// <summary>
    /// <c>ArenaHUD.events/Step.xml</c>: the counters, then the state machine.
    /// </summary>
    /// <remarks>
    /// <b>Every counter runs regardless of state; the state only decides what
    /// happens when one reaches zero.</b> The one exception is
    /// <c>cpUnlock</c>, which is frozen during the setup period —
    /// <c>if(state != ARENA_STATE_ROUND_SETUP and cpUnlock > 0)</c> — so the
    /// minute before the point opens is a minute of the round proper.
    /// <para>
    /// ⚠️ <b>The team head-counts have a scoping bug in the reference and the
    /// port reproduces its effect.</b> The loop reads
    /// <c>if (player.object != -1 or alarm[5] != -1)</c>, and <c>alarm[5]</c>
    /// there is <em>the HUD's own</em> alarm, not the player's — a bare
    /// <c>alarm[]</c> inside a plain <c>for</c> loop is <c>self</c>'s.
    /// <c>ArenaHUD</c> has no alarm events and nothing ever sets one, so it is
    /// always -1 and the clause is always false. The intent was plainly "alive
    /// or waiting to respawn"; the behaviour is "alive". Which is the right
    /// behaviour for elimination anyway, and is why the bug is invisible.
    /// </para>
    /// </remarks>
    public static void Step(
        ref WorldState state,
        Maps.SpawnPoints? spawnPoints,
        ref TickEvents events)
    {
        if (state.GameMode != GameMode.Arena)
            return;

        CountTeams(state, out var redPlayers, out var bluePlayers, out var redAlive, out var blueAlive);

        // The control point is frozen through the setup period and counts down
        // through everything else.
        if (state.ArenaState != ArenaState.RoundSetup && state.ControlPointUnlock > 0)
        {
            state.ControlPointUnlock--;

            if (state.ControlPointUnlock <= 0)
            {
                for (var i = 0; i < state.ControlPointCount; i++)
                    state.ControlPoints[i].Locked = false;

                state.UnlockedControlPointsThisTick = true;
            }
            else if (state.ControlPointUnlock == 150)
            {
                state.ScoreboardSyncRequested = true;
            }
        }

        if (state.ArenaState == ArenaState.RoundSetup && state.ArenaRoundStart > 0)
            state.ArenaRoundStart--;

        if (state.ArenaState == ArenaState.RoundEnd && state.ArenaEndCount > 0)
            state.ArenaEndCount--;

        if (state.RoundTimer > 0)
            state.RoundTimer--;

        // ⚠️ Overtime latches here, unlike the control-point modes': it is set
        // and never cleared within a round, and only a fresh round resets it.
        if (state.ArenaState is ArenaState.RoundSetup or ArenaState.RoundProper && state.RoundTimer <= 0)
            state.Overtime = true;

        switch (state.ArenaState)
        {
            case ArenaState.Waiting:
                // ⚠️ The one place a map can end without a round finishing: an
                // empty server whose clock runs out is a draw immediately.
                if (state.TimeLimitMinutes > 0 && state.RoundTimer <= 0)
                    Rounds.Decide(ref state, Team.Spectator);
                else if (redPlayers >= 1 && bluePlayers >= 1)
                    Restart(ref state, spawnPoints, ref events);

                break;

            case ArenaState.RoundSetup:
                if (state.ArenaRoundStart <= 0)
                    StartRound(ref state, spawnPoints, ref events);

                break;

            case ArenaState.RoundProper:
            {
                // Taking the point wins outright; so does wiping the enemy out.
                // The point is tested first, so a simultaneous capture and
                // elimination goes to whoever holds the point.
                var winner = Team.Spectator;

                if (state.ControlPointCount > 0 && state.ControlPoints[0].Owned)
                    winner = state.ControlPoints[0].Team;
                else if (blueAlive == 0)
                    winner = Team.Red;
                else if (redAlive == 0)
                    winner = Team.Blue;

                if (winner is Team.Red or Team.Blue)
                    EndRound(ref state, winner, ref events);

                break;
            }

            case ArenaState.RoundEnd:
                if (state.ArenaEndCount > 0)
                    break;

                if (state.CapLimit > 0 && state.ArenaRedWins >= state.CapLimit)
                    Rounds.Decide(ref state, Team.Red);
                else if (state.CapLimit > 0 && state.ArenaBlueWins >= state.CapLimit)
                    Rounds.Decide(ref state, Team.Blue);
                else if (state.TimeLimitMinutes > 0 && state.RoundTimer <= 0)
                    Rounds.Decide(ref state, Team.Spectator);
                else if (redPlayers == 0 || bluePlayers == 0)
                    WaitForPlayers(ref state, spawnPoints, ref events);
                else
                    Restart(ref state, spawnPoints, ref events);

                break;
        }
    }

    /// <summary>
    /// How many players each team has, and how many of those are alive.
    /// </summary>
    /// <remarks>
    /// A <em>player</em> count, not a character count: somebody who has picked a
    /// team but has no character still counts towards "are there enough people
    /// to start", which is what keeps a server with two dead players out of
    /// <see cref="ArenaState.Waiting"/> between rounds.
    /// </remarks>
    private static void CountTeams(
        in WorldState state,
        out int redPlayers,
        out int bluePlayers,
        out int redAlive,
        out int blueAlive)
    {
        redPlayers = 0;
        bluePlayers = 0;
        redAlive = 0;
        blueAlive = 0;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasPlayer[slot])
                continue;

            var alive = state.Alive[slot] ? 1 : 0;

            switch (state.Players[slot].Team)
            {
                case Team.Red:
                    redPlayers++;
                    redAlive += alive;
                    break;

                case Team.Blue:
                    bluePlayers++;
                    blueAlive += alive;
                    break;
            }
        }
    }

    /// <summary>
    /// <c>doEventArenaRestart.gml</c>: the setup period before a round.
    /// </summary>
    /// <remarks>
    /// The point goes neutral and locked, the map is swept, and everybody
    /// respawns. ⚠️ <b><c>serverArenaRestart</c>'s team balance is not
    /// transcribed here</b>: it runs <em>before</em> everything below, and
    /// unlike the ordinary autobalance it loops until the gap is closed and does
    /// not wait for anybody to be dead. <see cref="TeamBalance.ArenaRestart"/>.
    /// </remarks>
    private static void Restart(
        ref WorldState state,
        Maps.SpawnPoints? spawnPoints,
        ref TickEvents events)
    {
        // serverArenaRestart.gml:1-63, the whole of the script above its
        // sendEventArenaRestart — so the moves are decided before the round's
        // own state is touched and before anybody is respawned.
        TeamBalance.ArenaRestart(ref state, ref events);

        state.ArenaWinner = Team.Spectator;
        state.ArenaEndCount = 0;
        state.ArenaRoundStart = RoundSetupTicks;
        state.ControlPointUnlock = UnlockTicks;
        state.ArenaState = ArenaState.RoundSetup;
        state.Overtime = false;

        ResetPoint(ref state);
        Sweep(ref state, spawnPoints, ref events);

        events.ArenaRestarted = true;
    }

    /// <summary>
    /// <c>doEventArenaStartRound.gml</c>: the round proper begins.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>cpUnlock</c> is reset here as well as at the restart</b>, so the
    /// minute before the point opens is measured from the <em>start of the
    /// round</em> and not from the setup period. It is also the reason the
    /// counter is frozen during setup: it would otherwise be reset twice and
    /// the freeze would be pointless.
    /// <para>
    /// The round stats it resets are the scoreboard's and are not modelled.
    /// </para>
    /// </remarks>
    private static void StartRound(
        ref WorldState state,
        Maps.SpawnPoints? spawnPoints,
        ref TickEvents events)
    {
        state.ArenaWinner = Team.Spectator;
        state.ArenaEndCount = 0;
        state.ArenaRoundStart = 0;
        state.ControlPointUnlock = UnlockTicks;
        state.ArenaState = ArenaState.RoundProper;

        // doEventArenaStartRound.gml:10-12, in that order:
        // destroyGameplayObjects(), resetRoundStats(), respawnEveryone(). The
        // round scoreboard is the one thing an Arena round has that a CTF round
        // does not, and ARENA_ENDROUND's MVP lists are its only reader.
        Sweep(ref state, spawnPoints, ref events);
        Scoring.ResetRound(ref state);

        events.ArenaRoundStarted = true;
    }

    /// <summary>
    /// <c>doEventArenaEndRound.gml</c>: somebody has won the round.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It does <em>not</em> sweep the map or respawn anybody.</b> The
    /// bodies, the sentries and anything in the air stay where they are for the
    /// ten seconds the scoreboard is up — which is what the losing team's
    /// humiliation is drawn over. Only the next restart clears them.
    /// <para>
    /// <c>with Sentry if team != win event_user(1)</c> destroys the losing
    /// team's sentries, and the reference does it twice — once in
    /// <c>serverArenaEndRound</c> and again in <c>doEventArenaEndRound</c>,
    /// which the server calls. The second is a no-op.
    /// </para>
    /// <para>
    /// <c>humiliated</c> is set on the losing team here and is not modelled; it
    /// suppresses their ammo refill and drops a spy's cloak.
    /// </para>
    /// </remarks>
    private static void EndRound(ref WorldState state, Team winner, ref TickEvents events)
    {
        if (winner == Team.Red)
            state.ArenaRedWins++;
        else
            state.ArenaBlueWins++;

        state.ArenaWinner = winner;
        state.ArenaEndCount = RoundEndTicks;
        state.ArenaRoundStart = 0;
        state.ArenaState = ArenaState.RoundEnd;

        // The losing team's sentries go with the round.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.HasSentry[slot] && state.Sentries[slot].Team != winner)
            {
                state.Sentries[slot] = default;
                state.HasSentry[slot] = false;
            }
        }

        events.ArenaRoundEnded = true;
        events.ArenaRoundWinner = winner;
    }

    /// <summary>
    /// <c>doEventArenaWaitForPlayers.gml</c>: a team has emptied out.
    /// </summary>
    /// <remarks>
    /// The same shape as a restart with no setup countdown: the map is swept,
    /// everybody respawns, and nothing advances until both teams have somebody
    /// on them again.
    /// </remarks>
    private static void WaitForPlayers(
        ref WorldState state,
        Maps.SpawnPoints? spawnPoints,
        ref TickEvents events)
    {
        state.ArenaWinner = Team.Spectator;
        state.ArenaEndCount = 0;
        state.ArenaRoundStart = 0;
        state.ControlPointUnlock = UnlockTicks;
        state.ArenaState = ArenaState.Waiting;

        ResetPoint(ref state);
        Sweep(ref state, spawnPoints, ref events);

        events.ArenaWaitingForPlayers = true;
    }

    private static void ResetPoint(ref WorldState state)
    {
        if (state.ControlPointCount == 0)
            return;

        state.ControlPoints[0].Owned = false;
        state.ControlPoints[0].BeingCaptured = false;
        state.ControlPoints[0].Capping = 0;
        state.ControlPoints[0].Locked = true;
    }

    /// <summary>
    /// <c>destroyGameplayObjects()</c> then <c>respawnEveryone()</c>.
    /// </summary>
    /// <remarks>
    /// Everything in the air goes — sentries, rockets, pellets, flames, needles
    /// — and then every character is destroyed and immediately respawned.
    /// ⚠️ <b>Destroyed, not merely moved</b>, so each one runs the whole Destroy
    /// event: a carried briefcase is dropped, a demoman's mines go with their
    /// weapon, and a medic's beam is released. The respawn is
    /// <c>PlayerSpawn()</c> called directly rather than through the alarm, so it
    /// happens on this tick.
    /// </remarks>
    private static void Sweep(
        ref WorldState state,
        Maps.SpawnPoints? spawnPoints,
        ref TickEvents events)
    {
        while (state.LiveProjectileCount > 0)
            state.FreeProjectile(state.LiveProjectileHandle(0));

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            state.Sentries[slot] = default;
            state.HasSentry[slot] = false;

            state.Clear(slot);

            // ⚠️ `PlayerSpawn()` on the spot, not `alarm[5] = 1`. The alarm
            // stage has already run by the time a HUD's Step does, so arming it
            // would put every character in a new round one tick late — and the
            // round starts on this tick. A spectator's spawn does nothing, which
            // is what makes the blanket call safe.
            if (state.HasPlayer[slot])
                Timers.SpawnNow(spawnPoints, ref state, slot, ref events);
        }
    }
}
