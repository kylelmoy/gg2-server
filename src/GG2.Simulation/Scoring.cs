using System.Runtime.CompilerServices;
using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// One player's thirteen counters, indexed by <see cref="StatIndex"/>.
/// </summary>
/// <remarks>
/// <b>Thirteen and not eleven, on purpose.</b> Only eleven reach the wire —
/// <see cref="StatIndex.Dominations"/> and <see cref="StatIndex.Revenge"/> are
/// reset and serialised by nothing — but the reference indexes one array by the
/// whole enum, and leaving two holes in the middle would turn every award into
/// an index mapping. Two spare doubles per player is the cheaper mistake.
/// <para>
/// <b>Doubles, because the reference's are reals.</b> An assist is worth
/// <b>half</b> a point and healing accumulates fractional hit points, so an
/// integer counter would round in a place the reference does not.
/// </para>
/// <para>
/// Inline for the reason every other buffer in <see cref="WorldState"/> is: the
/// state must stay a value type that clones with a <c>memcpy</c>
/// (docs/BOTAI.md item 1).
/// </para>
/// </remarks>
[InlineArray(Scoring.StatCount)]
public struct StatBuffer
{
    private double _element0;
}

/// <summary>
/// The scoreboard: what a player has done this map, and what they have done
/// this Arena round.
/// </summary>
/// <remarks>
/// <c>Objects/InGameElements/Player.events/Create.xml:234-261</c> declares both
/// arrays and <c>User Event 12.xml</c> serialises eleven of the first one.
/// The awards are spread across <c>Scripts/Events/doEvent*.gml</c>,
/// <c>Scripts/Maps/pointCapture.gml</c> and
/// <c>Objects/Weapons/Medigun.events/User Event 1.xml</c>.
/// <para>
/// <b>Every award writes <em>both</em> arrays, without exception.</b> Every
/// one of the fourteen <c>stats[X] += n</c> lines in the reference is
/// immediately followed by <c>roundStats[X] += n</c> with the same index and the
/// same amount — checked across <c>Scripts/</c> and <c>Objects/</c>. So
/// <see cref="Award"/> writes both and no call site gets the chance to forget
/// one. That is transcribing the arithmetic, not the structure.
/// </para>
/// <para>
/// <b>What the two arrays are for.</b> <c>stats</c> is the map's scoreboard and
/// is what a <c>FULL_UPDATE</c> carries; <c>roundStats</c> exists for Arena,
/// whose <c>ARENA_ENDROUND</c> names the top three players of each team by
/// <c>roundStats[POINTS]</c> and is reset at every round start
/// (<c>Scripts/Maps/Arena/resetRoundStats.gml</c>). Outside Arena nothing ever
/// reads the second one — but everything writes it.
/// </para>
/// <para>
/// <b>The domination kill table is modelled</b> — <see cref="Dominations"/>,
/// reached from <c>GameServer</c> and written into the <c>FULL_UPDATE</c> block
/// that carries one byte per <em>other</em> player.
/// <see cref="StatIndex.Dominations"/> and
/// <see cref="StatIndex.Revenge"/> are vestigial in the reference too — reset in
/// four places and incremented nowhere.
/// </para>
/// </remarks>
public static class Scoring
{
    /// <summary>How many entries a <see cref="StatBuffer"/> holds.</summary>
    public const int StatCount = 13;

    /// <summary>
    /// How many of them reach the wire, and they are the first eleven —
    /// <see cref="StatIndex.Points"/> is the twelfth entry and is serialised out
    /// of order.
    /// </summary>
    /// <remarks>
    /// The serialisation order is <b>not</b> <see cref="StatIndex"/> order:
    /// <c>Player.events/User Event 12.xml</c> writes KILLS through BONUS and then
    /// <b>POINTS</b>, skipping DOMINATIONS and REVENGE. See
    /// <c>GG2.Server.StateUpdateWriter</c>, which has the one copy of that order.
    /// </remarks>
    public const int SerialisedStatCount = 11;

    /// <summary>
    /// Adds to one counter, in both arrays.
    /// </summary>
    /// <remarks>
    /// The only way anything in the simulation should touch a stat. See the
    /// class remarks for why it writes both.
    /// </remarks>
    public static void Award(ref WorldState state, int slot, StatIndex stat, double amount)
    {
        if ((uint)slot >= WorldState.MaxSlots || !state.HasPlayer[slot])
            return;

        ref var player = ref state.Players[slot];
        player.Stats[(int)stat] += amount;
        player.RoundStats[(int)stat] += amount;
    }

    /// <summary>Reads one counter from the map-long array.</summary>
    public static double Get(in WorldState state, int slot, StatIndex stat) =>
        (uint)slot < WorldState.MaxSlots ? state.Players[slot].Stats[(int)stat] : 0;

    /// <summary>Reads one counter from the Arena-round array.</summary>
    public static double GetRound(in WorldState state, int slot, StatIndex stat) =>
        (uint)slot < WorldState.MaxSlots ? state.Players[slot].RoundStats[(int)stat] : 0;

    /// <summary>
    /// <c>Scripts/Maps/Arena/resetRoundStats.gml</c>: a new Arena round starts
    /// everybody's round scoreboard at zero.
    /// </summary>
    /// <remarks>
    /// Called from <c>doEventArenaStartRound</c> and nowhere else, so it is the
    /// only thing that ever clears the second array on its own — a map change
    /// clears both (<see cref="ResetAll"/>).
    /// </remarks>
    public static void ResetRound(ref WorldState state)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            state.Players[slot].RoundStats = default;
    }

    /// <summary>
    /// <c>GameServerBeginStep.gml:144-169</c>: a map change wipes both arrays.
    /// </summary>
    /// <remarks>
    /// <b>Only on area 1</b>, inside the same <c>global.currentMapArea == 1</c>
    /// guard that decides whether teams are reset — so a multi-area Invasion map
    /// carries its scoreboard from one area to the next.
    /// <see cref="Rounds.ResetForNewMap"/> is the caller and holds that flag.
    /// </remarks>
    public static void ResetAll(ref WorldState state)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            state.Players[slot].Stats = default;
            state.Players[slot].RoundStats = default;
        }
    }

    /// <summary>
    /// <c>doEventPlayerDeath.gml:30-79</c>: everything a death is worth.
    /// </summary>
    /// <param name="victimSlot">Whose character died.</param>
    /// <param name="death">The credit <see cref="Deaths.Resolve"/> worked out.</param>
    /// <remarks>
    /// <b>Called before the body is cleared</b>, because four of the six
    /// clauses read the victim's <em>character</em>: the medic-bonus one reads
    /// its weapon and the defence one reads whether it was carrying a briefcase.
    /// <para>
    /// <b><c>victim.stats[DEATHS] += 1</c> is unconditional.</b> Falling in a
    /// pit, standing in a generator explosion and shooting yourself all count.
    /// </para>
    /// <para>
    /// <b>The stab and medic-bonus clauses are inside <c>if(killer)</c> but
    /// <em>outside</em> <c>if(killer != victim)</c></b>, so a medic who kills
    /// themselves with a full ubercharge awards themselves the bonus point. Read
    /// as one nested block, the two would agree; they do not.
    /// </para>
    /// <para>
    /// <b>An assist is worth half a point</b>, which is the only fractional
    /// award in the game and the reason these counters are reals. It is also why
    /// a client's scoreboard can read one point above the host's: the wire
    /// carries <c>write_ubyte(stats[POINTS])</c>, which rounds half away from
    /// zero, while the host's own table draws <c>floor(stats[POINTS])</c>.
    /// </para>
    /// </remarks>
    public static void RecordDeath(ref WorldState state, int victimSlot, in PlayerDeathEvent death)
    {
        Award(ref state, victimSlot, StatIndex.Deaths, 1);

        var killer = death.Killer;

        // `if(!instance_exists(killer)) killer = noone;` then `if(killer)` —
        // GM8's truth test on a player instance, which is "there is one".
        if (killer != CharacterState.Nobody && state.HasPlayer[killer])
            RecordKillCredit(ref state, victimSlot, killer, death.Source);

        var assistant = death.Assistant;

        if (assistant != CharacterState.Nobody && state.HasPlayer[assistant])
        {
            Award(ref state, assistant, StatIndex.Assists, 1);
            Award(ref state, assistant, StatIndex.Points, 0.5);
        }

        // doEventPlayerDeath.gml:81-96, over `killersForDomination` — which the
        // two blocks above appended to: the killer only when
        // `killer != victim`, and the assistant whenever there is one.
        //
        // So a suicide records no domination for anybody, and an assistant
        // is dominating the victim exactly as much as the shooter is.
        if (killer != CharacterState.Nobody && state.HasPlayer[killer] && killer != victimSlot)
            Dominations.RecordKill(ref state, victimSlot, killer);

        if (assistant != CharacterState.Nobody && state.HasPlayer[assistant])
            Dominations.RecordKill(ref state, victimSlot, assistant);
    }

    /// <summary>The <c>if(killer)</c> block of a death.</summary>
    private static void RecordKillCredit(
        ref WorldState state, int victimSlot, int killer, DamageSource source)
    {
        // **Outside the `killer != victim` test**, which is the surprising
        // part: a spy who somehow stabs themselves is awarded the stab and the
        // point.
        if (source is DamageSource.Knife or DamageSource.Backstab)
        {
            Award(ref state, killer, StatIndex.Stabs, 1);
            Award(ref state, killer, StatIndex.Points, 1);
        }

        // `if (victim.object.currentWeapon.object_index == Medigun)
        //      if (victim.object.currentWeapon.uberReady)` — killing a medic who
        // was sitting on a full charge is worth a bonus point, to anybody
        // including the medic.
        if (state.IsAlive(victimSlot)
            && state.Characters[victimSlot].Class == PlayerClass.Medic
            && state.Weapons[victimSlot].UberReady)
        {
            Award(ref state, killer, StatIndex.Bonus, 1);
            Award(ref state, killer, StatIndex.Points, 1);
        }

        if (killer == victimSlot)
            return;

        Award(ref state, killer, StatIndex.Kills, 1);
        Award(ref state, killer, StatIndex.Points, 1);

        // `if(victim.object.intel)` — killing a briefcase carrier is a defence.
        if (state.IsAlive(victimSlot) && state.Characters[victimSlot].CarryingIntel)
        {
            Award(ref state, killer, StatIndex.Defenses, 1);
            Award(ref state, killer, StatIndex.Points, 1);
        }
    }

    /// <summary>How many MVPs a team can have in an <c>ARENA_ENDROUND</c>.</summary>
    public const int MaxMvps = 3;

    /// <summary>
    /// <c>serverArenaEndRound.gml:9-56</c>: a team's top three by round points,
    /// best first.
    /// </summary>
    /// <returns>How many entries of <paramref name="into"/> were filled.</returns>
    /// <remarks>
    /// <b>Every player on the team goes in, whether or not they scored or
    /// even played.</b> The reference adds each to a <c>ds_priority</c> keyed on
    /// <c>roundStats[POINTS]</c> and only then trims to three, so on a team of
    /// three everybody is an MVP with nothing to their name. It does not read
    /// <c>object</c>, so the dead and the never-spawned are included.
    /// <para>
    /// <b><c>redMVP[i].roundStats[HEALING] = round(...)</c> writes the stat
    /// back.</b> The rounding is not only on the way out: a medic's round
    /// healing total is permanently rounded, half to even, the moment a round
    /// ends. Harmless, because the round array is about to be reset — but it is
    /// an assignment and not a cast, so it is transcribed as one.
    /// </para>
    /// <para>
    /// <b>The tie rule is not the reference's, and cannot be until somebody
    /// measures <c>ds_priority_delete_min</c>.</b> GM8's priority queue is a
    /// heap and which of two equal keys it evicts is an implementation detail
    /// this project has not established. The port breaks ties by roster index,
    /// lowest first, so the answer is at least deterministic and a saved world
    /// reproduces it. Two players on identical round points is common on an
    /// empty server and rare in a real round.
    /// </para>
    /// </remarks>
    public static int SelectMvps(ref WorldState state, Team team, Span<ArenaMvp> into)
    {
        Span<int> candidates = stackalloc int[WorldState.MaxSlots];
        var count = 0;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.HasPlayer[slot] && state.Players[slot].Team == team)
                candidates[count++] = slot;
        }

        candidates = candidates[..count];

        // Selection sort rather than a heap: at most 64 candidates and at most
        // three picks, and it keeps the tie rule visible — a later slot never
        // displaces an earlier one on equal points.
        var taken = Math.Min(Math.Min(count, MaxMvps), into.Length);

        for (var i = 0; i < taken; i++)
        {
            var best = i;

            for (var j = i + 1; j < count; j++)
            {
                if (GetRound(state, candidates[j], StatIndex.Points)
                    > GetRound(state, candidates[best], StatIndex.Points))
                {
                    best = j;
                }
            }

            (candidates[i], candidates[best]) = (candidates[best], candidates[i]);

            var slot = candidates[i];

            // The write-back, before the read — the reference's own order.
            state.Players[slot].RoundStats[(int)StatIndex.Healing] =
                Gml.Round(state.Players[slot].RoundStats[(int)StatIndex.Healing]);

            into[i] = new ArenaMvp(
                (byte)slot,
                WireQuantise.ToUByte(GetRound(state, slot, StatIndex.Kills)),
                WireQuantise.ToUShort(GetRound(state, slot, StatIndex.Healing)),
                WireQuantise.ToUByte(GetRound(state, slot, StatIndex.Points)));
        }

        return taken;
    }

    /// <summary>
    /// <c>doEventDestruction.gml:20-24</c>: a sentry pulled down by somebody
    /// else.
    /// </summary>
    /// <remarks>
    /// <b><c>killer != owner</c></b>, so an engineer who detonates their own
    /// sentry scores nothing — and neither does the generator explosion, whose
    /// killer is nobody. That is the whole guard; there is no team test, so
    /// destroying a team-mate's sentry does score.
    /// </remarks>
    public static void RecordDestruction(ref WorldState state, int ownerSlot, int killer)
    {
        if (killer == CharacterState.Nobody || killer == ownerSlot || !state.HasPlayer[killer])
            return;

        Award(ref state, killer, StatIndex.Destruction, 1);
        Award(ref state, killer, StatIndex.Points, 1);
    }
}
