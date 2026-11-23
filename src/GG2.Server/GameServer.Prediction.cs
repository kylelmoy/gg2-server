using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// Server support for client-side prediction: the welcome, the per-client
/// input queue, and the per-tick acknowledgement. docs/PREDICTION.md.
/// </summary>
/// <remarks>
/// <b>Everything here is per connection and only for a connection that sent
/// <c>PREDICT_HELLO</c>.</b> A stock client never sends one, so it receives the
/// same bytes and its inputs coalesce exactly as before; that is the "cannot see
/// the difference at all" arm of the rule in CONTRIBUTING.md.
/// <para>
/// ⚠️ <b>The ack's meaning rests on <see cref="Tick"/>'s order</b>: consume, then
/// gather and apply, then <c>WriteStateUpdate</c>, then the ack, then the flush.
/// Moving the consume below the state update, or the ack below the flush,
/// makes every ack name the wrong update.
/// </para>
/// </remarks>
public sealed partial class GameServer
{
    // Reused every tick: five bytes per predicting client, sent per player.
    private readonly NetWriter _predictionWriter = new(16);

    private void WelcomePredictingClient(ClientPlayer client)
    {
        client.Predicting = true;

        // Every input read before the hello has reached Latest and is applied
        // this tick, so the welcome's count and the first ack agree.
        ((NetworkInputSource)client.Entry.Source).StartQueueing(client.InputsRead);

        _predictionWriter.Reset();
        ServerMessages.PredictWelcome(_predictionWriter, client.InputsRead);
        client.Connection.Send(_predictionWriter.WrittenSpan);
    }

    private void ConsumePredictedInputs()
    {
        // Read at use time, like every live option on this path.
        var maxQueued = _options.PredictionMaxQueuedInputs;
        var drainTicks = _options.PredictionQueueDrainTicks;

        foreach (var client in _clients)
        {
            if (client.Predicting)
                ((NetworkInputSource)client.Entry.Source).ConsumeTick(maxQueued, drainTicks);
        }
    }

    private void SendPredictionAcks()
    {
        foreach (var client in _clients)
        {
            if (!client.Predicting)
                continue;

            _predictionWriter.Reset();
            ServerMessages.PredictAck(
                _predictionWriter, ((NetworkInputSource)client.Entry.Source).InputsApplied);
            client.Connection.Send(_predictionWriter.WrittenSpan);
        }
    }
}
