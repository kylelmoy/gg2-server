using System.Collections.Generic;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// Two nodes running one match, a link apart.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim under test is convergence</b>: each node hears its own players
/// at once and the other's a few ticks late, guesses at what it has not heard,
/// and ends up in the same world as the other once both know the same inputs.
/// If that fails there is no mesh, whatever else works.
/// </para>
/// <para>
/// ⛔ <b>Every convergence test here is paired with one that must NOT
/// converge.</b> Two simulations fed the same inputs agree trivially, so a test
/// that only asserted agreement would pass just as well against a node that
/// ignored the link, ignored the guesses and never rewound. The control turns
/// prediction off and asserts the digests DIFFER — if that ever starts passing,
/// this file has stopped testing rollback and is testing determinism twice.
/// </para>
/// </remarks>
public sealed class MeshNodeTests
{
    private const int Local = 0;
    private const int Remote = 1;

    private static CollisionWorld Collision() =>
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    /// <summary>Two characters on opposite sides of the open stretch.</summary>
    private static WorldState World()
    {
        var state = default(WorldState);

        Place(ref state, Local, Team.Red, 380, 268);
        Place(ref state, Remote, Team.Blue, 680, 268);

        return state;
    }

    private static void Place(ref WorldState state, int slot, Team team, double x, double y)
    {
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = PlayerClass.Soldier;

        var character = CharacterState.Fresh(PlayerClass.Soldier, team);
        character.X = x;
        character.Y = y;
        character.OnGround = true;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Soldier, x, y);
        state.Alive[slot] = true;
    }

    /// <summary>
    /// A scripted stream of keys per tick, seeded so both nodes and the
    /// reference see the same match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It has to CHANGE, or nothing here tests anything.</b> A player who
    /// holds one key for the whole test is predicted perfectly, never
    /// contradicts a guess and never provokes a rollback, so a script that
    /// rarely changed would make every assertion pass with the rollback path
    /// never running. <see cref="TheScriptActuallyProvokesRollbacks"/> is what
    /// stops that happening silently.
    /// </para>
    /// <para>
    /// ⚠️ <b>And it has to change at a HUMAN rate, which is every five ticks or
    /// so</b> — measured, `tools/inputpredict`. A script that changed every tick
    /// would make "they are still holding what they were holding" wrong as often
    /// as "they are pressing nothing", and the difference between a good guess
    /// and no guess — which is the thing worth knowing — would vanish into the
    /// script.
    /// </para>
    /// </remarks>
    private const int HoldTicks = 5;

    private static PlayerKeys KeysFor(int slot, int tick)
    {
        // Offset per slot so the two players do not change keys on the same
        // ticks, which would be a tidier world than any real one.
        var segment = (uint)((tick + (slot * 2)) / HoldTicks);
        var mix = (segment * 2654435761u) + ((uint)slot * 40503u);

        return (mix % 5) switch
        {
            0 => PlayerKeys.Left,
            1 => PlayerKeys.Right,
            2 => PlayerKeys.Right | PlayerKeys.Jump,
            3 => PlayerKeys.Left | PlayerKeys.Attack,
            _ => PlayerKeys.None,
        };
    }

    /// <summary>
    /// ⚠️ The aim is held across a segment too. An aim that moved every tick
    /// would contradict the guess on every tick by itself, which is the same
    /// way a too-busy key script hides what the guess is worth.
    /// </summary>
    private static PlayerInput InputFor(int slot, int tick) =>
        new(
            KeysFor(slot, tick),
            (ushort)(((tick + (slot * 2)) / HoldTicks * 2411) % 65536),
            40);

    /// <summary>
    /// The answer both nodes have to reach: one simulation that was told
    /// everything on time.
    /// </summary>
    private static ulong Reference(int ticks)
    {
        var state = World();
        var world = Collision();
        var inputs = new PlayerInput[WorldState.MaxSlots];

        for (var tick = 0; tick < ticks; tick++)
        {
            inputs[Local] = InputFor(Local, tick);
            inputs[Remote] = InputFor(Remote, tick);

            MatchSimulation.Tick(world, ref state, inputs);
        }

        return StateDigest.Of(in state);
    }

    /// <summary>
    /// Runs both nodes for <paramref name="ticks"/> ticks with each one's own
    /// inputs immediate and the peer's <paramref name="delay"/> ticks late, then
    /// drains the link so both have heard everything.
    /// </summary>
    private static (MeshNode A, MeshNode B) Run(
        int ticks, int delay, bool predict = true, bool rollback = true)
    {
        var options = new MeshOptions { Predict = predict, Rollback = rollback };

        var a = new MeshNode(Collision(), World(), options);
        var b = new MeshNode(Collision(), World(), options);

        // What is still on the wire. `At` is when it lands, `For` is the tick it
        // describes — keeping them apart is the whole point of a link.
        var toA = new List<(int At, int For, int Slot, PlayerInput Input)>();
        var toB = new List<(int At, int For, int Slot, PlayerInput Input)>();

        for (var tick = 0; tick < ticks; tick++)
        {
            // Each node's own player is known the instant it is pressed.
            a.Submit(tick, Local, InputFor(Local, tick));
            b.Submit(tick, Remote, InputFor(Remote, tick));

            // ...and the peer's is in flight.
            toB.Add((tick + delay, tick, Local, InputFor(Local, tick)));
            toA.Add((tick + delay, tick, Remote, InputFor(Remote, tick)));

            Deliver(a, toA, tick);
            Deliver(b, toB, tick);

            a.Advance();
            b.Advance();
        }

        // Everything still in flight lands, and each node reconciles what it
        // just learned. ⚠️ Settle rather than Advance: a further tick would have
        // no inputs at all, so each node would guess at it alone and two nodes
        // that agree about the match would still differ about that one tick.
        DrainAll(a, toA);
        DrainAll(b, toB);

        a.Settle();
        b.Settle();

        return (a, b);
    }

    private static void Deliver(
        MeshNode node, List<(int At, int For, int Slot, PlayerInput Input)> wire, int now)
    {
        for (var i = wire.Count - 1; i >= 0; i--)
        {
            if (wire[i].At > now)
                continue;

            node.Submit(wire[i].For, wire[i].Slot, wire[i].Input);
            wire.RemoveAt(i);
        }
    }

    private static void DrainAll(
        MeshNode node, List<(int At, int For, int Slot, PlayerInput Input)> wire)
    {
        foreach (var (_, forTick, slot, input) in wire)
            node.Submit(forTick, slot, input);

        wire.Clear();
    }

    [Fact]
    public void TwoNodesConvergeAcrossADelayedLink()
    {
        ReferenceTree.RequireAvailable();

        var (a, b) = Run(ticks: 180, delay: 3);

        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(a.Digest, b.Digest);
        Assert.Equal(Reference(a.Tick), a.Digest);
    }

    /// <summary>
    /// ⛔ The control. Without the rewind, each node keeps whatever it guessed
    /// and the two stay in different worlds.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="TwoNodesConvergeAcrossADelayedLink"/> would pass
    /// against an implementation that threw the link away entirely — two copies
    /// of a deterministic simulation fed identical local inputs agree by
    /// construction, and the agreement would say nothing about rollback.
    /// </remarks>
    [Fact]
    public void WithoutRollbackTheyDoNotConverge()
    {
        ReferenceTree.RequireAvailable();

        var (a, b) = Run(ticks: 180, delay: 3, rollback: false);

        Assert.NotEqual(a.Digest, b.Digest);
        Assert.Equal(0, a.Stats.Rollbacks);
    }

    /// <summary>
    /// A worse guess costs more rewinding and reaches the same world.
    /// </summary>
    /// <remarks>
    /// <b>The separation that matters:</b> correctness comes from the rollback,
    /// and the prediction only buys work. A node that guessed nothing at all
    /// still converges — it just rewinds far more often to get there — so a
    /// better predictor is a performance and smoothness question, never a
    /// correctness one.
    /// </remarks>
    [Fact]
    public void AWorseGuessCostsMoreRewindingAndStillConverges()
    {
        ReferenceTree.RequireAvailable();

        var (good, _) = Run(ticks: 180, delay: 3);
        var (blind, blindPeer) = Run(ticks: 180, delay: 3, predict: false);

        Assert.Equal(blind.Digest, blindPeer.Digest);
        Assert.Equal(good.Digest, blind.Digest);

        Assert.True(
            blind.Stats.ResimulatedTicks > good.Stats.ResimulatedTicks,
            $"guessing nothing re-simulated {blind.Stats.ResimulatedTicks} ticks against "
            + $"{good.Stats.ResimulatedTicks} for repeat-last-key, so the guess bought nothing");
    }

    /// <summary>
    /// The script provokes real rollbacks, so the convergence above is the
    /// rollback path working rather than never running.
    /// </summary>
    [Fact]
    public void TheScriptActuallyProvokesRollbacks()
    {
        ReferenceTree.RequireAvailable();

        var (a, _) = Run(ticks: 180, delay: 3);
        var stats = a.Stats;

        Assert.True(stats.Rollbacks > 20, $"only {stats.Rollbacks} rollback(s): {stats}");
        Assert.True(stats.DeepestRollback >= 3, $"deepest rollback {stats.DeepestRollback}: {stats}");
        Assert.Equal(0, stats.TooLate);
    }

    /// <summary>
    /// A link with no delay never contradicts a guess, so it never rewinds —
    /// the cost of the machinery is zero when it is not needed.
    /// </summary>
    [Fact]
    public void AnImmediateLinkNeverRollsBack()
    {
        ReferenceTree.RequireAvailable();

        var (a, b) = Run(ticks: 120, delay: 0);

        Assert.Equal(0, a.Stats.Rollbacks);
        Assert.Equal(a.Digest, b.Digest);
        Assert.Equal(Reference(a.Tick), a.Digest);
    }

    /// <summary>
    /// An input older than the rollback window is reported rather than applied
    /// to a world that can no longer be rewound to it.
    /// </summary>
    /// <remarks>
    /// <b>The honest failure.</b> A node that silently ignored one would go on
    /// running in a world its peer has left, and the divergence would surface
    /// later as a physics bug. Counted, it is a link telling an operator that
    /// its window is too small.
    /// </remarks>
    [Fact]
    public void AnInputOlderThanTheWindowIsCounted()
    {
        ReferenceTree.RequireAvailable();

        var node = new MeshNode(Collision(), World(), new MeshOptions { MaxRollbackTicks = 8 });

        node.Advance(40);
        Assert.Equal(0, node.Stats.TooLate);

        node.Submit(1, Remote, InputFor(Remote, 1));

        Assert.Equal(1, node.Stats.TooLate);
        Assert.Equal(0, node.Stats.Rollbacks);
    }

    /// <summary>
    /// A rewind restores the whole world, not just the characters: the same
    /// inputs re-run from a restored snapshot reach the same digest.
    /// </summary>
    /// <remarks>
    /// The direct check on the premise the whole type rests on — a
    /// <c>WorldState</c> copy is a whole world. If it ever became a view onto
    /// shared mutable parts, the re-run would drift from the original and this
    /// is what would say so.
    /// </remarks>
    [Fact]
    public void ARewindReproducesTheWorldItReplaced()
    {
        ReferenceTree.RequireAvailable();

        // ⚠️ Every Submit here stays inside the node's window. A node refuses
        // anything further ahead than it can hold — there is nowhere to put it
        // without overwriting history a rewind needs — so submitting sixty ticks
        // up front would silently measure a node that never heard most of them.
        var straight = new MeshNode(Collision(), World());

        for (var tick = 0; tick < 60; tick++)
        {
            straight.Submit(tick, Local, InputFor(Local, tick));
            straight.Submit(tick, Remote, InputFor(Remote, tick));
            straight.Advance();
        }

        // The same match, where slot Remote's inputs arrive twenty ticks late
        // and contradict the guess every time.
        var rewound = new MeshNode(Collision(), World());

        for (var tick = 0; tick < 60; tick++)
        {
            rewound.Submit(tick, Local, InputFor(Local, tick));

            if (tick >= 20)
                rewound.Submit(tick - 20, Remote, InputFor(Remote, tick - 20));

            rewound.Advance();
        }

        for (var tick = 40; tick < 60; tick++)
            rewound.Submit(tick, Remote, InputFor(Remote, tick));

        rewound.Settle();

        Assert.True(rewound.Stats.Rollbacks > 0, "the rewound node never rewound");
        Assert.Equal(straight.Tick, rewound.Tick);
        Assert.Equal(straight.Digest, rewound.Digest);
    }

}
