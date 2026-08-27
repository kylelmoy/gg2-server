using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// Which of the protocol's message types the whole fixture corpus actually
/// reaches — and, just as usefully, which it does not and why.
/// </summary>
/// <remarks>
/// ⚠️ <b>"What state can no recording reach?" is the question that has found
/// every framing bug in this project</b> — the sentry <c>INPUTSTATE</c> block,
/// <c>ARENA_ENDROUND</c>, the generator HUD block and <c>BALANCE</c> were all
/// wrong for phases and all survived because nothing recorded them. This makes
/// the question mechanical rather than a matter of memory.
/// <para>
/// <b>Of the 27 types no fixture contains, four are dead protocol</b>:
/// <c>RED_TEAM_CAP</c> (12), <c>BLUE_TEAM_CAP</c> (13), <c>CP_CAPTURED</c> (30)
/// and <c>SERVER_KICK</c> (37) are declared in the reference's
/// <c>Constants.xml</c> and referenced <b>nowhere</b> in its scripts or object
/// events. They cannot appear on any wire, and no recording will ever reach
/// them. Measured 2026-08-27 by grepping the reference tree.
/// </para>
/// <para>
/// Most of the rest are structural: the handshake (<c>HELLO</c>,
/// <c>RESERVE_SLOT</c>, <c>PASSWORD_REQUEST</c>, <c>PASSWORD_WRONG</c>,
/// <c>INCOMPATIBLE_PROTOCOL</c>, <c>SERVER_FULL</c>, <c>DOWNLOAD_MAP</c>) happens
/// before <c>gg2-parity record</c> starts writing ticks; the kick, plugin and
/// reward families need an operator, a plugin or the medal server.
/// </para>
/// <para>
/// ⚠️ <b>Five are reachable and simply unreached, and they are the candidates
/// for the next fixture</b>: <c>DROP_INTEL</c> (an ubered carrier dropping, or a
/// client's own drop command — a death-caused drop broadcasts nothing),
/// <c>ARENA_WAIT_FOR_PLAYERS</c>, <c>CHAT_BUBBLE</c>, <c>MESSAGE_STRING</c> and
/// <c>PLAYER_CHANGENAME</c>. Only the first two are gameplay.
/// </para>
/// </remarks>
public sealed class MessageCoverageTests
{
    /// <summary>
    /// Every message type at least one checked-in recording contains, measured
    /// 2026-08-27 over 27 fixtures.
    /// </summary>
    private static readonly MessageType[] Reached =
    [
        MessageType.PlayerJoin,
        MessageType.PlayerLeave,
        MessageType.PlayerChangeTeam,
        MessageType.PlayerChangeClass,
        MessageType.PlayerSpawn,
        MessageType.InputState,
        MessageType.ChangeMap,
        MessageType.FullUpdate,
        MessageType.QuickUpdate,
        MessageType.PlayerDeath,
        MessageType.MapEnd,
        MessageType.BuildSentry,
        MessageType.DestroySentry,
        MessageType.Balance,
        MessageType.GrabIntel,
        MessageType.DropIntel,
        MessageType.ScoreIntel,
        MessageType.UberCharged,
        MessageType.Uber,
        MessageType.Omnomnomnom,
        MessageType.CapsUpdate,
        MessageType.GeneratorDestroy,
        MessageType.ArenaEndRound,
        MessageType.ArenaRestart,
        MessageType.ArenaWaitForPlayers,
        MessageType.UnlockCp,
        MessageType.ArenaStartRound,
        MessageType.ToggleZoom,
        MessageType.ReturnIntel,
        MessageType.JoinUpdate,
        MessageType.SentryPosition,
        MessageType.WeaponFire,
    ];

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static WorldShape ShapeOf(string map) => GameModes.ShapeOf(MapLoader(map));

    private static HashSet<MessageType> Observed()
    {
        var seen = new HashSet<MessageType>();

        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.trace"))
        {
            var trace = Trace.Decode(File.ReadAllBytes(path));
            var ticks = TraceParser.Parse(trace, ShapeOf(trace.Header.Map), out _, out var stopped, ShapeOf);

            Assert.True(stopped is null, $"{Path.GetFileName(path)}: {stopped}");

            foreach (var tick in ticks)
            {
                seen.Add(tick.Tier);

                foreach (var message in tick.Events)
                    seen.Add(message.Type);
            }
        }

        return seen;
    }

    /// <summary>
    /// The corpus reaches exactly the message types it is recorded as reaching.
    /// </summary>
    /// <remarks>
    /// Both directions are asserted on purpose.
    /// <list type="bullet">
    /// <item><b>Something new appearing is good news</b> and this is where it
    /// gets noticed: add it to the list, and ask what that message's framing
    /// depends on — a length that varies with content is the family that has
    /// produced four bugs here.</item>
    /// <item><b>Something disappearing is a warning.</b> Several of these are
    /// held by exactly one fixture — <c>Balance</c>, <c>ArenaEndRound</c>,
    /// <c>GeneratorDestroy</c>, <c>UnlockCp</c>, <c>Uber</c> — so a re-recording
    /// that came out quiet would silently stop exercising a whole message
    /// family.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void TheFixtureCorpusReachesExactlyTheseMessageTypes()
    {
        var observed = Observed();

        var appeared = observed.Except(Reached).OrderBy(t => (int)t).ToArray();
        var vanished = Reached.Except(observed).OrderBy(t => (int)t).ToArray();

        Assert.True(
            appeared.Length == 0,
            "a fixture now reaches message type(s) no recording used to: "
                + string.Join(", ", appeared.Select(t => $"{(int)t}:{t}"))
                + " — good news. Add them here, and ask what makes each one's length change.");

        Assert.True(
            vanished.Length == 0,
            "no fixture reaches these any more: "
                + string.Join(", ", vanished.Select(t => $"{(int)t}:{t}"))
                + " — a recording that used to exercise a message family has stopped.");
    }

    /// <summary>
    /// ⚠️ <b>The four dead protocol constants stay unreachable.</b>
    /// </summary>
    /// <remarks>
    /// <c>RED_TEAM_CAP</c>, <c>BLUE_TEAM_CAP</c>, <c>CP_CAPTURED</c> and
    /// <c>SERVER_KICK</c> exist in <c>Constants.xml</c> and in nothing else the
    /// reference ships — no script, no object event. <b>They are not "unreached
    /// yet"; they cannot occur.</b> Worth an assertion of its own so that a
    /// future session hunting for full message coverage does not spend a day
    /// trying to record one, and so that a fixture somehow producing one is
    /// treated as the anomaly it would be.
    /// <para>
    /// ⚠️ <c>CP_CAPTURED</c> is the one that looks most like an oversight: this
    /// corpus captures control points on <c>koth_valley</c>, <c>cp_egypt</c> and
    /// <c>dkoth_atalia</c> and none of them sends it. A capture reaches the wire
    /// as a <c>CAPS_UPDATE</c> and nothing else.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDeadProtocolConstantsAreNeverOnTheWire()
    {
        var observed = Observed();

        MessageType[] dead =
        [
            MessageType.RedTeamCap,
            MessageType.BlueTeamCap,
            MessageType.CpCaptured,
            MessageType.ServerKick,
        ];

        foreach (var type in dead)
        {
            Assert.False(
                observed.Contains(type),
                $"{(int)type}:{type} appeared in a recording — the reference references it nowhere "
                + "outside Constants.xml, so either that reading was wrong or the fork has changed");
        }
    }
}
