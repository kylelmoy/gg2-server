using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

/// <summary>
/// Per-player aim statistics that separate a person's mouse from a program's:
/// how often the crosshair sits exactly on a moving enemy, lands exactly on one
/// out of a flick, fires on the very tick it first touches one, or follows one
/// the player cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here reaches the wire or the simulation.</b> It reads the world and
/// the inputs the tick is about to consume, the same instant
/// <c>Replayer.OnTick</c> exposes, so the live server and <c>tools/aimscan</c>
/// over a recording compute the same numbers.
/// </para>
/// <para>
/// ⚠️ <b>Every statistic is kept at every lag from 0 to <see cref="MaxLag"/>.</b>
/// A client aims at the world it has been sent, which is the server's from a
/// round trip ago, so an aimbot's crosshair sits exactly on where the enemy WAS.
/// Measured against the current positions a perfect lock reads as a miss of
/// velocity times latency. The scores take the best lag per player, which is an
/// estimate of that player's round trip in ticks.
/// </para>
/// <para>
/// <b>What "exact" means.</b> A miss is the perpendicular distance in pixels
/// from the target's origin (its chest, where every aimbot aims) to the aim
/// ray. The pointer is a whole pixel (<see cref="AimCheck"/>), so even a perfect
/// pointer-moving aimbot misses by up to about 0.7 px; <see cref="TightPixels"/>
/// is set above that. A person tracking a moving target misses by several pixels
/// on almost every tick. <c>docs/HARDENING.md</c>, "Aim analysis", has the
/// distributions measured over the fleet's recordings and where each threshold
/// came from.
/// </para>
/// </remarks>
public sealed class AimAnalysis
{
    /// <summary>The largest round trip, in ticks, the statistics are kept for (400 ms).</summary>
    public const int MaxLag = 12;

    /// <summary>The miss, in pixels, below which a tick counts as locked on.</summary>
    public const double TightPixels = 1.0;

    /// <summary>A one-tick aim change at least this large is a flick.</summary>
    public const double SnapDegrees = 20;

    /// <summary>
    /// A target whose bearing from the shooter turns less than this per tick is
    /// standing still relative to them, and a person can rest a pointer on it.
    /// Only moving targets count towards a lock.
    /// </summary>
    public const double MovingDegrees = 0.25;

    private static readonly double SinMoving = Math.Sin(MovingDegrees * Math.PI / 180);

    /// <summary>The union of every aspect ratio's half view (<c>rooms_fix_views.gml</c>): 888 wide, 624 tall.</summary>
    private const double ViewHalfWidth = 444;

    private const double ViewHalfHeight = 312;

    /// <summary>Beyond this an enemy is not counted at all, seen or hidden.</summary>
    private const double Reach = 1200;

    private const int History = 16;

    private readonly Body[] _history = new Body[History * WorldState.MaxSlots];

    /// <summary>
    /// Per row and watched slot, a bit per slot that was hidden from it then.
    /// Computed once, for the newest row, because a wall test is the one costly
    /// thing here and every lag would otherwise repeat it.
    /// </summary>
    private readonly ulong[] _hidden = new ulong[History * WorldState.MaxSlots];

    /// <summary>This tick's wall tests, two bits per slot pair: tested, and blocked.</summary>
    private readonly ulong[] _walled = new ulong[WorldState.MaxSlots * 2];
    private readonly Counters[] _counters = new Counters[WorldState.MaxSlots * (MaxLag + 1)];
    private readonly Previous[] _previous = new Previous[WorldState.MaxSlots];
    private readonly AimSignals[] _reported = new AimSignals[WorldState.MaxSlots];
    private int _tick;

    /// <summary>The counters for one player at one lag.</summary>
    public ref readonly Counters At(int slot, int lag) => ref _counters[slot * (MaxLag + 1) + lag];

    /// <summary>Forgets a slot, for a player leaving or a slot being reused.</summary>
    public void Reset(int slot)
    {
        _counters.AsSpan(slot * (MaxLag + 1), MaxLag + 1).Clear();
        _previous[slot] = default;
        _reported[slot] = AimSignals.None;
    }

    /// <summary>
    /// <see cref="WorldState.RemoveSlot"/>'s compaction: every slot above
    /// <paramref name="slot"/> moves down one, and the departed player's
    /// statistics go with them.
    /// </summary>
    public void Remove(int slot)
    {
        for (var s = slot; s < WorldState.MaxSlots - 1; s++)
            Move(s + 1, s);

        Reset(WorldState.MaxSlots - 1);
    }

    /// <summary>
    /// Moves every statistic of slot <paramref name="from"/> to <paramref name="to"/>,
    /// for the roster compaction a departure causes.
    /// </summary>
    public void Move(int from, int to)
    {
        _counters.AsSpan(from * (MaxLag + 1), MaxLag + 1).CopyTo(_counters.AsSpan(to * (MaxLag + 1), MaxLag + 1));
        _reported[to] = _reported[from];

        // The history names slots, by index and in the hidden masks, and the
        // indices just moved. It refills in History ticks.
        Array.Clear(_history);
        Array.Clear(_hidden);
        Array.Clear(_previous);
    }

    /// <summary>
    /// Takes one tick: records where everybody is, then scores each watched
    /// player's input against the positions of every lag.
    /// </summary>
    /// <param name="inputs">The input each slot is about to apply this tick.</param>
    /// <param name="watched">Which slots to score; a bot is not worth the time.</param>
    public void Observe(
        in WorldState world, CollisionWorld collision,
        ReadOnlySpan<PlayerInput> inputs, ReadOnlySpan<bool> watched)
    {
        var row = (_tick % History) * WorldState.MaxSlots;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!world.Alive[slot])
            {
                _history[row + slot] = default;
                continue;
            }

            ref readonly var c = ref world.Characters[slot];
            _history[row + slot] = new Body
            {
                Alive = true,
                X = c.X,
                Y = c.Y,
                Team = c.Team,
                Class = c.Class,
                Hidden = c.Cloak && c.CloakAlpha < 0.1 && !c.Stabbing,
                Zoomed = c.Zoomed,
            };
        }

        // A wall blocks sight both ways, so each pair is tested once: the walls
        // are what costs here, and halving them halves the analysis.
        Array.Clear(_walled);

        for (var slot = 0; slot < WorldState.MaxSlots && slot < watched.Length; slot++)
        {
            ref readonly var self = ref _history[row + slot];
            var mask = 0UL;

            if (watched[slot] && self.Alive)
            {
                for (var other = 0; other < WorldState.MaxSlots; other++)
                {
                    ref readonly var target = ref _history[row + other];

                    if (other == slot || !target.Alive || target.Team == self.Team)
                        continue;

                    var dx = target.X - self.X;
                    var dy = target.Y - self.Y;

                    if (dx * dx + dy * dy > Reach * Reach)
                        continue;

                    var outOfView = !self.Zoomed
                        && (Math.Abs(dx) > ViewHalfWidth || Math.Abs(dy) > ViewHalfHeight);

                    if (outOfView || target.Hidden || Walled(world, collision, row, slot, other))
                        mask |= 1UL << other;
                }
            }

            _hidden[row + slot] = mask;
        }

        var lags = Math.Min(_tick, MaxLag);

        for (var slot = 0; slot < WorldState.MaxSlots && slot < inputs.Length && slot < watched.Length; slot++)
        {
            if (!watched[slot])
                continue;

            if (!world.Alive[slot])
            {
                _previous[slot] = default;
                continue;
            }

            var input = inputs[slot];
            ref var previous = ref _previous[slot];

            var aim = input.AimDirectionDegrees;
            var flick = previous.Seen && Math.Abs(Delta(aim, previous.Aim)) >= SnapDegrees;
            var pressed = previous.Seen
                && input.Attack
                && (previous.Keys & PlayerKeys.Attack) == 0;

            var ux = Math.Cos(aim * Math.PI / 180);
            var uy = -Math.Sin(aim * Math.PI / 180);

            for (var lag = 0; lag <= lags; lag++)
                Score(slot, lag, ux, uy, flick, pressed, previous);

            previous = new Previous { Seen = true, Aim = aim, Keys = input.Keys };
        }

        _tick++;
    }

    private void Score(
        int slot, int lag,
        double ux, double uy, bool flick, bool pressed, in Previous previous)
    {
        var now = Row(lag);
        var before = Row(lag + 1);

        ref readonly var self = ref _history[now + slot];

        if (!self.Alive)
            return;

        ref var counters = ref _counters[slot * (MaxLag + 1) + lag];

        var visibleMiss = double.PositiveInfinity;
        var hiddenMiss = double.PositiveInfinity;
        var anyVisible = false;
        var anyHiddenMoving = false;
        var lockedMoving = false;
        var onTarget = false;
        var wasOnTarget = false;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            ref readonly var target = ref _history[now + other];

            if (other == slot || !target.Alive || target.Team == self.Team)
                continue;

            var dx = target.X - self.X;
            var dy = target.Y - self.Y;

            if (dx * dx + dy * dy > Reach * Reach)
                continue;

            var hidden = (_hidden[now + slot] & (1UL << other)) != 0;

            var miss = Miss(dx, dy, ux, uy);
            var moving = Moving(before, slot, other, dx, dy);

            if (hidden)
            {
                if (moving)
                {
                    anyHiddenMoving = true;
                    hiddenMiss = Math.Min(hiddenMiss, miss);
                }

                continue;
            }

            anyVisible = true;

            if (miss < visibleMiss)
            {
                visibleMiss = miss;
                lockedMoving = moving;
            }

            if (pressed)
            {
                onTarget |= RayHitsBox(dx, dy, ux, uy, target.Class);
                wasOnTarget |= WasOnTarget(before, slot, other, previous.Aim);
            }
        }

        if (anyVisible && lockedMoving)
        {
            counters.Tracking++;
            if (visibleMiss < TightPixels)
                counters.Locked++;
        }

        if (anyVisible && flick)
        {
            counters.Flicks++;
            if (visibleMiss < TightPixels)
                counters.FlickLocks++;
        }

        if (anyVisible && pressed)
        {
            counters.Presses++;
            if (onTarget && !wasOnTarget)
                counters.OnsetPresses++;
        }

        // A hidden lock only counts when no visible enemy is under the
        // crosshair too: aiming at somebody you can see who happens to stand in
        // front of somebody you cannot is not evidence of anything.
        if (anyHiddenMoving)
        {
            counters.HiddenTracking++;
            if (hiddenMiss < TightPixels && !(visibleMiss < 4 * TightPixels))
                counters.HiddenLocked++;
        }
    }

    private bool Walled(in WorldState world, CollisionWorld collision, int row, int a, int b)
    {
        var (low, high) = a < b ? (a, b) : (b, a);
        var bit = 1UL << high;

        if ((_walled[low * 2] & bit) != 0)
            return (_walled[low * 2 + 1] & bit) != 0;

        ref readonly var p = ref _history[row + low];
        ref readonly var q = ref _history[row + high];
        var blocked = collision.CollisionLineBlocksBullets(p.X, p.Y, q.X, q.Y, world.BulletFilter);

        _walled[low * 2] |= bit;
        if (blocked)
            _walled[low * 2 + 1] |= bit;

        return blocked;
    }

    /// <summary>
    /// Off screen at every aspect ratio, behind a wall, or a cloaked spy.
    /// </summary>
    /// <remarks>
    /// The union of the view sizes, so an enemy on screen at only some aspect
    /// ratios counts as visible: a hidden lock is the evidence, and a player
    /// whose screen did show the enemy must never be credited with one.
    /// </remarks>
    private static bool IsHidden(
        in WorldState world, CollisionWorld collision, in Body self, in Body target, double dx, double dy)
    {
        var outOfView = !self.Zoomed
            && (Math.Abs(dx) > ViewHalfWidth || Math.Abs(dy) > ViewHalfHeight);

        return outOfView
            || target.Hidden
            || collision.CollisionLineBlocksBullets(self.X, self.Y, target.X, target.Y, world.BulletFilter);
    }

    /// <summary>
    /// The offset from <paramref name="slot"/> to its nearest visible (or hidden)
    /// enemy, both as they were <paramref name="lag"/> ticks before the tick
    /// <see cref="Observe"/> last took. For <c>tools/aimscan</c>'s synthetic
    /// cheats, which aim the way a cheat does at what a lagged client sees.
    /// </summary>
    public bool NearestEnemy(
        in WorldState world, CollisionWorld collision, int slot, int lag, bool hidden,
        out double dx, out double dy)
    {
        dx = dy = 0;

        if (_tick == 0 || lag > Math.Min(_tick - 1, MaxLag))
            return false;

        var row = ((_tick - 1 - lag) % History) * WorldState.MaxSlots;
        ref readonly var self = ref _history[row + slot];

        if (!self.Alive)
            return false;

        var best = double.PositiveInfinity;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            ref readonly var target = ref _history[row + other];

            if (other == slot || !target.Alive || target.Team == self.Team)
                continue;

            var ox = target.X - self.X;
            var oy = target.Y - self.Y;
            var distance = ox * ox + oy * oy;

            if (distance > Reach * Reach || distance >= best)
                continue;

            if (IsHidden(world, collision, self, target, ox, oy) != hidden)
                continue;

            best = distance;
            dx = ox;
            dy = oy;
        }

        return best < double.PositiveInfinity;
    }

    /// <summary>
    /// Whether <paramref name="aim"/> from <paramref name="slot"/> passes through a
    /// visible enemy's box, positions <paramref name="lag"/> ticks before the tick
    /// <see cref="Observe"/> last took. For <c>tools/aimscan</c>'s synthetic triggerbot.
    /// </summary>
    public bool OnVisibleEnemy(in WorldState world, CollisionWorld collision, int slot, int lag, double aim)
    {
        if (_tick == 0 || lag > Math.Min(_tick - 1, MaxLag))
            return false;

        var row = ((_tick - 1 - lag) % History) * WorldState.MaxSlots;
        ref readonly var self = ref _history[row + slot];

        if (!self.Alive)
            return false;

        var ux = Math.Cos(aim * Math.PI / 180);
        var uy = -Math.Sin(aim * Math.PI / 180);

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            ref readonly var target = ref _history[row + other];

            if (other == slot || !target.Alive || target.Team == self.Team)
                continue;

            var dx = target.X - self.X;
            var dy = target.Y - self.Y;

            if (dx * dx + dy * dy <= Reach * Reach
                && RayHitsBox(dx, dy, ux, uy, target.Class)
                && !IsHidden(world, collision, self, target, dx, dy))
            {
                return true;
            }
        }

        return false;
    }

    private int Row(int lag) => ((_tick - lag) % History + History) % History * WorldState.MaxSlots;

    /// <summary>Whether the target's bearing from the shooter turned by at least <see cref="MovingDegrees"/> since the tick before.</summary>
    private bool Moving(int before, int slot, int other, double dx, double dy)
    {
        ref readonly var self = ref _history[before + slot];
        ref readonly var target = ref _history[before + other];

        if (!self.Alive || !target.Alive)
            return false;

        // |sin| of the angle between the two bearings, from the cross product,
        // so the inner loop does no trigonometry. A bearing that reversed (a
        // target passing through the shooter) has a small cross product and a
        // negative dot; it is moving, and the dot catches it.
        var px = target.X - self.X;
        var py = target.Y - self.Y;
        var cross = px * dy - py * dx;
        var dot = px * dx + py * dy;
        var lengths = (px * px + py * py) * (dx * dx + dy * dy);

        return dot < 0 || cross * cross >= SinMoving * SinMoving * lengths;
    }

    private bool WasOnTarget(int before, int slot, int other, double aim)
    {
        ref readonly var self = ref _history[before + slot];
        ref readonly var target = ref _history[before + other];

        if (!self.Alive || !target.Alive)
            return false;

        return RayHitsBox(
            target.X - self.X, target.Y - self.Y,
            Math.Cos(aim * Math.PI / 180), -Math.Sin(aim * Math.PI / 180), target.Class);
    }

    /// <summary>The perpendicular distance from a target at (dx, dy) to the aim ray, or infinity behind it.</summary>
    private static double Miss(double dx, double dy, double ux, double uy)
    {
        var along = dx * ux + dy * uy;
        return along <= 0 ? double.PositiveInfinity : Math.Abs(dx * uy - dy * ux);
    }

    /// <summary>Whether the aim ray passes through the target's collision rectangle.</summary>
    private static bool RayHitsBox(double dx, double dy, double ux, double uy, PlayerClass playerClass)
    {
        var box = CharacterBox.For(playerClass);

        var tMin = 0.0;
        var tMax = double.PositiveInfinity;

        return Slab(dx + box.Left, dx + box.Right + 1, ux, ref tMin, ref tMax)
            && Slab(dy + box.Top, dy + box.Bottom + 1, uy, ref tMin, ref tMax);
    }

    private static bool Slab(double low, double high, double u, ref double tMin, ref double tMax)
    {
        if (Math.Abs(u) < 1e-12)
            return low <= 0 && 0 <= high;

        var a = low / u;
        var b = high / u;

        if (a > b)
            (a, b) = (b, a);

        tMin = Math.Max(tMin, a);
        tMax = Math.Min(tMax, b);
        return tMin <= tMax;
    }

    private static double Delta(double a, double b)
    {
        var d = (a - b) % 360;
        return ((d + 540) % 360) - 180;
    }

    /// <summary>
    /// The signals whose rate, at this player's best lag, is above its threshold
    /// with 99.9% confidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A lower confidence bound, not the rate.</b> A short session's rate is
    /// noise: the highest raw human rates in the fleet's recordings were 21 of
    /// 225 and 4 of 26. The Wilson bound at z = 3.29 asks for the evidence to
    /// grow with the claim, and it also covers picking the best of
    /// <see cref="MaxLag"/> + 1 lags. Each signal also needs a minimum sample
    /// before it is judged at all.
    /// </para>
    /// <para>
    /// <b>Calibrated on 662 human sessions from the fleet</b> (567 traces,
    /// 2026-09-15 to 2026-10-05): none crosses any threshold, and the highest
    /// human bound is under half of every threshold. The same sessions with a
    /// cheat substituted (<c>tools/aimscan</c>) cross it in every session with
    /// enough samples to judge. docs/HARDENING.md, "Aim analysis".
    /// </para>
    /// </remarks>
    public AimSignals Judge(int slot)
    {
        var signals = AimSignals.None;

        for (var lag = 0; lag <= MaxLag; lag++)
        {
            ref readonly var c = ref _counters[slot * (MaxLag + 1) + lag];

            if (Exceeds(c.Locked, c.Tracking, 200, 0.12))
                signals |= AimSignals.Lock;

            if (Exceeds(c.FlickLocks, c.Flicks, 20, 0.35))
                signals |= AimSignals.Flick;

            if (Exceeds(c.OnsetPresses, c.Presses, 20, 0.40))
                signals |= AimSignals.Trigger;

            if (Exceeds(c.HiddenLocked, c.HiddenTracking, 300, 0.07))
                signals |= AimSignals.Hidden;
        }

        return signals;
    }

    /// <summary>
    /// <see cref="Judge"/>'s signals that have not been returned for this slot
    /// before, marking them returned: one report per signal per player.
    /// </summary>
    public AimSignals TakeNew(int slot)
    {
        var fresh = Judge(slot) & ~_reported[slot];
        _reported[slot] |= fresh;
        return fresh;
    }

    /// <summary>The best lag's counters for a signal, for a log line.</summary>
    public (int Hits, int Of, int Lag) Evidence(int slot, AimSignals signal)
    {
        var best = (Hits: 0, Of: 0, Lag: 0);
        var bestBound = -1.0;

        for (var lag = 0; lag <= MaxLag; lag++)
        {
            ref readonly var c = ref _counters[slot * (MaxLag + 1) + lag];
            var (hits, of) = signal switch
            {
                AimSignals.Lock => (c.Locked, c.Tracking),
                AimSignals.Flick => (c.FlickLocks, c.Flicks),
                AimSignals.Trigger => (c.OnsetPresses, c.Presses),
                _ => (c.HiddenLocked, c.HiddenTracking),
            };

            var bound = of == 0 ? 0 : LowerBound(hits, of);
            if (bound > bestBound)
            {
                bestBound = bound;
                best = (hits, of, lag);
            }
        }

        return best;
    }

    private static bool Exceeds(int hits, int of, int minimum, double threshold) =>
        of >= minimum && LowerBound(hits, of) > threshold;

    /// <summary>The Wilson score interval's lower end at z = 3.29.</summary>
    public static double LowerBound(int hits, int of)
    {
        const double Z = 3.29;
        var p = (double)hits / of;
        var z2n = Z * Z / of;
        var centre = p + z2n / 2;
        var spread = Z * Math.Sqrt(p * (1 - p) / of + z2n / (4 * of));
        return (centre - spread) / (1 + z2n);
    }

    /// <summary>One player's tallies at one lag.</summary>
    public struct Counters
    {
        /// <summary>Ticks with a visible enemy moving across the shooter's view nearest the crosshair.</summary>
        public int Tracking;

        /// <summary>Of those, ticks the crosshair was within <see cref="TightPixels"/> of it.</summary>
        public int Locked;

        /// <summary>Aim changes of at least <see cref="SnapDegrees"/> in one tick with a visible enemy.</summary>
        public int Flicks;

        /// <summary>Of those, flicks that landed within <see cref="TightPixels"/> of one.</summary>
        public int FlickLocks;

        /// <summary>Attack presses with a visible enemy.</summary>
        public int Presses;

        /// <summary>Of those, presses on the first tick the aim ray met an enemy's box.</summary>
        public int OnsetPresses;

        /// <summary>Ticks with a hidden enemy moving across the shooter's bearing.</summary>
        public int HiddenTracking;

        /// <summary>Of those, ticks the crosshair was within <see cref="TightPixels"/> of a hidden one.</summary>
        public int HiddenLocked;
    }

    private struct Body
    {
        public bool Alive;
        public double X;
        public double Y;
        public Team Team;
        public PlayerClass Class;
        public bool Hidden;
        public bool Zoomed;
    }

    private struct Previous
    {
        public bool Seen;
        public double Aim;
        public PlayerKeys Keys;
    }
}

/// <summary>What <see cref="AimAnalysis.Judge"/> found.</summary>
[Flags]
public enum AimSignals
{
    None = 0,

    /// <summary>The crosshair sits exactly on moving enemies far more often than a hand can.</summary>
    Lock = 1,

    /// <summary>Large one-tick flicks land exactly on an enemy.</summary>
    Flick = 2,

    /// <summary>Fire is pressed on the very tick the crosshair first meets an enemy.</summary>
    Trigger = 4,

    /// <summary>The crosshair follows enemies the player cannot see.</summary>
    Hidden = 8,
}
