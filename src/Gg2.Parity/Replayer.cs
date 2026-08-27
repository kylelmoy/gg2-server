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

    /// <summary>
    /// Every recorded <c>PLAYER_DEATH</c>, against the ones the port produced.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Hp"/> because it is a much stronger claim than
    /// the hp comparison makes. A slot's hp is only observed every seventh tick
    /// and a dead character is simply absent from the update, so hp alone cannot
    /// say <em>which</em> tick somebody died on, and says nothing at all about
    /// who got the kill.
    /// </remarks>
    Deaths = 1 << 5,

    /// <summary>
    /// Every recorded <c>UBER_CHARGED</c> and <c>UBER</c>, against the ones the
    /// port produced.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Hp"/> for the same reason <see cref="Deaths"/>
    /// is, and the argument is even stronger here: a medic's charge reaches the
    /// wire only as one saturating byte in a class-specific field, so a port
    /// that charged twice as fast would look identical from the moment it
    /// topped out. The message names the tick. See
    /// <see cref="Gg2.Parity.UberRecord"/>.
    /// </remarks>
    Ubers = 1 << 6,

    /// <summary>
    /// Every recorded <c>GRAB_INTEL</c>, <c>DROP_INTEL</c>, <c>SCORE_INTEL</c>
    /// and <c>RETURN_INTEL</c>, against the ones the port produced.
    /// </summary>
    /// <remarks>
    /// Separate from everything else because nothing else can see the
    /// objective at all: the briefcase's position rides only in a
    /// <c>FULL_UPDATE</c>, of which a recording holds exactly one, and a
    /// carrier's <c>intel</c> byte is in the same block. These four messages
    /// are the whole of what a recording says about the flag — and a grab in
    /// particular is a strong claim, because it lands on the tick a character's
    /// box first overlaps the briefcase's. See <see cref="IntelRecord"/>.
    /// </remarks>
    Intel = 1 << 7,

    /// <summary>What the character physics is responsible for.</summary>
    Trajectory = Position | Velocity,

    All = Position | Velocity | Hp | Ammo | Flags | Deaths | Ubers | Intel,
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

        for (var index = 0; index < ticks.Count; index++)
        {
            var tick = ticks[index];

            ApplyEvents(tick);

            // The seeds for the tick about to run are recorded in the *next*
            // tick's events; see SupplyFireSeeds.
            if (index + 1 < ticks.Count)
                SupplyFireSeeds(ticks[index + 1]);

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

            // A death is reported in the *next* tick's message block, for the
            // same reason a fire seed is — see SupplyFireSeeds. A tick whose
            // successor was truncated off the end of the recording is not
            // checked, because the recording does not say who died on it.
            //
            // The same is true of an uber message: it comes out of a Begin Step
            // that runs after the server has already serialised this tick.
            if (index + 1 < ticks.Count)
            {
                ReconcileDeaths(tick.Index, ticks[index + 1], mismatches);
                ReconcileUbers(tick.Index, ticks[index + 1], mismatches);
                ReconcileIntel(tick.Index, ticks[index + 1], mismatches);
            }

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
                    // ⚠️ RemoveSlot, not ClearCharacter. A roster index is
                    // positional: the server compacts on a departure and its
                    // state updates are already compacted by the time they are
                    // recorded, so clearing the slot in place would leave this
                    // simulation comparing its slot 3 against the recording's
                    // slot 3 while they name different players.
                    //
                    // This read ClearCharacter until 2026-08-25, and
                    // reference-ctf_truefort-combat.trace is what caught it —
                    // its medic leaves on tick 13, and from the next update
                    // onwards every remaining bot was compared against its
                    // neighbour. The bots trace has no departure in it and never
                    // noticed.
                    simulation.RemoveSlot(slot);
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

                case MessageType.BuildSentry when message.Payload.Length >= 6:
                    // [player, x*5 (ushort), y*5 (ushort), image_xscale
                    // (signed byte)] — serverBuildSentry.gml's broadcast. An
                    // injection like PLAYER_SPAWN: the message is the
                    // reference's own acceptance of the build, so the
                    // preconditions are not re-run. Everything after the
                    // placement is simulated; SENTRY_POSITION and
                    // DESTROY_SENTRY in the recording are not injected (the
                    // fall and the death are the simulation's to reproduce).
                    simulation.BuildSentry(
                        message.Payload[0],
                        (message.Payload[1] | (message.Payload[2] << 8)) / 5.0,
                        (message.Payload[3] | (message.Payload[4] << 8)) / 5.0,
                        (sbyte)message.Payload[5]);
                    break;
            }
        }
    }

    /// <summary>
    /// Hands the simulation the seeds the reference drew for the tick that is
    /// about to run.
    /// </summary>
    /// <remarks>
    /// <b>The seeds live one tick later in the stream than the shots they
    /// belong to, and that is the reference's own ordering rather than a quirk
    /// of this harness.</b> <c>GameServerBeginStep.gml</c> serialises the state
    /// in the <em>server's</em> Begin Step, and a character fires in the
    /// <em>character's</em> — and the <c>GameServer</c> instance is older than
    /// any <c>Character</c>, so GM8 runs it first. One tick therefore writes its
    /// state update and then, further down the same buffer, any
    /// <c>WEAPON_FIRE</c> the same tick produced. <see cref="TraceParser"/>
    /// closes a tick at each state update, so those messages land in the
    /// <em>following</em> tick's <see cref="TraceTick.Events"/>.
    /// <para>
    /// Cross-checked against the fixture rather than reasoned about alone: in
    /// <c>reference-ctf_truefort-bots.trace</c> the fire attributed to tick 389
    /// names a position 2542.2, and slot 3 — travelling at the 2.588 px/tick the
    /// same message reports — is at that position at the end of tick 387, which
    /// is what a Begin Step on tick 388 reads.
    /// </para>
    /// <para>
    /// A tick whose successor was truncated off the end of the recording gets no
    /// seed. That is correct: the recording does not say whether it fired.
    /// </para>
    /// </remarks>
    private void SupplyFireSeeds(TraceTick next)
    {
        foreach (var message in next.Events)
        {
            if (message.Type != MessageType.WeaponFire)
                continue;

            // [player, x, y, hspeed, vspeed, seed] — sendEventFireWeapon.gml,
            // nine bytes, with the seed as the trailing little-endian ushort.
            if (message.Payload.Length < 9)
                continue;

            var reader = new NetReader(message.Payload);
            var slot = reader.ReadUByte();
            reader.Skip(6);

            simulation.SetFireSeed(slot, reader.ReadUShort());
        }
    }

    /// <summary>
    /// Compares the deaths the tick just run produced against the ones the
    /// reference recorded for it.
    /// </summary>
    /// <remarks>
    /// <b>Both directions, and the missing one is the one worth having.</b> A
    /// death the recording has and the simulation does not is invisible to
    /// every other comparison: the victim is simply absent from the next state
    /// update, and the replayer's own <see cref="Reconcile"/> would quietly
    /// clear the slot to match and report nothing. Before this check existed,
    /// a port that never killed anybody replayed
    /// <c>reference-ctf_truefort-bots.trace</c> with zero mismatches.
    /// <para>
    /// Keyed on the victim, because that is the one field a death is unique by:
    /// a character that dies is destroyed, so no slot can appear twice in a
    /// tick. That also makes the comparison independent of the order the two
    /// sides list them in — GM8 runs End Step in <em>instance</em> order, which
    /// is creation order and not roster order, so matching them off pairwise
    /// would report a false mismatch whenever two people die on the same tick
    /// and the older character has the higher slot.
    /// </para>
    /// </remarks>
    private void ReconcileDeaths(int tickIndex, TraceTick next, List<Mismatch> mismatches)
    {
        if ((Compare & Fields.Deaths) == 0)
            return;

        var actual = simulation.DeathsLastTick;

        foreach (var message in next.Events)
        {
            if (!TryReadDeath(message, out var expected))
                continue;

            if (!TryFindDeath(actual, expected.Slot, out var got))
            {
                mismatches.Add(new Mismatch
                {
                    Tick = tickIndex,
                    Slot = expected.Slot,
                    Field = "death",
                    Expected = 1,
                    Actual = 0,
                });
                continue;
            }

            Check(Fields.Deaths, mismatches, tickIndex, expected.Slot, "killer", expected.Killer, got.Killer);
            Check(Fields.Deaths, mismatches, tickIndex, expected.Slot, "assistant", expected.Assistant, got.Assistant);
            Check(Fields.Deaths, mismatches, tickIndex, expected.Slot, "deathcause", expected.Source, got.Source);
        }

        foreach (var got in actual)
        {
            if (RecordsDeathOf(next, got.Slot))
                continue;

            mismatches.Add(new Mismatch
            {
                Tick = tickIndex,
                Slot = got.Slot,
                Field = "death",
                Expected = 0,
                Actual = 1,
            });
        }
    }

    /// <summary>
    /// Holds the simulation to every uber message the reference recorded, and
    /// to no others.
    /// </summary>
    /// <remarks>
    /// <b>Both directions, and the missing one is again the one worth
    /// having.</b> A port that never charged an uber would sail through every
    /// other comparison in this file: the charge byte would simply stay low,
    /// and low is a legal value. Only the absent message says so.
    /// <para>
    /// Matched as a multiset rather than keyed, because unlike a death an uber
    /// is not unique by slot — a medic can in principle fill and pop on the same
    /// tick, and the two messages then differ only in their type. Each recorded
    /// message consumes one produced message, so a duplicate on either side is
    /// reported.
    /// </para>
    /// </remarks>
    private void ReconcileUbers(int tickIndex, TraceTick next, List<Mismatch> mismatches)
    {
        if ((Compare & Fields.Ubers) == 0)
            return;

        // Copied because entries are struck off as they are matched, and the
        // simulation owns the list it handed us.
        var unmatched = new List<UberRecord>(simulation.UbersLastTick);

        foreach (var message in next.Events)
        {
            if (!TryReadUber(message, out var expected))
                continue;

            if (unmatched.Remove(expected))
                continue;

            mismatches.Add(new Mismatch
            {
                Tick = tickIndex,
                Slot = expected.Slot,
                Field = expected.Event == UberEvent.Charged ? "ubercharged" : "uber",
                Expected = 1,
                Actual = 0,
            });
        }

        foreach (var got in unmatched)
        {
            mismatches.Add(new Mismatch
            {
                Tick = tickIndex,
                Slot = got.Slot,
                Field = got.Event == UberEvent.Charged ? "ubercharged" : "uber",
                Expected = 0,
                Actual = 1,
            });
        }
    }

    /// <summary>
    /// Holds the simulation to every intel message the reference recorded, and
    /// to no others.
    /// </summary>
    /// <remarks>
    /// A multiset like <see cref="ReconcileUbers"/> rather than a keyed lookup
    /// like <see cref="ReconcileDeaths"/>: a slot is not unique here — an
    /// ubered carrier can drop and re-grab the same flag on one tick — and a
    /// <c>RETURN_INTEL</c> names a team rather than a player, so there is no
    /// roster index to key on at all.
    /// <para>
    /// <b>The missing direction is again the one worth having.</b> A port that
    /// never picked the flag up would pass every other comparison in this file
    /// without a murmur: the carrier's <c>intel</c> byte is <c>FULL_UPDATE</c>
    /// only, the briefcase is not in an <c>INPUTSTATE</c> or a
    /// <c>QUICK_UPDATE</c> either, and carrying one changes no trajectory except
    /// through the intel gates. Only the absent message says so.
    /// </para>
    /// </remarks>
    private void ReconcileIntel(int tickIndex, TraceTick next, List<Mismatch> mismatches)
    {
        if ((Compare & Fields.Intel) == 0)
            return;

        var unmatched = new List<IntelRecord>(simulation.IntelLastTick);

        foreach (var message in next.Events)
        {
            if (!TryReadIntel(message, out var expected))
                continue;

            if (unmatched.Remove(expected))
                continue;

            mismatches.Add(new Mismatch
            {
                Tick = tickIndex,
                Slot = expected.Id,
                Field = NameOf(expected.Event),
                Expected = 1,
                Actual = 0,
            });
        }

        foreach (var got in unmatched)
        {
            mismatches.Add(new Mismatch
            {
                Tick = tickIndex,
                Slot = got.Id,
                Field = NameOf(got.Event),
                Expected = 0,
                Actual = 1,
            });
        }
    }

    private static string NameOf(IntelEvent which) => which switch
    {
        IntelEvent.Grabbed => "grabintel",
        IntelEvent.Dropped => "dropintel",
        IntelEvent.Scored => "scoreintel",
        _ => "returnintel",
    };

    /// <summary>
    /// One intel message body: a single byte, which is a roster index for three
    /// of the four and a team for <c>RETURN_INTEL</c>.
    /// </summary>
    private static bool TryReadIntel(ServerMessage message, out IntelRecord intel)
    {
        if (message.Payload.Length >= 1)
        {
            switch (message.Type)
            {
                case MessageType.GrabIntel:
                    intel = new IntelRecord(message.Payload[0], IntelEvent.Grabbed);
                    return true;

                case MessageType.DropIntel:
                    intel = new IntelRecord(message.Payload[0], IntelEvent.Dropped);
                    return true;

                case MessageType.ScoreIntel:
                    intel = new IntelRecord(message.Payload[0], IntelEvent.Scored);
                    return true;

                case MessageType.ReturnIntel:
                    intel = new IntelRecord(message.Payload[0], IntelEvent.Returned);
                    return true;
            }
        }

        intel = default;
        return false;
    }

    /// <summary>
    /// One <c>UBER_CHARGED</c> or <c>UBER</c> body: a single player byte,
    /// <c>sendEventUberReady.gml</c> and <c>sendEventUber.gml</c>.
    /// </summary>
    private static bool TryReadUber(ServerMessage message, out UberRecord uber)
    {
        if (message.Payload.Length >= 1)
        {
            switch (message.Type)
            {
                case MessageType.UberCharged:
                    uber = new UberRecord(message.Payload[0], UberEvent.Charged);
                    return true;

                case MessageType.Uber:
                    uber = new UberRecord(message.Payload[0], UberEvent.Started);
                    return true;
            }
        }

        uber = default;
        return false;
    }

    /// <summary>
    /// One <c>PLAYER_DEATH</c> body: <c>[victim, killer, assistant, cause]</c>,
    /// <c>sendEventPlayerDeath.gml</c>.
    /// </summary>
    private static bool TryReadDeath(ServerMessage message, out DeathRecord death)
    {
        if (message.Type != MessageType.PlayerDeath || message.Payload.Length < 4)
        {
            death = default;
            return false;
        }

        death = new DeathRecord(
            message.Payload[0],
            message.Payload[1],
            message.Payload[2],
            message.Payload[3]);

        return true;
    }

    private static bool TryFindDeath(IReadOnlyList<DeathRecord> deaths, byte slot, out DeathRecord found)
    {
        for (var i = 0; i < deaths.Count; i++)
        {
            if (deaths[i].Slot != slot)
                continue;

            found = deaths[i];
            return true;
        }

        found = default;
        return false;
    }

    private static bool RecordsDeathOf(TraceTick tick, byte slot)
    {
        foreach (var message in tick.Events)
        {
            if (TryReadDeath(message, out var death) && death.Slot == slot)
                return true;
        }

        return false;
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
