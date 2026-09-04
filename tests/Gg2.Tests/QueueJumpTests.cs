using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Queued jumping: <c>CLIENT_SETTINGS</c> through to the jump it changes.
/// </summary>
/// <remarks>
/// <c>Character.events/Begin Step</c> has two halves of this feature and both
/// are gated on <c>player.queueJump</c> — the block that records
/// <c>wantToJump</c> from the jump key's press and release, and the
/// <c>or (player.queueJump and wantToJump)</c> arm of the jump test.
/// <para>
/// <b>This is observable simulation, which is why it is worth a file.</b>
/// Every client re-simulates every character between position updates, using
/// that character's own <c>queueJump</c>. A server that ignores the setting puts
/// itself on a different jump rule from the client that sent it, and the symptom
/// is a correction on every landing — indistinguishable from ordinary netcode
/// jitter.
/// </para>
/// <para>
/// <b>A dead field reads as evidence a feature is there when it is not.</b>
/// <c>CharacterState.WantToJump</c> was cleared in two places but never written,
/// so the field alone cannot be trusted as proof the queue is wired in.
/// </para>
/// </remarks>
public sealed class QueueJumpTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    /// <summary>
    /// A scout standing on the ground with the jump key already held.
    /// </summary>
    /// <remarks>
    /// The character is dropped onto solid ground and settled first, because the
    /// whole question is what happens on a <em>landing</em> and a character that
    /// starts mid-air answers a different one.
    /// </remarks>
    private static (WorldState State, CollisionWorld World) Standing(bool queueJump)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 5;
        Rounds.Start(ref state);

        state.Players[1] = new PlayerState
        {
            Team = Team.Red,
            Class = PlayerClass.Scout,
            RespawnTimer = Timers.Inactive,
            QueueJump = queueJump,
        };
        state.HasPlayer[1] = true;

        Assert.True(new SpawnPoints(level).TryGet(Team.Red, 0, 0, out var spawnX, out var spawnY));

        var character = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        character.X = spawnX;
        character.Y = spawnY;

        state.Characters[1] = character;
        state.Weapons[1] = WeaponState.Spawn(PlayerClass.Scout, character.X, character.Y);
        state.Alive[1] = true;

        return (state, world);
    }

    private static void Tick(CollisionWorld world, ref WorldState state, PlayerKeys keys, int count = 1)
    {
        var events = default(TickEvents);
        Span<PlayerInput> inputs = stackalloc PlayerInput[2];

        for (var i = 0; i < count; i++)
        {
            events = default;
            inputs[1] = new PlayerInput { Keys = keys };
            MatchSimulation.Tick(world, ref state, inputs, ref events);
        }
    }

    /// <summary>
    /// Falls until the character is resting on the ground.
    /// </summary>
    private static void Settle(CollisionWorld world, ref WorldState state)
    {
        for (var i = 0; i < 200 && !state.Characters[1].OnGround; i++)
            Tick(world, ref state, PlayerKeys.None);

        Assert.True(state.Characters[1].OnGround, "the character never landed");
    }

    /// <summary>
    /// Holding jump does not bunny-hop for an ordinary client.
    /// </summary>
    /// <remarks>
    /// The control, and the more important of the pair: this is the behaviour
    /// every existing fixture depends on, so it is what would break if the queue
    /// were wired in unconditionally.
    /// </remarks>
    [Fact]
    public void HoldingJumpDoesNotBunnyHopWithoutTheSetting()
    {
        var (state, world) = Standing(queueJump: false);
        Settle(world, ref state);

        // The first jump, on the fresh press.
        Tick(world, ref state, PlayerKeys.Jump);
        Assert.False(state.Characters[1].OnGround);

        // Hold it down through the whole arc and the landing.
        Tick(world, ref state, PlayerKeys.Jump, 120);

        Assert.True(state.Characters[1].OnGround, "the held jump never came back down");
        Assert.False(state.Characters[1].WantToJump, "nothing may queue a jump with the setting off");
    }

    /// <summary>
    /// A press made in mid-air fires the moment the character lands.
    /// </summary>
    /// <remarks>
    /// <b>This, and not "holding jump bunny-hops", is what the feature
    /// does.</b> <c>pressedKeys</c> is a transition rather than a level, so a
    /// held key sets <c>wantToJump</c> exactly once — and if the character was on
    /// the ground at the time, the jump it triggers clears the queue on the same
    /// tick. The queue only survives a press the jump test <em>refused</em>,
    /// which means a press made while airborne with no double jump left.
    /// <para>
    /// Run as an A/B against the identical key sequence with the setting off, so
    /// what is asserted is the setting's effect rather than this map's geometry.
    /// </para>
    /// </remarks>
    [Fact]
    public void APressMadeInTheAirFiresOnLanding()
    {
        Assert.False(RelaunchedWithoutAPress(queueJump: false), "the control must stay down");
        Assert.True(RelaunchedWithoutAPress(queueJump: true), "the queued press must relaunch");
    }

    /// <summary>
    /// Jumps, re-presses in mid-air, holds through the landing, and says whether
    /// the character ever launched again with no new press to explain it.
    /// </summary>
    /// <remarks>
    /// <b>The relaunch is detected by the velocity turning over, not by
    /// watching <c>OnGround</c>.</b> A queued jump fires in the same Begin Step
    /// that notices the landing, so <c>OnGround</c> is never observably true on
    /// the tick it matters — a test that waited for it would sail past the event
    /// and measure the <em>next</em> landing, by which time the queue is spent
    /// and both arms of the A/B agree.
    /// <para>
    /// Falling to rising is unambiguous: nothing but a jump turns a descent into
    /// a climb, and with the key held down for the whole window there is no
    /// press transition left to cause one.
    /// </para>
    /// </remarks>
    private static bool RelaunchedWithoutAPress(bool queueJump)
    {
        var (state, world) = Standing(queueJump);
        Settle(world, ref state);

        // The first jump, on a fresh press. It consumes the queue if there is
        // one, which is why the presses below have to be new transitions.
        Tick(world, ref state, PlayerKeys.Jump);
        Assert.False(state.Characters[1].OnGround, "the first jump did not leave the ground");

        // Release and re-press while airborne. The scout has a double jump, so
        // spend it on the second press and queue on the third, which nothing can
        // act on.
        Tick(world, ref state, PlayerKeys.None);
        Tick(world, ref state, PlayerKeys.Jump);
        Tick(world, ref state, PlayerKeys.None);
        Tick(world, ref state, PlayerKeys.Jump);

        Assert.False(state.Characters[1].OnGround, "the character came down too early to queue");
        Assert.Equal(queueJump, state.Characters[1].WantToJump);

        // Hold it through the descent and past the landing. Positive vspeed is
        // downward in GM8.
        for (var i = 0; i < 200; i++)
        {
            var before = state.Characters[1].VSpeed;
            Tick(world, ref state, PlayerKeys.Jump);
            var after = state.Characters[1].VSpeed;

            if (before >= 0 && after < 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Releasing the key cancels the queue.
    /// </summary>
    /// <remarks>
    /// The <c>else if (releasedKeys &amp; $80) wantToJump = false</c> half.
    /// Without it, one press made in mid-air would still fire on landing however
    /// long ago the player let go.
    /// </remarks>
    [Fact]
    public void ReleasingJumpClearsTheQueue()
    {
        var (state, world) = Standing(queueJump: true);
        Settle(world, ref state);

        // Get airborne and out of double jumps, then queue.
        Tick(world, ref state, PlayerKeys.Jump);
        Tick(world, ref state, PlayerKeys.None);
        Tick(world, ref state, PlayerKeys.Jump);
        Tick(world, ref state, PlayerKeys.None);
        Tick(world, ref state, PlayerKeys.Jump);

        Assert.True(state.Characters[1].WantToJump, "the airborne press should have queued");

        Tick(world, ref state, PlayerKeys.None);
        Assert.False(state.Characters[1].WantToJump, "the release should have cleared it");

        // And it stays down once it lands.
        Tick(world, ref state, PlayerKeys.None, 200);
        Assert.True(state.Characters[1].OnGround);
    }

    /// <summary>
    /// The setting reaches the world, the roster and every other client.
    /// </summary>
    /// <remarks>
    /// <b>Three destinations and all three matter.</b> The world copy is what
    /// the physics above reads; the roster copy is what a joiner's
    /// <c>FULL_UPDATE</c> carries; the broadcast is what already-connected
    /// clients need in order to dead-reckon this character correctly.
    /// </remarks>
    [Fact]
    public void TheSettingReachesTheWorldAndIsBroadcast()
    {
        using var harness = new ServerHarness();

        var sender = harness.JoinClient("sender");
        var watcher = harness.JoinClient("watcher");

        harness.Tick(2);
        watcher.Take(watcher.Received.Count);

        var slot = harness.Server.RosterCount - 2;
        Assert.Equal("sender", harness.Server.EntryOf(slot).Name);

        sender.Send([(byte)MessageType.ClientSettings, 1]);
        harness.Tick(2);

        Assert.True(harness.Server.EntryOf(slot).QueueJump, "the roster copy");
        Assert.True(harness.Server.SimulatedQueueJump(slot), "the world copy");

        var seen = watcher.Take(watcher.Received.Count);
        var at = IndexOfClientSettings(seen);

        Assert.True(at >= 0, "the other client was never told");
        Assert.Equal((byte)slot, seen[at + 1]);
        Assert.Equal(1, seen[at + 2]);
    }

    /// <summary>
    /// Finds a <c>CLIENT_SETTINGS</c> in a broadcast stream.
    /// </summary>
    /// <remarks>
    /// A scan rather than a parse, because the stream this shares with an
    /// <c>INPUTSTATE</c> every tick is not worth decoding to answer "was it sent
    /// at all". The two payload bytes are checked by the caller, which is what
    /// makes a coincidental 58 in someone's position bytes fail loudly rather
    /// than pass.
    /// </remarks>
    private static int IndexOfClientSettings(byte[] stream)
    {
        for (var i = 0; i + 2 < stream.Length; i++)
            if (stream[i] == (byte)MessageType.ClientSettings)
                return i;

        return -1;
    }
}
