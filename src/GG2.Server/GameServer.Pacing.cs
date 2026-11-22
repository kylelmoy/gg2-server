using System.Diagnostics;
using GG2.Protocol;
using GG2.Bots;

namespace GG2.Server;

public sealed partial class GameServer
{
    /// <summary>
    /// Nobody is connected, nobody is joining, and no bots are running — so
    /// nothing that happens this tick can be observed by anyone.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Joining connections count.</b> A client part-way through the
    /// handshake has no roster entry yet, and a server that ignored them would
    /// service the rest of that handshake at the idle rate — several round trips
    /// at half a second each.
    /// <para>
    /// ⚠️ <b>Bots count too.</b> A server running bots against each other with
    /// nobody watching is still simulating, and <c>--record</c> or the control
    /// port may well be the reason. Idling it would silently change what those
    /// recorded.
    /// </para>
    /// </remarks>
    public bool IsIdle
    {
        get
        {
            if (_clients.Count != 0 || _joining.Count != 0)
                return false;

            for (var i = 0; i < _roster.Count; i++)
            {
                if (_roster[i].IsBot)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The interval <see cref="Run"/> should pace to right now — the idle rate
    /// when there is nobody to serve, otherwise <see cref="TickInterval"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>An unpaced server is never idled.</b> <c>--speed max</c> is a direct
    /// instruction about the tick rate; quietly overriding it with a 2 Hz idle
    /// would make the flag look broken on exactly the configuration that asked
    /// for it loudest.
    /// </remarks>
    /// <summary>
    /// <see cref="PacingInterval"/>, for a test. Not part of the server's own
    /// surface — idling is otherwise only observable by timing <see cref="Run"/>,
    /// and a test that did that would be measuring the machine.
    /// </summary>
    internal TimeSpan PacingIntervalForTest() => PacingInterval();

    private TimeSpan PacingInterval()
    {
        var live = TickInterval;

        if (!_options.IdleWhenEmpty || live <= TimeSpan.Zero || !IsIdle)
            return live;

        var idle = TimeSpan.FromSeconds(1.0 / ServerOptions.IdleTickRate);

        // ⚠️ Never FASTER than the live rate. A --speed below 1 already ticks
        // more slowly than the idle rate, and "idling" it up to 2 Hz would be a
        // speed-up nobody asked for.
        return idle > live ? idle : live;
    }

    /// <summary>
    /// The wall-clock length of one tick at the configured speed, or
    /// <see cref="TimeSpan.Zero"/> when the server is unpaced.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller can tell a paced server from an unpaced one without
    /// re-deriving the arithmetic — and so the zero case has one definition
    /// rather than two.
    /// </remarks>
    public TimeSpan TickInterval =>
        double.IsPositiveInfinity(_options.SpeedMultiplier)
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1.0 / (GameConstants.TickRate * _options.SpeedMultiplier));

    /// <summary>
    /// Runs until <paramref name="cancellation"/> is signalled, pacing to 30 Hz
    /// times <see cref="ServerOptions.SpeedMultiplier"/> — or as fast as the
    /// machine allows when that is infinite.
    /// </summary>
    public void Run(CancellationToken cancellation)
    {
        // ⚠️ Windows rounds a wait up to its timer quantum — about 15.6ms by
        // default — and the pacing below waits. Measured on an idle server
        // without this: 15% of ticks arrived about 14ms late, nearly half a
        // tick, in a distribution with nothing between 34ms and 45ms. With it,
        // none. The stock client re-simulates between position updates and
        // corrects when one lands, so irregular ticks reach a player as
        // rubberbanding. See TimerResolution for the numbers.
        using var resolution = TimerResolution.Raise();

        var clock = Stopwatch.StartNew();
        var nextTick = TimeSpan.Zero;
        var pacing = TickInterval;

        // ⚠️ Measured with clients on it, not idle. The 15% figure above came
        // from an empty server, which is the clean measurement but not the one
        // a player experienced; --tick-stats is here so the same distribution
        // can be read back out of a real game.
        var stats = _options.TickStats ? new TickPacing(TickInterval.TotalMilliseconds) : null;
        var lastStart = clock.Elapsed;

        // ⚠️ ALWAYS ON, unlike --tick-stats. A healthy server prints nothing, so
        // there is no cost to leaving it armed, and a tick that overruns its
        // budget is the one thing an operator cannot see from inside the game:
        // the symptom is a client rubberbanding, which looks like the player's
        // own network. LateTickMonitor rate-limits itself; see its remarks.
        var late = new LateTickMonitor();

        // ⚠️ The restart flag is checked HERE, beside the cancellation token,
        // rather than by cancelling that token: the token is the HOST's — a
        // Ctrl-C or a SIGTERM — and cancelling it would make an operator's
        // restart indistinguishable from a shutdown, which is exactly the
        // distinction Program.Main has to make when this returns.
        while (!cancellation.IsCancellationRequested && !_restartRequested && _selfExit == SelfExitReason.None)
        {
            if (stats is not null)
            {
                var start = clock.Elapsed;

                // The first interval is measured from the loop's start rather
                // than skipped, so the count matches the tick count.
                stats.Record((start - lastStart).TotalMilliseconds);
                lastStart = start;

                // Every 30 seconds of play, so a hitch can be placed in time
                // against what the players were doing rather than only summed
                // at the end — and so a crash still leaves the evidence.
                if (stats.Count % (GameConstants.TickRate * 30) == 0)
                    _log.Info(stats.Report());
            }

            // ⚠️ The tick's OWN work, measured ONCE and read by two
            // diagnostics: the late warning's verdict below, and the panel's
            // "tick ms" tile. Both branches timed this interval separately when
            // they were merged; `clock` is a Stopwatch too, so the second pair of
            // reads was measuring the same thing with the same source.
            // ⚠️ Two timestamp reads a tick, unconditionally. Against a 33,300us
            // budget the pair costs tens of nanoseconds, and the alternative —
            // arming it only when --web is on — makes the paced loop two
            // different loops. Without it the late diagnostic cannot tell "this
            // server is too slow" from "this machine is busy" — see
            // LateTickContext.
            var workStart = Stopwatch.GetTimestamp();

            // ⚠️ THE LAST NET, and before this there was none: an exception
            // anywhere inside a tick reached Main and killed the process. On a
            // server nobody is sitting in front of, that is the difference
            // between a bad minute and an outage until somebody notices.
            //
            // ⛔ It is a net, not a fix. The stages below it guard themselves
            // where the blast radius can be made smaller — ServiceClients drops
            // the one client whose bytes caused a fault, which is the containment
            // that actually preserves the game for everybody else. Reaching HERE
            // means the fault was not attributable to one connection, so the
            // simulation may be part-way through a stage and this tick's
            // broadcast may be half written.
            //
            // Which is why it does not simply swallow. TickFaultBudget says what
            // "carrying on" is allowed to mean: absorb a fault, refuse to absorb
            // a pattern. A server that catches everything for ever is up, listed
            // in the lobby, accepting players and simulating nothing.
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                _log.Error($"tick {_frame} failed", e);

                if (!_faults.RecordAndContinue(_nowMs))
                {
                    _log.Error(
                        $"{TickFaultBudget.Allowance} ticks failed within "
                        + $"{TickFaultBudget.Window.TotalSeconds:0}s ({_faults.Total} in total); stopping. "
                        + "A supervisor with Restart=on-failure will bring back a clean process — "
                        + "see docs/HARDENING.md.");

                    throw new TickFaultException(
                        $"the server stopped after {_faults.Total} failed tick(s); the last one is the inner exception",
                        e);
                }
            }

            var workMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;

            // ⛔ NOT a benchmark number: an EMA over whatever the server was
            // doing, map loads included. tools/bench/ is where a per-tick cost
            // comes from.
            _tickWorkMs = (_tickWorkMs * 15 + workMs) / 16;
            _tickWorkSumMs += workMs;
            _tickWorkPeakMs = Math.Max(_tickWorkPeakMs, workMs);
            _tickWorkCount++;

            // ⚠️ Re-read every tick rather than captured before the loop: the
            // control port can change the speed on a running server, and a
            // captured interval would make --speed a startup-only setting with
            // no sign that a later change did nothing.
            //
            // ⚠️ PacingInterval, not TickInterval — it drops to the idle rate
            // when nobody is connected. The rebase below already handles an
            // interval that changes underneath the loop, which is why idling
            // needed no other change to the pacing: entering and leaving idle is
            // the same event as a --speed change.
            var tickLength = PacingInterval();

            // ⚠️ Unpaced returns to the top BEFORE the pacing arithmetic, and
            // that is not tidiness. The backlog branch below tests
            // `wait < -tickLength * 10`, which at a zero tickLength reads
            // `wait < 0` — true on every tick after the first — so falling
            // through would log "dropped 0ms of tick backlog" forever, at
            // unpaced speed. There is no debt to shed when there is no clock to
            // be behind. Rebasing on the way past keeps a later switch back to a
            // paced speed from thinking it is hours late.
            if (tickLength <= TimeSpan.Zero)
            {
                nextTick = clock.Elapsed;
                continue;
            }

            // A speed change rebases too: debt accumulated in the old interval
            // means nothing in the new one, and carrying it would make a server
            // slowed from max sprint until it had "caught up".
            if (tickLength != pacing)
            {
                pacing = tickLength;
                nextTick = clock.Elapsed;
            }

            nextTick += tickLength;
            var wait = nextTick - clock.Elapsed;

            // ⚠️ Below the unpaced `continue` above, so `tickLength` is real
            // here. A negative wait is the tick's work having overrun its own
            // budget, which is the only definition of "late" that does not need
            // a second clock.
            // ⚠️ The count is incremented OUTSIDE the `&&` the warning used to
            // hang off. LateTickMonitor rate-limits itself — it returns null for
            // most late ticks so the log is not a flood — so counting only when
            // it produced a line would have under-reported by exactly the amount
            // the rate limiter suppressed, which on a struggling server is nearly
            // all of them.
            var overrunMs = -wait.TotalMilliseconds;

            if (LateTickMonitor.IsLate(overrunMs, tickLength.TotalMilliseconds))
            {
                _lateTicks++;
                _worstOverrunMs = Math.Max(_worstOverrunMs, overrunMs);

                if (late.Record(_frame, overrunMs, tickLength.TotalMilliseconds, LateContext(workMs))
                    is { } warning)
                {
                    _log.Warn(warning);
                }
            }

            // ⚠️ Warn, matching the individual lines above. It used to be Info,
            // so the per-tick warnings went to stderr and the SUMMARY of the
            // ones they suppressed went to stdout — an operator filtering on
            // stderr saw the samples and not the count, which is the half that
            // says how bad it is.
            if (late.Poll(_frame) is { } summary)
                _log.Warn(summary);

            if (wait > TimeSpan.Zero)
            {
                cancellation.WaitHandle.WaitOne(wait);
            }
            else if (wait < -tickLength * 10)
            {
                // Far enough behind that catching up tick-by-tick would only dig
                // deeper. Give up the debt rather than spiral.
                _log.Warn($"dropped {-wait.TotalMilliseconds:0}ms of tick backlog");
                nextTick = clock.Elapsed;
            }
        }

        if (late.Flush() is { } pending)
            _log.Warn(pending);

        if (stats is not null)
            _log.Info(stats.Report());
    }

    /// <summary>
    /// What the server was doing on a tick that ran late.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Built only when a tick is actually late</b> — it walks the roster,
    /// and doing that every tick to feed a line that is almost never printed
    /// would be paying the diagnostic's cost on the healthy path.
    /// </remarks>
    private LateTickContext LateContext(double workMilliseconds)
    {
        var bots = 0;
        var characters = 0;

        for (var i = 0; i < _roster.Count; i++)
        {
            if (_roster[i].IsBot)
                bots++;

            if (_world.IsAlive(i))
                characters++;
        }

        return new LateTickContext(
            Clients: _clients.Count,
            Bots: bots,
            Characters: characters,
            Map: _currentMap,
            WorkMilliseconds: workMilliseconds);
    }
}
