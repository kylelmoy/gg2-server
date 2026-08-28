using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// One weapon firing: what <c>sendEventFireWeapon.gml</c> puts on the wire.
/// </summary>
/// <remarks>
/// The position and speeds are the <b>character's</b> and are taken at the
/// moment of the shot — Begin Step, before this tick's acceleration — because
/// that is when the reference reads them. They are not the muzzle: a shot
/// leaves from <see cref="WeaponState.X"/>, which trails the character by a
/// tick and a rounding.
/// </remarks>
/// <param name="Slot">The roster index that fired.</param>
/// <param name="Seed">
/// The 16-bit seed the shot's spread was drawn from. Every client re-derives
/// the pellets from it, so it is contract rather than a diagnostic — see
/// <see cref="Gg2.Protocol.GmlRandom"/>.
/// </param>
public readonly record struct WeaponFireEvent(
    int Slot,
    ushort Seed,
    double X,
    double Y,
    double HSpeed,
    double VSpeed);

/// <summary>
/// One character dying: what <c>sendEventPlayerDeath.gml</c> puts on the wire.
/// </summary>
/// <remarks>
/// All three roster fields are slots, and <see cref="CharacterState.Nobody"/> is
/// the empty one — which is 255 on the wire and never 0.
/// <para>
/// <b>The killer is a <em>player</em>, not a character, and it is read at the
/// moment of death rather than at the moment of the damage.</b> That is what
/// lets a rocket already in the air credit somebody who has since died, and it
/// is why <see cref="CharacterState.LastDamageDealer"/> survives the character
/// that dealt the damage.
/// </para>
/// </remarks>
/// <param name="Slot">The roster index whose character died.</param>
/// <param name="Killer">Who dealt the fatal damage, or <see cref="CharacterState.Nobody"/>.</param>
/// <param name="Assistant">
/// Who assisted. Normally the victim's second-to-last damage dealer, but
/// redirected to the killer's own medic where there is one — see
/// <see cref="Deaths.Resolve"/>.
/// </param>
/// <param name="Source">What the fatal damage was. Drives the client's kill log.</param>
public readonly record struct PlayerDeathEvent(
    int Slot,
    byte Killer,
    byte Assistant,
    DamageSource Source);

/// <summary>
/// One character spawning: what <c>sendEventSpawn.gml</c> puts on the wire as
/// <c>PLAYER_SPAWN</c>.
/// </summary>
/// <remarks>
/// The message carries the spawn point <em>id and group</em>, never a position
/// — every client resolves the pair against its own copy of the map
/// (docs/PARITY.md's anchor property rides on this). The id is the tick's own
/// <c>floor(random(n))</c> draw, which like a fire seed is server-side RNG the
/// port is free about precisely because it is broadcast.
/// </remarks>
/// <param name="Slot">The roster index that spawned.</param>
/// <param name="SpawnPointId">Index into the team's spawn list for the group.</param>
/// <param name="Group">The forward-spawn group; 0 in every mode the port runs.</param>
public readonly record struct PlayerSpawnEvent(
    int Slot,
    byte SpawnPointId,
    byte Group);

/// <summary>
/// One sentry being destroyed: what <c>sendEventDestruction.gml</c> puts on the
/// wire as <c>DESTROY_SENTRY</c>.
/// </summary>
/// <remarks>
/// The same slot-or-255 convention as a death, and the same assist source: the
/// assistant is the <em>killer's</em> medic
/// (<c>Sentry.events/End Step.xml</c>, <c>with(lastDamageDealer) if (object)
/// if (object.healer)</c>) — a sentry has no second-to-last dealer to demote,
/// so unlike a character death the assistant is the healer or nobody.
/// </remarks>
/// <param name="Slot">The roster index whose sentry was destroyed.</param>
/// <param name="Killer">Who dealt the fatal damage, or <see cref="CharacterState.Nobody"/>.</param>
/// <param name="Assistant">The killer's healer, or <see cref="CharacterState.Nobody"/>.</param>
/// <param name="Source">What the fatal damage was, or <see cref="DamageSource.None"/> for the self-destruct.</param>
public readonly record struct SentryDestroyedEvent(
    int Slot,
    byte Killer,
    byte Assistant,
    DamageSource Source);

/// <summary>
/// What one tick did that the world state alone does not record.
/// </summary>
/// <remarks>
/// <b>Not part of <see cref="WorldState"/>, on purpose.</b> A weapon firing is
/// an event, not a fact about the world afterwards: the state carries the
/// pellets and the emptied magazine, and nothing in it says a
/// <c>WEAPON_FIRE</c> has to be broadcast. Keeping the two apart is what lets a
/// bot rollout run thousands of speculative ticks without accumulating a
/// message queue it will throw away, and keeps the state a plain <c>memcpy</c>
/// (docs/BOTAI.md item 1).
/// <para>
/// It is a value type with inline storage for the same reason
/// <see cref="WorldState"/> is: the caller owns it, reuses it across ticks, and
/// nothing here allocates.
/// </para>
/// </remarks>
public struct TickEvents
{
    // --- inputs ---------------------------------------------------------------
    //
    // The one thing a tick is *told* rather than deciding: which seed a shot
    // must use. The live server never sets these and draws from
    // WorldState.Random instead.

    /// <summary>
    /// Whether <see cref="ForcedFireSeed"/> holds a seed for this slot's next
    /// shot.
    /// </summary>
    /// <remarks>
    /// <b>The parity harness's injection point, and it has to exist.</b> The
    /// host draws <c>irandom(65535)</c> and broadcasts it
    /// (<c>Scattergun.events/User Event 1.xml</c>), so a recording contains the
    /// seed the reference used — and a replay that drew its own would scatter
    /// its pellets differently and disagree about hp for reasons that have
    /// nothing to do with the code under test. Which seed is drawn is
    /// server-side RNG the port is free about (docs/PARITY.md); what is done
    /// with it is not.
    /// <para>
    /// A separate flag rather than a sentinel value, because every one of the
    /// 65,536 seeds is legal and a defaulted zero is a perfectly good one.
    /// </para>
    /// </remarks>
    public SlotBuffer<bool> HasForcedFireSeed;

    /// <summary>The seed to use for this slot's next shot, where forced.</summary>
    public SlotBuffer<ushort> ForcedFireSeed;

    // --- outputs --------------------------------------------------------------

    /// <summary>Whether this slot fired during the tick just run.</summary>
    /// <remarks>
    /// Indexed by slot rather than appended to a list because a character can
    /// fire at most once per tick — its weapon's <c>readyToShoot</c> is cleared
    /// by the shot and only an alarm puts it back. Walking slots in ascending
    /// order therefore reproduces the order the reference's Begin Steps wrote
    /// the messages in.
    /// </remarks>
    public SlotBuffer<bool> Fired;

    /// <summary>What each slot that fired fired, where <see cref="Fired"/> is set.</summary>
    public SlotBuffer<WeaponFireEvent> Fires;

    /// <summary>Whether this slot's character died during the tick just run.</summary>
    /// <remarks>
    /// Indexed by slot for the same reason <see cref="Fired"/> is, and with a
    /// stronger guarantee: a character that dies is destroyed, so it cannot die
    /// twice in a tick and nothing can respawn into the slot before the next one
    /// (respawn runs off the player's own alarm, which is a later phase).
    /// Walking slots in ascending order therefore reproduces the order the
    /// reference's End Steps wrote the messages in.
    /// </remarks>
    public SlotBuffer<bool> Died;

    /// <summary>How each slot died, where <see cref="Died"/> is set.</summary>
    public SlotBuffer<PlayerDeathEvent> Deaths;

    /// <summary>
    /// Whether this slot's character spawned during the tick just run —
    /// <c>PLAYER_SPAWN</c>.
    /// </summary>
    /// <remarks>
    /// Set by the respawn stage — <c>Player.events/Alarm 5.xml</c> firing
    /// between Begin Step and Step — so the character it announces has already
    /// run this tick's Step by the time the caller reads it, exactly as a
    /// reference spawn does. At most one per slot per tick: the alarm fires
    /// once and a live character never re-arms it.
    /// </remarks>
    public SlotBuffer<bool> Spawned;

    /// <summary>Where each slot spawned, where <see cref="Spawned"/> is set.</summary>
    public SlotBuffer<PlayerSpawnEvent> Spawns;

    /// <summary>
    /// Whether this slot's medic filled their charge during the tick just run —
    /// <c>UBER_CHARGED</c>.
    /// </summary>
    /// <remarks>
    /// Both uber messages are flags with no body beyond the player they name, so
    /// unlike a fire or a death they need no payload record. Each can happen at
    /// most once per tick per slot: charging is one-way and the flag it sets is
    /// what stops it happening again.
    /// <para>
    /// Sent from <c>Medigun.events/User Event 1.xml</c>, which is inside the
    /// owner's Begin Step — the same stage a <c>WEAPON_FIRE</c> comes from, so
    /// these bytes sit in the same place in the stream.
    /// </para>
    /// </remarks>
    public SlotBuffer<bool> UberCharged;

    /// <summary>
    /// Whether this slot's medic started spending their charge during the tick
    /// just run — <c>UBER</c>.
    /// </summary>
    /// <remarks>
    /// Sent from <c>Medigun.events/User Event 2.xml</c>, the right-click, which
    /// is a few lines below the attack key in the same Begin Step.
    /// </remarks>
    public SlotBuffer<bool> Ubered;

    /// <summary>
    /// Whether this slot's sentry finished falling during the tick just run —
    /// <c>SENTRY_POSITION</c>.
    /// </summary>
    /// <remarks>
    /// Sent from <c>Sentry.events/Begin Step.xml</c> when <c>oldVspeed</c> was
    /// non-zero and <c>vspeed</c> has just become zero, which happens once per
    /// placement. The broadcast position is the sentry's at that moment; it does
    /// not move again, so the server reads it back off the state when it
    /// serialises the message.
    /// </remarks>
    public SlotBuffer<bool> SentryLanded;

    /// <summary>Whether this slot's sentry was destroyed during the tick just run.</summary>
    /// <remarks>
    /// Only the simulated destructions land here — running out of hp in End
    /// Step, or the Begin Step overlap guard. An owner detonating their own
    /// sentry is a client command handled outside the tick, and the reference
    /// broadcasts nothing for it (<c>processClientCommands.gml</c>'s
    /// <c>DESTROY_SENTRY</c> case is a bare <c>instance_destroy</c>); clients
    /// notice through the subobject bit of the next update.
    /// </remarks>
    public SlotBuffer<bool> SentryDestroyed;

    /// <summary>How each destroyed sentry died, where <see cref="SentryDestroyed"/> is set.</summary>
    public SlotBuffer<SentryDestroyedEvent> SentryDestructions;

    /// <summary>
    /// Whether this slot picked the enemy briefcase up during the tick just run
    /// — <c>GRAB_INTEL</c>.
    /// </summary>
    /// <remarks>
    /// All three of these are flags with no payload beyond the player, like the
    /// uber pair: <c>sendEventGrabIntel</c>, <c>sendEventDropIntel</c> and
    /// <c>sendEventScoreIntel</c> each write a message byte and a roster index
    /// and nothing else — the position a drop leaves the flag at is not sent,
    /// because every client runs <c>doEventDropIntel</c> itself and reads it off
    /// the carrier it already has.
    /// <para>
    /// One per slot per tick is enough. A grab is a collision event and there is
    /// one enemy briefcase; a drop comes from the carrier's own Begin Step or
    /// from a client command, and a character that has dropped is no longer
    /// carrying. The one ordering a slot can see in a single tick is
    /// drop-then-grab — an ubered carrier drops in Begin Step and can pick the
    /// same flag straight back up in the collision stage, because its alarm is
    /// then 0 and the guard is <c>alarm[0] &lt;= 1</c>.
    /// </para>
    /// </remarks>
    public SlotBuffer<bool> GrabbedIntel;

    /// <summary>
    /// Whether this slot dropped the briefcase during the tick just run —
    /// <c>DROP_INTEL</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A drop caused by the carrier dying is deliberately absent.</b>
    /// <c>Character.events/Destroy.xml</c> drops the flag without sending
    /// anything (see <see cref="WorldState.Clear"/>), so this is set only by the
    /// ubered drop and by the client's own <c>DROP_INTEL</c> command.
    /// </remarks>
    public SlotBuffer<bool> DroppedIntel;

    /// <summary>
    /// Whether this slot captured during the tick just run —
    /// <c>SCORE_INTEL</c>.
    /// </summary>
    public SlotBuffer<bool> ScoredIntel;

    /// <summary>
    /// Whether the red briefcase returned itself during the tick just run —
    /// <c>RETURN_INTEL</c>.
    /// </summary>
    /// <remarks>
    /// Not indexed by slot, because a return names a <em>team</em> rather than a
    /// player: <c>sendEventReturnIntel</c> writes the team byte, and the flag
    /// returns on its own alarm with nobody to credit. Both can fire on one
    /// tick, so they are two flags and not one team field.
    /// </remarks>
    public bool ReturnedRedIntel;

    /// <inheritdoc cref="ReturnedRedIntel"/>
    public bool ReturnedBlueIntel;

    /// <summary>
    /// Whether the red team's generator was destroyed during the tick just run
    /// — <c>GENERATOR_DESTROY</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The message names the team that <em>lost</em> a generator</b>, and
    /// <c>sendEventGeneratorDestroy.gml</c>'s own docstring says the opposite;
    /// its one caller settles it. Two flags rather than one team field for the
    /// same reason the intel returns are two: both generators can reach zero on
    /// one tick, and the reference then sends both messages.
    /// </remarks>
    public bool RedGeneratorDestroyed;

    /// <inheritdoc cref="RedGeneratorDestroyed"/>
    public bool BlueGeneratorDestroyed;

    /// <summary>
    /// Whether the teams were found uneven this tick — the <c>BALANCE</c>
    /// notice, which names no player.
    /// </summary>
    /// <remarks>
    /// Stage 1 of two. The switch it warns about happens on a later tick and is
    /// reported through <see cref="Balanced"/>; see <see cref="TeamBalance"/>.
    /// </remarks>
    public bool BalanceNoticed;

    /// <summary>
    /// Whether this slot's player was moved to the other team this tick —
    /// <c>BALANCE</c> with an id.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Per slot rather than a single field</b>, because Arena's
    /// between-rounds balance moves as many players as it takes to close the
    /// gap — <c>serverArenaRestart</c> loops where <c>ServerBalanceTeams</c>
    /// moves one.
    /// </remarks>
    public SlotBuffer<bool> Balanced;

    /// <summary>
    /// Hit points this slot dealt to <em>other</em> characters this tick.
    /// </summary>
    /// <remarks>
    /// <b>`docs/ENV.md`'s first prerequisite, and the half of `docs/BOTAI.md`
    /// item 7 that was never built.</b> The rest of this struct carries kills,
    /// deaths, spawns and captures with attribution — the sparse events. A
    /// learned policy wants the <em>dense</em> signal, and item 7's design
    /// forbids reconstructing it by diffing hp between ticks: healing, spawn
    /// invulnerability and a respawn all move hp without anybody dealing
    /// damage.
    /// <para>
    /// ⚠️ <b>Credited at application time, following <c>LastDamageDealer</c>
    /// semantics</b> — so a rocket in the air still credits its shooter after
    /// they have died, exactly as a kill does.
    /// </para>
    /// <para>
    /// ⚠️ <b>Self-damage is never dealt, only taken.</b> A rocket jump must not
    /// score a positive reward, and a soldier who kills himself must not
    /// out-earn one who kills somebody else.
    /// </para>
    /// <para>
    /// ⚠️ <b>Blocked damage accumulates nothing.</b> <c>DamageApi.Deal</c>
    /// returning false is Team Deathmatch invulnerability: no amount landed, so
    /// neither side's counter moves. Note this is deliberately *not* the same
    /// rule the reference uses for <c>timeUnscathed</c>, which it resets even
    /// on a blocked hit.
    /// </para>
    /// <para>
    /// ⚠️ <b>Sentries and generators are out.</b> Their damage does not route
    /// through <c>DamageApi.Deal</c> at all, and `docs/ENV.md` puts them beyond
    /// v0 because a duel has no objective. Damage a sentry's turret deals to a
    /// <em>character</em> is in, because that goes through <c>Deal</c> like
    /// everything else — it is credited to the sentry's owner.
    /// </para>
    /// </remarks>
    public SlotBuffer<double> DamageDealt;

    /// <summary>
    /// Hit points this slot lost to damage this tick, including its own.
    /// </summary>
    /// <remarks>
    /// See <see cref="DamageDealt"/>. ⚠️ <b>This is damage, not hp lost</b> —
    /// falling in a pit, a spawn-room kill box and the generator explosion all
    /// take hp without going through <c>DamageApi.Deal</c>, and none of them
    /// appears here.
    /// </remarks>
    public SlotBuffer<double> DamageTaken;

    /// <summary>
    /// What class they came out as, where <see cref="Balanced"/> — the message
    /// carries it because <c>checkClasslimits</c> may have changed it.
    /// </summary>
    public SlotBuffer<PlayerClass> BalancedClass;

    /// <summary>
    /// Whether an Arena round moved into its setup period this tick —
    /// <c>ARENA_RESTART</c>.
    /// </summary>
    /// <remarks>
    /// The four Arena transitions are broadcast rather than derived, because a
    /// client's own copy of the state machine is driven by these messages and
    /// not by its own countdowns — <c>ClientBeginStep.gml</c> calls the same
    /// <c>doEventArena*</c> the server does. Only <c>ARENA_ENDROUND</c> carries
    /// a body.
    /// </remarks>
    public bool ArenaRestarted;

    /// <summary>Whether the round proper began this tick — <c>ARENA_STARTROUND</c>.</summary>
    public bool ArenaRoundStarted;

    /// <summary>Whether a round was won this tick — <c>ARENA_ENDROUND</c>.</summary>
    public bool ArenaRoundEnded;

    /// <summary>Who won it, where <see cref="ArenaRoundEnded"/>.</summary>
    public Team ArenaRoundWinner;

    /// <summary>
    /// Whether the map dropped back to waiting this tick —
    /// <c>ARENA_WAITFORPLAYERS</c>.
    /// </summary>
    public bool ArenaWaitingForPlayers;

    /// <summary>Forces the seed a slot's next shot uses.</summary>
    public void ForceFireSeed(int slot, ushort seed)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        HasForcedFireSeed[slot] = true;
        ForcedFireSeed[slot] = seed;
    }

    /// <summary>
    /// Clears the outputs, leaving the forced seeds alone.
    /// </summary>
    /// <remarks>
    /// Called at the top of every tick, so a caller that keeps one of these
    /// across a whole match never sees last tick's shots. The seeds survive
    /// because they are supplied <em>before</em> the tick that consumes them;
    /// each is cleared as it is used.
    /// </remarks>
    public void ClearOutputs()
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            Fired[slot] = false;
            Died[slot] = false;
            Spawned[slot] = false;
            UberCharged[slot] = false;
            Ubered[slot] = false;
            SentryLanded[slot] = false;
            SentryDestroyed[slot] = false;
            GrabbedIntel[slot] = false;
            DroppedIntel[slot] = false;
            ScoredIntel[slot] = false;
            Balanced[slot] = false;
            DamageDealt[slot] = 0;
            DamageTaken[slot] = 0;
        }

        ReturnedRedIntel = false;
        ReturnedBlueIntel = false;
        BalanceNoticed = false;
        RedGeneratorDestroyed = false;
        BlueGeneratorDestroyed = false;
        ArenaRestarted = false;
        ArenaRoundStarted = false;
        ArenaRoundEnded = false;
        ArenaWaitingForPlayers = false;
    }
}
