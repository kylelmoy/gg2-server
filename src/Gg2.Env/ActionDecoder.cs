using Gg2.Protocol;

namespace Gg2.Env;

/// <summary>
/// One agent's action, as the multi-discrete heads a policy emits.
/// </summary>
/// <remarks>
/// The shape is <c>gg2-rl/docs/DESIGN.md</c>'s, stamped into the action
/// encoding version: horizontal ×3, jump ×2, down ×2, fire ×3, aim delta ×15.
/// <para>
/// ⚠️ <b>Indices, not flags.</b> A head is one categorical choice, so
/// "left and right at once" is not expressible — which is deliberate: the
/// reference cancels that case to "not controlling" anyway
/// (<c>Begin Step.xml:249</c>), and letting a policy spend probability mass on
/// it teaches it nothing.
/// </para>
/// </remarks>
public readonly record struct EnvAction
{
    /// <summary>0 none, 1 left, 2 right.</summary>
    public byte Horizontal { get; init; }

    /// <summary>0 none, 1 press.</summary>
    public byte Jump { get; init; }

    /// <summary>0 none, 1 hold.</summary>
    public byte Down { get; init; }

    /// <summary>0 none, 1 attack, 2 special.</summary>
    public byte Fire { get; init; }

    /// <summary>
    /// 0..14, a signed bin over the turn-rate cap; 7 is "no turn".
    /// </summary>
    public byte AimDelta { get; init; }

    /// <summary>The action that presses nothing and does not turn.</summary>
    public static EnvAction Idle => new() { AimDelta = ActionDecoder.AimCentreBin };
}

/// <summary>
/// Turns action heads into the <see cref="PlayerInput"/> a tick consumes.
/// </summary>
/// <remarks>
/// ⚠️ <b>The conversion is SERVER-SIDE, and that is a decision rather than a
/// convenience</b> (<c>docs/ENV.md</c>, scope call 2). The action encoding
/// version rides inside <c>SIM_UUID</c>, which implies this side owns it; the
/// turn-rate cap is an honesty property that must not be re-implementable
/// differently per trainer; and the delta needs the current aim, which only
/// this side holds.
/// <para>
/// The decoder is stateful in exactly one thing — the aim angle per slot — and
/// that state has to travel with a snapshot, or a restored episode aims
/// somewhere else. See <see cref="Aim"/>.
/// </para>
/// </remarks>
public sealed class ActionDecoder
{
    /// <summary>Bins in the aim-delta head. Odd, so one of them is "no turn".</summary>
    public const int AimBins = 15;

    /// <summary>The bin that does not turn: the middle one.</summary>
    public const byte AimCentreBin = 7;

    /// <summary>
    /// Degrees a policy may turn in one decision, either way.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A v0 constant, calibrated in <c>gg2-rl</c> phase 5, and changing it
    /// bumps the action encoding version.</b> It exists so a policy cannot snap
    /// instantly onto a target from any angle — an aimbot is not the thing being
    /// trained, and a cap the trainer could set itself would be a cap that means
    /// nothing.
    /// </remarks>
    public const double MaxTurnDegreesPerDecision = 30.0;

    /// <summary>
    /// ⚠️ <b>Fixed in v0; the heads do not carry it.</b> Changing it is an
    /// encoding bump. <c>aimDistance</c> reaches the wire as
    /// <c>distance / 2</c> clamped to a byte, and nothing in a duel varies it
    /// meaningfully — the sniper's charge is the one weapon that reads it and
    /// the sniper is not in v0.
    /// </remarks>
    public const double AimDistancePixels = 100;

    private readonly double[] _aim = new double[WorldStateSlots];

    // Kept local rather than referencing Gg2.Simulation.WorldState.MaxSlots so
    // that this file has no reason to know about the simulation's layout.
    private const int WorldStateSlots = 32;

    /// <summary>The angle this slot is currently aiming at, in degrees.</summary>
    /// <remarks>
    /// ⚠️ <b>Part of the episode's state.</b> A snapshot that restored the world
    /// and not this would resume aiming wherever the decoder happened to be
    /// pointing, which is a silent divergence rather than a crash — the same
    /// class of bug as <c>ScriptedBot.Random</c> not being snapshotted.
    /// </remarks>
    public double Aim(int slot) => _aim[slot];

    /// <summary>Points a slot at an absolute angle, for a reset or a restore.</summary>
    public void SetAim(int slot, double degrees) => _aim[slot] = Normalise(degrees);

    /// <summary>
    /// Decodes one action for one slot, advancing that slot's aim.
    /// </summary>
    /// <param name="slot">The roster slot.</param>
    /// <param name="action">The heads.</param>
    /// <param name="firstTickOfDecision">
    /// Whether this is the first tick of an action repeat. ⚠️ <b>The jump bit is
    /// emitted only on this tick</b> — see the remarks.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>Jump is an EDGE, so a held bit does nothing after the first tick.</b>
    /// <c>Character.events/Begin Step.xml</c> jumps on <c>pressedKeys</c>, and
    /// this project has already paid for that once: a driven client that held
    /// the jump bit climbed exactly one step and sat there looking wedged
    /// (CLAUDE.md, "GG2 jumps on the PRESSED edge"). Under an action repeat of
    /// k, emitting the bit on all k ticks would spend one jump and waste k-1
    /// ticks of the head; emitting it on the first only is what makes the head
    /// mean "jump now".
    /// <para>
    /// ⚠️ <b>The mask is written ABSOLUTELY, never OR-ed into a previous one.</b>
    /// The same trap CLAUDE.md records for <c>gg2_watch</c> driving: an OR
    /// leaves an earlier direction bit set and the character walks the wrong
    /// way. Building the mask fresh from the heads every tick is why that cannot
    /// happen here.
    /// </para>
    /// </remarks>
    public PlayerInput Decode(int slot, EnvAction action, bool firstTickOfDecision)
    {
        var keys = PlayerKeys.None;

        if (action.Horizontal == 1)
            keys |= PlayerKeys.Left;
        else if (action.Horizontal == 2)
            keys |= PlayerKeys.Right;

        if (action.Down != 0)
            keys |= PlayerKeys.Down;

        if (action.Jump != 0 && firstTickOfDecision)
            keys |= PlayerKeys.Jump;

        if (action.Fire == 1)
            keys |= PlayerKeys.Attack;
        else if (action.Fire == 2)
            keys |= PlayerKeys.Special;

        // The turn is applied once per DECISION, not once per tick: repeating it
        // k times would make the effective cap k times larger and quietly undo
        // the honesty property the cap exists for.
        if (firstTickOfDecision)
            _aim[slot] = Normalise(_aim[slot] + TurnFor(action.AimDelta));

        // ⚠️ Through PlayerInput.FromAim, so the angle lands on the same 65,536
        // steps a client's does. A bot that aimed on a finer grid than the wire
        // carries would be aiming somewhere no human can — docs/BOTAI.md item 4.
        return PlayerInput.FromAim(keys, _aim[slot], AimDistancePixels);
    }

    /// <summary>Degrees for a bin, clamped to the cap.</summary>
    /// <remarks>
    /// Bin 0 is a full left turn, <see cref="AimCentreBin"/> is none, bin 14 is
    /// a full right turn. An out-of-range bin does not throw: a policy emitting
    /// one is a bug in the trainer, and the env's contract is that a malformed
    /// action is refused at the protocol edge rather than mid-tick.
    /// </remarks>
    public static double TurnFor(byte bin)
    {
        var clamped = bin >= AimBins ? AimCentreBin : bin;
        return (clamped - AimCentreBin) * (MaxTurnDegreesPerDecision / AimCentreBin);
    }

    /// <summary>Into [0, 360).</summary>
    private static double Normalise(double degrees)
    {
        var wrapped = degrees % 360.0;
        return wrapped < 0 ? wrapped + 360.0 : wrapped;
    }
}
