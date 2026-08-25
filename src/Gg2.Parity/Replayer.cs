using Gg2.Protocol;

namespace Gg2.Parity;

/// <summary>
/// Feeds a trace's recorded inputs into a simulation and compares what comes
/// out against what the reference said.
/// </summary>
/// <remarks>
/// docs/PARITY.md, tier 2. Two modes, and both are wanted: closed-loop is the
/// honest measure of whether the physics is the same, open-loop is what a
/// player actually experiences.
/// <para>
/// <b>What is deliberately not compared.</b> The <em>choice</em> of spawn point,
/// respawn timing and team selection come from server-side RNG that the port is
/// explicitly free about (docs/SIMULATION.md), so the replay takes them from the
/// trace instead of deciding them again rather than counting them as mismatches.
/// <para>
/// Note what that does <b>not</b> excuse. A <c>PLAYER_SPAWN</c> carries the
/// point the server picked, so the resulting <em>position</em> is not RNG at
/// all — it is a lookup, this replay does it, and the character it places is
/// held to the gate from its first tick.
/// </para>
/// </para>
/// </remarks>
/// <summary>Which wire fields a replay holds the simulation to.</summary>
/// <remarks>
/// Exists so a phase can be gated on what it claims to reproduce. Phase 4 is
/// about trajectories, and comparing <c>ammo</c> against a simulation with no
/// weapons in it would report a wall of failures that say nothing about the
/// physics. docs/PARITY.md's advice is the same one from the other direction:
/// start with the gates loose enough to be green, then tighten to zero as each
/// behaviour lands.
/// </remarks>
[Flags]
public enum Fields
{
    None = 0,
    Position = 1 << 0,
    Velocity = 1 << 1,
    Hp = 1 << 2,
    Ammo = 1 << 3,
    Flags = 1 << 4,

    /// <summary>What the character physics is responsible for.</summary>
    Trajectory = Position | Velocity,

    All = Position | Velocity | Hp | Ammo | Flags,
}

public sealed class Replayer(ITraceSimulation simulation)
{
    /// <summary>Which fields to hold the simulation to. Everything by default.</summary>
    public Fields Compare { get; init; } = Fields.All;

    /// <summary>
    /// Whether a tick's update describes the state <em>before</em> that tick's
    /// simulation ran.
    /// </summary>
    /// <remarks>
    /// It does, and this is not a matter of taste. <c>GameServerBeginStep.gml</c>
    /// calls <c>processClientCommands</c> and then <c>serializeState</c>, both in
    /// the server's own Begin Step — and GM8 runs Begin Step for every instance
    /// before any instance's Step. The <c>GameServer</c> is created before any
    /// <c>Character</c>, so it serialises first: one update therefore carries
    /// <b>this tick's input</b> alongside <b>last tick's position</b>. Which is
    /// exactly the pairing a client needs to predict forward.
    /// <para>
    /// Left as an option because it was settled by measurement rather than by
    /// reading — replaying the same trace both ways and keeping the one that
    /// drifts less. Turning it off should make a working simulation look
    /// systematically slow.
    /// </para>
    /// </remarks>
    public bool UpdatesPrecedeTheirTick { get; init; } = true;

    /// <summary>
    /// Replays <paramref name="ticks"/> and reports what disagreed.
    /// </summary>
    /// <param name="traceName">For the report.</param>
    /// <param name="map">The map to load before replaying.</param>
    /// <param name="ticks">Parsed by <see cref="TraceParser"/>.</param>
    /// <param name="mode">Closed- or open-loop.</param>
    /// <param name="stopAtFirstBadTick">
    /// Closed-loop default. One divergence produces a mismatch on every tick
    /// after it, and a hundred downstream mismatches are one bug.
    /// </param>
    public DriftReport Replay(
        string traceName,
        string map,
        IReadOnlyList<TraceTick> ticks,
        ReplayMode mode,
        bool stopAtFirstBadTick = true)
    {
        simulation.LoadMap(map);

        var mismatches = new List<Mismatch>();
        var xErrors = new List<long>();
        var yErrors = new List<long>();
        var comparisons = 0;
        var resyncs = 0;
        var replayed = 0;

        foreach (var tick in ticks)
        {
            ApplyEvents(tick);

            var bad = mismatches.Count;

            // One update carries this tick's input and last tick's position, so
            // the comparison happens before the inputs are consumed.
            if (UpdatesPrecedeTheirTick && tick.HasPositions)
            {
                Reconcile(tick, mismatches, xErrors, yErrors, ref comparisons, ref resyncs);

                if (mode == ReplayMode.OpenLoop)
                    Anchor(tick);
            }

            Advance(tick);
            replayed++;

            if (!UpdatesPrecedeTheirTick && tick.HasPositions)
            {
                Reconcile(tick, mismatches, xErrors, yErrors, ref comparisons, ref resyncs);

                // Open-loop measures one prediction window at a time: compare,
                // then put the truth back and let the next window start from a
                // correct state. That is what the client experiences.
                if (mode == ReplayMode.OpenLoop)
                    Anchor(tick);
            }

            if (stopAtFirstBadTick && mode == ReplayMode.ClosedLoop && mismatches.Count > bad)
                break;
        }

        return new DriftReport
        {
            TraceName = traceName,
            Mode = mode,
            TicksReplayed = replayed,
            Comparisons = comparisons,
            Mismatches = mismatches,
            XErrors = xErrors,
            YErrors = yErrors,
            Resyncs = resyncs,
        };
    }

    /// <summary>
    /// Applies the roster and spawn events recorded before this tick's update.
    /// </summary>
    /// <remarks>
    /// A <c>PLAYER_SPAWN</c> is placed rather than simulated, but it is placed
    /// <em>exactly</em>: the message carries the spawn point id and group the
    /// server chose, and resolving that pair against the map is what every
    /// client does too (<c>doEventSpawn.gml</c>). Only the choice of point is
    /// RNG this port does not reproduce, and the choice is on the wire.
    /// </remarks>
    private void ApplyEvents(TraceTick tick)
    {
        foreach (var message in tick.Events)
        {
            switch (message.Type)
            {
                case MessageType.ChangeMap when message.Text is { } map:
                    simulation.LoadMap(map);
                    break;

                case MessageType.PlayerSpawn when message.PlayerId is { } spawned:
                    // The exact anchor. Payload is [player, spawnpointId,
                    // group]; resolving it against the map's own spawn point
                    // lists places the character on the integer coordinate the
                    // reference used, with both speeds zero, rather than on the
                    // next broadcast position — which arrives up to six ticks
                    // later, already fallen and rounded to a fifth of a pixel.
                    // A simulation that cannot resolve it returns false and
                    // Reconcile anchors from the wire as before.
                    if (message.Payload.Length >= 3)
                        simulation.SpawnCharacter(spawned, message.Payload[1], message.Payload[2]);
                    break;

                case MessageType.PlayerLeave when message.PlayerId is { } slot:
                    simulation.ClearCharacter(slot);
                    break;

                case MessageType.PlayerChangeTeam when message.PlayerId is { } changedTeam:
                    // Destroys the character on the client
                    // (ClientBeginStep.gml, case PLAYER_CHANGETEAM), so here too
                    // — but the team itself is remembered, because it decides
                    // which gates are solid to whatever spawns next.
                    if (message.Payload.Length >= 2)
                        simulation.SetPlayerTeam(changedTeam, (Team)message.Payload[1]);
                    simulation.ClearCharacter(changedTeam);
                    break;

                case MessageType.PlayerChangeClass when message.PlayerId is { } changedClass:
                    if (message.Payload.Length >= 2)
                        simulation.SetPlayerClass(changedClass, (PlayerClass)message.Payload[1]);
                    simulation.ClearCharacter(changedClass);
                    break;
            }
        }
    }

    /// <summary>Forces every slot's character to what the trace observed.</summary>
    private void Anchor(TraceTick tick)
    {
        for (var slot = 0; slot < tick.Slots.Length; slot++)
        {
            var recorded = tick.Slots[slot];

            if (recorded.Observed is { } observed)
                simulation.SetCharacter(slot, observed);
            else
                simulation.ClearCharacter(slot);
        }
    }

    private void Advance(TraceTick tick)
    {
        var inputs = new PlayerInput[tick.Slots.Length];

        for (var slot = 0; slot < inputs.Length; slot++)
            inputs[slot] = tick.Slots[slot].Input;

        simulation.Tick(inputs);
    }

    /// <summary>
    /// Compares each slot the simulation can be held to, and resynchronises the
    /// ones it cannot.
    /// </summary>
    /// <remarks>
    /// A slot the trace has a character for and the simulation does not is one
    /// <see cref="ITraceSimulation.SpawnCharacter"/> could not place — an
    /// unresolvable spawn point, or a character that was already alive when the
    /// recording started. Its position is taken from the trace rather than
    /// counted as a mismatch, which is lossy but is the only anchor available
    /// (docs/PARITY.md, "Where replay legitimately diverges"). A slot the
    /// simulation has and the trace does not has died or gone spectator.
    /// </remarks>
    private void Reconcile(
        TraceTick tick,
        List<Mismatch> mismatches,
        List<long> xErrors,
        List<long> yErrors,
        ref int comparisons,
        ref int resyncs)
    {
        for (var slot = 0; slot < tick.Slots.Length; slot++)
        {
            if (tick.Slots[slot].Observed is not { } expected)
            {
                simulation.ClearCharacter(slot);
                continue;
            }

            if (!simulation.TryGetCharacter(slot, out var actual))
            {
                simulation.SetCharacter(slot, expected);
                resyncs++;
                continue;
            }

            comparisons++;

            xErrors.Add(Math.Abs(actual.X - expected.X));
            yErrors.Add(Math.Abs(actual.Y - expected.Y));

            Check(Fields.Position, mismatches, tick.Index, slot, "x", expected.X, actual.X);
            Check(Fields.Position, mismatches, tick.Index, slot, "y", expected.Y, actual.Y);
            Check(Fields.Velocity, mismatches, tick.Index, slot, "hspeed", expected.HSpeed, actual.HSpeed);
            Check(Fields.Velocity, mismatches, tick.Index, slot, "vspeed", expected.VSpeed, actual.VSpeed);
            Check(Fields.Hp, mismatches, tick.Index, slot, "hp", expected.Hp, actual.Hp);
            Check(Fields.Ammo, mismatches, tick.Index, slot, "ammo", expected.Ammo, actual.Ammo);
            Check(Fields.Flags, mismatches, tick.Index, slot, "flags", expected.Flags, actual.Flags);
        }
    }

    private void Check(
        Fields field2,
        List<Mismatch> mismatches,
        int tick,
        int slot,
        string field,
        long expected,
        long actual)
    {
        if (expected == actual || (Compare & field2) == 0)
            return;

        mismatches.Add(new Mismatch
        {
            Tick = tick,
            Slot = slot,
            Field = field,
            Expected = expected,
            Actual = actual,
        });
    }
}
