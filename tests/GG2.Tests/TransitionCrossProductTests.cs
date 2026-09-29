using GG2.Maps;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Map changes that land while a player is in the middle of something — the
/// combinations no fixture in the corpus contains and no other test sets up.
/// </summary>
/// <remarks>
/// <b>Both client fatals this project has found were TRANSITION bugs</b> (the
/// stale <c>MatchState</c> HUD block and <c>ServerStream</c>'s skipped
/// <c>MAP_END</c> area byte) — state carried across a boundary, invisible until
/// something on the far side read it. <c>GamemodeRotationTests</c> rotates a
/// <em>quiet</em> client; these rotate a busy one.
/// <para>
/// ⚠️ <b>No trace fixture can reach any of this</b>: no recording in the corpus
/// contains a map rotation at all (<c>docs/PARITY.md</c>, "Fixed: a gamemode
/// change desynchronised every stock client"). The oracle here is the client's
/// own rules, applied by <see cref="MirrorClient"/> — in particular
/// <c>Impossible</c>, which is the in-process twin of the live watchdog latch:
/// a character sent for a slot the client holds as a spectator is what kills a
/// stock client in <c>Character</c>'s Create event.
/// </para>
/// </remarks>
public sealed class TransitionCrossProductTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static ServerHarness Harness() => new(options =>
    {
        options.Map = Map;
        options.MapRotation = [Map, "ctf_2dfort"];
        options.ShuffleRotation = ShuffleMode.None;
    });

    /// <summary>
    /// A map change while a client is CARRYING THE INTEL leaves nobody holding
    /// it and no client told anything it cannot render.
    /// </summary>
    /// <remarks>
    /// The interesting state: the briefcase is attached to a roster slot, and a
    /// rotation destroys every character and puts every player back to
    /// spectator. A carrier that survived the boundary would leave the new
    /// round's intel held by a slot whose character no longer exists — and the
    /// marker drawn on a spectator.
    /// <para>
    /// The pickup is driven by <see cref="GameServer.Teleport"/> onto the
    /// briefcase rather than by walking, because what is under test is the
    /// transition, not the pathing — and a test that had to walk there would be
    /// slow and flaky for no gain.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMapChangeWhileCarryingTheIntelLeavesNobodyHoldingIt()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("carrier");
        using var mirror = new MirrorClient(socket, Load, Map);

        var slot = harness.Server.RosterCount - 1;

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the carrier to spawn",
            maxTicks: 400);

        // Red steals BLUE's briefcase — a team carries the other team's intel.
        var blue = harness.Server.IntelOf(Team.Blue);
        Assert.True(blue.HasBase, $"{Map} places no blue intel, so this test checks nothing");

        harness.TickUntil(
            () =>
            {
                harness.Server.Teleport(slot, blue.HomeX, blue.HomeY);
                return harness.Server.IsCarryingIntel(slot);
            },
            "the carrier to pick the briefcase up",
            maxTicks: 400);

        Assert.Equal(slot, harness.Server.IntelCarrier(Team.Blue));

        mirror.Pump();
        mirror.ClearLog();

        Assert.True(harness.Server.RequestMapChange("ctf_2dfort"), "the map change was refused");
        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMap == "ctf_2dfort";
            },
            "the client to be told about the new map",
            maxTicks: 900);

        harness.Tick(60);
        mirror.Pump();

        // Nobody carries anything across the boundary.
        Assert.Equal(-1, harness.Server.IntelCarrier(Team.Blue));
        Assert.Equal(-1, harness.Server.IntelCarrier(Team.Red));
        Assert.False(harness.Server.IsCarryingIntel(slot));

        // A rotation spectates everybody, on both sides.
        Assert.Equal(Team.Spectator, harness.Server.TeamOf(slot));
        Assert.Equal(Team.Spectator, mirror.Teams[slot]);

        // And the client was never sent a character for a slot it holds as a
        // spectator — the thing that kills a stock client outright.
        Assert.Empty(mirror.Impossible);
    }

    /// <summary>
    /// A respawn already ticking when the map changes spawns NOBODY.
    /// </summary>
    /// <remarks>
    /// <b>This is the sharpest cross-product on the list</b>, because the two
    /// halves fight: a rotation puts every player back to <c>Spectator</c>, and
    /// an armed respawn timer wants to create a character. If the timer wins,
    /// the server sends a character for a slot the client holds as a spectator —
    /// and <c>Character</c>'s Create event calls <c>getCharacterSpriteId</c>,
    /// which accepts team 0 and 1 only and <c>show_error</c>s on 2. That kills
    /// a stock client outright, and it is the same fatal
    /// <c>docs/PARITY.md</c> still has open on <c>cp_dirtbowl</c>.
    /// <para>
    /// <c>PlayerLifecycleTests.ASpectatorsRespawnAlarmSpawnsNobody</c> covers
    /// the guard in isolation; nothing covered it <em>across a rotation</em>,
    /// with a client watching, which is the arrangement that actually occurs.
    /// </para>
    /// <para>
    /// The death is dealt by a team change rather than by damage: an accepted
    /// change kills the character (<c>BID_FAREWELL</c>) and arms
    /// <c>alarm[5]</c>, which is precisely the state wanted and needs no combat
    /// to set up.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARespawnAlreadyTickingWhenTheMapChangesSpawnsNobody()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("respawner");
        using var mirror = new MirrorClient(socket, Load, Map);

        var slot = harness.Server.RosterCount - 1;

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the respawner to spawn",
            maxTicks: 400);

        // An accepted team change kills the character and arms the respawn.
        socket.SendChangeTeam(Team.Blue);
        harness.TickUntil(
            () => !harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the team change to kill the character",
            maxTicks: 200);

        Assert.Equal(Team.Blue, harness.Server.TeamOf(slot));

        mirror.Pump();
        mirror.ClearLog();

        Assert.True(harness.Server.RequestMapChange("ctf_2dfort"), "the map change was refused");
        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMap == "ctf_2dfort";
            },
            "the client to be told about the new map",
            maxTicks: 900);

        // Well past any respawn time, so a timer that survived would have fired.
        harness.Tick(400);
        mirror.Pump();

        Assert.Equal(Team.Spectator, harness.Server.TeamOf(slot));
        Assert.False(
            harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "a spectator was given a character by a respawn that outlived the rotation");
        Assert.Empty(mirror.Impossible);
    }

    /// <summary>
    /// A client that JOINS DURING the ten-second map-change window still ends
    /// up on the new map.
    /// </summary>
    /// <remarks>
    /// <b>The join burst is built from <c>_currentMap</c>, which is still the
    /// OLD map for the whole countdown</b> — <c>OpenMap</c> does not run until
    /// the change lands. So a joiner in that window is told to load the outgoing
    /// map and must then be brought forward by the <c>CHANGE_MAP</c> that
    /// follows. If it is not, the client sits on a map the server is no longer
    /// serving, and every position it is sent afterwards is against the wrong
    /// geometry — which looks like a physics bug and is not one.
    /// <para>
    /// ⚠️ <b>This is the same function that shipped a real bug on 2026-09-05</b>
    /// (the hardcoded empty MD5 that made every custom map unjoinable — see
    /// <c>MapDownloadTests.TheJoinBurstCarriesTheMapHashAndNotAnEmptyOne</c>).
    /// Its other output, the map NAME, is stale by construction during a
    /// rotation, so it is worth a test of its own rather than an assumption.
    /// </para>
    /// <para>
    /// The join is asserted to have actually landed inside the window
    /// (<c>MapChangeCountdown &gt; 0</c>), or this is an ordinary join wearing a
    /// misleading name.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClientJoiningDuringAMapChangeEndsUpOnTheNewMap()
    {
        using var harness = Harness();

        // Somebody has to be watching from before the change, so the roster the
        // joiner lands in is not empty.
        var sitting = harness.JoinClient("sitting");
        harness.Tick(5);

        Assert.True(harness.Server.RequestMapChange("ctf_2dfort"), "the map change was refused");
        Assert.True(harness.Server.MapChangeCountdown > 0, "the countdown did not start");

        // Join while the countdown is still running — the window under test.
        var latecomer = harness.JoinClient("latecomer");
        Assert.True(
            harness.Server.MapChangeCountdown > 0,
            "the join outlasted the countdown, so this is not the case under test");

        using var mirror = new MirrorClient(latecomer, Load, Map);

        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMap == "ctf_2dfort";
            },
            "the latecomer to be brought forward to the new map",
            maxTicks: 900);

        harness.Tick(60);
        mirror.Pump();

        Assert.Equal("ctf_2dfort", mirror.CurrentMap);
        Assert.Equal(harness.Server.RosterCount, mirror.PlayerCount);
        Assert.Empty(mirror.Impossible);
        Assert.False(latecomer.IsClosed);
        Assert.False(sitting.IsClosed);
    }

    /// <summary>
    /// The control: with NO map change, that same pending respawn does fire.
    /// </summary>
    /// <remarks>
    /// Without this,
    /// <see cref="ARespawnAlreadyTickingWhenTheMapChangesSpawnsNobody"/> is
    /// satisfied by a team change that armed no respawn at all — "nobody
    /// spawned" would then be true for an uninteresting reason, and the test
    /// would go on passing if the timer were removed entirely. This proves the
    /// timer was live and would have fired had the rotation not intervened.
    /// </remarks>
    [Fact]
    public void WithoutAMapChangeThatSamePendingRespawnDoesFire()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("respawner");

        var slot = harness.Server.RosterCount - 1;

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the respawner to spawn",
            maxTicks: 400);

        socket.SendChangeTeam(Team.Blue);
        harness.TickUntil(
            () => !harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the team change to kill the character",
            maxTicks: 200);

        // The same 400 ticks the other test waits through, with no rotation.
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the pending respawn to fire",
            maxTicks: 400);

        Assert.Equal(Team.Blue, harness.Server.TeamOf(slot));
    }

    /// <summary>
    /// An AREA ADVANCE mid-taunt keeps the team and clears the taunt.
    /// </summary>
    /// <remarks>
    /// <b>An advance is not a rotation and the difference is the point.</b>
    /// <c>GameServerBeginStep.gml:74</c>: red winning with areas left replays the
    /// same map at the next area, and the reset runs with
    /// <c>firstArea: false</c> — so teams and the scoreboard <b>survive</b>,
    /// where a rotation wipes both. Every other test in this file exercises the
    /// wiping path; this one exercises the keeping path, where a stale character
    /// would be attached to a player who is still on a team rather than to a
    /// spectator.
    /// </remarks>
    [Fact]
    public void AnAreaAdvanceMidTauntKeepsTheTeamAndClearsTheTaunt()
    {
        const string areaMap = "cp_dirtbowl";

        using var harness = new ServerHarness(options =>
        {
            options.Map = areaMap;
            options.MapRotation = [areaMap];
            options.ShuffleRotation = ShuffleMode.None;
        });

        var socket = harness.JoinClient("advancer");
        using var mirror = new MirrorClient(socket, Load, areaMap);

        var slot = harness.Server.RosterCount - 1;

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the advancer to spawn",
            maxTicks: 400);

        harness.TickUntil(
            () =>
            {
                socket.SendInputState(new PlayerInput(PlayerKeys.Taunt, 0, 0));
                return harness.Server.IsTaunting(slot);
            },
            "the taunt to start",
            maxTicks: 200);

        mirror.Pump();
        mirror.ClearLog();

        Assert.True(harness.Server.RequestMapChange(areaMap, 2), "the area advance was refused");
        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMapArea == 2;
            },
            "the client to be told about area 2",
            maxTicks: 900);

        harness.Tick(60);
        mirror.Pump();

        // The advance KEPT the team — this is what separates it from a rotation.
        Assert.Equal(Team.Red, harness.Server.TeamOf(slot));
        Assert.Equal(Team.Red, mirror.Teams[slot]);

        Assert.False(harness.Server.IsTaunting(slot));
        Assert.Empty(mirror.Impossible);
    }

    /// <summary>
    /// A map change while a client is MID-TAUNT does the same.
    /// </summary>
    /// <remarks>
    /// A taunt is not decoration: it freezes the character where it stands and
    /// the client derives that from the same key bit
    /// (<c>docs/TRAPS-SIMULATION.md</c>, "Taunting and the humiliation round").
    /// So a taunting player is a character in a state the client is separately
    /// tracking, being destroyed underneath it.
    /// <para>
    /// ⚠️ The taunt is asserted to have actually STARTED before the rotation —
    /// otherwise this is the quiet-client rotation `GamemodeRotationTests`
    /// already covers, wearing a different name.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMapChangeWhileTauntingLeavesTheClientInAgreement()
    {
        using var harness = Harness();
        var socket = harness.JoinClient("taunter");
        using var mirror = new MirrorClient(socket, Load, Map);

        var slot = harness.Server.RosterCount - 1;

        harness.Tick(5);
        socket.SendChangeTeam(Team.Red);
        harness.TickUntil(
            () => harness.Server.TryGetExactPosition(slot, out _, out _, out _),
            "the taunter to spawn",
            maxTicks: 400);

        harness.TickUntil(
            () =>
            {
                socket.SendInputState(new PlayerInput(PlayerKeys.Taunt, 0, 0));
                return harness.Server.IsTaunting(slot);
            },
            "the taunt to start",
            maxTicks: 200);

        mirror.Pump();
        mirror.ClearLog();

        Assert.True(harness.Server.RequestMapChange("ctf_2dfort"), "the map change was refused");
        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMap == "ctf_2dfort";
            },
            "the client to be told about the new map",
            maxTicks: 900);

        harness.Tick(60);
        mirror.Pump();

        Assert.False(harness.Server.IsTaunting(slot));
        Assert.Equal(Team.Spectator, mirror.Teams[slot]);
        Assert.Empty(mirror.Impossible);
    }
}
