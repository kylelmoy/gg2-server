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

    /// <summary>Each slot's class, which decides its box and its run power.</summary>
    /// <remarks>
    /// Taken from the trace's <c>PLAYER_CHANGECLASS</c> messages rather than
    /// guessed: a scout and a heavy are different widths and different speeds,
    /// so replaying one as the other is not a near miss.
    /// </remarks>
    public Dictionary<int, PlayerClass> Classes { get; } = [];

    public Dictionary<int, Team> Teams { get; } = [];

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
        var level = loadMap(map);
        _world = new CollisionWorld(level);
        _spawnPoints = new SpawnPoints(level);
        _state = default;
    }

    public void SetCharacter(int slot, in CharacterBlock observed)
    {
        if ((uint)slot >= WorldState.MaxSlots)
            return;

        var character = _state.Alive[slot] ? _state.Characters[slot] : new CharacterState();

        character.Class = Classes.GetValueOrDefault(slot, PlayerClass.Scout);
        character.Team = Teams.GetValueOrDefault(slot, Team.Red);

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
        character.LastKeyState = observed.Keys;
        character.NetAimDirection = observed.NetAimDirection;
        character.AimDistance = observed.AimDistance;

        character.MoveStatus = observed.MoveStatus;
        character.Cloak = observed.Cloaked;

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
        var weapon = _state.Alive[slot] ? _state.Weapons[slot] : WeaponState.Spawn(character.Class);
        weapon.AmmoCount = observed.Ammo;
        _state.Weapons[slot] = weapon;

        _state.Alive[slot] = true;
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

    public void SetPlayerClass(int slot, PlayerClass playerClass) => Classes[slot] = playerClass;

    public void SetPlayerTeam(int slot, Team team) => Teams[slot] = team;

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
        && _state.Spawn(
            _spawnPoints,
            slot,
            Classes.GetValueOrDefault(slot, PlayerClass.Scout),
            Teams.GetValueOrDefault(slot, Team.Red),
            spawnpointId,
            group);

    public void ClearCharacter(int slot) => _state.Clear(slot);

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

        MatchSimulation.Tick(_world, ref _state, inputs);
    }

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
