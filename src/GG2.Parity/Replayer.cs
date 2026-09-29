using GG2.Protocol;

namespace GG2.Parity;

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
    /// <see cref="GG2.Parity.UberRecord"/>.
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

    /// <summary>
    /// Every recorded <c>DESTROY_SENTRY</c>, against the ones the port produced.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Hp"/> for the same reason <see cref="Deaths"/>
    /// is, and the argument is stronger still: <b>a sentry contributes
    /// nothing at all to an <c>INPUTSTATE</c>-tier update, not even its hp</b>
    /// (docs/PROTOCOL.md, "Per sentry"), so on most ticks a recording says
    /// nothing whatever about one. A destroyed sentry is merely absent from a
    /// later update's subobject bit, and nothing in that update names a killer.
    /// See <see cref="SentryRecord"/>.
    /// </remarks>
    Sentries = 1 << 8,

    /// <summary>
    /// Every control point, as each position-bearing update described it.
    /// </summary>
    /// <remarks>
    /// <b>The block rides on a <c>CAPS_UPDATE</c> or the join
    /// <c>FULL_UPDATE</c> and on nothing else</b> — <c>StateUpdateReader</c>
    /// returns before the HUD tail on any other tier — so a recording observes
    /// a control point only when the server chose to send one, a few dozen
    /// times in several thousand ticks, and on no fixed cadence.
    /// <para>
    /// <b>Nothing else in a recording says anything about a hill.</b> A port
    /// that never captured the point, captured it for the wrong team, or moved
    /// the meter at the wrong rate would replay a King of the Hill recording
    /// exactly as well as a correct one without this field.
    /// </para>
    /// </remarks>
    ControlPoints = 1 << 9,

    /// <summary>What the character physics is responsible for.</summary>
    Trajectory = Position | Velocity,

    All = Position | Velocity | Hp | Ammo | Flags | Deaths | Ubers | Intel | Sentries
        | ControlPoints,
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
    /// Feed each tick the keys and aim recorded <paramref name="InputLag"/>
    /// ticks away, instead of its own. Zero — the default — is the real replay.
    /// </summary>
    /// <remarks>
    /// <b>This exists to be swept, not to be set.</b> docs/PARITY.md's
    /// instrument 3: a stream whose inputs are in phase predicts best at lag 0,
    /// and one whose server published each tick's keys a tick late predicts best
    /// at <c>+1</c> — a CONTENT-phase bug the flush-boundary instruments cannot
    /// see at all.
    /// <para>
    /// <b>Only meaningful in <see cref="ReplayMode.OpenLoop"/>.</b> Closed
    /// loop diverges on the first mismatch and never gets far enough for a
    /// prediction error to mean anything; open loop re-anchors at every
    /// position-bearing tick, so each window is an independent measurement of
    /// how well those inputs explain the motion that followed.
    /// </para>
    /// <para>
    /// The lag shifts <b>only the inputs</b>. Everything else — the events,
    /// the fire seeds, the anchors — stays on its own tick, because the question
    /// is which tick's keys explain this tick's motion.
    /// </para>
    /// </remarks>
    public int InputLag { get; init; }

    /// <summary>
    /// Print this slot's SIMULATED state on every tick in
    /// <see cref="DumpFrom"/>..<see cref="DumpTo"/>, or null for no dump.
    /// </summary>
    /// <remarks>
    /// <b>A trace carries positions only every seventh tick, so the six ticks
    /// between two comparisons have no "expected" value at all.</b> When a
    /// divergence appears at one comparison and the one before it was exact, the
    /// error was made somewhere in that gap and the trace cannot say where. This
    /// dumps what the PORT did across the gap, which is the only per-tick view
    /// available — the reference's endpoints bracket it.
    /// <para>
    /// <b>Wire units, because that is what can be compared.</b> x and y are
    /// x*5, hspeed and vspeed are *8.5, all as the quantised bytes the reference
    /// would have sent. A dump in world units would be more readable and would
    /// not line up with the mismatch rows above it.
    /// </para>
    /// </remarks>
    public int? DumpSlot { get; init; }

    public int DumpFrom { get; init; }

    public int DumpTo { get; init; } = int.MaxValue;

    /// <summary>
    /// Called once per tick, with that tick's index, just before its inputs are
    /// consumed.
    /// </summary>
    /// <remarks>
    /// <b>For reading the reconstructed world, not for steering it.</b> The
    /// recorded stream is the only thing that decides what a replay does; this
    /// exists so a caller can pair the state at the start of a tick with the
    /// action the recording says was taken on it, which is the
    /// <c>(observation, action)</c> a dataset is made of.
    /// <para>
    /// <b>The instant is chosen and not incidental.</b> Placed after the tick's
    /// roster and spawn events have been applied and after an open-loop anchor
    /// has put the recorded positions back, and before <c>Advance</c> — so the
    /// state is what the player was looking at when they pressed the keys this
    /// tick carries. A hook after <c>Advance</c> would pair every action with
    /// the state it produced rather than the one that caused it, which is a
    /// one-tick lag no consumer could detect and every consumer would train on.
    /// </para>
    /// <para>
    /// The simulation is not passed in: the caller built it, so it can close
    /// over its own instance and read whatever that concrete type exposes.
    /// </para>
    /// </remarks>
    public Action<int>? OnTick { get; init; }

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
            {
                SupplyFireSeeds(ticks[index + 1]);

                // And so is a spawn, for exactly the same reason: the alarm
                // stage runs after the state has already been serialised, so a
                // character created on this tick is first announced on the next
                // one. See QueueSpawns.
                QueueSpawns(ticks[index + 1]);
            }

            var bad = mismatches.Count;

            // One update carries this tick's input and last tick's position, so
            // the comparison happens before the inputs are consumed.
            // Dumped HERE, at the same instant Reconcile compares, so a dumped
            // row and a mismatch row for the same tick describe the same moment.
            Dump(tick.Index, tick.HasPositions);

            if (UpdatesPrecedeTheirTick && tick.HasPositions)
            {
                Reconcile(tick, mismatches, xErrors, yErrors, ref comparisons, ref resyncs);

                if (mode == ReplayMode.OpenLoop)
                    Anchor(tick);
            }

            // BEFORE the tick runs and before any spawn it queues, because
            // WorldState.Spawn reads it when it builds a character. A recording
            // carries exactly one FULL_UPDATE, at the join, so in practice this
            // fires once and long before the spawn wave — but ordering it here
            // is what makes that true rather than lucky.
            //
            // And `is { }` rather than `is > 0`, unlike the unlock beside it:
            // zero is a legitimate setting (a server with invulnerability turned
            // off) where a zero unlock is merely "already unlocked" and
            // ambiguous. The null check is doing the work the > 0 does there.
            if (tick.TdmInvulnerability is { } invulnerability)
                simulation.SetTdmInvulnerability(invulnerability);

            if (tick.ControlPointUnlock is > 0 and var unlock)
                simulation.SetControlPointUnlock(unlock);

            // BEFORE Advance, and that is not cosmetic. The control-point
            // block rides in this tick's state update, which — like the
            // positions beside it — was serialised in the server's Begin Step
            // and therefore describes the END OF THE PREVIOUS TICK. Comparing it
            // after Advance holds the port's end-of-this-tick meter against the
            // reference's end-of-last-tick one, and the port reads high by
            // exactly one tick of capture.
            //
            // Measured: comparing after Advance made the port look like it
            // started every capture two ticks early — +2 on a soldier recording
            // and +4 on a scout one. Both fixtures are exact with the comparison
            // here instead. docs/PARITY.md, "the capture meter was a harness bug".
            //
            // The unlock injection above must stay AHEAD of it, because it
            // re-locks and zeroes the meter: comparing first would hold the port
            // against a value the injection is about to wipe. Measured too — it
            // costs reference-koth_valley-uncontested.trace its exactness.
            //
            // NOT gated on HasPositions. The block arrives on a CAPS_UPDATE,
            // whose arrival is independent of the tick's own tier — so gating
            // this on the positional cadence silently skipped every observation
            // that landed on an INPUTSTATE tick, which was most of them.
            if (tick.ControlPoints.Length > 0)
            {
                ReconcileControlPoints(tick, mismatches);
            }

            // The last look at the world before this tick's keys are consumed —
            // see OnTick for why the instant matters.
            OnTick?.Invoke(tick.Index);

            // The inputs may come from a DIFFERENT tick — see InputLag. At
            // the default of 0 this is `ticks[index]`, i.e. `tick` itself.
            Advance(tick, ticks[Math.Clamp(index + InputLag, 0, ticks.Count - 1)]);
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

                // Read out of the FOLLOWING tick like a death, and for the
                // same reason: the turret fires from End Step, which runs after
                // the server has already serialised this tick.
                ReconcileSentries(tick.Index, ticks[index + 1], mismatches);
            }

            // Injected on EVERY observation, like a PLAYER_SPAWN and for the
            // same reason: a replay cannot derive it. The King of the Hill
            // pregame timer starts when the server restarts the room, and a
            // recording's only landmark is the CHANGE_MAP that announced the
            // change several hundred ticks earlier. Measured on two recordings:
            // both carry one CHANGE_MAP at t0 and unlock at t1236 and t1160,
            // neither of which is the 901 the timer actually counts.
            // KothUnlockTimingTests gates that arithmetic directly instead.
            //
            // Anchoring only ONCE does not work, and the reason is worth
            // keeping: the FIRST observation is the join FULL_UPDATE, which
            // describes the round the recorder joined *before* the reload — on
            // reference-koth_valley-uncontested.trace it reads 0, an already
            // unlocked point, so a one-shot anchor unlocks the replay's hill
            // immediately. The new round's countdown first appears at t1086
            // reading 149.
            //
            // Only a POSITIVE reading, and that is the whole trick. A zero
            // is ambiguous: it means "already unlocked", which is true both of
            // the round the recorder joined and of the one after the reload —
            // and the server keeps serialising the OLD round's HUD for a few
            // hundred ticks after announcing the change, so the first readings
            // are stale. On reference-koth_valley-uncontested.trace the wire
            // says 0 at t0 and t62 and does not show the new round's countdown
            // until t1086, reading 149.
            //
            // A positive value is unambiguous — the server only fires the unlock
            // when the counter reads 0 — so it says both "this many ticks left"
            // and "still locked", which is why the simulation re-locks on it.
            //
            // The delay is NOT a constant to subtract: the two KotH
            // recordings restart their rounds 335 and 254 ticks after their
            // CHANGE_MAP.
            //
            // What this concedes is the countdown, which is `-1` a tick and
            // unit-tested. What it still gates is everything downstream: who is
            // standing on the point, the capture rate, and ownership.
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
    /// <summary>Slots whose spawn the previous tick's alarm stage has taken on.</summary>
    private readonly HashSet<int> _queued = [];

    /// <summary>
    /// Asks the simulation to spawn, in the tick about to run, whoever the
    /// <em>next</em> tick's messages report as having spawned.
    /// </summary>
    /// <remarks>
    /// <b>A <c>PLAYER_SPAWN</c> in tick T's block describes tick T-1</b>, the
    /// same off-by-one a fire seed and a death have
    /// (<see cref="SupplyFireSeeds"/>): <c>GameServerBeginStep.gml</c> serialises
    /// the state inside Begin Step, and the alarm stage that runs
    /// <c>PlayerSpawn</c> comes after it — so a character created on a tick
    /// cannot appear in that tick's own update and is announced on the next.
    /// <para>
    /// That one tick is the whole point. A character created in the alarm stage
    /// gets a Step and an End Step but <b>no Begin Step</b>, so its first End
    /// Step still sees <c>vspeed == 0</c> and takes the stair descent's
    /// six-pixel snap. Queueing on the tick the message *arrives* is a tick late
    /// and breaks every spawn-anchored fixture; queueing a tick early — here —
    /// is what the reference does. Measured both ways.
    /// </para>
    /// </remarks>
    private void QueueSpawns(TraceTick next)
    {
        foreach (var message in next.Events)
        {
            if (message.Type != MessageType.PlayerSpawn
                || message.PlayerId is not { } slot
                || message.Payload.Length < 3)
            {
                continue;
            }

            if (simulation.QueueSpawn(slot, message.Payload[1], message.Payload[2]))
                _queued.Add(slot);
        }
    }

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
                    //
                    // And ask for it through the ALARM STAGE first. A
                    // character created there gets no Begin Step on its first
                    // tick, so its first End Step still sees vspeed == 0 and
                    // takes the stair descent's six-pixel snap. Placing it here
                    // instead hands it this tick's gravity and misses the snap —
                    // invisible on ctf_truefort, whose markers are eighteen
                    // pixels up, and worth 0.4 px for ever on a map whose
                    // markers are exactly six.
                    //
                    // Unless the previous tick already queued it — see
                    // QueueSpawns, which is where a spawn is normally handled
                    // now. This arm is the fallback for a spawn the simulation
                    // could not put through its alarm stage, and for the first
                    // tick of a replay, which has no previous tick to have
                    // queued it.
                    if (message.Payload.Length >= 3 && !_queued.Remove(spawned))
                        simulation.SpawnCharacter(spawned, message.Payload[1], message.Payload[2]);

                    break;

                case MessageType.PlayerLeave when message.PlayerId is { } slot:
                    // RemoveSlot, not ClearCharacter. A roster index is
                    // positional: the server compacts on a departure and its
                    // state updates are already compacted by the time they are
                    // recorded, so clearing the slot in place would leave this
                    // simulation comparing its slot 3 against the recording's
                    // slot 3 while they name different players.
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

                // The slot comes from the PAYLOAD, not from PlayerId.
                // ServerStream frames this through Fixed(), which skips the body
                // without decoding it, so PlayerId is null for every message in
                // that family.
                case MessageType.DropIntel when message.Payload.Length >= 1:
                    // Applied BEFORE this tick runs, like a BUILD_SENTRY and
                    // for the same reason: processClientCommands runs inside the
                    // server's Begin Step, ahead of serializeState, so the
                    // message lands in its OWN tick's block rather than the
                    // next one's.
                    simulation.DropIntel(message.Payload[0]);
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

                // The other two client commands. Neither is a key bit, so
                // nothing in an INPUTSTATE says a sniper scoped or a heavy ate —
                // exactly the BUILD_SENTRY situation. Both change physics
                // (runPower and
                // jumpStrength; frozen acceleration, frozen facing and 1.6 hp a
                // tick), so omitting either is a trajectory divergence.
                //
                // Applied before the tick, like a build: processClientCommands
                // runs inside GameServerBeginStep, ahead of serializeState.
                case MessageType.ToggleZoom when message.Payload.Length >= 1:
                    simulation.ToggleZoom(message.Payload[0]);
                    break;

                case MessageType.Omnomnomnom when message.Payload.Length >= 1:
                    simulation.EatSandvich(message.Payload[0]);
                    break;

                // Stage 2 of autobalance. The server moves a player between
                // teams and destroys their character with no PLAYER_DEATH — this
                // two-byte message is the whole record of it. `255` is stage 1's
                // notice, which moves nobody.
                case MessageType.Balance
                    when message.Payload.Length >= 2 && message.Payload[0] != ServerStream.BalanceNotice:
                    simulation.BalanceSwitch(message.Payload[0], (PlayerClass)message.Payload[1]);
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

    /// <summary>
    /// Every control point the update described, against the port's own.
    /// </summary>
    /// <remarks>
    /// <b>Three fields, and each one catches a different wrong port.</b>
    /// <c>Team</c> is who owns it, so a port that never completes a capture
    /// fails there; <c>CappingTeam</c> is who is working on it, which catches a
    /// port that ignores presence or contests wrongly; and <c>Capping</c> is the
    /// meter itself, which catches a capture rate that is merely the wrong
    /// speed.
    /// <para>
    /// <b>Compared by index, which is load order.</b> The block carries no
    /// identifier — <c>ControlPoints.Place</c> fixes the order once at load and
    /// the wire relies on both ends agreeing about it, which is the same
    /// property that makes the count-prefix-free framing work at all.
    /// </para>
    /// </remarks>
    private void ReconcileControlPoints(TraceTick tick, List<Mismatch> mismatches)
    {
        if ((Compare & Fields.ControlPoints) == 0)
            return;

        for (var i = 0; i < tick.ControlPoints.Length; i++)
        {
            var expected = tick.ControlPoints[i];

            if (!simulation.TryGetControlPoint(i, out var actual))
            {
                mismatches.Add(new Mismatch
                {
                    Tick = tick.Index,
                    Slot = i,
                    Field = "cp",
                    Expected = 1,
                    Actual = 0,
                });

                continue;
            }

            Compare(nameof(expected.Team), expected.Team, actual.Team);
            Compare("cappingTeam", expected.CappingTeam, actual.CappingTeam);
            Compare("capping", expected.Capping, actual.Capping);

            void Compare(string field, long want, long got)
            {
                if (want == got)
                    return;

                mismatches.Add(new Mismatch
                {
                    Tick = tick.Index,
                    Slot = i,
                    Field = $"cp{field}",
                    Expected = want,
                    Actual = got,
                });
            }
        }
    }

    /// <summary>
    /// Every <c>DESTROY_SENTRY</c> the recording holds, against every one the
    /// port produced.
    /// </summary>
    /// <remarks>
    /// <b>The missing direction is the one worth having.</b> A port that never
    /// destroyed a sentry at all passes every state comparison in this file: a
    /// sentry writes no bytes whatever on an <c>INPUTSTATE</c>, so on six ticks
    /// in seven the recording is silent about it, and on the seventh a destroyed
    /// one is simply an unset subobject bit that names nobody. Only the absent
    /// message says so.
    /// <para>
    /// Matched as a multiset like <see cref="ReconcileIntel"/>, so two sentries
    /// dying on one tick do not have to be produced in the reference's order.
    /// </para>
    /// </remarks>
    private void ReconcileSentries(int tickIndex, TraceTick next, List<Mismatch> mismatches)
    {
        if ((Compare & Fields.Sentries) == 0)
            return;

        var unmatched = new List<SentryRecord>(simulation.SentriesLastTick);

        foreach (var message in next.Events)
        {
            if (!TryReadSentry(message, out var expected))
                continue;

            if (unmatched.Remove(expected))
                continue;

            mismatches.Add(new Mismatch
            {
                Tick = tickIndex,
                Slot = expected.Slot,
                Field = "destroysentry",
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
                Field = "destroysentry",
                Expected = 0,
                Actual = 1,
            });
        }
    }

    /// <summary>
    /// One <c>DESTROY_SENTRY</c> body: <c>ubyte playerId</c>,
    /// <c>ubyte killer</c>, <c>ubyte assistant</c>, <c>ubyte cause</c> —
    /// <c>sendEventDestroySentry.gml</c>, the same shape as a
    /// <c>PLAYER_DEATH</c>.
    /// </summary>
    private static bool TryReadSentry(ServerMessage message, out SentryRecord sentry)
    {
        if (message.Type == MessageType.DestroySentry && message.Payload.Length >= 4)
        {
            sentry = new SentryRecord(
                message.Payload[0], message.Payload[1], message.Payload[2], message.Payload[3]);
            return true;
        }

        sentry = default;
        return false;
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
            // A slot whose spawn is queued for this tick's alarm stage is
            // left alone here as well as in Reconcile: the trace cannot show the
            // character yet — the state was serialised before the alarm stage ran
            // — so the `else` arm below would clear a slot that is deliberately
            // about to be filled by the tick itself.
            if (simulation.HasPendingSpawn(slot))
                continue;

            var recorded = tick.Slots[slot];

            if (recorded.Observed is { } observed)
                simulation.SetCharacter(slot, observed);
            else
                simulation.ClearCharacter(slot);
        }
    }

    /// <summary>One line of the port's own state, for a tick inside the window.</summary>
    private void Dump(int tick, bool compared)
    {
        if (DumpSlot is not { } slot || tick < DumpFrom || tick > DumpTo)
            return;

        if (!simulation.TryGetCharacter(slot, out var c))
        {
            Console.WriteLine($"  tick {tick,5}  slot {slot}: no character");
            return;
        }

        // The marker matters: only the starred rows have anything in the trace
        // to be right or wrong about. The rest are the port talking to itself.
        Console.WriteLine(
            $"  tick {tick,5} {(compared ? "*" : " ")} x {c.X,6} y {c.Y,6} "
            + $"hspeed {c.HSpeed,5} vspeed {c.VSpeed,5} "
            + $"moveStatus {c.MoveStatus} hp {c.Hp,4}");
    }

    /// <param name="tick">The tick being run, which sizes the input array.</param>
    /// <param name="inputs">
    /// The tick whose keys and aim to use. The same object as
    /// <paramref name="tick"/> unless <see cref="InputLag"/> is set.
    /// </param>
    /// <remarks>
    /// <b>The ROSTER comes from <paramref name="tick"/>, the keys from
    /// <paramref name="inputs"/>.</b> A lagged tick can have a different number
    /// of slots — somebody joined or left in between — and taking the length
    /// from the wrong one would hand the simulation a roster it is not on. A
    /// slot the lagged tick does not have keeps its own tick's input, which is
    /// the same fallback a slot that sent nothing gets.
    /// </remarks>
    private void Advance(TraceTick tick, TraceTick inputs)
    {
        var built = new PlayerInput[tick.Slots.Length];

        for (var slot = 0; slot < built.Length; slot++)
        {
            built[slot] = slot < inputs.Slots.Length
                ? inputs.Slots[slot].Input
                : tick.Slots[slot].Input;
        }

        simulation.Tick(built);
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
            // A slot whose spawn is queued for this tick's alarm stage is
            // deliberately not there yet, and must be left alone in both
            // directions. The trace cannot show it either — the state was
            // serialised before the alarm stage ran — so the arm below would
            // clear the slot, and if the trace does show it, anchoring from the
            // wire would throw away the exact spawn-point placement QueueSpawn
            // exists to keep and count a resync against a replay doing the right
            // thing.
            if (simulation.HasPendingSpawn(slot))
                continue;

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
