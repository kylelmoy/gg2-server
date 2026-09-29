namespace GG2.Server.Plugins;

/// <summary>
/// Per-player plugin state, kept in step with the roster.
/// </summary>
/// <typeparam name="T">
/// The state one player carries. Created by <paramref name="factory"/> when a
/// slot appears.
/// </typeparam>
/// <remarks>
/// <b>This type exists because a slot index is a roster index and removal
/// COMPACTS.</b> In GML this problem does not arise: a plugin's per-player
/// variables live on the <c>Player</c> instance and die with it. In C# the
/// obvious shape — a <c>Dictionary&lt;int, T&gt;</c> or an array indexed by slot —
/// is silently wrong the first time somebody disconnects, because every index
/// above the hole shifts down and the state stays put. The symptom is a mute, a
/// name or a chat flag landing on an unrelated player some seconds after a
/// disconnect nobody connected it to.
/// <para>
/// It is the same trap <c>WorldState.RemoveSlot</c> is written up for in
/// CLAUDE.md, one layer out.
/// </para>
/// </remarks>
public sealed class PluginPlayerTable<T>(Func<T> factory)
{
    private readonly List<T> _entries = [];

    public int Count => _entries.Count;

    public T this[int slot] => _entries[slot];

    /// <summary>Whether <paramref name="slot"/> is inside the table.</summary>
    public bool Has(int slot) => slot >= 0 && slot < _entries.Count;

    /// <summary>
    /// Adds state for a new slot. The roster only ever appends, so
    /// <paramref name="slot"/> is expected to be the table's current size.
    /// </summary>
    /// <remarks>
    /// The assertion is the point. A caller that adds out of order has
    /// already lost the alignment this type exists to keep, and finding that out
    /// here is much cheaper than finding it out from a misattributed message.
    /// </remarks>
    public void Add(int slot)
    {
        if (slot != _entries.Count)
            throw new ArgumentOutOfRangeException(
                nameof(slot),
                slot,
                $"roster entries only ever append; expected slot {_entries.Count}");

        _entries.Add(factory());
    }

    /// <summary>Removes a slot's state and compacts, exactly as the roster does.</summary>
    public void RemoveAt(int slot) => _entries.RemoveAt(slot);

    /// <summary>Drops everything, for a restart.</summary>
    public void Clear() => _entries.Clear();
}
