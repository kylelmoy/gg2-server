using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Env;

/// <summary>
/// The v0 observation layout: egocentric, normalised, fixed-size.
/// </summary>
/// <remarks>
/// <b><c>docs/ENV.md</c> step 3</b>, built to <c>gg2-rl/docs/DESIGN.md</c>'s
/// sketch. <b>This type is the authoritative field order</b> — the document
/// describes the shape, the code decides it, and
/// <see cref="LayoutVersion"/> is what a trainer checks.
/// <para>
/// ⚠️ <b>Egocentric and normalised, not raw.</b> Absolute world coordinates
/// would make a policy learn one map's geometry rather than the game, and
/// unnormalised velocities would put a rocket's 13 px/tick and a hp fraction on
/// scales three orders of magnitude apart in the same vector.
/// </para>
/// <para>
/// ⚠️ <b>Nothing here allocates and nothing enumerates the whole world.</b>
/// <c>docs/BOTAI.md</c> item 6: a feature vector is extracted for every agent on
/// every decision, so "nearest K" written as a scan costs more than the
/// simulation it observes. <see cref="WorldView.NearestEnemies"/> and
/// <see cref="WorldView.ProjectilesWithin"/> are the queries.
/// </para>
/// </remarks>
public static class ObservationWriter
{
    /// <summary>
    /// ⚠️ <b>Bumped whenever any count, order or scale below changes</b>, and
    /// folded into the simulation stamp so a trainer cannot silently load a
    /// checkpoint trained on a different vector.
    /// </summary>
    public const int LayoutVersion = 1;

    /// <summary>
    /// ⚠️ <b>Nine, not ten: the Quote has no slot.</b> It is a joke class that a
    /// duel never selects, and a tenth column would be a column that is always
    /// zero. <see cref="Write"/> refuses a Quote rather than encoding it as
    /// "no class at all", because silently observing nothing is the failure
    /// that trains a policy on a lie.
    /// </summary>
    public const int ClassCount = 9;

    /// <summary>Projectile kinds, one-hot. All nine of <see cref="ProjectileKind"/>.</summary>
    public const int ProjectileKindCount = 9;

    /// <summary>Enemies observed, nearest first.</summary>
    public const int Enemies = 2;

    /// <summary>Projectiles observed, nearest first.</summary>
    public const int Projectiles = 4;

    /// <summary>Rays in the geometry fan.</summary>
    public const int Rays = 24;

    /// <summary>Side of the downsampled walkmask patch.</summary>
    public const int PatchSide = 8;

    /// <summary>Goal kinds, one-hot. Constant "engage" in a duel.</summary>
    public const int GoalKinds = 4;

    // --- normalising scales, all v0 constants -------------------------------
    //
    // ⚠️ A scale is part of the layout: changing one changes what the numbers
    // mean and is a layout bump, exactly like changing a count.

    /// <summary>Pixels that map to 1.0 in a relative position.</summary>
    private const double PositionScale = 400.0;

    /// <summary>Pixels per tick that map to 1.0 in a velocity.</summary>
    private const double VelocityScale = 15.0;

    /// <summary>How far a ray looks before reporting 1.0.</summary>
    private const double RayRange = 320.0;

    /// <summary>World pixels per patch cell.</summary>
    private const int PatchCell = 16;

    /// <summary>Radius the projectile query sweeps.</summary>
    private const double ProjectileRadius = 400.0;

    /// <summary>Floats one observation occupies.</summary>
    public static int Length =>
        SelfBlock
        + (Enemies * EnemyBlock)
        + (Projectiles * ProjectileBlock)
        + Rays
        + (PatchSide * PatchSide)
        + GoalBlock;

    private const int SelfBlock = ClassCount + 1 + 2 + 1 + 2 + 1 + 1 + 1;

    private const int EnemyBlock = 1 + 2 + 2 + ClassCount + 1 + 2 + 1;

    private const int ProjectileBlock = 1 + 2 + 2 + ProjectileKindCount + 1;

    private const int GoalBlock = 2 + 1 + GoalKinds;

    /// <summary>
    /// Fills <paramref name="destination"/> with this slot's view of the world.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The span is cleared first.</b> A caller reusing a buffer across
    /// steps would otherwise see last step's enemy in a slot that is now empty —
    /// stale features are worse than absent ones, because a presence flag of 0
    /// beside a stale position teaches the policy that the flag means nothing.
    /// </remarks>
    /// <param name="goal">
    /// The macro goal to report. <b><c>default</c> is "engage the nearest
    /// enemy"</b>, which is what this block carried before goals were settable —
    /// so an unchanged caller writes an unchanged observation.
    /// </param>
    public static void Write(in WorldView view, int slot, Span<float> destination, in AgentGoal goal = default)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException(
                $"an observation is {Length} floats, not {destination.Length}", nameof(destination));
        }

        destination[..Length].Clear();

        if (!view.IsAlive(slot))
            return;

        ref readonly var self = ref view.Character(slot);

        if (self.Class == PlayerClass.Quote)
        {
            throw new ArgumentException(
                "the Quote has no column in the v0 observation layout; see ObservationWriter.ClassCount",
                nameof(slot));
        }

        var at = 0;

        WriteSelf(in view, slot, in self, destination, ref at);
        WriteEnemies(in view, slot, in self, destination, ref at);
        WriteProjectiles(in view, slot, in self, destination, ref at);
        WriteGeometry(in view, in self, destination, ref at);
        WriteGoal(in view, slot, in self, in goal, destination, ref at);

        System.Diagnostics.Debug.Assert(at == Length, "the writer and Length disagree");
    }

    private static void WriteSelf(
        in WorldView view, int slot, in CharacterState self, Span<float> o, ref int at)
    {
        OneHot(o, ref at, (int)self.Class, ClassCount);

        o[at++] = (float)(self.Hp / ClassStats.For(self.Class).MaxHp);
        o[at++] = (float)(self.HSpeed / VelocityScale);
        o[at++] = (float)(self.VSpeed / VelocityScale);

        // Grounded is asked of the collision world rather than read off a flag:
        // `onground` is a Begin Step reading and can be a tick stale, and a
        // policy learning to jump wants the truth at decision time.
        o[at++] = view.Collision.PlaceFree(self.Box, self.X, self.Y + 1, self.Filter) ? 0f : 1f;

        var aim = self.AimDirection * Math.PI / 180.0;
        o[at++] = (float)Math.Sin(aim);
        o[at++] = (float)Math.Cos(aim);

        ref readonly var weapon = ref view.Weapon(slot);
        var stats = WeaponStats.For(WeaponStats.KindFor(self.Class));

        o[at++] = stats.MaxAmmo > 0 ? (float)(weapon.AmmoCount / stats.MaxAmmo) : 0f;
        o[at++] = stats.ReloadTime > 0 && weapon.ReloadTimer > 0
            ? (float)(weapon.ReloadTimer / (double)stats.ReloadTime)
            : 0f;

        // ⚠️ One column for three different resources, because they are the same
        // thing to a policy: "the special I am holding is this full". A medic's
        // uber, an engineer's nuts and bolts and a sniper's charge never coexist
        // on one character, so a column each would be two columns of zeros.
        o[at++] = self.Class switch
        {
            PlayerClass.Medic => (float)(weapon.UberCharge / WeaponState.MaxUberCharge),
            PlayerClass.Engineer => (float)(self.NutsNBolts / CharacterState.MaxNutsNBolts),
            _ => 0f,
        };
    }

    private static void WriteEnemies(
        in WorldView view, int slot, in CharacterState self, Span<float> o, ref int at)
    {
        Span<int> found = stackalloc int[Enemies];
        var count = view.NearestEnemies(slot, found);

        for (var i = 0; i < Enemies; i++)
        {
            if (i >= count)
            {
                at += EnemyBlock;
                continue;
            }

            ref readonly var enemy = ref view.Character(found[i]);

            o[at++] = 1f;
            o[at++] = (float)((enemy.X - self.X) / PositionScale);
            o[at++] = (float)((enemy.Y - self.Y) / PositionScale);
            o[at++] = (float)((enemy.HSpeed - self.HSpeed) / VelocityScale);
            o[at++] = (float)((enemy.VSpeed - self.VSpeed) / VelocityScale);

            OneHot(o, ref at, (int)enemy.Class, ClassCount);

            o[at++] = (float)(enemy.Hp / ClassStats.For(enemy.Class).MaxHp);

            var aim = enemy.AimDirection * Math.PI / 180.0;
            o[at++] = (float)Math.Sin(aim);
            o[at++] = (float)Math.Cos(aim);

            // ⚠️ Time since seen is a fixed 0 in v0 and the column exists anyway.
            // The reaction-delay ring buffer is gg2-rl phase 5; carrying the
            // column now is what stops the layout churning — and a bump costs a
            // retrain, while a constant column costs one float.
            o[at++] = 0f;
        }
    }

    private static void WriteProjectiles(
        in WorldView view, int slot, in CharacterState self, Span<float> o, ref int at)
    {
        Span<ProjectileHandle> found = stackalloc ProjectileHandle[Projectiles];
        var count = view.ProjectilesWithin(self.X, self.Y, ProjectileRadius, found);

        for (var i = 0; i < Projectiles; i++)
        {
            if (i >= count || !view.State.TryGetProjectile(found[i], out var shot))
            {
                at += ProjectileBlock;
                continue;
            }

            o[at++] = 1f;
            o[at++] = (float)((shot.X - self.X) / PositionScale);
            o[at++] = (float)((shot.Y - self.Y) / PositionScale);
            o[at++] = (float)(shot.Motion.HSpeed / VelocityScale);
            o[at++] = (float)(shot.Motion.VSpeed / VelocityScale);

            OneHot(o, ref at, (int)shot.Kind, ProjectileKindCount);

            // Mine or theirs. ⚠️ By TEAM, not by owner slot: a team-mate's rocket
            // is as harmless to this agent as its own, and in a duel the
            // distinction the policy needs is "is this coming for me".
            o[at++] = shot.Team == self.Team ? 0f : 1f;
        }
    }

    /// <summary>Pixels between probes along a ray.</summary>
    /// <remarks>
    /// ⚠️ <b>Part of the layout, like a scale.</b> The fan reports the first
    /// probe that lands on something solid, so the spacing decides which thin
    /// geometry the policy can see at all. Changing it is a layout bump.
    /// </remarks>
    private const double RayStep = 4.0;

    /// <summary>Probes along one ray: <see cref="RayRange"/> / <see cref="RayStep"/>.</summary>
    private const int RayProbes = (int)(RayRange / RayStep);

    /// <summary>
    /// The fan's unit directions, which are the same 24 every call.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Computed from the same expression the loop used</b> —
    /// <c>Math.Cos(i * (2 * Math.PI / Rays))</c>, not a rearrangement of it — so
    /// every direction is the identical double and the fan is unchanged. This is
    /// a table for 48 trig calls an observation, not a new set of angles.
    /// </remarks>
    private static readonly (double Dx, double Dy)[] RayDirections = BuildRayDirections();

    private static (double, double)[] BuildRayDirections()
    {
        var directions = new (double, double)[Rays];

        for (var i = 0; i < Rays; i++)
        {
            var angle = i * (2 * Math.PI / Rays);
            directions[i] = (Math.Cos(angle), Math.Sin(angle));
        }

        return directions;
    }

    /// <summary>
    /// How far, in world pixels, a probe may move from a cell of this clearance
    /// and still be certain of landing on something open.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Public, and a named function rather than an expression inline in
    /// the march, because a test has to be able to read it.</b> Making it
    /// <c>internal</c> instead would have needed <c>InternalsVisibleTo</c> on this
    /// assembly, which also exposes the <c>Program</c> that top-level statements
    /// generate and collides with <c>Gg2.Parity</c>'s. The first version of
    /// <c>ObservationRayFanTests</c> restated <c>6c - 7</c> in its own body and so
    /// passed every mutation of the constant it existed to protect — including
    /// <c>6c - 5</c>, which is genuinely unsafe. It now calls this.
    /// <para>
    /// The algebra: a clearance of <c>c</c> says every cell within Chebyshev
    /// distance <c>c - 1</c> is open. A pixel <c>d</c> away from a probe is at
    /// most <c>floor((d + 5) / 6)</c> cells away — the 5 is the probe's own
    /// position inside its 6-pixel cell — so <c>d</c> is safe while
    /// <c>floor((d + 5) / 6) &lt;= c - 1</c>, which holds up to <c>d = 6c - 6</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>One pixel is given back deliberately.</b> <c>6c - 6</c> is the tight
    /// value; the extra pixel costs at most a quarter of one probe and leaves the
    /// bound with margin rather than sitting exactly on the boundary of an
    /// argument about rounding. It is not a slip to be tidied up.
    /// </para>
    /// </remarks>
    public static int SafeSkipPixels(int clearance) => (6 * clearance) - 7;

    private static void WriteGeometry(
        in WorldView view, in CharacterState self, Span<float> o, ref int at)
    {
        // A fan of rays from the character's origin, marched against the
        // walkmask. ⚠️ Marched rather than asked of CollisionLine: that
        // primitive answers "is anything in the way", and a policy needs how
        // far, which is a different question and not one the simulation ever
        // has to answer.
        //
        // ⚠️ The march SKIPS open ground rather than probing every four pixels,
        // and the result is identical rather than approximate. Walkmask.Clearance
        // gives the Chebyshev cell distance to the nearest solid cell; a
        // clearance of c guarantees every world pixel within 6c - 7 of the probe
        // is open, so advancing by no more than that lands somewhere already
        // known to be open and cannot step over the first solid probe. Naive:
        // 5.02 us a fan averaged over 512 map positions; this: 2.73 us.
        // ObservationRayFanTests pins the two together.
        var mask = view.Collision.Mask;
        var clearance = mask.Clearance;
        var width = mask.Width;
        var height = mask.Height;
        var originX = self.X;
        var originY = self.Y;

        for (var i = 0; i < Rays; i++)
        {
            var (dx, dy) = RayDirections[i];
            var distance = RayRange;

            for (var probe = 1; probe <= RayProbes; probe++)
            {
                var step = probe * RayStep;
                var column = CollisionWorld.CellOf((int)Math.Round(originX + (dx * step)));
                var row = CollisionWorld.CellOf((int)Math.Round(originY + (dy * step)));

                // Off-mask is open — Walkmask.IsSolid's own rule — and carries no
                // clearance, so a ray outside the map walks probe by probe. That
                // is rare and correct; seeding the border with a skip would need
                // a claim about geometry that is not there.
                if ((uint)column >= (uint)width || (uint)row >= (uint)height)
                    continue;

                var open = clearance[(row * width) + column];

                if (open == 0)
                {
                    distance = step;
                    break;
                }

                var skip = SafeSkipPixels(open) / (int)RayStep;

                if (skip > 1)
                    probe += skip - 1;
            }

            o[at++] = (float)(distance / RayRange);
        }

        // And a coarse patch centred on the character, which is what tells a
        // policy about a ledge it is standing next to rather than a wall it is
        // pointing at.
        var half = PatchSide / 2;

        for (var row = 0; row < PatchSide; row++)
        for (var column = 0; column < PatchSide; column++)
        {
            var x = (int)Math.Round(self.X) + ((column - half) * PatchCell);
            var y = (int)Math.Round(self.Y) + ((row - half) * PatchCell);

            o[at++] = view.Collision.IsSolidAt(x, y) ? 1f : 0f;
        }
    }

    private static void WriteGoal(
        in WorldView view, int slot, in CharacterState self, in AgentGoal goal, Span<float> o, ref int at)
    {
        // ⚠️ Was a constant "engage the nearest enemy" until 2026-08-30, and the
        // block was reserved from day one for exactly this. docs/ENV.md's
        // hybrid-boundary rule: a macro goal the policy has never seen cannot be
        // introduced later without a layout bump and a retrain, and a constant
        // column costs four floats. gg2-rl phase 4 is what cashed that in.
        //
        // ⚠️ A DEFAULT AgentGoal takes the nearest-enemy path below, unchanged,
        // so every observation written by a caller that knows nothing about goals
        // is byte-identical to the ones every existing checkpoint trained on.
        // ObservationRayFanTests' sibling, ObservationGoalTests, pins that.
        double dx;
        double dy;
        var have = false;

        if (goal.HasTarget)
        {
            dx = goal.TargetX - self.X;
            dy = goal.TargetY - self.Y;
            have = true;
        }
        else
        {
            Span<int> found = stackalloc int[1];

            if (view.NearestEnemies(slot, found) > 0)
            {
                ref readonly var enemy = ref view.Character(found[0]);

                dx = enemy.X - self.X;
                dy = enemy.Y - self.Y;
                have = true;
            }
            else
            {
                dx = 0;
                dy = 0;
            }
        }

        if (have)
        {
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            if (distance > 0)
            {
                // ⚠️ dy first, then dx. That is the order the block has always
                // been written in and it is what every trained checkpoint reads;
                // it looks like a transposition and is not one to fix.
                o[at++] = (float)(dy / distance);
                o[at++] = (float)(dx / distance);
            }
            else
            {
                at += 2;
            }

            o[at++] = (float)Math.Min(distance / PositionScale, 1.0);
        }
        else
        {
            at += 3;
        }

        // ⚠️ Validated where the goal is SET. OneHot writes no column at all for
        // an out-of-range index, which would reach the policy as "no goal" rather
        // than as an error.
        OneHot(o, ref at, goal.Kind, GoalKinds);
    }

    private static void OneHot(Span<float> o, ref int at, int index, int width)
    {
        if ((uint)index < (uint)width)
            o[at + index] = 1f;

        at += width;
    }
}
