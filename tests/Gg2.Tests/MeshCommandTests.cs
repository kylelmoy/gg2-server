using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// Somebody joining, leaving or changing class, on two nodes at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the events a mesh cannot guess at.</b> An unheard key press is
/// predicted and corrected; an unheard join cannot be — either slot 4 exists or
/// it does not, and two nodes that disagree are not running the same match.
/// So a roster change is scheduled for a future tick both nodes will reach with
/// it already in hand.
/// </para>
/// <para>
/// ⛔ The gate that matters is the third one: a command inside a rewound window
/// has to survive the rewind. That is the whole reason a schedule is keyed by
/// tick rather than applied when it arrives.
/// </para>
/// </remarks>
public sealed class MeshCommandTests
{
    private static CollisionWorld Collision() =>
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    private static SpawnPoints Spawns() =>
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1));

    /// <summary>One player already in the match, so there is something to simulate.</summary>
    private static WorldState World()
    {
        var state = default(WorldState);

        state.AddPlayer(0);
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;
        state.Players[1].Class = PlayerClass.Soldier;

        var character = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
        character.X = 380;
        character.Y = 268;
        character.OnGround = true;

        state.Characters[1] = character;
        state.Weapons[1] = WeaponState.Spawn(PlayerClass.Soldier, 380, 268);
        state.Alive[1] = true;

        return state;
    }

    private static MeshNode Node() => new(Collision(), World(), new MeshOptions(), Spawns());

    private static PlayerInput Input(int tick) =>
        new((tick / 5) % 2 == 0 ? PlayerKeys.Left : PlayerKeys.Right, (ushort)(tick * 13), 40);

    /// <summary>
    /// A join lands on the tick it was scheduled for, not the tick it arrived.
    /// </summary>
    [Fact]
    public void ACommandLandsOnItsScheduledTick()
    {
        ReferenceTree.RequireAvailable();

        var node = Node();

        Assert.True(node.Schedule(10, MeshCommand.AddPlayer(origin: 0, sequence: 2)));
        Assert.True(node.Schedule(10, MeshCommand.SetTeam(2, Team.Blue)));

        node.Advance(10);
        Assert.False(node.State.HasPlayer[2], "the join was applied before its tick");

        node.Advance();

        Assert.True(node.State.HasPlayer[2], "the join never happened");
        Assert.Equal(Team.Blue, node.State.Players[2].Team);
    }

    /// <summary>
    /// Two nodes told the same schedule reach the same world, even though each
    /// heard the other's players late.
    /// </summary>
    [Fact]
    public void TwoNodesAgreeAboutAJoinAcrossADelayedLink()
    {
        ReferenceTree.RequireAvailable();

        var a = Node();
        var b = Node();

        const int Delay = 3;
        const int JoinAt = 20;

        // A decides somebody is joining and tells B, which hears it in time
        // because the tick is far enough ahead.
        foreach (var node in new[] { a, b })
        {
            node.Schedule(JoinAt, MeshCommand.AddPlayer(origin: 0, sequence: 2));
            node.Schedule(JoinAt, MeshCommand.SetTeam(2, Team.Blue));
            node.Schedule(JoinAt, MeshCommand.SetClass(2, PlayerClass.Scout));
            node.Schedule(JoinAt, MeshCommand.Spawn(2));
        }

        for (var tick = 0; tick < 60; tick++)
        {
            a.Submit(tick, 1, Input(tick));
            b.Submit(tick, 1, Input(tick));

            // Slot 2 starts pressing things once it exists, and each node hears
            // the other's copy of it late.
            if (tick >= JoinAt)
            {
                a.Submit(tick, 2, Input(tick + 7));

                if (tick >= JoinAt + Delay)
                    b.Submit(tick - Delay, 2, Input(tick - Delay + 7));
            }

            a.Advance();
            b.Advance();
        }

        for (var tick = 60 - Delay; tick < 60; tick++)
            b.Submit(tick, 2, Input(tick + 7));

        b.Settle();
        a.Settle();

        Assert.True(a.State.HasPlayer[2]);
        Assert.Equal(a.Digest, b.Digest);
    }

    /// <summary>
    /// ⛔ Two nodes told the same thing at different moments still apply it at
    /// the same tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The real case, and the one that catches applying on arrival.</b> The
    /// node that decides somebody is joining knows first; its peer hears a link
    /// later. If a command took effect when it arrived, those two nodes would
    /// apply the same join several ticks apart and simulate different worlds
    /// from there on — with nothing to bring them back, because a roster
    /// disagreement is not a mispredicted key.
    /// </para>
    /// <para>
    /// ⚠️ An earlier version of this test asserted only that the player still
    /// existed after a rewind. That passes whether commands are scheduled or
    /// applied on arrival — both arms are wrong in the same way, so the digests
    /// agree. Mutation-checked: this version fails when the schedule is
    /// bypassed, and the old one did not.
    /// </para>
    /// </remarks>
    [Fact]
    public void HearingAboutAJoinLateDoesNotMoveIt()
    {
        ReferenceTree.RequireAvailable();

        var early = Node();
        var late = Node();

        const int JoinAt = 20;

        MeshCommand[] join =
        [
            MeshCommand.AddPlayer(origin: 0, sequence: 2),
            MeshCommand.SetTeam(2, Team.Blue),
            MeshCommand.SetClass(2, PlayerClass.Scout),
            MeshCommand.Spawn(2),
        ];

        // One node knows at tick 0; the other is told at tick 10, as a peer a
        // link away would be.
        foreach (var command in join)
            early.Schedule(JoinAt, command);

        for (var tick = 0; tick < 40; tick++)
        {
            early.Submit(tick, 1, Input(tick));
            late.Submit(tick, 1, Input(tick));

            if (tick == 10)
            {
                foreach (var command in join)
                    late.Schedule(JoinAt, command);
            }

            early.Advance();
            late.Advance();

            // ⚠️ The digest alone cannot see this. A character that spawns with
            // nobody pressing anything settles to the same resting place within
            // a few ticks, so two nodes that spawned it ten ticks apart agree
            // again by tick 40 — measured, and it is why the timing is asserted
            // while it is still observable rather than only at the end.
            var present = tick + 1 > JoinAt;

            Assert.Equal(present, early.State.HasPlayer[2]);
            Assert.Equal(present, late.State.HasPlayer[2]);
        }

        Assert.True(early.State.HasPlayer[2], "the join never happened");
        Assert.Equal(early.Digest, late.Digest);
    }

    /// <summary>
    /// A command inside a rewound window survives the rewind.
    /// </summary>
    /// <remarks>
    /// Kept for what it does prove — that a replay re-applies the schedule
    /// rather than losing it — while <see cref="HearingAboutAJoinLateDoesNotMoveIt"/>
    /// is the one that catches a command taking effect when it arrived.
    /// </remarks>
    [Fact]
    public void AJoinSurvivesARewindThroughItsTick()
    {
        ReferenceTree.RequireAvailable();

        var node = Node();

        node.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 2));
        node.Schedule(5, MeshCommand.SetTeam(2, Team.Blue));
        node.Schedule(5, MeshCommand.SetClass(2, PlayerClass.Scout));
        node.Schedule(5, MeshCommand.Spawn(2));

        // Run past the join, guessing at slot 1 the whole way.
        node.Advance(12);

        Assert.True(node.State.HasPlayer[2], "the join never happened");
        var before = node.Stats.Rollbacks;

        // Now the truth about tick 4 arrives and contradicts the guess, forcing
        // a rewind back through the tick the join was on.
        node.Submit(4, 1, Input(4));
        node.Advance();

        Assert.True(node.Stats.Rollbacks > before, "nothing was rewound, so nothing was tested");
        Assert.True(node.State.HasPlayer[2], "the rewind erased a player who had joined");
        Assert.Equal(Team.Blue, node.State.Players[2].Team);
        Assert.Equal(PlayerClass.Scout, node.State.Players[2].Class);
    }

    /// <summary>
    /// ...and it is applied exactly once, not once per replay.
    /// </summary>
    /// <remarks>
    /// The other half of the same bug. A command applied on top of the world it
    /// had already changed — rather than on the snapshot taken before it — would
    /// re-run on every rewind, and a spawn would re-spawn a character that had
    /// since moved or died.
    /// </remarks>
    [Fact]
    public void ARewoundCommandIsNotAppliedTwice()
    {
        ReferenceTree.RequireAvailable();

        var node = Node();

        node.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 2));
        node.Schedule(5, MeshCommand.SetTeam(2, Team.Blue));
        node.Schedule(5, MeshCommand.SetClass(2, PlayerClass.Scout));
        node.Schedule(5, MeshCommand.Spawn(2));

        node.Advance(12);

        var straight = Node();
        straight.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 2));
        straight.Schedule(5, MeshCommand.SetTeam(2, Team.Blue));
        straight.Schedule(5, MeshCommand.SetClass(2, PlayerClass.Scout));
        straight.Schedule(5, MeshCommand.Spawn(2));

        // ⚠️ Both nodes must end on the SAME tick or the comparison is of two
        // different moments: Settle reconciles without stepping, which is
        // exactly what makes that possible.
        for (var tick = 0; tick < 12; tick++)
            straight.Submit(tick, 1, Input(tick));

        straight.Advance(12);

        // The rewound node learns everything and replays; it must land exactly
        // where the node that never guessed wrong did.
        for (var tick = 0; tick < 12; tick++)
            node.Submit(tick, 1, Input(tick));

        node.Settle();
        straight.Settle();

        Assert.Equal(straight.Tick, node.Tick);

        Assert.True(node.Stats.Rollbacks > 0);
        Assert.Equal(straight.Digest, node.Digest);
    }

    /// <summary>
    /// A command for a tick already simulated is refused, not quietly applied.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The one case where a node must NOT be helpful.</b> Applying it would
    /// mean rewinding to a tick the peer never rewound to, over a change the peer
    /// has never heard of — and a roster disagreement does not converge the way a
    /// mispredicted key does. Refusing is what makes the schedule something both
    /// nodes can actually agree on.
    /// </remarks>
    [Fact]
    public void ACommandForAPastTickIsRefused()
    {
        ReferenceTree.RequireAvailable();

        var node = Node();
        node.Advance(20);

        Assert.False(node.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 2)));
        Assert.False(node.State.HasPlayer[2]);
        Assert.Equal(1, node.Stats.TooLate);
    }

    /// <summary>
    /// The order within a tick comes from the data, so two nodes that heard the
    /// same commands in different orders still apply them the same way.
    /// </summary>
    /// <remarks>
    /// Without this, a node that heard "spawn" before "set class" would spawn a
    /// Scout where its peer spawned a Soldier, and the two worlds would differ
    /// from that tick on with nothing to bring them back.
    /// </remarks>
    [Fact]
    public void ArrivalOrderDoesNotChangeApplicationOrder()
    {
        ReferenceTree.RequireAvailable();

        var forwards = Node();
        var backwards = Node();

        MeshCommand[] join =
        [
            MeshCommand.AddPlayer(origin: 0, sequence: 2),
            MeshCommand.SetTeam(2, Team.Blue),
            MeshCommand.SetClass(2, PlayerClass.Scout),
            MeshCommand.Spawn(2),
        ];

        foreach (var command in join)
            forwards.Schedule(6, command);

        for (var i = join.Length - 1; i >= 0; i--)
            backwards.Schedule(6, join[i]);

        Assert.Equal(forwards.ScheduledFor(6), backwards.ScheduledFor(6));

        forwards.Advance(8);
        backwards.Advance(8);

        Assert.Equal(PlayerClass.Scout, forwards.State.Players[2].Class);
        Assert.Equal(forwards.Digest, backwards.Digest);
    }

    /// <summary>
    /// ⛔ A removal shares a tick with a change, and the change still reaches the
    /// player it named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A removal renumbers every slot above it</b>, so it has to be the last
    /// thing a tick applies — everything else was written while the slot numbers
    /// still meant what the sender meant. Two nodes would agree either way, so
    /// nothing here diverges and no digest goes red: it silently hands the team
    /// change to whoever shifted down into the slot.
    /// </para>
    /// <para>
    /// The order is the numeric comparison of <see cref="MeshCommandKind"/>, not
    /// the order the members are declared in, which is why this asserts the
    /// behaviour and not the enum.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARemovalIsAppliedAfterEverythingElseInItsTick()
    {
        ReferenceTree.RequireAvailable();

        var node = Node();

        // Three more, so there is somebody above the hole to be mistaken for the
        // player the change names.
        node.Schedule(4, MeshCommand.AddPlayer(origin: 0, sequence: 1));
        node.Schedule(4, MeshCommand.AddPlayer(origin: 0, sequence: 2));
        node.Schedule(4, MeshCommand.AddPlayer(origin: 0, sequence: 3));
        node.Advance(6);

        Assert.True(node.State.HasPlayer[4]);

        // Slot 3 changes team; slot 2 leaves. Both were named against the roster
        // as it stands NOW, which is what the ordering has to preserve.
        node.Schedule(10, MeshCommand.ChangeTeam(3, Team.Red));
        node.Schedule(10, MeshCommand.RemovePlayer(2));
        node.Advance(12);

        // The player that was slot 3 is slot 2 now, and is the one on Red.
        Assert.Equal(Team.Red, node.State.Players[2].Team);

        // And the player that was slot 4 — who shifted into 3, where the team
        // change would have landed had the removal gone first — was not touched.
        Assert.NotEqual(Team.Red, node.State.Players[3].Team);
    }

    /// <summary>
    /// ⛔ Two joins on one tick, one from each node, take the same two slots on
    /// both machines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The reason a join does not name its slot.</b> A GG2 roster is dense
    /// and positional — the wire numbers players by their place in it — so two
    /// nodes that each picked a slot for somebody joining would pick the same
    /// one, and the second join would land on top of the first. Instead the slot
    /// falls out of the order both nodes already compute, and consecutive places
    /// go to the joins in that order.
    /// </para>
    /// <para>
    /// The two nodes hear the two joins in OPPOSITE orders here, which is the
    /// case a real link produces: each hears its own first.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoJoinsOnOneTickTakeTheSameSlotsOnBothNodes()
    {
        ReferenceTree.RequireAvailable();

        var listener = Node();
        var joiner = Node();

        var fromListener = MeshCommand.AddPlayer(origin: 0, sequence: 1);
        var fromJoiner = MeshCommand.AddPlayer(origin: 1, sequence: 1);

        // Each node hears its own join first and the peer's second.
        listener.Schedule(12, fromListener);
        listener.Schedule(12, fromJoiner);

        joiner.Schedule(12, fromJoiner);
        joiner.Schedule(12, fromListener);

        listener.Advance(14);
        joiner.Advance(14);

        Assert.True(listener.TryGetAssignedSlot(0, 1, out var listenersPlayer));
        Assert.True(listener.TryGetAssignedSlot(1, 1, out var joinersPlayer));

        Assert.True(joiner.TryGetAssignedSlot(0, 1, out var alsoListeners));
        Assert.True(joiner.TryGetAssignedSlot(1, 1, out var alsoJoiners));

        // The same two people, in the same two places, on both machines.
        Assert.Equal(listenersPlayer, alsoListeners);
        Assert.Equal(joinersPlayer, alsoJoiners);

        // ...and they are two DIFFERENT places, which is the failure this
        // guards: one slot handed to both would satisfy every equality above.
        Assert.NotEqual(listenersPlayer, joinersPlayer);

        Assert.Equal(listener.Digest, joiner.Digest);
    }

    /// <summary>
    /// A join takes the lowest free place, and never slot 0.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Slot 0 is the server's own reserved player</b> and always exists —
    /// CLAUDE.md. A join that took it would hand a person the slot every
    /// defaulted "nobody" already points at.
    /// </remarks>
    [Fact]
    public void AJoinTakesTheLowestFreePlaceAndNeverSlotZero()
    {
        ReferenceTree.RequireAvailable();

        var node = Node();

        // The fixture already holds slots 0 and 1, so the next place is 2.
        node.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 1));
        node.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 2));
        node.Advance(7);

        Assert.True(node.TryGetAssignedSlot(0, 1, out var first));
        Assert.True(node.TryGetAssignedSlot(0, 2, out var second));

        Assert.Equal(2, first);
        Assert.Equal(3, second);
    }

    /// <summary>
    /// ⛔ A departure renumbers the slot a join was given, and who owns it.
    /// </summary>
    /// <remarks>
    /// <b>CLAUDE.md's oldest trap, in a new place.</b> A removal compacts the
    /// roster, so every slot above it moves down — and anything that NAMES a
    /// player has to move with it. Two tables here name players: which slot a
    /// join was assigned, and which node owns each slot. Left alone, both point
    /// one place too high the moment somebody leaves, so a node would drive the
    /// wrong player and send the wrong one's keys.
    /// </remarks>
    [Fact]
    public void ADepartureRenumbersTheSlotsAboveIt()
    {
        ReferenceTree.RequireAvailable();

        var node = Node();

        // Three joins: the fixture holds 0 and 1, so these take 2, 3 and 4.
        node.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 1));
        node.Schedule(5, MeshCommand.AddPlayer(origin: 1, sequence: 1));
        node.Schedule(5, MeshCommand.AddPlayer(origin: 0, sequence: 2));
        node.Advance(7);

        Assert.True(node.TryGetAssignedSlot(0, 1, out var first));
        Assert.True(node.TryGetAssignedSlot(1, 1, out var middle));
        Assert.True(node.TryGetAssignedSlot(0, 2, out var last));

        // ⚠️ The places follow the canonical ORDER, not the order they were
        // scheduled in: origin sorts before sequence, so node 0's two joins take
        // 2 and 3 and node 1's takes 4. Two joins from one node are not
        // necessarily adjacent once the other node is joining too.
        Assert.Equal(2, first);
        Assert.Equal(3, last);
        Assert.Equal(4, middle);

        Assert.Equal(1, node.OriginOf(middle));
        Assert.Equal(0, node.OriginOf(last));

        // The one in the middle place leaves.
        node.Schedule(10, MeshCommand.RemovePlayer(last));
        node.Advance(6);

        // The one above it moved down, and its owner moved with it.
        Assert.True(node.TryGetAssignedSlot(1, 1, out var moved));
        Assert.Equal(middle - 1, moved);
        Assert.Equal(1, node.OriginOf(moved));

        // The one below did not move.
        Assert.True(node.TryGetAssignedSlot(0, 1, out var stayed));
        Assert.Equal(first, stayed);

        // ...and the player who left is gone from both tables.
        Assert.False(node.TryGetAssignedSlot(0, 2, out _));
    }
}
