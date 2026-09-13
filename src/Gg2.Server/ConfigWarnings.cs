using Gg2.Server.Plugins;

namespace Gg2.Server;

/// <summary>
/// Configuration that is accepted, applied, reported back — and cannot do
/// anything on this server.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A different failure from an invalid value, and nothing else catches
/// it.</b> <see cref="ServerOptions.Validate"/> refuses a value that is out of
/// range; <c>ConfigFileCoverageTests</c> proves every key reaches its property.
/// Both pass for a setting that is perfectly valid and delivered by a subsystem
/// this server is not running — the operator sets it, the settings page shows it
/// back, a restart applies it, and nothing happens. That is the failure
/// <c>CLAUDE.md</c> names as the one no test can catch, in its cheapest form.
/// </para>
/// <para>
/// <b>One wording, three surfaces.</b> The server says these at startup, the
/// fleet editor says them on the page where somebody sets them, and
/// <c>config render</c> says them before a push. Each caller prefixes the host
/// it is talking about; the sentence itself lives here so the three cannot drift
/// into saying slightly different things about the same server.
/// </para>
/// <para>
/// ⚠️ <b>A warning, never a refusal.</b> Setting a join line before adding the
/// plugin that carries it is a reasonable order to work in, and a configuration
/// that would not start is far worse than one that says what it is missing.
/// </para>
/// </remarks>
public static class ConfigWarnings
{
    /// <summary>
    /// Everything <paramref name="options"/> asks for that this server cannot
    /// deliver. Empty is the ordinary answer.
    /// </summary>
    public static IReadOnlyList<string> For(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var warnings = new List<string>();

        // Both of these are delivered by a chat plugin and by nothing else, and
        // ServerPluginList is EMPTY by default — so the obvious way to configure
        // either is on a server that can never show it to anybody.
        if (!ServerPluginCatalogue.ListHasChatConsole(options.ServerPluginList))
        {
            if (options.ChatJoinMessage.Length != 0)
            {
                warnings.Add(
                    "ChatJoinMessage is set but no chat plugin is loaded, so nobody will see it. "
                    + "Add chat or chat_v2_pr to ServerPluginList");
            }

            // ⚠️ Says what still works. The control port and the admin panel reach
            // a suspension without any plugin, so "no player can type /bots" is
            // the whole of what is lost and the warning should not overstate it.
            if (options.BotSuspendMinutes > 0)
            {
                warnings.Add(
                    $"Bots.SuspendMinutes is {options.BotSuspendMinutes} but no chat plugin is "
                    + "loaded, so no player can type /bots. Add chat or chat_v2_pr to "
                    + "ServerPluginList; the control port's bots-suspend still works");
            }
        }

        return warnings;
    }
}
