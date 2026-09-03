using Gg2.Protocol;

namespace Gg2.Server.Plugins;

/// <summary>
/// The slash commands and the voting layer that sit on top of chat.
/// </summary>
/// <remarks>
/// Ported from Lorgan3's server-side chat extensions — <c>party.gml</c>,
/// <c>votekick.gml</c>, <c>votemap.gml</c>, <c>votemute.gml</c> and
/// <c>votescramble.gml</c> (forum topic 33773). They are <b>local</b> plugins in
/// the reference: dropped in the server's own <c>Plugins/</c> directory and
/// loaded by <c>loadplugins.gml</c>, so only the machine running the server ever
/// has them.
///
/// <para>
/// ⛔ <b>They extend a DIFFERENT chat plugin from the one this server
/// advertises, and that changes the design.</b> Every one of them registers into
/// <c>global.commandMap</c> / <c>global.voteCommandMap</c> and calls
/// <c>global.sendVote</c>, <c>global.sendVoteResults</c>, <c>global.findPlayer</c>
/// and <c>Player.gid</c>. <b>None of those exist in <c>chat_v2_pr</c></b>, which
/// has no command parsing and — the part that actually matters — <b>no vote
/// UI</b>. The clients here run <c>chat_v2_pr</c>'s GML and cannot be given one,
/// because this port does not get to change what the site serves.
/// </para>
/// <para>
/// So the vote dialog becomes <b>text</b>: the server announces the question and
/// the options as chat lines, and players answer by typing <c>/yes</c>,
/// <c>/no</c> or <c>/vote &lt;n&gt;</c>. Everything else — the thresholds, the
/// quorum, the thirty-second clock, the one-vote-at-a-time rule, the target not
/// being allowed to vote on their own kick — is the reference's, and is cited
/// per rule below.
/// </para>
/// <para>
/// ⚠️ <b>This works because the client half does not know commands exist.</b> It
/// sends "/votekick bob" as an ordinary chat message; the server recognises the
/// leading slash and never echoes it. Nothing on the wire changed.
/// </para>
/// </remarks>
public sealed partial class ChatV2Plugin
{
    /// <summary>What a finished vote does.</summary>
    private enum VoteKind
    {
        Kick,
        Mute,
        Scramble,
        NextRound,

        /// <summary>One named map, yes or no.</summary>
        SingleMap,

        /// <summary>Up to eight maps plus "keep default", most votes wins.</summary>
        MapChoice,
    }

    /// <summary>
    /// <c>sendVote(30 * 30, ...)</c> — thirty seconds at 30 Hz, in every one of
    /// the five plugins.
    /// </summary>
    private const int VoteTicks = 30 * 30;

    /// <summary>
    /// The most maps a choice vote offers, before "keep default" is appended.
    /// </summary>
    /// <remarks><c>votemap.gml:83</c>, <c>i &lt; 8</c>.</remarks>
    private const int MaxMapOptions = 8;

    /// <summary>A vote in flight.</summary>
    private sealed class Vote
    {
        public required VoteKind Kind { get; init; }

        public required string Question { get; init; }

        public required string[] Options { get; init; }

        public int TicksLeft { get; set; } = VoteTicks;

        /// <summary>
        /// Ballots by <b>stable id</b>, never by slot.
        /// </summary>
        /// <remarks>
        /// ⛔ A vote outlives disconnections by design — thirty seconds is long
        /// enough for two — and a slot index means something different after
        /// each one. Keyed by slot, a departure would silently transfer somebody
        /// else's ballot.
        /// </remarks>
        public Dictionary<long, int> Ballots { get; } = [];

        /// <summary>The player a kick or mute vote is about, or -1.</summary>
        public long TargetId { get; init; } = -1;

        /// <summary>Their name when the vote was called, for the result line.</summary>
        public string TargetName { get; init; } = string.Empty;
    }

    private Vote? _vote;

    /// <summary>
    /// <c>selectedmap</c>: a map vote can only succeed once per round.
    /// </summary>
    /// <remarks>
    /// Cleared by <see cref="MapChanged"/>, which is why the plugin wants that
    /// hook at all.
    /// </remarks>
    private bool _mapVoteUsed;

    /// <summary>
    /// Every command, its help text, and whether it needs the caller in chat.
    /// </summary>
    /// <remarks>
    /// <c>global.helpMap</c>, with the reference's own wording where it had
    /// some. ⚠️ Kept as one table so <c>/help</c> cannot list a command that
    /// does not exist, or miss one that does.
    /// </remarks>
    private static readonly (string Name, string Help)[] Commands =
    [
        ("help", "Lists the commands. Use /help [command] for one of them."),
        ("party", "Party hard."),
        ("votekick", "Starts a vote to kick the selected player. Use /votekick <Name>"),
        ("votemute", "Starts a vote to mute the selected player. Use /votemute <Name>"),
        ("votescramble", "Starts a vote to scramble the teams. Use /votescramble"),
        ("votenextround", "Starts a vote to end the round. Use /votenextround"),
        ("votemap", "Starts a vote to set the next map. Use /votemap [Mapname/Map prefix]"),
        ("yes", "Votes yes in the vote that is running."),
        ("no", "Votes no in the vote that is running."),
        ("vote", "Picks an option in a numbered vote. Use /vote <number>"),
        ("kick", "Kicks a player. Host only."),
        ("nextround", "Ends the round without a vote. Host only."),
    ];

    /// <summary>
    /// Handles <paramref name="message"/> if it is a command.
    /// </summary>
    /// <returns>
    /// True if it was one — in which case it must not be echoed as chat, even
    /// when the command itself was refused.
    /// </returns>
    private bool TryRunCommand(int slot, string message)
    {
        if (message.Length < 2 || message[0] != '/')
            return false;

        var words = message[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        var name = words[0].ToLowerInvariant();

        // The argument is everything after the command word, kept whole — map
        // names and player names both contain spaces.
        var argument = words.Length > 1 ? message[1..][(words[0].Length + 1)..].Trim() : string.Empty;

        switch (name)
        {
            case "help":
                CommandHelp(slot, argument);
                return true;

            case "party":
                // party.gml: one line, to everybody. The reference colours it
                // "S", which chat_v2_pr's renderer does not know and draws
                // white; orange is what every other announcement uses.
                Deliver($"{ColourOrange}Party hard!", TeamEveryone);
                return true;

            case "votekick":
                CommandVoteAgainstPlayer(slot, argument, VoteKind.Kick, "kick");
                return true;

            case "votemute":
                CommandVoteAgainstPlayer(slot, argument, VoteKind.Mute, "mute");
                return true;

            case "votescramble":
                CommandSimpleVote(slot, VoteKind.Scramble, "Scramble teams?");
                return true;

            case "votenextround":
                CommandSimpleVote(slot, VoteKind.NextRound, "End the round?");
                return true;

            case "votemap":
                CommandVoteMap(slot, argument);
                return true;

            case "yes":
                CastBallot(slot, 0);
                return true;

            case "no":
                CastBallot(slot, 1);
                return true;

            case "vote":
                CommandVote(slot, argument);
                return true;

            case "kick":
            case "nextround":
                // votekick.gml:107-110 and votemap.gml:301-304, verbatim. ⚠️ A
                // headless server has no host player, so this branch is the only
                // reachable one and there is nothing behind it — the operator's
                // equivalent is the control port. Answering exactly what the
                // reference answers is the whole of the client-visible
                // behaviour.
                Whisper(slot, $"{ColourOrange}You can only use this command as host.");
                return true;

            default:
                Whisper(slot, $"{ColourOrange}Unknown command \"{name}\". Try /help.");
                return true;
        }
    }

    // --- /help ---------------------------------------------------------------

    private void CommandHelp(int slot, string argument)
    {
        if (argument.Length != 0)
        {
            var wanted = argument.TrimStart('/').ToLowerInvariant();

            foreach (var (name, help) in Commands)
            {
                if (name == wanted)
                {
                    Whisper(slot, $"{ColourOrange}/{name} - {help}");
                    return;
                }
            }

            Whisper(slot, $"{ColourOrange}No command called \"{wanted}\".");
            return;
        }

        // ⚠️ Split across lines rather than sent as one. The length prefix is a
        // byte, and the client half wraps at 53 characters, so one long line
        // would be truncated on the wire before it ever reached the wrapping.
        var line = string.Empty;
        foreach (var (name, _) in Commands)
        {
            var candidate = line.Length == 0 ? $"/{name}" : $"{line} /{name}";
            if (candidate.Length > 48)
            {
                Whisper(slot, ColourOrange + line);
                line = $"/{name}";
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length != 0)
            Whisper(slot, ColourOrange + line);
    }

    // --- calling a vote ------------------------------------------------------

    /// <summary>
    /// The guard every one of the five plugins opens with:
    /// <c>global.chatWindow.alarm[0] &gt; 0</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ That alarm is the newer chat plugin's <b>vote timer</b>, not the chat
    /// window's fade — the message it prints divides it by 30 and calls the
    /// result "seconds", and <c>sendVote</c> is called with <c>30 * 30</c>. So
    /// the rule is "one vote at a time", not "wait four seconds after anybody
    /// speaks", which is what the same expression means in <c>chat_v2_pr</c>.
    /// Reading it the other way would have produced a cooldown nobody asked for.
    /// </remarks>
    private bool RefuseWhileVoting(int slot)
    {
        if (_vote is null)
            return false;

        var seconds = (int)Math.Ceiling(_vote.TicksLeft / 30.0);
        Whisper(
            slot,
            $"{ColourOrange}You may not call a vote at the moment. "
            + $"Please wait at least {seconds} seconds.");

        return true;
    }

    private void CommandVoteAgainstPlayer(int slot, string argument, VoteKind kind, string verb)
    {
        if (argument.Length == 0)
        {
            Whisper(slot, $"{ColourOrange}Use /vote{verb} <Name>");
            return;
        }

        var target = FindPlayer(slot, argument, "vote" + verb);
        if (target < 0)
            return;

        // votekick.gml:23-29 — the host is not kickable. Slot 0 is this server's
        // own reserved player, which is the same thing.
        if (target == 0)
        {
            Whisper(slot, $"{ColourOrange}You cannot {verb} the host.");
            return;
        }

        if (!Host.IsClient(target))
        {
            // Not in the reference, which had no bots. Kicking one would work
            // and mean nothing; muting one is meaningless outright.
            Whisper(slot, $"{ColourOrange}You cannot {verb} a bot.");
            return;
        }

        if (RefuseWhileVoting(slot))
            return;

        var name = Host.NameOf(target);

        StartVote(new Vote
        {
            Kind = kind,
            Question = $"{char.ToUpperInvariant(verb[0])}{verb[1..]} player {name}?",
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

    private void CommandVote(int slot, string argument)
    {
        if (!int.TryParse(argument, out var option) || option < 1)
        {
            Whisper(slot, $"{ColourOrange}Use /vote <number>");
            return;
        }

        CastBallot(slot, option - 1);
    }

    // --- /votemap ------------------------------------------------------------

    private void CommandVoteMap(int slot, string argument)
    {
        if (_mapVoteUsed)
        {
            // votemap.gml:44-45.
            Whisper(slot, $"{ColourOrange}You can only vote for the next map once.");
            return;
        }

        if (RefuseWhileVoting(slot))
            return;

        var votable = Host.VotableMaps;
        if (votable.Count == 0)
        {
            Whisper(slot, $"{ColourOrange}No maps were found!");
            return;
        }

        // votemap.gml:70 is `string_count(typed, lower(candidate)) > 0`, whose
        // arguments read backwards to a C# eye: GML's string_count counts
        // occurrences of the FIRST argument in the second, so this is
        // "candidate contains what was typed". An empty argument matches
        // everything, which is how bare /votemap offers a random selection.
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
                Whisper(slot, $"{ColourOrange}Selected map not found, using random selection.");

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

    /// <summary>
    /// Fisher-Yates over <see cref="IPluginHost.Random"/>.
    /// </summary>
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

    private void StartVote(Vote vote)
    {
        _vote = vote;

        Deliver($"{ColourOrange}{vote.Question}", TeamEveryone);

        if (vote.Options.Length == 2 && vote.Options[0] == "Yes")
        {
            Deliver($"{ColourOrange}Type /yes or /no. 30 seconds.", TeamEveryone);
        }
        else
        {
            for (var i = 0; i < vote.Options.Length; i++)
                Deliver($"{ColourOrange}  {i + 1}. {vote.Options[i]}", TeamEveryone);

            Deliver($"{ColourOrange}Type /vote <number>. 30 seconds.", TeamEveryone);
        }
    }

    private void CastBallot(int slot, int option)
    {
        if (_vote is not Vote vote)
        {
            Whisper(slot, $"{ColourOrange}There is no vote running.");
            return;
        }

        if (option < 0 || option >= vote.Options.Length)
        {
            Whisper(slot, $"{ColourOrange}Pick 1 to {vote.Options.Length}.");
            return;
        }

        var id = Host.StableIdOf(slot);

        // votekick.gml:40, `target.mayVote = false`: the player a vote is about
        // does not get a say in it.
        if (id == vote.TargetId)
        {
            Whisper(slot, $"{ColourOrange}You cannot vote on this.");
            return;
        }

        // ⚠️ Re-voting replaces rather than adding. The reference collects one
        // answer per client because its dialog can only be answered once; here
        // the same message can arrive twice, and counting both would let one
        // player carry a vote by typing.
        vote.Ballots[id] = option;
        Whisper(slot, $"{ColourOrange}Voted: {vote.Options[option]}.");
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
        Finish(vote);
    }

    /// <summary>
    /// Who a vote needs to hear from: <c>floor(voters / 2)</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference divides <c>ds_list_size(global.players)</c>, and this
    /// divides the players who could actually vote.</b> A deliberate difference,
    /// and not a small one: this server fills with bots by default, and bots and
    /// slot 0 cannot type. Counting them would make the quorum unreachable on
    /// every bot server — the feature would look implemented and never once
    /// pass a vote, which is the worst of both. The reference had no bots, so
    /// its version of the sum is the same number.
    /// </remarks>
    private int Quorum()
    {
        var voters = 0;
        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
            if (Host.IsClient(slot) && _chatters[slot].HasChat)
                voters++;

        return voters / 2;
    }

    private void Finish(Vote vote)
    {
        if (vote.Kind == VoteKind.MapChoice)
        {
            FinishMapChoice(vote);
            return;
        }

        var yes = 0;
        var no = 0;

        foreach (var option in vote.Ballots.Values)
        {
            if (option == 0)
                yes++;
            else
                no++;
        }

        if (yes + no < Quorum())
        {
            Deliver($"{ColourOrange}Voting failed (not enough people voted)", TeamEveryone);
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
            Deliver($"{ColourOrange}Voting failed ({100 - percent}% no)", TeamEveryone);
            return;
        }

        switch (vote.Kind)
        {
            case VoteKind.Kick:
                ApplyKick(vote, percent);
                break;

            case VoteKind.Mute:
                ApplyMute(vote, percent);
                break;

            case VoteKind.Scramble:
                Deliver($"{ColourOrange}Scrambling teams ({percent}% yes)", TeamEveryone);
                Scramble();
                break;

            case VoteKind.NextRound:
                Deliver($"{ColourOrange}Ending round ({percent}% yes)", TeamEveryone);
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

        // ⚠️ The target may have left during the thirty seconds, which is the
        // commonest outcome of a kick vote. Nothing to do, and nothing to
        // apologise for — but do not let a stale slot index reach Kick.
        if (slot < 0)
        {
            Deliver($"{ColourOrange}{vote.TargetName} has already left.", TeamEveryone);
            return;
        }

        Deliver(
            $"{ColourOrange}Player {Host.NameOf(slot)} has been vote kicked ({percent}% yes)",
            TeamEveryone);

        // ⛔ <b>The kick is ONE disconnection, not a ban.</b> The reference's
        // plugin sets `target.kicked` and keeps a `chatKicklist` of addresses
        // that its chat plugin refuses on reconnect; a server-wide ban list is a
        // bigger decision than a chat plugin should be making by itself, and a
        // half-built one that only some code paths consult is worse than none.
        // What does persist is the chat mute, which shares the ban list the spam
        // check already keeps — so a vote-kicked player can come back, and comes
        // back unable to talk.
        //
        // ⚠️ Mute first: it reads the slot, and Kick ends the connection.
        Mute(slot);
        Host.Kick(slot, "vote kicked");
    }

    private void ApplyMute(Vote vote, int percent)
    {
        var slot = Host.SlotOfStableId(vote.TargetId);
        if (slot < 0)
        {
            Deliver($"{ColourOrange}{vote.TargetName} has already left.", TeamEveryone);
            return;
        }

        Deliver(
            $"{ColourOrange}Player {Host.NameOf(slot)} has been vote muted ({percent}% yes)",
            TeamEveryone);

        Mute(slot);
    }

    private void ApplyNextMap(string map)
    {
        if (!Host.SetNextMap(map))
        {
            Deliver($"{ColourOrange}The maprotation has not been changed", TeamEveryone);
            return;
        }

        _mapVoteUsed = true;
        Deliver($"{ColourOrange}Next map is {map}.", TeamEveryone);
    }

    /// <summary>
    /// <c>votemap.gml:141-183</c>: most votes wins, ties broken at random, and a
    /// win for "keep default" — or nobody voting at all — changes nothing.
    /// </summary>
    private void FinishMapChoice(Vote vote)
    {
        var counts = new int[vote.Options.Length];
        foreach (var option in vote.Ballots.Values)
            counts[option]++;

        var winner = 0;
        var highest = counts[0];

        for (var i = 1; i < counts.Length; i++)
        {
            // The reference's own tie-break, `random(1) > 0.5`, which is a fresh
            // coin at each tie rather than a uniform pick among the leaders. Not
            // uniform, and not worth fixing: it is a tie.
            if (counts[i] > highest || (counts[i] == highest && Host.Random.NextDouble() > 0.5))
            {
                winner = i;
                highest = counts[i];
            }
        }

        if (highest == 0 || vote.Options[winner] == KeepDefault)
        {
            Deliver($"{ColourOrange}The maprotation has not been changed", TeamEveryone);
            return;
        }

        ApplyNextMap(vote.Options[winner]);
    }

    /// <summary>
    /// <c>votescramble.gml:76-113</c>: shuffle everyone who is on a team, first
    /// half blue, second half red.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Spectators are left alone</b>, so a scramble does not drag somebody
    /// who is watching into a game.
    /// <para>
    /// ⚠️ Bots are included, because they are on teams and leaving them put
    /// would make the scramble not a scramble. The reference had none.
    /// </para>
    /// </remarks>
    private void Scramble()
    {
        var players = new List<int>();
        for (var slot = 0; slot < Host.PlayerCount; slot++)
            if (Host.TeamOf(slot) != Team.Spectator)
                players.Add(slot);

        // Fisher-Yates on the slots, over the plugin's own random.
        for (var i = players.Count - 1; i > 0; i--)
        {
            var j = Host.Random.Next(i + 1);
            (players[i], players[j]) = (players[j], players[i]);
        }

        // ⚠️ Read the target teams BEFORE moving anybody. SetTeam can kill a
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
    /// <c>global.findPlayer</c>: resolves what somebody typed to one player.
    /// </summary>
    /// <remarks>
    /// The reference's implementation lives in the chat plugin these extend and
    /// is not in the files that were ported, so this is written rather than
    /// transcribed: an exact case-insensitive name first, then a unique
    /// substring. ⚠️ <b>Ambiguity is refused rather than resolved</b> — picking
    /// the first of two matches is how a vote kicks the wrong person, and the
    /// caller can always type more of the name.
    /// </remarks>
    private int FindPlayer(int caller, string text, string command)
    {
        var wanted = text.ToLowerInvariant();

        for (var slot = 0; slot < Host.PlayerCount; slot++)
            if (Host.NameOf(slot).ToLowerInvariant() == wanted)
                return slot;

        var found = -1;
        var ambiguous = false;

        for (var slot = 0; slot < Host.PlayerCount; slot++)
        {
            if (!Host.NameOf(slot).ToLowerInvariant().Contains(wanted, StringComparison.Ordinal))
                continue;

            if (found >= 0)
                ambiguous = true;
            else
                found = slot;
        }

        if (ambiguous)
        {
            Whisper(caller, $"{ColourOrange}More than one player matches \"{text}\". Type more of the name.");
            return -1;
        }

        if (found < 0)
            Whisper(caller, $"{ColourOrange}/{command}: no player called \"{text}\".");

        return found;
    }

    // --- per-round state -----------------------------------------------------

    /// <summary>
    /// A new round clears the once-per-round map vote, and abandons a vote that
    /// was still running.
    /// </summary>
    /// <remarks>
    /// ⚠️ Abandoning rather than finishing it: a scramble or a round-end vote
    /// that resolved into the round <em>after</em> the one it was called in
    /// would act on a world nobody voted about.
    /// </remarks>
    public override void MapChanged(string map)
    {
        _mapVoteUsed = false;

        if (_vote is not null)
        {
            _vote = null;
            Deliver($"{ColourOrange}The vote was cancelled by the map change.", TeamEveryone);
        }
    }
}
