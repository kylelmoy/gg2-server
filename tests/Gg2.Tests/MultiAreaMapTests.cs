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

            // ⚠️ Waits for the change to COMPLETE, not merely to be announced.
            // CurrentMapArea moves the moment the countdown opens — that is the
            // reference's timing and what a joiner in the window is told — so
            // testing it alone would return on the same tick as the request and
            // assert TotalMapAreas against the map the server had not left yet.
            // NextMap is non-empty exactly while a change is pending.
            harness.TickUntil(
                () => string.IsNullOrEmpty(harness.Server.NextMap)
                      && harness.Server.CurrentMapArea == area,
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
    /// A client that joins inside the ten-second countdown is told the area it
    /// is about to load, not the one being played out.
    /// </summary>
    /// <remarks>
    /// <b>The reference moves <c>global.currentMapArea</c> at MAP_END time</b>,
    /// ten seconds before the change itself:
    /// <c>GameServerBeginStep.gml:74-92</c> assigns it (<c>+= 1</c> on an
    /// advance, <c>= 1</c> on a rotation) and only then writes the MAP_END that
    /// carries it. So <c>ServerJoinUpdate.gml:6</c>, which reads the same global,
    /// hands a joiner in that window the <em>next</em> area — and it has to,
    /// because MAP_END is the only other place the wire ever states the area and
    /// this joiner missed it. Its <c>global.currentMapArea</c> is frozen at
    /// whatever the JOIN_UPDATE said for as long as it stays connected.
    /// <para>
    /// Two things read it on the client and both are wrong if it is stale:
    /// <c>basicRoomSetup.gml:16-25</c> narrows the map to the wrong area's
    /// entities, and <c>ClientBeginStep.gml:511-544</c> wipes every team to
    /// <c>TEAM_SPECTATOR</c> on <c>global.currentMapArea == 1</c> — a rule the
    /// server applies to the area it is going TO. Stale at 1 across an advance,
    /// the joiner wipes teams the server keeps; it then reads a character for a
    /// slot it holds as a spectator and <c>Character</c>'s Create kills it with
    /// <c>unknown team ID: 2</c>. That is the open <c>cp_dirtbowl</c> fatal's
    /// shape exactly, and <see cref="MirrorClient.Impossible"/> asks the same
    /// question the client's own code does.
    /// </para>
    /// <para>
    /// The incumbent is not decoration: without a player who is on a team and
    /// holds a character, the wipe has nothing to disagree about and the test
    /// goes vacuously green.
    /// </para>
    /// </remarks>
    [Fact]
    public void AJoinerInTheMapChangeCountdownIsToldTheAreaItIsAboutToLoad()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
        });

        var incumbent = harness.JoinClient("incumbent");
        using var incumbentMirror = new MirrorClient(incumbent, Load, Map);

        harness.Tick(10);
        incumbent.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () =>
            {
                incumbentMirror.Pump();
                return harness.Server.TeamOf(harness.Server.RosterCount - 1) == Team.Red;
            },
            "the incumbent to reach red");

        // The advance, and then a join INSIDE the countdown it opened.
        Assert.True(harness.Server.RequestMapChange(Map, 2), "the change to area 2 was refused");

        var latecomer = harness.JoinClient("latecomer");
        using var mirror = new MirrorClient(latecomer, Load, Map);
        mirror.Pump();

        // NextMap is empty unless a change is pending, so this is the guard that
        // stops the test going vacuous if the join ever grows slower than the
        // ten-second countdown.
        Assert.False(
            string.IsNullOrEmpty(harness.Server.NextMap),
            "the join outlasted the countdown, so this test never reached the window it is about");

        Assert.Equal(2, mirror.CurrentMapArea);

        // Through the change, and far enough past it for the incumbent to
        // respawn — a character for a slot the joiner wiped to spectator is the
        // crash itself.
        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                incumbentMirror.Pump();
                return mirror.Of(MessageType.ChangeMap).Count > 1;
            },
            "the area change to reach the latecomer",
            maxTicks: Rounds.MapChangeDelay + 200);

        for (var i = 0; i < 200; i++)
        {
            harness.Tick(1);
            mirror.Pump();
            incumbentMirror.Pump();
        }

        Assert.Empty(mirror.Impossible);
        Assert.Empty(incumbentMirror.Impossible);

        // The area advance keeps teams, on the server and on both clients.
        Assert.Equal(Team.Red, harness.Server.TeamOf(1));
        Assert.Equal(Team.Red, mirror.Teams[1]);
        Assert.Equal(Team.Red, incumbentMirror.Teams[1]);
    }

    /// <summary>
    /// The same rule down the ROUND-END door, and in the other direction: a
    /// rotation off area 3 tells a joiner in the countdown area 1.
    /// </summary>
    /// <remarks>
    /// <b>Two doors open the countdown</b> — <see cref="GameServer.RequestMapChange"/>
    /// (the control port and the web admin) and <see cref="GameServer.ServeRoundEnd"/>
    /// (a round ending, which is the one that fires in production) — and they
    /// went through separate bodies until 2026-09-05. A gate on one says nothing
    /// about the other, and this is the half a real server actually reaches.
    /// <para>
    /// It also runs the wipe the other way round. A rotation resets
    /// <c>global.currentMapArea</c> to 1, so the server drops everyone to
    /// spectator (<c>GameServerBeginStep.gml:107-138</c>) and a client told 1
    /// does the same to itself. Told a stale 3, the joiner would <em>keep</em>
    /// the teams the server has just cleared — the inverse disagreement, which
    /// puts a spectator on a team in the scoreboard rather than killing the
    /// client, and would survive until each player happened to re-pick.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARoundEndingInTheCountdownTellsAJoinerTheAreaItRotatesTo()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.MapRotation = [Map];
            options.ShuffleRotation = ShuffleMode.None;
        });

        // Onto the last area, so "the area being played" and "the area a
        // rotation goes to" are different numbers. Without this the test cannot
        // tell a stale value from a correct one.
        Assert.True(harness.Server.RequestMapChange(Map, 3), "the change to area 3 was refused");
        harness.TickUntil(
            () => string.IsNullOrEmpty(harness.Server.NextMap)
                  && harness.Server.CurrentMapArea == 3,
            "the server to reach area 3",
            maxTicks: 600);

        var incumbent = harness.JoinClient("incumbent");
        using var incumbentMirror = new MirrorClient(incumbent, Load, Map);
        harness.Tick(10);
        incumbent.SendChangeTeam(Team.Blue);
        harness.TickUntil(
            () =>
            {
                incumbentMirror.Pump();
                return harness.Server.TeamOf(harness.Server.RosterCount - 1) == Team.Blue;
            },
            "the incumbent to reach blue");

        // The production door: a decided round, picked up by ServeRoundEnd on
        // the next tick. Nobody wins, so it rotates rather than advancing.
        Assert.True(harness.Server.RequestRoundEnd(), "the round end was refused");
        harness.Tick(2);

        var latecomer = harness.JoinClient("latecomer");
        using var mirror = new MirrorClient(latecomer, Load, Map);
        mirror.Pump();

        Assert.False(
            string.IsNullOrEmpty(harness.Server.NextMap),
            "the join outlasted the countdown, so this test never reached the window it is about");

        Assert.Equal(1, mirror.CurrentMapArea);

        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                incumbentMirror.Pump();
                return mirror.Of(MessageType.ChangeMap).Count > 1;
            },
            "the rotation to reach the latecomer",
            maxTicks: Rounds.MapChangeDelay + 200);

        for (var i = 0; i < 100; i++)
        {
            harness.Tick(1);
            mirror.Pump();
            incumbentMirror.Pump();
        }

        Assert.Empty(mirror.Impossible);
        Assert.Empty(incumbentMirror.Impossible);

        // A rotation wipes teams, and both clients have to have done it too.
        Assert.Equal(Team.Spectator, harness.Server.TeamOf(1));
        Assert.Equal(Team.Spectator, mirror.Teams[1]);
        Assert.Equal(Team.Spectator, incumbentMirror.Teams[1]);
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
