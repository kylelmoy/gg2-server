using System.Linq;
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
/// <summary>
/// One <c>PLAYER_DEATH</c>, as either side of the comparison states it.
/// </summary>
/// <remarks>
/// In wire terms, like everything else that crosses this seam: the roster
/// indices are the bytes the message carries and 255 means nobody, rather than
/// whatever a particular simulation spells that. That keeps the recorded side
/// and the simulated side literally the same type, so the comparison is an
/// equality and not a translation.
/// </remarks>
/// <param name="Slot">The roster index whose character died.</param>
/// <param name="Killer">Who dealt the fatal damage, or 255.</param>
/// <param name="Assistant">Who assisted, or 255.</param>
/// <param name="Source">The <c>DAMAGE_SOURCE_*</c> id.</param>
public readonly record struct DeathRecord(byte Slot, byte Killer, byte Assistant, byte Source);

/// <summary>Which of the two uber messages a <see cref="UberRecord"/> is.</summary>
public enum UberEvent : byte
{
    /// <summary><c>UBER_CHARGED</c>: the meter reached 2000.</summary>
    Charged,

    /// <summary><c>UBER</c>: the medic started spending it.</summary>
    Started,
}

/// <summary>
/// One <c>UBER_CHARGED</c> or <c>UBER</c>, as either side of the comparison
/// states it.
/// </summary>
/// <remarks>
/// <b>These two messages are the medic's only direct wire events, and they are
/// worth far more than their two bytes.</b> A medic's charge is also visible in
/// every state update as one quantised byte, but that byte says nothing about
/// <em>when</em> the meter filled: it saturates at 255 and stays there for as
/// long as the charge is banked. <c>UBER_CHARGED</c> names the exact tick, and
/// reaching it requires 800 consecutive ticks of the beam having connected to
/// the right patient at the right rate. Nothing else in the protocol audits the
/// heal loop that hard.
/// </remarks>
/// <param name="Slot">The medic's roster index.</param>
/// <param name="Event">Which message it was.</param>
public readonly record struct UberRecord(byte Slot, UberEvent Event);

/// <summary>Which of the four intel messages an <see cref="IntelRecord"/> is.</summary>
public enum IntelEvent : byte
{
    /// <summary><c>GRAB_INTEL</c>: a player took the enemy briefcase.</summary>
    Grabbed,

    /// <summary><c>DROP_INTEL</c>: a carrier put it down and lived.</summary>
    Dropped,

    /// <summary><c>SCORE_INTEL</c>: a capture.</summary>
    Scored,

    /// <summary><c>RETURN_INTEL</c>: a dropped briefcase's alarm fired.</summary>
    Returned,
}

/// <summary>
/// One <c>GRAB_INTEL</c>, <c>DROP_INTEL</c>, <c>SCORE_INTEL</c> or
/// <c>RETURN_INTEL</c>, as either side of the comparison states it.
/// </summary>
/// <remarks>
/// <b>These are the only direct evidence a recording carries about the
/// objective.</b> The briefcase's position and its return alarm reach the wire
/// in a <c>FULL_UPDATE</c> and nowhere else, and a recording contains exactly
/// one of those — the join. Everything else about the flag has to be inferred
/// from these four one-byte messages, which name the tick exactly.
/// <para>
/// <see cref="Id"/> is a roster index for the first three and a
/// <em>team</em> for <see cref="IntelEvent.Returned"/>, because that is what
/// each message's single body byte is.
/// </para>
/// </remarks>
/// <param name="Id">The roster index, or the team for a return.</param>
/// <param name="Event">Which message it was.</param>
public readonly record struct IntelRecord(byte Id, IntelEvent Event);

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
    /// A player left: their slot goes, and every slot above it moves down one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not <see cref="ClearCharacter"/>.</b> A roster index is positional,
    /// so a departure <em>renumbers</em> everybody above it — CLAUDE.md, "Roster
    /// indices are positional and shift on PLAYER_LEAVE". The server's own
    /// state updates are already compacted by the time they reach a recording,
    /// so a replay that merely cleared the slot would compare its slot 3 with
    /// the recording's slot 3 and be comparing two different players from the
    /// next update onwards.
    /// <para>
    /// It is separate from <c>ClearCharacter</c> because the two are genuinely
    /// different events and the reference treats them so: a death clears, a
    /// departure compacts, and what survives differs — see
    /// <c>Gg2.Simulation.WorldState.Clear</c> versus <c>RemoveSlot</c>.
    /// </para>
    /// </remarks>
    void RemoveSlot(int slot);

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
    /// A recorded <c>BUILD_SENTRY</c>: this slot placed a sentry at this
    /// position, facing this way.
    /// </summary>
    /// <remarks>
    /// Injected rather than simulated, like a <c>PLAYER_SPAWN</c>: the message
    /// on the wire is the reference's own acceptance of the build, so the
    /// preconditions are not re-checked. The position is the broadcast one —
    /// <c>round(x*5)</c>, a fifth of a pixel — and an implementation that holds
    /// the builder's exact character may prefer it; see
    /// <c>PortedSimulation.BuildSentry</c>.
    /// <para>
    /// Everything a sentry does after this — falling, building, the turret's
    /// fire, its death — is simulated, and its consequences (damage to
    /// characters the recording also observed) are what the hp comparison
    /// gates.
    /// </para>
    /// </remarks>
    void BuildSentry(int slot, double x, double y, int direction);

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
    /// The characters the tick just run killed, in slot order.
    /// </summary>
    /// <remarks>
    /// <b>An output, never an input, for the same reason a fire seed is not
    /// one.</b> The replayer knows from the recording exactly who the reference
    /// killed and when, and applying that would make the gate vacuous — the
    /// question it exists to answer is whether the port reaches <c>hp &lt;= 0</c>
    /// on the same tick by itself. A recorded <c>PLAYER_DEATH</c> is therefore
    /// compared against this and never fed in.
    /// <para>
    /// Deaths are <em>also</em> visible indirectly, as a slot that stops
    /// appearing in the state updates — but only at the next update, up to six
    /// ticks later, and with nothing to say about who got the kill. This is the
    /// direct measurement.
    /// </para>
    /// <para>
    /// The list is the simulation's own and is valid until the next
    /// <see cref="Tick"/>; a caller that wants to keep it copies it.
    /// </para>
    /// </remarks>
    IReadOnlyList<DeathRecord> DeathsLastTick { get; }

    /// <summary>
    /// The uber messages the tick just run produced, in slot order.
    /// </summary>
    /// <remarks>
    /// An output and never an input, for the same reason as
    /// <see cref="DeathsLastTick"/>: a replay that was told when to charge would
    /// answer the question it exists to ask. See <see cref="UberRecord"/> for
    /// why the two messages are a stronger gate than the charge byte they
    /// accompany.
    /// </remarks>
    IReadOnlyList<UberRecord> UbersLastTick { get; }

    /// <summary>
    /// The intel messages the tick just run produced, in the order the
    /// reference's own event stages would have written them.
    /// </summary>
    /// <remarks>
    /// An output and never an input, for the same reason as
    /// <see cref="DeathsLastTick"/>. A grab in particular is a hard gate: it
    /// happens on the tick a character's box first overlaps the briefcase's, so
    /// reproducing the recorded tick means the port has the trajectory, the
    /// intel box and the gate test all right at once.
    /// </remarks>
    IReadOnlyList<IntelRecord> IntelLastTick { get; }

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

    /// <summary>
    /// Compacts, so that the stand-in does not quietly model the bug this
    /// method exists to prevent.
    /// </summary>
    public void RemoveSlot(int slot)
    {
        _characters.Remove(slot);

        foreach (var above in _characters.Keys.Where(k => k > slot).Order().ToArray())
        {
            _characters[above - 1] = _characters[above];
            _characters.Remove(above);
        }
    }

    public void SetPlayerClass(int slot, PlayerClass playerClass) { }

    public void SetPlayerTeam(int slot, Team team) { }

    /// <remarks>Nothing here can hold a sentry.</remarks>
    public void BuildSentry(int slot, double x, double y, int direction) { }

    /// <remarks>Nothing here fires, so a seed has nothing to seed.</remarks>
    public void SetFireSeed(int slot, ushort seed) { }

    public void Tick(ReadOnlySpan<PlayerInput> inputs) => Ticks++;

    /// <remarks>Nothing here can be damaged, so nothing here can die.</remarks>
    public IReadOnlyList<DeathRecord> DeathsLastTick => [];

    /// <remarks>Nothing here holds a weapon, so nothing here can uber.</remarks>
    public IReadOnlyList<UberRecord> UbersLastTick => [];

    /// <remarks>Nothing here has an objective, so nothing here touches a flag.</remarks>
    public IReadOnlyList<IntelRecord> IntelLastTick => [];

    public bool TryGetCharacter(int slot, out CharacterBlock character) =>
        _characters.TryGetValue(slot, out character);
}
