using System.Linq;
using GG2.Maps;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// The recording that reaches <c>ARENA_WAIT_FOR_PLAYERS</c> — the last gameplay
/// message no fixture contained.
/// </summary>
/// <remarks>
/// The recording: three soldiers a side on <c>arena_lumberyard</c>, with blue
/// removed while the scoreboard was up.
/// <para>
/// <b>The recipe comes from the branch itself.</b>
/// <c>ArenaHUD.events/Step.xml</c> decides what happens when a round's
/// scoreboard runs out (<c>state == ARENA_STATE_ROUND_END</c> and
/// <c>endCount &lt;= 0</c>) in a fixed order: the cap limit, then the clock, then
/// <c>redteam == 0 or blueteam == 0</c> → wait for players, and only otherwise
/// <c>serverArenaRestart()</c>. So the message needs <b>a round to END with one
/// team having no PLAYERS</b> — not merely no living characters, which is what
/// wiping a team out gives and what every earlier arena round produced.
/// </para>
/// <para>
/// Removing blue's bots during the scoreboard is what makes the fourth branch
/// reachable, and it is the reference's own path: a server whose blue team all
/// disconnect between rounds takes exactly this one.
/// </para>
/// </remarks>
public sealed class ArenaWaitFixtureTests
{
    private const string Fixture = "reference-arena_lumberyard-waitforplayers.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");
        return (trace, ticks);
    }

    /// <summary>Every message of one type, with the tick it landed on.</summary>
    private static (int Tick, ServerMessage Message)[] All(
        IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks
            .SelectMany(t => t.Events.Where(m => m.Type == type).Select(m => (t.Index, m)))
            .ToArray();

    /// <summary>
    /// <b>It holds an <c>ARENA_WAIT_FOR_PLAYERS</c>, and the message really is
    /// bodyless.</b>
    /// </summary>
    /// <remarks>
    /// docs/PROTOCOL.md says <c>ARENA_RESTART</c>, <c>ARENA_STARTROUND</c> and
    /// <c>ARENA_WAIT_FOR_PLAYERS</c> "really are bodyless", against
    /// <c>ARENA_ENDROUND</c>, which is not and was framed as though it were for a
    /// phase. Two of the three had a recording behind that claim; this is the
    /// third, and its sender is one line —
    /// <c>write_ubyte(global.sendBuffer, ARENA_WAIT_FOR_PLAYERS);</c>.
    /// </remarks>
    [Fact]
    public void ItHoldsABodylessArenaWaitForPlayers()
    {
        var (_, ticks) = Load();

        var waits = All(ticks, MessageType.ArenaWaitForPlayers);

        Assert.Single(waits);
        Assert.Empty(waits[0].Message.Payload);

        // And the two bodyless siblings, from the same recording.
        Assert.All(All(ticks, MessageType.ArenaRestart), m => Assert.Empty(m.Message.Payload));
        Assert.All(All(ticks, MessageType.ArenaStartRound), m => Assert.Empty(m.Message.Payload));

        // The control: ARENA_ENDROUND, in the same stream, is not bodyless.
        Assert.All(All(ticks, MessageType.ArenaEndRound), m => Assert.True(m.Message.Payload.Length >= 5));
    }

    /// <summary>
    /// <b>The gate: the last round ended into a WAIT rather than a
    /// RESTART.</b>
    /// </summary>
    /// <remarks>
    /// This is what makes the fixture about the branch and not merely about the
    /// message existing. Every other round in this recording — and every round in
    /// <c>reference-arena_lumberyard-closedarena.trace</c> — ends into
    /// <c>serverArenaRestart()</c>, because both teams still have players even
    /// when one has been wiped out. Here the last <c>ARENA_ENDROUND</c> is
    /// followed by the wait and by no restart at all.
    /// <para>
    /// The <c>PLAYER_LEAVE</c>s are the cause and are asserted with it: without
    /// them the branch is unreachable, so a re-recording that lost them would
    /// quietly go back to restarting.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLastRoundEndsIntoAWaitRatherThanARestart()
    {
        var (_, ticks) = Load();

        var lastEnd = All(ticks, MessageType.ArenaEndRound).Max(m => m.Tick);
        var wait = All(ticks, MessageType.ArenaWaitForPlayers).Single().Tick;

        Assert.True(wait > lastEnd, $"the wait at t{wait} is not after the last round end at t{lastEnd}");

        // The branch is exclusive — `else serverArenaRestart()` — so nothing
        // may restart between them, and nothing does afterwards either while the
        // team stays empty.
        var restartsAfter = All(ticks, MessageType.ArenaRestart).Where(m => m.Tick > lastEnd).ToArray();

        Assert.True(
            restartsAfter.Length == 0,
            $"a restart at t{string.Join(",", restartsAfter.Select(m => m.Tick))} followed the last round end; "
                + "the wait branch and the restart branch are mutually exclusive");

        // The cause: a whole team left during the scoreboard.
        var leavesBefore = All(ticks, MessageType.PlayerLeave).Count(m => m.Tick <= wait && m.Tick >= lastEnd - 400);

        Assert.True(leavesBefore >= 3, $"only {leavesBefore} player(s) left before the wait; the branch needs a team to empty");
    }
}
