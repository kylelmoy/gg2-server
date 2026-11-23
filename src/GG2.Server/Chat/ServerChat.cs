using System.Text;
using GG2.Protocol;

using GG2.Server.Plugins;

namespace GG2.Server;

/// <summary>
/// What an operator can change about <see cref="ServerChat"/>: <c>[Chat]</c> in
/// <c>--config</c>, as <see cref="ServerOptions.Chat"/>.
/// </summary>
/// <remarks>
/// The defaults are the GML reference host's (<see cref="ServerChat"/> names it).
/// <see cref="ServerChat"/> reads them through
/// <see cref="IPluginHost.ChatSettings"/> when it attaches.
/// </remarks>
public sealed record ChatSettings
{
    /// <summary>
    /// <c>Enabled</c>: the server runs chat and advertises <c>chat</c> for it. Off,
    /// there is no chat and nothing is advertised for it.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary><c>Gravetalk</c>: whether the living hear the dead and spectators.</summary>
    public bool Gravetalk { get; init; } = true;

    /// <summary><c>UniqueNames</c>: suffix a duplicate name with the lowest free number.</summary>
    public bool UniqueNames { get; init; }

    /// <summary><c>MuteMinutes</c>: a vote mute, and <c>/mute</c> with no minutes.</summary>
    public int MuteMinutes { get; init; } = 10;

    /// <summary><c>Votes</c>.</summary>
    public bool VotesEnabled { get; init; } = true;

    /// <summary><c>VoteKickPercent</c>: the Yes share a kick needs, inclusive.</summary>
    public int VoteKickPercent { get; init; } = 60;

    /// <summary><c>VoteMutePercent</c>: the Yes share a mute needs, inclusive.</summary>
    public int VoteMutePercent { get; init; } = 60;

    /// <summary><c>KickBanMinutes</c>: how long a vote-kicked address is refused.</summary>
    public int KickBanMinutes { get; init; } = 10;

    /// <summary><c>AdminPassword</c>; empty disables <c>/login</c>.</summary>
    public string AdminPassword { get; init; } = string.Empty;

    /// <summary>The ranges the reference host clamps to, refused up front here.</summary>
    public void Validate()
    {
        if (MuteMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(MuteMinutes), MuteMinutes, "Chat.MuteMinutes must be 1..1440");

        if (KickBanMinutes is < 0 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(KickBanMinutes), KickBanMinutes, "Chat.KickBanMinutes must be 0..1440");

        if (VoteKickPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(VoteKickPercent), VoteKickPercent, "Chat.VoteKickPercent must be 1..100");

        if (VoteMutePercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(VoteMutePercent), VoteMutePercent, "Chat.VoteMutePercent must be 1..100");
    }
}

/// <summary>
/// The server's chat: one room, one rate limit, one mute and kick-ban list, one
/// command console and one vote engine, spoken to each player in the format that
/// player greeted with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Native to the server, carried by the <c>chat</c> plugin's packet id.</b> A
/// stock GM8 client has no chat of its own: it gets one by downloading
/// <c>chat@bad8081d64f0b808a2bb3a6d88978fb5</c> (Lorgan3's chat v6) from
/// ganggarrison.com. So with chat on (<see cref="ChatSettings.Enabled"/>, the
/// default) the server advertises <c>chat</c> itself and this object answers its
/// packet id. Nothing else about it is a plugin: the server builds it, owns it and
/// configures it from <c>[Chat]</c>.
/// </para>
/// <para>
/// <b>Two formats on that id.</b> A stock client speaks v6 (<c>plugin.gml</c>,
/// <c>chat_api.gml</c>, <c>vote_api.gml</c> in that zip). A native client
/// sends <see cref="Envelope"/> followed by a HELLO, and from then on
/// every message to and from it is one native message behind that byte. The
/// native format is version 1 of the native chat protocol, which is the
/// spec both ends cite. v6's client kinds are 0-3 and its host ignores any other
/// first byte (<c>plugin.gml</c> user event 0 has no <c>default</c>), so a native
/// client may greet any <c>chat</c> server this way.
/// </para>
/// <para>
/// <b>The behaviour is the native one for everybody</b>: TF2 gravetalk,
/// spectator team chat, a token-bucket rate limit, timed address mutes and kick
/// bans, commands in team chat too, and its vote thresholds and quorum. Only the
/// encoding differs, per recipient:
/// </para>
/// <list type="table">
/// <item><term>a player's line</term><description>native LINE with the server's flags; v6 roster index, audience (0, or 1 + team) and raw text. v6 has no field for DEAD or SPEC.</description></item>
/// <item><term>/me</term><description>LINE with the action flag; v6 <c>chr(13)</c> + text, the form v6's own <c>/me</c> sends; <c>doColorCode</c> draws it as <c>*Name text</c>.</description></item>
/// <item><term>a notice</term><description>NOTICE with its category; v6 254 for join, leave, rename and team change (a client may hide those), 255 for everything else, as <c>O</c> + text with <c>#</c> escaped.</description></item>
/// <item><term>the MOTD</term><description>WELCOME's motd; v6 a 255 on hello, where <c>#</c> stays GM8's line break.</description></item>
/// <item><term>a vote</term><description>VOTE_START + VOTE_TALLY, VOTE_TALLY, VOTE_END; v6 204, one 203 per new answer, 201 then the result as a 255.</description></item>
/// </list>
/// <para>
/// ⛔ <b>Nothing is sent to a player who has not greeted</b>, in either format. A
/// GM8 client without the plugin raises a fatal error on a plugin packet it has no
/// queue for, and a native client that joined without chat must stay
/// silent. And <b>an enveloped frame never reaches a v6 player</b>: v6 would read
/// <see cref="Envelope"/> as player 128.
/// </para>
/// <para>
/// <b>The most recent hello sets the format.</b> A repeat in the same format is
/// ignored; a hello in the other format switches the player and greets them again
/// in it, with the running vote re-offered without clearing their answer. A native
/// client that gave up waiting for WELCOME and fell back to v6 therefore ends up
/// v6 at both ends.
/// </para>
/// <para>
/// The host half's logic is transcribed from the native chat plugin's GML host
/// (<c>src/host/</c>, <c>src/commands/</c>, <c>src/votes/</c>); a method citing a
/// <c>host/*.gml</c> file names the file it stands for.
/// </para>
/// </remarks>
public sealed partial class ServerChat(ChatSettings? settings = null) : ServerPlugin, IServerChat
{
    /// <summary>PROTOCOL.md, "Protocol version".</summary>
    public const byte ProtocolVersion = 1;

    /// <summary>
    /// The first byte of a native message, in both directions. Outside v6's
    /// client kinds (0-3), and never sent to a v6 player.
    /// </summary>
    public const byte Envelope = 0x80;

    // Native, client to host.
    private const byte KindHello = 0;
    private const byte KindSay = 1;
    private const byte KindVote = 2;

    // Native, host to client.
    private const byte KindWelcome = 0;
    private const byte KindLine = 1;
    private const byte KindNotice = 2;
    private const byte KindVoteStart = 3;
    private const byte KindVoteTally = 4;
    private const byte KindVoteEnd = 5;

    // v6, client to host (plugin.gml:436-522).
    private const byte V6Hello = 0;
    private const byte V6GlobalChat = 1;
    private const byte V6TeamChat = 2;
    private const byte V6Vote = 3;

    // v6, host to client (plugin.gml:525-592). Below 200 the first byte is a roster index.
    private const byte V6Poll = 200;
    private const byte V6VoteResult = 201;
    private const byte V6VoteTally = 203;
    private const byte V6PollResync = 204;
    private const byte V6PlayerInfo = 254;
    private const byte V6Notice = 255;

    /// <summary>
    /// The colour code a v6 server line starts with. Not decoration: the v6 draw
    /// loop eats the first character of every line as a colour code
    /// (<c>plugin.gml</c>, the window's Draw), so without it the first letter is lost.
    /// </summary>
    private const string V6Orange = "O";

    // LINE flags.
    private const byte FlagTeam = 1;
    private const byte FlagDead = 2;
    private const byte FlagSpectator = 4;
    private const byte FlagAction = 8;

    // WELCOME serverFlags.
    private const byte ServerFlagGravetalk = 1;
    private const byte ServerFlagVotes = 2;
    private const byte ServerFlagUniqueNames = 4;

    /// <summary>NOTICE categories, PROTOCOL.md; the client filters on these.</summary>
    private enum Notice : byte
    {
        Server = 0,
        Join = 1,
        Leave = 2,
        Rename = 3,
        TeamChange = 4,
        Reply = 5,
        Vote = 6,
    }

    /// <summary>What a player greeted with, and so how everything sent to them is encoded.</summary>
    private enum Format : byte
    {
        /// <summary>No hello yet: sent nothing.</summary>
        None,
        V6,
        Native,
    }

    /// <summary>The native SAY cap. A v6 line may be a whole 255-byte string.</summary>
    private const int MaxSayLength = 127;
    private const int MaxStringLength = 255;

    /// <summary>The token bucket: a burst of three, then one message per two seconds.</summary>
    private const int RateBurst = 3;
    private const int RateSeconds = 2;

    /// <summary>Refusals inside the window that turn into a timed mute.</summary>
    private const int RefusalLimit = 10;
    private const long RefusalWindowMilliseconds = 30000;
    private const long SpamMuteMilliseconds = 60000;

    /// <summary>Per-player state; the GML keeps these on the <c>Player</c> instance.</summary>
    private sealed class Chatter
    {
        public Format Format;

        /// <summary>Greeted in either format, so may be sent anything in that format.</summary>
        public bool Hello => Format != Format.None;

        /// <summary>The native protocol version the player greeted with.</summary>
        public int Version;

        /// <summary><c>chatSeen</c>: announced, and name- and team-tracked from now on.</summary>
        public bool Seen;

        public string NameBackup = string.Empty;

        public Team TeamBackup = Team.Any;

        public int Tokens = RateBurst;

        public long TokenTime;

        public int Refusals;

        public long RefusalWindow;

        public long MutedUntil;

        /// <summary>The vote this player may answer, or -1.</summary>
        public int VoteId = -1;

        /// <summary>The option answered in <see cref="VoteId"/>, 0 for not yet.</summary>
        public int VoteChoice;

        /// <summary>Starts a minute in the past, so nobody waits at server start (GML fix A5).</summary>
        public long LastVoteCall = -60000;

        public bool Admin;

        /// <summary>A v6 player has the running vote's dialog open.</summary>
        public bool V6Dialog;

        /// <summary>
        /// What a v6 player's dialog has counted, per option. v6 can only add one
        /// vote at a time (203), so a count that falls is never shown to it.
        /// </summary>
        public readonly int[] V6Counted = new int[9];
    }

    private ChatSettings _settings = settings ?? new ChatSettings();

    private readonly PluginPlayerTable<Chatter> _chatters = new(() => new Chatter());

    /// <summary><c>global.chatMutes</c>: remote address to mute expiry.</summary>
    private readonly Dictionary<string, long> _mutes = new(StringComparer.Ordinal);

    /// <summary><c>global.chatKickBans</c>: remote address to kick-ban expiry.</summary>
    private readonly Dictionary<string, long> _kickBans = new(StringComparer.Ordinal);

    /// <summary>The native frame being built, envelope included.</summary>
    private readonly NetWriter _out = new(4096);

    /// <summary>The v6 frame being built. Separate, so one send can carry both formats.</summary>
    private readonly NetWriter _v6 = new(4096);

    private string _currentMap = string.Empty;

    /// <summary>The plugin whose packet id carries chat, and which the server advertises for it.</summary>
    public const string PluginName = "chat";

    public override string Name => PluginName;

    /// <summary>Settings passed to the constructor win; otherwise the server's.</summary>
    public override void Attach(PluginChannel channel)
    {
        base.Attach(channel);
        _settings = settings ?? channel.Host.ChatSettings;
    }

    /// <summary>Players dropped on arrival by a kick-ban, for tests and the log.</summary>
    public int KickBanDrops { get; private set; }

    private long Now => Host.CurrentTimeMilliseconds;

    public override void PlayerAdded(int slot)
    {
        _chatters.Add(slot);
        _chatters[slot].TokenTime = Now;
    }

    /// <summary>
    /// <c>Player</c>'s Destroy event: "X has left the game.", to everyone else.
    /// </summary>
    public override void PlayerRemoving(int slot)
    {
        if (_chatters.Has(slot))
        {
            var chatter = _chatters[slot];
            if (chatter.Seen && slot != 0)
            {
                chatter.Format = Format.None;
                SendNotice(Notice.Leave, Host.NameOf(slot) + " has left the game.");
            }

            _chatters.RemoveAt(slot);
        }
    }

    public override void MapChanged(string map)
    {
        _currentMap = map;
        _mapVoteUsed = false;
    }

    /// <summary>
    /// <c>Player</c>'s Begin Step for every player (<c>host/player_begin_step.gml</c>),
    /// then the vote clock (<c>host/vote_clock.gml</c>), in the GML's order.
    /// </summary>
    public override void Tick()
    {
        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
            TrackPlayer(slot);

        TickVote();
    }

    private void TrackPlayer(int slot)
    {
        var chatter = _chatters[slot];

        // The one thing a client without the plugin sees.
        if (Host.NameOf(slot).Length == 0)
            Host.Rename(slot, "Nameless");

        // Only for the player who just arrived or just renamed: whoever had the
        // name first keeps it. (The first GML build suffixed both, found live.)
        if (_settings.UniqueNames && (!chatter.Seen || Host.NameOf(slot) != chatter.NameBackup))
            MakeUnique(slot);

        var name = Host.NameOf(slot);
        var team = Host.TeamOf(slot);

        if (!chatter.Seen)
        {
            chatter.Seen = true;
            chatter.NameBackup = name;
            chatter.TeamBackup = team;

            if (ExpiryOf(_kickBans, Host.RemoteAddressOf(slot)) > Now)
            {
                KickBanDrops++;
                Host.Kick(slot, "You were vote-kicked from this server; try again later.");
                return;
            }

            if (slot != 0)
                SendNotice(Notice.Join, name + " has joined the game.");

            return;
        }

        if (name != chatter.NameBackup)
        {
            SendNotice(Notice.Rename, chatter.NameBackup + " changed name to " + name + ".");
            chatter.NameBackup = name;
        }

        if (team != chatter.TeamBackup)
        {
            chatter.TeamBackup = team;
            var teamName = team switch
            {
                Team.Red => "team RED",
                Team.Blue => "team BLU",
                _ => "the spectators",
            };
            SendNotice(Notice.TeamChange, name + " joined " + teamName + ".");
        }
    }

    /// <summary><c>host/fn_make_unique.gml</c>: append the lowest number that makes the name unique.</summary>
    private void MakeUnique(int slot)
    {
        var name = Host.NameOf(slot);
        if (!NameTaken(slot, name))
            return;

        for (var n = 2; ; n++)
        {
            var suffix = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = name[..Math.Min(name.Length, GameConstants.MaxPlayerNameLength - suffix.Length)] + suffix;
            if (!NameTaken(slot, candidate))
            {
                Host.Rename(slot, candidate);
                return;
            }
        }
    }

    private bool NameTaken(int slot, string name)
    {
        for (var other = 0; other < Host.PlayerCount; other++)
            if (other != slot && string.Equals(Host.NameOf(other), name, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    // --- receiving ----------------------------------------------------------

    /// <summary>
    /// One client-to-host packet. A leading <see cref="Envelope"/> means a native
    /// message and anything else is v6. A short
    /// packet is dropped, never read past, and an unknown kind is ignored.
    /// </summary>
    public override void Receive(int slot, ReadOnlySpan<byte> data)
    {
        if (!_chatters.Has(slot) || data.Length < 1)
            return;

        if (data[0] == Envelope)
            ReceiveNative(slot, data[1..]);
        else
            ReceiveV6(slot, data);
    }

    /// <summary><c>host/receive.gml</c>: one native message, without its envelope.</summary>
    private void ReceiveNative(int slot, ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return;

        var chatter = _chatters[slot];

        switch (data[0])
        {
            case KindHello:
                if (data.Length < 3 || chatter.Format == Format.Native)
                    return;

                chatter.Version = data[1];
                Greet(slot, Format.Native);
                return;

            case KindSay:
            {
                if (data.Length < 3)
                    return;

                var audience = data[1];
                int length = data[2];
                if (data.Length - 3 < length)
                    return;

                if (chatter.Format != Format.Native || chatter.Version != ProtocolVersion)
                    return;

                var text = Cap(StripControl(GG2Text.Decode(data.Slice(3, length)), " "), MaxSayLength);
                if (IsBlank(text))
                    return;

                Say(slot, audience == 1 ? 1 : 0, text, action: false);
                return;
            }

            case KindVote:
                if (data.Length < 3 || chatter.Format != Format.Native)
                    return;

                ReceiveVote(slot, data[1], data[2]);
                return;
        }
    }

    /// <summary>
    /// <c>plugin.gml:436-522</c>, v6's host receive: hello, global chat, team chat,
    /// a vote answer. What happens next is the core's, not v6's.
    /// </summary>
    private void ReceiveV6(int slot, ReadOnlySpan<byte> data)
    {
        var chatter = _chatters[slot];

        switch (data[0])
        {
            case V6Hello:
                if (chatter.Format != Format.V6)
                    Greet(slot, Format.V6);

                return;

            case V6GlobalChat:
            case V6TeamChat:
            {
                if (chatter.Format != Format.V6 || data.Length < 2)
                    return;

                var text = GG2Text.Decode(data.Slice(2, Math.Min(data[1], data.Length - 2)));

                // v6's client sends /me as chr(13) + the action (commands.gml:65-76).
                var action = text.Length > 0 && text[0] == '\r';
                if (action)
                    text = text[1..];

                text = Cap(StripControl(text, " "), MaxStringLength);
                if (IsBlank(text))
                    return;

                Say(slot, data[0] == V6TeamChat ? 1 : 0, text, action);
                return;
            }

            case V6Vote:
                // v6 carries no vote id, so an answer is for whatever vote is running.
                if (chatter.Format != Format.V6 || data.Length < 2)
                    return;

                ReceiveVote(slot, _voteId, data[1]);
                return;
        }
    }

    /// <summary>
    /// A hello in a format this player is not already in: switch to it, carry an
    /// address mute over, send the format's greeting, and offer the running vote.
    /// </summary>
    private void Greet(int slot, Format format)
    {
        var chatter = _chatters[slot];
        chatter.Format = format;
        chatter.V6Dialog = false;

        // A mute follows the address across a reconnect until it runs out.
        var expiry = ExpiryOf(_mutes, Host.RemoteAddressOf(slot));
        if (expiry > chatter.MutedUntil)
            chatter.MutedUntil = expiry;

        if (format == Format.Native)
            SendWelcome(slot);
        else if (Host.ChatJoinMessage.Length != 0)
            SendV6(slot, V6Notice, 0, V6Orange + Host.ChatJoinMessage.Replace('\n', ' ').Replace('\r', ' '));

        // Greeting mid-vote: VOTE_START (or v6's 204) doubles as the resync.
        if (_voteActive && ShownTo(slot))
            OfferVote(slot);
    }

    /// <summary><c>host/say.gml</c>: route one line, whichever format it came in.</summary>
    private void Say(int slot, int audience, string text, bool action)
    {
        var chatter = _chatters[slot];

        // /me is a chat line, not a command: muted and rate limited like one.
        if (!action && text.Length >= 4 && text[..4].Equals("/me ", StringComparison.OrdinalIgnoreCase))
        {
            action = true;
            text = text[4..].TrimStart(' ');
            if (text.Length == 0)
                return;
        }

        // Commands run whatever the audience, cost no token, and are never relayed.
        if (!action && text[0] == '/')
        {
            RunCommand(slot, text);
            return;
        }

        // Checked after dispatch, so a mute never silences a command's reply.
        if (chatter.MutedUntil > Now)
        {
            Reply(slot, "You are muted for another " + CeilSeconds(chatter.MutedUntil - Now) + " seconds.");
            return;
        }

        if (!TakeToken(slot))
            return;

        var spectating = Host.TeamOf(slot) == Team.Spectator;
        var dead = !spectating && !Host.IsAlive(slot);

        var flags = (byte)audience;
        if (dead)
            flags |= FlagDead;
        if (spectating)
            flags |= FlagSpectator;
        if (action)
            flags |= FlagAction;

        SendLine(slot, flags, text);
    }

    /// <summary>
    /// The token bucket (DESIGN.md 6.7). False, with the refusal already sent,
    /// when the player must wait.
    /// </summary>
    private bool TakeToken(int slot)
    {
        var chatter = _chatters[slot];
        var refillMilliseconds = RateSeconds * 1000L;

        if (chatter.Tokens >= RateBurst)
        {
            chatter.TokenTime = Now;
        }
        else
        {
            var add = (Now - chatter.TokenTime) / refillMilliseconds;
            if (add > 0)
            {
                chatter.Tokens = (int)Math.Min(RateBurst, chatter.Tokens + add);
                chatter.TokenTime += add * refillMilliseconds;
            }
        }

        if (chatter.Tokens >= 1)
        {
            chatter.Tokens--;
            return true;
        }

        if (Now - chatter.RefusalWindow > RefusalWindowMilliseconds)
        {
            chatter.RefusalWindow = Now;
            chatter.Refusals = 0;
        }

        chatter.Refusals++;
        if (chatter.Refusals >= RefusalLimit)
        {
            chatter.Refusals = 0;
            chatter.MutedUntil = Now + SpamMuteMilliseconds;
            SendNotice(Notice.Server, Host.NameOf(slot) + " has been muted for 1 minute for spamming.");
        }
        else
        {
            var wait = Math.Max(1, CeilSeconds(refillMilliseconds - (Now - chatter.TokenTime)));
            Reply(slot, wait == 1
                ? "Please wait 1 second before chatting again."
                : "Please wait " + wait + " seconds before chatting again.");
        }

        return false;
    }

    // --- sending ------------------------------------------------------------

    /// <summary>Starts a native frame in <see cref="_out"/>, behind the envelope.</summary>
    private void Begin(byte kind)
    {
        _out.Reset();
        _out.WriteUByte(Envelope);

        _out.WriteUByte(kind);
    }

    /// <summary>
    /// <c>global.sendChatMessage</c> (<c>chat_api.gml:2-29</c>) for one v6 player:
    /// a text longer than 255 bytes goes as several packets, and an empty one as
    /// none. <paramref name="code"/> below 200 is the speaker's roster index and
    /// carries <paramref name="audience"/>.
    /// </summary>
    private void SendV6(int slot, int code, int audience, string text)
    {
        var remaining = text;
        while (remaining.Length != 0)
        {
            var chunk = remaining.Length > MaxStringLength ? remaining[..MaxStringLength] : remaining;
            remaining = remaining.Length > MaxStringLength ? remaining[MaxStringLength..] : string.Empty;

            _v6.Reset();
            _v6.WriteUByte((byte)code);
            if (code < V6Poll)
                _v6.WriteUByte((byte)audience);

            _v6.WriteString1(chunk);
            Channel.SendTo(slot, _v6.WrittenSpan);
        }
    }

    /// <summary><c>host/fn_send_welcome.gml</c>.</summary>
    private void SendWelcome(int slot)
    {
        byte flags = 0;
        if (_settings.Gravetalk)
            flags |= ServerFlagGravetalk;
        if (_settings.VotesEnabled)
            flags |= ServerFlagVotes;
        if (_settings.UniqueNames)
            flags |= ServerFlagUniqueNames;

        Begin(KindWelcome);
        _out.WriteUByte(ProtocolVersion);
        _out.WriteUByte(flags);
        _out.WriteUByte(RateBurst);
        _out.WriteUByte(RateSeconds);
        _out.WriteString1(Cap(StripControl(Host.ChatJoinMessage, " "), MaxStringLength));
        Channel.SendTo(slot, _out.WrittenSpan);
    }

    /// <summary>
    /// <c>host/fn_send_line.gml</c>: a player's line to everyone who should see it.
    /// Team chat reaches the sender's team (spectators reach spectators). Public
    /// chat reaches everyone, except that with gravetalk off a dead or spectating
    /// sender reaches only dead and spectating players.
    /// </summary>
    private void SendLine(int sender, byte flags, string text)
    {
        text = Cap(StripControl(text, " "), MaxStringLength);
        var name = Cap(StripControl(Host.NameOf(sender), " "), MaxStringLength);
        var senderTeam = Host.TeamOf(sender);
        var teamOnly = (flags & FlagTeam) != 0;
        var graveOnly = !teamOnly && !_settings.Gravetalk && (flags & (FlagDead | FlagSpectator)) != 0;

        Begin(KindLine);
        _out.WriteUByte((byte)(sender > 254 ? 255 : sender));
        _out.WriteUByte(flags);
        _out.WriteUByte((byte)senderTeam);
        _out.WriteString1(name);
        _out.WriteString1(text);

        // v6 names and colours the line itself from the roster index, so it gets
        // the raw text. The action form is chr(13) then the text, as v6's own
        // /me sends it (commands.gml:65-76). doColorCode runs sanitiseNewlines
        // first, which turns the chr(13) into a space, so nothing after it is
        // read as a colour code; a stock client draws "*Name text". Measured live.
        var v6Text = (flags & FlagAction) != 0 ? "\r" + text : text;
        var v6Audience = teamOnly ? 1 + (int)senderTeam : 0;

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
        {
            var format = _chatters[slot].Format;
            if (format == Format.None)
                continue;

            if (teamOnly && Host.TeamOf(slot) != senderTeam)
                continue;

            if (graveOnly && Host.TeamOf(slot) != Team.Spectator && Host.IsAlive(slot))
                continue;

            // A roster index of 200 or more would read as a v6 code. No roster
            // is that large; drop rather than mislabel.
            if (format == Format.Native)
                Channel.SendTo(slot, _out.WrittenSpan);
            else if (sender < V6Poll)
                SendV6(slot, sender, v6Audience, v6Text);
        }

        Host.Log($"[chat] {(teamOnly ? "(TEAM) " : string.Empty)}{LogText.Safe(name)}: {LogText.Safe(text)}");
        Host.ChatHeard(sender, text, teamOnly);
    }

    /// <summary>
    /// The admin panel's chat box: a line from slot 0, through the same path as
    /// a player's, so each client names and colours it from its own roster entry.
    /// </summary>
    /// <remarks>
    /// Not through the command console, and not rate limited: a leading <c>/</c>
    /// from the operator is text.
    /// </remarks>
    public void SayAsServer(string message)
    {
        message = Cap(StripControl(message, " "), MaxStringLength);
        if (!IsBlank(message))
            SendLine(0, 0, message);
    }

    /// <summary><c>host/fn_notice.gml</c>: a notice to every greeted player.</summary>
    private void SendNotice(Notice category, string text)
    {
        text = Cap(StripControl(text, " "), MaxStringLength);
        WriteNotice(category, text);

        var v6Code = category is Notice.Join or Notice.Leave or Notice.Rename or Notice.TeamChange
            ? V6PlayerInfo
            : V6Notice;

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
            SendNoticeTo(slot, v6Code, text);

        Host.Log($"[chat] {LogText.Safe(text)}");
    }

    /// <summary><c>host/fn_notice_to.gml</c> with category 5: a private reply.</summary>
    private void Reply(int slot, string text)
    {
        if (!_chatters.Has(slot) || !_chatters[slot].Hello)
            return;

        text = Cap(StripControl(text, " "), MaxStringLength);
        WriteNotice(Notice.Reply, text);
        SendNoticeTo(slot, V6Notice, text);
    }

    /// <summary>The notice in <see cref="_out"/> to one player, or its v6 form.</summary>
    private void SendNoticeTo(int slot, byte v6Code, string text)
    {
        switch (_chatters[slot].Format)
        {
            case Format.Native:
                Channel.SendTo(slot, _out.WrittenSpan);
                break;

            case Format.V6:
                SendV6(slot, v6Code, 0, V6Orange + Sanitise(text));
                break;
        }
    }

    private void WriteNotice(Notice category, string text)
    {
        Begin(KindNotice);
        _out.WriteUByte((byte)category);
        _out.WriteString1(text);
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// <c>shared/fn_strip_control.gml</c>: every byte below 32 becomes
    /// <paramref name="replacement"/>, so the client's segment separator
    /// (<c>chr(1)</c>) cannot occur inside text.
    /// </summary>
    internal static string StripControl(string text, string replacement)
    {
        var needs = false;
        foreach (var c in text)
            if (c < ' ')
            {
                needs = true;
                break;
            }

        if (!needs)
            return text;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < ' ')
                builder.Append(replacement);
            else
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>sanitiseNewlines</c>, for a server-composed v6 line: GM8 draws <c>#</c>
    /// as a line break, so a player called <c>a#b</c> could otherwise split an
    /// announcement and forge its second half. A player's own line is not passed
    /// through this: the v6 client escapes it at draw time (<c>chat_api.gml:99</c>).
    /// </summary>
    private static string Sanitise(string text) => text.Replace("#", "\\#", StringComparison.Ordinal);

    private static string Cap(string text, int length) => text.Length > length ? text[..length] : text;

    private static bool IsBlank(string text)
    {
        foreach (var c in text)
            if (c != ' ')
                return false;

        return true;
    }

    /// <summary>GML's <c>ceil(ms / 1000)</c>.</summary>
    private static long CeilSeconds(long milliseconds) => (long)Math.Ceiling(milliseconds / 1000.0);

    /// <summary><c>host/fn_ip_expiry.gml</c>: an address's expiry, 0 if none; expired entries are removed.</summary>
    private long ExpiryOf(Dictionary<string, long> map, string address)
    {
        if (address.Length == 0 || !map.TryGetValue(address, out var expiry))
            return 0;

        if (expiry <= Now)
        {
            map.Remove(address);
            return 0;
        }

        return expiry;
    }

    /// <summary><c>host/fn_ip_set.gml</c>; an expiry of 0 or less removes the entry.</summary>
    private static void SetExpiry(Dictionary<string, long> map, string address, long expiry)
    {
        if (address.Length == 0)
            return;

        if (expiry > 0)
            map[address] = expiry;
        else
            map.Remove(address);
    }
}
