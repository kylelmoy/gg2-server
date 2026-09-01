using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// Checks the transcribed constant table against <c>Constants.xml</c> itself.
/// </summary>
/// <remarks>
/// The table was generated from that file rather than typed, but a generator
/// run once is not a guarantee that the file still says the same thing. This is
/// the test that notices when the reference fork moves — which
/// <c>CLAUDE.md</c> warns is when the protocol docs need re-checking.
/// <para>
/// The name mappings below are written out rather than derived, because the
/// reference's naming is not mechanical: <c>PLAYER_CHANGENAME</c>,
/// <c>INPUTSTATE</c>, <c>UNLOCKCP</c> and <c>ARENA_ENDROUND</c> all break a
/// SCREAMING_SNAKE round trip, and a clever transform that quietly renames one
/// of them is worse than a list.
/// </para>
/// </remarks>
public sealed class ConstantsTests
{
    /// <summary>Every message id, paired with the reference name it came from.</summary>
    private static readonly (string Name, MessageType Value)[] MessageIds =
    [
        ("HELLO", MessageType.Hello),
        ("PLAYER_JOIN", MessageType.PlayerJoin),
        ("PLAYER_LEAVE", MessageType.PlayerLeave),
        ("PLAYER_CHANGETEAM", MessageType.PlayerChangeTeam),
        ("PLAYER_CHANGECLASS", MessageType.PlayerChangeClass),
        ("PLAYER_SPAWN", MessageType.PlayerSpawn),
        ("INPUTSTATE", MessageType.InputState),
        ("CHANGE_MAP", MessageType.ChangeMap),
        ("FULL_UPDATE", MessageType.FullUpdate),
        ("QUICK_UPDATE", MessageType.QuickUpdate),
        ("PLAYER_DEATH", MessageType.PlayerDeath),
        ("SERVER_FULL", MessageType.ServerFull),
        ("RED_TEAM_CAP", MessageType.RedTeamCap),
        ("BLUE_TEAM_CAP", MessageType.BlueTeamCap),
        ("MAP_END", MessageType.MapEnd),
        ("CHAT_BUBBLE", MessageType.ChatBubble),
        ("BUILD_SENTRY", MessageType.BuildSentry),
        ("DESTROY_SENTRY", MessageType.DestroySentry),
        ("BALANCE", MessageType.Balance),
        ("GRAB_INTEL", MessageType.GrabIntel),
        ("SCORE_INTEL", MessageType.ScoreIntel),
        ("DROP_INTEL", MessageType.DropIntel),
        ("UBER_CHARGED", MessageType.UberCharged),
        ("UBER", MessageType.Uber),
        ("OMNOMNOMNOM", MessageType.Omnomnomnom),
        ("PASSWORD_REQUEST", MessageType.PasswordRequest),
        ("PASSWORD_WRONG", MessageType.PasswordWrong),
        ("CAPS_UPDATE", MessageType.CapsUpdate),
        ("CP_CAPTURED", MessageType.CpCaptured),
        ("PLAYER_CHANGENAME", MessageType.PlayerChangeName),
        ("GENERATOR_DESTROY", MessageType.GeneratorDestroy),
        ("ARENA_WAIT_FOR_PLAYERS", MessageType.ArenaWaitForPlayers),
        ("ARENA_ENDROUND", MessageType.ArenaEndRound),
        ("ARENA_RESTART", MessageType.ArenaRestart),
        ("UNLOCKCP", MessageType.UnlockCp),
        ("SERVER_KICK", MessageType.ServerKick),
        ("KICK", MessageType.Kick),
        ("KICK_NAME", MessageType.KickName),
        ("ARENA_STARTROUND", MessageType.ArenaStartRound),
        ("TOGGLE_ZOOM", MessageType.ToggleZoom),
        ("RETURN_INTEL", MessageType.ReturnIntel),
        ("INCOMPATIBLE_PROTOCOL", MessageType.IncompatibleProtocol),
        ("JOIN_UPDATE", MessageType.JoinUpdate),
        ("DOWNLOAD_MAP", MessageType.DownloadMap),
        ("SENTRY_POSITION", MessageType.SentryPosition),
        ("REWARD_UPDATE", MessageType.RewardUpdate),
        ("REWARD_REQUEST", MessageType.RewardRequest),
        ("REWARD_CHALLENGE_CODE", MessageType.RewardChallengeCode),
        ("REWARD_CHALLENGE_RESPONSE", MessageType.RewardChallengeResponse),
        ("MESSAGE_STRING", MessageType.MessageString),
        ("WEAPON_FIRE", MessageType.WeaponFire),
        ("PLUGIN_PACKET", MessageType.PluginPacket),
        ("KICK_BAD_PLUGIN_PACKET", MessageType.KickBadPluginPacket),
        ("PING", MessageType.Ping),
        ("CLIENT_SETTINGS", MessageType.ClientSettings),
        ("KICK_MULTI_CLIENT", MessageType.KickMultiClient),
        ("RESERVE_SLOT", MessageType.ReserveSlot),
    ];

    private static readonly (string Name, double Value)[] Scalars =
    [
        ("STATE_UPDATE", GameConstants.StateUpdate),
        ("VERSION", GameConstants.Version),
        ("LOBBY_SERVER_PORT", GameConstants.LobbyServerPort),
        ("AUTH_SERVER_PORT", GameConstants.AuthServerPort),
        ("MAX_PLAYERNAME_LENGTH", GameConstants.MaxPlayerNameLength),
        ("ASSIST_TIME", GameConstants.AssistTime),
        ("HP_PER_POINT", GameConstants.HpPerPoint),
        ("INTEL_MAX_TIMER", GameConstants.IntelMaxTimer),

        // ⚠️ GameConstants.CellSize is NOT here. It used to be checked against
        // NAV_CELL_SIZE, which exists only in the bot fork — the nav graph is
        // this project's own and stock GG2 has no opinion on its resolution.
        // Nothing in the reference can confirm it, so nothing pretends to.
        ("CHARACTER_ANIMATION_NORMAL", GameConstants.CharacterAnimationNormal),
        ("CHARACTER_ANIMATION_INTEL", GameConstants.CharacterAnimationIntel),
        ("CHARACTER_ANIMATION_LEN", GameConstants.CharacterAnimationLen),
    ];

    private static readonly (string Name, string Value)[] Strings =
    [
        ("GAME_VERSION_STRING", GameConstants.GameVersionString),
        ("GAME_NAME_STRING", GameConstants.GameNameString),
        ("GAME_URL_STRING", GameConstants.GameUrlString),
        ("PROTOCOL_UUID", GameConstants.ProtocolUuid),
        ("GG2_LOBBY_UUID", GameConstants.Gg2LobbyUuid),
        ("LOBBY_SERVER_HOST", GameConstants.LobbyServerHost),
        ("AUTH_SERVER_HOST", GameConstants.AuthServerHost),
    ];

    /// <summary>
    /// Names this port deliberately does not carry, with the reason. Kept
    /// explicit so the coverage test stays meaningful: a new constant in the
    /// reference should make it fail, not slip through a wildcard.
    /// </summary>
    private static readonly Dictionary<string, string> NotCarried = new(StringComparer.Ordinal)
    {
        // Client-side presentation and updater plumbing. A headless server has
        // no menus, no music, no particles and does not self-update.
        ["PLUGIN_SOURCE"] = "client plugin downloader",
        ["PLUGIN_SOURCE_NOTICE"] = "client plugin downloader",
        ["BACKGROUND_SOURCE"] = "client UI",
        ["DEVMESSAGES_SOURCE"] = "client UI",
        ["DEVMESSAGES_SOURCE_BETA"] = "client UI",
        ["UPDATE_SOURCE"] = "client updater",
        ["UPDATE_SOURCE_BETA"] = "client updater",
        ["PARTICLES_NORMAL"] = "client rendering",
        ["PARTICLES_OFF"] = "client rendering",
        ["PARTICLES_ALTERNATIVE"] = "client rendering",
        ["MOUSE_LEFT"] = "client input",
        ["MOUSE_RIGHT"] = "client input",
        ["MUSIC_MENU_ONLY"] = "client audio",
        ["MUSIC_BOTH"] = "client audio",
        ["MUSIC_INGAME_ONLY"] = "client audio",
        ["MUSIC_NONE"] = "client audio",
        ["BOOT_BEHAVIOUR"] = "GM8 boot mode",
        ["BOOT_STANDARD"] = "GM8 boot mode",
        ["BOOT_DEBUG"] = "GM8 boot mode",

        // Notices are HUD text ids the server never originates.
        ["NOTICE_NUTSNBOLTS"] = "client HUD",
        ["NOTICE_TOOCLOSE"] = "client HUD",
        ["NOTICE_AUTOGUNSCRAPPED"] = "client HUD",
        ["NOTICE_HAVEINTEL"] = "client HUD",
        ["NOTICE_SETCHECKPOINT"] = "client HUD",
        ["NOTICE_DESTROYCHECKPOINT"] = "client HUD",
        ["NOTICE_CUSTOM"] = "client HUD",

        // Cosmetic gear, granted by the reward server this port never talks to.
        ["GEAR_PARTY_HAT"] = "rewards, out of scope",
        ["GEAR_CROWN"] = "rewards, out of scope",
        ["GEAR_NAVIGATORHAT"] = "rewards, out of scope",
        ["GEAR_RAYBANN_CAPE"] = "rewards, out of scope",
        ["GEAR_HAXXY_CAPE"] = "rewards, out of scope",

        // ini-parsing value kinds, an artefact of GML having no type system.
        ["STRING"] = "GML ini parser",
        ["REAL"] = "GML ini parser",
        ["BOOL"] = "GML ini parser",
        ["HEX"] = "GML ini parser",
    };

    private static readonly ReferenceConstants Reference = ReferenceConstants.Load();

    [Fact]
    public void ProtocolUuidBytesMatchTheTextualForm()
    {
        var expected = Convert.FromHexString(GameConstants.ProtocolUuid.Replace("-", string.Empty));

        // parseUuid.gml strips hyphens and writes the 16 bytes in textual
        // order. Get this wrong and no stock client ever connects.
        Assert.Equal(expected, GameConstants.ProtocolUuidBytes.ToArray());
        Assert.Equal(16, GameConstants.ProtocolUuidBytes.Length);
    }

    [Fact]
    public void KeyBitsCoverExactlyWhatTheReferenceAllocates()
    {
        // 0x04 is unallocated and must stay that way.
        var allocated = (byte)(PlayerKeys.Taunt | PlayerKeys.Down | PlayerKeys.Special |
                               PlayerKeys.Attack | PlayerKeys.Right | PlayerKeys.Left | PlayerKeys.Jump);

        Assert.Equal(0xFB, allocated);
    }

    [Fact]
    public void StateUpdateIsAnAliasOfFullUpdate() =>
        Assert.Equal((byte)MessageType.FullUpdate, GameConstants.StateUpdate);

    [Fact]
    public void KickReasonsShareTheMessageIdSpace()
    {
        Assert.Equal((byte)MessageType.KickName, (byte)KickReason.Name);
        Assert.Equal((byte)MessageType.KickBadPluginPacket, (byte)KickReason.BadPluginPacket);
        Assert.Equal((byte)MessageType.KickMultiClient, (byte)KickReason.MultiClient);
    }

    [Fact]
    public void MessageIdsMatchTheReference()
    {
        foreach (var (name, value) in MessageIds)
            Assert.Equal(Reference.Number(name), (byte)value);
    }

    [Fact]
    public void TeamAndClassIdsMatchTheReference()
    {
        foreach (var team in Enum.GetValues<Team>())
            Assert.Equal(Reference.Number("TEAM_" + Enum.GetName(team)!.ToUpperInvariant()), (byte)team);

        foreach (var playerClass in Enum.GetValues<PlayerClass>())
            Assert.Equal(Reference.Number("CLASS_" + Enum.GetName(playerClass)!.ToUpperInvariant()), (byte)playerClass);

        foreach (var stat in Enum.GetValues<StatIndex>())
            Assert.Equal(Reference.Number(Enum.GetName(stat)!.ToUpperInvariant()), (int)stat);
    }

    /// <summary>
    /// The seven input bits, against the place stock GG2 actually assembles
    /// them.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Read out of an object event, not out of <c>Constants.xml</c>.</b>
    /// Stock GG2 has no <c>KEY_*</c> constants at all — <c>PlayerControl</c>'s
    /// Begin Step ors literal <c>$XX</c> masks into <c>keybyte</c>, and those
    /// literals are the whole definition. The named constants this test used to
    /// read were added by a fork, so pointing <c>GG2_REFERENCE</c> at an upstream
    /// checkout turned the strongest check on the input byte into a
    /// "Constants.xml has no numeric constant named KEY_JUMP" failure.
    /// <para>
    /// These bits are <b>observable wire</b>: every <c>INPUTSTATE</c> a client
    /// sends is one byte of them, so this is not a check worth losing to make a
    /// path resolve.
    /// </para>
    /// </remarks>
    [Fact]
    public void KeyBitsMatchTheReference()
    {
        var masks = ReferenceKeyBits();

        foreach (var key in Enum.GetValues<PlayerKeys>())
        {
            if (key == PlayerKeys.None)
                continue; // Not a bit: 0 is the absence of all of them.

            var name = Enum.GetName(key)!;
            Assert.True(masks.ContainsKey(name), $"PlayerControl's Begin Step ors no mask for {name}");
            Assert.Equal(masks[name], (byte)key);
        }
    }

    [Fact]
    public void ScalarConstantsMatchTheReference()
    {
        foreach (var (name, value) in Scalars)
            Assert.Equal(Reference.Number(name), value);
    }

    [Fact]
    public void StringConstantsMatchTheReference()
    {
        foreach (var (name, value) in Strings)
            Assert.Equal(Reference.Text(name), value);
    }

    /// <summary>
    /// Nothing in <c>Constants.xml</c> is silently missing: every name is either
    /// carried somewhere in <c>Gg2.Protocol</c> or listed in
    /// <see cref="NotCarried"/> with a reason.
    /// </summary>
    [Fact]
    public void EveryReferenceConstantIsCarriedOrExplained()
    {
        var carried = CarriedNames();

        var missing = Reference.Names
            .Where(name => !carried.Contains(name) && !NotCarried.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void NothingIsExcludedThatTheReferenceNoLongerDefines()
    {
        var stale = NotCarried.Keys.Where(name => !Reference.Names.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(stale);
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// The <c>keybyte |= $XX</c> lines of <c>PlayerControl</c>'s Begin Step,
    /// keyed by the <see cref="PlayerKeys"/> member each one is.
    /// </summary>
    /// <remarks>
    /// Each line names the setting it reads before it ors its mask —
    /// <c>keyboard_check(global.left) || keyboard_check(global.left2)) keybyte |=
    /// $40;</c> — so the first <c>global.</c> identifier on the line, with any
    /// trailing alternate-binding digit dropped, is the key's name.
    /// </remarks>
    private static Dictionary<string, byte> ReferenceKeyBits()
    {
        ReferenceTree.RequireAvailable();

        var path = Path.Combine(
            ReferenceTree.Root, "Source", "gg2", "Objects", "InGameElements",
            "PlayerControl.events", "Begin Step.xml");

        Assert.True(File.Exists(path), $"the reference tree has no PlayerControl Begin Step at {path}");

        // Object event code is XML-escaped inside the .xml (CLAUDE.md, "Reading
        // the reference"), so `||` survives but `&&` and comparisons do not.
        var source = File.ReadAllText(path)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

        var bits = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(
                     source,
                     @"global\.(?<key>[A-Za-z]+)\d?\b[^\r\n]*?keybyte\s*\|=\s*\$(?<mask>[0-9A-Fa-f]+)"))
        {
            bits[match.Groups["key"].Value] = Convert.ToByte(match.Groups["mask"].Value, 16);
        }

        // Seven allocated bits; $04 is unallocated. A regex that silently
        // matched fewer would make the assertions below vacuous.
        Assert.Equal(7, bits.Count);

        return bits;
    }

    private static HashSet<string> CarriedNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, _) in MessageIds)
            names.Add(name);

        foreach (var (name, _) in Scalars)
            names.Add(name);

        foreach (var (name, _) in Strings)
            names.Add(name);

        foreach (var team in Enum.GetValues<Team>())
            names.Add("TEAM_" + Enum.GetName(team)!.ToUpperInvariant());

        foreach (var playerClass in Enum.GetValues<PlayerClass>())
            names.Add("CLASS_" + Enum.GetName(playerClass)!.ToUpperInvariant());

        // No KEY_* loop: stock GG2 has no such constants. KeyBitsMatchTheReference
        // reads the masks out of PlayerControl's Begin Step instead.

        foreach (var stat in Enum.GetValues<StatIndex>())
            names.Add(Enum.GetName(stat)!.ToUpperInvariant());

        return names;
    }

    /// <summary>The reference's own constant table, read straight out of the XML.</summary>
    private sealed class ReferenceConstants
    {
        private readonly Dictionary<string, double> _numbers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Names => _names;

        public double Number(string name)
        {
            Assert.True(_numbers.ContainsKey(name), $"Constants.xml has no numeric constant named {name}");
            return _numbers[name];
        }

        public string Text(string name)
        {
            Assert.True(_strings.ContainsKey(name), $"Constants.xml has no string constant named {name}");
            return _strings[name];
        }

        public static ReferenceConstants Load()
        {
            ReferenceTree.RequireAvailable();

            var path = Path.Combine(ReferenceTree.Root, "Source", "gg2", "Constants.xml");
            Assert.True(File.Exists(path), $"the reference tree has no Constants.xml at {path}");

            var table = new ReferenceConstants();

            foreach (var element in XDocument.Load(path).Root!.Elements("constant"))
            {
                var name = element.Attribute("name")!.Value;
                var text = element.Attribute("value")!.Value;
                table._names.Add(name);

                // A GML string constant is written with its quotes included.
                if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                {
                    table._strings[name] = text[1..^1];
                    continue;
                }

                // GML hex literals are $-prefixed.
                if (text.StartsWith('$'))
                {
                    table._numbers[name] = int.Parse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    continue;
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    table._numbers[name] = value;
            }

            return table;
        }
    }
}
