using GG2.Protocol;
using GG2.Server.Plugins;

namespace GG2.Server;

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
/// <c>CONTRIBUTING.md</c> names as the one no test can catch, in its cheapest form.
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

        // Both of these are delivered by chat and by nothing else: the server's
        // own (on by default) or chat_v2_pr. With chat off and no chat_v2_pr,
        // either is configured on a server that can never show it to anybody.
        if (!options.Chat.Enabled && !ServerPluginCatalogue.ListHasChatConsole(options.ServerPluginList))
        {
            if (options.ChatJoinMessage.Length != 0)
            {
                warnings.Add(
                    "ChatJoinMessage is set but chat is off, so nobody will see it. "
                    + "Turn on [Chat] Enabled, or add chat_v2_pr to ServerPluginList");
            }

            // ⚠️ Says what still works. The control port and the admin panel reach
            // a suspension without any plugin, so "no player can type /bots" is
            // the whole of what is lost and the warning should not overstate it.
            if (options.BotSuspendMinutes > 0)
            {
                warnings.Add(
                    $"Bots.SuspendMinutes is {options.BotSuspendMinutes} but chat is off, so no "
                    + "player can type /bots. Turn on [Chat] Enabled, or add chat_v2_pr to "
                    + "ServerPluginList; the control port's bots-suspend still works");
            }
        }

        // ⚠️ Limits that cannot seat the fill stop the population short of it, in
        // silence: the manager simply finds no class with room and adds nobody.
        // 255 is unlimited, so any class left at 255 can seat everyone.
        if (options.Bots.Enabled)
        {
            var wanted = Math.Min(options.Bots.MaxBots, options.Bots.FillToPlayers);
            var perTeam = 0;
            var unlimited = false;

            foreach (var limit in options.Bots.EffectiveClassLimits[..((int)PlayerClass.Pyro + 1)])
            {
                unlimited |= limit == 255;
                perTeam += limit;
            }

            if (!unlimited && perTeam * 2 < wanted)
            {
                warnings.Add(
                    $"BotClasslimits seat at most {perTeam} bots a team, {perTeam * 2} in all, "
                    + $"but the bot fill asks for up to {wanted}. The population will stop at "
                    + $"{perTeam * 2}; raise a limit or lower Bots.FillToPlayers");
            }
        }

        return warnings;
    }
}
