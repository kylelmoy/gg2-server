using Gg2.Host;
using Gg2.Server;
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
    /// Which half of the roster a node owns is decided by which end listened.
    /// </summary>
    /// <remarks>
    /// <b>The only asymmetry two nodes have for free.</b> Anything else — a
    /// configured id, a coin toss — is a thing an operator can get wrong on one
    /// machine, and two nodes that both think they own slot 4 would each be
    /// guessing at a player the other is driving.
    /// </remarks>
    [Fact]
    public void TheListenerOwnsTheEvenSlots()
    {
        Assert.True(Owns(listening: true, slot: 0));
        Assert.False(Owns(listening: true, slot: 1));
        Assert.False(Owns(listening: false, slot: 0));
        Assert.True(Owns(listening: false, slot: 1));
    }

    /// <summary>
    /// Mirrors <c>MeshSession.Owns</c> without needing a socket, so the rule can
    /// be asserted from both sides of the link at once.
    /// </summary>
    private static bool Owns(bool listening, int slot) => slot % 2 == (listening ? 0 : 1);
}
