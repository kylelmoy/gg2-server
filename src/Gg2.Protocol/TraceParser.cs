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
    public static IReadOnlyList<TraceTick> Parse(
        Trace trace,
        in WorldShape shape,
        out StateUpdate? start,
        out string? stopped)
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
                Events = [.. pending],
            });

            pending.Clear();
        }

        return ticks;
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
