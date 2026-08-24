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
    public static IReadOnlyList<TraceTick> Parse(Trace trace, in WorldShape shape, out StateUpdate? start)
    {
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
            catch (ProtocolException)
            {
                // A recording can stop mid-message: the socket closed between
                // one read and the next. Everything before it is still a trace.
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
