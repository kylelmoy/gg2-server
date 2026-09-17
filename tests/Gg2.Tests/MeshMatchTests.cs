using System.Collections.Generic;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Nav;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// A whole match — twelve bots, projectiles, deaths, an objective — played
/// across two nodes a link apart, each driving its own players.
/// </summary>
/// <remarks>
/// <para>
/// <b>The difference from <see cref="MeshNodeTests"/> is where the inputs come
/// from.</b> There they are a script, chosen to provoke rollbacks. Here each
/// node's bots decide from the world that node currently believes in — which is
/// a world containing guesses about the other region — exactly as a person
/// decides from what their own screen shows them. That is the real loop, and it
/// is the one place a mesh could be circular: a guess feeds a decision, which
/// becomes an input, which the peer then has to be told about.
/// </para>
/// <para>
/// It is not circular, and the reason is worth stating: <b>an input is
/// authoritative once submitted, however it was arrived at.</b> A node that
/// guessed badly produces different inputs from the ones it would have produced
/// knowing everything — but those inputs are what that player really did, and
/// the peer applies them rather than second-guessing them. Both nodes then hold
/// the same ledger and reach the same world.
/// </para>
/// <para>
/// ⚠️ <b>A rewind restores the world, not the players' minds.</b> A bot polled
/// during a tick that is later re-simulated keeps whatever its own memory
/// became; only the world goes back. That is right rather than tolerated — a
/// person whose screen is corrected does not forget the last second either — and
/// it is why a node never re-polls a local input it has already recorded.
/// </para>
/// </remarks>
public sealed class MeshMatchTests
{
    private const string Map = "ctf_truefort";
    private const int Bots = 12;
    private const int Delay = 3;

    /// <summary>
    /// ⚠️ Slot 0 is the server's own reserved player and never a bot — CLAUDE.md.
    /// The twelve players are slots 1 to 12.
    /// </summary>
    private const int First = 1;

    /// <summary>One region's players: even slots to one node, odd to the other.</summary>
    private static bool IsLocalTo(int node, int slot) => slot % 2 == node;

    private sealed class Region
    {
        public required MeshNode Node { get; init; }

        public required CollisionWorld Collision { get; init; }

        /// <summary>This region's own bots, by slot. Nobody else's.</summary>
        public required Dictionary<int, ScriptedBot> Bots { get; init; }

        /// <summary>Inputs on the wire to this region, as (arrival, tick, slot, input).</summary>
        public List<(int At, int For, int Slot, PlayerInput Input)> Wire { get; } = [];
    }

    /// <summary>
    /// A match whose roster is fixed before the first tick, so both nodes start
    /// from the same world and no join has to be sequenced across the link.
    /// </summary>
    /// <remarks>
    /// <b>Joins are deliberately out of scope here.</b> A roster change is a
    /// non-input event and needs an agreed order across nodes — `docs/MESH.md`
    /// says so and says it is unwritten. Starting from a full roster is what
    /// lets this test be about the rollback loop and nothing else.
    /// </remarks>
    private static WorldState Roster(SpawnPoints spawns)
    {
        var state = default(WorldState);

        state.AddPlayer(0);

        for (var slot = First; slot < First + Bots; slot++)
        {
            var team = slot % 2 == 0 ? Team.Red : Team.Blue;

            state.AddPlayer(slot);
            state.Players[slot].IsBot = true;
            state.Players[slot].Team = team;
            state.Players[slot].Class = PlayerClass.Soldier;
            state.Spawn(spawns, slot, PlayerClass.Soldier, team, spawnpointId: 0, group: 0);
        }

        return state;
    }

    private static Region Build(int node, LevelMap map, NavGraph nav, SpawnPoints spawns)
    {
        var collision = new CollisionWorld(map);
        var bots = new Dictionary<int, ScriptedBot>();

        for (var slot = First; slot < First + Bots; slot++)
        {
            if (!IsLocalTo(node, slot))
                continue;

            var team = slot % 2 == 0 ? Team.Red : Team.Blue;
            var bot = new ScriptedBot(nav, new NavQuery(team), BotSkill.ForTier(3), slot);

            // Without a role a bot has no objective and stands where it spawned,
            // which is a match that ends rather than one that happens.
            var earlier = new List<PlayerClass>();

            for (var other = First; other < slot; other++)
            {
                if ((other % 2 == 0 ? Team.Red : Team.Blue) == team)
                    earlier.Add(PlayerClass.Soldier);
            }

            bot.Assign(slot, team, PlayerClass.Soldier, earlier.ToArray(), nav, spawns);
            bots[slot] = bot;
        }

        return new Region
        {
            Node = new MeshNode(collision, Roster(spawns), new MeshOptions(), spawns),
            Collision = collision,
            Bots = bots,
        };
    }

    private static void Deliver(Region region, int now)
    {
        for (var i = region.Wire.Count - 1; i >= 0; i--)
        {
            if (region.Wire[i].At > now)
                continue;

            region.Node.Submit(region.Wire[i].For, region.Wire[i].Slot, region.Wire[i].Input);
            region.Wire.RemoveAt(i);
        }
    }

    private static (Region A, Region B) Play(int ticks)
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);
        var nav = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var spawns = new SpawnPoints(map);

        var a = Build(0, map, nav, spawns);
        var b = Build(1, map, nav, spawns);

        for (var tick = 0; tick < ticks; tick++)
        {
            Poll(a, b, tick);
            Poll(b, a, tick);

            Deliver(a, tick);
            Deliver(b, tick);

            a.Node.Advance();
            b.Node.Advance();
        }

        // Everything still in flight lands, and each node reconciles what it
        // just learned — without stepping a tick neither has inputs for.
        foreach (var (_, forTick, slot, input) in a.Wire)
            a.Node.Submit(forTick, slot, input);

        foreach (var (_, forTick, slot, input) in b.Wire)
            b.Node.Submit(forTick, slot, input);

        a.Wire.Clear();
        b.Wire.Clear();

        a.Node.Settle();
        b.Node.Settle();

        return (a, b);
    }

    /// <summary>
    /// Asks a region's own bots what they are doing, records it, and puts it on
    /// the wire to the peer.
    /// </summary>
    private static void Poll(Region region, Region peer, int tick)
    {
        foreach (var (slot, bot) in region.Bots)
        {
            // A dead player presses nothing anybody needs to hear about, and
            // polling one would ask a bot to steer a character that is not there.
            if (!region.Node.State.IsAlive(slot))
                continue;

            var input = bot.Poll(new WorldView(in region.Node.State, region.Collision), slot, tick);

            region.Node.Submit(tick, slot, input);
            peer.Wire.Add((tick + Delay, tick, slot, input));
        }
    }

    [Fact]
    public void AWholeMatchConvergesAcrossADelayedLink()
    {
        var (a, b) = Play(ticks: 600);

        Assert.Equal(a.Node.Tick, b.Node.Tick);
        Assert.Equal(a.Node.Digest, b.Node.Digest);
        Assert.Equal(0, a.Node.Stats.TooLate);
        Assert.Equal(0, b.Node.Stats.TooLate);
    }

    /// <summary>
    /// And the match was a real one: bots moved, fought and died, so the
    /// agreement above is about a world where something happened.
    /// </summary>
    /// <remarks>
    /// <b>CLAUDE.md's rule, applied to a mesh:</b> a match that ends is not a
    /// match that happened. Two nodes agreeing about twelve characters standing
    /// still on their spawn points would satisfy every other assertion here.
    /// </remarks>
    [Fact]
    public void TheMatchActuallyHappened()
    {
        var (a, _) = Play(ticks: 600);

        var moved = 0;
        var damaged = 0;

        for (var slot = First; slot < First + Bots; slot++)
        {
            if (!a.Node.State.IsAlive(slot))
                continue;

            if (a.Node.State.Characters[slot].HSpeed != 0
                || a.Node.State.Characters[slot].VSpeed != 0)
            {
                moved++;
            }

            if (a.Node.State.Characters[slot].Hp < 100)
                damaged++;
        }

        Assert.True(moved > 0, "nobody was moving after 600 ticks");
        Assert.True(damaged > 0, "nobody had taken a hit after 600 ticks");
        Assert.True(a.Node.Stats.Rollbacks > 0, $"nothing was ever rewound: {a.Node.Stats}");
    }

    /// <summary>
    /// What the rollback costs, on a real match rather than a script.
    /// </summary>
    /// <remarks>
    /// <b>The number a deployment needs.</b> A node re-simulates ticks it has
    /// already simulated, so its tick budget is multiplied by whatever this
    /// reports — and a twelve-bot tick costs about 0.151ms in Release against a
    /// 33.3ms budget, so even several times over is a small fraction of it. The
    /// bound is deliberately loose: it is here to catch a change that makes a
    /// node rewind the whole window every tick, not to pin a tuning number.
    /// <para>
    /// Measured at the time of writing: 600 live ticks cost 2,127 simulated,
    /// from 509 rollbacks re-simulating 1,527 ticks, deepest 3. That is 0.72
    /// extra ticks per tick, about 1.7x a plain server, and the deepest rewind
    /// is exactly the link's delay. ⚠️ Bots change keys about twice as often as
    /// people, so this is the pessimistic case.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRollbackCostIsBounded()
    {
        var (a, _) = Play(ticks: 600);
        var stats = a.Node.Stats;

        var perTick = (double)stats.ResimulatedTicks / stats.Ticks;

        Assert.True(
            perTick < Delay + 1,
            $"re-simulated {perTick:0.00} extra tick(s) per tick, which is more than a "
            + $"{Delay}-tick link can explain: {stats}");

        Assert.True(
            stats.DeepestRollback <= Delay + 1,
            $"deepest rollback {stats.DeepestRollback} exceeds the link's {Delay} ticks: {stats}");
    }
}
