using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// What a <see cref="SimPolicyNetwork"/> sees: gg2-sim's <c>env/env.h</c>
/// <c>WriteObs</c>, as gg2-sim's <c>ref/PolicyDriver.cs</c> transcribes it.
/// </summary>
/// <remarks>
/// ⛔ <b>Bit-exact against gg2-sim's <c>PolicyDriver.ObserveTeam</c> and
/// <c>Observe</c></b>, which gg2-sim's <c>scripts/obs-check.sh</c> holds bit-exact
/// against the GPU environment the policies trained in. Every expression here
/// is that file's, types and order included: a double divided then narrowed is
/// not the same float as a float divided. The only departures are allocation —
/// spans where it built lists — which cannot change a value.
/// <para>
/// <b>Two layouts.</b> Team (<see cref="TeamSize"/>): self, the three nearest
/// live enemies, the two nearest live team-mates, the eight nearest rockets and
/// shots, an 11x9 wall grid and own class. Duel (<see cref="DuelSize"/>): self,
/// one named opponent, the six nearest rockets and shots, the grid, then both
/// classes; <see cref="DuelSizeNoClasses"/> is the same without the classes.
/// A zoom net's (<see cref="ZoomSize"/>) is the team layout plus the sniper's own
/// scope and rifle charge after the class one-hots; a demo net's
/// (<see cref="DemoSize"/>) is the team layout plus the demoman's eight nearest
/// own mines and lobbed/8 there instead; a pyro net's (<see cref="PyroSize"/>)
/// is the team layout plus the pyro's ReadyToBlast, ReadyToFlare and own
/// burn/210.
/// </para>
/// <para>
/// <b>The team layout's projectile list includes ENEMY mines and flares</b>
/// (gg2-sim <c>31f42ef</c> and the pyro commit after it), in every team width: a
/// mine or flare whose team is not the viewer's, beside rockets and shots. A
/// team-mate's or one's own is not listed, and flames never are. The duel
/// layout's list is rockets and shots only, unchanged.
/// </para>
/// </remarks>
public static class SimPolicyObservation
{
    private const int ClassCount = 10, GridW = 11, GridH = 9, GridStep = 18;
    private const int NearEnemies = 3, NearAllies = 2, NearShots = 8, NearRockets = 6;
    private const int CharacterObs = 6 + ClassCount, ShotObs = 7;

    /// <summary>How many enemies a team observation lists, and so how many relative aim groups a team net has.</summary>
    public const int ListedEnemies = NearEnemies;

    public const int TeamSize = 8 + ((NearEnemies + NearAllies) * CharacterObs) + (NearShots * ShotObs) + (GridW * GridH) + ClassCount;

    public const int DuelSizeNoClasses = 14 + (NearRockets * 6) + (GridW * GridH);

    public const int DuelSize = DuelSizeNoClasses + (2 * ClassCount);

    /// <summary>gg2-sim's <c>ZoomObs</c>: own scope, then rifle charge.</summary>
    public const int ZoomColumns = 2;

    public const int ZoomSize = TeamSize + ZoomColumns;

    private const int NearMines = 8, MineObs = 4, MaxMines = 8;

    /// <summary>gg2-sim's <c>DemoObs</c>: the eight nearest own mines, four columns each, then lobbed/8.</summary>
    public const int DemoColumns = (NearMines * MineObs) + 1;

    public const int DemoSize = TeamSize + DemoColumns;

    /// <summary>gg2-sim's <c>PyroObs</c>: ReadyToBlast, ReadyToFlare, then own burn/210.</summary>
    public const int PyroColumns = 3;

    public const int PyroSize = TeamSize + PyroColumns;

    /// <summary>
    /// The team observation for <paramref name="meSlot"/>, and the enemies it
    /// listed (nearest first, -1 past them) into <paramref name="listed"/>.
    /// <paramref name="zoom"/> appends the zoom layout's two columns,
    /// <paramref name="demo"/> the demo layout's thirty-three, <paramref name="pyro"/>
    /// the pyro layout's three.
    /// </summary>
    public static void Team(
        in WorldView world, int meSlot, Span<float> o, Span<int> listed, bool zoom = false, bool demo = false, bool pyro = false)
    {
        ref readonly var me = ref world.Character(meSlot);
        ref readonly var gun = ref world.Weapon(meSlot);
        var map = world.Collision;
        o[..(zoom ? ZoomSize : demo ? DemoSize : pyro ? PyroSize : TeamSize)].Clear();
        var k = 0;
        o[k++] = (float)(me.X / map.Width);
        o[k++] = (float)(me.Y / map.Height);
        o[k++] = Clip(me.HSpeed / 15, 2);
        o[k++] = Clip(me.VSpeed / 15, 2);
        o[k++] = (float)(me.Hp / ClassStats.For(me.Class).MaxHp);
        // The rifle has no magazine (MaxAmmo 0), so unguarded this is NaN for a sniper.
        o[k++] = WeaponStats.For(me.Class).MaxAmmo > 0 ? (float)(gun.AmmoCount / WeaponStats.For(me.Class).MaxAmmo) : 0;
        o[k++] = gun.ReadyToShoot ? 1 : 0;
        o[k++] = me.OnGround ? 1 : 0;

        Span<int> enemies = stackalloc int[NearEnemies];
        Span<int> allies = stackalloc int[NearAllies];
        int ne = Nearest(in world, meSlot, mates: false, enemies), na = Nearest(in world, meSlot, mates: true, allies);

        for (var n = 0; n < NearEnemies; n++)
        {
            listed[n] = n < ne ? enemies[n] : -1;

            if (n < ne)
                WriteCharacter(in me, in world.Character(enemies[n]), o, ref k);
            else
                k += CharacterObs;
        }

        for (var n = 0; n < NearAllies; n++)
        {
            if (n < na)
                WriteCharacter(in me, in world.Character(allies[n]), o, ref k);
            else
                k += CharacterObs;
        }

        Span<int> picked = stackalloc int[NearShots];
        var count = PickShots(in world, in me, picked, enemyHazards: true);

        for (var n = 0; n < NearShots; n++)
        {
            if (n < count)
            {
                ref readonly var r = ref world.State.Projectiles[picked[n]];
                o[k++] = Clip((r.X - me.X) / 500, 4);
                o[k++] = Clip((r.Y - me.Y) / 500, 4);
                o[k++] = Clip(r.Motion.HSpeed / 15, 2);
                o[k++] = Clip(r.Motion.VSpeed / 15, 2);
                o[k++] = r.OwnerSlot == meSlot ? 1 : 0;
                o[k++] = r.Team != me.Team ? 1 : 0;
                o[k++] = 1;
            }
            else
            {
                k += ShotObs;
            }
        }

        WriteGrid(map, in me, o, ref k);
        o[k + (int)me.Class] = 1;

        if (zoom)
        {
            k += ClassCount;
            o[k++] = me.Zoomed ? 1 : 0;
            o[k++] = (float)(gun.Charge / Weapons.RifleChargeTime);
        }
        else if (demo)
        {
            // The body's own nearest mines (ties to the earlier created), then lobbed/8.
            k += ClassCount;
            Span<int> mines = stackalloc int[NearMines];
            var found = PickOwnMines(in world, meSlot, in me, mines);

            for (var n = 0; n < NearMines; n++)
            {
                if (n < found)
                {
                    ref readonly var m = ref world.State.Projectiles[mines[n]];
                    o[k++] = Clip((m.X - me.X) / 500, 4);
                    o[k++] = Clip((m.Y - me.Y) / 500, 4);
                    o[k++] = m.Stickied ? 1 : 0;
                    o[k++] = 1;
                }
                else
                {
                    k += MineObs;
                }
            }

            o[k++] = (float)((double)gun.Lobbed / MaxMines);
        }
        else if (pyro)
        {
            k += ClassCount;
            o[k++] = gun.ReadyToBlast ? 1 : 0;
            o[k++] = gun.ReadyToFlare ? 1 : 0;
            o[k++] = (float)(me.BurnDuration / CharacterState.MaxBurnDuration);
        }
    }

    /// <summary>
    /// The duel observation for <paramref name="meSlot"/> against
    /// <paramref name="opSlot"/>, <paramref name="length"/> wide
    /// (<see cref="DuelSize"/> or <see cref="DuelSizeNoClasses"/>).
    /// </summary>
    public static void Duel(in WorldView world, int meSlot, int opSlot, Span<float> o, int length)
    {
        ref readonly var me = ref world.Character(meSlot);
        ref readonly var op = ref world.Character(opSlot);
        ref readonly var gun = ref world.Weapon(meSlot);
        var map = world.Collision;
        o[..length].Clear();
        var k = 0;
        o[k++] = (float)(me.X / map.Width);
        o[k++] = (float)(me.Y / map.Height);
        o[k++] = Clip(me.HSpeed / 15, 2);
        o[k++] = Clip(me.VSpeed / 15, 2);
        o[k++] = (float)(me.Hp / ClassStats.For(me.Class).MaxHp);
        // The rifle has no magazine (MaxAmmo 0), so unguarded this is NaN for a sniper.
        o[k++] = WeaponStats.For(me.Class).MaxAmmo > 0 ? (float)(gun.AmmoCount / WeaponStats.For(me.Class).MaxAmmo) : 0;
        o[k++] = gun.ReadyToShoot ? 1 : 0;
        o[k++] = me.OnGround ? 1 : 0;
        o[k++] = Clip((op.X - me.X) / 500, 4);
        o[k++] = Clip((op.Y - me.Y) / 500, 4);
        o[k++] = Clip(op.HSpeed / 15, 2);
        o[k++] = Clip(op.VSpeed / 15, 2);
        o[k++] = (float)(op.Hp / ClassStats.For(op.Class).MaxHp);
        o[k++] = world.IsAlive(opSlot) ? 1 : 0;

        Span<int> picked = stackalloc int[NearRockets];
        var count = PickShots(in world, in me, picked, enemyHazards: false);

        for (var n = 0; n < NearRockets; n++)
        {
            if (n < count)
            {
                ref readonly var r = ref world.State.Projectiles[picked[n]];
                o[k++] = Clip((r.X - me.X) / 500, 4);
                o[k++] = Clip((r.Y - me.Y) / 500, 4);
                o[k++] = Clip(r.Motion.HSpeed / 15, 2);
                o[k++] = Clip(r.Motion.VSpeed / 15, 2);
                o[k++] = r.OwnerSlot == meSlot ? 1 : 0;
                o[k++] = 1;
            }
            else
            {
                k += 6;
            }
        }

        WriteGrid(map, in me, o, ref k);

        if (length > k)
        {
            o[k + (int)me.Class] = 1;

            if (world.IsAlive(opSlot))
                o[k + ClassCount + (int)op.Class] = 1;
        }
    }

    /// <summary>
    /// <c>env/env.h</c>, <c>Nearest</c>: the <c>output.Length</c> nearest live
    /// characters on <paramref name="slot"/>'s team (<paramref name="mates"/>) or
    /// not, nearest first by squared distance, ties to the lower slot.
    /// </summary>
    public static int Nearest(in WorldView world, int slot, bool mates, Span<int> output)
    {
        ref readonly var me = ref world.Character(slot);
        var count = output.Length;
        Span<double> distances = stackalloc double[count];
        var found = 0;

        for (var s = 1; s < WorldState.MaxSlots; s++)
        {
            if (s == slot || !world.IsAlive(s) || (world.Character(s).Team == me.Team) != mates)
                continue;

            // Not in gg2-sim's ref: a no-op unless the view carries an opponents
            // table, which only the duel server builds.
            if (!mates && !world.MayOppose(slot, s))
                continue;

            ref readonly var c = ref world.Character(s);
            double dx = c.X - me.X, dy = c.Y - me.Y, d = (dx * dx) + (dy * dy);
            var at = Math.Min(found, count);

            while (at > 0 && d < distances[at - 1])
                at--;

            if (at >= count)
                continue;

            for (var i = Math.Min(found, count - 1); i > at; i--)
            {
                output[i] = output[i - 1];
                distances[i] = distances[i - 1];
            }

            output[at] = s;
            distances[at] = d;

            if (found < count)
                found++;
        }

        return found;
    }

    // The nearest rockets and shots (and, for the team layout, enemy mines and flares),
    // nearest first, by repeated selection over the live list in its own order —
    // so an exact tie goes to the earlier live entry, as gg2-sim's picks it.
    private static int PickShots(in WorldView world, in CharacterState me, Span<int> picked, bool enemyHazards)
    {
        var count = 0;

        for (var n = 0; n < picked.Length; n++)
        {
            var best = -1;
            var bestDistance = 0.0;

            for (var i = 0; i < world.State.LiveProjectileCount; i++)
            {
                var p = world.State.LiveProjectileHandle(i).Index;
                ref readonly var pr = ref world.State.Projectiles[p];

                var shot = pr.Kind is ProjectileKind.Rocket or ProjectileKind.Shot
                    || (enemyHazards && pr.Kind is (ProjectileKind.Mine or ProjectileKind.Flare) && pr.Team != me.Team);

                if (!shot || picked[..count].Contains(p))
                    continue;

                double dx = pr.X - me.X, dy = pr.Y - me.Y, d = (dx * dx) + (dy * dy);

                if (best < 0 || d < bestDistance)
                {
                    best = p;
                    bestDistance = d;
                }
            }

            if (best < 0)
                break;

            picked[count++] = best;
        }

        return count;
    }

    // The viewer's own mines, nearest first, the same repeated selection.
    private static int PickOwnMines(in WorldView world, int meSlot, in CharacterState me, Span<int> picked)
    {
        var count = 0;

        for (var n = 0; n < picked.Length; n++)
        {
            var best = -1;
            var bestDistance = 0.0;

            for (var i = 0; i < world.State.LiveProjectileCount; i++)
            {
                var p = world.State.LiveProjectileHandle(i).Index;
                ref readonly var pr = ref world.State.Projectiles[p];

                if (pr.Kind != ProjectileKind.Mine || pr.OwnerSlot != meSlot || picked[..count].Contains(p))
                    continue;

                double dx = pr.X - me.X, dy = pr.Y - me.Y, d = (dx * dx) + (dy * dy);

                if (best < 0 || d < bestDistance)
                {
                    best = p;
                    bestDistance = d;
                }
            }

            if (best < 0)
                break;

            picked[count++] = best;
        }

        return count;
    }

    private static void WriteCharacter(in CharacterState me, in CharacterState c, Span<float> o, ref int k)
    {
        o[k] = Clip((c.X - me.X) / 500, 4);
        o[k + 1] = Clip((c.Y - me.Y) / 500, 4);
        o[k + 2] = Clip(c.HSpeed / 15, 2);
        o[k + 3] = Clip(c.VSpeed / 15, 2);
        o[k + 4] = (float)(c.Hp / ClassStats.For(c.Class).MaxHp);
        o[k + 5] = 1;
        o[k + 6 + (int)c.Class] = 1;
        k += CharacterObs;
    }

    // The 11x9 wall grid around the character, 18px apart, reading the walkmask
    // AND the solid rects, so gates and player walls show (env/env.h, PointInSolidRect).
    private static void WriteGrid(CollisionWorld map, in CharacterState me, Span<float> o, ref int k)
    {
        var mask = map.Map.Walkmask;
        int cx = (int)Math.Floor(me.X), cy = (int)Math.Floor(me.Y);

        for (var gy = 0; gy < GridH; gy++)
        {
            for (var gx = 0; gx < GridW; gx++)
            {
                int px = cx + ((gx - (GridW / 2)) * GridStep), py = cy + ((gy - (GridH / 2)) * GridStep);
                int col = px >= 0 ? px / 6 : -1, row = py >= 0 ? py / 6 : -1;
                o[k++] = mask.IsSolid(col, row) || InSolidRect(map, px, py, me.Filter) ? 1 : 0;
            }
        }
    }

    private static bool InSolidRect(CollisionWorld map, int px, int py, in CollisionFilter filter)
    {
        foreach (ref readonly var r in map.RectSpan)
        {
            if (px >= r.Left && px <= r.Right && py >= r.Top && py <= r.Bottom && r.IsSolidTo(filter))
                return true;
        }

        return false;
    }

    private static float Clip(double v, double limit) => (float)(v > limit ? limit : v < -limit ? -limit : v);
}
