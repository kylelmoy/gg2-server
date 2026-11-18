using System.Globalization;
using GG2.Protocol;

using GG2.Server.Plugins;

namespace GG2.Server;

/// <summary>
/// The votes: <c>host/fn_vote_*.gml</c>, <c>host/vote_clock.gml</c>,
/// <c>host/fn_open_picker.gml</c> and <c>src/votes/*.gml</c>.
/// </summary>
/// <remarks>
/// One vote at a time, 30 seconds, keyed by a <c>voteId</c> so a late answer to a
/// finished vote is dropped rather than counted into the next one. Voters are
/// remembered on their <see cref="Chatter"/> (so they compact with the roster)
/// and everybody else a vote refers to by stable id, never by slot: a vote runs
/// long enough for the roster to compact under it.
/// </remarks>
public sealed partial class ServerChat
{
    private enum VoteHandler
    {
        Pick,
        Kick,
        Mute,
        Scramble,
        NextRound,
        Map,
    }

    private const long VoteMilliseconds = 30000;
    private const long VoteCooldownMilliseconds = 60000;
    private const int PickerPageSize = 8;
    private const char PickerPageMarker = '';

    private bool _voteActive;
    private int _voteId;
    private string _voteQuestion = string.Empty;
    private readonly List<string> _voteOptions = [];
    private readonly List<int> _voteCounts = [];
    private VoteHandler _voteHandler;
    private long? _voteOnly;
    private long? _voteExcluded;
    private long _voteEndsAt;
    private int _voteEligible;
    private int _voteAnswered;
    private long? _voteCaller;
    private long? _voteTarget;
    private string _voteArg = string.Empty;
    private string _voteSummary = string.Empty;
    private string _followUp = string.Empty;
    private long? _followUpFrom;
    private bool _mapVoteUsed;

    private readonly List<string> _pickValues = [];
    private readonly List<string> _pickLabels = [];
    private string _pickCommand = string.Empty;

    /// <summary>Whether a vote is running, for tests.</summary>
    public bool VoteActive => _voteActive;

    /// <summary>
    /// <c>host/fn_vote_start.gml</c>. False if a vote is already running.
    /// <paramref name="onlySlot"/> makes a private vote only that player sees (the
    /// picker); <paramref name="excludedSlot"/> may not answer (a kick target).
    /// </summary>
    private bool StartVote(string question, IReadOnlyList<string> options, VoteHandler handler, int onlySlot = -1, int excludedSlot = -1)
    {
        if (_voteActive)
            return false;

        _voteId = (_voteId + 1) % 256;
        _voteActive = true;
        _voteQuestion = Cap(StripControl(question, " "), MaxStringLength);
        _voteHandler = handler;
        _voteOnly = onlySlot >= 0 ? Host.StableIdOf(onlySlot) : null;
        _voteExcluded = excludedSlot >= 0 ? Host.StableIdOf(excludedSlot) : null;
        _voteEndsAt = Now + VoteMilliseconds;

        _voteOptions.Clear();
        for (var i = 0; i < Math.Min(9, options.Count); i++)
            _voteOptions.Add(Cap(StripControl(options[i], " "), MaxStringLength));

        for (var slot = 0; slot < _chatters.Count; slot++)
        {
            _chatters[slot].VoteId = -1;
            _chatters[slot].VoteChoice = 0;
            _chatters[slot].V6Dialog = false;
        }

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
            if (_chatters[slot].Hello && (onlySlot < 0 || slot == onlySlot))
                OfferVote(slot);

        return true;
    }

    /// <summary>
    /// <c>host/fn_vote_offer.gml</c>: the running vote and its tally to one greeted
    /// player, making it a voter unless it is the excluded player.
    /// </summary>
    /// <remarks>
    /// A player offered the same vote again (a hello in the other format) keeps
    /// the answer it already gave, so switching format is not a second vote.
    /// <para>
    /// v6 gets a 204, which is a 200 with the counts so far (<c>plugin.gml:455-482</c>),
    /// so a fresh vote and a mid-vote resync are one packet. v6 has no "shown but
    /// may not answer": its kick target gets no dialog at all (<c>mayVote</c>),
    /// only the result line.
    /// </para>
    /// </remarks>
    private void OfferVote(int slot)
    {
        if (!_voteActive)
            return;

        var chatter = _chatters[slot];
        var excluded = _voteExcluded == Host.StableIdOf(slot);
        if (!excluded && chatter.VoteId != _voteId)
        {
            chatter.VoteId = _voteId;
            chatter.VoteChoice = 0;
        }

        var ticks = (long)Math.Ceiling((_voteEndsAt - Now) * 30 / 1000.0);
        var ticksLeft = (ushort)Math.Clamp(ticks, 0, ushort.MaxValue);

        if (chatter.Format == Format.Native)
        {
            Begin(KindVoteStart);
            _out.WriteUByte((byte)_voteId);
            _out.WriteString1(_voteQuestion);
            _out.WriteUByte((byte)_voteOptions.Count);
            foreach (var option in _voteOptions)
                _out.WriteString1(option);

            _out.WriteUShort(ticksLeft);
            _out.WriteUByte(chatter.VoteId == _voteId ? (byte)1 : (byte)0);
            Channel.SendTo(slot, _out.WrittenSpan);

            SendTally(slot);
            return;
        }

        if (chatter.Format != Format.V6 || excluded)
            return;

        CountVotes();
        _v6.Reset();
        _v6.WriteUByte(V6PollResync);
        _v6.WriteString1(_voteQuestion);
        _v6.WriteUByte((byte)_voteOptions.Count);
        foreach (var option in _voteOptions)
            _v6.WriteString1(option);

        _v6.WriteUShort(ticksLeft);
        for (var i = 0; i < _voteCounts.Count; i++)
        {
            var count = Math.Min(255, _voteCounts[i]);
            _v6.WriteUByte((byte)count);
            chatter.V6Counted[i] = count;
        }

        Channel.SendTo(slot, _v6.WrittenSpan);
        chatter.V6Dialog = true;
    }

    /// <summary>
    /// <c>host/fn_vote_count.gml</c>: recount from the players still here. A player
    /// who left takes their vote with them.
    /// </summary>
    private void CountVotes()
    {
        _voteCounts.Clear();
        for (var i = 0; i < _voteOptions.Count; i++)
            _voteCounts.Add(0);

        _voteEligible = 0;
        _voteAnswered = 0;

        for (var slot = 0; slot < _chatters.Count; slot++)
        {
            var chatter = _chatters[slot];
            if (chatter.VoteId != _voteId)
                continue;

            _voteEligible++;
            if (chatter.VoteChoice >= 1 && chatter.VoteChoice <= _voteOptions.Count)
            {
                _voteAnswered++;
                _voteCounts[chatter.VoteChoice - 1]++;
            }
        }
    }

    /// <summary>
    /// <c>host/fn_vote_tally.gml</c>: VOTE_TALLY to one player, or to everyone the
    /// vote is shown to when <paramref name="slot"/> is -1.
    /// </summary>
    private void SendTally(int slot = -1)
    {
        if (!_voteActive)
            return;

        CountVotes();

        Begin(KindVoteTally);
        _out.WriteUByte((byte)_voteId);
        _out.WriteUByte((byte)_voteCounts.Count);
        foreach (var count in _voteCounts)
            _out.WriteUByte((byte)Math.Min(255, count));

        if (slot >= 0)
        {
            SendTallyTo(slot);
            return;
        }

        for (var other = 0; other < Host.PlayerCount && _chatters.Has(other); other++)
            if (_chatters[other].Hello && ShownTo(other))
                SendTallyTo(other);
    }

    /// <summary>
    /// The tally in <see cref="_out"/> to one player, or to a v6 dialog as one 203
    /// per vote it has not counted yet. v6 can only add, so a count that falls
    /// (a voter who left) stays where the dialog has it.
    /// </summary>
    private void SendTallyTo(int slot)
    {
        var chatter = _chatters[slot];
        if (chatter.Format == Format.Native)
        {
            Channel.SendTo(slot, _out.WrittenSpan);
            return;
        }

        if (chatter.Format != Format.V6 || !chatter.V6Dialog)
            return;

        for (var i = 0; i < _voteCounts.Count; i++)
        {
            while (chatter.V6Counted[i] < Math.Min(255, _voteCounts[i]))
            {
                _v6.Reset();
                _v6.WriteUByte(V6VoteTally);
                _v6.WriteUByte((byte)i);
                Channel.SendTo(slot, _v6.WrittenSpan);
                chatter.V6Counted[i]++;
            }
        }
    }

    private bool ShownTo(int slot) => _voteOnly is null || _voteOnly == Host.StableIdOf(slot);

    /// <summary>The VOTE case of <c>host/receive.gml</c>.</summary>
    private void ReceiveVote(int slot, int voteId, int option)
    {
        if (!_voteActive || voteId != _voteId)
            return;

        var chatter = _chatters[slot];
        if (chatter.VoteId != _voteId || chatter.VoteChoice != 0)
            return;

        if (option < 1 || option > _voteOptions.Count)
            return;

        chatter.VoteChoice = option;
        SendTally();
    }

    /// <summary>
    /// <c>host/vote_clock.gml</c>: a vote ends when time is up or every voter still
    /// here has answered. The end of a round cancels it, so nothing decided on one
    /// map acts on the next.
    /// </summary>
    private void TickVote()
    {
        if (!_voteActive)
            return;

        if (Host.RoundOver)
        {
            EndVote("Vote cancelled: the round is over.");
            return;
        }

        CountVotes();
        if (Now >= _voteEndsAt || (_voteEligible > 0 && _voteAnswered >= _voteEligible))
            EndVote(string.Empty);
    }

    /// <summary>
    /// <c>host/fn_vote_end.gml</c>. With an empty <paramref name="cancelReason"/>
    /// the handler decides the result; otherwise the vote is cancelled with that
    /// text. Then any follow-up command the picker queued runs.
    /// </summary>
    private void EndVote(string cancelReason)
    {
        if (!_voteActive)
            return;

        CountVotes();
        _followUp = string.Empty;
        _followUpFrom = null;

        var winner = 0;
        string text;
        if (cancelReason.Length != 0)
        {
            text = cancelReason;
        }
        else
        {
            // The plurality option; 0 when nobody voted or the top is tied.
            var best = 0;
            for (var i = 0; i < _voteCounts.Count; i++)
            {
                if (_voteCounts[i] > best)
                {
                    best = _voteCounts[i];
                    winner = i + 1;
                }
                else if (_voteCounts[i] == best && best > 0)
                {
                    winner = 0;
                }
            }

            text = RunHandler();
        }

        text = Cap(StripControl(text, " "), MaxStringLength);

        Begin(KindVoteEnd);
        _out.WriteUByte((byte)_voteId);
        _out.WriteUByte((byte)winner);
        _out.WriteString1(text);

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
        {
            var chatter = _chatters[slot];
            if (chatter.Hello && ShownTo(slot))
            {
                if (chatter.Format == Format.Native)
                    Channel.SendTo(slot, _out.WrittenSpan);
                else
                    EndV6Vote(slot, winner, text);
            }

            chatter.VoteId = -1;
            chatter.VoteChoice = 0;
            chatter.V6Dialog = false;
        }

        if (text.Length != 0)
            Host.Log($"[chat] vote: {LogText.Safe(text)}");

        _voteActive = false;
        _voteOnly = null;
        _voteExcluded = null;

        var followUp = _followUp;
        var from = _followUpFrom is { } id ? Host.SlotOfStableId(id) : -1;
        _followUp = string.Empty;
        _followUpFrom = null;
        if (followUp.Length != 0 && from >= 0)
            RunCommand(from, followUp);
    }

    /// <summary>
    /// VOTE_END for v6: 201 with the winner, which shows the result in the dialog
    /// (<c>displayVoteResults</c>), or 0 to close it when there is no result to
    /// show; then the result text as a 255, since 201 carries none.
    /// </summary>
    private void EndV6Vote(int slot, int winner, string text)
    {
        if (_chatters[slot].V6Dialog)
        {
            _v6.Reset();
            _v6.WriteUByte(V6VoteResult);
            _v6.WriteUByte((byte)(text.Length == 0 ? 0 : winner));
            Channel.SendTo(slot, _v6.WrittenSpan);
        }

        if (text.Length != 0)
            SendV6(slot, V6Notice, 0, V6Orange + Sanitise(text));
    }

    private string RunHandler() => _voteHandler switch
    {
        VoteHandler.Pick => PickHandler(),
        VoteHandler.Kick => KickHandler(),
        VoteHandler.Mute => MuteHandler(),
        VoteHandler.Scramble => ScrambleHandler(),
        VoteHandler.NextRound => NextRoundHandler(),
        VoteHandler.Map => MapHandler(),
        _ => string.Empty,
    };

    /// <summary>
    /// <c>host/fn_yes_no.gml</c>: a quorum of <c>ceil(voters / 2)</c>, at least 2,
    /// must answer, and the Yes share must reach <paramref name="percent"/> (or
    /// exceed it, when <paramref name="strict"/>).
    /// </summary>
    private bool YesNoPassed(int percent, bool strict)
    {
        var yes = _voteCounts[0];
        var no = _voteCounts[1];
        var total = yes + no;
        _voteSummary = "(" + yes + " yes, " + no + " no)";

        var quorum = Math.Max(2, (_voteEligible + 1) / 2);
        if (total < quorum)
        {
            _voteSummary = "(" + total + " of the " + quorum + " votes needed)";
            return false;
        }

        return strict ? yes * 100 > percent * total : yes * 100 >= percent * total;
    }

    private int TargetSlot() => _voteTarget is { } id ? Host.SlotOfStableId(id) : -1;

    // --- src/votes/*.gml ----------------------------------------------------

    private string PickHandler()
    {
        var choice = -1;
        for (var i = 0; i < _voteCounts.Count; i++)
            if (_voteCounts[i] > 0)
                choice = i;

        if (choice >= 0 && choice < _pickValues.Count)
        {
            _followUpFrom = _voteCaller;
            _followUp = "/" + _pickCommand + " " + _pickValues[choice];
        }

        return string.Empty;
    }

    private string KickHandler()
    {
        var target = TargetSlot();
        if (target < 0)
            return "The player left before the vote ended.";

        var name = Host.NameOf(target);
        if (!YesNoPassed(_settings.VoteKickPercent, strict: false))
            return name + " stays " + _voteSummary + ".";

        SetExpiry(_kickBans, Host.RemoteAddressOf(target), Now + _settings.KickBanMinutes * 60000L);
        Host.Kick(target, "You were vote-kicked.");
        return name + " was kicked " + _voteSummary + ".";
    }

    private string MuteHandler()
    {
        var target = TargetSlot();
        if (target < 0)
            return "The player left before the vote ended.";

        var name = Host.NameOf(target);
        if (!YesNoPassed(_settings.VoteMutePercent, strict: false))
            return name + " is not muted " + _voteSummary + ".";

        var expiry = Now + _settings.MuteMinutes * 60000L;
        _chatters[target].MutedUntil = expiry;
        SetExpiry(_mutes, Host.RemoteAddressOf(target), expiry);
        return name + " was muted for " + _settings.MuteMinutes + " minutes " + _voteSummary + ".";
    }

    private string ScrambleHandler()
    {
        if (!YesNoPassed(50, strict: true))
            return "The teams stay as they are " + _voteSummary + ".";

        Scramble();
        return "Teams scrambled " + _voteSummary + ".";
    }

    private string NextRoundHandler()
    {
        if (!YesNoPassed(50, strict: true))
            return "The round goes on " + _voteSummary + ".";

        Host.EndRound();
        return "Ending the round " + _voteSummary + ".";
    }

    private string MapHandler()
    {
        if (!YesNoPassed(50, strict: false))
            return "The rotation goes on " + _voteSummary + ".";

        if (!Host.SetNextMap(_voteArg))
            return _voteArg + " is no longer in the rotation.";

        _mapVoteUsed = true;
        return "The next map will be " + _voteArg + " " + _voteSummary + ".";
    }

    /// <summary>
    /// <c>host/fn_scramble.gml</c>: everyone on RED or BLU, shuffled into two teams
    /// of as-equal size. <see cref="IPluginHost.SetTeam"/> is the server's own team
    /// switch, which is what the GML reproduces by hand.
    /// </summary>
    private void Scramble()
    {
        var playing = new List<long>();
        for (var slot = 0; slot < Host.PlayerCount; slot++)
            if (Host.TeamOf(slot) is Team.Red or Team.Blue)
                playing.Add(Host.StableIdOf(slot));

        Host.Random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(playing));
        var first = Host.Random.Next(2) == 0 ? Team.Red : Team.Blue;

        for (var i = 0; i < playing.Count; i++)
        {
            var slot = Host.SlotOfStableId(playing[i]);
            if (slot < 0)
                continue;

            var team = i % 2 == 0 ? first : (first == Team.Red ? Team.Blue : Team.Red);
            if (Host.TeamOf(slot) != team)
                Host.SetTeam(slot, team);
        }
    }

    // --- starting votes -----------------------------------------------------

    /// <summary><c>host/fn_vote_check.gml</c>: may <paramref name="sender"/> start a vote now?</summary>
    private bool VoteAllowed(int sender)
    {
        if (!_settings.VotesEnabled)
        {
            Reply(sender, "Voting is disabled on this server.");
            return false;
        }

        if (_voteActive)
        {
            Reply(sender, "A vote is already running.");
            return false;
        }

        if (Host.RoundOver)
        {
            Reply(sender, "The round is over; vote on the next one.");
            return false;
        }

        if (!IsAdmin(sender))
        {
            var wait = CeilSeconds(_chatters[sender].LastVoteCall + VoteCooldownMilliseconds - Now);
            if (wait > 0)
            {
                Reply(sender, "Please wait " + wait + " seconds before starting another vote.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>host/fn_start_yes_no.gml</c>: a public Yes/No vote, with the caller counted
    /// as Yes and their cooldown started.
    /// </summary>
    private void StartYesNo(int sender, string question, VoteHandler handler, int excludedSlot, int targetSlot, string arg)
    {
        if (!StartVote(question, ["Yes", "No"], handler, excludedSlot: excludedSlot))
        {
            Reply(sender, "A vote is already running.");
            return;
        }

        _voteCaller = Host.StableIdOf(sender);
        _voteTarget = targetSlot >= 0 ? Host.StableIdOf(targetSlot) : null;
        _voteArg = arg;

        var caller = _chatters[sender];
        caller.LastVoteCall = Now;
        if (caller.VoteId == _voteId)
        {
            caller.VoteChoice = 1;
            SendTally();
        }

        SendNotice(Notice.Vote, Host.NameOf(sender) + " started a vote: " + question);
    }

    /// <summary><c>host/fn_target_vote.gml</c>: /votekick and /votemute.</summary>
    private void TargetVote(int sender, List<string> args, VoteHandler handler)
    {
        if (!VoteAllowed(sender))
            return;

        var command = handler == VoteHandler.Kick ? "votekick" : "votemute";
        if (args.Count < 1)
        {
            OpenPicker(sender, command, players: true, page: 0);
            return;
        }

        if (args[0].StartsWith(PickerPageMarker))
        {
            OpenPicker(sender, command, players: true, page: DigitsOr(args[0], 0));
            return;
        }

        var target = FindPlayer(args[0], sender);
        if (target < 0)
            return;

        if (target == 0)
        {
            Reply(sender, "The host cannot be voted out.");
            return;
        }

        if (target == sender)
        {
            Reply(sender, "You cannot start a vote against yourself.");
            return;
        }

        var verb = handler == VoteHandler.Kick ? "Kick " : "Mute ";
        StartYesNo(sender, verb + Host.NameOf(target) + "?", handler, target, target, string.Empty);
    }

    private void VoteScrambleCommand(int sender, List<string> args, string rest)
    {
        if (VoteAllowed(sender))
            StartYesNo(sender, "Scramble the teams?", VoteHandler.Scramble, -1, -1, string.Empty);
    }

    private void VoteNextRoundCommand(int sender, List<string> args, string rest)
    {
        if (VoteAllowed(sender))
            StartYesNo(sender, "End this round now?", VoteHandler.NextRound, -1, -1, string.Empty);
    }

    /// <summary><c>commands/votemap.gml</c>, over <see cref="IPluginHost.VotableMaps"/>.</summary>
    private void VoteMapCommand(int sender, List<string> args, string rest)
    {
        if (!VoteAllowed(sender))
            return;

        if (_mapVoteUsed)
        {
            Reply(sender, "The next map has already been voted on.");
            return;
        }

        if (args.Count < 1)
        {
            OpenPicker(sender, "votemap", players: false, page: 0);
            return;
        }

        if (args[0].StartsWith(PickerPageMarker))
        {
            OpenPicker(sender, "votemap", players: false, page: DigitsOr(args[0], 0));
            return;
        }

        string? found = null;
        foreach (var map in Host.VotableMaps)
            if (map.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                found = map;

        if (found is null)
        {
            Reply(sender, args[0] + " is not in the rotation. /maplist shows it.");
            return;
        }

        StartYesNo(sender, "Change the next map to " + found + "?", VoteHandler.Map, -1, -1, found);
    }

    /// <summary>
    /// <c>host/fn_open_picker.gml</c>: a private vote listing eight players (never
    /// the caller or slot 0) or maps (never the current one) per page, plus "Next
    /// page" when there are more. Answering re-runs <paramref name="command"/>
    /// with the choice.
    /// </summary>
    private void OpenPicker(int sender, string command, bool players, int page)
    {
        var values = new List<string>();
        var labels = new List<string>();

        if (players)
        {
            for (var slot = 0; slot < Host.PlayerCount; slot++)
            {
                if (slot == 0 || slot == sender)
                    continue;

                values.Add("\r" + Host.StableIdOf(slot).ToString(CultureInfo.InvariantCulture));
                labels.Add(Host.NameOf(slot));
            }
        }
        else
        {
            foreach (var map in Host.VotableMaps)
            {
                if (map.Equals(_currentMap, StringComparison.OrdinalIgnoreCase))
                    continue;

                values.Add(map);
                labels.Add(map);
            }
        }

        if (values.Count == 0)
        {
            Reply(sender, players ? "There is nobody to vote on." : "There are no other maps in the rotation.");
            return;
        }

        page = Math.Max(0, page);
        var first = page * PickerPageSize;
        if (first >= values.Count)
        {
            page = 0;
            first = 0;
        }

        var count = Math.Min(PickerPageSize, values.Count - first);
        _pickValues.Clear();
        _pickLabels.Clear();
        for (var i = first; i < first + count; i++)
        {
            _pickValues.Add(values[i]);
            _pickLabels.Add(labels[i]);
        }

        if (first + count < values.Count)
        {
            _pickValues.Add(PickerPageMarker + (page + 1).ToString(CultureInfo.InvariantCulture));
            _pickLabels.Add("Next page");
        }

        _pickCommand = command;
        if (!StartVote(players ? "Who?" : "Which map?", _pickLabels, VoteHandler.Pick, onlySlot: sender))
        {
            Reply(sender, "A vote is already running.");
            return;
        }

        _voteCaller = Host.StableIdOf(sender);
    }
}
