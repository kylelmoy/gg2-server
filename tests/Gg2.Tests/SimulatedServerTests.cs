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
    /// ⚠️ <b>A class change immediately after spawning is <em>silent</em>, and
    /// the replacement arrives on the next tick.</b>
    /// </summary>
    /// <remarks>
    /// This test used to assert a <c>BID_FAREWELL</c> death and a thirty-tick
    /// wait, and it was wrong — <b>every spawn point on every map is inside a
    /// spawn room</b> (checked on <c>ctf_truefort</c>, <c>koth_valley</c>,
    /// <c>gg_debug</c> and <c>cp_dirtbowl</c>), so picking a class at spawn,
    /// which is the commonest action in the game, takes
    /// <c>processClientCommands.gml:84</c>'s <c>else</c> arm: a bare
    /// <c>instance_destroy()</c> with no message at all. The port broadcast a
    /// death there for six phases.
    /// <para>
    /// The one-tick respawn is the other half of it, from
    /// <c>Character.events/Destroy.xml:37</c> — the Destroy event runs whether
    /// or not a death was announced.
    /// </para>
    /// <para>
    /// The death path itself is gated by <c>PlayerLifecycleTests</c>, which
    /// drives <see cref="PlayerCommands.ChangeClass"/> with no map and so always
    /// takes the other arm.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClassChangeInTheSpawnRoomIsSilentAndRespawnsAtOnce()
    {
        var map = LoadMap();

        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.RespawnTimeSeconds = 1; // 30 ticks
        });
        var client = harness.JoinClient("phoenix");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(client);

        client.SendChangeTeam(Team.Red);
        harness.Tick();
        harness.Tick();
        var slot = reader.Drain(client).Single(m => m.Type == MessageType.PlayerSpawn).Payload[0];

        // The premise: the fresh spawn really is inside a spawn room, so this
        // takes the silent arm. Asserted rather than assumed — if a map ever
        // moved its spawn points out of its rooms this test would quietly start
        // testing the death path instead.
        var collision = new CollisionWorld(map);
        var spawns = new SpawnPoints(map);

        Assert.True(spawns.TryGet(Team.Red, 0, 0, out var spawnX, out var spawnY));
        Assert.True(
            collision.PointInSpawnRoom(spawnX, spawnY),
            "the spawn point is not inside a spawn room, so this tests nothing");

        client.SendChangeClass(PlayerClass.Pyro);
        harness.Tick();
        harness.Tick();

        var messages = reader.Drain(client);

        // ⚠️ No death at all.
        Assert.DoesNotContain(messages, m => m.Type == MessageType.PlayerDeath);
        Assert.Contains(messages, m => m.Type == MessageType.PlayerChangeClass);

        // And the replacement is already out — one tick, not the configured
        // thirty.
        var respawn = messages.Single(m => m.Type == MessageType.PlayerSpawn);
        Assert.Equal(slot, respawn.Payload[0]);
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

    /// <summary>
    /// Holding the attack key makes the server broadcast a <c>WEAPON_FIRE</c>,
    /// and exactly one per refire period.
    /// </summary>
    /// <remarks>
    /// The server's half of phase 5 stage 3. <see cref="ScattergunTests"/> and
    /// <see cref="PhysicsParityTests"/> both drive
    /// <see cref="MatchSimulation"/> directly, so neither would notice if the
    /// server never told anybody a shot happened — and a client that is not told
    /// draws no pellets at all, because the server sends the seed rather than
    /// the pellets.
    /// <para>
    /// The cadence is asserted as well as the message, because a fire message
    /// per tick is exactly what a missing <c>readyToShoot</c> would produce and
    /// it would look correct in a screenshot.
    /// </para>
    /// </remarks>
    [Fact]
    public void HoldingTheAttackKeyBroadcastsOneWeaponFirePerRefirePeriod()
    {
        var map = LoadMap();

        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("shooter");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(client);

        client.SendChangeTeam(Team.Red);
        harness.Tick();
        harness.Tick();

        var spawn = reader.Drain(client).Single(m => m.Type == MessageType.PlayerSpawn);
        var slot = spawn.Payload[0];

        // A scattergun spawns with alarm[0] already on the clock, so the first
        // shot cannot come before it expires; sixty ticks is three refire
        // periods of twenty once it does.
        var fires = new List<ServerMessage>();

        for (var tick = 0; tick < 80; tick++)
        {
            client.SendInputState(new PlayerInput(PlayerKeys.Attack, 0, 0));
            harness.Tick();
            fires.AddRange(reader.Drain(client).Where(m => m.Type == MessageType.WeaponFire));
        }

        Assert.NotEmpty(fires);
        Assert.All(fires, f => Assert.Equal(slot, f.PlayerId));
        Assert.All(fires, f => Assert.Equal(9, f.Payload.Length));

        // Six rounds in the magazine, and eighty ticks is not long enough to
        // reload more than one of them back — so a held trigger cannot produce
        // more than seven blasts however the timing lands.
        Assert.InRange(fires.Count, 2, 7);

        // Not every seed the same, which is what a generator that never advances
        // would give.
        Assert.True(fires.Select(f => f.Payload[7] | (f.Payload[8] << 8)).Distinct().Count() > 1,
            "every shot carried the same seed");
    }

    /// <summary>
    /// Shooting somebody to death broadcasts a <c>PLAYER_DEATH</c>, and the
    /// body stops appearing in the state updates.
    /// </summary>
    /// <remarks>
    /// Phase 5 stage 4's end-to-end: <c>DeathTests</c> proves the rule and
    /// <c>PhysicsParityTests</c> proves the timing against a recording, but
    /// neither runs a byte through a socket. This is the only test that says the
    /// server is wired to the simulation's deaths at all.
    /// <para>
    /// <b>It runs on <c>gg_debug</c> for one reason: the two spawn points are
    /// eighteen pixels apart on the same row</b>, and there is exactly one of
    /// each. So a scout who spawns, aims right and holds the trigger is firing
    /// point blank at a stationary enemy on the very first blast, without any of
    /// the running about that made this take four hundred ticks on a real map —
    /// and without a random spawn choice that could put them somewhere else.
    /// Three blasts at 48 damage kill a 100 hp scout.
    /// </para>
    /// </remarks>
    [Fact]
    public void ShootingSomebodyToDeathBroadcastsAPlayerDeath()
    {
        const string DuelMap = "gg_debug";
        var map = LevelMap.FromPng(DuelMap, ReferenceTree.ReadMap(DuelMap));

        using var harness = new ServerHarness(o =>
        {
            o.Map = DuelMap;

            // ⚠️ gg_debug has no objective, so GameModes.Of falls through to
            // Team Deathmatch — and a TDM spawn is invulnerable. At the default
            // five seconds a victim who never moves keeps it for 150 ticks,
            // which is longer than the window below, so the shot lands and does
            // nothing. Turned off rather than waited out: this test is about
            // the death message, and 0 is a legal setting (game_init.gml:104
            // floors it at 0, not at 1). GeneratorTests' sibling —
            // TeamDeathmatchTests — is where the protection itself is gated.
            o.TdmInvulnerabilitySeconds = 0;
        });

        var shooter = harness.JoinClient("shooter");
        var victim = harness.JoinClient("victim");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(shooter);

        // One at a time, because two PLAYER_CHANGETEAMs in the same tick are
        // answered in whatever order the server polled the sockets in - and the
        // point of this test is lost if it guesses which slot is which.
        shooter.SendChangeTeam(Team.Red);
        harness.Tick();
        harness.Tick();
        var shooterSlot = reader.Drain(shooter).Single(m => m.Type == MessageType.PlayerSpawn).Payload[0];

        victim.SendChangeTeam(Team.Blue);
        harness.Tick();
        harness.Tick();
        var victimSlot = reader.Drain(shooter).Single(m => m.Type == MessageType.PlayerSpawn).Payload[0];

        Assert.NotEqual(shooterSlot, victimSlot);

        // Aim 0 is due right, and red's spawn point is the left-hand one.
        var deaths = new List<ServerMessage>();
        var spawnsWithTheDeath = new List<ServerMessage>();

        for (var tick = 0; tick < 120 && deaths.Count == 0; tick++)
        {
            shooter.SendInputState(new PlayerInput(PlayerKeys.Attack, 0, 0));
            victim.SendInputState(new PlayerInput(PlayerKeys.None, 0, 0));
            harness.Tick();

            var drained = reader.Drain(shooter);
            deaths.AddRange(drained.Where(m => m.Type == MessageType.PlayerDeath));

            // ⚠️ The replacement arrives in the SAME packet as the death, so it
            // has to be caught on this drain or it is consumed and lost. A death
            // is appended in the Character's End Step, which is AFTER the
            // GameServer's End Step flush, so it ships on tick D+1 — and the
            // one-tick respawn's PLAYER_SPAWN is appended in D+1's alarm stage,
            // which is before that same flush. Measured against the running game
            // on 2026-09-02; see GameServer.BroadcastEndStepEvents.
            if (deaths.Count > 0)
                spawnsWithTheDeath.AddRange(drained.Where(m => m.Type == MessageType.PlayerSpawn));
        }

        var death = Assert.Single(deaths);

        Assert.Equal(
            new byte[] { victimSlot, shooterSlot, 255, (byte)DamageSource.Scattergun },
            death.Payload);

        // ⚠️ The body is gone from the world and a *new* one is already back:
        // gg_debug's spawn points are inside its spawn rooms, so a victim who
        // never moved died in one and takes Destroy.xml:37's one-tick respawn.
        // The replacement is what proves the body was destroyed rather than
        // merely announced — a PLAYER_SPAWN cannot go out for a character that
        // still exists.
        var respawned = spawnsWithTheDeath.Any(m => m.Payload[0] == victimSlot);

        for (var tick = 0; tick < 10 && !respawned; tick++)
        {
            harness.Tick();
            respawned = reader.Drain(shooter)
                .Any(m => m.Type == MessageType.PlayerSpawn && m.Payload[0] == victimSlot);
        }

        Assert.True(respawned, "the victim died in a spawn room and did not come straight back");

        var update = NextUpdateWithPositions(harness, reader, shooter);
        Assert.NotNull(update.Players[shooterSlot].Character);

        // And the replacement is a fresh character, not the corpse: full hit
        // points, where the body that was shot had none.
        Assert.Equal(
            ClassStats.For(PlayerClass.Scout).MaxHp,
            update.Players[victimSlot].Character!.Value.Hp);
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
        harness.Tick();
        var slot = reader.Drain(client).Single(m => m.Type == MessageType.PlayerSpawn).Payload[0];

        Assert.NotNull(NextUpdateWithPositions(harness, reader, client).Players[slot].Character);

        client.SendChangeTeam(Team.Spectator);
        harness.Tick();
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
