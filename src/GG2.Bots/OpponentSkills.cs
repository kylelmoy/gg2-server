using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// The skill a bot plays at against each roster slot, where one is set: auto
/// difficulty's per-human ratings (<c>docs/BOTS.md</c>, "Auto difficulty").
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexed by roster slot and rewritten whole every tick</b> by whoever owns
/// it, so a departure that compacts the roster is wrong for at most the tick it
/// happens on. One table is shared by every bot on the server; each bot reads the
/// row of whatever it is shooting at (<see cref="ScriptedBot.Skill"/>).
/// </para>
/// <para>
/// A slot with no row — a bot, a spectator, an unrated human — falls back to the
/// bot's own skill, so bot-against-bot fights play the server's difficulty.
/// </para>
/// </remarks>
public sealed class OpponentSkills
{
    private readonly BotSkill[] _skills = new BotSkill[WorldState.MaxSlots];
    private readonly bool[] _set = new bool[WorldState.MaxSlots];

    /// <summary>Sets the skill bots play at against <paramref name="slot"/>.</summary>
    public void Set(int slot, in BotSkill skill)
    {
        if ((uint)slot >= (uint)_set.Length)
            return;

        _skills[slot] = skill;
        _set[slot] = true;
    }

    /// <summary>Removes every row.</summary>
    public void Clear() => Array.Clear(_set);

    /// <summary>The skill against <paramref name="slot"/>, or false when it has no row.</summary>
    public bool TryGet(int slot, out BotSkill skill)
    {
        if ((uint)slot < (uint)_set.Length && _set[slot])
        {
            skill = _skills[slot];
            return true;
        }

        skill = default;
        return false;
    }

    /// <summary>
    /// The skill to play against <paramref name="target"/>: its player's row for
    /// a character or a sentry, and <paramref name="own"/> for anything else.
    /// </summary>
    public BotSkill Against(in BotTarget target, in BotSkill own) =>
        target.Kind is BotTargetKind.Character or BotTargetKind.Sentry && TryGet(target.Slot, out var skill)
            ? skill
            : own;
}
