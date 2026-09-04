using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The recording that reaches <c>BALANCE</c> — the last of the four
/// self-shortening messages that no fixture could frame.
/// </summary>
/// <remarks>
/// The recording: four soldiers against two on <c>ctf_2dfort</c>,
/// spawn-anchored by the map-reload recipe, with the roster forced lopsided
/// <em>after</em> the reload.
/// <para>
/// <b><c>BALANCE</c> is not a fixed-width message.</b> There are two shapes:
/// <c>BALANCE, 255</c> is the "teams are uneven" notice, and
/// <c>BALANCE, playerId, class</c> says somebody was actually moved. The client
/// reads one byte and only then decides whether to read a second
/// (<c>ClientBeginStep.gml:244-253</c>), so a reader that always takes two is one
/// byte ahead from the first notice onwards.
/// </para>
/// <para>
/// <b>Why no earlier fixture reached it.</b> <c>ServerBalanceTeams.gml</c>
/// needs a team <b>two players larger</b> than the other, and every other
/// recording here is a balanced bot roster. It is also not enough to make the
/// teams uneven: the script is called only on a <b>death, a departure or a team
/// change</b>, and the switch needs the fuse — <c>balancecounter</c>, which runs
/// every tick — to have reached <c>Server_Respawntime + 30*2</c>, which is 210
/// ticks at the default respawn time.
/// </para>
/// <para>
/// <b>And the reload re-balances the roster underneath you.</b> A map change
/// puts every player back to spectator and the bots re-pick, which turned a
/// forced 4/2 into 3/3 — so the roster has to be forced <em>after</em> the
/// reload, not before. That is what the <c>PLAYER_LEAVE</c> in this recording
/// is.
/// </para>
/// </remarks>
public sealed class AutobalanceFixtureTests
{
    private const string Fixture = "reference-ctf_2dfort-autobalance.trace";

    /// <summary><c>ServerBalanceTeams.gml</c>'s "teams are uneven" sentinel.</summary>
    private const byte Notice = 255;

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

    private static byte[][] Balances(IReadOnlyList<TraceTick> ticks) =>
        ticks
            .SelectMany(t => t.Events.Where(m => m.Type == MessageType.Balance))
            .Select(m => m.Payload)
            .ToArray();

    /// <summary>It frames whole, and it really does contain the message.</summary>
    [Fact]
    public void ItFramesCleanlyAndHoldsBalanceMessages()
    {
        var (trace, ticks) = Load();

        Assert.Equal("ctf_2dfort", trace.Header.Map);
        Assert.True(ticks.Count > 4000, $"only {ticks.Count} ticks");
        Assert.True(Balances(ticks).Length >= 2, "not enough BALANCE messages");
    }

    /// <summary>
    /// <b>The gate: both shapes are on the wire, and each is exactly as long
    /// as its first byte says.</b>
    /// </summary>
    /// <remarks>
    /// <b>Framing cleanly is not enough on its own</b> — a recording holding
    /// only notices would frame perfectly under the old fixed-two reader for the
    /// wrong reason, and one holding only switches would frame perfectly under
    /// it for the right one. What proves the length is content-dependent is
    /// having <em>both</em> in the same stream, which
    /// <see cref="BothShapesArePresent"/> asserts separately so its failure says
    /// which half went missing.
    /// </remarks>
    [Fact]
    public void EveryBalanceIsAsLongAsItsFirstByteSays()
    {
        var (_, ticks) = Load();

        foreach (var payload in Balances(ticks))
        {
            Assert.True(payload.Length >= 1, "an empty BALANCE");

            // ClientBeginStep.gml:244-253 — one byte, then a second only when the
            // first is not the notice sentinel.
            var expected = payload[0] == Notice ? 1 : 2;

            Assert.Equal(expected, payload.Length);
        }
    }

    /// <summary>
    /// The control: the recording carries a notice <em>and</em> a switch, so the
    /// two lengths are both exercised.
    /// </summary>
    /// <remarks>
    /// This is the property a re-recording is most likely to lose — the fuse has
    /// to expire and a <b>dead</b> player has to be available on the larger team
    /// at the moment some death calls the script, and if that never lines up the
    /// recording holds notices alone. Asserted so that failure is loud rather
    /// than a fixture that quietly stops gating anything.
    /// </remarks>
    [Fact]
    public void BothShapesArePresent()
    {
        var (_, ticks) = Load();
        var payloads = Balances(ticks);

        Assert.Contains(payloads, p => p.Length == 1 && p[0] == Notice);
        Assert.Contains(payloads, p => p.Length == 2 && p[0] != Notice);
    }

    /// <summary>
    /// The switch names a real roster slot and a real class, and a
    /// <c>PLAYER_CHANGETEAM</c> for that slot follows it.
    /// </summary>
    /// <remarks>
    /// <b>The move is not a death</b> — no <c>PLAYER_DEATH</c>, no credit and
    /// no deaths stat (docs/SIMULATION.md, "Settled: autobalance") — so the only
    /// thing on the wire that says it happened is this pair. The class byte is
    /// <c>checkClasslimits</c>'s answer rather than the player's old class, which
    /// is why it is sent at all.
    /// </remarks>
    [Fact]
    public void TheSwitchNamesASlotAndAClass()
    {
        var (_, ticks) = Load();

        var switched = Balances(ticks).First(p => p.Length == 2);

        // Ten classes, and a roster that never exceeded the server's slots.
        Assert.InRange(switched[0], 0, 31);
        Assert.InRange(switched[1], 0, 9);

        // The team change the switch causes is broadcast in its own message, and
        // there are more of those than the bots' own picks because of it.
        Assert.True(
            ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.PlayerChangeTeam)) >= 2,
            "no PLAYER_CHANGETEAM to go with the switch");
    }
}
