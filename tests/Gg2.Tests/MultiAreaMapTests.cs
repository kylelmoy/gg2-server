using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A multi-area map — pushing through <c>cp_dirtbowl</c>'s three stages —
/// keeps the client and the server in agreement.
/// </summary>
/// <remarks>
/// <b>An area advance is NOT a rotation, and almost nothing about it is
/// shared.</b> <c>GameServerBeginStep.gml</c>: red winning on a map with areas
/// left replays the <em>same</em> map at the next area instead of rotating, and
/// the reset is then run with <c>firstArea: false</c> — so teams and the
/// scoreboard <b>survive</b>, where a rotation wipes both. The client does the
/// same, gated on <c>global.currentMapArea == 1</c>
/// (<c>ClientBeginStep.gml:514-544</c>). Two independent implementations of "did
/// the area change", and nothing was checking they agreed.
/// <para>
/// <b>Nothing in this project had ever advanced an area.</b>
/// <c>ServerStreamTests</c> asserted <c>CurrentMapArea == 1</c> at rest and that
/// was the whole of it — no test, no fixture and no recording ever reached area
/// 2, on the one shipped map that has one.
/// </para>
/// <para>
/// <b>And the reader was wrong because of it.</b> <c>ServerStream</c> skipped
/// the area byte in <c>MAP_END</c>, which is the only place the wire ever states
/// it after a join — <c>CHANGE_MAP</c> carries just the map name. So a reader sat
/// on area 1 for ever, narrowed entities with the wrong <c>ForArea</c>, and wiped
/// every team on an advance that keeps them.
/// </para>
/// </remarks>
public sealed class MultiAreaMapTests
{
    /// <summary>
    /// The one shipped map with more than one area.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed below: if <c>cp_dirtbowl</c> ever stopped
    /// being multi-area, every test in this file would still pass while checking
    /// nothing at all.
    /// </remarks>
    private const string Map = "cp_dirtbowl";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    [Fact]
    public void DirtbowlReallyHasThreeAreas()
    {
        Assert.Equal(3, GameModes.TotalAreas(Load(Map)));
    }

    /// <summary>
    /// The served area count is the MAP's, on every area — never the slice's.
    /// </summary>
    /// <remarks>
    /// <c>basicRoomSetup.gml:7</c> sets <c>global.totalMapAreas =
    /// 1+instance_number(NextAreaO)</c> <em>before</em> the loop at <c>:9-25</c>
    /// destroys the other areas' entities, so the reference's count is the whole
    /// map's and does not depend on which area is being played.
    /// <para>
    /// <b>Counting the narrowed map instead is the bug this pins</b>, and it is
    /// not hypothetical: <c>OpenMap</c> did exactly that, giving 2/3/2 for areas
    /// 1/2/3 against the reference's 3/3/3. It was invisible because those three
    /// wrong values happen to decide all three of <c>cp_dirtbowl</c>'s advances
    /// the same way as the right ones —
    /// <c>GameServerBeginStep.gml:74</c>'s <c>currentMapArea &lt;
    /// totalMapAreas</c> is 1&lt;2, 2&lt;3, 3&lt;2 instead of 1&lt;3, 2&lt;3,
    /// 3&lt;3 — so no behavioural test could see it on the only shipped
    /// multi-area map. A custom four-area map has no such guarantee.
    /// </para>
    /// <para>
    /// The slice counts are asserted too, so this fails if someone "simplifies"
    /// the fix away by making <c>ForArea</c> keep every marker: that would make
    /// the two quantities equal and this test vacuous.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheServedAreaCountIsTheMapsNotTheSlices()
    {
        var full = Load(Map);
        Assert.Equal(3, GameModes.TotalAreas(full));

        // The slice really is a different quantity — otherwise the rest is vacuous.
        Assert.Equal([2, 3, 2],
            new[] { 1, 2, 3 }.Select(a => GameModes.TotalAreas(full.ForArea(a))).ToArray());

        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
        });

        // Area 1 is where the server opened, so it is asserted in place; asking
        // for the area it is already on would leave a map change pending and get
        // the next request refused.
        Assert.Equal(1, harness.Server.CurrentMapArea);
        Assert.Equal(3, harness.Server.TotalMapAreas);

        for (var area = 2; area <= 3; area++)
        {
            Assert.True(
                harness.Server.RequestMapChange(Map, area),
                $"the change to area {area} was refused");

            harness.TickUntil(
                () => harness.Server.CurrentMapArea == area,
                $"the server to reach area {area}",
                maxTicks: 600);

            Assert.Equal(3, harness.Server.TotalMapAreas);
        }
    }

    /// <summary>
    /// Pushing through every area keeps the roster, the teams and the stream.
    /// </summary>
    /// <remarks>
    /// The area is driven directly rather than by winning a round, because what
    /// is under test is the <em>transition</em> — the messages and the reload —
    /// not the control-point simulation that decides to make it.
    /// <see cref="ADefenderWinRotatesRatherThanAdvancing"/> covers the half of
    /// the decision a test can reach.
    /// </remarks>
    [Fact]
    public void PushingThroughEveryAreaKeepsTheClientInAgreement()
    {
        var areas = GameModes.TotalAreas(Load(Map));
        Assert.True(areas > 1, $"{Map} is not multi-area, so this test checks nothing");

        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
        });

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, Map);

        harness.Tick(10);
        client.SendChangeTeam(Team.Red);
        harness.Tick(10);

        mirror.Pump();
        Assert.Equal(Team.Red, mirror.Teams[^1]);

        for (var area = 2; area <= areas; area++)
        {
            Assert.True(
                harness.Server.RequestMapChange(Map, area),
                $"the change to area {area} was refused");

            harness.TickUntil(
                () =>
                {
                    mirror.Pump();
                    return mirror.CurrentMapArea == area;
                },
                $"the client to be told about area {area}",
                maxTicks: 600);

            // The property that separates an advance from a rotation: the
            // player is STILL on red. A rotation would have made them a
            // spectator, on both sides, and a port that reset here would take
            // everyone back to team select every time red pushed a point.
            mirror.Pump();
            Assert.True(
                mirror.Teams[^1] == Team.Red,
                $"area {area}: the client was taken off its team by an area advance");

            Assert.Equal(Team.Red, harness.Server.TeamOf(harness.Server.RosterCount - 1));

            AssertHudArrives(harness, mirror, $"area {area}");
        }
    }

    /// <summary>
    /// A DEFENDER win rotates instead of advancing, even with areas left.
    /// </summary>
    /// <remarks>
    /// The other half of <c>if(global.winners == TEAM_RED and
    /// global.currentMapArea &lt; global.totalMapAreas)</c>, and the half that is
    /// reachable without forcing anything: on an attack/defend map the clock
    /// running out is a <b>blue</b> win, so the map rotates away rather than
    /// pushing on. A port that advanced on any win would replay dirtbowl three
    /// times whatever happened, and nobody would notice quickly because the
    /// symptom is just "we keep playing this map".
    /// <para>
    /// <b>The red half is verified live rather than here</b> — it needs red to
    /// actually take the points, which is the control-point simulation's job and
    /// is already covered by <c>ControlPointModeTests</c> at the
    /// <c>WorldState</c> level. What is untested by both is only the four-line
    /// branch in <c>ServeRoundEnd</c>, and this pins the side of it that a test
    /// can reach without a production hook.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADefenderWinRotatesRatherThanAdvancing()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map, "ctf_truefort"];
            options.ShuffleRotation = ShuffleMode.None;

            // No TimeLimitMinutes here, and setting one would be misleading:
            // attack/defend runs on its OWN clock. Rounds.AttackDefendTimeLimit
            // is a fixed three minutes and Rounds.Start assigns it over whatever
            // the server was configured with, after 30 seconds of setup. So a
            // defender hold takes 900 + 5400 ticks whatever the operator asked
            // for, and a budget sized from TimeLimitMinutes times out.
        });

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, Map);

        Assert.Equal(1, mirror.CurrentMapArea);

        // Setup (900) + the round (5,400) + the scoreboard countdown (300),
        // with slack.
        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMap == "ctf_truefort";
            },
            "the map to rotate away after the defenders held",
            maxTicks: Rounds.AttackDefendSetupTicks + Rounds.AttackDefendTimeLimit + 600);

        // Rotated, not advanced.
        Assert.Equal(1, mirror.CurrentMapArea);
    }

    /// <summary>
    /// Runs on until a <c>CAPS_UPDATE</c> lands, then checks the roster still
    /// adds up.
    /// </summary>
    /// <remarks>
    /// The HUD block rides on <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c> only, so
    /// without this wait the layout an area advance produces is never written and
    /// the test watches the right transition while checking nothing — the mistake
    /// that let the gamemode-change desync through <c>docs/PARITY.md</c>.
    /// </remarks>
    private static void AssertHudArrives(ServerHarness harness, MirrorClient mirror, string when)
    {
        var before = mirror.Of(MessageType.CapsUpdate).Count;

        for (var i = 0; i < 3700 && mirror.Of(MessageType.CapsUpdate).Count == before; i++)
        {
            harness.Tick(1);
            mirror.Pump();
        }

        Assert.True(
            mirror.Of(MessageType.CapsUpdate).Count > before,
            $"{when}: no CAPS_UPDATE was emitted, so its HUD block was never written");

        Assert.Equal(harness.Server.RosterCount, mirror.PlayerCount);

        for (var slot = 0; slot < harness.Server.RosterCount; slot++)
        {
            Assert.True(
                harness.Server.EntryOf(slot).Name == mirror.Names[slot],
                $"{when}: slot {slot} is '{harness.Server.EntryOf(slot).Name}' on the server "
                + $"and '{mirror.Names[slot]}' to the client — the stream has slipped");

            Assert.True(
                harness.Server.TeamOf(slot) == mirror.Teams[slot],
                $"{when}: slot {slot} is {harness.Server.TeamOf(slot)} on the server "
                + $"and {mirror.Teams[slot]} to the client");
        }
    }
}
