using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// Configuration that is valid, applied, reported back — and cannot do anything
/// on this server.
/// </summary>
/// <remarks>
/// <b>Nothing else in the suite goes red for these.</b>
/// <c>ServerOptions.Validate</c> refuses an out-of-range value and
/// <c>ConfigFileCoverageTests</c> proves every key reaches its property; both
/// pass for a setting delivered by a subsystem the server is not running. So the
/// assertions below are all of the form "this combination warns and that one does
/// not", and the second half is the one that matters — a warning that fires
/// whatever is configured is noise an operator learns to skip.
/// </remarks>
public sealed class ConfigWarningsTests
{
    private static ServerOptions Options(
        string plugins = "", string joinMessage = "", int suspendMinutes = 0) =>
        new()
        {
            MapDirectory = "maps",
            ServerPluginList = plugins,
            ChatJoinMessage = joinMessage,
            BotSuspendMinutes = suspendMinutes,
        };

    /// <summary>
    /// A join line with no chat plugin is a line nobody can receive, and the
    /// server says so.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>ServerPluginList</c> is EMPTY by default</b>, so this is not an
    /// exotic misconfiguration — it is what happens to anybody who sets the join
    /// message first, which is the obvious order to do it in.
    /// </remarks>
    [Fact]
    public void AJoinLineWithNoChatPluginWarns()
    {
        var warning = Assert.Single(ConfigWarnings.For(Options(joinMessage: "hello")));

        Assert.Contains("ChatJoinMessage", warning, StringComparison.Ordinal);
        Assert.Contains("ServerPluginList", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>/bots</c> window with no chat plugin warns, and says what still
    /// works.
    /// </summary>
    /// <remarks>
    /// <b>The control port and the admin panel reach a suspension without any
    /// plugin</b>, so "no player can type /bots" is the whole of what is lost.
    /// A warning that said the setting did nothing would be wrong.
    /// </remarks>
    [Fact]
    public void ABotSuspendWindowWithNoChatPluginWarnsAndSaysWhatStillWorks()
    {
        var warning = Assert.Single(ConfigWarnings.For(Options(suspendMinutes: 15)));

        Assert.Contains("SuspendMinutes", warning, StringComparison.Ordinal);
        Assert.Contains("/bots", warning, StringComparison.Ordinal);
        Assert.Contains("bots-suspend still works", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Either chat plugin silences both, and a plugin that is not a chat plugin
    /// does not.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The half that stops this being a warning nobody can clear.</b> A
    /// check that looked only at whether the list was non-empty would leave a
    /// correctly configured server warning for ever, which is how an operator
    /// learns to ignore the line — so an unknown plugin name still warns, and
    /// each of the two real ones does not.
    /// </remarks>
    [Theory]
    [InlineData("chat")]
    [InlineData("chat_v2_pr")]
    [InlineData("chat_v2_pr,chat")]
    public void AChatPluginSilencesBoth(string plugins)
    {
        Assert.Empty(ConfigWarnings.For(
            Options(plugins: plugins, joinMessage: "hello", suspendMinutes: 15)));
    }

    /// <summary>A pinned <c>name@md5</c> entry is still that plugin.</summary>
    /// <remarks>
    /// The list on a real fleet is pinned — <c>ServerPluginCatalogue</c> explains
    /// why — so a check that only matched a bare name would warn on exactly the
    /// servers that are configured most carefully.
    /// </remarks>
    [Fact]
    public void APinnedChatPluginIsStillAChatPlugin()
    {
        Assert.Empty(ConfigWarnings.For(Options(
            plugins: "chat_v2_pr@46d0937c9206dcf27e036100d1ea8685",
            joinMessage: "hello",
            suspendMinutes: 15)));
    }

    /// <summary>A plugin this port does not implement does not count as chat.</summary>
    [Fact]
    public void AnUnknownPluginDoesNotCountAsAChatPlugin()
    {
        Assert.Equal(2, ConfigWarnings.For(
            Options(plugins: "something_else", joinMessage: "hello", suspendMinutes: 15)).Count);
    }

    /// <summary>
    /// A server that configures neither setting is silent, whatever its plugins.
    /// </summary>
    [Fact]
    public void AServerThatAsksForNeitherIsSilent()
    {
        Assert.Empty(ConfigWarnings.For(Options()));
        Assert.Empty(ConfigWarnings.For(Options(plugins: "chat")));
    }

    /// <summary>
    /// A malformed plugin list answers "no chat console" rather than throwing.
    /// </summary>
    /// <remarks>
    /// Every caller here is a diagnostic — a settings page, a deploy preview, a
    /// startup line — and none of them may be the thing that fails. The list
    /// itself is refused with a proper message by <c>PluginNames.ParseList</c> on
    /// the path that matters.
    /// </remarks>
    [Fact]
    public void AMalformedPluginListDoesNotThrow()
    {
        var warning = Assert.Single(ConfigWarnings.For(
            Options(plugins: new string('x', 5000), joinMessage: "hello")));

        Assert.Contains("ChatJoinMessage", warning, StringComparison.Ordinal);
    }
}
