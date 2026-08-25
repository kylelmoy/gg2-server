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
    /// Used to anchor a replay at the start, to reset before each open-loop
    /// segment, and as the fallback when a <c>PLAYER_SPAWN</c> cannot be
    /// resolved to a spawn point. docs/PARITY.md is explicit that respawn
    /// timing and the choice of spawn point are server-side RNG the port is
    /// free about, so both are taken from the trace rather than decided again.
    /// <para>
    /// This anchor is <b>lossy</b> — see <see cref="SpawnCharacter"/>, which is
    /// not.
    /// </para>
    /// </remarks>
    void SetCharacter(int slot, in CharacterBlock observed);

    /// <summary>
    /// Places a freshly spawned character from a <c>PLAYER_SPAWN</c>'s own
    /// spawn point id and group. False if it cannot be resolved, in which case
    /// the replayer falls back to anchoring on the next observed position.
    /// </summary>
    /// <remarks>
    /// This is the <b>exact</b> anchor, and it is the only one a closed-loop
    /// gate can start from. <c>sendEventSpawn.gml</c> sends the pair rather than
    /// a position and every client resolves it in <c>doEventSpawn.gml</c>, so
    /// reproducing the lookup is not guessing at RNG — the server already made
    /// the random choice and told everyone which point it picked. The resulting
    /// position is an integer (every spawn entity in the built-in maps sits on
    /// one) and <c>instance_create</c> leaves both speeds at zero, so nothing
    /// here passes through the wire's quantisation.
    /// <para>
    /// Contrast <see cref="SetCharacter"/>, which anchors on a broadcast
    /// position: those arrive only every seventh tick, so by the time one names
    /// a newly spawned character it has already fallen, and the value is
    /// rounded to a fifth of a pixel.
    /// </para>
    /// </remarks>
    bool SpawnCharacter(int slot, int spawnpointId, int group);

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

    /// <summary>
    /// Supplies the random seed the reference used for a slot's shot on the
    /// tick that is about to run.
    /// </summary>
    /// <remarks>
    /// <b>An injection, and the only one the harness makes into the
    /// simulation's own decisions.</b> A weapon's <c>User Event 1</c> draws
    /// <c>irandom(65535)</c> and broadcasts it; every client then re-derives the
    /// shot spread from it (<c>Scripts/Events/doEventFireWeapon.gml</c>). Which
    /// seed is drawn is server-side RNG the port is free about
    /// (docs/PARITY.md), so a replay takes the recorded one — otherwise it would
    /// scatter its pellets differently and disagree about hp for a reason that
    /// has nothing to do with the arithmetic under test.
    /// <para>
    /// <b>It does not decide <em>whether</em> to fire.</b> That stays the
    /// simulation's job and is exactly what the gate is measuring: a supplied
    /// seed that no shot consumes is a replay firing on the wrong tick, and it
    /// shows up as a divergence rather than being papered over.
    /// </para>
    /// </remarks>
    void SetFireSeed(int slot, ushort seed);

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

    /// <remarks>
    /// Always false: this stand-in has no map, so it has no spawn points to
    /// resolve against and the replayer anchors from the wire instead. That is
    /// the phase-2 behaviour, kept as it was.
    /// </remarks>
    public bool SpawnCharacter(int slot, int spawnpointId, int group) => false;

    public void ClearCharacter(int slot) => _characters.Remove(slot);

    public void SetPlayerClass(int slot, PlayerClass playerClass) { }

    public void SetPlayerTeam(int slot, Team team) { }

    /// <remarks>Nothing here fires, so a seed has nothing to seed.</remarks>
    public void SetFireSeed(int slot, ushort seed) { }

    public void Tick(ReadOnlySpan<PlayerInput> inputs) => Ticks++;

    public bool TryGetCharacter(int slot, out CharacterBlock character) =>
        _characters.TryGetValue(slot, out character);
}
