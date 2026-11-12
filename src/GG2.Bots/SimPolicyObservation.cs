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
/// burn/210; a medic net's (<see cref="MedicSize"/>) is the team layout plus
/// its uber charge/2000, uber ready, ubering, beaming, the patient's hp share and
/// the patient's index among the listed team-mates (-1 when not one of them or
/// not beaming); an engineer net's (<see cref="EngineerSize"/>) adds its
/// nuts'n'bolts/100 and its own sentry (present, hp/100, dx/500, dy/500); a spy
/// net's (<see cref="SpySize"/>) its cloak, cloak alpha, ready-to-stab and the
/// stab freeze left.
/// </para>
/// <para>
/// <b>Base layout v2</b> (gg2-sim <c>env/env.h</c>, <c>SentryBlockObs</c>) is any
/// of those layouts <see cref="SentryColumns"/> wider: every row ends in the two
/// nearest sentries, either team's, and enemies are listed as the body sees them
/// — an enemy <see cref="BotVisibility.CanSee"/> refuses (a cloaked spy) comes
/// from the body's last sighting (<see cref="SimPolicyMemory"/>), standing still,
/// its present column fading over <see cref="SimPolicyMemory.Ticks"/>. A v2
/// observation is a call with a memory; with no hidden enemy and no sentry it is
/// the v1 row plus ten zeros.
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

    /// <summary>gg2-sim's <c>MedicObs</c>: uber/2000, ready, ubering, beaming, patient hp, patient index.</summary>
    public const int MedicColumns = 6;

    public const int MedicSize = TeamSize + MedicColumns;

    /// <summary>gg2-sim's <c>EngineerObs</c>: nuts'n'bolts/100, then own sentry present, hp/100, dx/500, dy/500.</summary>
    public const int EngineerColumns = 5;

    public const int EngineerSize = TeamSize + EngineerColumns;

    /// <summary>gg2-sim's <c>SpyObs</c>: cloak, cloak alpha, ready to stab, stab freeze left.</summary>
    public const int SpyColumns = 4;

    public const int SpySize = TeamSize + SpyColumns;

    private const int NearSentries = 2, SentryObs = 5;

    /// <summary>gg2-sim's <c>SentryBlockObs</c>: what base layout v2 adds to every layout's width.</summary>
    public const int SentryColumns = NearSentries * SentryObs;

    /// <summary>How many team-mates a team observation lists: a medic net's ally aim groups.</summary>
    public const int ListedAllies = NearAllies;

    /// <summary>
    /// The team observation for <paramref name="meSlot"/>, and the enemies it
    /// listed (nearest first, -1 past them) into <paramref name="listed"/>.
    /// <paramref name="zoom"/> appends the zoom layout's two columns,
    /// <paramref name="demo"/> the demo layout's thirty-three, <paramref name="pyro"/>
    /// the pyro layout's three, <paramref name="medic"/> the medic layout's six,
    /// <paramref name="engineer"/> the engineer layout's five, <paramref name="spy"/>
    /// the spy layout's four. The team-mates it listed go into
    /// <paramref name="listedAllies"/> when that is not empty.
    /// <para>
    /// With <paramref name="memory"/> the row is base layout v2: the body's
    /// sightings are learned at <paramref name="tick"/> (which only has to count
    /// the way the caller's other calls do), hidden enemies are listed from them,
    /// and the sentry block follows the layout's columns.
    /// </para>
    /// </summary>
    public static void Team(
        in WorldView world, int meSlot, Span<float> o, Span<int> listed, bool zoom = false, bool demo = false, bool pyro = false,
        bool medic = false, Span<int> listedAllies = default, bool engineer = false, bool spy = false,
        SimPolicyMemory? memory = null, int tick = 0)
    {
        ref readonly var me = ref world.Character(meSlot);
        ref readonly var gun = ref world.Weapon(meSlot);
        var map = world.Collision;
        var v2 = memory is not null;
        o[..(Width(zoom, demo, pyro, medic, engineer, spy) + (v2 ? SentryColumns : 0))].Clear();
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
        Span<bool> hidden = stackalloc bool[NearEnemies];
        var ne = v2 ? NearestSeen(in world, meSlot, memory!, tick, enemies, hidden) : Nearest(in world, meSlot, mates: false, enemies);
        var na = Nearest(in world, meSlot, mates: true, allies);

        for (var n = 0; n < NearEnemies; n++)
        {
            listed[n] = n < ne ? enemies[n] : -1;

            if (n >= ne)
            {
                k += CharacterObs;
                continue;
            }

            var s = enemies[n];
            ref readonly var c = ref world.Character(s);

            if (v2)
            {
                memory!.ListedX[n] = hidden[n] ? memory.X[s] : c.X;
                memory.ListedY[n] = hidden[n] ? memory.Y[s] : c.Y;
            }

            if (!hidden[n])
            {
                WriteCharacter(in me, in c, o, ref k);
                continue;
            }

            // gg2-sim env/env.h, WriteRemembered: at the sighting, still, its hp
            // then, present fading with the sighting's age.
            o[k] = Clip((memory!.X[s] - me.X) / 500, 4);
            o[k + 1] = Clip((memory.Y[s] - me.Y) / 500, 4);
            o[k + 4] = (float)(memory.Hp[s] / ClassStats.For(c.Class).MaxHp);
            o[k + 5] = (float)(1 - ((double)(tick - memory.At[s]) / SimPolicyMemory.Ticks));
            o[k + 6 + (int)c.Class] = 1;
            k += CharacterObs;
        }

        for (var n = 0; n < NearAllies; n++)
        {
            if (!listedAllies.IsEmpty)
                listedAllies[n] = n < na ? allies[n] : -1;

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
        else if (medic)
        {
            k += ClassCount;
            var patient = gun.HealTarget;
            var beaming = patient != WeaponState.NoTarget && world.IsAlive(patient);
            o[k++] = (float)(gun.UberCharge / WeaponState.MaxUberCharge);
            o[k++] = gun.UberReady ? 1 : 0;
            o[k++] = gun.Ubering ? 1 : 0;
            o[k++] = beaming ? 1 : 0;
            o[k++] = beaming ? (float)(world.Character(patient).Hp / ClassStats.For(world.Character(patient).Class).MaxHp) : 0;

            var index = -1f;
            for (var n = 0; n < na; n++)
            {
                if (beaming && allies[n] == patient)
                    index = n;
            }

            o[k++] = index;
        }
        else if (engineer)
        {
            k += ClassCount;
            o[k++] = (float)(me.NutsNBolts / CharacterState.MaxNutsNBolts);
            var own = world.State.HasSentry[meSlot];
            ref readonly var sentry = ref world.State.Sentries[meSlot];
            o[k++] = own ? 1 : 0;
            o[k++] = own ? (float)(sentry.Hp / SentryState.MaxHp) : 0;
            o[k++] = own ? Clip((sentry.X - me.X) / 500, 4) : 0;
            o[k++] = own ? Clip((sentry.Y - me.Y) / 500, 4) : 0;
        }
        else if (spy)
        {
            k += ClassCount;
            o[k++] = me.Cloak ? 1 : 0;
            o[k++] = (float)me.CloakAlpha;
            o[k++] = gun.ReadyToStab ? 1 : 0;
            o[k++] = (float)((double)me.StabFreezeTimer / Weapons.StabFreezeTicks);
        }
        else
        {
            k += ClassCount;
        }

        if (v2)
            WriteSentries(in world, in me, o, k);
    }

    private static int Width(bool zoom, bool demo, bool pyro, bool medic, bool engineer, bool spy) =>
        zoom ? ZoomSize : demo ? DemoSize : pyro ? PyroSize : medic ? MedicSize : engineer ? EngineerSize : spy ? SpySize : TeamSize;

    /// <summary>
    /// A v2 body's sightings, learned as an observation would learn them without
    /// writing one: gg2-sim's <c>PolicyDriver.Sight</c>, for a body the scripted
    /// layer is driving, since gg2-sim's environment observes every row every step.
    /// </summary>
    public static void Sight(in WorldView world, int meSlot, SimPolicyMemory memory, int tick)
    {
        if (!world.IsAlive(meSlot))
            return;

        Span<int> enemies = stackalloc int[NearEnemies];
        Span<bool> hidden = stackalloc bool[NearEnemies];
        NearestSeen(in world, meSlot, memory, tick, enemies, hidden);
    }

    // gg2-sim env/env.h, NearestSeen: each live enemy the body can see recorded as
    // sighted at tick and a dead slot forgotten; then the output.Length nearest of
    // those it can see and those it remembers within SimPolicyMemory.Ticks (at the
    // sighting), nearest first, ties to the lower slot.
    private static int NearestSeen(in WorldView world, int slot, SimPolicyMemory m, int tick, Span<int> output, Span<bool> hidden)
    {
        ref readonly var me = ref world.Character(slot);
        var count = output.Length;
        Span<double> distances = stackalloc double[count];
        var found = 0;

        for (var s = 1; s < WorldState.MaxSlots; s++)
        {
            if (s == slot)
                continue;

            if (!world.IsAlive(s))
            {
                m.Seen[s] = false;
                continue;
            }

            ref readonly var c = ref world.Character(s);

            // MayOppose is not in gg2-sim's ref: a no-op unless the view carries an
            // opponents table, which only the duel server builds.
            if (c.Team == me.Team || !world.MayOppose(slot, s))
                continue;

            var visible = BotVisibility.CanSee(in me, in c);

            if (visible)
            {
                m.Seen[s] = true;
                m.X[s] = c.X;
                m.Y[s] = c.Y;
                m.Hp[s] = c.Hp;
                m.At[s] = tick;
            }
            else if (!m.Seen[s] || tick - m.At[s] >= SimPolicyMemory.Ticks)
            {
                continue;
            }

            double dx = m.X[s] - me.X, dy = m.Y[s] - me.Y, d = (dx * dx) + (dy * dy);
            var at = Math.Min(found, count);

            while (at > 0 && d < distances[at - 1])
                at--;

            if (at >= count)
                continue;

            for (var i = Math.Min(found, count - 1); i > at; i--)
            {
                output[i] = output[i - 1];
                hidden[i] = hidden[i - 1];
                distances[i] = distances[i - 1];
            }

            output[at] = s;
            hidden[at] = !visible;
            distances[at] = d;

            if (found < count)
                found++;
        }

        return found;
    }

    // gg2-sim env/env.h, WriteSentries: the two nearest sentries of either team by
    // squared distance, ties to the lower owner slot, from o[k] on.
    private static void WriteSentries(in WorldView world, in CharacterState me, Span<float> o, int k)
    {
        Span<int> near = stackalloc int[NearSentries];
        Span<double> distances = stackalloc double[NearSentries];
        var found = 0;

        for (var s = 0; s < WorldState.MaxSlots; s++)
        {
            if (!world.State.HasSentry[s])
                continue;

            ref readonly var sentry = ref world.State.Sentries[s];
            double dx = sentry.X - me.X, dy = sentry.Y - me.Y, d = (dx * dx) + (dy * dy);
            var at = found;

            while (at > 0 && d < distances[at - 1])
                at--;

            if (at >= NearSentries)
                continue;

            for (var i = Math.Min(found, NearSentries - 1); i > at; i--)
            {
                near[i] = near[i - 1];
                distances[i] = distances[i - 1];
            }

            near[at] = s;
            distances[at] = d;

            if (found < NearSentries)
                found++;
        }

        for (var n = 0; n < found; n++)
        {
            ref readonly var sentry = ref world.State.Sentries[near[n]];
            var at = k + (n * SentryObs);
            o[at] = Clip((sentry.X - me.X) / 500, 4);
            o[at + 1] = Clip((sentry.Y - me.Y) / 500, 4);
            o[at + 2] = (float)(sentry.Hp / SentryState.MaxHp);
            o[at + 3] = sentry.Team != me.Team ? 1 : 0;
            o[at + 4] = 1;
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
    /// not, nearest first by squared distance, ties to the lower slot; with
    /// <paramref name="seen"/>, only those the body can see.
    /// </summary>
    public static int Nearest(in WorldView world, int slot, bool mates, Span<int> output, bool seen = false)
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

            if (seen && !BotVisibility.CanSee(in me, in c))
                continue;

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

/// <summary>
/// One body's sightings for a base layout v2 net: gg2-sim's
/// <c>PolicyDriver.SpyMemory</c> (<c>env/env.h</c>, <c>EnvState::Sightings</c>,
/// <c>ListedX</c> and <c>ListedY</c>).
/// </summary>
/// <remarks>
/// Indexed by roster slot. A departure renumbers the roster and this does not
/// follow it, so for up to <see cref="Ticks"/> ticks after somebody leaves an
/// entry can describe whoever moved into that slot. It is read only for an enemy
/// the body cannot see now (a cloaked spy), so the cost is one stale remembered
/// row; <c>docs/BOTS.md</c>, "gg2-sim policies", records it.
/// </remarks>
public sealed class SimPolicyMemory
{
    /// <summary>gg2-sim's <c>SpyMemoryTicks</c>: how long a hidden enemy stays listed after its last sighting.</summary>
    public const int Ticks = 60;

    public bool[] Seen { get; } = new bool[WorldState.MaxSlots];

    public double[] X { get; } = new double[WorldState.MaxSlots];

    public double[] Y { get; } = new double[WorldState.MaxSlots];

    public double[] Hp { get; } = new double[WorldState.MaxSlots];

    public int[] At { get; } = new int[WorldState.MaxSlots];

    /// <summary>Where the last observation listed each enemy: the aim's point at a hidden one.</summary>
    public double[] ListedX { get; } = new double[SimPolicyObservation.ListedEnemies];

    /// <inheritdoc cref="ListedX"/>
    public double[] ListedY { get; } = new double[SimPolicyObservation.ListedEnemies];

    /// <summary>Forgets every sighting, as for a body that has moved to another roster slot.</summary>
    public void Clear() => Array.Clear(Seen);
}
