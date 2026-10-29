using GG2.Bots;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// The duel server's population: one blue bot per red human, of the human's
/// class, both pinned to the human's cell. <c>docs/DUEL.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cell is a spawn index.</b> A cell map puts red spawn <c>i</c> and blue
/// spawn <c>i</c> in cell <c>i</c>, so pinning a pair to one index is the whole of
/// putting them in one room; the simulation's part is
/// <c>PlayerState.SpawnPin</c>. On a map that is not a cell map the same code
/// pairs players across that map's spawn points, which is harmless and useless.
/// </para>
/// <para>
/// <b>The pairing lives on the roster entries</b>, which move with their players
/// when the roster compacts, so a departure two slots up cannot hand one human's
/// bot to another. Slots are looked up afresh from the entry whenever one is used.
/// </para>
/// <para>
/// <b>When the human dies, the bot dies and both move.</b> The human gets a new
/// random cell nobody else holds and the bot is re-classed to the human's class
/// with no kill credit (<see cref="GameServer.RespawnBotUncredited"/>), which also
/// arms its respawn. When the bot dies the human has won the exchange and nothing
/// moves: the bot respawns in the same cell. Everything here goes through the
/// commands a client could send, except the credit and the pin.
/// </para>
/// </remarks>
public sealed partial class BotPopulationService
{
    private void ServiceDuel(in BotPopulationOptions options)
    {
        var cells = Spawns is null ? 0 : Math.Min(Spawns.Count(Team.Red, 0), Spawns.Count(Team.Blue, 0));

        // Humans first: team, cell, bot, death. Indexed, and re-read through the
        // entry after anything that can move the roster (adding a bot appends, so
        // nothing below it shifts; only removal compacts, and that is last).
        for (var slot = 1; slot < _server.RosterCount; slot++)
        {
            var human = _server.EntryOf(slot);
            if (human.IsBot)
                continue;

            // Humans are red: the score is humans against bots. Forced rather than
            // requested — the balance guard would refuse red, the bigger team,
            // whenever a pair is already waiting on a bot.
            if (human.Team == Team.Blue)
                _server.ForceTeam(slot, Team.Red);

            if (human.Team != Team.Red || cells == 0)
            {
                // Spectating (a map change makes everybody a spectator) or no cells:
                // no bot, no cell, and the bot is removed below for having no human.
                human.DuelPartner = null;
                human.DuelCell = -1;
                human.DuelDeaths = -1;
                _server.PinSpawn(slot, null);
                continue;
            }

            if (human.DuelCell < 0 || human.DuelCell >= cells)
                human.DuelCell = PickFreeCell(cells, human);

            var bot = human.DuelPartner is { } partner && _server.SlotOf(partner) is var at and > 0 ? at : -1;

            if (bot < 0)
            {
                bot = AddConfiguredBot(Team.Blue, human.Class, in options);
                if (bot < 0)
                    continue;

                human.DuelPartner = _server.EntryOf(bot);
                Retarget();
            }

            // Every death scores one (PlayerCommands.BidFarewell included), and a
            // map change zeroes the count, which reads as no death.
            var deaths = _server.DeathsOf(slot);
            var died = human.DuelDeaths >= 0 && deaths > human.DuelDeaths;
            human.DuelDeaths = deaths;

            if (died)
                human.DuelCell = PickFreeCell(cells, human);

            _server.PinSpawn(slot, human.DuelCell);
            _server.PinSpawn(bot, human.DuelCell);

            // A map change can leave the bot spectating while its human has already
            // re-picked red.
            if (_server.TeamOf(bot) != Team.Blue)
                _server.ForceTeam(bot, Team.Blue);

            if (died)
            {
                _server.RespawnBotUncredited(bot, human.Class);
                _server.RenameBot(bot, options.NamePrefix + human.Class.ToString());
            }
            else if (_server.ClassOf(bot) != human.Class)
            {
                // A class picked while dead, or [Classlimits] refusing the add's
                // class. The ordinary command: it kills a live bot, which is the
                // point — the duel restarts as the matching class.
                _server.RequestBotClass(bot, human.Class);
                _server.RenameBot(bot, options.NamePrefix + human.Class.ToString());
            }
        }

        RemoveUnpairedBots();
    }

    /// <summary>
    /// A random cell no other duelling human holds, and not the human's own when
    /// another is free; any cell when every one is taken, which happens only with
    /// more humans than cells.
    /// </summary>
    private int PickFreeCell(int cells, RosterEntry human)
    {
        Span<bool> taken = stackalloc bool[cells];
        var free = cells;

        for (var slot = 1; slot < _server.RosterCount; slot++)
        {
            var other = _server.EntryOf(slot);
            if (!ReferenceEquals(other, human) && other.DuelCell >= 0 && other.DuelCell < cells && !taken[other.DuelCell])
            {
                taken[other.DuelCell] = true;
                free--;
            }
        }

        if (free == 0)
            return (int)_random.Next(cells);

        // A death moves the pair, so the cell just left counts as taken unless it
        // is the only one free.
        if (human.DuelCell >= 0 && human.DuelCell < cells && !taken[human.DuelCell] && free > 1)
        {
            taken[human.DuelCell] = true;
            free--;
        }

        var pick = (int)_random.Next(free);
        for (var cell = 0; cell < cells; cell++)
        {
            if (!taken[cell] && pick-- == 0)
                return cell;
        }

        return 0;
    }

    /// <summary>Removes every bot no duelling human names as its partner.</summary>
    private void RemoveUnpairedBots()
    {
        var removed = false;

        // Collected first: a removal compacts the roster, so slots are re-read
        // from the entries one removal at a time.
        List<RosterEntry>? orphans = null;
        foreach (var slot in _server.BotSlots)
        {
            var bot = _server.EntryOf(slot);
            var claimed = false;

            for (var other = 1; other < _server.RosterCount; other++)
            {
                if (ReferenceEquals(_server.EntryOf(other).DuelPartner, bot))
                {
                    claimed = true;
                    break;
                }
            }

            if (!claimed)
                (orphans ??= []).Add(bot);
        }

        if (orphans is null)
            return;

        foreach (var bot in orphans)
        {
            var slot = _server.SlotOf(bot);
            if (slot <= 0)
                continue;

            if (ScriptedBot.Of(_server.SourceOf(slot)) is { } leaving)
                Occupancy.Remove(leaving.Path);

            _server.RemoveBot(slot);
            removed = true;
        }

        if (removed)
            Retarget();
    }
}
