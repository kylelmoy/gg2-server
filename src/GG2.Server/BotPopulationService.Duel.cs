using GG2.Bots;
using GG2.Protocol;
using GG2.Server.Plugins;
using GG2.Simulation;

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
/// random cell nobody else holds; the bot outlives them by
/// <see cref="KillCamTicks"/> for the client's kill cam, then is re-classed to
/// the human's class with no kill credit
/// (<see cref="GameServer.RespawnBotUncredited"/>) and its respawn lined up with
/// the human's. <b>When the bot dies the human has won</b>: after
/// <see cref="WinMoveTicks"/> they are healed to full and moved to a new cell
/// (<see cref="GameServer.RelocateDuelist"/>), and the bot respawns there on the
/// same tick. Everything here goes through the commands a client
/// could send, except the credit, the pin, the respawn countdown and the move.
/// </para>
/// </remarks>
public sealed partial class BotPopulationService
{
    /// <summary>
    /// How long a duel bot outlives its human: three seconds, a second past the
    /// stock client's kill-cam snapshot (<c>DeathCam.events/Create.xml</c>,
    /// <c>alarm[0] = 60</c>), so a round trip's lag does not catch it dead.
    /// </summary>
    internal const int KillCamTicks = 3 * GameConstants.TickRate;

    /// <summary>
    /// How long a human who killed their bot stays in the cell before the move:
    /// a beat to see the kill land. The bot respawns in the new cell on the tick
    /// the human arrives.
    /// </summary>
    internal const int WinMoveTicks = 45;

    /// <summary>How often forgotten ladder ratings are swept out: once a minute.</summary>
    private const int LadderSweepTicks = 60 * GameConstants.TickRate;

    /// <summary>Every duel human's rating, by address, name and class. <see cref="DuelLadder"/>.</summary>
    internal DuelLadder Ladder { get; } = new();

    private long _duelTick;

    /// <summary>
    /// Every <c>/difficulty</c> other than auto, by <see cref="LadderKey"/>, kept
    /// as long as a rating is and for the same reason: a stock client reconnects
    /// at every map change.
    /// </summary>
    private readonly Dictionary<string, (DuelDifficulty Setting, long LastSeen)> _duelSettings = [];

    private void ServiceDuel(in BotPopulationOptions options)
    {
        var cells = Spawns is null ? 0 : Math.Min(Spawns.Count(Team.Red, 0), Spawns.Count(Team.Blue, 0));
        var ladder = _server.Options.DuelLadder;

        if (++_duelTick % LadderSweepTicks == 0)
        {
            Ladder.Forget(_duelTick);
            ForgetSettings();
        }

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
            // whenever a pair is already waiting on a bot. The death a team
            // change costs is not a lost duel.
            var changedSide = human.Team == Team.Blue;
            if (changedSide)
                _server.ForceTeam(slot, Team.Red);

            if (human.Team != Team.Red || cells == 0)
            {
                // Spectating (a map change makes everybody a spectator) or no cells:
                // no bot, no cell, and the bot is removed below for having no human.
                human.DuelPartner = null;
                human.DuelCell = -1;
                human.DuelDeaths = -1;
                human.DuelBotDeaths = -1;
                human.DuelBotKillIn = 0;
                human.DuelMoveIn = 0;
                human.DuelEngaged = false;
                _server.PinSpawn(slot, null);
                continue;
            }

            if (human.DuelCell < 0 || human.DuelCell >= cells)
                human.DuelCell = PickFreeCell(cells, human);

            // /difficulty off: the cell and no bot, which the removal below takes
            // away for having no human. Pairing again starts from no death seen.
            var setting = SettingOf(slot, human);
            if (setting.Mode == DuelDifficultyMode.Off)
            {
                human.DuelPartner = null;
                human.DuelDeaths = -1;
                human.DuelBotDeaths = -1;
                human.DuelBotKillIn = 0;
                human.DuelMoveIn = 0;
                human.DuelSettingChanged = false;
                _server.PinSpawn(slot, human.DuelCell);
                continue;
            }

            // A fixed level is a level whether or not the ladder runs; only the
            // ladder's own rating needs it on.
            var leveled = ladder || setting.Mode == DuelDifficultyMode.Fixed;

            var bot =human.DuelPartner is { } partner && _server.SlotOf(partner) is var at and > 0 ? at : -1;

            if (bot < 0)
            {
                bot = AddConfiguredBot(Team.Blue, human.Class, in options);
                if (bot < 0)
                    continue;

                human.DuelPartner = _server.EntryOf(bot);
                human.DuelBotDeaths = -1;
                human.DuelBotKillIn = 0;
                human.DuelMoveIn = 0;
                Retarget();
                human.DuelSettingChanged = false;

                if (leveled)
                    ApplyLadder(slot, human, bot, rename: true);
            }
            else if (human.DuelSettingChanged)
            {
                // At once and mid-duel: a fixed level moves no rating, so a reset
                // of the fight would buy nothing.
                human.DuelSettingChanged = false;
                if (leveled)
                {
                    ApplyLadder(slot, human, bot, rename: true);
                }
                else
                {
                    if (ScriptedBot.Of(_server.SourceOf(bot)) is { } scripted)
                        scripted.Skill = BotSkill.ForTier(options.Difficulty);

                    _server.RenameBot(bot, options.NamePrefix + human.Class.ToString());
                }
            }

            // Every death scores one (PlayerCommands.BidFarewell included), and a
            // map change zeroes the count, which reads as no death.
            var deaths = _server.DeathsOf(slot);
            var died = human.DuelDeaths >= 0 && deaths > human.DuelDeaths;
            human.DuelDeaths = deaths;

            // The bot's count the same way: a rise while its human is alive, with
            // no kill of ours pending, is the human's win. Our own kills are
            // absorbed by the re-read at the bottom.
            var botDeaths = _server.DeathsOf(bot);
            var won = !died
                && human.DuelBotKillIn == 0
                && human.DuelMoveIn == 0
                && human.DuelBotDeaths >= 0
                && botDeaths > human.DuelBotDeaths
                && _server.IsAlive(slot);

            if (died || won)
                human.DuelCell = PickFreeCell(cells, human);

            if (ladder && setting.Mode == DuelDifficultyMode.Auto)
            {
                // A loss is a death that was not a class or team change, with the
                // bot alive to have won it: a trade, or a death while the bot is
                // already down, counts for nobody. A death by the human's own hand
                // is a loss, so it is no way out of one.
                var lost = died
                    && !changedSide
                    && human.DuelClass == human.Class
                    && _server.IsAlive(bot);

                if (won || lost)
                {
                    var rating = Ladder.For(LadderKey(slot, human), human.Class, _server.Options.DuelLadderStartLevel, _duelTick);
                    DuelLadder.Record(
                        rating,
                        won,
                        _server.HealthFractionOf(won ? slot : bot),
                        _server.Options.DuelLadderWinPercent / 100.0);

                    // A beaten bot is renamed at once; a winning one keeps its
                    // name through the kill cam, which shows it, and is renamed
                    // when it is killed below.
                    ApplyLadder(slot, human, bot, rename: won);
                }
            }

            _server.PinSpawn(slot, human.DuelCell);
            _server.PinSpawn(bot, human.DuelCell);

            // A map change can leave the bot spectating while its human has already
            // re-picked red.
            if (_server.TeamOf(bot) != Team.Blue)
                _server.ForceTeam(bot, Team.Blue);

            if (died)
            {
                // A win's move still pending is void: the death moves them anyway.
                human.DuelMoveIn = 0;

                // The bot lives on for the kill cam; a sentry left behind would
                // fight it meanwhile and sit in a cell another pair may draw.
                _server.ExecuteFor(slot, new PlayerCommand(PlayerCommandKind.DestroySentry, 0));
                human.DuelBotKillIn = KillCamTicks;
            }

            if (human.DuelBotKillIn > 0)
            {
                // Held until the kill cam has looked (DeathCam's alarm[0], 60
                // frames after the death, frames the killer's character, and a
                // dead killer reads "killed by the late"), or until the human is
                // about to respawn or has, whichever is first. A protected death
                // respawns on the next tick, so that one kills at once.
                human.DuelBotKillIn--;
                var respawn = _server.RespawnTicksLeft(slot);
                var alive = _server.IsAlive(slot);

                if (human.DuelBotKillIn == 0 || alive || respawn <= 1)
                {
                    human.DuelBotKillIn = 0;

                    if (_server.IsAlive(bot))
                        _server.RespawnBotUncredited(bot, human.Class);
                    else if (_server.ClassOf(bot) != human.Class)
                        _server.RequestBotClass(bot, human.Class);

                    // Spawn on the human's tick, not a whole respawn time after
                    // this late kill.
                    _server.ArmBotRespawn(bot, alive ? 1 : Math.Max(1, respawn));

                    if (leveled)
                        ApplyLadder(slot, human, bot, rename: true);
                    else
                        _server.RenameBot(bot, options.NamePrefix + human.Class.ToString());
                }
            }
            else if (won)
            {
                // The new cell is pinned now, so the bot's respawn lands there; its
                // countdown is set so it appears as the human arrives, not a whole
                // respawn time after. One tick longer than the move's own count:
                // the simulation's alarm stage runs this tick, after the
                // population, and spends one tick of it before the move's first.
                human.DuelMoveIn = WinMoveTicks;
                _server.ArmBotRespawn(bot, WinMoveTicks + 1);
            }
            else if (human.DuelMoveIn > 0)
            {
                human.DuelMoveIn--;
                if (human.DuelMoveIn == 0)
                    _server.RelocateDuelist(slot, Spawns!, human.DuelCell);
            }
            else if (_server.ClassOf(bot) != human.Class)
            {
                // [Classlimits] refusing the add's class. The ordinary command:
                // it kills a live bot, which is the point — the duel restarts as
                // the matching class.
                _server.RequestBotClass(bot, human.Class);

                if (leveled)
                    ApplyLadder(slot, human, bot, rename: true);
                else
                    _server.RenameBot(bot, options.NamePrefix + human.Class.ToString());
            }

            // Once per stay on red: the class menu a join opens sends no keys, so
            // the first key is the human ready to fight. Reopening the menu later
            // is indistinguishable from standing still and does not re-arm it.
            if (!human.DuelEngaged && _server.IsAlive(slot) && _server.KeysOf(slot) != PlayerKeys.None)
                human.DuelEngaged = true;

            human.DuelBotDeaths = _server.DeathsOf(bot);
            human.DuelClass = human.Class;
        }

        RemoveUnpairedBots();

        // Last, after the removals have compacted the roster: each bot may fight
        // its own human and nobody else. The bot layer picks targets by distance,
        // and a human in the next cell can be nearer than a partner across a big
        // one; walls do not stop that choice, only the shot. Until its human has
        // pressed a key, a bot's only allowed opponent is itself, which is
        // nobody: it stands still and holds fire while the class menu is open.
        Span<byte> opponents = stackalloc byte[WorldState.MaxSlots];
        opponents.Fill(WorldView.AnyOpponent);

        for (var slot = 1; slot < _server.RosterCount; slot++)
        {
            var human = _server.EntryOf(slot);
            if (human.DuelPartner is { } partner && _server.SlotOf(partner) is var bot and > 0)
                opponents[bot] = human.DuelEngaged ? (byte)slot : (byte)bot;
        }

        _server.SetOpponents(opponents);
    }

    /// <summary>
    /// Sets a duel bot's skill to its human's fixed level, or else their rating
    /// for the human's class, and, when asked, names it for the level.
    /// </summary>
    private void ApplyLadder(int slot, RosterEntry human, int bot, bool rename)
    {
        var level = SettingOf(slot, human) is { Mode: DuelDifficultyMode.Fixed } setting
            ? setting.Level
            : Ladder.For(LadderKey(slot, human), human.Class, _server.Options.DuelLadderStartLevel, _duelTick).Value;

        if (ScriptedBot.Of(_server.SourceOf(bot)) is { } scripted)
            scripted.Skill = BotLevel.Skill(level);

        if (rename)
        {
            var name = DuelLadder.NameOf(level);
            if (_server.NameOf(bot) != name)
                _server.RenameBot(bot, name);
        }
    }

    /// <summary>
    /// A human's <c>/difficulty</c>, with auto's level filled in: their rating for
    /// the class they are now, or -1 with the ladder off.
    /// </summary>
    internal DuelDifficulty DuelDifficultyOf(int slot)
    {
        var human = _server.EntryOf(slot);
        var setting = SettingOf(slot, human);
        if (setting.Mode != DuelDifficultyMode.Auto)
            return setting;

        if (!_server.Options.DuelLadder)
            return DuelDifficulty.Auto;

        var rating = Ladder.TryGet(LadderKey(slot, human), human.Class, out var held)
            ? held.Value
            : _server.Options.DuelLadderStartLevel;
        return new DuelDifficulty(DuelDifficultyMode.Auto, DuelLadder.LevelOf(rating));
    }

    /// <summary>Sets a human's <c>/difficulty</c>; the next tick applies it.</summary>
    internal void SetDuelDifficulty(int slot, DuelDifficulty setting)
    {
        var human = _server.EntryOf(slot);
        setting = setting.Mode switch
        {
            DuelDifficultyMode.Fixed => setting with { Level = Math.Clamp(setting.Level, DuelLadder.MinLevel, DuelLadder.MaxLevel) },
            DuelDifficultyMode.Off => new DuelDifficulty(DuelDifficultyMode.Off),
            _ => DuelDifficulty.Auto,
        };

        human.DuelSetting = setting;
        human.DuelSettingChanged = true;

        var key = LadderKey(slot, human);
        if (setting.Mode == DuelDifficultyMode.Auto)
            _duelSettings.Remove(key);
        else
            _duelSettings[key] = (setting, _duelTick);
    }

    /// <summary>A human's <c>/difficulty</c>: what their address and name last set, auto by default.</summary>
    private DuelDifficulty SettingOf(int slot, RosterEntry human) =>
        human.DuelSetting ??= _duelSettings.TryGetValue(LadderKey(slot, human), out var held)
            ? held.Setting
            : DuelDifficulty.Auto;

    /// <summary>
    /// Forgets every <c>/difficulty</c> not held by a human here for
    /// <see cref="DuelLadder.ForgetTicks"/>, the ladder's own memory.
    /// </summary>
    private void ForgetSettings()
    {
        if (_duelSettings.Count == 0)
            return;

        for (var slot = 1; slot < _server.RosterCount; slot++)
        {
            var human = _server.EntryOf(slot);
            if (!human.IsBot && human.RatingKey is { } key && _duelSettings.ContainsKey(key))
                _duelSettings[key] = (_duelSettings[key].Setting, _duelTick);
        }

        List<string>? stale = null;
        foreach (var (key, held) in _duelSettings)
        {
            if (_duelTick - held.LastSeen > DuelLadder.ForgetTicks)
                (stale ??= []).Add(key);
        }

        if (stale is null)
            return;

        foreach (var key in stale)
            _duelSettings.Remove(key);
    }

    /// <summary>
    /// Who a human is to the ladder: address and name, so two people behind one
    /// address keep their own ratings, and a stock client reconnecting at a map
    /// change keeps its. Made once per roster entry.
    /// </summary>
    private string LadderKey(int slot, RosterEntry human) =>
        human.RatingKey ??= _server.AddressOf(slot) is { Length: > 0 } address
            ? address + "/" + human.Name
            : "#" + human.StableId;

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
