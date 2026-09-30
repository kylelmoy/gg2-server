using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// The last few hundred lines of player chat, for the web panel's chat window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="LogRing"/> on purpose.</b> The chat plugins log
/// every line too, but as text already folded into each plugin's own format
/// (<c>chat_v2_pr</c>'s carries its colour codes), and on a busy server the
/// log ring turns over in about a minute, which would take the chat with it.
/// This keeps the speaker's name and team as fields and holds nothing else.
/// </para>
/// <para>
/// <b>Locked</b>, like <see cref="LogRing"/>: the tick thread writes and a
/// panel thread reads. <b>Sequence numbers, not indices</b>, for the same
/// reason as there.
/// </para>
/// </remarks>
public sealed class ChatHistory
{
    /// <summary>Lines kept.</summary>
    public const int Capacity = 200;

    private readonly Lock _gate = new();
    private readonly Entry[] _entries = new Entry[Capacity];
    private long _next;

    /// <summary>One line of chat.</summary>
    /// <param name="Name">The speaker's name when they spoke; names change.</param>
    /// <param name="Team">The speaker's team when they spoke.</param>
    /// <param name="TeamOnly">Team chat, which only that team's clients received.</param>
    public readonly record struct Entry(
        long Sequence,
        DateTime Utc,
        string Name,
        Team Team,
        string Text,
        bool TeamOnly);

    /// <summary>The sequence number the next line will take.</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
                return _next;
        }
    }

    public void Add(string name, Team team, string text, bool teamOnly)
    {
        lock (_gate)
        {
            _entries[(int)(_next % Capacity)] = new Entry(_next, DateTime.UtcNow, name, team, text, teamOnly);
            _next++;
        }
    }

    /// <summary>
    /// Every line written after <paramref name="after"/>, oldest first; -1 for
    /// everything still held.
    /// </summary>
    public IReadOnlyList<Entry> Since(long after)
    {
        lock (_gate)
        {
            var oldest = Math.Max(0, _next - Capacity);
            var from = after < 0 ? oldest : Math.Max(oldest, after + 1);
            var lines = new List<Entry>((int)Math.Max(0, _next - from));

            for (var sequence = from; sequence < _next; sequence++)
                lines.Add(_entries[(int)(sequence % Capacity)]);

            return lines;
        }
    }
}
