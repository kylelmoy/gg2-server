using System.Reflection;
using Gg2.Bots;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// That the public lobby is OFF unless somebody asked for it.
/// </summary>
/// <remarks>
/// <b>This is a privacy property, not a feature test.</b> Lobby registration
/// publishes the server's address, name, map and player count to
/// <c>ganggarrison.com</c>. The reference has it on, because a person ticked a
/// box in a hosting menu to get there; a headless binary has no such moment, so
/// <c>docs/PROTOCOL.md</c> asks for a flag that defaults off. A regression here
/// would be silent, would not fail anything else, and would be discovered by
/// somebody finding their private server in a public list.
/// <para>
/// <b>There is deliberately NO test of the ON path.</b> Exercising it would
/// send a real datagram to a real third-party server from anyone's test run,
/// which is exactly the thing this file exists to prevent. The packet's contents
/// are covered by <see cref="LobbyRegistrationTests"/>, which needs no socket.
/// </para>
/// </remarks>
public sealed class LobbyOptInTests
{
    [Fact]
    public void TheOptionDefaultsToOff() => Assert.False(new ServerOptions().AnnounceToLobby);

    /// <summary>
    /// A server started with default options builds no announcer at all.
    /// </summary>
    /// <remarks>
    /// Reads the private field, because the observable alternative is "does a
    /// UDP datagram leave this machine", which a test must not answer by trying.
    /// The field being null is the mechanism that makes every call site's
    /// <c>_lobby?.</c> a no-op.
    /// </remarks>
    [Fact]
    public void ADefaultServerBuildsNoAnnouncer()
    {
        using var harness = new ServerHarness();

        Assert.Null(Announcer(harness.Server));
    }

    /// <summary>
    /// Nothing else in the option set turns it on as a side effect.
    /// </summary>
    /// <remarks>
    /// The switches most likely to grow an implicit "…and list it publicly"
    /// are the ones about being played on. Named individually so that adding one
    /// to this list is a deliberate act.
    /// </remarks>
    [Fact]
    public void NoOtherSwitchTurnsItOn()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Bots = BotPopulationOptions.Default with { Enabled = true, FillToPlayers = 4 };
            options.PlayerLimit = 12;
            options.ServerName = "public sounding name";
            options.Password = "hunter2";
            options.TickStats = true;
        });

        Assert.False(harness.Options.AnnounceToLobby);
        Assert.Null(Announcer(harness.Server));
    }

    private static object? Announcer(GameServer server) =>
        typeof(GameServer)
            .GetField("_lobby", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(server);
}
