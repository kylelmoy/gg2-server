using GG2.Protocol;

namespace GG2.Bots;

/// <summary>
/// How many bots of each team currently have each edge in an installed route —
/// <c>botOccupancyAdd</c>, <c>botOccupancyMap</c> and <c>botOccupancyReset</c>.
/// </summary>
/// <remarks>
/// <see cref="NavSearch"/> multiplies an edge's cost by
/// <c>(1 + BOT_OCCUPANCY_COST × count)</c>, so the more team-mates are already
/// walking an edge the less attractive it is to the next bot to plan. That is the
/// whole route-variety mechanism.
/// <para>
/// <b>It is the only safe way to spread a team out, and a per-edge random
/// jitter is not.</b> The jitter was tried in the reference and deleted: it is
/// mean-reverting, so over a sixty-edge route every candidate inflates by the same
/// average and on ctf_truefort all eight bots got the <em>identical</em> 68-node
/// route anyway. Occupancy works because it is structured — what it makes
/// expensive is precisely what is crowded — shared, and coarse. <b>Do not re-add
/// the jitter.</b>
/// </para>
/// <para>
/// <b>Per team rather than one shared map</b>, because the thing being spread out
/// is a team. A blue bot has no reason to avoid the route red is walking — if
/// anything it wants to be on it — and folding both into one map would have each
/// team's routes push the other's around for no reason anyone could see from the
/// outside.
/// </para>
/// <para>
/// <b>Shared rather than per bot, deliberately.</b> It is what makes the mechanism
/// model bots bunching up rather than approximating it with per-bot noise, and it
/// is why the penalty is the same for every bot asking at the same moment.
/// </para>
/// </remarks>
public sealed class BotOccupancy
{
    // Keys are NavGraph.EdgeKey values built out of node indices, and a node
    // index only means anything to one build of the graph — so both maps are
    // dropped on a map change (Reset). Counts from the previous map would
    // otherwise sit here making arbitrary edges of the new one look busy.
    private readonly Dictionary<long, int> _red = [];
    private readonly Dictionary<long, int> _blue = [];

    /// <summary>
    /// One team's counts, or <c>null</c> for a team that fields no bots.
    /// </summary>
    public IReadOnlyDictionary<long, int>? Counts(Team team) => Map(team);

    /// <summary>How many edges that team currently has anything on.</summary>
    public int EdgeCount(Team team) => Map(team)?.Count ?? 0;

    /// <summary>Adds this bot's installed route to <paramref name="team"/>'s counts.</summary>
    /// <remarks>
    /// <b>Idempotent, and that is not tidiness.</b> A route is added in exactly
    /// one place — once a search has succeeded — but removed in three: on every
    /// re-plan, arrival and death; when the bot leaves or the map changes; and by
    /// the planner itself, which takes its own route out of the counts before
    /// searching. Without the flag the first two would double-decrement after the
    /// third, and <b>a count that goes negative makes an edge cheaper the more
    /// bots are on it</b>, which is the exact opposite of the point.
    /// </remarks>
    public void Add(BotPathState state, Team team)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Path.Count < 2)
        {
            // No route means nothing to count, and the flag must come off with
            // it: a flag left set against a route that no longer exists makes the
            // next add a no-op and leaves the bot permanently invisible to its
            // team-mates' searches.
            Forget(state);
            return;
        }

        if (state.Occupied)
            return;

        var map = Map(team);
        if (map is null)
            return;

        Apply(map, state, +1);
        state.OccupiedTeam = team;
    }

    /// <summary>Takes this bot's route back out of the counts.</summary>
    /// <remarks>
    /// <b>Against <see cref="BotPathState.OccupiedTeam"/> — the team the route
    /// was counted under — and never against the bot's team now.</b> The two
    /// genuinely come apart: autobalance switches the lowest-scoring player on the
    /// larger team mid-round and can perfectly well pick a bot, and a map rotation
    /// re-teams every one of them. Reading the current team on the way out would
    /// subtract from the map the bot has just joined and leave its old team's
    /// counts raised for ever — an edge nothing is walking that every future
    /// search on that team is charged extra for.
    /// </remarks>
    public void Remove(BotPathState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // A short-circuit rather than the guard: the guard is Map below, which
        // answers null for the Spectator this rests at when nothing is counted.
        if (!state.Occupied)
            return;

        // The route may already be gone; the counts still have to come off, and
        // they can only be found through the path that is still installed.
        var map = Map(state.OccupiedTeam);

        if (map is not null && state.Path.Count >= 2)
            Apply(map, state, -1);

        Forget(state);
    }

    /// <summary>
    /// Drops both teams' counts — <c>botOccupancyReset</c>, on a map change.
    /// </summary>
    /// <remarks>
    /// <b>Clearing the bots' flags is the half that is easy to forget</b>, and
    /// it is the caller's job here because this type holds no roster. Dropping the
    /// maps alone leaves every bot believing its route is still counted, so the
    /// next removal declines to decrement and the route is then added a second
    /// time on the next plan — a count of 2 for one bot, on a map where it is the
    /// only thing walking. Here the removal is what clears a bot's flag, so
    /// <see cref="ScriptedBot.SetMap"/> gets both halves from one call — but the
    /// order still matters, and the population manager resets the maps first.
    /// </remarks>
    public void Reset()
    {
        _red.Clear();
        _blue.Clear();
    }

    /// <summary>Clears the "my route is counted" bookkeeping, nothing else.</summary>
    private static void Forget(BotPathState state) => state.OccupiedTeam = Team.Spectator;

    private static void Apply(Dictionary<long, int> map, BotPathState state, int delta)
    {
        for (var i = 1; i < state.Path.Count; i++)
        {
            var key = NavGraph.EdgeKey(state.Path[i - 1], state.Path[i]);

            map.TryGetValue(key, out var was);
            was += delta;

            // A count that has fallen to nothing is deleted rather than left at
            // zero, so the map stays the size of what is actually being walked
            // rather than growing to the size of every edge any bot has ever used.
            if (was <= 0)
                map.Remove(key);
            else
                map[key] = was;
        }
    }

    private Dictionary<long, int>? Map(Team team) => team switch
    {
        Team.Red => _red,
        Team.Blue => _blue,
        _ => null,
    };
}
