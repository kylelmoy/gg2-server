using System.Text;
using Gg2.Protocol;

namespace Gg2.Server.Plugins;

/// <summary>
/// What an operator can change about <see cref="Chat2026Plugin"/>. The defaults
/// are the GML plugin's own defaults for the same <c>[Plugins]</c> keys.
/// </summary>
/// <remarks>
/// ⚠️ <b>Not yet reachable from <c>--config</c>.</b> The catalogue builds the
/// plugin with these defaults; wiring the keys through <c>ServerSettings</c> is
/// recorded as open in <c>docs/PLUGINS.md</c>, "chat2026".
/// </remarks>
public sealed record Chat2026Settings
{
    /// <summary><c>chat_gravetalk</c>: whether the living hear the dead and spectators.</summary>
    public bool Gravetalk { get; init; } = true;

    /// <summary><c>chat_unique_names</c>: suffix a duplicate name with the lowest free number.</summary>
    public bool UniqueNames { get; init; }

    /// <summary><c>chat_mute_minutes</c>: a vote mute, and <c>/mute</c> with no minutes.</summary>
    public int MuteMinutes { get; init; } = 10;

    /// <summary><c>chat_votes</c>.</summary>
    public bool VotesEnabled { get; init; } = true;

    /// <summary><c>chat_vote_kick_percent</c>: the Yes share a kick needs, inclusive.</summary>
    public int VoteKickPercent { get; init; } = 60;

    /// <summary><c>chat_vote_mute_percent</c>: the Yes share a mute needs, inclusive.</summary>
    public int VoteMutePercent { get; init; } = 60;

    /// <summary><c>chat_kickban_minutes</c>: how long a vote-kicked address is refused.</summary>
    public int KickBanMinutes { get; init; } = 10;

    /// <summary><c>chat_admin_password</c>; empty disables <c>/login</c>.</summary>
    public string AdminPassword { get; init; } = string.Empty;
}

/// <summary>
/// The server half of <c>chat2026</c>: TF2-style text chat, announcements, a
/// command console and votes.
/// </summary>
/// <remarks>
/// Transcribed from the <c>src/host/</c>, <c>src/commands/</c> and
/// <c>src/votes/</c> GML of <c>github.com/kylelmoy/gg2-chat2026</c> (the local
/// checkout is <c>D:\Code\gg2-chat2026</c>), whose <c>PROTOCOL.md</c> is the
/// contract both halves cite. Unlike <see cref="ChatPlugin"/> and
/// <see cref="ChatV2Plugin"/> the wire is <b>versioned</b>: HELLO and WELCOME
/// both carry <see cref="ProtocolVersion"/>, and a client on another version
/// disables its own sending rather than half-working.
/// <para>
/// <b>Where this differs from the GML host, and why.</b> Nothing below is
/// visible to a client except where noted.
/// <list type="bullet">
/// <item>The GM8 host is also a player with a chat window; slot 0 here is not.
/// It never greets, so it never receives, never votes and is never counted
/// towards a quorum — which is what the GML does on a dedicated server
/// (<c>chat_vote_host</c> defaults off there).</item>
/// <item>The GML makes a map vote land by pointing
/// <c>GameServer.currentMapIndex</c> at the slot before the map; this calls
/// <see cref="IPluginHost.SetNextMap"/>, which is the same outcome through this
/// server's own override. Map votes and the picker offer
/// <see cref="IPluginHost.VotableMaps"/> rather than only the rotation, so a
/// single-map server — the default — can still vote on a map. A client sees
/// more maps offered, nothing else.</item>
/// <item>A chat line is logged rather than drawn in a host window.</item>
/// </list>
/// </para>
/// </remarks>
public sealed partial class Chat2026Plugin(Chat2026Settings? settings = null) : ServerPlugin
{
    /// <summary>PROTOCOL.md, "Protocol version".</summary>
    public const byte ProtocolVersion = 1;

    // Client to host.
    private const byte KindHello = 0;
    private const byte KindSay = 1;
    private const byte KindVote = 2;

    // Host to client.
    private const byte KindWelcome = 0;
    private const byte KindLine = 1;
    private const byte KindNotice = 2;
    private const byte KindVoteStart = 3;
    private const byte KindVoteTally = 4;
    private const byte KindVoteEnd = 5;

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
        /// <summary><c>chatHello</c>: sent HELLO, so may be sent anything.</summary>
        public bool Hello;

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
    }

    private readonly Chat2026Settings _settings = settings ?? new Chat2026Settings();

    private readonly PluginPlayerTable<Chatter> _chatters = new(() => new Chatter());

    /// <summary><c>global.chatMutes</c>: remote address to mute expiry.</summary>
    private readonly Dictionary<string, long> _mutes = new(StringComparer.Ordinal);

    /// <summary><c>global.chatKickBans</c>: remote address to kick-ban expiry.</summary>
    private readonly Dictionary<string, long> _kickBans = new(StringComparer.Ordinal);

    private readonly NetWriter _out = new(1024);

    private string _currentMap = string.Empty;

    public override string Name => "chat2026";

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
                chatter.Hello = false;
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
    /// <c>host/receive.gml</c>: one client-to-host packet. A short packet is
    /// dropped, never read past.
    /// </summary>
    public override void Receive(int slot, ReadOnlySpan<byte> data)
    {
        if (!_chatters.Has(slot) || data.Length < 1)
            return;

        var chatter = _chatters[slot];
        var at = 1;

        switch (data[0])
        {
            case KindHello:
            {
                if (data.Length < 3 || chatter.Hello)
                    return;

                chatter.Version = data[1];
                chatter.Hello = true;

                // A mute follows the address across a reconnect until it runs out.
                var expiry = ExpiryOf(_mutes, Host.RemoteAddressOf(slot));
                if (expiry > chatter.MutedUntil)
                    chatter.MutedUntil = expiry;

                SendWelcome(slot);

                // Greeting mid-vote: VOTE_START doubles as the resync.
                if (_voteActive && _voteOnly is null)
                    OfferVote(slot);

                return;
            }

            case KindSay:
            {
                if (data.Length < 3)
                    return;

                var audience = data[1];
                int length = data[2];
                at = 3;
                if (data.Length - at < length)
                    return;

                if (!chatter.Hello || chatter.Version != ProtocolVersion)
                    return;

                var text = Cap(StripControl(Gg2Text.Decode(data.Slice(at, length)), " "), MaxSayLength);
                if (IsBlank(text))
                    return;

                Say(slot, audience == 1 ? 1 : 0, text);
                return;
            }

            case KindVote:
            {
                if (data.Length < 3)
                    return;

                ReceiveVote(slot, data[1], data[2]);
                return;
            }

            // Unknown kinds are ignored (PROTOCOL.md).
        }
    }

    /// <summary><c>host/say.gml</c>: route one SAY.</summary>
    private void Say(int slot, int audience, string text)
    {
        var chatter = _chatters[slot];

        // /me is a chat line, not a command: muted and rate limited like one.
        var action = false;
        if (text.Length >= 4 && text[..4].Equals("/me ", StringComparison.OrdinalIgnoreCase))
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

        _out.Reset();
        _out.WriteUByte(KindWelcome);
        _out.WriteUByte(ProtocolVersion);
        _out.WriteUByte(flags);
        _out.WriteUByte(RateBurst);
        _out.WriteUByte(RateSeconds);
        _out.WriteString1(Cap(StripControl(Host.ChatJoinMessage, " "), MaxStringLength));
        Channel.SendTo(slot, _out.WrittenSpan);
    }

    /// <summary>
    /// <c>host/fn_send_line.gml</c>: LINE to everyone who should see it. Team chat
    /// reaches the sender's team (spectators reach spectators). Public chat reaches
    /// everyone, except that with gravetalk off a dead or spectating sender reaches
    /// only dead and spectating players.
    /// </summary>
    private void SendLine(int sender, byte flags, string text)
    {
        text = Cap(StripControl(text, " "), MaxStringLength);
        var name = Cap(StripControl(Host.NameOf(sender), " "), MaxStringLength);
        var senderTeam = Host.TeamOf(sender);
        var teamOnly = (flags & FlagTeam) != 0;
        var graveOnly = !teamOnly && !_settings.Gravetalk && (flags & (FlagDead | FlagSpectator)) != 0;

        _out.Reset();
        _out.WriteUByte(KindLine);
        _out.WriteUByte((byte)(sender > 254 ? 255 : sender));
        _out.WriteUByte(flags);
        _out.WriteUByte((byte)senderTeam);
        _out.WriteString1(name);
        _out.WriteString1(text);

        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
        {
            if (!_chatters[slot].Hello)
                continue;

            if (teamOnly && Host.TeamOf(slot) != senderTeam)
                continue;

            if (graveOnly && Host.TeamOf(slot) != Team.Spectator && Host.IsAlive(slot))
                continue;

            Channel.SendTo(slot, _out.WrittenSpan);
        }

        Host.Log($"[chat] {(teamOnly ? "(TEAM) " : string.Empty)}{name}: {text}");
    }

    /// <summary><c>host/fn_notice.gml</c>: NOTICE to every greeted player.</summary>
    private void SendNotice(Notice category, string text)
    {
        WriteNotice(category, text);
        for (var slot = 0; slot < Host.PlayerCount && _chatters.Has(slot); slot++)
            if (_chatters[slot].Hello)
                Channel.SendTo(slot, _out.WrittenSpan);

        Host.Log($"[chat] {text}");
    }

    /// <summary><c>host/fn_notice_to.gml</c> with category 5: a private reply.</summary>
    private void Reply(int slot, string text)
    {
        if (!_chatters.Has(slot) || !_chatters[slot].Hello)
            return;

        WriteNotice(Notice.Reply, text);
        Channel.SendTo(slot, _out.WrittenSpan);
    }

    private void WriteNotice(Notice category, string text)
    {
        _out.Reset();
        _out.WriteUByte(KindNotice);
        _out.WriteUByte((byte)category);
        _out.WriteString1(Cap(StripControl(text, " "), MaxStringLength));
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
