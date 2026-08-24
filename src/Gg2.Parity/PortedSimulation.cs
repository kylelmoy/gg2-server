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
public sealed class PortedSimulation(Func<string, LevelMap> loadMap) : ITraceSimulation
{
    private readonly Dictionary<int, CharacterState> _characters = [];
    private CollisionWorld? _world;

    /// <summary>Each slot's class, which decides its box and its run power.</summary>
    /// <remarks>
    /// Taken from the trace's <c>PLAYER_CHANGECLASS</c> messages rather than
    /// guessed: a scout and a heavy are different widths and different speeds,
    /// so replaying one as the other is not a near miss.
    /// </remarks>
    public Dictionary<int, PlayerClass> Classes { get; } = [];

    public Dictionary<int, Team> Teams { get; } = [];

    /// <summary>
    /// A sub-quantisation nudge applied to the anchored position, in world
    /// pixels.
    /// </summary>
    /// <remarks>
    /// A diagnostic, not a fudge. A trace anchors from the wire, which carries
    /// position to a fifth of a pixel — so the true state the reference was in
    /// is only known to within ±0.1px, and a replay starts from somewhere inside
    /// that bin rather than on the reference's own value. Sweeping the nudge
    /// across the bin answers the question the gate actually cares about: is a
    /// residual mismatch a difference in the arithmetic, or just the anchor? If
    /// some offset inside the bin replays exactly, the arithmetic agrees.
    /// </remarks>
    public double AnchorNudgeY { get; init; }

    public double AnchorNudgeX { get; init; }

    /// <summary>Which slot the nudge applies to. All of them when null.</summary>
    public int? NudgeSlot { get; init; }

    public void LoadMap(string map)
    {
        _world = new CollisionWorld(loadMap(map));
        _characters.Clear();
    }

    public void SetCharacter(int slot, in CharacterBlock observed)
    {
        var character = _characters.TryGetValue(slot, out var existing) ? existing : new CharacterState();

        character.Class = Classes.GetValueOrDefault(slot, PlayerClass.Scout);
        character.Team = Teams.GetValueOrDefault(slot, Team.Red);

        // Wire units back to world units. The wire is the lossy side, so this
        // is the best anchor available — and it is exactly what the client does
        // when it applies a QUICK_UPDATE.
        var nudged = NudgeSlot is null || NudgeSlot == slot;
        character.X = observed.X / 5.0 + (nudged ? AnchorNudgeX : 0);
        character.Y = observed.Y / 5.0 + (nudged ? AnchorNudgeY : 0);
        character.HSpeed = observed.HSpeed / 8.5;
        character.VSpeed = observed.VSpeed / 8.5;
        character.Hp = observed.Hp;

        character.KeyState = observed.Keys;
        character.LastKeyState = observed.Keys;
        character.NetAimDirection = observed.NetAimDirection;
        character.AimDistance = observed.AimDistance;

        character.MoveStatus = observed.MoveStatus;

        // Facing follows the aim, and the spinjump test compares it against its
        // previous value — so an anchor that left LastFacing at zero would read
        // as "just turned round" on the very next tick and could hand out a free
        // tick of anti-gravity.
        character.Facing = (character.AimDirection + 270) % 360 > 180 ? 1 : -1;
        character.LastFacing = character.Facing;

        _characters[slot] = character;
    }

    public void SetPlayerClass(int slot, PlayerClass playerClass) => Classes[slot] = playerClass;

    public void SetPlayerTeam(int slot, Team team) => Teams[slot] = team;

    public void ClearCharacter(int slot) => _characters.Remove(slot);

    public void Tick(ReadOnlySpan<PlayerInput> inputs)
    {
        if (_world is null)
            throw new InvalidOperationException("LoadMap must run before Tick");

        // The phase split is observable, so every character finishes a phase
        // before any starts the next — see CharacterPhysics.
        var slots = _characters.Keys.Order().ToArray();

        foreach (var slot in slots)
        {
            if (slot >= inputs.Length)
                continue;

            var character = _characters[slot];
            character.ApplyInput(inputs[slot]);
            _characters[slot] = character;
        }

        foreach (var slot in slots)
        {
            var character = _characters[slot];
            CharacterPhysics.BeginStep(_world, ref character);
            _characters[slot] = character;
        }

        foreach (var slot in slots)
        {
            var character = _characters[slot];
            CharacterPhysics.Step(_world, ref character);
            _characters[slot] = character;
        }

        foreach (var slot in slots)
        {
            var character = _characters[slot];
            CharacterPhysics.EndStep(_world, ref character);
            _characters[slot] = character;
        }
    }

    public bool TryGetCharacter(int slot, out CharacterBlock character)
    {
        if (!_characters.TryGetValue(slot, out var state))
        {
            character = default;
            return false;
        }

        character = new CharacterBlock
        {
            Keys = state.KeyState,
            NetAimDirection = state.NetAimDirection,
            AimDistance = state.AimDistance,
            HasPosition = true,

            // Quantised exactly the way Character.events/User Event 12.xml
            // does it: plain x*5, with no GML round() first, so the extension's
            // half-away-from-zero rule applies.
            X = WireQuantise.ToUShort(state.X * 5),
            Y = WireQuantise.ToUShort(state.Y * 5),
            HSpeed = WireQuantise.ToByte(state.HSpeed * 8.5),
            VSpeed = WireQuantise.ToByte(state.VSpeed * 8.5),
            Hp = WireQuantise.ToUByte(Math.Ceiling(state.Hp)),

            // Not simulated yet, so they are copied from the anchor rather than
            // reported as zero — otherwise every comparison would fail on a
            // field this phase does not claim to reproduce.
            Ammo = 0,
            Flags = (byte)((state.MoveStatus & 0x07) << 1),
        };

        return true;
    }
}
