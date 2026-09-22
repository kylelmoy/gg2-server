using System.Collections.Generic;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// What a node may call final in the ticks immediately after a map change.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="MeshNode.Confirmed"/> is a promise</b>: "everything before it
/// ran on facts alone and can no longer change". Every irreversible event a
/// client is ever told about is released against that promise, so a tick
/// confirmed on a guess is a death or a capture sent to a stock client that the
/// node may then un-simulate.
/// </para>
/// <para>
/// <b>A rebuild confirms its first tick with every peer input still a guess,
/// and that is sound.</b> <c>MeshNode.IsCertain</c> waits only on slots ALIVE at
/// the start of the tick, and <c>Rounds.ResetForNewMap</c> clears every
/// character and gives everybody <c>RespawnTimer = 1</c> — so at the first tick
/// of a new map there is nobody to wait on. Measured here: that tick's outcome
/// does not move when the peer's real input for it arrives, because a respawn
/// does not consume the tick's input. From the next tick everybody is alive and
/// confirmation waits normally.
/// </para>
/// <para>
/// ⚠️ <b>So this asserts the promise, not the aliveness rule.</b> Refusing to
/// confirm a tick whose outcome cannot depend on the missing input would stall
/// event release for nothing, which is the fault <c>MeshSessionTests</c> gates
/// in the other direction. What has to hold is that whatever the node called
/// final is still true once the peer's inputs land — verified red by making
/// <c>IsCertain</c> ignore aliveness, which confirms ticks with live players
/// still outstanding and moves a confirmed tick's digest
/// (<c>4b45d55f4a26268f</c> became <c>15f01949c7d09ba8</c> at tick 1002).
/// </para>
/// </remarks>
public sealed class MeshRebuildConfirmationTests
{
    private const int Local = 0;
    private const int Remote = 1;

    /// <summary>The tick a rebuild lands on, well clear of zero.</summary>
    private const int Rebuild = 1000;

    private const int HoldTicks = 5;

    private static LevelMap Map() =>
        LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));

    private static CollisionWorld Collision(LevelMap map) => new(map);

    /// <summary>
    /// ⛔ <b>The spawn points are not optional here.</b>
    /// <c>PerformMapChange</c> hands <c>MeshSession.Rebuild</c> the new map's
    /// spawn points, and without them <c>RespawnTimer = 1</c> has nowhere to put
    /// anybody: nobody ever becomes alive, the world stays byte-identical for
    /// every tick, and a test built that way asserts nothing about a live match.
    /// Measured while writing this — four ticks, one digest, nobody alive.
    /// </summary>
    private static SpawnPoints Spawns(LevelMap map) => new(map);

    /// <summary>
    /// The world a rebuild actually starts from: two players on opposite teams,
    /// put through the real reset rather than a hand-built imitation of it.
    /// </summary>
    /// <remarks>
    /// <c>firstArea: false</c> — the multi-area reset, which keeps everybody's
    /// team. The first-area one makes the whole server a spectator, and a
    /// spectator never becomes alive, so the window this test is about would run
    /// until somebody picked a team again and the test would be measuring the
    /// team change instead.
    /// </remarks>
    private static WorldState RebuiltFrom()
    {
        var state = default(WorldState);

        Place(ref state, Local, Team.Red, 380, 268);
        Place(ref state, Remote, Team.Blue, 680, 268);

        Rounds.ResetForNewMap(ref state, firstArea: false);

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
    /// Keys that change at about the rate a person's do, offset per slot.
    /// </summary>
    private static PlayerKeys KeysFor(int slot, int tick)
    {
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

    private static PlayerInput InputFor(int slot, int tick) =>
        new(
            KeysFor(slot, tick),
            (ushort)(((tick + (slot * 2)) / HoldTicks * 2411) % 65536),
            40);

    /// <summary>
    /// ⛔ A tick this node called final, after a rebuild, still says the same
    /// thing once the peer's real inputs turn up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is the one a real map change produces. Both nodes rebuild at
    /// the same TICK but at different wall-clock moments, so each resumes having
    /// heard a different amount of the other's traffic — which is why the peer's
    /// inputs for the ticks right after a rebuild arrive late rather than not at
    /// all.
    /// </para>
    /// <para>
    /// ⚠️ <b>The rollback assertion is what stops this passing vacuously.</b>
    /// If the late inputs never contradicted a guess there would be nothing for
    /// a confirmation to have been wrong about, and the digest comparison below
    /// would hold against a node that confirmed everything on no evidence at
    /// all.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATickConfirmedAfterARebuildNeverChangesAfterwards()
    {
        ReferenceTree.RequireAvailable();

        const int window = 16;
        const int ticks = 4;

        var map = Map();

        var node = new MeshNode(
            Collision(map),
            RebuiltFrom(),
            new MeshOptions { MaxRollbackTicks = window },
            Spawns(map),
            startTick: Rebuild);

        // The peer is not heard from at all, which is the state a node is in for
        // the first few ticks after it rebuilds.
        for (var tick = Rebuild; tick < Rebuild + ticks; tick++)
        {
            node.Submit(tick, Local, InputFor(Local, tick));
            node.Advance();
        }

        // Everything the node is prepared to call final.
        //
        // ⚠️ Through Confirmed INCLUSIVE, because DigestAt(t) is the world
        // ENTERING tick t — so the outcome of the last confirmed tick is
        // DigestAt(Confirmed), and a loop stopping below it would never check
        // the one tick this scenario actually confirms on a guess.
        var promised = new Dictionary<int, ulong>();

        for (var tick = Rebuild; tick <= node.Confirmed; tick++)
        {
            if (node.DigestAt(tick) is { } digest)
                promised[tick] = digest;
        }

        // The peer's real inputs, late.
        for (var tick = Rebuild; tick < Rebuild + ticks; tick++)
            node.Submit(tick, Remote, InputFor(Remote, tick));

        node.Settle();

        Assert.True(
            node.Stats.Rollbacks > 0,
            "the peer's late inputs never contradicted a guess, so this test "
                + "asserted nothing about confirmation");

        foreach (var (tick, digest) in promised)
        {
            Assert.True(
                node.DigestAt(tick) is null || node.DigestAt(tick) == digest,
                $"the world entering tick {tick} was called final and then "
                    + $"CHANGED when the peer's late inputs arrived: "
                    + $"{digest:x16} became {node.DigestAt(tick):x16}. "
                    + $"Confirmed reached {node.Confirmed} of {node.Tick}. "
                    + "Every irreversible event for those ticks has already "
                    + "gone to a client. Start at MeshNode.IsCertain and what "
                    + "it is willing not to wait on.");
        }
    }
}
