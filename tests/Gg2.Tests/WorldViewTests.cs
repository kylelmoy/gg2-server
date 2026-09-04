using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The read-only projection a bot decides against, and the spatial queries
/// docs/BOTAI.md item 6 asks it to carry.
/// </summary>
/// <remarks>
/// Item 6's requirement is not a feature set — it is that "nearest K by team"
/// and "projectiles within a radius" exist as queries rather than as a scan
/// every caller writes for itself, because a feature vector is extracted for
/// every bot on every decision tick during training and a whole-collection
/// enumeration there costs more than the simulation it observes.
/// <para>
/// <b>None of this is observable simulation.</b> A bot's arithmetic never
/// reaches the wire and no client re-derives it, so these use ordinary squared
/// distances rather than <see cref="Gml"/>'s fuzzy comparisons. What they owe is
/// determinism, which is why the tie-break below is asserted rather than left to
/// whatever the sort happened to do.
/// </para>
/// </remarks>
public sealed class WorldViewTests
{
    private static CollisionWorld Collision() =>
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    /// <summary>A world with characters at chosen places, and nothing else going on.</summary>
    private static WorldState World(params (Team Team, double X, double Y)[] characters)
    {
        var state = default(WorldState);

        for (var slot = 0; slot < characters.Length; slot++)
        {
            state.AddPlayer(slot);
            state.Players[slot].Team = characters[slot].Team;
            state.Players[slot].Class = PlayerClass.Scout;

            // Fresh, never `new CharacterState()` — six of its fields name a
            // roster slot and a zeroed one claims slot 0 last hurt it.
            var character = CharacterState.Fresh(PlayerClass.Scout, characters[slot].Team);
            character.X = characters[slot].X;
            character.Y = characters[slot].Y;

            state.Characters[slot] = character;
            state.Alive[slot] = true;
        }

        return state;
    }

    /// <summary>
    /// Enemies come back nearest first, not in roster order.
    /// </summary>
    /// <remarks>
    /// The slots are laid out so the two orders disagree: slot 1 is the furthest
    /// enemy and slot 3 the nearest. A query that walked the roster and stopped
    /// when the caller's span filled would answer [1, 2] and pass no assertion
    /// here.
    /// </remarks>
    [Fact]
    public void EnemiesComeBackNearestFirst()
    {
        var state = World(
            (Team.Red, 0, 0),      // 0: the asker
            (Team.Blue, 300, 0),   // 1
            (Team.Blue, 200, 0),   // 2
            (Team.Blue, 50, 0));   // 3

        var view = new WorldView(in state, Collision());
        Span<int> found = stackalloc int[4];

        Assert.Equal(3, view.NearestEnemies(0, found));
        Assert.Equal([3, 2, 1], found[..3].ToArray());
    }

    /// <summary>A shorter span keeps the nearest, not the first found.</summary>
    [Fact]
    public void AShorterSpanKeepsTheNearest()
    {
        var state = World(
            (Team.Red, 0, 0),
            (Team.Blue, 300, 0),
            (Team.Blue, 200, 0),
            (Team.Blue, 50, 0));

        var view = new WorldView(in state, Collision());
        Span<int> found = stackalloc int[2];

        Assert.Equal(2, view.NearestEnemies(0, found));
        Assert.Equal([3, 2], found.ToArray());
    }

    /// <summary>
    /// Two enemies at the same distance come back lower slot first.
    /// </summary>
    /// <remarks>
    /// The determinism the queries owe. Without a defined tie-break the answer
    /// depends on whether the insertion used <c>&lt;</c> or <c>&lt;=</c>, which
    /// is exactly the kind of thing that makes a rollout search irreproducible
    /// for reasons nobody can find.
    /// </remarks>
    [Fact]
    public void ATieGoesToTheLowerSlot()
    {
        var state = World(
            (Team.Red, 0, 0),
            (Team.Blue, 0, 100),
            (Team.Blue, 100, 0),
            (Team.Blue, -100, 0));

        var view = new WorldView(in state, Collision());
        Span<int> found = stackalloc int[3];

        Assert.Equal(3, view.NearestEnemies(0, found));
        Assert.Equal([1, 2, 3], found.ToArray());
    }

    /// <summary>The dead are not near anything, and neither is the asker.</summary>
    [Fact]
    public void TheDeadAndTheAskerAreSkipped()
    {
        var state = World(
            (Team.Red, 0, 0),
            (Team.Blue, 10, 0),
            (Team.Blue, 20, 0));

        state.Alive[1] = false;

        var view = new WorldView(in state, Collision());
        Span<int> found = stackalloc int[4];

        Assert.Equal(1, view.NearestEnemies(0, found));
        Assert.Equal(2, found[0]);

        // And the other way round: a team-mate query never names the asker.
        Span<int> allies = stackalloc int[4];
        Assert.Equal(0, view.NearestAllies(0, allies));
    }

    /// <summary>Team-mates and enemies are two disjoint answers off one roster.</summary>
    [Fact]
    public void AlliesAndEnemiesPartitionTheRoster()
    {
        var state = World(
            (Team.Red, 0, 0),
            (Team.Blue, 10, 0),
            (Team.Red, 20, 0),
            (Team.Blue, 30, 0));

        var view = new WorldView(in state, Collision());

        Span<int> enemies = stackalloc int[4];
        Span<int> allies = stackalloc int[4];

        Assert.Equal(2, view.NearestEnemies(0, enemies));
        Assert.Equal([1, 3], enemies[..2].ToArray());

        Assert.Equal(1, view.NearestAllies(0, allies));
        Assert.Equal(2, allies[0]);
    }

    /// <summary>
    /// Projectiles come back by radius, in creation order, and the radius is
    /// inclusive.
    /// </summary>
    /// <remarks>
    /// Creation order rather than nearest-first because that is the order the
    /// rest of the project already makes observable
    /// (<see cref="WorldState.LiveProjectileHandle"/>) — pool slots are
    /// recycled, so the pool says nothing about when anything was fired.
    /// <para>
    /// The one exactly on the boundary is the assertion that bites: a
    /// <c>&lt;</c> where the code has <c>&lt;=</c> drops it, and the difference
    /// only ever shows up on a value that landed exactly on the radius.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProjectilesComeBackByRadiusInCreationOrder()
    {
        var state = default(WorldState);
        state.AddPlayer(0);

        var far = state.SpawnProjectile(new ProjectileState { Kind = ProjectileKind.Shot, X = 500, Y = 0 });
        var near = state.SpawnProjectile(new ProjectileState { Kind = ProjectileKind.Shot, X = 30, Y = 40 });
        var edge = state.SpawnProjectile(new ProjectileState { Kind = ProjectileKind.Shot, X = 0, Y = 100 });

        var view = new WorldView(in state, Collision());
        Span<ProjectileHandle> found = stackalloc ProjectileHandle[8];

        Assert.Equal(2, view.ProjectilesWithin(0, 0, 100, found));

        // `near` was created second and `edge` third; the answer keeps that
        // order rather than sorting by distance.
        Assert.Equal([near, edge], found[..2].ToArray());
        Assert.DoesNotContain(far, found[..2].ToArray());

        // A span of one truncates rather than picking.
        Span<ProjectileHandle> one = stackalloc ProjectileHandle[1];
        Assert.Equal(1, view.ProjectilesWithin(0, 0, 100, one));
        Assert.Equal(near, one[0]);
    }

    /// <summary>
    /// A dead projectile's slot is not reported, even though the pool still
    /// holds its bytes.
    /// </summary>
    /// <remarks>
    /// The query walks <c>LiveProjectileCount</c> in creation order rather than
    /// the pool, which is what keeps a recycled slot from answering for a shot
    /// that has already exploded.
    /// </remarks>
    [Fact]
    public void AFreedProjectileIsGone()
    {
        var state = default(WorldState);
        state.AddPlayer(0);

        var first = state.SpawnProjectile(new ProjectileState { Kind = ProjectileKind.Shot, X = 10, Y = 0 });
        var second = state.SpawnProjectile(new ProjectileState { Kind = ProjectileKind.Shot, X = 20, Y = 0 });

        Assert.True(state.FreeProjectile(first));

        var view = new WorldView(in state, Collision());
        Span<ProjectileHandle> found = stackalloc ProjectileHandle[8];

        Assert.Equal(1, view.ProjectilesWithin(0, 0, 100, found));
        Assert.Equal(second, found[0]);
    }

    /// <summary>
    /// The view reads the live state rather than a copy of it.
    /// </summary>
    /// <remarks>
    /// It carries a <c>ref readonly</c>, which is what makes constructing one
    /// free — <see cref="WorldState"/> is kilobytes of inline buffers and a
    /// by-value projection would be a copy per bot per tick, which is the
    /// allocation-free rule in another costume. The observable consequence is
    /// this: a view made before a change sees the change.
    /// </remarks>
    [Fact]
    public void TheViewIsAReferenceAndNotACopy()
    {
        var state = World((Team.Red, 0, 0), (Team.Blue, 10, 0));
        var view = new WorldView(in state, Collision());

        Assert.True(view.IsAlive(1));

        state.Alive[1] = false;

        Assert.False(view.IsAlive(1));
    }
}
