using Gg2.Protocol;

namespace Gg2.Parity;

/// <summary>
/// What the replayer needs from a simulation.
/// </summary>
/// <remarks>
/// Deliberately tiny, and deliberately in the harness rather than in the
/// simulation. Phase 4 writes the physics against
/// <c>Tick(WorldState, ReadOnlySpan&lt;PlayerInput&gt;)</c>
/// (docs/ARCHITECTURE.md) and an adapter here bridges the two — so the shape of
/// <c>WorldState</c> is not prejudged by a harness interface written before it
/// exists.
/// <para>
/// Everything crosses in <b>wire units</b>. The comparison is about the
/// integers a client can observe, not the doubles behind them, and converting
/// on the way out would hide exactly the quantisation the port has to match.
/// </para>
/// </remarks>
public interface ITraceSimulation
{
    /// <summary>
    /// Loads the map. Called once before a replay, and again on a
    /// <c>CHANGE_MAP</c>.
    /// </summary>
    void LoadMap(string map);

    /// <summary>
    /// Forces a slot's character to a known state, in wire units.
    /// </summary>
    /// <remarks>
    /// Used to anchor a replay at the start, to resynchronise on a
    /// <c>PLAYER_SPAWN</c>, and to reset before each open-loop segment.
    /// docs/PARITY.md is explicit that spawn positions and respawn timing are
    /// server-side RNG the port is free about, so they are taken from the trace
    /// rather than decided again.
    /// </remarks>
    void SetCharacter(int slot, in CharacterBlock observed);

    /// <summary>Removes a slot's character — it left, died, or went spectator.</summary>
    void ClearCharacter(int slot);

    /// <summary>
    /// A slot's class, from the trace's <c>PLAYER_CHANGECLASS</c>.
    /// </summary>
    /// <remarks>
    /// Not optional detail: a scout and a heavy have different collision boxes
    /// and different run powers, so replaying one as the other is not a near
    /// miss.
    /// </remarks>
    void SetPlayerClass(int slot, PlayerClass playerClass);

    /// <summary>A slot's team, which decides which gates are solid to it.</summary>
    void SetPlayerTeam(int slot, Team team);

    /// <summary>Advances one tick with the given per-slot inputs.</summary>
    void Tick(ReadOnlySpan<PlayerInput> inputs);

    /// <summary>
    /// What this simulation now believes a slot's character is, in wire units.
    /// False when it has no character in that slot.
    /// </summary>
    bool TryGetCharacter(int slot, out CharacterBlock character);
}

/// <summary>
/// A simulation with nothing in it.
/// </summary>
/// <remarks>
/// Phase 2's exit criterion in docs/PLAN.md is that "the replayer runs a trace
/// against an empty simulation and reports a mismatch on tick 1 with a legible
/// diff". This is that empty simulation, and it exists so the harness can be
/// finished — and shown to fail correctly — before there is any physics to
/// disagree with.
/// <para>
/// It accepts characters and then never moves them, so a trace of anything
/// standing still passes and a trace of anything moving fails on the first tick
/// it moves. That is the right failure: a harness that cannot show a failure
/// clearly is not finished.
/// </para>
/// </remarks>
public sealed class EmptySimulation : ITraceSimulation
{
    private readonly Dictionary<int, CharacterBlock> _characters = [];

    public string? Map { get; private set; }

    public int Ticks { get; private set; }

    public void LoadMap(string map) => Map = map;

    public void SetCharacter(int slot, in CharacterBlock observed) => _characters[slot] = observed;

    public void ClearCharacter(int slot) => _characters.Remove(slot);

    public void SetPlayerClass(int slot, PlayerClass playerClass) { }

    public void SetPlayerTeam(int slot, Team team) { }

    public void Tick(ReadOnlySpan<PlayerInput> inputs) => Ticks++;

    public bool TryGetCharacter(int slot, out CharacterBlock character) =>
        _characters.TryGetValue(slot, out character);
}
