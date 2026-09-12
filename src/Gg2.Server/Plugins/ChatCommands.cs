using System.Globalization;
using Gg2.Protocol;

namespace Gg2.Server.Plugins;

/// <summary>
/// The slash-command console and the vote dialog that sit on top of
/// <see cref="ChatPlugin"/>.
/// </summary>
/// <remarks>
/// Two sources, and they are different kinds of thing.
/// <para>
/// <b>The machinery is the plugin's own.</b> <c>global.executeChat</c> and
/// <c>global.parseCommand</c> (<c>chat_api.gml:153-262</c>), <c>global.sendVote</c>,
/// <c>global.handleVote</c>, <c>global.sendVoteResults</c>,
/// <c>global.findPlayer</c>, <c>global.selectPlayer</c> and the <c>select</c>
/// vote handler (<c>vote_api.gml:161-481</c>), and the two commands the zip ships
/// itself, <c>help</c> and <c>maplist</c> (<c>commands.gml:24-159</c>).
/// </para>
/// <para>
/// <b>The commands that call votes are not.</b> <c>votekick</c>, <c>votemute</c>,
/// <c>votescramble</c>, <c>votemap</c> and <c>party</c> are Lorgan3's separate
/// <b>local</b> server plugins (forum topic 33773) — dropped in a server's own
/// <c>Plugins/</c> directory and loaded by <c>loadplugins.gml</c>, so nothing is
/// sent to a client. They register into <c>global.commandMap</c> and
/// <c>global.voteCommandMap</c> and call this plugin's vote API, which is what
/// makes them portable here at all.
/// </para>
/// <para>
/// <b>This is where <c>chat</c> earns its place over <see cref="ChatV2Plugin"/>.</b>
/// Those five plugins were written against <em>this</em> plugin's API, and
/// <c>chat_v2_pr</c> has none of it: no command parsing, no
/// <c>Player.gid</c>, and no vote dialog. Ported onto <c>chat_v2_pr</c> the whole
/// vote UI had to become chat lines answered by typing. Here the client already
/// has the dialog, so a vote is a dialog, answered with a number key, with a
/// live bar chart and a clock.
/// </para>
/// <para>
/// ⚠️ <b>Nothing here reaches the simulation directly.</b> A kick, a team change
/// and a round end all go through the same <see cref="GameServer"/> method a
/// client command or the control port uses, and a shuffle draws from
/// <see cref="IPluginHost.Random"/> and never from <c>WorldState.Random</c> —
/// that stream is observable simulation, so a map-list shuffle taken from it
/// would move every client's re-derived weapon spread.
/// </para>
/// </remarks>
public sealed partial class ChatPlugin
{
    private enum VoteKind
    {
        Kick,
        Mute,
        Scramble,
        NextRound,

        /// <summary>One named map, yes or no.</summary>
        SingleMap,

        /// <summary>Several maps, most votes wins.</summary>
        MapChoice,

        /// <summary>
        /// The player picker: a vote only its caller sees, answered once, whose
        /// answer re-dispatches the command that opened it.
        /// </summary>
        SelectPlayer,
    }

    /// <summary>
    /// <c>sendVote(30 * 30, ...)</c>: how long a vote runs, in ticks.
    /// </summary>
    /// <remarks>
    /// Goes on the wire as a <c>ushort</c> of steps and the client divides by
    /// its own delta factor (<c>plugin.gml:549</c>), so the dialog's clock and
    /// this counter are the same quantity.
    /// </remarks>
    private const int VoteTicks = 30 * 30;

    /// <summary>
    /// <c>votemap.gml:83</c>: how many maps a map vote offers before "keep
    /// default".
    /// </summary>
    private const int MaxMapOptions = 8;

    /// <summary>
    /// <c>vote_api.gml:396</c>: how many players one page of the picker shows
    /// before "Next page".
    /// </summary>
    private const int PlayersPerPage = 8;

    /// <summary>
    /// The dialog reads a single digit, and 0 closes it
    /// (<c>vote_api.gml:53-77</c>).
    /// </summary>
    private const int MaxOptions = 9;

    private sealed class Vote
    {
        public required VoteKind Kind { get; init; }

        public required string Question { get; init; }

        public required string[] Options { get; init; }

        public int TicksLeft { get; set; } = VoteTicks;

        /// <summary>Votes per option, for the dialog's bars and for the result.</summary>
        public int[] Tally { get; set; } = [];

        /// <summary>Who has answered, so "everybody voted" can end it early.</summary>
        public HashSet<long> Answered { get; } = [];

        /// <summary>The player a kick or a mute is about. <see cref="IPluginHost.StableIdOf"/>.</summary>
        public long TargetId { get; init; } = -1;

        /// <summary>Their name when the vote was called, for the result line if they leave.</summary>
        public string TargetName { get; init; } = string.Empty;

        /// <summary>
        /// <c>votePlayer</c>: the only player who sees and answers this vote, or
        /// -1 when everybody does.
        /// </summary>
        public long AudienceId { get; init; } = -1;

        /// <summary><see cref="VoteKind.SelectPlayer"/>: the command waiting for a name.</summary>
        public string PendingCommand { get; init; } = string.Empty;

        /// <summary><see cref="VoteKind.SelectPlayer"/>: the players this page offers.</summary>
        public long[] Candidates { get; init; } = [];
    }

    /// <summary>
    /// <c>global.chatWindow.alarm[0]</c>: the one vote that may be running.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>That alarm is the vote timer, not the chat window's fade.</b> In
    /// <see cref="ChatV2Plugin"/>'s GML the same alarm is a four-second fade, so
    /// <c>alarm[0] &gt; 0</c> reads there as "somebody spoke recently". Here the
    /// refusal message divides it by 30 and calls the result seconds, and
    /// <c>sendVote</c> is given <c>30 * 30</c>. Reading it the other way would
    /// ship a cooldown nobody asked for.
    /// </remarks>
    private Vote? _vote;

    /// <summary>
    /// <c>selectedmap</c>: a map vote can only succeed once per round
    /// (<c>votemap.gml:42</c>).
    /// </summary>
    private bool _mapVoteUsed;

    /// <summary>
    /// Every command the host answers, its <c>global.helpMap</c> text, and
    /// whether it starts a vote.
    /// </summary>
    /// <remarks>
    /// One table, so <c>/help</c> cannot list a command that does not exist or
    /// miss one that does.
    /// <para>
    /// <b>The missing ones are the client's.</b> <c>/host</c>, <c>/me</c> and
    /// <c>/block</c> live in <c>global.clientCommandMap</c>
    /// (<c>commands.gml:58-126</c>) and never reach a server: the client runs
    /// them locally. <c>/me</c> is the one with a server-visible effect, and it
    /// arrives as an ordinary chat message whose first byte is
    /// <c>chr(13)</c> — relayed like any other, and rendered as
    /// "<c>*name does the thing</c>" by the client's <c>doColorCode</c>.
    /// </para>
    /// <para>
    /// The <c>Vote</c> column is <c>global.voteCommandMap</c> membership, and it
    /// is what <c>chat_api.gml:174-177</c> puts the one-vote-per-minute cooldown
    /// on.
    /// </para>
    /// </remarks>
    private static readonly (string Name, string Help, bool Vote)[] Commands =
    [
        ("help", "This command displays a list of all commands (including serverside ones). "
            + "Use /help [Command] for details about that command.", false),
        ("maplist", "This command lists 10 maps, starting from offset, or from the current map "
            + "if no offset is specified. Use /maplist [offset]", false),
        ("party", "Party hard.", false),
        ("votekick", "Starts a vote to kick the selected player. Use /votekick [Name]", true),
        ("votemute", "Starts a vote to mute the selected player. Use /votemute [Name]", true),
        ("votescramble", "Starts a vote to scramble the teams. Use /votescramble", true),
        ("votenextround", "Starts a vote to end the round. Use /votenextround", true),
        ("votemap", "Starts a vote to set the next map. Use /votemap [Mapname/Map prefix]", true),
    ];

    // --- executeChat ---------------------------------------------------------

    /// <summary>
    /// <c>global.executeChat</c> (<c>chat_api.gml:153-206</c>): a line from a
    /// player is either a command or something to relay.
    /// </summary>
    /// <remarks>
    /// <b>The client half does not know commands exist.</b> It sends
    /// <c>/votekick bob</c> as an ordinary chat message; the server sees the
    /// leading slash, acts, and never echoes it. So a client running the site's
    /// GML — and a client with no plugin at all — is unaffected by every command
    /// here.
    /// </remarks>
    private void ExecuteChat(int slot, string message, int audience)
    {
        if (message.Length == 0 || message[0] != '/')
        {
            Relay(slot, message, audience);
            return;
        }

        var words = ParseCommand(message);
        var name = words[0].ToLowerInvariant();

        var found = false;
        var isVote = false;
        foreach (var (candidate, _, vote) in Commands)
        {
            if (candidate != name)
                continue;

            found = true;
            isVote = vote;
            break;
        }

        if (!found)
        {
            Reply(slot, $"{ColourOrange}Sorry this command does not exist. Type /help for console commands.");
            return;
        }

        if (!isVote)
        {
            Run(slot, name, words);
            return;
        }

        // chat_api.gml:175-181. lastVoteCall starts at 0 against a clock that
        // starts at 0, so this also refuses every vote for the first minute of
        // the server's life — see VoteCallCooldownMilliseconds.
        var chatter = _chatters[slot];
        var now = Host.CurrentTimeMilliseconds;

        if (now - VoteCallCooldownMilliseconds > chatter.LastVoteCall)
        {
            Run(slot, name, words);

            // Set after the call and unconditionally, which is the reference's
            // order: a vote the command then refuses still spends the minute.
            chatter.LastVoteCall = now;
            return;
        }

        var wait = (int)Math.Ceiling(
            (VoteCallCooldownMilliseconds - (now - chatter.LastVoteCall)) / 1000.0);

        Reply(slot, $"{ColourOrange}Please wait {wait} seconds before starting a vote.");
    }

    private void Run(int slot, string name, IReadOnlyList<string> words)
    {
        // Everything after the command word, whole: the reference's own commands
        // read argument 1 and ignore the rest, and a name or a map name that had
        // to be quoted has already been reassembled by ParseCommand.
        var argument = words.Count > 1 ? words[1] : string.Empty;

        switch (name)
        {
            case "help":
                CommandHelp(slot, words);
                break;

            case "maplist":
                CommandMapList(slot, words);
                break;

            case "party":
                // party.gml, one line to everybody. "S" is the random colour
                // (plugin.gml:612): this plugin's renderer knows it, unlike
                // chat_v2_pr's.
                Notice("SParty hard!");
                break;

            case "votekick":
                CommandVoteAgainstPlayer(slot, argument, VoteKind.Kick, "kick", name);
                break;

            case "votemute":
                CommandVoteAgainstPlayer(slot, argument, VoteKind.Mute, "mute", name);
                break;

            case "votescramble":
                CommandSimpleVote(slot, VoteKind.Scramble, "Scramble teams?");
                break;

            case "votenextround":
                CommandSimpleVote(slot, VoteKind.NextRound, "End the round?");
                break;

            case "votemap":
                CommandVoteMap(slot, argument);
                break;
        }
    }

    /// <summary>
    /// <c>global.parseCommand</c> (<c>chat_api.gml:209-262</c>): strip the
    /// slash, split on spaces, and honour double quotes.
    /// </summary>
    /// <remarks>
    /// <b>The quotes are load bearing.</b> The client's own <c>/help</c>
    /// re-sends the line as <c>/help "votekick"</c> (<c>commands.gml:16</c>), so
    /// a parser that kept the quotes would answer "no such command" for every
    /// <c>/help x</c> a real client asks.
    /// <para>
    /// The reference's <c>/me</c> special case — the whole remainder as one
    /// argument — is not transcribed: <c>me</c> is a client command, so a server
    /// never parses it. A modified client sending <c>/me</c> as a command gets
    /// "this command does not exist", which is what the reference answers too.
    /// </para>
    /// </remarks>
    private static List<string> ParseCommand(string message)
    {
        var words = new List<string>();
        var rest = message[1..];

        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            words.Add(rest);
            return words;
        }

        words.Add(rest[..space]);
        rest = rest[(space + 1)..];

        while (rest.Length != 0)
        {
            if (rest[0] == '"')
            {
                // The closing quote is the one followed by a space or by the end
                // of the line, so a quote inside an argument is kept.
                var close = -1;
                for (var i = 1; i < rest.Length; i++)
                {
                    if (rest[i] != '"')
                        continue;

                    if (i == rest.Length - 1 || rest[i + 1] == ' ')
                    {
                        close = i;
                        break;
                    }
                }

                if (close >= 0)
                {
                    words.Add(rest[1..close]);
                    rest = close + 2 <= rest.Length ? rest[(close + 2)..] : string.Empty;
                    continue;
                }

                // No closing quote: the reference drops the opening one and
                // takes the rest of the line.
                words.Add(rest[1..]);
                return words;
            }

            space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                words.Add(rest);
                return words;
            }

            words.Add(rest[..space]);
            rest = rest[(space + 1)..];
        }

        return words;
    }

    // --- the two commands the zip ships itself -------------------------------

    /// <summary><c>commands.gml:24-55</c>.</summary>
    private void CommandHelp(int slot, IReadOnlyList<string> words)
    {
        if (words.Count == 1)
        {
            var list = string.Empty;
            for (var i = 0; i < Commands.Length; i++)
            {
                list += i == 0 ? Commands[i].Name
                    : i == Commands.Length - 1 ? " and " + Commands[i].Name
                    : ", " + Commands[i].Name;
            }

            Reply(slot, $"{ColourOrange}Available server commands: {list}");
            return;
        }

        var wanted = words[1].ToLowerInvariant();

        foreach (var (name, help, _) in Commands)
        {
            if (name != wanted)
                continue;

            Reply(slot, ColourOrange + help);
            return;
        }

        Reply(slot, $"{ColourOrange}This command does not have a help message or does not exist.");
    }

    /// <summary>
    /// <c>commands.gml:128-158</c>: ten maps from the rotation, starting at the
    /// current one or at a given offset.
    /// </summary>
    /// <remarks>
    /// The header's range is the reference's and it is off by one — it claims
    /// <c>offset + 11</c> while the loop stops at <c>offset + 10</c>, so a full
    /// page reads "(1-11/22)" and lists ten. Cosmetic, client-visible, and
    /// transcribed.
    /// <para>
    /// The empty-rotation branch is this port's: a single-map server has no
    /// rotation at all, where the reference's <c>global.map_rotation</c> always
    /// had at least the map it was playing. Without it this indexes past the end.
    /// </para>
    /// </remarks>
    private void CommandMapList(int slot, IReadOnlyList<string> words)
    {
        var rotation = Host.MapRotation;
        if (rotation.Count == 0)
        {
            Reply(slot, $"{ColourOrange}This server is not rotating maps.");
            return;
        }

        var offset = words.Count == 1
            ? Host.CurrentMapIndex
            : Math.Max(0, Digits(words[1]) - 1);

        if (offset + 10 >= rotation.Count)
            offset = Math.Max(0, rotation.Count - 10);

        var message =
            $"{ColourOrange}Maplist ({offset + 1}-{Math.Min(offset + 11, rotation.Count)}"
            + $"/{rotation.Count}): {rotation[offset]}";

        for (var i = offset + 1; i < rotation.Count && i < offset + 10; i++)
            message += " - " + rotation[i];

        Reply(slot, message);
    }

    /// <summary>
    /// GML's <c>real(string_digits(s))</c>: every digit in the string, read as a
    /// number, and 0 when there are none.
    /// </summary>
    private static int Digits(string text)
    {
        var digits = string.Empty;
        foreach (var c in text)
            if (c is >= '0' and <= '9')
                digits += c;

        // Bounded rather than parsed straight: "9999999999" is a number a player
        // can type and int.Parse would throw on it.
        if (digits.Length == 0)
            return 0;

        if (digits.Length > 9)
            digits = digits[..9];

        return int.Parse(digits, CultureInfo.InvariantCulture);
    }

    // --- calling a vote ------------------------------------------------------

    /// <summary>
    /// <c>vote_api.gml:377-385</c>, the guard every vote command opens with:
    /// one vote at a time.
    /// </summary>
    private bool RefuseWhileVoting(int slot)
    {
        if (_vote is null)
            return false;

        var seconds = (int)Math.Ceiling(_vote.TicksLeft / 30.0);
        Reply(
            slot,
            $"{ColourOrange}You may not call vote at the moment. "
            + $"Please wait at least {seconds} seconds.");

        return true;
    }

    private void CommandVoteAgainstPlayer(int slot, string argument, VoteKind kind, string verb, string command)
    {
        var target = FindPlayer(slot, argument, command);

        // Either nobody matched — FindPlayer has already said so — or the player
        // picker is now open and will call back into here with a stable id.
        if (target < 0)
            return;

        // votekick.gml:23-29 — the host is not kickable. Slot 0 is this server's
        // own reserved player, which is the same thing.
        if (target == 0)
        {
            Reply(slot, $"{ColourOrange}You cannot {verb} the host.");
            return;
        }

        if (!Host.IsClient(target))
        {
            // Not in the reference, which had no bots. Kicking one would work
            // and mean nothing; muting one is meaningless outright.
            Reply(slot, $"{ColourOrange}You cannot {verb} a bot.");
            return;
        }

        if (RefuseWhileVoting(slot))
            return;

        var name = Host.NameOf(target);

        StartVote(new Vote
        {
            Kind = kind,
            Question = $"{char.ToUpperInvariant(verb[0])}{verb[1..]} player {Sanitise(name)}?",
            Options = ["Yes", "No"],
            TargetId = Host.StableIdOf(target),
            TargetName = name,
        });
    }

    private void CommandSimpleVote(int slot, VoteKind kind, string question)
    {
        if (RefuseWhileVoting(slot))
            return;

        StartVote(new Vote { Kind = kind, Question = question, Options = ["Yes", "No"] });
    }

    /// <summary><c>votemap.gml:42-131</c>.</summary>
    private void CommandVoteMap(int slot, string argument)
    {
        if (_mapVoteUsed)
        {
            // votemap.gml:44-45.
            Reply(slot, $"{ColourOrange}You can only vote for the next map once.");
            return;
        }

        if (RefuseWhileVoting(slot))
            return;

        var votable = Host.VotableMaps;
        if (votable.Count == 0)
        {
            Reply(slot, $"{ColourOrange}No maps were found!");
            return;
        }

        // votemap.gml:70 is `string_count(typed, lower(candidate)) > 0`, whose
        // arguments read backwards to a C# eye: GML's string_count counts
        // occurrences of the FIRST argument in the second, so this is "candidate
        // contains what was typed". An empty argument matches everything, which
        // is how a bare /votemap offers a random selection.
        var typed = argument.ToLowerInvariant();
        var matches = new List<string>();

        foreach (var map in votable)
            if (map.ToLowerInvariant().Contains(typed, StringComparison.Ordinal))
                matches.Add(map);

        if (matches.Count == 1)
        {
            StartVote(new Vote
            {
                Kind = VoteKind.SingleMap,
                Question = $"Set next map to {matches[0]}?",
                Options = ["Yes", "No"],
                TargetName = matches[0],
            });

            return;
        }

        if (matches.Count == 0)
        {
            // votemap.gml:88-96: a miss falls back to a random selection from
            // everything, and says so first.
            if (typed.Length != 0)
                Reply(slot, $"{ColourOrange}Selected map not found, using random selection.");

            matches.AddRange(votable);
        }

        Shuffle(matches);

        var options = new List<string>();
        for (var i = 0; i < MaxMapOptions && i < matches.Count; i++)
            options.Add(matches[i]);

        // votemap.gml:131 — the last option is always "no change", and the tally
        // treats a win for it as a vote for the rotation.
        options.Add(KeepDefault);

        StartVote(new Vote
        {
            Kind = VoteKind.MapChoice,
            Question = "Vote for the next map",
            Options = [.. options],
        });
    }

    private const string KeepDefault = "keep default";

    /// <summary>Fisher-Yates over <see cref="IPluginHost.Random"/>.</summary>
    /// <remarks>
    /// ⛔ Not <c>WorldState.Random</c>. That stream is observable simulation —
    /// every client re-derives a weapon's spread from its position — so
    /// shuffling a map list from it would make the game depend on how many
    /// people had called a vote.
    /// </remarks>
    private void Shuffle(List<string> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Host.Random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    // --- running a vote ------------------------------------------------------

    /// <summary>
    /// <c>global.sendVote</c> (<c>vote_api.gml:208-261</c>): open the dialog on
    /// every client entitled to answer.
    /// </summary>
    /// <remarks>
    /// <b>A player with <c>mayVote</c> false is not sent the poll at all</b> —
    /// not shown it, not counted, not able to answer — which is how
    /// <c>votekick.gml:40</c> keeps a vote's target out of their own vote.
    /// </remarks>
    private void StartVote(Vote vote)
    {
        _vote = vote;
        vote.Tally = new int[vote.Options.Length];

        if (vote.TargetId >= 0)
        {
            var target = Host.SlotOfStableId(vote.TargetId);
            if (target >= 0 && _chatters.Has(target))
                _chatters[target].MayVote = false;
        }

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
        {
            var chatter = _chatters[slot];

            if (!chatter.HasChat || !chatter.MayVote)
                continue;

            if (vote.AudienceId >= 0 && Host.StableIdOf(slot) != vote.AudienceId)
                continue;

            chatter.CanVote = true;
            SendPoll(slot, vote, resync: false);
        }

        Host.Log($"[chat] vote: {LogText.Safe(vote.Question)}");
    }

    /// <summary>
    /// Packet 200, or 204 with the tallies appended
    /// (<c>vote_api.gml:240-250</c>, <c>plugin.gml:458-472</c>).
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Never more than <see cref="MaxOptions"/> options.</b> The client
    /// writes a count byte it does not honour: <c>plugin.gml:545</c> reads
    /// <c>i &lt; _options &amp;&amp; i &lt; 9</c>, so a tenth option would leave
    /// the strings it skipped in the buffer and the clock would be read from the
    /// wrong offset — which the client reports several messages later as "The
    /// Server sent unexpected data". Every caller is bounded by construction:
    /// <see cref="MaxMapOptions"/> plus "keep default", and
    /// <see cref="PlayersPerPage"/> plus "Next page", are both exactly nine.
    /// </remarks>
    private void SendPoll(int slot, Vote vote, bool resync)
    {
        _out.Reset();
        _out.WriteUByte(resync ? SenderPollResync : SenderPoll);
        _out.WriteString1(Truncate(vote.Question));
        _out.WriteUByte((byte)vote.Options.Length);

        foreach (var option in vote.Options)
            _out.WriteString1(Truncate(option));

        _out.WriteUShort((ushort)vote.TicksLeft);

        if (resync)
            foreach (var count in vote.Tally)
                _out.WriteUByte((byte)Math.Min(byte.MaxValue, count));

        Channel.SendTo(slot, _out.WrittenSpan);
    }

    /// <summary>
    /// <c>plugin.gml:455-482</c>: a client that says hello mid-vote is caught up
    /// rather than left staring at nothing for the rest of the thirty seconds.
    /// </summary>
    /// <remarks>
    /// Only for a vote everybody is in. A player picker belongs to one caller,
    /// and the reference has no host-side dialog to resync from for one.
    /// </remarks>
    private void SendPollResync(int slot)
    {
        if (_vote is not Vote vote || vote.AudienceId >= 0)
            return;

        var chatter = _chatters[slot];
        if (!chatter.MayVote)
            return;

        chatter.CanVote = true;
        SendPoll(slot, vote, resync: true);
    }

    /// <summary>A poll string is truncated, not chunked — there is nowhere to put the rest.</summary>
    private static string Truncate(string text) =>
        text.Length > MaxChunkLength ? text[..MaxChunkLength] : text;

    /// <summary>
    /// <c>global.handleVote</c> (<c>vote_api.gml:161-205</c>): one answer, from
    /// one player.
    /// </summary>
    /// <param name="answer">The 1-based option the client's dialog reported.</param>
    private void HandleVote(int slot, int answer)
    {
        if (_vote is not Vote vote)
            return;

        if (answer < 1 || answer > vote.Options.Length || answer > MaxOptions)
            return;

        var id = Host.StableIdOf(slot);

        // votePlayer == sender: a picker resolves the moment its one viewer
        // answers, rather than running the clock down.
        if (vote.AudienceId >= 0)
        {
            if (vote.AudienceId != id)
                return;

            _vote = null;
            ClearVoteFlags();
            ResolveSelection(slot, vote, answer);
            return;
        }

        vote.Tally[answer - 1]++;
        vote.Answered.Add(id);

        // "tell plebs about the votes IN REAL TIME" (vote_api.gml:182): every
        // dialog's bar chart grows as the votes land, the voter's included.
        _out.Reset();
        _out.WriteUByte(SenderVoteTally);
        _out.WriteUByte((byte)(answer - 1));

        var payload = _out.WrittenSpan;

        for (var other = 0; other < Host.PlayerCount && _chatters.Has(other); other++)
            if (_chatters[other].HasChat && _chatters[other].MayVote)
                Channel.SendTo(other, payload);

        // vote_api.gml:200 compares against the whole roster. Here it is the
        // players who could actually answer — see Quorum for why, and it is the
        // same number on a server with no bots.
        if (vote.Answered.Count >= EligibleVoters())
            vote.TicksLeft = 1;
    }

    /// <summary>Counts the clock down and finishes the vote when it runs out.</summary>
    private void TickVote()
    {
        if (_vote is not Vote vote)
            return;

        vote.TicksLeft--;
        if (vote.TicksLeft > 0)
            return;

        _vote = null;

        // plugin.gml:239-242 resets mayVote for everybody BEFORE the vote
        // command runs, which is why the result packet below reaches the player
        // the vote was about.
        ClearVoteFlags();
        Finish(vote);
    }

    /// <summary><c>with(Player) mayVote = true</c>, and nobody has an open dialog.</summary>
    private void ClearVoteFlags()
    {
        for (var slot = 0; slot < _chatters.Count; slot++)
        {
            _chatters[slot].MayVote = true;
            _chatters[slot].CanVote = false;
        }
    }

    /// <summary>
    /// <c>global.sendVoteResults</c> (<c>vote_api.gml:264-291</c>): close the
    /// dialog, or highlight what won.
    /// </summary>
    /// <param name="win">
    /// The 1-based winning option, or <b>0 to just clear the dialog</b> with no
    /// result shown (<c>vote_api.gml:298</c>).
    /// </param>
    /// <param name="target">One slot, or -1 for everybody in chat.</param>
    private void SendVoteResult(int win, int target = -1)
    {
        _out.Reset();
        _out.WriteUByte(SenderVoteResult);
        _out.WriteUByte((byte)win);

        var payload = _out.WrittenSpan;

        if (target >= 0)
        {
            if (_chatters.Has(target) && _chatters[target].HasChat)
                Channel.SendTo(target, payload);

            return;
        }

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
            if (_chatters[slot].HasChat)
                Channel.SendTo(slot, payload);
    }

    /// <summary>Who a vote may hear from at all.</summary>
    /// <remarks>
    /// ⚠️ <b>The reference counts <c>ds_list_size(global.players)</c> — the
    /// whole roster, host and all — and this counts the players who could
    /// actually answer.</b> A deliberate difference, and not a small one: this
    /// server fills with bots by default, and a bot cannot press a key. Counting
    /// them would make the quorum unreachable on every bot server, so the
    /// feature would look implemented and never once pass a vote. The reference
    /// had no bots, so on its own servers the two sums are the same number.
    /// </remarks>
    private int EligibleVoters()
    {
        var voters = 0;
        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
            if (Host.IsClient(slot) && _chatters[slot].HasChat && _chatters[slot].MayVote)
                voters++;

        return voters;
    }

    /// <summary>How many must answer for a vote to count: <c>floor(voters / 2)</c>.</summary>
    private int Quorum() => EligibleVoters() / 2;

    private void Finish(Vote vote)
    {
        switch (vote.Kind)
        {
            // A picker nobody answered: the client's dialog has already timed
            // out on its own alarm, and there is nothing to decide.
            case VoteKind.SelectPlayer:
                return;

            case VoteKind.MapChoice:
                FinishMapChoice(vote);
                return;
        }

        var yes = vote.Tally[0];
        var no = vote.Tally[1];

        if (yes + no < Quorum())
        {
            Notice($"{ColourOrange}Voting failed (not enough people voted)");
            SendVoteResult(0);
            return;
        }

        // votekick.gml:76-82's three branches collapse to this: the two special
        // cases it spells out are what the division already gives, apart from
        // 0/0 — which the quorum check above only lets through on an empty
        // server.
        var percent = yes + no == 0 ? 0 : (int)Math.Floor(yes / (double)(yes + no) * 100);

        // Each plugin's own bar, and they are all different on purpose: a kick
        // is near-unanimous, a mute is a strong majority, and the two that only
        // change the round are a simple one.
        var passed = vote.Kind switch
        {
            VoteKind.Kick => percent >= 90,          // votekick.gml:89
            VoteKind.Mute => percent >= 70,          // votemute.gml:92
            VoteKind.SingleMap => percent >= 50,     // votemap.gml:204
            _ => percent > 50,                       // votescramble.gml:68, votemap.gml:280
        };

        if (!passed)
        {
            Notice($"{ColourOrange}Voting failed ({100 - percent}% no)");
            SendVoteResult(2);
            return;
        }

        SendVoteResult(1);

        switch (vote.Kind)
        {
            case VoteKind.Kick:
                ApplyKick(vote, percent);
                break;

            case VoteKind.Mute:
                ApplyMute(vote, percent);
                break;

            case VoteKind.Scramble:
                Notice($"{ColourOrange}Scrambling teams ({percent}% yes)");
                Scramble();
                break;

            case VoteKind.NextRound:
                Notice($"{ColourOrange}Ending round ({percent}% yes)");
                Host.EndRound();
                break;

            case VoteKind.SingleMap:
                ApplyNextMap(vote.TargetName);
                break;
        }
    }

    private void ApplyKick(Vote vote, int percent)
    {
        var slot = Host.SlotOfStableId(vote.TargetId);

        // The target may have left during the thirty seconds, which is the
        // commonest outcome of a kick vote. Nothing to do, and nothing to
        // apologise for — but do not let a stale slot index reach Kick.
        if (slot < 0)
        {
            Notice($"{ColourOrange}{Sanitise(vote.TargetName)} has already left.");
            return;
        }

        Notice($"{ColourOrange}Player {Sanitise(Host.NameOf(slot))} has been vote kicked ({percent}% yes)");

        // ⚠️ The kick is ONE disconnection, not a ban. The reference sets
        // `target.kicked` and keeps a `chatKicklist` of addresses this plugin
        // refuses on reconnect (plugin.gml:149-156 is the check, and nothing in
        // the zip ever adds to the list). A server-wide ban list is a bigger
        // decision than a chat plugin should make by itself, and a half-built
        // one that only some paths consult is worse than none. What does persist
        // is the chat mute, which shares the ban list below — so a vote-kicked
        // player can come back, and comes back unable to talk.
        //
        // Mute first: it reads the slot, and Kick ends the connection.
        Mute(slot);
        Host.Kick(slot, "vote kicked");
    }

    private void ApplyMute(Vote vote, int percent)
    {
        var slot = Host.SlotOfStableId(vote.TargetId);
        if (slot < 0)
        {
            Notice($"{ColourOrange}{Sanitise(vote.TargetName)} has already left.");
            return;
        }

        Notice($"{ColourOrange}Player {Sanitise(Host.NameOf(slot))} has been vote muted ({percent}% yes)");
        Mute(slot);
    }

    /// <summary>
    /// Takes a player out of chat and remembers their address, so a reconnect
    /// cannot rejoin it.
    /// </summary>
    private void Mute(int slot)
    {
        _chatters[slot].HasChat = false;

        var address = Host.RemoteAddressOf(slot);
        if (address.Length != 0 && !_banlist.Contains(address))
            _banlist.Add(address);
    }

    private void ApplyNextMap(string map)
    {
        if (!Host.SetNextMap(map))
        {
            Notice($"{ColourOrange}The maprotation has not been changed");
            return;
        }

        _mapVoteUsed = true;
        Notice($"{ColourOrange}Next map is {map}.");
    }

    /// <summary>
    /// <c>votemap.gml:141-183</c>: most votes wins, ties broken at random, and a
    /// win for "keep default" — or nobody voting at all — changes nothing.
    /// </summary>
    private void FinishMapChoice(Vote vote)
    {
        var winner = 0;
        var highest = vote.Tally[0];

        for (var i = 1; i < vote.Tally.Length; i++)
        {
            // The reference's own tie-break, `random(1) > 0.5`, which is a fresh
            // coin at each tie rather than a uniform pick among the leaders. Not
            // uniform, and not worth fixing: it is a tie.
            if (vote.Tally[i] > highest || (vote.Tally[i] == highest && Host.Random.NextDouble() > 0.5))
            {
                winner = i;
                highest = vote.Tally[i];
            }
        }

        if (highest == 0 || vote.Options[winner] == KeepDefault)
        {
            SendVoteResult(0);
            Notice($"{ColourOrange}The maprotation has not been changed");
            return;
        }

        SendVoteResult(winner + 1);
        ApplyNextMap(vote.Options[winner]);
    }

    /// <summary>
    /// <c>votescramble.gml:76-113</c>: shuffle everyone who is on a team, first
    /// half blue, second half red.
    /// </summary>
    /// <remarks>
    /// <b>Spectators are left alone</b>, so a scramble does not drag somebody
    /// who is watching into a game.
    /// <para>
    /// Bots are included, because they are on teams and leaving them put would
    /// make the scramble not a scramble. The reference had none.
    /// </para>
    /// </remarks>
    private void Scramble()
    {
        var players = new List<int>();
        for (var slot = 0; slot < Host.PlayerCount; slot++)
            if (Host.TeamOf(slot) != Team.Spectator)
                players.Add(slot);

        for (var i = players.Count - 1; i > 0; i--)
        {
            var j = Host.Random.Next(i + 1);
            (players[i], players[j]) = (players[j], players[i]);
        }

        // Read the target teams BEFORE moving anybody. SetTeam can kill a
        // character, and a class limit can bounce a class, but neither reorders
        // the roster — so the slots stay valid. If that ever stops being true,
        // this loop is where it breaks.
        for (var i = 0; i < players.Count; i++)
        {
            var team = i >= players.Count / 2.0 ? Team.Red : Team.Blue;
            Host.SetTeam(players[i], team);
        }
    }

    // --- finding a player ----------------------------------------------------

    /// <summary>
    /// <c>global.findPlayer</c> (<c>vote_api.gml:315-367</c>): resolve what
    /// somebody typed to one player, or open the picker.
    /// </summary>
    /// <returns>A slot, or -1 when the caller has been answered instead.</returns>
    /// <remarks>
    /// <b>Nothing typed opens the picker</b> — the reference's <c>is_real</c>
    /// branch, which fires because <c>ds_list_find_value</c> of a missing
    /// argument is the number 0. An exact name wins over a substring, and an
    /// ambiguous substring is refused rather than resolved: picking the first of
    /// two matches is how a vote kicks the wrong person.
    /// </remarks>
    private int FindPlayer(int caller, string text, string command)
    {
        if (text.Length == 0)
        {
            SelectPlayer(caller, command, 0);
            return -1;
        }

        // chr(13) + a stable id: what the picker hands back, and the only place
        // a stable id is ever written down. It never reaches the wire — clients
        // name players by position.
        if (text[0] == '\r')
        {
            return long.TryParse(text[1..], CultureInfo.InvariantCulture, out var id)
                ? Host.SlotOfStableId(id)
                : -1;
        }

        // Case sensitive, as the reference's `name == _name` is, and before the
        // substring pass: an exact "bob" beats a "bobby".
        for (var slot = 0; slot < Host.PlayerCount; slot++)
            if (Host.NameOf(slot) == text)
                return slot;

        var wanted = text.ToLowerInvariant();
        var found = -1;
        var count = 0;

        for (var slot = 0; slot < Host.PlayerCount; slot++)
        {
            if (!Host.NameOf(slot).ToLowerInvariant().Contains(wanted, StringComparison.Ordinal))
                continue;

            found = slot;
            count++;
        }

        if (count == 1)
            return found;

        Reply(
            caller,
            count > 1
                ? $"{ColourOrange}There are multiple players with this name, please be more specific "
                  + "or use this command with no arguments."
                : $"{ColourOrange}There are no players with this name, please try again or use this "
                  + "command with no arguments.");

        return -1;
    }

    /// <summary>
    /// <c>global.selectPlayer</c> (<c>vote_api.gml:370-406</c>): offer the
    /// caller a numbered list of players, as a vote only they can see.
    /// </summary>
    /// <param name="start">The roster index this page begins at.</param>
    /// <remarks>
    /// Everybody in the roster is listed, slot 0 and the bots included, which is
    /// the reference's <c>with(Player)</c>. Picking one of those is then refused
    /// by the command that asked — an extra round trip, and what a player on a
    /// reference server sees.
    /// </remarks>
    private void SelectPlayer(int caller, string command, int start)
    {
        if (RefuseWhileVoting(caller))
            return;

        var options = new List<string>();
        var candidates = new List<long>();

        for (var slot = start; slot < Host.PlayerCount && options.Count < PlayersPerPage; slot++)
        {
            options.Add($"{Sanitise(Host.NameOf(slot))} ({Host.PointsOf(slot)})");
            candidates.Add(Host.StableIdOf(slot));
        }

        if (options.Count == 0)
        {
            // vote_api.gml:448-455: the reference's own message for a page with
            // nobody on it, which its "Next page" conditions below can produce.
            Reply(caller, $"{ColourOrange}An unknown error occurred, please try again.");
            return;
        }

        // ⚠️ Transcribed, and wrong on the last page in both the reference and
        // here: the test is on the roster size rather than on whether anybody is
        // left, so a roster of exactly 8 offers a "Next page" that lands on the
        // message above. The reference shipping that message is the evidence it
        // happens there too (vote_api.gml:400, :457).
        if (Host.PlayerCount >= PlayersPerPage + (start == 0 ? 0 : 1))
            options.Add("Next page");

        StartVote(new Vote
        {
            Kind = VoteKind.SelectPlayer,
            Question = "Select a player.",
            Options = [.. options],
            AudienceId = Host.StableIdOf(caller),
            PendingCommand = command,
            Candidates = [.. candidates],
        });
    }

    /// <summary>
    /// <c>voteCommandMap['select']</c> (<c>vote_api.gml:409-481</c>): the picker
    /// was answered.
    /// </summary>
    private void ResolveSelection(int caller, Vote vote, int answer)
    {
        // Clear the caller's dialog before anything else opens one.
        SendVoteResult(0, caller);

        // "Next page" is whatever option sits past the candidates on this page.
        if (answer > vote.Candidates.Length)
        {
            var last = Host.SlotOfStableId(vote.Candidates[^1]);
            SelectPlayer(caller, vote.PendingCommand, last < 0 ? Host.PlayerCount : last + 1);
            return;
        }

        var chosen = vote.Candidates[answer - 1];

        // Re-dispatched with chr(13) + the stable id, which FindPlayer decodes —
        // one code path for "what the caller typed" and "what the caller picked",
        // and the id cannot go stale the way a slot index would.
        Run(caller, vote.PendingCommand, [vote.PendingCommand, "\r" + chosen.ToString(CultureInfo.InvariantCulture)]);
    }

    // --- per-round state -----------------------------------------------------

    /// <summary>
    /// A new round clears the once-per-round map vote, and abandons a vote that
    /// was still running.
    /// </summary>
    /// <remarks>
    /// Abandoning rather than finishing it: a scramble or a round-end vote that
    /// resolved into the round <em>after</em> the one it was called in would act
    /// on a world nobody voted about.
    /// </remarks>
    public override void MapChanged(string map)
    {
        _mapVoteUsed = false;

        if (_vote is null)
            return;

        _vote = null;
        ClearVoteFlags();
        SendVoteResult(0);
        Notice($"{ColourOrange}The vote was cancelled by the map change.");
    }
}
