namespace Gg2.Server;

/// <summary>
/// Finds how much controller work THIS machine can afford in a tick, by watching
/// how close its ticks come to their deadline.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Why this exists: the cost model is only correct up to a constant
/// factor.</b> <c>SearchController</c>'s reference cost was measured on one
/// machine, on one map, in Release. A rented VPS with slower cores makes every
/// decision cost more, and no constant compiled into this repo can know that.
/// But <c>DecisionQuotaArbiter</c> never uses the absolute scale — it only ever
/// asks <c>cost &lt;= budget - spent</c> — so the fix is not a better constant,
/// it is a budget that calibrates itself to whatever the constants happen to mean
/// where they are running.
/// </para>
/// <para>
/// <b>AIMD</b>, the same shape as TCP congestion control, and chosen for the same
/// reason: it needs no model of the resource. Measuring spare microseconds and
/// converting would require the units to be true, which is precisely the
/// assumption being routed around. This treats the budget as an abstract dial and
/// finds the setting the machine tolerates — cut it hard on trouble, creep it up
/// when there is room.
/// </para>
/// <para>
/// ⛔ <b>DETERMINISM: the budget may only change BETWEEN ticks.</b> Within a tick
/// <c>Choose</c> stays a pure function of the tick, the poll order, the costs and
/// the budget, so a match still replays given the same budget sequence.
/// <c>GameServer.Run</c> is therefore the only caller — <c>HeadlessMatch</c>, the
/// env and <c>eval</c> never reach it and keep a fixed budget, which is what
/// keeps <c>RolloutThreadingTests</c> and
/// <c>EnvBatchTests.ABotDrivenBatchIsTheSameHoweverManyThreads</c> meaning what
/// they say. ⚠️ <b>Do not call this from inside a tick</b>, however convenient the
/// signal is there.
/// </para>
/// <para>
/// ⚠️ <b>It makes bots quietly worse under load, which is the point and is also
/// the risk.</b> A benchmark on a busy machine would silently measure a different
/// bot. That is why it is off everywhere a measurement is taken, and why every
/// move it makes is logged rather than absorbed.
/// </para>
/// <para>
/// ⚠️ <b>Two more untuned numbers, on top of a budget that was already a stated
/// guess.</b> <see cref="HighWater"/> and <see cref="Decrease"/> are chosen for
/// stability rather than measured. What would show them wrong is below.
/// </para>
/// </remarks>
public sealed class MicroBudgetController
{
    /// <summary>
    /// Fraction of the interval a tick may use before the budget is cut: 80%.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Reacting to NEAR-lateness, not lateness.</b> By the time a tick is
    /// actually late the frame is gone and a client has already dead-reckoned
    /// somewhere it will be corrected from. 80% leaves a fifth of the tick as the
    /// margin the controller defends.
    /// </remarks>
    public const double HighWater = 0.80;

    /// <summary>
    /// Fraction below which a whole window counts as comfortable: 50%.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Well below <see cref="HighWater"/>, deliberately.</b> Adjacent
    /// thresholds make the budget flap between two values, spending half its time
    /// in each and never settling — the gap is the hysteresis.
    /// </remarks>
    public const double LowWater = 0.50;

    /// <summary>Multiplier applied on a cut: halve.</summary>
    /// <remarks>
    /// ⛔ <b>Multiplicative down, additive up</b>, which is what makes AIMD
    /// converge instead of oscillate. Backing off gently would take many windows
    /// to escape an overload that is hurting players now.
    /// </remarks>
    public const double Decrease = 0.5;

    /// <summary>
    /// The floor a server walks down to: one policy decision's worth.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not zero.</b> Zero starves the CHEAP tier too, and the whole point of
    /// tiering is that it survives the expensive one being cut — a machine that
    /// cannot afford a search can still afford a 7 µs forward pass. At this floor
    /// a tiered server has become a policy server, which is a legitimate answer
    /// and is reported when it happens.
    /// </remarks>
    public const int PolicyFloorUnits = 7;

    /// <summary>One additive increase: a tenth of the default tiered budget.</summary>
    /// <remarks>
    /// ⚠️ Small enough that climbing back is gradual — the whole risk of AIMD is
    /// an increase big enough to re-trigger the cut it just recovered from.
    /// </remarks>
    public const int DefaultStepUnits = 1_000;

    private readonly int _ceiling;
    private readonly int _floor;
    private readonly int _step;
    private readonly int _windowTicks;

    private long _windowEndsAt;
    private double _windowPeak;
    private bool _cutThisWindow;

    /// <param name="ceiling">
    /// The configured budget — <c>--bot-arbiter</c>. ⚠️ <b>A ceiling, never a
    /// target</b>: this only ever holds the budget at or below what was asked for,
    /// so the flag stays an upper bound the operator set.
    /// </param>
    /// <param name="floor">
    /// The smallest budget worth holding. Below one decision's cost the expensive
    /// tier never runs at all — a legitimate answer on a small machine, and one
    /// that must be reported rather than reached silently.
    /// </param>
    /// <param name="step">How much one additive increase adds.</param>
    /// <param name="windowTicks">Ticks between adjustments.</param>
    public MicroBudgetController(int ceiling, int floor, int step, int windowTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);
        ArgumentOutOfRangeException.ThrowIfNegative(floor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowTicks);

        _ceiling = ceiling;
        _floor = Math.Min(floor, ceiling);
        _step = step;
        _windowTicks = windowTicks;

        Units = ceiling;
    }

    /// <summary>The budget right now. Starts at the ceiling and is walked down.</summary>
    /// <remarks>
    /// ⚠️ <b>Starts optimistic on purpose.</b> Beginning at the floor and climbing
    /// would give every server a worse first minute than it needed, including the
    /// ones that could always have afforded the ceiling.
    /// </remarks>
    public int Units { get; private set; }

    /// <summary>Whether the budget has been moved off the configured ceiling.</summary>
    public bool IsThrottled => Units < _ceiling;

    /// <summary>
    /// One tick's pacing outcome. Returns a line to log when the budget moved.
    /// </summary>
    /// <param name="tick">The server frame. Windows are counted in ticks, not seconds.</param>
    /// <param name="usedMs">Wall-clock milliseconds this tick's work took.</param>
    /// <param name="intervalMs">
    /// The tick's budget. ⚠️ <b>Zero or less means unpaced</b> — there is no
    /// deadline to defend and no pressure to read, so nothing moves.
    /// </param>
    /// <param name="controllerUnits">
    /// What the arbiter granted on this tick, for the log line only.
    /// ⚠️ <b>It does NOT gate the cut, and the difference matters.</b> This
    /// responds to total tick pressure rather than to controller-attributable
    /// pressure, so it will throttle bots for slowness that is not theirs — a slow
    /// map load, a Debug build, a noisy neighbour. That is the safe direction (if
    /// the tick is at risk, cutting optional work is right whatever the cause) but
    /// it is surprising from outside, so the line says what the controllers
    /// actually spent and lets an operator see when a cut was aimed at the wrong
    /// thing.
    /// </param>
    public string? Observe(long tick, double usedMs, double intervalMs, int controllerUnits = 0)
    {
        if (intervalMs <= 0)
            return null;

        // ⛔ The window is rolled FIRST, before any cut is considered. It used to
        // be the other way round and that was a real bug: the cut branch returned
        // early without ever opening a window, so the next tick found an expired
        // one, closed it, re-armed the once-per-window guard, and let a second cut
        // land two ticks after the first. A burst walked the budget down twice as
        // fast as intended. ABurstCutsOnceNotOnEveryTick is the gate.
        var rolled = tick >= _windowEndsAt ? Roll(tick) : null;

        var pressure = usedMs / intervalMs;

        if (pressure > _windowPeak)
            _windowPeak = pressure;

        if (pressure > HighWater && !_cutThisWindow && Units > _floor)
        {
            _cutThisWindow = true;

            var was = Units;
            Units = Math.Max(_floor, (int)(Units * Decrease));

            // ⚠️ The cut wins the line when both happened this tick: a budget
            // going down is the operator's problem and one going up is not.
            return $"bot controller budget {was} -> {Units} units: tick {tick} used "
                + $"{pressure * 100:0}% of its {intervalMs:0.0}ms budget, of which controllers "
                + $"spent {controllerUnits}us"
                + (Units == _floor ? " (at the floor: the expensive tier is effectively off)" : string.Empty);
        }

        return rolled;
    }

    /// <summary>
    /// Closes the window that just ended, raising the budget if it was quiet, and
    /// opens the next one.
    /// </summary>
    private string? Roll(long tick)
    {
        var peak = _windowPeak;
        var cut = _cutThisWindow;
        var window = _windowEndsAt;

        _windowEndsAt = tick + _windowTicks;
        _windowPeak = 0;
        _cutThisWindow = false;

        // ⚠️ No increase in a window that had to cut, whatever the peak says: the
        // peak is what the budget was cut FOR, and raising it back in the same
        // breath is the oscillation this is shaped to avoid.
        //
        // ⚠️ And none on the very first call, where there was no window to
        // observe — window 0 is the constructor's, not a quiet one anybody saw.
        if (window == 0 || cut || peak >= LowWater || Units >= _ceiling)
            return null;

        var previous = Units;
        Units = Math.Min(_ceiling, Units + _step);

        return $"bot controller budget {previous} -> {Units} units: "
            + $"{_windowTicks} ticks with headroom (peak {peak * 100:0}%)";
    }
}
