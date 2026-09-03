using Gg2.Protocol;

namespace Gg2.Server.Plugins;

/// <summary>
/// The server half of <c>chat_v2_pr</c> — "chat_v2 by Lorgan, various fixes by
/// PrOF" — text chat between players.
/// </summary>
/// <remarks>
/// Transcribed from <c>plugin.gml</c> inside
/// <c>chat_v2_pr@46d0937c9206dcf27e036100d1ea8685.zip</c>, dated 2017-04-10.
/// Only the <c>global.isHost</c> branches are here: the client half — the window,
/// the font, the key bindings, the colour codes, the wrapping — is still the
/// plugin's own GML, downloaded and executed by the client exactly as before.
/// <para>
/// The GML is structured as one object with eight <c>event_user</c> handlers
/// standing in for scripts. The four this port needs are named below so the two
/// can be read side by side: <c>event0</c> is <see cref="Receive"/>,
/// <c>event1</c> is <see cref="Format"/>, <c>event2</c> is <see cref="Deliver"/>
/// and <c>event7</c> is <see cref="CheckForSpam"/>. <c>event3</c> and
/// <c>event6</c> are the client's send and receive; <c>event4</c> and
/// <c>event5</c> are drawing.
/// </para>
/// <para>
/// ⚠️ <b>The wire format is two bytes of framing and no version field.</b>
/// Client to server: a kind byte, then for a chat message a <c>ubyte</c> length
/// and that many bytes. Server to client: a <c>ubyte</c> length and that many
/// bytes, already coloured and already prefixed with the speaker's name — the
/// client renders whatever it is handed. There is nothing in the packet that
/// would let either side notice it was talking to a different version, which is
/// why <see cref="ServerPluginCatalogue"/> pins the zip.
/// </para>
/// <para>
/// ⚠️ Strings here are GM8 byte strings, not UTF-8 — see <see cref="Gg2Text"/>.
/// A char is a byte, which is what makes the 255-byte truncation below a plain
/// <c>string</c> slice.
/// </para>
/// </remarks>
public sealed partial class ChatV2Plugin : ServerPlugin
{
    /// <summary>Per-player state; the reference keeps these on the <c>Player</c> instance.</summary>
    private sealed class Chatter
    {
        /// <summary>Whether this player has announced the plugin and is not muted.</summary>
        public bool HasChat;

        /// <summary>
        /// The name last announced. <c>""</c> until the first tick, which is what
        /// makes the de-duplication below run once on join.
        /// </summary>
        public string NameBackup = string.Empty;

        public long LastChatTime;

        public int ChatCount;
    }

    /// <summary>Client-to-server packet kinds. The first byte of the body.</summary>
    private const byte KindHello = 0;
    private const byte KindGlobalChat = 1;
    private const byte KindTeamChat = 2;

    /// <summary>
    /// The <c>_team</c> field the host works in: 0 is everyone, otherwise
    /// <c>team + 1</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Off by one on purpose, on both sides. The client sends
    /// <c>_team + 1</c> for a boolean "is this team chat"
    /// (<c>event3</c>), so 1 means global and 2 means team; the host then
    /// re-derives 0-for-global / <c>1 + team</c> from the packet kind. The two
    /// numbers are not the same quantity even though both are called
    /// <c>_team</c>, and the only place they meet is the packet kind byte.
    /// </remarks>
    private const int TeamEveryone = 0;

    /// <summary>Colour codes the client half understands (<c>event5</c>).</summary>
    private const string ColourRed = "R";
    private const string ColourBlue = "B";
    private const string ColourWhite = "W";
    private const string ColourOrange = "O";

    /// <summary>The longest message the length prefix can carry.</summary>
    private const int MaxMessageLength = 255;

    /// <summary>
    /// Two consecutive messages closer together than this count towards a mute.
    /// </summary>
    private const long SpamWindowMilliseconds = 2000;

    /// <summary>Messages inside the window before a mute.</summary>
    private const int SpamLimit = 5;

    /// <summary>
    /// <c>min(18, ...)</c> — the prefix length two names are compared on, and the
    /// length a de-duplicated name is truncated to before the counter is
    /// appended.
    /// </summary>
    private const int NamePrefixLength = 18;

    private readonly PluginPlayerTable<Chatter> _chatters = new(() => new Chatter());

    /// <summary>
    /// <c>global.chatBanlist</c>: the addresses of players muted for spamming.
    /// </summary>
    /// <remarks>
    /// ⚠️ Keyed on the remote address, not the roster slot, so a mute survives a
    /// reconnect — which is the point — and catches everyone behind the same NAT,
    /// which is not. Transcribed rather than improved: a client sees a mute the
    /// reference would have given it.
    /// </remarks>
    private readonly List<string> _banlist = [];

    /// <summary>A scratch buffer for the one message being built. Never re-entered.</summary>
    private readonly NetWriter _out = new(MaxMessageLength + 1);

    public override string Name => "chat_v2_pr";

    public override void PlayerAdded(int slot) => _chatters.Add(slot);

    public override void PlayerRemoving(int slot) => _chatters.RemoveAt(slot);

    /// <summary>
    /// <c>Player</c>'s Begin Step, host branch: notice a rename, force it unique,
    /// and tell the chat about it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the one thing the plugin does that a client can see without
    /// the plugin</b> — it broadcasts <c>PLAYER_CHANGENAME</c>, so a player
    /// joining a chat server as "bob" while a "bob" is already there is renamed
    /// to "bob2" for everybody, plugin or not. It runs on join too, because a
    /// fresh <c>nameBkp</c> is <c>""</c> and so differs from any real name.
    /// <para>
    /// ⚠️ Placed after the tick's client commands rather than before, so a
    /// <c>PLAYER_CHANGENAME</c> that arrived this tick is de-duplicated in the
    /// same tick. GM8 gave the reference no such choice and no guarantee about
    /// which instance's Begin Step ran first; nothing observable turns on it.
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

            if (name == chatter.NameBackup)
                continue;

            var deduplicated = Deduplicate(name);
            if (deduplicated != name)
            {
                Host.Rename(slot, deduplicated);
                name = deduplicated;
            }

            // First sight of this player: adopt the name silently. The reference
            // exits here, which is why a join never announces a rename.
            if (chatter.NameBackup.Length == 0)
            {
                chatter.NameBackup = name;
                continue;
            }

            // ⚠️ Only announced to players who have chat. A rename by somebody
            // without the plugin leaves nameBkp stale for ever, so this branch
            // is re-evaluated every tick from then on — harmless, because the
            // de-duplication settles after one pass, and transcribed rather than
            // fixed because the fix would change what a stock client sees.
            if (!chatter.HasChat)
                continue;

            var announcement = $"{ColourOrange}{chatter.NameBackup} has changed their name to {name}.";
            Deliver(announcement, TeamEveryone);
            CheckForSpam(slot);
            chatter.NameBackup = name;
        }
    }

    /// <summary>
    /// <c>event0</c>: a packet from a client, dispatched on its kind byte.
    /// </summary>
    /// <remarks>
    /// ⚠️ The reference reads past the end of a short buffer and GM8 hands it
    /// zeroes; this clamps instead. Both refuse the packet in the end, but a
    /// truncated read here would be an exception on the tick thread rather than a
    /// message nobody sees.
    /// </remarks>
    public override void Receive(int slot, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        var chatter = _chatters[slot];

        switch (data[0])
        {
            case KindHello:
                // ⚠️ The mute check is here and nowhere else: a muted player who
                // reconnects re-sends hello and is refused, silently, which is
                // what makes the address-keyed ban list work at all.
                if (_banlist.Contains(Host.RemoteAddressOf(slot)) || chatter.HasChat)
                    break;

                chatter.HasChat = true;
                chatter.NameBackup = Host.NameOf(slot);

                Deliver($"{ColourOrange}{chatter.NameBackup} has joined chat!", TeamEveryone);
                break;

            case KindGlobalChat:
            {
                if (!chatter.HasChat)
                    break;

                var message = ReadMessage(data);

                // ⚠️ Intercepted BEFORE the delivery, so a command is never
                // echoed as chat. The client half has no idea commands exist —
                // it sends "/votekick bob" as ordinary text — which is what
                // makes a whole command layer possible without touching the GML.
                if (TryRunCommand(slot, message))
                {
                    CheckForSpam(slot);
                    break;
                }

                Deliver(Format(slot, message, TeamEveryone), TeamEveryone);

                // ⚠️ AFTER the delivery, which is the reference's order: the
                // message that trips the limit is still sent, and the mute
                // notice follows it.
                CheckForSpam(slot);
                break;
            }

            case KindTeamChat:
            {
                var team = Host.TeamOf(slot);
                if (team == Team.Spectator || !chatter.HasChat)
                    break;

                var message = ReadMessage(data);

                // A command typed into team chat is still a command. A player
                // who forgot which box they were in would otherwise broadcast
                // "/votekick bob" to their team as a sentence.
                if (TryRunCommand(slot, message))
                {
                    CheckForSpam(slot);
                    break;
                }

                var audience = 1 + (int)team;
                Deliver(Format(slot, message, audience), audience);
                CheckForSpam(slot);
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

    /// <summary>
    /// <c>event1</c>: turn a player's text into the coloured line the client
    /// half draws.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>#</c> is stripped from what the player typed because the client half
    /// uses it as its own marker: when a line is too long to fit the window,
    /// <c>event4</c> looks for the last <c>#</c> to find which colour the
    /// continuation should start in. A <c>#</c> in a message would recolour the
    /// rest of it.
    /// <para>
    /// ⚠️ The reference has a third branch here — <c>#O</c> instead of <c>#W</c>
    /// when the speaker is <c>global.myself</c>, so the host sees its own
    /// messages in orange. A headless server's slot 0 never speaks, so that
    /// branch is unreachable and is not transcribed.
    /// </para>
    /// </remarks>
    private string Format(int slot, string message, int audience)
    {
        message = message.Replace("#", " ", StringComparison.Ordinal);

        var colour = Host.TeamOf(slot) switch
        {
            Team.Red => ColourRed,
            Team.Blue => ColourBlue,
            _ => ColourWhite,
        };

        var name = Host.NameOf(slot);

        return audience == TeamEveryone
            ? $"{colour}{name}: #{ColourWhite}{message}"
            : $"{colour}{name}: {message}";
    }

    /// <summary>
    /// <c>event2</c>: send one finished line to everyone entitled to see it.
    /// </summary>
    /// <param name="audience">
    /// <see cref="TeamEveryone"/>, or <c>team + 1</c> for one team only.
    /// </param>
    /// <remarks>
    /// ⚠️ Player by player rather than as a broadcast, because a team message
    /// must not reach the other team's clients — a client cannot be trusted to
    /// hide what it has been sent. The reference uses <c>PluginPacketSendTo</c>
    /// in a <c>with(Player)</c> loop for the same reason, and global chat goes
    /// the same way rather than through <c>PluginPacketSend</c>, so that a player
    /// who never sent hello never receives.
    /// </remarks>
    private void Deliver(string message, int audience)
    {
        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength];

        _out.Reset();
        _out.WriteString1(message);

        var payload = _out.WrittenSpan;

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
        {
            if (!_chatters[slot].HasChat)
                continue;

            if (audience != TeamEveryone && (int)Host.TeamOf(slot) != audience - 1)
                continue;

            Channel.SendTo(slot, payload);
        }

        // The reference adds the line to the host's own window here (event4). A
        // headless server has no window, so it goes where an operator will
        // actually see it.
        Host.Log($"[chat] {message}");
    }

    /// <summary>
    /// One line to one player, for a command's reply.
    /// </summary>
    /// <remarks>
    /// The reference sends these with the same <c>sendChatMessage</c> it
    /// broadcasts with, passing a target as a fourth argument. ⚠️ It reaches a
    /// player who is not in chat: a command's answer has to arrive even when the
    /// answer is "you are muted", or the command looks broken rather than
    /// refused.
    /// </remarks>
    private void Whisper(int slot, string message)
    {
        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength];

        _out.Reset();
        _out.WriteString1(message);
        Channel.SendTo(slot, _out.WrittenSpan);
    }

    /// <summary>
    /// <c>event7</c>: more than five messages inside two seconds mutes the
    /// sender.
    /// </summary>
    /// <remarks>
    /// ⚠️ The window restarts from the <em>last</em> message, not from the first:
    /// the count only resets after a two-second gap, so a player typing steadily
    /// every 1.9 seconds is muted on their sixth line. That is the reference's
    /// behaviour and players are used to it.
    /// </remarks>
    private void CheckForSpam(int slot)
    {
        var chatter = _chatters[slot];
        var now = Host.CurrentTimeMilliseconds;

        if (chatter.LastChatTime > now - SpamWindowMilliseconds)
        {
            chatter.ChatCount++;

            if (chatter.ChatCount > SpamLimit)
            {
                var name = Host.NameOf(slot);
                Deliver($"{ColourOrange}{name} has been muted due to spamming.", TeamEveryone);

                Mute(slot);
            }
        }
        else
        {
            chatter.ChatCount = 1;
        }

        chatter.LastChatTime = now;
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

    /// <summary>
    /// Appends a counter to a name that shares its first 18 bytes with somebody
    /// else's, case-insensitively.
    /// </summary>
    /// <remarks>
    /// ⚠️ The counter is <b>how many players match</b>, not the next free
    /// number, so the third "bob" becomes "bob3" — and if "bob2" then leaves,
    /// the next "bob" is "bob2" again. It also counts the renaming player
    /// itself, which is why the test is <c>&gt; 1</c>.
    /// </remarks>
    private string Deduplicate(string name)
    {
        var length = Math.Min(NamePrefixLength, name.Length);
        var prefix = name[..length].ToLowerInvariant();

        var matches = 0;
        for (var other = 0; other < Host.PlayerCount; other++)
        {
            var otherName = Host.NameOf(other);
            var otherPrefix = otherName[..Math.Min(length, otherName.Length)].ToLowerInvariant();

            if (otherPrefix == prefix)
                matches++;
        }

        // Always at least one: the player being checked matches itself.
        return matches > 1 ? name[..length] + matches.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
    }
}
