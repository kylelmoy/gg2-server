using GG2.Maps;
using GG2.Protocol;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// Recordings of a person playing a duel server, kept in
/// <c>Fixtures/duel/</c> as a replay corpus, with the values a replaying
/// client asserts against pinned here so the two cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>A subdirectory, not the top level.</b> The top-level fixtures are a corpus
/// with claims of their own (every one frames on a shipped map, the message
/// types the corpus reaches, every queued spawn lands on its marker), and a
/// duel recording is on a cell map from <c>maps/duel</c> with the duel's own
/// spawn rules.
/// </para>
/// <para>
/// What each one holds. All four are TDM on a cell map, the player at slot 1 on red
/// and their bot at slot 2 on blue, and each starts at the map change, so the
/// counts start at zero.
/// </para>
/// <list type="table">
/// <item><c>20261119-duel_08</c>: 6c07968, the player on a native client as pyro; the
/// session that showed the client's double deaths. The ladder (renames to
/// <c>Pyro N (Label)</c>), <c>QUICK_UPDATE</c> every 3rd tick, wins as a
/// <c>PLAYER_SPAWN</c> for a live player, losses ending in the bot's
/// BidFarewell class change, five <c>CAPS_UPDATE</c>s.</item>
/// <item><c>20261113-duel_01</c>, <c>_02</c>, <c>_06</c>: 9a86ba1, the player as
/// "Vindicator" on the stock client; no ladder (the bot is <c>[BOT] Class</c>),
/// <c>QUICK_UPDATE</c> every 7th tick, wins and BidFarewell losses as above.</item>
/// </list>
/// </remarks>
public sealed class DuelRecordingTests
{
    public static TheoryData<string, string, int, int, int, int, int> Recordings => new()
    {
        // file, human's name, PLAYER_DEATHs, final red, final blue, CAPS_UPDATEs, QUICK_UPDATE interval
        { "20261119-duel_08.trace", "Player", 57, 29, 15, 5, 3 },
        { "20261113-duel_01.trace", "Vindicator", 20, 2, 7, 7, 7 },
        { "20261113-duel_02.trace", "Vindicator", 11, 3, 4, 0, 7 },
        { "20261113-duel_06.trace", "Vindicator", 26, 2, 12, 1, 7 },
    };

    /// <summary>
    /// Frames to the last byte, and the Team Deathmatch count kept from its
    /// deaths by the stock client's rule (<c>doEventPlayerDeath.gml:310-316</c>)
    /// agrees with every <c>CAPS_UPDATE</c> the server sent and ends at the
    /// pinned total.
    /// </summary>
    [Theory]
    [MemberData(nameof(Recordings))]
    public void ARecordingFramesAndItsCountsAreTheServers(
        string file, string human, int deaths, int red, int blue, int capsUpdates, int quickInterval)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "duel", file);
        var trace = Trace.Decode(File.ReadAllBytes(path));

        var map = LevelMap.FromPng(
            trace.Header.Map,
            File.ReadAllBytes(Path.Combine(ReferenceTree.MapsDirectory, "duel", trace.Header.Map + ".png")));

        var parser = new ServerStream();
        parser.SetShape(GameModes.ShapeOf(map.ForArea(1)));
        var reader = new NetReader(trace.Stream);

        var names = new List<string>();
        var teams = new List<Team?>();
        int seenDeaths = 0, redCount = 0, blueCount = 0, caps = 0, quick = 0, input = 0;
        (string Name, Team? Team)? slotOne = null;

        while (reader.Remaining > 0)
        {
            var message = parser.Read(ref reader);
            var payload = message.Payload;

            switch (message.Type)
            {
                case MessageType.PlayerJoin:
                    names.Add(System.Text.Encoding.Latin1.GetString(payload, 1, payload[0]));
                    teams.Add(null);
                    break;

                case MessageType.PlayerLeave:
                    names.RemoveAt(payload[0]);
                    teams.RemoveAt(payload[0]);
                    break;

                case MessageType.PlayerChangeTeam:
                    teams[payload[0]] = (Team)payload[1];
                    break;

                case MessageType.PlayerChangeName:
                    names[payload[0]] = System.Text.Encoding.Latin1.GetString(payload, 2, payload[1]);
                    break;

                case MessageType.PlayerDeath:
                {
                    // Who held slot 1 while the duels ran: everybody has left by
                    // the end of a recording.
                    slotOne ??= (names[1], teams[1]);
                    seenDeaths++;
                    int victim = payload[0], killer = payload[1];
                    if (killer != 255 && killer != victim && teams[killer] != teams[victim])
                    {
                        if (teams[killer] == Team.Red)
                            redCount++;
                        else if (teams[killer] == Team.Blue)
                            blueCount++;
                    }

                    break;
                }

                case MessageType.CapsUpdate:
                    caps++;
                    Assert.Equal((redCount, blueCount), (payload[1], payload[2]));
                    break;

                case MessageType.QuickUpdate:
                    quick++;
                    break;

                case MessageType.InputState:
                    input++;
                    break;
            }
        }

        Assert.Equal(deaths, seenDeaths);
        Assert.Equal((red, blue), (redCount, blueCount));
        // One every 3600 ticks; duel_02 is under two minutes and has none.
        Assert.Equal(capsUpdates, caps);
        Assert.Equal((human, Team.Red), slotOne);
        Assert.Contains(trace.Roster, note => note.Slot == 1 && note.Participant == TraceParticipant.Human);

        // One update a tick, a QUICK_UPDATE every quickInterval-th.
        Assert.InRange((quick + input) / (double)quick, quickInterval - 0.05, quickInterval + 0.05);
    }
}
