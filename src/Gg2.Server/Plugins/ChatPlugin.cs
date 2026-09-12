using Gg2.Protocol;

namespace Gg2.Server.Plugins;

/// <summary>
/// The server half of <c>chat</c> — "Lorgan3's chat plugin v6" — text chat, a
/// slash-command console and the in-game vote dialog.
/// </summary>
/// <remarks>
/// Transcribed from <c>plugin.gml</c>, <c>chat_api.gml</c>, <c>vote_api.gml</c>
/// and <c>commands.gml</c> inside
/// <c>chat@bad8081d64f0b808a2bb3a6d88978fb5.zip</c>. Only the
/// <c>global.isHost</c> branches are here: the client half — the window, the
/// console font, the key bindings, the colour codes, the wrapping, the options
/// menu, the <c>cheatycheat</c> typing lock and the vote dialog's drawing — is
/// still the plugin's own GML, downloaded and executed by the client exactly as
/// before.
/// <para>
/// <b>This is a different protocol from <see cref="ChatV2Plugin"/>, not a newer
/// one.</b> The two are separate plugins with separate hashes and neither's
/// packets mean anything to the other's client half. The differences that matter
/// to a reader: this one sends the speaker's <em>roster index</em> and the raw
/// text and lets the client colour it, has a real vote dialog rather than lines
/// of text, has no anti-spam mute and no name de-duplication, and does not strip
/// <c>#</c> (the client's <c>sanitiseNewlines</c> escapes it at draw time).
/// </para>
/// <para>
/// <b>The wire format, both directions.</b> There is no version field anywhere
/// in it, which is why <see cref="ServerPluginCatalogue"/> pins the zip.
/// </para>
/// <para>
/// Client to server (<c>plugin.gml:436-522</c>, the host's <c>event_user 0</c>):
/// a kind byte, then
/// <list type="table">
/// <item><term>0</term><description>hello. No body.</description></item>
/// <item><term>1</term><description>global chat: <c>ubyte</c> length, that many bytes.</description></item>
/// <item><term>2</term><description>team chat: the same.</description></item>
/// <item><term>3</term><description>a vote answer: <c>ubyte</c>, the 1-based option.</description></item>
/// </list>
/// </para>
/// <para>
/// Server to client (<c>plugin.gml:525-592</c>, the client's
/// <c>event_user 1</c>): a <c>ubyte</c> that is either a roster index or a code.
/// <list type="table">
/// <item><term>&lt; 200</term><description>
/// the speaker's roster index, then a <c>ubyte</c> audience and a
/// <c>ubyte</c>-prefixed string. <b>The text is raw</b> — the client's
/// <c>doColorCode</c> prefixes the name and picks the colour from the speaker's
/// team, which is why nothing here formats a player's line.
/// </description></item>
/// <item><term>200</term><description>
/// a poll: question, option count, each option, then a <c>ushort</c> of steps
/// remaining (<c>vote_api.gml:240-250</c>).
/// </description></item>
/// <item><term>201</term><description>the result: a <c>ubyte</c> 1-based winner, or 0 to just close the dialog.</description></item>
/// <item><term>203</term><description>somebody voted: a <c>ubyte</c> 0-based option, which the dialog's bar chart adds to.</description></item>
/// <item><term>204</term><description>200's fields plus one <c>ubyte</c> tally per option, for a client that joined mid-vote.</description></item>
/// <item><term>254</term><description>a <c>ubyte</c>-prefixed pre-coloured line. <b>Suppressible</b>: a client with "Display player info" off drops these, so nothing a player needs may go out as 254.</description></item>
/// <item><term>255</term><description>the same, and never suppressed. Command replies and vote results.</description></item>
/// </list>
/// </para>
/// <para>
/// Strings here are GM8 byte strings, not UTF-8 — see <see cref="Gg2Text"/>. A
/// char is a byte, which is what makes the 255-byte chunking below a plain
/// <c>string</c> slice.
/// </para>
/// </remarks>
public sealed partial class ChatPlugin : ServerPlugin
{
    /// <summary>Per-player state; the reference keeps these on the <c>Player</c> instance.</summary>
    /// <remarks>
    /// <c>plugin.gml:117-129</c> declares four more — <c>lastChatTime</c>,
    /// <c>chatCount</c>, <c>mute</c> and <c>gid</c>. The first two are dead in
    /// this version (v6 has no anti-spam; <see cref="ChatV2Plugin"/> does),
    /// <c>mute</c> is the client's own render-time filter, and <c>gid</c> is
    /// <see cref="IPluginHost.StableIdOf"/>.
    /// </remarks>
    private sealed class Chatter
    {
        /// <summary>Whether this player has announced the plugin and is not muted.</summary>
        public bool HasChat;

        /// <summary>
        /// The name last announced. <c>""</c> until the first tick, which is what
        /// stops a join being announced as a rename.
        /// </summary>
        public string NameBackup = string.Empty;

        /// <summary>
        /// <c>current_time</c> of the last vote this player called. Zero means
        /// never — see <see cref="VoteCallCooldownMilliseconds"/> for why that is
        /// not the same as "may call one now".
        /// </summary>
        public long LastVoteCall;

        /// <summary>A vote is open and this player has not answered it yet.</summary>
        public bool CanVote;

        /// <summary>
        /// <c>mayVote</c>: false takes a player out of the current vote
        /// entirely — no dialog, no answer accepted, and not counted in the
        /// quorum. Reset for everybody when the vote ends
        /// (<c>plugin.gml:239-242</c>).
        /// </summary>
        public bool MayVote = true;
    }

    /// <summary>Client-to-server packet kinds. The first byte of the body.</summary>
    private const byte KindHello = 0;
    private const byte KindGlobalChat = 1;
    private const byte KindTeamChat = 2;
    private const byte KindVote = 3;

    /// <summary>
    /// Server-to-client first bytes that are codes rather than roster indices.
    /// </summary>
    /// <remarks>
    /// The boundary is 200: <c>plugin.gml:530</c> reads an audience byte only
    /// below it. 202 and 205-253 are unused and reach the client as ordinary
    /// pre-coloured lines.
    /// </remarks>
    private const byte SenderPoll = 200;
    private const byte SenderVoteResult = 201;
    private const byte SenderVoteTally = 203;
    private const byte SenderPollResync = 204;

    /// <summary>
    /// A server announcement about players — joins, leaves, renames.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A client can switch these off</b> ("Display player info",
    /// <c>chat_api.gml:50-52</c>), so anything a player must see — a command
    /// reply, a vote result — goes out as <see cref="SenderNotice"/> instead.
    /// </remarks>
    private const byte SenderPlayerInfo = 254;

    /// <summary>A server line no client setting suppresses.</summary>
    private const byte SenderNotice = 255;

    /// <summary>
    /// The audience a message is for: 0 is everyone, otherwise <c>team + 1</c>.
    /// </summary>
    /// <remarks>
    /// Off by one on purpose. The client sends a packet <em>kind</em> of 1 for
    /// global and 2 for team (<c>chat_api.gml:33</c> writes a boolean plus one);
    /// the host re-derives 0-for-global / <c>1 + team</c> from that kind
    /// (<c>plugin.gml:495</c>) and routes on <c>team == audience - 1</c>
    /// (<c>chat_api.gml:22</c>). The two numbers are not the same quantity even
    /// though the GML calls both <c>_team</c>.
    /// </remarks>
    private const int AudienceEveryone = 0;

    /// <summary>Colour codes the client half understands (<c>plugin.gml:595-618</c>).</summary>
    private const string ColourOrange = "O";

    /// <summary>The longest chunk the length prefix can carry.</summary>
    /// <remarks>
    /// Not the longest message: <c>chat_api.gml:7-28</c> loops, sending a
    /// message longer than this as several packets that the client's window
    /// shows as several lines.
    /// </remarks>
    private const int MaxChunkLength = 255;

    /// <summary>
    /// <c>voteTimeout</c>: how long after calling a vote a player may call
    /// another (<c>plugin.gml:224</c>).
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It also gates the first minute of the server's life</b>, because
    /// <c>lastVoteCall</c> starts at 0 and the test is
    /// <c>current_time - voteTimeout &gt; lastVoteCall</c> against a clock that
    /// starts at 0 too (<c>chat_api.gml:175</c>). Nobody can call a vote until
    /// the process has been up for a minute. Transcribed: it is what a player on
    /// a reference server sees, and the refusal message says how long is left.
    /// </remarks>
    private const long VoteCallCooldownMilliseconds = 60000;

    private readonly PluginPlayerTable<Chatter> _chatters = new(() => new Chatter());

    /// <summary>
    /// <c>chatBanlist</c>: the addresses of players voted out of chat.
    /// </summary>
    /// <remarks>
    /// Keyed on the remote address, not the roster slot, so a mute survives a
    /// reconnect — which is the point — and catches everyone behind the same NAT,
    /// which is not. Transcribed rather than improved: a client sees a mute the
    /// reference would have given it.
    /// </remarks>
    private readonly List<string> _banlist = [];

    /// <summary>
    /// A scratch buffer for the one packet being built. Never re-entered.
    /// </summary>
    /// <remarks>
    /// Sized for the largest packet the plugin can produce: a 204 resync with a
    /// 255-byte question, nine 255-byte options, the clock and nine tallies.
    /// </remarks>
    private readonly NetWriter _out = new(4096);

    public override string Name => "chat";

    public override void PlayerAdded(int slot) => _chatters.Add(slot);

    /// <summary>
    /// <c>Player</c>'s Destroy event (<c>plugin.gml:186-195</c>): tell the chat
    /// somebody left.
    /// </summary>
    /// <remarks>
    /// Runs <b>before</b> the roster removal, so <see cref="IPluginHost"/> still
    /// answers for the departing player and the announcement carries their name.
    /// They are still in the roster and still in chat, so the line goes to them
    /// as well — the reference's <c>with(Player)</c> includes the instance whose
    /// Destroy event is running, and their socket is closing anyway.
    /// </remarks>
    public override void PlayerRemoving(int slot)
    {
        if (_chatters.Has(slot) && _chatters[slot].HasChat)
            Announce($"{ColourOrange}{Sanitise(Host.NameOf(slot))} has left the game.");

        _chatters.RemoveAt(slot);
    }

    /// <summary>
    /// <c>Player</c>'s Begin Step, host branch (<c>plugin.gml:148-182</c>):
    /// notice a rename, replace an empty name, and tell the chat about it. Then
    /// the vote clock.
    /// </summary>
    /// <remarks>
    /// <b>The empty-name branch is the one thing here a client without the
    /// plugin still sees</b>, because it goes out as an ordinary
    /// <c>PLAYER_CHANGENAME</c>. <b>There is no name de-duplication in this
    /// plugin</b> — that is <see cref="ChatV2Plugin"/>'s, and looking for it here
    /// is how a reader convinces themselves this port dropped something.
    /// <para>
    /// A rename by a player who never sent hello leaves <c>nameBkp</c> stale for
    /// ever, so this branch is re-evaluated for them every tick from then on.
    /// Harmless, and transcribed rather than fixed, because the fix would change
    /// what a stock client sees.
    /// </para>
    /// <para>
    /// Placed after the tick's client commands rather than before, so a
    /// <c>PLAYER_CHANGENAME</c> that arrived this tick is announced in the same
    /// tick. GM8 gave the reference no such choice and nothing observable turns
    /// on it.
    /// </para>
    /// </remarks>
    public override void Tick()
    {
        TickVote();

        // Ascending, and re-reading PlayerCount each time: Rename below changes
        // a name but never the roster's shape, so the bound is stable.
        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
        {
            var chatter = _chatters[slot];
            var name = Host.NameOf(slot);

            if (name == chatter.NameBackup && name.Length != 0)
                continue;

            if (name.Length == 0)
            {
                name = "Nameless";
                Host.Rename(slot, name);
            }

            // First sight of this player: adopt the name silently. The reference
            // exits here, which is why a join never announces a rename.
            if (chatter.NameBackup.Length == 0)
            {
                chatter.NameBackup = name;
                continue;
            }

            if (!chatter.HasChat)
                continue;

            Announce(
                $"{ColourOrange}{Sanitise(chatter.NameBackup)} has changed their name to "
                + $"{Sanitise(name)}.");

            chatter.NameBackup = name;
        }
    }

    /// <summary>
    /// <c>event_user 0</c> (<c>plugin.gml:436-522</c>): a packet from a client,
    /// dispatched on its kind byte.
    /// </summary>
    /// <remarks>
    /// The reference reads past the end of a short buffer and GM8 hands it
    /// zeroes; this clamps instead. Both refuse the packet in the end, but a
    /// truncated read here would be an exception on the tick thread rather than
    /// a message nobody sees.
    /// </remarks>
    public override void Receive(int slot, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        var chatter = _chatters[slot];

        switch (data[0])
        {
            case KindHello:
                // The mute check is here and nowhere else: a muted player who
                // reconnects re-sends hello and is refused, silently, which is
                // what makes the address-keyed ban list work at all.
                if (_banlist.Contains(Host.RemoteAddressOf(slot)) || chatter.HasChat)
                    break;

                chatter.HasChat = true;
                chatter.NameBackup = Host.NameOf(slot);

                Announce($"{ColourOrange}{Sanitise(chatter.NameBackup)} has joined chat!");

                // plugin.gml:455-482: a client that arrives mid-vote is given
                // the question, the clock and the tallies so far.
                SendPollResync(slot);
                break;

            case KindGlobalChat:
                if (chatter.HasChat)
                    ExecuteChat(slot, ReadMessage(data), AudienceEveryone);

                break;

            case KindTeamChat:
            {
                var team = Host.TeamOf(slot);
                if (team == Team.Spectator || !chatter.HasChat)
                    break;

                // ⚠️ Team chat does NOT go through executeChat
                // (plugin.gml:493-505), so a slash command typed into team chat
                // is relayed as text rather than run. Transcribed: a player who
                // sees "/votekick bob" appear as a team message has learnt
                // something true about the server they are on.
                SendChatMessage(ReadMessage(data), slot, 1 + (int)team);
                break;
            }

            case KindVote:
            {
                if (data.Length < 2 || !chatter.CanVote || !chatter.MayVote)
                    break;

                chatter.CanVote = false;

                // The reference's guard is `vote <= 9`, which lets a 0 through
                // and indexes optionVotes[-1]. Bounded here instead: a client
                // is not a trusted source and an out-of-range answer must not
                // be an exception on the tick thread. See docs/HARDENING.md.
                HandleVote(slot, data[1]);
                break;
            }

            // Any other kind byte is ignored, as the reference's switch is.
        }
    }

    /// <summary>The <c>ubyte</c> length and its bytes, clamped to what arrived.</summary>
    private static string ReadMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return string.Empty;

        var length = Math.Min(data[1], data.Length - 2);
        return Gg2Text.Decode(data.Slice(2, length));
    }

    // --- sending -------------------------------------------------------------

    /// <summary>
    /// <c>global.sendChatMessage</c> (<c>chat_api.gml:2-29</c>): one line to
    /// everybody entitled to see it, or to one player.
    /// </summary>
    /// <param name="message">
    /// For a player's line this is the <b>raw</b> text they typed: the client
    /// adds the name and the colour. For a server line it is already coloured.
    /// </param>
    /// <param name="sender">A roster index, or one of the codes above.</param>
    /// <param name="audience">
    /// <see cref="AudienceEveryone"/>, or <c>team + 1</c> for one team. Only
    /// written to the wire when <paramref name="sender"/> is a roster index.
    /// </param>
    /// <param name="target">One slot, or -1 for everybody in chat.</param>
    /// <remarks>
    /// Player by player rather than as a broadcast, because a team message must
    /// not reach the other team's clients — a client cannot be trusted to hide
    /// what it has been sent. Global chat goes the same way rather than through
    /// <c>PluginPacketSend</c>, so a player who never sent hello never receives.
    /// <para>
    /// <b>A message longer than 255 bytes becomes several packets</b>, not a
    /// truncation: the reference's loop re-sends the remainder until it is
    /// empty. An empty message sends nothing at all, which is the same loop.
    /// </para>
    /// </remarks>
    private void SendChatMessage(string message, int sender, int audience, int target = -1)
    {
        var remaining = message;

        while (remaining.Length != 0)
        {
            var chunk = remaining.Length > MaxChunkLength ? remaining[..MaxChunkLength] : remaining;
            remaining = remaining.Length > MaxChunkLength ? remaining[MaxChunkLength..] : string.Empty;

            _out.Reset();
            _out.WriteUByte((byte)sender);

            if (sender < SenderPoll)
                _out.WriteUByte((byte)audience);

            _out.WriteString1(chunk);

            var payload = _out.WrittenSpan;

            if (target >= 0)
            {
                if (MayReceive(target, audience))
                    Channel.SendTo(target, payload);

                continue;
            }

            for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
                if (MayReceive(slot, audience))
                    Channel.SendTo(slot, payload);
        }
    }

    /// <summary>
    /// <c>chat_api.gml:21-22</c>: in chat, and on the right team if this is a
    /// team message.
    /// </summary>
    private bool MayReceive(int slot, int audience) =>
        _chatters.Has(slot)
        && _chatters[slot].HasChat
        && (audience == AudienceEveryone || (int)Host.TeamOf(slot) == audience - 1);

    /// <summary>
    /// A pre-coloured line about players, to everybody in chat. Suppressible by
    /// the client — see <see cref="SenderPlayerInfo"/>.
    /// </summary>
    private void Announce(string message)
    {
        SendChatMessage(message, SenderPlayerInfo, AudienceEveryone);
        Host.Log($"[chat] {LogText.Safe(message)}");
    }

    /// <summary>A line to everybody in chat that no client setting hides.</summary>
    private void Notice(string message)
    {
        SendChatMessage(message, SenderNotice, AudienceEveryone);
        Host.Log($"[chat] {LogText.Safe(message)}");
    }

    /// <summary>One line to one player, for a command's reply.</summary>
    private void Reply(int slot, string message) =>
        SendChatMessage(message, SenderNotice, AudienceEveryone, slot);

    /// <summary>
    /// A player's own line: the raw text, their roster index, and the audience.
    /// </summary>
    private void Relay(int slot, string message, int audience)
    {
        SendChatMessage(message, slot, audience);

        // The reference adds the line to the host's own window here. A headless
        // server has no window, so it goes where an operator will see it —
        // reconstructing what the client will draw, since the wire carries only
        // an index and the raw text.
        var scope = audience == AudienceEveryone ? string.Empty : $"({Host.TeamOf(slot)}) ";
        Host.Log($"[chat] {scope}{LogText.Safe(Host.NameOf(slot))}: {LogText.Safe(message)}");
    }

    /// <summary>
    /// <c>sanitiseNewlines</c>
    /// (<c>Scripts/Misc/Strings/sanitiseNewlines.gml</c>), which the host applies
    /// to every name it puts inside a line it builds.
    /// </summary>
    /// <remarks>
    /// GM8 draws <c>#</c> as a line break and <c>\#</c> as a <c>#</c>, so a
    /// player called <c>"a#b"</c> would otherwise split a server announcement
    /// across two lines and could forge the second one. CR and LF become spaces.
    /// <para>
    /// ⛔ <b>Applied only to text the SERVER composes.</b> A player's own message
    /// is relayed raw and sanitised by the client at draw time
    /// (<c>chat_api.gml:99</c>); doing it here as well would double the
    /// backslashes.
    /// </para>
    /// </remarks>
    private static string Sanitise(string text) =>
        text.Replace("#", "\\#", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
}
