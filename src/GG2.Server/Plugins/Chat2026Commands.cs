using System.Globalization;
using System.Text;
using GG2.Protocol;

namespace GG2.Server.Plugins;

/// <summary>
/// chat2026's command console: <c>host/command.gml</c>, <c>host/fn_parse_args.gml</c>,
/// <c>host/fn_find_player.gml</c> and <c>src/commands/*.gml</c>.
/// </summary>
/// <remarks>
/// Replies are NOTICE category 5 to the sender only, and a command is never
/// relayed as chat. Every reply string is the GML's, byte for byte: a client
/// cannot tell which host it is talking to.
/// </remarks>
public sealed partial class Chat2026Plugin
{
    private delegate void Command(int sender, List<string> args, string rest);

    private sealed record CommandEntry(Command Run, bool AdminOnly);

    private Dictionary<string, CommandEntry>? _commands;

    /// <summary>
    /// <c>global.chatHelp</c>: every command's help line, including the two the
    /// client handles itself. Ordinal order is GM8's <c>ds_map</c> order, which is
    /// the order <c>/help</c> lists them in.
    /// </summary>
    private static readonly SortedDictionary<string, string> Help = new(StringComparer.Ordinal)
    {
        ["help"] = "/help [command]: list the commands, or explain one.",
        ["me"] = "/me <action>: say it as an action, * Name waves.",
        ["players"] = "/players: every player with the #number other commands accept.",
        ["maplist"] = "/maplist [from]: the map rotation, ten at a time.",
        ["login"] = "/login <password>: become an admin for this session.",
        ["say"] = "/say <text>: a server message to everyone.",
        ["kick"] = "/kick <player>: kick a player.",
        ["mute"] = "/mute <player> [minutes]: stop a player chatting, by address, until it runs out.",
        ["unmute"] = "/unmute <player>: lift a mute.",
        ["nextround"] = "/nextround: end the round now.",
        ["map"] = "/map <name>: end the round now and go to that map.",
        ["votekick"] = "/votekick [player]: vote to kick a player. With no name, pick one.",
        ["votemute"] = "/votemute [player]: vote to stop a player chatting. With no name, pick one.",
        ["votescramble"] = "/votescramble: vote to shuffle the teams.",
        ["votenextround"] = "/votenextround: vote to end the round now.",
        ["votemap"] = "/votemap [map]: vote on the next map. With no name, pick one.",
        ["ignore"] = "/ignore [name]: hide a player's chat on your screen, or list who you ignore.",
        ["unignore"] = "/unignore <name>: show a player's chat again.",
    };

    private Dictionary<string, CommandEntry> Commands => _commands ??= new(StringComparer.Ordinal)
    {
        ["help"] = new(HelpCommand, false),
        ["me"] = new((sender, _, _) => Reply(sender, Help["me"]), false),
        ["players"] = new(PlayersCommand, false),
        ["maplist"] = new(MaplistCommand, false),
        ["login"] = new(LoginCommand, false),
        ["say"] = new(SayCommand, true),
        ["kick"] = new(KickCommand, true),
        ["mute"] = new(MuteCommand, true),
        ["unmute"] = new(UnmuteCommand, true),
        ["nextround"] = new(NextRoundCommand, true),
        ["map"] = new(MapCommand, true),
        ["votekick"] = new((sender, args, _) => TargetVote(sender, args, VoteHandler.Kick), false),
        ["votemute"] = new((sender, args, _) => TargetVote(sender, args, VoteHandler.Mute), false),
        ["votescramble"] = new(VoteScrambleCommand, false),
        ["votenextround"] = new(VoteNextRoundCommand, false),
        ["votemap"] = new(VoteMapCommand, false),
    };

    /// <summary><c>host/command.gml</c>: <paramref name="text"/> starts with <c>/</c>.</summary>
    private void RunCommand(int sender, string text)
    {
        text = text[1..];
        var space = text.IndexOf(' ');
        var name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : text[(space + 1)..].TrimStart(' ');

        if (!Commands.TryGetValue(name, out var command))
        {
            Reply(sender, "No such command. /help lists them.");
            return;
        }

        if (command.AdminOnly && !IsAdmin(sender))
        {
            Reply(sender, "/" + name + " is for server admins.");
            return;
        }

        command.Run(sender, ParseArgs(rest), rest);
    }

    /// <summary>Slot 0 never speaks here, so an admin is whoever used <c>/login</c>.</summary>
    private bool IsAdmin(int slot) => slot == 0 || _chatters[slot].Admin;

    /// <summary>
    /// <c>host/fn_parse_args.gml</c>: split on spaces, a double-quoted run being one
    /// argument. An unterminated quote runs to the end.
    /// </summary>
    internal static List<string> ParseArgs(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var have = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                have = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (have)
                    args.Add(current.ToString());

                current.Clear();
                have = false;
            }
            else
            {
                current.Append(c);
                have = true;
            }
        }

        if (have)
            args.Add(current.ToString());

        return args;
    }

    /// <summary>
    /// <c>host/fn_find_player.gml</c>: the one player an argument names, or -1
    /// after telling <paramref name="asker"/> why not. Accepts <c>chr(13)</c> plus
    /// a stable id (the picker's answer), <c>#n</c> (a roster index, as
    /// <c>/players</c> prints it), an exact name ignoring case, then a unique
    /// substring.
    /// </summary>
    private int FindPlayer(string query, int asker)
    {
        if (query.Length > 0 && query[0] == '\r')
        {
            var slot = long.TryParse(Digits(query), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? Host.SlotOfStableId(id)
                : -1;
            if (slot < 0)
                Reply(asker, "That player has left.");

            return slot;
        }

        if (query.Length > 1 && query[0] == '#' && Digits(query) == query[1..])
        {
            if (int.TryParse(query[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < Host.PlayerCount)
                return index;

            Reply(asker, "No player " + query + ". /players lists them.");
            return -1;
        }

        var found = Match(query, exact: true, out var count, out var names);
        if (count == 0)
            found = Match(query, exact: false, out count, out names);

        if (count == 1)
            return found;

        Reply(asker, count == 0
            ? "No player matches \"" + query + "\". /players lists them."
            : "\"" + query + "\" matches " + names + ". Use #number.");
        return -1;
    }

    private int Match(string query, bool exact, out int count, out string names)
    {
        var found = -1;
        count = 0;
        var list = new StringBuilder();

        for (var slot = 0; slot < Host.PlayerCount; slot++)
        {
            var name = Host.NameOf(slot);
            var hit = exact
                ? name.Equals(query, StringComparison.OrdinalIgnoreCase)
                : name.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!hit)
                continue;

            found = slot;
            count++;
            if (list.Length > 0)
                list.Append(", ");

            list.Append(name).Append(" (#").Append(slot.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        names = list.ToString();
        return found;
    }

    /// <summary>GML's <c>string_digits</c>.</summary>
    private static string Digits(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
            if (c is >= '0' and <= '9')
                builder.Append(c);

        return builder.ToString();
    }

    private static int DigitsOr(string text, int fallback)
    {
        var digits = Digits(text);
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    // --- commands/*.gml -----------------------------------------------------

    private void HelpCommand(int sender, List<string> args, string rest)
    {
        if (args.Count > 0)
        {
            var name = args[0].ToLowerInvariant();
            if (name.StartsWith('/'))
                name = name[1..];

            Reply(sender, Help.TryGetValue(name, out var line) ? line : "No such command. /help lists them.");
            return;
        }

        var list = new StringBuilder();
        var admin = new StringBuilder();
        foreach (var name in Help.Keys)
        {
            var target = Commands.TryGetValue(name, out var entry) && entry.AdminOnly ? admin : list;
            target.Append(" /").Append(name);
        }

        Reply(sender, "Commands:" + list + ". /help <command> for more.");
        if (IsAdmin(sender) && admin.Length > 0)
            Reply(sender, "Admin:" + admin + ".");
    }

    private void PlayersCommand(int sender, List<string> args, string rest)
    {
        for (var slot = 0; slot < Host.PlayerCount; slot++)
        {
            var team = Host.TeamOf(slot) switch
            {
                Team.Red => "RED",
                Team.Blue => "BLU",
                _ => "spectating",
            };
            Reply(sender, "#" + slot.ToString(CultureInfo.InvariantCulture) + " " + Host.NameOf(slot) + " (" + team + ")");
        }
    }

    private void MaplistCommand(int sender, List<string> args, string rest)
    {
        var rotation = Host.MapRotation;
        var n = rotation.Count;
        if (n <= 1)
        {
            Reply(sender, "This server is not rotating maps.");
            return;
        }

        var offset = args.Count > 0 ? DigitsOr(args[0], 0) : 0;
        offset = Math.Max(0, Math.Min(offset, n - 1));
        var last = Math.Min(n, offset + 10);

        var line = new StringBuilder("Maps ")
            .Append(offset + 1).Append('-').Append(last).Append(" of ").Append(n).Append(':');
        for (var i = offset; i < last; i++)
        {
            if (i > offset)
                line.Append(',');

            line.Append(' ').Append(rotation[i]);
            if (rotation[i].Equals(_currentMap, StringComparison.OrdinalIgnoreCase))
                line.Append(" (now)");
        }

        if (last < n)
            line.Append(". /maplist ").Append(last).Append(" for more.");

        Reply(sender, line.ToString());
    }

    private void LoginCommand(int sender, List<string> args, string rest)
    {
        if (_settings.AdminPassword.Length == 0)
        {
            Reply(sender, "Admin login is not enabled on this server.");
            return;
        }

        if (rest != _settings.AdminPassword)
        {
            Reply(sender, "Wrong password.");
            return;
        }

        _chatters[sender].Admin = true;
        Reply(sender, "You are now an admin for this session.");
    }

    private void SayCommand(int sender, List<string> args, string rest)
    {
        if (rest.Length == 0)
        {
            Reply(sender, Help["say"]);
            return;
        }

        SendNotice(Notice.Server, rest);
    }

    private void KickCommand(int sender, List<string> args, string rest)
    {
        if (args.Count < 1)
        {
            Reply(sender, Help["kick"]);
            return;
        }

        var target = FindPlayer(args[0], sender);
        if (target < 0)
            return;

        if (target == 0)
        {
            Reply(sender, "The host cannot be kicked.");
            return;
        }

        SendNotice(Notice.Server, Host.NameOf(target) + " was kicked by " + Host.NameOf(sender) + ".");
        Host.Kick(target, "Kicked by " + Host.NameOf(sender));
    }

    private void MuteCommand(int sender, List<string> args, string rest)
    {
        if (args.Count < 1)
        {
            Reply(sender, Help["mute"]);
            return;
        }

        var target = FindPlayer(args[0], sender);
        if (target < 0)
            return;

        var minutes = args.Count > 1 ? DigitsOr(args[1], _settings.MuteMinutes) : _settings.MuteMinutes;
        minutes = Math.Max(1, Math.Min(minutes, 1440));
        var expiry = Now + minutes * 60000L;
        _chatters[target].MutedUntil = expiry;
        SetExpiry(_mutes, Host.RemoteAddressOf(target), expiry);

        SendNotice(Notice.Server, minutes == 1
            ? Host.NameOf(target) + " was muted for 1 minute by " + Host.NameOf(sender) + "."
            : Host.NameOf(target) + " was muted for " + minutes + " minutes by " + Host.NameOf(sender) + ".");
    }

    private void UnmuteCommand(int sender, List<string> args, string rest)
    {
        if (args.Count < 1)
        {
            Reply(sender, Help["unmute"]);
            return;
        }

        var target = FindPlayer(args[0], sender);
        if (target < 0)
            return;

        _chatters[target].MutedUntil = 0;
        SetExpiry(_mutes, Host.RemoteAddressOf(target), 0);
        SendNotice(Notice.Server, Host.NameOf(target) + " was unmuted by " + Host.NameOf(sender) + ".");
    }

    private void NextRoundCommand(int sender, List<string> args, string rest)
    {
        if (Host.RoundOver)
        {
            Reply(sender, "The round is already over.");
            return;
        }

        SendNotice(Notice.Server, Host.NameOf(sender) + " ended the round.");
        Host.EndRound();
    }

    /// <summary>
    /// <c>commands/map.gml</c>. The GML reaches a rotation map through the
    /// rotation and any other map by overriding the next map; this server's
    /// <see cref="IPluginHost.SetNextMap"/> is one path for both.
    /// </summary>
    private void MapCommand(int sender, List<string> args, string rest)
    {
        if (args.Count < 1)
        {
            Reply(sender, Help["map"]);
            return;
        }

        if (Host.RoundOver)
        {
            Reply(sender, "The round is already over.");
            return;
        }

        var map = args[0];
        if (!Host.SetNextMap(map))
        {
            Reply(sender, "This server has no map called " + map + ".");
            return;
        }

        SendNotice(Notice.Server, Host.NameOf(sender) + " changed the map to " + map + ".");
        Host.EndRound();
    }
}
