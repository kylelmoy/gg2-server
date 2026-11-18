namespace GG2.Bots;

/// <summary>
/// An <see cref="IMicroController"/> whose observation reads the map's nav graph
/// and next-hop tables.
/// </summary>
/// <remarks>
/// <b>Optional, like <see cref="ISightingController"/>.</b> <see cref="HybridBot.Poll"/>
/// hands every tier that implements it the graph and tables its scripted layer
/// routes with, at the top of every poll, so a map change (which replaces both)
/// reaches the tier on the next tick. A gg2-sim terrain net is the one that needs
/// it (<see cref="SimPolicyNetwork.IsTerrain"/>): its route columns are hops on the
/// same table the body would follow.
/// </remarks>
public interface IRouteController
{
    /// <summary>The body's graph and tables; either may be null (no nav, or <c>--no-nav-distances</c>).</summary>
    void SetRoute(NavGraph? graph, NavDistanceSet? tables);
}
