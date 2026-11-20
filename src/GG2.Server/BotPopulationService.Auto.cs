using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

/// <summary>
/// Auto difficulty: every human has a rating that follows their fights with
/// bots, and every bot plays at the rating of the human it is shooting at.
/// <c>docs/BOTS.md</c>, "Auto difficulty".
/// </summary>
/// <remarks>
/// <para>
/// <b>Per target, not per team.</b> A bot fighting a strong human plays hard and
/// the same bot turning on a new player plays easy, the tick it turns: the skill
/// is read from <see cref="Opponents"/> through <see cref="ScriptedBot.Skill"/>,
/// which every reader of a bot's skill goes through. A team-wide level would
/// make the bots a strong human faces strong for the new player on the other
/// side as well, and with mostly-bot teams it reduces to per-human anyway.
/// </para>
/// <para>
/// <b>The rating is <see cref="DuelLadder"/>'s staircase, one per human across
/// every class.</b> A result is a kill between a human and a bot, whoever is
/// credited with it (<see cref="PlayerDeathEvent.Killer"/>): the bot dying is a
/// win and the human dying a loss, with the duel's margin, the winner's health
/// left. Human against human, a death with no killer and a death by the human's
/// own hand count for nobody: none of them says anything about the bots.
/// </para>
/// <para>
/// Bot against bot plays <c>[Bots] Difficulty</c>, and a new human's rating
/// starts on its level (<see cref="DuelLadder.LevelOfTier"/>), so a server that
/// turns this on starts out playing as it did.
/// </para>
/// </remarks>
public sealed partial class BotPopulationService
{
    /// <summary>Every human's auto-difficulty rating, by address and name, one per human.</summary>
    internal DuelLadder AutoRatings { get; } = new();

    /// <summary>
    /// The skill bots play at against each human this tick; empty with auto
    /// difficulty off. Every bot this service adds reads it.
    /// </summary>
    internal OpponentSkills Opponents { get; } = new();

    private long _autoTick;
    private bool _opponentsSet;

    private bool AutoOn => _server.Options.AutoDifficulty && !_server.Options.Duel;

    /// <summary>
    /// Rewrites <see cref="Opponents"/> from the ratings. Every tick, ahead of the
    /// bots' polls, because a slot is a roster index and a departure compacts it.
    /// </summary>
    private void ServiceAuto(in BotPopulationOptions options)
    {
        if (!AutoOn)
        {
            if (_opponentsSet)
            {
                Opponents.Clear();
                _opponentsSet = false;
            }

            return;
        }

        if (++_autoTick % LadderSweepTicks == 0)
            AutoRatings.Forget(_autoTick);

        Opponents.Clear();
        _opponentsSet = true;

        var start = DuelLadder.LevelOfTier(options.Difficulty);
        for (var slot = 1; slot < _server.RosterCount; slot++)
        {
            var entry = _server.EntryOf(slot);
            if (entry.IsBot || entry.Team is not (Team.Red or Team.Blue))
                continue;

            var rating = AutoRatings.For(LadderKey(slot, entry), null, start, _autoTick);
            Opponents.Set(slot, DuelLadder.SkillOf(rating.Value));
        }
    }

    /// <summary>
    /// Scores the tick's kills between a human and a bot. Called by the server
    /// straight after the simulation ticks, while the events' slots are still
    /// the roster's.
    /// </summary>
    internal void ObserveDeaths(in TickEvents events, int count)
    {
        if (!AutoOn)
            return;

        var target = _server.Options.AutoDifficultyWinPercent / 100.0;
        var start = DuelLadder.LevelOfTier(_server.Options.Bots.Difficulty);

        for (var victim = 1; victim < count; victim++)
        {
            if (!events.Died[victim])
                continue;

            int killer = events.Deaths[victim].Killer;
            if (killer == CharacterState.Nobody || killer == victim || killer <= 0 || killer >= _server.RosterCount)
                continue;

            var victimIsBot = _server.IsBot(victim);
            if (victimIsBot == _server.IsBot(killer))
                continue;

            var human = victimIsBot ? killer : victim;
            var rating = AutoRatings.For(LadderKey(human, _server.EntryOf(human)), null, start, _autoTick);
            DuelLadder.Record(rating, won: victimIsBot, _server.HealthFractionOf(killer), target);
        }
    }

    /// <summary>
    /// A human's auto-difficulty level, 1-8, or 0 with auto difficulty off or
    /// for a slot that is not a human with a rating. For the admin panel.
    /// </summary>
    internal double AutoRatingOf(int slot)
    {
        if (!AutoOn || slot <= 0 || slot >= _server.RosterCount || _server.IsBot(slot))
            return 0;

        var entry = _server.EntryOf(slot);
        return entry.RatingKey is { } key && AutoRatings.TryGet(key, null, out var rating) ? rating.Value : 0;
    }
}
