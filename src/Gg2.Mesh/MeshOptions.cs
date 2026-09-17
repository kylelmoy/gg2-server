namespace Gg2.Mesh;

/// <summary>How one node behaves.</summary>
public sealed record MeshOptions
{
    /// <summary>
    /// How far back a node can be asked to rewind, in ticks.
    /// </summary>
    /// <remarks>
    /// <b>This is the link's worst case plus its jitter, not its average.</b> An
    /// input that arrives older than this cannot be applied — the state it
    /// needed to rewind to has been overwritten — and the node counts it in
    /// <see cref="MeshStats.TooLate"/> rather than pretending. One way across
    /// the Atlantic is two or three ticks, so 32 is an order of magnitude of
    /// headroom; the cost is 32 copies of a <c>WorldState</c>.
    /// </remarks>
    public int MaxRollbackTicks { get; init; } = 32;

    /// <summary>
    /// Whether to guess that a player is still holding what they were holding.
    /// </summary>
    /// <remarks>
    /// <b>False is a WORSE guess, not a broken node.</b> With it off a node
    /// simulates an unheard player as pressing nothing, which is wrong far more
    /// often — measured at four times the positional error two ticks out — so it
    /// rewinds far more and does more work to reach the same place. It still
    /// reaches it: what convergence depends on is <see cref="Rollback"/>, and
    /// <c>MeshNodeTests</c> asserts exactly that separation.
    /// </remarks>
    public bool Predict { get; init; } = true;

    /// <summary>
    /// Whether to rewind when something arrives that contradicts what was
    /// simulated.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>False is a BROKEN node and exists to be the control in a test.</b>
    /// Without the rewind a node keeps whatever it guessed, for ever, so two
    /// nodes that guessed differently stay in different worlds. Convergence is
    /// the whole claim of the design, and a test asserting it has to be able to
    /// fail — two copies of a deterministic simulation fed the same local inputs
    /// agree for reasons that have nothing to do with this type.
    /// </remarks>
    public bool Rollback { get; init; } = true;
}

/// <summary>What a node has had to do, for a log line or a test.</summary>
public readonly record struct MeshStats(
    int Ticks,
    int Rollbacks,
    int ResimulatedTicks,
    int DeepestRollback,
    int TooLate)
{
    public override string ToString() =>
        $"{Ticks} tick(s), {Rollbacks} rollback(s) re-simulating {ResimulatedTicks} tick(s), "
        + $"deepest {DeepestRollback}, {TooLate} too late";
}
