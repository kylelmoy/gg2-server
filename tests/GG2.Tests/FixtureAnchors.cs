using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// Where a closed-loop replay's exactness claim can start: the one anchor
/// derivation shared by every fixture test.
/// </summary>
/// <remarks>
/// The anchor is the latest first-<c>PLAYER_SPAWN</c> over every slot ever
/// observed holding a character, returning null if any such slot never
/// spawned. A looser heuristic — "every spawn within 200 ticks of the first
/// one" — would silently include a respawn that landed within 200 ticks of
/// the map-reload wave; the strict rule instead refuses <c>closedcombat</c>
/// outright, because its slot 6 held a character before the map reload — last
/// observed at <b>t21</b>, 781 ticks before the anchor — and never spawned
/// inside the recording.
/// <para>
/// The unified rule: <b>the anchor is the latest first spawn, and a slot with
/// no spawn only poisons it if that slot is still observed after it.</b> A
/// ghost from before the reload that the reload removed cannot make the window
/// after the wave unanchorable, because nothing in that window compares
/// against it.
/// </para>
/// </remarks>
internal static class FixtureAnchors
{
    /// <summary>
    /// The tick of the last first-spawn, after which every character the
    /// recording still observes has been placed from an exact
    /// <c>PLAYER_SPAWN</c> anchor — or null if some slot is observed beyond it
    /// without ever spawning (an open-loop-only recording), or if nothing
    /// spawns at all.
    /// </summary>
    public static int? LastExactAnchor(IReadOnlyList<TraceTick> ticks)
    {
        var firstSpawn = new Dictionary<int, int>();
        var lastObserved = new Dictionary<int, int>();

        foreach (var tick in ticks)
        {
            foreach (var message in tick.Events)
            {
                if (message.Type == MessageType.PlayerSpawn && message.PlayerId is { } player)
                    firstSpawn.TryAdd(player, tick.Index);
            }

            for (var slot = 0; slot < tick.Slots.Length; slot++)
            {
                if (tick.Slots[slot].Observed is { HasPosition: true })
                    lastObserved[slot] = tick.Index;
            }
        }

        var anchor = int.MinValue;

        foreach (var (slot, _) in lastObserved)
        {
            if (firstSpawn.TryGetValue(slot, out var spawned))
                anchor = Math.Max(anchor, spawned);
        }

        if (anchor == int.MinValue)
            return null;

        // A slot with no spawn is fatal only if it outlives the anchor — a
        // ghost the map reload removed cannot poison the window after the wave.
        foreach (var (slot, seen) in lastObserved)
        {
            if (!firstSpawn.ContainsKey(slot) && seen > anchor)
                return null;
        }

        return anchor;
    }
}
