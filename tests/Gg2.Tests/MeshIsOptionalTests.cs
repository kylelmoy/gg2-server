using Gg2.Host;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Server;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// The mesh is an option, and a server that was not asked for one is exactly the
/// server it always was.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>This is the promise the whole feature is allowed to exist under.</b>
/// The fleet runs standalone servers with real players on them; a rollback
/// engine that changed how those tick would be a regression dressed as a
/// feature. The tick path branches on one null field, and these assert that the
/// field is null unless somebody asked.
/// </para>
/// <para>
/// The rest of the suite is the other half of the same claim: 3,500-odd tests
/// exercise a standalone server and none of them mention the mesh.
/// </para>
/// </remarks>
public sealed class MeshIsOptionalTests
{
    [Fact]
    public void ADefaultServerHasNoMeshAtAll()
    {
        var options = CommandLine.Parse([]);

        Assert.False(options.MeshEnabled);
        Assert.Equal(0, options.MeshListenPort);
        Assert.Equal(string.Empty, options.MeshConnect);
    }

    /// <summary>
    /// Every flag a normal server is started with leaves the mesh off.
    /// </summary>
    /// <remarks>
    /// The fleet's own shape, near enough: a map, a player limit, bots, the
    /// panel. If any of these ever turned the mesh on by accident, a live server
    /// would start rewinding.
    /// </remarks>
    [Fact]
    public void TheFleetsOwnFlagsLeaveItOff()
    {
        var options = CommandLine.Parse(
            ["--map", "ctf_truefort", "--players", "16", "--bots", "8", "--web", "8191"]);

        Assert.False(options.MeshEnabled);
    }

    /// <summary>
    /// Naming a port turns it on, and the explicit switch turns it back off.
    /// </summary>
    /// <remarks>
    /// Both directions, for the reason <c>ControlPortEnabled</c> has both: a
    /// setting that can only be turned on cannot be turned off by a config file
    /// that once had it.
    /// </remarks>
    [Fact]
    public void APortTurnsItOnAndNoMeshTurnsItOff()
    {
        Assert.True(CommandLine.Parse(["--mesh-listen", "27600"]).MeshEnabled);
        Assert.True(CommandLine.Parse(["--mesh-connect", "host:27600"]).MeshEnabled);

        Assert.False(
            CommandLine.Parse(["--mesh-listen", "27600", "--no-mesh"]).MeshEnabled);
    }

    /// <summary>
    /// The config file reaches it too, both ways round.
    /// </summary>
    /// <remarks>
    /// <c>ConfigFileCoverageTests</c> already asserts every settable option has a
    /// row; this asserts the rows mean what they say, which is the part a
    /// coverage test cannot check.
    /// </remarks>
    [Fact]
    public void TheConfigFileReachesIt()
    {
        var on = new ServerOptions();
        ServerSettings.Find("Mesh.ListenPort")!.Set(on, "27600");

        Assert.True(on.MeshEnabled);
        Assert.Equal(27600, on.MeshListenPort);

        ServerSettings.Find("Mesh.Enabled")!.Set(on, "0");
        Assert.False(on.MeshEnabled);

        var joined = new ServerOptions();
        ServerSettings.Find("Mesh.Connect")!.Set(joined, "gg2-us-west:27600");

        Assert.True(joined.MeshEnabled);
        Assert.Equal("gg2-us-west:27600", joined.MeshConnect);
    }

    /// <summary>
    /// ⛔ A slot belongs to the node whose join created it, whichever place it
    /// landed in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ownership is what decides whose inputs are real and whose are
    /// guessed</b>, so two nodes that both thought they owned slot 4 would each
    /// be predicting a player the other is driving. It cannot be a slot parity:
    /// a join takes the lowest free place, and a departure renumbers everything
    /// above it, so the two nodes' players interleave in whatever order people
    /// arrived.
    /// </para>
    /// <para>
    /// ⚠️ Asserted against a real <see cref="MeshNode"/> rather than against a
    /// local copy of the rule. A test that reimplements the thing it is checking
    /// passes whatever the real code goes on to do — which is what the parity
    /// version of this test did after ownership stopped being a parity.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASlotBelongsToTheNodeWhoseJoinCreatedIt()
    {
        ReferenceTree.RequireAvailable();

        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);
        var state = default(WorldState);
        state.AddPlayer(0);

        var node = new MeshNode(
            new CollisionWorld(map), state, new MeshOptions(), new SpawnPoints(map));

        // One join from each end, landing on the same tick. They take
        // consecutive places, ordered by origin — and each place is owned by
        // whoever asked for it.
        node.Schedule(4, MeshCommand.AddPlayer(origin: 1, sequence: 1));
        node.Schedule(4, MeshCommand.AddPlayer(origin: 0, sequence: 1));
        node.Advance(6);

        Assert.Equal(0, node.OriginOf(1));
        Assert.Equal(1, node.OriginOf(2));

        // ⛔ And a departure moves ownership with the slot. Under a parity rule
        // slot 1 would still be the listener's; here it is the joiner's player,
        // shifted down.
        node.Schedule(10, MeshCommand.RemovePlayer(origin: 0, sequence: 1));
        node.Advance(12);

        Assert.Equal(1, node.OriginOf(1));
    }
}
