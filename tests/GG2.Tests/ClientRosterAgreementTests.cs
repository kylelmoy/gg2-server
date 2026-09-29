using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// What a client was told must add up to what the server believes.
/// </summary>
/// <remarks>
/// <c>docs/PARITY.md</c>, "The gap in every tier above": every other automated
/// check reads the server, so a disagreement between the server's state and what
/// it actually emitted is invisible to all of them.
/// <para>
/// <b>A rotation is the hard case and gets its own tests.</b> The client sets
/// every player to spectator by itself on <c>CHANGE_MAP</c>
/// (<c>ClientBeginStep.gml:514-544</c>), and a <c>FULL_UPDATE</c>'s player block
/// carries <b>no team</b> — so after a rotation <c>PLAYER_CHANGETEAM</c> is the
/// only thing in the protocol that can put anybody back on a team. A server that
/// re-teams somebody without sending one has a client that will build a
/// <c>Character</c> for a spectator and die on GM8's
/// <c>Attempted to get a sprite for unknown team ID: 2</c>.
/// </para>
/// </remarks>
public sealed class ClientRosterAgreementTests
{
    private const string First = "ctf_truefort";
    private const string Second = "koth_valley";

    private static LevelMap Load(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static ServerHarness Rotating(int bots) =>
        new(options =>
        {
            options.Map = First;
            options.MapRotation = [First, Second];
            options.ShuffleRotation = ShuffleMode.None;
            options.NavCacheDirectory = ReferenceTree.NavCacheDirectory;

            // Named factory, never `default` or `with` off a zeroed one — see
            // BotPopulationOptions.Defaults. MinHumans 0 so the bots fill a
            // server whose only human is the watcher.
            options.Bots = new BotPopulationOptions(
                Enabled: bots > 0,
                FillToPlayers: bots + 1,
                MaxBots: bots,
                MinHumans: 0,
                Difficulty: 3);
        });

    /// <summary>
    /// Every slot's team, as the server holds it.
    /// </summary>
    private static List<Team> ServerTeams(ServerHarness harness)
    {
        var teams = new List<Team>();

        for (var slot = 0; slot < harness.Server.RosterCount; slot++)
            teams.Add(harness.Server.TeamOf(slot));

        return teams;
    }

    private static void AssertAgrees(ServerHarness harness, MirrorClient mirror, string when)
    {
        mirror.Pump();

        var server = ServerTeams(harness);

        Assert.True(
            server.Count == mirror.PlayerCount,
            $"{when}: server roster has {server.Count} players, the client believes {mirror.PlayerCount}");

        for (var slot = 0; slot < server.Count; slot++)
        {
            Assert.True(
                server[slot] == mirror.Teams[slot],
                $"{when}: slot {slot} ({harness.Server.EntryOf(slot).Name}) is {server[slot]} on the server "
                + $"and {mirror.Teams[slot]} to the client");
        }

        // Names too, and not for completeness: a stream that has slipped its
        // framing produces phantom roster entries with EMPTY names. Comparing
        // only teams would let "everybody is a spectator and there are eight
        // of them" pass.
        for (var slot = 0; slot < server.Count; slot++)
        {
            Assert.True(
                harness.Server.EntryOf(slot).Name == mirror.Names[slot],
                $"{when}: slot {slot} is '{harness.Server.EntryOf(slot).Name}' on the server "
                + $"and '{mirror.Names[slot]}' to the client");
        }

        // And the sharper question, which team equality does not ask: was this
        // client ever handed a character for somebody it holds as a spectator?
        // That is the GM8 fatal error, not an approximation of it.
        Assert.True(
            mirror.Impossible.Count == 0,
            $"{when}: the server sent {mirror.Impossible.Count} character(s) this client cannot build — "
            + string.Join("; ", mirror.Impossible.Take(5)));
    }

    /// <summary>
    /// The control: with nobody rotating, the two agree.
    /// </summary>
    /// <remarks>
    /// <b>Not decoration.</b> A mirror wired up wrongly — a reader that never
    /// applied <c>PLAYER_CHANGETEAM</c>, say — would fail every test in this file
    /// including the ones that are supposed to fail, and the rotation result
    /// would mean nothing. This says the instrument reads correctly before it is
    /// used to accuse anybody.
    /// </remarks>
    [Fact]
    public void TheClientAndServerAgreeOnAFreshServer()
    {
        using var harness = Rotating(bots: 4);

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, First);

        harness.Tick(120);

        AssertAgrees(harness, mirror, "on a fresh server");

        // Not vacuous: the bots must actually be on teams, or "everything is a
        // spectator" would satisfy this trivially.
        mirror.Pump();
        Assert.True(
            mirror.Teams.Count(t => t is Team.Red or Team.Blue) >= 4,
            $"expected the bots on teams; the client sees [{string.Join(", ", mirror.Teams)}]");
    }

    /// <summary>
    /// A team change reaches the client.
    /// </summary>
    [Fact]
    public void AChangeOfTeamReachesTheClient()
    {
        using var harness = Rotating(bots: 0);

        var client = harness.JoinClient("player");
        using var mirror = new MirrorClient(client, Load, First);

        harness.Tick(4);
        client.SendChangeTeam(Team.Red);
        harness.Tick(4);

        AssertAgrees(harness, mirror, "after joining a team");
    }

    /// <summary>
    /// The rotation. With bots, this is the reported crash.
    /// </summary>
    /// <remarks>
    /// Reproduces <c>docs/PLUGINS.md</c>, "A rotation kills every connected
    /// client, and it needs bots" — in process, with no GM8 client, in a second.
    /// </remarks>
    [Fact]
    public void TheClientAndServerAgreeAfterARotationWithBots()
    {
        using var harness = Rotating(bots: 4);

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, First);

        harness.Tick(120);
        AssertAgrees(harness, mirror, "before the rotation");

        Rotate(harness, mirror);

        AssertAgrees(harness, mirror, "after the rotation");
    }

    /// <summary>
    /// The same rotation with no bots, which is the reported passing case.
    /// </summary>
    /// <remarks>
    /// <b>Kept even though it passes.</b> It is what makes "bots are required"
    /// a property of this suite rather than a line in someone's write-up, so a
    /// later change that breaks the empty case too is distinguishable from this
    /// bug rather than looking like it.
    /// </remarks>
    [Fact]
    public void TheClientAndServerAgreeAfterARotationWithNoBots()
    {
        using var harness = Rotating(bots: 0);

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, First);

        harness.Tick(60);
        Rotate(harness, mirror);

        AssertAgrees(harness, mirror, "after the rotation");
    }

    /// <summary>
    /// Bots must still be playing after a rotation.
    /// </summary>
    /// <remarks>
    /// Invisible even to a real client: <c>ResetForNewMap</c> makes every player
    /// a spectator, and <c>BotPopulationService.Service</c> returns early while
    /// <c>bots == target</c> — so nothing puts them back. A server whose bots all
    /// spectate still ticks, still rotates and still reports matches as
    /// completed. <b>This asserts the SERVER's own state</b>, unlike the rest of
    /// this file: it is a bug the server can see.
    /// </remarks>
    [Fact]
    public void BotsAreStillPlayingAfterARotation()
    {
        using var harness = Rotating(bots: 4);

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, First);

        harness.Tick(120);

        var before = harness.Server.BotSlots.Count(s => harness.Server.TeamOf(s) is Team.Red or Team.Blue);
        Assert.Equal(4, before);

        Rotate(harness, mirror);
        harness.Tick(120);

        var after = harness.Server.BotSlots.Count(s => harness.Server.TeamOf(s) is Team.Red or Team.Blue);

        Assert.True(
            after == 4,
            $"{after} of 4 bots are on a team after the rotation; the rest are spectating for ever");
    }

    /// <summary>
    /// The HUD block after a GAMEMODE-changing rotation must be the new
    /// gamemode's.
    /// </summary>
    /// <remarks>
    /// <b>This is the bug that killed every connected stock client.</b>
    /// <c>GameServer.OpenMap</c> replaces <c>_match</c> on every rotation, and
    /// <c>StateUpdateWriter</c> captured that object once at <c>Start</c> — so
    /// after a CTF → KotH change it kept writing a <em>CTF</em> HUD block while
    /// the client read a KotH one. The stream desynchronised permanently at the
    /// first <c>CAPS_UPDATE</c> after the change.
    /// <para>
    /// <b>Why the rest of this file could miss it.</b> The HUD block rides on
    /// <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c> only (<c>serializeState.gml</c>),
    /// so a test that never runs long enough for a <c>CAPS_UPDATE</c> to be
    /// emitted after a rotation never exercises the write path. **A layout test
    /// has to make the layout actually get written.**
    /// </para>
    /// <para>
    /// <b>A same-gamemode rotation is NOT a control for this</b> — koth →
    /// koth was healthy against a real client precisely because the stale
    /// gamemode happened to equal the live one. The pair that matters is
    /// gamemode-changing versus not.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHudBlockAfterAGamemodeChangeIsTheNewGamemodes()
    {
        using var harness = Rotating(bots: 0);

        var client = harness.JoinClient("watcher");
        using var mirror = new MirrorClient(client, Load, First);

        harness.Tick(30);

        // BOTH directions. CTF → KotH and KotH → CTF are different stale
        // pairings, and a fix that only reached one of them would leave half the
        // rotation broken.
        Rotate(harness, mirror);
        Assert.Equal(Second, mirror.CurrentMap);
        AssertHudArrivesAndParses(harness, mirror, "after ctf → koth");

        harness.Server.RequestMapChange(First);
        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMap == First;
            },
            "the client to be told about the second map",
            maxTicks: 600);

        AssertHudArrivesAndParses(harness, mirror, "after koth → ctf");
    }

    /// <summary>
    /// Runs on until a <c>CAPS_UPDATE</c> arrives, then checks it read back.
    /// </summary>
    /// <remarks>
    /// <b>The waiting is the point.</b> The HUD block rides on
    /// <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c> only
    /// (<c>serializeState.gml</c>), so a test that rotates and then ticks a few
    /// hundred times never makes the server write the layout it is trying to
    /// check. `_frame % 3600` is the two-minute scoreboard heartbeat and is the
    /// one trigger that needs nothing to happen in the match.
    /// </remarks>
    private static void AssertHudArrivesAndParses(ServerHarness harness, MirrorClient mirror, string when)
    {
        var before = mirror.Of(MessageType.CapsUpdate).Count;

        for (var i = 0; i < 3700 && mirror.Of(MessageType.CapsUpdate).Count == before; i++)
        {
            harness.Tick(1);
            mirror.Pump();
        }

        var caps = mirror.Of(MessageType.CapsUpdate);

        // Not vacuous: with no CAPS_UPDATE this asserts nothing at all.
        Assert.True(caps.Count > before, $"{when}: no CAPS_UPDATE was emitted");

        // The reader was given the NEW map's shape. A stale HUD block from the
        // previous gamemode is a different length, so the update either fails to
        // frame or leaves the stream misaligned for everything after it — which
        // AssertAgrees then catches as a roster that has stopped adding up.
        Assert.NotNull(caps[^1].State);

        AssertAgrees(harness, mirror, when);
    }

    /// <summary>
    /// Runs the map change and settles.
    /// </summary>
    /// <remarks>
    /// Driven through the control port's own entry point rather than by waiting
    /// out a time limit, so the test costs a second instead of a minute. The path
    /// under test is the same one <c>--time-limit</c> reaches: both set
    /// <c>_nextMap</c> and run the countdown.
    /// </remarks>
    private static void Rotate(ServerHarness harness, MirrorClient mirror)
    {
        Assert.True(harness.Server.RequestMapChange(Second), "the map change was refused");

        harness.TickUntil(
            () =>
            {
                mirror.Pump();
                return mirror.CurrentMap == Second;
            },
            "the client to be told about the new map",
            maxTicks: 600);

        // Long enough for the population manager, the balancer and the first
        // spawns to have happened.
        for (var i = 0; i < 200; i++)
        {
            harness.Tick(1);
            mirror.Pump();
        }
    }
}
