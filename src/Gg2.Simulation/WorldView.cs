using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// A read-only projection of one match, handed to an <see cref="IInputSource"/>
/// so it can decide what to press.
/// </summary>
/// <remarks>
/// docs/ARCHITECTURE.md, "Bots": <c>PlayerInput Poll(in WorldView world, int
/// slot, int tick)</c>. The projection is read-only for the reason that document
/// gives — it "stops bot code from mutating simulation state by accident, the
/// failure mode that is hardest to debug and most likely under heavy bot logic".
/// <para>
/// <b>A <c>ref struct</c>, deliberately.</b> It carries a
/// <c>ref readonly</c> to the live <see cref="WorldState"/> rather than a copy,
/// so constructing one is free and observing it allocates nothing — and the
/// language then refuses to let a source stash it in a field, put it on the
/// heap, or keep it past the call. That is the read-only guarantee made
/// structural rather than documented: there is no way to hold a view until the
/// state has moved on, and no way to write through it.
/// </para>
/// <para>
/// It holds the <see cref="CollisionWorld"/> too, because the map is a
/// read-only input passed <em>to</em> the tick rather than part of the state
/// (docs/BOTAI.md item 1), and a bot that cannot ask about walls cannot aim.
/// </para>
/// <para>
/// <b>Nothing here is observable simulation.</b> A bot's arithmetic is not on
/// the wire and no client re-derives it, so the queries below use ordinary
/// doubles and squared distances rather than <see cref="Gml"/>'s fuzzy
/// comparisons — CLAUDE.md, "compatible on the wire and in observable
/// simulation, free everywhere else". What they must be is <em>deterministic</em>,
/// which is why every tie below is broken by roster index.
/// </para>
/// </remarks>
public readonly ref struct WorldView
{
    private readonly ref readonly WorldState _state;

    public WorldView(in WorldState state, CollisionWorld collision)
    {
        ArgumentNullException.ThrowIfNull(collision);

        _state = ref state;
        Collision = collision;
    }

    /// <summary>The match state. Read-only: there is no mutable path out of here.</summary>
    public ref readonly WorldState State => ref _state;

    /// <summary>The map's collision geometry, as <c>Tick</c> receives it.</summary>
    public CollisionWorld Collision { get; }

    public bool HasPlayer(int slot) => (uint)slot < WorldState.MaxSlots && _state.HasPlayer[slot];

    public bool IsAlive(int slot) => _state.IsAlive(slot);

    /// <summary>
    /// How many characters are alive right now.
    /// </summary>
    /// <remarks>
    /// <b>The cost driver for a rollout search</b>, which re-simulates every
    /// one of them for every tick of every candidate — see
    /// <c>SearchController.DecisionCostFor</c>. It is a count of live CHARACTERS
    /// and not of players: a spectator or a dead player costs a rollout nothing.
    /// </remarks>
    public int LiveCharacters
    {
        get
        {
            var count = 0;

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (_state.Alive[slot])
                    count++;
            }

            return count;
        }
    }

    /// <summary>The character in <paramref name="slot"/>. Only valid when <see cref="IsAlive"/>.</summary>
    public ref readonly CharacterState Character(int slot) => ref _state.Characters[slot];

    public ref readonly PlayerState Player(int slot) => ref _state.Players[slot];

    public ref readonly WeaponState Weapon(int slot) => ref _state.Weapons[slot];

    /// <summary>
    /// The living enemies of <paramref name="slot"/>, nearest first.
    /// </summary>
    /// <remarks>
    /// docs/BOTAI.md item 6 asks that this exist as a query rather than as a
    /// scan every caller writes for itself: a feature vector is extracted for
    /// every bot on every decision tick during training, and "nearest K by
    /// team" written with LINQ over an entity list costs more than the
    /// simulation it observes.
    /// <para>
    /// Distance is between character origins and is compared <em>squared</em> —
    /// no <c>sqrt</c>, and no <see cref="Gml.PointDistance"/>, because nothing
    /// here reaches the wire. Ties go to the lower slot, so the answer does not
    /// depend on how the sort was written.
    /// </para>
    /// </remarks>
    /// <returns>How many entries of <paramref name="results"/> were filled.</returns>
    public int NearestEnemies(int slot, Span<int> results) => Nearest(slot, results, allies: false);

    /// <summary>The living team-mates of <paramref name="slot"/>, nearest first, excluding itself.</summary>
    public int NearestAllies(int slot, Span<int> results) => Nearest(slot, results, allies: true);

    private int Nearest(int slot, Span<int> results, bool allies)
    {
        if (results.Length == 0 || !IsAlive(slot))
            return 0;

        if (results.Length > WorldState.MaxSlots)
            throw new ArgumentException($"at most {WorldState.MaxSlots} results", nameof(results));

        // Bounded by the roster, so it is a stack buffer rather than an
        // allocation: docs/BOTAI.md item 1 rules out garbage on this path just
        // as firmly as it does inside the tick.
        Span<double> keys = stackalloc double[results.Length];

        ref readonly var self = ref _state.Characters[slot];
        var team = _state.Players[slot].Team;
        var found = 0;

        for (var other = 0; other < WorldState.MaxSlots; other++)
        {
            if (other == slot || !_state.Alive[other])
                continue;

            if ((_state.Players[other].Team == team) != allies)
                continue;

            var dx = _state.Characters[other].X - self.X;
            var dy = _state.Characters[other].Y - self.Y;
            var key = (dx * dx) + (dy * dy);

            // Insertion into the sorted prefix. The list is at most as long as
            // the caller's span — a nearest-3 query does three comparisons a
            // candidate, not a sort of the roster. Strictly `<` so an equal
            // distance leaves the earlier (lower) slot in front.
            var at = found < results.Length ? found : results.Length;
            while (at > 0 && key < keys[at - 1])
                at--;

            if (at >= results.Length)
                continue;

            for (var shift = Math.Min(found, results.Length - 1); shift > at; shift--)
            {
                keys[shift] = keys[shift - 1];
                results[shift] = results[shift - 1];
            }

            keys[at] = key;
            results[at] = other;

            if (found < results.Length)
                found++;
        }

        return found;
    }

    /// <summary>
    /// Every live projectile whose origin is within <paramref name="radius"/> of
    /// a point, in creation order.
    /// </summary>
    /// <remarks>
    /// Creation order rather than nearest-first, because that is the order the
    /// rest of the project already makes observable
    /// (<see cref="WorldState.LiveProjectileHandle"/>) and a dodge wants the
    /// whole set anyway. Handles rather than states, so a caller cannot end up
    /// holding a stale copy of one.
    /// </remarks>
    /// <returns>How many entries of <paramref name="results"/> were filled.</returns>
    public int ProjectilesWithin(double x, double y, double radius, Span<ProjectileHandle> results)
    {
        var found = 0;
        var limit = radius * radius;

        for (var i = 0; i < _state.LiveProjectileCount && found < results.Length; i++)
        {
            var handle = _state.LiveProjectileHandle(i);
            ref readonly var projectile = ref _state.Projectiles[handle.Index];

            var dx = projectile.X - x;
            var dy = projectile.Y - y;

            if ((dx * dx) + (dy * dy) <= limit)
                results[found++] = handle;
        }

        return found;
    }
}
