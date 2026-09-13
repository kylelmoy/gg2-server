using System.Net;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

public sealed partial class GameServer
{
    // --- the control surface -------------------------------------------------
    //
    // docs/ARCHITECTURE.md's "A control surface, eventually", and phase 8's last
    // item. Everything here is an OPERATOR action — freezing the world, moving a
    // character, changing the map — and none of it is something a client can ask
    // for. ⚠️ Anything a client CAN ask for does not belong here: it goes
    // through Execute(RosterEntry, PlayerCommand), the one dispatch the socket
    // path and the bots already share, or the control port becomes a third way
    // to do what CLAUDE.md says must have exactly one.

    /// <summary>
    /// A live character's <em>exact</em> position and health.
    /// </summary>
    /// <remarks>
    /// ⚠️ The unquantised doubles, deliberately — <c>TryGetCharacter</c> hands
    /// back the wire blocks, which are rounded to what a client can observe. A
    /// control port exists to answer "where is it really", and a debugging tool
    /// that silently reported the wire's rounding would make every sub-pixel
    /// question unanswerable.
    /// </remarks>
    public bool TryGetExactPosition(int slot, out double x, out double y, out double hp)
    {
        x = 0;
        y = 0;
        hp = 0;

        if (slot < 0 || slot >= WorldState.MaxSlots || !_world.IsAlive(slot))
            return false;

        ref readonly var character = ref _world.Characters[slot];

        x = character.X;
        y = character.Y;
        hp = character.Hp;

        return true;
    }

    /// <summary>Ticks left on the round clock — the value the HUD blocks carry.</summary>
    public int RoundTimer => _world.RoundTimer;

    /// <summary>Whether the round has been decided and by whom.</summary>
    public bool RoundDecided => _world.RoundDecided;

    /// <inheritdoc cref="RoundDecided"/>
    public Team RoundWinner => _world.Winner;

    // --- what an operator surface reads -------------------------------------
    //
    // ControlCommands.State builds the control port's and the web panel's one
    // shared view out of these. They are reads, they run on the tick thread with
    // everything else, and none of them is on any hot path.

    /// <summary>Seconds since <see cref="Start"/>.</summary>
    public double UptimeSeconds => (DateTime.UtcNow - _startedUtc).TotalSeconds;

    /// <inheritdoc cref="_tickWorkMs"/>
    public double TickWorkMilliseconds => _tickWorkMs;

    /// <summary>Ticks that overran their pacing interval since startup.</summary>
    /// <inheritdoc cref="_lateTicks" path="/remarks"/>
    public long LateTicks => _lateTicks;

    /// <summary>The worst overrun seen, in milliseconds past the interval.</summary>
    public double WorstOverrunMilliseconds => _worstOverrunMs;

    /// <summary>Bytes this server has put on the wire, across every connection.</summary>
    public long BytesSent
    {
        get
        {
            var total = _bytesRetired;

            foreach (var client in _clients)
                total += client.Connection.BytesSent;

            return total;
        }
    }

    /// <summary>
    /// The mean and worst tick cost since the last call, and resets.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Reading it clears it</b>, so there is exactly one caller — the web
    /// panel's sampler, once a second, on this thread. A second caller would
    /// silently halve both of their windows.
    /// ⚠️ <paramref name="ticks"/> is zero on a server whose <see cref="Run"/>
    /// loop is not the thing driving it; the caller must not divide by it.
    /// </remarks>
    public void TakeTickWork(out double meanMs, out double peakMs, out int ticks)
    {
        ticks = _tickWorkCount;
        meanMs = ticks == 0 ? 0 : _tickWorkSumMs / ticks;
        peakMs = _tickWorkPeakMs;

        _tickWorkSumMs = 0;
        _tickWorkPeakMs = 0;
        _tickWorkCount = 0;
    }

    /// <summary>The gamemode the loaded map is being played as.</summary>
    public GameMode GameMode => _match.GameMode;

    /// <summary><c>map_width()</c> in world pixels. See <see cref="LevelMap.Width"/>.</summary>
    public int MapWidth => _match.Map.Width;

    /// <inheritdoc cref="MapWidth"/>
    public int MapHeight => _match.Map.Height;

    /// <summary>The 1-based area of a multi-area map being played.</summary>
    public byte CurrentMapArea => _match.CurrentMapArea;

    /// <inheritdoc cref="CurrentMapArea"/>
    public int TotalMapAreas => _match.TotalMapAreas;

    /// <summary>Ticks left on the setup gate, or 0 when there is none.</summary>
    public int SetupTimer => _world.SetupTimer;

    /// <summary>
    /// The map the server is on its way to, or empty when it is not changing.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A map change is a ten-second countdown, not an event</b>
    /// (<c>Rounds.MapChangeDelay</c>: <c>MAP_END</c> goes out, then the load).
    /// An operator surface that reported only "accepted" would look like nothing
    /// had happened for ten seconds.
    /// </remarks>
    public string NextMap => _world.MapChanging ? _nextMap : string.Empty;

    /// <summary>Ticks until the pending map change lands, or -1 when there is none.</summary>
    public int MapChangeCountdown => _mapChangeCountdown;

    /// <summary><c>global.redCaps</c>. ⚠️ Also the team's kill count in deathmatch.</summary>
    public int RedCaps => _world.RedCaps;

    /// <inheritdoc cref="RedCaps"/>
    public int BlueCaps => _world.BlueCaps;

    /// <summary>How many control points the loaded map has.</summary>
    public int ControlPointCount => _world.ControlPointCount;

    /// <summary>One control point's ownership and capture progress.</summary>
    /// <remarks>
    /// ⚠️ Fully qualified: <c>Gg2.Server.ControlPointState</c> also exists, on
    /// <see cref="MatchState"/>, and is a different type holding the map's copy
    /// rather than the simulation's.
    /// </remarks>
    public Gg2.Simulation.ControlPointState ControlPointAt(int index) => _world.ControlPoints[index];

    /// <summary>A team's briefcase — where it is, and whether it is on the ground.</summary>
    public IntelState IntelOf(Team team) => team == Team.Red ? _world.RedIntel : _world.BlueIntel;

    /// <summary>
    /// The slot carrying <paramref name="team"/>'s briefcase, or -1.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A team carries the OTHER team's intel</b>, so red's briefcase is
    /// held by a blue player. Getting this backwards puts the marker on the
    /// wrong side of the map, which looks like a physics bug and is not one.
    /// </remarks>
    public int IntelCarrier(Team team)
    {
        var thief = team == Team.Red ? Team.Blue : Team.Red;
        var live = Math.Min(_roster.Count, WorldState.MaxSlots);

        for (var slot = 0; slot < live; slot++)
        {
            if (_world.IsAlive(slot)
                && _roster[slot].Team == thief
                && _world.Characters[slot].CarryingIntel)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Whether the character in <paramref name="slot"/> is holding a briefcase.</summary>
    public bool IsCarryingIntel(int slot) =>
        slot >= 0 && slot < WorldState.MaxSlots
        && _world.IsAlive(slot)
        && _world.Characters[slot].CarryingIntel;

    /// <summary>Whether the character in <paramref name="slot"/> is mid-taunt.</summary>
    /// <remarks>
    /// A taunt is observable simulation rather than decoration: it freezes the
    /// character where it stands, and the client derives that from the same key
    /// bit (<c>docs/TRAPS-SIMULATION.md</c>, "Taunting and the humiliation
    /// round"). Exposed for the same reason as <see cref="IsCarryingIntel"/> —
    /// a diagnostic read of a state a transition has to clean up.
    /// </remarks>
    public bool IsTaunting(int slot) =>
        slot >= 0 && slot < WorldState.MaxSlots
        && _world.IsAlive(slot)
        && _world.Characters[slot].Taunting;

    /// <summary>One roster entry's scoreboard, as the wire's stats block.</summary>
    public PlayerStatsBlock StatisticsOf(int slot) => Statistics(slot);


    /// <summary>
    /// Whether this slot is a bot.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The allocation-free way to ask.</b> <see cref="BotSlots"/> builds a
    /// <c>List&lt;int&gt;</c> on every access, which is fine for the population
    /// manager and is not fine for anything called from inside
    /// <see cref="Tick"/>.
    /// </remarks>
    public bool IsBot(int slot) => _roster[slot].IsBot;

    /// <summary>
    /// Connections part-way through the handshake.
    /// </summary>
    /// <remarks>
    /// For an operator surface and for <c>tools/abuse/</c>. ⚠️ This is the
    /// number a connection flood moves and the roster does not — a flood that
    /// never completes a handshake leaves <c>PlayerCount</c> flat while this
    /// climbs to <c>ConnectionLimits.MaxJoiningConnections</c> and stops.
    /// </remarks>
    public int JoiningCount => _joining.Count;

    /// <summary>Whether this slot is a network client rather than a bot or slot 0.</summary>
    public bool IsConnected(int slot) => _roster[slot].Connection is { IsClosed: false };

    /// <summary>
    /// A connected player's address, or the empty string.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only an operator sees this.</b> It never reaches the game's wire —
    /// the reference does not put addresses on it and neither does this port —
    /// and the web panel shows it behind the same password as everything else.
    /// </remarks>
    public string AddressOf(int slot) =>
        _roster[slot].Connection?.RemoteAddress.ToString() ?? string.Empty;

    /// <summary>
    /// Closes a player's connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>An operator action with no client equivalent</b>, and the only one
    /// that ends a connection, so it does not go through <c>Execute</c>. The
    /// client is sent the reference's own <c>KICK</c> message with
    /// <see cref="KickReason.Operator"/> so it shows the player a kick rather
    /// than a dropped connection, and the flush happens BEFORE the close — the
    /// reference's plugin-packet
    /// kick has the same requirement, and without it the message is buffered,
    /// thrown away, and the player just sees a dropped connection.
    /// </para>
    /// <para>
    /// ⚠️ <b>A bot is refused rather than removed.</b> A bot has no connection to
    /// close, and the population manager would put it straight back on the next
    /// tick; the honest answer is that the bot count is the thing to change.
    /// </para>
    /// </remarks>
    public KickOutcome KickPlayer(int slot, string reason = "")
    {
        if (slot <= 0 || slot >= _roster.Count)
            return KickOutcome.NoSuchSlot;

        var connection = _roster[slot].Connection;

        if (connection is null || connection.IsClosed)
            return KickOutcome.NotAConnection;

        var writer = new NetWriter(2);
        ServerMessages.Kick(writer, KickReason.Operator);
        connection.Send(writer.WrittenSpan);
        connection.Flush();
        connection.Close(reason.Length == 0 ? "kicked by the operator" : reason);

        _log.Info(
            $"kicked {_roster[slot].Name} (slot {slot})"
            + (reason.Length == 0 ? string.Empty : $": {reason}"));

        return KickOutcome.Kicked;
    }

    /// <summary>
    /// The control port actually bound, or 0 when there is none.
    /// </summary>
    /// <remarks>
    /// ⚠️ Read back rather than assumed, the same as <see cref="BoundPort"/> —
    /// a test asks for port 0 and needs to learn which one it got. Hard-coding
    /// one makes the suite un-parallelisable and the failure intermittent.
    /// </remarks>
    public int ControlBoundPort => _control?.BoundPort ?? 0;

    /// <summary>The web admin's port, or 0 when there is none.</summary>
    /// <inheritdoc cref="ControlBoundPort" path="/remarks"/>
    public int WebBoundPort => _web?.BoundPort ?? 0;

    /// <summary>The password the web admin will accept, generated or configured.</summary>
    public string WebPassword => _web?.Password ?? string.Empty;

    /// <summary>
    /// How many state snapshots the panel has published.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A test hook, and it gates a claim rather than a behaviour</b>: the
    /// panel is meant to cost a server with nobody watching nothing per tick, and
    /// "only builds a snapshot when somebody is subscribed" is exactly the kind of
    /// sentence this repo has been wrong about before. WebAdminTests watches this
    /// number fail to move.
    /// </remarks>
    internal long WebSnapshotCount => _web?.SnapshotCount ?? 0;

    /// <summary>
    /// The admin password this server GENERATED, or null when one was configured
    /// or no panel is hosted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Exists for exactly one caller: the host's restart loop.</b> A
    /// generated password is printed to the console at startup and nowhere else —
    /// there is deliberately no endpoint that hands it back — so an operator who
    /// restarts from the panel in a browser on another machine would be locked out
    /// by the new one. <c>Program.Main</c> carries this into the next start.
    /// </para>
    /// <para>
    /// ⛔ <b>Null when the password was CONFIGURED</b>, so nothing can use this as
    /// a way to read an operator's own password back out of a running server.
    /// </para>
    /// </remarks>
    public string? GeneratedWebPassword => _web is { Generated: true } web ? web.Password : null;

    /// <summary>Whether the simulation is frozen.</summary>
    public bool IsFrozen => _frozen;

    /// <summary>
    /// Whether an operator has asked for a restart, so <see cref="Run"/> should
    /// return and the host should build a fresh server.
    /// </summary>
    /// <remarks>
    /// <b>Read by <c>Program.Main</c> after <see cref="Run"/> returns</b>, which is
    /// the one caller that can act on it. Every other caller drives ticks itself
    /// and never enters the loop.
    /// </remarks>
    public bool RestartRequested => _restartRequested;

    /// <summary>
    /// Ends <see cref="Run"/> and asks the host to start again from a re-read
    /// configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A RESTART, not a reload, and the difference is the point.</b> Every
    /// subsystem reads its options exactly once, at <see cref="Start"/>: the
    /// listener has bound its port, <c>BanList</c> has opened its file, the plugin
    /// list has been served to everyone connected, <c>WebAdminServer</c> has
    /// hashed its password. Pushing new options into a running server would apply
    /// some of them and not others, with nothing telling an operator which — so
    /// this tears the whole thing down and builds it again, which is the one
    /// operation whose result an operator can predict from the config file alone.
    /// </para>
    /// <para>
    /// ⚠️ <b>Every client is disconnected and the round is lost.</b> That is what
    /// a restart is; the panel says so before it asks. <see cref="Dispose"/> still
    /// runs, so the lobby listing comes down, an in-flight recording is closed
    /// into a readable trace, and every client gets a clean socket shutdown rather
    /// than a hang until TCP gives up.
    /// </para>
    /// <para>
    /// <b>It is not a kill.</b> The process survives, which is what makes this
    /// usable on a deployment with no supervisor — and on one WITH a supervisor it
    /// avoids the restart budget that would otherwise be spent on a deliberate
    /// action. <c>docs/DEPLOY.md</c>.
    /// </para>
    /// </remarks>
    public void RequestRestart()
    {
        _restartRequested = true;

        // Resumed first, or a server frozen from the panel would sit in the tick
        // loop's freeze branch and never reach the loop condition that reads the
        // flag. A restart is not something to have to un-freeze for.
        Resume();
    }

    /// <summary>Stops advancing the simulation. The network keeps running.</summary>
    public void Freeze()
    {
        _frozen = true;
        _stepsOwed = 0;
    }

    /// <summary>Resumes normal ticking.</summary>
    public void Resume()
    {
        _frozen = false;
        _stepsOwed = 0;
    }

    /// <summary>
    /// Advances the simulation <paramref name="ticks"/> times and stays frozen.
    /// </summary>
    /// <remarks>
    /// ⚠️ It <b>freezes first</b> rather than refusing on a running server. A
    /// step against a server that is still ticking would advance an unknowable
    /// number of times, which is the opposite of what stepping is for.
    /// </remarks>
    public void StepSimulation(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);

        _frozen = true;
        _stepsOwed = ticks;
    }

    /// <summary>
    /// Ends the round now and changes to <paramref name="map"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Down the same path a finished round takes</b> —
    /// <c>Rounds.BeginMapChange</c>, the ten-second countdown and a
    /// <c>MAP_END</c> — rather than swapping the map underneath everybody. A
    /// control port that changed maps by a route no round takes would produce a
    /// sequence no client has ever been sent, and the bug would look like a
    /// client bug.
    /// </remarks>
    public bool RequestMapChange(string map, int area = 1)
    {
        if (!MapExists(map) || _world.MapChanging)
            return false;

        // ⛔ The shared body, not a second copy — it also moves
        // _match.CurrentMapArea, and a joiner inside the countdown that is told
        // the wrong area dies on the other side of the change. See BeginMapChange.
        BeginMapChange(map, Math.Max(1, area));
        _log.Info($"control: {_nextMap} area {_nextArea} in ten seconds");

        return true;
    }

    /// <summary>
    /// Puts a bot back on a team, the way a client asks to join one.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Goes through <c>ExecuteChangeTeam</c> — the same body the socket
    /// switch and <see cref="Execute"/> reach.</b> CLAUDE.md's rule: a bot is an
    /// <see cref="IInputSource"/> like a socket, and a second copy here would let
    /// a bot take a team a client could not. That matters concretely — the
    /// reference's balance guard refuses a request for the bigger team, and this
    /// has to be refused by it too.
    /// <para>
    /// ⚠️ <b>It exists because a MAP CHANGE makes every player a spectator.</b>
    /// <c>Rounds.ResetForNewMap</c> does that on the first area, matching the
    /// reference's <c>with(Player) team = TEAM_SPECTATOR</c>, and a human re-picks
    /// from the team-select screen. A bot has nobody to pick for it, so
    /// <see cref="BotPopulationService"/> does — see the bug in its
    /// <c>Reteam</c>.
    /// </para>
    /// </remarks>
    public void RequestBotTeam(int slot, Team team)
    {
        if (slot <= 0 || slot >= _roster.Count || !_roster[slot].IsBot)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "not a bot slot");

        ExecuteChangeTeam(_roster[slot], team);
    }

    /// <summary>
    /// Ends the round now, with nobody winning, and rotates.
    /// </summary>
    /// <remarks>
    /// <c>global.winners = TEAM_SPECTATOR</c>, which is what the reference's
    /// <c>votenextround</c> plugin writes. ⚠️ Down the ordinary round-end path —
    /// <see cref="ServeRoundEnd"/> picks it up on the next tick and runs the
    /// same <c>MAP_END</c>, countdown and rotation a real win does. Refused
    /// while a map change is already in flight, so two votes cannot stack two
    /// countdowns.
    /// </remarks>
    public bool RequestRoundEnd()
    {
        if (_world.MapChanging || _world.RoundDecided)
            return false;

        Rounds.Decide(ref _world, Team.Spectator);
        _log.Info("round ended on request");
        return true;
    }

    /// <summary>
    /// Makes the next round's map <paramref name="map"/> instead of the
    /// rotation's pick. Returns false if there is no such map.
    /// </summary>
    public bool RequestNextMap(string map)
    {
        if (!MapExists(map))
            return false;

        _nextMapOverride = map;
        _log.Info($"next map overridden to {map}");
        return true;
    }

    private string? TakeNextMapOverride()
    {
        var map = _nextMapOverride;
        _nextMapOverride = null;

        // ⚠️ Re-checked at USE time, not only when it was set. A rotation can
        // outlive a --maps directory change, and a MAP_END naming a map the
        // server cannot then open would strand every client on a load screen.
        return map is not null && MapExists(map) ? map : null;
    }


    /// <summary>
    /// Moves a player onto <paramref name="team"/> whether or not they could
    /// have asked for it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The balance refusal is what this skips, and nothing else.</b> It
    /// goes through <see cref="ApplyChangeTeam"/> — the same death, class
    /// re-check and pair of broadcasts a client's own team change produces — so
    /// a scramble cannot put a player in a state a client command could not.
    /// </remarks>
    public void ForceTeam(int slot, Team team)
    {
        if (slot < 0 || slot >= _roster.Count || _roster[slot].Team == team)
            return;

        ApplyChangeTeam(_roster[slot], team);
    }

    /// <summary>Changes the bot fill target; the population manager converges on it.</summary>
    /// <remarks>
    /// ⚠️ A fill TARGET including humans, not a bot count — the reference's
    /// <c>FillToPlayers</c>. Setting it to 8 on a server with three people gives
    /// five bots.
    /// </remarks>
    public void SetBotFill(int fill)
    {
        _options.Bots = _options.Bots with
        {
            Enabled = fill > 0,
            FillToPlayers = fill,
            MaxBots = Math.Max(fill, _options.Bots.MaxBots),
        };

        // ⛔ An operator setting a bot count ENDS a player's suspension rather
        // than queueing behind it. Otherwise the panel's one bot control is a
        // field that accepts a number, reports it back, and changes nothing for
        // up to BotSuspendMinutes — the "button that does nothing" this file
        // already carries two warnings about, with the additional twist that the
        // operator has no way to see why.
        ResumeBots();
    }

    /// <summary>
    /// Ticks remaining on a <c>/bots off</c> suspension. Zero when none is
    /// running.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Ticks, not wall clock.</b> A vote's clock in both chat plugins is
    /// ticks and so is the round timer, so a suspension measured the same way
    /// stops and starts with <c>freeze</c>, <c>step</c> and <c>--speed</c>
    /// exactly as everything else a player can see does. On a live server at
    /// 30 Hz the two agree; on a frozen one a suspension that expired while the
    /// world was not moving would be a surprise.
    /// </remarks>
    public int BotSuspensionTicksLeft { get; private set; }

    /// <summary>Whether the bot population is currently held at zero by <c>/bots off</c>.</summary>
    public bool BotsSuspended => BotSuspensionTicksLeft > 0;

    /// <summary>Seconds remaining on a suspension, rounded up. Zero when none is running.</summary>
    public int BotSuspensionSecondsLeft => (BotSuspensionTicksLeft + GameConstants.TickRate - 1) / GameConstants.TickRate;

    /// <summary>
    /// Takes the bot population away for <paramref name="minutes"/>.
    /// </summary>
    /// <returns>False, and nothing changed, when the argument is not positive.</returns>
    /// <remarks>
    /// <b>It does not touch <see cref="ServerOptions.Bots"/>.</b> A suspension
    /// that turned the population manager off would be indistinguishable
    /// afterwards from an operator having done so, and the configuration the
    /// server goes back to would be whatever it was overwritten with. This is a
    /// clock, and <c>BotPopulationService.Service</c> reads it as a target of
    /// zero for as long as it runs.
    /// </remarks>
    public bool SuspendBots(int minutes)
    {
        if (minutes <= 0)
            return false;

        BotSuspensionTicksLeft = minutes * 60 * GameConstants.TickRate;
        return true;
    }

    /// <summary>Ends a suspension early.</summary>
    /// <returns>False when none was running.</returns>
    public bool ResumeBots()
    {
        if (BotSuspensionTicksLeft == 0)
            return false;

        BotSuspensionTicksLeft = 0;
        _log.Info("bots: suspension ended, the population manager is back in charge");
        return true;
    }

    /// <summary>
    /// Runs the suspension clock down by one tick and says so when it expires.
    /// </summary>
    /// <remarks>
    /// Called once a tick, immediately above <c>BotPopulationService.Service</c>,
    /// so the tick a suspension expires on is the tick the bots start coming
    /// back rather than the one after it.
    /// </remarks>
    private void TickBotSuspension()
    {
        if (BotSuspensionTicksLeft == 0)
            return;

        BotSuspensionTicksLeft--;

        if (BotSuspensionTicksLeft == 0)
            _log.Info("bots: suspension expired, the population manager is back in charge");
    }

    /// <summary>Sets the tick pacing. Infinity is unpaced.</summary>
    public void SetSpeed(double multiplier)
    {
        if (double.IsNaN(multiplier) || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "speed must be > 0");

        _options.SpeedMultiplier = multiplier;
    }

    /// <summary>Moves a live character, leaving its velocity alone.</summary>
    /// <remarks>
    /// ⚠️ <b>Not something a client can do</b>, which is why it is here and not
    /// a <see cref="PlayerCommand"/>. It writes the position the same way a
    /// spawn does and does not check that the destination is free: putting a
    /// character inside a wall is a legitimate thing to want from a debugging
    /// tool, and the movement solver's stuck case is one of the things worth
    /// being able to reproduce on purpose.
    /// </remarks>
    public bool Teleport(int slot, double x, double y)
    {
        if (slot < 0 || slot >= _roster.Count || !_world.IsAlive(slot))
            return false;

        _world.Characters[slot].X = x;
        _world.Characters[slot].Y = y;
        _world.Weapons[slot].X = x;
        _world.Weapons[slot].Y = y;

        return true;
    }

    /// <summary>
    /// Runs a <see cref="PlayerCommand"/> for a slot on the operator's behalf.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the seam that keeps the control port honest.</b> It hands
    /// the command to the same <c>Execute</c> the socket switch and the bots
    /// reach, so the port cannot do anything a client could not, and a command
    /// added later is reachable from all three without being written three
    /// times. Preconditions that fail are silence, as everywhere else.
    /// </remarks>
    public bool ExecuteFor(int slot, in PlayerCommand command)
    {
        if (slot < 0 || slot >= _roster.Count)
            return false;

        Execute(_roster[slot], command);
        return true;
    }

    // --- bans ----------------------------------------------------------------

    /// <summary>
    /// Bans an address or block, and disconnects anybody already on from it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The kick is not optional and is the whole reason this is a server
    /// method rather than a call straight into <see cref="BanList"/>.</b> A ban
    /// that only refuses future connections leaves the person it was aimed at
    /// sitting on the server until they choose to leave, which is exactly not
    /// what an operator pressing it means.
    /// <para>
    /// ⛔ An address is not a person — <see cref="BanList"/> says why this is a
    /// moderation tool and not a security control, and why nothing calls it
    /// automatically.
    /// </para>
    /// </remarks>
    /// <returns>How many connected players were disconnected by it.</returns>
    public int BanAddress(IpRange range, TimeSpan? duration, string reason)
    {
        if (range.IsEmpty)
            throw new ArgumentException("not an address or CIDR block", nameof(range));

        var entry = _bans.Add(range, duration, reason, DateTime.UtcNow);
        var kicked = 0;

        // ⚠️ Backwards, because KickPlayer removes the roster entry and compacts
        // every index above it — walking forwards would skip the player after
        // each one it disconnected, which on a banned /24 is half of them.
        for (var slot = _roster.Count - 1; slot > 0; slot--)
        {
            if (_roster[slot].Connection is not { } connection
                || !range.Contains(connection.RemoteAddress))
            {
                continue;
            }

            if (KickPlayer(slot, "banned") == KickOutcome.Kicked)
                kicked++;
        }

        _log.Warn(
            $"banned {range} {(entry.ExpiresUtc is null ? "permanently" : $"until {entry.ExpiresUtc:u}")}"
            + $" ({LogText.Safe(entry.Reason)}); {kicked} player(s) disconnected");

        // ⚠️ Said here rather than at startup, because at startup nobody has
        // asked for a ban yet and a warning about an unused feature is noise.
        // An operator finding out that bans evaporate by having the same person
        // come back after a restart is the failure this one line prevents.
        if (!_bans.IsPersistent)
        {
            _log.Warn(
                "this ban is in memory only and will be lost on restart; "
                + "set --ban-file to keep it");
        }

        return kicked;
    }

    /// <summary>Lifts a ban on exactly this range. False if there was none.</summary>
    public bool UnbanAddress(IpRange range)
    {
        if (!_bans.Remove(range))
            return false;

        _log.Info($"unbanned {range}");
        return true;
    }

}
