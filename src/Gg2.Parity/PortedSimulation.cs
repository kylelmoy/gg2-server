using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Parity;

/// <summary>
/// The real physics behind the replayer's seam.
/// </summary>
/// <remarks>
/// The adapter docs/PARITY.md's tier 2 was waiting for: it swaps in for
/// <see cref="EmptySimulation"/> so a recorded trace becomes a pass/fail gate on
/// <c>Gg2.Simulation</c> rather than a demonstration that the harness can report
/// a failure.
/// <para>
/// The seam is here rather than in the simulation on purpose — the physics knows
/// nothing about traces, and <c>WorldState</c> is not shaped by an interface
/// written before it existed.
/// </para>
/// </remarks>
/// <summary>
/// How far inside its wire quantisation bin an anchored character is placed.
/// </summary>
/// <remarks>
/// All four components are the amount to <em>add</em> to what the wire said, in
/// world pixels and pixels per tick. Zero is the naive anchor: the bottom of the
/// bin, which is where a replay lands if it simply divides the wire value back.
/// </remarks>
public readonly record struct AnchorNudge(double X, double Y, double HSpeed, double VSpeed);

public sealed class PortedSimulation(Func<string, LevelMap> loadMap) : ITraceSimulation
{
    private WorldState _state;
    private CollisionWorld? _world;
    private SpawnPoints? _spawnPoints;

    /// <summary>
    /// The tick's event channel, kept across ticks so a seed can be supplied
    /// before the tick that consumes it.
    /// </summary>
    /// <remarks>
    /// Reused rather than recreated because it is the thing carrying the forced
    /// seeds: <see cref="SetFireSeed"/> writes into it and
    /// <see cref="Tick"/> hands the same buffer to the simulation, which clears
    /// only the outputs.
    /// </remarks>
    private TickEvents _events;

    /// <summary>
    /// The deaths of the tick just run, refilled in place every tick.
    /// </summary>
    /// <remarks>
    /// A list rather than the <see cref="TickEvents"/> buffer itself because the
    /// buffer is a 64-slot inline array that would have to be walked by every
    /// caller, and because the harness's side of the seam speaks wire bytes
    /// (<see cref="DeathRecord"/>) rather than simulation types. Cleared and
    /// refilled so the steady state allocates nothing after the first death.
    /// </remarks>
    private readonly List<DeathRecord> _deaths = [];

    private readonly List<UberRecord> _ubers = [];

    private readonly List<IntelRecord> _intel = [];

    /// <summary>
    /// A slot's class, off the world's own player state - taken from the
    /// trace's <c>PLAYER_CHANGECLASS</c> messages rather than guessed: a scout
    /// and a heavy are different widths and different speeds, so replaying one
    /// as the other is not a near miss. Scout for a slot no message has named,
    /// which is what the old per-harness dictionary defaulted to.
    /// </summary>
    private PlayerClass ClassOf(int slot) =>
        _state.HasPlayer[slot] ? _state.Players[slot].Class : PlayerClass.Scout;

    /// <summary>
    /// A slot's team, Red for one no message has named - the recordings this
    /// harness replays always name a team before anything observable happens,
    /// so the default only decides which gates a stray anchor can pass.
    /// </summary>
    private Team TeamOf(int slot) =>
        _state.HasPlayer[slot] ? _state.Players[slot].Team : Team.Red;

    /// <summary>
    /// First contact with a slot's player: the harness's old dictionary-miss
    /// defaults, with the respawn alarm at rest. RespawnTime stays 0 on the
    /// whole state, so nothing in a replay ever arms it - the recording's own
    /// <c>PLAYER_SPAWN</c>s are injected instead.
    /// </summary>
    private void EnsurePlayer(int slot)
    {
        if (_state.HasPlayer[slot])
            return;

        _state.Players[slot] = new PlayerState
        {
            Team = Team.Red,
            Class = PlayerClass.Scout,
            RespawnTimer = Timers.Inactive,
        };
        _state.HasPlayer[slot] = true;
    }

    /// <summary>
    /// A per-slot offset applied to an anchored character, inside the wire's own
    /// quantisation.
    /// </summary>
    /// <remarks>
    /// A diagnostic, not a fudge. A trace anchors from the wire, which carries
    /// position to a fifth of a pixel and velocity to <c>1/8.5</c> px/tick — so
    /// the state the reference was actually in is only known to within
    /// <c>±0.1</c>px and <c>±1/17</c>px/tick, and a replay starts from somewhere
    /// inside those bins rather than on the reference's own values. Sweeping the
    /// offset across a bin answers the question the gate actually cares about: is
    /// a residual mismatch a difference in the arithmetic, or just the anchor?
    /// <para>
    /// <b>Per slot, and that matters.</b> Every anchored character has its own
    /// independent bin, so one shared offset is not the experiment — it asks
    /// whether one number happens to suit every character at once, which is a
    /// much stronger and quite different claim. Sweeping a single slot while the
    /// others stay wrong measures the same thing from the other side: the sweep
    /// bottoms out at the *next* slot's residual, not at zero.
    /// </para>
    /// </remarks>
    public Dictionary<int, AnchorNudge> Nudges { get; } = [];

    public void LoadMap(string map)
    {
        // Area 1: a recording is of a fresh round, and the replayer has no
        // JOIN_UPDATE handling to tell it otherwise. Every fixture is on a
        // single-area map, so this is exact today and would need the recording's
        // own currentMapArea if one were ever made on cp_dirtbowl.
        var level = loadMap(map).ForArea(1);
        _world = new CollisionWorld(level);
        _spawnPoints = new SpawnPoints(level);
        _state = default;

        // Both briefcases on their bases, exactly as basicRoomSetup leaves them
        // — the state cannot hold the map, so this is where the base positions
        // are copied in. A recording that starts mid-round with a flag already
        // out is therefore anchored wrongly here, and nothing on the wire can
        // correct it: the intel's position is FULL_UPDATE only. See
        // docs/PARITY.md.
        Intel.Place(ref _state, level);

        // Including any seed supplied for a tick that a map change has now
        // cancelled — the shot it was meant for cannot happen.
        _events = default;
        _deaths.Clear();
        _ubers.Clear();
        _intel.Clear();
    }

    public void SetCharacter(int slot, in CharacterBlock observed)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        var alive = _state.Alive[slot];
        var playerClass = ClassOf(slot);
        var team = TeamOf(slot);

        // ⚠️ CharacterState.Fresh, not `new CharacterState()`. Six of a
        // character's fields name a roster slot and their "nobody" is 255, so a
        // zeroed one claims slot 0 last hurt it, assisted, set it on fire and is
        // healing it — and slot 0 always exists, so nothing complains. This read
        // `new CharacterState()` until 2026-08-25, and it surfaced as a
        // PLAYER_DEATH on the hot-combat trace naming an assistant the reference
        // said was nobody.
        var character = alive ? _state.Characters[slot] : CharacterState.Fresh(playerClass, team);

        character.Class = playerClass;
        character.Team = team;

        // Wire units back to world units. The wire is the lossy side, so this
        // is the best anchor available — and it is exactly what the client does
        // when it applies a QUICK_UPDATE.
        var nudge = Nudges.GetValueOrDefault(slot);
        character.X = observed.X / 5.0 + nudge.X;
        character.Y = observed.Y / 5.0 + nudge.Y;
        character.HSpeed = observed.HSpeed / 8.5 + nudge.HSpeed;
        character.VSpeed = observed.VSpeed / 8.5 + nudge.VSpeed;
        character.Hp = observed.Hp;

        character.KeyState = observed.Keys;

        // ⚠️ LastKeyState is deliberately NOT anchored for a character the
        // replay already has, and this used to be a bug that only appeared one
        // tick in seven.
        //
        // Edge-detected keys — jump, the demoman's right-click, the spy's cloak
        // — fire on `KeyState & ~LastKeyState`. A state update carries this
        // tick's `keys` alongside last tick's position, so anchoring
        // LastKeyState from it means that when a key is first pressed *on an
        // update tick*, ApplyInput immediately computes an empty edge and the
        // press is swallowed. Measured on
        // reference-ctf_truefort-combat.trace: its demoman presses Jump for
        // exactly one tick, tick 9, which is an update tick — the reference
        // climbs 43 px by tick 16 and the port climbed 6.
        //
        // The replay's own input history is the authority here. It has seen
        // every INPUTSTATE, and the update's `keys` is a snapshot of the same
        // thing arriving a tick later.
        if (!alive)
            character.LastKeyState = observed.Keys;

        character.NetAimDirection = observed.NetAimDirection;
        character.AimDistance = observed.AimDistance;

        character.MoveStatus = observed.MoveStatus;
        character.Cloak = observed.Cloaked;

        // ⚠️ The objective is NOT anchored here, and it cannot be through this
        // seam. `observed` is the tick's own update block, and a tick's update
        // is never a FULL_UPDATE — TraceParser files that one under `start` and
        // treats it as an event, because it is sent on the join path outside the
        // cadence. So `observed.Intel` and `observed.IntelRecharge` are always
        // absent here, and a replay of a recording made mid-round begins with
        // both briefcases on their bases and nobody carrying, whatever the
        // recording's join update said.
        //
        // What that costs is measured: reference-ctf_truefort-hotsupport.trace's
        // SCORE_INTEL on tick 59 belongs to a bot that was already carrying when
        // the recorder connected, so no replay can reproduce it. Closing it means
        // applying `start` before the first tick, which would also place every
        // character from the wire several ticks earlier than the first
        // QUICK_UPDATE does — a change that can move all five measured fixture
        // windows and therefore needs its own before-and-after. See
        // docs/PARITY.md, "a replay cannot know who was already carrying".

        // Facing follows the aim, and the spinjump test compares it against its
        // previous value — so an anchor that left LastFacing at zero would read
        // as "just turned round" on the very next tick and could hand out a free
        // tick of anti-gravity.
        character.Facing = (character.AimDirection + 270) % 360 > 180 ? 1 : -1;
        character.LastFacing = character.Facing;

        _state.Characters[slot] = character;

        // The weapon is anchored too, or every resync would put a full magazine
        // back and the ammo comparison would report the drift the anchor was
        // meant to remove. Only ammo is on the wire in a QUICK_UPDATE; the rest
        // of the weapon keeps whatever it had, or starts as the reference's own
        // Create-event state if this slot had nothing.
        var weapon = alive
            ? _state.Weapons[slot]
            : WeaponState.Spawn(playerClass, character.X, character.Y);

        weapon.AmmoCount = observed.Ammo;

        // The muzzle follows the anchor. A weapon's position is normally set by
        // its owner's End Step and would catch up within a tick anyway — but an
        // open-loop replay resyncs every seventh tick and the character may fire
        // on the very next one, from wherever the weapon was left. Anchoring it
        // too costs nothing and removes a tick of stale aim from every window.
        Weapons.FollowOwner(character, ref weapon);

        _state.Weapons[slot] = weapon;

        _state.Alive[slot] = true;
    }

    /// <summary>
    /// How many projectiles of a kind are alive, and how many belong to a slot.
    /// For diagnostics.
    /// </summary>
    /// <remarks>
    /// Projectiles are the part of the world an open-loop replay never
    /// resynchronises — the wire carries a mine's position only in a
    /// <c>FULL_UPDATE</c> — so when a replay's damage disagrees this is the
    /// first thing to look at. See docs/PARITY.md, "what an anchor cannot
    /// reach".
    /// </remarks>
    public int LiveProjectiles(ProjectileKind kind, int ownerSlot = -1)
    {
        var total = 0;

        for (var i = 0; i < _state.LiveProjectileCount; i++)
        {
            ref readonly var projectile = ref _state.Projectiles[_state.LiveProjectileSlot(i)];

            if (projectile.Kind == kind && (ownerSlot < 0 || projectile.OwnerSlot == ownerSlot))
                total++;
        }

        return total;
    }

    /// <summary>
    /// One live projectile, in creation order. For diagnostics.
    /// </summary>
    /// <remarks>
    /// A projectile is never serialised, so a replay disagreeing about damage
    /// cannot be understood from the wire alone — this is the only way to see
    /// where the port thinks a rocket is when it goes off.
    /// </remarks>
    public bool TryGetProjectile(int index, out ProjectileState projectile)
    {
        if ((uint)index >= (uint)_state.LiveProjectileCount)
        {
            projectile = default;
            return false;
        }

        projectile = _state.Projectiles[_state.LiveProjectileSlot(index)];
        return true;
    }

    /// <summary>The full double-precision state of a slot, for diagnostics.</summary>
    /// <remarks>
    /// The wire view in <see cref="TryGetCharacter"/> is quantised, which is
    /// exactly what a hunt for a sub-wire-unit difference cannot see through.
    /// </remarks>
    public bool TryGetState(int slot, out CharacterState state)
    {
        state = _state.IsAlive(slot) ? _state.Characters[slot] : default;
        return _state.IsAlive(slot);
    }

    /// <summary>A slot's sentry, if it has one — for the landing gate.</summary>
    public bool TryGetSentry(int slot, out SentryState sentry)
    {
        var has = (uint)slot < WorldState.MaxSlots && _state.HasSentry[slot];
        sentry = has ? _state.Sentries[slot] : default;
        return has;
    }

    /// <summary>
    /// Whether this slot's sentry finished falling during the tick just run —
    /// the event a <c>SENTRY_POSITION</c> broadcast reports.
    /// </summary>
    public bool SentryLandedLastTick(int slot) =>
        (uint)slot < WorldState.MaxSlots && _events.SentryLanded[slot];

    /// <summary>
    /// A recorded <c>PLAYER_CHANGECLASS</c>, applied to the world's player.
    /// The sentry consequences are the simulation's own now: <c>Sentry.events/
    /// Begin Step.xml</c>'s guard reads this player state on the next tick,
    /// which is the reference's own timing - the old harness-side destroy here
    /// fired a tick early.
    /// </summary>
    public void SetPlayerClass(int slot, PlayerClass playerClass)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        EnsurePlayer(slot);
        _state.Players[slot].Class = playerClass;
    }

    /// <summary>A recorded <c>PLAYER_CHANGETEAM</c>. See <see cref="SetPlayerClass"/>.</summary>
    public void SetPlayerTeam(int slot, Team team)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        EnsurePlayer(slot);
        _state.Players[slot].Team = team;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The simulated builder's exact position wins over the broadcast
    /// one.</b> The reference server built at <c>player.object.x</c>, a full
    /// double; the message carries <c>round(x*5)</c>. In a closed-loop replay
    /// the port's own character <em>is</em> at the reference's exact position,
    /// so anchoring the sentry there loses nothing, while the broadcast is
    /// quantised to a fifth of a pixel. A slot with no live character falls
    /// back to the message.
    /// </remarks>
    public void BuildSentry(int slot, double x, double y, int direction)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        var buildX = x;
        var buildY = y;

        if (_state.IsAlive(slot))
        {
            buildX = _state.Characters[slot].X;
            buildY = _state.Characters[slot].Y;
        }

        // Build reads the owner's team off the world's player state, so make
        // sure the slot has one even if the recording somehow never named a
        // team before the build.
        EnsurePlayer(slot);
        Sentries.Build(ref _state, slot, buildX, buildY, direction);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Counted as well as stored, so a test can assert that the shots the
    /// recording contains were actually taken: a seed that is supplied and never
    /// used means the replay did not fire when the reference did, which is a
    /// divergence the hp comparison would report only indirectly.
    /// </remarks>
    public void SetFireSeed(int slot, ushort seed)
    {
        _events.ForceFireSeed(slot, seed);
        SeedsSupplied++;
    }

    /// <summary>How many recorded fire seeds have been handed to the simulation.</summary>
    public int SeedsSupplied { get; private set; }

    /// <summary>How many shots the simulation has actually taken.</summary>
    public int ShotsFired { get; private set; }

    /// <summary>
    /// Seeds that were supplied and never consumed, because no shot was taken on
    /// the tick they were meant for.
    /// </summary>
    /// <remarks>
    /// The direct measurement of "did the port fire when the reference did".
    /// Non-zero means the replay's firing decisions have drifted from the
    /// recording's, whatever the position comparison says.
    /// </remarks>
    public int SeedsUnused => SeedsSupplied - ShotsFired;

    /// <inheritdoc />
    /// <remarks>
    /// The one anchor in the harness that loses nothing. Everything here is
    /// <c>Character.events/Create.xml</c>'s own initial state: <c>hp = maxHp</c>
    /// (line 37), <c>moveStatus = 0</c> (line 166), and
    /// <c>_last_xscale = image_xscale</c> (line 120) with no assignment to
    /// <c>image_xscale</c> anywhere before it, so both facings start at GM8's
    /// default of +1. Speeds are whatever <c>instance_create</c> leaves them,
    /// which is zero.
    /// </remarks>
    public bool SpawnCharacter(int slot, int spawnpointId, int group) =>
        _spawnPoints is not null
        && (uint)slot < WorldState.MaxSlots
        && _state.Spawn(
            _spawnPoints,
            slot,
            ClassOf(slot),
            TeamOf(slot),
            spawnpointId,
            group);

    public void ClearCharacter(int slot) => _state.Clear(slot);

    /// <summary>
    /// <c>PLAYER_LEAVE</c>: the slot goes and the roster closes up behind it.
    /// </summary>
    /// <remarks>
    /// <see cref="WorldState.RemoveSlot"/> does all of it now - the players
    /// compact with the characters, weapons and sentries, and every stored
    /// roster index is renumbered. The per-harness class and team dictionaries
    /// this used to shift by hand moved into the state with phase 6.
    /// </remarks>
    public void RemoveSlot(int slot)
    {
        _state.RemoveSlot(slot);
    }

    /// <remarks>
    /// Straight through to <see cref="MatchSimulation.Tick"/> — the same call the
    /// live server makes. That is the point of the seam: a trace that passes
    /// here has exercised the code the server actually runs, not a replica of
    /// it that can drift away in maintenance.
    /// </remarks>
    public void Tick(ReadOnlySpan<PlayerInput> inputs)
    {
        if (_world is null)
            throw new InvalidOperationException("LoadMap must run before Tick");

        MatchSimulation.Tick(_world, ref _state, inputs, ref _events);

        _deaths.Clear();
        _ubers.Clear();
        _intel.Clear();

        // A return is the alarm stage, so it precedes every collision event and
        // every End Step; a drop is a Begin Step, so it precedes the return.
        // The order only has to be *a* consistent one — ReconcileIntel matches
        // as a multiset — but keeping it the reference's costs nothing and
        // makes a diff legible.
        if (_events.ReturnedRedIntel)
            _intel.Add(new IntelRecord((byte)Team.Red, IntelEvent.Returned));

        if (_events.ReturnedBlueIntel)
            _intel.Add(new IntelRecord((byte)Team.Blue, IntelEvent.Returned));

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (_events.DroppedIntel[slot])
                _intel.Add(new IntelRecord((byte)slot, IntelEvent.Dropped));

            if (_events.GrabbedIntel[slot])
                _intel.Add(new IntelRecord((byte)slot, IntelEvent.Grabbed));

            if (_events.ScoredIntel[slot])
                _intel.Add(new IntelRecord((byte)slot, IntelEvent.Scored));

            if (_events.Fired[slot])
                ShotsFired++;

            // In the reference's own order within a tick: both come out of the
            // medic's Begin Step, the charge from the attack key and the pop
            // from the right-click a few lines below it.
            if (_events.UberCharged[slot])
                _ubers.Add(new UberRecord((byte)slot, UberEvent.Charged));

            if (_events.Ubered[slot])
                _ubers.Add(new UberRecord((byte)slot, UberEvent.Started));

            if (!_events.Died[slot])
                continue;

            var death = _events.Deaths[slot];
            _deaths.Add(new DeathRecord((byte)slot, death.Killer, death.Assistant, (byte)death.Source));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DeathRecord> DeathsLastTick => _deaths;

    /// <inheritdoc />
    public IReadOnlyList<UberRecord> UbersLastTick => _ubers;

    /// <inheritdoc />
    public IReadOnlyList<IntelRecord> IntelLastTick => _intel;

    /// <summary>One team's briefcase, for diagnostics.</summary>
    /// <remarks>
    /// The flag reaches the wire only in a <c>FULL_UPDATE</c>, so a replay that
    /// disagrees about a grab cannot be understood from the recording alone —
    /// this is the only way to see where the port thinks the briefcase is.
    /// </remarks>
    public IntelState IntelOf(Team team) => team == Team.Blue ? _state.BlueIntel : _state.RedIntel;

    /// <summary>The two cap counters, for diagnostics.</summary>
    public (int Red, int Blue) Caps => (_state.RedCaps, _state.BlueCaps);

    public bool TryGetCharacter(int slot, out CharacterBlock character)
    {
        if (!_state.IsAlive(slot))
        {
            character = default;
            return false;
        }

        // Shared with the live server rather than reimplemented here: see
        // CharacterWire, and why having two copies would make this harness
        // prove the wrong program.
        character = CharacterWire.ToBlock(_state.Characters[slot], _state.Weapons[slot]);
        return true;
    }
}
