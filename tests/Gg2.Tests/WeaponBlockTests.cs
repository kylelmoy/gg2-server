using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The class-dependent weapon block that closes a character's
/// <c>FULL_UPDATE</c>.
/// </summary>
/// <remarks>
/// <b>This is a regression test for a bug that shipped through four phases
/// unnoticed, and the reason it went unnoticed is the interesting part.</b>
/// <c>StateUpdateReader</c> and docs/PARITY.md both said the block was zero
/// bytes for eight classes out of ten, having read <c>Objects/Weapons/</c> and
/// found a <c>User Event 12</c> on only <c>Medigun</c> and <c>Minegun</c>. GM8
/// inherits an event a child does not define, so the other eight run the
/// <c>Weapon</c> parent's, which writes two bytes.
/// <para>
/// No fixture caught it because <c>gg2-parity record</c> joins a server
/// immediately, and a <c>FULL_UPDATE</c> is sent on join — so every recording in
/// the repo was taken before any bot had spawned, and none of them contained a
/// character in a full update at all. The path was not merely untested; it was
/// unreachable by the fixtures that existed.
/// </para>
/// <para>
/// <c>reference-ctf_truefort-combat.trace</c> exists to close that hole: six
/// bots, already spawned, with their classes forced so that all three block
/// shapes appear at once — a medic, a demoman and four two-byte weapons.
/// </para>
/// </remarks>
public sealed class WeaponBlockTests
{
    private const string CombatTrace = "reference-ctf_truefort-combat.trace";

    private static (Trace Trace, WorldShape Shape) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", CombatTrace);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = LevelMap.FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map));

        return (trace, GameModes.ShapeOf(map));
    }

    private static List<ServerMessage> Frame(out ServerStream stream)
    {
        var (trace, shape) = Load();

        stream = new ServerStream();
        stream.SetShape(shape);

        var reader = new NetReader(trace.Stream);
        var messages = new List<ServerMessage>();

        while (reader.Remaining > 0)
            messages.Add(stream.Read(ref reader));

        Assert.Equal(trace.Stream.Length, reader.Position);
        return messages;
    }

    /// <summary>
    /// A recording that contains spawned characters frames to the last byte.
    /// </summary>
    /// <remarks>
    /// The whole-stream check is the one that actually holds the widths honest.
    /// A reader that is two bytes short on a weapon block does not fail politely
    /// at that field — it reads the next message's type byte out of the middle
    /// of a player block and runs off the end of the buffer, hundreds of
    /// messages later.
    /// </remarks>
    [Fact]
    public void FramesARecordingThatContainsCharacters()
    {
        var messages = Frame(out _);

        Assert.Contains(messages, m => m.Type == MessageType.FullUpdate);
        Assert.Contains(messages, m => m.Type == MessageType.WeaponFire);

        // The roster compacts on PLAYER_LEAVE, and every state update after it
        // is a different width. A trace with one in it is worth more than a
        // longer one without.
        Assert.Contains(messages, m => m.Type == MessageType.PlayerLeave);
    }

    /// <summary>
    /// Every character in the full update carries the weapon block its class
    /// writes — two bytes, or one for a medic, or three-plus-mines for a
    /// demoman.
    /// </summary>
    /// <remarks>
    /// Measured on the running engine by swapping <c>global.serializeBuffer</c>
    /// and reading <c>buffer_size</c> either side of
    /// <c>with(currentWeapon) event_user(12)</c>: <c>Medigun</c> 1,
    /// <c>Minegun</c> 3 + 7n, and <c>Rocketlauncher</c>, <c>Rifle</c>,
    /// <c>Flamethrower</c>, <c>Blade</c> and <c>Scattergun</c> all 2.
    /// </remarks>
    [Fact]
    public void EveryClassWritesTheBlockItsWeaponInherits()
    {
        var messages = Frame(out var stream);

        var full = messages.First(m => m.Type == MessageType.FullUpdate);
        Assert.NotNull(full.State);

        var withCharacters = 0;
        var medics = 0;

        for (var slot = 0; slot < full.State.Players.Length; slot++)
        {
            var player = full.State.Players[slot];
            if (player.Character is null)
                continue;

            withCharacters++;

            // A medic's Medigun overrides User Event 12 without an inherited
            // action, so it is the only weapon that writes neither of the
            // parent's bytes — and the only one that writes a heal target.
            if (player.HealTarget is not null)
            {
                medics++;
                Assert.Null(player.ReadyToShoot);
                Assert.Null(player.RefireAlarm);
                continue;
            }

            Assert.NotNull(player.ReadyToShoot);
            Assert.NotNull(player.RefireAlarm);

            // readyToShoot is written straight out of a GML boolean.
            Assert.InRange(player.ReadyToShoot!.Value, (byte)0, (byte)1);
        }

        Assert.True(withCharacters >= 5, $"fixture has only {withCharacters} characters in its FULL_UPDATE");
        Assert.Equal(1, medics);
        Assert.True(stream.PlayerCount > 0);
    }

    /// <summary>
    /// This server's own <c>FULL_UPDATE</c> round-trips through the reader, for
    /// every class — including the three that write a different block.
    /// </summary>
    /// <remarks>
    /// The writer had the same wrong layout as the reader, so fixing only one of
    /// them would have left them agreeing with each other and disagreeing with
    /// every stock client. Reading this server's real bytes back with
    /// <see cref="ServerStream"/> is not enough on its own to catch that — both
    /// sides could be wrong together — which is why
    /// <see cref="FramesARecordingThatContainsCharacters"/> above reads the
    /// <em>reference's</em> bytes with the same reader. One test pins the format,
    /// this one pins the writer to it.
    /// <para>
    /// A second client is needed because a <c>FULL_UPDATE</c> goes out on join:
    /// the first client picks a team and gets a character, and the second one's
    /// join burst is then the first full update in this repo's history to carry
    /// one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Scout)]
    [InlineData(PlayerClass.Medic)]
    [InlineData(PlayerClass.Demoman)]
    [InlineData(PlayerClass.Heavy)]
    public void ThisServersFullUpdateRoundTripsForEveryBlockShape(PlayerClass playerClass)
    {
        const string Map = "ctf_truefort";
        var map = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

        using var harness = new ServerHarness(o => o.Map = Map);

        var player = harness.JoinClient("shooter");
        harness.Tick();
        player.Take(player.Received.Count);

        player.SendChangeClass(playerClass);
        player.SendChangeTeam(Team.Red);
        harness.Tick();
        harness.Tick();

        // The observer's join burst carries the FULL_UPDATE, and by now the
        // first client has a character in it.
        var observer = harness.JoinClient("observer");
        harness.Tick();
        observer.Poll();

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(map));

        var buffer = new ReceiveBuffer();
        buffer.Append(observer.Take(observer.Received.Count));

        StateUpdate? full = null;
        while (stream.TryRead(buffer, out var message))
        {
            if (message.Type == MessageType.FullUpdate)
                full = message.State;
        }

        Assert.NotNull(full);

        var block = full.Players.FirstOrDefault(p => p.Character is not null);
        Assert.NotNull(block);

        if (playerClass == PlayerClass.Medic)
        {
            // 255 is "healing nobody". Zero would name roster slot 0.
            Assert.Equal((byte)255, block.HealTarget);
            Assert.Null(block.ReadyToShoot);
        }
        else
        {
            Assert.Null(block.HealTarget);
            Assert.NotNull(block.ReadyToShoot);
            Assert.NotNull(block.RefireAlarm);
        }

        if (playerClass == PlayerClass.Demoman)
            Assert.Empty(block.Mines);
    }
}
