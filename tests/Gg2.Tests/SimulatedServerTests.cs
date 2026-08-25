using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The live server actually simulating, rather than serialising an empty world.
/// </summary>
/// <remarks>
/// Phase 4's other half. <see cref="PhysicsParityTests"/> proves the physics
/// against a recording with no server in the way; this proves the server is
/// wired to that physics at all — that a player who picks a team gets a
/// character, that the character is where the map says it should be, and that it
/// then moves under the same rules the parity gate checked.
/// <para>
/// The broadcast is decoded with <see cref="ServerStream"/>, the same framer the
/// parity recorder uses, rather than by counting bytes by hand. A layout mistake
/// therefore fails as a decode error here instead of as a silently wrong number.
/// </para>
/// </remarks>
public sealed class SimulatedServerTests
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// Decodes everything the server has broadcast so far, keeping the roster
    /// the way a real client does.
    /// </summary>
    private sealed class BroadcastReader
    {
        private readonly ServerStream _stream = new();
        private readonly ReceiveBuffer _buffer = new();

        public BroadcastReader(LevelMap map) => _stream.SetShape(GameModes.ShapeOf(map));

        public List<ServerMessage> Drain(LoopbackClient client)
        {
            client.Poll();

            var pending = client.Take(client.Received.Count);
            _buffer.Append(pending);

            var messages = new List<ServerMessage>();
            while (_stream.TryRead(_buffer, out var message))
                messages.Add(message);

            return messages;
        }
    }

    private static LevelMap LoadMap() => LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

    /// <summary>
    /// Picking a team spawns a character, and it is announced as a spawn point
    /// rather than as a position.
    /// </summary>
    /// <remarks>
    /// The message carrying an id and a group rather than coordinates is the
    /// whole reason a replay can anchor exactly — see docs/PARITY.md. If the
    /// server ever sent a position instead, every client would still work and
    /// the parity harness would quietly lose its exact anchor.
    /// </remarks>
    [Fact]
    public void PickingATeamSpawnsACharacterAtAMapSpawnPoint()
    {
        var map = LoadMap();
        var spawnPoints = new SpawnPoints(map);

        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("walker");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(client);

        client.SendChangeTeam(Team.Red);
        harness.Tick();

        var messages = reader.Drain(client);
        var spawn = messages.SingleOrDefault(m => m.Type == MessageType.PlayerSpawn);

        Assert.NotNull(spawn);
        Assert.Equal(3, spawn.Payload.Length);

        var slot = spawn.Payload[0];
        var point = spawn.Payload[1];
        var group = spawn.Payload[2];

        Assert.Equal(0, group);
        Assert.True(point < spawnPoints.Count(Team.Red, 0), $"spawn point {point} is out of range");
        Assert.True(spawnPoints.TryGet(Team.Red, group, point, out var x, out var y));

        // Run to the next position-bearing update and check the character is
        // where that spawn point says, allowing for the ticks of gravity between
        // the spawn and the update.
        var character = NextCharacter(harness, reader, client, slot);

        Assert.Equal((ushort)(x * 5), character.X);
        Assert.True(character.Y >= (ushort)(y * 5), "the character rose after spawning");
        Assert.Equal(ClassStats.For(PlayerClass.Scout).MaxHp, character.Hp);
    }

    /// <summary>
    /// The character the server broadcasts is actually being simulated: it falls
    /// under gravity and then stops on the floor.
    /// </summary>
    /// <remarks>
    /// This is the check that the server is running <see cref="MatchSimulation"/>
    /// rather than holding a character still at its spawn point — which would
    /// look completely correct for one update and then diverge from every
    /// client's own prediction for as long as the round lasted.
    /// <para>
    /// Landing matters more than falling: a character that fell forever would
    /// mean the collision world was never handed to the tick.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASpawnedCharacterFallsAndThenLandsOnTheFloor()
    {
        var map = LoadMap();

        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("faller");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(client);

        client.SendChangeTeam(Team.Blue);
        harness.Tick();

        var spawn = reader.Drain(client).Single(m => m.Type == MessageType.PlayerSpawn);
        var slot = spawn.Payload[0];

        var first = NextCharacter(harness, reader, client, slot);

        // Far enough for a spawn a few pixels above the floor to land and settle.
        ushort last = first.Y;
        var fell = false;

        for (var i = 0; i < 12; i++)
        {
            var next = NextCharacter(harness, reader, client, slot);

            if (next.Y > last)
                fell = true;

            last = next.Y;
        }

        Assert.True(fell, "the character never moved downwards; is the simulation ticking?");

        // Settled: two more updates with no vertical movement at all.
        var settledAt = NextCharacter(harness, reader, client, slot).Y;
        Assert.Equal(settledAt, NextCharacter(harness, reader, client, slot).Y);
        Assert.Equal(0, NextCharacter(harness, reader, client, slot).VSpeed);
    }

    /// <summary>A spectator has no character, and picking spectator removes one.</summary>
    /// <remarks>
    /// The client destroys the character on <c>PLAYER_CHANGETEAM</c>
    /// (<c>ClientBeginStep.gml</c>), so a server that kept it would be
    /// broadcasting a body no client believes in.
    /// </remarks>
    [Fact]
    public void GoingSpectatorRemovesTheCharacter()
    {
        var map = LoadMap();

        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("quitter");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(client);

        client.SendChangeTeam(Team.Red);
        harness.Tick();
        var slot = reader.Drain(client).Single(m => m.Type == MessageType.PlayerSpawn).Payload[0];

        Assert.NotNull(NextUpdateWithPositions(harness, reader, client).Players[slot].Character);

        client.SendChangeTeam(Team.Spectator);
        harness.Tick();
        reader.Drain(client);

        Assert.Null(NextUpdateWithPositions(harness, reader, client).Players[slot].Character);
    }

    /// <summary>
    /// A player who never picks a team never gets a character.
    /// </summary>
    /// <remarks>
    /// Joining leaves everyone on <c>TEAM_SPECTATOR</c>, which is the state a
    /// client sits in on the team-select screen. Spawning them there would put a
    /// body on every observer's map before they had chosen to play.
    /// </remarks>
    [Fact]
    public void AJoinedPlayerHasNoCharacterUntilItPicksATeam()
    {
        var map = LoadMap();

        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("lurker");
        var reader = new BroadcastReader(map);

        harness.Tick(10);
        var messages = reader.Drain(client);

        Assert.DoesNotContain(messages, m => m.Type == MessageType.PlayerSpawn);

        var update = NextUpdateWithPositions(harness, reader, client);
        foreach (var player in update.Players)
            Assert.Null(player.Character);
    }

    /// <summary>
    /// A character follows its player down when somebody below them leaves.
    /// </summary>
    /// <remarks>
    /// The roster compacts on <c>PLAYER_LEAVE</c> and so must the world: a slot
    /// index <em>is</em> a roster index. Getting this wrong hands a character to
    /// the wrong player, but only after somebody disconnects — the intermittent
    /// off-by-one CLAUDE.md warns about, and one no static check catches.
    /// </remarks>
    [Fact]
    public void ACharacterFollowsItsPlayerWhenTheRosterCompacts()
    {
        var map = LoadMap();

        using var harness = new ServerHarness(o => o.Map = Map);
        var leaver = harness.JoinClient("leaver");
        var stayer = harness.JoinClient("stayer");
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(observer);

        // Only the second of the two picks a team, so exactly one character
        // exists and it is unambiguous which slot should own it afterwards.
        stayer.SendChangeTeam(Team.Red);
        harness.Tick();

        var before = reader.Drain(observer).Single(m => m.Type == MessageType.PlayerSpawn).Payload[0];
        Assert.Equal(2, before);

        var settled = NextCharacter(harness, reader, observer, before);

        // A clean PLAYER_LEAVE rather than a dropped socket, so the harness can
        // keep polling every client while the roster compacts.
        leaver.SendCommand(MessageType.PlayerLeave);
        harness.TickUntil(() => harness.Server.PlayerCount == 3, "the leaver to be dropped");
        reader.Drain(observer);

        // Slot 1 left, so the character that was in slot 2 is now in slot 1 —
        // and nothing is left behind in slot 2.
        var update = NextUpdateWithPositions(harness, reader, observer);

        Assert.Null(update.Players[2].Character);
        var moved = update.Players[1].Character;
        Assert.NotNull(moved);
        Assert.Equal(settled.X, moved.Value.X);
    }

    /// <summary>Ticks until a position-bearing update arrives, and returns it.</summary>
    private static StateUpdate NextUpdateWithPositions(
        ServerHarness harness,
        BroadcastReader reader,
        LoopbackClient client)
    {
        // Positions go out every seventh tick, so this never needs many.
        for (var i = 0; i < 20; i++)
        {
            harness.Tick();

            foreach (var message in reader.Drain(client))
            {
                if (message.State is { Tier: MessageType.QuickUpdate or MessageType.FullUpdate } state)
                    return state;
            }
        }

        Assert.Fail("no position-bearing update in 20 ticks");
        return null!;
    }

    private static CharacterBlock NextCharacter(
        ServerHarness harness,
        BroadcastReader reader,
        LoopbackClient client,
        int slot)
    {
        var update = NextUpdateWithPositions(harness, reader, client);
        var character = update.Players[slot].Character;

        Assert.True(character.HasValue, $"slot {slot} has no character");
        return character.Value;
    }
}
