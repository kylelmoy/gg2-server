namespace Gg2.Simulation;

/// <summary>
/// The domination kill table: how many times each player has killed each other
/// player <em>since last dying to them</em>.
/// </summary>
/// <remarks>
/// <c>Scripts/Dominations/</c> and <c>Scripts/Events/doEventPlayerDeath.gml:81-96</c>.
/// The reference keeps one <c>ds_map</c> per player, keyed by the <em>victim</em>
/// — so <c>killer.dominationKills[victim]</c> — and a missing key reads as zero
/// (<c>domination_kills_get</c>). Here that is a square matrix of bytes, and an
/// absent entry and a zero are the same thing.
/// <para>
/// <b>It is on the wire, which is why it is simulation at all.</b> Every
/// <c>FULL_UPDATE</c> carries each player's row — one byte per <em>other</em>
/// roster entry — so a joining client can draw the right medals immediately.
/// After that both sides keep their own copy in step by running the same
/// <c>doEventPlayerDeath</c>; nothing incremental is ever sent.
/// </para>
/// <para>
/// <b>The four-kill threshold is presentation only.</b>
/// <c>doEventPlayerDeath</c> compares against 3 to decide whether to write
/// "domination" or "revenge" into the kill log, and the score table and the
/// character's Draw event use it for medals — none of which a headless server
/// has. What the server owes is the <em>counts</em>, and it owes them exactly,
/// because a client derives its own medals from them.
/// </para>
/// </remarks>
public static class Dominations
{
    /// <summary>
    /// Kills of one player needed before the reference calls it a domination.
    /// </summary>
    /// <remarks>
    /// The comparisons are <c>&gt; 3</c> (already dominating) and <c>== 3</c>
    /// (this kill starts it), both taken <em>before</em> the increment. Carried
    /// as a constant because it is the only number in the feature, and used by
    /// nothing in the port: see the class remarks.
    /// </remarks>
    public const int DominationThreshold = 3;

    /// <summary>
    /// How many times <paramref name="killer"/> has killed
    /// <paramref name="victim"/> without dying to them since.
    /// </summary>
    public static byte Get(in WorldState state, int killer, int victim)
    {
        if ((uint)killer >= WorldState.MaxSlots || (uint)victim >= WorldState.MaxSlots)
            return 0;

        return state.DominationKills[killer][victim];
    }

    /// <summary>
    /// <c>doEventPlayerDeath.gml:94-95</c>: the two writes a kill makes.
    /// </summary>
    /// <remarks>
    /// <b>A kill wipes the victim's count of the killer.</b>
    /// <c>domination_kills_delete(victim.dominationKills, killer)</c> — so one
    /// kill back ends a domination outright rather than chipping at it, and the
    /// counter is "kills since they last killed me", not "kills ever".
    /// <para>
    /// <b>Both the killer and the assistant get this</b>, from the same
    /// <c>killersForDomination</c> list — a medic who assisted is dominating the
    /// victim just as much as the shooter, and has their own count wiped by a
    /// kill in return.
    /// </para>
    /// <para>
    /// The count saturates at 255 rather than wrapping. The reference stores a
    /// real and would keep counting, but <c>write_ubyte</c> clamps on the way
    /// out — so the two agree on every byte a client can ever see, and differ
    /// only in a number nothing reads past 255.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>A suicide is a no-op here even without a guard</b>, because the
    /// increment and the wipe address the same cell: <c>[me][me]</c> goes up by
    /// one and is then set to zero. The reference is the same shape — one
    /// <c>ds_map</c>, one key, increased and then deleted. The
    /// <c>killer != victim</c> test in <c>doEventPlayerDeath</c> is real, but it
    /// is there to keep the killer out of <c>killersForDomination</c>, and
    /// removing it would change nothing about the table.
    /// </remarks>
    public static void RecordKill(ref WorldState state, int victim, int killer)
    {
        if ((uint)killer >= WorldState.MaxSlots || (uint)victim >= WorldState.MaxSlots)
            return;

        if (state.DominationKills[killer][victim] < byte.MaxValue)
            state.DominationKills[killer][victim]++;

        state.DominationKills[victim][killer] = 0;
    }

    /// <summary>
    /// <c>clearPlayerDominations</c>: this player is out of everybody's table
    /// and their own is emptied.
    /// </summary>
    /// <remarks>
    /// Called on a <b>team change</b> (<c>processClientCommands.gml:188</c>) and
    /// on a player leaving (<c>Player.events/Destroy.xml:8</c>), and on nothing
    /// else — <b>a class change does not clear it</b>, so a spy who keeps
    /// switching class stays dominated.
    /// <para>
    /// Both directions, which is the whole point: swapping teams cancels the
    /// dominations you hold and the ones held over you.
    /// </para>
    /// </remarks>
    public static void Clear(ref WorldState state, int slot)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        // `with (Player) domination_kills_delete(dominationKills, argument0)` —
        // the column.
        for (var i = 0; i < WorldState.MaxSlots; i++)
            state.DominationKills[i][slot] = 0;

        // `domination_kills_clear(argument0.dominationKills)` — the row.
        state.DominationKills[slot] = default;
    }
}
