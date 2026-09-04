using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The simulation half of <c>processClientCommands.gml</c>'s
/// <c>PLAYER_CHANGECLASS</c> and <c>PLAYER_CHANGETEAM</c> cases: what a class
/// or team change does to the world.
/// </summary>
/// <remarks>
/// The socket half — reading the byte, the broadcasts and their order — stays
/// in <c>Gg2.Server</c>; what lives here is everything that touches
/// <see cref="WorldState"/>, so the live server and the parity harness share
/// one copy of it.
/// <para>
/// <b>A change with a live character is a death.</b> The reference sends
/// <c>DAMAGE_SOURCE_BID_FAREWELL</c> — the player killed themself by leaving
/// the body — unless somebody had damaged them recently, in which case that
/// somebody gets a <c>DAMAGE_SOURCE_FINISHED_OFF</c> kill with the usual
/// killer's-healer assist redirect. The death is returned rather than put in a
/// <see cref="TickEvents"/> because a client command is processed <em>outside</em>
/// <see cref="MatchSimulation.Tick"/>, and the caller broadcasts it inline —
/// before the change message itself, which is the reference's stream order.
/// </para>
/// <para>
/// Two reference clauses are deliberately not here: the spawn-room check on a
/// class change (<c>collision_point(x, y, SpawnRoom, ...)</c> — a map object
/// this port does not model, same as the sentry build precondition), so a
/// class change in a spawn room kills where the reference silently swaps; and
/// <c>checkClasslimits</c>, which no port path enforces yet.
/// </para>
/// </remarks>
public static class PlayerCommands
{
    /// <summary>
    /// <c>PLAYER_CHANGECLASS</c>: kill the body if there is one, remember the
    /// class for the next spawn.
    /// </summary>
    /// <returns>The death to broadcast, if the change killed a character.</returns>
    /// <param name="world">
    /// The map, for the spawn-room swap. <b>Null means "no spawn rooms"</b>,
    /// so a hand-built test world always takes the death arm — the same
    /// convention <see cref="Deaths.Kill"/> uses.
    /// </param>
    public static PlayerDeathEvent? ChangeClass(
        ref WorldState state, int slot, PlayerClass newClass, CollisionWorld? world = null)
    {
        if ((uint)slot >= WorldState.MaxSlots || !state.HasPlayer[slot])
            return null;

        PlayerDeathEvent? death = null;

        if (state.IsAlive(slot))
        {
            // processClientCommands.gml:84,
            // `if (collision_point(x, y, SpawnRoom, 0, 0) < 0)` — the character's
            // *origin*, not its box, and the else arm is a bare
            // `instance_destroy()`. So a class change inside a spawn room is a
            // **silent swap**: no PLAYER_DEATH, no kill credit, no death stat,
            // and — because the Destroy event still runs — a one-tick respawn
            // on top.
            //
            // The two guards on the same object disagree: this one is the
            // point and the Destroy event's is the box, so a character with its
            // feet outside the room and its head inside is killed *and* comes
            // back next tick.
            if (world is not null
                && world.PointInSpawnRoom(state.Characters[slot].X, state.Characters[slot].Y))
            {
                Vanish(ref state, slot, world);
            }
            else
            {
                death = BidFarewell(ref state, slot, world);
            }
        }
        else if (state.Players[slot].RespawnTimer <= 0)
        {
            // `else if (player.alarm[5] <= 0) player.alarm[5] = 1;` — a
            // characterless player with no respawn pending spawns in this same
            // tick's alarm stage; one already counting down is left counting.
            state.Players[slot].RespawnTimer = 1;
        }

        state.Players[slot].Class = newClass;
        return death;
    }

    /// <summary>
    /// <c>PLAYER_CHANGETEAM</c>'s effect on the world, once the team has been
    /// resolved — kill the body if there is one, remember the team.
    /// </summary>
    /// <remarks>
    /// Unlike a class change, a team change with a live character re-arms
    /// the respawn timer <em>explicitly</em> after the death
    /// (<c>processClientCommands.gml:175</c>) — which in the reference
    /// overrides the spawn-room fast respawn the Destroy event may have set.
    /// The two are indistinguishable here while spawn rooms are unmodelled,
    /// but the line is kept where the reference keeps it so they stay
    /// indistinguishable for the right reason.
    /// </remarks>
    /// <returns>The death to broadcast, if the change killed a character.</returns>
    public static PlayerDeathEvent? ChangeTeam(ref WorldState state, int slot, Team newTeam)
    {
        if ((uint)slot >= WorldState.MaxSlots || !state.HasPlayer[slot])
            return null;

        PlayerDeathEvent? death = null;

        if (state.IsAlive(slot))
        {
            death = BidFarewell(ref state, slot);

            if (state.RespawnTime > 0)
                state.Players[slot].RespawnTimer = state.RespawnTime;
        }
        else if (state.Players[slot].RespawnTimer <= 0)
        {
            state.Players[slot].RespawnTimer = 1;
        }

        state.Players[slot].Team = newTeam;
        return death;
    }

    /// <summary>
    /// The whole of <c>processClientCommands.gml</c>'s <c>PLAYER_CHANGETEAM</c>
    /// case, once the request has been resolved.
    /// </summary>
    /// <remarks>
    /// <c>:180-189</c>, in the reference's own order: <c>checkClasslimits</c>
    /// first — so a player pushed onto a full class is re-classed <em>before</em>
    /// the team is announced — then the change, then
    /// <c>ServerBalanceTeams()</c>.
    /// <para>
    /// <b>The class change is broadcast separately and only when it
    /// actually changed</b> (<c>if newClass != player.class</c>), which is why
    /// this reports it rather than folding it into the team change.
    /// </para>
    /// </remarks>
    public static PlayerDeathEvent? ChangeTeamWithBalance(
        ref WorldState state,
        int slot,
        Team newTeam,
        ref TickEvents events,
        out PlayerClass? reclassed)
    {
        reclassed = null;

        if ((uint)slot >= WorldState.MaxSlots || !state.HasPlayer[slot])
            return null;

        var wanted = state.Players[slot].Class;
        var allowed = Classlimits.Pick(ref state, slot, newTeam, wanted);

        if (allowed != wanted)
        {
            state.Players[slot].Class = allowed;
            reclassed = allowed;
        }

        var death = ChangeTeam(ref state, slot, newTeam);

        // processClientCommands.gml:188, between the team message and the
        // balance. A *class* change does not do this — only a team change
        // cancels the dominations a player holds and the ones held over them.
        Dominations.Clear(ref state, slot);

        TeamBalance.Check(ref state, ref events);

        return death;
    }

    /// <summary>
    /// <c>Scripts/GameServer/serverToggleZoom.gml</c> and
    /// <c>Scripts/Misc/toggleZoom.gml</c>: a sniper raises or lowers the scope.
    /// </summary>
    /// <returns>Whether the toggle was accepted and should be broadcast.</returns>
    /// <remarks>
    /// <b>Zoom is not a key.</b> It is its own client command, which is why
    /// the reference extracted this into a script of its own — a bot has no
    /// socket to send itself a packet on and there is no bit in the input byte
    /// it could set instead.
    /// <para>
    /// <b>It changes the physics.</b> A scoped sniper accelerates at
    /// <b>0.6</b> rather than their class's 0.9 and jumps two units lower — but
    /// <c>basemaxspeed</c> is <em>not</em> recomputed, so the acceleration
    /// <em>guard</em> stays at the unscoped value even though the speed they
    /// settle at drops with the push. See
    /// <see cref="CharacterState.RunPower"/>.
    /// </para>
    /// <para>
    /// The two preconditions are the reference's: a live character, and the
    /// sniper class. Neither is checked again anywhere, so a class change while
    /// scoped leaves nothing to unset — the character is destroyed by it.
    /// </para>
    /// </remarks>
    public static bool ToggleZoom(ref WorldState state, int slot)
    {
        if (!state.IsAlive(slot))
            return false;

        ref var character = ref state.Characters[slot];

        if (character.Class != PlayerClass.Sniper)
            return false;

        character.Zoomed = !character.Zoomed;

        if (character.Zoomed)
        {
            // 0.6 is one of the five decimal literals GM8 parses a ULP above
            // the IEEE nearest, and it appears twice here — once as the run
            // power and once inside the jump's halved-gravity correction.
            character.RunPower = Gml.PointSix;
            character.JumpStrength = 6 + (Gml.PointSix / 2);
        }
        else
        {
            character.RunPower = ClassStats.For(character.Class).RunPower;
            character.JumpStrength = ClassStats.BaseJumpStrength;
        }

        // toggleZoom also writes the weapon's hitDamage, which the Rifle's own
        // Begin Step recomputes from `t` on the very next tick anyway — see
        // Weapons.WeaponBeginStep. Writing it here as well would be
        // transcribing structure rather than arithmetic.
        return true;
    }

    /// <summary>
    /// The team a <c>PLAYER_CHANGETEAM</c> request actually lands on, or null
    /// for a request the server refuses.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml</c>'s balance block: anything that is not
    /// RED, BLUE or SPECTATOR is a request for "any"; "any" resolves to the
    /// smaller team, or to <c>choose(TEAM_RED, TEAM_BLUE)</c> — a draw off the
    /// global stream, free like every server-side draw — when they are level.
    /// The request is then refused when it names the <em>bigger</em> team
    /// (<c>balance != newTeam</c>, counting every player but the requester) or
    /// the team the player is already on. A refused request does nothing at
    /// all: no death, no broadcast.
    /// </remarks>
    public static Team? ResolveRequestedTeam(ref WorldState state, int slot, Team requested)
    {
        if (requested is not (Team.Red or Team.Blue or Team.Spectator))
            requested = Team.Any;

        // redSuperiority, counting every player except the requester.
        var superiority = 0;
        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            if (!state.HasPlayer[i] || i == slot)
                continue;

            if (state.Players[i].Team == Team.Red)
                superiority += 1;
            else if (state.Players[i].Team == Team.Blue)
                superiority -= 1;
        }

        Team? balance = superiority > 0 ? Team.Red : superiority < 0 ? Team.Blue : null;

        if (requested == Team.Any)
        {
            requested = balance switch
            {
                Team.Red => Team.Blue,
                Team.Blue => Team.Red,

                // choose(TEAM_RED, TEAM_BLUE): choose(...) is random(count)
                // floored over the arguments.
                _ => (int)state.Random.Next(2) == 0 ? Team.Red : Team.Blue,
            };
        }

        if (balance == requested || requested == state.Players[slot].Team)
            return null;

        return requested;
    }

    /// <summary>
    /// The death a class or team change deals to a live character:
    /// <c>BID_FAREWELL</c> by their own hand, or <c>FINISHED_OFF</c> for
    /// whoever had already hurt them.
    /// </summary>
    /// <remarks>
    /// The assist logic is <see cref="Deaths.Resolve"/>'s — second-to-last
    /// dealer, replaced outright by the killer's healer — but the killer
    /// arrives differently: <c>!instance_exists(lastDamageDealer) ||
    /// lastDamageDealer == player</c> picks the farewell arm, where an
    /// hp-death never asks. Clearing the slot takes the weapon and leaves the
    /// sentry, and arms the respawn exactly as <see cref="Deaths.Kill"/> does —
    /// the arming is <c>Character.events/Destroy.xml</c>'s, shared by every
    /// way a character can stop existing.
    /// </remarks>
    private static PlayerDeathEvent BidFarewell(
        ref WorldState state, int slot, CollisionWorld? world = null)
    {
        ref readonly var victim = ref state.Characters[slot];
        var dealer = victim.LastDamageDealer;

        PlayerDeathEvent death;

        if (dealer == CharacterState.Nobody || dealer == slot)
        {
            death = new PlayerDeathEvent(
                slot, (byte)slot, CharacterState.Nobody, DamageSource.BidFarewell);
        }
        else
        {
            var assistant = victim.SecondToLastDamageDealer;

            if (state.IsAlive(dealer) && state.Characters[dealer].Healer != CharacterState.Nobody)
                assistant = state.Characters[dealer].Healer;

            death = new PlayerDeathEvent(slot, dealer, assistant, DamageSource.FinishedOff);
        }

        Vanish(ref state, slot, world);
        return death;
    }

    /// <summary>
    /// <c>instance_destroy()</c> on a character, and the respawn its Destroy
    /// event arms.
    /// </summary>
    /// <remarks>
    /// Shared by the silent spawn-room swap and by <see cref="BidFarewell"/>,
    /// because in the reference they are the same thing: both end with the
    /// character destroyed, and <c>Character.events/Destroy.xml:37-46</c> runs
    /// either way. What differs is only whether a <c>PLAYER_DEATH</c> went out
    /// first.
    /// <para>
    /// <b>The fast-respawn arm is not guarded by
    /// <see cref="WorldState.RespawnTime"/></b>, exactly as
    /// <see cref="Deaths.Kill"/>'s is not: the reference writes
    /// <c>alarm[5] = 1</c> outright. A replay is protected by its null
    /// <c>world</c>, and by spawning nobody in any case — see
    /// <see cref="MatchSimulation.Tick"/>'s spawn points.
    /// </para>
    /// </remarks>
    private static void Vanish(ref WorldState state, int slot, CollisionWorld? world)
    {
        ref readonly var victim = ref state.Characters[slot];

        var inSpawnRoom = world is not null
            && world.MeetsSpawnRoom(victim.Box, victim.X, victim.Y);
        var protection = victim.DeathmatchInvulnerable;

        state.Clear(slot);

        if (inSpawnRoom || !Gml.Equal(protection, 0))
        {
            state.Players[slot].RespawnTimer = 1;
            state.Players[slot].DeathmatchRespawnBypass = protection;
            return;
        }

        state.Players[slot].DeathmatchRespawnBypass = 0;

        if (state.RespawnTime > 0)
            state.Players[slot].RespawnTimer = state.RespawnTime;
    }

    /// <summary>
    /// <c>serverEatSandvich.gml</c>: a heavy starts eating. False when any
    /// precondition fails, which is silence rather than an error.
    /// </summary>
    /// <remarks>
    /// All five of the reference's clauses, including <c>player.humiliated</c>
    /// and <c>object.taunting</c>: a heavy cannot eat while taunting, and the
    /// losing side of an Arena round cannot eat at all.
    /// <para>
    /// <b>The heal is not here.</b> <c>serverEatSandvich</c> only starts the
    /// animation; every point of health comes from
    /// <c>Character.events/End Step.xml:83-96</c>, a tick at a time, and stops
    /// if the character dies mid-eat. Healing on the command instead would give
    /// a heavy 200 hp the instant they pressed it.
    /// </para>
    /// </remarks>
    public static bool EatSandvich(ref WorldState state, int slot)
    {
        if (!state.IsAlive(slot))
            return false;

        ref var character = ref state.Characters[slot];

        if (character.Class != PlayerClass.Heavy)
            return false;

        // Already eating is a refusal, not a restart. Without it a heavy
        // holding the key would hold `omnomnomnomindex` at zero and eat forever.
        if (character.Omnomnomnom || !character.CanEat)
            return false;

        // `!player.humiliated and !player.object.taunting`. The humiliation
        // is read off the PLAYER rather than the mirrored copy on the character,
        // because a client command is processed outside the tick and the mirror
        // is written inside it.
        if (state.Players[slot].Humiliated || character.Taunting)
            return false;

        character.Omnomnomnom = true;
        character.OmnomnomnomIndex = 0;

        // `xscale = image_xscale`, and End Step writes it back every tick of the
        // eat. See CharacterState.EatFacing for why the server needs it.
        character.EatFacing = character.Facing;

        return true;
    }
}
