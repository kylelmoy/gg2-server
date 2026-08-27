namespace Gg2.Protocol;

/// <summary>
/// Turns a recorded stream into ticks.
/// </summary>
/// <remarks>
/// The whole of docs/PARITY.md's "key realisation" is implemented here: the
/// server's broadcast carries <b>every player's action every tick</b>
/// (<c>INPUTSTATE</c>) and <b>every player's position every seventh</b>
/// (<c>QUICK_UPDATE</c>), already quantised to what a client can observe. So one
/// recording holds both the inputs a simulation consumed and the outputs it
/// produced, and a replay is a matter of feeding the first back in and checking
/// the second.
/// </remarks>
public static class TraceParser
{
    /// <summary>
    /// Parses a trace into ticks. The first tick is the first state update
    /// after the trace's opening <c>FULL_UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// The opening <c>FULL_UPDATE</c> is kept separately as
    /// <paramref name="start"/>: it is the only message carrying complete
    /// initial state, and a replay has nowhere to begin without it.
    /// </remarks>
    public static IReadOnlyList<TraceTick> Parse(Trace trace, in WorldShape shape, out StateUpdate? start) =>
        Parse(trace, shape, out start, out _);

    /// <summary>
    /// Parses a trace, and says whether it framed all the way to the last byte.
    /// </summary>
    /// <param name="stopped">
    /// Null when the whole stream framed. Otherwise why the framer gave up and
    /// where, and everything after that offset was silently dropped.
    /// </param>
    /// <remarks>
    /// <b>The overload that exists because the other one hides a fixture-killing
    /// bug.</b> A recording can legitimately stop mid-message — the socket
    /// closed between one read and the next — so the loop treats a
    /// <see cref="ProtocolException"/> as the end of the trace and returns what
    /// it has. That is right for a truncated capture and catastrophic for a
    /// desynchronised reader: a trace whose framing slipped at byte 500 of
    /// 60,000 comes back as a handful of ticks, every test that loads it passes,
    /// and nothing anywhere says the other 59,500 bytes were thrown away.
    /// <para>
    /// That is exactly what an engineer's sentry did on 2026-08-26
    /// (<c>StateUpdateReader.ReadSentry</c>). Use this overload in any test that
    /// asserts something about a fixture's <em>contents</em>.
    /// </para>
    /// </remarks>
    /// <param name="shapeForMap">
    /// Resolves a map name to its wire shape, so the reader can follow a
    /// <c>CHANGE_MAP</c> onto a map of a different gamemode.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>Without <paramref name="shapeForMap"/> a trace is framed by one
    /// shape from end to end, and a rotation to another gamemode desynchronises
    /// it.</b> Measured on
    /// <c>reference-gen_destroy-rotationcaps.trace</c>: framing stops at byte
    /// 79,467 of 124,340 when the reader keeps the Generator shape across a
    /// change to <c>ctf_2dfort</c>.
    /// <para>
    /// It went unnoticed for a long time because <b>only <c>FULL_UPDATE</c> and
    /// <c>CAPS_UPDATE</c> carry the HUD block</b> — <c>StateUpdateReader</c>
    /// returns before the tail on every other tier — so a wrong shape is
    /// harmless until one of those arrives after the change.
    /// <c>reference-gen_destroy-rotation.trace</c> rotates gamemode and frames
    /// perfectly, purely because none does.
    /// </para>
    /// <para>
    /// Null keeps the old single-shape behaviour, which is correct for every
    /// recording that never changes map.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TraceTick> Parse(
        Trace trace,
        in WorldShape shape,
        out StateUpdate? start,
        out string? stopped,
        Func<string, WorldShape>? shapeForMap = null)
    {
        stopped = null;
        var stream = new ServerStream();
        stream.SetShape(shape);

        var reader = new NetReader(trace.Stream);
        var ticks = new List<TraceTick>();
        var pending = new List<ServerMessage>();

        start = null;

        while (reader.Remaining > 0)
        {
            ServerMessage message;
            try
            {
                message = stream.Read(ref reader);
            }
            catch (ProtocolException e)
            {
                // A recording can stop mid-message: the socket closed between
                // one read and the next. Everything before it is still a trace.
                //
                // But it is only harmless when the leftover is a fragment. Say
                // how much was dropped so a caller can tell a truncated capture
                // from a reader that lost the stream — see the remarks above.
                stopped =
                    $"framing stopped at byte {reader.Position} of {trace.Stream.Length}, " +
                    $"{reader.Remaining} byte(s) unread: {e.Message}";
                break;
            }

            // ⚠️ Re-shape before anything else reads a body. The server is on
            // the new map from this message onwards, and if its gamemode differs
            // the HUD block changes length underneath the reader.
            if (message.Type == MessageType.ChangeMap && shapeForMap is not null
                && message.Payload.Length >= 1)
            {
                var length = message.Payload[0];
                if (message.Payload.Length >= 1 + length)
                {
                    var name = System.Text.Encoding.ASCII.GetString(message.Payload, 1, length);
                    stream.SetShape(shapeForMap(name));
                }
            }

            if (message.State is not { } state)
            {
                pending.Add(message);
                continue;
            }

            switch (state.Tier)
            {
                case MessageType.FullUpdate:
                    // The anchor. Not a tick of its own — it is sent on the join
                    // path, outside the per-tick cadence.
                    start ??= state;
                    pending.Add(message);
                    continue;

                case MessageType.CapsUpdate:
                    // Sent *in addition* to that tick's update, so it is an
                    // event, not a tick boundary.
                    pending.Add(message);
                    continue;
            }

            ticks.Add(new TraceTick
            {
                Index = ticks.Count,
                Tier = state.Tier,
                Slots = SlotsOf(state),

                // ⚠️ NOT from `state`. The control-point block is read only on
                // a FULL_UPDATE or a CAPS_UPDATE — StateUpdateReader returns
                // before the HUD tail on anything else — and neither of those
                // delimits a tick, so both are sitting in `pending`. A tick's
                // points are therefore whatever the CAPS_UPDATE that arrived
                // with it said, and empty on the great majority of ticks.
                ControlPoints = PointsIn(pending),
                ControlPointUnlock = UnlockIn(pending, shape),
                TdmInvulnerability = TdmInvulnerabilityIn(pending),
                Events = [.. pending],
            });

            pending.Clear();
        }

        return ticks;
    }

    /// <summary>
    /// The control points named by a <c>CAPS_UPDATE</c> or <c>FULL_UPDATE</c>
    /// arriving with this tick, or empty.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A recording observes a control point only when the server chose to
    /// send one of those</b>, which on a King of the Hill recording is a few
    /// dozen times in several thousand ticks — not every tick, and not on the
    /// positional cadence either. The last one wins: two in a tick would mean
    /// the later described the state the tick ended in.
    /// </remarks>
    /// <summary>
    /// <c>cpUnlock</c> out of a King of the Hill HUD block arriving with this
    /// tick, or null.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Index 0 of the HUD scalars, and only for the two hill modes.</b>
    /// <c>StateUpdateReader.ReadHud</c> writes <c>cpUnlock, redTimer,
    /// blueTimer</c> for <c>KingOfTheHill</c> and <c>DualKingOfTheHill</c>; every
    /// other mode has a different layout, so this deliberately refuses to guess
    /// for them rather than reading whatever happens to sit at index 0.
    /// </remarks>
    private static int? UnlockIn(List<ServerMessage> pending, WorldShape shape)
    {
        if (shape.GameMode is not (GameMode.KingOfTheHill or GameMode.DualKingOfTheHill))
            return null;

        int? unlock = null;

        foreach (var message in pending)
        {
            if (message.State is { HudScalars.Length: > 0 } state)
                unlock = (int)state.HudScalars[0];
        }

        return unlock;
    }

    /// <summary>
    /// Team Deathmatch's spawn invulnerability, off whichever update in this
    /// tick carried one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not gated on the gamemode, unlike <see cref="UnlockIn"/>.</b> The
    /// field is on every <c>FULL_UPDATE</c> whatever the map is playing, and a
    /// recording that rotates onto a Team Deathmatch map mid-stream would carry
    /// the value in an update the shape at parse time still calls something
    /// else. Reading it always costs nothing and cannot mislead: a non-TDM
    /// simulation never spends it.
    /// </remarks>
    private static int? TdmInvulnerabilityIn(List<ServerMessage> pending)
    {
        int? ticks = null;

        foreach (var message in pending)
        {
            // ⚠️ FULL_UPDATE only, and asked by tier rather than by "is the
            // value non-zero": StateUpdateReader leaves it at zero on every
            // other tier, and zero is also a legitimate setting, so a reader
            // that took any non-zero value would silently keep a stale one for
            // a server that had turned invulnerability off.
            if (message.State is { Tier: MessageType.FullUpdate } state)
                ticks = state.TdmInvulnerabilityTicks;
        }

        return ticks;
    }

    private static ControlPointBlock[] PointsIn(List<ServerMessage> pending)
    {
        var points = Array.Empty<ControlPointBlock>();

        foreach (var message in pending)
        {
            if (message.State is { ControlPoints.Length: > 0 } state)
                points = state.ControlPoints;
        }

        return points;
    }

    private static TraceSlot[] SlotsOf(StateUpdate state)
    {
        var slots = new TraceSlot[state.Players.Length];

        for (var i = 0; i < slots.Length; i++)
        {
            var character = state.Players[i].Character;

            slots[i] = new TraceSlot
            {
                HasCharacter = character is not null,
                Input = character is { } c
                    ? new PlayerInput(c.Keys, c.NetAimDirection, c.AimDistance)
                    : PlayerInput.None,
                Observed = character is { HasPosition: true } ? character : null,
            };
        }

        return slots;
    }
}
