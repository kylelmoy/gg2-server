using GG2.Bots;

namespace GG2.Server;

/// <summary>
/// Policy checkpoints loaded once and shared by every server that holds this
/// object: the rooms of one room host (<c>docs/ROOMS.md</c>).
/// </summary>
/// <remarks>
/// <b>Safe to share because a <see cref="SimPolicyNetwork"/> never changes after
/// it is loaded</b>: its layers are written by <c>Load</c> alone,
/// <c>Decide</c> works in scratch the caller passes, and a sampled decision
/// draws from the bot's own stream. Bots already share one network within a
/// server for the same reason (<c>MicroControllerSpec</c>).
/// <para>
/// <b>What is NOT shared is the brain.</b> <see cref="BotBrain.Build"/> makes a
/// new <c>BotBrainSpec</c> per room, and with it a new
/// <c>DecisionQuotaArbiter</c>, whose per-tick spend is keyed by the room's own
/// frame counter: shared, two rooms on the same frame number would spend one
/// budget, and which rooms did would depend on when each was created.
/// </para>
/// <para>
/// Keyed by full path, never evicted: a host loads the shipped bundle and
/// nothing else.
/// </para>
/// </remarks>
public sealed class SharedPolicies
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SimPolicyNetwork> _networks = new(StringComparer.Ordinal);

    /// <summary>Checkpoints held, for a test or a status page.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _networks.Count;
        }
    }

    /// <summary><see cref="SimPolicyNetwork.Load(string)"/>, once per file per host.</summary>
    /// <remarks>A failed load is not cached, so a fixed file is picked up by the next room.</remarks>
    public SimPolicyNetwork Load(string path)
    {
        var key = Path.GetFullPath(path);

        lock (_lock)
        {
            if (!_networks.TryGetValue(key, out var network))
            {
                network = SimPolicyNetwork.Load(key);
                _networks[key] = network;
            }

            return network;
        }
    }
}
