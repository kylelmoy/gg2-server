using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// What happens when a character's hp reaches zero: who gets the credit, and
/// the body going away.
/// </summary>
/// <remarks>
/// <c>Character.events/End Step.xml:34-47</c> — the host's own check, run after
/// the stair descent and <em>before</em> the hp clamp, and it <c>exit</c>s, so
/// nothing else in End Step runs for a character that has just died. See
/// <see cref="CharacterPhysics.EndStep"/>, which returns that decision rather
/// than acting on it: the physics knows one character and attribution needs the
/// whole roster.
/// <para>
/// <b>Everything <c>doEventPlayerDeath.gml</c> does that is not here is
/// presentation or scoring.</b> Gibs, blood, corpses, tombstones, hats, ghosts,
/// the death cam, the kill log and the stats table are all client-side or
/// server bookkeeping that no client can re-simulate, and none of them can move
/// a character. What is left of that script is two lines: destroy the body, and
/// tell everybody.
/// </para>
/// <para>
/// <b>What is deliberately still missing.</b> Dropping carried intel:
/// <c>Destroy.xml</c> calls <c>doEventDropIntel</c> and the objective still
/// lives in <c>Gg2.Server.MatchState</c>; it arrives with the objective.
/// Respawn is here since phase 6 — <see cref="Kill"/> arms
/// <see cref="PlayerState.RespawnTimer"/> and <see cref="Timers"/>' alarm-5
/// stage runs <c>PlayerSpawn</c>.
/// </para>
/// </remarks>
public static class Deaths
{
    /// <summary>
    /// Who gets credit for a character that is about to die.
    /// </summary>
    /// <remarks>
    /// <c>End Step.xml:35-40</c>, four lines that are easy to read wrongly:
    /// <code>
    /// assistant = secondToLastDamageDealer;
    /// with(lastDamageDealer)
    ///     if (object)
    ///         if (object.healer)
    ///             assistant = object.healer;
    /// </code>
    /// <list type="bullet">
    /// <item>
    /// The <c>with</c> is over the <b>killer's player</b>, so <c>object</c> is
    /// the killer's current character and <c>object.healer</c> is the medic
    /// beaming <em>them</em>. A medic who kept the killer alive through the
    /// fight takes the assist off the victim's second dealer entirely; it is not
    /// a tie-break between the two.
    /// </item>
    /// <item>
    /// <c>if (object)</c> is what makes a dead killer safe.
    /// <c>Destroy.xml</c> writes <c>player.object = -1</c>, and GM8's truth test
    /// is <c>&gt; 0.5</c> — so a killer with no character skips the redirect
    /// rather than dereferencing -1. (That the guard exists at all is the proof
    /// that -1 is falsy in GM8: were it true, this line would error out every
    /// time a trade killed both players.) Here it is
    /// <see cref="WorldState.IsAlive"/>.
    /// </item>
    /// <item>
    /// <c>healer</c> holds a <b>player</b> instance or -1
    /// (<c>Create.xml:139</c>, <c>Medigun.events/User Event 1.xml:80</c>), so
    /// the same <c>&gt; 0.5</c> test means "somebody is healing them". Here it
    /// is <see cref="CharacterState.Nobody"/>, which is 255 and not 0 —
    /// a slot-0 healer is a real healer.
    /// </item>
    /// </list>
    /// <para>
    /// Nothing here checks that the killer is not the victim, or that the
    /// assistant is not the killer. The reference does not either at this point:
    /// <c>doEventPlayerDeath</c> makes those distinctions when it hands out
    /// points, and the message carries whatever these fields say.
    /// </para>
    /// </remarks>
    public static PlayerDeathEvent Resolve(in WorldState state, int slot)
    {
        ref readonly var victim = ref state.Characters[slot];

        var killer = victim.LastDamageDealer;
        var assistant = victim.SecondToLastDamageDealer;

        if (killer != CharacterState.Nobody && state.IsAlive(killer))
        {
            var healer = state.Characters[killer].Healer;
            if (healer != CharacterState.Nobody)
                assistant = healer;
        }

        return new PlayerDeathEvent(slot, killer, assistant, victim.LastDamageSource);
    }

    /// <summary>
    /// Kills a slot's character: resolves the credit, reports it, and removes
    /// the body.
    /// </summary>
    /// <remarks>
    /// <see cref="WorldState.Clear"/> and not <see cref="WorldState.RemoveSlot"/>
    /// — the player is still connected and still holds their roster index, and
    /// compacting here would hand every slot above them to the wrong person.
    /// Clearing takes the weapon with it (<c>Destroy.xml</c> destroys
    /// <c>currentWeapon</c>) and leaves the sentry and any projectile already in
    /// the air, which is the reference's own split.
    /// <para>
    /// <b>One thing <c>Destroy.xml</c> does that this does not.</b> It drops
    /// carried intel (<c>doEventDropIntel</c>), which needs an objective in
    /// <see cref="WorldState"/> rather than in <c>Gg2.Server.MatchState</c> —
    /// that arrives with the objective itself. The respawn arming it also does
    /// is below, now that the player and their timer live in the state.
    /// </para>
    /// </remarks>
    /// <param name="world">
    /// The map, for the spawn-room half of the respawn guard. ⚠️ <b>Null means
    /// "no spawn rooms"</b>, which is what a hand-built test world has and what
    /// every caller without a map wants — the same convention
    /// <see cref="MatchSimulation.Tick"/>'s spawn points use.
    /// </param>
    public static void Kill(
        ref WorldState state, int slot, ref TickEvents events, CollisionWorld? world = null)
    {
        if (!state.IsAlive(slot))
            return;

        var death = Resolve(state, slot);
        events.Deaths[slot] = death;
        events.Died[slot] = true;

        // The invulnerability the body still had, read before Clear wipes it.
        var protection = state.Characters[slot].DeathmatchInvulnerable;

        // Character.events/Destroy.xml, inside `if (intel)` and below the
        // implicit drop: ⚠️ **a carrier killed by one of the three map boxes
        // sends the briefcase home instead of leaving it in the pit.**
        //
        // ⚠️ What decides it is the source the *box* wrote, and a box that
        // handed the kill to somebody else wrote FINISHED_OFF — so a stolen box
        // kill leaves the flag in the pit. KillBoxTests has the control.
        //
        // The reference reads `lastDamageSource`; `death.Source` is the same
        // value today, because Resolve copies the field verbatim. Reading the
        // field anyway is not a distinction without a difference — it is what
        // keeps this correct if Resolve ever starts transforming the source, as
        // the reference's own kill log does for its own purposes.
        //
        // ⚠️ Placed here rather than in WorldState.Clear, where the drop is,
        // because the return is **broadcast** (sendEventReturnIntel) where the
        // drop deliberately is not — and only a death can carry one of these
        // three sources, since a box sets hp to 0 the moment it is touched.
        var boxSource = state.Characters[slot].LastDamageSource
            is DamageSource.KillBox or DamageSource.FragBox or DamageSource.Pitfall;

        var carriedFlag = state.Characters[slot].CarryingIntel
            ? state.Characters[slot].Team switch
            {
                Team.Red => Team.Blue,
                Team.Blue => Team.Red,
                _ => Team.Spectator,
            }
            : Team.Spectator;

        // And the other half of the same guard, also before Clear: the body has
        // to still be somewhere for `place_meeting` to ask about it.
        var inSpawnRoom = world is not null
            && world.MeetsSpawnRoom(
                state.Characters[slot].Box, state.Characters[slot].X, state.Characters[slot].Y);

        // Also before Clear: four of the six scoring clauses read the victim's
        // *character* — its weapon for the medic bonus, and whether it was
        // carrying a briefcase for the defence. doEventPlayerDeath does its
        // scoring at the top and destroys the body two hundred lines later.
        Scoring.RecordDeath(ref state, slot, in death);

        CountTeamDeathmatchKill(ref state, slot, in death);

        // Clear performs the drop, which is what puts the flag on the ground
        // where the body was. The return below then picks it back up, in that
        // order, because the reference does both and in that order.
        state.Clear(slot);

        if (boxSource && carriedFlag is Team.Red or Team.Blue)
            Intel.Return(ref state, carriedFlag, ref events);

        if (!state.HasPlayer[slot])
            return;

        // Character.events/Destroy.xml:37-46.
        //
        // ⚠️ A character that dies **while still invulnerable** comes back on
        // the *next tick* with whatever protection was left, rather than serving
        // the respawn time — so killing somebody through their spawn bubble buys
        // nothing at all.
        //
        // ⚠️ **And so does one that dies inside a spawn room**, which is the
        // other arm of the same `or`. Note it is `place_meeting` — the box —
        // where the class-change swap on the *same object* uses
        // `collision_point`, the origin. See CollisionWorld.MeetsSpawnRoom.
        //
        // The protection test is fuzzy, for the same reason the clamp in
        // CharacterPhysics.EndStep is: GML has no exact real comparison, and a
        // residue of 7e-14 here would give somebody an eternal one-tick respawn.
        if (inSpawnRoom || !Gml.Equal(protection, 0))
        {
            state.Players[slot].RespawnTimer = 1;
            state.Players[slot].DeathmatchRespawnBypass = protection;
            return;
        }

        // `player.deathmatch_respawn_bypass = 0` — the else arm writes it too,
        // so an unprotected death clears whatever the last one carried.
        state.Players[slot].DeathmatchRespawnBypass = 0;

        // `player.alarm[5] = Server_Respawntime`. A RespawnTime of 0 means
        // respawn is off entirely, which is the parity replayer's mode; see
        // WorldState.RespawnTime.
        if (state.RespawnTime > 0)
            state.Players[slot].RespawnTimer = state.RespawnTime;
    }

    /// <summary>
    /// <c>doEventPlayerDeath.gml:307-317</c>: Team Deathmatch's scoreboard.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The kills go into the capture counters</b>,
    /// <c>global.redCaps</c>/<c>global.blueCaps</c> — Team Deathmatch has no
    /// score of its own, which is why the mode needs no per-player statistics to
    /// decide a round and why its HUD draws the counters where CTF draws
    /// captures.
    /// <para>
    /// ⚠️ <b>Three guards, and each excludes a real case.</b>
    /// <c>instance_exists(TeamDeathmatchHUD)</c> — no other mode counts;
    /// <c>killer</c>, which is GM8's <c>&gt; 0.5</c> truth test, so a death with
    /// no killer scores nothing; <c>killer != victim</c>, so a suicide scores
    /// nothing; and <c>killer.team != victim.team</c>, so a team kill scores
    /// nothing <em>for either side</em> — it is not a point for the enemy.
    /// </para>
    /// <para>
    /// It reads the killer's <b>player</b>, which outlives their character, so a
    /// trade in which both die still credits the one who struck first.
    /// </para>
    /// </remarks>
    private static void CountTeamDeathmatchKill(ref WorldState state, int victimSlot, in PlayerDeathEvent death)
    {
        if (state.GameMode != GameMode.TeamDeathmatch)
            return;

        var killer = death.Killer;

        if (killer == CharacterState.Nobody || killer == victimSlot || !state.HasPlayer[killer])
            return;

        var victimTeam = state.Characters[victimSlot].Team;
        var killerTeam = state.Players[killer].Team;

        if (killerTeam == victimTeam)
            return;

        if (killerTeam == Team.Red)
            state.RedCaps++;
        else if (killerTeam == Team.Blue)
            state.BlueCaps++;
    }

    /// <summary>
    /// Whether a character is dead, as <c>End Step.xml:34</c> asks it.
    /// </summary>
    /// <remarks>
    /// Through <see cref="Gml.LessOrEqual"/> because GML's comparisons carry a
    /// 1e-13 fuzz and this one is reachable: afterburn subtracts a fractional
    /// amount every tick, so an hp that ordinary cancellation left at
    /// <c>+2.7e-14</c> is dead in the reference and alive to a raw
    /// <c>&lt;= 0</c>.
    /// </remarks>
    public static bool IsDead(in CharacterState character) => Gml.LessOrEqual(character.Hp, 0);
}
